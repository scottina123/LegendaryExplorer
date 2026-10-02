using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class MeshMaterialCloneTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow(MEGame.ME1, false)]
    [DataRow(MEGame.ME2, false)]
    [DataRow(MEGame.ME3, false)]
    [DataRow(MEGame.LE1, false)]
    [DataRow(MEGame.LE2, false)]
    [DataRow(MEGame.LE3, false)]
    [DataRow(MEGame.ME1, true)]
    [DataRow(MEGame.ME2, true)]
    [DataRow(MEGame.ME3, true)]
    [DataRow(MEGame.LE1, true)]
    [DataRow(MEGame.LE2, true)]
    [DataRow(MEGame.LE3, true)]
    public void MeshClonesRelinkEverySlotAndSharedMaterialDependenciesWithoutChangingGeometry(MEGame game, bool isStatic)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CloneMeshDependencies.pcc", game);
        var texture = CreateTexture(package, "SharedTexture");
        var parentTexture = CreateTexture(package, "ParentTexture");
        var baseMaterial = package.CreateExport("BaseMaterial", "Material", indexed: false);
        var expression = package.CreateExport("TextureExpression", "MaterialExpressionTextureSample", baseMaterial, indexed: false);
        expression.WriteProperty(new ObjectProperty(texture, "Texture"));
        var baseProperties = new PropertyCollection
        {
            new ArrayProperty<ObjectProperty>(new[] { new ObjectProperty(expression) }, "Expressions")
        };
        if (game is MEGame.ME1 or MEGame.ME2)
        {
            baseProperties.Add(new ArrayProperty<ObjectProperty>(new[] { new ObjectProperty(parentTexture) }, "ReferencedTextures"));
        }
        baseMaterial.WriteProperties(baseProperties);
        var baseBinary = Material.Create();
        SetTextureReferences(baseBinary.SM3MaterialResource, texture.UIndex, parentTexture.UIndex);
        SetTextureReferences(baseBinary.SM2MaterialResource, texture.UIndex, parentTexture.UIndex);
        baseMaterial.WriteBinary(baseBinary);
        var parentMaterial = CreateMaterial(package, "ParentMaterial", texture.UIndex);
        parentMaterial.WriteProperty(new ObjectProperty(baseMaterial, "Parent"));
        var firstMaterial = CreateMaterial(package, "FirstMaterial", texture.UIndex, texture.UIndex);
        firstMaterial.ObjectName = new NameReference("FirstMaterial", 5);
        firstMaterial.WriteProperty(new ObjectProperty(parentMaterial, "Parent"));
        firstMaterial.WriteProperty(new BoolProperty(true, "bHasStaticPermutationResource"));
        var instanceBinary = MaterialInstance.Create();
        SetTextureReferences(instanceBinary.SM3StaticPermutationResource, texture.UIndex, parentTexture.UIndex);
        SetTextureReferences(instanceBinary.SM2StaticPermutationResource, texture.UIndex, parentTexture.UIndex);
        firstMaterial.WriteBinary(instanceBinary);
        var secondMaterial = CreateMaterial(package, "SecondMaterial", texture.UIndex);
        secondMaterial.WriteProperty(new ObjectProperty(baseMaterial, "Parent"));
        var unrelated = package.CreateExport("SharedNonMaterial", "Object", indexed: false);
        var mesh = CreateMesh(package, "Mesh", isStatic, [firstMaterial.UIndex, secondMaterial.UIndex, firstMaterial.UIndex],
            [secondMaterial.UIndex, firstMaterial.UIndex], unrelated.UIndex);
        mesh.ObjectName = new NameReference("Mesh", 7);
        CreateMesh(package, "OtherMesh", isStatic, [firstMaterial.UIndex, secondMaterial.UIndex], [], unrelated.UIndex);
        var originalData = Snapshot(package);
        byte[] originalGeometry = GeometryBytes(mesh);
        int originalExportCount = package.ExportCount;
        var info = EntryCloner.GetMeshMaterialCloneInfo(mesh);
        Assert.AreEqual(isStatic ? 5 : 3, info.MaterialSlotCount);
        CollectionAssert.AreEquivalent(new IEntry[] { firstMaterial, secondMaterial, parentMaterial, baseMaterial }, info.Materials.ToArray());
        CollectionAssert.AreEquivalent(new IEntry[] { texture, parentTexture }, info.Textures.ToArray());
        CollectionAssert.AreEqual(new IEntry[] { expression }, info.Expressions.ToArray());
        Assert.AreEqual(originalExportCount, package.ExportCount, "Inspecting clone options must leave the package unchanged.");

        var clone = EntryCloner.CloneMesh(mesh, "custom", cloneMaterialsAndTextures: true);
        int[] slots = MaterialIndices(clone);
        var clonedFirst = package.GetUExport(slots[0]);
        var clonedSecond = package.GetUExport(slots[1]);
        var clonedParent = package.GetUExport(clonedFirst.GetProperty<ObjectProperty>("Parent").Value);
        var clonedBase = package.GetUExport(clonedParent.GetProperty<ObjectProperty>("Parent").Value);
        int clonedTextureIndex = ParameterIndices(clonedFirst)[0];
        var clonedTexture = package.GetUExport(clonedTextureIndex);
        var clonedBaseBinary = clonedBase.GetBinaryData<Material>();
        int clonedParentTextureIndex = clonedBaseBinary.SM3MaterialResource.UniformExpressionTextures[1];

        Assert.AreEqual(originalExportCount + 8, package.ExportCount,
            "Clone one mesh, four materials, two shared textures, and one material expression.");
        Assert.AreEqual(new NameReference("Mesh_custom", 0), clone.ObjectName);
        Assert.AreEqual(new NameReference("FirstMaterial_custom", 0), clonedFirst.ObjectName);
        Assert.AreEqual(new NameReference("SecondMaterial_custom", 0), clonedSecond.ObjectName);
        Assert.AreEqual(new NameReference("ParentMaterial_custom", 0), clonedParent.ObjectName);
        Assert.AreEqual(new NameReference("BaseMaterial_custom", 0), clonedBase.ObjectName);
        Assert.AreNotEqual(firstMaterial.UIndex, slots[0]);
        Assert.AreNotEqual(secondMaterial.UIndex, slots[1]);
        CollectionAssert.AreEqual(isStatic
            ? new[] { clonedFirst.UIndex, clonedSecond.UIndex, clonedFirst.UIndex, clonedSecond.UIndex, clonedFirst.UIndex }
            : new[] { clonedFirst.UIndex, clonedSecond.UIndex, clonedFirst.UIndex }, slots);
        Assert.AreEqual(clonedBase.UIndex, clonedSecond.GetProperty<ObjectProperty>("Parent").Value);
        CollectionAssert.AreEqual(new[] { clonedTextureIndex, clonedTextureIndex }, ParameterIndices(clonedFirst));
        CollectionAssert.AreEqual(new[] { clonedTextureIndex }, ParameterIndices(clonedSecond));
        CollectionAssert.AreEqual(new[] { clonedTextureIndex }, ParameterIndices(clonedParent));
        Assert.AreNotEqual(texture.UIndex, clonedTextureIndex);
        Assert.AreNotEqual(parentTexture.UIndex, clonedParentTextureIndex);
        if (game is MEGame.ME1 or MEGame.ME2)
        {
            Assert.AreEqual(clonedParentTextureIndex, clonedBase.GetProperty<ArrayProperty<ObjectProperty>>("ReferencedTextures").Single().Value);
        }
        int clonedExpressionIndex = clonedBase.GetProperty<ArrayProperty<ObjectProperty>>("Expressions").Single().Value;
        var clonedExpression = package.GetUExport(clonedExpressionIndex);
        Assert.AreNotEqual(expression.UIndex, clonedExpressionIndex);
        Assert.AreEqual(clonedBase.UIndex, clonedExpression.idxLink);
        Assert.AreEqual(clonedTextureIndex, clonedExpression.GetProperty<ObjectProperty>("Texture").Value);
        var clonedInstanceBinary = clonedFirst.GetBinaryData<MaterialInstance>();
        foreach (var resource in new[]
        {
            clonedBaseBinary.SM3MaterialResource, clonedBaseBinary.SM2MaterialResource,
            clonedInstanceBinary.SM3StaticPermutationResource, clonedInstanceBinary.SM2StaticPermutationResource
        })
        {
            CollectionAssert.AreEqual(new[] { clonedTextureIndex, clonedParentTextureIndex }, resource.UniformExpressionTextures);
            CollectionAssert.AreEqual(new[] { clonedTextureIndex, clonedParentTextureIndex }, resource.TextureDependencyLengthMap.Select(pair => pair.Key).ToArray());
        }
        CollectionAssert.AreEqual(originalGeometry, GeometryBytes(clone));
        AssertOriginalsUnchanged(originalData);
        Assert.AreEqual(new NameReference("Mesh", 7), mesh.ObjectName);
        Assert.AreEqual(new NameReference("FirstMaterial", 5), firstMaterial.ObjectName);

        using var saved = package.SaveToStream(false);
        saved.Position = 0;
        using var reopened = MEPackageHandler.OpenMEPackageFromStream(saved);
        var reopenedClone = reopened.GetUExport(clone.UIndex);
        CollectionAssert.AreEqual(slots, MaterialIndices(reopenedClone));
        CollectionAssert.AreEqual(originalGeometry, GeometryBytes(reopenedClone));
        CollectionAssert.AreEqual(ParameterIndices(clonedFirst), ParameterIndices(reopened.GetUExport(clonedFirst.UIndex)));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, reopened.GetUExport(clonedTextureIndex).GetBinaryData<UTexture2D>().Mips.Single().Mip);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RepeatedMeshClonesGetIndependentMaterialsAndTexturesWithIndexZeroNames(bool isStatic)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CloneMeshNames.pcc", MEGame.LE3);
        var texture = CreateTexture(package, "Texture");
        var material = CreateMaterial(package, "Material", texture.UIndex);
        var mesh = CreateMesh(package, "Mesh", isStatic, [material.UIndex], [material.UIndex]);
        mesh.ObjectName = new NameReference("Mesh", 9);
        material.ObjectName = new NameReference("Material", 4);
        CreateMesh(package, "Mesh_custom", isStatic, [material.UIndex], []);
        CreateMaterial(package, "Material_custom", texture.UIndex);
        CreateTexture(package, "Texture_custom");
        var originals = Snapshot(package);

        var first = EntryCloner.CloneMesh(mesh, "custom", cloneMaterialsAndTextures: true);
        var firstMaterial = package.GetUExport(MaterialIndices(first)[0]);
        var firstTexture = package.GetUExport(ParameterIndices(firstMaterial).Single());
        byte[] firstData = first.Data;
        var second = EntryCloner.CloneMesh(mesh, "_custom", cloneMaterialsAndTextures: true);
        var secondMaterial = package.GetUExport(MaterialIndices(second)[0]);
        var secondTexture = package.GetUExport(ParameterIndices(secondMaterial).Single());

        Assert.AreEqual(new NameReference("Mesh_custom_2", 0), first.ObjectName);
        Assert.AreEqual(new NameReference("Mesh_custom_3", 0), second.ObjectName);
        Assert.AreEqual(new NameReference("Material_custom_2", 0), firstMaterial.ObjectName);
        Assert.AreEqual(new NameReference("Material_custom_3", 0), secondMaterial.ObjectName);
        Assert.AreEqual(new NameReference("Texture_custom", 1), firstTexture.ObjectName);
        Assert.AreEqual(new NameReference("Texture_custom", 2), secondTexture.ObjectName);
        Assert.AreNotEqual(firstMaterial.UIndex, secondMaterial.UIndex);
        Assert.AreNotEqual(firstTexture.UIndex, secondTexture.UIndex);
        CollectionAssert.AreEqual(firstData, first.Data, "Cloning another copy must preserve the first copy.");
        AssertOriginalsUnchanged(originals);
        firstTexture.WriteProperty(new IntProperty(16, "SizeX"));
        Assert.AreEqual(1, texture.GetProperty<IntProperty>("SizeX").Value);
        Assert.AreEqual(1, secondTexture.GetProperty<IntProperty>("SizeX").Value);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NullImportedAndNonMaterialSlotsStaySharedWhileCubeFacesRelink(bool isStatic)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CloneMeshImports.pcc", MEGame.LE3);
        var importedMaterial = package.CreateImport("MaterialInstanceConstant", "ImportedMaterial");
        var importedParent = package.CreateImport("Material", "ImportedParent");
        var importedTexture = package.CreateImport("Texture2D", "ImportedTexture");
        var cube = package.CreateExport("Cube", "TextureCube", indexed: false);
        var face = CreateTexture(package, "Face", cube);
        cube.WriteProperties(new PropertyCollection
        {
            new ObjectProperty(face, "FacePosX"), new ObjectProperty(face, "FaceNegX"),
            new ObjectProperty(importedTexture, "FacePosY")
        });
        cube.WriteBinary(new UTextureCube { SourceArt = [] });
        var material = CreateMaterial(package, "Material", cube.UIndex, face.UIndex, importedTexture.UIndex, 0);
        material.WriteProperty(new ObjectProperty(importedParent, "Parent"));
        var nonMaterial = package.CreateExport("NonMaterial", "Object", indexed: false);
        int[] sourceSlots = [material.UIndex, importedMaterial.UIndex, 0, nonMaterial.UIndex, material.UIndex, int.MaxValue];
        var mesh = CreateMesh(package, "Mesh", isStatic, sourceSlots, [importedMaterial.UIndex, material.UIndex]);
        var originals = Snapshot(package);
        int originalExportCount = package.ExportCount;
        int originalImportCount = package.ImportCount;
        var info = EntryCloner.GetMeshMaterialCloneInfo(mesh);
        Assert.AreEqual(isStatic ? 8 : 6, info.MaterialSlotCount);
        CollectionAssert.AreEquivalent(new IEntry[] { material, importedMaterial, importedParent }, info.Materials.ToArray());
        CollectionAssert.AreEquivalent(new IEntry[] { cube, face, importedTexture }, info.Textures.ToArray());
        CollectionAssert.AreEquivalent(new IEntry[] { importedMaterial, importedParent, importedTexture }, info.SharedImports.ToArray());

        var clone = EntryCloner.CloneMesh(mesh, "custom", cloneMaterialsAndTextures: true);
        var slots = MaterialIndices(clone);
        var clonedMaterial = package.GetUExport(slots[0]);
        int[] parameters = ParameterIndices(clonedMaterial);
        var clonedCube = package.GetUExport(parameters[0]);
        var clonedFace = package.GetUExport(parameters[1]);

        Assert.AreEqual(originalExportCount + 4, package.ExportCount);
        Assert.AreEqual(originalImportCount, package.ImportCount);
        Assert.AreEqual(clonedMaterial.UIndex, slots[4]);
        Assert.AreEqual(int.MaxValue, slots[5]);
        CollectionAssert.AreEqual(sourceSlots.Skip(1).Take(3).ToArray(), slots.Skip(1).Take(3).ToArray());
        if (isStatic)
        {
            CollectionAssert.AreEqual(new[] { importedMaterial.UIndex, clonedMaterial.UIndex }, slots.Skip(6).ToArray());
        }
        Assert.AreEqual(importedParent.UIndex, clonedMaterial.GetProperty<ObjectProperty>("Parent").Value);
        Assert.AreEqual(importedTexture.UIndex, parameters[2]);
        Assert.AreEqual(0, parameters[3]);
        Assert.AreNotEqual(cube.UIndex, clonedCube.UIndex);
        Assert.AreNotEqual(face.UIndex, clonedFace.UIndex);
        Assert.AreEqual(clonedCube.UIndex, clonedFace.idxLink);
        Assert.AreEqual(clonedFace.UIndex, clonedCube.GetProperty<ObjectProperty>("FacePosX").Value);
        Assert.AreEqual(clonedFace.UIndex, clonedCube.GetProperty<ObjectProperty>("FaceNegX").Value);
        Assert.AreEqual(importedTexture.UIndex, clonedCube.GetProperty<ObjectProperty>("FacePosY").Value);
        AssertOriginalsUnchanged(originals);
        CollectionAssert.AreEqual(GeometryBytes(mesh), GeometryBytes(clone));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MaterialAndTextureChildrenAreClonedOnlyOnceWithTheMeshTree(bool isStatic)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CloneMeshTree.pcc", MEGame.LE3);
        var mesh = CreateMesh(package, "Mesh", isStatic, [], []);
        var material = CreateMaterial(package, "ChildMaterial", mesh);
        var texture = CreateTexture(package, "ChildTexture", material);
        material.WriteProperty(new ArrayProperty<StructProperty>(new[] { Parameter(texture.UIndex) }, "TextureParameterValues"));
        SetMaterials(mesh, material.UIndex);
        var child = package.CreateExport("OtherChild", "Object", mesh, indexed: false);
        child.WriteProperty(new ObjectProperty(material, "Material"));
        var originals = Snapshot(package);
        int originalExportCount = package.ExportCount;

        var clone = EntryCloner.CloneMesh(mesh, "custom", cloneMaterialsAndTextures: true, cloneTree: true);
        var clonedMaterial = package.GetUExport(MaterialIndices(clone).Single());
        var clonedTexture = package.GetUExport(ParameterIndices(clonedMaterial).Single());
        var clonedChild = package.Exports.Single(entry => entry.idxLink == clone.UIndex && entry.ClassName == "Object");

        Assert.AreEqual(originalExportCount + 4, package.ExportCount);
        Assert.AreEqual(clone.UIndex, clonedMaterial.idxLink);
        Assert.AreEqual(clonedMaterial.UIndex, clonedTexture.idxLink);
        Assert.AreEqual(new NameReference("Mesh_custom", 0), clone.ObjectName);
        Assert.AreEqual(new NameReference("ChildMaterial_custom", 0), clonedMaterial.ObjectName);
        Assert.AreEqual("ChildTexture_custom", clonedTexture.ObjectName.Name);
        Assert.AreEqual(clonedMaterial.UIndex, clonedChild.GetProperty<ObjectProperty>("Material").Value);
        AssertOriginalsUnchanged(originals);
    }

    [TestMethod]
    [DataRow(false, null)]
    [DataRow(true, null)]
    [DataRow(false, "custom")]
    [DataRow(true, "custom")]
    public void MeshOnlyCloningRetainsItsMaterialAndTextureReferences(bool isStatic, string suffix)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CloneMeshOnly.pcc", MEGame.LE3);
        var texture = CreateTexture(package, "Texture");
        var material = CreateMaterial(package, "Material", texture.UIndex);
        var mesh = CreateMesh(package, "Mesh", isStatic, [material.UIndex], [material.UIndex]);
        var originals = Snapshot(package);
        int originalExportCount = package.ExportCount;

        var clone = EntryCloner.CloneMesh(mesh, suffix);

        Assert.AreEqual(originalExportCount + 1, package.ExportCount);
        Assert.AreEqual(suffix == null ? new NameReference("Mesh", 1) : new NameReference("Mesh_custom", 0), clone.ObjectName);
        CollectionAssert.AreEqual(mesh.Data, clone.Data, "Mesh-only cloning must retain the complete original geometry binary.");
        CollectionAssert.AreEqual(MaterialIndices(mesh), MaterialIndices(clone));
        CollectionAssert.AreEqual(GeometryBytes(mesh), GeometryBytes(clone));
        AssertOriginalsUnchanged(originals);
        CollectionAssert.AreEqual(new[] { texture.UIndex }, ParameterIndices(material));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ME1ExternalTextureChildrenRetainTheirOriginalArchiveParent(bool isStatic)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CloneME1MeshExternalTexture.pcc", MEGame.ME1);
        var mesh = CreateMesh(package, "Mesh", isStatic, [], []);
        var material = CreateMaterial(package, "ChildMaterial", mesh);
        var texture = CreateTexture(package, "ChildTexture", material);
        var binary = texture.GetBinaryData<UTexture2D>();
        binary.Mips[0].StorageType = StorageTypes.extLZO;
        binary.Mips[0].DataOffset = 4096;
        texture.WriteBinary(binary);
        material.WriteProperty(new ArrayProperty<StructProperty>(new[] { Parameter(texture.UIndex) }, "TextureParameterValues"));
        SetMaterials(mesh, material.UIndex);
        var originals = Snapshot(package);
        int originalExportCount = package.ExportCount;

        var clone = EntryCloner.CloneMesh(mesh, "custom", cloneMaterialsAndTextures: true, cloneTree: true);
        var clonedMaterial = package.GetUExport(MaterialIndices(clone).Single());
        var clonedTexture = package.GetUExport(ParameterIndices(clonedMaterial).Single());

        Assert.AreEqual(originalExportCount + 3, package.ExportCount);
        Assert.AreEqual(clone.UIndex, clonedMaterial.idxLink);
        Assert.AreNotSame(texture, clonedTexture);
        Assert.AreEqual(texture.idxLink, clonedTexture.idxLink,
            "ME1 chooses the external mip archive from the original top-level parent.");
        Assert.AreEqual("ChildTexture_custom", clonedTexture.ObjectName.Name);
        Assert.AreSame(texture, package.FindExport(texture.InstancedFullPath));
        Assert.AreSame(clonedTexture, package.FindExport(clonedTexture.InstancedFullPath));
        Assert.AreEqual(StorageTypes.extLZO, clonedTexture.GetBinaryData<UTexture2D>().Mips.Single().StorageType);
        Assert.AreEqual(4096, clonedTexture.GetBinaryData<UTexture2D>().Mips.Single().DataOffset);
        AssertOriginalsUnchanged(originals);
    }

    private static (ExportEntry Entry, byte[] Header, byte[] Data)[] Snapshot(IMEPackage package) =>
        package.Exports.Select(entry => (entry, entry.Header, entry.Data)).ToArray();

    private static void AssertOriginalsUnchanged((ExportEntry Entry, byte[] Header, byte[] Data)[] originals)
    {
        foreach (var (entry, header, data) in originals)
        {
            CollectionAssert.AreEqual(header, entry.Header, $"Cloning must preserve the original header of {entry.InstancedFullPath}.");
            CollectionAssert.AreEqual(data, entry.Data, $"Cloning must preserve the original data of {entry.InstancedFullPath}.");
        }
    }

    private static ExportEntry CreateTexture(IMEPackage package, NameReference name, IEntry parent = null)
    {
        var texture = package.CreateExport(name, "Texture2D", parent, indexed: false);
        texture.WriteProperty(new IntProperty(1, "SizeX"));
        var binary = UTexture2D.Create();
        binary.Mips.Add(new UTexture2D.Texture2DMipMap(new byte[] { 1, 2, 3, 4 }, 1, 1));
        texture.WriteBinary(binary);
        return texture;
    }

    private static ExportEntry CreateMaterial(IMEPackage package, string name, params int[] textureIndices) =>
        CreateMaterial(package, name, null, textureIndices);

    private static ExportEntry CreateMaterial(IMEPackage package, string name, IEntry parent, params int[] textureIndices)
    {
        var material = package.CreateExport(name, "MaterialInstanceConstant", parent, indexed: false);
        material.WriteProperty(new ArrayProperty<StructProperty>(textureIndices.Select(Parameter), "TextureParameterValues"));
        return material;
    }

    private static StructProperty Parameter(int index) => new("TextureParameterValue", new PropertyCollection
    {
        new NameProperty("Diffuse", "ParameterName"), new ObjectProperty(index, "ParameterValue"),
        StructProperty.FromGuid(Guid.Empty, "ExpressionGUID")
    });

    private static int[] ParameterIndices(ExportEntry material) => material.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues")
        .Select(parameter => parameter.GetProp<ObjectProperty>("ParameterValue").Value).ToArray();

    private static void SetTextureReferences(MaterialResource resource, params int[] indices)
    {
        resource.UniformExpressionTextures = indices;
        resource.Uniform2DTextureExpressions = indices.Select(index => new MaterialUniformExpressionTexture
        {
            ExpressionType = "FMaterialUniformExpressionTexture", TextureIndex = index
        }).ToArray();
        foreach (int index in indices)
        {
            resource.TextureDependencyLengthMap.Add(index, 1);
        }
    }

    private static ExportEntry CreateMesh(IMEPackage package, string name, bool isStatic, int[] firstLodMaterials,
        int[] secondLodMaterials, int sharedNonMaterial = 0)
    {
        var mesh = package.CreateExport(name, isStatic ? "StaticMesh" : "SkeletalMesh", indexed: false);
        var bounds = new BoxSphereBounds { Origin = new Vector3(1, 2, 3), BoxExtent = new Vector3(4, 5, 6), SphereRadius = 7 };
        if (isStatic)
        {
            var binary = StaticMesh.Create();
            binary.Bounds = bounds;
            binary.BodySetup = sharedNonMaterial;
            binary.kDOPTreeME1ME2 = new kDOPTree { Nodes = [], Triangles = [] };
            binary.LODModels = secondLodMaterials.Length > 0
                ? [CreateStaticLod(firstLodMaterials, 1), CreateStaticLod(secondLodMaterials, 2)]
                : [CreateStaticLod(firstLodMaterials, 1)];
            binary.HighResSourceMeshName = "OriginalGeometry";
            binary.HighResSourceMeshCRC = 12345;
            binary.LightingGuid = new Guid("11111111-2222-3333-4444-555555555555");
            mesh.WriteBinary(binary);
        }
        else
        {
            var binary = SkeletalMesh.Create();
            binary.Bounds = bounds;
            binary.Materials = firstLodMaterials;
            binary.Origin = new Vector3(8, 9, 10);
            binary.RotOrigin = new Rotator(1, 2, 3);
            binary.RefSkeleton = [new MeshBone { Name = "root", ParentIndex = -1, Orientation = Quaternion.Identity, Position = Vector3.UnitY }];
            binary.SkeletalDepth = 1;
            binary.NameIndexMap.Add("root", 0);
            binary.ClothingAssets = sharedNonMaterial == 0 ? [] : [sharedNonMaterial];
            binary.LODModels = [CreateSkeletalLod()];
            mesh.WriteBinary(binary);
        }
        return mesh;
    }

    private static StaticMeshRenderData CreateStaticLod(int[] materials, float scale)
    {
        Vector3[] positions = [Vector3.Zero, Vector3.UnitX * scale, Vector3.UnitY * scale];
        return new StaticMeshRenderData
        {
            RawTriangles = [],
            Elements = materials.Select((index, position) => new StaticMeshElement
            {
                Material = index, MaterialIndex = position, EnableCollision = true, bEnableShadowCasting = true,
                NumTriangles = 1, MaxVertexIndex = 2, Fragments = [new FragmentRange(0, 1)]
            }).ToArray(),
            PositionVertexBuffer = new PositionVertexBuffer { Stride = 12, NumVertices = 3, VertexData = positions },
            VertexBuffer = new StaticMeshVertexBuffer
            {
                NumTexCoords = 1, NumVertices = 3, bUseFullPrecisionUVs = true,
                VertexData = positions.Select(position => new StaticMeshVertexBuffer.StaticMeshFullVertex
                {
                    TangentX = (PackedNormal)Vector3.UnitX, TangentZ = (PackedNormal)Vector3.UnitZ,
                    FullPrecisionUVs = [new Vector2(position.X, position.Y)]
                }).ToArray()
            },
            ColorVertexBuffer = new ColorVertexBuffer { NumVertices = 3, VertexData = [new SharpDX.Color(10, 20, 30, 255), new SharpDX.Color(40, 50, 60, 255), new SharpDX.Color(70, 80, 90, 255)] },
            ShadowExtrusionVertexBuffer = new ExtrusionVertexBuffer { NumVertices = 3, Stride = 4, VertexData = [1, 2, 3] },
            NumVertices = 3, IndexBuffer = [0, 1, 2], WireframeIndexBuffer = [0, 1, 1, 2, 2, 0], Edges = [],
            ShadowTriangleDoubleSided = [1], AdjacencyIndexBuffer = [], xmlFile = [1, 2, 3]
        };
    }

    private static StaticLODModel CreateSkeletalLod()
    {
        Vector3[] positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        return new StaticLODModel
        {
            Sections = [new SkelMeshSection { MaterialIndex = 0, ChunkIndex = 0, NumTriangles = 1 }],
            IndexBuffer = [0, 1, 2], ShadowIndices = [], ActiveBoneIndices = [0], ShadowTriangleDoubleSided = [1],
            Chunks = [new SkelMeshChunk { BoneMap = [0], NumSoftVertices = 3, MaxBoneInfluences = 1, RigidVertices = [], SoftVertices = positions.Select(position => new SoftSkinVertex
            {
                Position = position, TangentX = (PackedNormal)Vector3.UnitX, TangentY = (PackedNormal)Vector3.UnitY,
                TangentZ = (PackedNormal)Vector3.UnitZ, InfluenceWeights = new Influences(255, 0, 0, 0)
            }).ToArray() }],
            NumVertices = 3, Edges = [], RequiredBones = [0], RawPointIndices = [0, 1, 2], NumTexCoords = 1,
            VertexBufferGPUSkin = new SkeletalMeshVertexBuffer { NumTexCoords = 1, VertexData = positions.Select(position => new GPUSkinVertex
            {
                Position = position, TangentX = (PackedNormal)Vector3.UnitX, TangentZ = (PackedNormal)Vector3.UnitZ,
                InfluenceWeights = new Influences(255, 0, 0, 0)
            }).ToArray() }
        };
    }

    private static int[] MaterialIndices(ExportEntry mesh) => mesh.ClassName == "StaticMesh"
        ? mesh.GetBinaryData<StaticMesh>().LODModels.SelectMany(lod => lod.Elements.Select(element => element.Material)).ToArray()
        : mesh.GetBinaryData<SkeletalMesh>().Materials;

    private static void SetMaterials(ExportEntry mesh, params int[] materialIndices)
    {
        if (mesh.ClassName == "StaticMesh")
        {
            var binary = mesh.GetBinaryData<StaticMesh>();
            binary.LODModels[0].Elements = CreateStaticLod(materialIndices, 1).Elements;
            mesh.WriteBinary(binary);
        }
        else
        {
            var binary = mesh.GetBinaryData<SkeletalMesh>();
            binary.Materials = materialIndices;
            mesh.WriteBinary(binary);
        }
    }

    private static byte[] GeometryBytes(ExportEntry mesh)
    {
        var binary = ObjectBinary.From(mesh);
        if (binary is StaticMesh staticMesh)
        {
            foreach (var element in staticMesh.LODModels.SelectMany(lod => lod.Elements))
            {
                element.Material = 0;
            }
        }
        else if (binary is SkeletalMesh skeletalMesh)
        {
            Array.Fill(skeletalMesh.Materials, 0);
        }
        return binary.ToBytes(mesh.FileRef);
    }
}
