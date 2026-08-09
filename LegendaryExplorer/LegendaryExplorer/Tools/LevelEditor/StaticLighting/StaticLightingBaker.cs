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
    private static readonly Vector3[] DirectionalBasis =
    [
        Vector3.Normalize(new Vector3(MathF.Sqrt(2f / 3f), 0f, 1f / MathF.Sqrt(3f))),
        Vector3.Normalize(new Vector3(-1f / MathF.Sqrt(6f), 1f / MathF.Sqrt(2f), 1f / MathF.Sqrt(3f))),
        Vector3.Normalize(new Vector3(-1f / MathF.Sqrt(6f), -1f / MathF.Sqrt(2f), 1f / MathF.Sqrt(3f)))
    ];

    private readonly IReadOnlyList<StaticLightingMeshTarget> targets;
    private readonly IReadOnlyList<StaticLightingLight> lights;
    private readonly LevelCollisionScene collision;
    private readonly StaticLightingGenerationSettings settings;

    public StaticLightingBaker(IReadOnlyList<StaticLightingMeshTarget> targets,
        IReadOnlyList<StaticLightingLight> lights, LevelCollisionScene collision,
        StaticLightingGenerationSettings settings)
    {
        this.targets = targets;
        this.lights = lights;
        this.collision = collision;
        this.settings = settings;
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
        var results = new StaticLightingComponentBake[targets.Count];
        int textureMapped = 0;
        int vertexMapped = 0;
        int completed = 0;
        int workUnitCount = 0;
        var parallelOptions = CreateParallelOptions(cancellationToken);
        Parallel.For(0, targets.Count, parallelOptions, index =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            StaticLightingMeshTarget target = targets[index];
            TryCalculateBounds(target.Vertices, out Vector3 boundsMinimum, out Vector3 boundsMaximum);
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
            Stopwatch componentTimer = Stopwatch.StartNew();

            if (target.UseTextureMapping)
            {
                StaticLightingTextureBake texture = BakeTexture(target, affectingLights, counters, cancellationToken);
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
                StaticLightingVertexBake vertex = BakeVertices(target, affectingLights, counters, cancellationToken);
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
                ? $"UV{target.LightMapCoordinateIndex}, {diagnostic.MappedTexelCount:N0} texels"
                : target.HasTextureCoordinates
                    ? "authored vertex mapping"
                : diagnostic.Mapping.HasTextureMappingErrors
                    ? $"vertex fallback; UV errors: invalid={diagnostic.Mapping.InvalidUvVertexCount:N0}, " +
                      $"degenerate={diagnostic.Mapping.DegenerateUvTriangleCount:N0}, " +
                      $"overlap pairs={diagnostic.Mapping.OverlappingUvTrianglePairCount:N0}"
                    : "vertex mapping";
            progress?.Report($"Baked static lighting {completedCount:N0}/{targets.Count:N0}: " +
                             $"{target.Component.ObjectName.Instanced} ({mappingStatus}; " +
                             $"visibility {diagnostic.AverageVisibility:P1})");
        });

        bakeTimer.Stop();
        StaticLightingComponentDiagnostics[] diagnostics = results.Select(result => result.Diagnostics).ToArray();
        long visibilitySampleCount = diagnostics.Sum(item => item.VisibilitySampleCount);

        return new StaticLightingBakeResult
        {
            Components = results,
            SourceTriangleCount = collision.TriangleCount,
            LightCount = lights.Count,
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
            BakeMilliseconds = bakeTimer.Elapsed.TotalMilliseconds
        };
    }

    private ParallelOptions CreateParallelOptions(CancellationToken cancellationToken) => new()
    {
        CancellationToken = cancellationToken,
        MaxDegreeOfParallelism = settings.EffectiveWorkerThreads
    };

    public static (IReadOnlyList<StaticLightingMeshTarget> Targets, IReadOnlyList<StaticLightingLight> Lights,
        LevelCollisionScene Collision) BuildScene(IEnumerable<ActorProxy> actors,
        IReadOnlySet<OpenLevelFile> targetFiles, LevelEditorRenderContext renderContext,
        IReadOnlySet<ExportEntry> exactTargetComponents = null)
    {
        ActorProxy[] actorArray = actors.ToArray();
        var targets = new List<StaticLightingMeshTarget>();
        var occluders = new List<(Vector3 A, Vector3 B, Vector3 C, ExportEntry Source, int SourceTriangleIndex)>();
        var lights = new List<StaticLightingLight>();

        foreach (ActorProxy actor in actorArray)
        {
            if (TryCreateLight(actor, out StaticLightingLight light))
                lights.Add(light);

            if (actor.IsVolumetricMesh || actor is EmitterActorProxy or PawnProxy)
                continue;

            foreach (StaticMeshComponentProxy component in actor.Components.OfType<StaticMeshComponentProxy>())
            {
                if (!TryGetMesh(component, renderContext, out ExportEntry meshExport, out StaticMesh mesh) ||
                    mesh.LODModels is not { Length: > 0 })
                    continue;

                StaticMeshRenderData lod = mesh.LODModels[0];
                int lightMapCoordinateIndex = GetLightMapCoordinateIndex(meshExport, lod);
                IReadOnlyList<StaticLightingTriangle> triangles = BuildTriangles(lod, component.LocalToWorld,
                    lightMapCoordinateIndex, meshExport.InstancedFullPath, out StaticLightingVertex[] vertices,
                    out bool hasTextureCoordinates, out StaticLightingMappingDiagnostics mappingDiagnostics);
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
                bool canReceiveLighting = exactTargetComponents is null
                    ? IsStaticLightingTarget(component)
                    : isStaticCandidate && exactTargetComponents.Contains(component.Export);
                if (component.Actor.OwningFile is not { } owningFile || !targetFiles.Contains(owningFile) ||
                    !canReceiveLighting)
                    continue;

                StaticMeshComponent componentBinary = component.Export.GetBinaryData<StaticMeshComponent>();
                ELightMapType existingMappingType = GetExistingMappingType(componentBinary);
                bool generatedMapping = IsGeneratedTextureMapping(component.Export, componentBinary);
                int effectiveLightMapResolution = GetEffectiveLightMapResolution(component, meshExport);
                bool useTextureMapping = ShouldUseTextureMapping(hasTextureCoordinates,
                    effectiveLightMapResolution, existingMappingType, generatedMapping);
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
                    HasTextureCoordinates = hasTextureCoordinates,
                    UseTextureMapping = useTextureMapping,
                    MappingDiagnostics = mappingDiagnostics
                });
            }
        }

        return (targets, lights, LevelCollisionScene.FromTriangles(occluders));
    }

    private StaticLightingTextureBake BakeTexture(StaticLightingMeshTarget target,
        IReadOnlyList<StaticLightingLight> affectingLights, BakeCounters counters,
        CancellationToken cancellationToken)
    {
        int resolution = settings.TextureResolution;
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
        StaticLightingBakeTile[] tiles = CreateTextureWorkTiles(resolution, settings.WorkTileSize).ToArray();
        List<int>[] triangleBuckets = BuildTileTriangleBuckets(target.Triangles, tiles, resolution,
            coordinateScale, coordinateBias, settings.WorkTileSize);
        int[] activeTiles = Enumerable.Range(0, tiles.Length)
            .Where(index => triangleBuckets[index].Count > 0).ToArray();
        Parallel.ForEach(activeTiles, CreateParallelOptions(cancellationToken), tileIndex =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            StaticLightingBakeTile tile = tiles[tileIndex];
            foreach (int triangleIndex in triangleBuckets[tileIndex])
            {
                StaticLightingTriangle triangle = target.Triangles[triangleIndex];
                RasterizeTriangle(triangle, resolution, coordinateScale, coordinateBias, tile,
                (pixelIndex, barycentric) =>
                {
                    StaticLightingSurfaceSample candidate = Interpolate(triangle, barycentric,
                        target.Component, resolution);
                    int existingOwner = triangleOwners[pixelIndex];
                    if (existingOwner >= 0 && existingOwner != triangleIndex)
                    {
                        float tolerance = MathF.Max(0.001f,
                            MathF.Max(samples[pixelIndex].WorldUnitsPerTexel,
                                candidate.WorldUnitsPerTexel) * 0.02f);
                        if (Vector3.DistanceSquared(samples[pixelIndex].Position, candidate.Position) >
                            tolerance * tolerance)
                        {
                            if (!mappingConflicts[pixelIndex])
                            {
                                mappingConflicts[pixelIndex] = true;
                                Interlocked.Increment(ref counters.MappingConflictTexels);
                            }
                            return;
                        }
                    }
                    if (existingOwner >= 0)
                        return;
                    samples[pixelIndex] = candidate;
                    triangleOwners[pixelIndex] = triangleIndex;
                    mapped[pixelIndex] = true;
                });
            }

            for (int y = tile.MinimumY; y < tile.MaximumY; y++)
            for (int x = tile.MinimumX; x < tile.MaximumX; x++)
            {
                int pixelIndex = y * resolution + x;
                if (!mapped[pixelIndex]) continue;
                EvaluateLighting(samples[pixelIndex], target.LightingChannelMask, affectingLights,
                    coefficients, pixelIndex, useCompressedDirectionalLightMap, counters);
            }
        });

        counters.MappedTexels = mapped.Count(value => value);

        for (int coefficient = 0; coefficient < coefficients.Length; coefficient++)
            Dilate(coefficients[coefficient], mapped, resolution, 4, cancellationToken);
        var images = new List<byte[]>(coefficients.Length);
        var scales = new List<Vector3>(coefficients.Length);
        foreach (Vector3[] coefficient in coefficients)
        {
            Vector3 scale = CalculateScale(coefficient, mapped);
            scales.Add(scale);
            images.Add(EncodeColorImage(coefficient, scale, cancellationToken));
        }

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
        IReadOnlyList<StaticLightingLight> affectingLights, BakeCounters counters,
        CancellationToken cancellationToken)
    {
        bool useCompressedDirectionalLightMap = UsesCompressedDirectionalLightMap(target.Component.Game);
        int coefficientCount = useCompressedDirectionalLightMap ? 3 : 4;
        var coefficients = Enumerable.Range(0, coefficientCount)
            .Select(_ => new Vector3[target.Vertices.Count]).ToArray();
        float vertexShadowScale = EstimateVertexShadowScale(target);
        Parallel.ForEach(Partitioner.Create(0, target.Vertices.Count, 256),
            CreateParallelOptions(cancellationToken), range =>
        {
            for (int vertexIndex = range.Item1; vertexIndex < range.Item2; vertexIndex++)
            {
                StaticLightingVertex vertex = target.Vertices[vertexIndex];
                var sample = new StaticLightingSurfaceSample(vertex.Position, vertex.Normal, vertex.Tangent,
                    vertex.Bitangent, vertex.Normal, target.Component, -1, vertexShadowScale);
                EvaluateLighting(sample, target.LightingChannelMask, affectingLights, coefficients, vertexIndex,
                    useCompressedDirectionalLightMap, counters);
            }
        });

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

        return new StaticLightingVertexBake
        {
            DirectionalSamples = directionalSamples,
            SimpleSamples = simpleSamples,
            ScaleVectors = scales,
            ShadowMaps = []
        };
    }

    private void EvaluateLighting(StaticLightingSurfaceSample sample, uint targetChannels,
        IReadOnlyList<StaticLightingLight> affectingLights, Vector3[][] coefficients, int sampleIndex,
        bool useCompressedDirectionalLightMap, BakeCounters counters)
    {
        // Environment/indirect energy is accumulated independently and is never attenuated by a
        // direct light's visibility. This prevents several occluders or lights from stacking black.
        Vector3 environment = new(settings.AmbientIntensity);
        for (int lightIndex = 0; lightIndex < affectingLights.Count; lightIndex++)
        {
            StaticLightingLight light = affectingLights[lightIndex];
            if (light.Type == StaticLightingLightType.Sky &&
                SceneLight.ChannelsOverlap(light.LightingChannelMask, targetChannels))
                environment += Vector3.Max(Vector3.Zero, light.Color * light.Intensity);
        }

        Vector3 simple = environment;
        Vector3 totalDirect = Vector3.Zero;
        float isotropicMaximum = MaxComponent(environment);
        Span<Vector3> directional = stackalloc Vector3[3];

        for (int lightIndex = 0; lightIndex < affectingLights.Count; lightIndex++)
        {
            StaticLightingLight light = affectingLights[lightIndex];
            if (!SceneLight.ChannelsOverlap(light.LightingChannelMask, targetChannels))
                continue;
            if (light.Type == StaticLightingLightType.Sky)
                continue;

            int lightSampleCount = GetLightSampleCount(light);
            Vector3 sampledIrradiance = Vector3.Zero;
            Span<Vector3> sampledDirectional = stackalloc Vector3[3];
            for (int lightSampleIndex = 0; lightSampleIndex < lightSampleCount; lightSampleIndex++)
            {
                StaticLightingLight sampledLight = GetSampledLight(light, sample.Position,
                    lightSampleIndex, lightSampleCount);
                if (!TryEvaluateLight(sampledLight, sample, out Vector3 surfaceToLight,
                        out Vector3 unshadowed, out Vector3 irradiance))
                    continue;

                bool visible = true;
                if (light.CastsShadow)
                {
                    float epsilon = CalculateShadowEpsilon(sample);
                    Vector3 geometricNormal = GetReceiverGeometricNormal(sample);
                    Vector3 origin = sample.Position + geometricNormal * epsilon;
                    float maximumDistance = light.Type == StaticLightingLightType.Directional
                        ? 10_000_000f
                        : MathF.Max(epsilon,
                            Vector3.Distance(sampledLight.Position, sample.Position) - epsilon * 2f);
                    Interlocked.Increment(ref counters.RaysCast);
                    bool occluded = collision.RaycastFiltered(origin, surfaceToLight, maximumDistance,
                        sample.Source, sample.SourceTriangleIndex, epsilon * 4f,
                        out _, out int rejectedSelfIntersections);
                    if (rejectedSelfIntersections > 0)
                        Interlocked.Add(ref counters.RejectedSelfIntersections, rejectedSelfIntersections);
                    Interlocked.Increment(ref counters.VisibilitySampleCount);
                    if (occluded)
                    {
                        Interlocked.Increment(ref counters.OccludedSamples);
                        visible = false;
                    }
                    else
                    {
                        Interlocked.Add(ref counters.VisibilityMicroSum, BakeCounters.MicroScale);
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

            float inverseSampleCount = 1f / lightSampleCount;
            Vector3 direct = sampledIrradiance * inverseSampleCount;
            simple += direct;
            totalDirect += direct;
            for (int basisIndex = 0; basisIndex < directional.Length; basisIndex++)
                directional[basisIndex] += sampledDirectional[basisIndex] * inverseSampleCount;
        }

        Interlocked.Increment(ref counters.LitSampleCount);
        Interlocked.Add(ref counters.DirectContributionMicroSum,
            ToMicro(MaxComponent(totalDirect)));
        Interlocked.Add(ref counters.EnvironmentContributionMicroSum,
            ToMicro(MaxComponent(environment)));

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

    private StaticLightingLight GetSampledLight(StaticLightingLight light, Vector3 receiverPosition,
        int sampleIndex, int sampleCount)
    {
        if (sampleCount <= 1)
            return light;

        float seedAngle = (uint)light.Guid.GetHashCode() * (2f * MathF.PI / uint.MaxValue);
        float radius = MathF.Sqrt((sampleIndex + 0.5f) / sampleCount);
        float angle = seedAngle + sampleIndex * 2.39996323f;
        Vector2 disk = new(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
        if (light.Type == StaticLightingLightType.Directional)
        {
            Vector3 direction = SafeNormal(light.Direction, Vector3.UnitX);
            CreateOrthonormalBasis(direction, out Vector3 right, out Vector3 up);
            float tangent = MathF.Tan(settings.DirectionalSourceAngleDegrees * MathF.PI / 180f);
            return light with { Direction = SafeNormal(direction + (right * disk.X + up * disk.Y) * tangent, direction) };
        }

        float sourceRadius = GetEffectiveSourceRadius(light);
        Vector3 lightToReceiver = SafeNormal(receiverPosition - light.Position, light.Direction);
        CreateOrthonormalBasis(lightToReceiver, out Vector3 sourceRight, out Vector3 sourceUp);
        return light with { Position = light.Position + (sourceRight * disk.X + sourceUp * disk.Y) * sourceRadius };
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

    private static void RasterizeTriangle(StaticLightingTriangle triangle, int resolution,
        Vector2 coordinateScale, Vector2 coordinateBias, StaticLightingBakeTile tile,
        Action<int, Vector3> writeSample)
    {
        Vector2 a = triangle.A.LightMapCoordinate * coordinateScale + coordinateBias;
        Vector2 b = triangle.B.LightMapCoordinate * coordinateScale + coordinateBias;
        Vector2 c = triangle.C.LightMapCoordinate * coordinateScale + coordinateBias;
        float denominator = Cross(b - a, c - a);
        if (MathF.Abs(denominator) < 0.0000001f) return;
        if (!TryGetRasterBounds(triangle, resolution, coordinateScale, coordinateBias,
                out int minimumX, out int minimumY, out int maximumX, out int maximumY))
            return;
        minimumX = Math.Max(minimumX, tile.MinimumX);
        maximumX = Math.Min(maximumX, tile.MaximumX - 1);
        minimumY = Math.Max(minimumY, tile.MinimumY);
        maximumY = Math.Min(maximumY, tile.MaximumY - 1);
        if (minimumX > maximumX || minimumY > maximumY) return;

        for (int y = minimumY; y <= maximumY; y++)
        for (int x = minimumX; x <= maximumX; x++)
        {
            Vector2 point = new((x + 0.5f) / resolution, (y + 0.5f) / resolution);
            float v = Cross(point - a, c - a) / denominator;
            float w = Cross(b - a, point - a) / denominator;
            float u = 1f - v - w;
            const float edgeTolerance = -0.0001f;
            if (u >= edgeTolerance && v >= edgeTolerance && w >= edgeTolerance)
                writeSample(y * resolution + x, new Vector3(u, v, w));
        }
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
        ExportEntry source, int resolution)
    {
        Vector3 position = triangle.A.Position * weights.X + triangle.B.Position * weights.Y + triangle.C.Position * weights.Z;
        Vector3 geometricNormal = SafeNormal(Vector3.Cross(triangle.B.Position - triangle.A.Position,
            triangle.C.Position - triangle.A.Position), Vector3.UnitZ);
        Vector3 normal = SafeNormal(triangle.A.Normal * weights.X + triangle.B.Normal * weights.Y + triangle.C.Normal * weights.Z,
            geometricNormal);
        if (Vector3.Dot(geometricNormal, normal) < 0f)
            geometricNormal = -geometricNormal;
        Vector3 tangent = SafeNormal(triangle.A.Tangent * weights.X + triangle.B.Tangent * weights.Y + triangle.C.Tangent * weights.Z,
            Vector3.UnitX);
        tangent = SafeNormal(tangent - normal * Vector3.Dot(tangent, normal), Vector3.UnitX);
        Vector3 bitangent = SafeNormal(Vector3.Cross(normal, tangent), Vector3.UnitY);
        return new StaticLightingSurfaceSample(position, normal, tangent, bitangent, geometricNormal, source,
            triangle.SourceTriangleIndex, EstimateTriangleWorldUnitsPerTexel(triangle, resolution));
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
        CancellationToken cancellationToken)
    {
        var bytes = new byte[samples.Count * 4];
        Parallel.ForEach(Partitioner.Create(0, samples.Count, 4096),
            CreateParallelOptions(cancellationToken), range =>
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

    private void Dilate<T>(T[] values, bool[] mapped, int resolution, int iterations,
        CancellationToken cancellationToken) where T : struct
    {
        var occupied = (bool[])mapped.Clone();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            T[] source = (T[])values.Clone();
            bool[] sourceOccupied = (bool[])occupied.Clone();
            int changed = 0;
            Parallel.For(0, resolution, CreateParallelOptions(cancellationToken), y =>
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
                        values[index] = source[neighbor];
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

    /// <summary>
    /// UE3 uses singular shadow flags on primitive components and plural variants on lights and a few
    /// BioWare subclasses. Every explicit false is authoritative; absent properties keep UE3 defaults.
    /// </summary>
    public static bool CastsStaticShadow(PropertyCollection properties) =>
        properties.GetProp<BoolProperty>("CastShadow")?.Value != false &&
        properties.GetProp<BoolProperty>("CastShadows")?.Value != false &&
        properties.GetProp<BoolProperty>("bCastStaticShadow")?.Value != false &&
        properties.GetProp<BoolProperty>("CastStaticShadows")?.Value != false;

    public static bool ShouldUseTextureMapping(bool hasValidTextureCoordinates,
        int effectiveLightMapResolution, ELightMapType existingMappingType,
        bool existingMappingWasGenerated)
    {
        if (!hasValidTextureCoordinates)
            return false;

        if (!existingMappingWasGenerated)
        {
            if (existingMappingType is ELightMapType.LMT_1D or ELightMapType.LMT_3 or ELightMapType.LMT_5)
                return false;
            if (existingMappingType is ELightMapType.LMT_2D or ELightMapType.LMT_4 or ELightMapType.LMT_6)
                return true;
        }

        return effectiveLightMapResolution > 0;
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

    private static int GetLightMapCoordinateIndex(ExportEntry meshExport, StaticMeshRenderData lod)
    {
        return Math.Max(0, meshExport.GetProperty<IntProperty>("LightMapCoordinateIndex")?.Value ?? 1);
    }

    private static IReadOnlyList<StaticLightingTriangle> BuildTriangles(StaticMeshRenderData lod,
        Matrix4x4 localToWorld, int coordinateIndex, string meshPath, out StaticLightingVertex[] vertices,
        out bool hasTextureCoordinates, out StaticLightingMappingDiagnostics diagnostics)
    {
        Vector3[] positions = lod.PositionVertexBuffer?.VertexData ?? [];
        StaticMeshVertexBuffer.StaticMeshFullVertex[] sourceVertices = lod.VertexBuffer?.VertexData ?? [];
        int count = Math.Min(positions.Length, sourceVertices.Length);
        vertices = new StaticLightingVertex[count];
        bool coordinateChannelAvailable = count > 0 && coordinateIndex >= 0 &&
                                          lod.VertexBuffer is not null &&
                                          lod.VertexBuffer.NumTexCoords > coordinateIndex;
        var validCoordinates = new bool[count];
        for (int index = 0; index < count; index++)
        {
            StaticMeshVertexBuffer.StaticMeshFullVertex source = sourceVertices[index];
            Vector3 position = Vector3.Transform(positions[index], localToWorld);
            Vector3 normal = TransformNormal((Vector3)source.TangentZ, localToWorld, Vector3.UnitZ);
            Vector3 tangent = TransformNormal((Vector3)source.TangentX, localToWorld, Vector3.UnitX);
            Vector3 bitangent = SafeNormal(Vector3.Cross(normal, tangent) * (((Vector4)source.TangentZ).W < 0f ? -1f : 1f),
                Vector3.UnitY);
            Vector2 coordinate = default;
            if (coordinateChannelAvailable)
            {
                coordinate = lod.VertexBuffer.bUseFullPrecisionUVs
                    ? source.FullPrecisionUVs[coordinateIndex]
                    : new Vector2(source.HalfPrecisionUVs[coordinateIndex].X,
                        source.HalfPrecisionUVs[coordinateIndex].Y);
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
        HashSet<int> referencedVertices = [];
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
                referencedVertices.Add(first);
                referencedVertices.Add(second);
                referencedVertices.Add(third);
                StaticLightingTriangle triangle = new(vertices[first], vertices[second], vertices[third])
                {
                    SectionIndex = sectionIndex,
                    SourceTriangleIndex = offset / 3
                };
                if (Vector3.Cross(triangle.B.Position - triangle.A.Position,
                        triangle.C.Position - triangle.A.Position).LengthSquared() > 0.0001f)
                {
                    triangles.Add(triangle);
                    if (coordinateChannelAvailable && MathF.Abs(Cross(
                            triangle.B.LightMapCoordinate - triangle.A.LightMapCoordinate,
                            triangle.C.LightMapCoordinate - triangle.A.LightMapCoordinate)) < 0.0000001f)
                        degenerateUvTriangles++;
                }
            }
        }

        int invalidUvVertices = coordinateChannelAvailable
            ? referencedVertices.Count(index => !validCoordinates[index])
            : 0;
        int overlappingUvPairs = coordinateChannelAvailable && invalidUvVertices == 0 &&
                                 degenerateUvTriangles == 0
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
        const float tolerance = 0.00001f;
        return firstT > tolerance && firstT < 1f - tolerance &&
               secondT > tolerance && secondT < 1f - tolerance;
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

    private static Vector3 TransformNormal(Vector3 value, Matrix4x4 matrix, Vector3 fallback)
    {
        if (Matrix4x4.Invert(matrix, out Matrix4x4 inverse))
            return SafeNormal(Vector3.TransformNormal(value, Matrix4x4.Transpose(inverse)), fallback);
        return SafeNormal(Vector3.TransformNormal(value, matrix), fallback);
    }

    private static Vector3 SafeNormal(Vector3 value, Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 0.000001f && float.IsFinite(lengthSquared)
            ? value / MathF.Sqrt(lengthSquared)
            : Vector3.Normalize(fallback);
    }

    private static float Cross(Vector2 left, Vector2 right) => left.X * right.Y - left.Y * right.X;

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
            BakeMilliseconds = bakeMilliseconds
        };
    }

    public readonly record struct StaticLightingSurfaceSample(
        Vector3 Position, Vector3 Normal, Vector3 Tangent, Vector3 Bitangent,
        Vector3 GeometricNormal = default, ExportEntry Source = null, int SourceTriangleIndex = -1,
        float WorldUnitsPerTexel = 1f);
}
