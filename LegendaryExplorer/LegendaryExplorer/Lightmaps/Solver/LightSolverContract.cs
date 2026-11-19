using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace LegendaryExplorer.Lightmaps.Solver;

// LEX's own static lighting solver, which replaces UDK's Swarm + UnrealLightmass.exe for the UDK Lightmass Baker and gives Scottina's Lightmass Baker
// bounced light and sky occlusion. One set of HLSL compute kernels runs on the GPU (a hardware Direct3D 11 device) or on the CPU (Direct3D 11's WARP
// software device), so both devices give the same result.
//
// The solver knows nothing of packages: a baker flattens its scene into a SolverScene, describes the points it wants lit as SolverSamples, and turns the
// SolverOutput back into its own lightmaps.

/// <summary>Where the solver's kernels run</summary>
public enum SolverDevice
{
    /// <summary>A hardware Direct3D 11 device (the GPU)</summary>
    Gpu,
    /// <summary>Direct3D 11's WARP device: the same kernels on every CPU core</summary>
    Cpu,
}

/// <summary>How faithfully the solver follows UE3 Lightmass</summary>
public enum SolverProfile
{
    /// <summary>
    /// The UDK Lightmass Baker's: as close to UE3 Lightmass as can be done (area shadows with penumbra refinement, shadow maps, multiple bounces with
    /// material albedo and DiffuseBoost, IndirectLightingScale/Saturation, emissive, sky light and environment colour with occlusion, masked and translucent
    /// shadows, Lightmass's directional coefficients). Quality settings come from Lightmass's ini.
    /// </summary>
    Lightmass,
    /// <summary>Scottina's Lightmass Baker's: its own direct lighting model, plus bounced light and sky occlusion at a lower cost</summary>
    Scottina,
}

public enum SolverLightType : uint
{
    Point = 0,
    Spot = 1,
    Directional = 2,
    Sky = 3,
}

/// <summary>A light as the solver gets it. Colours are linear and already multiplied by brightness.</summary>
public sealed class SolverLight
{
    public SolverLightType Type;
    /// <summary>World position (point, spot)</summary>
    public Vector3 Position;
    /// <summary>The direction the light travels (spot, directional), unit length</summary>
    public Vector3 Direction;
    /// <summary>Linear colour × brightness. For a sky light, the upper hemisphere's</summary>
    public Vector3 Color;
    /// <summary>Sky light: the lower hemisphere's linear colour × LowerBrightness</summary>
    public Vector3 LowerColor;
    /// <summary>Point and spot: the attenuation radius and UE3's radial falloff exponent (pow(max(0, 1 - (d/R)²), FalloffExponent))</summary>
    public float Radius, FalloffExponent = 2;
    /// <summary>Spot: cosines of the inner and outer cone half-angles</summary>
    public float CosInnerCone = 1, CosOuterCone;
    /// <summary>Point and spot: the radius of the emitting sphere (LightSourceRadius). Directional: the half-angle in radians (LightSourceAngle)</summary>
    public float SourceSize;
    /// <summary>Whether it casts static shadows</summary>
    public bool CastShadows = true;
    /// <summary>
    /// Lightmass's ShadowExponent: a texel's shadow factor (the fraction of the light it sees) is raised to this power, which narrows and darkens
    /// penumbras. 1: the plain fraction
    /// </summary>
    public float ShadowExponent = 1;
    /// <summary>Scale and saturation of the light it bounces (IndirectLightingScale, IndirectLightingSaturation)</summary>
    public float IndirectLightingScale = 1, IndirectLightingSaturation = 1;
    /// <summary>Whether its direct lighting reaches only the receivers that list it (<see cref="SolverReceiver"/>); its bounce lighting reaches everything</summary>
    public bool Bounces = true;
}

/// <summary>A material's samples over its UV space (row-major Size × Size; linear RGB). Null means none (black, or for transmission: opaque)</summary>
public sealed class SolverTexture
{
    public int Size;
    public Vector4[] Texels;

    public static SolverTexture Constant(Vector4 value) => new() { Size = 1, Texels = [value] };
}

public sealed class SolverMaterial
{
    /// <summary>Linear albedo, already boosted (DiffuseBoost) and clamped to 1</summary>
    public SolverTexture Diffuse;
    /// <summary>Linear emitted radiance, already boosted (EmissiveBoost)</summary>
    public SolverTexture Emissive;
    /// <summary>How much light passes through (RGB), for translucent shadows; null for opaque</summary>
    public SolverTexture Transmission;
    /// <summary>Masked: a shadow ray passes where Transmission's red is below <see cref="OpacityMaskClipValue"/> instead of being tinted by it</summary>
    public bool Masked;
    public float OpacityMaskClipValue = 0.3333f;
    /// <summary>Lit and shadowing from both sides</summary>
    public bool TwoSided;
}

public struct SolverVertex
{
    /// <summary>World space</summary>
    public Vector3 Position;
    /// <summary>World space, unit length</summary>
    public Vector3 Normal;
    /// <summary>The material's texture coordinate (UV 0), for looking up <see cref="SolverMaterial"/> samples</summary>
    public Vector2 MaterialUV;
}

[Flags]
public enum SolverTriangleFlags : uint
{
    None = 0,
    /// <summary>Blocks shadow and bounce rays (CastShadow). Without it, the triangle is only a receiver</summary>
    CastShadow = 1,
    /// <summary>Shadows from both sides (bCastShadowAsTwoSided or a two-sided material)</summary>
    TwoSidedShadow = 2,
    /// <summary>Only shadows its own instance (bSelfShadowOnly)</summary>
    SelfShadowOnly = 4,
    /// <summary>Only blocks indirect light (bShadowIndirectOnly)</summary>
    ShadowIndirectOnly = 8,
}

public struct SolverTriangle
{
    public uint V0, V1, V2;
    /// <summary>Index into <see cref="SolverScene.Materials"/></summary>
    public uint Material;
    /// <summary>Which primitive it belongs to: rays never hit the triangles of the instance that's flagged SelfShadowOnly unless they come from it</summary>
    public uint Instance;
    public SolverTriangleFlags Flags;
}

public sealed class SolverScene
{
    public List<SolverVertex> Vertices = [];
    public List<SolverTriangle> Triangles = [];
    public List<SolverMaterial> Materials = [];
    public List<SolverLight> Lights = [];
    /// <summary>Linear radiance of rays that leave the scene, in every direction (Lightmass's EnvironmentColor × EnvironmentIntensity)</summary>
    public Vector3 Environment;
    /// <summary>Lightmass importance volumes; empty: the whole scene matters</summary>
    public List<(Vector3 Min, Vector3 Max)> ImportanceVolumes = [];
}

/// <summary>A point to light: a lightmap texel's centre or a vertex. Everything is in world space</summary>
public struct SolverSample
{
    public Vector3 Position;
    /// <summary>The shading normal (unit)</summary>
    public Vector3 Normal;
    /// <summary>The triangle's normal (unit), for offsetting rays off the surface</summary>
    public Vector3 GeometricNormal;
    /// <summary>The tangent frame the directional coefficients are relative to (unit; tangent-space Z is <see cref="Normal"/>)</summary>
    public Vector3 Tangent, Bitangent;
    /// <summary>World-space radius of the texel (half its diagonal), for ray offsets and supersampling the texel's area</summary>
    public float TexelRadius;
    /// <summary>The instance it's on (<see cref="SolverTriangle.Instance"/>), for self-shadowing and to not hit itself</summary>
    public uint Instance;
    /// <summary>Index into <see cref="SolverJob.Receivers"/></summary>
    public uint Receiver;
}

/// <summary>What a set of samples (one mapping, say) wants from each light</summary>
public sealed class SolverReceiver
{
    /// <summary>The lights (indices into <see cref="SolverScene.Lights"/>) whose direct light goes into these samples' lighting</summary>
    public int[] LightmapLights = [];
    /// <summary>The lights whose visibility is output instead (shadow maps), one value per sample per light in this order</summary>
    public int[] ShadowMapLights = [];
    /// <summary>Lit from both sides (bUseTwoSidedLighting)</summary>
    public bool TwoSidedLighting;
    /// <summary>Whether bounced light and sky light are gathered for these samples</summary>
    public bool GatherIndirect = true;
}

public sealed class SolverSettings
{
    public SolverDevice Device = SolverDevice.Gpu;
    public SolverProfile Profile = SolverProfile.Lightmass;

    /// <summary>Shadow rays per light per sample, and the extra rays a partially shadowed sample gets (NumShadowRays, NumPenumbraShadowRays)</summary>
    public int ShadowRays = 8, PenumbraShadowRays = 32;
    /// <summary>Final gather rays per sample (NumHemisphereSamples), and per bounce after the first (NumBounceHemisphereSamples)</summary>
    public int GatherRays = 64, BounceGatherRays = 16;
    /// <summary>NumIndirectLightingBounces</summary>
    public int Bounces = 3;
    /// <summary>
    /// How incident light of tangent-space direction d adds into the directional coefficients: E_i += L · DirectionalCoefficientScale ·
    /// max(0, d·B_i)^DirectionalCoefficientFalloffPower, B_i being Codec.Directional.Basis; the simple coefficient adds L · max(0, d.z)
    /// </summary>
    public float DirectionalCoefficientScale = 1, DirectionalCoefficientFalloffPower = 1;
    /// <summary>World-space ray offsets off surfaces (VisibilityRayOffsetDistance, VisibilityNormalOffsetDistance)</summary>
    public float RayOffset = 0.1f, NormalOffset = 0.1f;
    /// <summary>
    /// When above 0, a sample's rays leave from this fraction of its texel radius off the surface (VisibilityNormalOffsetSampleRadiusScale), between
    /// <see cref="RayOffset"/> and <see cref="NormalOffset"/>; 0: always <see cref="NormalOffset"/>
    /// </summary>
    public float NormalOffsetTexelScale;
    /// <summary>
    /// UE3's static shadows: a one-sided triangle shadows direct light only when its front faces the light, and bounced light is stopped by both sides.
    /// Off: a one-sided triangle shadows when its front faces the receiver
    /// </summary>
    public bool LightFacingShadows;
    /// <summary>Lightmass's IndirectNormalInfluenceBoost: how much the shading normal, over the geometric one, orients the gather hemisphere</summary>
    public float IndirectNormalInfluenceBoost = 0.3f;
    /// <summary>Ambient occlusion (bUseAmbientOcclusion and its settings); off by default as in Lightmass</summary>
    public bool AmbientOcclusion;
    public float DirectOcclusionFraction = 0.5f, IndirectOcclusionFraction = 1, OcclusionExponent = 1, FullyOccludedSamplesFraction = 1, MaxOcclusionDistance = 200;
    /// <summary>Smooths the gathered indirect light within each receiver, as Lightmass's irradiance cache interpolation does</summary>
    public bool FilterIndirect = true;
    /// <summary>
    /// With <see cref="FilterIndirect"/>: Lightmass's irradiance cache record radius, which keeps smoothing off contact shadows. A sample's bounced
    /// light is smoothed within DistanceSmoothFactor × clamp(RecordRadiusScale × the nearest surface its gather rays hit, MinRecordRadius,
    /// MaxRecordRadius) world units. RecordRadiusScale 0: by texel size alone
    /// </summary>
    public float RecordRadiusScale, MinRecordRadius = 16, MaxRecordRadius = 1024, DistanceSmoothFactor = 2;
    /// <summary>Seed of the random sequences, so a bake is repeatable</summary>
    public uint Seed = 0x4C455831;
    /// <summary>Bounced light the way Lightmass computes it (photon maps, irradiance photons, final gather); null: path traced bounces</summary>
    public SolverPhotonSettings Photons;
}

/// <summary>
/// Lightmass's photon mapping ([DevOptions.PhotonMapping]): the lights emit photons into the importance volume; direct photons are stored where they
/// first land, indirect photons where they land after bouncing. Irradiance is estimated at a subset of them (irradiance photons) from their nearest
/// photons, and each final gather ray takes the irradiance of the irradiance photon nearest to where it lands.
/// </summary>
public sealed class SolverPhotonSettings
{
    /// <summary>Photons per million square units of the importance volume's extent (DirectPhotonDensity, IndirectPhotonDensity, × the quality's scales)</summary>
    public float DirectPhotonDensity = 350, IndirectPhotonDensity = 600;
    /// <summary>Irradiance photons per million square units (DirectIrradiancePhotonDensity, IndirectIrradiancePhotonDensity)</summary>
    public float DirectIrradiancePhotonDensity = 350, IndirectIrradiancePhotonDensity = 300;
    /// <summary>Photons an irradiance estimate uses (NumIrradianceCalculationPhotons)</summary>
    public int IrradiancePhotons = 400;
    /// <summary>The farthest a photon is from the point it's estimated for (DirectPhotonSearchDistance, IndirectPhotonSearchDistance)</summary>
    public float DirectSearchDistance = 200, IndirectSearchDistance = 200;
    /// <summary>Least cosine between a photon's normal and the surface's for it to count (PhotonSearchAngleThreshold)</summary>
    public float NormalThreshold = 0.5f;
    /// <summary>Degrees off the surface's plane an irradiance photon may lie and still be used there (IrradiancePhotonSearchConeAngle)</summary>
    public float SearchConeAngle = 10;
    /// <summary>Jensen's cone filter constant (ConeFilterConstant)</summary>
    public float ConeFilterConstant = 1;
    /// <summary>Scales the photon counts (tuning)</summary>
    public float CountScale = 1;
    /// <summary>Direct light at irradiance photons by shadow rays to the lights instead of from the direct photons (bUsePhotonsForDirectLighting off)</summary>
    public bool DirectByShadowRays;
    /// <summary>Spot lights emit photons in every direction, without their cone (tuning)</summary>
    public bool SpotEmitsSphere;
    /// <summary>Photons ignore the lights' radial falloff inside their radius (tuning)</summary>
    public bool IgnoreFalloff;
    /// <summary>Tuning: gather rays only use irradiance photons on the mesh they hit / within the fine grid's cell</summary>
    public bool LookupSameInstance, LookupFineOnly = true;
    /// <summary>Photons pass the back of one-sided surfaces, as UE3's shadows do (tuning)</summary>
    public bool PassBackFaces;
}

public sealed class SolverJob
{
    public SolverScene Scene;
    public SolverSettings Settings = new();
    public List<SolverSample> Samples = [];
    public List<SolverReceiver> Receivers = [];
    /// <summary>Optional: for each sample, its neighbours in its receiver's lightmap (texel x, y and receiver-local grid width) for filtering; null: no filter</summary>
    public SolverSampleGrid[] Grids;
}

/// <summary>A receiver's samples laid out as a lightmap (for <see cref="SolverSettings.FilterIndirect"/>): Sample[y · Width + x] is a sample index or -1</summary>
public sealed class SolverSampleGrid
{
    public int Width, Height;
    public int[] Samples;
}

public sealed class SolverOutput
{
    /// <summary>Per sample: incident light along the three basis directions (tangent space), direct (lightmap lights) plus indirect</summary>
    public Vector3[] Directional0, Directional1, Directional2;
    /// <summary>Per sample: incident light along the normal (the simple coefficient), direct plus indirect</summary>
    public Vector3[] Simple;
    /// <summary>Per sample: the indirect part of <see cref="Simple"/> alone (diagnostics, and for bakers that keep direct and indirect apart)</summary>
    public Vector3[] IndirectSimple;
    /// <summary>Per sample, for each of its receiver's <see cref="SolverReceiver.ShadowMapLights"/>: visibility 0..1. Offsets into <see cref="Visibility"/> per sample</summary>
    public float[] Visibility;
    public int[] VisibilityOffsets;
    public SolverStatistics Statistics = new();
}

public sealed class SolverStatistics
{
    public string DeviceName;
    /// <summary>Why the GPU doesn't trace with its ray tracing hardware, when it doesn't</summary>
    public string DeviceNote;
    public long RaysCast;
    public TimeSpan BvhTime, DirectTime, IndirectTime, TotalTime;
    /// <summary>What the photon pass made (null: no photon pass, or its map was reused)</summary>
    public string PhotonSummary;
}

/// <summary>Progress of a solve: what it's doing and how far through it is (0..1)</summary>
public readonly record struct SolverProgress(string Stage, double Fraction);

public static partial class LightSolver
{
    /// <summary>Lights the job's samples. Throws <see cref="SolverUnavailableException"/> when the device can't be created</summary>
    public static SolverOutput Solve(SolverJob job, IProgress<SolverProgress> progress = null, CancellationToken cancel = default) => SolveCore(job, progress, cancel);

    /// <summary>Whether a hardware Direct3D 11 device with compute shaders exists (for greying out the GPU option)</summary>
    public static bool IsGpuAvailable() => IsGpuAvailableCore();

    static partial void SolveImpl(SolverJob job, IProgress<SolverProgress> progress, CancellationToken cancel, ref SolverOutput output);
    static partial void IsGpuAvailableImpl(ref bool available);

    static SolverOutput SolveCore(SolverJob job, IProgress<SolverProgress> progress, CancellationToken cancel)
    {
        SolverOutput output = null;
        SolveImpl(job, progress, cancel, ref output);
        return output ?? throw new SolverUnavailableException("The light solver isn't built yet.");
    }

    static bool IsGpuAvailableCore()
    {
        bool available = false;
        IsGpuAvailableImpl(ref available);
        return available;
    }
}

public sealed class SolverUnavailableException(string message, Exception inner = null) : Exception(message, inner);
