using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Tools.Meshplorer;

public partial class MeshplorerWindow
{
    private bool CanImportMeshAssets() => Pcc != null && Pcc.Game.IsMEGame() && !IsBusy && !IsRendererBusy;

    private void OpenMeshAssetImporter()
    {
        if (!CanImportMeshAssets() || !EndInlineMeshNameEdit(commit: true))
            return;

        var destination = Pcc;
        AssetDatabaseWindow.OpenForImport(this, destination.Game,
            async items => await ImportMeshesFromAssetDatabaseAsync(destination, items), meshesOnly: true);
    }

    private async Task ImportMeshesFromAssetDatabaseAsync(IMEPackage destination,
        IReadOnlyList<AssetDatabaseWindow.AssetImportQueueItem> items)
    {
        // The picker is modeless, so its original destination may no longer be open.
        if (Pcc != destination || !CanImportMeshAssets())
        {
            MessageBox.Show(this, "The destination PCC changed or is busy. Open Import Meshes again when it is ready.", "Import meshes");
            return;
        }
        if (!EndInlineMeshNameEdit(commit: true))
            return;

        BusyText = "Importing meshes from the Asset Database...";
        IsBusy = true;
        try
        {
            var (meshes, issues) = await Task.Run(() => ImportMeshAssets(destination, items));
            if (Pcc == destination)
            {
                RefreshMeshExports();
                if (meshes.Count > 0)
                {
                    if (meshes.Any(mesh => mesh.ClassName == "SkeletalMesh")) ShowSkeletalMeshes = true;
                    if (meshes.Any(mesh => mesh.ClassName == "StaticMesh")) ShowStaticMeshes = true;
                    if (meshes.Any(mesh => !FilterExportList(mesh)))
                    {
                        MeshSearchBox.Clear();
                        MeshSearchText = "";
                    }
                    CurrentExport = meshes[^1];
                    MeshExportsList.ScrollIntoView(CurrentExport);
                }
            }
            if (issues.Count > 0)
            {
                new ListDialog(issues, "Mesh import report", "The following items reported import or relinking issues.", this).Show();
            }
        }
        catch (Exception exception)
        {
            if (Pcc == destination)
                RefreshMeshExports();
            new ExceptionHandlerDialog(exception).ShowDialog();
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal static (List<ExportEntry> Meshes, List<EntryStringPair> Issues) ImportMeshAssets(IMEPackage destination,
        IReadOnlyList<AssetDatabaseWindow.AssetImportQueueItem> items)
    {
        var meshes = new List<ExportEntry>();
        var issues = new List<EntryStringPair>();
        foreach (var item in items)
        {
            try
            {
                using var source = MEPackageHandler.OpenMEPackage(item.ResolvedFilePath);
                if (!source.IsUExport(item.UIndex)
                    || source.GetUExport(item.UIndex) is not { ClassName: "SkeletalMesh" or "StaticMesh" } mesh)
                {
                    throw new ArgumentException("The selected asset is not a skeletal or static mesh export.");
                }

                if (source == destination)
                {
                    meshes.Add(EntryCloner.CloneTree(mesh));
                }
                else
                {
                    meshes.Add(CloneMeshToPackage(mesh, destination, out var report));
                    issues.AddRange(report);
                }
            }
            catch (Exception exception)
            {
                issues.Add(new EntryStringPair($"Error importing '{item.DisplayName}': {exception.Message}"));
            }
        }
        return (meshes, issues);
    }
}
