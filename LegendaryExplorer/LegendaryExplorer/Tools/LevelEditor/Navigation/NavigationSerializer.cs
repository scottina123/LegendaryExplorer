using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Pathing;
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

/// <summary>Writes generated navigation through the same actor/property/binary structures used by the pathfinding editor.</summary>
public static class NavigationSerializer
{
    private const string ReachSpecClass = "ReachSpec";
    private const string SlotReachSpecClass = "SlotToSlotReachSpec";
    private const string MantleReachSpecClass = "MantleReachSpec";
    private const float StandardNodeRadius = 40f;
    private const float StandardNodeHeight = 95f;
    private const float CoverLinkRadius = 105f;
    private const float CoverLinkHeight = 145f;

    public static NavigationSerializationResult Write(OpenLevelFile file, NavigationGenerationResult result,
        NavigationGenerationSettings settings)
    {
        if (file.IsReadOnly)
            throw new InvalidOperationException("The active level is read-only.");
        NavigationSerializationResult written = Write(file.Package, file.LevelExport, result, settings);
        file.IsDirty = true;
        return written;
    }

    public static NavigationSerializationResult Write(IMEPackage package, ExportEntry levelExport,
        NavigationGenerationResult result, NavigationGenerationSettings settings)
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

        Level level = levelExport.GetBinaryData<Level>();
        float nodeRadius = MathF.Max(settings.PawnRadius, StandardNodeRadius);
        float nodeHeight = MathF.Max(settings.PawnHeight, StandardNodeHeight);
        var pathExports = new List<ExportEntry>(result.Nodes.Count);
        foreach (GeneratedNavigationNode node in result.Nodes)
        {
            pathExports.Add(CreateNavigationActor(package, levelExport, "PathNode", node.Position,
                default, nodeRadius, nodeHeight));
        }

        var coverLinks = new List<ExportEntry>(result.CoverLinks.Count);
        var markerGroups = new List<List<ExportEntry>>(result.CoverLinks.Count);
        int coverSlotCount = 0;
        foreach (GeneratedCoverLink generatedLink in result.CoverLinks)
        {
            Rotator linkRotation = Rotator.FromDirectionVector(generatedLink.Facing);
            ExportEntry link = CreateNavigationActor(package, levelExport, "CoverLink", generatedLink.Position,
                linkRotation, MathF.Max(settings.PawnRadius, CoverLinkRadius),
                MathF.Max(settings.PawnHeight, CoverLinkHeight));
            var markers = new List<ExportEntry>(generatedLink.Slots.Count);
            for (int slotIndex = 0; slotIndex < generatedLink.Slots.Count; slotIndex++)
            {
                GeneratedCoverSlot generatedSlot = generatedLink.Slots[slotIndex];
                Rotator slotRotation = Rotator.FromDirectionVector(generatedSlot.Facing);
                ExportEntry marker = CreateNavigationActor(package, levelExport, "CoverSlotMarker",
                    generatedSlot.Position, slotRotation, nodeRadius, nodeHeight);
                marker.WriteProperty(CreateCoverInfo(link, slotIndex, "OwningSlot"));
                marker.WriteProperty(new ObjectProperty(link, "Owner"));
                markers.Add(marker);
                coverSlotCount++;
            }
            coverLinks.Add(link);
            markerGroups.Add(markers);
        }

        for (int linkIndex = 0; linkIndex < result.CoverLinks.Count; linkIndex++)
        {
            GeneratedCoverLink generatedLink = result.CoverLinks[linkIndex];
            ExportEntry link = coverLinks[linkIndex];
            Rotator linkRotation = Rotator.FromDirectionVector(generatedLink.Facing);
            var slotProperties = new List<StructProperty>(generatedLink.Slots.Count);
            for (int slotIndex = 0; slotIndex < generatedLink.Slots.Count; slotIndex++)
            {
                GeneratedCoverSlot generatedSlot = generatedLink.Slots[slotIndex];
                Rotator slotRotation = Rotator.FromDirectionVector(generatedSlot.Facing);
                Vector3 localOffset = Vector3.TransformNormal(generatedSlot.Position - generatedLink.Position,
                    ActorUtils.InverseRotation(linkRotation));
                int relativeYaw = slotRotation.Yaw - linkRotation.Yaw;
                ExportEntry mantleTarget = IsValidMantleTarget(result.CoverLinks, generatedSlot)
                    ? coverLinks[generatedSlot.MantleTargetLink]
                    : null;
                slotProperties.Add(CreateGame3CoverSlot(package, localOffset, relativeYaw,
                    markerGroups[linkIndex][slotIndex], generatedSlot, mantleTarget));
            }
            link.WriteProperty(new ArrayProperty<StructProperty>(slotProperties, "Slots"));
        }

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
        }
        foreach (ExportEntry link in coverLinks)
        {
            if (!level.CoverLinkRefs.Contains(link.UIndex)) level.CoverLinkRefs.Add(link.UIndex);
        }
        levelExport.WriteBinary(level);

        int reachSpecStart = package.Exports.Count(IsReachSpec);
        foreach (GeneratedNavigationEdge edge in result.Edges)
        {
            if ((uint)edge.StartNode >= pathExports.Count || (uint)edge.EndNode >= pathExports.Count)
                continue;
            PathTools.CreateReachSpec(pathExports[edge.StartNode], false, pathExports[edge.EndNode],
                ReachSpecClass, settings.PawnRadius, settings.PawnHeight);
        }

        for (int linkIndex = 0; linkIndex < result.CoverLinks.Count; linkIndex++)
        {
            GeneratedCoverLink generatedLink = result.CoverLinks[linkIndex];
            ExportEntry link = coverLinks[linkIndex];
            List<ExportEntry> markers = markerGroups[linkIndex];
            for (int slotIndex = 0; slotIndex < generatedLink.Slots.Count; slotIndex++)
            {
                GeneratedCoverSlot slot = generatedLink.Slots[slotIndex];
                ExportEntry marker = markers[slotIndex];
                if ((uint)slot.NearestNavigationNode < pathExports.Count)
                {
                    PathTools.CreateReachSpec(marker, true, pathExports[slot.NearestNavigationNode],
                        ReachSpecClass, settings.PawnRadius, settings.PawnHeight);
                }
                PathTools.CreateReachSpec(link, true, marker, ReachSpecClass,
                    settings.PawnRadius, settings.PawnHeight);
                if (slotIndex + 1 < markers.Count)
                {
                    PathTools.CreateReachSpec(marker, true, markers[slotIndex + 1], SlotReachSpecClass,
                        settings.PawnRadius, settings.PawnHeight);
                }
                if (IsValidMantleTarget(result.CoverLinks, slot) &&
                    (linkIndex < slot.MantleTargetLink ||
                     linkIndex == slot.MantleTargetLink && slotIndex < slot.MantleTargetSlot))
                {
                    ExportEntry targetMarker = markerGroups[slot.MantleTargetLink][slot.MantleTargetSlot];
                    PathTools.CreateReachSpec(marker, true, targetMarker, MantleReachSpecClass,
                        settings.PawnRadius, settings.PawnHeight);
                }
            }
        }

        int reachSpecEnd = package.Exports.Count(IsReachSpec);
        return new NavigationSerializationResult(pathExports.Count, reachSpecEnd - reachSpecStart,
            coverLinks.Count, coverSlotCount);
    }

    private static ExportEntry CreateNavigationActor(IMEPackage package, ExportEntry levelExport,
        string className, Vector3 location, Rotator rotation, float radius, float height)
    {
        ExportEntry actor = ExportCreator.CreateExport(package, className, className, levelExport,
            createWithStack: true);
        actor.ObjectFlags |= UnrealFlags.EObjectFlags.Transactional |
                             UnrealFlags.EObjectFlags.LoadForClient |
                             UnrealFlags.EObjectFlags.LoadForServer |
                             UnrealFlags.EObjectFlags.LoadForEdit;
        ExportEntry cylinder = ExportCreator.CreateExport(package, "CylinderComponent", "CylinderComponent",
            actor, prePropBinary: new byte[8]);
        cylinder.ObjectFlags |= UnrealFlags.EObjectFlags.Transactional;
        cylinder.WriteProperties([
            new FloatProperty(radius, "CollisionRadius"),
            new FloatProperty(height, "CollisionHeight"),
            new ObjectProperty(0, "ReplacementPrimitive")
        ]);

        actor.WriteProperties([
            new ArrayProperty<ObjectProperty>("PathList"),
            new ObjectProperty(cylinder, "CylinderComponent"),
            CreateCylinder(radius, height, "MaxPathSize"),
            CommonStructs.GuidProp(Guid.NewGuid(), "NavGuid"),
            new NameProperty(className, "Tag"),
            CommonStructs.Vector3Prop(location, "Location"),
            CommonStructs.RotatorProp(rotation, "Rotation"),
            new ObjectProperty(cylinder, "CollisionComponent")
        ]);
        return actor;
    }

    private static StructProperty CreateCylinder(float radius, float height, string name) =>
        new("Cylinder", [new FloatProperty(radius, "Radius"), new FloatProperty(height, "Height")], name);

    private static bool IsReachSpec(ExportEntry export) =>
        export.ClassName.EndsWith("ReachSpec", StringComparison.OrdinalIgnoreCase);

    private static StructProperty CreateCoverInfo(ExportEntry link, int slotIndex, string name) =>
        new("CoverInfo", [new ObjectProperty(link, "Link"), new IntProperty(slotIndex, "SlotIdx")], name, true);

    private static StructProperty CreateGame3CoverSlot(IMEPackage package, Vector3 localOffset, int relativeYaw,
        ExportEntry marker, GeneratedCoverSlot slot, ExportEntry mantleTargetLink)
    {
        PropertyCollection properties = GlobalUnrealObjectInfo.getDefaultStructValue(
            package.Game, "CoverSlot", stripTransients: true, package)
            ?? throw new InvalidOperationException("Could not load the CoverSlot layout for this game.");
        ArrayProperty<EnumProperty> actions = properties.GetProp<ArrayProperty<EnumProperty>>("Actions")
            ?? throw new InvalidOperationException("The CoverSlot layout does not define Actions.");
        actions.Clear();
        if (slot.LeanLeft) actions.Add(new EnumProperty("CA_LeanLeft", "ECoverAction", package.Game));
        if (slot.LeanRight) actions.Add(new EnumProperty("CA_LeanRight", "ECoverAction", package.Game));
        if (!slot.IsStanding) actions.Add(new EnumProperty("CA_PopUp", "ECoverAction", package.Game));

        properties.AddOrReplaceProp(CommonStructs.Vector3Prop(localOffset, "LocationOffset"));
        properties.AddOrReplaceProp(CommonStructs.RotatorProp(0, relativeYaw, 0, "RotationOffset"));
        properties.AddOrReplaceProp(new ObjectProperty(marker, "SlotMarker"));
        SetBool(properties, "bLeanLeft", slot.LeanLeft);
        SetBool(properties, "bLeanRight", slot.LeanRight);
        SetBool(properties, "bForceCanPopUp", false);
        SetBool(properties, "bCanPopUp", !slot.IsStanding);
        SetBool(properties, "bCanMantle", mantleTargetLink is not null);
        SetBool(properties, "bAllowMantle", true);
        SetBool(properties, "bCanClimbUp", false);
        SetBool(properties, "bForceCanCoverSlip_Left", false);
        SetBool(properties, "bForceCanCoverSlip_Right", false);
        SetBool(properties, "bCanCoverSlip_Left", slot.LeanLeft);
        SetBool(properties, "bCanCoverSlip_Right", slot.LeanRight);
        SetBool(properties, "bCanSwatTurn_Left", slot.LeanLeft);
        SetBool(properties, "bCanSwatTurn_Right", slot.LeanRight);
        SetBool(properties, "bCanCoverTurn_Left", slot.LeanLeft);
        SetBool(properties, "bCanCoverTurn_Right", slot.LeanRight);
        SetBool(properties, "bEnabled", true);
        if (mantleTargetLink is not null && properties.GetProp<StructProperty>("MantleTarget") is { } mantleTarget)
        {
            mantleTarget.Properties.AddOrReplaceProp(new IntProperty(slot.MantleTargetSlot, "SlotIdx"));
            mantleTarget.Properties.AddOrReplaceProp(new IntProperty(0, "Direction"));
            mantleTarget.Properties.AddOrReplaceProp(new ObjectProperty(mantleTargetLink, "Actor"));
        }
        string coverType = slot.IsStanding ? "CT_Standing" : "CT_MidLevel";
        properties.AddOrReplaceProp(new EnumProperty(coverType, "ECoverType", package.Game, "ForceCoverType"));
        properties.AddOrReplaceProp(new EnumProperty(coverType, "ECoverType", package.Game, "CoverType"));
        return new StructProperty("CoverSlot", properties, isImmutable: true);
    }

    private static bool IsValidMantleTarget(IReadOnlyList<GeneratedCoverLink> links, GeneratedCoverSlot slot) =>
        (uint)slot.MantleTargetLink < links.Count &&
        (uint)slot.MantleTargetSlot < links[slot.MantleTargetLink].Slots.Count;

    private static void SetBool(PropertyCollection properties, string name, bool value)
    {
        if (properties.GetProp<BoolProperty>(name) is { } property)
            property.Value = value;
    }

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
}
