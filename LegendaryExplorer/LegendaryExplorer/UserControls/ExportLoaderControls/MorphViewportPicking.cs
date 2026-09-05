using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public enum MorphViewportPickMode { Features, Skeleton, Materials }

internal static class MorphViewportPicking
{
    internal static bool IntersectTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c,
        out float distance, out Vector3 barycentric)
    {
        distance = 0;
        barycentric = default;
        Vector3 edge1 = b - a, edge2 = c - a;
        Vector3 cross = Vector3.Cross(direction, edge2);
        float determinant = Vector3.Dot(edge1, cross);
        if (Math.Abs(determinant) < 0.000001f) return false;
        Vector3 relative = origin - a;
        float u = Vector3.Dot(relative, cross) / determinant;
        Vector3 secondCross = Vector3.Cross(relative, edge1);
        float v = Vector3.Dot(direction, secondCross) / determinant;
        if (u < 0 || v < 0 || u + v > 1) return false;
        distance = Vector3.Dot(edge2, secondCross) / determinant;
        barycentric = new Vector3(1 - u - v, u, v);
        return float.IsFinite(distance) && distance > 0.000001f;
    }

    internal static Dictionary<int, float> BlendBoneWeights(
        IReadOnlyList<(int Bone, float Weight)> first, IReadOnlyList<(int Bone, float Weight)> second,
        IReadOnlyList<(int Bone, float Weight)> third, Vector3 barycentric)
    {
        var weights = new Dictionary<int, float>();
        Add(first, barycentric.X);
        Add(second, barycentric.Y);
        Add(third, barycentric.Z);
        return weights;

        void Add(IReadOnlyList<(int Bone, float Weight)> influences, float factor)
        {
            foreach (var (bone, weight) in influences)
                if (bone >= 0 && weight > 0 && factor > 0)
                    weights[bone] = weights.GetValueOrDefault(bone) + weight * factor;
        }
    }

    internal static Dictionary<string, float> IncludeParentWeights(MeshBone[] skeleton, IReadOnlyDictionary<int, float> weights)
    {
        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (index, weight) in weights)
        {
            int bone = index;
            while (bone >= 0 && bone < skeleton.Length)
            {
                string name = skeleton[bone].Name.Instanced;
                result[name] = result.GetValueOrDefault(name) + weight;
                int parent = skeleton[bone].ParentIndex;
                if (parent < 0 || parent >= bone) break;
                bone = parent;
            }
        }
        return result;
    }

    // Weight displacement magnitudes, rather than summing vectors: opposite deltas still affect this region.
    internal static float FeatureStrength(MorphTarget.MorphVertex[] deltas, MorphTarget.BoneOffset[] offsets,
        int a, int b, int c, Vector3 barycentric, IReadOnlyDictionary<string, float> boneWeights)
    {
        float strength = 0;
        if (deltas != null)
            foreach (var delta in deltas)
            {
                float weight = delta.SourceIdx == a ? barycentric.X : delta.SourceIdx == b ? barycentric.Y
                    : delta.SourceIdx == c ? barycentric.Z : 0;
                strength += weight * delta.PositionDelta.Length();
            }
        if (offsets != null)
            foreach (var offset in offsets)
                if (boneWeights.TryGetValue(offset.Bone.Instanced, out float weight))
                    strength += weight * offset.Offset.Length();
        return float.IsFinite(strength) ? strength : 0;
    }
}
