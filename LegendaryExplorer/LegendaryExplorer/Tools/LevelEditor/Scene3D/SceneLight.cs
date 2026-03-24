using System;
using System.Numerics;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

public readonly struct SceneLight
{
    public SceneLight(Vector3 position, float radius, Vector3 color, float intensity, bool isSpot, Vector3 direction, float innerConeAngleDegrees, float outerConeAngleDegrees, uint lightingChannelMask = 0)
    {
        Position = position;
        Radius = Math.Max(radius, 1f);
        Color = color;
        Intensity = intensity;
        IsSpot = isSpot;
        Direction = direction;
        InnerConeAngleDegrees = innerConeAngleDegrees;
        OuterConeAngleDegrees = outerConeAngleDegrees;
        LightingChannelMask = lightingChannelMask;
    }

    public Vector3 Position { get; }
    public float Radius { get; }
    public Vector3 Color { get; }
    public float Intensity { get; }
    public bool IsSpot { get; }
    public Vector3 Direction { get; }
    public float InnerConeAngleDegrees { get; }
    public float OuterConeAngleDegrees { get; }
    public uint LightingChannelMask { get; }

    public float InnerConeCos => MathF.Cos(MathF.PI / 180f * InnerConeAngleDegrees);
    public float OuterConeCos => MathF.Cos(MathF.PI / 180f * OuterConeAngleDegrees);

    public static bool ChannelsOverlap(uint a, uint b)
    {
        if ((a & 1u) == 0 || (b & 1u) == 0)
            return true;
        return ((a & b) & ~1u) != 0;
    }
}
