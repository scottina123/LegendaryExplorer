using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class GltfImportTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void MultipleStaticMeshNodesImportAsOneMeshWithMultipleSections()
    {
        var scene = new SceneBuilder();
        var root = new NodeBuilder("Root");
        scene.AddNode(root);
        AddTrianglePart(scene, root, "Body", "BodyMaterial", 0);
        AddTrianglePart(scene, root, "Body", "BodyDetailMaterial", 2);
        AddTrianglePart(scene, root, "Jacket", "JacketMaterial", 4);

        var gltf = scene.ToGltf2();
        GLTF.QueryMeshes(gltf, out var skeletalMeshes, out var staticMeshes);
        Assert.AreEqual(0, skeletalMeshes.Count());
        Assert.AreEqual(1, staticMeshes.Count());

        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("CombinedGltfTest.pcc", MEGame.LE3);
        GLTF.ConvertGltfToMesh(gltf, package, combinedMeshName: "CombinedMesh");

        ExportEntry[] meshExports = [.. package.Exports.Where(export => export.ClassName == "StaticMesh")];
        Assert.AreEqual(1, meshExports.Length);
        Assert.AreEqual("CombinedMesh", meshExports[0].ObjectName.Name);

        StaticMesh mesh = meshExports[0].GetBinaryData<StaticMesh>();
        Assert.AreEqual(1, mesh.LODModels.Length);
        Assert.AreEqual((uint)9, mesh.LODModels[0].NumVertices);
        Assert.AreEqual(3, mesh.LODModels[0].Elements.Length);
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, mesh.LODModels[0].Elements.Select(element => element.MaterialIndex).ToArray());
        CollectionAssert.AreEquivalent(new ushort[] { 0, 1, 2 },
            mesh.kDOPTreeME3UDKLE.Triangles.Select(triangle => triangle.MaterialIndex).ToArray());
    }

    [TestMethod]
    public void HeadAndEyelashPartsCanBeExcludedAndSharedMaterialSectionsAreMerged()
    {
        var scene = new SceneBuilder();
        var root = new NodeBuilder("Root");
        scene.AddNode(root);
        AddTrianglePart(scene, root, "Body", "BodyMaterial", 0);
        AddTrianglePart(scene, root, "BodyPanel", "BodyMaterial", 1);
        AddTrianglePart(scene, root, "CC_Base_Body1_Std_Skin_Head_0_0", "HeadMaterial", 2);
        AddTrianglePart(scene, root, "CC_Base_Body1_Std_Eyelash_0_0", "EyelashMaterial", 3);
        AddTrianglePart(scene, root, "Headgear", "HeadgearMaterial", 4);

        var gltf = scene.ToGltf2();
        CollectionAssert.AreEqual(new[]
        {
            "CC_Base_Body1_Std_Skin_Head_0_0",
            "CC_Base_Body1_Std_Eyelash_0_0"
        }, GLTF.GetHeadRelatedMeshPartNames(gltf).ToArray());

        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("HeadlessGltfTest.pcc", MEGame.LE3);
        GLTF.ConvertGltfToMesh(gltf, package, combinedMeshName: "BodyWithoutHead", includeHeadMeshes: false);

        ExportEntry meshExport = package.Exports.Single(export => export.ClassName == "StaticMesh");
        StaticMesh mesh = meshExport.GetBinaryData<StaticMesh>();
        Assert.AreEqual((uint)9, mesh.LODModels[0].NumVertices);
        Assert.AreEqual(2, mesh.LODModels[0].Elements.Length);
        Assert.AreEqual((uint)2, mesh.LODModels[0].Elements[0].NumTriangles);
        CollectionAssert.AreEqual(new[] { 0, 1 },
            mesh.LODModels[0].Elements.Select(element => element.MaterialIndex).ToArray());
    }

    private static void AddTrianglePart(SceneBuilder scene, NodeBuilder root, string meshName, string materialName, float xOffset)
    {
        var material = new MaterialBuilder(materialName);
        var mesh = new MeshBuilder<VertexPositionNormalTangent, VertexTexture1, VertexEmpty>(meshName);
        var primitive = mesh.UsePrimitive(material);
        primitive.AddTriangle(
            CreateVertex(new Vector3(xOffset, 0, 0)),
            CreateVertex(new Vector3(xOffset + 1, 0, 0)),
            CreateVertex(new Vector3(xOffset, 1, 0)));

        var node = new NodeBuilder(meshName);
        root.AddNode(node);
        scene.AddRigidMesh(mesh, node).WithName(meshName);
    }

    private static VertexBuilder<VertexPositionNormalTangent, VertexTexture1, VertexEmpty> CreateVertex(Vector3 position) =>
        new VertexBuilder<VertexPositionNormalTangent, VertexTexture1, VertexEmpty>()
            .WithGeometry(position, Vector3.UnitZ, new Vector4(Vector3.UnitX, 1))
            .WithMaterial(Vector2.Zero);
}
