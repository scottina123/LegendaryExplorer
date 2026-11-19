using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace LegendaryExplorer.Lightmaps.Solver;

// Lightmass's photon mapping for bounced light (SolverSettings.Photons):
// 1. The lights that bounce emit photon paths in proportion to their power, into the importance volume. Direct photons are stored where light first
//    lands; indirect photons where it lands after each bounce (up to NumIndirectLightingBounces - 1 bounces).
// 2. A subset of the photons become irradiance photons; their irradiance is estimated from the nearest photons of each map (cone filtered).
// 3. Each final gather ray takes the irradiance of the irradiance photon nearest to where it lands (albedo/pi of it is the radiance it brings back).
// The map depends only on the scene, so it's built once and reused by every solve of the same scene (mapping batches, refinement passes).
internal sealed partial class SolverRun
{
    /// <summary>Irradiance photons as the gather pass looks them up: a fine and a coarse grid, each a hash table over runs of photons</summary>
    sealed class PhotonLookup
    {
        public GpuPhoton[] Photons;
        public GpuHashCell[] Table;
        public uint FineSize, CoarseOff, CoarseSize;
        public float FineCell, CoarseCell, Radius, NormalCos, PlaneSin;
        public Vector3 ImpMin, ImpMax;
        public uint Variant;
    }

    static readonly ConditionalWeakTable<SolverScene, Dictionary<string, PhotonLookup>> photonCache = new();

    /// <summary>Most photon paths per pass, and most photons kept per map</summary>
    const long MaxPhotonPaths = 256L << 20, MaxStoredPhotons = 16L << 20;
    const int DownloadChunk = 1 << 20;

    PhotonLookup lookup;

    void ApplyPhotonLookup(ref GpuParams p)
    {
        if (lookup == null)
            return;
        p.PhotonMode = 1;
        p.ImpMin = lookup.ImpMin;
        p.ImpMax = lookup.ImpMax;
        p.TableAOff = 0;
        p.TableASize = lookup.FineSize;
        p.CellA = lookup.FineCell;
        p.TableBOff = lookup.CoarseOff;
        p.TableBSize = lookup.CoarseSize;
        p.CellB = lookup.CoarseCell;
        p.RadiusB = lookup.Radius;
        p.NormalCos = lookup.NormalCos;
        p.PlaneSin = lookup.PlaneSin;
        p.PhotonVariant = lookup.Variant;
    }

    void PhotonPass(SolverGpu d3d, GpuScene gpu, SolverStatistics stats)
    {
        var ps = settings.Photons;
        if (ps == null || settings.GatherRays <= 0 || settings.Bounces < 1 || NumBounceLights == 0 || sampleCount == 0)
            return;
        var receivers = job.Receivers ?? [];
        bool any = false;
        for (int r = 0; r < receivers.Count && !any; r++)
            any = receivers[r] is { GatherIndirect: true };
        if (!any)
            return;

        string key = string.Join("|", settings.Bounces, settings.Seed, settings.NormalOffset, settings.RayOffset, ps.DirectPhotonDensity, ps.IndirectPhotonDensity,
            ps.DirectIrradiancePhotonDensity, ps.IndirectIrradiancePhotonDensity, ps.IrradiancePhotons, ps.DirectSearchDistance, ps.IndirectSearchDistance,
            ps.NormalThreshold, ps.SearchConeAngle, ps.ConeFilterConstant, ps.CountScale, ps.DirectByShadowRays, ps.SpotEmitsSphere, ps.IgnoreFalloff, ps.LookupSameInstance, ps.LookupFineOnly, ps.PassBackFaces);
        var perScene = photonCache.GetOrCreateValue(scene);
        lock (perScene)
            perScene.TryGetValue(key, out lookup);
        if (lookup == null)
        {
            var built = BuildPhotonLookup(d3d, gpu, ps, out string summary);
            stats.PhotonSummary = summary;
            lock (perScene)
                perScene[key] = built;
            lookup = built;
        }
        gpu.Set(ref gpu.IrrPhotons, d3d.CreateStructured(lookup.Photons, lookup.Photons.Length));
        gpu.Set(ref gpu.Cells, d3d.CreateStructured(lookup.Table, lookup.Table.Length));
    }

    (Vector3 Min, Vector3 Max) ImportanceBounds()
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var (vmin, vmax) in scene.ImportanceVolumes ?? [])
        {
            min = Vector3.Min(min, vmin);
            max = Vector3.Max(max, vmax);
        }
        if (min.X > max.X)
            foreach (var v in scene.Vertices ?? [])
            {
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }
        if (min.X > max.X)
            min = max = Vector3.Zero;
        // so surfaces lying on the bounds (the scene's own extent) are inside
        var pad = new Vector3(1 + 1e-3f * Vector3.Distance(min, max));
        return (min - pad, max + pad);
    }

    PhotonLookup BuildPhotonLookup(SolverGpu d3d, GpuScene gpu, SolverPhotonSettings ps, out string summary)
    {
        var sw = Stopwatch.StartNew();
        var (imin, imax) = ImportanceBounds();
        float radius = MathF.Max(1, Vector3.Distance(imin, imax) * 0.5f);
        // Lightmass stores photons in proportion to the importance volume's extent: densities are photons per million square units
        double area = Math.PI * radius * radius / 1e6 * Math.Max(0, ps.CountScale);
        long directTarget = Math.Clamp((long)(ps.DirectPhotonDensity * area), 0, MaxStoredPhotons / 2);
        long indirectTarget = settings.Bounces >= 2 ? Math.Clamp((long)(ps.IndirectPhotonDensity * area), 0, MaxStoredPhotons / 2) : 0;

        var p = BaseParams();
        p.ImpMin = imin;
        p.ImpMax = imax;
        p.ImpRadius = radius;
        p.PhotonVariant = (ps.SpotEmitsSphere ? GpuParams.VariantSpotSphere : 0) | (ps.IgnoreFalloff ? GpuParams.VariantNoFalloff : 0) |
            (ps.DirectByShadowRays ? GpuParams.VariantDirectExplicit : 0) | (ps.PassBackFaces ? GpuParams.VariantPassBack : 0);

        // The pilot: the same number of paths from every light finds how much of each light's power lands in the importance volume, and how often a
        // path lands there at all. Many lights send most of their photons where they're never stored (into the sky, outside the volume), so the
        // paths are then shared out so that the stored photons follow the power each light delivers, and carry similar power each
        int lightCount = (int)NumBounceLights;
        var pilotPaths = new long[lightCount];
        Array.Fill(pilotPaths, PilotPathsPerLight);
        Report("Emitting photons", 0);
        var pilot = EmitPhotons(d3d, gpu, p, 2, pilotPaths, (long)lightCount * PilotPathsPerLight + 1024, out _);
        var delivered = new double[lightCount];
        var landed = new long[lightCount];
        foreach (var ph in pilot)
            if (ph.Tri < (uint)lightCount)
            {
                delivered[ph.Tri] += Math.Max(0, Vector3.Dot(ph.Power, new Vector3(0.3f, 0.59f, 0.11f)));
                landed[ph.Tri]++;
            }
        long[] Share(long target)
        {
            var paths = new long[lightCount];
            double total = 0;
            foreach (double d in delivered)
                total += d;
            if (target <= 0 || !(total > 0))
                return paths;
            var ideal = new double[lightCount];
            for (int k = 0; k < lightCount; k++)
                if (landed[k] > 0)
                    ideal[k] = Math.Ceiling(target * (delivered[k] / total) / ((double)landed[k] / PilotPathsPerLight));
            // Water-filling within the path budget: a light whose photons rarely land (a bright light far outside the volume) can't take the paths
            // of all the others; it gets fewer, more powerful photons instead
            var order = new int[lightCount];
            for (int k = 0; k < lightCount; k++)
                order[k] = k;
            Array.Sort((double[])ideal.Clone(), order);
            double remaining = MaxPhotonPaths;
            int left = lightCount;
            foreach (int k in order)
            {
                double give = Math.Min(ideal[k], remaining / left);
                paths[k] = (long)give;
                remaining -= give;
                left--;
            }
            return paths;
        }
        var directPaths = Share(directTarget);
        var direct = EmitPhotons(d3d, gpu, p, 0, directPaths, Math.Min(MaxStoredPhotons, directTarget * 2 + 65536), out long directLost);
        Report("Emitting photons", 0.5);
        var indirectPaths = Share(indirectTarget);
        long indirectLost = 0;
        var indirect = indirectTarget > 0 ? EmitPhotons(d3d, gpu, p, 1, indirectPaths, Math.Min(MaxStoredPhotons, indirectTarget * 3 + 65536), out indirectLost) : [];
        cancel.ThrowIfCancellationRequested();
        long directPathCount = 0, indirectPathCount = 0;
        foreach (long n in directPaths) directPathCount += n;
        foreach (long n in indirectPaths) indirectPathCount += n;

        // the photon maps, and the irradiance photons picked from them
        float rd = MathF.Max(1, ps.DirectSearchDistance), ri = MathF.Max(1, ps.IndirectSearchDistance);
        // the estimate searches cells a quarter of the search distance wide (EST_SHELLS)
        var (directSorted, directTable) = Grid(direct, rd / 4, int.MaxValue, 0);
        var (indirectSorted, indirectTable) = Grid(indirect, ri / 4, int.MaxValue, (uint)directSorted.Length);
        var irr = new List<GpuPhoton>();
        Pick(direct, ps.DirectPhotonDensity > 0 ? ps.DirectIrradiancePhotonDensity / ps.DirectPhotonDensity : 0, irr);
        Pick(indirect, ps.IndirectPhotonDensity > 0 ? ps.IndirectIrradiancePhotonDensity / ps.IndirectPhotonDensity : 0, irr);
        // in grid order, so neighbouring threads search the same cells
        var irrArray = Grid(irr.ToArray(), MathF.Max(rd, ri) / 4, int.MaxValue, 0).Sorted;

        // irradiance at each irradiance photon
        var all = new GpuPhoton[directSorted.Length + indirectSorted.Length];
        directSorted.CopyTo(all, 0);
        indirectSorted.CopyTo(all, directSorted.Length);
        var table = new GpuHashCell[directTable.Length + indirectTable.Length];
        directTable.CopyTo(table, 0);
        indirectTable.CopyTo(table, directTable.Length);
        gpu.Set(ref gpu.PhotonsIn, d3d.CreateStructured(all, all.Length));
        gpu.Set(ref gpu.Cells, d3d.CreateStructured(table, table.Length));
        gpu.Set(ref gpu.IrrPhotons, d3d.CreateStructured(irrArray, irrArray.Length));
        p.TableAOff = 0;
        p.TableASize = (uint)(direct.Length > 0 ? directTable.Length : 0);
        p.CellA = rd / 4;
        p.RadiusA = rd;
        p.TableBOff = (uint)directTable.Length;
        p.TableBSize = (uint)(indirect.Length > 0 ? indirectTable.Length : 0);
        p.CellB = ri / 4;
        p.RadiusB = ri;
        p.KNearest = (uint)Math.Max(1, ps.IrradiancePhotons);
        p.ConeK = MathF.Max(1, ps.ConeFilterConstant);
        p.NormalCos = ps.NormalThreshold;
        var energy = new Vector4[Math.Max(1, irrArray.Length)];
        using (var irrOut = d3d.CreateStructured<Vector4>(null, irrArray.Length, uav: true))
        using (var staging = d3d.CreateStaging(Math.Min(irrArray.Length, DownloadChunk) * 16))
        {
            gpu.PhotonOut = null;
            gpu.Counter = null;
            gpu.IrrOut = irrOut;
            int chunk = 256, done = 0;
            var timer = new Stopwatch();
            while (done < irrArray.Length)
            {
                cancel.ThrowIfCancellationRequested();
                int n = Math.Min(chunk, irrArray.Length - done);
                p.EmitBase = (uint)done;
                p.ItemCount = (uint)n;
                timer.Restart();
                gpu.Dispatch(SolverKernel.Irradiance, p, n);
                d3d.Wait();
                chunk = Adapt(chunk, 64, MaxDispatchThreads, timer.Elapsed.TotalSeconds, d3d.IsWarp);
                done += n;
                Report("Irradiance photons", (double)done / irrArray.Length);
            }
            for (int first = 0; first < irrArray.Length; first += DownloadChunk)
                d3d.DownloadRange(irrOut, staging, energy, first, first, Math.Min(DownloadChunk, irrArray.Length - first));
            gpu.IrrOut = null;
        }
        for (int i = 0; i < irrArray.Length; i++)
            irrArray[i].Power = new Vector3(energy[i].X, energy[i].Y, energy[i].Z);
        gpu.Set(ref gpu.PhotonsIn, d3d.CreateStructured<GpuPhoton>(null, 1));

        // the lookup: a fine grid answers most gather rays; a coarse one, up to the search distance, the rest
        float lookupRadius = MathF.Max(rd, ri);
        float fineCell = lookupRadius / 4;
        var (fine, fineTable) = Grid(irrArray, fineCell, 8, 0);
        var (coarse, coarseTable) = Grid(irrArray, lookupRadius, 32, (uint)fine.Length);
        var result = new PhotonLookup
        {
            Photons = new GpuPhoton[fine.Length + coarse.Length],
            Table = new GpuHashCell[fineTable.Length + coarseTable.Length],
            FineSize = (uint)fineTable.Length,
            CoarseOff = (uint)fineTable.Length,
            CoarseSize = (uint)coarseTable.Length,
            FineCell = fineCell,
            CoarseCell = lookupRadius,
            Radius = lookupRadius,
            NormalCos = ps.NormalThreshold,
            PlaneSin = MathF.Sin(Math.Clamp(ps.SearchConeAngle, 0, 90) * MathF.PI / 180),
            ImpMin = imin,
            ImpMax = imax,
            Variant = (ps.LookupSameInstance ? GpuParams.VariantLookupSameInstance : 0) | (ps.LookupFineOnly ? GpuParams.VariantLookupFineOnly : 0),
        };
        fine.CopyTo(result.Photons, 0);
        coarse.CopyTo(result.Photons, fine.Length);
        fineTable.CopyTo(result.Table, 0);
        coarseTable.CopyTo(result.Table, fineTable.Length);
        if (irrArray.Length == 0)
            result.FineSize = result.CoarseSize = 0;

        double meanE = 0;
        foreach (var ip in irrArray)
            meanE += (ip.Power.X + ip.Power.Y + ip.Power.Z) / 3;
        summary = $"Photons: {pilot.Length:N0} of {pilotPaths.Length * PilotPathsPerLight:N0} pilot paths landed; {directPathCount:N0} direct paths -> {direct.Length:N0} direct photons " +
            $"(target {directTarget:N0}, {directLost:N0} over), {indirectPathCount:N0} indirect paths -> {indirect.Length:N0} indirect photons (target {indirectTarget:N0}, {indirectLost:N0} over), " +
            $"{irrArray.Length:N0} irradiance photons (mean {meanE / Math.Max(1, irrArray.Length):G4}), importance radius {radius:F0}, {sw.Elapsed:mm\\:ss\\.f}";
        return result;
    }

    const long PilotPathsPerLight = 8192;

    /// <summary>
    /// Runs one emission pass (paths per bouncing light) and brings its photons back. If more land than the buffer holds, the ones kept (an arbitrary
    /// subset) carry the power of the ones lost
    /// </summary>
    GpuPhoton[] EmitPhotons(SolverGpu d3d, GpuScene gpu, GpuParams p, uint pass, long[] pathsPerLight, long capacity, out long lost)
    {
        lost = 0;
        if (capacity <= 0)
            return [];
        var shares = new List<UInt4>();
        long first = 0;
        for (int k = 0; k < pathsPerLight.Length; k++)
        {
            long count = pathsPerLight[k];
            if (count <= 0)
                continue;
            shares.Add(new UInt4(bounceLights[k], (uint)first, (uint)count, BitConverter.SingleToUInt32Bits(1f / count)));
            first += count;
        }
        if (first == 0)
            return [];
        gpu.Set(ref gpu.EmitLights, d3d.CreateStructured(shares.ToArray(), shares.Count));

        using var photonOut = d3d.CreateStructured<GpuPhoton>(null, (int)capacity, uav: true);
        using var counter = d3d.CreateStructured(new uint[] { 0 }, 1, uav: true);
        gpu.PhotonOut = photonOut;
        gpu.Counter = counter;
        gpu.IrrOut = null;
        p.EmitPass = pass;
        p.EmitCapacity = (uint)capacity;
        p.NumEmitLights = (uint)shares.Count;
        int chunk = 4096;
        long done = 0;
        var timer = new Stopwatch();
        while (done < first)
        {
            cancel.ThrowIfCancellationRequested();
            int n = (int)Math.Min(chunk, first - done);
            p.EmitBase = (uint)done;
            p.ItemCount = (uint)n;
            timer.Restart();
            gpu.Dispatch(SolverKernel.Emit, p, n);
            d3d.Wait();
            chunk = Adapt(chunk, 1024, MaxDispatchThreads, timer.Elapsed.TotalSeconds, d3d.IsWarp);
            done += n;
            Report(pass == 0 ? "Emitting direct photons" : "Emitting indirect photons", (double)done / first);
        }
        gpu.PhotonOut = null;
        gpu.Counter = null;

        var stored = new uint[1];
        using (var staging = d3d.CreateStaging(16))
            d3d.Download(counter, staging, stored, 1);
        int count2 = (int)Math.Min(stored[0], (uint)capacity);
        var photons = new GpuPhoton[count2];
        using (var staging = d3d.CreateStaging(Math.Min(count2, DownloadChunk) * 32))
            for (int f = 0; f < count2; f += DownloadChunk)
                d3d.DownloadRange(photonOut, staging, photons, f, f, Math.Min(DownloadChunk, count2 - f));
        lost = stored[0] - count2;
        if (lost > 0 && pass != 2)
        {
            float scale = (float)stored[0] / count2;
            for (int i = 0; i < count2; i++)
                photons[i].Power *= scale;
        }
        return photons;
    }

    /// <summary>Every 1/fraction-th photon, evenly spread (the input is in emission order, so this spreads over the lights and the scene)</summary>
    static void Pick(GpuPhoton[] photons, float fraction, List<GpuPhoton> into)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        double acc = 0;
        foreach (var ph in photons)
        {
            acc += fraction;
            if (acc < 1)
                continue;
            acc -= 1;
            into.Add(ph with { Power = Vector3.Zero });
        }
    }

    static (int X, int Y, int Z) CellOf(Vector3 p, float cell) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));

    /// <summary>The shader's CellHash: PCG mixing, so neighbouring cells don't cluster in the open-addressed table</summary>
    static uint CellHash(int x, int y, int z, uint size) => Pcg(unchecked((uint)x) ^ Pcg(unchecked((uint)y) ^ Pcg(unchecked((uint)z)))) & (size - 1);

    static uint Pcg(uint v)
    {
        unchecked
        {
            uint state = v * 747796405u + 2891336453u;
            uint word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
            return (word >> 22) ^ word;
        }
    }

    /// <summary>Sorts photons into grid cells (at most cap per cell, evenly picked) and hashes the cells; runs start at baseIndex in the final buffer</summary>
    static (GpuPhoton[] Sorted, GpuHashCell[] Table) Grid(GpuPhoton[] photons, float cell, int cap, uint baseIndex)
    {
        int count = photons.Length;
        var keys = new long[count];
        var order = new int[count];
        for (int i = 0; i < count; i++)
        {
            var (x, y, z) = CellOf(photons[i].Position, cell);
            keys[i] = ((long)(x + (1 << 20)) << 42) | ((long)(y + (1 << 20)) << 21) | (long)(z + (1 << 20));
            order[i] = i;
        }
        Array.Sort(keys, order);
        var sorted = new List<GpuPhoton>(count);
        var cells = new List<GpuHashCell>();
        for (int i = 0; i < count;)
        {
            int j = i;
            while (j < count && keys[j] == keys[i])
                j++;
            int n = j - i, take = Math.Min(n, cap);
            var (x, y, z) = CellOf(photons[order[i]].Position, cell);
            uint start = baseIndex + (uint)sorted.Count;
            for (int k = 0; k < take; k++)
                sorted.Add(photons[order[i + (int)((long)k * n / take)]]);
            cells.Add(new GpuHashCell { X = x, Y = y, Z = z, Start = start, Count = (uint)take });
            i = j;
        }
        uint size = 2;
        while (size < cells.Count * 2u)
            size <<= 1;
        var table = new GpuHashCell[size];
        foreach (var c in cells)
        {
            uint h = CellHash(c.X, c.Y, c.Z, size);
            while (table[h].Count != 0)
                h = (h + 1) & (size - 1);
            table[h] = c;
        }
        return (sorted.ToArray(), table);
    }
}
