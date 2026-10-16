using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LegendaryExplorerCore.Scaleform;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class ScaleformBehaviorTests
{
    [TestMethod]
    public void ClickBehaviorChainsExistingHandlerCallsFunctionAndExecutesTrueBranch()
    {
        var graph = new ScaleformBehaviorGraph();
        var click = new ScaleformBehaviorFlow { Name = "Close button", Trigger = ScaleformBehaviorTrigger.ButtonClick, Target = "Menu.Close" };
        var function = new ScaleformBehaviorFlow { Name = "Update label", Trigger = ScaleformBehaviorTrigger.Function, FunctionName = "UpdateLabel" };
        var text = Node(ScaleformBehaviorNodeKind.SetText, "Menu.Label", "Closed");
        function.Nodes.Add(text); function.EntryNodeId = text.Id;
        var condition = Node(ScaleformBehaviorNodeKind.IfEquals, "mode", "close");
        var call = Node(ScaleformBehaviorNodeKind.CallFunction, "UpdateLabel");
        var hide = Node(ScaleformBehaviorNodeKind.Hide, "Menu");
        var move = Node(ScaleformBehaviorNodeKind.SetPosition, "Menu", number: 120.5, number2: -45.25);
        var otherwise = Node(ScaleformBehaviorNodeKind.SetText, "Menu.Label", "Still open");
        click.EntryNodeId = condition.Id; condition.Next = call.Id; condition.FalseNext = otherwise.Id;
        call.Next = hide.Id; hide.Next = move.Id;
        click.Nodes.AddRange([move, otherwise, call, condition, hide]); // Connections, rather than storage order, determine execution.
        graph.Flows.AddRange([click, function]);
        var document = UiMovie();
        ScaleformBehaviorCompiler.Apply(document, graph);
        var vm = new Vm();
        var menu = vm.Object("Menu"); var button = vm.Object("Menu.Close"); var label = vm.Object("Menu.Label");
        vm.Root["mode"] = "close"; int oldCalls = 0;
        button["onRelease"] = new Native((receiver, _) => { Assert.AreSame(button, receiver); oldCalls++; return null; });
        vm.Run(document); vm.Run(document); // Looping the installation frame cannot capture the wrapper as its own predecessor.
        vm.Click(button);
        Assert.AreEqual(1, oldCalls);
        Assert.AreEqual("Closed", label["text"]);
        Assert.AreEqual(false, menu["_visible"]);
        Assert.AreEqual(120.5, menu["_x"]); Assert.AreEqual(-45.25, menu["_y"]);
        vm.Root["mode"] = "open"; vm.Click(button);
        Assert.AreEqual(2, oldCalls); Assert.AreEqual("Still open", label["text"]);
        int replacementCalls = 0;
        label["text"] = "Game replacement label";
        button["onRelease"] = new Native((_, _) => { replacementCalls++; return null; });
        vm.Run(document); vm.Run(document); vm.Click(button);
        Assert.AreEqual(2, oldCalls); Assert.AreEqual(1, replacementCalls);
        Assert.AreEqual("Game replacement label", label["text"]);
        Assert.AreEqual(0, vm.StackDepth);
        var reopened = ScaleformDocument.Parse(document.Compile());
        Assert.AreEqual(2, ScaleformBehaviorCompiler.Read(reopened).Flows.Count);
        byte[] unchanged = reopened.Compile();
        ScaleformBehaviorCompiler.Apply(reopened, ScaleformBehaviorCompiler.Read(reopened));
        Assert.IsFalse(reopened.IsModified);
        CollectionAssert.AreEqual(unchanged, reopened.Compile());
    }

    [TestMethod]
    public void FrameActionsExecuteVariablesAndNativeTimelineMethodsWithBalancedStack()
    {
        var flow = new ScaleformBehaviorFlow { Name = "Movie opens", Trigger = ScaleformBehaviorTrigger.Frame };
        flow.Nodes.AddRange([Node(ScaleformBehaviorNodeKind.SetVariable, "status", "ready"),
            Node(ScaleformBehaviorNodeKind.Play, "Clip"), Node(ScaleformBehaviorNodeKind.Stop, "Clip"),
            Node(ScaleformBehaviorNodeKind.GoToFrame, "Clip", "play", 7), Node(ScaleformBehaviorNodeKind.Show, "Clip")]);
        Connect(flow);
        var document = UiMovie(); ScaleformBehaviorCompiler.Apply(document, new() { Flows = [flow] });
        var vm = new Vm(); var clip = vm.Object("Clip"); var calls = new List<string>();
        clip["play"] = new Native((receiver, args) => { Assert.AreSame(clip, receiver); Assert.AreEqual(0, args.Length); calls.Add("play"); return null; });
        clip["stop"] = new Native((_, args) => { Assert.AreEqual(0, args.Length); calls.Add("stop"); return null; });
        clip["gotoAndPlay"] = new Native((_, args) => { Assert.AreEqual(7, args.Single()); calls.Add("frame"); return null; });
        vm.Run(document);
        Assert.AreEqual("ready", vm.Root["status"]); Assert.AreEqual(true, clip["_visible"]);
        CollectionAssert.AreEqual(new[] { "play", "stop", "frame" }, calls);
        Assert.AreEqual(0, vm.StackDepth);
    }

    [TestMethod]
    public void SaveReopenEditAndRemoveBehaviorsPreserveEveryUnownedRecord()
    {
        byte[] opaque = [5, 4, 3, 2, 1];
        var document = Movie(Tag(1023, opaque), Tag(12, [0]), Tag(39, Join(U16(9), U16(1), Tag(12, [0]), Tag(1), Tag(0))));
        var flow = new ScaleformBehaviorFlow { Trigger = ScaleformBehaviorTrigger.Frame, SpriteId = 9 };
        flow.Nodes.Add(Node(ScaleformBehaviorNodeKind.SetVariable, "hello", "world")); Connect(flow);
        var graph = new ScaleformBehaviorGraph { Flows = [flow] };
        ScaleformBehaviorCompiler.Apply(document, graph);
        var reopened = ScaleformDocument.Parse(document.Compile());
        var read = ScaleformBehaviorCompiler.Read(reopened);
        read.Flows[0].Nodes[0].Value = "edited";
        var clone = read.Clone(); clone.Flows[0].Nodes[0].Value = "clone";
        Assert.AreEqual("edited", read.Flows[0].Nodes[0].Value);
        ScaleformBehaviorCompiler.Apply(reopened, read);
        Assert.AreEqual(1, ScaleformBehaviorCompiler.Read(ScaleformDocument.Parse(reopened.Compile())).Flows.Count);
        ScaleformBehaviorCompiler.Apply(reopened, new());
        var cleaned = ScaleformDocument.Parse(reopened.Compile());
        Assert.AreEqual(0, ScaleformBehaviorCompiler.Read(cleaned).Flows.Count);
        CollectionAssert.AreEqual(opaque, cleaned.Tags[0].Payload.ToArray());
        CollectionAssert.AreEqual(new byte[] { 0 }, cleaned.Tags[1].Payload.ToArray());
        var sprite = cleaned.Tags.Single(t => t.Code == 39);
        Assert.AreEqual(3, sprite.Children.Count);
        CollectionAssert.AreEqual(new byte[] { 0 }, sprite.Children[0].Payload.ToArray());
    }

    [TestMethod]
    public void DamagedOwnedBytecodeIsNotSilentlyOverwritten()
    {
        var document = Movie();
        var flow = new ScaleformBehaviorFlow { Trigger = ScaleformBehaviorTrigger.Frame };
        ScaleformBehaviorCompiler.Apply(document, new() { Flows = [flow] });
        var actions = document.Tags.Single(t => t.Code == 12);
        byte[] changed = actions.Payload.ToArray();
        // Insert a real, structurally valid Stop action without changing the graph's metadata.
        actions.SetActionBytes([.. changed[..^1], 7, 0]);
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformBehaviorCompiler.Read(document));
        byte[] before = document.Compile();
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformBehaviorCompiler.Apply(document, new()));
        CollectionAssert.AreEqual(before, document.Compile());
    }

    [TestMethod]
    public void InvalidCyclesConnectionsNamesAndAs3LeaveMovieUntouched()
    {
        var document = Movie(); byte[] before = document.Compile();
        var flow = new ScaleformBehaviorFlow { Trigger = ScaleformBehaviorTrigger.Frame };
        var node = Node(ScaleformBehaviorNodeKind.Hide, "Menu"); flow.Nodes.Add(node); flow.EntryNodeId = node.Id; node.Next = node.Id;
        var graph = new ScaleformBehaviorGraph { Flows = [flow] };
        Assert.IsTrue(ScaleformBehaviorCompiler.Validate(document, graph).Any(e => e.Contains("loop")));
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformBehaviorCompiler.Apply(document, graph));
        node.Next = "gone";
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformBehaviorCompiler.Apply(document, graph));
        node.Next = ""; node.Target = "Menu[0]";
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformBehaviorCompiler.Apply(document, graph));
        node.Target = "Menu"; flow.Frame = 2;
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformBehaviorCompiler.Apply(document, graph));
        CollectionAssert.AreEqual(before, document.Compile());
        flow.Frame = 1;
        var as3 = Movie(Tag(69, [8, 0, 0, 0]));
        Assert.IsTrue(ScaleformBehaviorCompiler.Validate(as3, graph).Any(e => e.Contains("ActionScript 3")));
        var function = new ScaleformBehaviorFlow { Trigger = ScaleformBehaviorTrigger.Function, FunctionName = "Repeat" };
        function.Nodes.Add(Node(ScaleformBehaviorNodeKind.CallFunction, "Repeat")); Connect(function);
        Assert.IsTrue(ScaleformBehaviorCompiler.Validate(document, new() { Flows = [function] }).Any(e => e.Contains("loop")));
    }

    [TestMethod]
    public void NamedInstancesKeepNamesMatricesAndOpaqueClipActionsAcrossRepeatedEdits()
    {
        byte[] suffix = [17, 0, 99, 88, 77, 66];
        var document = Movie(Tag(26, Join([6 | 32 | 64 | 128], U16(1), U16(2), [0], Encoding.UTF8.GetBytes("old\0"), suffix)));
        var tag = document.Tags[0];
        tag.SetPlacementMatrix(ScaleformMatrix.Identity with { OffsetX = 10 });
        tag.SetPlacementName("CloseButton");
        tag.SetPlacementMatrix(ScaleformMatrix.Identity with { OffsetY = 20 });
        var reopened = ScaleformDocument.Parse(document.Compile());
        var placement = ScaleformPlacement.Read(reopened.Tags[0]);
        Assert.AreEqual("CloseButton", placement.InstanceName);
        Assert.AreEqual(20.0, placement.Matrix.Value.OffsetY);
        CollectionAssert.AreEqual(suffix, reopened.Tags[0].Payload.Span[^suffix.Length..].ToArray());
    }

    [TestMethod]
    public void NewButtonElementCompilesARealClickFlowAndMissingOrLateTargetsAreRejected()
    {
        var document = Movie();
        var element = document.AddElementDefinition(new() { Kind = ScaleformElementKind.Button, Name = "NewButton" });
        document.AddPlacement(element.CharacterId, ScaleformMatrix.Identity).SetPlacementName("NewButton");
        var click = new ScaleformBehaviorFlow { Trigger = ScaleformBehaviorTrigger.ButtonClick, Target = "NewButton" };
        click.Nodes.Add(Node(ScaleformBehaviorNodeKind.SetVariable, "clicked", "yes")); Connect(click);
        var graph = new ScaleformBehaviorGraph { Flows = [click] };
        ScaleformBehaviorCompiler.Apply(document, graph);
        var reopened = ScaleformDocument.Parse(document.Compile());
        var vm = new Vm(); var button = vm.Object("NewButton"); vm.Run(reopened); vm.Click(button);
        Assert.AreEqual("yes", vm.Root["clicked"]);
        click.Target = "MisspelledButton";
        Assert.IsTrue(ScaleformBehaviorCompiler.Validate(document, graph).Any(e => e.Contains("does not exist")));
        byte[] lateBytes = Join(Encoding.ASCII.GetBytes("GFX"), [8], new byte[4],
            [8, 0, 0, 24, 2, 0], Clip(5), Tag(1), Place(5, 1, "LateButton"), Tag(1), Tag(0));
        BinaryPrimitives.WriteUInt32LittleEndian(lateBytes.AsSpan(4), (uint)lateBytes.Length);
        var late = ScaleformDocument.Parse(lateBytes);
        click.Target = "LateButton";
        Assert.IsTrue(ScaleformBehaviorCompiler.Validate(late, graph).Any(e => e.Contains("installation frame 1")));
        click.Frame = 2;
        Assert.AreEqual(0, ScaleformBehaviorCompiler.Validate(late, graph).Count);
    }

    private static ScaleformBehaviorNode Node(ScaleformBehaviorNodeKind kind, string target, string value = "", double number = 0, double number2 = 0)
        => new() { Kind = kind, Target = target, Value = value, Number = number, Number2 = number2 };
    private static void Connect(ScaleformBehaviorFlow flow)
    {
        flow.EntryNodeId = flow.Nodes.FirstOrDefault()?.Id ?? "";
        for (int i = 0; i < flow.Nodes.Count - 1; i++) flow.Nodes[i].Next = flow.Nodes[i + 1].Id;
    }
    private static ScaleformDocument Movie(params byte[][] records)
    {
        byte[] body = Join([8, 0, 0, 24, 1, 0], Join(records), Tag(1), Tag(0));
        return ScaleformDocument.Parse(Join(Encoding.ASCII.GetBytes("GFX"), [8], BitConverter.GetBytes(body.Length + 8), body));
    }
    private static ScaleformDocument UiMovie() => Movie(Clip(2), Tag(37, Join(U16(3), [8, 0, 128, 0, 0, 0])),
        Clip(1, Place(2, 1, "Close"), Place(3, 2, "Label")), Clip(4), Place(1, 1, "Menu"), Place(4, 2, "Clip"));
    private static byte[] Clip(ushort id, params byte[][] children) => Tag(39, Join(U16(id), U16(1), Join(children), Tag(1), Tag(0)));
    private static byte[] Place(ushort id, ushort depth, string name) => Tag(26, Join([38], U16(depth), U16(id), [0], Encoding.UTF8.GetBytes(name + "\0")));
    private static byte[] Tag(ushort code, byte[] payload = null)
    {
        payload ??= []; bool wide = payload.Length >= 63;
        return Join(U16((ushort)(code << 6 | (wide ? 63 : payload.Length))), wide ? BitConverter.GetBytes(payload.Length) : [], payload);
    }
    private static byte[] U16(ushort n) => BitConverter.GetBytes(n);
    private static byte[] Join(params byte[][] arrays) => arrays.SelectMany(x => x).ToArray();

    // Small independent executable AVM1 model: tests assert observable state and receiver/stack
    // semantics, rather than only asserting that expected opcode names were emitted.
    private delegate object Native(Dictionary<string, object> receiver, object[] args);
    private sealed record Function(byte[] Body);
    private sealed class Vm
    {
        public Dictionary<string, object> Root { get; } = [];
        private readonly Stack<object> stack = new();
        public int StackDepth => stack.Count;
        public Dictionary<string, object> Object(string path)
        {
            var parent = Root;
            foreach (string part in path.Split('.'))
            {
                if (!parent.TryGetValue(part, out object found)) parent[part] = found = new Dictionary<string, object>();
                parent = (Dictionary<string, object>)found;
            }
            return parent;
        }
        public void Run(ScaleformDocument document)
        {
            foreach (var tag in document.Tags.Where(t => t.Code == 12)) Execute(tag.Payload.ToArray(), Root);
        }
        public void Click(Dictionary<string, object> button) => Invoke(button["onRelease"], button, []);
        private object Get(string name, Dictionary<string, object> receiver)
        {
            if (name == "this") return receiver;
            if (name == "_root") return Root;
            object parent = Root;
            foreach (string part in name.Replace("_root.", "").Split('.'))
                parent = parent is Dictionary<string, object> properties && properties.TryGetValue(part, out var value) ? value : null;
            return parent;
        }
        private object Invoke(object function, Dictionary<string, object> receiver, object[] args)
            => function is Native native ? native(receiver, args) : function is Function script ? Execute(script.Body, receiver) : null;
        private object Execute(byte[] bytes, Dictionary<string, object> receiver)
        {
            int offset = 0, count = 0;
            while (offset < bytes.Length)
            {
                Assert.IsTrue(++count < 10000, "Execution must terminate.");
                byte op = bytes[offset++]; int length = op >= 0x80 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset)) : 0;
                if (op >= 0x80) offset += 2;
                int start = offset; offset += length;
                switch (op)
                {
                    case 0: return null;
                    case 0x96:
                        int p = start; byte type = bytes[p++]; object value;
                        switch (type)
                        {
                            case 0: int end = Array.IndexOf(bytes, (byte)0, p); value = Encoding.UTF8.GetString(bytes, p, end - p); break;
                            case 3: value = null; break;
                            case 5: value = bytes[p] != 0; break;
                            case 7: value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p)); break;
                            case 6: value = BitConverter.ToDouble(Join(bytes[(p + 4)..(p + 8)], bytes[p..(p + 4)])); break;
                            default: throw new InvalidDataException("Unexpected push value.");
                        }
                        stack.Push(value); break;
                    case 0x17: stack.Pop(); break;
                    case 0x1C: stack.Push(Get((string)stack.Pop(), receiver)); break;
                    case 0x1D:
                        object assigned = stack.Pop(); string path = (string)stack.Pop(); int dot = path.LastIndexOf('.');
                        ((Dictionary<string, object>)Get(path[..dot], receiver))[path[(dot + 1)..]] = assigned; break;
                    case 0x4E:
                        string key = (string)stack.Pop(); var properties = (Dictionary<string, object>)stack.Pop();
                        stack.Push(properties.TryGetValue(key, out var found) ? found : null); break;
                    case 0x4F:
                        object member = stack.Pop(); string memberName = (string)stack.Pop();
                        ((Dictionary<string, object>)stack.Pop())[memberName] = member; break;
                    case 0x44:
                        object typed = stack.Pop(); stack.Push(typed is Native or Function ? "function" : typed == null ? "undefined" : "object"); break;
                    case 0x49: case 0x66: object right = stack.Pop(); stack.Push(Equals(stack.Pop(), right)); break;
                    case 0x12: stack.Push(!Truth(stack.Pop())); break;
                    case 0x99: offset += BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(start)); break;
                    case 0x9D: if (Truth(stack.Pop())) offset += BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(start)); break;
                    case 0x9B:
                        Assert.AreEqual(0, bytes[start]); Assert.AreEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(start + 1)));
                        int size = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(start + 3));
                        stack.Push(new Function(bytes[offset..(offset + size)])); offset += size; break;
                    case 0x52:
                        string methodName = (string)stack.Pop(); var target = (Dictionary<string, object>)stack.Pop(); int argCount = Convert.ToInt32(stack.Pop());
                        object[] args = new object[argCount]; for (int a = 0; a < argCount; a++) args[a] = stack.Pop();
                        stack.Push(Invoke(target.TryGetValue(methodName, out var method) ? method : null, target, args)); break;
                    case 0x3E: return stack.Pop();
                    default: throw new InvalidDataException($"Unexpected action {op:X2}.");
                }
            }
            return null;
        }
        private static bool Truth(object value) => value != null && !Equals(value, false) && !Equals(value, 0) && !Equals(value, "");
    }
}
