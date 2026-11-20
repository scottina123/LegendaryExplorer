using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using LegendaryExplorer.Lightmaps.Codec;
using LegendaryExplorer.Lightmaps.Solver;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Lightmaps.Lightmass;

/// <summary>Which solver lights a <see cref="LightmassScene"/>'s mappings</summary>
public enum LightmassSolverChoice
{
    /// <summary>LEX's solver on the GPU</summary>
    Gpu,
    /// <summary>LEX's solver on every CPU core (the same kernels, on Direct3D's WARP device)</summary>
    Cpu,
    /// <summary>UDK's UnrealLightmass.exe through Swarm, the reference</summary>
    UdkLightmass,
}

/// <summary>The Lightmass ini settings the local solve uses beyond <see cref="SolverSettings"/>: texel sizes and the distance field shadow maps'</summary>
public sealed class LightmassLocalShadowSettings
{
    public bool AllowDistanceFields = true;
    public float MaxTransitionDistance = 50, HighResTexelsPerTransition = 50, SmallestTexelRadius = 0.1f;
    public int MinUpsampleFactor = 3;
}

/// <summary>
/// Lights a <see cref="LightmassScene"/> with LEX's own solver (<see cref="LightSolver"/>) instead of UDK's Lightmass, and returns the same results
/// <see cref="LightmassResults"/> reads from Lightmass's channels, so the rest of the bake doesn't change.
/// <list type="number">
/// <item>The scene is flattened: every mesh instance's LOD 0 (occluders included; other LODs only shadow themselves in Lightmass and are left out, their
/// samples shadowed by their LOD 0), BSP and terrain triangles in world space, with their materials and shadowing flags.</item>
/// <item>For each mapping, its relevant lights are routed into the lightmap or shadow maps as Lightmass routes them
/// (<see cref="LightmassSolverLights.Route(LightmassLight, bool, bool, bool)"/>), and its samples are made: a texel sample where its triangles cover the
/// lightmap (<see cref="LightmassTexelRasterizer"/>), a sample per vertex for vertex mappings, and a finer grid for distance field shadow maps.</item>
/// <item>The solver's output becomes each mapping's <see cref="LightmassMappingResult"/>.</item>
/// </list>
/// </summary>
public static class LightmassLocalSolve
{
    /// <summary>The solver's settings from Lightmass's ini, scaled for the quality as <see cref="LightmassSceneHeader.Write"/> scales them, and the world's</summary>
    public static (SolverSettings Solver, LightmassLocalShadowSettings Shadows) Settings(DuplicatingIni ini, LightmassQuality quality, LightmassWorldSettings world,
        SolverDevice device)
    {
        const string Constants = "DevOptions.StaticLightingSceneConstants", Shadows = "DevOptions.StaticShadows", Importance = "DevOptions.ImportanceTracing",
            Cache = "DevOptions.IrradianceCache";
        string q = quality switch
        {
            LightmassQuality.Medium => "DevOptions.StaticLightingMediumQuality",
            LightmassQuality.High => "DevOptions.StaticLightingHighQuality",
            LightmassQuality.Production => "DevOptions.StaticLightingProductionQuality",
            _ => null,
        };
        float Scale(string key) => q == null ? 1f : ini.Float(q, key);
        // FScene::Import scales the scene's distances by the level scale
        float levelScale = ini.Float(Constants, "StaticLightingLevelScale") * world.StaticLightingLevelScale;
        var solver = new SolverSettings
        {
            Device = device,
            Profile = SolverProfile.Lightmass,
            ShadowRays = Math.Max(1, (int)(ini.Int(Shadows, "NumShadowRays") * Scale("NumShadowRaysScale"))),
            PenumbraShadowRays = Math.Max(0, (int)(ini.Int(Shadows, "NumPenumbraShadowRays") * Scale("NumPenumbraShadowRaysScale"))),
            GatherRays = Math.Max(1, (int)(ini.Int(Importance, "NumHemisphereSamples") * Scale("NumHemisphereSamplesScale"))),
            BounceGatherRays = Math.Max(1, ini.Int(Importance, "NumBounceHemisphereSamples")),
            Bounces = Math.Max(0, world.NumIndirectLightingBounces),
            DirectionalCoefficientScale = ini.Float(Constants, "DirectionalCoefficientScale"),
            DirectionalCoefficientFalloffPower = ini.Float(Constants, "DirectionalCoefficientFalloffPower"),
            RayOffset = ini.Float(Constants, "VisibilityRayOffsetDistance") * levelScale,
            NormalOffset = ini.Float(Constants, "VisibilityNormalOffsetDistance") * levelScale,
            NormalOffsetTexelScale = ini.Float(Constants, "VisibilityNormalOffsetSampleRadiusScale"),
            LightFacingShadows = true,
            IndirectNormalInfluenceBoost = world.IndirectNormalInfluenceBoost,
            AmbientOcclusion = world.UseAmbientOcclusion,
            DirectOcclusionFraction = world.DirectIlluminationOcclusionFraction,
            IndirectOcclusionFraction = world.IndirectIlluminationOcclusionFraction,
            OcclusionExponent = world.OcclusionExponent,
            FullyOccludedSamplesFraction = world.FullyOccludedSamplesFraction,
            MaxOcclusionDistance = world.MaxOcclusionDistance * levelScale,
            FilterIndirect = ini.Bool(Cache, "bAllowIrradianceCaching"),
            // The irradiance cache's record radius (FIrradianceCachingSettings), so smoothing leaves contact shadows alone
            RecordRadiusScale = ini.Float(Cache, "RecordRadiusScale") * Scale("RecordRadiusScaleScale"),
            MinRecordRadius = ini.Float(Cache, "MinRecordRadius") * Scale("MinRecordRadiusScale") * levelScale,
            MaxRecordRadius = ini.Float(Cache, "MaxRecordRadius") * levelScale,
            DistanceSmoothFactor = ini.Float(Cache, "DistanceSmoothFactor"),
        };
        // Lightmass's photon mapping for bounced light. FScene scales the densities down and the distances up with the level scale
        const string Photons = "DevOptions.PhotonMapping";
        if (ini.Bool(Photons, "bUsePhotonMapping"))
        {
            float densityScale = 1 / MathF.Max(1e-3f, levelScale * levelScale);
            solver.Photons = new SolverPhotonSettings
            {
                DirectPhotonDensity = ini.Float(Photons, "DirectPhotonDensity") * Scale("NumDirectPhotonsScale") * densityScale,
                DirectIrradiancePhotonDensity = ini.Float(Photons, "DirectIrradiancePhotonDensity") * Scale("NumDirectPhotonsScale") * densityScale,
                IndirectPhotonDensity = ini.Float(Photons, "IndirectPhotonDensity") * Scale("NumIndirectPhotonsScale") * densityScale,
                IndirectIrradiancePhotonDensity = ini.Float(Photons, "IndirectIrradiancePhotonDensity") * Scale("NumIndirectIrradiancePhotonsScale") * densityScale,
                IrradiancePhotons = ini.Int(Photons, "NumIrradianceCalculationPhotons"),
                DirectSearchDistance = ini.Float(Photons, "DirectPhotonSearchDistance") * Scale("DirectPhotonSearchDistanceScale") * levelScale,
                IndirectSearchDistance = ini.Float(Photons, "IndirectPhotonSearchDistance") * levelScale,
                NormalThreshold = ini.Float(Photons, "PhotonSearchAngleThreshold"),
                SearchConeAngle = ini.Float(Photons, "IrradiancePhotonSearchConeAngle"),
                ConeFilterConstant = ini.Float(Photons, "ConeFilterConstant"),
                DirectByShadowRays = !ini.Bool(Photons, "bUsePhotonsForDirectLighting"),
            };
        }
        var shadows = new LightmassLocalShadowSettings
        {
            AllowDistanceFields = ini.Bool(Shadows, "bAllowSignedDistanceFieldShadows"),
            MaxTransitionDistance = ini.Float(Shadows, "MaxTransitionDistanceWorldSpace") * levelScale,
            HighResTexelsPerTransition = (int)(ini.Int(Shadows, "ApproximateHighResTexelsPerMaxTransitionDistance") * Scale("ApproximateHighResTexelsPerMaxTransitionDistanceScale")),
            MinUpsampleFactor = q == null ? ini.Int(Shadows, "MinDistanceFieldUpsampleFactor") : ini.Int(q, "MinDistanceFieldUpsampleFactor"),
            SmallestTexelRadius = ini.Float(Constants, "SmallestTexelRadius") * levelScale,
        };
        // Tuning against UDK (--compare-lighting): LEX_SOLVER_DEBUG=direct leaves out bounced light, sky and environment
        if (System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG") is { } debug && debug.Split(',').Contains("direct", StringComparer.OrdinalIgnoreCase))
            solver.GatherRays = 0;
        if (System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',').FirstOrDefault(t => t.StartsWith("bounces:", StringComparison.OrdinalIgnoreCase)) is { } bounces)
            solver.Bounces = int.Parse(bounces[8..]);
        if (System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',').FirstOrDefault(t => t.StartsWith("normaloffset:", StringComparison.OrdinalIgnoreCase)) is { } offset)
            solver.NormalOffset = float.Parse(offset[13..], System.Globalization.CultureInfo.InvariantCulture);
        // Bounced light is path traced by default: it matches UDK texel by texel better than the photon map does (p95 error 106% vs 490% at High).
        // photon mapping tuning: photonmap (use it), pmphotons (direct light at irradiance photons from photons), pmshadowrays,
        // pmspotsphere, pmnofalloff, pmcount:X, pmk:N, pmradius:X, pmcone:X, pmnormal:X
        var tokens = System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',') ?? [];
        static float F(string t) => float.Parse(t[(t.IndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        if (!tokens.Contains("photonmap")) solver.Photons = null;
        if (tokens.Contains("nofilter")) solver.FilterIndirect = false;
        if (tokens.FirstOrDefault(t => t.StartsWith("dirscale:")) is { } ds) solver.DirectionalCoefficientScale = F(ds);
        if (tokens.FirstOrDefault(t => t.StartsWith("dirpower:")) is { } dp) solver.DirectionalCoefficientFalloffPower = F(dp);        if (solver.Photons is { } pm)
            foreach (var t in tokens)
            {
                if (t == "pmphotons") pm.DirectByShadowRays = false;
                else if (t == "pmshadowrays") pm.DirectByShadowRays = true;
                else if (t == "pmspotsphere") pm.SpotEmitsSphere = true;
                else if (t == "pmnofalloff") pm.IgnoreFalloff = true;
                else if (t == "pmsame") pm.LookupSameInstance = true;
                else if (t == "pmfine") pm.LookupFineOnly = true;
                else if (t == "pmwide") pm.LookupFineOnly = false;
                else if (t == "pmpassback") pm.PassBackFaces = true;
                else if (t.StartsWith("pmcount:")) pm.CountScale = F(t);
                else if (t.StartsWith("pmk:")) pm.IrradiancePhotons = (int)F(t);
                else if (t.StartsWith("pmradius:")) pm.DirectSearchDistance = pm.IndirectSearchDistance = F(t);
                else if (t.StartsWith("pmcone:")) pm.SearchConeAngle = F(t);
                else if (t.StartsWith("pmnormal:")) pm.NormalThreshold = F(t);
            }
        return (solver, shadows);
    }

    /// <summary>
    /// The most samples a batch of mappings is solved with at once (an estimate: their texels or vertices). A level's mappings can hold tens of
    /// millions of texels, too many for one solve's buffers and output; the scene is the same for every batch
    /// </summary>
    public const int BatchSamples = 4 * 1024 * 1024;

    /// <summary>Solves the scene's mappings (<see cref="LightmassScene.Mappings"/>) and returns their results by mapping GUID</summary>
    /// <param name="solved">Called with each batch's results as they come (from a worker thread), e.g. to start writing lightmaps</param>
    public static Dictionary<Guid, LightmassMappingResult> Solve(LightmassScene scene, DuplicatingIni ini, LightmassQuality quality, SolverDevice device,
        Action<string> log, CancellationToken cancel, out SolverStatistics statistics, Func<LightmassMapping, bool> only = null,
        Action<IReadOnlyList<LightmassMappingResult>> solved = null)
    {
        log ??= _ => { };
        var timer = Stopwatch.StartNew();
        var (settings, shadows) = Settings(ini, quality, scene.World, device);
        var sceneState = BuildScene(scene, cancel);
        var mappings = scene.Mappings.Where(m => sceneState.Geometry.ContainsKey(m) && (only == null || only(m))).ToList();
        var batches = Batches(mappings);
        log($"Solver input: {sceneState.Scene.Triangles.Count:N0} triangles, {sceneState.Scene.Lights.Count} lights, {mappings.Count} mappings in " +
            $"{batches.Count} {(batches.Count == 1 ? "batch" : "batches")}, built in {timer.Elapsed:mm\\:ss\\.f} ({MemoryNote()})");
        var world = scene.World;
        log($"World: {world.NumIndirectLightingBounces} bounces, environment {world.EnvironmentColor} x {world.EnvironmentIntensity}, diffuse boost {world.DiffuseBoost}, " +
            $"emissive boost {world.EmissiveBoost}, AO {world.UseAmbientOcclusion}; {sceneState.Scene.Materials.Count} materials, mean albedo " +
            $"{(sceneState.Scene.Materials.Count == 0 ? 0 : sceneState.Scene.Materials.Average(m => m.Diffuse?.Texels.Average(t => (t.X + t.Y + t.Z) / 3) ?? 0)):F3}, " +
            $"{sceneState.Scene.Lights.Count(l => l.Type == SolverLightType.Sky)} sky lights");
        if (System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',').Contains("materials") == true)
            foreach (var m in scene.Materials)
            {
                static string Mean(MaterialSamples s)
                {
                    if (s?.Rgba is not { Length: > 0 } t) return "-";
                    double r = 0, g = 0, b = 0; int n = t.Length / 4;
                    for (int i = 0; i < n; i++) { r += (float)t[i * 4]; g += (float)t[i * 4 + 1]; b += (float)t[i * 4 + 2]; }
                    return $"({r / n:F3},{g / n:F3},{b / n:F3})@{s.Size}";
                }
                log($"  material {m.Source}: blend {m.BlendMode}, two-sided {m.TwoSided}, clip {m.OpacityMaskClipValue}, diffuse {Mean(m.Diffuse)}, transmission {Mean(m.Transmission)}, emissive {Mean(m.Emissive)}");
            }
        if (System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',').Contains("lights") == true)
            for (int i = 0; i < scene.Lights.Count; i++)
            {
                var l = scene.Lights[i];
                var sl = sceneState.Scene.Lights[i];
                log($"  light {i} {LightName(l)} {l.Light.Type}: flags 0x{l.Flags:X}, shadow-only {l.ShadowOnly}, GI-only {l.IndirectOnly}, dominant {l.Light.IsDominant}, " +
                    $"position {sl.Position}, direction {sl.Direction}, cos outer {sl.CosOuterCone:F3} (cone {l.InnerConeAngle}/{l.OuterConeAngle}, source radius {l.LightSourceRadius}), colour {sl.Color} (brightness {l.Brightness}), radius {sl.Radius}, falloff {sl.FalloffExponent}, indirect scale {l.IndirectLightingScale}, bounces {sl.Bounces}");
            }
        log($"Solver settings: {settings.ShadowRays} shadow rays (+{settings.PenumbraShadowRays} in penumbras), {settings.GatherRays} gather rays " +
            $"({settings.BounceGatherRays} per later bounce), {settings.Bounces} bounces, {device}");
        cancel.ThrowIfCancellationRequested();

        statistics = new SolverStatistics();
        var stats = statistics;
        var results = new Dictionary<Guid, LightmassMappingResult>();
        var progress = Progress(log);
        var logLock = new object();
        void Log(string message) { lock (logLock) log(message); }
        long samples = 0;
        var total = Stopwatch.StartNew();

        // A pipeline over one solver session (the scene packed and on the device once): while the device traces a batch, the CPU prepares the next
        // batch's samples and finishes the previous one (filtering, distance fields, conversion). Each batch's result is what it would be alone
        using var session = LightSolver.OpenSession(sceneState.Scene, settings.Device);
        (SolvePlan Plan, SolverBatch Batch, TimeSpan Time) Prepare(int b)
        {
            var sw = Stopwatch.StartNew();
            var plan = BuildJob(sceneState, batches[b], settings, shadows, cancel);
            var batch = LightSolver.Begin(plan.Job, session, progress, cancel);
            batch.Prepare();
            return (plan, batch, sw.Elapsed);
        }
        void Finish(int b, SolvePlan plan, SolverBatch batch, TimeSpan prepareTime)
        {
            var sw = Stopwatch.StartNew();
            var output = batch.Finish();
            var finishTime = sw.Elapsed; sw.Restart();
            lock (stats) Accumulate(stats, output.Statistics);
            if (output.Statistics.PhotonSummary != null)
                Log(output.Statistics.PhotonSummary);
            cancel.ThrowIfCancellationRequested();
            if (plan.Mappings.Any(p => p.DistanceFieldLights.Count > 0))
                RefineDistanceFields(plan, output, shadows, session, progress, Log, cancel);
            var refineTime = sw.Elapsed; sw.Restart();
            var converted = new LightmassMappingResult[plan.Mappings.Count];
            Parallel.For(0, plan.Mappings.Count, new ParallelOptions { CancellationToken = cancel }, i => converted[i] = Convert(plan.Mappings[i], plan.Job, output, shadows));
            lock (results)
                foreach (var r in converted) if (r != null) results[r.Guid] = r;
            solved?.Invoke(converted.Where(r => r != null).ToList());
            var s = output.Statistics;
            Log($"Batch {b + 1}/{batches.Count} done (samples {prepareTime.TotalSeconds:F1}s, direct {s.DirectTime.TotalSeconds:F1}s, " +
                $"bounce {s.IndirectTime.TotalSeconds:F1}s, filtering {finishTime.TotalSeconds:F1}s, distance fields {refineTime.TotalSeconds:F1}s, " +
                $"convert {sw.Elapsed.TotalSeconds:F1}s; {MemoryNote()})");
        }

        var next = batches.Count > 0 ? Task.Run(() => Prepare(0), cancel) : null;
        var finishing = Task.CompletedTask;
        try
        {
            for (int b = 0; b < batches.Count; b++)
            {
                var (plan, batch, prepareTime) = next.GetAwaiter().GetResult();
                int following = b + 1;
                if (following < batches.Count) next = Task.Run(() => Prepare(following), cancel);
                samples += plan.Job.Samples.Count;
                Log($"Batch {b + 1}/{batches.Count}: {plan.Mappings.Count} mappings, {plan.Job.Samples.Count:N0} samples");
                Probe(plan, Log);
                batch.RunOnDevice();
                // The previous batch finished on the CPU while this one was on the device
                finishing.GetAwaiter().GetResult();
                int index = b;
                finishing = Task.Run(() =>
                {
                    Finish(index, plan, batch, prepareTime);
                    // The batch is done with: its arrays (gigabytes) go now. Left to its own pace the GC lets batches' garbage pile up to double the
                    // bake's memory; a collection per batch costs a fraction of a second
                    plan = default; batch = null;
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                }, cancel);
            }
            finishing.GetAwaiter().GetResult();
        }
        finally
        {
            // Nothing may still be using the session when it's disposed
            try { Task.WaitAll(new[] { next, finishing }.Where(t => t != null).ToArray()); } catch { }
        }
        statistics.TotalTime = total.Elapsed;
        if (statistics.DeviceNote != null) log(statistics.DeviceNote);
        log($"Solved {samples:N0} samples on {statistics.DeviceName ?? device.ToString()} in {total.Elapsed:hh\\:mm\\:ss} (BVH {statistics.BvhTime:mm\\:ss\\.f}, direct " +
            $"{statistics.DirectTime:hh\\:mm\\:ss}, indirect {statistics.IndirectTime:hh\\:mm\\:ss}, {statistics.RaysCast:N0} rays)");
        return results;
    }

    /// <summary>The process's memory for the log: the managed heap and everything it has committed</summary>
    internal static string MemoryNote()
    {
        using var process = Process.GetCurrentProcess();
        // Tuning: LEX_SOLVER_DEBUG=memlive collects first and logs what's still in use
        if (System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',').Contains("memlive") == true)
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            process.Refresh();
            return $"memory {GC.GetTotalMemory(false) / (double)(1L << 30):F1} GB managed in use, {process.PrivateMemorySize64 / (double)(1L << 30):F1} GB in all";
        }
        return $"memory {GC.GetGCMemoryInfo().HeapSizeBytes / (double)(1L << 30):F1} GB managed, {process.PrivateMemorySize64 / (double)(1L << 30):F1} GB in all";
    }

    /// <summary>Tuning: LEX_SOLVER_DEBUG=probe:X:Y logs texel (X, Y) of every texture mapping in the job, and each lightmap light's relation to it</summary>
    static void Probe(SolvePlan plan, Action<string> log)
    {
        var token = System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',').FirstOrDefault(t => t.StartsWith("probe:", StringComparison.OrdinalIgnoreCase));
        if (token == null) return;
        var parts = token.Split(':');
        int x = int.Parse(parts[1]), y = int.Parse(parts[2]);
        foreach (var p in plan.Mappings.Where(p => p.Texture && x < p.SizeX && y < p.SizeY))
        {
            int s = p.Samples[y * p.SizeX + x];
            if (s < 0) { log($"probe {p.Guid} ({x}, {y}): not covered"); continue; }
            var sample = plan.Job.Samples[s];
            log($"probe {p.Guid} ({x}, {y}): position {sample.Position}, normal {sample.Normal}, geometric {sample.GeometricNormal}, radius {sample.TexelRadius}");
            foreach (var (index, _) in p.LightmapLights)
            {
                var l = plan.Job.Scene.Lights[index];
                var toLight = l.Position - sample.Position;
                float d = toLight.Length();
                var dir = toLight / d;
                log($"    light {index} {l.Type}: position {l.Position}, direction {l.Direction}, radius {l.Radius}, distance {d:F1}, N.L {System.Numerics.Vector3.Dot(sample.Normal, dir):F3}, " +
                    $"cone cos {System.Numerics.Vector3.Dot(-dir, l.Direction):F3} (outer {l.CosOuterCone:F3}), colour {l.Color}");
                if (System.Numerics.Vector3.Dot(sample.Normal, dir) > 0)
                    foreach (var blocker in Blockers(plan.Job.Scene, sample.Position + sample.GeometricNormal * 0.25f, l.Type == SolverLightType.Directional ? -l.Direction * 1e6f : toLight))
                        log($"        blocked by {blocker}");
            }
            // what the texel's gather rays see first: 32 cosine-distributed directions about the geometric normal
            var gn = sample.GeometricNormal;
            var tx = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(MathF.Abs(gn.X) < 0.9f ? System.Numerics.Vector3.UnitX : System.Numerics.Vector3.UnitY, gn));
            var ty = System.Numerics.Vector3.Cross(gn, tx);
            for (int i = 0; i < 32; i++)
            {
                float u1 = (i % 4 + 0.5f) / 4, u2 = (i / 4 + 0.5f) / 8;
                float r = MathF.Sqrt(u1), phi = 2 * MathF.PI * u2;
                var dir = tx * (r * MathF.Cos(phi)) + ty * (r * MathF.Sin(phi)) + gn * MathF.Sqrt(MathF.Max(0, 1 - u1));
                var first = Blockers(plan.Job.Scene, sample.Position + gn * 0.25f, dir * 1e5f).FirstOrDefault();
                log($"    gather {i} (cos {MathF.Sqrt(MathF.Max(0, 1 - u1)):F2}): {first ?? "nothing"}");
            }
        }
    }

    static readonly Dictionary<uint, string> DebugInstanceNames = [];

    static string MaterialInfo(SolverScene scene, uint index)
    {
        if (index >= scene.Materials.Count) return "none";
        var m = scene.Materials[(int)index];
        static string Mean(SolverTexture t) => t?.Texels == null ? "null" : $"{t.Texels.Average(v => v.X):F2}/{t.Texels.Average(v => v.Y):F2}/{t.Texels.Average(v => v.Z):F2} ({t.Size})";
        return $"two-sided {m.TwoSided}, masked {m.Masked} clip {m.OpacityMaskClipValue}, transmission {Mean(m.Transmission)}, diffuse {Mean(m.Diffuse)}";
    }

    /// <summary>Tuning: every shadow-casting triangle the segment from o along v crosses (brute force), as instance name, distance and side</summary>
    static List<string> Blockers(SolverScene scene, System.Numerics.Vector3 o, System.Numerics.Vector3 v)
    {
        var hits = new List<(float T, string What)>();
        float len = v.Length();
        var d = v / len;
        foreach (var tri in scene.Triangles)
        {
            if ((tri.Flags & SolverTriangleFlags.CastShadow) == 0) continue;
            var p0 = scene.Vertices[(int)tri.V0].Position; var e1 = scene.Vertices[(int)tri.V1].Position - p0; var e2 = scene.Vertices[(int)tri.V2].Position - p0;
            var pv = System.Numerics.Vector3.Cross(d, e2); float det = System.Numerics.Vector3.Dot(e1, pv);
            if (MathF.Abs(det) < 1e-12f) continue;
            float inv = 1 / det; var sv = o - p0; float u = System.Numerics.Vector3.Dot(sv, pv) * inv;
            if (u < 0 || u > 1) continue;
            var q = System.Numerics.Vector3.Cross(sv, e1); float vv = System.Numerics.Vector3.Dot(d, q) * inv;
            if (vv < 0 || u + vv > 1) continue;
            float t = System.Numerics.Vector3.Dot(e2, q) * inv;
            if (t <= 0.1f || t >= len - 0.1f) continue;
            var n = System.Numerics.Vector3.Cross(e1, e2);
            hits.Add((t, $"{(DebugInstanceNames.TryGetValue(tri.Instance, out var name) ? name : tri.Instance.ToString())} at {t:F0} ({(System.Numerics.Vector3.Dot(n, d) < 0 ? "facing" : "back")}, material {tri.Material}: {MaterialInfo(scene, tri.Material)}, flags {tri.Flags}, vertex normal.d {System.Numerics.Vector3.Dot(scene.Vertices[(int)tri.V0].Normal, d):F2})"));
        }
        return hits.OrderBy(h => h.T).Take(6).Select(h => h.What).ToList();
    }

    /// <summary>The mappings in batches of about <see cref="BatchSamples"/> samples, biggest first; a mapping bigger than that is a batch of its own</summary>
    static List<List<LightmassMapping>> Batches(List<LightmassMapping> mappings)
    {
        var batches = new List<List<LightmassMapping>>();
        List<LightmassMapping> current = null;
        long size = 0;
        foreach (var mapping in mappings.OrderByDescending(m => m.Cost))
        {
            if (current == null || size + mapping.Cost > BatchSamples && current.Count > 0)
            {
                batches.Add(current = []);
                size = 0;
            }
            current.Add(mapping);
            size += mapping.Cost;
        }
        return batches;
    }

    static void Accumulate(SolverStatistics total, SolverStatistics batch)
    {
        if (batch == null) return;
        total.DeviceName ??= batch.DeviceName;
        total.DeviceNote ??= batch.DeviceNote;
        total.RaysCast += batch.RaysCast;
        total.BvhTime += batch.BvhTime;
        total.DirectTime += batch.DirectTime;
        total.IndirectTime += batch.IndirectTime;
    }

    static IProgress<SolverProgress> Progress(Action<string> log)
    {
        var lastReport = Stopwatch.StartNew();
        string lastStage = null;
        var progressLock = new object();
        return new SynchronousProgress<SolverProgress>(p =>
        {
            lock (progressLock)
            {
                if (p.Stage == lastStage && lastReport.Elapsed.TotalSeconds < 2) return;
                lastStage = p.Stage;
                lastReport.Restart();
            }
            log($"Solver: {p.Stage} {p.Fraction:P0}");
        });
    }

    /// <summary>An IProgress that reports on the solver's own thread (Progress&lt;T&gt; would post to a context the bake thread doesn't have)</summary>
    sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    // ================= plan =================

    internal sealed class SolvePlan
    {
        public SolverJob Job;
        public List<MappingPlan> Mappings = [];
    }

    /// <summary>One mapping's part of the job: its samples, and its lights by what the mapping does with them</summary>
    internal sealed class MappingPlan
    {
        public Guid Guid;
        public bool Texture;
        /// <summary>Texture mappings: the mapping's size. Vertex mappings: the vertex count, and 1</summary>
        public int SizeX, SizeY;
        /// <summary>Per texel or vertex: its sample (an index into the job's samples), or -1 off the surface</summary>
        public int[] Samples;
        /// <summary>Lights into the lightmap (indices into the solver's lights), and their Lightmass GUIDs</summary>
        public List<(int Light, Guid Guid)> LightmapLights = [];
        /// <summary>
        /// Lights with visibility shadow maps, in the order of the receivers' <see cref="SolverReceiver.ShadowMapLights"/>; the receivers' list continues
        /// with <see cref="DistanceFieldLights"/>
        /// </summary>
        public List<(int Light, Guid Guid)> ShadowMapLights = [];
        /// <summary>Lights with distance field shadow maps</summary>
        public List<(int Light, Guid Guid)> DistanceFieldLights = [];
        /// <summary>Lit from both sides, per sample from <see cref="FirstSample"/> on</summary>
        public int FirstSample;
        public bool[] SampleTwoSided;

        public bool IsTwoSided(int sample) => SampleTwoSided != null && sample >= FirstSample && sample - FirstSample < SampleTwoSided.Length && SampleTwoSided[sample - FirstSample];

        // Distance fields: the mesh and its lightmap texel coordinates, for the high resolution pass
        internal LightmassGeoMesh Mesh;
        internal Vector2[] Texels;
        public int Upsample = 1;
        public float HighTexelWorldSize;
        /// <summary>Per texel: its block of Upsample² high resolution texels, or -1 where it isn't refined (<see cref="LightmassDistanceField.Build"/>)</summary>
        public int[] Blocks;
        public bool[] HighCovered;
        public Vector3[] HighPositions;
        /// <summary>Per distance field light, per high resolution texel of the blocks: visibility (NaN off the surface)</summary>
        public float[][] HighVisibility;
    }

    /// <summary>A mapping's samples before they're numbered in the job</summary>
    sealed class MappingWork
    {
        public MappingPlan Plan;
        public bool ShadowOnly;
        public List<SolverSample> Samples = [];
        public List<bool> SampleTwoSided = [];
        public int[] LocalSamples;
    }

    /// <summary>The solver's scene, which every batch shares, and what planning a mapping's samples needs</summary>
    internal sealed class SceneState
    {
        public SolverScene Scene;
        public Dictionary<LightmassMapping, LightmassGeoMesh> Geometry;
        public Dictionary<LightmassLight, int> LightIndex;
    }

    /// <summary>Plans every mapping in one job (a small scene, or tests)</summary>
    internal static SolvePlan Build(LightmassScene scene, SolverSettings settings, LightmassLocalShadowSettings shadows, CancellationToken cancel = default)
    {
        var state = BuildScene(scene, cancel);
        return BuildJob(state, scene.Mappings.ToList(), settings, shadows, cancel);
    }

    internal static SceneState BuildScene(LightmassScene scene, CancellationToken cancel = default)
    {
        var solverScene = new SolverScene
        {
            Environment = scene.World.EnvironmentColor * scene.World.EnvironmentIntensity,
            ImportanceVolumes = scene.ImportanceVolumes.ToList(),
        };
        var lightIndex = new Dictionary<LightmassLight, int>();
        foreach (var light in scene.Lights)
        {
            lightIndex[light] = solverScene.Lights.Count;
            var converted = LightmassSolverLights.Convert(light);
            if (System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',').FirstOrDefault(t => t.StartsWith("noshadow:", StringComparison.OrdinalIgnoreCase)) is { } ns
                && LightName(light).Contains(ns[9..], StringComparison.OrdinalIgnoreCase))
                converted.CastShadows = false;
            // Tuning: LEX_SOLVER_DEBUG=nobounce:A|B stops the lights whose name (Actor.Component) ends with A or B from bouncing
            if (System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',').FirstOrDefault(t => t.StartsWith("nobounce:", StringComparison.OrdinalIgnoreCase)) is { } nb
                && nb[9..].Split('|').Any(n => LightName(light).EndsWith(n, StringComparison.OrdinalIgnoreCase)))
                converted.Bounces = false;
            // Tuning: LEX_SOLVER_DEBUG=bouncelevel:Package lets only that package's lights bounce
            if (System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',').FirstOrDefault(t => t.StartsWith("bouncelevel:", StringComparison.OrdinalIgnoreCase)) is { } bl
                && !string.Equals(light.Light.Export?.FileRef.FileNameNoExtension, bl[12..], StringComparison.OrdinalIgnoreCase))
                converted.Bounces = false;
            solverScene.Lights.Add(converted);
        }

        // Instance IDs: one per static mesh component (all its LODs), BSP mapping and terrain mapping
        var componentIds = new Dictionary<object, uint>(ReferenceEqualityComparer.Instance);
        uint nextId = 0;
        uint IdOf(object key) => componentIds.TryGetValue(key, out uint id) ? id : componentIds[key] = nextId++;

        // Geometry
        var keys = scene.Instances.Cast<LightmassMapping>().Concat(scene.BspMappings).Concat(scene.TerrainMappings).ToArray();
        var ids = keys.Select(m => IdOf(m is LightmassInstance i ? i.Component : m)).ToArray();
        var built = new LightmassGeoMesh[keys.Length];
        Parallel.For(0, keys.Length, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            built[i] = keys[i] switch
            {
                LightmassInstance instance => LightmassGeoMesh.FromInstance(instance, ids[i]),
                LightmassBspMapping bsp => LightmassGeoMesh.FromBsp(bsp, ids[i]),
                LightmassTerrainMapping terrain => LightmassGeoMesh.FromTerrain(terrain, ids[i]),
                _ => null,
            };
        });
        var geometry = new Dictionary<LightmassMapping, LightmassGeoMesh>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < keys.Length; i++) geometry[keys[i]] = built[i];
        DebugInstanceNames.Clear();
        for (int i = 0; i < keys.Length; i++)
            DebugInstanceNames[ids[i]] = keys[i] switch
            {
                LightmassInstance li => $"{li.Component.FileRef.FileNameNoExtension}: {li.Component.InstancedFullPath}",
                LightmassTerrainMapping tm => $"{tm.Component.FileRef.FileNameNoExtension}: {tm.Component.InstancedFullPath}",
                _ => $"mapping {keys[i].Guid}",
            };

        // Materials: one per material and the element's emissive (Lightmass only gathers emissive with bUseEmissiveForStaticLighting)
        var materials = new Dictionary<(LightmassMaterial, float), uint>();
        var textures = new Dictionary<MaterialSamples, SolverTexture>(ReferenceEqualityComparer.Instance);
        uint MaterialOf(LightmassGeoElement element)
        {
            bool allEmissive = System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',').Contains("emissive", StringComparer.OrdinalIgnoreCase) == true;
            float emissive = element.Settings.UseEmissiveForStaticLighting || allEmissive ? element.Material.EmissiveBoost * element.Settings.EmissiveBoost : 0;
            if (materials.TryGetValue((element.Material, emissive), out uint index)) return index;
            solverScene.Materials.Add(ConvertMaterial(element.Material, emissive, textures));
            return materials[(element.Material, emissive)] = (uint)(solverScene.Materials.Count - 1);
        }
        // LODs other than 0 only shadow themselves in Lightmass; their samples are shadowed by LOD 0 here
        for (int i = 0; i < keys.Length; i++)
            if (built[i] != null && keys[i] is not LightmassInstance { LodIndex: > 0 })
                built[i].AddTo(solverScene, MaterialOf);
        cancel.ThrowIfCancellationRequested();
        return new SceneState { Scene = solverScene, Geometry = geometry, LightIndex = lightIndex };
    }

    /// <summary>A job for some of the scene's mappings: their samples, receivers and grids</summary>
    internal static SolvePlan BuildJob(SceneState state, IReadOnlyList<LightmassMapping> mappings, SolverSettings settings, LightmassLocalShadowSettings shadows,
        CancellationToken cancel = default)
    {
        var geometry = state.Geometry;
        var lightIndex = state.LightIndex;
        var solverScene = state.Scene;
        var work = new MappingWork[mappings.Count];
        Parallel.For(0, mappings.Count, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            var mapping = mappings[i];
            if (geometry.GetValueOrDefault(mapping) is not { } mesh) return;
            work[i] = Plan(mapping, mesh, lightIndex, shadows);
        });

        var job = new SolverJob { Scene = solverScene, Settings = settings };
        var grids = new List<SolverSampleGrid>();
        var plan = new SolvePlan { Job = job };
        foreach (var w in work)
        {
            if (w == null) continue;
            var p = w.Plan;
            var receivers = new Dictionary<bool, int>();
            int Receiver(bool twoSided)
            {
                if (receivers.TryGetValue(twoSided, out int r)) return r;
                job.Receivers.Add(new SolverReceiver
                {
                    LightmapLights = p.LightmapLights.Select(l => l.Light).ToArray(),
                    ShadowMapLights = p.ShadowMapLights.Concat(p.DistanceFieldLights).Select(l => l.Light).ToArray(),
                    TwoSidedLighting = twoSided,
                    GatherIndirect = !w.ShadowOnly,
                });
                var grid = new SolverSampleGrid { Width = p.SizeX, Height = p.SizeY, Samples = new int[p.SizeX * p.SizeY] };
                Array.Fill(grid.Samples, -1);
                grids.Add(grid);
                return receivers[twoSided] = job.Receivers.Count - 1;
            }

            p.FirstSample = job.Samples.Count;
            p.SampleTwoSided = w.SampleTwoSided.ToArray();
            for (int s = 0; s < w.Samples.Count; s++)
            {
                var sample = w.Samples[s];
                sample.Receiver = (uint)Receiver(w.SampleTwoSided[s]);
                job.Samples.Add(sample);
            }
            p.Samples = w.LocalSamples.Select(l => l < 0 ? -1 : p.FirstSample + l).ToArray();
            for (int t = 0; t < p.Samples.Length; t++)
                if (p.Samples[t] >= 0) grids[(int)job.Samples[p.Samples[t]].Receiver].Samples[t] = p.Samples[t];
            plan.Mappings.Add(p);
        }
        job.Grids = grids.ToArray();
        return plan;
    }

    static SolverMaterial ConvertMaterial(LightmassMaterial material, float emissiveScale, Dictionary<MaterialSamples, SolverTexture> textures)
    {
        SolverTexture Texture(MaterialSamples samples, float scale = 1)
        {
            if (samples == null || samples.Size <= 0) return null;
            if (scale == 1 && textures.TryGetValue(samples, out var cached)) return cached;
            var texture = ToTexture(samples, scale);
            if (scale == 1) textures[samples] = texture;
            return texture;
        }
        return new SolverMaterial
        {
            Diffuse = DebugAlbedoScale == 1 ? Texture(material.Diffuse) : Texture(material.Diffuse, DebugAlbedoScale),
            Emissive = emissiveScale > 0 ? Texture(material.Emissive, emissiveScale) : null,
            Transmission = Texture(material.Transmission),
            // Masked, soft masked, and translucent cast as masked: the transmission samples hold opacity, which clips
            Masked = material.BlendMode is 1 or 6 || material.BlendMode is 2 or 7 && material.CastShadowAsMasked,
            OpacityMaskClipValue = material.OpacityMaskClipValue,
            TwoSided = material.TwoSided,
        };
    }

    /// <summary>A material's FFloat16Color samples as the solver's linear RGBA texels, times <paramref name="scale"/> (RGB only)</summary>
    public static SolverTexture ToTexture(MaterialSamples samples, float scale = 1)
    {
        int n = samples.Size * samples.Size;
        var texels = new Vector4[n];
        for (int i = 0; i < n && i * 4 + 3 < samples.Rgba.Length; i++)
            texels[i] = new Vector4((float)samples.Rgba[i * 4] * scale, (float)samples.Rgba[i * 4 + 1] * scale, (float)samples.Rgba[i * 4 + 2] * scale, (float)samples.Rgba[i * 4 + 3]);
        return new SolverTexture { Size = samples.Size, Texels = texels };
    }

    static List<LightmassLight> RelevantLights(LightmassMapping mapping) => mapping switch
    {
        LightmassInstance i => i.RelevantLights,
        LightmassBspMapping b => b.RelevantLights,
        LightmassTerrainMapping t => t.RelevantLights,
        _ => [],
    };

    /// <summary>Tuning: LEX_SOLVER_DEBUG=light:Name keeps only lightmap lights whose name (Actor.Component) contains Name</summary>
    static readonly string DebugLight = System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',')
        .FirstOrDefault(t => t.StartsWith("light:", StringComparison.OrdinalIgnoreCase))?[6..];

    /// <summary>Tuning: LEX_SOLVER_DEBUG=droplight:A|B leaves out lightmap lights whose name (Actor.Component) ends with A or B</summary>
    static readonly string[] DebugDropLights = System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',')
        .FirstOrDefault(t => t.StartsWith("droplight:", StringComparison.OrdinalIgnoreCase))?[10..].Split('|', StringSplitOptions.RemoveEmptyEntries) ?? [];


    /// <summary>Tuning: LEX_SOLVER_DEBUG=albedo:X scales every material's albedo by X</summary>
    static readonly float DebugAlbedoScale = System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',')
        .FirstOrDefault(t => t.StartsWith("albedo:", StringComparison.OrdinalIgnoreCase)) is { } t ? float.Parse(t[7..], System.Globalization.CultureInfo.InvariantCulture) : 1;

    static string LightName(LightmassLight l) => $"{l.Light.Export?.Parent?.ObjectName.Instanced}.{l.Light.Export?.ObjectName.Instanced}";

    static MappingWork Plan(LightmassMapping mapping, LightmassGeoMesh mesh, Dictionary<LightmassLight, int> lightIndex, LightmassLocalShadowSettings shadows)
    {
        bool shadowOnly = mapping is LightmassInstance { ShadowTarget: not null };
        bool texture = mapping.IsTextureMapping;
        var plan = new MappingPlan { Guid = mapping.Guid, Texture = texture };
        foreach (var light in RelevantLights(mapping))
        {
            if (!lightIndex.TryGetValue(light, out int index)) continue;
            switch (LightmassSolverLights.Route(light, shadowOnly, texture, shadows.AllowDistanceFields))
            {
                case LightmassLightRoute.Lightmap:
                    if (DebugLight is { } only && !LightName(light).Contains(only, StringComparison.OrdinalIgnoreCase)) break;
                    if (DebugDropLights.Any(d => LightName(light).EndsWith(d, StringComparison.OrdinalIgnoreCase))) break;
                    plan.LightmapLights.Add((index, light.Guid));
                    break;
                case LightmassLightRoute.ShadowMap: plan.ShadowMapLights.Add((index, light.Guid)); break;
                case LightmassLightRoute.DistanceFieldShadowMap: plan.DistanceFieldLights.Add((index, light.Guid)); break;
            }
        }
        var work = new MappingWork { Plan = plan, ShadowOnly = shadowOnly };
        if (texture)
        {
            plan.SizeX = mapping.SizeX;
            plan.SizeY = mapping.SizeY;
            var texels = mesh.Vertices.Select(v => LightmassTexelRasterizer.UvToTexel(v.LightmapUV, mapping.SizeX, mapping.SizeY, mapping.Padded)).ToArray();
            work.LocalSamples = TextureSamples(mesh, texels, mapping.SizeX, mapping.SizeY, shadows.SmallestTexelRadius, work.Samples, work.SampleTwoSided);
            if (plan.DistanceFieldLights.Count > 0 && work.Samples.Count > 0)
            {
                // The texels' typical world size: the side of a square of the samples' mean radius as its half diagonal
                float texelWorldSize = work.Samples.Average(s => s.TexelRadius) * MathF.Sqrt(2);
                plan.Upsample = LightmassDistanceField.UpsampleFactor(texelWorldSize, shadows.MaxTransitionDistance, shadows.HighResTexelsPerTransition,
                    shadows.MinUpsampleFactor, mapping.SizeX, mapping.SizeY);
                plan.HighTexelWorldSize = texelWorldSize / plan.Upsample;
                plan.Mesh = mesh;
                plan.Texels = texels;
            }
        }
        else
        {
            plan.SizeX = mapping.Cost;
            plan.SizeY = 1;
            work.LocalSamples = VertexSamples(mesh, mapping.Cost, shadows.SmallestTexelRadius, work.Samples, work.SampleTwoSided);
        }
        return work;
    }

    /// <summary>
    /// A sample per covered texel, at the point the rasteriser chose: the interpolated position and shading normal, the tangent frame orthonormalised
    /// around the normal, the triangle's normal turned to the shading normal's side, and the texel's radius from the triangle's UV-to-world scale.
    /// </summary>
    /// <param name="texels">Per vertex: its position in the grid's texel units</param>
    /// <param name="only">Optional: only these texels are sampled</param>
    internal static int[] TextureSamples(LightmassGeoMesh mesh, Vector2[] texels, int width, int height, float smallestRadius,
        List<SolverSample> samples, List<bool> twoSided, Func<int, bool> only = null, int supersample = 4)
    {
        // Triangles in no element (and so not in the scene) aren't sampled either
        var indices = new List<int>(mesh.Indices.Length);
        var triangleOf = new List<int>(mesh.TriangleCount);
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            if (mesh.TriangleElements[t] < 0 || mesh.TriangleElements[t] >= mesh.Elements.Count) continue;
            indices.Add(mesh.Indices[t * 3]); indices.Add(mesh.Indices[t * 3 + 1]); indices.Add(mesh.Indices[t * 3 + 2]);
            triangleOf.Add(t);
        }
        var raster = LightmassTexelRasterizer.Rasterize(width, height, texels, indices, supersample);
        var local = new int[width * height];
        Array.Fill(local, -1);
        var radii = new Dictionary<int, float>();
        for (int i = 0; i < local.Length; i++)
        {
            if (!raster.IsCovered(i) || only != null && !only(i)) continue;
            int t = triangleOf[raster.Triangle[i]];
            if (!radii.TryGetValue(t, out float radius)) radii[t] = radius = MathF.Max(smallestRadius, TexelRadius(mesh, texels, t));
            var sample = Interpolate(mesh, t, raster.Barycentric[i]);
            sample.TexelRadius = radius;
            local[i] = samples.Count;
            samples.Add(sample);
            twoSided.Add(mesh.Elements[mesh.TriangleElements[t]].TwoSidedLighting);
        }
        return local;
    }

    /// <summary>World size of a texel on the triangle: half the diagonal of the parallelogram one texel step along U and V spans</summary>
    internal static float TexelRadius(LightmassGeoMesh mesh, Vector2[] texels, int t)
    {
        int a = mesh.Indices[t * 3], b = mesh.Indices[t * 3 + 1], c = mesh.Indices[t * 3 + 2];
        Vector2 e1 = texels[b] - texels[a], e2 = texels[c] - texels[a];
        Vector3 p1 = mesh.Vertices[b].Position - mesh.Vertices[a].Position, p2 = mesh.Vertices[c].Position - mesh.Vertices[a].Position;
        float det = e1.X * e2.Y - e1.Y * e2.X;
        if (MathF.Abs(det) < 1e-12f) return 0;
        // [dP/du dP/dv] = [p1 p2] · inverse([e1 e2])
        var dPdu = (p1 * e2.Y - p2 * e1.Y) / det;
        var dPdv = (p2 * e1.X - p1 * e2.X) / det;
        float r = 0.5f * MathF.Max((dPdu + dPdv).Length(), (dPdu - dPdv).Length());
        return float.IsFinite(r) ? r : 0;
    }

    static SolverSample Interpolate(LightmassGeoMesh mesh, int t, Vector3 bary)
    {
        var a = mesh.Vertices[mesh.Indices[t * 3]];
        var b = mesh.Vertices[mesh.Indices[t * 3 + 1]];
        var c = mesh.Vertices[mesh.Indices[t * 3 + 2]];
        var position = a.Position * bary.X + b.Position * bary.Y + c.Position * bary.Z;
        var face = mesh.FaceNormal(t);
        var normal = LightmassGeoMesh.SafeNormalize(a.TangentZ * bary.X + b.TangentZ * bary.Y + c.TangentZ * bary.Z);
        if (normal == Vector3.Zero) normal = face == Vector3.Zero ? Vector3.UnitZ : face;
        if (face == Vector3.Zero) face = normal;
        else if (Vector3.Dot(face, normal) < 0) face = -face;
        var (tangent, bitangent) = Frame(normal, a.TangentX * bary.X + b.TangentX * bary.Y + c.TangentX * bary.Z,
            a.TangentY * bary.X + b.TangentY * bary.Y + c.TangentY * bary.Z);
        return new SolverSample
        {
            Position = position, Normal = normal, GeometricNormal = face, Tangent = tangent, Bitangent = bitangent, Instance = mesh.Instance,
        };
    }

    /// <summary>A unit tangent frame around the normal, from the interpolated tangents (Gram-Schmidt; the bitangent keeps its handedness)</summary>
    internal static (Vector3 Tangent, Vector3 Bitangent) Frame(Vector3 normal, Vector3 tangentX, Vector3 tangentY)
    {
        var t = tangentX - normal * Vector3.Dot(normal, tangentX);
        if (t.LengthSquared() < 1e-12f)
        {
            // No usable tangent: build one from the bitangent, or any perpendicular
            var fromY = Vector3.Cross(tangentY, normal);
            t = fromY.LengthSquared() > 1e-12f ? fromY
                : MathF.Abs(normal.X) < 0.9f ? Vector3.Cross(normal, Vector3.UnitX) : Vector3.Cross(normal, Vector3.UnitY);
        }
        t = Vector3.Normalize(t);
        var b = Vector3.Cross(normal, t);
        if (Vector3.Dot(b, tangentY) < 0) b = -b;
        return (t, b);
    }

    /// <summary>A sample per vertex (Lightmass's vertex mappings light each vertex), its geometric normal the area-weighted normal of its triangles</summary>
    internal static int[] VertexSamples(LightmassGeoMesh mesh, int count, float smallestRadius, List<SolverSample> samples, List<bool> twoSided)
    {
        int n = mesh.Vertices.Length;
        var faceSum = new Vector3[n];
        var area = new float[n];
        var sided = new bool[n];
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            int e = mesh.TriangleElements[t];
            if (e < 0 || e >= mesh.Elements.Count) continue;
            int a = mesh.Indices[t * 3], b = mesh.Indices[t * 3 + 1], c = mesh.Indices[t * 3 + 2];
            var cross = Vector3.Cross(mesh.Vertices[b].Position - mesh.Vertices[a].Position, mesh.Vertices[c].Position - mesh.Vertices[a].Position);
            float triangleArea = cross.Length() / 2;
            foreach (int v in (ReadOnlySpan<int>)[a, b, c])
            {
                var oriented = Vector3.Dot(cross, mesh.Vertices[v].TangentZ) < 0 ? -cross : cross;
                faceSum[v] += oriented;
                area[v] += triangleArea / 3;
                sided[v] |= mesh.Elements[e].TwoSidedLighting;
            }
        }
        var local = new int[count];
        for (int v = 0; v < count; v++)
        {
            if (n == 0) { local[v] = -1; continue; }
            // A vertex past the buffers' end (NumVertices larger than they are) shares the last one's sample
            if (v >= n) { local[v] = local[n - 1]; continue; }
            var vertex = mesh.Vertices[v];
            var normal = vertex.TangentZ == Vector3.Zero ? LightmassGeoMesh.SafeNormalize(faceSum[v]) : vertex.TangentZ;
            if (normal == Vector3.Zero) normal = Vector3.UnitZ;
            var geometric = LightmassGeoMesh.SafeNormalize(faceSum[v]);
            if (geometric == Vector3.Zero) geometric = normal;
            var (tangent, bitangent) = Frame(normal, vertex.TangentX, vertex.TangentY);
            local[v] = samples.Count;
            samples.Add(new SolverSample
            {
                Position = vertex.Position, Normal = normal, GeometricNormal = geometric, Tangent = tangent, Bitangent = bitangent,
                TexelRadius = MathF.Max(smallestRadius, MathF.Sqrt(area[v] / MathF.PI)), Instance = mesh.Instance,
            });
            twoSided.Add(sided[v]);
        }
        return local;
    }

    // ================= distance fields =================

    /// <summary>The most samples one high resolution solve gets, to bound memory; more go to further solves</summary>
    const int HighResBatchSamples = 4 << 20;

    /// <summary>Texels per side of the tiles the high resolution grid is rasterised in, so a large grid is never held whole</summary>
    const int TileTexels = 32;

    /// <summary>
    /// Lightmass's high resolution pass for distance field shadow maps, done only where it matters: the texels within MaxTransitionDistanceWorldSpace of a
    /// shadow edge found at the shadow map's own resolution (<see cref="LightmassDistanceField.RefinedTexels"/>) get Upsample² samples each, solved for
    /// visibility alone (no lightmap lights, no bounces) in batches.
    /// </summary>
    static void RefineDistanceFields(SolvePlan plan, SolverOutput output, LightmassLocalShadowSettings shadows, SolverSession session,
        IProgress<SolverProgress> progress, Action<string> log, CancellationToken cancel)
    {
        var main = plan.Job;
        var settings = CopySettings(main.Settings);
        settings.Bounces = 0;
        settings.AmbientOcclusion = false;
        settings.FilterIndirect = false;
        var batch = new SolverJob { Scene = main.Scene, Settings = settings, Grids = null };
        var pending = new List<(MappingPlan Plan, int Cell, int Sample)>();
        long total = 0;
        int batches = 0;

        void Flush()
        {
            if (batch.Samples.Count == 0) return;
            var result = LightSolver.Solve(batch, session, progress, cancel);
            foreach (var (p, cell, sample) in pending)
                for (int j = 0; j < p.DistanceFieldLights.Count; j++)
                    p.HighVisibility[j][cell] = result.Visibility[result.VisibilityOffsets[sample] + j];
            total += batch.Samples.Count;
            batches++;
            batch = new SolverJob { Scene = main.Scene, Settings = settings, Grids = null };
            pending.Clear();
        }

        foreach (var p in plan.Mappings)
        {
            cancel.ThrowIfCancellationRequested();
            if (p.DistanceFieldLights.Count == 0 || p.Mesh == null) continue;
            int u = p.Upsample, count = p.SizeX * p.SizeY;
            var covered = p.Samples.Select(s => s >= 0).ToArray();
            int radius = Math.Clamp((int)MathF.Ceiling(shadows.MaxTransitionDistance / MathF.Max(1e-3f, p.HighTexelWorldSize * u)) + 1, 1, 64);
            var refined = new bool[count];
            for (int j = 0; j < p.DistanceFieldLights.Count; j++)
            {
                var visibility = LowVisibility(p, main, output, p.ShadowMapLights.Count + j, p.DistanceFieldLights[j].Light);
                var r = LightmassDistanceField.RefinedTexels(p.SizeX, p.SizeY, covered, visibility, radius);
                for (int i = 0; i < count; i++) refined[i] |= r[i];
            }
            p.Blocks = new int[count];
            Array.Fill(p.Blocks, -1);
            int blocks = 0;
            for (int i = 0; i < count; i++) if (refined[i]) p.Blocks[i] = blocks++;
            // A mapping with so many texels near edges that its samples would fill more than two batches gets a lower upsample factor
            if ((long)blocks * u * u > 2L * HighResBatchSamples)
            {
                float texelWorldSize = p.HighTexelWorldSize * u;
                while (u > 1 && (long)blocks * u * u > 2L * HighResBatchSamples) u--;
                p.Upsample = u;
                p.HighTexelWorldSize = texelWorldSize / u;
            }
            int cells = u * u;
            p.HighCovered = new bool[blocks * cells];
            p.HighPositions = new Vector3[blocks * cells];
            p.HighVisibility = p.DistanceFieldLights.Select(_ => Enumerable.Repeat(float.NaN, blocks * cells).ToArray()).ToArray();
            if (blocks == 0) { p.Mesh = null; p.Texels = null; continue; }

            // Rasterise the high resolution grid tile by tile, sampling only the refined texels' blocks
            var receivers = new Dictionary<bool, int>();
            for (int ty = 0; ty < p.SizeY; ty += TileTexels)
            for (int tx = 0; tx < p.SizeX; tx += TileTexels)
            {
                int tw = Math.Min(TileTexels, p.SizeX - tx), th = Math.Min(TileTexels, p.SizeY - ty);
                bool any = false;
                for (int y = ty; y < ty + th && !any; y++)
                for (int x = tx; x < tx + tw && !any; x++) any = refined[y * p.SizeX + x];
                if (!any) continue;
                var origin = new Vector2(tx, ty) * u;
                var texels = p.Texels.Select(t => t * u - origin).ToArray();
                var samples = new List<SolverSample>();
                var twoSided = new List<bool>();
                int hw = tw * u;
                bool Refined(int i) => refined[(ty + i / hw / u) * p.SizeX + tx + i % hw / u];
                var local = TextureSamples(p.Mesh, texels, hw, th * u, shadows.SmallestTexelRadius / u, samples, twoSided, Refined, supersample: 2);
                for (int i = 0; i < local.Length; i++)
                {
                    if (local[i] < 0) continue;
                    int hx = i % hw, hy = i / hw;
                    int texel = (ty + hy / u) * p.SizeX + tx + hx / u;
                    int cell = p.Blocks[texel] * cells + hy % u * u + hx % u;
                    var sample = samples[local[i]];
                    p.HighCovered[cell] = true;
                    p.HighPositions[cell] = sample.Position;
                    bool sided = twoSided[local[i]];
                    if (!receivers.TryGetValue(sided, out int receiver))
                    {
                        batch.Receivers.Add(new SolverReceiver
                        {
                            ShadowMapLights = p.DistanceFieldLights.Select(l => l.Light).ToArray(), TwoSidedLighting = sided, GatherIndirect = false,
                        });
                        receivers[sided] = receiver = batch.Receivers.Count - 1;
                    }
                    sample.Receiver = (uint)receiver;
                    pending.Add((p, cell, batch.Samples.Count));
                    batch.Samples.Add(sample);
                }
            }
            p.Mesh = null;
            p.Texels = null;
            // A mapping's samples stay in one batch (its receivers are the batch's)
            if (batch.Samples.Count >= HighResBatchSamples) Flush();
        }
        Flush();
        log($"Distance field shadow maps: {total:N0} high resolution samples in {batches} {(batches == 1 ? "solve" : "solves")}");
    }

    /// <summary>A light's visibility at a mapping's texels (0 off the surface or where the light can't reach)</summary>
    static float[] LowVisibility(MappingPlan p, SolverJob job, SolverOutput output, int k, int light)
    {
        var visibility = new float[p.SizeX * p.SizeY];
        for (int i = 0; i < visibility.Length; i++)
        {
            int s = p.Samples[i];
            if (s < 0) continue;
            var sample = job.Samples[s];
            visibility[i] = LightmassSolverLights.Reaches(job.Scene.Lights[light], sample.Position, sample.Normal, false, facingMatters: false)
                ? Math.Clamp(output.Visibility[output.VisibilityOffsets[s] + k], 0, 1) : 0;
        }
        return visibility;
    }

    static SolverSettings CopySettings(SolverSettings s) => new()
    {
        Device = s.Device, Profile = s.Profile, ShadowRays = s.ShadowRays, PenumbraShadowRays = s.PenumbraShadowRays, GatherRays = s.GatherRays,
        BounceGatherRays = s.BounceGatherRays, Bounces = s.Bounces, DirectionalCoefficientScale = s.DirectionalCoefficientScale,
        DirectionalCoefficientFalloffPower = s.DirectionalCoefficientFalloffPower, RayOffset = s.RayOffset, NormalOffset = s.NormalOffset, NormalOffsetTexelScale = s.NormalOffsetTexelScale, LightFacingShadows = s.LightFacingShadows,
        IndirectNormalInfluenceBoost = s.IndirectNormalInfluenceBoost, AmbientOcclusion = s.AmbientOcclusion, DirectOcclusionFraction = s.DirectOcclusionFraction,
        IndirectOcclusionFraction = s.IndirectOcclusionFraction, OcclusionExponent = s.OcclusionExponent, FullyOccludedSamplesFraction = s.FullyOccludedSamplesFraction,
        MaxOcclusionDistance = s.MaxOcclusionDistance, FilterIndirect = s.FilterIndirect, RecordRadiusScale = s.RecordRadiusScale, MinRecordRadius = s.MinRecordRadius,
        MaxRecordRadius = s.MaxRecordRadius, DistanceSmoothFactor = s.DistanceSmoothFactor, Seed = s.Seed, Photons = s.Photons,
    };

    // ================= output =================

    /// <summary>
    /// A mapping's result from the solver's output, in the form <see cref="LightmassResults"/> reads Lightmass's:
    /// <list type="bullet">
    /// <item>Directional and simple coefficients per texel or vertex. The directional ones go through Lightmass's own storage (a normalised colour and a
    /// maximum per basis direction, <see cref="Directional.Split"/>), which loses the per-direction tint as Lightmass's does.</item>
    /// <item>Coverage 255 on the surface, 0 off it (vertex mappings: all 255).</item>
    /// <item>LightGuids: the lightmap lights that reach at least one of its samples (ignoring occlusion).</item>
    /// <item>Visibility shadow maps gamma 2.2 encoded into bytes (trunc(pow(v, 1/2.2)·255)), 0 where the light can't reach; vertex ones as floats.</item>
    /// <item>Distance field shadow maps from the high resolution visibility (<see cref="LightmassDistanceField"/>).</item>
    /// </list>
    /// A shadow map with no lit texel is left out, as Lightmass leaves out a light that's completely occluded.
    /// </summary>
    internal static LightmassMappingResult Convert(MappingPlan plan, SolverJob job, SolverOutput output, LightmassLocalShadowSettings shadows)
    {
        int count = plan.SizeX * plan.SizeY;
        var result = new LightmassMappingResult
        {
            Guid = plan.Guid, IsTextureMapping = plan.Texture, SizeX = plan.SizeX, SizeY = plan.SizeY,
            Directional = new Directional.Stored[count], Simple = new HalfVector3[count], Coverage = new byte[count],
        };
        for (int i = 0; i < count; i++)
        {
            int s = plan.Samples[i];
            if (s < 0) { if (!plan.Texture) result.Coverage[i] = 255; continue; }
            var (colour, max) = Directional.Split(new Directional.Sample(output.Directional0[s], output.Directional1[s], output.Directional2[s]));
            result.Directional[i] = new Directional.Stored(colour, max);
            result.Simple[i] = output.Simple[s];
            result.Coverage[i] = 255;
        }

        var lights = job.Scene.Lights;
        bool Reaches(int light, int sample, bool facing) =>
            LightmassSolverLights.Reaches(lights[light], job.Samples[sample].Position, job.Samples[sample].Normal, plan.IsTwoSided(sample), facing);
        result.LightGuids = plan.LightmapLights.Where(l => plan.Samples.Any(s => s >= 0 && Reaches(l.Light, s, true))).Select(l => l.Guid).Distinct().ToArray();

        for (int k = 0; k < plan.ShadowMapLights.Count; k++)
        {
            var (light, guid) = plan.ShadowMapLights[k];
            float Visibility(int s) => Reaches(light, s, false) ? Math.Clamp(output.Visibility[output.VisibilityOffsets[s] + k], 0, 1) : 0;
            if (plan.Texture)
            {
                var shadow = new LightmassShadowMap { LightGuid = guid, Values = new byte[count], Coverage = new byte[count] };
                bool any = false;
                for (int i = 0; i < count; i++)
                {
                    int s = plan.Samples[i];
                    if (s < 0) continue;
                    float v = Visibility(s);
                    shadow.Values[i] = EncodeVisibility(v);
                    shadow.Coverage[i] = (byte)(v > 0 ? 255 : 0);
                    any |= v > 0;
                }
                if (any) result.ShadowMaps.Add(shadow);
            }
            else
            {
                var shadow = new LightmassShadowMap { LightGuid = guid, Samples = new float[count] };
                for (int i = 0; i < count; i++) shadow.Samples[i] = plan.Samples[i] < 0 ? 0 : Visibility(plan.Samples[i]);
                if (shadow.Samples.Any(v => v > 0)) result.ShadowMaps.Add(shadow);
            }
        }

        if (plan.Texture && plan.DistanceFieldLights.Count > 0)
        {
            var covered = new bool[count];
            var positions = new Vector3[count];
            for (int i = 0; i < count; i++)
                if (plan.Samples[i] >= 0) { covered[i] = true; positions[i] = job.Samples[plan.Samples[i]].Position; }
            var blocks = plan.Blocks ?? Enumerable.Repeat(-1, count).ToArray();
            var highCovered = plan.HighCovered ?? [];
            var highPositions = plan.HighPositions ?? [];
            for (int j = 0; j < plan.DistanceFieldLights.Count; j++)
            {
                var (light, guid) = plan.DistanceFieldLights[j];
                var low = LowVisibility(plan, job, output, plan.ShadowMapLights.Count + j, light);
                var lit = low.Select(v => v >= 0.5f).ToArray();
                var highLit = new bool[highCovered.Length];
                var highVisibility = plan.HighVisibility?[j];
                for (int c = 0; c < highLit.Length; c++)
                    highLit[c] = highCovered[c] && highVisibility != null && highVisibility[c] >= 0.5f
                                 && LightmassSolverLights.Reaches(lights[light], highPositions[c], Vector3.UnitZ, true, facingMatters: false);
                var (values, coverage) = LightmassDistanceField.Build(plan.SizeX, plan.SizeY, plan.Upsample, covered, lit, positions, blocks, highCovered, highLit,
                    highPositions, shadows.MaxTransitionDistance, plan.HighTexelWorldSize);
                if (coverage.Any(c => c != 0))
                    result.ShadowMaps.Add(new LightmassShadowMap { LightGuid = guid, SignedDistanceField = true, Values = values, Coverage = coverage });
            }
        }
        return result;
    }

    /// <summary>A visibility as ShadowMapTexture2D stores it, quantized as Lightmass does (FQuantizedShadowSampleData): trunc(pow(v, 1/2.2) · 255)</summary>
    public static byte EncodeVisibility(float visibility) => (byte)Math.Clamp((int)(MathF.Pow(Math.Clamp(visibility, 0, 1), 1 / 2.2f) * 255f), 0, 255);
}
