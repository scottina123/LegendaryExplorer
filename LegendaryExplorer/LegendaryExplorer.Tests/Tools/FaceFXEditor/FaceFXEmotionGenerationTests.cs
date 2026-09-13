using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.Animation;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static LegendaryExplorer.UserControls.ExportLoaderControls.FaceFXAnimSetEditorControl;

namespace LegendaryExplorer.Tests.Tools.FaceFXEditor;

[TestClass]
public class FaceFXEmotionGenerationTests
{
    private const string PresetName = "E_Neutral_Shock";

    [TestMethod]
    public void TimedInsertionPreservesOtherTracksAndEmotionKeysOutsideTheInterval()
    {
        var source = CreateSource();
        source.Names.Add(PresetName);
        source.Line.AnimationNames.Add(3);
        source.Line.NumKeys.Add(4);
        FaceFXControlPoint[] originalEmotion =
        [
            new() { time = 0, weight = 0.1f, inTangent = 2, leaveTangent = 3 },
            new() { time = 1, weight = 0.5f },
            new() { time = 3, weight = 0.9f },
            new() { time = 5, weight = 0.2f, inTangent = 4, leaveTangent = 5 }
        ];
        var originalOtherKeys = source.Line.Points.ToArray();
        source.Line.Points.AddRange(originalEmotion);

        Generate(source, Options(2, 4));

        CollectionAssert.AreEqual(originalOtherKeys, source.Line.Points.Take(originalOtherKeys.Length).ToArray());
        var emotion = GetTrack(source, PresetName);
        CollectionAssert.AreEqual(new[] { originalEmotion[0], originalEmotion[1], originalEmotion[3] },
            emotion.Where(point => point.time < 2 || point.time > 4).ToArray());
        Assert.AreEqual(0f, emotion.Single(point => point.time == 2).weight);
        Assert.AreEqual(0f, emotion.Single(point => point.time == 4).weight);
        Assert.AreEqual(0.4f, emotion.Single(point => point.time == 2.2f).weight, 0.0001f);
        Assert.IsFalse(emotion.Any(point => point.time == 3));
        CollectionAssert.AreEqual(new[] { 2, 1, 7 }, source.Line.NumKeys);
    }

    [TestMethod]
    public void RepeatedInsertsOfTheSameEmotionRetainBothExpressions()
    {
        var source = CreateSource();
        Generate(source, Options(0, 1));
        var first = GetTrack(source, PresetName).ToArray();
        Generate(source, Options(3, 4));

        var keys = GetTrack(source, PresetName);
        CollectionAssert.AreEqual(first, keys.Where(point => point.time <= 1).ToArray());
        Assert.HasCount(8, keys);
        Assert.HasCount(1, source.Line.AnimationNames.Where(index => source.Names[index] == PresetName));
        Assert.AreEqual(0f, keys.Single(point => point.time == 3).weight);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void BothGenerationModesPlaceEveryEmotionLayerInsideTheChosenInterval(bool emotionsOnly)
    {
        var source = CreateSource();
        var options = Options(1.25f, 2.75f);
        options.AddEmotionToExistingLine = emotionsOnly;
        options.EmotionChoice = new FaceFXEmotionChoice { LayeredFamily = "Joy" };

        Generate(source, options);

        var names = source.Line.AnimationNames.Select(index => source.Names[index]).Where(name => name.StartsWith("E_")).ToArray();
        Assert.HasCount(4, names);
        foreach (string name in names)
        {
            var keys = GetTrack(source, name);
            Assert.AreEqual(1.25f, keys.First().time);
            Assert.AreEqual(2.75f, keys.Last().time);
            Assert.AreEqual(0f, keys.First().weight);
            Assert.AreEqual(0f, keys.Last().weight);
            Assert.IsTrue(keys.Any(point => point.weight > 0));
        }
    }

    [TestMethod]
    [DataRow(-1f, 2f)]
    [DataRow(2f, 2f)]
    [DataRow(3f, 2f)]
    [DataRow(float.NaN, 2f)]
    [DataRow(1f, float.PositiveInfinity)]
    public void InvalidTimingFailsBeforeChangingAnyAnimation(float start, float end)
    {
        var source = CreateSource();
        var originalPoints = source.Line.Points.ToArray();
        var originalNames = source.Names.ToArray();
        var options = Options(start, end);
        options.AddEmotionToExistingLine = false;
        var generator = new FaceFXGenerator(source, source.Line, "Test dialogue", null, options);

        Assert.IsFalse(generator.Generate());
        Assert.IsNotNull(generator.LastError);
        CollectionAssert.AreEqual(originalPoints, source.Line.Points);
        CollectionAssert.AreEqual(originalNames, source.Names);
        CollectionAssert.AreEqual(new[] { 2, 1 }, source.Line.NumKeys);
    }

    [TestMethod]
    public void WholeLineEmotionBehaviorIsRetainedWhenNoIntervalIsRequested()
    {
        var source = CreateSource();
        var options = Options(0, 1);
        options.EmotionStartTime = options.EmotionEndTime = null;

        Generate(source, options);

        var keys = GetTrack(source, PresetName);
        Assert.HasCount(2, keys);
        Assert.AreEqual(5f, keys.Last().time);
        Assert.IsTrue(keys.All(point => point.weight > 0));
    }

    [TestMethod]
    public void PreviewUsesDetachedNamesAndPointsUntilExplicitlyApplied()
    {
        var source = CreateSource();
        var originalPoints = source.Line.Points.ToArray();
        var originalNames = source.Names.ToArray();
        var draft = new FaceFXGenerationDraft(source, source.Line, MEGame.LE3);
        Assert.IsTrue(new FaceFXGenerator(draft, draft.Line, "", null, Options(1, 2)).Generate());

        CollectionAssert.AreEqual(originalPoints, source.Line.Points);
        CollectionAssert.AreEqual(originalNames, source.Names);
        source.Names.Add("NameAddedAfterPreview");
        draft.ApplyTo(source, source.Line);

        CollectionAssert.AreEqual(draft.Line.Points, source.Line.Points);
        CollectionAssert.AreEqual(draft.Line.AnimationNames.Select(index => draft.Names[index]).ToArray(),
            source.Line.AnimationNames.Select(index => source.Names[index]).ToArray());
        Assert.AreEqual("TestLine", source.Line.NameAsString);
        Assert.AreEqual("TestPath", source.Line.Path);
    }

    [TestMethod]
    public void GeneratedEmotionMovesTheChosenHeadSkeletonOnlyDuringItsInterval()
    {
        var source = CreateSource();
        Generate(source, Options(2, 4));
        var head = new SkeletalMesh
        {
            RefSkeleton =
            [
                new() { Name = "root", Orientation = Quaternion.Identity, ParentIndex = 0 },
                new() { Name = "jaw", Orientation = Quaternion.Identity, Position = Vector3.UnitX, ParentIndex = 0 }
            ]
        };
        var rig = FaceFXAsset.Create(MEGame.LE3);
        rig.Names = ["jaw", PresetName];
        rig.CompiledFaceGraph =
        [
            new() { Name = 1, NodeType = FxNodeType.BonePose, MinVal = 0, MaxVal = 1,
                InputOperation = FxInputOperation.Sum, InputLinks = [], UserProperties = [] }
        ];
        rig.RefBones =
        [
            new() { RefBone = new FaceFxBone { BoneName = 0, Position = Vector3.UnitX, Rotation = Quaternion.Identity },
                RefBoneInverseRot = Quaternion.Identity,
                Links = [new FaceFxBoneLink { GraphIndex = 0,
                    OptimizedBone = new FaceFxBone { Position = new Vector3(2, 0, 0), Rotation = Quaternion.Identity } }] }
        ];
        var player = new FaceFxPlayer(head) { FxActor = rig, AnimSet = source.Set };
        player.SetFaceFXLine(source.Line);
        player.SetCurrentTime(1);
        Matrix4x4 neutral = player.ComputeSkinningMatrices()[1];
        player.SetCurrentTime(3);
        Matrix4x4 expression = player.ComputeSkinningMatrices()[1];
        player.SetCurrentTime(4.5f);
        Matrix4x4 after = player.ComputeSkinningMatrices()[1];

        Assert.AreNotEqual(neutral, expression);
        Assert.AreEqual(neutral, after);
    }

    [STATestMethod]
    public void BothDialogsUsePlayheadTimingAndPreviewWithoutApplyingChanges()
    {
        foreach (bool emotionsOnly in new[] { true, false })
        {
            var source = CreateSource();
            var originalPoints = source.Line.Points.ToArray();
            var originalNames = source.Names.ToArray();
            var dialog = new AutoFaceFXGenerationDialog(source, source.Line, 0, "Test dialogue", null,
                emotionsOnly: emotionsOnly);
            try
            {
                dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Assert.AreEqual(emotionsOnly, dialog.AddEmotionToExistingLine);
                Assert.AreEqual(!emotionsOnly, dialog.CanChooseGenerationMode);
                dialog.SelectedEmotion = dialog.AvailableEmotions.First(emotion => emotion.PresetAnimation == PresetName);
                var preview = (FaceFXEmotionPreviewControl)dialog.FindName("emotionPreview");
                preview.Position = 1.25;
                ((Button)dialog.FindName("usePlayheadForStart")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                preview.Position = 2.75;
                ((Button)dialog.FindName("usePlayheadForEnd")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.IsTrue(dialog.TryGenerateDraft(out var draft, out var error), error);
                var keys = GetTrack(draft, draft.Line, PresetName);
                Assert.AreEqual(1.25f, keys.First().time);
                Assert.AreEqual(2.75f, keys.Last().time);
                Assert.IsTrue(dialog.TryGenerateDraft(out var samePreview, out error), error);
                Assert.AreSame(draft, samePreview);
                dialog.EmotionStartTimeText = 1.5f.ToString(CultureInfo.CurrentCulture);
                Assert.IsTrue(dialog.TryGenerateDraft(out var changedPreview, out error), error);
                Assert.AreNotSame(draft, changedPreview);
                dialog.EmotionStartTimeText = "invalid";
                Assert.IsFalse(dialog.TryGenerateDraft(out _, out error));
                Assert.IsNotNull(error);
            }
            finally { dialog.Close(); }
            CollectionAssert.AreEqual(originalPoints, source.Line.Points);
            CollectionAssert.AreEqual(originalNames, source.Names);
        }
    }

    private static FaceFXGenerationOptions Options(float start, float end) => new()
    {
        AddEmotionToExistingLine = true,
        EmotionChoice = new FaceFXEmotionChoice { PresetAnimation = PresetName },
        EmotionIntensity = 0.5f,
        EmotionStartTime = start,
        EmotionEndTime = end,
        UseAudioAmplitude = false,
        GenerateBlinkAnimation = false,
        GenerateEyebrowAnimation = false,
        GenerateHeadMovement = false
    };

    private static void Generate(TestBinary source, FaceFXGenerationOptions options)
    {
        var generator = new FaceFXGenerator(source, source.Line, "This is test dialogue.", null, options);
        Assert.IsTrue(generator.Generate(), generator.LastError);
    }

    private static List<FaceFXControlPoint> GetTrack(TestBinary source, string name) => GetTrack(source, source.Line, name);
    private static List<FaceFXControlPoint> GetTrack(IFaceFXBinary source, FaceFXLine line, string name)
    {
        int index = line.AnimationNames.FindIndex(nameIndex => source.Names[nameIndex] == name);
        Assert.IsGreaterThanOrEqualTo(0, index);
        return line.Points.Skip(line.NumKeys.Take(index).Sum()).Take(line.NumKeys[index]).ToList();
    }

    private static TestBinary CreateSource() => new();

    private sealed class TestBinary : IFaceFXBinary
    {
        public FaceFXAnimSet Set { get; } = FaceFXAnimSet.Create(MEGame.LE3);
        public FaceFXLine Line => Set.Lines[0];
        public List<string> Names => Set.Names;
        public List<FaceFXLine> Lines => Set.Lines;
        public ObjectBinary Binary => Set;

        public TestBinary()
        {
            Set.Names = ["TestLine", "m_Open", "Blink"];
            Set.Lines =
            [
                new FaceFXLine
                {
                    NameIndex = 0, NameAsString = "TestLine", ID = "TestLine", Path = "TestPath",
                    AnimationNames = [1, 2], NumKeys = [2, 1],
                    Points = [new() { time = 0, weight = 0.3f, inTangent = 2, leaveTangent = 3 },
                        new() { time = 5, weight = 0.8f }, new() { time = 0.5f, weight = 0.1f }]
                }
            ];
        }
    }
}
