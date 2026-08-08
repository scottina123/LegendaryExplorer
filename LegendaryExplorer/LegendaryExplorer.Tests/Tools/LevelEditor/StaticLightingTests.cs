using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
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
    public void Settings_ValidateParallelWorkControls()
    {
        new StaticLightingGenerationSettings { WorkerThreads = 0, WorkTileSize = 16 }.Validate();
        new StaticLightingGenerationSettings { WorkerThreads = 4, WorkTileSize = 32 }.Validate();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new StaticLightingGenerationSettings { WorkerThreads = -1 }.Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new StaticLightingGenerationSettings { WorkTileSize = 24 }.Validate());
    }

    [TestMethod]
    public void TextureWorkTiles_CoverEveryTexelExactlyOnce()
    {
        IReadOnlyList<StaticLightingBaker.StaticLightingBakeTile> tiles =
            StaticLightingBaker.CreateTextureWorkTiles(70, 16);
        var coverage = new int[70 * 70];
        foreach (StaticLightingBaker.StaticLightingBakeTile tile in tiles)
        for (int y = tile.MinimumY; y < tile.MaximumY; y++)
        for (int x = tile.MinimumX; x < tile.MaximumX; x++)
            coverage[y * 70 + x]++;

        Assert.AreEqual(25, tiles.Count);
        Assert.IsTrue(coverage.All(value => value == 1));
    }

    [TestMethod]
    public void ParallelTextureBake_MatchesSingleWorkerOutputAndCreatesManyWorkUnits()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("ParallelLightmass.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null,
            indexed: false);
        component.WriteProperties([]);
        StaticLightingMeshTarget target = CreateQuadTarget(component);
        var light = new StaticLightingLight(Guid.NewGuid(), StaticLightingLightType.Directional,
            Vector3.Zero, -Vector3.UnitZ, Vector3.One, 1f, float.MaxValue, 0f, 0f, 0);
        LevelCollisionScene collision = LevelCollisionScene.FromTriangles(
            Array.Empty<(Vector3 A, Vector3 B, Vector3 C)>());

        StaticLightingBakeResult single = new StaticLightingBaker([target], [light], collision,
            new StaticLightingGenerationSettings
            {
                TextureResolution = 64,
                WorkTileSize = 16,
                WorkerThreads = 1,
                GenerateShadowMaps = false
            }).Bake();
        StaticLightingBakeResult parallel = new StaticLightingBaker([target], [light], collision,
            new StaticLightingGenerationSettings
            {
                TextureResolution = 64,
                WorkTileSize = 16,
                WorkerThreads = 4,
                GenerateShadowMaps = false
            }).Bake();

        Assert.IsTrue(parallel.WorkUnitCount >= 16);
        Assert.AreEqual(4, parallel.WorkerCount);
        CollectionAssert.AreEqual(single.Components[0].Texture.CoefficientImages[0].ToArray(),
            parallel.Components[0].Texture.CoefficientImages[0].ToArray());
        CollectionAssert.AreEqual(single.Components[0].Texture.CoefficientImages[^1].ToArray(),
            parallel.Components[0].Texture.CoefficientImages[^1].ToArray());
    }

    [TestMethod]
    public void ParallelTextureBake_PreservesLightsThatShareALightGuid()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("DuplicateLightGuids.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null,
            indexed: false);
        component.WriteProperties([]);
        StaticLightingMeshTarget target = CreateQuadTarget(component);
        Guid sharedGuid = Guid.NewGuid();
        var first = new StaticLightingLight(sharedGuid, StaticLightingLightType.Directional,
            Vector3.Zero, -Vector3.UnitZ, Vector3.One, 1f, float.MaxValue, 0f, 0f, 0);
        var second = first with { Intensity = 0.5f };
        LevelCollisionScene collision = LevelCollisionScene.FromTriangles(
            Array.Empty<(Vector3 A, Vector3 B, Vector3 C)>());

        StaticLightingBakeResult result = new StaticLightingBaker([target], [first, second], collision,
            new StaticLightingGenerationSettings
            {
                TextureResolution = 64,
                WorkTileSize = 16,
                WorkerThreads = 4,
                GenerateShadowMaps = true
            }).Bake();

        StaticLightingTextureBake texture = result.Components[0].Texture;
        Assert.AreEqual(2, texture.ShadowMaps.Count);
        Assert.IsTrue(texture.ShadowMaps.All(shadow => shadow.LightGuid == sharedGuid));
        Assert.IsTrue(texture.ShadowMaps.All(shadow => shadow.Visibility[32 * 64 + 32] == byte.MaxValue));
        Assert.AreEqual(1, result.Components[0].LightGuids.Length);
    }

    [TestMethod]
    public void Bake_UsesExistingLightingChannelsAndRecordsIrrelevantLights()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("LightmassChannels.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null,
            indexed: false);
        component.WriteProperties([]);
        const uint initializedAndStatic = 1u | (1u << 2);
        const uint initializedAndDynamic = 1u | (1u << 3);
        StaticLightingMeshTarget target = CreateQuadTarget(component, initializedAndStatic);
        Guid affectingGuid = Guid.NewGuid();
        Guid irrelevantGuid = Guid.NewGuid();
        var affecting = new StaticLightingLight(affectingGuid, StaticLightingLightType.Directional,
            Vector3.Zero, -Vector3.UnitZ, Vector3.One, 1f, float.MaxValue, 0f, 0f,
            initializedAndStatic);
        var channelMismatch = affecting with
        {
            Guid = irrelevantGuid,
            LightingChannelMask = initializedAndDynamic
        };

        StaticLightingBakeResult result = new StaticLightingBaker([target], [affecting, channelMismatch],
            LevelCollisionScene.FromTriangles(Array.Empty<(Vector3 A, Vector3 B, Vector3 C)>()),
            new StaticLightingGenerationSettings
            {
                TextureResolution = 64,
                WorkerThreads = 1,
                GenerateShadowMaps = false
            }).Bake();

        CollectionAssert.AreEqual(new[] { affectingGuid }, result.Components[0].LightGuids);
        CollectionAssert.AreEqual(new[] { irrelevantGuid }, result.Components[0].IrrelevantLightGuids);
    }

    [TestMethod]
    public void Bake_UsesActorBoundsForLocalLightRelevance()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("LightmassBounds.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null,
            indexed: false);
        component.WriteProperties([]);
        StaticLightingMeshTarget target = CreateQuadTarget(component);
        Guid lightGuid = Guid.NewGuid();
        var light = new StaticLightingLight(lightGuid, StaticLightingLightType.Point,
            new Vector3(50, 50, 10), Vector3.UnitZ, Vector3.One, 1f, 20f, 0f, 0f, 0);

        StaticLightingBakeResult result = new StaticLightingBaker([target], [light],
            LevelCollisionScene.FromTriangles(Array.Empty<(Vector3 A, Vector3 B, Vector3 C)>()),
            new StaticLightingGenerationSettings
            {
                TextureResolution = 64,
                WorkerThreads = 1,
                GenerateShadowMaps = false
            }).Bake();

        CollectionAssert.AreEqual(new[] { lightGuid }, result.Components[0].LightGuids);
        Assert.AreEqual(0, result.Components[0].IrrelevantLightGuids.Length);
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

    [TestMethod]
    public void IrrelevantLightsProperty_RoundTripsGeneratedGuids()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("IrrelevantLights.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null,
            indexed: false);
        component.WriteProperties([]);
        Guid[] expected = [Guid.NewGuid(), Guid.NewGuid()];
        var irrelevantLights = new ArrayProperty<StructProperty>(
            expected.Select(guid => CommonStructs.GuidProp(guid)), "IrrelevantLights")
        {
            Reference = "Guid"
        };

        component.WriteProperty(irrelevantLights);

        Guid[] restored = component.GetProperty<ArrayProperty<StructProperty>>("IrrelevantLights")
            .Select(CommonStructs.GetGuid).ToArray();
        CollectionAssert.AreEqual(expected, restored);
    }

    private static StaticLightingMeshTarget CreateQuadTarget(ExportEntry component, uint lightingChannelMask = 0)
    {
        var a = new StaticLightingVertex(new Vector3(0, 0, 0), Vector3.UnitZ, Vector3.UnitX,
            Vector3.UnitY, new Vector2(0, 0));
        var b = new StaticLightingVertex(new Vector3(100, 0, 0), Vector3.UnitZ, Vector3.UnitX,
            Vector3.UnitY, new Vector2(1, 0));
        var c = new StaticLightingVertex(new Vector3(100, 100, 0), Vector3.UnitZ, Vector3.UnitX,
            Vector3.UnitY, new Vector2(1, 1));
        var d = new StaticLightingVertex(new Vector3(0, 100, 0), Vector3.UnitZ, Vector3.UnitX,
            Vector3.UnitY, new Vector2(0, 1));
        return new StaticLightingMeshTarget
        {
            File = null!,
            Component = component,
            ComponentBinary = StaticMeshComponent.Create(),
            MeshLod = new StaticMeshRenderData(),
            LocalToWorld = Matrix4x4.Identity,
            LightingChannelMask = lightingChannelMask,
            Triangles = [new StaticLightingTriangle(a, b, c), new StaticLightingTriangle(a, c, d)],
            Vertices = [a, b, c, d],
            LightMapCoordinateIndex = 1,
            HasTextureCoordinates = true
        };
    }
}
