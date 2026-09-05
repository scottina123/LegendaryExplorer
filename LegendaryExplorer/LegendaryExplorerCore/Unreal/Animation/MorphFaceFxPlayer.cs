using System.Numerics;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorerCore.Unreal.Animation;

/// <summary>Layers facial motion over an edited morph skeleton, retaining the original mesh bind pose.</summary>
public sealed class MorphFaceFxPlayer : AnimPlayer
{
    private readonly FaceFxPlayer facePlayer;
    private readonly MeshBone[] editedSkeleton;

    public MorphFaceFxPlayer(MeshBone[] bindSkeleton, MeshBone[] editedSkeleton,
        FaceFXAsset actor, FaceFXAnimSet animSet, FaceFXLine line)
        : base(new SkeletalMesh { RefSkeleton = bindSkeleton })
    {
        this.editedSkeleton = editedSkeleton;
        facePlayer = new FaceFxPlayer(new SkeletalMesh { RefSkeleton = bindSkeleton })
        {
            FxActor = actor,
            AnimSet = animSet
        };
        facePlayer.SetFaceFXLine(line);
    }

    public override bool HasAnimation => facePlayer.HasAnimation;
    public override float Duration => facePlayer.Duration;
    public override float StartTime => facePlayer.StartTime;
    public override float EndTime => facePlayer.EndTime;
    public override void SetCurrentTime(float time) => CurrentTime = time;

    public override Matrix4x4[] ComputeSkinningMatrices()
    {
        facePlayer.SetCurrentTime(CurrentTime);
        facePlayer.ComputeSkinningMatrices();
        Matrix4x4[] faceTransforms = facePlayer.BoneComponentSpaceTransforms;
        for (int i = 0; i < _bones.Length; i++)
        {
            MeshBone bone = editedSkeleton[i];
            Matrix4x4 local = Matrix4x4.CreateFromQuaternion(bone.Orientation)
                              * Matrix4x4.CreateTranslation(bone.Position);
            Matrix4x4 faceLocal = faceTransforms[i];
            int parent = _bones[i].ParentIndex;
            if (parent >= 0 && parent < i && Matrix4x4.Invert(faceTransforms[parent], out var inverseParent))
                faceLocal *= inverseParent;
            if (Matrix4x4.Invert(facePlayer.GetReferenceLocalTransform(i), out var inverseReference))
                local = local * inverseReference * faceLocal;
            _boneComponentSpace[i] = parent >= 0 && parent < i ? local * _boneComponentSpace[parent] : local;
            _skinningMatrices[i] = _inverseBindPose[i] * _boneComponentSpace[i];
        }
        return _skinningMatrices;
    }
}
