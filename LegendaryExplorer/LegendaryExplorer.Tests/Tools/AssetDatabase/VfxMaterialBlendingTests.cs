using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using LegendaryExplorer.Tools.AssetDatabase.VFXPreview;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace LegendaryExplorer.Tests.Tools.AssetDatabase;

[TestClass]
[DoNotParallelize]
public class VfxMaterialBlendingTests
{
    [DataTestMethod]
    [DataRow(VfxBlendMode.Opaque, 0.32f, 0.12f, 0.04f)]
    [DataRow(VfxBlendMode.Masked, 0.32f, 0.12f, 0.04f)]
    [DataRow(VfxBlendMode.Translucent, 0.1275f, 0.19f, 0.2675f)]
    [DataRow(VfxBlendMode.Additive, 0.14f, 0.215f, 0.305f)]
    [DataRow(VfxBlendMode.Modulate, 0.0915f, 0.178f, 0.264f)]
    [DataRow(VfxBlendMode.ModulateAndAdd, 0.104f, 0.203f, 0.3015f)]
    [DataRow(VfxBlendMode.SoftMasked, 0.1275f, 0.19f, 0.2675f)]
    [DataRow(VfxBlendMode.AlphaComposite, 0.1275f, 0.19f, 0.2675f)]
    public void SpriteAndMeshMaterialsCompositeFractionalAlpha(VfxBlendMode mode, float red, float green, float blue)
    {
        foreach (bool mesh in new[] { false, true })
        {
            Vector4 pixel = DrawFallbackPixel(mesh, mode, VfxOpacitySource.TextureAlpha, 0.25f);
            AssertPixel(pixel, new Vector4(red, green, blue, 1), $"{mode}, mesh={mesh}");
        }
    }

    [DataTestMethod]
    [DataRow(VfxBlendMode.Translucent)]
    [DataRow(VfxBlendMode.Additive)]
    [DataRow(VfxBlendMode.Modulate)]
    [DataRow(VfxBlendMode.ModulateAndAdd)]
    [DataRow(VfxBlendMode.SoftMasked)]
    [DataRow(VfxBlendMode.AlphaComposite)]
    public void FullyFadedParticlesLeaveTheBackgroundUntouched(VfxBlendMode mode)
    {
        foreach (bool mesh in new[] { false, true })
        {
            AssertPixel(DrawFallbackPixel(mesh, mode, VfxOpacitySource.TextureAlpha, 0),
                new Vector4(0.1f, 0.2f, 0.3f, 1), $"{mode}, mesh={mesh}");
        }
    }

    [TestMethod]
    public void AdditiveLuminanceCoverageIsNotAppliedTwice()
    {
        foreach (bool mesh in new[] { false, true })
        {
            AssertPixel(DrawFallbackPixel(mesh, VfxBlendMode.Additive, VfxOpacitySource.TextureLuminance, 0.25f),
                new Vector4(0.18f, 0.23f, 0.31f, 1), $"mesh={mesh}");
        }
    }

    [TestMethod]
    public void ViewDepthSortDoesNotUseOffAxisDistance()
    {
        var farther = new VfxParticle { Position = new Vector3(0, 0, 10) };
        var offAxis = new VfxParticle { Position = new Vector3(100, 0, 2) };
        var particles = new System.Collections.Generic.List<VfxParticle> { offAxis, farther };

        VfxBillboardRenderer.SortParticles(particles, VfxSortMode.ViewProjectionDepth,
            Vector3.Zero, Matrix4x4.Identity, Vector3.UnitZ);

        Assert.AreEqual(farther.Position, particles[0].Position);
    }

    [TestMethod]
    public void TransparentEmittersInterleaveBackToFront()
    {
        var order = VfxRenderOrdering.MergeBackToFront([new float[] { 10, 2 }, new float[] { 8, 4 }]).ToArray();

        CollectionAssert.AreEqual(new[] { (0, 0), (1, 0), (1, 1), (0, 1) }, order);
    }

    [TestMethod]
    public void AuthoredAgeOrderDoesNotDisableOtherEmittersDepthOrder()
    {
        var order = VfxRenderOrdering.MergeBackToFront([new float[] { 2, 10 }, new float[] { 8, 4 }]).ToArray();

        CollectionAssert.AreEqual(new[] { (1, 0), (1, 1), (0, 0), (0, 1) }, order);
    }

    [TestMethod]
    public void EqualDepthsKeepEmitterOrderAndEmptyEmittersAreSkipped()
    {
        var order = VfxRenderOrdering.MergeBackToFront([Array.Empty<float>(), new float[] { 5, 5 }, new float[] { 5 }]).ToArray();

        CollectionAssert.AreEqual(new[] { (1, 0), (1, 1), (2, 0) }, order);
    }

    // Run the production pixel shader and the actual output-merger blend state on D3D's software device.
    // A floating-point target permits verifying the blend calculation independently of display tone mapping.
    private static Vector4 DrawFallbackPixel(bool mesh, VfxBlendMode mode, VfxOpacitySource opacitySource, float particleAlpha)
    {
        using var device = new Device(DriverType.Warp, DeviceCreationFlags.None);
        DeviceContext context = device.ImmediateContext;
        string code = mesh ? VfxMeshRenderer.MeshShader : VfxBillboardRenderer.ParticleShader;
        string colorAssignment = mesh ? "" : $"output.color = float4(0.8, 0.6, 0.4, {particleAlpha.ToString(CultureInfo.InvariantCulture)}); output.worldPos = 0;";
        code += $$"""

VS_OUT VfxTestVS(uint id : SV_VertexID)
{
    VS_OUT output = (VS_OUT)0;
    float2 corner = float2((id << 1) & 2, id & 2);
    output.pos = float4(corner * float2(2, -2) + float2(-1, 1), 0, 1);
    output.normal = float3(0, 0, 1);
    output.uv = float2(0.5, 0.5);
    {{colorAssignment}}
    return output;
}
""";
        using CompilationResult vsCode = ShaderBytecode.Compile(code, "VfxTestVS", "vs_5_0");
        using CompilationResult psCode = ShaderBytecode.Compile(code, "PSMain", "ps_5_0");
        using CompilationResult productionVsCode = ShaderBytecode.Compile(code, "VSMain", "vs_5_0");
        using var vertexShader = new VertexShader(device, vsCode.Bytecode);
        using var pixelShader = new PixelShader(device, psCode.Bytecode);
        using Buffer constants = mesh
            ? CreateConstants(device, new VfxMeshRenderer.MeshConstants
            {
                ParticleColor = new Vector4(0.8f, 0.6f, 0.4f, particleAlpha),
                TintAndClip = new Vector4(1, 1, 1, 0.01f),
                Flags = 1 | (mode == VfxBlendMode.Masked ? 2 : 0) | ((int)opacitySource << 2) | ((int)mode << 5)
            })
            : CreateConstants(device, new MeshRenderContext.WorldConstants
            {
                AmbientColor = new Vector4(1, 1, 1, 0.01f),
                Flags = (RenderContext.ShaderFlags)(VfxBillboardRenderer.MaterialUnlitFlag
                    | (mode == VfxBlendMode.Masked ? VfxBillboardRenderer.MaterialMaskedFlag : 0)
                    | ((int)opacitySource << VfxBillboardRenderer.OpacitySourceShift)
                    | ((int)mode << VfxMaterialBlending.BlendModeShift))
            });

        var description = new Texture2DDescription
        {
            Width = 1, Height = 1, MipLevels = 1, ArraySize = 1,
            Format = Format.R32G32B32A32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource
        };
        using var texture = new Texture2D(device, description);
        Vector4 texel = new(0.4f, 0.2f, 0.1f, 0.5f);
        context.UpdateSubresource(ref texel, texture);
        using var textureView = new ShaderResourceView(device, texture);
        description.BindFlags = BindFlags.RenderTarget;
        using var target = new Texture2D(device, description);
        using var targetView = new RenderTargetView(device, target);
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CpuAccessFlags = CpuAccessFlags.Read;
        using var staging = new Texture2D(device, description);
        var blendDescription = new BlendStateDescription();
        blendDescription.RenderTarget[0] = VfxMaterialBlending.CreateBlendDescription(mode);
        using var blend = new BlendState(device, blendDescription);
        using var sampler = new SamplerState(device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipPoint,
            AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
            ComparisonFunction = Comparison.Never, MaximumLod = float.MaxValue
        });
        using var rasterizer = new RasterizerState(device, new RasterizerStateDescription
        {
            FillMode = FillMode.Solid, CullMode = CullMode.None, IsDepthClipEnabled = true
        });
        context.Rasterizer.State = rasterizer;
        context.Rasterizer.SetViewport(0, 0, 1, 1);
        context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        context.VertexShader.Set(vertexShader);
        context.VertexShader.SetConstantBuffer(0, constants);
        context.PixelShader.Set(pixelShader);
        context.PixelShader.SetConstantBuffer(0, constants);
        context.PixelShader.SetShaderResource(0, textureView);
        context.PixelShader.SetSampler(0, sampler);
        context.OutputMerger.SetRenderTargets(targetView);
        context.OutputMerger.SetBlendState(blend);
        context.ClearRenderTargetView(targetView, new RawColor4(0.1f, 0.2f, 0.3f, 1));
        context.Draw(3, 0);
        context.CopyResource(target, staging);
        var mapped = context.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
        try { return Marshal.PtrToStructure<Vector4>(mapped.DataPointer); }
        finally { context.UnmapSubresource(staging, 0); }
    }

    private static Buffer CreateConstants<T>(Device device, T value) where T : struct
    {
        var buffer = new Buffer(device, SharpDX.Utilities.SizeOf<T>(), ResourceUsage.Default,
            BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        device.ImmediateContext.UpdateSubresource(ref value, buffer);
        return buffer;
    }

    private static void AssertPixel(Vector4 actual, Vector4 expected, string label)
        => Assert.IsTrue(Vector4.Distance(actual, expected) < 0.0001f, $"{label}: expected {expected}, got {actual}");
}
