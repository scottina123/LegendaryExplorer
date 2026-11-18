using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>A row of the History drop-down: the state after a step (or before any, at 0)</summary>
public sealed class HistoryEntry
{
    /// <summary>How many steps are done in this state</summary>
    public int Position { get; init; }
    public string Description { get; init; }
    /// <summary>Whether it's the state the scene is in</summary>
    public bool IsCurrent { get; init; }
    /// <summary>Whether it's a step that's been undone, which redo brings back</summary>
    public bool IsUndone { get; init; }
}

// Fork: the History drop-down by Undo/Redo, in place of upstream's always-shown Changes row. It lists the undo history (double-click a row to go back or
// forward to it) and the uncommitted changes
public partial class LevelEditor
{
    public ObservableCollection<HistoryEntry> HistoryEntries { get; } = [];

    private void RefreshHistoryEntries()
    {
        var done = UndoHistory.DoneSteps;
        var undone = UndoHistory.RedoSteps;
        HistoryEntries.Clear();
        HistoryEntries.Add(new HistoryEntry { Position = 0, Description = "Opened / last commit", IsCurrent = done.Count == 0 });
        for (int i = 0; i < done.Count; i++)
        {
            HistoryEntries.Add(new HistoryEntry { Position = i + 1, Description = done[i].Description, IsCurrent = i == done.Count - 1 });
        }
        for (int i = 0; i < undone.Count; i++)
        {
            HistoryEntries.Add(new HistoryEntry { Position = done.Count + i + 1, Description = undone[i].Description, IsUndone = true });
        }
    }

    private bool historyRefreshHooked;

    private void HistoryDropDown_Opened(object sender, RoutedEventArgs e)
    {
        if (!historyRefreshHooked)
        {
            historyRefreshHooked = true;
            UndoHistory.PropertyChanged += (_, _) => RefreshHistoryEntries();
        }
        RefreshHistoryEntries();
    }

    /// <summary>Undoes or redoes steps until the scene is in the state of the double-clicked row</summary>
    private void HistoryEntryBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || (sender as FrameworkElement)?.DataContext is not HistoryEntry entry) return;
        GoToHistoryPosition(entry.Position);
        e.Handled = true;
    }

    private void GoToHistoryPosition(int position)
    {
        if (!CanMutateScene) return;
        int done = UndoHistory.DoneSteps.Count;
        if (position == done) return;
        CancelViewportDrag();
        if (position < done)
        {
            ApplyHistoryStep(() => UndoHistory.UndoMultiple(done - position), "Undo");
        }
        else
        {
            ApplyHistoryStep(() => UndoHistory.RedoMultiple(position - done), "Redo");
        }
        RefreshHistoryEntries();
    }
}
