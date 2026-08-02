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

public sealed class VfxParticleMaterialDefinition
{
    public VfxBlendMode BlendMode { get; set; } = VfxBlendMode.Translucent;
    public bool IsUnlit { get; set; }
    public bool TwoSided { get; set; }
    public bool DisableDepthTest { get; set; }
    public float OpacityMaskClipValue { get; set; } = 0.333f;
    public VfxOpacitySource OpacitySource { get; set; } = VfxOpacitySource.TextureAlpha;
    public Vector4 EmissiveTint { get; set; } = Vector4.One;
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
    public string Name { get; init; }
    public Matrix4x4 SystemTransform { get; init; } = Matrix4x4.Identity;
    public VfxBounds? FixedLocalBounds { get; init; }
    public IReadOnlyList<float> LodDistances { get; init; } = [];
    public IReadOnlyList<VfxLodSetting> LodSettings { get; init; } = [];
    public int SelectedLodIndex { get; init; }
    public List<VfxEmitterDefinition> Emitters { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<VfxPropertyCoverage> PropertyCoverage { get; } = [];

    public static Matrix4x4 CreateUnitScaleCenteringTransform(VfxBounds? localBounds)
    {
        if (localBounds is not { IsValid: true } bounds)
        {
            return Matrix4x4.Identity;
        }
        Vector3 center = (bounds.Minimum + bounds.Maximum) * 0.5f;
        return Matrix4x4.CreateTranslation(-center);
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
    public float Delay { get; init; }
    public float Duration { get; init; }
    public int Loops { get; init; }
    public int MaxParticles { get; init; } = 4096;
    public VfxScreenAlignment ScreenAlignment { get; init; } = VfxScreenAlignment.CameraFacing;
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
    public IVfxDistribution<Vector4> ColorOverLife { get; init; } = new VfxConstantDistribution<Vector4>(Vector4.One);
    public IVfxDistribution<Vector4> ColorScaleOverLife { get; init; } = new VfxConstantDistribution<Vector4>(Vector4.One);
    public bool ColorScaleUsesEmitterTime { get; init; }
    public IVfxDistribution<Vector3> VelocityOverLife { get; init; } = new VfxConstantDistribution<Vector3>(Vector3.One);
    public bool VelocityOverLifeIsAbsolute { get; init; }
    public IVfxDistribution<Vector3> AccelerationOverLife { get; init; } = new VfxConstantDistribution<Vector3>(Vector3.Zero);
    public bool UseLocalSpace { get; init; }
    public bool IsSpriteEmitter { get; init; } = true;
    public List<VfxSpawnInitializer> SpawnInitializers { get; } = [];
}

public readonly record struct VfxBurst(float Time, int Count, int CountLow = -1);

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
    public float Rotation;
    public float RotationRate;
    public float Age;
    public float Lifetime;
    public float Random;
    public float SubImageIndex;

    public readonly float RelativeTime => Lifetime <= 0 ? 1 : Math.Clamp(Age / Lifetime, 0, 1);
    public readonly bool IsAlive => Age < Lifetime;
}

public interface IVfxSourceAdapter
{
    bool CanAdapt(ExportEntry export);
    VfxPreviewDefinition CreateDefinition(ExportEntry export);
}
