using System;
using System.Collections.Generic;
using System.Linq;
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

/// <summary>
/// List wrapper that invalidates derived light-selection caches whenever the scene lights change.
/// Keeping invalidation here prevents callers in previews and the Level Editor from accidentally
/// serving stale lighting after a clear/reload.
/// </summary>
public sealed class SceneLightCollection(Action changed) : List<SceneLight>
{
    public new void Add(SceneLight item)
    {
        base.Add(item);
        changed?.Invoke();
    }

    public new void AddRange(IEnumerable<SceneLight> collection)
    {
        base.AddRange(collection);
        changed?.Invoke();
    }

    public new void Clear()
    {
        if (Count == 0) return;
        base.Clear();
        changed?.Invoke();
    }

    public new bool Remove(SceneLight item)
    {
        bool removed = base.Remove(item);
        if (removed) changed?.Invoke();
        return removed;
    }

    public new void RemoveAt(int index)
    {
        base.RemoveAt(index);
        changed?.Invoke();
    }
}

/// <summary>
/// Exact nearest-neighbor index for scene lights. Channel filtering is applied during traversal and
/// original list order breaks equal-distance ties, matching the former linear scan.
/// </summary>
internal sealed class SceneLightSpatialIndex
{
    private readonly IReadOnlyList<SceneLight> lights;
    private readonly Node root;

    private sealed class Node(int lightIndex, int axis)
    {
        public int LightIndex { get; } = lightIndex;
        public int Axis { get; } = axis;
        public Node Near { get; set; }
        public Node Far { get; set; }
    }

    public SceneLightSpatialIndex(IReadOnlyList<SceneLight> lights)
    {
        this.lights = lights;
        int[] indexes = Enumerable.Range(0, lights.Count).ToArray();
        root = Build(indexes, 0, indexes.Length, 0);
    }

    public SceneLight[] FindNearest(Vector3 position, uint lightingChannelMask)
    {
        Span<int> bestIndexes = stackalloc int[4] { -1, -1, -1, -1 };
        Span<float> bestDistances = stackalloc float[4]
            { float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue };
        Search(root, position, lightingChannelMask, bestIndexes, bestDistances);

        int count = 0;
        while (count < bestIndexes.Length && bestIndexes[count] >= 0) count++;
        var result = new SceneLight[count];
        for (int i = 0; i < count; i++) result[i] = lights[bestIndexes[i]];
        return result;
    }

    private Node Build(int[] indexes, int start, int length, int depth)
    {
        if (length <= 0) return null;
        int axis = depth % 3;
        Array.Sort(indexes, start, length, Comparer<int>.Create((left, right) =>
        {
            int comparison = GetAxis(lights[left].Position, axis).CompareTo(GetAxis(lights[right].Position, axis));
            return comparison != 0 ? comparison : left.CompareTo(right);
        }));
        int middle = start + length / 2;
        return new Node(indexes[middle], axis)
        {
            Near = Build(indexes, start, middle - start, depth + 1),
            Far = Build(indexes, middle + 1, start + length - middle - 1, depth + 1)
        };
    }

    private void Search(Node node, Vector3 position, uint lightingChannelMask,
        Span<int> bestIndexes, Span<float> bestDistances)
    {
        if (node is null) return;
        SceneLight light = lights[node.LightIndex];
        float delta = GetAxis(position, node.Axis) - GetAxis(light.Position, node.Axis);
        Node first = delta <= 0 ? node.Near : node.Far;
        Node second = delta <= 0 ? node.Far : node.Near;
        Search(first, position, lightingChannelMask, bestIndexes, bestDistances);

        if (SceneLight.ChannelsOverlap(light.LightingChannelMask, lightingChannelMask))
        {
            InsertCandidate(node.LightIndex, Vector3.DistanceSquared(light.Position, position), bestIndexes, bestDistances);
        }

        if (delta * delta <= bestDistances[^1])
        {
            Search(second, position, lightingChannelMask, bestIndexes, bestDistances);
        }
    }

    private static void InsertCandidate(int index, float distance, Span<int> bestIndexes, Span<float> bestDistances)
    {
        for (int slot = 0; slot < bestDistances.Length; slot++)
        {
            if (distance < bestDistances[slot]
                || (distance == bestDistances[slot] && (bestIndexes[slot] < 0 || index < bestIndexes[slot])))
            {
                for (int shift = bestDistances.Length - 1; shift > slot; shift--)
                {
                    bestDistances[shift] = bestDistances[shift - 1];
                    bestIndexes[shift] = bestIndexes[shift - 1];
                }
                bestDistances[slot] = distance;
                bestIndexes[slot] = index;
                return;
            }
        }
    }

    private static float GetAxis(Vector3 value, int axis) => axis switch
    {
        0 => value.X,
        1 => value.Y,
        _ => value.Z
    };
}
