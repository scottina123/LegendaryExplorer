using System.IO;
using LE1GalaxyMapEditor.Models;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.Classes;

namespace LE1GalaxyMapEditor.Services;

public sealed record PackageTextureReference(
    string InMemoryPath,
    string MemoryPath,
    string PackagePath,
    int ExportUIndex,
    string ObjectName,
    string BaseObjectName,
    GalaxyMapPackageFingerprint Fingerprint);

/// <summary>Resolves and decodes read-only Texture2D exports from registered PCCs.</summary>
public sealed class PccGalaxyMapTextureService
{
    private readonly object _indexLock = new();
    private readonly Dictionary<string, CachedTextureIndex> _indexes =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Diagnostics { get; private set; } = [];

    public IReadOnlyList<PackageTextureReference> Enumerate(GalaxyMapModule module)
        => Enumerate([module]);

    public IReadOnlyList<PackageTextureReference> Enumerate(IEnumerable<GalaxyMapModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var moduleList = modules.ToArray();
        var diagnostics = new List<string>();
        var textures = new List<PackageTextureReference>();
        foreach (var packagePath in CandidatePackages(moduleList, reference: null, diagnostics))
        {
            try
            {
                textures.AddRange(GetTextureIndex(packagePath));
            }
            catch (Exception exception)
            {
                diagnostics.Add($"Resource PCC could not be read '{packagePath}': {exception.Message}");
            }
        }
        Diagnostics = diagnostics;
        return textures
            .GroupBy(texture => texture.InMemoryPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(texture => texture.InMemoryPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public PackageTextureReference? Resolve(GalaxyMapModule module, string? reference)
        => Resolve([module], reference);

    public PackageTextureReference? Resolve(IEnumerable<GalaxyMapModule> modules, string? reference)
    {
        ArgumentNullException.ThrowIfNull(modules);
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }
        var normalizedReference = reference.Trim();
        var diagnostics = new List<string>();
        var packagePaths = CandidatePackages(modules.ToArray(), normalizedReference, diagnostics).ToArray();
        foreach (var packagePath in packagePaths)
        {
            try
            {
                var matches = GetTextureIndex(packagePath)
                    .Where(texture => IsExactTextureMatch(texture, normalizedReference))
                    .ToArray();
                if (matches.Length > 1)
                {
                    diagnostics.Add(
                        $"Texture reference '{normalizedReference}' is ambiguous inside '{packagePath}'.");
                    continue;
                }
                if (matches.Length == 1)
                {
                    Diagnostics = diagnostics;
                    return matches[0];
                }
            }
            catch (Exception exception)
            {
                diagnostics.Add($"Texture PCC could not be read '{packagePath}': {exception.Message}");
            }
        }

        var objectName = normalizedReference.Split('.').Last();
        var fallbackMatches = new List<PackageTextureReference>();
        foreach (var packagePath in packagePaths)
        {
            try
            {
                fallbackMatches.AddRange(GetTextureIndex(packagePath)
                    .Where(texture =>
                        string.Equals(texture.ObjectName, objectName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(texture.BaseObjectName, objectName, StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
                // The exact pass already recorded the package diagnostic.
            }
        }
        if (fallbackMatches.Count == 1)
        {
            Diagnostics = diagnostics;
            return fallbackMatches[0];
        }
        if (fallbackMatches.Count > 1)
        {
            diagnostics.Add(
                $"Texture object name '{objectName}' is ambiguous across the effective package stack.");
        }
        diagnostics.Add($"Texture2D export '{normalizedReference}' was not found in the registered PCCs.");
        Diagnostics = diagnostics;
        return null;
    }

    public byte[]? DecodePng(PackageTextureReference texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        return DecodePng([texture]).GetValueOrDefault(texture);
    }

    public IReadOnlyDictionary<PackageTextureReference, byte[]> DecodePng(
        IEnumerable<PackageTextureReference> textures)
    {
        ArgumentNullException.ThrowIfNull(textures);
        var requested = textures.Distinct().ToArray();
        var decoded = new Dictionary<PackageTextureReference, byte[]>();
        var diagnostics = new List<string>();
        foreach (var packageGroup in requested.GroupBy(
                     texture => texture.PackagePath,
                     StringComparer.OrdinalIgnoreCase))
        {
            DecodePackage(packageGroup.Key, packageGroup.ToArray(), decoded, diagnostics);
        }
        Diagnostics = diagnostics;
        return decoded;
    }

    private static void DecodePackage(
        string packagePath,
        IReadOnlyList<PackageTextureReference> textures,
        IDictionary<PackageTextureReference, byte[]> decoded,
        ICollection<string> diagnostics)
    {
        try
        {
            using var package = MEPackageHandler.OpenLE1Package(packagePath, forceLoadFromDisk: true);
            var exports = package.Exports.Where(IsTextureExport)
                .ToDictionary(export => export.UIndex);
            foreach (var texture in textures)
            {
                try
                {
                    if (!exports.TryGetValue(texture.ExportUIndex, out var export) ||
                        !string.Equals(
                            export.InstancedFullPath,
                            texture.InMemoryPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        diagnostics.Add($"Texture export '{texture.InMemoryPath}' changed or was removed.");
                        continue;
                    }

                    var texture2D = new Texture2D(export);
                    var mip = texture2D.GetMipWithDimension(1024, 1024) ?? texture2D.GetTopMip();
                    if (mip is null)
                    {
                        diagnostics.Add($"Texture export '{texture.InMemoryPath}' has no readable mip data.");
                        continue;
                    }
                    decoded[texture] = texture2D.GetPNG(mip);
                }
                catch (Exception exception)
                {
                    diagnostics.Add(
                        $"Texture export '{texture.InMemoryPath}' could not be decoded: {exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            diagnostics.Add($"Texture PCC could not be decoded '{packagePath}': {exception.Message}");
        }
    }

    private IReadOnlyList<PackageTextureReference> GetTextureIndex(string packagePath)
    {
        var fullPath = Path.GetFullPath(packagePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The texture PCC does not exist.", fullPath);
        }

        lock (_indexLock)
        {
            if (_indexes.TryGetValue(fullPath, out var cached) &&
                cached.Length == file.Length &&
                cached.LastWriteTimeUtc == file.LastWriteTimeUtc)
            {
                return cached.Textures;
            }

            var fingerprint = GalaxyMapPackageFingerprint.Capture(fullPath);
            using var package = MEPackageHandler.OpenLE1Package(fullPath, forceLoadFromDisk: true);
            var textures = package.Exports
                .Where(IsTextureExport)
                .Select(export => new PackageTextureReference(
                    export.InstancedFullPath,
                    export.MemoryFullPath,
                    fullPath,
                    export.UIndex,
                    export.ObjectName.Instanced,
                    export.ObjectName.Name,
                    fingerprint))
                .ToArray();
            _indexes[fullPath] = new CachedTextureIndex(
                fingerprint.Length,
                fingerprint.LastWriteTimeUtc,
                textures);
            return textures;
        }
    }

    private static IEnumerable<string> CandidatePackages(
        IReadOnlyList<GalaxyMapModule> modules,
        string? reference,
        ICollection<string> diagnostics)
    {
        var candidates = new List<string>();
        var packageQualifier = PackageQualifier(reference);
        foreach (var module in modules)
        {
            if (!string.IsNullOrWhiteSpace(packageQualifier) && module.FolderPath is not null)
            {
                candidates.Add(Path.Combine(module.FolderPath, packageQualifier + ".pcc"));
            }
            candidates.AddRange(module.ResourcePackagePaths);
            if (module.GalaxyMapPackagePath is not null)
            {
                candidates.Add(module.GalaxyMapPackagePath);
            }
        }

        var cookedPath = LE1Directory.CookedPCPath;
        if (!string.IsNullOrWhiteSpace(cookedPath))
        {
            if (!string.IsNullOrWhiteSpace(packageQualifier))
            {
                candidates.Add(Path.Combine(cookedPath, packageQualifier + ".pcc"));
            }
            candidates.Add(Path.Combine(cookedPath, "BIOA_NOR10_03_GM_LAY.pcc"));
        }
        else
        {
            diagnostics.Add(
                "The LE1 game path is unavailable; vanilla package textures cannot be resolved.");
        }

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
            {
                yield return Path.GetFullPath(path);
            }
            else if (modules.Any(module =>
                         module.ResourcePackagePaths.Contains(path, StringComparer.OrdinalIgnoreCase)))
            {
                diagnostics.Add($"Registered resource PCC is missing: {path}");
            }
        }
    }

    private static bool IsTextureExport(ExportEntry export)
        => !export.IsDefaultObject &&
           (string.Equals(export.ClassName, "Texture2D", StringComparison.Ordinal) ||
            export.ClassName.EndsWith("Texture2D", StringComparison.Ordinal));

    private static bool IsExactTextureMatch(PackageTextureReference texture, string reference)
    {
        return string.Equals(texture.InMemoryPath, reference, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(texture.MemoryPath, reference, StringComparison.OrdinalIgnoreCase) ||
               reference.EndsWith('.' + texture.InMemoryPath, StringComparison.OrdinalIgnoreCase) ||
               reference.EndsWith('.' + texture.MemoryPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string? PackageQualifier(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }
        var value = reference.Trim().Trim('\'');
        var quote = value.IndexOf('\'');
        if (quote >= 0 && quote < value.Length - 1)
        {
            value = value[(quote + 1)..].TrimEnd('\'');
        }
        var qualifier = value.Split('.', 2)[0];
        return qualifier.Length == 0 ? null : qualifier;
    }

    private sealed record CachedTextureIndex(
        long Length,
        DateTime LastWriteTimeUtc,
        IReadOnlyList<PackageTextureReference> Textures);
}
