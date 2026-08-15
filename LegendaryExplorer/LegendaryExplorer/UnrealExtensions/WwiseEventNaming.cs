using System;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.UnrealExtensions
{
    /// <summary>
    /// Centralizes the different Wwise event naming conventions used by LE2 and LE3.
    /// </summary>
    public static class WwiseEventNaming
    {
        public static string GetPlayEventName(MEGame game, string soundName)
        {
            if (game != MEGame.LE2)
            {
                return $"{soundName}_Play";
            }

            // LE2 dialogue events use the same VO_<id>_<gender>_Play layout expected by
            // FaceFX. Accept event-style WAV names as input without duplicating Play.
            return $"{NormalizeLe2EventBaseName(soundName)}_Play";
        }

        public static string GetPerAudioStopEventName(MEGame game, string soundName) => game == MEGame.LE2
            ? $"Stop_{NormalizeLe2EventBaseName(soundName)}"
            : $"{soundName}_Stop";

        private static string NormalizeLe2EventBaseName(string soundName)
        {
            if (soundName.StartsWith("Play_", StringComparison.OrdinalIgnoreCase))
            {
                soundName = soundName[5..];
            }
            if (soundName.EndsWith("_Play", StringComparison.OrdinalIgnoreCase))
            {
                soundName = soundName[..^5];
            }

            return soundName;
        }

        public static bool IsPlayEventForGender(string eventName, bool isFemale, MEGame game)
        {
            if (string.IsNullOrWhiteSpace(eventName))
            {
                return false;
            }

            string gender = isFemale ? "f" : "m";
            return eventName.EndsWith($"_{gender}_Play", StringComparison.OrdinalIgnoreCase);
        }
    }
}
