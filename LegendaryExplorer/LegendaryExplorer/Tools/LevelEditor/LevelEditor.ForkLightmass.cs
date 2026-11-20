using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using MessageBox = LegendaryExplorer.SharedUI.ThemedMessageBox;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Fork: the "Create Lightmass" static lighting baker (C# and native backends; per level, and per actor from the actor menu), the Navigation generator
/// and its pathing/cover overlay, and the busy indicator's determinate progress. Upstream's UDK Lightmass Baker stays on the Build menu
/// (<see cref="OpenInLightmassBaker_Click"/>)
/// </summary>
public partial class LevelEditor
{
    public ICommand GenerateNavigationCommand { get; private set; }
    public ICommand GenerateStaticLightingCommand { get; private set; }

    /// <summary>Build → Scottina's Lightmass Baker: the fork baker's settings, then its bake</summary>
    private void ScottinaLightmassBaker_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!HasAnyFileOpen) return;
        new ScottinaLightmassBakerWindow(this).ShowDialog();
    }
    public ICommand CommitSingleFileCommand { get; private set; }

    /// <summary>The fork's commands, made with upstream's (<see cref="LoadCommands"/>)</summary>
    private void LoadForkCommands()
    {
        CaptureViewportCommand = new GenericCommand(CaptureViewport, PackageIsLoaded);
        SnapActorToCameraCommand = new GenericCommand(SnapActorToCamera, CanMoveSelectedActor);
        ViewActorPropertiesCommand = new GenericCommand(() => LoadExportIntoTabs(SelectedActor?.Export, 0), () => SelectedActor?.Export is not null);
        ViewActorMetadataCommand = new GenericCommand(() => LoadExportIntoTabs(SelectedActor?.Export, 1), () => SelectedActor?.Export is not null);
        GenerateNavigationCommand = new GenericCommand(GenerateNavigation,
            () => CanEditScene && ActiveFile is { IsReadOnly: false } && Game.IsGame3() && !IsBusy);
        GenerateStaticLightingCommand = new GenericCommand(GenerateStaticLighting,
            () => CanEditScene && OpenFiles.Any(file => file.IncludeInLightmass && !file.IsReadOnly) && !IsBusy);
        CommitSingleFileCommand = new RelayCommand(CommitSingleFileExecute, _ => CanEditScene && PackageIsLoaded());
    }

    /// <summary>
    /// Fork: the per-file Commit button. Upstream commits every writable file at once, because a commit clears the undo history (and so the other files'
    /// undo steps); committing one file asks first if the others have uncommitted changes
    /// </summary>
    private void CommitSingleFileExecute(object parameter)
    {
        OpenLevelFile file = ResolveFileParameter(parameter);
        if (file is null || file.IsReadOnly) return;
        if (OpenFiles.Any(other => other != file && other.IsDirty)
            && MessageBox.Show(this, $"Committing {file.FileName} clears the undo history, including the steps of the other files' uncommitted changes (which stay uncommitted). Commit it anyway?",
                "Commit file", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }
        CommitChangesForFiles([file]);
    }

    #region Busy progress

    private bool _busyProgressIsIndeterminate = true;
    public bool BusyProgressIsIndeterminate
    {
        get => _busyProgressIsIndeterminate;
        set => SetProperty(ref _busyProgressIsIndeterminate, value);
    }

    private double _busyProgressMaximum = 1d;
    public double BusyProgressMaximum
    {
        get => _busyProgressMaximum;
        set => SetProperty(ref _busyProgressMaximum, Math.Max(1d, value));
    }

    private double _busyProgressValue;
    public double BusyProgressValue
    {
        get => _busyProgressValue;
        set => SetProperty(ref _busyProgressValue, Math.Clamp(value, 0d, BusyProgressMaximum));
    }

    private void UpdateStaticLightingScanProgress(StaticLightingBuildProgress progress)
    {
        BusyText = progress.DisplayText;
        BusyProgressIsIndeterminate = !progress.IsDeterminate;
        BusyProgressMaximum = Math.Max(1, progress.Total);
        BusyProgressValue = progress.IsDeterminate ? progress.Current : 0;
    }

    private void ResetBusyProgress()
    {
        BusyProgressIsIndeterminate = true;
        BusyProgressMaximum = 1;
        BusyProgressValue = 0;
    }

    /// <summary>Reports only the latest value, at most once per dispatcher pass</summary>
    private sealed class LatestUiProgress<T>(Dispatcher dispatcher, Action<T> update) : IProgress<T>
    {
        private readonly object gate = new();
        private T latest;
        private bool scheduled;

        public void Report(T value)
        {
            lock (gate)
            {
                latest = value;
                if (scheduled) return;
                scheduled = true;
            }
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(DeliverLatest));
        }

        private void DeliverLatest()
        {
            T value;
            lock (gate)
            {
                value = latest;
                scheduled = false;
            }
            update(value);
        }
    }

    #endregion

    #region Create Lightmass (the fork's static lighting baker)

    private int _lightmassResolution = 64;
    public int LightmassResolution { get => _lightmassResolution; set => SetProperty(ref _lightmassResolution, value); }
    public IReadOnlyList<int> LightmassResolutions { get; } = [64, 128, 256, 512, 1024, 2048];
    private int _actorLightmassResolution = 64;
    private StaticLightingTextureFormat _lightmassTextureFormat = StaticLightingTextureFormat.DXT1;
    public StaticLightingTextureFormat LightmassTextureFormat { get => _lightmassTextureFormat; set => SetProperty(ref _lightmassTextureFormat, value); }
    public IReadOnlyList<StaticLightingTextureFormat> LightmassTextureFormats { get; } = [StaticLightingTextureFormat.DXT1, StaticLightingTextureFormat.ARGB, StaticLightingTextureFormat.BC7];
    private float _lightmassAmbientIntensity = 0.12f;
    public float LightmassAmbientIntensity { get => _lightmassAmbientIntensity; set => SetProperty(ref _lightmassAmbientIntensity, value); }
    private float _lightmassShadowBias = 1f;
    public float LightmassShadowBias { get => _lightmassShadowBias; set => SetProperty(ref _lightmassShadowBias, value); }
    private int _lightmassShadowSamples = 8;
    public int LightmassShadowSamples { get => _lightmassShadowSamples; set => SetProperty(ref _lightmassShadowSamples, value); }
    private float _lightmassSourceRadius = 16f;
    public float LightmassSourceRadius { get => _lightmassSourceRadius; set => SetProperty(ref _lightmassSourceRadius, value); }
    private float _lightmassDirectionalSourceAngle = 0.5f;
    public float LightmassDirectionalSourceAngle { get => _lightmassDirectionalSourceAngle; set => SetProperty(ref _lightmassDirectionalSourceAngle, value); }
    private int _lightmassWorkerThreads;
    public int LightmassWorkerThreads { get => _lightmassWorkerThreads; set => SetProperty(ref _lightmassWorkerThreads, value); }
    private int _lightmassWorkTileSize = 16;
    public int LightmassWorkTileSize { get => _lightmassWorkTileSize; set => SetProperty(ref _lightmassWorkTileSize, value); }
    public IReadOnlyList<int> LightmassWorkTileSizes { get; } = [8, 16, 32, 64, 128];
    // New settings bake with the light solver: on the GPU when there's a hardware Direct3D 11 device, else on the CPU (WARP)
    private StaticLightingBakeBackend _lightmassBackend = StaticLightingBakeBackends.DefaultBackend();
    /// <summary>The "Device": the light solver on the GPU or CPU, or a classic direct-light backend</summary>
    public StaticLightingBakeBackend LightmassBackend
    {
        get => _lightmassBackend;
        set
        {
            if (SetProperty(ref _lightmassBackend, value))
                OnPropertyChanged(nameof(LightmassUsesLightSolver));
        }
    }
    public bool LightmassUsesLightSolver => StaticLightingBakeBackends.UsesLightSolver(LightmassBackend);
    /// <summary>The Device choices; GPU only when there's a hardware Direct3D 11 device</summary>
    public IReadOnlyList<LightmassBackendChoice> LightmassBackends { get; } = CreateLightmassBackendChoices();

    private static LightmassBackendChoice[] CreateLightmassBackendChoices()
    {
        var choices = new List<LightmassBackendChoice>();
        if (StaticLightingBakeBackends.IsGpuAvailable)
            choices.Add(new(StaticLightingBakeBackend.Gpu, "GPU",
                "LEX's light solver on the graphics card: direct light, bounced light and sky/environment occlusion."));
        choices.Add(new(StaticLightingBakeBackend.Cpu, "CPU",
            "LEX's light solver on every CPU core (Direct3D 11 WARP): the same lighting as on the GPU, more slowly."));
        choices.Add(new(StaticLightingBakeBackend.NativeCpp, StaticLightingBakeBackends.ClassicDisplayName,
            "The classic native C++ baker: direct light, soft shadows, emissive area samples and an unoccluded environment floor."));
        choices.Add(new(StaticLightingBakeBackend.CSharp, StaticLightingBakeBackends.ClassicDisplayName + ", C#",
            "The classic baker's managed evaluator: the same lighting as the native one."));
        return choices.ToArray();
    }
    private int _lightmassBounces = 2;
    /// <summary>Light solver: indirect lighting bounces</summary>
    public int LightmassBounces { get => _lightmassBounces; set => SetProperty(ref _lightmassBounces, value); }
    private int _lightmassGatherRays = 64;
    /// <summary>Light solver: final gather rays per sample</summary>
    public int LightmassGatherRays { get => _lightmassGatherRays; set => SetProperty(ref _lightmassGatherRays, value); }
    private string _lightmassTextureCacheName = "";
    public string LightmassTextureCacheName { get => _lightmassTextureCacheName; set => SetProperty(ref _lightmassTextureCacheName, value); }

    public sealed record LightmassBackendChoice(StaticLightingBakeBackend Backend, string DisplayName, string Description = "");

    private static StaticMeshComponentProxy[] GetStaticLightingComponents(ActorProxy actor) =>
        actor.Components.OfType<StaticMeshComponentProxy>()
            .Where(StaticLightingBaker.IsStaticLightingCandidate)
            .ToArray();

    private async void GenerateStaticLighting() => await GenerateStaticLightingCore(null,
        Math.Min(LightmassResolution, StaticLightingBaker.MaximumLevelTextureResolution),
        StaticLightingMappingMode.Automatic);

    private async Task GenerateStaticLightingForActor(ActorProxy actor, int textureResolution, StaticLightingMappingMode mappingMode)
    {
        if (actor is null || actor.IsReadOnly || IsBusy) return;

        StaticMeshComponentProxy[] components = GetStaticLightingComponents(actor);
        if (components.Length == 0)
        {
            MessageBox.Show(this, "The selected actor has no static-mesh components eligible for Lightmass.",
                "Create Actor Lightmass", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (mappingMode != StaticLightingMappingMode.Vertex1D)
            _actorLightmassResolution = textureResolution;
        await GenerateStaticLightingCore(actor, textureResolution, mappingMode);
    }

    /// <summary>
    /// Bakes with the fork's baker and writes the results into the packages (and TFCs) directly; upstream's preview refresh then redraws the components
    /// from their new lighting. Uncommitted editor changes aren't part of the bake
    /// </summary>
    private async Task GenerateStaticLightingCore(ActorProxy targetActor, int textureResolution, StaticLightingMappingMode mappingMode)
    {
        if (!CanMutateScene) return;
        bool isSingleActor = targetActor is not null;
        OpenLevelFile[] targetFiles = isSingleActor
            ? targetActor.OwningFile is { IsReadOnly: false } file ? [file] : []
            : OpenFiles.Where(file => file.IncludeInLightmass && !file.IsReadOnly).ToArray();
        string dialogTitle = isSingleActor ? "Create Actor Lightmass" : "Create Lightmass";
        if (targetFiles.Length == 0)
        {
            MessageBox.Show(this, isSingleActor
                    ? "The selected actor's level is read-only."
                    : "Select at least one writable loaded level to receive static lighting.",
                dialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var settings = new StaticLightingGenerationSettings
        {
            TextureResolution = textureResolution,
            MappingMode = mappingMode,
            AmbientIntensity = LightmassAmbientIntensity,
            ShadowBias = LightmassShadowBias,
            ShadowSampleCount = LightmassShadowSamples,
            DefaultLightSourceRadius = LightmassSourceRadius,
            DirectionalSourceAngleDegrees = LightmassDirectionalSourceAngle,
            WorkerThreads = LightmassWorkerThreads,
            WorkTileSize = LightmassWorkTileSize,
            TextureCacheName = LightmassTextureCacheName,
            TextureFormat = LightmassTextureFormat,
            Backend = LightmassBackend,
            SolverBounces = LightmassBounces,
            SolverGatherRays = LightmassGatherRays
        };
        bool usesLightSolver = StaticLightingBakeBackends.UsesLightSolver(settings.Backend);
        string backendName = StaticLightingBakeBackends.DisplayName(settings.Backend);

        bool busy = false;
        try
        {
            settings.Validate();
            StaticMeshComponentProxy[] actorComponents = isSingleActor ? GetStaticLightingComponents(targetActor) : [];
            IReadOnlySet<ExportEntry> exactTargetComponents = isSingleActor
                ? actorComponents.Select(component => component.Export).ToHashSet()
                : null;
            string targetList = isSingleActor
                ? $"  • {targetActor.Export.UIndex}: {targetActor.Export.ObjectName.Instanced} " +
                  $"({actorComponents.Length:N0} static component{(actorComponents.Length == 1 ? "" : "s")})"
                : string.Join("\n", targetFiles.Select(file => $"  • {file.FileName}"));
            string storageDescription = Game == MEGame.ME1
                ? "ME1 light data will be stored in the selected packages (ME1 does not use TFC texture caches)."
                : string.IsNullOrWhiteSpace(settings.TextureCacheName)
                    ? "Each target folder's existing TFC will be reused; if none exists, a dedicated Lightmass TFC will be created."
                    : $"Texture cache: {Path.GetFileNameWithoutExtension(settings.TextureCacheName)}.tfc";
            string targetDescription = isSingleActor
                ? "Build static lighting for this actor?"
                : $"Build static lighting for {targetFiles.Length:N0} loaded level(s)?";
            string mappingDescription = settings.MappingMode switch
            {
                StaticLightingMappingMode.Texture2D =>
                    $"Mapping: prefer LightMap2D at {settings.TextureResolution}×{settings.TextureResolution}; isolated collapsed UV triangles are repaired automatically, and components without a runtime-compatible lightmap UV transparently use LightMap1D.",
                StaticLightingMappingMode.Vertex1D =>
                    "Mapping: force LightMap1D per-vertex lighting.",
                _ =>
                    $"Mapping: automatic detail-aware selection; every LightMap2D receiver uses exactly {settings.TextureResolution}×{settings.TextureResolution}, and compact/dense receivers use LightMap1D."
            };
            string uncommittedWarning = IsDirty ? "Uncommitted Level Editor changes are not part of the bake: commit them first to include them.\n\n" : "";
            string confirmation = uncommittedWarning + $"{targetDescription}\n\n{targetList}\n\n" +
                                  $"Lights and occluding static geometry are gathered from all {OpenFiles.Count:N0} loaded levels. " +
                                  "Existing lighting channels are preserved and strictly filter which lights may contribute. " +
                                  (usesLightSolver
                                      ? "Emissive sections light the scene from their own surfaces; light bounces off every shadow-casting mesh, and the sky and environment are occluded by geometry. "
                                      : "Components authored for emissive static lighting are reduced to bounded area samples and spatially culled per receiver. ") +
                                  mappingDescription + "\n\n" +
                                  $"Soft shadows: {settings.ShadowSampleCount:N0} deterministic samples, " +
                                  $"{settings.DefaultLightSourceRadius:F1} point/spot radius, " +
                                  $"{settings.DirectionalSourceAngleDegrees:F1}° directional angle.\n" +
                                  (usesLightSolver
                                      ? $"Bounced light: {settings.SolverBounces:N0} bounce(s), {settings.SolverGatherRays:N0} gather rays per sample.\n"
                                      : "") +
                                  $"Bake workers: {settings.EffectiveWorkerThreads:N0}; UV/spatial work tile: {settings.WorkTileSize}×{settings.WorkTileSize}.\n\n" +
                                  $"Lightmap texture format: {settings.TextureFormat}.\n" +
                                  $"Bake backend: {backendName}.\n\n" +
                                  storageDescription + "\n\nThe packages remain unsaved until you use Save, but TFC data is appended during generation.";
            if (MessageBox.Show(this, confirmation, dialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            SetBusy("Collecting static meshes and lights from loaded levels...");
            busy = true;
            IsBusyTaskbar = true;
            ResetBusyProgress();
            IProgress<StaticLightingBuildProgress> scanProgress =
                new LatestUiProgress<StaticLightingBuildProgress>(Dispatcher, UpdateStaticLightingScanProgress);
            IProgress<string> progress = new LatestUiProgress<string>(Dispatcher, message =>
            {
                BusyText = message;
                BusyProgressIsIndeterminate = true;
            });
            var targetSet = targetFiles.ToHashSet();
            //(uncommitted actors aren't part of the bake, as it warned: a draft has no export to bake into, or to be read from)
            ActorProxy[] sceneActors = Actors.Where(actor => !actor.IsDraft).ToArray();
            StaticLightingBakeResult bake = await Task.Run(() =>
            {
                var scene = StaticLightingBaker.BuildScene(sceneActors, targetSet, RenderContext,
                    exactTargetComponents, settings.MappingMode, settings.TextureResolution,
                    settings.Backend, settings.EffectiveWorkerThreads, scanProgress);
                if (scene.Targets.Count == 0)
                    return new StaticLightingBakeResult
                    {
                        Components = [],
                        SourceTriangleCount = scene.Collision.TriangleCount,
                        LightCount = scene.Lights.Count,
                        EmissiveEmitterCount = scene.EmissiveEmitters.Count,
                        TextureMappedComponentCount = 0,
                        VertexMappedComponentCount = 0,
                        Backend = settings.Backend,
                        SceneDiagnostics = scene.Diagnostics
                    };
                return new StaticLightingBaker(scene.Targets, scene.Lights, scene.Collision, settings,
                        scene.Diagnostics, scene.EmissiveEmitters, scene.SolverGeometry)
                    .Bake(CancellationToken.None, progress, scanProgress);
            }).ConfigureAwait(true);

            if (bake.Components.Count == 0 && bake.SceneDiagnostics.ExcludedUnlitReceivers.Count == 0)
            {
                MessageBox.Show(this, isSingleActor
                        ? "The selected actor has no renderable static mesh components to bake."
                        : "No writable static mesh components were found in the selected levels.",
                    dialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BusyText = "Writing lightmaps, shadow maps, and texture-cache data...";
            BusyProgressIsIndeterminate = true;
            await Dispatcher.Yield(DispatcherPriority.Background);
            // The writes are package edits: upstream's preview refresh redraws the components with their new lighting
            StaticLightingWriteResult written = StaticLightingWriter.Write(bake, settings);
            int mappingFallbackCount = bake.UvFallbackComponentCount;
            string mappingFallbackWarning = mappingFallbackCount > 0
                ? $"WARNING: {mappingFallbackCount:N0} component{(mappingFallbackCount == 1 ? " was" : "s were")} diverted to LightMap1D because no valid runtime lightmap UV was available.\n\n"
                : "";
            int mappingConflictTexels = bake.Components.Sum(component => component.Diagnostics.MappingConflictTexelCount);
            int repairedUvTriangleCount = bake.Components
                .Where(component => component.Texture is not null)
                .Sum(component => component.Diagnostics.Mapping.DegenerateUvTriangleCount);
            string cacheSummary = written.TextureCachePaths.Count == 0
                ? "Lighting data was stored in the level packages."
                : "Texture cache output:\n" + string.Join("\n", written.TextureCachePaths.Select(path => $"  • {path}"));
            TextBelowActors = $"Lightmass: {written.ComponentCount:N0} static components, " +
                              $"{written.ExcludedUnlitReceiverCount:N0} incompatible receivers protected, " +
                              $"{written.LightMapTextureCount:N0} lightmap textures, {written.ShadowMapCount:N0} shadow maps; " +
                              $"{written.IrrelevantLightReferenceCount:N0} irrelevant-light references; " +
                              $"{bake.WorkUnitCount:N0} parallel work units on {bake.WorkerCount:N0} workers; " +
                              $"{bake.RaysCast:N0} rays, {bake.AverageVisibility:P1} average visibility; " +
                              $"{bake.LightCount:N0} lights / {bake.EmissiveEmitterCount:N0} emissive area samples / " +
                              $"{bake.SourceTriangleCount:N0} occluder triangles from all loaded levels; " +
                              $"{StaticLightingBakeBackends.DisplayName(bake.Backend)} backend.";
            MessageBox.Show(this,
                mappingFallbackWarning +
                $"Generated static lighting for {written.ComponentCount:N0} components.\n\n" +
                $"Unlit/unsupported-material receivers kept lightmap-free: {written.ExcludedUnlitReceiverCount:N0}\n" +
                $"2D lightmaps: {bake.TextureMappedComponentCount:N0} components\n" +
                $"Vertex-lightmap fallback: {bake.VertexMappedComponentCount:N0} components\n" +
                $"Lightmap textures: {written.LightMapTextureCount:N0}\n" +
                $"Shadow maps: {written.ShadowMapCount:N0}\n" +
                $"Irrelevant-light references: {written.IrrelevantLightReferenceCount:N0}\n" +
                $"Parallel work units: {bake.WorkUnitCount:N0} on {bake.WorkerCount:N0} workers\n" +
                $"Bake backend: {StaticLightingBakeBackends.DisplayName(bake.Backend)}\n" +
                (string.IsNullOrEmpty(bake.LightSolverSummary) ? "" : $"Light solver: {bake.LightSolverSummary}\n") +
                $"Rays / occluded samples: {bake.RaysCast:N0} / {bake.OccludedSamples:N0}\n" +
                $"Emissive area samples / evaluated / rays: {bake.EmissiveEmitterCount:N0} / {bake.EmissiveSamplesEvaluated:N0} / {bake.EmissiveRaysCast:N0}\n" +
                $"Average direct visibility: {bake.AverageVisibility:P1}\n" +
                $"Average direct / environment contribution: {bake.AverageDirectContribution:F3} / {bake.AverageEnvironmentContribution:F3}\n" +
                $"Rejected receiver self-intersections: {bake.RejectedSelfIntersections:N0}\n" +
                $"Repaired degenerate UV triangles: {repairedUvTriangleCount:N0}\n" +
                $"UV mapping fallbacks / conflicting texels: {mappingFallbackCount:N0} / {mappingConflictTexels:N0}\n" +
                $"Scene extraction / light gathering: {bake.SceneDiagnostics.SceneExtractionMilliseconds / 1000d:F2}s / {bake.SceneDiagnostics.LightGatheringMilliseconds / 1000d:F2}s\n" +
                $"Mesh prep / receiver prep / BVH: {bake.SceneDiagnostics.MeshPreparationMilliseconds / 1000d:F2}s / {bake.SceneDiagnostics.ReceiverPreparationMilliseconds / 1000d:F2}s / {bake.SceneDiagnostics.BvhConstructionMilliseconds / 1000d:F2}s ({bake.SceneDiagnostics.BvhNodeCount:N0} nodes)\n" +
                $"Emissive preprocessing / receiver culling: {bake.SceneDiagnostics.EmissivePreprocessingMilliseconds / 1000d:F2}s / {bake.EmissiveReceiverCullingMilliseconds / 1000d:F2}s " +
                $"({bake.SceneDiagnostics.EmissiveSourceTriangleCount:N0} source triangles -> {bake.SceneDiagnostics.AreaEmitterSampleCount:N0} samples, {bake.SceneDiagnostics.AreaEmitterBvhNodeCount:N0} nodes)\n" +
                $"Worker time — light prep / 2D raster / 1D sampling: {bake.LightPreparationMilliseconds / 1000d:F2}s / {bake.TextureRasterizationMilliseconds / 1000d:F2}s / {bake.VertexSamplingMilliseconds / 1000d:F2}s\n" +
                $"Worker time — direct lighting / visibility rays: {bake.DirectLightingMilliseconds / 1000d:F2}s / {bake.ShadowRayMilliseconds / 1000d:F2}s\n" +
                $"Worker time — occupied texels / filtering / texture construction: {bake.OccupiedTexelDiscoveryMilliseconds / 1000d:F2}s / {bake.FilteringMilliseconds / 1000d:F2}s / {bake.TextureConstructionMilliseconds / 1000d:F2}s\n" +
                (bake.Backend == StaticLightingBakeBackend.NativeCpp
                    ? $"Native topology / instances / light scan: {bake.SceneDiagnostics.NativeTopologyScanMilliseconds / 1000d:F2}s / {bake.SceneDiagnostics.NativeInstanceScanMilliseconds / 1000d:F2}s / {bake.SceneDiagnostics.NativeLightScanMilliseconds / 1000d:F2}s\n" +
                      $"Native BVH / compute: {bake.NativeBvhConstructionMilliseconds / 1000d:F2}s ({bake.NativeBvhNodeCount:N0} nodes) / {bake.NativeComputeMilliseconds / 1000d:F2}s\n" +
                      $"Native 1D / 2D / shadow traversal: {bake.NativeBake1DMilliseconds / 1000d:F2}s / {bake.NativeBake2DMilliseconds / 1000d:F2}s / {bake.NativeShadowTraversalMilliseconds / 1000d:F2}s\n" +
                      $"Native samples / occupied texels / relevant lights: {bake.NativeSamplesProcessed:N0} / {bake.NativeOccupiedTexels:N0} / {bake.NativeRelevantLights:N0}\n" +
                      $"Native BVH visits / triangle tests / any-hit early-outs: {bake.NativeBvhNodesVisited:N0} / {bake.NativeRayTriangleTests:N0} / {bake.NativeAnyHitEarlyOuts:N0}\n" +
                      $"Native throughput: {bake.NativeSamplesPerSecond:N0} samples/s, {bake.NativeRaysPerSecond:N0} rays/s\n"
                    : "") +
                (StaticLightingBakeBackends.UsesLightSolver(bake.Backend)
                    ? ""
                    : "Texture-resolved emissive detail, multi-bounce GI and denoising: not present in this direct-light baker (choose the GPU or CPU device for bounced light, or use Build → Bake Lighting in Lightmass Baker for UDK Lightmass)\n") +
                $"Bake wall time / total serialization: {bake.BakeMilliseconds / 1000d:F2}s / {written.SerializationMilliseconds / 1000d:F2}s\n" +
                $"LightMap1D / LightMap2D serialization: {written.LightMap1DSerializationMilliseconds / 1000d:F2}s / {written.LightMap2DSerializationMilliseconds / 1000d:F2}s\n" +
                $"Components replacing existing lighting: {written.ReplacedExistingComponentCount:N0}\n\n" +
                $"{cacheSummary}\n\nUse Save All to write the modified level packages.",
                dialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.FlattenException(), dialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (busy)
            {
                ResetBusyProgress();
                IsBusyTaskbar = false;
                EndBusy();
            }
        }
    }

    #endregion

    #region Navigation

    private readonly NavigationOverlay navigationOverlay = new();

    private float _navigationPawnRadius = 34f;
    public float NavigationPawnRadius { get => _navigationPawnRadius; set => SetProperty(ref _navigationPawnRadius, value); }
    private float _navigationPawnHeight = 90f;
    public float NavigationPawnHeight { get => _navigationPawnHeight; set => SetProperty(ref _navigationPawnHeight, value); }
    private float _navigationGridSpacing = 256f;
    public float NavigationGridSpacing { get => _navigationGridSpacing; set => SetProperty(ref _navigationGridSpacing, value); }
    private float _navigationMaximumSlope = 45f;
    public float NavigationMaximumSlope { get => _navigationMaximumSlope; set => SetProperty(ref _navigationMaximumSlope, value); }
    private float _navigationMaximumStepUp = 35f;
    public float NavigationMaximumStepUp { get => _navigationMaximumStepUp; set => SetProperty(ref _navigationMaximumStepUp, value); }
    private float _navigationMaximumStepDown = 45f;
    public float NavigationMaximumStepDown { get => _navigationMaximumStepDown; set => SetProperty(ref _navigationMaximumStepDown, value); }
    private float _navigationMaximumDrop = 160f;
    public float NavigationMaximumDrop { get => _navigationMaximumDrop; set => SetProperty(ref _navigationMaximumDrop, value); }
    private float _navigationConnectionDistance = 400f;
    public float NavigationConnectionDistance { get => _navigationConnectionDistance; set => SetProperty(ref _navigationConnectionDistance, value); }
    private float _navigationGenerationRadius = 10000f;
    public float NavigationGenerationRadius { get => _navigationGenerationRadius; set => SetProperty(ref _navigationGenerationRadius, value); }
    private bool _generateCover = true;
    public bool GenerateCover { get => _generateCover; set => SetProperty(ref _generateCover, value); }

    private void RefreshNavigationOverlay() => navigationOverlay.Refresh(OpenFiles);

    private async void GenerateNavigation()
    {
        if (!CanMutateScene || ActiveFile is not { IsReadOnly: false } activeFile) return;
        if (!Game.IsGame3())
        {
            MessageBox.Show(this, "Automatic cover serialization currently supports ME3 and LE3 levels.",
                "Navigation Generator", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var settings = new NavigationGenerationSettings
        {
            PawnRadius = NavigationPawnRadius,
            PawnHeight = NavigationPawnHeight,
            GridSpacing = NavigationGridSpacing,
            MaximumSlopeDegrees = NavigationMaximumSlope,
            MaximumStepUp = NavigationMaximumStepUp,
            MaximumStepDown = NavigationMaximumStepDown,
            MaximumSafeDrop = NavigationMaximumDrop,
            ConnectionDistance = NavigationConnectionDistance,
            GenerationRadius = NavigationGenerationRadius,
            GenerateCover = GenerateCover
        };

        bool busy = false;
        try
        {
            settings.Validate();
            SetBusy("Building collision acceleration structure...");
            busy = true;
            IsBusyTaskbar = true;
            ActorProxy[] collisionActors = Actors.ToArray();
            Vector3 generationCenter = RenderContext.Camera.Position;
            var progress = new Progress<string>(message => BusyText = message);
            var generation = await Task.Run(() =>
            {
                LevelCollisionScene collision = LevelCollisionScene.Build(collisionActors);
                NavigationGenerationResult result = new NavigationGenerator(collision, settings).Generate(
                    generationCenter, CancellationToken.None, progress);
                return (Result: result, collision.NavigationSourceCount, collision.CoverSourceCount,
                    collision.NavigationTriangleCount, collision.CoverTriangleCount);
            }).ConfigureAwait(true);
            NavigationGenerationResult generated = generation.Result;

            busy = false;
            IsBusyTaskbar = false;
            EndBusy();
            int mantleConnections = generated.CoverLinks.Sum(link => link.Slots.Count(slot => slot.MantleTargetLink >= 0)) / 2;
            string summary = $"Generated {generated.Nodes.Count:N0} path nodes, " +
                             $"{generated.Edges.Count:N0} directed connections, " +
                             $"{generated.CoverLinks.Count:N0} cover links, and " +
                             $"{generated.CoverLinks.Sum(link => link.Slots.Count):N0} cover slots, including " +
                             $"{mantleConnections:N0} validated vault connections.\n\n" +
                             $"Scanned {generation.CoverSourceCount:N0} mesh/brush components for cover " +
                             $"({generation.CoverTriangleCount:N0} triangles); " +
                             $"{generation.NavigationSourceCount:N0} components provide blocking navigation collision.\n\n" +
                             $"Append these objects to {activeFile.FileName}? Existing navigation is preserved, " +
                             "and the package is not saved to disk until you use Save.";
            if (MessageBox.Show(this, summary, "Navigation Generator", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            if (!CanMutateScene || !OpenFiles.Contains(activeFile) || activeFile.IsReadOnly) return;

            SetBusy("Serializing navigation and cover...");
            busy = true;
            IsBusyTaskbar = true;
            NavigationSerializationResult serialized = NavigationSerializer.Write(activeFile, generated, settings);
            RefreshNavigationOverlay();
            ShowPathing = true;
            ShowCover = GenerateCover;
            TextBelowActors = $"Navigation generator: {serialized.PathNodeCount:N0} nodes, " +
                              $"{serialized.ReachSpecCount:N0} ReachSpecs, " +
                              $"{serialized.CoverLinkCount:N0} cover links / {serialized.CoverSlotCount:N0} slots; " +
                              $"cover geometry from {generation.CoverSourceCount:N0} components.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Navigation Generator", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (busy)
            {
                IsBusyTaskbar = false;
                EndBusy();
            }
        }
    }

    #endregion
}
