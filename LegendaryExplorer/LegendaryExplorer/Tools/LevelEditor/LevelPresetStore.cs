using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using Newtonsoft.Json;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>A named, ordered collection of levels shared by scene editors and previews.</summary>
public sealed record LevelPreset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; }
    public MEGame Game { get; init; }
    public List<string> FilePaths { get; init; } = [];
    public List<string> ReadOnlyFilePaths { get; init; } = [];
    public List<LevelCameraPreset> CameraPresets { get; init; } = [];

    [JsonIgnore]
    public string Summary => $"{Game} · {FilePaths.Count} level{(FilePaths.Count == 1 ? "" : "s")}";

    [JsonIgnore]
    public string TooltipText => string.Join(Environment.NewLine, FilePaths);
}

public sealed class LevelPresetStore
{
    private const int CurrentVersion = 1;
    private readonly string storagePath;
    private static readonly Lazy<LevelPresetStore> SharedStore = new(() => new LevelPresetStore(
        Path.Combine(AppDirectories.AppDataFolder, "LevelEditor", "LEVELPRESETS.json")));

    public static LevelPresetStore Shared => SharedStore.Value;
    public ObservableCollectionExtended<LevelPreset> Presets { get; } = [];
    public string LoadError { get; private set; }

    public LevelPresetStore(string path)
    {
        storagePath = Path.GetFullPath(path);
        if (!File.Exists(storagePath)) return;
        try
        {
            var collection = JsonConvert.DeserializeObject<PresetCollection>(File.ReadAllText(storagePath));
            if (collection?.Version != CurrentVersion || collection.Presets == null)
                throw new InvalidDataException("The level preset library has an unsupported format.");
            var presets = collection.Presets.Select(Normalize).ToList();
            if (presets.Select(preset => preset.Id).Distinct().Count() != presets.Count
                || presets.Select(preset => preset.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != presets.Count)
                throw new InvalidDataException("The level preset library contains duplicate names or identifiers.");
            // Missing files remain in the library so users can repair a preset later.
            Presets.ReplaceAll(presets);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            LoadError = $"Unable to read level presets: {exception.Message}";
        }
    }

    public LevelPreset Save(LevelPreset preset)
    {
        EnsureWritable();
        LevelPreset normalized = Normalize(preset);
        if (Presets.Any(existing => existing.Id != normalized.Id
            && string.Equals(existing.Name, normalized.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A level preset named '{normalized.Name}' already exists.");

        var updated = Presets.ToList();
        int index = updated.FindIndex(existing => existing.Id == normalized.Id);
        if (index < 0) updated.Add(normalized);
        else updated[index] = normalized;
        Write(updated);
        Presets.ReplaceAll(updated);
        return normalized;
    }

    public void Delete(LevelPreset preset)
    {
        if (preset == null || !Presets.Any(existing => existing.Id == preset.Id)) return;
        EnsureWritable();
        var updated = Presets.Where(existing => existing.Id != preset.Id).ToList();
        Write(updated);
        Presets.ReplaceAll(updated);
    }

    public static bool MatchesSearch(LevelPreset preset, string search)
    {
        if (preset == null) return false;
        search = search?.Trim() ?? "";
        return preset.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || preset.Game.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
            || preset.FilePaths.Any(path => path.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    public static bool MatchesCameraSearch(LevelCameraPreset preset, string search)
    {
        if (preset == null) return false;
        search = search?.Trim() ?? "";
        return preset.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static LevelPreset Normalize(LevelPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (string.IsNullOrWhiteSpace(preset.Name))
            throw new ArgumentException("Enter a name for the level preset.");
        if (preset.FilePaths == null || preset.FilePaths.Count == 0)
            throw new ArgumentException("A level preset needs at least one level file.");
        if (preset.FilePaths.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Level preset paths cannot be empty.");
        var paths = preset.FilePaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var readOnly = (preset.ReadOnlyFilePaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cameras = (preset.CameraPresets ?? []).Select(NormalizeCamera).ToList();
        if (cameras.Select(camera => camera.Id).Distinct().Count() != cameras.Count
            || cameras.Select(camera => camera.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != cameras.Count)
            throw new ArgumentException("A level preset cannot contain camera presets with duplicate names or identifiers.");
        return preset with
        {
            Id = preset.Id == Guid.Empty ? Guid.NewGuid() : preset.Id,
            Name = preset.Name.Trim(),
            FilePaths = paths,
            ReadOnlyFilePaths = paths.Where(readOnly.Contains).ToList(),
            CameraPresets = cameras
        };
    }

    private static LevelCameraPreset NormalizeCamera(LevelCameraPreset camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (string.IsNullOrWhiteSpace(camera.Name))
            throw new ArgumentException("Enter a name for the camera preset.");
        if (!float.IsFinite(camera.X) || !float.IsFinite(camera.Y) || !float.IsFinite(camera.Z)
            || !float.IsFinite(camera.Roll) || !float.IsFinite(camera.Pitch) || !float.IsFinite(camera.Yaw))
            throw new ArgumentException("Camera preset coordinates and rotations must be finite numbers.");
        return camera with
        {
            Id = camera.Id == Guid.Empty ? Guid.NewGuid() : camera.Id,
            Name = camera.Name.Trim()
        };
    }

    private void EnsureWritable()
    {
        if (LoadError != null)
            throw new InvalidDataException($"{LoadError}\nThe existing library has been kept unchanged.");
    }

    private void Write(List<LevelPreset> presets)
    {
        string directory = Path.GetDirectoryName(storagePath);
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(storagePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(new PresetCollection
            {
                Version = CurrentVersion,
                Presets = presets
            }, Formatting.Indented));
            File.Move(temporaryPath, storagePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private sealed class PresetCollection
    {
        public int Version { get; set; }
        public List<LevelPreset> Presets { get; set; }
    }
}
