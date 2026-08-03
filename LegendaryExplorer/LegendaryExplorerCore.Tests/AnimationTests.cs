using System.Numerics;
using LegendaryExplorerCore.Unreal.Animation;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class AnimationTests
{
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
}
