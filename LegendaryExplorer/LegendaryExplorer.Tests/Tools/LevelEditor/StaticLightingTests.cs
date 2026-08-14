using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Collections;
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
    [DataRow(2048)]
    public void Settings_AcceptSupportedPowerOfTwoResolutions(int resolution)
    {
        new StaticLightingGenerationSettings { TextureResolution = resolution }.Validate();
    }

    [DataTestMethod]
    [DataRow(32)]
    [DataRow(96)]
    [DataRow(4096)]
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
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new StaticLightingGenerationSettings { TextureFormat = (StaticLightingTextureFormat)int.MaxValue }.Validate());
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
    public void BulkAutomaticResolution_UsesExactRequestedSize()
    {
        Assert.AreEqual(2048, StaticLightingBaker.ResolveTextureResolution(
            StaticLightingMappingMode.Automatic, false, 32, 2048));
        Assert.AreEqual(1024, StaticLightingBaker.ResolveTextureResolution(
            StaticLightingMappingMode.Automatic, false, 32, 1024));
        Assert.AreEqual(1024, StaticLightingBaker.ResolveTextureResolution(
            StaticLightingMappingMode.Automatic, false, 96, 1024));
        Assert.AreEqual(128, StaticLightingBaker.ResolveTextureResolution(
            StaticLightingMappingMode.Automatic, false, 256, 128));
    }

    [TestMethod]
    public void ExplicitOrSingleActorResolution_UsesExactRequestedSize()
    {
        Assert.AreEqual(1024, StaticLightingBaker.ResolveTextureResolution(
            StaticLightingMappingMode.Automatic, true, 32, 1024));
        Assert.AreEqual(1024, StaticLightingBaker.ResolveTextureResolution(
            StaticLightingMappingMode.Texture2D, false, 32, 1024));
        Assert.AreEqual(2048, StaticLightingBaker.ResolveTextureResolution(
            StaticLightingMappingMode.Texture2D, true, 32, 2048));
        Assert.AreEqual(2048, StaticLightingBaker.ResolveTextureResolution(
            StaticLightingMappingMode.Texture2D, false, 32, 2048));
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
    public void NativeBackend_MatchesManagedTextureAndVertexOutputs()
    {
        if (!StaticLightingBaker.IsNativeBackendAvailable)
            Assert.Inconclusive("Build LightmassNative before running native-backend parity tests.");

        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("NativeParity.pcc", MEGame.LE3);
        ExportEntry textureComponent = package.CreateExport("StaticMeshComponent_Texture", "StaticMeshComponent",
            null, indexed: false);
        ExportEntry vertexComponent = package.CreateExport("StaticMeshComponent_Vertex", "StaticMeshComponent",
            null, indexed: false);
        textureComponent.WriteProperties([]);
        vertexComponent.WriteProperties([]);
        StaticLightingMeshTarget textureTarget = CreateQuadTarget(textureComponent);
        StaticLightingMeshTarget vertexTarget = CreateQuadTarget(vertexComponent, useTextureMapping: false);
        var light = new StaticLightingLight(Guid.Parse("ed833201-3301-4ed8-a38f-66f92ad70e2a"),
            StaticLightingLightType.Point, new Vector3(50, 50, 100), -Vector3.UnitZ,
            Vector3.One, 1f, 250f, 0f, 0f, 0, true, 35f);
        var blocker = Enumerable.Range(0, 16).SelectMany(index =>
        {
            float offset = index * 200f;
            return new[]
            {
                (new Vector3(45 + offset, -20, 50), new Vector3(55 + offset, -20, 50),
                    new Vector3(55 + offset, 120, 50)),
                (new Vector3(45 + offset, -20, 50), new Vector3(55 + offset, 120, 50),
                    new Vector3(45 + offset, 120, 50))
            };
        }).ToArray();
        LevelCollisionScene collision = LevelCollisionScene.FromTriangles(blocker);

        StaticLightingGenerationSettings CreateSettings(StaticLightingBakeBackend backend) => new()
        {
            Backend = backend,
            TextureResolution = 64,
            WorkerThreads = 2,
            AmbientIntensity = 0.12f,
            ShadowSampleCount = 16,
            DefaultLightSourceRadius = 0f
        };

        StaticLightingBakeResult managed = new StaticLightingBaker([textureTarget, vertexTarget], [light],
            collision, CreateSettings(StaticLightingBakeBackend.CSharp)).Bake();
        var nativeProgress = new CollectingProgress<StaticLightingBuildProgress>();
        StaticLightingBakeResult native = new StaticLightingBaker([textureTarget, vertexTarget], [light],
            collision, CreateSettings(StaticLightingBakeBackend.NativeCpp))
            .Bake(detailedProgress: nativeProgress);

        Assert.AreEqual(StaticLightingBakeBackend.NativeCpp, native.Backend);
        Assert.IsGreaterThan(0, native.NativeBvhNodeCount);
        Assert.IsGreaterThan(0L, native.NativeRayTriangleTests);
        Assert.IsGreaterThan(0d, native.NativeSamplesPerSecond);
        StaticLightingBuildProgress completedTexture = nativeProgress.Snapshot().Last(update =>
            update.Phase.StartsWith("Baking native texture samples", StringComparison.Ordinal));
        Assert.AreEqual(completedTexture.Total, completedTexture.Current);
        StringAssert.Contains(completedTexture.DisplayText, "NativeParity.pcc");
        Assert.AreEqual(managed.OccludedSamples, native.OccludedSamples);
        Assert.AreEqual(managed.AverageVisibility, native.AverageVisibility, 0.000001d);
        for (int coefficient = 0; coefficient < managed.Components[0].Texture.CoefficientImages.Count;
             coefficient++)
        {
            byte[] expected = managed.Components[0].Texture.CoefficientImages[coefficient];
            byte[] actual = native.Components[0].Texture.CoefficientImages[coefficient];
            Assert.AreEqual(expected.Length, actual.Length);
            int maximumDifference = expected.Zip(actual, (left, right) => Math.Abs(left - right)).Max();
            Assert.IsLessThanOrEqualTo(1, maximumDifference,
                $"Texture coefficient {coefficient} diverged from the managed backend.");
        }
        for (int index = 0; index < managed.Components[1].Vertex.DirectionalSamples.Length; index++)
        {
            QuantizedDirectionalLightSample expected = managed.Components[1].Vertex.DirectionalSamples[index];
            QuantizedDirectionalLightSample actual = native.Components[1].Vertex.DirectionalSamples[index];
            Assert.AreEqual(expected.Coefficient2, actual.Coefficient2);
            Assert.AreEqual(expected.Coefficient3, actual.Coefficient3);
            Assert.AreEqual(managed.Components[1].Vertex.SimpleSamples[index].Coefficient,
                native.Components[1].Vertex.SimpleSamples[index].Coefficient);
        }
    }

    [TestMethod]
    public void HighResolutionScheduler_CapsLiveReceiverBuffersAndRebalancesWorkers()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("BakeConcurrency.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null,
            indexed: false);
        component.WriteProperties([]);
        StaticLightingMeshTarget target = CreateQuadTarget(component);
        StaticLightingMeshTarget[] targets = Enumerable.Repeat(target, 16).ToArray();

        target.TextureResolution = 1024;
        Assert.AreEqual((2, 8), StaticLightingBaker.CalculateBakeConcurrency(targets, 16));
        target.TextureResolution = 2048;
        Assert.AreEqual((1, 16), StaticLightingBaker.CalculateBakeConcurrency(targets, 16));
        target.TextureResolution = 512;
        Assert.AreEqual((8, 2), StaticLightingBaker.CalculateBakeConcurrency(targets, 16));
    }

    [TestMethod]
    public void NativeBvhBuild_LargeSceneReportsDeterminateProgress()
    {
        if (!StaticLightingBaker.IsNativeBackendAvailable)
            Assert.Inconclusive("Build LightmassNative before running native-backend tests.");

        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("NativeBvhProgress.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_Bvh", "StaticMeshComponent", null,
            indexed: false);
        component.WriteProperties([]);
        const int side = 256;
        var source = new List<(Vector3 A, Vector3 B, Vector3 C, ExportEntry Source,
            int SourceTriangleIndex)>(side * side);
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
            source.Add((new Vector3(x, y, 0), new Vector3(x + 0.75f, y, 0),
                new Vector3(x, y + 0.75f, 0), component, source.Count));

        LevelCollisionScene collision = LevelCollisionScene.FromTriangles(source, buildBvh: false);
        var progress = new CollectingProgress<StaticLightingBuildProgress>();
        using var context = new NativeStaticLightingContext(collision, 1f, progress,
            "Global/bulk | Native C++ | Automatic | 1,024px");

        Assert.AreEqual(side * side, collision.TriangleCount);
        Assert.IsGreaterThan(1, context.BvhNodeCount);
        StaticLightingBuildProgress[] updates = progress.Snapshot();
        StaticLightingBuildProgress completed = updates.Last(update =>
            update.Phase == "Building native occluder BVH");
        Assert.AreEqual(source.Count, completed.Current);
        Assert.AreEqual(source.Count, completed.Total);
        StringAssert.Contains(completed.DisplayText, $"Export UIndex {component.UIndex:N0}");
        StringAssert.Contains(completed.DisplayText, "NativeBvhProgress.pcc");
        Console.WriteLine($"Native BVH: {source.Count:N0} triangles, {context.BvhNodeCount:N0} nodes, " +
                          $"{context.BvhBuildMilliseconds:F1} ms.");
    }

    [TestMethod]
    public void NativeSceneScan_DeduplicatesMeshesTransformsInstancesAndCullsLights()
    {
        if (!StaticLightingBaker.IsNativeBackendAvailable)
            Assert.Inconclusive("Build LightmassNative before running native scene-scan tests.");

        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("NativeSceneScan.pcc", MEGame.LE3);
        ExportEntry mesh = package.CreateExport("StaticMesh_0", "StaticMesh", null, indexed: false);
        mesh.WriteProperties([]);
        var lod = new StaticMeshRenderData
        {
            NumVertices = 3,
            PositionVertexBuffer = new PositionVertexBuffer
            {
                NumVertices = 3,
                VertexData = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY]
            },
            VertexBuffer = new StaticMeshVertexBuffer
            {
                NumVertices = 3,
                NumTexCoords = 1,
                bUseFullPrecisionUVs = true,
                VertexData =
                [
                    new StaticMeshVertexBuffer.StaticMeshFullVertex { FullPrecisionUVs = [Vector2.Zero] },
                    new StaticMeshVertexBuffer.StaticMeshFullVertex { FullPrecisionUVs = [Vector2.UnitX] },
                    new StaticMeshVertexBuffer.StaticMeshFullVertex { FullPrecisionUVs = [Vector2.UnitY] }
                ]
            },
            IndexBuffer = [0, 1, 2],
            Elements = [new StaticMeshElement { FirstIndex = 0, NumTriangles = 1 }]
        };
        const uint staticChannel = 1u | (1u << 2);
        NativeStaticLightingMeshInstance[] instances =
        [
            new(null!, mesh, lod, 0, Matrix4x4.Identity, staticChannel),
            new(null!, mesh, lod, 0, Matrix4x4.CreateTranslation(10, 0, 0), staticChannel)
        ];
        StaticLightingLight[] lights =
        [
            new(Guid.NewGuid(), StaticLightingLightType.Directional, Vector3.Zero, -Vector3.UnitZ,
                Vector3.One, 1f, float.MaxValue, 0, 0, staticChannel),
            new(Guid.NewGuid(), StaticLightingLightType.Point, new Vector3(0.25f, 0.25f, 1), Vector3.UnitZ,
                Vector3.One, 1f, 2f, 0, 0, staticChannel),
            new(Guid.NewGuid(), StaticLightingLightType.Directional, Vector3.Zero, -Vector3.UnitZ,
                Vector3.One, 1f, float.MaxValue, 0, 0, 1u | (1u << 3))
        ];

        var progress = new CollectingProgress<StaticLightingBuildProgress>();
        NativeStaticLightingSceneScan scan = NativeStaticLightingSceneScanner.Scan(instances, lights, 2,
            progress, "Global/bulk | Native C++ | Automatic | 1,024px");

        Assert.AreEqual(1, scan.UniqueMeshCount);
        Assert.HasCount(2, scan.Instances);
        Assert.HasCount(1, scan.Instances[0].Triangles);
        Assert.HasCount(1, scan.Instances[1].Triangles);
        Assert.IsTrue(scan.Instances[0].HasTextureCoordinates);
        Assert.AreEqual(0.5f, scan.Instances[0].SurfaceArea, 0.0001f);
        Assert.AreEqual(new Vector3(10, 0, 0), scan.Instances[1].BoundsMinimum);
        Assert.AreEqual(new Vector3(11, 1, 0), scan.Instances[1].BoundsMaximum);
        CollectionAssert.AreEqual(new[] { 0, 1 }, scan.Instances[0].RelevantLightIndices);
        CollectionAssert.AreEqual(new[] { 0 }, scan.Instances[1].RelevantLightIndices);
        Assert.IsGreaterThanOrEqualTo(0d, scan.Diagnostics.TotalScanMilliseconds);
        StaticLightingBuildProgress[] updates = progress.Snapshot();
        Assert.IsTrue(updates.Any(update => update.Phase == "Scanning native mesh topology and UVs"));
        Assert.IsTrue(updates.Any(update => update.Phase == "Transforming native mesh instances"));
        Assert.IsTrue(updates.Any(update => update.Phase == "Scanning native light relevance"));
        StaticLightingBuildProgress itemUpdate = updates.First(update => update.ExportUIndex == mesh.UIndex);
        StringAssert.Contains(itemUpdate.DisplayText, "Global/bulk | Native C++ | Automatic | 1,024px");
        StringAssert.Contains(itemUpdate.DisplayText, $"Export UIndex {mesh.UIndex:N0}");
        StringAssert.Contains(itemUpdate.DisplayText, "NativeSceneScan.pcc");
        StringAssert.Contains(itemUpdate.DisplayText, "left");
    }

    [TestMethod]
    [TestCategory("Performance")]
    public void NativeBackend_Benchmark1024Texture()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("LEX_RUN_LIGHTMASS_BENCHMARK"), "1",
                StringComparison.Ordinal))
            Assert.Inconclusive("Set LEX_RUN_LIGHTMASS_BENCHMARK=1 to run the 1024x1024 native benchmark.");
        if (!StaticLightingBaker.IsNativeBackendAvailable)
            Assert.Inconclusive("Build LightmassNative before running the native benchmark.");

        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("NativeBenchmark.pcc", MEGame.LE3);
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
            Backend = StaticLightingBakeBackend.NativeCpp,
            TextureResolution = 1024,
            WorkerThreads = 0,
            WorkTileSize = 32,
            AmbientIntensity = 0.12f,
            ShadowSampleCount = 16,
            DefaultLightSourceRadius = 0f
        };

        StaticLightingBakeResult result = new StaticLightingBaker([target], [light],
            LevelCollisionScene.FromTriangles(blocker), settings).Bake();

        Assert.AreEqual(1024, result.Components[0].Texture.Resolution);
        Assert.IsGreaterThan(1_000_000L, result.NativeSamplesProcessed);
        Assert.IsGreaterThan(0L, result.NativeRayTriangleTests);
        Assert.IsGreaterThan(0d, result.NativeSamplesPerSecond);
        Console.WriteLine($"1024x1024 native Lightmass: wall={result.BakeMilliseconds:F1} ms, " +
                          $"compute={result.NativeComputeMilliseconds:F1} ms, " +
                          $"samples={result.NativeSamplesProcessed:N0}, rays={result.RaysCast:N0}, " +
                          $"samples/s={result.NativeSamplesPerSecond:N0}, rays/s={result.NativeRaysPerSecond:N0}, " +
                          $"triangle tests={result.NativeRayTriangleTests:N0}, " +
                          $"early-outs={result.NativeAnyHitEarlyOuts:N0}.");
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
        bool occluded = collision.IsOccludedFiltered(Vector3.Zero, Vector3.UnitZ, 10f,
            component, 7, 2f, out int anyHitRejected);

        Assert.IsFalse(hit);
        Assert.IsFalse(occluded);
        Assert.AreEqual(1, rejected);
        Assert.AreEqual(1, anyHitRejected);
        Assert.IsGreaterThan(0, collision.BvhNodeCount);

        LevelCollisionScene withBlocker = LevelCollisionScene.FromTriangles(new[]
        {
            (new Vector3(-10, -10, 0.5f), new Vector3(10, -10, 0.5f), new Vector3(0, 10, 0.5f), component, 7),
            (new Vector3(-10, -10, 3f), new Vector3(10, -10, 3f), new Vector3(0, 10, 3f), component, 8)
        });
        Assert.IsTrue(withBlocker.RaycastFiltered(Vector3.Zero, Vector3.UnitZ, 10f,
            component, 7, 2f, out _, out _));
        Assert.IsTrue(withBlocker.IsOccludedFiltered(Vector3.Zero, Vector3.UnitZ, 10f,
            component, 7, 2f, out _));
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
        // the intersection about 0.0000007 UV units from the endpoint. That is less than 0.002
        // texel even at 2048 and must not be diagnosed as a chart overlap.
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
        Assert.IsFalse(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Automatic, true, 32,
            ELightMapType.LMT_2D, true), "A previous generated map must not promote a dense small prop forever.");
        Assert.IsTrue(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Automatic, true, 32,
            ELightMapType.LMT_2D, true, "GenericLargeMesh", 600f, 100_000f, 64),
            "A broad receiver remains texture mapped after replacing a generated map.");
        Assert.IsFalse(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Automatic, true, 32,
            ELightMapType.LMT_2D, true, "BioApl_Fur_TableLab03.TableLab03", 600f, 100_000f, 298),
            "A generated texture map must not keep a vertex-dense TableLab03-style prop in 2D mode.");
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
        Assert.IsFalse(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Automatic,
            true, 0, ELightMapType.LMT_1D, false, "GenericLargeDenseMesh", 600f, 100_000f, 2_000));
        Assert.IsFalse(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Automatic,
            true, 0, ELightMapType.LMT_1D, false, "GenericLargeLowPoly", 600f, 100_000f, 64),
            "Authored vertex mappings are retained for non-architectural receivers.");
        Assert.IsTrue(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Automatic,
            true, 0, ELightMapType.LMT_None, false, "GenericLargeLowPoly", 600f, 100_000f, 64));
        Assert.IsTrue(StaticLightingBaker.ShouldUseTextureMapping(StaticLightingMappingMode.Automatic,
            true, 0, ELightMapType.LMT_None, false, "GenericBroadQuad", 200f, 20_000f, 2));
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
    public void MissingLightMapCoordinateIndex_UsesUvZeroWithoutOverridingAuthoredIndex()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("LightMapCoordinateIndex.pcc",
            MEGame.LE3);
        ExportEntry mesh = package.CreateExport("StaticMesh_0", "StaticMesh", null, indexed: false);
        mesh.WriteProperties([]);
        var lod = new StaticMeshRenderData
        {
            VertexBuffer = new StaticMeshVertexBuffer { NumTexCoords = 2 }
        };
        var method = typeof(StaticLightingBaker).GetMethod("GetLightMapCoordinateIndex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.IsNotNull(method);
        Assert.AreEqual(0, (int)method.Invoke(null, [mesh, lod]));

        mesh.WriteProperty(new IntProperty(1, "LightMapCoordinateIndex"));
        Assert.AreEqual(1, (int)method.Invoke(null, [mesh, lod]));
    }

    [TestMethod]
    public void MissingLightMapCoordinateIndex_UsesRuntimeDefaultUvZero()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("ValidatedLightMapCoordinate.pcc",
            MEGame.LE3);
        ExportEntry mesh = package.CreateExport("StaticMesh_0", "StaticMesh", null, indexed: false);
        mesh.WriteProperties([]);
        var lod = new StaticMeshRenderData
        {
            NumVertices = 3,
            PositionVertexBuffer = new PositionVertexBuffer
            {
                NumVertices = 3,
                VertexData = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY]
            },
            VertexBuffer = new StaticMeshVertexBuffer
            {
                NumVertices = 3,
                NumTexCoords = 2,
                bUseFullPrecisionUVs = true,
                VertexData =
                [
                    new StaticMeshVertexBuffer.StaticMeshFullVertex
                        { FullPrecisionUVs = [new Vector2(2, 2), new Vector2(0, 0)] },
                    new StaticMeshVertexBuffer.StaticMeshFullVertex
                        { FullPrecisionUVs = [new Vector2(3, 2), new Vector2(1, 0)] },
                    new StaticMeshVertexBuffer.StaticMeshFullVertex
                        { FullPrecisionUVs = [new Vector2(2, 3), new Vector2(0, 1)] }
                ]
            },
            IndexBuffer = [0, 1, 2],
            Elements = [new StaticMeshElement { FirstIndex = 0, NumTriangles = 1 }]
        };
        var diagnosticsCache = new Dictionary<(ExportEntry Mesh, int CoordinateIndex),
            StaticLightingMappingDiagnostics>();
        var selectMethod = typeof(StaticLightingBaker).GetMethod(
            "BuildTrianglesWithRuntimeLightMapCoordinate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        object[] parameters =
            [mesh, lod, Matrix4x4.Identity, diagnosticsCache, 0, null, false, null];

        Assert.IsNotNull(selectMethod);
        selectMethod.Invoke(null, parameters);

        Assert.AreEqual(0, (int)parameters[4]);
        Assert.IsFalse((bool)parameters[6]);
        Assert.IsTrue(((StaticLightingMappingDiagnostics)parameters[7]).HasTextureMappingErrors);
        Assert.IsTrue(diagnosticsCache[(mesh, 0)].HasTextureMappingErrors);
        Assert.IsFalse(diagnosticsCache.ContainsKey((mesh, 1)));
    }

    [TestMethod]
    public void EmissiveSettings_RequireExplicitOptInAndPreserveAuthoredControls()
    {
        Assert.IsFalse(StaticLightingEmissive.TryGetSettings([], out _));
        PropertyCollection properties =
        [
            new StructProperty("LightmassPrimitiveSettings",
            [
                new BoolProperty(true, "bUseEmissiveForStaticLighting"),
                new FloatProperty(2.5f, "EmissiveBoost"),
                new FloatProperty(3f, "EmissiveLightFalloffExponent"),
                new FloatProperty(750f, "EmissiveLightExplicitInfluenceRadius"),
                new BoolProperty(true, "bUseTwoSidedLighting")
            ], "LightmassSettings")
        ];

        Assert.IsTrue(StaticLightingEmissive.TryGetSettings(properties,
            out StaticLightingEmissiveSettings settings));
        Assert.AreEqual(2.5f, settings.Boost, 0.0001f);
        Assert.AreEqual(3f, settings.FalloffExponent, 0.0001f);
        Assert.AreEqual(750f, settings.ExplicitInfluenceRadius, 0.0001f);
        Assert.IsTrue(settings.TwoSided);
    }

    [TestMethod]
    public void EmissivePreprocessing_ReducesLargePanelsAndRejectsTinyDimGeometry()
    {
        IReadOnlyList<StaticLightingTriangle> panel = CreatePanelTriangles(32, 64f);
        var settings = new StaticLightingEmissiveSettings(1f, 2f, 0f, false);

        IReadOnlyList<StaticLightingAreaEmitter> samples = StaticLightingEmissive.CreateAreaEmitterSamples(
            panel, new Vector3(4f, 2f, 1f), settings, 0);

        Assert.IsGreaterThan(1, samples.Count);
        Assert.IsLessThanOrEqualTo(StaticLightingEmissive.MaximumSamplesPerSection, samples.Count);
        Assert.IsLessThan(panel.Count / 100, samples.Count);
        Assert.AreEqual(2048f * 2048f, samples.Sum(sample => sample.Area), 1f);

        var a = new StaticLightingVertex(Vector3.Zero, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY,
            Vector2.Zero);
        var b = a with { Position = new Vector3(0.1f, 0, 0) };
        var c = a with { Position = new Vector3(0, 0.1f, 0) };
        IReadOnlyList<StaticLightingAreaEmitter> ignored = StaticLightingEmissive.CreateAreaEmitterSamples(
            [new StaticLightingTriangle(a, b, c)], new Vector3(0.1f), settings, 0);
        Assert.IsEmpty(ignored);

        var brightB = a with { Position = Vector3.UnitX };
        var brightC = a with { Position = Vector3.UnitY };
        IReadOnlyList<StaticLightingAreaEmitter> retained = StaticLightingEmissive.CreateAreaEmitterSamples(
            [new StaticLightingTriangle(a, brightB, brightC)], new Vector3(0.1f),
            settings with { Boost = 100f }, 0);
        Assert.HasCount(1, retained);
    }

    [TestMethod]
    public void EmissiveIndex_CullsByBoundsChannelsAndSourceAndCapsReceiverWork()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("EmissiveIndex.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null,
            indexed: false);
        ExportEntry excluded = package.CreateExport("StaticMeshComponent_1", "StaticMeshComponent", null,
            indexed: false);
        const uint staticChannel = 1u | (1u << 2);
        const uint dynamicChannel = 1u | (1u << 3);
        StaticLightingAreaEmitter near = new(new Vector3(0, 0, 10), -Vector3.UnitZ, Vector3.One,
            100f, 100f, 2f, staticChannel, false, source);
        var index = new StaticLightingAreaEmitterIndex(
        [
            near,
            near with { LightingChannelMask = dynamicChannel },
            near with { Source = excluded },
            near with { Position = new Vector3(1000, 0, 0), InfluenceRadius = 10f }
        ]);

        StaticLightingAreaEmitter[] selected = index.Query(new Vector3(-1), new Vector3(1),
            staticChannel, excluded);
        Assert.HasCount(1, selected);
        Assert.AreSame(source, selected[0].Source);

        var many = Enumerable.Range(1, 40).Select(intensity => near with
        {
            Position = new Vector3(intensity - 20f, 0, 10),
            Radiance = new Vector3(intensity),
            LightingChannelMask = 0,
            Source = null
        });
        StaticLightingAreaEmitter[] capped = new StaticLightingAreaEmitterIndex(many)
            .Query(new Vector3(-100), new Vector3(100), 0);
        Assert.HasCount(StaticLightingAreaEmitterIndex.MaximumEmittersPerReceiver, capped);
        Assert.IsGreaterThanOrEqualTo(17f, capped.Min(emitter => emitter.Radiance.X));
    }

    [TestMethod]
    public void EmissiveAreaSamples_IlluminateReceiversAndRespectOcclusion()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("EmissiveBake.pcc", MEGame.LE3);
        ExportEntry receiver = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null,
            indexed: false);
        ExportEntry emitterSource = package.CreateExport("StaticMeshComponent_1", "StaticMeshComponent", null,
            indexed: false);
        ExportEntry blockerSource = package.CreateExport("StaticMeshComponent_2", "StaticMeshComponent", null,
            indexed: false);
        receiver.WriteProperties([]);
        StaticLightingMeshTarget target = CreateQuadTarget(receiver, useTextureMapping: false);
        var emitter = new StaticLightingAreaEmitter(new Vector3(50, 50, 100), -Vector3.UnitZ,
            new Vector3(4f, 2f, 1f), 4_000f, 500f, 2f, 0, false, emitterSource);
        var emitterIndex = new StaticLightingAreaEmitterIndex([emitter]);
        var settings = new StaticLightingGenerationSettings
        {
            MappingMode = StaticLightingMappingMode.Vertex1D,
            AmbientIntensity = 0f,
            WorkerThreads = 1
        };
        StaticLightingBakeResult visible = new StaticLightingBaker([target], [],
            LevelCollisionScene.FromTriangles(Array.Empty<(Vector3 A, Vector3 B, Vector3 C)>()),
            settings, emissiveEmitterIndex: emitterIndex).Bake();
        LevelCollisionScene blockedScene = LevelCollisionScene.FromTriangles(new[]
        {
            (new Vector3(-100, -100, 50), new Vector3(200, -100, 50), new Vector3(200, 200, 50),
                blockerSource, 0),
            (new Vector3(-100, -100, 50), new Vector3(200, 200, 50), new Vector3(-100, 200, 50),
                blockerSource, 1)
        });
        StaticLightingBakeResult blocked = new StaticLightingBaker([target], [], blockedScene,
            settings, emissiveEmitterIndex: emitterIndex).Bake();

        Assert.AreEqual(1, visible.EmissiveEmitterCount);
        Assert.IsGreaterThan(0L, visible.EmissiveSamplesEvaluated);
        Assert.IsGreaterThanOrEqualTo(visible.EmissiveRaysCast, visible.EmissiveSamplesEvaluated);
        Assert.IsGreaterThan(0d, visible.AverageDirectContribution);
        Assert.AreEqual(0d, blocked.AverageDirectContribution, 0.000001d);
        Assert.IsGreaterThan(0L, blocked.OccludedSamples);
    }

    [TestMethod]
    public void MappingMode_ForcedTextureBakeFallsBackToVertexAndContinues()
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

        Assert.HasCount(1, result.Components);
        Assert.IsNull(result.Components[0].Texture);
        Assert.IsNotNull(result.Components[0].Vertex);
        Assert.AreEqual(0, result.TextureMappedComponentCount);
        Assert.AreEqual(1, result.VertexMappedComponentCount);
        Assert.AreEqual(1, result.UvFallbackComponentCount);
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

        Assert.AreEqual(1, result.TextureMappedComponentCount);
        Assert.IsNotNull(result.Components[0].Texture);
        Assert.IsTrue(result.Components[0].Diagnostics.MappedTexelCount >= (collapseToPoint ? 1 : 70));
        Assert.IsTrue(mapping.HasRepairableTextureMappingIssues);
        Assert.IsFalse(mapping.HasTextureMappingErrors);
    }

    [TestMethod]
    [DataRow(StaticLightingTextureFormat.DXT1, "PF_DXT1")]
    [DataRow(StaticLightingTextureFormat.ARGB, "PF_A8R8G8B8")]
    public void TextureWriter_PersistsSelectedFormatAsLightMap2D(
        StaticLightingTextureFormat textureFormat, string expectedEngineFormat)
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
                [componentBake, textureBake, "LEX_TextureWriter", cachePath, cache, textureFormat, streamingTextures, 0];
            installMethod.Invoke(null, arguments);
            int textureCount = (int)arguments[7];

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
            int textureUIndex = ((LightMap_2D)reopenedMap).Texture1;
            EnumProperty format = reopened.GetUExport(textureUIndex).GetProperty<EnumProperty>("Format");
            Assert.IsNotNull(format);
            Assert.AreEqual(expectedEngineFormat, format.Value.Name);
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
    public void UnlitAndMixedMaterials_UseComponentCompatibleReceiverPolicy()
    {
        var properties = new PropertyCollection
        {
            new EnumProperty("MLM_Unlit", "EMaterialLightingModel", MEGame.LE3, "LightingModel"),
            new BoolProperty(true, "bUsedWithStaticLighting")
        };

        Assert.IsFalse(StaticLightingBaker.CanMaterialReceiveStaticLighting(properties));
        properties.AddOrReplaceProp(new EnumProperty("MLM_Phong", "EMaterialLightingModel", MEGame.LE3,
            "LightingModel"));
        Assert.IsTrue(StaticLightingBaker.CanMaterialReceiveStaticLighting(properties));
        properties.AddOrReplaceProp(new BoolProperty(false, "bUsedWithStaticLighting"));
        Assert.IsFalse(StaticLightingBaker.CanMaterialReceiveStaticLighting(properties));

        Assert.IsFalse(StaticLightingBaker.CanComponentReceiveStaticLighting(
            hasResolvedMaterial: true, hasLitMaterial: false, allLitMaterialsCompatible: true));
        Assert.IsTrue(StaticLightingBaker.CanComponentReceiveStaticLighting(
            hasResolvedMaterial: true, hasLitMaterial: true, allLitMaterialsCompatible: true),
            "A mixed lit/unlit mesh must remain a receiver for its lit sections.");
        Assert.IsFalse(StaticLightingBaker.CanComponentReceiveStaticLighting(
            hasResolvedMaterial: true, hasLitMaterial: true, allLitMaterialsCompatible: false),
            "One unsupported lit section makes a component-scoped mapping unsafe.");
        Assert.IsTrue(StaticLightingBaker.CanComponentReceiveStaticLighting(
            hasResolvedMaterial: false, hasLitMaterial: false, allLitMaterialsCompatible: false));
    }

    [TestMethod]
    public void StaticLightingShaderPolicy_RequiresMatchingVertexAndPixelShaders()
    {
        var shaders = new UMultiMap<NameReference, ShaderReference>();
        var shaderMap = new MaterialShaderMap
        {
            MeshShaderMaps =
            [
                new MeshShaderMap
                {
                    VertexFactoryType = "FLocalVertexFactory",
                    Shaders = shaders
                }
            ]
        };

        shaders.Add("TBasePassVertexShaderFDirectionalVertexLightMapPolicyFNoDensityPolicy",
            new ShaderReference());
        Assert.IsFalse(StaticLightingBaker.HasStaticLightingShaderPolicy(shaderMap,
            useTextureMapping: false));
        shaders.Add("TBasePassPixelShaderFDirectionalVertexLightMapPolicyNoSkyLight",
            new ShaderReference());
        Assert.IsTrue(StaticLightingBaker.HasStaticLightingShaderPolicy(shaderMap,
            useTextureMapping: false));
        Assert.IsFalse(StaticLightingBaker.HasStaticLightingShaderPolicy(shaderMap,
            useTextureMapping: true));
    }

    [TestMethod]
    public void ResetExcludedReceiver_RemovesGeneratedMappingAndRestoresSelectedLightingPolicy()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("UnlitReceiver.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null,
            indexed: false);
        component.WriteProperties([
            new BoolProperty(false, "bAcceptsLights"),
            new BoolProperty(true, "bForceDirectLightMap"),
            new BoolProperty(true, "bUsePrecomputedShadows"),
            new ArrayProperty<StructProperty>([CommonStructs.GuidProp(Guid.NewGuid())], "IrrelevantLights")
            {
                Reference = "Guid"
            }
        ]);
        component.WriteBinary(new StaticMeshComponent
        {
            LODData =
            [
                new StaticMeshComponentLODInfo
                {
                    LightMap = new LightMap_1D
                    {
                        LightMapType = ELightMapType.LMT_1D,
                        LightGuids = [],
                        DirectionalSamples = [],
                        SimpleSamples = []
                    },
                    ShadowMaps = [7],
                    ShadowVertexBuffers = [8]
                }
            ]
        });

        var resetMethod = typeof(StaticLightingWriter).GetMethod("ResetExcludedReceiver",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(resetMethod);
        resetMethod.Invoke(null, [component, false]);

        StaticMeshComponent reset = component.GetBinaryData<StaticMeshComponent>();
        Assert.AreEqual(ELightMapType.LMT_None, reset.LODData[0].LightMap.LightMapType);
        Assert.IsEmpty(reset.LODData[0].ShadowMaps);
        Assert.IsEmpty(reset.LODData[0].ShadowVertexBuffers);
        Assert.IsFalse(component.GetProperty<BoolProperty>("bForceDirectLightMap").Value);
        Assert.IsFalse(component.GetProperty<BoolProperty>("bUsePrecomputedShadows").Value);
        Assert.IsNull(component.GetProperty<ArrayProperty<StructProperty>>("IrrelevantLights"));

        resetMethod.Invoke(null, [component, true]);
        Assert.IsTrue(component.GetProperty<BoolProperty>("bAcceptsLights").Value);
        Assert.IsTrue(component.GetProperty<BoolProperty>("bAcceptsDynamicLights").Value);
        Assert.IsTrue(component.GetProperty<BoolProperty>("bCastDynamicShadow").Value);
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
    public void StaticLightingProperties_SelectUe3EncodingAndDisableRuntimeLightAcceptance()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("StaticLightingProperties.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null,
            indexed: false);
        component.WriteProperties([
            new EnumProperty("LMET_Vector", nameof(LightMapEncodingType), package.Game, "LightMapEncoding")
        ]);
        var method = typeof(StaticLightingWriter).GetMethod("ApplyStaticLightingProperties",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.IsNotNull(method);
        method.Invoke(null, [component, Array.Empty<Guid>()]);

        Assert.IsFalse(component.GetProperty<BoolProperty>("bAcceptsLights").Value);
        Assert.IsFalse(component.GetProperty<BoolProperty>("bAcceptsDynamicLights").Value);
        Assert.IsTrue(component.GetProperty<BoolProperty>("bForceDirectLightMap").Value);
        Assert.IsTrue(component.GetProperty<BoolProperty>("bUsePrecomputedShadows").Value);
        Assert.AreEqual("LMET_UE3", component.GetProperty<EnumProperty>("LightMapEncoding").Value.Name);
    }

    private static IReadOnlyList<StaticLightingTriangle> CreatePanelTriangles(int cells, float cellSize)
    {
        var triangles = new List<StaticLightingTriangle>(cells * cells * 2);
        for (int y = 0; y < cells; y++)
        for (int x = 0; x < cells; x++)
        {
            Vector3 origin = new(x * cellSize, y * cellSize, 0);
            var a = new StaticLightingVertex(origin, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, Vector2.Zero);
            var b = a with { Position = origin + new Vector3(cellSize, 0, 0) };
            var c = a with { Position = origin + new Vector3(cellSize, cellSize, 0) };
            var d = a with { Position = origin + new Vector3(0, cellSize, 0) };
            triangles.Add(new StaticLightingTriangle(a, b, c));
            triangles.Add(new StaticLightingTriangle(a, c, d));
        }
        return triangles;
    }

    private static StaticLightingMeshTarget CreateQuadTarget(ExportEntry component, uint lightingChannelMask = 0,
        bool useTextureMapping = true)
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
            UseTextureMapping = useTextureMapping
        };
    }

    private sealed class CollectingProgress<T> : IProgress<T>
    {
        private readonly object gate = new();
        private readonly List<T> values = [];

        public void Report(T value)
        {
            lock (gate)
                values.Add(value);
        }

        public T[] Snapshot()
        {
            lock (gate)
                return values.ToArray();
        }
    }
}
