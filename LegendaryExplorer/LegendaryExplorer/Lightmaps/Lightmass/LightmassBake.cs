using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LegendaryExplorer.Lightmaps.Codec;
using LegendaryExplorer.Lightmaps.Game;
using LegendaryExplorer.Lightmaps.Solver;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorer.Lightmaps.Lightmass;

public sealed class LightmassBakeSettings
{
    /// <summary>UDK's Binaries folder (SwarmAgent.exe, Win64\UnrealLightmass.exe, Win64\AgentInterface.dll). Only the UDK Lightmass solver needs it.</summary>
    public string UdkBinaries;
    /// <summary>LEX's solver on the GPU or CPU, or UDK's Lightmass through Swarm</summary>
    public LightmassSolverChoice Solver = LightmassSolverChoice.Gpu;
    public LightmassQuality Quality = LightmassQuality.Preview;
    /// <summary>
    /// Applies <see cref="LightmassIni.ApplyBioWareOverrides"/> over Lightmass's settings (UDK's BaseLightmass.ini for UDK Lightmass, LEX's copy of its
    /// defaults, <see cref="LightmassDefaultIni"/>, for LEX's solver).
    /// </summary>
    public bool BioWareLightmassSettings = true;
    /// <summary>
    /// Fork: turns on bUsePrecomputedShadows on every static mesh placed in the levels being baked before the bake, so each of them is given baked lighting
    /// (written to the TFC): without static shadowing a mesh isn't part of the bake (UStaticMeshComponent::GetStaticLightingInfo). Saved with the bake
    /// </summary>
    public bool UsePrecomputedShadowsOnAllMeshes = true;
    public LightmassSceneOptions Scene = new();
    /// <summary>Where the mod TFC for streamed lightmap textures is written, and its name (Textures_DLC_MOD_..., for the game to find it).</summary>
    public string TfcDirectory;
    /// <summary>Original path of the cache copied into TfcDirectory for a preview.</summary>
    internal string CopiedCacheSource;
    public string TfcName = "Textures_DLC_MOD_LEXLightmaps";
    /// <summary>
    /// Atlases get new allocations (AtlasPacker) instead of keeping the original layout. Not offered in the Lightmass Baker: it trashes and
    /// replaces the atlases, which is for testing the rewriter's write path.
    /// </summary>
    public bool Repack;
    /// <summary>
    /// How much larger static mesh and BSP texture lightmaps are made than their current allocations; null: the quality's
    /// (<see cref="DefaultLightmapScale"/>). Other than 1, the bake solves at the new sizes and writes new lightmap textures, trashing the old ones.
    /// </summary>
    public float? LightmapScale;
    /// <summary>The format of the colour lightmap textures (the ones LE3 stores as DXT1); null keeps each texture's</summary>
    public LightmapTextureFormat? TextureFormat;

    /// <summary>Preview keeps the allocations' sizes; Medium 1.5x, High 2x and Production 4x (each grown to at most 2048 texels)</summary>
    public static float DefaultLightmapScale(LightmassQuality quality) => quality switch
    {
        LightmassQuality.Medium => 1.5f,
        LightmassQuality.High => 2,
        LightmassQuality.Production => 4,
        _ => 1,
    };

    public float EffectiveLightmapScale => LightmapScale ?? DefaultLightmapScale(Quality);

    /// <summary>Whether the bake writes new lightmap textures (resized or repacked) instead of rewriting the levels' own in place</summary>
    public bool RepacksLightmaps => Repack || EffectiveLightmapScale != 1;

    /// <summary>The colour lightmaps' pixel format name, or null to keep each texture's</summary>
    public string ColourPixelFormat => TextureFormat switch
    {
        LightmapTextureFormat.DXT1 => "PF_DXT1",
        LightmapTextureFormat.ARGB => "PF_A8R8G8B8",
        LightmapTextureFormat.BC7 => "PF_BC7",
        _ => null,
    };
}

/// <summary>Lightmap texture formats a bake can write: DXT1 (the game's own, 4 bits a texel), uncompressed ARGB (32 bits), BC7 (8 bits, far less loss than DXT1)</summary>
public enum LightmapTextureFormat
{
    DXT1,
    ARGB,
    BC7,
}

public sealed class LightmassBakeResult
{
    public LightmassScene Scene;
    public Dictionary<Guid, LightmassMappingResult> Results = new();
    public List<string> Alerts = [];
    /// <summary>What solved the lighting, for the report: "Lightmass", or LEX's solver and its device</summary>
    public string SolverName = "Lightmass";
    /// <summary>Mappings Lightmass failed, and mappings it delivered no results for. They keep their original lighting.</summary>
    public int FailedTasks, MissingMappings;
    /// <summary>Whether every mapping was baked.</summary>
    public bool Complete => FailedTasks == 0 && MissingMappings == 0;
    public TimeSpan ExportTime, LightmassTime, ApplyTime;
    public Dictionary<IMEPackage, LightmapRewriter.Summary> Rewrites = new();
    public Dictionary<IMEPackage, LightmassShadowMapWriter.Summary> ShadowMaps = new();
    public Dictionary<IMEPackage, DecalReceiverLighting.Summary> Decals = new();
    public Dictionary<string, int> KeptOriginal = new();
    /// <summary>Levels that had marks of what's changed since their last bake, which the bake cleared: they're saved even if no lightmap of theirs changed</summary>
    public HashSet<IMEPackage> ClearedMarks = [];
    public string TfcPath;
    /// <summary>Where the rebaked files were saved (<see cref="LightmassBakeJob"/>).</summary>
    public List<string> SavedFiles = [];
}

/// <summary>
/// Bakes static lighting for a set of LE3 levels with UDK's Lightmass: builds the scene, writes the channels, runs a Swarm job with a
/// task per mapping (as FLightmassProcessor::Run does, Lightmass.cpp:3141), reads the results and rewrites the levels' lightmaps.
/// The packages are changed in memory; saving them (and shipping the TFC) is the caller's.
/// </summary>
public static class LightmassBake
{
    /// <summary>
    /// Fork: sets bUsePrecomputedShadows on every static mesh component (static, spline, InterpActor and collection meshes...) placed in a level of
    /// <paramref name="levels"/> that's baked (not one only read for context), so that the bake gives each of them static lighting
    /// </summary>
    static void UsePrecomputedShadowsOnAllMeshes(IReadOnlyList<IMEPackage> levels, ICollection<IMEPackage> contextOnly, Action<string> log)
    {
        int changed = 0, restored = 0;
        foreach (var level in levels)
        {
            if (contextOnly?.Contains(level) == true) continue;
            foreach (var component in level.Exports)
            {
                if (component.IsDefaultObject || !component.IsA("StaticMeshComponent") || !IsPlacedInLevel(component)) continue;
                var props = component.GetProperties();
                bool write = false;
                if (props.GetProp<BoolProperty>("bUsePrecomputedShadows") is not { Value: true })
                {
                    props.AddOrReplaceProp(new BoolProperty(true, "bUsePrecomputedShadows"));
                    changed++;
                    write = true;
                }
                if (BakedByScottinasBaker(props))
                {
                    // Scottina's Lightmass Baker turns lights off on the meshes it bakes (its lighting is all in the lightmap), and Lightmass leaves out a
                    // mesh that doesn't accept lights (UStaticMeshComponent::GetStaticLightingInfo): they're turned back on, as UE3 bakes have them
                    props.AddOrReplaceProp(new BoolProperty(true, "bAcceptsLights"));
                    props.AddOrReplaceProp(new BoolProperty(true, "bAcceptsDynamicLights"));
                    // Its list is of the lights it baked into the coefficients; this bake makes its own
                    props.RemoveNamedProperty("IrrelevantLights");
                    restored++;
                    write = true;
                }
                if (write) component.WriteProperties(props);
            }
        }
        if (changed > 0) log($"Use precomputed shadows turned on for {changed} static {(changed == 1 ? "mesh" : "meshes")}, so they're baked.");
        if (restored > 0) log($"Accepts lights turned back on for {restored} static {(restored == 1 ? "mesh" : "meshes")} that Scottina's Lightmass Baker had baked, so they're baked again.");

        // The properties Scottina's baker (StaticLightingWriter.ApplyStaticLightingProperties) gives a mesh it bakes
        static bool BakedByScottinasBaker(PropertyCollection props) =>
            props.GetProp<BoolProperty>("bAcceptsLights") is { Value: false }
            && props.GetProp<BoolProperty>("bAcceptsDynamicLights") is { Value: false }
            && props.GetProp<BoolProperty>("bForceDirectLightMap") is { Value: true }
            && props.GetProp<BoolProperty>("bBioForcePrecomputedShadows") is { Value: true };

        // A level actor's component (Level > actor > component), or a collection's member (Level > collection > component), not an archetype or template
        static bool IsPlacedInLevel(ExportEntry component)
        {
            IEntry owner = component.Parent;
            for (int depth = 0; depth < 3 && owner is ExportEntry outer; depth++, owner = outer.Parent)
            {
                if (outer.ClassName == "Level") return depth > 0;
            }
            return false;
        }
    }

    /// <summary>BioWare's LMT_3/4 colour against the directional look (LE3_Lightmaps.md §16.1, provisional).</summary>
    const float VectorBrightness = 1.16f;

    /// <summary>What the Swarm message handler tells the wait loop (written on the bridge's reader thread).</summary>
    sealed class JobState
    {
        public volatile bool Done, Failed;
        public volatile string FailureMessage, AgentError;
    }

    public static LightmassBakeResult Run(IReadOnlyList<IMEPackage> levels, LightmassBakeSettings settings, Action<string> log, CancellationToken cancel = default)
    {
        log ??= _ => { };
        var result = new LightmassBakeResult();
        var ini = LoadIni(settings, levels.Count > 0 ? levels[0].Game : MEGame.LE3);
        var timer = Stopwatch.StartNew();
        using var builder = BuildScene(levels, settings, ini, log);
        var scene = result.Scene = builder.Scene;
        cancel.ThrowIfCancellationRequested();
        if (!scene.Mappings.Any())
        {
            log("Nothing to bake: no compatible lightmap or shadow-only mappings in these levels.");
            return result;
        }
        result.ExportTime = timer.Elapsed;

        // LEX's solver hands its results over batch by batch, so atlases can be encoded while the rest is still solving
        using var early = settings.Solver != LightmassSolverChoice.UdkLightmass && !settings.RepacksLightmaps ? new EarlyWrite(scene, settings.ColourPixelFormat) : null;
        if (settings.Solver == LightmassSolverChoice.UdkLightmass) SolveWithLightmass(levels, scene, settings, ini, result, log, cancel);
        else SolveLocally(scene, settings, ini, result, log, cancel, solved: early == null ? null : early.Solved);
        if (result.Results.Count == 0) return result;
        cancel.ThrowIfCancellationRequested();

        timer.Restart();
        log("Writing lightmaps...");
        Apply(levels, scene, result, settings, builder.Builder.PackageCache, log, cancel, early);
        result.ApplyTime = timer.Elapsed;
        log($"Done in {result.ApplyTime:mm\\:ss}.");
        return result;
    }

    /// <summary>
    /// Lightmap atlases rebuilt and encoded while the bake is still solving: each one as soon as all its lightmaps' mappings are solved, on spare
    /// cores. The writing itself (package and texture cache) still happens in <see cref="Apply"/>, in the same order, so the files come out the same
    /// </summary>
    sealed class EarlyWrite : IDisposable
    {
        readonly ConcurrentDictionary<Guid, LightmassMappingResult> results = new();
        readonly Dictionary<IMEPackage, LightmapRewriter> rewriters = new();
        readonly List<IMEPackage> packages;
        readonly object gate = new();
        Task work = Task.CompletedTask;

        public LightmassBakeSource Source { get; }

        readonly string colourFormat;

        public EarlyWrite(LightmassScene scene, string colourFormat)
        {
            this.colourFormat = colourFormat;
            Source = new LightmassBakeSource(scene, results);
            packages = scene.Mappings.SelectMany(m => m.OutputTargets).Select(t => t.Owner.FileRef).Distinct().ToList();
        }

        public LightmapRewriter Rewriter(IMEPackage package)
        {
            lock (rewriters)
                return rewriters.TryGetValue(package, out var r) ? r
                    : rewriters[package] = new LightmapRewriter(package, null, ByteEncoding.Gamma22, Source, VectorBrightness) { ColourFormat = colourFormat };
        }

        public void Solved(IReadOnlyList<LightmassMappingResult> batch)
        {
            foreach (var r in batch) results[r.Guid] = r;
            // One preparation after another; each takes every atlas that has become ready
            lock (gate)
                work = work.ContinueWith(_ =>
                {
                    foreach (var package in packages)
                        Rewriter(package).PrepareReady(Source.Ready, Math.Max(1, Environment.ProcessorCount / 2));
                }, TaskScheduler.Default);
        }

        /// <summary>Waits for the preparation to finish. A failure there only means the writing does those atlases itself</summary>
        public void Wait()
        {
            Task pending;
            lock (gate) pending = work;
            try { pending.Wait(); } catch (AggregateException) { }
        }

        public void Dispose()
        {
            Wait();
            Source.Dispose();
        }
    }

    /// <summary>
    /// Lightmass's settings for the bake: UDK's BaseLightmass.ini for UDK's Lightmass, LEX's copy of its defaults (<see cref="LightmassDefaultIni"/>) for
    /// LEX's solver or when no UDK is set, with BioWare's changes for LE3 if the settings ask for them
    /// </summary>
    public static DuplicatingIni LoadIni(LightmassBakeSettings settings, MEGame game)
    {
        DuplicatingIni ini;
        if (settings.Solver == LightmassSolverChoice.UdkLightmass)
        {
            if (string.IsNullOrWhiteSpace(settings.UdkBinaries)) throw new InvalidOperationException("UDK Lightmass needs UDK's Binaries folder.");
            ini = DuplicatingIni.LoadIni(Path.Combine(settings.UdkBinaries, "..", "Engine", "Config", "BaseLightmass.ini"));
        }
        else ini = LightmassDefaultIni.Load();
        if (settings.BioWareLightmassSettings && game == MEGame.LE3) ini.ApplyBioWareOverrides();
        return ini;
    }

    /// <summary>A built scene and the builder that keeps the packages its imports resolved in, until it's disposed</summary>
    public sealed class BuiltScene(LightmassSceneBuilder builder, LightmassScene scene) : IDisposable
    {
        public LightmassSceneBuilder Builder { get; } = builder;
        public LightmassScene Scene { get; } = scene;
        public void Dispose() => Builder.Dispose();
    }

    /// <summary>Gathers the scene to bake (rendering materials with the game's shaders where it can), at the material sample sizes of <paramref name="ini"/></summary>
    public static BuiltScene BuildScene(IReadOnlyList<IMEPackage> levels, LightmassBakeSettings settings, DuplicatingIni ini, Action<string> log)
    {
        log("Gathering the scene...");
        const string materialSection = "DevOptions.StaticLightingMaterial";
        settings.Scene.EmissiveSampleSize = (int)ini.Float(materialSection, "EmissiveSampleSize");
        settings.Scene.DiffuseSampleSize = (int)ini.Float(materialSection, "DiffuseSampleSize");
        settings.Scene.TransmissionSampleSize = (int)ini.Float(materialSection, "TransmissionSampleSize");
        settings.Scene.DefaultWorldDiffuseBoost = settings.BioWareLightmassSettings && levels.Count > 0 && levels[0].Game == MEGame.LE3
            ? LightmassIni.BioWareWorldDiffuseBoost : null;
        settings.Scene.LightmapScale = settings.EffectiveLightmapScale;
        if (settings.Scene.LightmapScale != 1)
            log($"Lightmap size: {settings.Scene.LightmapScale:0.##}x the current static mesh and BSP lightmaps (up to {Game.LightmapMapping.MaxTextureSize}), written as new textures");
        LightmassMaterialCapture capture = null;
        try
        {
            capture = new LightmassMaterialCapture();
        }
        catch (Exception e)
        {
            // No Direct3D 11 device: still a usable bake, only flatter
            log($"Materials can't be rendered ({e.Message}), so they all get a constant albedo.");
        }
        settings.Scene.MaterialCapture = capture;
        if (settings.UsePrecomputedShadowsOnAllMeshes) UsePrecomputedShadowsOnAllMeshes(levels, settings.Scene.ContextOnly, log);
        var builder = new LightmassSceneBuilder(settings.Scene);
        LightmassScene scene;
        try
        {
            scene = builder.Build(levels, log);
        }
        catch
        {
            builder.Dispose();
            throw;
        }
        finally
        {
            settings.Scene.MaterialCapture = null;
            capture?.Dispose();
        }
        foreach (var (what, n) in scene.Report.Where(r => r.Key.StartsWith("material:")))
            log($"{n} {(n == 1 ? "material" : "materials")} {what["material: ".Length..]}");
        foreach (var (what, n) in scene.Report.Where(r => r.Key.StartsWith("mesh:") || r.Key.StartsWith("mapping:")).OrderBy(r => r.Key))
            log($"  {what}: {n}");
        return new BuiltScene(builder, scene);
    }

    /// <summary>
    /// Lights the scene's mappings with LEX's solver on the settings' device (the CPU when the GPU was chosen but has no usable Direct3D 11 device), into
    /// <paramref name="result"/>
    /// </summary>
    /// <param name="only">Solves only these mappings (comparisons); null: all of them</param>
    /// <param name="solved">Called with each batch's results as they come (see <see cref="LightmassLocalSolve.Solve"/>)</param>
    public static void SolveLocally(LightmassScene scene, LightmassBakeSettings settings, DuplicatingIni ini, LightmassBakeResult result, Action<string> log,
        CancellationToken cancel, Func<LightmassMapping, bool> only = null, Action<IReadOnlyList<LightmassMappingResult>> solved = null)
    {
        var device = settings.Solver == LightmassSolverChoice.Cpu ? SolverDevice.Cpu : SolverDevice.Gpu;
        if (device == SolverDevice.Gpu && !LightSolver.IsGpuAvailable())
        {
            log("No GPU with Direct3D 11 compute shaders was found, so the bake runs on the CPU.");
            device = SolverDevice.Cpu;
        }
        var mappings = scene.Mappings.Where(m => only == null || only(m)).ToList();
        log($"LEX's solver is baking {mappings.Count} mappings ({settings.Quality} quality, {device})...");
        var timer = Stopwatch.StartNew();
        var results = LightmassLocalSolve.Solve(scene, ini, settings.Quality, device, log, cancel, out var statistics, only, solved);
        foreach (var (guid, r) in results) result.Results[guid] = r;
        result.LightmassTime = timer.Elapsed;
        result.SolverName = $"LEX's solver ({statistics.DeviceName ?? device.ToString()})";
        result.FailedTasks = 0;
        result.MissingMappings = mappings.Count(m => !result.Results.ContainsKey(m.Guid));
        log($"{result.SolverName} finished in {result.LightmassTime:hh\\:mm\\:ss}: {result.Results.Count} mappings baked" +
            $"{(result.MissingMappings > 0 ? $", {result.MissingMappings} missing" : "")} ({LightmassLocalSolve.MemoryNote()}).");
    }

    /// <summary>Lights the scene's mappings with UDK's Lightmass: writes the channels, runs a Swarm job with a task per mapping and reads the results into <paramref name="result"/></summary>
    public static void SolveWithLightmass(IReadOnlyList<IMEPackage> levels, LightmassScene scene, LightmassBakeSettings settings, DuplicatingIni ini,
        LightmassBakeResult result, Action<string> log, CancellationToken cancel)
    {
        string win64 = Path.Combine(settings.UdkBinaries, "Win64");
        string lightmassExe = Path.Combine(win64, "UnrealLightmass.exe");
        if (!File.Exists(lightmassExe)) throw new FileNotFoundException("UnrealLightmass.exe wasn't found in the UDK's Binaries\\Win64 folder", lightmassExe);
        var mappings = scene.Mappings.ToDictionary(i => i.Guid);
        result.SolverName = "Lightmass";
        var timer = Stopwatch.StartNew();

        var sceneGuid = Guid.NewGuid();
        using var swarm = new SwarmClient(settings.UdkBinaries, cancel: cancel);
        var completed = new BlockingCollection<(Guid Task, bool Success)>();
        var job = new JobState();
        swarm.Message += m =>
        {
            switch (m.Type)
            {
                case "TASK_STATE" when m.Fields[1] is "TASK_STATE_COMPLETE_SUCCESS" or "TASK_STATE_COMPLETE_FAILURE" or "TASK_STATE_KILLED":
                    if (LightmassFormat.ParseUe3String(m.Fields[0]) is { } done) completed.Add((done, m.Fields[1] == "TASK_STATE_COMPLETE_SUCCESS"));
                    break;
                case "TASK_STATE" when m.Fields[1] is "TASK_STATE_REJECTED" or "TASK_STATE_INVALID":
                    if (LightmassFormat.ParseUe3String(m.Fields[0]) is { } rejected) completed.Add((rejected, false));
                    job.FailureMessage ??= $"a task was {m.Fields[1]["TASK_STATE_".Length..].ToLowerInvariant()}";
                    job.Failed = true;
                    break;
                case "JOB_STATE":
                    if (m.Fields[0] is "STATE_COMPLETE_FAILURE" or "STATE_KILLED" or "STATE_INVALID")
                    {
                        job.FailureMessage ??= string.IsNullOrEmpty(m.Fields.ElementAtOrDefault(1)) ? m.Fields[0] : m.Fields[1];
                        job.Failed = true;
                    }
                    if (m.Fields[0] is "STATE_COMPLETE_SUCCESS" or "STATE_COMPLETE_FAILURE" or "STATE_KILLED") job.Done = true;
                    if (!string.IsNullOrEmpty(m.Fields.ElementAtOrDefault(1))) log($"Swarm: job {m.Fields[0]}: {m.Fields[1]}");
                    break;
                case "ALERT":
                    lock (result.Alerts) result.Alerts.Add($"{m.Fields[0]}: {m.Fields.ElementAtOrDefault(3)}");
                    break;
                case "QUIT":
                    job.AgentError ??= "the agent quit";
                    break;
                case "ERROR":
                    // The bridge lost the agent (closed from its tray, or crashed): no more messages will come.
                    job.AgentError ??= string.Join(" ", m.Fields);
                    break;
            }
        };

        swarm.OpenJob(sceneGuid);
        log($"Exporting {scene.Instances.Count} mesh instances and {scene.Lights.Count} lights...");
        var exporter = new LightmassExporter(scene, ini, settings.Quality, swarm);
        exporter.Write(sceneGuid);
        log($"Exported ({exporter.MeshesWritten} new meshes, {exporter.MaterialsWritten} new materials and {exporter.TerrainsWritten} new terrains in Swarm's cache).");
        result.ExportTime += timer.Elapsed;
        cancel.ThrowIfCancellationRequested();

        // One task per mapping, biggest first (the agent hands them out in order).
        swarm.BeginJobSpecification(lightmassExe, $"{LightmassFormat.Ue3String(sceneGuid)} -trisperleaf 4", 0,
            [Path.Combine(win64, "AgentInterface.dll")], levels[0].FileNameNoExtension);
        foreach (var m in mappings.Values.OrderByDescending(m => m.Cost))
            swarm.AddTask(m.Guid, m.TaskType, m.Cost);
        swarm.EndJobSpecification();
        log($"Lightmass is baking {mappings.Count} mappings ({settings.Quality} quality)...");

        timer.Restart();
        var pending = mappings.Keys.ToHashSet();
        var failed = new HashSet<Guid>();
        var lastReport = Stopwatch.StartNew();
        try
        {
            while (pending.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                if (!completed.TryTake(out var task, 250))
                {
                    // Only once the queue is drained, so every task the agent reported is counted
                    if (swarm.Failure is { } bridgeFailure) throw new InvalidOperationException(bridgeFailure);
                    if (job.AgentError is { } agentError) throw new InvalidOperationException($"Lost the Swarm agent ({agentError}), so the bake can't finish. Nothing was written.");
                    if (job.Done || job.Failed) break;
                    continue;
                }
                if (!pending.Remove(task.Task)) continue;
                if (!task.Success) { failed.Add(task.Task); continue; }
                ImportChannel(swarm, task.Task, mappings, result);
                if (lastReport.Elapsed.TotalSeconds >= 2 || pending.Count == 0)
                {
                    log($"Lightmass: {mappings.Count - pending.Count}/{mappings.Count} mappings done");
                    lastReport.Restart();
                }
            }
            // Mappings delivered in another task's channel are already in; any still pending are read now if their channel exists.
            if (!job.Failed)
                foreach (var guid in pending.ToList()) ImportChannel(swarm, guid, mappings, result);
        }
        finally
        {
            try { if (swarm.Failure == null) swarm.CloseJob(); } catch (InvalidOperationException e) { log($"Swarm: CloseJob failed: {e.Message}"); }
            DeleteJobFolder(swarm.JobFolder, log);
        }
        result.LightmassTime = timer.Elapsed;
        if (job.Failed) throw new InvalidOperationException($"Lightmass's job failed ({job.FailureMessage}). Nothing was written; see Lightmass's log in UDK's Logs folder.");
        // (A failed task's mapping can still arrive in another task's channel.)
        result.FailedTasks = failed.Count(g => !result.Results.ContainsKey(g));
        result.MissingMappings = mappings.Keys.Count(g => !result.Results.ContainsKey(g) && !failed.Contains(g));
        log($"Lightmass finished in {result.LightmassTime:hh\\:mm\\:ss}: {result.Results.Count} mappings baked" +
            $"{(result.FailedTasks > 0 ? $", {result.FailedTasks} failed" : "")}{(result.MissingMappings > 0 ? $", {result.MissingMappings} missing" : "")}.");
    }

    /// <summary>
    /// The job's folder in Swarm's cache (its scene channel and results, often hundreds of MB), once the job is closed and the results read.
    /// The agent's own pruning (MaximumJobsToKeep) leaves these behind.
    /// </summary>
    static void DeleteJobFolder(string folder, Action<string> log)
    {
        if (folder == null || !Directory.Exists(folder)) return;
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"Swarm's job folder {folder} couldn't be deleted ({e.Message}).");
        }
    }

    /// <summary>A mapping's results channel. Lightmass can merge several mappings into one channel, named after one of them.</summary>
    static void ImportChannel(SwarmClient swarm, Guid task, Dictionary<Guid, LightmassMapping> mappings, LightmassBakeResult result)
    {
        if (result.Results.ContainsKey(task) || !mappings.TryGetValue(task, out var mapping)) return;
        string channel = mapping.IsTextureMapping
            ? LightmassFormat.ChannelName(task, LightmassFormat.TextureMappingVersion, LightmassFormat.TextureMappingExtension)
            : LightmassFormat.ChannelName(task, LightmassFormat.VertexMappingVersion, LightmassFormat.VertexMappingExtension);
        if (swarm.JobChannelPath(channel) is not { } path) return;
        List<LightmassMappingResult> read;
        try
        {
            read = LightmassResults.Read(path, mapping.IsTextureMapping);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            throw new InvalidDataException($"Lightmass's results in {path} couldn't be read: {e.Message}", e);
        }
        foreach (var r in read) result.Results[r.Guid] = r;
    }

    /// <param name="cache">The scene's, which has the packages its materials are in</param>
    static void Apply(IReadOnlyList<IMEPackage> levels, LightmassScene scene, LightmassBakeResult result, LightmassBakeSettings settings, PackageCache cache,
        Action<string> log, CancellationToken cancel, EarlyWrite early = null)
    {
        var applyTimer = Stopwatch.StartNew();
        string tfcDirectory = settings.TfcDirectory ?? Path.GetDirectoryName(levels[0].FilePath);
        early?.Wait();
        using var ownSource = early == null ? new LightmassBakeSource(scene, result.Results) : null;
        var source = early?.Source ?? ownSource;
        var packages = scene.Mappings.Where(m => result.Results.ContainsKey(m.Guid)).SelectMany(m => m.OutputTargets).Select(t => t.Owner.FileRef).Distinct().ToList();
        var tfc = new TfcWriter(tfcDirectory, settings.TfcName, append: true, sourcePackages: packages,
            copiedCacheSource: settings.CopiedCacheSource);
        result.TfcPath = tfc.FilePath;
        var step = Stopwatch.StartNew();
        string Lap() { string t = $"{step.Elapsed:mm\\:ss}"; step.Restart(); return t; }
        log($"Prepared the bake's results for writing in {applyTimer.Elapsed:mm\\:ss} ({LightmassLocalSolve.MemoryNote()})");
        foreach (var package in packages)
        {
            cancel.ThrowIfCancellationRequested();
            step.Restart();
            var rewriter = early?.Rewriter(package) ?? new LightmapRewriter(package, tfc, ByteEncoding.Gamma22, source, VectorBrightness)
            {
                ColourFormat = settings.ColourPixelFormat, SizeScale = settings.Scene.LightmapScale,
            };
            rewriter.UseTfc(tfc);
            var summary = rewriter.RewriteAll(settings.RepacksLightmaps);
            string rewrite = Lap();
            LightmassMappingAllocator.Write(package, tfc, scene, result.Results, summary, cache, settings.ColourPixelFormat ?? "PF_DXT1");
            string allocate = Lap();
            result.Rewrites[package] = summary;
            log($"{package.FileNameNoExtension}: {LightmassBakeReport.LightmapLine(summary)} (lightmaps {rewrite}: {summary.Timing}; new mappings {allocate}; {LightmassLocalSolve.MemoryNote()})");
            var shadows = result.ShadowMaps[package] = new LightmassShadowMapWriter(package, tfc, scene, result.Results).Write(summary.AppliedLightmaps, summary.ShadowOnlyAllocations);
            log($"{package.FileNameNoExtension}: {LightmassBakeReport.ShadowLine(shadows)} ({Lap()})");
            var decals = result.Decals[package] = DecalReceiverLighting.Write(package, DecalReceivers(scene, package, summary), cache);
            string decalTime = Lap();
            if (decals.Receivers > 0 || decals.Problems.Count > 0) log($"{package.FileNameNoExtension}: {LightmassBakeReport.DecalLine(decals)} ({decalTime})");
            foreach (string problem in decals.Problems) log(problem);
        }
        step.Restart();
        tfc.PreserveUnwrittenTextures(packages);
        log($"Texture cache finished in {Lap()} ({LightmassLocalSolve.MemoryNote()})");
        foreach (var (why, n) in source.Report) result.KeptOriginal[why] = n;
        // A lightmap of a level's own that a mapping Lightmass failed or delivered nothing for, or whose result couldn't be applied
        var unfinished = source.Unfinished
            .Concat(scene.Mappings.Where(m => !result.Results.ContainsKey(m.Guid)).SelectMany(m => m.OutputTargets).Select(t => t.Owner.FileRef)).ToHashSet();
        result.ClearedMarks.UnionWith(ClearMarks(levels, settings.Scene.ContextOnly, scene.UnreadPrimitives > 0, unfinished, log));
    }

    /// <summary>The static meshes whose LOD 0 got new lighting, each with its mesh's LOD 0: their decals copy it (<see cref="DecalReceiverLighting"/>)</summary>
    static Dictionary<ExportEntry, StaticMeshRenderData> DecalReceivers(LightmassScene scene, IMEPackage package, LightmapRewriter.Summary summary) =>
        scene.Instances.Where(i => i.LodIndex == 0 && i.Spline == null && i.Component.FileRef == package
                && (summary.AppliedLightmaps.Contains((i.Component, "LOD0")) || summary.ShadowOnlyAllocations.Contains((i.Component, "LOD0"))))
            .ToDictionary(i => i.Component, i => i.Mesh.Mesh.LODModels[0]);

    /// <summary>
    /// After a bake: clears the marks of what's changed since the last bake (<see cref="StaticLightingChanges"/>) in each level it finished, which is baked
    /// as it is now. A level whose own lightmap kept its original lighting (<paramref name="unfinishedLevels"/>) isn't finished, and none is if the scene
    /// couldn't read a primitive (<paramref name="unreadPrimitives"/>), which is then missing from every level's shadows and bounced light. A lightmap kept by
    /// design (an incompatible allocation, say) doesn't count: no bake would change it. A context-only level's own changes aren't baked into it. A light's
    /// mark is in its own level, so one that reaches another level that wasn't rebaked is cleared all the same: the marks are a reminder, not a guarantee
    /// </summary>
    /// <returns>The levels whose marks were cleared</returns>
    public static List<IMEPackage> ClearMarks(IEnumerable<IMEPackage> levels, IReadOnlySet<IMEPackage> contextOnly, bool unreadPrimitives,
        IReadOnlySet<IMEPackage> unfinishedLevels, Action<string> log)
    {
        var cleared = new List<IMEPackage>();
        if (unreadPrimitives)
        {
            log("Some primitives couldn't be read, so they're missing from the bake, and what's changed since the last bake stays marked.");
            return cleared;
        }
        foreach (var level in levels.Where(l => !contextOnly.Contains(l)))
        {
            if (unfinishedLevels.Contains(level)) log($"{level.FileNameNoExtension}: some of its lighting wasn't baked, so what's changed since its last bake stays marked.");
            else if (StaticLightingChanges.ClearAll(level)) cleared.Add(level);
        }
        return cleared;
    }
}

/// <summary>A human-readable account of a bake, for the Lightmass Baker's log and the headless bake.</summary>
public static class LightmassBakeReport
{
    public static string ShadowLine(LightmassShadowMapWriter.Summary s) =>
        $"{s.TextureShadowMaps} texture shadow maps in {s.Atlases} atlases, {s.VertexShadowMaps} vertex shadow maps (replacing {s.Removed})";

    public static string DecalLine(DecalReceiverLighting.Summary s) =>
        $"{s.Receivers} decal surfaces copied their receivers' lighting ({s.VertexLit} with vertex lighting, {s.ShadowMaps} vertex shadow maps" +
        (s.Removed > 0 ? $", {s.Removed} old ones removed" : "") + ")" +
        (s.UnknownMaterials > 0 ? $" ({s.UnknownMaterials} decals' materials couldn't be found, so they were taken to be lit)" : "") +
        (s.Problems.Count > 0 ? $" ({s.Problems.Count} left as they were)" : "");

    public static string LightmapLine(LightmapRewriter.Summary s) =>
        $"{s.Allocations} texture lightmaps in {s.TextureSets} atlases, {s.VertexLightmaps} vertex lightmaps" +
        (s.AllocatedLightmaps > 0 ? $" ({s.AllocatedLightmaps} newly allocated)" : "") +
        (s.ShadowOnlyAllocations.Count > 0 ? $" ({s.ShadowOnlyAllocations.Count} new mappings without direct lighting got only shadow maps)" : "") +
        (s.SkippedNewMappings > 0 ? $" ({s.SkippedNewMappings} new mappings left unchanged: missing, invalid or unmapped results)" : "") +
        (s.UnbakedSets > 0 ? $" ({s.UnbakedSets} atlases with nothing baked left as they were)" : "");

    public static string Summary(LightmassBakeResult result, bool preview = false)
    {
        int mappings = result.Scene?.Mappings.Count() ?? 0;
        if (mappings == 0) return "There was nothing to bake: none of the baked files have compatible lightmap or shadow-only mappings.";
        var lines = new List<string>
        {
            $"{result.SolverName} baked {result.Results.Count} of {mappings} mappings in {result.LightmassTime:hh\\:mm\\:ss}" +
            (result.Complete ? "." : $" ({result.FailedTasks} failed, {result.MissingMappings} missing: they keep their original lighting).")
        };
        foreach (var (package, summary) in result.Rewrites)
        {
            lines.Add($"{package.FileNameNoExtension}: {LightmapLine(summary)}");
            if (result.ShadowMaps.TryGetValue(package, out var shadows)) lines.Add($"    {ShadowLine(shadows)}");
            if (result.Decals.TryGetValue(package, out var decals) && (decals.Receivers > 0 || decals.Problems.Count > 0)) lines.Add($"    {DecalLine(decals)}");
        }
        if (result.KeptOriginal.Count > 0)
            lines.Add($"Kept their original lighting: {string.Join(", ", result.KeptOriginal.Select(k => $"{k.Value} {k.Key}"))}.");
        if (result.ClearedMarks.Count > 0)
            lines.Add($"No longer marked as changed since their last bake: {string.Join(", ", result.ClearedMarks.Select(l => l.FileNameNoExtension))}.");
        if (result.Alerts.Count > 0)
        {
            lines.Add($"Lightmass reported {result.Alerts.Count} warnings:");
            foreach (var group in result.Alerts.GroupBy(a => a).OrderByDescending(g => g.Count()).Take(10))
                lines.Add($"  {group.Count()} x {group.Key}");
        }
        if (result.TfcPath != null) lines.Add(preview ? "Texture cache prepared for preview; it will be published when you save."
            : $"Streamed lightmap textures: {result.TfcPath} (ship it with the mod).");
        foreach (string saved in result.SavedFiles) lines.Add($"Saved {saved}");
        return string.Join("\n", lines);
    }
}
