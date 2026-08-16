using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Services;

/// <summary>
/// Reads and atomically writes editor-only module metadata. The game-facing CSV
/// files remain ordinary Legendary Explorer partial-table exports.
/// </summary>
public sealed class GalaxyMapModuleManifestStore
{
    public const string FileName = "module.json";
    public const int CurrentSchemaVersion = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public GalaxyMapModule Load(string folderPath)
    {
        var fullFolderPath = RequireFolder(folderPath);
        var manifestPath = Path.Combine(fullFolderPath, FileName);
        if (!File.Exists(manifestPath))
        {
            throw new GalaxyMapLoadException($"The module folder has no {FileName}: {fullFolderPath}");
        }

        ModuleManifestDto manifest;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            manifest = JsonSerializer.Deserialize<ModuleManifestDto>(stream, JsonOptions)
                ?? throw new GalaxyMapLoadException($"{FileName} contains no module metadata.");
        }
        catch (GalaxyMapLoadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new GalaxyMapLoadException($"Could not read {manifestPath}: {exception.Message}", exception);
        }

        if (manifest.SchemaVersion is < 1 or > CurrentSchemaVersion)
        {
            throw new GalaxyMapLoadException(
                $"{manifestPath} uses unsupported schema version {manifest.SchemaVersion}; expected {CurrentSchemaVersion}.");
        }

        try
        {
            return new GalaxyMapModule(
                manifest.Name ?? string.Empty,
                manifest.Tag ?? string.Empty,
                manifest.Color,
                fullFolderPath,
                manifest.IsReadOnly,
                manifest.LoadOrder,
                new ModuleIdReservations(
                    ToRange(manifest.Reservations?.Cluster),
                    ToRange(manifest.Reservations?.System),
                    ToRange(manifest.Reservations?.Planet),
                    ToRange(manifest.Reservations?.Map),
                    ToRange(manifest.Reservations?.Relay)),
                manifest.ClusterTextures,
                manifest.PlanetTextures?.Select(texture => new PlanetTextureLink(
                    texture.Id ?? string.Empty,
                    texture.InMemoryPath ?? string.Empty,
                    texture.RelativePath ?? string.Empty,
                    texture.Categories)).ToArray());
        }
        catch (ArgumentException exception)
        {
            throw new GalaxyMapLoadException($"Invalid module metadata in {manifestPath}: {exception.Message}", exception);
        }
    }

    public void Save(GalaxyMapModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (module.IsBaseGame || module.FolderPath is null)
        {
            throw new InvalidOperationException("BASEGAME and modules without a folder cannot have a writable manifest.");
        }

        Save(module.FolderPath, module);
    }

    public void Save(string folderPath, GalaxyMapModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (module.IsBaseGame)
        {
            throw new InvalidOperationException("BASEGAME metadata is built into the application and cannot be written.");
        }

        var fullFolderPath = Path.GetFullPath(folderPath);
        if (module.FolderPath is not null &&
            !string.Equals(fullFolderPath, module.FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A module manifest can only be written to that module's own folder.");
        }

        var dto = new ModuleManifestDto
        {
            SchemaVersion = CurrentSchemaVersion,
            Name = module.Name,
            Tag = module.Tag,
            Color = module.Color,
            IsReadOnly = module.IsReadOnly,
            LoadOrder = module.LoadOrder,
            ClusterTextures = module.ClusterTextureLinks.ToDictionary(pair => pair.Key, pair => pair.Value),
            PlanetTextures = module.PlanetTextureLinks.Select(texture => new PlanetTextureDto
            {
                Id = texture.Id,
                InMemoryPath = texture.InMemoryPath,
                RelativePath = texture.RelativePath,
                Categories = texture.Categories
            }).ToList(),
            Reservations = new ReservationDto
            {
                Cluster = FromRange(module.Reservations.Cluster),
                System = FromRange(module.Reservations.System),
                Planet = FromRange(module.Reservations.Planet),
                Map = FromRange(module.Reservations.Map),
                Relay = FromRange(module.Reservations.Relay)
            }
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions) + "\r\n";
        AtomicFileWriter.Write(
            Path.Combine(fullFolderPath, FileName),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json));
    }

    private static string RequireFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            throw new GalaxyMapLoadException($"The module folder does not exist: {folderPath}");
        }

        return Path.GetFullPath(folderPath);
    }

    private static RowIdRange? ToRange(RangeDto? range)
        => range is null ? null : new RowIdRange(range.Start, range.End);

    private static RangeDto? FromRange(RowIdRange? range)
        => range is null ? null : new RangeDto { Start = range.Value.Start, End = range.Value.End };

    private sealed class ModuleManifestDto
    {
        public int SchemaVersion { get; set; }
        public string? Name { get; set; }
        public string? Tag { get; set; }
        public ModuleColor Color { get; set; }
        public bool IsReadOnly { get; set; }
        public int LoadOrder { get; set; }
        public Dictionary<int, string>? ClusterTextures { get; set; }
        public List<PlanetTextureDto>? PlanetTextures { get; set; }
        public ReservationDto? Reservations { get; set; }
    }

    private sealed class PlanetTextureDto
    {
        public string? Id { get; set; }
        public string? InMemoryPath { get; set; }
        public string? RelativePath { get; set; }
        public PlanetTextureCategory Categories { get; set; }
    }

    private sealed class ReservationDto
    {
        public RangeDto? Cluster { get; set; }
        public RangeDto? System { get; set; }
        public RangeDto? Planet { get; set; }
        public RangeDto? Map { get; set; }
        public RangeDto? Relay { get; set; }
    }

    private sealed class RangeDto
    {
        public int Start { get; set; }
        public int End { get; set; }
    }
}
