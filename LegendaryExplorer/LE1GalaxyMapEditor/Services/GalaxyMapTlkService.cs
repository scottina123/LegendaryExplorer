using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using LE1GalaxyMapEditor.Models;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK.ME1;

namespace LE1GalaxyMapEditor.Services;

public sealed record GalaxyMapTlkLookup(
    MELocalization Locale,
    int StringRef,
    string Text,
    string SourcePackage,
    int SourceExportUIndex,
    string SourceExportName,
    int Priority,
    bool IsOverride);

/// <summary>Editor-owned lookup index built from Legendary Explorer's LE1 TLK selection.</summary>
public sealed class GalaxyMapTlkService
{
    public const string TalkFileClassName = "BioTlkFile";
    private IReadOnlyDictionary<(MELocalization Locale, int StringRef), GalaxyMapTlkLookup> _index
        = new ReadOnlyDictionary<(MELocalization, int), GalaxyMapTlkLookup>(
            new Dictionary<(MELocalization, int), GalaxyMapTlkLookup>());
    private IReadOnlyList<string> _diagnostics = [];

    public GalaxyMapTlkService(string? cachePath = null)
    {
        CachePath = string.IsNullOrWhiteSpace(cachePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LegendaryExplorer",
                "LE1LoadedTLKs.JSON")
            : Path.GetFullPath(cachePath);
    }

    public string CachePath { get; }
    public IReadOnlyList<string> Diagnostics => _diagnostics;
    public IReadOnlySet<MELocalization> AvailableLocales { get; private set; }
        = new HashSet<MELocalization>();

    public void Reload(IEnumerable<GalaxyMapModule>? modules = null)
    {
        var diagnostics = new List<string>();
        var loaded = new List<LoadedTlkSource>();
        LoadModuleTalkFiles(modules ?? [], loaded, diagnostics);

        if (!File.Exists(CachePath))
        {
            diagnostics.Add($"Legendary Explorer's LE1 TLK cache was not found: {CachePath}");
            Publish(loaded, diagnostics);
            return;
        }

        IReadOnlyList<TlkCacheEntry> entries;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(CachePath));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("The root value must be an array.");
            }
            entries = document.RootElement.EnumerateArray()
                .Select((element, index) => ParseEntry(element, index))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            diagnostics.Add($"Legendary Explorer's LE1 TLK cache could not be read: {exception.Message}");
            Publish(loaded, diagnostics);
            return;
        }

        foreach (var entry in entries)
        {
            if (!entry.IsValid)
            {
                diagnostics.Add(entry.Error!);
                continue;
            }
            if (!File.Exists(entry.PackagePath))
            {
                diagnostics.Add($"TLK selection {entry.Index + 1} points to a missing PCC: {entry.PackagePath}");
                continue;
            }

            try
            {
                using var package = MEPackageHandler.OpenLE1Package(entry.PackagePath!, forceLoadFromDisk: true);
                var export = package.GetUExport(entry.ExportUIndex);
                if (!string.Equals(export.ClassName, TalkFileClassName, StringComparison.Ordinal))
                {
                    throw new GalaxyMapLoadException(
                        $"export {entry.ExportUIndex} is {export.ClassName}, not {TalkFileClassName}");
                }
                var talkFile = new ME1TalkFile(export);
                var locale = ResolvePackageLocale(entry.PackagePath!, talkFile.Localization);
                if (locale is null)
                {
                    diagnostics.Add(
                        $"TLK selection {entry.Index + 1} has unsupported/unknown locale in package name: " +
                        entry.PackagePath);
                    continue;
                }
                loaded.Add(new LoadedTlkSource(talkFile, locale.Value, entry.Index, IsModuleSource: false));
            }
            catch (Exception exception)
            {
                diagnostics.Add(
                    $"TLK selection {entry.Index + 1} could not be loaded from '{entry.PackagePath}', " +
                    $"export {entry.ExportUIndex}: {exception.Message}");
            }
        }

        Publish(loaded, diagnostics);
    }

    public GalaxyMapTlkLookup? Find(MELocalization locale, int stringRef)
        => _index.GetValueOrDefault((locale, stringRef));

    private void Publish(
        IReadOnlyList<LoadedTlkSource> loaded,
        IReadOnlyList<string> diagnostics)
    {
        var result = new Dictionary<(MELocalization, int), GalaxyMapTlkLookup>();
        AddInPriorityOrder(loaded.Where(item => item.IsModuleSource)
            .OrderByDescending(item => item.Priority)
            .ThenByDescending(item => item.TalkFile.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => item.TalkFile.UIndex));
        AddInPriorityOrder(loaded.Where(item => !item.IsModuleSource && item.TalkFile.IsLE1OverrideTLK)
            .OrderByDescending(item => item.Priority));
        AddInPriorityOrder(loaded.Where(item => !item.IsModuleSource && !item.TalkFile.IsLE1OverrideTLK)
            .OrderByDescending(item => item.Priority));
        _index = new ReadOnlyDictionary<(MELocalization, int), GalaxyMapTlkLookup>(result);
        _diagnostics = diagnostics.ToArray();
        AvailableLocales = loaded.Select(item => item.Locale).ToHashSet();
        return;

        void AddInPriorityOrder(IEnumerable<LoadedTlkSource> sources)
        {
            foreach (var source in sources)
            {
                var talkFile = source.TalkFile;
                foreach (var stringRef in talkFile.StringRefs)
                {
                    var key = (source.Locale, stringRef.StringID);
                    result.TryAdd(key, new GalaxyMapTlkLookup(
                        source.Locale,
                        stringRef.StringID,
                        stringRef.Data ?? string.Empty,
                        talkFile.FilePath,
                        talkFile.UIndex,
                        talkFile.Name,
                        source.Priority,
                        talkFile.IsLE1OverrideTLK));
                }
            }
        }
    }

    private static void LoadModuleTalkFiles(
        IEnumerable<GalaxyMapModule> modules,
        ICollection<LoadedTlkSource> loaded,
        ICollection<string> diagnostics)
    {
        foreach (var module in modules
                     .Where(module => module.IsPccBacked && !string.IsNullOrWhiteSpace(module.FolderPath))
                     .OrderBy(module => module.LoadOrder)
                     .ThenBy(module => module.Tag, StringComparer.OrdinalIgnoreCase))
        {
            string[] packagePaths;
            try
            {
                packagePaths = Directory.EnumerateFiles(module.FolderPath!, "*.pcc", SearchOption.TopDirectoryOnly)
                    .Where(path => Path.GetFileNameWithoutExtension(path)
                        .Contains("GlobalTlk", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add($"Could not inspect {module.Name} [{module.Tag}] for GlobalTlk PCCs: {exception.Message}");
                continue;
            }

            foreach (var packagePath in packagePaths)
            {
                try
                {
                    using var package = MEPackageHandler.OpenLE1Package(packagePath, forceLoadFromDisk: true);
                    var exports = package.Exports
                        .Where(export => string.Equals(export.ClassName, TalkFileClassName, StringComparison.Ordinal))
                        .OrderBy(export => export.UIndex)
                        .ToArray();
                    if (exports.Length == 0)
                    {
                        diagnostics.Add($"GlobalTlk PCC for {module.Name} [{module.Tag}] has no {TalkFileClassName} export: {packagePath}");
                        continue;
                    }

                    foreach (var export in exports)
                    {
                        var talkFile = new ME1TalkFile(export);
                        var locale = ResolvePackageLocale(packagePath, talkFile.Localization);
                        if (locale is null)
                        {
                            diagnostics.Add(
                                $"GlobalTlk export {export.UIndex} for {module.Name} [{module.Tag}] has " +
                                $"an unsupported/unknown locale suffix: {packagePath}");
                            continue;
                        }
                        loaded.Add(new LoadedTlkSource(talkFile, locale.Value, module.LoadOrder, IsModuleSource: true));
                    }
                }
                catch (Exception exception)
                {
                    diagnostics.Add(
                        $"GlobalTlk PCC for {module.Name} [{module.Tag}] could not be loaded from " +
                        $"'{packagePath}': {exception.Message}");
                }
            }
        }
    }

    /// <summary>
    /// LE1 package names are the authoritative locale boundary. This avoids
    /// Legendary Explorer's legacy GE/DE naming and prevents optional HU files
    /// from being indexed as INT when their embedded metadata is ambiguous.
    /// Unsuffixed GlobalTlk packages retain their parsed locale (normally INT).
    /// </summary>
    public static MELocalization? ResolvePackageLocale(string packagePath, MELocalization parsedLocale)
    {
        var name = Path.GetFileNameWithoutExtension(packagePath);
        var marker = name.LastIndexOf("GlobalTlk_", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            if (name.EndsWith("GlobalTlk", StringComparison.OrdinalIgnoreCase))
            {
                return MELocalization.INT;
            }
            return GalaxyMapModule.SupportedTlkLocales.Contains(parsedLocale) ? parsedLocale : null;
        }

        var suffix = name[(marker + "GlobalTlk_".Length)..].ToUpperInvariant();
        return suffix switch
        {
            "INT" => MELocalization.INT,
            "DE" => MELocalization.DEU,
            "ES" => MELocalization.ESN,
            "FR" => MELocalization.FRA,
            "IT" => MELocalization.ITA,
            "JA" or "JP" => MELocalization.JPN,
            "PL" => MELocalization.POL,
            "RU" => MELocalization.RUS,
            _ => null
        };
    }

    private static TlkCacheEntry ParseEntry(JsonElement element, int index)
    {
        try
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                var values = element.EnumerateArray().ToArray();
                return values.Length >= 2
                    ? Valid(index, values[0].GetInt32(), values[1].GetString())
                    : Invalid(index, "must contain an export UIndex and PCC path");
            }
            if (element.ValueKind != JsonValueKind.Object)
            {
                return Invalid(index, "must be an object or two-item array");
            }

            int? exportIndex = null;
            string? packagePath = null;
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("Item1", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("uindex", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("exportnum", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("exportUIndex", StringComparison.OrdinalIgnoreCase))
                {
                    exportIndex = property.Value.GetInt32();
                }
                else if (property.Name.Equals("Item2", StringComparison.OrdinalIgnoreCase) ||
                         property.Name.Equals("filename", StringComparison.OrdinalIgnoreCase) ||
                         property.Name.Equals("filePath", StringComparison.OrdinalIgnoreCase) ||
                         property.Name.Equals("packagePath", StringComparison.OrdinalIgnoreCase))
                {
                    packagePath = property.Value.GetString();
                }
            }
            return exportIndex is null
                ? Invalid(index, "has no export UIndex")
                : Valid(index, exportIndex.Value, packagePath);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or
                                           OverflowException or ArgumentException or IOException or NotSupportedException)
        {
            return Invalid(index, exception.Message);
        }
    }

    private static TlkCacheEntry Valid(int index, int exportUIndex, string? packagePath)
        => exportUIndex <= 0 || string.IsNullOrWhiteSpace(packagePath)
            ? Invalid(index, "contains an invalid export UIndex or PCC path")
            : new(index, exportUIndex, Path.GetFullPath(packagePath), null);

    private static TlkCacheEntry Invalid(int index, string message)
        => new(index, 0, null, $"TLK selection {index + 1} {message}.");

    private sealed record TlkCacheEntry(
        int Index,
        int ExportUIndex,
        string? PackagePath,
        string? Error)
    {
        public bool IsValid => Error is null;
    }

    private sealed record LoadedTlkSource(
        ME1TalkFile TalkFile,
        MELocalization Locale,
        int Priority,
        bool IsModuleSource);
}
