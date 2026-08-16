using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Services;

/// <summary>
/// Resolves the texture names used by the galaxy-map CSV and decodes the game
/// textures without applying any source alpha channel.
/// </summary>
public sealed partial class GalaxyMapTextureService
{
    public const string GalaxyTextureReference = "BIOA_GalaxyMap_T.galaxy";
    public const string SystemTextureReference = "BIOA_GalaxyMap_T.stars01";
    private const int MaximumCachedTextures = 6;
    private static IReadOnlySet<string> SupportedImageExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"
        };

    private readonly Action<string, TimeSpan>? _decodeObserver;
    private readonly Dictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _leastRecentlyUsed = [];
    private readonly object _cacheLock = new();
    private readonly PccGalaxyMapTextureService _packageTextures = new();
    private readonly Dictionary<string, PackageTextureData> _packageDataCache =
        new(StringComparer.OrdinalIgnoreCase);

    public GalaxyMapTextureService()
    {
    }

    internal GalaxyMapTextureService(Action<string, TimeSpan> decodeObserver)
    {
        _decodeObserver = decodeObserver ?? throw new ArgumentNullException(nameof(decodeObserver));
    }

    /// <summary>
    /// Retained for source compatibility with legacy callers. Vanilla textures
    /// are package-backed; the directory is no longer used for map assets.
    /// </summary>
    public GalaxyMapTextureService(string textureDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(textureDirectory);
    }

    public BitmapSource? GetGalaxyTexture() => GetPackageTexture([], GalaxyTextureReference);

    public BitmapSource? GetSystemTexture() => GetPackageTexture([], SystemTextureReference);

    public BitmapSource? GetClusterTexture(string? backgroundReference)
        => GetPackageTexture([], backgroundReference);

    public BitmapSource? GetModuleClusterTexture(GalaxyMapModule module, int clusterRowId)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (module.FolderPath is null ||
            !module.ClusterTextureLinks.TryGetValue(clusterRowId, out var relativePath))
        {
            return null;
        }

        var fullPath = ResolveModuleTexturePath(module, relativePath);
        return fullPath is null ? null : LoadFileTexture(fullPath);
    }

    public BitmapSource? GetPackageTexture(GalaxyMapModule module, string? inMemoryReference)
        => GetPackageTexture([module], inMemoryReference);

    public BitmapSource? GetPackageTexture(
        IEnumerable<GalaxyMapModule> modules,
        string? inMemoryReference)
    {
        var moduleList = modules.ToArray();
        var resolved = _packageTextures.Resolve(moduleList, inMemoryReference);
        if (resolved is null)
        {
            return null;
        }
        var cacheKey = PackageCacheKey(resolved);
        return LoadCached(cacheKey, () =>
        {
            var png = _packageTextures.DecodePng(resolved);
            return png is null ? null : new MemoryStream(png, writable: false);
        });
    }

    public PackageTextureData? GetPackageTextureData(
        IEnumerable<GalaxyMapModule> modules,
        string? inMemoryReference)
    {
        if (string.IsNullOrWhiteSpace(inMemoryReference))
        {
            return null;
        }
        return GetPackageTextureData(modules, [inMemoryReference])
            .GetValueOrDefault(inMemoryReference);
    }

    public IReadOnlyDictionary<string, PackageTextureData> GetPackageTextureData(
        IEnumerable<GalaxyMapModule> modules,
        IEnumerable<string> inMemoryReferences)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(inMemoryReferences);
        var moduleList = modules.ToArray();
        var references = inMemoryReferences
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var resolvedByReference = references
            .Select(reference => (Reference: reference, Texture: _packageTextures.Resolve(moduleList, reference)))
            .Where(item => item.Texture is not null)
            .ToDictionary(
                item => item.Reference,
                item => item.Texture!,
                StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, PackageTextureData>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<PackageTextureReference>();
        foreach (var (reference, texture) in resolvedByReference)
        {
            var cacheKey = PackageCacheKey(texture);
            lock (_cacheLock)
            {
                if (_packageDataCache.TryGetValue(cacheKey, out var cached))
                {
                    result[reference] = cached;
                    continue;
                }
            }
            missing.Add(texture);
        }

        var decoded = _packageTextures.DecodePng(missing);
        foreach (var texture in missing.Distinct())
        {
            if (!decoded.TryGetValue(texture, out var contents))
            {
                continue;
            }
            var cacheKey = PackageCacheKey(texture);
            var data = new PackageTextureData(cacheKey, contents);
            lock (_cacheLock)
            {
                if (_packageDataCache.Count >= 32)
                {
                    _packageDataCache.Clear();
                }
                _packageDataCache[cacheKey] = data;
            }
        }

        foreach (var (reference, texture) in resolvedByReference)
        {
            var cacheKey = PackageCacheKey(texture);
            lock (_cacheLock)
            {
                if (_packageDataCache.TryGetValue(cacheKey, out var data))
                {
                    result[reference] = data;
                }
            }
        }
        return result;
    }

    public IReadOnlyList<PackageTextureReference> GetPackageTextureOptions(GalaxyMapModule module)
        => _packageTextures.Enumerate(module);

    public IReadOnlyList<PackageTextureReference> GetPackageTextureOptions(IEnumerable<GalaxyMapModule> modules)
        => _packageTextures.Enumerate(modules);

    public IReadOnlyList<PackageTextureReference> GetPlanetPackageTextureOptions(
        IEnumerable<GalaxyMapModule> modules,
        IEnumerable<string> referencedPlanetTextures)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(referencedPlanetTextures);
        var moduleList = modules.ToArray();
        var referencedPaths = referencedPlanetTextures
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var customResourcePackages = moduleList
            .Where(module => !module.IsBaseGame)
            .SelectMany(module => module.ResourcePackagePaths)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _packageTextures.Enumerate(moduleList)
            .Where(texture => PlanetAppearanceSchema.IsSelectablePlanetTextureObject(
                texture.BaseObjectName,
                customResourcePackages.Contains(texture.PackagePath),
                referencedPaths.Contains(texture.InMemoryPath)))
            .ToArray();
    }

    public IReadOnlyList<string> PackageTextureDiagnostics => _packageTextures.Diagnostics;

    private static string PackageCacheKey(PackageTextureReference texture)
        => $"pcc:{texture.PackagePath}:{texture.ExportUIndex}:{texture.Fingerprint.Sha256}";

    public BitmapSource? LoadTextureBytes(string cacheKey, byte[] contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        ArgumentNullException.ThrowIfNull(contents);
        return LoadCached($"memory:{cacheKey}", () => new MemoryStream(contents, writable: false));
    }

    internal bool CanDecodeImageBytes(byte[] contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        return DecodeTexture(
            () => new MemoryStream(contents, writable: false),
            observerKey: null) is not null;
    }

    internal static bool IsSupportedImagePath(string path)
        => !string.IsNullOrWhiteSpace(path) &&
           SupportedImageExtensions.Contains(Path.GetExtension(path));

    public static string? ResolveModuleTexturePath(GalaxyMapModule module, string relativePath)
    {
        if (module.FolderPath is null || string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var root = Path.GetFullPath(module.FolderPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : null;
    }

    public static string? ResolveClusterAssetName(string? backgroundReference)
    {
        if (string.IsNullOrWhiteSpace(backgroundReference))
        {
            return null;
        }

        var value = backgroundReference.Trim();
        if (value.Contains('/') || value.Contains('\\'))
        {
            return null;
        }

        if (value.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }
        else if (value.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^5];
        }

        var finalDot = value.LastIndexOf('.');
        var objectName = finalDot >= 0 ? value[(finalDot + 1)..] : value;
        var match = ClusterAssetPattern().Match(objectName);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            number is < 1 or > 99)
        {
            return null;
        }

        return $"Cluster{number:00}.jpg";
    }

    private BitmapSource? LoadFileTexture(string fullPath)
    {
        try
        {
            var file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                return null;
            }

            // The fingerprint prevents a committed replacement texture at the
            // same path from being hidden behind a stale cache entry.
            var cacheKey = $"file:{file.FullName}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
            return LoadCached(cacheKey, () => OpenSharedRead(file.FullName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private BitmapSource? LoadCached(string cacheKey, Func<Stream?> openStream)
    {
        lock (_cacheLock)
        {
            if (TryGetCached(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var texture = DecodeTexture(openStream, cacheKey);
        if (texture is null)
        {
            return null;
        }

        lock (_cacheLock)
        {
            // Another caller may have completed the same decode while this one
            // was outside the lock. Prefer the established cached instance.
            if (TryGetCached(cacheKey, out var cached))
            {
                return cached;
            }

            var node = _leastRecentlyUsed.AddFirst(cacheKey);
            _cache[cacheKey] = new CacheEntry(texture, node);
            while (_cache.Count > MaximumCachedTextures && _leastRecentlyUsed.Last is { } oldest)
            {
                _leastRecentlyUsed.RemoveLast();
                _cache.Remove(oldest.Value);
            }
        }

        return texture;
    }

    private BitmapSource? DecodeTexture(Func<Stream?> openStream, string? observerKey)
    {
        var decodeClock = Stopwatch.StartNew();
        try
        {
            using var stream = openStream();
            if (stream is null)
            {
                return null;
            }

            var decoded = BitmapFrame.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            // Bgr32 deliberately has no alpha channel. WPF retains the original
            // hidden RGB bytes while treating every pixel as fully opaque.
            var texture = new FormatConvertedBitmap(decoded, PixelFormats.Bgr32, null, 0);
            texture.Freeze();
            decodeClock.Stop();
            if (observerKey is not null)
            {
                _decodeObserver?.Invoke(observerKey, decodeClock.Elapsed);
            }
            return texture;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or FileFormatException
                                           or NotSupportedException
                                           or InvalidOperationException
                                           or ArgumentException)
        {
            return null;
        }
    }

    private bool TryGetCached(string cacheKey, out BitmapSource? texture)
    {
        if (!_cache.TryGetValue(cacheKey, out var entry))
        {
            texture = null;
            return false;
        }

        _leastRecentlyUsed.Remove(entry.Node);
        _leastRecentlyUsed.AddFirst(entry.Node);
        texture = entry.Texture;
        return true;
    }

    private static FileStream OpenSharedRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private sealed record CacheEntry(BitmapSource Texture, LinkedListNode<string> Node);

    [GeneratedRegex("^Cluster([0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClusterAssetPattern();
}

public sealed record PackageTextureData(string CacheKey, byte[] Contents);
