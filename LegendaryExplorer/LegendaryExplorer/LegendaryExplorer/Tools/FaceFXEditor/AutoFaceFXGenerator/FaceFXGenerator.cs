using System;
using System.Collections.Generic;
using System.Linq;
using LegendaryExplorer.Resources;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using static LegendaryExplorer.UserControls.ExportLoaderControls.FaceFXAnimSetEditorControl;

namespace LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator
{
    /// <summary>
    /// Supported character types for FaceFX generation (legacy - use FaceFXSpecies instead)
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
        /// <summary>
        /// Target game whose FaceFX rig vocabulary should be generated.
        /// </summary>
        public MEGame Game { get; set; } = MEGame.LE3;

        public CharacterType CharacterType { get; set; } = CharacterType.HumanFemale;
        
        /// <summary>
        /// Species for FaceFX generation - determines which phoneme-to-viseme mappings to use
        /// </summary>
        public FaceFXSpecies Species { get; set; } = FaceFXSpecies.HumanFemale;
        
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
        /// Exact layered family or rig preset selected from the audited LE3 catalog.
        /// Takes precedence over the legacy Emotion value.
        /// </summary>
        public FaceFXEmotionChoice EmotionChoice { get; set; }

        /// <summary>
        /// Adds or replaces only the selected emotion curves and preserves every
        /// existing lip-sync, blink, gaze, and gesture curve on the line.
        /// </summary>
        public bool AddEmotionToExistingLine { get; set; }
        
        /// <summary>
        /// Intensity of the emotion (0-1) - higher values create more visible expressions
        /// </summary>
        public float EmotionIntensity { get; set; } = 0.8f;
        
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
        private static readonly Lazy<FxaAnimationData> QuarianReferenceFaceFx = new(() => FxaXmlParser.ParseFxaXml(EmbeddedResources.QuarianFaceFxReference));

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

                // Analyze audio for duration and amplitude. Emotion-only edits can
                // also use the existing line's timeline when no audio export is linked.
                _audioDuration = AudioAnalyzer.GetAudioDuration(_audioExport);
                if (_audioDuration <= 0)
                {
                    float existingDuration = GetExistingLineDuration();
                    _audioDuration = _options.AddEmotionToExistingLine && existingDuration > 0f
                        ? existingDuration
                        : EstimateDurationFromText(_tlkText);
                }

                if (_options.UseAudioAmplitude && _audioExport != null)
                {
                    _amplitudeData = AudioAnalyzer.AnalyzeAmplitude(_audioExport);
                    AlignAmplitudeTimeline();
                }
                else
                {
                    _amplitudeData = new List<AmplitudeData>();
                }

                FaceFXEmotionChoice selectedEmotion = GetSelectedEmotionChoice();
                if (_options.AddEmotionToExistingLine)
                {
                    if (selectedEmotion == null || selectedEmotion.IsNone || _options.EmotionIntensity <= 0f)
                    {
                        LastError = "Select an emotion before using emotion-only mode.";
                        return false;
                    }

                    GenerateEmotionAnimation(selectedEmotion);
                    LastError = null;
                    return true;
                }

                // Clear existing lip sync animations
                ClearLipSyncAnimations();

                List<PhonemeData> textPhonemes = null;
                if (_options.UseTextFallback && !string.IsNullOrWhiteSpace(_tlkText))
                {
                    textPhonemes = TextToPhonemeAnalyzer.AnalyzeText(_tlkText, _audioDuration);
                    _phonemes = textPhonemes;
                }

                // Generate lip sync - use the working method
                bool generatedAudioLipSync = textPhonemes != null && textPhonemes.Count > 0;
                if (generatedAudioLipSync)
                {
                    // Use the proven GenerateLipSyncAnimations method which works
                    GenerateLipSyncAnimations(textPhonemes);
                }
                else
                {
                    bool hasImportedCurves = _options.FxaData?.Animations?.Count > 0;
                    if (!hasImportedCurves && _options.Species != FaceFXSpecies.Quarian)
                    {
                        LastError = "No text or imported FXA/FXT curves were provided for lip sync generation.";
                        return false;
                    }
                }

                // Quarian lines historically use the complete authored reference,
                // including its dense jawOpen curve. The generic audio-derived jaw
                // does not drive this rig correctly, so the reference replaces it.
                if (UsesAuthoredQuarianReference)
                {
                    GenerateQuarianReferenceAnimations();
                }

                // If we have FXA data, merge it in (it can enhance the generated animations)
                if (_options.FxaData != null && _options.FxaData.Animations.Count > 0)
                {
                    MergeFxaAnimations(_options.FxaData);
                }

                // Quarians already received their authored blink, eyebrow, head,
                // gaze, emphasis, talking, and gesture curves above.
                if (!UsesAuthoredQuarianReference && SupportsStandardExpressionControls && _options.GenerateBlinkAnimation)
                {
                    GenerateBlinkAnimation();
                }

                // Generate eyebrow animation for emphasis
                if (!UsesAuthoredQuarianReference && SupportsStandardExpressionControls && _options.GenerateEyebrowAnimation)
                {
                    GenerateEyebrowAnimation();
                }

                // Generate subtle head movement
                if (!UsesAuthoredQuarianReference && SupportsStandardExpressionControls && _options.GenerateHeadMovement)
                {
                    GenerateHeadMovement();
                }

                // Generate emotion expression
                if (selectedEmotion != null && !selectedEmotion.IsNone && _options.EmotionIntensity > 0)
                {
                    GenerateEmotionAnimation(selectedEmotion);
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

        private bool UsesAuthoredQuarianReference => _options.Species == FaceFXSpecies.Quarian
            && !FaceFXSpeciesCatalog.IsLegacyLegendaryGame(_options.Game);

        private bool SupportsStandardExpressionControls => _options.Species != FaceFXSpecies.EDI;

        /// <summary>
        /// Generate lip sync animations from phonemes, modulated by audio amplitude
        /// </summary>
        private void GenerateLipSyncAnimationsWithAudio(List<PhonemeData> phonemes)
        {
            if (phonemes == null || phonemes.Count == 0)
                return;

            // Get species-specific phoneme map
            var phonemeMap = PhonemeToVisemeMap.GetPhonemeMap(_options.Species, _options.Game);
            
            // Group phoneme events by viseme animation
            var visemeAnimations = new Dictionary<string, List<(float time, float weight)>>();
            
            // Maximum weight cap
            const float MaxWeight = 1.0f;

            foreach (var phoneme in phonemes)
            {
                if (!phonemeMap.TryGetValue(phoneme.Phoneme, out var mappings))
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
            var lipSyncNames = new HashSet<string>(
                PhonemeToVisemeMap.GetVisemes(_options.Species, _options.Game),
                StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in fxaData.Animations)
            {
                string animName = kvp.Key;
                FxaAnimation fxaAnim = kvp.Value;

                // Imported UDK curves are authoritative for any audited rig lip control.
                if (!lipSyncNames.Contains(animName) || fxaAnim.Keys.Count == 0)
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
                    if (nameIdx >= 0 && nameIdx < _faceFX.Names.Count &&
                        string.Equals(_faceFX.Names[nameIdx], animName, StringComparison.OrdinalIgnoreCase))
                    {
                        existingIndex = i;
                        break;
                    }
                }

                // Convert FXA keys to control points
                var points = new List<FaceFXControlPoint>();
                
                foreach (var key in fxaAnim.Keys)
                {
                    // Preserve UDK/FaceFX's absolute key times, including preroll.
                    // Rescaling every curve by its own min/max caused track-to-track
                    // desynchronization and made timing depend on key distribution.
                    float scaledTime = key.Time;
                    float scaledValue = key.Value * _options.LipSyncIntensity;

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
            
            var phonemeMap = PhonemeToVisemeMap.GetPhonemeMap(_options.Species, _options.Game);
            
            foreach (var (phoneme, startTime, endTime) in phonemeEvents)
            {
                if (!phonemeMap.TryGetValue(phoneme.ToUpper(), out var mappings))
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

            // Binary search keeps lookup cost local/constant as lines grow.
            int low = 0;
            int high = _amplitudeData.Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                if (_amplitudeData[middle].Time < time)
                    low = middle + 1;
                else
                    high = middle - 1;
            }

            AmplitudeData after = low < _amplitudeData.Count ? _amplitudeData[low] : null;
            AmplitudeData before = low > 0 ? _amplitudeData[low - 1] : after;

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

            return Math.Clamp(amplitude, 0f, 1f);
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

            var phonemeMap = PhonemeToVisemeMap.GetPhonemeMap(_options.Species, _options.Game);
            
            // Generate animations from text analysis for any missing visemes
            foreach (var phoneme in textPhonemes)
            {
                if (!phonemeMap.TryGetValue(phoneme.Phoneme, out var mappings))
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

        private float GetExistingLineDuration() => _line?.Points?.Select(point => point.time)
            .DefaultIfEmpty(0f).Max() ?? 0f;

        private void AlignAmplitudeTimeline()
        {
            if (_amplitudeData == null || _amplitudeData.Count < 2 || _audioDuration <= 0f)
                return;

            float decodedDuration = _amplitudeData[^1].Time + 0.02f;
            if (decodedDuration <= 0f)
                return;

            float scale = _audioDuration / decodedDuration;
            if (Math.Abs(scale - 1f) < 0.001f)
                return;

            foreach (AmplitudeData sample in _amplitudeData)
                sample.Time *= scale;
        }

        private void ClearLipSyncAnimations()
        {
            // Safety checks
            if (_line.AnimationNames == null || _line.AnimationNames.Count == 0)
                return;
            if (_line.NumKeys == null || _line.Points == null)
                return;

            // Remove the complete regenerated control set for the selected rig.
            // Non-human rigs do not use the m_ prefix, so prefix-only removal left
            // stale Quarian/Geth curves behind.
            var lipSyncNames = new HashSet<string>(
                PhonemeToVisemeMap.GetVisemes(_options.Species, _options.Game),
                StringComparer.OrdinalIgnoreCase);
            var indicesToRemove = new List<int>();
            for (int i = 0; i < _line.AnimationNames.Count; i++)
            {
                int nameIndex = _line.AnimationNames[i];
                if (nameIndex >= 0 && nameIndex < _faceFX.Names.Count)
                {
                    string animName = _faceFX.Names[nameIndex];
                    if (animName.StartsWith("m_", StringComparison.OrdinalIgnoreCase) || lipSyncNames.Contains(animName))
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
            float duration = Math.Max(_audioDuration, 0.02f);
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

        private void GenerateQuarianReferenceAnimations()
        {
            var referenceData = QuarianReferenceFaceFx.Value;
            float duration = Math.Max(_audioDuration, 0.02f);
            float referenceDuration = referenceData.Animations.Values
                .SelectMany(anim => anim.Keys)
                .Select(key => key.Time)
                .DefaultIfEmpty(duration)
                .Max();
            float timeScale = referenceDuration > 0.01f ? duration / referenceDuration : 1.0f;

            foreach ((string animName, FxaAnimation referenceAnimation) in referenceData.Animations)
            {
                if (referenceAnimation.Keys.Count == 0)
                {
                    continue;
                }

                var points = referenceAnimation.Keys
                    .Select(key => new FaceFXControlPoint
                    {
                        time = key.Time * timeScale,
                        weight = ScaleQuarianReferenceWeight(animName, key.Value),
                        inTangent = key.InTangent,
                        leaveTangent = key.OutTangent
                    })
                    .OrderBy(point => point.time)
                    .ToList();

                AddAnimation(animName, points);
            }
        }

        private float ScaleQuarianReferenceWeight(string animName, float weight)
        {
            if (animName == "jawOpen")
            {
                return Math.Clamp(weight * _options.LipSyncIntensity, 0f, 1f);
            }

            return weight;
        }
















        private void GenerateLipSyncAnimations(List<PhonemeData> phonemes)
        {
            float duration = Math.Max(_audioDuration, 0.02f);
            
            // === CORRECT APPROACH ===
            // Text determines WHICH animations (phonemes -> visemes)
            // Audio determines TIMING, WIDTH, and STRENGTH
            
            // Get species-specific mappings
            var phonemeMap = PhonemeToVisemeMap.GetPhonemeMap(_options.Species, _options.Game);
            var visemeNames = PhonemeToVisemeMap.GetVisemes(_options.Species, _options.Game);
            
            // Step 1: Analyze audio to find speech segments and amplitude envelope
            var audioSegments = AnalyzeAudioForSpeechSegments(duration);
            
            // Step 2: Map text phonemes to audio timing
            var timedPhonemes = MapPhonemesToAudioTiming(phonemes, audioSegments, duration);
            _phonemes = timedPhonemes.Select(phoneme => new PhonemeData
            {
                Phoneme = phoneme.Phoneme,
                StartTime = phoneme.StartTime,
                Duration = phoneme.Duration
            }).ToList();
            
            // Step 3: Generate viseme curves based on timed phonemes
            // Use a sampled curve approach for smoother animation
            var visemeSamples = new Dictionary<string, float[]>();
            
            // Sample rate: 30 samples per second (smooth enough, not too dense)
            const float sampleRate = 30f;
            int numSamples = (int)(duration * sampleRate) + 1;
            
            // Initialize all visemes with zero samples
            foreach (var viseme in visemeNames)
            {
                visemeSamples[viseme] = new float[numSamples];
            }

            // Process each timed phoneme - add contribution to the sampled curves
            foreach (var timedPhoneme in timedPhonemes)
            {
                if (!phonemeMap.TryGetValue(timedPhoneme.Phoneme, out var mappings))
                    continue;

                float intensity = timedPhoneme.Intensity;
                
                // Add contribution to each mapped viseme
                foreach (var mapping in mappings)
                {
                    string visemeName = PhonemeToVisemeMap.CanonicalizeVisemeName(mapping.VisemeName, _options.Species, _options.Game);
                    if (!visemeSamples.ContainsKey(visemeName))
                        continue;
                    
                    float[] samples = visemeSamples[visemeName];
                    
                    // Full weight for proper mouth opening
                    float peakWeight = mapping.Weight * intensity * _options.LipSyncIntensity;
                    peakWeight = Math.Min(peakWeight, 1.0f);
                    
                    if (peakWeight < 0.02f)
                        continue;
                    
                    FaceFXGenerationMath.AddLocalVisemeEnvelope(samples, sampleRate, timedPhoneme, peakWeight);
                }
            }

            // Convert sampled curves to keyframes (with intelligent decimation)
            foreach (var viseme in visemeNames)
            {
                var samples = visemeSamples[viseme];

                // One symmetric, fixed-time pass. Repeated in-place passes on m_Open
                // progressively flattened local peaks on phoneme-dense lines.
                FaceFXGenerationMath.SmoothLocally(samples);

                // Ramp the tail end of the curve to zero so the mouth closes
                // at the end of the line (prevents open-mouth freeze).
                int rampSamples = Math.Min((int)(0.15f * sampleRate), numSamples / 4);
                if (rampSamples > 0)
                {
                    int rampStart = numSamples - 1 - rampSamples;
                    for (int i = rampStart; i < numSamples; i++)
                    {
                        float t = (float)(i - rampStart) / rampSamples; // 0 → 1
                        samples[i] *= 1f - t;
                    }
                }
                // Ensure the very last sample is exactly zero
                samples[numSamples - 1] = 0f;

                // The Quarian and Geth FaceFX rigs require the complete legacy
                // animation-name inventory on each generated line. Other rigs can
                // continue omitting unused curves to avoid empty-track bloat.
                bool requiresCompleteRigList = _options.Species == FaceFXSpecies.Geth || UsesAuthoredQuarianReference;
                if (!requiresCompleteRigList && !samples.Any(value => value > 0.005f))
                    continue;

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
        /// Also enforces a maximum gap so that sustained regions always have enough density.
        /// </summary>
        private List<FaceFXControlPoint> ConvertSamplesToKeyframes(float[] samples, float sampleRate, float duration)
        {
            if (samples.Length < 2)
            {
                return new List<FaceFXControlPoint>
                {
                    new() { time = 0f, weight = 0f, inTangent = 0f, leaveTangent = 0f },
                    new() { time = duration, weight = 0f, inTangent = 0f, leaveTangent = 0f }
                };
            }

            List<int> indices = FaceFXGenerationMath.SelectKeyframeIndices(samples, sampleRate);
            return indices.Select(index => new FaceFXControlPoint
            {
                time = index == samples.Length - 1 ? duration : index / sampleRate,
                weight = index is 0 || index == samples.Length - 1 ? 0f : samples[index],
                inTangent = 0f,
                leaveTangent = 0f
            }).ToList();
        }

        /// <summary>
        /// Analyze audio to find speech segments with amplitude information
        /// </summary>
        private List<FaceFXSpeechSegment> AnalyzeAudioForSpeechSegments(float duration)
        {
            var segments = new List<FaceFXSpeechSegment>();
            
            if (_amplitudeData == null || _amplitudeData.Count < 2)
            {
                // No audio data - create one segment spanning the whole duration
                segments.Add(new FaceFXSpeechSegment
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
                            segments.Add(new FaceFXSpeechSegment
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
                segments.Add(new FaceFXSpeechSegment
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
                segments.Add(new FaceFXSpeechSegment
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
        /// Map text phonemes to audio timing based on speech segments.
        /// Every phoneme is guaranteed to be placed — they are distributed
        /// proportionally across segments by duration.
        /// </summary>
        private List<FaceFXTimedPhoneme> MapPhonemesToAudioTiming(List<PhonemeData> phonemes,
            List<FaceFXSpeechSegment> audioSegments, float duration)
        {
            return FaceFXGenerationMath.MapPhonemesToSpeech(phonemes, audioSegments, duration,
                _options.UseAudioAmplitude ? GetAmplitudeAtTime : _ => 1f);
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

            float duration = Math.Max(_audioDuration, 0.02f);
            
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
            float duration = Math.Max(_audioDuration, 0.02f);
            
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

            float duration = Math.Max(_audioDuration, 0.02f);
            
            // Generate blinks at random intervals
            float averageBlinkInterval = 1f / _options.BlinkFrequency;
            var random = new Random(123);
            float currentTime = (float)(random.NextDouble() * averageBlinkInterval * 0.5 + 0.5);

            while (currentTime < duration - 0.2f)
            {
                // A blink is a local zero/peak/zero pulse. A lone peak between the
                // endpoints interpolated the eyelids slowly across several seconds.
                float blinkDuration = 0.15f + (float)random.NextDouble() * 0.1f;
                float peakTime = currentTime + blinkDuration * 0.5f;

                points.Add(new FaceFXControlPoint { time = currentTime, weight = 0f, inTangent = 0f, leaveTangent = 0f });
                points.Add(new FaceFXControlPoint { time = peakTime, weight = 1f, inTangent = 0f, leaveTangent = 0f });
                points.Add(new FaceFXControlPoint { time = currentTime + blinkDuration, weight = 0f, inTangent = 0f, leaveTangent = 0f });

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

            float duration = Math.Max(_audioDuration, 0.02f);
            
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
                        
                        AddLocalPulse(points, centerTime, weight, duration, 0.12f, 0.16f);
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
                    AddLocalPulse(points, currentTime, weight, duration, 0.12f, 0.16f);
                    currentTime += 2.0f + (float)random.NextDouble() * 2.0f;
                }
            }

            points.Add(new FaceFXControlPoint { time = duration, weight = 0f, inTangent = 0f, leaveTangent = 0f });
            
            var sortedPoints = points.OrderBy(p => p.time).ToList();
            AddAnimation("Eyebrow_Raise", sortedPoints);
        }

        private void GenerateHeadMovement()
        {
            float duration = Math.Max(_audioDuration, 0.02f);
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
                            AddLocalPulse(points, centerTime, weight, duration, 0.20f, 0.24f);
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
                        AddLocalPulse(points, currentTime, weight, duration, 0.20f, 0.24f);
                        currentTime += interval + (float)random.NextDouble() * 0.4f;
                    }
                }

                points.Add(new FaceFXControlPoint { time = duration, weight = 0f, inTangent = 0f, leaveTangent = 0f });
                
                
                var sortedPoints = points.OrderBy(p => p.time).ToList();
                AddAnimation(axis, sortedPoints);
            }
        }

        private static void AddLocalPulse(List<FaceFXControlPoint> points, float centerTime,
            float weight, float duration, float lead, float trail)
        {
            points.Add(new FaceFXControlPoint
            {
                time = Math.Max(0f, centerTime - lead), weight = 0f, inTangent = 0f, leaveTangent = 0f
            });
            points.Add(new FaceFXControlPoint
            {
                time = Math.Clamp(centerTime, 0f, duration), weight = weight, inTangent = 0f, leaveTangent = 0f
            });
            points.Add(new FaceFXControlPoint
            {
                time = Math.Min(duration, centerTime + trail), weight = 0f, inTangent = 0f, leaveTangent = 0f
            });
        }

        /// <summary>
        /// Generates emotion expression animations
        /// </summary>
        private FaceFXEmotionChoice GetSelectedEmotionChoice()
        {
            if (_options.EmotionChoice != null)
                return _options.EmotionChoice;

            string layeredFamily = _options.Emotion switch
            {
                EmotionType.Anger => "Anger",
                EmotionType.Disgust => "Disgust",
                EmotionType.Fear => "Fear",
                EmotionType.Happy => "Joy",
                EmotionType.Sad => "Sadness",
                EmotionType.Contempt => "Disdain",
                EmotionType.Determined => "Stern",
                EmotionType.Worried => "Concern",
                _ => null
            };
            if (layeredFamily != null && FaceFXEmotionCatalog.SupportsLayeredEmotions(_options.Species, _options.Game))
            {
                return new FaceFXEmotionChoice
                {
                    DisplayName = $"Layered: {layeredFamily}",
                    LayeredFamily = layeredFamily
                };
            }

            if (_options.Emotion == EmotionType.Surprise)
            {
                return new FaceFXEmotionChoice
                {
                    DisplayName = "Preset: Neutral: Shock",
                    PresetAnimation = "E_Neutral_Shock"
                };
            }

            return FaceFXEmotionCatalog.GetForSpecies(_options.Species, _options.Game).FirstOrDefault();
        }

        private void GenerateEmotionAnimation(FaceFXEmotionChoice emotion)
        {
            float duration = Math.Max(_audioDuration, 0.02f);
            float intensity = Math.Clamp(_options.EmotionIntensity, 0f, 1f);
            List<FaceFXSpeechSegment> segments = AnalyzeAudioForSpeechSegments(duration);
            float speechStart = Math.Clamp(segments.FirstOrDefault()?.StartTime ?? 0f, 0f, duration);
            float speechEnd = Math.Clamp(segments.LastOrDefault()?.EndTime ?? duration, speechStart, duration);
            float preRoll = Math.Max(-0.272f, speechStart - 0.272f);

            if (!emotion.IsLayered)
            {
                // High-level full-face presets in shipped lines are generally sparse,
                // constant curves spanning the line (often beginning in preroll).
                float presetWeight = 0.8f * intensity;
                AddAnimation(emotion.PresetAnimation, new List<FaceFXControlPoint>
                {
                    new() { time = preRoll, weight = presetWeight, inTangent = 0f, leaveTangent = 0f },
                    new() { time = duration, weight = presetWeight, inTangent = 0f, leaveTangent = 0f }
                });
                return;
            }

            foreach ((string layer, float layerWeight) in new[]
                     {
                         ("WB", 0.469f), ("S", 1.0f), ("B", 0.349f), ("Y", 0.349f)
                     })
            {
                // UDK/BioWare output selects one variant for each face layer. It does
                // not stack variants 1 and 2 of every layer as the old generator did.
                int variant = 1 + StableVariant(_line.NameAsString, emotion.LayeredFamily, layer) % 3;
                string animationName = $"E_{layer}_{emotion.LayeredFamily}{variant}";
                float peak = layerWeight * intensity;
                float middle = speechStart + (speechEnd - speechStart) * 0.55f;
                float release = Math.Max(middle, speechEnd - 0.12f);
                var points = new List<FaceFXControlPoint>
                {
                    new() { time = preRoll, weight = 0f, inTangent = 0f, leaveTangent = 0f },
                    new() { time = speechStart, weight = peak * 0.85f, inTangent = 0f, leaveTangent = 0f },
                    new() { time = middle, weight = peak, inTangent = 0f, leaveTangent = 0f },
                    new() { time = release, weight = peak * 0.92f, inTangent = 0f, leaveTangent = 0f },
                    new() { time = duration, weight = 0f, inTangent = 0f, leaveTangent = 0f }
                };

                // Very short lines can collapse envelope landmarks onto one time.
                points = points.GroupBy(point => point.time)
                    .Select(group => group.OrderByDescending(point => point.weight).First())
                    .OrderBy(point => point.time)
                    .ToList();
                AddAnimation(animationName, points);
            }
        }

        private static int StableVariant(string lineName, string family, string layer)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char character in $"{lineName}|{family}|{layer}")
                {
                    hash ^= character;
                    hash *= 16777619;
                }
                return (int)(hash & 0x7fffffff);
            }
        }

        [Obsolete("Use the audited FaceFXEmotionChoice overload.")]
        private void GenerateEmotionAnimation()
        {
            float duration = Math.Max(_audioDuration, 0.02f);
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
                    // Anger - strong weights for game visibility
                    mappings.Add(new EmotionAnimationMapping("E_S_Anger1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_S_Anger2", 1.0f));
                    mappings.Add(new EmotionAnimationMapping("E_B_Anger1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_B_Anger2", 1.0f));
                    mappings.Add(new EmotionAnimationMapping("E_Y_Anger1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_Y_Anger2", 1.0f));
                    break;

                case EmotionType.Disgust:
                    // Disgust - strong weights for game visibility
                    mappings.Add(new EmotionAnimationMapping("E_S_Disgust1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_S_Disgust2", 1.0f));
                    mappings.Add(new EmotionAnimationMapping("E_B_Disgust1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_B_Disgust2", 1.0f));
                    mappings.Add(new EmotionAnimationMapping("E_Y_Disgust1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_Y_Disgust2", 1.0f));
                    break;

                case EmotionType.Fear:
                    // Fear - strong weights for game visibility
                    mappings.Add(new EmotionAnimationMapping("E_S_Fear1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_S_Fear2", 1.0f));
                    mappings.Add(new EmotionAnimationMapping("E_B_Fear1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_B_Fear2", 1.0f));
                    mappings.Add(new EmotionAnimationMapping("E_Y_Fear1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_Y_Fear2", 1.0f));
                    break;

                case EmotionType.Happy:
                    // Happy/Joy - strong weights for game visibility
                    mappings.Add(new EmotionAnimationMapping("E_S_Joy1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_S_Joy2", 1.0f));
                    mappings.Add(new EmotionAnimationMapping("E_B_Joy1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_B_Joy2", 1.0f));
                    mappings.Add(new EmotionAnimationMapping("E_Y_Joy1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_Y_Joy2", 1.0f));
                    break;

                case EmotionType.Sad:
                    // Sad - strong weights for game visibility
                    mappings.Add(new EmotionAnimationMapping("E_S_Sadness1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_S_Sadness2", 1.0f));
                    mappings.Add(new EmotionAnimationMapping("E_B_Sadness1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_B_Sadness2", 1.0f));
                    mappings.Add(new EmotionAnimationMapping("E_Y_Sadness1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_Y_Sadness2", 1.0f));
                    break;

                case EmotionType.Surprise:
                    // Surprise/Shock - strong weights for game visibility
                    mappings.Add(new EmotionAnimationMapping("E_S_Shock1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_B_Shock1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_Y_Shock1", 1.2f));
                    break;

                case EmotionType.Contempt:
                    // Contempt/Disdain - strong weights for game visibility
                    mappings.Add(new EmotionAnimationMapping("E_S_Disdain1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_S_Disdain2", 1.0f));
                    mappings.Add(new EmotionAnimationMapping("E_B_Disdain1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_B_Disdain2", 1.0f));
                    mappings.Add(new EmotionAnimationMapping("E_Y_Disdain1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_Y_Disdain2", 1.0f));
                    break;

                case EmotionType.Determined:
                    // Determined/Stern - strong weights for game visibility
                    mappings.Add(new EmotionAnimationMapping("E_S_Stern1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_S_Stern2", 1.0f));
                    mappings.Add(new EmotionAnimationMapping("E_B_Stern1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_B_Stern2", 1.0f));
                    mappings.Add(new EmotionAnimationMapping("E_Y_Stern1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_Y_Stern2", 1.0f));
                    break;

                case EmotionType.Worried:
                    // Worried/Concern - strong weights for game visibility
                    mappings.Add(new EmotionAnimationMapping("E_S_Concern1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_S_Concern2", 1.0f));
                    mappings.Add(new EmotionAnimationMapping("E_B_Concern1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_B_Concern2", 1.0f));
                    mappings.Add(new EmotionAnimationMapping("E_Y_Concern1", 1.2f));
                    mappings.Add(new EmotionAnimationMapping("E_Y_Concern2", 1.0f));
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
            name = PhonemeToVisemeMap.CanonicalizeVisemeName(name, _options.Species, _options.Game);
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
                if (nameIdx >= 0 && nameIdx < _faceFX.Names.Count &&
                    string.Equals(_faceFX.Names[nameIdx], name, StringComparison.OrdinalIgnoreCase))
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
                int nameIndex = _faceFX.Names.FindIndex(existingName =>
                    string.Equals(existingName, name, StringComparison.OrdinalIgnoreCase));
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
