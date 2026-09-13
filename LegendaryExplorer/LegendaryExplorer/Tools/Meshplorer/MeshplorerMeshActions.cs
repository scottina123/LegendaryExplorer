using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.Dialogs;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Tools.Meshplorer;

public partial class MeshplorerWindow
{
    private ExportEntry GetEditableContextMesh(object sender) => sender is MenuItem
    {
        Parent: ContextMenu { PlacementTarget: ListBoxItem { DataContext: ExportEntry export } }
    } && !IsBusy && !IsRendererBusy && export.FileRef == Pcc
      && Pcc.GetEntry(export.UIndex) == export && Mesh3DViewer.CanParse(export)
        ? export : null;

    private void CloneMeshTree_Click(object sender, RoutedEventArgs e)
    {
        if (GetEditableContextMesh(sender) is not { } mesh || !EndInlineMeshNameEdit(commit: true))
            return;

        try
        {
            var clone = EntryCloner.CloneTree(mesh);
            RefreshMeshExports();
            if (!FilterExportList(clone))
            {
                MeshSearchBox.Clear();
                MeshSearchText = "";
            }
            CurrentExport = clone;
            MeshExportsList.ScrollIntoView(clone);
        }
        catch (Exception exception)
        {
            new ExceptionHandlerDialog(exception).ShowDialog();
        }
    }

    private async void TrashMeshTree_Click(object sender, RoutedEventArgs e)
    {
        if (GetEditableContextMesh(sender) is not { } mesh || !EndInlineMeshNameEdit(commit: true))
            return;

        var package = Pcc;
        var entriesToTrash = mesh.GetAllDescendants().Prepend(mesh).ToHashSet();
        BusyText = "Performing reference check...";
        IsBusy = true;
        try
        {
            IEntry referencedEntry = await Task.Run(() => FindExternalMeshTreeReference(entriesToTrash));
            if (Pcc != package || package.GetEntry(mesh.UIndex) != mesh || !Mesh3DViewer.CanParse(mesh))
                return;

            if (referencedEntry != null && MessageBox.Show(this,
                    $"#{referencedEntry.UIndex} {referencedEntry.InstancedFullPath} is referenced by other entries! " +
                    "These references will be broken if you trash it. Are you sure you want to proceed?",
                    "Trash warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            int selectedIndex = MeshExportsList.SelectedIndex;
            bool selectionTrashed = CurrentExport != null && entriesToTrash.Contains(CurrentExport);
            if (selectionTrashed)
            {
                // Unload the preview before its export is converted to trash or removed.
                CurrentExport = null;
            }

            EntryPruner.TrashEntries(package, entriesToTrash.OrderByDescending(entry => entry.InstancedFullPath.Count(c => c == '.')));
            RefreshMeshExports();
            if (selectionTrashed && MeshExportsList.Items.Count > 0)
            {
                CurrentExport = (ExportEntry)MeshExportsList.Items[Math.Clamp(selectedIndex, 0, MeshExportsList.Items.Count - 1)];
                MeshExportsList.ScrollIntoView(CurrentExport);
            }
        }
        catch (Exception exception)
        {
            new ExceptionHandlerDialog(exception).ShowDialog();
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal static IEntry FindExternalMeshTreeReference(HashSet<IEntry> entriesToTrash) =>
        entriesToTrash.FirstOrDefault(entry => entry.GetEntriesThatReferenceThisOne().Keys
            .Any(referencer => !entriesToTrash.Contains(referencer)));

    private void RefreshMeshExports()
    {
        ExportEntry selected = CurrentExport;
        MeshExports.ReplaceAll(Pcc.Exports.Where(Mesh3DViewer.CanParse));
        if (selected != null && (!MeshExports.Contains(selected) || !MeshesView.Contains(selected)))
        {
            CurrentExport = null;
        }
        else if (selected != CurrentExport)
        {
            CurrentExport = selected;
        }
    }
}
