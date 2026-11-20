using System;
using System.Numerics;

namespace LegendaryExplorer.Lightmaps.Codec;

/// <summary>
/// An RGB value in half precision (6 bytes), for lighting kept per lightmap texel (<see cref="Lightmass.LightmassMappingResult"/>). Finite values
/// beyond half's range are clamped to it; NaN and infinities stay, so a broken result is still recognisable
/// </summary>
public readonly struct HalfVector3 : IEquatable<HalfVector3>
{
    readonly Half x, y, z;

    public HalfVector3(Vector3 v) : this(v.X, v.Y, v.Z) { }
    public HalfVector3(float x, float y, float z) { this.x = ToHalf(x); this.y = ToHalf(y); this.z = ToHalf(z); }

    public float X => (float)x;
    public float Y => (float)y;
    public float Z => (float)z;

    static Half ToHalf(float v)
    {
        if (!float.IsFinite(v)) return (Half)v;
        // A value too small for half stays its smallest step rather than becoming 0: a faint texel's maxima times its colour still give back that
        // colour (Directional.Split makes a texel with no light black, and a black texel would change the DXT1 colours of its whole block)
        if (v > 0 && v < (float)Half.Epsilon) return Half.Epsilon;
        if (v < 0 && v > -(float)Half.Epsilon) return -Half.Epsilon;
        return (Half)Math.Clamp(v, -65504f, 65504f);
    }

    public static implicit operator Vector3(HalfVector3 v) => new((float)v.x, (float)v.y, (float)v.z);
    public static implicit operator HalfVector3(Vector3 v) => new(v);

    public bool Equals(HalfVector3 other) => x.Equals(other.x) && y.Equals(other.y) && z.Equals(other.z);
    public override bool Equals(object obj) => obj is HalfVector3 other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(x, y, z);
    public override string ToString() => ((Vector3)this).ToString();
}
