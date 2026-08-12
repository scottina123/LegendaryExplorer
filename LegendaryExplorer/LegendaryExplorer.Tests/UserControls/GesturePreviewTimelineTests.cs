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
    public void EveryBioGestureDataEntryIsScheduledCompletelyWithTheStartingPoseAsItsBaseLayer()
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
            .All(clip => !clip.UseMotionBoneMask));
        CollectionAssert.AreEquivalent(new[] { first, second, third, fourth }, timeline
            .Where(clip => clip.AnimationExport != startingPose)
            .Select(clip => clip.AnimationExport)
            .ToArray());
    }

    [TestMethod]
    public void GestureWithoutStartingPoseAnimatesTheCompleteSkeleton()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("StandaloneGestureTest.pcc", MEGame.LE3);
        ExportEntry gestureAnimation = CreateAnimation(package, "StandaloneGesture", 2);

        List<AnimationPreviewControl.AnimationTimelineClip> timeline = BuildPlaybackTimeline(
        [
            CreateGesture(0, 0, gestureAnimation),
        ]);

        Assert.HasCount(1, timeline);
        Assert.IsFalse(timeline[0].UseMotionBoneMask);
        Assert.IsFalse(timeline[0].IsBaseLayer);
    }

    [TestMethod]
    public void ConversationPoseLoopsUntilTheNodeEndsWithoutExtendingGestures()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("PersistentPoseTest.pcc", MEGame.LE3);
        ExportEntry wallLeanPose = CreateAnimation(package, "WallLeanPose", 5);
        ExportEntry headGesture = CreateAnimation(package, "HeadGesture", 1);
        var animations = new List<GesturePreviewExportLoader.GestureAnimationItem>
        {
            CreatePose(0, 0, wallLeanPose),
            CreateGesture(1, 3.8f, headGesture),
        };

        List<AnimationPreviewControl.AnimationTimelineClip> timeline =
            BuildPlaybackTimelineWithBaseLayer(animations, playbackDuration: 5.467f);

        AnimationPreviewControl.AnimationTimelineClip poseClip =
            timeline.Single(clip => clip.AnimationExport == wallLeanPose);
        AnimationPreviewControl.AnimationTimelineClip gestureClip =
            timeline.Single(clip => clip.AnimationExport == headGesture);
        Assert.AreEqual(5.467f, poseClip.EndTime, 0.0001f);
        Assert.IsTrue(poseClip.Loop);
        Assert.IsTrue(poseClip.IsBaseLayer);
        Assert.AreEqual(0, poseClip.BlendOutDuration, 0.0001f);
        Assert.AreEqual(4.8f, gestureClip.EndTime, 0.0001f);
        Assert.IsFalse(gestureClip.Loop);
    }

    [TestMethod]
    public void PoseTransitionBlendsOutInsteadOfHardCuttingToThePose()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("PoseTransitionTest.pcc", MEGame.LE3);
        ExportEntry wallLeanPose = CreateAnimation(package, "WallLeanPose", 5);
        ExportEntry wallLeanEnter = CreateAnimation(package, "WallLeanEnter", 4.3333335f);
        var settings = new GesturePreviewExportLoader.GesturePlaybackSettings
        {
            StartBlendDuration = 0.1f,
            EndBlendDuration = 0.1f,
        };
        var animations = new List<GesturePreviewExportLoader.GestureAnimationItem>
        {
            new()
            {
                GestureIndex = 0,
                Time = 0,
                SlotOrder = 0,
                SlotName = "Pose",
                AnimationExport = wallLeanPose,
                Settings = settings,
            },
            new()
            {
                GestureIndex = 0,
                Time = 0,
                SlotOrder = 2,
                SlotName = "Transition",
                AnimationExport = wallLeanEnter,
                Settings = settings,
            },
        };

        List<AnimationPreviewControl.AnimationTimelineClip> timeline =
            BuildPlaybackTimelineWithBaseLayer(animations, playbackDuration: 5.467f);

        AnimationPreviewControl.AnimationTimelineClip transition =
            timeline.Single(clip => clip.AnimationExport == wallLeanEnter);
        AnimationPreviewControl.AnimationTimelineClip pose =
            timeline.Single(clip => clip.AnimationExport == wallLeanPose);
        Assert.IsTrue(transition.IsTransition);
        Assert.AreEqual(0.1f, transition.BlendInDuration, 0.0001f);
        Assert.AreEqual(0.1f, transition.BlendOutDuration, 0.0001f);
        Assert.AreEqual(4.3333335f, transition.EndTime, 0.0001f);
        Assert.AreEqual(4.2333335f, pose.StartTime, 0.0001f);
        Assert.AreEqual(0, pose.BlendInDuration, 0.0001f);
        Assert.AreEqual(0, pose.AnimationStartTime, 0.0001f);
    }

    [TestMethod]
    public void PoseTransitionPreservesTheIncomingNegativeTimeGesture()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("PoseTransitionPrerollTest.pcc", MEGame.LE3);
        ExportEntry wanderSouth = CreateAnimation(package, "WanderSouth", 8.333333f);
        ExportEntry wallLeanPose = CreateAnimation(package, "WallLeanPose", 5);
        ExportEntry wallLeanEnter = CreateAnimation(package, "WallLeanEnter", 4.3333335f);
        var incomingSettings = new GesturePreviewExportLoader.GesturePlaybackSettings
        {
            StartOffset = 5.5f,
            EndBlendDuration = 0.1f,
        };
        var transitionSettings = new GesturePreviewExportLoader.GesturePlaybackSettings
        {
            StartBlendDuration = 0.1f,
            EndBlendDuration = 0.1f,
        };
        var animations = new List<GesturePreviewExportLoader.GestureAnimationItem>
        {
            new()
            {
                GestureIndex = 0,
                Time = -1.5f,
                SlotOrder = 1,
                SlotName = "Gesture",
                AnimationExport = wanderSouth,
                Settings = incomingSettings,
            },
            new()
            {
                GestureIndex = 1,
                Time = 0,
                SlotOrder = 0,
                SlotName = "Pose",
                AnimationExport = wallLeanPose,
                Settings = transitionSettings,
            },
            new()
            {
                GestureIndex = 1,
                Time = 0,
                SlotOrder = 2,
                SlotName = "Transition",
                AnimationExport = wallLeanEnter,
                Settings = transitionSettings,
            },
        };

        List<AnimationPreviewControl.AnimationTimelineClip> timeline =
            BuildPlaybackTimelineWithBaseLayer(animations, playbackDuration: 5.467f);

        AnimationPreviewControl.AnimationTimelineClip incoming =
            timeline.Single(clip => clip.AnimationExport == wanderSouth);
        AnimationPreviewControl.AnimationTimelineClip transition =
            timeline.Single(clip => clip.AnimationExport == wallLeanEnter);
        Assert.AreEqual(-1.5f, incoming.StartTime, 0.0001f);
        Assert.AreEqual(1.333333f, incoming.EndTime, 0.0001f);
        Assert.AreEqual(5.5f, incoming.AnimationStartTime, 0.0001f);
        Assert.AreEqual(0, transition.StartTime, 0.0001f);
    }

    [TestMethod]
    public void MatchingStartingPoseContinuesTheInheritedConversationPhase()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("PoseContinuationTest.pcc", MEGame.LE3);
        ExportEntry wallLeanPose = CreateAnimation(package, "WallLeanPose", 5);
        float e22EndPhase = CurveEditor3D.ResolveGestureAnimationTime(5.467f, 0, 0, 5, 1, loop: true);

        float? r22StartPhase = CurveEditor3D.ResolveMatchingStartingPoseTime(wallLeanPose,
            startingPoseDuration: 5, inheritedAnimation: wallLeanPose,
            inheritedAnimationTime: e22EndPhase);

        Assert.AreEqual(0.467f, e22EndPhase, 0.0001f);
        Assert.IsTrue(r22StartPhase.HasValue);
        Assert.AreEqual(e22EndPhase, r22StartPhase.Value, 0.0001f);
        Assert.AreEqual(e22EndPhase, CurveEditor3D.ResolveMatchingStartingPoseTime(wallLeanPose,
            startingPoseDuration: 5, inheritedAnimation: wallLeanPose,
            inheritedAnimationTime: e22EndPhase));
    }

    [TestMethod]
    public void MatchingStartingPoseSurvivesAnimationPackageReload()
    {
        using IMEPackage firstPackage =
            MEPackageHandler.CreateMemoryEmptyPackage("ReloadedPoseSet.pcc", MEGame.LE3);
        using IMEPackage reopenedPackage =
            MEPackageHandler.CreateMemoryEmptyPackage("ReloadedPoseSet.pcc", MEGame.LE3);
        ExportEntry outgoingPose = CreateAnimation(firstPackage, "WallLeanPose", 5);
        ExportEntry incomingPose = CreateAnimation(reopenedPackage, "WallLeanPose", 5);

        Assert.AreNotSame(outgoingPose.FileRef, incomingPose.FileRef);
        Assert.AreEqual(outgoingPose.UIndex, incomingPose.UIndex);
        Assert.AreEqual(outgoingPose.FileRef.FilePath, incomingPose.FileRef.FilePath);
        Assert.AreEqual(0.467f, CurveEditor3D.ResolveMatchingStartingPoseTime(incomingPose,
            startingPoseDuration: 5, inheritedAnimation: outgoingPose,
            inheritedAnimationTime: 0.467f));
    }

    [TestMethod]
    public void ConversationStartingPoseWithoutGestureKeysUsesTheNodeDuration()
    {
        (float start, float end) = CurveEditor3D.ResolveStartingPoseTimelineRange(
            [], playbackDuration: 2.08f, animationStart: 0.5f, animationSequenceLength: 3);

        Assert.AreEqual(0, start, 0.0001f);
        Assert.AreEqual(2.08f, end, 0.0001f);
    }

    [TestMethod]
    public void StartingPoseWithoutGestureKeysFallsBackToItsRemainingAnimationLength()
    {
        (float start, float end) = CurveEditor3D.ResolveStartingPoseTimelineRange(
            [], playbackDuration: null, animationStart: 0.5f, animationSequenceLength: 3);

        Assert.AreEqual(0, start, 0.0001f);
        Assert.AreEqual(2.5f, end, 0.0001f);
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

    private static GesturePreviewExportLoader.GestureAnimationItem CreatePose(int index, float time,
        ExportEntry animation) => new()
    {
        GestureIndex = index,
        Time = time,
        SlotOrder = 0,
        SlotName = "Pose",
        SetName = "TestSet",
        AnimationName = animation.ObjectName,
        AnimationExport = animation,
        Settings = new GesturePreviewExportLoader.GesturePlaybackSettings
        {
            EndBlendDuration = 0.1f,
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

    private static List<AnimationPreviewControl.AnimationTimelineClip> BuildPlaybackTimelineWithBaseLayer(
        List<GesturePreviewExportLoader.GestureAnimationItem> animations, float playbackDuration)
    {
        MethodInfo method = typeof(GesturePreviewExportLoader).GetMethod("BuildPlaybackTimelineWithBaseLayer",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        return (List<AnimationPreviewControl.AnimationTimelineClip>)method.Invoke(null,
            [animations, false, playbackDuration]);
    }
}
