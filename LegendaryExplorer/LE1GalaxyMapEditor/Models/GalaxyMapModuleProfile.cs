using System.IO;
using LegendaryExplorerCore.Packages;

namespace LE1GalaxyMapEditor.Models;

/// <summary>Editor-owned module data persisted outside the DLC installation.</summary>
public sealed record GalaxyMapModuleProfile(
    string ProfileId,
    string DlcTag,
    string LastKnownDlcPath,
    string GalaxyMapPackage,
    ModuleColor ModuleColor,
    MELocalization TlkLocale,
    IReadOnlyList<string> ResourcePackages,
    ModuleIdReservations Reservations,
    string? DisplayName = null)
{
    public static GalaxyMapModuleProfile FromModule(GalaxyMapModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (!module.IsPccBacked || module.ProfileId is null || module.DlcRootPath is null ||
            module.GalaxyMapPackagePath is null)
        {
            throw new InvalidOperationException("The module is not linked to a PCC profile.");
        }

        string Relative(string path)
        {
            var relative = Path.GetRelativePath(module.DlcRootPath, path).Replace('\\', '/');
            _ = ResolveDlcRelativePath(module.DlcRootPath, relative);
            return relative;
        }

        return new GalaxyMapModuleProfile(
            module.ProfileId,
            module.Tag,
            module.DlcRootPath,
            Relative(module.GalaxyMapPackagePath),
            module.Color,
            module.TlkLocale,
            module.ResourcePackagePaths.Select(Relative).ToArray(),
            module.Reservations,
            module.Name);
    }

    public GalaxyMapModule ToModule(string sourceDisplayName, int loadOrder)
    {
        var dlcRoot = Path.GetFullPath(LastKnownDlcPath);
        var packagePath = ResolveDlcRelativePath(dlcRoot, GalaxyMapPackage);
        var cookedPath = Path.GetDirectoryName(packagePath)
            ?? throw new InvalidOperationException("The galaxy-map PCC has no parent directory.");
        return new GalaxyMapModule(
            string.IsNullOrWhiteSpace(DisplayName) ? sourceDisplayName : DisplayName.Trim(),
            DlcTag,
            ModuleColor,
            cookedPath,
            isReadOnly: false,
            loadOrder,
            Reservations,
            profileId: ProfileId,
            dlcRootPath: dlcRoot,
            galaxyMapPackagePath: packagePath,
            tlkLocale: TlkLocale,
            resourcePackagePaths: ResourcePackages.Select(path => ResolveDlcRelativePath(dlcRoot, path)).ToArray());
    }

    public static string ResolveDlcRelativePath(string dlcRootPath, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dlcRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("A module profile package path must be DLC-relative.", nameof(relativePath));
        }

        var root = Path.GetFullPath(dlcRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A module profile package path escapes the DLC root.", nameof(relativePath));
        }
        return fullPath;
    }
}
