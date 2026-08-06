using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.Animation;
using LegendaryExplorerCore.Unreal.BinaryConverters;
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
    public float Weight0, Weight1, Weight2, Weight3; // normalized weights
}

public class SkinnedMeshRenderer
{
    public bool NeedsUpdate { get; set; }
    private MEGame _game;
    private SkinVertex[] _skinVertices;
    private Vector3[] _sourceBindPositions;

    /// <summary>
    /// Builds per-vertex skinning data from a SkeletalMesh LOD model.
    /// Resolves chunk-local bone indices to skeleton-wide indices via chunk.BoneMap.
    /// </summary>
    public void BuildFromSkeletalMesh(MEGame game, StaticLODModel lodModel)
        => BuildFromSkeletalMesh(game, lodModel, null, null);

    public void BuildFromSkeletalMesh(MEGame game, StaticLODModel lodModel, MeshBone[] sourceSkeleton,
        MeshBone[] animationSkeleton)
    {
        _game = game;
        bool isME1 = game == MEGame.ME1;
        int vertexCount = isME1 ? lodModel.ME1VertexBufferGPUSkin.Length : (int)lodModel.NumVertices;
        _skinVertices = new SkinVertex[vertexCount];
        int[] boneMap = BuildAnimationBoneMap(sourceSkeleton, animationSkeleton);

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
        // Resolve chunk-local bone indices to skeleton-wide indices via BoneMap
        skinVert.Bone0 = ResolveBoneIndex(bones[0], chunk, boneMap);
        skinVert.Bone1 = ResolveBoneIndex(bones[1], chunk, boneMap);
        skinVert.Bone2 = ResolveBoneIndex(bones[2], chunk, boneMap);
        skinVert.Bone3 = ResolveBoneIndex(bones[3], chunk, boneMap);

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

    private static int ResolveBoneIndex(byte influenceBone, SkelMeshChunk chunk, int[] boneMap)
    {
        int sourceIndex = influenceBone < chunk.BoneMap.Length ? chunk.BoneMap[influenceBone] : 0;
        return boneMap is not null && sourceIndex < boneMap.Length ? boneMap[sourceIndex] : sourceIndex;
    }

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
            int candidate = sourceIndex;
            while (candidate >= 0 && candidate < sourceSkeleton.Length
                   && !animationBones.TryGetValue(sourceSkeleton[candidate].Name.Name, out map[sourceIndex]))
            {
                int parent = sourceSkeleton[candidate].ParentIndex;
                candidate = parent == candidate ? -1 : parent;
            }
        }
        return map;
    }

    /// <summary>
    /// Performs CPU skinning: blends skinning matrices per vertex, transforms bind-pose position/normal,
    /// writes results to the mesh vertex list with Unreal-to-renderer coordinate conversion,
    /// then rebuilds the D3D vertex buffer.
    /// </summary>
    public void UpdateSkinning(DeviceContext context, Mesh<WorldVertex> mesh, AnimPlayer animPlayer)
    {
        NeedsUpdate = false;
        if (_skinVertices == null || mesh == null) return;

        var skinningMatrices = animPlayer.ComputeSkinningMatrices();
        if (skinningMatrices == null) return;

        mesh.EnsureUniqueVertices();
        int vertexCount = Math.Min(_skinVertices.Length, mesh.Vertices.Count);
        for (int i = 0; i < vertexCount; i++)
        {
            ref var sv = ref _skinVertices[i];

            // Blend skinning matrices by bone weights
            var blended = BlendMatrix(
                skinningMatrices, sv.Bone0, sv.Weight0,
                sv.Bone1, sv.Weight1,
                sv.Bone2, sv.Weight2,
                sv.Bone3, sv.Weight3);

            // Transform bind position and normal in Unreal space
            var skinnedPos = Vector3.Transform(sv.BindPosition, blended);
            var skinnedNormal = Vector3.TransformNormal(sv.BindNormal, blended);

            var rendererNormal = new Vector4(skinnedNormal.X, skinnedNormal.Z, skinnedNormal.Y, 1);

            mesh.Vertices[i] = new WorldVertex(skinnedPos, rendererNormal, sv.UV);
        }

        mesh.UpdateVertices(context);
    }

    /// <summary>
    /// Updates the local-vertex-factory mesh used by the compiled game-shader preview while preserving
    /// its tangents, UV sets, and vertex color.
    /// </summary>
    public void UpdateSkinning(DeviceContext context, Mesh<LEVertex> mesh, AnimPlayer animPlayer)
    {
        NeedsUpdate = false;
        if (_skinVertices == null || mesh == null) return;

        var skinningMatrices = animPlayer.ComputeSkinningMatrices();
        if (skinningMatrices == null) return;

        mesh.EnsureUniqueVertices();
        int vertexCount = Math.Min(_skinVertices.Length, mesh.Vertices.Count);
        for (int i = 0; i < vertexCount; i++)
        {
            ref var sv = ref _skinVertices[i];
            var blended = BlendMatrix(
                skinningMatrices, sv.Bone0, sv.Weight0,
                sv.Bone1, sv.Weight1,
                sv.Bone2, sv.Weight2,
                sv.Bone3, sv.Weight3);

            var skinnedPos = Vector3.Transform(sv.BindPosition, blended);
            var skinnedNormal = Vector3.TransformNormal(sv.BindNormal, blended);
            mesh.Vertices[i] = mesh.Vertices[i].WithPositionAndNormal(
                _game, skinnedPos, new Vector4(skinnedNormal, sv.BindNormalW));
        }

        mesh.UpdateVertices(context);
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

        MeshBone[] editedSkeleton = LegendaryExplorerCore.Unreal.Classes.BioMorphFace.CreateFinalSkeleton(
            bindSkeleton, finalSkeleton);
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
                vertex.Bone0, vertex.Weight0,
                vertex.Bone1, vertex.Weight1,
                vertex.Bone2, vertex.Weight2,
                vertex.Bone3, vertex.Weight3);
        }
        NeedsUpdate = true;
    }

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
        for (int i = 0; i < _skinVertices.Length && i < positions.Length; i++)
        {
            _skinVertices[i].BindPosition = positions[i];
            _sourceBindPositions[i] = positions[i];
        }
    }
}
