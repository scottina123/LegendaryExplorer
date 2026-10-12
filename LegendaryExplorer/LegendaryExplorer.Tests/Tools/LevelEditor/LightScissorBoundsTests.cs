using System;
using System.Numerics;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpDX.Mathematics.Interop;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class LightScissorBoundsTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [DataTestMethod]
    [DataRow(true, false, 0f, 0f, 0f)]
    [DataRow(true, true, 0.8f, -0.3f, 0.2f)]
    [DataRow(false, false, 2.1f, 0.6f, -0.4f)]
    [DataRow(false, true, 4.5f, -0.7f, 0.8f)]
    public void BoundsContainDenseProjectedLightVolumeAtCameraRotations(bool firstPerson, bool spot,
        float yaw, float pitch, float roll)
    {
        var camera = new SceneCamera
        {
            FirstPerson = firstPerson, FocusDepth = 800,
            Position = new Vector3(10000, -20000, 30000),
            Yaw = yaw, Pitch = pitch, Roll = roll,
            aspect = 16f / 9f, ZNear = 1, ZFar = 100000
        };
        SceneLight light = CreateLight(camera.EyePosition + camera.CameraForward * 800
            + camera.CameraRight * 45 + camera.CameraUp * 30, 100, spot);
        const float x = 17.25f, y = 83.75f, width = 1279.5f, height = 719.25f;
        RawRectangle? result = LightScissorBounds.Compute(light, camera, x, y, width, height);
        Assert.IsTrue(result.HasValue);
        RawRectangle bounds = result.Value;
        Assert.IsTrue(bounds.Left > x && bounds.Top > y);
        Assert.IsTrue(bounds.Right < x + width && bounds.Bottom < y + height);
        Assert.IsTrue((bounds.Right - bounds.Left) * (bounds.Bottom - bounds.Top) < width * height / 2);

        // Probe both the boundary and interior of the sphere; this also covers every
        // spotlight orientation because the spotlight's finite influence is a subset.
        for (int i = 0; i < 2048; i++)
        {
            float z = 1 - 2 * (i + 0.5f) / 2048;
            float angle = i * 2.39996323f;
            float radial = MathF.Sqrt(1 - z * z);
            var direction = new Vector3(radial * MathF.Cos(angle), radial * MathF.Sin(angle), z);
            for (int shell = 1; shell <= 4; shell++)
            {
                Vector3 point = light.Position + direction * (light.Radius * shell / 4);
                Vector4 clip = Vector4.Transform(new Vector4(point, 1), camera.ViewProjectionMatrix);
                double screenX = x + (1 + (double)clip.X / clip.W) * width / 2;
                double screenY = y + (1 - (double)clip.Y / clip.W) * height / 2;
                Assert.IsTrue(screenX >= bounds.Left && screenX <= bounds.Right,
                    $"Light point {point} projects beyond horizontal bounds {bounds.Left}, {bounds.Right}.");
                Assert.IsTrue(screenY >= bounds.Top && screenY <= bounds.Bottom,
                    $"Light point {point} projects beyond vertical bounds {bounds.Top}, {bounds.Bottom}.");
            }
        }
    }

    [TestMethod]
    public void OrbitUsesTheEyeInsteadOfTheOrbitTarget()
    {
        var camera = new SceneCamera
        {
            Position = new Vector3(10000, -20000, 30000),
            FirstPerson = false, FocusDepth = 800, Yaw = 0.7f, Pitch = -0.3f
        };
        // The orbit target is inside this light, but the camera is 800 units away.
        SceneLight light = CreateLight(camera.Position, 100);
        Assert.IsNotNull(LightScissorBounds.Compute(light, camera, 0, 0, 800, 600));
        camera.FirstPerson = true;
        Assert.IsNull(LightScissorBounds.Compute(light, camera, 0, 0, 800, 600));
    }

    [DataTestMethod]
    [DataRow(0f)]
    [DataRow(-10f)]
    [DataRow(1f)]
    [DataRow(10f)]
    public void CameraOrNearPlaneCrossingsRemainUnrestricted(float distance)
    {
        var camera = new SceneCamera { FirstPerson = true, ZNear = 1 };
        SceneLight light = CreateLight(new Vector3(distance, 0, 0), 10);
        Assert.IsNull(LightScissorBounds.Compute(light, camera, 0, 0, 800, 600));
    }

    [TestMethod]
    public void NearPlaneCrossingOutsideTheEyeBoxRemainsUnrestricted()
    {
        var camera = new SceneCamera { FirstPerson = true, ZNear = 10, Yaw = MathF.PI / 4 };
        // The eye is outside the box. At this yaw the box reaches across the near plane.
        SceneLight light = CreateLight(new Vector3(20, 0, 0), 9);
        Assert.IsNull(LightScissorBounds.Compute(light, camera, 0, 0, 800, 600));
    }

    [TestMethod]
    public void VisibleLightSphereCrossingNearPlaneRemainsUnrestricted()
    {
        var camera = new SceneCamera { FirstPerson = true, ZNear = 5 };
        SceneLight light = CreateLight(new Vector3(8, 0, 0), 4);
        Assert.IsNull(LightScissorBounds.Compute(light, camera, 0, 0, 800, 600));
    }

    [DataTestMethod]
    [DataRow(-800f, 0f, 0f)]
    [DataRow(1f, 1000f, 0f)]
    [DataRow(1f, -1000f, 0f)]
    [DataRow(1f, 0f, 1000f)]
    [DataRow(1f, 0f, -1000f)]
    public void BehindOrOffscreenNearCrossingBoxesHaveEmptyBounds(float forward, float horizontal, float vertical)
    {
        var camera = new SceneCamera { FirstPerson = true, ZNear = 1 };
        SceneLight light = CreateLight(new Vector3(forward, horizontal, vertical), 10);
        RawRectangle? result = LightScissorBounds.Compute(light, camera, 0, 0, 800, 600);
        Assert.IsTrue(result.HasValue);
        Assert.IsTrue(result.Value.Left == result.Value.Right || result.Value.Top == result.Value.Bottom);
    }

    [TestMethod]
    public void ReusedSideFrustumKeepsDefaultBoundsAndFarLightInfluence()
    {
        var camera = new SceneCamera
        {
            FirstPerson = false, FocusDepth = 100, Yaw = 0.7f, Pitch = -0.3f,
            Position = new Vector3(10000, -20000, 30000), ZFar = 1000
        };
        var frustum = new ViewFrustum();
        Matrix4x4 view = camera.ViewMatrix;
        view.Translation = Vector3.Zero;
        frustum.Update(view * camera.ProjectionMatrix, depthClipEnabled: false);
        SceneLight visible = CreateLight(camera.EyePosition + camera.CameraForward * 2000, 100);
        RawRectangle? expected = LightScissorBounds.Compute(visible, camera, 17.25f, 83.75f, 1279.5f, 719.25f);
        RawRectangle? actual = LightScissorBounds.Compute(visible, camera, 17.25f, 83.75f, 1279.5f, 719.25f, frustum);
        Assert.IsTrue(expected.HasValue && actual.HasValue);
        Assert.IsTrue(actual.Value.Right > actual.Value.Left && actual.Value.Bottom > actual.Value.Top,
            "Side-only culling must retain lights beyond the far plane for materials that disable depth clipping.");
        Assert.AreEqual(expected.Value.Left, actual.Value.Left);
        Assert.AreEqual(expected.Value.Top, actual.Value.Top);
        Assert.AreEqual(expected.Value.Right, actual.Value.Right);
        Assert.AreEqual(expected.Value.Bottom, actual.Value.Bottom);
        SceneLight behind = CreateLight(camera.EyePosition - camera.CameraForward * 800, 10);
        actual = LightScissorBounds.Compute(behind, camera, 17.25f, 83.75f, 1279.5f, 719.25f, frustum);
        Assert.IsTrue(actual.HasValue);
        Assert.IsTrue(actual.Value.Left == actual.Value.Right || actual.Value.Top == actual.Value.Bottom);
    }

    [DataTestMethod]
    [DataRow(104f, 0f, 0f)]
    [DataRow(104f, 56f, 0f)]
    [DataRow(0f, 104f, 0f)]
    [DataRow(-104f, 0f, 0f)]
    public void SmallLightBoundsAreInvariantFarFromTheWorldOrigin(float forward, float horizontal, float vertical)
    {
        var translatedCamera = new SceneCamera
        {
            FirstPerson = true, Position = new Vector3(100000000),
            aspect = 1, FOV = MathF.PI / 2
        };
        var originCamera = new SceneCamera { FirstPerson = true, aspect = 1, FOV = MathF.PI / 2 };
        SceneLight translatedLight = CreateLight(translatedCamera.EyePosition + new Vector3(forward, horizontal, vertical), 2);
        // Use the exact representable displacement, rather than assume large absolute
        // coordinates can retain every requested unit of the nominal offset.
        SceneLight originLight = CreateLight(translatedLight.Position - translatedCamera.EyePosition, 2);
        RawRectangle? expected = LightScissorBounds.Compute(originLight, originCamera, 17.25f, 83.75f, 900, 900);
        RawRectangle? actual = LightScissorBounds.Compute(translatedLight, translatedCamera, 17.25f, 83.75f, 900, 900);
        Assert.IsTrue(expected.HasValue && actual.HasValue);
        Assert.AreEqual(expected.Value.Left, actual.Value.Left);
        Assert.AreEqual(expected.Value.Top, actual.Value.Top);
        Assert.AreEqual(expected.Value.Right, actual.Value.Right);
        Assert.AreEqual(expected.Value.Bottom, actual.Value.Bottom);
        if (forward > 0)
        {
            Assert.IsTrue(actual.Value.Right - actual.Value.Left > 10 && actual.Value.Bottom - actual.Value.Top > 10,
                "A two-unit radius must not collapse to the light center at a large absolute position.");
        }
        else
        {
            Assert.IsTrue(actual.Value.Left == actual.Value.Right || actual.Value.Top == actual.Value.Bottom);
        }

        var frustum = new ViewFrustum();
        Matrix4x4 view = translatedCamera.ViewMatrix;
        view.Translation = Vector3.Zero;
        frustum.Update(view * translatedCamera.ProjectionMatrix, depthClipEnabled: false);
        RawRectangle? cached = LightScissorBounds.Compute(translatedLight, translatedCamera,
            17.25f, 83.75f, 900, 900, frustum);
        Assert.IsTrue(cached.HasValue);
        Assert.AreEqual(actual.Value.Left, cached.Value.Left);
        Assert.AreEqual(actual.Value.Top, cached.Value.Top);
        Assert.AreEqual(actual.Value.Right, cached.Value.Right);
        Assert.AreEqual(actual.Value.Bottom, cached.Value.Bottom);
    }

    [DataTestMethod]
    [DataRow(10000f, 0f)]
    [DataRow(-10000f, 0f)]
    [DataRow(0f, 10000f)]
    [DataRow(0f, -10000f)]
    public void OffscreenFiniteLightsHaveEmptyBounds(float horizontal, float vertical)
    {
        var camera = new SceneCamera { FirstPerson = true };
        SceneLight light = CreateLight(new Vector3(800, horizontal, vertical), 10);
        RawRectangle? result = LightScissorBounds.Compute(light, camera, 0, 0, 800, 600);
        Assert.IsTrue(result.HasValue);
        Assert.IsTrue(result.Value.Left == result.Value.Right || result.Value.Top == result.Value.Bottom);
    }

    [TestMethod]
    public void FractionalLetterboxedViewportClampsOutwardToCoveredPixels()
    {
        var camera = new SceneCamera { FirstPerson = true, aspect = 16f / 9f };
        SceneLight light = CreateLight(new Vector3(200, 0, 0), 110);
        RawRectangle? result = LightScissorBounds.Compute(light, camera, 17.25f, 83.75f, 1279.5f, 719.25f);
        Assert.IsTrue(result.HasValue);
        Assert.AreEqual(17, result.Value.Left);
        Assert.AreEqual(83, result.Value.Top);
        Assert.AreEqual(1297, result.Value.Right);
        Assert.AreEqual(803, result.Value.Bottom);
    }

    [TestMethod]
    public void ProjectionIncludesOnePixelBeyondRoundedBoxCorners()
    {
        var camera = new SceneCamera { FirstPerson = true, FOV = MathF.PI / 2, aspect = 1 };
        SceneLight light = CreateLight(new Vector3(100, 0, 0), 10);
        RawRectangle? result = LightScissorBounds.Compute(light, camera, 0, 0, 900, 900);
        Assert.IsTrue(result.HasValue);
        Assert.IsTrue(result.Value.Left <= 399 && result.Value.Top <= 399);
        Assert.IsTrue(result.Value.Right >= 501 && result.Value.Bottom >= 501);
    }

    [TestMethod]
    public void InvalidLightCameraAndViewportDataRemainUnrestricted()
    {
        var camera = new SceneCamera { FirstPerson = true };
        SceneLight light = CreateLight(new Vector3(800, 0, 0), 10);
        Assert.IsNull(LightScissorBounds.Compute(null, camera, 0, 0, 800, 600));
        Assert.IsNull(LightScissorBounds.Compute(light, null, 0, 0, 800, 600));
        Assert.IsNull(LightScissorBounds.Compute(CreateLight(new Vector3(float.NaN, 0, 0), 10), camera, 0, 0, 800, 600));
        Assert.IsNull(LightScissorBounds.Compute(CreateLight(light.Position, float.NaN), camera, 0, 0, 800, 600));
        Assert.IsNull(LightScissorBounds.Compute(CreateLight(light.Position, float.PositiveInfinity), camera, 0, 0, 800, 600));
        Assert.IsNull(LightScissorBounds.Compute(CreateLight(new Vector3(float.MaxValue), float.MaxValue), camera, 0, 0, 800, 600));
        Assert.IsNull(LightScissorBounds.Compute(light, camera, float.NaN, 0, 800, 600));
        Assert.IsNull(LightScissorBounds.Compute(light, camera, 0, 0, float.PositiveInfinity, 600));
        Assert.IsNull(LightScissorBounds.Compute(light, camera, 0, 0, 0, 600));
        Assert.IsNull(LightScissorBounds.Compute(light, camera, 0, 0, 800, -1));
        Assert.IsNull(LightScissorBounds.Compute(light, camera, float.MaxValue, 0, 800, 600));
        camera.Position = new Vector3(float.NaN, 0, 0);
        Assert.IsNull(LightScissorBounds.Compute(light, camera, 0, 0, 800, 600));
        camera.Position = Vector3.Zero;
        camera.ZFar = camera.ZNear;
        Assert.IsNull(LightScissorBounds.Compute(light, camera, 0, 0, 800, 600));
        camera.ZFar = 100000;
        camera.FOV = 0;
        Assert.IsNull(LightScissorBounds.Compute(light, camera, 0, 0, 800, 600));
        camera.FOV = MathF.PI / 3;
        camera.aspect = 0;
        Assert.IsNull(LightScissorBounds.Compute(light, camera, 0, 0, 800, 600));
    }

    [TestMethod]
    public void OrthographicLightsRemainUnrestricted()
    {
        var camera = new SceneCamera { IsOrthographic = true };
        Assert.IsNull(LightScissorBounds.Compute(CreateLight(new Vector3(0, 0, -800), 10), camera, 0, 0, 800, 600));
    }

    [DataTestMethod]
    [DataRow(0f, 2f)]
    [DataRow(-10f, 2f)]
    [DataRow(10f, 0f)]
    [DataRow(10f, -1f)]
    [DataRow(10f, float.NaN)]
    [DataRow(10f, float.PositiveInfinity)]
    public void CookedLightsWithoutFiniteRadialAttenuationRemainUnrestricted(float radius, float exponent)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InvalidLightBounds.pcc", MEGame.LE2);
        ExportEntry actor = package.CreateExport("PointLight", "PointLight", indexed: false);
        ExportEntry component = package.CreateExport("PointLightComponent", "PointLightComponent", actor, indexed: false);
        component.WriteProperties([
            // Invalid attenuation must remain unrestricted even when side culling would
            // otherwise reject an ordinary finite light at this position.
            CommonStructs.MatrixProp(Matrix4x4.CreateTranslation(-800, 10000, 0), "CachedParentToWorld"),
            new FloatProperty(radius, "Radius"), new FloatProperty(exponent, "FalloffExponent")
        ]);
        using var cache = new PackageCache();
        SceneLight light = SceneLight.Create(component, "PointLight", cache);
        Assert.IsNotNull(light);
        Assert.IsNull(LightScissorBounds.Compute(light, new SceneCamera { FirstPerson = true }, 0, 0, 800, 600));
    }

    [DataTestMethod]
    [DataRow("DirectionalLight")]
    [DataRow("SkyLight")]
    public void LightsWithoutRadialAttenuationRemainUnrestricted(string lightClass)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InfiniteLightBounds.pcc", MEGame.LE2);
        ExportEntry actor = package.CreateExport(lightClass, lightClass, indexed: false);
        ExportEntry component = package.CreateExport(lightClass + "Component", lightClass + "Component", actor, indexed: false);
        component.WriteProperties([
            CommonStructs.MatrixProp(Matrix4x4.CreateTranslation(800, 0, 0), "CachedParentToWorld"),
            new FloatProperty(10, "Radius")
        ]);
        using var cache = new PackageCache();
        SceneLight light = SceneLight.Create(component, lightClass, cache);
        Assert.IsNotNull(light);
        Assert.IsNull(LightScissorBounds.Compute(light, new SceneCamera { FirstPerson = true }, 0, 0, 800, 600));
    }

    private static SceneLight CreateLight(Vector3 position, float radius, bool spot = false) =>
        new(position, radius, Vector3.One, 1, spot, -Vector3.UnitX, 20, 45);
}
