using System;
using System.Windows;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Tools.Meshplorer;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public partial class MeshRenderer
{
    private void DrawModel_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || CurrentLoadedExport is not { } current) return;
        var package = current.FileRef;
        var dialog = new DrawModelWindow(package, current.Parent) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || CurrentLoadedExport?.FileRef != package) return;
        try
        {
            ExportEntry model = MeshplorerWindow.AddDrawnModel(package, dialog.ResultName, dialog.ResultParent, dialog.ResultModel, dialog.ResultPolys);
            if (Window.GetWindow(this) is Tools.PackageEditor.PackageEditorWindow editor)
                editor.GoToNumber(model.UIndex);
            else
                LoadExport(model);
        }
        catch (Exception exception) { new ExceptionHandlerDialog(exception).ShowDialog(); }
    }
}
