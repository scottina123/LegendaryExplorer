using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorer.Tools.InterpEditor;

public enum CameraAnchorMode
{
    ManualOrigin,
    SingleActor,
    MultipleActors,
    StageBoneOrigin
}

public sealed record CameraActorAnchorContext(
    ConversationExtended Conversation,
    DialogueNodeExtended SelectedNode,
    IReadOnlyList<string> ActorTags);

public sealed record ResolvedActorTransform(
    string ActorTag,
    Vector3 Location,
    Vector3 Rotation,
    ExportEntry SourceTrackMove,
    DialogueNodeExtended SourceNode,
    string SourceDescription);

public sealed record ActorSceneStatePath(
    string PathId,
    IReadOnlyList<DialogueNodeExtended> Nodes,
    IReadOnlyDictionary<string, ResolvedActorTransform> ActorTransforms);

public sealed record ActorAnchorResolution(
    CameraOrigin Origin,
    IReadOnlyDictionary<string, ResolvedActorTransform> ActorTransforms,
    ActorSceneStatePath Path);

public static class CameraActorAnchorResolver
{
    private const float PositionTolerance = 1f;
    private const float RotationTolerance = 1f;

    public static ActorAnchorResolution Resolve(ActorSceneStatePath path, IReadOnlyList<string> actorTags,
        string primaryActorTag)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(actorTags);

        ResolvedActorTransform[] actors = actorTags
            .Where(path.ActorTransforms.ContainsKey)
            .Select(tag => path.ActorTransforms[tag])
            .ToArray();
        if (actors.Length == 0)
        {
            return null;
        }

        CameraOrigin origin = actors.Length == 1
            ? new CameraOrigin(actors[0].Location, actors[0].Rotation)
            : BuildSharedOrigin(actors, primaryActorTag);
        return new ActorAnchorResolution(origin, path.ActorTransforms, path);
    }

    public static bool HaveEquivalentTransforms(ActorSceneStatePath left, ActorSceneStatePath right,
        IReadOnlyCollection<string> actorTags)
    {
        foreach (string actorTag in actorTags)
        {
            if (!left.ActorTransforms.TryGetValue(actorTag, out ResolvedActorTransform leftTransform)
                || !right.ActorTransforms.TryGetValue(actorTag, out ResolvedActorTransform rightTransform)
                || Vector3.Distance(leftTransform.Location, rightTransform.Location) > PositionTolerance
                || AngleDifference(leftTransform.Rotation.X, rightTransform.Rotation.X) > RotationTolerance
                || AngleDifference(leftTransform.Rotation.Y, rightTransform.Rotation.Y) > RotationTolerance
                || AngleDifference(leftTransform.Rotation.Z, rightTransform.Rotation.Z) > RotationTolerance)
            {
                return false;
            }
        }

        return true;
    }

    public static IReadOnlyList<string> GetDifferingActors(IReadOnlyList<ActorSceneStatePath> paths,
        IReadOnlyCollection<string> actorTags)
    {
        if (paths.Count < 2)
        {
            return [];
        }

        return actorTags.Where(actorTag => paths.Skip(1).Any(path =>
                !HaveEquivalentTransformsForActor(paths[0], path, actorTag)))
            .ToArray();
    }

    private static CameraOrigin BuildSharedOrigin(IReadOnlyList<ResolvedActorTransform> actors,
        string primaryActorTag)
    {
        Vector3 center = actors.Aggregate(Vector3.Zero, (sum, actor) => sum + actor.Location) / actors.Count;
        ResolvedActorTransform primary = actors.FirstOrDefault(actor =>
            string.Equals(actor.ActorTag, primaryActorTag, StringComparison.OrdinalIgnoreCase)) ?? actors[0];
        Vector3 arrangement = center - primary.Location;
        arrangement.Z = 0;

        Vector3 primaryForward = ForwardFromRotation(primary.Rotation);
        primaryForward.Z = 0;
        if (primaryForward.LengthSquared() <= float.Epsilon)
        {
            primaryForward = Vector3.UnitX;
        }
        else
        {
            primaryForward = Vector3.Normalize(primaryForward);
        }

        Vector3 forward;
        if (arrangement.LengthSquared() <= float.Epsilon)
        {
            Vector3 averageForward = actors.Aggregate(Vector3.Zero,
                (sum, actor) => sum + ForwardFromRotation(actor.Rotation));
            averageForward.Z = 0;
            forward = averageForward.LengthSquared() <= float.Epsilon
                ? primaryForward
                : Vector3.Normalize(averageForward);
        }
        else
        {
            arrangement = Vector3.Normalize(arrangement);
            Vector3 perpendicular = new(-arrangement.Y, arrangement.X, 0);
            forward = Vector3.Dot(perpendicular, primaryForward) >= 0 ? perpendicular : -perpendicular;
        }

        float yaw = MathF.Atan2(forward.Y, forward.X) * (180f / MathF.PI);
        return new CameraOrigin(center, new Vector3(0, 0, yaw));
    }

    private static bool HaveEquivalentTransformsForActor(ActorSceneStatePath left, ActorSceneStatePath right,
        string actorTag) =>
        left.ActorTransforms.TryGetValue(actorTag, out ResolvedActorTransform leftTransform)
        && right.ActorTransforms.TryGetValue(actorTag, out ResolvedActorTransform rightTransform)
        && Vector3.Distance(leftTransform.Location, rightTransform.Location) <= PositionTolerance
        && AngleDifference(leftTransform.Rotation.X, rightTransform.Rotation.X) <= RotationTolerance
        && AngleDifference(leftTransform.Rotation.Y, rightTransform.Rotation.Y) <= RotationTolerance
        && AngleDifference(leftTransform.Rotation.Z, rightTransform.Rotation.Z) <= RotationTolerance;

    private static Vector3 ForwardFromRotation(Vector3 rotation)
    {
        float pitch = rotation.Y * (MathF.PI / 180f);
        float yaw = rotation.Z * (MathF.PI / 180f);
        float cosPitch = MathF.Cos(pitch);
        return new Vector3(cosPitch * MathF.Cos(yaw), cosPitch * MathF.Sin(yaw), MathF.Sin(pitch));
    }

    private static float AngleDifference(float left, float right)
    {
        float difference = MathF.Abs((left - right) % 360f);
        return difference > 180f ? 360f - difference : difference;
    }
}

public static class CameraActorSceneStateResolver
{
    public static IReadOnlyList<ActorSceneStatePath> ResolvePaths(CameraActorAnchorContext context,
        IReadOnlyCollection<string> actorTags, CameraOrigin? trackAnchorOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(actorTags);

        string[] tags = actorTags.Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var paths = new List<ActorSceneStatePath>();
        foreach (IReadOnlyList<DialogueNodeExtended> nodes in GetIncomingPaths(context.Conversation, context.SelectedNode))
        {
            var transforms = new Dictionary<string, ResolvedActorTransform>(StringComparer.OrdinalIgnoreCase);
            foreach (string actorTag in tags)
            {
                ResolvedActorTransform transform = null;
                foreach (DialogueNodeExtended node in nodes.TakeWhile(node => !ReferenceEquals(node, context.SelectedNode)))
                {
                    if (TryResolveNodeTrackMove(node, actorTag, trackAnchorOrigin,
                            out ResolvedActorTransform nodeTransform))
                    {
                        transform = nodeTransform;
                    }
                }

                transform ??= ResolveInitialTransform(context.Conversation.Export.FileRef, actorTag);
                if (transform is not null)
                {
                    transforms.Add(actorTag, transform);
                }
            }

            paths.Add(new ActorSceneStatePath(GetPathId(nodes), nodes, transforms));
        }

        return paths;
    }

    public static IReadOnlyList<IReadOnlyList<DialogueNodeExtended>> GetIncomingPaths(
        ConversationExtended conversation, DialogueNodeExtended selectedNode)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(selectedNode);

        var incoming = conversation.EntryList.Concat(conversation.ReplyList)
            .ToDictionary(GetNodeKey, _ => new List<DialogueNodeExtended>());
        foreach (DialogueNodeExtended node in conversation.EntryList.Concat(conversation.ReplyList))
        {
            foreach (DialogueNodeExtended target in GetOutgoingNodes(conversation, node))
            {
                if (incoming.TryGetValue(GetNodeKey(target), out List<DialogueNodeExtended> sources))
                {
                    sources.Add(node);
                }
            }
        }

        var paths = new List<IReadOnlyList<DialogueNodeExtended>>();
        TraceIncoming(selectedNode, incoming, [], [], paths);
        return paths;
    }

    private static void TraceIncoming(DialogueNodeExtended node,
        IReadOnlyDictionary<(bool IsReply, int Index), List<DialogueNodeExtended>> incoming,
        HashSet<(bool IsReply, int Index)> visited, List<DialogueNodeExtended> reversePath,
        List<IReadOnlyList<DialogueNodeExtended>> paths)
    {
        var key = GetNodeKey(node);
        if (!visited.Add(key))
        {
            AddForwardPath(reversePath, paths);
            return;
        }

        reversePath.Add(node);
        if (!incoming.TryGetValue(key, out List<DialogueNodeExtended> sources) || sources.Count == 0)
        {
            AddForwardPath(reversePath, paths);
        }
        else
        {
            foreach (DialogueNodeExtended source in sources)
            {
                TraceIncoming(source, incoming, new HashSet<(bool IsReply, int Index)>(visited),
                    new List<DialogueNodeExtended>(reversePath), paths);
            }
        }
    }

    private static void AddForwardPath(List<DialogueNodeExtended> reversePath,
        List<IReadOnlyList<DialogueNodeExtended>> paths)
    {
        reversePath.Reverse();
        paths.Add(reversePath);
    }

    private static IEnumerable<DialogueNodeExtended> GetOutgoingNodes(ConversationExtended conversation,
        DialogueNodeExtended node)
    {
        if (node.IsReply)
        {
            return node.NodeProp.GetProp<ArrayProperty<IntProperty>>("EntryList")?
                .Select(property => property.Value)
                .Where(index => index >= 0 && index < conversation.EntryList.Count)
                .Select(index => conversation.EntryList[index]) ?? [];
        }

        return node.NodeProp.GetProp<ArrayProperty<StructProperty>>("ReplyListNew")?
            .Select(reply => reply.GetProp<IntProperty>("nIndex")?.Value ?? -1)
            .Where(index => index >= 0 && index < conversation.ReplyList.Count)
            .Select(index => conversation.ReplyList[index]) ?? [];
    }

    private static (bool IsReply, int Index) GetNodeKey(DialogueNodeExtended node) =>
        (node.IsReply, node.NodeCount);

    private static bool TryResolveNodeTrackMove(DialogueNodeExtended node, string actorTag,
        CameraOrigin? trackAnchorOrigin, out ResolvedActorTransform transform)
    {
        transform = null;
        if (node.InterpData is null)
        {
            return false;
        }

        var groupReferences = node.InterpData.GetProperty<ArrayProperty<ObjectProperty>>("InterpGroups");
        if (groupReferences is null)
        {
            return false;
        }

        foreach (ExportEntry group in groupReferences
                     .Where(reference => node.InterpData.FileRef.IsUExport(reference.Value))
                     .Select(reference => node.InterpData.FileRef.GetUExport(reference.Value)))
        {
            string groupActorTag = group.GetProperty<NameProperty>("m_nmSFXFindActor")?.Value.Instanced;
            if (!string.Equals(groupActorTag, actorTag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var trackReferences = group.GetProperty<ArrayProperty<ObjectProperty>>("InterpTracks");
            if (trackReferences is null)
            {
                continue;
            }

            foreach (ExportEntry trackMove in trackReferences
                         .Where(reference => group.FileRef.IsUExport(reference.Value))
                         .Select(reference => group.FileRef.GetUExport(reference.Value))
                         .Where(track => track.ClassName == "InterpTrackMove")
                         .Reverse())
            {
                if (TryReadLastTrackMoveKey(trackMove, out Vector3 location, out Vector3 rotation))
                {
                    var trackOrigin = new CameraOrigin(location, rotation);
                    EInterpTrackMoveFrame moveFrame = trackMove.GetProperty<EnumProperty>("MoveFrame")
                        .GetEnumValOrDefault(EInterpTrackMoveFrame.IMF_World);
                    CameraOrigin resolvedOrigin = moveFrame switch
                    {
                        EInterpTrackMoveFrame.IMF_AnchorObject when trackAnchorOrigin is { } anchor =>
                            InterpTrackMoveTransform.ToWorld(anchor, trackOrigin),
                        _ => trackOrigin,
                    };
                    string resolution = moveFrame == EInterpTrackMoveFrame.IMF_AnchorObject
                                        && trackAnchorOrigin.HasValue
                        ? " final key anchored to the conversation stage"
                        : " final key";
                    transform = new ResolvedActorTransform(actorTag, resolvedOrigin.Location,
                        resolvedOrigin.Rotation, trackMove, node,
                        $"{GetNodeLabel(node)} / {group.InstancedFullPath} / {trackMove.ObjectName.Instanced}{resolution}");
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadLastTrackMoveKey(ExportEntry trackMove, out Vector3 location, out Vector3 rotation)
    {
        location = default;
        rotation = default;
        var positionPoints = trackMove.GetProperty<StructProperty>("PosTrack")?
            .GetProp<ArrayProperty<StructProperty>>("Points");
        var rotationPoints = trackMove.GetProperty<StructProperty>("EulerTrack")?
            .GetProp<ArrayProperty<StructProperty>>("Points");
        if (positionPoints is not { Count: > 0 } || rotationPoints is not { Count: > 0 })
        {
            return false;
        }

        StructProperty position = positionPoints[^1].GetProp<StructProperty>("OutVal");
        StructProperty euler = rotationPoints[^1].GetProp<StructProperty>("OutVal");
        if (position is null || euler is null)
        {
            return false;
        }

        location = CommonStructs.GetVector3(position);
        rotation = CommonStructs.GetVector3(euler);
        return true;
    }

    private static ResolvedActorTransform ResolveInitialTransform(IMEPackage package, string actorTag)
    {
        ExportEntry actor = package.Exports.FirstOrDefault(export => GlobalUnrealObjectInfo.IsA(export, "Actor")
            && string.Equals(export.GetProperty<NameProperty>("Tag")?.Value.Instanced, actorTag,
                StringComparison.OrdinalIgnoreCase));
        if (actor is null)
        {
            return null;
        }

        StructProperty locationProperty = actor.GetProperty<StructProperty>("location")
            ?? actor.GetProperty<StructProperty>("Location");
        StructProperty rotationProperty = actor.GetProperty<StructProperty>("Rotation");
        Vector3 location = locationProperty is null ? Vector3.Zero : CommonStructs.GetVector3(locationProperty);
        Vector3 rotation = rotationProperty is null
            ? Vector3.Zero
            : CommonStructs.GetRotator(rotationProperty).GetDegreesVector();
        return new ResolvedActorTransform(actorTag, location, rotation, null, null,
            $"{actor.InstancedFullPath} initial transform");
    }

    private static string GetPathId(IEnumerable<DialogueNodeExtended> nodes) =>
        string.Join(" > ", nodes.Select(GetNodeLabel));

    private static string GetNodeLabel(DialogueNodeExtended node) =>
        $"{(node.IsReply ? 'R' : 'E')}{node.NodeCount}";
}
