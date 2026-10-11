using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using LegendaryExplorer.Misc;
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

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class LevelEditorUpstreamRenderingTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReverseDepthKeepsNearInFrontOfFar(bool orthographic)
    {
        var camera = new SceneCamera { IsOrthographic = orthographic, ZNear = 1, ZFar = 10000 };
        var near = Vector4.Transform(new Vector4(0, 0, camera.ZNear, 1), camera.ProjectionMatrix);
        var far = Vector4.Transform(new Vector4(0, 0, camera.ZFar, 1), camera.ProjectionMatrix);
        Assert.AreEqual(1f, near.Z / near.W, 0.00001f);
        Assert.AreEqual(0f, far.Z / far.W, 0.00001f);
    }

    [DataTestMethod]
    [DataRow(MEGame.LE3, 0.5f)]
    [DataRow(MEGame.ME3, 127.5f)]
    public void AnimatedTangentsRetainUvColorAndGamePacking(MEGame game, float encodedZero)
    {
        Fixed4<Vector4> uvs = default;
        uvs[0] = new Vector4(0.1f, 0.2f, 0.3f, 0.4f);
        uvs[1] = Vector4.One;
        LEVertex original = ((LEVertex)LEVertex.Create(game, Vector3.Zero, Vector3.UnitX,
            new Vector4(0, 0, 1, -1), uvs)).WithColor(new Vector4(0.25f, 0.5f, 0.75f, 1));
        LEVertex rotated = original.WithPositionNormalAndTangent(game, Vector3.One,
            new Vector4(0, 0, 1, -1), Vector3.UnitY);
        var before = new float[LEVertex.Stride / sizeof(float)];
        var after = new float[before.Length];
        original.ToFloats(before);
        rotated.ToFloats(after);
        CollectionAssert.AreEqual(before.Skip(11).ToArray(), after.Skip(11).ToArray());
        Assert.AreEqual(encodedZero, after[4]);
        Assert.AreEqual(encodedZero * 2, after[5]);
        Assert.AreEqual(encodedZero, after[6]);
        Assert.AreEqual(0f, after[10]);
        Assert.AreEqual(Vector3.One, rotated.Position);
    }

    [TestMethod]
    public void OrbitCameraRelativeNativeCoordinatesPreserveProjection()
    {
        var context = new MeshRenderContext();
        context.Camera.FirstPerson = false;
        context.Camera.Position = new Vector3(10000, -20000, 30000);
        context.Camera.Yaw = 0.4f;
        context.Camera.Pitch = 0.2f;
        context.Camera.FocusDepth = 100;
        var model = Matrix4x4.CreateTranslation(context.Camera.Position);
        Vector4 absolute = Vector4.Transform(Vector4.UnitW, model * context.Camera.ViewMatrix);
        context.UseCameraRelativeNativeRendering = true;
        Vector4 relative = Vector4.Transform(Vector4.UnitW,
            context.GetNativeShaderLocalToWorld(model) * context.GetNativeShaderViewMatrix());
        Assert.IsTrue(Vector4.Distance(absolute, relative) < 0.01f);
    }

    [TestMethod]
    public void MeshVertexColorDefaultsToWhite()
    {
        var vertex = (LEVertex)LEVertex.Create(MEGame.LE3, Vector3.Zero, Vector3.UnitX,
            new Vector4(0, 0, 1, 1), default);
        var stream = new float[LEVertex.Stride / sizeof(float)];
        vertex.ToFloats(stream);
        CollectionAssert.AreEqual(new[] { 1f, 1f, 1f, 1f }, stream.Skip(11).Take(4).ToArray());
    }

    [TestMethod]
    public void LightMapPolicyRequiresMatchingVertexAndPixelShaders()
    {
        Shader lightMapVertex = new TBasePassVertexShader<FLightMapTexturePolicy.VertexParametersType, FNullPolicy>
        { ShaderType = "TBasePassVertexShaderFDirectionalLightMapTexturePolicyFNoDensityPolicy" };
        Shader lightMapPixel = new TBasePassPixelShader<FLightMapTexturePolicy.PixelParametersType>
        { ShaderType = "TBasePassPixelShaderFDirectionalLightMapTexturePolicyNoSkyLight" };
        Shader previewVertex = new TBasePassVertexShader<FNullPolicy, FNullPolicy>
        { ShaderType = "TBasePassVertexShaderFNoLightMapPolicyFNoDensityPolicy" };
        Shader previewPixel = new TBasePassPixelShader<FNullPolicy>
        { ShaderType = "TBasePassPixelShaderFNoLightMapPolicySkyLight" };
        var selected = MaterialRenderProxy.SelectShaders([previewVertex, previewPixel, lightMapVertex, lightMapPixel],
            false, ELightMapType.LMT_2D, out bool usesLightMap);
        Assert.IsTrue(usesLightMap);
        Assert.AreSame(lightMapVertex, selected.vertexShader);
        Assert.AreSame(lightMapPixel, selected.pixelShader);
        selected = MaterialRenderProxy.SelectShaders([previewVertex, previewPixel, lightMapVertex],
            false, ELightMapType.LMT_2D, out usesLightMap);
        Assert.IsFalse(usesLightMap);
        Assert.AreSame(previewVertex, selected.vertexShader);
        Assert.AreSame(previewPixel, selected.pixelShader);
    }

    [DataTestMethod]
    [DataRow("VSMain", "vs_5_0", 1)]
    [DataRow("VSMainLEVertex", "vs_5_0", 1)]
    [DataRow("PSMain", "ps_5_0", 1)]
    [DataRow("PSMainHitProxy", "ps_5_0", 1)]
    [DataRow("VSMainResolve", "vs_5_0", 1)]
    [DataRow("PSMainResolve", "ps_5_0", 1)]
    [DataRow("PSMainResolve", "ps_5_0", 4)]
    public void LevelShaderEntryPointsCompile(string entryPoint, string profile, int samples)
    {
        using var shader = ShaderBytecode.Compile($"#define MSAA_SAMPLES {samples}\n" + EmbeddedResources.LevelEditorShader,
            entryPoint, profile);
        Assert.IsTrue(shader.Bytecode.Data.Length > 0);
    }

    [TestMethod]
    public void TextureChannelInspectionRetainsDiffuseShaderDiagnostics()
    {
        var context = new MeshRenderContext();
        Assert.IsTrue(context.HasDefaultTextureChannels);
        context.RenderFlags &= ~RenderContext.ShaderFlags.EnableRedChannel;
        Assert.IsFalse(context.HasDefaultTextureChannels);
        context.RenderFlags |= RenderContext.ShaderFlags.EnableRedChannel;
        context.RenderFlags |= RenderContext.ShaderFlags.Selected;
        Assert.IsTrue(context.HasDefaultTextureChannels);
        context.RenderFlags &= ~RenderContext.ShaderFlags.EnableAlphaChannel;
        Assert.IsFalse(context.HasDefaultTextureChannels);
    }

    [DataTestMethod]
    [DataRow(false, 0f)]
    [DataRow(true, 1f)]
    public void NativeMeshPipelineKeepsHdrOutputAndLegacyCoverageSeparate(bool legacyPipeline, float expectedAlpha)
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
        using var device = new SharpDX.Direct3D11.Device(DriverType.Warp);
        DeviceContext context = device.ImmediateContext;
        MeshRenderContext renderContext = legacyPipeline ? new LegacyColorRenderContext() : new MeshRenderContext();
        // Supply the software test device without creating an interactive viewport or hardware renderer.
        typeof(RenderContext).GetProperty(nameof(RenderContext.Device)).SetValue(renderContext, device);
        using var vertexBytecode = ShaderBytecode.Compile(shader, "VSMain", "vs_5_0");
        using var pixelBytecode = ShaderBytecode.Compile(shader, "PSMain", "ps_5_0");
        using var vertexShader = new VertexShader(device, vertexBytecode);
        PixelShader pixelShader = renderContext.GetCachedMeshPixelShader(
            Guid.NewGuid(), pixelBytecode.Bytecode.Data, useSrgbColorManagement: false);
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

    [TestMethod]
    public void BakedLightInteractionsRemainAvailableToMaterialsWithoutTheCookedMapPolicy()
    {
        LegendaryExplorerCore.LegendaryExplorerCoreLib.InitLib(System.Threading.Tasks.TaskScheduler.Default);
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("MappedLightFallback.pcc", MEGame.LE3);
        ExportEntry meshExport = package.CreateExport("Mesh", "StaticMesh", indexed: false);
        meshExport.WriteProperties([]);
        ExportEntry component = package.CreateExport("Component", "StaticMeshComponent", indexed: false);
        component.WriteProperties([]);
        Guid bakedGuid = Guid.NewGuid();
        component.WriteBinary(new StaticMeshComponent
        {
            LODData = [new StaticMeshComponentLODInfo
            {
                ShadowMaps = [], ShadowVertexBuffers = [],
                LightMap = new LightMap_1D
                {
                    LightMapType = ELightMapType.LMT_1D, LightGuids = [bakedGuid],
                    DirectionalSamples = [new QuantizedDirectionalLightSample()], SimpleSamples = [],
                    ScaleVector1 = Vector3.One, ScaleVector2 = Vector3.One, ScaleVector3 = Vector3.One
                }
            }]
        });
        var mesh = new StaticMesh
        {
            Export = meshExport,
            LODModels = [new StaticMeshRenderData
            {
                NumVertices = 1,
                VertexBuffer = new StaticMeshVertexBuffer { NumTexCoords = 0, VertexData = [] }
            }]
        };
        var context = new MeshRenderContext();
        using var device = new SharpDX.Direct3D11.Device(DriverType.Warp);
        typeof(RenderContext).GetProperty(nameof(RenderContext.Device)).SetValue(context, device);
        try
        {
            SceneLight baked = AddLight("Baked", bakedGuid);
            SceneLight dynamic = AddLight("Unbaked", Guid.NewGuid());
            context.AddLights([baked, dynamic]);
            using MeshStaticLighting lighting = MeshStaticLighting.Create(context, component, mesh, 0);
            Assert.IsNotNull(lighting);
            Assert.AreEqual(ELightMapType.LMT_1D, lighting.LightMapType);
            var bounds = new BoxSphereBounds { Origin = Vector3.Zero, BoxExtent = Vector3.One, SphereRadius = 1 };
            CollectionAssert.AreEqual(new[] { dynamic }, lighting.GetLightInteractions(bounds).Select(interaction => interaction.Light).ToArray());
            CollectionAssert.AreEquivalent(new[] { baked, dynamic }, lighting.GetLightInteractions(bounds, includeLightMappedLights: true)
                .Select(interaction => interaction.Light).ToArray());
            // A fallback draw must not change the cached normal draw and double-count its baked light.
            CollectionAssert.AreEqual(new[] { dynamic }, lighting.GetLightInteractions(bounds).Select(interaction => interaction.Light).ToArray());
            Assert.HasCount(0, lighting.GetLightInteractions(new BoxSphereBounds { Origin = new Vector3(10000), BoxExtent = Vector3.One, SphereRadius = 1 }, includeLightMappedLights: true));
        }
        finally { context.EmptyCaches(); }

        SceneLight AddLight(string name, Guid mapGuid)
        {
            ExportEntry actor = package.CreateExport(name, "PointLight", indexed: false);
            ExportEntry lightComponent = package.CreateExport(name + "Component", "PointLightComponent", actor, indexed: false);
            lightComponent.WriteProperties([
                new FloatProperty(1024, "Radius"), new FloatProperty(1, "Brightness"),
                CommonStructs.GuidProp(Guid.NewGuid(), "LightGuid"), CommonStructs.GuidProp(mapGuid, "LightmapGuid"),
                CommonStructs.MatrixProp(Matrix4x4.Identity, "CachedParentToWorld")
            ]);
            return SceneLight.Create(lightComponent, "PointLight", context.PackageCache, Matrix4x4.Identity);
        }
    }

    [TestMethod]
    public void ExistingUnlitViewportFlagSuppressesLevelLightsAndModulatedShadows()
    {
        var context = new MeshRenderContext();
        context.AddLights([new SceneLight(Vector3.Zero, 1024, Vector3.One, 1, false, Vector3.UnitZ, 0, 0)]);
        Assert.IsTrue(context.IsLevelLightingActive);
        context.RenderFlags |= RenderContext.ShaderFlags.Unlit;
        Assert.IsTrue(context.IsUnlit);
        Assert.IsFalse(context.IsLevelLightingActive);
        Assert.IsFalse(context.AreModulatedShadowsActive);
        context.RenderFlags &= ~RenderContext.ShaderFlags.Unlit;
        Assert.IsTrue(context.IsLevelLightingActive);
    }

    [DataTestMethod]
    [DataRow(true, true, true)]
    [DataRow(true, true, false)]
    [DataRow(true, false, false)]
    [DataRow(false, false, false)]
    public void NativeHitProxyPreservesMaskedCoverageAndNonWritingDepthModes(bool depthEnabled, bool writesDepth, bool masked)
    {
        string shader = (masked ? "#define MASKED\n" : "") + """
float4 VSMain(uint id : SV_VertexID) : SV_POSITION
{
    float2 corner = float2((id << 1) & 2, id & 2);
    return float4(corner * float2(2, -2) + float2(-1, 1), 0.5, 1);
}
float4 PSMain(float4 position : SV_POSITION) : SV_TARGET0
{
#ifdef MASKED
    if (position.x < 1) discard;
#endif
    return float4(0.25, 0.5, 0.75, 0);
}
""";
        using var device = new SharpDX.Direct3D11.Device(DriverType.Warp);
        DeviceContext context = device.ImmediateContext;
        using var effect = new LEEffect(device, EmbeddedResources.LevelEditorShader);
        using var vertexBytecode = ShaderBytecode.Compile(shader, "VSMain", "vs_5_0");
        using var pixelBytecode = ShaderBytecode.Compile(shader, "PSMain", "ps_5_0");
        using var vertexShader = new VertexShader(device, vertexBytecode);
        using var pixelShader = new PixelShader(device, pixelBytecode);
        using var indices = SharpDX.Direct3D11.Buffer.Create(device, BindFlags.IndexBuffer, new[] { 0, 1, 2 });
        var description = new Texture2DDescription
        {
            Width = 2, Height = 1, MipLevels = 1, ArraySize = 1,
            Format = Format.R32G32B32A32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget
        };
        using var color = new Texture2D(device, description);
        using var colorView = new RenderTargetView(device, color);
        using var hit = new Texture2D(device, description);
        using var hitView = new RenderTargetView(device, hit);
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CpuAccessFlags = CpuAccessFlags.Read;
        using var staging = new Texture2D(device, description);
        description.Format = Format.D32_Float;
        description.Usage = ResourceUsage.Default;
        description.BindFlags = BindFlags.DepthStencil;
        description.CpuAccessFlags = CpuAccessFlags.None;
        using var depth = new Texture2D(device, description);
        using var depthView = new DepthStencilView(device, depth);
        using var baseDepthState = new DepthStencilState(device, new DepthStencilStateDescription
        {
            IsDepthEnabled = depthEnabled,
            DepthWriteMask = writesDepth ? DepthWriteMask.All : DepthWriteMask.Zero,
            DepthComparison = Comparison.Greater,
            IsStencilEnabled = false
        });
        using var rasterizer = new RasterizerState(device, new RasterizerStateDescription
        {
            FillMode = FillMode.Solid, CullMode = CullMode.None, IsDepthClipEnabled = true
        });
        try
        {
            context.Rasterizer.State = rasterizer;
            context.Rasterizer.SetViewport(0, 0, 2, 1);
            context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            context.InputAssembler.SetIndexBuffer(indices, Format.R32_UInt, 0);
            context.VertexShader.Set(vertexShader);
            context.PixelShader.Set(pixelShader);
            context.OutputMerger.SetRenderTargets(depthView, colorView, hitView);
            context.OutputMerger.SetDepthStencilState(baseDepthState);
            context.ClearRenderTargetView(colorView, new RawColor4(0.1f, 0.2f, 0.3f, 1));
            context.ClearRenderTargetView(hitView, new RawColor4(0, 0, 0, 0));
            // Disabled-depth materials must remain selectable even in front of a closer depth value.
            context.ClearDepthStencilView(depthView, DepthStencilClearFlags.Depth, depthEnabled ? 0 : 1, 0);
            context.DrawIndexed(3, 0, 0);
            var hitConstants = new LEHitProxyConstants
            {
                HitProxyID = new Vector3(0.3f, 0.4f, 0.5f),
                Flags = RenderContext.ShaderFlags.Selected
            };
            if (depthEnabled) effect.RenderHitProxy(context, hitConstants, 0, 3, baseDepthState);
            else effect.RenderHitProxy(context, hitConstants, 0, 3); // Existing API infers disabled depth.
            context.ClearState();

            Vector4[] colors = ReadPixels(color);
            Vector4[] hits = ReadPixels(hit);
            AssertPixel(new Vector4(0.2f, 0.4f, 1.2f, 2), colors[1]);
            AssertPixel(new Vector4(hitConstants.HitProxyID, 1), hits[1]);
            if (masked)
            {
                // The clipped pixel keeps its background color/alpha and never becomes clickable.
                AssertPixel(new Vector4(0.1f, 0.2f, 0.3f, 1), colors[0]);
                AssertPixel(Vector4.Zero, hits[0]);
            }
            else
            {
                AssertPixel(colors[1], colors[0]);
                AssertPixel(hits[1], hits[0]);
            }
        }
        finally { context.ClearState(); }

        Vector4[] ReadPixels(Texture2D target)
        {
            context.CopyResource(target, staging);
            var mapped = context.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            try
            {
                return [Marshal.PtrToStructure<Vector4>(mapped.DataPointer),
                    Marshal.PtrToStructure<Vector4>(IntPtr.Add(mapped.DataPointer, Marshal.SizeOf<Vector4>()))];
            }
            finally { context.UnmapSubresource(staging, 0); }
        }
        static void AssertPixel(Vector4 expected, Vector4 actual)
        {
            Assert.AreEqual(expected.X, actual.X, 0.00001f);
            Assert.AreEqual(expected.Y, actual.Y, 0.00001f);
            Assert.AreEqual(expected.Z, actual.Z, 0.00001f);
            Assert.AreEqual(expected.W, actual.W, 0.00001f);
        }
    }

    private sealed class LegacyColorRenderContext : MeshRenderContext
    {
        public override bool UsesLegacyNativeMeshColorPipeline => true;
    }

}
