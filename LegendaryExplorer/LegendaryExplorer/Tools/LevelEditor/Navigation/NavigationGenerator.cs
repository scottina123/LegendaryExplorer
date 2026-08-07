using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace LegendaryExplorer.Tools.LevelEditor;

public sealed record NavigationGenerationSettings
{
    public float PawnRadius { get; init; } = 34f;
    /// <summary>UE3 cylinder half-height, matching ReachSpec CollisionHeight.</summary>
    public float PawnHeight { get; init; } = 90f;
    public float GridSpacing { get; init; } = 256f;
    public float MaximumSlopeDegrees { get; init; } = 45f;
    public float MaximumStepUp { get; init; } = 35f;
    public float MaximumStepDown { get; init; } = 45f;
    public float MaximumSafeDrop { get; init; } = 160f;
    public float ConnectionDistance { get; init; } = 400f;
    public float GenerationRadius { get; init; } = 10000f;
    public float CoverProbeDistance { get; init; } = 192f;
    public float CoverSlotInterval { get; init; } = 160f;
    public bool GenerateCover { get; init; } = true;

    public void Validate()
    {
        if (PawnRadius < 4f || PawnHeight < PawnRadius)
            throw new ArgumentOutOfRangeException(nameof(PawnHeight), "Pawn half-height must be at least its radius.");
        if (GridSpacing < PawnRadius * 2f)
            throw new ArgumentOutOfRangeException(nameof(GridSpacing), "Grid spacing must be at least the pawn diameter.");
        if (MaximumSlopeDegrees is <= 0f or >= 89f)
            throw new ArgumentOutOfRangeException(nameof(MaximumSlopeDegrees));
        if (MaximumStepUp < 0f || MaximumStepDown < 0f || MaximumSafeDrop < MaximumStepDown)
            throw new ArgumentOutOfRangeException(nameof(MaximumSafeDrop));
        if (ConnectionDistance < GridSpacing)
            throw new ArgumentOutOfRangeException(nameof(ConnectionDistance), "Connection distance must be at least the grid spacing.");
    }
}

public sealed record GeneratedNavigationNode(Vector3 Position, Vector3 FloorNormal);
public sealed record GeneratedNavigationEdge(int StartNode, int EndNode);
public sealed record GeneratedCoverSlot(Vector3 Position, Vector3 Facing, bool IsStanding,
    bool LeanLeft, bool LeanRight, int NearestNavigationNode,
    int MantleTargetLink = -1, int MantleTargetSlot = -1);
public sealed record GeneratedCoverLink(Vector3 Position, Vector3 Facing, IReadOnlyList<GeneratedCoverSlot> Slots);

public sealed record NavigationGenerationResult(
    IReadOnlyList<GeneratedNavigationNode> Nodes,
    IReadOnlyList<GeneratedNavigationEdge> Edges,
    IReadOnlyList<GeneratedCoverLink> CoverLinks,
    int SampleCount,
    int RejectedForSlope,
    int RejectedForClearance);

/// <summary>
/// Builds a sparse UE3-style navigation graph from Level Editor collision. It performs multi-floor
/// projection, pawn-capsule clearance tests, directional step/drop validation, local topology pruning,
/// and wall-probe cover classification.
/// </summary>
public sealed class NavigationGenerator
{
    private const int MaximumCandidateCount = 20000;
    private const int MaximumCoverSampleCount = 100000;
    private readonly LevelCollisionScene collision;
    private readonly NavigationGenerationSettings settings;
    private readonly float minimumWalkableNormalZ;
    private float PawnFullHeight => settings.PawnHeight * 2f;

    private sealed record Candidate(Vector3 Position, Vector3 Normal, int GridX, int GridY);
    private sealed record CoverCandidate(Vector3 Position, Vector3 Facing, bool IsStanding);

    public NavigationGenerator(LevelCollisionScene collision, NavigationGenerationSettings settings)
    {
        this.collision = collision;
        this.settings = settings;
        settings.Validate();
        minimumWalkableNormalZ = MathF.Cos(settings.MaximumSlopeDegrees * MathF.PI / 180f);
    }

    public NavigationGenerationResult Generate(Vector3 center, CancellationToken cancellationToken = default,
        IProgress<string> progress = null)
    {
        if (collision.NavigationTriangleCount == 0)
            throw new InvalidOperationException("The loaded levels do not contain usable blocking mesh collision.");

        progress?.Report("Detecting walkable surfaces...");
        List<Candidate> candidates = SampleWalkableSurfaces(center, cancellationToken,
            out int sampleCount, out int rejectedForSlope, out int rejectedForClearance);
        if (candidates.Count == 0)
            throw new InvalidOperationException("No walkable surfaces passed the configured slope and pawn-clearance tests.");

        progress?.Report($"Testing connectivity for {candidates.Count:N0} walkable samples...");
        List<GeneratedNavigationEdge> edges = BuildConnectivity(candidates, cancellationToken);

        List<CoverCandidate> coverCandidates = [];
        if (settings.GenerateCover && collision.CoverTriangleCount > 0)
        {
            progress?.Report("Sampling walkable approaches around cover...");
            List<Candidate> coverFloors = SampleCoverSurfaces(center, candidates, cancellationToken);
            progress?.Report($"Detecting cover surfaces from {coverFloors.Count:N0} approach samples...");
            coverCandidates = DetectCover(coverFloors, cancellationToken);
            progress?.Report("Probing vertical mesh faces directly...");
            DetectCoverFromMeshSurfaces(coverCandidates, center, cancellationToken);
        }

        progress?.Report("Pruning redundant navigation points...");
        Prune(candidates, edges, coverCandidates, cancellationToken);
        Reindex(candidates, edges, out List<GeneratedNavigationNode> generatedNodes,
            out List<GeneratedNavigationEdge> generatedEdges);

        progress?.Report("Grouping cover slots...");
        List<GeneratedCoverLink> coverLinks = GroupCover(coverCandidates, generatedNodes);
        progress?.Report("Pairing vaultable cover slots...");
        AssignMantleTargets(coverLinks);
        return new NavigationGenerationResult(generatedNodes, generatedEdges, coverLinks,
            sampleCount, rejectedForSlope, rejectedForClearance);
    }

    private List<Candidate> SampleWalkableSurfaces(Vector3 center, CancellationToken cancellationToken,
        out int sampleCount, out int rejectedForSlope, out int rejectedForClearance)
    {
        sampleCount = 0;
        rejectedForSlope = 0;
        rejectedForClearance = 0;
        Vector3 minimum = collision.Minimum;
        Vector3 maximum = collision.Maximum;
        if (settings.GenerationRadius > 0f)
        {
            minimum.X = MathF.Max(minimum.X, center.X - settings.GenerationRadius);
            minimum.Y = MathF.Max(minimum.Y, center.Y - settings.GenerationRadius);
            maximum.X = MathF.Min(maximum.X, center.X + settings.GenerationRadius);
            maximum.Y = MathF.Min(maximum.Y, center.Y + settings.GenerationRadius);
        }

        float startX = MathF.Floor(minimum.X / settings.GridSpacing) * settings.GridSpacing;
        float startY = MathF.Floor(minimum.Y / settings.GridSpacing) * settings.GridSpacing;
        float rayStartZ = maximum.Z + PawnFullHeight + settings.MaximumStepUp + 10f;
        float rayDistance = rayStartZ - minimum.Z + PawnFullHeight + settings.MaximumSafeDrop;
        var candidates = new List<Candidate>();
        int gridX = 0;
        for (float x = startX; x <= maximum.X; x += settings.GridSpacing, gridX++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int gridY = 0;
            for (float y = startY; y <= maximum.Y; y += settings.GridSpacing, gridY++)
            {
                if (settings.GenerationRadius > 0f &&
                    Vector2.DistanceSquared(new Vector2(x, y), new Vector2(center.X, center.Y)) >
                    settings.GenerationRadius * settings.GenerationRadius)
                {
                    continue;
                }

                IReadOnlyList<LevelCollisionHit> hits = collision.RaycastAll(
                    new Vector3(x, y, rayStartZ), -Vector3.UnitZ, rayDistance);
                float previousAcceptedZ = float.PositiveInfinity;
                foreach (LevelCollisionHit hit in hits)
                {
                    sampleCount++;
                    if (previousAcceptedZ - hit.Position.Z < MathF.Max(4f, settings.MaximumStepUp * 0.5f))
                        continue;
                    if (hit.Normal.Z < minimumWalkableNormalZ)
                    {
                        rejectedForSlope++;
                        continue;
                    }

                    Vector3 position = hit.Position + Vector3.UnitZ;
                    if (collision.OverlapCapsule(GetClearancePosition(position, hit.Normal),
                            settings.PawnRadius, PawnFullHeight))
                    {
                        rejectedForClearance++;
                        continue;
                    }

                    candidates.Add(new Candidate(position, hit.Normal, gridX, gridY));
                    previousAcceptedZ = hit.Position.Z;
                    if (candidates.Count > MaximumCandidateCount)
                    {
                        throw new InvalidOperationException(
                            $"Generation exceeded {MaximumCandidateCount:N0} candidates. Increase grid spacing or reduce generation radius.");
                    }
                }
            }
        }
        return candidates;
    }

    private List<Candidate> SampleCoverSurfaces(Vector3 center, List<Candidate> navigationCandidates,
        CancellationToken cancellationToken)
    {
        float spacing = MathF.Min(settings.GridSpacing,
            MathF.Max(settings.PawnRadius * 2f, settings.CoverSlotInterval * 0.75f));
        if (spacing >= settings.GridSpacing - 0.01f)
            return navigationCandidates;

        Vector3 minimum = collision.Minimum;
        Vector3 maximum = collision.Maximum;
        if (settings.GenerationRadius > 0f)
        {
            minimum.X = MathF.Max(minimum.X, center.X - settings.GenerationRadius);
            minimum.Y = MathF.Max(minimum.Y, center.Y - settings.GenerationRadius);
            maximum.X = MathF.Min(maximum.X, center.X + settings.GenerationRadius);
            maximum.Y = MathF.Min(maximum.Y, center.Y + settings.GenerationRadius);
        }

        float startX = MathF.Floor(minimum.X / spacing) * spacing;
        float startY = MathF.Floor(minimum.Y / spacing) * spacing;
        float rayStartZ = maximum.Z + PawnFullHeight + settings.MaximumStepUp + 10f;
        float rayDistance = rayStartZ - minimum.Z + PawnFullHeight + settings.MaximumSafeDrop;
        var samples = new List<Candidate>();
        int gridX = 0;
        for (float x = startX; x <= maximum.X; x += spacing, gridX++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int gridY = 0;
            for (float y = startY; y <= maximum.Y; y += spacing, gridY++)
            {
                if (settings.GenerationRadius > 0f &&
                    Vector2.DistanceSquared(new Vector2(x, y), new Vector2(center.X, center.Y)) >
                    settings.GenerationRadius * settings.GenerationRadius)
                    continue;

                float previousAcceptedZ = float.PositiveInfinity;
                foreach (LevelCollisionHit hit in collision.RaycastAll(
                             new Vector3(x, y, rayStartZ), -Vector3.UnitZ, rayDistance))
                {
                    if (previousAcceptedZ - hit.Position.Z < MathF.Max(4f, settings.MaximumStepUp * 0.5f) ||
                        hit.Normal.Z < minimumWalkableNormalZ)
                        continue;

                    Vector3 position = hit.Position + Vector3.UnitZ;
                    if (collision.OverlapCapsule(GetClearancePosition(position, hit.Normal),
                            settings.PawnRadius, PawnFullHeight))
                        continue;

                    samples.Add(new Candidate(position, hit.Normal, gridX, gridY));
                    previousAcceptedZ = hit.Position.Z;
                    if (samples.Count > MaximumCoverSampleCount)
                    {
                        throw new InvalidOperationException(
                            $"Cover generation exceeded {MaximumCoverSampleCount:N0} approach samples. " +
                            "Increase cover slot interval or reduce generation radius.");
                    }
                }
            }
        }
        return samples;
    }

    private List<GeneratedNavigationEdge> BuildConnectivity(List<Candidate> candidates,
        CancellationToken cancellationToken)
    {
        var buckets = candidates.Select((candidate, index) => (candidate, index))
            .GroupBy(item => (item.candidate.GridX, item.candidate.GridY))
            .ToDictionary(group => group.Key, group => group.Select(item => item.index).ToArray());
        var edges = new List<GeneratedNavigationEdge>();
        var edgeSet = new HashSet<(int, int)>();
        float maximumDistanceSquared = settings.ConnectionDistance * settings.ConnectionDistance;
        int bucketRange = Math.Max(1, (int)MathF.Ceiling(settings.ConnectionDistance / settings.GridSpacing));
        for (int index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Candidate start = candidates[index];
            for (int offsetX = -bucketRange; offsetX <= bucketRange; offsetX++)
            {
                for (int offsetY = -bucketRange; offsetY <= bucketRange; offsetY++)
                {
                    if (offsetX == 0 && offsetY == 0 ||
                        !buckets.TryGetValue((start.GridX + offsetX, start.GridY + offsetY), out int[] neighbors))
                        continue;
                    foreach (int neighborIndex in neighbors)
                    {
                        if (neighborIndex == index || Vector3.DistanceSquared(start.Position,
                                candidates[neighborIndex].Position) > maximumDistanceSquared)
                            continue;
                        if (edgeSet.Add((index, neighborIndex)) && CanTraverse(start.Position,
                                candidates[neighborIndex].Position))
                        {
                            edges.Add(new GeneratedNavigationEdge(index, neighborIndex));
                        }
                    }
                }
            }
        }
        return edges;
    }

    private bool CanTraverse(Vector3 start, Vector3 end)
    {
        float heightDifference = end.Z - start.Z;
        if (heightDifference > settings.MaximumStepUp || heightDifference < -settings.MaximumSafeDrop)
            return false;
        float sweepLift = MathF.Max(settings.MaximumStepUp,
            settings.PawnRadius / minimumWalkableNormalZ - settings.PawnRadius + 1f);
        Vector3 sweepOffset = Vector3.UnitZ * sweepLift;
        if (heightDifference < -settings.MaximumStepDown)
        {
            // A safe drop is directional: clear the ledge horizontally at the upper elevation, then
            // sweep down to the landing. A diagonal sweep would incorrectly collide with the ledge lip.
            Vector3 overLanding = new(end.X, end.Y, start.Z);
            if (collision.CapsuleSweep(start + sweepOffset, overLanding + sweepOffset,
                    settings.PawnRadius, PawnFullHeight, out _) ||
                collision.CapsuleSweep(overLanding + sweepOffset, end + sweepOffset,
                    settings.PawnRadius, PawnFullHeight, out _))
                return false;
        }
        else if (collision.CapsuleSweep(start + sweepOffset, end + sweepOffset,
                     settings.PawnRadius, PawnFullHeight, out _))
        {
            return false;
        }

        Vector3 delta = end - start;
        float horizontalDistance = new Vector2(delta.X, delta.Y).Length();
        int steps = Math.Max(1, (int)MathF.Ceiling(horizontalDistance /
            MathF.Max(settings.PawnRadius, settings.GridSpacing * 0.25f)));
        Vector3 previous = start;
        bool usedSafeDrop = false;
        for (int step = 1; step <= steps; step++)
        {
            float alpha = step / (float)steps;
            Vector3 expected = start + delta * alpha;
            if (!TryFindFloorNear(expected, out LevelCollisionHit floor))
                return false;
            Vector3 current = floor.Position + Vector3.UnitZ;
            float stepHeight = current.Z - previous.Z;
            if (stepHeight > settings.MaximumStepUp ||
                (stepHeight < -settings.MaximumStepDown &&
                 (usedSafeDrop || stepHeight < -settings.MaximumSafeDrop)) ||
                collision.OverlapCapsule(GetClearancePosition(current, floor.Normal),
                    settings.PawnRadius, PawnFullHeight))
                return false;
            if (stepHeight < -settings.MaximumStepDown)
                usedSafeDrop = true;
            previous = current;
        }
        return true;
    }

    private bool TryFindFloorNear(Vector3 expected, out LevelCollisionHit floor)
    {
        Vector3 origin = expected + Vector3.UnitZ * settings.MaximumStepUp;
        float distance = settings.MaximumStepUp + settings.MaximumSafeDrop;
        foreach (LevelCollisionHit hit in collision.RaycastAll(origin, -Vector3.UnitZ, distance))
        {
            if (hit.Normal.Z >= minimumWalkableNormalZ)
            {
                floor = hit;
                return true;
            }
        }
        floor = default;
        return false;
    }

    private List<CoverCandidate> DetectCover(List<Candidate> candidates, CancellationToken cancellationToken)
    {
        var coverCandidates = new List<CoverCandidate>();
        float probeDistance = MathF.Max(settings.CoverProbeDistance, settings.PawnRadius * 2f);
        float middleHeight = MathF.Min(PawnFullHeight - settings.PawnRadius, 72f);
        float standingHeight = MathF.Min(PawnFullHeight - 5f, 128f);
        for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Vector3 floor = candidates[candidateIndex].Position;
            for (int directionIndex = 0; directionIndex < 8; directionIndex++)
            {
                float angle = directionIndex * MathF.PI / 4f;
                Vector3 direction = new(MathF.Cos(angle), MathF.Sin(angle), 0f);
                Vector3 origin = floor + Vector3.UnitZ * middleHeight;
                if (!collision.Raycast(origin, direction, probeDistance, out LevelCollisionHit middleHit,
                        LevelCollisionFlags.CoverProbe) ||
                    MathF.Abs(middleHit.Normal.Z) > 0.35f)
                    continue;

                Vector3 wallNormal = Vector3.Normalize(new Vector3(middleHit.Normal.X, middleHit.Normal.Y, 0f));
                Vector3 slotPosition = middleHit.Position + wallNormal * (settings.PawnRadius + 4f);
                slotPosition.Z = floor.Z;
                if (collision.OverlapCapsule(GetClearancePosition(slotPosition,
                        candidates[candidateIndex].Normal), settings.PawnRadius, PawnFullHeight))
                    continue;

                bool standing = collision.Raycast(slotPosition + Vector3.UnitZ * standingHeight,
                    -wallNormal, settings.PawnRadius + 12f, out LevelCollisionHit standingHit,
                    LevelCollisionFlags.CoverProbe) &&
                    MathF.Abs(standingHit.Normal.Z) <= 0.35f;
                var cover = new CoverCandidate(slotPosition, -wallNormal, standing);
                TryAddCoverCandidate(coverCandidates, cover);
            }
        }
        return coverCandidates;
    }

    private void DetectCoverFromMeshSurfaces(List<CoverCandidate> coverCandidates, Vector3 center,
        CancellationToken cancellationToken)
    {
        float seedSpacing = MathF.Max(settings.PawnRadius * 2f, settings.CoverSlotInterval * 0.5f);
        IReadOnlyList<LevelCoverSurfaceSeed> seeds = collision.GetCoverSurfaceSeeds(seedSpacing, center,
            settings.GenerationRadius);
        float middleHeight = MathF.Min(PawnFullHeight - settings.PawnRadius, 72f);
        float standingHeight = MathF.Min(PawnFullHeight - 5f, 128f);
        float approachOffset = settings.PawnRadius + 4f;
        foreach (LevelCoverSurfaceSeed seed in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 outward = seed.Normal * side;
                if (!TryFindCoverFloor(seed.Position + outward * approachOffset,
                        out LevelCollisionHit floorHit))
                    continue;

                Vector3 floor = floorHit.Position + Vector3.UnitZ;
                Vector3 origin = floor + Vector3.UnitZ * middleHeight;
                if (!collision.Raycast(origin, -outward, settings.CoverProbeDistance,
                        out LevelCollisionHit middleHit, LevelCollisionFlags.CoverProbe) ||
                    MathF.Abs(middleHit.Normal.Z) > 0.35f ||
                    Vector3.Dot(middleHit.Normal, outward) < 0.5f)
                    continue;

                Vector3 wallNormal = Vector3.Normalize(new Vector3(
                    middleHit.Normal.X, middleHit.Normal.Y, 0f));
                Vector3 slotPosition = middleHit.Position + wallNormal * approachOffset;
                slotPosition.Z = floor.Z;
                if (collision.OverlapCapsule(GetClearancePosition(slotPosition, floorHit.Normal),
                        settings.PawnRadius, PawnFullHeight))
                    continue;

                bool standing = collision.Raycast(slotPosition + Vector3.UnitZ * standingHeight,
                    -wallNormal, settings.PawnRadius + 12f, out LevelCollisionHit standingHit,
                    LevelCollisionFlags.CoverProbe) && MathF.Abs(standingHit.Normal.Z) <= 0.35f;
                TryAddCoverCandidate(coverCandidates,
                    new CoverCandidate(slotPosition, -wallNormal, standing));
            }
        }
    }

    private bool TryFindCoverFloor(Vector3 nearSurface, out LevelCollisionHit floor)
    {
        float probeAbove = PawnFullHeight + settings.MaximumStepUp;
        float probeDistance = PawnFullHeight * 2f + settings.MaximumSafeDrop + settings.MaximumStepUp;
        Vector3 origin = new(nearSurface.X, nearSurface.Y, nearSurface.Z + probeAbove);
        foreach (LevelCollisionHit hit in collision.RaycastAll(origin, -Vector3.UnitZ, probeDistance))
        {
            if (hit.Normal.Z < minimumWalkableNormalZ)
                continue;
            Vector3 position = hit.Position + Vector3.UnitZ;
            if (!collision.OverlapCapsule(GetClearancePosition(position, hit.Normal),
                    settings.PawnRadius, PawnFullHeight))
            {
                floor = hit;
                return true;
            }
        }
        floor = default;
        return false;
    }

    private void TryAddCoverCandidate(List<CoverCandidate> coverCandidates, CoverCandidate cover)
    {
        if (!coverCandidates.Any(existing =>
                Vector3.DistanceSquared(existing.Position, cover.Position) <
                settings.CoverSlotInterval * settings.CoverSlotInterval * 0.2f &&
                Vector3.Dot(existing.Facing, cover.Facing) > 0.9f))
            coverCandidates.Add(cover);
    }

    private Vector3 GetClearancePosition(Vector3 floorPosition, Vector3 floorNormal)
    {
        float normalZ = MathF.Max(minimumWalkableNormalZ, floorNormal.Z);
        float supportLift = MathF.Max(0f, settings.PawnRadius / normalZ - settings.PawnRadius) + 1f;
        return floorPosition + Vector3.UnitZ * supportLift;
    }

    private void Prune(List<Candidate> candidates, List<GeneratedNavigationEdge> edges,
        List<CoverCandidate> coverCandidates, CancellationToken cancellationToken)
    {
        var active = Enumerable.Repeat(true, candidates.Count).ToArray();
        bool changed;
        int passes = 0;
        do
        {
            changed = false;
            passes++;
            var adjacency = BuildUndirectedAdjacency(candidates.Count, edges, active);
            var removedThisPass = new HashSet<int>();
            for (int index = 0; index < candidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!active[index]) continue;
                List<int> neighbors = adjacency[index];
                if (neighbors.Count == 0)
                {
                    active[index] = false;
                    changed = true;
                    continue;
                }
                if (neighbors.Count < 2 || neighbors.Count > 12 || IsImportantElevation(index, neighbors, candidates) ||
                    IsNearCover(candidates[index].Position, coverCandidates) ||
                    neighbors.Any(removedThisPass.Contains) ||
                    !NeighborRingIsConnected(neighbors, adjacency, index))
                    continue;
                active[index] = false;
                removedThisPass.Add(index);
                changed = true;
            }
        } while (changed && passes < 8);

        var oldToNew = new int[candidates.Count];
        Array.Fill(oldToNew, -1);
        var remaining = new List<Candidate>();
        for (int index = 0; index < candidates.Count; index++)
        {
            if (!active[index]) continue;
            oldToNew[index] = remaining.Count;
            remaining.Add(candidates[index]);
        }
        edges.RemoveAll(edge => oldToNew[edge.StartNode] < 0 || oldToNew[edge.EndNode] < 0);
        for (int index = 0; index < edges.Count; index++)
        {
            GeneratedNavigationEdge edge = edges[index];
            edges[index] = edge with { StartNode = oldToNew[edge.StartNode], EndNode = oldToNew[edge.EndNode] };
        }
        candidates.Clear();
        candidates.AddRange(remaining);
    }

    private static List<int>[] BuildUndirectedAdjacency(int nodeCount, List<GeneratedNavigationEdge> edges,
        bool[] active)
    {
        var adjacency = Enumerable.Range(0, nodeCount).Select(_ => new List<int>()).ToArray();
        foreach (GeneratedNavigationEdge edge in edges)
        {
            if (!active[edge.StartNode] || !active[edge.EndNode]) continue;
            if (!adjacency[edge.StartNode].Contains(edge.EndNode)) adjacency[edge.StartNode].Add(edge.EndNode);
            if (!adjacency[edge.EndNode].Contains(edge.StartNode)) adjacency[edge.EndNode].Add(edge.StartNode);
        }
        return adjacency;
    }

    private bool IsImportantElevation(int index, List<int> neighbors, List<Candidate> candidates) =>
        neighbors.Any(neighbor => MathF.Abs(candidates[index].Position.Z - candidates[neighbor].Position.Z) >
                                  MathF.Max(8f, settings.MaximumStepUp * 0.5f));

    private bool IsNearCover(Vector3 position, List<CoverCandidate> covers)
    {
        float distanceSquared = settings.GridSpacing * settings.GridSpacing;
        return covers.Any(cover => Vector3.DistanceSquared(position, cover.Position) <= distanceSquared);
    }

    private static bool NeighborRingIsConnected(List<int> neighbors, List<int>[] adjacency, int excluded)
    {
        var allowed = neighbors.ToHashSet();
        var visited = new HashSet<int> { neighbors[0] };
        var queue = new Queue<int>();
        queue.Enqueue(neighbors[0]);
        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            foreach (int neighbor in adjacency[current])
            {
                if (neighbor != excluded && allowed.Contains(neighbor) && visited.Add(neighbor))
                    queue.Enqueue(neighbor);
            }
        }
        return visited.Count == neighbors.Count;
    }

    private static void Reindex(List<Candidate> candidates, List<GeneratedNavigationEdge> edges,
        out List<GeneratedNavigationNode> generatedNodes, out List<GeneratedNavigationEdge> generatedEdges)
    {
        generatedNodes = candidates.Select(candidate =>
            new GeneratedNavigationNode(candidate.Position, candidate.Normal)).ToList();
        generatedEdges = edges.Distinct().ToList();
    }

    private List<GeneratedCoverLink> GroupCover(List<CoverCandidate> candidates,
        List<GeneratedNavigationNode> navigationNodes)
    {
        var groups = new List<List<CoverCandidate>>();
        foreach (CoverCandidate candidate in candidates)
        {
            List<CoverCandidate> group = groups.FirstOrDefault(existing =>
            {
                Vector3 averageFacing = Vector3.Normalize(existing.Aggregate(Vector3.Zero,
                    (sum, item) => sum + item.Facing));
                if (Vector3.Dot(averageFacing, candidate.Facing) < 0.94f) return false;
                Vector3 averagePosition = existing.Aggregate(Vector3.Zero,
                    (sum, item) => sum + item.Position) / existing.Count;
                float planeDistance = MathF.Abs(Vector3.Dot(candidate.Position - averagePosition, averageFacing));
                return planeDistance <= settings.PawnRadius * 2f && existing.Any(item =>
                    Vector3.Distance(item.Position, candidate.Position) <= settings.CoverSlotInterval * 1.6f);
            });
            if (group is null)
                groups.Add([candidate]);
            else
                group.Add(candidate);
        }

        var results = new List<GeneratedCoverLink>();
        foreach (List<CoverCandidate> group in groups)
        {
            Vector3 facing = Vector3.Normalize(group.Aggregate(Vector3.Zero, (sum, item) => sum + item.Facing));
            Vector3 tangent = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, facing));
            group.Sort((left, right) => Vector3.Dot(left.Position, tangent).CompareTo(Vector3.Dot(right.Position, tangent)));
            foreach (List<CoverCandidate> run in SplitCoverRun(group))
            {
                var validCandidates = new List<(CoverCandidate Candidate, int NavigationNode)>(run.Count);
                foreach (CoverCandidate candidate in run)
                {
                    int nearestNode = FindNearestNavigationNode(candidate.Position, navigationNodes);
                    if (nearestNode >= 0)
                        validCandidates.Add((candidate, nearestNode));
                }

                foreach (List<(CoverCandidate Candidate, int NavigationNode)> validRun in
                         SplitValidCoverRun(validCandidates))
                {
                    var slots = new List<GeneratedCoverSlot>(validRun.Count);
                    for (int index = 0; index < validRun.Count; index++)
                    {
                        (CoverCandidate candidate, int nearestNode) = validRun[index];
                        slots.Add(new GeneratedCoverSlot(candidate.Position, candidate.Facing,
                            candidate.IsStanding, index == 0, index == validRun.Count - 1, nearestNode));
                    }

                    Vector3 position = slots.Aggregate(Vector3.Zero,
                        (sum, item) => sum + item.Position) / slots.Count;
                    Vector3 runFacing = Vector3.Normalize(slots.Aggregate(Vector3.Zero,
                        (sum, item) => sum + item.Facing));
                    results.Add(new GeneratedCoverLink(position, runFacing, slots));
                }
            }
        }
        return results;
    }

    private IEnumerable<List<CoverCandidate>> SplitCoverRun(List<CoverCandidate> group)
    {
        var run = new List<CoverCandidate>();
        foreach (CoverCandidate candidate in group)
        {
            if (run.Count > 0 && Vector3.Distance(run[^1].Position, candidate.Position) >
                settings.CoverSlotInterval * 1.75f)
            {
                yield return run;
                run = [];
            }
            run.Add(candidate);
        }
        if (run.Count > 0) yield return run;
    }

    private IEnumerable<List<(CoverCandidate Candidate, int NavigationNode)>> SplitValidCoverRun(
        List<(CoverCandidate Candidate, int NavigationNode)> candidates)
    {
        var run = new List<(CoverCandidate Candidate, int NavigationNode)>();
        foreach ((CoverCandidate candidate, int navigationNode) in candidates)
        {
            if (run.Count > 0 && Vector3.Distance(run[^1].Candidate.Position, candidate.Position) >
                settings.CoverSlotInterval * 1.75f)
            {
                yield return run;
                run = [];
            }
            run.Add((candidate, navigationNode));
        }
        if (run.Count > 0) yield return run;
    }

    private void AssignMantleTargets(List<GeneratedCoverLink> links)
    {
        if (links.Count < 2) return;
        GeneratedCoverSlot[][] slots = links.Select(link => link.Slots.ToArray()).ToArray();
        var possiblePairs = new List<(float Score, int LinkA, int SlotA, int LinkB, int SlotB)>();
        float minimumDistance = settings.PawnRadius * 2f + 4f;
        float maximumDistance = MathF.Max(192f, settings.CoverProbeDistance * 1.5f);
        float maximumLateralOffset = MathF.Max(settings.PawnRadius * 2f,
            settings.CoverSlotInterval * 0.55f);
        float maximumVerticalOffset = MathF.Max(32f, settings.MaximumStepUp);
        float vaultHeight = MathF.Min(PawnFullHeight - settings.PawnRadius, 145f);
        float vaultRadius = MathF.Max(4f, settings.PawnRadius * 0.35f);

        for (int linkA = 0; linkA < slots.Length; linkA++)
        {
            for (int slotA = 0; slotA < slots[linkA].Length; slotA++)
            {
                GeneratedCoverSlot source = slots[linkA][slotA];
                if (source.IsStanding) continue;
                for (int linkB = linkA + 1; linkB < slots.Length; linkB++)
                {
                    for (int slotB = 0; slotB < slots[linkB].Length; slotB++)
                    {
                        GeneratedCoverSlot target = slots[linkB][slotB];
                        if (target.IsStanding || Vector3.Dot(source.Facing, target.Facing) > -0.85f)
                            continue;

                        Vector3 delta = target.Position - source.Position;
                        float distance = delta.Length();
                        if (distance < minimumDistance || distance > maximumDistance)
                            continue;
                        float sourceForward = Vector3.Dot(delta, source.Facing);
                        float targetForward = Vector3.Dot(-delta, target.Facing);
                        if (sourceForward <= 0f || targetForward <= 0f)
                            continue;
                        Vector3 lateral = delta - source.Facing * sourceForward;
                        float lateralOffset = new Vector2(lateral.X, lateral.Y).Length();
                        float verticalOffset = MathF.Abs(delta.Z);
                        if (lateralOffset > maximumLateralOffset || verticalOffset > maximumVerticalOffset)
                            continue;

                        Vector3 vaultStart = source.Position + Vector3.UnitZ * vaultHeight;
                        Vector3 vaultEnd = target.Position + Vector3.UnitZ * vaultHeight;
                        if (collision.SphereCast(vaultStart, vaultEnd, vaultRadius, out _))
                            continue;

                        float score = distance + lateralOffset * 4f + verticalOffset * 3f +
                                      MathF.Abs(sourceForward - targetForward);
                        possiblePairs.Add((score, linkA, slotA, linkB, slotB));
                    }
                }
            }
        }

        var assigned = new HashSet<(int Link, int Slot)>();
        foreach ((float _, int linkA, int slotA, int linkB, int slotB) in
                 possiblePairs.OrderBy(pair => pair.Score))
        {
            if (assigned.Contains((linkA, slotA)) || assigned.Contains((linkB, slotB)))
                continue;
            assigned.Add((linkA, slotA));
            assigned.Add((linkB, slotB));
            slots[linkA][slotA] = slots[linkA][slotA] with
            {
                MantleTargetLink = linkB,
                MantleTargetSlot = slotB
            };
            slots[linkB][slotB] = slots[linkB][slotB] with
            {
                MantleTargetLink = linkA,
                MantleTargetSlot = slotA
            };
        }

        for (int linkIndex = 0; linkIndex < links.Count; linkIndex++)
            links[linkIndex] = links[linkIndex] with { Slots = slots[linkIndex] };
    }

    private int FindNearestNavigationNode(Vector3 position, List<GeneratedNavigationNode> nodes)
    {
        int nearest = -1;
        float nearestDistance = settings.ConnectionDistance * settings.ConnectionDistance * 2.25f;
        for (int index = 0; index < nodes.Count; index++)
        {
            float distance = Vector3.DistanceSquared(position, nodes[index].Position);
            if (distance < nearestDistance && CanTraverse(nodes[index].Position, position))
            {
                nearestDistance = distance;
                nearest = index;
            }
        }
        return nearest;
    }
}
