using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Flattens package-owned mesh buffers once per unique LOD, then delegates the complete scalable
/// topology, instance, bounds/area, UV-validation, and light-relevance scan to LightmassNative.
/// </summary>
internal static unsafe partial class NativeStaticLightingSceneScanner
{
    private const string LibraryName = "LightmassNative.dll";
    private const uint AbiVersion = 5;

    public static NativeStaticLightingSceneScan Scan(
        IReadOnlyList<NativeStaticLightingMeshInstance> instances,
        IReadOnlyList<StaticLightingLight> lights, int workerCount,
        IProgress<StaticLightingBuildProgress> progress = null, string scanMode = "Native C++")
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(lights);
        if (NativeMethods.GetAbiVersion() != AbiVersion)
            throw new InvalidOperationException("The native Lightmass DLL uses an incompatible ABI version.");

        var rawVertices = new List<NativeRawMeshVertex>();
        var indices = new List<ushort>();
        var sections = new List<NativeMeshSection>();
        var meshes = new List<NativeRawMeshDesc>();
        var meshKeys = new List<(ExportEntry Mesh, int CoordinateIndex)>();
        var meshFirstInstanceIndices = new List<int>();
        var meshIndices = new Dictionary<(ExportEntry Mesh, int CoordinateIndex), int>();
        var nativeInstances = new NativeMeshInstanceDesc[instances.Count];

        for (int instanceIndex = 0; instanceIndex < instances.Count; instanceIndex++)
        {
            NativeStaticLightingMeshInstance instance = instances[instanceIndex];
            ExportEntry componentExport = instance.Component?.Export;
            progress?.Report(new StaticLightingBuildProgress(scanMode, "Preparing native mesh descriptors",
                instanceIndex + 1, instances.Count, componentExport?.UIndex ?? 0,
                componentExport is null ? "" : Path.GetFileName(componentExport.FileRef.FilePath),
                componentExport?.ObjectName.Instanced ?? ""));
            var key = (instance.MeshExport, instance.CoordinateIndex);
            if (!meshIndices.TryGetValue(key, out int meshIndex))
            {
                meshIndex = meshes.Count;
                meshIndices.Add(key, meshIndex);
                meshKeys.Add(key);
                meshFirstInstanceIndices.Add(instanceIndex);
                AddRawMesh(instance.Lod, instance.CoordinateIndex, rawVertices, indices, sections, meshes);
            }
            Matrix4x4 normalToWorld = Matrix4x4.Invert(instance.LocalToWorld, out Matrix4x4 inverse)
                ? Matrix4x4.Transpose(inverse)
                : instance.LocalToWorld;
            nativeInstances[instanceIndex] = new NativeMeshInstanceDesc
            {
                MeshIndex = checked((uint)meshIndex),
                LocalToWorld = instance.LocalToWorld,
                NormalToWorld = normalToWorld,
                LightingChannels = instance.LightingChannelMask
            };
        }

        var nativeLights = new NativeScanLight[lights.Count];
        for (int index = 0; index < lights.Count; index++)
        {
            StaticLightingLight light = lights[index];
            nativeLights[index] = new NativeScanLight
            {
                Type = (uint)light.Type,
                Position = light.Position,
                Direction = light.Direction,
                Radius = light.Radius,
                OuterConeAngleDegrees = light.OuterConeAngleDegrees,
                LightingChannels = light.LightingChannelMask
            };
        }

        NativeRawMeshVertex[] vertexArray = rawVertices.ToArray();
        ushort[] indexArray = indices.ToArray();
        NativeMeshSection[] sectionArray = sections.ToArray();
        NativeRawMeshDesc[] meshArray = meshes.ToArray();
        nint scanHandle = nint.Zero;
        GCHandle progressHandle = default;
        try
        {
            if (progress is not null)
                progressHandle = GCHandle.Alloc(new NativeProgressState(progress, scanMode, instances,
                    meshFirstInstanceIndices));
            fixed (NativeRawMeshVertex* vertexPointer = vertexArray)
            fixed (ushort* indexPointer = indexArray)
            fixed (NativeMeshSection* sectionPointer = sectionArray)
            fixed (NativeRawMeshDesc* meshPointer = meshArray)
            fixed (NativeMeshInstanceDesc* instancePointer = nativeInstances)
            fixed (NativeScanLight* lightPointer = nativeLights)
            {
                var desc = new NativeSceneScanDesc
                {
                    StructSize = (uint)sizeof(NativeSceneScanDesc),
                    AbiVersion = AbiVersion,
                    Vertices = vertexPointer,
                    VertexCount = checked((uint)vertexArray.Length),
                    Indices = indexPointer,
                    IndexCount = checked((uint)indexArray.Length),
                    Sections = sectionPointer,
                    SectionCount = checked((uint)sectionArray.Length),
                    Meshes = meshPointer,
                    MeshCount = checked((uint)meshArray.Length),
                    Instances = instancePointer,
                    InstanceCount = checked((uint)nativeInstances.Length),
                    Lights = lightPointer,
                    LightCount = checked((uint)nativeLights.Length),
                    WorkerCount = checked((uint)Math.Max(1, workerCount)),
                    ProgressCallback = progress is null ? nint.Zero :
                        (nint)(delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, void>)
                        &ReportNativeScanProgress,
                    ProgressState = progressHandle.IsAllocated ? GCHandle.ToIntPtr(progressHandle) : nint.Zero
                };
                ThrowOnError(NativeMethods.ScanScene(&desc, &scanHandle), "scan native meshes and lights");
            }
        }
        finally
        {
            if (progressHandle.IsAllocated)
                progressHandle.Free();
        }

        try
        {
            NativeSceneScanView view = new() { StructSize = (uint)sizeof(NativeSceneScanView) };
            ThrowOnError(NativeMethods.GetSceneScanView(scanHandle, &view), "read native scene scan");
            if (view.AbiVersion != AbiVersion || view.MeshCount != meshArray.Length ||
                view.InstanceCount != nativeInstances.Length)
                throw new InvalidOperationException("The native Lightmass scene scan returned inconsistent output.");

            var mappingDiagnostics = new StaticLightingMappingDiagnostics[meshArray.Length];
            for (int index = 0; index < mappingDiagnostics.Length; index++)
            {
                NativeRawMeshDesc raw = meshArray[index];
                NativeMeshScanResult result = view.Meshes[index];
                mappingDiagnostics[index] = new StaticLightingMappingDiagnostics
                {
                    MeshPath = meshKeys[index].Mesh.InstancedFullPath,
                    DeclaredVertexCount = checked((int)raw.DeclaredVertexCount),
                    PositionVertexCount = checked((int)raw.PositionVertexCount),
                    AttributeVertexCount = checked((int)raw.AttributeVertexCount),
                    TextureCoordinateCount = checked((int)raw.TextureCoordinateCount),
                    SelectedCoordinateIndex = raw.SelectedCoordinateIndex,
                    SectionCount = checked((int)raw.SectionCount),
                    SourceIndexCount = checked((int)raw.IndexCount),
                    TriangleCount = checked((int)result.TriangleCount),
                    InvalidSectionRangeCount = checked((int)result.InvalidSectionRangeCount),
                    InvalidIndexCount = checked((int)result.InvalidIndexCount),
                    InvalidUvVertexCount = checked((int)result.InvalidUvVertexCount),
                    DegenerateUvTriangleCount = checked((int)result.DegenerateUvTriangleCount),
                    OverlappingUvTrianglePairCount = checked((int)result.OverlappingUvTrianglePairCount)
                };
            }

            var scannedInstances = new NativeStaticLightingScannedInstance[instances.Count];
            for (int instanceIndex = 0; instanceIndex < scannedInstances.Length; instanceIndex++)
            {
                NativeInstanceScanResult result = view.Instances[instanceIndex];
                var vertices = new StaticLightingVertex[checked((int)result.VertexCount)];
                for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
                {
                    NativeScannedVertex vertex = view.Vertices[result.FirstVertex + vertexIndex];
                    vertices[vertexIndex] = new StaticLightingVertex(vertex.Position, vertex.Normal,
                        vertex.Tangent, vertex.Bitangent, vertex.LightMapUv);
                }
                var triangles = new StaticLightingTriangle[checked((int)result.TriangleCount)];
                for (int triangleIndex = 0; triangleIndex < triangles.Length; triangleIndex++)
                {
                    NativeScannedTriangle triangle = view.Triangles[result.FirstTriangle + triangleIndex];
                    triangles[triangleIndex] = new StaticLightingTriangle(vertices[triangle.First],
                        vertices[triangle.Second], vertices[triangle.Third])
                    {
                        SectionIndex = triangle.SectionIndex,
                        SourceTriangleIndex = triangle.SourceTriangleIndex
                    };
                }
                var relevantLightIndices = new int[checked((int)result.RelevantLightCount)];
                for (int lightIndex = 0; lightIndex < relevantLightIndices.Length; lightIndex++)
                    relevantLightIndices[lightIndex] = checked((int)view.RelevantLightIndices[
                        result.FirstRelevantLight + lightIndex]);
                int meshIndex = checked((int)nativeInstances[instanceIndex].MeshIndex);
                StaticLightingMappingDiagnostics diagnostics = mappingDiagnostics[meshIndex];
                scannedInstances[instanceIndex] = new NativeStaticLightingScannedInstance(vertices, triangles,
                    diagnostics, meshArray[meshIndex].CoordinateChannelAvailable != 0 &&
                    !diagnostics.HasTextureMappingErrors, result.BoundsMinimum, result.BoundsMaximum,
                    result.MaximumWorldDimension, result.SurfaceArea, relevantLightIndices);
            }
            return new NativeStaticLightingSceneScan(scannedInstances, meshArray.Length,
                new NativeStaticLightingSceneScanDiagnostics(view.TopologyScanMilliseconds,
                    view.InstanceScanMilliseconds, view.LightScanMilliseconds,
                    view.TotalScanMilliseconds));
        }
        finally
        {
            if (scanHandle != nint.Zero)
                NativeMethods.DestroySceneScan(scanHandle);
        }
    }

    private static void AddRawMesh(StaticMeshRenderData lod, int coordinateIndex,
        List<NativeRawMeshVertex> vertices, List<ushort> indices, List<NativeMeshSection> sections,
        List<NativeRawMeshDesc> meshes)
    {
        Vector3[] positions = lod.PositionVertexBuffer?.VertexData ?? [];
        StaticMeshVertexBuffer.StaticMeshFullVertex[] attributes = lod.VertexBuffer?.VertexData ?? [];
        int vertexCount = Math.Min(positions.Length, attributes.Length);
        bool coordinateAvailable = vertexCount > 0 && coordinateIndex >= 0 && lod.VertexBuffer is not null &&
                                   lod.VertexBuffer.NumTexCoords > coordinateIndex;
        int firstVertex = vertices.Count;
        for (int index = 0; index < vertexCount; index++)
        {
            StaticMeshVertexBuffer.StaticMeshFullVertex source = attributes[index];
            Vector2 uv = default;
            if (coordinateAvailable)
                uv = lod.VertexBuffer.bUseFullPrecisionUVs
                    ? source.FullPrecisionUVs[coordinateIndex]
                    : new Vector2(source.HalfPrecisionUVs[coordinateIndex].X,
                        source.HalfPrecisionUVs[coordinateIndex].Y);
            vertices.Add(new NativeRawMeshVertex
            {
                Position = positions[index],
                TangentX = (Vector3)source.TangentX,
                TangentZ = (Vector3)source.TangentZ,
                LightMapUv = uv,
                Handedness = ((Vector4)source.TangentZ).W < 0f ? -1f : 1f
            });
        }
        ushort[] sourceIndices = lod.IndexBuffer ?? [];
        int firstIndex = indices.Count;
        indices.AddRange(sourceIndices);
        StaticMeshElement[] sourceSections = lod.Elements ?? [];
        int firstSection = sections.Count;
        foreach (StaticMeshElement section in sourceSections)
            sections.Add(new NativeMeshSection { FirstIndex = section.FirstIndex, TriangleCount = section.NumTriangles });
        meshes.Add(new NativeRawMeshDesc
        {
            FirstVertex = checked((uint)firstVertex),
            VertexCount = checked((uint)vertexCount),
            FirstIndex = checked((uint)firstIndex),
            IndexCount = checked((uint)sourceIndices.Length),
            FirstSection = checked((uint)firstSection),
            SectionCount = checked((uint)sourceSections.Length),
            DeclaredVertexCount = lod.NumVertices,
            PositionVertexCount = checked((uint)positions.Length),
            AttributeVertexCount = checked((uint)attributes.Length),
            TextureCoordinateCount = lod.VertexBuffer?.NumTexCoords ?? 0,
            SelectedCoordinateIndex = coordinateIndex,
            CoordinateChannelAvailable = coordinateAvailable ? 1u : 0u
        });
    }

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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReportNativeScanProgress(nint statePointer, uint phase, uint currentIndex,
        uint completed, uint total)
    {
        try
        {
            if (statePointer == nint.Zero ||
                GCHandle.FromIntPtr(statePointer).Target is not NativeProgressState state)
                return;
            int instanceIndex = phase == 1
                ? currentIndex < (uint)state.MeshFirstInstanceIndices.Count
                    ? state.MeshFirstInstanceIndices[checked((int)currentIndex)]
                    : -1
                : currentIndex < (uint)state.Instances.Count ? checked((int)currentIndex) : -1;
            NativeStaticLightingMeshInstance instance = instanceIndex >= 0
                ? state.Instances[instanceIndex]
                : null;
            ExportEntry export = instance?.Component?.Export ?? instance?.MeshExport;
            string phaseName = phase switch
            {
                1 => "Scanning native mesh topology and UVs",
                2 => "Transforming native mesh instances",
                3 => "Scanning native light relevance",
                _ => "Scanning native scene"
            };
            int totalCount = checked((int)total);
            int currentCount = totalCount == 0 ? 0 : completed == 0
                ? 1
                : Math.Min(checked((int)completed), totalCount);
            state.Progress.Report(new StaticLightingBuildProgress(state.ScanMode, phaseName,
                currentCount, totalCount, export?.UIndex ?? 0,
                export is null ? "" : Path.GetFileName(export.FileRef.FilePath),
                export?.ObjectName.Instanced ?? ""));
        }
        catch
        {
            // No exception may cross an unmanaged progress callback boundary.
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRawMeshVertex
    { public Vector3 Position; public Vector3 TangentX; public Vector3 TangentZ; public Vector2 LightMapUv; public float Handedness; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMeshSection
    { public uint FirstIndex; public uint TriangleCount; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRawMeshDesc
    {
        public uint FirstVertex, VertexCount, FirstIndex, IndexCount, FirstSection, SectionCount;
        public uint DeclaredVertexCount, PositionVertexCount, AttributeVertexCount, TextureCoordinateCount;
        public int SelectedCoordinateIndex; public uint CoordinateChannelAvailable;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMeshInstanceDesc
    { public uint MeshIndex; public Matrix4x4 LocalToWorld; public Matrix4x4 NormalToWorld; public uint LightingChannels; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeScanLight
    { public uint Type; public Vector3 Position; public Vector3 Direction; public float Radius; public float OuterConeAngleDegrees; public uint LightingChannels; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSceneScanDesc
    {
        public uint StructSize, AbiVersion; public NativeRawMeshVertex* Vertices; public uint VertexCount;
        public ushort* Indices; public uint IndexCount; public NativeMeshSection* Sections; public uint SectionCount;
        public NativeRawMeshDesc* Meshes; public uint MeshCount; public NativeMeshInstanceDesc* Instances;
        public uint InstanceCount; public NativeScanLight* Lights; public uint LightCount, WorkerCount;
        public nint ProgressCallback, ProgressState;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeScannedVertex
    { public Vector3 Position, Normal, Tangent, Bitangent; public Vector2 LightMapUv; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeScannedTriangle
    { public uint First, Second, Third; public int SectionIndex, SourceTriangleIndex; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMeshScanResult
    {
        public uint TriangleCount, InvalidSectionRangeCount, InvalidIndexCount, InvalidUvVertexCount;
        public uint DegenerateUvTriangleCount, OverlappingUvTrianglePairCount;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInstanceScanResult
    {
        public uint FirstVertex, VertexCount, FirstTriangle, TriangleCount, FirstRelevantLight, RelevantLightCount;
        public Vector3 BoundsMinimum, BoundsMaximum; public float MaximumWorldDimension, SurfaceArea;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSceneScanView
    {
        public uint StructSize, AbiVersion; public NativeScannedVertex* Vertices; public uint VertexCount;
        public NativeScannedTriangle* Triangles; public uint TriangleCount; public NativeMeshScanResult* Meshes;
        public uint MeshCount; public NativeInstanceScanResult* Instances; public uint InstanceCount;
        public uint* RelevantLightIndices; public uint RelevantLightIndexCount;
        public double TopologyScanMilliseconds, InstanceScanMilliseconds, LightScanMilliseconds, TotalScanMilliseconds;
    }

    private static partial class NativeMethods
    {
        [LibraryImport(LibraryName, EntryPoint = "LmnGetAbiVersion")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();
        [LibraryImport(LibraryName, EntryPoint = "LmnScanScene")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int ScanScene(NativeSceneScanDesc* scene, nint* scan);
        [LibraryImport(LibraryName, EntryPoint = "LmnGetSceneScanView")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int GetSceneScanView(nint scan, NativeSceneScanView* view);
        [LibraryImport(LibraryName, EntryPoint = "LmnDestroySceneScan")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void DestroySceneScan(nint scan);
        [LibraryImport(LibraryName, EntryPoint = "LmnGetLastError")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial nuint GetLastError(byte* destination, nuint destinationSize);
    }
}

internal sealed record NativeProgressState(IProgress<StaticLightingBuildProgress> Progress, string ScanMode,
    IReadOnlyList<NativeStaticLightingMeshInstance> Instances, IReadOnlyList<int> MeshFirstInstanceIndices);

internal sealed record NativeStaticLightingMeshInstance(StaticMeshComponentProxy Component,
    ExportEntry MeshExport, StaticMeshRenderData Lod, int CoordinateIndex, Matrix4x4 LocalToWorld,
    uint LightingChannelMask);

internal sealed record NativeStaticLightingScannedInstance(StaticLightingVertex[] Vertices,
    StaticLightingTriangle[] Triangles, StaticLightingMappingDiagnostics MappingDiagnostics,
    bool HasTextureCoordinates, Vector3 BoundsMinimum, Vector3 BoundsMaximum,
    float MaximumWorldDimension, float SurfaceArea, int[] RelevantLightIndices);

internal sealed record NativeStaticLightingSceneScan(NativeStaticLightingScannedInstance[] Instances,
    int UniqueMeshCount, NativeStaticLightingSceneScanDiagnostics Diagnostics);

internal sealed record NativeStaticLightingSceneScanDiagnostics(double TopologyScanMilliseconds,
    double InstanceScanMilliseconds, double LightScanMilliseconds, double TotalScanMilliseconds);
