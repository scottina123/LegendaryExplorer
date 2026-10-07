using System.Numerics;
using LegendaryExplorer.Tools.AssetDatabase.VFXPreview;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.AssetDatabase;

[TestClass]
public class VfxNativeCoordinateTests
{
    [DataTestMethod]
    [DataRow(0f)]
    [DataRow(0.8f)]
    [DataRow(2.4f)]
    public void CameraRelativeMeshParticlesKeepTheirScenePlacement(float yaw)
    {
        var context = new MeshRenderContext();
        context.Camera.FirstPerson = true;
        context.Camera.Position = new Vector3(1200, -800, 75);
        context.Camera.Pitch = -0.2f;
        context.Camera.Yaw = yaw;
        Matrix4x4 model = Matrix4x4.CreateRotationZ(0.4f)
            * Matrix4x4.CreateTranslation(1350, -700, 120);
        Vector4 localVertex = new(7, 11, 3, 1);

        LEVSConstants absolute = VfxGameShaderRenderer.CreateNativeVertexConstants(context);
        Vector4 expected = Vector4.Transform(localVertex, model * absolute.ViewProjectionMatrix);
        context.UseCameraRelativeNativeRendering = true;
        LEVSConstants relative = VfxGameShaderRenderer.CreateNativeVertexConstants(context);
        Vector4 actual = Vector4.Transform(localVertex,
            context.GetNativeShaderLocalToWorld(model) * relative.ViewProjectionMatrix);

        Assert.IsTrue(Vector4.Distance(expected, actual) < 0.001f, $"Expected {expected}, got {actual}");
        Assert.AreEqual(new Vector4(Vector3.Zero, 1), relative.CameraPosition);
        Assert.AreEqual(Vector4.Zero, relative.PreViewTranslation);
    }

    [TestMethod]
    public void WorldSpaceSpriteConstantsKeepTheNormalPreviewView()
    {
        var context = new MeshRenderContext();
        context.Camera.FirstPerson = true;
        context.Camera.Position = new Vector3(300, -200, 40);
        context.Camera.Yaw = 1.1f;
        Vector4 worldParticlePosition = new(350, -150, 90, 1);

        LEVSConstants constants = VfxGameShaderRenderer.CreateNativeVertexConstants(context);
        Vector4 expected = Vector4.Transform(worldParticlePosition,
            context.Camera.ViewMatrix * context.Camera.ProjectionMatrix);

        Assert.AreEqual(expected, Vector4.Transform(worldParticlePosition, constants.ViewProjectionMatrix));
        Assert.AreEqual(new Vector4(context.Camera.Position, 1), constants.CameraPosition);
    }

    [TestMethod]
    public void LensFlareConstantsKeepTheExplicitFactoryEyePosition()
    {
        var context = new MeshRenderContext();
        context.Camera.Position = new Vector3(300, -200, 40);
        Vector3 factoryEye = new(280, -180, 20);

        LEVSConstants constants = VfxGameShaderRenderer.CreateNativeVertexConstants(context, factoryEye);

        Assert.AreEqual(new Vector4(factoryEye, 1), constants.CameraPosition);
    }
}
