using System.Globalization;
using System.IO;
using LE1GalaxyMapEditor.Models;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;

namespace LE1GalaxyMapEditor.Services;

public sealed record DiscoveredGalaxyMapModule(
    GalaxyMapModule Module,
    GalaxyMapModuleProfile Profile,
    bool IsNewProfile);

/// <summary>Derives immutable module facts from a selected galaxy-map PCC and its DLC.</summary>
public sealed class DlcModuleDiscoveryService
{
    private readonly GalaxyMapModuleProfileStore _profiles;

    public DlcModuleDiscoveryService(GalaxyMapModuleProfileStore? profiles = null)
    {
        _profiles = profiles ?? new GalaxyMapModuleProfileStore();
    }

    public DiscoveredGalaxyMapModule Discover(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var fullPackagePath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPackagePath))
        {
            throw new GalaxyMapLoadException($"The selected PCC does not exist: {fullPackagePath}");
        }

        var cookedDirectory = Directory.GetParent(fullPackagePath)
            ?? throw new GalaxyMapLoadException("The selected PCC has no parent directory.");
        if (!string.Equals(cookedDirectory.Name, "CookedPCConsole", StringComparison.OrdinalIgnoreCase))
        {
            throw new GalaxyMapLoadException(
                "The selected galaxy-map PCC must be directly inside a CookedPCConsole directory.");
        }

        var dlcDirectory = cookedDirectory.Parent
            ?? throw new GalaxyMapLoadException("CookedPCConsole has no containing DLC directory.");
        var autoLoadPath = Path.Combine(dlcDirectory.FullName, "AutoLoad.ini");
        if (!File.Exists(autoLoadPath))
        {
            throw new GalaxyMapLoadException($"The containing DLC has no AutoLoad.ini: {dlcDirectory.FullName}");
        }

        var mountValues = ReadMountValues(autoLoadPath);
        var autoLoad = new AutoloadIni(autoLoadPath);
        var modName = mountValues.GetValueOrDefault("ModName")?.Trim();
        if (string.IsNullOrWhiteSpace(modName) || string.IsNullOrWhiteSpace(autoLoad.ModName))
        {
            throw new GalaxyMapLoadException("AutoLoad.ini [ME1DLCMOUNT] must contain a non-empty ModName.");
        }
        var rawMount = mountValues.GetValueOrDefault("ModMount");
        if (!int.TryParse(rawMount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var modMount) ||
            modMount < 0 || autoLoad.ModMount != modMount)
        {
            throw new GalaxyMapLoadException(
                "AutoLoad.ini [ME1DLCMOUNT] must contain a valid non-negative integer ModMount.");
        }

        var dlcTag = dlcDirectory.Name;
        if (!GalaxyMapModule.IsValidTag(dlcTag))
        {
            throw new GalaxyMapLoadException(
                $"The DLC directory name '{dlcTag}' is not a valid module tag.");
        }
        var relativePackage = NormalizeRelative(Path.GetRelativePath(dlcDirectory.FullName, fullPackagePath));
        var existing = _profiles.Find(dlcTag, relativePackage);
        var isNew = existing is null;
        var profile = existing ?? new GalaxyMapModuleProfile(
            GalaxyMapModuleProfileStore.Identity(dlcTag, relativePackage),
            dlcTag,
            dlcDirectory.FullName,
            relativePackage,
            ModuleColor.White,
            MELocalization.INT,
            [],
            ModuleIdReservations.Empty,
            modName);

        if (existing is not null &&
            !string.Equals(existing.LastKnownDlcPath, dlcDirectory.FullName, StringComparison.OrdinalIgnoreCase))
        {
            var oldPackage = GalaxyMapModuleProfile.ResolveDlcRelativePath(
                existing.LastKnownDlcPath,
                existing.GalaxyMapPackage);
            if (File.Exists(oldPackage))
            {
                throw new GalaxyMapLoadException(
                    $"Profile collision: {dlcTag} and {relativePackage} already point to another installed DLC at " +
                    $"'{existing.LastKnownDlcPath}'. Unlink or forget that installation before relinking.");
            }
            profile = existing with { LastKnownDlcPath = dlcDirectory.FullName };
        }

        if (string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            profile = profile with { DisplayName = modName };
        }

        var module = profile.ToModule(modName, modMount);
        return new DiscoveredGalaxyMapModule(module, profile, isNew);
    }

    private static Dictionary<string, string> ReadMountValues(string autoLoadPath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inMountSection = false;
        foreach (var rawLine in File.ReadLines(autoLoadPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inMountSection = string.Equals(
                    line[1..^1].Trim(),
                    "ME1DLCMOUNT",
                    StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inMountSection)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            var key = line[..separator].Trim();
            if (key.Equals("ModName", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("ModMount", StringComparison.OrdinalIgnoreCase))
            {
                values[key] = line[(separator + 1)..].Trim();
            }
        }
        return values;
    }

    private static string NormalizeRelative(string path)
        => path.Replace('\\', '/').Trim('/');
}
