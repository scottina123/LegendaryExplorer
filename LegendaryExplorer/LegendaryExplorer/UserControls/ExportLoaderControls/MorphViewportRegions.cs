using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Media;
using LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

internal static class MorphViewportRegions
{
    // Use displacement magnitudes so opposite movements do not cancel. Include inherited bone
    // weights: moving a parent also affects vertices skinned to its children.
    internal static float[] FeatureWeights(int vertexCount, MorphTarget.MorphVertex[] deltas,
        MorphTarget.BoneOffset[] offsets, IReadOnlyDictionary<string, float>[] boneWeights)
    {
        var result = new float[vertexCount];
        foreach (var delta in deltas ?? [])
            if (delta.SourceIdx < vertexCount && float.IsFinite(delta.PositionDelta.Length()))
                result[delta.SourceIdx] += delta.PositionDelta.Length();
        foreach (var offset in offsets ?? [])
        {
            float movement = offset.Offset.Length();
            if (!float.IsFinite(movement)) continue;
            for (int vertex = 0; vertex < Math.Min(vertexCount, boneWeights.Length); vertex++)
                if (boneWeights[vertex].TryGetValue(offset.Bone.Instanced, out float weight))
                    result[vertex] += movement * weight;
        }
        return result;
    }

    internal static Color RegionColor(int index)
    {
        // Golden-angle spacing keeps adjacent labels distinct. The same ordering gives the same
        // colors after hiding/showing labels, switching LODs, or changing the current selection.
        double hue = (index * 0.618033988749895 + 0.06) % 1 * 6;
        // Keep even blue/purple captions readable against the dark viewport legend.
        double chroma = 0.48, x = chroma * (1 - Math.Abs(hue % 2 - 1)), m = 0.50;
        var rgb = (int)hue switch
        {
            0 => (chroma, x, 0d), 1 => (x, chroma, 0d), 2 => (0d, chroma, x),
            3 => (0d, x, chroma), 4 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        return Color.FromRgb((byte)((rgb.Item1 + m) * 255), (byte)((rgb.Item2 + m) * 255), (byte)((rgb.Item3 + m) * 255));
    }

    internal static Vector4[] SurfaceColors(int vertexCount, IReadOnlyList<float[]> weights,
        IReadOnlyList<Color> colors, int focusedRegion = -1, IReadOnlyList<Triangle> triangles = null, int[] owners = null)
    {
        int count = triangles?.Count ?? vertexCount;
        var result = new Vector4[triangles != null ? count * 3 : count];
        var best = new float[count];
        if (owners != null) Array.Fill(owners, -1);
        for (int region = 0; region < weights.Count; region++)
        {
            if (focusedRegion >= 0 && focusedRegion != region) continue;
            var values = weights[region];
            float maximum = values.Length == 0 ? 0 : values.Max();
            if (maximum <= 0.00001f) continue;
            // Favor local controls over broad race/face-shape targets in the overview. Hover or
            // selection shows the complete influence of one control, including overlapping areas.
            float locality = 1 / MathF.Sqrt(Math.Max(1, values.Count(value => value > maximum * 0.05f)));
            var color = colors[region];
            for (int element = 0; element < count; element++)
            {
                int a = element, b = element, c = element;
                if (triangles != null)
                {
                    a = (int)triangles[element].Vertex1;
                    b = (int)triangles[element].Vertex2;
                    c = (int)triangles[element].Vertex3;
                }
                if ((uint)a >= values.Length || (uint)b >= values.Length || (uint)c >= values.Length) continue;
                float strength = (values[a] + values[b] + values[c]) / (3 * maximum);
                float score = strength * locality;
                if (!float.IsFinite(score) || strength <= 0.001f || score <= best[element]) continue;
                best[element] = score;
                if (owners != null) owners[element] = region;
                Set(triangles != null ? element * 3 : element, a);
                if (triangles != null) { Set(element * 3 + 1, b); Set(element * 3 + 2, c); }

                void Set(int corner, int vertex)
                {
                    float opacity = focusedRegion < 0 ? 0.52f : values[vertex] <= 0 ? 0
                        : 0.25f + 0.45f * MathF.Sqrt(values[vertex] / maximum);
                    result[corner] = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, opacity);
                }
            }
        }
        return result;
    }
}
