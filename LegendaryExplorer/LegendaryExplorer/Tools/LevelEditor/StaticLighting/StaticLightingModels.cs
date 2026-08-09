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

public enum StaticLightingMappingMode
{
    /// <summary>Choose between texture and vertex mapping from receiver size, density and authored metadata.</summary>
    Automatic,
    /// <summary>Use a texture lightmap whenever the selected UV mapping passes validation.</summary>
    Texture2D,
    /// <summary>Use interpolated per-vertex lighting.</summary>
    Vertex1D
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
    bool CastsStaticShadow = true,
    float SourceRadius = 0f)
{
    public bool CastsShadow => Type != StaticLightingLightType.Sky && CastsStaticShadow;
}

public sealed class StaticLightingGenerationSettings
{
    public int TextureResolution { get; set; } = 64;
    public StaticLightingMappingMode MappingMode { get; set; } = StaticLightingMappingMode.Automatic;
    /// <summary>Unoccluded environment/indirect floor. It is never multiplied by direct-light visibility.</summary>
    public float AmbientIntensity { get; set; } = 0.12f;
    public float ShadowBias { get; set; } = 1f;
    public int ShadowSampleCount { get; set; } = 8;
    public float DefaultLightSourceRadius { get; set; } = 16f;
    public float DirectionalSourceAngleDegrees { get; set; } = 0.5f;
    public string TextureCacheName { get; set; } = "";
    public int WorkerThreads { get; set; }
    public int WorkTileSize { get; set; } = 16;

    public int EffectiveWorkerThreads => WorkerThreads > 0
        ? WorkerThreads
        : Math.Max(1, Environment.ProcessorCount);

    public void Validate()
    {
        if (!Enum.IsDefined(MappingMode))
            throw new ArgumentOutOfRangeException(nameof(MappingMode));
        if (TextureResolution is < 64 or > 1024 || !BitOperations.IsPow2((uint)TextureResolution))
            throw new ArgumentOutOfRangeException(nameof(TextureResolution),
                "Lightmap resolution must be a power of two from 64 through 1024.");
        if (!float.IsFinite(AmbientIntensity) || AmbientIntensity < 0f)
            throw new ArgumentOutOfRangeException(nameof(AmbientIntensity));
        if (!float.IsFinite(ShadowBias) || ShadowBias is < 0.01f or > 100f)
            throw new ArgumentOutOfRangeException(nameof(ShadowBias));
        if (ShadowSampleCount is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(ShadowSampleCount),
                "Shadow samples must be from 1 through 64.");
        if (!float.IsFinite(DefaultLightSourceRadius) || DefaultLightSourceRadius is < 0f or > 10000f)
            throw new ArgumentOutOfRangeException(nameof(DefaultLightSourceRadius));
        if (!float.IsFinite(DirectionalSourceAngleDegrees) || DirectionalSourceAngleDegrees is < 0f or > 10f)
            throw new ArgumentOutOfRangeException(nameof(DirectionalSourceAngleDegrees));
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
    StaticLightingVertex C)
{
    public int SectionIndex { get; init; } = -1;
    public int SourceTriangleIndex { get; init; } = -1;
}

public sealed class StaticLightingMappingDiagnostics
{
    public string MeshPath { get; init; } = "";
    public int DeclaredVertexCount { get; init; }
    public int PositionVertexCount { get; init; }
    public int AttributeVertexCount { get; init; }
    public int TextureCoordinateCount { get; init; }
    public int SelectedCoordinateIndex { get; init; }
    public int SectionCount { get; init; }
    public int SourceIndexCount { get; init; }
    public int TriangleCount { get; init; }
    public int InvalidSectionRangeCount { get; init; }
    public int InvalidIndexCount { get; init; }
    public int InvalidUvVertexCount { get; init; }
    public int DegenerateUvTriangleCount { get; init; }
    public int OverlappingUvTrianglePairCount { get; init; }

    public bool HasVertexLayoutMismatch =>
        DeclaredVertexCount != PositionVertexCount || PositionVertexCount != AttributeVertexCount;

    /// <summary>
    /// Degenerate UV triangles are repairable as constant/linear texel mappings and therefore do not
    /// invalidate an otherwise usable lightmap channel.
    /// </summary>
    public bool HasRepairableTextureMappingIssues => DegenerateUvTriangleCount > 0;

    public bool HasTextureMappingErrors => HasVertexLayoutMismatch || SelectedCoordinateIndex < 0 ||
        SelectedCoordinateIndex >= TextureCoordinateCount || InvalidSectionRangeCount > 0 ||
        InvalidIndexCount > 0 || InvalidUvVertexCount > 0 || OverlappingUvTrianglePairCount > 0;
}

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
    /// <summary>
    /// True when the selected generation policy chose a texture mapping for this component. A valid
    /// UV channel by itself is not sufficient in automatic mode: many stock meshes deliberately use
    /// vertex lightmaps even though they contain a second UV channel.
    /// </summary>
    public bool UseTextureMapping { get; init; }
    public StaticLightingMappingDiagnostics MappingDiagnostics { get; init; } = new();
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
    public required Guid[] IrrelevantLightGuids { get; init; }
    public StaticLightingTextureBake Texture { get; init; }
    public StaticLightingVertexBake Vertex { get; init; }
    public StaticLightingComponentDiagnostics Diagnostics { get; init; } = new();
}

public sealed class StaticLightingComponentDiagnostics
{
    public StaticLightingMappingDiagnostics Mapping { get; init; } = new();
    public int MappedTexelCount { get; init; }
    public int MappingConflictTexelCount { get; init; }
    public long RaysCast { get; init; }
    public long OccludedSamples { get; init; }
    public long RejectedSelfIntersections { get; init; }
    public long VisibilitySampleCount { get; init; }
    public double AverageVisibility { get; init; }
    public double AverageDirectContribution { get; init; }
    public double AverageEnvironmentContribution { get; init; }
    public double BakeMilliseconds { get; init; }
}

public sealed class StaticLightingBakeResult
{
    public required IReadOnlyList<StaticLightingComponentBake> Components { get; init; }
    public string ValidationError { get; init; }
    public required int SourceTriangleCount { get; init; }
    public required int LightCount { get; init; }
    public required int TextureMappedComponentCount { get; init; }
    public required int VertexMappedComponentCount { get; init; }
    public int WorkUnitCount { get; init; }
    public int WorkerCount { get; init; }
    public long RaysCast { get; init; }
    public long OccludedSamples { get; init; }
    public long RejectedSelfIntersections { get; init; }
    public long VisibilitySampleCount { get; init; }
    public double AverageVisibility { get; init; }
    public double AverageDirectContribution { get; init; }
    public double AverageEnvironmentContribution { get; init; }
    public double BakeMilliseconds { get; init; }
}

public sealed class StaticLightingWriteResult
{
    public int ComponentCount { get; init; }
    public int LightMapTextureCount { get; init; }
    public int ShadowMapCount { get; init; }
    public int IrrelevantLightReferenceCount { get; init; }
    public IReadOnlyList<string> TextureCachePaths { get; init; } = [];
    public int ReplacedExistingComponentCount { get; init; }
}
