using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LegendaryExplorer.Tools.LevelEditor;

public sealed record NavigationSerializationResult(
    int PathNodeCount,
    int ReachSpecCount,
    int CoverLinkCount,
    int CoverSlotCount);

/// <summary>
/// Writes generated navigation the way cooked ME3/LE3 levels store it: PathNodes, CoverLinks and CoverSlotMarkers at
/// the heights the engine places them, ReachSpecs of every cover class sized to Scout.PathSizes, and the packed
/// cover data (FireLinks, exposure, danger, turn targets) together with the Level's CoverLinkRefs, CoverIndexPairs and
/// NavRefs tables they index into.
/// </summary>
public static class NavigationSerializer
{
    /// <summary>Default__PathNode's cylinder (inherited from NavigationPoint) is 90 high; FindBase rests it 2 units into that.</summary>
    public const float PathNodeHeightAboveFloor = 88f;
    /// <summary>Default__CoverLink.CollisionCylinder is 58 high; FindBase rests it 2 units into that.</summary>
    public const float CoverLinkHeightAboveFloor = 56f;

    private const ushort NoCoverPair = 0xFFFF;

    private sealed class Writer
    {
        public required IMEPackage Package;
        public required ExportEntry LevelExport;
        public required Level Level;
        public required IReadOnlyList<NavigationPathSize> Sizes;
        public readonly Dictionary<(int LinkRef, int Slot), int> CoverPairs = [];
        public readonly Dictionary<int, int> NavRefIndex = [];
        public readonly List<(ExportEntry Actor, Guid Guid, Func<Vector3, bool> Contains)> Volumes = [];
        public int ReachSpecCount;
    }

    public static NavigationSerializationResult Write(OpenLevelFile file, NavigationGenerationResult result,
        NavigationGenerationSettings settings, IReadOnlyList<ExportEntry> existingNavigationPoints = null)
    {
        if (file.IsReadOnly)
            throw new InvalidOperationException("The active level is read-only.");
        NavigationSerializationResult written = Write(file.Package, file.LevelExport, result, settings,
            existingNavigationPoints);
        file.IsDirty = true;
        return written;
    }

    /// <summary>
    /// Navigation points in <paramref name="levelExport"/> that nothing connects yet (spawn points, player starts): the
    /// generator links these into the new network. Pass their locations to <see cref="NavigationGenerator.Generate"/>
    /// and the same list here.
    /// </summary>
    public static List<ExportEntry> FindUnconnectedNavigationPoints(ExportEntry levelExport)
    {
        IMEPackage package = levelExport.FileRef;
        var points = new List<ExportEntry>();
        foreach (int actorIndex in levelExport.GetBinaryData<Level>().Actors)
        {
            if (actorIndex <= 0 || !package.IsUExport(actorIndex)) continue;
            ExportEntry actor = package.GetUExport(actorIndex);
            if (actor.ClassName is "CoverLink" or "CoverSlotMarker" || !actor.IsA("NavigationPoint") ||
                actor.GetProperty<ArrayProperty<ObjectProperty>>("PathList") is { Count: > 0 } ||
                actor.GetProperty<StructProperty>("location") is null)
                continue;
            points.Add(actor);
        }
        return points;
    }

    public static NavigationSerializationResult Write(IMEPackage package, ExportEntry levelExport,
        NavigationGenerationResult result, NavigationGenerationSettings settings,
        IReadOnlyList<ExportEntry> existingNavigationPoints = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(levelExport);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(settings);
        if (!package.Game.IsGame3())
        {
            throw new NotSupportedException(
                "Automatic cover serialization currently supports ME3 and LE3. Visualization and collision queries support every game.");
        }
        if (!ReferenceEquals(levelExport.FileRef, package))
            throw new ArgumentException("The level export does not belong to the supplied package.", nameof(levelExport));

        var writer = new Writer
        {
            Package = package,
            LevelExport = levelExport,
            Level = levelExport.GetBinaryData<Level>(),
            Sizes = settings.PathSizes is { Count: > 0 } sizes ? sizes : NavigationPathSize.Game3Defaults
        };
        Level level = writer.Level;
        for (int index = 0; index < level.CoverIndexPairs.Count; index++)
        {
            CoverIndexPair pair = level.CoverIndexPairs[index];
            writer.CoverPairs.TryAdd(((int)pair.CoverIndexIdx, pair.SlotIdx), index);
        }
        for (int index = 0; index < level.NavRefs.Count; index++)
        {
            if (level.NavRefs[index] > 0)
                writer.NavRefIndex.TryAdd(level.NavRefs[index], index);
        }
        CollectCombatZones(writer);

        IReadOnlyList<GeneratedCoverConnection> connections = result.CoverConnections is { Count: > 0 }
            ? result.CoverConnections
            : DeriveBasicCoverConnections(result);

        // Largest outgoing size per navigation point, for MaxPathSize.
        var nodeSize = new int[result.Nodes.Count];
        var slotSize = result.CoverLinks.Select(link => new int[link.Slots.Count]).ToArray();
        var linkSize = new int[result.CoverLinks.Count];
        foreach (GeneratedNavigationEdge edge in result.Edges)
        {
            if ((uint)edge.StartNode < nodeSize.Length)
                nodeSize[edge.StartNode] = Math.Max(nodeSize[edge.StartNode], edge.SizeIndex);
        }
        foreach (GeneratedCoverConnection connection in connections)
        {
            if (connection.FromLink >= 0)
            {
                if ((uint)connection.FromLink < linkSize.Length)
                    linkSize[connection.FromLink] = Math.Max(linkSize[connection.FromLink], connection.SizeIndex);
            }
            else if (connection.From.IsNode)
            {
                if ((uint)connection.From.Node < nodeSize.Length)
                    nodeSize[connection.From.Node] = Math.Max(nodeSize[connection.From.Node], connection.SizeIndex);
            }
            else if (IsValidSlot(result, connection.From.Slot))
            {
                ref int size = ref slotSize[connection.From.Slot.Link][connection.From.Slot.Slot];
                size = Math.Max(size, connection.SizeIndex);
            }
        }

        // Actors.
        var pathExports = new List<ExportEntry>(result.Nodes.Count);
        for (int index = 0; index < result.Nodes.Count; index++)
        {
            Vector3 location = result.Nodes[index].Position + Vector3.UnitZ * PathNodeHeightAboveFloor;
            pathExports.Add(CreateNavigationActor(writer, "PathNode", location, null, nodeSize[index]));
        }

        var coverLinks = new List<ExportEntry>(result.CoverLinks.Count);
        var markerGroups = new List<List<ExportEntry>>(result.CoverLinks.Count);
        var linkLocations = new List<Vector3>(result.CoverLinks.Count);
        var linkRotations = new List<Rotator>(result.CoverLinks.Count);
        int coverSlotCount = 0;
        for (int linkIndex = 0; linkIndex < result.CoverLinks.Count; linkIndex++)
        {
            GeneratedCoverLink generatedLink = result.CoverLinks[linkIndex];
            Rotator linkRotation = Rotator.FromDirectionVector(Flatten(generatedLink.Facing));
            Vector3 linkLocation = generatedLink.Position + Vector3.UnitZ * CoverLinkHeightAboveFloor;
            ExportEntry link = CreateNavigationActor(writer, "CoverLink", linkLocation, linkRotation, linkSize[linkIndex]);
            link.WriteProperty(new BoolProperty(false, "bAutoAdjust"));
            var markers = new List<ExportEntry>(generatedLink.Slots.Count);
            for (int slotIndex = 0; slotIndex < generatedLink.Slots.Count; slotIndex++)
            {
                GeneratedCoverSlot generatedSlot = generatedLink.Slots[slotIndex];
                Vector3 slotLocation = generatedSlot.Position + Vector3.UnitZ * NavigationGenerator.CoverSlotHeightAboveFloor;
                ExportEntry marker = CreateNavigationActor(writer, "CoverSlotMarker", slotLocation,
                    Rotator.FromDirectionVector(Flatten(generatedSlot.Facing)), slotSize[linkIndex][slotIndex]);
                marker.WriteProperty(CreateCoverInfo(link, slotIndex, "OwningSlot"));
                marker.WriteProperty(new ObjectProperty(link, "Owner"));
                markers.Add(marker);
                coverSlotCount++;
            }
            coverLinks.Add(link);
            markerGroups.Add(markers);
            linkLocations.Add(linkLocation);
            linkRotations.Add(linkRotation);
        }

        // Level tables the packed cover data indexes into.
        var navigationChain = new List<ExportEntry>(pathExports.Count + coverLinks.Count + coverSlotCount);
        navigationChain.AddRange(pathExports);
        for (int index = 0; index < coverLinks.Count; index++)
        {
            navigationChain.Add(coverLinks[index]);
            navigationChain.AddRange(markerGroups[index]);
        }
        AppendNavigationChain(level, package, navigationChain);
        AppendCoverChain(level, package, coverLinks);
        foreach (ExportEntry actor in navigationChain)
        {
            if (!level.Actors.Contains(actor.UIndex)) level.Actors.Add(actor.UIndex);
            if (!writer.NavRefIndex.ContainsKey(actor.UIndex))
            {
                writer.NavRefIndex[actor.UIndex] = level.NavRefs.Count;
                level.NavRefIndicies.Add(level.NavRefs.Count);
                level.NavRefs.Add(actor.UIndex);
            }
        }
        var linkRefIndex = new int[coverLinks.Count];
        for (int index = 0; index < coverLinks.Count; index++)
        {
            int existing = level.CoverLinkRefs.IndexOf(coverLinks[index].UIndex);
            if (existing < 0)
            {
                existing = level.CoverLinkRefs.Count;
                level.CoverLinkRefs.Add(coverLinks[index].UIndex);
            }
            linkRefIndex[index] = existing;
        }

        ushort Pair(GeneratedCoverRef? reference)
        {
            if (reference is not { } value || !IsValidSlot(result, value)) return NoCoverPair;
            (int, int) key = (linkRefIndex[value.Link], value.Slot);
            if (!writer.CoverPairs.TryGetValue(key, out int pairIndex))
            {
                pairIndex = level.CoverIndexPairs.Count;
                if (pairIndex >= NoCoverPair)
                    throw new InvalidOperationException("The level has more cover slot references than ME3 can pack (65535).");
                level.CoverIndexPairs.Add(new CoverIndexPair { CoverIndexIdx = (uint)linkRefIndex[value.Link], SlotIdx = (byte)value.Slot });
                writer.CoverPairs[key] = pairIndex;
            }
            return (ushort)pairIndex;
        }

        ExportEntry Resolve(GeneratedNavTarget target) => target.IsNode
            ? (uint)target.Node < pathExports.Count ? pathExports[target.Node] : null
            : IsValidSlot(result, target.Slot) ? markerGroups[target.Slot.Link][target.Slot.Slot] : null;

        // Slots.
        for (int linkIndex = 0; linkIndex < result.CoverLinks.Count; linkIndex++)
        {
            GeneratedCoverLink generatedLink = result.CoverLinks[linkIndex];
            Matrix4x4 worldToLink = ActorUtils.InverseRotation(linkRotations[linkIndex]);
            var slotProperties = new List<StructProperty>(generatedLink.Slots.Count);
            for (int slotIndex = 0; slotIndex < generatedLink.Slots.Count; slotIndex++)
            {
                GeneratedCoverSlot slot = generatedLink.Slots[slotIndex];
                Vector3 slotLocation = slot.Position + Vector3.UnitZ * NavigationGenerator.CoverSlotHeightAboveFloor;
                Vector3 localOffset = Vector3.TransformNormal(slotLocation - linkLocations[linkIndex], worldToLink);
                int relativeYaw = NormalizeYaw(Rotator.FromDirectionVector(Flatten(slot.Facing)).Yaw - linkRotations[linkIndex].Yaw);
                ExportEntry mantleTarget = (uint)slot.MantleTargetLink < coverLinks.Count &&
                                           IsValidSlot(result, new GeneratedCoverRef(slot.MantleTargetLink, slot.MantleTargetSlot))
                    ? coverLinks[slot.MantleTargetLink]
                    : null;
                var slip = new List<(ExportEntry Marker, int Direction)>();
                if (slot.SlipLeft is { } slipLeft && Resolve(GeneratedNavTarget.ForSlot(slipLeft.Link, slipLeft.Slot)) is { } leftMarker)
                    slip.Add((leftMarker, -1));
                if (slot.SlipRight is { } slipRight && Resolve(GeneratedNavTarget.ForSlot(slipRight.Link, slipRight.Slot)) is { } rightMarker)
                    slip.Add((rightMarker, 1));

                var fireLinks = slot.FireLinks.Where(fire => IsValidSlot(result, fire.Target))
                    .Select(fire => CreateFireLink(Pair(fire.Target), fire.IsFallback)).ToList();
                var exposed = slot.Exposures.Where(exposure => IsValidSlot(result, exposure.Target))
                    .Select(exposure => (exposure.Scale << 16) | Pair(exposure.Target)).ToList();
                var danger = new List<int>(slot.Dangers.Count);
                foreach (GeneratedDanger item in slot.Dangers)
                {
                    if (Resolve(item.Target) is { } navigation && writer.NavRefIndex.TryGetValue(navigation.UIndex, out int navRef) &&
                        navRef < NoCoverPair)
                        danger.Add((item.Cost << 16) | navRef);
                }

                slotProperties.Add(CreateGame3CoverSlot(package, localOffset, relativeYaw,
                    markerGroups[linkIndex][slotIndex], slot, mantleTarget, fireLinks, exposed, danger, slip,
                    PackTargets(Pair(slot.SwatTurnLeft), Pair(slot.SwatTurnRight)),
                    PackTargets(Pair(slot.CoverTurnLeft), Pair(slot.CoverTurnRight))));
            }
            coverLinks[linkIndex].WriteProperty(new ArrayProperty<StructProperty>(slotProperties, "Slots"));
        }

        // ReachSpecs.
        var pathLists = new Dictionary<ExportEntry, List<ObjectProperty>>();
        void AddSpec(ExportEntry start, ExportEntry end, string className, int sizeIndex, byte specDirection,
            bool walkFlags, bool hasDirection, bool addToOctree = true)
        {
            if (start is null || end is null || ReferenceEquals(start, end)) return;
            ExportEntry spec = CreateReachSpec(writer, start, end, className, sizeIndex, specDirection, walkFlags,
                hasDirection, addToOctree);
            if (!pathLists.TryGetValue(start, out List<ObjectProperty> list))
                pathLists[start] = list = [];
            list.Add(new ObjectProperty(spec));
        }

        foreach (GeneratedNavigationEdge edge in result.Edges)
        {
            if ((uint)edge.StartNode >= pathExports.Count || (uint)edge.EndNode >= pathExports.Count)
                continue;
            AddSpec(pathExports[edge.StartNode], pathExports[edge.EndNode], "ReachSpec", edge.SizeIndex, 0, true, true);
        }
        foreach (GeneratedCoverConnection connection in connections)
        {
            ExportEntry start = connection.FromLink >= 0
                ? (uint)connection.FromLink < coverLinks.Count ? coverLinks[connection.FromLink] : null
                : Resolve(connection.From);
            ExportEntry end = Resolve(connection.To);
            switch (connection.Kind)
            {
                case GeneratedCoverConnectionKind.Walk:
                case GeneratedCoverConnectionKind.LinkToNode:
                    AddSpec(start, end, "ReachSpec", connection.SizeIndex, 0, true, true);
                    break;
                case GeneratedCoverConnectionKind.LinkToSlot:
                    AddSpec(start, end, "ReachSpec", connection.SizeIndex, 0, true, true, addToOctree: false);
                    break;
                case GeneratedCoverConnectionKind.SlotToSlot:
                    AddSpec(start, end, "SlotToSlotReachSpec", connection.SizeIndex, connection.Direction, true, true);
                    break;
                case GeneratedCoverConnectionKind.SwatTurn:
                    AddSpec(start, end, "SwatTurnReachSpec", connection.SizeIndex, connection.Direction, true, true);
                    break;
                case GeneratedCoverConnectionKind.CoverSlip:
                    AddSpec(start, end, "CoverSlipReachSpec", 0, connection.Direction, false, false);
                    break;
                case GeneratedCoverConnectionKind.CoverTurn:
                    AddSpec(start, end, "CoverTurnReachSpec", 0, connection.Direction, false, false);
                    break;
                case GeneratedCoverConnectionKind.Mantle:
                    AddSpec(start, end, "MantleReachSpec", 0, 0, false, false);
                    break;
            }
        }
        // Existing navigation points (spawn points, player starts) joined to the generated network.
        var joinedExisting = new List<ExportEntry>();
        if (existingNavigationPoints is { Count: > 0 })
        {
            foreach (GeneratedExistingConnection connection in result.ExistingConnections)
            {
                if ((uint)connection.Existing >= existingNavigationPoints.Count ||
                    (uint)connection.Node >= pathExports.Count ||
                    existingNavigationPoints[connection.Existing] is not { } existing ||
                    !ReferenceEquals(existing.FileRef, package))
                    continue;
                if (connection.ToNode)
                    AddSpec(existing, pathExports[connection.Node], "ReachSpec", connection.SizeIndex, 0, true, true);
                if (connection.FromNode)
                    AddSpec(pathExports[connection.Node], existing, "ReachSpec", connection.SizeIndex, 0, true, true);
                if (!joinedExisting.Contains(existing))
                    joinedExisting.Add(existing);
            }
            foreach (ExportEntry existing in joinedExisting)
            {
                if (writer.NavRefIndex.ContainsKey(existing.UIndex)) continue;
                writer.NavRefIndex[existing.UIndex] = level.NavRefs.Count;
                level.NavRefIndicies.Add(level.NavRefs.Count);
                level.NavRefs.Add(existing.UIndex);
            }
        }

        foreach ((ExportEntry start, List<ObjectProperty> specs) in pathLists)
        {
            ArrayProperty<ObjectProperty> pathList = start.GetProperty<ArrayProperty<ObjectProperty>>("PathList")
                                                     ?? new ArrayProperty<ObjectProperty>("PathList");
            foreach (ObjectProperty spec in specs) pathList.Add(spec);
            start.WriteProperty(pathList);
        }

        AssignNetworkIds(writer, [.. navigationChain, .. joinedExisting], pathLists);
        levelExport.WriteBinary(level);
        return new NavigationSerializationResult(pathExports.Count, writer.ReachSpecCount,
            coverLinks.Count, coverSlotCount);
    }

    private static bool IsValidSlot(NavigationGenerationResult result, GeneratedCoverRef reference) =>
        (uint)reference.Link < result.CoverLinks.Count &&
        (uint)reference.Slot < result.CoverLinks[reference.Link].Slots.Count;

    private static Vector3 Flatten(Vector3 facing)
    {
        facing.Z = 0;
        return facing.LengthSquared() < 0.0001f ? Vector3.UnitX : Vector3.Normalize(facing);
    }

    private static int NormalizeYaw(int yaw)
    {
        yaw %= 65536;
        if (yaw > 32768) yaw -= 65536;
        if (yaw < -32768) yaw += 65536;
        return yaw;
    }

    private static int PackTargets(ushort left, ushort right) =>
        left == NoCoverPair && right == NoCoverPair ? -1 : (int)(((uint)right << 16) | left);

    /// <summary>Basic connections for hand-built results that carry no generator connection data.</summary>
    private static List<GeneratedCoverConnection> DeriveBasicCoverConnections(NavigationGenerationResult result)
    {
        var connections = new List<GeneratedCoverConnection>();
        for (int linkIndex = 0; linkIndex < result.CoverLinks.Count; linkIndex++)
        {
            GeneratedCoverLink link = result.CoverLinks[linkIndex];
            for (int slotIndex = 0; slotIndex < link.Slots.Count; slotIndex++)
            {
                GeneratedCoverSlot slot = link.Slots[slotIndex];
                GeneratedNavTarget self = GeneratedNavTarget.ForSlot(linkIndex, slotIndex);
                connections.Add(new GeneratedCoverConnection(default, self, GeneratedCoverConnectionKind.LinkToSlot,
                    FromLink: linkIndex));
                if ((uint)slot.NearestNavigationNode < result.Nodes.Count)
                {
                    GeneratedNavTarget node = GeneratedNavTarget.ForNode(slot.NearestNavigationNode);
                    connections.Add(new GeneratedCoverConnection(self, node, GeneratedCoverConnectionKind.Walk));
                    connections.Add(new GeneratedCoverConnection(node, self, GeneratedCoverConnectionKind.Walk));
                }
                if (slotIndex > 0)
                    connections.Add(new GeneratedCoverConnection(self, GeneratedNavTarget.ForSlot(linkIndex, slotIndex - 1),
                        GeneratedCoverConnectionKind.SlotToSlot, Direction: 1));
                if (slotIndex + 1 < link.Slots.Count)
                    connections.Add(new GeneratedCoverConnection(self, GeneratedNavTarget.ForSlot(linkIndex, slotIndex + 1),
                        GeneratedCoverConnectionKind.SlotToSlot, Direction: 2));
                if (IsValidSlot(result, new GeneratedCoverRef(slot.MantleTargetLink, slot.MantleTargetSlot)))
                    connections.Add(new GeneratedCoverConnection(self,
                        GeneratedNavTarget.ForSlot(slot.MantleTargetLink, slot.MantleTargetSlot), GeneratedCoverConnectionKind.Mantle));
            }
        }
        return connections;
    }

    #region Actors

    private static ExportEntry CreateNavigationActor(Writer writer, string className, Vector3 location,
        Rotator? rotation, int sizeIndex)
    {
        IMEPackage package = writer.Package;
        ExportEntry actor = ExportCreator.CreateExport(package, className, className, writer.LevelExport,
            createWithStack: true);
        actor.ObjectFlags |= UnrealFlags.EObjectFlags.Transactional |
                             UnrealFlags.EObjectFlags.LoadForClient |
                             UnrealFlags.EObjectFlags.LoadForServer |
                             UnrealFlags.EObjectFlags.LoadForEdit;
        ExportEntry cylinder = ExportCreator.CreateExport(package, "CylinderComponent", "CylinderComponent",
            actor, prePropBinary: new byte[8]);
        cylinder.ObjectFlags |= UnrealFlags.EObjectFlags.Transactional |
                                UnrealFlags.EObjectFlags.LoadForClient |
                                UnrealFlags.EObjectFlags.LoadForServer |
                                UnrealFlags.EObjectFlags.LoadForEdit;
        // Cooked navigation components are instances of the class's own template, which supplies the cylinder size.
        package.GetEntryOrAddImport($"Engine.Default__{className}", className, "Engine");
        cylinder.Archetype = package.GetEntryOrAddImport($"Engine.Default__{className}.CollisionCylinder",
            "CylinderComponent", "Engine");
        cylinder.WriteProperties([new ObjectProperty(0, "ReplacementPrimitive")]);

        NavigationPathSize size = writer.Sizes[Math.Clamp(sizeIndex, 0, writer.Sizes.Count - 1)];
        var properties = new PropertyCollection
        {
            new ArrayProperty<ObjectProperty>("PathList")
        };
        if (GetVolumes(writer, location) is { Count: > 0 } volumes)
            properties.Add(new ArrayProperty<StructProperty>(volumes, "Volumes"));
        properties.Add(CommonStructs.GuidProp(Guid.NewGuid(), "NavGuid"));
        properties.Add(new ObjectProperty(cylinder, "CylinderComponent"));
        properties.Add(CreateCylinder(size.Radius, size.Height, "MaxPathSize"));
        properties.Add(CommonStructs.Vector3Prop(location, "location"));
        if (rotation is { } value && (value.Pitch != 0 || value.Yaw != 0 || value.Roll != 0))
            properties.Add(CommonStructs.RotatorProp(value, "Rotation"));
        properties.Add(new NameProperty(className, "Tag"));
        properties.Add(new ObjectProperty(cylinder, "CollisionComponent"));
        actor.WriteProperties(properties);
        return actor;
    }

    private static StructProperty CreateCylinder(float radius, float height, string name) =>
        new("Cylinder", [new FloatProperty(radius, "Radius"), new FloatProperty(height, "Height")], name);

    private static StructProperty CreateCoverInfo(ExportEntry link, int slotIndex, string name) =>
        new("CoverInfo", [new ObjectProperty(link, "Link"), new IntProperty(slotIndex, "SlotIdx")], name, true);

    /// <summary>
    /// SFXCombatZones the AI's CovGoal_CombatZones constraint tests against. Cooked navigation lists the zones it
    /// stands in under Volumes (with the zone's CombatZoneGuid).
    /// </summary>
    private static void CollectCombatZones(Writer writer)
    {
        foreach (int actorIndex in writer.Level.Actors)
        {
            if (actorIndex <= 0 || !writer.Package.IsUExport(actorIndex)) continue;
            ExportEntry actor = writer.Package.GetUExport(actorIndex);
            if (!actor.IsA("SFXCombatZone")) continue;
            PropertyCollection properties = actor.GetProperties();
            if (properties.GetProp<ObjectProperty>("BrushComponent") is not { Value: > 0 } componentReference ||
                !writer.Package.IsUExport(componentReference.Value))
                continue;
            ExportEntry component = writer.Package.GetUExport(componentReference.Value);
            if (component.GetProperty<StructProperty>("BrushAggGeom")?.GetProp<ArrayProperty<StructProperty>>("ConvexElems")
                is not { Count: > 0 } elements)
                continue;
            Matrix4x4 localToWorld = ActorUtils.GetLocalToWorld(actor);
            var hulls = new List<List<(Vector3 Point, Vector3 Normal)>>();
            foreach (StructProperty element in elements)
            {
                ArrayProperty<IntProperty> indices = element.GetProp<ArrayProperty<IntProperty>>("FaceTriData");
                ArrayProperty<StructProperty> vertexData = element.GetProp<ArrayProperty<StructProperty>>("VertexData");
                if (indices is null || vertexData is not { Count: > 0 }) continue;
                Vector3[] vertices = vertexData.Select(vertex => Vector3.Transform(CommonStructs.GetVector3(vertex), localToWorld)).ToArray();
                Vector3 centroid = vertices.Aggregate(Vector3.Zero, (sum, vertex) => sum + vertex) / vertices.Length;
                var planes = new List<(Vector3, Vector3)>();
                for (int index = 0; index + 2 < indices.Count; index += 3)
                {
                    if ((uint)indices[index].Value >= vertices.Length || (uint)indices[index + 1].Value >= vertices.Length ||
                        (uint)indices[index + 2].Value >= vertices.Length) continue;
                    Vector3 a = vertices[indices[index].Value];
                    Vector3 normal = Vector3.Cross(vertices[indices[index + 1].Value] - a, vertices[indices[index + 2].Value] - a);
                    if (normal.LengthSquared() < 0.0001f) continue;
                    normal = Vector3.Normalize(normal);
                    if (Vector3.Dot(centroid - a, normal) > 0f) normal = -normal;
                    planes.Add((a, normal));
                }
                if (planes.Count >= 4) hulls.Add(planes);
            }
            if (hulls.Count == 0) continue;
            Guid guid = properties.GetProp<StructProperty>("CombatZoneGuid") is { } guidProperty
                ? CommonStructs.GetGuid(guidProperty)
                : Guid.Empty;
            writer.Volumes.Add((actor, guid, point => hulls.Any(hull =>
                hull.All(plane => Vector3.Dot(point - plane.Point, plane.Normal) <= 1f))));
        }
    }

    private static List<StructProperty> GetVolumes(Writer writer, Vector3 location)
    {
        var volumes = new List<StructProperty>();
        foreach ((ExportEntry actor, Guid guid, Func<Vector3, bool> contains) in writer.Volumes)
        {
            if (contains(location))
                volumes.Add(CreateActorReference(actor, guid, "Volumes"));
        }
        return volumes;
    }

    private static StructProperty CreateActorReference(ExportEntry actor, Guid guid, string name) =>
        new("ActorReference", [CommonStructs.GuidProp(guid, "Guid"), new ObjectProperty(actor, "Actor")], name, true);

    #endregion

    #region Cover slots

    private static StructProperty CreateFireLink(ushort pair, bool fallback) =>
        new("FireLink",
        [
            new ArrayProperty<ByteProperty>("Interactions"),
            new IntProperty(pair, "PackedProperties_CoverPairRefAndDynamicInfo"),
            new BoolProperty(fallback, "bFallbackLink"),
            new BoolProperty(false, "bDynamicIndexInited")
        ], "FireLinks", true);

    private static StructProperty CreateGame3CoverSlot(IMEPackage package, Vector3 localOffset, int relativeYaw,
        ExportEntry marker, GeneratedCoverSlot slot, ExportEntry mantleTargetLink, List<StructProperty> fireLinks,
        List<int> exposed, List<int> danger, List<(ExportEntry Marker, int Direction)> slipTargets,
        int swatTurnTargets, int coverTurnTargets)
    {
        PropertyCollection properties = GlobalUnrealObjectInfo.getDefaultStructValue(
            package.Game, "CoverSlot", stripTransients: true, package)
            ?? throw new InvalidOperationException("Could not load the CoverSlot layout for this game.");
        ArrayProperty<EnumProperty> actions = properties.GetProp<ArrayProperty<EnumProperty>>("Actions")
            ?? throw new InvalidOperationException("The CoverSlot layout does not define Actions.");
        bool popUp = !slot.IsStanding && slot.PopUp;
        actions.Clear();
        if (slot.LeanLeft) actions.Add(new EnumProperty("CA_LeanLeft", "ECoverAction", package.Game));
        if (slot.LeanRight) actions.Add(new EnumProperty("CA_LeanRight", "ECoverAction", package.Game));
        if (popUp) actions.Add(new EnumProperty("CA_PopUp", "ECoverAction", package.Game));

        if (properties.GetProp<ArrayProperty<StructProperty>>("FireLinks") is { } fireLinkArray)
        {
            fireLinkArray.Clear();
            foreach (StructProperty fireLink in fireLinks) fireLinkArray.Add(fireLink);
        }
        properties.AddOrReplaceProp(new ArrayProperty<IntProperty>(exposed.Select(value => new IntProperty(value)),
            "ExposedCoverPackedProperties"));
        properties.AddOrReplaceProp(new ArrayProperty<IntProperty>(danger.Select(value => new IntProperty(value)),
            "DangerCoverPackedProperties"));
        if (properties.GetProp<ArrayProperty<StructProperty>>("SlipTarget") is { } slipArray)
        {
            slipArray.Clear();
            foreach ((ExportEntry targetMarker, int direction) in slipTargets)
            {
                slipArray.Add(new StructProperty("CoverReference",
                [
                    new IntProperty(0, "SlotIdx"),
                    new IntProperty(direction, "Direction"),
                    CommonStructs.GuidProp(Guid.Empty, "Guid"),
                    new ObjectProperty(targetMarker, "Actor")
                ], null, true));
            }
        }

        properties.AddOrReplaceProp(CommonStructs.Vector3Prop(localOffset, "LocationOffset"));
        properties.AddOrReplaceProp(CommonStructs.RotatorProp(0, relativeYaw, 0, "RotationOffset"));
        properties.AddOrReplaceProp(new ObjectProperty(marker, "SlotMarker"));
        properties.AddOrReplaceProp(new IntProperty(swatTurnTargets, "TurnTargetPackedProperties"));
        properties.AddOrReplaceProp(new IntProperty(coverTurnTargets, "CoverTurnTargetPackedProperties"));
        SetBool(properties, "bLeanLeft", slot.LeanLeft);
        SetBool(properties, "bLeanRight", slot.LeanRight);
        SetBool(properties, "bForceCanPopUp", popUp);
        SetBool(properties, "bCanPopUp", popUp);
        SetBool(properties, "bCanMantle", mantleTargetLink is not null);
        SetBool(properties, "bAllowMantle", true);
        SetBool(properties, "bCanClimbUp", false);
        SetBool(properties, "bForceCanCoverSlip_Left", false);
        SetBool(properties, "bForceCanCoverSlip_Right", false);
        SetBool(properties, "bCanCoverSlip_Left", slot.SlipLeft is not null);
        SetBool(properties, "bCanCoverSlip_Right", slot.SlipRight is not null);
        SetBool(properties, "bCanSwatTurn_Left", slot.SwatTurnLeft is not null);
        SetBool(properties, "bCanSwatTurn_Right", slot.SwatTurnRight is not null);
        SetBool(properties, "bCanCoverTurn_Left", slot.CoverTurnLeft is not null);
        SetBool(properties, "bCanCoverTurn_Right", slot.CoverTurnRight is not null);
        SetBool(properties, "bEnabled", true);
        SetBool(properties, "bAllowPopup", true);
        SetBool(properties, "bAllowCoverSlip", true);
        SetBool(properties, "bAllowSwatTurn", true);
        SetBool(properties, "bAllowCoverTurn", true);
        // Cooked slots are already on the ground; the engine must not re-trace them at load.
        SetBool(properties, "bForceNoGroundAdjust", true);
        if (properties.GetProp<StructProperty>("MantleTarget") is { } mantleTarget)
        {
            mantleTarget.Properties.AddOrReplaceProp(new IntProperty(mantleTargetLink is null ? -1 : slot.MantleTargetSlot, "SlotIdx"));
            mantleTarget.Properties.AddOrReplaceProp(new IntProperty(0, "Direction"));
            mantleTarget.Properties.AddOrReplaceProp(new ObjectProperty(mantleTargetLink, "Actor"));
        }
        string coverType = slot.IsStanding ? "CT_Standing" : "CT_MidLevel";
        properties.AddOrReplaceProp(new EnumProperty(coverType, "ECoverType", package.Game, "ForceCoverType"));
        properties.AddOrReplaceProp(new EnumProperty(coverType, "ECoverType", package.Game, "CoverType"));
        return new StructProperty("CoverSlot", properties, isImmutable: true);
    }

    private static void SetBool(PropertyCollection properties, string name, bool value)
    {
        if (properties.GetProp<BoolProperty>(name) is { } property)
            property.Value = value;
    }

    #endregion

    #region ReachSpecs

    /// <summary>
    /// Creates a ReachSpec with the property set cooked levels carry for its class: walkable specs have Direction,
    /// reachFlags (R_WALK) and the PathColorIndex of their size; slip/turn/mantle specs only the endpoints, distance and
    /// the Common size.
    /// </summary>
    private static ExportEntry CreateReachSpec(Writer writer, ExportEntry start, ExportEntry end, string className,
        int sizeIndex, byte specDirection, bool walkFlags, bool hasDirection, bool addToOctree)
    {
        ExportEntry spec = ExportCreator.CreateExport(writer.Package, className, className, writer.LevelExport);
        spec.ObjectFlags |= UnrealFlags.EObjectFlags.LoadForClient |
                            UnrealFlags.EObjectFlags.LoadForServer |
                            UnrealFlags.EObjectFlags.LoadForEdit;
        Vector3 startLocation = GetLocation(start), endLocation = GetLocation(end);
        Vector3 delta = endLocation - startLocation;
        float distance = delta.Length();
        sizeIndex = Math.Clamp(sizeIndex, 0, writer.Sizes.Count - 1);
        NavigationPathSize size = writer.Sizes[sizeIndex];

        var properties = new PropertyCollection();
        if (specDirection != 0)
            properties.Add(new ByteProperty(specDirection, "SpecDirection"));
        properties.Add(new StructProperty("ActorReference",
            [CommonStructs.GuidProp(Guid.Empty, "Guid"), new ObjectProperty(end, "Actor")], "End", true));
        if (hasDirection)
            properties.Add(CommonStructs.Vector3Prop(distance > 0.001f ? delta / distance : Vector3.Zero, "Direction"));
        properties.Add(new ObjectProperty(start, "Start"));
        properties.Add(new IntProperty((int)MathF.Round(distance), "Distance"));
        properties.Add(new IntProperty((int)size.Radius, "CollisionRadius"));
        properties.Add(new IntProperty((int)size.Height, "CollisionHeight"));
        if (walkFlags)
            properties.Add(new IntProperty(1, "reachFlags"));
        if (!addToOctree)
            properties.Add(new BoolProperty(false, "bAddToNavigationOctree"));
        if (walkFlags && sizeIndex > 0)
            properties.Add(new ByteProperty((byte)sizeIndex, "PathColorIndex"));
        spec.WriteProperties(properties);
        writer.ReachSpecCount++;
        return spec;
    }

    private static Vector3 GetLocation(ExportEntry actor) =>
        actor.GetProperty<StructProperty>("location") is { } location ? CommonStructs.GetVector3(location) : Vector3.Zero;

    /// <summary>
    /// Pathfinding refuses to search between points with different known NetworkIDs, so every connected group of
    /// generated navigation gets one id beyond any already in the level.
    /// </summary>
    private static void AssignNetworkIds(Writer writer, List<ExportEntry> navigation,
        Dictionary<ExportEntry, List<ObjectProperty>> pathLists)
    {
        int nextId = 0;
        foreach (int actorIndex in writer.Level.Actors)
        {
            if (actorIndex > 0 && writer.Package.IsUExport(actorIndex) &&
                writer.Package.GetUExport(actorIndex).GetProperty<IntProperty>("NetworkID") is { } id)
                nextId = Math.Max(nextId, id.Value + 1);
        }

        var index = navigation.Select((actor, position) => (actor, position)).ToDictionary(item => item.actor, item => item.position);
        var adjacency = navigation.Select(_ => new List<int>()).ToArray();
        foreach ((ExportEntry start, List<ObjectProperty> specs) in pathLists)
        {
            if (!index.TryGetValue(start, out int from)) continue;
            foreach (ObjectProperty specReference in specs)
            {
                ExportEntry spec = writer.Package.GetUExport(specReference.Value);
                int endIndex = spec.GetProperty<StructProperty>("End")?.GetProp<ObjectProperty>("Actor")?.Value ?? 0;
                if (endIndex > 0 && index.TryGetValue(writer.Package.GetUExport(endIndex), out int to))
                {
                    adjacency[from].Add(to);
                    adjacency[to].Add(from);
                }
            }
        }
        // Slot markers are part of their link's network even when the link only reaches them one way.
        var ids = Enumerable.Repeat(-1, navigation.Count).ToArray();
        for (int seed = 0; seed < navigation.Count; seed++)
        {
            if (ids[seed] >= 0) continue;
            int id = nextId++;
            var queue = new Queue<int>();
            queue.Enqueue(seed);
            ids[seed] = id;
            while (queue.TryDequeue(out int current))
            {
                foreach (int neighbour in adjacency[current])
                {
                    if (ids[neighbour] >= 0) continue;
                    ids[neighbour] = id;
                    queue.Enqueue(neighbour);
                }
            }
        }
        for (int position = 0; position < navigation.Count; position++)
            navigation[position].WriteProperty(new IntProperty(ids[position], "NetworkID"));
    }

    #endregion

    #region Level lists

    private static void AppendNavigationChain(Level level, IMEPackage package, List<ExportEntry> nodes)
    {
        if (nodes.Count == 0) return;
        for (int index = 0; index + 1 < nodes.Count; index++)
            nodes[index].WriteProperty(new ObjectProperty(nodes[index + 1], "nextNavigationPoint"));
        nodes[^1].RemoveProperty("nextNavigationPoint");

        ExportEntry existingEnd = FindChainEnd(package, level.NavListStart, level.NavListEnd,
            "nextNavigationPoint");
        if (existingEnd is not null)
            existingEnd.WriteProperty(new ObjectProperty(nodes[0], "nextNavigationPoint"));
        else
            level.NavListStart = nodes[0].UIndex;
        level.NavListEnd = nodes[^1].UIndex;
    }

    private static void AppendCoverChain(Level level, IMEPackage package, List<ExportEntry> links)
    {
        if (links.Count == 0) return;
        for (int index = 0; index + 1 < links.Count; index++)
            links[index].WriteProperty(new ObjectProperty(links[index + 1], "NextCoverLink"));
        links[^1].RemoveProperty("NextCoverLink");

        ExportEntry existingEnd = FindChainEnd(package, level.CoverListStart, level.CoverListEnd,
            "NextCoverLink");
        if (existingEnd is not null)
            existingEnd.WriteProperty(new ObjectProperty(links[0], "NextCoverLink"));
        else
            level.CoverListStart = links[0].UIndex;
        level.CoverListEnd = links[^1].UIndex;
    }

    private static ExportEntry FindChainEnd(IMEPackage package, int startIndex, int endIndex, string nextProperty)
    {
        if (endIndex > 0 && package.IsUExport(endIndex))
            return package.GetUExport(endIndex);
        if (startIndex <= 0 || !package.IsUExport(startIndex))
            return null;

        var visited = new HashSet<int>();
        ExportEntry current = package.GetUExport(startIndex);
        while (visited.Add(current.UIndex) &&
               current.GetProperty<ObjectProperty>(nextProperty) is { Value: > 0 } next &&
               package.IsUExport(next.Value))
        {
            current = package.GetUExport(next.Value);
        }
        return current;
    }

    #endregion
}
