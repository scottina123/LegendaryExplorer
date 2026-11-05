using System;
using System.Windows;
using LegendaryExplorer.Dialogs;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.Tools.Meshplorer;

public partial class MeshplorerWindow
{
    private void DrawModel_Click(object sender, RoutedEventArgs e)
    {
        if (Pcc == null || IsBusy || IsRendererBusy || !EndInlineMeshNameEdit(commit: true)) return;
        var package = Pcc;
        var dialog = new DrawModelWindow(package, CurrentExport?.Parent) { Owner = this };
        if (dialog.ShowDialog() != true || Pcc != package) return;
        try
        {
            ExportEntry model = AddDrawnModel(package, dialog.ResultName, dialog.ResultParent, dialog.ResultModel, dialog.ResultPolys);
            ShowModels = true;
            MeshSearchBox.Clear();
            MeshSearchText = "";
            RefreshMeshExports();
            CurrentExport = model;
            MeshExportsList.ScrollIntoView(model);
        }
        catch (Exception exception) { new ExceptionHandlerDialog(exception).ShowDialog(); }
    }

    internal static ExportEntry AddDrawnModel(IMEPackage package, string name, IEntry parent, Model model, Polys polys)
    {
        var export = package.CreateExport(name, "Model", parent);
        var polyExport = package.CreateExport("Polys", "Polys", export);
        model.Self = export.UIndex;
        model.Polys = polyExport.UIndex;
        polys.Owner = export.UIndex;
        export.WriteBinary(model);
        polyExport.WriteBinary(polys);
        return export;
    }
}
