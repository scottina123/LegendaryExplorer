using System.Numerics;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MorphFace = LegendaryExplorerCore.Unreal.Classes.BioMorphFace;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class BioMorphFacePreviewTests
{
    [TestMethod]
    public void FinalSkeletonSkinningUsesOriginalBindPose()
    {
        MeshBone[] bindSkeleton =
        [
            new MeshBone
            {
                Name = "root",
                Orientation = Quaternion.Identity,
                Position = Vector3.Zero,
                ParentIndex = 0
            },
            new MeshBone
            {
                Name = "face",
                Orientation = Quaternion.Identity,
                Position = Vector3.UnitX,
                ParentIndex = 0
            }
        ];
        MeshBone[] finalSkeleton =
        [
            new MeshBone
            {
                Name = "root",
                Orientation = Quaternion.Identity,
                Position = Vector3.Zero,
                ParentIndex = 0
            },
            new MeshBone
            {
                Name = "face",
                Orientation = Quaternion.Identity,
                Position = Vector3.UnitX * 2,
                ParentIndex = 0
            }
        ];

        Matrix4x4[] matrices = MorphFace.ComputePreviewSkinningMatrices(bindSkeleton, finalSkeleton);
        Vector3 result = MorphFace.SkinPreviewPosition(
            new Vector3(5, 0, 0), matrices,
            1, 1f, 0, 0f, 0, 0f, 0, 0f);

        Assert.AreEqual(new Vector3(6, 0, 0), result);
    }

    [TestMethod]
    public void PreviewSkinningBlendsFourInfluences()
    {
        Matrix4x4[] matrices =
        [
            Matrix4x4.CreateTranslation(4, 0, 0),
            Matrix4x4.CreateTranslation(0, 4, 0),
            Matrix4x4.CreateTranslation(0, 0, 4),
            Matrix4x4.Identity
        ];

        Vector3 result = MorphFace.SkinPreviewPosition(
            Vector3.One, matrices,
            0, 0.25f, 1, 0.25f, 2, 0.25f, 3, 0.25f);

        Assert.AreEqual(new Vector3(2, 2, 2), result);
    }
}
