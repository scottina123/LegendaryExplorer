using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Wave;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorer.UnrealExtensions.Classes;

namespace LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator
{
    /// <summary>
    /// Analyzes audio to extract timing and amplitude information for lip sync generation
    /// </summary>
    public static class AudioAnalyzer
    {
        /// <summary>
        /// Gets the duration of audio from a WwiseStream or SoundNodeWave export.
        /// Prefers reading the WEM header directly (sample count / sample rate) which
        /// is more accurate than converting to WAV via vgmstream.
        /// </summary>
        /// <param name="audioExport">The audio export entry</param>
        /// <returns>Duration in seconds, or 0 if unable to determine</returns>
        public static float GetAudioDuration(ExportEntry audioExport)
        {
            if (audioExport == null)
                return 0f;

            // Preferred path: read duration straight from the WEM header.
            // This avoids the vgmstream conversion pipeline which can add/trim
            // samples and introduce small timing errors.
            try
            {
                if (audioExport.ClassName == "WwiseStream")
                {
                    var stream = audioExport.GetBinaryData<WwiseStream>();
                    var audioInfo = stream?.GetAudioInfo();
                    if (audioInfo != null)
                    {
                        var length = audioInfo.GetLength();
                        if (length.TotalSeconds > 0)
                        {
                            return (float)length.TotalSeconds;
                        }
                    }
                }
            }
            catch
            {
                // Fall through to WAV-based measurement
            }

            // Fallback: decode to WAV and measure
            try
            {
                byte[] audioData = GetAudioAsWav(audioExport);
                if (audioData == null || audioData.Length == 0)
                    return 0f;

                using var ms = new MemoryStream(audioData);
                using var reader = new WaveFileReader(ms);
                return (float)reader.TotalTime.TotalSeconds;
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        /// Analyzes audio amplitude over time to determine emphasis points
        /// </summary>
        /// <param name="audioExport">The audio export entry</param>
        /// <returns>List of amplitude data points</returns>
        public static List<AmplitudeData> AnalyzeAmplitude(ExportEntry audioExport)
        {
            var result = new List<AmplitudeData>();

            if (audioExport == null)
                return result;

            try
            {
                byte[] audioData = GetAudioAsWav(audioExport);
                if (audioData == null || audioData.Length == 0)
                    return result;
                return AnalyzeWavAmplitude(audioData);
            }
            catch
            {
                // Return empty list on error
            }

            return result;
        }

        internal static List<AmplitudeData> AnalyzeWavAmplitude(byte[] audioData)
        {
            var result = new List<AmplitudeData>();
            using var ms = new MemoryStream(audioData);
            using var reader = new WaveFileReader(ms);

            // Use fixed 20 ms windows. NAudio's sample provider is interleaved, so the
            // channel count must be included or stereo audio is placed on a 2x-slow timeline.
            const float sampleInterval = 0.02f;
            int channelCount = Math.Max(1, reader.WaveFormat.Channels);
            int framesPerWindow = Math.Max(1, (int)Math.Round(reader.WaveFormat.SampleRate * sampleInterval));
            int samplesPerWindow = framesPerWindow * channelCount;
            float[] buffer = new float[samplesPerWindow];
            var sampleProvider = reader.ToSampleProvider();

            long framesProcessed = 0;
            while (true)
            {
                int samplesRead = sampleProvider.Read(buffer, 0, samplesPerWindow);
                if (samplesRead == 0)
                    break;

                int completeSamples = samplesRead - samplesRead % channelCount;
                if (completeSamples == 0)
                    break;

                double sum = 0;
                for (int i = 0; i < completeSamples; i++)
                    sum += buffer[i] * buffer[i];
                float rms = (float)Math.Sqrt(sum / completeSamples);
                int framesRead = completeSamples / channelCount;

                result.Add(new AmplitudeData
                {
                    // Derive every timestamp from the exact decoded frame count. This
                    // avoids accumulated float additions on long dialogue lines.
                    Time = (float)((double)framesProcessed / reader.WaveFormat.SampleRate),
                    Amplitude = rms
                });
                framesProcessed += framesRead;
            }

            NormalizeAmplitudesLocally(result, sampleInterval);
            return result;
        }

        /// <summary>
        /// Normalizes an envelope against a fixed-duration neighborhood instead of the
        /// loudest point in the whole file. A shout elsewhere in a long line therefore
        /// cannot flatten otherwise valid quiet speech.
        /// </summary>
        internal static void NormalizeAmplitudesLocally(IList<AmplitudeData> data, float sampleInterval = 0.02f,
            float neighborhoodSeconds = 2.0f)
        {
            if (data == null || data.Count == 0)
                return;

            int halfWindow = Math.Max(1, (int)Math.Round(neighborhoodSeconds / sampleInterval / 2f));
            var localValues = new List<float>(halfWindow * 2 + 1);

            for (int i = 0; i < data.Count; i++)
            {
                localValues.Clear();
                int start = Math.Max(0, i - halfWindow);
                int end = Math.Min(data.Count - 1, i + halfWindow);
                for (int j = start; j <= end; j++)
                    localValues.Add(Math.Max(0f, data[j].Amplitude));

                localValues.Sort();
                float noiseFloor = Percentile(localValues, 0.15f);
                float localCeiling = Percentile(localValues, 0.90f);
                float baseline = noiseFloor * 0.8f;
                float range = Math.Max(localCeiling - baseline, 0.00001f);
                data[i].NormalizedAmplitude = Math.Clamp((data[i].Amplitude - baseline) / range, 0f, 1f);
            }
        }

        private static float Percentile(IReadOnlyList<float> sortedValues, float percentile)
        {
            if (sortedValues.Count == 0)
                return 0f;

            float position = Math.Clamp(percentile, 0f, 1f) * (sortedValues.Count - 1);
            int lower = (int)position;
            int upper = Math.Min(lower + 1, sortedValues.Count - 1);
            float fraction = position - lower;
            return sortedValues[lower] + (sortedValues[upper] - sortedValues[lower]) * fraction;
        }

        /// <summary>
        /// Detects voice activity (when someone is speaking vs silence)
        /// </summary>
        /// <param name="amplitudeData">Amplitude data from AnalyzeAmplitude</param>
        /// <param name="threshold">Amplitude threshold for voice detection (0-1)</param>
        /// <returns>List of voice activity segments</returns>
        public static List<VoiceSegment> DetectVoiceActivity(List<AmplitudeData> amplitudeData, float threshold = 0.1f)
        {
            var segments = new List<VoiceSegment>();
            
            if (amplitudeData.Count == 0)
                return segments;

            bool inVoice = false;
            float segmentStart = 0f;
            
            foreach (var data in amplitudeData)
            {
                if (data.NormalizedAmplitude > threshold && !inVoice)
                {
                    inVoice = true;
                    segmentStart = data.Time;
                }
                else if (data.NormalizedAmplitude <= threshold && inVoice)
                {
                    inVoice = false;
                    segments.Add(new VoiceSegment
                    {
                        StartTime = segmentStart,
                        EndTime = data.Time
                    });
                }
            }
            
            // Handle case where voice continues to the end
            if (inVoice && amplitudeData.Count > 0)
            {
                segments.Add(new VoiceSegment
                {
                    StartTime = segmentStart,
                    EndTime = amplitudeData.Last().Time
                });
            }

            return segments;
        }

        /// <summary>
        /// Gets audio data as WAV bytes
        /// </summary>
        private static byte[] GetAudioAsWav(ExportEntry audioExport)
        {
            try
            {
                if (audioExport.ClassName == "WwiseStream")
                {
                    // For WwiseStream, get the audio data and convert to WAV
                    return WwiseStreamToWav(audioExport);
                }
                else if (audioExport.ClassName == "SoundNodeWave")
                {
                    // For SoundNodeWave (ME1/LE1), extract and convert
                    return SoundNodeWaveToWav(audioExport);
                }
            }
            catch
            {
                // Return null on any error
            }
            return null;
        }

        private static byte[] WwiseStreamToWav(ExportEntry wwiseStreamExport)
        {
            try
            {
                var stream = wwiseStreamExport.GetBinaryData<WwiseStream>();
                if (stream != null)
                {
                    // Use the extension method to create a wave stream
                    var waveStream = stream.CreateWaveStream();
                    if (waveStream != null)
                    {
                        return waveStream.ToArray();
                    }
                }
            }
            catch
            {
                // Return null on conversion error
            }
            return null;
        }

        private static byte[] SoundNodeWaveToWav(ExportEntry soundNodeWave)
        {
            try
            {
                // Get raw audio data from ISB or internal storage
                var props = soundNodeWave.GetProperties();
                var rawData = props.GetProp<LegendaryExplorerCore.Unreal.ImmutableByteArrayProperty>("RawData");
                if (rawData != null && rawData.Bytes.Length > 0)
                {
                    // The raw data should already be PCM or can be converted
                    // This is a simplified implementation
                    return rawData.Bytes;
                }
            }
            catch
            {
                // Return null on error
            }
            return null;
        }
    }

    /// <summary>
    /// Represents amplitude data at a point in time
    /// </summary>
    public class AmplitudeData
    {
        public float Time { get; set; }
        public float Amplitude { get; set; }
        public float NormalizedAmplitude { get; set; }
    }

    /// <summary>
    /// Represents a segment where voice is detected
    /// </summary>
    public class VoiceSegment
    {
        public float StartTime { get; set; }
        public float EndTime { get; set; }
        public float Duration => EndTime - StartTime;
    }
}
