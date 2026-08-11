using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
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

    private static AnimSequence CreateAnimation(IMEPackage package, string name, List<string> bones,
        List<AnimTrack> tracks)
    {
        ExportEntry export = package.CreateExport(name, "AnimSequence", indexed: false);
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
