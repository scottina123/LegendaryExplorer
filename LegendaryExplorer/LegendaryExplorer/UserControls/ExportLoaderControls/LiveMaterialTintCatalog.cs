using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

internal sealed record LiveMaterialTintProfile(
    string SourceName,
    string MaterialName,
    string ParentMaterialKey,
    IReadOnlyDictionary<string, LinearColor> Tints);

internal sealed record LiveMaterialTintCatalogResult(
    IReadOnlyList<LiveMaterialTintProfile> Profiles,
    string Error = null);

/// <summary>
/// Reads official material tint profiles already extracted by Asset Database generation.
/// No game packages are opened from the live material editor.
/// </summary>
internal static class LiveMaterialTintCatalog
{
    private sealed record CatalogKey(MEGame Game, string Path, long LastWriteTicks, long Length);
    private sealed record CacheEntry(CatalogKey Key, Lazy<Task<LiveMaterialTintCatalogResult>> Catalog);
    private static readonly object CacheLock = new();
    private static readonly Dictionary<MEGame, CacheEntry> CachedCatalogs = new();
    private static readonly Regex ColorPattern = new(
        @"^\s*R:\s*(?<R>\S+)\s+G:\s*(?<G>\S+)\s+B:\s*(?<B>\S+)\s+A:\s*(?<A>\S+)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static async Task<LiveMaterialTintCatalogResult> GetCatalogAsync(MEGame game)
    {
        if (!game.IsMEGame())
            return new LiveMaterialTintCatalogResult([], "BioWare tint randomization is only available for Mass Effect games.");

        string databasePath = AssetDatabaseWindow.GetDBPath(game);
        var file = new FileInfo(databasePath);
        if (!file.Exists)
        {
            return new LiveMaterialTintCatalogResult([],
                $"The {game} Asset Database is not installed. Generate it from Tools > Asset Database, then try again.");
        }

        var key = new CatalogKey(game, file.FullName, file.LastWriteTimeUtc.Ticks, file.Length);
        CacheEntry cached;
        lock (CacheLock)
        {
            if (!CachedCatalogs.TryGetValue(game, out cached) || cached.Key != key)
            {
                cached = new CacheEntry(key, new Lazy<Task<LiveMaterialTintCatalogResult>>(() => LoadCatalogAsync(key)));
                CachedCatalogs[game] = cached;
            }
        }

        try
        {
            LiveMaterialTintCatalogResult result = await cached.Catalog.Value;
            if (result.Error is not null)
                RemoveCachedEntry(game, cached);
            return result;
        }
        catch
        {
            // A failed read can be transient; allow the next click to retry the same database.
            RemoveCachedEntry(game, cached);
            throw;
        }
    }

    private static void RemoveCachedEntry(MEGame game, CacheEntry cached)
    {
        lock (CacheLock)
        {
            if (CachedCatalogs.TryGetValue(game, out var current) && ReferenceEquals(current, cached))
                CachedCatalogs.Remove(game);
        }
    }

    private static async Task<LiveMaterialTintCatalogResult> LoadCatalogAsync(CatalogKey key)
    {
        var database = new AssetDB();
        await AssetDatabaseWindow.LoadDatabase(key.Path, key.Game, database, CancellationToken.None);
        if (!string.Equals(database.DatabaseVersion, AssetDatabaseWindow.dbCurrentBuild, StringComparison.Ordinal))
        {
            return new LiveMaterialTintCatalogResult([],
                $"The {key.Game} Asset Database is version {database.DatabaseVersion ?? "unknown"}; version {AssetDatabaseWindow.dbCurrentBuild} is required. Rebuild it, then try again.");
        }
        if (database.Game != key.Game)
            return new LiveMaterialTintCatalogResult([], $"The Asset Database does not contain {key.Game} data. Rebuild it from Tools > Asset Database, then try again.");

        IReadOnlyList<LiveMaterialTintProfile> profiles = await Task.Run(() => BuildProfiles(database));
        return profiles.Count > 0
            ? new LiveMaterialTintCatalogResult(profiles)
            : new LiveMaterialTintCatalogResult([],
                $"The {key.Game} Asset Database contains no verified official BioWare tint profiles. Rebuild it from Tools > Asset Database, then try again.");
    }

    internal static IReadOnlyList<LiveMaterialTintProfile> BuildProfiles(AssetDB database)
    {
        ArgumentNullException.ThrowIfNull(database);
        MaterialRecord[] materials = (database.Materials ?? [])
            .Where(material => material?.MatSettings?.Any(IsTintSetting) == true)
            .ToArray();
        var materialUsages = materials
            .SelectMany(material => material.Usages ?? [])
            .Where(usage => usage is not null)
            .Select(usage => (usage.FileKey, usage.UIndex))
            .ToHashSet();
        var officialUsages = new Dictionary<(int FileKey, int UIndex), bool>();
        foreach (ClassUsage usage in (database.ClassRecords ?? [])
                     .Where(record => record is not null)
                     .SelectMany(record => record.Usages ?? []))
        {
            var usageKey = (usage.FileKey, usage.UIndex);
            if (materialUsages.Contains(usageKey))
            {
                officialUsages[usageKey] = !usage.IsMod
                    && (!officialUsages.TryGetValue(usageKey, out bool previous) || previous);
            }
        }

        var profiles = new List<LiveMaterialTintProfile>();
        foreach (MaterialRecord material in materials)
        {
            // Material settings are taken from the first copy scanned. If any copy came from
            // a mod, the database cannot prove which copy supplied these values, so exclude it.
            if (material.Usages is not { Count: > 0 }
                || !material.Usages.All(usage => usage is not null
                    && officialUsages.TryGetValue((usage.FileKey, usage.UIndex), out bool official) && official))
                continue;

            var tints = new Dictionary<string, LinearColor>(StringComparer.OrdinalIgnoreCase);
            foreach (MatSetting setting in material.MatSettings.Where(IsTintSetting))
                if (TryParseColor(setting.Parm2, out LinearColor color))
                    tints[setting.Parm1] = color;
            if (tints.Count == 0)
                continue;

            MatUsage source = material.Usages[0];
            string packageName = source.FileKey >= 0 && source.FileKey < database.FileList.Count
                ? database.FileList[source.FileKey].FileName
                : "Unknown package";
            profiles.Add(new LiveMaterialTintProfile(
                $"{packageName}: {material.MaterialName}", material.MaterialName, material.ParentMaterialKey, tints));
        }
        return profiles;
    }

    private static bool IsTintSetting(MatSetting setting) => setting is not null
        && (string.Equals(setting.Name, "VectorParameter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(setting.Name, "MaterialExpressionVectorParameter", StringComparison.OrdinalIgnoreCase))
        && setting.Parm1?.StartsWith("TNT_", StringComparison.OrdinalIgnoreCase) == true;

    private static bool TryParseColor(string value, out LinearColor color)
    {
        color = default;
        if (value is null)
            return false;
        Match match = ColorPattern.Match(value);
        if (!match.Success
            || !TryParseComponent(match.Groups["R"].Value, out float r)
            || !TryParseComponent(match.Groups["G"].Value, out float g)
            || !TryParseComponent(match.Groups["B"].Value, out float b)
            || !TryParseComponent(match.Groups["A"].Value, out float a))
            return false;
        color = new LinearColor(r, g, b, a);
        return true;
    }

    private static bool TryParseComponent(string value, out float component)
    {
        // The scanner stored these strings using the generation machine's culture. Neither
        // format emits thousands separators, so decimal commas can be normalized safely.
        return float.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out component)
            && float.IsFinite(component);
    }
}
