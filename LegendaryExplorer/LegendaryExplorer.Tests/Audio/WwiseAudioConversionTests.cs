using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LegendaryExplorer.Audio;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.UnrealExtensions;
using LegendaryExplorerCore.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NAudio.Wave;

namespace LegendaryExplorer.Tests.Audio;

[TestClass]
public class WwiseAudioConversionTests
{
    [TestMethod]
    [DataRow(MEGame.ME3, 1, 44100, 24000, 0.24)]
    [DataRow(MEGame.ME3, 2, 48000, 32000, 0.24)]
    [DataRow(MEGame.LE2, 1, 32000, 44100, 0.24)]
    [DataRow(MEGame.LE2, 2, 44100, 24000, 0.24)]
    [DataRow(MEGame.LE3, 1, 24000, 32000, 0.24)]
    [DataRow(MEGame.LE3, 2, 32000, 44100, 0.24)]
    [DataRow(MEGame.ME3, 1, 24000, 24000, 0.004)]
    [DataRow(MEGame.LE3, 2, 44100, 44100, 0.004)]
    [DataRow(MEGame.ME3, 2, 24000, 44100, 3.0)]
    [DataRow(MEGame.LE2, 1, 48000, 48000, 2.3)]
    public async Task ConvertsWaveToPlayableGameAudioWithoutAnAuthoringInstallation(
        MEGame game, int channels, int inputRate, int outputRate, double seconds)
    {
        using var files = new TemporaryAudioDirectory();
        string input = files.File("voice.wav");
        WriteTone(input, inputRate, channels, seconds);

        string output = await WwiseAudioConversion.ConvertAsync(game, input, Settings(game, outputRate));

        Assert.IsTrue(System.IO.File.Exists(input), "Converting audio must preserve the source file.");
        Assert.AreEqual(game == MEGame.ME3 ? ".ogg" : ".wem", Path.GetExtension(output));
        AssertPlayableOutput(output, game, channels, outputRate, seconds);
    }

    [TestMethod]
    [DataRow(MEGame.ME3, 1)]
    [DataRow(MEGame.LE2, 2)]
    [DataRow(MEGame.LE3, 1)]
    public async Task ConvertsMp3WithAudibleSignalAndPreservesDecodedDuration(MEGame game, int channels)
    {
        using var files = new TemporaryAudioDirectory();
        string wave = files.File("source.wav");
        string mp3 = files.File("voice.mp3");
        WriteTone(wave, 44100, channels, 0.30);
        using (var source = new WaveFileReader(wave))
        {
            MediaFoundationEncoder.EncodeToMp3(source, mp3, 128000);
        }

        string normalized = files.File("decoded-input.wav");
        AudioInputConverter.ConvertToPcmWave(mp3, normalized);
        double expectedSeconds;
        using (var source = new WaveFileReader(normalized))
        {
            expectedSeconds = source.TotalTime.TotalSeconds;
        }

        string output = await WwiseAudioConversion.ConvertAsync(game, mp3, Settings(game, 24000));

        AssertPlayableOutput(output, game, channels, 24000, expectedSeconds);
    }

    [TestMethod]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void EncoderUsesDefaultRateAndProducesDecodableStereo(MEGame game)
    {
        using var files = new TemporaryAudioDirectory();
        string input = files.File("music.wav");
        string output = files.File(game == MEGame.ME3 ? "encoded.ogg" : "encoded.wem");
        WriteTone(input, 44100, 2, 0.20);

        WwiseAudioEncoder.ConvertAudioFile(game, input, output);

        AssertPlayableOutput(output, game, 2, 24000, 0.20);
    }

    [TestMethod]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void EncoderEndsOddSizedAudioDataAtEndOfFile(MEGame game)
    {
        using var files = new TemporaryAudioDirectory();
        string input = files.File("music.wav");
        string output = files.File("encoded.wem");
        // Vary the signal length to cover an odd packet total without tying the
        // fixture to the precise bytes produced by a particular encoder build.
        for (int attempt = 0; attempt < 16; attempt++)
        {
            WriteTone(input, 44100, 2, 0.20 + attempt * 0.001);
            WwiseAudioEncoder.ConvertAudioFile(game, input, output, 44100);
            byte[] audio = System.IO.File.ReadAllBytes(output);
            int dataSize = AssertDataEndsAtEndOfFile(audio);
            if ((dataSize & 1) != 0) return;
        }
        Assert.Fail("The regression fixture must exercise an odd-sized audio data chunk.");
    }

    [TestMethod]
    public async Task FolderConversionRejectsDuplicateOutputBasenamesBeforeOverwritingAudio()
    {
        using var files = new TemporaryAudioDirectory();
        string first = files.File("voice.wav");
        string second = files.File("voice.mp3");
        WriteTone(first, 44100, 1, 0.20);
        using (var source = new WaveFileReader(first))
        {
            MediaFoundationEncoder.EncodeToMp3(source, second, 128000);
        }
        byte[] originalWave = System.IO.File.ReadAllBytes(first);
        byte[] originalMp3 = System.IO.File.ReadAllBytes(second);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            WwiseAudioConversion.ConvertAsync(MEGame.LE3, files.Path, Settings(MEGame.LE3, 24000)));

        CollectionAssert.AreEqual(originalWave, System.IO.File.ReadAllBytes(first));
        CollectionAssert.AreEqual(originalMp3, System.IO.File.ReadAllBytes(second));
    }

    [TestMethod]
    public async Task ConcurrentFolderConversionsKeepTheirInputsAndOutputsIndependent()
    {
        using var files = new TemporaryAudioDirectory();
        string firstDirectory = Directory.CreateDirectory(files.File("first")).FullName;
        string secondDirectory = Directory.CreateDirectory(files.File("second")).FullName;
        WriteTone(Path.Combine(firstDirectory, "voice.wav"), 44100, 1, 0.12);
        WriteTone(Path.Combine(secondDirectory, "voice.wav"), 32000, 2, 0.31);

        string[] outputs = await Task.WhenAll(
            WwiseAudioConversion.ConvertAsync(MEGame.ME3, firstDirectory, Settings(MEGame.ME3, 24000)),
            WwiseAudioConversion.ConvertAsync(MEGame.LE3, secondDirectory, Settings(MEGame.LE3, 32000)));

        Assert.AreNotEqual(outputs[0], outputs[1]);
        string firstOutput = Directory.GetFiles(outputs[0], "*.ogg").Single();
        string secondOutput = Directory.GetFiles(outputs[1], "*.wem").Single();
        AssertPlayableOutput(firstOutput, MEGame.ME3, 1, 24000, 0.12);
        AssertPlayableOutput(secondOutput, MEGame.LE3, 2, 32000, 0.31);
        Assert.IsTrue(System.IO.File.Exists(Path.Combine(firstDirectory, "voice.wav")));
        Assert.IsTrue(System.IO.File.Exists(Path.Combine(secondDirectory, "voice.wav")));
    }

    private static WwiseConversionSettingsPackage Settings(MEGame game, int sampleRate) => new()
    {
        TargetGame = game,
        TargetSamplerate = sampleRate
    };

    private static void AssertPlayableOutput(string output, MEGame game, int channels, int sampleRate, double seconds)
    {
        byte[] audio = System.IO.File.ReadAllBytes(output);
        Assert.AreEqual("RIFF", Encoding.ASCII.GetString(audio, 0, 4));
        Assert.AreEqual("WAVE", Encoding.ASCII.GetString(audio, 8, 4));
        Assert.AreEqual("fmt ", Encoding.ASCII.GetString(audio, 12, 4));
        Assert.AreEqual(game == MEGame.ME3 ? 24 : 66, BitConverter.ToInt32(audio, 16));
        AssertDataEndsAtEndOfFile(audio);
        if (game == MEGame.ME3)
        {
            Assert.AreEqual("vorb", Encoding.ASCII.GetString(audio, 44, 4));
            Assert.AreEqual(42, BitConverter.ToInt32(audio, 48));
            using var source = new MemoryStream(audio, writable: false);
            using var gameAudio = AudioStreamHelper.ConvertWwiseOggToME3Ogg(source);
            audio = gameAudio.ToArray();
            Assert.AreEqual(66, BitConverter.ToInt32(audio, 16));
            AssertDataEndsAtEndOfFile(audio);
        }

        // The playback helper deletes its input, so decode a private copy of the game-ready audio.
        string decodeInput = output + $".{Guid.NewGuid():N}.wem";
        System.IO.File.WriteAllBytes(decodeInput, audio);
        using var decoded = AudioStreamHelper.ConvertRIFFToWaveVGMStream(decodeInput);
        decoded.Position = 0;
        using var reader = new WaveFileReader(decoded);
        Assert.AreEqual(channels, reader.WaveFormat.Channels);
        Assert.AreEqual(sampleRate, reader.WaveFormat.SampleRate);
        Assert.AreEqual(seconds, reader.TotalTime.TotalSeconds, Math.Max(0.001, 2d / sampleRate));

        var provider = reader.ToSampleProvider();
        var samples = new float[checked((int)(reader.SampleCount * channels))];
        int count = provider.Read(samples, 0, samples.Length);
        Assert.IsTrue(count > 0, "The decoder must produce audio samples.");
        double energy = samples.Take(count).Sum(sample => (double)sample * sample) / count;
        Assert.IsTrue(energy > 0.005, $"The decoded signal was silent or too quiet (mean square {energy}).");
        if (channels == 2)
        {
            double difference = Enumerable.Range(0, count / 2)
                .Average(frame => Math.Abs(samples[frame * 2] - samples[frame * 2 + 1]));
            Assert.IsTrue(difference > 0.01, "Stereo conversion must preserve the distinct channel signals.");
        }
        Assert.IsTrue(System.IO.File.Exists(output), "Playback validation must preserve the converted output.");
    }

    private static int AssertDataEndsAtEndOfFile(byte[] audio)
    {
        Assert.AreEqual(audio.Length - 8, BitConverter.ToInt32(audio, 4), "The RIFF size must describe the complete media.");
        int offset = 12;
        while (offset + 8 <= audio.Length)
        {
            string tag = Encoding.ASCII.GetString(audio, offset, 4);
            int size = BitConverter.ToInt32(audio, offset + 4);
            Assert.IsTrue(size >= 0 && size <= audio.Length - offset - 8, "The media chunk must fit in its RIFF container.");
            if (tag == "data")
            {
                Assert.AreEqual(audio.Length, offset + 8 + size,
                    "The Wwise runtime rejects trailing bytes after the final data chunk, including an alignment pad.");
                return size;
            }
            offset += 8 + size + (size & 1);
        }
        Assert.Fail("The media must contain an audio data chunk.");
        return 0;
    }

    private static void WriteTone(string path, int sampleRate, int channels, double seconds)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, channels));
        int frames = (int)Math.Round(sampleRate * seconds);
        for (int frame = 0; frame < frames; frame++)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                writer.WriteSample((float)(0.5 * Math.Sin(2 * Math.PI * (440 + channel * 220) * frame / sampleRate)));
            }
        }
    }

    private sealed class TemporaryAudioDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"LEX_WwiseConversion_{Guid.NewGuid():N}");

        public TemporaryAudioDirectory() => Directory.CreateDirectory(Path);

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
