using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorerCore.Packages;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

internal sealed record BioMorphReferenceFace(
    string SourceName,
    string BaseHeadName,
    IReadOnlyDictionary<string, float> Features,
    IReadOnlyDictionary<string, float> ScalarOverrides,
    IReadOnlyDictionary<string, BioMorphReferenceColor> ColorOverrides);

internal readonly record struct BioMorphReferenceColor(float R, float G, float B, float A);

internal sealed record BioMorphReferenceCatalog(
    BioMorphSpecies Species,
    IReadOnlyList<BioMorphReferenceFace> Faces,
    string Error = null);

/// <summary>
/// Reads species-specific BioMorphFace populations already extracted by Asset Database generation.
/// No game packages are opened from the Morph Editor.
/// </summary>
internal static class BioMorphRandomizationCatalog
{
    private static readonly ConcurrentDictionary<string, Task<AssetDB>> DatabaseTasks = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<BioMorphReferenceCatalog> GetCatalogAsync(BioMorphSpecies species)
    {
        string databasePath = AssetDatabaseWindow.GetDBPath(MEGame.LE3);
        if (species == BioMorphSpecies.Unknown)
        {
            return new BioMorphReferenceCatalog(species, [], "The species could not be identified from m_oBaseHead.");
        }
        if (!File.Exists(databasePath))
        {
            return new BioMorphReferenceCatalog(species, [],
                "The LE3 Asset Database is not installed. Generate it from Tools > Asset Database, then try again.");
        }

        long databaseStamp = File.GetLastWriteTimeUtc(databasePath).Ticks;
        string cacheKey = $"{databasePath}|{databaseStamp}";
        AssetDB database = await DatabaseTasks.GetOrAdd(cacheKey, _ => LoadDatabaseAsync(databasePath));
        if (!string.Equals(database.DatabaseVersion, AssetDatabaseWindow.dbCurrentBuild, StringComparison.Ordinal))
        {
            return new BioMorphReferenceCatalog(species, [],
                $"The LE3 Asset Database is version {database.DatabaseVersion ?? "unknown"}; version {AssetDatabaseWindow.dbCurrentBuild} is required. Rebuild the database, then try again.");
        }
        if (database.MorphFaces.Count == 0)
        {
            return new BioMorphReferenceCatalog(species, [],
                $"The LE3 Asset Database contains no morph profiles. Rebuild it with database version {AssetDatabaseWindow.dbCurrentBuild}, then try again.");
        }

        List<BioMorphReferenceFace> faces = database.MorphFaces
            .Where(face => face.Species == species
                           && !face.IsMod
                           && (face.Features is { Length: > 0 }
                               || face.ScalarOverrides is { Length: > 0 }
                               || face.ColorOverrides is { Length: > 0 }))
            .Select(face => new BioMorphReferenceFace(
                GetSourceName(database, face),
                face.BaseHeadName,
                (face.Features ?? []).ToDictionary(feature => feature.Name, feature => feature.Value, StringComparer.OrdinalIgnoreCase),
                (face.ScalarOverrides ?? []).ToDictionary(scalar => scalar.Name, scalar => scalar.Value, StringComparer.OrdinalIgnoreCase),
                (face.ColorOverrides ?? []).ToDictionary(
                    color => color.Name,
                    color => new BioMorphReferenceColor(color.R, color.G, color.B, color.A),
                    StringComparer.OrdinalIgnoreCase)))
            .Where(face => (face.Features.Count > 0 || face.ScalarOverrides.Count > 0 || face.ColorOverrides.Count > 0)
                           && face.Features.Values.All(value => float.IsFinite(value) && Math.Abs(value) <= 2f)
                           && face.ScalarOverrides.Values.All(float.IsFinite)
                           && face.ColorOverrides.Values.All(color => float.IsFinite(color.R)
                                                                      && float.IsFinite(color.G)
                                                                      && float.IsFinite(color.B)
                                                                      && float.IsFinite(color.A)))
            .ToList();

        return faces.Count > 0
            ? new BioMorphReferenceCatalog(species, faces)
            : new BioMorphReferenceCatalog(species, [],
                $"The LE3 Asset Database contains no official {species.ToDisplayName()} morph profiles.");
    }

    private static async Task<AssetDB> LoadDatabaseAsync(string databasePath)
    {
        var database = new AssetDB();
        await AssetDatabaseWindow.LoadDatabase(databasePath, MEGame.LE3, database, CancellationToken.None);
        return database;
    }

    private static string GetSourceName(AssetDB database, BioMorphFaceRecord face)
    {
        string fileName = face.FileKey >= 0 && face.FileKey < database.FileList.Count
            ? database.FileList[face.FileKey].FileName
            : "Unknown package";
        return $"{fileName}: {face.MorphName}";
    }
}
