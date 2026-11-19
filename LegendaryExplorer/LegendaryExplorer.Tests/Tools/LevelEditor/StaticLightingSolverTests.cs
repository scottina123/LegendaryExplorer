using LegendaryExplorer.Lightmaps.Solver;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CodecDirectional = LegendaryExplorer.Lightmaps.Codec.Directional;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

/// <summary>Scottina's Lightmass Baker's light solver backends: the conversion to the solver's job and back to the baker's coefficients</summary>
[TestClass]
public class StaticLightingSolverTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    #region Backend choice

    [TestMethod]
    public void DefaultBackend_IsTheGpuSolverWhenAGpuExistsElseTheCpuSolver()
    {
        Assert.AreEqual(StaticLightingBakeBackend.Gpu, StaticLightingBakeBackends.DefaultBackend(gpuAvailable: true));
        Assert.AreEqual(StaticLightingBakeBackend.Cpu, StaticLightingBakeBackends.DefaultBackend(gpuAvailable: false));
        Assert.AreEqual(StaticLightingBakeBackends.DefaultBackend(StaticLightingBakeBackends.IsGpuAvailable),
            StaticLightingBakeBackends.DefaultBackend());
    }

    [TestMethod]
    public void OnlyGpuAndCpuUseTheLightSolver_AndClassicSettingsKeepTheirDefaults()
    {
        Assert.IsTrue(StaticLightingBakeBackends.UsesLightSolver(StaticLightingBakeBackend.Gpu));
        Assert.IsTrue(StaticLightingBakeBackends.UsesLightSolver(StaticLightingBakeBackend.Cpu));
        Assert.IsFalse(StaticLightingBakeBackends.UsesLightSolver(StaticLightingBakeBackend.CSharp));
        Assert.IsFalse(StaticLightingBakeBackends.UsesLightSolver(StaticLightingBakeBackend.NativeCpp));
        Assert.AreEqual("Native C++", StaticLightingBakeBackends.DisplayName(StaticLightingBakeBackend.NativeCpp));
        Assert.AreEqual("C#", StaticLightingBakeBackends.DisplayName(StaticLightingBakeBackend.CSharp));

        var settings = new StaticLightingGenerationSettings();
        Assert.AreEqual(StaticLightingBakeBackend.CSharp, settings.Backend);
        Assert.AreEqual(2, settings.SolverBounces);
        Assert.AreEqual(64, settings.SolverGatherRays);
        settings.Validate();
        new StaticLightingGenerationSettings { Backend = StaticLightingBakeBackend.Gpu }.Validate();
        new StaticLightingGenerationSettings { Backend = StaticLightingBakeBackend.Cpu, SolverBounces = 0 }.Validate();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new StaticLightingGenerationSettings { SolverBounces = -1 }.Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new StaticLightingGenerationSettings { SolverBounces = 9 }.Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new StaticLightingGenerationSettings { SolverGatherRays = 3 }.Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new StaticLightingGenerationSettings { SolverGatherRays = 2048 }.Validate());
    }

    [TestMethod]
    public void SolverSettings_UseTheScottinaProfileAndLinearUnscaledBasisWeighting()
    {
        SolverSettings settings = StaticLightingSolverConversion.CreateSettings(new StaticLightingGenerationSettings
        {
            Backend = StaticLightingBakeBackend.Cpu,
            ShadowSampleCount = 12,
            SolverBounces = 3,
            SolverGatherRays = 128,
            ShadowBias = 2f
        });
        Assert.AreEqual(SolverProfile.Scottina, settings.Profile);
        Assert.AreEqual(SolverDevice.Cpu, settings.Device);
        Assert.AreEqual(1f, settings.DirectionalCoefficientScale);
        Assert.AreEqual(1f, settings.DirectionalCoefficientFalloffPower);
        Assert.AreEqual(12, settings.ShadowRays);
        Assert.AreEqual(0, settings.PenumbraShadowRays);
        Assert.AreEqual(3, settings.Bounces);
        Assert.AreEqual(128, settings.GatherRays);
        Assert.AreEqual(2f, settings.NormalOffset);
        Assert.AreEqual(SolverDevice.Gpu, StaticLightingSolverConversion.CreateSettings(
            new StaticLightingGenerationSettings { Backend = StaticLightingBakeBackend.Gpu }).Device);
    }

    #endregion

    #region Directional basis and packing

    [TestMethod]
    public void SolverTangentFrame_TurnsTheCodecBasisIntoTheBakersBasis()
    {
        var random = new Random(1234);
        for (int iteration = 0; iteration < 200; iteration++)
        {
            Vector3 normal = RandomUnit(random);
            Vector3 tangent = Vector3.Normalize(Vector3.Cross(normal, RandomUnit(random)));
            // A slightly skewed bitangent: the identity must hold for any frame, not only orthonormal ones
            Vector3 bitangent = Vector3.Normalize(Vector3.Cross(normal, tangent) + RandomUnit(random) * 0.1f);
            Vector3 direction = RandomUnit(random);
            (Vector3 solverTangent, Vector3 solverBitangent) = StaticLightingSolverConversion.ToSolverTangentFrame(tangent, bitangent);

            Vector3 baker = Vector3.Normalize(new Vector3(Vector3.Dot(direction, tangent), Vector3.Dot(direction, bitangent),
                Vector3.Dot(direction, normal)));
            Vector3 solver = Vector3.Normalize(new Vector3(Vector3.Dot(direction, solverTangent),
                Vector3.Dot(direction, solverBitangent), Vector3.Dot(direction, normal)));
            for (int basis = 0; basis < 3; basis++)
                Assert.AreEqual(MathF.Max(0f, Vector3.Dot(baker, StaticLightingSolverConversion.ScottinaDirectionalBasis[basis])),
                    MathF.Max(0f, Vector3.Dot(solver, CodecDirectional.Basis[basis])), 1e-5f, $"basis {basis}");
        }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void PackCoefficients_MatchesLightmassNativePacking(bool compressed)
    {
        // A sample in a tilted, rotated frame, lit by the environment, a directional light and a point light, none shadowed
        Vector3 normal = Vector3.Normalize(new Vector3(0.2f, -0.3f, 1f));
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(new Vector3(0.3f, 1f, 0.1f), normal));
        Vector3 bitangent = Vector3.Cross(normal, tangent);
        var sample = new StaticLightingBaker.StaticLightingSurfaceSample(new Vector3(10, 20, 30), normal, tangent, bitangent,
            normal, null, -1, 2f);
        Vector3 environment = new(0.12f, 0.1f, 0.08f);
        Vector3 toDirectional = Vector3.Normalize(new Vector3(0.6f, 0.2f, 0.8f));
        var directional = new StaticLightingBaker.PreparedLight
        {
            Type = StaticLightingLightType.Directional,
            Direction = -toDirectional,
            Radiance = new Vector3(1.5f, 1.2f, 0.7f),
            SampleCount = 1,
            DirectionalSurfaceToLight = [toDirectional]
        };
        Vector3 pointPosition = sample.Position + new Vector3(-120f, 60f, 200f);
        var point = new StaticLightingBaker.PreparedLight
        {
            Type = StaticLightingLightType.Point,
            Position = pointPosition,
            Direction = Vector3.UnitX,
            Radiance = new Vector3(3f, 2f, 4f),
            RadiusSquared = 600f * 600f,
            InverseRadius = 1f / 600f,
            SampleCount = 1
        };
        float[] native = BakeNative(sample, environment, [directional, point], compressed);

        // The lighting by hand: environment, then each light's irradiance (simple) and unshadowed radiance weighted by the basis
        Vector3 simple = environment;
        var basis = new Vector3[3];
        void Add(Vector3 surfaceToLight, Vector3 unshadowed)
        {
            float normalDotLight = MathF.Max(0f, Vector3.Dot(normal, surfaceToLight));
            simple += unshadowed * normalDotLight;
            Vector3 tangentDirection = Vector3.Normalize(new Vector3(Vector3.Dot(surfaceToLight, tangent),
                Vector3.Dot(surfaceToLight, bitangent), Vector3.Dot(surfaceToLight, normal)));
            for (int index = 0; index < 3; index++)
                basis[index] += unshadowed * MathF.Max(0f,
                    Vector3.Dot(tangentDirection, StaticLightingSolverConversion.ScottinaDirectionalBasis[index]));
        }
        Add(toDirectional, directional.Radiance);
        Vector3 delta = pointPosition - sample.Position;
        float distance = delta.Length();
        float attenuation = MathF.Pow(1f - distance * distance / (600f * 600f), 2f);
        Add(delta / distance, point.Radiance * attenuation);

        int count = compressed ? 3 : 4;
        Vector3[][] packed = Enumerable.Range(0, count).Select(_ => new Vector3[1]).ToArray();
        StaticLightingSolverConversion.PackCoefficients(simple, basis[0], basis[1], basis[2],
            MathF.Max(environment.X, MathF.Max(environment.Y, environment.Z)), compressed, packed, 0);
        for (int coefficient = 0; coefficient < count; coefficient++)
        {
            Vector3 expected = packed[coefficient][0];
            Vector3 actual = new(native[coefficient * 3], native[coefficient * 3 + 1], native[coefficient * 3 + 2]);
            AssertClose(expected, actual, 1e-4f, $"coefficient {coefficient}");
        }
    }

    [TestMethod]
    public void PackCoefficients_DarkSampleIsZero()
    {
        Vector3[][] packed = Enumerable.Range(0, 3).Select(_ => new Vector3[1]).ToArray();
        StaticLightingSolverConversion.PackCoefficients(Vector3.Zero, Vector3.One, Vector3.One, Vector3.One, 0f, true, packed, 0);
        Assert.AreEqual(Vector3.Zero, packed[0][0]);
        Assert.AreEqual(Vector3.Zero, packed[1][0]);
        Assert.AreEqual(Vector3.Zero, packed[2][0]);
    }

    #endregion

    #region Lights

    [DataTestMethod]
    [DataRow(StaticLightingLightType.Point, 150f)]
    [DataRow(StaticLightingLightType.Point, 520f)]
    [DataRow(StaticLightingLightType.Spot, 150f)]
    [DataRow(StaticLightingLightType.Spot, 400f)]
    [DataRow(StaticLightingLightType.Directional, 0f)]
    public void ConvertLight_GivesTheNativeIrradianceAtAKnownDistance(StaticLightingLightType type, float distance)
    {
        var settings = new StaticLightingGenerationSettings { ShadowSampleCount = 1 };
        Vector3 normal = Vector3.UnitZ;
        var sample = new StaticLightingBaker.StaticLightingSurfaceSample(Vector3.Zero, normal, Vector3.UnitX, Vector3.UnitY,
            normal, null, -1, 1f);
        // From above and to the side, so the cosine, falloff and (for the spot) the cone all matter
        Vector3 toLight = Vector3.Normalize(new Vector3(0.3f, 0.2f, 1f));
        Vector3 spotAxis = Vector3.Normalize(-toLight + new Vector3(0.12f, 0f, 0f));
        var light = new StaticLightingLight(Guid.NewGuid(), type, toLight * distance,
            type == StaticLightingLightType.Directional ? -toLight : spotAxis, new Vector3(1f, 0.8f, 0.5f), 3f, 600f,
            InnerConeAngleDegrees: 5f, OuterConeAngleDegrees: 25f, LightingChannelMask: 0);

        StaticLightingBaker.PreparedLight prepared = PrepareLike(light);
        float[] native = BakeNative(sample, Vector3.Zero, [prepared], compressed: false);
        Vector3 nativeIrradiance = new(native[9], native[10], native[11]);
        Assert.IsTrue(nativeIrradiance.X > 0f, "The test light must reach the sample.");

        SolverLight converted = StaticLightingSolverConversion.ConvertLight(light, settings);
        AssertClose(nativeIrradiance, EvaluateScottinaContractModel(converted, sample.Position, normal), 1e-4f, type.ToString());
        Assert.AreEqual(0f, converted.SourceSize);
        Assert.IsTrue(converted.CastShadows);
    }

    [TestMethod]
    public void ConvertLight_SoftShadowSourcesMatchTheBakersDiskSampling()
    {
        var settings = new StaticLightingGenerationSettings { ShadowSampleCount = 8, DefaultLightSourceRadius = 16f, DirectionalSourceAngleDegrees = 0.5f };
        var point = new StaticLightingLight(Guid.NewGuid(), StaticLightingLightType.Point, Vector3.Zero, Vector3.UnitX, Vector3.One, 1f,
            40f, 0f, 0f, 0);
        Assert.AreEqual(10f, StaticLightingSolverConversion.ConvertLight(point, settings).SourceSize); // min(16, radius / 4)
        Assert.AreEqual(5f, StaticLightingSolverConversion.ConvertLight(point with { Radius = 1000f, SourceRadius = 5f }, settings).SourceSize);
        var directional = point with { Type = StaticLightingLightType.Directional, Direction = -Vector3.UnitZ };
        SolverLight sun = StaticLightingSolverConversion.ConvertLight(directional, settings);
        Assert.AreEqual(0.5f * MathF.PI / 180f, sun.SourceSize, 1e-7f);
        AssertClose(-Vector3.UnitZ, sun.Direction, 1e-6f, "direction of travel");
        Assert.AreEqual(0f, StaticLightingSolverConversion.ConvertLight(point with { CastsStaticShadow = false }, settings).SourceSize);
    }

    [TestMethod]
    public void ConvertLight_SkyIsAnOccludedUniformSky()
    {
        var sky = new StaticLightingLight(Guid.NewGuid(), StaticLightingLightType.Sky, Vector3.Zero, Vector3.UnitZ,
            new Vector3(0.2f, 0.3f, 0.4f), 2f, 0f, 0f, 0f, 0);
        SolverLight converted = StaticLightingSolverConversion.ConvertLight(sky, new StaticLightingGenerationSettings());
        Assert.AreEqual(SolverLightType.Sky, converted.Type);
        AssertClose(new Vector3(0.4f, 0.6f, 0.8f) / MathF.PI, converted.Color, 1e-6f, "upper");
        AssertClose(converted.Color, converted.LowerColor, 0f, "lower");
        Assert.IsTrue(converted.CastShadows);
        AssertClose(new Vector3(0.12f) / MathF.PI, StaticLightingSolverConversion.ConvertEnvironment(0.12f), 1e-7f, "environment");
    }

    #endregion

    #region The bake through the solver

    [DataTestMethod]
    [DataRow(MEGame.LE3, true)]
    [DataRow(MEGame.LE3, false)]
    [DataRow(MEGame.LE1, true)]
    [DataRow(MEGame.LE1, false)]
    public void SolverBake_OfDirectLightMatchesTheClassicBake(MEGame game, bool textureMapping)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("SolverParity.pcc", game);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null, indexed: false);
        component.WriteProperties([]);
        StaticLightingMeshTarget target = CreateQuadTarget(component, textureMapping);
        var light = new StaticLightingLight(Guid.NewGuid(), StaticLightingLightType.Directional, Vector3.Zero,
            Vector3.Normalize(new Vector3(0.4f, -0.3f, -1f)), new Vector3(1f, 0.9f, 0.7f), 1.5f, float.MaxValue, 0f, 0f, 0);
        LevelCollisionScene collision = LevelCollisionScene.FromTriangles(Array.Empty<(Vector3 A, Vector3 B, Vector3 C)>());
        StaticLightingGenerationSettings Settings(StaticLightingBakeBackend backend) => new()
        {
            Backend = backend,
            TextureResolution = 64,
            WorkerThreads = 2,
            // The classic environment is flat and unoccluded; the solver's isn't, so this compares direct light alone
            AmbientIntensity = 0f,
            ShadowSampleCount = 1
        };

        StaticLightingBakeResult classic = new StaticLightingBaker([target], [light], collision,
            Settings(StaticLightingBakeBackend.CSharp)).Bake();
        var solverBaker = new StaticLightingBaker([target], [light], collision, Settings(StaticLightingBakeBackend.Gpu))
        {
            SolveOverride = (job, _, _) => SolveDirectOnly(job)
        };
        StaticLightingBakeResult solved = solverBaker.Bake();

        Assert.AreEqual(StaticLightingBakeBackend.Gpu, solved.Backend);
        CollectionAssert.AreEqual(classic.Components[0].LightGuids, solved.Components[0].LightGuids);
        CollectionAssert.AreEqual(classic.Components[0].IrrelevantLightGuids, solved.Components[0].IrrelevantLightGuids);
        if (textureMapping)
        {
            StaticLightingTextureBake expected = classic.Components[0].Texture, actual = solved.Components[0].Texture;
            Assert.AreEqual(expected.CoefficientImages.Count, actual.CoefficientImages.Count);
            Assert.AreEqual(expected.CoordinateScale, actual.CoordinateScale);
            Assert.AreEqual(expected.CoordinateBias, actual.CoordinateBias);
            for (int coefficient = 0; coefficient < expected.CoefficientImages.Count; coefficient++)
            {
                AssertClose(expected.ScaleVectors[coefficient], actual.ScaleVectors[coefficient], 1e-4f, $"scale {coefficient}");
                int difference = expected.CoefficientImages[coefficient].Zip(actual.CoefficientImages[coefficient],
                    (left, right) => Math.Abs(left - right)).Max();
                Assert.IsLessThanOrEqualTo(1, difference, $"coefficient {coefficient}");
            }
        }
        else
        {
            StaticLightingVertexBake expected = classic.Components[0].Vertex, actual = solved.Components[0].Vertex;
            for (int coefficient = 0; coefficient < expected.ScaleVectors.Count; coefficient++)
                AssertClose(expected.ScaleVectors[coefficient], actual.ScaleVectors[coefficient], 1e-4f, $"scale {coefficient}");
            for (int vertex = 0; vertex < expected.SimpleSamples.Length; vertex++)
            {
                AssertColorClose(expected.SimpleSamples[vertex].Coefficient, actual.SimpleSamples[vertex].Coefficient);
                AssertColorClose(expected.DirectionalSamples[vertex].Coefficient1, actual.DirectionalSamples[vertex].Coefficient1);
                AssertColorClose(expected.DirectionalSamples[vertex].Coefficient2, actual.DirectionalSamples[vertex].Coefficient2);
                AssertColorClose(expected.DirectionalSamples[vertex].Coefficient3, actual.DirectionalSamples[vertex].Coefficient3);
            }
        }
    }

    [TestMethod]
    public void SolverBake_BuildsOneReceiverPerComponentWithItsLightsAndAGridForTextures()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("SolverJob.pcc", MEGame.LE3);
        ExportEntry textureComponent = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null, indexed: false);
        textureComponent.WriteProperties([]);
        ExportEntry vertexComponent = package.CreateExport("StaticMeshComponent_1", "StaticMeshComponent", null, indexed: false);
        vertexComponent.WriteProperties([]);
        StaticLightingMeshTarget textureTarget = CreateQuadTarget(textureComponent, true, lightingChannelMask: 0b011);
        StaticLightingMeshTarget vertexTarget = CreateQuadTarget(vertexComponent, false, lightingChannelMask: 0b101);
        var sun = new StaticLightingLight(Guid.NewGuid(), StaticLightingLightType.Directional, Vector3.Zero, -Vector3.UnitZ,
            Vector3.One, 1f, float.MaxValue, 0f, 0f, 0b011);
        var sky = new StaticLightingLight(Guid.NewGuid(), StaticLightingLightType.Sky, Vector3.Zero, Vector3.UnitZ,
            new Vector3(0.1f), 1f, 0f, 0f, 0f, 0b101);
        // Something to bounce off and to occlude the sky: a shadow-casting quad above the receivers
        LevelCollisionScene collision = LevelCollisionScene.FromTriangles(new[]
        {
            (new Vector3(0, 0, 300), new Vector3(100, 0, 300), new Vector3(100, 100, 300), (ExportEntry)null, 0),
            (new Vector3(0, 0, 300), new Vector3(100, 100, 300), new Vector3(0, 100, 300), (ExportEntry)null, 1)
        });
        var emitters = new StaticLightingAreaEmitterIndex([new StaticLightingAreaEmitter(new Vector3(50, 50, 200), -Vector3.UnitZ,
            new Vector3(4f), 100f, 1000f, 2f, 0, false)]);
        var settings = new StaticLightingGenerationSettings
        {
            Backend = StaticLightingBakeBackend.Cpu,
            TextureResolution = 64,
            AmbientIntensity = 0.2f,
            SolverBounces = 3,
            SolverGatherRays = 32
        };
        SolverJob captured = null;
        var baker = new StaticLightingBaker([textureTarget, vertexTarget], [sun, sky], collision, settings, null, emitters)
        {
            SolveOverride = (job, _, _) =>
            {
                captured = job;
                return SolveDirectOnly(job);
            }
        };
        StaticLightingBakeResult result = baker.Bake();

        Assert.IsNotNull(captured);
        Assert.AreEqual(SolverProfile.Scottina, captured.Settings.Profile);
        Assert.AreEqual(SolverDevice.Cpu, captured.Settings.Device);
        Assert.AreEqual(3, captured.Settings.Bounces);
        Assert.AreEqual(32, captured.Settings.GatherRays);
        AssertClose(new Vector3(0.2f) / MathF.PI, captured.Scene.Environment, 1e-7f, "environment");
        Assert.AreEqual(2, captured.Scene.Lights.Count);
        Assert.AreEqual(SolverLightType.Sky, captured.Scene.Lights[1].Type);
        // Two occluder triangles and the emitter's two-triangle square
        Assert.AreEqual(4, captured.Scene.Triangles.Count);
        Assert.IsTrue(captured.Scene.Triangles.Take(2).All(triangle =>
            triangle.Flags == (SolverTriangleFlags.CastShadow | SolverTriangleFlags.TwoSidedShadow)));
        Assert.IsTrue(captured.Scene.Triangles.Skip(2).All(triangle =>
            triangle.Flags.HasFlag(SolverTriangleFlags.ShadowIndirectOnly) &&
            captured.Scene.Materials[(int)triangle.Material].Emissive is not null));
        Assert.AreEqual(2, captured.Receivers.Count);
        CollectionAssert.AreEqual(new[] { 0 }, captured.Receivers[0].LightmapLights);
        CollectionAssert.AreEqual(new[] { 1 }, captured.Receivers[1].LightmapLights);
        Assert.IsTrue(captured.Receivers.All(receiver => receiver.GatherIndirect && receiver.ShadowMapLights.Length == 0));
        Assert.AreEqual(2, captured.Grids.Length);
        Assert.IsNotNull(captured.Grids[0]);
        Assert.IsNull(captured.Grids[1]);
        Assert.AreEqual(64, captured.Grids[0].Width);
        int[] gridSamples = captured.Grids[0].Samples.Where(index => index >= 0).ToArray();
        Assert.AreEqual(result.Components[0].Diagnostics.MappedTexelCount, gridSamples.Length);
        Assert.IsTrue(gridSamples.All(index => captured.Samples[index].Receiver == 0));
        Assert.AreEqual(gridSamples.Length + vertexTarget.Vertices.Count, captured.Samples.Count);
        Assert.IsTrue(captured.Samples.Skip(gridSamples.Length).All(sample => sample.Receiver == 1));
        Assert.AreNotEqual(captured.Samples[0].Instance, captured.Samples[^1].Instance);
        // The frame handed over is the baker's turned onto the Codec basis: (-B, T, N)
        AssertClose(-Vector3.UnitY, captured.Samples[0].Tangent, 1e-6f, "tangent");
        AssertClose(Vector3.UnitX, captured.Samples[0].Bitangent, 1e-6f, "bitangent");
        Assert.IsTrue(captured.Samples.All(sample => sample.TexelRadius > 0f));
    }

    [TestMethod]
    public void SolverBake_WithoutTheSolverAsksForTheClassicDevice()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("SolverUnavailable.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null, indexed: false);
        component.WriteProperties([]);
        var baker = new StaticLightingBaker([CreateQuadTarget(component, true)], [],
            LevelCollisionScene.FromTriangles(Array.Empty<(Vector3 A, Vector3 B, Vector3 C)>()),
            new StaticLightingGenerationSettings { Backend = StaticLightingBakeBackend.Gpu })
        {
            SolveOverride = (_, _, _) => throw new SolverUnavailableException("No device.")
        };
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => baker.Bake());
        StringAssert.Contains(exception.Message, StaticLightingBakeBackends.ClassicDisplayName);
        Assert.IsInstanceOfType<SolverUnavailableException>(exception.InnerException);
    }

    [TestMethod]
    public void SolverBake_ReportsSolverProgressThroughTheBakersProgress()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("SolverProgress.pcc", MEGame.LE3);
        ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", null, indexed: false);
        component.WriteProperties([]);
        var reported = new List<StaticLightingBuildProgress>();
        var progress = new SynchronousProgress<StaticLightingBuildProgress>(reported.Add);
        var baker = new StaticLightingBaker([CreateQuadTarget(component, false)], [],
            LevelCollisionScene.FromTriangles(Array.Empty<(Vector3 A, Vector3 B, Vector3 C)>()),
            new StaticLightingGenerationSettings { Backend = StaticLightingBakeBackend.Cpu })
        {
            SolveOverride = (job, solverProgress, cancel) =>
            {
                cancel.ThrowIfCancellationRequested();
                solverProgress.Report(new SolverProgress("Gathering", 0.5));
                return SolveDirectOnly(job);
            }
        };
        baker.Bake(detailedProgress: progress);
        StaticLightingBuildProgress gathering = reported.Single(update => update.Phase == "Light solver: Gathering");
        Assert.AreEqual(500, gathering.Current);
        Assert.AreEqual(1000, gathering.Total);

        using var cancelled = new System.Threading.CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => baker.Bake(cancelled.Token));
    }

    #endregion

    #region Helpers

    /// <summary>
    /// The solver's documented Scottina model for one light with nothing in the way: radiance × (1 - (d/R)²)^FalloffExponent (point, spot)
    /// × the linear cone (spot) × N·L
    /// </summary>
    private static Vector3 EvaluateScottinaContractModel(SolverLight light, Vector3 position, Vector3 normal)
    {
        Vector3 surfaceToLight;
        float attenuation = 1f;
        if (light.Type == SolverLightType.Directional)
        {
            surfaceToLight = -light.Direction;
        }
        else
        {
            Vector3 delta = light.Position - position;
            float distance = delta.Length();
            surfaceToLight = delta / distance;
            attenuation = MathF.Pow(MathF.Max(0f, 1f - distance * distance / (light.Radius * light.Radius)), light.FalloffExponent);
            if (light.Type == SolverLightType.Spot)
                attenuation *= Math.Clamp((Vector3.Dot(-surfaceToLight, light.Direction) - light.CosOuterCone) /
                                          (light.CosInnerCone - light.CosOuterCone), 0f, 1f);
        }
        return light.Color * attenuation * MathF.Max(0f, Vector3.Dot(normal, surfaceToLight));
    }

    /// <summary>A stand-in solver: each receiver's directional lights, unshadowed, weighted by the Codec basis in the sample's frame</summary>
    private static SolverOutput SolveDirectOnly(SolverJob job)
    {
        int count = job.Samples.Count;
        var output = new SolverOutput
        {
            Simple = new Vector3[count],
            IndirectSimple = new Vector3[count],
            Directional0 = new Vector3[count],
            Directional1 = new Vector3[count],
            Directional2 = new Vector3[count]
        };
        for (int index = 0; index < count; index++)
        {
            SolverSample sample = job.Samples[index];
            foreach (int lightIndex in job.Receivers[(int)sample.Receiver].LightmapLights)
            {
                SolverLight light = job.Scene.Lights[lightIndex];
                if (light.Type != SolverLightType.Directional) continue;
                Vector3 surfaceToLight = -light.Direction;
                float normalDotLight = MathF.Max(0f, Vector3.Dot(sample.Normal, surfaceToLight));
                if (normalDotLight <= 0f) continue;
                output.Simple[index] += light.Color * normalDotLight;
                Vector3 direction = Vector3.Normalize(new Vector3(Vector3.Dot(surfaceToLight, sample.Tangent),
                    Vector3.Dot(surfaceToLight, sample.Bitangent), Vector3.Dot(surfaceToLight, sample.Normal)));
                output.Directional0[index] += light.Color * MathF.Max(0f, Vector3.Dot(direction, CodecDirectional.Basis[0]));
                output.Directional1[index] += light.Color * MathF.Max(0f, Vector3.Dot(direction, CodecDirectional.Basis[1]));
                output.Directional2[index] += light.Color * MathF.Max(0f, Vector3.Dot(direction, CodecDirectional.Basis[2]));
            }
        }
        output.Statistics.DeviceName = "test";
        return output;
    }

    /// <summary>A light prepared as the baker prepares it with one shadow sample</summary>
    private static StaticLightingBaker.PreparedLight PrepareLike(StaticLightingLight light)
    {
        Vector3 direction = Vector3.Normalize(light.Direction);
        float outerCos = MathF.Cos(light.OuterConeAngleDegrees * MathF.PI / 180f);
        float innerCos = MathF.Cos(light.InnerConeAngleDegrees * MathF.PI / 180f);
        return new StaticLightingBaker.PreparedLight
        {
            Type = light.Type,
            Position = light.Position,
            Direction = direction,
            Radiance = light.Color * light.Intensity,
            RadiusSquared = light.Radius * light.Radius,
            InverseRadius = 1f / light.Radius,
            OuterConeCos = outerCos,
            InverseConeRange = 1f / MathF.Max(0.0001f, innerCos - outerCos),
            CastsShadow = false,
            SampleCount = 1,
            DirectionalSurfaceToLight = light.Type == StaticLightingLightType.Directional ? [Vector3.Normalize(-light.Direction)] : null
        };
    }

    /// <summary>LightmassNative's coefficients for one sample, flattened (coefficient-major RGB)</summary>
    private static float[] BakeNative(StaticLightingBaker.StaticLightingSurfaceSample sample, Vector3 environment,
        StaticLightingBaker.PreparedLight[] lights, bool compressed)
    {
        if (!StaticLightingBaker.IsNativeBackendAvailable)
            Assert.Inconclusive("Build LightmassNative before running the native packing and light parity tests.");
        using var context = new NativeStaticLightingContext(
            LevelCollisionScene.FromTriangles(Array.Empty<(Vector3 A, Vector3 B, Vector3 C)>()), 1f);
        int count = compressed ? 3 : 4;
        NativeStaticLightingBake bake = context.BakeSamples([sample],
            new StaticLightingBaker.PreparedLighting(environment, lights, []), count, compressed, 1, false, null, 1, 1);
        return Enumerable.Range(0, count).SelectMany(coefficient =>
        {
            Vector3 value = bake.Coefficients[coefficient][0];
            return new[] { value.X, value.Y, value.Z };
        }).ToArray();
    }

    private static StaticLightingMeshTarget CreateQuadTarget(ExportEntry component, bool useTextureMapping, uint lightingChannelMask = 0)
    {
        var a = new StaticLightingVertex(new Vector3(0, 0, 0), Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, new Vector2(0, 0));
        var b = new StaticLightingVertex(new Vector3(100, 0, 0), Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, new Vector2(1, 0));
        var c = new StaticLightingVertex(new Vector3(100, 100, 0), Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, new Vector2(1, 1));
        var d = new StaticLightingVertex(new Vector3(0, 100, 0), Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, new Vector2(0, 1));
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

    private static Vector3 RandomUnit(Random random)
    {
        Vector3 value;
        do value = new Vector3(random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1);
        while (value.LengthSquared() is < 0.01f or > 1f);
        return Vector3.Normalize(value);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, float relativeTolerance, string what)
    {
        float tolerance = MathF.Max(1e-6f, relativeTolerance * MathF.Max(1f, expected.Length()));
        Assert.IsTrue(Vector3.Distance(expected, actual) <= tolerance, $"{what}: expected {expected}, got {actual}");
    }

    private static void AssertColorClose(LegendaryExplorerCore.SharpDX.Color expected, LegendaryExplorerCore.SharpDX.Color actual)
    {
        Assert.IsTrue(Math.Abs(expected.R - actual.R) <= 1 && Math.Abs(expected.G - actual.G) <= 1 &&
                      Math.Abs(expected.B - actual.B) <= 1 && expected.A == actual.A, $"expected {expected}, got {actual}");
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly object gate = new();
        public void Report(T value)
        {
            lock (gate) report(value);
        }
    }

    #endregion
}
