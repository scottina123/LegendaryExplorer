using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Numerics;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class BoxSphereBoundsTests
{
    [TestMethod]
    public void UnionIncludesBothComponentSpheresRegardlessOfOrder()
    {
        var small = new BoxSphereBounds
        {
            Origin = Vector3.Zero,
            BoxExtent = Vector3.One,
            SphereRadius = Vector3.One.Length()
        };
        var large = new BoxSphereBounds
        {
            Origin = Vector3.Zero,
            BoxExtent = new Vector3(10f, 2f, 1f),
            SphereRadius = new Vector3(10f, 2f, 1f).Length()
        };

        BoxSphereBounds smallThenLarge = small.Union(large);
        BoxSphereBounds largeThenSmall = large.Union(small);

        Assert.AreEqual(large.SphereRadius, smallThenLarge.SphereRadius, 0.0001f);
        Assert.AreEqual(smallThenLarge.SphereRadius, largeThenSmall.SphereRadius, 0.0001f);
    }
}
