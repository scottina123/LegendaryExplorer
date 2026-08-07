using System;
using System.IO;
using LegendaryExplorer.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NAudio.Wave;

namespace LegendaryExplorer.Tests.Audio;

[TestClass]
public class AudioInputConverterTests
{
    [TestMethod]
    public void SupportedAudioFiles_AreRecognizedCaseInsensitively()
    {
        Assert.IsTrue(AudioInputConverter.IsSupportedAudioFile("voice.wav"));
        Assert.IsTrue(AudioInputConverter.IsSupportedAudioFile("voice.MP3"));
        Assert.IsFalse(AudioInputConverter.IsSupportedAudioFile("voice.ogg"));
        Assert.IsFalse(AudioInputConverter.IsSupportedAudioFile(null));
    }

    [TestMethod]
    public void ConvertToPcmWave_DecodesMp3ToSixteenBitPcmWave()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"LEX_AudioInputConverter_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);

        try
        {
            var mp3Path = Path.Combine(testDirectory, "input.mp3");
            var wavePath = Path.Combine(testDirectory, "output.wav");
            var sourceFormat = new WaveFormat(44100, 16, 1);
            var silence = new byte[sourceFormat.AverageBytesPerSecond];

            using (var source = new RawSourceWaveStream(new MemoryStream(silence), sourceFormat))
            {
                MediaFoundationEncoder.EncodeToMp3(source, mp3Path, 128000);
            }

            AudioInputConverter.ConvertToPcmWave(mp3Path, wavePath);

            using var result = new WaveFileReader(wavePath);
            Assert.AreEqual(WaveFormatEncoding.Pcm, result.WaveFormat.Encoding);
            Assert.AreEqual(16, result.WaveFormat.BitsPerSample);
            Assert.AreEqual(sourceFormat.SampleRate, result.WaveFormat.SampleRate);
            Assert.AreEqual(sourceFormat.Channels, result.WaveFormat.Channels);
            Assert.AreEqual(1, result.TotalTime.TotalSeconds, 0.1);
            Assert.AreEqual(1, AudioInputConverter.GetDurationSeconds(mp3Path), 0.1);
        }
        finally
        {
            Directory.Delete(testDirectory, true);
        }
    }
}
