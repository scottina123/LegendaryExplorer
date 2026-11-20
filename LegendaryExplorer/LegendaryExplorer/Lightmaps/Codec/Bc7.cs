using System;
using System.Threading.Tasks;

namespace LegendaryExplorer.Lightmaps.Codec;

/// <summary>
/// BC7 for opaque lightmaps. Every block is first encoded in mode 6 (one endpoint pair of 7 bits plus a p-bit per channel, sixteen
/// 4-bit interpolation steps): lightmap blocks are mostly smooth gradients and shadow edges between two colours, which mode 6 holds
/// to about a byte. Its endpoints start on the block's principal axis, then alternate between the best p-bits and indices for them
/// and the least-squares endpoints for those indices. Blocks it can't fit that closely (three or more unrelated colours, which need
/// BC7's multi-subset modes) go through BCnEncoder.NET's full BC7 search, gathered into one image, and the better of the two is kept.
/// (LEC's TexConverter BC7 encoder averages pairs of texels across each block, losing half the horizontal detail.)
/// </summary>
public static class Bc7
{
    static readonly int[] Weights = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];
    const int Refinements = 3;
    /// <summary>A block whose mode 6 squared error (summed over its 48 channel values) is above this, an RMS of 1, is searched in every mode</summary>
    const float FullSearchError = 48;

    /// <summary>BC7 blocks for an RGB image (3 bytes a texel), alpha 255; edges of a size that isn't a multiple of 4 repeat the last texel</summary>
    public static byte[] Encode(byte[] rgb, int width, int height)
    {
        int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
        var output = new byte[blocksX * blocksY * 16];
        var errors = new float[blocksX * blocksY];
        Parallel.For(0, blocksY, by =>
        {
            Span<float> block = stackalloc float[48];
            for (int bx = 0; bx < blocksX; bx++)
            {
                Gather(rgb, width, height, bx, by, block);
                errors[by * blocksX + bx] = EncodeBlock(block, output.AsSpan((by * blocksX + bx) * 16, 16));
            }
        });
        SearchHardBlocks(rgb, width, height, blocksX, output, errors);
        return output;
    }

    static void Gather(byte[] rgb, int width, int height, int bx, int by, Span<float> block)
    {
        for (int i = 0; i < 16; i++)
        {
            int x = Math.Min(bx * 4 + (i & 3), width - 1), y = Math.Min(by * 4 + (i >> 2), height - 1);
            int s = (y * width + x) * 3;
            block[i * 3] = rgb[s]; block[i * 3 + 1] = rgb[s + 1]; block[i * 3 + 2] = rgb[s + 2];
        }
    }

    /// <summary>The blocks mode 6 fits poorly, encoded in every mode by BCnEncoder.NET (side by side in one strip of blocks), each kept where it's closer</summary>
    static void SearchHardBlocks(byte[] rgb, int width, int height, int blocksX, byte[] output, float[] errors)
    {
        var hard = new System.Collections.Generic.List<int>();
        for (int b = 0; b < errors.Length; b++) if (errors[b] > FullSearchError) hard.Add(b);
        if (hard.Count == 0) return;
        // The hard blocks as tiles of a strip image, 256 blocks across
        int across = Math.Min(hard.Count, 256), down = (hard.Count + across - 1) / across;
        int stripWidth = across * 4, stripHeight = down * 4;
        var rgba = new byte[stripWidth * stripHeight * 4];
        Span<float> block = stackalloc float[48];
        for (int h = 0; h < hard.Count; h++)
        {
            Gather(rgb, width, height, hard[h] % blocksX, hard[h] / blocksX, block);
            int tx = h % across * 4, ty = h / across * 4;
            for (int i = 0; i < 16; i++)
            {
                int d = ((ty + (i >> 2)) * stripWidth + tx + (i & 3)) * 4;
                rgba[d] = (byte)block[i * 3]; rgba[d + 1] = (byte)block[i * 3 + 1]; rgba[d + 2] = (byte)block[i * 3 + 2]; rgba[d + 3] = 255;
            }
        }
        var encoder = new BCnEncoder.Encoder.BcEncoder(BCnEncoder.Shared.CompressionFormat.Bc7)
        {
            OutputOptions = { Quality = BCnEncoder.Encoder.CompressionQuality.BestQuality, GenerateMipMaps = false },
        };
        encoder.Options.IsParallel = hard.Count > 64;
        var searched = encoder.EncodeToRawBytes(rgba, stripWidth, stripHeight, BCnEncoder.Encoder.PixelFormat.Rgba32)[0];
        var decoded = new BCnEncoder.Decoder.BcDecoder().DecodeRaw(searched, stripWidth, stripHeight, BCnEncoder.Shared.CompressionFormat.Bc7);
        for (int h = 0; h < hard.Count; h++)
        {
            int tx = h % across * 4, ty = h / across * 4;
            float error = 0;
            for (int i = 0; i < 16; i++)
            {
                int p = (ty + (i >> 2)) * stripWidth + tx + (i & 3);
                var c = decoded[p];
                float dr = c.r - rgba[p * 4], dg = c.g - rgba[p * 4 + 1], db = c.b - rgba[p * 4 + 2];
                error += dr * dr + dg * dg + db * db;
            }
            if (error < errors[hard[h]])
                Array.Copy(searched, (ty / 4 * across + tx / 4) * 16, output, hard[h] * 16, 16);
        }
    }

    /// <summary>Encodes one block in mode 6 and returns its squared error</summary>
    static float EncodeBlock(ReadOnlySpan<float> px, Span<byte> output)
    {
        // Mean and principal axis (power iteration on the covariance)
        float mr = 0, mg = 0, mb = 0;
        for (int i = 0; i < 16; i++) { mr += px[i * 3]; mg += px[i * 3 + 1]; mb += px[i * 3 + 2]; }
        mr /= 16; mg /= 16; mb /= 16;
        float crr = 0, crg = 0, crb = 0, cgg = 0, cgb = 0, cbb = 0;
        for (int i = 0; i < 16; i++)
        {
            float r = px[i * 3] - mr, g = px[i * 3 + 1] - mg, b = px[i * 3 + 2] - mb;
            crr += r * r; crg += r * g; crb += r * b; cgg += g * g; cgb += g * b; cbb += b * b;
        }
        float ar = 1, ag = 1, ab = 1;
        for (int k = 0; k < 8; k++)
        {
            float nr = crr * ar + crg * ag + crb * ab, ng = crg * ar + cgg * ag + cgb * ab, nb = crb * ar + cgb * ag + cbb * ab;
            float len = MathF.Sqrt(nr * nr + ng * ng + nb * nb);
            if (len < 1e-12f) break;
            (ar, ag, ab) = (nr / len, ng / len, nb / len);
        }
        float tmin = float.MaxValue, tmax = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            float t = (px[i * 3] - mr) * ar + (px[i * 3 + 1] - mg) * ag + (px[i * 3 + 2] - mb) * ab;
            tmin = MathF.Min(tmin, t); tmax = MathF.Max(tmax, t);
        }
        Span<float> e = [mr + ar * tmin, mg + ag * tmin, mb + ab * tmin, mr + ar * tmax, mg + ag * tmax, mb + ab * tmax];

        Span<int> q = stackalloc int[6];
        Span<int> bestQ = stackalloc int[6];
        Span<byte> indices = stackalloc byte[16];
        Span<byte> bestIndices = stackalloc byte[16];
        Span<float> r0 = stackalloc float[3], r1 = stackalloc float[3];
        int bestP0 = 0, bestP1 = 0;
        float bestError = float.MaxValue;
        for (int pass = 0; pass <= Refinements; pass++)
        {
            for (int p0 = 0; p0 < 2; p0++)
            for (int p1 = 0; p1 < 2; p1++)
            {
                for (int c = 0; c < 3; c++)
                {
                    q[c] = Math.Clamp((int)MathF.Round((e[c] - p0) / 2), 0, 127);
                    q[3 + c] = Math.Clamp((int)MathF.Round((e[3 + c] - p1) / 2), 0, 127);
                }
                float error = Assign(px, q, p0, p1, indices, bestError);
                if (error < bestError)
                {
                    bestError = error; bestP0 = p0; bestP1 = p1;
                    q.CopyTo(bestQ); indices.CopyTo(bestIndices);
                }
            }
            if (bestError == 0 || pass == Refinements) break;
            // Least-squares endpoints for the best indices: x_i = (1 - t_i) e0 + t_i e1
            float s00 = 0, s01 = 0, s11 = 0;
            r0.Clear(); r1.Clear();
            for (int i = 0; i < 16; i++)
            {
                float t = Weights[bestIndices[i]] / 64f, u = 1 - t;
                s00 += u * u; s01 += u * t; s11 += t * t;
                for (int c = 0; c < 3; c++) { r0[c] += u * px[i * 3 + c]; r1[c] += t * px[i * 3 + c]; }
            }
            float det = s00 * s11 - s01 * s01;
            if (MathF.Abs(det) < 1e-6f) break;
            for (int c = 0; c < 3; c++)
            {
                e[c] = Math.Clamp((r0[c] * s11 - r1[c] * s01) / det, 0, 255);
                e[3 + c] = Math.Clamp((r1[c] * s00 - r0[c] * s01) / det, 0, 255);
            }
        }

        // The first texel's index has an implied leading 0: swap the endpoints if it would need one
        if (bestIndices[0] >= 8)
        {
            for (int c = 0; c < 3; c++) (bestQ[c], bestQ[3 + c]) = (bestQ[3 + c], bestQ[c]);
            (bestP0, bestP1) = (bestP1, bestP0);
            for (int i = 0; i < 16; i++) bestIndices[i] = (byte)(15 - bestIndices[i]);
        }
        Pack(bestQ, bestP0, bestP1, bestIndices, output);
        return bestError;
    }

    /// <summary>Each texel's nearest of the 16 colours between the quantized endpoints, and the summed squared error (stops early past <paramref name="limit"/>)</summary>
    static float Assign(ReadOnlySpan<float> px, ReadOnlySpan<int> q, int p0, int p1, Span<byte> indices, float limit)
    {
        Span<float> palette = stackalloc float[48];
        for (int c = 0; c < 3; c++)
        {
            int a = q[c] * 2 + p0, b = q[3 + c] * 2 + p1;
            for (int k = 0; k < 16; k++) palette[k * 3 + c] = ((64 - Weights[k]) * a + Weights[k] * b + 32) >> 6;
        }
        float total = 0;
        for (int i = 0; i < 16; i++)
        {
            float best = float.MaxValue; int bestK = 0;
            for (int k = 0; k < 16; k++)
            {
                float dr = px[i * 3] - palette[k * 3], dg = px[i * 3 + 1] - palette[k * 3 + 1], db = px[i * 3 + 2] - palette[k * 3 + 2];
                float d = dr * dr + dg * dg + db * db;
                if (d < best) { best = d; bestK = k; }
            }
            indices[i] = (byte)bestK;
            total += best;
            if (total >= limit) return float.MaxValue;
        }
        return total;
    }

    /// <summary>Mode 6's 128 bits, least significant first: mode (0000001), R0 R1 G0 G1 B0 B1 A0 A1 (7 bits each), P0, P1, then the indices (3 bits for the first, 4 for the rest)</summary>
    static void Pack(ReadOnlySpan<int> q, int p0, int p1, ReadOnlySpan<byte> indices, Span<byte> output)
    {
        ulong lo = 0, hi = 0;
        int position = 0;
        void Put(int value, int bits)
        {
            for (int b = 0; b < bits; b++, position++)
            {
                ulong bit = (ulong)((value >> b) & 1);
                if (position < 64) lo |= bit << position;
                else hi |= bit << (position - 64);
            }
        }
        Put(1 << 6, 7);
        for (int c = 0; c < 3; c++) { Put(q[c], 7); Put(q[3 + c], 7); }
        Put(127, 7); Put(127, 7); // alpha 254/255 by the p-bits; the game doesn't read a lightmap's alpha
        Put(p0, 1); Put(p1, 1);
        Put(indices[0], 3);
        for (int i = 1; i < 16; i++) Put(indices[i], 4);
        BitConverter.TryWriteBytes(output, lo);
        BitConverter.TryWriteBytes(output[8..], hi);
    }
}
