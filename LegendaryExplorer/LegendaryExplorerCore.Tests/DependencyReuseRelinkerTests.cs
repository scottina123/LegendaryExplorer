using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using LegendaryExplorerCore.Localization;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class DependencyReuseRelinkerTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void CloneAllDependenciesPreservesReusedMaterialsAndRelinksOnlyCopiedDependencies(MEGame game)
    {
        using var source = MEPackageHandler.CreateMemoryEmptyPackage("Source.pcc", game);
        using var destination = MEPackageHandler.CreateMemoryEmptyPackage("Destination.pcc", game);
        var fixture = CreateFixture(source, destination);
        AssertNoReferenceIssues(source);
        AssertNoReferenceIssues(destination);
        var originalSource = SnapshotCleanExports(source);
        var originalDestination = SnapshotCleanExports(destination);
        int originalExportCount = destination.ExportCount;
        int originalImportCount = destination.ImportCount;
        var options = new RelinkerOptionsPackage { GenerateImportsForGlobalFiles = false };

        var report = EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies,
            fixture.SourceMesh, destination, fixture.DestinationParent, true, options, out var clonedEntry);

        Assert.HasCount(0, report, string.Join("; ", report));
        var clonedMesh = (ExportEntry)clonedEntry;
        var freshMaterial = destination.FindExport("Assets.FreshInstance");
        Assert.IsNotNull(freshMaterial);
        CollectionAssert.AreEqual(new[]
        {
            fixture.DestinationInstance.UIndex, fixture.DestinationMaterial.UIndex,
            fixture.DestinationInstance.UIndex, freshMaterial.UIndex
        }, clonedMesh.GetBinaryData<StaticMesh>().LODModels[0].Elements.Select(element => element.Material).ToArray());
        Assert.AreEqual(fixture.DestinationParent.UIndex, clonedMesh.idxLink);
        Assert.AreEqual(originalExportCount + 3, destination.ExportCount,
            "Only the mesh, its missing material, and the missing texture should be copied.");
        Assert.AreEqual(originalImportCount, destination.ImportCount);
        Assert.IsNull(destination.FindExport("SourceIndexTrap"),
            "A destination texture index must never be interpreted as an unrelated source export.");
        Assert.AreSame(fixture.DestinationMaterial, options.CrossPackageMap[fixture.SourceMaterial]);
        Assert.AreSame(fixture.DestinationInstance, options.CrossPackageMap[fixture.SourceInstance]);
        Assert.AreSame(fixture.DestinationTexture, options.CrossPackageMap[fixture.SourceTexture]);
        AssertFreshMaterialReferences(destination, fixture);
        AssertUnchanged(originalSource);
        AssertUnchanged(originalDestination);
        AssertNoReferenceIssues(destination);

        using var saved = destination.SaveToStream(compress: false);
        saved.Position = 0;
        using var reopened = MEPackageHandler.OpenMEPackageFromStream(saved);
        AssertFreshMaterialReferences(reopened, fixture);
        AssertNoReferenceIssues(reopened);
    }

    [TestMethod]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void CloneAllDependenciesReusesExistingPackageChildrenWithoutChangingTheirData(MEGame game)
    {
        using var source = MEPackageHandler.CreateMemoryEmptyPackage("Source.pcc", game);
        using var destination = MEPackageHandler.CreateMemoryEmptyPackage("Destination.pcc", game);
        var fixture = CreateFixture(source, destination);
        AssertNoReferenceIssues(destination);
        var originalSource = SnapshotCleanExports(source);
        var originalDestination = SnapshotCleanExports(destination);
        int originalExportCount = destination.ExportCount;
        int originalImportCount = destination.ImportCount;

        var report = EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies,
            fixture.SourceAssets, destination, null, true,
            new RelinkerOptionsPackage { GenerateImportsForGlobalFiles = false }, out var clonedEntry);

        Assert.HasCount(0, report, string.Join("; ", report));
        Assert.AreSame(destination.FindExport("Assets"), clonedEntry);
        Assert.AreEqual(originalExportCount + 2, destination.ExportCount,
            "Existing package children must be reused; only the missing material and texture are copied.");
        Assert.AreEqual(originalImportCount, destination.ImportCount);
        AssertFreshMaterialReferences(destination, fixture);
        AssertUnchanged(originalSource);
        AssertUnchanged(originalDestination);
        AssertNoReferenceIssues(destination);
    }

    [TestMethod]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void ReplaceSingularWithRelinkStillCopiesSourceMaterialAndReusesItsTexture(MEGame game)
    {
        using var source = MEPackageHandler.CreateMemoryEmptyPackage("Source.pcc", game);
        using var destination = MEPackageHandler.CreateMemoryEmptyPackage("Destination.pcc", game);
        var fixture = CreateFixture(source, destination);
        var originalSource = SnapshotCleanExports(source);
        var otherDestinationExports = SnapshotCleanExports(destination)
            .Where(snapshot => snapshot.Entry != fixture.DestinationMaterial).ToArray();
        byte[] originalMaterialData = fixture.DestinationMaterial.Data;
        int originalExportCount = destination.ExportCount;
        int originalImportCount = destination.ImportCount;

        var report = EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.ReplaceSingularWithRelink,
            fixture.SourceMaterial, destination, fixture.DestinationMaterial, true,
            new RelinkerOptionsPackage { GenerateImportsForGlobalFiles = false }, out var replacedEntry);

        Assert.HasCount(0, report, string.Join("; ", report));
        Assert.AreSame(fixture.DestinationMaterial, replacedEntry);
        Assert.IsFalse(originalMaterialData.AsSpan().SequenceEqual(fixture.DestinationMaterial.Data));
        Assert.IsTrue(fixture.DestinationMaterial.EntryHasPendingChanges);
        Assert.IsTrue(fixture.DestinationMaterial.GetProperty<BoolProperty>("TwoSided").Value,
            "Explicit replacement must copy the source material's properties.");
        var sourceBinary = fixture.SourceMaterial.GetBinaryData<Material>();
        var replacedBinary = fixture.DestinationMaterial.GetBinaryData<Material>();
        Assert.AreEqual(sourceBinary.SM3MaterialResource.ID, replacedBinary.SM3MaterialResource.ID);
        AssertTextureReferences(replacedBinary.SM3MaterialResource, fixture.DestinationTexture.UIndex,
            fixture.DestinationTexture.UIndex);
        AssertTextureReferences(replacedBinary.SM2MaterialResource, fixture.DestinationTexture.UIndex,
            fixture.DestinationTexture.UIndex);
        Assert.AreEqual(originalExportCount, destination.ExportCount);
        Assert.AreEqual(originalImportCount, destination.ImportCount);
        AssertUnchanged(originalSource);
        AssertUnchanged(otherDestinationExports);
        AssertNoReferenceIssues(destination);
    }

    private sealed record Fixture(ExportEntry SourceAssets, ExportEntry SourceTexture, ExportEntry SourceMaterial,
        ExportEntry SourceInstance, ExportEntry SourceMesh, ExportEntry DestinationTexture,
        ExportEntry DestinationMaterial, ExportEntry DestinationInstance, ExportEntry DestinationParent);

    private static Fixture CreateFixture(IMEPackage source, IMEPackage destination)
    {
        var sourceAssets = source.CreatePackageExport("Assets");
        var trap = source.CreateExport("SourceIndexTrap", "Object", indexed: false);
        var sourceTexture = CreateTexture(source, sourceAssets, "SharedTexture", 1);
        var sourceMaterial = CreateMaterial(source, sourceAssets, sourceTexture, true);
        var sourceInstance = CreateInstance(source, sourceAssets, "SharedInstance", sourceMaterial,
            sourceTexture.UIndex, sourceTexture.UIndex);
        var freshTexture = CreateTexture(source, sourceAssets, "FreshTexture", 5);
        CreateInstance(source, sourceAssets, "FreshInstance", sourceMaterial,
            freshTexture.UIndex, sourceTexture.UIndex, freshTexture.UIndex);
        var sourceMeshes = source.CreatePackageExport("SourceMeshes");
        var freshMaterial = source.FindExport("Assets.FreshInstance");
        var sourceMesh = CreateMesh(source, sourceMeshes, "Terrain", sourceInstance.UIndex,
            sourceMaterial.UIndex, sourceInstance.UIndex, freshMaterial.UIndex);

        var destinationAssets = destination.CreatePackageExport("Assets");
        var destinationTexture = CreateTexture(destination, destinationAssets, "SharedTexture", 10);
        var destinationMaterial = CreateMaterial(destination, destinationAssets, destinationTexture, false);
        var destinationInstance = CreateInstance(destination, destinationAssets, "SharedInstance", destinationMaterial,
            destinationTexture.UIndex, destinationTexture.UIndex);
        var destinationParent = destination.CreatePackageExport("DestinationMeshes");
        // Seed the mesh class import so every import required by the clone already exists.
        CreateMesh(destination, destinationParent, "ExistingMesh", destinationInstance.UIndex);
        Assert.AreEqual(trap.UIndex, destinationTexture.UIndex,
            "The destination texture's local index must identify a wrong-class object in the source.");
        Assert.AreNotEqual(sourceTexture.UIndex, destinationTexture.UIndex);
        Assert.AreNotEqual(sourceMaterial.UIndex, destinationMaterial.UIndex);
        Assert.AreNotEqual(sourceInstance.UIndex, destinationInstance.UIndex);
        return new Fixture(sourceAssets, sourceTexture, sourceMaterial, sourceInstance, sourceMesh,
            destinationTexture, destinationMaterial, destinationInstance, destinationParent);
    }

    private static ExportEntry CreateTexture(IMEPackage package, IEntry parent, string name, byte color)
    {
        var texture = package.CreateExport(name, "Texture2D", parent, indexed: false);
        texture.WriteProperties(new PropertyCollection { new IntProperty(1, "SizeX"), new IntProperty(1, "SizeY") });
        var binary = UTexture2D.Create();
        binary.Mips.Add(new UTexture2D.Texture2DMipMap(new[] { color, color, color, (byte)255 }, 1, 1));
        texture.WriteBinary(binary);
        return texture;
    }

    private static ExportEntry CreateMaterial(IMEPackage package, IEntry parent, ExportEntry texture, bool twoSided)
    {
        var material = package.CreateExport("SharedMaterial", "Material", parent, indexed: false);
        material.WriteProperty(new BoolProperty(twoSided, "TwoSided"));
        var binary = Material.Create();
        binary.SM3MaterialResource.ID = Guid.NewGuid();
        binary.SM2MaterialResource.ID = Guid.NewGuid();
        SetTextureReferences(binary.SM3MaterialResource, texture.UIndex, texture.UIndex);
        SetTextureReferences(binary.SM2MaterialResource, texture.UIndex, texture.UIndex);
        material.WriteBinary(binary);
        return material;
    }

    private static ExportEntry CreateInstance(IMEPackage package, IEntry parent, string name,
        ExportEntry baseMaterial, params int[] textures)
    {
        var instance = package.CreateExport(name, "MaterialInstanceConstant", parent, indexed: false);
        instance.WriteProperties(new PropertyCollection
        {
            new ObjectProperty(baseMaterial, "Parent"),
            new BoolProperty(true, "bHasStaticPermutationResource"),
            new ArrayProperty<StructProperty>(textures.Select((index, position) => new StructProperty(
                "TextureParameterValue", new PropertyCollection
                {
                    new NameProperty($"Texture{position}", "ParameterName"),
                    new ObjectProperty(index, "ParameterValue"),
                    StructProperty.FromGuid(Guid.Empty, "ExpressionGUID")
                })), "TextureParameterValues")
        });
        var binary = MaterialInstance.Create();
        SetTextureReferences(binary.SM3StaticPermutationResource, textures);
        SetTextureReferences(binary.SM2StaticPermutationResource, textures);
        instance.WriteBinary(binary);
        return instance;
    }

    private static void SetTextureReferences(MaterialResource resource, params int[] indices)
    {
        resource.UniformExpressionTextures = indices;
    }

    private static void AssertFreshMaterialReferences(IMEPackage package, Fixture fixture)
    {
        var material = package.FindExport("Assets.FreshInstance");
        var texture = package.FindExport("Assets.FreshTexture");
        Assert.IsNotNull(material);
        Assert.IsNotNull(texture);
        int[] expected = [texture.UIndex, fixture.DestinationTexture.UIndex, texture.UIndex];
        Assert.AreEqual(fixture.DestinationMaterial.UIndex, material.GetProperty<ObjectProperty>("Parent").Value);
        CollectionAssert.AreEqual(expected, material.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues")
            .Select(parameter => parameter.GetProp<ObjectProperty>("ParameterValue").Value).ToArray());
        var binary = material.GetBinaryData<MaterialInstance>();
        AssertTextureReferences(binary.SM3StaticPermutationResource, expected);
        AssertTextureReferences(binary.SM2StaticPermutationResource, expected);
        CollectionAssert.AreEqual(new byte[] { 5, 5, 5, 255 }, texture.GetBinaryData<UTexture2D>().Mips.Single().Mip);
    }

    private static void AssertTextureReferences(MaterialResource resource, params int[] expected)
    {
        CollectionAssert.AreEqual(expected, resource.UniformExpressionTextures);
    }

    private static (ExportEntry Entry, byte[] Header, byte[] Data)[] SnapshotCleanExports(IMEPackage package)
    {
        foreach (var entry in package.Exports)
        {
            entry.DataChanged = false;
            entry.HeaderChanged = false;
            entry.EntryHasPendingChanges = false;
        }
        return package.Exports.Select(entry => (entry, entry.Header, entry.Data)).ToArray();
    }

    private static void AssertUnchanged((ExportEntry Entry, byte[] Header, byte[] Data)[] originals)
    {
        foreach (var (entry, header, data) in originals)
        {
            CollectionAssert.AreEqual(header, entry.Header, $"The original header of {entry.InstancedFullPath} changed.");
            CollectionAssert.AreEqual(data, entry.Data, $"The original data of {entry.InstancedFullPath} changed.");
            Assert.IsFalse(entry.DataChanged, $"{entry.InstancedFullPath} was marked as modified.");
            Assert.IsFalse(entry.HeaderChanged, $"{entry.InstancedFullPath} was marked as modified.");
            Assert.IsFalse(entry.EntryHasPendingChanges, $"{entry.InstancedFullPath} was marked as modified.");
        }
    }

    private static void AssertNoReferenceIssues(IMEPackage package)
    {
        var issues = new ReferenceCheckPackage();
        EntryChecker.CheckReferences(issues, package, LECLocalizationShim.NonLocalizedStringConverter);
        Assert.HasCount(0, issues.GetBlockingErrors(), string.Join("; ", issues.GetBlockingErrors()));
        Assert.HasCount(0, issues.GetSignificantIssues(), string.Join("; ", issues.GetSignificantIssues()));
    }

    private static ExportEntry CreateMesh(IMEPackage package, IEntry parent, string name, params int[] materials)
    {
        var mesh = package.CreateExport(name, "StaticMesh", parent, indexed: false);
        var binary = StaticMesh.Create();
        Vector3[] positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        binary.LODModels = [new StaticMeshRenderData
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
            ColorVertexBuffer = new ColorVertexBuffer { NumVertices = 0, VertexData = [] },
            ShadowExtrusionVertexBuffer = new ExtrusionVertexBuffer { NumVertices = 0, Stride = 4, VertexData = [] },
            NumVertices = 3, IndexBuffer = [0, 1, 2], WireframeIndexBuffer = [0, 1, 1, 2, 2, 0], Edges = [],
            ShadowTriangleDoubleSided = [1], AdjacencyIndexBuffer = []
        }];
        mesh.WriteBinary(binary);
        return mesh;
    }
}
