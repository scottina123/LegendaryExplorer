using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using LegendaryExplorer.Dialogs;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;

namespace LegendaryExplorer.SharedUI;

internal enum PackageSaveChoice { Cancel, CurrentFile, HighestMountedFile }

internal sealed record PackageSaveWarning(string CurrentPath, string HighestMountedPath, bool IsOutsideGame)
{
    public bool CanSaveHighestMounted => HighestMountedPath != null;
}

/// <summary>Destination selection for interactive package saves in LEX tools.</summary>
internal static class PackageSaveService
{
    internal static bool ConfirmReferenceIssues(IMEPackage package, string destination, ReferenceCheckPackage references)
    {
        int issueCount = references.GetBlockingErrors().Count + references.GetSignificantIssues().Count;
        return Application.Current.Dispatcher.Invoke(() =>
        {
            var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
            owner ??= Application.Current.MainWindow;
            var dialog = new PackageReferenceSaveWarningDialog(destination, issueCount);
            if (owner?.IsVisible == true)
                dialog.Owner = owner;
            return dialog.ShowDialog() == true;
        });
    }

    internal static PackageSaveWarning GetWarning(IMEPackage package)
    {
        if (package == null || !package.Game.IsMEGame() || string.IsNullOrWhiteSpace(package.FilePath)) return null;

        string currentPath = Path.GetFullPath(package.FilePath);
        MELoadedFiles.TryGetHighestMountedFile(package.Game, Path.GetFileName(currentPath), out var highestPath);
        if (PathsEqual(currentPath, highestPath)) return null;

        string gamePath = MEDirectories.GetDefaultGamePath(package.Game);
        bool outsideGame = string.IsNullOrWhiteSpace(gamePath)
            || !currentPath.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        return new PackageSaveWarning(currentPath, highestPath, outsideGame);
    }

    private static bool PathsEqual(string left, string right) => left != null && right != null
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    internal static string ChooseSavePath(IMEPackage package, Window owner, string savePath = null,
        Func<PackageSaveWarning, PackageSaveChoice> choose = null)
    {
        if (package == null) return null;
        // Save As already asks the user to choose an explicit destination.
        if (savePath != null && !PathsEqual(savePath, package.FilePath)) return savePath;
        var warning = GetWarning(package);
        if (warning == null) return savePath ?? package.FilePath;

        PackageSaveChoice choice = choose != null ? choose(warning) : ShowWarning(owner, warning);
        return choice switch
        {
            PackageSaveChoice.CurrentFile => package.FilePath,
            PackageSaveChoice.HighestMountedFile when warning.CanSaveHighestMounted => warning.HighestMountedPath,
            _ => null
        };
    }

    private static PackageSaveChoice ShowWarning(Window owner, PackageSaveWarning warning)
    {
        // Texture Studio also saves packages from worker threads.
        var dispatcher = owner?.Dispatcher ?? Application.Current.Dispatcher;
        return dispatcher.Invoke(() =>
        {
            var dialog = new PackageSaveWarningDialog(warning);
            if (owner?.IsVisible == true) dialog.Owner = owner;
            dialog.ShowDialog();
            return dialog.Choice;
        });
    }

    internal static async Task<bool> SaveWithMountWarningAsync(this IMEPackage package, Window owner,
        string savePath = null, bool? compress = null, Func<PackageSaveWarning, PackageSaveChoice> choose = null)
    {
        string destination = ChooseSavePath(package, owner, savePath, choose);
        if (destination == null) return false;
        return await package.TrySaveAsync(destination, compress);
    }

    internal static bool SaveWithMountWarning(this IMEPackage package, Window owner,
        string savePath = null, bool? compress = null, Func<PackageSaveWarning, PackageSaveChoice> choose = null)
    {
        string destination = ChooseSavePath(package, owner, savePath, choose);
        if (destination == null) return false;
        return package.TrySave(destination, compress);
    }
}
