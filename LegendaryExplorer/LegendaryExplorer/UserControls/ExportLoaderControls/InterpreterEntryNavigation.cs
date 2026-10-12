using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

internal static class InterpreterEntryNavigation
{
    private static readonly RelayCommand OpenInNewWindowCommand = new(OpenInNewWindow,
        parameter => parameter is IEntry entry && entry.FileRef.GetEntry(entry.UIndex) == entry);

    public static RelayCommand GetCommand(RelayCommand navigateToEntry, RelayCommand openEntryInNewWindow, bool newWindow)
    {
        // Other tools have no in-place Package Editor navigation; always open a new editor there.
        if (navigateToEntry == null)
        {
            return openEntryInNewWindow ?? OpenInNewWindowCommand;
        }

        return newWindow ? openEntryInNewWindow : navigateToEntry;
    }

    private static void OpenInNewWindow(object parameter)
    {
        if (!OpenInNewWindowCommand.CanExecute(parameter) || parameter is not IEntry entry)
        {
            return;
        }

        var editor = new PackageEditorWindow();
        editor.Show();
        editor.LoadPackage(entry.FileRef, entry.UIndex);
        editor.RestoreAndBringToFront();
    }
}
