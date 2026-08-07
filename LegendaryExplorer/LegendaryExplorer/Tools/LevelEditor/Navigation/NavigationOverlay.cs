using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>Depth-tested 3D visualization of serialized navigation, ReachSpecs, cover runs, and cover facing.</summary>
public sealed class NavigationOverlay : UIElement
{
    private static readonly Vector4 PathNodeColor = new(0.15f, 0.9f, 1f, 0.95f);
    private static readonly Vector4 PathEdgeColor = new(0.2f, 1f, 0.35f, 0.8f);
    private static readonly Vector4 CoverLinkColor = new(1f, 0.62f, 0.08f, 0.95f);
    private static readonly Vector4 StandingCoverColor = new(1f, 0.25f, 0.08f, 0.95f);
    private static readonly Vector4 MidCoverColor = new(1f, 0.9f, 0.1f, 0.95f);

    private readonly List<VisualNode> pathNodes = [];
    private readonly List<VisualEdge> pathEdges = [];
    private readonly List<VisualCoverRun> coverRuns = [];

    private readonly record struct VisualNode(Vector3 Position, float Radius);
    private readonly record struct VisualEdge(Vector3 Start, Vector3 End);
    private readonly record struct VisualCoverSlot(Vector3 Position, Vector3 Facing, bool Standing);
    private sealed record VisualCoverRun(Vector3 LinkPosition, List<VisualCoverSlot> Slots);

    public bool ShowPaths { get; set; }
    public bool ShowCover { get; set; }
    public int PathNodeCount => pathNodes.Count;
    public int CoverSlotCount => coverRuns.Sum(run => run.Slots.Count);

    public void Refresh(IEnumerable<OpenLevelFile> files)
    {
        pathNodes.Clear();
        pathEdges.Clear();
        coverRuns.Clear();
        foreach (OpenLevelFile file in files)
        {
            LoadPackage(file.Package, file.LevelExport);
        }
    }

    private void LoadPackage(IMEPackage package, ExportEntry levelExport)
    {
        Level level;
        try
        {
            level = levelExport.GetBinaryData<Level>();
        }
        catch
        {
            return;
        }

        var nodeLocations = new Dictionary<int, Vector3>();
        foreach (int actorIndex in level.Actors)
        {
            if (!package.IsUExport(actorIndex)) continue;
            ExportEntry actor = package.GetUExport(actorIndex);
            PropertyCollection properties = actor.GetProperties();
            if (properties.GetProp<StructProperty>("NavGuid") is null &&
                properties.GetProp<ArrayProperty<ObjectProperty>>("PathList") is null)
                continue;
            Vector3 location = ReadLocation(properties);
            nodeLocations[actorIndex] = location;
            if (actor.ClassName is not "CoverLink" and not "CoverSlotMarker")
            {
                float radius = properties.GetProp<StructProperty>("MaxPathSize") is { } size
                    ? size.GetProp<FloatProperty>("Radius")?.Value ?? 34f
                    : 34f;
                pathNodes.Add(new VisualNode(location, radius));
            }
        }

        foreach ((int actorIndex, Vector3 start) in nodeLocations)
        {
            ExportEntry actor = package.GetUExport(actorIndex);
            if (actor.GetProperty<ArrayProperty<ObjectProperty>>("PathList") is not { } pathList) continue;
            foreach (ObjectProperty pathReference in pathList)
            {
                if (!package.IsUExport(pathReference.Value)) continue;
                ExportEntry reachSpec = package.GetUExport(pathReference.Value);
                StructProperty end = reachSpec.GetProperty<StructProperty>("End");
                int destination = end?.GetProp<ObjectProperty>(package.Game < MEGame.ME3 ? "Nav" : "Actor")?.Value ?? 0;
                if (nodeLocations.TryGetValue(destination, out Vector3 finish))
                    pathEdges.Add(new VisualEdge(start, finish));
            }
        }

        foreach (int actorIndex in level.Actors)
        {
            if (!package.IsUExport(actorIndex)) continue;
            ExportEntry actor = package.GetUExport(actorIndex);
            if (actor.ClassName != "CoverLink") continue;
            PropertyCollection properties = actor.GetProperties();
            Vector3 linkPosition = ReadLocation(properties);
            Rotator rotation = properties.GetProp<StructProperty>("Rotation") is { } rotationProperty
                ? CommonStructs.GetRotator(rotationProperty)
                : default;
            Matrix4x4 localToWorld = ActorUtils.ComposeLocalToWorld(linkPosition, rotation, Vector3.One);
            var slots = new List<VisualCoverSlot>();
            if (properties.GetProp<ArrayProperty<StructProperty>>("Slots") is { } slotArray)
            {
                foreach (StructProperty slot in slotArray)
                {
                    Vector3 position = slot.GetProp<ObjectProperty>("SlotMarker") is { Value: > 0 } markerProperty &&
                                       package.IsUExport(markerProperty.Value)
                        ? ReadLocation(package.GetUExport(markerProperty.Value).GetProperties())
                        : Vector3.Transform(slot.GetProp<StructProperty>("LocationOffset") is { } offset
                            ? CommonStructs.GetVector3(offset) : Vector3.Zero, localToWorld);
                    Rotator relativeRotation = slot.GetProp<StructProperty>("RotationOffset") is { } relative
                        ? CommonStructs.GetRotator(relative) : default;
                    Vector3 facing = (rotation + relativeRotation).GetDirectionalVector();
                    bool standing = slot.GetProp<EnumProperty>("CoverType")?.Value.Name == "CT_Standing";
                    slots.Add(new VisualCoverSlot(position, Vector3.Normalize(new Vector3(facing.X, facing.Y, 0f)), standing));
                }
            }
            coverRuns.Add(new VisualCoverRun(linkPosition, slots));
        }
    }

    public override void Draw(LevelEditorRenderContext context)
    {
        Vector3 camera = context.Camera.Position;
        const float maximumDistanceSquared = 100000f * 100000f;
        if (ShowPaths)
        {
            foreach (VisualEdge edge in pathEdges)
            {
                if (Vector3.DistanceSquared((edge.Start + edge.End) * 0.5f, camera) <= maximumDistanceSquared)
                    context.Primitives.AddLine(edge.Start + Vector3.UnitZ * 8f,
                        edge.End + Vector3.UnitZ * 8f, PathEdgeColor, 0);
            }
            foreach (VisualNode node in pathNodes)
            {
                if (Vector3.DistanceSquared(node.Position, camera) <= maximumDistanceSquared)
                    DrawNode(context, node.Position, Math.Clamp(node.Radius * 0.3f, 8f, 28f), PathNodeColor);
            }
        }

        if (ShowCover)
        {
            foreach (VisualCoverRun run in coverRuns)
            {
                if (run.Slots.Count == 0) continue;
                context.Primitives.AddLine(run.LinkPosition, run.LinkPosition + Vector3.UnitZ * 70f,
                    CoverLinkColor, 0);
                for (int index = 0; index < run.Slots.Count; index++)
                {
                    VisualCoverSlot slot = run.Slots[index];
                    Vector4 color = slot.Standing ? StandingCoverColor : MidCoverColor;
                    float height = slot.Standing ? 130f : 70f;
                    context.Primitives.AddLine(slot.Position, slot.Position + Vector3.UnitZ * height, color, 0);
                    context.Primitives.AddLine(slot.Position + Vector3.UnitZ * 20f,
                        slot.Position + Vector3.UnitZ * 20f + slot.Facing * 55f, color, 0);
                    if (index > 0)
                        context.Primitives.AddLine(run.Slots[index - 1].Position + Vector3.UnitZ * 12f,
                            slot.Position + Vector3.UnitZ * 12f, CoverLinkColor, 0);
                }
            }
        }
    }

    private static void DrawNode(LevelEditorRenderContext context, Vector3 position, float radius, Vector4 color)
    {
        context.Primitives.AddLine(position - Vector3.UnitX * radius, position + Vector3.UnitX * radius, color, 0);
        context.Primitives.AddLine(position - Vector3.UnitY * radius, position + Vector3.UnitY * radius, color, 0);
        context.Primitives.AddLine(position, position + Vector3.UnitZ * (radius * 2f), color, 0);
        const int segments = 12;
        Vector3 previous = position + Vector3.UnitX * radius;
        for (int index = 1; index <= segments; index++)
        {
            float angle = MathF.PI * 2f * index / segments;
            Vector3 point = position + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * radius;
            context.Primitives.AddLine(previous, point, color, 0);
            previous = point;
        }
    }

    private static Vector3 ReadLocation(PropertyCollection properties) =>
        properties.GetProp<StructProperty>("Location") is { } location
            ? CommonStructs.GetVector3(location)
            : Vector3.Zero;
}

