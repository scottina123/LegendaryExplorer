using System.IO;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Packages;

namespace LE1GalaxyMapEditor.Models;

public enum ModuleColor
{
    BaseGameBlue,
    Red,
    Pink,
    Purple,
    Cyan,
    Yellow,
    Green,
    White,
    Magenta
}

[Flags]
public enum PlanetTextureCategory
{
    None = 0,
    Continent = 1 << 0,
    Normals = 1 << 1,
    Ocean = 1 << 2,
    CityEmissive = 1 << 3,
    Atmosphere = 1 << 4
}

/// <summary>
/// Editor metadata which keeps a staged preview image independent from the
/// seek-free texture reference written to GalaxyMap_Planet.csv.
/// </summary>
public sealed record PlanetTextureLink(
    string Id,
    string InMemoryPath,
    string RelativePath,
    PlanetTextureCategory Categories);

public readonly record struct RowIdRange
{
    public RowIdRange(int start, int end)
    {
        if (start < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "A reserved row ID range cannot start below zero.");
        }

        if (end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), "A reserved row ID range cannot end before it starts.");
        }

        Start = start;
        End = end;
    }

    public int Start { get; }
    public int End { get; }
    public long Length => (long)End - Start + 1;

    public bool Contains(int rowId) => rowId >= Start && rowId <= End;

    public bool Overlaps(RowIdRange other) => Start <= other.End && other.Start <= End;

    public override string ToString() => $"{Start}-{End}";
}

/// <summary>
/// Per-table reservations for rows created by a module. PlotPlanet deliberately
/// uses the Planet reservation because both records must share a row ID.
/// </summary>
public sealed record ModuleIdReservations(
    RowIdRange? Cluster = null,
    RowIdRange? System = null,
    RowIdRange? Planet = null,
    RowIdRange? Map = null,
    RowIdRange? Relay = null)
{
    public static ModuleIdReservations Empty { get; } = new();

    public RowIdRange? GetRange(GalaxyMapTable table) => table switch
    {
        GalaxyMapTable.Cluster => Cluster,
        GalaxyMapTable.System => System,
        GalaxyMapTable.Planet or GalaxyMapTable.PlotPlanet => Planet,
        GalaxyMapTable.Map => Map,
        GalaxyMapTable.Relay => Relay,
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, null)
    };
}

/// <summary>
/// Immutable metadata for a mounted galaxy-map layer. BASEGAME is the only
/// module allowed to use blue and is always read-only.
/// </summary>
public sealed partial class GalaxyMapModule
{
    public const string BaseGameTag = "BASEGAME";

    public static GalaxyMapModule BaseGame { get; } = new(
        "Mass Effect 1 Base Game",
        BaseGameTag,
        ModuleColor.BaseGameBlue,
        folderPath: null,
        isReadOnly: true,
        loadOrder: 0,
        ModuleIdReservations.Empty,
        clusterTextureLinks: null,
        planetTextureLinks: null,
        isBaseGame: true);

    public GalaxyMapModule(
        string name,
        string tag,
        ModuleColor color,
        string? folderPath,
        bool isReadOnly,
        int loadOrder,
        ModuleIdReservations? reservations = null,
        IReadOnlyDictionary<int, string>? clusterTextureLinks = null,
        IReadOnlyList<PlanetTextureLink>? planetTextureLinks = null,
        string? profileId = null,
        string? dlcRootPath = null,
        string? galaxyMapPackagePath = null,
        MELocalization tlkLocale = MELocalization.INT,
        IReadOnlyList<string>? resourcePackagePaths = null)
        : this(name, tag, color, folderPath, isReadOnly, loadOrder,
            reservations ?? ModuleIdReservations.Empty, clusterTextureLinks, planetTextureLinks, isBaseGame: false,
            profileId, dlcRootPath, galaxyMapPackagePath, tlkLocale, resourcePackagePaths)
    {
    }

    private GalaxyMapModule(
        string name,
        string tag,
        ModuleColor color,
        string? folderPath,
        bool isReadOnly,
        int loadOrder,
        ModuleIdReservations reservations,
        IReadOnlyDictionary<int, string>? clusterTextureLinks,
        IReadOnlyList<PlanetTextureLink>? planetTextureLinks,
        bool isBaseGame,
        string? profileId = null,
        string? dlcRootPath = null,
        string? galaxyMapPackagePath = null,
        MELocalization tlkLocale = MELocalization.INT,
        IReadOnlyList<string>? resourcePackagePaths = null)
    {
        Name = RequireValue(name, nameof(name));
        Tag = RequireValue(tag, nameof(tag)).ToUpperInvariant();
        if (!IsValidTag(Tag))
        {
            throw new ArgumentException(
                "A module tag must contain only letters, numbers, underscores, or hyphens.", nameof(tag));
        }

        if (isBaseGame)
        {
            if (Tag != BaseGameTag || color != ModuleColor.BaseGameBlue || !isReadOnly)
            {
                throw new ArgumentException("BASEGAME must be blue and read-only.");
            }
        }
        else if (Tag == BaseGameTag || color == ModuleColor.BaseGameBlue)
        {
            throw new ArgumentException("BASEGAME and its blue colour are reserved for the built-in source data.");
        }

        Color = color;
        FolderPath = string.IsNullOrWhiteSpace(folderPath) ? null : Path.GetFullPath(folderPath);
        IsReadOnly = isReadOnly;
        if (loadOrder < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(loadOrder), "Mount priority cannot be negative.");
        }

        LoadOrder = loadOrder;
        Reservations = reservations;
        ClusterTextureLinks = new Dictionary<int, string>(
            clusterTextureLinks ?? new Dictionary<int, string>(),
            EqualityComparer<int>.Default);
        PlanetTextureLinks = (planetTextureLinks ?? [])
            .Select(link => new PlanetTextureLink(
                RequireValue(link.Id, nameof(planetTextureLinks)),
                RequireValue(link.InMemoryPath, nameof(planetTextureLinks)),
                RequireValue(link.RelativePath, nameof(planetTextureLinks)),
                link.Categories))
            .ToArray();
        if (!IsSupportedTlkLocale(tlkLocale))
        {
            throw new ArgumentOutOfRangeException(nameof(tlkLocale), "The module TLK locale is not supported.");
        }
        ProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim();
        DlcRootPath = string.IsNullOrWhiteSpace(dlcRootPath) ? null : Path.GetFullPath(dlcRootPath);
        GalaxyMapPackagePath = string.IsNullOrWhiteSpace(galaxyMapPackagePath)
            ? null
            : Path.GetFullPath(galaxyMapPackagePath);
        TlkLocale = tlkLocale;
        ResourcePackagePaths = (resourcePackagePaths ?? [])
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IsBaseGame = isBaseGame;
    }

    public string Name { get; }
    public string Tag { get; }
    public ModuleColor Color { get; }
    public string? FolderPath { get; }
    public bool IsReadOnly { get; }
    public int LoadOrder { get; }
    public ModuleIdReservations Reservations { get; }
    public IReadOnlyDictionary<int, string> ClusterTextureLinks { get; }
    public IReadOnlyList<PlanetTextureLink> PlanetTextureLinks { get; }
    public bool IsBaseGame { get; }
    public string? ProfileId { get; }
    public string? DlcRootPath { get; }
    public string? GalaxyMapPackagePath { get; }
    public MELocalization TlkLocale { get; }
    public IReadOnlyList<string> ResourcePackagePaths { get; }
    public bool IsPccBacked => GalaxyMapPackagePath is not null;

    public GalaxyMapModule With(
        string? name = null,
        string? tag = null,
        ModuleColor? color = null,
        int? loadOrder = null,
        ModuleIdReservations? reservations = null,
        IReadOnlyDictionary<int, string>? clusterTextureLinks = null,
        IReadOnlyList<PlanetTextureLink>? planetTextureLinks = null,
        MELocalization? tlkLocale = null,
        IReadOnlyList<string>? resourcePackagePaths = null)
    {
        if (IsBaseGame)
        {
            throw new InvalidOperationException("BASEGAME module metadata cannot be edited.");
        }

        return new GalaxyMapModule(
            name ?? Name,
            tag ?? Tag,
            color ?? Color,
            FolderPath,
            IsReadOnly,
            loadOrder ?? LoadOrder,
            reservations ?? Reservations,
            clusterTextureLinks ?? ClusterTextureLinks,
            planetTextureLinks ?? PlanetTextureLinks,
            ProfileId,
            DlcRootPath,
            GalaxyMapPackagePath,
            tlkLocale ?? TlkLocale,
            resourcePackagePaths ?? ResourcePackagePaths);
    }

    public override string ToString() => $"{Name} [{Tag}]";

    public static bool IsValidTag(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           TagPattern().IsMatch(value.Trim().ToUpperInvariant());

    public static string SuggestTag(string? value)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var character in (value ?? string.Empty).Trim().ToUpperInvariant())
        {
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var tag = builder.ToString().Trim('_', '-');
        return tag.Length == 0 ? "MODULE" : tag;
    }

    private static string RequireValue(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();

    [GeneratedRegex("^[A-Z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    private static bool IsSupportedTlkLocale(MELocalization locale)
        => locale is MELocalization.INT or MELocalization.DEU or MELocalization.ESN or
            MELocalization.FRA or MELocalization.ITA or MELocalization.JPN or
            MELocalization.POL or MELocalization.RUS;

    public static IReadOnlySet<MELocalization> SupportedTlkLocales { get; } = new HashSet<MELocalization>
    {
        MELocalization.INT,
        MELocalization.DEU,
        MELocalization.ESN,
        MELocalization.FRA,
        MELocalization.ITA,
        MELocalization.JPN,
        MELocalization.POL,
        MELocalization.RUS
    };
}

public sealed record GalaxyMapRowOrigin(GalaxyMapModule Module, bool OverridesLowerLayer)
{
    public string ModuleTag => Module.Tag;
    public ModuleColor Color => Module.Color;
    public bool IsBaseGame => Module.IsBaseGame;
    public bool IsReadOnly => Module.IsReadOnly;
}
