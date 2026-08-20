using System.Collections.Generic;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator
{
    public static class FaceFXSpeciesCatalog
    {
        private static readonly IReadOnlyList<FaceFXSpecies> LegacySpecies = new[]
        {
            FaceFXSpecies.HumanFemale,
            FaceFXSpecies.HumanMale,
            FaceFXSpecies.Asari,
            FaceFXSpecies.Krogan,
            FaceFXSpecies.Batarian,
            FaceFXSpecies.Drell,
            FaceFXSpecies.Turian,
            FaceFXSpecies.TurianFemale,
            FaceFXSpecies.Salarian,
            FaceFXSpecies.Quarian,
            FaceFXSpecies.Geth,
            FaceFXSpecies.Elcor,
            FaceFXSpecies.Hanar,
            FaceFXSpecies.Volus,
            FaceFXSpecies.Vorcha,
            FaceFXSpecies.Yahg,
            FaceFXSpecies.EDI
        };

        private static readonly IReadOnlyList<FaceFXSpecies> Le3Species = new[]
        {
            FaceFXSpecies.HumanFemale,
            FaceFXSpecies.HumanMale,
            FaceFXSpecies.HumanChild,
            FaceFXSpecies.Asari,
            FaceFXSpecies.Krogan,
            FaceFXSpecies.Drell,
            FaceFXSpecies.Turian,
            FaceFXSpecies.TurianFemale,
            FaceFXSpecies.Salarian,
            FaceFXSpecies.Quarian,
            FaceFXSpecies.Geth,
            FaceFXSpecies.Elcor,
            FaceFXSpecies.Hanar,
            FaceFXSpecies.Volus,
            FaceFXSpecies.Batarian,
            FaceFXSpecies.Vorcha,
            FaceFXSpecies.Prothean,
            FaceFXSpecies.Yahg,
            FaceFXSpecies.AlienB,
            FaceFXSpecies.EDI,
            FaceFXSpecies.Shepard
        };

        public static IReadOnlyList<FaceFXSpecies> GetForGame(MEGame game) =>
            IsLegacyLegendaryGame(game) ? LegacySpecies : Le3Species;

        public static bool IsLegacyLegendaryGame(MEGame game) => game is MEGame.LE1 or MEGame.LE2;

        public static string GetDisplayName(FaceFXSpecies species) => species switch
        {
            FaceFXSpecies.HumanFemale => "Human Female",
            FaceFXSpecies.HumanMale => "Human Male",
            FaceFXSpecies.HumanChild => "Human Child",
            FaceFXSpecies.TurianFemale => "Turian Female",
            FaceFXSpecies.AlienB => "Alien B",
            FaceFXSpecies.EDI => "EDI (Non-Humanoid)",
            _ => species.ToString()
        };

        public static FaceFXSpecies FromDisplayName(string displayName) => displayName switch
        {
            "Human Male" => FaceFXSpecies.HumanMale,
            "Human Child" => FaceFXSpecies.HumanChild,
            "Asari" => FaceFXSpecies.Asari,
            "Krogan" => FaceFXSpecies.Krogan,
            "Drell" => FaceFXSpecies.Drell,
            "Turian" => FaceFXSpecies.Turian,
            "Turian Female" => FaceFXSpecies.TurianFemale,
            "Salarian" => FaceFXSpecies.Salarian,
            "Quarian" => FaceFXSpecies.Quarian,
            "Geth" => FaceFXSpecies.Geth,
            "Elcor" => FaceFXSpecies.Elcor,
            "Hanar" => FaceFXSpecies.Hanar,
            "Volus" => FaceFXSpecies.Volus,
            "Batarian" => FaceFXSpecies.Batarian,
            "Vorcha" => FaceFXSpecies.Vorcha,
            "Prothean" => FaceFXSpecies.Prothean,
            "Yahg" => FaceFXSpecies.Yahg,
            "Alien B" => FaceFXSpecies.AlienB,
            "EDI" or "EDI (Non-Humanoid)" => FaceFXSpecies.EDI,
            "Shepard" => FaceFXSpecies.Shepard,
            _ => FaceFXSpecies.HumanFemale
        };
    }
}
