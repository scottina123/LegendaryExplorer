using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NAudio.Wave;
using NAudio.WaveFormRenderer;

namespace LegendaryExplorer.Tests.Audio;

[TestClass]
public class WaveformRendererTests
{
    [TestMethod]
    [DataRow(1, 16, 22200)]
    [DataRow(2, 16, 22200)] // The previous renderer requested 74 bytes from a four-byte-aligned WAV.
    [DataRow(2, 24, 22200)]
    [DataRow(6, 16, 22200)]
    [DataRow(8, 32, 22201)]
    [DataRow(2, 16, 3)]
    [DataRow(2, 16, 1)]
    [DataRow(2, 16, 0)]
    public void RenderReadsCompleteFramesAndHandlesClipsShorterThanTheImage(int channels, int bits, int frames)
    {
        using var wave = CreateWave(channels, bits, frames);
        using var reader = new TrackingWaveFileReader(wave);
        var settings = new StandardWaveFormRendererSettings
        {
            Width = 1200,
            TopHeight = 32,
            BottomHeight = 32,
            BackgroundColor = Color.Transparent
        };

        using var image = new WaveFormRenderer().Render(reader, new AveragePeakProvider(4), settings);

        Assert.AreEqual(1200, image.Width);
        Assert.AreEqual(64, image.Height);
        Assert.IsGreaterThan(0, reader.RequestedByteCounts.Count);
        foreach (int requested in reader.RequestedByteCounts)
        {
            Assert.IsGreaterThanOrEqualTo(reader.WaveFormat.BlockAlign, requested);
            Assert.AreEqual(0, requested % reader.WaveFormat.BlockAlign,
                "Each peak must request complete interleaved audio frames.");
        }

        Assert.AreEqual(reader.Length, reader.Position, "The waveform should include the end of the clip.");
        if (frames > 0)
        {
            Assert.AreNotEqual(Color.Transparent.ToArgb(), ((Bitmap)image).GetPixel(0, 20).ToArgb(),
                "A clip shorter than the waveform must still produce a visible peak.");
        }
    }

    [TestMethod]
    public void AveragePeakProviderReturnsSilenceAfterTheLastFrame()
    {
        using var wave = CreateWave(1, 16, 1);
        using var reader = new WaveFileReader(wave);
        var provider = new AveragePeakProvider(4);
        provider.Init(reader.ToSampleProvider(), 1);

        var first = provider.GetNextPeak();
        Assert.IsGreaterThan(0f, first.Max);
        for (int i = 0; i < 3; i++)
        {
            var end = provider.GetNextPeak();
            Assert.AreEqual(0f, end.Min);
            Assert.AreEqual(0f, end.Max);
        }
    }

    private static MemoryStream CreateWave(int channels, int bits, int frames)
    {
        var format = bits == 32
            ? WaveFormat.CreateIeeeFloatWaveFormat(44100, channels)
            : new WaveFormat(44100, bits, channels);
        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, format))
        {
            for (int i = 0; i < frames * channels; i++)
                writer.WriteSample(0.125f);
        }

        return new MemoryStream(output.ToArray());
    }

    private sealed class TrackingWaveFileReader(Stream source) : WaveFileReader(source)
    {
        public List<int> RequestedByteCounts { get; } = [];

        public override int Read(byte[] buffer, int offset, int count)
        {
            RequestedByteCounts.Add(count);
            return base.Read(buffer, offset, count);
        }
    }
}
