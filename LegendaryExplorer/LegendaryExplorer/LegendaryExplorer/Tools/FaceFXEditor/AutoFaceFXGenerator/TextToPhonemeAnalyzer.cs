using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator
{
    /// <summary>
    /// Analyzes text and converts it to phonemes for lip sync generation
    /// </summary>
    public static class TextToPhonemeAnalyzer
    {
        /// <summary>
        /// Simple grapheme-to-phoneme mapping for English
        /// This is a simplified approach; more sophisticated systems would use CMU dict or ML models
        /// </summary>
        private static readonly Dictionary<string, string[]> GraphemeToPhoneme = new()
        {
            // Common letter combinations (order matters - check longer combinations first)
            { "tion", new[] { "SH", "AH", "N" } },
            { "sion", new[] { "ZH", "AH", "N" } },
            { "ough", new[] { "AH", "F" } },
            { "ight", new[] { "AY", "T" } },
            { "eigh", new[] { "EY" } },
            { "ould", new[] { "UH", "D" } },
            { "ally", new[] { "AE", "L", "IY" } },
            { "illy", new[] { "IH", "L", "IY" } },
            { "ully", new[] { "UH", "L", "IY" } },
            { "all", new[] { "AO", "L" } },
            { "ell", new[] { "EH", "L" } },
            { "ill", new[] { "IH", "L" } },
            { "oll", new[] { "AA", "L" } },
            { "ull", new[] { "UH", "L" } },
            { "ll", new[] { "L" } },
            { "le", new[] { "AH", "L" } },
            { "th", new[] { "TH" } },
            { "ch", new[] { "CH" } },
            { "sh", new[] { "SH" } },
            { "ph", new[] { "F" } },
            { "wh", new[] { "W" } },
            { "ng", new[] { "NG" } },
            { "qu", new[] { "K", "W" } },
            { "ck", new[] { "K" } },
            { "ee", new[] { "IY" } },
            { "ea", new[] { "IY" } },
            { "oo", new[] { "UW" } },
            { "ou", new[] { "AW" } },
            { "ow", new[] { "OW" } },
            { "oi", new[] { "OY" } },
            { "oy", new[] { "OY" } },
            { "ai", new[] { "EY" } },
            { "ay", new[] { "EY" } },
            { "au", new[] { "AO" } },
            { "aw", new[] { "AO" } },
            { "ie", new[] { "IY" } },
            { "ue", new[] { "UW" } },
            { "er", new[] { "ER" } },
            { "ir", new[] { "ER" } },
            { "ur", new[] { "ER" } },
            { "or", new[] { "AO", "R" } },
            { "ar", new[] { "AA", "R" } },
            
            // Single letters
            { "a", new[] { "AE" } },
            { "b", new[] { "B" } },
            { "c", new[] { "K" } },  // simplified
            { "d", new[] { "D" } },
            { "e", new[] { "EH" } },
            { "f", new[] { "F" } },
            { "g", new[] { "G" } },
            { "h", new[] { "H" } },
            { "i", new[] { "IH" } },
            { "j", new[] { "JH" } },
            { "k", new[] { "K" } },
            { "l", new[] { "L" } },
            { "m", new[] { "M" } },
            { "n", new[] { "N" } },
            { "o", new[] { "AA" } },
            { "p", new[] { "P" } },
            { "q", new[] { "K" } },
            { "r", new[] { "R" } },
            { "s", new[] { "S" } },
            { "t", new[] { "T" } },
            { "u", new[] { "AH" } },
            { "v", new[] { "V" } },
            { "w", new[] { "W" } },
            { "x", new[] { "K", "S" } },
            { "y", new[] { "Y" } },
            { "z", new[] { "Z" } },
        };

        /// <summary>
        /// Converts text to a list of phonemes with timing information
        /// </summary>
        /// <param name="text">The text to analyze</param>
        /// <param name="duration">Total duration in seconds</param>
        /// <returns>List of phonemes with their start times and durations</returns>
        public static List<PhonemeData> AnalyzeText(string text, float duration)
        {
            var result = new List<PhonemeData>();
            
            if (string.IsNullOrWhiteSpace(text))
                return result;

            // Clean the text - remove punctuation but keep spaces
            text = Regex.Replace(text.ToLower(), @"[^\w\s]", "");
            
            // Get all phonemes from the text
            var allPhonemes = new List<string>();
            int i = 0;
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    // Add a pause phoneme for spaces between words
                    allPhonemes.Add("PAUSE");
                    i++;
                    continue;
                }

                bool found = false;
                
                // Try matching longer sequences first (up to 5 characters)
                for (int len = Math.Min(5, text.Length - i); len > 0; len--)
                {
                    string substr = text.Substring(i, len);
                    if (GraphemeToPhoneme.TryGetValue(substr, out string[] phonemes))
                    {
                        allPhonemes.AddRange(phonemes);
                        i += len;
                        found = true;
                        break;
                    }
                }
                
                if (!found)
                {
                    // Skip unknown characters
                    i++;
                }
            }

            if (allPhonemes.Count == 0)
                return result;

            // Calculate timing for each phoneme.
            // NOTE: MapPhonemesToAudioTiming later overrides the timing completely
            // based on audio segments. The timing here is only a rough guide, so we
            // must NOT drop phonemes early — the full phoneme list is needed for the
            // audio-timed redistribution to produce enough mouth movements.

            float totalWeight = allPhonemes.Sum(p => GetPhonemeWeight(p));
            float availableDuration = duration - 0.1f; // Leave small buffer at start/end

            // Scale phoneme durations to fill the available audio duration
            float timeScale = totalWeight > 0
                ? availableDuration / totalWeight
                : 0.07f; // fallback ~70ms per unit weight

            float currentTime = 0.05f; // Small offset from the start

            foreach (var phoneme in allPhonemes)
            {
                float weight = GetPhonemeWeight(phoneme);

                // Duration proportional to weight, scaled to fill the audio
                float phonemeDuration = Math.Max(weight * timeScale, 0.04f);

                if (phoneme != "PAUSE")
                {
                    result.Add(new PhonemeData
                    {
                        Phoneme = phoneme,
                        StartTime = currentTime,
                        Duration = phonemeDuration
                    });
                }

                currentTime += phonemeDuration;
            }

            return result;
        }

        /// <summary>
        /// Gets the weight of a phoneme for timing calculation
        /// Vowels generally take longer to pronounce than consonants
        /// </summary>
        private static float GetPhonemeWeight(string phoneme)
        {
            if (phoneme == "PAUSE")
                return 0.3f; // Pauses are short

            // Vowels and diphthongs take longer
            var longVowels = new HashSet<string> 
            { 
                "IY", "EY", "UW", "OW", "AO", "AA", "AY", "AW", "OY" 
            };
            
            if (longVowels.Contains(phoneme))
                return 1.8f;
            
            // Short vowels
            var shortVowels = new HashSet<string> 
            { 
                "IH", "EH", "AE", "AH", "ER", "UH"
            };
            
            if (shortVowels.Contains(phoneme))
                return 1.2f;

            // Stops are very short
            var stops = new HashSet<string> 
            { 
                "P", "B", "T", "D", "K", "G" 
            };
            
            if (stops.Contains(phoneme))
                return 0.5f;
            
            // Fricatives are medium
            var fricatives = new HashSet<string>
            {
                "F", "V", "S", "Z", "SH", "ZH", "TH", "DH", "H"
            };
            
            if (fricatives.Contains(phoneme))
                return 0.8f;

            return 1.0f; // Default for other consonants
        }
    }

    /// <summary>
    /// Represents a phoneme with timing information
    /// </summary>
    public class PhonemeData
    {
        public string Phoneme { get; set; }
        public float StartTime { get; set; }
        public float Duration { get; set; }
        public float EndTime => StartTime + Duration;
    }
}
