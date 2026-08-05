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

internal readonly record struct BioPlanetReferenceVector(float R, float G, float B, float A);

internal sealed record BioPlanetReferenceProfile(
    string SourceName,
    BioPlanetMaterialLayer Layer,
    IReadOnlyDictionary<string, float> Scalars,
    IReadOnlyDictionary<string, BioPlanetReferenceVector> Vectors);

internal sealed record BioPlanetReferenceCatalog(
    IReadOnlyList<BioPlanetReferenceProfile> Profiles,
    string Error = null);

/// <summary>
/// Reads official BioPlanet MIC populations extracted during Asset Database generation. No game
/// packages are opened from the Galaxy Map material editor.
/// </summary>
internal static class BioPlanetRandomizationCatalog
{
    private static readonly ConcurrentDictionary<string, Task<AssetDB>> DatabaseTasks = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<BioPlanetReferenceCatalog> GetCatalogAsync(MEGame game)
    {
        if (game != MEGame.LE3)
        {
            return new BioPlanetReferenceCatalog([], "Galaxy Map material randomization requires an LE3 Asset Database.");
        }

        string databasePath = AssetDatabaseWindow.GetDBPath(game);
        if (!File.Exists(databasePath))
        {
            return new BioPlanetReferenceCatalog([], "The LE3 Asset Database is not installed. Generate it from Tools > Asset Database, then try again.");
        }

        long databaseStamp = File.GetLastWriteTimeUtc(databasePath).Ticks;
        string cacheKey = $"{game}|{databasePath}|{databaseStamp}";
        AssetDB database = await DatabaseTasks.GetOrAdd(cacheKey, _ => LoadDatabaseAsync(databasePath, game));
        if (!string.Equals(database.DatabaseVersion, AssetDatabaseWindow.dbCurrentBuild, StringComparison.Ordinal))
        {
            return new BioPlanetReferenceCatalog([], 
                $"The LE3 Asset Database is version {database.DatabaseVersion ?? "unknown"}; version {AssetDatabaseWindow.dbCurrentBuild} is required. Rebuild it, then try again.");
        }
        if (database.BioPlanetMaterials.Count == 0)
        {
            return new BioPlanetReferenceCatalog([], "The LE3 Asset Database contains no BioPlanet material profiles. Rebuild it, then try again.");
        }

        List<BioPlanetReferenceProfile> profiles = database.BioPlanetMaterials
            .Where(profile => !profile.IsMod
                              && (profile.Scalars is { Length: > 0 } || profile.Vectors is { Length: > 0 }))
            .Select(profile => new BioPlanetReferenceProfile(
                GetSourceName(database, profile),
                profile.Layer,
                (profile.Scalars ?? []).ToDictionary(
                    scalar => scalar.Name, scalar => scalar.Value, StringComparer.OrdinalIgnoreCase),
                (profile.Vectors ?? []).ToDictionary(
                    vector => vector.Name,
                    vector => new BioPlanetReferenceVector(vector.R, vector.G, vector.B, vector.A),
                    StringComparer.OrdinalIgnoreCase)))
            .Where(profile => profile.Scalars.Values.All(float.IsFinite)
                              && profile.Vectors.Values.All(vector => float.IsFinite(vector.R)
                                                                      && float.IsFinite(vector.G)
                                                                      && float.IsFinite(vector.B)
                                                                      && float.IsFinite(vector.A)))
            .ToList();

        return profiles.Count > 0
            ? new BioPlanetReferenceCatalog(profiles)
            : new BioPlanetReferenceCatalog([], "The LE3 Asset Database contains no official BioPlanet material profiles.");
    }

    private static async Task<AssetDB> LoadDatabaseAsync(string databasePath, MEGame game)
    {
        var database = new AssetDB();
        await AssetDatabaseWindow.LoadDatabase(databasePath, game, database, CancellationToken.None);
        return database;
    }

    private static string GetSourceName(AssetDB database, BioPlanetMaterialProfileRecord profile)
    {
        string fileName = profile.FileKey >= 0 && profile.FileKey < database.FileList.Count
            ? database.FileList[profile.FileKey].FileName
            : "Unknown package";
        return $"{fileName}: {profile.PlanetName} ({profile.MaterialName})";
    }
}
