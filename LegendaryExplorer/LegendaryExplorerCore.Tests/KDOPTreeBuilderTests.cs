using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class KDOPTreeBuilderTests
{
    [TestMethod]
    public void ChildPartitionsUseOnlyTheirOwnTriangleRange()
    {
        List<Vector3> vertices = [];
        List<kDOPCollisionTriangle> triangles = [];
        int[] order = [9, 0, 19, 2, 17, 4, 15, 6, 13, 8, 11, 10, 7, 12, 5, 14, 3, 16, 1, 18];
        foreach (int index in order)
        {
            AddTriangle(new Vector3(1_000, (index - 9.5f) * 50, 0));
            AddTriangle(new Vector3(-1_000, 0, (index - 9.5f) * 50));
        }

        kDOPTreeCompact tree = KDOPTreeBuilder.ToCompact([.. triangles], [.. vertices]);
        Vector3[] centroids = tree.Triangles.Select(GetCentroid).ToArray();

        Assert.IsTrue(centroids.Take(20).All(centroid => centroid.X < 0));
        Assert.IsTrue(centroids.Skip(20).All(centroid => centroid.X > 0));
        Assert.IsTrue(centroids.Skip(20).Take(10).Max(centroid => centroid.Y)
                      <= centroids.Skip(30).Min(centroid => centroid.Y));

        void AddTriangle(Vector3 center)
        {
            ushort firstVertex = (ushort)vertices.Count;
            vertices.Add(center + new Vector3(-0.1f, 0, 0));
            vertices.Add(center + new Vector3(0.1f, 0, 0));
            vertices.Add(center + new Vector3(0, 0.1f, 0));
            triangles.Add(new kDOPCollisionTriangle(firstVertex, (ushort)(firstVertex + 1),
                (ushort)(firstVertex + 2), 0));
        }

        Vector3 GetCentroid(kDOPCollisionTriangle triangle) =>
            (vertices[triangle.Vertex1] + vertices[triangle.Vertex2] + vertices[triangle.Vertex3]) / 3;
    }
}
