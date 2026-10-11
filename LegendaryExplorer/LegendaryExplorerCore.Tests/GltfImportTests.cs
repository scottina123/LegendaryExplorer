using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using ModelRoot = SharpGLTF.Schema2.ModelRoot;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.IO;
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

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(1, true)]
    public void GeneratesMissingTangentsInEveryPrimitiveWhilePreservingAuthoredTangents(int missingPrimitive, bool omitUVs)
    {
        var scene = new SceneBuilder();
        var mesh = new MeshBuilder<VertexPositionNormalTangent, VertexTexture1, VertexEmpty>("MixedTangents");
        for (int i = 0; i < 2; i++)
        {
            var primitive = mesh.UsePrimitive(new MaterialBuilder($"Material{i}"));
            VertexBuilder<VertexPositionNormalTangent, VertexTexture1, VertexEmpty> Vertex(Vector3 position, Vector2 uv) =>
                new VertexBuilder<VertexPositionNormalTangent, VertexTexture1, VertexEmpty>()
                    .WithGeometry(position, Vector3.UnitZ, new Vector4(Vector3.UnitY, 1))
                    .WithMaterial(uv);
            primitive.AddTriangle(
                Vertex(new Vector3(i * 2, 0, 0), Vector2.Zero),
                Vertex(new Vector3(i * 2 + 1, 0, 0), Vector2.UnitX),
                Vertex(new Vector3(i * 2, 1, 0), Vector2.UnitY));
        }
        scene.AddRigidMesh(mesh, new NodeBuilder("MixedTangents"));
        var gltf = scene.ToGltf2();
        using var originalPackage = MEPackageHandler.CreateMemoryEmptyPackage("AuthoredTangents.pcc", MEGame.LE3);
        GLTF.ConvertGltfToMesh(gltf, originalPackage);
        var originalMesh = originalPackage.Exports.Single(export => export.ClassName == "StaticMesh").GetBinaryData<StaticMesh>();
        gltf.LogicalMeshes[0].Primitives[missingPrimitive].SetVertexAccessor("TANGENT", null);
        if (omitUVs)
            gltf.LogicalMeshes[0].Primitives[missingPrimitive].SetVertexAccessor("TEXCOORD_0", null);

        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MixedTangents.pcc", MEGame.LE3);
        GLTF.ConvertGltfToMesh(gltf, package);

        var imported = package.Exports.Single(export => export.ClassName == "StaticMesh").GetBinaryData<StaticMesh>();
        Assert.AreEqual((uint)6, imported.LODModels[0].NumVertices);
        var vertices = imported.LODModels[0].VertexBuffer.VertexData;
        Assert.IsTrue(vertices.All(vertex => ((Vector3)vertex.TangentX).LengthSquared() > 0.9f));
        int preservedStart = (1 - missingPrimitive) * 3;
        for (int i = preservedStart; i < preservedStart + 3; i++)
        {
            Assert.AreEqual(originalMesh.LODModels[0].VertexBuffer.VertexData[i].TangentX, vertices[i].TangentX);
            Assert.AreEqual(originalMesh.LODModels[0].VertexBuffer.VertexData[i].TangentZ, vertices[i].TangentZ);
        }
    }

    [TestMethod]
    public void SkeletalMaterialFallbackPreservesExplicitEmptySlots()
    {
        var scene = new SceneBuilder();
        var bone = new NodeBuilder("RootBone");
        var mesh = new MeshBuilder<VertexPositionNormalTangent, VertexTexture1, VertexJoints4>("SkinnedMaterials");
        for (int i = 0; i < 2; i++)
        {
            VertexBuilder<VertexPositionNormalTangent, VertexTexture1, VertexJoints4> Vertex(Vector3 position) =>
                new VertexBuilder<VertexPositionNormalTangent, VertexTexture1, VertexJoints4>()
                    .WithGeometry(position, Vector3.UnitZ, new Vector4(Vector3.UnitX, 1))
                    .WithMaterial(Vector2.Zero)
                    .WithSkinning(new[] { (0, 1f) });
            mesh.UsePrimitive(new MaterialBuilder($"ExistingMaterial{i}")).AddTriangle(
                Vertex(new Vector3(i * 2, 0, 0)),
                Vertex(new Vector3(i * 2 + 1, 0, 0)),
                Vertex(new Vector3(i * 2, 1, 0)));
        }
        scene.AddSkinnedMesh(mesh, Matrix4x4.Identity, bone);
        var gltf = scene.ToGltf2();
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("SkeletalMaterialFallback.pcc", MEGame.LE3);
        for (int i = 0; i < 2; i++)
            package.CreateExport($"ExistingMaterial{i}", "Material", indexed: false).WriteBinary(Material.Create());
        GLTF.ConvertGltfToMesh(gltf, package);
        var existing = package.Exports.Single(export => export.ClassName == "SkeletalMesh");
        int preservedMaterial = existing.GetBinaryData<SkeletalMesh>().Materials[0];
        gltf.LogicalMaterials[0].Name = "UnresolvedMaterial";
        gltf.LogicalMaterials[1].Name = "null";

        GLTF.ConvertGltfToMesh(gltf, package, existingMesh: existing);

        CollectionAssert.AreEqual(new[] { preservedMaterial, 0 }, existing.GetBinaryData<SkeletalMesh>().Materials);
    }

    [TestMethod]
    public void ExportedNormalTexturesHandleChannelEndpointsAndOutOfRangeXY()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ExportNormals.pcc", MEGame.LE3);
        var texture = package.CreateExport("Material_Norm", "Texture2D", indexed: false);
        texture.WriteProperties(new PropertyCollection
        {
            new IntProperty(2, "SizeX"), new IntProperty(2, "SizeY"),
            new EnumProperty("PF_A8R8G8B8", "EPixelFormat", MEGame.LE3, "Format")
        });
        var textureBinary = UTexture2D.Create();
        textureBinary.Mips.Add(new UTexture2D.Texture2DMipMap(
            new byte[] { 0, 0, 128, 255, 0, 128, 128, 255, 0, 255, 255, 255, 0, 128, 0, 255 }, 2, 2));
        texture.WriteBinary(textureBinary);
        var material = package.CreateExport("NormalMaterial", "Material", indexed: false);
        var materialBinary = Material.Create();
        materialBinary.SM3MaterialResource.UniformExpressionTextures = [texture.UIndex];
        material.WriteBinary(materialBinary);
        var scene = new SceneBuilder();
        var root = new NodeBuilder("Root");
        scene.AddNode(root);
        AddTrianglePart(scene, root, "Mesh", "NormalMaterial", 0);
        GLTF.ConvertGltfToMesh(scene.ToGltf2(), package);
        string filePath = Path.Combine(Path.GetTempPath(), $"ExportNormals-{Guid.NewGuid()}.glb");
        try
        {
            GLTF.ExportMeshToGltf(package.Exports.Single(export => export.ClassName == "StaticMesh"), filePath);
            var exported = ModelRoot.Load(filePath);
            using var pixels = SixLabors.ImageSharp.Image.Load<Rgba32>(exported.LogicalImages.Single().Content.Content.Span);
            Assert.AreEqual((byte)255, pixels[0, 0].G, "Inverting a zero channel must not wrap back to zero.");
            Assert.AreEqual((byte)255, pixels[1, 0].B, "A neutral XY normal must reconstruct a forward Z component.");
            Assert.AreEqual((byte)128, pixels[0, 1].B, "Out-of-range XY components must clamp Z to zero.");
        }
        finally
        {
            File.Delete(filePath);
        }
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
