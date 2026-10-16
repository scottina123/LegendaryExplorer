using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LegendaryExplorerCore.Scaleform;

/// <summary>
/// Compiles visual flows to AVM1, retaining unrelated scripts verbatim. This deliberately bounded
/// language is not an ActionScript source compiler or an emulator of game-side Unreal callbacks.
/// Opcode operands follow Adobe SWF v19, chapter 5 (DefineFunction, CallMethod, members, If/Jump).
/// </summary>
public static class ScaleformBehaviorCompiler
{
    private const string Marker = "LegendaryExplorer.VisualBehavior/1:";
    private const string FunctionPrefix = "__lex_behavior_function_";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex Identifier = new(@"^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Path = new(@"^(?:_root\.)?[A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*)*$", RegexOptions.CultureInvariant);

    /// <summary>Loads only verified, editor-owned action records; existing hand-written scripts stay opaque.</summary>
    public static ScaleformBehaviorGraph Read(ScaleformDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var graph = new ScaleformBehaviorGraph();
        foreach (var (tag, _, _, _) in Records(document.Tags))
            if (TryRead(tag, out var flow)) graph.Flows.Add(flow);
        return graph;
    }

    /// <summary>Returns friendly errors without modifying the movie. Values and graph sizes are bounded.</summary>
    public static IReadOnlyList<string> Validate(ScaleformDocument document, ScaleformBehaviorGraph graph)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(graph);
        var errors = new List<string>();
        if (graph.Flows == null) return ["The behavior list is missing."];
        if (graph.Flows.Count > 128) return ["A movie can contain at most 128 visual behaviors."];
        if (graph.Flows.Count > 0 && document.Version < 6) errors.Add("Visual behaviors require SWF/GFX version 6 or later.");
        var tags = Records(document.Tags).Select(x => x.Tag).ToList();
        var targets = new TargetResolver(document);
        if (graph.Flows.Count > 0 && tags.Any(t => t.Code is 72 or 82 || t.Code == 69 && t.Payload.Length > 0 && (t.Payload.Span[0] & 8) != 0))
            errors.Add("This movie uses ActionScript 3. Visual behaviors currently compile ActionScript 1/2 movies only.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var buttons = new HashSet<string>(StringComparer.Ordinal);
        var functions = new Dictionary<string, ScaleformBehaviorFlow>(StringComparer.Ordinal);
        foreach (var flow in graph.Flows)
        {
            if (flow == null) { errors.Add("A behavior is missing."); continue; }
            string label = string.IsNullOrWhiteSpace(flow.Name) ? "Behavior" : flow.Name;
            void Error(string message) => errors.Add($"{label}: {message}");
            if (string.IsNullOrEmpty(flow.Id) || flow.Id.Length > 64 || !flow.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) Error("the behavior ID is invalid.");
            else if (!ids.Add(flow.Id)) Error("behavior IDs must be unique.");
            if (!Enum.IsDefined(flow.Trigger)) Error("choose a supported event.");
            if (flow.Trigger == ScaleformBehaviorTrigger.ButtonClick && !ValidPath(flow.Target, false)) Error("choose a named button or movie clip.");
            else if (flow.Trigger == ScaleformBehaviorTrigger.ButtonClick)
            {
                if (!buttons.Add(Absolute(flow.Target))) Error("this button already has a click behavior. Add more actions to that existing behavior.");
                var target = targets.Resolve(flow.Target, flow.SpriteId.HasValue ? null : flow.Frame);
                if (!target.Exists) Error($"button '{flow.Target}' does not exist on installation frame {flow.Frame}. Choose its creation frame or add the element first.");
                else if (target.Definition != null && target.Definition.Code is not (7 or 34 or 39)) Error("click events need a button or movie clip; add a Button element around this artwork.");
            }
            if (flow.Trigger == ScaleformBehaviorTrigger.Function)
            {
                if (!ValidName(flow.FunctionName)) Error("function names must start with a letter and contain letters, numbers or underscores.");
                else if (!functions.TryAdd(flow.FunctionName, flow)) Error("function names must be unique.");
                if (flow.SpriteId.HasValue || flow.Frame != 1) Error("reusable functions must be created on the main timeline at frame 1.");
            }
            var timeline = flow.SpriteId.HasValue
                ? tags.FirstOrDefault(t => t.Code == 39 && t.Payload.Length >= 4 && BinaryPrimitives.ReadUInt16LittleEndian(t.Payload.Span) == flow.SpriteId)?.Children
                : document.Tags;
            if (timeline == null || flow.Frame < 1 || flow.Frame > timeline.Count(t => t.Code == 1)) Error("choose a frame that exists in this timeline.");
            if (flow.Nodes == null || flow.Nodes.Count > 256) { Error("a behavior must contain at most 256 action nodes."); continue; }
            var nodes = new Dictionary<string, ScaleformBehaviorNode>(StringComparer.Ordinal);
            foreach (var node in flow.Nodes)
            {
                if (node == null) { Error("an action is missing."); continue; }
                if (string.IsNullOrEmpty(node.Id) || node.Id.Length > 80 || !nodes.TryAdd(node.Id, node)) Error("action IDs must be present and unique.");
                if (!Enum.IsDefined(node.Kind)) Error("choose a supported action.");
                if (!double.IsFinite(node.Number) || !double.IsFinite(node.Number2) || Math.Abs(node.Number) > 1_000_000 || Math.Abs(node.Number2) > 1_000_000) Error("action numbers must be finite and between -1,000,000 and 1,000,000.");
                if (!double.IsFinite(node.EditorX) || !double.IsFinite(node.EditorY)) Error("action positions must be finite.");
                if (node.Value?.Contains('\0') == true || (node.Value?.Length ?? 0) > 8000) Error("text values must contain at most 8000 characters and no null character.");
                if (node.Kind is ScaleformBehaviorNodeKind.SetVariable or ScaleformBehaviorNodeKind.IfEquals)
                { if (!ValidPath(node.Target, false) || node.Target == "_root") Error("choose a variable name, such as menuOpen."); }
                else if (node.Kind == ScaleformBehaviorNodeKind.CallFunction)
                { if (!ValidName(node.Target)) Error("choose a named reusable function."); }
                else if (!ValidPath(node.Target, true)) Error("choose a named UI target.");
                else
                {
                    var target = targets.Resolve(node.Target, null);
                    if (!target.Exists) Error($"UI target '{node.Target}' does not exist. Choose an element from the target list.");
                    else if (node.Kind == ScaleformBehaviorNodeKind.SetText && (Absolute(node.Target) == "_root" || target.Definition != null && target.Definition.Code != 37))
                        Error("set text needs an editable text field; choose the button's label or add a Text element.");
                    else if (node.Kind is ScaleformBehaviorNodeKind.Play or ScaleformBehaviorNodeKind.Stop or ScaleformBehaviorNodeKind.GoToFrame &&
                             target.Definition != null && target.Definition.Code != 39)
                        Error("animation actions need a movie clip.");
                }
                if (node.Kind == ScaleformBehaviorNodeKind.GoToFrame && (node.Number < 1 || node.Number > 65535 || node.Number != Math.Truncate(node.Number))) Error("go-to frame must be a whole number between 1 and 65535.");
            }
            if (!string.IsNullOrEmpty(flow.EntryNodeId) && !nodes.ContainsKey(flow.EntryNodeId)) Error("the event is connected to an action that no longer exists.");
            foreach (var node in nodes.Values)
                foreach (string link in new[] { node.Next, node.Kind == ScaleformBehaviorNodeKind.IfEquals ? node.FalseNext : "" })
                    if (!string.IsNullOrEmpty(link) && !nodes.ContainsKey(link)) Error("an action is connected to an action that no longer exists.");
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var active = new HashSet<string>(StringComparer.Ordinal);
            bool Cycle(string id)
            {
                if (string.IsNullOrEmpty(id) || !nodes.TryGetValue(id, out var node)) return false;
                if (active.Contains(id)) return true;
                if (!visited.Add(id)) return false;
                active.Add(id);
                bool result = Cycle(node.Next) || node.Kind == ScaleformBehaviorNodeKind.IfEquals && Cycle(node.FalseNext);
                active.Remove(id);
                return result;
            }
            if (nodes.Keys.Any(Cycle)) Error("actions form a loop. Connect a finite sequence to avoid freezing the game.");
            try
            {
                if (Utf8.GetByteCount(Marker + JsonSerializer.Serialize(flow)) > 60000) Error("this behavior is too large; split it into reusable functions.");
            }
            catch (Exception e) when (e is JsonException or EncoderFallbackException) { Error("text contains invalid Unicode."); }
        }
        foreach (var flow in graph.Flows.Where(f => f?.Nodes != null))
            foreach (var call in flow.Nodes.Where(n => n?.Kind == ScaleformBehaviorNodeKind.CallFunction))
                if (!functions.ContainsKey(call.Target ?? "")) errors.Add($"{flow.Name}: reusable function '{call.Target}' does not exist.");
        var seenFunctions = new HashSet<string>();
        var activeFunctions = new HashSet<string>();
        bool Recursive(string name)
        {
            if (!functions.TryGetValue(name, out var flow)) return false;
            if (flow.Nodes == null) return false;
            if (activeFunctions.Contains(name)) return true;
            if (!seenFunctions.Add(name)) return false;
            activeFunctions.Add(name);
            bool result = flow.Nodes.Where(n => n?.Kind == ScaleformBehaviorNodeKind.CallFunction).Any(n => Recursive(n.Target ?? ""));
            activeFunctions.Remove(name);
            return result;
        }
        if (functions.Keys.Any(Recursive)) errors.Add("Reusable functions call each other in a loop. Remove the recursive connection to avoid freezing the game.");
        if (errors.Count == 0)
            foreach (var flow in graph.Flows)
                try { Compile(flow); }
                catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException) { errors.Add($"{flow.Name}: {e.Message}"); }
        return errors;
    }

    /// <summary>Replaces only verified editor-owned blocks, compiling every block before making changes.</summary>
    public static void Apply(ScaleformDocument document, ScaleformBehaviorGraph graph)
    {
        var errors = Validate(document, graph);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        var owned = Records(document.Tags).Where(record => TryRead(record.Tag, out _)).ToList();
        var compiled = graph.Flows.Select(flow => (Flow: flow, Bytes: Compile(flow)))
            .OrderBy(x => x.Flow.Trigger == ScaleformBehaviorTrigger.Function ? 0 : 1).ToList();
        // Loading an existing graph and applying unrelated visual edits must leave its
        // original action-record headers and compression entirely alone.
        if (owned.Count == compiled.Count && owned.All(record => compiled.Any(candidate =>
                record.SpriteId == candidate.Flow.SpriteId && record.Frame == candidate.Flow.Frame &&
                record.Tag.Payload.Span.SequenceEqual(candidate.Bytes)))) return;
        document.ReplaceVisualBehaviors(owned.Select(x => (x.Tag, x.Sprite)).ToList(), compiled);
    }

    private static bool ValidName(string value) => value is { Length: > 0 and <= 100 } && Identifier.IsMatch(value) && !value.StartsWith("__lex", StringComparison.OrdinalIgnoreCase);
    private static bool ValidPath(string value, bool allowEmpty) => allowEmpty && string.IsNullOrEmpty(value) || value is { Length: > 0 and <= 1024 } && Path.IsMatch(value);
    private static string Absolute(string target) => string.IsNullOrEmpty(target) ? "_root" : target == "_root" || target.StartsWith("_root.", StringComparison.Ordinal) ? target : "_root." + target;

    private sealed class TargetResolver(ScaleformDocument document)
    {
        private readonly Dictionary<(string, int?), (bool Exists, ScaleformTag Definition)> cache = [];
        private readonly Dictionary<(ushort?, string), List<(bool Exists, ScaleformTag Definition)>> children = [];
        private ScaleformScene index;

        public (bool Exists, ScaleformTag Definition) Resolve(string path, int? frame)
        {
            path = Absolute(path);
            if (path == "_root") return (true, null);
            if (cache.TryGetValue((path, frame), out var found)) return found;
            index ??= ScaleformScene.Build(document);
            string[] parts = path[6..].Split('.');
            var candidates = frame.HasValue && frame >= 1 && frame <= document.FrameCount
                ? ScaleformScene.Build(document, frame: frame.Value).Items.Where(item => item.InstanceName == parts[0])
                    .Select(item => (Exists: true, item.Definition)).ToList()
                : NamedChildren(null, parts[0]);
            foreach (string part in parts.Skip(1))
            {
                var next = new List<(bool Exists, ScaleformTag Definition)>();
                foreach (var candidate in candidates)
                {
                    // Shared imported symbols are defined in another movie; their internal instance
                    // hierarchy is deliberately unknown here rather than falsely rejected.
                    if (candidate.Definition == null) { next.Add(candidate); continue; }
                    if (candidate.Definition.Code != 39) continue;
                    ushort id = BinaryPrimitives.ReadUInt16LittleEndian(candidate.Definition.Payload.Span);
                    next.AddRange(NamedChildren(id, part));
                }
                candidates = next;
            }
            found = candidates.FirstOrDefault();
            cache[(path, frame)] = found;
            return found;
        }

        private List<(bool Exists, ScaleformTag Definition)> NamedChildren(ushort? spriteId, string name)
        {
            if (children.TryGetValue((spriteId, name), out var found)) return found;
            var timeline = spriteId.HasValue ? index.Definitions[spriteId.Value].Children : document.Tags;
            int frame = 1;
            found = [];
            foreach (var tag in timeline)
            {
                if (tag.Code == 1) { frame++; continue; }
                if (tag.Code is not (26 or 70)) continue;
                ScaleformPlacement placement;
                try { placement = ScaleformPlacement.Read(tag); }
                catch (InvalidDataException) { continue; }
                if (placement.InstanceName != name) continue;
                ushort? id = placement.CharacterId;
                if (!id.HasValue)
                    id = ScaleformScene.Build(document, spriteId, frame).Items.FirstOrDefault(item => item.InstanceName == name)?.CharacterId;
                if (id.HasValue) found.Add((true, index.Definitions.GetValueOrDefault(id.Value)));
            }
            children[(spriteId, name)] = found;
            return found;
        }
    }

    private static byte[] Compile(ScaleformBehaviorFlow flow)
    {
        var code = new Code();
        code.String(Marker + JsonSerializer.Serialize(flow)); code.Op(0x17);
        if (flow.Trigger == ScaleformBehaviorTrigger.Frame) EmitFlow(code, flow);
        else if (flow.Trigger == ScaleformBehaviorTrigger.Function)
        {
            code.Get("_root"); code.String(FunctionPrefix + flow.FunctionName);
            var function = new Code(); EmitFlow(function, flow); function.Undefined(); function.Op(0x3E);
            code.Function(function.Finish()); code.Op(0x4F);
        }
        else
        {
            string previous = "__lex_previous_" + flow.Id;
            string installed = "__lex_installed_" + flow.Id;
            // Install once per actual runtime instance. Replaying this frame must not wrap
            // ourselves, nor recapture an opaque game handler that may already wrap us.
            // Later game-side handler changes keep their own normal authority.
            code.Get(Absolute(flow.Target)); code.String(installed); code.Op(0x4E); code.Branch(0x9D, "event_done");
            code.Get(Absolute(flow.Target)); code.String(previous);
            code.Get(Absolute(flow.Target)); code.String("onRelease"); code.Op(0x4E); code.Op(0x4F);
            code.Get(Absolute(flow.Target)); code.String("onRelease");
            var handler = new Code();
            handler.Get("this"); handler.String(previous); handler.Op(0x4E); handler.Op(0x44); handler.String("function"); handler.Op(0x49); handler.Op(0x12); handler.Branch(0x9D, "own");
            handler.Integer(0); handler.Get("this"); handler.String(previous); handler.Op(0x52); handler.Op(0x17);
            handler.Label("own"); EmitFlow(handler, flow); handler.Undefined(); handler.Op(0x3E);
            code.Function(handler.Finish()); code.Op(0x4F);
            code.Get(Absolute(flow.Target)); code.String(installed); code.Boolean(true); code.Op(0x4F);
            code.Label("event_done");
        }
        code.Op(0);
        byte[] result = code.Finish();
        ScaleformActionScript.Validate(result);
        return result;
    }

    private static void EmitFlow(Code code, ScaleformBehaviorFlow flow)
    {
        var labels = flow.Nodes.Select((node, i) => (node.Id, Label: "node_" + i)).ToDictionary(x => x.Id, x => x.Label, StringComparer.Ordinal);
        string Link(string id) => string.IsNullOrEmpty(id) ? "flow_end" : labels[id];
        code.Branch(0x99, Link(flow.EntryNodeId));
        foreach (var node in flow.Nodes)
        {
            code.Label(labels[node.Id]);
            switch (node.Kind)
            {
                case ScaleformBehaviorNodeKind.Show: case ScaleformBehaviorNodeKind.Hide:
                    code.Get(Absolute(node.Target)); code.String("_visible"); code.Boolean(node.Kind == ScaleformBehaviorNodeKind.Show); code.Op(0x4F); break;
                case ScaleformBehaviorNodeKind.SetText:
                    code.Get(Absolute(node.Target)); code.String("text"); code.String(node.Value ?? ""); code.Op(0x4F); break;
                case ScaleformBehaviorNodeKind.SetPosition:
                    code.Get(Absolute(node.Target)); code.String("_x"); code.Number(node.Number); code.Op(0x4F);
                    code.Get(Absolute(node.Target)); code.String("_y"); code.Number(node.Number2); code.Op(0x4F); break;
                case ScaleformBehaviorNodeKind.Play: case ScaleformBehaviorNodeKind.Stop:
                    code.Integer(0); code.Get(Absolute(node.Target)); code.String(node.Kind == ScaleformBehaviorNodeKind.Play ? "play" : "stop"); code.Op(0x52); code.Op(0x17); break;
                case ScaleformBehaviorNodeKind.GoToFrame:
                    code.Integer((int)node.Number); code.Integer(1); code.Get(Absolute(node.Target)); code.String(string.Equals(node.Value, "play", StringComparison.OrdinalIgnoreCase) ? "gotoAndPlay" : "gotoAndStop"); code.Op(0x52); code.Op(0x17); break;
                case ScaleformBehaviorNodeKind.SetVariable:
                    code.String(Absolute(node.Target)); code.String(node.Value ?? ""); code.Op(0x1D); break;
                case ScaleformBehaviorNodeKind.CallFunction:
                    code.Integer(0); code.Get("_root"); code.String(FunctionPrefix + node.Target); code.Op(0x52); code.Op(0x17); break;
                case ScaleformBehaviorNodeKind.IfEquals:
                    code.Get(Absolute(node.Target)); code.String(node.Value ?? ""); code.Op(0x49);
                    code.Branch(0x9D, Link(node.Next)); code.Branch(0x99, Link(node.FalseNext)); continue;
            }
            code.Branch(0x99, Link(node.Next));
        }
        code.Label("flow_end");
    }

    private static bool TryRead(ScaleformTag tag, out ScaleformBehaviorFlow flow)
    {
        flow = null;
        if (tag.Code != 12) return false;
        ReadOnlySpan<byte> bytes = tag.Payload.Span;
        if (bytes.Length < 5 || bytes[0] != 0x96 || bytes[3] != 0) return false;
        int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[1..]);
        if (length < 2 || length + 3 >= bytes.Length || bytes[length + 3] != 0x17 || bytes[length + 2] != 0) return false;
        string text;
        try { text = Utf8.GetString(bytes.Slice(4, length - 2)); }
        catch (DecoderFallbackException) { return false; }
        if (!text.StartsWith(Marker, StringComparison.Ordinal)) return false;
        try
        {
            flow = JsonSerializer.Deserialize<ScaleformBehaviorFlow>(text[Marker.Length..]);
            if (flow?.Nodes == null || flow.Nodes.Count > 256 || !bytes.SequenceEqual(Compile(flow)))
                throw new InvalidDataException("An editor-owned visual behavior was changed in the script editor. Preserve or remove that changed action record before editing its graph.");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or NullReferenceException or ArgumentException)
        { throw new InvalidDataException("Visual behavior metadata is damaged. Its script has been preserved; repair or remove that action record before editing behaviors.", e); }
        return true;
    }

    private static IEnumerable<(ScaleformTag Tag, ScaleformTag Sprite, ushort? SpriteId, int Frame)> Records(IReadOnlyList<ScaleformTag> tags, ScaleformTag sprite = null)
    {
        int frame = 1;
        ushort? id = sprite == null ? null : BinaryPrimitives.ReadUInt16LittleEndian(sprite.Payload.Span);
        foreach (var tag in tags)
        {
            yield return (tag, sprite, id, frame);
            if (tag.Code == 39) foreach (var child in Records(tag.Children, tag)) yield return child;
            if (tag.Code == 1) frame++;
        }
    }

    private sealed class Code
    {
        private readonly MemoryStream stream = new();
        private readonly Dictionary<string, int> labels = new(StringComparer.Ordinal);
        private readonly List<(int Offset, string Target)> branches = [];
        public void Op(byte opcode) => stream.WriteByte(opcode);
        public void Label(string name) => labels.Add(name, checked((int)stream.Position));
        private void Action(byte opcode, byte[] payload)
        {
            if (payload.Length > ushort.MaxValue) throw new InvalidDataException("This action is too large.");
            Op(opcode); stream.Write(BitConverter.GetBytes((ushort)payload.Length)); stream.Write(payload);
        }
        public void String(string value) => Action(0x96, [0, .. Utf8.GetBytes(value), 0]);
        public void Integer(int value) => Action(0x96, [7, .. BitConverter.GetBytes(value)]);
        public void Number(double value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Action(0x96, [6, .. bytes[4..8], .. bytes[0..4]]); // AVM1 swaps the IEEE double's two words.
        }
        public void Boolean(bool value) => Action(0x96, [5, (byte)(value ? 1 : 0)]);
        public void Undefined() => Action(0x96, [3]);
        public void Get(string name) { String(name); Op(0x1C); }
        public void Function(byte[] body)
        {
            if (body.Length > ushort.MaxValue) throw new InvalidDataException("This behavior has too many actions; split it into reusable functions.");
            Action(0x9B, [0, 0, 0, .. BitConverter.GetBytes((ushort)body.Length)]);
            stream.Write(body);
        }
        public void Branch(byte opcode, string target)
        {
            Action(opcode, [0, 0]); branches.Add((checked((int)stream.Position - 2), target));
        }
        public byte[] Finish()
        {
            byte[] result = stream.ToArray();
            foreach (var (offset, target) in branches)
            {
                if (!labels.TryGetValue(target, out int destination)) throw new InvalidDataException("A behavior connection is missing.");
                int relative = destination - offset - 2;
                if (relative is < short.MinValue or > short.MaxValue) throw new InvalidDataException("A behavior connection is too far apart; split it into reusable functions.");
                BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(offset), (short)relative);
            }
            return result;
        }
    }
}

public sealed partial class ScaleformDocument
{
    internal void ReplaceVisualBehaviors(List<(ScaleformTag Tag, ScaleformTag Sprite)> previous,
        List<(ScaleformBehaviorFlow Flow, byte[] Bytes)> compiled)
    {
        // All payloads are compiled before this method is entered; only editor-owned records may be removed.
        var records = compiled.Select(x => (x.Flow, Tag: CreateTimelineRecord(12, x.Bytes))).ToList();
        foreach (var (tag, sprite) in previous)
        {
            if (sprite == null) { timelineTags.Remove(tag); timelineModified = true; }
            else sprite.RemoveTimelineRecord(tag);
        }
        foreach (var (flow, tag) in records)
        {
            var scene = ScaleformScene.Build(this, flow.SpriteId, flow.Frame);
            var (_, sprite, insertion) = FindInsertion(scene, flow.SpriteId, flow.Frame);
            InsertRecord(sprite, insertion, tag);
        }
    }
}
