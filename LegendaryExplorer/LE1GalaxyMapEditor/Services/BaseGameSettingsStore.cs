using System.IO;
using System.Text;
using System.Text.Json;
using LE1GalaxyMapEditor.Models;
using LegendaryExplorerCore.Packages;

namespace LE1GalaxyMapEditor.Services;

/// <summary>Persists presentation-only settings for the built-in read-only layer.</summary>
public sealed class BaseGameSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public BaseGameSettingsStore(string? settingsPath = null)
    {
        SettingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LE1GalaxyMapEditor",
                "basegame.json")
            : Path.GetFullPath(settingsPath);
    }

    public string SettingsPath { get; }

    public MELocalization LoadLocale()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return MELocalization.INT;
            }

            var settings = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(SettingsPath), Options);
            return settings is not null && GalaxyMapModule.SupportedTlkLocales.Contains(settings.TlkLocale)
                ? settings.TlkLocale
                : MELocalization.INT;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return MELocalization.INT;
        }
    }

    public void SaveLocale(MELocalization locale)
    {
        if (!GalaxyMapModule.SupportedTlkLocales.Contains(locale))
        {
            throw new ArgumentOutOfRangeException(nameof(locale), "The BASEGAME TLK locale is not supported.");
        }

        var json = JsonSerializer.Serialize(new SettingsDto { TlkLocale = locale }, Options) + "\r\n";
        AtomicFileWriter.Write(SettingsPath, new UTF8Encoding(false).GetBytes(json));
    }

    private sealed class SettingsDto
    {
        public MELocalization TlkLocale { get; set; } = MELocalization.INT;
    }
}
