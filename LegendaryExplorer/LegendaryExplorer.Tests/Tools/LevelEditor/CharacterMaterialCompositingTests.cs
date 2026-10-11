using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using LegendaryExplorer.Resources;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Device = SharpDX.Direct3D11.Device;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class CharacterMaterialCompositingTests
{
    [DataTestMethod]
    [DataRow(EBlendMode.BLEND_Translucent, 1)]
    [DataRow(EBlendMode.BLEND_Translucent, 4)]
    [DataRow(EBlendMode.BLEND_Opaque, 1)]
    [DataRow(EBlendMode.BLEND_Opaque, 4)]
    [DataRow(EBlendMode.BLEND_Masked, 1)]
    [DataRow(EBlendMode.BLEND_Masked, 4)]
    public void HairCompositesOverCompletedOpaqueLightingAndKeepsItsDepthAndPickingContract(EBlendMode blendMode, int samples)
    {
        LegendaryExplorerCore.LegendaryExplorerCoreLib.InitLib(System.Threading.Tasks.TaskScheduler.Default);
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("CharacterHairCompositing.pcc", MEGame.LE3);
        ExportEntry materialExport = package.CreateExport("Hair", "Material", indexed: false);
        materialExport.WriteProperties([
            new EnumProperty(blendMode.ToString(), "EBlendMode", MEGame.LE3, "BlendMode"),
            new EnumProperty("MLM_Unlit", "EMaterialLightingModel", MEGame.LE3, "LightingModel"),
            new BoolProperty(true, "bHairPass"), new BoolProperty(true, "TwoSided")
        ]);
        materialExport.WriteBinary(Material.Create());
        using var scene = new CharacterScene(samples);
        var material = new LEShaderPreviewMaterial(scene.Context, materialExport);
        scene.InjectBaseShaders(material.RenderProxy, blendMode);
        using var preview = new ModelPreview<LEVertex>(scene.Context, scene.CreateHairMesh());
        preview.Materials.Add("Hair", material);
        preview.LODs[0].Sections.Add(new ModelPreviewSection("Hair", 0, 1));
        scene.Context.CurrentHitTestId = new Vector3(192 / 255f, 128 / 255f, 64 / 255f);

        // This is the renderer's real pass order. The typed native material chooses its own pass,
        // shaders, blend state, depth state and hit-proxy draw; the test does not set material.Pass.
        preview.Render(RenderPass.Base, scene.Context, 0);
        preview.Render(RenderPass.Hair, scene.Context, 0);
        preview.Render(RenderPass.Lighting, scene.Context, 0);
        scene.AddOpaqueLighting();
        preview.Render(RenderPass.Translucent, scene.Context, 0);
        Assert.IsTrue(material.RenderProxy.CanRenderWithGameShaders, material.RenderProxy.GameShaderError);

        Vector4[,,] result = scene.ReadSamples();
        Vector3 hair = new(0.75f, 0.125f, 0.25f);
        Vector3 litBody = new(0.625f, 0.5f, 0.5f);
        for (int pixel = 0; pixel < 2; pixel++)
        {
            for (int sample = 0; sample < samples; sample++)
            {
                bool clipped = blendMode == EBlendMode.BLEND_Masked && pixel == 0;
                float opacity = blendMode == EBlendMode.BLEND_Translucent ? (sample + 1f) / (samples == 1 ? 2 : samples) : 1;
                Vector3 expectedColor = clipped ? litBody : hair * opacity + litBody * (1 - opacity);
                AssertVector(new Vector4(expectedColor, 2), result[0, pixel, sample]);
                float expectedDepth = blendMode == EBlendMode.BLEND_Translucent || clipped ? 0.25f : 0.75f;
                Assert.AreEqual(expectedDepth, result[1, pixel, sample].X, 0.00001f,
                    "Translucent hair preserves body depth; opaque and surviving masked hair samples occlude it.");
                Vector3 expectedId = clipped ? new Vector3(32 / 255f, 64 / 255f, 96 / 255f) : scene.Context.CurrentHitTestId;
                AssertVector(new Vector4(expectedId, 1), result[2, pixel, sample]);
            }
        }
    }

    private static void AssertVector(Vector4 expected, Vector4 actual)
    {
        Assert.AreEqual(expected.X, actual.X, 0.0005f);
        Assert.AreEqual(expected.Y, actual.Y, 0.0005f);
        Assert.AreEqual(expected.Z, actual.Z, 0.0005f);
        Assert.AreEqual(expected.W, actual.W, 0.0005f);
    }

    private sealed class CharacterScene : IDisposable
    {
        public MeshRenderContext Context { get; } = new() { UseGameShaders = true, LightingMode = ViewportLightingMode.Unlit };
        private readonly Device device = new(DriverType.Warp);
        private readonly DeviceContext immediate;
        private readonly List<IDisposable> resources = [];
        private readonly int samples;
        private readonly Texture2D color;
        private readonly Texture2D depth;
        private readonly Texture2D hit;
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        public CharacterScene(int sampleCount)
        {
            samples = sampleCount;
            immediate = device.ImmediateContext;
            SetProperty(typeof(RenderContext), nameof(RenderContext.Device), device);
            SetProperty(typeof(RenderContext), nameof(RenderContext.ImmediateContext), immediate);
            SetProperty(typeof(MeshRenderContext), nameof(MeshRenderContext.SampleCount), samples);
            var effect = Own(new LEEffect(device, EmbeddedResources.LevelEditorShader));
            SetProperty(typeof(MeshRenderContext), nameof(MeshRenderContext.LEEffect), effect);
            var rasterizer = Own(new RasterizerState(device, new RasterizerStateDescription
            {
                FillMode = FillMode.Solid, CullMode = CullMode.None, IsDepthClipEnabled = true, IsMultisampleEnabled = samples > 1
            }));
            foreach (string name in new[] { "FillRasterizerState", "CullBackRasterizerState", "CullFrontRasterizerState" }) SetField(name, rasterizer);
            SetProperty(typeof(MeshRenderContext), nameof(MeshRenderContext.DefaultDepthState), Own(new DepthStencilState(device, new DepthStencilStateDescription
            {
                IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.All, DepthComparison = Comparison.Greater
            })));
            SetProperty(typeof(MeshRenderContext), nameof(MeshRenderContext.TranslucentDepthState), Own(new DepthStencilState(device, new DepthStencilStateDescription
            {
                IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.Zero, DepthComparison = Comparison.Greater
            })));
            color = Own(CreateTexture(Format.R16G16B16A16_Float, BindFlags.RenderTarget | BindFlags.ShaderResource));
            hit = Own(CreateTexture(Format.B8G8R8A8_UNorm, BindFlags.RenderTarget | BindFlags.ShaderResource));
            depth = Own(CreateTexture(Format.R32_Typeless, BindFlags.DepthStencil | BindFlags.ShaderResource));
            var colorView = Own(new RenderTargetView(device, color));
            var hitView = Own(new RenderTargetView(device, hit));
            var depthView = Own(new DepthStencilView(device, depth, new DepthStencilViewDescription
            {
                Format = Format.D32_Float,
                Dimension = samples > 1 ? DepthStencilViewDimension.Texture2DMultisampled : DepthStencilViewDimension.Texture2D
            }));
            immediate.OutputMerger.SetRenderTargets(depthView, colorView, hitView);
            immediate.Rasterizer.State = rasterizer;
            immediate.Rasterizer.SetViewport(0, 0, 2, 1);
            immediate.ClearRenderTargetView(colorView, new RawColor4(0.125f, 0.25f, 0.375f, 2));
            immediate.ClearRenderTargetView(hitView, new RawColor4(32 / 255f, 64 / 255f, 96 / 255f, 1));
            immediate.ClearDepthStencilView(depthView, DepthStencilClearFlags.Depth, 0.25f, 0);
        }

        public Mesh<LEVertex> CreateHairMesh() => new(device, [new Triangle(0, 1, 2)],
        [
            Vertex(new Vector3(-1, 1, 0.75f)), Vertex(new Vector3(3, 1, 0.75f)), Vertex(new Vector3(-1, -3, 0.75f))
        ]);

        private static LEVertex Vertex(Vector3 position) => (LEVertex)LEVertex.Create(MEGame.LE3, position, Vector3.UnitX, new Vector4(0, 0, 1, 1), default);

        public void InjectBaseShaders(MaterialRenderProxy material, EBlendMode mode)
        {
            string code = $"#define OPACITY_DIVISOR {(samples == 1 ? 2 : samples)}\n" + (mode == EBlendMode.BLEND_Masked ? "#define MASKED\n" : "") + """
float4 VSMain(float4 position : POSITION) : SV_POSITION { return position; }
float4 PSMain(float4 position : SV_POSITION, uint sampleIndex : SV_SAMPLEINDEX) : SV_TARGET0
{
#ifdef MASKED
    if (position.x < 1) discard;
#endif
    return float4(0.75, 0.125, 0.25, (sampleIndex + 1.0) / OPACITY_DIVISOR);
}
""";
            using var vsCode = ShaderBytecode.Compile(code, "VSMain", "vs_5_0");
            using var psCode = ShaderBytecode.Compile(code, "PSMain", "ps_5_0");
            var vs = new TBasePassVertexShader<FNullPolicy, FNullPolicy>
            {
                Guid = Guid.NewGuid(), ShaderType = "TBasePassVertexShaderFNoLightMapPolicyFNoDensityPolicy", ShaderByteCode = vsCode.Bytecode.Data,
                VertexFactoryParameters = new FVertexFactoryParameterRef { VertexFactoryType = "FLocalVertexFactory", Parameters = new FLocalVertexFactoryShaderParameters() },
                MaterialParameters = new FMaterialVertexShaderParameters { UniformVertexScalarShaderParameters = [], UniformVertexVectorShaderParameters = [] }
            };
            var ps = new TBasePassPixelShader<FNullPolicy>
            {
                Guid = Guid.NewGuid(), ShaderType = "TBasePassPixelShaderFNoLightMapPolicyNoSkyLight", ShaderByteCode = psCode.Bytecode.Data,
                MaterialParameters = new FMaterialPixelShaderParameters
                {
                    UniformPixelScalarShaderParameters = [], UniformPixelVectorShaderParameters = [],
                    UniformPixel2DShaderResourceParameters = [], UniformPixelCubeShaderResourceParameters = []
                }
            };
            material.UnrealVertexShader = material.NoLightMapVertexShader = material.PreviewVertexShader = vs;
            material.UnrealPixelShader = material.NoLightMapPixelShader = material.PreviewPixelShader = ps;
            typeof(MaterialRenderProxy).GetField("gameShaderError", PrivateInstance)!.SetValue(material, null);
            typeof(MaterialRenderProxy).GetField("AttemptedShaderLoad", PrivateInstance)!.SetValue(material, true);
        }

        public void AddOpaqueLighting()
        {
            const string code = """
float4 VSMain(uint id : SV_VertexID) : SV_POSITION
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0.25, 1);
}
float4 PSMain() : SV_TARGET0 { return float4(0.5, 0.25, 0.125, 0); }
""";
            using var vsCode = ShaderBytecode.Compile(code, "VSMain", "vs_5_0");
            using var psCode = ShaderBytecode.Compile(code, "PSMain", "ps_5_0");
            using var vs = new VertexShader(device, vsCode);
            using var ps = new PixelShader(device, psCode);
            using var depthState = new DepthStencilState(device, new DepthStencilStateDescription
            {
                IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.Zero, DepthComparison = Comparison.Equal
            });
            var description = new BlendStateDescription { IndependentBlendEnable = true };
            description.RenderTarget[0] = new RenderTargetBlendDescription
            {
                IsBlendEnabled = true, SourceBlend = BlendOption.One, DestinationBlend = BlendOption.One,
                BlendOperation = BlendOperation.Add, SourceAlphaBlend = BlendOption.Zero, DestinationAlphaBlend = BlendOption.One,
                AlphaBlendOperation = BlendOperation.Add, RenderTargetWriteMask = ColorWriteMaskFlags.Red | ColorWriteMaskFlags.Green | ColorWriteMaskFlags.Blue
            };
            using var blend = new BlendState(device, description);
            immediate.InputAssembler.InputLayout = null;
            immediate.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            immediate.VertexShader.Set(vs);
            immediate.PixelShader.Set(ps);
            immediate.OutputMerger.SetBlendState(blend);
            immediate.OutputMerger.SetDepthStencilState(depthState);
            immediate.Draw(3, 0);
        }

        public Vector4[,,] ReadSamples()
        {
            string code = $"#define SAMPLES {samples}\n" + """
#if SAMPLES > 1
Texture2DMS<float4, SAMPLES> ColorSource : register(t0);
Texture2DMS<float, SAMPLES> DepthSource : register(t1);
Texture2DMS<float4, SAMPLES> HitSource : register(t2);
#define LOAD(source, pixel, sampleIndex) source.Load(int2(pixel, 0), sampleIndex)
#else
Texture2D<float4> ColorSource : register(t0);
Texture2D<float> DepthSource : register(t1);
Texture2D<float4> HitSource : register(t2);
#define LOAD(source, pixel, sampleIndex) source.Load(int3(pixel, 0, 0))
#endif
float4 VSMain(uint id : SV_VertexID) : SV_POSITION
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}
float4 PSMain(float4 position : SV_POSITION) : SV_TARGET0
{
    int column = (int)position.x;
    int sampleIndex = column % SAMPLES;
    int pixel = column / SAMPLES;
    int row = (int)position.y;
    if (row == 0) return LOAD(ColorSource, pixel, sampleIndex);
    if (row == 1) return float4(LOAD(DepthSource, pixel, sampleIndex), 0, 0, 1);
    return LOAD(HitSource, pixel, sampleIndex);
}
""";
            using var vsCode = ShaderBytecode.Compile(code, "VSMain", "vs_5_0");
            using var psCode = ShaderBytecode.Compile(code, "PSMain", "ps_5_0");
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
                Width = samples * 2, Height = 3, MipLevels = 1, ArraySize = 1, Format = Format.R32G32B32A32_Float,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget
            };
            using var output = new Texture2D(device, description);
            using var view = new RenderTargetView(device, output);
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CpuAccessFlags = CpuAccessFlags.Read;
            using var staging = new Texture2D(device, description);
            using var rasterizer = new RasterizerState(device, new RasterizerStateDescription { FillMode = FillMode.Solid, CullMode = CullMode.None, IsDepthClipEnabled = true });
            immediate.ClearState();
            immediate.OutputMerger.SetRenderTargets(view);
            immediate.Rasterizer.State = rasterizer;
            immediate.Rasterizer.SetViewport(0, 0, samples * 2, 3);
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
                var result = new Vector4[3, 2, samples];
                for (int row = 0; row < 3; row++)
                    for (int pixel = 0; pixel < 2; pixel++)
                        for (int sample = 0; sample < samples; sample++)
                            result[row, pixel, sample] = Marshal.PtrToStructure<Vector4>(IntPtr.Add(mapped.DataPointer, row * mapped.RowPitch + (pixel * samples + sample) * 16));
                return result;
            }
            finally { immediate.UnmapSubresource(staging, 0); }
        }

        private Texture2D CreateTexture(Format format, BindFlags binds) => new(device, new Texture2DDescription
        {
            Width = 2, Height = 1, MipLevels = 1, ArraySize = 1, Format = format,
            SampleDescription = new SampleDescription(samples, 0), Usage = ResourceUsage.Default, BindFlags = binds
        });
        private T Own<T>(T resource) where T : IDisposable { resources.Add(resource); return resource; }
        private void SetProperty(Type type, string name, object value) => type.GetProperty(name)!.SetValue(Context, value);
        private void SetField(string name, object value) => typeof(MeshRenderContext).GetField(name, PrivateInstance)!.SetValue(Context, value);
        public void Dispose()
        {
            immediate.ClearState();
            Context.EmptyCaches();
            for (int i = resources.Count - 1; i >= 0; i--) resources[i].Dispose();
            immediate.Dispose();
            device.Dispose();
        }
    }
}
