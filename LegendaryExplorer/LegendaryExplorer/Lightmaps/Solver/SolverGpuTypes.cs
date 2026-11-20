using System.Numerics;
using System.Runtime.InteropServices;

namespace LegendaryExplorer.Lightmaps.Solver;

// CPU mirrors of the HLSL structs in SolverShaders. Keep the layouts in step.

[StructLayout(LayoutKind.Sequential)]
internal struct GpuTri
{
    public Vector3 V0;
    public uint Flags;
    public Vector3 E1;
    public uint Instance;
    public Vector3 E2;
    public uint Orig;

    public const uint MaterialMask = 0x00FFFFFF;
    public const uint CastShadow = 1u << 24;
    public const uint TwoSidedShadow = 1u << 25;
    public const uint SelfShadowOnly = 1u << 26;
    public const uint ShadowIndirectOnly = 1u << 27;
    public const uint Transmissive = 1u << 28;
    public const uint MaterialTwoSided = 1u << 29;
    public const uint Flipped = 1u << 30;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GpuVert
{
    public Vector3 Normal;
    public Vector2 UV;
    public float Pad;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GpuMaterial
{
    public uint DiffuseOffset, DiffuseSize, EmissiveOffset, EmissiveSize, TransmissionOffset, TransmissionSize, Flags;
    public float Clip;

    public const uint TwoSided = 1, Masked = 2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GpuLight
{
    public Vector3 Position;
    public uint Type;
    public Vector3 Direction;
    public float Radius;
    public Vector3 Color;
    public float FalloffExponent;
    public Vector3 IndirectColor;
    public float CosInner;
    public float CosOuter;
    public float SourceSize;
    public uint CastShadows;
    public float ShadowExponent;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GpuSample
{
    public Vector3 Position;
    public float TexelRadius;
    public Vector3 Normal;
    public uint Instance;
    public Vector3 GeometricNormal;
    public uint Flags;
    public Vector3 Tangent;
    public uint Pad0;
    public Vector3 Bitangent;
    public uint Pad1;

    public const uint TwoSidedLighting = 1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct UInt4
{
    public uint X, Y, Z, W;

    public UInt4(uint x, uint y, uint z, uint w)
    {
        X = x; Y = y; Z = z; W = w;
    }

    public readonly Vector3 Xyz => new(BitConverterF(X), BitConverterF(Y), BitConverterF(Z));
    public readonly float WFloat => BitConverterF(W);

    static float BitConverterF(uint v) => System.BitConverter.UInt32BitsToSingle(v);
}

[StructLayout(LayoutKind.Sequential)]
internal struct GpuParams
{
    public uint ItemCount, GatherRays, Bounces, ShadowRays;
    public uint PenumbraRays, Seed, NumBounceLights, NumTriangles;
    public float RayOffset, NormalOffset, NormalInfluence, MaxOcclusionDistance;
    public float DirScale, DirPower, SceneExtent;
    public uint SceneFlags;
    public Vector3 SkyUpper;
    public uint Pad0;
    public Vector3 SkyLower;
    public uint Pad1;
    public Vector4 Basis0, Basis1, Basis2;

    // photon mapping
    public uint PhotonMode, EmitPass, EmitBase, EmitCapacity;
    public Vector3 ImpMin;
    public uint NumEmitLights;
    public Vector3 ImpMax;
    public float ImpRadius;
    public uint TableAOff, TableASize;
    public float CellA, RadiusA;
    public uint TableBOff, TableBSize;
    public float CellB, RadiusB;
    public uint KNearest;
    public float ConeK, NormalCos, PlaneSin;
    public uint PhotonVariant;
    public float NormalTexelScale;
    public uint PhotonPad1, PhotonPad2;

    public const uint SceneHasTranslucency = 1, SceneLightFacingShadows = 2, SceneSkyBounces = 4;
    public const uint VariantSpotSphere = 1, VariantNoFalloff = 2, VariantDirectExplicit = 4, VariantLookupSameInstance = 8, VariantLookupFineOnly = 16, VariantPassBack = 32;
}

/// <summary>A photon (or irradiance photon: Power is then its irradiance). Normal is octahedral, 16 bits a component</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuPhoton
{
    public Vector3 Position;
    public uint Normal;
    public Vector3 Power;
    public uint Tri;
}

/// <summary>A spatial hash table entry: a grid cell and its run of photons. Count 0 marks an empty entry</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuHashCell
{
    public int X, Y, Z;
    public uint Start, Count, Pad0, Pad1, Pad2;
}
