using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace LegendaryExplorer.Lightmaps.Solver;

/// <summary>
/// Smooths the gathered (indirect) light within each receiver's lightmap: an edge-aware à-trous wavelet filter, weighted by how far apart the samples are
/// in the world relative to their texel size and by how alike their normals are. It stands in for Lightmass's irradiance cache interpolation.
/// </summary>
internal static class SolverFilter
{
    struct Value
    {
        public Vector3 A, B, C, S;
        public float O;

        public static Value operator *(Value v, float w) => new() { A = v.A * w, B = v.B * w, C = v.C * w, S = v.S * w, O = v.O * w };
        public static Value operator +(Value a, Value b) => new() { A = a.A + b.A, B = a.B + b.B, C = a.C + b.C, S = a.S + b.S, O = a.O + b.O };
    }

    static readonly float[] Kernel = [1f / 16f, 1f / 4f, 3f / 8f, 1f / 4f, 1f / 16f];

    public const int Iterations = 3;

    public static void Filter(SolverSampleGrid[] grids, GpuSample[] samples, bool[] gathered, Vector3[] i0, Vector3[] i1, Vector3[] i2, Vector3[] iS,
        float[] occlusion, bool filterOcclusion, float[] recordRadii, CancellationToken cancel)
    {
        Parallel.ForEach(grids, new ParallelOptions { CancellationToken = cancel }, grid =>
        {
            if (grid?.Samples == null || grid.Width <= 0 || grid.Height <= 0 || grid.Samples.Length < grid.Width * grid.Height)
                return;
            int w = grid.Width, h = grid.Height, cells = w * h;
            var index = new int[cells];
            var current = new Value[cells];
            var next = new Value[cells];
            bool any = false;
            for (int c = 0; c < cells; c++)
            {
                int s = grid.Samples[c];
                if (s < 0 || s >= gathered.Length || !gathered[s])
                {
                    index[c] = -1;
                    continue;
                }
                index[c] = s;
                current[c] = new Value { A = i0[s], B = i1[s], C = i2[s], S = iS[s], O = occlusion[s] };
                any = true;
            }
            if (!any)
                return;

            for (int it = 0; it < Iterations; it++)
            {
                cancel.ThrowIfCancellationRequested();
                int step = 1 << it;
                var src = current;
                var dst = next;
                void Row(int y)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int c = y * w + x;
                        int p = index[c];
                        if (p < 0)
                            continue;
                        ref readonly GpuSample sp = ref samples[p];
                        float r = MathF.Max(sp.TexelRadius, 1e-4f);
                        float sigma = r * step;
                        float invSigma2 = 1f / (sigma * sigma);
                        Value sum = default;
                        float wsum = 0;
                        for (int dy = -2; dy <= 2; dy++)
                        {
                            int yy = y + dy * step;
                            if ((uint)yy >= (uint)h)
                                continue;
                            for (int dx = -2; dx <= 2; dx++)
                            {
                                int xx = x + dx * step;
                                if ((uint)xx >= (uint)w)
                                    continue;
                                int cq = yy * w + xx;
                                int q = index[cq];
                                if (q < 0)
                                    continue;
                                float weight = Kernel[dx + 2] * Kernel[dy + 2];
                                if (q != p)
                                {
                                    ref readonly GpuSample sq = ref samples[q];
                                    float nd = Vector3.Dot(sp.Normal, sq.Normal);
                                    if (nd <= 0)
                                        continue;
                                    float nd2 = nd * nd, nd4 = nd2 * nd2, nd8 = nd4 * nd4;
                                    Vector3 delta = sq.Position - sp.Position;
                                    // neighbours this far apart in the lightmap are about √2·r·step·|offset| apart in the world
                                    float expected = 1.41421356f * r * step * MathF.Sqrt(dx * dx + dy * dy);
                                    float excess = MathF.Max(0, delta.Length() - 1.5f * expected);
                                    float plane = Vector3.Dot(delta, sp.Normal);
                                    weight *= nd8 * nd8 * MathF.Exp(-(excess * excess + plane * plane) * invSigma2);
                                    if (recordRadii != null)
                                    {
                                        // Lightmass's irradiance cache: a record reaches only so far, less near other surfaces
                                        float reach = MathF.Min(recordRadii[p], recordRadii[q]);
                                        weight *= MathF.Max(0, 1 - delta.Length() / MathF.Max(reach, 1e-3f));
                                    }
                                }
                                sum += src[cq] * weight;
                                wsum += weight;
                            }
                        }
                        dst[c] = wsum > 0 ? sum * (1f / wsum) : src[c];
                    }
                }
                if (cells >= 65536)
                    Parallel.For(0, h, Row);
                else
                    for (int y = 0; y < h; y++)
                        Row(y);
                (current, next) = (next, current);
            }

            for (int c = 0; c < cells; c++)
            {
                int s = index[c];
                if (s < 0)
                    continue;
                i0[s] = current[c].A;
                i1[s] = current[c].B;
                i2[s] = current[c].C;
                iS[s] = current[c].S;
                if (filterOcclusion)
                    occlusion[s] = current[c].O;
            }
        });
    }
}
