using System;
using System.Collections.Generic;
using LegendaryExplorerCore.Scaleform;

namespace LegendaryExplorer.UserControls.ExportLoaderControls.Scaleform;

/// <summary>Visual edits belong to the movie draft, so changing exports never loses them.</summary>
public sealed class ScaleformVisualDraft
{
    public Dictionary<string, ScaleformMatrix> Matrices { get; } = new();
    public Dictionary<string, ScaleformTextFormat> TextFormats { get; } = new();
    public HashSet<string> HtmlTextFormats { get; } = new();
    public List<ScaleformVisualAddition> Additions { get; } = new();
    public List<ScaleformVisualRemoval> Removals { get; } = new();
    public List<ScaleformVisualKeyframe> Keyframes { get; } = new();
    public ushort? SpriteId { get; set; }
    public int Frame { get; set; } = 1;
    internal bool InitialFrameChosen { get; set; }
    internal Stack<GroupNavigationLocation> GroupNavigationHistory { get; } = new();
    internal Stack<VisualChange> UndoHistory { get; } = new();
    internal Stack<VisualChange> RedoHistory { get; } = new();
    public bool IsModified => Matrices.Count > 0 || TextFormats.Count > 0 || Additions.Count > 0 || Removals.Count > 0 || Keyframes.Count > 0;

    public void ClearEdits()
    {
        Matrices.Clear(); TextFormats.Clear(); HtmlTextFormats.Clear(); Additions.Clear(); Removals.Clear(); Keyframes.Clear();
        UndoHistory.Clear(); RedoHistory.Clear();
    }

    internal void Record(Action undo, Action redo)
    {
        UndoHistory.Push(new VisualChange(undo, redo));
        RedoHistory.Clear();
    }

    internal sealed record VisualChange(Action Undo, Action Redo);
    internal sealed record GroupNavigationLocation(ushort? SpriteId, int Frame, string SelectionIdentity,
        ushort? SelectionOwnerSpriteId, ushort? SelectionDepth);
}

public sealed class ScaleformVisualAddition
{
    public string Id { get; } = "+" + Guid.NewGuid().ToString("N");
    public ushort CharacterId { get; set; }
    public ScaleformElementSpec NewElement { get; init; }
    public string Name { get; init; }
    public ushort? SpriteId { get; init; }
    public int Frame { get; init; }
    public ushort? Depth { get; set; }
    public ScaleformMatrix Matrix { get; set; }
}

public sealed record ScaleformVisualRemoval(ushort Depth, ushort? SpriteId, int Frame);

public sealed class ScaleformVisualKeyframe
{
    public string Id { get; } = "~" + Guid.NewGuid().ToString("N");
    public ushort Depth { get; init; }
    public ushort? SpriteId { get; init; }
    public int Frame { get; init; }
    public ScaleformMatrix Matrix { get; set; }
}
