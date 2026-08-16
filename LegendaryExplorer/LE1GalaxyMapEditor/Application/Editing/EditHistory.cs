using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Workflows.Editing;

public sealed record HistoryPresentationState(
    GalaxyMapRowKey? SelectionKey,
    NavigationTarget Navigation,
    string? PreferredInstanceTag,
    bool InspectPhysicalInstance);

internal sealed record EditorHistoryState(
    IReadOnlyList<GalaxyMapLayer> Layers,
    string? ActiveModuleTag,
    EditChangeSetSnapshot Changes,
    HistoryPresentationState Presentation,
    long ApproximateBytes);

internal sealed record EditHistorySnapshot(
    IReadOnlyList<EditorHistoryState> Undo,
    IReadOnlyList<EditorHistoryState> Redo);

public sealed class EditHistory
{
    private const int MaximumEntries = 50;
    private const long MaximumBytes = 64L * 1024 * 1024;
    private readonly Stack<EditorHistoryState> _undo = [];
    private readonly Stack<EditorHistoryState> _redo = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    internal void PushUndo(EditorHistoryState state) => Push(_undo, state);
    internal void PushRedo(EditorHistoryState state) => Push(_redo, state);
    internal EditorHistoryState PopUndo() => _undo.Pop();
    internal EditorHistoryState PopRedo() => _redo.Pop();
    internal void ClearRedo() => _redo.Clear();

    internal EditHistorySnapshot Capture()
        => new(_undo.ToArray(), _redo.ToArray());

    internal void Restore(EditHistorySnapshot snapshot)
    {
        RestoreStack(_undo, snapshot.Undo);
        RestoreStack(_redo, snapshot.Redo);
    }

    internal void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private static void Push(Stack<EditorHistoryState> stack, EditorHistoryState state)
    {
        stack.Push(state);
        if (stack.Count <= MaximumEntries && stack.Sum(item => item.ApproximateBytes) <= MaximumBytes)
        {
            return;
        }

        var newestFirst = stack.ToArray();
        var retained = new List<EditorHistoryState>(Math.Min(newestFirst.Length, MaximumEntries));
        long retainedBytes = 0;
        foreach (var candidate in newestFirst)
        {
            if (retained.Count >= MaximumEntries ||
                retained.Count > 0 && retainedBytes + candidate.ApproximateBytes > MaximumBytes)
            {
                break;
            }

            retained.Add(candidate);
            retainedBytes += candidate.ApproximateBytes;
        }

        stack.Clear();
        for (var index = retained.Count - 1; index >= 0; index--)
        {
            stack.Push(retained[index]);
        }
    }

    private static void RestoreStack(
        Stack<EditorHistoryState> stack,
        IReadOnlyList<EditorHistoryState> newestFirst)
    {
        stack.Clear();
        for (var index = newestFirst.Count - 1; index >= 0; index--)
        {
            stack.Push(newestFirst[index]);
        }
    }
}
