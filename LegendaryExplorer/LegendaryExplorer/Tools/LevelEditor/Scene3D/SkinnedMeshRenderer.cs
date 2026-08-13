using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.Animation;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3D11Buffer = SharpDX.Direct3D11.Buffer;
using DeviceContext = SharpDX.Direct3D11.DeviceContext;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

/// <summary>
/// Per-vertex bone skinning data. Stores bind-pose positions/normals and bone influence data.
/// Performs CPU skinning each frame and updates the vertex buffer.
/// </summary>
public struct SkinVertex
{
    public Vector3 BindPosition;  // Unreal space (Z-up)
    public Vector3 BindNormal;    // Unreal space
    public float BindNormalW;     // Tangent-basis handedness from TangentZ.W
    public Vector2 UV;
    public int Bone0, Bone1, Bone2, Bone3;       // skeleton-wide bone indices
    public int MorphBone0, MorphBone1, MorphBone2, MorphBone3; // source-mesh skeleton indices
    public float Weight0, Weight1, Weight2, Weight3; // normalized weights
}

public class SkinnedMeshRenderer : IDisposable
{
    private const int ParallelSkinningVertexThreshold = 4096;
    private const int GpuThreadGroupSize = 64;
    private const int MaxGpuBones = 256;
    private const int GpuConstantsSize = 32 + MaxGpuBones * 64;

    private const string GpuSkinningShaderSource = """
        struct SkinVertex
        {
            float4 BindPosition;
            float4 BindNormal;
            int4 AnimationBones;
            int4 SourceBones;
            float4 Weights;
        };

        StructuredBuffer<SkinVertex> SkinVertices : register(t0);
        RWByteAddressBuffer OutputVertices : register(u0);

        cbuffer SkinningConstants : register(b0)
        {
            uint VertexCount;
            uint BoneCount;
            uint UseSourceBoneIndices;
            float NormalScale;
            float NormalMaximum;
            float3 ConstantPadding;
            row_major float4x4 Bones[256];
        };

        [numthreads(64, 1, 1)]
        void Main(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            uint vertexIndex = dispatchThreadId.x;
            if (vertexIndex >= VertexCount)
            {
                return;
            }

            SkinVertex vertex = SkinVertices[vertexIndex];
            int4 indices = UseSourceBoneIndices != 0 ? vertex.SourceBones : vertex.AnimationBones;
            indices = clamp(indices, int4(0, 0, 0, 0), int4(BoneCount - 1, BoneCount - 1,
                BoneCount - 1, BoneCount - 1));
            row_major float4x4 blended = Bones[indices.x] * vertex.Weights.x
                                       + Bones[indices.y] * vertex.Weights.y
                                       + Bones[indices.z] * vertex.Weights.z
                                       + Bones[indices.w] * vertex.Weights.w;
            float3 skinnedPosition = mul(float4(vertex.BindPosition.xyz, 1.0), blended).xyz;
            float3 skinnedNormal = mul(float4(vertex.BindNormal.xyz, 0.0), blended).xyz;
            float4 encodedNormal = clamp((float4(skinnedNormal, vertex.BindNormal.w) + 1.0)
                                         * NormalScale, 0.0, NormalMaximum);

            uint outputOffset = vertexIndex * 116;
            OutputVertices.Store3(outputOffset, asuint(skinnedPosition));
            OutputVertices.Store4(outputOffset + 28, asuint(encodedNormal));
        }
        """;

    private static readonly Lazy<Task<byte[]>> GpuSkinningShaderBytecode = new(() => Task.Run(() =>
    {
        using CompilationResult compilation = ShaderBytecode.Compile(
            GpuSkinningShaderSource, "Main", "cs_5_0", ShaderFlags.OptimizationLevel3);
        return compilation.Bytecode.Data;
    }));

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct GpuSkinVertex
    {
        public Vector4 BindPosition;
        public Vector4 BindNormal;
        public int Bone0, Bone1, Bone2, Bone3;
        public int SourceBone0, SourceBone1, SourceBone2, SourceBone3;
        public Vector4 Weights;
    }

    public bool NeedsUpdate { get; set; }
    private MEGame _game;
    private SkinVertex[] _skinVertices;
    private Vector3[] _sourceBindPositions;
    private MeshBone[] _sourceSkeleton;
    private MeshBone[] _animationSkeleton;
    private MeshBone[] _skinningBindSkeleton;
    private int[] _animationBoneMap;
    private Matrix4x4[] _retargetBindLocalPose;
    private Matrix4x4[] _retargetBindComponentPose;
    private Matrix4x4[] _retargetInverseBindComponentPose;
    private Matrix4x4[] _retargetAnimatedComponentPose;
    private Matrix4x4[] _retargetedSkinningMatrices;
    private Matrix4x4[] _animationInverseComponentPose;
    private Matrix4x4[] _retargetRotationCorrection;
    private Vector3[] _retargetTranslationCorrection;
    private ComputeShader _gpuSkinningShader;
    private D3D11Buffer _gpuSkinVertices;
    private ShaderResourceView _gpuSkinVerticesView;
    private UnorderedAccessView _gpuOutputVerticesView;
    private D3D11Buffer _gpuConstants;
    private Mesh<LEVertex> _gpuMesh;
    private bool _gpuSkinningDisabled;

    /// <summary>
    /// Builds per-vertex skinning data from a SkeletalMesh LOD model.
    /// Resolves chunk-local bone indices to skeleton-wide indices via chunk.BoneMap.
    /// </summary>
    public void BuildFromSkeletalMesh(MEGame game, StaticLODModel lodModel)
        => BuildFromSkeletalMesh(game, lodModel, null, null);

    public void BuildFromSkeletalMesh(MEGame game, StaticLODModel lodModel, MeshBone[] sourceSkeleton,
        MeshBone[] animationSkeleton)
    {
        DisposeGpuResources();
        _gpuSkinningDisabled = false;
        // Compile once on a worker while model/cache setup continues. In the normal path the bytecode
        // is ready before the first animated frame and cannot introduce a playback hitch.
        _ = GpuSkinningShaderBytecode.Value;
        _game = game;
        _sourceSkeleton = sourceSkeleton;
        _animationSkeleton = animationSkeleton;
        _skinningBindSkeleton = sourceSkeleton;
        bool isME1 = game == MEGame.ME1;
        int vertexCount = isME1 ? lodModel.ME1VertexBufferGPUSkin.Length : (int)lodModel.NumVertices;
        _skinVertices = new SkinVertex[vertexCount];
        int[] boneMap = BuildAnimationBoneMap(sourceSkeleton, animationSkeleton);
        _animationBoneMap = boneMap;

        if (isME1)
        {
            // ME1 uses SoftSkinVertex in ME1VertexBufferGPUSkin
            for (int v = 0; v < vertexCount; v++)
            {
                var sv = lodModel.ME1VertexBufferGPUSkin[v];
                var chunk = FindChunkForVertex(lodModel, v);
                ref var skinVert = ref _skinVertices[v];
                skinVert.BindPosition = sv.Position;
                skinVert.BindNormal = (Vector3)sv.TangentZ;
                skinVert.BindNormalW = ((Vector4)sv.TangentZ).W;
                skinVert.UV = sv.UV;
                ResolveInfluences(ref skinVert, sv.InfluenceBones, sv.InfluenceWeights, chunk, boneMap);
            }
        }
        else
        {
            // ME2+ uses GPUSkinVertex in VertexBufferGPUSkin.VertexData
            for (int v = 0; v < vertexCount; v++)
            {
                var gv = lodModel.VertexBufferGPUSkin.VertexData[v];
                var chunk = FindChunkForVertex(lodModel, v);
                ref var skinVert = ref _skinVertices[v];
                skinVert.BindPosition = gv.Position;
                skinVert.BindNormal = (Vector3)gv.TangentZ;
                skinVert.BindNormalW = ((Vector4)gv.TangentZ).W;
                skinVert.UV = gv.UV;
                ResolveInfluences(ref skinVert, gv.InfluenceBones, gv.InfluenceWeights, chunk, boneMap);
            }
        }
        _sourceBindPositions = _skinVertices.Select(vertex => vertex.BindPosition).ToArray();
        PrepareRetargetingCache();
    }

    private static SkelMeshChunk FindChunkForVertex(StaticLODModel lodModel, int vertexIndex)
    {
        foreach (var chunk in lodModel.Chunks)
        {
            int chunkStart = (int)chunk.BaseVertexIndex;
            int chunkEnd = chunkStart + chunk.NumRigidVertices + chunk.NumSoftVertices;
            if (vertexIndex >= chunkStart && vertexIndex < chunkEnd)
                return chunk;
        }
        // Fallback to first chunk if not found
        return lodModel.Chunks[0];
    }

    private static void ResolveInfluences(ref SkinVertex skinVert, Influences bones, Influences weights,
        SkelMeshChunk chunk, int[] boneMap)
    {
        // BioMorphFace final-skeleton positions are indexed against the component's source mesh.
        // Runtime animation can instead target a parent/body skeleton, so retain both mappings.
        skinVert.MorphBone0 = ResolveSourceBoneIndex(bones[0], chunk);
        skinVert.MorphBone1 = ResolveSourceBoneIndex(bones[1], chunk);
        skinVert.MorphBone2 = ResolveSourceBoneIndex(bones[2], chunk);
        skinVert.MorphBone3 = ResolveSourceBoneIndex(bones[3], chunk);
        skinVert.Bone0 = ResolveAnimationBoneIndex(skinVert.MorphBone0, boneMap);
        skinVert.Bone1 = ResolveAnimationBoneIndex(skinVert.MorphBone1, boneMap);
        skinVert.Bone2 = ResolveAnimationBoneIndex(skinVert.MorphBone2, boneMap);
        skinVert.Bone3 = ResolveAnimationBoneIndex(skinVert.MorphBone3, boneMap);

        // Normalize weights (byte -> float)
        float w0 = weights[0] / 255f;
        float w1 = weights[1] / 255f;
        float w2 = weights[2] / 255f;
        float w3 = weights[3] / 255f;
        float total = w0 + w1 + w2 + w3;
        if (total > 0)
        {
            skinVert.Weight0 = w0 / total;
            skinVert.Weight1 = w1 / total;
            skinVert.Weight2 = w2 / total;
            skinVert.Weight3 = w3 / total;
        }
        else
        {
            skinVert.Weight0 = 1f;
            skinVert.Weight1 = 0f;
            skinVert.Weight2 = 0f;
            skinVert.Weight3 = 0f;
        }
    }

    private static int ResolveSourceBoneIndex(byte influenceBone, SkelMeshChunk chunk) =>
        influenceBone < chunk.BoneMap.Length ? chunk.BoneMap[influenceBone] : 0;

    private static int ResolveAnimationBoneIndex(int sourceIndex, int[] boneMap) =>
        boneMap is not null && sourceIndex < boneMap.Length ? boneMap[sourceIndex] : sourceIndex;

    private static int[] BuildAnimationBoneMap(MeshBone[] sourceSkeleton, MeshBone[] animationSkeleton)
    {
        if (sourceSkeleton is null || animationSkeleton is null || ReferenceEquals(sourceSkeleton, animationSkeleton))
        {
            return null;
        }

        Dictionary<string, int> animationBones = animationSkeleton
            .Select((bone, index) => (Name: bone.Name.Name, Index: index))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);
        int[] map = new int[sourceSkeleton.Length];
        for (int sourceIndex = 0; sourceIndex < sourceSkeleton.Length; sourceIndex++)
        {
            map[sourceIndex] = animationBones.GetValueOrDefault(sourceSkeleton[sourceIndex].Name.Name, -1);
        }
        return map;
    }

    /// <summary>
    /// Performs the CPU portion of skinning: blends matrices per vertex, transforms the bind-pose
    /// position/normal, and writes the results to the mesh vertex list. The caller can upload those
    /// prepared vertices later when it owns the D3D immediate context.
    /// </summary>
    public bool PrepareSkinning(Mesh<WorldVertex> mesh, AnimPlayer animPlayer)
    {
        NeedsUpdate = false;
        if (_skinVertices == null || mesh == null || animPlayer == null) return false;

        var skinningMatrices = GetSkinningMatrices(animPlayer, out bool useSourceBoneIndices);
        if (skinningMatrices == null) return false;

        mesh.EnsureUniqueVertices();
        int vertexCount = Math.Min(_skinVertices.Length, mesh.Vertices.Count);
        void SkinVertex(int i)
        {
            ref var sv = ref _skinVertices[i];

            // Blend skinning matrices by bone weights
            var blended = BlendMatrix(
                skinningMatrices, useSourceBoneIndices ? sv.MorphBone0 : sv.Bone0, sv.Weight0,
                useSourceBoneIndices ? sv.MorphBone1 : sv.Bone1, sv.Weight1,
                useSourceBoneIndices ? sv.MorphBone2 : sv.Bone2, sv.Weight2,
                useSourceBoneIndices ? sv.MorphBone3 : sv.Bone3, sv.Weight3);

            // Transform bind position and normal in Unreal space
            var skinnedPos = Vector3.Transform(sv.BindPosition, blended);
            var skinnedNormal = Vector3.TransformNormal(sv.BindNormal, blended);

            var rendererNormal = new Vector4(skinnedNormal.X, skinnedNormal.Z, skinnedNormal.Y, 1);

            mesh.Vertices[i] = new WorldVertex(skinnedPos, rendererNormal, sv.UV);
        }

        if (vertexCount >= ParallelSkinningVertexThreshold && Environment.ProcessorCount > 1)
        {
            Parallel.For(0, vertexCount, SkinVertex);
        }
        else
        {
            for (int i = 0; i < vertexCount; i++) SkinVertex(i);
        }

        return true;
    }

    public void UpdateSkinning(DeviceContext context, Mesh<WorldVertex> mesh, AnimPlayer animPlayer)
    {
        if (PrepareSkinning(mesh, animPlayer))
        {
            mesh.UpdateVertices(context);
        }
    }

    /// <summary>
    /// Updates the local-vertex-factory mesh used by the compiled game-shader preview while preserving
    /// its tangents, UV sets, and vertex color.
    /// </summary>
    public bool PrepareSkinning(Mesh<LEVertex> mesh, AnimPlayer animPlayer)
    {
        NeedsUpdate = false;
        if (_skinVertices == null || mesh == null || animPlayer == null) return false;

        var skinningMatrices = GetSkinningMatrices(animPlayer, out bool useSourceBoneIndices);
        if (skinningMatrices == null) return false;

        mesh.EnsureUniqueVertices();
        int vertexCount = Math.Min(_skinVertices.Length, mesh.Vertices.Count);
        void SkinVertex(int i)
        {
            ref var sv = ref _skinVertices[i];
            var blended = BlendMatrix(
                skinningMatrices, useSourceBoneIndices ? sv.MorphBone0 : sv.Bone0, sv.Weight0,
                useSourceBoneIndices ? sv.MorphBone1 : sv.Bone1, sv.Weight1,
                useSourceBoneIndices ? sv.MorphBone2 : sv.Bone2, sv.Weight2,
                useSourceBoneIndices ? sv.MorphBone3 : sv.Bone3, sv.Weight3);

            var skinnedPos = Vector3.Transform(sv.BindPosition, blended);
            var skinnedNormal = Vector3.TransformNormal(sv.BindNormal, blended);
            mesh.Vertices[i] = mesh.Vertices[i].WithPositionAndNormal(
                _game, skinnedPos, new Vector4(skinnedNormal, sv.BindNormalW));
        }

        if (vertexCount >= ParallelSkinningVertexThreshold && Environment.ProcessorCount > 1)
        {
            Parallel.For(0, vertexCount, SkinVertex);
        }
        else
        {
            for (int i = 0; i < vertexCount; i++) SkinVertex(i);
        }

        return true;
    }

    public void UpdateSkinning(DeviceContext context, Mesh<LEVertex> mesh, AnimPlayer animPlayer)
    {
        if (TryUpdateGpuSkinning(context, mesh, animPlayer))
        {
            return;
        }

        if (PrepareSkinning(mesh, animPlayer))
        {
            // A CPU fallback can replace the output buffer, so release any UAV that still references it.
            DisposeGpuResources();
            mesh.UpdateVertices(context);
        }
    }

    private bool TryUpdateGpuSkinning(DeviceContext context, Mesh<LEVertex> mesh, AnimPlayer animPlayer)
    {
        if (_gpuSkinningDisabled || context is null || mesh is null || animPlayer is null
            || _skinVertices is not { Length: > 0 } || mesh.Vertices.Count == 0)
        {
            return false;
        }

        Matrix4x4[] skinningMatrices = GetSkinningMatrices(animPlayer, out bool useSourceBoneIndices);
        if (skinningMatrices is not { Length: > 0 } || skinningMatrices.Length > MaxGpuBones)
        {
            DisposeGpuResources();
            return false;
        }

        try
        {
            if (!ReferenceEquals(_gpuMesh, mesh) || _gpuSkinningShader is null
                || _gpuSkinVerticesView is null || _gpuOutputVerticesView is null || _gpuConstants is null)
            {
                InitializeGpuResources(context, mesh);
            }

            int vertexCount = Math.Min(_skinVertices.Length, mesh.Vertices.Count);
            UploadGpuConstants(context, skinningMatrices, vertexCount, useSourceBoneIndices);

            // D3D11 does not allow the same resource to be simultaneously bound as vertex input and UAV.
            // Every mesh draw establishes its own input binding again, so clearing slot zero here is safe.
            context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(null, 0, 0));
            try
            {
                context.ComputeShader.Set(_gpuSkinningShader);
                context.ComputeShader.SetConstantBuffer(0, _gpuConstants);
                context.ComputeShader.SetShaderResource(0, _gpuSkinVerticesView);
                context.ComputeShader.SetUnorderedAccessView(0, _gpuOutputVerticesView);
                context.Dispatch((vertexCount + GpuThreadGroupSize - 1) / GpuThreadGroupSize, 1, 1);
            }
            finally
            {
                context.ComputeShader.SetUnorderedAccessView(0, null);
                context.ComputeShader.SetShaderResource(0, null);
                context.ComputeShader.SetConstantBuffer(0, null);
                context.ComputeShader.Set(null);
            }

            NeedsUpdate = false;
            return true;
        }
        catch (Exception exception)
        {
            // Keep previews functional on adapters/drivers that do not support this buffer combination.
            // The renderer permanently uses the established CPU path after the first GPU failure.
            Trace.WriteLine($"Dialogue preview GPU skinning unavailable; using CPU fallback: {exception}");
            DisposeGpuResources();
            _gpuSkinningDisabled = true;
            return false;
        }
    }

    private void InitializeGpuResources(DeviceContext context, Mesh<LEVertex> mesh)
    {
        DisposeGpuResources();
        if (!mesh.EnableGpuVertexWrites(context.Device))
        {
            throw new InvalidOperationException("The animated mesh could not create a GPU-writable vertex buffer.");
        }

        var gpuVertices = new GpuSkinVertex[_skinVertices.Length];
        for (int vertexIndex = 0; vertexIndex < _skinVertices.Length; vertexIndex++)
        {
            ref readonly SkinVertex source = ref _skinVertices[vertexIndex];
            gpuVertices[vertexIndex] = new GpuSkinVertex
            {
                BindPosition = new Vector4(source.BindPosition, 1),
                BindNormal = new Vector4(source.BindNormal, source.BindNormalW),
                Bone0 = source.Bone0,
                Bone1 = source.Bone1,
                Bone2 = source.Bone2,
                Bone3 = source.Bone3,
                SourceBone0 = source.MorphBone0,
                SourceBone1 = source.MorphBone1,
                SourceBone2 = source.MorphBone2,
                SourceBone3 = source.MorphBone3,
                Weights = new Vector4(source.Weight0, source.Weight1, source.Weight2, source.Weight3),
            };
        }

        int skinVertexStride = Marshal.SizeOf<GpuSkinVertex>();
        using var skinVertexStream = SharpDX.DataStream.Create(gpuVertices, true, true);
        _gpuSkinVertices = new D3D11Buffer(context.Device, skinVertexStream, new BufferDescription
        {
            SizeInBytes = skinVertexStride * gpuVertices.Length,
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = skinVertexStride,
        });
        _gpuSkinVerticesView = new ShaderResourceView(context.Device, _gpuSkinVertices,
            new ShaderResourceViewDescription
            {
                Format = Format.Unknown,
                Dimension = ShaderResourceViewDimension.Buffer,
                Buffer = new ShaderResourceViewDescription.BufferResource
                {
                    FirstElement = 0,
                    ElementCount = gpuVertices.Length,
                },
            });

        _gpuOutputVerticesView = new UnorderedAccessView(context.Device, mesh.VertexBuffer,
            new UnorderedAccessViewDescription
            {
                Format = Format.R32_Typeless,
                Dimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new UnorderedAccessViewDescription.BufferResource
                {
                    FirstElement = 0,
                    ElementCount = LEVertex.Stride * mesh.Vertices.Count / sizeof(uint),
                    Flags = UnorderedAccessViewBufferFlags.Raw,
                },
            });
        _gpuConstants = new D3D11Buffer(context.Device, new BufferDescription
        {
            SizeInBytes = GpuConstantsSize,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CpuAccessFlags = CpuAccessFlags.Write,
            OptionFlags = ResourceOptionFlags.None,
            StructureByteStride = 0,
        });
        _gpuSkinningShader = new ComputeShader(context.Device,
            GpuSkinningShaderBytecode.Value.GetAwaiter().GetResult());
        _gpuMesh = mesh;
    }

    private unsafe void UploadGpuConstants(DeviceContext context, Matrix4x4[] skinningMatrices,
        int vertexCount, bool useSourceBoneIndices)
    {
        SharpDX.DataBox mapped = context.MapSubresource(
            _gpuConstants, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
        try
        {
            byte* destination = (byte*)mapped.DataPointer;
            *(uint*)(destination + 0) = (uint)vertexCount;
            *(uint*)(destination + 4) = (uint)skinningMatrices.Length;
            *(uint*)(destination + 8) = useSourceBoneIndices ? 1u : 0u;
            *(float*)(destination + 12) = _game.IsLEGame() ? 0.5f : 127.5f;
            *(float*)(destination + 16) = _game.IsLEGame() ? 1f : 255f;
            new ReadOnlySpan<Matrix4x4>(skinningMatrices).CopyTo(
                new Span<Matrix4x4>(destination + 32, skinningMatrices.Length));
        }
        finally
        {
            context.UnmapSubresource(_gpuConstants, 0);
        }
    }

    private void DisposeGpuResources()
    {
        _gpuSkinningShader?.Dispose();
        _gpuSkinningShader = null;
        _gpuSkinVerticesView?.Dispose();
        _gpuSkinVerticesView = null;
        _gpuSkinVertices?.Dispose();
        _gpuSkinVertices = null;
        _gpuOutputVerticesView?.Dispose();
        _gpuOutputVerticesView = null;
        _gpuConstants?.Dispose();
        _gpuConstants = null;
        _gpuMesh = null;
    }

    /// <summary>
    /// Bakes a BioMorphFace's stored vertex LOD and final skeleton into this component's bind positions.
    /// The matrix and four-bone blend are shared with the morph editor preview.
    /// </summary>
    public void ApplyMorph(MeshBone[] bindSkeleton,
        LegendaryExplorerCore.Unreal.Classes.BonePosition[] finalSkeleton, Vector3[] morphPositions)
    {
        if (_skinVertices is null || _sourceBindPositions is null || bindSkeleton is null)
        {
            return;
        }

        DisposeGpuResources();
        MeshBone[] editedSkeleton = LegendaryExplorerCore.Unreal.Classes.BioMorphFace.CreateFinalSkeleton(
            bindSkeleton, finalSkeleton);
        _sourceSkeleton ??= bindSkeleton;
        _animationSkeleton ??= bindSkeleton;
        _skinningBindSkeleton = editedSkeleton;
        PrepareRetargetingCache();
        Matrix4x4[] skinningMatrices = LegendaryExplorerCore.Unreal.Classes.BioMorphFace
            .ComputePreviewSkinningMatrices(bindSkeleton, editedSkeleton);
        for (int i = 0; i < _skinVertices.Length; i++)
        {
            ref SkinVertex vertex = ref _skinVertices[i];
            Vector3 position = morphPositions is not null && i < morphPositions.Length
                ? morphPositions[i]
                : _sourceBindPositions[i];
            vertex.BindPosition = LegendaryExplorerCore.Unreal.Classes.BioMorphFace.SkinPreviewPosition(
                position, skinningMatrices,
                vertex.MorphBone0, vertex.Weight0,
                vertex.MorphBone1, vertex.Weight1,
                vertex.MorphBone2, vertex.Weight2,
                vertex.MorphBone3, vertex.Weight3);
        }
        NeedsUpdate = true;
    }

    private Matrix4x4[] GetSkinningMatrices(AnimPlayer animPlayer, out bool useSourceBoneIndices)
    {
        Matrix4x4[] animationMatrices = animPlayer.ComputeSkinningMatrices();
        useSourceBoneIndices = false;
        if (_sourceSkeleton is null || _animationSkeleton is null || _skinningBindSkeleton is null
            || animPlayer.BoneComponentSpaceTransforms is not { Length: > 0 } animationPose
            || _animationBoneMap is null && ReferenceEquals(_skinningBindSkeleton, _animationSkeleton))
        {
            return animationMatrices;
        }

        useSourceBoneIndices = true;
        return ComputeRetargetedSkinningMatricesCached(animationPose);
    }

    private void PrepareRetargetingCache()
    {
        bool requiresRetargeting = _skinningBindSkeleton is { Length: > 0 }
                                   && _animationSkeleton is { Length: > 0 }
                                   && (_animationBoneMap is not null
                                       || !ReferenceEquals(_skinningBindSkeleton, _animationSkeleton));
        if (!requiresRetargeting)
        {
            _retargetBindLocalPose = null;
            _retargetBindComponentPose = null;
            _retargetInverseBindComponentPose = null;
            _retargetAnimatedComponentPose = null;
            _retargetedSkinningMatrices = null;
            _animationInverseComponentPose = null;
            _retargetRotationCorrection = null;
            _retargetTranslationCorrection = null;
            return;
        }

        int targetBoneCount = _skinningBindSkeleton.Length;
        _retargetBindLocalPose = new Matrix4x4[targetBoneCount];
        _retargetBindComponentPose = new Matrix4x4[targetBoneCount];
        _retargetInverseBindComponentPose = new Matrix4x4[targetBoneCount];
        _retargetAnimatedComponentPose = new Matrix4x4[targetBoneCount];
        _retargetedSkinningMatrices = new Matrix4x4[targetBoneCount];
        _animationInverseComponentPose = new Matrix4x4[_animationSkeleton.Length];
        _retargetRotationCorrection = new Matrix4x4[targetBoneCount];
        _retargetTranslationCorrection = new Vector3[targetBoneCount];

        for (int targetIndex = 0; targetIndex < targetBoneCount; targetIndex++)
        {
            MeshBone targetBone = _skinningBindSkeleton[targetIndex];
            Matrix4x4 targetBindLocal = CreateBoneLocalTransform(targetBone);
            _retargetBindLocalPose[targetIndex] = targetBindLocal;
            int targetParentIndex = targetBone.ParentIndex;
            Matrix4x4 targetBindComponent = targetParentIndex >= 0 && targetParentIndex < targetIndex
                ? targetBindLocal * _retargetBindComponentPose[targetParentIndex]
                : targetBindLocal;
            _retargetBindComponentPose[targetIndex] = targetBindComponent;
            _retargetInverseBindComponentPose[targetIndex] = Matrix4x4.Invert(targetBindComponent,
                out Matrix4x4 inverseTargetBind)
                ? inverseTargetBind
                : Matrix4x4.Identity;

            int animationIndex = _animationBoneMap is null
                ? targetIndex
                : targetIndex < _animationBoneMap.Length ? _animationBoneMap[targetIndex] : -1;
            if (animationIndex >= 0 && animationIndex < _animationSkeleton.Length)
            {
                MeshBone animationBindBone = _animationSkeleton[animationIndex];
                Matrix4x4 animationBindRotation = Matrix4x4.CreateFromQuaternion(animationBindBone.Orientation);
                Matrix4x4.Invert(animationBindRotation, out Matrix4x4 inverseAnimationBindRotation);
                _retargetRotationCorrection[targetIndex] =
                    Matrix4x4.CreateFromQuaternion(targetBone.Orientation) * inverseAnimationBindRotation;
                _retargetTranslationCorrection[targetIndex] = targetBone.Position - animationBindBone.Position;
            }
            else
            {
                _retargetRotationCorrection[targetIndex] = Matrix4x4.Identity;
                _retargetTranslationCorrection[targetIndex] = targetBone.Position;
            }
        }
    }

    private Matrix4x4[] ComputeRetargetedSkinningMatricesCached(Matrix4x4[] animationComponentPose)
    {
        if (_retargetedSkinningMatrices is null
            || _retargetedSkinningMatrices.Length != _skinningBindSkeleton.Length)
        {
            PrepareRetargetingCache();
        }

        int animationPoseCount = Math.Min(animationComponentPose.Length,
            _animationInverseComponentPose.Length);
        for (int animationIndex = 0; animationIndex < animationPoseCount; animationIndex++)
        {
            _animationInverseComponentPose[animationIndex] = Matrix4x4.Invert(
                animationComponentPose[animationIndex], out Matrix4x4 inverseAnimationPose)
                ? inverseAnimationPose
                : Matrix4x4.Identity;
        }

        for (int targetIndex = 0; targetIndex < _skinningBindSkeleton.Length; targetIndex++)
        {
            MeshBone targetBone = _skinningBindSkeleton[targetIndex];
            int animationIndex = _animationBoneMap is null
                ? targetIndex
                : targetIndex < _animationBoneMap.Length ? _animationBoneMap[targetIndex] : -1;
            Matrix4x4 targetAnimatedLocal = _retargetBindLocalPose[targetIndex];
            if (animationIndex >= 0 && animationIndex < animationPoseCount)
            {
                int animationParentIndex = _animationSkeleton[animationIndex].ParentIndex;
                Matrix4x4 animationLocal = animationParentIndex >= 0
                                                 && animationParentIndex < animationIndex
                                                 && animationParentIndex < animationPoseCount
                    ? animationComponentPose[animationIndex]
                      * _animationInverseComponentPose[animationParentIndex]
                    : animationComponentPose[animationIndex];
                if (Matrix4x4.Decompose(animationLocal, out Vector3 animationScale,
                        out Quaternion animationRotation, out Vector3 animationTranslation))
                {
                    targetAnimatedLocal = _retargetRotationCorrection[targetIndex]
                                          * Matrix4x4.CreateFromQuaternion(animationRotation)
                                          * Matrix4x4.CreateScale(animationScale)
                                          * Matrix4x4.CreateTranslation(
                                              _retargetTranslationCorrection[targetIndex]
                                              + animationTranslation);
                }
            }

            int targetParentIndex = targetBone.ParentIndex;
            _retargetAnimatedComponentPose[targetIndex] = targetParentIndex >= 0
                                                          && targetParentIndex < targetIndex
                ? targetAnimatedLocal * _retargetAnimatedComponentPose[targetParentIndex]
                : targetAnimatedLocal;
            _retargetedSkinningMatrices[targetIndex] = _retargetInverseBindComponentPose[targetIndex]
                                                        * _retargetAnimatedComponentPose[targetIndex];
        }
        return _retargetedSkinningMatrices;
    }

    /// <summary>
    /// Transfers an animation pose onto a component's own (possibly morphed) reference skeleton.
    /// UE attached skeletal components share animation by bone name, but each component keeps its own
    /// local bind translations and rotations. Reusing the parent's skinning matrices directly rotates
    /// head and eye vertices around the body mesh's different reference-pose pivots.
    /// </summary>
    internal static Matrix4x4[] ComputeRetargetedSkinningMatrices(MeshBone[] targetBindSkeleton,
        MeshBone[] animationBindSkeleton, int[] animationBoneMap, Matrix4x4[] animationComponentPose)
    {
        if (targetBindSkeleton is not { Length: > 0 })
        {
            return [];
        }

        var targetBindComponentPose = new Matrix4x4[targetBindSkeleton.Length];
        var targetAnimatedComponentPose = new Matrix4x4[targetBindSkeleton.Length];
        var matrices = new Matrix4x4[targetBindSkeleton.Length];
        for (int targetIndex = 0; targetIndex < targetBindSkeleton.Length; targetIndex++)
        {
            MeshBone targetBone = targetBindSkeleton[targetIndex];
            Matrix4x4 targetBindLocal = CreateBoneLocalTransform(targetBone);
            int animationIndex = animationBoneMap is null
                ? targetIndex
                : targetIndex < animationBoneMap.Length ? animationBoneMap[targetIndex] : -1;
            Matrix4x4 targetAnimatedLocal = targetBindLocal;
            if (animationIndex >= 0 && animationIndex < animationBindSkeleton.Length
                && animationIndex < animationComponentPose.Length)
            {
                Matrix4x4 animationLocal = GetLocalTransform(animationBindSkeleton,
                    animationComponentPose, animationIndex);
                if (Matrix4x4.Decompose(animationLocal, out Vector3 animationScale,
                        out Quaternion animationRotation, out Vector3 animationTranslation))
                {
                    MeshBone animationBindBone = animationBindSkeleton[animationIndex];
                    Matrix4x4 targetRotation = Matrix4x4.CreateFromQuaternion(targetBone.Orientation);
                    Matrix4x4 animationBindRotation = Matrix4x4.CreateFromQuaternion(animationBindBone.Orientation);
                    Matrix4x4.Invert(animationBindRotation, out Matrix4x4 inverseAnimationBindRotation);
                    targetAnimatedLocal = targetRotation * inverseAnimationBindRotation
                                          * Matrix4x4.CreateFromQuaternion(animationRotation)
                                          * Matrix4x4.CreateScale(animationScale)
                                          * Matrix4x4.CreateTranslation(targetBone.Position
                                                                        + animationTranslation
                                                                        - animationBindBone.Position);
                }
            }

            int targetParentIndex = targetBone.ParentIndex;
            targetBindComponentPose[targetIndex] = targetParentIndex >= 0 && targetParentIndex < targetIndex
                ? targetBindLocal * targetBindComponentPose[targetParentIndex]
                : targetBindLocal;
            targetAnimatedComponentPose[targetIndex] = targetParentIndex >= 0 && targetParentIndex < targetIndex
                ? targetAnimatedLocal * targetAnimatedComponentPose[targetParentIndex]
                : targetAnimatedLocal;
            matrices[targetIndex] = Matrix4x4.Invert(targetBindComponentPose[targetIndex],
                out Matrix4x4 inverseTargetBind)
                ? inverseTargetBind * targetAnimatedComponentPose[targetIndex]
                : Matrix4x4.Identity;
        }
        return matrices;
    }

    private static Matrix4x4 GetLocalTransform(MeshBone[] skeleton, Matrix4x4[] componentPose, int boneIndex)
    {
        int parentIndex = skeleton[boneIndex].ParentIndex;
        if (parentIndex >= 0 && parentIndex < boneIndex && parentIndex < componentPose.Length
            && Matrix4x4.Invert(componentPose[parentIndex], out Matrix4x4 inverseParent))
        {
            return componentPose[boneIndex] * inverseParent;
        }
        return componentPose[boneIndex];
    }

    private static Matrix4x4 CreateBoneLocalTransform(MeshBone bone) =>
        Matrix4x4.CreateFromQuaternion(bone.Orientation) * Matrix4x4.CreateTranslation(bone.Position);

    private static Matrix4x4 BlendMatrix(Matrix4x4[] matrices, int b0, float w0, int b1, float w1, int b2, float w2, int b3, float w3)
    {
        var m = matrices[b0 < matrices.Length ? b0 : 0] * w0;
        if (w1 > 0 && b1 < matrices.Length) m += matrices[b1] * w1;
        if (w2 > 0 && b2 < matrices.Length) m += matrices[b2] * w2;
        if (w3 > 0 && b3 < matrices.Length) m += matrices[b3] * w3;
        return m;
    }

    public void UpdateVertexPositions(Vector3[] positions)
    {
        DisposeGpuResources();
        for (int i = 0; i < _skinVertices.Length && i < positions.Length; i++)
        {
            _skinVertices[i].BindPosition = positions[i];
            _sourceBindPositions[i] = positions[i];
        }
    }

    public void Dispose() => DisposeGpuResources();
}
