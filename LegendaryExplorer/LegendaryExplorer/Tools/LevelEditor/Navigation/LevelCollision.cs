using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LegendaryExplorer.Tools.LevelEditor;

[Flags]
public enum LevelCollisionFlags
{
    None = 0,
    BlocksRay = 1,
    BlocksShape = 2,
    CoverProbe = 4,
    Navigation = BlocksRay | BlocksShape,
    All = Navigation | CoverProbe
}

public readonly record struct LevelCollisionHit(
    float Distance,
    Vector3 Position,
    Vector3 Normal,
    ExportEntry Source,
    int SourceTriangleIndex = -1);

public readonly record struct LevelCoverSurfaceSeed(
    Vector3 Position,
    Vector3 Normal,
    ExportEntry Source);

internal readonly record struct LevelCollisionTriangle(
    Vector3 A,
    Vector3 B,
    Vector3 C,
    Vector3 Edge1,
    Vector3 Edge2,
    Vector3 Centroid,
    Vector3 Normal,
    CollisionBounds Bounds,
    LevelCollisionFlags Flags,
    ExportEntry Source,
    int SourceTriangleIndex = -1)
{
    public static bool TryCreate(Vector3 a, Vector3 b, Vector3 c, LevelCollisionFlags flags,
        ExportEntry source, out LevelCollisionTriangle triangle, int sourceTriangleIndex = -1)
    {
        Vector3 cross = Vector3.Cross(b - a, c - a);
        float lengthSquared = cross.LengthSquared();
        if (lengthSquared < 0.0001f || !float.IsFinite(lengthSquared))
        {
            triangle = default;
            return false;
        }

        triangle = new LevelCollisionTriangle(a, b, c, b - a, c - a, (a + b + c) / 3f,
            cross / MathF.Sqrt(lengthSquared),
            CollisionBounds.FromTriangle(a, b, c), flags, source, sourceTriangleIndex);
        return true;
    }
}

internal readonly record struct CollisionRay(Vector3 Origin, Vector3 Direction, Vector3 InverseDirection,
    int ParallelAxisMask)
{
    public static bool TryCreate(Vector3 origin, Vector3 direction, out CollisionRay ray)
    {
        float lengthSquared = direction.LengthSquared();
        if (lengthSquared < 0.000001f || !float.IsFinite(lengthSquared))
        {
            ray = default;
            return false;
        }
        if (MathF.Abs(lengthSquared - 1f) > 0.00001f)
            direction /= MathF.Sqrt(lengthSquared);
        return TryCreateNormalized(origin, direction, out ray);
    }

    /// <summary>Creates a ray whose direction has already been normalized by the caller.</summary>
    public static bool TryCreateNormalized(Vector3 origin, Vector3 direction, out CollisionRay ray)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z) ||
            MathF.Abs(direction.X) + MathF.Abs(direction.Y) + MathF.Abs(direction.Z) < 0.000001f)
        {
            ray = default;
            return false;
        }
        int parallelMask = 0;
        Vector3 inverse = default;
        if (MathF.Abs(direction.X) < 0.000001f) parallelMask |= 1;
        else inverse.X = 1f / direction.X;
        if (MathF.Abs(direction.Y) < 0.000001f) parallelMask |= 2;
        else inverse.Y = 1f / direction.Y;
        if (MathF.Abs(direction.Z) < 0.000001f) parallelMask |= 4;
        else inverse.Z = 1f / direction.Z;
        ray = new CollisionRay(origin, direction, inverse, parallelMask);
        return true;
    }
}

internal readonly record struct CollisionBounds(Vector3 Minimum, Vector3 Maximum)
{
    public Vector3 Size => Maximum - Minimum;
    public Vector3 Center => (Minimum + Maximum) * 0.5f;

    public static CollisionBounds Empty => new(
        new Vector3(float.PositiveInfinity), new Vector3(float.NegativeInfinity));

    public static CollisionBounds FromTriangle(Vector3 a, Vector3 b, Vector3 c) =>
        new(Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c)));

    public CollisionBounds Include(CollisionBounds other) => new(
        Vector3.Min(Minimum, other.Minimum), Vector3.Max(Maximum, other.Maximum));

    public CollisionBounds Expanded(float amount)
    {
        Vector3 expansion = new(amount);
        return new CollisionBounds(Minimum - expansion, Maximum + expansion);
    }

    public bool Intersects(CollisionBounds other) =>
        Minimum.X <= other.Maximum.X && Maximum.X >= other.Minimum.X &&
        Minimum.Y <= other.Maximum.Y && Maximum.Y >= other.Minimum.Y &&
        Minimum.Z <= other.Maximum.Z && Maximum.Z >= other.Minimum.Z;

    public bool IntersectsRay(Vector3 origin, Vector3 direction, float maximumDistance)
    {
        float minimumT = 0f;
        float maximumT = maximumDistance;
        for (int axis = 0; axis < 3; axis++)
        {
            float component = Get(origin, axis);
            float directionComponent = Get(direction, axis);
            float minimum = Get(Minimum, axis);
            float maximum = Get(Maximum, axis);
            if (MathF.Abs(directionComponent) < 0.000001f)
            {
                if (component < minimum || component > maximum)
                {
                    return false;
                }
                continue;
            }

            float inverse = 1f / directionComponent;
            float first = (minimum - component) * inverse;
            float second = (maximum - component) * inverse;
            if (first > second)
            {
                (first, second) = (second, first);
            }
            minimumT = MathF.Max(minimumT, first);
            maximumT = MathF.Min(maximumT, second);
            if (minimumT > maximumT)
            {
                return false;
            }
        }
        return maximumT >= 0f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IntersectsRay(in CollisionRay ray, float maximumDistance)
    {
        float minimumT = 0f;
        float maximumT = maximumDistance;
        if (!IntersectRayAxis(ray.Origin.X, ray.InverseDirection.X, (ray.ParallelAxisMask & 1) != 0,
                Minimum.X, Maximum.X, ref minimumT, ref maximumT) ||
            !IntersectRayAxis(ray.Origin.Y, ray.InverseDirection.Y, (ray.ParallelAxisMask & 2) != 0,
                Minimum.Y, Maximum.Y, ref minimumT, ref maximumT) ||
            !IntersectRayAxis(ray.Origin.Z, ray.InverseDirection.Z, (ray.ParallelAxisMask & 4) != 0,
                Minimum.Z, Maximum.Z, ref minimumT, ref maximumT) || maximumT < 0f)
            return false;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IntersectRayAxis(float origin, float inverseDirection, bool parallel,
        float minimum, float maximum, ref float minimumT, ref float maximumT)
    {
        if (parallel)
            return origin >= minimum && origin <= maximum;
        float first = (minimum - origin) * inverseDirection;
        float second = (maximum - origin) * inverseDirection;
        if (first > second)
            (first, second) = (second, first);
        minimumT = MathF.Max(minimumT, first);
        maximumT = MathF.Min(maximumT, second);
        return minimumT <= maximumT;
    }

    private static float Get(Vector3 vector, int axis) => axis switch
    {
        0 => vector.X,
        1 => vector.Y,
        _ => vector.Z
    };
}

/// <summary>
/// CPU collision scene used by Level Editor tools. Geometry is indexed by a BVH so thousands of
/// floor probes and clearance sweeps do not scan every mesh triangle.
/// </summary>
public sealed class LevelCollisionScene
{
    private const int LeafTriangleCount = 8;
    private const int BvhBuildBinCount = 12;
    private const float ContactSkin = 0.75f;

    private readonly List<LevelCollisionTriangle> triangles;
    private readonly int[] triangleIndices;
    private readonly List<BvhNode> nodes = [];
    private readonly CollisionBounds navigationBounds;

    public int TriangleCount => triangles.Count;
    public int NavigationTriangleCount { get; }
    public int CoverTriangleCount { get; }
    public int NavigationSourceCount { get; }
    public int CoverSourceCount { get; }
    public int BvhNodeCount => nodes.Count;
    public double BvhBuildMilliseconds { get; }
    public Vector3 Minimum => NavigationTriangleCount == 0 ? Vector3.Zero : navigationBounds.Minimum;
    public Vector3 Maximum => NavigationTriangleCount == 0 ? Vector3.Zero : navigationBounds.Maximum;

    private readonly record struct BvhNode(CollisionBounds Bounds, int Left, int Right, int Start, int Count,
        int SplitAxis)
    {
        public bool IsLeaf => Count > 0;
    }

    private struct BvhBuildBin
    {
        public CollisionBounds Bounds;
        public int Count;
    }

    internal LevelCollisionScene(List<LevelCollisionTriangle> sourceTriangles)
    {
        Stopwatch buildTimer = Stopwatch.StartNew();
        triangles = sourceTriangles;
        CollisionBounds blockingBounds = CollisionBounds.Empty;
        int navigationTriangleCount = 0;
        int coverTriangleCount = 0;
        HashSet<ExportEntry> navigationSources = [];
        HashSet<ExportEntry> coverSources = [];
        foreach (LevelCollisionTriangle triangle in triangles)
        {
            if ((triangle.Flags & LevelCollisionFlags.Navigation) != 0)
            {
                navigationTriangleCount++;
                blockingBounds = blockingBounds.Include(triangle.Bounds);
                if (triangle.Source is not null)
                    navigationSources.Add(triangle.Source);
            }
            if ((triangle.Flags & LevelCollisionFlags.CoverProbe) != 0)
            {
                coverTriangleCount++;
                if (triangle.Source is not null)
                    coverSources.Add(triangle.Source);
            }
        }
        NavigationTriangleCount = navigationTriangleCount;
        CoverTriangleCount = coverTriangleCount;
        NavigationSourceCount = navigationSources.Count;
        CoverSourceCount = coverSources.Count;
        navigationBounds = blockingBounds;
        triangleIndices = Enumerable.Range(0, triangles.Count).ToArray();
        if (triangles.Count > 0)
        {
            BuildNode(0, triangles.Count);
        }
        buildTimer.Stop();
        BvhBuildMilliseconds = buildTimer.Elapsed.TotalMilliseconds;
    }

    public static LevelCollisionScene Build(IEnumerable<ActorProxy> actors)
    {
        var collisionTriangles = new List<LevelCollisionTriangle>();
        foreach (ActorProxy actor in actors)
        {
            if (actor.IsVolumetricMesh || actor is EmitterActorProxy or PawnProxy)
            {
                continue;
            }

            foreach (PrimitiveComponentProxy component in actor.Components)
            {
                switch (component)
                {
                    case StaticMeshComponentProxy staticMesh:
                        staticMesh.AppendNavigationCollision(collisionTriangles);
                        break;
                    case BrushComponentProxy brush when IsBlockingBrush(actor):
                        brush.AppendNavigationCollision(collisionTriangles);
                        break;
                }
            }
        }
        return new LevelCollisionScene(collisionTriangles);
    }

    /// <summary>Builds a collision scene from raw triangles for diagnostics and reusable geometry tools.</summary>
    public static LevelCollisionScene FromTriangles(
        IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> sourceTriangles,
        LevelCollisionFlags flags = LevelCollisionFlags.All)
    {
        ArgumentNullException.ThrowIfNull(sourceTriangles);
        var collisionTriangles = new List<LevelCollisionTriangle>();
        foreach ((Vector3 a, Vector3 b, Vector3 c) in sourceTriangles)
        {
            if (LevelCollisionTriangle.TryCreate(a, b, c, flags, null, out LevelCollisionTriangle triangle))
            {
                collisionTriangles.Add(triangle);
            }
        }
        return new LevelCollisionScene(collisionTriangles);
    }

    /// <summary>Builds a diagnostic scene where each triangle can carry independent collision filtering.</summary>
    public static LevelCollisionScene FromTriangles(
        IEnumerable<(Vector3 A, Vector3 B, Vector3 C, LevelCollisionFlags Flags)> sourceTriangles)
    {
        ArgumentNullException.ThrowIfNull(sourceTriangles);
        var collisionTriangles = new List<LevelCollisionTriangle>();
        foreach ((Vector3 a, Vector3 b, Vector3 c, LevelCollisionFlags flags) in sourceTriangles)
        {
            if (LevelCollisionTriangle.TryCreate(a, b, c, flags, null, out LevelCollisionTriangle triangle))
                collisionTriangles.Add(triangle);
        }
        return new LevelCollisionScene(collisionTriangles);
    }

    /// <summary>
    /// Builds a ray scene whose triangles retain their component and source-triangle identity. Static
    /// lighting uses this to reject only receiver self-intersections without disabling legitimate
    /// shadows cast by another part of the same mesh component.
    /// </summary>
    public static LevelCollisionScene FromTriangles(
        IEnumerable<(Vector3 A, Vector3 B, Vector3 C, ExportEntry Source, int SourceTriangleIndex)> sourceTriangles,
        LevelCollisionFlags flags = LevelCollisionFlags.All)
    {
        ArgumentNullException.ThrowIfNull(sourceTriangles);
        var collisionTriangles = new List<LevelCollisionTriangle>();
        foreach ((Vector3 a, Vector3 b, Vector3 c, ExportEntry source, int sourceTriangleIndex) in sourceTriangles)
        {
            if (LevelCollisionTriangle.TryCreate(a, b, c, flags, source, out LevelCollisionTriangle triangle,
                    sourceTriangleIndex))
                collisionTriangles.Add(triangle);
        }
        return new LevelCollisionScene(collisionTriangles);
    }

    private static bool IsBlockingBrush(ActorProxy actor) =>
        actor.Export.ClassName.Contains("BlockingVolume", StringComparison.OrdinalIgnoreCase) ||
        !actor.IsVolume;

    public bool Raycast(Vector3 origin, Vector3 direction, float maximumDistance,
        out LevelCollisionHit hit, LevelCollisionFlags requiredFlags = LevelCollisionFlags.BlocksRay)
        => RaycastCore(origin, direction, maximumDistance, out hit, requiredFlags,
            null, -1, 0f, out _);

    /// <summary>
    /// Raycasts while rejecting the originating receiver triangle and near-zero hits from the same
    /// component. Farther triangles on that component still cast shadows.
    /// </summary>
    public bool RaycastFiltered(Vector3 origin, Vector3 direction, float maximumDistance,
        ExportEntry receiverSource, int receiverTriangleIndex, float selfIntersectionDistance,
        out LevelCollisionHit hit, out int rejectedSelfIntersections,
        LevelCollisionFlags requiredFlags = LevelCollisionFlags.BlocksRay)
        => RaycastCore(origin, direction, maximumDistance, out hit, requiredFlags,
            receiverSource, receiverTriangleIndex, MathF.Max(0f, selfIntersectionDistance),
            out rejectedSelfIntersections);

    /// <summary>
    /// Any-hit shadow query. Unlike <see cref="RaycastFiltered"/>, this returns as soon as a valid
    /// blocker is found and avoids delegate allocation and closest-hit traversal work.
    /// </summary>
    public bool IsOccludedFiltered(Vector3 origin, Vector3 direction, float maximumDistance,
        ExportEntry receiverSource, int receiverTriangleIndex, float selfIntersectionDistance,
        out int rejectedSelfIntersections,
        LevelCollisionFlags requiredFlags = LevelCollisionFlags.BlocksRay)
        => IsOccludedFilteredCore(origin, direction, maximumDistance, receiverSource,
            receiverTriangleIndex, selfIntersectionDistance, out rejectedSelfIntersections,
            requiredFlags, false);

    /// <summary>
    /// Lighting-only fast path. Prepared light directions are already normalized, so avoid
    /// redundantly measuring and normalizing each of the millions of shadow rays in a level bake.
    /// </summary>
    internal bool IsOccludedFilteredNormalized(Vector3 origin, Vector3 direction, float maximumDistance,
        ExportEntry receiverSource, int receiverTriangleIndex, float selfIntersectionDistance,
        out int rejectedSelfIntersections,
        LevelCollisionFlags requiredFlags = LevelCollisionFlags.BlocksRay)
        => IsOccludedFilteredCore(origin, direction, maximumDistance, receiverSource,
            receiverTriangleIndex, selfIntersectionDistance, out rejectedSelfIntersections,
            requiredFlags, true);

    private bool IsOccludedFilteredCore(Vector3 origin, Vector3 direction, float maximumDistance,
        ExportEntry receiverSource, int receiverTriangleIndex, float selfIntersectionDistance,
        out int rejectedSelfIntersections, LevelCollisionFlags requiredFlags, bool directionIsNormalized)
    {
        rejectedSelfIntersections = 0;
        if (nodes.Count == 0 || maximumDistance <= 0f ||
            !(directionIsNormalized
                ? CollisionRay.TryCreateNormalized(origin, direction, out CollisionRay ray)
                : CollisionRay.TryCreate(origin, direction, out ray)))
            return false;
        int rejected = 0;
        bool occluded = IsOccludedIterative(ray, maximumDistance, requiredFlags, receiverSource,
            receiverTriangleIndex, MathF.Max(0f, selfIntersectionDistance), ref rejected);
        rejectedSelfIntersections = rejected;
        return occluded;
    }

    private bool IsOccludedIterative(in CollisionRay ray, float maximumDistance,
        LevelCollisionFlags requiredFlags, ExportEntry receiverSource, int receiverTriangleIndex,
        float selfIntersectionDistance, ref int rejectedSelfIntersections)
    {
        // Recursive traversal cannot be inlined and used to make every BVH level a method call.
        // A 64-entry pending-node stack covers practical SAH tree depths; the recursive path below
        // remains as a correctness fallback for an unusually deep or pathological scene.
        Span<int> pendingNodes = stackalloc int[64];
        int pendingCount = 0;
        int nodeIndex = 0;
        ReadOnlySpan<BvhNode> bvhNodes = CollectionsMarshal.AsSpan(nodes);
        while (true)
        {
            ref readonly BvhNode node = ref bvhNodes[nodeIndex];
            if (node.Bounds.IntersectsRay(ray, maximumDistance))
            {
                if (node.IsLeaf)
                {
                    if (IsOccludedLeaf(node, ray, maximumDistance, requiredFlags, receiverSource,
                            receiverTriangleIndex, selfIntersectionDistance, ref rejectedSelfIntersections))
                        return true;
                }
                else
                {
                    int first = GetAxis(ray.Direction, node.SplitAxis) < 0f ? node.Right : node.Left;
                    int second = first == node.Left ? node.Right : node.Left;
                    if (pendingCount < pendingNodes.Length)
                    {
                        pendingNodes[pendingCount++] = second;
                        nodeIndex = first;
                        continue;
                    }
                    if (IsOccludedNode(first, ray, maximumDistance, requiredFlags, receiverSource,
                            receiverTriangleIndex, selfIntersectionDistance, ref rejectedSelfIntersections) ||
                        IsOccludedNode(second, ray, maximumDistance, requiredFlags, receiverSource,
                            receiverTriangleIndex, selfIntersectionDistance, ref rejectedSelfIntersections))
                        return true;
                }
            }

            if (pendingCount == 0)
                return false;
            nodeIndex = pendingNodes[--pendingCount];
        }
    }

    private bool IsOccludedNode(int nodeIndex, in CollisionRay ray, float maximumDistance,
        LevelCollisionFlags requiredFlags, ExportEntry receiverSource, int receiverTriangleIndex,
        float selfIntersectionDistance, ref int rejectedSelfIntersections)
    {
        BvhNode node = nodes[nodeIndex];
        if (!node.Bounds.IntersectsRay(ray, maximumDistance))
            return false;
        if (!node.IsLeaf)
        {
            // Construction places lower centroids in the left child. Directional ordering
            // reaches the likely blocker first without paying for two extra child AABB tests here.
            int first = GetAxis(ray.Direction, node.SplitAxis) < 0f ? node.Right : node.Left;
            int second = first == node.Left ? node.Right : node.Left;
            return IsOccludedNode(first, ray, maximumDistance, requiredFlags, receiverSource,
                       receiverTriangleIndex, selfIntersectionDistance, ref rejectedSelfIntersections) ||
                   IsOccludedNode(second, ray, maximumDistance, requiredFlags, receiverSource,
                       receiverTriangleIndex, selfIntersectionDistance, ref rejectedSelfIntersections);
        }

        return IsOccludedLeaf(node, ray, maximumDistance, requiredFlags, receiverSource,
            receiverTriangleIndex, selfIntersectionDistance, ref rejectedSelfIntersections);
    }

    private bool IsOccludedLeaf(in BvhNode node, in CollisionRay ray, float maximumDistance,
        LevelCollisionFlags requiredFlags, ExportEntry receiverSource, int receiverTriangleIndex,
        float selfIntersectionDistance, ref int rejectedSelfIntersections)
    {
        for (int index = node.Start; index < node.Start + node.Count; index++)
        {
            // This is the hottest loop in a full lighting bake. The triangle record contains many
            // vectors plus source metadata, so copying it for every leaf candidate is needlessly
            // expensive on miss-heavy shadow rays.
            ref readonly LevelCollisionTriangle triangle =
                ref CollectionsMarshal.AsSpan(triangles)[triangleIndices[index]];
            if ((triangle.Flags & requiredFlags) != requiredFlags ||
                !RayIntersectsTriangle(ray.Origin, ray.Direction, triangle, out float distance) ||
                distance > maximumDistance)
                continue;
            if (receiverSource is not null && ReferenceEquals(triangle.Source, receiverSource) &&
                (triangle.SourceTriangleIndex == receiverTriangleIndex ||
                 distance <= selfIntersectionDistance))
            {
                rejectedSelfIntersections++;
                continue;
            }
            return true;
        }
        return false;
    }

    private bool RaycastCore(Vector3 origin, Vector3 direction, float maximumDistance,
        out LevelCollisionHit hit, LevelCollisionFlags requiredFlags, ExportEntry receiverSource,
        int receiverTriangleIndex, float selfIntersectionDistance, out int rejectedSelfIntersections)
    {
        hit = default;
        rejectedSelfIntersections = 0;
        if (nodes.Count == 0 || maximumDistance <= 0f || direction.LengthSquared() < 0.000001f)
        {
            return false;
        }

        direction = Vector3.Normalize(direction);
        float closest = maximumDistance;
        bool found = false;
        int selfRejected = 0;
        LevelCollisionHit closestHit = default;
        VisitRay(0, origin, direction, maximumDistance, triangleIndex =>
        {
            LevelCollisionTriangle triangle = triangles[triangleIndex];
            if ((triangle.Flags & requiredFlags) != requiredFlags ||
                !RayIntersectsTriangle(origin, direction, triangle, out float distance) || distance > closest)
            {
                return;
            }

            if (receiverSource is not null && ReferenceEquals(triangle.Source, receiverSource) &&
                (triangle.SourceTriangleIndex == receiverTriangleIndex ||
                 distance <= selfIntersectionDistance))
            {
                selfRejected++;
                return;
            }

            closest = distance;
            Vector3 normal = Vector3.Dot(triangle.Normal, direction) > 0f ? -triangle.Normal : triangle.Normal;
            closestHit = new LevelCollisionHit(distance, origin + direction * distance, normal, triangle.Source,
                triangle.SourceTriangleIndex);
            found = true;
        });
        hit = closestHit;
        rejectedSelfIntersections = selfRejected;
        return found;
    }

    public IReadOnlyList<LevelCollisionHit> RaycastAll(Vector3 origin, Vector3 direction, float maximumDistance,
        LevelCollisionFlags requiredFlags = LevelCollisionFlags.BlocksRay)
    {
        if (nodes.Count == 0 || maximumDistance <= 0f || direction.LengthSquared() < 0.000001f)
        {
            return [];
        }

        direction = Vector3.Normalize(direction);
        var hits = new List<LevelCollisionHit>();
        VisitRay(0, origin, direction, maximumDistance, triangleIndex =>
        {
            LevelCollisionTriangle triangle = triangles[triangleIndex];
            if ((triangle.Flags & requiredFlags) != requiredFlags ||
                !RayIntersectsTriangle(origin, direction, triangle, out float distance) ||
                distance > maximumDistance)
            {
                return;
            }

            Vector3 normal = Vector3.Dot(triangle.Normal, direction) > 0f ? -triangle.Normal : triangle.Normal;
            hits.Add(new LevelCollisionHit(distance, origin + direction * distance, normal, triangle.Source,
                triangle.SourceTriangleIndex));
        });
        hits.Sort((left, right) => left.Distance.CompareTo(right.Distance));
        return hits;
    }

    public bool OverlapSphere(Vector3 center, float radius,
        LevelCollisionFlags requiredFlags = LevelCollisionFlags.BlocksShape)
    {
        float effectiveRadius = MathF.Max(0f, radius - ContactSkin);
        float radiusSquared = effectiveRadius * effectiveRadius;
        var bounds = new CollisionBounds(center - new Vector3(radius), center + new Vector3(radius));
        bool overlaps = false;
        VisitBounds(0, bounds, triangleIndex =>
        {
            LevelCollisionTriangle triangle = triangles[triangleIndex];
            if ((triangle.Flags & requiredFlags) == requiredFlags &&
                PointTriangleDistanceSquared(center, triangle.A, triangle.B, triangle.C) < radiusSquared)
            {
                overlaps = true;
            }
        }, () => overlaps);
        return overlaps;
    }

    /// <summary>Tests a vertical pawn capsule whose position is its floor contact point.</summary>
    public bool OverlapCapsule(Vector3 floorPosition, float radius, float height,
        LevelCollisionFlags requiredFlags = LevelCollisionFlags.BlocksShape)
    {
        GetCapsuleSegment(floorPosition, radius, height, out Vector3 bottom, out Vector3 top);
        float effectiveRadius = MathF.Max(0f, radius - ContactSkin);
        float radiusSquared = effectiveRadius * effectiveRadius;
        var bounds = new CollisionBounds(Vector3.Min(bottom, top) - new Vector3(radius),
            Vector3.Max(bottom, top) + new Vector3(radius));
        bool overlaps = false;
        VisitBounds(0, bounds, triangleIndex =>
        {
            LevelCollisionTriangle triangle = triangles[triangleIndex];
            if ((triangle.Flags & requiredFlags) == requiredFlags &&
                SegmentTriangleDistanceSquared(bottom, top, triangle.A, triangle.B, triangle.C) < radiusSquared)
            {
                overlaps = true;
            }
        }, () => overlaps);
        return overlaps;
    }

    public bool SphereCast(Vector3 start, Vector3 end, float radius, out LevelCollisionHit hit,
        LevelCollisionFlags requiredFlags = LevelCollisionFlags.BlocksShape)
    {
        Vector3 delta = end - start;
        float distance = delta.Length();
        int steps = Math.Max(1, (int)MathF.Ceiling(distance / MathF.Max(4f, radius * 0.5f)));
        for (int step = 0; step <= steps; step++)
        {
            float alpha = step / (float)steps;
            Vector3 center = start + delta * alpha;
            if (OverlapSphere(center, radius, requiredFlags))
            {
                Vector3 direction = distance > 0.0001f ? delta / distance : Vector3.UnitZ;
                hit = new LevelCollisionHit(distance * alpha, center, -direction, null);
                return true;
            }
        }
        hit = default;
        return false;
    }

    public bool CapsuleSweep(Vector3 startFloor, Vector3 endFloor, float radius, float height,
        out LevelCollisionHit hit, LevelCollisionFlags requiredFlags = LevelCollisionFlags.BlocksShape)
    {
        Vector3 delta = endFloor - startFloor;
        float distance = delta.Length();
        int steps = Math.Max(1, (int)MathF.Ceiling(distance / MathF.Max(4f, radius * 0.5f)));
        for (int step = 0; step <= steps; step++)
        {
            float alpha = step / (float)steps;
            Vector3 position = startFloor + delta * alpha;
            if (OverlapCapsule(position, radius, height, requiredFlags))
            {
                Vector3 direction = distance > 0.0001f ? delta / distance : Vector3.UnitZ;
                hit = new LevelCollisionHit(distance * alpha, position, -direction, null);
                return true;
            }
        }
        hit = default;
        return false;
    }

    /// <summary>
    /// Samples the horizontal span of every near-vertical cover triangle. These object-space seeds let
    /// cover generation test narrow and rotated mesh faces directly instead of depending on rays fired
    /// from a world-aligned floor grid.
    /// </summary>
    public IReadOnlyList<LevelCoverSurfaceSeed> GetCoverSurfaceSeeds(float spacing, Vector3 center,
        float generationRadius)
    {
        spacing = MathF.Max(8f, spacing);
        float keySpacing = MathF.Max(4f, spacing * 0.5f);
        float radiusSquared = generationRadius * generationRadius;
        var occupied = new HashSet<(int X, int Y, int Z, int Direction)>();
        var seeds = new List<LevelCoverSurfaceSeed>();
        foreach (LevelCollisionTriangle triangle in triangles)
        {
            if ((triangle.Flags & LevelCollisionFlags.CoverProbe) == 0 ||
                MathF.Abs(triangle.Normal.Z) > 0.35f)
                continue;

            Vector3 normal = new(triangle.Normal.X, triangle.Normal.Y, 0f);
            if (normal.LengthSquared() < 0.0001f)
                continue;
            normal = Vector3.Normalize(normal);
            if (normal.X < -0.0001f || MathF.Abs(normal.X) <= 0.0001f && normal.Y < 0f)
                normal = -normal;
            Vector3 tangent = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, normal));
            float first = Vector3.Dot(triangle.A, tangent);
            float second = Vector3.Dot(triangle.B, tangent);
            float third = Vector3.Dot(triangle.C, tangent);
            float minimum = MathF.Min(first, MathF.Min(second, third));
            float maximum = MathF.Max(first, MathF.Max(second, third));
            int sampleCount = Math.Max(1, (int)MathF.Ceiling((maximum - minimum) / spacing));
            float centroidProjection = Vector3.Dot(triangle.Centroid, tangent);
            int directionKey = (int)MathF.Round(MathF.Atan2(normal.Y, normal.X) * 8f / MathF.PI);
            for (int sampleIndex = 0; sampleIndex <= sampleCount; sampleIndex++)
            {
                float projection = sampleCount == 0 ? centroidProjection :
                    minimum + (maximum - minimum) * sampleIndex / sampleCount;
                Vector3 position = triangle.Centroid + tangent * (projection - centroidProjection);
                if (generationRadius > 0f &&
                    Vector2.DistanceSquared(new Vector2(position.X, position.Y),
                        new Vector2(center.X, center.Y)) > radiusSquared)
                    continue;

                var key = ((int)MathF.Round(position.X / keySpacing),
                    (int)MathF.Round(position.Y / keySpacing),
                    (int)MathF.Round(position.Z / MathF.Max(64f, spacing)), directionKey);
                if (occupied.Add(key))
                    seeds.Add(new LevelCoverSurfaceSeed(position, normal, triangle.Source));
            }
        }
        return seeds;
    }

    private int BuildNode(int start, int count)
    {
        CollisionBounds bounds = CollisionBounds.Empty;
        CollisionBounds centroidBounds = CollisionBounds.Empty;
        for (int index = start; index < start + count; index++)
        {
            LevelCollisionTriangle triangle = triangles[triangleIndices[index]];
            bounds = bounds.Include(triangle.Bounds);
            var centroid = new CollisionBounds(triangle.Centroid, triangle.Centroid);
            centroidBounds = centroidBounds.Include(centroid);
        }

        int nodeIndex = nodes.Count;
        nodes.Add(default);
        if (count <= LeafTriangleCount)
        {
            nodes[nodeIndex] = new BvhNode(bounds, -1, -1, start, count, -1);
            return nodeIndex;
        }

        Vector3 size = centroidBounds.Size;
        int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        int leftCount;
        if (!TryPartitionBySurfaceArea(start, count, centroidBounds, out axis, out leftCount))
        {
            leftCount = count / 2;
            SelectMedian(start, start + count - 1, start + leftCount, axis);
        }
        int leftNode = BuildNode(start, leftCount);
        int rightNode = BuildNode(start + leftCount, count - leftCount);
        nodes[nodeIndex] = new BvhNode(bounds, leftNode, rightNode, 0, 0, axis);
        return nodeIndex;
    }

    /// <summary>
    /// Binned surface-area splitting costs slightly more while constructing the tree, but produces
    /// substantially less child overlap than a longest-axis median split in mixed architectural
    /// scenes. Shadow rays which miss all geometry benefit most because they otherwise have to walk
    /// every overlapping branch before proving that no blocker exists.
    /// </summary>
    private bool TryPartitionBySurfaceArea(int start, int count, CollisionBounds centroidBounds,
        out int splitAxis, out int leftCount)
    {
        splitAxis = -1;
        leftCount = 0;
        int bestSplitBin = -1;
        float bestCost = float.PositiveInfinity;
        Vector3 centroidSize = centroidBounds.Size;

        Span<BvhBuildBin> bins = stackalloc BvhBuildBin[BvhBuildBinCount];
        Span<CollisionBounds> prefixBounds = stackalloc CollisionBounds[BvhBuildBinCount];
        Span<CollisionBounds> suffixBounds = stackalloc CollisionBounds[BvhBuildBinCount];
        Span<int> prefixCounts = stackalloc int[BvhBuildBinCount];
        Span<int> suffixCounts = stackalloc int[BvhBuildBinCount];
        for (int candidateAxis = 0; candidateAxis < 3; candidateAxis++)
        {
            float extent = GetAxis(centroidSize, candidateAxis);
            if (!(extent > 0.000001f) || !float.IsFinite(extent))
                continue;

            for (int binIndex = 0; binIndex < BvhBuildBinCount; binIndex++)
            {
                bins[binIndex].Bounds = CollisionBounds.Empty;
                bins[binIndex].Count = 0;
            }
            float minimum = GetAxis(centroidBounds.Minimum, candidateAxis);
            float scale = BvhBuildBinCount / extent;
            for (int index = start; index < start + count; index++)
            {
                LevelCollisionTriangle triangle = triangles[triangleIndices[index]];
                int binIndex = Math.Clamp((int)((GetAxis(triangle.Centroid, candidateAxis) - minimum) * scale),
                    0, BvhBuildBinCount - 1);
                ref BvhBuildBin bin = ref bins[binIndex];
                bin.Bounds = bin.Count == 0 ? triangle.Bounds : bin.Bounds.Include(triangle.Bounds);
                bin.Count++;
            }

            CollisionBounds runningBounds = CollisionBounds.Empty;
            int runningCount = 0;
            for (int binIndex = 0; binIndex < BvhBuildBinCount; binIndex++)
            {
                if (bins[binIndex].Count > 0)
                    runningBounds = runningCount == 0 ? bins[binIndex].Bounds : runningBounds.Include(bins[binIndex].Bounds);
                runningCount += bins[binIndex].Count;
                prefixBounds[binIndex] = runningBounds;
                prefixCounts[binIndex] = runningCount;
            }
            runningBounds = CollisionBounds.Empty;
            runningCount = 0;
            for (int binIndex = BvhBuildBinCount - 1; binIndex >= 0; binIndex--)
            {
                if (bins[binIndex].Count > 0)
                    runningBounds = runningCount == 0 ? bins[binIndex].Bounds : runningBounds.Include(bins[binIndex].Bounds);
                runningCount += bins[binIndex].Count;
                suffixBounds[binIndex] = runningBounds;
                suffixCounts[binIndex] = runningCount;
            }

            for (int binIndex = 0; binIndex < BvhBuildBinCount - 1; binIndex++)
            {
                int candidateLeftCount = prefixCounts[binIndex];
                int candidateRightCount = suffixCounts[binIndex + 1];
                if (candidateLeftCount == 0 || candidateRightCount == 0)
                    continue;
                float cost = SurfaceArea(prefixBounds[binIndex]) * candidateLeftCount +
                             SurfaceArea(suffixBounds[binIndex + 1]) * candidateRightCount;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    splitAxis = candidateAxis;
                    bestSplitBin = binIndex;
                }
            }
        }

        if (splitAxis < 0)
            return false;

        float splitMinimum = GetAxis(centroidBounds.Minimum, splitAxis);
        float splitExtent = GetAxis(centroidSize, splitAxis);
        float splitScale = BvhBuildBinCount / splitExtent;
        int lower = start;
        int upper = start + count - 1;
        while (lower <= upper)
        {
            while (lower <= upper && GetBuildBin(triangleIndices[lower], splitAxis, splitMinimum, splitScale) <= bestSplitBin)
                lower++;
            while (lower <= upper && GetBuildBin(triangleIndices[upper], splitAxis, splitMinimum, splitScale) > bestSplitBin)
                upper--;
            if (lower > upper)
                break;
            (triangleIndices[lower], triangleIndices[upper]) = (triangleIndices[upper], triangleIndices[lower]);
            lower++;
            upper--;
        }
        leftCount = lower - start;
        return leftCount > 0 && leftCount < count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetBuildBin(int triangleIndex, int axis, float minimum, float scale) =>
        Math.Clamp((int)((GetAxis(triangles[triangleIndex].Centroid, axis) - minimum) * scale),
            0, BvhBuildBinCount - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SurfaceArea(CollisionBounds bounds)
    {
        Vector3 size = Vector3.Max(Vector3.Zero, bounds.Size);
        return 2f * (size.X * size.Y + size.X * size.Z + size.Y * size.Z);
    }

    private void SelectMedian(int left, int right, int target, int axis)
    {
        while (left < right)
        {
            int middle = left + (right - left) / 2;
            int pivot = MedianTriangleIndex(triangleIndices[left], triangleIndices[middle],
                triangleIndices[right], axis);
            int lower = left;
            int upper = right;
            while (lower <= upper)
            {
                while (CompareTriangleCentroids(triangleIndices[lower], pivot, axis) < 0) lower++;
                while (CompareTriangleCentroids(triangleIndices[upper], pivot, axis) > 0) upper--;
                if (lower > upper) break;
                (triangleIndices[lower], triangleIndices[upper]) =
                    (triangleIndices[upper], triangleIndices[lower]);
                lower++;
                upper--;
            }
            if (target <= upper) right = upper;
            else if (target >= lower) left = lower;
            else return;
        }
    }

    private int MedianTriangleIndex(int first, int second, int third, int axis)
    {
        if (CompareTriangleCentroids(first, second, axis) > 0) (first, second) = (second, first);
        if (CompareTriangleCentroids(second, third, axis) > 0) (second, third) = (third, second);
        if (CompareTriangleCentroids(first, second, axis) > 0) (first, second) = (second, first);
        return second;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CompareTriangleCentroids(int left, int right, int axis)
    {
        int comparison = GetAxis(triangles[left].Centroid, axis)
            .CompareTo(GetAxis(triangles[right].Centroid, axis));
        return comparison != 0 ? comparison : left.CompareTo(right);
    }

    private void VisitRay(int nodeIndex, Vector3 origin, Vector3 direction, float maximumDistance,
        Action<int> visitor)
    {
        BvhNode node = nodes[nodeIndex];
        if (!node.Bounds.IntersectsRay(origin, direction, maximumDistance))
        {
            return;
        }
        if (node.IsLeaf)
        {
            for (int index = node.Start; index < node.Start + node.Count; index++)
            {
                visitor(triangleIndices[index]);
            }
            return;
        }
        VisitRay(node.Left, origin, direction, maximumDistance, visitor);
        VisitRay(node.Right, origin, direction, maximumDistance, visitor);
    }

    private void VisitBounds(int nodeIndex, CollisionBounds bounds, Action<int> visitor, Func<bool> stop)
    {
        if (nodes.Count == 0 || stop())
        {
            return;
        }
        BvhNode node = nodes[nodeIndex];
        if (!node.Bounds.Intersects(bounds))
        {
            return;
        }
        if (node.IsLeaf)
        {
            for (int index = node.Start; index < node.Start + node.Count && !stop(); index++)
            {
                visitor(triangleIndices[index]);
            }
            return;
        }
        VisitBounds(node.Left, bounds, visitor, stop);
        VisitBounds(node.Right, bounds, visitor, stop);
    }

    private static bool RayIntersectsTriangle(Vector3 origin, Vector3 direction,
        in LevelCollisionTriangle triangle, out float distance)
    {
        Vector3 cross = Vector3.Cross(direction, triangle.Edge2);
        float determinant = Vector3.Dot(triangle.Edge1, cross);
        if (MathF.Abs(determinant) < 0.000001f)
        {
            distance = 0f;
            return false;
        }
        float inverse = 1f / determinant;
        Vector3 originDelta = origin - triangle.A;
        float u = Vector3.Dot(originDelta, cross) * inverse;
        if (u < 0f || u > 1f)
        {
            distance = 0f;
            return false;
        }
        Vector3 secondCross = Vector3.Cross(originDelta, triangle.Edge1);
        float v = Vector3.Dot(direction, secondCross) * inverse;
        if (v < 0f || u + v > 1f)
        {
            distance = 0f;
            return false;
        }
        distance = Vector3.Dot(triangle.Edge2, secondCross) * inverse;
        return distance >= 0f;
    }

    private static void GetCapsuleSegment(Vector3 floorPosition, float radius, float height,
        out Vector3 bottom, out Vector3 top)
    {
        height = MathF.Max(height, radius * 2f);
        bottom = floorPosition + Vector3.UnitZ * radius;
        top = floorPosition + Vector3.UnitZ * (height - radius);
    }

    private static float PointTriangleDistanceSquared(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a;
        Vector3 ac = c - a;
        Vector3 ap = point - a;
        float d1 = Vector3.Dot(ab, ap);
        float d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return Vector3.DistanceSquared(point, a);

        Vector3 bp = point - b;
        float d3 = Vector3.Dot(ab, bp);
        float d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return Vector3.DistanceSquared(point, b);

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            float v = d1 / (d1 - d3);
            return Vector3.DistanceSquared(point, a + v * ab);
        }

        Vector3 cp = point - c;
        float d5 = Vector3.Dot(ab, cp);
        float d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return Vector3.DistanceSquared(point, c);

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            float w = d2 / (d2 - d6);
            return Vector3.DistanceSquared(point, a + w * ac);
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
        {
            float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            return Vector3.DistanceSquared(point, b + w * (c - b));
        }

        float denominator = 1f / (va + vb + vc);
        float faceV = vb * denominator;
        float faceW = vc * denominator;
        Vector3 closest = a + ab * faceV + ac * faceW;
        return Vector3.DistanceSquared(point, closest);
    }

    private static float SegmentTriangleDistanceSquared(Vector3 start, Vector3 end,
        Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 delta = end - start;
        float length = delta.Length();
        if (length > 0.000001f)
        {
            Vector3 edge1 = b - a;
            Vector3 edge2 = c - a;
            var temporary = new LevelCollisionTriangle(a, b, c, edge1, edge2, (a + b + c) / 3f,
                Vector3.Zero, CollisionBounds.FromTriangle(a, b, c), LevelCollisionFlags.Navigation, null);
            if (RayIntersectsTriangle(start, delta / length, temporary, out float distance) && distance <= length)
            {
                return 0f;
            }
        }

        float result = MathF.Min(PointTriangleDistanceSquared(start, a, b, c),
            PointTriangleDistanceSquared(end, a, b, c));
        result = MathF.Min(result, SegmentSegmentDistanceSquared(start, end, a, b));
        result = MathF.Min(result, SegmentSegmentDistanceSquared(start, end, b, c));
        return MathF.Min(result, SegmentSegmentDistanceSquared(start, end, c, a));
    }

    private static float SegmentSegmentDistanceSquared(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        Vector3 d1 = q1 - p1;
        Vector3 d2 = q2 - p2;
        Vector3 r = p1 - p2;
        float a = Vector3.Dot(d1, d1);
        float e = Vector3.Dot(d2, d2);
        float f = Vector3.Dot(d2, r);
        float s;
        float t;
        if (a <= 0.000001f && e <= 0.000001f) return Vector3.DistanceSquared(p1, p2);
        if (a <= 0.000001f)
        {
            s = 0f;
            t = Math.Clamp(f / e, 0f, 1f);
        }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= 0.000001f)
            {
                t = 0f;
                s = Math.Clamp(-c / a, 0f, 1f);
            }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denominator = a * e - b * b;
                s = denominator == 0f ? 0f : Math.Clamp((b * f - c * e) / denominator, 0f, 1f);
                t = (b * s + f) / e;
                if (t < 0f)
                {
                    t = 0f;
                    s = Math.Clamp(-c / a, 0f, 1f);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Math.Clamp((b - c) / a, 0f, 1f);
                }
            }
        }
        Vector3 c1 = p1 + d1 * s;
        Vector3 c2 = p2 + d2 * t;
        return Vector3.DistanceSquared(c1, c2);
    }

    private static float GetAxis(Vector3 vector, int axis) => axis switch
    {
        0 => vector.X,
        1 => vector.Y,
        _ => vector.Z
    };
}
