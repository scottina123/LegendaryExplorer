using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class StaticLightingTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [DataTestMethod]
    [DataRow(64)]
    [DataRow(128)]
    [DataRow(1024)]
    public void Settings_AcceptSupportedPowerOfTwoResolutions(int resolution)
    {
        new StaticLightingGenerationSettings { TextureResolution = resolution }.Validate();
    }

    [DataTestMethod]
    [DataRow(32)]
    [DataRow(96)]
    [DataRow(2048)]
    public void Settings_RejectUnsupportedResolutions(int resolution)
    {
        try
        {
            new StaticLightingGenerationSettings { TextureResolution = resolution }.Validate();
            Assert.Fail("Validation should reject unsupported lightmap resolutions.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    [TestMethod]
    public void PointLight_UsesSurfaceFacingAndRadialAttenuation()
    {
        var light = new StaticLightingLight(Guid.NewGuid(), StaticLightingLightType.Point,
            new Vector3(0, 0, 50), Vector3.UnitZ, new Vector3(1, 0.5f, 0.25f),
            2f, 100f, 0f, 0f, 0);
        var front = new StaticLightingBaker.StaticLightingSurfaceSample(
            Vector3.Zero, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY);

        Assert.IsTrue(StaticLightingBaker.TryEvaluateLight(light, front, out Vector3 direction,
            out Vector3 radiance, out Vector3 irradiance));
        Assert.AreEqual(1f, direction.Z, 0.0001f);
        Assert.AreEqual(1.125f, radiance.X, 0.0001f);
        Assert.AreEqual(radiance.X, irradiance.X, 0.0001f);

        var back = front with { Normal = -Vector3.UnitZ };
        Assert.IsFalse(StaticLightingBaker.TryEvaluateLight(light, back, out _, out _, out _));
    }

    [TestMethod]
    public void SpotLight_RejectsSamplesOutsideItsCone()
    {
        var light = new StaticLightingLight(Guid.NewGuid(), StaticLightingLightType.Spot,
            Vector3.Zero, Vector3.UnitX, Vector3.One, 1f, 100f, 20f, 40f, 0);
        var inside = new StaticLightingBaker.StaticLightingSurfaceSample(
            new Vector3(10, 0, 0), -Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        var outside = inside with { Position = new Vector3(0, 10, 0), Normal = -Vector3.UnitY };

        Assert.IsTrue(StaticLightingBaker.TryEvaluateLight(light, inside, out _, out _, out _));
        Assert.IsFalse(StaticLightingBaker.TryEvaluateLight(light, outside, out _, out _, out _));
    }

    [TestMethod]
    public void ExistingObjectBinarySerializer_RoundTripsGeneratedLightAndShadowMaps()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("StaticLightingSerialization.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null, indexed: false);
        component.WriteProperties([]);
        Guid lightGuid = Guid.NewGuid();
        component.WriteBinary(new StaticMeshComponent
        {
            LODData =
            [
                new StaticMeshComponentLODInfo
                {
                    ShadowMaps = [12],
                    ShadowVertexBuffers = [],
                    LightMap = new LightMap_2D
                    {
                        LightMapType = ELightMapType.LMT_2D,
                        LightGuids = [lightGuid],
                        Texture1 = 7,
                        Texture2 = 8,
                        Texture3 = 9,
                        ScaleVector1 = Vector3.One,
                        ScaleVector2 = new Vector3(2),
                        ScaleVector3 = new Vector3(3),
                        CoordinateScale = new Vector2(0.96875f),
                        CoordinateBias = new Vector2(0.015625f)
                    }
                }
            ]
        });

        StaticMeshComponent restoredComponent = component.GetBinaryData<StaticMeshComponent>();
        var restoredLightMap = (LightMap_2D)restoredComponent.LODData[0].LightMap;
        Assert.AreEqual(lightGuid, restoredLightMap.LightGuids[0]);
        Assert.AreEqual(8, restoredLightMap.Texture2);
        Assert.AreEqual(3f, restoredLightMap.ScaleVector3.X, 0.0001f);

        ExportEntry shadow = package.CreateExport("ShadowMap1D_0", "ShadowMap1D", component, indexed: false);
        shadow.WriteProperties([]);
        shadow.WriteBinary(new ShadowMap1D { LightGuid = lightGuid, Samples = [0, -1, 0x7F7F7F7F] });
        ShadowMap1D restoredShadow = shadow.GetBinaryData<ShadowMap1D>();
        CollectionAssert.AreEqual(new[] { 0, -1, 0x7F7F7F7F }, restoredShadow.Samples);
        Assert.AreEqual(lightGuid, restoredShadow.LightGuid);
    }
}
