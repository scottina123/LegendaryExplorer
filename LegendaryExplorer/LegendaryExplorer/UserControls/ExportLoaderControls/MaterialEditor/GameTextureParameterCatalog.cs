using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using BinaryPack;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorer.UserControls.ExportLoaderControls.MaterialEditor;

/// <summary>
/// Collects known texture parameters across a game, independently of the material being edited.
/// </summary>
internal static class GameTextureParameterCatalog
{
    private sealed record SourceStamp(string Path, long LastWriteTicks, long Length)
    {
        internal static SourceStamp FromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new SourceStamp(null, 0, 0);
            var file = new FileInfo(path);
            return file.Exists
                ? new SourceStamp(file.FullName, file.LastWriteTimeUtc.Ticks, file.Length)
                : new SourceStamp(file.FullName, 0, 0);
        }
    }

    private sealed record CatalogKey(MEGame Game, SourceStamp Database, SourceStamp ReferenceShaders, string GamePath);
    private sealed record CacheEntry(CatalogKey Key, Lazy<Task<IReadOnlyList<NameReference>>> Names);
    private static readonly object CacheLock = new();
    private static readonly Dictionary<MEGame, CacheEntry> CachedNames = new();
    private static readonly Dictionary<MEGame, (CatalogKey Key, IReadOnlyList<SourceStamp> Packages)> ScannedCatalogs = new();

    internal static Task<IReadOnlyList<NameReference>> GetNamesAsync(MEGame game)
    {
        string cookedPath = MEDirectories.GetCookedPath(game);
        var key = new CatalogKey(game, SourceStamp.FromPath(AssetDatabaseWindow.GetDBPath(game)),
            SourceStamp.FromPath(cookedPath is null ? null : Path.Combine(cookedPath, RefShaderCacheReader.ShaderCacheName(game))),
            MEDirectories.GetDefaultGamePath(game));
        lock (CacheLock)
        {
            bool shouldReload = !CachedNames.TryGetValue(game, out var cached) || cached.Key != key;
            if (!shouldReload && cached.Names.IsValueCreated && cached.Names.Value.IsCompleted
                && ScannedCatalogs.TryGetValue(game, out var scanned) && scanned.Key == key)
                shouldReload = !scanned.Packages.SequenceEqual(GetInstalledPackageStamps(game));
            if (shouldReload)
            {
                cached = new CacheEntry(key, new Lazy<Task<IReadOnlyList<NameReference>>>(() => Task.Run(() => LoadNames(key))));
                CachedNames[game] = cached;
                ScannedCatalogs.Remove(game);
            }
            return cached.Names.Value;
        }
    }

    /// <summary>
    /// AssetDB stores parameter names as strings, without a separate FName number. Keep those
    /// strings literal; interpreting a trailing underscore and number would change real names.
    /// </summary>
    internal static IReadOnlyList<NameReference> GetNames(IEnumerable<MaterialRecord> materials) =>
        (materials ?? [])
        .Where(material => material?.MatSettings is not null)
        .SelectMany(material => material.MatSettings)
        .Where(setting => setting is not null && IsTextureParameterSetting(setting.Name) && IsUsableName(setting.Parm1))
        .Select(setting => setting.Parm1)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .Select(name => new NameReference(name)).ToArray();

    private static bool IsTextureParameterSetting(string settingName)
    {
        if (settingName is null)
            return false;
        if (settingName.StartsWith("MaterialExpression", StringComparison.Ordinal))
            settingName = settingName["MaterialExpression".Length..];
        return settingName is "TextureParameterValue" or "TextureObjectParameter"
            or "TextureSampleParameter" or "TextureSampleParameter2D" or "TextureSampleParameterCube"
            or "TextureSampleParameterNormal" or "TextureSampleParameterMovie" or "TextureSampleParameterSubUV"
            or "TextureSampleParameterMeshSubUV" or "TextureSampleParameterMeshSubUVBlend";
    }

    private static bool IsUsableName(string name) => !string.IsNullOrWhiteSpace(name)
        && !string.Equals(name, "n/a", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<NameReference> LoadNames(CatalogKey key)
    {
        var names = new List<NameReference>();
        bool hasCurrentMaterialDatabase = false;
        if (key.Database.Length > 0)
        {
            try
            {
                using var archive = new ZipArchive(new FileStream(key.Database.Path, FileMode.Open,
                    FileAccess.Read, FileShare.Read | FileShare.Delete));
                string build = AssetDatabaseWindow.dbCurrentBuild.Trim(' ', '*', '.');
                var entry = archive.GetEntry($"MasterDB.{key.Game}_{build}.bin");
                if (entry is not null)
                {
                    using var contents = new MemoryStream(checked((int)entry.Length));
                    using (var stream = entry.Open())
                        stream.CopyTo(contents);
                    var database = BinaryConverter.Deserialize<AssetDB>(contents.GetBuffer().AsSpan(0, (int)contents.Length));
                    if (database.Game == key.Game && database.DatabaseVersion == AssetDatabaseWindow.dbCurrentBuild
                        && database.Materials is { Count: > 0 })
                    {
                        hasCurrentMaterialDatabase = true;
                        names.AddRange(GetNames(database.Materials));
                    }
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Debug.WriteLine($"Could not read {key.Game} texture parameters from {key.Database.Path}: {exception.Message}");
            }
        }

        try
        {
            names.AddRange(RefShaderCacheReader.GetTextureParameterNames(key.Game));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Debug.WriteLine($"Could not read global {key.Game} reference shader parameters: {exception.Message}");
        }

        if (!hasCurrentMaterialDatabase)
        {
            var packages = GetInstalledPackageStamps(key.Game);
            names.AddRange(ScanInstalledPackages(key.Game, packages));
            lock (CacheLock)
            {
                if (CachedNames.TryGetValue(key.Game, out var cached) && cached.Key == key)
                    ScannedCatalogs[key.Game] = (key, packages);
            }
        }
        if (names.Count == 0)
            throw new InvalidOperationException($"The game-wide {key.Game} texture parameter catalog is unavailable. Configure the game path or generate a current {key.Game} Asset Database.");
        return DistinctNames(names);
    }

    private static IReadOnlyList<SourceStamp> GetInstalledPackageStamps(MEGame game) =>
        MELoadedFiles.GetFilesLoadedInGame(game, forceReload: true).Values
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".pcc" or ".upk" or ".u" or ".sfm")
            .Where(path => !Path.GetFileName(path).StartsWith("RefShaderCache", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(SourceStamp.FromPath).ToArray();

    private static IEnumerable<NameReference> ScanInstalledPackages(MEGame game, IEnumerable<SourceStamp> packages)
    {
        var names = new List<NameReference>();
        foreach (var source in packages)
        {
            string path = source.Path;
            try
            {
                using var package = MEPackageHandler.UnsafePartialLoad(path, IsRelevantExport);
                if (package.Game == game)
                    names.AddRange(GetNames(package));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Debug.WriteLine($"Could not scan texture parameters in {path}: {exception.Message}");
            }
        }
        return names;
    }

    private static bool IsRelevantExport(ExportEntry export) => !export.IsDefaultObject
        && (export.IsA("Material") || export.IsA("MaterialInstance")
            || export.IsA("MaterialExpressionTextureSampleParameter")
            || export.IsA("MaterialExpressionTextureObjectParameter") || export.ClassName == "ShaderCache");

    internal static IReadOnlyList<NameReference> GetNames(IMEPackage package)
    {
        var names = new List<NameReference>();
        foreach (var export in package.Exports.Where(export => export.IsDataLoaded() && IsRelevantExport(export)))
        {
            try
            {
                if (export.ClassName == "ShaderCache")
                {
                    var shaders = ObjectBinary.From<ShaderCache>(export);
                    foreach (var map in shaders.MaterialShaderMaps.Values)
                    {
                        CompiledMaterialParameterNames.AddExpressions(map.UniformPixelScalarExpressions, "TextureParameterValues", names);
                        CompiledMaterialParameterNames.AddExpressions(map.UniformPixelVectorExpressions, "TextureParameterValues", names);
                        CompiledMaterialParameterNames.AddExpressions(map.UniformVertexScalarExpressions, "TextureParameterValues", names);
                        CompiledMaterialParameterNames.AddExpressions(map.UniformVertexVectorExpressions, "TextureParameterValues", names);
                        CompiledMaterialParameterNames.AddExpressions(map.Uniform2DTextureExpressions, "TextureParameterValues", names);
                        CompiledMaterialParameterNames.AddExpressions(map.UniformCubeTextureExpressions, "TextureParameterValues", names);
                    }
                    continue;
                }

                var properties = export.GetProperties();
                if (export.IsA("MaterialExpressionTextureSampleParameter") || export.IsA("MaterialExpressionTextureObjectParameter"))
                {
                    if (properties.GetProp<NameProperty>("ParameterName") is { } name)
                        names.Add(name.Value);
                }
                if (export.IsA("MaterialInstance") && properties.GetProp<ArrayProperty<StructProperty>>("TextureParameterValues") is { } parameters)
                {
                    foreach (var parameter in parameters)
                        if (parameter.GetProp<NameProperty>("ParameterName") is { } name)
                            names.Add(name.Value);
                }
                // ME1/ME2 keep named compiled uniforms in each material's resource, rather than
                // in the reference shader maps used by ME3 and Legendary Edition.
                if ((package.Game is MEGame.ME1 or MEGame.ME2) && (export.IsA("Material") || export.IsA("MaterialInstance")))
                    names.AddRange(CompiledMaterialParameterNames.GetNames(export, "TextureParameterValues"));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Debug.WriteLine($"Could not read texture parameters on {export.InstancedFullPath}: {exception.Message}");
            }
        }
        return DistinctNames(names);
    }

    private static IReadOnlyList<NameReference> DistinctNames(IEnumerable<NameReference> source)
    {
        var seen = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var names = new List<NameReference>();
        foreach (var name in source.Where(name => IsUsableName(name.Name)))
        {
            if (!seen.TryGetValue(name.Name, out var numbers))
                seen.Add(name.Name, numbers = new HashSet<int>());
            if (numbers.Add(name.Number))
                names.Add(name);
        }
        return names.OrderBy(name => name.Instanced, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name.Name, StringComparer.OrdinalIgnoreCase).ThenBy(name => name.Number).ToArray();
    }
}
