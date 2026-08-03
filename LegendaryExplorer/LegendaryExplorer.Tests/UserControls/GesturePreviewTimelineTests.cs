using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class GesturePreviewTimelineTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void TerminationOnlyKeyStopsEveryEarlierGesture()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("GestureTimelineTest.pcc", MEGame.LE3);
        ExportEntry firstAnimation = CreateAnimation(package, "FirstAnimation", 10);
        ExportEntry secondAnimation = CreateAnimation(package, "SecondAnimation", 10);
        var animations = new List<GesturePreviewExportLoader.GestureAnimationItem>
        {
            CreateGesture(0, 0, firstAnimation),
            CreateGesture(1, 2, secondAnimation),
            new()
            {
                GestureIndex = 2,
                Time = 4,
                SlotOrder = 3,
                SlotName = "Control",
                SetName = "None",
                AnimationName = "None",
                Settings = new GesturePreviewExportLoader.GesturePlaybackSettings
                {
                    TerminateAllGestures = true,
                },
                IsControlMarker = true,
            },
        };

        List<AnimationPreviewControl.AnimationTimelineClip> timeline = BuildPlaybackTimeline(animations);

        Assert.HasCount(2, timeline);
        Assert.IsTrue(timeline.All(clip => Math.Abs(clip.EndTime - 4) < 0.0001f));
    }

    [TestMethod]
    public void ChainedKeyReplacesTheImmediatelyPreviousGesture()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("GestureChainTest.pcc", MEGame.LE3);
        ExportEntry firstAnimation = CreateAnimation(package, "FirstAnimation", 10);
        ExportEntry chainedAnimation = CreateAnimation(package, "ChainedAnimation", 10);
        var animations = new List<GesturePreviewExportLoader.GestureAnimationItem>
        {
            CreateGesture(0, 0, firstAnimation),
            CreateGesture(1, 3, chainedAnimation, chainToPrevious: true),
        };

        List<AnimationPreviewControl.AnimationTimelineClip> timeline = BuildPlaybackTimeline(animations);

        Assert.HasCount(2, timeline);
        Assert.AreEqual(3, timeline.Single(clip => clip.AnimationExport == firstAnimation).EndTime, 0.0001f);
        Assert.AreEqual(13, timeline.Single(clip => clip.AnimationExport == chainedAnimation).EndTime, 0.0001f);
    }

    private static ExportEntry CreateAnimation(IMEPackage package, string name, float duration)
    {
        ExportEntry animation = package.CreateExport(name, "AnimSequence", indexed: false);
        animation.WriteProperty(new FloatProperty(duration, "SequenceLength"));
        return animation;
    }

    private static GesturePreviewExportLoader.GestureAnimationItem CreateGesture(int index, float time,
        ExportEntry animation, bool chainToPrevious = false) => new()
    {
        GestureIndex = index,
        Time = time,
        SlotOrder = 1,
        SlotName = "Gesture",
        SetName = "TestSet",
        AnimationName = animation.ObjectName,
        AnimationExport = animation,
        Settings = new GesturePreviewExportLoader.GesturePlaybackSettings
        {
            ChainToPrevious = chainToPrevious,
        },
    };

    private static List<AnimationPreviewControl.AnimationTimelineClip> BuildPlaybackTimeline(
        List<GesturePreviewExportLoader.GestureAnimationItem> animations)
    {
        MethodInfo method = typeof(GesturePreviewExportLoader).GetMethod("BuildPlaybackTimeline",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        return (List<AnimationPreviewControl.AnimationTimelineClip>)method.Invoke(null, [animations]);
    }
}
