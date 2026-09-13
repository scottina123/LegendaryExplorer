using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Tools.Meshplorer;

public partial class MeshplorerWindow
{
    internal const string MeshDragFormat = "LegendaryExplorer.Meshplorer.Mesh";
    internal sealed record MeshDragData(MeshplorerWindow SourceWindow, ExportEntry Export);

    private Point _meshDragStart;
    private ExportEntry _meshDragCandidate;

    private void MeshExportsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ResetMeshDrag();
        if (IsBusy || IsRendererBusy || e.OriginalSource is not DependencyObject source
            || source is Visual visual && _inlineMeshNameEditorPanel?.IsAncestorOf(visual) == true
            || ItemsControl.ContainerFromElement(MeshExportsList, source) is not ListBoxItem { DataContext: ExportEntry mesh })
        {
            return;
        }

        _meshDragStart = e.GetPosition(MeshExportsList);
        _meshDragCandidate = mesh;
        // Selecting starts an asynchronous preview load and disables the list. Wait until
        // mouse-up so a drag can start from an unselected row without loading its preview.
        e.Handled = true;
        Mouse.Capture(MeshExportsList);
    }

    private void MeshExportsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var mesh = _meshDragCandidate;
        ResetMeshDrag();
        if (mesh != null && mesh.FileRef == Pcc && MeshExports.Contains(mesh))
        {
            MeshExportsList.SelectedItem = mesh;
            (MeshExportsList.ItemContainerGenerator.ContainerFromItem(mesh) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
    }

    private void MeshExportsList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ResetMeshDrag();
            return;
        }
        if (_meshDragCandidate == null || IsBusy || IsRendererBusy)
            return;

        Vector delta = e.GetPosition(MeshExportsList) - _meshDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var mesh = _meshDragCandidate;
        ResetMeshDrag();
        if (mesh.FileRef != Pcc || !MeshExports.Contains(mesh) || !EndInlineMeshNameEdit(commit: true))
            return;

        e.Handled = true;
        DragDrop.DoDragDrop(MeshExportsList, new DataObject(MeshDragFormat, new MeshDragData(this, mesh)), DragDropEffects.Copy);
    }

    private void MeshExportsList_LostMouseCapture(object sender, MouseEventArgs e) => _meshDragCandidate = null;

    private void ResetMeshDrag()
    {
        _meshDragCandidate = null;
        if (Mouse.Captured == MeshExportsList)
            Mouse.Capture(null);
    }

    internal bool CanAcceptMeshDrop(MeshDragData data) =>
        data?.SourceWindow != null && data.SourceWindow != this && data.Export != null
        && Pcc != null && !IsBusy && !IsRendererBusy && !data.SourceWindow.IsBusy
        && data.SourceWindow.Pcc == data.Export.FileRef && data.Export.FileRef != Pcc
        && data.Export.FileRef.GetEntry(data.Export.UIndex) == data.Export
        && data.Export.Game == Pcc.Game && data.Export.FileRef.Platform == Pcc.Platform
        && MeshRenderer.CanParseStatic(data.Export);

    private void MeshExportsList_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(MeshDragFormat))
            return; // Leave package-file drops to the existing window handler.

        e.Handled = true;
        e.Effects = (e.AllowedEffects & DragDropEffects.Copy) != 0
                    && CanAcceptMeshDrop(e.Data.GetData(MeshDragFormat) as MeshDragData)
            ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void MeshExportsList_PreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(MeshDragFormat))
            return;

        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if ((e.AllowedEffects & DragDropEffects.Copy) == 0
            || e.Data.GetData(MeshDragFormat) is not MeshDragData data
            || !CanAcceptMeshDrop(data) || !EndInlineMeshNameEdit(commit: true))
            return;

        BusyText = $"Cloning {data.Export.ObjectName.Instanced}...";
        IsBusy = true;
        try
        {
            var clone = CloneMeshToPackage(data.Export, Pcc, out var relinkReport);
            RefreshMeshExports();
            // The destination can have filters that hide this mesh type or name.
            if (clone.ClassName == "SkeletalMesh") ShowSkeletalMeshes = true;
            if (clone.ClassName == "StaticMesh") ShowStaticMeshes = true;
            if (clone.ClassName == "Brush") ShowBrushes = true;
            if (!FilterExportList(clone))
            {
                MeshSearchBox.Clear();
                MeshSearchText = "";
            }
            CurrentExport = clone;
            MeshExportsList.ScrollIntoView(clone);
            e.Effects = DragDropEffects.Copy;
            if (relinkReport.Count > 0)
            {
                new ListDialog(relinkReport, "Mesh import report", "The following items reported relinking issues.", this).Show();
            }
        }
        catch (Exception exception)
        {
            RefreshMeshExports();
            new ExceptionHandlerDialog(exception).ShowDialog();
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal static ExportEntry CloneMeshToPackage(ExportEntry source, IMEPackage destination, out List<EntryStringPair> relinkReport)
    {
        if (source == null || destination == null || source.FileRef == destination
            || source.Game != destination.Game || source.FileRef.Platform != destination.Platform
            || source.FileRef.GetEntry(source.UIndex) != source || !MeshRenderer.CanParseStatic(source))
        {
            throw new ArgumentException("Choose a mesh from a different PCC for the same game and platform.");
        }

        using var cache = new PackageCache();
        var options = new RelinkerOptionsPackage(cache)
        {
            ImportExportDependencies = true,
            PortImportsMemorySafe = Settings.PackageEditor_DefaultMemorySafeImportPorting,
        };
        var parent = EntryImporter.GetOrAddCrossImportOrPackage(source.ParentInstancedFullPath, source.FileRef, destination, options);
        NameReference name = source.ObjectName;
        string prefix = parent == null ? "" : parent.InstancedFullPath + ".";
        while (destination.FindEntry(prefix + name.Instanced) != null)
        {
            name = new NameReference(name.Name, checked(name.Number + 1));
        }

        // Import a new root explicitly: the general importer otherwise reuses an existing
        // export with the source path. Rename only the new root, leaving the source intact.
        var clone = (ExportEntry)EntryImporter.ImportExport(destination, source, parent?.UIndex ?? 0, options);
        clone.ObjectName = name;
        relinkReport = EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.MergeTreeChildren,
            source, destination, clone, true, options, out _);
        return clone;
    }
}
