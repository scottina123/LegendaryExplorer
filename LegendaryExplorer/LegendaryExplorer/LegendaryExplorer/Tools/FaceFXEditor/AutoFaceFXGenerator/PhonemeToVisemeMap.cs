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
        Asari,
        Krogan,
        Drell,
        Turian,
        Salarian
    }

    /// <summary>
    /// Maps phonemes to visemes using UDK FaceFX reference data.
    /// Each phoneme maps to multiple visemes with specific weights.
    /// </summary>
    public static class PhonemeToVisemeMap
    {
        /// <summary>
        /// Gets the phoneme map for the specified species
        /// </summary>
        public static Dictionary<string, VisemeMapping[]> GetPhonemeMap(FaceFXSpecies species)
        {
            return species switch
            {
                FaceFXSpecies.HumanMale => HumanMalePhonemeMap,
                FaceFXSpecies.Asari => AsariPhonemeMap,
                FaceFXSpecies.Krogan => KroganPhonemeMap,
                FaceFXSpecies.Drell => DrellPhonemeMap,
                FaceFXSpecies.Turian => TurianPhonemeMap,
                FaceFXSpecies.Salarian => SalarianPhonemeMap,
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
                FaceFXSpecies.Asari => AsariVisemes,
                FaceFXSpecies.Krogan => KroganVisemes,
                FaceFXSpecies.Drell => DrellVisemes,
                FaceFXSpecies.Turian => TurianVisemes,
                FaceFXSpecies.Salarian => SalarianVisemes,
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
