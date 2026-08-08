using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace LegendaryExplorer.Tools.LevelEditor;

public enum StaticLightingLightType
{
    Point,
    Spot,
    Directional,
    Sky
}

public readonly record struct StaticLightingLight(
    Guid Guid,
    StaticLightingLightType Type,
    Vector3 Position,
    Vector3 Direction,
    Vector3 Color,
    float Intensity,
    float Radius,
    float InnerConeAngleDegrees,
    float OuterConeAngleDegrees,
    uint LightingChannelMask,
    bool CastsStaticShadow = true)
{
    public bool CastsShadow => Type != StaticLightingLightType.Sky && CastsStaticShadow;
}

public sealed class StaticLightingGenerationSettings
{
    public int TextureResolution { get; set; } = 64;
    public float AmbientIntensity { get; set; } = 0.03f;
    public float ShadowBias { get; set; } = 1f;
    public string TextureCacheName { get; set; } = "";
    public bool GenerateShadowMaps { get; set; } = true;
    public int WorkerThreads { get; set; }
    public int WorkTileSize { get; set; } = 16;

    public int EffectiveWorkerThreads => WorkerThreads > 0
        ? WorkerThreads
        : Math.Max(1, Environment.ProcessorCount);

    public void Validate()
    {
        if (TextureResolution is < 64 or > 1024 || !BitOperations.IsPow2((uint)TextureResolution))
            throw new ArgumentOutOfRangeException(nameof(TextureResolution),
                "Lightmap resolution must be a power of two from 64 through 1024.");
        if (!float.IsFinite(AmbientIntensity) || AmbientIntensity < 0f)
            throw new ArgumentOutOfRangeException(nameof(AmbientIntensity));
        if (!float.IsFinite(ShadowBias) || ShadowBias is < 0.01f or > 100f)
            throw new ArgumentOutOfRangeException(nameof(ShadowBias));
        if (WorkerThreads is < 0 or > 256)
            throw new ArgumentOutOfRangeException(nameof(WorkerThreads),
                "Worker threads must be 0 (automatic) or from 1 through 256.");
        if (WorkTileSize is < 8 or > 128 || !BitOperations.IsPow2((uint)WorkTileSize))
            throw new ArgumentOutOfRangeException(nameof(WorkTileSize),
                "Bake tile size must be a power of two from 8 through 128.");
        if (!string.IsNullOrWhiteSpace(TextureCacheName) &&
            TextureCacheName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("The texture-cache name contains invalid filename characters.",
                nameof(TextureCacheName));
    }
}

public readonly record struct StaticLightingVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector3 Tangent,
    Vector3 Bitangent,
    Vector2 LightMapCoordinate);

public readonly record struct StaticLightingTriangle(
    StaticLightingVertex A,
    StaticLightingVertex B,
    StaticLightingVertex C);

public sealed class StaticLightingMeshTarget
{
    public required OpenLevelFile File { get; init; }
    public required ExportEntry Component { get; init; }
    public required StaticMeshComponent ComponentBinary { get; init; }
    public required StaticMeshRenderData MeshLod { get; init; }
    public required Matrix4x4 LocalToWorld { get; init; }
    public required uint LightingChannelMask { get; init; }
    public required IReadOnlyList<StaticLightingTriangle> Triangles { get; init; }
    public required IReadOnlyList<StaticLightingVertex> Vertices { get; init; }
    public int LightMapCoordinateIndex { get; init; }
    public bool HasTextureCoordinates { get; init; }
}

public sealed class StaticLightingTextureBake
{
    public required int Resolution { get; init; }
    public required IReadOnlyList<byte[]> CoefficientImages { get; init; }
    public required IReadOnlyList<Vector3> ScaleVectors { get; init; }
    public required IReadOnlyList<StaticLightingShadowBake> ShadowMaps { get; init; }
    public required Vector2 CoordinateScale { get; init; }
    public required Vector2 CoordinateBias { get; init; }
    public int WorkUnitCount { get; init; }
}

public sealed class StaticLightingVertexBake
{
    public required QuantizedDirectionalLightSample[] DirectionalSamples { get; init; }
    public required QuantizedSimpleLightSample[] SimpleSamples { get; init; }
    public required IReadOnlyList<Vector3> ScaleVectors { get; init; }
    public required IReadOnlyList<StaticLightingShadowBake> ShadowMaps { get; init; }
}

public sealed class StaticLightingShadowBake
{
    public required Guid LightGuid { get; init; }
    public required byte[] Visibility { get; init; }
}

public sealed class StaticLightingComponentBake
{
    public required StaticLightingMeshTarget Target { get; init; }
    public required Guid[] LightGuids { get; init; }
    public StaticLightingTextureBake Texture { get; init; }
    public StaticLightingVertexBake Vertex { get; init; }
}

public sealed class StaticLightingBakeResult
{
    public required IReadOnlyList<StaticLightingComponentBake> Components { get; init; }
    public required int SourceTriangleCount { get; init; }
    public required int LightCount { get; init; }
    public required int TextureMappedComponentCount { get; init; }
    public required int VertexMappedComponentCount { get; init; }
    public int WorkUnitCount { get; init; }
    public int WorkerCount { get; init; }
}

public sealed class StaticLightingWriteResult
{
    public int ComponentCount { get; init; }
    public int LightMapTextureCount { get; init; }
    public int ShadowMapCount { get; init; }
    public IReadOnlyList<string> TextureCachePaths { get; init; } = [];
    public int ReplacedExistingComponentCount { get; init; }
}
