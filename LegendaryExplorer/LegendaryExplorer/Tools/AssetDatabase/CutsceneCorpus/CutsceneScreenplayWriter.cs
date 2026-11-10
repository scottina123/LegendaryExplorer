using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace LegendaryExplorer.Tools.AssetDatabase.CutsceneCorpus
{
    /// <summary>
    /// Writes a conversation as a readable screenplay: nodes in branch order from the start nodes, each with its
    /// line, stage direction, measured shots, gestures and links. Ids match nodes.jsonl so agents can jump between them.
    /// </summary>
    internal static class CutsceneScreenplayWriter
    {
        public static void Write(string path, ConversationRecord conversation, ConversationStageRecord context, StageRecord stage)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {conversation.Name}");
            sb.AppendLine($"# id: {conversation.Id}   file: {conversation.File}   dlc: {conversation.Dlc}{(conversation.IsMod ? " (mod)" : "")}");
            sb.AppendLine($"# speakers: {string.Join(", ", conversation.Speakers.Select(s => $"{s.Index}={s.Tag}"))}");
            if (context is not null)
            {
                sb.AppendLine($"# stage: {stage?.Tag ?? context.StageTag} ({context.StageId})   start: {context.StartConversation}");
                sb.AppendLine($"# links: {string.Join("; ", context.Links.Select(pair => $"{pair.Key} = {pair.Value}"))}");
                sb.AppendLine($"# chosen because: {conversation.ChosenStageReason}");
            }
            else
            {
                sb.AppendLine($"# stage: none ({conversation.ChosenStageReason})");
            }
            sb.AppendLine($"# start nodes: {string.Join(", ", conversation.StartNodes)}   ambient: {conversation.IsAmbient}");
            sb.AppendLine();

            Dictionary<string, NodeRecord> nodes = conversation.Nodes.ToDictionary(node => node.Id);
            var visited = new HashSet<string>();
            var queue = new Queue<string>(conversation.StartNodes.Where(nodes.ContainsKey));
            var order = new List<NodeRecord>();
            while (queue.Count > 0)
            {
                string id = queue.Dequeue();
                if (!visited.Add(id) || !nodes.TryGetValue(id, out NodeRecord node))
                {
                    continue;
                }
                order.Add(node);
                foreach (string next in (node.Replies?.Select(reply => reply.Target) ?? []).Concat(node.Next ?? []))
                {
                    if (!visited.Contains(next) && nodes.ContainsKey(next))
                    {
                        queue.Enqueue(next);
                    }
                }
            }
            List<NodeRecord> unreached = conversation.Nodes.Where(node => !visited.Contains(node.Id)).ToList();

            string ownerTag = context?.Links.GetValueOrDefault("Owner");
            foreach (NodeRecord node in order)
            {
                AppendNode(sb, node, ownerTag);
            }
            if (unreached.Count > 0)
            {
                sb.AppendLine("# ---- nodes not reachable from the start nodes ----");
                sb.AppendLine();
                foreach (NodeRecord node in unreached)
                {
                    AppendNode(sb, node, ownerTag);
                }
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static void AppendNode(StringBuilder sb, NodeRecord node, string ownerTag)
        {
            string Name(string tag) => tag == "Owner" && !string.IsNullOrEmpty(ownerTag) ? $"Owner/{ownerTag}" : tag;
            string speaker = (Name(node.Speaker) ?? $"speaker {node.SpeakerIndex}").ToUpperInvariant();
            string listener = node.Listener is null ? "" : $" -> {Name(node.Listener)}";
            var tags = new List<string>();
            if (node.IsStart) tags.Add("start");
            if (node.ReplyType is not null) tags.Add(node.ReplyType.Replace("REPLY_", "").ToLowerInvariant());
            tags.Add($"intimacy {node.CameraIntimacy}");
            if (node.Timeline is not null) tags.Add($"{node.Timeline.Length:0.##}s");
            if (node.Flags is not null) tags.AddRange(node.Flags.Keys);
            sb.AppendLine($"[{node.Id}] {speaker}{listener}   ({string.Join(", ", tags)})");
            sb.AppendLine(string.IsNullOrWhiteSpace(node.Text) ? "  (no line)" : $"  \"{node.Text}\"");
            if (!string.IsNullOrWhiteSpace(node.StageDirection))
            {
                sb.AppendLine($"  direction: {node.StageDirection}");
            }

            if (node.Shots is { Count: > 0 })
            {
                foreach (ShotRecord shot in node.Shots)
                {
                    var parts = new List<string> { $"{shot.Start:0.00}-{shot.End:0.00}s" };
                    if (shot.Size is not null) parts.Add(shot.Size);
                    if (shot.Subject is not null) parts.Add($"on {shot.Subject} ({shot.SubjectRole})");
                    else if (shot.NearestActor is not null) parts.Add($"no actor in frame (nearest {shot.NearestActor})");
                    if (shot.OverShoulderOf is not null) parts.Add($"OTS {shot.OverShoulderOf}");
                    if (shot.TwoShot) parts.Add("two-shot");
                    if (shot.Azimuth is { } azimuth) parts.Add($"az {azimuth:0}");
                    if (shot.Angle is not null && shot.Angle != "eye_level") parts.Add(shot.Angle);
                    if (shot.LineSide is not null) parts.Add($"{shot.LineSide} of line");
                    if (shot.Move is not null && !(shot.Move.Count == 1 && shot.Move[0] == "static")) parts.Add(string.Join("+", shot.Move));
                    parts.Add($"[{shot.Source} {shot.Camera}{(shot.Fov is { } fov ? $" fov {fov:0}" : "")}]");
                    sb.AppendLine($"  shot: {string.Join(" ", parts)}");
                }
            }
            else if (node.Timeline is not null)
            {
                sb.AppendLine("  shot: (no cut in this node; camera carries over)");
            }

            foreach (TrackRecord track in node.Timeline?.Tracks.Where(track => track.Kind == "gesture") ?? [])
            {
                IEnumerable<string> gestures = track.Keys
                    .Select(key => $"{Get(key, "nmGestureAnim") ?? Get(key, "nmPoseAnim") ?? "(none)"}@{key["t"]}")
                    .Take(8);
                sb.AppendLine($"  gestures {track.Actor}: {string.Join(", ", gestures)}");
            }
            foreach (TrackRecord track in node.Timeline?.Tracks.Where(track => track.Kind == "lookat") ?? [])
            {
                IEnumerable<string> lookAts = track.Keys.Select(key => $"{Get(key, "nmFindActor") ?? "none"}@{key["t"]}");
                sb.AppendLine($"  look-at {track.Actor}: {string.Join(", ", lookAts)}");
            }

            if (node.Replies is { Count: > 0 })
            {
                foreach (ReplyLinkRecord reply in node.Replies)
                {
                    string category = reply.Category?.Replace("REPLY_CATEGORY_", "").ToLowerInvariant();
                    sb.AppendLine($"  -> {reply.Target} [{category}] {(reply.Paraphrase is null ? "" : $"\"{reply.Paraphrase}\"")}");
                }
            }
            if (node.Next is { Count: > 0 })
            {
                sb.AppendLine($"  -> {string.Join(", ", node.Next)}");
            }
            sb.AppendLine();
        }

        private static string Get(JsonObject key, string name) => key[name] is JsonValue value ? value.ToString() : null;
    }
}
