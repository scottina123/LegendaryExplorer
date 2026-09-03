using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class MeshDecimatorTests
{
    private const int OversizedVertexCount = MeshDecimator.MaxSupportedVertexCount + 1;

    [TestMethod]
    public void StaticMeshIsDecimatedToSixteenBitVertexLimit()
    {
        Vector3[] positions = CreatePositions();
        ushort[] indices = CreateTriangleStripIndices();
        var lod = new StaticMeshRenderData
        {
            NumVertices = OversizedVertexCount,
            PositionVertexBuffer = new PositionVertexBuffer
            {
                NumVertices = OversizedVertexCount,
                VertexData = positions
            },
            VertexBuffer = new StaticMeshVertexBuffer
            {
                NumVertices = OversizedVertexCount,
                VertexData = Enumerable.Range(0, OversizedVertexCount)
                    .Select(_ => new StaticMeshVertexBuffer.StaticMeshFullVertex()).ToArray()
            },
            ColorVertexBuffer = new ColorVertexBuffer { VertexData = [] },
            ShadowExtrusionVertexBuffer = new ExtrusionVertexBuffer { VertexData = [] },
            Elements =
            [
                new StaticMeshElement
                {
                    FirstIndex = 0,
                    NumTriangles = (uint)(indices.Length / 3),
                    MinVertexIndex = 0,
                    MaxVertexIndex = OversizedVertexCount - 1,
                    Fragments = []
                }
            ],
            IndexBuffer = indices,
            RawTriangles = [],
            WireframeIndexBuffer = [],
            Edges = [],
            ShadowTriangleDoubleSided = [],
            AdjacencyIndexBuffer = []
        };
        var mesh = new StaticMesh
        {
            LODModels = [lod],
            kDOPTreeME3UDKLE = KDOPTreeBuilder.ToCompact([], [])
        };

        MeshDecimator.DecimateToVertexLimit(mesh);

        Assert.AreEqual((uint)MeshDecimator.MaxSupportedVertexCount, mesh.LODModels[0].NumVertices);
        Assert.AreEqual(MeshDecimator.MaxSupportedVertexCount, mesh.LODModels[0].PositionVertexBuffer.VertexData.Length);
        Assert.IsTrue(mesh.LODModels[0].IndexBuffer.All(index => index < MeshDecimator.MaxSupportedVertexCount));
        Assert.AreEqual((uint)(mesh.LODModels[0].IndexBuffer.Length / 3), mesh.LODModels[0].Elements[0].NumTriangles);
    }

    [TestMethod]
    public void SkeletalMeshIsDecimatedToSixteenBitVertexLimitAndRebuildsChunks()
    {
        Vector3[] positions = CreatePositions();
        ushort[] indices = CreateTriangleStripIndices();
        var vertices = positions.Select(position => new GPUSkinVertex
        {
            Position = position,
            InfluenceBones = new Influences(0, 0, 0, 0),
            InfluenceWeights = new Influences(255, 0, 0, 0)
        }).ToArray();
        var lod = new StaticLODModel
        {
            NumVertices = OversizedVertexCount,
            Sections =
            [
                new SkelMeshSection
                {
                    BaseIndex = 0,
                    ChunkIndex = 0,
                    NumTriangles = indices.Length / 3
                }
            ],
            IndexBuffer = indices,
            Chunks =
            [
                new SkelMeshChunk
                {
                    BaseVertexIndex = 0,
                    BoneMap = [0],
                    NumRigidVertices = OversizedVertexCount,
                    RigidVertices = [],
                    SoftVertices = []
                }
            ],
            VertexBufferGPUSkin = new SkeletalMeshVertexBuffer { VertexData = vertices },
            RawPointIndices = Enumerable.Range(0, OversizedVertexCount).Select(index => (ushort)index).ToArray(),
            ShadowIndices = [],
            ShadowTriangleDoubleSided = [],
            Edges = []
        };
        var mesh = new SkeletalMesh { LODModels = [lod] };

        MeshDecimator.DecimateToVertexLimit(mesh);

        StaticLODModel result = mesh.LODModels[0];
        Assert.AreEqual((uint)MeshDecimator.MaxSupportedVertexCount, result.NumVertices);
        Assert.AreEqual(MeshDecimator.MaxSupportedVertexCount, result.VertexBufferGPUSkin.VertexData.Length);
        Assert.IsTrue(result.IndexBuffer.All(index => index < MeshDecimator.MaxSupportedVertexCount));
        Assert.AreEqual(result.NumVertices, (uint)result.Chunks.Sum(chunk => chunk.NumRigidVertices + chunk.NumSoftVertices));
        Assert.AreEqual(0, result.Sections[0].ChunkIndex);
    }

    [TestMethod]
    public void GeometryDecimationBalancesUnevenTessellationAcrossSurface()
    {
        const int denseColumns = 32;
        const int sparseColumns = 8;
        const int rows = 16;
        const int targetVertexCount = 160;
        List<Vector3> positions = [];
        for (int y = 0; y <= rows; y++)
        {
            for (int x = 0; x <= denseColumns + sparseColumns; x++)
            {
                float positionX = x <= denseColumns
                    ? (float)x / denseColumns
                    : 1 + (float)(x - denseColumns) / sparseColumns;
                positions.Add(new Vector3(positionX, (float)y / rows, 0));
            }
        }

        int columns = denseColumns + sparseColumns;
        List<int> indices = [];
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                int topLeft = y * (columns + 1) + x;
                int bottomLeft = topLeft + columns + 1;
                indices.AddRange([topLeft, topLeft + 1, bottomLeft + 1]);
                indices.AddRange([topLeft, bottomLeft + 1, bottomLeft]);
            }
        }

        MeshDecimationResult result = MeshDecimator.DecimateGeometry(
            positions, [[.. indices]], targetVertexCount);

        int denseHalfVertices = result.Positions.Count(position => position.X < 1);
        int sparseHalfVertices = result.Positions.Count(position => position.X > 1);
        Assert.AreEqual(targetVertexCount, result.Positions.Length);
        Assert.IsTrue(denseHalfVertices is >= 55 and <= 105,
            $"The densely tessellated half retained {denseHalfVertices} of {targetVertexCount} vertices.");
        Assert.IsTrue(sparseHalfVertices is >= 55 and <= 105,
            $"The sparsely tessellated half retained {sparseHalfVertices} of {targetVertexCount} vertices.");
    }

    private static Vector3[] CreatePositions()
    {
        return Enumerable.Range(0, OversizedVertexCount)
            .Select(index => new Vector3(index, index % 2, (index % 3) * 0.1f)).ToArray();
    }

    private static ushort[] CreateTriangleStripIndices()
    {
        ushort[] indices = new ushort[(OversizedVertexCount - 2) * 3];
        for (int triangle = 0; triangle < OversizedVertexCount - 2; triangle++)
        {
            indices[triangle * 3] = (ushort)triangle;
            indices[triangle * 3 + 1] = (ushort)(triangle + 1);
            indices[triangle * 3 + 2] = (ushort)(triangle + 2);
        }
        return indices;
    }
}
