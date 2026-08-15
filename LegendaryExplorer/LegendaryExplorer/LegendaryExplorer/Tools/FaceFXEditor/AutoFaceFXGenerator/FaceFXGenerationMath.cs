using System;
using System.Collections.Generic;
using System.Linq;

namespace LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator
{
    internal sealed class FaceFXSpeechSegment
    {
        public float StartTime { get; init; }
        public float EndTime { get; init; }
        public float PeakAmplitude { get; init; }
        public float AverageAmplitude { get; init; }
    }

    internal sealed class FaceFXTimedPhoneme
    {
        public string Phoneme { get; init; }
        public float StartTime { get; init; }
        public float Duration { get; init; }
        public float Intensity { get; init; }
    }

    /// <summary>
    /// Duration-independent, locally bounded operations used by FaceFX generation.
    /// Keeping these operations pure also makes long-line behavior directly testable.
    /// </summary>
    internal static class FaceFXGenerationMath
    {
        internal const float CoarticulationLeadSeconds = 0.045f;
        internal const float CoarticulationTrailSeconds = 0.055f;

        internal static List<FaceFXTimedPhoneme> MapPhonemesToSpeech(
            IReadOnlyList<PhonemeData> phonemes,
            IReadOnlyList<FaceFXSpeechSegment> segments,
            float duration,
            Func<float, float> amplitudeAtTime)
        {
            var result = new List<FaceFXTimedPhoneme>();
            if (phonemes == null || phonemes.Count == 0)
                return result;

            var usableSegments = (segments ?? Array.Empty<FaceFXSpeechSegment>())
                .Where(s => s.EndTime > s.StartTime)
                .OrderBy(s => s.StartTime)
                .ToList();
            if (usableSegments.Count == 0)
            {
                usableSegments.Add(new FaceFXSpeechSegment
                {
                    StartTime = 0f,
                    EndTime = Math.Max(0.02f, duration),
                    PeakAmplitude = 1f,
                    AverageAmplitude = 1f
                });
            }

            float totalSpeechDuration = usableSegments.Sum(s => s.EndTime - s.StartTime);
            int phonemeIndex = 0;
            float cumulativeSpeechDuration = 0f;

            for (int segmentIndex = 0; segmentIndex < usableSegments.Count && phonemeIndex < phonemes.Count; segmentIndex++)
            {
                FaceFXSpeechSegment segment = usableSegments[segmentIndex];
                float segmentDuration = segment.EndTime - segment.StartTime;
                cumulativeSpeechDuration += segmentDuration;

                // Cumulative rounding prevents the per-segment rounding drift and
                // last-segment pile-up that the previous implementation exhibited.
                int targetEndIndex = segmentIndex == usableSegments.Count - 1
                    ? phonemes.Count
                    : (int)Math.Round(phonemes.Count * cumulativeSpeechDuration / totalSpeechDuration,
                        MidpointRounding.AwayFromZero);
                targetEndIndex = Math.Clamp(targetEndIndex, phonemeIndex, phonemes.Count);
                int count = targetEndIndex - phonemeIndex;
                if (count == 0)
                    continue;

                float totalWeight = 0f;
                for (int i = phonemeIndex; i < targetEndIndex; i++)
                    totalWeight += Math.Max(0.001f, phonemes[i].Duration);

                float elapsedWeight = 0f;
                for (int i = phonemeIndex; i < targetEndIndex; i++)
                {
                    PhonemeData phoneme = phonemes[i];
                    float weight = Math.Max(0.001f, phoneme.Duration);
                    float start = segment.StartTime + segmentDuration * elapsedWeight / totalWeight;
                    elapsedWeight += weight;
                    float end = segment.StartTime + segmentDuration * elapsedWeight / totalWeight;
                    float center = (start + end) * 0.5f;
                    float amplitude = Math.Clamp(amplitudeAtTime?.Invoke(center) ?? 1f, 0f, 1f);

                    result.Add(new FaceFXTimedPhoneme
                    {
                        Phoneme = phoneme.Phoneme,
                        StartTime = start,
                        Duration = Math.Max(0.001f, end - start),
                        // Keep quiet, locally-normalized speech articulated without
                        // erasing useful dynamics between words.
                        Intensity = 0.55f + 0.45f * amplitude
                    });
                }

                phonemeIndex = targetEndIndex;
            }

            return result;
        }

        internal static void AddLocalVisemeEnvelope(float[] samples, float sampleRate,
            FaceFXTimedPhoneme phoneme, float peakWeight)
        {
            if (samples == null || samples.Length == 0 || sampleRate <= 0f || peakWeight <= 0f)
                return;

            float curveEnd = (samples.Length - 1) / sampleRate;
            float attackStart = Math.Max(0f, phoneme.StartTime - CoarticulationLeadSeconds);
            float peakTime = phoneme.StartTime + phoneme.Duration * 0.45f;
            float releaseStart = phoneme.StartTime + phoneme.Duration * 0.70f;
            float releaseEnd = Math.Min(curveEnd,
                phoneme.StartTime + phoneme.Duration + CoarticulationTrailSeconds);

            int firstSample = Math.Max(0, (int)Math.Floor(attackStart * sampleRate));
            int lastSample = Math.Min(samples.Length - 1, (int)Math.Ceiling(releaseEnd * sampleRate));
            for (int i = firstSample; i <= lastSample; i++)
            {
                float time = i / sampleRate;
                float value;
                if (time <= peakTime)
                {
                    float amount = (time - attackStart) / Math.Max(0.001f, peakTime - attackStart);
                    value = peakWeight * SmoothStep(amount);
                }
                else if (time < releaseStart)
                {
                    value = peakWeight;
                }
                else
                {
                    float amount = (time - releaseStart) / Math.Max(0.001f, releaseEnd - releaseStart);
                    value = peakWeight * (1f - SmoothStep(amount));
                }

                // FaceFX-style competing phoneme targets do not accumulate across a
                // line. Only the strongest local influence wins at a sample.
                samples[i] = Math.Max(samples[i], value);
            }
        }

        internal static void SmoothLocally(float[] samples)
        {
            if (samples == null || samples.Length < 3)
                return;

            var source = (float[])samples.Clone();
            for (int i = 1; i < samples.Length - 1; i++)
                samples[i] = (source[i - 1] + source[i] * 2f + source[i + 1]) * 0.25f;
        }

        internal static List<int> SelectKeyframeIndices(float[] samples, float sampleRate)
        {
            var result = new List<int>();
            if (samples == null || samples.Length == 0)
                return result;

            result.Add(0);
            if (samples.Length == 1)
                return result;

            const float tolerance = 0.015f;
            const float maximumActiveGapSeconds = 0.25f;
            int maximumActiveGap = Math.Max(1, (int)Math.Round(maximumActiveGapSeconds * sampleRate));
            int lastKept = 0;

            for (int i = 1; i < samples.Length - 1; i++)
            {
                float previous = samples[i - 1];
                float current = samples[i];
                float next = samples[i + 1];
                bool localExtremum = (current > previous && current >= next) ||
                                     (current < previous && current <= next);
                bool transition = (samples[lastKept] <= 0.005f && current > 0.01f) ||
                                  (samples[lastKept] > 0.01f && current <= 0.005f);
                bool changed = Math.Abs(current - samples[lastKept]) >= tolerance;
                bool activeGap = current > 0.005f && i - lastKept >= maximumActiveGap;

                if (localExtremum || transition || changed || activeGap)
                {
                    result.Add(i);
                    lastKept = i;
                }
            }

            result.Add(samples.Length - 1);
            return result;
        }

        private static float SmoothStep(float value)
        {
            value = Math.Clamp(value, 0f, 1f);
            return value * value * (3f - 2f * value);
        }
    }
}
