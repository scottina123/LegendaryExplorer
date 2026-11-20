using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using LegendaryExplorer.Lightmaps.Codec;
using SharpDXException = SharpDX.SharpDXException;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using D3DBuffer = SharpDX.Direct3D11.Buffer;

namespace LegendaryExplorer.Lightmaps.Solver;

public static partial class LightSolver
{
    static readonly Lazy<bool> gpuAvailable = new(() =>
    {
        try
        {
            using var adapter = SolverD3D.PickHardwareAdapter();
            if (adapter == null)
                return false;
            using var device = new SharpDX.Direct3D11.Device(adapter, DeviceCreationFlags.None, FeatureLevel.Level_11_0);
            return device.FeatureLevel >= FeatureLevel.Level_11_0;
        }
        catch
        {
            return false;
        }
    });

    static partial void IsGpuAvailableImpl(ref bool available) => available = gpuAvailable.Value;

    static partial void SolveImpl(SolverJob job, IProgress<SolverProgress> progress, CancellationToken cancel, ref SolverOutput output)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Scene == null)
            throw new ArgumentException("The job has no scene.", nameof(job));
        output = new SolverRun(job, progress, cancel).Run();
    }

    /// <summary>A session for solving batches of one scene: the scene is packed once and stays on one device (see <see cref="SolverSession"/>)</summary>
    public static SolverSession OpenSession(SolverScene scene, SolverDevice device) => new(scene, device);

    /// <summary>Lights the job's samples with a session's scene and device. The job's scene must be the session's</summary>
    public static SolverOutput Solve(SolverJob job, SolverSession session, IProgress<SolverProgress> progress = null, CancellationToken cancel = default)
    {
        var batch = Begin(job, session, progress, cancel);
        batch.Prepare();
        batch.RunOnDevice();
        return batch.Finish();
    }

    /// <summary>A batch to solve with a session, in steps that can overlap with other batches' (see <see cref="SolverBatch"/>)</summary>
    public static SolverBatch Begin(SolverJob job, SolverSession session, IProgress<SolverProgress> progress = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(job.Scene, session.Scene))
            throw new ArgumentException("The job's scene isn't the session's.", nameof(job));
        return new SolverBatch(new SolverRun(job, progress, cancel, session));
    }
}

/// <summary>One solve: packs the scene, runs the direct and indirect passes in bounded chunks, filters and combines</summary>
internal sealed partial class SolverRun
{
    readonly SolverJob job;
    readonly SolverScene scene;
    readonly SolverSettings settings;
    readonly IProgress<SolverProgress> progress;
    readonly CancellationToken cancel;
    readonly int sampleCount;
    readonly SolverSession session;
    readonly SolverOutput output = new();

    // the packed scene (the session's, shared by its batches)
    PackedScene packed;
    uint[] bounceLights => packed.BounceLights;
    uint NumBounceLights => packed.NumBounceLights;
    GpuSample[] samples;
    float sceneExtent;

    // per receiver: (light, kind 0 lightmap / 1 shadow map, shadow map slot) for the GPU
    UInt4[][] receiverItems;

    long raysCast;

    // results per sample
    Vector3[] d0, d1, d2, ds;   // direct
    Vector3[] i0, i1, i2, iS;   // indirect
    float[] occlusion;
    float[] nearestHit;         // the shortest distance a sample's gather rays went before hitting something
    bool[] gathered;
    float[] visibility;
    int[] visibilityOffsets;

    const int MaxDirectChunk = 1 << 18;
    const int MaxDispatchThreads = 65535 * SolverShaders.GroupSize;

    public SolverRun(SolverJob job, IProgress<SolverProgress> progress, CancellationToken cancel, SolverSession session = null)
    {
        this.job = job;
        scene = job.Scene;
        settings = job.Settings ?? new SolverSettings();
        this.progress = progress;
        this.cancel = cancel;
        this.session = session;
        sampleCount = job.Samples?.Count ?? 0;
    }

    readonly Stopwatch total = new();

    /// <summary>A whole solve on a device of its own</summary>
    public SolverOutput Run()
    {
        Prepare();
        RunOnDevice();
        return Finish();
    }

    /// <summary>CPU: the packed scene (the session's, or packed now), and the batch's samples and per-receiver light lists</summary>
    public void Prepare()
    {
        total.Start();
        var stats = output.Statistics;
        cancel.ThrowIfCancellationRequested();
        Report("Preparing scene", 0);
        var sw = Stopwatch.StartNew();
        packed = session != null ? session.Packed(stage => Report(stage, 0)) : PackedScene.Build(scene, stage => Report(stage, 0));
        stats.BvhTime = sw.Elapsed;
        PackReceivers();
        PackSamples();
        PackDirectItems();
        SceneExtent();
        cancel.ThrowIfCancellationRequested();

        d0 = new Vector3[sampleCount]; d1 = new Vector3[sampleCount]; d2 = new Vector3[sampleCount]; ds = new Vector3[sampleCount];
        i0 = new Vector3[sampleCount]; i1 = new Vector3[sampleCount]; i2 = new Vector3[sampleCount]; iS = new Vector3[sampleCount];
        occlusion = new float[sampleCount];
        nearestHit = new float[sampleCount];
        gathered = new bool[sampleCount];
        total.Stop();
    }

    /// <summary>The device's passes: direct light, then (photons and) bounced light. With a session, one batch at a time on its device</summary>
    public void RunOnDevice()
    {
        total.Start();
        if (session == null)
        {
            try
            {
                using var d3d = SolverGpu.Create(settings.Device, note => output.Statistics.DeviceNote = note);
                using var buffers = new SceneBuffers(d3d, packed);
                Passes(d3d, buffers);
            }
            catch (SharpDXException e)
            {
                throw DeviceFailed(e);
            }
        }
        else
            lock (session.DeviceLock)
            {
                try
                {
                    var (d3d, buffers) = session.Gpu(packed);
                    output.Statistics.DeviceNote = session.DeviceNote;
                    Passes(d3d, buffers);
                }
                catch (SharpDXException e)
                {
                    session.ResetGpu();
                    throw DeviceFailed(e);
                }
            }
        total.Stop();
    }

    static SolverUnavailableException DeviceFailed(SharpDXException e) =>
        new($"The light solver's Direct3D device failed ({e.ResultCode}). If this is the GPU, the driver may have reset; try the CPU.", e);

    void Passes(SolverGpu d3d, SceneBuffers buffers)
    {
        var stats = output.Statistics;
        stats.DeviceName = d3d.Name;
        using var gpu = new GpuScene(d3d, this, buffers);
        var sw = Stopwatch.StartNew();
        DirectPass(d3d, gpu);
        stats.DirectTime = sw.Elapsed;

        sw.Restart();
        PhotonPass(d3d, gpu, stats);
        IndirectPass(d3d, gpu);
        stats.IndirectTime = sw.Elapsed;
    }

    /// <summary>CPU: smooths the bounced light and combines everything into the output</summary>
    public SolverOutput Finish()
    {
        total.Start();
        if (settings.FilterIndirect && job.Grids != null && settings.GatherRays > 0)
        {
            Report("Filtering bounced light", 0);
            SolverFilter.Filter(job.Grids, samples, gathered, i0, i1, i2, iS, occlusion, settings.AmbientOcclusion, RecordRadii(), cancel);
        }

        Combine(output);
        // The output is all that's left of the batch; its working arrays (hundreds of bytes a sample) go now, not when the batch does
        samples = null; receiverItems = null; directItems = null; directBlocks = null;
        d0 = d1 = d2 = ds = i0 = i1 = i2 = iS = null;
        occlusion = nearestHit = null;
        gathered = null;
        total.Stop();
        output.Statistics.RaysCast = raysCast;
        output.Statistics.TotalTime = total.Elapsed;
        Report("Done", 1);
        return output;
    }

    /// <summary>
    /// How far each sample's bounced light may be smoothed, as Lightmass's irradiance cache spreads a record: DistanceSmoothFactor × the record radius,
    /// which is RecordRadiusScale × the nearest surface its gather rays hit, between MinRecordRadius and MaxRecordRadius. Null when the settings
    /// don't ask for it (the filter then goes by texel size alone)
    /// </summary>
    float[] RecordRadii()
    {
        if (!(settings.RecordRadiusScale > 0)) return null;
        var radii = new float[sampleCount];
        float min = Math.Max(0, settings.MinRecordRadius), max = Math.Max(min, settings.MaxRecordRadius);
        float smooth = Math.Max(1e-3f, settings.DistanceSmoothFactor);
        for (int s = 0; s < sampleCount; s++)
            radii[s] = smooth * Math.Clamp(nearestHit[s] * settings.RecordRadiusScale, min, max);
        return radii;
    }

    void Report(string stage, double fraction) => progress?.Report(new SolverProgress(stage, Math.Clamp(fraction, 0, 1)));

    // ------------------------------------------------------------------ packing

    /// <summary>Scene size, for ray lengths: the scene's vertices and the batch's samples</summary>
    void SceneExtent()
    {
        Vector3 bmin = packed.VertexMin, bmax = packed.VertexMax;
        foreach (var s in job.Samples ?? [])
        {
            bmin = Vector3.Min(bmin, s.Position);
            bmax = Vector3.Max(bmax, s.Position);
        }
        sceneExtent = bmin.X <= bmax.X ? 2f * Vector3.Distance(bmin, bmax) + 1f : 1f;
    }

    /// <summary>What each receiver wants from the lights</summary>
    void PackReceivers()
    {
        var lightsIn = scene.Lights ?? [];
        var receivers = job.Receivers ?? [];
        receiverItems = new UInt4[receivers.Count][];
        for (int r = 0; r < receivers.Count; r++)
        {
            var rec = receivers[r];
            var list = new List<UInt4>();
            if (rec != null)
            {
                foreach (int li in rec.LightmapLights ?? [])
                    if ((uint)li < (uint)lightsIn.Count && lightsIn[li] is { Type: not SolverLightType.Sky })
                        list.Add(new UInt4((uint)li, 0, 0, 0));
                var sm = rec.ShadowMapLights ?? [];
                for (int k = 0; k < sm.Length; k++)
                    if ((uint)sm[k] < (uint)lightsIn.Count && lightsIn[sm[k]] is { Type: not SolverLightType.Sky })
                        list.Add(new UInt4((uint)sm[k], 1, (uint)k, 0));
            }
            receiverItems[r] = list.ToArray();
        }
    }

    void PackSamples()
    {
        var receivers = job.Receivers ?? [];
        samples = new GpuSample[Math.Max(1, sampleCount)];
        visibilityOffsets = new int[sampleCount];
        int visCount = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            var s = job.Samples[i];
            var rec = s.Receiver < (uint)receivers.Count ? receivers[(int)s.Receiver] : null;
            visibilityOffsets[i] = visCount;
            visCount += rec?.ShadowMapLights?.Length ?? 0;

            Vector3 ng = SafeNormalize(s.GeometricNormal, SafeNormalize(s.Normal, Vector3.UnitZ));
            Vector3 n = SafeNormalize(s.Normal, ng);
            Vector3 t = s.Tangent, b = s.Bitangent;
            if (!(t.LengthSquared() > 1e-12f) || !(b.LengthSquared() > 1e-12f))
                BuildBasis(n, out t, out b);
            samples[i] = new GpuSample
            {
                Position = s.Position,
                TexelRadius = Math.Max(0, s.TexelRadius),
                Normal = n,
                GeometricNormal = ng,
                Tangent = Vector3.Normalize(t),
                Bitangent = Vector3.Normalize(b),
                Instance = s.Instance,
                Flags = rec?.TwoSidedLighting == true ? GpuSample.TwoSidedLighting : 0,
            };
        }
        visibility = new float[visCount];

        // shadow-mapped sky lights see everything
        var lightsIn = scene.Lights ?? [];
        for (int i = 0; i < sampleCount; i++)
        {
            uint r = job.Samples[i].Receiver;
            if (r >= (uint)receivers.Count || receivers[(int)r]?.ShadowMapLights is not { } sm)
                continue;
            for (int k = 0; k < sm.Length; k++)
                if ((uint)sm[k] < (uint)lightsIn.Count && lightsIn[sm[k]]?.Type == SolverLightType.Sky)
                    visibility[visibilityOffsets[i] + k] = 1;
        }
    }

    static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        float l = v.Length();
        return l > 1e-12f && float.IsFinite(l) ? v / l : fallback;
    }

    static void BuildBasis(Vector3 n, out Vector3 t, out Vector3 b)
    {
        float s = n.Z >= 0 ? 1f : -1f;
        float a = -1f / (s + n.Z);
        float bb = n.X * n.Y * a;
        t = new Vector3(1f + s * n.X * n.X * a, s * bb, -s * n.X);
        b = new Vector3(bb, s + n.Y * n.Y * a, -n.Y);
    }

    GpuParams BaseParams()
    {
        var basis = Directional.Basis;
        var p = new GpuParams
        {
            GatherRays = (uint)Math.Max(0, settings.GatherRays),
            Bounces = (uint)Math.Clamp(settings.Bounces, 0, 32),
            ShadowRays = (uint)Math.Max(1, settings.ShadowRays),
            PenumbraRays = (uint)Math.Max(0, settings.PenumbraShadowRays),
            Seed = settings.Seed,
            NumBounceLights = NumBounceLights,
            NumTriangles = (uint)(scene.Triangles?.Count ?? 0),
            RayOffset = settings.RayOffset,
            NormalOffset = settings.NormalOffset,
            NormalInfluence = Math.Clamp(settings.IndirectNormalInfluenceBoost, 0, 1),
            MaxOcclusionDistance = settings.MaxOcclusionDistance,
            DirScale = settings.DirectionalCoefficientScale,
            DirPower = settings.DirectionalCoefficientFalloffPower,
            SceneExtent = sceneExtent,
            SceneFlags = (packed.HasTranslucency ? GpuParams.SceneHasTranslucency : 0) | (settings.LightFacingShadows ? GpuParams.SceneLightFacingShadows : 0),
            NormalTexelScale = Math.Max(0, settings.NormalOffsetTexelScale),
            SkyUpper = packed.SkyUpper,
            SkyLower = packed.SkyLower,
            Basis0 = new Vector4(basis[0], 0),
            Basis1 = new Vector4(basis[1], 0),
            Basis2 = new Vector4(basis[2], 0),
        };
        ApplyPhotonLookup(ref p);
        return p;
    }

    /// <summary>The scene's buffers on the device, and the binding of every kernel's resources</summary>
    internal sealed class GpuScene : IDisposable
    {
        public readonly GpuBuffer Nodes, Tris, TriVerts, Verts, Materials, Texels, Lights, BounceLights, Samples, Items;
        public GpuBuffer DirectOut, GatherRecords, ReduceOut;
        // photon mapping's inputs (owned: replaced with Set) and outputs (not owned)
        public GpuBuffer PhotonsIn, Cells, IrrPhotons, EmitLights;
        public GpuBuffer PhotonOut, Counter, IrrOut;
        readonly SolverGpu d3d;

        /// <param name="scene">The scene's buffers on this device; they stay the caller's</param>
        public GpuScene(SolverGpu d3d, SolverRun run, SceneBuffers scene)
        {
            this.d3d = d3d;
            (Nodes, Tris, TriVerts, Verts, Materials, Texels, Lights, BounceLights) =
                (scene.Nodes, scene.Tris, scene.TriVerts, scene.Verts, scene.Materials, scene.Texels, scene.Lights, scene.BounceLights);
            Samples = d3d.CreateStructured(run.samples, run.samples.Length);
            Items = d3d.CreateStructured<UInt4>(null, Math.Max(MaxDirectChunk, run.MaxGatherChunk()));
            PhotonsIn = d3d.CreateStructured<GpuPhoton>(null, 1);
            Cells = d3d.CreateStructured<GpuHashCell>(null, 1);
            IrrPhotons = d3d.CreateStructured<GpuPhoton>(null, 1);
            EmitLights = d3d.CreateStructured<UInt4>(null, 1);
        }

        /// <summary>Replaces one of the photon inputs, disposing the old buffer</summary>
        public void Set(ref GpuBuffer slot, GpuBuffer buffer)
        {
            slot?.Dispose();
            slot = buffer;
        }

        public void SetOutputs(GpuBuffer directOut, GpuBuffer gatherRecords, GpuBuffer reduceOut)
        {
            DirectOut = directOut;
            GatherRecords = gatherRecords;
            ReduceOut = reduceOut;
        }

        public void Dispatch(SolverKernel kernel, GpuParams p, int threads) =>
            d3d.Dispatch(kernel, p,
                [Nodes, Tris, TriVerts, Verts, Materials, Texels, Lights, BounceLights, Samples, Items, PhotonsIn, Cells, IrrPhotons, EmitLights],
                [DirectOut, GatherRecords, ReduceOut, PhotonOut, Counter, IrrOut], threads);

        public void Dispose()
        {
            Samples.Dispose(); Items.Dispose();
            PhotonsIn?.Dispose(); Cells?.Dispose(); IrrPhotons?.Dispose(); EmitLights?.Dispose();
        }
    }

    int MaxGatherChunk() => settings.GatherRays > 0 ? Math.Clamp(MaxDispatchThreads / settings.GatherRays, 1, 1 << 18) : 1;

    /// <summary>Grows or shrinks a chunk so one dispatch takes about the target time (well under Windows's 2 s GPU timeout)</summary>
    static int Adapt(int chunk, int min, int max, double seconds, bool warp)
    {
        double target = warp ? 0.5 : 0.1;
        double f = seconds > 1e-4 ? target / seconds : 2;
        f = Math.Clamp(f, 0.25, 2);
        return Math.Clamp((int)(chunk * f), min, max);
    }

    // ------------------------------------------------------------------ direct pass

    // The direct pass's items (sample, light, kind, slot) in the order they go out: blocks of samples, light-slot major within a block, so
    // neighbouring threads trace the same light. Laid out in Prepare, off the device's thread
    UInt4[] directItems;
    // where each block of samples starts in directItems (and a last entry: the item count); a sample's items are all in its block
    int[] directBlocks;
    const int DirectBlock = 256;

    void PackDirectItems()
    {
        var items = new List<UInt4>();
        var blocks = new List<int>();
        for (int first = 0; first < sampleCount; first += DirectBlock)
        {
            blocks.Add(items.Count);
            int end = Math.Min(sampleCount, first + DirectBlock), maxSlots = 0;
            for (int s = first; s < end; s++)
            {
                uint r = job.Samples[s].Receiver;
                if (r < (uint)receiverItems.Length)
                    maxSlots = Math.Max(maxSlots, receiverItems[r].Length);
            }
            for (int lightSlot = 0; lightSlot < maxSlots; lightSlot++)
                for (int s = first; s < end; s++)
                {
                    uint r = job.Samples[s].Receiver;
                    if (r < (uint)receiverItems.Length && lightSlot < receiverItems[r].Length)
                    {
                        var e = receiverItems[r][lightSlot];
                        items.Add(new UInt4((uint)s, e.X, e.Y, e.Z));
                    }
                }
        }
        blocks.Add(items.Count);
        directItems = items.ToArray();
        directBlocks = blocks.ToArray();
    }

    void DirectPass(SolverGpu d3d, GpuScene gpu)
    {
        int totalItems = directItems.Length;
        Report("Direct lighting", 0);
        if (totalItems == 0)
            return;

        // Double-buffered: the device works on one chunk while the CPU reads back the one before. A chunk's results are added up in parallel
        // by block of samples (each sample's items are in one block, in order), so the sums are the same as one item at a time
        using var outA = d3d.CreateStructured<UInt4>(null, MaxDirectChunk * 4, uav: true);
        using var outB = d3d.CreateStructured<UInt4>(null, MaxDirectChunk * 4, uav: true);
        using var gatherDummy = d3d.CreateStructured<UInt4>(null, 1, uav: true);
        using var reduceDummy = d3d.CreateStructured<UInt4>(null, 1, uav: true);
        using var stagingA = d3d.CreateStaging(MaxDirectChunk * 4 * 16);
        using var stagingB = d3d.CreateStaging(MaxDirectChunk * 4 * 16);
        var outs = new[] { outA, outB };
        var stagings = new[] { stagingA, stagingB };
        var upload = new UInt4[MaxDirectChunk];
        var results = new UInt4[MaxDirectChunk * 4];
        int slot = 0, previousFirst = 0, previousCount = 0;
        void ReadBack(int from, int first, int count)
        {
            d3d.ReadStaging(stagings[from], results, count * 4);
            int end = first + count;
            int b0 = Array.BinarySearch(directBlocks, first);
            if (b0 < 0) b0 = ~b0 - 1;
            int b1 = Array.BinarySearch(directBlocks, end);
            if (b1 < 0) b1 = ~b1;
            Parallel.For(b0, b1, b =>
            {
                int lo = Math.Max(directBlocks[b], first), hi = Math.Min(directBlocks[b + 1], end);
                long rays = 0;
                for (int i = lo; i < hi; i++)
                {
                    var it = directItems[i];
                    int s = (int)it.X, k = i - first;
                    rays += results[k * 4 + 1].W;
                    if (it.Z == 0)
                    {
                        d0[s] += results[k * 4 + 0].Xyz;
                        d1[s] += results[k * 4 + 1].Xyz;
                        d2[s] += results[k * 4 + 2].Xyz;
                        ds[s] += results[k * 4 + 3].Xyz;
                    }
                    else
                        visibility[visibilityOffsets[s] + (int)it.W] = results[k * 4 + 0].WFloat;
                }
                Interlocked.Add(ref raysCast, rays);
            });
        }
        var p = BaseParams();
        int chunk = 2048;
        int next = 0;
        var sw = new Stopwatch();
        while (next < totalItems)
        {
            cancel.ThrowIfCancellationRequested();
            int n = Math.Min(chunk, totalItems - next);
            Array.Copy(directItems, next, upload, 0, n);

            sw.Restart();
            d3d.Upload(gpu.Items, upload, n);
            p.ItemCount = (uint)n;
            gpu.SetOutputs(outs[slot], gatherDummy, reduceDummy);
            gpu.Dispatch(SolverKernel.Direct, p, n);
            d3d.CopyToStaging(outs[slot], stagings[slot], n * 4 * 16);
            // the previous chunk, while the device works on this one
            if (previousCount > 0)
            {
                ReadBack(slot ^ 1, previousFirst, previousCount);
                Report("Direct lighting", (double)(previousFirst + previousCount) / totalItems);
                chunk = Adapt(chunk, 64, MaxDirectChunk, sw.Elapsed.TotalSeconds, d3d.IsWarp);
            }
            (previousFirst, previousCount) = (next, n);
            next += n;
            slot ^= 1;
        }
        if (previousCount > 0)
        {
            ReadBack(slot ^ 1, previousFirst, previousCount);
            Report("Direct lighting", 1);
        }
    }

    // ------------------------------------------------------------------ indirect pass

    void IndirectPass(SolverGpu d3d, GpuScene gpu)
    {
        var receivers = job.Receivers ?? [];
        var list = new List<int>();
        for (int i = 0; i < sampleCount; i++)
        {
            uint r = job.Samples[i].Receiver;
            if (r < (uint)receivers.Count && receivers[(int)r] is { GatherIndirect: true })
                list.Add(i);
        }
        Report("Bounced light", 0);
        int gatherRays = settings.GatherRays;
        if (list.Count == 0 || gatherRays <= 0)
            return;
        foreach (int s in list)
            gathered[s] = true;

        int maxChunk = MaxGatherChunk();
        using var recordBuf = d3d.CreateStructured<UInt4>(null, maxChunk * gatherRays, uav: true);
        using var reduceA = d3d.CreateStructured<UInt4>(null, maxChunk * 4, uav: true);
        using var reduceB = d3d.CreateStructured<UInt4>(null, maxChunk * 4, uav: true);
        using var directDummy = d3d.CreateStructured<UInt4>(null, 1, uav: true);
        using var stagingA = d3d.CreateStaging(maxChunk * 4 * 16);
        using var stagingB = d3d.CreateStaging(maxChunk * 4 * 16);
        // Double-buffered like the direct pass: the device gathers one chunk while the CPU reads back the one before
        var reduces = new[] { reduceA, reduceB };
        var stagings = new[] { stagingA, stagingB };

        var items = new UInt4[maxChunk];
        var results = new UInt4[maxChunk * 4];
        var p = BaseParams();
        int chunk = Math.Min(maxChunk, 128);
        int done = 0, slot = 0, previousFirst = 0, previousCount = 0;
        void ReadBack(int from, int first, int count)
        {
            d3d.ReadStaging(stagings[from], results, count * 4);
            for (int k = 0; k < count; k++)
            {
                int s = list[first + k];
                i0[s] = results[k * 4 + 0].Xyz;
                i1[s] = results[k * 4 + 1].Xyz;
                i2[s] = results[k * 4 + 2].Xyz;
                iS[s] = results[k * 4 + 3].Xyz;
                occlusion[s] = results[k * 4 + 0].WFloat;
                nearestHit[s] = results[k * 4 + 2].WFloat;
                raysCast += results[k * 4 + 1].W;
            }
            Report("Bounced light", (double)(first + count) / list.Count);
        }
        var sw = new Stopwatch();
        while (done < list.Count)
        {
            cancel.ThrowIfCancellationRequested();
            int n = Math.Min(chunk, list.Count - done);
            for (int k = 0; k < n; k++)
                items[k] = new UInt4((uint)list[done + k], 0, 0, 0);

            sw.Restart();
            d3d.Upload(gpu.Items, items, n);
            gpu.SetOutputs(directDummy, recordBuf, reduces[slot]);
            p.ItemCount = (uint)(n * gatherRays);
            gpu.Dispatch(SolverKernel.Gather, p, n * gatherRays);
            p.ItemCount = (uint)n;
            gpu.Dispatch(SolverKernel.Reduce, p, n);
            d3d.CopyToStaging(reduces[slot], stagings[slot], n * 4 * 16);
            if (previousCount > 0)
            {
                ReadBack(slot ^ 1, previousFirst, previousCount);
                chunk = Adapt(chunk, 16, maxChunk, sw.Elapsed.TotalSeconds, d3d.IsWarp);
            }
            (previousFirst, previousCount) = (done, n);
            done += n;
            slot ^= 1;
        }
        if (previousCount > 0)
            ReadBack(slot ^ 1, previousFirst, previousCount);
    }

    // ------------------------------------------------------------------ combine

    void Combine(SolverOutput output)
    {
        output.Directional0 = new Vector3[sampleCount];
        output.Directional1 = new Vector3[sampleCount];
        output.Directional2 = new Vector3[sampleCount];
        output.Simple = new Vector3[sampleCount];
        output.IndirectSimple = new Vector3[sampleCount];
        output.Visibility = visibility;
        output.VisibilityOffsets = visibilityOffsets;
        bool ao = settings.AmbientOcclusion;
        float fullyOccluded = Math.Max(settings.FullyOccludedSamplesFraction, 1e-4f);
        for (int s = 0; s < sampleCount; s++)
        {
            float directScale = 1, indirectScale = 1;
            if (ao && gathered[s])
            {
                float occ = MathF.Min(MathF.Pow(Math.Clamp(occlusion[s], 0, 1), settings.OcclusionExponent) / fullyOccluded, 1f);
                directScale = 1 + (1 - occ - 1) * settings.DirectOcclusionFraction;
                indirectScale = 1 + (1 - occ - 1) * settings.IndirectOcclusionFraction;
            }
            output.Directional0[s] = d0[s] * directScale + i0[s] * indirectScale;
            output.Directional1[s] = d1[s] * directScale + i1[s] * indirectScale;
            output.Directional2[s] = d2[s] * directScale + i2[s] * indirectScale;
            output.Simple[s] = ds[s] * directScale + iS[s] * indirectScale;
            output.IndirectSimple[s] = iS[s] * indirectScale;
        }
    }
}
