using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegendaryExplorer.Resources;
using LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static LegendaryExplorer.UserControls.ExportLoaderControls.FaceFXAnimSetEditorControl;

namespace LegendaryExplorer.Tests.Tools.FaceFXEditor
{
    [TestClass]
    public class AutoFaceFXGeneratorTests
    {
        [TestMethod]
        public void LocalNormalizationIsUnaffectedByDistantLoudAudio()
        {
            List<AmplitudeData> shortClip = MakeQuietPhrase();
            List<AmplitudeData> longClip = MakeQuietPhrase();
            for (int i = 0; i < 200; i++)
            {
                longClip.Add(new AmplitudeData
                {
                    Time = longClip.Count * 0.02f,
                    Amplitude = i >= 150 ? 0.95f : 0.004f
                });
            }

            AudioAnalyzer.NormalizeAmplitudesLocally(shortClip);
            AudioAnalyzer.NormalizeAmplitudesLocally(longClip);

            for (int i = 55; i < 105; i++)
                Assert.AreEqual(shortClip[i].NormalizedAmplitude, longClip[i].NormalizedAmplitude, 0.0001f);
            Assert.IsTrue(shortClip.Skip(55).Take(50).Average(sample => sample.NormalizedAmplitude) > 0.7f);
        }

        [TestMethod]
        public void StereoAmplitudeWindowsStayOnTheAudioTimeline()
        {
            List<AmplitudeData> samples = AudioAnalyzer.AnalyzeWavAmplitude(
                MakePcmWav(sampleRate: 1000, channels: 2, frameCount: 1000));

            Assert.AreEqual(50, samples.Count);
            Assert.AreEqual(0f, samples[0].Time, 0.0001f);
            Assert.AreEqual(0.98f, samples[^1].Time, 0.0001f);
        }

        [TestMethod]
        public void PhonemeTimingUsesAllSegmentsWithoutAccumulatedDrift()
        {
            var phonemes = Enumerable.Range(0, 400).Select(index => new PhonemeData
            {
                Phoneme = index % 2 == 0 ? "AA" : "T",
                Duration = index % 5 == 0 ? 0.2f : 0.08f
            }).ToList();
            var segments = new List<FaceFXSpeechSegment>
            {
                new() { StartTime = 0.25f, EndTime = 7.75f },
                new() { StartTime = 8.20f, EndTime = 19.30f },
                new() { StartTime = 20.10f, EndTime = 38.00f }
            };

            List<FaceFXTimedPhoneme> timed = FaceFXGenerationMath.MapPhonemesToSpeech(
                phonemes, segments, 38f, _ => 0.5f);

            Assert.AreEqual(phonemes.Count, timed.Count);
            Assert.AreEqual(0.25f, timed[0].StartTime, 0.0001f);
            Assert.AreEqual(38f, timed[^1].StartTime + timed[^1].Duration, 0.0001f);
            Assert.IsTrue(timed.All(phoneme => phoneme.Duration > 0f));
            Assert.IsFalse(timed.Any(phoneme =>
                phoneme.StartTime < 8.2f && phoneme.StartTime + phoneme.Duration > 7.7501f));
            Assert.IsFalse(timed.Any(phoneme =>
                phoneme.StartTime < 20.1f && phoneme.StartTime + phoneme.Duration > 19.3001f));
        }

        [TestMethod]
        public void CoarticulationInfluenceStaysLocalOnLongLines()
        {
            const float sampleRate = 50f;
            var samples = new float[60 * 50 + 1];
            var phoneme = new FaceFXTimedPhoneme
            {
                Phoneme = "AA",
                StartTime = 30f,
                Duration = 0.12f,
                Intensity = 1f
            };

            FaceFXGenerationMath.AddLocalVisemeEnvelope(samples, sampleRate, phoneme, 1f);

            int first = Array.FindIndex(samples, value => value > 0f);
            int last = Array.FindLastIndex(samples, value => value > 0f);
            Assert.IsTrue(first / sampleRate >= 30f - FaceFXGenerationMath.CoarticulationLeadSeconds - 1f / sampleRate);
            Assert.IsTrue(last / sampleRate <= 30.12f + FaceFXGenerationMath.CoarticulationTrailSeconds + 1f / sampleRate);
            Assert.AreEqual(0f, samples[10 * 50]);
            Assert.AreEqual(0f, samples[50 * 50]);
        }

        [TestMethod]
        public void KeyReductionPreservesTheSameShiftedLocalPeak()
        {
            const float sampleRate = 50f;
            float[] shortCurve = MakePulseCurve(8, 2, sampleRate);
            float[] longCurve = MakePulseCurve(80, 42, sampleRate);

            List<int> shortKeys = FaceFXGenerationMath.SelectKeyframeIndices(shortCurve, sampleRate)
                .Where(index => index / sampleRate is >= 1.5f and <= 2.5f)
                .Select(index => index - 2 * (int)sampleRate)
                .ToList();
            List<int> longKeys = FaceFXGenerationMath.SelectKeyframeIndices(longCurve, sampleRate)
                .Where(index => index / sampleRate is >= 41.5f and <= 42.5f)
                .Select(index => index - 42 * (int)sampleRate)
                .ToList();

            CollectionAssert.AreEqual(shortKeys, longKeys);
            Assert.IsTrue(shortKeys.Contains(0), "The local viseme peak must be retained.");
        }

        [TestMethod]
        public void AuditedEmotionCatalogAndRigMapsAreComplete()
        {
            CollectionAssert.AreEquivalent(new[]
            {
                "Amusement", "Anger", "Anxiety", "Aversion", "Concern", "Dejection",
                "Disdain", "Disgust", "Fear", "Grief", "Indignation", "Joy", "Laughter",
                "Melancholy", "Rage", "Revulsion", "Sadness", "Satisfaction", "Stern", "Terror"
            }, FaceFXEmotionCatalog.LayeredFamilies.ToArray());
            Assert.AreEqual(50, FaceFXEmotionCatalog.GetForSpecies(FaceFXSpecies.HumanFemale).Count);
            Assert.AreEqual(24, FaceFXEmotionCatalog.GetForSpecies(FaceFXSpecies.AlienB).Count);
            Assert.AreEqual(32, FaceFXEmotionCatalog.GetForSpecies(FaceFXSpecies.Drell).Count);

            CollectionAssert.AreEquivalent(
                PhonemeToVisemeMap.HumanFemaleVisemes,
                PhonemeToVisemeMap.GetVisemes(FaceFXSpecies.Asari));
            CollectionAssert.AreEqual(PhonemeToVisemeMap.QuarianVisemes, PhonemeToVisemeMap.GetVisemes(FaceFXSpecies.Quarian));
            CollectionAssert.AreEqual(PhonemeToVisemeMap.GethVisemes, PhonemeToVisemeMap.GetVisemes(FaceFXSpecies.Geth));
            Assert.AreEqual(22, PhonemeToVisemeMap.QuarianVisemes.Length);
            Assert.AreEqual(18, PhonemeToVisemeMap.GethVisemes.Length);
            CollectionAssert.AreEqual(new[] { "m_JawOpen" }, PhonemeToVisemeMap.GetVisemes(FaceFXSpecies.Yahg));
            Assert.IsFalse(PhonemeToVisemeMap.GetVisemes(FaceFXSpecies.Krogan).Contains("lowerLipCurlin"));
            Assert.IsTrue(PhonemeToVisemeMap.GetVisemes(FaceFXSpecies.Krogan).Contains("jawRotateDown"));
            Assert.IsFalse(PhonemeToVisemeMap.GetVisemes(FaceFXSpecies.Krogan).Contains("jawBack"));

            Dictionary<string, VisemeMapping[]> gethMap = PhonemeToVisemeMap.GetPhonemeMap(FaceFXSpecies.Geth);
            foreach ((string phoneme, VisemeMapping[] mappings) in gethMap)
            {
                Assert.IsTrue(mappings.Any(mapping => mapping.VisemeName == "blinker"), $"{phoneme} must drive blinker.");
                Assert.IsFalse(mappings.Any(mapping => mapping.VisemeName == "jawOpen"), $"{phoneme} must use the Geth rig, not a mouth curve.");
            }
            Assert.IsTrue(gethMap["AA"].Any(mapping => mapping.VisemeName == "G_TalkingNormal"));
            Assert.IsTrue(gethMap["AA"].Any(mapping => mapping.VisemeName == "Emphasis_Head_Pitch"));
            Assert.IsTrue(gethMap["H"].Any(mapping => mapping.VisemeName == "E_Neutral_Thoughtfull"));
        }

        [TestMethod]
        public void QuarianReferenceRetainsItsCompleteAuthoredAnimationSet()
        {
            FxaAnimationData reference = FxaXmlParser.ParseFxaXml(EmbeddedResources.QuarianFaceFxReference);

            CollectionAssert.AreEquivalent(PhonemeToVisemeMap.QuarianVisemes, reference.Animations.Keys.ToArray());
            Assert.IsTrue(reference.Animations.TryGetValue("Blink", out FxaAnimation blink));
            Assert.AreEqual(6, blink.Keys.Count);
            Assert.IsTrue(blink.Keys.Max(key => key.Value) > 0.9f);
        }

        [TestMethod]
        public void QuarianAndGethGenerationEmitTheirCompleteLegacyAnimationLists()
        {
            AssertGeneratedAnimationList(FaceFXSpecies.Quarian, PhonemeToVisemeMap.QuarianVisemes);
            AssertGeneratedAnimationList(FaceFXSpecies.Quarian, PhonemeToVisemeMap.QuarianVisemes, string.Empty);
            AssertGeneratedAnimationList(FaceFXSpecies.Geth, PhonemeToVisemeMap.GethVisemes);
        }

        [TestMethod]
        public void QuarianGenerationUsesTheAuthoredJawOpenCurve()
        {
            (FaceFXLine line, TestFaceFxBinary faceFx) = GenerateLine(FaceFXSpecies.Quarian, "The signal is synchronized and ready.");
            int animationIndex = line.AnimationNames.FindIndex(nameIndex => faceFx.Names[nameIndex] == "jawOpen");
            Assert.IsTrue(animationIndex >= 0);
            int pointOffset = line.NumKeys.Take(animationIndex).Sum();
            List<FaceFXControlPoint> actual = line.Points.Skip(pointOffset).Take(line.NumKeys[animationIndex]).ToList();
            FxaAnimation expected = FxaXmlParser.ParseFxaXml(EmbeddedResources.QuarianFaceFxReference).Animations["jawOpen"];

            Assert.AreEqual(expected.Keys.Count, actual.Count);
            float timeScale = actual[^1].time / expected.Keys[^1].Time;
            for (int index = 0; index < expected.Keys.Count; index++)
            {
                Assert.AreEqual(expected.Keys[index].Time * timeScale, actual[index].time, 0.0001f);
                Assert.AreEqual(expected.Keys[index].Value, actual[index].weight, 0.0001f);
                Assert.AreEqual(expected.Keys[index].InTangent, actual[index].inTangent, 0.0001f);
                Assert.AreEqual(expected.Keys[index].OutTangent, actual[index].leaveTangent, 0.0001f);
            }
        }

        private static List<AmplitudeData> MakeQuietPhrase()
        {
            return Enumerable.Range(0, 160).Select(index => new AmplitudeData
            {
                Time = index * 0.02f,
                Amplitude = index is >= 50 and < 115
                    ? 0.07f + (index % 7) * 0.003f
                    : 0.004f
            }).ToList();
        }

        private static void AssertGeneratedAnimationList(FaceFXSpecies species, string[] expected, string text = "The signal is synchronized and ready.")
        {
            (FaceFXLine line, TestFaceFxBinary faceFx) = GenerateLine(species, text);
            string[] actual = line.AnimationNames.Select(index => faceFx.Names[index]).ToArray();
            CollectionAssert.AreEquivalent(expected, actual);
        }

        private static (FaceFXLine Line, TestFaceFxBinary FaceFx) GenerateLine(FaceFXSpecies species, string text)
        {
            var line = new FaceFXLine
            {
                AnimationNames = [],
                NumKeys = [],
                Points = []
            };
            var faceFx = new TestFaceFxBinary(line);
            var generator = new FaceFXGenerator(
                faceFx,
                line,
                text,
                null,
                new FaceFXGenerationOptions
                {
                    Species = species,
                    UseAudioAmplitude = false,
                    GenerateBlinkAnimation = false,
                    GenerateEyebrowAnimation = false,
                    GenerateHeadMovement = false
                });

            Assert.IsTrue(generator.Generate(), generator.LastError);
            return (line, faceFx);
        }

        private sealed class TestFaceFxBinary : IFaceFXBinary
        {
            public TestFaceFxBinary(FaceFXLine line)
            {
                Lines = [line];
            }

            public List<string> Names { get; } = [];
            public List<FaceFXLine> Lines { get; }
            public ObjectBinary Binary => null;
        }

        private static float[] MakePulseCurve(int durationSeconds, int centerSeconds, float sampleRate)
        {
            var result = new float[durationSeconds * (int)sampleRate + 1];
            int center = centerSeconds * (int)sampleRate;
            for (int offset = -10; offset <= 10; offset++)
                result[center + offset] = 1f - Math.Abs(offset) / 10f;
            return result;
        }

        private static byte[] MakePcmWav(int sampleRate, short channels, int frameCount)
        {
            const short bitsPerSample = 16;
            int blockAlign = channels * bitsPerSample / 8;
            int dataLength = frameCount * blockAlign;
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write("RIFF"u8.ToArray());
            writer.Write(36 + dataLength);
            writer.Write("WAVE"u8.ToArray());
            writer.Write("fmt "u8.ToArray());
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * blockAlign);
            writer.Write((short)blockAlign);
            writer.Write(bitsPerSample);
            writer.Write("data"u8.ToArray());
            writer.Write(dataLength);
            for (int frame = 0; frame < frameCount; frame++)
            {
                for (int channel = 0; channel < channels; channel++)
                    writer.Write((short)(Math.Sin(frame * 0.1) * short.MaxValue * 0.25));
            }
            writer.Flush();
            return stream.ToArray();
        }
    }
}
