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

    [TestMethod]
    public void EveryBioGestureDataEntryIsScheduledWithTheStartingPoseAsItsBaseLayer()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("MultiGestureTest.pcc", MEGame.LE3);
        ExportEntry startingPose = CreateAnimation(package, "StartingPose", 3);
        ExportEntry first = CreateAnimation(package, "First", 10);
        ExportEntry second = CreateAnimation(package, "Second", 10);
        ExportEntry third = CreateAnimation(package, "Third", 10);
        ExportEntry fourth = CreateAnimation(package, "Fourth", 10);
        var animations = new List<GesturePreviewExportLoader.GestureAnimationItem>
        {
            new()
            {
                Time = 0,
                SlotOrder = -1,
                SlotName = "Starting Pose",
                SetName = "PoseSet",
                AnimationName = startingPose.ObjectName,
                AnimationExport = startingPose,
                Settings = new GesturePreviewExportLoader.GesturePlaybackSettings(),
            },
            CreateGesture(0, 0, first),
            CreateGesture(1, 4.417f, second, chainToPrevious: true),
            CreateGesture(2, 4.458f, third),
            CreateGesture(3, 4.5f, fourth),
        };

        List<AnimationPreviewControl.AnimationTimelineClip> timeline = BuildPlaybackTimeline(animations);

        Assert.HasCount(5, timeline);
        Assert.IsTrue(timeline.Single(clip => clip.AnimationExport == startingPose).IsBaseLayer);
        Assert.IsTrue(timeline.Where(clip => clip.AnimationExport != startingPose)
            .All(clip => clip.UseMotionBoneMask));
        CollectionAssert.AreEquivalent(new[] { first, second, third, fourth }, timeline
            .Where(clip => clip.AnimationExport != startingPose)
            .Select(clip => clip.AnimationExport)
            .ToArray());
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
