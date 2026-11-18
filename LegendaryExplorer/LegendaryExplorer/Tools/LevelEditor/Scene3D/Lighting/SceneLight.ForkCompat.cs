using System;
using System.Numerics;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

/// <summary>
/// Fork light API on upstream's <see cref="SceneLight"/>
/// </summary>
public sealed partial class SceneLight
{
    /// <summary>
    /// Fork: the light's brightness, kept apart from <see cref="Color"/>. Upstream folds brightness into <see cref="Color"/> (as the fork did for
    /// lights read from packages), so this is always 1 and <see cref="Color"/> is the light's full intensity
    /// </summary>
    public float Intensity => 1f;

    public bool IsSpot => Type == SceneLightType.Spot;

    /// <summary>The spot cone's inner angle, in degrees, recovered from the cone terms upstream keeps</summary>
    public float InnerConeAngleDegrees
    {
        get
        {
            if (!float.IsFinite(InvCosConeDifference) || InvCosConeDifference == 0) return OuterConeAngleDegrees;
            float cosInner = Math.Clamp(CosOuterCone + 1f / InvCosConeDifference, -1f, 1f);
            return MathF.Acos(cosInner) * 180f / MathF.PI;
        }
    }

    /// <summary>The spot cone's outer angle, in degrees</summary>
    public float OuterConeAngleDegrees => MathF.Acos(Math.Clamp(CosOuterCone, -1f, 1f)) * 180f / MathF.PI;

    public float InnerConeCos => MathF.Cos(MathF.PI / 180f * InnerConeAngleDegrees);
    public float OuterConeCos => CosOuterCone;

    /// <summary>
    /// Fork's packed channel mask: the <see cref="LightingChannels"/> shifted up one bit, with bit 0 marking the mask as initialized
    /// </summary>
    public uint LightingChannelMask => (LightingChannels.Channels << 1) | 1u;

    /// <summary>
    /// Whether two fork channel masks (see <see cref="LightingChannelMask"/>) share a channel. An uninitialized mask (bit 0 clear) overlaps everything
    /// </summary>
    public static bool ChannelsOverlap(uint a, uint b) =>
        (a & 1u) == 0 || (b & 1u) == 0 || ((a & b) & ~1u) != 0;

    /// <summary>
    /// Fork: a free-standing preview light, not read from a package. It has no export, so it isn't one of the level lights upstream's lighting
    /// passes render (<see cref="MeshRenderContext.AddLights"/>); preview contexts keep such lights in <see cref="MeshRenderContext.SceneLights"/>,
    /// and game-shader previews are lit by <see cref="MeshRenderContext.Lighting"/> instead. <paramref name="intensity"/> is folded into <see cref="Color"/>.
    /// </summary>
    /// <param name="lightingChannelMask">A fork channel mask (see <see cref="LightingChannelMask"/>); 0 for the default light channels</param>
    public SceneLight(Vector3 position, float radius, Vector3 color, float intensity, bool isSpot,
        Vector3 direction, float innerConeAngleDegrees, float outerConeAngleDegrees, uint lightingChannelMask = 0)
    {
        Type = isSpot ? SceneLightType.Spot : SceneLightType.Point;
        Position = position;
        Radius = Math.Max(radius, 1f);
        Color = color * intensity;
        Direction = direction.LengthSquared() > 0 ? Vector3.Normalize(direction) : Vector3.UnitX;
        float inner = Math.Clamp(innerConeAngleDegrees, 0, 89) * MathF.PI / 180;
        float outer = Math.Clamp(outerConeAngleDegrees * MathF.PI / 180, inner + 0.001f, 89 * MathF.PI / 180 + 0.001f);
        CosOuterCone = MathF.Cos(outer);
        SinOuterCone = MathF.Sin(outer);
        InvCosConeDifference = 1 / (MathF.Cos(inner) - CosOuterCone);
        FalloffExponent = 2f;
        LightingChannels = (lightingChannelMask & 1u) == 0 ? LightingChannels.LightDefault : new LightingChannels(lightingChannelMask >> 1);
        WorldToLight = Matrix4x4.CreateTranslation(-position);
        TraceDistance = 100000;
        LevelName = "";
        ShadowRadiusMultiplier = 1.1f;
    }
}
