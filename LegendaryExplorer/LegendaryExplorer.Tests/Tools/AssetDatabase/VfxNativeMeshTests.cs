using LegendaryExplorer.Tools.AssetDatabase.VFXPreview;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Numerics;

namespace LegendaryExplorer.Tests.Tools.AssetDatabase;

[TestClass]
public class VfxNativeMeshTests
{
    [TestMethod]
    public void NativeMeshPreservesSeparateVertexColorsIncludingTransparentVertices()
    {
        var lod = new StaticMeshRenderData
        {
            ColorVertexBuffer = new ColorVertexBuffer
            {
                VertexData = [new LegendaryExplorerCore.SharpDX.Color(64, 128, 255, 192), new LegendaryExplorerCore.SharpDX.Color(0, 0, 0, 0)]
            },
            VertexBuffer = new StaticMeshVertexBuffer
            {
                VertexData =
                [
                    new StaticMeshVertexBuffer.StaticMeshFullVertex { Color = new LegendaryExplorerCore.SharpDX.Color(255, 255, 255, 255) },
                    new StaticMeshVertexBuffer.StaticMeshFullVertex { Color = new LegendaryExplorerCore.SharpDX.Color(255, 255, 255, 255) }
                ]
            }
        };

        Vector4[] colors = VfxMeshRenderer.ReadMeshVertexColors(lod, 2);
        Vector4 particleColor = new(0.5f, 0.25f, 1, 0.5f);

        Assert.AreEqual(new Vector4(64, 128, 255, 192) / 255f, colors[0]);
        Assert.AreEqual(new Vector4(32, 32, 255, 96) / 255f, colors[0] * particleColor);
        Assert.AreEqual(Vector4.Zero, colors[1]);
        Assert.AreEqual(Vector4.Zero, colors[1] * particleColor);
        Assert.AreEqual(new LegendaryExplorerCore.SharpDX.Color(64, 128, 255, 192), lod.ColorVertexBuffer.VertexData[0]);
    }

    [TestMethod]
    public void NativeMeshReadsPackedVertexColorsFromOriginalTrilogyMeshes()
    {
        var lod = new StaticMeshRenderData
        {
            VertexBuffer = new StaticMeshVertexBuffer
            {
                VertexData =
                [new StaticMeshVertexBuffer.StaticMeshFullVertex { Color = new LegendaryExplorerCore.SharpDX.Color(255, 128, 64, 32) }]
            }
        };

        Vector4[] colors = VfxMeshRenderer.ReadMeshVertexColors(lod, 1);

        Assert.AreEqual(new Vector4(255, 128, 64, 32) / 255f, colors[0]);
    }

    [TestMethod]
    public void MissingNativeMeshColorStreamUsesNeutralWhiteForParticleFades()
    {
        var lod = new StaticMeshRenderData
        {
            ColorVertexBuffer = new ColorVertexBuffer { VertexData = [] },
            VertexBuffer = new StaticMeshVertexBuffer
            {
                VertexData =
                [
                    new StaticMeshVertexBuffer.StaticMeshFullVertex(),
                    new StaticMeshVertexBuffer.StaticMeshFullVertex()
                ]
            }
        };

        Vector4[] colors = VfxMeshRenderer.ReadMeshVertexColors(lod, 2);
        Vector4 particleColor = new(0.8f, 0.4f, 0.2f, 0.1f);

        Assert.AreEqual(Vector4.One, colors[0]);
        Assert.AreEqual(Vector4.One, colors[1]);
        Assert.AreEqual(particleColor, colors[0] * particleColor);
        Assert.AreEqual(new LegendaryExplorerCore.SharpDX.Color(0, 0, 0, 0), lod.VertexBuffer.VertexData[0].Color);
    }
}
