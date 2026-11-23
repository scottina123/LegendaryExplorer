using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Cover data the Game 3 runtime reads from cooked CoverLinks. The constants are Engine.Default__CoverLink's
/// (StandingLeanOffset, CrouchLeanOffset, PopupOffset, SlipDist, TurnDist, MaxFireLinkDist, COVERLINK_* consts) so
/// generated slots behave like ones built by the original editor.
/// </summary>
public sealed partial class NavigationGenerator
{
    /// <summary>
    /// CoverLinks rest on the floor at their CollisionCylinder height (58) less UE3's 2-unit FindBase skin, and cooked
    /// slots sit 2 units above their link: slot locations are therefore 58 above the floor.
    /// </summary>
    public const float CoverSlotHeightAboveFloor = 58f;
    private const float StandingLeanSide = 78f, StandingLeanUp = 69f;
    private const float CrouchLeanSide = 70f, CrouchLeanUp = 19f;
    private const float PopupUp = 70f;
    private const float LeanTraceDistance = 64f;
    private const float SwatTurnDistance = 612f;
    private const float MaxFireLinkDistance = 3000f;
    private const float DangerDistance = 1536f;
    private const float ExposureDot = 0.4f;
    /// <summary>Fire links outside this cone of the slot's facing are only usable as fallbacks (cooked data agrees).</summary>
    private const float PrimaryFireDot = 0.7f;

    private float CoverAlignDistance => MathF.Max(36f, settings.PawnRadius + 2f);

    private static Vector3 RightOf(Vector3 facing)
    {
        Vector3 right = Vector3.Cross(Vector3.UnitZ, new Vector3(facing.X, facing.Y, 0f));
        return right.LengthSquared() < 0.0001f ? Vector3.UnitY : Vector3.Normalize(right);
    }

    private static Vector3 Horizontal(Vector3 vector)
    {
        vector.Z = 0f;
        return vector.LengthSquared() < 0.0001f ? Vector3.Zero : Vector3.Normalize(vector);
    }

    private static Vector3 SlotLocation(GeneratedCoverSlot slot) =>
        slot.Position + Vector3.UnitZ * CoverSlotHeightAboveFloor;

    private static Vector3 LeanPoint(GeneratedCoverSlot slot, int side)
    {
        Vector3 right = RightOf(slot.Facing);
        return slot.IsStanding
            ? SlotLocation(slot) + right * (side * StandingLeanSide) + Vector3.UnitZ * StandingLeanUp
            : SlotLocation(slot) + right * (side * CrouchLeanSide) + Vector3.UnitZ * CrouchLeanUp;
    }

    private static Vector3 PopupPoint(GeneratedCoverSlot slot) => SlotLocation(slot) + Vector3.UnitZ * PopupUp;

    /// <summary>The points a pawn fires from when it uses this slot's actions (GetSlotViewPoint).</summary>
    private static List<Vector3> ViewPoints(GeneratedCoverSlot slot)
    {
        var points = new List<Vector3>(3);
        if (slot.LeanLeft) points.Add(LeanPoint(slot, -1));
        if (slot.LeanRight) points.Add(LeanPoint(slot, 1));
        if (!slot.IsStanding && slot.PopUp) points.Add(PopupPoint(slot));
        return points;
    }

    /// <summary>The torso a pawn keeps behind the cover while it is not firing.</summary>
    private static Vector3 ProtectedPoint(GeneratedCoverSlot slot) =>
        SlotLocation(slot) + Vector3.UnitZ * (slot.IsStanding ? 40f : 4f);

    private bool HasLineOfSight(Vector3 from, Vector3 to)
    {
        Vector3 delta = to - from;
        float distance = delta.Length();
        return distance < 1f || !collision.Raycast(from, delta / distance, distance, out _);
    }

    #region Leans, pop-ups and cover moves

    /// <summary>
    /// Validates the edge actions (AutoAdjustSlot's lean and pop-up traces) and finds the cover-turn, cover-slip and
    /// swat-turn targets for every edge slot.
    /// </summary>
    private void AnalyzeCoverMoves(List<GeneratedCoverLink> links, CancellationToken cancellationToken)
    {
        GeneratedCoverSlot[][] slots = links.Select(link => link.Slots.ToArray()).ToArray();
        for (int linkIndex = 0; linkIndex < slots.Length; linkIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GeneratedCoverSlot[] linkSlots = slots[linkIndex];
            for (int slotIndex = 0; slotIndex < linkSlots.Length; slotIndex++)
            {
                GeneratedCoverSlot slot = linkSlots[slotIndex];
                bool leftEdge = slotIndex == 0, rightEdge = slotIndex == linkSlots.Length - 1;
                linkSlots[slotIndex] = slot with
                {
                    LeanLeft = leftEdge && CanLean(slot, -1),
                    LeanRight = rightEdge && CanLean(slot, 1),
                    CanPopUp = !slot.IsStanding && CanPopUp(slot)
                };
            }
        }

        // Moves are found after every slot's actions are final.
        var moves = new (GeneratedCoverRef? TurnLeft, GeneratedCoverRef? TurnRight,
            GeneratedCoverRef? SwatLeft, GeneratedCoverRef? SwatRight)[slots.Length][];
        Parallel.For(0, slots.Length, new ParallelOptions { CancellationToken = cancellationToken }, linkIndex =>
        {
            GeneratedCoverSlot[] linkSlots = slots[linkIndex];
            moves[linkIndex] = new (GeneratedCoverRef?, GeneratedCoverRef?, GeneratedCoverRef?, GeneratedCoverRef?)[linkSlots.Length];
            for (int slotIndex = 0; slotIndex < linkSlots.Length; slotIndex++)
            {
                if (slotIndex == 0)
                {
                    moves[linkIndex][slotIndex].TurnLeft = FindCoverTurn(slots, linkIndex, slotIndex, -1);
                    moves[linkIndex][slotIndex].SwatLeft = FindSwatTurn(slots, linkIndex, slotIndex, -1);
                }
                if (slotIndex == linkSlots.Length - 1)
                {
                    moves[linkIndex][slotIndex].TurnRight = FindCoverTurn(slots, linkIndex, slotIndex, 1);
                    moves[linkIndex][slotIndex].SwatRight = FindSwatTurn(slots, linkIndex, slotIndex, 1);
                }
            }
        });

        for (int linkIndex = 0; linkIndex < slots.Length; linkIndex++)
        {
            for (int slotIndex = 0; slotIndex < slots[linkIndex].Length; slotIndex++)
            {
                var move = moves[linkIndex][slotIndex];
                // Cooked levels pair the slip target with the slot around the corner (same marker as the turn).
                slots[linkIndex][slotIndex] = slots[linkIndex][slotIndex] with
                {
                    CoverTurnLeft = move.TurnLeft,
                    CoverTurnRight = move.TurnRight,
                    SlipLeft = move.TurnLeft,
                    SlipRight = move.TurnRight,
                    SwatTurnLeft = move.SwatLeft,
                    SwatTurnRight = move.SwatRight
                };
            }
            links[linkIndex] = links[linkIndex] with { Slots = slots[linkIndex] };
        }
    }

    /// <summary>A lean needs room beside the slot and an unobstructed LeanTraceDist forward from the lean point.</summary>
    private bool CanLean(GeneratedCoverSlot slot, int side)
    {
        Vector3 lean = LeanPoint(slot, side);
        Vector3 shoulder = new(SlotLocation(slot).X, SlotLocation(slot).Y, lean.Z);
        if (!HasLineOfSight(shoulder, lean) || collision.OverlapSphere(lean, 10f))
            return false;
        Vector3 facing = Horizontal(slot.Facing);
        return HasLineOfSight(lean, lean + facing * LeanTraceDistance);
    }

    /// <summary>A pop-up needs the space over mid-level cover to be open in front of the slot.</summary>
    private bool CanPopUp(GeneratedCoverSlot slot)
    {
        Vector3 popup = PopupPoint(slot);
        Vector3 facing = Horizontal(slot.Facing);
        return HasLineOfSight(SlotLocation(slot), popup) &&
               HasLineOfSight(popup, popup + facing * (CoverAlignDistance + LeanTraceDistance));
    }

    /// <summary>
    /// The edge slot of another link around an outside corner: perpendicular facing, turned toward this slot's edge
    /// direction, close enough to wrap to and connected by a clear walk around the corner.
    /// </summary>
    private GeneratedCoverRef? FindCoverTurn(GeneratedCoverSlot[][] slots, int linkIndex, int slotIndex, int side)
    {
        GeneratedCoverSlot source = slots[linkIndex][slotIndex];
        Vector3 facing = Horizontal(source.Facing);
        Vector3 edge = RightOf(facing) * side;
        float maximumDistance = 2f * (CoverAlignDistance + settings.PawnRadius) + 64f;
        GeneratedCoverRef? best = null;
        float bestDistance = float.MaxValue;
        for (int otherLink = 0; otherLink < slots.Length; otherLink++)
        {
            if (otherLink == linkIndex) continue;
            GeneratedCoverSlot[] otherSlots = slots[otherLink];
            foreach (int otherSlot in EdgeSlotIndices(otherSlots.Length))
            {
                GeneratedCoverSlot target = otherSlots[otherSlot];
                Vector3 targetFacing = Horizontal(target.Facing);
                if (MathF.Abs(Vector3.Dot(targetFacing, facing)) > 0.5f || Vector3.Dot(targetFacing, edge) > -0.5f)
                    continue;
                Vector3 delta = target.Position - source.Position;
                float distance = new Vector2(delta.X, delta.Y).Length();
                if (distance > maximumDistance || distance >= bestDistance || Vector3.Dot(delta, edge) < 8f ||
                    MathF.Abs(delta.Z) > settings.MaximumStepUp)
                    continue;
                // The target slot's own edge must be the one nearest the corner.
                Vector3 targetEdge = RightOf(targetFacing) * (otherSlot == 0 ? -1 : 1);
                if (otherSlots.Length > 1 && Vector3.Dot(targetEdge, -delta) < 0f)
                    continue;
                Vector3 corner = source.Position + edge * Vector3.Dot(delta, edge);
                if (!CanTraverse(source.Position, target.Position) &&
                    !(CanTraverse(source.Position, corner) && CanTraverse(corner, target.Position)))
                    continue;
                best = new GeneratedCoverRef(otherLink, otherSlot);
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>
    /// Cover-to-cover run: the facing edge slot of the next piece of cover along the same line, within TurnDist,
    /// with a clear path behind the cover.
    /// </summary>
    private GeneratedCoverRef? FindSwatTurn(GeneratedCoverSlot[][] slots, int linkIndex, int slotIndex, int side)
    {
        GeneratedCoverSlot source = slots[linkIndex][slotIndex];
        Vector3 facing = Horizontal(source.Facing);
        Vector3 edge = RightOf(facing) * side;
        GeneratedCoverRef? best = null;
        float bestAlong = float.MaxValue;
        for (int otherLink = 0; otherLink < slots.Length; otherLink++)
        {
            if (otherLink == linkIndex) continue;
            GeneratedCoverSlot[] otherSlots = slots[otherLink];
            // Moving right arrives at the other cover's left edge and vice versa.
            int otherSlot = side > 0 ? 0 : otherSlots.Length - 1;
            GeneratedCoverSlot target = otherSlots[otherSlot];
            if (Vector3.Dot(Horizontal(target.Facing), facing) < 0.9f)
                continue;
            Vector3 delta = target.Position - source.Position;
            float along = Vector3.Dot(delta, edge);
            if (along <= settings.PawnRadius * 2f || along > SwatTurnDistance || along >= bestAlong ||
                MathF.Abs(Vector3.Dot(delta, facing)) > 48f || MathF.Abs(delta.Z) > settings.MaximumStepUp ||
                !CanTraverse(source.Position, target.Position))
                continue;
            best = new GeneratedCoverRef(otherLink, otherSlot);
            bestAlong = along;
        }
        return best;
    }

    private static IEnumerable<int> EdgeSlotIndices(int count)
    {
        if (count <= 0) yield break;
        yield return 0;
        if (count > 1) yield return count - 1;
    }

    #endregion

    #region Fire links, exposure and danger

    /// <summary>
    /// Builds the per-slot visibility data AI cover evaluation reads (FireLinks, ExposedCoverPackedProperties,
    /// DangerCoverPackedProperties) from line-of-sight traces between the slots' firing and protected positions.
    /// </summary>
    private void AnalyzeCoverVisibility(List<GeneratedCoverLink> links, List<GeneratedNavigationNode> nodes,
        CancellationToken cancellationToken)
    {
        var refs = new List<GeneratedCoverRef>();
        for (int linkIndex = 0; linkIndex < links.Count; linkIndex++)
            for (int slotIndex = 0; slotIndex < links[linkIndex].Slots.Count; slotIndex++)
                refs.Add(new GeneratedCoverRef(linkIndex, slotIndex));
        GeneratedCoverSlot[] flat = refs.Select(item => links[item.Link].Slots[item.Slot]).ToArray();
        List<Vector3>[] viewPoints = flat.Select(ViewPoints).ToArray();
        Vector3[] facings = flat.Select(slot => Horizontal(slot.Facing)).ToArray();

        var dangerTargets = new List<(GeneratedNavTarget Target, Vector3 Point)>(nodes.Count + flat.Length);
        for (int node = 0; node < nodes.Count; node++)
            dangerTargets.Add((GeneratedNavTarget.ForNode(node), nodes[node].Position + Vector3.UnitZ * 60f));
        for (int index = 0; index < flat.Length; index++)
            dangerTargets.Add((GeneratedNavTarget.ForSlot(refs[index].Link, refs[index].Slot),
                SlotLocation(flat[index]) + Vector3.UnitZ * 20f));

        var fireLinks = new List<GeneratedFireLink>[flat.Length];
        var exposures = new List<GeneratedExposure>[flat.Length];
        var dangers = new List<GeneratedDanger>[flat.Length];
        Parallel.For(0, flat.Length, new ParallelOptions { CancellationToken = cancellationToken }, source =>
        {
            GeneratedCoverSlot slot = flat[source];
            Vector3 location = SlotLocation(slot);
            Vector3 facing = facings[source];
            var fire = new List<GeneratedFireLink>();
            var exposed = new List<GeneratedExposure>();
            for (int target = 0; target < flat.Length; target++)
            {
                if (refs[target].Link == refs[source].Link) continue;
                GeneratedCoverSlot other = flat[target];
                Vector3 otherLocation = SlotLocation(other);
                Vector3 offset = otherLocation - location;
                float distance = offset.Length();
                if (distance > MaxFireLinkDistance || distance < 1f) continue;
                float dot = Vector3.Dot(Horizontal(offset), facing);

                // FireLink: we can hit the target when it leans/pops out, or its body when our cover flanks it.
                if (dot > 0.2f && viewPoints[source].Count > 0 &&
                    CanHitAny(viewPoints[source], TargetPoints(other, viewPoints[target]), facing))
                    fire.Add(new GeneratedFireLink(refs[target], dot < PrimaryFireDot));

                // Exposure: an enemy at the target slot can hit our protected position from our side or rear.
                if (dot < ExposureDot && CanHitAny(ShooterPoints(other, viewPoints[target]),
                        [ProtectedPoint(slot)], null))
                {
                    float angle = Math.Clamp((ExposureDot - dot) / ExposureDot, 0.05f, 1f);
                    float falloff = distance <= DangerDistance ? 1f :
                        1f - 0.7f * (distance - DangerDistance) / (MaxFireLinkDistance - DangerDistance);
                    exposed.Add(new GeneratedExposure(refs[target],
                        (byte)Math.Clamp(MathF.Round(255f * angle * falloff), 1f, 255f)));
                }
            }

            // Danger: navigation in front of this slot that a pawn using it can fire upon.
            var danger = new List<GeneratedDanger>();
            if (viewPoints[source].Count > 0)
            {
                foreach ((GeneratedNavTarget navTarget, Vector3 point) in dangerTargets)
                {
                    if (!navTarget.IsNode && navTarget.Slot.Link == refs[source].Link) continue;
                    Vector3 offset = point - location;
                    float distance = offset.Length();
                    if (distance > DangerDistance || distance < settings.PawnRadius * 2f) continue;
                    float dot = Vector3.Dot(Horizontal(offset), facing);
                    if (dot <= 0.1f || !CanHitAny(viewPoints[source], [point], facing)) continue;
                    danger.Add(new GeneratedDanger(navTarget,
                        (byte)Math.Clamp(MathF.Round(255f * dot), 16f, 255f)));
                }
            }

            fireLinks[source] = fire;
            exposures[source] = exposed;
            dangers[source] = danger;
        });

        GeneratedCoverSlot[][] slots = links.Select(link => link.Slots.ToArray()).ToArray();
        for (int index = 0; index < refs.Count; index++)
        {
            GeneratedCoverRef item = refs[index];
            slots[item.Link][item.Slot] = slots[item.Link][item.Slot] with
            {
                FireLinks = fireLinks[index],
                Exposures = exposures[index],
                Dangers = dangers[index]
            };
        }
        for (int linkIndex = 0; linkIndex < links.Count; linkIndex++)
            links[linkIndex] = links[linkIndex] with { Slots = slots[linkIndex] };
    }

    /// <summary>Where a pawn in the target slot can be hit: its firing positions, and its torso.</summary>
    private static IEnumerable<Vector3> TargetPoints(GeneratedCoverSlot slot, List<Vector3> viewPoints)
    {
        foreach (Vector3 point in viewPoints) yield return point;
        yield return SlotLocation(slot) + Vector3.UnitZ * 30f;
    }

    /// <summary>Where a pawn in the slot can shoot from (its actions, or standing over/next to it when it has none).</summary>
    private static IEnumerable<Vector3> ShooterPoints(GeneratedCoverSlot slot, List<Vector3> viewPoints)
    {
        if (viewPoints.Count == 0)
        {
            yield return PopupPoint(slot);
            yield break;
        }
        foreach (Vector3 point in viewPoints) yield return point;
    }

    private bool CanHitAny(IEnumerable<Vector3> from, IEnumerable<Vector3> to, Vector3? requiredFacing)
    {
        Vector3[] targets = to as Vector3[] ?? to.ToArray();
        foreach (Vector3 start in from)
        {
            foreach (Vector3 end in targets)
            {
                if (requiredFacing is { } facing && Vector3.Dot(end - start, facing) <= 0f)
                    continue;
                if (HasLineOfSight(start, end))
                    return true;
            }
        }
        return false;
    }

    #endregion

    #region ReachSpecs

    /// <summary>
    /// Every ReachSpec touching cover, mirroring cooked levels: links reach their own markers and nearby nodes;
    /// markers walk to nearby nodes and other cover, step along their link (SlotToSlot), vault (Mantle), and use
    /// the cover moves found by <see cref="AnalyzeCoverMoves"/>.
    /// </summary>
    private List<GeneratedCoverConnection> BuildCoverConnections(List<GeneratedCoverLink> links,
        List<GeneratedNavigationNode> nodes, CancellationToken cancellationToken)
    {
        var refs = new List<GeneratedCoverRef>();
        for (int linkIndex = 0; linkIndex < links.Count; linkIndex++)
            for (int slotIndex = 0; slotIndex < links[linkIndex].Slots.Count; slotIndex++)
                refs.Add(new GeneratedCoverRef(linkIndex, slotIndex));
        float connectionDistanceSquared = settings.ConnectionDistance * settings.ConnectionDistance;
        var perSlot = new List<GeneratedCoverConnection>[refs.Count];
        var sizeCache = new ConcurrentDictionary<(Vector3, Vector3), int>();
        int Size(Vector3 start, Vector3 end) => sizeCache.GetOrAdd((start, end), key =>
            GetLargestPathSize(key.Item1, key.Item2));

        Parallel.For(0, refs.Count, new ParallelOptions { CancellationToken = cancellationToken }, index =>
        {
            GeneratedCoverRef item = refs[index];
            GeneratedCoverSlot slot = links[item.Link].Slots[item.Slot];
            GeneratedNavTarget self = GeneratedNavTarget.ForSlot(item.Link, item.Slot);
            var connections = new List<GeneratedCoverConnection>();

            // Walking to and from the path network (both directions are owned by the slot).
            bool connectedToNode = false;
            for (int node = 0; node < nodes.Count; node++)
            {
                Vector3 nodePosition = nodes[node].Position;
                if (Vector3.DistanceSquared(nodePosition, slot.Position) > connectionDistanceSquared ||
                    !CanTraverse(slot.Position, nodePosition))
                    continue;
                int size = Size(slot.Position, nodePosition);
                connections.Add(new GeneratedCoverConnection(self, GeneratedNavTarget.ForNode(node),
                    GeneratedCoverConnectionKind.Walk, size));
                if (CanTraverse(nodePosition, slot.Position))
                    connections.Add(new GeneratedCoverConnection(GeneratedNavTarget.ForNode(node), self,
                        GeneratedCoverConnectionKind.Walk, size));
                connectedToNode = true;
            }
            if (!connectedToNode && (uint)slot.NearestNavigationNode < nodes.Count)
            {
                int node = slot.NearestNavigationNode;
                int size = Size(slot.Position, nodes[node].Position);
                connections.Add(new GeneratedCoverConnection(self, GeneratedNavTarget.ForNode(node),
                    GeneratedCoverConnectionKind.Walk, size));
                connections.Add(new GeneratedCoverConnection(GeneratedNavTarget.ForNode(node), self,
                    GeneratedCoverConnectionKind.Walk, size));
            }

            // Walking directly to other cover (each slot owns its outgoing spec).
            for (int other = 0; other < refs.Count; other++)
            {
                GeneratedCoverRef otherRef = refs[other];
                if (otherRef.Link == item.Link) continue;
                Vector3 otherPosition = links[otherRef.Link].Slots[otherRef.Slot].Position;
                if (Vector3.DistanceSquared(otherPosition, slot.Position) > connectionDistanceSquared ||
                    !CanTraverse(slot.Position, otherPosition))
                    continue;
                connections.Add(new GeneratedCoverConnection(self,
                    GeneratedNavTarget.ForSlot(otherRef.Link, otherRef.Slot), GeneratedCoverConnectionKind.Walk,
                    Size(slot.Position, otherPosition)));
            }

            // Moving along the link: 1 toward the left neighbour, 2 toward the right.
            IReadOnlyList<GeneratedCoverSlot> linkSlots = links[item.Link].Slots;
            if (item.Slot > 0)
                connections.Add(new GeneratedCoverConnection(self, GeneratedNavTarget.ForSlot(item.Link, item.Slot - 1),
                    GeneratedCoverConnectionKind.SlotToSlot, Size(slot.Position, linkSlots[item.Slot - 1].Position), 1));
            if (item.Slot + 1 < linkSlots.Count)
                connections.Add(new GeneratedCoverConnection(self, GeneratedNavTarget.ForSlot(item.Link, item.Slot + 1),
                    GeneratedCoverConnectionKind.SlotToSlot, Size(slot.Position, linkSlots[item.Slot + 1].Position), 2));

            if ((uint)slot.MantleTargetLink < links.Count &&
                (uint)slot.MantleTargetSlot < links[slot.MantleTargetLink].Slots.Count)
                connections.Add(new GeneratedCoverConnection(self,
                    GeneratedNavTarget.ForSlot(slot.MantleTargetLink, slot.MantleTargetSlot),
                    GeneratedCoverConnectionKind.Mantle));

            void AddMove(GeneratedCoverRef? target, GeneratedCoverConnectionKind kind, byte direction, bool sized)
            {
                if (target is not { } value) return;
                int size = sized ? Size(slot.Position, links[value.Link].Slots[value.Slot].Position) : 0;
                connections.Add(new GeneratedCoverConnection(self, GeneratedNavTarget.ForSlot(value.Link, value.Slot),
                    kind, size, direction));
            }
            AddMove(slot.SlipLeft, GeneratedCoverConnectionKind.CoverSlip, 1, false);
            AddMove(slot.SlipRight, GeneratedCoverConnectionKind.CoverSlip, 2, false);
            AddMove(slot.CoverTurnLeft, GeneratedCoverConnectionKind.CoverTurn, 1, false);
            AddMove(slot.CoverTurnRight, GeneratedCoverConnectionKind.CoverTurn, 2, false);
            AddMove(slot.SwatTurnLeft, GeneratedCoverConnectionKind.SwatTurn, 1, true);
            AddMove(slot.SwatTurnRight, GeneratedCoverConnectionKind.SwatTurn, 2, true);
            perSlot[index] = connections;
        });

        var result = new List<GeneratedCoverConnection>();
        for (int linkIndex = 0; linkIndex < links.Count; linkIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GeneratedCoverLink link = links[linkIndex];
            for (int slotIndex = 0; slotIndex < link.Slots.Count; slotIndex++)
            {
                result.Add(new GeneratedCoverConnection(default, GeneratedNavTarget.ForSlot(linkIndex, slotIndex),
                    GeneratedCoverConnectionKind.LinkToSlot, Size(link.Position, link.Slots[slotIndex].Position),
                    FromLink: linkIndex));
            }
            int nearest = -1;
            float nearestDistance = connectionDistanceSquared;
            for (int node = 0; node < nodes.Count; node++)
            {
                float distance = Vector3.DistanceSquared(nodes[node].Position, link.Position);
                if (distance < nearestDistance && CanTraverse(link.Position, nodes[node].Position))
                {
                    nearest = node;
                    nearestDistance = distance;
                }
            }
            if (nearest >= 0)
            {
                result.Add(new GeneratedCoverConnection(default, GeneratedNavTarget.ForNode(nearest),
                    GeneratedCoverConnectionKind.LinkToNode, Size(link.Position, nodes[nearest].Position),
                    FromLink: linkIndex));
            }
        }
        foreach (List<GeneratedCoverConnection> connections in perSlot)
            result.AddRange(connections);
        return result;
    }

    #endregion
}
