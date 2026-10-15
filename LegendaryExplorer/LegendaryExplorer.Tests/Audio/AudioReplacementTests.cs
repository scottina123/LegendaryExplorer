using System;
using System.IO;
using System.Linq;
using LegendaryExplorer.Audio;
using LegendaryExplorer.UnrealExtensions;
using LegendaryExplorerCore.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NAudio.Wave;

namespace LegendaryExplorer.Tests.Audio;

[TestClass]
public class AudioReplacementTests
{
    [TestMethod]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void EmbeddedReplacementReadsPlayableMediaAndPreservesAlreadyImportedMedia(MEGame game)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"LEX_Replacement_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "source.wav");
            using (var writer = new WaveFileWriter(input, new WaveFormat(24000, 16, 1)))
            {
                for (int frame = 0; frame < 4800; frame++)
                {
                    writer.WriteSample((float)(0.5 * Math.Sin(2 * Math.PI * 440 * frame / 24000)));
                }
            }
            string encoded = Path.Combine(directory, "encoded.wem");
            WwiseAudioEncoder.ConvertAudioFile(game, input, encoded);
            byte[] encodedBefore = File.ReadAllBytes(encoded);
            byte[] replacement = AudioStreamHelper.ReadAudioForImport(encoded, game);
            Assert.IsTrue(replacement.Length > 94, "Replacement must preserve the encoded media payload.");
            Assert.AreEqual(66, BitConverter.ToInt32(replacement, 16));
            CollectionAssert.AreEqual(encodedBefore, File.ReadAllBytes(encoded));

            string gameReady = Path.Combine(directory, "game-ready.wem");
            File.WriteAllBytes(gameReady, replacement);
            CollectionAssert.AreEqual(replacement, AudioStreamHelper.ReadAudioForImport(gameReady, game));
            CollectionAssert.AreEqual(replacement, File.ReadAllBytes(gameReady));

            string decodeInput = Path.Combine(directory, "decode.wem");
            File.WriteAllBytes(decodeInput, replacement);
            using var decoded = AudioStreamHelper.ConvertRIFFToWaveVGMStream(decodeInput);
            decoded.Position = 0;
            using var reader = new WaveFileReader(decoded);
            Assert.AreEqual(0.2, reader.TotalTime.TotalSeconds, 0.001);
            var samples = new float[4800];
            int count = reader.ToSampleProvider().Read(samples, 0, samples.Length);
            Assert.IsTrue(samples.Take(count).Average(sample => sample * sample) > 0.005);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
