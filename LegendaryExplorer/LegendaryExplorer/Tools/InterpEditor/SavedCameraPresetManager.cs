using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegendaryExplorer.Misc;
using LegendaryExplorerCore.Misc;
using Newtonsoft.Json;

namespace LegendaryExplorer.Tools.InterpEditor;

public static class SavedCameraPresetManager
{
    private const int CurrentVersion = 1;
    private static string StoragePath => Path.Combine(AppDirectories.AppDataFolder, "CameraTrackMovePresets.json");

    public static ObservableCollectionExtended<CameraPreset> Presets { get; } = [];

    static SavedCameraPresetManager()
    {
        Presets.ReplaceAll(ReadCollection(StoragePath));
    }

    public static bool ContainsName(string name) =>
        Presets.Any(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));

    public static void Add(CameraPreset preset)
    {
        ValidatePreset(preset);
        if (ContainsName(preset.Name))
        {
            throw new InvalidOperationException($"A saved camera preset named '{preset.Name}' already exists.");
        }

        Presets.Add(preset);
        Save();
    }

    public static void Delete(CameraPreset preset)
    {
        if (preset is not null && Presets.Remove(preset))
        {
            Save();
        }
    }

    public static (int Added, int Replaced, int Skipped) Merge(IEnumerable<CameraPreset> importedPresets,
        bool replaceDuplicates)
    {
        int added = 0;
        int replaced = 0;
        int skipped = 0;
        foreach (CameraPreset importedPreset in importedPresets)
        {
            ValidatePreset(importedPreset);
            int existingIndex = -1;
            for (int i = 0; i < Presets.Count; i++)
            {
                if (string.Equals(Presets[i].Name, importedPreset.Name, StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = i;
                    break;
                }
            }
            if (existingIndex < 0)
            {
                Presets.Add(importedPreset);
                added++;
            }
            else if (replaceDuplicates)
            {
                Presets[existingIndex] = importedPreset;
                replaced++;
            }
            else
            {
                skipped++;
            }
        }

        Save();
        return (added, replaced, skipped);
    }

    public static IReadOnlyList<CameraPreset> ReadCollection(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var file = JsonConvert.DeserializeObject<SavedCameraPresetFile>(File.ReadAllText(path));
            if (file is null || file.Version != CurrentVersion)
            {
                return [];
            }

            return file.Presets.Where(IsValidPreset)
                .GroupBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void Export(string path) => WriteCollection(path, Presets);

    private static void Save() => WriteCollection(StoragePath, Presets);

    private static void WriteCollection(string path, IEnumerable<CameraPreset> presets)
    {
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var file = new SavedCameraPresetFile
        {
            Version = CurrentVersion,
            Presets = presets.ToList()
        };
        File.WriteAllText(path, JsonConvert.SerializeObject(file, Formatting.Indented));
    }

    private static void ValidatePreset(CameraPreset preset)
    {
        if (!IsValidPreset(preset))
        {
            throw new InvalidDataException("Saved camera presets require a name and at least one valid local TrackMove key.");
        }
    }

    private static bool IsValidPreset(CameraPreset preset) =>
        preset is { IsSavedTrackMove: true }
        && !string.IsNullOrWhiteSpace(preset.Name)
        && preset.LocalKeys.All(key => float.IsFinite(key.TimeOffset)
            && IsFinite(key.LocalPosition) && IsFinite(key.LocalRotation));

    private static bool IsFinite(System.Numerics.Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private sealed class SavedCameraPresetFile
    {
        public int Version { get; set; }
        public List<CameraPreset> Presets { get; set; } = [];
    }
}
