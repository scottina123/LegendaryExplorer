using System.Collections.Generic;

namespace LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator
{
    /// <summary>
    /// Supported species for FaceFX generation
    /// </summary>
    public enum FaceFXSpecies
    {
        HumanFemale,
        HumanMale,
        HumanChild,
        Asari,
        Krogan,
        Drell,
        Turian,
        Salarian,
        Quarian,
        Geth,
        Elcor,
        Hanar,
        Volus,
        Batarian,
        Vorcha,
        Prothean,
        Yahg
    }

    /// <summary>
    /// Maps phonemes to visemes using UDK FaceFX reference data.
    /// Each phoneme maps to multiple visemes with specific weights.
    /// </summary>
    public static class PhonemeToVisemeMap
    {
        /// <summary>
        /// Gets the phoneme map for the specified species.
        /// Note: Some species (Elcor, Hanar, Volus, Batarian, Vorcha, Yahg) have
        /// FaceFX data that uses bone names as phoneme identifiers instead of standard phonemes.
        /// For these species, we use Drell phoneme map which has standard phonemes with similar viseme names.
        /// Prothean uses m_* style visemes like Human Male, so it uses HumanMalePhonemeMap.
        /// </summary>
        public static Dictionary<string, VisemeMapping[]> GetPhonemeMap(FaceFXSpecies species)
        {
            return species switch
            {
                FaceFXSpecies.HumanMale => HumanMalePhonemeMap,
                FaceFXSpecies.HumanChild => HumanChildPhonemeMap,
                FaceFXSpecies.Asari => AsariPhonemeMap,
                FaceFXSpecies.Krogan => KroganPhonemeMap,
                FaceFXSpecies.Drell => DrellPhonemeMap,
                FaceFXSpecies.Turian => TurianPhonemeMap,
                FaceFXSpecies.Salarian => SalarianPhonemeMap,
                FaceFXSpecies.Quarian => QuarianPhonemeMap,
                FaceFXSpecies.Geth => GethPhonemeMap,
                // These species have bone-based phoneme maps that don't match standard phonemes.
                // Use Drell as fallback since it has standard phonemes with similar viseme names (jawOpen, smileRight, etc.)
                FaceFXSpecies.Elcor => DrellPhonemeMap,
                FaceFXSpecies.Hanar => DrellPhonemeMap,
                FaceFXSpecies.Volus => DrellPhonemeMap,
                FaceFXSpecies.Batarian => DrellPhonemeMap,
                FaceFXSpecies.Vorcha => DrellPhonemeMap,
                // Prothean uses m_* style visemes like Human Male (m_Open, m_Jaw+, m_OH, m_EE, etc.)
                FaceFXSpecies.Prothean => HumanMalePhonemeMap,
                // Yahg has unique visemes - use Drell as closest approximation
                FaceFXSpecies.Yahg => DrellPhonemeMap,
                _ => HumanFemalePhonemeMap
            };
        }

        /// <summary>
        /// Gets the viseme animation names for the specified species
        /// </summary>
        public static string[] GetVisemes(FaceFXSpecies species)
        {
            return species switch
            {
                FaceFXSpecies.HumanMale => HumanMaleVisemes,
                FaceFXSpecies.HumanChild => HumanChildVisemes,
                FaceFXSpecies.Asari => AsariVisemes,
                FaceFXSpecies.Krogan => KroganVisemes,
                FaceFXSpecies.Drell => DrellVisemes,
                FaceFXSpecies.Turian => TurianVisemes,
                FaceFXSpecies.Salarian => SalarianVisemes,
                FaceFXSpecies.Quarian => QuarianVisemes,
                FaceFXSpecies.Geth => GethVisemes,
                FaceFXSpecies.Elcor => ElcorVisemes,
                FaceFXSpecies.Hanar => HanarVisemes,
                FaceFXSpecies.Volus => VolusVisemes,
                FaceFXSpecies.Batarian => BatarianVisemes,
                FaceFXSpecies.Vorcha => VorchaVisemes,
                FaceFXSpecies.Prothean => ProtheanVisemes,
                FaceFXSpecies.Yahg => YahgVisemes,
                _ => HumanFemaleVisemes
            };
        }

        /// <summary>
        /// Human Female phoneme to viseme mappings - EXACT values from Unreal FaceFX.
        /// Each phoneme can trigger multiple visemes with specific weights.
        /// </summary>
        public static readonly Dictionary<string, VisemeMapping[]> HumanFemalePhonemeMap = new()
        {
            // Silence
            { "SIL", new[] { new VisemeMapping("m_Jaw+", 0.107f), new VisemeMapping("m_Open", 0.162f) } },
            
            // Bilabial stops
            { "P", new[] { new VisemeMapping("m_M", 0.730f), new VisemeMapping("m_Jaw-", 0.112f) } },
            { "B", new[] { new VisemeMapping("m_M", 0.765f), new VisemeMapping("m_Jaw-", 0.760f) } },
            { "M", new[] { new VisemeMapping("m_M", 1.000f), new VisemeMapping("m_Jaw-", 0.186f) } },
            
            // Alveolar stops
            { "T", new[] { new VisemeMapping("m_EE", 0.407f), new VisemeMapping("m_N", 1.000f), new VisemeMapping("m_G", 0.128f) } },
            { "D", new[] { new VisemeMapping("m_Jaw-", 0.055f), new VisemeMapping("m_N", 1.000f) } },
            
            // Velar stops
            { "K", new[] { new VisemeMapping("m_Jaw+", 0.250f), new VisemeMapping("m_G", 0.364f) } },
            { "G", new[] { new VisemeMapping("m_Jaw+", 0.090f), new VisemeMapping("m_G", 0.657f) } },
            
            // Nasals
            { "N", new[] { new VisemeMapping("m_EE", 0.496f), new VisemeMapping("m_OW", 0.496f), new VisemeMapping("m_Jaw-", 0.110f), new VisemeMapping("m_N", 1.000f) } },
            { "NG", new[] { new VisemeMapping("m_EE", 0.499f), new VisemeMapping("m_Jaw+", 0.123f), new VisemeMapping("m_M", 0.509f), new VisemeMapping("m_N", 1.000f) } },
            
            // Fricatives
            { "F", new[] { new VisemeMapping("m_Jaw+", 0.129f), new VisemeMapping("m_FV", 0.765f) } },
            { "V", new[] { new VisemeMapping("m_Jaw+", 0.151f), new VisemeMapping("m_FV", 1.000f) } },
            { "TH", new[] { new VisemeMapping("m_Jaw+", 0.157f), new VisemeMapping("m_TH", 0.724f) } },
            { "DH", new[] { new VisemeMapping("m_Jaw+", 0.216f), new VisemeMapping("m_TH", 0.932f) } },
            { "S", new[] { new VisemeMapping("m_EE", 0.262f), new VisemeMapping("m_Jaw+", 0.090f), new VisemeMapping("m_OW", 0.614f), new VisemeMapping("m_M", 0.472f) } },
            { "Z", new[] { new VisemeMapping("m_EE", 0.267f), new VisemeMapping("m_Jaw+", 0.018f), new VisemeMapping("m_OW", 0.572f), new VisemeMapping("m_M", 0.648f) } },
            { "SH", new[] { new VisemeMapping("m_Jaw+", 0.090f), new VisemeMapping("m_OW", 0.856f) } },
            { "ZH", new[] { new VisemeMapping("m_Jaw+", 0.090f), new VisemeMapping("m_OW", 0.533f) } },
            { "H", new[] { new VisemeMapping("m_EE", 0.353f), new VisemeMapping("m_Jaw+", 0.164f) } },
            
            // Approximants
            { "R", new[] { new VisemeMapping("m_Jaw+", 0.100f), new VisemeMapping("m_OH", 0.570f) } },
            { "L", new[] { new VisemeMapping("m_Jaw+", 0.151f), new VisemeMapping("m_L", 1.000f) } },
            { "W", new[] { new VisemeMapping("m_Jaw+", 0.136f), new VisemeMapping("m_OH", 0.709f) } },
            { "Y", new[] { new VisemeMapping("m_EE", 0.223f), new VisemeMapping("m_Jaw+", 0.149f) } },
            
            // Affricates
            { "CH", new[] { new VisemeMapping("m_Jaw+", 0.088f), new VisemeMapping("m_Open", 1.000f) } },
            { "JH", new[] { new VisemeMapping("m_Jaw+", 0.125f), new VisemeMapping("m_OH", 0.459f) } },
            
            // Flap
            { "FLAP", new[] { new VisemeMapping("m_Flap", 1.000f), new VisemeMapping("m_Jaw+", 0.220f) } },
            
            // Special
            { "TS", new[] { new VisemeMapping("m_Jaw-", 0.066f), new VisemeMapping("m_ZZ", 1.000f) } },
            
            // Front vowels
            { "IY", new[] { new VisemeMapping("m_EE", 0.318f), new VisemeMapping("m_Jaw+", 0.196f), new VisemeMapping("m_ZZ", 0.823f) } },
            { "IH", new[] { new VisemeMapping("m_EE", 0.251f), new VisemeMapping("m_Jaw+", 0.253f), new VisemeMapping("m_OW", 0.336f) } },
            { "EH", new[] { new VisemeMapping("m_Jaw+", 0.248f), new VisemeMapping("m_EH", 0.436f) } },
            { "EY", new[] { new VisemeMapping("m_Jaw+", 0.435f), new VisemeMapping("m_Open", 0.305f) } },
            { "AE", new[] { new VisemeMapping("m_Jaw+", 0.424f), new VisemeMapping("m_EH", 0.757f) } },
            
            // Central vowels
            { "AH", new[] { new VisemeMapping("m_Jaw+", 0.429f), new VisemeMapping("m_EH", 0.280f), new VisemeMapping("m_OH", 0.086f) } },
            { "AX", new[] { new VisemeMapping("m_Jaw+", 0.454f), new VisemeMapping("m_Open", 0.442f) } },
            { "ER", new[] { new VisemeMapping("m_Jaw+", 0.322f), new VisemeMapping("m_Open", 0.548f) } },
            
            // Back vowels
            { "UW", new[] { new VisemeMapping("m_Jaw+", 0.153f), new VisemeMapping("m_OH", 0.863f) } },
            { "UH", new[] { new VisemeMapping("m_Jaw+", 0.264f), new VisemeMapping("m_OH", 0.659f) } },
            { "OW", new[] { new VisemeMapping("m_Jaw+", 0.198f), new VisemeMapping("m_OH", 0.570f) } },
            { "AA", new[] { new VisemeMapping("m_Jaw+", 0.459f), new VisemeMapping("m_Open", 0.403f), new VisemeMapping("m_OW", 0.403f) } },
            { "AO", new[] { new VisemeMapping("m_Jaw+", 0.405f), new VisemeMapping("m_OH", 0.460f) } },
            
            // Diphthongs
                { "AY", new[] { new VisemeMapping("m_Jaw+", 0.313f) } },
                { "AW", new[] { new VisemeMapping("m_Jaw+", 0.574f), new VisemeMapping("m_Open", 0.683f) } },
                { "OY", new[] { new VisemeMapping("m_Jaw+", 0.261f), new VisemeMapping("m_OH", 0.570f) } },
            };

            /// <summary>
            /// Legacy alias for backward compatibility
            /// </summary>
            public static readonly Dictionary<string, VisemeMapping[]> PhonemeMap = HumanFemalePhonemeMap;

            /// <summary>
            /// Krogan phoneme to viseme mappings - from KRO_HED_PROBase_MDL_FaceFX data.
            /// </summary>
            public static readonly Dictionary<string, VisemeMapping[]> KroganPhonemeMap = new()
            {
                // Silence
                { "SIL", new[] { new VisemeMapping("jawOpen", 0.073224f) } },

                // Bilabial stops
                { "P", new[] { new VisemeMapping("jawOpen", 0.092563f), new VisemeMapping("smileRight", 0.068354f), new VisemeMapping("smileLeft", 0.070886f), new VisemeMapping("sneerRight", 0.144304f), new VisemeMapping("sneerLeft", 0.141210f), new VisemeMapping("lowerLipCurlin", 0.266426f), new VisemeMapping("upperLipCurlin", 0.091621f), new VisemeMapping("jawBack", 0.250599f) } },
                { "B", new[] { new VisemeMapping("jawOpen", 0.113120f), new VisemeMapping("smileRight", 0.147117f), new VisemeMapping("smileLeft", 0.149648f), new VisemeMapping("sneerRight", 0.200000f), new VisemeMapping("sneerLeft", 0.200000f), new VisemeMapping("lowerLipCurlin", 0.230000f), new VisemeMapping("upperLipCurlin", 0.120000f), new VisemeMapping("jawBack", 0.050000f) } },
                { "M", new[] { new VisemeMapping("jawOpen", 0.109595f), new VisemeMapping("smileRight", 0.372152f), new VisemeMapping("smileLeft", 0.326864f), new VisemeMapping("frownRight", 0.363713f), new VisemeMapping("frownLeft", 0.354993f), new VisemeMapping("lowerLipCurlin", 0.357989f), new VisemeMapping("upperLipCurlin", 0.188784f), new VisemeMapping("jawBack", 0.404822f) } },

                // Alveolar stops
                { "T", new[] { new VisemeMapping("jawOpen", 0.146185f), new VisemeMapping("smileRight", 0.150000f), new VisemeMapping("smileLeft", 0.150000f), new VisemeMapping("upperLipCurlOut", 0.151899f), new VisemeMapping("lowerLipCurlOut", 0.094635f), new VisemeMapping("sneerRight", 0.085232f), new VisemeMapping("sneerLeft", 0.084951f), new VisemeMapping("tongueUP", 1.000000f) } },
                { "D", new[] { new VisemeMapping("jawOpen", 0.111023f), new VisemeMapping("smileRight", 0.200959f), new VisemeMapping("smileLeft", 0.205087f), new VisemeMapping("upperLipCurlOut", 0.188065f), new VisemeMapping("pucker", 0.179024f), new VisemeMapping("O_mouth", 0.088608f), new VisemeMapping("tongueUP", 0.570000f), new VisemeMapping("jawBack", 0.174522f) } },

                // Velar stops
                { "K", new[] { new VisemeMapping("jawOpen", 0.130362f), new VisemeMapping("smileRight", 0.353202f), new VisemeMapping("smileLeft", 0.354071f), new VisemeMapping("upperLipCurlOut", 0.145871f), new VisemeMapping("lowerLipCurlOut", 0.133816f), new VisemeMapping("sneerRight", 0.136811f), new VisemeMapping("sneerLeft", 0.136811f), new VisemeMapping("pucker", 0.148850f), new VisemeMapping("tongueUP", 0.710000f), new VisemeMapping("jawBack", 0.128102f) } },
                { "G", new[] { new VisemeMapping("jawOpen", 0.133878f), new VisemeMapping("smileRight", 0.300000f), new VisemeMapping("smileLeft", 0.300000f), new VisemeMapping("upperLipCurlOut", 0.154913f), new VisemeMapping("lowerLipCurlOut", 0.146513f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("sneerRight", 0.292479f), new VisemeMapping("sneerLeft", 0.357157f) } },

                // Nasals
                { "N", new[] { new VisemeMapping("jawOpen", 0.131241f), new VisemeMapping("smileRight", 0.256821f), new VisemeMapping("smileLeft", 0.223255f), new VisemeMapping("upperLipCurlOut", 0.133816f), new VisemeMapping("lowerLipCurlOut", 0.082580f), new VisemeMapping("sneerRight", 0.200000f), new VisemeMapping("sneerLeft", 0.200000f), new VisemeMapping("pucker", 0.070524f), new VisemeMapping("O_mouth", 0.076552f) } },
                { "NG", new[] { new VisemeMapping("jawOpen", 0.088445f), new VisemeMapping("smileRight", 0.287764f), new VisemeMapping("smileLeft", 0.300000f), new VisemeMapping("upperLipCurlOut", 0.160000f), new VisemeMapping("sneerRight", 0.217440f), new VisemeMapping("sneerLeft", 0.234037f), new VisemeMapping("tongueUP", 1.000000f), new VisemeMapping("upperLipCurlin", 0.090000f), new VisemeMapping("jawForward", 0.067000f) } },

                // Fricatives
                { "F", new[] { new VisemeMapping("jawOpen", 0.130000f), new VisemeMapping("lowerLipCurlin", 0.400000f), new VisemeMapping("jawBack", 0.220000f) } },
                { "V", new[] { new VisemeMapping("smileRight", 0.239000f), new VisemeMapping("smileLeft", 0.250000f), new VisemeMapping("jawOpen", 0.142000f), new VisemeMapping("frownRight", 0.183000f), new VisemeMapping("frownLeft", 0.180000f), new VisemeMapping("sneerRight", 0.197000f), new VisemeMapping("sneerLeft", 0.190000f), new VisemeMapping("lowerLipCurlin", 0.480000f), new VisemeMapping("jawBack", 0.220000f) } },
                { "TH", new[] { new VisemeMapping("smileRight", 0.100000f), new VisemeMapping("smileLeft", 0.100000f), new VisemeMapping("jawOpen", 0.140000f), new VisemeMapping("upperLipCurlOut", 0.197000f), new VisemeMapping("lowerLipCurlOut", 0.070000f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("sneerRight", 0.127000f), new VisemeMapping("sneerLeft", 0.124000f), new VisemeMapping("tongueUP", 1.000000f) } },
                { "DH", new[] { new VisemeMapping("smileRight", 0.200000f), new VisemeMapping("smileLeft", 0.200000f), new VisemeMapping("jawOpen", 0.125088f), new VisemeMapping("upperLipCurlOut", 0.269000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("sneerRight", 0.300000f), new VisemeMapping("sneerLeft", 0.300000f), new VisemeMapping("O_mouth", 0.140000f), new VisemeMapping("lowerLipCurlin", 0.130000f), new VisemeMapping("tongueUP", 1.000000f), new VisemeMapping("upperLipCurlin", 0.080000f) } },
                { "S", new[] { new VisemeMapping("smileRight", 0.100000f), new VisemeMapping("smileLeft", 0.100000f), new VisemeMapping("jawOpen", 0.165500f), new VisemeMapping("lowerLipCurlOut", 0.110000f), new VisemeMapping("sneerRight", 0.239000f), new VisemeMapping("sneerLeft", 0.240000f), new VisemeMapping("tongueUP", 1.000000f) } },
                { "Z", new[] { new VisemeMapping("jawOpen", 0.120000f), new VisemeMapping("upperLipCurlOut", 0.250000f), new VisemeMapping("lowerLipCurlOut", 0.160000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("sneerRight", 0.290000f), new VisemeMapping("upperLipCurlin", 0.150000f) } },
                { "SH", new[] { new VisemeMapping("smileRight", 0.300000f), new VisemeMapping("smileLeft", 0.300000f), new VisemeMapping("jawOpen", 0.140000f), new VisemeMapping("upperLipCurlOut", 0.359000f), new VisemeMapping("lowerLipCurlOut", 0.130000f), new VisemeMapping("sneerRight", 0.300000f), new VisemeMapping("sneerLeft", 0.300000f) } },
                { "ZH", new[] { new VisemeMapping("smileRight", 0.150000f), new VisemeMapping("smileLeft", 0.150000f), new VisemeMapping("jawOpen", 0.126000f), new VisemeMapping("upperLipCurlOut", 0.329000f), new VisemeMapping("lowerLipCurlOut", 0.188000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("sneerRight", 0.700000f), new VisemeMapping("sneerLeft", 0.700000f), new VisemeMapping("pucker", 0.120000f) } },
                { "H", new[] { new VisemeMapping("smileRight", 0.300000f), new VisemeMapping("smileLeft", 0.300000f), new VisemeMapping("jawOpen", 0.270000f), new VisemeMapping("upperLipCurlOut", 0.170000f), new VisemeMapping("sneerRight", 0.350000f), new VisemeMapping("sneerLeft", 0.350000f), new VisemeMapping("O_mouth", 0.150000f), new VisemeMapping("tongueUP", 0.260000f) } },

                // Approximants
                { "R", new[] { new VisemeMapping("jawOpen", 0.140000f), new VisemeMapping("upperLipCurlOut", 0.120000f), new VisemeMapping("lowerLipCurlOut", 0.110000f), new VisemeMapping("pucker", 0.260000f), new VisemeMapping("tongueUP", 1.000000f) } },
                { "L", new[] { new VisemeMapping("jawOpen", 0.180000f), new VisemeMapping("O_mouth", 0.090000f), new VisemeMapping("tongueUP", 1.000000f) } },
                { "W", new[] { new VisemeMapping("jawOpen", 0.098700f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("pucker", 0.160000f), new VisemeMapping("O_mouth", 0.390000f), new VisemeMapping("upperLipCurlin", 0.070000f) } },
                { "Y", new[] { new VisemeMapping("smileRight", 0.150000f), new VisemeMapping("smileLeft", 0.150000f), new VisemeMapping("jawOpen", 0.100000f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("sneerRight", 0.500000f), new VisemeMapping("sneerLeft", 0.500000f), new VisemeMapping("pucker", 0.330000f), new VisemeMapping("O_mouth", 0.150000f) } },

                // Affricates
                { "CH", new[] { new VisemeMapping("smileRight", 0.340000f), new VisemeMapping("smileLeft", 0.340000f), new VisemeMapping("jawOpen", 0.110000f), new VisemeMapping("lowerLipCurlOut", 0.115700f), new VisemeMapping("sneerRight", 0.330000f), new VisemeMapping("sneerLeft", 0.330000f), new VisemeMapping("O_mouth", 0.310000f), new VisemeMapping("upperLipCurlin", 0.200000f) } },
                { "JH", new[] { new VisemeMapping("jawOpen", 0.060000f), new VisemeMapping("frownRight", 0.150000f), new VisemeMapping("frownLeft", 0.150000f), new VisemeMapping("sneerRight", 0.550000f), new VisemeMapping("sneerLeft", 0.550000f), new VisemeMapping("O_mouth", 0.150000f), new VisemeMapping("tongueUP", 1.000000f), new VisemeMapping("jawForward", 0.110000f) } },

                // Flap
                { "FLAP", new[] { new VisemeMapping("jawOpen", 0.098717f), new VisemeMapping("smileRight", 0.100000f), new VisemeMapping("smileLeft", 0.100000f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("tongueUP", 1.000000f) } },

                // Special
                { "TS", new[] { new VisemeMapping("smileRight", 0.150000f), new VisemeMapping("smileLeft", 0.150000f), new VisemeMapping("jawOpen", 0.150000f), new VisemeMapping("upperLipCurlOut", 0.150000f), new VisemeMapping("lowerLipCurlOut", 0.090000f), new VisemeMapping("sneerRight", 0.090000f), new VisemeMapping("sneerLeft", 0.080000f), new VisemeMapping("tongueUP", 1.000000f) } },

                // Front vowels
                { "IY", new[] { new VisemeMapping("smileRight", 0.200000f), new VisemeMapping("smileLeft", 0.200000f), new VisemeMapping("jawOpen", 0.110000f), new VisemeMapping("upperLipCurlOut", 0.230000f), new VisemeMapping("lowerLipCurlOut", 0.340000f) } },
                { "IH", new[] { new VisemeMapping("smileRight", 0.180000f), new VisemeMapping("smileLeft", 0.180000f), new VisemeMapping("jawOpen", 0.300000f), new VisemeMapping("upperLipCurlOut", 0.260000f), new VisemeMapping("lowerLipCurlOut", 0.370000f) } },
                { "EH", new[] { new VisemeMapping("smileRight", 0.250000f), new VisemeMapping("smileLeft", 0.250000f), new VisemeMapping("jawOpen", 0.300000f), new VisemeMapping("frownRight", 0.190000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("sneerRight", 0.300000f), new VisemeMapping("sneerLeft", 0.300000f) } },
                { "EY", new[] { new VisemeMapping("smileRight", 0.300000f), new VisemeMapping("smileLeft", 0.300000f), new VisemeMapping("jawOpen", 0.160000f), new VisemeMapping("lowerLipCurlOut", 0.150000f), new VisemeMapping("frownRight", 0.250000f), new VisemeMapping("frownLeft", 0.250000f), new VisemeMapping("sneerRight", 0.250000f), new VisemeMapping("sneerLeft", 0.250000f) } },
                { "AE", new[] { new VisemeMapping("smileRight", 0.170000f), new VisemeMapping("smileLeft", 0.170000f), new VisemeMapping("jawOpen", 0.300000f), new VisemeMapping("lowerLipCurlOut", 0.140000f) } },

                // Central vowels
                { "AH", new[] { new VisemeMapping("jawOpen", 0.300000f), new VisemeMapping("pucker", 0.340000f), new VisemeMapping("O_mouth", 0.100000f) } },
                { "AX", new[] { new VisemeMapping("smileRight", 0.250000f), new VisemeMapping("smileLeft", 0.200000f), new VisemeMapping("jawOpen", 0.170000f), new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("sneerLeft", 0.090000f) } },
                { "ER", new[] { new VisemeMapping("smileRight", 0.100000f), new VisemeMapping("smileLeft", 0.100000f), new VisemeMapping("jawOpen", 0.140000f), new VisemeMapping("lowerLipCurlOut", 0.100000f), new VisemeMapping("O_mouth", 0.320000f), new VisemeMapping("tongueUP", 1.000000f), new VisemeMapping("jawBack", 0.270000f) } },

                // Back vowels
                { "UW", new[] { new VisemeMapping("jawOpen", 0.120000f), new VisemeMapping("pucker", 0.330000f), new VisemeMapping("O_mouth", 0.480000f) } },
                { "UH", new[] { new VisemeMapping("jawOpen", 0.130000f), new VisemeMapping("pucker", 0.390000f), new VisemeMapping("O_mouth", 0.150000f), new VisemeMapping("lowerLipCurlin", 0.090000f) } },
                { "OW", new[] { new VisemeMapping("jawOpen", 0.110144f), new VisemeMapping("sneerLeft", 0.320000f), new VisemeMapping("pucker", 0.250000f) } },
                { "AA", new[] { new VisemeMapping("jawOpen", 0.180000f), new VisemeMapping("pucker", 0.220000f), new VisemeMapping("lowerLipCurlin", 0.220000f), new VisemeMapping("jawBack", 0.080000f) } },
                { "AO", new[] { new VisemeMapping("jawOpen", 0.150000f), new VisemeMapping("frownRight", 0.130000f), new VisemeMapping("frownLeft", 0.140000f), new VisemeMapping("pucker", 0.060000f), new VisemeMapping("O_mouth", 0.280000f) } },

                // Diphthongs
                { "AY", new[] { new VisemeMapping("jawOpen", 0.200000f), new VisemeMapping("upperLipCurlOut", 0.200000f), new VisemeMapping("lowerLipCurlOut", 0.270000f), new VisemeMapping("sneerRight", 0.690000f) } },
                { "AW", new[] { new VisemeMapping("jawOpen", 0.180000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("O_mouth", 0.250000f), new VisemeMapping("lowerLipCurlin", 0.250000f) } },
                { "OY", new[] { new VisemeMapping("smileRight", 0.250000f), new VisemeMapping("smileLeft", 0.250000f), new VisemeMapping("jawOpen", 0.140000f), new VisemeMapping("upperLipCurlOut", 0.130000f), new VisemeMapping("frownRight", 0.120000f), new VisemeMapping("frownLeft", 0.120000f), new VisemeMapping("pucker", 0.210000f), new VisemeMapping("O_mouth", 0.270000f) } },
            };

        /// <summary>
        /// All viseme animation names used in lip sync for Human Female
        /// </summary>
        public static readonly string[] HumanFemaleVisemes =
        [
            "m_Jaw+",
            "m_Open",
            "m_M",
            "m_Jaw-",
            "m_EE",
            "m_N",
                "m_G",
                "m_OW",
                "m_OH",
                "m_Flap",
                "m_FV",
                "m_TH",
                "m_L",
                "m_ZZ",
                "m_EH"
            ];

            /// <summary>
            /// Human Male phoneme to viseme mappings - from HMM_HED_PROJoker_MDL_FaceFX data.
            /// </summary>
            public static readonly Dictionary<string, VisemeMapping[]> HumanMalePhonemeMap = new()
            {
                // Silence
                { "SIL", new[] { new VisemeMapping("m_Jaw+", 0.106584f), new VisemeMapping("m_Open", 0.161712f) } },

                // Bilabial stops
                { "P", new[] { new VisemeMapping("m_M", 0.729515f), new VisemeMapping("m_Jaw-", 0.112150f) } },
                { "B", new[] { new VisemeMapping("m_M", 0.764770f), new VisemeMapping("m_Jaw-", 0.760000f) } },
                { "M", new[] { new VisemeMapping("m_M", 1.000000f), new VisemeMapping("m_Jaw-", 0.186373f) } },

                // Alveolar stops
                { "T", new[] { new VisemeMapping("m_EE", 0.406647f), new VisemeMapping("m_N", 1.000000f), new VisemeMapping("m_G", 0.128312f) } },
                { "D", new[] { new VisemeMapping("m_Jaw-", 0.054628f), new VisemeMapping("m_N", 1.000000f) } },

                // Velar stops
                { "K", new[] { new VisemeMapping("m_Jaw+", 0.250000f), new VisemeMapping("m_G", 0.363969f) } },
                { "G", new[] { new VisemeMapping("m_Jaw+", 0.090000f), new VisemeMapping("m_G", 0.657148f) } },

                // Nasals
                { "N", new[] { new VisemeMapping("m_EE", 0.495714f), new VisemeMapping("m_OW", 0.495714f), new VisemeMapping("m_Jaw-", 0.110295f), new VisemeMapping("m_N", 1.000000f) } },
                { "NG", new[] { new VisemeMapping("m_EE", 0.499425f), new VisemeMapping("m_Jaw+", 0.123284f), new VisemeMapping("m_M", 0.508703f), new VisemeMapping("m_N", 1.000000f) } },

                // R variant
                { "RU", new[] { new VisemeMapping("m_Jaw+", 0.110000f), new VisemeMapping("m_OH", 0.504991f) } },

                // Flap
                { "FLAP", new[] { new VisemeMapping("m_Flap", 1.000000f), new VisemeMapping("m_Jaw+", 0.219773f) } },

                // Fricatives
                { "F", new[] { new VisemeMapping("m_Jaw+", 0.128850f), new VisemeMapping("m_FV", 0.764770f) } },
                { "V", new[] { new VisemeMapping("m_Jaw+", 0.151117f), new VisemeMapping("m_FV", 1.000000f) } },
                { "TH", new[] { new VisemeMapping("m_Jaw+", 0.156684f), new VisemeMapping("m_TH", 0.723948f) } },
                { "DH", new[] { new VisemeMapping("m_Jaw+", 0.216062f), new VisemeMapping("m_TH", 0.931771f) } },
                { "S", new[] { new VisemeMapping("m_EE", 0.261913f), new VisemeMapping("m_Jaw+", 0.089884f), new VisemeMapping("m_OW", 0.614470f), new VisemeMapping("m_M", 0.471591f) } },
                { "Z", new[] { new VisemeMapping("m_EE", 0.267479f), new VisemeMapping("m_Jaw+", 0.017517f), new VisemeMapping("m_OW", 0.571792f), new VisemeMapping("m_M", 0.647870f) } },
                { "SH", new[] { new VisemeMapping("m_Jaw+", 0.090000f), new VisemeMapping("m_OW", 0.855693f) } },
                { "ZH", new[] { new VisemeMapping("m_Jaw+", 0.089884f), new VisemeMapping("m_OW", 0.532825f) } },
                { "HH", new[] { new VisemeMapping("m_EE", 0.352835f), new VisemeMapping("m_Jaw+", 0.164106f) } },
                { "H", new[] { new VisemeMapping("m_EE", 0.352835f), new VisemeMapping("m_Jaw+", 0.164106f) } },

                // Approximants
                { "R", new[] { new VisemeMapping("m_Jaw+", 0.100000f), new VisemeMapping("m_OH", 0.569936f) } },
                { "Y", new[] { new VisemeMapping("m_EE", 0.222946f), new VisemeMapping("m_Jaw+", 0.149262f) } },
                { "L", new[] { new VisemeMapping("m_Jaw+", 0.151117f), new VisemeMapping("m_L", 1.000000f) } },
                { "W", new[] { new VisemeMapping("m_Jaw+", 0.136273f), new VisemeMapping("m_OH", 0.709103f) } },

                // Special
                { "TS", new[] { new VisemeMapping("m_Jaw-", 0.065761f), new VisemeMapping("m_ZZ", 1.000000f) } },

                // Affricates
                { "CH", new[] { new VisemeMapping("m_Jaw+", 0.088028f), new VisemeMapping("m_Open", 1.000000f) } },
                { "JH", new[] { new VisemeMapping("m_Jaw+", 0.125139f), new VisemeMapping("m_OH", 0.458602f) } },

                // Front vowels
                { "IY", new[] { new VisemeMapping("m_EE", 0.317580f), new VisemeMapping("m_Jaw+", 0.195651f), new VisemeMapping("m_ZZ", 0.822831f) } },
                { "IH", new[] { new VisemeMapping("m_EE", 0.251318f), new VisemeMapping("m_Jaw+", 0.253173f), new VisemeMapping("m_OW", 0.336135f) } },
                { "E", new[] { new VisemeMapping("m_Jaw+", 0.512952f), new VisemeMapping("m_EH", 0.466025f) } },
                { "EH", new[] { new VisemeMapping("m_Jaw+", 0.247606f), new VisemeMapping("m_EH", 0.436336f) } },
                { "EY", new[] { new VisemeMapping("m_Jaw+", 0.435018f), new VisemeMapping("m_Open", 0.304591f) } },
                { "AE", new[] { new VisemeMapping("m_Jaw+", 0.423885f), new VisemeMapping("m_EH", 0.757348f) } },

                // Central vowels
                { "AH", new[] { new VisemeMapping("m_Jaw+", 0.429452f), new VisemeMapping("m_EH", 0.280468f), new VisemeMapping("m_OH", 0.085634f) } },
                { "AX", new[] { new VisemeMapping("m_Jaw+", 0.453574f), new VisemeMapping("m_Open", 0.441902f) } },
                { "ER", new[] { new VisemeMapping("m_Jaw+", 0.321829f), new VisemeMapping("m_Open", 0.547669f) } },
                { "AXR", new[] { new VisemeMapping("m_Jaw+", 0.340385f), new VisemeMapping("m_Open", 0.820437f) } },
                { "EXR", new[] { new VisemeMapping("m_Jaw+", 0.318118f), new VisemeMapping("m_Open", 0.336135f) } },

                // Back vowels
                { "UW", new[] { new VisemeMapping("m_Jaw+", 0.152973f), new VisemeMapping("m_OH", 0.863115f) } },
                { "UH", new[] { new VisemeMapping("m_Jaw+", 0.264307f), new VisemeMapping("m_OH", 0.659003f) } },
                { "OW", new[] { new VisemeMapping("m_Jaw+", 0.197506f), new VisemeMapping("m_OH", 0.569936f) } },
                { "AA", new[] { new VisemeMapping("m_Jaw+", 0.459141f), new VisemeMapping("m_Open", 0.402936f), new VisemeMapping("m_OW", 0.402936f) } },
                { "O", new[] { new VisemeMapping("m_Jaw+", 0.405329f), new VisemeMapping("m_OH", 0.460458f) } },
                { "AO", new[] { new VisemeMapping("m_Jaw+", 0.405329f), new VisemeMapping("m_OH", 0.460458f) } },

                // Diphthongs
                { "AY", new[] { new VisemeMapping("m_Jaw+", 0.312551f) } },
                { "AW", new[] { new VisemeMapping("m_Jaw+", 0.574185f), new VisemeMapping("m_Open", 0.683125f) } },
                { "OY", new[] { new VisemeMapping("m_Jaw+", 0.260595f), new VisemeMapping("m_OH", 0.569936f) } },
            };

            /// <summary>
                /// All viseme animation names used in lip sync for Human Male
                /// </summary>
                public static readonly string[] HumanMaleVisemes =
                [
                    "m_Jaw+",
                    "m_Open",
                    "m_M",
                    "m_Jaw-",
                    "m_EE",
                    "m_N",
                    "m_G",
                    "m_OW",
                    "m_OH",
                    "m_Flap",
                    "m_FV",
                    "m_TH",
                    "m_L",
                    "m_ZZ",
                    "m_EH"
                ];

            /// <summary>
            /// Asari phoneme to viseme mappings - from ASA_HED_PROBASE_MDL_FaceFX data.
            /// </summary>
            public static readonly Dictionary<string, VisemeMapping[]> AsariPhonemeMap = new()
            {
                // Bilabial stops
                { "P", new[] { new VisemeMapping("pucker", 0.270000f), new VisemeMapping("upperLipCurlIn", 0.330000f), new VisemeMapping("lowerLipCurlIn", 0.420000f), new VisemeMapping("jawClench", 0.240000f) } },
                { "B", new[] { new VisemeMapping("upperLipCurlIn", 0.110000f), new VisemeMapping("lowerLipCurlIn", 0.110000f), new VisemeMapping("jawClench", 0.190000f) } },
                { "M", new[] { new VisemeMapping("upperLipCurlIn", 0.433527f), new VisemeMapping("lowerLipCurlIn", 0.433527f), new VisemeMapping("jawBack", 0.154540f) } },

                // Alveolar stops
                { "T", new[] { new VisemeMapping("sneerRight", 0.050000f), new VisemeMapping("sneerLeft", 0.050000f), new VisemeMapping("frownRight", 0.512228f), new VisemeMapping("frownLeft", 0.520720f), new VisemeMapping("jawForward", 0.350883f) } },
                { "D", new[] { new VisemeMapping("sneerRight", 0.020000f), new VisemeMapping("sneerLeft", 0.020000f), new VisemeMapping("frownRight", 0.350000f), new VisemeMapping("frownLeft", 0.350000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("mouthDownLeft", 0.210000f), new VisemeMapping("mouthDownRight", 0.200000f), new VisemeMapping("tongueUP", 1.000000f) } },

                // Velar stops
                { "K", new[] { new VisemeMapping("frownRight", 0.350000f), new VisemeMapping("frownLeft", 0.350000f), new VisemeMapping("jawOpen", 0.048573f), new VisemeMapping("jawRotateDown", 0.080000f) } },
                { "G", new[] { new VisemeMapping("frownRight", 0.420000f), new VisemeMapping("frownLeft", 0.420000f), new VisemeMapping("jawRotate", 0.320000f), new VisemeMapping("pucker", 0.130000f), new VisemeMapping("lowerLipCurlIn", 0.130000f), new VisemeMapping("upperLipCurlIn", 0.330000f) } },

                // Nasals
                { "N", new[] { new VisemeMapping("O_mouth", 0.230000f), new VisemeMapping("jawOpen", 0.070000f), new VisemeMapping("jawRotate", 0.030000f), new VisemeMapping("tongueUP", 1.000000f) } },
                { "NG", new[] { new VisemeMapping("frownRight", 0.400000f), new VisemeMapping("frownLeft", 0.400000f), new VisemeMapping("jawOpen", 0.057065f), new VisemeMapping("jawRotateDown", 0.030000f), new VisemeMapping("lowerLipCurlIn", 0.260000f) } },

                // R variants
                { "RA", new[] { new VisemeMapping("O_mouth", 0.152174f), new VisemeMapping("jawOpen", 0.072351f), new VisemeMapping("jawRotate", 0.110000f), new VisemeMapping("jawRotateUp", 0.136889f) } },
                { "RU", new[] { new VisemeMapping("jawRotate", 0.110000f), new VisemeMapping("jawOpen", 0.072351f), new VisemeMapping("O_mouth", 0.152174f), new VisemeMapping("jawRotateUp", 0.140000f) } },

                // Flap
                { "FLAP", new[] { new VisemeMapping("jawOpen", 0.005000f) } },

                // Fricatives
                { "PH", new[] { new VisemeMapping("sneerRight", 0.080000f), new VisemeMapping("sneerLeft", 0.080000f), new VisemeMapping("upperLipCurlOut", 0.547894f), new VisemeMapping("lowerLipCurlIn", 0.357989f), new VisemeMapping("jawClench", 0.082276f), new VisemeMapping("jawRotateUp", 0.128983f), new VisemeMapping("lowerLipUpLeft", 0.262568f), new VisemeMapping("lowerLipUpRight", 0.284647f) } },
                { "F", new[] { new VisemeMapping("sneerRight", 0.080000f), new VisemeMapping("sneerLeft", 0.080000f), new VisemeMapping("upperLipCurlOut", 0.547894f), new VisemeMapping("lowerLipCurlIn", 0.357989f), new VisemeMapping("jawClench", 0.082276f), new VisemeMapping("jawRotateUp", 0.128983f), new VisemeMapping("lowerLipUpLeft", 0.262568f), new VisemeMapping("lowerLipUpRight", 0.284647f) } },
                { "V", new[] { new VisemeMapping("upperLipCurlOut", 0.490000f), new VisemeMapping("lowerLipCurlIn", 0.216712f), new VisemeMapping("jawClench", 0.170000f) } },
                { "TH", new[] { new VisemeMapping("O_mouth", 0.310000f), new VisemeMapping("jawOpen", 0.048573f), new VisemeMapping("jawRotate", 0.070000f), new VisemeMapping("jawRotateDown", 0.077446f), new VisemeMapping("tongueUP", 1.000000f) } },
                { "DH", new[] { new VisemeMapping("upperLipCurlOut", 0.430000f), new VisemeMapping("jawOpen", 0.060000f), new VisemeMapping("jawRotate", 0.070000f), new VisemeMapping("tongueUP", 1.000000f) } },
                { "S", new[] { new VisemeMapping("sneerRight", 0.080000f), new VisemeMapping("sneerLeft", 0.080000f), new VisemeMapping("frownRight", 0.423913f), new VisemeMapping("frownLeft", 0.432405f), new VisemeMapping("jawForward", 0.344090f), new VisemeMapping("smileRight", 0.020000f), new VisemeMapping("smileLeft", 0.020000f), new VisemeMapping("jawRotateUp", 0.094429f) } },
                { "Z", new[] { new VisemeMapping("sneerRight", 0.063000f), new VisemeMapping("sneerLeft", 0.063000f), new VisemeMapping("frownRight", 0.445992f), new VisemeMapping("frownLeft", 0.452785f), new VisemeMapping("jawForward", 0.355978f), new VisemeMapping("smileRight", 0.040000f), new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("jawRotateUp", 0.079144f) } },
                { "SH", new[] { new VisemeMapping("sneerRight", 0.030000f), new VisemeMapping("sneerLeft", 0.030000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("O_mouth", 0.540000f) } },
                { "ZH", new[] { new VisemeMapping("frownRight", 0.150000f), new VisemeMapping("frownLeft", 0.150000f), new VisemeMapping("O_mouth", 0.420000f), new VisemeMapping("jawOpen", 0.019701f), new VisemeMapping("pucker", 0.250000f), new VisemeMapping("jawRotateDown", 0.046875f) } },
                { "CX", new[] { new VisemeMapping("frownRight", 0.350000f), new VisemeMapping("frownLeft", 0.350000f), new VisemeMapping("jawOpen", 0.048573f), new VisemeMapping("jawRotateDown", 0.080842f) } },
                { "X", new[] { new VisemeMapping("frownRight", 0.350000f), new VisemeMapping("frownLeft", 0.350000f), new VisemeMapping("jawOpen", 0.048573f), new VisemeMapping("jawRotateDown", 0.080842f) } },
                { "GH", new[] { new VisemeMapping("frownRight", 0.420000f), new VisemeMapping("frownLeft", 0.420000f), new VisemeMapping("jawRotate", 0.320000f), new VisemeMapping("pucker", 0.130000f), new VisemeMapping("lowerLipCurlIn", 0.130000f), new VisemeMapping("upperLipCurlIn", 0.330000f) } },
                { "HH", new[] { new VisemeMapping("frownRight", 0.617527f), new VisemeMapping("frownLeft", 0.619226f), new VisemeMapping("jawOpen", 0.092708f), new VisemeMapping("jawRotate", 0.300000f), new VisemeMapping("pucker", 0.170000f), new VisemeMapping("jawRotateDown", 0.169158f) } },
                { "H", new[] { new VisemeMapping("jawRotateDown", 0.169158f), new VisemeMapping("frownRight", 0.620000f), new VisemeMapping("frownLeft", 0.620000f), new VisemeMapping("jawOpen", 0.090000f), new VisemeMapping("jawRotate", 0.300000f), new VisemeMapping("pucker", 0.170000f) } },

                // Approximants
                { "R", new[] { new VisemeMapping("jawRotateUp", 0.136889f), new VisemeMapping("O_mouth", 0.152174f), new VisemeMapping("jawRotate", 0.110000f), new VisemeMapping("jawOpen", 0.072351f) } },
                { "Y", new[] { new VisemeMapping("frownRight", 0.580163f), new VisemeMapping("frownLeft", 0.534307f), new VisemeMapping("jawRotate", 0.100000f), new VisemeMapping("jawRotateDown", 0.150000f), new VisemeMapping("jawOpen", 0.029891f) } },
                { "L", new[] { new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("jawRotateDown", 0.090000f), new VisemeMapping("tongueUP", 1.000000f), new VisemeMapping("jawRotate", 0.060462f), new VisemeMapping("jawOpen", 0.060000f) } },
                { "W", new[] { new VisemeMapping("pucker", 0.760000f) } },

                // Special
                { "TS", new[] { new VisemeMapping("sneerRight", 0.050000f), new VisemeMapping("sneerLeft", 0.050000f), new VisemeMapping("frownRight", 0.512228f), new VisemeMapping("frownLeft", 0.520720f), new VisemeMapping("jawForward", 0.350883f) } },

                // Affricates
                { "CH", new[] { new VisemeMapping("sneerRight", 0.050000f), new VisemeMapping("sneerLeft", 0.050000f), new VisemeMapping("jawForward", 0.279552f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("O_mouth", 0.442595f), new VisemeMapping("pucker", 0.116508f), new VisemeMapping("jawRotateDown", 0.062160f) } },
                { "JH", new[] { new VisemeMapping("sneerRight", 0.020000f), new VisemeMapping("sneerLeft", 0.020000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("lowerLipCurlOut", 0.160000f), new VisemeMapping("jawForward", 0.340000f), new VisemeMapping("mouthDownLeft", 0.070000f), new VisemeMapping("mouthDownRight", 0.060000f) } },

                // Front vowels
                { "IY", new[] { new VisemeMapping("frownRight", 0.600000f), new VisemeMapping("frownLeft", 0.600000f), new VisemeMapping("jawOpen", 0.034986f), new VisemeMapping("smileLeft", 0.020000f), new VisemeMapping("smileRight", 0.020000f), new VisemeMapping("jawRotateDown", 0.123302f) } },
                { "IH", new[] { new VisemeMapping("jawRotate", 0.100000f), new VisemeMapping("frownRight", 0.400000f), new VisemeMapping("frownLeft", 0.400000f), new VisemeMapping("jawOpen", 0.045177f), new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("smileRight", 0.040000f), new VisemeMapping("jawRotateDown", 0.128397f) } },
                { "E", new[] { new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("jawOpen", 0.063859f), new VisemeMapping("jawRotateDown", 0.102921f) } },
                { "EN", new[] { new VisemeMapping("jawRotateDown", 0.102921f), new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("jawOpen", 0.063859f) } },
                { "EH", new[] { new VisemeMapping("sneerRight", 0.060000f), new VisemeMapping("sneerLeft", 0.060000f), new VisemeMapping("frownRight", 0.450000f), new VisemeMapping("frownLeft", 0.450000f), new VisemeMapping("jawOpen", 0.072917f), new VisemeMapping("jawRotate", 0.350000f), new VisemeMapping("jawRotateDown", 0.116508f) } },
                { "EY", new[] { new VisemeMapping("frownRight", 0.600000f), new VisemeMapping("frownLeft", 0.600000f), new VisemeMapping("smileRight", 0.030000f), new VisemeMapping("smileLeft", 0.030000f), new VisemeMapping("jawRotateDown", 0.150476f), new VisemeMapping("jawOpen", 0.050000f) } },
                { "AE", new[] { new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("jawOpen", 0.053669f), new VisemeMapping("lowerLipCurlOut", 0.210000f), new VisemeMapping("jawRotateDown", 0.113111f) } },

                // Central vowels
                { "AH", new[] { new VisemeMapping("mouthDownLeft", 0.160000f), new VisemeMapping("mouthDownRight", 0.150000f), new VisemeMapping("O_mouth", 0.170000f), new VisemeMapping("jawOpen", 0.090000f), new VisemeMapping("jawRotate", 0.100000f) } },
                { "AX", new[] { new VisemeMapping("jawRotateDown", 0.089334f), new VisemeMapping("frownRight", 0.400000f), new VisemeMapping("frownLeft", 0.400000f), new VisemeMapping("jawOpen", 0.065557f), new VisemeMapping("jawRotate", 0.100000f) } },
                { "UX", new[] { new VisemeMapping("jawRotateDown", 0.089334f), new VisemeMapping("frownLeft", 0.400000f), new VisemeMapping("frownRight", 0.400000f), new VisemeMapping("jawOpen", 0.065557f), new VisemeMapping("jawRotate", 0.100000f) } },
                { "ER", new[] { new VisemeMapping("jawRotateDown", 0.140285f), new VisemeMapping("jawClench", 0.125000f), new VisemeMapping("upperLipCurlIn", 0.190000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("jawOpen", 0.021399f), new VisemeMapping("frownLeft", 0.575068f), new VisemeMapping("frownRight", 0.561481f) } },
                { "AXR", new[] { new VisemeMapping("jawRotateDown", 0.140285f), new VisemeMapping("jawClench", 0.125000f), new VisemeMapping("upperLipCurlIn", 0.190000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("jawOpen", 0.021399f), new VisemeMapping("frownLeft", 0.575068f), new VisemeMapping("frownRight", 0.561481f) } },
                { "EXR", new[] { new VisemeMapping("jawRotateDown", 0.140285f), new VisemeMapping("jawClench", 0.125000f), new VisemeMapping("upperLipCurlIn", 0.190000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("jawOpen", 0.021399f), new VisemeMapping("frownLeft", 0.575068f), new VisemeMapping("frownRight", 0.561481f) } },

                // Back vowels
                { "A", new[] { new VisemeMapping("jawRotateDown", 0.102921f), new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("jawOpen", 0.063859f) } },
                { "AA", new[] { new VisemeMapping("jawRotateDown", 0.102921f), new VisemeMapping("jawOpen", 0.063859f), new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f) } },
                { "AAN", new[] { new VisemeMapping("jawRotateDown", 0.102921f), new VisemeMapping("jawOpen", 0.063859f), new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f) } },
                { "AO", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("O_mouth", 0.125000f), new VisemeMapping("jawOpen", 0.053669f), new VisemeMapping("jawRotate", 0.100000f), new VisemeMapping("pucker", 0.074049f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("jawRotateDown", 0.090000f) } },
                { "AON", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("O_mouth", 0.125000f), new VisemeMapping("jawOpen", 0.053669f), new VisemeMapping("jawRotate", 0.100000f), new VisemeMapping("pucker", 0.074049f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("jawRotateDown", 0.085938f) } },
                { "O", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("O_mouth", 0.125000f), new VisemeMapping("jawOpen", 0.053669f), new VisemeMapping("jawRotate", 0.100000f), new VisemeMapping("pucker", 0.074049f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("jawRotateDown", 0.085938f) } },
                { "ON", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("O_mouth", 0.125000f), new VisemeMapping("jawOpen", 0.053669f), new VisemeMapping("jawRotate", 0.100000f), new VisemeMapping("pucker", 0.074049f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("jawRotateDown", 0.085938f) } },
                { "UW", new[] { new VisemeMapping("jawRotateDown", 0.125000f), new VisemeMapping("O_mouth", 0.264266f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("jawOpen", 0.109375f), new VisemeMapping("lowerLipCurlOut", 0.570000f) } },
                { "UH", new[] { new VisemeMapping("jawRotateDown", 0.080842f), new VisemeMapping("O_mouth", 0.370000f), new VisemeMapping("upperLipCurlOut", 0.220000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("jawRotate", 0.150000f), new VisemeMapping("lowerLipCurlOut", 0.510000f) } },
                { "OW", new[] { new VisemeMapping("jawRotateDown", 0.082541f), new VisemeMapping("O_mouth", 0.310000f), new VisemeMapping("jawRotate", 0.100000f), new VisemeMapping("jawOpen", 0.038383f), new VisemeMapping("pucker", 0.270000f) } },
                { "UY", new[] { new VisemeMapping("pucker", 0.760000f) } },
                { "UU", new[] { new VisemeMapping("pucker", 0.760000f) } },
                { "EU", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("O_mouth", 0.125000f), new VisemeMapping("jawOpen", 0.053669f), new VisemeMapping("jawRotate", 0.100000f), new VisemeMapping("pucker", 0.074049f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("jawRotateDown", 0.085938f) } },
                { "OE", new[] { new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("O_mouth", 0.125000f), new VisemeMapping("jawOpen", 0.053669f), new VisemeMapping("jawRotate", 0.100000f), new VisemeMapping("pucker", 0.074049f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("jawRotateDown", 0.085938f) } },
                { "OEN", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("O_mouth", 0.125000f), new VisemeMapping("jawOpen", 0.053669f), new VisemeMapping("jawRotate", 0.100000f), new VisemeMapping("pucker", 0.074049f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("jawRotateDown", 0.085938f) } },

                // Diphthongs
                { "AY", new[] { new VisemeMapping("frownRight", 0.400000f), new VisemeMapping("frownLeft", 0.400000f), new VisemeMapping("jawOpen", 0.065557f), new VisemeMapping("jawRotate", 0.100000f), new VisemeMapping("smileRight", 0.050000f), new VisemeMapping("smileLeft", 0.038383f), new VisemeMapping("jawRotateDown", 0.085938f) } },
                { "AW", new[] { new VisemeMapping("jawRotateDown", 0.097826f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("jawRotate", 0.350000f), new VisemeMapping("mouthDownRight", 0.120000f), new VisemeMapping("mouthDownLeft", 0.140000f), new VisemeMapping("O_mouth", 0.408628f) } },
                { "OY", new[] { new VisemeMapping("jawRotateDown", 0.102921f), new VisemeMapping("O_mouth", 0.371264f), new VisemeMapping("jawOpen", 0.048573f), new VisemeMapping("jawRotate", 0.300000f) } },
            };

            /// <summary>
            /// All viseme animation names used in lip sync for Asari
            /// </summary>
            public static readonly string[] AsariVisemes =
            [
                "pucker",
                "upperLipCurlIn",
                "lowerLipCurlIn",
                "jawClench",
                "sneerRight",
                "sneerLeft",
                "frownRight",
                "frownLeft",
                "jawForward",
                "jawOpen",
                "jawRotate",
                "jawRotateDown",
                "jawRotateUp",
                "mouthDownLeft",
                "mouthDownRight",
                "tongueUP",
                "O_mouth",
                "upperLipCurlOut",
                "lowerLipCurlOut",
                "smileRight",
                "smileLeft",
                "jawBack",
                "lowerLipUpLeft",
                "lowerLipUpRight"
            ];

                /// <summary>
                /// All viseme animation names used in lip sync for Krogan
                /// </summary>
            public static readonly string[] KroganVisemes =
            [
                "jawOpen",
                "smileRight",
                "smileLeft",
                "sneerRight",
                "sneerLeft",
                "lowerLipCurlin",
                "upperLipCurlin",
                "jawBack",
                "upperLipCurlOut",
                "lowerLipCurlOut",
                "tongueUP",
                "pucker",
                "O_mouth",
                "frownRight",
                "frownLeft",
                "jawForward"
            ];

        /// <summary>
        /// Drell phoneme to viseme mappings - from DRL_HED_PROTHANE_MDL_FaceFX data.
        /// </summary>
        public static readonly Dictionary<string, VisemeMapping[]> DrellPhonemeMap = new()
        {
            // Alveolar stops
            { "T", new[] { new VisemeMapping("smileRight", 0.040000f), new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("sneerLeft", 0.120000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawForward", 0.350000f) } },
            { "D", new[] { new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("sneerRight", 0.110000f), new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("mouthDownLeft", 0.210000f), new VisemeMapping("tongueUP", 1.000000f), new VisemeMapping("mouthDownRight", 0.200000f) } },

            // Velar stops
            { "K", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawOpen", 0.050000f) } },
            { "G", new[] { new VisemeMapping("sneerRight", 0.113839f), new VisemeMapping("sneerLeft", 0.087798f), new VisemeMapping("frownRight", 0.348214f), new VisemeMapping("frownLeft", 0.340774f), new VisemeMapping("jawRotate", 0.220000f), new VisemeMapping("pucker", 0.130000f), new VisemeMapping("upperLipCurlIn", 0.330000f), new VisemeMapping("lowerLipCurlIn", 0.046875f), new VisemeMapping("jawRotateUp", 0.229167f) } },

            // Bilabial stops
            { "P", new[] { new VisemeMapping("pucker", 0.050000f), new VisemeMapping("upperLipCurlIn", 0.330000f), new VisemeMapping("jawClench", 0.240000f), new VisemeMapping("lowerLipCurlIn", 0.420000f) } },
            { "B", new[] { new VisemeMapping("upperLipCurlIn", 0.110000f), new VisemeMapping("lowerLipCurlIn", 0.110000f), new VisemeMapping("jawClench", 0.190000f) } },
            { "M", new[] { new VisemeMapping("upperLipCurlIn", 0.690000f), new VisemeMapping("lowerLipCurlIn", 0.890000f) } },

            // Nasals
            { "N", new[] { new VisemeMapping("sneerRight", 0.113957f), new VisemeMapping("sneerLeft", 0.118523f), new VisemeMapping("jawRotateUp", 0.220107f), new VisemeMapping("tongueUP", 1.000000f), new VisemeMapping("jawRotate", 0.030000f), new VisemeMapping("O_mouth", 0.100000f), new VisemeMapping("jawOpen", 0.070000f) } },
            { "NG", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawOpen", 0.060000f), new VisemeMapping("lowerLipCurlIn", 0.260000f) } },

            // R variants
            { "RA", new[] { new VisemeMapping("jawRotate", 0.110000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("O_mouth", 0.200000f) } },
            { "RU", new[] { new VisemeMapping("jawRotate", 0.110000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("O_mouth", 0.200000f) } },

            // Flap
            { "FLAP", new[] { new VisemeMapping("jawOpen", 0.005000f) } },

            // Fricatives
            { "PH", new[] { new VisemeMapping("sneerRight", 0.070000f), new VisemeMapping("sneerLeft", 0.060000f), new VisemeMapping("upperLipCurlOut", 0.520000f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "F", new[] { new VisemeMapping("sneerRight", 0.070000f), new VisemeMapping("sneerLeft", 0.060000f), new VisemeMapping("upperLipCurlOut", 0.520000f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "V", new[] { new VisemeMapping("sneerRight", 0.060000f), new VisemeMapping("sneerLeft", 0.040000f), new VisemeMapping("lowerLipCurlIn", 1.000000f), new VisemeMapping("upperLipCurlOut", 0.490000f), new VisemeMapping("jawClench", 0.170000f) } },
            { "TH", new[] { new VisemeMapping("sneerRight", 0.173310f), new VisemeMapping("sneerLeft", 0.164179f), new VisemeMapping("jawRotate", 0.053463f), new VisemeMapping("O_mouth", 0.200000f), new VisemeMapping("jawOpen", 0.039766f), new VisemeMapping("jawRotateUp", 0.210976f), new VisemeMapping("tongueUP", 1.000000f) } },
            { "DH", new[] { new VisemeMapping("jawRotate", 0.070000f), new VisemeMapping("O_mouth", 0.100000f), new VisemeMapping("jawOpen", 0.060000f), new VisemeMapping("upperLipCurlOut", 0.430000f), new VisemeMapping("tongueUP", 1.000000f), new VisemeMapping("jawRotateUp", 0.119664f) } },
            { "S", new[] { new VisemeMapping("smileRight", 0.050000f), new VisemeMapping("smileLeft", 0.050000f), new VisemeMapping("sneerRight", 0.180000f), new VisemeMapping("sneerLeft", 0.210000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("jawForward", 0.020000f) } },
            { "Z", new[] { new VisemeMapping("smileRight", 0.060000f), new VisemeMapping("smileLeft", 0.060000f), new VisemeMapping("jawForward", 0.220000f), new VisemeMapping("sneerRight", 0.130000f), new VisemeMapping("sneerLeft", 0.150000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f) } },
            { "SH", new[] { new VisemeMapping("sneerRight", 0.120000f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("O_mouth", 0.300000f) } },
            { "ZH", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("sneerRight", 0.070000f), new VisemeMapping("sneerLeft", 0.070000f), new VisemeMapping("O_mouth", 0.200000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipCurlOut", 0.300000f), new VisemeMapping("jawForward", 0.340000f) } },
            { "X", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawOpen", 0.050000f) } },
            { "CX", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawOpen", 0.050000f) } },
            { "GH", new[] { new VisemeMapping("sneerRight", 0.113839f), new VisemeMapping("sneerLeft", 0.087798f), new VisemeMapping("frownRight", 0.348214f), new VisemeMapping("frownLeft", 0.340774f), new VisemeMapping("jawRotate", 0.220000f), new VisemeMapping("pucker", 0.130000f), new VisemeMapping("upperLipCurlIn", 0.330000f), new VisemeMapping("lowerLipCurlIn", 0.046875f), new VisemeMapping("jawRotateUp", 0.229167f) } },
            { "HH", new[] { new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("jawRotate", 0.190000f), new VisemeMapping("pucker", 0.050000f) } },
            { "H", new[] { new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("jawRotate", 0.190000f), new VisemeMapping("pucker", 0.050000f) } },

            // Approximants
            { "R", new[] { new VisemeMapping("jawRotate", 0.110000f), new VisemeMapping("O_mouth", 0.200000f), new VisemeMapping("jawOpen", 0.040000f) } },
            { "Y", new[] { new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("jawRotate", 0.100000f) } },
            { "L", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("tongueUP", 1.000000f) } },
            { "W", new[] { new VisemeMapping("pucker", 0.300000f) } },

            // Special
            { "TS", new[] { new VisemeMapping("smileRight", 0.040000f), new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("sneerLeft", 0.120000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("jawForward", 0.350000f) } },

            // Affricates
            { "CH", new[] { new VisemeMapping("sneerRight", 0.090000f), new VisemeMapping("sneerLeft", 0.090000f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.070000f), new VisemeMapping("O_mouth", 0.100000f), new VisemeMapping("pucker", 0.050000f) } },
            { "JH", new[] { new VisemeMapping("sneerRight", 0.130000f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("O_mouth", 0.100000f), new VisemeMapping("lowerLipCurlOut", 0.160000f), new VisemeMapping("jawForward", 0.340000f), new VisemeMapping("mouthDownLeft", 0.070000f), new VisemeMapping("mouthDownRight", 0.060000f) } },

            // Front vowels
            { "IY", new[] { new VisemeMapping("smileRight", 0.118176f), new VisemeMapping("smileLeft", 0.104346f), new VisemeMapping("sneerRight", 0.080000f), new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("sneerLeft", 0.090000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("jawOpen", 0.020000f), new VisemeMapping("mouthDownLeft", 0.210000f), new VisemeMapping("mouthDownRight", 0.220000f) } },
            { "IH", new[] { new VisemeMapping("smileRight", 0.040000f), new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("jawRotate", 0.200000f), new VisemeMapping("mouthDownLeft", 0.230000f), new VisemeMapping("mouthDownRight", 0.210000f) } },
            { "EH", new[] { new VisemeMapping("smileRight", 0.070000f), new VisemeMapping("smileLeft", 0.070000f), new VisemeMapping("sneerRight", 0.120000f), new VisemeMapping("sneerLeft", 0.110000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawRotate", 0.250000f) } },
            { "EY", new[] { new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawOpen", 0.070000f), new VisemeMapping("mouthDownLeft", 0.220000f), new VisemeMapping("mouthDownRight", 0.210000f), new VisemeMapping("smileRight", 0.081651f), new VisemeMapping("smileLeft", 0.131740f) } },
            { "AE", new[] { new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("lowerLipCurlOut", 0.270000f), new VisemeMapping("mouthDownLeft", 0.180000f), new VisemeMapping("mouthDownRight", 0.190000f) } },
            { "E", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("jawOpen", 0.089988f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "EN", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("jawOpen", 0.089988f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },

            // Central vowels
            { "AH", new[] { new VisemeMapping("jawRotate", 0.190000f), new VisemeMapping("O_mouth", 0.100000f), new VisemeMapping("jawOpen", 0.090000f), new VisemeMapping("mouthDownLeft", 0.160000f), new VisemeMapping("mouthDownRight", 0.150000f) } },
            { "AX", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.100000f), new VisemeMapping("jawOpen", 0.060000f) } },
            { "UX", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.100000f), new VisemeMapping("jawOpen", 0.060000f) } },
            { "ER", new[] { new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("upperLipCurlIn", 0.190000f), new VisemeMapping("jawClench", 0.280000f) } },
            { "AXR", new[] { new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("upperLipCurlIn", 0.190000f), new VisemeMapping("jawClench", 0.280000f) } },
            { "EXR", new[] { new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("upperLipCurlIn", 0.190000f), new VisemeMapping("jawClench", 0.280000f) } },

            // Back vowels
            { "UW", new[] { new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("jawOpen", 0.050000f), new VisemeMapping("O_mouth", 0.363923f), new VisemeMapping("pucker", 0.099119f), new VisemeMapping("lowerLipCurlOut", 0.570000f) } },
            { "UH", new[] { new VisemeMapping("jawRotate", 0.150000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("O_mouth", 0.200000f), new VisemeMapping("upperLipCurlOut", 0.220000f), new VisemeMapping("lowerLipCurlOut", 0.510000f) } },
            { "OW", new[] { new VisemeMapping("jawRotate", 0.190000f), new VisemeMapping("O_mouth", 0.200000f), new VisemeMapping("jawOpen", 0.060000f), new VisemeMapping("pucker", 0.050000f) } },
            { "AA", new[] { new VisemeMapping("jawOpen", 0.089988f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "A", new[] { new VisemeMapping("jawOpen", 0.089988f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "AAN", new[] { new VisemeMapping("jawOpen", 0.089988f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "AO", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipCurlOut", 0.180000f) } },
            { "AON", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipCurlOut", 0.180000f) } },
            { "O", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipCurlOut", 0.180000f) } },
            { "ON", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipCurlOut", 0.180000f) } },
            { "UY", new[] { new VisemeMapping("pucker", 0.300000f) } },
            { "UU", new[] { new VisemeMapping("pucker", 0.300000f) } },
            { "EU", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "OE", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "OEN", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },

            // Diphthongs
            { "AY", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawOpen", 0.010000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("mouthDownLeft", 0.230000f), new VisemeMapping("mouthDownRight", 0.220000f) } },
            { "AW", new[] { new VisemeMapping("jawRotate", 0.240000f), new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("mouthDownLeft", 0.140000f), new VisemeMapping("mouthDownRight", 0.120000f) } },
            { "OY", new[] { new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("jawRotate", 0.210000f) } },
        };

        /// <summary>
        /// All viseme animation names used in lip sync for Drell
        /// </summary>
        public static readonly string[] DrellVisemes =
        [
            "smileRight",
            "smileLeft",
            "sneerRight",
            "sneerLeft",
            "frownRight",
            "frownLeft",
            "jawForward",
            "jawRotate",
            "jawOpen",
            "mouthDownLeft",
            "mouthDownRight",
            "tongueUP",
            "pucker",
            "upperLipCurlIn",
            "lowerLipCurlIn",
            "jawClench",
            "jawRotateUp",
            "O_mouth",
            "upperLipCurlOut",
            "lowerLipCurlOut"
        ];

        /// <summary>
        /// Turian phoneme to viseme mappings - from TUR_HED_PROGarrus_MDL_FaceFX data.
        /// </summary>
        public static readonly Dictionary<string, VisemeMapping[]> TurianPhonemeMap = new()
        {
            // Silence
            { "SIL", new[] { new VisemeMapping("jawClench", 0.010000f) } },

            // Bilabial stops
            { "P", new[] { new VisemeMapping("lowerLipCurlin", 0.760000f), new VisemeMapping("pucker", 0.760000f), new VisemeMapping("upperLipCurlin", 0.760000f), new VisemeMapping("noseDown", 0.020000f) } },
            { "B", new[] { new VisemeMapping("jawClench", 0.130599f) } },
            { "M", new[] { new VisemeMapping("upperLipCurlin", 1.000000f), new VisemeMapping("lowerLipCurlin", 1.000000f), new VisemeMapping("pucker", 1.000000f), new VisemeMapping("smileRight", 0.430000f), new VisemeMapping("smileLeft", 0.430000f) } },

            // Alveolar stops
            { "T", new[] { new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("upperLipCurlin", 0.760000f), new VisemeMapping("lowerLipCurlin", 0.760000f), new VisemeMapping("pucker", 0.170000f), new VisemeMapping("noseDown", 0.210000f), new VisemeMapping("tongueUp2", 0.505000f), new VisemeMapping("tongueUp3", 0.809000f) } },
            { "D", new[] { new VisemeMapping("upperLipCurlOut", 0.370000f), new VisemeMapping("lowerLipCurlOut", 0.280000f), new VisemeMapping("jawOpen", 0.050000f), new VisemeMapping("sneerRight", 0.130000f), new VisemeMapping("sneerLeft", 0.130000f), new VisemeMapping("tongueUp1", 0.537000f), new VisemeMapping("tongueUp2", 0.774000f), new VisemeMapping("tongueUp3", 0.280000f) } },

            // Velar stops
            { "K", new[] { new VisemeMapping("upperLipCurlOut", 0.350000f), new VisemeMapping("lowerLipCurlOut", 0.390000f), new VisemeMapping("O_mouth", 0.080000f), new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("sneerLeft", 0.100000f) } },
            { "G", new[] { new VisemeMapping("upperLipCurlOut", 0.500000f), new VisemeMapping("lowerLipCurlOut", 0.520000f), new VisemeMapping("O_mouth", 0.160000f), new VisemeMapping("jawOpen", 0.010000f) } },

            // Nasals
            { "N", new[] { new VisemeMapping("sneerRight", 0.250000f), new VisemeMapping("sneerLeft", 0.250000f), new VisemeMapping("tongueUp1", 0.510000f), new VisemeMapping("tongueUp2", 0.530000f), new VisemeMapping("tongueUp3", 0.700000f) } },
            { "NG", new[] { new VisemeMapping("upperLipCurlin", 0.420000f), new VisemeMapping("tongueUp", 0.720000f), new VisemeMapping("smileRight", 0.690000f), new VisemeMapping("smileLeft", 0.690000f), new VisemeMapping("sneerRight", 0.200000f), new VisemeMapping("sneerLeft", 0.200000f) } },

            // R variants
            { "RA", new[] { new VisemeMapping("jawOpen", 0.111740f) } },
            { "RU", new[] { new VisemeMapping("jawOpen", 0.110000f) } },

            // Flap
            { "FLAP", new[] { new VisemeMapping("lowerLipCurlOut", 0.120000f), new VisemeMapping("jawOpen", 0.020000f), new VisemeMapping("lowerLipCurlin", 0.130000f), new VisemeMapping("tongueUp", 0.660000f) } },

            // Fricatives
            { "PH", new[] { new VisemeMapping("jawOpen", 0.046000f), new VisemeMapping("upperLipCurlin", 1.000000f), new VisemeMapping("lowerLipCurlin", 1.000000f), new VisemeMapping("pucker", 1.000000f), new VisemeMapping("sneerRight", 0.150000f), new VisemeMapping("sneerLeft", 0.150000f) } },
            { "F", new[] { new VisemeMapping("jawOpen", 0.050000f), new VisemeMapping("upperLipCurlin", 1.000000f), new VisemeMapping("lowerLipCurlin", 1.000000f), new VisemeMapping("pucker", 1.000000f), new VisemeMapping("tongueUp", 0.660000f), new VisemeMapping("sneerRight", 0.150000f), new VisemeMapping("sneerLeft", 0.150000f) } },
            { "V", new[] { new VisemeMapping("jawOpen", 0.054300f), new VisemeMapping("upperLipCurlin", 0.590000f), new VisemeMapping("lowerLipCurlin", 1.000000f), new VisemeMapping("pucker", 0.530000f) } },
            { "TH", new[] { new VisemeMapping("sneerRight", 0.290000f), new VisemeMapping("sneerLeft", 0.500000f), new VisemeMapping("tongueUp1", 0.230000f), new VisemeMapping("tongueUp2", 0.500000f), new VisemeMapping("tongueUp3", 0.800000f), new VisemeMapping("MandibleFlareRight", 0.130000f), new VisemeMapping("MandibleFlareLeft", 0.220000f) } },
            { "DH", new[] { new VisemeMapping("tongueUp1", 0.350000f), new VisemeMapping("tongueUp2", 0.750000f), new VisemeMapping("tongueUp3", 0.730000f) } },
            { "S", new[] { new VisemeMapping("jawOpen", 0.084000f), new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 0.210000f), new VisemeMapping("sneerLeft", 0.210000f) } },
            { "Z", new[] { new VisemeMapping("jawOpen", 0.910000f), new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 0.260000f), new VisemeMapping("sneerLeft", 0.260000f) } },
            { "SH", new[] { new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f), new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawOpen", 0.039000f), new VisemeMapping("pucker", 0.530000f) } },
            { "ZH", new[] { new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f), new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("sneerRight", 0.250000f), new VisemeMapping("sneerLeft", 0.250000f), new VisemeMapping("tongueUp1", 0.680000f), new VisemeMapping("tongueUp2", 0.520000f), new VisemeMapping("tongueUp3", 0.210000f) } },
            { "CX", new[] { new VisemeMapping("upperLipCurlOut", 0.350000f), new VisemeMapping("lowerLipCurlOut", 0.390000f), new VisemeMapping("O_mouth", 0.080000f), new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("sneerLeft", 0.100000f) } },
            { "X", new[] { new VisemeMapping("upperLipCurlOut", 0.350000f), new VisemeMapping("lowerLipCurlOut", 0.390000f), new VisemeMapping("O_mouth", 0.080000f), new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("sneerLeft", 0.100000f) } },
            { "GH", new[] { new VisemeMapping("upperLipCurlOut", 0.500000f), new VisemeMapping("lowerLipCurlOut", 0.520000f), new VisemeMapping("O_mouth", 0.160000f), new VisemeMapping("jawOpen", 0.010000f) } },
            { "HH", new[] { new VisemeMapping("upperLipCurlOut", 0.370000f), new VisemeMapping("lowerLipCurlOut", 0.390000f), new VisemeMapping("O_mouth", 0.330000f), new VisemeMapping("pucker", 0.900000f), new VisemeMapping("noseDown", 0.200000f) } },
            { "H", new[] { new VisemeMapping("upperLipCurlOut", 0.370000f), new VisemeMapping("lowerLipCurlOut", 0.390000f), new VisemeMapping("O_mouth", 0.330000f), new VisemeMapping("pucker", 0.900000f), new VisemeMapping("noseDown", 0.200000f) } },

            // Approximants
            { "R", new[] { new VisemeMapping("jawOpen", 0.110000f) } },
            { "Y", new[] { new VisemeMapping("upperLipCurlOut", 0.370000f), new VisemeMapping("lowerLipCurlOut", 0.390000f), new VisemeMapping("O_mouth", 0.480000f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("pucker", 0.900000f), new VisemeMapping("noseDown", 0.200000f), new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f) } },
            { "L", new[] { new VisemeMapping("O_mouth", 0.260000f), new VisemeMapping("jawOpen", 0.070000f), new VisemeMapping("tongueUp1", 0.430000f), new VisemeMapping("tongueUp2", 0.630000f), new VisemeMapping("tongueUp3", 0.680000f), new VisemeMapping("mouthDownRight", 0.050000f), new VisemeMapping("jawClench", 0.440000f) } },
            { "W", new[] { new VisemeMapping("lowerLipCurlOut", 0.010000f), new VisemeMapping("upperLipCurlin", 1.000000f), new VisemeMapping("lowerLipCurlin", 0.700000f), new VisemeMapping("pucker", 1.000000f), new VisemeMapping("noseDown", 0.100000f) } },

            // Special
            { "TS", new[] { new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("upperLipCurlin", 0.760000f), new VisemeMapping("lowerLipCurlin", 0.760000f), new VisemeMapping("pucker", 0.170000f), new VisemeMapping("noseDown", 0.210000f), new VisemeMapping("tongueUp2", 0.505517f), new VisemeMapping("tongueUp3", 0.810000f) } },

            // Affricates
            { "CH", new[] { new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f), new VisemeMapping("O_mouth", 0.300000f) } },
            { "JH", new[] { new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f), new VisemeMapping("O_mouth", 0.220000f) } },

            // Front vowels
            { "IY", new[] { new VisemeMapping("jawOpen", 0.020000f), new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 0.300000f), new VisemeMapping("sneerLeft", 0.300000f) } },
            { "IH", new[] { new VisemeMapping("jawOpen", 0.200000f), new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 0.250000f), new VisemeMapping("sneerLeft", 0.250000f), new VisemeMapping("jawForward", 0.380000f) } },
            { "E", new[] { new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("sneerRight", 0.131000f), new VisemeMapping("sneerLeft", 0.151000f), new VisemeMapping("MandibleFlareRight", 0.053000f), new VisemeMapping("MandibleFlareLeft", 0.020000f) } },
            { "EN", new[] { new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 0.130000f), new VisemeMapping("sneerLeft", 0.150000f), new VisemeMapping("MandibleFlareRight", 0.050000f), new VisemeMapping("MandibleFlareLeft", 0.020000f) } },
            { "EH", new[] { new VisemeMapping("jawOpen", 0.120000f), new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f) } },
            { "EY", new[] { new VisemeMapping("jawOpen", 0.120000f), new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 1.000000f), new VisemeMapping("sneerLeft", 1.000000f), new VisemeMapping("MandibleFlareRight", 0.140000f), new VisemeMapping("MandibleFlareLeft", 0.099856f) } },
            { "AE", new[] { new VisemeMapping("jawOpen", 0.130000f) } },

            // Central vowels
            { "AH", new[] { new VisemeMapping("jawOpen", 0.300000f), new VisemeMapping("MandibleFlareRight", 0.050000f), new VisemeMapping("MandibleFlareLeft", 0.050000f), new VisemeMapping("noseUp", 0.040000f) } },
            { "AX", new[] { new VisemeMapping("jawOpen", 0.121000f), new VisemeMapping("smileRight", 0.710000f), new VisemeMapping("smileLeft", 0.710000f) } },
            { "UX", new[] { new VisemeMapping("jawOpen", 0.120000f), new VisemeMapping("smileRight", 0.710000f), new VisemeMapping("smileLeft", 0.710000f) } },
            { "ER", new[] { new VisemeMapping("jawOpen", 0.100000f), new VisemeMapping("lowerLipCurlin", 1.000000f), new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("tongueUp1", 0.730000f), new VisemeMapping("tongueUp2", 0.796000f), new VisemeMapping("tongueUp3", 0.150000f) } },
            { "AXR", new[] { new VisemeMapping("jawOpen", 0.100000f), new VisemeMapping("lowerLipCurlin", 1.000000f), new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("tongueUp1", 0.730000f), new VisemeMapping("tongueUp2", 0.800000f), new VisemeMapping("tongueUp3", 0.150000f) } },
            { "EXR", new[] { new VisemeMapping("jawOpen", 0.100000f), new VisemeMapping("lowerLipCurlin", 1.000000f), new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("tongueUp1", 0.730000f), new VisemeMapping("tongueUp2", 0.800000f), new VisemeMapping("tongueUp3", 0.150000f) } },

            // Back vowels
            { "A", new[] { new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("sneerRight", 0.130000f), new VisemeMapping("sneerLeft", 0.150000f), new VisemeMapping("MandibleFlareRight", 0.050000f), new VisemeMapping("MandibleFlareLeft", 0.020000f) } },
            { "AA", new[] { new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.200000f), new VisemeMapping("sneerRight", 0.130000f), new VisemeMapping("sneerLeft", 0.150000f), new VisemeMapping("MandibleFlareRight", 0.050000f), new VisemeMapping("MandibleFlareLeft", 0.020000f) } },
            { "AAN", new[] { new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("sneerRight", 0.130000f), new VisemeMapping("sneerLeft", 0.150000f), new VisemeMapping("MandibleFlareRight", 0.050000f), new VisemeMapping("MandibleFlareLeft", 0.020000f) } },
            { "AO", new[] { new VisemeMapping("lowerLipCurlOut", 1.000000f), new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.170000f) } },
            { "AON", new[] { new VisemeMapping("lowerLipCurlOut", 1.000000f), new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.170000f) } },
            { "O", new[] { new VisemeMapping("lowerLipCurlOut", 1.000000f), new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.250000f) } },
            { "ON", new[] { new VisemeMapping("lowerLipCurlOut", 1.000000f), new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.170000f) } },
            { "UW", new[] { new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f), new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawOpen", 0.170000f), new VisemeMapping("jawForward", 0.470000f) } },
            { "UH", new[] { new VisemeMapping("upperLipCurlOut", 0.870000f), new VisemeMapping("lowerLipCurlOut", 0.850000f), new VisemeMapping("O_mouth", 0.390000f), new VisemeMapping("jawOpen", 0.100000f), new VisemeMapping("pucker", 1.000000f) } },
            { "OW", new[] { new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f), new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawOpen", 0.140000f), new VisemeMapping("tongueUp", 0.337707f), new VisemeMapping("smileRight", 0.470000f), new VisemeMapping("smileLeft", 0.170000f) } },
            { "UY", new[] { new VisemeMapping("lowerLipCurlOut", 0.010000f), new VisemeMapping("upperLipCurlin", 1.000000f), new VisemeMapping("lowerLipCurlin", 0.710000f), new VisemeMapping("pucker", 1.000000f), new VisemeMapping("noseDown", 0.100000f) } },
            { "UU", new[] { new VisemeMapping("lowerLipCurlOut", 0.005952f), new VisemeMapping("upperLipCurlin", 1.000000f), new VisemeMapping("lowerLipCurlin", 0.710000f), new VisemeMapping("pucker", 1.000000f), new VisemeMapping("noseDown", 0.100000f) } },
            { "EU", new[] { new VisemeMapping("lowerLipCurlOut", 1.000000f), new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.170000f) } },
            { "OE", new[] { new VisemeMapping("lowerLipCurlOut", 1.000000f), new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.170000f) } },
            { "OEN", new[] { new VisemeMapping("lowerLipCurlOut", 1.000000f), new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.170000f) } },

            // Diphthongs
            { "AY", new[] { new VisemeMapping("jawOpen", 0.120000f), new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 0.150000f), new VisemeMapping("sneerLeft", 0.150000f) } },
            { "AW", new[] { new VisemeMapping("upperLipCurlOut", 0.450000f), new VisemeMapping("lowerLipCurlOut", 0.510000f), new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawOpen", 0.080357f), new VisemeMapping("pucker", 1.000000f) } },
            { "OY", new[] { new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f), new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawOpen", 0.100000f) } },
        };

        /// <summary>
        /// All viseme animation names used in lip sync for Turian
        /// </summary>
        public static readonly string[] TurianVisemes =
        [
            "jawClench",
            "lowerLipCurlin",
            "pucker",
            "upperLipCurlin",
            "noseDown",
            "jawOpen",
            "tongueUp",
            "tongueUp1",
            "tongueUp2",
            "tongueUp3",
            "upperLipCurlOut",
            "lowerLipCurlOut",
            "sneerRight",
            "sneerLeft",
            "O_mouth",
            "smileRight",
            "smileLeft",
            "MandibleFlareRight",
            "MandibleFlareLeft",
            "mouthDownRight",
            "jawForward",
            "noseUp"
        ];

        /// <summary>
        /// Salarian phoneme to viseme mappings - from SAL_HED_PROBASE_MDL_FaceFX data.
        /// </summary>
        public static readonly Dictionary<string, VisemeMapping[]> SalarianPhonemeMap = new()
        {
            // Alveolar stops
            { "T", new[] { new VisemeMapping("smileRight", 0.040000f), new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("sneerLeft", 0.120000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawForward", 0.350000f) } },
            { "D", new[] { new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("sneerRight", 0.110000f), new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("mouthDownLeft", 0.210000f), new VisemeMapping("tongueUP", 1.000000f), new VisemeMapping("mouthDownRight", 0.200000f) } },

            // Velar stops
            { "K", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawOpen", 0.050000f) } },
            { "G", new[] { new VisemeMapping("sneerRight", 0.113839f), new VisemeMapping("sneerLeft", 0.087798f), new VisemeMapping("frownRight", 0.348214f), new VisemeMapping("frownLeft", 0.340774f), new VisemeMapping("jawRotate", 0.220000f), new VisemeMapping("pucker", 0.130000f), new VisemeMapping("upperLipCurlIn", 0.330000f), new VisemeMapping("lowerLipCurlIn", 0.046875f), new VisemeMapping("jawRotateUp", 0.229167f) } },

            // Bilabial stops
            { "P", new[] { new VisemeMapping("pucker", 0.050000f), new VisemeMapping("upperLipCurlIn", 0.330000f), new VisemeMapping("jawClench", 0.240000f), new VisemeMapping("lowerLipCurlIn", 0.420000f) } },
            { "B", new[] { new VisemeMapping("upperLipCurlIn", 0.110000f), new VisemeMapping("lowerLipCurlIn", 0.110000f), new VisemeMapping("jawClench", 0.190000f) } },
            { "M", new[] { new VisemeMapping("upperLipCurlIn", 0.690000f), new VisemeMapping("lowerLipCurlIn", 0.890000f) } },

            // Nasals
            { "N", new[] { new VisemeMapping("sneerRight", 0.113957f), new VisemeMapping("sneerLeft", 0.118523f), new VisemeMapping("jawRotateUp", 0.220107f), new VisemeMapping("tongueUP", 1.000000f), new VisemeMapping("jawRotate", 0.030000f), new VisemeMapping("O_mouth", 0.100000f), new VisemeMapping("jawOpen", 0.070000f) } },
            { "NG", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawOpen", 0.060000f), new VisemeMapping("lowerLipCurlIn", 0.260000f) } },

            // R variants
            { "RA", new[] { new VisemeMapping("jawRotate", 0.110000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("O_mouth", 0.200000f) } },
            { "RU", new[] { new VisemeMapping("jawRotate", 0.110000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("O_mouth", 0.200000f) } },

            // Flap
            { "FLAP", new[] { new VisemeMapping("jawOpen", 0.005000f) } },

            // Fricatives
            { "PH", new[] { new VisemeMapping("sneerRight", 0.070000f), new VisemeMapping("sneerLeft", 0.060000f), new VisemeMapping("upperLipCurlOut", 0.520000f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "F", new[] { new VisemeMapping("sneerRight", 0.070000f), new VisemeMapping("sneerLeft", 0.060000f), new VisemeMapping("upperLipCurlOut", 0.520000f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "V", new[] { new VisemeMapping("sneerRight", 0.060000f), new VisemeMapping("sneerLeft", 0.040000f), new VisemeMapping("lowerLipCurlIn", 1.000000f), new VisemeMapping("upperLipCurlOut", 0.490000f), new VisemeMapping("jawClench", 0.170000f) } },
            { "TH", new[] { new VisemeMapping("sneerRight", 0.173310f), new VisemeMapping("sneerLeft", 0.164179f), new VisemeMapping("jawRotate", 0.053463f), new VisemeMapping("O_mouth", 0.200000f), new VisemeMapping("jawOpen", 0.039766f), new VisemeMapping("jawRotateUp", 0.210976f), new VisemeMapping("tongueUP", 1.000000f) } },
            { "DH", new[] { new VisemeMapping("jawRotate", 0.070000f), new VisemeMapping("O_mouth", 0.100000f), new VisemeMapping("jawOpen", 0.060000f), new VisemeMapping("upperLipCurlOut", 0.430000f), new VisemeMapping("tongueUP", 1.000000f), new VisemeMapping("jawRotateUp", 0.119664f) } },
            { "S", new[] { new VisemeMapping("smileRight", 0.050000f), new VisemeMapping("smileLeft", 0.050000f), new VisemeMapping("sneerRight", 0.180000f), new VisemeMapping("sneerLeft", 0.210000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("jawForward", 0.020000f) } },
            { "Z", new[] { new VisemeMapping("smileRight", 0.060000f), new VisemeMapping("smileLeft", 0.060000f), new VisemeMapping("jawForward", 0.220000f), new VisemeMapping("sneerRight", 0.130000f), new VisemeMapping("sneerLeft", 0.150000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f) } },
            { "SH", new[] { new VisemeMapping("sneerRight", 0.120000f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("O_mouth", 0.300000f) } },
            { "ZH", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("sneerRight", 0.070000f), new VisemeMapping("sneerLeft", 0.070000f), new VisemeMapping("O_mouth", 0.200000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipCurlOut", 0.300000f), new VisemeMapping("jawForward", 0.340000f) } },
            { "X", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawOpen", 0.050000f) } },
            { "CX", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawOpen", 0.050000f) } },
            { "GH", new[] { new VisemeMapping("sneerRight", 0.113839f), new VisemeMapping("sneerLeft", 0.087798f), new VisemeMapping("frownRight", 0.348214f), new VisemeMapping("frownLeft", 0.340774f), new VisemeMapping("jawRotate", 0.220000f), new VisemeMapping("pucker", 0.130000f), new VisemeMapping("upperLipCurlIn", 0.330000f), new VisemeMapping("lowerLipCurlIn", 0.046875f), new VisemeMapping("jawRotateUp", 0.229167f) } },
            { "HH", new[] { new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("jawRotate", 0.190000f), new VisemeMapping("pucker", 0.050000f) } },
            { "H", new[] { new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("jawRotate", 0.190000f), new VisemeMapping("pucker", 0.050000f) } },

            // Approximants
            { "R", new[] { new VisemeMapping("jawRotate", 0.110000f), new VisemeMapping("O_mouth", 0.200000f), new VisemeMapping("jawOpen", 0.040000f) } },
            { "Y", new[] { new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("jawRotate", 0.100000f) } },
            { "L", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("tongueUP", 1.000000f) } },
            { "W", new[] { new VisemeMapping("pucker", 0.300000f) } },

            // Special
            { "TS", new[] { new VisemeMapping("smileRight", 0.040000f), new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("sneerLeft", 0.120000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("jawForward", 0.350000f) } },

            // Affricates
            { "CH", new[] { new VisemeMapping("sneerRight", 0.090000f), new VisemeMapping("sneerLeft", 0.090000f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.070000f), new VisemeMapping("O_mouth", 0.100000f), new VisemeMapping("pucker", 0.050000f) } },
            { "JH", new[] { new VisemeMapping("sneerRight", 0.130000f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("O_mouth", 0.100000f), new VisemeMapping("lowerLipCurlOut", 0.160000f), new VisemeMapping("jawForward", 0.340000f), new VisemeMapping("mouthDownLeft", 0.070000f), new VisemeMapping("mouthDownRight", 0.060000f) } },

            // Front vowels
            { "IY", new[] { new VisemeMapping("smileRight", 0.118176f), new VisemeMapping("smileLeft", 0.104346f), new VisemeMapping("sneerRight", 0.080000f), new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("sneerLeft", 0.090000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("jawOpen", 0.020000f), new VisemeMapping("mouthDownLeft", 0.210000f), new VisemeMapping("mouthDownRight", 0.220000f) } },
            { "IH", new[] { new VisemeMapping("smileRight", 0.040000f), new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("jawRotate", 0.200000f), new VisemeMapping("mouthDownLeft", 0.230000f), new VisemeMapping("mouthDownRight", 0.210000f) } },
            { "EH", new[] { new VisemeMapping("smileRight", 0.070000f), new VisemeMapping("smileLeft", 0.070000f), new VisemeMapping("sneerRight", 0.120000f), new VisemeMapping("sneerLeft", 0.110000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawRotate", 0.250000f) } },
            { "EY", new[] { new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawOpen", 0.070000f), new VisemeMapping("mouthDownLeft", 0.220000f), new VisemeMapping("mouthDownRight", 0.210000f), new VisemeMapping("smileRight", 0.081651f), new VisemeMapping("smileLeft", 0.131740f) } },
            { "AE", new[] { new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("lowerLipCurlOut", 0.270000f), new VisemeMapping("mouthDownLeft", 0.180000f), new VisemeMapping("mouthDownRight", 0.190000f) } },
            { "E", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("jawOpen", 0.089988f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "EN", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("jawOpen", 0.089988f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },

            // Central vowels
            { "AH", new[] { new VisemeMapping("jawRotate", 0.190000f), new VisemeMapping("O_mouth", 0.100000f), new VisemeMapping("jawOpen", 0.090000f), new VisemeMapping("mouthDownLeft", 0.160000f), new VisemeMapping("mouthDownRight", 0.150000f) } },
            { "AX", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.100000f), new VisemeMapping("jawOpen", 0.060000f) } },
            { "UX", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.100000f), new VisemeMapping("jawOpen", 0.060000f) } },
            { "ER", new[] { new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("upperLipCurlIn", 0.190000f), new VisemeMapping("jawClench", 0.280000f) } },
            { "AXR", new[] { new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("upperLipCurlIn", 0.190000f), new VisemeMapping("jawClench", 0.280000f) } },
            { "EXR", new[] { new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("upperLipCurlIn", 0.190000f), new VisemeMapping("jawClench", 0.280000f) } },

            // Back vowels
            { "UW", new[] { new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("jawOpen", 0.050000f), new VisemeMapping("O_mouth", 0.363923f), new VisemeMapping("pucker", 0.099119f), new VisemeMapping("lowerLipCurlOut", 0.570000f) } },
            { "UH", new[] { new VisemeMapping("jawRotate", 0.150000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("O_mouth", 0.200000f), new VisemeMapping("upperLipCurlOut", 0.220000f), new VisemeMapping("lowerLipCurlOut", 0.510000f) } },
            { "OW", new[] { new VisemeMapping("jawRotate", 0.190000f), new VisemeMapping("O_mouth", 0.200000f), new VisemeMapping("jawOpen", 0.060000f), new VisemeMapping("pucker", 0.050000f) } },
            { "AA", new[] { new VisemeMapping("jawOpen", 0.089988f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "A", new[] { new VisemeMapping("jawOpen", 0.089988f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "AAN", new[] { new VisemeMapping("jawOpen", 0.089988f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "AO", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipCurlOut", 0.180000f) } },
            { "AON", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipCurlOut", 0.180000f) } },
            { "O", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipCurlOut", 0.180000f) } },
            { "ON", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipCurlOut", 0.180000f) } },
            { "UY", new[] { new VisemeMapping("pucker", 0.300000f) } },
            { "UU", new[] { new VisemeMapping("pucker", 0.300000f) } },
            { "EU", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "OE", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "OEN", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },

            // Diphthongs
            { "AY", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawOpen", 0.010000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("mouthDownLeft", 0.230000f), new VisemeMapping("mouthDownRight", 0.220000f) } },
            { "AW", new[] { new VisemeMapping("jawRotate", 0.240000f), new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("mouthDownLeft", 0.140000f), new VisemeMapping("mouthDownRight", 0.120000f) } },
            { "OY", new[] { new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("jawRotate", 0.210000f) } },
        };

        /// <summary>
        /// All viseme animation names used in lip sync for Salarian
        /// </summary>
        public static readonly string[] SalarianVisemes =
        [
            "smileRight",
            "smileLeft",
            "sneerRight",
            "sneerLeft",
            "frownRight",
            "frownLeft",
            "jawForward",
            "jawRotate",
            "jawOpen",
            "mouthDownLeft",
            "mouthDownRight",
            "tongueUP",
            "pucker",
            "upperLipCurlIn",
            "lowerLipCurlIn",
            "jawClench",
            "jawRotateUp",
            "O_mouth",
            "upperLipCurlOut",
            "lowerLipCurlOut"
        ];

        /// <summary>
        /// Elcor phoneme to viseme mappings - from SFX_Elcor_FaceFX data.
        /// Note: Elcor use bone names as phoneme identifiers in FaceFX mapping.
        /// </summary>
        public static readonly Dictionary<string, VisemeMapping[]> ElcorPhonemeMap = new()
        {
            // Bone-based phoneme mappings from SFX_Elcor_FaceFX
            { "brow_left", new[] { new VisemeMapping("sneerRight", 0.250000f), new VisemeMapping("sneerLeft", 0.250000f), new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "brow_right", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 0.210000f), new VisemeMapping("sneerLeft", 0.210000f), new VisemeMapping("jawOpen", 0.084077f) } },
            { "cheek_left", new[] { new VisemeMapping("upperLipCurlOut", 0.500000f), new VisemeMapping("lowerLipCurlOut", 0.520000f), new VisemeMapping("jawOpen", 0.010000f), new VisemeMapping("O_mouth", 0.160000f) } },
            { "cheek_right", new[] { new VisemeMapping("pucker", 0.534226f), new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawOpen", 0.039435f), new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "Chest", new[] { new VisemeMapping("sneerRight", 0.130000f), new VisemeMapping("sneerLeft", 0.130000f), new VisemeMapping("upperLipCurlOut", 0.370000f), new VisemeMapping("lowerLipCurlOut", 0.230000f), new VisemeMapping("jawOpen", 0.050000f), new VisemeMapping("tongueUp1", 0.390000f), new VisemeMapping("tongueUp2", 0.660000f), new VisemeMapping("tongueUp3", 0.760000f) } },
            { "Chest1", new[] { new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("upperLipCurlOut", 0.350000f), new VisemeMapping("lowerLipCurlOut", 0.390000f), new VisemeMapping("O_mouth", 0.080000f) } },
            { "eyeBlink_Left", new[] { new VisemeMapping("tongueUp1", 0.180000f), new VisemeMapping("tongueUp2", 0.270000f), new VisemeMapping("tongueUp3", 0.360000f), new VisemeMapping("tongueForward", 0.890000f) } },
            { "eyeBlink_Right", new[] { new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("upperLipCurlOut", 0.350000f), new VisemeMapping("lowerLipCurlOut", 0.390000f), new VisemeMapping("O_mouth", 0.080000f) } },
            { "GOD", new[] { new VisemeMapping("upperLipCurlIn", 0.760000f), new VisemeMapping("lowerLipCurlIn", 0.760000f), new VisemeMapping("pucker", 0.170000f), new VisemeMapping("noseDown", 0.200000f) } },
            { "Head", new[] { new VisemeMapping("smileRight", 0.430000f), new VisemeMapping("smileLeft", 0.430000f), new VisemeMapping("pucker", 1.000000f), new VisemeMapping("upperLipCurlIn", 1.000000f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "jawBone", new[] { new VisemeMapping("sneerRight", 0.250000f), new VisemeMapping("sneerLeft", 0.250000f), new VisemeMapping("tongueUp1", 0.510000f), new VisemeMapping("tongueUp2", 0.530000f), new VisemeMapping("tongueUp3", 0.700000f) } },
            { "LeftAnkle", new[] { new VisemeMapping("pucker", 1.000000f), new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawOpen", 0.080357f), new VisemeMapping("upperLipCurlOut", 0.450000f), new VisemeMapping("lowerLipCurlOut", 0.510000f) } },
            { "LeftBangle", new[] { new VisemeMapping("jawOpen", 0.010000f) } },
            { "LeftCollar", new[] { new VisemeMapping("upperLipCurlOut", 0.370000f), new VisemeMapping("lowerLipCurlOut", 0.390000f), new VisemeMapping("pucker", 0.900000f), new VisemeMapping("O_mouth", 0.330000f), new VisemeMapping("noseDown", 0.200000f) } },
            { "LeftDigit", new[] { new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.080357f) } },
            { "LeftElbow", new[] { new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f), new VisemeMapping("O_mouth", 0.300000f) } },
            { "LeftFlap", new[] { new VisemeMapping("upperLipCurlOut", 0.370000f), new VisemeMapping("lowerLipCurlOut", 0.390000f), new VisemeMapping("pucker", 0.900000f), new VisemeMapping("O_mouth", 0.330000f), new VisemeMapping("noseDown", 0.200000f) } },
            { "LeftFoot", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 0.150000f), new VisemeMapping("sneerLeft", 0.150000f), new VisemeMapping("jawOpen", 0.120000f) } },
            { "LeftHip", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("jawOpen", 0.098958f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "LeftIndexFinger", new[] { new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.080357f) } },
            { "LeftIndexFinger1", new[] { new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.173363f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "LeftIndexFinger2", new[] { new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.173363f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "LeftIndexToe", new[] { new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawOpen", 0.136161f), new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "LeftKnee", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 0.150000f), new VisemeMapping("sneerLeft", 0.150000f), new VisemeMapping("jawOpen", 0.121280f) } },
            { "LeftPinkFinger", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("jawOpen", 0.125000f) } },
            { "LeftPinkFinger1", new[] { new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.080357f) } },
            { "LeftPinkFinger2", new[] { new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.080357f) } },
            { "LeftPinkToe", new[] { new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawOpen", 0.098958f), new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "LeftShoulder", new[] { new VisemeMapping("upperLipCurlIn", 0.760000f), new VisemeMapping("lowerLipCurlIn", 0.760000f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("pucker", 0.170000f), new VisemeMapping("tongueUp1", 0.300000f), new VisemeMapping("tongueUp2", 0.570000f), new VisemeMapping("tongueUp3", 0.690000f), new VisemeMapping("noseDown", 0.210000f) } },
            { "LeftShoulderTwist1", new[] { new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.173363f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "LeftThumbFinger", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 0.300000f), new VisemeMapping("sneerLeft", 0.300000f), new VisemeMapping("jawOpen", 0.020000f) } },
            { "LeftThumbFinger1", new[] { new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.080357f) } },
            { "LeftWrist", new[] { new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f), new VisemeMapping("O_mouth", 0.220000f) } },
            { "LipCorner_left", new[] { new VisemeMapping("jawOpen", 0.010000f) } },
            { "LipCorner_right", new[] { new VisemeMapping("sneerRight", 0.150000f), new VisemeMapping("sneerLeft", 0.150000f), new VisemeMapping("pucker", 1.000000f), new VisemeMapping("jawOpen", 0.046875f), new VisemeMapping("upperLipCurlIn", 1.000000f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "LipCorner1_left", new[] { new VisemeMapping("lowerLipCurlOut", 0.120000f), new VisemeMapping("lowerLipCurlIn", 0.130000f), new VisemeMapping("jawOpen", 0.020000f), new VisemeMapping("tongueUp", 0.660000f) } },
            { "LipCorner1_right", new[] { new VisemeMapping("sneerRight", 0.150000f), new VisemeMapping("sneerLeft", 0.150000f), new VisemeMapping("pucker", 1.000000f), new VisemeMapping("jawOpen", 0.046875f), new VisemeMapping("upperLipCurlIn", 1.000000f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "LowerBack", new[] { new VisemeMapping("upperLipCurlIn", 0.760000f), new VisemeMapping("lowerLipCurlIn", 0.760000f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("pucker", 0.170000f), new VisemeMapping("tongueUp1", 0.300000f), new VisemeMapping("tongueUp2", 0.570000f), new VisemeMapping("tongueUp3", 0.690000f), new VisemeMapping("noseDown", 0.210000f) } },
            { "LowerCheek_left", new[] { new VisemeMapping("smileRight", 0.690000f), new VisemeMapping("smileLeft", 0.690000f), new VisemeMapping("sneerRight", 0.200000f), new VisemeMapping("sneerLeft", 0.200000f), new VisemeMapping("upperLipCurlIn", 0.420000f), new VisemeMapping("tongueUp", 0.720000f) } },
            { "LowerCheek_right", new[] { new VisemeMapping("jawOpen", 0.010000f) } },
            { "Neck", new[] { new VisemeMapping("upperLipCurlOut", 0.500000f), new VisemeMapping("lowerLipCurlOut", 0.520000f), new VisemeMapping("jawOpen", 0.010000f), new VisemeMapping("O_mouth", 0.160000f) } },
            { "outBrow_left", new[] { new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("upperLipCurlOut", 0.350000f), new VisemeMapping("lowerLipCurlOut", 0.390000f), new VisemeMapping("O_mouth", 0.080000f) } },
            { "outBrow_right", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 0.260000f), new VisemeMapping("sneerLeft", 0.260000f), new VisemeMapping("jawOpen", 0.091518f) } },
            { "Pelvis", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("jawOpen", 0.098958f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "RightBangle", new[] { new VisemeMapping("jawOpen", 0.020000f), new VisemeMapping("O_mouth", 0.260000f), new VisemeMapping("tongueUp1", 0.280000f), new VisemeMapping("tongueUp2", 0.330000f), new VisemeMapping("tongueUp3", 0.400000f), new VisemeMapping("tongueForward", 0.820000f) } },
            { "RightCollar", new[] { new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.173363f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "RightDigit", new[] { new VisemeMapping("jawOpen", 0.173363f), new VisemeMapping("noseUp", 0.040000f) } },
            { "RightElbow", new[] { new VisemeMapping("pucker", 1.000000f), new VisemeMapping("upperLipCurlIn", 1.000000f), new VisemeMapping("lowerLipCurlOut", 0.005952f), new VisemeMapping("lowerLipCurlIn", 0.716518f), new VisemeMapping("noseDown", 0.100000f) } },
            { "RightFlap", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("pucker", 0.900000f), new VisemeMapping("O_mouth", 0.480000f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("upperLipCurlOut", 0.370000f), new VisemeMapping("lowerLipCurlOut", 0.390000f), new VisemeMapping("noseDown", 0.200000f) } },
            { "RightIndexFinger", new[] { new VisemeMapping("smileRight", 0.710000f), new VisemeMapping("smileLeft", 0.710000f), new VisemeMapping("jawOpen", 0.121280f) } },
            { "RightIndexFinger1", new[] { new VisemeMapping("smileRight", 0.710000f), new VisemeMapping("smileLeft", 0.710000f), new VisemeMapping("jawOpen", 0.121280f) } },
            { "RightIndexFinger2", new[] { new VisemeMapping("jawOpen", 0.139881f) } },
            { "RightPinkFinger", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 0.250000f), new VisemeMapping("sneerLeft", 0.250000f), new VisemeMapping("jawOpen", 0.060000f), new VisemeMapping("jawForward", 0.380000f) } },
            { "RightPinkFinger1", new[] { new VisemeMapping("pucker", 1.000000f), new VisemeMapping("upperLipCurlIn", 1.000000f), new VisemeMapping("lowerLipCurlOut", 0.005952f), new VisemeMapping("lowerLipCurlIn", 0.716518f), new VisemeMapping("noseDown", 0.100000f) } },
            { "RightPinkFinger2", new[] { new VisemeMapping("pucker", 1.000000f), new VisemeMapping("O_mouth", 0.392857f), new VisemeMapping("jawOpen", 0.050595f), new VisemeMapping("upperLipCurlOut", 0.870000f), new VisemeMapping("lowerLipCurlOut", 0.850000f) } },
            { "RightShoulder", new[] { new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawForward", 0.470000f), new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "RightShoulderTwist1", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("jawOpen", 0.098958f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "RightThumbFinger", new[] { new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.173363f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "RightThumbFinger1", new[] { new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.173363f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "RightWrist", new[] { new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.173363f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "Root", new[] { new VisemeMapping("jawForward", 0.010000f) } },
            { "SFX_Elcor", new[] { new VisemeMapping("tongueUp", 0.010000f) } },
            { "Sneer", new[] { new VisemeMapping("sneerRight", 0.200000f), new VisemeMapping("sneerLeft", 0.200000f), new VisemeMapping("tongueUp1", 0.190000f), new VisemeMapping("tongueUp2", 0.240000f), new VisemeMapping("tongueUp3", 0.380000f), new VisemeMapping("tongueForward", 0.850000f) } },
            { "Throat", new[] { new VisemeMapping("pucker", 0.530506f), new VisemeMapping("jawOpen", 0.054315f), new VisemeMapping("upperLipCurlIn", 0.593750f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "Throat1", new[] { new VisemeMapping("pucker", 1.000000f), new VisemeMapping("upperLipCurlIn", 1.000000f), new VisemeMapping("lowerLipCurlOut", 0.005952f), new VisemeMapping("lowerLipCurlIn", 0.716518f), new VisemeMapping("noseDown", 0.100000f) } },
        };

        /// <summary>
        /// All viseme animation names used in lip sync for Elcor
        /// </summary>
        public static readonly string[] ElcorVisemes =
        [
            "sneerRight",
            "sneerLeft",
            "O_mouth",
            "upperLipCurlOut",
            "lowerLipCurlOut",
            "smileRight",
            "smileLeft",
            "jawOpen",
            "pucker",
            "upperLipCurlIn",
            "lowerLipCurlIn",
            "noseDown",
            "noseUp",
            "tongueUp",
            "tongueUp1",
            "tongueUp2",
            "tongueUp3",
            "tongueForward",
            "jawForward"
        ];

        /// <summary>
        /// Hanar phoneme to viseme mappings - from SFX_Hannar_FaceFX data.
        /// Note: Hanar use specialized phoneme identifiers in FaceFX mapping.
        /// </summary>
        public static readonly Dictionary<string, VisemeMapping[]> HanarPhonemeMap = new()
        {
            // Hanar phoneme mappings from SFX_Hannar_FaceFX
            { "cheekPuff", new[] { new VisemeMapping("jawForward", 0.010000f) } },
            { "E_Angry_Interested", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 0.300000f), new VisemeMapping("sneerLeft", 0.300000f), new VisemeMapping("jawOpen", 0.020000f) } },
            { "E_Angry_Question", new[] { new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.080357f) } },
            { "E_Angry_Rage", new[] { new VisemeMapping("upperLipCurlIn", 0.760000f), new VisemeMapping("lowerLipCurlIn", 0.760000f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("pucker", 0.170000f), new VisemeMapping("tongueUp1", 0.300000f), new VisemeMapping("tongueUp2", 0.570000f), new VisemeMapping("tongueUp3", 0.690000f), new VisemeMapping("noseDown", 0.210000f) } },
            { "E_Angry_Shocked", new[] { new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f), new VisemeMapping("O_mouth", 0.220000f) } },
            { "E_Angry_Squint", new[] { new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f), new VisemeMapping("O_mouth", 0.300000f) } },
            { "E_flirt", new[] { new VisemeMapping("sneerRight", 0.200000f), new VisemeMapping("sneerLeft", 0.200000f), new VisemeMapping("tongueUp1", 0.190000f), new VisemeMapping("tongueUp2", 0.240000f), new VisemeMapping("tongueUp3", 0.380000f), new VisemeMapping("tongueForward", 0.850000f) } },
            { "E_GESTURE_HeadLeft", new[] { new VisemeMapping("jawOpen", 0.139881f) } },
            { "E_GESTURE_HeadRight", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("jawOpen", 0.098958f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "E_GESTURE_NeckBackLeft", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 0.250000f), new VisemeMapping("sneerLeft", 0.250000f), new VisemeMapping("jawOpen", 0.060000f), new VisemeMapping("jawForward", 0.380000f) } },
            { "E_GESTURE_NeckBackRight", new[] { new VisemeMapping("jawOpen", 0.173363f), new VisemeMapping("noseUp", 0.040000f) } },
            { "E_GESTURE_NeckForwardLeft", new[] { new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.173363f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "E_GESTURE_NeckForwardRight", new[] { new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.173363f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "E_Happy_Diabolical", new[] { new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.173363f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "E_Happy_Dissapointed", new[] { new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.173363f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "E_Happy_Fake", new[] { new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.080357f) } },
            { "E_Happy_Interested", new[] { new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.080357f) } },
            { "E_Happy_OverJoyed", new[] { new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.080357f) } },
            { "E_Happy_Question", new[] { new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.173363f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "E_Neck_Pitch", new[] { new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawOpen", 0.098958f), new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "E_Neck_Yaw", new[] { new VisemeMapping("pucker", 1.000000f), new VisemeMapping("upperLipCurlIn", 1.000000f), new VisemeMapping("lowerLipCurlOut", 0.005952f), new VisemeMapping("lowerLipCurlIn", 0.716518f), new VisemeMapping("noseDown", 0.100000f) } },
            { "Emissive", new[] { new VisemeMapping("upperLipCurlOut", 0.370000f), new VisemeMapping("lowerLipCurlOut", 0.390000f), new VisemeMapping("pucker", 0.900000f), new VisemeMapping("O_mouth", 0.330000f), new VisemeMapping("noseDown", 0.200000f) } },
            { "Emote_Blender", new[] { new VisemeMapping("jawOpen", 0.020000f), new VisemeMapping("O_mouth", 0.260000f), new VisemeMapping("tongueUp1", 0.280000f), new VisemeMapping("tongueUp2", 0.330000f), new VisemeMapping("tongueUp3", 0.400000f), new VisemeMapping("tongueForward", 0.820000f) } },
            { "EmotionBlender", new[] { new VisemeMapping("upperLipCurlOut", 0.500000f), new VisemeMapping("lowerLipCurlOut", 0.520000f), new VisemeMapping("jawOpen", 0.010000f), new VisemeMapping("O_mouth", 0.160000f) } },
            { "Emphasis_Head_Pitch", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 0.150000f), new VisemeMapping("sneerLeft", 0.150000f), new VisemeMapping("jawOpen", 0.120000f) } },
            { "Emphasis_Head_Yaw", new[] { new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.173363f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "FXA_Anim", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 0.260000f), new VisemeMapping("sneerLeft", 0.260000f), new VisemeMapping("jawOpen", 0.091518f) } },
            { "FXA_Group", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 0.210000f), new VisemeMapping("sneerLeft", 0.210000f), new VisemeMapping("jawOpen", 0.084077f) } },
            { "FXA_Path", new[] { new VisemeMapping("tongueUp1", 0.180000f), new VisemeMapping("tongueUp2", 0.270000f), new VisemeMapping("tongueUp3", 0.360000f), new VisemeMapping("tongueForward", 0.890000f) } },
            { "G_WeightShiftLeft", new[] { new VisemeMapping("pucker", 0.530506f), new VisemeMapping("jawOpen", 0.054315f), new VisemeMapping("upperLipCurlIn", 0.593750f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "happy_alarmed", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("pucker", 0.900000f), new VisemeMapping("O_mouth", 0.480000f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("upperLipCurlOut", 0.370000f), new VisemeMapping("lowerLipCurlOut", 0.390000f), new VisemeMapping("noseDown", 0.200000f) } },
            { "head_RX-", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("sneerRight", 0.150000f), new VisemeMapping("sneerLeft", 0.150000f), new VisemeMapping("jawOpen", 0.121280f) } },
            { "head_RX+", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("jawOpen", 0.098958f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "Head_Yaw", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("jawOpen", 0.098958f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "InnerBrowLeft_Out", new[] { new VisemeMapping("smileRight", 0.430000f), new VisemeMapping("smileLeft", 0.430000f), new VisemeMapping("pucker", 1.000000f), new VisemeMapping("upperLipCurlIn", 1.000000f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "InnerBrowRight_Out", new[] { new VisemeMapping("upperLipCurlOut", 0.500000f), new VisemeMapping("lowerLipCurlOut", 0.520000f), new VisemeMapping("jawOpen", 0.010000f), new VisemeMapping("O_mouth", 0.160000f) } },
            { "jawForward", new[] { new VisemeMapping("smileRight", 1.000000f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("jawOpen", 0.125000f) } },
            { "jawRotate", new[] { new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawForward", 0.470000f), new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "jawRotateDown", new[] { new VisemeMapping("jawOpen", 0.010000f) } },
            { "jawRotateUp", new[] { new VisemeMapping("jawOpen", 0.010000f) } },
            { "jawSideLeft", new[] { new VisemeMapping("sneerRight", 0.250000f), new VisemeMapping("sneerLeft", 0.250000f), new VisemeMapping("tongueUp1", 0.510000f), new VisemeMapping("tongueUp2", 0.530000f), new VisemeMapping("tongueUp3", 0.700000f) } },
            { "jawSideRight", new[] { new VisemeMapping("smileRight", 0.690000f), new VisemeMapping("smileLeft", 0.690000f), new VisemeMapping("sneerRight", 0.200000f), new VisemeMapping("sneerLeft", 0.200000f), new VisemeMapping("upperLipCurlIn", 0.420000f), new VisemeMapping("tongueUp", 0.720000f) } },
            { "LipSynch", new[] { new VisemeMapping("pucker", 0.534226f), new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawOpen", 0.039435f), new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "lowerLipCurlIn", new[] { new VisemeMapping("sneerRight", 0.150000f), new VisemeMapping("sneerLeft", 0.150000f), new VisemeMapping("pucker", 1.000000f), new VisemeMapping("jawOpen", 0.046875f), new VisemeMapping("upperLipCurlIn", 1.000000f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "lowerLipDownLeft", new[] { new VisemeMapping("upperLipCurlIn", 0.760000f), new VisemeMapping("lowerLipCurlIn", 0.760000f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("pucker", 0.170000f), new VisemeMapping("tongueUp1", 0.300000f), new VisemeMapping("tongueUp2", 0.570000f), new VisemeMapping("tongueUp3", 0.690000f), new VisemeMapping("noseDown", 0.210000f) } },
            { "lowerLipDownRight", new[] { new VisemeMapping("sneerRight", 0.130000f), new VisemeMapping("sneerLeft", 0.130000f), new VisemeMapping("upperLipCurlOut", 0.370000f), new VisemeMapping("lowerLipCurlOut", 0.230000f), new VisemeMapping("jawOpen", 0.050000f), new VisemeMapping("tongueUp1", 0.390000f), new VisemeMapping("tongueUp2", 0.660000f), new VisemeMapping("tongueUp3", 0.760000f) } },
            { "Material_Slot_Id", new[] { new VisemeMapping("sneerRight", 0.250000f), new VisemeMapping("sneerLeft", 0.250000f), new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "neck_RX-", new[] { new VisemeMapping("smileRight", 0.710000f), new VisemeMapping("smileLeft", 0.710000f), new VisemeMapping("jawOpen", 0.121280f) } },
            { "neck_RX+", new[] { new VisemeMapping("pucker", 1.000000f), new VisemeMapping("O_mouth", 0.392857f), new VisemeMapping("jawOpen", 0.050595f), new VisemeMapping("upperLipCurlOut", 0.870000f), new VisemeMapping("lowerLipCurlOut", 0.850000f) } },
            { "neck_RZ+", new[] { new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawOpen", 0.136161f), new VisemeMapping("upperLipCurlOut", 1.000000f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "neckYaw_sum", new[] { new VisemeMapping("smileRight", 0.710000f), new VisemeMapping("smileLeft", 0.710000f), new VisemeMapping("jawOpen", 0.121280f) } },
            { "Orientation_Head_Pitch", new[] { new VisemeMapping("pucker", 1.000000f), new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawOpen", 0.080357f), new VisemeMapping("upperLipCurlOut", 0.450000f), new VisemeMapping("lowerLipCurlOut", 0.510000f) } },
            { "Orientation_Head_Yaw", new[] { new VisemeMapping("pucker", 1.000000f), new VisemeMapping("upperLipCurlIn", 1.000000f), new VisemeMapping("lowerLipCurlOut", 0.005952f), new VisemeMapping("lowerLipCurlIn", 0.716518f), new VisemeMapping("noseDown", 0.100000f) } },
            { "Parameter_Name", new[] { new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("upperLipCurlOut", 0.350000f), new VisemeMapping("lowerLipCurlOut", 0.390000f), new VisemeMapping("O_mouth", 0.080000f) } },
            { "Red", new[] { new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("upperLipCurlOut", 0.350000f), new VisemeMapping("lowerLipCurlOut", 0.390000f), new VisemeMapping("O_mouth", 0.080000f) } },
            { "S_Angry", new[] { new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.080357f) } },
            { "S_Happy", new[] { new VisemeMapping("O_mouth", 0.380000f), new VisemeMapping("jawOpen", 0.173363f), new VisemeMapping("lowerLipCurlOut", 1.000000f) } },
            { "sad_angry", new[] { new VisemeMapping("jawOpen", 0.010000f) } },
            { "SFX_Hannar", new[] { new VisemeMapping("tongueUp1", 0.010000f) } },
            { "sneerLeft", new[] { new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("upperLipCurlOut", 0.350000f), new VisemeMapping("lowerLipCurlOut", 0.390000f), new VisemeMapping("O_mouth", 0.080000f) } },
            { "Stage_Blender", new[] { new VisemeMapping("pucker", 1.000000f), new VisemeMapping("upperLipCurlIn", 1.000000f), new VisemeMapping("lowerLipCurlOut", 0.005952f), new VisemeMapping("lowerLipCurlIn", 0.716518f), new VisemeMapping("noseDown", 0.100000f) } },
            { "talk_shade", new[] { new VisemeMapping("upperLipCurlOut", 0.370000f), new VisemeMapping("lowerLipCurlOut", 0.390000f), new VisemeMapping("pucker", 0.900000f), new VisemeMapping("O_mouth", 0.330000f), new VisemeMapping("noseDown", 0.200000f) } },
            { "tongueUp", new[] { new VisemeMapping("upperLipCurlIn", 0.760000f), new VisemeMapping("lowerLipCurlIn", 0.760000f), new VisemeMapping("pucker", 0.170000f), new VisemeMapping("noseDown", 0.200000f) } },
            { "upperLipCurlIn", new[] { new VisemeMapping("sneerRight", 0.150000f), new VisemeMapping("sneerLeft", 0.150000f), new VisemeMapping("pucker", 1.000000f), new VisemeMapping("jawOpen", 0.046875f), new VisemeMapping("upperLipCurlIn", 1.000000f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "upperLipCurlOut", new[] { new VisemeMapping("lowerLipCurlOut", 0.120000f), new VisemeMapping("lowerLipCurlIn", 0.130000f), new VisemeMapping("jawOpen", 0.020000f), new VisemeMapping("tongueUp", 0.660000f) } },
        };

        /// <summary>
        /// All viseme animation names used in lip sync for Hanar
        /// </summary>
        public static readonly string[] HanarVisemes =
        [
            "smileRight",
            "smileLeft",
            "sneerRight",
            "sneerLeft",
            "jawOpen",
            "jawForward",
            "O_mouth",
            "upperLipCurlIn",
            "lowerLipCurlIn",
            "upperLipCurlOut",
            "lowerLipCurlOut",
            "pucker",
            "tongueUp",
            "tongueUp1",
            "tongueUp2",
            "tongueUp3",
            "tongueForward",
            "noseDown",
            "noseUp"
        ];

        /// <summary>
        /// Volus phoneme to viseme mappings - from SFX_Volus_FaceFX data.
        /// Note: Volus use bone names as phoneme identifiers in FaceFX mapping.
        /// </summary>
        public static readonly Dictionary<string, VisemeMapping[]> VolusPhonemeMap = new()
        {
            // Volus phoneme mappings from SFX_Volus_FaceFX
            { "cheekPuff", new[] { new VisemeMapping("smileRight", 0.050000f), new VisemeMapping("smileLeft", 0.050000f), new VisemeMapping("frownRight", 0.150000f), new VisemeMapping("frownLeft", 0.150000f), new VisemeMapping("jawOpen", 0.070000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("mouthDownLeft", 0.220000f), new VisemeMapping("mouthDownRight", 0.210000f) } },
            { "Chest", new[] { new VisemeMapping("smileRight", 0.040000f), new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("sneerLeft", 0.120000f), new VisemeMapping("frownRight", 0.250000f), new VisemeMapping("frownLeft", 0.250000f), new VisemeMapping("jawForward", 0.350000f) } },
            { "Chest1", new[] { new VisemeMapping("sneerRight", 0.110000f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("frownRight", 0.340000f), new VisemeMapping("frownLeft", 0.290000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("tongueUp", 1.000000f), new VisemeMapping("mouthDownLeft", 0.210000f), new VisemeMapping("mouthDownRight", 0.200000f) } },
            { "Chest2", new[] { new VisemeMapping("frownRight", 0.370000f), new VisemeMapping("frownLeft", 0.250000f), new VisemeMapping("jawOpen", 0.050000f) } },
            { "Head", new[] { new VisemeMapping("jawOpen", 0.070000f), new VisemeMapping("O_mouth", 0.230000f), new VisemeMapping("jawRotate", 0.030000f), new VisemeMapping("tongueUp", 1.000000f) } },
            { "InnerBrowRight_Out", new[] { new VisemeMapping("jawOpen", 0.060000f), new VisemeMapping("pucker", 0.270000f), new VisemeMapping("O_mouth", 0.310000f), new VisemeMapping("jawRotate", 0.190000f) } },
            { "LeftAnkle", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.150000f), new VisemeMapping("jawOpen", 0.060000f), new VisemeMapping("jawRotate", 0.100000f) } },
            { "LeftCollar", new[] { new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("O_mouth", 0.360000f), new VisemeMapping("jawRotate", 0.110000f) } },
            { "LeftElbow", new[] { new VisemeMapping("sneerRight", 0.070000f), new VisemeMapping("sneerLeft", 0.060000f), new VisemeMapping("upperLipCurlOut", 0.520000f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "LeftElbowTwist1", new[] { new VisemeMapping("frownRight", 0.370000f), new VisemeMapping("frownLeft", 0.370000f), new VisemeMapping("upperLipCurlIn", 0.330000f), new VisemeMapping("lowerLipCurlIn", 0.130000f), new VisemeMapping("pucker", 0.130000f), new VisemeMapping("jawRotate", 0.220000f) } },
            { "LeftElbowTwist2", new[] { new VisemeMapping("frownRight", 0.370000f), new VisemeMapping("frownLeft", 0.370000f), new VisemeMapping("pucker", 0.170000f), new VisemeMapping("jawRotate", 0.190000f) } },
            { "LeftFlap", new[] { new VisemeMapping("frownRight", 0.250000f), new VisemeMapping("frownLeft", 0.350000f), new VisemeMapping("lowerLipCurlIn", 0.260000f), new VisemeMapping("jawOpen", 0.060000f) } },
            { "LeftHip", new[] { new VisemeMapping("pucker", 0.760000f) } },
            { "LeftHipTwist1", new[] { new VisemeMapping("lowerLipCurlOut", 0.270000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("mouthDownLeft", 0.180000f), new VisemeMapping("mouthDownRight", 0.190000f) } },
            { "LeftIndexFinger", new[] { new VisemeMapping("sneerRight", 0.070000f), new VisemeMapping("sneerLeft", 0.070000f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("lowerLipCurlOut", 0.300000f), new VisemeMapping("jawForward", 0.340000f), new VisemeMapping("pucker", 0.250000f), new VisemeMapping("O_mouth", 0.420000f) } },
            { "LeftIndexFinger1", new[] { new VisemeMapping("frownRight", 0.370000f), new VisemeMapping("frownLeft", 0.250000f), new VisemeMapping("jawOpen", 0.050000f) } },
            { "LeftIndexFinger2", new[] { new VisemeMapping("frownRight", 0.370000f), new VisemeMapping("frownLeft", 0.250000f), new VisemeMapping("jawOpen", 0.050000f) } },
            { "LeftKnee", new[] { new VisemeMapping("upperLipCurlOut", 0.220000f), new VisemeMapping("lowerLipCurlOut", 0.510000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("O_mouth", 0.370000f), new VisemeMapping("jawRotate", 0.150000f) } },
            { "LeftPinkFinger", new[] { new VisemeMapping("smileRight", 0.050000f), new VisemeMapping("smileLeft", 0.050000f), new VisemeMapping("sneerRight", 0.180000f), new VisemeMapping("sneerLeft", 0.210000f), new VisemeMapping("frownRight", 0.370000f), new VisemeMapping("frownLeft", 0.350000f), new VisemeMapping("jawForward", 0.020000f) } },
            { "LeftPinkFinger1", new[] { new VisemeMapping("smileRight", 0.060000f), new VisemeMapping("smileLeft", 0.060000f), new VisemeMapping("sneerRight", 0.130000f), new VisemeMapping("sneerLeft", 0.150000f), new VisemeMapping("frownRight", 0.250000f), new VisemeMapping("frownLeft", 0.250000f), new VisemeMapping("jawForward", 0.220000f) } },
            { "LeftPinkFinger2", new[] { new VisemeMapping("sneerRight", 0.120000f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("frownRight", 0.120000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("O_mouth", 0.540000f) } },
            { "LeftShoulder", new[] { new VisemeMapping("jawOpen", 0.005000f) } },
            { "LeftShoulderTwist1", new[] { new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("O_mouth", 0.360000f), new VisemeMapping("jawRotate", 0.110000f) } },
            { "LeftShoulderTwist2", new[] { new VisemeMapping("frownRight", 0.370000f), new VisemeMapping("frownLeft", 0.370000f), new VisemeMapping("jawRotate", 0.100000f) } },
            { "LeftThumbFinger", new[] { new VisemeMapping("sneerRight", 0.060000f), new VisemeMapping("sneerLeft", 0.040000f), new VisemeMapping("upperLipCurlOut", 0.490000f), new VisemeMapping("lowerLipCurlIn", 1.000000f), new VisemeMapping("jawClench", 0.170000f) } },
            { "LeftThumbFinger1", new[] { new VisemeMapping("jawOpen", 0.070000f), new VisemeMapping("O_mouth", 0.310000f), new VisemeMapping("jawRotate", 0.070000f), new VisemeMapping("tongueUp", 1.000000f) } },
            { "LeftThumbFinger2", new[] { new VisemeMapping("upperLipCurlOut", 0.430000f), new VisemeMapping("jawOpen", 0.060000f), new VisemeMapping("O_mouth", 0.160000f), new VisemeMapping("jawRotate", 0.070000f), new VisemeMapping("tongueUp", 1.000000f) } },
            { "LeftToe", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.150000f), new VisemeMapping("jawOpen", 0.060000f), new VisemeMapping("jawRotate", 0.100000f) } },
            { "LeftWrist", new[] { new VisemeMapping("sneerRight", 0.070000f), new VisemeMapping("sneerLeft", 0.060000f), new VisemeMapping("upperLipCurlOut", 0.520000f), new VisemeMapping("lowerLipCurlIn", 1.000000f) } },
            { "LowerBack", new[] { new VisemeMapping("upperLipCurlIn", 0.110000f), new VisemeMapping("lowerLipCurlIn", 0.110000f), new VisemeMapping("jawClench", 0.190000f) } },
            { "lowerLipDownLeft", new[] { new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("O_mouth", 0.520000f), new VisemeMapping("jawRotate", 0.240000f), new VisemeMapping("mouthDownLeft", 0.140000f), new VisemeMapping("mouthDownRight", 0.120000f) } },
            { "lowerLipDownRight", new[] { new VisemeMapping("frownRight", 0.250000f), new VisemeMapping("frownLeft", 0.250000f), new VisemeMapping("jawOpen", 0.010000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("mouthDownLeft", 0.230000f), new VisemeMapping("mouthDownRight", 0.220000f) } },
            { "Neck", new[] { new VisemeMapping("frownRight", 0.370000f), new VisemeMapping("frownLeft", 0.370000f), new VisemeMapping("upperLipCurlIn", 0.330000f), new VisemeMapping("lowerLipCurlIn", 0.130000f), new VisemeMapping("pucker", 0.130000f), new VisemeMapping("jawRotate", 0.220000f) } },
            { "Neck1", new[] { new VisemeMapping("upperLipCurlIn", 0.690000f), new VisemeMapping("lowerLipCurlIn", 0.890000f) } },
            { "Pack", new[] { new VisemeMapping("lowerLipCurlOut", 0.570000f), new VisemeMapping("jawOpen", 0.050000f), new VisemeMapping("pucker", 0.160000f), new VisemeMapping("O_mouth", 0.480000f), new VisemeMapping("jawRotate", 0.140000f) } },
            { "Pelvis", new[] { new VisemeMapping("pucker", 0.760000f) } },
            { "Pouch", new[] { new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("frownRight", 0.250000f), new VisemeMapping("frownLeft", 0.250000f), new VisemeMapping("upperLipCurlIn", 0.190000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("jawClench", 0.280000f) } },
            { "RightAnkle", new[] { new VisemeMapping("frownRight", 0.110000f), new VisemeMapping("frownLeft", 0.110000f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("pucker", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "RightCollar", new[] { new VisemeMapping("frownRight", 0.250000f), new VisemeMapping("frownLeft", 0.150000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("tongueUp", 1.000000f) } },
            { "RightElbow", new[] { new VisemeMapping("frownRight", 0.370000f), new VisemeMapping("frownLeft", 0.370000f), new VisemeMapping("pucker", 0.170000f), new VisemeMapping("jawRotate", 0.190000f) } },
            { "RightElbowTwist1", new[] { new VisemeMapping("frownRight", 0.110000f), new VisemeMapping("frownLeft", 0.110000f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("pucker", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "RightElbowTwist2", new[] { new VisemeMapping("frownRight", 0.110000f), new VisemeMapping("frownLeft", 0.110000f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("pucker", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "RightFlap", new[] { new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("O_mouth", 0.360000f), new VisemeMapping("jawRotate", 0.110000f) } },
            { "RightHip", new[] { new VisemeMapping("frownRight", 0.110000f), new VisemeMapping("frownLeft", 0.110000f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("pucker", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "RightHipTwist1", new[] { new VisemeMapping("smileRight", 0.040000f), new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("frownRight", 0.370000f), new VisemeMapping("frownLeft", 0.370000f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("jawRotate", 0.200000f), new VisemeMapping("mouthDownLeft", 0.230000f), new VisemeMapping("mouthDownRight", 0.210000f) } },
            { "RightIndexFinger", new[] { new VisemeMapping("frownRight", 0.090000f), new VisemeMapping("frownLeft", 0.110000f), new VisemeMapping("jawOpen", 0.070000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "RightIndexFinger1", new[] { new VisemeMapping("frownRight", 0.090000f), new VisemeMapping("frownLeft", 0.110000f), new VisemeMapping("jawOpen", 0.070000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "RightIndexFinger2", new[] { new VisemeMapping("frownRight", 0.090000f), new VisemeMapping("frownLeft", 0.110000f), new VisemeMapping("jawOpen", 0.070000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "RightKnee", new[] { new VisemeMapping("frownRight", 0.110000f), new VisemeMapping("frownLeft", 0.110000f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("pucker", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "RightPinkFinger", new[] { new VisemeMapping("frownRight", 0.090000f), new VisemeMapping("frownLeft", 0.110000f), new VisemeMapping("jawOpen", 0.070000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "RightPinkFinger1", new[] { new VisemeMapping("frownRight", 0.090000f), new VisemeMapping("frownLeft", 0.110000f), new VisemeMapping("jawOpen", 0.070000f), new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "RightPinkFinger2", new[] { new VisemeMapping("smileRight", 0.070000f), new VisemeMapping("smileLeft", 0.070000f), new VisemeMapping("sneerRight", 0.120000f), new VisemeMapping("sneerLeft", 0.110000f), new VisemeMapping("frownRight", 0.250000f), new VisemeMapping("frownLeft", 0.370000f), new VisemeMapping("jawRotate", 0.250000f) } },
            { "RightShoulder", new[] { new VisemeMapping("pucker", 0.760000f) } },
            { "RightShoulderTwist1", new[] { new VisemeMapping("frownRight", 0.110000f), new VisemeMapping("frownLeft", 0.110000f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("pucker", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "RightShoulderTwist2", new[] { new VisemeMapping("frownRight", 0.110000f), new VisemeMapping("frownLeft", 0.110000f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("pucker", 0.100000f), new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f) } },
            { "RightThumbFinger", new[] { new VisemeMapping("sneerRight", 0.090000f), new VisemeMapping("sneerLeft", 0.090000f), new VisemeMapping("frownRight", 0.150000f), new VisemeMapping("frownLeft", 0.070000f), new VisemeMapping("pucker", 0.120000f), new VisemeMapping("O_mouth", 0.270000f) } },
            { "RightThumbFinger1", new[] { new VisemeMapping("sneerRight", 0.130000f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("frownRight", 0.130000f), new VisemeMapping("frownLeft", 0.150000f), new VisemeMapping("lowerLipCurlOut", 0.160000f), new VisemeMapping("jawForward", 0.340000f), new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("mouthDownLeft", 0.070000f), new VisemeMapping("mouthDownRight", 0.060000f) } },
            { "RightThumbFinger2", new[] { new VisemeMapping("smileRight", 0.020000f), new VisemeMapping("smileLeft", 0.020000f), new VisemeMapping("sneerRight", 0.080000f), new VisemeMapping("sneerLeft", 0.090000f), new VisemeMapping("frownRight", 0.370000f), new VisemeMapping("frownLeft", 0.370000f), new VisemeMapping("jawOpen", 0.020000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("mouthDownLeft", 0.210000f), new VisemeMapping("mouthDownRight", 0.220000f) } },
            { "RightToe", new[] { new VisemeMapping("jawOpen", 0.090000f), new VisemeMapping("O_mouth", 0.170000f), new VisemeMapping("jawRotate", 0.190000f), new VisemeMapping("mouthDownLeft", 0.160000f), new VisemeMapping("mouthDownRight", 0.150000f) } },
            { "RightWrist", new[] { new VisemeMapping("smileRight", 0.040000f), new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("sneerLeft", 0.120000f), new VisemeMapping("frownRight", 0.250000f), new VisemeMapping("frownLeft", 0.250000f), new VisemeMapping("jawForward", 0.350000f) } },
            { "Root", new[] { new VisemeMapping("upperLipCurlIn", 0.330000f), new VisemeMapping("lowerLipCurlIn", 0.420000f), new VisemeMapping("pucker", 0.270000f), new VisemeMapping("jawClench", 0.240000f) } },
            { "SFX_Volus", new[] { new VisemeMapping("jawClench", 0.010000f) } },
            { "sneerLeft", new[] { new VisemeMapping("jawOpen", 0.080000f), new VisemeMapping("O_mouth", 0.440000f), new VisemeMapping("jawRotate", 0.210000f) } },
            { "Stick", new[] { new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("frownRight", 0.250000f), new VisemeMapping("frownLeft", 0.250000f), new VisemeMapping("upperLipCurlIn", 0.190000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("jawClench", 0.280000f) } },
            { "tongueUp", new[] { new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("frownRight", 0.250000f), new VisemeMapping("frownLeft", 0.250000f), new VisemeMapping("upperLipCurlIn", 0.190000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("jawClench", 0.280000f) } },
        };

        /// <summary>
        /// All viseme animation names used in lip sync for Volus
        /// </summary>
        public static readonly string[] VolusVisemes =
        [
            "smileRight",
            "smileLeft",
            "sneerRight",
            "sneerLeft",
            "frownRight",
            "frownLeft",
            "jawOpen",
            "jawRotate",
            "jawForward",
            "jawClench",
            "mouthDownLeft",
            "mouthDownRight",
            "O_mouth",
            "pucker",
            "upperLipCurlIn",
            "lowerLipCurlIn",
            "upperLipCurlOut",
            "lowerLipCurlOut",
            "tongueUp"
        ];

        /// <summary>
        /// Batarian phoneme to viseme mappings - from SFX_Batarian_FaceFX data.
        /// Note: Batarian use bone and facial feature names as phoneme identifiers in FaceFX mapping.
        /// </summary>
        public static readonly Dictionary<string, VisemeMapping[]> BatarianPhonemeMap = new()
        {
            // Batarian phoneme mappings from SFX_Batarian_FaceFX
            { "brow_left", new[] { new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("O_mouth", 0.200000f) } },
            { "brow_right", new[] { new VisemeMapping("jawOpen", 0.005000f) } },
            { "cheek_left", new[] { new VisemeMapping("smileRight", 0.060000f), new VisemeMapping("smileLeft", 0.060000f), new VisemeMapping("frownRight", 0.594429f), new VisemeMapping("frownLeft", 0.587845f), new VisemeMapping("jawOpen", 0.035714f), new VisemeMapping("sneerLeft", 0.150000f), new VisemeMapping("sneerRight", 0.130000f), new VisemeMapping("jawForward", 0.220000f), new VisemeMapping("jawRotateUp", 0.289409f) } },
            { "cheek_right", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("O_mouth", 0.541667f), new VisemeMapping("lowerLipUpLeft", 0.143088f), new VisemeMapping("lowerLipUpRight", 0.192446f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("sneerRight", 0.120000f) } },
            { "Chest", new[] { new VisemeMapping("frownRight", 0.579785f), new VisemeMapping("frownLeft", 0.579850f), new VisemeMapping("mouthDownLeft", 0.210000f), new VisemeMapping("mouthDownRight", 0.200000f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("sneerRight", 0.110000f), new VisemeMapping("jawRotateDown", 0.500000f), new VisemeMapping("tongueUP", 1.000000f) } },
            { "Chest1", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawOpen", 0.050000f) } },
            { "Chest2", new[] { new VisemeMapping("frownRight", 0.674305f), new VisemeMapping("frownLeft", 0.677114f), new VisemeMapping("sneerLeft", 0.038809f), new VisemeMapping("sneerRight", 0.074519f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "E_GESTURE_NeckBackLeft", new[] { new VisemeMapping("O_mouth", 0.277530f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "E_GESTURE_NeckBackRight", new[] { new VisemeMapping("O_mouth", 0.344494f), new VisemeMapping("jawOpen", 0.030000f) } },
            { "E_GESTURE_NeckForwardLeft", new[] { new VisemeMapping("frownRight", 0.499908f), new VisemeMapping("frownLeft", 0.498576f), new VisemeMapping("mouthDownLeft", 0.230000f), new VisemeMapping("mouthDownRight", 0.220000f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "E_GESTURE_NeckForwardRight", new[] { new VisemeMapping("mouthDownLeft", 0.140000f), new VisemeMapping("mouthDownRight", 0.120000f), new VisemeMapping("O_mouth", 0.314732f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "eye_Left", new[] { new VisemeMapping("frownRight", 0.320186f), new VisemeMapping("frownLeft", 0.322702f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("O_mouth", 0.050595f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "eye_Left_RX-", new[] { new VisemeMapping("frownRight", 0.342817f), new VisemeMapping("frownLeft", 0.342688f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("lowerLipDownLeft", 0.199389f), new VisemeMapping("lowerLipDownRight", 0.228156f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "eye_Left_RX+", new[] { new VisemeMapping("frownRight", 0.253622f), new VisemeMapping("frownLeft", 0.260081f), new VisemeMapping("mouthDownLeft", 0.180000f), new VisemeMapping("mouthDownRight", 0.190000f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("lowerLipDownLeft", 0.177067f), new VisemeMapping("lowerLipDownRight", 0.172352f), new VisemeMapping("jawRotateDown", 0.400000f), new VisemeMapping("lowerLipCurlOut", 0.270000f) } },
            { "eye_Right", new[] { new VisemeMapping("frownRight", 0.764832f), new VisemeMapping("frownLeft", 0.766383f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "eye_Right_RX-", new[] { new VisemeMapping("frownRight", 0.342817f), new VisemeMapping("frownLeft", 0.342688f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("lowerLipDownLeft", 0.199389f), new VisemeMapping("lowerLipDownRight", 0.228156f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "eye_Right_RX+", new[] { new VisemeMapping("frownRight", 0.342817f), new VisemeMapping("frownLeft", 0.342688f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("lowerLipDownLeft", 0.199389f), new VisemeMapping("lowerLipDownRight", 0.228156f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "eyeBlink_Left", new[] { new VisemeMapping("frownRight", 0.320186f), new VisemeMapping("frownLeft", 0.322702f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("O_mouth", 0.050595f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "eyeBlink_Right", new[] { new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("sneerLeft", 0.169017f), new VisemeMapping("sneerRight", 0.160084f), new VisemeMapping("jawRotateDown", 0.400000f), new VisemeMapping("lowerLipCurlIn", 0.953105f) } },
            { "Gaze_Eye_Yaw", new[] { new VisemeMapping("smileRight", 0.050000f), new VisemeMapping("smileLeft", 0.050000f), new VisemeMapping("frownRight", 0.270000f), new VisemeMapping("frownLeft", 0.270000f), new VisemeMapping("mouthDownLeft", 0.220000f), new VisemeMapping("mouthDownRight", 0.210000f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("lowerLipDownLeft", 0.177067f), new VisemeMapping("lowerLipDownRight", 0.153751f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "GOD", new[] { new VisemeMapping("upperLipCurlIn", 0.330000f), new VisemeMapping("lowerLipCurlIn", 0.420000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("jawClench", 0.240000f) } },
            { "Head", new[] { new VisemeMapping("frownRight", 0.436007f), new VisemeMapping("frownLeft", 0.459937f), new VisemeMapping("O_mouth", 0.132440f), new VisemeMapping("jawOpen", 0.060000f), new VisemeMapping("jawRotateDown", 0.400000f), new VisemeMapping("lowerLipCurlIn", 0.260000f) } },
            { "HeadBase", new[] { new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("O_mouth", 0.200000f) } },
            { "innerLowLip_left", new[] { new VisemeMapping("frownRight", 0.378762f), new VisemeMapping("frownLeft", 0.375997f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("sneerLeft", 0.027648f), new VisemeMapping("sneerRight", 0.033596f), new VisemeMapping("jawRotateDown", 0.400000f), new VisemeMapping("tongueUP", 1.000000f) } },
            { "innerLowLip_right", new[] { new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("O_mouth", 0.200000f) } },
            { "innerUpperLip_left", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("O_mouth", 0.277530f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipUpLeft", 0.131927f), new VisemeMapping("lowerLipUpRight", 0.140363f), new VisemeMapping("sneerLeft", 0.070000f), new VisemeMapping("sneerRight", 0.070000f), new VisemeMapping("jawForward", 0.340000f), new VisemeMapping("lowerLipCurlOut", 0.300000f) } },
            { "innerUpperLip_right", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawOpen", 0.050000f) } },
            { "jawBack", new[] { new VisemeMapping("mouthDownLeft", 0.160000f), new VisemeMapping("mouthDownRight", 0.150000f), new VisemeMapping("O_mouth", 0.100000f), new VisemeMapping("jawOpen", 0.100000f) } },
            { "jawBone", new[] { new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("pucker", 0.050000f) } },
            { "jawRotateDown", new[] { new VisemeMapping("frownRight", 0.505233f), new VisemeMapping("frownLeft", 0.503905f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "jawRotateUp", new[] { new VisemeMapping("pucker", 0.300000f) } },
            { "jawSideLeft", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("mouthDownLeft", 0.225446f), new VisemeMapping("mouthDownRight", 0.240327f), new VisemeMapping("jawOpen", 0.100000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("jawRotateDown", 0.400000f), new VisemeMapping("lowerLipCurlOut", 0.180000f) } },
            { "jawSideRight", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("mouthDownLeft", 0.225446f), new VisemeMapping("mouthDownRight", 0.240327f), new VisemeMapping("jawOpen", 0.100000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("jawRotateDown", 0.400000f), new VisemeMapping("lowerLipCurlOut", 0.180000f) } },
            { "LipCorner_left", new[] { new VisemeMapping("smileRight", 0.019052f), new VisemeMapping("smileLeft", 0.020000f), new VisemeMapping("frownRight", 0.445326f), new VisemeMapping("frownLeft", 0.443948f), new VisemeMapping("mouthDownLeft", 0.210000f), new VisemeMapping("mouthDownRight", 0.220000f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("sneerLeft", 0.098333f), new VisemeMapping("sneerRight", 0.096840f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "LipCorner_right", new[] { new VisemeMapping("frownRight", 0.320186f), new VisemeMapping("frownLeft", 0.322702f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("O_mouth", 0.050595f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "LowerBack", new[] { new VisemeMapping("smileRight", 0.040000f), new VisemeMapping("smileLeft", 0.053577f), new VisemeMapping("frownRight", 0.670312f), new VisemeMapping("frownLeft", 0.675781f), new VisemeMapping("sneerLeft", 0.120000f), new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("jawForward", 0.350000f), new VisemeMapping("jawRotateUp", 0.337772f) } },
            { "LowerCheek_left", new[] { new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("pucker", 0.050000f) } },
            { "lowerCheek_right", new[] { new VisemeMapping("smileRight", 0.040000f), new VisemeMapping("smileLeft", 0.053577f), new VisemeMapping("frownRight", 0.670312f), new VisemeMapping("frownLeft", 0.675781f), new VisemeMapping("sneerLeft", 0.120000f), new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("jawForward", 0.350000f), new VisemeMapping("jawRotateUp", 0.337772f) } },
            { "lowerLip_left", new[] { new VisemeMapping("pucker", 0.300000f) } },
            { "lowerLip_right", new[] { new VisemeMapping("frownRight", 0.483933f), new VisemeMapping("frownLeft", 0.499908f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "lowerLipDownLeft", new[] { new VisemeMapping("pucker", 0.300000f) } },
            { "lowerLipDownRight", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("mouthDownLeft", 0.225446f), new VisemeMapping("mouthDownRight", 0.240327f), new VisemeMapping("jawOpen", 0.100000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("jawRotateDown", 0.400000f), new VisemeMapping("lowerLipCurlOut", 0.180000f) } },
            { "lowLid_Left", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("mouthDownLeft", 0.225446f), new VisemeMapping("mouthDownRight", 0.240327f), new VisemeMapping("jawOpen", 0.100000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("jawRotateDown", 0.400000f), new VisemeMapping("lowerLipCurlOut", 0.180000f) } },
            { "lowLid_Right", new[] { new VisemeMapping("frownRight", 0.320186f), new VisemeMapping("frownLeft", 0.322702f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("O_mouth", 0.050595f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "MouthBase", new[] { new VisemeMapping("smileRight", 0.036909f), new VisemeMapping("smileLeft", 0.050000f), new VisemeMapping("frownRight", 0.562478f), new VisemeMapping("frownLeft", 0.561197f), new VisemeMapping("sneerLeft", 0.210000f), new VisemeMapping("sneerRight", 0.180000f), new VisemeMapping("jawForward", 0.020000f), new VisemeMapping("jawRotateUp", 0.315451f) } },
            { "Neck", new[] { new VisemeMapping("upperLipCurlIn", 0.690000f), new VisemeMapping("lowerLipCurlIn", 0.890000f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "Neck1", new[] { new VisemeMapping("O_mouth", 0.470982f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("lowerLipUpLeft", 0.143088f), new VisemeMapping("lowerLipUpRight", 0.144083f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "outBrow_left", new[] { new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("sneerLeft", 0.169017f), new VisemeMapping("sneerRight", 0.160084f), new VisemeMapping("jawRotateDown", 0.400000f), new VisemeMapping("lowerLipCurlIn", 0.953105f) } },
            { "outBrow_Right", new[] { new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("sneerLeft", 0.113214f), new VisemeMapping("sneerRight", 0.130322f), new VisemeMapping("jawRotateDown", 0.400000f), new VisemeMapping("lowerLipCurlIn", 0.770813f) } },
            { "outerUpperLip_left", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("mouthDownLeft", 0.070000f), new VisemeMapping("mouthDownRight", 0.060000f), new VisemeMapping("O_mouth", 0.337054f), new VisemeMapping("lowerLipUpLeft", 0.105886f), new VisemeMapping("lowerLipUpRight", 0.110601f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("sneerRight", 0.130000f), new VisemeMapping("jawForward", 0.340000f), new VisemeMapping("lowerLipCurlOut", 0.160000f) } },
            { "outerUpperLip_right", new[] { new VisemeMapping("frownRight", 0.320186f), new VisemeMapping("frownLeft", 0.322702f), new VisemeMapping("mouthDownLeft", 0.170000f), new VisemeMapping("mouthDownRight", 0.140000f), new VisemeMapping("O_mouth", 0.050595f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "Root", new[] { new VisemeMapping("upperLipCurlIn", 0.110000f), new VisemeMapping("lowerLipCurlIn", 0.110000f), new VisemeMapping("jawClench", 0.190000f) } },
            { "SFX_Batarian", new[] { new VisemeMapping("jawClench", 0.010000f) } },
            { "smileOmouthLeft", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("mouthDownLeft", 0.225446f), new VisemeMapping("mouthDownRight", 0.240327f), new VisemeMapping("jawOpen", 0.100000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("jawRotateDown", 0.400000f), new VisemeMapping("lowerLipCurlOut", 0.180000f) } },
            { "smileOmouthRight", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("mouthDownLeft", 0.225446f), new VisemeMapping("mouthDownRight", 0.240327f), new VisemeMapping("jawOpen", 0.100000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("jawRotateDown", 0.400000f), new VisemeMapping("lowerLipCurlOut", 0.180000f) } },
            { "Sneer", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("mouthDownLeft", 0.225446f), new VisemeMapping("mouthDownRight", 0.240327f), new VisemeMapping("jawOpen", 0.100000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("jawRotateDown", 0.400000f), new VisemeMapping("lowerLipCurlOut", 0.180000f) } },
            { "Tongue", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.070000f), new VisemeMapping("O_mouth", 0.344494f), new VisemeMapping("lowerLipUpLeft", 0.076124f), new VisemeMapping("lowerLipUpRight", 0.080839f), new VisemeMapping("sneerLeft", 0.090000f), new VisemeMapping("sneerRight", 0.090000f) } },
            { "tongueUP", new[] { new VisemeMapping("O_mouth", 0.404018f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipUpLeft", 0.157969f), new VisemeMapping("lowerLipUpRight", 0.114321f), new VisemeMapping("lowerLipCurlOut", 0.570000f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "underEye_left", new[] { new VisemeMapping("O_mouth", 0.200000f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("jawRotateDown", 0.400000f), new VisemeMapping("tongueUP", 1.000000f) } },
            { "underEye_Right", new[] { new VisemeMapping("frownRight", 0.171082f), new VisemeMapping("frownLeft", 0.174809f), new VisemeMapping("O_mouth", 0.173363f), new VisemeMapping("jawOpen", 0.061756f), new VisemeMapping("lowerLipDownLeft", 0.132425f), new VisemeMapping("lowerLipDownRight", 0.142590f), new VisemeMapping("jawRotateDown", 0.304290f) } },
            { "unner_InnerBrowRight_Out", new[] { new VisemeMapping("jawOpen", 0.139881f) } },
            { "upper_InnerBrowLeft_Out", new[] { new VisemeMapping("jawOpen", 0.139881f) } },
            { "upperLip_left", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawOpen", 0.050000f) } },
            { "upperLip_right", new[] { new VisemeMapping("frownRight", 0.674305f), new VisemeMapping("frownLeft", 0.677114f), new VisemeMapping("sneerLeft", 0.038809f), new VisemeMapping("sneerRight", 0.074519f), new VisemeMapping("jawRotateDown", 0.400000f) } },
            { "upperLipCurlOut", new[] { new VisemeMapping("O_mouth", 0.262649f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("jawRotateDown", 0.400000f), new VisemeMapping("upperLipCurlOut", 0.220000f), new VisemeMapping("lowerLipCurlOut", 0.510000f) } },
        };

        /// <summary>
        /// All viseme animation names used in lip sync for Batarian
        /// </summary>
        public static readonly string[] BatarianVisemes =
        [
            "smileRight",
            "smileLeft",
            "frownRight",
            "frownLeft",
            "sneerRight",
            "sneerLeft",
            "jawOpen",
            "jawForward",
            "jawRotateUp",
            "jawRotateDown",
            "jawClench",
            "mouthDownLeft",
            "mouthDownRight",
            "O_mouth",
            "pucker",
            "upperLipCurlIn",
            "lowerLipCurlIn",
            "upperLipCurlOut",
            "lowerLipCurlOut",
            "lowerLipUpLeft",
            "lowerLipUpRight",
            "lowerLipDownLeft",
            "lowerLipDownRight",
            "tongueUP"
        ];

        /// <summary>
        /// Vorcha phoneme to viseme mappings - from SFX_AlienB_FaceFX data.
        /// Note: Vorcha use bone and facial feature names as phoneme identifiers in FaceFX mapping.
        /// </summary>
        public static readonly Dictionary<string, VisemeMapping[]> VorchaPhonemeMap = new()
        {
            // Vorcha phoneme mappings from SFX_AlienB_FaceFX
            { "brow_left", new[] { new VisemeMapping("jawRotate", 0.309038f), new VisemeMapping("mouthDownLeft", 0.372206f), new VisemeMapping("mouthDownRight", 0.391642f), new VisemeMapping("O_mouth", 0.799806f), new VisemeMapping("jawOpen", 0.040000f) } },
            { "brow_right", new[] { new VisemeMapping("jawOpen", 0.010000f) } },
            { "cheek_left", new[] { new VisemeMapping("smileRight", 0.483965f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("frownRight", 0.192420f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("mouthDownLeft", 0.610301f), new VisemeMapping("mouthDownRight", 0.712342f), new VisemeMapping("sneerLeft", 0.150000f), new VisemeMapping("sneerRight", 0.130000f), new VisemeMapping("jawForward", 0.220000f), new VisemeMapping("noseDown", 0.177843f), new VisemeMapping("smileJawClench", 0.197279f) } },
            { "cheek_right", new[] { new VisemeMapping("cheekLeft", 0.417208f), new VisemeMapping("cheekRight", 0.215909f), new VisemeMapping("smileRight", 0.702922f), new VisemeMapping("jawClench", 0.326576f), new VisemeMapping("smileLeft", 0.605519f), new VisemeMapping("frownRight", 0.858115f), new VisemeMapping("frownLeft", 0.832792f), new VisemeMapping("mouthDownLeft", 0.425656f), new VisemeMapping("mouthDownRight", 0.454810f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("sneerRight", 0.120000f), new VisemeMapping("jawForward", 0.255588f), new VisemeMapping("noseUp", 0.241011f) } },
            { "Chest", new[] { new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("mouthDownLeft", 0.210000f), new VisemeMapping("mouthDownRight", 0.200000f), new VisemeMapping("O_mouth", 0.581147f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("sneerRight", 0.110000f), new VisemeMapping("tongueUP", 1.000000f) } },
            { "Chest1", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawOpen", 0.050000f) } },
            { "Chest2", new[] { new VisemeMapping("jawRotate", 0.478470f), new VisemeMapping("frownRight", 0.348214f), new VisemeMapping("frownLeft", 0.340774f), new VisemeMapping("jawOpen", 0.039588f), new VisemeMapping("sneerLeft", 0.087798f), new VisemeMapping("sneerRight", 0.113839f), new VisemeMapping("jawRotateUp", 0.229167f), new VisemeMapping("upperLipCurlIn", 0.330000f), new VisemeMapping("lowerLipCurlIn", 0.046875f) } },
            { "eye_Left", new[] { new VisemeMapping("jawRotate", 0.347911f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("mouthDownLeft", 0.430515f), new VisemeMapping("mouthDownRight", 0.479106f), new VisemeMapping("O_mouth", 0.372206f), new VisemeMapping("jawOpen", 0.128251f), new VisemeMapping("jawForward", 0.571429f), new VisemeMapping("noseDown", 0.226433f) } },
            { "eye_Right", new[] { new VisemeMapping("jawRotate", 0.250729f), new VisemeMapping("smileRight", 0.654033f), new VisemeMapping("smileLeft", 0.192420f), new VisemeMapping("frownRight", 0.206997f), new VisemeMapping("frownLeft", 0.454810f), new VisemeMapping("mouthDownLeft", 0.508260f), new VisemeMapping("mouthDownRight", 0.508260f), new VisemeMapping("O_mouth", 0.415938f), new VisemeMapping("sneerLeft", 0.110000f), new VisemeMapping("sneerRight", 0.120000f), new VisemeMapping("jawForward", 0.615160f), new VisemeMapping("noseDown", 0.270165f) } },
            { "eyeBlink_Left", new[] { new VisemeMapping("jawRotate", 0.347911f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("mouthDownLeft", 0.430515f), new VisemeMapping("mouthDownRight", 0.479106f), new VisemeMapping("O_mouth", 0.372206f), new VisemeMapping("jawOpen", 0.128251f), new VisemeMapping("jawForward", 0.571429f), new VisemeMapping("noseDown", 0.226433f) } },
            { "eyeBlink_Right", new[] { new VisemeMapping("jawClench", 0.624763f), new VisemeMapping("smileLeft", 0.177843f), new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawOpen", 0.066187f), new VisemeMapping("pucker", 1.000000f), new VisemeMapping("sneerLeft", 0.362013f), new VisemeMapping("sneerRight", 0.394480f), new VisemeMapping("upperLipCurlOut", 0.454811f), new VisemeMapping("lowerLipCurlIn", 1.000000f), new VisemeMapping("noseUp", 0.182702f) } },
            { "GOD", new[] { new VisemeMapping("jawClench", 1.000000f), new VisemeMapping("frownRight", 0.241011f), new VisemeMapping("frownLeft", 0.221574f), new VisemeMapping("mouthDownLeft", 0.255588f), new VisemeMapping("mouthDownRight", 0.211856f), new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawOpen", 0.066084f), new VisemeMapping("pucker", 1.000000f), new VisemeMapping("jawSideRight", 0.148688f), new VisemeMapping("upperLipCurlIn", 0.330000f), new VisemeMapping("lowerLipCurlIn", 0.420000f) } },
            { "Head", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawOpen", 0.060000f), new VisemeMapping("lowerLipCurlIn", 0.260000f), new VisemeMapping("noseUp", 0.236152f) } },
            { "HeadBase", new[] { new VisemeMapping("jawRotate", 0.309038f), new VisemeMapping("mouthDownLeft", 0.372206f), new VisemeMapping("mouthDownRight", 0.391642f), new VisemeMapping("O_mouth", 0.799806f), new VisemeMapping("jawOpen", 0.040000f) } },
            { "innerLowLip_left", new[] { new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("noseDown", 0.192420f), new VisemeMapping("tongueUP", 1.000000f) } },
            { "innerLowLip_right", new[] { new VisemeMapping("jawRotate", 0.309038f), new VisemeMapping("mouthDownLeft", 0.372206f), new VisemeMapping("mouthDownRight", 0.391642f), new VisemeMapping("O_mouth", 0.799806f), new VisemeMapping("jawOpen", 0.040000f) } },
            { "innerUpperLip_left", new[] { new VisemeMapping("sneerRight", 0.070000f), new VisemeMapping("sneerLeft", 0.070000f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("lowerLipCurlOut", 0.300000f), new VisemeMapping("jawForward", 0.340000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("O_mouth", 0.200000f) } },
            { "innerUpperLip_right", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawOpen", 0.050000f) } },
            { "jawBone", new[] { new VisemeMapping("jawRotate", 0.190000f), new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("jawOpen", 0.044021f), new VisemeMapping("pucker", 0.050000f) } },
            { "LeftCollar", new[] { new VisemeMapping("jawRotate", 0.208048f), new VisemeMapping("smileRight", 0.366815f), new VisemeMapping("cheekPuff", 0.846726f), new VisemeMapping("smileLeft", 0.430059f), new VisemeMapping("frownRight", 0.474702f), new VisemeMapping("frownLeft", 0.489583f), new VisemeMapping("mouthDownLeft", 0.325893f), new VisemeMapping("mouthDownRight", 0.288690f), new VisemeMapping("jawOpen", 0.185882f), new VisemeMapping("pucker", 0.590030f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("noseDown", 0.245870f) } },
            { "LeftElbow", new[] { new VisemeMapping("jawRotate", 0.140000f), new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.050000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("lowerLipCurlOut", 0.570000f), new VisemeMapping("noseUp", 0.216715f) } },
            { "LeftElbowTwist1", new[] { new VisemeMapping("jawRotate", 0.190000f), new VisemeMapping("frownRight", 0.527697f), new VisemeMapping("frownLeft", 0.483965f), new VisemeMapping("mouthDownLeft", 0.790087f), new VisemeMapping("mouthDownRight", 0.775510f), new VisemeMapping("O_mouth", 0.216715f), new VisemeMapping("jawOpen", 0.079486f), new VisemeMapping("pucker", 0.163265f), new VisemeMapping("jawForward", 0.007775f), new VisemeMapping("noseUp", 0.206997f) } },
            { "LeftIndexFinger", new[] { new VisemeMapping("jawRotate", 0.284742f), new VisemeMapping("frownRight", 0.454810f), new VisemeMapping("frownLeft", 0.493683f), new VisemeMapping("O_mouth", 0.556851f), new VisemeMapping("jawOpen", 0.066084f), new VisemeMapping("jawForward", 0.168124f), new VisemeMapping("upperLipCurlOut", 0.220000f), new VisemeMapping("lowerLipCurlOut", 0.510000f), new VisemeMapping("noseDown", 0.221575f) } },
            { "LeftIndexFinger1", new[] { new VisemeMapping("jawRotate", 0.686828f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f) } },
            { "LeftIndexFinger2", new[] { new VisemeMapping("jawRotate", 0.686828f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f) } },
            { "LeftMiddleFinger", new[] { new VisemeMapping("jawRotate", 0.060000f), new VisemeMapping("mouthDownLeft", 0.180000f), new VisemeMapping("mouthDownRight", 0.190000f), new VisemeMapping("jawOpen", 0.247946f), new VisemeMapping("lowerLipCurlOut", 0.270000f), new VisemeMapping("noseDown", 0.138970f) } },
            { "LeftMiddleFinger1", new[] { new VisemeMapping("jawRotate", 0.474247f), new VisemeMapping("jawClench", 0.280000f), new VisemeMapping("smileLeft", 0.095238f), new VisemeMapping("frownRight", 0.726919f), new VisemeMapping("frownLeft", 0.824101f), new VisemeMapping("O_mouth", 0.265306f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("upperLipCurlIn", 0.190000f), new VisemeMapping("noseDown", 0.197279f) } },
            { "LeftMiddleFinger2", new[] { new VisemeMapping("jawRotate", 0.474247f), new VisemeMapping("jawClench", 0.280000f), new VisemeMapping("smileLeft", 0.095238f), new VisemeMapping("frownRight", 0.726919f), new VisemeMapping("frownLeft", 0.824101f), new VisemeMapping("O_mouth", 0.265306f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("upperLipCurlIn", 0.190000f), new VisemeMapping("noseDown", 0.197279f) } },
            { "LeftPinkFinger", new[] { new VisemeMapping("jawRotate", 0.190000f), new VisemeMapping("mouthDownLeft", 0.160000f), new VisemeMapping("mouthDownRight", 0.150000f), new VisemeMapping("O_mouth", 0.100000f), new VisemeMapping("jawOpen", 0.090000f), new VisemeMapping("noseUp", 0.265306f) } },
            { "LeftPinkFinger1", new[] { new VisemeMapping("jawRotate", 0.200000f), new VisemeMapping("smileRight", 0.040000f), new VisemeMapping("smileLeft", 0.040000f), new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("mouthDownLeft", 0.230000f), new VisemeMapping("mouthDownRight", 0.210000f), new VisemeMapping("jawOpen", 0.030000f), new VisemeMapping("pucker", 0.736637f), new VisemeMapping("sneerLeft", 0.581147f), new VisemeMapping("sneerRight", 0.571429f), new VisemeMapping("noseDown", 0.270165f) } },
            { "LeftPinkFinger2", new[] { new VisemeMapping("pucker", 0.300000f) } },
            { "LeftRingFinger", new[] { new VisemeMapping("jawRotate", 0.474247f), new VisemeMapping("jawClench", 0.280000f), new VisemeMapping("smileLeft", 0.095238f), new VisemeMapping("frownRight", 0.726919f), new VisemeMapping("frownLeft", 0.824101f), new VisemeMapping("O_mouth", 0.265306f), new VisemeMapping("jawOpen", 0.040000f), new VisemeMapping("upperLipCurlIn", 0.190000f), new VisemeMapping("noseDown", 0.197279f) } },
            { "LeftRingFinger1", new[] { new VisemeMapping("jawRotate", 0.256812f), new VisemeMapping("smileRight", 0.050000f), new VisemeMapping("smileLeft", 0.050000f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("mouthDownLeft", 0.220000f), new VisemeMapping("mouthDownRight", 0.210000f), new VisemeMapping("jawOpen", 0.199181f), new VisemeMapping("noseUp", 0.187561f) } },
            { "LeftRingFinger2", new[] { new VisemeMapping("jawRotate", 0.240000f), new VisemeMapping("mouthDownLeft", 0.140000f), new VisemeMapping("mouthDownRight", 0.120000f), new VisemeMapping("O_mouth", 0.300000f), new VisemeMapping("jawOpen", 0.181449f), new VisemeMapping("jawForward", 0.547133f) } },
            { "LeftShoulder", new[] { new VisemeMapping("jawRotate", 0.208048f), new VisemeMapping("smileRight", 0.366815f), new VisemeMapping("cheekPuff", 0.846726f), new VisemeMapping("smileLeft", 0.430059f), new VisemeMapping("frownRight", 0.474702f), new VisemeMapping("frownLeft", 0.489583f), new VisemeMapping("mouthDownLeft", 0.325893f), new VisemeMapping("mouthDownRight", 0.288690f), new VisemeMapping("jawOpen", 0.185882f), new VisemeMapping("pucker", 0.590030f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("noseDown", 0.245870f) } },
            { "LeftThumbFinger", new[] { new VisemeMapping("jawRotate", 0.208048f), new VisemeMapping("smileRight", 0.366815f), new VisemeMapping("cheekPuff", 0.846726f), new VisemeMapping("smileLeft", 0.430059f), new VisemeMapping("frownRight", 0.474702f), new VisemeMapping("frownLeft", 0.489583f), new VisemeMapping("mouthDownLeft", 0.325893f), new VisemeMapping("mouthDownRight", 0.288690f), new VisemeMapping("jawOpen", 0.185882f), new VisemeMapping("pucker", 0.590030f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("noseDown", 0.245870f) } },
            { "LeftThumbFinger1", new[] { new VisemeMapping("jawRotate", 0.208048f), new VisemeMapping("smileRight", 0.366815f), new VisemeMapping("cheekPuff", 0.846726f), new VisemeMapping("smileLeft", 0.430059f), new VisemeMapping("frownRight", 0.474702f), new VisemeMapping("frownLeft", 0.489583f), new VisemeMapping("mouthDownLeft", 0.325893f), new VisemeMapping("mouthDownRight", 0.288690f), new VisemeMapping("jawOpen", 0.185882f), new VisemeMapping("pucker", 0.590030f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("noseDown", 0.245870f) } },
            { "LeftThumbFinger2", new[] { new VisemeMapping("jawRotate", 0.208048f), new VisemeMapping("smileRight", 0.366815f), new VisemeMapping("cheekPuff", 0.846726f), new VisemeMapping("smileLeft", 0.430059f), new VisemeMapping("frownRight", 0.474702f), new VisemeMapping("frownLeft", 0.489583f), new VisemeMapping("mouthDownLeft", 0.325893f), new VisemeMapping("mouthDownRight", 0.288690f), new VisemeMapping("jawOpen", 0.185882f), new VisemeMapping("pucker", 0.590030f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("noseDown", 0.245870f) } },
            { "LeftWrist", new[] { new VisemeMapping("pucker", 0.300000f) } },
            { "LipCorner_left", new[] { new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("smileRight", 0.989310f), new VisemeMapping("smileLeft", 1.000000f), new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("mouthDownLeft", 0.210000f), new VisemeMapping("mouthDownRight", 0.220000f), new VisemeMapping("jawOpen", 0.020000f), new VisemeMapping("sneerLeft", 0.090000f), new VisemeMapping("sneerRight", 0.080000f), new VisemeMapping("noseUp", 0.182702f) } },
            { "LipCorner_right", new[] { new VisemeMapping("jawRotate", 0.347911f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("mouthDownLeft", 0.430515f), new VisemeMapping("mouthDownRight", 0.479106f), new VisemeMapping("O_mouth", 0.372206f), new VisemeMapping("jawOpen", 0.128251f), new VisemeMapping("jawForward", 0.571429f), new VisemeMapping("noseDown", 0.226433f) } },
            { "LowerBack", new[] { new VisemeMapping("smileRight", 0.401361f), new VisemeMapping("jawClench", 0.362488f), new VisemeMapping("cheekPuff", 0.508260f), new VisemeMapping("smileLeft", 0.498542f), new VisemeMapping("frownRight", 0.921283f), new VisemeMapping("frownLeft", 0.765792f), new VisemeMapping("sneerLeft", 0.120000f), new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("jawForward", 0.114674f) } },
            { "LowerCheek_left", new[] { new VisemeMapping("jawRotate", 0.190000f), new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("jawOpen", 0.044021f), new VisemeMapping("pucker", 0.050000f) } },
            { "lowerCheek_right", new[] { new VisemeMapping("smileRight", 0.401361f), new VisemeMapping("jawClench", 0.362488f), new VisemeMapping("cheekPuff", 0.508260f), new VisemeMapping("smileLeft", 0.498542f), new VisemeMapping("frownRight", 0.921283f), new VisemeMapping("frownLeft", 0.765792f), new VisemeMapping("sneerLeft", 0.120000f), new VisemeMapping("sneerRight", 0.100000f), new VisemeMapping("jawForward", 0.114674f) } },
            { "lowerLip_left", new[] { new VisemeMapping("pucker", 0.300000f) } },
            { "lowerLip_right", new[] { new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("jawRotate", 0.100000f) } },
            { "lowLid_Left", new[] { new VisemeMapping("jawRotate", 0.208048f), new VisemeMapping("smileRight", 0.366815f), new VisemeMapping("cheekPuff", 0.846726f), new VisemeMapping("smileLeft", 0.430059f), new VisemeMapping("frownRight", 0.474702f), new VisemeMapping("frownLeft", 0.489583f), new VisemeMapping("mouthDownLeft", 0.325893f), new VisemeMapping("mouthDownRight", 0.288690f), new VisemeMapping("jawOpen", 0.185882f), new VisemeMapping("pucker", 0.590030f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("noseDown", 0.245870f) } },
            { "lowLid_Right", new[] { new VisemeMapping("jawRotate", 0.347911f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("mouthDownLeft", 0.430515f), new VisemeMapping("mouthDownRight", 0.479106f), new VisemeMapping("O_mouth", 0.372206f), new VisemeMapping("jawOpen", 0.128251f), new VisemeMapping("jawForward", 0.571429f), new VisemeMapping("noseDown", 0.226433f) } },
            { "MouthBase", new[] { new VisemeMapping("smileRight", 0.050000f), new VisemeMapping("smileLeft", 0.050000f), new VisemeMapping("frownRight", 0.300000f), new VisemeMapping("frownLeft", 0.300000f), new VisemeMapping("sneerLeft", 0.210000f), new VisemeMapping("sneerRight", 0.180000f), new VisemeMapping("jawForward", 0.020000f), new VisemeMapping("noseUp", 0.250729f) } },
            { "Neck", new[] { new VisemeMapping("cheekLeft", 0.231293f), new VisemeMapping("cheekRight", 0.323615f), new VisemeMapping("smileRight", 0.683188f), new VisemeMapping("jawClench", 0.468082f), new VisemeMapping("smileLeft", 0.658892f), new VisemeMapping("frownRight", 0.289602f), new VisemeMapping("frownLeft", 0.435374f), new VisemeMapping("mouthDownLeft", 0.746356f), new VisemeMapping("mouthDownRight", 0.751215f), new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawForward", 0.406220f), new VisemeMapping("upperLipCurlIn", 0.690000f), new VisemeMapping("lowerLipCurlIn", 0.890000f) } },
            { "Neck1", new[] { new VisemeMapping("jawRotate", 0.030000f), new VisemeMapping("mouthDownLeft", 0.736637f), new VisemeMapping("mouthDownRight", 0.707483f), new VisemeMapping("O_mouth", 0.100000f), new VisemeMapping("jawForward", 0.265306f), new VisemeMapping("tongueUP", 1.000000f) } },
            { "outBrow_left", new[] { new VisemeMapping("jawClench", 0.624763f), new VisemeMapping("smileLeft", 0.177843f), new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawOpen", 0.066187f), new VisemeMapping("pucker", 1.000000f), new VisemeMapping("sneerLeft", 0.362013f), new VisemeMapping("sneerRight", 0.394480f), new VisemeMapping("upperLipCurlOut", 0.454811f), new VisemeMapping("lowerLipCurlIn", 1.000000f), new VisemeMapping("noseUp", 0.182702f) } },
            { "outBrow_Right", new[] { new VisemeMapping("sneerRight", 0.060000f), new VisemeMapping("sneerLeft", 0.040000f), new VisemeMapping("upperLipCurlOut", 0.490000f), new VisemeMapping("lowerLipCurlIn", 1.000000f), new VisemeMapping("jawClench", 0.170000f) } },
            { "outerUpperLip_left", new[] { new VisemeMapping("sneerRight", 0.130000f), new VisemeMapping("sneerLeft", 0.100000f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("lowerLipCurlOut", 0.160000f), new VisemeMapping("jawForward", 0.340000f), new VisemeMapping("O_mouth", 0.100000f), new VisemeMapping("mouthDownLeft", 0.070000f), new VisemeMapping("mouthDownRight", 0.060000f) } },
            { "outerUpperLip_right", new[] { new VisemeMapping("jawRotate", 0.347911f), new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.100000f), new VisemeMapping("mouthDownLeft", 0.430515f), new VisemeMapping("mouthDownRight", 0.479106f), new VisemeMapping("O_mouth", 0.372206f), new VisemeMapping("jawOpen", 0.128251f), new VisemeMapping("jawForward", 0.571429f), new VisemeMapping("noseDown", 0.226433f) } },
            { "Prop02", new[] { new VisemeMapping("jawRotate", 0.160000f), new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("mouthDownLeft", 0.230000f), new VisemeMapping("mouthDownRight", 0.220000f), new VisemeMapping("jawOpen", 0.066187f), new VisemeMapping("noseUp", 0.206997f) } },
            { "Root", new[] { new VisemeMapping("upperLipCurlIn", 0.110000f), new VisemeMapping("lowerLipCurlIn", 0.110000f), new VisemeMapping("jawClench", 0.190000f) } },
            { "SFX_AlienB", new[] { new VisemeMapping("jawClench", 1.000000f), new VisemeMapping("frownRight", 0.688047f), new VisemeMapping("frownLeft", 0.668610f), new VisemeMapping("mouthDownLeft", 0.445092f), new VisemeMapping("mouthDownRight", 0.415938f), new VisemeMapping("pucker", 0.046647f), new VisemeMapping("jawForward", 0.590865f) } },
            { "Sneer", new[] { new VisemeMapping("jawRotate", 0.208048f), new VisemeMapping("smileRight", 0.366815f), new VisemeMapping("cheekPuff", 0.846726f), new VisemeMapping("smileLeft", 0.430059f), new VisemeMapping("frownRight", 0.474702f), new VisemeMapping("frownLeft", 0.489583f), new VisemeMapping("mouthDownLeft", 0.325893f), new VisemeMapping("mouthDownRight", 0.288690f), new VisemeMapping("jawOpen", 0.185882f), new VisemeMapping("pucker", 0.590030f), new VisemeMapping("lowerLipCurlOut", 0.180000f), new VisemeMapping("noseDown", 0.245870f) } },
            { "Socket_02", new[] { new VisemeMapping("jawRotate", 0.210000f), new VisemeMapping("frownRight", 0.296131f), new VisemeMapping("frownLeft", 0.296131f), new VisemeMapping("mouthDownLeft", 0.258929f), new VisemeMapping("mouthDownRight", 0.340774f), new VisemeMapping("O_mouth", 1.000000f), new VisemeMapping("jawOpen", 0.173363f), new VisemeMapping("pucker", 0.895089f), new VisemeMapping("noseDown", 0.216715f) } },
            { "Tongue", new[] { new VisemeMapping("frownRight", 0.100000f), new VisemeMapping("frownLeft", 0.070000f), new VisemeMapping("O_mouth", 0.100000f), new VisemeMapping("pucker", 0.050000f), new VisemeMapping("sneerLeft", 0.090000f), new VisemeMapping("sneerRight", 0.090000f), new VisemeMapping("noseUp", 0.488636f) } },
            { "underEye_left", new[] { new VisemeMapping("jawRotate", 0.070000f), new VisemeMapping("mouthDownLeft", 0.532556f), new VisemeMapping("mouthDownRight", 0.547133f), new VisemeMapping("O_mouth", 0.200000f), new VisemeMapping("jawOpen", 0.138970f), new VisemeMapping("sneerLeft", 0.435374f), new VisemeMapping("sneerRight", 0.445092f), new VisemeMapping("jawForward", 1.000000f), new VisemeMapping("noseUp", 0.197279f), new VisemeMapping("tongueUP", 1.000000f) } },
            { "underEye_Right", new[] { new VisemeMapping("jawRotate", 0.070000f), new VisemeMapping("O_mouth", 0.532556f), new VisemeMapping("jawOpen", 0.060000f), new VisemeMapping("pucker", 0.328474f), new VisemeMapping("sneerLeft", 0.571429f), new VisemeMapping("sneerRight", 0.367347f), new VisemeMapping("jawForward", 0.420797f), new VisemeMapping("upperLipCurlOut", 0.430000f), new VisemeMapping("noseUp", 0.109816f), new VisemeMapping("tongueUP", 1.000000f) } },
            { "upperLip_left", new[] { new VisemeMapping("frownRight", 0.200000f), new VisemeMapping("frownLeft", 0.200000f), new VisemeMapping("jawOpen", 0.050000f) } },
            { "upperLip_right", new[] { new VisemeMapping("jawRotate", 0.478470f), new VisemeMapping("frownRight", 0.348214f), new VisemeMapping("frownLeft", 0.340774f), new VisemeMapping("jawOpen", 0.039588f), new VisemeMapping("sneerLeft", 0.087798f), new VisemeMapping("sneerRight", 0.113839f), new VisemeMapping("jawRotateUp", 0.229167f), new VisemeMapping("upperLipCurlIn", 0.330000f), new VisemeMapping("lowerLipCurlIn", 0.046875f) } },
        };

        /// <summary>
        /// All viseme animation names used in lip sync for Vorcha
        /// </summary>
        public static readonly string[] VorchaVisemes =
        [
            "smileRight",
            "smileLeft",
            "frownRight",
            "frownLeft",
            "sneerRight",
            "sneerLeft",
            "jawOpen",
            "jawRotate",
            "jawRotateUp",
            "jawForward",
            "jawSideRight",
            "jawClench",
            "mouthDownLeft",
            "mouthDownRight",
            "O_mouth",
            "pucker",
            "upperLipCurlIn",
            "lowerLipCurlIn",
            "upperLipCurlOut",
            "lowerLipCurlOut",
            "cheekLeft",
            "cheekRight",
            "cheekPuff",
            "smileJawClench",
            "noseUp",
            "noseDown",
            "tongueUP"
        ];

        /// <summary>
        /// Prothean phoneme to viseme mappings - from SFX_Prothean_FaceFX data.
        /// Note: Prothean uses a unique morph target system with m_ prefixed names.
        /// </summary>
        public static readonly Dictionary<string, VisemeMapping[]> ProtheanPhonemeMap = new()
        {
            // Prothean phoneme mappings from SFX_Prothean_FaceFX
            { "Neck", new[] { new VisemeMapping("neck_RX+", 0.000000f), new VisemeMapping("neck_RX-", 0.000000f), new VisemeMapping("neck_RZ+", 0.000000f), new VisemeMapping("neck_RZ-", 0.000000f), new VisemeMapping("neck_RY+", 0.000000f), new VisemeMapping("neck_RY-", 0.000000f) } },
            { "Head", new[] { new VisemeMapping("head_RX+", 0.000000f), new VisemeMapping("head_RX-", 0.000000f), new VisemeMapping("head_RY+", 0.000000f), new VisemeMapping("head_RY-", 0.000000f), new VisemeMapping("head_RZ+", 0.000000f), new VisemeMapping("head_RZ-", 0.000000f) } },
            { "brow_left", new[] { new VisemeMapping("m_CockedBrows_D", 0.574100f), new VisemeMapping("m_EmotionBrows_D", 0.436900f), new VisemeMapping("m_CockedBrows_R", 0.003001f), new VisemeMapping("m_CockedBrows_L", 1.512001f), new VisemeMapping("m_CockedBrows_U", 0.237400f), new VisemeMapping("m_EmotionBrows_R", 0.235501f), new VisemeMapping("m_UpDownBrow_LR", 0.575301f), new VisemeMapping("m_EyelidsLookat_U", 0.000000f), new VisemeMapping("m_EyelidsLookat_D", 0.000001f), new VisemeMapping("m_UpDownBrow_UD", 1.061300f), new VisemeMapping("m_EmotionBrows_L", 0.274302f), new VisemeMapping("m_EmotionBrows_U", 0.650102f) } },
            { "brow_right", new[] { new VisemeMapping("m_CockedBrows_D", 0.242701f), new VisemeMapping("m_EmotionBrows_D", 0.436900f), new VisemeMapping("m_CockedBrows_R", 1.461500f), new VisemeMapping("m_CockedBrows_L", 0.000001f), new VisemeMapping("m_CockedBrows_U", 0.568701f), new VisemeMapping("m_EmotionBrows_R", 0.235499f), new VisemeMapping("m_UpDownBrow_LR", 0.575301f), new VisemeMapping("m_EyelidsLookat_U", 0.000001f), new VisemeMapping("m_EyelidsLookat_D", 0.000001f), new VisemeMapping("m_UpDownBrow_UD", 1.061300f), new VisemeMapping("m_EmotionBrows_L", 0.274300f), new VisemeMapping("m_EmotionBrows_U", 0.650001f) } },
            { "eyeBlink_Right", new[] { new VisemeMapping("m_Squint_Eyelids_UD", -0.064899f), new VisemeMapping("m_WideOpen_Eyelids_LR", -0.002100f), new VisemeMapping("m_WideOpen_Eyelids_UD", -0.001801f), new VisemeMapping("m_EyelidsLookat_L", 0.000001f), new VisemeMapping("m_EyelidsLookat_R", 0.000000f), new VisemeMapping("m_EyelidsLookat_U", 0.000000f), new VisemeMapping("m_EyelidsLookat_D", -0.048900f), new VisemeMapping("m_BlinksLookat_UD", -0.064899f), new VisemeMapping("m_BlinksLookat_LR", -0.064899f) } },
            { "outBrow_left", new[] { new VisemeMapping("m_CockedBrows_D", 1.058300f), new VisemeMapping("m_EmotionBrows_D", 0.135100f), new VisemeMapping("m_CockedBrows_R", 0.000001f), new VisemeMapping("m_CockedBrows_L", 0.000001f), new VisemeMapping("m_CockedBrows_U", -0.005400f), new VisemeMapping("m_EmotionBrows_R", -0.027700f), new VisemeMapping("m_UpDownBrow_LR", 1.058300f), new VisemeMapping("m_EyelidsLookat_U", 0.000000f), new VisemeMapping("m_EyelidsLookat_D", 0.000000f), new VisemeMapping("m_UpDownBrow_UD", -0.000799f), new VisemeMapping("m_EmotionBrows_L", 0.091702f), new VisemeMapping("m_EmotionBrows_U", 1.296301f) } },
            { "outBrow_Right", new[] { new VisemeMapping("m_CockedBrows_D", -0.000599f), new VisemeMapping("m_EmotionBrows_D", 0.135003f), new VisemeMapping("m_CockedBrows_R", 0.000001f), new VisemeMapping("m_CockedBrows_L", 0.000001f), new VisemeMapping("m_CockedBrows_U", 1.053000f), new VisemeMapping("m_EmotionBrows_R", -0.027699f), new VisemeMapping("m_UpDownBrow_LR", 1.058202f), new VisemeMapping("m_EyelidsLookat_U", 0.000001f), new VisemeMapping("m_EyelidsLookat_D", 0.000000f), new VisemeMapping("m_UpDownBrow_UD", -0.000899f), new VisemeMapping("m_EmotionBrows_L", 0.091701f), new VisemeMapping("m_EmotionBrows_U", 1.296202f) } },
            { "underEye_left", new[] { new VisemeMapping("m_CockedBrows_D", -0.001900f), new VisemeMapping("m_EmotionBrows_D", -0.001900f), new VisemeMapping("m_CockedBrows_R", -0.001900f), new VisemeMapping("m_CockedBrows_L", -0.001900f), new VisemeMapping("m_CockedBrows_U", -0.001900f), new VisemeMapping("m_EmotionBrows_R", -0.001900f), new VisemeMapping("m_UpDownBrow_LR", -0.001900f), new VisemeMapping("m_UpDownBrow_UD", -0.001900f), new VisemeMapping("m_EmotionBrows_L", -0.001900f), new VisemeMapping("m_EmotionBrows_U", -0.001900f) } },
            { "underEye_Right", new[] { new VisemeMapping("m_CockedBrows_D", -0.001901f), new VisemeMapping("m_EmotionBrows_D", -0.001901f), new VisemeMapping("m_CockedBrows_R", -0.001901f), new VisemeMapping("m_CockedBrows_L", -0.001901f), new VisemeMapping("m_CockedBrows_U", -0.001901f), new VisemeMapping("m_EmotionBrows_R", -0.001901f), new VisemeMapping("m_UpDownBrow_LR", -0.001901f), new VisemeMapping("m_UpDownBrow_UD", -0.001901f), new VisemeMapping("m_EmotionBrows_L", -0.001901f), new VisemeMapping("m_EmotionBrows_U", -0.001901f) } },
            { "jawBone", new[] { new VisemeMapping("m_Open_LR", 0.035004f), new VisemeMapping("m_Open_UD", 0.035004f), new VisemeMapping("m_JawRotate_U", 0.266388f), new VisemeMapping("m_JawRotate_D", 0.036392f), new VisemeMapping("m_JawRotate_L", 0.035004f), new VisemeMapping("m_Jaw-", 0.002396f), new VisemeMapping("m_Jaw+", 0.000397f), new VisemeMapping("m_JawRotate_R", 0.035004f), new VisemeMapping("m_Angry_UD", -0.241714f) } },
            { "LowerCheek_left", new[] { new VisemeMapping("m_Open_UD", 2.313103f), new VisemeMapping("m_OH", 0.126709f), new VisemeMapping("m_EE", 0.002609f), new VisemeMapping("m_EH", 0.001907f), new VisemeMapping("m_OW", 0.000000f), new VisemeMapping("m_ZZ", 0.001907f), new VisemeMapping("m_TH", 0.000595f), new VisemeMapping("m_N", 0.000595f), new VisemeMapping("m_L", 0.000595f), new VisemeMapping("m_G", 0.000595f), new VisemeMapping("m_Open", 0.000595f), new VisemeMapping("m_Closed_R", 0.033004f), new VisemeMapping("m_Offset_L", 0.034515f), new VisemeMapping("m_Closed_U", 0.034012f), new VisemeMapping("m_Offset_R", 0.034515f), new VisemeMapping("m_Offset_U", -0.741989f), new VisemeMapping("m_Closed_D", -0.119888f), new VisemeMapping("m_Offset_D", 0.034515f), new VisemeMapping("m_M", 0.000595f), new VisemeMapping("m_FV", 0.000595f), new VisemeMapping("m_Angry_UD", 1.356705f), new VisemeMapping("m_Smile_Frown_U", -3.582093f), new VisemeMapping("m_Smile_Frown_D", 1.648514f), new VisemeMapping("m_Smile_Frown_L", -3.575791f), new VisemeMapping("m_Smile_Frown_R", -1.511002f), new VisemeMapping("m_Closed_L", 0.035507f), new VisemeMapping("m_Angry_L", 0.035507f), new VisemeMapping("m_Angry_R", 0.035507f) } },
            { "lowerCheek_right", new[] { new VisemeMapping("m_Open_UD", 1.860199f), new VisemeMapping("m_OH", 0.126801f), new VisemeMapping("m_EE", 0.002686f), new VisemeMapping("m_EH", 0.001999f), new VisemeMapping("m_OW", -0.000015f), new VisemeMapping("m_ZZ", 0.001984f), new VisemeMapping("m_TH", 0.000687f), new VisemeMapping("m_N", 0.000687f), new VisemeMapping("m_L", 0.000687f), new VisemeMapping("m_G", 0.000687f), new VisemeMapping("m_Open", 0.000687f), new VisemeMapping("m_Closed_R", 0.035996f), new VisemeMapping("m_Offset_L", 0.036499f), new VisemeMapping("m_Closed_U", 0.034988f), new VisemeMapping("m_Offset_R", 0.036499f), new VisemeMapping("m_Offset_U", 0.036499f), new VisemeMapping("m_Closed_D", -0.119202f), new VisemeMapping("m_Offset_D", 0.036499f), new VisemeMapping("m_M", 0.000687f), new VisemeMapping("m_FV", 0.000687f), new VisemeMapping("m_Angry_UD", 1.672989f), new VisemeMapping("m_Smile_Frown_U", -3.581100f), new VisemeMapping("m_Smile_Frown_D", 1.648499f), new VisemeMapping("m_Smile_Frown_L", -1.672012f), new VisemeMapping("m_Smile_Frown_R", -3.887787f), new VisemeMapping("m_Closed_L", 0.035492f), new VisemeMapping("m_Angry_L", 0.035492f), new VisemeMapping("m_Angry_R", 0.035492f) } },
            { "Tongue", new[] { new VisemeMapping("m_TH", -1.315598f), new VisemeMapping("m_N", -1.209579f), new VisemeMapping("m_L", -1.423691f), new VisemeMapping("m_Offset_L", 0.035492f), new VisemeMapping("m_Offset_R", 0.035492f), new VisemeMapping("m_Offset_U", 0.035492f), new VisemeMapping("m_Offset_D", 0.035492f), new VisemeMapping("m_Smile_Frown_U", 0.035492f), new VisemeMapping("m_Smile_Frown_D", 0.034500f), new VisemeMapping("m_Smile_Frown_L", 0.035492f), new VisemeMapping("m_Smile_Frown_R", 0.035492f), new VisemeMapping("m_Angry_L", 0.035492f), new VisemeMapping("m_Angry_R", 0.035492f) } },
            { "lowerLip_right", new[] { new VisemeMapping("m_Open_LR", 0.291214f), new VisemeMapping("m_Open_UD", 0.399506f), new VisemeMapping("m_OH", 0.224289f), new VisemeMapping("m_EE", 0.336899f), new VisemeMapping("m_EH", 0.281296f), new VisemeMapping("m_OW", 0.403000f), new VisemeMapping("m_ZZ", 0.201706f), new VisemeMapping("m_TH", 0.113693f), new VisemeMapping("m_N", 0.235306f), new VisemeMapping("m_L", 0.235306f), new VisemeMapping("m_G", 0.339691f), new VisemeMapping("m_Open", 0.436096f), new VisemeMapping("m_Closed_R", 0.122391f), new VisemeMapping("m_Offset_L", 0.035004f), new VisemeMapping("m_Closed_U", -0.186401f), new VisemeMapping("m_Offset_R", 0.034988f), new VisemeMapping("m_Offset_U", -1.028503f), new VisemeMapping("m_Closed_D", -0.335098f), new VisemeMapping("m_Offset_D", 1.140991f), new VisemeMapping("m_M", -0.118500f), new VisemeMapping("m_FV", -0.381393f), new VisemeMapping("m_Angry_UD", 1.507019f), new VisemeMapping("m_Smile_Frown_U", -0.095688f), new VisemeMapping("m_Smile_Frown_D", -0.167587f), new VisemeMapping("m_Smile_Frown_L", -0.884308f), new VisemeMapping("m_Smile_Frown_R", -0.131500f), new VisemeMapping("m_Closed_L", -0.031494f), new VisemeMapping("m_Angry_L", 0.143005f), new VisemeMapping("m_Angry_R", 0.035004f) } },
            { "lowerLip_left", new[] { new VisemeMapping("m_Open_LR", 0.291214f), new VisemeMapping("m_Open_UD", 0.400711f), new VisemeMapping("m_OH", 0.226303f), new VisemeMapping("m_EE", 0.342789f), new VisemeMapping("m_EH", 0.281204f), new VisemeMapping("m_OW", 0.402893f), new VisemeMapping("m_ZZ", 0.201599f), new VisemeMapping("m_TH", 0.113602f), new VisemeMapping("m_N", 0.235107f), new VisemeMapping("m_L", 0.235107f), new VisemeMapping("m_G", 0.339600f), new VisemeMapping("m_Open", 0.436005f), new VisemeMapping("m_Closed_R", 0.110413f), new VisemeMapping("m_Offset_L", 0.035995f), new VisemeMapping("m_Closed_U", -0.151398f), new VisemeMapping("m_Offset_R", 0.035995f), new VisemeMapping("m_Offset_U", -1.027496f), new VisemeMapping("m_Closed_D", -0.334793f), new VisemeMapping("m_Offset_D", 1.140991f), new VisemeMapping("m_M", -0.106598f), new VisemeMapping("m_FV", -0.381500f), new VisemeMapping("m_Angry_UD", 1.572006f), new VisemeMapping("m_Smile_Frown_U", -0.142700f), new VisemeMapping("m_Smile_Frown_D", -0.215607f), new VisemeMapping("m_Smile_Frown_L", -0.131500f), new VisemeMapping("m_Smile_Frown_R", -0.884293f), new VisemeMapping("m_Closed_L", -0.009598f), new VisemeMapping("m_Angry_L", 0.035995f), new VisemeMapping("m_Angry_R", 0.143005f) } },
            { "LipCorner_right", new[] { new VisemeMapping("m_Open_UD", 1.258209f), new VisemeMapping("m_OH", -0.434387f), new VisemeMapping("m_EE", -0.357086f), new VisemeMapping("m_EH", -0.405090f), new VisemeMapping("m_OW", -0.465195f), new VisemeMapping("m_ZZ", -0.388397f), new VisemeMapping("m_TH", -0.566788f), new VisemeMapping("m_N", -0.567108f), new VisemeMapping("m_L", -0.567108f), new VisemeMapping("m_G", -0.566788f), new VisemeMapping("m_Open", -0.410690f), new VisemeMapping("m_Flap", -0.222992f), new VisemeMapping("m_JawRotate_L", -0.041901f), new VisemeMapping("m_Closed_R", -0.297592f), new VisemeMapping("m_Offset_L", 0.035507f), new VisemeMapping("m_JawRotate_R", -0.041702f), new VisemeMapping("m_Closed_U", -0.459885f), new VisemeMapping("m_Offset_R", 0.036499f), new VisemeMapping("m_Offset_U", -1.026993f), new VisemeMapping("m_Closed_D", -0.848495f), new VisemeMapping("m_Offset_D", 1.141495f), new VisemeMapping("m_M", -0.451904f), new VisemeMapping("m_FV", -0.761795f), new VisemeMapping("m_Angry_UD", -0.280289f), new VisemeMapping("m_Smile_Frown_U", -2.468109f), new VisemeMapping("m_Smile_Frown_D", 0.592896f), new VisemeMapping("m_Smile_Frown_L", -1.457992f), new VisemeMapping("m_Smile_Frown_R", -2.873810f), new VisemeMapping("m_Closed_L", -0.728989f), new VisemeMapping("m_Angry_L", 0.035492f), new VisemeMapping("m_Angry_R", -0.343994f) } },
            { "LipCorner_left", new[] { new VisemeMapping("m_Open_UD", 0.859612f), new VisemeMapping("m_OH", -0.389404f), new VisemeMapping("m_EE", -0.357010f), new VisemeMapping("m_EH", -0.405106f), new VisemeMapping("m_OW", -0.465103f), new VisemeMapping("m_ZZ", -0.388397f), new VisemeMapping("m_TH", -0.566803f), new VisemeMapping("m_N", -0.567001f), new VisemeMapping("m_L", -0.567001f), new VisemeMapping("m_G", -0.566803f), new VisemeMapping("m_Open", -0.410690f), new VisemeMapping("m_Flap", -0.223007f), new VisemeMapping("m_Closed_R", -0.400589f), new VisemeMapping("m_Offset_L", 0.034500f), new VisemeMapping("m_Closed_U", -0.421890f), new VisemeMapping("m_Offset_R", 0.035492f), new VisemeMapping("m_Offset_U", -1.028000f), new VisemeMapping("m_Closed_D", -0.848984f), new VisemeMapping("m_Offset_D", 1.140488f), new VisemeMapping("m_M", -0.463898f), new VisemeMapping("m_FV", -0.762802f), new VisemeMapping("m_Angry_UD", -0.380188f), new VisemeMapping("m_Smile_Frown_U", -2.468201f), new VisemeMapping("m_Smile_Frown_D", 0.592896f), new VisemeMapping("m_Smile_Frown_L", -2.873795f), new VisemeMapping("m_Smile_Frown_R", -1.458008f), new VisemeMapping("m_Closed_L", -0.707993f), new VisemeMapping("m_Angry_L", -0.343994f), new VisemeMapping("m_Angry_R", 0.035492f) } },
            { "throat_left", new[] { new VisemeMapping("m_Open_LR", 4.224503f), new VisemeMapping("m_Open_UD", 5.796401f), new VisemeMapping("m_Closed_R", 3.381607f), new VisemeMapping("m_Closed_L", 0.035492f) } },
            { "throat_right", new[] { new VisemeMapping("m_Open_LR", 4.224503f), new VisemeMapping("m_Open_UD", 5.796212f), new VisemeMapping("m_Closed_R", 3.383514f), new VisemeMapping("m_Closed_L", 0.036514f) } },
            { "lowerLip_mid", new[] { new VisemeMapping("m_Open_LR", 0.620193f), new VisemeMapping("m_Open_UD", 0.467598f), new VisemeMapping("m_OH", 0.792191f), new VisemeMapping("m_EE", 0.653702f), new VisemeMapping("m_EH", 0.519501f), new VisemeMapping("m_OW", 0.614090f), new VisemeMapping("m_ZZ", 0.591583f), new VisemeMapping("m_TH", 0.240891f), new VisemeMapping("m_N", 0.540298f), new VisemeMapping("m_L", 0.406296f), new VisemeMapping("m_G", 0.548492f), new VisemeMapping("m_Open", 0.695190f), new VisemeMapping("m_Flap", 0.099792f), new VisemeMapping("m_Closed_R", -0.607803f), new VisemeMapping("m_Offset_L", 0.035294f), new VisemeMapping("m_Closed_U", -0.267197f), new VisemeMapping("m_Offset_R", 0.035294f), new VisemeMapping("m_Offset_U", -1.028198f), new VisemeMapping("m_Closed_D", -0.236801f), new VisemeMapping("m_Offset_D", 1.141296f), new VisemeMapping("m_M", 0.003296f), new VisemeMapping("m_FV", -0.209595f), new VisemeMapping("m_Angry_UD", 0.774200f), new VisemeMapping("m_Smile_Frown_U", 0.336594f), new VisemeMapping("m_Smile_Frown_D", -0.287109f), new VisemeMapping("m_Smile_Frown_L", -0.394897f), new VisemeMapping("m_Smile_Frown_R", -0.394897f), new VisemeMapping("m_Closed_L", 0.035187f), new VisemeMapping("m_Angry_L", 0.096298f), new VisemeMapping("m_Angry_R", 0.096298f) } },
            { "upperLip_left", new[] { new VisemeMapping("m_Open_LR", 0.035400f), new VisemeMapping("m_Open_UD", 0.268707f), new VisemeMapping("m_OH", 0.513092f), new VisemeMapping("m_EE", -0.028610f), new VisemeMapping("m_EH", 0.114594f), new VisemeMapping("m_OW", 0.001099f), new VisemeMapping("m_ZZ", -0.043900f), new VisemeMapping("m_TH", 0.102493f), new VisemeMapping("m_N", -0.171097f), new VisemeMapping("m_L", -0.035110f), new VisemeMapping("m_G", 0.102600f), new VisemeMapping("m_Open", 0.375793f), new VisemeMapping("m_Flap", 0.057098f), new VisemeMapping("m_JawRotate_L", -0.267288f), new VisemeMapping("m_Closed_R", 0.175400f), new VisemeMapping("m_Offset_L", 0.033401f), new VisemeMapping("m_JawRotate_R", 0.371292f), new VisemeMapping("m_Closed_U", -0.027802f), new VisemeMapping("m_Offset_R", 0.134399f), new VisemeMapping("m_Offset_U", -1.029099f), new VisemeMapping("m_Closed_D", 0.178207f), new VisemeMapping("m_Offset_D", 1.453400f), new VisemeMapping("m_M", -0.070206f), new VisemeMapping("m_FV", -0.102097f), new VisemeMapping("m_Angry_UD", -1.807404f), new VisemeMapping("m_Smile_Frown_U", -0.492584f), new VisemeMapping("m_Smile_Frown_D", 0.244110f), new VisemeMapping("m_Smile_Frown_L", -0.655396f), new VisemeMapping("m_Smile_Frown_R", -0.086090f), new VisemeMapping("m_Closed_L", -0.032196f), new VisemeMapping("m_Angry_L", -1.330200f), new VisemeMapping("m_Angry_R", 0.035400f) } },
            { "Sneer", new[] { new VisemeMapping("m_Open_LR", 0.035110f), new VisemeMapping("m_Open_UD", -0.114197f), new VisemeMapping("m_OH", 0.078903f), new VisemeMapping("m_EE", -0.106201f), new VisemeMapping("m_EH", 0.001297f), new VisemeMapping("m_OW", -0.197800f), new VisemeMapping("m_ZZ", -0.150299f), new VisemeMapping("m_TH", -0.117386f), new VisemeMapping("m_N", -0.228485f), new VisemeMapping("m_L", -0.092499f), new VisemeMapping("m_G", -0.036499f), new VisemeMapping("m_Open", 0.001297f), new VisemeMapping("m_Flap", 0.040192f), new VisemeMapping("m_JawRotate_L", -0.169296f), new VisemeMapping("m_Closed_R", -0.026489f), new VisemeMapping("m_Offset_L", 0.034103f), new VisemeMapping("m_JawRotate_R", -0.169495f), new VisemeMapping("m_Closed_U", -0.230286f), new VisemeMapping("m_Offset_R", 0.034103f), new VisemeMapping("m_Offset_U", -1.028381f), new VisemeMapping("m_Closed_D", 0.037308f), new VisemeMapping("m_Offset_D", 1.140106f), new VisemeMapping("m_M", -0.071594f), new VisemeMapping("m_FV", -0.394302f), new VisemeMapping("m_Angry_UD", -1.009003f), new VisemeMapping("m_Smile_Frown_U", -0.338196f), new VisemeMapping("m_Smile_Frown_D", -0.478699f), new VisemeMapping("m_Smile_Frown_L", -0.145294f), new VisemeMapping("m_Smile_Frown_R", -0.145294f), new VisemeMapping("m_Closed_L", 0.185104f), new VisemeMapping("m_Angry_L", -0.110291f), new VisemeMapping("m_Angry_R", -0.110382f) } },
            { "upperLip_right", new[] { new VisemeMapping("m_Open_LR", 0.035507f), new VisemeMapping("m_Open_UD", 0.268112f), new VisemeMapping("m_OH", 0.460495f), new VisemeMapping("m_EE", -0.028595f), new VisemeMapping("m_EH", 0.114700f), new VisemeMapping("m_OW", 0.001205f), new VisemeMapping("m_ZZ", -0.043793f), new VisemeMapping("m_TH", 0.031815f), new VisemeMapping("m_N", -0.170990f), new VisemeMapping("m_L", -0.034988f), new VisemeMapping("m_G", -0.015900f), new VisemeMapping("m_Open", 0.375916f), new VisemeMapping("m_Flap", 0.057007f), new VisemeMapping("m_JawRotate_L", 0.371704f), new VisemeMapping("m_Closed_R", 0.178802f), new VisemeMapping("m_Offset_L", 0.134506f), new VisemeMapping("m_JawRotate_R", -0.267288f), new VisemeMapping("m_Closed_U", -0.010193f), new VisemeMapping("m_Offset_R", 0.035507f), new VisemeMapping("m_Offset_U", -1.028992f), new VisemeMapping("m_Closed_D", 0.178604f), new VisemeMapping("m_Offset_D", 1.453506f), new VisemeMapping("m_M", -0.071198f), new VisemeMapping("m_FV", -0.101990f), new VisemeMapping("m_Angry_UD", -1.979095f), new VisemeMapping("m_Smile_Frown_U", -0.458588f), new VisemeMapping("m_Smile_Frown_D", 0.262405f), new VisemeMapping("m_Smile_Frown_L", -0.085999f), new VisemeMapping("m_Smile_Frown_R", -0.655396f), new VisemeMapping("m_Closed_L", -0.011093f), new VisemeMapping("m_Angry_L", 0.035507f), new VisemeMapping("m_Angry_R", -1.167786f) } },
            { "eye_Right", new[] { new VisemeMapping("eye_Right_RX+", 0.000000f), new VisemeMapping("eye_Right_RX-", 0.000000f), new VisemeMapping("eye_Right_RY+", 0.000000f), new VisemeMapping("eye_Right_RY-", 0.000000f), new VisemeMapping("eye_Right_RZ+", 0.000000f), new VisemeMapping("eye_Right_RZ-", 0.000000f) } },
            { "eye_Left", new[] { new VisemeMapping("eye_Left_RX+", 0.000000f), new VisemeMapping("eye_Left_RX-", 0.000000f), new VisemeMapping("eye_Left_RY+", 0.000000f), new VisemeMapping("eye_Left_RY-", 0.000000f), new VisemeMapping("eye_Left_RZ+", 0.000000f), new VisemeMapping("eye_Left_RZ-", 0.000000f) } },
            { "lowLid_Right", new[] { new VisemeMapping("m_Squint_Eyelids_UD", -0.077099f), new VisemeMapping("m_WideOpen_Eyelids_LR", -0.010899f), new VisemeMapping("m_WideOpen_Eyelids_UD", -0.001900f), new VisemeMapping("m_EyelidsLookat_L", 0.000001f), new VisemeMapping("m_EyelidsLookat_R", 0.000000f), new VisemeMapping("m_EyelidsLookat_U", 0.000000f), new VisemeMapping("m_EyelidsLookat_D", 0.000000f), new VisemeMapping("m_BlinksLookat_UD", -0.017000f), new VisemeMapping("m_BlinksLookat_LR", -0.137198f) } },
            { "eyeBlink_Left", new[] { new VisemeMapping("m_Squint_Eyelids_LR", -0.064899f), new VisemeMapping("m_WideOpen_Eyelids_LR", -0.002100f), new VisemeMapping("m_WideOpen_Eyelids_UD", -0.001800f), new VisemeMapping("m_EyelidsLookat_L", 0.000001f), new VisemeMapping("m_EyelidsLookat_R", 0.000000f), new VisemeMapping("m_EyelidsLookat_U", 0.000001f), new VisemeMapping("m_EyelidsLookat_D", -0.048900f), new VisemeMapping("m_BlinksLookat_UD", -0.064899f), new VisemeMapping("m_BlinksLookat_LR", -0.064899f) } },
            { "lowLid_Left", new[] { new VisemeMapping("m_Squint_Eyelids_LR", -0.077100f), new VisemeMapping("m_WideOpen_Eyelids_LR", -0.011000f), new VisemeMapping("m_WideOpen_Eyelids_UD", -0.001800f), new VisemeMapping("m_EyelidsLookat_L", 0.000001f), new VisemeMapping("m_EyelidsLookat_R", 0.000000f), new VisemeMapping("m_EyelidsLookat_U", 0.000001f), new VisemeMapping("m_EyelidsLookat_D", 0.000001f), new VisemeMapping("m_BlinksLookat_UD", -0.017000f), new VisemeMapping("m_BlinksLookat_LR", -0.137199f) } },
            { "nose", new[] { new VisemeMapping("m_JawRotate_L", -0.005198f), new VisemeMapping("m_Closed_R", -0.005198f), new VisemeMapping("m_JawRotate_R", -0.005198f), new VisemeMapping("m_Angry_UD", -0.005500f), new VisemeMapping("m_Closed_L", -0.005198f), new VisemeMapping("m_Angry_L", -0.005400f), new VisemeMapping("m_Angry_R", -0.005400f) } },
            { "cheek_right", new[] { new VisemeMapping("m_Open_UD", 0.829101f), new VisemeMapping("m_JawRotate_L", 0.815900f), new VisemeMapping("m_Closed_R", 2.052300f), new VisemeMapping("m_JawRotate_R", -0.004500f), new VisemeMapping("m_Closed_U", -0.004300f), new VisemeMapping("m_Closed_D", -0.001900f), new VisemeMapping("m_Angry_UD", -0.004800f), new VisemeMapping("m_Sneer_UD", -0.007999f), new VisemeMapping("m_Smile_Frown_U", -0.004500f), new VisemeMapping("m_Smile_Frown_D", -0.004300f), new VisemeMapping("m_Smile_Frown_L", -0.004300f), new VisemeMapping("m_Smile_Frown_R", -0.004700f), new VisemeMapping("m_Closed_L", -0.004000f), new VisemeMapping("m_Angry_L", -0.004400f), new VisemeMapping("m_Angry_R", -0.004800f) } },
            { "cheek_left", new[] { new VisemeMapping("m_Open_UD", -0.004300f), new VisemeMapping("m_JawRotate_L", -0.004399f), new VisemeMapping("m_Closed_R", 2.328001f), new VisemeMapping("m_JawRotate_R", 0.955801f), new VisemeMapping("m_Closed_U", -0.004299f), new VisemeMapping("m_Closed_D", -0.001800f), new VisemeMapping("m_Sneer_LR", -0.007900f), new VisemeMapping("m_Angry_UD", -0.004800f), new VisemeMapping("m_Smile_Frown_U", -0.004500f), new VisemeMapping("m_Smile_Frown_D", -0.004300f), new VisemeMapping("m_Smile_Frown_L", -0.004499f), new VisemeMapping("m_Smile_Frown_R", -0.004200f), new VisemeMapping("m_Closed_L", -0.004200f), new VisemeMapping("m_Angry_L", -0.004700f), new VisemeMapping("m_Angry_R", -0.004300f) } },
            { "upperBrow_Left", new[] { new VisemeMapping("m_CockedBrows_D", 0.605400f), new VisemeMapping("m_EmotionBrows_D", 0.015800f), new VisemeMapping("m_CockedBrows_R", 0.002299f), new VisemeMapping("m_CockedBrows_L", 0.000001f), new VisemeMapping("m_CockedBrows_U", -0.003800f), new VisemeMapping("m_EmotionBrows_R", -0.246000f), new VisemeMapping("m_UpDownBrow_LR", 0.605400f), new VisemeMapping("m_EyelidsLookat_L", 0.000000f), new VisemeMapping("m_EyelidsLookat_U", 0.000000f), new VisemeMapping("m_EyelidsLookat_D", 0.000000f), new VisemeMapping("m_UpDownBrow_UD", -0.001101f), new VisemeMapping("m_EmotionBrows_L", 0.029799f), new VisemeMapping("m_EmotionBrows_U", -0.082499f) } },
            { "upperBrow_Right", new[] { new VisemeMapping("m_CockedBrows_D", 0.000000f), new VisemeMapping("m_EmotionBrows_D", 0.015700f), new VisemeMapping("m_CockedBrows_R", 0.002100f), new VisemeMapping("m_CockedBrows_L", 0.000001f), new VisemeMapping("m_CockedBrows_U", 0.600600f), new VisemeMapping("m_EmotionBrows_R", -0.245900f), new VisemeMapping("m_UpDownBrow_LR", 0.605200f), new VisemeMapping("m_EyelidsLookat_U", 0.000000f), new VisemeMapping("m_EyelidsLookat_D", 0.000001f), new VisemeMapping("m_UpDownBrow_UD", -0.001100f), new VisemeMapping("m_EmotionBrows_L", 0.029800f), new VisemeMapping("m_EmotionBrows_U", -0.082601f) } },
            { "lowLid_top_Left", new[] { new VisemeMapping("m_Squint_Eyelids_LR", -0.014700f), new VisemeMapping("m_WideOpen_Eyelids_LR", -0.002200f), new VisemeMapping("m_WideOpen_Eyelids_UD", -0.001900f), new VisemeMapping("m_EyelidsLookat_L", 0.000000f), new VisemeMapping("m_EyelidsLookat_R", 0.000000f), new VisemeMapping("m_EyelidsLookat_U", 0.000000f), new VisemeMapping("m_EyelidsLookat_D", 0.000000f), new VisemeMapping("m_BlinksLookat_UD", -0.014699f), new VisemeMapping("m_BlinksLookat_LR", -0.014700f) } },
            { "eyeBlink_top_Left", new[] { new VisemeMapping("m_Squint_Eyelids_LR", 0.017900f), new VisemeMapping("m_WideOpen_Eyelids_LR", -0.035900f), new VisemeMapping("m_WideOpen_Eyelids_UD", -0.036799f), new VisemeMapping("m_EyelidsLookat_L", -0.034900f), new VisemeMapping("m_EyelidsLookat_R", 0.000000f), new VisemeMapping("m_EyelidsLookat_U", -0.034900f), new VisemeMapping("m_EyelidsLookat_D", -0.034900f), new VisemeMapping("m_BlinksLookat_UD", 0.017800f), new VisemeMapping("m_BlinksLookat_LR", 0.018300f) } },
            { "lowLid_top_Right", new[] { new VisemeMapping("m_Squint_Eyelids_UD", -0.014700f), new VisemeMapping("m_WideOpen_Eyelids_LR", -0.002200f), new VisemeMapping("m_WideOpen_Eyelids_UD", -0.001900f), new VisemeMapping("m_EyelidsLookat_L", -0.000001f), new VisemeMapping("m_EyelidsLookat_R", -0.000001f), new VisemeMapping("m_EyelidsLookat_U", 0.000000f), new VisemeMapping("m_EyelidsLookat_D", 0.000000f), new VisemeMapping("m_BlinksLookat_UD", -0.014701f), new VisemeMapping("m_BlinksLookat_LR", -0.014700f) } },
            { "eyeBlink_top_Right", new[] { new VisemeMapping("m_Squint_Eyelids_UD", 0.052799f), new VisemeMapping("m_WideOpen_Eyelids_LR", -0.001000f), new VisemeMapping("m_WideOpen_Eyelids_UD", -0.001900f), new VisemeMapping("m_EyelidsLookat_L", 0.033600f), new VisemeMapping("m_EyelidsLookat_R", -0.000001f), new VisemeMapping("m_EyelidsLookat_U", 0.000000f), new VisemeMapping("m_EyelidsLookat_D", 0.000000f), new VisemeMapping("m_BlinksLookat_UD", 0.052699f), new VisemeMapping("m_BlinksLookat_LR", 0.053199f) } },
            { "eye_top_Left", new[] { new VisemeMapping("eye_Left_RX+", 0.000000f), new VisemeMapping("eye_Left_RX-", 0.000000f), new VisemeMapping("eye_Left_RY+", 0.000000f), new VisemeMapping("eye_Left_RY-", 0.000000f), new VisemeMapping("eye_Left_RZ+", 0.000000f), new VisemeMapping("eye_Left_RZ-", 0.000000f) } },
            { "eye_top_Right", new[] { new VisemeMapping("eye_Right_RX+", 0.000000f), new VisemeMapping("eye_Right_RX-", 0.000000f), new VisemeMapping("eye_Right_RY+", 0.000000f), new VisemeMapping("eye_Right_RY-", 0.000000f), new VisemeMapping("eye_Right_RZ+", 0.000000f), new VisemeMapping("eye_Right_RZ-", 0.000000f) } },
        };

        /// <summary>
        /// All viseme animation names used in lip sync for Prothean
        /// Note: Prothean uses unique morph target names with m_ prefix
        /// </summary>
        public static readonly string[] ProtheanVisemes =
        [
            // Head and neck rotations
            "neck_RX+",
            "neck_RX-",
            "neck_RZ+",
            "neck_RZ-",
            "neck_RY+",
            "neck_RY-",
            "head_RX+",
            "head_RX-",
            "head_RY+",
            "head_RY-",
            "head_RZ+",
            "head_RZ-",
            // Brow controls
            "m_CockedBrows_D",
            "m_CockedBrows_R",
            "m_CockedBrows_L",
            "m_CockedBrows_U",
            "m_EmotionBrows_D",
            "m_EmotionBrows_R",
            "m_EmotionBrows_L",
            "m_EmotionBrows_U",
            "m_UpDownBrow_LR",
            "m_UpDownBrow_UD",
            // Eyelid controls
            "m_Squint_Eyelids_LR",
            "m_Squint_Eyelids_UD",
            "m_WideOpen_Eyelids_LR",
            "m_WideOpen_Eyelids_UD",
            "m_EyelidsLookat_L",
            "m_EyelidsLookat_R",
            "m_EyelidsLookat_U",
            "m_EyelidsLookat_D",
            "m_BlinksLookat_UD",
            "m_BlinksLookat_LR",
            // Jaw controls
            "m_Open_LR",
            "m_Open_UD",
            "m_Open",
            "m_JawRotate_U",
            "m_JawRotate_D",
            "m_JawRotate_L",
            "m_JawRotate_R",
            "m_Jaw-",
            "m_Jaw+",
            // Mouth shapes (phonemes)
            "m_OH",
            "m_EE",
            "m_EH",
            "m_OW",
            "m_ZZ",
            "m_TH",
            "m_N",
            "m_L",
            "m_G",
            "m_M",
            "m_FV",
            "m_Flap",
            // Closed/Offset controls
            "m_Closed_R",
            "m_Closed_L",
            "m_Closed_U",
            "m_Closed_D",
            "m_Offset_L",
            "m_Offset_R",
            "m_Offset_U",
            "m_Offset_D",
            // Expression controls
            "m_Angry_UD",
            "m_Angry_L",
            "m_Angry_R",
            "m_Smile_Frown_U",
            "m_Smile_Frown_D",
            "m_Smile_Frown_L",
            "m_Smile_Frown_R",
            "m_Sneer_UD",
            "m_Sneer_LR",
            // Eye rotations
            "eye_Right_RX+",
            "eye_Right_RX-",
            "eye_Right_RY+",
            "eye_Right_RY-",
            "eye_Right_RZ+",
            "eye_Right_RZ-",
            "eye_Left_RX+",
            "eye_Left_RX-",
            "eye_Left_RY+",
            "eye_Left_RY-",
            "eye_Left_RZ+",
            "eye_Left_RZ-"
        ];

        /// <summary>
        /// Yahg phoneme to viseme mappings - from SFX_Yahg_FaceFX data.
        /// Note: Yahg has a unique multi-jawed face with fins and 4 pairs of eyes.
        /// </summary>
        public static readonly Dictionary<string, VisemeMapping[]> YahgPhonemeMap = new()
        {
            // Yahg phoneme mappings from SFX_Yahg_FaceFX
            { "Neck", new[] { new VisemeMapping("neck_RX+", 0.000000f), new VisemeMapping("neck_RX-", 0.000000f), new VisemeMapping("neck_RZ+", 0.000000f), new VisemeMapping("neck_RZ-", 0.000000f), new VisemeMapping("neck_RY+", 0.000000f), new VisemeMapping("neck_RY-", 0.000000f) } },
            { "Head", new[] { new VisemeMapping("head_RX+", 0.000000f), new VisemeMapping("head_RX-", 0.000000f), new VisemeMapping("head_RY+", 0.000000f), new VisemeMapping("head_RY-", 0.000000f), new VisemeMapping("head_RZ+", 0.000000f), new VisemeMapping("head_RZ-", 0.000000f) } },
            { "HeadBase", new[] { new VisemeMapping("m_FaceOpen", 10.484099f) } },
            { "Jaw", new[] { new VisemeMapping("m_JawOpen", 0.647744f), new VisemeMapping("m_JawClench", 0.019733f), new VisemeMapping("m_FaceOpen", -1.036158f), new VisemeMapping("m_FaceClose", 0.000000f) } },
            { "LeftLipBtm3", new[] { new VisemeMapping("m_BLipCurlLMidOut", 0.000000f), new VisemeMapping("m_BLipCurlLMidIn", 0.000000f), new VisemeMapping("m_BLipCurlLOut", 0.000000f), new VisemeMapping("m_BLipCurlLIn", 0.000000f), new VisemeMapping("m_FaceOpen", -1.118356f) } },
            { "LeftChin", new[] { new VisemeMapping("m_LCFinUp", 0.000000f), new VisemeMapping("m_LCFinDown", 0.000000f), new VisemeMapping("m_LCFinSplayOut", 0.000000f), new VisemeMapping("m_LCFinSplayIn", 0.000000f) } },
            { "LeftFin1", new[] { new VisemeMapping("m_LFinFlare", 3.119480f), new VisemeMapping("m_LFinFold", 0.995887f), new VisemeMapping("m_LFinRotUp", 0.000000f), new VisemeMapping("m_LFinRotDown", 0.000000f) } },
            { "RightFin1", new[] { new VisemeMapping("m_RFinFlare", 3.092630f), new VisemeMapping("m_RFinFold", 0.969497f), new VisemeMapping("m_RFinRotUp", 0.000000f), new VisemeMapping("m_RFinRotDown", 0.000000f) } },
            { "RightLipBtm3", new[] { new VisemeMapping("m_BLipCurlRMidOut", 0.000000f), new VisemeMapping("m_BLipCurlRMidIn", 0.000000f), new VisemeMapping("m_BLipCurlROut", 0.000000f), new VisemeMapping("m_BLipCurlRIn", 0.000000f), new VisemeMapping("m_FaceOpen", -1.118360f) } },
            { "RightChin", new[] { new VisemeMapping("m_RCFinUp", 0.000000f), new VisemeMapping("m_RCFinDown", 0.000000f), new VisemeMapping("m_RCFinSplayOut", 0.000000f), new VisemeMapping("m_RCFinSplayIn", 0.000000f) } },
            { "LipBtmBase", new[] { new VisemeMapping("m_BBLipCurlOut", 0.000000f), new VisemeMapping("m_BBLipCurlIn", 0.000000f) } },
            { "LeftLipBtm1", new[] { new VisemeMapping("m_BLipCurlLMidOut", 0.000000f), new VisemeMapping("m_BLipCurlLMidIn", 0.000000f), new VisemeMapping("m_BLipCurlLOut", 0.000000f), new VisemeMapping("m_BLipCurlLIn", 0.000000f) } },
            { "RightLipBtm1", new[] { new VisemeMapping("m_BLipCurlRMidOut", 0.000000f), new VisemeMapping("m_BLipCurlRMidIn", 0.000000f), new VisemeMapping("m_BLipCurlROut", 0.000000f), new VisemeMapping("m_BLipCurlRIn", 0.000000f) } },
            { "RightLipBtm2", new[] { new VisemeMapping("m_BLipCurlRMidOut", 0.000000f), new VisemeMapping("m_BLipCurlRMidIn", 0.000000f), new VisemeMapping("m_BLipCurlROut", 0.000000f), new VisemeMapping("m_BLipCurlRIn", 0.000000f), new VisemeMapping("m_FaceOpen", -0.000003f) } },
            { "LeftLipBtm2", new[] { new VisemeMapping("m_BLipCurlLMidOut", 0.000000f), new VisemeMapping("m_BLipCurlLMidIn", 0.000000f), new VisemeMapping("m_BLipCurlLOut", 0.000000f), new VisemeMapping("m_BLipCurlLIn", 0.000000f), new VisemeMapping("m_FaceOpen", -0.000003f) } },
            { "MouthBottom", new[] { new VisemeMapping("m_InMouthContract", 1.372421f), new VisemeMapping("m_InMouthOut", 8.203371f), new VisemeMapping("m_InMouthRelax", -2.329309f), new VisemeMapping("m_InMouthIn", -14.095701f) } },
            { "LeftFin2", new[] { new VisemeMapping("m_LFinFlare", 3.005980f), new VisemeMapping("m_LFinFold", 0.764654f), new VisemeMapping("m_LFinRotUp", 0.000000f), new VisemeMapping("m_LFinRotDown", 0.000000f) } },
            { "RightFin2", new[] { new VisemeMapping("m_RFinFlare", 2.984540f), new VisemeMapping("m_RFinFold", 0.744391f), new VisemeMapping("m_RFinRotUp", 0.000000f), new VisemeMapping("m_RFinRotDown", 0.000000f) } },
            { "LeftFaceBase", new[] { new VisemeMapping("m_FaceOpen", -0.000001f), new VisemeMapping("m_FaceClose", 0.000000f) } },
            { "LeftLipTop1", new[] { new VisemeMapping("m_LLipCurlTopOut", 0.000000f), new VisemeMapping("m_LLipCurlTopIn", 0.000000f), new VisemeMapping("m_LLipCurlMidOut", 0.000000f), new VisemeMapping("m_LLipCurlMidIn", 0.000000f), new VisemeMapping("m_FaceOpen", 0.306275f) } },
            { "LeftLipTop2", new[] { new VisemeMapping("m_LLipCurlTopOut", 0.000000f), new VisemeMapping("m_LLipCurlTopIn", 0.000000f), new VisemeMapping("m_LLipCurlMidOut", 0.000000f), new VisemeMapping("m_LLipCurlMidIn", 0.000000f), new VisemeMapping("m_LLipCurlLowOut", 0.000000f), new VisemeMapping("m_LLipCurlLowIn", 0.000000f), new VisemeMapping("m_FaceOpen", 0.074781f) } },
            { "LeftLipTop3", new[] { new VisemeMapping("m_LLipCurlTopOut", 0.000000f), new VisemeMapping("m_LLipCurlTopIn", 0.000000f), new VisemeMapping("m_LLipCurlMidOut", 0.000000f), new VisemeMapping("m_LLipCurlMidIn", 0.000000f), new VisemeMapping("m_LLipCurlLowOut", 0.000000f), new VisemeMapping("m_LLipCurlLowIn", 0.000000f), new VisemeMapping("m_FaceOpen", 0.368922f) } },
            { "LeftLipTop4", new[] { new VisemeMapping("m_LLipCurlLowOut", 0.000000f), new VisemeMapping("m_LLipCurlLowIn", 0.000000f), new VisemeMapping("m_FaceOpen", 0.803947f) } },
            { "LeftEyeLid3Btm", new[] { new VisemeMapping("m_EyeLowInWide", 0.120258f), new VisemeMapping("m_EyeLowInBlink", -0.158587f), new VisemeMapping("m_L3EyeLidLowUp", -0.182460f), new VisemeMapping("m_L3EyeLidLowDown", 0.366809f) } },
            { "LeftEyeLid3Top", new[] { new VisemeMapping("m_EyeLowInWide", 0.129472f), new VisemeMapping("m_EyeLowInBlink", -0.175843f), new VisemeMapping("m_L3EyeLidTopUp", 0.073894f), new VisemeMapping("m_L3EyeLidTopDown", -0.386891f) } },
            { "LeftBrow3", new[] { new VisemeMapping("m_L3BrowUp", -0.221134f), new VisemeMapping("m_L3BrowDown", 0.277736f), new VisemeMapping("m_L3BrowOut", 1.071668f), new VisemeMapping("m_L3BrowIn", -0.659603f), new VisemeMapping("m_L3BrowRotOut", 0.000000f), new VisemeMapping("m_L3BrowRotIn", 0.000000f) } },
            { "LeftEyeLid1Btm", new[] { new VisemeMapping("m_EyeTopInWide", -0.159580f), new VisemeMapping("m_EyeTopInBlink", -0.450143f), new VisemeMapping("m_L1EyeLidLowUp", -0.172322f), new VisemeMapping("m_L1EyeLidLowDown", 0.272523f) } },
            { "LeftEyeLid1Top", new[] { new VisemeMapping("m_EyeTopInWide", 0.073172f), new VisemeMapping("m_EyeTopInBlink", -1.202842f), new VisemeMapping("m_L1EyeLidTopUp", 0.407610f), new VisemeMapping("m_L1EyeLidTopDown", -1.134641f) } },
            { "LeftBrow1", new[] { new VisemeMapping("m_L1BrowUp", -0.320147f), new VisemeMapping("m_L1BrowDown", 0.445965f), new VisemeMapping("m_L1BrowOut", 0.742152f), new VisemeMapping("m_L1BrowIn", -0.613498f), new VisemeMapping("m_L1BrowRotOut", 0.000000f), new VisemeMapping("m_L1BrowRotIn", 0.000000f) } },
            { "LeftCheek1", new[] { new VisemeMapping("m_LCheekOut", -0.284754f), new VisemeMapping("m_LCheekIn", 0.240236f), new VisemeMapping("m_LCheekUp", -0.489461f), new VisemeMapping("m_LCheekDown", 0.603806f) } },
            { "LeftCheek2", new[] { new VisemeMapping("m_LCheekOut", 2.299129f), new VisemeMapping("m_LCheekIn", -1.939702f), new VisemeMapping("m_LCheekUp", -2.241601f), new VisemeMapping("m_LCheekDown", 2.765291f) } },
            { "LeftBrow4", new[] { new VisemeMapping("m_L4BrowUp", -0.424752f), new VisemeMapping("m_L4BrowDown", 0.638373f), new VisemeMapping("m_L4BrowOut", 2.492081f), new VisemeMapping("m_L4BrowIn", -2.009891f), new VisemeMapping("m_L4BrowRotOut", 0.000000f), new VisemeMapping("m_L4BrowRotIn", 0.000000f) } },
            { "LeftBrow2", new[] { new VisemeMapping("m_L2BrowUp", -0.837227f), new VisemeMapping("m_L2BrowDown", 0.827205f), new VisemeMapping("m_L2BrowOut", 0.687475f), new VisemeMapping("m_L2BrowIn", -1.669969f), new VisemeMapping("m_L2BrowRotOut", 0.000000f), new VisemeMapping("m_L2BrowRotIn", 0.000000f) } },
            { "LeftEyeLid2Top", new[] { new VisemeMapping("m_EyeTopOutWide", -0.094891f), new VisemeMapping("m_EyeTopOutBlink", -0.116404f), new VisemeMapping("m_L2EyeLidTopUp", 0.016263f), new VisemeMapping("m_L2EyeLidTopDown", -0.022547f) } },
            { "LeftEyeLid2Btm", new[] { new VisemeMapping("m_EyeTopOutWide", 0.236201f), new VisemeMapping("m_EyeTopOutBlink", -0.544777f), new VisemeMapping("m_L2EyeLidLowUp", -0.771935f), new VisemeMapping("m_L2EyeLidLowDown", 0.440722f) } },
            { "LeftEyeLid4Top", new[] { new VisemeMapping("m_EyeLowOutWide", 0.092134f), new VisemeMapping("m_EyeLowOutBlink", -0.616920f), new VisemeMapping("m_L4EyeLidTopUp", 0.127346f), new VisemeMapping("m_L4EyeLidTopDown", -0.520843f) } },
            { "LeftEyeLid4Btm", new[] { new VisemeMapping("m_EyeLowOutWide", 0.107629f), new VisemeMapping("m_EyeLowOutBlink", -0.301813f), new VisemeMapping("m_L4EyeLidLowUp", -0.336879f), new VisemeMapping("m_L4EyeLidLowDown", 0.210599f) } },
            { "MouthTopLeft", new[] { new VisemeMapping("m_InMouthContract", -2.148889f), new VisemeMapping("m_InMouthOut", -9.308189f), new VisemeMapping("m_InMouthRelax", 3.555179f), new VisemeMapping("m_InMouthIn", 13.744499f) } },
            { "RightFaceBase", new[] { new VisemeMapping("m_FaceOpen", -0.000001f), new VisemeMapping("m_FaceClose", 0.000000f) } },
            { "RightLipTop1", new[] { new VisemeMapping("m_RLipCurlTopOut", 0.000000f), new VisemeMapping("m_RLipCurlTopIn", 0.000000f), new VisemeMapping("m_RLipCurlMidOut", 0.000000f), new VisemeMapping("m_RLipCurlMidIn", 0.000000f), new VisemeMapping("m_FaceOpen", -0.315406f) } },
            { "RightLipTop2", new[] { new VisemeMapping("m_RLipCurlTopOut", 0.000000f), new VisemeMapping("m_RLipCurlTopIn", 0.000000f), new VisemeMapping("m_RLipCurlMidOut", 0.000000f), new VisemeMapping("m_RLipCurlMidIn", 0.000000f), new VisemeMapping("m_RLipCurlLowOut", 0.000000f), new VisemeMapping("m_RLipCurlLowIn", 0.000000f), new VisemeMapping("m_FaceOpen", -0.051830f) } },
            { "RightLipTop3", new[] { new VisemeMapping("m_RLipCurlTopOut", 0.000000f), new VisemeMapping("m_RLipCurlTopIn", 0.000000f), new VisemeMapping("m_RLipCurlMidOut", 0.000000f), new VisemeMapping("m_RLipCurlMidIn", 0.000000f), new VisemeMapping("m_RLipCurlLowOut", 0.000000f), new VisemeMapping("m_RLipCurlLowIn", 0.000000f), new VisemeMapping("m_FaceOpen", -0.288717f) } },
            { "RightLipTop4", new[] { new VisemeMapping("m_RLipCurlLowOut", 0.000000f), new VisemeMapping("m_RLipCurlLowIn", 0.000000f), new VisemeMapping("m_FaceOpen", 0.386889f) } },
            { "RightEyeLid3Btm", new[] { new VisemeMapping("m_EyeLowInWide", -0.120262f), new VisemeMapping("m_EyeLowInBlink", 0.158592f), new VisemeMapping("m_R3EyeLidLowUp", 0.187330f), new VisemeMapping("m_R3EyeLidLowDown", -0.250314f) } },
            { "RightEyeLid3Top", new[] { new VisemeMapping("m_EyeLowInWide", -0.129473f), new VisemeMapping("m_EyeLowInBlink", 0.175840f), new VisemeMapping("m_R3EyeLidTopUp", -0.055202f), new VisemeMapping("m_R3EyeLidTopDown", 0.377562f) } },
            { "RightBrow3", new[] { new VisemeMapping("m_R3BrowUp", 0.175496f), new VisemeMapping("m_R3BrowDown", -0.280254f), new VisemeMapping("m_R3BrowOut", -0.986670f), new VisemeMapping("m_R3BrowIn", 0.585998f), new VisemeMapping("m_R3BrowRotOut", 0.000000f), new VisemeMapping("m_R3BrowRotIn", 0.000000f) } },
            { "RightEyeLid1Btm", new[] { new VisemeMapping("m_EyeTopInWide", 0.159581f), new VisemeMapping("m_EyeTopInBlink", 0.450141f), new VisemeMapping("m_R1EyeLidLowUp", 0.210226f), new VisemeMapping("m_R1EyeLidLowDown", -0.275908f) } },
            { "RightEyeLid1Top", new[] { new VisemeMapping("m_EyeTopInWide", -0.073183f), new VisemeMapping("m_EyeTopInBlink", 1.202850f), new VisemeMapping("m_R1EyeLidTopUp", -0.328074f), new VisemeMapping("m_R1EyeLidTopDown", 1.102590f) } },
            { "RightBrow1", new[] { new VisemeMapping("m_R1BrowUp", 0.315742f), new VisemeMapping("m_R1BrowDown", -0.438191f), new VisemeMapping("m_R1BrowOut", -0.813430f), new VisemeMapping("m_R1BrowIn", 0.591295f), new VisemeMapping("m_R1BrowRotOut", 0.000000f), new VisemeMapping("m_R1BrowRotIn", 0.000000f) } },
            { "RightBrow2", new[] { new VisemeMapping("m_R2BrowUp", 0.654576f), new VisemeMapping("m_R2BrowDown", -0.847932f), new VisemeMapping("m_R2BrowOut", -0.691819f), new VisemeMapping("m_R2BrowIn", 0.968189f), new VisemeMapping("m_R2BrowRotOut", 0.093332f), new VisemeMapping("m_R2BrowRotIn", 0.000000f) } },
            { "RightEyeLid2Top", new[] { new VisemeMapping("m_EyeTopOutWide", 0.094891f), new VisemeMapping("m_EyeTopOutBlink", 0.116403f), new VisemeMapping("m_R2EyeLidTopUp", -0.058838f), new VisemeMapping("m_R2EyeLidTopDown", 0.236545f) } },
            { "RightEyeLid2Btm", new[] { new VisemeMapping("m_EyeTopOutWide", -0.236202f), new VisemeMapping("m_EyeTopOutBlink", 0.544781f), new VisemeMapping("m_R2EyeLidLowUp", 0.754195f), new VisemeMapping("m_R2EyeLidLowDown", -0.449958f) } },
            { "RightBrow4", new[] { new VisemeMapping("m_R4BrowUp", 0.459908f), new VisemeMapping("m_R4BrowDown", -0.529171f), new VisemeMapping("m_R4BrowOut", -2.319571f), new VisemeMapping("m_R4BrowIn", 2.148589f), new VisemeMapping("m_R4BrowRotOut", 0.000000f), new VisemeMapping("m_R4BrowRotIn", 0.000000f) } },
            { "RightEyeLid4Top", new[] { new VisemeMapping("m_EyeLowOutWide", -0.092134f), new VisemeMapping("m_EyeLowOutBlink", 0.616919f), new VisemeMapping("m_R4EyeLidTopUp", -0.138492f), new VisemeMapping("m_R4EyeLidTopDown", 0.574798f) } },
            { "RightEyeLid4Btm", new[] { new VisemeMapping("m_EyeLowOutWide", -0.107634f), new VisemeMapping("m_EyeLowOutBlink", 0.301818f), new VisemeMapping("m_R4EyeLidLowUp", 0.239602f), new VisemeMapping("m_R4EyeLidLowDown", -0.135381f) } },
            { "RightCheek1", new[] { new VisemeMapping("m_RCheekOut", 0.296659f), new VisemeMapping("m_RCheekIn", -0.240531f), new VisemeMapping("m_RCheekUp", 0.466537f), new VisemeMapping("m_RCheekDown", -0.596921f) } },
            { "RightCheek2", new[] { new VisemeMapping("m_RCheekOut", -2.394999f), new VisemeMapping("m_RCheekIn", 1.941870f), new VisemeMapping("m_RCheekUp", 2.136611f), new VisemeMapping("m_RCheekDown", -2.733738f) } },
            { "MouthTopRight", new[] { new VisemeMapping("m_InMouthContract", 2.148889f), new VisemeMapping("m_InMouthOut", 9.308182f), new VisemeMapping("m_InMouthRelax", -3.555180f), new VisemeMapping("m_InMouthIn", -13.744500f) } },
            { "MouthCenter", new[] { new VisemeMapping("m_InMouthContract", 2.257080f), new VisemeMapping("m_InMouthOut", 4.794481f), new VisemeMapping("m_InMouthRelax", -8.838210f), new VisemeMapping("m_InMouthIn", -14.095700f) } },
            { "NeckTwist", new[] { new VisemeMapping("head_RX+", 0.000000f), new VisemeMapping("head_RX-", 0.000000f), new VisemeMapping("head_RY+", 0.000000f), new VisemeMapping("head_RY-", 0.000000f) } },
        };

        /// <summary>
        /// All viseme animation names used in lip sync for Yahg
        /// Note: Yahg has a unique facial structure with fins, 4 pairs of eyes, and complex jaw/lip system
        /// </summary>
        public static readonly string[] YahgVisemes =
        [
            // Head and neck rotations
            "neck_RX+",
            "neck_RX-",
            "neck_RZ+",
            "neck_RZ-",
            "neck_RY+",
            "neck_RY-",
            "head_RX+",
            "head_RX-",
            "head_RY+",
            "head_RY-",
            "head_RZ+",
            "head_RZ-",
            // Face open/close
            "m_FaceOpen",
            "m_FaceClose",
            // Jaw controls
            "m_JawOpen",
            "m_JawClench",
            // Inner mouth controls
            "m_InMouthContract",
            "m_InMouthOut",
            "m_InMouthRelax",
            "m_InMouthIn",
            // Left lip bottom curls
            "m_BLipCurlLMidOut",
            "m_BLipCurlLMidIn",
            "m_BLipCurlLOut",
            "m_BLipCurlLIn",
            // Right lip bottom curls
            "m_BLipCurlRMidOut",
            "m_BLipCurlRMidIn",
            "m_BLipCurlROut",
            "m_BLipCurlRIn",
            // Bottom lip base
            "m_BBLipCurlOut",
            "m_BBLipCurlIn",
            // Left lip top curls
            "m_LLipCurlTopOut",
            "m_LLipCurlTopIn",
            "m_LLipCurlMidOut",
            "m_LLipCurlMidIn",
            "m_LLipCurlLowOut",
            "m_LLipCurlLowIn",
            // Right lip top curls
            "m_RLipCurlTopOut",
            "m_RLipCurlTopIn",
            "m_RLipCurlMidOut",
            "m_RLipCurlMidIn",
            "m_RLipCurlLowOut",
            "m_RLipCurlLowIn",
            // Left fin controls
            "m_LFinFlare",
            "m_LFinFold",
            "m_LFinRotUp",
            "m_LFinRotDown",
            // Right fin controls
            "m_RFinFlare",
            "m_RFinFold",
            "m_RFinRotUp",
            "m_RFinRotDown",
            // Left chin fin controls
            "m_LCFinUp",
            "m_LCFinDown",
            "m_LCFinSplayOut",
            "m_LCFinSplayIn",
            // Right chin fin controls
            "m_RCFinUp",
            "m_RCFinDown",
            "m_RCFinSplayOut",
            "m_RCFinSplayIn",
            // Eye lid controls (4 pairs of eyes)
            "m_EyeLowInWide",
            "m_EyeLowInBlink",
            "m_EyeTopInWide",
            "m_EyeTopInBlink",
            "m_EyeTopOutWide",
            "m_EyeTopOutBlink",
            "m_EyeLowOutWide",
            "m_EyeLowOutBlink",
            // Left eye 1 (top inner)
            "m_L1EyeLidLowUp",
            "m_L1EyeLidLowDown",
            "m_L1EyeLidTopUp",
            "m_L1EyeLidTopDown",
            // Left eye 2 (top outer)
            "m_L2EyeLidLowUp",
            "m_L2EyeLidLowDown",
            "m_L2EyeLidTopUp",
            "m_L2EyeLidTopDown",
            // Left eye 3 (bottom inner)
            "m_L3EyeLidLowUp",
            "m_L3EyeLidLowDown",
            "m_L3EyeLidTopUp",
            "m_L3EyeLidTopDown",
            // Left eye 4 (bottom outer)
            "m_L4EyeLidLowUp",
            "m_L4EyeLidLowDown",
            "m_L4EyeLidTopUp",
            "m_L4EyeLidTopDown",
            // Right eye 1 (top inner)
            "m_R1EyeLidLowUp",
            "m_R1EyeLidLowDown",
            "m_R1EyeLidTopUp",
            "m_R1EyeLidTopDown",
            // Right eye 2 (top outer)
            "m_R2EyeLidLowUp",
            "m_R2EyeLidLowDown",
            "m_R2EyeLidTopUp",
            "m_R2EyeLidTopDown",
            // Right eye 3 (bottom inner)
            "m_R3EyeLidLowUp",
            "m_R3EyeLidLowDown",
            "m_R3EyeLidTopUp",
            "m_R3EyeLidTopDown",
            // Right eye 4 (bottom outer)
            "m_R4EyeLidLowUp",
            "m_R4EyeLidLowDown",
            "m_R4EyeLidTopUp",
            "m_R4EyeLidTopDown",
            // Brow controls (4 pairs)
            "m_L1BrowUp",
            "m_L1BrowDown",
            "m_L1BrowOut",
            "m_L1BrowIn",
            "m_L1BrowRotOut",
            "m_L1BrowRotIn",
            "m_L2BrowUp",
            "m_L2BrowDown",
            "m_L2BrowOut",
            "m_L2BrowIn",
            "m_L2BrowRotOut",
            "m_L2BrowRotIn",
            "m_L3BrowUp",
            "m_L3BrowDown",
            "m_L3BrowOut",
            "m_L3BrowIn",
            "m_L3BrowRotOut",
            "m_L3BrowRotIn",
            "m_L4BrowUp",
            "m_L4BrowDown",
            "m_L4BrowOut",
            "m_L4BrowIn",
            "m_L4BrowRotOut",
            "m_L4BrowRotIn",
            "m_R1BrowUp",
            "m_R1BrowDown",
            "m_R1BrowOut",
            "m_R1BrowIn",
            "m_R1BrowRotOut",
            "m_R1BrowRotIn",
            "m_R2BrowUp",
            "m_R2BrowDown",
            "m_R2BrowOut",
            "m_R2BrowIn",
            "m_R2BrowRotOut",
            "m_R2BrowRotIn",
            "m_R3BrowUp",
            "m_R3BrowDown",
            "m_R3BrowOut",
            "m_R3BrowIn",
            "m_R3BrowRotOut",
            "m_R3BrowRotIn",
            "m_R4BrowUp",
            "m_R4BrowDown",
            "m_R4BrowOut",
            "m_R4BrowIn",
            "m_R4BrowRotOut",
            "m_R4BrowRotIn",
            // Cheek controls
            "m_LCheekOut",
            "m_LCheekIn",
            "m_LCheekUp",
            "m_LCheekDown",
            "m_RCheekOut",
            "m_RCheekIn",
            "m_RCheekUp",
            "m_RCheekDown"
        ];

        /// <summary>
        /// Human Child phoneme to viseme mappings - from SFX_HumanChild_FaceFX data.
        /// Uses similar morph targets to adult humans but with child-specific values.
        /// </summary>
        public static readonly Dictionary<string, VisemeMapping[]> HumanChildPhonemeMap = new()
        {
            // Human Child phoneme mappings from SFX_HumanChild_FaceFX
            { "Neck", new[] { new VisemeMapping("neck_RX+", 0.000000f), new VisemeMapping("neck_RX-", 0.000000f), new VisemeMapping("neck_RZ+", 0.000000f), new VisemeMapping("neck_RZ-", 0.000000f), new VisemeMapping("neck_RY+", 0.000000f), new VisemeMapping("neck_RY-", 0.000000f) } },
            { "Head", new[] { new VisemeMapping("head_RX+", 0.000000f), new VisemeMapping("head_RX-", 0.000000f), new VisemeMapping("head_RY+", 0.000000f), new VisemeMapping("head_RY-", 0.000000f), new VisemeMapping("head_RZ+", 0.000000f), new VisemeMapping("head_RZ-", 0.000000f) } },
            { "brow_right", new[] { new VisemeMapping("m_CockedBrows_D", 0.360201f), new VisemeMapping("m_EmotionBrows_D", 0.000000f), new VisemeMapping("m_CockedBrows_R", 0.472400f), new VisemeMapping("m_CockedBrows_L", 0.260099f), new VisemeMapping("m_CockedBrows_U", 0.000000f), new VisemeMapping("m_EmotionBrows_R", 0.079000f), new VisemeMapping("m_UpDownBrow_LR", 0.151199f), new VisemeMapping("m_UpDownBrow_UD", 0.000000f), new VisemeMapping("m_EmotionBrows_L", 0.000199f), new VisemeMapping("m_EmotionBrows_U", 0.321300f) } },
            { "eyeBlink_Right", new[] { new VisemeMapping("m_Squint_Eyelids_UD", 0.000000f), new VisemeMapping("m_WideOpen_Eyelids_LR", 0.000000f), new VisemeMapping("m_WideOpen_Eyelids_UD", 0.000000f), new VisemeMapping("m_EyelidsLookat_L", 0.002000f), new VisemeMapping("m_EyelidsLookat_R", 0.001800f), new VisemeMapping("m_EyelidsLookat_U", 0.002000f), new VisemeMapping("m_EyelidsLookat_D", 0.001900f), new VisemeMapping("m_BlinksLookat_UD", 0.000000f), new VisemeMapping("m_BlinksLookat_LR", 0.000000f) } },
            { "jawBone", new[] { new VisemeMapping("m_JawRotate_L", 0.124603f), new VisemeMapping("m_Jaw+", 0.000000f), new VisemeMapping("m_Jaw-", 0.000000f), new VisemeMapping("m_Open_UD", 0.000000f), new VisemeMapping("m_JawRotate_U", 0.000008f), new VisemeMapping("m_JawRotate_D", 0.000008f), new VisemeMapping("m_Open_LR", 0.000000f), new VisemeMapping("m_JawRotate_R", 0.124596f), new VisemeMapping("m_Angry_UD", 0.000008f), new VisemeMapping("m_Smile_Frown_U", 0.000000f), new VisemeMapping("m_Smile_Frown_D", 0.116302f) } },
            { "Tongue", new[] { new VisemeMapping("m_TH", -0.528008f), new VisemeMapping("m_L", -1.654900f), new VisemeMapping("m_Flap", -1.623901f), new VisemeMapping("m_Closed_R", 0.209900f), new VisemeMapping("m_Offset_L", 0.001099f), new VisemeMapping("m_Closed_U", 0.208496f), new VisemeMapping("m_Offset_R", 0.001099f), new VisemeMapping("m_Offset_U", 0.001099f), new VisemeMapping("m_Open_UD", -0.577415f), new VisemeMapping("m_Open_LR", 0.207397f), new VisemeMapping("m_Offset_D", 0.001099f), new VisemeMapping("m_Angry_UD", 0.206993f), new VisemeMapping("m_Smile_Frown_U", 0.181297f), new VisemeMapping("m_Smile_Frown_D", 0.206894f), new VisemeMapping("m_Smile_Frown_L", 0.206100f), new VisemeMapping("m_Smile_Frown_R", 0.206100f), new VisemeMapping("m_Closed_L", 0.206596f), new VisemeMapping("m_Angry_L", 0.149498f), new VisemeMapping("m_Angry_R", 0.149498f) } },
            { "lowerCheek_right", new[] { new VisemeMapping("m_JawRotate_L", 0.000000f), new VisemeMapping("m_Closed_R", 0.473000f), new VisemeMapping("m_Offset_L", -0.000008f), new VisemeMapping("m_Closed_U", 0.178703f), new VisemeMapping("m_Offset_R", 0.000000f), new VisemeMapping("m_Offset_U", -0.467094f), new VisemeMapping("m_Open_UD", -0.367516f), new VisemeMapping("m_Open_LR", -0.000008f), new VisemeMapping("m_Closed_D", 0.000000f), new VisemeMapping("m_Offset_D", 0.522705f), new VisemeMapping("m_Angry_UD", 0.528992f), new VisemeMapping("m_Smile_Frown_U", -0.042305f), new VisemeMapping("m_Smile_Frown_D", 0.748794f), new VisemeMapping("m_Smile_Frown_L", -0.000298f), new VisemeMapping("m_Smile_Frown_R", -0.924004f), new VisemeMapping("m_Closed_L", -0.000008f), new VisemeMapping("m_Angry_R", 0.000000f) } },
            { "LowerCheek_left", new[] { new VisemeMapping("m_JawRotate_L", -0.000404f), new VisemeMapping("m_Closed_R", 0.472900f), new VisemeMapping("m_Offset_L", -0.000008f), new VisemeMapping("m_Closed_U", 0.178703f), new VisemeMapping("m_Offset_R", 0.000000f), new VisemeMapping("m_Offset_U", -0.467094f), new VisemeMapping("m_Open_UD", -0.364616f), new VisemeMapping("m_Open_LR", -0.000015f), new VisemeMapping("m_Closed_D", 0.000000f), new VisemeMapping("m_Offset_D", 0.522705f), new VisemeMapping("m_Angry_UD", 0.528992f), new VisemeMapping("m_Smile_Frown_U", -0.042305f), new VisemeMapping("m_Smile_Frown_D", 0.748787f), new VisemeMapping("m_Smile_Frown_L", -0.923706f), new VisemeMapping("m_Smile_Frown_R", -0.000008f), new VisemeMapping("m_Closed_L", -0.000008f), new VisemeMapping("m_Angry_L", 0.000000f), new VisemeMapping("m_Angry_R", 0.000000f) } },
            { "innerLowLip_left", new[] { new VisemeMapping("m_Closed_R", -0.131004f), new VisemeMapping("m_Offset_L", 0.000000f), new VisemeMapping("m_Closed_U", -0.409996f), new VisemeMapping("m_Offset_R", 0.000008f), new VisemeMapping("m_Offset_U", -0.467102f), new VisemeMapping("m_Open_UD", 0.704094f), new VisemeMapping("m_Closed_D", 0.821503f), new VisemeMapping("m_Offset_D", 0.522698f), new VisemeMapping("m_Angry_UD", 0.000000f), new VisemeMapping("m_Smile_Frown_U", 0.024696f), new VisemeMapping("m_Smile_Frown_D", 0.000008f), new VisemeMapping("m_Smile_Frown_L", -0.000298f), new VisemeMapping("m_Smile_Frown_R", -0.145004f), new VisemeMapping("m_Closed_L", 0.000008f), new VisemeMapping("m_Angry_L", -0.180008f), new VisemeMapping("m_Angry_R", -0.180008f) } },
            { "lowerLip_left", new[] { new VisemeMapping("m_OH", -0.089996f), new VisemeMapping("m_EE", 0.007095f), new VisemeMapping("m_EH", 0.115997f), new VisemeMapping("m_OW", -0.002998f), new VisemeMapping("m_ZZ", 0.000000f), new VisemeMapping("m_TH", -0.226006f), new VisemeMapping("m_N", 0.000000f), new VisemeMapping("m_L", -0.040001f), new VisemeMapping("m_G", -0.002007f), new VisemeMapping("m_Open", -0.191002f), new VisemeMapping("m_Flap", 0.000000f), new VisemeMapping("m_Closed_R", 0.054909f), new VisemeMapping("m_Closed_U", -0.287197f), new VisemeMapping("m_Open_UD", -0.285099f), new VisemeMapping("m_Open_LR", 0.000000f), new VisemeMapping("m_Closed_D", -0.533188f), new VisemeMapping("m_M", -0.101295f), new VisemeMapping("m_FV", -0.349998f), new VisemeMapping("m_Angry_UD", -0.077797f), new VisemeMapping("m_Smile_Frown_U", -0.420601f), new VisemeMapping("m_Smile_Frown_D", -0.111206f), new VisemeMapping("m_Smile_Frown_L", 0.115906f), new VisemeMapping("m_Smile_Frown_R", -0.204994f), new VisemeMapping("m_Closed_L", -0.070999f), new VisemeMapping("m_Angry_L", 0.250900f), new VisemeMapping("m_Angry_R", 0.136902f) } },
            { "innerLowLip_right", new[] { new VisemeMapping("m_Closed_R", -0.131004f), new VisemeMapping("m_Offset_L", -0.000000f), new VisemeMapping("m_Closed_U", -0.409996f), new VisemeMapping("m_Offset_R", 0.000000f), new VisemeMapping("m_Offset_U", -0.467102f), new VisemeMapping("m_Open_UD", 0.704185f), new VisemeMapping("m_Closed_D", 0.821503f), new VisemeMapping("m_Offset_D", 0.522697f), new VisemeMapping("m_Angry_UD", 0.000000f), new VisemeMapping("m_Smile_Frown_U", 0.024696f), new VisemeMapping("m_Smile_Frown_D", -0.000008f), new VisemeMapping("m_Smile_Frown_L", -0.144707f), new VisemeMapping("m_Smile_Frown_R", 0.000000f), new VisemeMapping("m_Closed_L", 0.000008f), new VisemeMapping("m_Angry_L", -0.180008f), new VisemeMapping("m_Angry_R", -0.180008f) } },
            { "lowerLip_right", new[] { new VisemeMapping("m_OH", -0.090004f), new VisemeMapping("m_EE", 0.007103f), new VisemeMapping("m_EH", 0.115997f), new VisemeMapping("m_OW", -0.003006f), new VisemeMapping("m_ZZ", 0.000000f), new VisemeMapping("m_TH", -0.225998f), new VisemeMapping("m_N", 0.000000f), new VisemeMapping("m_L", -0.040001f), new VisemeMapping("m_G", -0.002007f), new VisemeMapping("m_Open", -0.191002f), new VisemeMapping("m_Flap", 0.000000f), new VisemeMapping("m_JawRotate_L", -0.072807f), new VisemeMapping("m_Closed_R", 0.049103f), new VisemeMapping("m_Closed_U", -0.287403f), new VisemeMapping("m_Open_UD", -0.290199f), new VisemeMapping("m_Open_LR", -0.000008f), new VisemeMapping("m_Closed_D", -0.593895f), new VisemeMapping("m_M", -0.101402f), new VisemeMapping("m_FV", -0.362999f), new VisemeMapping("m_Angry_UD", -0.078094f), new VisemeMapping("m_Smile_Frown_U", -0.415901f), new VisemeMapping("m_Smile_Frown_D", -0.111206f), new VisemeMapping("m_Smile_Frown_L", -0.288399f), new VisemeMapping("m_Smile_Frown_R", 0.292793f), new VisemeMapping("m_Closed_L", -0.070999f), new VisemeMapping("m_Angry_L", 0.136406f), new VisemeMapping("m_Angry_R", 0.187202f) } },
            { "cheek_right", new[] { new VisemeMapping("m_OH", 0.494598f), new VisemeMapping("m_EE", 0.000000f), new VisemeMapping("m_EH", 0.219704f), new VisemeMapping("m_OW", -0.163300f), new VisemeMapping("m_ZZ", -0.318306f), new VisemeMapping("m_TH", -0.117302f), new VisemeMapping("m_N", -0.173302f), new VisemeMapping("m_L", -0.085297f), new VisemeMapping("m_G", -0.212303f), new VisemeMapping("m_Open", 0.357597f), new VisemeMapping("m_Flap", 0.205803f), new VisemeMapping("m_JawRotate_L", 1.145897f), new VisemeMapping("m_Closed_R", 0.000000f), new VisemeMapping("m_Offset_L", 0.000000f), new VisemeMapping("m_Closed_U", -0.000000f), new VisemeMapping("m_Offset_R", 0.000000f), new VisemeMapping("m_Offset_U", 0.000000f), new VisemeMapping("m_Open_UD", 1.915703f), new VisemeMapping("m_Open_LR", 0.158203f), new VisemeMapping("m_JawRotate_R", -0.831406f), new VisemeMapping("m_Closed_D", 0.000000f), new VisemeMapping("m_Offset_D", 0.000000f), new VisemeMapping("m_M", -0.337296f), new VisemeMapping("m_FV", -0.163300f), new VisemeMapping("m_Angry_UD", -0.737900f), new VisemeMapping("m_Sneer_UD", -0.407402f), new VisemeMapping("m_Smile_Frown_U", -0.521996f), new VisemeMapping("m_Smile_Frown_D", 0.652802f), new VisemeMapping("m_Smile_Frown_L", 0.000000f), new VisemeMapping("m_Smile_Frown_R", -1.308998f), new VisemeMapping("m_Closed_L", -0.000000f), new VisemeMapping("m_Angry_L", -0.000298f), new VisemeMapping("m_Angry_R", 0.044106f) } },
            { "cheek_left", new[] { new VisemeMapping("m_OH", 0.494598f), new VisemeMapping("m_EE", -0.000008f), new VisemeMapping("m_EH", 0.219704f), new VisemeMapping("m_OW", -0.163300f), new VisemeMapping("m_ZZ", -0.318306f), new VisemeMapping("m_TH", -0.117302f), new VisemeMapping("m_N", -0.173302f), new VisemeMapping("m_L", -0.085297f), new VisemeMapping("m_G", -0.212303f), new VisemeMapping("m_Open", 0.357597f), new VisemeMapping("m_Flap", 0.205795f), new VisemeMapping("m_JawRotate_L", -0.831406f), new VisemeMapping("m_Closed_R", -0.005402f), new VisemeMapping("m_Offset_L", 0.000000f), new VisemeMapping("m_Closed_U", 0.000000f), new VisemeMapping("m_Offset_R", 0.000000f), new VisemeMapping("m_Offset_U", 0.000000f), new VisemeMapping("m_Open_UD", 1.915703f), new VisemeMapping("m_Open_LR", 0.158203f), new VisemeMapping("m_JawRotate_R", 1.146095f), new VisemeMapping("m_Closed_D", 0.000000f), new VisemeMapping("m_Sneer_LR", -0.407303f), new VisemeMapping("m_Offset_D", 0.000000f), new VisemeMapping("m_M", -0.337303f), new VisemeMapping("m_FV", -0.163300f), new VisemeMapping("m_Angry_UD", -0.737709f), new VisemeMapping("m_Smile_Frown_U", -0.522003f), new VisemeMapping("m_Smile_Frown_D", 0.539795f), new VisemeMapping("m_Smile_Frown_L", -1.309006f), new VisemeMapping("m_Smile_Frown_R", 0.000000f), new VisemeMapping("m_Closed_L", -0.000297f), new VisemeMapping("m_Angry_L", 0.044098f), new VisemeMapping("m_Angry_R", -0.000305f) } },
            { "outerUpperLip_left", new[] { new VisemeMapping("m_OH", 0.074104f), new VisemeMapping("m_EE", 0.000000f), new VisemeMapping("m_EH", 0.000000f), new VisemeMapping("m_OW", 0.000000f), new VisemeMapping("m_ZZ", -0.087799f), new VisemeMapping("m_TH", 0.000000f), new VisemeMapping("m_N", 0.000000f), new VisemeMapping("m_G", 0.000000f), new VisemeMapping("m_Open", 0.000000f), new VisemeMapping("m_JawRotate_L", 0.351105f), new VisemeMapping("m_Closed_R", -0.012901f), new VisemeMapping("m_Offset_L", 0.000000f), new VisemeMapping("m_Closed_U", 0.000000f), new VisemeMapping("m_Offset_R", 0.000000f), new VisemeMapping("m_Offset_U", -0.466904f), new VisemeMapping("m_Open_UD", 0.360207f), new VisemeMapping("m_Open_LR", 0.000000f), new VisemeMapping("m_JawRotate_R", -0.509895f), new VisemeMapping("m_Closed_D", 0.000000f), new VisemeMapping("m_Offset_D", 0.522804f), new VisemeMapping("m_M", -0.248802f), new VisemeMapping("m_FV", -0.215797f), new VisemeMapping("m_Angry_UD", -0.323898f), new VisemeMapping("m_Smile_Frown_U", -0.039001f), new VisemeMapping("m_Smile_Frown_D", 0.021301f), new VisemeMapping("m_Smile_Frown_L", -0.685997f), new VisemeMapping("m_Smile_Frown_R", 0.000000f), new VisemeMapping("m_Closed_L", 0.000008f), new VisemeMapping("m_Angry_L", -0.179893f), new VisemeMapping("m_Angry_R", -0.272903f) } },
            { "LipCorner_left", new[] { new VisemeMapping("m_OH", -0.013504f), new VisemeMapping("m_EH", 0.000000f), new VisemeMapping("m_ZZ", 0.000000f), new VisemeMapping("m_TH", 0.000000f), new VisemeMapping("m_N", 0.000000f), new VisemeMapping("m_L", 0.000000f), new VisemeMapping("m_Open", 0.163902f), new VisemeMapping("m_Closed_R", -0.212105f), new VisemeMapping("m_Offset_L", -0.001007f), new VisemeMapping("m_Closed_U", 0.157005f), new VisemeMapping("m_Open_UD", -0.017395f), new VisemeMapping("m_Open_LR", -0.000603f), new VisemeMapping("m_Closed_D", -0.021896f), new VisemeMapping("m_FV", 0.009598f), new VisemeMapping("m_Angry_UD", -0.051994f), new VisemeMapping("m_Smile_Frown_U", 0.014107f), new VisemeMapping("m_Smile_Frown_D", -0.000603f), new VisemeMapping("m_Smile_Frown_L", 0.157196f), new VisemeMapping("m_Smile_Frown_R", -0.033798f), new VisemeMapping("m_Closed_L", -0.052002f), new VisemeMapping("m_Angry_L", -0.109505f) } },
            { "outerUpperLip_right", new[] { new VisemeMapping("m_OH", 0.074104f), new VisemeMapping("m_EE", 0.000000f), new VisemeMapping("m_EH", 0.000000f), new VisemeMapping("m_OW", 0.000000f), new VisemeMapping("m_ZZ", -0.087799f), new VisemeMapping("m_TH", 0.000000f), new VisemeMapping("m_N", 0.000000f), new VisemeMapping("m_L", 0.000000f), new VisemeMapping("m_G", 0.000000f), new VisemeMapping("m_Open", 0.000000f), new VisemeMapping("m_Flap", 0.156303f), new VisemeMapping("m_JawRotate_L", -0.509895f), new VisemeMapping("m_Closed_R", -0.001900f), new VisemeMapping("m_Offset_L", -0.000000f), new VisemeMapping("m_Closed_U", 0.000000f), new VisemeMapping("m_Offset_R", 0.000008f), new VisemeMapping("m_Offset_U", -0.466904f), new VisemeMapping("m_Open_UD", 0.360207f), new VisemeMapping("m_Open_LR", 0.000000f), new VisemeMapping("m_JawRotate_R", 0.302208f), new VisemeMapping("m_Closed_D", 0.000000f), new VisemeMapping("m_Offset_D", 0.522804f), new VisemeMapping("m_M", -0.248802f), new VisemeMapping("m_FV", -0.215797f), new VisemeMapping("m_Angry_UD", -0.323898f), new VisemeMapping("m_Smile_Frown_U", -0.039001f), new VisemeMapping("m_Smile_Frown_D", 0.021301f), new VisemeMapping("m_Smile_Frown_L", 0.000000f), new VisemeMapping("m_Smile_Frown_R", -0.418793f), new VisemeMapping("m_Closed_L", 0.000008f), new VisemeMapping("m_Angry_L", -0.272896f), new VisemeMapping("m_Angry_R", -0.179901f) } },
            { "LipCorner_right", new[] { new VisemeMapping("m_OH", -0.013802f), new VisemeMapping("m_EH", -0.000694f), new VisemeMapping("m_ZZ", 0.000000f), new VisemeMapping("m_TH", -0.000008f), new VisemeMapping("m_N", 0.000000f), new VisemeMapping("m_L", 0.000000f), new VisemeMapping("m_Open", 0.163597f), new VisemeMapping("m_Closed_R", -0.210999f), new VisemeMapping("m_Offset_L", -0.001007f), new VisemeMapping("m_Closed_U", 0.156105f), new VisemeMapping("m_Open_UD", -0.017601f), new VisemeMapping("m_Open_LR", 0.000000f), new VisemeMapping("m_Closed_D", -0.021904f), new VisemeMapping("m_FV", 0.009300f), new VisemeMapping("m_Angry_UD", -0.051399f), new VisemeMapping("m_Smile_Frown_U", 0.015808f), new VisemeMapping("m_Smile_Frown_D", -0.000092f), new VisemeMapping("m_Smile_Frown_L", -0.034607f), new VisemeMapping("m_Smile_Frown_R", 0.156403f), new VisemeMapping("m_Closed_L", -0.052010f), new VisemeMapping("m_Angry_R", -0.109604f) } },
            { "innerUpperLip_right", new[] { new VisemeMapping("m_JawRotate_L", -0.644997f), new VisemeMapping("m_Closed_R", -1.462006f), new VisemeMapping("m_Offset_L", -0.000000f), new VisemeMapping("m_Closed_U", 0.173500f), new VisemeMapping("m_Offset_R", 0.000000f), new VisemeMapping("m_Offset_U", -0.467102f), new VisemeMapping("m_Open_UD", 1.259995f), new VisemeMapping("m_JawRotate_R", 0.841400f), new VisemeMapping("m_Closed_D", 0.000000f), new VisemeMapping("m_Offset_D", 0.522697f), new VisemeMapping("m_Angry_UD", 0.000000f), new VisemeMapping("m_Sneer_UD", -2.915001f), new VisemeMapping("m_Smile_Frown_U", -0.073997f), new VisemeMapping("m_Smile_Frown_D", 0.116203f), new VisemeMapping("m_Smile_Frown_L", 0.388100f), new VisemeMapping("m_Smile_Frown_R", 0.791100f), new VisemeMapping("m_Closed_L", 0.000000f), new VisemeMapping("m_Angry_L", 1.728493f), new VisemeMapping("m_Angry_R", -0.180000f) } },
            { "upperLip_right", new[] { new VisemeMapping("m_OH", 0.108101f), new VisemeMapping("m_EE", 0.000000f), new VisemeMapping("m_EH", 0.074898f), new VisemeMapping("m_OW", 0.082901f), new VisemeMapping("m_ZZ", 0.000000f), new VisemeMapping("m_TH", 0.025101f), new VisemeMapping("m_N", 0.003799f), new VisemeMapping("m_L", -0.000000f), new VisemeMapping("m_G", 0.067101f), new VisemeMapping("m_Open", 0.074799f), new VisemeMapping("m_Flap", 0.059097f), new VisemeMapping("m_JawRotate_L", 0.247002f), new VisemeMapping("m_Closed_R", 0.033096f), new VisemeMapping("m_Closed_U", -0.110199f), new VisemeMapping("m_Open_UD", -0.870102f), new VisemeMapping("m_Open_LR", 0.000000f), new VisemeMapping("m_JawRotate_R", -0.310890f), new VisemeMapping("m_Closed_D", 0.032501f), new VisemeMapping("m_M", -0.133003f), new VisemeMapping("m_FV", -0.018196f), new VisemeMapping("m_Angry_UD", 0.200104f), new VisemeMapping("m_Sneer_UD", 2.913300f), new VisemeMapping("m_Smile_Frown_U", -0.248901f), new VisemeMapping("m_Smile_Frown_D", -0.064400f), new VisemeMapping("m_Smile_Frown_L", -0.206406f), new VisemeMapping("m_Smile_Frown_R", -0.496185f), new VisemeMapping("m_Closed_L", 0.000000f), new VisemeMapping("m_Angry_L", -0.515709f), new VisemeMapping("m_Angry_R", 0.020309f) } },
            { "innerUpperLip_left", new[] { new VisemeMapping("m_JawRotate_L", 0.841308f), new VisemeMapping("m_Closed_R", -1.438995f), new VisemeMapping("m_Offset_L", -0.000000f), new VisemeMapping("m_Closed_U", 0.173500f), new VisemeMapping("m_Offset_R", 0.000000f), new VisemeMapping("m_Offset_U", -0.467102f), new VisemeMapping("m_Open_UD", 1.625000f), new VisemeMapping("m_JawRotate_R", -0.644997f), new VisemeMapping("m_Closed_D", 0.000000f), new VisemeMapping("m_Sneer_LR", -2.930000f), new VisemeMapping("m_Offset_D", 0.522697f), new VisemeMapping("m_Angry_UD", 0.000000f), new VisemeMapping("m_Smile_Frown_U", -0.073997f), new VisemeMapping("m_Smile_Frown_D", 0.116203f), new VisemeMapping("m_Smile_Frown_L", 0.791100f), new VisemeMapping("m_Smile_Frown_R", 0.388100f), new VisemeMapping("m_Closed_L", 0.000000f), new VisemeMapping("m_Angry_L", -0.180000f), new VisemeMapping("m_Angry_R", 1.728493f) } },
            { "upperLip_left", new[] { new VisemeMapping("m_OH", 0.108101f), new VisemeMapping("m_EE", -0.000008f), new VisemeMapping("m_EH", 0.074898f), new VisemeMapping("m_OW", 0.082893f), new VisemeMapping("m_ZZ", -0.000008f), new VisemeMapping("m_TH", 0.001099f), new VisemeMapping("m_N", 0.003799f), new VisemeMapping("m_L", 0.000000f), new VisemeMapping("m_G", 0.067101f), new VisemeMapping("m_Open", 0.074799f), new VisemeMapping("m_Flap", 0.059097f), new VisemeMapping("m_JawRotate_L", -0.303902f), new VisemeMapping("m_Closed_R", 0.028702f), new VisemeMapping("m_Closed_U", -0.102501f), new VisemeMapping("m_Open_UD", -0.961006f), new VisemeMapping("m_Open_LR", 0.000000f), new VisemeMapping("m_JawRotate_R", 0.311195f), new VisemeMapping("m_Closed_D", 0.032997f), new VisemeMapping("m_Sneer_LR", 2.928299f), new VisemeMapping("m_M", -0.132996f), new VisemeMapping("m_FV", -0.018295f), new VisemeMapping("m_Angry_UD", 0.200294f), new VisemeMapping("m_Smile_Frown_U", -0.249008f), new VisemeMapping("m_Smile_Frown_D", -0.064407f), new VisemeMapping("m_Smile_Frown_L", -0.625107f), new VisemeMapping("m_Smile_Frown_R", -0.023506f), new VisemeMapping("m_Closed_L", 0.000000f), new VisemeMapping("m_Angry_L", 0.019997f), new VisemeMapping("m_Angry_R", -0.476913f) } },
            { "eye_Right", new[] { new VisemeMapping("eye_Right_RX+", 0.000000f), new VisemeMapping("eye_Right_RX-", 0.000000f), new VisemeMapping("eye_Right_RY+", 0.000000f), new VisemeMapping("eye_Right_RY-", 0.000000f), new VisemeMapping("eye_Right_RZ+", 0.000000f), new VisemeMapping("eye_Right_RZ-", 0.000000f) } },
            { "eye_Left", new[] { new VisemeMapping("eye_Left_RX+", 0.000000f), new VisemeMapping("eye_Left_RX-", 0.000000f), new VisemeMapping("eye_Left_RY+", 0.000000f), new VisemeMapping("eye_Left_RY-", 0.000000f), new VisemeMapping("eye_Left_RZ+", 0.000000f), new VisemeMapping("eye_Left_RZ-", 0.000000f) } },
            { "lowLid_Right", new[] { new VisemeMapping("m_Squint_Eyelids_UD", 0.000000f), new VisemeMapping("m_WideOpen_Eyelids_LR", 0.000000f), new VisemeMapping("m_WideOpen_Eyelids_UD", 0.000000f), new VisemeMapping("m_EyelidsLookat_L", 0.002000f), new VisemeMapping("m_EyelidsLookat_R", 0.001800f), new VisemeMapping("m_EyelidsLookat_U", 0.002000f), new VisemeMapping("m_EyelidsLookat_D", 0.001900f), new VisemeMapping("m_BlinksLookat_UD", 0.000000f), new VisemeMapping("m_BlinksLookat_LR", 0.000000f) } },
            { "eyeBlink_Left", new[] { new VisemeMapping("m_Squint_Eyelids_LR", 0.000000f), new VisemeMapping("m_WideOpen_Eyelids_LR", 0.000000f), new VisemeMapping("m_WideOpen_Eyelids_UD", 0.000000f), new VisemeMapping("m_EyelidsLookat_L", 0.001600f), new VisemeMapping("m_EyelidsLookat_R", 0.001800f), new VisemeMapping("m_EyelidsLookat_U", 0.001800f), new VisemeMapping("m_EyelidsLookat_D", 0.001700f), new VisemeMapping("m_BlinksLookat_UD", 0.000000f), new VisemeMapping("m_BlinksLookat_LR", 0.000000f) } },
            { "lowLid_Left", new[] { new VisemeMapping("m_Squint_Eyelids_LR", 0.000000f), new VisemeMapping("m_WideOpen_Eyelids_LR", 0.000000f), new VisemeMapping("m_WideOpen_Eyelids_UD", 0.000000f), new VisemeMapping("m_EyelidsLookat_L", 0.001600f), new VisemeMapping("m_EyelidsLookat_R", 0.001800f), new VisemeMapping("m_EyelidsLookat_U", 0.001800f), new VisemeMapping("m_EyelidsLookat_D", 0.001800f), new VisemeMapping("m_BlinksLookat_UD", 0.000000f), new VisemeMapping("m_BlinksLookat_LR", 0.000000f) } },
            { "brow_left", new[] { new VisemeMapping("m_CockedBrows_D", -0.000099f), new VisemeMapping("m_EmotionBrows_D", 0.000000f), new VisemeMapping("m_CockedBrows_R", 0.259900f), new VisemeMapping("m_CockedBrows_L", 0.472300f), new VisemeMapping("m_CockedBrows_U", 0.360300f), new VisemeMapping("m_EmotionBrows_R", 0.213200f), new VisemeMapping("m_UpDownBrow_LR", 0.151300f), new VisemeMapping("m_UpDownBrow_UD", 0.000000f), new VisemeMapping("m_EmotionBrows_L", 0.000199f), new VisemeMapping("m_EmotionBrows_U", 0.321300f) } },
            { "outBrow_left", new[] { new VisemeMapping("m_CockedBrows_D", 0.000000f), new VisemeMapping("m_EmotionBrows_D", 0.000000f), new VisemeMapping("m_CockedBrows_R", 0.129100f), new VisemeMapping("m_CockedBrows_L", -0.039700f), new VisemeMapping("m_CockedBrows_U", -0.000000f), new VisemeMapping("m_EmotionBrows_R", -0.000000f), new VisemeMapping("m_UpDownBrow_LR", 0.105200f), new VisemeMapping("m_UpDownBrow_UD", -0.000000f), new VisemeMapping("m_EmotionBrows_L", -0.000900f), new VisemeMapping("m_EmotionBrows_U", -0.094901f) } },
            { "outBrow_Right", new[] { new VisemeMapping("m_CockedBrows_D", -0.000000f), new VisemeMapping("m_EmotionBrows_D", 0.000000f), new VisemeMapping("m_CockedBrows_R", -0.039500f), new VisemeMapping("m_CockedBrows_L", 0.129500f), new VisemeMapping("m_CockedBrows_U", 0.000000f), new VisemeMapping("m_EmotionBrows_R", 0.000000f), new VisemeMapping("m_UpDownBrow_LR", 0.105500f), new VisemeMapping("m_UpDownBrow_UD", 0.000000f), new VisemeMapping("m_EmotionBrows_L", -0.000900f), new VisemeMapping("m_EmotionBrows_U", -0.094600f) } },
            { "underEye_left", new[] { new VisemeMapping("m_Closed_R", 0.405500f), new VisemeMapping("m_Closed_U", 0.171000f), new VisemeMapping("m_Open_UD", -0.467500f), new VisemeMapping("m_UpDownBrow_LR", 0.314700f), new VisemeMapping("m_Smile_Frown_U", 1.168500f), new VisemeMapping("m_Smile_Frown_D", 0.063800f), new VisemeMapping("m_Smile_Frown_L", 0.706201f), new VisemeMapping("m_Closed_L", 0.454300f), new VisemeMapping("m_Angry_L", 0.783999f) } },
            { "underEye_Right", new[] { new VisemeMapping("m_Closed_R", 0.398400f), new VisemeMapping("m_Closed_U", 0.171300f), new VisemeMapping("m_Open_UD", -0.467200f), new VisemeMapping("m_UpDownBrow_LR", 0.315101f), new VisemeMapping("m_Smile_Frown_U", 1.168500f), new VisemeMapping("m_Smile_Frown_D", 0.063800f), new VisemeMapping("m_Smile_Frown_R", 0.706600f), new VisemeMapping("m_Closed_L", 0.454300f), new VisemeMapping("m_Angry_L", 0.224200f) } },
            { "Sneer", new[] { new VisemeMapping("m_CockedBrows_D", -0.000001f), new VisemeMapping("m_EmotionBrows_D", -0.000001f), new VisemeMapping("m_CockedBrows_R", 0.000000f), new VisemeMapping("m_CockedBrows_L", 0.000000f), new VisemeMapping("m_CockedBrows_U", -0.000001f), new VisemeMapping("m_EmotionBrows_R", -0.001201f), new VisemeMapping("m_UpDownBrow_LR", 0.000000f), new VisemeMapping("m_UpDownBrow_UD", -0.002400f), new VisemeMapping("m_EmotionBrows_L", 0.005300f), new VisemeMapping("m_EmotionBrows_U", 0.823400f) } },
        };

        /// <summary>
        /// All viseme animation names used in lip sync for Human Child
        /// Similar to adult human visemes
        /// </summary>
        public static readonly string[] HumanChildVisemes =
        [
            // Head and neck rotations
            "neck_RX+",
            "neck_RX-",
            "neck_RZ+",
            "neck_RZ-",
            "neck_RY+",
            "neck_RY-",
            "head_RX+",
            "head_RX-",
            "head_RY+",
            "head_RY-",
            "head_RZ+",
            "head_RZ-",
            // Brow controls
            "m_CockedBrows_D",
            "m_CockedBrows_R",
            "m_CockedBrows_L",
            "m_CockedBrows_U",
            "m_EmotionBrows_D",
            "m_EmotionBrows_R",
            "m_EmotionBrows_L",
            "m_EmotionBrows_U",
            "m_UpDownBrow_LR",
            "m_UpDownBrow_UD",
            // Eyelid controls
            "m_Squint_Eyelids_LR",
            "m_Squint_Eyelids_UD",
            "m_WideOpen_Eyelids_LR",
            "m_WideOpen_Eyelids_UD",
            "m_EyelidsLookat_L",
            "m_EyelidsLookat_R",
            "m_EyelidsLookat_U",
            "m_EyelidsLookat_D",
            "m_BlinksLookat_UD",
            "m_BlinksLookat_LR",
            // Jaw controls
            "m_JawRotate_L",
            "m_JawRotate_R",
            "m_JawRotate_U",
            "m_JawRotate_D",
            "m_Jaw+",
            "m_Jaw-",
            "m_Open_UD",
            "m_Open_LR",
            "m_Open",
            // Mouth phoneme shapes
            "m_OH",
            "m_EE",
            "m_EH",
            "m_OW",
            "m_ZZ",
            "m_TH",
            "m_N",
            "m_L",
            "m_G",
            "m_M",
            "m_FV",
            "m_Flap",
            // Closed/Offset controls
            "m_Closed_R",
            "m_Closed_L",
            "m_Closed_U",
            "m_Closed_D",
            "m_Offset_L",
            "m_Offset_R",
            "m_Offset_U",
            "m_Offset_D",
            // Expression controls
            "m_Angry_UD",
            "m_Angry_L",
            "m_Angry_R",
            "m_Smile_Frown_U",
            "m_Smile_Frown_D",
            "m_Smile_Frown_L",
            "m_Smile_Frown_R",
            "m_Sneer_UD",
            "m_Sneer_LR",
            // Eye rotations
            "eye_Right_RX+",
            "eye_Right_RX-",
            "eye_Right_RY+",
            "eye_Right_RY-",
            "eye_Right_RZ+",
            "eye_Right_RZ-",
            "eye_Left_RX+",
            "eye_Left_RX-",
            "eye_Left_RY+",
            "eye_Left_RY-",
            "eye_Left_RZ+",
            "eye_Left_RZ-"
        ];

        /// <summary>
        /// Quarian phoneme to viseme mappings - from SFX_Quarian_FaceFX data.
        /// Note: Quarians wear environmental suits with helmets, so their facial animations
        /// are simplified to primarily use "jawOpen" for all phonemes visible through the visor.
        /// </summary>
        public static readonly Dictionary<string, VisemeMapping[]> QuarianPhonemeMap = new()
        {
            // Quarian phoneme mappings from SFX_Quarian_FaceFX
            // All mappings use jawOpen since the face is behind a helmet visor
            { "brow_left", new[] { new VisemeMapping("jawOpen", 0.524650f) } },
            { "brow_right", new[] { new VisemeMapping("jawOpen", 0.719121f) } },
            { "cheek_left", new[] { new VisemeMapping("jawOpen", 0.908336f) } },
            { "cheek_right", new[] { new VisemeMapping("jawOpen", 0.529906f) } },
            { "Chest", new[] { new VisemeMapping("jawOpen", 0.093661f) } },
            { "Chest1", new[] { new VisemeMapping("jawOpen", 0.834752f) } },
            { "Chest2", new[] { new VisemeMapping("jawOpen", 0.734889f) } },
            { "eye_Left", new[] { new VisemeMapping("jawOpen", 0.387995f) } },
            { "eye_Right", new[] { new VisemeMapping("jawOpen", 0.750657f) } },
            { "eyeBlink_Left", new[] { new VisemeMapping("jawOpen", 0.345948f) } },
            { "eyeBlink_Right", new[] { new VisemeMapping("jawOpen", 0.866288f) } },
            { "GOD", new[] { new VisemeMapping("jawOpen", 0.529906f) } },
            { "Head", new[] { new VisemeMapping("jawOpen", 0.424787f) } },
            { "HeadBase", new[] { new VisemeMapping("jawOpen", 0.309156f) } },
            { "innerLowLip_left", new[] { new VisemeMapping("jawOpen", 0.298644f) } },
            { "innerLowLip_right", new[] { new VisemeMapping("jawOpen", 0.514139f) } },
            { "innerUpperLip_left", new[] { new VisemeMapping("jawOpen", 0.734889f) } },
            { "innerUpperLip_right", new[] { new VisemeMapping("jawOpen", 0.219804f) } },
            { "jawBone", new[] { new VisemeMapping("jawOpen", 0.650794f) } },
            { "LeftCollar", new[] { new VisemeMapping("jawOpen", 0.687585f) } },
            { "LeftElbow", new[] { new VisemeMapping("jawOpen", 0.666561f) } },
            { "LeftElbowTwist1", new[] { new VisemeMapping("jawOpen", 0.571954f) } },
            { "LeftIndexFinger", new[] { new VisemeMapping("jawOpen", 0.645538f) } },
            { "LeftIndexFinger1", new[] { new VisemeMapping("jawOpen", 0.430043f) } },
            { "LeftIndexFinger2", new[] { new VisemeMapping("jawOpen", 0.629770f) } },
            { "LeftMiddleFinger", new[] { new VisemeMapping("jawOpen", 0.719121f) } },
            { "LeftMiddleFinger1", new[] { new VisemeMapping("jawOpen", 0.477347f) } },
            { "LeftMiddleFinger2", new[] { new VisemeMapping("jawOpen", 0.677073f) } },
            { "LeftPinkFinger", new[] { new VisemeMapping("jawOpen", 0.724377f) } },
            { "LeftPinkFinger1", new[] { new VisemeMapping("jawOpen", 0.924104f) } },
            { "LeftPinkFinger2", new[] { new VisemeMapping("jawOpen", 0.955640f) } },
            { "LeftRingFinger", new[] { new VisemeMapping("jawOpen", 0.393251f) } },
            { "LeftRingFinger1", new[] { new VisemeMapping("jawOpen", 0.959360f) } },
            { "LeftRingFinger2", new[] { new VisemeMapping("jawOpen", 0.871544f) } },
            { "LeftShoulder", new[] { new VisemeMapping("jawOpen", 0.656050f) } },
            { "LeftThumbFinger", new[] { new VisemeMapping("jawOpen", 0.303900f) } },
            { "LeftThumbFinger1", new[] { new VisemeMapping("jawOpen", 0.193525f) } },
            { "LeftThumbFinger2", new[] { new VisemeMapping("jawOpen", 0.582466f) } },
            { "LeftWrist", new[] { new VisemeMapping("jawOpen", 0.824240f) } },
            { "LipCorner_left", new[] { new VisemeMapping("jawOpen", 0.472091f) } },
            { "LipCorner_right", new[] { new VisemeMapping("jawOpen", 0.324924f) } },
            { "LowerBack", new[] { new VisemeMapping("jawOpen", 0.256596f) } },
            { "LowerCheek_left", new[] { new VisemeMapping("jawOpen", 0.298644f) } },
            { "lowerCheek_right", new[] { new VisemeMapping("jawOpen", 0.924104f) } },
            { "lowerLip_left", new[] { new VisemeMapping("jawOpen", 0.850520f) } },
            { "lowerLip_right", new[] { new VisemeMapping("jawOpen", 0.083149f) } },
            { "lowLid_Left", new[] { new VisemeMapping("jawOpen", 0.708609f) } },
            { "lowLid_Right", new[] { new VisemeMapping("jawOpen", 0.508883f) } },
            { "MouthBase", new[] { new VisemeMapping("jawOpen", 0.729633f) } },
            { "Neck", new[] { new VisemeMapping("jawOpen", 1.000000f) } },
            { "Neck1", new[] { new VisemeMapping("jawOpen", 0.535162f) } },
            { "outBrow_left", new[] { new VisemeMapping("jawOpen", 0.703353f) } },
            { "outBrow_Right", new[] { new VisemeMapping("jawOpen", 0.939872f) } },
            { "outerUpperLip_left", new[] { new VisemeMapping("jawOpen", 0.324924f) } },
            { "outerUpperLip_right", new[] { new VisemeMapping("jawOpen", 0.761169f) } },
            { "Prop02", new[] { new VisemeMapping("jawOpen", 0.629770f) } },
            { "Root", new[] { new VisemeMapping("jawOpen", 0.818985f) } },
            { "SFX_Quarian", new[] { new VisemeMapping("jawOpen", 0.014822f) } },
            { "sneer", new[] { new VisemeMapping("jawOpen", 0.508883f) } },
            { "Socket_02", new[] { new VisemeMapping("jawOpen", 0.603490f) } },
            { "tongue", new[] { new VisemeMapping("jawOpen", 0.903080f) } },
            { "underEye_left", new[] { new VisemeMapping("jawOpen", 0.409019f) } },
            { "underEye_Right", new[] { new VisemeMapping("jawOpen", 0.277620f) } },
            { "upperLip_left", new[] { new VisemeMapping("jawOpen", 0.876800f) } },
            { "upperLip_right", new[] { new VisemeMapping("jawOpen", 0.866288f) } },
        };

        /// <summary>
        /// All viseme animation names used in lip sync for Quarian
        /// Note: Quarians use a simplified system with only jawOpen due to wearing helmets
        /// </summary>
        public static readonly string[] QuarianVisemes =
        [
            // Quarians primarily use jawOpen for all facial animations
            // since their face is behind a helmet visor
            "jawOpen"
        ];

        /// <summary>
        /// Geth phoneme to viseme mappings - from SFX_Legion_FaceFX data.
        /// Note: Geth (like Legion) communicate through their eye/lamp "blinker" and
        /// head plate movements rather than traditional mouth movements.
        /// Standard phonemes are mapped to blinker, head orientation, gaze, and gesture animations.
        /// </summary>
        public static readonly Dictionary<string, VisemeMapping[]> GethPhonemeMap = new()
        {
            // Geth use a "blinker" system - their eye lamp flickers when speaking
            // They also have head/gaze movements and gesture animations during speech

            // Silence - minimal activity, slight idle movement
            { "SIL", new[]
            {
                new VisemeMapping("blinker", 0.1f),
                new VisemeMapping("G_TalkingNormal", 0.05f),
                new VisemeMapping("E_defaultNoiseLoop", 0.1f),
            }},

            // Vowels - high activity across all animations
            { "AA", new[]
            {
                new VisemeMapping("blinker", 0.9f),
                new VisemeMapping("G_TalkingNormal", 0.8f),
                new VisemeMapping("E_defaultNoiseLoop", 0.3f),
                new VisemeMapping("Emphasis_Head_Pitch", 0.4f),
                new VisemeMapping("Emphasis_Head_Yaw", 0.2f),
            }},
            { "AE", new[]
            {
                new VisemeMapping("blinker", 0.85f),
                new VisemeMapping("G_TalkingNormal", 0.75f),
                new VisemeMapping("E_defaultNoiseLoop", 0.25f),
                new VisemeMapping("Emphasis_Head_Pitch", 0.35f),
            }},
            { "AH", new[]
            {
                new VisemeMapping("blinker", 0.8f),
                new VisemeMapping("G_TalkingNormal", 0.7f),
                new VisemeMapping("E_defaultNoiseLoop", 0.2f),
                new VisemeMapping("Emphasis_Head_Pitch", 0.3f),
            }},
            { "AO", new[]
            {
                new VisemeMapping("blinker", 0.85f),
                new VisemeMapping("G_TalkingNormal", 0.75f),
                new VisemeMapping("E_defaultNoiseLoop", 0.25f),
                new VisemeMapping("E_Neutral_Thoughtfull", 0.2f),
            }},
            { "AW", new[]
            {
                new VisemeMapping("blinker", 0.9f),
                new VisemeMapping("G_TalkingNormal", 0.8f),
                new VisemeMapping("E_defaultNoiseLoop", 0.3f),
                new VisemeMapping("Emphasis_Head_Roll", 0.2f),
            }},
            { "AY", new[]
            {
                new VisemeMapping("blinker", 0.9f),
                new VisemeMapping("G_TalkingNormal", 0.8f),
                new VisemeMapping("E_defaultNoiseLoop", 0.3f),
                new VisemeMapping("Gaze_Eye_Pitch", 0.15f),
            }},
            { "EH", new[]
            {
                new VisemeMapping("blinker", 0.8f),
                new VisemeMapping("G_TalkingNormal", 0.7f),
                new VisemeMapping("E_defaultNoiseLoop", 0.2f),
            }},
            { "ER", new[]
            {
                new VisemeMapping("blinker", 0.75f),
                new VisemeMapping("G_TalkingNormal", 0.65f),
                new VisemeMapping("E_defaultNoiseLoop", 0.2f),
                new VisemeMapping("E_Neutral_Thoughtfull", 0.15f),
            }},
            { "EY", new[]
            {
                new VisemeMapping("blinker", 0.85f),
                new VisemeMapping("G_TalkingNormal", 0.75f),
                new VisemeMapping("E_defaultNoiseLoop", 0.25f),
                new VisemeMapping("Gaze_Eye_Yaw", 0.1f),
            }},
            { "IH", new[]
            {
                new VisemeMapping("blinker", 0.75f),
                new VisemeMapping("G_TalkingNormal", 0.65f),
                new VisemeMapping("E_defaultNoiseLoop", 0.2f),
            }},
            { "IY", new[]
            {
                new VisemeMapping("blinker", 0.8f),
                new VisemeMapping("G_TalkingNormal", 0.7f),
                new VisemeMapping("E_defaultNoiseLoop", 0.2f),
                new VisemeMapping("Emphasis_Head_Pitch", 0.25f),
            }},
            { "OW", new[]
            {
                new VisemeMapping("blinker", 0.85f),
                new VisemeMapping("G_TalkingNormal", 0.75f),
                new VisemeMapping("E_defaultNoiseLoop", 0.25f),
                new VisemeMapping("E_GESTURE_HeadLeft", 0.15f),
            }},
            { "OY", new[]
            {
                new VisemeMapping("blinker", 0.9f),
                new VisemeMapping("G_TalkingNormal", 0.8f),
                new VisemeMapping("E_defaultNoiseLoop", 0.3f),
                new VisemeMapping("Emphasis_Head_Yaw", 0.2f),
            }},
            { "UH", new[]
            {
                new VisemeMapping("blinker", 0.7f),
                new VisemeMapping("G_TalkingNormal", 0.6f),
                new VisemeMapping("E_defaultNoiseLoop", 0.15f),
            }},
            { "UW", new[]
            {
                new VisemeMapping("blinker", 0.75f),
                new VisemeMapping("G_TalkingNormal", 0.65f),
                new VisemeMapping("E_defaultNoiseLoop", 0.2f),
                new VisemeMapping("E_GESTURE_NeckForwardLeft", 0.1f),
            }},

            // Consonants - varying activity levels
            // Stops (plosives) - quick burst of activity
            { "B", new[]
            {
                new VisemeMapping("blinker", 0.7f),
                new VisemeMapping("G_TalkingNormal", 0.6f),
                new VisemeMapping("E_defaultNoiseLoop", 0.15f),
                new VisemeMapping("Emphasis_Head_Pitch", 0.2f),
            }},
            { "D", new[]
            {
                new VisemeMapping("blinker", 0.65f),
                new VisemeMapping("G_TalkingNormal", 0.55f),
                new VisemeMapping("E_defaultNoiseLoop", 0.15f),
            }},
            { "G", new[]
            {
                new VisemeMapping("blinker", 0.65f),
                new VisemeMapping("G_TalkingNormal", 0.55f),
                new VisemeMapping("E_defaultNoiseLoop", 0.15f),
                new VisemeMapping("E_GESTURE_HeadRollLeft", 0.1f),
            }},
            { "K", new[]
            {
                new VisemeMapping("blinker", 0.6f),
                new VisemeMapping("G_TalkingNormal", 0.5f),
                new VisemeMapping("E_defaultNoiseLoop", 0.1f),
            }},
            { "P", new[]
            {
                new VisemeMapping("blinker", 0.6f),
                new VisemeMapping("G_TalkingNormal", 0.5f),
                new VisemeMapping("E_defaultNoiseLoop", 0.1f),
            }},
            { "T", new[]
            {
                new VisemeMapping("blinker", 0.6f),
                new VisemeMapping("G_TalkingNormal", 0.5f),
                new VisemeMapping("E_defaultNoiseLoop", 0.1f),
            }},

            // Fricatives - sustained activity
            { "CH", new[]
            {
                new VisemeMapping("blinker", 0.7f),
                new VisemeMapping("G_TalkingNormal", 0.6f),
                new VisemeMapping("E_defaultNoiseLoop", 0.2f),
                new VisemeMapping("Emphasis_Head_Roll", 0.15f),
            }},
            { "DH", new[]
            {
                new VisemeMapping("blinker", 0.65f),
                new VisemeMapping("G_TalkingNormal", 0.55f),
                new VisemeMapping("E_defaultNoiseLoop", 0.15f),
            }},
            { "F", new[]
            {
                new VisemeMapping("blinker", 0.5f),
                new VisemeMapping("G_TalkingNormal", 0.4f),
                new VisemeMapping("E_defaultNoiseLoop", 0.1f),
            }},
            { "HH", new[]
            {
                new VisemeMapping("blinker", 0.4f),
                new VisemeMapping("G_TalkingNormal", 0.3f),
                new VisemeMapping("E_defaultNoiseLoop", 0.1f),
                new VisemeMapping("E_Neutral_Thoughtfull", 0.1f),
            }},
            { "JH", new[]
            {
                new VisemeMapping("blinker", 0.7f),
                new VisemeMapping("G_TalkingNormal", 0.6f),
                new VisemeMapping("E_defaultNoiseLoop", 0.2f),
            }},
            { "S", new[]
            {
                new VisemeMapping("blinker", 0.55f),
                new VisemeMapping("G_TalkingNormal", 0.45f),
                new VisemeMapping("E_defaultNoiseLoop", 0.15f),
            }},
            { "SH", new[]
            {
                new VisemeMapping("blinker", 0.6f),
                new VisemeMapping("G_TalkingNormal", 0.5f),
                new VisemeMapping("E_defaultNoiseLoop", 0.15f),
            }},
            { "TH", new[]
            {
                new VisemeMapping("blinker", 0.5f),
                new VisemeMapping("G_TalkingNormal", 0.4f),
                new VisemeMapping("E_defaultNoiseLoop", 0.1f),
            }},
            { "V", new[]
            {
                new VisemeMapping("blinker", 0.6f),
                new VisemeMapping("G_TalkingNormal", 0.5f),
                new VisemeMapping("E_defaultNoiseLoop", 0.15f),
            }},
            { "Z", new[]
            {
                new VisemeMapping("blinker", 0.65f),
                new VisemeMapping("G_TalkingNormal", 0.55f),
                new VisemeMapping("E_defaultNoiseLoop", 0.2f),
            }},
            { "ZH", new[]
            {
                new VisemeMapping("blinker", 0.65f),
                new VisemeMapping("G_TalkingNormal", 0.55f),
                new VisemeMapping("E_defaultNoiseLoop", 0.2f),
            }},

            // Nasals - moderate activity
            { "M", new[]
            {
                new VisemeMapping("blinker", 0.6f),
                new VisemeMapping("G_TalkingNormal", 0.5f),
                new VisemeMapping("E_defaultNoiseLoop", 0.15f),
                new VisemeMapping("E_GESTURE_NeckBackLeft", 0.1f),
            }},
            { "N", new[]
            {
                new VisemeMapping("blinker", 0.55f),
                new VisemeMapping("G_TalkingNormal", 0.45f),
                new VisemeMapping("E_defaultNoiseLoop", 0.15f),
            }},
            { "NG", new[]
            {
                new VisemeMapping("blinker", 0.55f),
                new VisemeMapping("G_TalkingNormal", 0.45f),
                new VisemeMapping("E_defaultNoiseLoop", 0.15f),
            }},

            // Liquids and glides - smooth activity
            { "L", new[]
            {
                new VisemeMapping("blinker", 0.6f),
                new VisemeMapping("G_TalkingNormal", 0.5f),
                new VisemeMapping("E_defaultNoiseLoop", 0.15f),
            }},
            { "R", new[]
            {
                new VisemeMapping("blinker", 0.65f),
                new VisemeMapping("G_TalkingNormal", 0.55f),
                new VisemeMapping("E_defaultNoiseLoop", 0.2f),
                new VisemeMapping("E_Neutral_Thoughtfull", 0.1f),
            }},
            { "W", new[]
            {
                new VisemeMapping("blinker", 0.6f),
                new VisemeMapping("G_TalkingNormal", 0.5f),
                new VisemeMapping("E_defaultNoiseLoop", 0.15f),
            }},
            { "Y", new[]
            {
                new VisemeMapping("blinker", 0.6f),
                new VisemeMapping("G_TalkingNormal", 0.5f),
                new VisemeMapping("E_defaultNoiseLoop", 0.15f),
                new VisemeMapping("Gaze_Eye_Pitch", 0.1f),
            }},
        };

        /// <summary>
        /// All viseme animation names used in lip sync for Geth
        /// Note: Geth use a "blinker" system plus head/gaze movements and gestures.
        /// </summary>
        public static readonly string[] GethVisemes =
        [
            // Primary speech animation - the eye lamp blinker
            "blinker",
            // Standard expression/orientation animations
            "Orientation_Head_Pitch",
            "Orientation_Head_Roll",
            "Orientation_Head_Yaw",
            "Gaze_Eye_Pitch",
            "Gaze_Eye_Yaw",
            "Emphasis_Head_Pitch",
            "Emphasis_Head_Roll",
            "Emphasis_Head_Yaw",
            "Eyebrow_Raise",
            "Blink",
            // Geth-specific gesture animations
            "G_TalkingNormal",
            "E_defaultNoiseLoop",
            "E_Neutral_Thoughtfull",
            "E_GESTURE_HeadRollLeft",
            "E_GESTURE_HeadLeft",
            "E_GESTURE_NeckForwardLeft",
            "E_GESTURE_NeckBackLeft"
        ];


        /// <summary>
        /// Non-lip sync animations that control head/eye movement
        /// </summary>
        public static readonly string[] ExpressionAnimations =
        [
            "Orientation_Head_Pitch",
            "Orientation_Head_Roll",
            "Orientation_Head_Yaw",
            "Gaze_Eye_Pitch",
            "Gaze_Eye_Yaw",
            "Emphasis_Head_Pitch",
            "Emphasis_Head_Roll",
            "Emphasis_Head_Yaw",
            "Eyebrow_Raise",
            "Blink"
        ];
    }

    /// <summary>
    /// Represents a mapping from a phoneme to a viseme animation
    /// </summary>
    public class VisemeMapping
    {
        public string VisemeName { get; }
        public float Weight { get; }

        public VisemeMapping(string visemeName, float weight)
        {
            VisemeName = visemeName;
            Weight = weight;
        }
    }
}
