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
    /// Available emotion types for FaceFX generation
    /// </summary>
    public enum EmotionType
    {
        None,
        Anger,
        Disgust,
        Fear,
        Happy,
        Sad,
        Surprise,
        Contempt,
        Determined,
        Worried
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
        
        /// <summary>
        /// Emotion to apply to the animation
        /// </summary>
        public EmotionType Emotion { get; set; } = EmotionType.None;
        
        /// <summary>
        /// Intensity of the emotion (0-1)
        /// </summary>
        public float EmotionIntensity { get; set; } = 0.5f;
        
        /// <summary>
        /// FXA animation data imported from UDK FaceFX Studio
        /// </summary>
        public FxaAnimationData FxaData { get; set; }
        
        /// <summary>
        /// Whether to use text-to-phoneme fallback if no FXA data
        /// </summary>
        public bool UseTextFallback { get; set; } = true;
    }

    /// <summary>
    /// Generates FaceFX animations automatically from FXA files or text
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
                    _audioDuration = EstimateDurationFromText(_tlkText);
                }

                // ALWAYS analyze audio amplitude - this is critical for natural lip sync
                if (_audioExport != null)
                {
                    _amplitudeData = AudioAnalyzer.AnalyzeAmplitude(_audioExport);
                }
                else
                {
                    _amplitudeData = new List<AmplitudeData>();
                }

                // Clear existing lip sync animations
                ClearLipSyncAnimations();

                // ALWAYS generate text-based phonemes - this is the foundation
                List<PhonemeData> textPhonemes = null;
                if (!string.IsNullOrWhiteSpace(_tlkText))
                {
                    textPhonemes = TextToPhonemeAnalyzer.AnalyzeText(_tlkText, _audioDuration);
                    _phonemes = textPhonemes;
                }

                // Generate lip sync - use the working method
                if (textPhonemes != null && textPhonemes.Count > 0)
                {
                    // Use the proven GenerateLipSyncAnimations method which works
                    GenerateLipSyncAnimations(textPhonemes);
                }
                else
                {
                    LastError = "No text provided for lip sync generation.";
                    return false;
                }

                // If we have FXA data, merge it in (it can enhance the generated animations)
                if (_options.FxaData != null && _options.FxaData.Animations.Count > 0)
                {
                    MergeFxaAnimations(_options.FxaData);
                }

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

                // Generate emotion expression
                if (_options.Emotion != EmotionType.None && _options.EmotionIntensity > 0)
                {
                    GenerateEmotionAnimation();
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

        /// <summary>
        /// Generate lip sync animations from phonemes, modulated by audio amplitude
        /// </summary>
        private void GenerateLipSyncAnimationsWithAudio(List<PhonemeData> phonemes)
        {
            if (phonemes == null || phonemes.Count == 0)
                return;

            // Group phoneme events by viseme animation
            var visemeAnimations = new Dictionary<string, List<(float time, float weight)>>();
            
            // Maximum weight cap
            const float MaxWeight = 1.0f;

            foreach (var phoneme in phonemes)
            {
                if (!PhonemeToVisemeMap.PhonemeMap.TryGetValue(phoneme.Phoneme, out var mappings))
                    continue;

                float centerTime = phoneme.StartTime + phoneme.Duration / 2f;
                
                // Get audio amplitude at this time for modulation
                float amplitudeModifier = GetAmplitudeAtTime(centerTime);
                
                // Apply both intensity scale and amplitude modulation
                // Ensure minimum modulation so animations are always visible
                float intensityScale = _options.LipSyncIntensity * Math.Max(0.5f, amplitudeModifier);

                foreach (var mapping in mappings)
                {
                    if (!visemeAnimations.ContainsKey(mapping.VisemeName))
                    {
                        visemeAnimations[mapping.VisemeName] = new List<(float time, float weight)>();
                    }

                    float weight = Math.Min(mapping.Weight * intensityScale, MaxWeight);
                    
                    // Only add the peak point - we'll interpolate the rest
                    visemeAnimations[mapping.VisemeName].Add((centerTime, weight));
                }
            }

            // Convert to proper animation curves with smooth interpolation
            foreach (var kvp in visemeAnimations)
            {
                var peakPoints = kvp.Value.OrderBy(p => p.time).ToList();
                
                if (peakPoints.Count == 0)
                    continue;

                var points = new List<FaceFXControlPoint>();
                
                // Start at 0
                points.Add(new FaceFXControlPoint 
                { 
                    time = 0f, 
                    weight = 0f, 
                    inTangent = 0f, 
                    leaveTangent = 0f 
                });

                // Process each peak point with attack and release
                float lastReleaseTime = 0f;
                
                for (int i = 0; i < peakPoints.Count; i++)
                {
                    var (peakTime, peakWeight) = peakPoints[i];
                    
                    // Calculate attack time (before peak)
                    float attackDuration = 0.04f; // 40ms attack
                    float releaseDuration = 0.04f; // 40ms release
                    
                    float attackTime = Math.Max(peakTime - attackDuration, lastReleaseTime + 0.01f);
                    float releaseTime = peakTime + releaseDuration;
                    
                    // Check if we need to add a zero point before attack
                    if (attackTime > lastReleaseTime + 0.02f)
                    {
                        points.Add(new FaceFXControlPoint
                        {
                            time = attackTime - 0.01f,
                            weight = 0f,
                            inTangent = 0f,
                            leaveTangent = 0f
                        });
                    }
                    
                    // Attack point (ramp up)
                    points.Add(new FaceFXControlPoint
                    {
                        time = attackTime,
                        weight = peakWeight * 0.3f,
                        inTangent = 0f,
                        leaveTangent = peakWeight * 5f
                    });
                    
                    // Peak point
                    points.Add(new FaceFXControlPoint
                    {
                        time = peakTime,
                        weight = peakWeight,
                        inTangent = 0f,
                        leaveTangent = 0f
                    });
                    
                    // Release point (ramp down) - only if not overlapping with next peak
                    bool hasNextPeak = i < peakPoints.Count - 1;
                    float nextPeakTime = hasNextPeak ? peakPoints[i + 1].time : float.MaxValue;
                    
                    if (releaseTime < nextPeakTime - 0.05f)
                    {
                        points.Add(new FaceFXControlPoint
                        {
                            time = releaseTime,
                            weight = 0f,
                            inTangent = -peakWeight * 5f,
                            leaveTangent = 0f
                        });
                        lastReleaseTime = releaseTime;
                    }
                    else
                    {
                        lastReleaseTime = peakTime;
                    }
                }
                
                // End at 0
                if (points.Count > 0 && points[^1].time < _audioDuration - 0.01f)
                {
                    // Add final zero point
                    points.Add(new FaceFXControlPoint
                    {
                        time = Math.Min(lastReleaseTime + 0.05f, _audioDuration),
                        weight = 0f,
                        inTangent = 0f,
                        leaveTangent = 0f
                    });
                    
                    if (points[^1].time < _audioDuration - 0.01f)
                    {
                        points.Add(new FaceFXControlPoint
                        {
                            time = _audioDuration,
                            weight = 0f,
                            inTangent = 0f,
                            leaveTangent = 0f
                        });
                    }
                }

                // Remove duplicate times (keep highest weight)
                points = RemoveDuplicateTimes(points);
                
                AddAnimation(kvp.Key, points);
            }
        }

        /// <summary>
        /// Remove points with duplicate times, keeping the highest weight
        /// </summary>
        private List<FaceFXControlPoint> RemoveDuplicateTimes(List<FaceFXControlPoint> points)
        {
            if (points.Count < 2)
                return points;

            var result = new List<FaceFXControlPoint>();
            var sortedPoints = points.OrderBy(p => p.time).ToList();
            
            FaceFXControlPoint current = sortedPoints[0];
            
            for (int i = 1; i < sortedPoints.Count; i++)
            {
                var next = sortedPoints[i];
                
                if (Math.Abs(next.time - current.time) < 0.005f)
                {
                    // Same time - keep the one with higher weight
                    if (next.weight > current.weight)
                    {
                        current = next;
                    }
                }
                else
                {
                    result.Add(current);
                    current = next;
                }
            }
            
            result.Add(current);
            return result;
        }

        /// <summary>
        /// Merge FXA animation data - only replaces if FXA has meaningful data
        /// </summary>
        private void MergeFxaAnimations(FxaAnimationData fxaData)
        {
            if (fxaData == null) return;

            foreach (var kvp in fxaData.Animations)
            {
                string animName = kvp.Key;
                FxaAnimation fxaAnim = kvp.Value;

                // Skip non-lip-sync animations or empty animations
                if (!animName.StartsWith("m_") || fxaAnim.Keys.Count == 0)
                    continue;

                // Check if the FXA animation has any meaningful (non-zero) values
                bool hasNonZeroValues = fxaAnim.Keys.Any(k => Math.Abs(k.Value) > 0.01f);
                if (!hasNonZeroValues)
                {
                    // FXA has this animation but it's all zeros - skip it
                    // Keep the text-generated animation instead
                    continue;
                }

                // Check if we already have this animation
                int existingIndex = -1;
                for (int i = 0; i < _line.AnimationNames.Count; i++)
                {
                    int nameIdx = _line.AnimationNames[i];
                    if (nameIdx >= 0 && nameIdx < _faceFX.Names.Count && _faceFX.Names[nameIdx] == animName)
                    {
                        existingIndex = i;
                        break;
                    }
                }

                // Convert FXA keys to control points
                var points = new List<FaceFXControlPoint>();
                
                float fxaMinTime = fxaAnim.Keys.Min(k => k.Time);
                float fxaMaxTime = fxaAnim.Keys.Max(k => k.Time);
                float fxaDuration = fxaMaxTime - fxaMinTime;
                float timeScale = fxaDuration > 0.01f ? _audioDuration / fxaDuration : 1.0f;

                foreach (var key in fxaAnim.Keys)
                {
                    float scaledTime = (key.Time - fxaMinTime) * timeScale;
                    float scaledValue = key.Value * _options.LipSyncIntensity;
                    
                    // Modulate with audio amplitude
                    float ampMod = GetAmplitudeAtTime(scaledTime);
                    scaledValue *= Math.Max(0.7f, ampMod); // At least 70% of the value

                    points.Add(new FaceFXControlPoint
                    {
                        time = scaledTime,
                        weight = scaledValue,
                        inTangent = key.InTangent,
                        leaveTangent = key.OutTangent
                    });
                }

                points = points.OrderBy(p => p.time).ToList();
                
                // Final check: does the converted data have any significant values?
                bool hasSignificantPoints = points.Any(p => Math.Abs(p.weight) > 0.02f);
                if (!hasSignificantPoints)
                {
                    // Even after conversion, no significant values - skip
                    continue;
                }

                // Ensure we have start and end points
                if (points.Count > 0 && points[0].time > 0.01f)
                {
                    points.Insert(0, new FaceFXControlPoint { time = 0f, weight = 0f, inTangent = 0f, leaveTangent = 0f });
                }
                if (points.Count > 0 && points[^1].time < _audioDuration - 0.01f)
                {
                    points.Add(new FaceFXControlPoint { time = _audioDuration, weight = 0f, inTangent = 0f, leaveTangent = 0f });
                }

                if (existingIndex >= 0)
                {
                    // Replace existing animation with FXA data (FXA takes priority)
                    ReplaceAnimation(existingIndex, points);
                }
                else
                {
                    // Add new animation
                    AddAnimation(animName, points);
                }
            }
        }

        /// <summary>
        /// Refine animation timing using phoneme events from FXT
        /// </summary>
        private void RefineWithPhonemeEvents(List<(string phoneme, float startTime, float endTime)> phonemeEvents)
        {
            // This adjusts the timing of existing animations based on precise phoneme timing
            // For now, we'll add additional keyframes at phoneme boundaries
            
            foreach (var (phoneme, startTime, endTime) in phonemeEvents)
            {
                if (!PhonemeToVisemeMap.PhonemeMap.TryGetValue(phoneme.ToUpper(), out var mappings))
                    continue;

                float ampMod = GetAmplitudeAtTime((startTime + endTime) / 2f);
                float intensity = _options.LipSyncIntensity * (0.5f + ampMod * 0.5f);

                foreach (var mapping in mappings)
                {
                    // Find this animation
                    int animIndex = -1;
                    for (int i = 0; i < _line.AnimationNames.Count; i++)
                    {
                        int nameIdx = _line.AnimationNames[i];
                        if (nameIdx >= 0 && nameIdx < _faceFX.Names.Count && _faceFX.Names[nameIdx] == mapping.VisemeName)
                        {
                            animIndex = i;
                            break;
                        }
                    }

                    if (animIndex >= 0)
                    {
                        // Add refinement keyframes
                        var newPoints = new List<FaceFXControlPoint>
                        {
                            new FaceFXControlPoint { time = startTime, weight = 0f, inTangent = 0f, leaveTangent = 0f },
                            new FaceFXControlPoint { time = (startTime + endTime) / 2f, weight = mapping.Weight * intensity, inTangent = 0f, leaveTangent = 0f },
                            new FaceFXControlPoint { time = endTime, weight = 0f, inTangent = 0f, leaveTangent = 0f }
                        };
                        AppendToAnimation(animIndex, newPoints);
                    }
                }
            }
        }

        /// <summary>
        /// Replace an existing animation's points
        /// </summary>
        private void ReplaceAnimation(int animIndex, List<FaceFXControlPoint> newPoints)
        {
            // Calculate point offset
            int pointOffset = 0;
            for (int i = 0; i < animIndex; i++)
            {
                pointOffset += _line.NumKeys[i];
            }

            // Remove old points
            int oldCount = _line.NumKeys[animIndex];
            if (oldCount > 0 && pointOffset < _line.Points.Count)
            {
                _line.Points.RemoveRange(pointOffset, Math.Min(oldCount, _line.Points.Count - pointOffset));
            }

            // Insert new points
            _line.Points.InsertRange(pointOffset, newPoints);
            _line.NumKeys[animIndex] = newPoints.Count;
        }

        /// <summary>
        /// Merge nearby control points to avoid jitter
        /// </summary>
        private List<FaceFXControlPoint> MergeNearbyPoints(List<FaceFXControlPoint> points, float threshold)
        {
            if (points.Count < 2) return points;

            var result = new List<FaceFXControlPoint> { points[0] };

            for (int i = 1; i < points.Count; i++)
            {
                var last = result[^1];
                var current = points[i];

                if (current.time - last.time < threshold)
                {
                    // Merge by keeping the higher weight
                    if (current.weight > last.weight)
                    {
                        result[^1] = current;
                    }
                }
                else
                {
                    result.Add(current);
                }
            }

            return result;
        }

        /// <summary>
        /// Get audio amplitude at a specific time (0-1 range) with interpolation
        /// </summary>
        private float GetAmplitudeAtTime(float time)
        {
            if (_amplitudeData == null || _amplitudeData.Count == 0)
                return 1.0f; // Default to full intensity if no amplitude data

            // Find the two closest samples for interpolation
            AmplitudeData before = null;
            AmplitudeData after = null;

            foreach (var amp in _amplitudeData)
            {
                if (amp.Time <= time)
                {
                    if (before == null || amp.Time > before.Time)
                        before = amp;
                }
                if (amp.Time >= time)
                {
                    if (after == null || amp.Time < after.Time)
                        after = amp;
                }
            }

            // Interpolate between the two samples
            float amplitude;
            if (before == null && after == null)
            {
                amplitude = 1.0f;
            }
            else if (before == null)
            {
                amplitude = after.NormalizedAmplitude;
            }
            else if (after == null)
            {
                amplitude = before.NormalizedAmplitude;
            }
            else if (Math.Abs(after.Time - before.Time) < 0.001f)
            {
                amplitude = before.NormalizedAmplitude;
            }
            else
            {
                // Linear interpolation
                float t = (time - before.Time) / (after.Time - before.Time);
                amplitude = before.NormalizedAmplitude + t * (after.NormalizedAmplitude - before.NormalizedAmplitude);
            }

            // Use full amplitude for maximum mouth movement
            return Math.Max(0.8f, amplitude);
        }

        /// <summary>
        /// Use text analysis to supplement FXA/FXT data - fill in missing animations
        /// </summary>
        private void SupplementWithTextAnalysis(List<PhonemeData> textPhonemes)
        {
            // Get the list of animation names that were already added
            var existingAnims = new HashSet<string>();
            foreach (var animIndex in _line.AnimationNames)
            {
                if (animIndex >= 0 && animIndex < _faceFX.Names.Count)
                {
                    existingAnims.Add(_faceFX.Names[animIndex]);
                }
            }

            // Generate animations from text analysis for any missing visemes
            foreach (var phoneme in textPhonemes)
            {
                if (!PhonemeToVisemeMap.PhonemeMap.TryGetValue(phoneme.Phoneme, out var mappings))
                    continue;

                foreach (var mapping in mappings)
                {
                    // Only add if this animation wasn't already added from FXA/FXT
                    if (!existingAnims.Contains(mapping.VisemeName))
                    {
                        // This viseme wasn't in the FXA/FXT data - add it from text analysis
                        // But use a reduced weight since it's supplementary
                        var points = new List<FaceFXControlPoint>
                        {
                            new FaceFXControlPoint { time = phoneme.StartTime, weight = 0f, inTangent = 0f, leaveTangent = 0f },
                            new FaceFXControlPoint { time = phoneme.StartTime + phoneme.Duration * 0.3f, weight = mapping.Weight * 0.5f * _options.LipSyncIntensity, inTangent = 0f, leaveTangent = 0f },
                            new FaceFXControlPoint { time = phoneme.StartTime + phoneme.Duration * 0.7f, weight = mapping.Weight * 0.5f * _options.LipSyncIntensity, inTangent = 0f, leaveTangent = 0f },
                            new FaceFXControlPoint { time = phoneme.StartTime + phoneme.Duration, weight = 0f, inTangent = 0f, leaveTangent = 0f }
                        };

                        // Check if we already have some data for this animation from a previous phoneme
                        int existingIndex = -1;
                        for (int i = 0; i < _line.AnimationNames.Count; i++)
                        {
                            int nameIdx = _line.AnimationNames[i];
                            if (nameIdx >= 0 && nameIdx < _faceFX.Names.Count && _faceFX.Names[nameIdx] == mapping.VisemeName)
                            {
                                existingIndex = i;
                                break;
                            }
                        }

                        if (existingIndex >= 0)
                        {
                            // Append to existing animation
                            AppendToAnimation(existingIndex, points);
                        }
                        else
                        {
                            // Create new animation
                            AddAnimation(mapping.VisemeName, points);
                            existingAnims.Add(mapping.VisemeName);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Append points to an existing animation
        /// </summary>
        private void AppendToAnimation(int animIndex, List<FaceFXControlPoint> newPoints)
        {
            // Calculate the point offset for this animation
            int pointOffset = 0;
            for (int i = 0; i < animIndex; i++)
            {
                pointOffset += _line.NumKeys[i];
            }

            // Insert the new points at the correct position (sorted by time)
            int insertIndex = pointOffset + _line.NumKeys[animIndex];
            
            // Find correct insertion point based on time
            for (int i = pointOffset; i < pointOffset + _line.NumKeys[animIndex]; i++)
            {
                if (i < _line.Points.Count && _line.Points[i].time > newPoints[0].time)
                {
                    insertIndex = i;
                    break;
                }
            }

            _line.Points.InsertRange(insertIndex, newPoints);
            _line.NumKeys[animIndex] += newPoints.Count;
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

        /// <summary>
        /// Import animation curves directly from FXA data
        /// </summary>
        private void ImportFxaAnimations(FxaAnimationData fxaData)
        {
            float duration = Math.Max(_audioDuration, 1.0f);
            float intensityScale = _options.LipSyncIntensity;

            foreach (var kvp in fxaData.Animations)
            {
                string animName = kvp.Key;
                FxaAnimation fxaAnim = kvp.Value;

                // Only import lip sync animations (m_ prefix)
                if (!animName.StartsWith("m_"))
                    continue;

                if (fxaAnim.Keys.Count == 0)
                    continue;

                // Convert FXA keys to FaceFX control points
                var points = new List<FaceFXControlPoint>();
                
                // Get the time range of the FXA animation
                float fxaMinTime = fxaAnim.Keys.Min(k => k.Time);
                float fxaMaxTime = fxaAnim.Keys.Max(k => k.Time);
                float fxaDuration = fxaMaxTime - fxaMinTime;
                
                // Scale time to match our audio duration if needed
                float timeScale = fxaDuration > 0.01f ? duration / fxaDuration : 1.0f;

                foreach (var key in fxaAnim.Keys)
                {
                    // Scale time to match audio duration
                    float scaledTime = (key.Time - fxaMinTime) * timeScale;
                    
                    // Apply intensity scaling to the value
                    float scaledValue = key.Value * intensityScale;
                    
                    points.Add(new FaceFXControlPoint
                    {
                        time = scaledTime,
                        weight = scaledValue,
                        inTangent = key.InTangent,
                        leaveTangent = key.OutTangent
                    });
                }

                // Ensure we have start and end points at 0
                if (points.Count > 0)
                {
                    if (points[0].time > 0.01f)
                    {
                        points.Insert(0, new FaceFXControlPoint { time = 0f, weight = 0f, inTangent = 0f, leaveTangent = 0f });
                    }
                    if (points[^1].time < duration - 0.01f)
                    {
                        points.Add(new FaceFXControlPoint { time = duration, weight = 0f, inTangent = 0f, leaveTangent = 0f });
                    }
                }

                // Sort by time
                points = points.OrderBy(p => p.time).ToList();

                AddAnimation(animName, points);
            }
        }
















        private void GenerateLipSyncAnimations(List<PhonemeData> phonemes)
        {
            float duration = Math.Max(_audioDuration, 1.0f);
            
            // === CORRECT APPROACH ===
            // Text determines WHICH animations (phonemes -> visemes)
            // Audio determines TIMING, WIDTH, and STRENGTH
            
            // Step 1: Analyze audio to find speech segments and amplitude envelope
            var audioSegments = AnalyzeAudioForSpeechSegments(duration);
            
            // Step 2: Map text phonemes to audio timing
            var timedPhonemes = MapPhonemesToAudioTiming(phonemes, audioSegments, duration);
            
            // Step 3: Generate viseme curves based on timed phonemes
            // Use a sampled curve approach for smoother animation
            var visemeSamples = new Dictionary<string, float[]>();
            
            // Sample rate: 30 samples per second (smooth enough, not too dense)
            const float sampleRate = 30f;
            int numSamples = (int)(duration * sampleRate) + 1;
            
            // Initialize all visemes with zero samples
            foreach (var viseme in PhonemeToVisemeMap.HumanFemaleVisemes)
            {
                visemeSamples[viseme] = new float[numSamples];
            }

            // Process each timed phoneme - add contribution to the sampled curves
            foreach (var timedPhoneme in timedPhonemes)
            {
                if (!PhonemeToVisemeMap.PhonemeMap.TryGetValue(timedPhoneme.Phoneme, out var mappings))
                    continue;

                float peakTime = timedPhoneme.StartTime + timedPhoneme.Duration * 0.5f;
                float intensity = timedPhoneme.Intensity;
                
                // Calculate envelope parameters - faster attack/release for snappier animation
                float attackStart = timedPhoneme.StartTime;
                float attackEnd = timedPhoneme.StartTime + timedPhoneme.Duration * 0.25f;
                float releaseStart = timedPhoneme.StartTime + timedPhoneme.Duration * 0.75f;
                float releaseEnd = timedPhoneme.StartTime + timedPhoneme.Duration;
                
                // Add contribution to each mapped viseme
                foreach (var mapping in mappings)
                {
                    if (!visemeSamples.ContainsKey(mapping.VisemeName))
                        continue;
                    
                    float[] samples = visemeSamples[mapping.VisemeName];
                    
                    // Full weight for proper mouth opening
                    float peakWeight = mapping.Weight * intensity * _options.LipSyncIntensity;
                    peakWeight = Math.Min(peakWeight, 1.0f);
                    
                    if (peakWeight < 0.02f)
                        continue;
                    
                    // Add this phoneme's contribution to the curve using a smooth envelope
                    for (int i = 0; i < numSamples; i++)
                    {
                        float t = i / sampleRate;
                        
                        if (t < attackStart || t > releaseEnd)
                            continue;
                        
                        float weight = 0f;
                        
                        if (t < attackEnd)
                        {
                            // Attack phase - smooth ramp up
                            float attackT = (t - attackStart) / (attackEnd - attackStart);
                            weight = peakWeight * SmoothStep(attackT);
                        }
                        else if (t < releaseStart)
                        {
                            // Sustain phase - hold at peak
                            weight = peakWeight;
                        }
                        else
                        {
                            // Release phase - smooth ramp down
                            float releaseT = (t - releaseStart) / (releaseEnd - releaseStart);
                            weight = peakWeight * (1f - SmoothStep(releaseT));
                        }
                        
                        // Use max blending (like FaceFX does)
                        samples[i] = Math.Max(samples[i], weight);
                    }
                }
            }

            // Convert sampled curves to keyframes (with intelligent decimation)
            foreach (var viseme in PhonemeToVisemeMap.HumanFemaleVisemes)
            {
                var samples = visemeSamples[viseme];
                
                // Apply smoothing to reduce jitter - more smoothing for m_Open which gets many triggers
                int smoothingPasses = viseme == "m_Open" ? 3 : 1;
                for (int pass = 0; pass < smoothingPasses; pass++)
                {
                    SmoothSamplesInPlace(samples);
                }
                
                var keyframes = ConvertSamplesToKeyframes(samples, sampleRate, duration);
                
                if (keyframes.Count >= 2)
                {
                    AddAnimation(viseme, keyframes);
                }
            }
        }

        /// <summary>
        /// Smooth samples in-place using a 3-point moving average
        /// </summary>
        private void SmoothSamplesInPlace(float[] samples)
        {
            if (samples.Length < 3)
                return;

            float prev = samples[0];
            for (int i = 1; i < samples.Length - 1; i++)
            {
                float current = samples[i];
                float next = samples[i + 1];
                samples[i] = (prev + current + next) / 3f;
                prev = current;
            }
        }

        /// <summary>
        /// Smooth step function for smooth attack/release
        /// </summary>
        private float SmoothStep(float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// Convert sampled curve to keyframes with intelligent decimation
        /// Only keeps keyframes at significant changes (peaks, valleys, inflection points)
        /// </summary>
        private List<FaceFXControlPoint> ConvertSamplesToKeyframes(float[] samples, float sampleRate, float duration)
        {
            var keyframes = new List<FaceFXControlPoint>();
            
            if (samples.Length < 2)
            {
                keyframes.Add(new FaceFXControlPoint { time = 0f, weight = 0f, inTangent = 0f, leaveTangent = 0f });
                keyframes.Add(new FaceFXControlPoint { time = duration, weight = 0f, inTangent = 0f, leaveTangent = 0f });
                return keyframes;
            }

            // Always start at 0
            keyframes.Add(new FaceFXControlPoint { time = 0f, weight = samples[0], inTangent = 0f, leaveTangent = 0f });

            // Faster keyframe interval for snappier animation
            const float minKeyframeInterval = 0.08f; // 80ms between keyframes
            const float significanceThreshold = 0.02f; // Lower threshold for more responsive animation
            
            float lastKeyframeTime = 0f;
            float lastKeyframeWeight = samples[0];
            
            for (int i = 1; i < samples.Length - 1; i++)
            {
                float time = i / sampleRate;
                float weight = samples[i];
                float prevWeight = samples[i - 1];
                float nextWeight = samples[i + 1];
                
                // Check if this is a significant point
                bool isPeak = prevWeight < weight && weight > nextWeight && weight > 0.015f;
                bool isValley = prevWeight > weight && weight < nextWeight && lastKeyframeWeight > 0.015f;
                bool isSignificantChange = Math.Abs(weight - lastKeyframeWeight) > significanceThreshold;
                bool hasEnoughTime = time - lastKeyframeTime >= minKeyframeInterval;
                
                if (hasEnoughTime && (isPeak || isValley || isSignificantChange))
                {
                    keyframes.Add(new FaceFXControlPoint
                    {
                        time = time,
                        weight = weight,
                        inTangent = 0f,
                        leaveTangent = 0f
                    });
                    lastKeyframeTime = time;
                    lastKeyframeWeight = weight;
                }
            }

            // Always end at 0
            keyframes.Add(new FaceFXControlPoint { time = duration, weight = samples[^1], inTangent = 0f, leaveTangent = 0f });

            return keyframes;
        }

        /// <summary>
        /// Analyze audio to find speech segments with amplitude information
        /// </summary>
        private List<AudioSegment> AnalyzeAudioForSpeechSegments(float duration)
        {
            var segments = new List<AudioSegment>();
            
            if (_amplitudeData == null || _amplitudeData.Count < 2)
            {
                // No audio data - create one segment spanning the whole duration
                segments.Add(new AudioSegment
                {
                    StartTime = 0f,
                    EndTime = duration,
                    PeakAmplitude = 0.8f,
                    AverageAmplitude = 0.8f
                });
                return segments;
            }

            // Find speech segments based on amplitude threshold
            const float speechThreshold = 0.15f; // Minimum amplitude to consider as speech
            const float minSegmentDuration = 0.1f; // Minimum 100ms segment
            const float minGapToSplit = 0.15f; // Minimum 150ms silence to split segments
            
            bool inSpeech = false;
            float segmentStart = 0f;
            float peakAmp = 0f;
            float sumAmp = 0f;
            int ampCount = 0;
            float lastSpeechTime = 0f;
            
            foreach (var amp in _amplitudeData)
            {
                if (!inSpeech && amp.NormalizedAmplitude > speechThreshold)
                {
                    // Start of speech segment
                    inSpeech = true;
                    segmentStart = amp.Time;
                    peakAmp = amp.NormalizedAmplitude;
                    sumAmp = amp.NormalizedAmplitude;
                    ampCount = 1;
                    lastSpeechTime = amp.Time;
                }
                else if (inSpeech)
                {
                    if (amp.NormalizedAmplitude > speechThreshold)
                    {
                        // Continue speech segment
                        peakAmp = Math.Max(peakAmp, amp.NormalizedAmplitude);
                        sumAmp += amp.NormalizedAmplitude;
                        ampCount++;
                        lastSpeechTime = amp.Time;
                    }
                    else if (amp.Time - lastSpeechTime >= minGapToSplit)
                    {
                        // End of speech segment (long enough silence)
                        if (lastSpeechTime - segmentStart >= minSegmentDuration)
                        {
                            segments.Add(new AudioSegment
                            {
                                StartTime = segmentStart,
                                EndTime = lastSpeechTime,
                                PeakAmplitude = peakAmp,
                                AverageAmplitude = ampCount > 0 ? sumAmp / ampCount : peakAmp
                            });
                        }
                        inSpeech = false;
                    }
                }
            }
            
            // Close final segment if still in speech
            if (inSpeech && ampCount > 0 && lastSpeechTime - segmentStart >= minSegmentDuration)
            {
                segments.Add(new AudioSegment
                {
                    StartTime = segmentStart,
                    EndTime = Math.Min(lastSpeechTime + 0.05f, duration),
                    PeakAmplitude = peakAmp,
                    AverageAmplitude = sumAmp / ampCount
                });
            }
            
            // If no segments found, create one spanning the whole duration
            if (segments.Count == 0)
            {
                segments.Add(new AudioSegment
                {
                    StartTime = 0f,
                    EndTime = duration,
                    PeakAmplitude = 0.8f,
                    AverageAmplitude = 0.8f
                });
            }
            
            return segments;
        }

        /// <summary>
        /// Map text phonemes to audio timing based on speech segments
        /// </summary>
        private List<TimedPhoneme> MapPhonemesToAudioTiming(List<PhonemeData> phonemes, List<AudioSegment> audioSegments, float duration)
        {
            var timedPhonemes = new List<TimedPhoneme>();
            
            if (phonemes.Count == 0)
                return timedPhonemes;
            
            // Calculate total speech duration from audio segments
            float totalSpeechTime = audioSegments.Sum(s => s.EndTime - s.StartTime);
            
            // Calculate average phoneme duration based on audio
            // Slightly faster range for snappier animation
            float avgPhonemeDuration = totalSpeechTime / phonemes.Count;
            avgPhonemeDuration = Math.Max(0.06f, Math.Min(avgPhonemeDuration, 0.15f)); // Clamp to 60-150ms range
            
            // Distribute phonemes across audio segments
            int phonemeIndex = 0;
            
            foreach (var segment in audioSegments)
            {
                float segmentDuration = segment.EndTime - segment.StartTime;
                int phonemesInSegment = (int)Math.Round(segmentDuration / avgPhonemeDuration);
                phonemesInSegment = Math.Max(1, Math.Min(phonemesInSegment, phonemes.Count - phonemeIndex));
                
                float phonemeDuration = segmentDuration / phonemesInSegment;
                // Ensure minimum duration - slightly faster
                phonemeDuration = Math.Max(phonemeDuration, 0.06f);
                
                for (int i = 0; i < phonemesInSegment && phonemeIndex < phonemes.Count; i++)
                {
                    var phoneme = phonemes[phonemeIndex];
                    
                    // Get local amplitude at this position
                    float localTime = segment.StartTime + i * phonemeDuration + phonemeDuration * 0.5f;
                    float localAmplitude = GetAmplitudeAtTime(localTime);
                    
                    timedPhonemes.Add(new TimedPhoneme
                    {
                        Phoneme = phoneme.Phoneme,
                        StartTime = segment.StartTime + i * phonemeDuration,
                        Duration = phonemeDuration,
                        Intensity = Math.Max(0.9f, localAmplitude) // High minimum intensity for full mouth movement
                    });
                    
                    phonemeIndex++;
                }
            }
            
            // If we have remaining phonemes, distribute them evenly at the end
            if (phonemeIndex < phonemes.Count)
            {
                float remainingTime = duration - (audioSegments.Count > 0 ? audioSegments[^1].EndTime : 0f);
                if (remainingTime > 0.1f)
                {
                    int remaining = phonemes.Count - phonemeIndex;
                    float startTime = audioSegments.Count > 0 ? audioSegments[^1].EndTime : 0f;
                    float phonemeDuration = Math.Max(remainingTime / remaining, 0.08f);
                    
                    for (int i = phonemeIndex; i < phonemes.Count; i++)
                    {
                        timedPhonemes.Add(new TimedPhoneme
                        {
                            Phoneme = phonemes[i].Phoneme,
                            StartTime = startTime + (i - phonemeIndex) * phonemeDuration,
                            Duration = phonemeDuration,
                            Intensity = 1.0f // Full intensity
                        });
                    }
                }
            }
            
            return timedPhonemes;
        }

        /// <summary>
        /// Clean up keyframes by removing duplicates and merging very close points
        /// </summary>
        private List<FaceFXControlPoint> CleanupKeyframes(List<FaceFXControlPoint> keyframes)
        {
            if (keyframes.Count < 2)
                return keyframes;

            var result = new List<FaceFXControlPoint>();
            const float minTimeDiff = 0.05f; // 50ms minimum between keyframes for smooth animation
            
            foreach (var kf in keyframes)
            {
                if (result.Count == 0)
                {
                    result.Add(kf);
                    continue;
                }
                
                var last = result[^1];
                
                if (kf.time - last.time < minTimeDiff)
                {
                    // Too close - keep the one with higher weight
                    if (kf.weight > last.weight)
                    {
                        result[^1] = kf;
                    }
                }
                else
                {
                    result.Add(kf);
                }
            }
            
            return result;
        }

        /// <summary>
        /// Audio segment with amplitude information
        /// </summary>
        private class AudioSegment
        {
            public float StartTime { get; set; }
            public float EndTime { get; set; }
            public float PeakAmplitude { get; set; }
            public float AverageAmplitude { get; set; }
        }

        /// <summary>
        /// Phoneme with timing derived from audio
        /// </summary>
        private class TimedPhoneme
        {
            public string Phoneme { get; set; }
            public float StartTime { get; set; }
            public float Duration { get; set; }
            public float Intensity { get; set; }
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

        /// <summary>
        /// Generates emotion expression animations
        /// </summary>
        private void GenerateEmotionAnimation()
        {
            float duration = Math.Max(_audioDuration, 1.0f);
            float intensity = _options.EmotionIntensity;

            // Define emotion animation mappings
            // Each emotion maps to specific facial animations with weights
            var emotionMappings = GetEmotionMappings(_options.Emotion);

            foreach (var mapping in emotionMappings)
            {
                var points = new List<FaceFXControlPoint>();
                
                // Ease in at the start
                points.Add(new FaceFXControlPoint 
                { 
                    time = 0f, 
                    weight = 0f, 
                    inTangent = 0f, 
                    leaveTangent = 0f 
                });

                // Ramp up to full intensity
                float rampUpTime = Math.Min(0.3f, duration * 0.1f);
                points.Add(new FaceFXControlPoint 
                { 
                    time = rampUpTime, 
                    weight = mapping.Weight * intensity, 
                    inTangent = 0f, 
                    leaveTangent = 0f 
                });

                // Hold emotion throughout with slight variation for naturalness
                float holdTime = duration - 0.3f;
                if (holdTime > rampUpTime + 0.5f)
                {
                    // Add some variation in the middle for naturalness
                    float midTime = (rampUpTime + holdTime) / 2f;
                    float variation = 0.9f + (float)new Random().NextDouble() * 0.2f; // 90-110%
                    points.Add(new FaceFXControlPoint 
                    { 
                        time = midTime, 
                        weight = mapping.Weight * intensity * variation, 
                        inTangent = 0f, 
                        leaveTangent = 0f 
                    });
                }

                // Hold near the end
                points.Add(new FaceFXControlPoint 
                { 
                    time = Math.Max(holdTime, rampUpTime + 0.1f), 
                    weight = mapping.Weight * intensity, 
                    inTangent = 0f, 
                    leaveTangent = 0f 
                });

                // Ease out at the end
                points.Add(new FaceFXControlPoint 
                { 
                    time = duration, 
                    weight = 0f, 
                    inTangent = 0f, 
                    leaveTangent = 0f 
                });

                AddAnimation(mapping.AnimationName, points);
            }
        }






        /// <summary>
        /// Gets the animation mappings for a specific emotion
        /// Uses actual Mass Effect FaceFX emotion animation names
        /// Animation naming pattern: E_[Category]_[Emotion][Number]
        /// Categories: S=Smile/Mouth, B=Brow, Y=Eye, WB=Wide Brow/Full Face, D=Other, D_S=Other Mouth
        /// Each emotion should have variants for all face parts for complete expression
        /// </summary>
        private List<EmotionAnimationMapping> GetEmotionMappings(EmotionType emotion)
        {
            var mappings = new List<EmotionAnimationMapping>();

            // Get available emotion animation names from the FaceFX asset
            var availableEmotionAnims = _faceFX.Names
                .Where(n => n.StartsWith("E_") || n.Contains("Blink") || n.Contains("Eyebrow"))
                .ToHashSet();

            switch (emotion)
            {
                case EmotionType.Anger:
                    // Anger - all face part categories (variants 2, 3)
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Anger2", 0.8f);      // S = Mouth
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Anger2", 0.8f);      // B = Brow
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Anger2", 0.8f);      // Y = Eye
                    AddIfAvailable(mappings, availableEmotionAnims, "E_WB_Anger2", 0.8f);     // WB = Wide Brow
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Anger3", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Anger3", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Anger3", 0.6f);
                    // Stern as alternative - all face parts
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Stern1", 0.5f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Stern1", 0.5f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Stern1", 0.5f);
                    break;

                case EmotionType.Disgust:
                    // Disdain - all face part categories (variants 1, 2, 3)
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Disdain2", 0.8f);    // S
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Disdain2", 0.8f);    // B
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Disdain2", 0.8f);    // Y
                    AddIfAvailable(mappings, availableEmotionAnims, "E_WB_Disdain2", 0.8f);   // WB
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Disdain1", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Disdain1", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Disdain1", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Disdain3", 0.5f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Disdain3", 0.5f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Disdain3", 0.5f);
                    break;

                case EmotionType.Fear:
                    // Fear/Terror - all categories
                    AddIfAvailable(mappings, availableEmotionAnims, "E_D_B_Terror1", 0.8f);   // D_B
                    AddIfAvailable(mappings, availableEmotionAnims, "E_D_S_Concern", 0.7f);   // D_S
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Wounded_Squint", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_NervousLoop", 0.6f);
                    // Concern - all face parts
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Concern1", 0.7f);    // S
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Concern1", 0.7f);    // B
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Concern2", 0.7f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Concern1", 0.7f);    // Y
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Concern2", 0.7f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_WB_Concern1", 0.7f);   // WB
                    break;

                case EmotionType.Happy:
                    // Joy - all face part categories (variants 1, 2, 3)
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Joy1", 0.8f);        // S
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Joy2", 0.8f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Joy1", 0.8f);        // B
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Joy3", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Joy1", 0.8f);        // Y
                    AddIfAvailable(mappings, availableEmotionAnims, "E_WB_Joy1", 0.8f);       // WB
                    // Satisfaction - all face parts
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Satisfaction1", 0.7f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Satisfaction2", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Satisfaction3", 0.5f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Satisfaction1", 0.7f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Satisfaction2", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Satisfaction3", 0.5f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Satisfaction1", 0.7f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Satisfaction2", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Satisfaction3", 0.5f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_WB_Satisfaction3", 0.6f);
                    // Laughter - all face parts
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Laughter1", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Laughter1", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Laughter1", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Happy_Diabolical", 0.4f);
                    break;

                case EmotionType.Sad:
                    // Sad expressions
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Sad_Disappointed", 0.8f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Sad_Shocked", 0.6f);
                    // Dejection - all face part categories
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Dejection1", 0.8f);  // S
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Dejection3", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Dejection1", 0.8f);  // B
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Dejection3", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Dejection1", 0.8f);  // Y
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Dejection3", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_D_S_Concern", 0.5f);   // D_S
                    break;

                case EmotionType.Surprise:
                    // Shock/Surprise
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Neutral_Shock", 0.8f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Sad_Shocked", 0.7f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_D_S_MouthOpen", 0.7f); // D_S
                    AddIfAvailable(mappings, availableEmotionAnims, "E_D_Blink", 0.5f);       // D
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Neutral_Perplexed", 0.5f);
                    // Concern for wide eyes - all face parts
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Concern1", 0.5f);    // S
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Concern1", 0.6f);    // B
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Concern2", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Concern1", 0.6f);    // Y
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Concern2", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_WB_Concern1", 0.6f);   // WB
                    break;

                case EmotionType.Contempt:
                    // Disdain for contempt - all face parts
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Disdain1", 0.7f);    // S
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Disdain1", 0.7f);    // B
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Disdain1", 0.7f);    // Y
                    // Stern - all face parts
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Stern1", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Stern2", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Stern1", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Stern3", 0.5f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Stern1", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Satisfaction2", 0.4f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Happy_Diabolical", 0.5f);
                    break;

                case EmotionType.Determined:
                    // Stern - all face part categories
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Stern1", 0.8f);      // S
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Stern2", 0.7f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Stern3", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Stern1", 0.8f);      // B
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Stern3", 0.6f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Stern1", 0.8f);      // Y
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Stern3", 0.6f);
                    // Slight anger for intensity
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Anger2", 0.4f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Anger2", 0.3f);
                    break;

                case EmotionType.Worried:
                    // Concern - all face part categories
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Concern1", 0.8f);    // S
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Concern1", 0.8f);    // B
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Concern2", 0.7f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Concern1", 0.8f);    // Y
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Concern2", 0.7f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_WB_Concern1", 0.8f);   // WB
                    AddIfAvailable(mappings, availableEmotionAnims, "E_D_S_Concern", 0.6f);   // D_S
                    AddIfAvailable(mappings, availableEmotionAnims, "E_NervousLoop", 0.5f);
                    // Dejection for worry - all face parts
                    AddIfAvailable(mappings, availableEmotionAnims, "E_S_Dejection1", 0.5f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_B_Dejection1", 0.5f);
                    AddIfAvailable(mappings, availableEmotionAnims, "E_Y_Dejection1", 0.5f);
                    break;
            }

            return mappings;
        }

        /// <summary>
        /// Adds an emotion mapping only if the animation exists in the available set
        /// </summary>
        private void AddIfAvailable(List<EmotionAnimationMapping> mappings, HashSet<string> available, string animName, float weight)
        {
            if (available.Contains(animName))
            {
                mappings.Add(new EmotionAnimationMapping(animName, weight));
            }
        }

        /// <summary>
        /// Mapping for emotion to animation
        /// </summary>
        private class EmotionAnimationMapping
        {
            public string AnimationName { get; }
            public float Weight { get; }

            public EmotionAnimationMapping(string name, float weight)
            {
                AnimationName = name;
                Weight = weight;
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
