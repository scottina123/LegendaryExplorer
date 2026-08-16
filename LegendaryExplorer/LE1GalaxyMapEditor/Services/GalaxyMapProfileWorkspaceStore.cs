using System.IO;
using System.Text;
using System.Text.Json;

namespace LE1GalaxyMapEditor.Services;

public sealed record ProfileWorkspaceState(
    IReadOnlyList<string> ProfileIds,
    string? ActiveProfileId)
{
    public static ProfileWorkspaceState Empty { get; } = new([], null);
}

/// <summary>Persists only linked profile identities and the active profile.</summary>
public sealed class GalaxyMapProfileWorkspaceStore
{
    public const string FileName = "workspace.json";
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public GalaxyMapProfileWorkspaceStore(string? settingsPath = null)
    {
        SettingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LE1GalaxyMapEditor",
                FileName)
            : Path.GetFullPath(settingsPath);
    }

    public string SettingsPath { get; }

    public ProfileWorkspaceState Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return ProfileWorkspaceState.Empty;
        }
        try
        {
            using var stream = File.OpenRead(SettingsPath);
            var dto = JsonSerializer.Deserialize<WorkspaceDto>(stream, Options)
                ?? throw new GalaxyMapLoadException("The remembered workspace file is empty.");
            if (dto.SchemaVersion != CurrentSchemaVersion)
            {
                throw new GalaxyMapLoadException(
                    $"The remembered workspace uses unsupported schema version {dto.SchemaVersion}. " +
                    "Legacy folder-based workspace entries are not migrated.");
            }
            var ids = (dto.ProfileIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new ProfileWorkspaceState(ids, dto.ActiveProfileId);
        }
        catch (GalaxyMapLoadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new GalaxyMapLoadException($"Could not read remembered module profiles: {exception.Message}", exception);
        }
    }

    public void Save(IEnumerable<string> profileIds, string? activeProfileId)
    {
        ArgumentNullException.ThrowIfNull(profileIds);
        var ids = profileIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var dto = new WorkspaceDto
        {
            SchemaVersion = CurrentSchemaVersion,
            ProfileIds = ids,
            ActiveProfileId = string.IsNullOrWhiteSpace(activeProfileId) ? null : activeProfileId.Trim()
        };
        var json = JsonSerializer.Serialize(dto, Options) + "\r\n";
        AtomicFileWriter.Write(SettingsPath, new UTF8Encoding(false).GetBytes(json));
    }

    private sealed class WorkspaceDto
    {
        public int SchemaVersion { get; set; }
        public List<string>? ProfileIds { get; set; }
        public string? ActiveProfileId { get; set; }
    }
}
