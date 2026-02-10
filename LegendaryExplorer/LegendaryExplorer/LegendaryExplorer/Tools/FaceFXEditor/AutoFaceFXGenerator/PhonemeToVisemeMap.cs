using System.Collections.Generic;

namespace LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator
{
    /// <summary>
    /// Maps phonemes to visemes using UDK FaceFX reference data.
    /// Each phoneme maps to multiple visemes with specific weights.
    /// </summary>
    public static class PhonemeToVisemeMap
    {
        /// <summary>
        /// UDK-based phoneme to viseme mappings - EXACT values from Unreal FaceFX.
        /// Each phoneme can trigger multiple visemes with specific weights.
        /// </summary>
        public static readonly Dictionary<string, VisemeMapping[]> PhonemeMap = new()
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
        /// All viseme animation names used in lip sync
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
