using System;
using System.Collections.Generic;
using System.Linq;

namespace LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator
{
    public sealed class FaceFXEmotionChoice
    {
        public string DisplayName { get; init; }
        public string LayeredFamily { get; init; }
        public string PresetAnimation { get; init; }
        public bool IsNone => string.IsNullOrEmpty(LayeredFamily) && string.IsNullOrEmpty(PresetAnimation);
        public bool IsLayered => !string.IsNullOrEmpty(LayeredFamily);
        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// Emotions found by auditing every FaceFXAsset in LE3's BIOG_FaceFX_Assets.
    /// The exact misspellings/capitalization used by individual shipped rigs are
    /// preserved when a preset is resolved.
    /// </summary>
    public static class FaceFXEmotionCatalog
    {
        public static readonly IReadOnlyList<string> LayeredFamilies = new[]
        {
            "Amusement", "Anger", "Anxiety", "Aversion", "Concern", "Dejection",
            "Disdain", "Disgust", "Fear", "Grief", "Indignation", "Joy", "Laughter",
            "Melancholy", "Rage", "Revulsion", "Sadness", "Satisfaction", "Stern", "Terror"
        };

        private static readonly IReadOnlyList<(string DisplayName, string Animation)> CorePresets = new[]
        {
            ("Angry: Interested", "E_Angry_Interested"),
            ("Angry: Question", "E_Angry_Question"),
            ("Angry: Rage", "E_Angry_Rage"),
            ("Angry: Shocked", "E_Angry_Shocked"),
            ("Angry: Squint", "E_Angry_Squint"),
            ("Happy: Diabolical", "E_Happy_Diabolical"),
            ("Happy: Disappointed", "E_Happy_Dissapointed"),
            ("Happy: Fake", "E_Happy_Fake"),
            ("Happy: Interested", "E_Happy_Interested"),
            ("Happy: Overjoyed", "E_Happy_OverJoyed"),
            ("Happy: Question", "E_Happy_Question"),
            ("Neutral: Perplexed", "E_Neutral_Perplexed"),
            ("Neutral: Question", "E_Neutral_Question"),
            ("Neutral: Shock", "E_Neutral_Shock"),
            ("Neutral: Squint", "E_Neutral_Squint"),
            ("Neutral: Thoughtful", "E_Neutral_Thoughtful"),
            ("Sad: Disappointed", "E_Sad_Dissapointed"),
            ("Sad: Perplexed", "E_Sad_Perplexed"),
            ("Sad: Question", "E_Sad_Question"),
            ("Sad: Shocked", "E_Sad_Shocked"),
            ("Sad: Squint", "E_Sad_Squint")
        };

        private static readonly IReadOnlyList<(string DisplayName, string Animation)> FlirtPresets = new[]
        {
            ("Flirt: Fake", "E_Flirt_Fake"),
            ("Flirt: Interested", "E_Flirt_Interested"),
            ("Flirt: Overjoyed", "E_Flirt_OverJoyed"),
            ("Flirt: Question", "E_Flirt_Question")
        };

        private static readonly IReadOnlyList<(string DisplayName, string Animation)> WoundedPresets = new[]
        {
            ("Wounded: Neutral", "E_Wounded_Neutral"),
            ("Wounded: Pain", "E_Wounded_Pain"),
            ("Wounded: Question", "E_Wounded_Question"),
            ("Wounded: Squint", "E_Wounded_Squint")
        };

        public static IReadOnlyList<FaceFXEmotionChoice> GetForSpecies(FaceFXSpecies species)
        {
            var result = new List<FaceFXEmotionChoice>
            {
                new() { DisplayName = "None" }
            };

            if (SupportsLayeredEmotions(species))
            {
                result.AddRange(LayeredFamilies.Select(family => new FaceFXEmotionChoice
                {
                    DisplayName = $"Layered: {family}",
                    LayeredFamily = family
                }));
            }

            AddPresets(result, CorePresets, species);

            if (species is not (FaceFXSpecies.AlienB or FaceFXSpecies.Elcor or FaceFXSpecies.Hanar
                or FaceFXSpecies.Quarian or FaceFXSpecies.Volus))
            {
                AddPresets(result, FlirtPresets, species);
            }

            if (species is FaceFXSpecies.Asari or FaceFXSpecies.Drell or FaceFXSpecies.EDI
                or FaceFXSpecies.HumanChild or FaceFXSpecies.HumanFemale or FaceFXSpecies.HumanMale
                or FaceFXSpecies.Krogan or FaceFXSpecies.Prothean or FaceFXSpecies.Salarian
                or FaceFXSpecies.Shepard or FaceFXSpecies.Turian or FaceFXSpecies.Yahg)
            {
                AddPresets(result, WoundedPresets, species);
            }

            if (species is FaceFXSpecies.AlienB or FaceFXSpecies.Drell or FaceFXSpecies.Salarian)
                AddPreset(result, "Wounded: Grimace", "E_Wounded_Grimace", species);
            if (species == FaceFXSpecies.AlienB)
                AddPreset(result, "Angry: Outrage", "E_Angry_Outrage", species);
            if (species == FaceFXSpecies.Drell)
                AddPreset(result, "Happy: Broad Smile", "E_Happy_BroadSmile", species);

            return result;
        }

        public static bool SupportsLayeredEmotions(FaceFXSpecies species) =>
            species is FaceFXSpecies.Asari or FaceFXSpecies.HumanFemale or FaceFXSpecies.HumanMale
                or FaceFXSpecies.Shepard;

        private static void AddPresets(List<FaceFXEmotionChoice> destination,
            IEnumerable<(string DisplayName, string Animation)> presets, FaceFXSpecies species)
        {
            foreach ((string displayName, string animation) in presets)
                AddPreset(destination, displayName, animation, species);
        }

        private static void AddPreset(List<FaceFXEmotionChoice> destination, string displayName,
            string animation, FaceFXSpecies species)
        {
            destination.Add(new FaceFXEmotionChoice
            {
                DisplayName = $"Preset: {displayName}",
                PresetAnimation = ResolveRigSpelling(animation, species)
            });
        }

        private static string ResolveRigSpelling(string animation, FaceFXSpecies species)
        {
            if (animation == "E_Neutral_Thoughtful" && species is not (FaceFXSpecies.Asari
                or FaceFXSpecies.HumanChild or FaceFXSpecies.HumanFemale or FaceFXSpecies.HumanMale
                or FaceFXSpecies.Prothean or FaceFXSpecies.Shepard))
            {
                return "E_Neutral_Thoughtfull";
            }

            if (species == FaceFXSpecies.Yahg)
                return animation.Replace("OverJoyed", "Overjoyed", StringComparison.Ordinal);

            return animation;
        }
    }
}
