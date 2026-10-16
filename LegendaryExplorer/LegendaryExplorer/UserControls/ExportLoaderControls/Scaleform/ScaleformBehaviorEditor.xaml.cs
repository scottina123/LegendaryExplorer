using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LegendaryExplorerCore.Scaleform;

namespace LegendaryExplorer.UserControls.ExportLoaderControls.Scaleform;

/// <summary>Small executable behavior graphs. Game scripts remain separate from editor-authored flows.</summary>
public partial class ScaleformBehaviorEditor : UserControl
{
    private const double CardWidth = 230;
    private const string EventId = "@event";
    private const string PaletteFormat = "LegendaryExplorer.ScaleformBehavior";
    private readonly Stack<EditorState> undo = new();
    private readonly Stack<EditorState> redo = new();
    private readonly Dictionary<string, Border> cards = new();
    private readonly List<System.Windows.Shapes.Path> wires = new();
    private readonly ScaleTransform zoom = new(1, 1);
    private ScaleformDocument document;
    private ScaleformBehaviorGraph graph;
    private string flowId;
    private string selectedId;
    private bool updating;
    private string[] fieldSnapshot = Array.Empty<string>();
    private string[] targets = Array.Empty<string>();
    private TargetChoice[] targetChoices = Array.Empty<TargetChoice>();
    private Point paletteDown;
    private string connectFrom;
    private bool connectFalse;
    private Point dragStart;
    private Point nodeStart;
    private ScaleformBehaviorNode dragging;
    private EditorState dragBefore;
    private Border dragCard;
    private Point? panStart;
    private Point panOffset;
    private int authoringFrame = 1;

    public event EventHandler DraftChanged;
    public event Action<string> StatusChanged;
    public ScaleformBehaviorGraph Graph => graph;
    public ScaleformBehaviorFlow SelectedFlow => graph?.Flows.FirstOrDefault(x => x.Id == flowId);
    public ScaleformBehaviorNode SelectedNode => SelectedFlow?.Nodes.FirstOrDefault(x => x.Id == selectedId);
    public IReadOnlyList<string> Targets => targets;

    private static readonly PaletteEntry[] Entries =
    {
        new("Button click", "Event · run actions when an element is clicked", Trigger: ScaleformBehaviorTrigger.ButtonClick),
        new("Movie opens", "Event · run actions on a movie frame", Trigger: ScaleformBehaviorTrigger.Frame),
        new("Reusable function", "Event · a named set of actions you can call", Trigger: ScaleformBehaviorTrigger.Function),
        new("Show element", "Make a hidden element visible", ScaleformBehaviorNodeKind.Show),
        new("Hide element", "Make an element invisible", ScaleformBehaviorNodeKind.Hide),
        new("Change text", "Set the words in a text element", ScaleformBehaviorNodeKind.SetText),
        new("Move element", "Set an element's X and Y position", ScaleformBehaviorNodeKind.SetPosition),
        new("Play animation", "Resume a movie or group timeline", ScaleformBehaviorNodeKind.Play),
        new("Stop animation", "Pause a movie or group timeline", ScaleformBehaviorNodeKind.Stop),
        new("Jump to frame", "Choose an animation frame", ScaleformBehaviorNodeKind.GoToFrame),
        new("Remember a value", "Store text for another action to use", ScaleformBehaviorNodeKind.SetVariable),
        new("Call function", "Run one of your reusable functions", ScaleformBehaviorNodeKind.CallFunction),
        new("Check a value", "Choose Yes or No by comparing stored text", ScaleformBehaviorNodeKind.IfEquals)
    };

    public ScaleformBehaviorEditor()
    {
        InitializeComponent();
        Palette.ItemsSource = Entries;
        GraphCanvas.LayoutTransform = zoom;
    }

    /// <summary>The graph is the persistent movie draft and is edited in place.</summary>
    public void LoadMovie(ScaleformDocument movie, ScaleformBehaviorGraph behaviorGraph, IEnumerable<string> availableTargets = null, int authoringFrame = 1)
    {
        ArgumentNullException.ThrowIfNull(movie);
        ArgumentNullException.ThrowIfNull(behaviorGraph);
        bool sameGraph = ReferenceEquals(graph, behaviorGraph);
        document = movie; graph = behaviorGraph; this.authoringFrame = Math.Clamp(authoringFrame, 1, Math.Max(1, (int)movie.FrameCount));
        if (!sameGraph)
        {
            undo.Clear(); redo.Clear();
            flowId = graph.Flows.FirstOrDefault()?.Id;
            selectedId = EventId;
            connectFrom = null;
        }
        RefreshTargets(availableTargets ?? DiscoverTargets(movie));
        RefreshAll();
        SetStatus(graph.IsEmpty ? "Add an event to begin. Existing game scripts stay intact." : "Select a node to choose what it does. Add actions from the left, or connect their circles.");
    }

    public void ClearMovie()
    {
        graph = null; document = null; flowId = selectedId = connectFrom = null;
        undo.Clear(); redo.Clear(); targets = Array.Empty<string>(); targetChoices = Array.Empty<TargetChoice>();
        RefreshAll();
    }

    public void RefreshTargets(IEnumerable<string> availableTargets)
    {
        targets = new[] { "_root" }.Concat(availableTargets ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        targetChoices = targets.Select(path => new TargetChoice(path, TargetLabelFor(path))).ToArray();
        if (graph != null) UpdateInspector();
    }

    /// <summary>Returns real named instances, including those appearing later in a timeline.</summary>
    public static IReadOnlyList<string> DiscoverTargets(ScaleformDocument movie)
    {
        var definitions = ScaleformScene.Build(movie).Definitions;
        var result = new HashSet<string>(StringComparer.Ordinal) { "_root" };
        var ancestry = new HashSet<ushort>();
        int visited = 0;
        void Walk(IReadOnlyList<ScaleformTag> timeline, string prefix, int depth)
        {
            if (depth > 12 || visited > 4000) return;
            var display = new Dictionary<ushort, (ushort? Character, string Name)>();
            foreach (var tag in timeline)
            {
                if (tag.Code is 5 or 28)
                {
                    int offset = tag.Code == 5 ? 2 : 0;
                    if (tag.Payload.Length >= offset + 2) display.Remove(BinaryPrimitives.ReadUInt16LittleEndian(tag.Payload.Span[offset..]));
                    continue;
                }
                if (tag.Code is not (4 or 26 or 70)) continue;
                ScaleformPlacement placement;
                try { placement = ScaleformPlacement.Read(tag); }
                catch (InvalidDataException) { continue; }
                display.TryGetValue(placement.Depth, out var previous);
                if (!placement.IsMove) previous = default;
                var character = placement.CharacterId ?? previous.Character;
                string name = placement.InstanceName ?? previous.Name;
                display[placement.Depth] = (character, name);
                if (string.IsNullOrEmpty(name) || !character.HasValue) continue;
                if (++visited > 4000) break;
                string path = prefix + "." + name;
                if (!result.Add(path)) continue;
                if (definitions.TryGetValue(character.Value, out var definition) && definition.Code == 39 && ancestry.Add(character.Value))
                {
                    Walk(definition.Children, path, depth + 1);
                    ancestry.Remove(character.Value);
                }
            }
        }
        Walk(movie.Tags, "_root", 0);
        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public ScaleformBehaviorFlow AddBehavior(ScaleformBehaviorTrigger trigger, string target = null)
    {
        if (graph == null || !CommitPendingFields()) return null;
        var before = Capture();
        var flow = NewFlow(trigger, target);
        var existing = trigger == ScaleformBehaviorTrigger.ButtonClick ? graph.Flows.FirstOrDefault(f => f.Trigger == trigger && NormalizeTarget(f.Target) == NormalizeTarget(flow.Target)) : null;
        if (existing != null)
        {
            flowId = existing.Id; selectedId = EventId; RefreshAll();
            SetStatus("This button already has a click behavior. Add another action to its connected sequence.");
            return existing;
        }
        graph.Flows.Add(flow); flowId = flow.Id; selectedId = EventId;
        Changed(before, "Choose what this event should control, then add actions from the left.");
        return flow;
    }

    private ScaleformBehaviorFlow NewFlow(ScaleformBehaviorTrigger trigger, string target)
    {
        int number = 1;
        while (graph.Flows.Any(x => x.FunctionName == "MyFunction" + number)) number++;
        string function = "MyFunction" + number;
        return new ScaleformBehaviorFlow
        {
            Id = Guid.NewGuid().ToString("N"), Trigger = trigger,
            Name = trigger switch
            {
                ScaleformBehaviorTrigger.ButtonClick => "When button clicked",
                ScaleformBehaviorTrigger.Function => function,
                _ => "When movie opens"
            },
            Target = target ?? targets.FirstOrDefault(x => x.Contains("button", StringComparison.OrdinalIgnoreCase) || x.Contains("btn", StringComparison.OrdinalIgnoreCase)) ?? targets.FirstOrDefault(x => x != "_root") ?? "_root",
            FunctionName = trigger == ScaleformBehaviorTrigger.Function ? function : "",
            Frame = trigger == ScaleformBehaviorTrigger.ButtonClick ? authoringFrame : 1
        };
    }

    /// <summary>Palette additions automatically follow the selected action when its output is free.</summary>
    public ScaleformBehaviorNode AddAction(ScaleformBehaviorNodeKind kind, double? x = null, double? y = null)
    {
        if (graph == null || !CommitPendingFields()) return null;
        var before = Capture();
        if (SelectedFlow == null)
        {
            var created = NewFlow(ScaleformBehaviorTrigger.Frame, null);
            graph.Flows.Add(created); flowId = created.Id; selectedId = EventId;
        }
        var flow = SelectedFlow;
        var previous = SelectedNode;
        if (previous == null && !string.IsNullOrEmpty(flow.EntryNodeId))
            previous = flow.Nodes.LastOrDefault(n => string.IsNullOrEmpty(n.Next) && n.Kind != ScaleformBehaviorNodeKind.IfEquals);
        double nextX = previous?.EditorX + 290 ?? 340;
        double nextY = previous?.EditorY ?? 55;
        var node = new ScaleformBehaviorNode
        {
            Id = Guid.NewGuid().ToString("N"), Kind = kind,
            Target = kind is ScaleformBehaviorNodeKind.IfEquals or ScaleformBehaviorNodeKind.SetVariable ? "myValue" :
                kind == ScaleformBehaviorNodeKind.CallFunction ? graph.Flows.FirstOrDefault(f => f.Trigger == ScaleformBehaviorTrigger.Function)?.FunctionName ?? "" :
                targets.FirstOrDefault(t => t != "_root") ?? "_root",
            Value = "", Number = kind == ScaleformBehaviorNodeKind.GoToFrame ? 1 : 0,
            EditorX = Math.Max(30, x ?? nextX), EditorY = Math.Max(25, y ?? nextY)
        };
        flow.Nodes.Add(node);
        if (previous != null) { node.Next = previous.Next; previous.Next = node.Id; }
        else if (string.IsNullOrEmpty(flow.EntryNodeId)) flow.EntryNodeId = node.Id;
        selectedId = node.Id;
        Changed(before, "Choose an element and settings on the right. Lines show which actions run next.");
        return node;
    }

    public bool SelectNode(string id)
    {
        if (!CommitPendingFields()) return false;
        if (id != EventId && SelectedFlow?.Nodes.All(n => n.Id != id) != false) return false;
        selectedId = id; RefreshCards(); UpdateInspector(); return true;
    }

    public bool SelectBehavior(string id)
    {
        if (!CommitPendingFields() || graph?.Flows.All(f => f.Id != id) != false) return false;
        flowId = id; selectedId = EventId; connectFrom = null; RefreshAll(); return true;
    }

    /// <summary>A null source means the event output; a null destination disconnects it.</summary>
    public bool Connect(string fromNodeId, string toNodeId, bool falseBranch = false)
    {
        if (SelectedFlow == null || !CommitPendingFields()) return false;
        var flow = SelectedFlow;
        var from = fromNodeId == null || fromNodeId == EventId ? null : flow.Nodes.FirstOrDefault(n => n.Id == fromNodeId);
        if (fromNodeId != null && fromNodeId != EventId && from == null || toNodeId != null && flow.Nodes.All(n => n.Id != toNodeId)) return false;
        if (falseBranch && from?.Kind != ScaleformBehaviorNodeKind.IfEquals) return false;
        string previous = from == null ? flow.EntryNodeId : falseBranch ? from.FalseNext : from.Next;
        if (previous == toNodeId) return true;
        var before = Capture();
        if (from == null) flow.EntryNodeId = toNodeId;
        else if (falseBranch) from.FalseNext = toNodeId;
        else from.Next = toNodeId;
        if (HasCycle(flow))
        {
            if (from == null) flow.EntryNodeId = previous;
            else if (falseBranch) from.FalseNext = previous;
            else from.Next = previous;
            SetStatus("That connection would repeat forever. Choose a different action."); return false;
        }
        connectFrom = null;
        Changed(before, toNodeId == null ? "Link disconnected." : "Connected. Actions run from the event along these lines.");
        return true;
    }

    private static bool HasCycle(ScaleformBehaviorFlow flow)
    {
        var visiting = new HashSet<string>();
        var done = new HashSet<string>();
        var nodes = flow.Nodes.ToDictionary(x => x.Id);
        bool Visit(string id)
        {
            if (string.IsNullOrEmpty(id) || done.Contains(id) || !nodes.TryGetValue(id, out var node)) return false;
            if (!visiting.Add(id)) return true;
            if (Visit(node.Next) || Visit(node.FalseNext)) return true;
            visiting.Remove(id); done.Add(id); return false;
        }
        return flow.Nodes.Any(n => Visit(n.Id));
    }

    public void RemoveSelection()
    {
        if (SelectedFlow == null || !CommitPendingFields()) return;
        var before = Capture(); var flow = SelectedFlow; var node = SelectedNode;
        if (node == null)
        {
            graph.Flows.Remove(flow); flowId = graph.Flows.FirstOrDefault()?.Id; selectedId = EventId;
        }
        else
        {
            if (flow.EntryNodeId == node.Id) flow.EntryNodeId = node.Next;
            foreach (var other in flow.Nodes)
            {
                if (other.Next == node.Id) other.Next = node.Next;
                if (other.FalseNext == node.Id) other.FalseNext = node.Next;
            }
            flow.Nodes.Remove(node); selectedId = EventId;
        }
        connectFrom = null; Changed(before, "Removed. Undo restores it.");
    }

    public void Undo()
    {
        if (graph == null || !CommitPendingFields() || undo.Count == 0) return;
        redo.Push(Capture()); Restore(undo.Pop()); DraftChanged?.Invoke(this, EventArgs.Empty); SetStatus("Undid behavior change.");
    }

    public void Redo()
    {
        if (graph == null || !CommitPendingFields() || redo.Count == 0) return;
        undo.Push(Capture()); Restore(redo.Pop()); DraftChanged?.Invoke(this, EventArgs.Empty); SetStatus("Restored behavior change.");
    }

    private EditorState Capture() => new(graph.Clone(), flowId, selectedId);
    private void Restore(EditorState state)
    {
        graph.Flows.Clear(); graph.Flows.AddRange(state.Graph.Clone().Flows);
        flowId = state.FlowId; selectedId = state.SelectedId; connectFrom = null; RefreshAll();
    }
    private void Changed(EditorState before, string message)
    {
        undo.Push(before); redo.Clear(); RefreshAll(); DraftChanged?.Invoke(this, EventArgs.Empty); SetStatus(message);
    }

    private void SetStatus(string text)
    {
        GraphStatus.Text = text; StatusChanged?.Invoke(text);
    }

    private void RefreshAll()
    {
        updating = true;
        FlowPicker.ItemsSource = graph?.Flows.ToArray(); FlowPicker.SelectedItem = SelectedFlow;
        updating = false;
        RefreshCards(); UpdateInspector();
    }

    private void RefreshCards()
    {
        GraphCanvas.Children.Clear(); cards.Clear(); wires.Clear();
        var flow = SelectedFlow;
        EmptyHint.Visibility = flow == null ? Visibility.Visible : Visibility.Collapsed;
        if (flow == null) return;
        AddCard(EventId, 45, 55, EventTitle(flow), EventSummary(flow), true, false);
        foreach (var node in flow.Nodes)
            AddCard(node.Id, Math.Max(25, node.EditorX), Math.Max(25, node.EditorY), Entry(node.Kind).Name,
                NodeSummary(node), false, node.Kind == ScaleformBehaviorNodeKind.IfEquals);
        GraphCanvas.Width = Math.Max(1100, flow.Nodes.Select(n => n.EditorX + CardWidth + 100).DefaultIfEmpty(1100).Max());
        GraphCanvas.Height = Math.Max(650, flow.Nodes.Select(n => n.EditorY + 250).DefaultIfEmpty(650).Max());
        DrawWires();
    }

    private static string EventTitle(ScaleformBehaviorFlow flow) => flow.Trigger switch
    {
        ScaleformBehaviorTrigger.ButtonClick => "WHEN · Button clicked",
        ScaleformBehaviorTrigger.Function => "FUNCTION · " + flow.FunctionName,
        _ => flow.Frame <= 1 ? "WHEN · Movie opens" : "WHEN · Frame " + flow.Frame
    };
    private static string EventSummary(ScaleformBehaviorFlow flow) => flow.Trigger switch
    {
        ScaleformBehaviorTrigger.ButtonClick => Friendly(flow.Target),
        ScaleformBehaviorTrigger.Function => "Reusable actions · choose Call function to run",
        _ => "Starts this chain of actions"
    };
    private static string Friendly(string target) => string.IsNullOrEmpty(target) || target == "_root" ? "Movie" : target.Split('.').Last();
    private static string NormalizeTarget(string target) => string.IsNullOrEmpty(target) || target == "_root" ? "_root" : target.StartsWith("_root.", StringComparison.Ordinal) ? target : "_root." + target;
    private static string TargetLabelFor(string path)
    {
        if (path == "_root") return "Movie";
        int dot = path.LastIndexOf('.');
        if (dot < 0 || path[..dot] == "_root") return Friendly(path);
        return Friendly(path) + " · " + path[..dot].Replace("_root.", "", StringComparison.Ordinal);
    }
    private static PaletteEntry Entry(ScaleformBehaviorNodeKind kind) => Entries.First(e => e.Kind == kind && e.Trigger == null);
    private static string NodeSummary(ScaleformBehaviorNode node) => node.Kind switch
    {
        ScaleformBehaviorNodeKind.SetText => Friendly(node.Target) + "\n“" + node.Value + "”",
        ScaleformBehaviorNodeKind.SetPosition => Friendly(node.Target) + $"\nX {node.Number:0.##}   Y {node.Number2:0.##}",
        ScaleformBehaviorNodeKind.GoToFrame => Friendly(node.Target) + $" · frame {node.Number:0}",
        ScaleformBehaviorNodeKind.SetVariable => node.Target + " = “" + node.Value + "”",
        ScaleformBehaviorNodeKind.IfEquals => node.Target + " equals “" + node.Value + "”?",
        ScaleformBehaviorNodeKind.CallFunction => string.IsNullOrEmpty(node.Target) ? "Choose a reusable function" : node.Target,
        _ => Friendly(node.Target)
    };

    private void AddCard(string id, double x, double y, string title, string summary, bool isEvent, bool conditional)
    {
        var accent = new SolidColorBrush(isEvent ? Color.FromRgb(131, 92, 173) : conditional ? Color.FromRgb(161, 124, 51) : Color.FromRgb(42, 117, 154));
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        grid.RowDefinitions.Add(new RowDefinition());
        var titleBorder = new Border { Background = accent, Padding = new Thickness(11, 7, 8, 6), CornerRadius = new CornerRadius(5, 5, 0, 0) };
        titleBorder.Child = new TextBlock { Text = title, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        grid.Children.Add(titleBorder);
        var body = new TextBlock { Text = summary, Foreground = new SolidColorBrush(Color.FromRgb(210, 221, 233)), TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 48, Margin = new Thickness(16, 10, 16, 6) };
        Grid.SetRow(body, 1); grid.Children.Add(body);
        var card = new Border { Width = CardWidth, Height = conditional ? 136 : 112, Background = new SolidColorBrush(Color.FromRgb(35, 46, 60)),
            BorderBrush = selectedId == id ? Brushes.White : accent, BorderThickness = new Thickness(selectedId == id ? 2 : 1), CornerRadius = new CornerRadius(6),
            Child = grid, Tag = id, Cursor = Cursors.Hand };
        card.MouseLeftButtonDown += Node_MouseDown; card.MouseMove += Node_MouseMove; card.MouseLeftButtonUp += Node_MouseUp;
        Canvas.SetLeft(card, x); Canvas.SetTop(card, y); Panel.SetZIndex(card, 2); GraphCanvas.Children.Add(card); cards[id] = card;
        if (!isEvent) AddConnector(card, id, false, false, 55, "Input · click after choosing an output");
        AddConnector(card, id, true, false, 55, conditional ? "Yes · value matches" : "Output · connect the next action");
        if (conditional) AddConnector(card, id, true, true, 100, "No · value does not match");
    }

    private void AddConnector(Border card, string id, bool output, bool falseBranch, double y, string tooltip)
    {
        var dot = new Ellipse { Width = 14, Height = 14, Fill = output && connectFrom == id && connectFalse == falseBranch ? Brushes.White : falseBranch ? Brushes.Goldenrod : Brushes.LightSkyBlue,
            Stroke = Brushes.White, StrokeThickness = 1.5, Cursor = Cursors.Cross, ToolTip = tooltip,
            Tag = new Connector(id, output, falseBranch) };
        double x = Canvas.GetLeft(card) + (output ? CardWidth - 7 : -7);
        Canvas.SetLeft(dot, x); Canvas.SetTop(dot, Canvas.GetTop(card) + y - 7); Panel.SetZIndex(dot, 3);
        dot.MouseLeftButtonDown += Connector_Click; GraphCanvas.Children.Add(dot);
        if (output)
        {
            var label = new TextBlock { Text = falseBranch ? "No" : card.Height > 120 ? "Yes" : "Next", FontSize = 10,
                Foreground = Brushes.LightGray, IsHitTestVisible = false, Tag = new PortLabel(id, falseBranch) };
            Canvas.SetLeft(label, x - 29); Canvas.SetTop(label, Canvas.GetTop(card) + y - 6);
            Panel.SetZIndex(label, 4); GraphCanvas.Children.Add(label);
        }
    }

    private void DrawWires()
    {
        foreach (var wire in wires) GraphCanvas.Children.Remove(wire);
        wires.Clear();
        var flow = SelectedFlow;
        if (flow == null) return;
        DrawWire(EventId, flow.EntryNodeId, false);
        foreach (var node in flow.Nodes)
        {
            DrawWire(node.Id, node.Next, false);
            if (node.Kind == ScaleformBehaviorNodeKind.IfEquals) DrawWire(node.Id, node.FalseNext, true);
        }
    }

    private void DrawWire(string from, string to, bool falseBranch)
    {
        if (string.IsNullOrEmpty(to) || !cards.TryGetValue(from, out var startCard) || !cards.TryGetValue(to, out var endCard)) return;
        var start = new Point(Canvas.GetLeft(startCard) + CardWidth, Canvas.GetTop(startCard) + (falseBranch ? 100 : 55));
        var end = new Point(Canvas.GetLeft(endCard), Canvas.GetTop(endCard) + 55);
        double curve = Math.Max(65, Math.Abs(end.X - start.X) * .45);
        var figure = new PathFigure { StartPoint = start, IsFilled = false };
        figure.Segments.Add(new BezierSegment(new Point(start.X + curve, start.Y), new Point(end.X - curve, end.Y), end, true));
        var geometry = new PathGeometry(); geometry.Figures.Add(figure);
        var path = new System.Windows.Shapes.Path { Data = geometry, Stroke = falseBranch ? Brushes.Goldenrod : Brushes.LightSkyBlue, StrokeThickness = 2.5, IsHitTestVisible = false };
        Panel.SetZIndex(path, 0); GraphCanvas.Children.Add(path); wires.Add(path);
    }

    private void UpdateInspector()
    {
        updating = true;
        var flow = SelectedFlow; var node = SelectedNode;
        EventProperties.Visibility = flow != null && node == null ? Visibility.Visible : Visibility.Collapsed;
        ActionProperties.Visibility = node != null ? Visibility.Visible : Visibility.Collapsed;
        SelectionButtons.Visibility = flow != null ? Visibility.Visible : Visibility.Collapsed;
        EventTarget.ItemsSource = targetChoices;
        bool plainTarget = node?.Kind is ScaleformBehaviorNodeKind.CallFunction or ScaleformBehaviorNodeKind.IfEquals or ScaleformBehaviorNodeKind.SetVariable;
        ActionTarget.DisplayMemberPath = plainTarget ? "" : "Label";
        ActionTarget.SelectedValuePath = plainTarget ? "" : "Path";
        ActionTarget.ItemsSource = node?.Kind == ScaleformBehaviorNodeKind.CallFunction
            ? graph.Flows.Where(f => f.Trigger == ScaleformBehaviorTrigger.Function).Select(f => f.FunctionName).ToArray() : plainTarget ? Array.Empty<string>() : targetChoices;
        if (flow == null)
        {
            InspectorTitle.Text = "Select an event or action";
            InspectorDescription.Text = "Choose a node to change what it does.";
        }
        else if (node == null)
        {
            InspectorTitle.Text = EventTitle(flow); InspectorDescription.Text = "This event starts the connected actions.";
            BehaviorName.Text = flow.Name; SelectTarget(EventTarget, flow.Target); FunctionName.Text = flow.FunctionName ?? "";
            EventFrame.Text = flow.Frame.ToString(CultureInfo.InvariantCulture);
            EventTargetPanel.Visibility = flow.Trigger == ScaleformBehaviorTrigger.ButtonClick ? Visibility.Visible : Visibility.Collapsed;
            FunctionPanel.Visibility = flow.Trigger == ScaleformBehaviorTrigger.Function ? Visibility.Visible : Visibility.Collapsed;
            FramePanel.Visibility = flow.Trigger != ScaleformBehaviorTrigger.Function ? Visibility.Visible : Visibility.Collapsed;
            EventFrameLabel.Text = flow.Trigger == ScaleformBehaviorTrigger.ButtonClick ? "Install click behavior on frame" : "When the movie reaches frame";
        }
        else
        {
            InspectorTitle.Text = Entry(node.Kind).Name; InspectorDescription.Text = Entry(node.Kind).Description;
            if (plainTarget) ActionTarget.Text = node.Target ?? "";
            else SelectTarget(ActionTarget, node.Target);
            ActionValue.Text = node.Value ?? "";
            ActionNumber.Text = node.Number.ToString("G", CultureInfo.InvariantCulture); ActionNumber2.Text = node.Number2.ToString("G", CultureInfo.InvariantCulture);
            PlayAfterJump.IsChecked = string.Equals(node.Value, "play", StringComparison.OrdinalIgnoreCase);
            TargetLabel.Text = node.Kind is ScaleformBehaviorNodeKind.IfEquals or ScaleformBehaviorNodeKind.SetVariable ? "Value name" : node.Kind == ScaleformBehaviorNodeKind.CallFunction ? "Reusable function" : "Element";
            ValueLabel.Text = node.Kind == ScaleformBehaviorNodeKind.IfEquals ? "Equals this text" : node.Kind == ScaleformBehaviorNodeKind.SetVariable ? "Remember this text" : "New text";
            ValuePanel.Visibility = node.Kind is ScaleformBehaviorNodeKind.SetText or ScaleformBehaviorNodeKind.SetVariable or ScaleformBehaviorNodeKind.IfEquals ? Visibility.Visible : Visibility.Collapsed;
            NumberPanel.Visibility = node.Kind is ScaleformBehaviorNodeKind.SetPosition or ScaleformBehaviorNodeKind.GoToFrame ? Visibility.Visible : Visibility.Collapsed;
            Number2Panel.Visibility = node.Kind == ScaleformBehaviorNodeKind.SetPosition ? Visibility.Visible : Visibility.Collapsed;
            NumberLabel.Text = node.Kind == ScaleformBehaviorNodeKind.GoToFrame ? "Frame number" : "X position";
            PlaybackPanel.Visibility = node.Kind == ScaleformBehaviorNodeKind.GoToFrame ? Visibility.Visible : Visibility.Collapsed;
        }
        fieldSnapshot = ReadFields(); updating = false;
    }

    private string[] ReadFields() => new[]
    {
        BehaviorName.Text, EventTarget.Text, FunctionName.Text, EventFrame.Text,
        ActionTarget.Text, ActionValue.Text, ActionNumber.Text, ActionNumber2.Text, PlayAfterJump.IsChecked == true ? "play" : ""
    };

    private static void SelectTarget(ComboBox combo, string path)
    {
        combo.SelectedValue = string.IsNullOrEmpty(path) ? "_root" : path;
        if (combo.SelectedItem == null) combo.Text = path ?? "";
    }

    private static string TargetPath(ComboBox combo) => combo.SelectedItem is TargetChoice choice && combo.Text == choice.Label ? choice.Path : combo.Text.Trim();

    public bool CommitPendingFields()
    {
        if (updating || graph == null || SelectedFlow == null) return true;
        var fields = ReadFields();
        if (fieldSnapshot.SequenceEqual(fields)) return true;
        var flow = SelectedFlow; var node = SelectedNode;
        double first = node?.Number ?? 0, second = node?.Number2 ?? 0;
        int frame = flow.Frame;
        if (node == null && flow.Trigger != ScaleformBehaviorTrigger.Function &&
            (!int.TryParse(EventFrame.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out frame) || frame < 1 || frame > 65535))
        { SetStatus("Enter a whole frame number between 1 and 65535."); return false; }
        if (node == null && flow.Trigger == ScaleformBehaviorTrigger.ButtonClick && graph.Flows.Any(f => f.Id != flow.Id && f.Trigger == flow.Trigger && NormalizeTarget(f.Target) == NormalizeTarget(TargetPath(EventTarget))))
        { SetStatus("That button already has a click behavior. Choose another element, or add actions to its existing behavior."); return false; }
        if (node?.Kind is ScaleformBehaviorNodeKind.SetPosition or ScaleformBehaviorNodeKind.GoToFrame)
        {
            if (!ReadNumber(ActionNumber.Text, out first) || node.Kind == ScaleformBehaviorNodeKind.GoToFrame && (first < 1 || first > 65535 || first != Math.Truncate(first)))
            { SetStatus(node.Kind == ScaleformBehaviorNodeKind.GoToFrame ? "Enter a whole frame number between 1 and 65535." : "Enter a valid X position."); return false; }
            if (node.Kind == ScaleformBehaviorNodeKind.SetPosition && !ReadNumber(ActionNumber2.Text, out second))
            { SetStatus("Enter a valid Y position."); return false; }
        }
        var before = Capture();
        if (node == null)
        {
            flow.Name = string.IsNullOrWhiteSpace(BehaviorName.Text) ? EventTitle(flow) : BehaviorName.Text.Trim();
            flow.Target = TargetPath(EventTarget); flow.FunctionName = FunctionName.Text.Trim(); flow.Frame = frame;
        }
        else
        {
            node.Target = TargetPath(ActionTarget); node.Value = node.Kind == ScaleformBehaviorNodeKind.GoToFrame ? fields[8] : ActionValue.Text;
            node.Number = first; node.Number2 = second;
        }
        Changed(before, "Behavior settings updated."); return true;
    }

    private static bool ReadNumber(string text, out double value) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value);

    private void Connector_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var port = (Connector)((FrameworkElement)sender).Tag;
        if (port.Output)
        {
            if (!CommitPendingFields()) return;
            connectFrom = port.Id; connectFalse = port.FalseBranch;
            RefreshCards();
            SetStatus("Now click the input circle on the action to run next. Escape cancels.");
        }
        else if (connectFrom != null) Connect(connectFrom, port.Id, connectFalse);
        else SetStatus("Choose an output circle first, then click this input to connect it.");
    }

    private void Node_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        var border = (Border)sender; string id = (string)border.Tag;
        if (!SelectNode(id)) return;
        GraphCanvas.Focus();
        if (SelectedNode == null) return;
        dragging = SelectedNode; dragStart = e.GetPosition(GraphCanvas); nodeStart = new Point(dragging.EditorX, dragging.EditorY);
        dragBefore = Capture(); dragCard = cards[id]; dragCard.CaptureMouse();
    }

    private void Node_MouseMove(object sender, MouseEventArgs e)
    {
        if (dragging == null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(GraphCanvas); var delta = point - dragStart;
        dragging.EditorX = Math.Max(25, nodeStart.X + delta.X); dragging.EditorY = Math.Max(25, nodeStart.Y + delta.Y);
        // The canvas contains few lightweight cards; avoid rebuilding controls or the movie while dragging.
        Canvas.SetLeft(dragCard, dragging.EditorX); Canvas.SetTop(dragCard, dragging.EditorY);
        foreach (var child in GraphCanvas.Children.OfType<FrameworkElement>())
        {
            if (child.Tag is Connector port && port.Id == dragging.Id)
            {
                Canvas.SetLeft(child, dragging.EditorX + (port.Output ? CardWidth - 7 : -7));
                Canvas.SetTop(child, dragging.EditorY + (port.FalseBranch ? 100 : 55) - 7);
            }
            else if (child.Tag is PortLabel label && label.Id == dragging.Id)
            {
                Canvas.SetLeft(child, dragging.EditorX + CardWidth - 36);
                Canvas.SetTop(child, dragging.EditorY + (label.FalseBranch ? 100 : 55) - 6);
            }
        }
        DrawWires(); e.Handled = true;
    }

    private void Node_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (dragging == null) return;
        bool moved = dragging.EditorX != nodeStart.X || dragging.EditorY != nodeStart.Y;
        var before = dragBefore; dragging = null; dragBefore = null;
        dragCard?.ReleaseMouseCapture(); dragCard = null;
        if (moved) Changed(before, "Node moved.");
        e.Handled = true;
    }

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != GraphCanvas) return;
        GraphCanvas.Focus();
        if (connectFrom != null) { connectFrom = null; RefreshCards(); SetStatus("Connection canceled."); }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (Palette == null) return;
        string query = PaletteSearch.Text.Trim();
        Palette.ItemsSource = Entries.Where(x => (x.Name + " " + x.Description).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private void Palette_MouseDown(object sender, MouseButtonEventArgs e) => paletteDown = e.GetPosition(Palette);
    private void Palette_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || Palette.SelectedItem is not PaletteEntry entry) return;
        var delta = e.GetPosition(Palette) - paletteDown;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(Palette, new DataObject(PaletteFormat, entry), DragDropEffects.Copy);
    }
    private void Palette_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Palette.SelectedItem is PaletteEntry entry) AddEntry(entry, null); e.Handled = true;
    }
    private void AddEntry(PaletteEntry entry, Point? position)
    {
        if (entry.Trigger.HasValue) AddBehavior(entry.Trigger.Value);
        else if (entry.Kind.HasValue) AddAction(entry.Kind.Value, position?.X, position?.Y);
    }
    private void Graph_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(PaletteFormat) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
    }
    private void Graph_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(PaletteFormat) is PaletteEntry entry) AddEntry(entry, e.GetPosition(GraphCanvas)); e.Handled = true;
    }
    private void Graph_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        double scale = Math.Clamp(zoom.ScaleX * (e.Delta > 0 ? 1.1 : 1 / 1.1), .4, 1.75);
        zoom.ScaleX = zoom.ScaleY = scale; e.Handled = true;
    }
    private void Graph_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        panStart = e.GetPosition(GraphScroll); panOffset = new Point(GraphScroll.HorizontalOffset, GraphScroll.VerticalOffset);
        GraphScroll.CaptureMouse(); e.Handled = true;
    }
    private void Graph_MouseMove(object sender, MouseEventArgs e)
    {
        if (panStart == null || e.MiddleButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(GraphScroll) - panStart.Value;
        GraphScroll.ScrollToHorizontalOffset(panOffset.X - delta.X); GraphScroll.ScrollToVerticalOffset(panOffset.Y - delta.Y); e.Handled = true;
    }
    private void Graph_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || panStart == null) return;
        panStart = null; GraphScroll.ReleaseMouseCapture(); e.Handled = true;
    }
    private void Flow_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!updating && FlowPicker.SelectedItem is ScaleformBehaviorFlow flow && flow.Id != flowId)
        {
            if (!SelectBehavior(flow.Id)) { updating = true; FlowPicker.SelectedItem = SelectedFlow; updating = false; }
        }
    }
    private void NewButton_Click(object sender, RoutedEventArgs e) => AddBehavior(ScaleformBehaviorTrigger.ButtonClick);
    private void NewFunction_Click(object sender, RoutedEventArgs e) => AddBehavior(ScaleformBehaviorTrigger.Function);
    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();
    private void Remove_Click(object sender, RoutedEventArgs e) => RemoveSelection();
    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFlow == null || !CommitPendingFields()) return;
        var before = Capture(); var node = SelectedNode;
        if (node == null) SelectedFlow.EntryNodeId = null;
        else node.Next = node.FalseNext = null;
        connectFrom = null; Changed(before, "Outgoing links disconnected. Choose an output circle to reconnect.");
    }
    private void Arrange_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFlow == null || !CommitPendingFields()) return;
        var before = Capture(); var flow = SelectedFlow; var visited = new HashSet<string>();
        var nodes = flow.Nodes.ToDictionary(n => n.Id); int row = 0;
        void Place(string id, int column, int branchRow)
        {
            if (string.IsNullOrEmpty(id) || !visited.Add(id) || !nodes.TryGetValue(id, out var node)) return;
            node.EditorX = 340 + column * 290; node.EditorY = 55 + branchRow * 190; row = Math.Max(row, branchRow);
            Place(node.Next, column + 1, branchRow);
            if (!string.IsNullOrEmpty(node.FalseNext)) Place(node.FalseNext, column + 1, ++row);
        }
        Place(flow.EntryNodeId, 0, 0);
        foreach (var node in flow.Nodes.Where(n => !visited.Contains(n.Id))) Place(node.Id, 0, ++row);
        Changed(before, "Nodes arranged. Follow the lines from the event.");
    }
    private void Field_Commit(object sender, KeyboardFocusChangedEventArgs e) { if (!updating) CommitPendingFields(); }
    private void Playback_Commit(object sender, RoutedEventArgs e) { if (!updating) CommitPendingFields(); }
    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitPendingFields(); e.Handled = true;
    }
    private void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { connectFrom = null; RefreshCards(); SetStatus("Connection canceled."); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Y) { Redo(); e.Handled = true; }
        else if (e.Key == Key.Delete && e.OriginalSource is not TextBox) { RemoveSelection(); e.Handled = true; }
    }

    private sealed record PaletteEntry(string Name, string Description, ScaleformBehaviorNodeKind? Kind = null, ScaleformBehaviorTrigger? Trigger = null);
    private sealed record Connector(string Id, bool Output, bool FalseBranch);
    private sealed record PortLabel(string Id, bool FalseBranch);
    private sealed record TargetChoice(string Path, string Label);
    private sealed record EditorState(ScaleformBehaviorGraph Graph, string FlowId, string SelectedId);
}
