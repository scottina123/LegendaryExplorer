using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LegendaryExplorerCore.Unreal.Animation;

/// <summary>
/// Pure math class that handles skeleton bind-pose computation, animation track mapping,
/// and per-frame skinning matrix computation. No rendering dependency.
/// </summary>
/// <remarks>
/// Builds bind-pose transforms from a SkeletalMesh's RefSkeleton.
/// </remarks>
public class AnimSequencePlayer : AnimPlayer
{
    public sealed class ScheduledAnimationClip
    {
        public AnimSequence Animation { get; init; }
        public float StartTime { get; init; }
        public float EndTime { get; init; }
        public float AnimationStartTime { get; init; }
        public float AnimationEndTime { get; init; }
        public float PlayRate { get; init; } = 1f;
        public float BlendInDuration { get; init; }
        public float BlendOutDuration { get; init; }
        public float Weight { get; init; } = 1f;
        public bool Loop { get; init; }
    }

    private sealed class ScheduledAnimationClipState
    {
        public required ScheduledAnimationClip Clip { get; init; }
        public required AnimSequencePlayer Player { get; init; }
    }

    // Animation state
    private AnimSequence _animSequence;
    private int[] _skelToAnimMap; // skeleton bone index -> animTrack[i] or -1 if no track
    private bool _animRotationOnly = true; // animSequence => animSetData.bAnimRotationOnly; default true; if true, bones will ignore position tracks, only using the rotation
    private HashSet<string> _useTranslationBones = []; // animSequence => animSetData.UseTranslationBoneNames; these bones will use the positions from the animation even if _animRotationOnly in true
    private HashSet<string> _forceMeshTranslationBoneNames = []; // animSequence => animSetData.ForceMeshTranslationBoneNames; these bones will use the position from the mesh even if _animRotationOnly is false

    // Crossfade blend state
    private Matrix4x4[] _blendFromComponentSpace;
    private float _crossfadeDuration;
    private List<ScheduledAnimationClipState> _scheduledClips;
    private float _scheduledStartTime;
    private float _scheduledEndTime;
    private Matrix4x4[] _scheduledBasePose;
    private Matrix4x4[] _scheduledLocalPose;

    public AnimSequencePlayer(SkeletalMesh skeletalMesh) : base(skeletalMesh)
    {
        _skelToAnimMap = new int[_bones.Length];
    }

    public NameReference AnimName => _animSequence?.Name ?? "None";
    public int TotalFrames => _animSequence?.NumFrames ?? 0;
    public override float Duration => _scheduledClips != null ? _scheduledEndTime - _scheduledStartTime : _animSequence?.SequenceLength ?? 0f;

    public override float StartTime => _scheduledClips != null ? _scheduledStartTime : 0;

    public override float EndTime => _scheduledClips != null ? _scheduledEndTime : Duration;

    public override bool HasAnimation => _scheduledClips is { Count: > 0 } || _animSequence != null;

    public int CurrentFrame
    {
        get
        {
            if (_animSequence == null || TotalFrames <= 1) return 0;
            float frameRate = (TotalFrames - 1) / Duration;
            return Math.Clamp((int)(CurrentTime * frameRate), 0, TotalFrames - 1);
        }
        set
        {
            if (_animSequence == null || TotalFrames <= 1)
            {
                CurrentTime = 0;
                return;
            }
            float frameRate = (TotalFrames - 1) / Duration;
            CurrentTime = Math.Clamp(value / frameRate, 0, Duration);
        }
    }

    public int BoneCount => _bones?.Length ?? 0;

    /// <summary>
    /// Maps animation bone names to skeleton bone indices and prepares for playback.
    /// </summary>
    public void SetAnimation(AnimSequence animSequence, PackageCache packageCache = null)
    {
        _scheduledClips = null;
        _scheduledBasePose = null;
        _scheduledLocalPose = null;
        _scheduledStartTime = 0;
        _scheduledEndTime = 0;
        _animSequence = animSequence;
        CurrentTime = 0;
        _skelToAnimMap.AsSpan().Fill(-1);

        if (animSequence == null || _bones == null)
        {
            // Reset to bind pose
            if (_skinningMatrices != null)
            {
                for (int i = 0; i < _skinningMatrices.Length; i++)
                {
                    _skinningMatrices[i] = Matrix4x4.Identity;
                }
            }
            return;
        }

        animSequence.DecompressAnimationData();
        if (animSequence.RawAnimationData is null)
        {
            throw new InvalidOperationException("AnimSequence has no animation data!");
        }

        PackageCache temporaryCache = null;
        ExportEntry animSetData;
        try
        {
            animSetData = GetAnimSetData(animSequence, packageCache ?? (temporaryCache = new PackageCache()));
            if (animSetData?.GetProperty<ArrayProperty<NameProperty>>("TrackBoneNames") is { Count: > 0 } trackBoneNames)
            {
                animSequence.Bones = trackBoneNames.Select(nameProperty => nameProperty.Value.Instanced).ToList();
            }

            _animRotationOnly = animSetData?.GetProperty<BoolProperty>("bAnimRotationOnly")?.Value ?? true;
            _useTranslationBones = [.. animSetData?.GetProperty<ArrayProperty<NameProperty>>("UseTranslationBoneNames")?.Select(np => np.Value.Instanced) ?? []];
            _forceMeshTranslationBoneNames = [.. animSetData?.GetProperty<ArrayProperty<NameProperty>>("ForceMeshTranslationBoneNames")?.Select(np => np.Value.Instanced) ?? []];
        }
        finally
        {
            temporaryCache?.Dispose();
        }

        // Build name -> skeleton index map
        var nameToIndex = new Dictionary<string, int>(_bones.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _bones.Length; i++)
        {
            nameToIndex[_bones[i].Name.Instanced] = i;
        }

        // Build reverse lookup: skeleton bone index -> anim track index
        for (int i = 0; i < animSequence.Bones.Count; i++)
        {
            if (nameToIndex.TryGetValue(animSequence.Bones[i], out int skelIdx))
            {
                _skelToAnimMap[skelIdx] = i;
            }
        }

    }

    public void SetAnimationTimeline(IEnumerable<ScheduledAnimationClip> clips, PackageCache packageCache = null)
    {
        _scheduledBasePose = (Matrix4x4[])_boneComponentSpace.Clone();
        _scheduledLocalPose = new Matrix4x4[_boneComponentSpace.Length];
        ConvertComponentToLocalPose(_scheduledBasePose, _scheduledLocalPose);
        _animSequence = null;
        _blendFromComponentSpace = null;
        _crossfadeDuration = 0;
        _scheduledClips = [];

        foreach (ScheduledAnimationClip clip in clips.Where(clip =>
                     clip.Animation != null && clip.EndTime > clip.StartTime && clip.AnimationEndTime > clip.AnimationStartTime))
        {
            clip.Animation.DecompressAnimationData();
            var player = new AnimSequencePlayer(new SkeletalMesh { RefSkeleton = _bones })
            {
                IsLooping = false,
            };
            player.SetAnimation(clip.Animation, packageCache);
            _scheduledClips.Add(new ScheduledAnimationClipState { Clip = clip, Player = player });
        }

        if (_scheduledClips.Count == 0)
        {
            _scheduledClips = null;
            _scheduledBasePose = null;
            _scheduledLocalPose = null;
            _scheduledStartTime = 0;
            _scheduledEndTime = 0;
            CurrentTime = 0;
        }
        else
        {
            _scheduledStartTime = _scheduledClips.Min(state => state.Clip.StartTime);
            _scheduledEndTime = _scheduledClips.Max(state => state.Clip.EndTime);
            CurrentTime = _scheduledStartTime;
        }
    }

    private static ExportEntry GetAnimSetData(AnimSequence animSequence, PackageCache packageCache)
    {
        ObjectProperty animSetDataReference = animSequence?.Export.GetProperty<ObjectProperty>("m_pBioAnimSetData");
        return animSetDataReference?.ResolveToExport(animSequence.Export.FileRef, packageCache);
    }

    private bool ShouldBoneUsePositionTrack(string boneName)
    {
        // anything in this list should always use the mesh position rather than the animation position
        if (_forceMeshTranslationBoneNames.Contains(boneName))
        {
            return false;
        }
        // if animRotationOnly is set (it almost always will be)
        if (_animRotationOnly)
        {
            // then return false unless it is in the list of UseTranslationBoneNames
            return _useTranslationBones.Contains(boneName);
        }
        // otherwise, return true
        return true;
    }

    public override void SetCurrentTime(float time)
    {
        if (!HasAnimation || Duration <= 0)
        {
            CurrentTime = 0;
            return;
        }
        CurrentTime = Math.Clamp(time, StartTime, EndTime);
    }

    /// <summary>
    /// True when a crossfade blend is in progress.
    /// </summary>
    public bool IsBlending => _blendFromComponentSpace != null && _crossfadeDuration > 0 && CurrentTime < _crossfadeDuration;

    /// <summary>
    /// Crossfades from the current animation pose to a new AnimSequence.
    /// Snapshots the current bone transforms and interpolates toward the new animation
    /// over <paramref name="blendDuration"/> seconds.
    /// </summary>
    public void CrossfadeTo(AnimSequence newSequence, float blendDuration, PackageCache packageCache = null)
    {
        if (_animSequence != null && blendDuration > 0 && _boneComponentSpace != null)
        {
            // Ensure component-space transforms are up to date before snapshotting
            ComputeSkinningMatrices();
            _blendFromComponentSpace = (Matrix4x4[])_boneComponentSpace.Clone();
            _crossfadeDuration = blendDuration;
        }
        else
        {
            _blendFromComponentSpace = null;
            _crossfadeDuration = 0;
        }

        SetAnimation(newSequence, packageCache);
    }

    /// <summary>
    /// Clears any active crossfade blend state.
    /// </summary>
    public void ClearBlend()
    {
        _blendFromComponentSpace = null;
        _crossfadeDuration = 0;
    }

    /// <summary>
    /// Computes skinning matrices for the current frame.
    /// Returns the array of skinning matrices (InverseBindPose * AnimatedComponentSpace).
    /// </summary>
    public override Matrix4x4[] ComputeSkinningMatrices()
    {
        if (_bones == null || _skinningMatrices == null) return null;

        if (_scheduledClips != null)
        {
            return ComputeScheduledSkinningMatrices();
        }

        int numBones = _bones.Length;

        float frame = 0;
        if (TotalFrames > 1 && Duration > 0)
        {
            float frameRate = (TotalFrames - 1) / Duration;
            frame = CurrentTime * frameRate;
            frame = Math.Clamp(frame, 0, TotalFrames - 1);
        }

        for (int i = 0; i < numBones; i++)
        {
            Matrix4x4 localTransform;
            int trackIdx = _skelToAnimMap[i];
            var bone = _bones[i];

            if (trackIdx >= 0 && _animSequence?.RawAnimationData != null && trackIdx < _animSequence.RawAnimationData.Count)
            {
                var track = _animSequence.RawAnimationData[trackIdx];

                var pos = ShouldBoneUsePositionTrack(bone.Name)
                    ? SamplePosition(track, frame, bone.Position)
                    : bone.Position;

                // UE3 stores animation rotations conjugated relative to RefSkeleton bone orientations.
                // So animRot = Conjugate(bone.Orientation) for the same rotation.
                // We conjugate the sampled animation rotation so it matches the bind pose convention
                // (which uses bone.Orientation directly). This ensures InvBindPose * AnimatedCS = Identity
                // at rest pose. Fallback uses bone.Orientation directly (already in bind pose convention).
                var rot = (track.Rotations is { Count: > 0 })
                    ? -Quaternion.Conjugate(SampleRotation(track, frame))
                    : bone.Orientation;

                localTransform = Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos);
            }
            else
            {
                // No animation track for this bone at all - use bind pose local transform
                localTransform = Matrix4x4.CreateFromQuaternion(bone.Orientation) * Matrix4x4.CreateTranslation(bone.Position);
            }

            if (bone.ParentIndex >= 0 && bone.ParentIndex < i)
            {
                _boneComponentSpace[i] = localTransform * _boneComponentSpace[bone.ParentIndex];
            }
            else
            {
                _boneComponentSpace[i] = localTransform;
            }

            _skinningMatrices[i] = _inverseBindPose[i] * _boneComponentSpace[i];
        }

        // Crossfade blend pass: interpolate between snapshotted pose and current animation
        if (_blendFromComponentSpace != null && _crossfadeDuration > 0)
        {
            float alpha = Math.Clamp(CurrentTime / _crossfadeDuration, 0f, 1f);
            if (alpha >= 1f)
            {
                _blendFromComponentSpace = null;
                _crossfadeDuration = 0;
            }
            else
            {
                for (int i = 0; i < numBones; i++)
                {
                    if (Matrix4x4.Decompose(_blendFromComponentSpace[i], out _, out var rotFrom, out var posFrom)
                        && Matrix4x4.Decompose(_boneComponentSpace[i], out _, out var rotTo, out var posTo))
                    {
                        var blendedRot = Quaternion.Slerp(rotFrom, rotTo, alpha);
                        var blendedPos = Vector3.Lerp(posFrom, posTo, alpha);
                        _boneComponentSpace[i] = Matrix4x4.CreateFromQuaternion(blendedRot) * Matrix4x4.CreateTranslation(blendedPos);
                    }
                    _skinningMatrices[i] = _inverseBindPose[i] * _boneComponentSpace[i];
                }
            }
        }

        return _skinningMatrices;
    }

    private Matrix4x4[] ComputeScheduledSkinningMatrices()
    {
        List<(ScheduledAnimationClipState State, float Weight)> activeClips = [];
        foreach (ScheduledAnimationClipState state in _scheduledClips)
        {
            ScheduledAnimationClip clip = state.Clip;
            if (CurrentTime < clip.StartTime || CurrentTime > clip.EndTime)
            {
                continue;
            }

            float clipTime = Math.Max(0, CurrentTime - clip.StartTime);
            float animationDuration = clip.AnimationEndTime - clip.AnimationStartTime;
            float animationTime = clip.AnimationStartTime + clipTime * Math.Max(0.0001f, clip.PlayRate);
            if (clip.Loop && animationDuration > 0)
            {
                animationTime = clip.AnimationStartTime + (animationTime - clip.AnimationStartTime) % animationDuration;
            }
            else
            {
                animationTime = Math.Clamp(animationTime, clip.AnimationStartTime, clip.AnimationEndTime);
            }

            state.Player.CurrentTime = animationTime;
            state.Player.ComputeSkinningMatrices();

            float weight = Math.Max(0, clip.Weight);
            if (clip.BlendInDuration > 0)
            {
                float blendProgress = clipTime / clip.BlendInDuration;
                if (clip.StartTime == _scheduledStartTime && clipTime == 0)
                {
                    blendProgress = Math.Min(1, (1f / 60f) / clip.BlendInDuration);
                }
                weight *= Math.Clamp(blendProgress, 0, 1);
            }
            if (clip.BlendOutDuration > 0)
            {
                weight *= Math.Clamp((clip.EndTime - CurrentTime) / clip.BlendOutDuration, 0, 1);
            }
            if (weight > 0)
            {
                activeClips.Add((state, weight));
            }
        }

        if (activeClips.Count == 0)
        {
            return _skinningMatrices;
        }

        var blendedLocalPose = new Matrix4x4[_bones.Length];
        var clipLocalPose = new Matrix4x4[_bones.Length];
        float activeWeight = activeClips.Sum(activeClip => activeClip.Weight);
        float accumulatedWeight = _scheduledLocalPose is null ? 0 : Math.Max(0, 1 - activeWeight);
        if (accumulatedWeight > 0)
        {
            Array.Copy(_scheduledLocalPose, blendedLocalPose, blendedLocalPose.Length);
        }

        for (int clipIndex = 0; clipIndex < activeClips.Count; clipIndex++)
        {
            (ScheduledAnimationClipState state, float weight) = activeClips[clipIndex];
            ConvertComponentToLocalPose(state.Player._boneComponentSpace, clipLocalPose);
            float alpha = accumulatedWeight <= 0 ? 1 : Math.Clamp(weight / (accumulatedWeight + weight), 0, 1);
            for (int boneIndex = 0; boneIndex < _bones.Length; boneIndex++)
            {
                if (Matrix4x4.Decompose(blendedLocalPose[boneIndex], out _, out Quaternion currentRotation, out Vector3 currentPosition)
                    && Matrix4x4.Decompose(clipLocalPose[boneIndex], out _, out Quaternion nextRotation, out Vector3 nextPosition))
                {
                    Quaternion rotation = Quaternion.Slerp(currentRotation, nextRotation, alpha);
                    Vector3 position = Vector3.Lerp(currentPosition, nextPosition, alpha);
                    blendedLocalPose[boneIndex] = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
                }
            }
            accumulatedWeight += weight;
        }

        for (int boneIndex = 0; boneIndex < _bones.Length; boneIndex++)
        {
            int parentIndex = _bones[boneIndex].ParentIndex;
            _boneComponentSpace[boneIndex] = parentIndex >= 0 && parentIndex < boneIndex
                ? blendedLocalPose[boneIndex] * _boneComponentSpace[parentIndex]
                : blendedLocalPose[boneIndex];
            _skinningMatrices[boneIndex] = _inverseBindPose[boneIndex] * _boneComponentSpace[boneIndex];
        }

        return _skinningMatrices;
    }

    private void ConvertComponentToLocalPose(Matrix4x4[] componentPose, Matrix4x4[] localPose)
    {
        for (int boneIndex = 0; boneIndex < _bones.Length; boneIndex++)
        {
            int parentIndex = _bones[boneIndex].ParentIndex;
            if (parentIndex >= 0 && parentIndex < boneIndex
                && Matrix4x4.Invert(componentPose[parentIndex], out Matrix4x4 inverseParent))
            {
                localPose[boneIndex] = componentPose[boneIndex] * inverseParent;
            }
            else
            {
                localPose[boneIndex] = componentPose[boneIndex];
            }
        }
    }

    private static Vector3 SamplePosition(AnimTrack track, float frame, Vector3 bonePosition)
    {
        // Use animation position if track has keys, otherwise fall back to bind pose position.
        // Many bones only have rotation animation, so position tracks are often empty.
        if (track.Positions == null || track.Positions.Count == 0)
            return bonePosition;
        if (track.Positions.Count == 1)
            return track.Positions[0];

        int frameIdx = Math.Clamp((int)frame, 0, track.Positions.Count - 1);
        var first = track.Positions[frameIdx];
        float lerpAmount = frame - frameIdx;
        if (lerpAmount is > 0 and < 1 && track.Positions.Count > frameIdx + 1)
        {
            return Vector3.Lerp(first, track.Positions[frameIdx + 1], lerpAmount);
        }
        return first;
    }

    private static Quaternion SampleRotation(AnimTrack track, float frame)
    {
        if (track.Rotations == null || track.Rotations.Count == 0)
            return Quaternion.Identity;
        if (track.Rotations.Count == 1)
            return track.Rotations[0];

        int frameIdx = Math.Clamp((int)frame, 0, track.Rotations.Count - 1);
        var first = track.Rotations[frameIdx];
        float lerpAmount = frame - frameIdx;
        if (lerpAmount is > 0 and < 1 && track.Rotations.Count > frameIdx + 1)
        {
            return Quaternion.Lerp(first, track.Rotations[frameIdx + 1], lerpAmount);
        }
        return first;
    }
}
