using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LegendaryExplorer.Misc.AppSettings;

/// <summary>
/// Scottina's saved preferences, captured on October 7, 2026. The embedded snapshot
/// includes tool preferences and favorites, but excludes credentials and session history.
/// </summary>
public static class ScottinaPreset
{
    private const string ResourceName = "LegendaryExplorer.Misc.AppSettings.ScottinaPreset.json";

    /// <summary>Raised after the preset is saved so open tools can reload their local preferences.</summary>
    public static event EventHandler Applied;

    public static void Apply()
    {
        using var stream = typeof(ScottinaPreset).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Scottina's preset is missing from this build.");
        using var reader = new StreamReader(stream);
        var preset = JObject.Parse(reader.ReadToEnd());
        var settings = (JObject)preset["Settings"];
        var files = PrepareToolSettings((JObject)preset["ToolSettings"]);

        // Use the normal setters so bindings, themes and the core settings bridge update.
        // Defer autosaves until all preferences have been applied.
        Settings.ApplyPresetChanges(() =>
        {
            ApplySettings(settings);
            foreach (var (path, contents) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, contents);
            }
        });
        Applied?.Invoke(null, EventArgs.Empty);
    }

    private static void ApplySettings(JObject settings)
    {
        var properties = typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.CanWrite && property.Name != nameof(Settings.Global_DarkMode_Enabled))
            .ToDictionary(property => property.Name.ToLowerInvariant());
        var changes = new List<(PropertyInfo Property, object Value)>();
        // Validate and convert the entire snapshot before changing any setting. Missing
        // snapshot keys leave newer preferences alone, rather than resetting them.
        foreach (var setting in settings.Properties())
        {
            if (!properties.TryGetValue(setting.Name, out var property))
            {
                throw new InvalidOperationException($"Unknown setting in Scottina's preset: {setting.Name}");
            }
            var value = setting.Value.ToObject(property.PropertyType);
            if (value == null)
            {
                throw new InvalidOperationException($"Missing value in Scottina's preset: {setting.Name}");
            }
            changes.Add((property, value));
        }
        // Theme listeners initialize graph colors. Apply the theme first so captured
        // custom colors win even when changing themes with graph editors open.
        foreach (var (property, value) in changes.OrderBy(change => change.Property.Name != nameof(Settings.Global_Theme)))
        {
            property.SetValue(null, value);
        }
    }

    private static List<(string Path, string Contents)> PrepareToolSettings(JObject toolSettings)
    {
        var files = new List<(string, string)>();
        foreach (var file in toolSettings.Properties())
        {
            // These are preference files, not a restore of the entire AppData directory.
            // JSON snapshots contain only preference fields to preserve other user data.
            bool json = file.Name is "DialogueEditor/DialogueEditorOptions.JSON"
                or "CoalescedEditorState.json" or "FaceFXEditor/ElevenLabs.json";
            bool text = file.Name is "CameraPresetDistanceScale.txt" or "CameraPresetSelection.txt"
                or "PackageEditor/PINNEDFILES";
            bool array = file.Name == "SequenceEditor/CustomSequenceObjectSources.json";
            if (!json && !text && !array)
            {
                throw new InvalidOperationException($"Unknown tool preference file in Scottina's preset: {file.Name}");
            }
            var path = Path.Combine(AppDirectories.AppDataFolder, file.Name.Replace('/', Path.DirectorySeparatorChar));
            string contents;
            if (json)
            {
                var existingJson = File.Exists(path) ? File.ReadAllText(path) : null;
                if (file.Name == "CoalescedEditorState.json" && !string.IsNullOrWhiteSpace(existingJson)
                    && JToken.Parse(existingJson) is JArray legacyPaths)
                {
                    // Older Coalesced profiles stored just the open paths. Preserve
                    // them when upgrading the state document to add the preference.
                    existingJson = new JObject
                    {
                        ["OpenFiles"] = new JArray(legacyPaths.Values<string>()
                            .Select(legacyPath => new JObject { ["FilePath"] = legacyPath }))
                    }.ToString();
                }
                contents = MergeToolSettings(existingJson, (JObject)file.Value);
            }
            else
            {
                contents = array ? ((JArray)file.Value).ToString(Formatting.Indented) : file.Value.Value<string>();
            }
            files.Add((path, contents));
        }
        return files;
    }

    private static string MergeToolSettings(string existingJson, JObject preset)
    {
        var merged = string.IsNullOrWhiteSpace(existingJson) ? new JObject() : JObject.Parse(existingJson);
        foreach (var preference in preset.Properties())
        {
            merged[preference.Name] = preference.Value.DeepClone();
        }
        return merged.ToString(Formatting.Indented);
    }
}
