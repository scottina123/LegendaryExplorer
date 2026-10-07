using LegendaryExplorer.Tools.AssetDatabase.VFXPreview;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using System;
using System.Runtime.InteropServices;
using Device = SharpDX.Direct3D11.Device;

namespace LegendaryExplorer.Tests.Tools.AssetDatabase;

[TestClass]
public class VfxSceneColorBufferTests
{
    [TestMethod]
    public void HdrParticlesCompositeBeforeDisplayConversionRetainsHighlightGradients()
    {
        // CampFire_Small's cooked additive shader writes alpha zero and HDR particle RGB <20,4,1>.
        // Draw overlapping cards at three intensities, including a dim sample below the shoulder.
        byte[] pixels = RenderOverlappingParticles();

        Assert.IsTrue(pixels[0] > pixels[1] && pixels[1] > pixels[2]);
        Assert.IsTrue(pixels[4] > pixels[0], "The highlight must remain brighter than the dim sample.");
        Assert.IsTrue(pixels[5] > 100 && pixels[5] < 160,
            "The medium HDR sample should retain its warm color instead of clipping to white.");
        Assert.IsTrue(pixels[9] > pixels[5] + 30,
            "Hotter highlights must retain an intensity gradient after all cards have blended.");
        Assert.IsTrue(pixels[10] > pixels[6] + 30);
        Assert.IsTrue(pixels[4] > pixels[5] && pixels[5] > pixels[6]);
        Assert.AreEqual(255, pixels[3]);
        Assert.AreEqual(255, pixels[7]);
        Assert.AreEqual(255, pixels[11]);
    }

    [TestMethod]
    public void FilmicDisplayTransferIsMonotonicForFadingHdrParticles()
    {
        byte[] pixels = RenderOverlappingParticles(129, sweep: true);
        for (int sample = 1; sample < 129; sample++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                Assert.IsTrue(pixels[sample * 4 + channel] >= pixels[(sample - 1) * 4 + channel],
                    $"Channel {channel} became darker as HDR intensity increased at sample {sample}.");
            }
        }
    }

    [TestMethod]
    public void SelectedDisplayBackgroundSurvivesHdrResolveAndResize()
    {
        using var device = new Device(DriverType.Warp);
        using DeviceContext context = device.ImmediateContext;
        using var buffer = new VfxSceneColorBuffer(device);
        using var target = CreateTarget(device, 1, 1);
        using var targetView = new RenderTargetView(device, target);
        context.Rasterizer.SetViewport(0, 0, 1, 1);
        foreach (float value in new[] { 0f, 0.02f, 0.117f, 0.4f, 0.8f, 1f })
        {
            buffer.Resize(1, 1);
            buffer.Begin(context, null, null, new RawColor4(value, value, value, 1));
            buffer.Resolve(context, targetView, null, null);
            byte[] pixels = ReadPixels(context, target, 4);
            Assert.AreEqual((int)MathF.Round(value * 255), pixels[0], 1,
                "The selected canvas background is a display color and must not be darkened by the postprocess.");
        }
        context.ClearState();
    }

    [TestMethod]
    public void ResolveRetainsViewportRasterizerDepthBlendAndMaterialBindings()
    {
        using var device = new Device(DriverType.Warp);
        using DeviceContext context = device.ImmediateContext;
        using var buffer = new VfxSceneColorBuffer(device);
        buffer.Resize(8, 8);
        using var target = CreateTarget(device, 8, 8);
        using var targetView = new RenderTargetView(device, target);
        using var rasterizer = new RasterizerState(device, new RasterizerStateDescription
        {
            CullMode = CullMode.Back,
            FillMode = FillMode.Wireframe
        });
        using var depth = new DepthStencilState(device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthComparison = Comparison.Less
        });
        using var blend = CreateAdditiveBlend(device);
        context.Rasterizer.State = rasterizer;
        context.Rasterizer.SetViewport(1, 2, 3, 4);
        context.OutputMerger.SetDepthStencilState(depth, 7);
        context.OutputMerger.SetBlendState(blend, new RawColor4(0.1f, 0.2f, 0.3f, 0.4f), -1);
        // Use a separate texture so material SRV binding never aliases the resolve destination.
        using var texture = CreateTarget(device, 1, 1);
        using var textureView = new ShaderResourceView(device, texture);
        context.PixelShader.SetShaderResource(0, textureView);
        buffer.Begin(context, null, null, new RawColor4(0.25f, 0.5f, 0.75f, 1));

        buffer.Resolve(context, targetView, null, null);

        RawViewportF viewport = context.Rasterizer.GetViewports<RawViewportF>()[0];
        Assert.AreEqual(1, viewport.X);
        Assert.AreEqual(2, viewport.Y);
        Assert.AreEqual(3, viewport.Width);
        Assert.AreEqual(4, viewport.Height);
        using RasterizerState actualRasterizer = context.Rasterizer.State;
        using DepthStencilState actualDepth = context.OutputMerger.GetDepthStencilState(out int stencilReference);
        using BlendState actualBlend = context.OutputMerger.GetBlendState(out RawColor4 blendFactor, out int sampleMask);
        using ShaderResourceView actualTexture = context.PixelShader.GetShaderResources(0, 1)[0];
        Assert.AreEqual(rasterizer.NativePointer, actualRasterizer.NativePointer);
        Assert.AreEqual(depth.NativePointer, actualDepth.NativePointer);
        Assert.AreEqual(blend.NativePointer, actualBlend.NativePointer);
        Assert.AreEqual(textureView.NativePointer, actualTexture.NativePointer);
        Assert.AreEqual(7, stencilReference);
        Assert.AreEqual(0.3f, blendFactor.B);
        Assert.AreEqual(-1, sampleMask);
        context.ClearState();
    }

    private static byte[] RenderOverlappingParticles(int sampleCount = 3, bool sweep = false)
    {
        const string particleShader = """
float4 VSMain(uint vertexID : SV_VertexID) : SV_POSITION
{
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}
float4 PSMain(float4 position : SV_POSITION) : SV_TARGET0
{
    float intensity = position.x < 1 ? 0.01 : position.x < 2 ? 0.1 : 0.3;
    return float4(float3(20, 4, 1) * intensity, 0);
}
""";
        string shaderSource = sweep
            ? particleShader.Replace("position.x < 1 ? 0.01 : position.x < 2 ? 0.1 : 0.3",
                $"(position.x - 0.5) / {sampleCount - 1}.0 * 0.5", StringComparison.Ordinal)
            : particleShader;
        using var device = new Device(DriverType.Warp);
        using DeviceContext context = device.ImmediateContext;
        using var buffer = new VfxSceneColorBuffer(device);
        buffer.Resize(sampleCount, 1);
        using var display = CreateTarget(device, sampleCount, 1);
        using var displayView = new RenderTargetView(device, display);
        using var vertexBytecode = ShaderBytecode.Compile(shaderSource, "VSMain", "vs_5_0");
        using var pixelBytecode = ShaderBytecode.Compile(shaderSource, "PSMain", "ps_5_0");
        using var vertexShader = new VertexShader(device, vertexBytecode);
        using var pixelShader = new PixelShader(device, pixelBytecode);
        using var blend = CreateAdditiveBlend(device);
        context.Rasterizer.SetViewport(0, 0, sampleCount, 1);
        context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        context.VertexShader.Set(vertexShader);
        context.PixelShader.Set(pixelShader);
        context.OutputMerger.SetBlendState(blend);
        buffer.Begin(context, null, null, new RawColor4(0, 0, 0, 1));
        context.Draw(3, 0);
        context.Draw(3, 0);
        buffer.Resolve(context, displayView, null, null);
        byte[] pixels = ReadPixels(context, display, sampleCount * 4);
        context.ClearState();
        return pixels;
    }

    private static byte[] ReadPixels(DeviceContext context, Texture2D display, int count)
    {
        context.OutputMerger.SetRenderTargets((RenderTargetView)null);
        var stagingDescription = display.Description;
        stagingDescription.BindFlags = BindFlags.None;
        stagingDescription.Usage = ResourceUsage.Staging;
        stagingDescription.CpuAccessFlags = CpuAccessFlags.Read;
        using var staging = new Texture2D(context.Device, stagingDescription);
        context.CopyResource(display, staging);
        var mapped = context.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
        var pixels = new byte[count];
        Marshal.Copy(mapped.DataPointer, pixels, 0, pixels.Length);
        context.UnmapSubresource(staging, 0);
        return pixels;
    }

    private static Texture2D CreateTarget(Device device, int width, int height) => new(device, new Texture2DDescription
    {
        Width = width,
        Height = height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.R8G8B8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
    });

    private static BlendState CreateAdditiveBlend(Device device)
    {
        var description = new BlendStateDescription();
        description.RenderTarget[0] = new RenderTargetBlendDescription
        {
            IsBlendEnabled = true,
            SourceBlend = BlendOption.One,
            DestinationBlend = BlendOption.One,
            BlendOperation = BlendOperation.Add,
            SourceAlphaBlend = BlendOption.Zero,
            DestinationAlphaBlend = BlendOption.One,
            AlphaBlendOperation = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteMaskFlags.All
        };
        return new BlendState(device, description);
    }
}
