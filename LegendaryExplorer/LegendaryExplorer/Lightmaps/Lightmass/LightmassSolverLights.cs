using System;
using System.Numerics;
using LegendaryExplorer.Lightmaps.Solver;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;

namespace LegendaryExplorer.Lightmaps.Lightmass;

/// <summary>Where a relevant light's direct light goes in a mapping (UE3 Lightmass's per-light choice in ProcessTextureMapping/ProcessVertexMapping)</summary>
public enum LightmassLightRoute
{
    /// <summary>Nothing direct: the light only bounces (a GI-only or dominant light without static shadowing), or it isn't for this receiver</summary>
    None,
    /// <summary>Its direct light goes into the lightmap, and the lightmap lists it (LightGuids)</summary>
    Lightmap,
    /// <summary>A visibility shadow map</summary>
    ShadowMap,
    /// <summary>A signed distance field shadow map (texture mappings only)</summary>
    DistanceFieldShadowMap,
}

/// <summary>
/// The lights of a <see cref="LightmassScene"/> as LEX's solver gets them, and the choice Lightmass makes, for each light relevant to a mapping, between
/// lighting the mapping's lightmap and giving it a shadow map.
/// </summary>
public static class LightmassSolverLights
{
    /// <summary>
    /// FLinearColor(FColor) as UE3 and Lightmass convert a light's colour: each byte through the pow(c / 255, 2.2) table (Pow22OneOver255Table), not the
    /// exact sRGB curve. LightmassWorldSettings decodes the environment colour the same way.
    /// </summary>
    public static float ColorByteToLinear(byte c) => Pow22[c];

    static readonly float[] Pow22 = BuildPow22();

    static float[] BuildPow22()
    {
        var table = new float[256];
        bool linear = System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Contains("linearcolor", StringComparison.OrdinalIgnoreCase) == true;
        for (int i = 0; i < 256; i++) table[i] = linear ? i / 255f : MathF.Pow(i / 255f, 2.2f);
        return table;
    }

    public static Vector3 LinearColor((byte R, byte G, byte B) c) => new(ColorByteToLinear(c.R), ColorByteToLinear(c.G), ColorByteToLinear(c.B));

    /// <summary>A light as the solver gets it: linear colour × brightness, its shape and falloff, and whether it bounces.</summary>
    public static SolverLight Convert(LightmassLight light)
    {
        var l = light.Light;
        var type = l.Type switch
        {
            SceneLightType.Point => SolverLightType.Point,
            SceneLightType.Spot => SolverLightType.Spot,
            SceneLightType.Directional => SolverLightType.Directional,
            _ => SolverLightType.Sky,
        };
        var direction = l.Direction.LengthSquared() > 1e-12f ? Vector3.Normalize(l.Direction) : Vector3.UnitZ * -1;
        var result = new SolverLight
        {
            Type = type,
            Position = l.Position,
            Direction = direction,
            Color = LinearColor(light.Color) * light.Brightness,
            LowerColor = type == SolverLightType.Sky ? LinearColor(light.LowerColor) * light.LowerBrightness : Vector3.Zero,
            Radius = l.Radius,
            FalloffExponent = l.FalloffExponent,
            // Lightmass: point and spot lights are spheres of LightSourceRadius; directional lights subtend LightSourceAngle (already in radians)
            SourceSize = type switch
            {
                SolverLightType.Point or SolverLightType.Spot => MathF.Max(0, light.LightSourceRadius),
                SolverLightType.Directional => MathF.Max(0, light.LightSourceAngle),
                _ => 0,
            },
            // FLight::CastsStaticShadows... Lightmass traces shadow rays when the light has both GI_LIGHT_CASTSHADOWS and GI_LIGHT_CASTSTATICSHADOWS
            CastShadows = (light.Flags & LightFlags.CastShadows) != 0 && (light.Flags & LightFlags.CastStaticShadows) != 0,
            ShadowExponent = light.ShadowExponent > 0 ? light.ShadowExponent : 1,
            IndirectLightingScale = light.IndirectLightingScale,
            IndirectLightingSaturation = light.IndirectLightingSaturation,
            Bounces = Bounces(light),
        };
        if (type == SolverLightType.Spot)
        {
            var (cosInner, cosOuter) = SpotCone(light.InnerConeAngle, light.OuterConeAngle);
            result.CosInnerCone = cosInner;
            result.CosOuterCone = cosOuter;
        }
        return result;
    }

    /// <summary>
    /// USpotLightComponent's cone clamps (as Lightmass gets the angles in degrees and clamps them the same way): inner in [0°, 89°], outer in
    /// [inner + 0.001 rad, 89° + 0.001 rad].
    /// </summary>
    public static (float CosInner, float CosOuter) SpotCone(float innerDegrees, float outerDegrees)
    {
        float inner = Math.Clamp(innerDegrees, 0, 89) * MathF.PI / 180;
        float outer = Math.Clamp(outerDegrees * MathF.PI / 180, inner + 0.001f, 89 * MathF.PI / 180 + 0.001f);
        return (MathF.Cos(inner), MathF.Cos(outer));
    }

    /// <summary>
    /// Whether the light emits bounce light: lights with static lighting do (Lightmass emits photons for every light with GI_LIGHT_HASSTATICLIGHTING,
    /// whatever its relevance to primitives), shadow-only twins don't, and nor do sky lights, whose lighting UDK's Lightmass never bounces.
    /// </summary>
    public static bool Bounces(LightmassLight light) =>
        !light.ShadowOnly && light.Light.Type != SceneLightType.Sky && (light.Flags & LightFlags.HasStaticLighting) != 0 && light.IndirectLightingScale > 0;

    /// <summary>FLight::UseStaticLighting(bForceDirectLightMap = false, as every exported mapping has it): static lighting into the direct lightmap</summary>
    public static bool UseStaticLighting(uint flags) => (flags & LightFlags.HasStaticLighting) != 0 && (flags & LightFlags.UseDirectLightMap) != 0;

    /// <summary>
    /// What a mapping does with a light relevant to it, as Lightmass decides it from the light's flags: lights with static lighting into the direct
    /// lightmap go there (sky lights included); otherwise a light with static shadowing gets a shadow map, a distance field one when it asks for one
    /// (GI_LIGHT_USESIGNEDDISTANCEFIELDSHADOWS) and distance fields are allowed and the mapping is a texture mapping (vertex shadow maps are visibility).
    /// A GI-only light only bounces. A shadow-only receiver keeps only its shadow maps: its lightmap is dropped.
    /// </summary>
    public static LightmassLightRoute Route(uint flags, bool isSky, bool shadowOnly, bool indirectOnly, bool shadowOnlyReceiver, bool textureMapping,
        bool allowDistanceFields)
    {
        if (indirectOnly) return LightmassLightRoute.None;
        if (isSky)
            return !shadowOnly && !shadowOnlyReceiver && UseStaticLighting(flags) ? LightmassLightRoute.Lightmap : LightmassLightRoute.None;
        if (!shadowOnly && UseStaticLighting(flags))
            return shadowOnlyReceiver ? LightmassLightRoute.None : LightmassLightRoute.Lightmap;
        if ((flags & LightFlags.HasStaticShadowing) == 0) return LightmassLightRoute.None;
        return textureMapping && allowDistanceFields && (flags & LightFlags.UseSignedDistanceFieldShadows) != 0
            ? LightmassLightRoute.DistanceFieldShadowMap : LightmassLightRoute.ShadowMap;
    }

    public static LightmassLightRoute Route(LightmassLight light, bool shadowOnlyReceiver, bool textureMapping, bool allowDistanceFields) =>
        Route(light.Flags, light.Light.Type == SceneLightType.Sky, light.ShadowOnly, light.IndirectOnly, shadowOnlyReceiver, textureMapping, allowDistanceFields);

    /// <summary>
    /// Whether the light's direct light reaches a point with the given normal, ignoring occlusion: within a point or spot light's radius and a spot's
    /// outer cone, and in front of the surface (or either side of a two-sided one). Sky lights reach everything. Used for the lights a lightmap lists
    /// and for where a shadow map's light can't reach at all.
    /// </summary>
    public static bool Reaches(SolverLight light, Vector3 position, Vector3 normal, bool twoSided, bool facingMatters = true)
    {
        Vector3 toLight;
        switch (light.Type)
        {
            case SolverLightType.Sky:
                return true;
            case SolverLightType.Directional:
                toLight = -light.Direction;
                break;
            default:
            {
                var d = light.Position - position;
                float distanceSquared = d.LengthSquared();
                if (light.Radius > 0 && distanceSquared >= light.Radius * light.Radius) return false;
                // A light inside the sphere of its own source reaches every direction
                if (distanceSquared <= light.SourceSize * light.SourceSize) return true;
                toLight = d / MathF.Sqrt(distanceSquared);
                if (light.Type == SolverLightType.Spot && Vector3.Dot(-toLight, light.Direction) <= light.CosOuterCone)
                {
                    // An area source can still light inside its penumbra past the cone's edge: Lightmass tests the cone from the light's centre
                    return false;
                }
                break;
            }
        }
        if (!facingMatters || twoSided) return true;
        // A source of finite size lights a little past the terminator
        float slack = light.Type == SolverLightType.Directional ? MathF.Sin(MathF.Min(light.SourceSize, MathF.PI / 2))
            : light.Type is SolverLightType.Point or SolverLightType.Spot ? MathF.Min(1, light.SourceSize / MathF.Max(1e-3f, Vector3.Distance(light.Position, position))) : 0;
        return Vector3.Dot(toLight, normal) > -slack;
    }
}
