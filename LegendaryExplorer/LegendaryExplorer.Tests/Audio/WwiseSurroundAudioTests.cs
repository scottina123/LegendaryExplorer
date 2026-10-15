using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using LegendaryExplorer.Audio;
using LegendaryExplorer.UnrealExtensions;
using LegendaryExplorerCore.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NAudio.Wave;

namespace LegendaryExplorer.Tests.Audio;

[TestClass]
public class WwiseSurroundAudioTests
{
    [TestMethod]
    [DataRow(MEGame.ME3, 3, 48000, 0x7)]
    [DataRow(MEGame.ME3, 4, 48000, 0x33)]
    [DataRow(MEGame.ME3, 5, 48000, 0x37)]
    [DataRow(MEGame.ME3, 6, 44100, 0x3F)]
    [DataRow(MEGame.ME3, 7, 48000, 0x637)]
    [DataRow(MEGame.ME3, 8, 48000, 0x63F)]
    [DataRow(MEGame.LE2, 3, 24000, 0x7)]
    [DataRow(MEGame.LE2, 4, 32000, 0x33)]
    [DataRow(MEGame.LE2, 5, 44100, 0x37)]
    [DataRow(MEGame.LE2, 6, 48000, 0x3F)]
    [DataRow(MEGame.LE2, 7, 24000, 0x637)]
    [DataRow(MEGame.LE2, 8, 48000, 0x63F)]
    [DataRow(MEGame.LE3, 6, 44100, 0x3F)]
    [DataRow(MEGame.LE3, 8, 48000, 0x63F)]
    public void PreservesEverySurroundChannelInWaveSpeakerOrder(MEGame game, int channels, int sampleRate, int expectedMask)
        => AssertChannels(game, channels, sampleRate, (uint)expectedMask, extensible: false);

    [TestMethod]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void PreservesExplicitExtensibleSpeakerMask(MEGame game)
        => AssertChannels(game, 6, 48000, 0x60F, extensible: true, preserveInputRate: true);

    [TestMethod]
    [DataRow(MEGame.ME3, 96000)]
    [DataRow(MEGame.LE2, 192000)]
    [DataRow(MEGame.LE3, 96000)]
    public void PreserveInputRateKeepsHighSampleRates(MEGame game, int sampleRate)
        => AssertChannels(game, 2, sampleRate, 0x3, extensible: false, preserveInputRate: true);

    private static void AssertChannels(MEGame game, int channels, int sampleRate, uint mask,
        bool extensible, bool preserveInputRate = false)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"LEX_Surround_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "channels.wav");
            WaveFormat format = extensible ? new WaveFormatExtensible(sampleRate, 16, channels) :
                new WaveFormat(sampleRate, 16, channels);
            int frames = sampleRate / 4;
            using (var writer = new WaveFileWriter(source, format))
            {
                for (int frame = 0; frame < frames; frame++)
                    for (int channel = 0; channel < channels; channel++)
                        writer.WriteSample((float)(0.5 * Math.Sin(2 * Math.PI * Frequency(channel) * frame / sampleRate)));
            }
            if (extensible)
            {
                byte[] wave = File.ReadAllBytes(source);
                // WaveFileWriter places fmt immediately after RIFF/WAVE; the channel
                // mask is at offset20 within WAVEFORMATEXTENSIBLE.
                BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), mask);
                File.WriteAllBytes(source, wave);
            }

            string encodedPath = Path.Combine(directory, game == MEGame.ME3 ? "channels.ogg" : "channels.wem");
            WwiseAudioEncoder.ConvertAudioFile(game, source, encodedPath, preserveInputRate ? 0 : sampleRate);
            byte[] encoded = File.ReadAllBytes(encodedPath);
            uint actualConfig = BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(40));
            Assert.AreEqual(game == MEGame.ME3 ? mask : mask << 12 | 0x100u | (uint)channels, actualConfig);
            if (game == MEGame.ME3)
            {
                using var stream = new MemoryStream(encoded);
                using var converted = AudioStreamHelper.ConvertWwiseOggToME3Ogg(stream);
                encoded = converted.ToArray();
            }
            string decodePath = Path.Combine(directory, "decode.wem");
            File.WriteAllBytes(decodePath, encoded);
            using var decoded = AudioStreamHelper.ConvertRIFFToWaveVGMStream(decodePath);
            decoded.Position = 0;
            using var reader = new WaveFileReader(decoded);
            Assert.AreEqual(channels, reader.WaveFormat.Channels);
            Assert.AreEqual(sampleRate, reader.WaveFormat.SampleRate);
            Assert.AreEqual(frames, reader.SampleCount);
            float[] samples = new float[frames * channels];
            int read = reader.ToSampleProvider().Read(samples, 0, samples.Length);
            Assert.AreEqual(samples.Length, read);
            for (int channel = 0; channel < channels; channel++)
            {
                double expected = Amplitude(samples, channels, channel, Frequency(channel), sampleRate);
                double wrong = Enumerable.Range(0, channels).Where(other => other != channel)
                    .Select(other => Amplitude(samples, channels, channel, Frequency(other), sampleRate)).DefaultIfEmpty().Max();
                Assert.IsTrue(expected > 0.25 && expected > wrong * 5,
                    $"Channel {channel} lost its tone or moved to another speaker: expected amplitude {expected}, other {wrong}.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int Frequency(int channel) => 240 + channel * 120;

    private static double Amplitude(float[] samples, int channels, int channel, int frequency, int sampleRate)
    {
        double sine = 0, cosine = 0;
        int frames = samples.Length / channels;
        for (int frame = 0; frame < frames; frame++)
        {
            double phase = 2 * Math.PI * frequency * frame / sampleRate;
            sine += samples[frame * channels + channel] * Math.Sin(phase);
            cosine += samples[frame * channels + channel] * Math.Cos(phase);
        }
        return 2 * Math.Sqrt(sine * sine + cosine * cosine) / frames;
    }
}
