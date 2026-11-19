using System;
using System.Collections.Generic;
using System.Numerics;

namespace LegendaryExplorer.Lightmaps.Lightmass;

/// <summary>Which triangle each texel of a mapping samples, and where on it (barycentric coordinates), or -1 for a texel off the surface.</summary>
public sealed class TexelRasterization
{
    public int Width, Height;
    public int[] Triangle;
    public Vector3[] Barycentric;

    public bool IsCovered(int texel) => Triangle[texel] >= 0;
}

/// <summary>
/// Rasterises a mapping's triangles in lightmap texel space the way Lightmass does with bUseConservativeTexelRasterization and bUseMaxWeight: a texel
/// that any triangle overlaps (more than touching) is mapped, and among the triangles overlapping it the one whose sample point is nearest the texel's
/// centre wins (a centre inside the triangle is nearest). A triangle's sample point is the texel's centre when the centre is inside it; otherwise the
/// nearest of a supersampling grid's points inside it; otherwise (a sliver passing between them) the point of the triangle nearest the centre. So
/// every sample is on the surface, and its attributes are interpolated there.
/// </summary>
public static class LightmassTexelRasterizer
{
    /// <summary>Overlap, in texels, below which a triangle only touches a texel</summary>
    const float TouchEpsilon = 1e-4f;

    /// <summary>
    /// Texel coordinates of lightmap UVs: with padding (bPadMappings) UV 0..1 covers the interior, one texel in from each side; without it, the whole
    /// mapping. <paramref name="upsample"/> scales the result for a finer grid over the same mapping.
    /// </summary>
    public static Vector2 UvToTexel(Vector2 uv, int sizeX, int sizeY, bool padded, int upsample = 1) => padded && sizeX > 2 && sizeY > 2
        ? new Vector2(uv.X * (sizeX - 2) + 1, uv.Y * (sizeY - 2) + 1) * upsample
        : new Vector2(uv.X * sizeX, uv.Y * sizeY) * upsample;

    /// <param name="texels">Each vertex's position in texel units (texel (x, y) spans [x, x+1] × [y, y+1])</param>
    /// <param name="indices">Three per triangle</param>
    /// <param name="supersample">Points per texel side tested when the centre isn't inside a triangle</param>
    public static TexelRasterization Rasterize(int width, int height, IReadOnlyList<Vector2> texels, IReadOnlyList<int> indices, int supersample = 4)
    {
        var result = new TexelRasterization
        {
            Width = width, Height = height,
            Triangle = new int[width * height],
            Barycentric = new Vector3[width * height],
        };
        Array.Fill(result.Triangle, -1);
        var bestDistance = new float[width * height];
        var bestInside = new int[width * height];
        Array.Fill(bestDistance, float.MaxValue);

        for (int t = 0; t + 2 < indices.Count; t += 3)
        {
            Vector2 a = texels[indices[t]], b = texels[indices[t + 1]], c = texels[indices[t + 2]];
            float area = Cross(b - a, c - a);
            if (!float.IsFinite(area) || MathF.Abs(area) < 1e-12f) continue;
            var min = Vector2.Min(a, Vector2.Min(b, c));
            var max = Vector2.Max(a, Vector2.Max(b, c));
            int x0 = Math.Max(0, (int)MathF.Floor(min.X)), y0 = Math.Max(0, (int)MathF.Floor(min.Y));
            int x1 = Math.Min(width - 1, (int)MathF.Ceiling(max.X)), y1 = Math.Min(height - 1, (int)MathF.Ceiling(max.Y));
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                if (!Overlaps(a, b, c, x, y)) continue;
                var centre = new Vector2(x + 0.5f, y + 0.5f);
                var (bary, distance, inside) = SamplePoint(a, b, c, area, centre, supersample);
                int i = y * width + x;
                if (distance < bestDistance[i] - 1e-6f || MathF.Abs(distance - bestDistance[i]) <= 1e-6f && inside > bestInside[i])
                {
                    bestDistance[i] = distance;
                    bestInside[i] = inside;
                    result.Triangle[i] = t / 3;
                    result.Barycentric[i] = bary;
                }
            }
        }
        return result;
    }

    static float Cross(Vector2 u, Vector2 v) => u.X * v.Y - u.Y * v.X;

    static Vector3 Barycentric(Vector2 a, Vector2 b, Vector2 c, float area, Vector2 p)
    {
        float wa = Cross(b - p, c - p) / area;
        float wb = Cross(c - p, a - p) / area;
        return new Vector3(wa, wb, 1 - wa - wb);
    }

    static bool Inside(Vector3 bary) => bary.X >= -1e-6f && bary.Y >= -1e-6f && bary.Z >= -1e-6f;

    /// <summary>The triangle's sample point for the texel: barycentrics, distance from the centre, and how many supersamples are inside (coverage)</summary>
    static (Vector3 Bary, float Distance, int Inside) SamplePoint(Vector2 a, Vector2 b, Vector2 c, float area, Vector2 centre, int supersample)
    {
        int inside = 0;
        float best = float.MaxValue;
        Vector3 bestBary = default;
        var centreBary = Barycentric(a, b, c, area, centre);
        for (int sy = 0; sy < supersample; sy++)
        for (int sx = 0; sx < supersample; sx++)
        {
            var p = centre - new Vector2(0.5f) + new Vector2((sx + 0.5f) / supersample, (sy + 0.5f) / supersample);
            var bary = Barycentric(a, b, c, area, p);
            if (!Inside(bary)) continue;
            inside++;
            float d = Vector2.DistanceSquared(p, centre);
            if (d < best) { best = d; bestBary = bary; }
        }
        if (Inside(centreBary)) return (Clamp(centreBary), 0, Math.Max(inside, 1));
        if (inside > 0) return (bestBary, MathF.Sqrt(best), inside);
        var closest = ClosestPoint(a, b, c, centre);
        var closestBary = Clamp(Barycentric(a, b, c, area, closest));
        return (closestBary, Vector2.Distance(closest, centre), 0);
    }

    static Vector3 Clamp(Vector3 bary)
    {
        bary = Vector3.Max(bary, Vector3.Zero);
        float sum = bary.X + bary.Y + bary.Z;
        return sum > 0 ? bary / sum : new Vector3(1f / 3);
    }

    static Vector2 ClosestPoint(Vector2 a, Vector2 b, Vector2 c, Vector2 p)
    {
        Vector2 best = a; float bestD = float.MaxValue;
        foreach (var (u, v) in (ReadOnlySpan<(Vector2, Vector2)>)[(a, b), (b, c), (c, a)])
        {
            var e = v - u;
            float t = e.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(p - u, e) / e.LengthSquared(), 0, 1) : 0;
            var q = u + e * t;
            float d = Vector2.DistanceSquared(q, p);
            if (d < bestD) { bestD = d; best = q; }
        }
        return best;
    }

    /// <summary>Whether the triangle and the texel's square overlap with positive area (separating axis test; touching along an edge doesn't count)</summary>
    public static bool Overlaps(Vector2 a, Vector2 b, Vector2 c, int x, int y)
    {
        float minX = x, minY = y, maxX = x + 1, maxY = y + 1;
        if (MathF.Max(a.X, MathF.Max(b.X, c.X)) <= minX + TouchEpsilon || MathF.Min(a.X, MathF.Min(b.X, c.X)) >= maxX - TouchEpsilon) return false;
        if (MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) <= minY + TouchEpsilon || MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) >= maxY - TouchEpsilon) return false;
        // The triangle's edge normals
        foreach (var (u, v, w) in (ReadOnlySpan<(Vector2, Vector2, Vector2)>)[(a, b, c), (b, c, a), (c, a, b)])
        {
            var n = new Vector2(-(v.Y - u.Y), v.X - u.X);
            float length = n.Length();
            if (length == 0) continue;
            n /= length;
            // The triangle's extent on this axis is [edge, opposite vertex]
            float edge = Vector2.Dot(n, u), opposite = Vector2.Dot(n, w);
            float triMin = MathF.Min(edge, opposite), triMax = MathF.Max(edge, opposite);
            float c0 = Vector2.Dot(n, new Vector2(minX, minY)), c1 = Vector2.Dot(n, new Vector2(maxX, minY));
            float c2 = Vector2.Dot(n, new Vector2(minX, maxY)), c3 = Vector2.Dot(n, new Vector2(maxX, maxY));
            float boxMin = MathF.Min(MathF.Min(c0, c1), MathF.Min(c2, c3)), boxMax = MathF.Max(MathF.Max(c0, c1), MathF.Max(c2, c3));
            if (boxMax <= triMin + TouchEpsilon || boxMin >= triMax - TouchEpsilon) return false;
        }
        return true;
    }
}
