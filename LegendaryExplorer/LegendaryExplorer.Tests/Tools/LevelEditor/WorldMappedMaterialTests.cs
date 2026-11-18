using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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

    /// <summary>
    /// Game shaders work in upstream's translated world space (FSceneView::PreViewTranslation = -eye for a perspective view): the factory's LocalToWorld is
    /// translated, the frame's view-projection undoes it, and the material's CameraWorldPosition (the view origin) restores world positions for
    /// world-mapped (triplanar) materials. Moving the camera must not move them.
    /// </summary>
    [DataTestMethod]
    [DataRow("FirstPerson")]
    [DataRow("Orbit")]
    public void GroundWorldMappingStaysAnchoredAcrossCameraMoves(string cameraMode)
    {
        using var fixture = new WarpMaterialFixture();
        MeshRenderContext context = fixture.Context;
        ConfigureCamera(context.Camera, cameraMode);
        using var mesh = CreateGroundMesh();
        Vector3[] localPoints = [new(7, 11, 3), new(-240, 375, 40), new(100, -75, 12)];
        Vector3[] expectedWorldPoints = localPoints.Select(point => Vector3.Transform(point, mesh.LocalToWorld)).ToArray();

        for (int move = 0; move < 3; move++)
        {
            MoveCamera(context.Camera, move);
            context.LEEffect.BeginFrame(context);
            Assert.AreEqual(-context.Camera.EyePosition, context.LEEffect.PreViewTranslation);

            Matrix4x4 shaderLocalToWorld = WriteLocalToWorld(fixture, mesh);
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
                AssertClipPlacement(context, expectedWorldPoints[pointIndex], varying, $"{cameraMode}, move {move}");
            }
        }
    }

    /// <summary>
    /// An orthographic view isn't translated (its FSceneView::ViewOrigin is a direction): the factory's LocalToWorld is the primitive's own
    /// </summary>
    [TestMethod]
    public void OrthographicViewsAreNotTranslated()
    {
        using var fixture = new WarpMaterialFixture();
        MeshRenderContext context = fixture.Context;
        ConfigureCamera(context.Camera, "Orthographic");
        using var mesh = CreateGroundMesh();

        for (int move = 0; move < 3; move++)
        {
            MoveCamera(context.Camera, move);
            context.LEEffect.BeginFrame(context);
            Assert.AreEqual(Vector3.Zero, context.LEEffect.PreViewTranslation);

            Matrix4x4 shaderLocalToWorld = WriteLocalToWorld(fixture, mesh);
            AssertVector(mesh.LocalToWorld.Translation, shaderLocalToWorld.Translation, 0.01f);
            Vector3 point = new(-240, 375, 40);
            AssertClipPlacement(context, Vector3.Transform(point, mesh.LocalToWorld), Vector3.Transform(point, shaderLocalToWorld),
                $"Orthographic, move {move}");
        }
    }

    [DataTestMethod]
    [DataRow("FirstPerson")]
    [DataRow("Orbit")]
    public void MaterialParametersUseTheActualEye(string cameraMode)
    {
        using var fixture = new WarpMaterialFixture();
        ConfigureCamera(fixture.Context.Camera, cameraMode);
        MoveCamera(fixture.Context.Camera, 1);
        fixture.Context.LEEffect.BeginFrame(fixture.Context);
        using var mesh = new Mesh<LEVertex>(null, [], []);

        (Vector3 vertexCamera, Vector3 pixelCamera) = WriteMaterialCameras(fixture, mesh);

        AssertVector(fixture.Context.Camera.EyePosition, vertexCamera, 0);
        AssertVector(fixture.Context.Camera.EyePosition, pixelCamera, 0);
        Assert.AreEqual(fixture.Context.Camera.EyePosition, fixture.Context.LEEffect.ViewOrigin);
    }

    private static Mesh<LEVertex> CreateGroundMesh() => new(null, [], [])
    {
        LocalToWorld = Matrix4x4.CreateScale(1.5f, 0.75f, 2f)
                       * Matrix4x4.CreateRotationZ(0.4f)
                       * Matrix4x4.CreateTranslation(-33000, 12500, 6400)
    };

    private static Matrix4x4 WriteLocalToWorld(WarpMaterialFixture fixture, Mesh<LEVertex> mesh)
    {
        byte[] factoryBuffer = new byte[64];
        var factoryParameters = new FLocalVertexFactoryShaderParameters
        {
            LocalToWorld = new FShaderParameter { BaseIndex = 0, NumBytes = 64 }
        };
        NativeSetters.WriteValues(factoryParameters, factoryBuffer, fixture.Context, mesh, fixture.Material);
        return MemoryMarshal.Read<Matrix4x4>(factoryBuffer);
    }

    private static void AssertClipPlacement(MeshRenderContext context, Vector3 world, Vector3 translated, string description)
    {
        Vector4 expectedClip = Vector4.Transform(new Vector4(world, 1), context.Camera.ViewProjectionMatrix);
        Vector4 actualClip = Vector4.Transform(new Vector4(translated, 1), context.LEEffect.TranslatedViewProjectionMatrix);
        Assert.IsTrue(Vector4.Distance(expectedClip, actualClip) < 0.01f,
            $"Translated placement changed in {description}: expected {expectedClip}, got {actualClip}.");
    }

    private static (Vector3 vertex, Vector3 pixel) WriteMaterialCameras(WarpMaterialFixture fixture, Mesh<LEVertex> mesh)
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
}
