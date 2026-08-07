using System;
using System.IO;
using NAudio.Wave;

namespace LegendaryExplorer.Audio
{
    /// <summary>
    /// Normalizes user-supplied audio into the 16-bit PCM WAV input expected by LEX's Wwise workflows.
    /// </summary>
    public static class AudioInputConverter
    {
        public const string OpenFileDialogFilter =
            "Audio files (*.wav;*.mp3)|*.wav;*.mp3|Wave PCM (*.wav)|*.wav|MP3 audio (*.mp3)|*.mp3";

        public static bool IsSupportedAudioFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            var extension = Path.GetExtension(filePath);
            return extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase);
        }

        public static void ConvertToPcmWave(string sourcePath, string destinationPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("The audio input file could not be found.", sourcePath);
            }

            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            var extension = Path.GetExtension(sourcePath);
            if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                if (!Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourcePath, destinationPath, true);
                }
                return;
            }

            if (!extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException($"Unsupported audio input format '{extension}'. Only WAV and MP3 files are supported.");
            }

            using var mp3Reader = new Mp3FileReader(sourcePath);
            if (mp3Reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm || mp3Reader.WaveFormat.BitsPerSample != 16)
            {
                throw new InvalidDataException(
                    $"The MP3 decoder produced unsupported audio format '{mp3Reader.WaveFormat}'. Expected 16-bit PCM.");
            }

            WaveFileWriter.CreateWaveFile(destinationPath, mp3Reader);
        }

        public static float GetDurationSeconds(string filePath)
        {
            if (Path.GetExtension(filePath).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                using var mp3Reader = new Mp3FileReader(filePath);
                return (float)mp3Reader.TotalTime.TotalSeconds;
            }

            using var waveReader = new WaveFileReader(filePath);
            return (float)waveReader.TotalTime.TotalSeconds;
        }
    }
}
