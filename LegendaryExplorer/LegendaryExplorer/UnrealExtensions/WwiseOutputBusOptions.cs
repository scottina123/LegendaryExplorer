using System;
using System.Collections.Generic;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.UnrealExtensions;

/// <summary>
/// Game-native Wwise output bus names used by the audio import and editing tools.
/// Wwise stores these references as name-derived ShortIDs rather than strings.
/// </summary>
internal static class WwiseOutputBusOptions
{
    internal const string MasterAudioBus = "Master Audio Bus";
    internal const string Le3ConversationBus = "Env-VO-Conversation";
    internal const string Le2ConversationBus = "Conversation";

    private static readonly string[] Le3OutputBuses =
    [
        MasterAudioBus,
        Le3ConversationBus,
        "Env-VO-Ambient-Duck",
        "Env-VO-Ambient-NonDuck",
        "Env-VO-Ambient-Critical",
        "Env-VO-SoundSet-Duck",
        "Env-VO-SoundSet-NonDuck",
        "Env-VO-Exertions",
        "Env-Music",
        "Env-Snd-0-CineDesign",
        "Env-Snd-0-CineAnim",
        "Env-Snd-0-CineD-SkipKill",
        "Env-Snd-0-CineD-SkipNoKill",
        "Env-Snd-0-LevelEvents",
        "Env-Snd-0-LevelTransitions",
        "Env-Snd-0-ProceduralFoley",
        "Env-Snd-1-Amb-Stream",
        "Env-Snd-1-Amb-NonStream",
        "Env-Snd-1-Creatures",
        "Env-Snd-1-Foley",
        "Env-Snd-1-Footsteps",
        "Env-Snd-1-Physics",
        "Env-Snd-1-Placeables",
        "Env-Snd-1-Powers",
        "Env-Snd-1-Vehicles",
        "Env-Snd-1-VFX",
        "Env-Snd-1-Weapons",
        "Env-Snd-1-Bullets",
        "Env-Snd-2-PlayerWeapons",
        "Env-Snd-2-PlayerPowers",
        "Env-Snd-3-CreatureCritical",
        "Env-Snd-4-Explosions",
        "Env-Snd-5-Critical",
        "NonEnv-Snd-0-CineAnim",
        "NonEnv-Snd-0-CineDes",
        "NonEnv-Snd-0-LevelEvents",
        "NonEnv-VO-Radio-Convo",
        "NonEnv-VO-Radio-Critical",
        "NonSlowdown-GUI Sounds",
        "NonSlowdown-Music",
        "NonSlowdown-Dialog",
    ];

    private static readonly string[] Le2OutputBuses =
    [
        MasterAudioBus,
        "Game Speed Affected",
        "Capture Buss",
        "Enviromental",
        "Migrated",
        "UnDucked Bus",
        "Ducked Bus",
        "Dialog",
        "Ambient - Does Duck Ambiences",
        Le2ConversationBus,
        "SoundSet",
        "Ambient - Doesn't Duck Ambiences",
        "Ambient-Ducked By Conversation VO",
        "Conversation - Critical",
        "Music-Diegetic",
        "Sound Effects",
        "Foley",
        "Ambiences - Streaming",
        "Physics",
        "Particle Emitters",
        "Gunshots",
        "Bullet Impacts",
        "Ambiences - NonStreaming",
        "Creatures",
        "Cine Design",
        "Skipping Killed",
        "Skipping Not Killed",
        "Cine Anim",
        "Vehicles",
        "Powers",
        "Placeables",
        "Non-Environmental",
        "UnDucked Bus_01",
        "UnDucked Music",
        "UnDucked Sound Effects",
        "UnDucked LFE",
        "GUI Sounds",
        "Ducked Bus_01",
        "Sound Effects_01",
        "Ambiences - Streaming_01",
        "Ambiences - NonStreaming_01",
        "Cine Anim_01",
        "Ducked LFE",
        "Cine Design_01",
        "Skipping Killed_01",
        "Skipping Not Killed_01",
        "Cine Anim_01_NoAffectedByStopCineDesign",
        "Dialog_01",
        "Music",
        "Not Game Speed Affected",
        "Sound Effects_02",
        "GUI Sounds_01",
        "Music_01",
        "Dialog_02",
        "Combat Ducking Control Bus",
    ];

    internal static IReadOnlyList<string> GetOutputBuses(MEGame game) => game == MEGame.LE2
        ? Le2OutputBuses
        : Le3OutputBuses;

    internal static string GetDefaultOutputBus(MEGame game) => game == MEGame.LE2
        ? Le2ConversationBus
        : Le3ConversationBus;

    internal static uint GetOutputBusId(string outputBus) =>
        string.Equals(outputBus, MasterAudioBus, StringComparison.Ordinal) ? 0 : GenerateShortId(outputBus);

    internal static string GetOutputBusName(MEGame game, uint outputBusId)
    {
        if (outputBusId == 0)
        {
            return MasterAudioBus;
        }

        foreach (string outputBus in GetOutputBuses(game))
        {
            if (GetOutputBusId(outputBus) == outputBusId)
            {
                return outputBus;
            }
        }

        return null;
    }

    internal static bool DefaultsToHelmetEffect(MEGame game, string outputBus) =>
        (game == MEGame.LE3 && string.Equals(outputBus, Le3ConversationBus, StringComparison.Ordinal)) ||
        (game == MEGame.LE2 && string.Equals(outputBus, Le2ConversationBus, StringComparison.Ordinal));

    internal static bool SupportsMusicDucking(MEGame game, string outputBus) =>
        game is MEGame.LE2 or MEGame.LE3 && !string.IsNullOrWhiteSpace(outputBus) &&
        (outputBus.Contains("Music", StringComparison.OrdinalIgnoreCase) ||
         outputBus.StartsWith("Mus-", StringComparison.OrdinalIgnoreCase));

    internal static uint GenerateShortId(string name)
    {
        uint hash = 2166136261;
        foreach (char character in name.ToLowerInvariant())
        {
            hash *= 16777619;
            hash ^= character;
        }

        return hash;
    }
}
