using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore.Gammtek;
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
    public Vector3 BindTangent;   // Unreal space
    public float BinormalSign;    // -1 or 1
    public Vector2 UV;
    public int Bone0, Bone1, Bone2, Bone3;       // skeleton-wide bone indices (of the animation's skeleton)
    public float Weight0, Weight1, Weight2, Weight3; // normalized weights
    // Fork: indices into the mesh's own skeleton, for BioMorphFace final skeletons and retargeting onto a parent's animation
    public int MorphBone0, MorphBone1, MorphBone2, MorphBone3;
}

/// <remarks>
/// Fork additions (BioMorph previews and attached heads/hair animated by a body's skeleton): <see cref="BuildFromSkeletalMesh(MEGame, StaticLODModel, MeshBone[], MeshBone[])"/>
/// retargets an animation onto the component's own reference skeleton by bone name, and <see cref="ApplyMorph"/> bakes a morph's final skeleton and
/// vertex positions into the bind pose. The fork's GPU skinning path isn't carried over: skinning stays on the CPU, as upstream does it.
/// </remarks>
public class SkinnedMeshRenderer : IDisposable
{
    public bool NeedsUpdate { get; set; }
    private SkinVertex[] _skinVertices;
    private Vector3[] _sourceBindPositions;
    private MeshBone[] _sourceSkeleton;
    private MeshBone[] _animationSkeleton;
    private MeshBone[] _skinningBindSkeleton;
    private int[] _animationBoneMap;
    private Matrix4x4[] _retargetBindLocalPose;
    private Matrix4x4[] _retargetInverseBindComponentPose;
    private Matrix4x4[] _retargetAnimatedComponentPose;
    private Matrix4x4[] _retargetedSkinningMatrices;
    private Matrix4x4[] _animationInverseComponentPose;
    private Matrix4x4[] _retargetRotationCorrection;
    private Vector3[] _retargetTranslationCorrection;

    /// <summary>
    /// Builds per-vertex skinning data from a SkeletalMesh LOD model.
    /// Resolves chunk-local bone indices to skeleton-wide indices via chunk.BoneMap.
    /// </summary>
    public void BuildFromSkeletalMesh(MEGame game, StaticLODModel lodModel) => BuildFromSkeletalMesh(game, lodModel, null, null);

    /// <summary>
    /// As <see cref="BuildFromSkeletalMesh(MEGame, StaticLODModel)"/>, for a component animated by another skeleton (an attached head animated
    /// by its body's): bones are matched by name, and the animation is transferred onto <paramref name="sourceSkeleton"/>'s bind pose
    /// </summary>
    /// <param name="sourceSkeleton">The mesh's own reference skeleton</param>
    /// <param name="animationSkeleton">The skeleton the animation player drives. Null, or the same as <paramref name="sourceSkeleton"/>, for none</param>
    public void BuildFromSkeletalMesh(MEGame game, StaticLODModel lodModel, MeshBone[] sourceSkeleton, MeshBone[] animationSkeleton)
    {
        _sourceSkeleton = sourceSkeleton;
        _animationSkeleton = animationSkeleton;
        _skinningBindSkeleton = sourceSkeleton;
        int[] boneMap = BuildAnimationBoneMap(sourceSkeleton, animationSkeleton);
        _animationBoneMap = boneMap;

        bool isME1 = game == MEGame.ME1;
        int vertexCount = isME1 ? lodModel.ME1VertexBufferGPUSkin.Length : (int)lodModel.NumVertices;
        _skinVertices = new SkinVertex[vertexCount];

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
                skinVert.BindTangent = (Vector3)sv.TangentX;
                skinVert.BinormalSign = ((Vector4)sv.TangentZ).W < 0 ? -1f : 1f;
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
                skinVert.BindTangent = (Vector3)gv.TangentX;
                skinVert.BinormalSign = ((Vector4)gv.TangentZ).W < 0 ? -1f : 1f;
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

    private static void ResolveInfluences(ref SkinVertex skinVert, Influences bones, Influences weights, SkelMeshChunk chunk, int[] boneMap)
    {
        // Resolve chunk-local bone indices to skeleton-wide indices via BoneMap. A morph's final skeleton is indexed against the mesh's own
        // skeleton; an animation may drive another (a parent body's), so both are kept
        skinVert.MorphBone0 = bones[0] < chunk.BoneMap.Length ? chunk.BoneMap[bones[0]] : 0;
        skinVert.MorphBone1 = bones[1] < chunk.BoneMap.Length ? chunk.BoneMap[bones[1]] : 0;
        skinVert.MorphBone2 = bones[2] < chunk.BoneMap.Length ? chunk.BoneMap[bones[2]] : 0;
        skinVert.MorphBone3 = bones[3] < chunk.BoneMap.Length ? chunk.BoneMap[bones[3]] : 0;
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

    private static int ResolveAnimationBoneIndex(int sourceIndex, int[] boneMap) =>
        boneMap is not null && sourceIndex < boneMap.Length ? Math.Max(boneMap[sourceIndex], 0) : sourceIndex;

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
    /// Performs CPU skinning: blends skinning matrices per vertex, transforms bind-pose position/normal,
    /// writes results to the mesh vertex list with Unreal-to-renderer coordinate conversion,
    /// then rebuilds the D3D vertex buffer.
    /// </summary>
    public void UpdateSkinning(DeviceContext context, Mesh<WorldVertex> mesh, AnimPlayer animPlayer)
    {
        UpdateSkinning(context, mesh, animPlayer, static (in SkinVertex sv, Vector3 position, Vector3 normal, Vector3 _) =>
            new WorldVertex(position, new Vector4(normal.X, normal.Z, normal.Y, 1), sv.UV));
    }

    /// <summary>
    /// Performs CPU skinning for a mesh rendered with the game's shaders. Unlike the <see cref="WorldVertex"/> overload,
    /// the tangent basis is skinned too, and the normals are left in Unreal space.
    /// </summary>
    public void UpdateSkinning(DeviceContext context, Mesh<LEVertex> mesh, AnimPlayer animPlayer)
    {
        UpdateSkinning(context, mesh, animPlayer, static (in SkinVertex sv, Vector3 position, Vector3 normal, Vector3 tangent) =>
        {
            Fixed4<Vector4> uvs = default;
            uvs[0] = new Vector4(sv.UV, 0, 0);
            return (LEVertex)LEVertex.Create(position, tangent, new Vector4(normal, sv.BinormalSign), uvs);
        });
    }

    private delegate TVertex SkinnedVertexBuilder<out TVertex>(in SkinVertex sv, Vector3 position, Vector3 normal, Vector3 tangent);

    private void UpdateSkinning<TVertex>(DeviceContext context, Mesh<TVertex> mesh, AnimPlayer animPlayer, SkinnedVertexBuilder<TVertex> buildVertex)
        where TVertex : IVertexBase
    {
        NeedsUpdate = false;
        if (_skinVertices == null || mesh == null || animPlayer == null) return;

        var skinningMatrices = GetSkinningMatrices(animPlayer, out bool useSourceBoneIndices);
        if (skinningMatrices == null) return;

        int vertexCount = Math.Min(_skinVertices.Length, mesh.Vertices.Count);
        for (int i = 0; i < vertexCount; i++)
        {
            ref var sv = ref _skinVertices[i];

            // Blend skinning matrices by bone weights
            var blended = useSourceBoneIndices
                ? BlendMatrix(skinningMatrices, sv.MorphBone0, sv.Weight0, sv.MorphBone1, sv.Weight1, sv.MorphBone2, sv.Weight2, sv.MorphBone3, sv.Weight3)
                : BlendMatrix(skinningMatrices, sv.Bone0, sv.Weight0, sv.Bone1, sv.Weight1, sv.Bone2, sv.Weight2, sv.Bone3, sv.Weight3);

            // Transform bind position and normal in Unreal space
            var skinnedPos = Vector3.Transform(sv.BindPosition, blended);
            var skinnedNormal = Vector3.TransformNormal(sv.BindNormal, blended);
            var skinnedTangent = Vector3.TransformNormal(sv.BindTangent, blended);

            mesh.Vertices[i] = buildVertex(in sv, skinnedPos, skinnedNormal, skinnedTangent);
        }

        mesh.UpdateVertices(context);
    }

    /// <summary>
    /// The animation's skinning matrices, or, for a retargeted or morphed component, matrices onto its own bind skeleton (indexed by
    /// <see cref="SkinVertex.MorphBone0"/> and co, which <paramref name="useSourceBoneIndices"/> says)
    /// </summary>
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
        return ComputeRetargetedSkinningMatrices(animationPose);
    }

    /// <summary>
    /// Component-space bone pose produced by the last skinning update, including morph/retarget adjustments
    /// </summary>
    internal Matrix4x4[] GetCurrentBoneComponentSpaceTransforms(AnimPlayer animPlayer) =>
        _retargetAnimatedComponentPose ?? animPlayer?.BoneComponentSpaceTransforms;

    /// <summary>
    /// Bakes a BioMorphFace's final skeleton and (optionally) its stored vertex LOD into this component's bind positions.
    /// The matrix and four-bone blend are shared with the morph editor's preview
    /// </summary>
    public void ApplyMorph(MeshBone[] bindSkeleton, LegendaryExplorerCore.Unreal.Classes.BonePosition[] finalSkeleton, Vector3[] morphPositions)
    {
        if (_skinVertices is null || _sourceBindPositions is null || bindSkeleton is null)
        {
            return;
        }

        MeshBone[] editedSkeleton = LegendaryExplorerCore.Unreal.Classes.BioMorphFace.CreateFinalSkeleton(bindSkeleton, finalSkeleton);
        _sourceSkeleton ??= bindSkeleton;
        _animationSkeleton ??= bindSkeleton;
        _skinningBindSkeleton = editedSkeleton;
        PrepareRetargetingCache();
        Matrix4x4[] skinningMatrices = LegendaryExplorerCore.Unreal.Classes.BioMorphFace.ComputePreviewSkinningMatrices(bindSkeleton, editedSkeleton);
        for (int i = 0; i < _skinVertices.Length; i++)
        {
            ref SkinVertex vertex = ref _skinVertices[i];
            Vector3 position = morphPositions is not null && i < morphPositions.Length ? morphPositions[i] : _sourceBindPositions[i];
            vertex.BindPosition = LegendaryExplorerCore.Unreal.Classes.BioMorphFace.SkinPreviewPosition(
                position, skinningMatrices,
                vertex.MorphBone0, vertex.Weight0,
                vertex.MorphBone1, vertex.Weight1,
                vertex.MorphBone2, vertex.Weight2,
                vertex.MorphBone3, vertex.Weight3);
        }
        NeedsUpdate = true;
    }

    private void PrepareRetargetingCache()
    {
        bool requiresRetargeting = _skinningBindSkeleton is { Length: > 0 }
                                   && _animationSkeleton is { Length: > 0 }
                                   && (_animationBoneMap is not null || !ReferenceEquals(_skinningBindSkeleton, _animationSkeleton));
        if (!requiresRetargeting)
        {
            _retargetBindLocalPose = null;
            _retargetInverseBindComponentPose = null;
            _retargetAnimatedComponentPose = null;
            _retargetedSkinningMatrices = null;
            _animationInverseComponentPose = null;
            _retargetRotationCorrection = null;
            _retargetTranslationCorrection = null;
            return;
        }

        int targetBoneCount = _skinningBindSkeleton.Length;
        var bindComponentPose = new Matrix4x4[targetBoneCount];
        _retargetBindLocalPose = new Matrix4x4[targetBoneCount];
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
                ? targetBindLocal * bindComponentPose[targetParentIndex]
                : targetBindLocal;
            bindComponentPose[targetIndex] = targetBindComponent;
            _retargetInverseBindComponentPose[targetIndex] = Matrix4x4.Invert(targetBindComponent, out Matrix4x4 inverseTargetBind)
                ? inverseTargetBind
                : Matrix4x4.Identity;

            int animationIndex = AnimationIndexOf(targetIndex);
            if (animationIndex >= 0 && animationIndex < _animationSkeleton.Length)
            {
                MeshBone animationBindBone = _animationSkeleton[animationIndex];
                Matrix4x4 animationBindRotation = Matrix4x4.CreateFromQuaternion(animationBindBone.Orientation);
                Matrix4x4.Invert(animationBindRotation, out Matrix4x4 inverseAnimationBindRotation);
                _retargetRotationCorrection[targetIndex] = Matrix4x4.CreateFromQuaternion(targetBone.Orientation) * inverseAnimationBindRotation;
                _retargetTranslationCorrection[targetIndex] = targetBone.Position - animationBindBone.Position;
            }
            else
            {
                _retargetRotationCorrection[targetIndex] = Matrix4x4.Identity;
                _retargetTranslationCorrection[targetIndex] = targetBone.Position;
            }
        }
    }

    private int AnimationIndexOf(int targetIndex) => _animationBoneMap is null
        ? targetIndex
        : targetIndex < _animationBoneMap.Length ? _animationBoneMap[targetIndex] : -1;

    /// <summary>
    /// Transfers an animation pose onto the component's own (possibly morphed) reference skeleton. Attached skeletal components share animation
    /// by bone name, but each keeps its own bind translations and rotations; reusing the parent's skinning matrices directly would rotate head and
    /// eye vertices around the body's reference-pose pivots
    /// </summary>
    private Matrix4x4[] ComputeRetargetedSkinningMatrices(Matrix4x4[] animationComponentPose)
    {
        if (_retargetedSkinningMatrices is null || _retargetedSkinningMatrices.Length != _skinningBindSkeleton.Length)
        {
            PrepareRetargetingCache();
            if (_retargetedSkinningMatrices is null) return null;
        }

        int animationPoseCount = Math.Min(animationComponentPose.Length, _animationInverseComponentPose.Length);
        for (int animationIndex = 0; animationIndex < animationPoseCount; animationIndex++)
        {
            _animationInverseComponentPose[animationIndex] = Matrix4x4.Invert(animationComponentPose[animationIndex], out Matrix4x4 inverseAnimationPose)
                ? inverseAnimationPose
                : Matrix4x4.Identity;
        }

        for (int targetIndex = 0; targetIndex < _skinningBindSkeleton.Length; targetIndex++)
        {
            MeshBone targetBone = _skinningBindSkeleton[targetIndex];
            int animationIndex = AnimationIndexOf(targetIndex);
            Matrix4x4 targetAnimatedLocal = _retargetBindLocalPose[targetIndex];
            if (animationIndex >= 0 && animationIndex < animationPoseCount)
            {
                int animationParentIndex = _animationSkeleton[animationIndex].ParentIndex;
                Matrix4x4 animationLocal = animationParentIndex >= 0 && animationParentIndex < animationIndex && animationParentIndex < animationPoseCount
                    ? animationComponentPose[animationIndex] * _animationInverseComponentPose[animationParentIndex]
                    : animationComponentPose[animationIndex];
                if (Matrix4x4.Decompose(animationLocal, out Vector3 animationScale, out Quaternion animationRotation, out Vector3 animationTranslation))
                {
                    targetAnimatedLocal = _retargetRotationCorrection[targetIndex]
                                          * Matrix4x4.CreateFromQuaternion(animationRotation)
                                          * Matrix4x4.CreateScale(animationScale)
                                          * Matrix4x4.CreateTranslation(_retargetTranslationCorrection[targetIndex] + animationTranslation);
                }
            }

            int targetParentIndex = targetBone.ParentIndex;
            _retargetAnimatedComponentPose[targetIndex] = targetParentIndex >= 0 && targetParentIndex < targetIndex
                ? targetAnimatedLocal * _retargetAnimatedComponentPose[targetParentIndex]
                : targetAnimatedLocal;
            _retargetedSkinningMatrices[targetIndex] = _retargetInverseBindComponentPose[targetIndex] * _retargetAnimatedComponentPose[targetIndex];
        }
        return _retargetedSkinningMatrices;
    }

    /// <summary>
    /// The skinning matrices that transfer <paramref name="animationComponentPose"/> (of <paramref name="animationBindSkeleton"/>) onto
    /// <paramref name="targetBindSkeleton"/>, as a retargeted component's skinning computes them
    /// </summary>
    /// <param name="animationBoneMap">For each target bone, its animation bone's index (-1 for none); null if the indices are the same</param>
    internal static Matrix4x4[] ComputeRetargetedSkinningMatrices(MeshBone[] targetBindSkeleton, MeshBone[] animationBindSkeleton,
        int[] animationBoneMap, Matrix4x4[] animationComponentPose)
    {
        if (targetBindSkeleton is not { Length: > 0 }) return [];
        var renderer = new SkinnedMeshRenderer
        {
            _sourceSkeleton = targetBindSkeleton,
            _skinningBindSkeleton = targetBindSkeleton,
            _animationSkeleton = animationBindSkeleton,
            _animationBoneMap = animationBoneMap
        };
        renderer.PrepareRetargetingCache();
        return renderer.ComputeRetargetedSkinningMatrices(animationComponentPose)?.ToArray() ?? [];
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
            if (_sourceBindPositions is not null && i < _sourceBindPositions.Length) _sourceBindPositions[i] = positions[i];
        }
    }

    /// <summary>Fork API: the fork's GPU skinning held D3D resources. CPU skinning holds none</summary>
    public void Dispose() => GC.SuppressFinalize(this);
}
