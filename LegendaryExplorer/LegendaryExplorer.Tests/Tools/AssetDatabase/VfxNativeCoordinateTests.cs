using System;
using System.Numerics;
using System.Runtime.InteropServices;
using LegendaryExplorer.Tests.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NativeSetters = LegendaryExplorer.Tools.LevelEditor.Scene3D.ShaderParameterSetters;

namespace LegendaryExplorer.Tests.Tools.AssetDatabase;

/// <summary>
/// VFX draws with game particle shaders in upstream's translated world space: the frame's <see cref="LEEffect"/> view constants
/// (<see cref="LEEffect.TranslatedViewProjectionMatrix"/>) and the particle vertex factory's translated LocalToWorld and camera position
/// </summary>
[TestClass]
public class VfxNativeCoordinateTests
{
    [DataTestMethod]
    [DataRow(0f)]
    [DataRow(0.8f)]
    [DataRow(2.4f)]
    public void CameraRelativeMeshParticlesKeepTheirScenePlacement(float yaw)
    {
        using var fixture = new WarpMaterialFixture();
        MeshRenderContext context = fixture.Context;
        context.Camera.FirstPerson = true;
        context.Camera.Position = new Vector3(1200, -800, 75);
        context.Camera.Pitch = -0.2f;
        context.Camera.Yaw = yaw;
        context.LEEffect.BeginFrame(context);
        using var mesh = new Mesh<ParticleVertex>(null, [], [])
        {
            LocalToWorld = Matrix4x4.CreateRotationZ(0.4f) * Matrix4x4.CreateTranslation(1350, -700, 120)
        };
        Vector4 localVertex = new(7, 11, 3, 1);

        (Matrix4x4 shaderLocalToWorld, Vector4 shaderCamera) = WriteParticleFactory(fixture, mesh);
        Vector4 expected = Vector4.Transform(localVertex, mesh.LocalToWorld * context.Camera.ViewProjectionMatrix);
        Vector4 actual = Vector4.Transform(localVertex, shaderLocalToWorld * context.LEEffect.TranslatedViewProjectionMatrix);

        Assert.IsTrue(Vector4.Distance(expected, actual) < 0.001f, $"Expected {expected}, got {actual}");
        Assert.AreEqual(-context.Camera.EyePosition, context.LEEffect.PreViewTranslation);
        Assert.IsTrue(Vector4.Distance(new Vector4(Vector3.Zero, 1), shaderCamera) < 0.001f,
            $"The factory's camera must be the translated (camera-relative) eye, got {shaderCamera}");
    }

    [TestMethod]
    public void WorldSpaceSpriteConstantsKeepTheNormalPreviewView()
    {
        using var fixture = new WarpMaterialFixture();
        MeshRenderContext context = fixture.Context;
        context.Camera.FirstPerson = true;
        context.Camera.Position = new Vector3(300, -200, 40);
        context.Camera.Yaw = 1.1f;
        context.LEEffect.BeginFrame(context);
        // World-space sprite vertices are already in world space: the emitter's LocalToWorld is identity
        using var mesh = new Mesh<ParticleVertex>(null, [], []);
        Vector4 worldParticlePosition = new(350, -150, 90, 1);

        (Matrix4x4 shaderLocalToWorld, Vector4 shaderCamera) = WriteParticleFactory(fixture, mesh);
        Vector4 expected = Vector4.Transform(worldParticlePosition, context.Camera.ViewMatrix * context.Camera.ProjectionMatrix);
        Vector4 actual = Vector4.Transform(worldParticlePosition, shaderLocalToWorld * context.LEEffect.TranslatedViewProjectionMatrix);

        Assert.IsTrue(Vector4.Distance(expected, actual) < 0.001f, $"Expected {expected}, got {actual}");
        Assert.AreEqual(new Vector4(context.Camera.Position, 1), context.Camera.ViewOrigin);
        Vector3 orientedTowards = new Vector3(shaderCamera.X, shaderCamera.Y, shaderCamera.Z) - context.LEEffect.PreViewTranslation;
        Assert.IsTrue(Vector3.Distance(context.Camera.EyePosition, orientedTowards) < 0.001f,
            $"Sprites must face the actual eye, got {orientedTowards}");
    }

    private static (Matrix4x4 LocalToWorld, Vector4 Camera) WriteParticleFactory(WarpMaterialFixture fixture, Mesh<ParticleVertex> mesh)
    {
        var parameters = new FParticleVertexFactoryShaderParameters
        {
            LocalToWorld = new FShaderParameter { BaseIndex = 0, NumBytes = 64 },
            CameraWorldPosition = new FShaderParameter { BaseIndex = 64, NumBytes = 16 }
        };
        byte[] buffer = new byte[80];
        NativeSetters.WriteValues(parameters, buffer, fixture.Context, mesh, fixture.Material);
        return (MemoryMarshal.Read<Matrix4x4>(buffer), MemoryMarshal.Read<Vector4>(buffer.AsSpan(64)));
    }
}
