using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Services;

public sealed record PlanetAppearanceTemplate(
    string Name,
    string Description,
    DateTimeOffset CreatedUtc,
    IReadOnlyDictionary<string, string> Values)
{
    public PlanetAppearance ToAppearance()
    {
        var appearance = new PlanetAppearance(PlanetAppearanceSchema.Columns.Select(column =>
            new KeyValuePair<string, string>(column, string.Empty)));
        foreach (var (column, value) in Values)
        {
            if (PlanetAppearanceSchema.IsAppearanceColumn(column) &&
                !column.Equals("Shader", StringComparison.OrdinalIgnoreCase))
            {
                appearance[column] = value;
            }
        }

        return appearance;
    }
}

public sealed class PlanetAppearanceTemplateStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly string _folder;
    private IReadOnlyList<string> _warnings = [];

    public PlanetAppearanceTemplateStore(string? folder = null)
    {
        _folder = folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LE1GalaxyMapEditor",
            "PlanetTemplates");
    }

    public IReadOnlyList<string> Warnings => _warnings;

    public IReadOnlyList<PlanetAppearanceTemplate> LoadAll()
    {
        var warnings = new List<string>();
        string[] paths;
        try
        {
            paths = Directory.GetFiles(_folder, "*.json");
        }
        catch (DirectoryNotFoundException)
        {
            _warnings = warnings;
            return [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Personal Planet templates could not be enumerated: {exception.Message}");
            _warnings = warnings;
            return [];
        }

        var templates = new List<PlanetAppearanceTemplate>();
        foreach (var path in paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var file = JsonSerializer.Deserialize<TemplateFile>(File.ReadAllText(path), SerializerOptions);
                if (file is not { Version: CurrentVersion } || string.IsNullOrWhiteSpace(file.Name))
                {
                    continue;
                }

                templates.Add(new PlanetAppearanceTemplate(
                    file.Name.Trim(),
                    file.Description?.Trim() ?? string.Empty,
                    file.CreatedUtc,
                    FilterValues(file.Values)));
            }
            catch (JsonException exception)
            {
                warnings.Add($"Skipped personal Planet template '{Path.GetFileName(path)}': {exception.Message}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Skipped inaccessible personal Planet template '{Path.GetFileName(path)}': {exception.Message}");
            }
        }

        _warnings = warnings;
        return templates.OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public PlanetAppearanceTemplate SaveNew(
        string name,
        string? description,
        PlanetAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        var cleanName = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A template name is required.", nameof(name))
            : name.Trim();
        if (LoadAll().Any(template => string.Equals(template.Name, cleanName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A Planet template named '{cleanName}' already exists.");
        }

        var values = PlanetAppearanceSchema.Columns
            .Where(column => !column.Equals("Shader", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(column => column, column => appearance[column], StringComparer.OrdinalIgnoreCase);
        var created = DateTimeOffset.UtcNow;
        var file = new TemplateFile(CurrentVersion, cleanName, description?.Trim() ?? string.Empty, created, values);
        var safeName = Regex.Replace(cleanName, "[^A-Za-z0-9._-]+", "-").Trim('-', '.');
        if (safeName.Length == 0)
        {
            safeName = "planet-template";
        }

        var target = Path.Combine(_folder, safeName + ".json");
        if (File.Exists(target))
        {
            target = Path.Combine(_folder, $"{safeName}-{Guid.NewGuid():N}.json");
        }
        AtomicFileWriter.Write(target, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(file, SerializerOptions)));
        return new PlanetAppearanceTemplate(cleanName, file.Description ?? string.Empty, created, values);
    }

    public bool Delete(PlanetAppearanceTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (!Directory.Exists(_folder))
        {
            return false;
        }

        foreach (var path in Directory.EnumerateFiles(_folder, "*.json"))
        {
            try
            {
                var file = JsonSerializer.Deserialize<TemplateFile>(File.ReadAllText(path), SerializerOptions);
                if (file is not null && string.Equals(file.Name, template.Name, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(path);
                    return true;
                }
            }
            catch (JsonException)
            {
            }
        }

        return false;
    }

    private static IReadOnlyDictionary<string, string> FilterValues(IReadOnlyDictionary<string, string>? values) =>
        (values ?? new Dictionary<string, string>())
        .Where(pair => PlanetAppearanceSchema.IsAppearanceColumn(pair.Key) &&
                       !pair.Key.Equals("Shader", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(pair => pair.Key, pair => pair.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    private sealed record TemplateFile(
        int Version,
        string Name,
        string? Description,
        DateTimeOffset CreatedUtc,
        IReadOnlyDictionary<string, string>? Values);
}
