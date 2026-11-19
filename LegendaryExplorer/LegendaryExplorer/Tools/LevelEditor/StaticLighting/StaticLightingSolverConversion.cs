using LegendaryExplorer.Lightmaps.Solver;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// What the light solver backends need of the scene beyond the classic baker's occluders: every shadow caster's and emissive section's triangles
/// with their vertex normals, section materials (for albedo) and emission. Gathered by <see cref="StaticLightingBaker.BuildScene"/>
/// </summary>
public sealed class StaticLightingSolverGeometry
{
    public List<StaticLightingSolverSurface> Surfaces { get; } = [];

    /// <summary>Adds a component that casts static shadows or has emissive sections; anything else isn't part of the solver's scene</summary>
    public void AddSurface(StaticMeshComponentProxy component, ExportEntry meshExport, StaticMeshRenderData lod,
        IReadOnlyList<StaticLightingTriangle> triangles, bool castsShadow, Vector3?[] sectionRadiance,
        StaticLightingEmissiveSettings? emission, LevelEditorRenderContext renderContext)
    {
        Vector3?[] sectionEmission = emission is { } emissive && sectionRadiance is not null
            ? StaticLightingSolverConversion.BoostEmission(sectionRadiance, emissive.Boost)
            : null;
        bool emits = sectionEmission is not null && Array.Exists(sectionEmission, value => value.HasValue);
        if (!castsShadow && !emits || triangles.Count == 0)
            return;
        StaticMeshElement[] elements = lod.Elements ?? [];
        var materials = new ExportEntry[elements.Length];
        for (int section = 0; section < elements.Length; section++)
            materials[section] = StaticLightingBaker.ResolveSectionMaterialForSolver(component, meshExport,
                elements[section], section, renderContext);
        Surfaces.Add(new StaticLightingSolverSurface
        {
            Component = component.Export,
            Triangles = triangles,
            CastsShadow = castsShadow,
            SectionMaterials = materials,
            SectionEmission = emits ? sectionEmission : null,
            TwoSidedEmission = emission?.TwoSided ?? false
        });
    }
}

public sealed class StaticLightingSolverSurface
{
    public required ExportEntry Component { get; init; }
    /// <summary>World space, as the baker builds them (<see cref="StaticLightingTriangle.SectionIndex"/> picks the section)</summary>
    public required IReadOnlyList<StaticLightingTriangle> Triangles { get; init; }
    /// <summary>Whether it blocks light; an emissive-only surface is still seen by gather rays, but never shadows direct light</summary>
    public bool CastsShadow { get; init; }
    /// <summary>The material of each section (null where unresolved)</summary>
    public ExportEntry[] SectionMaterials { get; init; } = [];
    /// <summary>Linear emitted radiance per section, boosted by EmissiveBoost; null: nothing emits</summary>
    public Vector3?[] SectionEmission { get; init; }
    public bool TwoSidedEmission { get; init; }
}

/// <summary>
/// Maps Scottina's Lightmass Baker's data to the light solver's (<see cref="SolverJob"/>, <see cref="SolverProfile.Scottina"/>), and the solver's
/// output back to the coefficients the baker writes
/// </summary>
public static class StaticLightingSolverConversion
{
    /// <summary>The albedo of surfaces whose material can't be rendered</summary>
    public const float FallbackAlbedo = 0.5f;
    /// <summary>The material capture's size: only the average diffuse is used, since the baker's triangles carry no material UVs</summary>
    public const int AlbedoCaptureSize = 16;

    /// <summary>
    /// The baker's directional basis (LightmassNative.cpp's and StaticLightingBaker's DirectionalBasis), in its tangent frame (T, B, N).
    /// </summary>
    public static readonly Vector3[] ScottinaDirectionalBasis =
    [
        Vector3.Normalize(new Vector3(MathF.Sqrt(2f / 3f), 0f, 1f / MathF.Sqrt(3f))),
        Vector3.Normalize(new Vector3(-1f / MathF.Sqrt(6f), 1f / MathF.Sqrt(2f), 1f / MathF.Sqrt(3f))),
        Vector3.Normalize(new Vector3(-1f / MathF.Sqrt(6f), -1f / MathF.Sqrt(2f), 1f / MathF.Sqrt(3f)))
    ];

    /// <summary>
    /// The tangent frame to give the solver so that its directional coefficients are the baker's. The solver weights by Codec.Directional.Basis,
    /// which is the baker's basis turned 90° about the normal (Codec B_i = Rz(90°) · Scottina B_i, same order). A direction with tangent-space
    /// components (x, y, z) in the baker's frame (T, B, N) has components (-y, x, z) in the frame (-B, T, N), and
    /// (-y, x, z) · Rz(90°)B_i = (x, y, z) · B_i, so with that frame the solver's Directional0–2 are exactly the baker's three coefficients
    /// </summary>
    public static (Vector3 Tangent, Vector3 Bitangent) ToSolverTangentFrame(Vector3 tangent, Vector3 bitangent) =>
        (-bitangent, tangent);

    public static SolverSettings CreateSettings(StaticLightingGenerationSettings settings) => new()
    {
        Device = settings.Backend == StaticLightingBakeBackend.Gpu ? SolverDevice.Gpu : SolverDevice.Cpu,
        Profile = SolverProfile.Scottina,
        ShadowRays = settings.ShadowSampleCount,
        // The baker has no penumbra refinement: every light gets its fixed sample count
        PenumbraShadowRays = 0,
        GatherRays = settings.SolverGatherRays,
        BounceGatherRays = Math.Max(StaticLightingGenerationSettings.MinimumSolverGatherRays, settings.SolverGatherRays / 4),
        Bounces = settings.SolverBounces,
        // E_i += unshadowed · max(0, d·B_i): linear, unscaled (StaticLightingBaker.EvaluateLighting, LightmassNative evaluate_sample)
        DirectionalCoefficientScale = 1f,
        DirectionalCoefficientFalloffPower = 1f,
        // The baker offsets its rays by at least its shadow bias
        RayOffset = settings.ShadowBias,
        NormalOffset = settings.ShadowBias,
        FilterIndirect = true
    };

    /// <summary>
    /// A baker light as the solver's, with the same models LightmassNative uses: radiance = colour × intensity; point and spot attenuation
    /// (1 - (d/R)²)², no inverse square; a linear spot cone from the outer to the inner cosine; soft shadows from a source disk of the
    /// effective source radius (or the directional source angle) only when the light gets more than one shadow sample. A sky light is the
    /// same constant above and below (the baker added it to every receiver regardless of orientation), now occluded by the scene
    /// </summary>
    public static SolverLight ConvertLight(StaticLightingLight light, StaticLightingGenerationSettings settings)
    {
        Vector3 radiance = Vector3.Max(Vector3.Zero, light.Color * light.Intensity);
        bool softShadows = StaticLightingBaker.GetLightSampleCount(light, settings) > 1;
        switch (light.Type)
        {
            case StaticLightingLightType.Sky:
                return new SolverLight
                {
                    Type = SolverLightType.Sky,
                    Color = radiance * GatheredRadianceScale,
                    LowerColor = radiance * GatheredRadianceScale,
                    CastShadows = true
                };
            case StaticLightingLightType.Directional:
                return new SolverLight
                {
                    Type = SolverLightType.Directional,
                    // The light travels opposite the baker's surface-to-light vector (-Direction, +Z if degenerate)
                    Direction = -SafeNormal(-light.Direction, Vector3.UnitZ),
                    Color = radiance,
                    SourceSize = softShadows ? settings.DirectionalSourceAngleDegrees * MathF.PI / 180f : 0f,
                    CastShadows = light.CastsShadow
                };
            default:
            {
                float outerCos = MathF.Cos(light.OuterConeAngleDegrees * MathF.PI / 180f);
                float innerCos = MathF.Cos(light.InnerConeAngleDegrees * MathF.PI / 180f);
                bool spot = light.Type == StaticLightingLightType.Spot;
                return new SolverLight
                {
                    Type = spot ? SolverLightType.Spot : SolverLightType.Point,
                    Position = light.Position,
                    Direction = SafeNormal(light.Direction, Vector3.UnitX),
                    Color = radiance,
                    Radius = light.Radius,
                    FalloffExponent = 2f,
                    // The baker divides by max(0.0001, inner - outer)
                    CosInnerCone = spot ? MathF.Max(innerCos, outerCos + 0.0001f) : 1f,
                    CosOuterCone = spot ? outerCos : 0f,
                    SourceSize = softShadows ? StaticLightingBaker.GetEffectiveSourceRadius(light, settings) : 0f,
                    CastShadows = light.CastsShadow
                };
            }
        }
    }

    /// <summary>
    /// The solver gathers radiance over the hemisphere: an unoccluded receiver under a uniform radiance L gets πL. The baker added its environment and
    /// sky light straight into the lightmap, and its emitters gave 1/π of a physical area light's irradiance, so these are given to the solver as
    /// radiance × 1/π: an unoccluded receiver keeps today's level, and only what's blocked gets darker
    /// </summary>
    public const float GatheredRadianceScale = 1f / MathF.PI;

    /// <summary>The baker's environment floor as the solver's environment (<see cref="GatheredRadianceScale"/>)</summary>
    public static Vector3 ConvertEnvironment(float ambientIntensity) => new(MathF.Max(0f, ambientIntensity) * GatheredRadianceScale);

    /// <summary>Half a texel's diagonal, from the baker's texel size</summary>
    public static float TexelRadius(float worldUnitsPerTexel) => MathF.Max(0f, worldUnitsPerTexel) * (MathF.Sqrt(2f) * 0.5f);

    public static SolverSample CreateSample(StaticLightingBaker.StaticLightingSurfaceSample sample, uint instance, uint receiver)
    {
        Vector3 normal = SafeNormal(sample.Normal, Vector3.UnitZ);
        Vector3 geometric = SafeNormal(sample.GeometricNormal, normal);
        if (Vector3.Dot(geometric, normal) < 0f)
            geometric = -geometric;
        (Vector3 tangent, Vector3 bitangent) = ToSolverTangentFrame(sample.Tangent, sample.Bitangent);
        return new SolverSample
        {
            Position = sample.Position,
            Normal = normal,
            GeometricNormal = geometric,
            Tangent = tangent,
            Bitangent = bitangent,
            TexelRadius = TexelRadius(sample.WorldUnitsPerTexel),
            Instance = instance,
            Receiver = receiver
        };
    }

    /// <summary>
    /// The coefficients the baker writes for a sample, from its simple (cosine-weighted) lighting and its three directional coefficients —
    /// LightmassNative's evaluate_sample packing, so <see cref="StaticLightingWriter"/> doesn't change. Compressed (LE3/ME3): [0] the simple
    /// colour normalized to its maximum, [1] the directional maxima rescaled so their mean is that maximum, [2] the simple lighting; plain:
    /// [0..2] the directional coefficients, [^1] the simple lighting. <paramref name="isotropicMaximum"/> is the flat term the classic
    /// evaluators add to each directional maximum (their unoccluded environment); the solver's output already has its environment in its
    /// directional coefficients, so it passes 0
    /// </summary>
    public static void PackCoefficients(Vector3 simple, Vector3 directional0, Vector3 directional1, Vector3 directional2,
        float isotropicMaximum, bool compressedDirectional, Vector3[][] coefficients, int index)
    {
        if (compressedDirectional)
        {
            float maximumColor = MaxComponent(simple);
            if (maximumColor > 0.000001f)
            {
                coefficients[0][index] = simple / maximumColor;
                Vector3 directionalMaximums = new(
                    MaxComponent(directional0) + isotropicMaximum,
                    MaxComponent(directional1) + isotropicMaximum,
                    MaxComponent(directional2) + isotropicMaximum);
                float flatNormalResponse = (directionalMaximums.X + directionalMaximums.Y + directionalMaximums.Z) / 3f;
                coefficients[1][index] = flatNormalResponse > 0.000001f
                    ? directionalMaximums * (maximumColor / flatNormalResponse)
                    : new Vector3(maximumColor);
            }
            else
            {
                coefficients[0][index] = Vector3.Zero;
                coefficients[1][index] = Vector3.Zero;
            }
            coefficients[2][index] = Vector3.Max(Vector3.Zero, simple);
            return;
        }

        coefficients[0][index] = Vector3.Max(Vector3.Zero, directional0);
        coefficients[1][index] = Vector3.Max(Vector3.Zero, directional1);
        coefficients[2][index] = Vector3.Max(Vector3.Zero, directional2);
        coefficients[^1][index] = Vector3.Max(Vector3.Zero, simple);
    }

    /// <summary>Emissive radiance × EmissiveBoost, clamped as <see cref="StaticLightingEmissive.CreateAreaEmitterSamples"/> clamps it</summary>
    public static Vector3?[] BoostEmission(Vector3?[] sectionRadiance, float boost)
    {
        float clampedBoost = float.IsFinite(boost) ? Math.Clamp(boost, 0f, 128f) : 0f;
        var boosted = new Vector3?[sectionRadiance.Length];
        for (int section = 0; section < sectionRadiance.Length; section++)
        {
            if (sectionRadiance[section] is not { } radiance) continue;
            Vector3 value = Vector3.Clamp(radiance * clampedBoost, Vector3.Zero, new Vector3(128f));
            if (MaxComponent(value) > 0f)
                boosted[section] = value;
        }
        return boosted;
    }

    /// <summary>A material's albedo from its captured diffuse: the average over the texels it covers, clamped to 0..1</summary>
    public static Vector3 AverageAlbedo(Lightmaps.Lightmass.CapturedMaterial captured)
    {
        Vector3 sum = Vector3.Zero;
        int count = 0;
        for (int texel = 0; texel < captured.Lit.Length; texel++)
        {
            if (captured.Covered is { } covered && !covered[texel]) continue;
            Vector3 diffuse = captured.Diffuse(texel);
            if (!float.IsFinite(diffuse.X) || !float.IsFinite(diffuse.Y) || !float.IsFinite(diffuse.Z)) continue;
            sum += diffuse;
            count++;
        }
        return count == 0
            ? new Vector3(FallbackAlbedo)
            : Vector3.Clamp(sum / count, Vector3.Zero, Vector3.One);
    }

    internal static float MaxComponent(Vector3 value) => MathF.Max(value.X, MathF.Max(value.Y, value.Z));

    internal static Vector3 SafeNormal(Vector3 value, Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 0.000001f && float.IsFinite(lengthSquared)
            ? value / MathF.Sqrt(lengthSquared)
            : fallback;
    }
}
