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

            // Calculate timing for each phoneme
            // Use natural speaking rhythm - average phoneme is about 60-80ms
            // But scale to fit the actual audio duration
            
            float totalWeight = allPhonemes.Sum(p => GetPhonemeWeight(p));
            float availableDuration = duration - 0.1f; // Leave small buffer at start/end
            
            // Calculate minimum phoneme duration based on natural speech
            // Natural speech is about 12-15 phonemes per second (faster rate for snappier lip sync)
            float naturalPhonemeRate = 14f; // phonemes per second (increased from 12)
            float naturalDuration = allPhonemes.Count / naturalPhonemeRate;
            
            // If the audio is longer than natural speech would take, 
            // stretch phonemes but don't make them unnaturally long
            float timeScale = availableDuration / Math.Max(naturalDuration, 0.1f);
            timeScale = Math.Max(0.6f, Math.Min(timeScale, 2.5f)); // Clamp between 0.6x and 2.5x natural speed
            
            float currentTime = 0.05f; // Small offset from the start

            foreach (var phoneme in allPhonemes)
            {
                float weight = GetPhonemeWeight(phoneme);
                
                // Base duration from natural speech timing
                float baseDuration = (weight / naturalPhonemeRate) * timeScale;
                
                // Ensure minimum duration for visibility (at least 50ms for faster transitions)
                float phonemeDuration = Math.Max(baseDuration, 0.05f);
                
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
                
                // If we're running past the audio duration, stop adding phonemes
                if (currentTime >= duration - 0.05f)
                    break;
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
