using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using LegendaryExplorerCore.Gammtek.IO;
using LegendaryExplorerCore.Localization;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class ReferenceIssueBinaryPatchTests
{
    private const int BadBodySetup = 39060;
    private const int BadMaterial = 33946;
    // Bounds (28), BodySetup (4), empty compact KDOP tree (40).
    private const int InternalVersionOffset = 72;
    // InternalVersion (4), LOD count (4), first LOD's bulk-data flags/count/size (12).
    private const int FirstBulkDataOffsetOffset = 92;

    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void ClearsNonRoundTrippingMeshReferencesWithoutChangingGeometryOrOpaqueBytes(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceRawMesh.pcc", game);
        var validMaterial = package.CreateImport("Material", "ValidMaterial");
        var mesh = CreateMesh(package, BadBodySetup, [BadMaterial, validMaterial.UIndex, BadMaterial]);
        byte[] originalBinary = MakeBinaryNonRoundTripping(mesh);
        byte[] expectedBinary = ClearReferenceWords(originalBinary, BadBodySetup, BadMaterial);
        byte[] originalProperties = mesh.DataReadOnly[..mesh.propsEnd()].ToArray();
        string[] originalNames = package.Names.ToArray();
        Assert.IsFalse(originalBinary.AsSpan().SequenceEqual(
            mesh.GetBinaryData<StaticMesh>().ToBytes(package, mesh.DataOffset + mesh.propsEnd())),
            "The fixture must reproduce the cleanup failure caused by a lossy mesh serializer.");

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(0, result.Failures.Count, string.Join("; ", result.Failures));
        Assert.AreEqual(0, result.RemovedPropertyCount);
        Assert.AreEqual(3, result.ClearedBinaryReferenceCount);
        CollectionAssert.AreEqual(new[] { mesh }, result.ChangedExports.ToArray());
        CollectionAssert.AreEqual(originalProperties, mesh.DataReadOnly[..mesh.propsEnd()].ToArray());
        CollectionAssert.AreEqual(expectedBinary, mesh.GetBinaryData(),
            "Only the invalid reference words may change, including in a non-round-tripping mesh.");
        CollectionAssert.AreEqual(originalNames, package.Names.ToArray());
        AssertMeshReferences(mesh, validMaterial.UIndex);
        AssertNoReferenceIssues(package, mesh);

        using var stream = package.SaveToStream(compress: false);
        stream.Position = 0;
        using var reopened = MEPackageHandler.OpenMEPackageFromStream(stream);
        var savedMesh = reopened.GetUExport(mesh.UIndex);
        CollectionAssert.AreEqual(expectedBinary, savedMesh.GetBinaryData());
        AssertMeshReferences(savedMesh, validMaterial.UIndex);
        AssertNoReferenceIssues(reopened, savedMesh);
        var secondCleanup = ReferenceIssueCleaner.RemoveBadReferences(reopened);
        Assert.AreEqual(0, secondCleanup.Failures.Count, string.Join("; ", secondCleanup.Failures));
        Assert.AreEqual(0, secondCleanup.ChangedExports.Count);
        CollectionAssert.AreEqual(expectedBinary, savedMesh.GetBinaryData());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemovesTaggedBodySetupAndPatchesMeshReferencesWithBulkDataRelocation(bool exportRelativeBulkPointer)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceMeshProperties.pcc", MEGame.LE3);
        var validMaterial = package.CreateImport("Material", "ValidMaterial");
        var mesh = CreateMesh(package, BadBodySetup, [BadMaterial, validMaterial.UIndex, BadMaterial], new PropertyCollection
        {
            new ObjectProperty(BadBodySetup, "BodySetup"),
            new IntProperty(12345, "UnrelatedValue")
        }, dataOffset: 10000);
        if (exportRelativeBulkPointer)
        {
            byte[] data = mesh.Data;
            int pointer = EndianReader.ToInt32(data, mesh.propsEnd() + FirstBulkDataOffsetOffset, package.Endian);
            BitConverter.GetBytes(pointer - mesh.DataOffset).CopyTo(data, mesh.propsEnd() + FirstBulkDataOffsetOffset);
            mesh.Data = data;
        }
        byte[] originalBinary = MakeBinaryNonRoundTripping(mesh);
        byte[] expectedBinary = ClearReferenceWords(originalBinary, BadBodySetup, BadMaterial);
        int originalPropertyEnd = mesh.propsEnd();
        int originalBulkDataOffset = EndianReader.ToInt32(originalBinary, FirstBulkDataOffsetOffset, package.Endian);

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(0, result.Failures.Count, string.Join("; ", result.Failures));
        Assert.AreEqual(1, result.RemovedPropertyCount);
        Assert.AreEqual(3, result.ClearedBinaryReferenceCount);
        Assert.IsNull(mesh.GetProperty<ObjectProperty>("BodySetup"));
        Assert.AreEqual(12345, mesh.GetProperty<IntProperty>("UnrelatedValue").Value);
        int propertyShrink = originalPropertyEnd - mesh.propsEnd();
        Assert.IsGreaterThan(0, propertyShrink);
        BitConverter.GetBytes(originalBulkDataOffset - propertyShrink).CopyTo(expectedBinary, FirstBulkDataOffsetOffset);
        CollectionAssert.AreEqual(expectedBinary, mesh.GetBinaryData(),
            "Removing the property must preserve mesh bytes and relocate the inline bulk-data pointer.");
        Assert.AreEqual((exportRelativeBulkPointer ? 0 : mesh.DataOffset)
                        + mesh.propsEnd() + FirstBulkDataOffsetOffset + sizeof(int),
            EndianReader.ToInt32(mesh.GetBinaryData(), FirstBulkDataOffsetOffset, package.Endian));
        AssertMeshReferences(mesh, validMaterial.UIndex);
        AssertNoReferenceIssues(package, mesh);

        using var stream = package.SaveToStream(compress: false);
        stream.Position = 0;
        using var reopened = MEPackageHandler.OpenMEPackageFromStream(stream);
        var savedMesh = reopened.GetUExport(mesh.UIndex);
        Assert.IsNull(savedMesh.GetProperty<ObjectProperty>("BodySetup"));
        Assert.AreEqual(12345, savedMesh.GetProperty<IntProperty>("UnrelatedValue").Value);
        CollectionAssert.AreEqual(expectedBinary, savedMesh.GetBinaryData());
        AssertMeshReferences(savedMesh, validMaterial.UIndex);
        Assert.AreEqual(0, ReferenceIssueCleaner.RemoveBadReferences(reopened).ChangedExports.Count);
    }

    [TestMethod]
    public void ClearsNonRoundTrippingFracturedMeshReferencesWithoutChangingFractureData()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceFracturedMesh.pcc", MEGame.LE3);
        var mesh = package.CreateExport("Mesh", "FracturedStaticMesh", indexed: false);
        var binary = FracturedStaticMesh.Create();
        binary.kDOPTreeME3UDKLE = new kDOPTreeCompact { RootBound = new kDOP(), Nodes = [], Triangles = [] };
        binary.BodySetup = BadBodySetup;
        binary.SourceStaticMesh = 39061;
        binary.CoreMeshOffset = new Vector3(8, 9, 10);
        mesh.WriteBinary(binary);
        byte[] data = mesh.Data;
        BitConverter.GetBytes(9).CopyTo(data, mesh.propsEnd() + InternalVersionOffset);
        mesh.Data = data.Concat(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }).ToArray();
        byte[] expectedBinary = mesh.GetBinaryData();
        int[] referenceOffsets = FindWordOffsets(expectedBinary, BadBodySetup)
            .Concat(FindWordOffsets(expectedBinary, 39061)).ToArray();
        Assert.HasCount(2, referenceOffsets);
        foreach (int offset in referenceOffsets)
            expectedBinary.AsSpan(offset, sizeof(int)).Clear();

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(0, result.Failures.Count, string.Join("; ", result.Failures));
        Assert.AreEqual(2, result.ClearedBinaryReferenceCount);
        CollectionAssert.AreEqual(expectedBinary, mesh.GetBinaryData());
        var repaired = mesh.GetBinaryData<FracturedStaticMesh>();
        Assert.AreEqual(0, repaired.BodySetup);
        Assert.AreEqual(0, repaired.SourceStaticMesh);
        Assert.AreEqual(9, repaired.InternalVersion);
        Assert.AreEqual(new Vector3(8, 9, 10), repaired.CoreMeshOffset);
        Assert.AreEqual(0, ReferenceIssueCleaner.RemoveBadReferences(package).ChangedExports.Count);
    }

    [TestMethod]
    public void RemovingOnlyABadPropertyPreservesNonRoundTrippingMeshBinary()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceMeshPropertyOnly.pcc", MEGame.LE3);
        var validMaterial = package.CreateImport("Material", "ValidMaterial");
        var mesh = CreateMesh(package, 0, [validMaterial.UIndex], new PropertyCollection
        {
            new ObjectProperty(BadBodySetup, "BodySetup")
        });
        byte[] expectedBinary = MakeBinaryNonRoundTripping(mesh);
        int originalPropertyEnd = mesh.propsEnd();
        int originalBulkDataOffset = EndianReader.ToInt32(expectedBinary, FirstBulkDataOffsetOffset, package.Endian);

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(0, result.Failures.Count, string.Join("; ", result.Failures));
        Assert.AreEqual(1, result.RemovedPropertyCount);
        Assert.AreEqual(0, result.ClearedBinaryReferenceCount);
        Assert.IsNull(mesh.GetProperty<ObjectProperty>("BodySetup"));
        BitConverter.GetBytes(originalBulkDataOffset + mesh.propsEnd() - originalPropertyEnd)
            .CopyTo(expectedBinary, FirstBulkDataOffsetOffset);
        CollectionAssert.AreEqual(expectedBinary, mesh.GetBinaryData());
        Assert.AreEqual(validMaterial.UIndex, mesh.GetBinaryData<StaticMesh>().LODModels[0].Elements[0].Material);
    }

    private static ExportEntry CreateMesh(IMEPackage package, int bodySetup, int[] materialIndices,
        PropertyCollection properties = null, int dataOffset = 0)
    {
        var mesh = package.CreateExport("Mesh", "StaticMesh", indexed: false);
        mesh.DataOffset = dataOffset;
        var binary = StaticMesh.Create();
        binary.BodySetup = bodySetup;
        binary.Bounds = new BoxSphereBounds
        {
            Origin = new Vector3(1, 2, 3), BoxExtent = new Vector3(4, 5, 6), SphereRadius = 7
        };
        binary.kDOPTreeME3UDKLE = new kDOPTreeCompact
        {
            RootBound = new kDOP(), Nodes = [], Triangles = []
        };
        Vector3[] positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        binary.LODModels = [new StaticMeshRenderData
        {
            RawTriangles = [new StaticMeshTriangle { NumUVs = 1 }],
            Elements = materialIndices.Select((index, position) => new StaticMeshElement
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
            ColorVertexBuffer = new ColorVertexBuffer
            {
                NumVertices = 3, VertexData = [new SharpDX.Color(10, 20, 30, 255),
                    new SharpDX.Color(40, 50, 60, 255), new SharpDX.Color(70, 80, 90, 255)]
            },
            ShadowExtrusionVertexBuffer = new ExtrusionVertexBuffer { NumVertices = 3, Stride = 4, VertexData = [1, 2, 3] },
            NumVertices = 3, IndexBuffer = [0, 1, 2], WireframeIndexBuffer = [0, 1, 1, 2, 2, 0], Edges = [],
            ShadowTriangleDoubleSided = [1], AdjacencyIndexBuffer = []
        }];
        binary.HighResSourceMeshName = "OriginalGeometry";
        binary.HighResSourceMeshCRC = 12345;
        binary.LightingGuid = new Guid("11111111-2222-3333-4444-555555555555");
        mesh.WritePropertiesAndBinary(properties ?? new PropertyCollection(), binary);
        return mesh;
    }

    private static byte[] MakeBinaryNonRoundTripping(ExportEntry mesh)
    {
        byte[] originalData = mesh.Data;
        int binaryStart = mesh.propsEnd();
        BitConverter.GetBytes(9).CopyTo(originalData, binaryStart + InternalVersionOffset);
        // The converter consumes this platform-data flag without retaining it. It must survive cleanup.
        // The first LOD contains one 368-byte raw triangle, followed by the element count and first element.
        originalData[binaryStart + 96 + 368 + 4 + 48] = 0xA7;
        mesh.Data = originalData.Concat(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x27 }).ToArray();
        return mesh.GetBinaryData();
    }

    private static byte[] ClearReferenceWords(byte[] source, int bodySetup, int material)
    {
        byte[] expected = source.ToArray();
        var bodyOffsets = FindWordOffsets(source, bodySetup);
        var materialOffsets = FindWordOffsets(source, material);
        Assert.HasCount(1, bodyOffsets, "The fixture must have one bad BodySetup binary reference.");
        Assert.HasCount(2, materialOffsets, "The fixture must have two repeated bad material references.");
        foreach (int offset in bodyOffsets.Concat(materialOffsets))
            expected.AsSpan(offset, sizeof(int)).Clear();
        return expected;
    }

    private static int[] FindWordOffsets(byte[] source, int value)
    {
        byte[] word = BitConverter.GetBytes(value);
        return Enumerable.Range(0, source.Length - sizeof(int) + 1)
            .Where(offset => source.AsSpan(offset, sizeof(int)).SequenceEqual(word)).ToArray();
    }

    private static void AssertMeshReferences(ExportEntry mesh, int validMaterial)
    {
        var binary = mesh.GetBinaryData<StaticMesh>();
        Assert.AreEqual(0, binary.BodySetup);
        CollectionAssert.AreEqual(new[] { 0, validMaterial, 0 }, binary.LODModels[0].Elements.Select(element => element.Material).ToArray());
        Assert.AreEqual(9, binary.InternalVersion, "Cleanup must preserve the original mesh version.");
        Assert.HasCount(3, binary.LODModels[0].PositionVertexBuffer.VertexData);
        Assert.AreEqual(Vector3.UnitY, binary.LODModels[0].PositionVertexBuffer.VertexData[2]);
    }

    private static void AssertNoReferenceIssues(IMEPackage package, ExportEntry mesh)
    {
        var issues = new ReferenceCheckPackage();
        EntryChecker.CheckReferences(issues, package, LECLocalizationShim.NonLocalizedStringConverter);
        Assert.IsFalse(issues.GetSignificantIssues().Any(issue => issue.Entry == mesh),
            string.Join("; ", issues.GetSignificantIssues().Where(issue => issue.Entry == mesh)));
    }
}
