using System.Numerics;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class CommonStructsTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AffineMatrixRoundTripsWithLegacyAndCanonicalRowNames(bool canonicalRows)
    {
        Matrix4x4 expected = Matrix4x4.CreateScale(1.25f, 2.5f, 0.75f)
                             * Matrix4x4.CreateFromYawPitchRoll(0.4f, -0.2f, 0.8f)
                             * Matrix4x4.CreateTranslation(1250, -320, 47);
        StructProperty property = CommonStructs.MatrixProp(expected, "LightToWorld");
        if (canonicalRows)
        {
            foreach (StructProperty row in property.Properties)
            {
                row.Name = row.Name.Name + "Plane";
            }
        }

        Matrix4x4 actual = CommonStructs.GetMatrix(property);
        Assert.AreEqual(expected, actual);
        Assert.AreEqual(new Vector3(1250, -320, 47), Vector3.Transform(Vector3.Zero, actual));
        Vector3 point = new(2, -3, 7);
        Assert.AreEqual(Vector3.Transform(point, expected), Vector3.Transform(point, actual));
    }

    [TestMethod]
    public void CanonicalMatrixRowsTakePrecedenceOverLegacyRows()
    {
        Matrix4x4 expected = Matrix4x4.CreateTranslation(200, -400, 600);
        StructProperty property = CommonStructs.MatrixProp(expected);
        StructProperty legacyRows = CommonStructs.MatrixProp(Matrix4x4.Identity);
        foreach (StructProperty row in property.Properties)
        {
            row.Name = row.Name.Name + "Plane";
        }
        foreach (StructProperty row in legacyRows.Properties)
        {
            property.Properties.Add(row);
        }

        Assert.AreEqual(expected, CommonStructs.GetMatrix(property));
    }
}
