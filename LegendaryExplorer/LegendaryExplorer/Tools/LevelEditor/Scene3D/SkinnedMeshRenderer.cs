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
    public int MorphBone0, MorphBone1, MorphBone2, MorphBone3; // source-mesh skeleton indices
    public float Weight0, Weight1, Weight2, Weight3; // normalized weights
}

public class SkinnedMeshRenderer
{
    public bool NeedsUpdate { get; set; }
    private MEGame _game;
    private SkinVertex[] _skinVertices;
    private Vector3[] _sourceBindPositions;
    private MeshBone[] _sourceSkeleton;
    private MeshBone[] _animationSkeleton;
    private MeshBone[] _skinningBindSkeleton;
    private int[] _animationBoneMap;

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
        for (int i = 0; i < vertexCount; i++)
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
        for (int i = 0; i < vertexCount; i++)
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

        return true;
    }

    public void UpdateSkinning(DeviceContext context, Mesh<LEVertex> mesh, AnimPlayer animPlayer)
    {
        if (PrepareSkinning(mesh, animPlayer))
        {
            mesh.UpdateVertices(context);
        }
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
        _sourceSkeleton ??= bindSkeleton;
        _animationSkeleton ??= bindSkeleton;
        _skinningBindSkeleton = editedSkeleton;
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
        return ComputeRetargetedSkinningMatrices(_skinningBindSkeleton, _animationSkeleton,
            _animationBoneMap, animationPose);
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
        for (int i = 0; i < _skinVertices.Length && i < positions.Length; i++)
        {
            _skinVertices[i].BindPosition = positions[i];
            _sourceBindPositions[i] = positions[i];
        }
    }
}
