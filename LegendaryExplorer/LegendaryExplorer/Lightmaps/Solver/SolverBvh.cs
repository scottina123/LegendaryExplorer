using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LegendaryExplorer.Lightmaps.Solver;

/// <summary>A GPU BVH node: two children, each with its bounds. Count 0: Data is an inner node's index; Count &gt; 0: a leaf of Count triangles from Data</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuBvhNode
{
    public Vector3 AMin;
    public uint AData;
    public Vector3 AMax;
    public uint ACount;
    public Vector3 BMin;
    public uint BData;
    public Vector3 BMax;
    public uint BCount;

    public const uint Invalid = 0xFFFFFFFF;
}

/// <summary>A binned-SAH bounding volume hierarchy over triangles, built on the CPU and flattened for the HLSL traversal</summary>
internal sealed class SolverBvh
{
    /// <summary>The flattened nodes; node 0 is the root</summary>
    public GpuBvhNode[] Nodes;
    /// <summary>Leaf triangle order: GPU triangle i is input triangle TriangleOrder[i]</summary>
    public int[] TriangleOrder;
    public int Depth;

    // More bins find better splits among a level's mix of huge walls and floors and small props (a faster BVH to trace, for the compute path);
    // the BVH is built once per bake, so the extra build time doesn't count
    const int Bins = 32;
    const int MaxDepth = 56; // the HLSL stack holds 64
    const int MaxLeafSize = 8;
    const int ParallelThreshold = 8192;

    struct BuildNode
    {
        public Vector3 Min, Max;
        public int Left, Right; // -1 for a leaf
        public int First, Count;
    }

    BuildNode[] build;
    int buildCount;
    Vector3[] triMin, triMax, centroid;
    int[] order;
    int maxDepthSeen;

    public static SolverBvh Build(Vector3[] triMin, Vector3[] triMax)
    {
        int n = triMin.Length;
        var bvh = new SolverBvh
        {
            triMin = triMin,
            triMax = triMax,
            centroid = new Vector3[n],
            order = new int[n],
            build = new BuildNode[Math.Max(1, 2 * n)],
        };
        Parallel.For(0, n, i =>
        {
            bvh.centroid[i] = (triMin[i] + triMax[i]) * 0.5f;
            bvh.order[i] = i;
        });
        if (n == 0)
        {
            bvh.Nodes = [new GpuBvhNode { AData = GpuBvhNode.Invalid, BData = GpuBvhNode.Invalid }];
            bvh.TriangleOrder = [];
            return bvh;
        }
        int root = bvh.Allocate();
        bvh.BuildRecursive(root, 0, n, 0);
        bvh.Flatten();
        bvh.TriangleOrder = bvh.order;
        bvh.Depth = bvh.maxDepthSeen;
        bvh.build = null;
        bvh.centroid = null;
        return bvh;
    }

    int Allocate() => Interlocked.Increment(ref buildCount) - 1;

    void BuildRecursive(int nodeIndex, int first, int count, int depth)
    {
        // bounds of the triangles and of their centroids
        Vector3 bmin = new(float.MaxValue), bmax = new(float.MinValue);
        Vector3 cmin = new(float.MaxValue), cmax = new(float.MinValue);
        for (int i = first; i < first + count; i++)
        {
            int t = order[i];
            bmin = Vector3.Min(bmin, triMin[t]);
            bmax = Vector3.Max(bmax, triMax[t]);
            cmin = Vector3.Min(cmin, centroid[t]);
            cmax = Vector3.Max(cmax, centroid[t]);
        }
        ref BuildNode node = ref build[nodeIndex];
        node.Min = bmin;
        node.Max = bmax;
        node.Left = node.Right = -1;
        node.First = first;
        node.Count = count;
        InterlockedMax(ref maxDepthSeen, depth);
        if (count <= 2 || depth >= MaxDepth)
            return;

        // binned SAH over the three axes
        int bestAxis = -1, bestSplit = -1;
        float bestCost = float.MaxValue;
        Vector3 extent = cmax - cmin;
        Span<int> binCount = stackalloc int[Bins];
        Span<Vector3> binMin = stackalloc Vector3[Bins];
        Span<Vector3> binMax = stackalloc Vector3[Bins];
        Span<float> rightArea = stackalloc float[Bins];
        Span<int> rightCount = stackalloc int[Bins];
        for (int axis = 0; axis < 3; axis++)
        {
            float lo = Component(cmin, axis), ext = Component(extent, axis);
            if (!(ext > 1e-12f))
                continue;
            float scale = Bins / ext;
            binCount.Clear();
            binMin.Fill(new Vector3(float.MaxValue));
            binMax.Fill(new Vector3(float.MinValue));
            for (int i = first; i < first + count; i++)
            {
                int t = order[i];
                int b = Math.Clamp((int)((Component(centroid[t], axis) - lo) * scale), 0, Bins - 1);
                binCount[b]++;
                binMin[b] = Vector3.Min(binMin[b], triMin[t]);
                binMax[b] = Vector3.Max(binMax[b], triMax[t]);
            }
            Vector3 rmin = new(float.MaxValue), rmax = new(float.MinValue);
            int rc = 0;
            for (int b = Bins - 1; b > 0; b--)
            {
                rc += binCount[b];
                rmin = Vector3.Min(rmin, binMin[b]);
                rmax = Vector3.Max(rmax, binMax[b]);
                rightCount[b] = rc;
                rightArea[b] = rc > 0 ? Area(rmin, rmax) : 0;
            }
            Vector3 lmin = new(float.MaxValue), lmax = new(float.MinValue);
            int lc = 0;
            for (int b = 0; b < Bins - 1; b++)
            {
                lc += binCount[b];
                lmin = Vector3.Min(lmin, binMin[b]);
                lmax = Vector3.Max(lmax, binMax[b]);
                int rcount = rightCount[b + 1];
                if (lc == 0 || rcount == 0)
                    continue;
                float cost = Area(lmin, lmax) * lc + rightArea[b + 1] * rcount;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestAxis = axis;
                    bestSplit = b + 1;
                }
            }
        }

        int mid;
        if (bestAxis < 0)
        {
            // every centroid in one spot: split by index if the leaf would be too big
            if (count <= MaxLeafSize * 4)
                return;
            mid = first + count / 2;
        }
        else
        {
            float parentArea = Area(bmin, bmax);
            float splitCost = 1f + (parentArea > 0 ? bestCost / parentArea : count);
            if (splitCost >= count && count <= MaxLeafSize)
                return;
            float lo = Component(cmin, bestAxis), scale = Bins / Component(extent, bestAxis);
            int i = first, j = first + count - 1;
            while (i <= j)
            {
                int b = Math.Clamp((int)((Component(centroid[order[i]], bestAxis) - lo) * scale), 0, Bins - 1);
                if (b < bestSplit)
                    i++;
                else
                {
                    (order[i], order[j]) = (order[j], order[i]);
                    j--;
                }
            }
            mid = i;
            if (mid == first || mid == first + count)
                mid = first + count / 2;
        }

        int left = Allocate(), right = Allocate();
        node.Left = left;
        node.Right = right;
        int leftCount = mid - first, rightCountTotal = count - leftCount;
        if (count >= ParallelThreshold)
        {
            Parallel.Invoke(
                () => BuildRecursive(left, first, leftCount, depth + 1),
                () => BuildRecursive(right, mid, rightCountTotal, depth + 1));
        }
        else
        {
            BuildRecursive(left, first, leftCount, depth + 1);
            BuildRecursive(right, mid, rightCountTotal, depth + 1);
        }
    }

    void Flatten()
    {
        int inner = 0;
        for (int i = 0; i < buildCount; i++)
            if (build[i].Left >= 0)
                inner++;
        var nodes = new GpuBvhNode[Math.Max(1, inner)];
        if (build[0].Left < 0)
        {
            // the whole scene is one leaf
            ref readonly BuildNode leaf = ref build[0];
            nodes[0] = new GpuBvhNode
            {
                AMin = leaf.Min, AMax = leaf.Max, AData = (uint)leaf.First, ACount = (uint)leaf.Count,
                BData = GpuBvhNode.Invalid,
            };
            Nodes = nodes;
            return;
        }
        int next = 1;
        var stack = new (int Build, int Gpu)[128];
        int sp = 0;
        stack[sp++] = (0, 0);
        while (sp > 0)
        {
            var (b, g) = stack[--sp];
            ref readonly BuildNode bn = ref build[b];
            ref readonly BuildNode l = ref build[bn.Left];
            ref readonly BuildNode r = ref build[bn.Right];
            var gn = new GpuBvhNode { AMin = l.Min, AMax = l.Max, BMin = r.Min, BMax = r.Max };
            if (l.Left < 0)
            {
                gn.AData = (uint)l.First;
                gn.ACount = (uint)l.Count;
            }
            else
            {
                int idx = next++;
                gn.AData = (uint)idx;
                stack[sp++] = (bn.Left, idx);
            }
            if (r.Left < 0)
            {
                gn.BData = (uint)r.First;
                gn.BCount = (uint)r.Count;
            }
            else
            {
                int idx = next++;
                gn.BData = (uint)idx;
                stack[sp++] = (bn.Right, idx);
            }
            nodes[g] = gn;
        }
        Nodes = nodes;
    }

    static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    static float Area(Vector3 min, Vector3 max)
    {
        Vector3 d = Vector3.Max(max - min, Vector3.Zero);
        return d.X * d.Y + d.Y * d.Z + d.Z * d.X;
    }

    static void InterlockedMax(ref int target, int value)
    {
        int current = Volatile.Read(ref target);
        while (value > current)
        {
            int previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
                return;
            current = previous;
        }
    }
}
