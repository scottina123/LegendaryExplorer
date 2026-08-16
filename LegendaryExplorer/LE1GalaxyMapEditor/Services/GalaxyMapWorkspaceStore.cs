using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Services;

public sealed record UnmanifestedReadOnlyModule(
    string Name,
    string Tag,
    ModuleColor Color,
    int LoadOrder,
    ModuleIdReservations Reservations,
    IReadOnlyDictionary<int, string> ClusterTextureLinks)
{
    public static UnmanifestedReadOnlyModule FromModule(GalaxyMapModule module)
    {
        if (!module.IsReadOnly)
        {
            throw new InvalidOperationException("Only an unmanifested read-only module needs metadata in workspace.json.");
        }

        return new(
            module.Name,
            module.Tag,
            module.Color,
            module.LoadOrder,
            module.Reservations,
            new Dictionary<int, string>(module.ClusterTextureLinks));
    }

    public GalaxyMapModule ToModule(string folderPath)
        => new(Name, Tag, Color, folderPath, isReadOnly: true, LoadOrder, Reservations, ClusterTextureLinks);
}

public sealed record RememberedModule(
    string FolderPath,
    UnmanifestedReadOnlyModule? UnmanifestedReadOnlyModule = null)
{
    public static RememberedModule FromModule(GalaxyMapModule module)
    {
        var folderPath = module.FolderPath
            ?? throw new InvalidOperationException("A remembered module must have a folder.");
        var hasManifest = File.Exists(Path.Combine(folderPath, GalaxyMapModuleManifestStore.FileName));
        return new(
            folderPath,
            !hasManifest && module.IsReadOnly
                ? global::LE1GalaxyMapEditor.Services.UnmanifestedReadOnlyModule.FromModule(module)
                : null);
    }

    public string DiagnosticTag => UnmanifestedReadOnlyModule?.Tag ?? string.Empty;
}

public sealed record RememberedWorkspace(
    IReadOnlyList<RememberedModule> Modules,
    string? ActiveModuleTag)
{
    public static RememberedWorkspace Empty { get; } = new([], null);
}

/// <summary>
/// Remembers which module folders form the local workspace and which writable
/// module is active. Module-owned metadata remains authoritative in module.json.
/// Only an unmanifested read-only source needs a metadata fallback here.
/// </summary>
public sealed class GalaxyMapWorkspaceStore
{
    public const string FileName = "workspace.json";
    private const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public GalaxyMapWorkspaceStore(string? settingsPath = null)
    {
        SettingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LE1GalaxyMapEditor",
                FileName)
            : Path.GetFullPath(settingsPath);
    }

    public string SettingsPath { get; }

    public RememberedWorkspace Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return RememberedWorkspace.Empty;
        }

        try
        {
            using var stream = File.OpenRead(SettingsPath);
            var dto = JsonSerializer.Deserialize<WorkspaceDto>(stream, Options)
                ?? throw new GalaxyMapLoadException("The remembered workspace file is empty.");
            if (dto.SchemaVersion is < 1 or > CurrentSchemaVersion)
            {
                throw new GalaxyMapLoadException(
                    $"The remembered workspace uses unsupported schema version {dto.SchemaVersion}.");
            }

            var modules = (dto.Modules ?? []).Select(module => FromDto(module, dto.SchemaVersion)).ToArray();
            return new RememberedWorkspace(modules, dto.ActiveModuleTag);
        }
        catch (GalaxyMapLoadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new GalaxyMapLoadException($"Could not read remembered modules: {exception.Message}", exception);
        }
    }

    public void Save(IEnumerable<RememberedModule> modules, string? activeModuleTag)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var dto = new WorkspaceDto
        {
            SchemaVersion = CurrentSchemaVersion,
            ActiveModuleTag = activeModuleTag,
            Modules = modules.Select(ToDto).ToList()
        };
        var json = JsonSerializer.Serialize(dto, Options) + "\r\n";
        AtomicFileWriter.Write(SettingsPath, new UTF8Encoding(false).GetBytes(json));
    }

    private static RememberedModule FromDto(ModuleDto dto, int schemaVersion)
    {
        var folderPath = dto.FolderPath ?? string.Empty;
        if (schemaVersion == 1)
        {
            return new RememberedModule(
                folderPath,
                dto.IsReadOnly ? FromMetadataDto(dto) : null);
        }

        return new RememberedModule(
            folderPath,
            dto.UnmanifestedReadOnlyModule is null
                ? null
                : FromMetadataDto(dto.UnmanifestedReadOnlyModule));
    }

    private static UnmanifestedReadOnlyModule FromMetadataDto(ModuleMetadataDto dto)
        => new(
            dto.Name ?? string.Empty,
            dto.Tag ?? string.Empty,
            dto.Color,
            dto.LoadOrder,
            new ModuleIdReservations(
                ToRange(dto.Reservations?.Cluster),
                ToRange(dto.Reservations?.System),
                ToRange(dto.Reservations?.Planet),
                ToRange(dto.Reservations?.Map),
                ToRange(dto.Reservations?.Relay)),
            dto.ClusterTextures ?? new Dictionary<int, string>());

    private static ModuleDto ToDto(RememberedModule module)
        => new()
        {
            FolderPath = module.FolderPath,
            UnmanifestedReadOnlyModule = module.UnmanifestedReadOnlyModule is null
                ? null
                : ToMetadataDto(module.UnmanifestedReadOnlyModule)
        };

    private static ModuleMetadataDto ToMetadataDto(UnmanifestedReadOnlyModule module)
        => new()
        {
            Name = module.Name,
            Tag = module.Tag,
            Color = module.Color,
            LoadOrder = module.LoadOrder,
            ClusterTextures = module.ClusterTextureLinks.ToDictionary(pair => pair.Key, pair => pair.Value),
            Reservations = new ReservationDto
            {
                Cluster = FromRange(module.Reservations.Cluster),
                System = FromRange(module.Reservations.System),
                Planet = FromRange(module.Reservations.Planet),
                Map = FromRange(module.Reservations.Map),
                Relay = FromRange(module.Reservations.Relay)
            }
        };

    private static RowIdRange? ToRange(RangeDto? range)
        => range is null ? null : new RowIdRange(range.Start, range.End);

    private static RangeDto? FromRange(RowIdRange? range)
        => range is null ? null : new RangeDto { Start = range.Value.Start, End = range.Value.End };

    private sealed class WorkspaceDto
    {
        public int SchemaVersion { get; set; }
        public string? ActiveModuleTag { get; set; }
        public List<ModuleDto>? Modules { get; set; }
    }

    private sealed class ModuleDto : ModuleMetadataDto
    {
        public string? FolderPath { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsReadOnly { get; set; }
        public ModuleMetadataDto? UnmanifestedReadOnlyModule { get; set; }
    }

    private class ModuleMetadataDto
    {
        public string? Name { get; set; }
        public string? Tag { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ModuleColor Color { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int LoadOrder { get; set; }
        public ReservationDto? Reservations { get; set; }
        public Dictionary<int, string>? ClusterTextures { get; set; }
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
