using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Misc;

internal sealed record MovieFileCatalogItem(string Name, string FilePath, string Source)
{
    public string FileName => Path.GetFileName(FilePath);
}

/// <summary>
/// Discovers loose Bink movies in the basegame and enabled DLC Movies folders.
/// </summary>
internal static class MovieFileCatalog
{
    private static readonly EnumerationOptions RecursiveEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public static IReadOnlyList<MovieFileCatalogItem> FindMovies(MEGame game)
    {
        string bioGamePath = MEDirectories.GetBioGamePath(game);
        IEnumerable<string> dlcFolders = MELoadedDLC.GetEnabledDLCFolders(game);
        return FindMovies(bioGamePath, dlcFolders);
    }

    internal static IReadOnlyList<MovieFileCatalogItem> FindMovies(string bioGamePath, IEnumerable<string> dlcFolders)
    {
        var movies = new List<MovieFileCatalogItem>();
        var scannedMovieDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(bioGamePath))
        {
            AddMoviesFromDirectory(Path.Combine(bioGamePath, "Movies"), "Basegame", movies,
                scannedMovieDirectories);
        }

        foreach (string dlcFolder in dlcFolders ?? [])
        {
            if (string.IsNullOrWhiteSpace(dlcFolder) || !Directory.Exists(dlcFolder))
            {
                continue;
            }

            string source = Path.GetFileName(dlcFolder);
            IEnumerable<string> movieDirectories;
            try
            {
                movieDirectories = Directory
                    .EnumerateDirectories(dlcFolder, "Movies", RecursiveEnumerationOptions)
                    .ToList();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string movieDirectory in movieDirectories)
            {
                AddMoviesFromDirectory(movieDirectory, source, movies, scannedMovieDirectories);
            }
        }

        return movies
            .OrderBy(movie => movie.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(movie => movie.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(movie => movie.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddMoviesFromDirectory(string movieDirectory, string source,
        ICollection<MovieFileCatalogItem> movies, ISet<string> scannedMovieDirectories)
    {
        if (string.IsNullOrWhiteSpace(movieDirectory) || !Directory.Exists(movieDirectory))
        {
            return;
        }

        string fullMovieDirectory = Path.GetFullPath(movieDirectory);
        if (!scannedMovieDirectories.Add(fullMovieDirectory))
        {
            return;
        }

        try
        {
            foreach (string filePath in Directory.EnumerateFiles(fullMovieDirectory, "*", RecursiveEnumerationOptions))
            {
                string extension = Path.GetExtension(filePath);
                if (!extension.Equals(".bik", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".bk2", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                movies.Add(new MovieFileCatalogItem(Path.GetFileNameWithoutExtension(filePath), filePath, source));
            }
        }
        catch (IOException)
        {
            // Keep movies already found in this directory if another entry becomes unavailable.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore inaccessible movie folders without hiding results from the remaining roots.
        }
    }
}
