using LegendaryExplorerCore.Packages;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace LegendaryExplorer.Tools.AssetDatabase.VFXPreview;

public enum VfxScreenAlignment
{
    CameraFacing,
    Square,
    Rectangle,
    Velocity,
    TypeSpecific
}

public enum VfxAxisLock
{
    None,
    PositiveX,
    NegativeX,
    PositiveY,
    NegativeY,
    PositiveZ,
    NegativeZ,
    RotateX,
    RotateY,
    RotateZ
}

/// <summary>
/// Which renderer an emitter is dispatched to, based on the LOD's TypeDataModule class.
/// </summary>
public enum VfxEmitterRenderMode
{
    Sprite,
    Mesh,
    Beam,
    Trail,
    Unsupported
}

/// <summary>
/// EMeshScreenAlignment, as declared by ParticleModuleTypeDataMesh in Engine.
/// </summary>
public enum VfxMeshAlignment
{
    FaceCameraWithRoll,
    FaceCameraWithSpin,
    FaceCameraWithLockedAxis
}

/// <summary>
/// EMeshCameraFacingOptions, as declared by ParticleModuleTypeDataMesh in Engine.
/// </summary>
public enum VfxMeshCameraFacing
{
    XAxisFacingNoUp,
    XAxisFacingZUp,
    XAxisFacingNegativeZUp,
    XAxisFacingYUp,
    XAxisFacingNegativeYUp,
    LockedAxisZAxisFacing,
    LockedAxisNegativeZAxisFacing,
    LockedAxisYAxisFacing,
    LockedAxisNegativeYAxisFacing,
    VelocityAlignedZAxisFacing,
    VelocityAlignedNegativeZAxisFacing,
    VelocityAlignedYAxisFacing,
    VelocityAlignedNegativeYAxisFacing
}

/// <summary>
/// Data read from ParticleModuleTypeDataMesh and the mesh rotation modules of a mesh emitter.
/// </summary>
public sealed class VfxMeshEmitterDefinition
{
    public IEntry Mesh { get; init; }
    /// <summary>ParticleModuleTypeDataMesh.Pitch/Yaw/Roll, in degrees.</summary>
    public Vector3 PreRotation { get; init; }
    public bool CameraFacing { get; init; }
    public bool OverrideMaterial { get; init; }
    public VfxMeshAlignment MeshAlignment { get; init; } = VfxMeshAlignment.FaceCameraWithRoll;
    public VfxMeshCameraFacing CameraFacingOption { get; init; } = VfxMeshCameraFacing.XAxisFacingNoUp;
    public VfxAxisLock AxisLockOption { get; init; }
    /// <summary>ParticleModuleMeshMaterial.MeshMaterials, indexed by mesh section.</summary>
    public IReadOnlyList<IEntry> SectionMaterialOverrides { get; init; } = [];
    public IVfxDistribution<Vector3> StartRotation { get; init; } = new VfxConstantDistribution<Vector3>(Vector3.Zero);
    public IVfxDistribution<Vector3> StartRotationRate { get; init; } = new VfxConstantDistribution<Vector3>(Vector3.Zero);
    public IVfxDistribution<Vector3> RotationRateMultiplierOverLife { get; init; } = new VfxConstantDistribution<Vector3>(Vector3.One);
    /// <summary>Local-space bounds of the resolved mesh, filled in once the mesh has been loaded for rendering.</summary>
    public VfxBounds? LocalBounds { get; set; }
}

public enum VfxSubUVInterpolation
{
    None,
    Linear,
    LinearBlend,
    Random,
    RandomBlend
}

public enum VfxBlendMode
{
    Opaque,
    Masked,
    Translucent,
    Additive,
    Modulate,
    ModulateAndAdd,
    SoftMasked,
    AlphaComposite
}

public enum VfxOpacitySource
{
    TextureAlpha,
    TextureLuminance,
    TextureRed,
    TextureGreen,
    TextureBlue,
    One
}

/// <summary>
/// Mirrors EEmitterDynamicParameterValue from ParticleModuleParameterDynamic.
/// </summary>
public enum VfxDynamicParameterValueMethod
{
    UserSet,
    VelocityX,
    VelocityY,
    VelocityZ,
    VelocityMagnitude
}

public sealed record VfxDynamicParameterDefinition(
    IVfxDistribution<float> Value,
    VfxDynamicParameterValueMethod ValueMethod,
    bool UseEmitterTime,
    bool SpawnTimeOnly,
    bool ScaleVelocityByParamValue);

/// <summary>
/// A database-backed package candidate used when an import cannot be resolved through the game's normal
/// associated-package rules. Seek-free level packages can reference shared VFX materials that were embedded
/// in a different level package, so the asset database is the only reliable way to locate a preview copy.
/// </summary>
public readonly record struct VfxImportFallback(string FilePath, int UIndex);

public sealed class VfxParticleMaterialDefinition
{
    public VfxBlendMode BlendMode { get; set; } = VfxBlendMode.Translucent;
    /// <summary>
    /// False when the owning material's BlendMode could not be read (unresolved import, missing base material, ...),
    /// in which case <see cref="BlendMode"/> is only a guess.
    /// </summary>
    public bool BlendModeResolved { get; set; }
    public bool IsUnlit { get; set; }
    public bool TwoSided { get; set; }
    public bool DisableDepthTest { get; set; }
    public float OpacityMaskClipValue { get; set; } = 0.333f;
    public VfxOpacitySource OpacitySource { get; set; } = VfxOpacitySource.TextureAlpha;
    public Vector4 EmissiveTint { get; set; } = Vector4.One;
    /// <summary>
    /// True when the cooked material was compiled for a particle dynamic-parameter vertex factory. The local
    /// vertex-factory path used by Meshplorer cannot provide that stream, so sprites with this flag use the
    /// standard VFX renderer rather than rendering the material with missing inputs.
    /// </summary>
    public bool UsesDynamicParameter { get; set; }
    public IEntry Texture { get; set; }
    public bool IsSupported { get; set; }
    public string Warning { get; set; }
}

public enum VfxCylinderHeightAxis
{
    X,
    Y,
    Z
}

/// <summary>
/// Mirrors EParticleSortMode on ParticleModuleRequired.
/// </summary>
public enum VfxSortMode
{
    None,
    ViewProjectionDepth,
    DistanceToView,
    AgeOldestFirst,
    AgeNewestFirst
}

/// <summary>
/// Mirrors EParticleBurstMethod on ParticleModuleRequired / ParticleModuleSpawn.
/// </summary>
public enum VfxBurstMethod
{
    Instant,
    Interpolated
}

public abstract record VfxSpawnInitializer;

public sealed record VfxLocationSpawnInitializer(IVfxDistribution<Vector3> Location) : VfxSpawnInitializer;

public sealed record VfxVelocitySpawnInitializer(IVfxDistribution<Vector3> Velocity) : VfxSpawnInitializer;

public sealed record VfxCylinderSpawnInitializer(
    IVfxDistribution<Vector3> StartLocation,
    IVfxDistribution<float> StartRadius,
    IVfxDistribution<float> StartHeight,
    IVfxDistribution<float> VelocityScale,
    VfxCylinderHeightAxis HeightAxis,
    bool SurfaceOnly,
    bool Velocity,
    bool RadialVelocity,
    bool PositiveX,
    bool PositiveY,
    bool PositiveZ,
    bool NegativeX,
    bool NegativeY,
    bool NegativeZ) : VfxSpawnInitializer;

public sealed record VfxAccelerationSpawnInitializer(IVfxDistribution<Vector3> Acceleration) : VfxSpawnInitializer;

public sealed record VfxSphereSpawnInitializer(
    IVfxDistribution<Vector3> StartLocation,
    IVfxDistribution<float> StartRadius,
    IVfxDistribution<float> VelocityScale,
    bool SurfaceOnly,
    bool Velocity,
    bool PositiveX,
    bool PositiveY,
    bool PositiveZ,
    bool NegativeX,
    bool NegativeY,
    bool NegativeZ) : VfxSpawnInitializer;

public sealed record VfxRadialVelocitySpawnInitializer(IVfxDistribution<float> Speed) : VfxSpawnInitializer;

public sealed record VfxOrbitSpawnInitializer(
    IVfxDistribution<Vector3> Offset,
    IVfxDistribution<Vector3> Rotation,
    IVfxDistribution<Vector3> RotationRate,
    bool OffsetUsesEmitterTime,
    bool RotationUsesEmitterTime,
    bool RotationRateUsesEmitterTime) : VfxSpawnInitializer;

public enum VfxPreviewShadingMode
{
    Unlit,
    Lit,
    Wireframe
}

public enum VfxPreviewBackground
{
    Transparent,
    Black,
    NeutralGray
}

public interface IVfxDistribution<T>
{
    T Evaluate(float time, float random);
}

public sealed class VfxPreviewDefinition
{
    public const float PreviewGridHalfExtent = 500;
    public const float PreviewGridFitFraction = 0.8f;
    public const float PreviewGridFitSize = PreviewGridHalfExtent * 2 * PreviewGridFitFraction;

    public string Name { get; init; }
    public Matrix4x4 SystemTransform { get; set; } = Matrix4x4.Identity;
    public VfxBounds? FixedLocalBounds { get; init; }
    public IReadOnlyList<float> LodDistances { get; init; } = [];
    public IReadOnlyList<VfxLodSetting> LodSettings { get; init; } = [];
    public int SelectedLodIndex { get; init; }
    public bool LockLowestLodToHighest { get; init; }
    public float WarmupTime { get; init; }
    public float SystemDelay { get; init; }
    public float SystemDelayLow { get; init; }
    public bool UseSystemDelayRange { get; init; }
    public bool OrientZAxisTowardCamera { get; init; }
    public List<VfxEmitterDefinition> Emitters { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<VfxPropertyCoverage> PropertyCoverage { get; } = [];

    public static Matrix4x4 CreateGridFittingTransform(VfxBounds? localBounds)
    {
        if (localBounds is not { IsValid: true } bounds)
        {
            return Matrix4x4.Identity;
        }

        Vector3 center = (bounds.Minimum + bounds.Maximum) * 0.5f;
        Vector3 groundTranslation = new(-center.X, -center.Y, -bounds.Minimum.Z);
        Vector3 size = bounds.Maximum - bounds.Minimum;
        float largestDimension = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (!float.IsFinite(largestDimension) || largestDimension <= 0.0001f)
        {
            return Matrix4x4.CreateTranslation(groundTranslation);
        }

        float scale = PreviewGridFitSize / largestDimension;
        return Matrix4x4.CreateTranslation(groundTranslation) * Matrix4x4.CreateScale(scale);
    }
}

public readonly record struct VfxBounds(Vector3 Minimum, Vector3 Maximum)
{
    public bool IsValid => VfxBoundsMath.IsFinite(Minimum)
        && VfxBoundsMath.IsFinite(Maximum)
        && Minimum.X <= Maximum.X
        && Minimum.Y <= Maximum.Y
        && Minimum.Z <= Maximum.Z;
}

public readonly record struct VfxLodSetting(bool IsLit);

public static class VfxBoundsMath
{
    public static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    public static VfxBounds Transform(VfxBounds bounds, Matrix4x4 transform)
    {
        Vector3 minimum = new(float.MaxValue);
        Vector3 maximum = new(float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 local = new(
                (corner & 1) == 0 ? bounds.Minimum.X : bounds.Maximum.X,
                (corner & 2) == 0 ? bounds.Minimum.Y : bounds.Maximum.Y,
                (corner & 4) == 0 ? bounds.Minimum.Z : bounds.Maximum.Z);
            Vector3 world = Vector3.Transform(local, transform);
            minimum = Vector3.Min(minimum, world);
            maximum = Vector3.Max(maximum, world);
        }
        return new VfxBounds(minimum, maximum);
    }
}

public enum VfxPropertyCoverageStatus
{
    Applied,
    Metadata,
    Unsupported
}

public sealed record VfxPropertyCoverage(
    string OwnerPath,
    string OwnerClass,
    string PropertyName,
    VfxPropertyCoverageStatus Status);

public sealed class VfxEmitterDefinition
{
    public string Name { get; init; }
    /// <summary>
    /// Optional actor reference-skeleton bone used by receiver-effect wrappers. Particle positions remain in the
    /// emitter's authored local space and are transformed through this bone when the preview is rendered.
    /// </summary>
    public string AttachmentBone { get; set; }
    public Matrix4x4 AttachmentTransform { get; set; } = Matrix4x4.Identity;
    public float Delay { get; init; }
    public float DelayLow { get; init; }
    public bool UseDelayRange { get; init; }
    public bool DelayFirstLoopOnly { get; init; }
    public float Duration { get; init; }
    public float DurationLow { get; init; }
    public bool UseDurationRange { get; init; }
    public bool RecalculateDurationEachLoop { get; init; }
    public int Loops { get; init; }
    public bool KillOnDeactivate { get; init; }
    public bool KillOnCompleted { get; init; }
    public VfxSortMode SortMode { get; init; }
    public VfxBurstMethod BurstMethod { get; init; }
    public int MaxDrawCount { get; init; }
    public bool UseMaxDrawCount { get; init; }
    public int MaxParticles { get; init; } = 4096;
    public VfxScreenAlignment ScreenAlignment { get; init; } = VfxScreenAlignment.Square;
    public VfxAxisLock AxisLock { get; init; }
    public Vector2 PivotOffset { get; init; }
    public Vector2 SourceAspect { get; init; } = Vector2.One;
    public int SubImagesHorizontal { get; init; } = 1;
    public int SubImagesVertical { get; init; } = 1;
    public VfxSubUVInterpolation SubUVInterpolation { get; init; }
    public IVfxDistribution<float> SubImageIndex { get; init; } = new VfxConstantDistribution<float>(0);
    public IVfxDistribution<float> SubUVFrameRate { get; init; } = new VfxConstantDistribution<float>(0);
    public int SubUVStartingFrame { get; init; }
    public bool SubUVUseEmitterTime { get; init; }
    public float RandomImageTime { get; init; }
    public int RandomImageChanges { get; init; }
    public IVfxDistribution<float> SubImageSelect { get; init; }
    public IEntry Material { get; init; }
    public VfxParticleMaterialDefinition ParticleMaterial { get; set; } = new();
    public IVfxDistribution<float> SpawnRate { get; init; } = new VfxConstantDistribution<float>(0);
    public IReadOnlyList<VfxBurst> Bursts { get; init; } = [];
    public IVfxDistribution<float> Lifetime { get; init; } = new VfxConstantDistribution<float>(1);
    public IVfxDistribution<Vector3> InitialLocation { get; init; } = new VfxConstantDistribution<Vector3>(Vector3.Zero);
    public IVfxDistribution<Vector3> InitialVelocity { get; init; } = new VfxConstantDistribution<Vector3>(Vector3.Zero);
    public IVfxDistribution<Vector3> InitialSize { get; init; } = new VfxConstantDistribution<Vector3>(Vector3.One);
    public IVfxDistribution<float> InitialRotation { get; init; } = new VfxConstantDistribution<float>(0);
    public IVfxDistribution<float> RotationRate { get; init; } = new VfxConstantDistribution<float>(0);
    public IVfxDistribution<Vector4> InitialColor { get; init; } = new VfxConstantDistribution<Vector4>(Vector4.One);
    public IVfxDistribution<Vector3> SizeOverLife { get; init; } = new VfxConstantDistribution<Vector3>(Vector3.One);
    public IVfxDistribution<Vector3> SizeScale { get; init; } = new VfxConstantDistribution<Vector3>(Vector3.One);
    public IVfxDistribution<Vector3> SizeScaleByTime { get; init; } = new VfxConstantDistribution<Vector3>(Vector3.One);
    public IVfxDistribution<Vector3> SizeMultiplyVelocity { get; init; } = new VfxConstantDistribution<Vector3>(Vector3.One);
    public IVfxDistribution<float> RotationOverLife { get; init; } = new VfxConstantDistribution<float>(0);
    public bool RotationOverLifeScales { get; init; }
    public IVfxDistribution<float> RotationRateMultiplierOverLife { get; init; } = new VfxConstantDistribution<float>(1);
    public IVfxDistribution<Vector4> ColorOverLife { get; init; } = new VfxConstantDistribution<Vector4>(Vector4.One);
    public IVfxDistribution<Vector4> ColorScaleOverLife { get; init; } = new VfxConstantDistribution<Vector4>(Vector4.One);
    public bool ColorScaleUsesEmitterTime { get; init; }
    public IReadOnlyList<VfxDynamicParameterDefinition> DynamicParameters { get; init; } = [];
    public IVfxDistribution<Vector3> VelocityOverLife { get; init; } = new VfxConstantDistribution<Vector3>(Vector3.One);
    public bool VelocityOverLifeIsAbsolute { get; init; }
    public IVfxDistribution<Vector3> AccelerationOverLife { get; init; } = new VfxConstantDistribution<Vector3>(Vector3.Zero);
    public bool UseLocalSpace { get; init; }
    public IReadOnlyList<VfxKillVolume> KillVolumes { get; init; } = [];
    public VfxEmitterRenderMode RenderMode { get; init; } = VfxEmitterRenderMode.Sprite;
    public VfxMeshEmitterDefinition MeshEmitter { get; init; }
    public bool IsSpriteEmitter => RenderMode == VfxEmitterRenderMode.Sprite;
    public List<VfxSpawnInitializer> SpawnInitializers { get; } = [];
}

public readonly record struct VfxBurst(float Time, int Count, int CountLow = -1);

/// <summary>
/// Describes a ParticleModuleKillBox or ParticleModuleKillHeight volume.
/// </summary>
public abstract record VfxKillVolume(bool IsAbsolute);

public sealed record VfxKillBox(
    IVfxDistribution<Vector3> LowerLeftCorner,
    IVfxDistribution<Vector3> UpperRightCorner,
    bool KillInside,
    bool Absolute) : VfxKillVolume(Absolute);

public sealed record VfxKillHeight(
    IVfxDistribution<float> Height,
    bool IsFloor,
    bool Absolute) : VfxKillVolume(Absolute);

public struct VfxParticle
{
    public Vector3 Position;
    public Vector3 BaseVelocity;
    public Vector3 Velocity;
    public Vector3 Acceleration;
    public Vector3 OrbitBaseOffset;
    public Vector3 OrbitRotation;
    public Vector3 OrbitRotationRate;
    public Vector3 OrbitOffset;
    public Vector3 BaseSize;
    public Vector3 Size;
    public Vector4 Color;
    public Vector4 DynamicParameter;
    public float Rotation;
    public float RotationRate;
    public Vector3 MeshRotation;
    public Vector3 MeshRotationRate;
    public float Age;
    public float Lifetime;
    public float Random;
    public float SubImageIndex;
    public float BaseRotation;
    public float RandomImageTimer;
    public int RandomImageChangesRemaining;

    public readonly float RelativeTime => Lifetime <= 0 ? 1 : Math.Clamp(Age / Lifetime, 0, 1);
    public readonly bool IsAlive => Age < Lifetime;
}

public interface IVfxSourceAdapter
{
    bool CanAdapt(ExportEntry export);
    VfxPreviewDefinition CreateDefinition(ExportEntry export);
}
