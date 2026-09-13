using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Tools.Sequence_Editor;

public partial class SequenceEditorWPF
{
    private TextBox inlineSequenceNameEditor;
    private TextBlock inlineSequenceNameDisplay;
    private ExportEntry inlineSequenceNameExport;
    private bool isEndingInlineSequenceNameEdit;

    private bool CanEditSequence(ExportEntry export) => !isReadOnlyPreview && !IsBusy
        && export != null && export.FileRef == Pcc && !export.IsDefaultObject && export.IsA("Sequence");

    private void SequencesTreeContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }
        bool canEdit = menu.PlacementTarget is TreeViewItem
        {
            DataContext: TreeViewEntry { Entry: ExportEntry export }
        } && CanEditSequence(export);
        foreach (var item in menu.Items)
        {
            if (item is MenuItem { Name: "RenameSequenceMenuItem" or "CreateSubsequenceMenuItem"
                or "CloneSequenceMenuItem" or "TrashSequenceMenuItem" } action)
            {
                action.IsEnabled = canEdit;
            }
        }
    }

    private void SequencesTree_Rename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem
            {
                Parent: ContextMenu
                {
                    PlacementTarget: TreeViewItem { DataContext: TreeViewEntry { Entry: ExportEntry export } } item
                }
            }
            || !CanEditSequence(export)
            || !EndInlineSequenceNameEdit(commit: true))
        {
            return;
        }

        var editor = FindSequenceNameControl<TextBox>(item, "SequenceNameEditor");
        var display = FindSequenceNameControl<TextBlock>(item, "SequenceNameDisplay");
        if (editor == null || display == null)
        {
            return;
        }

        inlineSequenceNameEditor = editor;
        inlineSequenceNameDisplay = display;
        inlineSequenceNameExport = export;
        editor.Text = export.GetProperty<StrProperty>("ObjName")?.Value ?? export.ObjectName.Name;
        display.Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        // Wait for the context menu to close before giving the text box keyboard focus.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (inlineSequenceNameEditor == editor)
            {
                editor.Focus();
                editor.SelectAll();
            }
        }));
    }

    private void SequenceNameEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender == inlineSequenceNameEditor && e.Key is Key.Enter or Key.Escape)
        {
            EndInlineSequenceNameEdit(commit: e.Key == Key.Enter);
            e.Handled = true;
        }
    }

    private void SequenceNameEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender == inlineSequenceNameEditor && !inlineSequenceNameEditor.IsKeyboardFocusWithin)
        {
            // Commit before a selection change can rebuild the tree and discard the editor.
            EndInlineSequenceNameEdit(commit: true);
        }
    }

    private void SequenceEditor_CommitRenameOnOutsideClick(object sender, MouseButtonEventArgs e)
    {
        if (inlineSequenceNameEditor == null)
        {
            return;
        }
        if (e.OriginalSource is Visual source
            && (source == inlineSequenceNameEditor || inlineSequenceNameEditor.IsAncestorOf(source)))
        {
            return;
        }

        // Blank areas and other non-focusable controls do not raise LostKeyboardFocus.
        if (!EndInlineSequenceNameEdit(commit: true))
        {
            e.Handled = true;
        }
    }

    private void SequenceGraph_CommitRenameOnMouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
    {
        // The WinForms graph does not participate in WPF's routed mouse events.
        EndInlineSequenceNameEdit(commit: true);
    }

    private bool EndInlineSequenceNameEdit(bool commit)
    {
        if (inlineSequenceNameEditor == null)
        {
            return true;
        }
        if (isEndingInlineSequenceNameEdit)
        {
            return false;
        }

        isEndingInlineSequenceNameEdit = true;
        try
        {
            var editor = inlineSequenceNameEditor;
            string name = editor.Text.Trim();
            if (commit && string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(this, "Sequence names cannot be empty.", "Invalid sequence name",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (inlineSequenceNameEditor == editor)
                    {
                        editor.Focus();
                        editor.SelectAll();
                    }
                }));
                return false;
            }

            var export = inlineSequenceNameExport;
            var display = inlineSequenceNameDisplay;
            inlineSequenceNameEditor = null;
            inlineSequenceNameDisplay = null;
            inlineSequenceNameExport = null;
            editor.Visibility = Visibility.Collapsed;
            display.Visibility = Visibility.Visible;

            if (commit && CanEditSequence(export))
            {
                var label = export.GetProperty<StrProperty>("ObjName");
                if (name != (label?.Value ?? export.ObjectName.Name))
                {
                    // Keep the instance number and update any friendly label used by the tree.
                    export.ObjectName = new NameReference(name, export.ObjectName.Number);
                    if (label != null)
                    {
                        export.WriteProperty(new StrProperty(name, "ObjName"));
                    }

                    // Keep the clicked tree item alive until its mouse/selection handlers finish.
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        if (isDisposed || Pcc != export.FileRef)
                        {
                            return;
                        }
                        var selectedExport = SelectedItem?.Entry as ExportEntry;
                        SequenceExports.ClearEx();
                        LoadSequences();
                        if (selectedExport != null)
                        {
                            GoToExport(selectedExport);
                        }
                    }));
                }
            }
            return true;
        }
        finally
        {
            isEndingInlineSequenceNameEdit = false;
        }
    }

    private static T FindSequenceNameControl<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T element && element.Name == name)
            {
                return element;
            }
            if (FindSequenceNameControl<T>(child, name) is { } match)
            {
                return match;
            }
        }
        return null;
    }
}
