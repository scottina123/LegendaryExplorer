using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LegendaryExplorer.Misc;

namespace LegendaryExplorer.Tools.FaceFXEditor.ElevenLabs
{
    internal sealed class ElevenLabsPreferences
    {
        public bool RememberApiKey { get; set; }
        public string EncryptedApiKey { get; set; }
        public string VoiceId { get; set; }
        public string ModelId { get; set; }
        public string LanguageCode { get; set; }
        public double Stability { get; set; } = 0.5d;
        public double SimilarityBoost { get; set; } = 0.75d;
        public double Style { get; set; }
        public bool UseSpeakerBoost { get; set; } = true;
        public double Speed { get; set; } = 1d;
        public string ApplyTextNormalization { get; set; } = "auto";
        public bool ApplyLanguageTextNormalization { get; set; }
        public bool UseAdjacentTextContext { get; set; } = true;
        public bool EnableLogging { get; set; } = true;
        public int OptimizeStreamingLatency { get; set; }
        public string Seed { get; set; }
        public bool MirrorOppositeGender { get; set; }
    }

    internal static class ElevenLabsPreferencesStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LegendaryExplorer.FaceFX.ElevenLabs.v1");
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private static string SettingsDirectory => Path.Combine(AppDirectories.AppDataFolder, "FaceFXEditor");
        private static string SettingsPath => Path.Combine(SettingsDirectory, "ElevenLabs.json");

        public static ElevenLabsPreferences Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    return JsonSerializer.Deserialize<ElevenLabsPreferences>(File.ReadAllText(SettingsPath), JsonOptions)
                           ?? new ElevenLabsPreferences();
                }
            }
            catch
            {
                // Corrupt or inaccessible preferences should not prevent the editor from opening.
            }

            return new ElevenLabsPreferences();
        }

        public static string TryDecryptApiKey(ElevenLabsPreferences preferences)
        {
            if (preferences?.RememberApiKey != true || string.IsNullOrWhiteSpace(preferences.EncryptedApiKey))
            {
                return null;
            }

            try
            {
                byte[] encrypted = Convert.FromBase64String(preferences.EncryptedApiKey);
                byte[] clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(clear);
            }
            catch
            {
                return null;
            }
        }

        public static void Save(ElevenLabsPreferences preferences, string apiKey)
        {
            ArgumentNullException.ThrowIfNull(preferences);
            if (preferences.RememberApiKey && !string.IsNullOrWhiteSpace(apiKey))
            {
                byte[] clear = Encoding.UTF8.GetBytes(apiKey.Trim());
                byte[] encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
                preferences.EncryptedApiKey = Convert.ToBase64String(encrypted);
                CryptographicOperations.ZeroMemory(clear);
            }
            else
            {
                preferences.EncryptedApiKey = null;
            }

            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(preferences, JsonOptions));
        }
    }
}
