using System;
using System.Collections.Generic;
using System.Linq;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using static LegendaryExplorer.UserControls.ExportLoaderControls.FaceFXAnimSetEditorControl;

namespace LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator
{
    /// <summary>
    /// Supported character types for FaceFX generation
    /// </summary>
    public enum CharacterType
    {
        HumanFemale,
        HumanMale,
        // Future character types can be added here
    }

    /// <summary>
    /// Options for FaceFX generation
    /// </summary>
    public class FaceFXGenerationOptions
    {
        public CharacterType CharacterType { get; set; } = CharacterType.HumanFemale;
        public bool GenerateJawAnimation { get; set; } = true;
        public bool GenerateBlinkAnimation { get; set; } = true;
        public bool GenerateEyebrowAnimation { get; set; } = true;
        public bool GenerateHeadMovement { get; set; } = false;
        public float LipSyncIntensity { get; set; } = 1.0f;
        public float BlinkFrequency { get; set; } = 0.2f; // Blinks per second
        public bool UseAudioAmplitude { get; set; } = true;
    }

    /// <summary>
    /// Generates FaceFX animations automatically from text and audio
    /// </summary>
    public class FaceFXGenerator
    {
        private readonly IFaceFXBinary _faceFX;
        private readonly FaceFXLine _line;
        private readonly string _tlkText;
        private readonly ExportEntry _audioExport;
        private readonly FaceFXGenerationOptions _options;
        private float _audioDuration;
        private List<AmplitudeData> _amplitudeData;
        private List<PhonemeData> _phonemes; // Store phoneme data for use by jaw animations

        /// <summary>
        /// Contains the last error message if generation failed
        /// </summary>
        public string LastError { get; private set; }

        public FaceFXGenerator(IFaceFXBinary faceFX, FaceFXLine line, string tlkText, ExportEntry audioExport, FaceFXGenerationOptions options = null)
        {
            _faceFX = faceFX;
            _line = line;
            _tlkText = tlkText ?? "";
            _audioExport = audioExport;
            _options = options ?? new FaceFXGenerationOptions();
        }

        /// <summary>
        /// Generates FaceFX animations for the line
        /// </summary>
        /// <returns>True if generation was successful</returns>
        public bool Generate()
        {
            try
            {
                // Validate inputs
                if (_faceFX == null || _line == null)
                    return false;

                // Analyze audio for duration and amplitude
                _audioDuration = AudioAnalyzer.GetAudioDuration(_audioExport);
                if (_audioDuration <= 0)
                {
                    // Estimate duration from text if audio analysis fails
                    // Rough estimate: ~0.15 seconds per syllable
                    _audioDuration = EstimateDurationFromText(_tlkText);
                }

                if (_options.UseAudioAmplitude && _audioExport != null)
                {
                    _amplitudeData = AudioAnalyzer.AnalyzeAmplitude(_audioExport);
                }
                else
                {
                    _amplitudeData = new List<AmplitudeData>();
                }

                // Clear existing lip sync animations
                ClearLipSyncAnimations();

                // Generate phoneme timing from text - store for use by other generators
                _phonemes = TextToPhonemeAnalyzer.AnalyzeText(_tlkText, _audioDuration);

                // Generate lip sync animations from phonemes (includes m_Jaw+, m_Jaw-, m_Open based on UDK mappings)
                GenerateLipSyncAnimations(_phonemes);

                // Note: Jaw animations are now generated as part of lip sync using UDK phoneme mappings
                // The separate jaw generation is no longer needed

                // Generate blink animation
                if (_options.GenerateBlinkAnimation)
                {
                    GenerateBlinkAnimation();
                }

                // Generate eyebrow animation for emphasis
                if (_options.GenerateEyebrowAnimation)
                {
                    GenerateEyebrowAnimation();
                }

                // Generate subtle head movement
                if (_options.GenerateHeadMovement)
                {
                    GenerateHeadMovement();
                }

                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        private float EstimateDurationFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 2.0f; // Default 2 seconds

            // Count approximate syllables (simplified)
            int vowelCount = text.Count(c => "aeiouAEIOU".Contains(c));
            int wordCount = text.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;

            // Average speaking rate is about 150 words per minute
            // Or about 4-5 syllables per second
            float estimatedDuration = Math.Max(vowelCount * 0.15f, wordCount * 0.4f);
            return Math.Max(0.5f, estimatedDuration); // Minimum 0.5 seconds
        }

        private void ClearLipSyncAnimations()
        {
            // Safety checks
            if (_line.AnimationNames == null || _line.AnimationNames.Count == 0)
                return;
            if (_line.NumKeys == null || _line.Points == null)
                return;

            // Remove all m_ prefixed animations (lip sync) from the line
            var indicesToRemove = new List<int>();
            for (int i = 0; i < _line.AnimationNames.Count; i++)
            {
                int nameIndex = _line.AnimationNames[i];
                if (nameIndex >= 0 && nameIndex < _faceFX.Names.Count)
                {
                    string animName = _faceFX.Names[nameIndex];
                    if (animName.StartsWith("m_"))
                    {
                        indicesToRemove.Add(i);
                    }
                }
            }

            // Remove in reverse order to maintain indices
            for (int i = indicesToRemove.Count - 1; i >= 0; i--)
            {
                int idx = indicesToRemove[i];
                
                // Calculate point offset
                int pointOffset = 0;
                for (int j = 0; j < idx; j++)
                {
                    pointOffset += _line.NumKeys[j];
                }

                // Remove points
                int numPoints = _line.NumKeys[idx];
                if (pointOffset >= 0 && numPoints > 0 && pointOffset + numPoints <= _line.Points.Count)
                {
                    _line.Points.RemoveRange(pointOffset, numPoints);
                }

                // Remove animation
                _line.AnimationNames.RemoveAt(idx);
                _line.NumKeys.RemoveAt(idx);
            }
        }













        private void GenerateLipSyncAnimations(List<PhonemeData> phonemes)
        {
            float duration = Math.Max(_audioDuration, 1.0f);
            
            // Maximum weight cap to prevent over-exaggerated mouth movements
            const float MaxWeight = 0.58f;
            
            // Create a dictionary to hold points for each viseme
            var visemePoints = new Dictionary<string, List<FaceFXControlPoint>>();
            
            // Initialize ALL visemes with starting zero point
            foreach (var viseme in PhonemeToVisemeMap.HumanFemaleVisemes)
            {
                visemePoints[viseme] = new List<FaceFXControlPoint>
                {
                    new FaceFXControlPoint { time = 0f, weight = 0f, inTangent = 0f, leaveTangent = 0f }
                };
            }

            // Process each phoneme - handles multiple viseme targets per phoneme (UDK style)
            foreach (var phoneme in phonemes)
            {
                if (phoneme.StartTime < 0.01f)
                    continue;
                    
                if (PhonemeToVisemeMap.PhonemeMap.TryGetValue(phoneme.Phoneme, out var mappings))
                {
                    float centerTime = phoneme.StartTime + phoneme.Duration * 0.5f;
                    
                    // Apply ALL viseme mappings for this phoneme
                    foreach (var mapping in mappings)
                    {
                        if (!visemePoints.ContainsKey(mapping.VisemeName))
                            continue;
                        
                        // Apply intensity scaling but cap at MaxWeight to prevent exaggeration
                        float weight = Math.Min(mapping.Weight * _options.LipSyncIntensity, MaxWeight);
                        
                        visemePoints[mapping.VisemeName].Add(new FaceFXControlPoint 
                        { 
                            time = centerTime, 
                            weight = weight, 
                            inTangent = 0f, 
                            leaveTangent = 0f 
                        });
                    }
                }
            }

            // Finalize and add each viseme animation
            foreach (var viseme in PhonemeToVisemeMap.HumanFemaleVisemes)
            {
                var points = visemePoints[viseme];
                
                // Add ending zero point
                points.Add(new FaceFXControlPoint { time = duration, weight = 0f, inTangent = 0f, leaveTangent = 0f });
                
                // Sort by time and merge nearby points
                var sortedPoints = points.OrderBy(p => p.time).ToList();
                var smoothedPoints = SmoothKeyframes(sortedPoints, 0.04f); // Merge points within 40ms
                
                AddAnimation(viseme, smoothedPoints);
            }
        }

        /// <summary>
        /// Smooths keyframes by merging points that are too close together
        /// </summary>
        private List<FaceFXControlPoint> SmoothKeyframes(List<FaceFXControlPoint> points, float minInterval)
        {
            if (points.Count < 2)
                return points;
                
            var result = new List<FaceFXControlPoint> { points[0] };
            
            for (int i = 1; i < points.Count; i++)
            {
                var lastPoint = result[result.Count - 1];
                var currentPoint = points[i];
                
                if (currentPoint.time - lastPoint.time < minInterval)
                {
                    // Merge: keep the higher weight, use average time
                    if (currentPoint.weight > lastPoint.weight)
                    {
                        result[result.Count - 1] = new FaceFXControlPoint
                        {
                            time = (lastPoint.time + currentPoint.time) / 2f,
                            weight = currentPoint.weight,
                            inTangent = 0f,
                            leaveTangent = 0f
                        };
                    }
                }
                else
                {
                    result.Add(currentPoint);
                }
            }
            
            return result;
        }











        private void GenerateJawAnimation()
        {
            var points = new List<FaceFXControlPoint>();
            points.Add(new FaceFXControlPoint { time = 0f, weight = 0f, inTangent = 0f, leaveTangent = 0f });

            float duration = Math.Max(_audioDuration, 1.0f);
            
            if (_phonemes != null && _phonemes.Count > 0)
            {
                // Phonemes that require significant jaw opening
                var wideOpenPhonemes = new HashSet<string> { "AA", "AE", "AH", "AO", "AW", "AY" };
                var mediumOpenPhonemes = new HashSet<string> { "EH", "EY", "OW", "OY", "H", "L", "R" };
                var slightOpenPhonemes = new HashSet<string> { "IH", "IY", "UH", "UW", "ER", "W", "Y" };
                
                foreach (var phoneme in _phonemes)
                {
                    if (phoneme.StartTime < 0.01f)
                        continue;
                    
                    float weight = 0f;
                    float centerTime = phoneme.StartTime + phoneme.Duration * 0.5f;
                    
                    if (wideOpenPhonemes.Contains(phoneme.Phoneme))
                        weight = 0.7f * _options.LipSyncIntensity;
                    else if (mediumOpenPhonemes.Contains(phoneme.Phoneme))
                        weight = 0.5f * _options.LipSyncIntensity;
                    else if (slightOpenPhonemes.Contains(phoneme.Phoneme))
                        weight = 0.3f * _options.LipSyncIntensity;
                    else
                        weight = 0.15f * _options.LipSyncIntensity;
                    
                    // Single keyframe at center
                    points.Add(new FaceFXControlPoint { time = centerTime, weight = weight, inTangent = 0f, leaveTangent = 0f });
                }
            }
            else
            {
                // Fallback: procedural animation
                var random = new Random(42);
                float interval = 0.15f;
                float currentTime = interval;
                
                while (currentTime < duration)
                {
                    float weight = (0.2f + (float)random.NextDouble() * 0.4f) * _options.LipSyncIntensity;
                    points.Add(new FaceFXControlPoint { time = currentTime, weight = weight, inTangent = 0f, leaveTangent = 0f });
                    currentTime += interval;
                }
            }

            points.Add(new FaceFXControlPoint { time = duration, weight = 0f, inTangent = 0f, leaveTangent = 0f });
            
            var sortedPoints = points.OrderBy(p => p.time).ToList();
            var smoothedPoints = SmoothKeyframes(sortedPoints, 0.05f);
            AddAnimation("m_Open", smoothedPoints);
        }

        /// <summary>
        /// Generates m_Jaw+ and m_Jaw- animations for jaw positioning during speech
        /// Uses phoneme data to create natural jaw movement patterns
        /// </summary>
        private void GenerateJawPositionAnimations()
        {
            float duration = Math.Max(_audioDuration, 1.0f);
            
            var jawUpPoints = new List<FaceFXControlPoint>();
            var jawDownPoints = new List<FaceFXControlPoint>();
            
            jawUpPoints.Add(new FaceFXControlPoint { time = 0f, weight = 0f, inTangent = 0f, leaveTangent = 0f });
            jawDownPoints.Add(new FaceFXControlPoint { time = 0f, weight = 0f, inTangent = 0f, leaveTangent = 0f });

            if (_phonemes != null && _phonemes.Count > 0)
            {
                // Jaw down (m_Jaw-): vowels that need open mouth
                var jawDownPhonemes = new HashSet<string> { "AA", "AE", "AH", "AO", "AW", "AY", "EH", "OW", "OY", "H" };
                // Jaw up (m_Jaw+): consonants that close the mouth
                var jawUpPhonemes = new HashSet<string> { "M", "P", "B", "F", "V", "TH", "DH", "S", "Z", "SH", "ZH", "CH", "JH" };
                
                foreach (var phoneme in _phonemes)
                {
                    if (phoneme.StartTime < 0.01f)
                        continue;
                    
                    float weight = 0.4f * _options.LipSyncIntensity;
                    float centerTime = phoneme.StartTime + phoneme.Duration * 0.5f;
                    
                    if (jawDownPhonemes.Contains(phoneme.Phoneme))
                    {
                        // Jaw opens down - single keyframe
                        jawDownPoints.Add(new FaceFXControlPoint { time = centerTime, weight = weight, inTangent = 0f, leaveTangent = 0f });
                    }
                    else if (jawUpPhonemes.Contains(phoneme.Phoneme))
                    {
                        // Jaw closes up - single keyframe
                        jawUpPoints.Add(new FaceFXControlPoint { time = centerTime, weight = weight, inTangent = 0f, leaveTangent = 0f });
                    }
                    else
                    {
                        // Neutral phonemes - subtle movement
                        float subtleWeight = 0.1f * _options.LipSyncIntensity;
                        jawUpPoints.Add(new FaceFXControlPoint { time = centerTime, weight = subtleWeight, inTangent = 0f, leaveTangent = 0f });
                        jawDownPoints.Add(new FaceFXControlPoint { time = centerTime, weight = subtleWeight, inTangent = 0f, leaveTangent = 0f });
                    }
                }
            }
            else
            {
                // Fallback: procedural animation
                var random = new Random(567);
                float interval = 0.15f;
                float currentTime = interval;
                bool jawUp = random.NextDouble() > 0.5;
                
                
                while (currentTime < duration)
                {
                    float upWeight = jawUp ? (0.2f + (float)random.NextDouble() * 0.3f) * _options.LipSyncIntensity : 0.05f;
                    float downWeight = !jawUp ? (0.2f + (float)random.NextDouble() * 0.3f) * _options.LipSyncIntensity : 0.05f;
                    
                    jawUpPoints.Add(new FaceFXControlPoint { time = currentTime, weight = upWeight, inTangent = 0f, leaveTangent = 0f });
                    jawDownPoints.Add(new FaceFXControlPoint { time = currentTime, weight = downWeight, inTangent = 0f, leaveTangent = 0f });
                    
                    if (random.NextDouble() > 0.6)
                        jawUp = !jawUp;
                    
                    currentTime += interval;
                }
            }

            jawUpPoints.Add(new FaceFXControlPoint { time = duration, weight = 0f, inTangent = 0f, leaveTangent = 0f });
            jawDownPoints.Add(new FaceFXControlPoint { time = duration, weight = 0f, inTangent = 0f, leaveTangent = 0f });
            
            var sortedUp = jawUpPoints.OrderBy(p => p.time).ToList();
            var sortedDown = jawDownPoints.OrderBy(p => p.time).ToList();
            
            // Apply smoothing to merge close keyframes
            var smoothedUp = SmoothKeyframes(sortedUp, 0.05f);
            var smoothedDown = SmoothKeyframes(sortedDown, 0.05f);
            
            AddAnimation("m_Jaw+", smoothedUp);
            AddAnimation("m_Jaw-", smoothedDown);
        }

        private void GenerateBlinkAnimation()
        {
            var points = new List<FaceFXControlPoint>();
            points.Add(new FaceFXControlPoint { time = 0f, weight = 0f, inTangent = 0f, leaveTangent = 0f });

            float duration = Math.Max(_audioDuration, 1.0f);
            
            // Generate blinks at random intervals
            float averageBlinkInterval = 1f / _options.BlinkFrequency;
            var random = new Random(123);
            float currentTime = (float)(random.NextDouble() * averageBlinkInterval * 0.5 + 0.5);

            while (currentTime < duration - 0.2f)
            {
                // Blink - single peak keyframe at center
                float blinkDuration = 0.15f + (float)random.NextDouble() * 0.1f;
                float peakTime = currentTime + blinkDuration * 0.5f;

                points.Add(new FaceFXControlPoint { time = peakTime, weight = 1f, inTangent = 0f, leaveTangent = 0f });

                // Next blink with some randomness
                currentTime += averageBlinkInterval * (0.7f + (float)random.NextDouble() * 0.6f);
            }

            points.Add(new FaceFXControlPoint { time = duration, weight = 0f, inTangent = 0f, leaveTangent = 0f });
            
            var sortedPoints = points.OrderBy(p => p.time).ToList();
            AddAnimation("Blink", sortedPoints);
        }

        private void GenerateEyebrowAnimation()
        {
            var points = new List<FaceFXControlPoint>();
            points.Add(new FaceFXControlPoint { time = 0f, weight = 0f, inTangent = 0f, leaveTangent = 0f });

            float duration = Math.Max(_audioDuration, 1.0f);
            
            // Use phoneme data to raise eyebrows on emphasized sounds
            if (_phonemes != null && _phonemes.Count > 0)
            {
                var emphasisPhonemes = new HashSet<string> { "AY", "EY", "OW", "IY", "UW", "AO", "H" };
                
                int phonemeIndex = 0;
                foreach (var phoneme in _phonemes)
                {
                    if (phoneme.StartTime < 0.01f)
                    {
                        phonemeIndex++;
                        continue;
                    }
                    
                    bool isLateInPhrase = phonemeIndex > _phonemes.Count * 0.6f;
                    bool isEmphasis = emphasisPhonemes.Contains(phoneme.Phoneme);
                    
                    if (isEmphasis && (isLateInPhrase || phonemeIndex % 5 == 0))
                    {
                        float weight = 0.4f * _options.LipSyncIntensity;
                        float centerTime = phoneme.StartTime + phoneme.Duration * 0.5f;
                        
                        // Single keyframe
                        points.Add(new FaceFXControlPoint { time = centerTime, weight = weight, inTangent = 0f, leaveTangent = 0f });
                    }
                    
                    phonemeIndex++;
                }
            }
            else
            {
                // Fallback: procedural animation - sparse keyframes
                var random = new Random(789);
                float currentTime = 1.0f + (float)random.NextDouble() * 1.0f;
                
                while (currentTime < duration - 0.5f)
                {
                    float weight = (0.3f + (float)random.NextDouble() * 0.3f) * _options.LipSyncIntensity;
                    points.Add(new FaceFXControlPoint { time = currentTime, weight = weight, inTangent = 0f, leaveTangent = 0f });
                    currentTime += 2.0f + (float)random.NextDouble() * 2.0f;
                }
            }

            points.Add(new FaceFXControlPoint { time = duration, weight = 0f, inTangent = 0f, leaveTangent = 0f });
            
            var sortedPoints = points.OrderBy(p => p.time).ToList();
            AddAnimation("Eyebrow_Raise", sortedPoints);
        }

        private void GenerateHeadMovement()
        {
            float duration = Math.Max(_audioDuration, 1.0f);
            var random = new Random(456);
            
            foreach (var axis in new[] { "Emphasis_Head_Pitch", "Emphasis_Head_Yaw" })
            {
                var points = new List<FaceFXControlPoint>();
                points.Add(new FaceFXControlPoint { time = 0f, weight = 0f, inTangent = 0f, leaveTangent = 0f });

                // Use phonemes to drive head movement - sparse single keyframes
                if (_phonemes != null && _phonemes.Count > 0)
                {
                    var emphasisPhonemes = new HashSet<string> { "AA", "AE", "AO", "AY", "EY", "OW", "H" };
                    
                    foreach (var phoneme in _phonemes)
                    {
                        if (phoneme.StartTime < 0.01f)
                            continue;
                        
                        if (emphasisPhonemes.Contains(phoneme.Phoneme) && random.NextDouble() > 0.7) // Less frequent
                        {
                            float weight = ((float)random.NextDouble() * 0.2f - 0.1f) * _options.LipSyncIntensity;
                            float centerTime = phoneme.StartTime + phoneme.Duration * 0.5f;
                            points.Add(new FaceFXControlPoint { time = centerTime, weight = weight, inTangent = 0f, leaveTangent = 0f });
                        }
                    }
                }
                else
                {
                    // Fallback: sparse procedural
                    float interval = 0.6f + (float)random.NextDouble() * 0.4f;
                    float currentTime = interval;

                    while (currentTime < duration - 0.3f)
                    {
                        float weight = ((float)random.NextDouble() * 0.2f - 0.1f) * _options.LipSyncIntensity;
                        points.Add(new FaceFXControlPoint { time = currentTime, weight = weight, inTangent = 0f, leaveTangent = 0f });
                        currentTime += interval + (float)random.NextDouble() * 0.4f;
                    }
                }

                points.Add(new FaceFXControlPoint { time = duration, weight = 0f, inTangent = 0f, leaveTangent = 0f });
                
                
                var sortedPoints = points.OrderBy(p => p.time).ToList();
                AddAnimation(axis, sortedPoints);
            }
        }

        private void AddAnimation(string name, List<FaceFXControlPoint> points)
        {
            // Initialize lists if null
            if (_line.AnimationNames == null)
                _line.AnimationNames = new List<int>();
            if (_line.NumKeys == null)
                _line.NumKeys = new List<int>();
            if (_line.Points == null)
                _line.Points = new List<FaceFXControlPoint>();

            // Check if animation already exists
            int existingIndex = -1;
            for (int i = 0; i < _line.AnimationNames.Count; i++)
            {
                int nameIdx = _line.AnimationNames[i];
                if (nameIdx >= 0 && nameIdx < _faceFX.Names.Count && _faceFX.Names[nameIdx] == name)
                {
                    existingIndex = i;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                // Update existing animation
                int pointOffset = 0;
                for (int i = 0; i < existingIndex; i++)
                {
                    pointOffset += _line.NumKeys[i];
                }

                // Remove old points
                int oldNumKeys = _line.NumKeys[existingIndex];
                if (pointOffset >= 0 && oldNumKeys > 0 && pointOffset + oldNumKeys <= _line.Points.Count)
                {
                    _line.Points.RemoveRange(pointOffset, oldNumKeys);
                }

                // Insert new points
                if (pointOffset <= _line.Points.Count)
                {
                    _line.Points.InsertRange(pointOffset, points);
                }
                else
                {
                    _line.Points.AddRange(points);
                }
                _line.NumKeys[existingIndex] = points.Count;
            }
            else
            {
                // Add new animation
                int nameIndex = _faceFX.Names.IndexOf(name);
                if (nameIndex < 0)
                {
                    nameIndex = _faceFX.Names.Count;
                    _faceFX.Names.Add(name);
                }

                _line.AnimationNames.Add(nameIndex);
                _line.NumKeys.Add(points.Count);
                _line.Points.AddRange(points);
            }
        }
    }
}
