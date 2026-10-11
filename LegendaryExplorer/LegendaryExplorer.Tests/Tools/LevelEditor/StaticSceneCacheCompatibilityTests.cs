using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Device = SharpDX.Direct3D11.Device;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class StaticSceneCacheCompatibilityTests
{
    [DataTestMethod]
    [DataRow(DriverType.Warp, 1)]
    [DataRow(DriverType.Warp, 4)]
    [DataRow(DriverType.Hardware, 1)]
    [DataRow(DriverType.Hardware, 4)]
    public void StaticBackdropRestoresEveryColorDepthAndHitSample(DriverType driver, int sampleCount)
    {
        using var scene = new OffscreenScene(driver, sampleCount);
        scene.DrawSampleValues();
        scene.Context.BeginStaticSceneCacheCandidate();
        Assert.IsTrue(scene.Context.CaptureStaticSceneCache(123, captureHitBuffer: true));
        scene.AssertTargetsRemainBound();
        scene.Clear();
        Assert.IsTrue(scene.Context.TryRestoreStaticSceneCache(123, restoreHitBuffer: true));
        scene.AssertTargetsRemainBound();

        Vector4[,] restored = scene.ReadSamples();
        for (int sample = 0; sample < sampleCount; sample++)
        {
            AssertVector(new Vector4(0.25f, 0.5f, 0.75f, 2) * new Vector4(sample + 1, sample + 1, sample + 1, 1), restored[0, sample]);
            AssertVector(new Vector4((sample + 1) / 8f, 0, 0, 1), restored[1, sample]);
            AssertVector(new Vector4(16 * (sample + 1) / 255f, 32 / 255f, 64 / 255f, 1), restored[2, sample]);
        }

        // A stale scene key or camera must never restore a previously captured backdrop.
        Assert.IsFalse(scene.Context.TryRestoreStaticSceneCache(124, restoreHitBuffer: true));
        scene.Context.Camera.Position += Vector3.UnitX;
        Assert.IsFalse(scene.Context.TryRestoreStaticSceneCache(123, restoreHitBuffer: true));
        scene.Context.Camera.Position -= Vector3.UnitX;
        Assert.IsTrue(scene.Context.TryRestoreStaticSceneCache(123, restoreHitBuffer: true));
        scene.Context.InvalidateStaticSceneCache();
        Assert.IsFalse(scene.Context.TryRestoreStaticSceneCache(123, restoreHitBuffer: true));
        scene.Context.BeginStaticSceneCacheCandidate();
        scene.Context.RejectStaticSceneCache();
        Assert.IsFalse(scene.Context.CaptureStaticSceneCache(123, captureHitBuffer: true));
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(4)]
    public void BackdropWithoutHitCaptureLeavesCurrentHitIdsIntact(int sampleCount)
    {
        using var scene = new OffscreenScene(DriverType.Warp, sampleCount);
        scene.DrawSampleValues();
        Assert.IsTrue(scene.Context.CaptureStaticSceneCache(42));
        scene.Clear();
        Assert.IsFalse(scene.Context.TryRestoreStaticSceneCache(42, restoreHitBuffer: true));
        Assert.IsTrue(scene.Context.TryRestoreStaticSceneCache(42));
        Vector4[,] restored = scene.ReadSamples();
        for (int sample = 0; sample < sampleCount; sample++)
        {
            Assert.AreEqual((sample + 1) / 8f, restored[1, sample].X, 0.00001f);
            AssertVector(Vector4.Zero, restored[2, sample]);
        }
    }

    private static void AssertVector(Vector4 expected, Vector4 actual)
    {
        Assert.AreEqual(expected.X, actual.X, 0.00001f);
        Assert.AreEqual(expected.Y, actual.Y, 0.00001f);
        Assert.AreEqual(expected.Z, actual.Z, 0.00001f);
        Assert.AreEqual(expected.W, actual.W, 0.00001f);
    }

    /// <summary>
    /// Supplies the actual renderer's scene targets without creating a WPF window. The public
    /// cache methods allocate and copy their own resources; this fixture only supplies live targets.
    /// </summary>
    private sealed class OffscreenScene : IDisposable
    {
        public MeshRenderContext Context { get; } = new();
        private readonly Device device;
        private readonly DeviceContext immediate;
        private readonly List<IDisposable> resources = [];
        private readonly int samples;
        private readonly Texture2D color;
        private readonly Texture2D depth;
        private readonly Texture2D hit;
        private readonly RenderTargetView colorView;
        private readonly RenderTargetView hitView;
        private readonly DepthStencilView depthView;
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        public OffscreenScene(DriverType driver, int sampleCount)
        {
            samples = sampleCount;
            device = new Device(driver, DeviceCreationFlags.BgraSupport);
            immediate = device.ImmediateContext;
            foreach (Format format in new[] { Format.R16G16B16A16_Float, Format.D32_Float, Format.B8G8R8A8_UNorm })
                Assert.IsTrue(device.CheckMultisampleQualityLevels(format, samples) > 0, $"{driver} must support {samples} samples for {format}.");
            SetProperty(typeof(RenderContext), nameof(RenderContext.Device), device);
            SetProperty(typeof(RenderContext), nameof(RenderContext.ImmediateContext), immediate);
            SetProperty(typeof(RenderContext), nameof(RenderContext.Width), 3);
            SetProperty(typeof(RenderContext), nameof(RenderContext.Height), 2);
            SetProperty(typeof(MeshRenderContext), nameof(MeshRenderContext.SampleCount), samples);
            color = Own(CreateTexture(Format.R16G16B16A16_Float, BindFlags.RenderTarget | BindFlags.ShaderResource, samples));
            hit = Own(CreateTexture(Format.B8G8R8A8_UNorm, BindFlags.RenderTarget | BindFlags.ShaderResource, samples));
            depth = Own(CreateTexture(Format.R32_Typeless, BindFlags.DepthStencil | BindFlags.ShaderResource, samples));
            colorView = Own(new RenderTargetView(device, color));
            hitView = Own(new RenderTargetView(device, hit));
            depthView = Own(new DepthStencilView(device, depth, new DepthStencilViewDescription
            {
                Format = Format.D32_Float,
                Dimension = samples > 1 ? DepthStencilViewDimension.Texture2DMultisampled : DepthStencilViewDimension.Texture2D
            }));
            SetField("SceneColor", color);
            SetField("SceneColorView", colorView);
            SetField("HitBufferMS", hit);
            SetField("HitBufferView", hitView);
            SetField("HitBuffer", Own(CreateTexture(Format.B8G8R8A8_UNorm, BindFlags.RenderTarget, 1)));
            SetProperty(typeof(MeshRenderContext), nameof(MeshRenderContext.DepthBuffer), depth);
            SetProperty(typeof(MeshRenderContext), nameof(MeshRenderContext.DepthBufferView), depthView);
            BindTargets();
        }

        public void DrawSampleValues()
        {
            const string shader = """
float4 VSMain(uint id : SV_VertexID) : SV_POSITION
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0.5, 1);
}
struct Output { float4 color : SV_TARGET0; float4 hit : SV_TARGET1; float depth : SV_DEPTH; };
Output PSMain(uint sampleIndex : SV_SAMPLEINDEX)
{
    Output output;
    output.color = float4(float3(0.25, 0.5, 0.75) * (sampleIndex + 1), 2);
    output.hit = float4(16 * (sampleIndex + 1), 32, 64, 255) / 255;
    output.depth = (sampleIndex + 1) / 8.0;
    return output;
}
""";
            using var vsCode = ShaderBytecode.Compile(shader, "VSMain", "vs_5_0");
            using var psCode = ShaderBytecode.Compile(shader, "PSMain", "ps_5_0");
            using var vs = new VertexShader(device, vsCode);
            using var ps = new PixelShader(device, psCode);
            using var rasterizer = new RasterizerState(device, new RasterizerStateDescription
            {
                FillMode = FillMode.Solid, CullMode = CullMode.None, IsDepthClipEnabled = true, IsMultisampleEnabled = samples > 1
            });
            using var depthState = new DepthStencilState(device, new DepthStencilStateDescription
            {
                IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.All, DepthComparison = Comparison.Always
            });
            BindTargets();
            immediate.Rasterizer.State = rasterizer;
            immediate.Rasterizer.SetViewport(0, 0, 3, 2);
            immediate.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            immediate.VertexShader.Set(vs);
            immediate.PixelShader.Set(ps);
            immediate.OutputMerger.SetBlendState(null);
            immediate.OutputMerger.SetDepthStencilState(depthState);
            immediate.Draw(3, 0);
        }

        public void Clear()
        {
            immediate.ClearRenderTargetView(colorView, new RawColor4(0, 0, 0, 0));
            immediate.ClearRenderTargetView(hitView, new RawColor4(0, 0, 0, 0));
            immediate.ClearDepthStencilView(depthView, DepthStencilClearFlags.Depth, 0, 0);
        }

        public void AssertTargetsRemainBound()
        {
            RenderTargetView[] targets = immediate.OutputMerger.GetRenderTargets(2, out DepthStencilView activeDepth);
            try
            {
                Assert.AreEqual(colorView.NativePointer, targets[0].NativePointer);
                Assert.AreEqual(hitView.NativePointer, targets[1].NativePointer);
                Assert.AreEqual(depthView.NativePointer, activeDepth.NativePointer);
            }
            finally
            {
                foreach (RenderTargetView view in targets) view?.Dispose();
                activeDepth?.Dispose();
            }
        }

        public Vector4[,] ReadSamples()
        {
            string shader = $"#define SAMPLES {samples}\n" + """
#if SAMPLES > 1
Texture2DMS<float4, SAMPLES> ColorSource : register(t0);
Texture2DMS<float, SAMPLES> DepthSource : register(t1);
Texture2DMS<float4, SAMPLES> HitSource : register(t2);
#define LOAD(source, sampleIndex) source.Load(int2(0, 0), sampleIndex)
#else
Texture2D<float4> ColorSource : register(t0);
Texture2D<float> DepthSource : register(t1);
Texture2D<float4> HitSource : register(t2);
#define LOAD(source, sampleIndex) source.Load(int3(0, 0, 0))
#endif
float4 VSMain(uint id : SV_VertexID) : SV_POSITION
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}
float4 PSMain(float4 position : SV_POSITION) : SV_TARGET0
{
    int sampleIndex = (int)position.x;
    int row = (int)position.y;
    if (row == 0) return LOAD(ColorSource, sampleIndex);
    if (row == 1) return float4(LOAD(DepthSource, sampleIndex), 0, 0, 1);
    return LOAD(HitSource, sampleIndex);
}
""";
            using var vsCode = ShaderBytecode.Compile(shader, "VSMain", "vs_5_0");
            using var psCode = ShaderBytecode.Compile(shader, "PSMain", "ps_5_0");
            using var vs = new VertexShader(device, vsCode);
            using var ps = new PixelShader(device, psCode);
            using var colorSource = new ShaderResourceView(device, color);
            using var hitSource = new ShaderResourceView(device, hit);
            using var depthSource = new ShaderResourceView(device, depth, new ShaderResourceViewDescription
            {
                Format = Format.R32_Float,
                Dimension = samples > 1 ? ShaderResourceViewDimension.Texture2DMultisampled : ShaderResourceViewDimension.Texture2D,
                Texture2D = { MipLevels = 1 }
            });
            var description = new Texture2DDescription
            {
                Width = samples, Height = 3, MipLevels = 1, ArraySize = 1, Format = Format.R32G32B32A32_Float,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget
            };
            using var output = new Texture2D(device, description);
            using var outputView = new RenderTargetView(device, output);
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CpuAccessFlags = CpuAccessFlags.Read;
            using var staging = new Texture2D(device, description);
            using var rasterizer = new RasterizerState(device, new RasterizerStateDescription
            {
                FillMode = FillMode.Solid, CullMode = CullMode.None, IsDepthClipEnabled = true
            });
            immediate.ClearState();
            immediate.OutputMerger.SetRenderTargets(outputView);
            immediate.Rasterizer.State = rasterizer;
            immediate.Rasterizer.SetViewport(0, 0, samples, 3);
            immediate.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            immediate.VertexShader.Set(vs);
            immediate.PixelShader.Set(ps);
            immediate.PixelShader.SetShaderResources(0, colorSource, depthSource, hitSource);
            immediate.Draw(3, 0);
            immediate.ClearState();
            immediate.CopyResource(output, staging);
            var mapped = immediate.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            try
            {
                var result = new Vector4[3, samples];
                for (int row = 0; row < 3; row++)
                    for (int sample = 0; sample < samples; sample++)
                        result[row, sample] = Marshal.PtrToStructure<Vector4>(IntPtr.Add(mapped.DataPointer, row * mapped.RowPitch + sample * 16));
                return result;
            }
            finally { immediate.UnmapSubresource(staging, 0); }
        }

        private void BindTargets() => immediate.OutputMerger.SetRenderTargets(depthView, colorView, hitView);
        private void SetProperty(Type type, string name, object value) => type.GetProperty(name)!.SetValue(Context, value);
        private void SetField(string name, object value) => typeof(MeshRenderContext).GetField(name, PrivateInstance)!.SetValue(Context, value);
        private T Own<T>(T resource) where T : IDisposable { resources.Add(resource); return resource; }
        private Texture2D CreateTexture(Format format, BindFlags binds, int count) => new(device, new Texture2DDescription
        {
            Width = 3, Height = 2, MipLevels = 1, ArraySize = 1, Format = format,
            SampleDescription = new SampleDescription(count, 0), BindFlags = binds, Usage = ResourceUsage.Default
        });

        public void Dispose()
        {
            immediate.ClearState();
            foreach (string name in new[] { "StaticSceneColorCache", "StaticSceneDepthCache", "StaticSceneHitCache" })
                (typeof(MeshRenderContext).GetField(name, PrivateInstance)!.GetValue(Context) as IDisposable)?.Dispose();
            for (int i = resources.Count - 1; i >= 0; i--) resources[i].Dispose();
            Context.EmptyCaches();
            immediate.Dispose();
            device.Dispose();
        }
    }
}
