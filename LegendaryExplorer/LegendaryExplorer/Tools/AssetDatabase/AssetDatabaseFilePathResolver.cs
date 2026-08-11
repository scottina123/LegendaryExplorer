using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Tools.AssetDatabase;

/// <summary>
/// Resolves asset-database file keys without repeatedly walking the game installation.
/// </summary>
public static class AssetDatabaseFilePathResolver
{
    public static Task<Dictionary<int, string>> BuildIndexAsync(AssetDB database, MEGame game,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string gamePath = MEDirectories.GetDefaultGamePath(game);
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            {
                return [];
            }

            // GetAllGameFiles performs one cached traversal. Resolving each selection with
            // Directory.EnumerateFiles(..., AllDirectories) made every click walk the install again.
            List<string> installedFiles = MELoadedFiles.GetAllGameFiles(gamePath, game);
            cancellationToken.ThrowIfCancellationRequested();
            return BuildIndex(database.FileList, database.ContentDir, installedFiles, cancellationToken);
        }, cancellationToken);
    }

    internal static Dictionary<int, string> BuildIndex(IReadOnlyList<FileNameDirKeyPair> databaseFiles,
        IReadOnlyList<string> contentDirectories, IEnumerable<string> installedFiles,
        CancellationToken cancellationToken = default)
    {
        var candidatesByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in installedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(fileName))
            {
                if (!candidatesByName.TryGetValue(fileName, out List<string> candidates))
                {
                    candidates = [];
                    candidatesByName[fileName] = candidates;
                }
                candidates.Add(path);
            }
        }

        var resolvedPaths = new Dictionary<int, string>();
        for (int fileKey = 0; fileKey < databaseFiles.Count; fileKey++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileNameDirKeyPair databaseFile = databaseFiles[fileKey];
            if (databaseFile.DirectoryKey < 0 || databaseFile.DirectoryKey >= contentDirectories.Count
                || !candidatesByName.TryGetValue(databaseFile.FileName, out List<string> candidates))
            {
                continue;
            }

            string contentDirectory = contentDirectories[databaseFile.DirectoryKey];
            string match = candidates.FirstOrDefault(path =>
                string.Equals(GetContentDirectoryName(path), contentDirectory,
                    StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                resolvedPaths[fileKey] = match;
            }
        }

        return resolvedPaths;
    }

    private static string GetContentDirectoryName(string filePath)
    {
        DirectoryInfo directory = new FileInfo(filePath).Directory;
        while (directory != null)
        {
            if (directory.Name.StartsWith("Cooked", StringComparison.OrdinalIgnoreCase))
            {
                return directory.Parent?.Name;
            }
            directory = directory.Parent;
        }
        return null;
    }
}
