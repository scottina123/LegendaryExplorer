using System.Collections.Generic;

namespace LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator
{
    /// <summary>
    /// Maps phonemes to visemes using UDK FaceFX reference data.
    /// Each phoneme maps to multiple visemes with specific weights.
    /// Weights are scaled down from UDK values for Mass Effect use.
    /// </summary>
    public static class PhonemeToVisemeMap
    {
        // Scale factor to reduce UDK weights for ME3 (UDK weights are too aggressive)
        private const float WeightScale = 0.45f;
        
        /// <summary>
        /// UDK-based phoneme to viseme mappings. Each phoneme can trigger multiple visemes.
        /// Weights are scaled down for Mass Effect compatibility.
        /// </summary>
        public static readonly Dictionary<string, VisemeMapping[]> PhonemeMap = new()
        {
            // Silence - subtle jaw position
            { "SIL", new[] { new VisemeMapping("m_Jaw+", 0.058f), new VisemeMapping("m_Open", 0.092f) } },
            
            // Bilabial stops - lips together
            { "P", new[] { new VisemeMapping("m_M", 0.404f), new VisemeMapping("m_Jaw-", 0.058f) } },
            { "B", new[] { new VisemeMapping("m_M", 0.404f), new VisemeMapping("m_Jaw-", 0.404f) } },
            { "M", new[] { new VisemeMapping("m_M", 0.520f), new VisemeMapping("m_Jaw-", 0.104f) } },
            
            // Alveolar stops
            { "T", new[] { new VisemeMapping("m_EE", 0.208f), new VisemeMapping("m_N", 0.520f), new VisemeMapping("m_G", 0.069f) } },
            { "D", new[] { new VisemeMapping("m_Jaw-", 0.035f), new VisemeMapping("m_N", 0.520f) } },
            
            // Velar stops
            { "K", new[] { new VisemeMapping("m_Jaw+", 0.127f), new VisemeMapping("m_G", 0.185f) } },
            { "G", new[] { new VisemeMapping("m_Jaw+", 0.046f), new VisemeMapping("m_G", 0.347f) } },
            
            // Nasals
            { "N", new[] { new VisemeMapping("m_EE", 0.254f), new VisemeMapping("m_OW", 0.254f), new VisemeMapping("m_Jaw-", 0.058f), new VisemeMapping("m_N", 0.520f) } },
            { "NG", new[] { new VisemeMapping("m_EE", 0.254f), new VisemeMapping("m_Jaw+", 0.069f), new VisemeMapping("m_M", 0.266f), new VisemeMapping("m_N", 0.520f) } },
            
            // Fricatives
            { "F", new[] { new VisemeMapping("m_Jaw+", 0.069f), new VisemeMapping("m_FV", 0.404f) } },
            { "V", new[] { new VisemeMapping("m_Jaw+", 0.081f), new VisemeMapping("m_FV", 0.520f) } },
            { "TH", new[] { new VisemeMapping("m_Jaw+", 0.081f), new VisemeMapping("m_TH", 0.381f) } },
            { "DH", new[] { new VisemeMapping("m_Jaw+", 0.116f), new VisemeMapping("m_TH", 0.485f) } },
            { "S", new[] { new VisemeMapping("m_EE", 0.139f), new VisemeMapping("m_Jaw+", 0.046f), new VisemeMapping("m_OW", 0.323f), new VisemeMapping("m_M", 0.243f) } },
            { "Z", new[] { new VisemeMapping("m_EE", 0.139f), new VisemeMapping("m_Jaw+", 0.012f), new VisemeMapping("m_OW", 0.300f), new VisemeMapping("m_M", 0.335f) } },
            { "SH", new[] { new VisemeMapping("m_Jaw+", 0.046f), new VisemeMapping("m_OW", 0.450f) } },
            { "ZH", new[] { new VisemeMapping("m_Jaw+", 0.046f), new VisemeMapping("m_OW", 0.277f) } },
            { "H", new[] { new VisemeMapping("m_EE", 0.185f), new VisemeMapping("m_Jaw+", 0.081f) } },
            
            // Approximants
            { "R", new[] { new VisemeMapping("m_Jaw+", 0.058f), new VisemeMapping("m_OH", 0.300f) } },
            { "L", new[] { new VisemeMapping("m_Jaw+", 0.095f), new VisemeMapping("m_L", 0.58f) } },
            { "W", new[] { new VisemeMapping("m_Jaw+", 0.069f), new VisemeMapping("m_OH", 0.370f) } },
            { "Y", new[] { new VisemeMapping("m_EE", 0.116f), new VisemeMapping("m_Jaw+", 0.081f) } },
            
            // Affricates
            { "CH", new[] { new VisemeMapping("m_Jaw+", 0.046f), new VisemeMapping("m_Open", 0.520f) } },
            { "JH", new[] { new VisemeMapping("m_Jaw+", 0.069f), new VisemeMapping("m_OH", 0.243f) } },
            
            // Flap
            { "FLAP", new[] { new VisemeMapping("m_Flap", 0.520f), new VisemeMapping("m_Jaw+", 0.116f) } },
            
            // Special
            { "TS", new[] { new VisemeMapping("m_Jaw-", 0.035f), new VisemeMapping("m_ZZ", 0.520f) } },
            
            // Front vowels
            { "IY", new[] { new VisemeMapping("m_EE", 0.162f), new VisemeMapping("m_Jaw+", 0.104f), new VisemeMapping("m_ZZ", 0.427f) } },
            { "IH", new[] { new VisemeMapping("m_EE", 0.127f), new VisemeMapping("m_Jaw+", 0.127f), new VisemeMapping("m_OW", 0.173f) } },
            { "EH", new[] { new VisemeMapping("m_Jaw+", 0.127f), new VisemeMapping("m_EH", 0.231f) } },
            { "EY", new[] { new VisemeMapping("m_Jaw+", 0.231f), new VisemeMapping("m_Open", 0.162f) } },
            { "AE", new[] { new VisemeMapping("m_Jaw+", 0.219f), new VisemeMapping("m_EH", 0.393f) } },
            
            // Central vowels
            { "AH", new[] { new VisemeMapping("m_Jaw+", 0.219f), new VisemeMapping("m_EH", 0.150f), new VisemeMapping("m_OH", 0.046f) } },
            { "AX", new[] { new VisemeMapping("m_Jaw+", 0.231f), new VisemeMapping("m_Open", 0.231f) } },
            { "ER", new[] { new VisemeMapping("m_Jaw+", 0.173f), new VisemeMapping("m_Open", 0.289f) } },
            
            // Back vowels
            { "UW", new[] { new VisemeMapping("m_Jaw+", 0.081f), new VisemeMapping("m_OH", 0.450f) } },
            { "UH", new[] { new VisemeMapping("m_Jaw+", 0.139f), new VisemeMapping("m_OH", 0.347f) } },
            { "OW", new[] { new VisemeMapping("m_Jaw+", 0.104f), new VisemeMapping("m_OH", 0.300f) } },
            { "AA", new[] { new VisemeMapping("m_Jaw+", 0.243f), new VisemeMapping("m_Open", 0.208f), new VisemeMapping("m_OW", 0.208f) } },
            { "AO", new[] { new VisemeMapping("m_Jaw+", 0.208f), new VisemeMapping("m_OH", 0.243f) } },
            
            // Diphthongs
            { "AY", new[] { new VisemeMapping("m_Jaw+", 0.162f) } },
            { "AW", new[] { new VisemeMapping("m_Jaw+", 0.300f), new VisemeMapping("m_Open", 0.358f) } },
            { "OY", new[] { new VisemeMapping("m_Jaw+", 0.139f), new VisemeMapping("m_OH", 0.300f) } },
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
