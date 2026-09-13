using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Xceed.Wpf.Toolkit;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Tools.Meshplorer;

public partial class MeshplorerWindow
{
    private TextBox _inlineMeshNameEditor;
    private IntegerUpDown _inlineMeshNameIndexEditor;
    private Panel _inlineMeshNameEditorPanel;
    private TextBlock _inlineMeshNameDisplay;
    private ExportEntry _inlineMeshNameExport;
    private bool _isEndingInlineMeshNameEdit;

    private void RenameMesh_Click(object sender, RoutedEventArgs e) => BeginInlineMeshNameEdit(sender, editIndex: false);

    private void ChangeMeshIndex_Click(object sender, RoutedEventArgs e) => BeginInlineMeshNameEdit(sender, editIndex: true);

    private void BeginInlineMeshNameEdit(object sender, bool editIndex)
    {
        if (sender is not MenuItem
            {
                Parent: ContextMenu { PlacementTarget: ListBoxItem { DataContext: ExportEntry export } item }
            }
            || export.FileRef != Pcc || IsBusy || IsRendererBusy
            || !EndInlineMeshNameEdit(commit: true))
        {
            return;
        }

        var editor = FindMeshNameControl<TextBox>(item, "MeshNameEditor");
        var indexEditor = FindMeshNameControl<IntegerUpDown>(item, "MeshNameIndexEditor");
        var editorPanel = FindMeshNameControl<Panel>(item, "MeshNameEditorPanel");
        var display = FindMeshNameControl<TextBlock>(item, "MeshNameDisplay");
        if (editor == null || indexEditor == null || editorPanel == null || display == null)
        {
            return;
        }

        _inlineMeshNameEditor = editor;
        _inlineMeshNameIndexEditor = indexEditor;
        _inlineMeshNameEditorPanel = editorPanel;
        _inlineMeshNameDisplay = display;
        _inlineMeshNameExport = export;
        editor.Text = export.ObjectName.Name;
        indexEditor.Value = export.indexValue;
        display.Visibility = Visibility.Collapsed;
        editorPanel.Visibility = Visibility.Visible;
        // Let the context menu close before moving keyboard focus to the editor.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_inlineMeshNameEditor == editor)
            {
                if (editIndex)
                {
                    indexEditor.Focus();
                    FindMeshNameControl<TextBox>(indexEditor, "PART_TextBox")?.SelectAll();
                }
                else
                {
                    editor.Focus();
                    editor.SelectAll();
                }
            }
        }));
    }

    private void MeshNameEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender == _inlineMeshNameEditorPanel && e.Key is Key.Enter or Key.Escape)
        {
            EndInlineMeshNameEdit(commit: e.Key == Key.Enter);
            e.Handled = true;
        }
    }

    private void MeshNameEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender == _inlineMeshNameEditorPanel && !_isEndingInlineMeshNameEdit)
        {
            var editorPanel = _inlineMeshNameEditorPanel;
            // Moving between the name, index, and spinner buttons must keep the edit open.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (_inlineMeshNameEditorPanel == editorPanel && !editorPanel.IsKeyboardFocusWithin)
                {
                    EndInlineMeshNameEdit(commit: true);
                }
            }));
        }
    }

    private void MeshNameEditor_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender == _inlineMeshNameEditorPanel)
        {
            EndInlineMeshNameEdit(commit: false);
        }
    }

    private void Meshplorer_CommitRenameOnOutsideClick(object sender, MouseButtonEventArgs e)
    {
        if (_inlineMeshNameEditor == null
            || e.OriginalSource is Visual source
            && (source == _inlineMeshNameEditorPanel || _inlineMeshNameEditorPanel.IsAncestorOf(source)))
        {
            return;
        }

        // Empty areas do not take keyboard focus, so handle those clicks explicitly.
        if (!EndInlineMeshNameEdit(commit: true))
        {
            e.Handled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Commit before WPFBase checks the package for unsaved changes.
        if (!EndInlineMeshNameEdit(commit: true))
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    private bool EndInlineMeshNameEdit(bool commit)
    {
        if (_inlineMeshNameEditor == null)
            return true;
        if (_isEndingInlineMeshNameEdit)
            return false;

        _isEndingInlineMeshNameEdit = true;
        try
        {
            var editor = _inlineMeshNameEditor;
            var indexEditor = _inlineMeshNameIndexEditor;
            string name = editor.Text.Trim();
            if (commit && string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(this, "Mesh names cannot be empty.", "Invalid mesh name",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (_inlineMeshNameEditor == editor)
                    {
                        editor.Focus();
                        editor.SelectAll();
                    }
                }));
                return false;
            }

            if (commit && !indexEditor.CommitInput())
            {
                MessageBox.Show(this, "The object index must be a whole number from 0 to 2147483647.", "Invalid mesh index",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (_inlineMeshNameIndexEditor == indexEditor)
                        indexEditor.Focus();
                }));
                return false;
            }

            var export = _inlineMeshNameExport;
            var display = _inlineMeshNameDisplay;
            var editorPanel = _inlineMeshNameEditorPanel;
            int number = indexEditor.Value ?? 0;
            _inlineMeshNameEditor = null;
            _inlineMeshNameIndexEditor = null;
            _inlineMeshNameEditorPanel = null;
            _inlineMeshNameDisplay = null;
            _inlineMeshNameExport = null;
            editorPanel.Visibility = Visibility.Collapsed;
            display.Visibility = Visibility.Visible;

            if (commit && export.FileRef == Pcc && (name != export.ObjectName.Name || number != export.ObjectName.Number))
            {
                export.ObjectName = new NameReference(name, number);
                display.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();

                // Finish the current click before filtering can remove its target row.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (Pcc == export.FileRef)
                    {
                        MeshesView.Refresh();
                    }
                }));
            }
            return true;
        }
        finally
        {
            _isEndingInlineMeshNameEdit = false;
        }
    }

    private static T FindMeshNameControl<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T element && element.Name == name)
                return element;
            if (FindMeshNameControl<T>(child, name) is { } match)
                return match;
        }
        return null;
    }
}
