using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.Animation;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class MorphFaceFxPlayerTests
{
    [TestMethod]
    public void FacialMotionPreservesEditedSkeletonAndRemovesRigReferencePose()
    {
        var (bind, edited, asset, line) = CreateAnimation();
        var player = new MorphFaceFxPlayer(bind, edited, asset, null, line);
        player.SetCurrentTime(0);
        // The asset's jaw reference is at X=10; the edited morph's jaw is at X=3.
        Assert.AreEqual(3f, player.ComputeSkinningMatrices()[1].Translation.X + 1, 0.0001f);
        player.SetCurrentTime(1);
        Assert.AreEqual(5f, player.ComputeSkinningMatrices()[1].Translation.X + 1, 0.0001f);
        // Live edits remain visible during playback without changing the original bind skeleton.
        edited[1].Position = new Vector3(5, 0, 0);
        Assert.AreEqual(7f, player.ComputeSkinningMatrices()[1].Translation.X + 1, 0.0001f);
        Assert.AreEqual(Vector3.UnitX, bind[1].Position);
    }

    [TestMethod]
    public void EmptyLineHasZeroDurationAndKeepsMorphPose()
    {
        var (bind, edited, asset, line) = CreateAnimation();
        line.AnimationNames.Clear();
        line.Points.Clear();
        line.NumKeys.Clear();
        var player = new MorphFaceFxPlayer(bind, edited, asset, null, line);
        Assert.AreEqual(0f, player.Duration);
        Assert.AreEqual(0f, player.StartTime);
        Assert.AreEqual(2f, player.ComputeSkinningMatrices()[1].Translation.X, 0.0001f);
    }

    [TestMethod]
    public void AnimSetNamesAndNegativePrerollAreRespected()
    {
        var (bind, edited, asset, line) = CreateAnimation();
        line.AnimationNames = [0];
        line.Points[0] = new FaceFXControlPoint { time = -0.5f, weight = 0 };
        var set = new FaceFXAnimSet { Names = ["open"], Lines = [line] };
        asset.Lines.Clear();
        var player = new MorphFaceFxPlayer(bind, edited, asset, set, line);
        Assert.AreEqual(-0.5f, player.StartTime);
        Assert.AreEqual(1.5f, player.Duration);
        player.SetCurrentTime(1);
        Assert.AreEqual(4f, player.ComputeSkinningMatrices()[1].Translation.X, 0.0001f);
    }

    private static (MeshBone[] Bind, MeshBone[] Edited, FaceFXAsset Asset, FaceFXLine Line) CreateAnimation()
    {
        MeshBone[] bind =
        [
            new() { Name = "root", Orientation = Quaternion.Identity, ParentIndex = 0 },
            new() { Name = "jaw", Orientation = Quaternion.Identity, Position = Vector3.UnitX, ParentIndex = 0 }
        ];
        MeshBone[] edited =
        [
            new() { Name = "root", Orientation = Quaternion.Identity, ParentIndex = 0 },
            new() { Name = "jaw", Orientation = Quaternion.Identity, Position = new Vector3(3, 0, 0), ParentIndex = 0 }
        ];
        var asset = FaceFXAsset.Create(MEGame.LE3);
        asset.Names = ["jaw", "open"];
        asset.CompiledFaceGraph =
        [
            new() { Name = 1, NodeType = FxNodeType.BonePose, MinVal = 0, MaxVal = 1,
                InputOperation = FxInputOperation.Sum, InputLinks = [], UserProperties = [] }
        ];
        asset.RefBones =
        [
            new() { RefBone = new FaceFxBone { BoneName = 0, Position = new Vector3(10, 0, 0), Rotation = Quaternion.Identity },
                RefBoneInverseRot = Quaternion.Identity,
                Links = [new FaceFxBoneLink { GraphIndex = 0,
                    OptimizedBone = new FaceFxBone { Position = new Vector3(2, 0, 0), Rotation = Quaternion.Identity } }] }
        ];
        var line = new FaceFXLine { AnimationNames = [1], NumKeys = [2],
            Points = [new() { time = 0, weight = 0 }, new() { time = 1, weight = 1 }] };
        asset.Lines = [line];
        return (bind, edited, asset, line);
    }
}
