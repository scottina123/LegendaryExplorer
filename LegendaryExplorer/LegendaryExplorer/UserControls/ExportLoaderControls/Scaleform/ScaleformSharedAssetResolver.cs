using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Scaleform;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.UserControls.ExportLoaderControls.Scaleform;

/// <summary>
/// Resolves imported Scaleform artwork from the current package and the game's known
/// shared UI movies. Packages are opened for reading and kept alive for lazy texture
/// decoding until this resolver is disposed. Import URLs are never opened as files.
/// </summary>
internal sealed class ScaleformSharedAssetResolver : IDisposable
{
    private const int MaximumMovies = 24;
    private const int MaximumPackages = 8;
    private const int MaximumImportDepth = 24;
    private const int MaximumCachedDrawings = 4096;
    private const long MaximumMovieBytes = 128L * 1024 * 1024;
    private readonly ExportEntry source;
    private readonly Func<string, ImageSource> mainTextureResolver;
    private readonly Dictionary<string, MovieAsset> movies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IMEPackage> packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Movie, string Symbol, int Frame), Drawing> drawings = new();
    private readonly HashSet<(string Movie, string Symbol, int Frame)> resolving = new();
    private readonly HashSet<string> warningSet = new(StringComparer.Ordinal);
    private readonly List<string> warnings = new();
    private long movieBytes;
    private bool disposed;

    public IReadOnlyList<string> Warnings
    {
        get
        {
            foreach (var asset in movies.Values.Where(asset => asset != null))
            {
                foreach (string warning in asset.Renderer.Warnings) Warn($"{asset.Name}: {warning}");
                foreach (string warning in asset.Textures.Warnings) Warn($"{asset.Name}: {warning}");
            }
            return warnings.AsReadOnly();
        }
    }

    public ScaleformSharedAssetResolver(ExportEntry movie, Func<string, ImageSource> mainTextureResolver)
    {
        source = movie ?? throw new ArgumentNullException(nameof(movie));
        this.mainTextureResolver = mainTextureResolver;
    }

    /// <summary>Returns null with a descriptive warning when an imported symbol is unavailable.</summary>
    public Drawing Resolve(string movieFile, string symbolName, int frame)
    {
        if (disposed) return null;
        string movieName = NormalizeMovieName(movieFile);
        if (string.IsNullOrEmpty(movieName) || string.IsNullOrEmpty(symbolName))
        {
            Warn("A shared movie import has an empty movie or symbol name.");
            return null;
        }
        var key = (movieName.ToUpperInvariant(), symbolName, Math.Clamp(frame, 1, ushort.MaxValue));
        if (drawings.TryGetValue(key, out var cached)) return cached;
        if (resolving.Count >= MaximumImportDepth || !resolving.Add(key))
        {
            Warn($"Shared symbol {movieFile} → {symbolName} contains a recursive or excessively nested import; repeated contents are omitted.");
            return null;
        }
        Drawing drawing = null;
        try
        {
            var asset = GetMovie(movieName);
            if (asset == null) return null;
            if (!asset.Symbols.TryGetValue(symbolName, out ushort characterId))
            {
                Warn($"Shared movie {movieFile} does not export the symbol '{symbolName}'.");
                return null;
            }
            drawing = asset.Renderer.RenderCharacter(characterId, key.Item3);
            if (drawing?.CanFreeze == true) drawing.Freeze();
            return drawing;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Warn($"Could not preview shared symbol {movieFile} → {symbolName}: {ex.Message}");
            return null;
        }
        finally
        {
            resolving.Remove(key);
            if (drawings.Count >= MaximumCachedDrawings) drawings.Clear();
            drawings[key] = drawing;
        }
    }

    private MovieAsset GetMovie(string movieName)
    {
        if (movies.TryGetValue(movieName, out var existing)) return existing;
        // Cache failures too: a missing import must not repeatedly scan/load packages while panning.
        if (movies.Count >= MaximumMovies)
        {
            Warn($"The preview shared-movie limit ({MaximumMovies}) was reached; {movieName} is omitted.");
            return null;
        }
        movies[movieName] = null;
        var export = FindMovie(source.FileRef, movieName);
        if (export == null)
        {
            foreach (string packageName in GetPackageCandidates(movieName))
            {
                var package = GetPackage(packageName);
                if (package == null) continue;
                export = FindMovie(package, movieName);
                if (export != null) break;
            }
        }
        if (export == null)
        {
            Warn($"Shared movie {movieName} is unavailable. The preview searched this package and its game's known shared UI packages; verify the game's configured installation path.");
            return null;
        }
        string propertyName = export.ClassName == "BioSWF" ? "Data" : "RawData";
        byte[] bytes = export.GetProperty<ImmutableByteArrayProperty>(propertyName)?.Bytes;
        if (bytes == null) throw new InvalidDataException($"Shared movie {movieName} has no {propertyName} byte array.");
        if (bytes.LongLength > MaximumMovieBytes - movieBytes)
        {
            Warn($"Shared movie {movieName} exceeds the preview's shared-movie memory limit.");
            return null;
        }
        var document = ScaleformDocument.Parse(bytes);
        movieBytes += bytes.LongLength;
        var symbols = new Dictionary<string, ushort>(StringComparer.Ordinal);
        foreach (var symbol in ScaleformScene.Build(document).SymbolNames)
            symbols.TryAdd(symbol.Value, symbol.Key);
        var textures = new ScaleformTextureResolver(export);
        ImageSource ResolveTexture(string fileName) => textures.Resolve(fileName) ?? mainTextureResolver?.Invoke(fileName);
        var renderer = new ScaleformArtworkRenderer(document, ResolveTexture, Resolve);
        var asset = new MovieAsset(movieName, symbols, textures, renderer);
        movies[movieName] = asset;
        return asset;
    }

    private IMEPackage GetPackage(string packageName)
    {
        if (packages.TryGetValue(packageName, out var existing)) return existing;
        if (packages.Count >= MaximumPackages)
        {
            Warn($"The preview shared-package limit ({MaximumPackages}) was reached; {packageName} is omitted.");
            return null;
        }
        packages[packageName] = null;
        try
        {
            if (!MELoadedFiles.GetFilesLoadedInGame(source.Game, forceUseCached: true).TryGetValue(packageName, out string path))
            {
                Warn($"Shared UI package {packageName} was not found for {source.Game}.");
                return null;
            }
            // Preview only needs a few shared movies and their visible textures. Loading
            // every export in Startup synchronously makes merely selecting a movie stall.
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(source.FileRef.FilePath), StringComparison.OrdinalIgnoreCase))
                return source.FileRef;
            // Keep a read-only snapshot for lazy export reads without holding a game
            // package's file handle open while another Package Editor saves it.
            var snapshot = MEPackageHandler.ReadAllFileBytesIntoMemoryStream(path);
            ILazyLoadPackage package;
            try { package = MEPackageHandler.UnsafeLazyLoadFromStream(snapshot, path); }
            catch { snapshot.Dispose(); throw; }
            packages[packageName] = package;
            return package;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Warn($"Could not read shared UI package {packageName}: {ex.Message}");
            return null;
        }
    }

    private IEnumerable<string> GetPackageCandidates(string movieName)
    {
        if (movieName.Equals("gfxfonts", StringComparison.OrdinalIgnoreCase))
        {
            yield return "SFXGUI_Fonts.pcc";
            yield break;
        }
        if (movieName is not null && new[]
            {
                "PC_SharedAssets", "Xbox_ControllerIcons", "mainController", "BioMassFont",
                "BioMassFont_glyphs", "AeroLightFont", "AeroLightFont_glyphs"
            }.Contains(movieName, StringComparer.OrdinalIgnoreCase))
        {
            // These are the same shared movies used by the external JPEXS launcher.
            yield return source.Game is MEGame.ME3 or MEGame.LE3 ? "Startup.pcc" : "Startup_INT.pcc";
        }
    }

    private static ExportEntry FindMovie(IMEPackage package, string movieName)
    {
        var exports = package.Exports.Where(export => !export.IsDefaultObject && export.ClassName is "GFxMovieInfo" or "BioSWF").ToArray();
        var exact = exports.FirstOrDefault(export => string.Equals(NormalizeMovieName(export.ObjectName.Name), movieName, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return package is ILazyLoadPackage lazyExact ? lazyExact.LoadExport(exact.UIndex) : exact;
        foreach (var export in exports)
        {
            if (package is ILazyLoadPackage lazy) lazy.LoadExport(export.UIndex);
            string sourcePath = export.GetProperty<StrProperty>("SourceFilePath")?.Value;
            if (!string.IsNullOrEmpty(sourcePath) && string.Equals(NormalizeMovieName(sourcePath), movieName, StringComparison.OrdinalIgnoreCase)) return export;
        }
        return null;
    }

    private static string NormalizeMovieName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;
        string name = fileName.Trim().Replace('\\', '/');
        int query = name.IndexOfAny(['?', '#']);
        if (query >= 0) name = name[..query];
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name.EndsWith(".swf", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".gfx", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Equals("gfxfontlib", StringComparison.OrdinalIgnoreCase) ? "gfxfonts" : name;
    }

    private void Warn(string warning)
    {
        if (warningSet.Add(warning)) warnings.Add(warning);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        drawings.Clear(); resolving.Clear(); movies.Clear();
        foreach (var package in packages.Values.Where(package => package != null).Distinct()) package.Dispose();
        packages.Clear();
    }

    private sealed record MovieAsset(string Name, Dictionary<string, ushort> Symbols,
        ScaleformTextureResolver Textures, ScaleformArtworkRenderer Renderer);
}
