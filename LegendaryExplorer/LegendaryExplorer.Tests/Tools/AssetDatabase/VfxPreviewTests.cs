using LegendaryExplorer.Tools.AssetDatabase.VFXPreview;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Numerics;

namespace LegendaryExplorer.Tests.Tools.AssetDatabase;

[TestClass]
public class VfxPreviewTests
{
    [TestMethod]
    public void InGameShaderPreviewIsEnabledByDefault()
    {
        var context = new VfxPreviewRenderContext();

        Assert.IsTrue(context.UseGameShader);
    }

    [TestMethod]
    public void CurveDistributionInterpolatesSamples()
    {
        var distribution = new VfxCurveFloatDistribution([0, 10, 20]);

        Assert.AreEqual(5, distribution.Evaluate(0.25f, 0), 0.0001f);
        Assert.AreEqual(15, distribution.Evaluate(0.75f, 0), 0.0001f);
    }

    [TestMethod]
    public void RawFloatDistributionUsesTimeAndRandomRanges()
    {
        var distribution = new VfxRawFloatDistribution([0, 10, 20, 30], 2, 2, 0, 1, -1);

        Assert.AreEqual(5, distribution.Evaluate(0, 0.5f), 0.0001f);
        Assert.AreEqual(25, distribution.Evaluate(1, 0.5f), 0.0001f);
        Assert.AreEqual(15, distribution.Evaluate(0.5f, 0.5f), 0.0001f);
    }

    [TestMethod]
    public void RawVectorDistributionUsesTimeAndRandomRanges()
    {
        var distribution = new VfxRawVectorDistribution(
            [Vector3.Zero, Vector3.One, new Vector3(2), new Vector3(4)],
            2,
            2,
            0,
            1,
            new Vector3(-1));

        Assert.AreEqual(new Vector3(0.5f), distribution.Evaluate(0, 0.5f));
        Assert.AreEqual(new Vector3(3), distribution.Evaluate(1, 0.5f));
        Assert.AreEqual(new Vector3(1.75f), distribution.Evaluate(0.5f, 0.5f));
    }

    [TestMethod]
    public void RawDistributionTimeScaleIsSamplesPerSecondRatherThanNormalizedMultiplier()
    {
        var distribution = new VfxRawFloatDistribution(
            Enumerable.Range(0, 21).Select(value => (float)value).ToArray(),
            1,
            1,
            0,
            20,
            -1);

        Assert.AreEqual(5, distribution.Evaluate(0.25f, 0), 0.0001f);
        Assert.AreEqual(10, distribution.Evaluate(0.5f, 0), 0.0001f);
        Assert.AreEqual(20, distribution.Evaluate(1, 0), 0.0001f);
    }

    [TestMethod]
    public void BillboardDistanceIncludesOrbitOffset()
    {
        var particle = new VfxParticle
        {
            Position = new Vector3(1, 2, 3),
            OrbitOffset = new Vector3(4, 0, 0)
        };

        Assert.AreEqual(38, VfxBillboardRenderer.DistanceSquared(particle, Vector3.Zero), 0.0001f);
    }

    [TestMethod]
    public void PropertyCoverageRetainsUnsupportedInterpreterProperties()
    {
        var definition = new VfxPreviewDefinition();
        definition.PropertyCoverage.Add(new VfxPropertyCoverage(
            "ParticleSystem.Emitter.Module",
            "ParticleModuleExample",
            "ExampleProperty",
            VfxPropertyCoverageStatus.Unsupported));

        VfxPropertyCoverage coverage = definition.PropertyCoverage[0];
        Assert.AreEqual("ExampleProperty", coverage.PropertyName);
        Assert.AreEqual(VfxPropertyCoverageStatus.Unsupported, coverage.Status);
    }

    [DataTestMethod]
    [DataRow("BioVFX.Textures.BubbleSprite_NRM", "BubbleSprite_NRM")]
    [DataRow("BioVFX.Distortion.Fx_distort_06", "Fx_distort_06")]
    [DataRow("BioVFX.Cube_Maps.Ref_Cube_Map", "Ref_Cube_Map")]
    [DataRow("BioVFX.Textures.FireNormalMap", "FireNormalMap")]
    public void AuxiliaryMaterialTexturesAreNotUsedAsParticleColor(string path, string name)
    {
        Assert.IsTrue(VfxPreviewRenderContext.IsAuxiliaryParticleTexture(path, name));
    }

    [DataTestMethod]
    [DataRow("BioVFX.Textures.BubbleSprite", "BubbleSprite")]
    [DataRow("BioVFX.Textures.vfx_smoke_thick", "vfx_smoke_thick")]
    [DataRow("BioVFX.Textures.Fire_Opacity", "Fire_Opacity")]
    public void ParticleColorTexturesAreNotClassifiedAsAuxiliary(string path, string name)
    {
        Assert.IsFalse(VfxPreviewRenderContext.IsAuxiliaryParticleTexture(path, name));
    }

    [DataTestMethod]
    [DataRow("PF_DXT3")]
    [DataRow("PF_DXT5")]
    [DataRow("PF_A8R8G8B8")]
    [DataRow("PF_A8")]
    public void AlphaCapableParticleTextureFormatsPreserveTextureAlpha(string format)
    {
        Assert.IsTrue(VfxPreviewRenderContext.TextureFormatHasAlpha(format));
    }

    [DataTestMethod]
    [DataRow("PF_DXT1")]
    [DataRow("PF_R8G8B8")]
    [DataRow("PF_BC5")]
    public void NoAlphaParticleTextureFormatsRequireGraphDerivedCoverage(string format)
    {
        Assert.IsFalse(VfxPreviewRenderContext.TextureFormatHasAlpha(format));
    }

    [TestMethod]
    public void PreviewLodDefaultsToHighestDetailWithoutCameraDistance()
    {
        Assert.AreEqual(0, ParticleSystemSourceAdapter.SelectPreviewLodIndex(
            [0, 3000, 6000],
            [new(false), new(false), new(false)],
            null));
    }

    [TestMethod]
    public void PreviewLodUsesSerializedDistanceThresholds()
    {
        Assert.AreEqual(1, ParticleSystemSourceAdapter.SelectPreviewLodIndex(
            [0, 3000, 6000],
            [new(false), new(false), new(false)],
            4500));
    }

    [TestMethod]
    public void FixedBoundsTransformUsesAllEightCorners()
    {
        var local = new VfxBounds(new Vector3(-1, -2, -3), new Vector3(1, 2, 3));
        Matrix4x4 transform = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 20, 30);

        VfxBounds world = VfxBoundsMath.Transform(local, transform);

        AssertVector(new Vector3(8, 19, 27), world.Minimum);
        AssertVector(new Vector3(12, 21, 33), world.Maximum);
    }

    [TestMethod]
    public void PreviewGridTransformCentersAndScalesOversizedBoundsInsideGrid()
    {
        var bounds = new VfxBounds(new Vector3(-2000, -1000, -500), new Vector3(2000, 1000, 500));

        Matrix4x4 transform = VfxPreviewDefinition.CreateGridFittingTransform(bounds);
        VfxBounds centered = VfxBoundsMath.Transform(bounds, transform);

        AssertVector(new Vector3(-400, -200, 0), centered.Minimum);
        AssertVector(new Vector3(400, 200, 200), centered.Maximum);
        Assert.AreEqual(0.2f, transform.M11, 0.0001f);
        Assert.AreEqual(transform.M11, transform.M22, 0.0001f);
        Assert.AreEqual(transform.M11, transform.M33, 0.0001f);
    }

    [TestMethod]
    public void PreviewGridTransformAlsoScalesSmallBoundsToConsistentInitialFraming()
    {
        var bounds = new VfxBounds(new Vector3(-10, -20, -5), new Vector3(10, 20, 5));

        Matrix4x4 transform = VfxPreviewDefinition.CreateGridFittingTransform(bounds);
        VfxBounds fitted = VfxBoundsMath.Transform(bounds, transform);

        AssertVector(new Vector3(-200, -400, 0), fitted.Minimum);
        AssertVector(new Vector3(200, 400, 200), fitted.Maximum);
        Assert.AreEqual(20, transform.M11, 0.0001f);
    }

    [TestMethod]
    public void BillboardSortingUsesOriginCenteredPreviewPosition()
    {
        var particle = new VfxParticle { Position = new Vector3(110, 0, 0), Size = Vector3.One };
        Matrix4x4 transform = Matrix4x4.CreateTranslation(-100, 0, 0);

        float distance = VfxBillboardRenderer.DistanceSquared(particle, Vector3.Zero, transform);

        Assert.AreEqual(100, distance, 0.0001f);
    }

    [TestMethod]
    public void DynamicBoundsIncludeSizeOrbitAndLocalTransform()
    {
        var definition = new VfxPreviewDefinition { SystemTransform = Matrix4x4.CreateTranslation(10, 0, 0) };
        definition.Emitters.Add(new VfxEmitterDefinition
        {
            UseLocalSpace = true,
            Duration = 1,
            Loops = 1,
            Bursts = [new VfxBurst(0, 1)],
            Lifetime = new VfxConstantDistribution<float>(1),
            InitialLocation = new VfxConstantDistribution<Vector3>(new Vector3(2, 0, 0)),
            InitialSize = new VfxConstantDistribution<Vector3>(new Vector3(4, 6, 1)),
            SpawnInitializers = { new VfxOrbitSpawnInitializer(new VfxConstantDistribution<Vector3>(new Vector3(3, 0, 0)), new VfxConstantDistribution<Vector3>(Vector3.Zero), new VfxConstantDistribution<Vector3>(Vector3.Zero), false, false, false) }
        });
        var simulation = new VfxSimulation { Loop = false };
        simulation.Load(definition);
        simulation.Tick(0.01f);

        Assert.IsTrue(simulation.TryGetDynamicBounds(out Vector3 minimum, out Vector3 maximum));
        AssertVector(new Vector3(13, -3, -3), minimum);
        AssertVector(new Vector3(17, 3, 3), maximum);
    }

    [TestMethod]
    public void DynamicBoundsScaleParticlePositionAndSizeWithPreviewTransform()
    {
        var definition = new VfxPreviewDefinition { SystemTransform = Matrix4x4.CreateScale(2) };
        definition.Emitters.Add(new VfxEmitterDefinition
        {
            Duration = 1,
            Loops = 1,
            Bursts = [new VfxBurst(0, 1)],
            Lifetime = new VfxConstantDistribution<float>(1),
            InitialLocation = new VfxConstantDistribution<Vector3>(new Vector3(2, 0, 0)),
            InitialSize = new VfxConstantDistribution<Vector3>(new Vector3(4, 6, 1))
        });
        var simulation = new VfxSimulation { Loop = false };
        simulation.Load(definition);
        simulation.Tick(0.01f);

        Assert.IsTrue(simulation.TryGetDynamicBounds(out Vector3 minimum, out Vector3 maximum));
        AssertVector(new Vector3(0, -6, -6), minimum);
        AssertVector(new Vector3(8, 6, 6), maximum);
    }

    [TestMethod]
    public void SizeMultiplyLifeIsAppliedOnceInUnrealUnits()
    {
        var definition = new VfxPreviewDefinition();
        definition.Emitters.Add(new VfxEmitterDefinition
        {
            Duration = 1,
            Loops = 1,
            Bursts = [new VfxBurst(0, 1)],
            Lifetime = new VfxConstantDistribution<float>(1),
            InitialSize = new VfxConstantDistribution<Vector3>(new Vector3(20, 30, 1)),
            SizeOverLife = new VfxConstantDistribution<Vector3>(new Vector3(0.5f, 2, 1))
        });
        var simulation = new VfxSimulation { Loop = false };
        simulation.Load(definition);
        simulation.Tick(0.01f);

        AssertVector(new Vector3(10, 60, 1), simulation.Emitters[0].Particles[0].Size);
    }

    [TestMethod]
    public void BillboardSubUVSelectsDeclaredAtlasCell()
    {
        var emitter = new VfxEmitterDefinition
        {
            SubImagesHorizontal = 4,
            SubImagesVertical = 2
        };

        VfxBillboardRenderer.GetSubUVs(emitter, 5, out Vector2 minimum, out Vector2 maximum);

        Assert.AreEqual(new Vector2(0.25f, 0.5f), minimum);
        Assert.AreEqual(new Vector2(0.5f, 1), maximum);
    }

    [TestMethod]
    public void SubUVMovieAdvancesFromDeclaredStartingFrame()
    {
        var definition = new VfxPreviewDefinition { Name = "SubUV" };
        definition.Emitters.Add(new VfxEmitterDefinition
        {
            Duration = 1,
            Loops = 1,
            SpawnRate = new VfxConstantDistribution<float>(0),
            Bursts = [new VfxBurst(0, 1)],
            Lifetime = new VfxConstantDistribution<float>(1),
            SubImagesHorizontal = 4,
            SubImagesVertical = 2,
            SubUVFrameRate = new VfxConstantDistribution<float>(10),
            SubUVStartingFrame = 2
        });
        var simulation = new VfxSimulation { Loop = false };
        simulation.Load(definition);

        simulation.Tick(0.2f);

        VfxParticle particle = simulation.Emitters[0].Particles[0];
        Assert.AreEqual(1 + (particle.Age * 10), particle.SubImageIndex, 0.0001f);
    }

    [TestMethod]
    public void VelocityOverLifeMultipliesInitialVelocityWithoutCompounding()
    {
        VfxEmitterDefinition emitter = CreateDefinition().Emitters[0];
        emitter = new VfxEmitterDefinition
        {
            Duration = emitter.Duration,
            Loops = emitter.Loops,
            Bursts = [new VfxBurst(0, 1)],
            Lifetime = new VfxConstantDistribution<float>(1),
            InitialVelocity = new VfxConstantDistribution<Vector3>(new Vector3(2, 3, 4)),
            VelocityOverLife = new VfxConstantDistribution<Vector3>(new Vector3(0.5f, 2, 0))
        };
        var definition = new VfxPreviewDefinition { Name = "Velocity" };
        definition.Emitters.Add(emitter);
        var simulation = new VfxSimulation { Loop = false };
        simulation.Load(definition);

        simulation.Tick(0.2f);

        Assert.AreEqual(new Vector3(1, 6, 0), simulation.Emitters[0].Particles[0].Velocity);
    }

    [TestMethod]
    public void AbsoluteVelocityOverLifeReplacesInitialVelocity()
    {
        var definition = new VfxPreviewDefinition { Name = "AbsoluteVelocity" };
        definition.Emitters.Add(new VfxEmitterDefinition
        {
            Duration = 1,
            Loops = 1,
            Bursts = [new VfxBurst(0, 1)],
            Lifetime = new VfxConstantDistribution<float>(1),
            InitialVelocity = new VfxConstantDistribution<Vector3>(new Vector3(8)),
            VelocityOverLife = new VfxConstantDistribution<Vector3>(new Vector3(1, 2, 3)),
            VelocityOverLifeIsAbsolute = true
        });
        var simulation = new VfxSimulation { Loop = false };
        simulation.Load(definition);

        simulation.Tick(0.2f);

        Assert.AreEqual(new Vector3(1, 2, 3), simulation.Emitters[0].Particles[0].Velocity);
    }

    [TestMethod]
    public void ColorScaleOverLifeMultipliesParticleColor()
    {
        var definition = new VfxPreviewDefinition { Name = "ColorScale" };
        definition.Emitters.Add(new VfxEmitterDefinition
        {
            Duration = 1,
            Loops = 1,
            Bursts = [new VfxBurst(0, 1)],
            Lifetime = new VfxConstantDistribution<float>(1),
            InitialColor = new VfxConstantDistribution<Vector4>(new Vector4(0.8f, 0.6f, 0.4f, 1)),
            ColorScaleOverLife = new VfxConstantDistribution<Vector4>(new Vector4(0.5f, 0.25f, 2, 0.75f))
        });
        var simulation = new VfxSimulation { Loop = false };
        simulation.Load(definition);

        simulation.Tick(0.2f);

        Assert.AreEqual(new Vector4(0.4f, 0.15f, 0.8f, 0.75f), simulation.Emitters[0].Particles[0].Color);
    }

    [TestMethod]
    public void CylinderSurfaceSpawnHonorsHeightAxisAndAxisRestrictions()
    {
        VfxEmitterDefinition emitter = CreateCylinderEmitter(surfaceOnly: true, velocity: false, radialVelocity: true);
        var definition = new VfxPreviewDefinition { Name = "Cylinder" };
        definition.Emitters.Add(emitter);
        var simulation = new VfxSimulation { Loop = false };
        simulation.Load(definition);

        simulation.Tick(0.1f);

        Vector3 position = simulation.Emitters[0].Particles[0].Position;
        Assert.AreEqual(10, MathF.Sqrt((position.X * position.X) + (position.Y * position.Y)), 0.0001f);
        Assert.IsGreaterThanOrEqualTo(0, position.X);
        Assert.IsLessThanOrEqualTo(0, position.Y);
        Assert.IsGreaterThanOrEqualTo(0, position.Z);
        Assert.IsLessThanOrEqualTo(2, position.Z);
    }

    [TestMethod]
    public void CylinderRadialVelocityExcludesHeightAxisAndPreservesModuleOrder()
    {
        VfxEmitterDefinition emitter = CreateCylinderEmitter(surfaceOnly: true, velocity: true, radialVelocity: true);
        emitter.SpawnInitializers.Insert(0, new VfxVelocitySpawnInitializer(
            new VfxConstantDistribution<Vector3>(new Vector3(0, 0, 3))));
        var definition = new VfxPreviewDefinition { Name = "CylinderVelocity" };
        definition.Emitters.Add(emitter);
        var simulation = new VfxSimulation { Loop = false };
        simulation.Load(definition);

        simulation.Tick(0.1f);

        VfxParticle particle = simulation.Emitters[0].Particles[0];
        Assert.AreEqual(3, particle.BaseVelocity.Z, 0.0001f);
        Assert.AreEqual(20, MathF.Sqrt((particle.BaseVelocity.X * particle.BaseVelocity.X) + (particle.BaseVelocity.Y * particle.BaseVelocity.Y)), 0.0001f);
    }

    [TestMethod]
    public void RestartFullyResetsDeterministicSimulation()
    {
        var definition = CreateDefinition();
        var simulation = new VfxSimulation { Loop = false };
        simulation.Load(definition);
        simulation.Tick(0.2f);
        VfxParticle firstRun = simulation.Emitters[0].Particles[0];

        simulation.Restart();
        simulation.Tick(0.2f);
        VfxParticle secondRun = simulation.Emitters[0].Particles[0];

        Assert.AreEqual(0.2f, simulation.Time, 0.0001f);
        Assert.AreEqual(firstRun.Random, secondRun.Random);
        Assert.AreEqual(firstRun.Position, secondRun.Position);
        Assert.AreEqual(firstRun.Rotation, secondRun.Rotation);
    }

    [TestMethod]
    public void SimulationProcessesBurstAndContinuousSpawn()
    {
        var simulation = new VfxSimulation { Loop = false };
        simulation.Load(CreateDefinition());

        simulation.Tick(0.2f);

        Assert.AreEqual(5, simulation.ParticleCount);
    }

    [TestMethod]
    public void BillboardFacesCameraAndPreservesDimensions()
    {
        VfxBillboardBasis basis = VfxBillboardMath.CreateBasis(
            Vector3.UnitY,
            Vector3.UnitZ,
            Vector3.UnitX,
            Vector3.Zero,
            VfxScreenAlignment.Rectangle,
            VfxAxisLock.None,
            0);
        var particle = new VfxParticle { Position = new Vector3(10, 20, 30), Size = new Vector3(4, 2, 1) };
        var emitter = new VfxEmitterDefinition
        {
            ScreenAlignment = VfxScreenAlignment.Rectangle,
            PivotOffset = new Vector2(0.5f, 0),
            SourceAspect = new Vector2(2, 1)
        };
        System.Span<Vector3> corners = stackalloc Vector3[4];

        VfxBillboardMath.CreateQuad(particle, emitter, basis, corners);

        Assert.AreEqual(-1, Vector3.Dot(basis.Normal, Vector3.UnitX), 0.0001f);
        Assert.AreEqual(8, Vector3.Distance(corners[0], corners[1]), 0.0001f);
        Assert.AreEqual(2, Vector3.Distance(corners[1], corners[2]), 0.0001f);
        Vector3 center = (corners[0] + corners[2]) * 0.5f;
        Assert.AreEqual(particle.Position + (basis.Right * 4), center);
    }

    [TestMethod]
    public void SquareBillboardUsesXSizeWhenCookedFireDataLeavesYAtZero()
    {
        VfxBillboardBasis basis = VfxBillboardMath.CreateBasis(
            Vector3.UnitY,
            Vector3.UnitZ,
            Vector3.UnitX,
            Vector3.Zero,
            VfxScreenAlignment.Square,
            VfxAxisLock.None,
            0);
        var particle = new VfxParticle { Size = new Vector3(10, 0, 0) };
        var emitter = new VfxEmitterDefinition();
        System.Span<Vector3> corners = stackalloc Vector3[4];

        VfxBillboardMath.CreateQuad(particle, emitter, basis, corners);

        Assert.AreEqual(10, Vector3.Distance(corners[0], corners[1]), 0.0001f);
        Assert.AreEqual(10, Vector3.Distance(corners[1], corners[2]), 0.0001f);
    }

    [TestMethod]
    public void LockedAxisRemainsFixedAsCameraChanges()
    {
        VfxBillboardBasis first = VfxBillboardMath.CreateBasis(Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, Vector3.Zero,
            VfxScreenAlignment.CameraFacing, VfxAxisLock.PositiveZ, 0);
        VfxBillboardBasis second = VfxBillboardMath.CreateBasis(Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY, Vector3.Zero,
            VfxScreenAlignment.CameraFacing, VfxAxisLock.PositiveZ, 0);

        Assert.AreEqual(Vector3.UnitZ, first.Up);
        Assert.AreEqual(Vector3.UnitZ, second.Up);
        Assert.AreEqual(0, Vector3.Dot(first.Right, first.Up), 0.0001f);
        Assert.AreEqual(0, Vector3.Dot(second.Right, second.Up), 0.0001f);
    }

    private static VfxPreviewDefinition CreateDefinition()
    {
        var definition = new VfxPreviewDefinition { Name = "Test" };
        definition.Emitters.Add(new VfxEmitterDefinition
        {
            Duration = 0.5f,
            Loops = 1,
            SpawnRate = new VfxConstantDistribution<float>(10),
            Bursts = [new VfxBurst(0, 3)],
            Lifetime = new VfxConstantDistribution<float>(1),
            InitialVelocity = new VfxUniformVectorDistribution(Vector3.UnitX, Vector3.One),
            InitialRotation = new VfxUniformFloatDistribution(0, 1)
        });
        return definition;
    }

    private static VfxEmitterDefinition CreateCylinderEmitter(bool surfaceOnly, bool velocity, bool radialVelocity)
    {
        var emitter = new VfxEmitterDefinition
        {
            Duration = 1,
            Loops = 1,
            Bursts = [new VfxBurst(0, 1)],
            Lifetime = new VfxConstantDistribution<float>(1)
        };
        emitter.SpawnInitializers.Add(new VfxCylinderSpawnInitializer(
            new VfxConstantDistribution<Vector3>(Vector3.Zero),
            new VfxConstantDistribution<float>(10),
            new VfxConstantDistribution<float>(4),
            new VfxConstantDistribution<float>(2),
            VfxCylinderHeightAxis.Z,
            surfaceOnly,
            velocity,
            radialVelocity,
            true,
            false,
            false,
            true,
            false,
            false));
        return emitter;
    }

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.AreEqual(expected.X, actual.X, 0.0001f);
        Assert.AreEqual(expected.Y, actual.Y, 0.0001f);
        Assert.AreEqual(expected.Z, actual.Z, 0.0001f);
    }
}
