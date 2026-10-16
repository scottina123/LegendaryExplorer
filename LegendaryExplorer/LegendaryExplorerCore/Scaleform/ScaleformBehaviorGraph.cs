using System;
using System.Collections.Generic;
using System.Text.Json;

namespace LegendaryExplorerCore.Scaleform;

public enum ScaleformBehaviorTrigger { ButtonClick, Frame, Function }
public enum ScaleformBehaviorNodeKind { Show, Hide, SetText, SetPosition, Play, Stop, GoToFrame, SetVariable, CallFunction, IfEquals }

/// <summary>Editable visual behaviors. Connections use stable node IDs; an empty connection finishes a flow.</summary>
public sealed class ScaleformBehaviorGraph
{
    public List<ScaleformBehaviorFlow> Flows { get; set; } = [];
    public bool IsEmpty => Flows.Count == 0;
    public ScaleformBehaviorGraph Clone() => JsonSerializer.Deserialize<ScaleformBehaviorGraph>(JsonSerializer.Serialize(this));
}

public sealed class ScaleformBehaviorFlow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New behavior";
    public ScaleformBehaviorTrigger Trigger { get; set; }
    public string Target { get; set; } = "";
    public string FunctionName { get; set; } = "";
    public ushort? SpriteId { get; set; }
    public int Frame { get; set; } = 1;
    public string EntryNodeId { get; set; } = "";
    public List<ScaleformBehaviorNode> Nodes { get; set; } = [];
}

public sealed class ScaleformBehaviorNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ScaleformBehaviorNodeKind Kind { get; set; }
    public string Target { get; set; } = "";
    /// <summary>Plain text for text/variable/condition nodes; "play" optionally makes GoToFrame play.</summary>
    public string Value { get; set; } = "";
    public double Number { get; set; }
    public double Number2 { get; set; }
    public string Next { get; set; } = "";
    public string FalseNext { get; set; } = "";
    public double EditorX { get; set; }
    public double EditorY { get; set; }
}
