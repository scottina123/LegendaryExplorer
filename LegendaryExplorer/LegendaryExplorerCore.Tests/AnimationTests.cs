using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Animation;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class AnimationTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void ClearingAnimationRestoresBindPoseComponentTransforms()
    {
        var skeletalMesh = new SkeletalMesh
        {
            RefSkeleton =
            [
                new MeshBone
                {
                    Name = "root",
                    Orientation = Quaternion.Identity,
                    Position = new Vector3(10, 20, 30),
                    ParentIndex = 0,
                },
                new MeshBone
                {
                    Name = "child",
                    Orientation = Quaternion.Identity,
                    Position = new Vector3(4, 5, 6),
                    ParentIndex = 0,
                },
            ],
        };
        var player = new AnimSequencePlayer(skeletalMesh);

        Assert.AreEqual(new Vector3(10, 20, 30), player.BoneComponentSpaceTransforms[0].Translation);
        Assert.AreEqual(new Vector3(14, 25, 36), player.BoneComponentSpaceTransforms[1].Translation);

        player.BoneComponentSpaceTransforms[0] = Matrix4x4.CreateTranslation(100, 200, 300);
        player.BoneComponentSpaceTransforms[1] = Matrix4x4.CreateTranslation(400, 500, 600);
        player.SetAnimation(null);

        Assert.AreEqual(new Vector3(10, 20, 30), player.BoneComponentSpaceTransforms[0].Translation);
        Assert.AreEqual(new Vector3(14, 25, 36), player.BoneComponentSpaceTransforms[1].Translation);
    }

    [TestMethod]
    public void FaceFxAssetOwnedLineAdvancesWithoutAnAnimSet()
    {
        var skeletalMesh = new SkeletalMesh { RefSkeleton = [] };
        var line = new FaceFXLine
        {
            AnimationNames = [],
            NumKeys = [],
            Points =
            [
                new FaceFXControlPoint { time = 0 },
                new FaceFXControlPoint { time = 1 },
            ],
        };
        var asset = new FaceFXAsset
        {
            Names = [],
            RefBones = [],
            CompiledFaceGraph = [],
            Lines = [line],
        };
        var player = new FaceFxPlayer(skeletalMesh) { FxActor = asset };
        player.SetFaceFXLine(line);

        player.SetCurrentTime(0.5f);

        Assert.AreEqual(0.5f, player.CurrentTime);
    }

    [TestMethod]
    public void GestureMotionMaskPreservesContinuouslyEvaluatedBaseAnimation()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("GestureLayerTest.pcc", MEGame.LE3);
        var skeletalMesh = new SkeletalMesh
        {
            RefSkeleton =
            [
                CreateBone("root", 0),
                CreateBone("body", 0),
                CreateBone("head", 1),
            ],
        };
        AnimSequence baseAnimation = CreateAnimation(package, "Walk", ["root", "body", "head"],
        [
            CreateTrack(Quaternion.Identity),
            CreateTrack(Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)),
            CreateTrack(Quaternion.Identity),
        ]);
        AnimSequence gestureAnimation = CreateAnimation(package, "HeadGesture", ["root", "body", "head"],
        [
            CreateTrack(Quaternion.Identity),
            CreateTrack(Quaternion.Identity),
            CreateTrack(Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2)),
        ]);

        var basePlayer = new AnimSequencePlayer(skeletalMesh);
        basePlayer.SetAnimation(baseAnimation);
        basePlayer.SetCurrentTime(0.5f);
        basePlayer.ComputeSkinningMatrices();
        Matrix4x4 expectedBodyTransform = basePlayer.BoneComponentSpaceTransforms[1];

        var layeredPlayer = new AnimSequencePlayer(skeletalMesh);
        layeredPlayer.SetAnimationTimeline(
        [
            new AnimSequencePlayer.ScheduledAnimationClip
            {
                Animation = baseAnimation,
                StartTime = 0,
                EndTime = 1,
                AnimationStartTime = 0,
                AnimationEndTime = 1,
                IsBaseLayer = true,
            },
            new AnimSequencePlayer.ScheduledAnimationClip
            {
                Animation = gestureAnimation,
                StartTime = 0,
                EndTime = 1,
                AnimationStartTime = 0,
                AnimationEndTime = 1,
                UseMotionBoneMask = true,
            },
        ]);
        layeredPlayer.SetCurrentTime(0.5f);
        layeredPlayer.ComputeSkinningMatrices();

        AssertMatrixEqual(expectedBodyTransform, layeredPlayer.BoneComponentSpaceTransforms[1]);
        Assert.AreNotEqual(layeredPlayer.BoneComponentSpaceTransforms[1],
            layeredPlayer.BoneComponentSpaceTransforms[2]);
    }

    [TestMethod]
    public void ConversationTimelineExtractsRootTranslationAndKeepsSkeletalRootAnchored()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("RootNormalizationTest.pcc", MEGame.LE3);
        var skeletalMesh = new SkeletalMesh
        {
            RefSkeleton =
            [
                new MeshBone
                {
                    Name = "Master",
                    Orientation = Quaternion.Identity,
                    Position = Vector3.Zero,
                    ParentIndex = 0,
                },
                new MeshBone
                {
                    Name = "Root",
                    Orientation = Quaternion.Identity,
                    Position = new Vector3(10, 0, 0),
                    ParentIndex = 0,
                },
            ],
        };
        AnimSequence authoredPose = CreateAnimation(package, "AuthoredPose", ["Root"],
            [CreateTranslationTrack(new Vector3(150, 0, 0), new Vector3(170, 0, 0))], useTranslation: true);
        var player = new AnimSequencePlayer(skeletalMesh);
        player.SetAnimation(authoredPose);
        player.SetCurrentTime(0);
        player.ComputeSkinningMatrices();
        Assert.AreEqual(150, player.BoneComponentSpaceTransforms[1].Translation.X, 0.001f);
        player.SetAnimationTimeline(
        [
            new AnimSequencePlayer.ScheduledAnimationClip
            {
                Animation = authoredPose,
                StartTime = 0,
                EndTime = 1,
                AnimationStartTime = 0,
                AnimationEndTime = 1,
                IsBaseLayer = true,
                NormalizeRootTranslation = true,
            },
        ]);

        player.SetCurrentTime(0);
        player.ComputeSkinningMatrices();
        Assert.AreEqual(10, player.BoneComponentSpaceTransforms[1].Translation.X, 0.001f);
        Assert.AreEqual(0, player.ExtractedRootMotionTranslation.X, 0.001f);

        player.SetCurrentTime(0.5f);
        player.ComputeSkinningMatrices();
        Assert.AreEqual(10, player.BoneComponentSpaceTransforms[1].Translation.X, 0.001f);
        Assert.AreEqual(10, player.ExtractedRootMotionTranslation.X, 0.001f);
    }

    [TestMethod]
    public void ExtractedRootMotionAccumulatesAcrossSequentialGestureAnimations()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("RootAccumulationTest.pcc", MEGame.LE3);
        var skeletalMesh = new SkeletalMesh
        {
            RefSkeleton =
            [
                CreateBone("Master", 0),
                new MeshBone
                {
                    Name = "Root",
                    Orientation = Quaternion.Identity,
                    Position = new Vector3(10, 0, 0),
                    ParentIndex = 0,
                },
            ],
        };
        AnimSequence first = CreateAnimation(package, "FirstMove", ["Root"],
            [CreateTranslationTrack(new Vector3(100, 0, 0), new Vector3(120, 0, 0))], useTranslation: true);
        AnimSequence second = CreateAnimation(package, "SecondMove", ["Root"],
            [CreateTranslationTrack(new Vector3(500, 0, 0), new Vector3(530, 0, 0))], useTranslation: true);
        var player = new AnimSequencePlayer(skeletalMesh);
        player.SetAnimationTimeline(
        [
            new AnimSequencePlayer.ScheduledAnimationClip
            {
                Animation = first,
                StartTime = 0,
                EndTime = 1,
                AnimationStartTime = 0,
                AnimationEndTime = 1,
                NormalizeRootTranslation = true,
            },
            new AnimSequencePlayer.ScheduledAnimationClip
            {
                Animation = second,
                StartTime = 1,
                EndTime = 2,
                AnimationStartTime = 0,
                AnimationEndTime = 1,
                NormalizeRootTranslation = true,
            },
        ]);

        player.SetCurrentTime(0.5f);
        player.ComputeSkinningMatrices();
        Assert.AreEqual(10, player.ExtractedRootMotionTranslation.X, 0.001f);
        Assert.AreEqual(10, player.BoneComponentSpaceTransforms[1].Translation.X, 0.001f);

        player.SetCurrentTime(1.5f);
        player.ComputeSkinningMatrices();
        Assert.AreEqual(35, player.ExtractedRootMotionTranslation.X, 0.001f);
        Assert.AreEqual(10, player.BoneComponentSpaceTransforms[1].Translation.X, 0.001f);

        player.SetCurrentTime(2);
        player.ComputeSkinningMatrices();
        Assert.AreEqual(50, player.ExtractedRootMotionTranslation.X, 0.001f);
        Assert.AreEqual(10, player.BoneComponentSpaceTransforms[1].Translation.X, 0.001f);
    }

    [TestMethod]
    public void NewRootMovingGestureTakesOwnershipWithoutAddingOverlappingRootCurves()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("RootOwnershipTest.pcc", MEGame.LE3);
        var skeletalMesh = new SkeletalMesh
        {
            RefSkeleton =
            [
                CreateBone("Master", 0),
                new MeshBone
                {
                    Name = "Root",
                    Orientation = Quaternion.Identity,
                    Position = new Vector3(10, 0, 0),
                    ParentIndex = 0,
                },
            ],
        };
        AnimSequence first = CreateAnimation(package, "FirstMove", ["Root"],
            [CreateTranslationTrack(new Vector3(100, 0, 0), new Vector3(140, 0, 0))], useTranslation: true);
        AnimSequence second = CreateAnimation(package, "SecondMove", ["Root"],
            [CreateTranslationTrack(new Vector3(500, 0, 0), new Vector3(530, 0, 0))], useTranslation: true);
        var player = new AnimSequencePlayer(skeletalMesh);
        player.SetAnimationTimeline(
        [
            new AnimSequencePlayer.ScheduledAnimationClip
            {
                Animation = first,
                StartTime = 0,
                EndTime = 2,
                AnimationStartTime = 0,
                AnimationEndTime = 1,
                PlayRate = 0.5f,
                NormalizeRootTranslation = true,
            },
            new AnimSequencePlayer.ScheduledAnimationClip
            {
                Animation = second,
                StartTime = 0.5f,
                EndTime = 1.5f,
                AnimationStartTime = 0,
                AnimationEndTime = 1,
                NormalizeRootTranslation = true,
            },
        ]);

        player.SetCurrentTime(1);
        player.ComputeSkinningMatrices();

        // The first clip contributes only the ten units travelled before the handoff. The second
        // contributes fifteen units after it. Adding both overlapping paths would incorrectly be 35.
        Assert.AreEqual(25, player.ExtractedRootMotionTranslation.X, 0.001f);
    }

    [TestMethod]
    public void ConstantRootGestureDoesNotInterruptMovingRootOwnership()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("ConstantRootOverlayTest.pcc", MEGame.LE3);
        var skeletalMesh = new SkeletalMesh
        {
            RefSkeleton =
            [
                CreateBone("Master", 0),
                new MeshBone
                {
                    Name = "Root",
                    Orientation = Quaternion.Identity,
                    Position = new Vector3(10, 0, 0),
                    ParentIndex = 0,
                },
            ],
        };
        AnimSequence locomotion = CreateAnimation(package, "Locomotion", ["Root"],
            [CreateTranslationTrack(new Vector3(100, 0, 0), new Vector3(120, 0, 0))], useTranslation: true);
        AnimSequence poseOverlay = CreateAnimation(package, "PoseOverlay", ["Root"],
            [CreateTranslationTrack(new Vector3(500, 0, 0), new Vector3(500, 0, 0))], useTranslation: true);
        var player = new AnimSequencePlayer(skeletalMesh);
        player.SetAnimationTimeline(
        [
            new AnimSequencePlayer.ScheduledAnimationClip
            {
                Animation = locomotion,
                StartTime = 0,
                EndTime = 1,
                AnimationStartTime = 0,
                AnimationEndTime = 1,
                NormalizeRootTranslation = true,
            },
            new AnimSequencePlayer.ScheduledAnimationClip
            {
                Animation = poseOverlay,
                StartTime = 0.25f,
                EndTime = 1,
                AnimationStartTime = 0,
                AnimationEndTime = 1,
                NormalizeRootTranslation = true,
            },
        ]);

        player.SetCurrentTime(0.75f);
        player.ComputeSkinningMatrices();

        Assert.AreEqual(15, player.ExtractedRootMotionTranslation.X, 0.001f);
    }

    [TestMethod]
    public void SubUnitRootJitterDoesNotInterruptMovingRootOwnership()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("RootJitterOverlayTest.pcc", MEGame.LE3);
        var skeletalMesh = new SkeletalMesh
        {
            RefSkeleton =
            [
                CreateBone("Master", 0),
                new MeshBone
                {
                    Name = "Root",
                    Orientation = Quaternion.Identity,
                    Position = new Vector3(10, 0, 0),
                    ParentIndex = 0,
                },
            ],
        };
        AnimSequence locomotion = CreateAnimation(package, "Locomotion", ["Root"],
            [CreateTranslationTrack(new Vector3(100, 0, 0), new Vector3(120, 0, 0))], useTranslation: true);
        AnimSequence poseOverlay = CreateAnimation(package, "PoseWithRootJitter", ["Root"],
            [CreateTranslationTrack(new Vector3(500, 0, 0), new Vector3(500.4f, 0.2f, 0))], useTranslation: true);
        var player = new AnimSequencePlayer(skeletalMesh);
        player.SetAnimationTimeline(
        [
            new AnimSequencePlayer.ScheduledAnimationClip
            {
                Animation = locomotion,
                StartTime = 0,
                EndTime = 1,
                AnimationStartTime = 0,
                AnimationEndTime = 1,
                NormalizeRootTranslation = true,
            },
            new AnimSequencePlayer.ScheduledAnimationClip
            {
                Animation = poseOverlay,
                StartTime = 0.25f,
                EndTime = 1,
                AnimationStartTime = 0,
                AnimationEndTime = 1,
                NormalizeRootTranslation = true,
            },
        ]);

        player.SetCurrentTime(0.75f);
        player.ComputeSkinningMatrices();

        Assert.AreEqual(15, player.ExtractedRootMotionTranslation.X, 0.001f);

        var skeletalRootPlayer = new AnimSequencePlayer(skeletalMesh);
        skeletalRootPlayer.SetAnimationTimeline(
        [
            new AnimSequencePlayer.ScheduledAnimationClip
            {
                Animation = locomotion,
                StartTime = 0,
                EndTime = 1,
                AnimationStartTime = 0,
                AnimationEndTime = 1,
            },
            new AnimSequencePlayer.ScheduledAnimationClip
            {
                Animation = poseOverlay,
                StartTime = 0.25f,
                EndTime = 1,
                AnimationStartTime = 0,
                AnimationEndTime = 1,
            },
        ]);

        skeletalRootPlayer.SetCurrentTime(0.75f);
        skeletalRootPlayer.ComputeSkinningMatrices();

        Assert.AreEqual(115, skeletalRootPlayer.BoneComponentSpaceTransforms[1].Translation.X, 0.001f);
    }

    private static MeshBone CreateBone(string name, int parentIndex) => new()
    {
        Name = name,
        Orientation = Quaternion.Identity,
        Position = Vector3.Zero,
        ParentIndex = parentIndex,
    };

    private static AnimTrack CreateTrack(params Quaternion[] rotations) => new()
    {
        Positions = [Vector3.Zero],
        Rotations = [.. rotations],
    };

    private static AnimTrack CreateTranslationTrack(params Vector3[] positions) => new()
    {
        Positions = [.. positions],
        Rotations = [Quaternion.Identity],
    };

    private static AnimSequence CreateAnimation(IMEPackage package, string name, List<string> bones,
        List<AnimTrack> tracks, bool useTranslation = false)
    {
        ExportEntry export = package.CreateExport(name, "AnimSequence", indexed: false);
        if (useTranslation)
        {
            ExportEntry animSetData = package.CreateExport($"{name}_Data", "BioAnimSetData", indexed: false);
            animSetData.WriteProperty(new BoolProperty(false, "bAnimRotationOnly"));
            export.WriteProperty(new ObjectProperty(animSetData, "m_pBioAnimSetData"));
        }
        return new AnimSequence
        {
            Export = export,
            Name = name,
            Bones = bones,
            RawAnimationData = tracks,
            CompressedAnimationData = [],
            NumFrames = 2,
            SequenceLength = 1,
            RateScale = 1,
        };
    }

    private static void AssertMatrixEqual(Matrix4x4 expected, Matrix4x4 actual)
    {
        Assert.AreEqual(expected.M11, actual.M11, 0.0001f);
        Assert.AreEqual(expected.M12, actual.M12, 0.0001f);
        Assert.AreEqual(expected.M13, actual.M13, 0.0001f);
        Assert.AreEqual(expected.M21, actual.M21, 0.0001f);
        Assert.AreEqual(expected.M22, actual.M22, 0.0001f);
        Assert.AreEqual(expected.M23, actual.M23, 0.0001f);
        Assert.AreEqual(expected.M31, actual.M31, 0.0001f);
        Assert.AreEqual(expected.M32, actual.M32, 0.0001f);
        Assert.AreEqual(expected.M33, actual.M33, 0.0001f);
    }
}
