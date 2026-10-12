using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using LegendaryExplorer.Resources;
using LegendaryExplorer.Tools.AssetDatabase.VFXPreview;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpDX.Direct3D;
using Device = SharpDX.Direct3D11.Device;
using NativeSetters = LegendaryExplorer.Tools.LevelEditor.Scene3D.ShaderParameterSetters;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class WorldMappedMaterialTests
{
    // LIQ_Ground01_MatInst's LE2 ground pixel shader binds CameraWorldPosition at byte 64.
    private const int CameraOffset = 64;
    private const int CameraBytes = 3 * sizeof(float);
    private const byte Sentinel = 0xCD;
    private const float TerrainTextureScale = 1200f;

    [DataTestMethod]
    [DataRow("FirstPerson")]
    [DataRow("Orbit")]
    [DataRow("Orthographic")]
    public void GroundWorldMappingStaysAnchoredAcrossCameraMoves(string cameraMode)
    {
        using var fixture = new MaterialFixture();
        MeshRenderContext context = fixture.Context;
        context.UseCameraRelativeNativeRendering = true;
        ConfigureCamera(context.Camera, cameraMode);
        using var mesh = new Mesh<LEVertex>(null, [], [])
        {
            LocalToWorld = Matrix4x4.CreateScale(1.5f, 0.75f, 2f)
                           * Matrix4x4.CreateRotationZ(0.4f)
                           * Matrix4x4.CreateTranslation(-33000, 12500, 6400)
        };
        Vector3[] localPoints = [new(7, 11, 3), new(-240, 375, 40), new(100, -75, 12)];
        Vector3[] expectedWorldPoints = localPoints.Select(point => Vector3.Transform(point, mesh.LocalToWorld)).ToArray();

        for (int move = 0; move < 3; move++)
        {
            MoveCamera(context.Camera, move);
            context.LEEffect.BeginFrame(context);
            LEVSConstants shared = VfxGameShaderRenderer.CreateNativeVertexConstants(context);
            Assert.AreEqual(new Vector4(Vector3.Zero, 1), shared.CameraPosition,
                "The vertex shader's view direction must keep the camera-relative origin.");
            Assert.AreEqual(Vector4.Zero, shared.PreViewTranslation);

            byte[] factoryBuffer = new byte[64];
            var factoryParameters = new FLocalVertexFactoryShaderParameters
            {
                LocalToWorld = new FShaderParameter { BaseIndex = 0, NumBytes = 64 }
            };
            NativeSetters.WriteValues(factoryParameters, factoryBuffer, context, mesh, fixture.Material);
            Matrix4x4 shaderLocalToWorld = MemoryMarshal.Read<Matrix4x4>(factoryBuffer);
            AssertVector(mesh.LocalToWorld.Translation - context.Camera.EyePosition, shaderLocalToWorld.Translation, 0.01f);

            (Vector3 vertexCamera, Vector3 pixelCamera) = WriteMaterialCameras(fixture, mesh);
            AssertVector(context.Camera.EyePosition, vertexCamera, 0);
            AssertVector(context.Camera.EyePosition, pixelCamera, 0);

            for (int pointIndex = 0; pointIndex < localPoints.Length; pointIndex++)
            {
                // The cooked ground VS passes LocalToWorld position directly through TEXCOORD5.
                // The PS adds material CameraWorldPosition before dividing by Texture Scale
                // for triplanar sampling. Moving the camera must not change that world coordinate.
                Vector3 varying = Vector3.Transform(localPoints[pointIndex], shaderLocalToWorld);
                Vector3 reconstructedWorld = varying + pixelCamera;
                AssertVector(expectedWorldPoints[pointIndex], reconstructedWorld, 0.01f);
                AssertVector(expectedWorldPoints[pointIndex] / TerrainTextureScale,
                    reconstructedWorld / TerrainTextureScale, 0.00001f);

                Vector4 expectedClip = Vector4.Transform(new Vector4(expectedWorldPoints[pointIndex], 1), context.Camera.ViewProjectionMatrix);
                Vector4 actualClip = Vector4.Transform(new Vector4(varying, 1), shared.ViewProjectionMatrix);
                Assert.IsTrue(Vector4.Distance(expectedClip, actualClip) < 0.01f,
                    $"Camera-relative placement changed in {cameraMode}, move {move}: expected {expectedClip}, got {actualClip}.");
            }
        }
    }

    [DataTestMethod]
    [DataRow("FirstPerson")]
    [DataRow("Orbit")]
    [DataRow("Orthographic")]
    public void AbsoluteRenderingMaterialParametersStillUseTheActualEye(string cameraMode)
    {
        using var fixture = new MaterialFixture();
        ConfigureCamera(fixture.Context.Camera, cameraMode);
        MoveCamera(fixture.Context.Camera, 1);
        fixture.Context.LEEffect.BeginFrame(fixture.Context);
        using var mesh = new Mesh<LEVertex>(null, [], []);

        (Vector3 vertexCamera, Vector3 pixelCamera) = WriteMaterialCameras(fixture, mesh);

        AssertVector(fixture.Context.Camera.EyePosition, vertexCamera, 0);
        AssertVector(fixture.Context.Camera.EyePosition, pixelCamera, 0);
        Assert.AreEqual(new Vector4(fixture.Context.Camera.EyePosition, 1),
            VfxGameShaderRenderer.CreateNativeVertexConstants(fixture.Context).CameraPosition);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LensFlareMaterialParametersKeepTheirExplicitCameraOverride(bool cameraRelative)
    {
        using var fixture = new MaterialFixture();
        ConfigureCamera(fixture.Context.Camera, "Orbit");
        MoveCamera(fixture.Context.Camera, 1);
        fixture.Context.UseCameraRelativeNativeRendering = cameraRelative;
        fixture.Context.LEEffect.BeginFrame(fixture.Context);
        typeof(MaterialRenderProxy).GetProperty(nameof(MaterialRenderProxy.VertexFactoryType))!
            .SetValue(fixture.Material, "FLensFlareVertexFactory");
        Vector3 factoryEye = new(-33750, 11900, 6350);
        fixture.Material.LensFlareCameraPosition = factoryEye;
        using var mesh = new Mesh<LEVertex>(null, [], []);

        (Vector3 vertexCamera, Vector3 pixelCamera) = WriteMaterialCameras(fixture, mesh);

        AssertVector(factoryEye, vertexCamera, 0);
        AssertVector(factoryEye, pixelCamera, 0);
        Assert.AreEqual(new Vector4(factoryEye, 1),
            VfxGameShaderRenderer.CreateNativeVertexConstants(fixture.Context, factoryEye).CameraPosition);
    }

    private static (Vector3 vertex, Vector3 pixel) WriteMaterialCameras(MaterialFixture fixture, Mesh<LEVertex> mesh)
    {
        var binding = new FShaderParameter { BaseIndex = CameraOffset, NumBytes = CameraBytes };
        var vertexParameters = new FMaterialVertexShaderParameters
        {
            CameraWorldPosition = binding,
            UniformVertexScalarShaderParameters = [], UniformVertexVectorShaderParameters = []
        };
        var pixelParameters = new FMaterialPixelShaderParameters
        {
            CameraWorldPosition = binding,
            UniformPixelScalarShaderParameters = [], UniformPixelVectorShaderParameters = [],
            UniformPixel2DShaderResourceParameters = [], UniformPixelCubeShaderResourceParameters = []
        };
        byte[] vertexBuffer = Enumerable.Repeat(Sentinel, 128).ToArray();
        byte[] pixelBuffer = Enumerable.Repeat(Sentinel, 128).ToArray();
        NativeSetters.WriteValues(ref vertexParameters, vertexBuffer, fixture.Context, mesh, fixture.Material);
        NativeSetters.WriteValues(ref pixelParameters, pixelBuffer, fixture.Context, mesh, fixture.Material);
        AssertUnboundConstantsUntouched(vertexBuffer);
        AssertUnboundConstantsUntouched(pixelBuffer);
        return (MemoryMarshal.Read<Vector3>(vertexBuffer.AsSpan(CameraOffset)),
            MemoryMarshal.Read<Vector3>(pixelBuffer.AsSpan(CameraOffset)));
    }

    private static void ConfigureCamera(SceneCamera camera, string cameraMode)
    {
        camera.FirstPerson = cameraMode == "FirstPerson";
        camera.IsOrthographic = cameraMode == "Orthographic";
        camera.FocusDepth = 1800;
        camera.aspect = 1.5f;
    }

    private static void MoveCamera(SceneCamera camera, int move)
    {
        camera.FocusDepth = 1800 + move * 250;
        camera.Position = new Vector3(-33723 + move * 600, 11841 - move * 350, 6958 + move * 100);
        camera.Pitch = -0.3f + move * 0.1f;
        camera.Yaw = 0.4f + move * 0.7f;
    }

    private static void AssertVector(Vector3 expected, Vector3 actual, float tolerance)
        => Assert.IsTrue(Vector3.Distance(expected, actual) <= tolerance, $"Expected {expected}, got {actual}.");

    private static void AssertUnboundConstantsUntouched(byte[] buffer)
    {
        Assert.IsTrue(buffer.Take(CameraOffset).All(value => value == Sentinel));
        Assert.IsTrue(buffer.Skip(CameraOffset + CameraBytes).All(value => value == Sentinel));
    }

    private sealed class MaterialFixture : IDisposable
    {
        private readonly IMEPackage package;
        private readonly Device device = new(DriverType.Warp);
        public MeshRenderContext Context { get; } = new();
        public MaterialRenderProxy Material { get; }

        public MaterialFixture()
        {
            typeof(RenderContext).GetProperty(nameof(RenderContext.Device))!.GetSetMethod(true)!.Invoke(Context, [device]);
            typeof(RenderContext).GetProperty(nameof(RenderContext.ImmediateContext))!.GetSetMethod(true)!
                .Invoke(Context, [device.ImmediateContext]);
            typeof(MeshRenderContext).GetProperty(nameof(MeshRenderContext.LEEffect))!.GetSetMethod(true)!
                .Invoke(Context, [new LEEffect(device, EmbeddedResources.LevelEditorShader)]);

            LegendaryExplorerCore.LegendaryExplorerCoreLib.InitLib(System.Threading.Tasks.TaskScheduler.Default);
            package = MEPackageHandler.CreateMemoryEmptyPackage("WorldMappedTerrain.pcc", MEGame.LE2);
            // An orphan MIC exercises the production material setters without an installed game
            // or an external cooked shader cache. No uniform expressions or textures are needed.
            ExportEntry export = package.CreateExport("Terrain", "MaterialInstanceConstant", indexed: false);
            export.WriteProperties([]);
            Material = new MaterialRenderProxy(Context, export);
        }

        public void Dispose()
        {
            package.Dispose();
            Context.LEEffect.Dispose();
            Context.ImmediateContext.Dispose();
            device.Dispose();
        }
    }
}
