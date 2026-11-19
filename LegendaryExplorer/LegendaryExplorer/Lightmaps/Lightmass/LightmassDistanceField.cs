using System;
using System.Numerics;
using System.Threading.Tasks;

namespace LegendaryExplorer.Lightmaps.Lightmass;

/// <summary>
/// Lightmass's signed distance field shadow maps (CalculateDirectSignedDistanceFieldLightingTextureMappingTextureSpace): the light's visibility is found
/// at an upsampled resolution, each high resolution texel lit or shadowed, and each texel of the shadow map then stores the world-space distance from
/// its own high resolution texel to the nearest one of the other state in its neighbourhood, normalised by MaxTransitionDistanceWorldSpace and encoded
/// linearly with 0.5 at the edge: 0.5 + d/2 when lit, 0.5 − d/2 when shadowed.
/// <para>
/// Only texels within MaxTransitionDistanceWorldSpace of a transition at the shadow map's own resolution are refined (<see cref="RefinedTexels"/>): the
/// others are a full distance from any edge, 1 or 0. Their high resolution texels take their own state where a refined texel's search reaches them.
/// </para>
/// </summary>
public static class LightmassDistanceField
{
    /// <summary>The largest upsample factor (UE3 caps it, so a coarse mapping's high resolution pass stays affordable)</summary>
    public const int MaxUpsampleFactor = 13;

    /// <summary>The most high resolution texels one mapping's grid may have; past it the upsample factor is lowered</summary>
    public const long MaxHighResTexels = 64L << 20;

    /// <summary>How far, in high resolution texels, a texel's search for the nearest transition goes at most</summary>
    public const int MaxSearchRadius = 128;

    /// <summary>
    /// Lightmass's upsample factor: enough high resolution texels that MaxTransitionDistanceWorldSpace spans about
    /// ApproximateHighResTexelsPerMaxTransitionDistance of them, at least MinDistanceFieldUpsampleFactor, at most <see cref="MaxUpsampleFactor"/>, and
    /// small enough for <see cref="MaxHighResTexels"/>.
    /// </summary>
    public static int UpsampleFactor(float texelWorldSize, float maxTransitionDistance, float highResTexelsPerTransition, int minUpsample, int sizeX, int sizeY)
    {
        int factor = maxTransitionDistance > 0 && texelWorldSize > 0
            ? (int)MathF.Ceiling(highResTexelsPerTransition * texelWorldSize / maxTransitionDistance) : minUpsample;
        factor = Math.Clamp(factor, Math.Max(1, minUpsample), MaxUpsampleFactor);
        while (factor > 1 && (long)sizeX * sizeY * factor * factor > MaxHighResTexels) factor--;
        return Math.Max(1, factor);
    }

    /// <summary>A normalised signed distance (0..1 on the lit side, 0..1 on the shadowed side) as Lightmass quantizes it: trunc(value · 255)</summary>
    public static byte Encode(float normalizedDistance, bool lit)
    {
        float d = Math.Clamp(normalizedDistance, 0, 1);
        float value = lit ? 0.5f + 0.5f * d : 0.5f - 0.5f * d;
        return (byte)Math.Clamp((int)(value * 255f), 0, 255);
    }

    /// <summary>
    /// The texels whose distance field needs the high resolution pass: those within <paramref name="radius"/> texels of a transition at the shadow map's
    /// resolution, a texel partly lit (0 &lt; visibility &lt; 1) or a texel lit (visibility ≥ 0.5) next to a shadowed one or the other way round.
    /// </summary>
    public static bool[] RefinedTexels(int width, int height, bool[] covered, float[] visibility, int radius)
    {
        var transition = new bool[width * height];
        bool any = false;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * width + x;
            if (!covered[i]) continue;
            float v = visibility[i];
            bool lit = v >= 0.5f, edge = v > 0 && v < 1;
            for (int sy = Math.Max(0, y - 1); !edge && sy <= Math.Min(height - 1, y + 1); sy++)
            for (int sx = Math.Max(0, x - 1); !edge && sx <= Math.Min(width - 1, x + 1); sx++)
            {
                int j = sy * width + sx;
                edge = covered[j] && visibility[j] >= 0.5f != lit;
            }
            transition[i] = edge;
            any |= edge;
        }
        var refined = new bool[width * height];
        if (!any) return refined;
        // Dilate by the radius: rows, then columns, with running counts
        var rows = new bool[width * height];
        for (int y = 0; y < height; y++)
        {
            int count = 0;
            for (int x = 0; x < Math.Min(width, radius); x++) if (transition[y * width + x]) count++;
            for (int x = 0; x < width; x++)
            {
                if (x + radius < width && transition[y * width + x + radius]) count++;
                rows[y * width + x] = count > 0;
                if (x - radius >= 0 && transition[y * width + x - radius]) count--;
            }
        }
        for (int x = 0; x < width; x++)
        {
            int count = 0;
            for (int y = 0; y < Math.Min(height, radius); y++) if (rows[y * width + x]) count++;
            for (int y = 0; y < height; y++)
            {
                if (y + radius < height && rows[(y + radius) * width + x]) count++;
                refined[y * width + x] = count > 0 && covered[y * width + x];
                if (y - radius >= 0 && rows[(y - radius) * width + x]) count--;
            }
        }
        return refined;
    }

    /// <summary>
    /// The shadow map of one light over a mapping.
    /// </summary>
    /// <param name="width">The shadow map's (mapping's) size</param>
    /// <param name="upsample">High resolution texels per texel side</param>
    /// <param name="covered">Per texel: whether it's on the surface</param>
    /// <param name="lit">Per texel: lit at the shadow map's resolution (visibility of at least a half)</param>
    /// <param name="positions">Per texel: its sample's world position</param>
    /// <param name="blocks">Per texel: its block of upsample² high resolution texels (row-major within the block), or -1 if it isn't refined</param>
    /// <param name="highCovered">Per high resolution texel of the blocks: on the surface</param>
    /// <param name="highLit">Per high resolution texel of the blocks: lit</param>
    /// <param name="highPositions">Per high resolution texel of the blocks: world position</param>
    /// <param name="highTexelWorldSize">The high resolution texels' typical world size, for how far around a texel to search</param>
    /// <returns>The encoded distances, and a coverage that, as Lightmass's, leaves out texels off the surface and texels in full shadow</returns>
    public static (byte[] Values, byte[] Coverage) Build(int width, int height, int upsample, bool[] covered, bool[] lit, Vector3[] positions,
        int[] blocks, bool[] highCovered, bool[] highLit, Vector3[] highPositions, float maxTransitionDistance, float highTexelWorldSize)
    {
        int hw = width * upsample, hh = height * upsample, cells = upsample * upsample;
        var values = new byte[width * height];
        var coverage = new byte[width * height];
        int radius = maxTransitionDistance > 0 && highTexelWorldSize > 0
            ? Math.Clamp((int)MathF.Ceiling(maxTransitionDistance / highTexelWorldSize) + 1, 1, MaxSearchRadius) : upsample;
        float maxSquared = maxTransitionDistance * maxTransitionDistance;

        bool Cell(int hx, int hy, out bool cellLit, out Vector3 position)
        {
            int texel = hy / upsample * width + hx / upsample;
            int block = blocks[texel];
            if (block >= 0)
            {
                int i = block * cells + hy % upsample * upsample + hx % upsample;
                cellLit = highLit[i];
                position = highPositions[i];
                return highCovered[i];
            }
            cellLit = lit[texel];
            position = positions[texel];
            return covered[texel];
        }

        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int texel = y * width + x;
                if (!covered[texel]) continue;
                int block = blocks[texel];
                if (block < 0)
                {
                    values[texel] = Encode(1, lit[texel]);
                    coverage[texel] = (byte)(values[texel] > 0 ? 255 : 0);
                    continue;
                }
                // The texel's own high resolution texel: the one at its centre, else the state most of its high resolution texels have
                int cx = x * upsample + upsample / 2, cy = y * upsample + upsample / 2;
                bool state;
                Vector3 position;
                if (!Cell(cx, cy, out state, out position))
                {
                    int litCount = 0, shadowCount = 0;
                    for (int c = 0; c < cells; c++)
                        if (highCovered[block * cells + c]) { if (highLit[block * cells + c]) litCount++; else shadowCount++; }
                    state = litCount + shadowCount > 0 ? litCount >= shadowCount : lit[texel];
                    position = positions[texel];
                }

                // Rings outward from the centre; past a ring whose nearest texels are (going by their typical size) further than the best found, stop
                float best = float.MaxValue;
                for (int r = 0; r <= radius; r++)
                {
                    if (best < float.MaxValue && (r - 1) * highTexelWorldSize * 0.7f > MathF.Sqrt(best)) break;
                    void Test(int hx, int hy)
                    {
                        if (hx < 0 || hy < 0 || hx >= hw || hy >= hh) return;
                        if (!Cell(hx, hy, out bool other, out var p) || other == state) return;
                        float d = Vector3.DistanceSquared(p, position);
                        if (d < best) best = d;
                    }
                    if (r == 0) { Test(cx, cy); continue; }
                    for (int hx = cx - r; hx <= cx + r; hx++) { Test(hx, cy - r); Test(hx, cy + r); }
                    for (int hy = cy - r + 1; hy <= cy + r - 1; hy++) { Test(cx - r, hy); Test(cx + r, hy); }
                }
                float normalized = best < maxSquared && maxTransitionDistance > 0 ? MathF.Sqrt(best) / maxTransitionDistance : 1;
                values[texel] = Encode(normalized, state);
                coverage[texel] = (byte)(values[texel] > 0 ? 255 : 0);
            }
        });
        return (values, coverage);
    }
}
