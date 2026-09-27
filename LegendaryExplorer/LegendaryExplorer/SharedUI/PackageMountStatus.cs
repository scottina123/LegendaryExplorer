using System;
using System.IO;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.SharedUI;

internal static class PackageMountStatus
{
    internal const string WarningText = "NOT HIGHEST MOUNTED VERSION";

    /// <summary>Returns the installed file that overrides this package, or null if there is none.</summary>
    internal static string GetOverridingFilePath(IMEPackage package)
    {
        if (package == null || string.IsNullOrWhiteSpace(package.FilePath) || !package.Game.IsMEGame()
            || !MEDirectories.GetLocationDescriptor(package.FilePath, package.Game, out _))
        {
            return null;
        }

        if (MELoadedFiles.TryGetHighestMountedFile(package.Game, Path.GetFileName(package.FilePath), out string mountedPath)
            && !string.Equals(Path.GetFullPath(package.FilePath), Path.GetFullPath(mountedPath), StringComparison.OrdinalIgnoreCase))
        {
            return mountedPath;
        }

        return null;
    }
}
