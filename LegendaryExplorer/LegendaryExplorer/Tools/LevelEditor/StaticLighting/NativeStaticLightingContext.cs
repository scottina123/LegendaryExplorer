using LegendaryExplorerCore.Packages;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Owns one native immutable occluder scene. Receiver data crosses the ABI in complete bulk arrays;
/// there are no managed callbacks and no per-ray, per-light, or per-texel transitions.
/// </summary>
internal sealed unsafe partial class NativeStaticLightingContext : IDisposable
{
    private const string LibraryName = "LightmassNative.dll";
    private const uint AbiVersion = 5;
    private nint handle;
    private readonly Dictionary<ExportEntry, int> sourceIds = new(ReferenceEqualityComparer.Instance);
    private readonly float shadowBias;
    private readonly IProgress<StaticLightingBuildProgress> detailedProgress;
    private readonly string progressMode;

    public double BvhBuildMilliseconds { get; }
    public int BvhNodeCount { get; }

    public static bool IsAvailable
    {
        get
        {
            try
            {
                if (!NativeLibrary.TryLoad(LibraryName, Assembly.GetExecutingAssembly(), null, out nint library))
                    return false;
                NativeLibrary.Free(library);
                return NativeMethods.GetAbiVersion() == AbiVersion;
            }
            catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or
                                               EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    public NativeStaticLightingContext(LevelCollisionScene collision, float shadowBias,
        IProgress<StaticLightingBuildProgress> progress = null, string progressMode = "Native C++")
    {
        ArgumentNullException.ThrowIfNull(collision);
        this.shadowBias = shadowBias;
        detailedProgress = progress;
        this.progressMode = string.IsNullOrWhiteSpace(progressMode) ? "Native C++" : progressMode;
        if (NativeMethods.GetAbiVersion() != AbiVersion)
            throw new InvalidOperationException("The native Lightmass DLL uses an incompatible ABI version.");

        IReadOnlyList<LevelCollisionTriangle> collisionTriangles = collision.StaticLightingTriangles;
        var triangles = new NativeTriangle[collisionTriangles.Count];
        var triangleSources = new ExportEntry[collisionTriangles.Count];
        for (int index = 0; index < collisionTriangles.Count; index++)
        {
            LevelCollisionTriangle triangle = collisionTriangles[index];
            triangleSources[index] = triangle.Source;
            triangles[index] = new NativeTriangle
            {
                A = triangle.A,
                B = triangle.A + triangle.Edge1,
                C = triangle.A + triangle.Edge2,
                SourceId = GetOrAddSourceId(triangle.Source),
                SourceTriangleIndex = triangle.SourceTriangleIndex
            };
        }

        GCHandle progressHandle = default;
        try
        {
            if (progress is not null)
                progressHandle = GCHandle.Alloc(new NativeBvhProgressState(progress, progressMode,
                    triangleSources));
            fixed (NativeTriangle* trianglePointer = triangles)
            {
                var scene = new NativeSceneDesc
                {
                    StructSize = (uint)sizeof(NativeSceneDesc),
                    AbiVersion = AbiVersion,
                    Triangles = trianglePointer,
                    TriangleCount = (uint)triangles.Length,
                    LeafTriangleCount = 8,
                    ProgressCallback = progress is null ? nint.Zero :
                        (nint)(delegate* unmanaged[Cdecl]<nint, uint, uint, uint, void>)
                        &ReportNativeBvhProgress,
                    ProgressState = progressHandle.IsAllocated
                        ? GCHandle.ToIntPtr(progressHandle)
                        : nint.Zero
                };
                var diagnostics = new NativeSceneDiagnostics
                {
                    StructSize = (uint)sizeof(NativeSceneDiagnostics)
                };
                nint createdHandle = nint.Zero;
                ThrowOnError(NativeMethods.CreateBakeContext(&scene, &createdHandle, &diagnostics),
                    "create native bake context");
                handle = createdHandle;
                BvhBuildMilliseconds = diagnostics.BvhBuildMilliseconds;
                BvhNodeCount = checked((int)diagnostics.BvhNodeCount);
            }
        }
        finally
        {
            if (progressHandle.IsAllocated)
                progressHandle.Free();
        }
    }

    public NativeStaticLightingBake BakeSamples(StaticLightingBaker.StaticLightingSurfaceSample[] samples,
        StaticLightingBaker.PreparedLighting lighting, int coefficientCount, bool compressedDirectional,
        int workerCount, bool textureMapping, ExportEntry receiver, int receiverNumber, int receiverTotal)
    {
        ObjectDisposedException.ThrowIf(handle == nint.Zero, this);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(lighting);
        if (coefficientCount is < 3 or > 4)
            throw new ArgumentOutOfRangeException(nameof(coefficientCount));

        var nativeSamples = new NativeSurfaceSample[samples.Length];
        for (int index = 0; index < samples.Length; index++)
        {
            StaticLightingBaker.StaticLightingSurfaceSample sample = samples[index];
            nativeSamples[index] = new NativeSurfaceSample
            {
                Position = sample.Position,
                Normal = sample.Normal,
                Tangent = sample.Tangent,
                Bitangent = sample.Bitangent,
                GeometricNormal = sample.GeometricNormal,
                SourceId = GetSourceId(sample.Source),
                SourceTriangleIndex = sample.SourceTriangleIndex,
                WorldUnitsPerTexel = sample.WorldUnitsPerTexel
            };
        }

        var nativeLights = new NativePreparedLight[lighting.DirectLights.Length];
        var nativeLightSamples = new List<Vector3>();
        for (int lightIndex = 0; lightIndex < lighting.DirectLights.Length; lightIndex++)
        {
            StaticLightingBaker.PreparedLight light = lighting.DirectLights[lightIndex];
            int firstSample = nativeLightSamples.Count;
            if (light.Type == StaticLightingLightType.Directional)
            {
                foreach (Vector3 direction in light.DirectionalSurfaceToLight)
                    nativeLightSamples.Add(direction);
            }
            else if (light.DiskSamples is { } diskSamples)
            {
                foreach (Vector2 disk in diskSamples)
                    nativeLightSamples.Add(new Vector3(disk, 0f));
            }
            else
            {
                nativeLightSamples.Add(Vector3.Zero);
            }
            nativeLights[lightIndex] = new NativePreparedLight
            {
                Type = (uint)light.Type,
                CastsShadow = light.CastsShadow ? 1u : 0u,
                Position = light.Position,
                Direction = light.Direction,
                Radiance = light.Radiance,
                RadiusSquared = light.RadiusSquared,
                InverseRadius = light.InverseRadius,
                OuterConeCos = light.OuterConeCos,
                InverseConeRange = light.InverseConeRange,
                SourceRadius = light.SourceRadius,
                FirstSample = checked((uint)firstSample),
                SampleCount = checked((uint)light.SampleCount)
            };
        }

        var nativeEmitters = new NativeAreaEmitter[lighting.AreaEmitters.Length];
        for (int index = 0; index < lighting.AreaEmitters.Length; index++)
        {
            StaticLightingAreaEmitter emitter = lighting.AreaEmitters[index];
            nativeEmitters[index] = new NativeAreaEmitter
            {
                Position = emitter.Position,
                Normal = emitter.Normal,
                Radiance = emitter.Radiance,
                Area = emitter.Area,
                InfluenceRadius = emitter.InfluenceRadius,
                FalloffExponent = emitter.FalloffExponent,
                TwoSided = emitter.TwoSided ? 1u : 0u
            };
        }

        Vector3[] lightSampleArray = nativeLightSamples.ToArray();
        var nativeCoefficients = new Vector3[checked(samples.Length * coefficientCount)];
        NativeBakeDiagnostics diagnostics = new() { StructSize = (uint)sizeof(NativeBakeDiagnostics) };
        GCHandle progressHandle = default;
        try
        {
            if (detailedProgress is not null)
                progressHandle = GCHandle.Alloc(new NativeBakeProgressState(detailedProgress,
                    progressMode, receiver, receiverNumber, receiverTotal, textureMapping));
            fixed (NativeSurfaceSample* samplePointer = nativeSamples)
            fixed (NativePreparedLight* lightPointer = nativeLights)
            fixed (Vector3* lightSamplePointer = lightSampleArray)
            fixed (NativeAreaEmitter* emitterPointer = nativeEmitters)
            fixed (Vector3* coefficientPointer = nativeCoefficients)
            {
                var bake = new NativeBakeDesc
                {
                    StructSize = (uint)sizeof(NativeBakeDesc),
                    AbiVersion = AbiVersion,
                    Samples = samplePointer,
                    SampleCount = checked((uint)nativeSamples.Length),
                    Lights = lightPointer,
                    LightCount = checked((uint)nativeLights.Length),
                    LightSamples = lightSamplePointer,
                    LightSampleCount = checked((uint)lightSampleArray.Length),
                    Emitters = emitterPointer,
                    EmitterCount = checked((uint)nativeEmitters.Length),
                    Environment = lighting.Environment,
                    ShadowBias = shadowBias,
                    MinimumEmissiveContribution = StaticLightingEmissive.MinimumContribution,
                    CoefficientCount = checked((uint)coefficientCount),
                    CompressedDirectional = compressedDirectional ? 1u : 0u,
                    WorkerCount = checked((uint)Math.Max(1, workerCount)),
                    MappingType = textureMapping ? 2u : 1u,
                    ProgressCallback = detailedProgress is null ? nint.Zero :
                        (nint)(delegate* unmanaged[Cdecl]<nint, uint, uint, void>)
                        &ReportNativeBakeProgress,
                    ProgressState = progressHandle.IsAllocated
                        ? GCHandle.ToIntPtr(progressHandle)
                        : nint.Zero
                };
                ThrowOnError(NativeMethods.BakeSamples(handle, &bake, coefficientPointer,
                    checked((nuint)nativeCoefficients.Length), &diagnostics), "bake native receiver");
            }
        }
        finally
        {
            if (progressHandle.IsAllocated)
                progressHandle.Free();
        }

        var coefficients = new Vector3[coefficientCount][];
        for (int coefficientIndex = 0; coefficientIndex < coefficientCount; coefficientIndex++)
        {
            Vector3[] coefficient = coefficients[coefficientIndex] = new Vector3[samples.Length];
            for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
                coefficient[sampleIndex] = nativeCoefficients[sampleIndex * coefficientCount + coefficientIndex];
        }
        return new NativeStaticLightingBake(coefficients, ToManagedDiagnostics(diagnostics), diagnostics);
    }

    public void Dispose()
    {
        nint current = handle;
        handle = nint.Zero;
        if (current != nint.Zero)
            NativeMethods.DestroyBakeContext(current);
    }

    private int GetOrAddSourceId(ExportEntry source)
    {
        if (source is null)
            return -1;
        if (sourceIds.TryGetValue(source, out int sourceId))
            return sourceId;
        sourceId = sourceIds.Count;
        sourceIds.Add(source, sourceId);
        return sourceId;
    }

    private int GetSourceId(ExportEntry source) => source is not null && sourceIds.TryGetValue(source,
        out int sourceId) ? sourceId : -1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReportNativeBvhProgress(nint statePointer, uint currentIndex,
        uint completed, uint total)
    {
        try
        {
            if (statePointer == nint.Zero ||
                GCHandle.FromIntPtr(statePointer).Target is not NativeBvhProgressState state)
                return;
            ExportEntry export = currentIndex < (uint)state.TriangleSources.Count
                ? state.TriangleSources[checked((int)currentIndex)]
                : null;
            int totalCount = checked((int)total);
            int currentCount = Math.Min(checked((int)completed), totalCount);
            state.Progress.Report(new StaticLightingBuildProgress(state.ProgressMode,
                "Building native occluder BVH", currentCount, totalCount,
                export?.UIndex ?? 0,
                export is null ? "" : Path.GetFileName(export.FileRef.FilePath),
                export?.ObjectName.Instanced ?? ""));
        }
        catch
        {
            // No exception may cross an unmanaged progress callback boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReportNativeBakeProgress(nint statePointer, uint completed, uint total)
    {
        try
        {
            if (statePointer == nint.Zero ||
                GCHandle.FromIntPtr(statePointer).Target is not NativeBakeProgressState state)
                return;
            ExportEntry export = state.Receiver;
            string mapping = state.TextureMapping ? "texture" : "vertex";
            state.Progress.Report(new StaticLightingBuildProgress(state.ProgressMode,
                $"Baking native {mapping} samples - receiver {state.ReceiverNumber:N0}/{state.ReceiverTotal:N0}",
                Math.Min(checked((int)completed), checked((int)total)), checked((int)total),
                export?.UIndex ?? 0,
                export is null ? "" : Path.GetFileName(export.FileRef.FilePath),
                export?.ObjectName.Instanced ?? ""));
        }
        catch
        {
            // No exception may cross an unmanaged progress callback boundary.
        }
    }

    private static StaticLightingNativeDiagnostics ToManagedDiagnostics(NativeBakeDiagnostics diagnostics) => new()
    {
        SamplesProcessed = checked((long)diagnostics.SamplesProcessed),
        OccupiedTexels = checked((long)diagnostics.OccupiedTexels),
        RelevantLights = checked((long)diagnostics.RelevantLights),
        RayTriangleTests = checked((long)diagnostics.RayTriangleTests),
        BvhNodesVisited = checked((long)diagnostics.BvhNodesVisited),
        AnyHitEarlyOuts = checked((long)diagnostics.AnyHitEarlyOuts),
        ShadowTraversalMilliseconds = diagnostics.ShadowTraversalMilliseconds,
        Bake1DMilliseconds = diagnostics.Bake1DMilliseconds,
        Bake2DMilliseconds = diagnostics.Bake2DMilliseconds,
        TotalComputeMilliseconds = diagnostics.TotalComputeMilliseconds,
        SamplesPerSecond = diagnostics.SamplesPerSecond,
        RaysPerSecond = diagnostics.RaysPerSecond
    };

    private static void ThrowOnError(int status, string operation)
    {
        if (status == 0)
            return;
        Span<byte> error = stackalloc byte[1024];
        string detail;
        fixed (byte* errorPointer = error)
        {
            NativeMethods.GetLastError(errorPointer, (nuint)error.Length);
            int terminator = error.IndexOf((byte)0);
            detail = System.Text.Encoding.UTF8.GetString(error[..(terminator < 0 ? error.Length : terminator)]);
        }
        throw new InvalidOperationException($"Could not {operation} (status {status}): {detail}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTriangle
    {
        public Vector3 A;
        public Vector3 B;
        public Vector3 C;
        public int SourceId;
        public int SourceTriangleIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSceneDesc
    {
        public uint StructSize;
        public uint AbiVersion;
        public NativeTriangle* Triangles;
        public uint TriangleCount;
        public uint LeafTriangleCount;
        public nint ProgressCallback;
        public nint ProgressState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSceneDiagnostics
    {
        public uint StructSize;
        public uint AbiVersion;
        public uint TriangleCount;
        public uint BvhNodeCount;
        public double BvhBuildMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSurfaceSample
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector3 Tangent;
        public Vector3 Bitangent;
        public Vector3 GeometricNormal;
        public int SourceId;
        public int SourceTriangleIndex;
        public float WorldUnitsPerTexel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePreparedLight
    {
        public uint Type;
        public uint CastsShadow;
        public Vector3 Position;
        public Vector3 Direction;
        public Vector3 Radiance;
        public float RadiusSquared;
        public float InverseRadius;
        public float OuterConeCos;
        public float InverseConeRange;
        public float SourceRadius;
        public uint FirstSample;
        public uint SampleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeAreaEmitter
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector3 Radiance;
        public float Area;
        public float InfluenceRadius;
        public float FalloffExponent;
        public uint TwoSided;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBakeDesc
    {
        public uint StructSize;
        public uint AbiVersion;
        public NativeSurfaceSample* Samples;
        public uint SampleCount;
        public NativePreparedLight* Lights;
        public uint LightCount;
        public Vector3* LightSamples;
        public uint LightSampleCount;
        public NativeAreaEmitter* Emitters;
        public uint EmitterCount;
        public Vector3 Environment;
        public float ShadowBias;
        public float MinimumEmissiveContribution;
        public uint CoefficientCount;
        public uint CompressedDirectional;
        public uint WorkerCount;
        public uint MappingType;
        public nint ProgressCallback;
        public nint ProgressState;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeBakeDiagnostics
    {
        public uint StructSize;
        public uint AbiVersion;
        public ulong SamplesProcessed;
        public ulong OccupiedTexels;
        public ulong RelevantLights;
        public ulong RaysCast;
        public ulong OccludedSamples;
        public ulong RejectedSelfIntersections;
        public ulong VisibilitySampleCount;
        public ulong VisibilityMicroSum;
        public ulong DirectContributionMicroSum;
        public ulong EnvironmentContributionMicroSum;
        public ulong EmissiveSamplesEvaluated;
        public ulong EmissiveRaysCast;
        public ulong RayTriangleTests;
        public ulong BvhNodesVisited;
        public ulong AnyHitEarlyOuts;
        public double ShadowTraversalMilliseconds;
        public double Bake1DMilliseconds;
        public double Bake2DMilliseconds;
        public double TotalComputeMilliseconds;
        public double SamplesPerSecond;
        public double RaysPerSecond;
    }

    private static partial class NativeMethods
    {
        [LibraryImport(LibraryName, EntryPoint = "LmnGetAbiVersion")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "LmnCreateBakeContext")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int CreateBakeContext(NativeSceneDesc* scene, nint* context,
            NativeSceneDiagnostics* diagnostics);

        [LibraryImport(LibraryName, EntryPoint = "LmnDestroyBakeContext")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void DestroyBakeContext(nint context);

        [LibraryImport(LibraryName, EntryPoint = "LmnBakeSamples")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int BakeSamples(nint context, NativeBakeDesc* bake, Vector3* coefficients,
            nuint coefficientCapacity, NativeBakeDiagnostics* diagnostics);

        [LibraryImport(LibraryName, EntryPoint = "LmnGetLastError")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial nuint GetLastError(byte* destination, nuint destinationSize);
    }
}

internal sealed record NativeBvhProgressState(IProgress<StaticLightingBuildProgress> Progress,
    string ProgressMode, IReadOnlyList<ExportEntry> TriangleSources);

internal sealed record NativeBakeProgressState(IProgress<StaticLightingBuildProgress> Progress,
    string ProgressMode, ExportEntry Receiver, int ReceiverNumber, int ReceiverTotal,
    bool TextureMapping);

internal sealed record NativeStaticLightingBake(Vector3[][] Coefficients,
    StaticLightingNativeDiagnostics Diagnostics, NativeStaticLightingContext.NativeBakeDiagnostics Counters);
