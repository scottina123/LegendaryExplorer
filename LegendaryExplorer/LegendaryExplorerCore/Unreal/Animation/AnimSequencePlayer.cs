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
    // Authored dialogue poses often contain sub-unit Root drift from compression or capture noise.
    // Treating that as locomotion makes a pose overlay steal ownership from a walk animation.
    private const float MinimumRootMotionDistance = 1f;

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
        public bool IsBaseLayer { get; init; }
        public bool UseMotionBoneMask { get; init; }
        public bool NormalizeRootTranslation { get; init; }
        public bool HoldBeforeStart { get; init; }
    }

    private sealed class ScheduledAnimationClipState
    {
        public required ScheduledAnimationClip Clip { get; init; }
        public required AnimSequencePlayer Player { get; init; }
        public required bool[] BoneMask { get; init; }
        public bool HasAnimatedRootTranslation { get; init; }
        public Vector3 RootStartPosition { get; init; }
        public Vector3 RootEndPosition { get; init; }
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
    private Matrix4x4[] _scheduledBlendedLocalPose;
    private Matrix4x4[] _scheduledClipLocalPose;
    private float[] _scheduledOverlayWeights;
    private float[] _scheduledAccumulatedWeights;
    private readonly List<(ScheduledAnimationClipState State, float Weight, bool HeldBeforeStart)>
        _scheduledActiveClips = [];
    private readonly List<ScheduledAnimationClipState> _scheduledRootMotionClips = [];
    private bool _scheduledNormalizesRootTranslation;
    private readonly int _rootMotionBoneIndex;

    public AnimSequencePlayer(SkeletalMesh skeletalMesh) : base(skeletalMesh)
    {
        _skelToAnimMap = new int[_bones.Length];
        // BioAnimSetData names the locomotion translation track `Root`. Some character rigs have
        // a separate parentless master bone above it, so hierarchy-root detection alone leaves the
        // actual animation Root translation untouched. Prefer the mapped animation bone name and
        // retain the hierarchy root as a fallback for rigs that use a different convention.
        _rootMotionBoneIndex = Array.FindIndex(_bones,
            bone => bone.Name.Name.Equals("Root", StringComparison.OrdinalIgnoreCase));
        for (int boneIndex = 0; _rootMotionBoneIndex < 0 && boneIndex < _bones.Length; boneIndex++)
        {
            if (_bones[boneIndex].ParentIndex < 0 || _bones[boneIndex].ParentIndex == boneIndex)
            {
                _rootMotionBoneIndex = boneIndex;
                break;
            }
        }
    }

    public NameReference AnimName => _animSequence?.Name ?? "None";
    public int TotalFrames => _animSequence?.NumFrames ?? 0;
    public override float Duration => _scheduledClips != null ? _scheduledEndTime - _scheduledStartTime : _animSequence?.SequenceLength ?? 0f;

    public override float StartTime => _scheduledClips != null ? _scheduledStartTime : 0;

    public override float EndTime => _scheduledClips != null ? _scheduledEndTime : Duration;

    public override bool HasAnimation => _scheduledClips is { Count: > 0 } || _animSequence != null;

    /// <summary>
    /// Root translation accumulated across the scheduled clips at <see cref="AnimPlayer.CurrentTime"/>.
    /// This is populated only when a scheduled clip requests root normalization. The caller can
    /// apply it to the owning actor while the skeletal Root remains fixed at its reference position.
    /// </summary>
    public Vector3 ExtractedRootMotionTranslation { get; private set; }

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
    public void SetAnimation(AnimSequence animSequence, PackageCache packageCache = null,
        bool animationDataIsPrepared = false)
    {
        _scheduledClips = null;
        _scheduledBasePose = null;
        _scheduledLocalPose = null;
        _scheduledStartTime = 0;
        _scheduledEndTime = 0;
        _scheduledNormalizesRootTranslation = false;
        _scheduledRootMotionClips.Clear();
        _scheduledActiveClips.Clear();
        ExtractedRootMotionTranslation = Vector3.Zero;
        _animSequence = animSequence;
        CurrentTime = 0;
        _skelToAnimMap.AsSpan().Fill(-1);

        if (animSequence == null)
        {
            ComputeBindPose();
            return;
        }

        if (!animationDataIsPrepared)
        {
            animSequence.DecompressAnimationData();
        }
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
        _scheduledRootMotionClips.Clear();
        _scheduledActiveClips.Clear();

        foreach (ScheduledAnimationClip clip in clips.Where(clip =>
                     clip.Animation != null && clip.EndTime > clip.StartTime && clip.AnimationEndTime > clip.AnimationStartTime))
        {
            clip.Animation.DecompressAnimationData();
            var player = new AnimSequencePlayer(new SkeletalMesh { RefSkeleton = _bones })
            {
                IsLooping = false,
            };
            player.SetAnimation(clip.Animation, packageCache);
            Vector3 rootStartPosition = SampleRootLocalPosition(player, clip.AnimationStartTime);
            Vector3 rootEndPosition = SampleRootLocalPosition(player, clip.AnimationEndTime);
            bool hasAnimatedRootTranslation = HasAnimatedRootTranslation(player);
            _scheduledClips.Add(new ScheduledAnimationClipState
            {
                Clip = clip,
                Player = player,
                // Ordinary dialogue gestures layer over the authored starting pose. Preserve its
                // seated/leaning lower body; full-body root-motion gestures still animate every
                // authored moving bone.
                BoneMask = player.BuildScheduledBoneMask(clip.UseMotionBoneMask,
                    preserveBaseLowerBody: clip.UseMotionBoneMask && !hasAnimatedRootTranslation),
                HasAnimatedRootTranslation = hasAnimatedRootTranslation,
                RootStartPosition = rootStartPosition,
                RootEndPosition = rootEndPosition,
            });
        }

        _scheduledRootMotionClips.AddRange(_scheduledClips
            .Where(state => state.Clip.NormalizeRootTranslation && state.HasAnimatedRootTranslation)
            .OrderBy(state => state.Clip.StartTime));
        if (_scheduledActiveClips.Capacity < _scheduledClips.Count)
        {
            _scheduledActiveClips.Capacity = _scheduledClips.Count;
        }
        _scheduledNormalizesRootTranslation = _rootMotionBoneIndex >= 0
                                               && _scheduledClips.Any(state =>
                                                   state.Clip.NormalizeRootTranslation);
        _scheduledBlendedLocalPose ??= new Matrix4x4[_bones.Length];
        _scheduledClipLocalPose ??= new Matrix4x4[_bones.Length];
        _scheduledOverlayWeights ??= new float[_bones.Length];
        _scheduledAccumulatedWeights ??= new float[_bones.Length];

        if (_scheduledClips.Count == 0)
        {
            _scheduledClips = null;
            _scheduledBasePose = null;
            _scheduledLocalPose = null;
            _scheduledStartTime = 0;
            _scheduledEndTime = 0;
            _scheduledNormalizesRootTranslation = false;
            _scheduledRootMotionClips.Clear();
            ExtractedRootMotionTranslation = Vector3.Zero;
            CurrentTime = 0;
        }
        else
        {
            _scheduledStartTime = _scheduledClips.Any(state => state.Clip.HoldBeforeStart)
                ? Math.Min(0, _scheduledClips.Min(state => state.Clip.StartTime))
                : _scheduledClips.Min(state => state.Clip.StartTime);
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

    private bool[] BuildScheduledBoneMask(bool motionOnly, bool preserveBaseLowerBody = false)
    {
        var boneMask = new bool[_bones.Length];
        int upperBodyRoot = preserveBaseLowerBody ? FindUpperBodyRootIndex() : -1;
        bool foundMotion = false;
        for (int boneIndex = 0; boneIndex < _bones.Length; boneIndex++)
        {
            int trackIndex = _skelToAnimMap[boneIndex];
            if (trackIndex < 0 || _animSequence?.RawAnimationData is null
                               || trackIndex >= _animSequence.RawAnimationData.Count)
            {
                continue;
            }

            if (!motionOnly)
            {
                boneMask[boneIndex] = true;
                continue;
            }

            AnimTrack track = _animSequence.RawAnimationData[trackIndex];
            bool hasMotion = track.Positions is { Count: > 1 } || track.Rotations is { Count: > 1 };
            if (hasMotion && upperBodyRoot >= 0)
            {
                hasMotion = IsBoneDescendantOf(boneIndex, upperBodyRoot);
            }
            boneMask[boneIndex] = hasMotion;
            foundMotion |= hasMotion;
        }

        // A single-frame authored gesture is a pose rather than a motion clip. In that case the
        // complete authored pose is still meaningful and must not disappear from the timeline.
        if (motionOnly && !foundMotion)
        {
            for (int boneIndex = 0; boneIndex < _bones.Length; boneIndex++)
            {
                boneMask[boneIndex] = _skelToAnimMap[boneIndex] >= 0;
            }
        }

        return boneMask;
    }

    private int FindUpperBodyRootIndex()
    {
        string[] preferredNames = ["LowerBack", "Spine", "Spine1", "Spine_01", "Spine01", "Chest"];
        foreach (string preferredName in preferredNames)
        {
            int boneIndex = Array.FindIndex(_bones,
                bone => bone.Name.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase));
            if (boneIndex >= 0)
            {
                return boneIndex;
            }
        }
        return -1;
    }

    private bool IsBoneDescendantOf(int boneIndex, int ancestorIndex)
    {
        for (int depth = 0; boneIndex >= 0 && boneIndex < _bones.Length && depth < _bones.Length; depth++)
        {
            if (boneIndex == ancestorIndex)
            {
                return true;
            }
            int parentIndex = _bones[boneIndex].ParentIndex;
            if (parentIndex == boneIndex)
            {
                break;
            }
            boneIndex = parentIndex;
        }
        return false;
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
        _scheduledActiveClips.Clear();
        foreach (ScheduledAnimationClipState state in _scheduledClips)
        {
            ScheduledAnimationClip clip = state.Clip;
            bool heldBeforeStart = clip.HoldBeforeStart && CurrentTime < clip.StartTime;
            bool retainFinalFrame = CurrentTime >= _scheduledEndTime && clip.EndTime >= _scheduledEndTime;
            if (!heldBeforeStart
                && (CurrentTime < clip.StartTime || CurrentTime >= clip.EndTime && !retainFinalFrame))
            {
                continue;
            }

            float clipTime = heldBeforeStart ? 0 : Math.Max(0, CurrentTime - clip.StartTime);
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
            if (!heldBeforeStart && clip.BlendInDuration > 0)
            {
                float blendProgress = clipTime / clip.BlendInDuration;
                if (clip.StartTime == _scheduledStartTime && clipTime == 0)
                {
                    blendProgress = Math.Min(1, (1f / 60f) / clip.BlendInDuration);
                }
                weight *= Math.Clamp(blendProgress, 0, 1);
            }
            if (!heldBeforeStart && clip.BlendOutDuration > 0)
            {
                weight *= Math.Clamp((clip.EndTime - CurrentTime) / clip.BlendOutDuration, 0, 1);
            }
            if (weight > 0)
            {
                _scheduledActiveClips.Add((state, weight, heldBeforeStart));
            }
        }

        if (_scheduledActiveClips.Count == 0 && !_scheduledNormalizesRootTranslation)
        {
            if (_scheduledBasePose is not null)
            {
                Array.Copy(_scheduledBasePose, _boneComponentSpace, _boneComponentSpace.Length);
                for (int boneIndex = 0; boneIndex < _bones.Length; boneIndex++)
                {
                    _skinningMatrices[boneIndex] = _inverseBindPose[boneIndex] * _boneComponentSpace[boneIndex];
                }
            }
            return _skinningMatrices;
        }

        Matrix4x4[] blendedLocalPose = _scheduledBlendedLocalPose;
        Matrix4x4[] clipLocalPose = _scheduledClipLocalPose;
        if (_scheduledLocalPose is not null)
        {
            Array.Copy(_scheduledLocalPose, blendedLocalPose, blendedLocalPose.Length);
        }

        // Pose animations establish the continuously evaluated base layer. This is how gesture
        // tracks can keep a walk cycle running while dialogue gestures animate the upper body.
        foreach ((ScheduledAnimationClipState state, float weight, bool heldBeforeStart) in _scheduledActiveClips)
        {
            if (!state.Clip.IsBaseLayer && !heldBeforeStart)
            {
                continue;
            }
            ConvertComponentToLocalPose(state.Player._boneComponentSpace, clipLocalPose);
            float alpha = Math.Clamp(weight, 0, 1);
            for (int boneIndex = 0; boneIndex < _bones.Length; boneIndex++)
            {
                if (!heldBeforeStart && !state.BoneMask[boneIndex]
                    || heldBeforeStart && state.Player._skelToAnimMap[boneIndex] < 0)
                {
                    continue;
                }

                BlendLocalTransform(ref blendedLocalPose[boneIndex], clipLocalPose[boneIndex], alpha);
            }
        }

        Array.Clear(_scheduledOverlayWeights);
        foreach ((ScheduledAnimationClipState state, float weight, bool heldBeforeStart) in _scheduledActiveClips)
        {
            if (state.Clip.IsBaseLayer || heldBeforeStart)
            {
                continue;
            }
            for (int boneIndex = 0; boneIndex < _bones.Length; boneIndex++)
            {
                if (state.BoneMask[boneIndex])
                {
                    _scheduledOverlayWeights[boneIndex] += weight;
                }
            }
        }
        for (int boneIndex = 0; boneIndex < _bones.Length; boneIndex++)
        {
            _scheduledAccumulatedWeights[boneIndex] = Math.Max(0, 1 - _scheduledOverlayWeights[boneIndex]);
        }

        Vector3 rootBasePosition = Vector3.Zero;
        Vector3 dominantRootPosition = Vector3.Zero;
        float dominantRootMotion = -1;
        if (_rootMotionBoneIndex >= 0
            && Matrix4x4.Decompose(blendedLocalPose[_rootMotionBoneIndex], out _, out _, out rootBasePosition))
        {
            dominantRootPosition = rootBasePosition;
            dominantRootMotion = 0;
        }

        for (int clipIndex = 0; clipIndex < _scheduledActiveClips.Count; clipIndex++)
        {
            (ScheduledAnimationClipState state, float weight, bool heldBeforeStart) =
                _scheduledActiveClips[clipIndex];
            if (state.Clip.IsBaseLayer || heldBeforeStart)
            {
                continue;
            }
            ConvertComponentToLocalPose(state.Player._boneComponentSpace, clipLocalPose);
            for (int boneIndex = 0; boneIndex < _bones.Length; boneIndex++)
            {
                if (!state.BoneMask[boneIndex])
                {
                    continue;
                }

                float accumulatedWeight = _scheduledAccumulatedWeights[boneIndex];
                float alpha = accumulatedWeight <= 0
                    ? 1
                    : Math.Clamp(weight / (accumulatedWeight + weight), 0, 1);
                if (Matrix4x4.Decompose(blendedLocalPose[boneIndex], out _, out Quaternion currentRotation, out Vector3 currentPosition)
                    && Matrix4x4.Decompose(clipLocalPose[boneIndex], out _, out Quaternion nextRotation, out Vector3 nextPosition))
                {
                    Quaternion rotation = Quaternion.Slerp(currentRotation, nextRotation, alpha);
                    Vector3 position = Vector3.Lerp(currentPosition, nextPosition, alpha);
                    if (boneIndex == _rootMotionBoneIndex && dominantRootMotion >= 0)
                    {
                        if (state.HasAnimatedRootTranslation)
                        {
                            Vector3 candidate = Vector3.Lerp(rootBasePosition, nextPosition,
                                Math.Clamp(weight, 0, 1));
                            float candidateMotion = Vector3.DistanceSquared(candidate, rootBasePosition);
                            if (candidateMotion > dominantRootMotion)
                            {
                                dominantRootPosition = candidate;
                                dominantRootMotion = candidateMotion;
                            }
                        }
                        position = dominantRootPosition;
                    }
                    blendedLocalPose[boneIndex] = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
                }
                _scheduledAccumulatedWeights[boneIndex] += weight;
            }
        }

        ExtractedRootMotionTranslation = _scheduledNormalizesRootTranslation
            ? ComputeExtractedRootMotionTranslation(CurrentTime)
            : Vector3.Zero;
        if (_scheduledNormalizesRootTranslation
            && Matrix4x4.Decompose(blendedLocalPose[_rootMotionBoneIndex], out _, out Quaternion rootRotation,
                out _))
        {
            // UE3 extracts root translation into the owning actor. Leaving it on the skeletal Root
            // makes each BioGestureData slice restart from its own authored coordinates, producing
            // a visible snap at clip boundaries. Keep the bone at the mesh reference position; the
            // conversation preview applies ExtractedRootMotionTranslation to the actor transform.
            // Root rotation remains authored pose data: walk-out/strafe animations use it to turn
            // the body independently of the TrackMove displacement direction.
            blendedLocalPose[_rootMotionBoneIndex] = Matrix4x4.CreateFromQuaternion(rootRotation)
                                                      * Matrix4x4.CreateTranslation(
                                                          _bones[_rootMotionBoneIndex].Position);
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

    /// <summary>
    /// Evaluates normalized scheduled root translation without evaluating and blending the full
    /// skeleton. Dialogue playback uses this for actor movement before the pose is skinned once.
    /// </summary>
    public Vector3 EvaluateExtractedRootMotionTranslation(float timelineTime) =>
        _scheduledNormalizesRootTranslation
            ? ComputeExtractedRootMotionTranslation(timelineTime)
            : Vector3.Zero;

    private Vector3 ComputeExtractedRootMotionTranslation(float timelineTime)
    {
        Vector3 translation = Vector3.Zero;
        // BioGesture pose clips may overlap, but their root motion is not additive. A newer
        // gesture with authored Root translation takes ownership from the older gesture at its
        // start time. Keep the older displacement already travelled, then continue from the new
        // clip's authored root curve. Constant-root pose/head clips do not interrupt locomotion.
        for (int clipIndex = 0; clipIndex < _scheduledRootMotionClips.Count; clipIndex++)
        {
            ScheduledAnimationClipState state = _scheduledRootMotionClips[clipIndex];
            ScheduledAnimationClip clip = state.Clip;
            if (timelineTime <= clip.StartTime)
            {
                continue;
            }

            float ownershipEndTime = clip.EndTime;
            if (clipIndex + 1 < _scheduledRootMotionClips.Count)
            {
                ownershipEndTime = Math.Min(ownershipEndTime,
                    _scheduledRootMotionClips[clipIndex + 1].Clip.StartTime);
            }
            if (ownershipEndTime <= clip.StartTime)
            {
                continue;
            }

            float timelineElapsed = Math.Clamp(
                Math.Min(timelineTime, ownershipEndTime) - clip.StartTime,
                0,
                ownershipEndTime - clip.StartTime);
            float animationElapsed = timelineElapsed * Math.Max(0.0001f, clip.PlayRate);
            float animationDuration = clip.AnimationEndTime - clip.AnimationStartTime;
            if (animationDuration <= 0)
            {
                continue;
            }

            int completedLoops = 0;
            float animationTime;
            if (clip.Loop)
            {
                completedLoops = (int)MathF.Floor(animationElapsed / animationDuration);
                float remainder = animationElapsed - completedLoops * animationDuration;
                animationTime = clip.AnimationStartTime + remainder;
            }
            else
            {
                animationTime = Math.Clamp(clip.AnimationStartTime + animationElapsed,
                    clip.AnimationStartTime, clip.AnimationEndTime);
            }

            Vector3 currentPosition = SampleRootLocalPosition(state.Player, animationTime);
            Vector3 clipTranslation = completedLoops * (state.RootEndPosition - state.RootStartPosition)
                                      + currentPosition - state.RootStartPosition;
            translation += clipTranslation * Math.Max(0, clip.Weight);
        }
        return translation;
    }

    private static bool HasAnimatedRootTranslation(AnimSequencePlayer player)
    {
        int rootBoneIndex = player._rootMotionBoneIndex;
        if (rootBoneIndex < 0 || rootBoneIndex >= player._skelToAnimMap.Length
                              || !player.ShouldBoneUsePositionTrack(player._bones[rootBoneIndex].Name))
        {
            return false;
        }

        int trackIndex = player._skelToAnimMap[rootBoneIndex];
        if (trackIndex < 0 || player._animSequence?.RawAnimationData is null
                           || trackIndex >= player._animSequence.RawAnimationData.Count
                           || player._animSequence.RawAnimationData[trackIndex].Positions is not { Count: > 1 } positions)
        {
            return false;
        }

        Vector3 firstPosition = positions[0];
        float minimumDistanceSquared = MinimumRootMotionDistance * MinimumRootMotionDistance;
        return positions.Any(position =>
            Vector3.DistanceSquared(position, firstPosition) > minimumDistanceSquared);
    }

    private Vector3 SampleRootLocalPosition(AnimSequencePlayer player, float animationTime)
    {
        if (_rootMotionBoneIndex < 0)
        {
            return Vector3.Zero;
        }

        player.SetCurrentTime(animationTime);
        player.ComputeSkinningMatrices();
        Matrix4x4 rootTransform = player._boneComponentSpace[_rootMotionBoneIndex];
        int parentIndex = _bones[_rootMotionBoneIndex].ParentIndex;
        if (parentIndex >= 0 && parentIndex < _rootMotionBoneIndex
            && Matrix4x4.Invert(player._boneComponentSpace[parentIndex], out Matrix4x4 inverseParent))
        {
            rootTransform *= inverseParent;
        }
        return Matrix4x4.Decompose(rootTransform, out _, out _, out Vector3 position)
            ? position
            : Vector3.Zero;
    }

    private static void BlendLocalTransform(ref Matrix4x4 currentTransform, Matrix4x4 nextTransform, float alpha)
    {
        if (Matrix4x4.Decompose(currentTransform, out _, out Quaternion currentRotation, out Vector3 currentPosition)
            && Matrix4x4.Decompose(nextTransform, out _, out Quaternion nextRotation, out Vector3 nextPosition))
        {
            Quaternion rotation = Quaternion.Slerp(currentRotation, nextRotation, alpha);
            Vector3 position = Vector3.Lerp(currentPosition, nextPosition, alpha);
            currentTransform = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
        }
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
