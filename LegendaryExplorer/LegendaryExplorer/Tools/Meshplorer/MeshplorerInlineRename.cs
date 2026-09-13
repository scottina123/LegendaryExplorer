using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Tools.Meshplorer;

public partial class MeshplorerWindow
{
    private TextBox _inlineMeshNameEditor;
    private TextBlock _inlineMeshNameDisplay;
    private ExportEntry _inlineMeshNameExport;
    private bool _isEndingInlineMeshNameEdit;

    private void RenameMesh_Click(object sender, RoutedEventArgs e)
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
        var display = FindMeshNameControl<TextBlock>(item, "MeshNameDisplay");
        if (editor == null || display == null)
        {
            return;
        }

        _inlineMeshNameEditor = editor;
        _inlineMeshNameDisplay = display;
        _inlineMeshNameExport = export;
        editor.Text = export.ObjectName.Name;
        display.Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        // Let the context menu close before moving keyboard focus to the editor.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_inlineMeshNameEditor == editor)
            {
                editor.Focus();
                editor.SelectAll();
            }
        }));
    }

    private void MeshNameEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender == _inlineMeshNameEditor && e.Key is Key.Enter or Key.Escape)
        {
            EndInlineMeshNameEdit(commit: e.Key == Key.Enter);
            e.Handled = true;
        }
    }

    private void MeshNameEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender == _inlineMeshNameEditor && !_inlineMeshNameEditor.IsKeyboardFocusWithin)
        {
            EndInlineMeshNameEdit(commit: true);
        }
    }

    private void MeshNameEditor_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender == _inlineMeshNameEditor)
        {
            EndInlineMeshNameEdit(commit: false);
        }
    }

    private void Meshplorer_CommitRenameOnOutsideClick(object sender, MouseButtonEventArgs e)
    {
        if (_inlineMeshNameEditor == null
            || e.OriginalSource is Visual source
            && (source == _inlineMeshNameEditor || _inlineMeshNameEditor.IsAncestorOf(source)))
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

            var export = _inlineMeshNameExport;
            var display = _inlineMeshNameDisplay;
            _inlineMeshNameEditor = null;
            _inlineMeshNameDisplay = null;
            _inlineMeshNameExport = null;
            editor.Visibility = Visibility.Collapsed;
            display.Visibility = Visibility.Visible;

            if (commit && export.FileRef == Pcc && name != export.ObjectName.Name)
            {
                export.ObjectName = new NameReference(name, export.ObjectName.Number);
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
