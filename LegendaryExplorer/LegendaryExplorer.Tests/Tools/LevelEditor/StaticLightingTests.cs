using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
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
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new StaticLightingGenerationSettings { ShadowSampleCount = 0 }.Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new StaticLightingGenerationSettings { DefaultLightSourceRadius = -1f }.Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new StaticLightingGenerationSettings { DirectionalSourceAngleDegrees = 11f }.Validate());
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
                WorkerThreads = 1
            }).Bake();
        StaticLightingBakeResult parallel = new StaticLightingBaker([target], [light], collision,
            new StaticLightingGenerationSettings
            {
                TextureResolution = 64,
                WorkTileSize = 16,
                WorkerThreads = 4
            }).Bake();

        Assert.IsTrue(parallel.WorkUnitCount >= 16);
        Assert.AreEqual(4, parallel.WorkerCount);
        CollectionAssert.AreEqual(single.Components[0].Texture.CoefficientImages[0].ToArray(),
            parallel.Components[0].Texture.CoefficientImages[0].ToArray());
        CollectionAssert.AreEqual(single.Components[0].Texture.CoefficientImages[^1].ToArray(),
            parallel.Components[0].Texture.CoefficientImages[^1].ToArray());
    }

    [TestMethod]
    public void ParallelTextureBake_BakesLightsThatShareAGuidWithoutRuntimeShadowMaps()
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
                WorkerThreads = 4
            }).Bake();

        StaticLightingTextureBake texture = result.Components[0].Texture;
        Assert.AreEqual(0, texture.ShadowMaps.Count);
        Assert.AreEqual(1.62f, texture.ScaleVectors[2].X, 0.0001f);
        Assert.AreEqual(1, result.Components[0].LightGuids.Length);
    }

    [TestMethod]
    public void Game3Bake_EncodesDirectionalBasisMaximaAndSimpleLightmap()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("DirectionalLightmass.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null,
            indexed: false);
        component.WriteProperties([]);
        StaticLightingMeshTarget target = CreateQuadTarget(component);

        StaticLightingBakeResult result = new StaticLightingBaker([target], [],
            LevelCollisionScene.FromTriangles(Array.Empty<(Vector3 A, Vector3 B, Vector3 C)>()),
            new StaticLightingGenerationSettings
            {
                TextureResolution = 64,
                WorkerThreads = 1,
                AmbientIntensity = 0.25f
            }).Bake();

        StaticLightingTextureBake texture = result.Components[0].Texture;
        Assert.AreEqual(3, texture.CoefficientImages.Count);
        Assert.AreEqual(Vector3.One, texture.ScaleVectors[0]);
        Assert.AreEqual(new Vector3(0.25f), texture.ScaleVectors[1]);
        Assert.AreEqual(0.25f, texture.ScaleVectors[2].Z, 0.0001f);

        int center = (32 * 64 + 32) * 4;
        byte[] normalizedColor = texture.CoefficientImages[0];
        byte[] directionalMaxima = texture.CoefficientImages[1];
        byte[] simple = texture.CoefficientImages[2];
        Assert.AreEqual(byte.MaxValue, normalizedColor[center]);
        Assert.AreEqual(byte.MaxValue, normalizedColor[center + 1]);
        Assert.AreEqual(byte.MaxValue, normalizedColor[center + 2]);
        Assert.AreEqual(byte.MaxValue, directionalMaxima[center]);
        Assert.AreEqual(byte.MaxValue, directionalMaxima[center + 1]);
        Assert.AreEqual(byte.MaxValue, directionalMaxima[center + 2]);
        Assert.AreEqual(byte.MaxValue, simple[center]);
        Assert.AreEqual(byte.MaxValue, simple[center + 1]);
        Assert.AreEqual(byte.MaxValue, simple[center + 2]);
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
                WorkerThreads = 1
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
                WorkerThreads = 1
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
    public void OccludedDirectLight_PreservesEnvironmentAndDuplicateOccludersDoNotStackDarkness()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("SeparatedLightmassTerms.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null,
            indexed: false);
        component.WriteProperties([]);
        StaticLightingMeshTarget target = CreateQuadTarget(component);
        var light = new StaticLightingLight(Guid.NewGuid(), StaticLightingLightType.Directional,
            Vector3.Zero, -Vector3.UnitZ, Vector3.One, 1f, float.MaxValue, 0f, 0f, 0);
        var first = (new Vector3(-10, -10, 10), new Vector3(110, -10, 10), new Vector3(110, 110, 10));
        var second = (new Vector3(-10, -10, 10), new Vector3(110, 110, 10), new Vector3(-10, 110, 10));
        var settings = new StaticLightingGenerationSettings
        {
            TextureResolution = 64,
            WorkerThreads = 1,
            AmbientIntensity = 0.2f,
            ShadowSampleCount = 1,
            DirectionalSourceAngleDegrees = 0f
        };

        StaticLightingBakeResult oneLayer = new StaticLightingBaker([target], [light],
            LevelCollisionScene.FromTriangles(new[] { first, second }), settings).Bake();
        StaticLightingBakeResult duplicateLayer = new StaticLightingBaker([target], [light],
            LevelCollisionScene.FromTriangles(new[] { first, second, first, second }), settings).Bake();

        Assert.AreEqual(0.2f, oneLayer.Components[0].Texture.ScaleVectors[2].X, 0.0001f);
        Assert.AreEqual(oneLayer.Components[0].Texture.ScaleVectors[2].X,
            duplicateLayer.Components[0].Texture.ScaleVectors[2].X, 0.0001f);
        CollectionAssert.AreEqual(oneLayer.Components[0].Texture.CoefficientImages[2].ToArray(),
            duplicateLayer.Components[0].Texture.CoefficientImages[2].ToArray());
        Assert.AreEqual(0d, oneLayer.AverageVisibility, 0.0001d);
        Assert.AreEqual(0.2d, oneLayer.AverageEnvironmentContribution, 0.0001d);
    }

    [TestMethod]
    public void AreaShadowSampling_IsDeterministicAndProducesPartialVisibilityAtLowResolution()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("SoftLightmass.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null,
            indexed: false);
        component.WriteProperties([]);
        StaticLightingMeshTarget target = CreateQuadTarget(component);
        var light = new StaticLightingLight(Guid.Parse("ed833201-3301-4ed8-a38f-66f92ad70e2a"),
            StaticLightingLightType.Point, new Vector3(50, 50, 100), -Vector3.UnitZ,
            Vector3.One, 1f, 250f, 0f, 0f, 0, true, 35f);
        var blocker = new[]
        {
            (new Vector3(45, -20, 50), new Vector3(55, -20, 50), new Vector3(55, 120, 50)),
            (new Vector3(45, -20, 50), new Vector3(55, 120, 50), new Vector3(45, 120, 50))
        };
        var settings = new StaticLightingGenerationSettings
        {
            TextureResolution = 64,
            WorkerThreads = 4,
            AmbientIntensity = 0.12f,
            ShadowSampleCount = 16,
            DefaultLightSourceRadius = 0f
        };

        StaticLightingBakeResult first = new StaticLightingBaker([target], [light],
            LevelCollisionScene.FromTriangles(blocker), settings).Bake();
        StaticLightingBakeResult second = new StaticLightingBaker([target], [light],
            LevelCollisionScene.FromTriangles(blocker), settings).Bake();

        Assert.IsGreaterThan(0d, first.AverageVisibility);
        Assert.IsLessThan(1d, first.AverageVisibility);
        Assert.IsGreaterThan(0L, first.OccludedSamples);
        CollectionAssert.AreEqual(first.Components[0].Texture.CoefficientImages[2].ToArray(),
            second.Components[0].Texture.CoefficientImages[2].ToArray());
        Assert.AreEqual(first.AverageVisibility, second.AverageVisibility, 0.000001d);
    }

    [TestMethod]
    public void CollisionRaycast_RejectsReceiverSelfIntersectionButRetainsSourceIdentity()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("SelfIntersection.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null,
            indexed: false);
        component.WriteProperties([]);
        LevelCollisionScene collision = LevelCollisionScene.FromTriangles(new[]
        {
            (new Vector3(-10, -10, 0.5f), new Vector3(10, -10, 0.5f), new Vector3(0, 10, 0.5f), component, 7)
        });

        bool hit = collision.RaycastFiltered(Vector3.Zero, Vector3.UnitZ, 10f,
            component, 7, 2f, out _, out int rejected);

        Assert.IsFalse(hit);
        Assert.AreEqual(1, rejected);
    }

    [TestMethod]
    public void UvOverlapDiagnostics_AllowSharedEdgesAndRejectSharedChartInteriors()
    {
        StaticLightingMeshTarget quad = CreateQuadTarget(null!);
        Assert.AreEqual(0, StaticLightingBaker.CountOverlappingUvTrianglePairs(quad.Triangles));

        StaticLightingTriangle original = quad.Triangles[0];
        StaticLightingTriangle overlapping = new(
            original.A with { Position = original.A.Position + Vector3.UnitZ * 10f },
            original.B with { Position = original.B.Position + Vector3.UnitZ * 10f },
            original.C with { Position = original.C.Position + Vector3.UnitZ * 10f });
        Assert.AreEqual(1, StaticLightingBaker.CountOverlappingUvTrianglePairs([original, overlapping]));

        // BioA_CitHub_Temple export 2198 uses this half-precision UV T-junction. The right
        // triangle's middle vertex belongs on the left triangle's edge, but quantization places
        // the intersection about 0.0000007 UV units from the endpoint. That is less than 0.001
        // texel even at 1024 and must not be diagnosed as a chart overlap.
        var junctionLeft = new StaticLightingTriangle(
            original.A with { LightMapCoordinate = new Vector2(0.502441406f, 0.158325195f) },
            original.B with { LightMapCoordinate = new Vector2(0.491455078f, 0.104980469f) },
            original.C with { LightMapCoordinate = new Vector2(0.512207031f, 0.0996704102f) });
        var junctionRight = new StaticLightingTriangle(
            original.A with { LightMapCoordinate = new Vector2(0.5f, 0.0868530273f) },
            original.B with { LightMapCoordinate = new Vector2(0.501953125f, 0.102294922f) },
            original.C with { LightMapCoordinate = new Vector2(0.491455078f, 0.104980469f) });
        Assert.AreEqual(0,
            StaticLightingBaker.CountOverlappingUvTrianglePairs([junctionLeft, junctionRight]));
    }

    [TestMethod]
    public void MappingMode_PreservesAuthoredVertexMapsAndRecoversGeneratedMappingsFromMeshResolution()
    {
        Assert.IsFalse(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Automatic, true, 0,
            ELightMapType.LMT_3, false), "Stock Bench02-style vertex mapping must be preserved.");
        Assert.IsFalse(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Automatic, true, 0,
            ELightMapType.LMT_2D, true), "A previous generated texture map must not hide a vertex-authored mesh.");
        Assert.IsTrue(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Automatic, true, 32,
            ELightMapType.LMT_2D, true), "A texture-authored mesh keeps using the selected texture size.");
        Assert.IsTrue(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Automatic, true, 0,
            ELightMapType.LMT_4, false), "Existing stock atlas texture mappings remain texture based.");
        Assert.IsFalse(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Automatic, false, 32,
            ELightMapType.LMT_2D, false), "Invalid UV mappings always require vertex fallback.");
    }

    [TestMethod]
    public void MappingMode_AutomaticPromotesArchitecturalAndBroadLowPolyReceivers()
    {
        Assert.IsTrue(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Automatic,
            true, 0, ELightMapType.LMT_1D, false, "BioA_FloorTile_01", 96f, 8_000f, 40));
        Assert.IsTrue(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Automatic,
            true, 0, ELightMapType.LMT_1D, false, "GenericLargeMesh", 600f, 100_000f, 2_000));
        Assert.IsTrue(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Automatic,
            true, 0, ELightMapType.LMT_1D, false, "GenericBroadQuad", 200f, 20_000f, 2));
        Assert.IsFalse(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Automatic,
            true, 0, ELightMapType.LMT_1D, false, "DenseSmallProp", 120f, 12_000f, 1_000));
    }

    [TestMethod]
    public void MappingMode_SingleActorOverridesAutomaticPolicyButStillRequiresValidTextureUvs()
    {
        Assert.IsTrue(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Texture2D,
            true, 0, ELightMapType.LMT_1D, false));
        Assert.IsFalse(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Texture2D,
            false, 64, ELightMapType.LMT_2D, false));
        Assert.IsFalse(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Vertex1D,
            true, 64, ELightMapType.LMT_2D, false));
    }

    [TestMethod]
    public void MappingMode_ForcedTextureBakeReportsVertexFallbackWithoutThrowing()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("ForcedTextureFallback.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null,
            indexed: false);
        component.WriteProperties([]);
        StaticLightingMeshTarget original = CreateQuadTarget(component);
        StaticLightingMeshTarget target = new()
        {
            File = original.File,
            Component = original.Component,
            ComponentBinary = original.ComponentBinary,
            MeshLod = original.MeshLod,
            LocalToWorld = original.LocalToWorld,
            LightingChannelMask = original.LightingChannelMask,
            Triangles = original.Triangles,
            Vertices = original.Vertices,
            LightMapCoordinateIndex = original.LightMapCoordinateIndex,
            HasTextureCoordinates = false,
            UseTextureMapping = false,
            MappingDiagnostics = new StaticLightingMappingDiagnostics
            {
                SelectedCoordinateIndex = 1,
                TextureCoordinateCount = 1,
                InvalidUvVertexCount = 4
            }
        };

        var baker = new StaticLightingBaker([target], [], LevelCollisionScene.FromTriangles([]),
            new StaticLightingGenerationSettings { MappingMode = StaticLightingMappingMode.Texture2D });
        StaticLightingBakeResult result = baker.Bake();

        Assert.IsEmpty(result.Components);
        StringAssert.Contains(result.ValidationError, "No bake or TFC write was started");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MappingMode_ForcedTextureBakeRepairsDegenerateUvTriangles(bool collapseToPoint)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("RepairTextureMapping.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null,
            indexed: false);
        component.WriteProperties([]);
        Vector2 firstUv = collapseToPoint ? new Vector2(0.5f) : new Vector2(0.2f, 0.5f);
        Vector2 secondUv = new(0.5f, 0.5f);
        Vector2 thirdUv = collapseToPoint ? new Vector2(0.5f) : new Vector2(0.8f, 0.5f);
        var a = new StaticLightingVertex(Vector3.Zero, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, firstUv);
        var b = new StaticLightingVertex(new Vector3(100, 0, 0), Vector3.UnitZ, Vector3.UnitX,
            Vector3.UnitY, secondUv);
        var c = new StaticLightingVertex(new Vector3(0, 100, 0), Vector3.UnitZ, Vector3.UnitX,
            Vector3.UnitY, thirdUv);
        var mapping = new StaticLightingMappingDiagnostics
        {
            DeclaredVertexCount = 3,
            PositionVertexCount = 3,
            AttributeVertexCount = 3,
            TextureCoordinateCount = 2,
            SelectedCoordinateIndex = 1,
            SectionCount = 1,
            SourceIndexCount = 3,
            TriangleCount = 1,
            DegenerateUvTriangleCount = 1
        };
        var target = new StaticLightingMeshTarget
        {
            File = null!,
            Component = component,
            ComponentBinary = StaticMeshComponent.Create(),
            MeshLod = new StaticMeshRenderData(),
            LocalToWorld = Matrix4x4.Identity,
            LightingChannelMask = 0,
            Triangles = [new StaticLightingTriangle(a, b, c)],
            Vertices = [a, b, c],
            LightMapCoordinateIndex = 1,
            HasTextureCoordinates = true,
            UseTextureMapping = true,
            MappingDiagnostics = mapping
        };

        StaticLightingBakeResult result = new StaticLightingBaker([target], [],
            LevelCollisionScene.FromTriangles(Array.Empty<(Vector3 A, Vector3 B, Vector3 C)>()),
            new StaticLightingGenerationSettings
            {
                MappingMode = StaticLightingMappingMode.Texture2D,
                TextureResolution = 64,
                WorkTileSize = 16,
                WorkerThreads = 1,
                AmbientIntensity = 0.25f
            }).Bake();

        Assert.IsNull(result.ValidationError);
        Assert.AreEqual(1, result.TextureMappedComponentCount);
        Assert.IsNotNull(result.Components[0].Texture);
        Assert.IsTrue(result.Components[0].Diagnostics.MappedTexelCount >= (collapseToPoint ? 1 : 70));
        Assert.IsTrue(mapping.HasRepairableTextureMappingIssues);
        Assert.IsFalse(mapping.HasTextureMappingErrors);
    }

    [TestMethod]
    public void TextureWriter_PersistsLightMap2DInsteadOfVertexMapping()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("TextureWriter.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null,
            indexed: false);
        component.WriteProperties([]);
        component.WriteBinary(StaticMeshComponent.Create());
        StaticLightingMeshTarget target = CreateQuadTarget(component);
        const int resolution = 64;
        byte[][] coefficients = Enumerable.Range(0, 3)
            .Select(index => Enumerable.Repeat((byte)(64 + index * 32), resolution * resolution * 4).ToArray())
            .ToArray();
        var textureBake = new StaticLightingTextureBake
        {
            Resolution = resolution,
            CoefficientImages = coefficients,
            ScaleVectors = [Vector3.One, Vector3.One, Vector3.One],
            ShadowMaps = [],
            CoordinateScale = new Vector2(0.96875f),
            CoordinateBias = new Vector2(0.015625f)
        };
        var componentBake = new StaticLightingComponentBake
        {
            Target = target,
            LightGuids = [],
            IrrelevantLightGuids = [],
            Texture = textureBake
        };
        string cachePath = Path.Combine(Path.GetTempPath(), $"LEX_TextureWriter_{Guid.NewGuid():N}.tfc");

        try
        {
            using (var header = new FileStream(cachePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                header.WriteGuid(Guid.NewGuid());
            using var cache = new FileStream(cachePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            cache.Seek(0, SeekOrigin.End);
            var streamingTextures = new List<(ExportEntry Texture, StaticLightingMeshTarget Target, int Resolution)>();
            var installMethod = typeof(StaticLightingWriter).GetMethod("InstallTextureLightMap",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(installMethod);
            object[] arguments =
                [componentBake, textureBake, "LEX_TextureWriter", cachePath, cache, streamingTextures, 0];
            installMethod.Invoke(null, arguments);
            int textureCount = (int)arguments[6];

            Assert.AreEqual(3, textureCount);
            StaticMeshComponent installed = component.GetBinaryData<StaticMeshComponent>();
            Assert.IsInstanceOfType<LightMap_2D>(installed.LODData[0].LightMap);
            Assert.AreEqual(ELightMapType.LMT_2D, installed.LODData[0].LightMap.LightMapType);
            using var saved = package.SaveToStream(false);
            saved.Position = 0;
            using IMEPackage reopened = MEPackageHandler.OpenMEPackageFromStream(saved, "TextureWriter.pcc");
            LightMap reopenedMap = reopened.GetUExport(component.UIndex)
                .GetBinaryData<StaticMeshComponent>().LODData[0].LightMap;
            Assert.IsInstanceOfType<LightMap_2D>(reopenedMap);
            Assert.AreEqual(ELightMapType.LMT_2D, reopenedMap.LightMapType);
        }
        finally
        {
            if (File.Exists(cachePath))
                File.Delete(cachePath);
        }
    }

    [TestMethod]
    public void ShadowCasterFlags_IncludeDefaultObjectsAndHonorEveryExplicitStaticShadowOptOut()
    {
        Assert.IsTrue(StaticLightingBaker.CastsStaticShadow([]));
        Assert.IsFalse(StaticLightingBaker.CastsStaticShadow(
            [new BoolProperty(false, "CastShadow")]));
        Assert.IsFalse(StaticLightingBaker.CastsStaticShadow(
            [new BoolProperty(false, "bCastStaticShadow")]));
        Assert.IsFalse(StaticLightingBaker.CastsStaticShadow(
            [new BoolProperty(false, "CastStaticShadows")]));
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

    [TestMethod]
    public void StaticLightingProperties_DisableRuntimeLightAcceptance()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("StaticLightingProperties.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null,
            indexed: false);
        component.WriteProperties([]);
        var method = typeof(StaticLightingWriter).GetMethod("ApplyStaticLightingProperties",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.IsNotNull(method);
        method.Invoke(null, [component, Array.Empty<Guid>()]);

        Assert.IsFalse(component.GetProperty<BoolProperty>("bAcceptsLights").Value);
        Assert.IsFalse(component.GetProperty<BoolProperty>("bAcceptsDynamicLights").Value);
        Assert.IsTrue(component.GetProperty<BoolProperty>("bForceDirectLightMap").Value);
        Assert.IsTrue(component.GetProperty<BoolProperty>("bUsePrecomputedShadows").Value);
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
            HasTextureCoordinates = true,
            UseTextureMapping = true
        };
    }
}
