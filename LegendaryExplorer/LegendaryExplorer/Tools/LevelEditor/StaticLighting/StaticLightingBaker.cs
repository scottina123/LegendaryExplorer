using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Color = LegendaryExplorerCore.SharpDX.Color;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// CPU direct-light baker for UE3 static mesh components. Package writes are deliberately kept out of
/// this class; generated data is handed to <see cref="StaticLightingWriter"/>, which uses the existing
/// ObjectBinary and texture serializers.
/// </summary>
public sealed class StaticLightingBaker
{
    // Intersections closer to a chart endpoint than one thousandth of a texel at the maximum
    // supported resolution are half-precision UV T-junction noise, not usable overlap area.
    private const float UvBoundaryDistanceTolerance = 1f / (1024f * 1024f);

    private static readonly Vector3[] DirectionalBasis =
    [
        Vector3.Normalize(new Vector3(MathF.Sqrt(2f / 3f), 0f, 1f / MathF.Sqrt(3f))),
        Vector3.Normalize(new Vector3(-1f / MathF.Sqrt(6f), 1f / MathF.Sqrt(2f), 1f / MathF.Sqrt(3f))),
        Vector3.Normalize(new Vector3(-1f / MathF.Sqrt(6f), -1f / MathF.Sqrt(2f), 1f / MathF.Sqrt(3f)))
    ];
    private static readonly ConcurrentDictionary<(int Resolution, int TileSize), StaticLightingBakeTile[]>
        TextureTileCache = new();

    private readonly IReadOnlyList<StaticLightingMeshTarget> targets;
    private readonly IReadOnlyList<StaticLightingLight> lights;
    private readonly LevelCollisionScene collision;
    private readonly StaticLightingGenerationSettings settings;
    private readonly StaticLightingSceneDiagnostics sceneDiagnostics;
    private readonly StaticLightingAreaEmitterIndex emissiveEmitterIndex;

    public StaticLightingBaker(IReadOnlyList<StaticLightingMeshTarget> targets,
        IReadOnlyList<StaticLightingLight> lights, LevelCollisionScene collision,
        StaticLightingGenerationSettings settings, StaticLightingSceneDiagnostics sceneDiagnostics = null,
        StaticLightingAreaEmitterIndex emissiveEmitterIndex = null)
    {
        this.targets = targets;
        this.lights = lights;
        this.collision = collision;
        this.settings = settings;
        this.sceneDiagnostics = sceneDiagnostics ?? new StaticLightingSceneDiagnostics();
        this.emissiveEmitterIndex = emissiveEmitterIndex ?? StaticLightingAreaEmitterIndex.Empty;
        settings.Validate();
    }

    public StaticLightingBakeResult Bake(CancellationToken cancellationToken = default,
        IProgress<string> progress = null)
    {
        ExportEntry duplicateTarget = targets.GroupBy(target => target.Component)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateTarget is not null)
            throw new InvalidOperationException($"Static-lighting target {duplicateTarget.InstancedFullPath} was collected more than once.");

        Stopwatch bakeTimer = Stopwatch.StartNew();
        var targetBounds = new (Vector3 Minimum, Vector3 Maximum)[targets.Count];
        for (int index = 0; index < targets.Count; index++)
        {
            TryCalculateBounds(targets[index].Vertices, out Vector3 minimum, out Vector3 maximum);
            targetBounds[index] = (minimum, maximum);
        }

        long emissiveCullingStart = Stopwatch.GetTimestamp();
        var affectingEmittersByTarget = new StaticLightingAreaEmitter[targets.Count][];
        if (emissiveEmitterIndex.Count == 0)
        {
            Array.Fill(affectingEmittersByTarget, Array.Empty<StaticLightingAreaEmitter>());
        }
        else
        {
            for (int index = 0; index < targets.Count; index++)
            {
                StaticLightingMeshTarget target = targets[index];
                (Vector3 minimum, Vector3 maximum) = targetBounds[index];
                affectingEmittersByTarget[index] = emissiveEmitterIndex.Query(minimum, maximum,
                    target.LightingChannelMask, target.Component);
            }
        }
        double emissiveReceiverCullingMilliseconds =
            TicksToMilliseconds(Stopwatch.GetTimestamp() - emissiveCullingStart);

        var results = new StaticLightingComponentBake[targets.Count];
        int textureMapped = 0;
        int vertexMapped = 0;
        int completed = 0;
        int workUnitCount = 0;
        int outerWorkerCount = Math.Min(settings.EffectiveWorkerThreads, Math.Max(1, targets.Count));
        int componentWorkerCount = Math.Max(1, settings.EffectiveWorkerThreads / outerWorkerCount);
        var parallelOptions = CreateParallelOptions(cancellationToken, outerWorkerCount);
        // Longest-processing-time ordering prevents a few large texture receivers from becoming
        // serial stragglers after all small vertex/texture mappings have completed. Account for
        // lights as well as mapping size: visibility sampling, not rasterization, dominates the
        // expensive architectural receivers.
        int[] workOrder = Enumerable.Range(0, targets.Count)
            .OrderByDescending(index => EstimateTargetWork(targets[index], targetBounds[index], lights,
                affectingEmittersByTarget[index].Length))
            .ToArray();
        // Parallel.For range partitioning can assign broad, distant slices to workers and defeat
        // the LPT order above. Pull one receiver at a time from the ordered sequence so expensive
        // mappings really start first and cannot become the end-of-bake tail.
        Parallel.ForEach(Partitioner.Create(workOrder, EnumerablePartitionerOptions.NoBuffering),
            parallelOptions, index =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            StaticLightingMeshTarget target = targets[index];
            long lightPreparationStart = Stopwatch.GetTimestamp();
            (Vector3 boundsMinimum, Vector3 boundsMaximum) = targetBounds[index];
            var affectingLightList = new List<StaticLightingLight>();
            var irrelevantLightList = new List<StaticLightingLight>();
            foreach (StaticLightingLight light in lights)
            {
                (LightCanAffect(light, target.LightingChannelMask, target.Vertices.Count > 0,
                    boundsMinimum, boundsMaximum) ? affectingLightList : irrelevantLightList).Add(light);
            }
            StaticLightingLight[] affectingLights = affectingLightList.ToArray();
            Guid[] lightGuids = affectingLights.Select(light => light.Guid).Distinct().ToArray();
            var affectingLightGuids = lightGuids.ToHashSet();
            Guid[] irrelevantLightGuids = irrelevantLightList.Select(light => light.Guid)
                .Where(guid => !affectingLightGuids.Contains(guid))
                .Distinct().ToArray();
            var counters = new BakeCounters();
            StaticLightingAreaEmitter[] affectingEmitters = affectingEmittersByTarget[index];
            PreparedLighting preparedLighting = PrepareLighting(affectingLights, affectingEmitters);
            counters.AffectingEmissiveEmitters = affectingEmitters.Length;
            counters.LightPreparationTicks = Stopwatch.GetTimestamp() - lightPreparationStart;
            Stopwatch componentTimer = Stopwatch.StartNew();

            if (target.UseTextureMapping)
            {
                StaticLightingTextureBake texture = BakeTexture(target, preparedLighting, counters,
                    cancellationToken, componentWorkerCount);
                componentTimer.Stop();
                results[index] = new StaticLightingComponentBake
                {
                    Target = target,
                    LightGuids = lightGuids,
                    IrrelevantLightGuids = irrelevantLightGuids,
                    Texture = texture,
                    Diagnostics = counters.CreateDiagnostics(target.MappingDiagnostics, componentTimer.Elapsed.TotalMilliseconds)
                };
                Interlocked.Increment(ref textureMapped);
                Interlocked.Add(ref workUnitCount, texture.WorkUnitCount);
            }
            else
            {
                StaticLightingVertexBake vertex = BakeVertices(target, preparedLighting, counters,
                    cancellationToken, componentWorkerCount);
                componentTimer.Stop();
                results[index] = new StaticLightingComponentBake
                {
                    Target = target,
                    LightGuids = lightGuids,
                    IrrelevantLightGuids = irrelevantLightGuids,
                    Vertex = vertex,
                    Diagnostics = counters.CreateDiagnostics(target.MappingDiagnostics, componentTimer.Elapsed.TotalMilliseconds)
                };
                Interlocked.Increment(ref vertexMapped);
                Interlocked.Add(ref workUnitCount, Math.Max(1, (target.Vertices.Count + 255) / 256));
            }
            int completedCount = Interlocked.Increment(ref completed);
            StaticLightingComponentDiagnostics diagnostic = results[index].Diagnostics;
            string mappingStatus = target.UseTextureMapping
                ? target.MappingDiagnostics.DegenerateUvTriangleCount > 0
                    ? $"UV{target.LightMapCoordinateIndex}, {target.TextureResolution}x{target.TextureResolution}, repaired " +
                      $"{target.MappingDiagnostics.DegenerateUvTriangleCount:N0} degenerate triangle(s), " +
                      $"{diagnostic.MappedTexelCount:N0} texels"
                    : $"UV{target.LightMapCoordinateIndex}, {target.TextureResolution}x{target.TextureResolution}, " +
                      $"{diagnostic.MappedTexelCount:N0} texels"
                : target.HasTextureCoordinates
                    ? "vertex mapping"
                    : diagnostic.Mapping.HasTextureMappingErrors
                    ? $"vertex fallback; UV errors: invalid={diagnostic.Mapping.InvalidUvVertexCount:N0}, " +
                      $"degenerate={diagnostic.Mapping.DegenerateUvTriangleCount:N0}, " +
                      $"overlap pairs={diagnostic.Mapping.OverlappingUvTrianglePairCount:N0}"
                    : "vertex mapping";
            progress?.Report($"Baked static lighting {completedCount:N0}/{targets.Count:N0}: " +
                             $"#{target.Component.UIndex:N0} {target.Component.Parent?.ObjectName.Instanced}." +
                             $"{target.Component.ObjectName.Instanced} ({mappingStatus}; " +
                             $"{affectingLights.Length:N0} lights / {affectingEmitters.Length:N0} emissive samples; " +
                             $"{diagnostic.RaysCast:N0} rays; " +
                             $"visibility {diagnostic.AverageVisibility:P1}; " +
                             $"shadow {diagnostic.ShadowRayMilliseconds / 1000d:F2}s; " +
                             $"total {diagnostic.BakeMilliseconds / 1000d:F2}s)");
        });

        bakeTimer.Stop();
        StaticLightingComponentDiagnostics[] diagnostics = results.Select(result => result.Diagnostics).ToArray();
        long visibilitySampleCount = diagnostics.Sum(item => item.VisibilitySampleCount);

        return new StaticLightingBakeResult
        {
            Components = results,
            SourceTriangleCount = collision.TriangleCount,
            LightCount = lights.Count,
            EmissiveEmitterCount = emissiveEmitterIndex.Count,
            TextureMappedComponentCount = textureMapped,
            VertexMappedComponentCount = vertexMapped,
            WorkUnitCount = workUnitCount,
            WorkerCount = settings.EffectiveWorkerThreads,
            RaysCast = diagnostics.Sum(item => item.RaysCast),
            OccludedSamples = diagnostics.Sum(item => item.OccludedSamples),
            RejectedSelfIntersections = diagnostics.Sum(item => item.RejectedSelfIntersections),
            VisibilitySampleCount = visibilitySampleCount,
            AverageVisibility = visibilitySampleCount == 0 ? 1d :
                diagnostics.Sum(item => item.AverageVisibility * item.VisibilitySampleCount) / visibilitySampleCount,
            AverageDirectContribution = diagnostics.Length == 0 ? 0d : diagnostics.Average(item => item.AverageDirectContribution),
            AverageEnvironmentContribution = diagnostics.Length == 0 ? 0d : diagnostics.Average(item => item.AverageEnvironmentContribution),
            EmissiveSamplesEvaluated = diagnostics.Sum(item => item.EmissiveSamplesEvaluated),
            EmissiveRaysCast = diagnostics.Sum(item => item.EmissiveRaysCast),
            EmissiveReceiverCullingMilliseconds = emissiveReceiverCullingMilliseconds,
            BakeMilliseconds = bakeTimer.Elapsed.TotalMilliseconds,
            SceneDiagnostics = sceneDiagnostics,
            LightPreparationMilliseconds = diagnostics.Sum(item => item.LightPreparationMilliseconds),
            TextureRasterizationMilliseconds = diagnostics.Sum(item => item.TextureRasterizationMilliseconds),
            DirectLightingMilliseconds = diagnostics.Sum(item => item.DirectLightingMilliseconds),
            ShadowRayMilliseconds = diagnostics.Sum(item => item.ShadowRayMilliseconds),
            VertexSamplingMilliseconds = diagnostics.Sum(item => item.VertexSamplingMilliseconds),
            FilteringMilliseconds = diagnostics.Sum(item => item.FilteringMilliseconds),
            OccupiedTexelDiscoveryMilliseconds = diagnostics.Sum(item => item.OccupiedTexelDiscoveryMilliseconds),
            TextureConstructionMilliseconds = diagnostics.Sum(item => item.TextureConstructionMilliseconds)
        };
    }

    private static ParallelOptions CreateParallelOptions(CancellationToken cancellationToken,
        int workerCount) => new()
    {
        CancellationToken = cancellationToken,
        MaxDegreeOfParallelism = workerCount
    };

    private static long EstimateTargetWork(StaticLightingMeshTarget target,
        (Vector3 Minimum, Vector3 Maximum) bounds, IReadOnlyList<StaticLightingLight> lights,
        int affectingEmissiveEmitterCount)
    {
        int affectingLightCount = 0;
        foreach (StaticLightingLight light in lights)
        {
            if (LightCanAffect(light, target.LightingChannelMask, target.Vertices.Count > 0,
                    bounds.Minimum, bounds.Maximum))
                affectingLightCount++;
        }
        long mappingWork = target.UseTextureMapping
            ? (long)Math.Max(1, target.TextureResolution) * Math.Max(1, target.TextureResolution)
            : Math.Max(1, target.Vertices.Count);
        return mappingWork * Math.Max(1, affectingLightCount + affectingEmissiveEmitterCount);
    }

    public static (IReadOnlyList<StaticLightingMeshTarget> Targets, IReadOnlyList<StaticLightingLight> Lights,
        LevelCollisionScene Collision, StaticLightingAreaEmitterIndex EmissiveEmitters,
        StaticLightingSceneDiagnostics Diagnostics) BuildScene(IEnumerable<ActorProxy> actors,
        IReadOnlySet<OpenLevelFile> targetFiles, LevelEditorRenderContext renderContext,
        IReadOnlySet<ExportEntry> exactTargetComponents = null,
        StaticLightingMappingMode mappingMode = StaticLightingMappingMode.Automatic,
        int maximumTextureResolution = 64)
    {
        long extractionStart = Stopwatch.GetTimestamp();
        ActorProxy[] actorArray = actors.ToArray();
        long extractionTicks = Stopwatch.GetTimestamp() - extractionStart;
        long lightGatheringTicks = 0;
        long meshPreparationTicks = 0;
        long receiverPreparationTicks = 0;
        var targets = new List<StaticLightingMeshTarget>();
        var occluders = new List<(Vector3 A, Vector3 B, Vector3 C, ExportEntry Source, int SourceTriangleIndex)>();
        var lights = new List<StaticLightingLight>();
        var mappingDiagnosticsCache = new Dictionary<(ExportEntry Mesh, int CoordinateIndex),
            StaticLightingMappingDiagnostics>();
        var stockAtlasResolutions = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var textureDimensions = new Dictionary<ExportEntry, (int Width, int Height)>();
        var materialCompatibilityCache = new Dictionary<ExportEntry, (bool Compatible, string MaterialPath)>();
        Dictionary<ExportEntry, (bool Emissive, Vector3 Radiance)> emissiveMaterialCache = null;
        var excludedUnlitReceivers = new List<StaticLightingExcludedReceiver>();
        var areaEmitters = new List<StaticLightingAreaEmitter>();
        int emissiveSourceTriangleCount = 0;
        long emissivePreprocessingTicks = 0;

        foreach (ActorProxy actor in actorArray)
        {
            long lightStart = Stopwatch.GetTimestamp();
            if (TryCreateLight(actor, out StaticLightingLight light))
                lights.Add(light);
            lightGatheringTicks += Stopwatch.GetTimestamp() - lightStart;

            if (actor.IsVolumetricMesh || actor is EmitterActorProxy or PawnProxy)
                continue;

            foreach (StaticMeshComponentProxy component in actor.Components.OfType<StaticMeshComponentProxy>())
            {
                long meshStart = Stopwatch.GetTimestamp();
                if (!TryGetMesh(component, renderContext, out ExportEntry meshExport, out StaticMesh mesh) ||
                    mesh.LODModels is not { Length: > 0 })
                {
                    meshPreparationTicks += Stopwatch.GetTimestamp() - meshStart;
                    continue;
                }

                StaticMeshRenderData lod = mesh.LODModels[0];
                IReadOnlyList<StaticLightingTriangle> triangles = BuildTrianglesWithRuntimeLightMapCoordinate(
                    meshExport, lod, component.LocalToWorld, mappingDiagnosticsCache,
                    out int lightMapCoordinateIndex,
                    out StaticLightingVertex[] vertices, out bool hasTextureCoordinates,
                    out StaticLightingMappingDiagnostics mappingDiagnostics);
                meshPreparationTicks += Stopwatch.GetTimestamp() - meshStart;
                if (triangles.Count == 0 || vertices.Length == 0)
                    continue;

                bool isStaticCandidate = IsStaticLightingCandidate(component);
                if (CastsStaticShadow(component.Properties) &&
                    CastsStaticShadow(component.Actor.Export.GetCondensedProperties()))
                {
                    foreach (StaticLightingTriangle triangle in triangles)
                        occluders.Add((triangle.A.Position, triangle.B.Position, triangle.C.Position,
                            component.Export, triangle.SourceTriangleIndex));
                }
                if (StaticLightingEmissive.TryGetSettings(component.Properties,
                        out StaticLightingEmissiveSettings emissiveSettings))
                {
                    long emissiveStart = Stopwatch.GetTimestamp();
                    emissiveMaterialCache ??= new Dictionary<ExportEntry, (bool Emissive, Vector3 Radiance)>();
                    emissiveSourceTriangleCount += CollectEmissiveAreaEmitters(component, meshExport, lod,
                        triangles, renderContext, emissiveSettings, emissiveMaterialCache, areaEmitters);
                    emissivePreprocessingTicks += Stopwatch.GetTimestamp() - emissiveStart;
                }
                bool canReceiveLighting = exactTargetComponents is null
                    ? IsStaticLightingTarget(component)
                    : isStaticCandidate && exactTargetComponents.Contains(component.Export);
                if (component.Actor.OwningFile is not { } owningFile || !targetFiles.Contains(owningFile) ||
                    !canReceiveLighting)
                    continue;

                long receiverStart = Stopwatch.GetTimestamp();
                // A component whose resolved sections are all unlit can still cast shadows without being
                // a receiver. Mixed meshes remain receivers: their lit sections use the component mapping
                // while UE3's unlit sections ignore it.
                if (!HasCompatibleReceiverMaterials(component, meshExport, lod, renderContext,
                        materialCompatibilityCache, out string incompatibleMaterialPath))
                {
                    excludedUnlitReceivers.Add(new StaticLightingExcludedReceiver
                    {
                        File = owningFile,
                        Component = component.Export,
                        MaterialPath = incompatibleMaterialPath
                    });
                    receiverPreparationTicks += Stopwatch.GetTimestamp() - receiverStart;
                    continue;
                }

                StaticMeshComponent componentBinary = component.Export.GetBinaryData<StaticMeshComponent>();
                ELightMapType existingMappingType = GetExistingMappingType(componentBinary);
                bool generatedMapping = IsGeneratedTextureMapping(component.Export, componentBinary);
                bool hasResolutionOverride =
                    component.Properties.GetProp<BoolProperty>("bOverrideLightMapRes")?.Value == true;
                int effectiveLightMapResolution = GetEffectiveLightMapResolution(component, meshExport);
                int stockAtlasResolution = generatedMapping ? 0 :
                    GetExistingTextureMappingResolution(component.Export, componentBinary, textureDimensions);
                if (stockAtlasResolution > 0)
                {
                    if (!stockAtlasResolutions.TryGetValue(meshExport.InstancedFullPath, out List<int> resolutions))
                        stockAtlasResolutions.Add(meshExport.InstancedFullPath, resolutions = []);
                    resolutions.Add(stockAtlasResolution);
                    effectiveLightMapResolution = stockAtlasResolution;
                }
                CalculateReceiverMetrics(vertices, triangles, out float maximumWorldDimension,
                    out float surfaceArea);
                bool useTextureMapping = ShouldUseTextureMapping(mappingMode, hasTextureCoordinates,
                    effectiveLightMapResolution, existingMappingType, generatedMapping,
                    meshExport.InstancedFullPath, maximumWorldDimension, surfaceArea, triangles.Count);
                int textureResolution = useTextureMapping
                    ? ResolveTextureResolution(mappingMode, exactTargetComponents is not null,
                        effectiveLightMapResolution, maximumTextureResolution)
                    : 0;
                targets.Add(new StaticLightingMeshTarget
                {
                    File = owningFile,
                    Component = component.Export,
                    ComponentBinary = componentBinary,
                    MeshLod = lod,
                    LocalToWorld = component.LocalToWorld,
                    LightingChannelMask = component.LightingChannelMask,
                    Triangles = triangles,
                    Vertices = vertices,
                    LightMapCoordinateIndex = lightMapCoordinateIndex,
                    AuthoredLightMapResolution = effectiveLightMapResolution,
                    StockAtlasLightMapResolution = stockAtlasResolution,
                    HasExplicitLightMapResolutionOverride = hasResolutionOverride,
                    TextureResolution = textureResolution,
                    HasTextureCoordinates = hasTextureCoordinates,
                    UseTextureMapping = useTextureMapping,
                    MappingDiagnostics = mappingDiagnostics
                });
                receiverPreparationTicks += Stopwatch.GetTimestamp() - receiverStart;
            }
        }

        // Some cooked instances have no mapping while another instance of the same mesh retains the
        // stock atlas allocation. Use that real allocation instead of unreliable mesh properties such
        // as WB_Plane_02's declared 2048 resolution for an authored 14x14 mapping.
        var representativeAtlasResolutions = stockAtlasResolutions.ToDictionary(pair => pair.Key, pair =>
        {
            pair.Value.Sort();
            return pair.Value[pair.Value.Count / 2];
        }, StringComparer.OrdinalIgnoreCase);
        foreach (StaticLightingMeshTarget target in targets)
        {
            int stockAtlasResolution = target.StockAtlasLightMapResolution;
            if (stockAtlasResolution <= 0 && !target.HasExplicitLightMapResolutionOverride &&
                !representativeAtlasResolutions.TryGetValue(target.MappingDiagnostics.MeshPath,
                    out stockAtlasResolution)) continue;
            if (stockAtlasResolution <= 0) continue;
            target.AuthoredLightMapResolution = stockAtlasResolution;
            if (target.UseTextureMapping)
                target.TextureResolution = ResolveTextureResolution(mappingMode,
                    exactTargetComponents is not null, stockAtlasResolution, maximumTextureResolution);
        }

        LevelCollisionScene collision = LevelCollisionScene.FromTriangles(occluders);
        StaticLightingAreaEmitterIndex emissiveEmitters = areaEmitters.Count == 0
            ? StaticLightingAreaEmitterIndex.Empty
            : new StaticLightingAreaEmitterIndex(areaEmitters);
        var diagnostics = new StaticLightingSceneDiagnostics
        {
            SceneExtractionMilliseconds = TicksToMilliseconds(extractionTicks),
            LightGatheringMilliseconds = TicksToMilliseconds(lightGatheringTicks),
            MeshPreparationMilliseconds = TicksToMilliseconds(meshPreparationTicks),
            ReceiverPreparationMilliseconds = TicksToMilliseconds(receiverPreparationTicks),
            BvhConstructionMilliseconds = collision.BvhBuildMilliseconds,
            BvhNodeCount = collision.BvhNodeCount,
            UniquePreparedMeshCount = mappingDiagnosticsCache.Keys.Select(key => key.Mesh).Distinct().Count(),
            EmissiveSourceTriangleCount = emissiveSourceTriangleCount,
            AreaEmitterSampleCount = emissiveEmitters.Count,
            AreaEmitterBvhNodeCount = emissiveEmitters.NodeCount,
            EmissivePreprocessingMilliseconds = TicksToMilliseconds(emissivePreprocessingTicks) +
                                               emissiveEmitters.BuildMilliseconds,
            ExcludedUnlitReceivers = excludedUnlitReceivers
        };
        return (targets, lights, collision, emissiveEmitters, diagnostics);
    }

    private StaticLightingTextureBake BakeTexture(StaticLightingMeshTarget target,
        PreparedLighting lighting, BakeCounters counters, CancellationToken cancellationToken,
        int workerCount)
    {
        int resolution = target.TextureResolution > 0 ? target.TextureResolution : settings.TextureResolution;
        int pixelCount = resolution * resolution;
        bool useCompressedDirectionalLightMap = UsesCompressedDirectionalLightMap(target.Component.Game);
        int coefficientCount = useCompressedDirectionalLightMap ? 3 : 4;
        var coefficients = Enumerable.Range(0, coefficientCount)
            .Select(_ => new Vector3[pixelCount]).ToArray();
        var mapped = new bool[pixelCount];
        var mappingConflicts = new bool[pixelCount];
        var triangleOwners = new int[pixelCount];
        Array.Fill(triangleOwners, -1);
        Vector2 coordinateScale = new((resolution - 2f) / resolution);
        Vector2 coordinateBias = new(1f / resolution);
        var samples = new StaticLightingSurfaceSample[pixelCount];
        StaticLightingBakeTile[] tiles = TextureTileCache.GetOrAdd((resolution, settings.WorkTileSize), key =>
            CreateTextureWorkTiles(key.Resolution, key.TileSize).ToArray());
        List<int>[] triangleBuckets = BuildTileTriangleBuckets(target.Triangles, tiles, resolution,
            coordinateScale, coordinateBias, settings.WorkTileSize);
        var geometricNormals = new Vector3[target.Triangles.Count];
        var worldUnitsPerTexel = new float[target.Triangles.Count];
        for (int triangleIndex = 0; triangleIndex < target.Triangles.Count; triangleIndex++)
        {
            StaticLightingTriangle triangle = target.Triangles[triangleIndex];
            geometricNormals[triangleIndex] = SafeNormal(Vector3.Cross(
                triangle.B.Position - triangle.A.Position, triangle.C.Position - triangle.A.Position),
                Vector3.UnitZ);
            worldUnitsPerTexel[triangleIndex] = EstimateTriangleWorldUnitsPerTexel(triangle, resolution);
        }
        int[] activeTiles = Enumerable.Range(0, tiles.Length)
            .Where(index => triangleBuckets[index].Count > 0).ToArray();
        Parallel.ForEach(activeTiles, CreateParallelOptions(cancellationToken, workerCount), tileIndex =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localCounters = new BakeCounterValues();
            int localMappingConflicts = 0;
            StaticLightingBakeTile tile = tiles[tileIndex];
            long rasterizationStart = Stopwatch.GetTimestamp();
            foreach (int triangleIndex in triangleBuckets[tileIndex])
            {
                StaticLightingTriangle triangle = target.Triangles[triangleIndex];
                localMappingConflicts += RasterizeTriangle(triangle, triangleIndex, resolution,
                    coordinateScale, coordinateBias, tile, target.Component,
                    geometricNormals[triangleIndex], worldUnitsPerTexel[triangleIndex], samples,
                    triangleOwners, mapped, mappingConflicts);
            }
            localCounters.TextureRasterizationTicks += Stopwatch.GetTimestamp() - rasterizationStart;

            long directLightingStart = Stopwatch.GetTimestamp();
            for (int y = tile.MinimumY; y < tile.MaximumY; y++)
            for (int x = tile.MinimumX; x < tile.MaximumX; x++)
            {
                int pixelIndex = y * resolution + x;
                if (!mapped[pixelIndex]) continue;
                EvaluateLighting(samples[pixelIndex], lighting, coefficients, pixelIndex,
                    useCompressedDirectionalLightMap, ref localCounters);
            }
            localCounters.DirectLightingTicks += Stopwatch.GetTimestamp() - directLightingStart;
            localCounters.MappingConflictTexels += localMappingConflicts;
            counters.Merge(localCounters);
        });

        long occupiedTexelStart = Stopwatch.GetTimestamp();
        int mappedTexels = 0;
        foreach (bool isMapped in mapped)
            if (isMapped) mappedTexels++;
        counters.MappedTexels = mappedTexels;
        counters.OccupiedTexelDiscoveryTicks += Stopwatch.GetTimestamp() - occupiedTexelStart;

        long filteringStart = Stopwatch.GetTimestamp();
        DilateCoefficients(coefficients, mapped, resolution, 4, cancellationToken, workerCount);
        counters.FilteringTicks += Stopwatch.GetTimestamp() - filteringStart;
        long textureConstructionStart = Stopwatch.GetTimestamp();
        var images = new List<byte[]>(coefficients.Length);
        var scales = new List<Vector3>(coefficients.Length);
        foreach (Vector3[] coefficient in coefficients)
        {
            Vector3 scale = CalculateScale(coefficient, mapped);
            scales.Add(scale);
            images.Add(EncodeColorImage(coefficient, scale, cancellationToken, workerCount));
        }
        counters.TextureConstructionTicks += Stopwatch.GetTimestamp() - textureConstructionStart;

        return new StaticLightingTextureBake
        {
            Resolution = resolution,
            CoefficientImages = images,
            ScaleVectors = scales,
            // Direct-light visibility is already ray traced into the lightmap coefficients. Emitting
            // another runtime shadow map for those same lights double-applies their baked shadows.
            ShadowMaps = [],
            CoordinateScale = coordinateScale,
            CoordinateBias = coordinateBias,
            WorkUnitCount = Math.Max(1, activeTiles.Length)
        };
    }

    private StaticLightingVertexBake BakeVertices(StaticLightingMeshTarget target,
        PreparedLighting lighting, BakeCounters counters, CancellationToken cancellationToken,
        int workerCount)
    {
        bool useCompressedDirectionalLightMap = UsesCompressedDirectionalLightMap(target.Component.Game);
        int coefficientCount = useCompressedDirectionalLightMap ? 3 : 4;
        var coefficients = Enumerable.Range(0, coefficientCount)
            .Select(_ => new Vector3[target.Vertices.Count]).ToArray();
        float vertexShadowScale = EstimateVertexShadowScale(target);
        Parallel.ForEach(Partitioner.Create(0, target.Vertices.Count, 256),
            CreateParallelOptions(cancellationToken, workerCount), range =>
        {
            var localCounters = new BakeCounterValues();
            long samplingStart = Stopwatch.GetTimestamp();
            for (int vertexIndex = range.Item1; vertexIndex < range.Item2; vertexIndex++)
            {
                StaticLightingVertex vertex = target.Vertices[vertexIndex];
                var sample = new StaticLightingSurfaceSample(vertex.Position, vertex.Normal, vertex.Tangent,
                    vertex.Bitangent, vertex.Normal, target.Component, -1, vertexShadowScale);
                EvaluateLighting(sample, lighting, coefficients, vertexIndex,
                    useCompressedDirectionalLightMap, ref localCounters);
            }
            long samplingTicks = Stopwatch.GetTimestamp() - samplingStart;
            localCounters.VertexSamplingTicks += samplingTicks;
            localCounters.DirectLightingTicks += samplingTicks;
            counters.Merge(localCounters);
        });

        long textureConstructionStart = Stopwatch.GetTimestamp();
        var scales = coefficients.Select(coefficient => CalculateScale(coefficient, null)).ToArray();
        var directionalSamples = new QuantizedDirectionalLightSample[target.Vertices.Count];
        var simpleSamples = new QuantizedSimpleLightSample[target.Vertices.Count];
        for (int index = 0; index < target.Vertices.Count; index++)
        {
            if (useCompressedDirectionalLightMap)
            {
                directionalSamples[index] = new QuantizedDirectionalLightSample
                {
                    Coefficient2 = ToColor(coefficients[0][index], scales[0]),
                    Coefficient3 = ToColor(coefficients[1][index], scales[1])
                };
            }
            else
            {
                directionalSamples[index] = new QuantizedDirectionalLightSample
                {
                    Coefficient1 = ToColor(coefficients[0][index], scales[0]),
                    Coefficient2 = ToColor(coefficients[1][index], scales[1]),
                    Coefficient3 = ToColor(coefficients[2][index], scales[2])
                };
            }
            int simpleIndex = coefficients.Length - 1;
            simpleSamples[index] = new QuantizedSimpleLightSample
            {
                Coefficient = ToColor(coefficients[simpleIndex][index], scales[simpleIndex])
            };
        }
        counters.TextureConstructionTicks += Stopwatch.GetTimestamp() - textureConstructionStart;

        return new StaticLightingVertexBake
        {
            DirectionalSamples = directionalSamples,
            SimpleSamples = simpleSamples,
            ScaleVectors = scales,
            ShadowMaps = []
        };
    }

    private void EvaluateLighting(StaticLightingSurfaceSample sample, PreparedLighting lighting,
        Vector3[][] coefficients, int sampleIndex, bool useCompressedDirectionalLightMap,
        ref BakeCounterValues counters)
    {
        // Environment/indirect energy is accumulated independently and is never attenuated by a
        // direct light's visibility. This prevents several occluders or lights from stacking black.
        Vector3 environment = lighting.Environment;
        Vector3 simple = environment;
        Vector3 totalDirect = Vector3.Zero;
        float isotropicMaximum = MaxComponent(environment);
        Span<Vector3> directional = stackalloc Vector3[3];
        directional.Clear();
        Span<Vector3> sampledDirectional = stackalloc Vector3[3];
        float epsilon = CalculateShadowEpsilon(sample);
        Vector3 origin = sample.Position + GetReceiverGeometricNormal(sample) * epsilon;

        foreach (PreparedLight light in lighting.DirectLights)
        {
            Vector3 sampledIrradiance = Vector3.Zero;
            sampledDirectional.Clear();
            Vector3 sourceRight = default;
            Vector3 sourceUp = default;
            if (light.DiskSamples is not null &&
                light.Type is StaticLightingLightType.Point or StaticLightingLightType.Spot)
            {
                Vector3 lightToReceiver = SafeNormal(sample.Position - light.Position, light.Direction);
                CreateOrthonormalBasis(lightToReceiver, out sourceRight, out sourceUp);
            }

            for (int lightSampleIndex = 0; lightSampleIndex < light.SampleCount; lightSampleIndex++)
            {
                Vector3 surfaceToLight;
                Vector3 unshadowed;
                Vector3 irradiance;
                float lightDistance;
                if (light.Type == StaticLightingLightType.Directional)
                {
                    surfaceToLight = light.DirectionalSurfaceToLight[lightSampleIndex];
                    float normalDotLight = MathF.Max(0f, Vector3.Dot(sample.Normal, surfaceToLight));
                    if (normalDotLight <= 0f)
                        continue;
                    unshadowed = light.Radiance;
                    irradiance = unshadowed * normalDotLight;
                    lightDistance = 10_000_000f;
                }
                else
                {
                    Vector3 sampledPosition = light.Position;
                    if (light.DiskSamples is not null)
                    {
                        Vector2 disk = light.DiskSamples[lightSampleIndex];
                        sampledPosition += (sourceRight * disk.X + sourceUp * disk.Y) * light.SourceRadius;
                    }
                    if (!TryEvaluatePreparedLocalLight(light, sampledPosition, sample,
                            out surfaceToLight, out unshadowed, out irradiance, out lightDistance))
                        continue;
                }

                bool visible = true;
                if (light.CastsShadow)
                {
                    float maximumDistance = light.Type == StaticLightingLightType.Directional
                        ? lightDistance
                        : MathF.Max(epsilon, lightDistance - epsilon * 2f);
                    counters.RaysCast++;
                    long shadowStart = Stopwatch.GetTimestamp();
                    bool occluded = collision.IsOccludedFilteredNormalized(origin, surfaceToLight, maximumDistance,
                        sample.Source, sample.SourceTriangleIndex, epsilon * 4f,
                        out int rejectedSelfIntersections);
                    counters.ShadowRayTicks += Stopwatch.GetTimestamp() - shadowStart;
                    counters.RejectedSelfIntersections += rejectedSelfIntersections;
                    counters.VisibilitySampleCount++;
                    if (occluded)
                    {
                        counters.OccludedSamples++;
                        visible = false;
                    }
                    else
                    {
                        counters.VisibilityMicroSum += BakeCounters.MicroScale;
                    }
                }
                if (!visible)
                    continue;

                sampledIrradiance += irradiance;
                Vector3 tangentDirection = new(
                    Vector3.Dot(surfaceToLight, sample.Tangent),
                    Vector3.Dot(surfaceToLight, sample.Bitangent),
                    Vector3.Dot(surfaceToLight, sample.Normal));
                tangentDirection = SafeNormal(tangentDirection, Vector3.UnitZ);
                for (int basisIndex = 0; basisIndex < sampledDirectional.Length; basisIndex++)
                {
                    sampledDirectional[basisIndex] += unshadowed * MathF.Max(0f,
                        Vector3.Dot(tangentDirection, DirectionalBasis[basisIndex]));
                }
            }

            float inverseSampleCount = 1f / light.SampleCount;
            Vector3 direct = sampledIrradiance * inverseSampleCount;
            simple += direct;
            totalDirect += direct;
            for (int basisIndex = 0; basisIndex < directional.Length; basisIndex++)
                directional[basisIndex] += sampledDirectional[basisIndex] * inverseSampleCount;
        }

        // Receiver-level BVH culling has already reduced this to a small bounded array. The hot loop
        // performs only the exact geometric test and casts a ray when the contribution is visible.
        foreach (StaticLightingAreaEmitter emitter in lighting.AreaEmitters)
        {
            counters.EmissiveSamplesEvaluated++;
            if (!TryEvaluateAreaEmitter(emitter, sample, out Vector3 surfaceToEmitter,
                    out Vector3 unshadowed, out Vector3 irradiance, out float emitterDistance))
                continue;
            counters.RaysCast++;
            counters.EmissiveRaysCast++;
            long shadowStart = Stopwatch.GetTimestamp();
            bool occluded = collision.IsOccludedFilteredNormalized(origin, surfaceToEmitter,
                MathF.Max(epsilon, emitterDistance - epsilon * 2f), sample.Source,
                sample.SourceTriangleIndex, epsilon * 4f, out int rejectedSelfIntersections);
            counters.ShadowRayTicks += Stopwatch.GetTimestamp() - shadowStart;
            counters.RejectedSelfIntersections += rejectedSelfIntersections;
            counters.VisibilitySampleCount++;
            if (occluded)
            {
                counters.OccludedSamples++;
                continue;
            }
            counters.VisibilityMicroSum += BakeCounters.MicroScale;
            simple += irradiance;
            totalDirect += irradiance;
            Vector3 tangentDirection = SafeNormal(new Vector3(
                Vector3.Dot(surfaceToEmitter, sample.Tangent),
                Vector3.Dot(surfaceToEmitter, sample.Bitangent),
                Vector3.Dot(surfaceToEmitter, sample.Normal)), Vector3.UnitZ);
            for (int basisIndex = 0; basisIndex < directional.Length; basisIndex++)
            {
                directional[basisIndex] += unshadowed * MathF.Max(0f,
                    Vector3.Dot(tangentDirection, DirectionalBasis[basisIndex]));
            }
        }

        counters.LitSampleCount++;
        counters.DirectContributionMicroSum += ToMicro(MaxComponent(totalDirect));
        counters.EnvironmentContributionMicroSum += ToMicro(MaxComponent(environment));

        if (useCompressedDirectionalLightMap)
        {
            float maximumColor = MaxComponent(simple);
            if (maximumColor > 0.000001f)
            {
                coefficients[0][sampleIndex] = simple / maximumColor;
                Vector3 directionalMaximums = new(
                    MaxComponent(directional[0]) + isotropicMaximum,
                    MaxComponent(directional[1]) + isotropicMaximum,
                    MaxComponent(directional[2]) + isotropicMaximum);

                // LE3's directional texture is not a signed vector. Its RGB channels are the maximum
                // light intensities along UE3's three lightmap bases. The game weights those channels
                // by the squared normal response; a flat tangent-space normal gives each channel 1/3.
                // Normalize that reconstruction to the simple irradiance maximum so the directional
                // policy cannot turn a correctly lit surface black.
                float flatNormalResponse = (directionalMaximums.X + directionalMaximums.Y +
                                            directionalMaximums.Z) / 3f;
                coefficients[1][sampleIndex] = flatNormalResponse > 0.000001f
                    ? directionalMaximums * (maximumColor / flatNormalResponse)
                    : new Vector3(maximumColor);
            }
            else
            {
                coefficients[0][sampleIndex] = Vector3.Zero;
                coefficients[1][sampleIndex] = Vector3.Zero;
            }
            coefficients[2][sampleIndex] = Vector3.Max(Vector3.Zero, simple);
            return;
        }

        for (int index = 0; index < directional.Length; index++)
            coefficients[index][sampleIndex] = Vector3.Max(Vector3.Zero, directional[index]);
        coefficients[^1][sampleIndex] = Vector3.Max(Vector3.Zero, simple);
    }

    private int GetLightSampleCount(StaticLightingLight light)
    {
        if (!light.CastsShadow || settings.ShadowSampleCount <= 1)
            return 1;
        return light.Type switch
        {
            StaticLightingLightType.Directional when settings.DirectionalSourceAngleDegrees > 0f =>
                settings.ShadowSampleCount,
            StaticLightingLightType.Point or StaticLightingLightType.Spot
                when GetEffectiveSourceRadius(light) > 0f => settings.ShadowSampleCount,
            _ => 1
        };
    }

    private PreparedLighting PrepareLighting(IReadOnlyList<StaticLightingLight> affectingLights,
        StaticLightingAreaEmitter[] affectingEmitters)
    {
        Vector3 environment = new(settings.AmbientIntensity);
        var directLights = new List<PreparedLight>(affectingLights.Count);
        foreach (StaticLightingLight light in affectingLights)
        {
            if (light.Type == StaticLightingLightType.Sky)
            {
                environment += Vector3.Max(Vector3.Zero, light.Color * light.Intensity);
                continue;
            }
            int sampleCount = GetLightSampleCount(light);
            Vector2[] diskSamples = sampleCount > 1 ? CreateDiskSamples(light.Guid, sampleCount) : null;
            Vector3 direction = SafeNormal(light.Direction, Vector3.UnitX);
            Vector3[] directionalSurfaceToLight = null;
            if (light.Type == StaticLightingLightType.Directional)
            {
                directionalSurfaceToLight = new Vector3[sampleCount];
                if (sampleCount == 1)
                {
                    directionalSurfaceToLight[0] = SafeNormal(-light.Direction, Vector3.UnitZ);
                }
                else
                {
                    CreateOrthonormalBasis(direction, out Vector3 right, out Vector3 up);
                    float tangent = MathF.Tan(settings.DirectionalSourceAngleDegrees * MathF.PI / 180f);
                    for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
                    {
                        Vector2 disk = diskSamples[sampleIndex];
                        Vector3 sampledDirection = SafeNormal(
                            direction + (right * disk.X + up * disk.Y) * tangent, direction);
                        directionalSurfaceToLight[sampleIndex] = SafeNormal(-sampledDirection, Vector3.UnitZ);
                    }
                }
            }
            float outerCos = MathF.Cos(light.OuterConeAngleDegrees * MathF.PI / 180f);
            float innerCos = MathF.Cos(light.InnerConeAngleDegrees * MathF.PI / 180f);
            directLights.Add(new PreparedLight
            {
                Type = light.Type,
                Position = light.Position,
                Direction = direction,
                Radiance = Vector3.Max(Vector3.Zero, light.Color * light.Intensity),
                RadiusSquared = light.Radius * light.Radius,
                InverseRadius = light.Radius > 0f ? 1f / light.Radius : 0f,
                OuterConeCos = outerCos,
                InverseConeRange = 1f / MathF.Max(0.0001f, innerCos - outerCos),
                CastsShadow = light.CastsShadow,
                SampleCount = sampleCount,
                SourceRadius = GetEffectiveSourceRadius(light),
                DiskSamples = diskSamples,
                DirectionalSurfaceToLight = directionalSurfaceToLight
            });
        }
        return new PreparedLighting(environment, directLights.ToArray(), affectingEmitters);
    }

    private static Vector2[] CreateDiskSamples(Guid guid, int sampleCount)
    {
        var samples = new Vector2[sampleCount];
        float seedAngle = (uint)guid.GetHashCode() * (2f * MathF.PI / uint.MaxValue);
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            float radius = MathF.Sqrt((sampleIndex + 0.5f) / sampleCount);
            float angle = seedAngle + sampleIndex * 2.39996323f;
            samples[sampleIndex] = new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
        }
        return samples;
    }

    private static bool TryEvaluatePreparedLocalLight(PreparedLight light, Vector3 sampledPosition,
        StaticLightingSurfaceSample sample, out Vector3 surfaceToLight, out Vector3 unshadowedRadiance,
        out Vector3 irradiance, out float distance)
    {
        Vector3 delta = sampledPosition - sample.Position;
        float distanceSquared = delta.LengthSquared();
        if (distanceSquared <= 0.0001f || distanceSquared >= light.RadiusSquared)
        {
            surfaceToLight = default;
            unshadowedRadiance = default;
            irradiance = default;
            distance = 0f;
            return false;
        }
        distance = MathF.Sqrt(distanceSquared);
        surfaceToLight = delta / distance;
        float normalizedDistance = distance * light.InverseRadius;
        float attenuation = MathF.Max(0f, 1f - normalizedDistance * normalizedDistance);
        attenuation *= attenuation;
        if (light.Type == StaticLightingLightType.Spot)
        {
            float coneDot = Vector3.Dot(-surfaceToLight, light.Direction);
            if (coneDot <= light.OuterConeCos)
            {
                unshadowedRadiance = default;
                irradiance = default;
                return false;
            }
            attenuation *= Math.Clamp((coneDot - light.OuterConeCos) * light.InverseConeRange, 0f, 1f);
        }
        float normalDotLight = MathF.Max(0f, Vector3.Dot(sample.Normal, surfaceToLight));
        if (normalDotLight <= 0f)
        {
            unshadowedRadiance = default;
            irradiance = default;
            return false;
        }
        unshadowedRadiance = light.Radiance * attenuation;
        irradiance = unshadowedRadiance * normalDotLight;
        return true;
    }

    public static bool TryEvaluateAreaEmitter(StaticLightingAreaEmitter emitter,
        StaticLightingSurfaceSample sample, out Vector3 surfaceToEmitter,
        out Vector3 unshadowedRadiance, out Vector3 irradiance, out float distance)
    {
        Vector3 delta = emitter.Position - sample.Position;
        float distanceSquared = delta.LengthSquared();
        float radiusSquared = emitter.InfluenceRadius * emitter.InfluenceRadius;
        if (!float.IsFinite(distanceSquared) || distanceSquared <= 0.0001f || distanceSquared >= radiusSquared)
        {
            surfaceToEmitter = default;
            unshadowedRadiance = default;
            irradiance = default;
            distance = 0f;
            return false;
        }

        distance = MathF.Sqrt(distanceSquared);
        surfaceToEmitter = delta / distance;
        float receiverCosine = MathF.Max(0f, Vector3.Dot(sample.Normal, surfaceToEmitter));
        float emitterCosine = Vector3.Dot(emitter.Normal, -surfaceToEmitter);
        emitterCosine = emitter.TwoSided ? MathF.Abs(emitterCosine) : MathF.Max(0f, emitterCosine);
        if (receiverCosine <= 0f || emitterCosine <= 0f)
        {
            unshadowedRadiance = default;
            irradiance = default;
            return false;
        }

        float normalizedDistance = distance / emitter.InfluenceRadius;
        float falloffBase = MathF.Max(0f, 1f - normalizedDistance);
        float falloff = emitter.FalloffExponent == 2f
            ? falloffBase * falloffBase
            : emitter.FalloffExponent == 1f
                ? falloffBase
                : MathF.Pow(falloffBase, emitter.FalloffExponent);
        // The bounded solid-angle approximation converges to inverse-square behavior at distance
        // without exploding when a receiver is close to a large representative sample.
        float solidAngle = emitter.Area / (MathF.PI * distanceSquared + emitter.Area);
        unshadowedRadiance = emitter.Radiance * (emitterCosine * solidAngle * falloff);
        irradiance = unshadowedRadiance * receiverCosine;
        if (MaxComponent(irradiance) < StaticLightingEmissive.MinimumContribution)
        {
            unshadowedRadiance = default;
            irradiance = default;
            return false;
        }
        return true;
    }

    private float GetEffectiveSourceRadius(StaticLightingLight light)
    {
        float sourceRadius = light.SourceRadius > 0f ? light.SourceRadius : settings.DefaultLightSourceRadius;
        return light.Type is StaticLightingLightType.Point or StaticLightingLightType.Spot
            ? MathF.Min(sourceRadius, MathF.Max(0f, light.Radius * 0.25f))
            : sourceRadius;
    }

    private float CalculateShadowEpsilon(StaticLightingSurfaceSample sample) => MathF.Max(settings.ShadowBias,
        MathF.Max(0.01f, sample.WorldUnitsPerTexel * 0.02f));

    private static Vector3 GetReceiverGeometricNormal(StaticLightingSurfaceSample sample)
    {
        Vector3 geometric = SafeNormal(sample.GeometricNormal, sample.Normal);
        return Vector3.Dot(geometric, sample.Normal) < 0f ? -geometric : geometric;
    }

    private static void CreateOrthonormalBasis(Vector3 normal, out Vector3 right, out Vector3 up)
    {
        Vector3 helper = MathF.Abs(normal.Z) < 0.999f ? Vector3.UnitZ : Vector3.UnitY;
        right = SafeNormal(Vector3.Cross(helper, normal), Vector3.UnitX);
        up = SafeNormal(Vector3.Cross(normal, right), Vector3.UnitY);
    }

    private static long ToMicro(float value) => (long)MathF.Round(MathF.Max(0f, value) * BakeCounters.MicroScale);

    private static double TicksToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private static bool UsesCompressedDirectionalLightMap(MEGame game) => game >= MEGame.ME3;

    private static float MaxComponent(Vector3 value) => MathF.Max(value.X, MathF.Max(value.Y, value.Z));

    public static bool TryEvaluateLight(StaticLightingLight light, StaticLightingSurfaceSample sample,
        out Vector3 surfaceToLight, out Vector3 unshadowedRadiance, out Vector3 irradiance)
    {
        float attenuation;
        if (light.Type == StaticLightingLightType.Directional)
        {
            surfaceToLight = SafeNormal(-light.Direction, Vector3.UnitZ);
            attenuation = 1f;
        }
        else
        {
            Vector3 delta = light.Position - sample.Position;
            float distanceSquared = delta.LengthSquared();
            float radiusSquared = light.Radius * light.Radius;
            if (distanceSquared <= 0.0001f || distanceSquared >= radiusSquared)
            {
                surfaceToLight = default;
                unshadowedRadiance = default;
                irradiance = default;
                return false;
            }
            float distance = MathF.Sqrt(distanceSquared);
            surfaceToLight = delta / distance;
            float normalizedDistance = distance / light.Radius;
            attenuation = MathF.Max(0f, 1f - normalizedDistance * normalizedDistance);
            attenuation *= attenuation;
        }

        if (light.Type == StaticLightingLightType.Spot)
        {
            float coneDot = Vector3.Dot(-surfaceToLight, SafeNormal(light.Direction, Vector3.UnitX));
            float outerCos = MathF.Cos(light.OuterConeAngleDegrees * MathF.PI / 180f);
            float innerCos = MathF.Cos(light.InnerConeAngleDegrees * MathF.PI / 180f);
            if (coneDot <= outerCos)
            {
                unshadowedRadiance = default;
                irradiance = default;
                return false;
            }
            float coneRange = MathF.Max(0.0001f, innerCos - outerCos);
            attenuation *= Math.Clamp((coneDot - outerCos) / coneRange, 0f, 1f);
        }

        float normalDotLight = MathF.Max(0f, Vector3.Dot(sample.Normal, surfaceToLight));
        if (normalDotLight <= 0f)
        {
            unshadowedRadiance = default;
            irradiance = default;
            return false;
        }
        unshadowedRadiance = Vector3.Max(Vector3.Zero, light.Color * (light.Intensity * attenuation));
        irradiance = unshadowedRadiance * normalDotLight;
        return true;
    }

    private static bool LightCanAffect(StaticLightingLight light, uint targetChannels, bool hasBounds,
        Vector3 minimum, Vector3 maximum)
    {
        if (!SceneLight.ChannelsOverlap(light.LightingChannelMask, targetChannels))
            return false;
        if (light.Type is StaticLightingLightType.Directional or StaticLightingLightType.Sky)
            return true;
        if (!hasBounds)
            return false;
        Vector3 closestPoint = Vector3.Clamp(light.Position, minimum, maximum);
        if (Vector3.DistanceSquared(closestPoint, light.Position) >= light.Radius * light.Radius)
            return false;
        if (light.Type != StaticLightingLightType.Spot)
            return true;

        Vector3 center = (minimum + maximum) * 0.5f;
        float boundsRadius = (maximum - center).Length();
        Vector3 lightToCenter = center - light.Position;
        float centerDistance = lightToCenter.Length();
        if (centerDistance <= boundsRadius)
            return true;
        float angularRadius = MathF.Asin(Math.Clamp(boundsRadius / centerDistance, 0f, 1f));
        float outerAngle = light.OuterConeAngleDegrees * MathF.PI / 180f;
        float expandedAngle = MathF.Min(MathF.PI, outerAngle + angularRadius);
        return Vector3.Dot(lightToCenter / centerDistance, SafeNormal(light.Direction, Vector3.UnitX)) >=
               MathF.Cos(expandedAngle);
    }

    private static bool TryCalculateBounds(IReadOnlyList<StaticLightingVertex> vertices,
        out Vector3 minimum, out Vector3 maximum)
    {
        if (vertices.Count == 0)
        {
            minimum = maximum = Vector3.Zero;
            return false;
        }
        minimum = maximum = vertices[0].Position;
        for (int index = 1; index < vertices.Count; index++)
        {
            minimum = Vector3.Min(minimum, vertices[index].Position);
            maximum = Vector3.Max(maximum, vertices[index].Position);
        }
        return true;
    }

    public readonly record struct StaticLightingBakeTile(int MinimumX, int MinimumY, int MaximumX, int MaximumY);

    public static IReadOnlyList<StaticLightingBakeTile> CreateTextureWorkTiles(int resolution, int tileSize)
    {
        if (resolution <= 0) throw new ArgumentOutOfRangeException(nameof(resolution));
        if (tileSize <= 0) throw new ArgumentOutOfRangeException(nameof(tileSize));
        var tiles = new List<StaticLightingBakeTile>();
        for (int y = 0; y < resolution; y += tileSize)
        for (int x = 0; x < resolution; x += tileSize)
            tiles.Add(new StaticLightingBakeTile(x, y, Math.Min(resolution, x + tileSize),
                Math.Min(resolution, y + tileSize)));
        return tiles;
    }

    private static List<int>[] BuildTileTriangleBuckets(IReadOnlyList<StaticLightingTriangle> triangles,
        IReadOnlyList<StaticLightingBakeTile> tiles, int resolution, Vector2 coordinateScale,
        Vector2 coordinateBias, int tileSize)
    {
        var buckets = Enumerable.Range(0, tiles.Count).Select(_ => new List<int>()).ToArray();
        int tilesX = (resolution + tileSize - 1) / tileSize;
        for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
        {
            if (!TryGetRasterBounds(triangles[triangleIndex], resolution, coordinateScale, coordinateBias,
                    out int minimumX, out int minimumY, out int maximumX, out int maximumY))
                continue;
            if (IsDegenerateUvTriangle(triangles[triangleIndex]))
            {
                // The repair rasterizer covers a one-texel strip around a collapsed UV line. Include
                // neighbouring tiles so a repaired line that sits on a tile boundary is not clipped.
                minimumX = Math.Max(0, minimumX - 1);
                minimumY = Math.Max(0, minimumY - 1);
                maximumX = Math.Min(resolution - 1, maximumX + 1);
                maximumY = Math.Min(resolution - 1, maximumY + 1);
            }
            int minimumTileX = minimumX / tileSize;
            int maximumTileX = maximumX / tileSize;
            int minimumTileY = minimumY / tileSize;
            int maximumTileY = maximumY / tileSize;
            for (int tileY = minimumTileY; tileY <= maximumTileY; tileY++)
            for (int tileX = minimumTileX; tileX <= maximumTileX; tileX++)
                buckets[tileY * tilesX + tileX].Add(triangleIndex);
        }
        return buckets;
    }

    private static int RasterizeTriangle(StaticLightingTriangle triangle, int triangleIndex, int resolution,
        Vector2 coordinateScale, Vector2 coordinateBias, StaticLightingBakeTile tile, ExportEntry source,
        Vector3 geometricNormal, float worldUnitsPerTexel, StaticLightingSurfaceSample[] samples,
        int[] triangleOwners, bool[] mapped, bool[] mappingConflicts)
    {
        int conflictCount = 0;
        Vector2 a = triangle.A.LightMapCoordinate * coordinateScale + coordinateBias;
        Vector2 b = triangle.B.LightMapCoordinate * coordinateScale + coordinateBias;
        Vector2 c = triangle.C.LightMapCoordinate * coordinateScale + coordinateBias;
        float denominator = Cross(b - a, c - a);
        if (MathF.Abs(denominator) < 0.0000001f)
        {
            return RasterizeDegenerateUvTriangle(triangle, triangleIndex, a, b, c, resolution, tile,
                source, geometricNormal, worldUnitsPerTexel, samples, triangleOwners, mapped,
                mappingConflicts);
        }
        if (!TryGetRasterBounds(triangle, resolution, coordinateScale, coordinateBias,
                out int minimumX, out int minimumY, out int maximumX, out int maximumY))
            return 0;
        minimumX = Math.Max(minimumX, tile.MinimumX);
        maximumX = Math.Min(maximumX, tile.MaximumX - 1);
        minimumY = Math.Max(minimumY, tile.MinimumY);
        maximumY = Math.Min(maximumY, tile.MaximumY - 1);
        if (minimumX > maximumX || minimumY > maximumY) return 0;

        for (int y = minimumY; y <= maximumY; y++)
        for (int x = minimumX; x <= maximumX; x++)
        {
            Vector2 point = new((x + 0.5f) / resolution, (y + 0.5f) / resolution);
            float v = Cross(point - a, c - a) / denominator;
            float w = Cross(b - a, point - a) / denominator;
            float u = 1f - v - w;
            const float edgeTolerance = -0.0001f;
            if (u >= edgeTolerance && v >= edgeTolerance && w >= edgeTolerance)
                conflictCount += WriteRasterSample(triangle, triangleIndex, y * resolution + x,
                    new Vector3(u, v, w), source, geometricNormal, worldUnitsPerTexel, samples,
                    triangleOwners, mapped, mappingConflicts);
        }
        return conflictCount;
    }

    private static int RasterizeDegenerateUvTriangle(StaticLightingTriangle triangle, int triangleIndex,
        Vector2 a, Vector2 b, Vector2 c, int resolution, StaticLightingBakeTile tile, ExportEntry source,
        Vector3 geometricNormal, float worldUnitsPerTexel, StaticLightingSurfaceSample[] samples,
        int[] triangleOwners, bool[] mapped, bool[] mappingConflicts)
    {
        Span<Vector2> points = stackalloc Vector2[3] { a * resolution, b * resolution, c * resolution };
        Span<Vector3> weights = stackalloc Vector3[3] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        float distance01 = Vector2.DistanceSquared(points[0], points[1]);
        float distance12 = Vector2.DistanceSquared(points[1], points[2]);
        float distance20 = Vector2.DistanceSquared(points[2], points[0]);
        int first = distance01 >= distance12 && distance01 >= distance20 ? 0 : distance12 >= distance20 ? 1 : 2;
        int second = first == 0 ? 1 : first == 1 ? 2 : 0;
        Vector2 start = points[first];
        Vector2 end = points[second];
        Vector2 direction = end - start;
        float lengthSquared = direction.LengthSquared();

        if (lengthSquared < 0.0001f)
        {
            Vector2 center = (points[0] + points[1] + points[2]) / 3f;
            int x = Math.Clamp((int)MathF.Floor(center.X), 0, resolution - 1);
            int y = Math.Clamp((int)MathF.Floor(center.Y), 0, resolution - 1);
            if (x >= tile.MinimumX && x < tile.MaximumX && y >= tile.MinimumY && y < tile.MaximumY)
                return WriteRasterSample(triangle, triangleIndex, y * resolution + x,
                    new Vector3(1f / 3f), source, geometricNormal, worldUnitsPerTexel, samples,
                    triangleOwners, mapped, mappingConflicts);
            return 0;
        }

        int minimumX = Math.Max(tile.MinimumX,
            Math.Clamp((int)MathF.Floor(MathF.Min(start.X, end.X)) - 1, 0, resolution - 1));
        int maximumX = Math.Min(tile.MaximumX - 1,
            Math.Clamp((int)MathF.Ceiling(MathF.Max(start.X, end.X)) + 1, 0, resolution - 1));
        int minimumY = Math.Max(tile.MinimumY,
            Math.Clamp((int)MathF.Floor(MathF.Min(start.Y, end.Y)) - 1, 0, resolution - 1));
        int maximumY = Math.Min(tile.MaximumY - 1,
            Math.Clamp((int)MathF.Ceiling(MathF.Max(start.Y, end.Y)) + 1, 0, resolution - 1));
        const float maximumDistanceSquared = 0.75f * 0.75f;
        int conflictCount = 0;
        for (int y = minimumY; y <= maximumY; y++)
        for (int x = minimumX; x <= maximumX; x++)
        {
            Vector2 pixelCenter = new(x + 0.5f, y + 0.5f);
            float amount = Math.Clamp(Vector2.Dot(pixelCenter - start, direction) / lengthSquared, 0f, 1f);
            Vector2 closest = start + direction * amount;
            if (Vector2.DistanceSquared(pixelCenter, closest) > maximumDistanceSquared)
                continue;
            Vector3 barycentric = Vector3.Lerp(weights[first], weights[second], amount);
            conflictCount += WriteRasterSample(triangle, triangleIndex, y * resolution + x,
                barycentric, source, geometricNormal, worldUnitsPerTexel, samples, triangleOwners,
                mapped, mappingConflicts);
        }
        return conflictCount;
    }

    private static int WriteRasterSample(StaticLightingTriangle triangle, int triangleIndex, int pixelIndex,
        Vector3 barycentric, ExportEntry source, Vector3 geometricNormal, float worldUnitsPerTexel,
        StaticLightingSurfaceSample[] samples, int[] triangleOwners, bool[] mapped, bool[] mappingConflicts)
    {
        int existingOwner = triangleOwners[pixelIndex];
        if (existingOwner == triangleIndex)
            return 0;
        StaticLightingSurfaceSample candidate = Interpolate(triangle, barycentric, source,
            geometricNormal, worldUnitsPerTexel);
        if (existingOwner >= 0)
        {
            float tolerance = MathF.Max(0.001f,
                MathF.Max(samples[pixelIndex].WorldUnitsPerTexel, candidate.WorldUnitsPerTexel) * 0.02f);
            if (Vector3.DistanceSquared(samples[pixelIndex].Position, candidate.Position) > tolerance * tolerance &&
                !mappingConflicts[pixelIndex])
            {
                mappingConflicts[pixelIndex] = true;
                return 1;
            }
            return 0;
        }
        samples[pixelIndex] = candidate;
        triangleOwners[pixelIndex] = triangleIndex;
        mapped[pixelIndex] = true;
        return 0;
    }

    private static bool TryGetRasterBounds(StaticLightingTriangle triangle, int resolution,
        Vector2 coordinateScale, Vector2 coordinateBias, out int minimumX, out int minimumY,
        out int maximumX, out int maximumY)
    {
        Vector2 a = triangle.A.LightMapCoordinate * coordinateScale + coordinateBias;
        Vector2 b = triangle.B.LightMapCoordinate * coordinateScale + coordinateBias;
        Vector2 c = triangle.C.LightMapCoordinate * coordinateScale + coordinateBias;
        minimumX = Math.Clamp((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) * resolution),
            0, resolution - 1);
        maximumX = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X)) * resolution),
            0, resolution - 1);
        minimumY = Math.Clamp((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) * resolution),
            0, resolution - 1);
        maximumY = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) * resolution),
            0, resolution - 1);
        return maximumX >= minimumX && maximumY >= minimumY;
    }

    private static StaticLightingSurfaceSample Interpolate(StaticLightingTriangle triangle, Vector3 weights,
        ExportEntry source, Vector3 geometricNormal, float worldUnitsPerTexel)
    {
        Vector3 position = triangle.A.Position * weights.X + triangle.B.Position * weights.Y + triangle.C.Position * weights.Z;
        Vector3 normal = SafeNormal(triangle.A.Normal * weights.X + triangle.B.Normal * weights.Y + triangle.C.Normal * weights.Z,
            geometricNormal);
        if (Vector3.Dot(geometricNormal, normal) < 0f)
            geometricNormal = -geometricNormal;
        Vector3 tangent = SafeNormal(triangle.A.Tangent * weights.X + triangle.B.Tangent * weights.Y + triangle.C.Tangent * weights.Z,
            Vector3.UnitX);
        tangent = SafeNormal(tangent - normal * Vector3.Dot(tangent, normal), Vector3.UnitX);
        Vector3 bitangent = SafeNormal(Vector3.Cross(normal, tangent), Vector3.UnitY);
        return new StaticLightingSurfaceSample(position, normal, tangent, bitangent, geometricNormal, source,
            triangle.SourceTriangleIndex, worldUnitsPerTexel);
    }

    private static float EstimateTriangleWorldUnitsPerTexel(StaticLightingTriangle triangle, int resolution)
    {
        float worldDoubleArea = Vector3.Cross(triangle.B.Position - triangle.A.Position,
            triangle.C.Position - triangle.A.Position).Length();
        float uvDoubleArea = MathF.Abs(Cross(triangle.B.LightMapCoordinate - triangle.A.LightMapCoordinate,
            triangle.C.LightMapCoordinate - triangle.A.LightMapCoordinate));
        if (worldDoubleArea <= 0f || uvDoubleArea <= 0.0000001f || resolution <= 0)
            return 1f;
        return MathF.Sqrt(worldDoubleArea / uvDoubleArea) / resolution;
    }

    private static float EstimateVertexShadowScale(StaticLightingMeshTarget target)
    {
        if (target.Triangles.Count == 0)
            return 1f;
        double edgeLength = 0d;
        long edgeCount = 0;
        foreach (StaticLightingTriangle triangle in target.Triangles)
        {
            edgeLength += Vector3.Distance(triangle.A.Position, triangle.B.Position);
            edgeLength += Vector3.Distance(triangle.B.Position, triangle.C.Position);
            edgeLength += Vector3.Distance(triangle.C.Position, triangle.A.Position);
            edgeCount += 3;
        }
        return edgeCount == 0 ? 1f : (float)(edgeLength / edgeCount);
    }

    private static Vector3 CalculateScale(IReadOnlyList<Vector3> samples, IReadOnlyList<bool> mapped)
    {
        Vector3 maximum = new(1f / 255f);
        for (int index = 0; index < samples.Count; index++)
        {
            if (mapped is not null && !mapped[index]) continue;
            maximum = Vector3.Max(maximum, samples[index]);
        }
        return maximum;
    }

    private byte[] EncodeColorImage(IReadOnlyList<Vector3> samples, Vector3 scale,
        CancellationToken cancellationToken, int workerCount)
    {
        var bytes = new byte[samples.Count * 4];
        Parallel.ForEach(Partitioner.Create(0, samples.Count, 4096),
            CreateParallelOptions(cancellationToken, workerCount), range =>
        {
            for (int index = range.Item1; index < range.Item2; index++)
            {
                Vector3 normalized = new(samples[index].X / scale.X, samples[index].Y / scale.Y,
                    samples[index].Z / scale.Z);
                bytes[index * 4] = ToByte(normalized.Z);
                bytes[index * 4 + 1] = ToByte(normalized.Y);
                bytes[index * 4 + 2] = ToByte(normalized.X);
                bytes[index * 4 + 3] = byte.MaxValue;
            }
        });
        return bytes;
    }

    private static Color ToColor(Vector3 value, Vector3 scale) => new(
        ToByte(value.X / scale.X), ToByte(value.Y / scale.Y), ToByte(value.Z / scale.Z), byte.MaxValue);

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    private static void DilateCoefficients(Vector3[][] values, bool[] mapped, int resolution, int iterations,
        CancellationToken cancellationToken, int workerCount)
    {
        var occupied = (bool[])mapped.Clone();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            Vector3[][] source = values.Select(coefficient => (Vector3[])coefficient.Clone()).ToArray();
            bool[] sourceOccupied = (bool[])occupied.Clone();
            int changed = 0;
            Parallel.For(0, resolution, CreateParallelOptions(cancellationToken, workerCount), y =>
            {
                for (int x = 0; x < resolution; x++)
                {
                    int index = y * resolution + x;
                    if (sourceOccupied[index]) continue;
                    foreach ((int offsetX, int offsetY) in Neighbors)
                    {
                        int neighborX = x + offsetX;
                        int neighborY = y + offsetY;
                        if ((uint)neighborX >= resolution || (uint)neighborY >= resolution) continue;
                        int neighbor = neighborY * resolution + neighborX;
                        if (!sourceOccupied[neighbor]) continue;
                        for (int coefficient = 0; coefficient < values.Length; coefficient++)
                            values[coefficient][index] = source[coefficient][neighbor];
                        occupied[index] = true;
                        Interlocked.Exchange(ref changed, 1);
                        break;
                    }
                }
            });
            if (changed == 0) break;
        }
    }

    private static readonly (int X, int Y)[] Neighbors = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    private static bool IsStaticLightingTarget(StaticMeshComponentProxy component)
        => IsStaticLightingCandidate(component);

    public static bool IsStaticLightingCandidate(StaticMeshComponentProxy component)
    {
        string actorClass = component.Actor.Export.ClassName;
        if (actorClass.Contains("Dynamic", StringComparison.OrdinalIgnoreCase) ||
            actorClass.Contains("InterpActor", StringComparison.OrdinalIgnoreCase) ||
            actorClass.Contains("KActor", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static int CollectEmissiveAreaEmitters(StaticMeshComponentProxy component,
        ExportEntry meshExport, StaticMeshRenderData lod, IReadOnlyList<StaticLightingTriangle> triangles,
        LevelEditorRenderContext renderContext, StaticLightingEmissiveSettings settings,
        Dictionary<ExportEntry, (bool Emissive, Vector3 Radiance)> materialCache,
        List<StaticLightingAreaEmitter> output)
    {
        StaticMeshElement[] elements = lod.Elements ?? [];
        var sectionRadiance = new Vector3?[elements.Length];
        for (int sectionIndex = 0; sectionIndex < elements.Length; sectionIndex++)
        {
            ExportEntry material = ResolveSectionMaterial(component, meshExport, elements[sectionIndex],
                sectionIndex, renderContext);
            if (material is null) continue;
            if (!materialCache.TryGetValue(material, out var emission))
            {
                bool emissive = StaticLightingEmissive.TryResolveMaterialRadiance(material, renderContext,
                    out Vector3 radiance);
                emission = (emissive, radiance);
                materialCache.Add(material, emission);
            }
            if (emission.Emissive)
                sectionRadiance[sectionIndex] = emission.Radiance;
        }

        if (sectionRadiance.All(value => !value.HasValue))
            return 0;
        var sectionTriangles = new List<StaticLightingTriangle>[elements.Length];
        int sourceTriangleCount = 0;
        foreach (StaticLightingTriangle triangle in triangles)
        {
            int sectionIndex = triangle.SectionIndex;
            if ((uint)sectionIndex >= sectionRadiance.Length || !sectionRadiance[sectionIndex].HasValue)
                continue;
            (sectionTriangles[sectionIndex] ??= []).Add(triangle);
            sourceTriangleCount++;
        }
        for (int sectionIndex = 0; sectionIndex < sectionTriangles.Length; sectionIndex++)
        {
            if (sectionTriangles[sectionIndex] is not { Count: > 0 } sourceTriangles ||
                sectionRadiance[sectionIndex] is not { } radiance)
                continue;
            output.AddRange(StaticLightingEmissive.CreateAreaEmitterSamples(sourceTriangles, radiance,
                settings, component.LightingChannelMask, component.Export));
        }
        return sourceTriangleCount;
    }

    private static bool HasCompatibleReceiverMaterials(StaticMeshComponentProxy component,
        ExportEntry meshExport, StaticMeshRenderData lod, LevelEditorRenderContext renderContext,
        Dictionary<ExportEntry, (bool Compatible, string MaterialPath)> compatibilityCache,
        out string incompatibleMaterialPath)
    {
        StaticMeshElement[] elements = lod.Elements ?? [];
        bool hasResolvedMaterial = false;
        bool hasCompatibleMaterial = false;
        string firstIncompatibleMaterialPath = null;
        for (int slot = 0; slot < elements.Length; slot++)
        {
            ExportEntry material = ResolveSectionMaterial(component, meshExport, elements[slot], slot,
                renderContext);
            if (material is null)
                continue;
            hasResolvedMaterial = true;
            if (!compatibilityCache.TryGetValue(material, out var compatibility))
            {
                ExportEntry baseMaterial = ResolveBaseMaterial(material, renderContext);
                compatibility = (CanMaterialReceiveStaticLighting(baseMaterial?.GetProperties()),
                    baseMaterial?.InstancedFullPath ?? material.InstancedFullPath);
                compatibilityCache.Add(material, compatibility);
            }
            if (compatibility.Compatible)
            {
                hasCompatibleMaterial = true;
                break;
            }
            firstIncompatibleMaterialPath ??= compatibility.MaterialPath;
        }
        bool canReceive = CanComponentReceiveStaticLighting(hasResolvedMaterial, hasCompatibleMaterial);
        incompatibleMaterialPath = canReceive ? null : firstIncompatibleMaterialPath;
        return canReceive;
    }

    private static ExportEntry ResolveSectionMaterial(StaticMeshComponentProxy component,
        ExportEntry meshExport, StaticMeshElement element, int slot,
        LevelEditorRenderContext renderContext)
    {
        IEntry materialEntry = null;
        ArrayProperty<ObjectProperty> overrides =
            component.Properties.GetProp<ArrayProperty<ObjectProperty>>("Materials");
        if (overrides is not null && slot < overrides.Count && overrides[slot].Value != 0)
            materialEntry = component.Export.FileRef.GetEntry(overrides[slot].Value);
        if (materialEntry is null && element.Material != 0)
            materialEntry = meshExport.FileRef.GetEntry(element.Material);
        return renderContext.ResolveExportCached(materialEntry);
    }

    private static ExportEntry ResolveBaseMaterial(ExportEntry material,
        LevelEditorRenderContext renderContext)
    {
        var visited = new HashSet<ExportEntry>();
        ExportEntry current = material;
        while (current is not null &&
               current.ClassName.Contains("MaterialInstance", StringComparison.Ordinal) &&
               visited.Add(current))
        {
            ObjectProperty parent = current.GetProperty<ObjectProperty>("Parent");
            current = parent is null ? null : renderContext.ResolveExportCached(current.FileRef, parent.Value);
        }
        return current;
    }

    /// <summary>
    /// Unlit materials do not consume direct, indirect, or vertex lighting. Installing a component
    /// lightmap for them is both redundant and incompatible with how the shipped ME3 levels use them.
    /// </summary>
    public static bool CanMaterialReceiveStaticLighting(PropertyCollection baseMaterialProperties) =>
        baseMaterialProperties?.GetProp<EnumProperty>("LightingModel")?.Value.Name != "MLM_Unlit";

    /// <summary>
    /// Unresolved/no-material components retain the historical receiver behavior. Resolved components
    /// are excluded only when every section is incompatible; one lit section is sufficient because
    /// UE3 applies the component lightmap only in material draw policies that consume static lighting.
    /// </summary>
    public static bool CanComponentReceiveStaticLighting(bool hasResolvedMaterial,
        bool hasCompatibleMaterial) => !hasResolvedMaterial || hasCompatibleMaterial;

    /// <summary>
    /// UE3 uses singular shadow flags on primitive components and plural variants on lights and a few
    /// BioWare subclasses. Every explicit false is authoritative; absent properties keep UE3 defaults.
    /// </summary>
    public static bool CastsStaticShadow(PropertyCollection properties) =>
        properties.GetProp<BoolProperty>("CastShadow")?.Value != false &&
        properties.GetProp<BoolProperty>("CastShadows")?.Value != false &&
        properties.GetProp<BoolProperty>("bCastStaticShadow")?.Value != false &&
        properties.GetProp<BoolProperty>("CastStaticShadows")?.Value != false;

    public static bool ShouldUseTextureMapping(StaticLightingMappingMode mappingMode,
        bool hasValidTextureCoordinates, int effectiveLightMapResolution,
        ELightMapType existingMappingType, bool existingMappingWasGenerated,
        string meshPath = "", float maximumWorldDimension = 0f, float surfaceArea = 0f,
        int triangleCount = 0)
    {
        if (mappingMode == StaticLightingMappingMode.Vertex1D)
            return false;
        if (!hasValidTextureCoordinates)
            return false;
        if (mappingMode == StaticLightingMappingMode.Texture2D)
            return true;

        if (!existingMappingWasGenerated &&
            existingMappingType is ELightMapType.LMT_2D or ELightMapType.LMT_4 or ELightMapType.LMT_6)
            return true;

        if (IsArchitecturalReceiver(meshPath))
            return true;

        // An authored vertex mapping is stronger evidence than the receiver's world-space size. In
        // particular, BioWare uses LightMap1D for TableLab03 and other large, vertex-dense props. The
        // old average-triangle-area test promoted those props to LightMap2D, exposing UV chart islands
        // as large rectangular lighting blocks in game.
        if (!existingMappingWasGenerated &&
            existingMappingType is ELightMapType.LMT_1D or ELightMapType.LMT_3 or ELightMapType.LMT_5)
            return false;

        bool isBroadLowPolyReceiver = triangleCount is > 0 and <= 256;
        if (maximumWorldDimension >= 512f && isBroadLowPolyReceiver)
            return true;

        if (maximumWorldDimension >= 128f && surfaceArea >= 16_384f && isBroadLowPolyReceiver)
            return true;

        return false;
    }

    /// <summary>
    /// Bulk automatic bakes retain the receiver density authored into the base mesh and treat the
    /// toolbar selection as a ceiling. Explicit/single-actor bakes retain exact-size behavior.
    /// </summary>
    public static int ResolveTextureResolution(StaticLightingMappingMode mappingMode, bool exactTarget,
        int authoredResolution, int requestedMaximum)
    {
        if (requestedMaximum is < 64 or > 1024 || !BitOperations.IsPow2((uint)requestedMaximum))
            throw new ArgumentOutOfRangeException(nameof(requestedMaximum));
        if (exactTarget || mappingMode != StaticLightingMappingMode.Automatic || authoredResolution <= 0)
            return requestedMaximum;
        uint authored = (uint)Math.Min(authoredResolution, 1024);
        int roundedAuthored = (int)BitOperations.RoundUpToPowerOf2(authored);
        return Math.Min(requestedMaximum, Math.Max(64, roundedAuthored));
    }

    private static bool IsArchitecturalReceiver(string meshPath)
    {
        if (string.IsNullOrWhiteSpace(meshPath))
            return false;
        string[] tokens = meshPath.Split(['.', '_', '-', ' ', '/', '\\'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] architecturalPrefixes =
            ["floor", "wall", "ceiling", "roof", "ground", "terrain", "bsp", "architecture", "architectural"];
        return tokens.Any(token => architecturalPrefixes.Any(prefix =>
            token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    private static void CalculateReceiverMetrics(IReadOnlyList<StaticLightingVertex> vertices,
        IReadOnlyList<StaticLightingTriangle> triangles, out float maximumWorldDimension,
        out float surfaceArea)
    {
        maximumWorldDimension = 0f;
        if (TryCalculateBounds(vertices, out Vector3 minimum, out Vector3 maximum))
        {
            Vector3 size = maximum - minimum;
            maximumWorldDimension = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        }

        double area = 0d;
        foreach (StaticLightingTriangle triangle in triangles)
            area += Vector3.Cross(triangle.B.Position - triangle.A.Position,
                triangle.C.Position - triangle.A.Position).Length() * 0.5d;
        surfaceArea = (float)Math.Min(float.MaxValue, area);
    }

    private static int GetEffectiveLightMapResolution(StaticMeshComponentProxy component,
        ExportEntry meshExport)
    {
        bool overridesResolution = component.Properties.GetProp<BoolProperty>("bOverrideLightMapRes")?.Value == true;
        if (overridesResolution)
            return Math.Max(0, component.Properties.GetProp<IntProperty>("OverriddenLightMapRes")?.Value ?? 0);
        return Math.Max(0, meshExport.GetProperty<IntProperty>("LightMapResolution")?.Value ?? 0);
    }

    private static ELightMapType GetExistingMappingType(StaticMeshComponent component) =>
        component.LODData is { Length: > 0 } && component.LODData[0].LightMap is { } lightMap
            ? lightMap.LightMapType
            : ELightMapType.LMT_None;

    private static int GetExistingTextureMappingResolution(ExportEntry component,
        StaticMeshComponent binary, Dictionary<ExportEntry, (int Width, int Height)> textureDimensions)
    {
        (int[] Textures, Vector2 Scale) mapping = binary.LODData is { Length: > 0 }
            ? binary.LODData[0].LightMap switch
            {
                LightMap_2D map => ([map.Texture1, map.Texture2, map.Texture3], map.CoordinateScale),
                LightMap_4or6 map => ([map.Texture1, map.Texture2, map.Texture3], map.CoordinateScale),
                _ => ([], Vector2.Zero)
            }
            : ([], Vector2.Zero);
        if (mapping.Textures.Length == 0 || !float.IsFinite(mapping.Scale.X) ||
            !float.IsFinite(mapping.Scale.Y) || mapping.Scale.X <= 0f || mapping.Scale.Y <= 0f)
            return 0;
        int allocatedDimension = 0;
        foreach (int textureIndex in mapping.Textures)
        {
            if (!component.FileRef.TryGetUExport(textureIndex, out ExportEntry textureExport))
                continue;
            if (!textureDimensions.TryGetValue(textureExport, out (int Width, int Height) dimensions))
            {
                try
                {
                    UTexture2D.Texture2DMipMap topMip =
                        textureExport.GetBinaryData<UTexture2D>().Mips?.FirstOrDefault();
                    dimensions = topMip is null ? default : (topMip.SizeX, topMip.SizeY);
                }
                catch
                {
                    dimensions = default;
                }
                textureDimensions.Add(textureExport, dimensions);
            }
            if (dimensions.Width <= 0 || dimensions.Height <= 0) continue;
            allocatedDimension = Math.Max(allocatedDimension, Math.Max(
                (int)MathF.Ceiling(mapping.Scale.X * dimensions.Width),
                (int)MathF.Ceiling(mapping.Scale.Y * dimensions.Height)));
        }
        if (allocatedDimension <= 0) return 0;
        return (int)BitOperations.RoundUpToPowerOf2((uint)Math.Min(allocatedDimension, 2048));
    }

    private static bool IsGeneratedTextureMapping(ExportEntry component, StaticMeshComponent binary)
    {
        if (binary.LODData is not { Length: > 0 })
            return false;
        int[] textureReferences = binary.LODData[0].LightMap switch
        {
            LightMap_2D map => [map.Texture1, map.Texture2, map.Texture3],
            LightMap_4or6 map => [map.Texture1, map.Texture2, map.Texture3],
            _ => []
        };
        return textureReferences.Where(index => index != 0)
            .Select(component.FileRef.GetEntry)
            .Any(entry => entry?.ObjectName.Name.StartsWith("LEX_Lightmass_", StringComparison.Ordinal) == true);
    }

    private static bool TryGetMesh(StaticMeshComponentProxy component, LevelEditorRenderContext renderContext,
        out ExportEntry meshExport, out StaticMesh mesh)
    {
        meshExport = null;
        mesh = null;
        if (component.Properties.GetProp<ObjectProperty>("StaticMesh") is not { Value: not 0 } meshProperty ||
            renderContext.ResolveExportCached(component.Export.FileRef, meshProperty.Value) is not { } resolved)
            return false;
        meshExport = resolved;
        mesh = renderContext.GetCachedStaticMesh(resolved);
        return mesh is not null;
    }

    private static int GetLightMapCoordinateIndex(ExportEntry meshExport, StaticMeshRenderData _)
    {
        // UE3 samples texture lightmaps with the mesh's authored channel, defaulting to UV0 when the
        // property is absent. Baking another available channel without changing the mesh would make
        // the runtime read an unrelated atlas and produce large polygonal lighting artifacts.
        return Math.Max(0, meshExport.GetProperty<IntProperty>("LightMapCoordinateIndex")?.Value ?? 0);
    }

    private static IReadOnlyList<StaticLightingTriangle> BuildTrianglesWithRuntimeLightMapCoordinate(
        ExportEntry meshExport, StaticMeshRenderData lod, Matrix4x4 localToWorld,
        Dictionary<(ExportEntry Mesh, int CoordinateIndex), StaticLightingMappingDiagnostics> diagnosticsCache,
        out int coordinateIndex, out StaticLightingVertex[] vertices, out bool hasTextureCoordinates,
        out StaticLightingMappingDiagnostics diagnostics)
    {
        coordinateIndex = GetLightMapCoordinateIndex(meshExport, lod);
        return BuildTrianglesForCoordinate(meshExport, lod, localToWorld, coordinateIndex,
            diagnosticsCache, out vertices, out hasTextureCoordinates, out diagnostics);
    }

    private static IReadOnlyList<StaticLightingTriangle> BuildTrianglesForCoordinate(
        ExportEntry meshExport, StaticMeshRenderData lod, Matrix4x4 localToWorld, int coordinateIndex,
        Dictionary<(ExportEntry Mesh, int CoordinateIndex), StaticLightingMappingDiagnostics> diagnosticsCache,
        out StaticLightingVertex[] vertices, out bool hasTextureCoordinates,
        out StaticLightingMappingDiagnostics diagnostics)
    {
        diagnosticsCache.TryGetValue((meshExport, coordinateIndex), out StaticLightingMappingDiagnostics cached);
        IReadOnlyList<StaticLightingTriangle> triangles = BuildTriangles(lod, localToWorld, coordinateIndex,
            meshExport.InstancedFullPath, out vertices, out hasTextureCoordinates, out diagnostics, cached);
        if (cached is null)
            diagnosticsCache.Add((meshExport, coordinateIndex), diagnostics);
        return triangles;
    }

    private static IReadOnlyList<StaticLightingTriangle> BuildTriangles(StaticMeshRenderData lod,
        Matrix4x4 localToWorld, int coordinateIndex, string meshPath, out StaticLightingVertex[] vertices,
        out bool hasTextureCoordinates, out StaticLightingMappingDiagnostics diagnostics,
        StaticLightingMappingDiagnostics cachedDiagnostics = null)
    {
        Vector3[] positions = lod.PositionVertexBuffer?.VertexData ?? [];
        StaticMeshVertexBuffer.StaticMeshFullVertex[] sourceVertices = lod.VertexBuffer?.VertexData ?? [];
        int count = Math.Min(positions.Length, sourceVertices.Length);
        vertices = new StaticLightingVertex[count];
        bool coordinateChannelAvailable = count > 0 && coordinateIndex >= 0 &&
                                          lod.VertexBuffer is not null &&
                                          lod.VertexBuffer.NumTexCoords > coordinateIndex;
        bool validateMapping = cachedDiagnostics is null;
        bool[] validCoordinates = validateMapping ? new bool[count] : null;
        Matrix4x4 normalToWorld = Matrix4x4.Invert(localToWorld, out Matrix4x4 inverse)
            ? Matrix4x4.Transpose(inverse)
            : localToWorld;
        for (int index = 0; index < count; index++)
        {
            StaticMeshVertexBuffer.StaticMeshFullVertex source = sourceVertices[index];
            Vector3 position = Vector3.Transform(positions[index], localToWorld);
            Vector3 normal = SafeNormal(Vector3.TransformNormal((Vector3)source.TangentZ, normalToWorld),
                Vector3.UnitZ);
            Vector3 tangent = SafeNormal(Vector3.TransformNormal((Vector3)source.TangentX, normalToWorld),
                Vector3.UnitX);
            Vector3 bitangent = SafeNormal(Vector3.Cross(normal, tangent) * (((Vector4)source.TangentZ).W < 0f ? -1f : 1f),
                Vector3.UnitY);
            Vector2 coordinate = default;
            if (coordinateChannelAvailable)
            {
                coordinate = lod.VertexBuffer.bUseFullPrecisionUVs
                    ? source.FullPrecisionUVs[coordinateIndex]
                    : new Vector2(source.HalfPrecisionUVs[coordinateIndex].X,
                        source.HalfPrecisionUVs[coordinateIndex].Y);
                if (validateMapping)
                    validCoordinates[index] = float.IsFinite(coordinate.X) && float.IsFinite(coordinate.Y) &&
                                              coordinate.X is >= -0.0001f and <= 1.0001f &&
                                              coordinate.Y is >= -0.0001f and <= 1.0001f;
            }
            vertices[index] = new StaticLightingVertex(position, normal, tangent, bitangent, coordinate);
        }

        var triangles = new List<StaticLightingTriangle>();
        ushort[] indices = lod.IndexBuffer ?? [];
        int invalidSectionRanges = 0;
        int invalidIndices = 0;
        int degenerateUvTriangles = 0;
        HashSet<int> referencedVertices = validateMapping ? [] : null;
        StaticMeshElement[] elements = lod.Elements ?? [];
        for (int sectionIndex = 0; sectionIndex < elements.Length; sectionIndex++)
        {
            StaticMeshElement element = elements[sectionIndex];
            long requestedEnd = (long)element.FirstIndex + (long)element.NumTriangles * 3L;
            if (element.FirstIndex > indices.Length || requestedEnd > indices.Length)
                invalidSectionRanges++;
            int start = element.FirstIndex >= indices.Length ? indices.Length : (int)element.FirstIndex;
            int end = (int)Math.Min(indices.Length, requestedEnd);
            for (int offset = start; offset + 2 < end; offset += 3)
            {
                int first = indices[offset];
                int second = indices[offset + 1];
                int third = indices[offset + 2];
                if ((uint)first >= vertices.Length || (uint)second >= vertices.Length || (uint)third >= vertices.Length)
                {
                    invalidIndices++;
                    continue;
                }
                if (validateMapping)
                {
                    referencedVertices.Add(first);
                    referencedVertices.Add(second);
                    referencedVertices.Add(third);
                }
                StaticLightingTriangle triangle = new(vertices[first], vertices[second], vertices[third])
                {
                    SectionIndex = sectionIndex,
                    SourceTriangleIndex = offset / 3
                };
                if (Vector3.Cross(triangle.B.Position - triangle.A.Position,
                        triangle.C.Position - triangle.A.Position).LengthSquared() > 0.0001f)
                {
                    triangles.Add(triangle);
                    if (validateMapping && coordinateChannelAvailable && MathF.Abs(Cross(
                            triangle.B.LightMapCoordinate - triangle.A.LightMapCoordinate,
                            triangle.C.LightMapCoordinate - triangle.A.LightMapCoordinate)) < 0.0000001f)
                        degenerateUvTriangles++;
                }
            }
        }

        if (!validateMapping)
        {
            diagnostics = cachedDiagnostics;
            hasTextureCoordinates = coordinateChannelAvailable && !diagnostics.HasTextureMappingErrors;
            return triangles;
        }

        int invalidUvVertices = coordinateChannelAvailable
            ? referencedVertices.Count(index => !validCoordinates[index])
            : 0;
        int overlappingUvPairs = coordinateChannelAvailable && invalidUvVertices == 0
            ? CountOverlappingUvTrianglePairs(triangles)
            : 0;
        diagnostics = new StaticLightingMappingDiagnostics
        {
            MeshPath = meshPath,
            DeclaredVertexCount = (int)Math.Min(lod.NumVertices, (uint)int.MaxValue),
            PositionVertexCount = positions.Length,
            AttributeVertexCount = sourceVertices.Length,
            TextureCoordinateCount = (int)(lod.VertexBuffer?.NumTexCoords ?? 0),
            SelectedCoordinateIndex = coordinateIndex,
            SectionCount = elements.Length,
            SourceIndexCount = indices.Length,
            TriangleCount = triangles.Count,
            InvalidSectionRangeCount = invalidSectionRanges,
            InvalidIndexCount = invalidIndices,
            InvalidUvVertexCount = invalidUvVertices,
            DegenerateUvTriangleCount = degenerateUvTriangles,
            OverlappingUvTrianglePairCount = overlappingUvPairs
        };
        hasTextureCoordinates = coordinateChannelAvailable && !diagnostics.HasTextureMappingErrors;
        return triangles;
    }

    public static int CountOverlappingUvTrianglePairs(IReadOnlyList<StaticLightingTriangle> triangles)
    {
        const int gridSize = 16;
        var cells = new Dictionary<int, List<int>>();
        var testedPairs = new HashSet<long>();
        int overlapCount = 0;
        for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
        {
            StaticLightingTriangle triangle = triangles[triangleIndex];
            if (IsDegenerateUvTriangle(triangle))
                continue;
            float minimumX = MathF.Min(triangle.A.LightMapCoordinate.X,
                MathF.Min(triangle.B.LightMapCoordinate.X, triangle.C.LightMapCoordinate.X));
            float maximumX = MathF.Max(triangle.A.LightMapCoordinate.X,
                MathF.Max(triangle.B.LightMapCoordinate.X, triangle.C.LightMapCoordinate.X));
            float minimumY = MathF.Min(triangle.A.LightMapCoordinate.Y,
                MathF.Min(triangle.B.LightMapCoordinate.Y, triangle.C.LightMapCoordinate.Y));
            float maximumY = MathF.Max(triangle.A.LightMapCoordinate.Y,
                MathF.Max(triangle.B.LightMapCoordinate.Y, triangle.C.LightMapCoordinate.Y));
            int firstX = Math.Clamp((int)MathF.Floor(minimumX * gridSize), 0, gridSize - 1);
            int lastX = Math.Clamp((int)MathF.Floor(maximumX * gridSize), 0, gridSize - 1);
            int firstY = Math.Clamp((int)MathF.Floor(minimumY * gridSize), 0, gridSize - 1);
            int lastY = Math.Clamp((int)MathF.Floor(maximumY * gridSize), 0, gridSize - 1);
            for (int y = firstY; y <= lastY; y++)
            for (int x = firstX; x <= lastX; x++)
            {
                int key = y * gridSize + x;
                if (!cells.TryGetValue(key, out List<int> occupants))
                {
                    occupants = [];
                    cells.Add(key, occupants);
                }
                foreach (int otherIndex in occupants)
                {
                    long pairKey = ((long)otherIndex << 32) | (uint)triangleIndex;
                    if (testedPairs.Add(pairKey) && UvTrianglesOverlapInterior(triangles[otherIndex], triangle))
                        overlapCount++;
                }
                occupants.Add(triangleIndex);
            }
        }
        return overlapCount;
    }

    private static bool IsDegenerateUvTriangle(StaticLightingTriangle triangle) => MathF.Abs(Cross(
        triangle.B.LightMapCoordinate - triangle.A.LightMapCoordinate,
        triangle.C.LightMapCoordinate - triangle.A.LightMapCoordinate)) < 0.0000001f;

    private static bool UvTrianglesOverlapInterior(StaticLightingTriangle left, StaticLightingTriangle right)
    {
        ReadOnlySpan<Vector2> leftPoints = [left.A.LightMapCoordinate, left.B.LightMapCoordinate,
            left.C.LightMapCoordinate];
        ReadOnlySpan<Vector2> rightPoints = [right.A.LightMapCoordinate, right.B.LightMapCoordinate,
            right.C.LightMapCoordinate];
        Vector2 leftCenter = (leftPoints[0] + leftPoints[1] + leftPoints[2]) / 3f;
        Vector2 rightCenter = (rightPoints[0] + rightPoints[1] + rightPoints[2]) / 3f;
        if (PointInsideUvTriangleStrict(leftCenter, rightPoints) ||
            PointInsideUvTriangleStrict(rightCenter, leftPoints))
            return true;
        for (int leftEdge = 0; leftEdge < 3; leftEdge++)
        for (int rightEdge = 0; rightEdge < 3; rightEdge++)
        {
            if (UvEdgesIntersectProperly(leftPoints[leftEdge], leftPoints[(leftEdge + 1) % 3],
                    rightPoints[rightEdge], rightPoints[(rightEdge + 1) % 3]))
                return true;
        }
        return false;
    }

    private static bool PointInsideUvTriangleStrict(Vector2 point, ReadOnlySpan<Vector2> triangle)
    {
        float denominator = Cross(triangle[1] - triangle[0], triangle[2] - triangle[0]);
        if (MathF.Abs(denominator) < 0.0000001f)
            return false;
        float v = Cross(point - triangle[0], triangle[2] - triangle[0]) / denominator;
        float w = Cross(triangle[1] - triangle[0], point - triangle[0]) / denominator;
        float u = 1f - v - w;
        const float tolerance = 0.00001f;
        return u > tolerance && v > tolerance && w > tolerance;
    }

    private static bool UvEdgesIntersectProperly(Vector2 firstStart, Vector2 firstEnd,
        Vector2 secondStart, Vector2 secondEnd)
    {
        Vector2 firstDirection = firstEnd - firstStart;
        Vector2 secondDirection = secondEnd - secondStart;
        float denominator = Cross(firstDirection, secondDirection);
        if (MathF.Abs(denominator) < 0.0000001f)
            return false;
        Vector2 delta = secondStart - firstStart;
        float firstT = Cross(delta, secondDirection) / denominator;
        float secondT = Cross(delta, firstDirection) / denominator;
        if (firstT <= 0f || firstT >= 1f || secondT <= 0f || secondT >= 1f)
            return false;

        float firstEndpointDistance = MathF.Min(firstT, 1f - firstT) * firstDirection.Length();
        float secondEndpointDistance = MathF.Min(secondT, 1f - secondT) * secondDirection.Length();
        return firstEndpointDistance > UvBoundaryDistanceTolerance &&
               secondEndpointDistance > UvBoundaryDistanceTolerance;
    }

    private static bool TryCreateLight(ActorProxy actor, out StaticLightingLight light)
    {
        PrimitiveComponentProxy component = actor.Components.FirstOrDefault(candidate => candidate.Export.IsA("LightComponent"));
        if (component is null ||
            actor.Export.GetCondensedProperties().GetProp<BoolProperty>("bEnabled")?.Value == false ||
            component.Properties.GetProp<BoolProperty>("bEnabled")?.Value == false)
        {
            light = default;
            return false;
        }
        Guid guid = component?.Properties.GetProp<StructProperty>("LightGuid") is { } guidProperty
            ? CommonStructs.GetGuid(guidProperty)
            : CreateStableGuid(actor.Export.FileRef.FilePath, component?.Export.UIndex ?? actor.Export.UIndex);
        bool castsStaticShadow = component.Properties.GetProp<BoolProperty>("CastShadows")?.Value != false &&
                                 component.Properties.GetProp<BoolProperty>("CastStaticShadows")?.Value != false;
        float sourceRadius = MathF.Max(0f,
            component.Properties.GetProp<FloatProperty>("SourceRadius")?.Value ??
            component.Properties.GetProp<FloatProperty>("LightSourceRadius")?.Value ?? 0f);

        if (actor.TryGetSceneLight(out SceneLight sceneLight))
        {
            light = new StaticLightingLight(guid,
                sceneLight.IsSpot ? StaticLightingLightType.Spot : StaticLightingLightType.Point,
                sceneLight.Position, sceneLight.Direction, sceneLight.Color, sceneLight.Intensity,
                sceneLight.Radius, sceneLight.InnerConeAngleDegrees, sceneLight.OuterConeAngleDegrees,
                sceneLight.LightingChannelMask, castsStaticShadow, sourceRadius);
            return true;
        }
        if (actor is SkyLightActorProxy && component is not null)
        {
            Vector3 upperColor = ReadColor(component.Properties, "LightColor", Vector3.One);
            Vector3 lowerColor = ReadColor(component.Properties, "LowerColor", upperColor);
            float upperBrightness = component.Properties.GetProp<FloatProperty>("Brightness")?.Value ?? 1f;
            float lowerBrightness = component.Properties.GetProp<FloatProperty>("LowerBrightness")?.Value ?? upperBrightness;
            Vector3 hemisphericalRadiance =
                (upperColor * MathF.Max(0f, upperBrightness) + lowerColor * MathF.Max(0f, lowerBrightness)) * 0.5f;
            light = new StaticLightingLight(guid, StaticLightingLightType.Sky,
                actor.LocalToWorld.Translation, Vector3.UnitZ, hemisphericalRadiance, 1f,
                float.MaxValue, 0f, 0f, component.LightingChannelMask, false);
            return true;
        }
        if (actor is DirectionalLightActorProxy or DirectionalLightComponentActorProxy)
        {
            Vector3 color = new(actor.LightColor.R / 255f, actor.LightColor.G / 255f, actor.LightColor.B / 255f);
            light = new StaticLightingLight(guid, StaticLightingLightType.Directional,
                actor.LocalToWorld.Translation, actor.LocalToWorld.GetAxis(0).Normal(), color,
                actor.Brightness, float.MaxValue, 0f, 0f, component.LightingChannelMask, castsStaticShadow);
            return true;
        }
        light = default;
        return false;
    }

    private static Vector3 ReadColor(PropertyCollection properties, NameReference propertyName, Vector3 fallback)
    {
        if (properties.GetProp<StructProperty>(propertyName) is not { } colorProperty)
            return fallback;
        System.Drawing.Color color = CommonStructs.GetColor(colorProperty);
        return new Vector3(color.R / 255f, color.G / 255f, color.B / 255f);
    }

    private static Guid CreateStableGuid(string path, int uIndex)
    {
        byte[] bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{path}|{uIndex}"));
        return new Guid(bytes);
    }

    private static Vector3 SafeNormal(Vector3 value, Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 0.000001f && float.IsFinite(lengthSquared)
            ? value / MathF.Sqrt(lengthSquared)
            : Vector3.Normalize(fallback);
    }

    private static float Cross(Vector2 left, Vector2 right) => left.X * right.Y - left.Y * right.X;

    private sealed record PreparedLighting(Vector3 Environment, PreparedLight[] DirectLights,
        StaticLightingAreaEmitter[] AreaEmitters);

    private sealed class PreparedLight
    {
        public StaticLightingLightType Type;
        public Vector3 Position;
        public Vector3 Direction;
        public Vector3 Radiance;
        public float RadiusSquared;
        public float InverseRadius;
        public float OuterConeCos;
        public float InverseConeRange;
        public bool CastsShadow;
        public int SampleCount;
        public float SourceRadius;
        public Vector2[] DiskSamples;
        public Vector3[] DirectionalSurfaceToLight;
    }

    private struct BakeCounterValues
    {
        public int MappingConflictTexels;
        public long RaysCast;
        public long OccludedSamples;
        public long RejectedSelfIntersections;
        public long VisibilitySampleCount;
        public long VisibilityMicroSum;
        public long LitSampleCount;
        public long DirectContributionMicroSum;
        public long EnvironmentContributionMicroSum;
        public long EmissiveSamplesEvaluated;
        public long EmissiveRaysCast;
        public long TextureRasterizationTicks;
        public long DirectLightingTicks;
        public long ShadowRayTicks;
        public long VertexSamplingTicks;
    }

    private sealed class BakeCounters
    {
        public const long MicroScale = 1_000_000;
        public int MappedTexels;
        public int MappingConflictTexels;
        public long RaysCast;
        public long OccludedSamples;
        public long RejectedSelfIntersections;
        public long VisibilitySampleCount;
        public long VisibilityMicroSum;
        public long LitSampleCount;
        public long DirectContributionMicroSum;
        public long EnvironmentContributionMicroSum;
        public int AffectingEmissiveEmitters;
        public long EmissiveSamplesEvaluated;
        public long EmissiveRaysCast;
        public long LightPreparationTicks;
        public long TextureRasterizationTicks;
        public long DirectLightingTicks;
        public long ShadowRayTicks;
        public long VertexSamplingTicks;
        public long FilteringTicks;
        public long OccupiedTexelDiscoveryTicks;
        public long TextureConstructionTicks;

        public void Merge(BakeCounterValues values)
        {
            Interlocked.Add(ref MappingConflictTexels, values.MappingConflictTexels);
            Interlocked.Add(ref RaysCast, values.RaysCast);
            Interlocked.Add(ref OccludedSamples, values.OccludedSamples);
            Interlocked.Add(ref RejectedSelfIntersections, values.RejectedSelfIntersections);
            Interlocked.Add(ref VisibilitySampleCount, values.VisibilitySampleCount);
            Interlocked.Add(ref VisibilityMicroSum, values.VisibilityMicroSum);
            Interlocked.Add(ref LitSampleCount, values.LitSampleCount);
            Interlocked.Add(ref DirectContributionMicroSum, values.DirectContributionMicroSum);
            Interlocked.Add(ref EnvironmentContributionMicroSum, values.EnvironmentContributionMicroSum);
            Interlocked.Add(ref EmissiveSamplesEvaluated, values.EmissiveSamplesEvaluated);
            Interlocked.Add(ref EmissiveRaysCast, values.EmissiveRaysCast);
            Interlocked.Add(ref TextureRasterizationTicks, values.TextureRasterizationTicks);
            Interlocked.Add(ref DirectLightingTicks, values.DirectLightingTicks);
            Interlocked.Add(ref ShadowRayTicks, values.ShadowRayTicks);
            Interlocked.Add(ref VertexSamplingTicks, values.VertexSamplingTicks);
        }

        public StaticLightingComponentDiagnostics CreateDiagnostics(StaticLightingMappingDiagnostics mapping,
            double bakeMilliseconds) => new()
        {
            Mapping = mapping,
            MappedTexelCount = MappedTexels,
            MappingConflictTexelCount = MappingConflictTexels,
            RaysCast = RaysCast,
            OccludedSamples = OccludedSamples,
            RejectedSelfIntersections = RejectedSelfIntersections,
            VisibilitySampleCount = VisibilitySampleCount,
            AverageVisibility = VisibilitySampleCount == 0 ? 1d :
                VisibilityMicroSum / (double)(VisibilitySampleCount * MicroScale),
            AverageDirectContribution = LitSampleCount == 0 ? 0d :
                DirectContributionMicroSum / (double)(LitSampleCount * MicroScale),
            AverageEnvironmentContribution = LitSampleCount == 0 ? 0d :
                EnvironmentContributionMicroSum / (double)(LitSampleCount * MicroScale),
            AffectingEmissiveEmitterCount = AffectingEmissiveEmitters,
            EmissiveSamplesEvaluated = EmissiveSamplesEvaluated,
            EmissiveRaysCast = EmissiveRaysCast,
            BakeMilliseconds = bakeMilliseconds,
            LightPreparationMilliseconds = TicksToMilliseconds(LightPreparationTicks),
            TextureRasterizationMilliseconds = TicksToMilliseconds(TextureRasterizationTicks),
            DirectLightingMilliseconds = TicksToMilliseconds(DirectLightingTicks),
            ShadowRayMilliseconds = TicksToMilliseconds(ShadowRayTicks),
            VertexSamplingMilliseconds = TicksToMilliseconds(VertexSamplingTicks),
            FilteringMilliseconds = TicksToMilliseconds(FilteringTicks),
            OccupiedTexelDiscoveryMilliseconds = TicksToMilliseconds(OccupiedTexelDiscoveryTicks),
            TextureConstructionMilliseconds = TicksToMilliseconds(TextureConstructionTicks)
        };
    }

    public readonly record struct StaticLightingSurfaceSample(
        Vector3 Position, Vector3 Normal, Vector3 Tangent, Vector3 Bitangent,
        Vector3 GeometricNormal = default, ExportEntry Source = null, int SourceTriangleIndex = -1,
        float WorldUnitsPerTexel = 1f);
}
