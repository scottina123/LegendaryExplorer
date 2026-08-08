using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
            StaticLightingLight[] affectingLights = lights.Where(light =>
                LightCanAffect(light, target.LightingChannelMask, target.Vertices)).ToArray();
            Guid[] lightGuids = affectingLights.Select(light => light.Guid).Distinct().ToArray();

            if (target.HasTextureCoordinates)
            {
                StaticLightingTextureBake texture = BakeTexture(target, affectingLights, cancellationToken);
                results[index] = new StaticLightingComponentBake
                {
                    Target = target,
                    LightGuids = lightGuids,
                    Texture = texture
                };
                Interlocked.Increment(ref textureMapped);
                Interlocked.Add(ref workUnitCount, texture.WorkUnitCount);
            }
            else
            {
                results[index] = new StaticLightingComponentBake
                {
                    Target = target,
                    LightGuids = lightGuids,
                    Vertex = BakeVertices(target, affectingLights, cancellationToken)
                };
                Interlocked.Increment(ref vertexMapped);
                Interlocked.Add(ref workUnitCount, Math.Max(1, (target.Vertices.Count + 255) / 256));
            }
            int completedCount = Interlocked.Increment(ref completed);
            progress?.Report($"Baked static lighting {completedCount:N0}/{targets.Count:N0}: " +
                             target.Component.ObjectName.Instanced);
        });

        return new StaticLightingBakeResult
        {
            Components = results,
            SourceTriangleCount = collision.TriangleCount,
            LightCount = lights.Count,
            TextureMappedComponentCount = textureMapped,
            VertexMappedComponentCount = vertexMapped,
            WorkUnitCount = workUnitCount,
            WorkerCount = settings.EffectiveWorkerThreads
        };
    }

    private ParallelOptions CreateParallelOptions(CancellationToken cancellationToken) => new()
    {
        CancellationToken = cancellationToken,
        MaxDegreeOfParallelism = settings.EffectiveWorkerThreads
    };

    public static (IReadOnlyList<StaticLightingMeshTarget> Targets, IReadOnlyList<StaticLightingLight> Lights,
        LevelCollisionScene Collision) BuildScene(IEnumerable<ActorProxy> actors,
        IReadOnlySet<OpenLevelFile> targetFiles, LevelEditorRenderContext renderContext)
    {
        ActorProxy[] actorArray = actors.ToArray();
        var targets = new List<StaticLightingMeshTarget>();
        var occluders = new List<(Vector3 A, Vector3 B, Vector3 C)>();
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
                IReadOnlyList<StaticLightingTriangle> triangles = BuildTriangles(lod, component.LocalToWorld,
                    GetLightMapCoordinateIndex(meshExport, lod), out StaticLightingVertex[] vertices,
                    out bool hasTextureCoordinates);
                foreach (StaticLightingTriangle triangle in triangles)
                    occluders.Add((triangle.A.Position, triangle.B.Position, triangle.C.Position));

                if (component.Actor.OwningFile is not { } owningFile || !targetFiles.Contains(owningFile) ||
                    !IsStaticLightingTarget(component))
                    continue;

                targets.Add(new StaticLightingMeshTarget
                {
                    File = owningFile,
                    Component = component.Export,
                    ComponentBinary = component.Export.GetBinaryData<StaticMeshComponent>(),
                    MeshLod = lod,
                    LocalToWorld = component.LocalToWorld,
                    LightingChannelMask = component.LightingChannelMask,
                    Triangles = triangles,
                    Vertices = vertices,
                    LightMapCoordinateIndex = GetLightMapCoordinateIndex(meshExport, lod),
                    HasTextureCoordinates = hasTextureCoordinates
                });
            }
        }

        return (targets, lights, LevelCollisionScene.FromTriangles(occluders));
    }

    private StaticLightingTextureBake BakeTexture(StaticLightingMeshTarget target,
        IReadOnlyList<StaticLightingLight> affectingLights, CancellationToken cancellationToken)
    {
        int resolution = settings.TextureResolution;
        int pixelCount = resolution * resolution;
        int directionalCount = target.Component.Game < MEGame.ME3 ? 3 : 2;
        var coefficients = Enumerable.Range(0, directionalCount + 1)
            .Select(_ => new Vector3[pixelCount]).ToArray();
        var mapped = new bool[pixelCount];
        StaticLightingLight[] shadowLights = settings.GenerateShadowMaps
            ? affectingLights.Where(light => light.CastsShadow).ToArray()
            : [];
        var shadowVisibility = shadowLights.Select(_ => new byte[pixelCount]).ToArray();
        int[] shadowIndices = CreateShadowIndices(affectingLights);
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
                    samples[pixelIndex] = Interpolate(triangle, barycentric);
                    mapped[pixelIndex] = true;
                });
            }

            for (int y = tile.MinimumY; y < tile.MaximumY; y++)
            for (int x = tile.MinimumX; x < tile.MaximumX; x++)
            {
                int pixelIndex = y * resolution + x;
                if (!mapped[pixelIndex]) continue;
                EvaluateLighting(samples[pixelIndex], target.LightingChannelMask, affectingLights,
                    coefficients, pixelIndex, shadowIndices, shadowVisibility);
            }
        });

        for (int coefficient = 0; coefficient < coefficients.Length; coefficient++)
            Dilate(coefficients[coefficient], mapped, resolution, 4, cancellationToken);
        foreach (byte[] visibility in shadowVisibility)
            Dilate(visibility, mapped, resolution, 4, cancellationToken);

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
            ShadowMaps = shadowLights.Select((light, index) => new StaticLightingShadowBake
            {
                LightGuid = light.Guid,
                Visibility = shadowVisibility[index]
            }).ToArray(),
            CoordinateScale = coordinateScale,
            CoordinateBias = coordinateBias,
            WorkUnitCount = Math.Max(1, activeTiles.Length)
        };
    }

    private StaticLightingVertexBake BakeVertices(StaticLightingMeshTarget target,
        IReadOnlyList<StaticLightingLight> affectingLights, CancellationToken cancellationToken)
    {
        int directionalCount = target.Component.Game < MEGame.ME3 ? 3 : 2;
        var coefficients = Enumerable.Range(0, directionalCount + 1)
            .Select(_ => new Vector3[target.Vertices.Count]).ToArray();
        StaticLightingLight[] shadowLights = settings.GenerateShadowMaps
            ? affectingLights.Where(light => light.CastsShadow).ToArray()
            : [];
        var shadowVisibility = shadowLights.Select(_ => new byte[target.Vertices.Count]).ToArray();
        int[] shadowIndices = CreateShadowIndices(affectingLights);
        Parallel.ForEach(Partitioner.Create(0, target.Vertices.Count, 256),
            CreateParallelOptions(cancellationToken), range =>
        {
            for (int vertexIndex = range.Item1; vertexIndex < range.Item2; vertexIndex++)
            {
                StaticLightingVertex vertex = target.Vertices[vertexIndex];
                var sample = new StaticLightingSurfaceSample(vertex.Position, vertex.Normal, vertex.Tangent,
                    vertex.Bitangent);
                EvaluateLighting(sample, target.LightingChannelMask, affectingLights, coefficients, vertexIndex,
                    shadowIndices, shadowVisibility);
            }
        });

        var scales = coefficients.Select(coefficient => CalculateScale(coefficient, null)).ToArray();
        var directionalSamples = new QuantizedDirectionalLightSample[target.Vertices.Count];
        var simpleSamples = new QuantizedSimpleLightSample[target.Vertices.Count];
        for (int index = 0; index < target.Vertices.Count; index++)
        {
            directionalSamples[index] = new QuantizedDirectionalLightSample
            {
                Coefficient1 = ToColor(coefficients[0][index], scales[0]),
                Coefficient2 = ToColor(coefficients[target.Component.Game < MEGame.ME3 ? 1 : 0][index],
                    scales[target.Component.Game < MEGame.ME3 ? 1 : 0]),
                Coefficient3 = ToColor(coefficients[target.Component.Game < MEGame.ME3 ? 2 : 1][index],
                    scales[target.Component.Game < MEGame.ME3 ? 2 : 1])
            };
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
            ShadowMaps = shadowLights.Select((light, index) => new StaticLightingShadowBake
            {
                LightGuid = light.Guid,
                Visibility = shadowVisibility[index]
            }).ToArray()
        };
    }

    private void EvaluateLighting(StaticLightingSurfaceSample sample, uint targetChannels,
        IReadOnlyList<StaticLightingLight> affectingLights, Vector3[][] coefficients, int sampleIndex,
        IReadOnlyList<int> shadowIndices, IReadOnlyList<byte[]> shadowVisibility)
    {
        Vector3 simple = new(settings.AmbientIntensity);
        int directionalCount = coefficients.Length - 1;
        Span<Vector3> directional = stackalloc Vector3[3];

        for (int lightIndex = 0; lightIndex < affectingLights.Count; lightIndex++)
        {
            StaticLightingLight light = affectingLights[lightIndex];
            if (!SceneLight.ChannelsOverlap(light.LightingChannelMask, targetChannels))
                continue;
            if (light.Type == StaticLightingLightType.Sky)
            {
                simple += light.Color * light.Intensity;
                continue;
            }

            if (!TryEvaluateLight(light, sample, out Vector3 surfaceToLight, out Vector3 unshadowed,
                    out Vector3 irradiance))
                continue;
            bool visible = !collision.Raycast(sample.Position + sample.Normal * settings.ShadowBias,
                surfaceToLight, light.Type == StaticLightingLightType.Directional
                    ? 10_000_000f
                    : MathF.Max(settings.ShadowBias, Vector3.Distance(light.Position, sample.Position) - settings.ShadowBias),
                out _);
            int shadowIndex = shadowIndices[lightIndex];
            if (shadowIndex >= 0)
                shadowVisibility[shadowIndex][sampleIndex] = visible ? byte.MaxValue : byte.MinValue;
            if (!visible)
                continue;

            simple += irradiance;
            Vector3 tangentDirection = new(
                Vector3.Dot(surfaceToLight, sample.Tangent),
                Vector3.Dot(surfaceToLight, sample.Bitangent),
                Vector3.Dot(surfaceToLight, sample.Normal));
            tangentDirection = SafeNormal(tangentDirection, Vector3.UnitZ);
            for (int basisIndex = 0; basisIndex < directionalCount; basisIndex++)
            {
                int sourceBasis = directionalCount == 2 ? basisIndex + 1 : basisIndex;
                directional[basisIndex] += unshadowed * MathF.Max(0f,
                    Vector3.Dot(tangentDirection, DirectionalBasis[sourceBasis]));
            }
        }

        for (int index = 0; index < directionalCount; index++)
            coefficients[index][sampleIndex] = Vector3.Max(Vector3.Zero, directional[index]);
        coefficients[^1][sampleIndex] = Vector3.Max(Vector3.Zero, simple);
    }

    private int[] CreateShadowIndices(IReadOnlyList<StaticLightingLight> affectingLights)
    {
        var indices = new int[affectingLights.Count];
        Array.Fill(indices, -1);
        if (!settings.GenerateShadowMaps)
            return indices;

        int shadowIndex = 0;
        for (int lightIndex = 0; lightIndex < affectingLights.Count; lightIndex++)
        {
            if (affectingLights[lightIndex].CastsShadow)
                indices[lightIndex] = shadowIndex++;
        }
        return indices;
    }

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

    private static bool LightCanAffect(StaticLightingLight light, uint targetChannels,
        IReadOnlyList<StaticLightingVertex> vertices)
    {
        if (!SceneLight.ChannelsOverlap(light.LightingChannelMask, targetChannels))
            return false;
        if (light.Type is StaticLightingLightType.Directional or StaticLightingLightType.Sky)
            return true;
        float radiusSquared = light.Radius * light.Radius;
        return vertices.Any(vertex => Vector3.DistanceSquared(vertex.Position, light.Position) < radiusSquared);
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

    private static StaticLightingSurfaceSample Interpolate(StaticLightingTriangle triangle, Vector3 weights)
    {
        Vector3 position = triangle.A.Position * weights.X + triangle.B.Position * weights.Y + triangle.C.Position * weights.Z;
        Vector3 normal = SafeNormal(triangle.A.Normal * weights.X + triangle.B.Normal * weights.Y + triangle.C.Normal * weights.Z,
            Vector3.Cross(triangle.B.Position - triangle.A.Position, triangle.C.Position - triangle.A.Position));
        Vector3 tangent = SafeNormal(triangle.A.Tangent * weights.X + triangle.B.Tangent * weights.Y + triangle.C.Tangent * weights.Z,
            Vector3.UnitX);
        tangent = SafeNormal(tangent - normal * Vector3.Dot(tangent, normal), Vector3.UnitX);
        Vector3 bitangent = SafeNormal(Vector3.Cross(normal, tangent), Vector3.UnitY);
        return new StaticLightingSurfaceSample(position, normal, tangent, bitangent);
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
    {
        string actorClass = component.Actor.Export.ClassName;
        if (actorClass.Contains("Dynamic", StringComparison.OrdinalIgnoreCase) ||
            actorClass.Contains("InterpActor", StringComparison.OrdinalIgnoreCase) ||
            actorClass.Contains("KActor", StringComparison.OrdinalIgnoreCase))
            return false;
        return component.Properties.GetProp<BoolProperty>("bAcceptsLights")?.Value != false &&
               component.Properties.GetProp<BoolProperty>("bUsePrecomputedShadows")?.Value != false;
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
        Matrix4x4 localToWorld, int coordinateIndex, out StaticLightingVertex[] vertices,
        out bool hasTextureCoordinates)
    {
        Vector3[] positions = lod.PositionVertexBuffer?.VertexData ?? [];
        StaticMeshVertexBuffer.StaticMeshFullVertex[] sourceVertices = lod.VertexBuffer?.VertexData ?? [];
        int count = Math.Min(positions.Length, sourceVertices.Length);
        vertices = new StaticLightingVertex[count];
        hasTextureCoordinates = count > 0 && lod.VertexBuffer.NumTexCoords > coordinateIndex;
        for (int index = 0; index < count; index++)
        {
            StaticMeshVertexBuffer.StaticMeshFullVertex source = sourceVertices[index];
            Vector3 position = Vector3.Transform(positions[index], localToWorld);
            Vector3 normal = TransformNormal((Vector3)source.TangentZ, localToWorld, Vector3.UnitZ);
            Vector3 tangent = TransformNormal((Vector3)source.TangentX, localToWorld, Vector3.UnitX);
            Vector3 bitangent = SafeNormal(Vector3.Cross(normal, tangent) * (((Vector4)source.TangentZ).W < 0f ? -1f : 1f),
                Vector3.UnitY);
            Vector2 coordinate = default;
            if (hasTextureCoordinates)
            {
                coordinate = lod.VertexBuffer.bUseFullPrecisionUVs
                    ? source.FullPrecisionUVs[coordinateIndex]
                    : new Vector2(source.HalfPrecisionUVs[coordinateIndex].X,
                        source.HalfPrecisionUVs[coordinateIndex].Y);
                hasTextureCoordinates &= float.IsFinite(coordinate.X) && float.IsFinite(coordinate.Y) &&
                                         coordinate.X is >= -0.0001f and <= 1.0001f &&
                                         coordinate.Y is >= -0.0001f and <= 1.0001f;
            }
            vertices[index] = new StaticLightingVertex(position, normal, tangent, bitangent, coordinate);
        }

        var triangles = new List<StaticLightingTriangle>();
        ushort[] indices = lod.IndexBuffer ?? [];
        foreach (StaticMeshElement element in lod.Elements ?? [])
        {
            int end = Math.Min(indices.Length, (int)element.FirstIndex + (int)element.NumTriangles * 3);
            for (int offset = (int)element.FirstIndex; offset + 2 < end; offset += 3)
            {
                int first = indices[offset];
                int second = indices[offset + 1];
                int third = indices[offset + 2];
                if ((uint)first >= vertices.Length || (uint)second >= vertices.Length || (uint)third >= vertices.Length)
                    continue;
                StaticLightingTriangle triangle = new(vertices[first], vertices[second], vertices[third]);
                if (Vector3.Cross(triangle.B.Position - triangle.A.Position,
                        triangle.C.Position - triangle.A.Position).LengthSquared() > 0.0001f)
                    triangles.Add(triangle);
            }
        }
        return triangles;
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

        if (actor.TryGetSceneLight(out SceneLight sceneLight))
        {
            light = new StaticLightingLight(guid,
                sceneLight.IsSpot ? StaticLightingLightType.Spot : StaticLightingLightType.Point,
                sceneLight.Position, sceneLight.Direction, sceneLight.Color, sceneLight.Intensity,
                sceneLight.Radius, sceneLight.InnerConeAngleDegrees, sceneLight.OuterConeAngleDegrees,
                sceneLight.LightingChannelMask, castsStaticShadow);
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

    public readonly record struct StaticLightingSurfaceSample(
        Vector3 Position, Vector3 Normal, Vector3 Tangent, Vector3 Bitangent);
}
