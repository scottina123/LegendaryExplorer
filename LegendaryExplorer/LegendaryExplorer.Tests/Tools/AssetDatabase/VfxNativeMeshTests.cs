using LegendaryExplorer.Tools.AssetDatabase.VFXPreview;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Device = SharpDX.Direct3D11.Device;

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

    [DataTestMethod]
    [DataRow(false, 0f)]
    [DataRow(true, 1f)]
    public void NativeMaterialShaderRepairsOnlyOpaqueZeroAlphaWithoutChangingRgb(bool depthWrite, float expectedAlpha)
    {
        const string shader = """
float4 VSMain(uint id : SV_VertexID) : SV_POSITION
{
    float2 corner = float2((id << 1) & 2, id & 2);
    return float4(corner * float2(2, -2) + float2(-1, 1), 0, 1);
}
float4 PSMain(float4 position : SV_POSITION) : SV_TARGET0
{
    return float4(position.xyz * float3(0.5, 1, 1) + float3(0, 0, 0.75), 0);
}
""";
        using var device = new Device(DriverType.Warp);
        DeviceContext context = device.ImmediateContext;
        var renderContext = new MeshRenderContext();
        // Supply the software test device without creating an interactive viewport or hardware renderer.
        typeof(RenderContext).GetProperty(nameof(RenderContext.Device)).SetValue(renderContext, device);
        using var vertexBytecode = ShaderBytecode.Compile(shader, "VSMain", "vs_5_0");
        using var pixelBytecode = ShaderBytecode.Compile(shader, "PSMain", "ps_5_0");
        using var vertexShader = new VertexShader(device, vertexBytecode);
        PixelShader pixelShader = VfxGameShaderRenderer.GetNativeMaterialPixelShader(
            renderContext, Guid.NewGuid(), pixelBytecode.Bytecode.Data, depthWrite);
        var description = new Texture2DDescription
        {
            Width = 1, Height = 1, MipLevels = 1, ArraySize = 1,
            Format = Format.R32G32B32A32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget
        };
        using var target = new Texture2D(device, description);
        using var targetView = new RenderTargetView(device, target);
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CpuAccessFlags = CpuAccessFlags.Read;
        using var staging = new Texture2D(device, description);
        using var rasterizer = new RasterizerState(device, new RasterizerStateDescription
        {
            FillMode = FillMode.Solid, CullMode = CullMode.None, IsDepthClipEnabled = true
        });
        try
        {
            context.Rasterizer.State = rasterizer;
            context.Rasterizer.SetViewport(0, 0, 1, 1);
            context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            context.VertexShader.Set(vertexShader);
            context.PixelShader.Set(pixelShader);
            context.OutputMerger.SetRenderTargets(targetView);
            context.ClearRenderTargetView(targetView, new RawColor4(0, 0, 0, 1));
            context.Draw(3, 0);
            context.CopyResource(target, staging);
            var mapped = context.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            Vector4 pixel;
            try { pixel = Marshal.PtrToStructure<Vector4>(mapped.DataPointer); }
            finally { context.UnmapSubresource(staging, 0); }

            Assert.AreEqual(new Vector4(0.25f, 0.5f, 0.75f, expectedAlpha), pixel);
        }
        finally
        {
            context.ClearState();
            renderContext.EmptyCaches();
        }
    }
}
