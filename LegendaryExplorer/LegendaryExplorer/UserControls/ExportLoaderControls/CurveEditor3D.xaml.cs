using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.Tools.PackageEditor.Experiments;
using LegendaryExplorer.Tools.InterpEditor;
using LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator;
using LegendaryExplorer.UserControls.Interfaces;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.SharpDX;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Animation;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Newtonsoft.Json;
using System.Windows.Threading;
using CameraOrigin = LegendaryExplorer.Tools.InterpEditor.CameraOrigin;
using InterpTrackMoveTransform = LegendaryExplorer.Tools.InterpEditor.InterpTrackMoveTransform;
using StageBoneOriginResolver = LegendaryExplorer.Tools.InterpEditor.StageBoneOriginResolver;
using StageCameraDefinition = LegendaryExplorer.Tools.InterpEditor.StageCameraDefinition;
using StageConversationContext = LegendaryExplorer.Tools.InterpEditor.StageConversationContext;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;
using InterpCurveFloat = LegendaryExplorerCore.Unreal.BinaryConverters.InterpCurve<float>;
using Key = System.Windows.Input.Key;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public sealed partial class CurveEditor3D : ExportLoaderControl, IActorEditorContext, ISceneRenderContextConfigurable,
    IWeakPackageUser
{
    private enum DialoguePreviewAudioGender
    {
        Male,
        Female
    }

    public enum DialoguePreviewPlayerGender
    {
        Female,
        Male
    }

    public sealed record DialogueNodePreviewActor(string ActorTag, CameraOrigin Origin,
        IReadOnlyList<string> Aliases = null);
    internal sealed record DialoguePreviewActorIdentity(string ActorTag, IReadOnlyList<string> Aliases);
    public sealed record DialoguePreviewRecentLevelSet(string DisplayName, IReadOnlyList<string> FilePaths);
    public sealed record DialoguePreviewPlayerSelection(
        DialoguePreviewPlayerGender Gender,
        string AssetName,
        bool UseFemaleLines,
        string BodyModelName,
        string HeadModelName,
        string HairModelName)
    {
        public static DialoguePreviewPlayerSelection Female { get; } = new(
            DialoguePreviewPlayerGender.Female,
            "SFX_HumanFemale_FaceFX",
            true,
            "HMF_ARM_CTHb_MDL",
            "HMF_HED_PROShepard_MDL",
            "HMF_HIR_PROShepard_MDL");

        public static DialoguePreviewPlayerSelection Male { get; } = new(
            DialoguePreviewPlayerGender.Male,
            "SFX_HumanMale_FaceFX",
            false,
            "HMM_ARM_CTHb_MDL",
            "HMM_HED_PROSheppard_MDL",
            null);

        public static DialoguePreviewPlayerSelection ForGender(DialoguePreviewPlayerGender gender) =>
            gender == DialoguePreviewPlayerGender.Male ? Male : Female;
    }
    public sealed record DialogueNodeReference(bool IsReply, int Index);

    private sealed record DialogueNodePreviewConfiguration(
        ConversationExtended Conversation,
        DialogueNodeExtended Node,
        IReadOnlyList<DialogueNodePreviewActor> Actors,
        IReadOnlyList<string> LevelPaths,
        StageConversationContext StageContext,
        DialoguePreviewPlayerSelection PlayerSelection,
        DialogueCachePreset CachePreset,
        string NewCacheLabel,
        float VoStartTime);

    public sealed class DialogueTimelineSegment : NotifyPropertyChangedBase
    {
        private float startTime;
        private bool isOnActivePath;
        private bool isVisited;
        private bool isAwaitingBranchChoice;
        private bool isAvailableBranch;
        private IReadOnlyList<DialogueBranchOption> branchOptions = [];

        public DialogueNodeExtended Node { get; init; }
        public DialogueNodeReference Reference { get; init; }
        public float StartTime
        {
            get => startTime;
            set
            {
                if (SetProperty(ref startTime, value))
                {
                    OnPropertyChanged(nameof(EndTime));
                }
            }
        }
        public float Duration { get; init; }
        public float EndTime => StartTime + Duration;
        public string NodeLabel => $"{(Node.IsReply ? "R" : "E")}{Node.NodeCount}";
        public string LineLabel => string.IsNullOrWhiteSpace(Node.Line) ? $"StrRef {Node.LineStrRef}" : Node.Line;
        public string DisplayLabel => $"{NodeLabel}  {LineLabel}";
        public string SpeakerLabel => Node.SpeakerTag?.SpeakerName ?? "None";
        public string ListenerLabel { get; init; }
        public string StrRefLabel => Node.LineStrRef > 0 ? Node.LineStrRef.ToString(CultureInfo.InvariantCulture) : "No TLK";
        public string DurationLabel => $"{Duration:0.00}s";
        public IReadOnlyList<DialogueBranchOption> BranchOptions
        {
            get => branchOptions;
            set
            {
                if (SetProperty(ref branchOptions, value ?? []))
                {
                    OnPropertyChanged(nameof(HasBranches));
                }
            }
        }
        public bool HasBranches => BranchOptions.Count > 1;
        public DialogueTimelineSegment Parent { get; set; }
        public DialogueBranchOption IncomingBranch { get; set; }
        public int TreeDepth { get; set; }
        public double TreeLeft { get; set; }
        public double TreeTop { get; set; }
        public bool IsOnActivePath
        {
            get => isOnActivePath;
            set => SetProperty(ref isOnActivePath, value);
        }
        public bool IsVisited
        {
            get => isVisited;
            set => SetProperty(ref isVisited, value);
        }
        public bool IsAwaitingBranchChoice
        {
            get => isAwaitingBranchChoice;
            set => SetProperty(ref isAwaitingBranchChoice, value);
        }
        public bool IsAvailableBranch
        {
            get => isAvailableBranch;
            set => SetProperty(ref isAvailableBranch, value);
        }
    }

    private sealed class DialogueSegmentRuntime
    {
        public DialogueTimelineSegment Segment { get; init; }
        public TrackMovePlaybackOption PrimaryTrackMove { get; init; }
        public IReadOnlyList<TrackMovePlaybackOption> TrackMoves { get; init; } = [];
        public IReadOnlyList<TrackMovePlaybackOption> ExtraTrackMoves { get; init; } = [];
        public IReadOnlyList<DirectorPlaybackOption> DirectorTracks { get; init; } = [];
        public IReadOnlyList<TrackMovePlaybackOption> CameraTracks { get; init; } = [];
        public IReadOnlyList<GestureTrackOption> GestureTracks { get; init; } = [];
        public Dictionary<string, TrackMovePlaybackOption> ActorTrackAssignments { get; init; }
            = new Dictionary<string, TrackMovePlaybackOption>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, GestureTrackOption> ActorGestureAssignments { get; init; }
            = new Dictionary<string, GestureTrackOption>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<ActorDirectionTrack> DirectionTracks { get; init; } = [];
        public IReadOnlyList<FaceOnlyVoEvent> FaceOnlyVoEvents { get; init; } = [];
        public ExportEntry DialogueAudio { get; init; }
        public DialogueFaceFxBinding MainFaceFx { get; init; }
        public IReadOnlyDictionary<FaceOnlyVoEvent, DialogueFaceFxBinding> FaceOnlyVoFaceFx { get; init; }
            = new Dictionary<FaceOnlyVoEvent, DialogueFaceFxBinding>();
        public Dictionary<string, CameraOrigin> StartActorOrigins { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CameraOrigin> EndActorOrigins { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CameraOrigin> ActorOriginOverrides { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> StartLookAtTargets { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> EndLookAtTargets { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CameraOrigin> StartCameraOrigins { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CameraOrigin> EndCameraOrigins { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, float> StartCameraFovs { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, float> EndCameraFovs { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Matrix4x4[]> StartActorGesturePoses { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Matrix4x4[]> EndActorGesturePoses { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DialogueGesturePoseState> StartActorGestureStates { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DialogueGesturePoseState> EndActorGestureStates { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public bool HasPendingPreviewChanges { get; set; }
        public bool HasPendingPackageChanges { get; set; }

        public bool HasPendingChanges => HasPendingPreviewChanges
                                         || HasPendingPackageChanges
                                         || TrackMoves.Any(option => option.Model?.HasPendingChanges == true)
                                         || TrackMoves.Select(option => option.FovModel).Where(model => model is not null)
                                             .Distinct().Any(model => model.HasPendingChanges);
    }

    private sealed record DialogueGesturePoseState(ExportEntry Animation, float AnimationTime);

    public sealed class DialogueBranchOption : NotifyPropertyChangedBase
    {
        private bool isSelected;

        public DialogueNodeExtended Source { get; init; }
        public DialogueNodeExtended Target { get; init; }
        public DialogueNodeReference TargetReference { get; init; }
        public string BranchKey { get; init; }
        public string Category { get; init; }
        public bool IsSelected
        {
            get => isSelected;
            set => SetProperty(ref isSelected, value);
        }
        public DialogueTimelineSegment SourceSegment { get; set; }
        public DialogueTimelineSegment TargetSegment { get; set; }
        public string NodeLabel => $"{(Target.IsReply ? "R" : "E")}{Target.NodeCount}";
        public string LineLabel => string.IsNullOrWhiteSpace(Target.Line) ? $"StrRef {Target.LineStrRef}" : Target.Line;
        public string DisplayLabel => string.IsNullOrWhiteSpace(Category)
            ? $"{NodeLabel}: {LineLabel}"
            : $"{Category} — {NodeLabel}: {LineLabel}";
    }

    public sealed class DialogueTimelineEdge : NotifyPropertyChangedBase
    {
        private bool isOnActivePath;

        public DialogueTimelineSegment Source { get; init; }
        public DialogueTimelineSegment Target { get; init; }
        public DialogueBranchOption Branch { get; init; }
        public double X1 => Source.TreeLeft + 230;
        public double Y1 => Source.TreeTop + 41;
        public double X2 => Target.TreeLeft;
        public double Y2 => Target.TreeTop + 41;
        public bool IsOnActivePath
        {
            get => isOnActivePath;
            set => SetProperty(ref isOnActivePath, value);
        }
    }

    private sealed class PreviewActorWidgetTarget : ITransformWidgetTarget
    {
        private Vector3 location;
        private Rotator rotation;

        public Action<CameraOrigin> TransformChanged { get; set; }
        public Vector3 Location
        {
            get => location;
            set
            {
                location = value;
                NotifyTransformChanged();
            }
        }

        public Rotator Rotation
        {
            get => rotation;
            set
            {
                rotation = value;
                NotifyTransformChanged();
            }
        }

        public float DrawScale { get; set; } = 1;
        public Vector3 DrawScale3D { get; set; } = Vector3.One;
        public bool IsReadOnly => false;
        public Matrix4x4 LocalToWorld => ActorUtils.ComposeLocalToWorld(Location, Rotation, Vector3.One);
        public TransformSnapshot SnapshotTransform() => new(Location, Rotation, DrawScale, DrawScale3D);

        public void SetTransform(CameraOrigin origin)
        {
            location = origin.Location;
            rotation = Rotator.FromDegreesVector(origin.Rotation);
        }

        private void NotifyTransformChanged()
        {
            TransformChanged?.Invoke(new CameraOrigin(location, rotation.GetDegreesVector()));
        }
    }

    private sealed record ActorDirectionKey(
        float Time,
        bool Enabled,
        string TargetActorTag,
        string TargetStageNode,
        float OrientationOffset);

    private sealed record ActorDirectionTrack(
        PreviewActorConfiguration Actor,
        bool IsLookAt,
        IReadOnlyList<ActorDirectionKey> Keys);

    private sealed record FaceOnlyVoEvent(
        float StartTime,
        ExportEntry Track,
        ExportEntry Group,
        DialogueNodeExtended Node,
        PreviewActorConfiguration Actor);

    private sealed record DialogueFaceFxBinding(
        PreviewActorConfiguration Actor,
        FaceFXAsset Asset,
        FaceFXAnimSet AnimSet,
        FaceFXLine Line,
        float TimelineOffset);

    private sealed record TaggedDialoguePreviewActor(ExportEntry Actor, TagUsage Usage);

    private sealed class ActorModelSet : IDisposable
    {
        public sealed class Component : IDisposable
        {
            public PreviewActorModelComponent Kind { get; init; }
            public ModelPreview<LEVertex> Model { get; init; }
            public SkinnedMeshRenderer Renderer { get; init; }
            public Matrix4x4? LocalTransform { get; init; }
            public void Dispose() => Model?.Dispose();
        }

        private readonly Dictionary<string, Component> components = new(StringComparer.OrdinalIgnoreCase);
        public ModelPreview<LEVertex> Body => components.Values
            .FirstOrDefault(component => component.Kind == PreviewActorModelComponent.Body)?.Model;
        public IEnumerable<Component> Components => components.Values;

        public void Set(PreviewActorModelComponent component, ModelPreview<LEVertex> model,
            SkinnedMeshRenderer renderer, string slotName = null, Matrix4x4? localTransform = null)
        {
            string key = string.IsNullOrWhiteSpace(slotName) ? component.ToString() : slotName;
            if (components.Remove(key, out Component previous)) previous.Dispose();
            components[key] = new Component
            {
                Kind = component,
                Model = model,
                Renderer = renderer,
                LocalTransform = localTransform,
            };
        }

        public void Remove(PreviewActorModelComponent component)
        {
            foreach (string key in components.Where(pair => pair.Value.Kind == component)
                         .Select(pair => pair.Key).ToArray())
            {
                if (components.Remove(key, out Component previous)) previous.Dispose();
            }
        }

        public void Dispose()
        {
            foreach (Component component in components.Values) component.Dispose();
            components.Clear();
        }
    }

    private sealed class GestureTrackOption
    {
        public static readonly GestureTrackOption None = new() { DisplayName = "None" };

        public string DisplayName { get; init; }
        public ExportEntry Group { get; init; }
        public ExportEntry Track { get; init; }
        public IReadOnlyList<GesturePreviewExportLoader.GestureAnimationItem> Animations { get; init; } = [];
        public GesturePreviewExportLoader.GestureAnimationItem StartingPose { get; init; }
        public IReadOnlyList<AnimationPreviewControl.AnimationTimelineClip> Timeline { get; init; } = [];
        public string Status { get; init; }
        public bool HasResolvedTimeline => Timeline.Count > 0;
    }

    internal static (float Start, float End) ResolveStartingPoseTimelineRange(
        IReadOnlyList<AnimationPreviewControl.AnimationTimelineClip> timelineClips,
        float? playbackDuration, float animationStart, float animationSequenceLength)
    {
        float start = timelineClips.Count > 0
            ? Math.Min(0, timelineClips.Min(clip => clip.StartTime))
            : 0;
        float end = playbackDuration is > 0
            ? playbackDuration.Value
            : timelineClips.Count > 0
                ? timelineClips.Max(clip => clip.EndTime)
                : start + Math.Max(0, animationSequenceLength - animationStart);
        return (start, end);
    }

    private sealed class PreviewActorAnimationState
    {
        public sealed class LayeredAnimationPlayer : AnimPlayer
        {
            private Matrix4x4[] heldGestureComponentPose;
            private readonly int lookAtBoneIndex;

            public AnimSequencePlayer GesturePlayer { get; }
            public FaceFxPlayer FaceFxPlayer { get; }
            public Vector3? LookAtTargetComponent { get; private set; }
            public bool HasLayeredAnimation => GesturePlayer.HasAnimation
                                               || heldGestureComponentPose is not null
                                               || FaceFxPlayer.HasAnimation;
            public bool HasLookAtTarget => lookAtBoneIndex >= 0 && LookAtTargetComponent.HasValue;
            public float GestureTimelineOffset { get; set; }
            public bool LoopStandaloneGesture { get; set; }
            public bool HoldGesturePose { get; set; }
            public float FaceFxTimelineOffset { get; set; }
            public override bool HasAnimation => HasLayeredAnimation || HasLookAtTarget;
            public override float Duration => Math.Max(GesturePlayer.Duration, FaceFxPlayer.Duration);
            public override float StartTime => Math.Min(GesturePlayer.StartTime, FaceFxTimelineOffset);
            public override float EndTime => Math.Max(GesturePlayer.EndTime, FaceFxTimelineOffset + FaceFxPlayer.Duration);

            public LayeredAnimationPlayer(SkeletalMesh skeletalMesh) : base(skeletalMesh)
            {
                GesturePlayer = new AnimSequencePlayer(skeletalMesh);
                FaceFxPlayer = new FaceFxPlayer(skeletalMesh);
                lookAtBoneIndex = FindLookAtBoneIndex(_bones);
            }

            public override void SetCurrentTime(float time)
            {
                CurrentTime = time;
                if (!HoldGesturePose)
                {
                    float gestureTime = time + GestureTimelineOffset;
                    if (LoopStandaloneGesture && GesturePlayer.Duration > 0)
                    {
                        gestureTime %= GesturePlayer.Duration;
                    }
                    GesturePlayer.SetCurrentTime(gestureTime);
                }
                FaceFxPlayer.SetCurrentTime(FaceFxPlayer.StartTime + time - FaceFxTimelineOffset);
            }

            public override Matrix4x4[] ComputeSkinningMatrices()
            {
                bool hasGesture = GesturePlayer.HasAnimation;
                bool hasHeldGesture = !hasGesture && heldGestureComponentPose is not null;
                bool hasFaceFx = FaceFxPlayer.HasAnimation;
                if (hasGesture)
                {
                    GesturePlayer.ComputeSkinningMatrices();
                }
                if (hasFaceFx)
                {
                    FaceFxPlayer.ComputeSkinningMatrices();
                }

                for (int index = 0; index < _skinningMatrices.Length; index++)
                {
                    MeshBone bone = _bones[index];
                    Matrix4x4 bindLocal = Matrix4x4.CreateFromQuaternion(bone.Orientation)
                                          * Matrix4x4.CreateTranslation(bone.Position);
                    Matrix4x4 bodyLocal = hasGesture
                        ? GetLocalTransform(GesturePlayer.BoneComponentSpaceTransforms, index)
                        : hasHeldGesture
                            ? GetLocalTransform(heldGestureComponentPose, index)
                            : bindLocal;
                    Matrix4x4 finalLocal = bodyLocal;
                    if (hasFaceFx)
                    {
                        Matrix4x4 faceLocal = GetLocalTransform(FaceFxPlayer.BoneComponentSpaceTransforms, index);
                        if (Matrix4x4.Invert(bindLocal, out Matrix4x4 inverseBindLocal))
                        {
                            finalLocal *= inverseBindLocal * faceLocal;
                        }
                    }

                    Matrix4x4 componentTransform = bone.ParentIndex >= 0 && bone.ParentIndex < index
                        ? finalLocal * _boneComponentSpace[bone.ParentIndex]
                        : finalLocal;
                    if (index == lookAtBoneIndex && LookAtTargetComponent is { } lookAtTarget)
                    {
                        componentTransform = ApplyLookAtBoneRotation(componentTransform, lookAtTarget);
                    }
                    _boneComponentSpace[index] = componentTransform;
                    _skinningMatrices[index] = _inverseBindPose[index] * _boneComponentSpace[index];
                }
                return _skinningMatrices;
            }

            public void SetLookAtTargetComponent(Vector3? target)
            {
                LookAtTargetComponent = target;
            }

            public Vector3? GetLookAtAnchorComponent()
            {
                if (lookAtBoneIndex < 0)
                {
                    return null;
                }
                ComputeSkinningMatrices();
                return _boneComponentSpace[lookAtBoneIndex].Translation;
            }

            private static int FindLookAtBoneIndex(IReadOnlyList<MeshBone> bones)
            {
                string[] preferredNames = ["Head", "Bip01_Head", "b_head"];
                foreach (string preferredName in preferredNames)
                {
                    for (int index = 0; index < bones.Count; index++)
                    {
                        if (bones[index].Name.Instanced.Equals(preferredName, StringComparison.OrdinalIgnoreCase))
                        {
                            return index;
                        }
                    }
                }
                for (int index = 0; index < bones.Count; index++)
                {
                    if (bones[index].Name.Instanced.Contains("head", StringComparison.OrdinalIgnoreCase))
                    {
                        return index;
                    }
                }
                return -1;
            }

            public Matrix4x4[] CaptureGesturePose()
            {
                if (GesturePlayer.HasAnimation)
                {
                    GesturePlayer.ComputeSkinningMatrices();
                    return (Matrix4x4[])GesturePlayer.BoneComponentSpaceTransforms.Clone();
                }
                return heldGestureComponentPose is null
                    ? null
                    : (Matrix4x4[])heldGestureComponentPose.Clone();
            }

            public void SetHeldGesturePose(Matrix4x4[] componentPose)
            {
                GesturePlayer.SetAnimation(null);
                heldGestureComponentPose = componentPose is null || componentPose.Length != _bones.Length
                    ? null
                    : (Matrix4x4[])componentPose.Clone();
                HoldGesturePose = heldGestureComponentPose is not null;
            }

            public void ClearHeldGesturePose()
            {
                heldGestureComponentPose = null;
            }

            private Matrix4x4 GetLocalTransform(Matrix4x4[] componentPose, int boneIndex)
            {
                int parentIndex = _bones[boneIndex].ParentIndex;
                if (parentIndex >= 0 && parentIndex < boneIndex
                    && Matrix4x4.Invert(componentPose[parentIndex], out Matrix4x4 inverseParent))
                {
                    return componentPose[boneIndex] * inverseParent;
                }
                return componentPose[boneIndex];
            }
        }

        public SkeletalMesh SkeletalMesh { get; init; }
        public SkinnedMeshRenderer Renderer { get; init; }
        public LayeredAnimationPlayer Player { get; init; }
        public GestureTrackOption AppliedGesture { get; private set; }
        public bool HasTimeline => Player?.HasLayeredAnimation == true;
        public bool HasGestureTimeline => Player?.GesturePlayer?.HasAnimation == true;
        public bool HasLookAtTarget => Player?.HasLookAtTarget == true;

        public Vector3 EvaluateExtractedRootMotion(float time)
        {
            SetTime(time);
            Player.GesturePlayer.ComputeSkinningMatrices();
            return Player.GesturePlayer.ExtractedRootMotionTranslation;
        }

        public Vector3 EvaluateExtractedRootMotionDelta(float startTime, float endTime)
        {
            if (endTime <= startTime)
            {
                // The TrackMove still owns translation, but the gesture pose must continue to
                // evaluate while the actor traverses the spline.
                EvaluateExtractedRootMotion(endTime);
                return Vector3.Zero;
            }

            Vector3 start = EvaluateExtractedRootMotion(startTime);
            Vector3 end = EvaluateExtractedRootMotion(endTime);
            return end - start;
        }

        public void SetTimeline(GesturePreviewExportLoader.GestureAnimationItem startingPose,
            IEnumerable<AnimationPreviewControl.AnimationTimelineClip> timeline, PackageCache packageCache,
            float? playbackDuration = null, GestureTrackOption gesture = null,
            bool maskDialogueOverlayStaticBones = false, bool extractRootTranslation = false,
            float? startingPoseTimeOverride = null)
        {
            AppliedGesture = gesture;
            List<AnimationPreviewControl.AnimationTimelineClip> timelineClips = timeline.ToList();
            Player.ClearHeldGesturePose();
            Player.GesturePlayer.SetAnimation(null);
            Player.GestureTimelineOffset = 0;
            Player.LoopStandaloneGesture = false;
            Player.HoldGesturePose = false;
            AnimSequence startingPoseAnimation = null;
            if (startingPose?.AnimationExport is not null)
            {
                startingPoseAnimation = ObjectBinary.From<AnimSequence>(startingPose.AnimationExport);
                startingPoseAnimation.DecompressAnimationData();
                float startingPoseTime = Math.Clamp(startingPoseTimeOverride ?? startingPose.Settings.StartOffset,
                    0, startingPoseAnimation.SequenceLength);
                Player.GesturePlayer.SetAnimation(startingPoseAnimation, packageCache);
                Player.GesturePlayer.SetCurrentTime(startingPoseTime);
                Player.GesturePlayer.ComputeSkinningMatrices();
                if (timelineClips.Count == 0 && !maskDialogueOverlayStaticBones)
                {
                    Player.GestureTimelineOffset = startingPoseTime;
                    Player.LoopStandaloneGesture = true;
                }
            }
            List<AnimSequencePlayer.ScheduledAnimationClip> scheduledClips = [];
            if (startingPoseAnimation is not null
                && (timelineClips.Count > 0 || maskDialogueOverlayStaticBones))
            {
                float animationStart = Math.Clamp(startingPoseTimeOverride ?? startingPose.Settings.StartOffset,
                    0, startingPoseAnimation.SequenceLength);
                (float baseStart, float baseEnd) = ResolveStartingPoseTimelineRange(timelineClips,
                    playbackDuration, animationStart, startingPoseAnimation.SequenceLength);
                if (baseEnd > baseStart && startingPoseAnimation.SequenceLength > animationStart)
                {
                    scheduledClips.Add(new AnimSequencePlayer.ScheduledAnimationClip
                    {
                        Animation = startingPoseAnimation,
                        StartTime = baseStart,
                        EndTime = baseEnd,
                        AnimationStartTime = animationStart,
                        AnimationEndTime = startingPoseAnimation.SequenceLength,
                        Weight = 1,
                        Loop = true,
                        IsBaseLayer = true,
                        NormalizeRootTranslation = extractRootTranslation,
                    });
                }
            }
            foreach (AnimationPreviewControl.AnimationTimelineClip clip in timelineClips)
            {
                if (clip.AnimationExport is null)
                {
                    continue;
                }

                var animation = ObjectBinary.From<AnimSequence>(clip.AnimationExport);
                animation.DecompressAnimationData();
                scheduledClips.Add(new AnimSequencePlayer.ScheduledAnimationClip
                {
                    Animation = animation,
                    StartTime = clip.StartTime,
                    EndTime = clip.EndTime,
                    AnimationStartTime = clip.AnimationStartTime,
                    AnimationEndTime = clip.AnimationEndTime,
                    PlayRate = clip.PlayRate,
                    BlendInDuration = clip.BlendInDuration,
                    BlendOutDuration = clip.BlendOutDuration,
                    Weight = clip.Weight,
                    Loop = clip.Loop,
                    IsBaseLayer = clip.IsBaseLayer,
                    // Matinee locomotion is supplied by the TrackGesture starting pose while
                    // BioGestureData entries layer head/body gestures over it. Head gestures still
                    // contain constant tracks for the rest of the skeleton; treating those as a
                    // full-body overlay replaces the walk cycle and makes the actor slide. Apply
                    // the motion mask only in whole-conversation playback so the standalone
                    // Gesture Preview and regular 3D editor continue showing complete clips.
                    // Transition clips are full-body bridges into a new pose. Masking their
                    // constant tracks lets the destination pose leak through while the bridge is
                    // still playing, so only ordinary gesture overlays receive the motion mask.
                    UseMotionBoneMask = clip.UseMotionBoneMask
                                        || maskDialogueOverlayStaticBones
                                        && !clip.IsBaseLayer
                                        && !clip.IsTransition,
                    NormalizeRootTranslation = extractRootTranslation,
                });
            }

            if (scheduledClips.Count > 0)
            {
                Player.GesturePlayer.SetAnimationTimeline(scheduledClips, packageCache);
            }
            Renderer.NeedsUpdate = true;
        }

        public void SetFaceFx(FaceFXAsset asset, FaceFXAnimSet animSet, FaceFXLine line, float timelineOffset)
        {
            Player.FaceFxPlayer.FxActor = asset;
            Player.FaceFxPlayer.AnimSet = animSet;
            Player.FaceFxPlayer.SetFaceFXLine(line);
            Player.FaceFxTimelineOffset = timelineOffset;
            Renderer.NeedsUpdate = true;
        }

        public void AttachFaceFx(FaceFXAsset asset)
        {
            Player.FaceFxPlayer.SetFaceFXLine(null);
            Player.FaceFxPlayer.AnimSet = null;
            Player.FaceFxPlayer.FxActor = asset;
            Player.FaceFxTimelineOffset = 0;
            Renderer.NeedsUpdate = true;
        }

        public void ClearFaceFx()
        {
            Player.FaceFxPlayer.SetFaceFXLine(null);
            Player.FaceFxPlayer.AnimSet = null;
            Player.FaceFxPlayer.FxActor = null;
            Player.FaceFxTimelineOffset = 0;
            Renderer.NeedsUpdate = true;
        }

        public void SetTime(float time)
        {
            Player.SetCurrentTime(time);
            Renderer.NeedsUpdate = true;
        }

        public void Clear()
        {
            AppliedGesture = null;
            Player.GesturePlayer.SetAnimation(null);
            Player.ClearHeldGesturePose();
            Player.GestureTimelineOffset = 0;
            Player.LoopStandaloneGesture = false;
            Player.HoldGesturePose = false;
            Player.SetLookAtTargetComponent(null);
            Renderer.NeedsUpdate = true;
        }

        public void SetLookAtTargetWorld(Vector3? targetWorld, CameraOrigin actorOrigin)
        {
            Vector3? targetComponent = null;
            if (targetWorld is { } world
                && Matrix4x4.Invert(CreatePreviewActorTransform(actorOrigin), out Matrix4x4 worldToComponent))
            {
                targetComponent = Vector3.Transform(world, worldToComponent);
            }
            Player.SetLookAtTargetComponent(targetComponent);
            Renderer.NeedsUpdate = true;
        }

        public Vector3 GetLookAtAnchorWorld(CameraOrigin actorOrigin)
        {
            Vector3 componentAnchor = Player.GetLookAtAnchorComponent() ?? new Vector3(0, 0, 160);
            return Vector3.Transform(componentAnchor, CreatePreviewActorTransform(actorOrigin));
        }

        public Matrix4x4[] CaptureGesturePose() => Player.CaptureGesturePose();

        public void SetHeldGesturePose(Matrix4x4[] componentPose)
        {
            AppliedGesture = null;
            Player.SetHeldGesturePose(componentPose);
            Renderer.NeedsUpdate = true;
        }

        public void HoldCurrentGesturePose()
        {
            AppliedGesture = null;
            if (!Player.GesturePlayer.HasAnimation)
            {
                return;
            }
            Player.GesturePlayer.ComputeSkinningMatrices();
            Player.HoldGesturePose = true;
            Renderer.NeedsUpdate = true;
        }
    }

    private sealed class PreviewActorConfiguration
    {
        public string ActorTag { get; set; }
        public string DisplayName { get; set; }
        public bool BaseGameModelsOnly { get; set; }
        public string ModelName { get; set; }
        public string HeadModelName { get; set; }
        public string HairModelName { get; set; }
        public string FaceFxAssetName { get; set; }
        public DialogueActorConstructionCache Construction { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Roll { get; set; }
        public float Pitch { get; set; }
        public float Yaw { get; set; }

        public CameraOrigin Origin
        {
            get => new(new Vector3(X, Y, Z), new Vector3(Roll, Pitch, Yaw));
            set
            {
                X = value.Location.X;
                Y = value.Location.Y;
                Z = value.Location.Z;
                Roll = value.Rotation.X;
                Pitch = value.Rotation.Y;
                Yaw = value.Rotation.Z;
            }
        }
    }

    private sealed class TrackMovePlaybackOption
    {
        public static readonly TrackMovePlaybackOption None = new() { DisplayName = "None" };

        public string DisplayName { get; init; }
        public string TabDisplayName { get; set; }
        public ExportEntry Group { get; init; }
        public ExportEntry TrackMove { get; init; }
        public CurveEditor3DModel Model { get; init; }
        public CurveEditor3DFovModel FovModel { get; init; }
        public InterpCurveFloat FovTrack => FovModel?.Track;
        public bool DisableMovement => TrackMove?.GetProperty<BoolProperty>("bDisableMovement")?.Value == true;
        public bool UseQuaternionInterpolation => TrackMove?.GetProperty<BoolProperty>("bUseQuatInterpolation")?.Value == true;
        public bool UsesLegacyStuntActorLocation =>
            TrackMove?.GetProperty<BoolProperty>("SFXCreatedBeforeStuntActorLocationChange")?.Value == true;
        public EInterpTrackMoveRotMode RotationMode => TrackMove?.GetProperty<EnumProperty>("RotMode")
            .GetEnumValOrDefault(EInterpTrackMoveRotMode.IMR_Keyframed) ?? EInterpTrackMoveRotMode.IMR_Keyframed;
        public string LookAtGroupName => TrackMove?.GetProperty<NameProperty>("LookAtGroupName")?.Value.Instanced;
    }

    private sealed class PreviewActorPlaybackState
    {
        public PreviewActorConfiguration Actor { get; init; }
        public TrackMovePlaybackOption TrackMove { get; set; }
        public CameraOrigin OriginalOrigin { get; set; }
        public EInterpTrackMoveFrame MoveFrame { get; set; }
    }

    private sealed class DirectorPlaybackOption
    {
        public static readonly DirectorPlaybackOption None = new() { DisplayName = "None" };

        public string DisplayName { get; init; }
        public ExportEntry DirectorTrack { get; init; }
        public IReadOnlyList<DirectorCameraCut> Cuts { get; init; } = [];
    }

    private sealed class DirectorCameraCut
    {
        public float Time { get; init; }
        public string GroupName { get; init; }
        public TrackMovePlaybackOption Camera { get; init; }
        public ExportEntry SwitchCameraTrack { get; init; }
        public string CameraActorTag { get; init; }
        public ExportEntry CameraActor { get; set; }
        public CameraOrigin? FallbackOrigin { get; set; }
        public float? FallbackFovDegrees { get; set; }
    }

    private sealed record PlacedCameraState(ExportEntry Actor, CameraOrigin Origin, float? FovDegrees);
    private sealed record ResolvedSwitchCamera(CameraOrigin Origin, float FovDegrees);

    private const float PreviewBodyMeshRelativeZ = -88f;
    internal const float ConversationSwitchCameraFovDegrees = 52.9f;
    private static readonly RenderPass[] RenderPasses = [RenderPass.Base, RenderPass.Hair];
    private static readonly object sessionLevelPathsLock = new();
    private static readonly List<string> sessionLevelPaths = [];
    private static IMEPackage sessionSourcePackage;

    private readonly CurveEditor3DModel model = new();
    private readonly List<IMEPackage> levelPackages = [];
    private readonly List<ActorProxy> levelActors = [];
    private readonly List<string> levelPaths = [];
    private readonly ObservableCollection<PreviewActorConfiguration> previewActors = [];
    private readonly List<ActorModelSet> previewActorModels = [];
    private readonly PreviewActorWidgetTarget previewActorWidgetTarget = new();
    private readonly PackageCache previewActorGesturePackageCache = new();
    private IMEPackage dialoguePreviewFaceFxPackage;
    private StageConversationContext trackAnchorStageContext;
    private ConversationExtended trackAnchorConversation;
    private readonly Dictionary<PreviewActorConfiguration, FaceFXAnimSet> dialoguePreviewFaceFxAnimSets = [];
    private readonly ObservableCollection<GestureTrackOption> availableGestureTracks = [];
    private readonly Dictionary<PreviewActorConfiguration, GestureTrackOption> previewActorGestureAssignments = [];
    private readonly Dictionary<PreviewActorConfiguration, PreviewActorAnimationState> previewActorAnimationStates = [];
    private readonly Dictionary<string, HashSet<string>> dialoguePreviewActorTagAliases =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PreviewActorConfiguration, TrackMovePlaybackOption> previewActorTrackAssignments = [];
    private readonly List<TrackMovePlaybackOption> availableTrackMoves = [];
    private readonly ObservableCollection<TrackMovePlaybackOption> availableExtraTrackMoves = [];
    private readonly ObservableCollection<DirectorPlaybackOption> availableDirectorTracks = [];
    private readonly ObservableCollection<TrackMovePlaybackOption> characterTrackMoves = [];
    private readonly ObservableCollection<TrackMovePlaybackOption> cameraTrackMoves = [];
    private readonly HashSet<string> tracksWithVisibleKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TrackMovePlaybackOption> dialoguePreviewCameraActors = [];
    private readonly Dictionary<string, PlacedCameraState> dialoguePlacedCameras =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlacedCameraState> dialogueAuthoredCameraDefaults =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CameraOrigin> dialogueLookAtTargets =
        new(StringComparer.OrdinalIgnoreCase);
    private CameraOrigin dialoguePreviewInitialCameraOrigin;
    private float dialoguePreviewInitialCameraFovDegrees = 60f;
    private AssetDB previewAssetDatabase;
    private List<MeshRecord> previewActorMeshes = [];
    private List<(string FileName, string ContentDir)> previewAssetFiles = [];
    private Dictionary<int, string> previewAssetFilePaths = [];
    private PreviewActorConfiguration selectedPreviewActor;
    private MEGame previewActorGame = MEGame.Unknown;
    private bool updatingPreviewActorControls;
    private bool previewActorWidgetActive;
    private IReadOnlyList<Vector3> trajectorySamples = [];
    private bool eventsAttached;
    private bool hasSnappedInitialCamera;
    private bool isPlayingMove;
    private bool isPlayingActor;
    private bool isPlayingDialogueTimeline;
    private bool updatingDialogueTimelineSlider;
    private bool updatingDialogueTimelineSelection;
    private bool resumeDialogueTimelineAfterScrub;
    private bool loadingDialogueTimelineSegment;
    private bool buildingDialogueRuntimeCache;
    private bool suppressDialogueCacheEditTracking;
    private float dialogueTimelineCurrentTime;
    private DialogueTimelineSegment activeDialogueTimelineSegment;
    private DialogueSegmentRuntime activeDialogueSegmentRuntime;
    private bool updatingMulticamControls;
    private bool playExtraTrackMove;
    private bool playDirectorMulticam;
    private bool sessionLevelsRestored;
    private bool trajectorySamplesDirty;
    private Button playMoveButton;
    private Button playActorButton;
    private readonly List<PreviewActorPlaybackState> playbackActors = [];
    private readonly List<ActorDirectionTrack> actorDirectionTracks = [];
    private readonly List<FaceOnlyVoEvent> faceOnlyVoEvents = [];
    private readonly ObservableCollection<DialogueTimelineSegment> dialogueTimelineSegments = [];
    private readonly ObservableCollection<DialogueTimelineEdge> dialogueTimelineEdges = [];
    private readonly List<DialogueTimelineSegment> dialogueTimelineActivePath = [];
    private readonly Dictionary<DialogueNodeReference, DialogueTimelineSegment> dialogueTimelineSegmentsByReference = [];
    private readonly ObservableCollection<DialogueBranchOption> dialogueBranchOptions = [];
    private readonly Dictionary<string, DialogueNodeReference> dialogueBranchSelections = new(StringComparer.Ordinal);
    private readonly Dictionary<DialogueTimelineSegment, DialogueSegmentRuntime> dialogueRuntimeCache = [];
    private readonly Dictionary<DialogueNodeExtended, IReadOnlyList<ExportEntry>> dialogueNodeInterpDataCache = [];
    private IMEPackage dialoguePreviewWorkingPackage;
    private IMEPackage dialoguePreviewSourcePackage;
    private PackageEditorWindow dialoguePackageEditor;
    private int dialogueWorkingCommittedNameCount;
    private int dialogueWorkingCommittedImportCount;
    private int dialogueWorkingCommittedExportCount;
    private bool suppressDialoguePackageEditTracking;
    private DialogueCachePreset loadedDialogueCachePreset;
    private DialogueTimelineSegment dialogueTimelineStartSegment;
    private double dialogueTimelineTreeWidth = 230;
    private double dialogueTimelineTreeHeight = 82;
    private CurveEditor3DKeyframe selectedKeyframe;
    private CurveEditor3DFovKeyframe selectedFovKeyframe;
    private string currentExportName;
    private string sceneStatus = "Select an InterpTrackMove export, then optionally open a level backdrop.";
    private string playbackKeyframeStatus = "Not playing";
    private float playbackStartTime;
    private float playbackEndTime;
    private float playbackElapsed;
    private float playbackCurrentTime;
    private bool dialoguePreviewAudioStarted;
    private bool faceOnlyVoAudioStarted;
    private FaceOnlyVoEvent activeFaceOnlyVoEvent;
    private TrackMovePlaybackOption selectedExtraTrackMove;
    private DirectorPlaybackOption selectedDirectorPlayback;
    private TrackMovePlaybackOption primaryTrackMove;
    private DialogueNodePreviewConfiguration dialogueNodePreview;
    private bool isDialogueConversationPreview;
    private CurveEditor3DModel registeredKeyframeModel;
    private CurveEditor3DFovModel registeredFovModel;
    private bool updatingKeyframeTrackTabs;
    private bool updatingTrackKeyVisibilityControls;
    private Vector3 pendingViewportKeyframeLocation;
    private Vector3 pendingViewportSelectedKeyframeLocation;
    private bool showCollision = Settings.LevelEditor_ShowCollision;
    private bool showLightIcons;
    private bool showFovIcons = true;
    private bool cameraFramingMode;
    private bool suppressTrackVisualizationForCameraPreview;
    private bool showVolumes = Settings.LevelEditor_ShowVolumes;
    private bool showVolumetrics;
    private bool unlit = Settings.LevelEditor_Unlit;
    private bool setAlphaToBlack = true;
    private bool showRedChannel = true;
    private bool showGreenChannel = true;
    private bool showBlueChannel = true;
    private bool showAlphaChannel = true;
    private System.Windows.Media.Color backgroundColor;
    private string cameraPositionX = "0";
    private string cameraPositionY = "0";
    private string cameraPositionZ = "0";
    private string cameraRotationX = "0";
    private string cameraRotationY = "0";
    private string cameraRotationZ = "0";
    private float cameraPositionStep = 10f;
    private float cameraRotationStep = 5f;
    private bool updatingCameraPositionText;
    private int cameraPositionEditorsFocused;
    private bool updatingCameraRotationText;
    private int cameraRotationEditorsFocused;
    private string selectedKeyframeInVal;
    private string selectedFovKeyframeInVal;
    private string locationScrubAxes = "X";
    private double locationScrubDragAccumulator;
    private double locationScrubPreviousHorizontalChange;
    private string fovScrubProperty = nameof(CurveEditor3DFovKeyframe.Value);
    private double fovScrubDragAccumulator;
    private double fovScrubPreviousHorizontalChange;
    private string rotationDialAxis = nameof(CurveEditor3DKeyframe.Pitch);
    private bool rotationDialDragging;
    private double rotationDialAngleAccumulator;
    private double rotationDialPreviousAngle;
    private string previewActorLocationScrubAxes = "X";
    private double previewActorLocationScrubAccumulator;
    private double previewActorLocationScrubPreviousHorizontalChange;
    private string previewActorRotationDialAxes = "Roll";
    private bool previewActorRotationDialDragging;
    private double previewActorRotationDialAngleAccumulator;
    private double previewActorRotationDialPreviousAngle;

    public IEnumerable<DialogueTimelineSegment> DialogueTimelineSegments => dialogueTimelineSegments;
    public IEnumerable<DialogueTimelineEdge> DialogueTimelineEdges => dialogueTimelineEdges;
    public IEnumerable<DialogueBranchOption> DialogueBranchOptions => dialogueBranchOptions;
    public double DialogueTimelineTreeWidth
    {
        get => dialogueTimelineTreeWidth;
        private set => SetProperty(ref dialogueTimelineTreeWidth, value);
    }
    public double DialogueTimelineTreeHeight
    {
        get => dialogueTimelineTreeHeight;
        private set => SetProperty(ref dialogueTimelineTreeHeight, value);
    }

    private string SavedPreviewActorsPath => Path.Combine(AppDirectories.AppDataFolder,
        $"CurveEditor3DPreviewActors_{previewActorGame}.json");

    private CurveEditor3DModel ActiveModel => ActiveTrackMoveOption?.Model ?? model;

    private TrackMovePlaybackOption ActiveTrackMoveOption => SelectedPreviewEditorCategory switch
    {
        "Cameras" => CameraTrackMoveTabs?.SelectedItem as TrackMovePlaybackOption,
        "Characters" => CharacterTrackMoveTabs?.SelectedItem as TrackMovePlaybackOption,
        _ => null,
    };

    private string SelectedPreviewEditorCategory =>
        (PreviewEditorCategoryTabs?.SelectedItem as TabItem)?.Tag as string ?? "Actors";

    private bool IsTrackEditorCategorySelected => SelectedPreviewEditorCategory is "Characters" or "Cameras";

    private bool AreActiveTrackKeysVisible => IsTrackEditorCategorySelected
        && ActiveTrackMoveOption?.TrackMove is { } trackMove
        && tracksWithVisibleKeys.Contains(GetTrackMoveEditingKey(trackMove));

    private CurveEditor3DFovModel ActiveFovModel => ActiveTrackMoveOption?.FovModel;

    private ExportEntry ActiveTrackMoveExport => ActiveModel.Export ?? CurrentLoadedExport;

    private CameraOrigin? ActiveTrackCoordinateBasis =>
        GetTrackMoveFrame(ActiveTrackMoveExport) == EInterpTrackMoveFrame.IMF_AnchorObject
        && TrackAnchorOrigin is { } anchor
            ? anchor
            : null;

    public CurveEditor3D() : base("3D Curve Editor")
    {
        RenderContext = new LevelEditorRenderContext
        {
            ConstrainedAspectRatio = 16f / 9f,
            UseGameShaderMeshPreviews = true,
            UseGameShaderStaticMeshPreviews = false,
        };
        backgroundColor = LevelEditor.GetThemeDefaultBackgroundColor();
        RenderContext.BackgroundColor = backgroundColor;
        RenderContext.ShowLightIcons = showLightIcons;
        RenderContext.ShowEmitterIcons = false;
        RenderContext.ShowPointsOfInterest = false;
        RenderContext.SetShowEmitterVfx(false);
        if (unlit)
        {
            RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.Unlit;
        }
        InterpModes = Enum.GetValues<EInterpCurveMode>();
        LoadCommands();
        InitializeComponent();
        // Match Level Editor input scheduling so background resource preparation yields during interaction.
        PreviewMouseMove += (_, _) => RenderContext.NotifyUserActivity();
        PreviewMouseDown += (_, _) => RenderContext.NotifyUserActivity();
        PreviewMouseWheel += (_, _) => RenderContext.NotifyUserActivity();
        PreviewKeyDown += (_, _) => RenderContext.NotifyUserActivity();
        PreviewActorListBox.ItemsSource = previewActors;
        DialoguePreviewActorListBox.ItemsSource = previewActors;
        PreviewActorGestureComboBox.ItemsSource = availableGestureTracks;
        ExtraTrackMoveComboBox.ItemsSource = availableExtraTrackMoves;
        DirectorTrackComboBox.ItemsSource = availableDirectorTracks;
        CharacterTrackMoveTabs.ItemsSource = characterTrackMoves;
        CameraTrackMoveTabs.ItemsSource = cameraTrackMoves;
        ConfigureKeyframeContextMenu();
        SceneViewer.Context = RenderContext;
        RenderContext.EnableTransformWidget();
        previewActorWidgetTarget.TransformChanged = PreviewActorGizmo_TransformChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;
        model.Changed += Model_Changed;
        RenderContext.UpdateScene += UpdatePlayback;
    }

    public LevelEditorRenderContext RenderContext { get; }

    public static ExportEntry FindDialoguePreviewTrackMove(ExportEntry interpData)
    {
        if (interpData is null)
        {
            return null;
        }

        List<ExportEntry> groups = GetReferencedExports(interpData, "InterpGroups").ToList();
        ExportEntry directorTrack = FindDirectorTracks(interpData).FirstOrDefault();
        if (directorTrack is not null)
        {
            HashSet<string> directedGroups = directorTrack
                .GetProperty<ArrayProperty<StructProperty>>("CutTrack")?
                .Select(cut => cut.GetProp<NameProperty>("TargetCamGroup")?.Value.Instanced)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            ExportEntry directedCamera = groups
                .Where(group => directedGroups.Contains(GetInterpGroupName(group)))
                .SelectMany(group => GetReferencedExports(group, "InterpTracks"))
                .FirstOrDefault(track => track.ClassName == "InterpTrackMove");
            if (directedCamera is not null)
            {
                return directedCamera;
            }
        }

        ExportEntry fallbackCamera = groups
            .Where(group => GetInterpGroupName(group).StartsWith("Cam", StringComparison.OrdinalIgnoreCase))
            .SelectMany(group => GetReferencedExports(group, "InterpTracks"))
            .FirstOrDefault(track => track.ClassName == "InterpTrackMove");
        return fallbackCamera ?? groups.SelectMany(group => GetReferencedExports(group, "InterpTracks"))
            .FirstOrDefault(track => track.ClassName == "InterpTrackMove");
    }

    internal void ConfigureDialogueNodePreview(ConversationExtended conversation, DialogueNodeExtended node,
        IReadOnlyList<DialogueNodePreviewActor> actors, IReadOnlyList<string> levelPaths,
        StageConversationContext stageContext, DialoguePreviewPlayerSelection playerSelection) =>
        ConfigureDialoguePreview(conversation, node, actors, levelPaths, stageContext, playerSelection,
            cachePreset: null, newCacheLabel: null, conversationPreview: false);

    internal void ConfigureDialogueConversationPreview(ConversationExtended conversation, DialogueNodeExtended startNode,
        IReadOnlyList<DialogueNodePreviewActor> actors, IReadOnlyList<string> levelPaths,
        StageConversationContext stageContext, DialoguePreviewPlayerSelection playerSelection,
        DialogueCachePreset cachePreset, string newCacheLabel) =>
        ConfigureDialoguePreview(conversation, startNode, actors, levelPaths, stageContext, playerSelection,
            cachePreset, newCacheLabel, conversationPreview: true);

    private void ConfigureDialoguePreview(ConversationExtended conversation, DialogueNodeExtended startNode,
        IReadOnlyList<DialogueNodePreviewActor> actors, IReadOnlyList<string> levelPaths,
        StageConversationContext stageContext, DialoguePreviewPlayerSelection playerSelection,
        DialogueCachePreset cachePreset, string newCacheLabel, bool conversationPreview)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(startNode);
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(levelPaths);
        ArgumentNullException.ThrowIfNull(stageContext);
        ArgumentNullException.ThrowIfNull(playerSelection);
        ReleaseTrackAnchorStageContext();
        dialogueBranchSelections.Clear();
        loadedDialogueCachePreset = null;
        isDialogueConversationPreview = conversationPreview;
        dialogueNodeInterpDataCache.Clear();
        DisposeDialoguePackageEditor();
        if (conversationPreview)
        {
            InitializeDialogueWorkingPackage(conversation.Export.FileRef, startNode.InterpData?.UIndex ?? 0);
        }
        dialogueNodePreview = new DialogueNodePreviewConfiguration(conversation, startNode, actors, levelPaths, stageContext,
            playerSelection, cachePreset, newCacheLabel, 0);
        BuildDialoguePreviewActorTagAliases(actors, stageContext);
        dialogueNodePreview = dialogueNodePreview with { VoStartTime = GetDialogueNodeVoStartTime(startNode) };
        BuildDialogueTimeline(startNode);
        DialoguePreviewActorPanel.Visibility = conversationPreview ? Visibility.Collapsed : Visibility.Visible;
        DialoguePreviewActorPanelSplitter.Visibility = conversationPreview ? Visibility.Collapsed : Visibility.Visible;
        DialoguePackageEditorTab.Visibility = conversationPreview ? Visibility.Visible : Visibility.Collapsed;
        DialoguePreviewLeftTabs.SelectedIndex = 0;
        DialoguePreviewActorPanel.Width = 260;
        DialogueTimelinePanel.Visibility = Visibility.Collapsed;
        PreviewEditorPanel.Visibility = conversationPreview ? Visibility.Collapsed : Visibility.Visible;
        DialogueNodeCommitButton.Visibility = conversationPreview ? Visibility.Visible : Visibility.Collapsed;
        DialogueCacheCommitAllButton.Visibility = conversationPreview ? Visibility.Visible : Visibility.Collapsed;
        DialogueCacheSaveButton.Visibility = conversationPreview ? Visibility.Visible : Visibility.Collapsed;
        DialogueCachePresetsButton.Visibility = conversationPreview ? Visibility.Visible : Visibility.Collapsed;
        DialogueCacheLoadingOverlay.Visibility = conversationPreview ? Visibility.Visible : Visibility.Collapsed;
        SceneViewer.Visibility = conversationPreview ? Visibility.Hidden : Visibility.Visible;
        PreviewActorModelSelectionPanel.Visibility = Visibility.Collapsed;
        // Dialogue actors are attached to authored TrackMove splines. Their vertical position is
        // part of that spline (stairs, lifts, ramps, and similar movement), so the standalone curve
        // editor's manual Z override must never be allowed to flatten dialogue playback.
        ActorPlaybackTrackZCheckBox.IsChecked = true;
        ActorPlaybackTrackZCheckBox.IsEnabled = false;
    }

    private void InitializeDialogueWorkingPackage(IMEPackage sourcePackage, int initialUIndex)
    {
        ArgumentNullException.ThrowIfNull(sourcePackage);

        dialoguePreviewSourcePackage = sourcePackage;
        using MemoryStream snapshot = sourcePackage.SaveToStream(false);
        snapshot.Position = 0;
        dialoguePreviewWorkingPackage = MEPackageHandler.OpenMEPackageFromStream(snapshot,
            sourcePackage.FilePath, useSharedPackageCache: false);
        dialoguePreviewWorkingPackage.IsMemoryPackage = true;
        dialoguePreviewWorkingPackage.WeakUsers.Add(this);
        dialogueWorkingCommittedNameCount = dialoguePreviewWorkingPackage.Names.Count;
        dialogueWorkingCommittedImportCount = dialoguePreviewWorkingPackage.Imports.Count;
        dialogueWorkingCommittedExportCount = dialoguePreviewWorkingPackage.Exports.Count;

        dialoguePackageEditor = new PackageEditorWindow(submitTelemetry: false);
        dialoguePackageEditor.PropertyChanged += DialoguePackageEditor_PropertyChanged;
        _ = new System.Windows.Interop.WindowInteropHelper(dialoguePackageEditor).EnsureHandle();
        dialoguePackageEditor.LoadPackage(dialoguePreviewWorkingPackage, initialUIndex);
        dialoguePackageEditor.SetEmbeddedTreeScope([]);
        FrameworkElement workspace = dialoguePackageEditor.PackageEditorWorkspace;
        if (workspace.Parent is Panel parent)
        {
            parent.Children.Remove(workspace);
        }
        workspace.DataContext = dialoguePackageEditor;
        DialoguePackageEditorHost.Content = workspace;
    }

    private void DisposeDialoguePackageEditor()
    {
        if (DialoguePackageEditorHost is not null)
        {
            DialoguePackageEditorHost.Content = null;
        }

        IMEPackage workingPackage = dialoguePreviewWorkingPackage;
        if (workingPackage is not null)
        {
            workingPackage.WeakUsers.Remove(this);
        }
        if (dialoguePackageEditor is not null)
        {
            dialoguePackageEditor.PropertyChanged -= DialoguePackageEditor_PropertyChanged;
            if (workingPackage is not null && ReferenceEquals(dialoguePackageEditor.Pcc, workingPackage))
            {
                workingPackage.Release(dialoguePackageEditor);
            }
            dialoguePackageEditor.Close();
            dialoguePackageEditor = null;
        }
        workingPackage?.Dispose();
        dialoguePreviewWorkingPackage = null;
        dialoguePreviewSourcePackage = null;
        dialogueWorkingCommittedNameCount = 0;
        dialogueWorkingCommittedImportCount = 0;
        dialogueWorkingCommittedExportCount = 0;
        suppressDialoguePackageEditTracking = false;
    }

    private void DialoguePackageEditor_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackageEditorWindow.IsBusy)
            && ReferenceEquals(sender, dialoguePackageEditor)
            && dialoguePackageEditor.IsBusy == false)
        {
            NavigateDialoguePackageEditorToActiveNode();
        }
    }

    private void DialoguePreviewLeftTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, DialoguePreviewLeftTabs))
        {
            return;
        }

        bool packageEditorSelected = ReferenceEquals(DialoguePreviewLeftTabs.SelectedItem,
            DialoguePackageEditorTab);
        DialoguePreviewActorPanel.Width = packageEditorSelected ? 760 : 260;
        if (!packageEditorSelected || dialoguePackageEditor is null)
        {
            return;
        }

        PauseDialogueTimeline();
        NavigateDialoguePackageEditorToActiveNode();
    }

    private void NavigateDialoguePackageEditorToActiveNode()
    {
        if (dialoguePackageEditor?.Pcc is null || dialoguePackageEditor.IsBusy
                                                 || activeDialogueTimelineSegment is null)
        {
            return;
        }

        ExportEntry interpData = MapDialogueExportToWorkingPackage(activeDialogueTimelineSegment.Node.InterpData);
        int? scopedInterpDataUIndex = interpData?.FileRef == dialoguePreviewWorkingPackage
            ? interpData.UIndex
            : null;
        List<TreeViewEntry> scopedRoots = dialoguePackageEditor.AllTreeViewNodesX
            .SelectMany(root => root.FlattenTree())
            .Where(node => node.Entry is ExportEntry export && export.UIndex == scopedInterpDataUIndex)
            .ToList();
        HashSet<int> scopedUIndexes = scopedRoots
            .SelectMany(root => root.FlattenTree())
            .Select(node => node.UIndex)
            .ToHashSet();
        int? preferredSelection = dialoguePackageEditor.SelectedItem is { } selected
                                  && scopedUIndexes.Contains(selected.UIndex)
            ? selected.UIndex
            : scopedRoots.FirstOrDefault()?.UIndex;
        dialoguePackageEditor.SetEmbeddedTreeScope(scopedRoots, preferredSelection);
    }

    private void QueueDialoguePackageEditorScopeRefresh()
    {
        if (dialoguePackageEditor is not null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(NavigateDialoguePackageEditorToActiveNode));
        }
    }

    private void BuildDialogueTimeline(DialogueNodeExtended startNode)
    {
        dialogueTimelineSegments.Clear();
        dialogueTimelineEdges.Clear();
        dialogueTimelineActivePath.Clear();
        dialogueTimelineSegmentsByReference.Clear();
        dialogueBranchOptions.Clear();
        dialogueRuntimeCache.Clear();
        dialogueNodeInterpDataCache.Clear();
        activeDialogueSegmentRuntime = null;
        dialogueTimelineStartSegment = null;
        if (dialogueNodePreview?.Conversation is not { } conversation || startNode is null)
        {
            return;
        }

        DialogueNodeReference startReference = GetDialogueNodeReference(conversation, startNode);
        dialogueTimelineStartSegment = CreateDialogueTimelineSegment(conversation, startNode, startReference, 0);
        dialogueTimelineSegments.Add(dialogueTimelineStartSegment);
        dialogueTimelineSegmentsByReference[startReference] = dialogueTimelineStartSegment;

        var pending = new Queue<DialogueTimelineSegment>();
        pending.Enqueue(dialogueTimelineStartSegment);
        int processedNodes = 0;
        while (pending.Count > 0 && processedNodes++ < 2048)
        {
            DialogueTimelineSegment source = pending.Dequeue();
            List<DialogueBranchOption> outgoing = isDialogueConversationPreview
                ? GetDialogueBranchOptions(conversation, source.Node)
                : [];
            source.BranchOptions = outgoing;
            foreach (DialogueBranchOption branch in outgoing)
            {
                branch.SourceSegment = source;
                if (!dialogueTimelineSegmentsByReference.TryGetValue(branch.TargetReference,
                        out DialogueTimelineSegment target))
                {
                    target = CreateDialogueTimelineSegment(conversation, branch.Target,
                        branch.TargetReference, source.TreeDepth + 1);
                    target.Parent = source;
                    target.IncomingBranch = branch;
                    dialogueTimelineSegmentsByReference[branch.TargetReference] = target;
                    dialogueTimelineSegments.Add(target);
                    pending.Enqueue(target);
                }
                branch.TargetSegment = target;
                dialogueTimelineEdges.Add(new DialogueTimelineEdge
                {
                    Source = source,
                    Target = target,
                    Branch = branch
                });
            }
        }

        const double horizontalSpacing = 270;
        const double verticalSpacing = 100;
        int largestColumn = 1;
        foreach (IGrouping<int, DialogueTimelineSegment> column in dialogueTimelineSegments.GroupBy(segment => segment.TreeDepth))
        {
            int row = 0;
            foreach (DialogueTimelineSegment segment in column)
            {
                segment.TreeLeft = segment.TreeDepth * horizontalSpacing;
                segment.TreeTop = row++ * verticalSpacing;
            }
            largestColumn = Math.Max(largestColumn, row);
        }

        int maxDepth = dialogueTimelineSegments.Max(segment => segment.TreeDepth);
        DialogueTimelineTreeWidth = maxDepth * horizontalSpacing + 250;
        DialogueTimelineTreeHeight = largestColumn * verticalSpacing + 2;
        RefreshDialogueTimelineActivePath();
        UpdateDialogueTimelineControls();
    }

    private DialogueTimelineSegment CreateDialogueTimelineSegment(ConversationExtended conversation,
        DialogueNodeExtended node, DialogueNodeReference reference, int treeDepth) => new()
    {
        Node = node,
        Reference = reference,
        Duration = GetDialogueNodeTimelineDuration(node),
        ListenerLabel = conversation.Speakers.FirstOrDefault(speaker => speaker.SpeakerID == node.Listener)
                            ?.SpeakerName ?? "None",
        TreeDepth = treeDepth
    };

    private void RefreshDialogueTimelineActivePath()
    {
        dialogueTimelineActivePath.Clear();
        dialogueBranchOptions.Clear();
        foreach (DialogueTimelineSegment segment in dialogueTimelineSegments)
        {
            segment.IsOnActivePath = false;
            segment.IsAwaitingBranchChoice = false;
            segment.IsAvailableBranch = false;
            foreach (DialogueBranchOption branch in segment.BranchOptions)
            {
                branch.IsSelected = dialogueBranchSelections.TryGetValue(branch.BranchKey,
                                            out DialogueNodeReference selectedReference)
                                    && branch.TargetReference == selectedReference;
            }
        }
        foreach (DialogueTimelineEdge edge in dialogueTimelineEdges)
        {
            edge.IsOnActivePath = false;
        }

        float timelineTime = 0;
        DialogueTimelineSegment current = dialogueTimelineStartSegment;
        var visited = new HashSet<DialogueNodeReference>();
        while (current is not null && visited.Add(current.Reference))
        {
            current.StartTime = timelineTime;
            current.IsOnActivePath = true;
            dialogueTimelineActivePath.Add(current);
            timelineTime += current.Duration;

            IReadOnlyList<DialogueBranchOption> outgoing = current.BranchOptions;
            if (outgoing.Count == 0)
            {
                break;
            }

            DialogueBranchOption next = outgoing.Count == 1
                ? outgoing[0]
                : outgoing.FirstOrDefault(branch => branch.IsSelected);
            if (next is null)
            {
                current.IsAwaitingBranchChoice = true;
                foreach (DialogueBranchOption branch in outgoing)
                {
                    dialogueBranchOptions.Add(branch);
                    if (branch.TargetSegment is not null)
                    {
                        branch.TargetSegment.IsAvailableBranch = true;
                    }
                }
                break;
            }

            if (dialogueTimelineEdges.FirstOrDefault(edge => ReferenceEquals(edge.Branch, next)) is { } activeEdge)
            {
                activeEdge.IsOnActivePath = true;
            }
            current = next.TargetSegment;
        }

        ReprojectDialogueActivePathActorOrigins();
    }

    private bool SelectDialogueTimelinePathTo(DialogueTimelineSegment target)
    {
        if (target is null || dialogueTimelineStartSegment is null)
        {
            return false;
        }

        DialogueBranchOption availableChoice = dialogueBranchOptions.FirstOrDefault(branch =>
            ReferenceEquals(branch.TargetSegment, target));
        List<DialogueBranchOption> path = availableChoice is null
            ? FindDialogueTimelinePath(target)
            : FindDialogueTimelinePath(availableChoice.SourceSegment) is { } sourcePath
                ? [.. sourcePath, availableChoice]
                : null;
        if (path is null)
        {
            return false;
        }

        bool changed = false;
        foreach (DialogueBranchOption branch in path)
        {
            if (branch.Source is null || branch.TargetReference is null)
            {
                continue;
            }
            if (branch.SourceSegment?.BranchOptions.Count > 1
                && (!dialogueBranchSelections.TryGetValue(branch.BranchKey, out DialogueNodeReference selected)
                    || selected != branch.TargetReference))
            {
                dialogueBranchSelections[branch.BranchKey] = branch.TargetReference;
                changed = true;
            }
        }

        if (changed)
        {
            RefreshDialogueTimelineActivePath();
        }
        return dialogueTimelineActivePath.Contains(target);
    }

    private List<DialogueBranchOption> FindDialogueTimelinePath(DialogueTimelineSegment target)
    {
        var pending = new Queue<(DialogueTimelineSegment Segment, List<DialogueBranchOption> Path)>();
        var visited = new HashSet<DialogueNodeReference>();
        pending.Enqueue((dialogueTimelineStartSegment, []));
        while (pending.Count > 0)
        {
            (DialogueTimelineSegment segment, List<DialogueBranchOption> path) = pending.Dequeue();
            if (!visited.Add(segment.Reference))
            {
                continue;
            }
            if (ReferenceEquals(segment, target))
            {
                return path;
            }
            foreach (DialogueBranchOption branch in segment.BranchOptions)
            {
                if (branch.TargetSegment is not null)
                {
                    pending.Enqueue((branch.TargetSegment, [.. path, branch]));
                }
            }
        }
        return null;
    }

    private bool ClearDialogueBranchSelectionsFrom(DialogueTimelineSegment segment)
    {
        if (segment is null)
        {
            return false;
        }

        bool changed = false;
        var pending = new Queue<DialogueTimelineSegment>();
        var visited = new HashSet<DialogueNodeReference>();
        pending.Enqueue(segment);
        while (pending.Count > 0)
        {
            DialogueTimelineSegment current = pending.Dequeue();
            if (!visited.Add(current.Reference))
            {
                continue;
            }
            if (current.BranchOptions.Count > 1
                && dialogueBranchSelections.Remove(GetDialogueBranchKey(current.Reference)))
            {
                changed = true;
            }
            foreach (DialogueBranchOption branch in current.BranchOptions)
            {
                if (branch.TargetSegment is not null)
                {
                    pending.Enqueue(branch.TargetSegment);
                }
            }
        }

        if (changed)
        {
            RefreshDialogueTimelineActivePath();
        }
        return changed;
    }

    private float GetDialogueNodeTimelineDuration(DialogueNodeExtended node)
    {
        // A ConvNode can start several Interps at once, but they all run inside the dialogue
        // node's authored clock. Additional Interps contribute tracks, never more timeline time.
        float interpLength = node.InterpData?.GetProperty<FloatProperty>("InterpLength")?.Value
                             ?? node.InterpLength;
        if (interpLength > 0)
        {
            return interpLength;
        }

        IReadOnlyList<ExportEntry> interpDatas = GetDialogueNodeInterpDatas(node);
        float voStartTime = GetDialogueNodeVoStartTime(node);
        float voDuration = AudioAnalyzer.GetAudioDuration(GetDialogueNodeAudio(node));
        float lastFaceOnlyVoKeyTime = GetLastFaceOnlyVoKeyTime(interpDatas);
        return ResolveDialogueNodeFallbackDuration(interpLength, voStartTime, voDuration,
            lastFaceOnlyVoKeyTime);
    }

    internal static float ResolveDialogueNodeFallbackDuration(float interpLength, float voStartTime,
        float voDuration, float lastFaceOnlyVoKeyTime)
    {
        if (interpLength > 0)
        {
            return interpLength;
        }

        float voEndTime = MathF.Max(0, voStartTime) + MathF.Max(0, voDuration);
        float contentEndTime = MathF.Max(voEndTime, MathF.Max(0, lastFaceOnlyVoKeyTime));
        return contentEndTime > 0 ? contentEndTime + 1f : 0.1f;
    }

    internal static float GetLastFaceOnlyVoKeyTime(IEnumerable<ExportEntry> interpDatas) =>
        interpDatas.Where(interpData => interpData is not null)
            .SelectMany(interpData => GetReferencedExports(interpData, "InterpGroups"))
            .SelectMany(group => GetReferencedExports(group, "InterpTracks"))
            .Where(track => track.IsA("SFXInterpTrackPlayFaceOnlyVO"))
            .SelectMany(track => track.GetProperty<ArrayProperty<StructProperty>>("m_aTrackKeys")?.AsEnumerable()
                                 ?? Enumerable.Empty<StructProperty>())
            .Select(key => key.GetProp<FloatProperty>("fTime")?.Value ?? 0)
            .DefaultIfEmpty(0)
            .Max();

    private float GetDialogueNodeVoStartTime(DialogueNodeExtended node)
    {
        float[] starts = GetDialogueNodeInterpDatas(node)
            .Select(GetDialoguePreviewVoStartTime)
            .Where(start => start > 0)
            .ToArray();
        return starts.Length > 0 ? starts.Min() : 0;
    }

    private IReadOnlyList<ExportEntry> GetDialogueNodeInterpDatas(DialogueNodeExtended node)
    {
        if (node is null)
        {
            return [];
        }
        if (dialogueNodeInterpDataCache.TryGetValue(node, out IReadOnlyList<ExportEntry> cached))
        {
            return cached;
        }

        cached = ResolveDialogueNodeInterpDatas(dialogueNodePreview?.Conversation, node);
        if (dialoguePreviewWorkingPackage is not null)
        {
            cached = cached.Select(MapDialogueExportToWorkingPackage)
                .Where(export => export is not null)
                .ToArray();
        }
        dialogueNodeInterpDataCache[node] = cached;
        return cached;
    }

    private ExportEntry MapDialogueExportToWorkingPackage(ExportEntry export)
    {
        if (export is null || dialoguePreviewWorkingPackage is null
                           || export.FileRef != dialoguePreviewSourcePackage)
        {
            return export;
        }

        return dialoguePreviewWorkingPackage.TryGetUExport(export.UIndex, out ExportEntry workingExport)
            ? workingExport
            : null;
    }

    private static IReadOnlyList<ExportEntry> ResolveDialogueNodeInterpDatas(ConversationExtended conversation,
        DialogueNodeExtended node)
    {
        if (node is null)
        {
            return [];
        }

        var interpDatas = new List<ExportEntry>();
        void AddInterpData(ExportEntry interpData)
        {
            if (interpData is not null
                && interpData.ClassName == "InterpData"
                && interpDatas.All(existing => !IsSameExport(existing, interpData)))
            {
                interpDatas.Add(interpData);
            }
        }

        AddInterpData(node.InterpData);
        if (conversation?.Sequence is ExportEntry sequence)
        {
            int exportId = node.ExportID != 0
                ? node.ExportID
                : node.NodeProp?.GetProp<IntProperty>("nExportID")?.Value ?? 0;
            IEnumerable<ExportEntry> conversationEvents = GetReferencedExports(sequence, "SequenceObjects")
                .Where(sequenceObject => sequenceObject.ClassName == "BioSeqEvt_ConvNode"
                                         && sequenceObject.GetProperty<IntProperty>("m_nNodeID")?.Value == exportId);
            var pending = new Queue<(ExportEntry Export, int Depth)>(
                conversationEvents.Select(conversationEvent => (conversationEvent, 0)));
            var visited = new HashSet<int>();
            while (pending.Count > 0)
            {
                (ExportEntry current, int depth) = pending.Dequeue();
                if (current is null || depth > 16 || !visited.Add(current.UIndex))
                {
                    continue;
                }
                if (current.ClassName == "SeqAct_Interp")
                {
                    foreach (ExportEntry interpData in GetSeqActInterpDatas(current))
                    {
                        AddInterpData(interpData);
                    }
                    // Do not walk beyond this action: a later SeqAct_Interp is a separate Kismet event.
                    continue;
                }
                foreach (OutputLink output in KismetHelper.GetOutputLinksOfNode(current).SelectMany(link => link))
                {
                    if (output.LinkedOp is ExportEntry linkedExport)
                    {
                        pending.Enqueue((linkedExport, depth + 1));
                    }
                }
            }
        }

        return interpDatas.ToArray();
    }

    private static IEnumerable<ExportEntry> GetSeqActInterpDatas(ExportEntry seqActInterp)
    {
        ArrayProperty<StructProperty> variableLinks = seqActInterp?
            .GetProperty<ArrayProperty<StructProperty>>("VariableLinks");
        foreach (StructProperty variableLink in variableLinks ?? Enumerable.Empty<StructProperty>())
        {
            if (!string.Equals(variableLink.GetProp<StrProperty>("LinkDesc")?.Value, "Data",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (ObjectProperty linkedVariable in variableLink
                         .GetProp<ArrayProperty<ObjectProperty>>("LinkedVariables")
                     ?? Enumerable.Empty<ObjectProperty>())
            {
                if (seqActInterp.FileRef.TryGetUExport(linkedVariable.Value, out ExportEntry interpData)
                    && interpData.ClassName == "InterpData")
                {
                    yield return interpData;
                }
            }
        }
    }

    private static List<DialogueBranchOption> GetDialogueBranchOptions(
        ConversationExtended conversation,
        DialogueNodeExtended source)
    {
        DialogueNodeReference sourceReference = GetDialogueNodeReference(conversation, source);
        string branchKey = GetDialogueBranchKey(sourceReference);
        if (source.IsReply)
        {
            return source.NodeProp.GetProp<ArrayProperty<IntProperty>>("EntryList")?
                .Where(reference => reference.Value >= 0 && reference.Value < conversation.EntryList.Count)
                .Select(reference => conversation.EntryList[reference.Value])
                .Select(target => new DialogueBranchOption
                {
                    Source = source,
                    Target = target,
                    TargetReference = GetDialogueNodeReference(conversation, target),
                    BranchKey = branchKey
                })
                .ToList() ?? [];
        }

        return source.NodeProp.GetProp<ArrayProperty<StructProperty>>("ReplyListNew")?
            .Select(link => (Link: link, Index: link.GetProp<IntProperty>("nIndex")?.Value ?? -1))
            .Where(item => item.Index >= 0 && item.Index < conversation.ReplyList.Count)
            .Select(item => new DialogueBranchOption
            {
                Source = source,
                Target = conversation.ReplyList[item.Index],
                TargetReference = new DialogueNodeReference(true, item.Index),
                BranchKey = branchKey,
                Category = GetDialogueReplyCategoryLabel(item.Link.GetProp<EnumProperty>("Category")?.Value.Name)
            })
            .ToList() ?? [];
    }

    private static DialogueNodeReference GetDialogueNodeReference(
        ConversationExtended conversation,
        DialogueNodeExtended node)
    {
        int index = node.IsReply ? conversation.ReplyList.IndexOf(node) : conversation.EntryList.IndexOf(node);
        return index >= 0
            ? new DialogueNodeReference(node.IsReply, index)
            : throw new InvalidOperationException("The dialogue node is not part of the selected BioConversation.");
    }

    private static string GetDialogueBranchKey(DialogueNodeReference reference) =>
        $"{(reference.IsReply ? 'R' : 'E')}:{reference.Index}";

    private static string GetDialogueReplyCategoryLabel(string category) => category switch
    {
        "REPLY_CATEGORY_DEFAULT" => "Default",
        "REPLY_CATEGORY_AGREE" => "Agree",
        "REPLY_CATEGORY_DISAGREE" => "Disagree",
        "REPLY_CATEGORY_FRIENDLY" => "Friendly",
        "REPLY_CATEGORY_HOSTILE" => "Hostile",
        "REPLY_CATEGORY_INVESTIGATE" => "Investigate",
        "REPLY_CATEGORY_RENEGADE_INTERRUPT" => "Renegade Interrupt",
        "REPLY_CATEGORY_PARAGON_INTERRUPT" => "Paragon Interrupt",
        _ => category
    };

    private static float GetDialoguePreviewVoStartTime(ExportEntry interpData)
    {
        ExportEntry voTrack = GetReferencedExports(interpData, "InterpGroups")
            .Where(group => GetInterpGroupName(group).Equals("Conversation", StringComparison.OrdinalIgnoreCase))
            .SelectMany(group => GetReferencedExports(group, "InterpTracks"))
            .FirstOrDefault(track => track.ClassName == "BioEvtSysTrackVOElements");
        return voTrack?.GetProperty<ArrayProperty<StructProperty>>("m_aTrackKeys")
                   ?.FirstOrDefault()?.GetProp<FloatProperty>("fTime")?.Value ?? 0;
    }

    public static IReadOnlyList<DialoguePreviewRecentLevelSet> GetDialoguePreviewRecentLevelSets() =>
        LoadRecentSets().Select(set => new DialoguePreviewRecentLevelSet(set.DisplayName, set.FilePaths.ToArray()))
            .ToArray();

    internal static IReadOnlyList<string> GetDialoguePreviewActorTags(ConversationExtended conversation) =>
        GetDialoguePreviewActorIdentities(conversation).Select(identity => identity.ActorTag).ToArray();

    internal static IReadOnlyList<DialoguePreviewActorIdentity> GetDialoguePreviewActorIdentities(
        ConversationExtended conversation)
    {
        if (conversation is null)
        {
            return [];
        }

        ExportEntry[] interpDatas = conversation.EntryList.Concat(conversation.ReplyList)
            .SelectMany(node => ResolveDialogueNodeInterpDatas(conversation, node))
            .DistinctBy(interp => (interp.FileRef, interp.UIndex))
            .ToArray();
        ExportEntry[] interpGroups = interpDatas.SelectMany(interpData =>
                GetReferencedExports(interpData, "InterpGroups"))
            .DistinctBy(group => (group.FileRef, group.UIndex))
            .ToArray();
        var cameraGroupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ExportEntry directorTrack in interpDatas.SelectMany(FindDirectorTracks))
        {
            foreach (StructProperty cut in directorTrack.GetProperty<ArrayProperty<StructProperty>>("CutTrack")
                         ?? Enumerable.Empty<StructProperty>())
            {
                string target = cut.GetProp<NameProperty>("TargetCamGroup")?.Value.Instanced;
                if (!string.IsNullOrWhiteSpace(target)) cameraGroupNames.Add(target);
            }
        }
        foreach (ExportEntry group in interpGroups)
        {
            ExportEntry[] tracks = GetReferencedExports(group, "InterpTracks").ToArray();
            string groupName = GetInterpGroupName(group);
            bool hasFovTrack = tracks.Any(IsDialogueFovTrack);
            if (IsDialogueCameraName(groupName) || hasFovTrack)
            {
                cameraGroupNames.Add(groupName);
            }
        }
        var candidates = new List<(string Tag, HashSet<string> Aliases)>();
        void Add(string tag, params string[] aliases)
        {
            if (ShouldCreateDialogueActor(tag, cameraGroupNames))
            {
                var identityAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { tag };
                identityAliases.UnionWith(aliases.Where(alias => ShouldCreateDialogueActor(alias,
                    cameraGroupNames)));
                candidates.Add((tag, identityAliases));
            }
        }

        // Player is a required preview actor even when a malformed conversation omits its
        // synthetic speaker entry. Camera groups are allowed to follow Player; that attachment
        // must not turn Player itself into a camera actor.
        Add("Player");
        foreach (SpeakerExtended speaker in conversation.Speakers)
        {
            Add(speaker.SpeakerName);
        }

        foreach (ExportEntry interpData in interpDatas)
        {
            foreach (ExportEntry group in GetReferencedExports(interpData, "InterpGroups"))
            {
                ExportEntry[] tracks = GetReferencedExports(group, "InterpTracks").ToArray();
                string groupName = GetInterpGroupName(group);
                bool hasFovTrack = tracks.Any(IsDialogueFovTrack);
                bool reservedGroup = groupName.Equals("Conversation", StringComparison.OrdinalIgnoreCase)
                                     || groupName.Equals("Director", StringComparison.OrdinalIgnoreCase)
                                     || tracks.Any(track => track.IsA("InterpTrackDirector"));
                bool cameraGroup = reservedGroup || hasFovTrack || IsDialogueCameraName(groupName)
                                   || cameraGroupNames.Contains(groupName);
                if (cameraGroup || tracks.Length == 0)
                {
                    continue;
                }

                string groupActor = group.GetProperty<NameProperty>("m_nmSFXFindActor")?.Value.Instanced;
                string[] trackActors = tracks.SelectMany(track => new[]
                    {
                        track.GetProperty<NameProperty>("m_nmSFXFindActor")?.Value.Instanced,
                        track.GetProperty<NameProperty>("m_nmFindActor")?.Value.Instanced,
                    })
                    .Where(tag => !string.IsNullOrWhiteSpace(tag) && !tag.Equals("None",
                        StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                string canonicalTag = trackActors.FirstOrDefault() ?? groupActor ?? groupName;
                Add(canonicalTag, trackActors.Append(groupActor).Append(groupName).ToArray());
            }
        }

        var identities = new List<DialoguePreviewActorIdentity>();
        foreach ((string tag, HashSet<string> aliases) in candidates)
        {
            DialoguePreviewActorIdentity[] matches = identities
                .Where(identity => identity.Aliases.Any(aliases.Contains))
                .ToArray();
            if (matches.Length == 0)
            {
                identities.Add(new DialoguePreviewActorIdentity(tag, aliases.ToArray()));
                continue;
            }
            foreach (DialoguePreviewActorIdentity match in matches)
            {
                aliases.UnionWith(match.Aliases);
                identities.Remove(match);
            }
            string preferredTag = aliases.OrderBy(GetDialogueActorIdentityPriority)
                .ThenBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                .First();
            identities.Add(new DialoguePreviewActorIdentity(preferredTag, aliases.ToArray()));
        }
        return identities;
    }

    internal static bool IsDialogueCameraName(string name) =>
        name?.Contains("cam", StringComparison.OrdinalIgnoreCase) == true;

    internal static bool ShouldCreateDialogueActor(string name, IReadOnlySet<string> directorCameraGroups) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.Equals("None", StringComparison.OrdinalIgnoreCase)
        && !IsDialogueCameraName(name)
        && !(directorCameraGroups?.Contains(name) ?? false);

    private static bool IsDialogueFovTrack(ExportEntry track) =>
        track?.ClassName == "InterpTrackFloatProp"
        && (track.GetProperty<NameProperty>("PropertyName")?.Value.Instanced
                .Equals("FOVAngle", StringComparison.OrdinalIgnoreCase) == true
            || track.GetProperty<StrProperty>("TrackTitle")?.Value
                .Equals("FOVAngle", StringComparison.OrdinalIgnoreCase) == true);

    internal static IReadOnlyList<DialoguePreviewActorIdentity> MergeDialoguePreviewActorIdentities(
        IEnumerable<DialoguePreviewActorIdentity> identities,
        IReadOnlyDictionary<string, CameraOrigin> actorOrigins)
    {
        var merged = new List<DialoguePreviewActorIdentity>();
        foreach (DialoguePreviewActorIdentity identity in identities ?? [])
        {
            var aliases = new HashSet<string>(identity.Aliases ?? [], StringComparer.OrdinalIgnoreCase)
            {
                identity.ActorTag,
            };
            foreach (string alias in aliases.ToArray())
            {
                if (!actorOrigins.TryGetValue(alias, out CameraOrigin aliasOrigin)) continue;
                foreach ((string authoredTag, CameraOrigin authoredOrigin) in actorOrigins)
                {
                    if (HaveEquivalentActorAliasOrigins(aliasOrigin, authoredOrigin)) aliases.Add(authoredTag);
                }
            }

            DialoguePreviewActorIdentity[] matches = merged
                .Where(existing => existing.Aliases.Any(aliases.Contains))
                .ToArray();
            foreach (DialoguePreviewActorIdentity match in matches)
            {
                aliases.UnionWith(match.Aliases);
                merged.Remove(match);
            }
            string preferredTag = aliases.OrderBy(GetDialogueActorIdentityPriority)
                .ThenBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .First();
            merged.Add(new DialoguePreviewActorIdentity(preferredTag, aliases.ToArray()));
        }
        return merged;
    }

    private static int GetDialogueActorIdentityPriority(string tag) =>
        tag.Equals("Player", StringComparison.OrdinalIgnoreCase) ? 0
        : tag.Equals("Owner", StringComparison.OrdinalIgnoreCase) ? 1
        : tag.StartsWith("Global_", StringComparison.OrdinalIgnoreCase) ? 2
        : 3;

    public static IReadOnlyList<string> GetDialoguePreviewFaceFxAssetNames(MEGame game)
    {
        string packagePath = FindDialoguePreviewFaceFxPackagePath(game);
        if (packagePath is null)
        {
            return ["SFX_HumanFemale_FaceFX", "SFX_HumanMale_FaceFX"];
        }

        using IMEPackage package = MEPackageHandler.OpenMEPackage(packagePath);
        return package.Exports
            .Where(export => export.ClassName == "FaceFXAsset"
                             && !export.IsDefaultObject
                             && (export.ObjectNameString.Equals("SFX_HumanFemale_FaceFX", StringComparison.OrdinalIgnoreCase)
                                 || export.ObjectNameString.Equals("SFX_HumanMale_FaceFX", StringComparison.OrdinalIgnoreCase)))
            .Select(export => export.ObjectNameString)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FindDialoguePreviewFaceFxPackagePath(MEGame game)
    {
        string cookedPath = MEDirectories.GetCookedPath(game);
        return Directory.Exists(cookedPath)
            ? Directory.EnumerateFiles(cookedPath, "BIOG_FaceFX_Assets.*", SearchOption.TopDirectoryOnly).FirstOrDefault()
            : null;
    }

    public bool IsApplyingUndoRedo => false;

    public bool ShowCollision
    {
        get => showCollision;
        set
        {
            if (SetProperty(ref showCollision, value))
            {
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    public bool ShowLightIcons
    {
        get => showLightIcons;
        set
        {
            if (SetProperty(ref showLightIcons, value))
            {
                RenderContext.ShowLightIcons = value;
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    public bool ShowFovIcons
    {
        get => showFovIcons;
        set
        {
            if (SetProperty(ref showFovIcons, value))
            {
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    public bool CameraFramingMode
    {
        get => cameraFramingMode;
        set
        {
            if (!SetProperty(ref cameraFramingMode, value))
            {
                return;
            }

            suppressTrackVisualizationForCameraPreview = false;
            if (value)
            {
                RenderContext.TransformWidget.Attach = null;
                PreviewSelectedCameraTrackValue();
            }
            else
            {
                RenderContext.TransformWidget.Attach = previewActorWidgetActive
                    ? previewActorWidgetTarget
                    : SelectedKeyframe;
            }
            SceneViewer?.MarkRenderDirty();
        }
    }

    public bool ShowVolumes
    {
        get => showVolumes;
        set
        {
            if (SetProperty(ref showVolumes, value))
            {
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    public bool ShowVolumetrics
    {
        get => showVolumetrics;
        set
        {
            if (SetProperty(ref showVolumetrics, value))
            {
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    public bool Unlit
    {
        get => unlit;
        set
        {
            if (SetProperty(ref unlit, value))
            {
                if (value)
                {
                    RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.Unlit;
                }
                else
                {
                    RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.Unlit;
                }
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    public bool SetAlphaToBlack
    {
        get => setAlphaToBlack;
        set
        {
            if (SetProperty(ref setAlphaToBlack, value))
            {
                if (value)
                {
                    RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.AlphaAsBlack;
                }
                else
                {
                    RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.AlphaAsBlack;
                }
            }
        }
    }

    public bool ShowRedChannel
    {
        get => showRedChannel;
        set
        {
            if (SetProperty(ref showRedChannel, value))
            {
                if (value)
                {
                    RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.EnableRedChannel;
                }
                else
                {
                    RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.EnableRedChannel;
                }
            }
        }
    }

    public bool ShowGreenChannel
    {
        get => showGreenChannel;
        set
        {
            if (SetProperty(ref showGreenChannel, value))
            {
                if (value)
                {
                    RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.EnableGreenChannel;
                }
                else
                {
                    RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.EnableGreenChannel;
                }
            }
        }
    }

    public bool ShowBlueChannel
    {
        get => showBlueChannel;
        set
        {
            if (SetProperty(ref showBlueChannel, value))
            {
                if (value)
                {
                    RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.EnableBlueChannel;
                }
                else
                {
                    RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.EnableBlueChannel;
                }
            }
        }
    }

    public bool ShowAlphaChannel
    {
        get => showAlphaChannel;
        set
        {
            if (SetProperty(ref showAlphaChannel, value))
            {
                if (value)
                {
                    RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.EnableAlphaChannel;
                }
                else
                {
                    RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.EnableAlphaChannel;
                }
            }
        }
    }

    public System.Windows.Media.Color BackgroundColor
    {
        get => backgroundColor;
        set
        {
            if (SetProperty(ref backgroundColor, value))
            {
                RenderContext.BackgroundColor = value;
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    public bool UseLocalCoordsForWidget
    {
        get => RenderContext.TransformWidget.UseLocalCoords;
        set => SetProperty(ref RenderContext.TransformWidget.UseLocalCoords, value);
    }

    public string CameraPositionX
    {
        get => cameraPositionX;
        set => SetProperty(ref cameraPositionX, value);
    }

    public string CameraPositionY
    {
        get => cameraPositionY;
        set => SetProperty(ref cameraPositionY, value);
    }

    public string CameraPositionZ
    {
        get => cameraPositionZ;
        set => SetProperty(ref cameraPositionZ, value);
    }

    public string CameraRotationX
    {
        get => cameraRotationX;
        set => SetProperty(ref cameraRotationX, value);
    }

    public string CameraRotationY
    {
        get => cameraRotationY;
        set => SetProperty(ref cameraRotationY, value);
    }

    public string CameraRotationZ
    {
        get => cameraRotationZ;
        set => SetProperty(ref cameraRotationZ, value);
    }

    public float CameraPositionStep
    {
        get => cameraPositionStep;
        set => SetProperty(ref cameraPositionStep, value);
    }

    public float CameraRotationStep
    {
        get => cameraRotationStep;
        set => SetProperty(ref cameraRotationStep, value);
    }

    public ICommand ToggleTranslateCommand { get; private set; }

    public ICommand ToggleRotateCommand { get; private set; }

    public ICommand ToggleScaleCommand { get; private set; }

    public ICommand ToggleUniformScaleCommand { get; private set; }

    public ICommand ToggleLocalCoordsCommand { get; private set; }

    public IReadOnlyList<EInterpCurveMode> InterpModes { get; }

    public string CurrentExportName
    {
        get => currentExportName;
        private set => SetProperty(ref currentExportName, value);
    }

    public string SceneStatus
    {
        get => sceneStatus;
        private set => SetProperty(ref sceneStatus, value);
    }

    public string PlaybackKeyframeStatus
    {
        get => playbackKeyframeStatus;
        private set => SetProperty(ref playbackKeyframeStatus, value);
    }

    public CurveEditor3DKeyframe SelectedKeyframe
    {
        get => selectedKeyframe;
        private set
        {
            if (value is not null)
            {
                suppressTrackVisualizationForCameraPreview = false;
            }
            bool selectionChanged = SetProperty(ref selectedKeyframe, value);
            if (value is not null && selectedFovKeyframe is not null)
            {
                selectedFovKeyframe = null;
                OnPropertyChanged(nameof(SelectedFovKeyframe));
                FovKeyframeList.SelectedItem = null;
            }
            previewActorWidgetActive = false;
            SelectedKeyframeInVal = value?.Time.ToString(CultureInfo.CurrentCulture);
            SnapToKeyButton.IsEnabled = value is not null;
            SnapKeyToCursorButton.IsEnabled = value is not null;
            KeyframeList.SelectedItem = value;
            if (value is not null && (selectionChanged || !KeyframeList.IsKeyboardFocusWithin))
            {
                KeyframeList.ScrollIntoView(value);
            }
            RenderContext.TransformWidget.Attach = CameraFramingMode ? null : value;
            UpdateRotationDialIndicator();
            if (CameraFramingMode && value is not null)
            {
                PreviewSelectedCameraTrackValue();
            }
            SceneViewer?.MarkRenderDirty();
        }
    }

    public CurveEditor3DFovKeyframe SelectedFovKeyframe
    {
        get => selectedFovKeyframe;
        private set
        {
            if (value is not null)
            {
                suppressTrackVisualizationForCameraPreview = false;
            }
            bool selectionChanged = SetProperty(ref selectedFovKeyframe, value);
            if (value is not null && selectedKeyframe is not null)
            {
                selectedKeyframe = null;
                OnPropertyChanged(nameof(SelectedKeyframe));
                KeyframeList.SelectedItem = null;
                SnapToKeyButton.IsEnabled = false;
                SnapKeyToCursorButton.IsEnabled = false;
            }
            SelectedFovKeyframeInVal = value?.Time.ToString(CultureInfo.CurrentCulture);
            FovKeyframeList.SelectedItem = value;
            if (value is not null && (selectionChanged || !FovKeyframeList.IsKeyboardFocusWithin))
            {
                FovKeyframeList.ScrollIntoView(value);
            }
            previewActorWidgetActive = false;
            RenderContext.TransformWidget.Attach = CameraFramingMode || value is not null ? null : SelectedKeyframe;
            if (CameraFramingMode && value is not null)
            {
                PreviewSelectedCameraTrackValue();
            }
            SceneViewer?.MarkRenderDirty();
        }
    }

    public string SelectedKeyframeInVal
    {
        get => selectedKeyframeInVal;
        set => SetProperty(ref selectedKeyframeInVal, value);
    }

    public string SelectedFovKeyframeInVal
    {
        get => selectedFovKeyframeInVal;
        set => SetProperty(ref selectedFovKeyframeInVal, value);
    }

    public override bool CanParse(ExportEntry exportEntry)
        => exportEntry?.ClassName == "InterpTrackMove"
           && exportEntry.GetProperty<StructProperty>("PosTrack") is not null
           && exportEntry.GetProperty<StructProperty>("EulerTrack") is not null;

    public override void LoadExport(ExportEntry exportEntry)
    {
        bool isSameExport = CurrentLoadedExport is not null
                            && CurrentLoadedExport.FileRef == exportEntry.FileRef
                            && CurrentLoadedExport.UIndex == exportEntry.UIndex;
        float? selectedKeyframeTime = isSameExport ? SelectedKeyframe?.Time : null;
        StopPlayback(false);
        UnregisterKeyframes();
        CurrentLoadedExport = exportEntry;
        ResolveTrackAnchorStageContext(exportEntry);
        model.Load(exportEntry);
        RefreshAvailableGestureTracks(exportEntry);
        RefreshMulticamPlaybackOptions(exportEntry);
        if (dialogueNodePreview is null && PreviewEditorCategoryTabs.SelectedIndex == 0)
        {
            PreviewEditorCategoryTabs.SelectedIndex = 1;
        }
        if (!loadingDialogueTimelineSegment)
        {
            InitializePreviewActorLayout(exportEntry.Game);
        }
        EnsurePreviewActorTrackAssignments();
        RefreshKeyframeTrackMoveTabs();
        trajectorySamplesDirty = true;
        if (dialogueNodePreview is null && ActiveTrackMoveOption?.TrackMove is { } standaloneTrack)
        {
            tracksWithVisibleKeys.Add(GetTrackMoveEditingKey(standaloneTrack));
            ActivateSelectedTrackMove();
        }
        SelectedKeyframe = selectedKeyframeTime.HasValue && ActiveModel.Keyframes.Count > 0
            ? ActiveModel.Keyframes.MinBy(keyframe => MathF.Abs(keyframe.Time - selectedKeyframeTime.Value))
            : ActiveModel.Keyframes.FirstOrDefault();
        if (!hasSnappedInitialCamera && model.Keyframes.MinBy(keyframe => keyframe.Time) is { } earliestKeyframe)
        {
            SnapCameraToKey(earliestKeyframe);
            hasSnappedInitialCamera = true;
        }
        CurrentExportName = $"{exportEntry.UIndex}: {exportEntry.InstancedFullPath}";
        SceneStatus = $"{model.Keyframes.Count} trajectory keyframe(s); {levelPaths.Count} level backdrop file(s).";
        UpdatePlaybackButton();
        SceneViewer?.MarkRenderDirty();
        if (!loadingDialogueTimelineSegment)
        {
            _ = RestoreSessionLevelsAsync();
        }
    }

    public override void UnloadExport()
    {
        StopPlayback(false);
        UnregisterKeyframes();
        ReleaseTrackAnchorStageContext();
        model.Clear();
        trajectorySamples = [];
        trajectorySamplesDirty = false;
        PlaybackKeyframeStatus = "Not playing";
        KeyframeList.ItemsSource = null;
        SelectedKeyframe = null;
        FovKeyframeList.ItemsSource = null;
        SelectedFovKeyframe = null;
        FovTrackPanel.Visibility = Visibility.Collapsed;
        CurrentLoadedExport = null;
        CurrentExportName = null;
        previewActorGestureAssignments.Clear();
        previewActorTrackAssignments.Clear();
        availableGestureTracks.Clear();
        availableTrackMoves.Clear();
        availableExtraTrackMoves.Clear();
        availableDirectorTracks.Clear();
        characterTrackMoves.Clear();
        cameraTrackMoves.Clear();
        tracksWithVisibleKeys.Clear();
        selectedExtraTrackMove = null;
        selectedDirectorPlayback = null;
        primaryTrackMove = null;
        playExtraTrackMove = false;
        playDirectorMulticam = false;
        UpdatePlaybackButton();
        SceneViewer?.MarkRenderDirty();
    }

    public override void PopOut()
    {
        if (CurrentLoadedExport is null)
        {
            return;
        }

        var window = new ExportLoaderHostedWindow(new CurveEditor3D(), CurrentLoadedExport)
        {
            Title = $"3D Curve Editor - {CurrentLoadedExport.UIndex} {CurrentLoadedExport.InstancedFullPath}"
        };
        window.Show();
    }

    public override void Dispose()
    {
        SavePreviewActorLayout();
        ClearPreviewActorModels();
        UnloadExport();
        CloseLevels();
        DetachEvents();
        model.Changed -= Model_Changed;
        RenderContext.UpdateScene -= UpdatePlayback;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        KeyframeList.PreviewMouseRightButtonDown -= KeyframeList_PreviewMouseRightButtonDown;
        previewActorGesturePackageCache.ReleasePackages();
        dialoguePreviewFaceFxPackage?.Dispose();
        dialoguePreviewFaceFxPackage = null;
        DisposeDialoguePackageEditor();
        dialogueNodePreview?.StageContext.Dispose();
        SceneViewer.Dispose();
    }

    private void ResolveTrackAnchorStageContext(ExportEntry trackMove)
    {
        ReleaseTrackAnchorStageContext();
        if (dialogueNodePreview is not null || !HasAnchorObjectTrack(trackMove))
        {
            return;
        }

        StageBoneOriginResolver.TrySelectContext(Window.GetWindow(this), trackMove.FileRef, trackMove,
            trackAnchorConversation,
            out trackAnchorStageContext, out _);
    }

    internal void ConfigureTrackAnchorConversation(ConversationExtended conversation)
    {
        trackAnchorConversation = conversation;
    }

    private static bool HasAnchorObjectTrack(ExportEntry trackMove)
    {
        if (GetTrackMoveFrame(trackMove) == EInterpTrackMoveFrame.IMF_AnchorObject)
        {
            return true;
        }

        ExportEntry interpData = FindOwningInterpData(trackMove);
        return GetReferencedExports(interpData, "InterpGroups")
            .SelectMany(group => GetReferencedExports(group, "InterpTracks"))
            .Any(track => track.ClassName == "InterpTrackMove"
                          && GetTrackMoveFrame(track) == EInterpTrackMoveFrame.IMF_AnchorObject);
    }

    private void ReleaseTrackAnchorStageContext()
    {
        trackAnchorStageContext?.Dispose();
        trackAnchorStageContext = null;
    }

    private void RefreshAvailableGestureTracks(ExportEntry trackMove) =>
        RefreshAvailableGestureTracksForInterpData(FindOwningInterpData(trackMove));

    private void RefreshAvailableGestureTracksForInterpData(ExportEntry interpData)
    {
        availableGestureTracks.Clear();
        availableGestureTracks.Add(GestureTrackOption.None);
        previewActorGestureAssignments.Clear();
        foreach (KeyValuePair<PreviewActorConfiguration, PreviewActorAnimationState> pair in previewActorAnimationStates)
        {
            if (dialogueNodePreview is not null)
            {
                pair.Value.HoldCurrentGesturePose();
            }
            else
            {
                pair.Value.Clear();
            }
            UpdatePreviewActorSkinning(pair.Key);
        }
        if (!buildingDialogueRuntimeCache)
        {
            previewActorGesturePackageCache.ReleasePackages();
        }

        foreach (ExportEntry gestureTrack in FindGestureTracksInInterpData(interpData))
        {
            List<GesturePreviewExportLoader.GestureAnimationItem> animations = GesturePreviewExportLoader
                .BuildAnimationTimeline(gestureTrack, previewActorGesturePackageCache);
            List<GesturePreviewExportLoader.GestureAnimationItem> resolvedAnimations = animations
                .Where(animation => animation.AnimationExport is not null)
                .ToList();
            GesturePreviewExportLoader.GestureAnimationItem startingPose = resolvedAnimations
                .FirstOrDefault(animation => !animation.GestureIndex.HasValue);
            float? playbackDuration = dialogueNodePreview?.Node is { } node
                ? GetDialogueNodeTimelineDuration(node)
                : interpData?.GetProperty<FloatProperty>("InterpLength")?.Value;
            List<AnimationPreviewControl.AnimationTimelineClip> timeline = GesturePreviewExportLoader
                .BuildPlaybackTimelineWithBaseLayer(animations.Where(animation => animation.GestureIndex.HasValue).ToList(),
                    startingPose is not null, playbackDuration);
            string title = gestureTrack.GetProperty<StrProperty>("TrackTitle")?.Value ?? gestureTrack.ObjectName.Instanced;
            string actor = gestureTrack.GetProperty<NameProperty>("m_nmFindActor")?.Value.Instanced ?? "None";
            availableGestureTracks.Add(new GestureTrackOption
            {
                DisplayName = $"{title} ({actor})",
                Group = gestureTrack.Parent as ExportEntry,
                Track = gestureTrack,
                Animations = animations,
                StartingPose = startingPose,
                Timeline = timeline,
                Status = timeline.Count == 0 && startingPose is null
                    ? $"{title}: no resolved gesture animation timeline."
                    : $"{title}: {resolvedAnimations.Count} resolved animation slot(s).",
            });
        }
    }

    private void RefreshMulticamPlaybackOptions(ExportEntry trackMove, ExportEntry interpDataOverride = null)
    {
        var fovModelsByExport = new Dictionary<int, CurveEditor3DFovModel>();
        Dictionary<PreviewActorConfiguration, ExportEntry> previousActorTracks = dialogueNodePreview is null
            ? previewActorTrackAssignments
                .Where(pair => pair.Value?.TrackMove is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value.TrackMove)
            : [];
        updatingMulticamControls = true;
        primaryTrackMove = null;
        availableTrackMoves.Clear();
        availableExtraTrackMoves.Clear();
        availableExtraTrackMoves.Add(TrackMovePlaybackOption.None);
        availableDirectorTracks.Clear();
        availableDirectorTracks.Add(DirectorPlaybackOption.None);
        dialoguePreviewCameraActors.Clear();

        ExportEntry interpData = interpDataOverride ?? FindOwningInterpData(trackMove);
        if (interpData is not null)
        {
            Dictionary<string, TrackMovePlaybackOption> cameraOptionsByGroup = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ExportEntry> groupsByName = new(StringComparer.OrdinalIgnoreCase);
            foreach (ExportEntry group in GetReferencedExports(interpData, "InterpGroups"))
            {
                string groupName = GetInterpGroupName(group);
                groupsByName.TryAdd(groupName, group);
                foreach (ExportEntry groupTrackMove in GetReferencedExports(group, "InterpTracks")
                             .Where(track => track.ClassName == "InterpTrackMove"))
                {
                    CurveEditor3DModel trackModel = groupTrackMove == trackMove ? model : new CurveEditor3DModel();
                    if (trackModel != model)
                    {
                        trackModel.Load(groupTrackMove);
                        trackModel.Changed += Model_Changed;
                    }
                    if (trackModel.Keyframes.Count == 0)
                    {
                        continue;
                    }

                    string trackTitle = groupTrackMove.GetProperty<StrProperty>("TrackTitle")?.Value ?? groupTrackMove.ObjectName.Instanced;
                    var option = new TrackMovePlaybackOption
                    {
                        DisplayName = $"{groupName} - {trackTitle}",
                        Group = group,
                        TrackMove = groupTrackMove,
                        Model = trackModel,
                        FovModel = LoadCameraFovModel(group, fovModelsByExport),
                    };
                    availableTrackMoves.Add(option);
                    bool isCameraTrackForGroup = cameraOptionsByGroup.TryAdd(groupName, option);
                    if (isCameraTrackForGroup && groupTrackMove != trackMove)
                    {
                        availableExtraTrackMoves.Add(option);
                    }
                    else
                    {
                        primaryTrackMove = option;
                    }
                }
            }

            foreach (ExportEntry directorTrack in FindDirectorTracks(interpData))
            {
                List<DirectorCameraCut> cuts = BuildDirectorCameraCuts(directorTrack, cameraOptionsByGroup,
                    groupsByName);
                if (cuts.Count == 0)
                {
                    continue;
                }

                string title = directorTrack.GetProperty<StrProperty>("TrackTitle")?.Value ?? directorTrack.ObjectName.Instanced;
                availableDirectorTracks.Add(new DirectorPlaybackOption
                {
                    DisplayName = $"{title} ({cuts.Count} cut{(cuts.Count == 1 ? string.Empty : "s")})",
                    DirectorTrack = directorTrack,
                    Cuts = cuts,
                });
            }

            if (dialogueNodePreview is not null)
            {
                HashSet<string> cameraGroupNames = availableDirectorTracks
                    .SelectMany(director => director.Cuts)
                    .Select(cut => cut.GroupName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                HashSet<string> cameraActorTags = availableDirectorTracks
                    .SelectMany(director => director.Cuts)
                    .Select(cut => cut.CameraActorTag)
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                cameraGroupNames.UnionWith(availableTrackMoves
                    .Where(option => IsCameraTrackGroup(option.Group, option.FovModel is not null))
                    .Select(option => GetInterpGroupName(option.Group)));
                dialoguePreviewCameraActors.AddRange(availableTrackMoves
                    .Where(option => cameraGroupNames.Contains(GetInterpGroupName(option.Group))
                                     || cameraActorTags.Contains(GetCameraActorTag(option.Group)))
                    .DistinctBy(option => option.TrackMove.UIndex));
            }
        }

        selectedExtraTrackMove = availableExtraTrackMoves.Contains(selectedExtraTrackMove) ? selectedExtraTrackMove : TrackMovePlaybackOption.None;
        selectedDirectorPlayback = availableDirectorTracks.Contains(selectedDirectorPlayback) ? selectedDirectorPlayback : DirectorPlaybackOption.None;
        ExtraTrackMoveComboBox.SelectedItem = selectedExtraTrackMove;
        DirectorTrackComboBox.SelectedItem = selectedDirectorPlayback;
        playExtraTrackMove = ExtraTrackMoveCheckBox.IsChecked == true && selectedExtraTrackMove?.TrackMove is not null;
        playDirectorMulticam = DirectorMulticamCheckBox.IsChecked == true && selectedDirectorPlayback?.Cuts.Count > 0;
        updatingMulticamControls = false;
        if (trackMove is not null)
        {
            primaryTrackMove ??= new TrackMovePlaybackOption
            {
                DisplayName = trackMove.GetProperty<StrProperty>("TrackTitle")?.Value ?? trackMove.ObjectName.Instanced,
                Group = trackMove.Parent as ExportEntry,
                TrackMove = trackMove,
                Model = model,
                FovModel = LoadCameraFovModel(trackMove.Parent as ExportEntry, fovModelsByExport),
            };
            if (availableTrackMoves.All(option => !IsSameExport(option.TrackMove, primaryTrackMove.TrackMove)))
            {
                availableTrackMoves.Insert(0, primaryTrackMove);
            }
        }
        previewActorTrackAssignments.Clear();
        foreach ((PreviewActorConfiguration actor, ExportEntry previousTrack) in previousActorTracks)
        {
            TrackMovePlaybackOption replacement = availableTrackMoves.FirstOrDefault(option =>
                IsSameExport(option.TrackMove, previousTrack));
            if (replacement is not null)
            {
                previewActorTrackAssignments[actor] = replacement;
            }
        }
        if (dialogueNodePreview is not null && previewActors.Count > 0)
        {
            AssignDialoguePreviewTrackMoves();
        }
        else
        {
            EnsurePreviewActorTrackAssignments();
        }
        RefreshKeyframeTrackMoveTabs();
    }

    private void EnsurePreviewActorTrackAssignments()
    {
        foreach (PreviewActorConfiguration removedActor in previewActorTrackAssignments.Keys
                     .Where(actor => !previewActors.Contains(actor)).ToList())
        {
            previewActorTrackAssignments.Remove(removedActor);
        }

        if (previewActors.FirstOrDefault() is { } actor1
            && !previewActorTrackAssignments.ContainsKey(actor1)
            && primaryTrackMove is not null)
        {
            previewActorTrackAssignments[actor1] = primaryTrackMove;
        }
        UpdatePreviewActorTrackAssignmentControls();
    }

    private void RefreshKeyframeTrackMoveTabs()
    {
        ExportEntry previouslySelectedCharacter =
            (CharacterTrackMoveTabs.SelectedItem as TrackMovePlaybackOption)?.TrackMove;
        ExportEntry previouslySelectedCamera =
            (CameraTrackMoveTabs.SelectedItem as TrackMovePlaybackOption)?.TrackMove;
        List<TrackMovePlaybackOption> tabs = [];
        foreach (TrackMovePlaybackOption availableTrack in availableTrackMoves)
        {
            AddDistinctTrackMove(tabs, availableTrack);
        }
        AddDistinctTrackMove(tabs, primaryTrackMove);
        AddDistinctTrackMove(tabs, selectedExtraTrackMove);
        if (selectedDirectorPlayback is not null)
        {
            foreach (TrackMovePlaybackOption camera in selectedDirectorPlayback.Cuts.Select(cut => cut.Camera))
            {
                AddDistinctTrackMove(tabs, camera);
            }
        }
        foreach (TrackMovePlaybackOption actorTrack in previewActorTrackAssignments.Values)
        {
            AddDistinctTrackMove(tabs, actorTrack);
        }

        foreach (TrackMovePlaybackOption tab in tabs)
        {
            string actorNames = string.Join(", ", previewActors
                .Where(actor => previewActorTrackAssignments.TryGetValue(actor, out TrackMovePlaybackOption assignment)
                                && IsSameExport(assignment.TrackMove, tab.TrackMove))
                .Select(actor => actor.DisplayName));
            tab.TabDisplayName = string.IsNullOrEmpty(actorNames)
                ? tab.DisplayName
                : $"{tab.DisplayName} [{actorNames}]";
        }

        TrackMovePlaybackOption[] cameras = tabs.Where(IsCameraMovementTrack).ToArray();
        TrackMovePlaybackOption[] characters = tabs.Where(tab => !IsCameraMovementTrack(tab)).ToArray();

        updatingKeyframeTrackTabs = true;
        characterTrackMoves.Clear();
        foreach (TrackMovePlaybackOption tab in characters)
        {
            characterTrackMoves.Add(tab);
        }
        cameraTrackMoves.Clear();
        foreach (TrackMovePlaybackOption tab in cameras)
        {
            cameraTrackMoves.Add(tab);
        }
        CharacterTrackMoveTabs.SelectedItem = characters.FirstOrDefault(tab =>
                                                      IsSameExport(tab.TrackMove, previouslySelectedCharacter))
                                                  ?? characters.FirstOrDefault(tab =>
                                                      IsSameExport(tab.TrackMove, primaryTrackMove?.TrackMove))
                                                  ?? characters.FirstOrDefault();
        CameraTrackMoveTabs.SelectedItem = cameras.FirstOrDefault(tab =>
                                                   IsSameExport(tab.TrackMove, previouslySelectedCamera))
                                               ?? cameras.FirstOrDefault(tab =>
                                                   IsSameExport(tab.TrackMove, primaryTrackMove?.TrackMove))
                                               ?? cameras.FirstOrDefault();
        updatingKeyframeTrackTabs = false;
        ActivateSelectedTrackMove();
    }

    private bool IsCameraMovementTrack(TrackMovePlaybackOption option) => option?.TrackMove is not null
        && (dialoguePreviewCameraActors.Any(camera => IsSameExport(camera.TrackMove, option.TrackMove))
            || availableDirectorTracks.SelectMany(director => director.Cuts)
                .Any(cut => IsSameExport(cut.Camera?.TrackMove, option.TrackMove))
            || IsCameraTrackGroup(option.Group, option.FovModel is not null));

    private static void AddDistinctTrackMove(List<TrackMovePlaybackOption> tabs, TrackMovePlaybackOption option)
    {
        if (option?.TrackMove is not null && tabs.All(tab => !IsSameExport(tab.TrackMove, option.TrackMove)))
        {
            tabs.Add(option);
        }
    }

    private static bool IsSameExport(ExportEntry left, ExportEntry right)
        => left is not null && right is not null && left.FileRef == right.FileRef && left.UIndex == right.UIndex;

    private static bool IsSameAnimationExport(ExportEntry left, ExportEntry right)
        => left is not null
           && right is not null
           && left.UIndex == right.UIndex
           && (left.FileRef == right.FileRef
               || string.Equals(left.FileRef?.FilePath, right.FileRef?.FilePath,
                   StringComparison.OrdinalIgnoreCase));

    internal static float ResolveGestureAnimationTime(float timelineTime, float clipStartTime,
        float animationStartTime, float animationEndTime, float playRate, bool loop)
    {
        float animationDuration = Math.Max(0, animationEndTime - animationStartTime);
        if (animationDuration <= 0)
        {
            return animationStartTime;
        }

        float elapsed = Math.Max(0, timelineTime - clipStartTime) * Math.Max(0.0001f, playRate);
        if (loop)
        {
            return animationStartTime + elapsed % animationDuration;
        }
        return Math.Clamp(animationStartTime + elapsed, animationStartTime, animationEndTime);
    }

    private static float? ResolveStartingPoseContinuationTime(GestureTrackOption gesture,
        DialogueGesturePoseState inheritedState)
        => ResolveMatchingStartingPoseTime(gesture?.StartingPose?.AnimationExport,
            gesture?.StartingPose?.AnimationDuration ?? 0,
            inheritedState?.Animation, inheritedState?.AnimationTime ?? 0);

    internal static float? ResolveMatchingStartingPoseTime(ExportEntry startingPoseAnimation,
        float startingPoseDuration, ExportEntry inheritedAnimation,
        float inheritedAnimationTime)
    {
        // A TrackGesture's starting pose is the base beneath every authored gesture/pose key. It
        // must retain the phase already running on the actor even when this node also has keys.
        // Restarting E22's standing idle at its authored offset makes the actor visibly snap just
        // before WI_WallLeanLeftEnter blends in.
        if (startingPoseAnimation is null
            || inheritedAnimation is null
            || !IsSameAnimationExport(startingPoseAnimation, inheritedAnimation))
        {
            return null;
        }

        return Math.Clamp(inheritedAnimationTime, 0, startingPoseDuration);
    }

    private static DialogueGesturePoseState ResolveDialogueGestureEndState(GestureTrackOption gesture,
        float playbackDuration, float? startingPoseTimeOverride)
    {
        if (gesture is null)
        {
            return null;
        }

        AnimationPreviewControl.AnimationTimelineClip basePose = gesture.Timeline
            .Where(clip => clip.IsBaseLayer && clip.AnimationExport is not null
                           && playbackDuration >= clip.StartTime
                           && playbackDuration <= clip.EndTime + 0.0001f)
            .OrderBy(clip => clip.StartTime)
            .LastOrDefault();
        if (basePose is not null)
        {
            return new DialogueGesturePoseState(basePose.AnimationExport,
                ResolveGestureAnimationTime(playbackDuration, basePose.StartTime,
                    basePose.AnimationStartTime, basePose.AnimationEndTime, basePose.PlayRate, basePose.Loop));
        }

        GesturePreviewExportLoader.GestureAnimationItem startingPose = gesture.StartingPose;
        if (startingPose?.AnimationExport is null)
        {
            return null;
        }

        float animationStart = Math.Clamp(startingPoseTimeOverride ?? startingPose.Settings.StartOffset,
            0, startingPose.AnimationDuration);
        float timelineStart = gesture.Timeline.Count > 0
            ? Math.Min(0, gesture.Timeline.Min(clip => clip.StartTime))
            : 0;
        return new DialogueGesturePoseState(startingPose.AnimationExport,
            ResolveGestureAnimationTime(playbackDuration, timelineStart, animationStart,
                startingPose.AnimationDuration, 1, loop: true));
    }

    internal static bool IsEligibleActorTrackMove(ExportEntry trackMove, IEnumerable<ExportEntry> cameraTrackMoves)
        => trackMove is not null
           && (cameraTrackMoves is null
               || cameraTrackMoves.All(cameraTrack => !IsSameExport(trackMove, cameraTrack)));

    internal static bool IsActorMatchingInterpGroup(ExportEntry group)
        => GetReferencedExports(group, "InterpTracks").Any();

    internal static bool IsCameraTrackGroup(ExportEntry group, bool hasFovTrack)
        => hasFovTrack
           || GetInterpGroupName(group).StartsWith("Cam", StringComparison.OrdinalIgnoreCase);

    internal static bool IsEligibleActorTrackGroup(ExportEntry group, string actorTag)
        => !string.Equals(actorTag, "player", StringComparison.OrdinalIgnoreCase)
           || group is not null
           && GetInterpGroupName(group).Equals("Player", StringComparison.OrdinalIgnoreCase);

    private static ExportEntry FindOwningInterpData(ExportEntry track)
        => track?.Parent is ExportEntry interpGroup && interpGroup.Parent is ExportEntry interpData ? interpData : null;

    private static IEnumerable<ExportEntry> GetReferencedExports(ExportEntry export, string propertyName)
    {
        ArrayProperty<ObjectProperty> references = export?.GetProperty<ArrayProperty<ObjectProperty>>(propertyName);
        if (references is null)
        {
            yield break;
        }

        foreach (ObjectProperty reference in references)
        {
            if (export.FileRef.TryGetUExport(reference.Value, out ExportEntry referencedExport))
            {
                yield return referencedExport;
            }
        }
    }

    private static string GetInterpGroupName(ExportEntry group)
        => group.GetProperty<NameProperty>("GroupName")?.Value.Instanced ?? group.ObjectName.Instanced;

    private CurveEditor3DFovModel LoadCameraFovModel(ExportEntry group,
        IDictionary<int, CurveEditor3DFovModel> fovModelsByExport)
    {
        ExportEntry fovTrack = GetReferencedExports(group, "InterpTracks").FirstOrDefault(track =>
            track.ClassName == "InterpTrackFloatProp"
            && (string.Equals(track.GetProperty<NameProperty>("PropertyName")?.Value.Instanced, "FOVAngle",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(track.GetProperty<StrProperty>("TrackTitle")?.Value, "FOVAngle",
                    StringComparison.OrdinalIgnoreCase)));
        if (fovTrack is null)
        {
            return null;
        }
        if (fovModelsByExport.TryGetValue(fovTrack.UIndex, out CurveEditor3DFovModel existingModel))
        {
            return existingModel;
        }

        var fovModel = new CurveEditor3DFovModel();
        fovModel.Load(fovTrack);
        fovModel.Changed += FovModel_Changed;
        fovModelsByExport[fovTrack.UIndex] = fovModel;
        return fovModel;
    }

    private static IEnumerable<ExportEntry> FindDirectorTracks(ExportEntry interpData)
    {
        foreach (ExportEntry group in GetReferencedExports(interpData, "InterpGroups"))
        {
            foreach (ExportEntry track in GetReferencedExports(group, "InterpTracks"))
            {
                if (track.ClassName == "InterpTrackDirector")
                {
                    yield return track;
                }
            }
        }
    }

    private List<DirectorCameraCut> BuildDirectorCameraCuts(ExportEntry directorTrack,
        IReadOnlyDictionary<string, TrackMovePlaybackOption> cameraOptionsByGroup,
        IReadOnlyDictionary<string, ExportEntry> groupsByName)
    {
        ArrayProperty<StructProperty> cutTrack = directorTrack.GetProperty<ArrayProperty<StructProperty>>("CutTrack");
        if (cutTrack is null)
        {
            return [];
        }

        ExportEntry switchCameraTrack = FindSwitchCameraTrack(FindOwningInterpData(directorTrack));

        return cutTrack
            .Select(cut => new
            {
                Time = cut.GetProp<FloatProperty>("Time")?.Value ?? 0,
                GroupName = cut.GetProp<NameProperty>("TargetCamGroup")?.Value.Instanced,
            })
            .Where(cut => !string.IsNullOrWhiteSpace(cut.GroupName))
            .Select(cut =>
            {
                ExportEntry group = groupsByName.GetValueOrDefault(cut.GroupName);
                string actorTag = GetCameraActorTag(group);
                PlacedCameraState placedCamera = !string.IsNullOrWhiteSpace(actorTag)
                    ? dialoguePlacedCameras.GetValueOrDefault(actorTag)
                    : null;
                return new DirectorCameraCut
                {
                    Time = cut.Time,
                    GroupName = cut.GroupName,
                    Camera = cameraOptionsByGroup.GetValueOrDefault(cut.GroupName),
                    SwitchCameraTrack = cut.GroupName.Equals("Conversation", StringComparison.OrdinalIgnoreCase)
                                        && TryResolveSwitchCamera(switchCameraTrack, cut.Time,
                                            useForNextCamera: false, out _)
                        ? switchCameraTrack
                        : null,
                    CameraActorTag = actorTag,
                    CameraActor = placedCamera?.Actor,
                    FallbackOrigin = placedCamera?.Origin,
                    FallbackFovDegrees = placedCamera?.FovDegrees,
                };
            })
            .Where(cut => ShouldRetainDirectorCameraCut(cut.Camera is not null,
                cut.SwitchCameraTrack is not null, cut.CameraActorTag))
            .OrderBy(cut => cut.Time)
            .ToList();
    }

    internal static string GetCameraActorTag(ExportEntry group) =>
        group?.GetProperty<NameProperty>("m_nmSFXFindActor")?.Value.Instanced;

    internal static bool ShouldRetainDirectorCameraCut(bool hasTrackMove, bool hasSwitchCamera,
        string cameraActorTag) =>
        hasTrackMove || hasSwitchCamera || !string.IsNullOrWhiteSpace(cameraActorTag);

    internal static CameraOrigin ResolveDialogueCameraSeed(CameraOrigin? placed, CameraOrigin? authored,
        CameraOrigin? cached, CameraOrigin viewport) =>
        placed ?? authored ?? cached ?? viewport;

    internal static float ResolveDialogueCameraFovSeed(float? placed, float? authored, float? cached,
        float viewport) =>
        placed ?? authored ?? cached ?? viewport;

    private static ExportEntry FindSwitchCameraTrack(ExportEntry interpData) =>
        GetReferencedExports(interpData, "InterpGroups")
            .Where(group => GetInterpGroupName(group).Equals("Conversation", StringComparison.OrdinalIgnoreCase))
            .SelectMany(group => GetReferencedExports(group, "InterpTracks"))
            .FirstOrDefault(track => track.IsA("BioEvtSysTrackSwitchCamera"));

    private bool TryResolveSwitchCamera(ExportEntry switchCameraTrack, float time, bool useForNextCamera,
        out ResolvedSwitchCamera camera)
    {
        camera = null;
        StageConversationContext stageContext = dialogueNodePreview?.StageContext ?? trackAnchorStageContext;
        IReadOnlyDictionary<string, CameraOrigin> stageNodes = dialogueNodePreview?.StageContext.StageNodeOrigins
                                                               ?? trackAnchorStageContext?.StageNodeOrigins;
        ArrayProperty<StructProperty> cameraKeys = switchCameraTrack?
            .GetProperty<ArrayProperty<StructProperty>>("m_aCameras");
        ArrayProperty<StructProperty> trackKeys = switchCameraTrack?
            .GetProperty<ArrayProperty<StructProperty>>("m_aTrackKeys");
        int keyCount = Math.Min(cameraKeys?.Count ?? 0, trackKeys?.Count ?? 0);
        if (stageContext is null || stageNodes is null || keyCount == 0)
        {
            return false;
        }

        float[] keyTimes = trackKeys.Take(keyCount)
            .Select(key => key.GetProp<FloatProperty>("fTime")?.Value ?? 0)
            .ToArray();
        bool[] queuedKeys = cameraKeys.Take(keyCount)
            .Select(key => key.GetProp<BoolProperty>("bUseForNextCamera")?.Value == true)
            .ToArray();
        int keyIndex = GetSwitchCameraKeyIndex(keyTimes, queuedKeys, time, useForNextCamera);
        if (keyIndex < 0)
        {
            return false;
        }
        string stageBone = cameraKeys[keyIndex].GetProp<NameProperty>("nmStageSpecificCam")?.Value.Instanced;
        if (string.IsNullOrWhiteSpace(stageBone) || stageBone.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (stageContext.StageCameras.TryGetValue(stageBone, out StageCameraDefinition stageCamera))
        {
            camera = new ResolvedSwitchCamera(stageCamera.Origin,
                stageCamera.FovDegrees ?? ConversationSwitchCameraFovDegrees);
            return true;
        }
        if (stageNodes.TryGetValue(stageBone, out CameraOrigin stageNode))
        {
            camera = new ResolvedSwitchCamera(stageNode, ConversationSwitchCameraFovDegrees);
            return true;
        }
        return false;
    }

    internal static int GetActiveSwitchCameraKeyIndex(IReadOnlyList<float> keyTimes, float time)
    {
        if (keyTimes is not { Count: > 0 })
        {
            return -1;
        }

        int keyIndex = 0;
        for (int index = 1; index < keyTimes.Count; index++)
        {
            if (keyTimes[index] > time)
            {
                break;
            }
            keyIndex = index;
        }
        return keyIndex;
    }

    internal static int GetSwitchCameraKeyIndex(IReadOnlyList<float> keyTimes,
        IReadOnlyList<bool> useForNextCamera, float time, bool queued)
    {
        int keyCount = Math.Min(keyTimes?.Count ?? 0, useForNextCamera?.Count ?? 0);
        int keyIndex = -1;
        for (int index = 0; index < keyCount; index++)
        {
            if (keyTimes[index] > time)
            {
                break;
            }
            if (useForNextCamera[index] == queued)
            {
                keyIndex = index;
            }
        }
        return keyIndex;
    }

    private static IEnumerable<ExportEntry> FindGestureTracksInInterpData(ExportEntry interpData)
    {
        if (interpData is null)
        {
            yield break;
        }

        ArrayProperty<ObjectProperty> groupRefs = interpData.GetProperty<ArrayProperty<ObjectProperty>>("InterpGroups");
        if (groupRefs is null)
        {
            yield break;
        }

        foreach (ObjectProperty groupRef in groupRefs)
        {
            if (groupRef.ResolveToExport(interpData.FileRef, null) is not ExportEntry group)
            {
                continue;
            }

            ArrayProperty<ObjectProperty> trackRefs = group.GetProperty<ArrayProperty<ObjectProperty>>("InterpTracks");
            if (trackRefs is null)
            {
                continue;
            }

            foreach (ObjectProperty trackRef in trackRefs)
            {
                if (trackRef.ResolveToExport(interpData.FileRef, null) is ExportEntry track
                    && track.IsA("BioEvtSysTrackGesture"))
                {
                    yield return track;
                }
            }
        }
    }

    private async Task InitializePreviewActorModelsAsync()
    {
        if (CurrentLoadedExport is null)
        {
            SetPreviewActorStatus("Select an InterpTrackMove export to load actor models.");
            return;
        }

        MEGame game = CurrentLoadedExport.Game;
        string databasePath = AssetDatabaseWindow.GetDBPath(game);
        if (!File.Exists(databasePath))
        {
            SetPreviewActorStatus($"No {game} Asset Database found. Generate one in the Asset Database tool.");
            return;
        }

        try
        {
            SetPreviewActorStatus($"Loading {game} actor models...");
            var database = new AssetDB();
            await AssetDatabaseWindow.LoadDatabase(databasePath, game, database, CancellationToken.None);
            if (database.DatabaseVersion != AssetDatabaseWindow.dbCurrentBuild)
            {
                SetPreviewActorStatus($"The {game} Asset Database is out of date. Regenerate it to select actor models.");
                return;
            }

            List<MeshRecord> meshes = database.Meshes
                .Where(mesh => mesh.IsSkeleton && mesh.Usages.Count > 0)
                .OrderBy(mesh => mesh.MeshName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            previewAssetDatabase = database;
            previewAssetFiles = database.FileList
                .Select(file => (file.FileName, database.ContentDir[file.DirectoryKey]))
                .ToList();
            previewAssetFilePaths = await AssetDatabaseFilePathResolver.BuildIndexAsync(database, game,
                CancellationToken.None).ConfigureAwait(true);
            previewActorMeshes = meshes;

            if (dialogueNodePreview is not null)
            {
                // Loading the first backdrop resets the Level Editor render caches. Do it before
                // constructing actor materials so their compiled shaders and textures remain live.
                await LoadDialoguePreviewLevelsAsync().ConfigureAwait(true);
                ResolveDialoguePreviewActorConstructions();
            }

            for (int actorIndex = 0; actorIndex < previewActors.Count; actorIndex++)
            {
                PreviewActorConfiguration actor = previewActors[actorIndex];
                HashSet<PreviewActorModelComponent> loadedConstruction = dialogueNodePreview is not null
                    ? LoadCachedActorConstruction(actorIndex, actor)
                    : [];
                foreach (PreviewActorModelComponent component in Enum.GetValues<PreviewActorModelComponent>())
                {
                    if (loadedConstruction.Contains(component))
                    {
                        continue;
                    }
                    if (component is not PreviewActorModelComponent.Body
                        && string.IsNullOrEmpty(GetPreviewActorModelName(actor, component)))
                    {
                        previewActorModels.ElementAtOrDefault(actorIndex)?.Remove(component);
                        continue;
                    }
                    MeshRecord configuredMesh = FindConfiguredPreviewActorMesh(meshes, actor, component);
                    MeshRecord mesh = dialogueNodePreview is not null
                        ? configuredMesh
                        : actor.BaseGameModelsOnly
                            ? configuredMesh
                            : configuredMesh ?? PreviewActorModelDefaults.FindDefaultMesh(meshes, database, component, game);
                    if (mesh is null)
                    {
                        continue;
                    }
                    SetPreviewActorModelName(actor, component, mesh.MeshName);
                    TryLoadPreviewActorModel(actorIndex, component, mesh, actor.BaseGameModelsOnly, out _);
                }
            }
            SynchronizePreviewActorControls();
            if (dialogueNodePreview is not null)
            {
                if (isDialogueConversationPreview)
                {
                    bool cacheReady = false;
                    if (dialogueNodePreview.CachePreset is { } savedPreset)
                    {
                        cacheReady = TryRestoreDialogueCachePreset(savedPreset, out string restoreError);
                        if (!cacheReady)
                        {
                            MessageBox.Show(Window.GetWindow(this),
                                $"The saved cache could not be restored and will be rebuilt.\n\n{restoreError}",
                                "Dialogue Cache", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                    if (!cacheReady && !await BuildDialogueRuntimeCacheAsync().ConfigureAwait(true))
                    {
                        return;
                    }
                    if (!cacheReady && !string.IsNullOrWhiteSpace(dialogueNodePreview.NewCacheLabel))
                    {
                        try
                        {
                            SaveDialogueCachePreset(dialogueNodePreview.NewCacheLabel);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                          or InvalidOperationException or InvalidDataException)
                        {
                            SceneStatus = $"The conversation cache is ready but could not be saved: {exception.Message}";
                        }
                    }
                    ShowDialogueConversationPreviewUi();
                }
                else
                {
                    ConfigureDialoguePreviewPlayback();
                }
                StartDialogueTimelinePlaybackAt(0, reconstruct: true);
            }
            SetPreviewActorStatus(meshes.Count == 0
                ? $"The {game} Asset Database contains no skeletal meshes."
                : $"{meshes.Count:N0} skeletal actor models available.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetPreviewActorStatus($"Unable to load actor models: {exception.Message}");
        }
    }

    private async Task LoadDialoguePreviewLevelsAsync()
    {
        string[] paths = dialogueNodePreview.LevelPaths.Where(File.Exists).ToArray();
        for (int index = 0; index < paths.Length; index++)
        {
            await LoadLevelAsync(paths[index], replace: index == 0).ConfigureAwait(true);
        }
        IndexDialoguePreviewCameras();
    }

    private void IndexDialoguePreviewCameras()
    {
        const float radiansToDegrees = 57.29577951308232f;
        dialoguePreviewInitialCameraOrigin = new CameraOrigin(RenderContext.Camera.Position,
            new Vector3(RenderContext.Camera.Roll, RenderContext.Camera.Pitch, RenderContext.Camera.Yaw)
            * radiansToDegrees);
        dialoguePreviewInitialCameraFovDegrees = RenderContext.Camera.FOV > 0
            ? RenderContext.Camera.FOV * radiansToDegrees
            : 60f;
        dialoguePlacedCameras.Clear();
        dialogueAuthoredCameraDefaults.Clear();
        dialogueLookAtTargets.Clear();

        IEnumerable<IMEPackage> packages = levelPackages;
        if (dialogueNodePreview?.Conversation?.Export?.FileRef is { } conversationPackage
            && levelPackages.All(package => !ReferenceEquals(package, conversationPackage)))
        {
            packages = packages.Append(conversationPackage);
        }

        foreach (IMEPackage package in packages.Distinct())
        {
            // CameraActor is intentionally not a renderable Level Editor proxy, so it never appears
            // in levelActors. Read it from Level.Actors instead; this also covers cameras owned by a
            // selected non-LOC BioD or BioP rather than by the conversation's LOC package.
            foreach (ExportEntry actor in EnumerateLevelActorExports(package))
            {
                if (actor.ClassName.Equals("BioLookAtTarget", StringComparison.OrdinalIgnoreCase)
                    || actor.IsA("BioLookAtTarget"))
                {
                    string lookAtTag = actor.GetProperty<NameProperty>("Tag")?.Value.Instanced;
                    if (!string.IsNullOrWhiteSpace(lookAtTag)
                        && !lookAtTag.Equals("None", StringComparison.OrdinalIgnoreCase))
                    {
                        dialogueLookAtTargets.TryAdd(lookAtTag, ReadPlacedCameraState(actor).Origin);
                    }
                }
                if (!actor.ClassName.Equals("CameraActor", StringComparison.OrdinalIgnoreCase)
                    && !actor.IsA("CameraActor"))
                {
                    continue;
                }
                string actorTag = actor.GetProperty<NameProperty>("Tag")?.Value.Instanced;
                if (string.IsNullOrWhiteSpace(actorTag)
                    || actorTag.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                dialoguePlacedCameras.TryAdd(actorTag, ReadPlacedCameraState(actor));
            }
        }
        IndexDialogueAuthoredCameraDefaults();
        UpdateDirectorCameraFallbacks(availableDirectorTracks);
    }

    private void IndexDialogueAuthoredCameraDefaults()
    {
        // Cam_1/Cam_2 are persistent runtime cameras and commonly have no placed export. A preview
        // may start on a stub before that actor has moved on the selected path, so seed it from the
        // first real camera curve authored for the same tag instead of from an unrelated actor curve.
        IEnumerable<DialogueNodeExtended> nodes = dialogueNodePreview?.Conversation?.EntryList
            .Concat(dialogueNodePreview.Conversation.ReplyList) ?? [];
        foreach (ExportEntry interpData in nodes.SelectMany(GetDialogueNodeInterpDatas)
                     .DistinctBy(interpData => (interpData.FileRef, interpData.UIndex)))
        {
            foreach (ExportEntry group in GetReferencedExports(interpData, "InterpGroups"))
            {
                string actorTag = GetCameraActorTag(group);
                if (string.IsNullOrWhiteSpace(actorTag)
                    || dialogueAuthoredCameraDefaults.ContainsKey(actorTag))
                {
                    continue;
                }
                ExportEntry trackMove = GetReferencedExports(group, "InterpTracks")
                    .FirstOrDefault(track => track.ClassName == "InterpTrackMove");
                if (trackMove is null)
                {
                    continue;
                }
                ExportEntry fovTrack = GetReferencedExports(group, "InterpTracks").FirstOrDefault(track =>
                    track.ClassName == "InterpTrackFloatProp"
                    && (string.Equals(track.GetProperty<NameProperty>("PropertyName")?.Value.Instanced,
                            "FOVAngle", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(track.GetProperty<StrProperty>("TrackTitle")?.Value, "FOVAngle",
                            StringComparison.OrdinalIgnoreCase)));
                if (!IsCameraTrackGroup(group, fovTrack is not null))
                {
                    continue;
                }

                var trackModel = new CurveEditor3DModel();
                trackModel.Load(trackMove);
                if (trackModel.Keyframes is not { Count: > 0 } keys)
                {
                    continue;
                }
                CurveEditor3DFovModel fovModel = null;
                if (fovTrack is not null)
                {
                    fovModel = new CurveEditor3DFovModel();
                    fovModel.Load(fovTrack);
                }
                var option = new TrackMovePlaybackOption
                {
                    Group = group,
                    TrackMove = trackMove,
                    Model = trackModel,
                    FovModel = fovModel,
                };
                float firstTime = keys[0].Time;
                CameraOrigin origin = ResolveCameraTrackOrigin(option, EvaluateTrackMove(option, firstTime));
                float? fovDegrees = fovModel?.Track?.Eval(firstTime, 60f);
                dialogueAuthoredCameraDefaults[actorTag] = new PlacedCameraState(null, origin, fovDegrees);
            }
        }
    }

    private void UpdateDirectorCameraFallbacks(IEnumerable<DirectorPlaybackOption> directorOptions)
    {
        foreach (DirectorCameraCut cut in directorOptions.SelectMany(option => option.Cuts)
                     .Where(cut => cut.Camera is null && cut.SwitchCameraTrack is null
                                                   && !string.IsNullOrWhiteSpace(cut.CameraActorTag)))
        {
            PlacedCameraState placed = dialoguePlacedCameras.GetValueOrDefault(cut.CameraActorTag);
            PlacedCameraState authored = dialogueAuthoredCameraDefaults.GetValueOrDefault(cut.CameraActorTag);
            cut.CameraActor = placed?.Actor;
            cut.FallbackOrigin = ResolveDialogueCameraSeed(placed?.Origin, authored?.Origin, null,
                dialoguePreviewInitialCameraOrigin);
            cut.FallbackFovDegrees = ResolveDialogueCameraFovSeed(placed?.FovDegrees, authored?.FovDegrees,
                null, dialoguePreviewInitialCameraFovDegrees);
        }
    }

    private static IEnumerable<ExportEntry> EnumerateLevelActorExports(IMEPackage package)
    {
        foreach (ExportEntry levelExport in package.Exports.Where(export => export.ClassName == "Level"))
        {
            Level level = levelExport.GetBinaryData<Level>();
            foreach (int actorIndex in level.Actors)
            {
                if (package.TryGetUExport(actorIndex, out ExportEntry actor))
                {
                    yield return actor;
                }
            }
        }
    }

    private static PlacedCameraState ReadPlacedCameraState(ExportEntry actor)
    {
        StructProperty locationProperty = actor.GetProperty<StructProperty>("location")
                                          ?? actor.GetProperty<StructProperty>("Location");
        StructProperty rotationProperty = actor.GetProperty<StructProperty>("Rotation");
        CameraOrigin origin = new(
            locationProperty is null ? Vector3.Zero : CommonStructs.GetVector3(locationProperty),
            rotationProperty is null ? Vector3.Zero : CommonStructs.GetRotator(rotationProperty).GetDegreesVector());
        float authoredFov = actor.GetProperty<FloatProperty>("FOVAngle")?.Value ?? 0;
        float? fovDegrees = authoredFov is > 0 and < 180 ? authoredFov : null;
        return new PlacedCameraState(actor, origin, fovDegrees);
    }

    private void ShowDialogueConversationPreviewUi()
    {
        DialoguePreviewActorPanel.Visibility = Visibility.Visible;
        DialoguePreviewActorPanelSplitter.Visibility = Visibility.Visible;
        DialogueTimelinePanel.Visibility = Visibility.Visible;
        PreviewEditorPanel.Visibility = Visibility.Visible;
    }

    private async Task<bool> BuildDialogueRuntimeCacheAsync()
    {
        if (!isDialogueConversationPreview || dialogueTimelineSegments.Count == 0)
        {
            return false;
        }

        PauseDialogueTimeline();
        dialogueRuntimeCache.Clear();
        buildingDialogueRuntimeCache = true;
        suppressDialogueCacheEditTracking = true;
        loadingDialogueTimelineSegment = true;
        DialogueTimelinePanel.IsEnabled = false;
        DialogueNodeCommitButton.IsEnabled = false;
        DialogueCacheLoadingOverlay.Visibility = Visibility.Visible;
        SceneViewer.Visibility = Visibility.Hidden;
        ActorPlaybackTrackZCheckBox.IsChecked = true;
        bool completed = false;
        try
        {
            for (int index = 0; index < dialogueTimelineSegments.Count; index++)
            {
                DialogueTimelineSegment segment = dialogueTimelineSegments[index];
                DialogueCacheLoadingText.Text =
                    $"Caching {segment.NodeLabel} ({index + 1:N0} of {dialogueTimelineSegments.Count:N0})...";
                await Dispatcher.Yield(DispatcherPriority.Background);

                dialogueNodePreview = dialogueNodePreview with
                {
                    Node = segment.Node,
                    VoStartTime = GetDialogueNodeVoStartTime(segment.Node)
                };
                IReadOnlyList<ExportEntry> interpDatas = GetDialogueNodeInterpDatas(segment.Node);
                var interpRuntimes = new List<DialogueSegmentRuntime>();
                foreach (ExportEntry interpData in interpDatas.DefaultIfEmpty())
                {
                    ExportEntry trackMove = FindDialoguePreviewTrackMove(interpData);
                    if (trackMove is not null)
                    {
                        LoadExport(trackMove);
                    }
                    else
                    {
                        ResetTrackPlaybackOptionsForCachedNode();
                        RefreshMulticamPlaybackOptions(null, interpData);
                        RefreshAvailableGestureTracksForInterpData(interpData);
                    }

                    bool hasCameraPlayback = availableDirectorTracks.Any(option =>
                        option.DirectorTrack is not null && option.Cuts.Count > 0);
                    ConfigureDialoguePreviewPlayback(configureTrackPlayback: trackMove is not null || hasCameraPlayback,
                        interpDataOverride: interpData);
                    interpRuntimes.Add(CaptureDialogueSegmentRuntime(segment));
                }
                DialogueSegmentRuntime runtime = MergeDialogueSegmentRuntimes(segment, interpRuntimes);
                RepairMissingActorAssignments(runtime);
                dialogueRuntimeCache[segment] = runtime;
            }

            BuildDialogueRuntimeActorSnapshots();
            activeDialogueTimelineSegment = null;
            activeDialogueSegmentRuntime = null;
            ResetDialogueTimelineActorGestures();
            completed = true;
            return true;
        }
        catch (Exception exception)
        {
            dialogueRuntimeCache.Clear();
            activeDialogueTimelineSegment = null;
            activeDialogueSegmentRuntime = null;
            DialogueCacheLoadingText.Text = $"Unable to cache the conversation: {exception.Message}";
            SetPreviewActorStatus(DialogueCacheLoadingText.Text);
            return false;
        }
        finally
        {
            loadingDialogueTimelineSegment = false;
            buildingDialogueRuntimeCache = false;
            suppressDialogueCacheEditTracking = false;
            DialogueTimelinePanel.IsEnabled = completed;
            DialogueCacheLoadingOverlay.Visibility = completed ? Visibility.Collapsed : Visibility.Visible;
            SceneViewer.Visibility = completed ? Visibility.Visible : Visibility.Hidden;
            UpdateDialogueNodeCommitButton();
        }
    }

    private bool IsDialogueCachePresetCompatible(DialogueCachePreset preset)
    {
        if (!isDialogueConversationPreview || preset is null || dialogueNodePreview?.Conversation?.Export is not { } conversationExport
            || dialogueTimelineStartSegment is null)
        {
            return false;
        }

        string sourcePath = conversationExport.FileRef.FilePath;
        if (string.IsNullOrWhiteSpace(sourcePath)
            || !DialogueCachePathsEqual(preset.SourceFilePath, sourcePath)
            || preset.Game != conversationExport.Game
            || preset.DialogueUIndex != conversationExport.UIndex
            || !string.Equals(preset.DialogueExportPath, conversationExport.InstancedFullPath,
                StringComparison.OrdinalIgnoreCase)
            || preset.StartNodeIsReply != dialogueTimelineStartSegment.Reference.IsReply
            || preset.StartNodeIndex != dialogueTimelineStartSegment.Reference.Index
            || preset.Nodes.Count != dialogueTimelineSegments.Count)
        {
            return false;
        }

        return dialogueTimelineSegments.All(segment => preset.Nodes.Any(node =>
            node.IsReply == segment.Reference.IsReply
            && node.NodeIndex == segment.Reference.Index
            && node.LineStrRef == segment.Node.LineStrRef));
    }

    private static bool DialogueCachePathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                          or PathTooLongException)
        {
            return false;
        }
    }

    private DialogueCachePreset SaveDialogueCachePreset(string label)
    {
        if (!isDialogueConversationPreview || dialogueRuntimeCache.Count != dialogueTimelineSegments.Count
            || dialogueNodePreview?.Conversation?.Export is not { } conversationExport)
        {
            throw new InvalidOperationException("The entire conversation cache must be ready before it can be saved.");
        }

        string sourcePath = conversationExport.FileRef.FilePath;
        var sourceInfo = new FileInfo(sourcePath);
        var preset = new DialogueCachePreset
        {
            Label = label,
            SourceFilePath = sourcePath,
            PccName = Path.GetFileName(sourcePath),
            DialogueName = dialogueNodePreview.Conversation.ConvName,
            DialogueExportPath = conversationExport.InstancedFullPath,
            DialogueUIndex = conversationExport.UIndex,
            Game = conversationExport.Game,
            SourceLastWriteUtc = sourceInfo.Exists ? sourceInfo.LastWriteTimeUtc : DateTime.MinValue,
            SourceFileSize = sourceInfo.Exists ? sourceInfo.Length : 0,
            StartNodeIsReply = dialogueTimelineStartSegment.Reference.IsReply,
            StartNodeIndex = dialogueTimelineStartSegment.Reference.Index,
            PlayerGender = dialogueNodePreview.PlayerSelection.Gender,
            LevelPaths = dialogueNodePreview.LevelPaths.Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Actors = previewActors.Where(actor => actor.Construction is not null)
                .Select(actor => actor.Construction).ToList(),
            Nodes = dialogueTimelineSegments.Select(CaptureDialogueCacheNode).ToList(),
        };
        loadedDialogueCachePreset = SavedDialogueCachePresetManager.Save(preset);
        SceneStatus = $"Saved dialogue cache preset '{loadedDialogueCachePreset.Label}'.";
        return loadedDialogueCachePreset;
    }

    private DialogueCacheNodePreset CaptureDialogueCacheNode(DialogueTimelineSegment segment)
    {
        DialogueSegmentRuntime runtime = dialogueRuntimeCache[segment];
        return new DialogueCacheNodePreset
        {
            IsReply = segment.Reference.IsReply,
            NodeIndex = segment.Reference.Index,
            LineStrRef = segment.Node.LineStrRef,
            InterpDatas = GetDialogueNodeInterpDatas(segment.Node).Select(CreateExportReference).ToList(),
            PrimaryTrackMove = CreateExportReference(runtime.PrimaryTrackMove?.TrackMove),
            TrackMoves = runtime.TrackMoves.Where(option => option.TrackMove is not null)
                .Select(option => new DialogueTrackMoveCache
                {
                    DisplayName = option.DisplayName,
                    TabDisplayName = option.TabDisplayName,
                    Group = CreateExportReference(option.Group),
                    TrackMove = CreateExportReference(option.TrackMove),
                    Model = option.Model?.CreateCacheSnapshot(),
                    FovExport = CreateExportReference(option.FovModel?.Export),
                    FovModel = option.FovModel?.CreateCacheSnapshot(),
                }).ToList(),
            ExtraTrackMoves = runtime.ExtraTrackMoves.Where(option => option.TrackMove is not null)
                .Select(option => CreateExportReference(option.TrackMove)).ToList(),
            DirectorTracks = runtime.DirectorTracks.Where(option => option.DirectorTrack is not null)
                .Select(option => new DialogueDirectorCache
                {
                    DisplayName = option.DisplayName,
                    DirectorTrack = CreateExportReference(option.DirectorTrack),
                    Cuts = option.Cuts.Select(cut => new DialogueDirectorCutCache
                    {
                        Time = cut.Time,
                        GroupName = cut.GroupName,
                        CameraTrack = CreateExportReference(cut.Camera?.TrackMove),
                        SwitchCameraTrack = CreateExportReference(cut.SwitchCameraTrack),
                        CameraActorTag = cut.CameraActorTag,
                        CameraActor = CreateExportReference(cut.CameraActor),
                        FallbackOrigin = cut.FallbackOrigin is { } fallbackOrigin
                            ? CreateOriginCache(fallbackOrigin)
                            : null,
                        FallbackFovDegrees = cut.FallbackFovDegrees,
                    }).ToList(),
                }).ToList(),
            CameraTracks = runtime.CameraTracks.Where(option => option.TrackMove is not null)
                .Select(option => CreateExportReference(option.TrackMove)).ToList(),
            GestureTracks = runtime.GestureTracks.Where(option => option.Track is not null)
                .Select(CaptureGestureTrack).ToList(),
            ActorTrackAssignments = runtime.ActorTrackAssignments
                .Where(pair => pair.Value?.TrackMove is not null)
                .ToDictionary(pair => pair.Key, pair => CreateExportReference(pair.Value.TrackMove),
                    StringComparer.OrdinalIgnoreCase),
            ActorGestureAssignments = runtime.ActorGestureAssignments
                .Where(pair => pair.Value?.Track is not null)
                .ToDictionary(pair => pair.Key, pair => CreateExportReference(pair.Value.Track),
                    StringComparer.OrdinalIgnoreCase),
            DirectionTracks = runtime.DirectionTracks.Select(track => new DialogueDirectionTrackCache
            {
                ActorTag = track.Actor.ActorTag,
                IsLookAt = track.IsLookAt,
                Keys = track.Keys.Select(key => new DialogueDirectionKeyCache
                {
                    Time = key.Time,
                    Enabled = key.Enabled,
                    TargetActorTag = key.TargetActorTag,
                    TargetStageNode = key.TargetStageNode,
                    OrientationOffset = key.OrientationOffset,
                }).ToList(),
            }).ToList(),
            FaceOnlyVoEvents = runtime.FaceOnlyVoEvents.Select(faceOnlyVo =>
            {
                int nodeIndex = faceOnlyVo.Node.IsReply
                    ? dialogueNodePreview.Conversation.ReplyList.IndexOf(faceOnlyVo.Node)
                    : dialogueNodePreview.Conversation.EntryList.IndexOf(faceOnlyVo.Node);
                return new DialogueFaceOnlyVoCache
                {
                    StartTime = faceOnlyVo.StartTime,
                    Track = CreateExportReference(faceOnlyVo.Track),
                    Group = CreateExportReference(faceOnlyVo.Group),
                    NodeIsReply = faceOnlyVo.Node.IsReply,
                    NodeIndex = nodeIndex,
                    LineStrRef = faceOnlyVo.Node.LineStrRef,
                    ActorTag = faceOnlyVo.Actor?.ActorTag,
                };
            }).ToList(),
            DialogueAudio = CreateExportReference(runtime.DialogueAudio),
            StartActorOrigins = runtime.StartActorOrigins.ToDictionary(pair => pair.Key,
                pair => CreateOriginCache(pair.Value), StringComparer.OrdinalIgnoreCase),
            EndActorOrigins = runtime.EndActorOrigins.ToDictionary(pair => pair.Key,
                pair => CreateOriginCache(pair.Value), StringComparer.OrdinalIgnoreCase),
            ActorOriginOverrides = runtime.ActorOriginOverrides.ToDictionary(pair => pair.Key,
                pair => CreateOriginCache(pair.Value), StringComparer.OrdinalIgnoreCase),
            StartActorGesturePoses = runtime.StartActorGesturePoses.ToDictionary(pair => pair.Key,
                pair => pair.Value.Select(DialogueMatrixCache.FromMatrix).ToList(), StringComparer.OrdinalIgnoreCase),
            EndActorGesturePoses = runtime.EndActorGesturePoses.ToDictionary(pair => pair.Key,
                pair => pair.Value.Select(DialogueMatrixCache.FromMatrix).ToList(), StringComparer.OrdinalIgnoreCase),
            HasPendingPreviewChanges = runtime.HasPendingPreviewChanges,
        };
    }

    private static DialogueGestureTrackCache CaptureGestureTrack(GestureTrackOption option) => new()
    {
        DisplayName = option.DisplayName,
        Status = option.Status,
        Group = CreateExportReference(option.Group),
        Track = CreateExportReference(option.Track),
        StartingPose = option.StartingPose?.AnimationExport is null ? null : new DialogueGestureStartingPoseCache
        {
            Animation = CreateExportReference(option.StartingPose.AnimationExport),
            Settings = CaptureGestureSettings(option.StartingPose.Settings),
        },
        Timeline = option.Timeline.Where(clip => clip.AnimationExport is not null)
            .Select(clip => new DialogueGestureClipCache
            {
                Animation = CreateExportReference(clip.AnimationExport),
                StartTime = clip.StartTime,
                EndTime = clip.EndTime,
                AnimationStartTime = clip.AnimationStartTime,
                AnimationEndTime = clip.AnimationEndTime,
                PlayRate = clip.PlayRate,
                BlendInDuration = clip.BlendInDuration,
                BlendOutDuration = clip.BlendOutDuration,
                Weight = clip.Weight,
                Loop = clip.Loop,
                IsBaseLayer = clip.IsBaseLayer,
                IsTransition = clip.IsTransition,
                UseMotionBoneMask = clip.UseMotionBoneMask,
            }).ToList(),
    };

    private static DialogueGestureSettingsCache CaptureGestureSettings(
        GesturePreviewExportLoader.GesturePlaybackSettings settings) => settings is null ? null : new()
    {
        PlayRate = settings.PlayRate,
        StartOffset = settings.StartOffset,
        EndOffset = settings.EndOffset,
        StartBlendDuration = settings.StartBlendDuration,
        EndBlendDuration = settings.EndBlendDuration,
        Weight = settings.Weight,
        TransitionBlendTime = settings.TransitionBlendTime,
        InvalidData = settings.InvalidData,
        OneShotAnimation = settings.OneShotAnimation,
        ChainToPrevious = settings.ChainToPrevious,
        PlayUntilNext = settings.PlayUntilNext,
        TerminateAllGestures = settings.TerminateAllGestures,
        UseDynamicAnimationSets = settings.UseDynamicAnimationSets,
        SnapToPose = settings.SnapToPose,
        PoseFilter = settings.PoseFilter,
        Pose = settings.Pose,
        GestureFilter = settings.GestureFilter,
        Gesture = settings.Gesture,
        ChainedGestures = settings.ChainedGestures,
    };

    private bool TryRestoreDialogueCachePreset(DialogueCachePreset preset, out string error)
    {
        error = null;
        if (!IsDialogueCachePresetCompatible(preset))
        {
            error = "The cache identity does not match this PCC, dialogue, starting node, or conversation tree.";
            return false;
        }

        PauseDialogueTimeline();
        dialogueRuntimeCache.Clear();
        dialogueNodeInterpDataCache.Clear();
        buildingDialogueRuntimeCache = true;
        suppressDialogueCacheEditTracking = true;
        loadingDialogueTimelineSegment = true;
        DialogueTimelinePanel.IsEnabled = false;
        DialogueCacheLoadingOverlay.Visibility = Visibility.Visible;
        DialogueCacheLoadingText.Text = $"Loading saved cache '{preset.Label}'...";
        SceneViewer.Visibility = Visibility.Hidden;
        bool completed = false;
        try
        {
            LoadDialoguePreviewFaceFxAssets();
            foreach (DialogueTimelineSegment segment in dialogueTimelineSegments)
            {
                DialogueCacheNodePreset nodePreset = preset.Nodes.Single(node =>
                    node.IsReply == segment.Reference.IsReply && node.NodeIndex == segment.Reference.Index);
                IReadOnlyList<ExportEntry> interpDatas = nodePreset.InterpDatas
                    .Select(reference => ResolveExportReference(reference, required: true)).ToArray();
                dialogueNodeInterpDataCache[segment.Node] = interpDatas;
                dialogueNodePreview = dialogueNodePreview with
                {
                    Node = segment.Node,
                    VoStartTime = interpDatas.Select(GetDialoguePreviewVoStartTime).Where(time => time > 0)
                        .DefaultIfEmpty(0).Min(),
                };
                dialogueRuntimeCache[segment] = RestoreDialogueCacheNode(segment, nodePreset);
            }

            // Actor assignments are revalidated while each node is restored (for example, old
            // presets may have cached Owner or Player against a camera TrackMove). Start/end origins are
            // derived state, so serialized snapshots based on an invalid assignment must not be
            // reused. Re-project them from the already-loaded cache; no PCC, curve, or animation
            // assets are rebuilt here.
            BuildDialogueRuntimeActorSnapshots();

            activeDialogueTimelineSegment = null;
            activeDialogueSegmentRuntime = null;
            ResetDialogueTimelineActorGestures();
            loadedDialogueCachePreset = preset;
            completed = true;
            FileInfo sourceInfo = new(preset.SourceFilePath);
            bool sourceChanged = sourceInfo.Exists
                                 && (sourceInfo.Length != preset.SourceFileSize
                                     || sourceInfo.LastWriteTimeUtc > preset.SourceLastWriteUtc.AddSeconds(1));
            SceneStatus = sourceChanged
                ? $"Loaded '{preset.Label}'. The source PCC changed after this cache was saved; export references were revalidated."
                : $"Loaded dialogue cache preset '{preset.Label}'.";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                          or InvalidDataException or InvalidOperationException
                                          or JsonException)
        {
            dialogueRuntimeCache.Clear();
            dialogueNodeInterpDataCache.Clear();
            activeDialogueTimelineSegment = null;
            activeDialogueSegmentRuntime = null;
            error = exception.Message;
            return false;
        }
        finally
        {
            loadingDialogueTimelineSegment = false;
            buildingDialogueRuntimeCache = false;
            suppressDialogueCacheEditTracking = false;
            DialogueTimelinePanel.IsEnabled = completed;
            DialogueCacheLoadingOverlay.Visibility = completed ? Visibility.Collapsed : Visibility.Visible;
            SceneViewer.Visibility = completed ? Visibility.Visible : Visibility.Hidden;
            UpdateDialogueNodeCommitButton();
        }
    }

    private DialogueSegmentRuntime RestoreDialogueCacheNode(DialogueTimelineSegment segment,
        DialogueCacheNodePreset preset)
    {
        var trackMoves = new List<TrackMovePlaybackOption>();
        var trackMovesByReference = new Dictionary<string, TrackMovePlaybackOption>(StringComparer.OrdinalIgnoreCase);
        var fovModelsByReference = new Dictionary<string, CurveEditor3DFovModel>(StringComparer.OrdinalIgnoreCase);
        foreach (DialogueTrackMoveCache cachedTrack in preset.TrackMoves)
        {
            ExportEntry trackExport = ResolveExportReference(cachedTrack.TrackMove, required: true);
            var trackModel = new CurveEditor3DModel { AutoCommit = false };
            trackModel.LoadCacheSnapshot(trackExport, cachedTrack.Model ?? throw new InvalidDataException(
                $"{segment.NodeLabel} is missing a TrackMove curve snapshot."));
            trackModel.Changed += Model_Changed;
            CurveEditor3DFovModel fovModel = null;
            if (cachedTrack.FovExport is not null)
            {
                string fovKey = GetExportReferenceKey(cachedTrack.FovExport);
                if (!fovModelsByReference.TryGetValue(fovKey, out fovModel))
                {
                    ExportEntry fovExport = ResolveExportReference(cachedTrack.FovExport, required: true);
                    fovModel = new CurveEditor3DFovModel { AutoCommit = false };
                    fovModel.LoadCacheSnapshot(fovExport, cachedTrack.FovModel
                        ?? throw new InvalidDataException($"{segment.NodeLabel} is missing an FOV curve snapshot."));
                    fovModel.Changed += FovModel_Changed;
                    fovModelsByReference[fovKey] = fovModel;
                }
            }
            var option = new TrackMovePlaybackOption
            {
                DisplayName = cachedTrack.DisplayName,
                TabDisplayName = cachedTrack.TabDisplayName,
                Group = ResolveExportReference(cachedTrack.Group, required: true),
                TrackMove = trackExport,
                Model = trackModel,
                FovModel = fovModel,
            };
            trackMoves.Add(option);
            trackMovesByReference[GetExportReferenceKey(cachedTrack.TrackMove)] = option;
        }

        TrackMovePlaybackOption FindTrack(PackageExportReference reference)
        {
            if (reference is null)
            {
                return null;
            }
            if (!trackMovesByReference.TryGetValue(GetExportReferenceKey(reference), out TrackMovePlaybackOption option))
            {
                throw new InvalidDataException($"{segment.NodeLabel} is missing cached TrackMove {reference.InstancedFullPath}.");
            }
            return option;
        }

        var gestureTracks = preset.GestureTracks.Select(RestoreGestureTrack).ToArray();
        var gesturesByReference = gestureTracks.ToDictionary(option => GetExportReferenceKey(CreateExportReference(option.Track)),
            StringComparer.OrdinalIgnoreCase);
        GestureTrackOption FindGesture(PackageExportReference reference)
        {
            if (reference is null)
            {
                return null;
            }
            if (!gesturesByReference.TryGetValue(GetExportReferenceKey(reference), out GestureTrackOption option))
            {
                throw new InvalidDataException($"{segment.NodeLabel} is missing cached gesture {reference.InstancedFullPath}.");
            }
            return option;
        }
        var faceOnlyVoEvents = preset.FaceOnlyVoEvents.Select(item =>
        {
            ExportEntry track = ResolveExportReference(item.Track, required: true);
            DialogueNodeExtended node = item.NodeIndex >= 0
                ? GetDialogueNode(item.NodeIsReply, item.NodeIndex)
                : ResolveCachedFaceOnlyVoNode(track, item.StartTime, item.LineStrRef);
            return new FaceOnlyVoEvent(item.StartTime, track,
                ResolveExportReference(item.Group, required: true), node, FindPreviewActorByTag(item.ActorTag));
        }).Where(item => item.Node is not null && item.Actor is not null).ToArray();
        PreviewActorConfiguration speakingActor = FindPreviewActorByTag(segment.Node.SpeakerTag?.SpeakerName);
        DialogueFaceFxBinding mainFaceFx = CreateDialogueFaceFxBinding(segment.Node, speakingActor,
            dialogueNodePreview.VoStartTime);
        var fovoFaceFx = faceOnlyVoEvents.Select(item => new
            {
                Event = item,
                Binding = CreateDialogueFaceFxBinding(item.Node, item.Actor, item.StartTime),
            })
            .Where(item => item.Binding is not null)
            .ToDictionary(item => item.Event, item => item.Binding);
        var runtime = new DialogueSegmentRuntime
        {
            Segment = segment,
            PrimaryTrackMove = FindTrack(preset.PrimaryTrackMove),
            TrackMoves = trackMoves,
            ExtraTrackMoves = preset.ExtraTrackMoves.Select(FindTrack).Where(option => option is not null)
                .Prepend(TrackMovePlaybackOption.None).ToArray(),
            DirectorTracks = preset.DirectorTracks.Select(director => new DirectorPlaybackOption
                {
                    DisplayName = director.DisplayName,
                    DirectorTrack = ResolveExportReference(director.DirectorTrack, required: true),
                    Cuts = director.Cuts.Select(cut => new DirectorCameraCut
                    {
                        Time = cut.Time,
                        GroupName = cut.GroupName,
                        Camera = FindTrack(cut.CameraTrack),
                        SwitchCameraTrack = ResolveExportReference(cut.SwitchCameraTrack,
                            required: cut.SwitchCameraTrack is not null),
                        CameraActorTag = cut.CameraActorTag,
                        CameraActor = ResolveExportReference(cut.CameraActor, required: false),
                        FallbackOrigin = cut.FallbackOrigin is null
                            ? null
                            : new CameraOrigin(cut.FallbackOrigin.Location, cut.FallbackOrigin.Rotation),
                        FallbackFovDegrees = cut.FallbackFovDegrees,
                    }).ToArray(),
                }).Prepend(DirectorPlaybackOption.None).ToArray(),
            CameraTracks = preset.CameraTracks.Select(FindTrack)
                .Where(option => option is not null)
                // Older presets only recorded director/Cam*-named tracks as cameras. FOV is
                // authored on camera groups such as E10's `pcam`, so use it to repair their
                // classification before validating Owner/Player assignments.
                .Concat(trackMoves.Where(option => IsCameraTrackGroup(option.Group,
                    option.FovModel is not null)))
                .DistinctBy(option => (option.TrackMove.FileRef, option.TrackMove.UIndex))
                .ToArray(),
            GestureTracks = gestureTracks.Prepend(GestureTrackOption.None).ToArray(),
            ActorTrackAssignments = preset.ActorTrackAssignments
                .Select(pair => (pair.Key, Track: FindTrack(pair.Value)))
                .Where(pair => pair.Track is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Track, StringComparer.OrdinalIgnoreCase),
            ActorGestureAssignments = preset.ActorGestureAssignments
                .Select(pair => (pair.Key, Gesture: FindGesture(pair.Value)))
                .Where(pair => pair.Gesture is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Gesture, StringComparer.OrdinalIgnoreCase),
            DirectionTracks = preset.DirectionTracks.Select(track => new ActorDirectionTrack(
                FindPreviewActorByTag(track.ActorTag), track.IsLookAt,
                track.Keys.Select(key => new ActorDirectionKey(key.Time, key.Enabled, key.TargetActorTag,
                    key.TargetStageNode, key.OrientationOffset)).ToArray()))
                .Where(track => track.Actor is not null).ToArray(),
            FaceOnlyVoEvents = faceOnlyVoEvents,
            DialogueAudio = ResolveExportReference(preset.DialogueAudio, required: preset.DialogueAudio is not null),
            MainFaceFx = mainFaceFx,
            FaceOnlyVoFaceFx = fovoFaceFx,
            HasPendingPreviewChanges = preset.HasPendingPreviewChanges,
        };
        CopyOrigins(preset.StartActorOrigins, runtime.StartActorOrigins);
        CopyOrigins(preset.EndActorOrigins, runtime.EndActorOrigins);
        CopyOrigins(preset.ActorOriginOverrides, runtime.ActorOriginOverrides);
        CopyPoses(preset.StartActorGesturePoses, runtime.StartActorGesturePoses);
        CopyPoses(preset.EndActorGesturePoses, runtime.EndActorGesturePoses);
        RepairMissingActorAssignments(runtime);
        return runtime;
    }

    private void RepairMissingActorAssignments(DialogueSegmentRuntime runtime)
    {
        ExportEntry[] cameraTrackMoves = runtime.CameraTracks
            .Where(option => option?.TrackMove is not null)
            .Select(option => option.TrackMove)
            .ToArray();
        foreach (PreviewActorConfiguration actor in previewActors)
        {
            if (runtime.ActorTrackAssignments.TryGetValue(actor.ActorTag,
                    out TrackMovePlaybackOption assignedTrackMove)
                && (!IsEligibleActorTrackMove(assignedTrackMove.TrackMove, cameraTrackMoves)
                    || !IsEligibleActorTrackGroup(assignedTrackMove.Group, actor.ActorTag)))
            {
                // Camera groups can share a stage transform with Owner/Player. That makes the
                // transform useful as an actor-tag alias, but it must never attach the actor to
                // the camera spline. This also repairs presets saved before the distinction was
                // enforced.
                runtime.ActorTrackAssignments.Remove(actor.ActorTag);
            }

            if (!runtime.ActorTrackAssignments.ContainsKey(actor.ActorTag))
            {
                TrackMovePlaybackOption trackMove = runtime.TrackMoves
                    .Where(option => option?.TrackMove is not null)
                    .Where(option => IsEligibleActorTrackMove(option.TrackMove, cameraTrackMoves))
                    .Where(option => IsEligibleActorTrackGroup(option.Group, actor.ActorTag))
                    .Select(option => new
                    {
                        Option = option,
                        Score = GetActorGroupMatchScore(option.Group, actor.ActorTag),
                    })
                    .Where(candidate => candidate.Score > 0)
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenByDescending(candidate => GetAuthoredMovementRank(candidate.Option))
                    .Select(candidate => candidate.Option)
                    .FirstOrDefault();
                if (trackMove is not null)
                {
                    runtime.ActorTrackAssignments[actor.ActorTag] = trackMove;
                }
            }

            if (!runtime.ActorGestureAssignments.ContainsKey(actor.ActorTag))
            {
                GestureTrackOption gesture = runtime.GestureTracks
                    .Where(option => option?.Track is not null)
                    .Select(option => new
                    {
                        Option = option,
                        Score = GetGestureActorMatchScore(option, actor.ActorTag),
                    })
                    .Where(candidate => candidate.Score > 0)
                    .OrderByDescending(candidate => candidate.Score)
                    .Select(candidate => candidate.Option)
                    .FirstOrDefault();
                if (gesture is not null)
                {
                    runtime.ActorGestureAssignments[actor.ActorTag] = gesture;
                }
            }
        }
    }

    private GestureTrackOption RestoreGestureTrack(DialogueGestureTrackCache cached)
    {
        GesturePreviewExportLoader.GestureAnimationItem startingPose = cached.StartingPose?.Animation is null
            ? null
            : new GesturePreviewExportLoader.GestureAnimationItem
            {
                AnimationExport = ResolveExportReference(cached.StartingPose.Animation, required: true),
                Settings = RestoreGestureSettings(cached.StartingPose.Settings),
            };
        return new GestureTrackOption
        {
            DisplayName = cached.DisplayName,
            Status = cached.Status,
            Group = ResolveExportReference(cached.Group, required: true),
            Track = ResolveExportReference(cached.Track, required: true),
            StartingPose = startingPose,
            Timeline = cached.Timeline.Select(clip => new AnimationPreviewControl.AnimationTimelineClip
            {
                AnimationExport = ResolveExportReference(clip.Animation, required: true),
                StartTime = clip.StartTime,
                EndTime = clip.EndTime,
                AnimationStartTime = clip.AnimationStartTime,
                AnimationEndTime = clip.AnimationEndTime,
                PlayRate = clip.PlayRate,
                BlendInDuration = clip.BlendInDuration,
                BlendOutDuration = clip.BlendOutDuration,
                Weight = clip.Weight,
                Loop = clip.Loop,
                IsBaseLayer = clip.IsBaseLayer,
                IsTransition = clip.IsTransition,
                UseMotionBoneMask = clip.UseMotionBoneMask,
            }).ToArray(),
        };
    }

    private static GesturePreviewExportLoader.GesturePlaybackSettings RestoreGestureSettings(
        DialogueGestureSettingsCache settings) => settings is null ? null : new()
    {
        PlayRate = settings.PlayRate,
        StartOffset = settings.StartOffset,
        EndOffset = settings.EndOffset,
        StartBlendDuration = settings.StartBlendDuration,
        EndBlendDuration = settings.EndBlendDuration,
        Weight = settings.Weight,
        TransitionBlendTime = settings.TransitionBlendTime,
        InvalidData = settings.InvalidData,
        OneShotAnimation = settings.OneShotAnimation,
        ChainToPrevious = settings.ChainToPrevious,
        PlayUntilNext = settings.PlayUntilNext,
        TerminateAllGestures = settings.TerminateAllGestures,
        UseDynamicAnimationSets = settings.UseDynamicAnimationSets,
        SnapToPose = settings.SnapToPose,
        PoseFilter = settings.PoseFilter,
        Pose = settings.Pose,
        GestureFilter = settings.GestureFilter,
        Gesture = settings.Gesture,
        ChainedGestures = settings.ChainedGestures,
    };

    private DialogueNodeExtended GetDialogueNode(bool isReply, int index)
    {
        IReadOnlyList<DialogueNodeExtended> nodes = isReply
            ? dialogueNodePreview.Conversation.ReplyList
            : dialogueNodePreview.Conversation.EntryList;
        return index >= 0 && index < nodes.Count ? nodes[index] : null;
    }

    private DialogueNodeExtended ResolveCachedFaceOnlyVoNode(ExportEntry track, float startTime, int lineStrRef)
    {
        ArrayProperty<StructProperty> trackKeys = track?.GetProperty<ArrayProperty<StructProperty>>("m_aTrackKeys");
        ArrayProperty<StructProperty> voKeys = track?.GetProperty<ArrayProperty<StructProperty>>("m_aFOVOKeys");
        int count = Math.Min(trackKeys?.Count ?? 0, voKeys?.Count ?? 0);
        for (int index = 0; index < count; index++)
        {
            float keyTime = trackKeys[index].GetProp<FloatProperty>("fTime")?.Value ?? 0;
            int keyLineStrRef = voKeys[index].GetProp<IntProperty>("nLineStrRef")?.Value ?? 0;
            if (MathF.Abs(keyTime - startTime) > 0.0001f || keyLineStrRef != lineStrRef)
            {
                continue;
            }
            int conversationIndex = voKeys[index].GetProp<ObjectProperty>("pConversation")?.Value ?? 0;
            return ResolveFaceOnlyVoNode(track.FileRef.GetEntry(conversationIndex), lineStrRef);
        }
        return null;
    }

    private static PackageExportReference CreateExportReference(ExportEntry export) => export is null ? null : new()
    {
        PackagePath = export.FileRef.FilePath,
        UIndex = export.UIndex,
        InstancedFullPath = export.InstancedFullPath,
        ClassName = export.ClassName,
    };

    private static string GetExportReferenceKey(PackageExportReference reference) => reference is null
        ? string.Empty
        : $"{reference.PackagePath}|{reference.UIndex}";

    private ExportEntry ResolveExportReference(PackageExportReference reference, bool required)
    {
        if (reference is null)
        {
            if (required)
            {
                throw new InvalidDataException("The cache is missing a required export reference.");
            }
            return null;
        }

        IMEPackage package = new[] { dialoguePreviewWorkingPackage, dialogueNodePreview.Conversation.Export.FileRef }
            .Where(candidate => candidate is not null)
            .Concat(levelPackages)
            .FirstOrDefault(candidate => string.Equals(candidate.FilePath, reference.PackagePath,
                StringComparison.OrdinalIgnoreCase));
        package ??= previewActorGesturePackageCache.GetCachedPackage(reference.PackagePath);
        if (package is null || !package.IsUExport(reference.UIndex))
        {
            if (required)
            {
                throw new InvalidDataException($"Cached export {reference.UIndex} could not be resolved from {reference.PackagePath}.");
            }
            return null;
        }

        ExportEntry export = package.GetUExport(reference.UIndex);
        if ((!string.IsNullOrWhiteSpace(reference.ClassName)
             && !string.Equals(export.ClassName, reference.ClassName, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(reference.InstancedFullPath)
                && !string.Equals(export.InstancedFullPath, reference.InstancedFullPath,
                    StringComparison.OrdinalIgnoreCase)))
        {
            if (required)
            {
                throw new InvalidDataException($"Cached export {reference.UIndex} no longer matches {reference.InstancedFullPath}.");
            }
            return null;
        }
        return export;
    }

    private static DialogueOriginCache CreateOriginCache(CameraOrigin origin) => new()
    {
        Location = origin.Location,
        Rotation = origin.Rotation,
    };

    private static void CopyOrigins(IReadOnlyDictionary<string, DialogueOriginCache> source,
        IDictionary<string, CameraOrigin> destination)
    {
        foreach ((string actorTag, DialogueOriginCache origin) in source)
        {
            destination[actorTag] = new CameraOrigin(origin.Location, origin.Rotation);
        }
    }

    private static void CopyPoses(IReadOnlyDictionary<string, List<DialogueMatrixCache>> source,
        IDictionary<string, Matrix4x4[]> destination)
    {
        foreach ((string actorTag, List<DialogueMatrixCache> pose) in source)
        {
            destination[actorTag] = pose.Select(matrix => matrix.ToMatrix()).ToArray();
        }
    }

    private void SaveDialogueCachePresetInteractively()
    {
        string suggestedLabel = dialogueNodePreview?.Conversation?.ConvName;
        string label = PromptDialog.Prompt(Window.GetWindow(this), "Cache label:",
            "Save Dialogue Cache Preset", suggestedLabel)?.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        try
        {
            SaveDialogueCachePreset(label);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                          or InvalidOperationException or InvalidDataException)
        {
            MessageBox.Show(Window.GetWindow(this), $"Unable to save the dialogue cache: {exception.Message}",
                "Save Dialogue Cache", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetTrackPlaybackOptionsForCachedNode()
    {
        UnregisterKeyframes();
        primaryTrackMove = null;
        availableTrackMoves.Clear();
        availableExtraTrackMoves.Clear();
        availableExtraTrackMoves.Add(TrackMovePlaybackOption.None);
        availableDirectorTracks.Clear();
        availableDirectorTracks.Add(DirectorPlaybackOption.None);
        dialoguePreviewCameraActors.Clear();
        previewActorTrackAssignments.Clear();
        characterTrackMoves.Clear();
        cameraTrackMoves.Clear();
        selectedExtraTrackMove = TrackMovePlaybackOption.None;
        selectedDirectorPlayback = DirectorPlaybackOption.None;
        playExtraTrackMove = false;
        playDirectorMulticam = false;
    }

    private DialogueSegmentRuntime CaptureDialogueSegmentRuntime(DialogueTimelineSegment segment)
    {
        var clonedTrackMoves = new List<TrackMovePlaybackOption>();
        var trackMovesByExport = new Dictionary<int, TrackMovePlaybackOption>();
        var fovModelsByExport = new Dictionary<int, CurveEditor3DFovModel>();
        foreach (TrackMovePlaybackOption source in availableTrackMoves)
        {
            var trackModel = new CurveEditor3DModel { AutoCommit = false };
            trackModel.Load(source.TrackMove);
            trackModel.Changed += Model_Changed;
            CurveEditor3DFovModel fovModel = null;
            if (source.FovModel?.Export is { } fovExport)
            {
                if (!fovModelsByExport.TryGetValue(fovExport.UIndex, out fovModel))
                {
                    fovModel = new CurveEditor3DFovModel { AutoCommit = false };
                    fovModel.Load(fovExport);
                    fovModel.Changed += FovModel_Changed;
                    fovModelsByExport[fovExport.UIndex] = fovModel;
                }
            }
            var clone = new TrackMovePlaybackOption
            {
                DisplayName = source.DisplayName,
                TabDisplayName = source.TabDisplayName,
                Group = source.Group,
                TrackMove = source.TrackMove,
                Model = trackModel,
                FovModel = fovModel,
            };
            clonedTrackMoves.Add(clone);
            trackMovesByExport[source.TrackMove.UIndex] = clone;
        }

        TrackMovePlaybackOption Remap(TrackMovePlaybackOption source) =>
            source?.TrackMove is { } export ? trackMovesByExport.GetValueOrDefault(export.UIndex) : null;

        var clonedDirectors = availableDirectorTracks
            .Where(option => option.DirectorTrack is not null)
            .Select(option => new DirectorPlaybackOption
            {
                DisplayName = option.DisplayName,
                DirectorTrack = option.DirectorTrack,
                Cuts = option.Cuts.Select(cut => new DirectorCameraCut
                {
                    Time = cut.Time,
                    GroupName = cut.GroupName,
                    Camera = Remap(cut.Camera),
                    SwitchCameraTrack = cut.SwitchCameraTrack,
                    CameraActorTag = cut.CameraActorTag,
                    CameraActor = cut.CameraActor,
                    FallbackOrigin = cut.FallbackOrigin,
                    FallbackFovDegrees = cut.FallbackFovDegrees,
                }).ToArray(),
            })
            .Prepend(DirectorPlaybackOption.None)
            .ToArray();
        var clonedExtras = availableExtraTrackMoves
            .Where(option => option.TrackMove is not null)
            .Select(Remap)
            .Where(option => option is not null)
            .Prepend(TrackMovePlaybackOption.None)
            .ToArray();
        var actorTracks = previewActorTrackAssignments
            .Where(pair => Remap(pair.Value) is not null)
            .ToDictionary(pair => pair.Key.ActorTag, pair => Remap(pair.Value), StringComparer.OrdinalIgnoreCase);
        var actorGestures = previewActorGestureAssignments
            .ToDictionary(pair => pair.Key.ActorTag, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        PreviewActorConfiguration speakingActor = previewActors.FirstOrDefault(IsDialogueNodeSpeaker);
        DialogueFaceFxBinding mainFaceFx = CreateDialogueFaceFxBinding(segment.Node, speakingActor,
            dialogueNodePreview.VoStartTime);
        var fovoFaceFx = faceOnlyVoEvents
            .Select(faceOnlyVo => new
            {
                Event = faceOnlyVo,
                Binding = CreateDialogueFaceFxBinding(faceOnlyVo.Node, faceOnlyVo.Actor, faceOnlyVo.StartTime)
            })
            .Where(item => item.Binding is not null)
            .ToDictionary(item => item.Event, item => item.Binding);

        return new DialogueSegmentRuntime
        {
            Segment = segment,
            PrimaryTrackMove = Remap(primaryTrackMove),
            TrackMoves = clonedTrackMoves,
            ExtraTrackMoves = clonedExtras,
            DirectorTracks = clonedDirectors,
            CameraTracks = dialoguePreviewCameraActors.Select(Remap).Where(option => option is not null).ToArray(),
            GestureTracks = availableGestureTracks.ToArray(),
            ActorTrackAssignments = actorTracks,
            ActorGestureAssignments = actorGestures,
            DirectionTracks = actorDirectionTracks.ToArray(),
            FaceOnlyVoEvents = faceOnlyVoEvents.ToArray(),
            DialogueAudio = GetDialogueNodeAudio(segment.Node),
            MainFaceFx = mainFaceFx,
            FaceOnlyVoFaceFx = fovoFaceFx,
        };
    }

    private DialogueSegmentRuntime MergeDialogueSegmentRuntimes(DialogueTimelineSegment segment,
        IReadOnlyList<DialogueSegmentRuntime> interpRuntimes)
    {
        var actorTracks = new Dictionary<string, TrackMovePlaybackOption>(StringComparer.OrdinalIgnoreCase);
        var actorGestures = new Dictionary<string, GestureTrackOption>(StringComparer.OrdinalIgnoreCase);
        foreach (DialogueSegmentRuntime runtime in interpRuntimes)
        {
            foreach ((string actorTag, TrackMovePlaybackOption trackMove) in runtime.ActorTrackAssignments)
            {
                if (!actorTracks.TryGetValue(actorTag, out TrackMovePlaybackOption existing)
                    || GetActorGroupMatchScore(trackMove.Group, actorTag)
                       > GetActorGroupMatchScore(existing.Group, actorTag)
                    || GetActorGroupMatchScore(trackMove.Group, actorTag)
                       == GetActorGroupMatchScore(existing.Group, actorTag)
                    && GetAuthoredMovementRank(trackMove) > GetAuthoredMovementRank(existing))
                {
                    actorTracks[actorTag] = trackMove;
                }
            }
            foreach ((string actorTag, GestureTrackOption gesture) in runtime.ActorGestureAssignments)
            {
                if (!actorGestures.TryGetValue(actorTag, out GestureTrackOption existing)
                    || GetGestureActorMatchScore(gesture, actorTag)
                       > GetGestureActorMatchScore(existing, actorTag))
                {
                    actorGestures[actorTag] = gesture;
                }
            }
        }

        IReadOnlyList<TrackMovePlaybackOption> trackMoves = interpRuntimes
            .SelectMany(runtime => runtime.TrackMoves)
            .DistinctBy(option => (option.TrackMove.FileRef, option.TrackMove.UIndex))
            .ToArray();
        IReadOnlyList<TrackMovePlaybackOption> extraTrackMoves = interpRuntimes
            .SelectMany(runtime => runtime.ExtraTrackMoves)
            .Where(option => option.TrackMove is not null)
            .DistinctBy(option => (option.TrackMove.FileRef, option.TrackMove.UIndex))
            .Prepend(TrackMovePlaybackOption.None)
            .ToArray();
        IReadOnlyList<DirectorPlaybackOption> directorTracks = interpRuntimes
            .SelectMany(runtime => runtime.DirectorTracks)
            .Where(option => option.DirectorTrack is not null)
            .DistinctBy(option => (option.DirectorTrack.FileRef, option.DirectorTrack.UIndex))
            .Prepend(DirectorPlaybackOption.None)
            .ToArray();
        IReadOnlyList<GestureTrackOption> gestureTracks = interpRuntimes
            .SelectMany(runtime => runtime.GestureTracks)
            .Where(option => option.Track is not null)
            .DistinctBy(option => (option.Track.FileRef, option.Track.UIndex))
            .Prepend(GestureTrackOption.None)
            .ToArray();
        IReadOnlyList<FaceOnlyVoEvent> fovoEvents = interpRuntimes
            .SelectMany(runtime => runtime.FaceOnlyVoEvents)
            .DistinctBy(faceOnlyVo => (faceOnlyVo.Track.FileRef, faceOnlyVo.Track.UIndex, faceOnlyVo.StartTime,
                faceOnlyVo.Node.LineStrRef))
            .OrderBy(faceOnlyVo => faceOnlyVo.StartTime)
            .ToArray();
        var fovoFaceFx = interpRuntimes
            .SelectMany(runtime => runtime.FaceOnlyVoFaceFx)
            .GroupBy(pair => pair.Key)
            .ToDictionary(group => group.Key, group => group.First().Value);

        return new DialogueSegmentRuntime
        {
            Segment = segment,
            PrimaryTrackMove = interpRuntimes.Select(runtime => runtime.PrimaryTrackMove)
                .FirstOrDefault(option => option is not null),
            TrackMoves = trackMoves,
            ExtraTrackMoves = extraTrackMoves,
            DirectorTracks = directorTracks,
            CameraTracks = interpRuntimes.SelectMany(runtime => runtime.CameraTracks)
                .DistinctBy(option => (option.TrackMove.FileRef, option.TrackMove.UIndex)).ToArray(),
            GestureTracks = gestureTracks,
            ActorTrackAssignments = actorTracks,
            ActorGestureAssignments = actorGestures,
            DirectionTracks = interpRuntimes.SelectMany(runtime => runtime.DirectionTracks).ToArray(),
            FaceOnlyVoEvents = fovoEvents,
            DialogueAudio = interpRuntimes.Select(runtime => runtime.DialogueAudio).FirstOrDefault(audio => audio is not null),
            MainFaceFx = interpRuntimes.Select(runtime => runtime.MainFaceFx).FirstOrDefault(faceFx => faceFx is not null),
            FaceOnlyVoFaceFx = fovoFaceFx,
        };
    }

    private DialogueFaceFxBinding CreateDialogueFaceFxBinding(DialogueNodeExtended node,
        PreviewActorConfiguration actor, float timelineOffset)
    {
        if (node is null || actor is null)
        {
            return null;
        }
        foreach (DialoguePreviewAudioGender gender in GetDialogueLineGenderCandidates())
        {
            ExportEntry dialogueFaceFxExport = ResolveDialogueFaceFxAnimSet(node, gender);
            if (dialogueFaceFxExport is null)
            {
                continue;
            }
            bool isFemale = gender == DialoguePreviewAudioGender.Female;
            if (dialogueFaceFxExport.ClassName == "FaceFXAsset")
            {
                FaceFXAsset dialogueAsset = dialogueFaceFxExport.GetBinaryData<FaceFXAsset>();
                FaceFXLine dialogueLine = FindDialogueFaceFxLine(dialogueAsset.Lines, node, isFemale,
                    dialogueFaceFxExport.Game);
                if (dialogueLine is not null)
                {
                    return new DialogueFaceFxBinding(actor, dialogueAsset, null, dialogueLine, timelineOffset);
                }
                continue;
            }
            if (dialogueFaceFxExport.ClassName != "FaceFXAnimSet")
            {
                continue;
            }

            FaceFXAnimSet animSet = dialogueFaceFxExport.GetBinaryData<FaceFXAnimSet>();
            FaceFXLine line = FindDialogueFaceFxLine(animSet.Lines, node, isFemale, dialogueFaceFxExport.Game);
            if (line is null)
            {
                continue;
            }
            // The DLG owns the authored animation curves, but the actor owns the compiled facial
            // graph and its bone mapping. Combining the DLG AnimSet with a FaceFXAsset found next
            // to it can bind successfully while producing no deformation on this actor's skeleton.
            ExportEntry assetExport = GetDialoguePreviewFaceFxAssetExport(actor);
            if (assetExport is not null)
            {
                return new DialogueFaceFxBinding(actor, assetExport.GetBinaryData<FaceFXAsset>(), animSet, line,
                    timelineOffset);
            }
        }

        // A tagged pawn can carry its own FaceFXAsset and lines. Those are intentionally considered
        // only after every DLG-owned FaceFX reference failed to supply this line.
        ExportEntry importedAssetExport = ResolveExportReference(actor.Construction?.FaceFxAsset, required: false);
        if (importedAssetExport?.ClassName == "FaceFXAsset")
        {
            FaceFXAsset importedAsset = importedAssetExport.GetBinaryData<FaceFXAsset>();
            foreach (DialoguePreviewAudioGender gender in GetDialogueLineGenderCandidates())
            {
                FaceFXLine line = FindDialogueFaceFxLine(importedAsset.Lines, node,
                    gender == DialoguePreviewAudioGender.Female, importedAssetExport.Game);
                if (line is not null)
                {
                    return new DialogueFaceFxBinding(actor, importedAsset, null, line, timelineOffset);
                }
            }
        }
        return null;
    }

    private void BuildDialogueRuntimeActorSnapshots()
    {
        foreach (DialogueTimelineSegment segment in dialogueTimelineSegments.OrderBy(segment => segment.TreeDepth))
        {
            DialogueSegmentRuntime runtime = dialogueRuntimeCache[segment];
            runtime.StartActorGestureStates.Clear();
            runtime.EndActorGestureStates.Clear();
            IReadOnlyDictionary<string, CameraOrigin> inheritedOrigins = segment.Parent is not null
                && dialogueRuntimeCache.TryGetValue(segment.Parent, out DialogueSegmentRuntime parentRuntime)
                ? parentRuntime.EndActorOrigins
                : dialogueNodePreview.Actors.ToDictionary(actor => actor.ActorTag, actor => actor.Origin,
                    StringComparer.OrdinalIgnoreCase);
            foreach (PreviewActorConfiguration actor in previewActors)
            {
                runtime.StartActorOrigins[actor.ActorTag] = inheritedOrigins.GetValueOrDefault(actor.ActorTag,
                    actor.Origin);
            }

            IReadOnlyDictionary<string, Matrix4x4[]> inheritedGesturePoses = segment.Parent is not null
                && dialogueRuntimeCache.TryGetValue(segment.Parent, out parentRuntime)
                    ? parentRuntime.EndActorGesturePoses
                    : new Dictionary<string, Matrix4x4[]>(StringComparer.OrdinalIgnoreCase);
            foreach ((string actorTag, Matrix4x4[] pose) in inheritedGesturePoses)
            {
                runtime.StartActorGesturePoses[actorTag] = (Matrix4x4[])pose.Clone();
            }

            IReadOnlyDictionary<string, DialogueGesturePoseState> inheritedGestureStates = segment.Parent is not null
                && dialogueRuntimeCache.TryGetValue(segment.Parent, out parentRuntime)
                    ? parentRuntime.EndActorGestureStates
                    : new Dictionary<string, DialogueGesturePoseState>(StringComparer.OrdinalIgnoreCase);
            foreach ((string actorTag, DialogueGesturePoseState state) in inheritedGestureStates)
            {
                runtime.StartActorGestureStates[actorTag] = state;
            }

            var resolved = new Dictionary<PreviewActorConfiguration, CameraOrigin>();
            var evaluatedGesturePoses = new Dictionary<string, Matrix4x4[]>(StringComparer.OrdinalIgnoreCase);
            var evaluatedGestureStates = new Dictionary<string, DialogueGesturePoseState>(StringComparer.OrdinalIgnoreCase);
            foreach (PreviewActorConfiguration actor in previewActors)
            {
                CameraOrigin start = runtime.StartActorOrigins[actor.ActorTag];
                TrackMovePlaybackOption trackMove = runtime.ActorTrackAssignments.GetValueOrDefault(actor.ActorTag);
                float? movementTrackEndTime = null;
                int movementKeyCount = 0;
                if (trackMove?.Model?.Keyframes is { Count: > 0 } keys)
                {
                    movementKeyCount = keys.Count;
                    float time = Math.Clamp(segment.Duration, keys[0].Time, keys[^1].Time);
                    movementTrackEndTime = keys[^1].Time;
                    var state = new PreviewActorPlaybackState
                    {
                        Actor = actor,
                        TrackMove = trackMove,
                        OriginalOrigin = start,
                        MoveFrame = GetTrackMoveFrame(trackMove),
                    };
                    resolved[actor] = ResolveActorTrackOrigin(state, EvaluateTrackMove(trackMove, time));
                }
                else
                {
                    resolved[actor] = start;
                }

                if (runtime.ActorGestureAssignments.TryGetValue(actor.ActorTag, out GestureTrackOption gesture)
                    && previewActorAnimationStates.TryGetValue(actor, out PreviewActorAnimationState animationState))
                {
                    float? startingPoseTimeOverride = ResolveStartingPoseContinuationTime(gesture,
                        runtime.StartActorGestureStates.GetValueOrDefault(actor.ActorTag));
                    animationState.SetTimeline(gesture.StartingPose, gesture.Timeline,
                        previewActorGesturePackageCache, segment.Duration, gesture,
                        maskDialogueOverlayStaticBones: true,
                        // Only a genuine movement spline may transfer gesture motion into the
                        // actor transform inherited by the following dialogue node.
                        extractRootTranslation: ShouldExtractDialogueGestureRootTranslation(
                            isDialogueConversationPreview, movementKeyCount),
                        startingPoseTimeOverride: startingPoseTimeOverride);
                    Vector3 rootMotion = movementTrackEndTime is float trackEndTime
                        ? animationState.EvaluateExtractedRootMotionDelta(trackEndTime, segment.Duration)
                        : animationState.EvaluateExtractedRootMotion(segment.Duration);
                    if (rootMotion != Vector3.Zero)
                    {
                        resolved[actor] = ApplyDialogueGestureRootMotion(resolved[actor], rootMotion);
                    }
                    if (animationState.CaptureGesturePose() is { } pose)
                    {
                        evaluatedGesturePoses[actor.ActorTag] = pose;
                    }
                    if (ResolveDialogueGestureEndState(gesture, segment.Duration, startingPoseTimeOverride)
                        is { } endGestureState)
                    {
                        evaluatedGestureStates[actor.ActorTag] = endGestureState;
                    }
                }
            }
            foreach (PreviewActorConfiguration actor in previewActors)
            {
                bool hasMovementTrack = runtime.ActorTrackAssignments.GetValueOrDefault(actor.ActorTag)?.TrackMove
                    is not null;
                CameraOrigin end = ApplyActorDirectionTracks(runtime.DirectionTracks, actor, segment.Duration,
                    resolved[actor], resolved, hasMovementTrack,
                    previewActorAnimationStates.GetValueOrDefault(actor));
                if (evaluatedGesturePoses.TryGetValue(actor.ActorTag, out Matrix4x4[] pose))
                {
                    runtime.EndActorGesturePoses[actor.ActorTag] = pose;
                }
                else if (runtime.StartActorGesturePoses.TryGetValue(actor.ActorTag, out Matrix4x4[] heldPose))
                {
                    runtime.EndActorGesturePoses[actor.ActorTag] = (Matrix4x4[])heldPose.Clone();
                }
                if (evaluatedGestureStates.TryGetValue(actor.ActorTag, out DialogueGesturePoseState gestureState))
                {
                    runtime.EndActorGestureStates[actor.ActorTag] = gestureState;
                }
                else if (runtime.StartActorGestureStates.TryGetValue(actor.ActorTag,
                             out DialogueGesturePoseState inheritedGestureState))
                {
                    runtime.EndActorGestureStates[actor.ActorTag] = inheritedGestureState;
                }
                runtime.EndActorOrigins[actor.ActorTag] = end;
            }
        }

        BuildDialogueRuntimeLookAtSnapshots();
        BuildDialogueRuntimeCameraSnapshots();
        ReprojectDialogueActivePathActorOrigins();
    }

    private void BuildDialogueRuntimeLookAtSnapshots()
    {
        foreach (DialogueTimelineSegment segment in dialogueTimelineSegments.OrderBy(segment => segment.TreeDepth))
        {
            DialogueSegmentRuntime runtime = dialogueRuntimeCache[segment];
            DialogueSegmentRuntime parentRuntime = segment.Parent is not null
                                                   && dialogueRuntimeCache.TryGetValue(segment.Parent,
                                                       out DialogueSegmentRuntime resolvedParent)
                ? resolvedParent
                : null;
            PopulateDialogueLookAtState(runtime, parentRuntime?.EndLookAtTargets);
        }
    }

    private void PopulateDialogueLookAtState(DialogueSegmentRuntime runtime,
        IReadOnlyDictionary<string, string> inheritedTargets)
    {
        runtime.StartLookAtTargets.Clear();
        runtime.EndLookAtTargets.Clear();
        foreach (PreviewActorConfiguration actor in previewActors)
        {
            string inheritedTarget = inheritedTargets?.GetValueOrDefault(actor.ActorTag);
            runtime.StartLookAtTargets[actor.ActorTag] = inheritedTarget;
            runtime.EndLookAtTargets[actor.ActorTag] = ResolveDialogueLookAtTarget(runtime.DirectionTracks,
                actor.ActorTag, runtime.Segment.Duration, inheritedTarget);
        }
    }

    private string ResolveDialogueLookAtTarget(IEnumerable<ActorDirectionTrack> directionTracks, string actorTag,
        float time, string inheritedTarget)
    {
        IReadOnlyList<(float Time, bool Enabled, string Target)> keys = directionTracks
            .Where(track => track.IsLookAt && ActorTagMatches(track.Actor?.ActorTag, actorTag))
            .SelectMany(track => track.Keys)
            .Select(key => (key.Time, key.Enabled, key.TargetActorTag))
            .ToArray();
        return ResolveInheritedLookAtTarget(inheritedTarget, keys, time);
    }

    internal static string ResolveInheritedLookAtTarget(string inheritedTarget,
        IReadOnlyList<(float Time, bool Enabled, string Target)> keys, float time)
    {
        (float Time, bool Enabled, string Target) active = default;
        bool found = false;
        foreach ((float keyTime, bool enabled, string target) in keys)
        {
            if (keyTime <= time && (!found || keyTime >= active.Time))
            {
                active = (keyTime, enabled, target);
                found = true;
            }
        }
        if (!found)
        {
            return inheritedTarget;
        }
        return active.Enabled && !string.IsNullOrWhiteSpace(active.Target)
            ? active.Target
            : null;
    }

    private void BuildDialogueRuntimeCameraSnapshots()
    {
        // An empty camera group means "leave that camera actor where the previous Matinee left it."
        // Project camera state through the tree just like stunt-actor state so direct seeks and
        // linear playback agree, including intervening reply/no-data nodes.
        foreach (DialogueTimelineSegment segment in dialogueTimelineSegments.OrderBy(segment => segment.TreeDepth))
        {
            DialogueSegmentRuntime runtime = dialogueRuntimeCache[segment];
            DialogueSegmentRuntime parentRuntime = segment.Parent is not null
                                                   && dialogueRuntimeCache.TryGetValue(segment.Parent,
                                                       out DialogueSegmentRuntime resolvedParent)
                ? resolvedParent
                : null;
            IReadOnlyDictionary<string, CameraOrigin> inheritedOrigins = parentRuntime?.EndCameraOrigins;
            IReadOnlyDictionary<string, float> inheritedFovs = parentRuntime?.EndCameraFovs;
            PopulateDialogueCameraStartState(runtime, inheritedOrigins, inheritedFovs);
            EvaluateDialogueCameraEndState(runtime);
            ResolveDialogueStubCameraCuts(runtime);
        }
    }

    private void PopulateDialogueCameraStartState(DialogueSegmentRuntime runtime,
        IReadOnlyDictionary<string, CameraOrigin> inheritedOrigins,
        IReadOnlyDictionary<string, float> inheritedFovs)
    {
        runtime.StartCameraOrigins.Clear();
        runtime.StartCameraFovs.Clear();
        if (inheritedOrigins is not null)
        {
            foreach ((string actorTag, CameraOrigin origin) in inheritedOrigins)
            {
                runtime.StartCameraOrigins[actorTag] = origin;
                runtime.StartCameraFovs[actorTag] = inheritedFovs?.GetValueOrDefault(actorTag)
                                                    ?? dialoguePreviewInitialCameraFovDegrees;
            }
        }
        foreach (string actorTag in GetDialogueCameraActorTags(runtime))
        {
            if (runtime.StartCameraOrigins.ContainsKey(actorTag))
            {
                continue;
            }
            PlacedCameraState placed = dialoguePlacedCameras.GetValueOrDefault(actorTag);
            PlacedCameraState authored = dialogueAuthoredCameraDefaults.GetValueOrDefault(actorTag);
            DirectorCameraCut cachedCut = runtime.DirectorTracks.SelectMany(option => option.Cuts)
                .FirstOrDefault(cut => string.Equals(cut.CameraActorTag, actorTag,
                    StringComparison.OrdinalIgnoreCase) && cut.FallbackOrigin.HasValue);
            runtime.StartCameraOrigins[actorTag] = ResolveDialogueCameraSeed(placed?.Origin, authored?.Origin,
                cachedCut?.FallbackOrigin, dialoguePreviewInitialCameraOrigin);
            runtime.StartCameraFovs[actorTag] = ResolveDialogueCameraFovSeed(placed?.FovDegrees,
                authored?.FovDegrees, cachedCut?.FallbackFovDegrees, dialoguePreviewInitialCameraFovDegrees);
        }
    }

    private static IEnumerable<string> GetDialogueCameraActorTags(DialogueSegmentRuntime runtime) =>
        runtime.CameraTracks.Select(option => GetCameraActorTag(option.Group))
            .Concat(runtime.DirectorTracks.SelectMany(option => option.Cuts)
                .Select(cut => cut.CameraActorTag))
            .Where(tag => !string.IsNullOrWhiteSpace(tag)
                          && !tag.Equals("None", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private void EvaluateDialogueCameraEndState(DialogueSegmentRuntime runtime)
    {
        runtime.EndCameraOrigins.Clear();
        runtime.EndCameraFovs.Clear();
        foreach ((string actorTag, CameraOrigin origin) in runtime.StartCameraOrigins)
        {
            runtime.EndCameraOrigins[actorTag] = origin;
            runtime.EndCameraFovs[actorTag] = runtime.StartCameraFovs.GetValueOrDefault(actorTag,
                dialoguePreviewInitialCameraFovDegrees);
        }
        foreach (TrackMovePlaybackOption camera in runtime.CameraTracks)
        {
            string actorTag = GetCameraActorTag(camera.Group);
            if (string.IsNullOrWhiteSpace(actorTag) || camera.Model?.Keyframes is not { Count: > 0 })
            {
                continue;
            }
            CameraOrigin initialOrigin = runtime.StartCameraOrigins.GetValueOrDefault(actorTag,
                dialoguePreviewInitialCameraOrigin);
            float initialFov = runtime.StartCameraFovs.GetValueOrDefault(actorTag,
                dialoguePreviewInitialCameraFovDegrees);
            runtime.EndCameraOrigins[actorTag] = ResolveCameraTrackOrigin(camera,
                EvaluateTrackMove(camera, runtime.Segment.Duration), initialOrigin);
            runtime.EndCameraFovs[actorTag] = camera.FovTrack?.Eval(runtime.Segment.Duration, initialFov)
                                              ?? initialFov;
        }
    }

    private void ResolveDialogueStubCameraCuts(DialogueSegmentRuntime runtime)
    {
        foreach (DirectorCameraCut cut in runtime.DirectorTracks.SelectMany(option => option.Cuts)
                     .Where(cut => cut.Camera is null && cut.SwitchCameraTrack is null
                                                   && !string.IsNullOrWhiteSpace(cut.CameraActorTag)))
        {
            CameraOrigin origin = runtime.StartCameraOrigins.GetValueOrDefault(cut.CameraActorTag,
                cut.FallbackOrigin ?? dialoguePreviewInitialCameraOrigin);
            float fovDegrees = runtime.StartCameraFovs.GetValueOrDefault(cut.CameraActorTag,
                cut.FallbackFovDegrees ?? dialoguePreviewInitialCameraFovDegrees);
            TrackMovePlaybackOption actorCamera = runtime.CameraTracks.FirstOrDefault(option =>
                string.Equals(GetCameraActorTag(option.Group), cut.CameraActorTag,
                    StringComparison.OrdinalIgnoreCase)
                && option.Model?.Keyframes is { Count: > 0 });
            if (actorCamera is not null)
            {
                origin = ResolveCameraTrackOrigin(actorCamera, EvaluateTrackMove(actorCamera, cut.Time), origin);
                fovDegrees = actorCamera.FovTrack?.Eval(cut.Time, fovDegrees) ?? fovDegrees;
            }
            cut.FallbackOrigin = origin;
            cut.FallbackFovDegrees = fovDegrees;
        }
    }

    private void ReprojectDialogueActivePathActorOrigins()
    {
        if (dialogueNodePreview is null || dialogueTimelineActivePath.Count == 0
            || dialogueTimelineActivePath.Any(segment => !dialogueRuntimeCache.ContainsKey(segment)))
        {
            return;
        }

        IReadOnlyDictionary<string, CameraOrigin> inheritedOrigins = dialogueNodePreview.Actors
            .ToDictionary(actor => actor.ActorTag, actor => actor.Origin, StringComparer.OrdinalIgnoreCase);
        foreach (DialogueTimelineSegment segment in dialogueTimelineActivePath)
        {
            DialogueSegmentRuntime runtime = dialogueRuntimeCache[segment];
            runtime.StartActorOrigins.Clear();
            runtime.EndActorOrigins.Clear();
            foreach (PreviewActorConfiguration actor in previewActors)
            {
                runtime.StartActorOrigins[actor.ActorTag] = inheritedOrigins.GetValueOrDefault(actor.ActorTag,
                    actor.Origin);
            }

            var resolved = new Dictionary<PreviewActorConfiguration, CameraOrigin>();
            foreach (PreviewActorConfiguration actor in previewActors)
            {
                CameraOrigin start = runtime.ActorOriginOverrides.GetValueOrDefault(actor.ActorTag,
                    runtime.StartActorOrigins[actor.ActorTag]);
                TrackMovePlaybackOption trackMove = runtime.ActorTrackAssignments.GetValueOrDefault(actor.ActorTag);
                float? movementTrackEndTime = null;
                int movementKeyCount = 0;
                if (trackMove?.Model?.Keyframes is { Count: > 0 } keys)
                {
                    movementKeyCount = keys.Count;
                    float time = Math.Clamp(segment.Duration, keys[0].Time, keys[^1].Time);
                    movementTrackEndTime = keys[^1].Time;
                    var state = new PreviewActorPlaybackState
                    {
                        Actor = actor,
                        TrackMove = trackMove,
                        OriginalOrigin = start,
                        MoveFrame = GetTrackMoveFrame(trackMove),
                    };
                    resolved[actor] = ResolveActorTrackOrigin(state, EvaluateTrackMove(trackMove, time));
                }
                else
                {
                    resolved[actor] = start;
                }

                if (runtime.ActorGestureAssignments.TryGetValue(actor.ActorTag, out GestureTrackOption gesture)
                    && previewActorAnimationStates.TryGetValue(actor, out PreviewActorAnimationState animationState))
                {
                    float? startingPoseTimeOverride = ResolveStartingPoseContinuationTime(gesture,
                        runtime.StartActorGestureStates.GetValueOrDefault(actor.ActorTag));
                    animationState.SetTimeline(gesture.StartingPose, gesture.Timeline,
                        previewActorGesturePackageCache, segment.Duration, gesture,
                        maskDialogueOverlayStaticBones: true,
                        extractRootTranslation: ShouldExtractDialogueGestureRootTranslation(
                            isDialogueConversationPreview, movementKeyCount),
                        startingPoseTimeOverride: startingPoseTimeOverride);
                    Vector3 rootMotion = movementTrackEndTime is float trackEndTime
                        ? animationState.EvaluateExtractedRootMotionDelta(trackEndTime, segment.Duration)
                        : animationState.EvaluateExtractedRootMotion(segment.Duration);
                    resolved[actor] = ApplyDialogueGestureRootMotion(resolved[actor],
                        rootMotion);
                }
            }
            foreach (PreviewActorConfiguration actor in previewActors)
            {
                bool hasMovementTrack = runtime.ActorTrackAssignments.GetValueOrDefault(actor.ActorTag)?.TrackMove
                    is not null;
                runtime.EndActorOrigins[actor.ActorTag] = ApplyActorDirectionTracks(runtime.DirectionTracks, actor,
                    segment.Duration, resolved[actor], resolved, hasMovementTrack,
                    previewActorAnimationStates.GetValueOrDefault(actor));
            }
            inheritedOrigins = runtime.EndActorOrigins;
        }
        ReprojectDialogueActivePathLookAtStates();
        ReprojectDialogueActivePathCameraStates();
    }

    private void ReprojectDialogueActivePathLookAtStates()
    {
        IReadOnlyDictionary<string, string> inheritedTargets = null;
        foreach (DialogueTimelineSegment segment in dialogueTimelineActivePath)
        {
            DialogueSegmentRuntime runtime = dialogueRuntimeCache[segment];
            PopulateDialogueLookAtState(runtime, inheritedTargets);
            inheritedTargets = runtime.EndLookAtTargets;
        }
    }

    private void ReprojectDialogueActivePathCameraStates()
    {
        IReadOnlyDictionary<string, CameraOrigin> inheritedOrigins = null;
        IReadOnlyDictionary<string, float> inheritedFovs = null;
        foreach (DialogueTimelineSegment segment in dialogueTimelineActivePath)
        {
            DialogueSegmentRuntime runtime = dialogueRuntimeCache[segment];
            PopulateDialogueCameraStartState(runtime, inheritedOrigins, inheritedFovs);
            EvaluateDialogueCameraEndState(runtime);
            ResolveDialogueStubCameraCuts(runtime);
            inheritedOrigins = runtime.EndCameraOrigins;
            inheritedFovs = runtime.EndCameraFovs;
        }
    }

    private void ConfigureDialoguePreviewPlayback(bool configureTrackPlayback = true,
        ExportEntry interpDataOverride = null)
    {
        DialoguePreviewSoundpanel.StopPlaying();
        FaceOnlyVoSoundpanel.StopPlaying();
        DialoguePreviewSoundpanel.UnloadExport();
        FaceOnlyVoSoundpanel.UnloadExport();
        activeFaceOnlyVoEvent = null;
        dialoguePreviewAudioStarted = false;
        faceOnlyVoAudioStarted = false;
        ClearDialoguePreviewFaceFx();
        LoadDialoguePreviewFaceFxAssets();
        LoadFaceOnlyVoEvents(interpDataOverride);
        foreach (PreviewActorConfiguration actor in previewActors)
        {
            AttachDialoguePreviewFaceFxAsset(actor);
            if (!dialogueNodePreview.Node.IgnoreBodyGesture)
            {
                GestureTrackOption gesture = availableGestureTracks
                    .Select(option => new { Option = option, Score = GetGestureActorMatchScore(option, actor.ActorTag) })
                    .Where(candidate => candidate.Score > 0)
                    .OrderByDescending(candidate => candidate.Score)
                    .Select(candidate => candidate.Option)
                    .FirstOrDefault();
                if (gesture is not null)
                {
                    previewActorGestureAssignments[actor] = gesture;
                    ApplyAssignedGestureToActor(actor);
                }
            }
            else if (previewActorAnimationStates.TryGetValue(actor, out PreviewActorAnimationState animationState))
            {
                previewActorGestureAssignments.Remove(actor);
                animationState.HoldCurrentGesturePose();
            }
            if (IsDialogueNodeSpeaker(actor))
            {
                ApplyDialoguePreviewFaceFx(actor);
            }
        }

        updatingMulticamControls = true;
        selectedDirectorPlayback = configureTrackPlayback
            ? availableDirectorTracks.FirstOrDefault(option => option.DirectorTrack is not null)
              ?? DirectorPlaybackOption.None
            : DirectorPlaybackOption.None;
        playDirectorMulticam = configureTrackPlayback && selectedDirectorPlayback.Cuts.Count > 0;
        selectedExtraTrackMove = TrackMovePlaybackOption.None;
        playExtraTrackMove = false;
        if (configureTrackPlayback && !playDirectorMulticam)
        {
            selectedExtraTrackMove = availableTrackMoves.FirstOrDefault(option =>
                IsCameraTrackGroup(option.Group, option.FovModel is not null))
                ?? TrackMovePlaybackOption.None;
            playExtraTrackMove = selectedExtraTrackMove.TrackMove is not null;
        }
        DirectorTrackComboBox.SelectedItem = selectedDirectorPlayback;
        ExtraTrackMoveComboBox.SelectedItem = selectedExtraTrackMove;
        DirectorMulticamCheckBox.IsChecked = playDirectorMulticam;
        ExtraTrackMoveCheckBox.IsChecked = playExtraTrackMove;
        updatingMulticamControls = false;
        ActorPlaybackTrackZCheckBox.IsChecked = true;
        BuildActorDirectionTracks(interpDataOverride);
        if (configureTrackPlayback)
        {
            RefreshKeyframeTrackMoveTabs();
        }
    }

    private void LoadFaceOnlyVoEvents(ExportEntry interpDataOverride = null)
    {
        faceOnlyVoEvents.Clear();
        ExportEntry interpData = interpDataOverride ?? dialogueNodePreview?.Node.InterpData;
        if (interpData is null)
        {
            return;
        }

        foreach (ExportEntry group in GetReferencedExports(interpData, "InterpGroups"))
        {
            foreach (ExportEntry track in GetReferencedExports(group, "InterpTracks")
                         .Where(candidate => candidate.IsA("SFXInterpTrackPlayFaceOnlyVO")))
            {
                ArrayProperty<StructProperty> trackKeys = track.GetProperty<ArrayProperty<StructProperty>>("m_aTrackKeys");
                ArrayProperty<StructProperty> voKeys = track.GetProperty<ArrayProperty<StructProperty>>("m_aFOVOKeys");
                int keyCount = Math.Min(trackKeys?.Count ?? 0, voKeys?.Count ?? 0);
                PreviewActorConfiguration trackActor = ResolveFaceOnlyVoActor(track, group);
                for (int index = 0; index < keyCount; index++)
                {
                    float startTime = trackKeys[index].GetProp<FloatProperty>("fTime")?.Value ?? 0;
                    int conversationIndex = voKeys[index].GetProp<ObjectProperty>("pConversation")?.Value ?? 0;
                    int lineStrRef = voKeys[index].GetProp<IntProperty>("nLineStrRef")?.Value ?? 0;
                    DialogueNodeExtended node = ResolveFaceOnlyVoNode(track.FileRef.GetEntry(conversationIndex), lineStrRef);
                    if (node is not null)
                    {
                        PreviewActorConfiguration actor = trackActor ?? FindPreviewActorByTag(node.SpeakerTag?.SpeakerName);
                        faceOnlyVoEvents.Add(new FaceOnlyVoEvent(startTime, track, group, node, actor));
                    }
                }
            }
        }

        faceOnlyVoEvents.Sort((left, right) => left.StartTime.CompareTo(right.StartTime));
    }

    private PreviewActorConfiguration ResolveFaceOnlyVoActor(ExportEntry track, ExportEntry group)
    {
        string trackActor = track.GetProperty<NameProperty>("m_nmSFXFindActor")?.Value.Instanced;
        string groupActor = group.GetProperty<NameProperty>("m_nmSFXFindActor")?.Value.Instanced;
        string actorTag = string.IsNullOrWhiteSpace(trackActor)
                          || string.Equals(trackActor, "None", StringComparison.OrdinalIgnoreCase)
            ? groupActor
            : trackActor;
        if (string.Equals(actorTag, "Owner", StringComparison.OrdinalIgnoreCase))
        {
            string resolvedOwner = ConversationExtended.ResolveOwnerTagFromExport(track)
                                   ?? ConversationExtended.ResolveOwnerTagFromExport(group);
            if (!string.IsNullOrWhiteSpace(resolvedOwner))
            {
                actorTag = resolvedOwner;
            }
        }
        if (string.IsNullOrWhiteSpace(actorTag)
            || string.Equals(actorTag, "None", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return FindPreviewActorByTag(actorTag);
    }

    private PreviewActorConfiguration FindPreviewActorByTag(string actorTag)
    {
        if (string.IsNullOrWhiteSpace(actorTag))
        {
            return null;
        }

        PreviewActorConfiguration actor = previewActors.FirstOrDefault(candidate =>
            ActorTagMatches(actorTag, candidate.ActorTag));
        if (actor is not null)
        {
            return actor;
        }

        const string faceOnlyVoPrefix = "fovo_";
        string unprefixedActorTag = actorTag.StartsWith(faceOnlyVoPrefix, StringComparison.OrdinalIgnoreCase)
            ? actorTag[faceOnlyVoPrefix.Length..]
            : actorTag;
        return previewActors.FirstOrDefault(candidate =>
        {
            string candidateTag = candidate.ActorTag;
            if (candidateTag.StartsWith(faceOnlyVoPrefix, StringComparison.OrdinalIgnoreCase))
            {
                candidateTag = candidateTag[faceOnlyVoPrefix.Length..];
            }
            return ActorTagMatches(unprefixedActorTag, candidateTag);
        });
    }

    private DialogueNodeExtended ResolveFaceOnlyVoNode(IEntry conversationEntry, int lineStrRef)
    {
        ExportEntry conversationExport = conversationEntry switch
        {
            ExportEntry export => export,
            ImportEntry import => EntryImporter.ResolveImport(import, previewActorGesturePackageCache),
            _ => null
        };
        if (conversationExport is null || lineStrRef == 0)
        {
            return null;
        }

        var conversation = new ConversationExtended(conversationExport);
        conversation.LoadConversation(detailedParse: true);
        return conversation.EntryList.Concat(conversation.ReplyList)
            .FirstOrDefault(node => node.LineStrRef == lineStrRef);
    }

    private void ApplyFaceOnlyVoAtTime(float time, bool playAudio = true)
    {
        FaceOnlyVoEvent faceOnlyVo = faceOnlyVoEvents.LastOrDefault(candidate => candidate.StartTime <= time);
        if (faceOnlyVo is null || time >= GetFaceOnlyVoEndTime(faceOnlyVo))
        {
            if (activeFaceOnlyVoEvent is not null)
            {
                FaceOnlyVoSoundpanel.StopPlaying();
                EndActiveFaceOnlyVo();
            }
            return;
        }

        if (!ReferenceEquals(activeFaceOnlyVoEvent, faceOnlyVo))
        {
            if (activeFaceOnlyVoEvent is not null)
            {
                FaceOnlyVoSoundpanel.StopPlaying();
                EndActiveFaceOnlyVo();
            }
            activeFaceOnlyVoEvent = faceOnlyVo;
            faceOnlyVoAudioStarted = false;
            ApplyFaceOnlyVoFaceFx(faceOnlyVo);
        }

        if (playAudio && !faceOnlyVoAudioStarted)
        {
            ExportEntry audio = GetDialogueNodeAudio(faceOnlyVo.Node);
            if (audio is null && activeDialogueSegmentRuntime is not null
                && faceOnlyVo.Node.LineStrRef == activeDialogueSegmentRuntime.Segment.Node.LineStrRef)
            {
                audio = activeDialogueSegmentRuntime.DialogueAudio;
            }
            if (audio is not null)
            {
                FaceOnlyVoSoundpanel.LoadExport(audio);
                FaceOnlyVoSoundpanel.StopPlaying();
                faceOnlyVoAudioStarted = FaceOnlyVoSoundpanel.StartOrPausePlaying(
                    Math.Max(0, time - faceOnlyVo.StartTime));
            }
        }
    }

    private void ApplyFaceOnlyVoFaceFx(FaceOnlyVoEvent faceOnlyVo)
    {
        if (faceOnlyVo.Actor is null
            || !previewActorAnimationStates.TryGetValue(faceOnlyVo.Actor, out PreviewActorAnimationState animationState))
        {
            return;
        }

        if (activeDialogueSegmentRuntime?.FaceOnlyVoFaceFx.GetValueOrDefault(faceOnlyVo) is { } cachedFaceFx)
        {
            if (cachedFaceFx.AnimSet is not null)
            {
                dialoguePreviewFaceFxAnimSets[cachedFaceFx.Actor] = cachedFaceFx.AnimSet;
            }
            animationState.SetFaceFx(cachedFaceFx.Asset, cachedFaceFx.AnimSet, cachedFaceFx.Line,
                cachedFaceFx.TimelineOffset);
            animationState.SetTime(playbackCurrentTime);
            UpdatePreviewActorSkinning(cachedFaceFx.Actor);
            return;
        }
        DialogueFaceFxBinding binding = CreateDialogueFaceFxBinding(faceOnlyVo.Node, faceOnlyVo.Actor,
            faceOnlyVo.StartTime);
        if (binding is null)
        {
            return;
        }
        if (binding.AnimSet is not null)
        {
            dialoguePreviewFaceFxAnimSets[faceOnlyVo.Actor] = binding.AnimSet;
        }
        animationState.SetFaceFx(binding.Asset, binding.AnimSet, binding.Line, binding.TimelineOffset);
        animationState.SetTime(playbackCurrentTime);
        UpdatePreviewActorSkinning(faceOnlyVo.Actor);
    }

    private static FaceFXLine FindDialogueFaceFxLine(IEnumerable<FaceFXLine> lines, DialogueNodeExtended node,
        bool isFemale, MEGame game)
    {
        if (lines is null)
        {
            return null;
        }
        var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string parsedLineName = isFemale ? node.FaceFX_Female : node.FaceFX_Male;
        if (!string.IsNullOrWhiteSpace(parsedLineName)
            && !string.Equals(parsedLineName, "None", StringComparison.OrdinalIgnoreCase))
        {
            candidateNames.Add(parsedLineName);
        }

        string baseLineName = $"FXA_{node.LineStrRef}";
        candidateNames.Add($"{baseLineName}_{(isFemale ? 'F' : 'M')}");
        if (isFemale && game.IsGame1())
        {
            candidateNames.Add(baseLineName);
        }

        FaceFXLine line = lines.FirstOrDefault(candidate =>
            candidateNames.Contains(candidate.NameAsString));
        if (line is not null)
        {
            return line;
        }

        string lineId = node.LineStrRef.ToString(CultureInfo.InvariantCulture);
        return lines.FirstOrDefault(candidate =>
            string.Equals(candidate.ID, lineId, StringComparison.OrdinalIgnoreCase));
    }

    private void EndActiveFaceOnlyVo()
    {
        PreviewActorConfiguration actor = activeFaceOnlyVoEvent?.Actor;
        activeFaceOnlyVoEvent = null;
        faceOnlyVoAudioStarted = false;
        if (actor is null || !previewActorAnimationStates.TryGetValue(actor, out PreviewActorAnimationState animationState))
        {
            return;
        }

        dialoguePreviewFaceFxAnimSets.Remove(actor);
        animationState.ClearFaceFx();
        AttachDialoguePreviewFaceFxAsset(actor);
        if (IsDialogueNodeSpeaker(actor))
        {
            ApplyDialoguePreviewFaceFx(actor);
        }
        animationState.SetTime(playbackCurrentTime);
        UpdatePreviewActorSkinning(actor);
    }

    private IEnumerable<DialoguePreviewAudioGender> GetDialogueLineGenderCandidates()
    {
        DialoguePreviewAudioGender preferred = dialogueNodePreview?.PlayerSelection.UseFemaleLines == true
            ? DialoguePreviewAudioGender.Female
            : DialoguePreviewAudioGender.Male;
        yield return preferred;
        yield return preferred == DialoguePreviewAudioGender.Female
            ? DialoguePreviewAudioGender.Male
            : DialoguePreviewAudioGender.Female;
    }

    private ExportEntry GetDialogueNodeAudio(DialogueNodeExtended node)
    {
        if (node is null)
        {
            return null;
        }

        foreach (DialoguePreviewAudioGender gender in GetDialogueLineGenderCandidates())
        {
            ExportEntry audio = gender == DialoguePreviewAudioGender.Female
                ? node.WwiseStream_Female
                : node.WwiseStream_Male;
            if (audio is not null)
            {
                return audio;
            }

            string suffix = gender == DialoguePreviewAudioGender.Female ? "_f" : "_m";
            string token = $"{node.LineStrRef}{suffix}";
            IEnumerable<IMEPackage> packages = new[]
                {
                    node.InterpData?.FileRef,
                    dialogueNodePreview?.Conversation?.Export?.FileRef,
                }
                .Where(package => package is not null)
                .Distinct();
            ExportEntry fallback = packages.SelectMany(package => package.Exports)
                .FirstOrDefault(export => export.ClassName == "WwiseStream"
                                          && export.ObjectNameString.Contains(token,
                                              StringComparison.OrdinalIgnoreCase));
            if (fallback is not null)
            {
                return fallback;
            }
        }
        return null;
    }

    private ExportEntry ResolveDialogueFaceFxAnimSet(DialogueNodeExtended node, DialoguePreviewAudioGender gender)
    {
        IEntry animSetEntry = gender == DialoguePreviewAudioGender.Female
            ? node?.SpeakerTag?.FaceFX_Female
            : node?.SpeakerTag?.FaceFX_Male;
        return animSetEntry switch
        {
            ExportEntry export => export,
            ImportEntry import => EntryImporter.ResolveImport(import, previewActorGesturePackageCache),
            _ => null
        };
    }

    private float GetFaceOnlyVoEndTime(FaceOnlyVoEvent faceOnlyVo)
    {
        FaceOnlyVoEvent nextEvent = faceOnlyVoEvents.FirstOrDefault(candidate => candidate.StartTime > faceOnlyVo.StartTime);
        float timelineEndTime = isPlayingDialogueTimeline || isDialogueConversationPreview
            ? activeDialogueTimelineSegment?.Duration
              ?? dialogueNodePreview?.Node.InterpData?.GetProperty<FloatProperty>("InterpLength")?.Value
              ?? 0
            : playbackEndTime;
        return nextEvent?.StartTime ?? MathF.Max(timelineEndTime, faceOnlyVo.StartTime + 0.001f);
    }

    private void LoadDialoguePreviewFaceFxAssets()
    {
        string packagePath = FindDialoguePreviewFaceFxPackagePath(CurrentLoadedExport.Game);
        if (dialoguePreviewFaceFxPackage is not null
            && string.Equals(dialoguePreviewFaceFxPackage.FilePath, packagePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        dialoguePreviewFaceFxPackage?.Dispose();
        dialoguePreviewFaceFxPackage = null;
        if (packagePath is null)
        {
            return;
        }

        dialoguePreviewFaceFxPackage = MEPackageHandler.OpenMEPackage(packagePath);
    }

    private void ApplyDialoguePreviewFaceFx(PreviewActorConfiguration actor)
    {
        if (!IsDialogueNodeSpeaker(actor)
            || !previewActorAnimationStates.TryGetValue(actor, out PreviewActorAnimationState animationState))
        {
            return;
        }

        DialogueFaceFxBinding binding = CreateDialogueFaceFxBinding(dialogueNodePreview.Node, actor,
            dialogueNodePreview.VoStartTime);
        if (binding is null)
        {
            return;
        }
        if (binding.AnimSet is not null)
        {
            dialoguePreviewFaceFxAnimSets[actor] = binding.AnimSet;
        }
        animationState.SetFaceFx(binding.Asset, binding.AnimSet, binding.Line, binding.TimelineOffset);
        UpdatePreviewActorSkinning(actor);
    }

    private void AttachDialoguePreviewFaceFxAsset(PreviewActorConfiguration actor)
    {
        if (!previewActorAnimationStates.TryGetValue(actor, out PreviewActorAnimationState animationState))
        {
            return;
        }

        ExportEntry assetExport = GetDialoguePreviewFaceFxAssetExport(actor);
        if (assetExport is not null)
        {
            actor.Construction ??= new DialogueActorConstructionCache { ActorTag = actor.ActorTag };
            actor.Construction.FaceFxAsset ??= CreateExportReference(assetExport);
            animationState.AttachFaceFx(assetExport.GetBinaryData<FaceFXAsset>());
        }
    }

    private void ClearDialoguePreviewFaceFx()
    {
        dialoguePreviewFaceFxAnimSets.Clear();
        foreach (KeyValuePair<PreviewActorConfiguration, PreviewActorAnimationState> pair in previewActorAnimationStates)
        {
            pair.Value.ClearFaceFx();
            UpdatePreviewActorSkinning(pair.Key);
        }
    }

    private bool IsDialogueNodeSpeaker(PreviewActorConfiguration actor) =>
        dialogueNodePreview?.Node.SpeakerTag is { } speaker
        && ActorTagMatches(speaker.SpeakerName, actor.ActorTag);

    private void BuildActorDirectionTracks(ExportEntry interpDataOverride = null)
    {
        actorDirectionTracks.Clear();
        ExportEntry interpData = interpDataOverride ?? dialogueNodePreview?.Node.InterpData;
        if (interpData is null)
        {
            return;
        }

        foreach (ExportEntry group in GetReferencedExports(interpData, "InterpGroups"))
        {
            PreviewActorConfiguration groupActor = previewActors
                .Select(candidate => new { Actor = candidate, Score = GetActorGroupMatchScore(group, candidate.ActorTag) })
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .Select(candidate => candidate.Actor)
                .FirstOrDefault();

            foreach (ExportEntry track in GetReferencedExports(group, "InterpTracks")
                         .Where(track => track.IsA("BioEvtSysTrackSetFacing") || track.IsA("BioEvtSysTrackLookAt")))
            {
                string trackActorTag = GetDirectionTrackActorTag(track);
                PreviewActorConfiguration actor = trackActorTag is null
                    ? groupActor
                    : FindPreviewActorByTag(trackActorTag);
                if (actor is null)
                {
                    continue;
                }
                bool isLookAt = track.IsA("BioEvtSysTrackLookAt");
                string dataPropertyName = isLookAt ? "m_aLookAtKeys" : "m_aFacingKeys";
                ArrayProperty<StructProperty> times = track.GetProperty<ArrayProperty<StructProperty>>("m_aTrackKeys");
                ArrayProperty<StructProperty> data = track.GetProperty<ArrayProperty<StructProperty>>(dataPropertyName);
                if (times is null || data is null)
                {
                    continue;
                }

                List<ActorDirectionKey> keys = [];
                for (int index = 0; index < Math.Min(times.Count, data.Count); index++)
                {
                    float keyTime = times[index].GetProp<FloatProperty>("fTime")?.Value ?? 0;
                    if (isLookAt)
                    {
                        bool enabled = data[index].GetProp<BoolProperty>("bEnabled")?.Value ?? false;
                        string targetActorTag = enabled ? FindDirectionTargetActor(data[index], actor.ActorTag) : null;
                        keys.Add(new ActorDirectionKey(keyTime, targetActorTag is not null, targetActorTag, null, 0));
                    }
                    else
                    {
                        string stageNode = data[index].GetProp<NameProperty>("nmStageNode")?.Value.Instanced;
                        bool applyOrientation = data[index].GetProp<BoolProperty>("bApplyOrientation")?.Value ?? false;
                        float orientation = applyOrientation
                            ? data[index].GetProp<FloatProperty>("fOrientation")?.Value ?? 0
                            : 0;
                        bool enabled = !string.IsNullOrWhiteSpace(stageNode)
                                       && dialogueNodePreview.StageContext.StageNodeOrigins.ContainsKey(stageNode);
                        keys.Add(new ActorDirectionKey(keyTime, enabled, null, stageNode, orientation));
                    }
                }
                if (keys.Count > 0)
                {
                    actorDirectionTracks.Add(new ActorDirectionTrack(actor, isLookAt,
                        keys.OrderBy(key => key.Time).ToArray()));
                }
            }
        }
    }

    private ExportEntry GetDialoguePreviewFaceFxAssetExport(PreviewActorConfiguration actor)
    {
        // This applies uniformly to every actor: use the FaceFXAsset discovered on the actor/pawn,
        // one of its components, or its archetype chain. The DLG separately supplies the AnimSet
        // and line. Player presets and actors without an imported graph fall back to the matching
        // shared base-game asset.
        ExportEntry importedActorAsset = ResolveExportReference(actor?.Construction?.FaceFxAsset, required: false);
        if (importedActorAsset?.ClassName == "FaceFXAsset")
        {
            return importedActorAsset;
        }
        if (dialoguePreviewFaceFxPackage is not null && !string.IsNullOrWhiteSpace(actor?.FaceFxAssetName))
        {
            ExportEntry baseGameAsset = dialoguePreviewFaceFxPackage.Exports.FirstOrDefault(export =>
                export.ClassName == "FaceFXAsset"
                && export.ObjectNameString.Equals(actor.FaceFxAssetName, StringComparison.OrdinalIgnoreCase));
            if (baseGameAsset is not null)
            {
                return baseGameAsset;
            }
        }
        return null;
    }

    internal static string GetDirectionTrackActorTag(ExportEntry track)
    {
        string actorTag = track?.GetProperty<NameProperty>("m_nmFindActor")?.Value.Instanced
                          ?? track?.GetProperty<StrProperty>("m_nmFindActor")?.Value;
        return string.IsNullOrWhiteSpace(actorTag)
               || actorTag.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? null
            : actorTag;
    }

    private string FindDirectionTargetActor(StructProperty keyData, string sourceActorTag)
    {
        string candidate = keyData.GetProp<NameProperty>("nmFindActor")?.Value.Instanced
                           ?? keyData.GetProp<StrProperty>("nmFindActor")?.Value;
        return !string.IsNullOrWhiteSpace(candidate)
               && !candidate.Equals("None", StringComparison.OrdinalIgnoreCase)
               && !ActorTagMatches(candidate, sourceActorTag)
            ? candidate
            : null;
    }

    private CameraOrigin ApplyActorDirectionTracks(PreviewActorConfiguration actor, float time, CameraOrigin origin,
        IReadOnlyDictionary<PreviewActorConfiguration, CameraOrigin> resolvedActorOrigins, bool hasMovementTrack,
        PreviewActorAnimationState animationState)
        => ApplyActorDirectionTracks(actorDirectionTracks, actor, time, origin, resolvedActorOrigins,
            hasMovementTrack, animationState);

    private CameraOrigin ApplyActorDirectionTracks(IEnumerable<ActorDirectionTrack> directionTracks,
        PreviewActorConfiguration actor, float time, CameraOrigin origin,
        IReadOnlyDictionary<PreviewActorConfiguration, CameraOrigin> resolvedActorOrigins, bool hasMovementTrack,
        PreviewActorAnimationState animationState)
    {
        foreach (ActorDirectionTrack track in directionTracks.Where(track => ReferenceEquals(track.Actor, actor)))
        {
            ActorDirectionKey key = track.Keys.LastOrDefault(candidate => candidate.Time <= time);
            if (key is not { Enabled: true })
            {
                continue;
            }

            if (!track.IsLookAt && key.TargetStageNode is not null
                && dialogueNodePreview.StageContext.StageNodeOrigins.TryGetValue(key.TargetStageNode,
                    out CameraOrigin stageNodeOrigin))
            {
                ActorDirectionKey nextFacingKey = track.Keys.FirstOrDefault(candidate =>
                    candidate.Time > key.Time && candidate is { Enabled: true, TargetStageNode: not null });
                Vector3 rootMotionSinceFacingKey = !hasMovementTrack && nextFacingKey is not null
                    && animationState?.HasTimeline == true
                    ? animationState.EvaluateExtractedRootMotionDelta(key.Time, time)
                    : Vector3.Zero;
                origin = ApplySetFacingStageNode(origin, stageNodeOrigin, hasMovementTrack, key.OrientationOffset,
                    rootMotionSinceFacingKey, nextFacingKey is not null);
                continue;
            }

            if (!DirectionTrackControlsActorTransform(track.IsLookAt))
            {
                // BioEvtSysTrackLookAt drives the character gaze, not the pawn transform.
                // Rotating the whole preview actor here turns a forward root-motion walk sideways.
                continue;
            }

            PreviewActorConfiguration targetActor = key.TargetActorTag is null
                ? null
                : resolvedActorOrigins.Keys.FirstOrDefault(candidate =>
                    ActorTagMatches(key.TargetActorTag, candidate.ActorTag));
            if (targetActor is not null)
            {
                origin = ApplyActorDirectionRotation(origin, resolvedActorOrigins[targetActor].Location,
                    includePitch: true, orientationOffset: key.OrientationOffset);
            }
        }
        return origin;
    }

    internal static bool DirectionTrackControlsActorTransform(bool isLookAt) => !isLookAt;

    internal static Matrix4x4 ApplyLookAtBoneRotation(Matrix4x4 componentTransform, Vector3 targetComponent)
    {
        Vector3 direction = targetComponent - componentTransform.Translation;
        if (direction.LengthSquared() <= float.Epsilon)
        {
            return componentTransform;
        }

        float yaw = Math.Clamp(MathF.Atan2(direction.Y, direction.X) * (180f / MathF.PI), -70f, 70f);
        float horizontalDistance = MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        float pitch = Math.Clamp(MathF.Atan2(direction.Z, horizontalDistance) * (180f / MathF.PI), -35f, 35f);
        Matrix4x4 lookAtRotation = Rotator.FromDegreesVector(new Vector3(0, pitch, yaw)).ToRotationMatrix();
        Vector3 translation = componentTransform.Translation;
        componentTransform.Translation = Vector3.Zero;
        componentTransform *= lookAtRotation;
        componentTransform.Translation = translation;
        return componentTransform;
    }

    internal static CameraOrigin ApplySetFacingStageNode(CameraOrigin actorOrigin, CameraOrigin stageNodeOrigin,
        bool hasMovementTrack, float orientationOffset, Vector3 rootMotionSinceFacingKey = default,
        bool hasFollowingFacingKey = false)
    {
        Vector3 location = actorOrigin.Location;
        if (!hasMovementTrack)
        {
            // Stage nodes identify the body slot, while every preview actor renders its BodyMesh
            // 88 units below the pawn origin. Compensate for that shared render offset when a
            // SetFacing key supplies the pawn location.
            location = stageNodeOrigin.Location;
            location.Z -= PreviewBodyMeshRelativeZ;
        }
        Vector3 rotation = actorOrigin.Rotation;
        rotation.Z = stageNodeOrigin.Rotation.Z + orientationOffset;
        var facedOrigin = new CameraOrigin(location, rotation);
        return hasMovementTrack || !hasFollowingFacingKey
            ? facedOrigin
            : ApplyDialogueGestureRootMotion(facedOrigin, rootMotionSinceFacingKey);
    }

    internal static CameraOrigin ApplyActorDirectionRotation(CameraOrigin actorOrigin, Vector3 targetLocation,
        bool includePitch, float orientationOffset)
    {
        Vector3 direction = targetLocation - actorOrigin.Location;
        if (direction.LengthSquared() <= float.Epsilon)
        {
            return actorOrigin;
        }

        float yaw = MathF.Atan2(direction.Y, direction.X) * (180f / MathF.PI) + orientationOffset;
        Vector3 rotation = actorOrigin.Rotation;
        if (includePitch)
        {
            float horizontalDistance = MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
            rotation.Y = MathF.Atan2(direction.Z, horizontalDistance) * (180f / MathF.PI);
        }
        rotation.Z = yaw;
        return new CameraOrigin(actorOrigin.Location, rotation);
    }

    private int GetGestureActorMatchScore(GestureTrackOption gesture, string actorTag)
    {
        string findActor = gesture.Track?.GetProperty<NameProperty>("m_nmFindActor")?.Value.Instanced;
        int score = GetActorGroupMatchScore(gesture.Group, actorTag);
        if (ActorTagMatches(findActor, actorTag))
        {
            score += 4;
        }
        return score;
    }

    private void SetPreviewActorStatus(string status)
    {
        PreviewActorStatusTextBlock.Text = status;
    }

    private void ResolveDialoguePreviewActorConstructions()
    {
        foreach (PreviewActorConfiguration actor in previewActors)
        {
            if (actor.BaseGameModelsOnly)
            {
                continue;
            }

            if (actor.Construction?.Meshes.Count > 0
                && actor.Construction.Meshes.All(mesh =>
                    ResolveExportReference(mesh.MeshExport, required: false)?.ClassName == "SkeletalMesh"))
            {
                // Older caches may contain all of the actor meshes but no FaceFX reference because
                // the graph is normally owned by the inherited conversation module rather than a
                // property directly on the pawn. Repair that construction without rebuilding or
                // replacing its cached meshes.
                if (actor.Construction.FaceFxAsset is null
                    && ResolveExportReference(actor.Construction.SourceActor, required: false) is { } cachedSourceActor)
                {
                    IEnumerable<ExportEntry> cachedRelatedExports = actor.Construction.Meshes
                        .SelectMany(mesh => new[]
                        {
                            ResolveExportReference(mesh.ComponentExport, required: false),
                            ResolveExportReference(mesh.MeshExport, required: false),
                        })
                        .Where(export => export is not null);
                    actor.Construction.FaceFxAsset = CreateExportReference(
                        FindFaceFxAsset(cachedSourceActor, cachedRelatedExports));
                }
                actor.ModelName = GetCachedActorModelName(actor.Construction, PreviewActorModelComponent.Body);
                actor.HeadModelName = GetCachedActorModelName(actor.Construction, PreviewActorModelComponent.Head);
                actor.HairModelName = GetCachedActorModelName(actor.Construction, PreviewActorModelComponent.Hair);
                actor.FaceFxAssetName = actor.Construction.FaceFxAsset?.InstancedFullPath?.Split('.').LastOrDefault();
                continue;
            }

            IReadOnlyList<TaggedDialoguePreviewActor> taggedActors = FindTaggedDialoguePreviewActors(actor.ActorTag);
            ExportEntry sourceActor = ResolveExportReference(actor.Construction?.SourceActor, required: false)
                                      ?? taggedActors.FirstOrDefault()?.Actor;
            if (sourceActor is null)
            {
                actor.Construction = null;
                actor.ModelName = null;
                actor.HeadModelName = null;
                actor.HairModelName = null;
                actor.FaceFxAssetName = null;
                continue;
            }

            DialogueActorConstructionCache construction = BuildDialogueActorConstruction(actor.ActorTag, sourceActor);
            SupplementDialogueActorHeadAndHair(construction, sourceActor, taggedActors);
            if (construction.Meshes.Count == 0)
            {
                continue;
            }
            actor.Construction = construction;
            actor.ModelName = GetCachedActorModelName(construction, PreviewActorModelComponent.Body);
            actor.HeadModelName = GetCachedActorModelName(construction, PreviewActorModelComponent.Head);
            actor.HairModelName = GetCachedActorModelName(construction, PreviewActorModelComponent.Hair);
            actor.FaceFxAssetName = construction.FaceFxAsset?.InstancedFullPath?.Split('.').LastOrDefault();
        }
    }

    private IReadOnlyList<TaggedDialoguePreviewActor> FindTaggedDialoguePreviewActors(string actorTag)
    {
        if (previewAssetDatabase is null || string.IsNullOrWhiteSpace(actorTag))
        {
            return [];
        }

        IEnumerable<string> searchTags = dialoguePreviewActorTagAliases.GetValueOrDefault(actorTag)
            ?? [actorTag];
        TagUsage[] usages = searchTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag)
                          && !tag.Equals("player", StringComparison.OrdinalIgnoreCase)
                          && !tag.Equals("owner", StringComparison.OrdinalIgnoreCase))
            .Prepend(actorTag)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(tag => previewAssetDatabase.Tags
                .Where(record => record.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))
                .SelectMany(record => record.Usages))
            .DistinctBy(usage => (usage.FileKey, usage.UIndex))
            .OrderBy(GetDialogueActorUsagePriority)
            .ThenBy(usage => usage.FileKey)
            .ThenBy(usage => usage.UIndex)
            .ToArray();

        var actors = new List<TaggedDialoguePreviewActor>();
        bool foundBaseGameActor = false;
        bool foundBaseGameHead = false;
        bool foundBaseGameHair = false;
        foreach (TagUsage usage in usages)
        {
            if ((usage.IsInMod || usage.IsInDLC) && foundBaseGameActor)
            {
                // Head/hair supplementation is intentionally vanilla-only. Once the base-game
                // candidates are exhausted there is no reason to load DLC or mod packages.
                break;
            }
            if (!previewAssetFilePaths.TryGetValue(usage.FileKey, out string packagePath))
            {
                continue;
            }
            try
            {
                IMEPackage package = previewActorGesturePackageCache.GetCachedPackage(packagePath);
                if (package?.TryGetUExport(usage.UIndex, out ExportEntry candidate) == true
                    && ActorProxy.CanCreate(candidate) && ActorHasPreviewSkeletalMesh(candidate))
                {
                    actors.Add(new TaggedDialoguePreviewActor(candidate, usage));
                    if (!usage.IsInMod && !usage.IsInDLC)
                    {
                        foundBaseGameActor = true;
                        foundBaseGameHead |= ActorHasNamedPreviewSkeletalMesh(candidate,
                            "HeadMesh", "m_oHeadMesh");
                        foundBaseGameHair |= ActorHasNamedPreviewSkeletalMesh(candidate,
                            "HairMesh", "m_oHairMesh");
                        if (foundBaseGameHead && foundBaseGameHair)
                        {
                            break;
                        }
                    }
                    else
                    {
                        // There was no usable base-game actor. Use the first prioritized DLC/mod
                        // source without sourcing missing pieces from another mod.
                        break;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException)
            {
                // A database may outlive an installed mod. Continue to the next vanilla/DLC candidate.
            }
        }
        return actors;
    }

    private void SupplementDialogueActorHeadAndHair(DialogueActorConstructionCache construction,
        ExportEntry sourceActor, IReadOnlyList<TaggedDialoguePreviewActor> taggedActors)
    {
        bool missingHead = !HasDialogueActorPrimaryComponent(construction, PreviewActorModelComponent.Head,
            "HeadMesh");
        bool missingHair = !HasDialogueActorPrimaryComponent(construction, PreviewActorModelComponent.Hair,
            "HairMesh");
        if (!missingHead && !missingHair)
        {
            return;
        }

        foreach (TaggedDialoguePreviewActor taggedActor in taggedActors
                     .Where(candidate => !candidate.Usage.IsInMod && !candidate.Usage.IsInDLC
                                         && !IsSameExport(candidate.Actor, sourceActor)))
        {
            DialogueActorConstructionCache fallback = BuildDialogueActorConstruction(
                construction.ActorTag, taggedActor.Actor);
            if (missingHead && FindDialogueActorPrimaryComponent(fallback,
                    PreviewActorModelComponent.Head, "HeadMesh") is { } head)
            {
                construction.Meshes.Add(head);
                missingHead = false;
            }
            if (missingHair && FindDialogueActorPrimaryComponent(fallback,
                    PreviewActorModelComponent.Hair, "HairMesh") is { } hair)
            {
                construction.Meshes.Add(hair);
                missingHair = false;
            }
            construction.FaceFxAsset ??= fallback.FaceFxAsset;
            if (!missingHead && !missingHair)
            {
                break;
            }
        }
    }

    private static bool HasDialogueActorPrimaryComponent(DialogueActorConstructionCache construction,
        PreviewActorModelComponent component, string slotName) =>
        FindDialogueActorPrimaryComponent(construction, component, slotName) is not null;

    private static DialogueActorMeshCache FindDialogueActorPrimaryComponent(
        DialogueActorConstructionCache construction, PreviewActorModelComponent component, string slotName) =>
        construction?.Meshes?.FirstOrDefault(mesh => mesh.Component == component
                                                    && string.Equals(mesh.SlotName, slotName,
                                                        StringComparison.OrdinalIgnoreCase));

    private bool ActorHasPreviewSkeletalMesh(ExportEntry actor)
    {
        IEnumerable<ExportEntry> components = new[]
            {
                FindInheritedObjectExport(actor, "BodyMesh", "SkeletalMeshComponent", "Mesh"),
                FindInheritedObjectExport(actor, "HeadMesh", "m_oHeadMesh"),
                FindInheritedObjectExport(actor, "HairMesh", "m_oHairMesh"),
            }
            .Concat(EnumerateInheritedObjectExports(actor)
                .Where(pair => pair.Export?.IsA("SkeletalMeshComponent") == true)
                .Select(pair => pair.Export))
            .Where(component => component is not null)
            .DistinctBy(component => (component.FileRef, component.UIndex));
        return components.Any(component => component.ClassName == "SkeletalMesh"
                                           || FindInheritedObjectExport(component, "SkeletalMesh")?.ClassName
                                           == "SkeletalMesh");
    }

    private bool ActorHasNamedPreviewSkeletalMesh(ExportEntry actor, params string[] propertyNames)
    {
        ExportEntry component = FindInheritedObjectExport(actor, propertyNames);
        return component?.ClassName == "SkeletalMesh"
               || FindInheritedObjectExport(component, "SkeletalMesh")?.ClassName == "SkeletalMesh";
    }

    internal static int GetDialogueActorClassPriority(string className)
    {
        if (string.IsNullOrWhiteSpace(className)) return 100;
        if (className.Contains("StuntActor", StringComparison.OrdinalIgnoreCase)) return 0;
        if (className.Contains("BioPawn", StringComparison.OrdinalIgnoreCase)) return 1;
        if (className.Contains("SFXPawn", StringComparison.OrdinalIgnoreCase)) return 2;
        if (className.EndsWith("Pawn", StringComparison.OrdinalIgnoreCase)) return 3;
        if (className.Contains("SFXSkeletalMeshActor", StringComparison.OrdinalIgnoreCase)) return 4;
        if (className.Contains("SkeletalMeshActor", StringComparison.OrdinalIgnoreCase)) return 5;
        return 50;
    }

    internal static (int Mod, int Dlc, int Class, int Context) GetDialogueActorUsagePriority(TagUsage usage) =>
        (usage.IsInMod ? 1 : 0,
            usage.IsInDLC ? 1 : 0,
            GetDialogueActorClassPriority(usage.ClassName),
            usage.Context == TagUsageContext.TaggedObject ? 0 : 1);

    private DialogueActorConstructionCache BuildDialogueActorConstruction(string actorTag, ExportEntry sourceActor)
    {
        var construction = new DialogueActorConstructionCache
        {
            ActorTag = actorTag,
            SourceActor = CreateExportReference(sourceActor),
        };
        var components = new List<(PreviewActorModelComponent Kind, string SlotName, ExportEntry Export)>();
        var seenComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddActorComponent(PreviewActorModelComponent.Body,
            "BodyMesh", FindInheritedObjectExport(sourceActor, "BodyMesh", "SkeletalMeshComponent", "Mesh"));
        AddActorComponent(PreviewActorModelComponent.Head,
            "HeadMesh", FindInheritedObjectExport(sourceActor, "HeadMesh", "m_oHeadMesh"));
        AddActorComponent(PreviewActorModelComponent.Hair,
            "HairMesh", FindInheritedObjectExport(sourceActor, "HairMesh", "m_oHairMesh"));

        foreach ((string propertyName, ExportEntry component) in EnumerateInheritedObjectExports(sourceActor)
                     .Where(pair => pair.Export?.IsA("SkeletalMeshComponent") == true))
        {
            AddActorComponent(GetComponentKind(propertyName), propertyName, component);
        }
        foreach ((string propertyName, ExportEntry component) in EnumerateInheritedObjectArrayExports(sourceActor)
                     .Where(pair => pair.Export?.IsA("SkeletalMeshComponent") == true))
        {
            AddActorComponent(GetComponentKind(propertyName), propertyName, component);
        }

        ExportEntry morphHead = FindFaceOrMorphExport(sourceActor, "MorphHead");
        bool hasSeparateHead = components.Any(component => component.Kind == PreviewActorModelComponent.Head);
        foreach ((PreviewActorModelComponent componentKind, string slotName, ExportEntry component) in components)
        {
            ExportEntry meshExport = component.ClassName == "SkeletalMesh"
                ? component
                : FindInheritedObjectExport(component, "SkeletalMesh");
            if (meshExport?.ClassName != "SkeletalMesh")
            {
                continue;
            }
            ExportEntry componentMorph = FindFaceOrMorphExport(component, "MorphHead") ?? morphHead;
            bool isPrimaryHead = componentKind == PreviewActorModelComponent.Head
                                 && slotName.Equals("HeadMesh", StringComparison.OrdinalIgnoreCase);
            bool isMorphFollower = componentKind == PreviewActorModelComponent.Hair
                                   && slotName.Equals("HairMesh", StringComparison.OrdinalIgnoreCase);
            bool isSinglePawnMesh = componentKind == PreviewActorModelComponent.Body && !hasSeparateHead;
            bool applyMorph = isPrimaryHead || isMorphFollower || isSinglePawnMesh;
            construction.Meshes.Add(new DialogueActorMeshCache
            {
                Component = componentKind,
                SlotName = slotName,
                ComponentExport = CreateExportReference(component),
                MeshExport = CreateExportReference(meshExport),
                MaterialOverrides = FindInheritedObjectArrayExports(component, "Materials")
                    .Select(CreateExportReference).ToList(),
                MorphHead = applyMorph ? CreateExportReference(componentMorph) : null,
                UseStoredMorphLods = isPrimaryHead || isSinglePawnMesh,
                LocalTransform = DialogueMatrixCache.FromMatrix(GetDialogueComponentLocalTransform(
                    sourceActor, componentKind, component)),
            });
        }

        construction.FaceFxAsset = CreateExportReference(FindFaceFxAsset(sourceActor,
            components.Select(component => component.Export)
                .Concat(construction.Meshes.Select(mesh => ResolveExportReference(mesh.MeshExport,
                    required: false)))));
        return construction;

        PreviewActorModelComponent GetComponentKind(string propertyName)
        {
            string name = propertyName ?? string.Empty;
            if (name.Equals("HeadMesh", StringComparison.OrdinalIgnoreCase)
                || name.Equals("m_oHeadMesh", StringComparison.OrdinalIgnoreCase))
            {
                return PreviewActorModelComponent.Head;
            }
            if (name.Equals("HairMesh", StringComparison.OrdinalIgnoreCase)
                || name.Equals("m_oHairMesh", StringComparison.OrdinalIgnoreCase))
            {
                return PreviewActorModelComponent.Hair;
            }
            return components.Any(existing => existing.Kind == PreviewActorModelComponent.Body)
                ? PreviewActorModelComponent.Hair
                : PreviewActorModelComponent.Body;
        }

        void AddActorComponent(PreviewActorModelComponent kind, string slotName, ExportEntry component)
        {
            if (component is null) return;
            string exportKey = $"{component.FileRef.FilePath}|{component.UIndex}";
            if (!seenComponents.Add(exportKey)) return;
            string uniqueSlot = string.IsNullOrWhiteSpace(slotName) ? kind.ToString() : slotName;
            if (components.Any(existing => existing.SlotName.Equals(uniqueSlot,
                    StringComparison.OrdinalIgnoreCase)))
            {
                uniqueSlot = $"{uniqueSlot}:{component.UIndex}";
            }
            components.Add((kind, uniqueSlot, component));
        }
    }

    private static Matrix4x4 GetDialogueComponentLocalTransform(ExportEntry sourceActor,
        PreviewActorModelComponent componentKind, ExportEntry component)
    {
        PropertyCollection properties = component.GetCondensedProperties();
        Vector3 translation = properties.GetProp<StructProperty>("Translation") is { } translationProperty
            ? CommonStructs.GetVector3(translationProperty)
            : Vector3.Zero;
        if (componentKind == PreviewActorModelComponent.Body && translation == Vector3.Zero
            && sourceActor.IsA("SFXStuntActor"))
        {
            translation = new Vector3(0, 0, PreviewBodyMeshRelativeZ);
        }
        Rotator rotation = properties.GetProp<StructProperty>("Rotation") is { } rotationProperty
            ? CommonStructs.GetRotator(rotationProperty)
            : new Rotator(0, 0, 0);
        Vector3 scale3D = properties.GetProp<StructProperty>("Scale3D") is { } scaleProperty
            ? CommonStructs.GetVector3(scaleProperty)
            : Vector3.One;
        float scale = properties.GetProp<FloatProperty>("Scale")?.Value ?? 1f;
        return ActorUtils.ComposeLocalToWorld(translation, rotation, scale * scale3D);
    }

    private ExportEntry FindFaceFxAsset(ExportEntry actor, IEnumerable<ExportEntry> relatedExports)
    {
        foreach (ExportEntry source in new[] { actor }.Concat(relatedExports).Where(export => export is not null))
        {
            if (FindFaceFxAssetReference(source) is { } directAsset)
            {
                return directAsset;
            }

            // SFXPawns and SFXStuntActors normally obtain their facial graph from an inherited
            // SFXModule_Conversation in Modules. The pawn itself therefore has no FaceFX object
            // property even though the in-game actor has a valid m_pDefaultFaceFXAsset.
            foreach (ExportEntry module in FindInheritedObjectArrayExports(source, "Modules")
                         .Where(export => export is not null))
            {
                if (FindFaceFxAssetReference(module) is { } moduleAsset)
                {
                    return moduleAsset;
                }
            }
        }
        return null;
    }

    private ExportEntry FindFaceFxAssetReference(ExportEntry source) =>
        EnumerateInheritedObjectExports(source)
            .FirstOrDefault(pair => pair.PropertyName.Contains("FaceFX", StringComparison.OrdinalIgnoreCase)
                                    && pair.Export?.ClassName == "FaceFXAsset")
            .Export;

    private ExportEntry FindFaceOrMorphExport(ExportEntry source, string propertyName)
    {
        ExportEntry export = FindInheritedObjectExport(source, propertyName);
        return export?.ClassName is "BioMorphFace" or "FaceFXAsset" ? export : null;
    }

    private ExportEntry FindInheritedObjectExport(ExportEntry source, params string[] propertyNames)
    {
        foreach (ExportEntry current in EnumeratePreviewArchetypeChain(source))
        {
            PropertyCollection properties = current.GetProperties();
            foreach (string propertyName in propertyNames)
            {
                if (properties.GetProp<ObjectProperty>(propertyName) is { Value: not 0 } reference
                    && RenderContext.ResolveExportCached(current.FileRef, reference.Value) is { } export)
                {
                    return export;
                }
            }
        }
        return null;
    }

    private IEnumerable<ExportEntry> FindInheritedObjectArrayExports(ExportEntry source, string propertyName)
    {
        foreach (ExportEntry current in EnumeratePreviewArchetypeChain(source))
        {
            if (current.GetProperties().GetProp<ArrayProperty<ObjectProperty>>(propertyName) is not { } references)
            {
                continue;
            }
            foreach (ObjectProperty reference in references)
            {
                yield return reference.Value == 0
                    ? null
                    : RenderContext.ResolveExportCached(current.FileRef, reference.Value);
            }
            yield break;
        }
    }

    private IEnumerable<(string PropertyName, ExportEntry Export)> EnumerateInheritedObjectExports(ExportEntry source)
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ExportEntry current in EnumeratePreviewArchetypeChain(source))
        {
            foreach (ObjectProperty property in current.GetProperties().OfType<ObjectProperty>())
            {
                string propertyName = property.Name.Instanced;
                if (property.Value != 0 && seenNames.Add(propertyName)
                    && RenderContext.ResolveExportCached(current.FileRef, property.Value) is { } export)
                {
                    yield return (propertyName, export);
                }
            }
        }
    }

    private IEnumerable<(string PropertyName, ExportEntry Export)> EnumerateInheritedObjectArrayExports(
        ExportEntry source)
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ExportEntry current in EnumeratePreviewArchetypeChain(source))
        {
            foreach (ArrayProperty<ObjectProperty> property in current.GetProperties()
                         .OfType<ArrayProperty<ObjectProperty>>())
            {
                string propertyName = property.Name.Instanced;
                if (!seenNames.Add(propertyName)) continue;
                for (int index = 0; index < property.Count; index++)
                {
                    ObjectProperty item = property[index];
                    if (item.Value != 0 && RenderContext.ResolveExportCached(current.FileRef, item.Value) is { } export)
                    {
                        yield return ($"{propertyName}[{index}]", export);
                    }
                }
            }
        }
    }

    private IEnumerable<ExportEntry> EnumeratePreviewArchetypeChain(ExportEntry source)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (ExportEntry current = source; current is not null;)
        {
            string key = $"{current.FileRef.FilePath}|{current.UIndex}";
            if (!visited.Add(key)) yield break;
            yield return current;
            current = current.Archetype switch
            {
                ExportEntry archetype => archetype,
                ImportEntry import => RenderContext.ResolveExportCached(import),
                _ => null,
            };
        }
    }

    private HashSet<PreviewActorModelComponent> LoadCachedActorConstruction(int actorIndex,
        PreviewActorConfiguration actor)
    {
        var loaded = new HashSet<PreviewActorModelComponent>();
        Matrix4x4? bodyLocalTransform = actor.Construction?.Meshes
            .FirstOrDefault(mesh => mesh.Component == PreviewActorModelComponent.Body)
            ?.LocalTransform?.ToMatrix();
        foreach (DialogueActorMeshCache mesh in (actor.Construction?.Meshes ?? []).OrderBy(mesh => mesh.Component))
        {
            ExportEntry meshExport = ResolveExportReference(mesh.MeshExport, required: false);
            if (meshExport?.ClassName != "SkeletalMesh")
            {
                continue;
            }
            IReadOnlyList<IEntry> materialOverrides = mesh.MaterialOverrides
                .Select(reference => ResolveExportReference(reference, required: false))
                .Cast<IEntry>().ToArray();
            ExportEntry morphHead = ResolveExportReference(mesh.MorphHead, required: false);
            try
            {
                LoadPreviewActorModel(actorIndex, mesh.Component, meshExport, materialOverrides, morphHead,
                    mesh.UseStoredMorphLods, mesh.SlotName, bodyLocalTransform);
                SetPreviewActorModelName(actor, mesh.Component, meshExport.ObjectNameString);
                loaded.Add(mesh.Component);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException
                                              or NotSupportedException)
            {
                // Fall back to the cached model name through the asset database below.
            }
        }
        return loaded;
    }

    private bool TryLoadPreviewActorModel(int actorIndex, PreviewActorModelComponent component,
        MeshRecord meshRecord, bool baseGameOnly, out string error)
    {
        error = null;
        if (CurrentLoadedExport is null || previewAssetDatabase is null)
        {
            error = "The actor model database is not loaded.";
            return false;
        }

        if (previewAssetFilePaths.Count == 0)
        {
            error = $"The configured {CurrentLoadedExport.Game} game directory could not be found.";
            return false;
        }

        foreach (MeshUsage usage in PreviewActorModelDefaults.GetUsages(meshRecord, previewAssetDatabase, baseGameOnly))
        {
            if (usage.FileKey < 0 || usage.FileKey >= previewAssetFiles.Count)
            {
                continue;
            }

            if (!previewAssetFilePaths.TryGetValue(usage.FileKey, out string filePath))
            {
                continue;
            }

            IMEPackage meshPackage = previewActorGesturePackageCache.GetCachedPackage(filePath);
            if (!meshPackage.IsUExport(usage.UIndex))
            {
                continue;
            }

            ExportEntry meshExport = meshPackage.GetUExport(usage.UIndex);
            if (!string.Equals(meshExport.ClassName, "SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                LoadPreviewActorModel(actorIndex, component, meshExport);
                if (dialogueNodePreview is not null && actorIndex < previewActors.Count)
                {
                    PreviewActorConfiguration actor = previewActors[actorIndex];
                    actor.Construction ??= new DialogueActorConstructionCache { ActorTag = actor.ActorTag };
                    actor.Construction.Meshes.RemoveAll(cached => cached.Component == component);
                    actor.Construction.Meshes.Add(new DialogueActorMeshCache
                    {
                        Component = component,
                        SlotName = component.ToString(),
                        MeshExport = CreateExportReference(meshExport),
                    });
                }
                return true;
            }
            catch (Exception exception)
            {
                error = $"Unable to render {meshRecord.MeshName}: {exception.Message}";
                return false;
            }
        }

        error = $"No installed package containing {meshRecord.MeshName} could be resolved.";
        return false;
    }

    private void InitializePreviewActorLayout(MEGame game)
    {
        if (previewActorGame == game)
        {
            return;
        }

        SavePreviewActorLayout();
        ClearPreviewActorModels();
        previewActors.Clear();
        previewActorGestureAssignments.Clear();
        previewActorTrackAssignments.Clear();
        previewActorGame = game;
        previewAssetDatabase = null;
        previewAssetFiles = [];
        previewAssetFilePaths = [];
        PreviewActorTextBox.Clear();
        PreviewActorHeadTextBox.Clear();
        PreviewActorHairTextBox.Clear();
        if (dialogueNodePreview is not null)
        {
            InitializeDialoguePreviewActors();
        }
        else
        {
            LoadPreviewActorLayout();
        }
        _ = InitializePreviewActorModelsAsync();
    }

    private void InitializeDialoguePreviewActors()
    {
        Dictionary<string, DialogueActorConstructionCache> cachedConstructions = dialogueNodePreview.CachePreset?.Actors
            .Where(actor => !string.IsNullOrWhiteSpace(actor.ActorTag))
            .ToDictionary(actor => actor.ActorTag, StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (DialogueNodePreviewActor previewActor in dialogueNodePreview.Actors
                     .Where(actor => !string.IsNullOrWhiteSpace(actor.ActorTag))
                     .DistinctBy(actor => actor.ActorTag, StringComparer.OrdinalIgnoreCase))
        {
            bool isPlayer = string.Equals(previewActor.ActorTag, "player", StringComparison.OrdinalIgnoreCase);
            DialoguePreviewPlayerSelection player = dialogueNodePreview.PlayerSelection;
            cachedConstructions.TryGetValue(previewActor.ActorTag, out DialogueActorConstructionCache construction);
            if (isPlayer && dialogueNodePreview.CachePreset?.PlayerGender != player.Gender)
            {
                construction = null;
            }
            previewActors.Add(new PreviewActorConfiguration
            {
                ActorTag = previewActor.ActorTag,
                DisplayName = previewActor.ActorTag,
                BaseGameModelsOnly = isPlayer,
                ModelName = isPlayer ? player.BodyModelName : GetCachedActorModelName(construction,
                    PreviewActorModelComponent.Body),
                HeadModelName = isPlayer ? player.HeadModelName : GetCachedActorModelName(construction,
                    PreviewActorModelComponent.Head),
                HairModelName = isPlayer ? player.HairModelName : GetCachedActorModelName(construction,
                    PreviewActorModelComponent.Hair),
                FaceFxAssetName = isPlayer ? player.AssetName : construction?.FaceFxAsset?.InstancedFullPath?
                    .Split('.').LastOrDefault(),
                Construction = isPlayer
                    ? construction ?? new DialogueActorConstructionCache { ActorTag = previewActor.ActorTag }
                    : construction,
                Origin = previewActor.Origin,
            });
        }

        AssignDialoguePreviewTrackMoves();
        PreviewActorListBox.SelectedIndex = previewActors.Count > 0 ? 0 : -1;
        PreviewActorConfiguration speakingActor = previewActors.FirstOrDefault(IsDialogueNodeSpeaker);
        if (speakingActor is not null)
        {
            PreviewActorListBox.SelectedItem = speakingActor;
        }
        DialoguePreviewActorListBox.SelectedIndex = PreviewActorListBox.SelectedIndex;
        PreviewActorGestureComboBox.Items.Refresh();
    }

    private static string GetCachedActorModelName(DialogueActorConstructionCache construction,
        PreviewActorModelComponent component) => construction?.Meshes
        .FirstOrDefault(mesh => mesh.Component == component)?.MeshExport?.InstancedFullPath?
        .Split('.').LastOrDefault();

    private void BuildDialoguePreviewActorTagAliases(IReadOnlyList<DialogueNodePreviewActor> actors,
        StageConversationContext stageContext)
    {
        dialoguePreviewActorTagAliases.Clear();
        Dictionary<string, HashSet<string>> stageAliases = BuildActorTagAliases(
            actors.Select(actor => actor.ActorTag), stageContext.ActorOrigins);
        foreach (DialogueNodePreviewActor actor in actors)
        {
            HashSet<string> aliases = stageAliases.GetValueOrDefault(actor.ActorTag)
                                      ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            aliases.Add(actor.ActorTag);
            aliases.UnionWith(actor.Aliases ?? []);
            dialoguePreviewActorTagAliases[actor.ActorTag] = aliases;
        }
    }

    internal static Dictionary<string, HashSet<string>> BuildActorTagAliases(IEnumerable<string> actorTags,
        IReadOnlyDictionary<string, CameraOrigin> actorOrigins)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string actorTag in actorTags.Where(tag => !string.IsNullOrWhiteSpace(tag))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { actorTag };
            if (actorOrigins.TryGetValue(actorTag, out CameraOrigin actorOrigin))
            {
                foreach ((string authoredTag, CameraOrigin authoredOrigin) in actorOrigins)
                {
                    // StartConversation exposes both its generic Owner/Player slot and the linked
                    // actor's real tag. Identical slot transforms make that relationship explicit.
                    if (HaveEquivalentActorAliasOrigins(actorOrigin, authoredOrigin))
                    {
                        aliases.Add(authoredTag);
                    }
                }
            }
            result[actorTag] = aliases;
        }
        return result;
    }

    private static bool HaveEquivalentActorAliasOrigins(CameraOrigin left, CameraOrigin right) =>
        Vector3.DistanceSquared(left.Location, right.Location) <= 0.0001f
        && Vector3.DistanceSquared(left.Rotation, right.Rotation) <= 0.0001f;

    private bool ActorTagMatches(string authoredTag, string actorTag)
        => ActorTagMatchesAlias(authoredTag, actorTag, dialoguePreviewActorTagAliases);

    internal static bool ActorTagMatchesAlias(string authoredTag, string actorTag,
        IReadOnlyDictionary<string, HashSet<string>> actorAliases)
    {
        if (string.IsNullOrWhiteSpace(authoredTag) || string.IsNullOrWhiteSpace(actorTag)
            || authoredTag.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (string.Equals(authoredTag, actorTag, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return actorAliases.TryGetValue(actorTag, out HashSet<string> aliases)
               && aliases.Contains(authoredTag);
    }

    private void AssignDialoguePreviewTrackMoves()
    {
        previewActorTrackAssignments.Clear();
        ExportEntry[] cameraTrackMoves = dialoguePreviewCameraActors
            .Where(option => option?.TrackMove is not null)
            .Select(option => option.TrackMove)
            .ToArray();
        foreach (PreviewActorConfiguration actor in previewActors)
        {
            TrackMovePlaybackOption trackMove = availableTrackMoves
                .Where(option => IsEligibleActorTrackMove(option.TrackMove, cameraTrackMoves))
                .Where(option => IsEligibleActorTrackGroup(option.Group, actor.ActorTag))
                .Select(option => new { Option = option, Score = GetActorGroupMatchScore(option.Group, actor.ActorTag) })
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                // A Matinee group can contain more than one TrackMove. The stunt actor is driven by
                // the authored spline, so prefer the track that actually changes position instead of
                // whichever track happened to be serialized first in InterpTracks.
                .ThenByDescending(candidate => GetAuthoredMovementRank(candidate.Option))
                .Select(candidate => candidate.Option)
                .FirstOrDefault();
            if (trackMove is not null)
            {
                previewActorTrackAssignments[actor] = trackMove;
            }
        }
    }

    private static int GetAuthoredMovementRank(TrackMovePlaybackOption trackMove)
    {
        IReadOnlyList<InterpCurvePoint<Vector3>> points = trackMove?.Model?.PositionTrack?.Points;
        if (points is not { Count: > 0 })
        {
            return 0;
        }
        if (points.Count == 1)
        {
            return 1;
        }

        Vector3 first = points[0].OutVal;
        return points.Skip(1).Any(point => Vector3.DistanceSquared(first, point.OutVal) > 0.0001f)
            ? 3
            : 2;
    }

    private int GetActorGroupMatchScore(ExportEntry group, string actorTag)
    {
        if (group is null || string.IsNullOrWhiteSpace(actorTag) || !IsActorMatchingInterpGroup(group))
        {
            return 0;
        }

        bool groupNameMatches = ActorTagMatches(GetInterpGroupName(group), actorTag);
        bool findActorMatches = ActorTagMatches(
            group.GetProperty<NameProperty>("m_nmSFXFindActor")?.Value.Instanced, actorTag);
        return (groupNameMatches ? 1 : 0) + (findActorMatches ? 2 : 0);
    }

    private void LoadPreviewActorLayout()
    {
        try
        {
            if (File.Exists(SavedPreviewActorsPath))
            {
                List<PreviewActorConfiguration> actors = JsonConvert.DeserializeObject<List<PreviewActorConfiguration>>(
                    File.ReadAllText(SavedPreviewActorsPath));
                if (actors is { Count: > 0 })
                {
                    foreach (PreviewActorConfiguration actor in actors)
                    {
                        actor.ModelName ??= PreviewActorModelDefaults.BodyMeshName;
                        actor.HeadModelName ??= PreviewActorModelDefaults.HeadMeshName;
                        actor.HairModelName ??= PreviewActorModelDefaults.HairMeshName;
                        previewActors.Add(actor);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            SetPreviewActorStatus($"Saved actor layout could not be loaded: {exception.Message}");
        }

        RenumberPreviewActors();
        PreviewActorListBox.SelectedIndex = previewActors.Count > 0 ? 0 : -1;
        PreviewActorGestureComboBox.Items.Refresh();
    }

    private void SavePreviewActorLayout()
    {
        if (previewActorGame == MEGame.Unknown || dialogueNodePreview is not null)
        {
            return;
        }

        try
        {
            File.WriteAllText(SavedPreviewActorsPath,
                JsonConvert.SerializeObject(previewActors.ToList(), Formatting.Indented));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetPreviewActorStatus($"Actor layout could not be saved: {exception.Message}");
        }
    }

    private void AddDefaultPreviewActor()
    {
        CameraOrigin origin = SelectedKeyframe is null
            ? new CameraOrigin(RenderContext.Camera.Position, Vector3.Zero)
            : new CameraOrigin(SelectedKeyframe.Location, SelectedKeyframe.Rotation);
        var actor = new PreviewActorConfiguration
        {
            ModelName = PreviewActorModelDefaults.BodyMeshName,
            HeadModelName = PreviewActorModelDefaults.HeadMeshName,
            HairModelName = PreviewActorModelDefaults.HairMeshName,
            Origin = origin
        };
        previewActors.Add(actor);
        if (previewActors.Count == 1 && primaryTrackMove is not null)
        {
            previewActorTrackAssignments[actor] = primaryTrackMove;
        }
        RenumberPreviewActors();
        PreviewActorListBox.SelectedItem = actor;
        RefreshKeyframeTrackMoveTabs();
    }

    private void AddPreviewActor_Click(object sender, RoutedEventArgs e)
    {
        AddDefaultPreviewActor();
        LoadSelectedPreviewActorModel();
        SavePreviewActorLayout();
        SceneViewer.MarkRenderDirty();
    }

    private void RemovePreviewActor_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPreviewActor is null)
        {
            return;
        }

        PreviewActorConfiguration actorToRemove = selectedPreviewActor;
        if (isPlayingActor && playbackActors.Any(state => ReferenceEquals(state.Actor, actorToRemove)))
        {
            StopPlayback();
        }

        int removedIndex = previewActors.IndexOf(actorToRemove);
        previewActorAnimationStates.Remove(actorToRemove);
        previewActorGestureAssignments.Remove(actorToRemove);
        previewActorTrackAssignments.Remove(actorToRemove);
        previewActors.RemoveAt(removedIndex);
        RemovePreviewActorModel(removedIndex);
        RenumberPreviewActors();
        PreviewActorListBox.SelectedIndex = Math.Min(removedIndex, previewActors.Count - 1);
        RefreshKeyframeTrackMoveTabs();
        SavePreviewActorLayout();
    }

    private void ClearPreviewActors_Click(object sender, RoutedEventArgs e)
    {
        if (isPlayingActor)
        {
            StopPlayback();
        }
        previewActors.Clear();
        previewActorGestureAssignments.Clear();
        previewActorTrackAssignments.Clear();
        ClearPreviewActorModels();
        RenumberPreviewActors();
        PreviewActorListBox.SelectedIndex = -1;
        RefreshKeyframeTrackMoveTabs();
        SetPreviewActorStatus("Preview actors cleared.");
        SavePreviewActorLayout();
    }

    private void RenumberPreviewActors()
    {
        for (int index = 0; index < previewActors.Count; index++)
        {
            previewActors[index].DisplayName = string.IsNullOrWhiteSpace(previewActors[index].ActorTag)
                ? $"Actor {index + 1}"
                : previewActors[index].ActorTag;
        }
        PreviewActorListBox.Items.Refresh();
        RemovePreviewActorButton.IsEnabled = previewActors.Count > 0;
    }

    private void PreviewActorModel_Select_Click(object sender, RoutedEventArgs e)
    {
        if (updatingPreviewActorControls || selectedPreviewActor is null
            || sender is not Button { Tag: string componentName }
            || !Enum.TryParse(componentName, out PreviewActorModelComponent component))
        {
            return;
        }
        MeshRecord meshRecord = PreviewActorModelDefaults.SelectMesh(this, previewActorMeshes, component,
            GetPreviewActorModelName(selectedPreviewActor, component));
        if (meshRecord is null) return;

        int actorIndex = previewActors.IndexOf(selectedPreviewActor);
        if (PreviewActorModelDefaults.IsNone(meshRecord))
        {
            previewActorModels.ElementAtOrDefault(actorIndex)?.Remove(component);
            SetPreviewActorModelName(selectedPreviewActor, component, string.Empty);
            SetPreviewActorStatus($"{selectedPreviewActor.DisplayName} {component.ToString().ToLowerInvariant()}: None");
            SavePreviewActorLayout();
            SceneViewer.MarkRenderDirty();
            return;
        }
        if (!TryLoadPreviewActorModel(actorIndex, component, meshRecord, false, out string error))
        {
            SetPreviewActorStatus(error);
            return;
        }

        SetPreviewActorModelName(selectedPreviewActor, component, meshRecord.MeshName);
        SetPreviewActorStatus($"{selectedPreviewActor.DisplayName} {component.ToString().ToLowerInvariant()}: {meshRecord.MeshName}");
        SavePreviewActorLayout();
        SceneViewer.MarkRenderDirty();
    }

    private void LoadSelectedPreviewActorModel()
    {
        if (selectedPreviewActor is null || previewActorMeshes.Count == 0)
        {
            return;
        }
        int actorIndex = previewActors.IndexOf(selectedPreviewActor);
        foreach (PreviewActorModelComponent component in Enum.GetValues<PreviewActorModelComponent>())
        {
            MeshRecord mesh = FindConfiguredPreviewActorMesh(previewActorMeshes, selectedPreviewActor, component);
            if (mesh is not null)
            {
                TryLoadPreviewActorModel(actorIndex, component, mesh, false, out _);
            }
        }
    }

    private static MeshRecord FindConfiguredPreviewActorMesh(IEnumerable<MeshRecord> meshes,
        PreviewActorConfiguration actor, PreviewActorModelComponent component) =>
        meshes.FirstOrDefault(mesh => string.Equals(mesh.MeshName, GetPreviewActorModelName(actor, component),
            StringComparison.OrdinalIgnoreCase));

    private static string GetPreviewActorModelName(PreviewActorConfiguration actor,
        PreviewActorModelComponent component) => component switch
    {
        PreviewActorModelComponent.Body => actor.ModelName,
        PreviewActorModelComponent.Head => actor.HeadModelName,
        PreviewActorModelComponent.Hair => actor.HairModelName,
        _ => null
    };

    private static void SetPreviewActorModelName(PreviewActorConfiguration actor,
        PreviewActorModelComponent component, string modelName)
    {
        switch (component)
        {
            case PreviewActorModelComponent.Body: actor.ModelName = modelName; break;
            case PreviewActorModelComponent.Head: actor.HeadModelName = modelName; break;
            case PreviewActorModelComponent.Hair: actor.HairModelName = modelName; break;
        }
    }

    private void ConfigureKeyframeContextMenu()
    {
        KeyframeList.PreviewMouseRightButtonDown += KeyframeList_PreviewMouseRightButtonDown;
        KeyframeList.ContextMenu = CreateKeyframeContextMenu();
    }

    private ContextMenu CreateKeyframeContextMenu()
    {
        var menu = new ContextMenu();

        var deleteItem = new MenuItem { Header = "Delete Keyframe" };
        deleteItem.Click += DeleteKeyframe_Click;
        menu.Items.Add(deleteItem);

        var snapCameraItem = new MenuItem { Header = "Snap Camera to Key" };
        snapCameraItem.Click += SnapCameraToKey_Click;
        menu.Items.Add(snapCameraItem);

        menu.Items.Add(new Separator());

        var translateItem = new MenuItem { Header = "Translate" };
        translateItem.Click += TranslateMode_Click;
        menu.Items.Add(translateItem);

        var rollItem = new MenuItem { Header = "ROT Roll (X)", Tag = "X" };
        rollItem.Click += RotateMode_Click;
        menu.Items.Add(rollItem);

        var pitchItem = new MenuItem { Header = "ROT Pitch (Y)", Tag = "Y" };
        pitchItem.Click += RotateMode_Click;
        menu.Items.Add(pitchItem);

        var yawItem = new MenuItem { Header = "ROT Yaw (Z)", Tag = "Z" };
        yawItem.Click += RotateMode_Click;
        menu.Items.Add(yawItem);

        return menu;
    }

    private void OnThemeChanged(object sender, bool isDarkMode)
    {
        BackgroundColor = LevelEditor.GetThemeDefaultBackgroundColor();
    }

    private void LoadCommands()
    {
        ToggleTranslateCommand = new GenericCommand(() => RenderContext.TransformWidget.Mode = EWidgetMode.Translate);
        ToggleRotateCommand = new GenericCommand(() =>
        {
            RenderContext.TransformWidget.Mode = EWidgetMode.Rotate;
            RenderContext.TransformWidget.VisibleAxes = EWidgetAxis.XYZ;
        });
        ToggleScaleCommand = new GenericCommand(() => RenderContext.TransformWidget.Mode = EWidgetMode.Scale);
        ToggleUniformScaleCommand = new GenericCommand(() => RenderContext.TransformWidget.Mode = EWidgetMode.UniformScale);
        ToggleLocalCoordsCommand = new GenericCommand(() => UseLocalCoordsForWidget = !UseLocalCoordsForWidget);
    }

    private async void CurveEditor3D_Loaded(object sender, RoutedEventArgs e)
    {
        AttachEvents();
        SceneViewer.SetShouldRender(true);
        await RestoreSessionLevelsAsync().ConfigureAwait(true);
    }

    private async Task RestoreSessionLevelsAsync()
    {
        if (sessionLevelsRestored)
        {
            return;
        }

        List<string> paths;
        lock (sessionLevelPathsLock)
        {
            paths = sessionLevelPaths.Where(File.Exists).ToList();
        }
        if (paths.Count == 0)
        {
            return;
        }

        sessionLevelsRestored = true;
        foreach (string path in paths)
        {
            await LoadLevelAsync(path, replace: false, updateSession: false).ConfigureAwait(true);
        }
    }

    private void CurveEditor3D_Unloaded(object sender, RoutedEventArgs e)
    {
        SceneViewer.SetShouldRender(false);
    }

    private void AttachEvents()
    {
        if (eventsAttached)
        {
            return;
        }

        RenderContext.RenderScene += RenderScene;
        RenderContext.SelectHitProxy += SelectHitProxy;
        RenderContext.RightClickHitProxy += RightClickHitProxy;
        RenderContext.SelectActor += IgnoreActorSelection;
        RenderContext.RightClickActor += RightClickActor;
        RenderContext.RightClickViewport += RightClickViewport;
        eventsAttached = true;
    }

    private void DetachEvents()
    {
        if (!eventsAttached)
        {
            return;
        }

        RenderContext.RenderScene -= RenderScene;
        RenderContext.SelectHitProxy -= SelectHitProxy;
        RenderContext.RightClickHitProxy -= RightClickHitProxy;
        RenderContext.SelectActor -= IgnoreActorSelection;
        RenderContext.RightClickActor -= RightClickActor;
        RenderContext.RightClickViewport -= RightClickViewport;
        eventsAttached = false;
    }

    private void SelectHitProxy(IHitProxy hitProxy)
    {
        if (hitProxy is CurveEditor3DKeyframe keyframe)
        {
            SelectedKeyframe = keyframe;
        }
        else if (hitProxy is CurveEditor3DFovKeyframe fovKeyframe)
        {
            SelectedFovKeyframe = fovKeyframe;
        }
    }

    private void RightClickHitProxy(IHitProxy hitProxy)
    {
        if (hitProxy is CurveEditor3DKeyframe keyframe)
        {
            SelectedKeyframe = keyframe;
            ShowKeyframeContextMenu(SceneViewer);
        }
        else if (hitProxy is CurveEditor3DFovKeyframe fovKeyframe)
        {
            SelectedFovKeyframe = fovKeyframe;
        }
        else
        {
            ShowViewportContextMenu(SceneViewer);
        }
    }

    private void IgnoreActorSelection(ActorProxy actor)
    {
        RenderContext.TransformWidget.Attach = CameraFramingMode
            ? null
            : previewActorWidgetActive ? previewActorWidgetTarget : SelectedKeyframe;
    }

    private void RightClickActor(ActorProxy actor)
    {
        ShowViewportContextMenu(SceneViewer);
    }

    private void RightClickViewport()
    {
        ShowViewportContextMenu(SceneViewer);
    }

    private void TranslateMode_Click(object sender, RoutedEventArgs e)
    {
        RenderContext.TransformWidget.Mode = EWidgetMode.Translate;
        RenderContext.TransformWidget.VisibleAxes = EWidgetAxis.XYZ;
        RenderContext.TransformWidget.CurrentAxis = EWidgetAxis.None;
        SceneViewer.MarkRenderDirty();
        SceneViewer.Focus();
    }

    private void RotateMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string axisName }
            || !Enum.TryParse(axisName, out EWidgetAxis axis))
        {
            return;
        }

        RenderContext.TransformWidget.Mode = EWidgetMode.Rotate;
        RenderContext.TransformWidget.VisibleAxes = axis;
        RenderContext.TransformWidget.CurrentAxis = EWidgetAxis.None;
        SceneViewer.MarkRenderDirty();
        SceneViewer.Focus();
    }

    private void LocationScrubAxis_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string axes })
        {
            locationScrubAxes = axes;
        }
    }

    private void LocationScrubThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (SelectedKeyframe is null)
        {
            e.Handled = true;
            return;
        }

        StopPlayback(false);
        locationScrubDragAccumulator = 0;
        locationScrubPreviousHorizontalChange = 0;
    }

    private void LocationScrubThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (SelectedKeyframe is null || !double.IsFinite(e.HorizontalChange))
        {
            return;
        }

        double horizontalChange = e.HorizontalChange - locationScrubPreviousHorizontalChange;
        locationScrubPreviousHorizontalChange = e.HorizontalChange;
        locationScrubDragAccumulator += horizontalChange;
        double dragStep = SystemParameters.MinimumHorizontalDragDistance;
        int stepCount = (int)(locationScrubDragAccumulator / dragStep);
        if (stepCount == 0)
        {
            return;
        }

        locationScrubDragAccumulator -= stepCount * dragStep;
        float increment = LocationIncrementUpDown.Value ?? 1f;
        float delta = stepCount * increment;
        var locationDelta = new Vector3(
            locationScrubAxes.Contains('X') ? delta : 0,
            locationScrubAxes.Contains('Y') ? delta : 0,
            locationScrubAxes.Contains('Z') ? delta : 0);
        if (LocationScrubAllKeysCheckBox.IsChecked == true)
        {
            ActiveModel.TranslateAllKeyframesInDisplaySpace(locationDelta);
        }
        else
        {
            SelectedKeyframe.DisplayLocation += locationDelta;
        }
    }

    private void LocationScrubThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        SceneViewer.MarkRenderDirty();
    }

    private void RotationDialAxis_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string axis })
        {
            rotationDialAxis = axis;
            UpdateRotationDialIndicator();
        }
    }

    private void RotationDial_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SelectedKeyframe is null)
        {
            return;
        }

        StopPlayback(false);
        rotationDialPreviousAngle = GetRotationDialPointerAngle(e.GetPosition(RotationDial));
        rotationDialAngleAccumulator = 0;
        rotationDialDragging = RotationDial.CaptureMouse();
        e.Handled = true;
    }

    private void RotationDial_MouseMove(object sender, MouseEventArgs e)
    {
        if (!rotationDialDragging || SelectedKeyframe is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        double pointerAngle = GetRotationDialPointerAngle(e.GetPosition(RotationDial));
        double angleDelta = NormalizeAngle(pointerAngle - rotationDialPreviousAngle);
        rotationDialPreviousAngle = pointerAngle;
        rotationDialAngleAccumulator += angleDelta;

        float increment = RotationIncrementUpDown.Value ?? 1f;
        int stepCount = (int)(rotationDialAngleAccumulator / increment);
        if (stepCount == 0)
        {
            return;
        }

        rotationDialAngleAccumulator -= stepCount * increment;
        float rotationDelta = stepCount * increment;
        var rotationDeltaVector = new Vector3(
            rotationDialAxis is nameof(CurveEditor3DKeyframe.Roll) or "All" ? rotationDelta : 0,
            rotationDialAxis is nameof(CurveEditor3DKeyframe.Pitch) or "All" ? rotationDelta : 0,
            rotationDialAxis is nameof(CurveEditor3DKeyframe.Yaw) or "All" ? rotationDelta : 0);
        if (RotationDialAllKeysCheckBox.IsChecked == true)
        {
            ActiveModel.RotateAllKeyframesInDisplaySpace(rotationDeltaVector);
        }
        else
        {
            SelectedKeyframe.SetDisplayRotation(SelectedKeyframe.DisplayRotation + rotationDeltaVector, commit: true);
        }

        UpdateRotationDialIndicator();
        e.Handled = true;
    }

    private void RotationDial_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!rotationDialDragging)
        {
            return;
        }

        rotationDialDragging = false;
        RotationDial.ReleaseMouseCapture();
        SceneViewer.MarkRenderDirty();
        e.Handled = true;
    }

    private void RotationDial_LostMouseCapture(object sender, MouseEventArgs e)
    {
        rotationDialDragging = false;
    }

    private void UpdateRotationDialIndicator()
    {
        if (RotationDialIndicator?.RenderTransform is not System.Windows.Media.RotateTransform indicatorTransform)
        {
            return;
        }

        indicatorTransform.Angle = rotationDialAxis switch
        {
            nameof(CurveEditor3DKeyframe.Pitch) => SelectedKeyframe?.DisplayPitch ?? 0,
            nameof(CurveEditor3DKeyframe.Roll) => SelectedKeyframe?.DisplayRoll ?? 0,
            nameof(CurveEditor3DKeyframe.Yaw) => SelectedKeyframe?.DisplayYaw ?? 0,
            "All" => SelectedKeyframe?.DisplayPitch ?? 0,
            _ => 0
        };
    }

    private static double GetRotationDialPointerAngle(Point pointerPosition)
    {
        double centerX = 60;
        double centerY = 60;
        return Math.Atan2(pointerPosition.Y - centerY, pointerPosition.X - centerX) * 180d / Math.PI + 90d;
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > 180d) angle -= 360d;
        while (angle < -180d) angle += 360d;
        return angle;
    }

    private void CameraPositionAdjustButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        string[] parts = tag.Split(',');
        if (parts.Length != 2 || !float.TryParse(parts[1], out float direction)) return;

        Vector3 position = RenderContext.Camera.Position;
        if (float.TryParse(CameraPositionX, out float x)) position.X = x;
        if (float.TryParse(CameraPositionY, out float y)) position.Y = y;
        if (float.TryParse(CameraPositionZ, out float z)) position.Z = z;

        float delta = CameraPositionStep * direction;
        switch (parts[0])
        {
            case "X":
                position.X += delta;
                break;
            case "Y":
                position.Y += delta;
                break;
            case "Z":
                position.Z += delta;
                break;
            default:
                return;
        }

        CameraPositionX = position.X.ToString("0.##");
        CameraPositionY = position.Y.ToString("0.##");
        CameraPositionZ = position.Z.ToString("0.##");
        MoveCameraToEnteredPosition();
    }

    private void CameraRotationAdjustButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        string[] parts = tag.Split(',');
        if (parts.Length != 2 || !float.TryParse(parts[1], out float direction)) return;

        float x = MathUtil.RadiansToDegrees(RenderContext.Camera.Roll);
        float y = MathUtil.RadiansToDegrees(RenderContext.Camera.Pitch);
        float z = MathUtil.RadiansToDegrees(RenderContext.Camera.Yaw);
        if (float.TryParse(CameraRotationX, out float enteredX)) x = enteredX;
        if (float.TryParse(CameraRotationY, out float enteredY)) y = enteredY;
        if (float.TryParse(CameraRotationZ, out float enteredZ)) z = enteredZ;

        float delta = CameraRotationStep * direction;
        switch (parts[0])
        {
            case "X":
                x += delta;
                break;
            case "Y":
                y += delta;
                break;
            case "Z":
                z += delta;
                break;
            default:
                return;
        }

        CameraRotationX = x.ToString("0.##");
        CameraRotationY = y.ToString("0.##");
        CameraRotationZ = z.ToString("0.##");
        MoveCameraToEnteredRotation();
    }

    private bool AreCameraPositionBoxesFocused() => cameraPositionEditorsFocused > 0;

    private bool AreCameraRotationBoxesFocused() => cameraRotationEditorsFocused > 0;

    private void UpdateCameraPositionText()
    {
        if (AreCameraPositionBoxesFocused()) return;

        updatingCameraPositionText = true;
        try
        {
            Vector3 position = RenderContext.Camera.Position;
            CameraPositionX = position.X.ToString("0.##");
            CameraPositionY = position.Y.ToString("0.##");
            CameraPositionZ = position.Z.ToString("0.##");
        }
        finally
        {
            updatingCameraPositionText = false;
        }
    }

    private void UpdateCameraRotationText()
    {
        if (AreCameraRotationBoxesFocused()) return;

        updatingCameraRotationText = true;
        try
        {
            CameraRotationX = MathUtil.RadiansToDegrees(RenderContext.Camera.Roll).ToString("0.##");
            CameraRotationY = MathUtil.RadiansToDegrees(RenderContext.Camera.Pitch).ToString("0.##");
            CameraRotationZ = MathUtil.RadiansToDegrees(RenderContext.Camera.Yaw).ToString("0.##");
        }
        finally
        {
            updatingCameraRotationText = false;
        }
    }

    private void CameraPositionBoxes_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            MoveCameraToEnteredPosition();
            e.Handled = true;
        }
    }

    private void CameraPositionBoxes_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        cameraPositionEditorsFocused++;
    }

    private void CameraPositionBoxes_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        cameraPositionEditorsFocused = Math.Max(0, cameraPositionEditorsFocused - 1);
        if (updatingCameraPositionText) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!updatingCameraPositionText && !AreCameraPositionBoxesFocused())
            {
                MoveCameraToEnteredPosition();
            }
        }));
    }

    private void MoveCameraToEnteredPosition()
    {
        if (!float.TryParse(CameraPositionX, out float x)
            || !float.TryParse(CameraPositionY, out float y)
            || !float.TryParse(CameraPositionZ, out float z))
        {
            UpdateCameraPositionText();
            return;
        }

        RenderContext.Camera.Position = new Vector3(x, y, z);
        UpdateCameraPositionText();
        UpdateCameraRotationText();
        SceneViewer?.MarkRenderDirty();
        SceneViewer?.Focus();
    }

    private void CameraRotationBoxes_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            MoveCameraToEnteredRotation();
            e.Handled = true;
        }
    }

    private void CameraRotationBoxes_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        cameraRotationEditorsFocused++;
    }

    private void CameraRotationBoxes_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        cameraRotationEditorsFocused = Math.Max(0, cameraRotationEditorsFocused - 1);
        if (updatingCameraRotationText) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!updatingCameraRotationText && !AreCameraRotationBoxesFocused())
            {
                MoveCameraToEnteredRotation();
            }
        }));
    }

    private void MoveCameraToEnteredRotation()
    {
        if (!float.TryParse(CameraRotationX, out float x)
            || !float.TryParse(CameraRotationY, out float y)
            || !float.TryParse(CameraRotationZ, out float z))
        {
            UpdateCameraRotationText();
            return;
        }

        RenderContext.Camera.Roll = MathUtil.DegreesToRadians(x);
        RenderContext.Camera.Pitch = MathUtil.Clamp(MathUtil.DegreesToRadians(y), -MathF.PI / 2 + 0.01f, MathF.PI / 2 - 0.01f);
        RenderContext.Camera.Yaw = MathUtil.DegreesToRadians(z);
        UpdateCameraRotationText();
        SceneViewer?.MarkRenderDirty();
        SceneViewer?.Focus();
    }

    private void CoordinateEditor_GotFocus(object sender, RoutedEventArgs e)
    {
        if (Keyboard.PrimaryDevice.IsKeyDown(Key.Tab) && sender is TextBox textBox)
        {
            textBox.SelectAll();
        }
    }

    private void KeyframeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KeyframeList.SelectedItem is CurveEditor3DKeyframe keyframe && keyframe != SelectedKeyframe)
        {
            SelectedKeyframe = keyframe;
        }
    }

    private void KeyframeList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(KeyframeList, e.OriginalSource as DependencyObject) is not ListBoxItem
            { DataContext: CurveEditor3DKeyframe keyframe })
        {
            return;
        }

        StopPlayback();
        SelectedKeyframe = keyframe;
        SnapCameraToKey(keyframe);
        e.Handled = true;
    }

    private void FovKeyframeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FovKeyframeList.SelectedItem is CurveEditor3DFovKeyframe keyframe
            && keyframe != SelectedFovKeyframe)
        {
            SelectedFovKeyframe = keyframe;
        }
    }

    private void FovKeyframeEditor_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(FovKeyframeList, e.OriginalSource as DependencyObject) is ListBoxItem
            { DataContext: CurveEditor3DFovKeyframe keyframe })
        {
            SelectedFovKeyframe = keyframe;
        }
    }

    private void PreviewFovKeyframe_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        PreviewSelectedFovKeyframe();
    }

    private void PreviewSelectedFovKeyframe()
    {
        if (SelectedFovKeyframe is not { } keyframe || ActiveTrackMoveOption is not { } trackMove)
        {
            return;
        }

        suppressTrackVisualizationForCameraPreview = true;
        ApplyViewportCameraAtTime(trackMove, keyframe.Time);
        RenderContext.Camera.FocusDepth = 0f;
        UpdateCameraPositionText();
        UpdateCameraRotationText();
        SceneStatus = $"Previewing linked Move + FOV tracks at InVal {keyframe.Time:0.###} ({keyframe.Value:0.##}°).";
        SceneViewer?.MarkRenderDirty();
        SceneViewer?.Focus();
    }

    private void PreviewSelectedCameraTrackValue()
    {
        float? time = SelectedFovKeyframe?.Time ?? SelectedKeyframe?.Time;
        if (time is null || ActiveTrackMoveOption is not { } trackMove)
        {
            return;
        }

        ApplyViewportCameraAtTime(trackMove, time.Value);
        RenderContext.Camera.FocusDepth = 0f;
        UpdateCameraPositionText();
        UpdateCameraRotationText();
    }

    private void ApplyFovKeyframeInVal_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFovKeyframe is not { } keyframe || ActiveFovModel is not { } fovModel)
        {
            return;
        }

        if (!float.TryParse(SelectedFovKeyframeInVal, NumberStyles.Float, CultureInfo.CurrentCulture,
                out float inVal) || !float.IsFinite(inVal))
        {
            MessageBox.Show("Enter a valid finite InVal.");
            return;
        }

        if (fovModel.HasKeyframeAtTime(inVal, keyframe))
        {
            MessageBox.Show("An FOV key already exists at this InVal.");
            return;
        }

        StopPlayback();
        keyframe.Time = inVal;
        SelectedFovKeyframeInVal = keyframe.Time.ToString(CultureInfo.CurrentCulture);
        SceneStatus = $"Changed FOV key InVal to {keyframe.Time:0.###}.";
    }

    private void FovKeyframeInVal_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        ApplyFovKeyframeInVal_Click(sender, e);
        e.Handled = true;
    }

    private void FovScrubProperty_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string propertyName })
        {
            fovScrubProperty = propertyName;
        }
    }

    private void FovScrubThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (SelectedFovKeyframe is null)
        {
            e.Handled = true;
            return;
        }

        StopPlayback(false);
        fovScrubDragAccumulator = 0;
        fovScrubPreviousHorizontalChange = 0;
    }

    private void FovScrubThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (SelectedFovKeyframe is not { } keyframe || !double.IsFinite(e.HorizontalChange))
        {
            return;
        }

        double horizontalChange = e.HorizontalChange - fovScrubPreviousHorizontalChange;
        fovScrubPreviousHorizontalChange = e.HorizontalChange;
        fovScrubDragAccumulator += horizontalChange;
        double dragStep = SystemParameters.MinimumHorizontalDragDistance;
        int stepCount = (int)(fovScrubDragAccumulator / dragStep);
        if (stepCount == 0)
        {
            return;
        }

        fovScrubDragAccumulator -= stepCount * dragStep;
        float delta = stepCount * (FovIncrementUpDown.Value ?? 0.1f);
        switch (fovScrubProperty)
        {
            case nameof(CurveEditor3DFovKeyframe.Time):
                float proposedTime = keyframe.Time + delta;
                if (ActiveFovModel?.HasKeyframeAtTime(proposedTime, keyframe) == true)
                {
                    return;
                }
                keyframe.Time = proposedTime;
                SelectedFovKeyframeInVal = keyframe.Time.ToString(CultureInfo.CurrentCulture);
                break;
            case nameof(CurveEditor3DFovKeyframe.ArriveTangent):
                keyframe.ArriveTangent += delta;
                break;
            case nameof(CurveEditor3DFovKeyframe.LeaveTangent):
                keyframe.LeaveTangent += delta;
                break;
            default:
                keyframe.Value += delta;
                break;
        }
    }

    private void FovScrubThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        SceneViewer?.MarkRenderDirty();
    }

    private void AddFovKeyframe_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        if (ActiveFovModel is not { } fovModel)
        {
            return;
        }

        float defaultTime = SelectedFovKeyframe?.Time + 1f
                            ?? SelectedKeyframe?.Time
                            ?? ActiveModel.Keyframes.FirstOrDefault()?.Time
                            ?? 0f;
        string response = PromptDialog.Prompt(
            this,
            "Enter the InVal for the new FOV key.",
            "Add FOV Key",
            defaultTime.ToString(CultureInfo.CurrentCulture),
            selectText: true,
            validator: text =>
            {
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out float value)
                    || !float.IsFinite(value))
                {
                    return (false, "Enter a valid finite number.");
                }
                return fovModel.HasKeyframeAtTime(value)
                    ? (false, "An FOV key already exists at this InVal.")
                    : (true, null);
            });
        if (!float.TryParse(response, NumberStyles.Float, CultureInfo.CurrentCulture, out float inVal))
        {
            return;
        }

        float value = fovModel.Track.Eval(inVal, 60f);
        CurveEditor3DFovKeyframe keyframe = fovModel.AddKeyframe(inVal, value);
        if (keyframe is null)
        {
            return;
        }

        RenderContext.AddHitProxy(keyframe);
        SelectedFovKeyframe = keyframe;
        SceneStatus = $"Added FOV key at InVal {keyframe.Time:0.###}.";
        RefreshFovKeyframePanel();
        SceneViewer?.MarkRenderDirty();
    }

    private void DeleteFovKeyframe_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        if (ActiveFovModel is not { } fovModel || SelectedFovKeyframe is not { } keyframe)
        {
            return;
        }

        RenderContext.RemoveHitProxy(keyframe);
        SelectedFovKeyframe = fovModel.DeleteKeyframe(keyframe);
        SceneStatus = $"{fovModel.Keyframes.Count} FOV key(s) remain.";
        RefreshFovKeyframePanel();
        SceneViewer?.MarkRenderDirty();
    }

    private void ApplyKeyframeInVal_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedKeyframe is not { } keyframe)
        {
            return;
        }

        if (!float.TryParse(SelectedKeyframeInVal, NumberStyles.Float, CultureInfo.CurrentCulture, out float inVal) || !float.IsFinite(inVal))
        {
            MessageBox.Show("Enter a valid finite InVal.");
            return;
        }

        if (ActiveModel.HasKeyframeAtTime(inVal, keyframe))
        {
            MessageBox.Show("A keyframe already exists at this InVal.");
            return;
        }

        StopPlayback();
        keyframe.Time = inVal;
        SelectedKeyframeInVal = keyframe.Time.ToString(CultureInfo.CurrentCulture);
        SceneStatus = $"Changed keyframe InVal to {keyframe.Time:0.###}.";
    }

    private void KeyframeInVal_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        ApplyKeyframeInVal_Click(sender, e);
        e.Handled = true;
    }

    private void AddKeyframe_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        if (SelectedKeyframe is not { } keyframe)
        {
            return;
        }

        float? inVal = PromptForKeyframeInVal(keyframe.Time + 5f);
        if (inVal is null)
        {
            return;
        }

        CurveEditor3DKeyframe newKeyframe = ActiveModel.AddKeyframe(keyframe, inVal.Value);
        if (newKeyframe is null)
        {
            return;
        }

        newKeyframe.SetCoordinateBasis(ActiveTrackCoordinateBasis);
        RenderContext.AddHitProxy(newKeyframe);
        SelectedKeyframe = newKeyframe;
        SceneStatus = $"Added keyframe at InVal {newKeyframe.Time:0.###}; {ActiveModel.Keyframes.Count} trajectory keyframe(s).";
        RefreshKeyframePanel();
        SceneViewer.MarkRenderDirty();
    }

    private float? PromptForKeyframeInVal(float defaultValue)
    {
        string response = PromptDialog.Prompt(
            this,
            "Enter the InVal for the new keyframe.",
            "Add Keyframe",
            defaultValue.ToString(CultureInfo.CurrentCulture),
            selectText: true,
            validator: text =>
            {
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out float value) || !float.IsFinite(value))
                {
                    return (false, "Enter a valid finite number.");
                }

                if (ActiveModel.HasKeyframeAtTime(value))
                {
                    return (false, "A keyframe already exists at this InVal.");
                }

                return (true, null);
            });

        return float.TryParse(response, NumberStyles.Float, CultureInfo.CurrentCulture, out float inVal)
            ? inVal
            : null;
    }

    private void SnapSelectedKeyframeToViewport_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        if (SelectedKeyframe is not { } keyframe)
        {
            return;
        }

        keyframe.Location = pendingViewportSelectedKeyframeLocation;
        SceneStatus = $"Snapped keyframe at InVal {keyframe.Time:0.###} to the viewport cursor.";
    }

    private void SnapSelectedKeyframeToCurrentViewportCursor_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedKeyframe is not { } keyframe)
        {
            return;
        }
        if (!SceneViewer.IsMouseOver)
        {
            SceneStatus = "Move the cursor over the viewport before snapping the selected keyframe.";
            return;
        }

        Point viewportPoint = Mouse.GetPosition(SceneViewer);
        pendingViewportSelectedKeyframeLocation = GetViewportKeyframeLocation(viewportPoint, keyframe.Location);
        SnapSelectedKeyframeToViewport_Click(sender, e);
    }

    private void AddKeyframeAfterLast_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        float? inVal = PromptForKeyframeInVal(ActiveModel.Keyframes[^1].Time + 1f);
        if (inVal is null)
        {
            return;
        }

        CurveEditor3DKeyframe newKeyframe = ActiveModel.AddKeyframeAfterLast(pendingViewportKeyframeLocation, inVal.Value);
        if (newKeyframe is null)
        {
            return;
        }

        newKeyframe.SetCoordinateBasis(ActiveTrackCoordinateBasis);
        RenderContext.AddHitProxy(newKeyframe);
        SelectedKeyframe = newKeyframe;
        SceneStatus = $"Added keyframe at InVal {newKeyframe.Time:0.###}; {ActiveModel.Keyframes.Count} trajectory keyframe(s).";
        RefreshKeyframePanel();
        SceneViewer.MarkRenderDirty();
    }

    private void DeleteKeyframe_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        if (SelectedKeyframe is not { } keyframe)
        {
            return;
        }

        RenderContext.RemoveHitProxy(keyframe);
        SelectedKeyframe = ActiveModel.DeleteKeyframe(keyframe);
        SceneStatus = $"{ActiveModel.Keyframes.Count} trajectory keyframe(s); {levelPaths.Count} level backdrop file(s).";
        RefreshKeyframePanel();
        SceneViewer.MarkRenderDirty();
    }

    private void ShiftInterpTrack_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        if (ActiveTrackMoveExport is not { } activeExport)
        {
            return;
        }

        var dialog = new ShiftInterpTrackDialog
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        float selectedTime = SelectedKeyframe?.Time ?? 0f;
        PackageEditorExperimentsM.ShiftInterpTrackMove(activeExport, dialog.Parameters);
        ReloadCurrentExport(selectedTime + dialog.Parameters.TimeOffset);
        SceneStatus = $"Shifted {ActiveModel.Keyframes.Count} trajectory keyframe(s); {levelPaths.Count} level backdrop file(s).";
    }

    private void ApplyPosTrackInterpModeToAll_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        if (SelectedKeyframe is not { } keyframe || ActiveModel.Keyframes.Count == 0)
        {
            return;
        }

        ActiveModel.SetAllPosTrackInterpModes(keyframe.PosTrackInterpMode);
        RefreshKeyframePanel();
        trajectorySamplesDirty = true;
        SceneStatus = $"Set PosTrack InterpMode to {keyframe.PosTrackInterpMode} for {ActiveModel.Keyframes.Count} keyframe(s).";
        SceneViewer.MarkRenderDirty();
    }

    private void ApplyEulerTrackInterpModeToAll_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        if (SelectedKeyframe is not { } keyframe || ActiveModel.Keyframes.Count == 0)
        {
            return;
        }

        ActiveModel.SetAllEulerTrackInterpModes(keyframe.EulerTrackInterpMode);
        RefreshKeyframePanel();
        trajectorySamplesDirty = true;
        SceneStatus = $"Set EulerTrack InterpMode to {keyframe.EulerTrackInterpMode} for {ActiveModel.Keyframes.Count} keyframe(s).";
        SceneViewer.MarkRenderDirty();
    }

    private void ReloadCurrentExport(float preferredSelectionTime)
    {
        UnregisterKeyframes();
        ActiveModel.Load(ActiveTrackMoveExport);
        trajectorySamplesDirty = true;
        KeyframeList.ItemsSource = ActiveModel.Keyframes;
        RegisterKeyframes();
        SelectedKeyframe = ActiveModel.Keyframes.Count == 0
            ? null
            : ActiveModel.Keyframes.MinBy(keyframe => MathF.Abs(keyframe.Time - preferredSelectionTime));
        RefreshKeyframePanel();
        SceneViewer.MarkRenderDirty();
    }

    private void KeyframeList_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(KeyframeList, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: CurveEditor3DKeyframe keyframe })
        {
            SelectedKeyframe = keyframe;
        }
    }

    private void ShowKeyframeContextMenu(FrameworkElement placementTarget)
    {
        ContextMenu menu = CreateKeyframeContextMenu();
        menu.PlacementTarget = placementTarget;
        menu.IsOpen = true;
    }

    private void ShowViewportContextMenu(FrameworkElement placementTarget)
    {
        Point viewportPoint = Mouse.GetPosition(SceneViewer);
        pendingViewportKeyframeLocation = GetViewportKeyframeLocation(viewportPoint);
        if (SelectedKeyframe is { } selectedKeyframe)
        {
            pendingViewportSelectedKeyframeLocation = GetViewportKeyframeLocation(viewportPoint, selectedKeyframe.Location);
        }

        var menu = new ContextMenu { PlacementTarget = placementTarget };
        var snapItem = new MenuItem
        {
            Header = "Snap Selected Keyframe Here",
            IsEnabled = SelectedKeyframe is not null
        };
        snapItem.Click += SnapSelectedKeyframeToViewport_Click;
        menu.Items.Add(snapItem);

        var snapActorItem = new MenuItem
        {
            Header = "Snap Selected Actor Here",
            IsEnabled = selectedPreviewActor is not null
        };
        Vector3 actorLocation = selectedPreviewActor is null
            ? default
            : GetViewportKeyframeLocation(viewportPoint, selectedPreviewActor.Origin.Location,
                returnTrackSpace: false);
        snapActorItem.Click += (_, _) =>
        {
            if (selectedPreviewActor is not null)
            {
                SetSelectedPreviewActorOrigin(new CameraOrigin(actorLocation, selectedPreviewActor.Origin.Rotation));
            }
        };
        menu.Items.Add(snapActorItem);
        menu.Items.Add(new Separator());

        var addItem = new MenuItem
        {
            Header = "Add Keyframe",
            IsEnabled = ActiveModel.Keyframes.Count > 0
        };
        addItem.Click += AddKeyframeAfterLast_Click;
        menu.Items.Add(addItem);
        menu.IsOpen = true;
    }

    private Vector3 GetViewportKeyframeLocation(Point viewportPoint, Vector3? depthReference = null,
        bool returnTrackSpace = true)
    {
        CameraOrigin? coordinateBasis = returnTrackSpace ? ActiveTrackCoordinateBasis : null;
        Vector3 ToTrackSpace(Vector3 worldLocation) => coordinateBasis is { } basis
            ? InterpTrackMoveTransform.ToLocal(basis, new CameraOrigin(worldLocation, Vector3.Zero)).Location
            : worldLocation;
        if (depthReference is null && ActiveModel.Keyframes.Count == 0)
        {
            return ToTrackSpace(RenderContext.Camera.Position + RenderContext.Camera.CameraForward * 100f);
        }

        Vector3 referenceLocation = depthReference ?? ActiveModel.Keyframes[^1].Location;
        if (coordinateBasis is { } activeBasis)
        {
            referenceLocation = InterpTrackMoveTransform.ToWorld(activeBasis,
                new CameraOrigin(referenceLocation, Vector3.Zero)).Location;
        }
        Vector2 normalizedViewportPoint = RenderContext.PixelToViewportNormalized(
            (float)viewportPoint.X, (float)viewportPoint.Y);
        float normalizedX = normalizedViewportPoint.X;
        float normalizedY = normalizedViewportPoint.Y;
        Vector3 forward = RenderContext.Camera.CameraForward;
        Vector3 right = RenderContext.Camera.CameraRight;
        Vector3 up = RenderContext.Camera.CameraUp;
        Vector3 cameraPosition = RenderContext.Camera.Position;

        if (RenderContext.Camera.IsOrthographic)
        {
            return ToTrackSpace(cameraPosition
                                + (right * (normalizedX * RenderContext.Camera.OrthoWidth * 0.5f))
                                + (up * (normalizedY * RenderContext.Camera.OrthoWidth / MathF.Max(RenderContext.Camera.aspect, float.Epsilon) * 0.5f))
                                + (forward * Vector3.Dot(referenceLocation - cameraPosition, forward)));
        }

        float halfHeightAtUnitDepth = MathF.Tan(RenderContext.Camera.FOV * 0.5f);
        Vector3 rayDirection = Vector3.Normalize(forward + right * normalizedX * halfHeightAtUnitDepth * RenderContext.Camera.aspect + up * normalizedY * halfHeightAtUnitDepth);
        float denominator = Vector3.Dot(rayDirection, forward);
        if (MathF.Abs(denominator) < 0.0001f)
        {
            return ToTrackSpace(referenceLocation);
        }

        float distance = Vector3.Dot(referenceLocation - cameraPosition, forward) / denominator;
        if (distance <= 0f || float.IsNaN(distance) || float.IsInfinity(distance))
        {
            return ToTrackSpace(referenceLocation);
        }

        return ToTrackSpace(cameraPosition + rayDirection * distance);
    }

    private void RefreshKeyframePanel()
    {
        KeyframeList?.Items.Refresh();
        OnPropertyChanged(nameof(SelectedKeyframe));
        KeyframeList.SelectedItem = SelectedKeyframe;
        if (SelectedKeyframe is not null)
        {
            KeyframeList.ScrollIntoView(SelectedKeyframe);
        }
    }

    private void RefreshFovKeyframePanel()
    {
        FovKeyframeList?.Items.Refresh();
        OnPropertyChanged(nameof(SelectedFovKeyframe));
        FovKeyframeList.SelectedItem = SelectedFovKeyframe;
        if (SelectedFovKeyframe is not null)
        {
            FovKeyframeList.ScrollIntoView(SelectedFovKeyframe);
        }
    }

    private void SnapCameraToKey_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        if (SelectedKeyframe is not { } keyframe)
        {
            return;
        }

        SnapCameraToKey(keyframe);
    }

    private void SnapCameraToKey(CurveEditor3DKeyframe keyframe, bool focusViewport = true)
    {
        if (CameraFramingMode)
        {
            PreviewSelectedCameraTrackValue();
            SceneViewer.MarkRenderDirty();
            if (focusViewport)
            {
                SceneViewer.Focus();
            }
            return;
        }

        const float degreesToRadians = 0.017453292519943295f;
        const float cameraDistance = 150f;
        CameraOrigin displayOrigin = keyframe.DisplayOrigin;
        RenderContext.Camera.Roll = displayOrigin.Rotation.X * degreesToRadians;
        RenderContext.Camera.Pitch = displayOrigin.Rotation.Y * degreesToRadians;
        RenderContext.Camera.Yaw = displayOrigin.Rotation.Z * degreesToRadians;
        RenderContext.Camera.Position = displayOrigin.Location - RenderContext.Camera.CameraForward * cameraDistance;
        RenderContext.Camera.FocusDepth = 0f;
        UpdateCameraPositionText();
        UpdateCameraRotationText();
        SceneViewer.MarkRenderDirty();
        if (focusViewport)
        {
            SceneViewer.Focus();
        }
    }

    private void PlayMoveButton_Loaded(object sender, RoutedEventArgs e)
    {
        playMoveButton = (Button)sender;
        UpdatePlaybackButton();
    }

    private void PlayActorButton_Loaded(object sender, RoutedEventArgs e)
    {
        playActorButton = (Button)sender;
        UpdatePlaybackButton();
    }

    private void PlayMove_Click(object sender, RoutedEventArgs e)
    {
        suppressTrackVisualizationForCameraPreview = false;
        if (isPlayingMove)
        {
            bool wasPlayingCamera = !isPlayingActor;
            StopPlayback();
            if (wasPlayingCamera)
            {
                return;
            }
        }

        if (ActiveModel.Keyframes.Count == 0)
        {
            return;
        }

        SetPlaybackRangeForCurrentMode();
        if (playbackEndTime <= playbackStartTime)
        {
            ApplyCameraAtTime(playbackStartTime);
            return;
        }

        if (playMoveButton is not null)
        {
            playMoveButton.Content = "Stop";
        }
        RenderContext.TransformWidget.Attach = null;
        playbackElapsed = 0f;
        playbackCurrentTime = playbackStartTime;
        isPlayingActor = false;
        playbackActors.Clear();
        isPlayingMove = true;
        RenderContext.ForceContinuousRendering = true;
        ApplyCameraAtTime(playbackStartTime);
        SceneViewer.Focus();
    }

    private void PlayActor_Click(object sender, RoutedEventArgs e)
    {
        suppressTrackVisualizationForCameraPreview = false;
        if (isPlayingMove)
        {
            bool wasPlayingActor = isPlayingActor;
            StopPlayback();
            if (wasPlayingActor)
            {
                return;
            }
        }

        playbackActors.Clear();
        foreach (PreviewActorConfiguration actor in previewActors)
        {
            previewActorTrackAssignments.TryGetValue(actor, out TrackMovePlaybackOption trackMove);
            if (dialogueNodePreview is null && trackMove?.Model?.Keyframes is not { Count: > 0 })
            {
                continue;
            }

            CameraOrigin originalOrigin = actor.Origin;
            playbackActors.Add(new PreviewActorPlaybackState
            {
                Actor = actor,
                TrackMove = trackMove,
                OriginalOrigin = originalOrigin,
                MoveFrame = GetTrackMoveFrame(trackMove)
            });
        }

        if (playbackActors.Count == 0)
        {
            return;
        }

        foreach (PreviewActorPlaybackState state in playbackActors)
        {
            ApplyAssignedGestureToActor(state.Actor);
        }

        SetPlaybackRangeForCurrentMode(includeActorTracks: true);
        if (playbackEndTime <= playbackStartTime)
        {
            ApplyActorsAtTime(playbackStartTime);
            RestorePlaybackActorOrigins();
            playbackActors.Clear();
            return;
        }

        if (playActorButton is not null)
        {
            playActorButton.Content = "Stop Actors";
        }
        RenderContext.TransformWidget.Attach = null;
        playbackElapsed = 0f;
        playbackCurrentTime = playbackStartTime;
        isPlayingActor = true;
        isPlayingMove = true;
        RenderContext.ForceContinuousRendering = true;
        ApplyActorsAtTime(playbackStartTime);
        dialoguePreviewAudioStarted = false;
        faceOnlyVoAudioStarted = false;
        if (activeFaceOnlyVoEvent is not null)
        {
            EndActiveFaceOnlyVo();
        }
        if (faceOnlyVoEvents.Count > 0)
        {
            ApplyFaceOnlyVoAtTime(playbackStartTime);
        }
        UpdateDialoguePreviewAudio(playbackStartTime);
        SceneViewer.Focus();
    }

    private void UpdatePlayback(object sender, float deltaTime)
    {
        if (isPlayingDialogueTimeline)
        {
            float endTime = GetDialogueTimelineEndTime();
            float nextTime = MathF.Min(dialogueTimelineCurrentTime + deltaTime, endTime);
            ApplyDialogueTimelineAtTime(nextTime, reconstruct: false);
            if (nextTime >= endTime)
            {
                PauseDialogueTimeline();
            }
            return;
        }

        if (!isPlayingMove)
        {
            return;
        }

        playbackElapsed += deltaTime;
        float time = playbackStartTime + playbackElapsed;
        if (time >= playbackEndTime)
        {
            ApplyPlaybackAtTime(playbackEndTime);
            StopPlayback();
            return;
        }

        ApplyPlaybackAtTime(time);
    }

    private void DialogueTimelinePlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (isPlayingDialogueTimeline)
        {
            PauseDialogueTimeline();
            return;
        }

        float endTime = GetDialogueTimelineEndTime();
        if (endTime <= 0 || dialogueTimelineCurrentTime >= endTime && dialogueBranchOptions.Count > 0)
        {
            return;
        }
        if (dialogueTimelineCurrentTime >= endTime)
        {
            StartDialogueTimelinePlaybackAt(0, reconstruct: true);
            return;
        }

        StartDialogueTimelinePlayback();
    }

    private void StartDialogueTimelinePlayback(bool applyCurrentFrame = true)
    {
        if (isPlayingMove)
        {
            StopPlayback(false);
        }
        suppressTrackVisualizationForCameraPreview = false;
        isPlayingDialogueTimeline = true;
        if (FindName("DialogueTimelinePlayButton") is Button playButton)
        {
            playButton.Content = "Pause";
        }
        RenderContext.ForceContinuousRendering = true;
        if (applyCurrentFrame && activeDialogueTimelineSegment is not null)
        {
            ApplyPlaybackAtTime(Math.Clamp(dialogueTimelineCurrentTime - activeDialogueTimelineSegment.StartTime,
                0, activeDialogueTimelineSegment.Duration), playAudio: true);
        }
        SceneViewer?.MarkRenderDirty();
        SceneViewer.Focus();
    }

    private void StartDialogueTimelinePlaybackAt(float globalTime, bool reconstruct)
    {
        // Enter the playing state before restoring/evaluating the target node. This makes the
        // first evaluation start body animation, FaceFX and VO together instead of producing a
        // paused frame whose audio-start flags have to be repaired by a second Play click.
        StartDialogueTimelinePlayback(applyCurrentFrame: false);
        ApplyDialogueTimelineAtTime(globalTime, reconstruct);
    }

    private void DialogueTimelineRewind_Click(object sender, RoutedEventArgs e)
    {
        DialogueTimelineSegment node = activeDialogueTimelineSegment
                                       ?? dialogueTimelineActivePath.FirstOrDefault(segment =>
                                           dialogueTimelineCurrentTime >= segment.StartTime
                                           && dialogueTimelineCurrentTime <= segment.EndTime)
                                       ?? dialogueTimelineActivePath.FirstOrDefault();
        if (node is null)
        {
            return;
        }
        PauseDialogueTimeline();
        ForceCachedDialogueSegmentReactivation(node);
        StartDialogueTimelinePlaybackAt(node.StartTime, reconstruct: true);
    }

    public void HandleUpdate(List<PackageUpdate> updates)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => HandleUpdate(updates));
            return;
        }
        if (!isDialogueConversationPreview || dialoguePreviewWorkingPackage is null
                                            || suppressDialoguePackageEditTracking
                                            || !HasDialogueWorkingPackageChanges())
        {
            return;
        }

        bool markedRuntime = false;
        foreach (PackageUpdate update in updates)
        {
            if (!dialoguePreviewWorkingPackage.TryGetUExport(update.Index, out ExportEntry changedExport))
            {
                continue;
            }

            foreach (DialogueSegmentRuntime runtime in dialogueRuntimeCache.Values)
            {
                bool belongsToNode = GetDialogueNodeInterpDatas(runtime.Segment.Node).Any(interpData =>
                    changedExport == interpData || changedExport.IsDescendantOf(interpData));
                if (belongsToNode)
                {
                    runtime.HasPendingPackageChanges = true;
                    markedRuntime = true;
                }
            }
        }
        if (!markedRuntime && activeDialogueSegmentRuntime is not null)
        {
            activeDialogueSegmentRuntime.HasPendingPackageChanges = true;
        }
        QueueDialoguePackageEditorScopeRefresh();
        DialoguePackageEditorTab.Header = "Package Editor *";
        PauseDialogueTimeline();
        UpdateDialogueNodeCommitButton();
    }

    private bool HasDialogueWorkingPackageChanges()
    {
        if (dialoguePreviewWorkingPackage is null || dialoguePreviewSourcePackage is null)
        {
            return false;
        }

        return dialoguePreviewWorkingPackage.Names.Count != dialoguePreviewSourcePackage.Names.Count
               || !dialoguePreviewWorkingPackage.Names.SequenceEqual(dialoguePreviewSourcePackage.Names)
               || dialoguePreviewWorkingPackage.Imports.Count != dialogueWorkingCommittedImportCount
               || dialoguePreviewWorkingPackage.Exports.Count != dialogueWorkingCommittedExportCount
               || dialoguePreviewWorkingPackage.Imports.Any(entry => entry.EntryHasPendingChanges)
               || dialoguePreviewWorkingPackage.Exports.Any(entry => entry.EntryHasPendingChanges);
    }

    private bool CommitDialogueWorkingPackageChanges(out int changedEntryCount, out string error)
    {
        changedEntryCount = 0;
        error = null;
        if (dialoguePreviewWorkingPackage is null || dialoguePreviewSourcePackage is null
                                                        || !HasDialogueWorkingPackageChanges())
        {
            return true;
        }

        IMEPackage working = dialoguePreviewWorkingPackage;
        IMEPackage source = dialoguePreviewSourcePackage;
        if (source.Names.Count != dialogueWorkingCommittedNameCount
            || source.Imports.Count != dialogueWorkingCommittedImportCount
            || source.Exports.Count != dialogueWorkingCommittedExportCount)
        {
            error = "The source package structure changed outside the preview. Close and reopen the preview before committing its package edits.";
            return false;
        }
        if (working.Names.Count < dialogueWorkingCommittedNameCount
            || working.Imports.Count < dialogueWorkingCommittedImportCount
            || working.Exports.Count < dialogueWorkingCommittedExportCount)
        {
            error = "The preview package removed table entries in a way that cannot be merged into the open source package.";
            return false;
        }

        suppressDialoguePackageEditTracking = true;
        try
        {
            for (int index = 0; index < dialogueWorkingCommittedNameCount; index++)
            {
                if (!string.Equals(source.Names[index], working.Names[index], StringComparison.Ordinal))
                {
                    source.replaceName(index, working.Names[index]);
                }
            }
            for (int index = dialogueWorkingCommittedNameCount; index < working.Names.Count; index++)
            {
                int sourceIndex = source.FindNameOrAdd(working.Names[index]);
                if (sourceIndex != index)
                {
                    error = $"Unable to preserve name index {index} while merging the preview package.";
                    return false;
                }
            }

            for (int index = dialogueWorkingCommittedImportCount; index < working.Imports.Count; index++)
            {
                ImportEntry workingImport = working.Imports[index];
                var sourceImport = new ImportEntry(source) { Header = workingImport.Header };
                source.AddImport(sourceImport);
                if (sourceImport.UIndex != workingImport.UIndex)
                {
                    error = $"Unable to preserve import index {workingImport.UIndex} while merging the preview package.";
                    return false;
                }
                changedEntryCount++;
            }
            for (int index = dialogueWorkingCommittedExportCount; index < working.Exports.Count; index++)
            {
                ExportEntry workingExport = working.Exports[index];
                var sourceExport = new ExportEntry(source, workingExport.Header) { Data = workingExport.Data };
                source.AddExport(sourceExport);
                if (sourceExport.UIndex != workingExport.UIndex)
                {
                    error = $"Unable to preserve export index {workingExport.UIndex} while merging the preview package.";
                    return false;
                }
                changedEntryCount++;
            }

            for (int index = 0; index < dialogueWorkingCommittedImportCount; index++)
            {
                ImportEntry workingImport = working.Imports[index];
                if (!workingImport.EntryHasPendingChanges)
                {
                    continue;
                }
                source.Imports[index].Header = workingImport.Header;
                changedEntryCount++;
            }
            for (int index = 0; index < dialogueWorkingCommittedExportCount; index++)
            {
                ExportEntry workingExport = working.Exports[index];
                if (!workingExport.EntryHasPendingChanges)
                {
                    continue;
                }
                ExportEntry sourceExport = source.Exports[index];
                sourceExport.Header = workingExport.Header;
                sourceExport.Data = workingExport.Data;
                changedEntryCount++;
            }

            dialogueWorkingCommittedNameCount = working.Names.Count;
            dialogueWorkingCommittedImportCount = working.Imports.Count;
            dialogueWorkingCommittedExportCount = working.Exports.Count;
            foreach (ImportEntry import in working.Imports)
            {
                import.HeaderChanged = false;
                import.EntryHasPendingChanges = false;
            }
            foreach (ExportEntry export in working.Exports)
            {
                export.DataChanged = false;
                export.HeaderChanged = false;
                export.EntryHasPendingChanges = false;
            }
            foreach (DialogueSegmentRuntime runtime in dialogueRuntimeCache.Values)
            {
                runtime.HasPendingPackageChanges = false;
            }
            DialoguePackageEditorTab.Header = "Package Editor";
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
        finally
        {
            suppressDialoguePackageEditTracking = false;
        }
    }

    private void DialogueNodeCommit_Click(object sender, RoutedEventArgs e)
    {
        if (!isDialogueConversationPreview || activeDialogueSegmentRuntime is null)
        {
            return;
        }

        foreach (CurveEditor3DModel trackModel in activeDialogueSegmentRuntime.TrackMoves
                     .Select(option => option.Model).Where(model => model is not null).Distinct())
        {
            trackModel.CommitChanges();
        }
        foreach (CurveEditor3DFovModel fovModel in activeDialogueSegmentRuntime.TrackMoves
                     .Select(option => option.FovModel).Where(model => model is not null).Distinct())
        {
            fovModel.CommitChanges();
        }
        if (!CommitDialogueWorkingPackageChanges(out int changedEntryCount, out string error))
        {
            activeDialogueSegmentRuntime.HasPendingPackageChanges = true;
            MessageBox.Show(Window.GetWindow(this), $"Unable to commit the cached package edits: {error}",
                "Commit Dialogue Node", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateDialogueNodeCommitButton();
            return;
        }
        activeDialogueSegmentRuntime.HasPendingPreviewChanges = false;
        activeDialogueSegmentRuntime.HasPendingPackageChanges = false;
        UpdateDialogueNodeCommitButton();
        SceneStatus = changedEntryCount > 0
            ? $"Committed cached edits for {activeDialogueSegmentRuntime.Segment.NodeLabel}, including {changedEntryCount} package entr{(changedEntryCount == 1 ? "y" : "ies")}."
            : $"Committed cached edits for {activeDialogueSegmentRuntime.Segment.NodeLabel}.";
    }

    private void DialogueCacheCommitAll_Click(object sender, RoutedEventArgs e)
    {
        if (!isDialogueConversationPreview || dialogueRuntimeCache.Count == 0)
        {
            return;
        }

        DialogueSegmentRuntime[] pendingRuntimes = dialogueRuntimeCache.Values
            .Where(runtime => runtime.HasPendingChanges).ToArray();
        if (pendingRuntimes.Length == 0)
        {
            return;
        }
        if (MessageBox.Show(Window.GetWindow(this),
                $"Commit all cached TrackMove, FOV, and Package Editor edits from {pendingRuntimes.Length} node(s) to "
                + $"{Path.GetFileName(dialogueNodePreview.Conversation.Export.FileRef.FilePath)}?",
                "Commit Entire Dialogue Cache", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (CurveEditor3DModel trackModel in pendingRuntimes.SelectMany(runtime => runtime.TrackMoves)
                     .Select(option => option.Model).Where(model => model is not null).Distinct())
        {
            trackModel.CommitChanges();
        }
        foreach (CurveEditor3DFovModel fovModel in pendingRuntimes.SelectMany(runtime => runtime.TrackMoves)
                     .Select(option => option.FovModel).Where(model => model is not null).Distinct())
        {
            fovModel.CommitChanges();
        }
        if (!CommitDialogueWorkingPackageChanges(out int changedEntryCount, out string error))
        {
            foreach (DialogueSegmentRuntime runtime in pendingRuntimes)
            {
                runtime.HasPendingPackageChanges = true;
            }
            MessageBox.Show(Window.GetWindow(this), $"Unable to commit the cached package edits: {error}",
                "Commit Entire Dialogue Cache", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateDialogueNodeCommitButton();
            return;
        }
        foreach (DialogueSegmentRuntime runtime in pendingRuntimes)
        {
            runtime.HasPendingPreviewChanges = false;
            runtime.HasPendingPackageChanges = false;
        }
        UpdateDialogueNodeCommitButton();
        SceneStatus = changedEntryCount > 0
            ? $"Committed the entire dialogue cache ({pendingRuntimes.Length} changed node(s), {changedEntryCount} package entr{(changedEntryCount == 1 ? "y" : "ies")}) to the PCC."
            : $"Committed the entire dialogue cache ({pendingRuntimes.Length} changed node(s)) to the PCC.";
    }

    private void DialogueCacheSave_Click(object sender, RoutedEventArgs e) => SaveDialogueCachePresetInteractively();

    private void DialogueCachePresets_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DialogueCachePresetDialog(SaveDialogueCachePreset, IsDialogueCachePresetCompatible)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true || dialog.SelectedPreset is not { } preset)
        {
            return;
        }

        FileInfo sourceInfo = new(preset.SourceFilePath);
        bool sourceChanged = sourceInfo.Exists
                             && (sourceInfo.Length != preset.SourceFileSize
                                 || sourceInfo.LastWriteTimeUtc > preset.SourceLastWriteUtc.AddSeconds(1));
        if (sourceChanged
            && MessageBox.Show(Window.GetWindow(this),
                "This PCC changed after the cache was saved. Export identities will be revalidated while loading. Continue?",
                "Load Dialogue Cache", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        if (!TryRestoreDialogueCachePreset(preset, out string error))
        {
            MessageBox.Show(Window.GetWindow(this), $"Unable to load the dialogue cache: {error}",
                "Load Dialogue Cache", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        StartDialogueTimelinePlaybackAt(0, reconstruct: true);
    }

    private void UpdateDialogueNodeCommitButton()
    {
        if (DialogueNodeCommitButton is null)
        {
            return;
        }
        DialogueNodeCommitButton.Visibility = isDialogueConversationPreview
            ? Visibility.Visible
            : Visibility.Collapsed;
        DialogueNodeCommitButton.IsEnabled = activeDialogueSegmentRuntime?.HasPendingChanges == true;
        DialogueNodeCommitButton.Content = activeDialogueSegmentRuntime?.HasPendingChanges == true
            ? "Commit Node *"
            : "Commit Node";
        if (DialogueCacheCommitAllButton is not null)
        {
            bool hasPendingCacheChanges = dialogueRuntimeCache.Values.Any(runtime => runtime.HasPendingChanges);
            DialogueCacheCommitAllButton.Visibility = isDialogueConversationPreview
                ? Visibility.Visible
                : Visibility.Collapsed;
            DialogueCacheCommitAllButton.IsEnabled = hasPendingCacheChanges;
            DialogueCacheCommitAllButton.Content = hasPendingCacheChanges
                ? "Commit Entire Cache *"
                : "Commit Entire Cache";
        }
    }

    private void DialogueTimelineSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        resumeDialogueTimelineAfterScrub = isPlayingDialogueTimeline;
        PauseDialogueTimeline();
    }

    private void DialogueTimelineSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!resumeDialogueTimelineAfterScrub)
        {
            return;
        }
        resumeDialogueTimelineAfterScrub = false;
        StartDialogueTimelinePlayback();
    }

    private void DialogueTimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!updatingDialogueTimelineSlider && dialogueNodePreview is not null)
        {
            float targetTime = (float)e.NewValue;
            ApplyDialogueTimelineAtTime(targetTime, reconstruct: RequiresDialogueTimelineReconstruction(targetTime));
        }
    }

    private void DialogueTimelineNode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingDialogueTimelineSelection
            && sender is ListBox { SelectedItem: DialogueTimelineSegment segment })
        {
            PlayDialogueTimelineSegmentFromStart(segment);
        }
    }

    private void DialogueTimelineNode_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox timelineList
            || e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(timelineList, source) is not ListBoxItem { DataContext: DialogueTimelineSegment segment })
        {
            return;
        }

        updatingDialogueTimelineSelection = true;
        timelineList.SelectedItem = segment;
        updatingDialogueTimelineSelection = false;
        PlayDialogueTimelineSegmentFromStart(segment);
        e.Handled = true;
    }

    private void PlayDialogueTimelineSegmentFromStart(DialogueTimelineSegment segment)
    {
        PauseDialogueTimeline();
        bool selectedDifferentBranch = !dialogueTimelineActivePath.Contains(segment);
        bool navigatingBackward = !selectedDifferentBranch
                                  && segment.StartTime < dialogueTimelineCurrentTime - 0.0001f;
        if (selectedDifferentBranch || navigatingBackward)
        {
            // A seek back to a choice point invalidates every decision made after that point.
            // Clicking an off-path node is itself the new explicit choice, so only its downstream
            // choices are cleared before selecting the route to it.
            ClearDialogueBranchSelectionsFrom(segment);
        }
        if (selectedDifferentBranch && !SelectDialogueTimelinePathTo(segment))
        {
            return;
        }
        ForceCachedDialogueSegmentReactivation(segment);
        StartDialogueTimelinePlaybackAt(segment.StartTime, reconstruct: true);
    }

    private void ForceCachedDialogueSegmentReactivation(DialogueTimelineSegment segment)
    {
        if (isDialogueConversationPreview
            && dialogueRuntimeCache.ContainsKey(segment)
            && ReferenceEquals(activeDialogueTimelineSegment, segment))
        {
            activeDialogueTimelineSegment = null;
            activeDialogueSegmentRuntime = null;
        }
    }

    private bool RequiresDialogueTimelineReconstruction(float targetTime) =>
        targetTime < dialogueTimelineCurrentTime
        || activeDialogueTimelineSegment is null
        || targetTime < activeDialogueTimelineSegment.StartTime
        || targetTime >= activeDialogueTimelineSegment.EndTime;

    private void ApplyDialogueTimelineAtTime(float globalTime, bool reconstruct)
    {
        if (dialogueTimelineActivePath.Count == 0)
        {
            return;
        }

        bool movingBackward = globalTime < dialogueTimelineCurrentTime - 0.0001f;
        float endTime = GetDialogueTimelineEndTime();
        globalTime = Math.Clamp(globalTime, 0, endTime);
        DialogueTimelineSegment target = dialogueTimelineActivePath
            .FirstOrDefault(segment => globalTime < segment.EndTime)
            ?? dialogueTimelineActivePath[^1];
        if (movingBackward && ClearDialogueBranchSelectionsFrom(target))
        {
            endTime = GetDialogueTimelineEndTime();
            globalTime = Math.Clamp(globalTime, 0, endTime);
        }

        bool useCachedRuntime = isDialogueConversationPreview && dialogueRuntimeCache.Count > 0;
        if (reconstruct && useCachedRuntime)
        {
            // Cached activation replaces every actor's movement and gesture state below. Do not
            // clear the live players first: rendering that intermediate bind pose is what caused
            // actors to T-pose when navigating backward.
            activeDialogueTimelineSegment = null;
            activeDialogueSegmentRuntime = null;
            playbackActors.Clear();
        }
        if (reconstruct && !useCachedRuntime)
        {
            activeDialogueTimelineSegment = null;
            ResetDialogueTimelineActorGestures();
            foreach (DialogueNodePreviewActor actor in dialogueNodePreview.Actors)
            {
                PreviewActorConfiguration configuredActor = previewActors.FirstOrDefault(candidate =>
                    string.Equals(candidate.ActorTag, actor.ActorTag, StringComparison.OrdinalIgnoreCase));
                if (configuredActor is not null)
                {
                    configuredActor.Origin = actor.Origin;
                }
            }

            foreach (DialogueTimelineSegment segment in dialogueTimelineActivePath.TakeWhile(segment => segment.StartTime < target.StartTime))
            {
                ActivateDialogueTimelineSegment(segment);
                ApplyPlaybackAtTime(segment.Duration, playAudio: false);
            }
        }

        ActivateDialogueTimelineSegment(target);
        float localTime = Math.Clamp(globalTime - target.StartTime, 0, target.Duration);
        ApplyPlaybackAtTime(localTime, playAudio: isPlayingDialogueTimeline);
        dialogueTimelineCurrentTime = globalTime;
        UpdateDialogueTimelineControls();
        SceneViewer?.MarkRenderDirty();
    }

    private void ActivateDialogueTimelineSegment(DialogueTimelineSegment segment)
    {
        if (ReferenceEquals(activeDialogueTimelineSegment, segment))
        {
            return;
        }

        IReadOnlyDictionary<string, CameraOrigin> liveInheritedOrigins = null;
        IReadOnlyDictionary<string, DialogueGesturePoseState> liveInheritedGestureStates = null;
        if (activeDialogueTimelineSegment is not null
            && segment.StartTime >= activeDialogueTimelineSegment.EndTime)
        {
            // InterpTrackInstMove leaves the actor at its last evaluated transform. Finalize the
            // outgoing node before replacing its cached runtime so a node without a TrackMove can
            // inherit the exact last key instead of the preceding rendered frame or scene origin.
            ApplyPlaybackAtTime(activeDialogueTimelineSegment.Duration, playAudio: false);
            liveInheritedOrigins = previewActors.ToDictionary(actor => actor.ActorTag,
                actor => actor.Origin, StringComparer.OrdinalIgnoreCase);
            liveInheritedGestureStates = activeDialogueSegmentRuntime?.EndActorGestureStates;
        }

        if (isDialogueConversationPreview
            && dialogueRuntimeCache.TryGetValue(segment, out DialogueSegmentRuntime cachedRuntime))
        {
            ActivateCachedDialogueRuntime(cachedRuntime, liveInheritedOrigins, liveInheritedGestureStates);
            return;
        }

        Dictionary<string, CameraOrigin> actorOrigins = previewActors.ToDictionary(actor => actor.ActorTag,
            actor => actor.Origin, StringComparer.OrdinalIgnoreCase);
        dialogueNodePreview = dialogueNodePreview with
        {
            Node = segment.Node,
            VoStartTime = GetDialoguePreviewVoStartTime(segment.Node.InterpData)
        };
        ExportEntry trackMove = FindDialoguePreviewTrackMove(segment.Node.InterpData);
        if (trackMove is not null)
        {
            loadingDialogueTimelineSegment = true;
            try
            {
                LoadExport(trackMove);
            }
            finally
            {
                loadingDialogueTimelineSegment = false;
            }
            foreach (PreviewActorConfiguration actor in previewActors)
            {
                if (actorOrigins.TryGetValue(actor.ActorTag, out CameraOrigin origin))
                {
                    actor.Origin = origin;
                }
            }
            ConfigureDialoguePreviewPlayback();
            PrepareDialogueTimelineActorPlayback();
        }
        else
        {
            RefreshAvailableGestureTracksForInterpData(segment.Node.InterpData);
            ConfigureDialoguePreviewPlayback(configureTrackPlayback: false);
            PrepareDialogueTimelineActorPlayback(includeMovementTracks: false);
        }
        segment.IsVisited = true;
        activeDialogueTimelineSegment = segment;
    }

    private void ActivateCachedDialogueRuntime(DialogueSegmentRuntime runtime,
        IReadOnlyDictionary<string, CameraOrigin> liveInheritedOrigins = null,
        IReadOnlyDictionary<string, DialogueGesturePoseState> liveInheritedGestureStates = null)
    {
        DialoguePreviewSoundpanel.StopPlaying();
        FaceOnlyVoSoundpanel.StopPlaying();
        DialoguePreviewSoundpanel.UnloadExport();
        FaceOnlyVoSoundpanel.UnloadExport();
        dialoguePreviewAudioStarted = false;
        faceOnlyVoAudioStarted = false;
        activeFaceOnlyVoEvent = null;

        suppressDialogueCacheEditTracking = true;
        try
        {
            dialogueNodePreview = dialogueNodePreview with
            {
                Node = runtime.Segment.Node,
                VoStartTime = GetDialogueNodeVoStartTime(runtime.Segment.Node)
            };
            foreach (PreviewActorConfiguration actor in previewActors)
            {
                CameraOrigin cachedStart = runtime.StartActorOrigins.GetValueOrDefault(actor.ActorTag, actor.Origin);
                CameraOrigin? actorOverride = runtime.ActorOriginOverrides.TryGetValue(actor.ActorTag,
                    out CameraOrigin overrideOrigin)
                    ? overrideOrigin
                    : null;
                CameraOrigin? liveInherited = liveInheritedOrigins is not null
                                                  && liveInheritedOrigins.TryGetValue(actor.ActorTag,
                                                      out CameraOrigin liveOrigin)
                    ? liveOrigin
                    : null;
                bool hasMovementTrack = runtime.ActorTrackAssignments.TryGetValue(actor.ActorTag,
                                            out TrackMovePlaybackOption movementTrack)
                                        && movementTrack?.Model?.Keyframes is { Count: > 0 };
                actor.Origin = ResolveDialogueActorStartOrigin(cachedStart, actorOverride, liveInherited,
                    hasMovementTrack);
            }

            availableTrackMoves.Clear();
            availableTrackMoves.AddRange(runtime.TrackMoves);
            availableExtraTrackMoves.Clear();
            foreach (TrackMovePlaybackOption option in runtime.ExtraTrackMoves)
            {
                availableExtraTrackMoves.Add(option);
            }
            availableDirectorTracks.Clear();
            foreach (DirectorPlaybackOption option in runtime.DirectorTracks)
            {
                availableDirectorTracks.Add(option);
            }
            dialoguePreviewCameraActors.Clear();
            dialoguePreviewCameraActors.AddRange(runtime.CameraTracks);
            primaryTrackMove = runtime.PrimaryTrackMove;

            previewActorTrackAssignments.Clear();
            foreach (PreviewActorConfiguration actor in previewActors)
            {
                if (runtime.ActorTrackAssignments.TryGetValue(actor.ActorTag, out TrackMovePlaybackOption trackMove))
                {
                    previewActorTrackAssignments[actor] = trackMove;
                }
            }
            availableGestureTracks.Clear();
            foreach (GestureTrackOption gesture in runtime.GestureTracks)
            {
                availableGestureTracks.Add(gesture);
            }
            previewActorGestureAssignments.Clear();
            foreach (PreviewActorConfiguration actor in previewActors)
            {
                if (runtime.ActorGestureAssignments.TryGetValue(actor.ActorTag, out GestureTrackOption gesture))
                {
                    previewActorGestureAssignments[actor] = gesture;
                    DialogueGesturePoseState inheritedGestureState = liveInheritedGestureStates is not null
                                                                     && liveInheritedGestureStates.TryGetValue(
                                                                         actor.ActorTag, out DialogueGesturePoseState liveState)
                        ? liveState
                        : runtime.StartActorGestureStates.GetValueOrDefault(actor.ActorTag);
                    float? startingPoseTimeOverride = ResolveStartingPoseContinuationTime(gesture,
                        inheritedGestureState);
                    ApplyAssignedGestureToActor(actor, runtime.Segment.Duration, startingPoseTimeOverride);
                }
                else if (previewActorAnimationStates.TryGetValue(actor, out PreviewActorAnimationState heldAnimationState))
                {
                    if (runtime.StartActorGesturePoses.TryGetValue(actor.ActorTag, out Matrix4x4[] heldPose))
                    {
                        heldAnimationState.SetHeldGesturePose(heldPose);
                    }
                    else
                    {
                        heldAnimationState.Clear();
                    }
                    UpdatePreviewActorSkinning(actor);
                }
            }
            actorDirectionTracks.Clear();
            actorDirectionTracks.AddRange(runtime.DirectionTracks);
            faceOnlyVoEvents.Clear();
            faceOnlyVoEvents.AddRange(runtime.FaceOnlyVoEvents);

            ClearDialoguePreviewFaceFx();
            foreach (PreviewActorConfiguration actor in previewActors)
            {
                AttachDialoguePreviewFaceFxAsset(actor);
            }
            if (runtime.MainFaceFx is { } faceFx
                && previewActorAnimationStates.TryGetValue(faceFx.Actor, out PreviewActorAnimationState animationState))
            {
                if (faceFx.AnimSet is not null)
                {
                    dialoguePreviewFaceFxAnimSets[faceFx.Actor] = faceFx.AnimSet;
                }
                animationState.SetFaceFx(faceFx.Asset, faceFx.AnimSet, faceFx.Line, faceFx.TimelineOffset);
                UpdatePreviewActorSkinning(faceFx.Actor);
            }

            updatingMulticamControls = true;
            selectedDirectorPlayback = runtime.DirectorTracks.FirstOrDefault(option => option.DirectorTrack is not null)
                                       ?? DirectorPlaybackOption.None;
            playDirectorMulticam = selectedDirectorPlayback.Cuts.Count > 0;
            selectedExtraTrackMove = playDirectorMulticam
                ? TrackMovePlaybackOption.None
                : runtime.ExtraTrackMoves.FirstOrDefault(option => option.TrackMove is not null)
                  ?? TrackMovePlaybackOption.None;
            playExtraTrackMove = !playDirectorMulticam && selectedExtraTrackMove.TrackMove is not null;
            DirectorTrackComboBox.SelectedItem = selectedDirectorPlayback;
            ExtraTrackMoveComboBox.SelectedItem = selectedExtraTrackMove;
            DirectorMulticamCheckBox.IsChecked = playDirectorMulticam;
            ExtraTrackMoveCheckBox.IsChecked = playExtraTrackMove;
            updatingMulticamControls = false;

            activeDialogueSegmentRuntime = runtime;
            activeDialogueTimelineSegment = runtime.Segment;
            runtime.Segment.IsVisited = true;
            if (primaryTrackMove is not null)
            {
                RefreshKeyframeTrackMoveTabs();
                CurrentExportName = $"{primaryTrackMove.TrackMove.UIndex}: {primaryTrackMove.TrackMove.InstancedFullPath}";
            }
            else
            {
                UnregisterKeyframes();
                characterTrackMoves.Clear();
                cameraTrackMoves.Clear();
                KeyframeList.ItemsSource = null;
                FovKeyframeList.ItemsSource = null;
                SelectedKeyframe = null;
                SelectedFovKeyframe = null;
                CurrentExportName = $"{runtime.Segment.NodeLabel}: no TrackMove";
            }
            PrepareDialogueTimelineActorPlayback();
            SynchronizePreviewActorControls();
            UpdatePreviewActorTrackAssignmentControls();
        }
        finally
        {
            suppressDialogueCacheEditTracking = false;
        }
        NavigateDialoguePackageEditorToActiveNode();
        UpdateDialogueNodeCommitButton();
    }

    internal static CameraOrigin ResolveDialogueActorStartOrigin(CameraOrigin cachedStart,
        CameraOrigin? actorOverride, CameraOrigin? liveInheritedOrigin, bool hasMovementTrack)
        => actorOverride
           ?? (!hasMovementTrack ? liveInheritedOrigin : null)
           ?? cachedStart;

    private void PrepareDialogueTimelineActorPlayback(bool includeMovementTracks = true)
    {
        playbackActors.Clear();
        foreach (PreviewActorConfiguration actor in previewActors)
        {
            TrackMovePlaybackOption trackMove = includeMovementTracks
                ? previewActorTrackAssignments.GetValueOrDefault(actor)
                : null;
            CameraOrigin originalOrigin = actor.Origin;
            playbackActors.Add(new PreviewActorPlaybackState
            {
                Actor = actor,
                TrackMove = trackMove,
                OriginalOrigin = originalOrigin,
                MoveFrame = GetTrackMoveFrame(trackMove)
            });
        }
        isPlayingActor = playbackActors.Count > 0;
        isPlayingMove = false;
        dialoguePreviewAudioStarted = false;
        faceOnlyVoAudioStarted = false;
        activeFaceOnlyVoEvent = null;
    }

    private void ResetDialogueTimelineActorGestures()
    {
        previewActorGestureAssignments.Clear();
        foreach (KeyValuePair<PreviewActorConfiguration, PreviewActorAnimationState> pair in previewActorAnimationStates)
        {
            pair.Value.Clear();
            UpdatePreviewActorSkinning(pair.Key);
        }
        if (!isDialogueConversationPreview)
        {
            previewActorGesturePackageCache.ReleasePackages();
        }
    }

    private void PauseDialogueTimeline()
    {
        isPlayingDialogueTimeline = false;
        if (FindName("DialogueTimelinePlayButton") is Button playButton)
        {
            playButton.Content = "Play";
        }
        RenderContext.ForceContinuousRendering = false;
        DialoguePreviewSoundpanel.StopPlaying();
        FaceOnlyVoSoundpanel.StopPlaying();
        dialoguePreviewAudioStarted = false;
        faceOnlyVoAudioStarted = false;
        SceneViewer?.MarkRenderDirty();
    }

    private float GetDialogueTimelineEndTime() => dialogueTimelineActivePath.LastOrDefault()?.EndTime ?? 0;

    private void UpdateDialogueTimelineControls()
    {
        if (FindName("DialogueTimelineSlider") is not Slider slider
            || FindName("DialogueTimelineTimeText") is not TextBlock timeText)
        {
            return;
        }
        float endTime = GetDialogueTimelineEndTime();
        updatingDialogueTimelineSlider = true;
        slider.Maximum = endTime;
        slider.Value = Math.Clamp(dialogueTimelineCurrentTime, 0, endTime);
        updatingDialogueTimelineSlider = false;
        timeText.Text = $"{dialogueTimelineCurrentTime:0.00} / {endTime:0.00}";
        if (FindName("DialogueTimelineListBox") is ListBox timelineList)
        {
            DialogueTimelineSegment currentSegment = activeDialogueTimelineSegment is not null
                                                     && dialogueTimelineActivePath.Contains(activeDialogueTimelineSegment)
                ? activeDialogueTimelineSegment
                : dialogueTimelineActivePath.FirstOrDefault(segment =>
                    dialogueTimelineCurrentTime >= segment.StartTime && dialogueTimelineCurrentTime < segment.EndTime)
                  ?? dialogueTimelineActivePath.LastOrDefault();
            bool selectionChanged = !ReferenceEquals(timelineList.SelectedItem, currentSegment);
            updatingDialogueTimelineSelection = true;
            timelineList.SelectedItem = currentSegment;
            if (selectionChanged && currentSegment is not null)
            {
                timelineList.ScrollIntoView(currentSegment);
                Dispatcher.BeginInvoke(() =>
                {
                    if (timelineList.ItemContainerGenerator.ContainerFromItem(currentSegment)
                        is FrameworkElement container)
                    {
                        container.BringIntoView();
                    }
                }, DispatcherPriority.Background);
            }
            updatingDialogueTimelineSelection = false;
        }
    }

    private void ApplyPlaybackAtTime(float time, bool playAudio = true)
    {
        playbackCurrentTime = time;
        if (isPlayingActor)
        {
            ApplyActorsAtTime(time);
            if (faceOnlyVoEvents.Count > 0)
            {
                ApplyFaceOnlyVoAtTime(time, playAudio);
            }
            if (playAudio)
            {
                UpdateDialoguePreviewAudio(time);
            }
            UpdateAdditionalCameraPlayback(time);
        }
        else
        {
            ApplyCameraAtTime(time);
        }
    }

    private void SetPlaybackRangeForCurrentMode(bool includeActorTracks = false)
    {
        IReadOnlyList<CurveEditor3DKeyframe> initialKeys = includeActorTracks
                                                          && playbackActors.FirstOrDefault(state =>
                                                              state.TrackMove?.Model?.Keyframes.Count > 0) is { } firstActor
            ? firstActor.TrackMove.Model.Keyframes
            : ActiveModel.Keyframes;
        playbackStartTime = initialKeys[0].Time;
        playbackEndTime = initialKeys[^1].Time;
        IncludeFovPlaybackRange(ActiveTrackMoveOption);

        if (includeActorTracks)
        {
            foreach (PreviewActorPlaybackState state in playbackActors)
            {
                if (state.TrackMove?.Model?.Keyframes is not { Count: > 0 } actorKeys)
                {
                    continue;
                }
                playbackStartTime = MathF.Min(playbackStartTime, actorKeys[0].Time);
                playbackEndTime = MathF.Max(playbackEndTime, actorKeys[^1].Time);
            }
            if (dialogueNodePreview?.Node.InterpData?.GetProperty<FloatProperty>("InterpLength") is { } interpLength)
            {
                playbackStartTime = MathF.Min(playbackStartTime, 0);
                playbackEndTime = MathF.Max(playbackEndTime, interpLength.Value);
            }
        }

        if (playExtraTrackMove && selectedExtraTrackMove?.Model.Keyframes is { Count: > 0 } extraKeys)
        {
            playbackStartTime = MathF.Min(playbackStartTime, extraKeys[0].Time);
            playbackEndTime = MathF.Max(playbackEndTime, extraKeys[^1].Time);
            IncludeFovPlaybackRange(selectedExtraTrackMove);
        }

        if (playDirectorMulticam && selectedDirectorPlayback?.Cuts is { Count: > 0 } cuts)
        {
            playbackStartTime = MathF.Min(playbackStartTime, cuts[0].Time);
            playbackEndTime = MathF.Max(playbackEndTime, cuts[^1].Time);
            foreach (DirectorCameraCut cut in cuts)
            {
                if (cut.Camera?.Model.Keyframes is { Count: > 0 } cameraKeys)
                {
                    playbackStartTime = MathF.Min(playbackStartTime, cameraKeys[0].Time);
                    playbackEndTime = MathF.Max(playbackEndTime, cameraKeys[^1].Time);
                    IncludeFovPlaybackRange(cut.Camera);
                }
            }
        }
    }

    private void IncludeFovPlaybackRange(TrackMovePlaybackOption camera)
    {
        if (camera?.FovModel?.Keyframes is not { Count: > 0 } fovKeys)
        {
            return;
        }

        playbackStartTime = MathF.Min(playbackStartTime, fovKeys[0].Time);
        playbackEndTime = MathF.Max(playbackEndTime, fovKeys[^1].Time);
    }

    private TrackMovePlaybackOption GetPlaybackCameraOption(float time)
    {
        if (GetPlaybackDirectorCut(time) is { Camera: not null } directorCut)
        {
            return directorCut.Camera;
        }

        return availableTrackMoves.FirstOrDefault(option => ReferenceEquals(option.Model, ActiveModel))
               ?? primaryTrackMove;
    }

    private DirectorCameraCut GetPlaybackDirectorCut(float time)
    {
        if (!playDirectorMulticam || selectedDirectorPlayback?.Cuts is not { Count: > 0 } cuts)
        {
            return null;
        }

        DirectorCameraCut cut = cuts[0];
        foreach (DirectorCameraCut candidate in cuts)
        {
            if (candidate.Time > time)
            {
                break;
            }
            cut = candidate;
        }
        return cut;
    }

    private static CameraOrigin EvaluateTrackMove(TrackMovePlaybackOption trackMove, float time)
        => EvaluateTrackMove(trackMove?.Model, time, trackMove?.UseQuaternionInterpolation == true);

    internal static CameraOrigin EvaluateTrackMove(CurveEditor3DModel trackModel, float time,
        bool useQuaternionInterpolation = false)
    {
        Vector3 location = trackModel?.PositionTrack?.Eval(time, Vector3.Zero) ?? Vector3.Zero;
        Vector3 rotation = useQuaternionInterpolation
            ? EvaluateQuaternionTrackRotation(trackModel, time)
            : trackModel?.RotationTrack?.Eval(time, Vector3.Zero) ?? Vector3.Zero;
        return new CameraOrigin(location, rotation);
    }

    private static Vector3 EvaluateQuaternionTrackRotation(CurveEditor3DModel trackModel, float time)
    {
        IReadOnlyList<InterpCurvePoint<Vector3>> points = trackModel?.RotationTrack?.Points;
        if (points is not { Count: > 0 })
        {
            return Vector3.Zero;
        }
        if (points.Count == 1 || time <= points[0].InVal)
        {
            return points[0].OutVal;
        }
        if (time >= points[^1].InVal)
        {
            return points[^1].OutVal;
        }

        int upperIndex = 1;
        while (upperIndex < points.Count && points[upperIndex].InVal < time)
        {
            upperIndex++;
        }
        InterpCurvePoint<Vector3> lower = points[upperIndex - 1];
        InterpCurvePoint<Vector3> upper = points[upperIndex];
        if (lower.InterpMode == EInterpCurveMode.CIM_Constant && time < upper.InVal)
        {
            return lower.OutVal;
        }
        Quaternion lowerRotation = Rotator.FromDegreesVector(lower.OutVal).ToQuaternion();
        Quaternion upperRotation = Rotator.FromDegreesVector(upper.OutVal).ToQuaternion();
        if (Quaternion.Dot(lowerRotation, upperRotation) < 0)
        {
            upperRotation = Quaternion.Negate(upperRotation);
        }
        float alpha = upper.InVal > lower.InVal
            ? Math.Clamp((time - lower.InVal) / (upper.InVal - lower.InVal), 0, 1)
            : 0;
        return Rotator.FromQuaternion(Quaternion.Slerp(lowerRotation, upperRotation, alpha)).GetDegreesVector();
    }

    private static EInterpTrackMoveFrame GetTrackMoveFrame(TrackMovePlaybackOption trackMove) =>
        GetTrackMoveFrame(trackMove?.TrackMove);

    private static EInterpTrackMoveFrame GetTrackMoveFrame(ExportEntry trackMove) =>
        trackMove?.GetProperty<EnumProperty>("MoveFrame")
            .GetEnumValOrDefault(EInterpTrackMoveFrame.IMF_World)
        ?? EInterpTrackMoveFrame.IMF_World;

    private CameraOrigin? TrackAnchorOrigin =>
        dialogueNodePreview?.StageContext.StageOrigin ?? trackAnchorStageContext?.StageOrigin;

    private CameraOrigin ResolveAnchorObjectTrackOrigin(CameraOrigin trackOrigin)
        => TrackAnchorOrigin is { } anchor
            ? InterpTrackMoveTransform.ToWorld(anchor, trackOrigin)
            : trackOrigin;

    private CameraOrigin ResolveCameraTrackOrigin(TrackMovePlaybackOption trackMove, CameraOrigin trackOrigin,
        CameraOrigin? initialOrigin = null)
    {
        EInterpTrackMoveFrame moveFrame = GetTrackMoveFrame(trackMove?.TrackMove ?? ActiveTrackMoveExport);
        if (moveFrame == EInterpTrackMoveFrame.IMF_AnchorObject)
        {
            return ResolveAnchorObjectTrackOrigin(trackOrigin);
        }
        if (moveFrame != EInterpTrackMoveFrame.IMF_RelativeToInitial)
        {
            return trackOrigin;
        }

        string actorTag = GetCameraActorTag(trackMove?.Group);
        if (initialOrigin is null && !string.IsNullOrWhiteSpace(actorTag))
        {
            if (activeDialogueSegmentRuntime?.StartCameraOrigins.TryGetValue(actorTag,
                    out CameraOrigin cachedInitial) == true)
            {
                initialOrigin = cachedInitial;
            }
            else if (dialoguePlacedCameras.TryGetValue(actorTag, out PlacedCameraState placedCamera))
            {
                initialOrigin = placedCamera.Origin;
            }
        }
        return initialOrigin is { } basis
            ? InterpTrackMoveTransform.ToWorld(basis, trackOrigin)
            : trackOrigin;
    }

    private CameraOrigin ResolveActorTrackOrigin(PreviewActorPlaybackState state, CameraOrigin trackOrigin)
    {
        CameraOrigin origin = state.MoveFrame switch
        {
            EInterpTrackMoveFrame.IMF_RelativeToInitial =>
                InterpTrackMoveTransform.ToWorld(state.OriginalOrigin, trackOrigin),
            EInterpTrackMoveFrame.IMF_AnchorObject => ResolveAnchorObjectTrackOrigin(trackOrigin),
            _ => trackOrigin
        };
        Vector3 location = origin.Location;
        if (state.TrackMove?.UsesLegacyStuntActorLocation == true)
        {
            // SFXStuntActor applies this compatibility correction to locations authored before its
            // BodyMesh was moved down by 88 units (see SFXStuntActor.OnTeleport in SFXGame.pcc).
            location.Z -= PreviewBodyMeshRelativeZ;
        }
        if (!ShouldUseActorTrackZ(dialogueNodePreview is not null,
                ActorPlaybackTrackZCheckBox.IsChecked == true))
        {
            location.Z = state.OriginalOrigin.Location.Z;
        }
        return new CameraOrigin(location, origin.Rotation);
    }

    internal static bool ShouldUseActorTrackZ(bool isDialoguePreview, bool manualTrackZEnabled) =>
        isDialoguePreview || manualTrackZEnabled;

    private void ApplyViewportCameraOrigin(CameraOrigin origin)
    {
        const float degreesToRadians = 0.017453292519943295f;
        RenderContext.Camera.Position = origin.Location;
        RenderContext.Camera.Roll = origin.Rotation.X * degreesToRadians;
        RenderContext.Camera.Pitch = origin.Rotation.Y * degreesToRadians;
        RenderContext.Camera.Yaw = origin.Rotation.Z * degreesToRadians;
    }

    private void ApplyViewportCameraAtTime(TrackMovePlaybackOption camera, float time)
    {
        const float defaultFovDegrees = 60f;
        const float degreesToRadians = 0.017453292519943295f;
        camera ??= ActiveTrackMoveOption;
        string actorTag = GetCameraActorTag(camera?.Group);
        float initialFovDegrees = !string.IsNullOrWhiteSpace(actorTag)
                                  && activeDialogueSegmentRuntime?.StartCameraFovs.TryGetValue(actorTag,
                                      out float cachedFov) == true
            ? cachedFov
            : defaultFovDegrees;
        if (camera?.DisableMovement != true)
        {
            CameraOrigin origin = ResolveCameraTrackOrigin(camera, EvaluateTrackMove(camera, time));
            origin = ApplyTrackLookAtRotation(camera, origin, time, null);
            ApplyViewportCameraOrigin(origin);
        }
        RenderContext.Camera.FOV = (camera?.FovTrack?.Eval(time, initialFovDegrees) ?? initialFovDegrees)
                                   * degreesToRadians;
    }

    private void ApplyPlaybackViewportCameraAtTime(float time)
    {
        if (TryResolveQueuedSwitchCameraAtSegmentEnd(time, out ResolvedSwitchCamera queuedCamera))
        {
            ApplySwitchCamera(queuedCamera);
            return;
        }

        DirectorCameraCut directorCut = GetPlaybackDirectorCut(time);
        if (directorCut?.SwitchCameraTrack is not null
            && TryResolveSwitchCamera(directorCut.SwitchCameraTrack, time, useForNextCamera: false,
                out ResolvedSwitchCamera stageCamera))
        {
            ApplySwitchCamera(stageCamera);
            return;
        }
        if (directorCut?.Camera is null && directorCut?.FallbackOrigin is { } fallbackOrigin)
        {
            const float degreesToRadians = 0.017453292519943295f;
            ApplyViewportCameraOrigin(fallbackOrigin);
            RenderContext.Camera.FOV = (directorCut.FallbackFovDegrees ?? dialoguePreviewInitialCameraFovDegrees)
                                       * degreesToRadians;
            return;
        }

        ApplyViewportCameraAtTime(directorCut?.Camera ?? GetPlaybackCameraOption(time), time);
    }

    private bool TryResolveQueuedSwitchCameraAtSegmentEnd(float time, out ResolvedSwitchCamera camera)
    {
        camera = null;
        if (activeDialogueSegmentRuntime?.Segment is not { } segment
            || time < segment.Duration - 0.0001f)
        {
            return false;
        }

        foreach (ExportEntry interpData in GetDialogueNodeInterpDatas(segment.Node))
        {
            ExportEntry switchCameraTrack = FindSwitchCameraTrack(interpData);
            if (TryResolveSwitchCamera(switchCameraTrack, time, useForNextCamera: true,
                    out ResolvedSwitchCamera queuedCamera))
            {
                camera = queuedCamera;
            }
        }
        return camera is not null;
    }

    private void ApplySwitchCamera(ResolvedSwitchCamera camera)
    {
        const float degreesToRadians = 0.017453292519943295f;
        ApplyViewportCameraOrigin(camera.Origin);
        RenderContext.Camera.FOV = camera.FovDegrees * degreesToRadians;
    }

    private void UpdateAdditionalCameraPlayback(float time)
    {
        playbackCurrentTime = time;
    }

    private void ApplyCameraAtTime(float time)
    {
        ApplyPlaybackViewportCameraAtTime(time);
        RenderContext.Camera.FocusDepth = 0f;
        PlaybackKeyframeStatus = GetPlaybackKeyframeStatus(time);
        string playbackMode = playDirectorMulticam ? "director multicam" : "camera";
        SceneStatus = $"Playing {playbackMode} at InVal {time:0.###} / {playbackEndTime:0.###}; {levelPaths.Count} level backdrop file(s).";
        UpdateAdditionalCameraPlayback(time);
        UpdateCameraPositionText();
        UpdateCameraRotationText();
        SceneViewer.MarkRenderDirty();
    }

    private void ApplyActorsAtTime(float time)
    {
        if (playbackActors.Count == 0)
        {
            return;
        }

        var resolvedOrigins = new Dictionary<PreviewActorPlaybackState, CameraOrigin>();
        foreach (PreviewActorPlaybackState state in playbackActors)
        {
            Vector3 extractedRootMotion = Vector3.Zero;
            if (previewActorAnimationStates.TryGetValue(state.Actor, out PreviewActorAnimationState animationState))
            {
                // FaceFX can remain active after the body player has been cleared. Restore the
                // authored gesture timeline before evaluating both its pose and extracted motion.
                if (previewActorGestureAssignments.TryGetValue(state.Actor, out GestureTrackOption assignedGesture)
                    && (assignedGesture.Timeline.Count > 0 || assignedGesture.StartingPose is not null)
                    && (!animationState.HasGestureTimeline
                        || !ReferenceEquals(animationState.AppliedGesture, assignedGesture)))
                {
                    ApplyAssignedGestureToActor(state.Actor, activeDialogueSegmentRuntime?.Segment.Duration);
                }
                if (animationState.HasTimeline)
                {
                    extractedRootMotion = state.TrackMove?.Model?.Keyframes is { Count: > 0 } movementKeys
                        ? animationState.EvaluateExtractedRootMotionDelta(movementKeys[^1].Time, time)
                        : animationState.EvaluateExtractedRootMotion(time);
                }
            }

            if (state.TrackMove?.Model?.Keyframes is { Count: > 0 } actorKeys)
            {
                float actorTrackTime = Math.Clamp(time, actorKeys[0].Time, actorKeys[^1].Time);
                CameraOrigin trackOrigin = EvaluateTrackMove(state.TrackMove, actorTrackTime);
                resolvedOrigins[state] = ApplyDialogueGestureRootMotion(
                    ResolveActorTrackOrigin(state, trackOrigin), extractedRootMotion);
            }
            else
            {
                resolvedOrigins[state] = ApplyDialogueGestureRootMotion(state.OriginalOrigin,
                    extractedRootMotion);
            }
        }
        Dictionary<PreviewActorConfiguration, CameraOrigin> actorOrigins = resolvedOrigins
            .ToDictionary(pair => pair.Key.Actor, pair => pair.Value);
        var finalActorOrigins = new Dictionary<PreviewActorConfiguration, CameraOrigin>();
        foreach (PreviewActorPlaybackState state in playbackActors)
        {
            CameraOrigin origin = ApplyTrackLookAtRotation(state.TrackMove, resolvedOrigins[state], time, resolvedOrigins);
            finalActorOrigins[state.Actor] = ApplyActorDirectionTracks(state.Actor, time, origin, actorOrigins,
                state.TrackMove?.TrackMove is not null,
                previewActorAnimationStates.GetValueOrDefault(state.Actor));
        }
        foreach (PreviewActorPlaybackState state in playbackActors)
        {
            state.Actor.Origin = finalActorOrigins[state.Actor];
            if (ReferenceEquals(selectedPreviewActor, state.Actor))
            {
                updatingPreviewActorControls = true;
                SetPreviewActorOriginFields(state.Actor.Origin);
                UpdatePreviewActorRotationDialIndicator();
                updatingPreviewActorControls = false;
            }
            if (previewActorAnimationStates.TryGetValue(state.Actor, out PreviewActorAnimationState animationState))
            {
                ApplyPreviewActorLookAt(state.Actor, time, finalActorOrigins, animationState);
                UpdatePreviewActorSkinning(state.Actor);
            }
        }
        ApplyActorPlaybackCameraAtTime(time);
        PlaybackKeyframeStatus = GetPlaybackKeyframeStatus(time);
        string cameraMode = playDirectorMulticam ? " with director multicam" : playExtraTrackMove ? " with extra camera" : string.Empty;
        SceneStatus = $"Playing {playbackActors.Count} actor(s){cameraMode} at InVal {time:0.###} / {playbackEndTime:0.###}; {levelPaths.Count} level backdrop file(s).";
        SceneViewer.MarkRenderDirty();
    }

    private void ApplyPreviewActorLookAt(PreviewActorConfiguration actor, float time,
        IReadOnlyDictionary<PreviewActorConfiguration, CameraOrigin> actorOrigins,
        PreviewActorAnimationState animationState)
    {
        string inheritedTarget = activeDialogueSegmentRuntime?.StartLookAtTargets
            .GetValueOrDefault(actor.ActorTag);
        string targetTag = ResolveDialogueLookAtTarget(
            activeDialogueSegmentRuntime?.DirectionTracks ?? actorDirectionTracks,
            actor.ActorTag, time, inheritedTarget);
        Vector3? targetWorld = null;
        if (!string.IsNullOrWhiteSpace(targetTag))
        {
            PreviewActorConfiguration targetActor = actorOrigins.Keys.FirstOrDefault(candidate =>
                !ReferenceEquals(candidate, actor) && ActorTagMatches(targetTag, candidate.ActorTag));
            if (targetActor is not null)
            {
                CameraOrigin targetOrigin = actorOrigins[targetActor];
                targetWorld = previewActorAnimationStates.TryGetValue(targetActor,
                    out PreviewActorAnimationState targetAnimationState)
                    ? targetAnimationState.GetLookAtAnchorWorld(targetOrigin)
                    : targetOrigin.Location + new Vector3(0, 0, 72);
            }
            else if (dialogueLookAtTargets.TryGetValue(targetTag, out CameraOrigin targetOrigin))
            {
                targetWorld = targetOrigin.Location;
            }
        }
        animationState.SetLookAtTargetWorld(targetWorld, actorOrigins[actor]);
    }

    private CameraOrigin ApplyTrackLookAtRotation(TrackMovePlaybackOption trackMove, CameraOrigin origin, float time,
        IReadOnlyDictionary<PreviewActorPlaybackState, CameraOrigin> resolvedActorOrigins)
    {
        if (trackMove?.DisableMovement == true
            || trackMove?.RotationMode != EInterpTrackMoveRotMode.IMR_LookAtGroup
            || string.IsNullOrWhiteSpace(trackMove.LookAtGroupName))
        {
            return origin;
        }

        CameraOrigin? target = null;
        if (resolvedActorOrigins is not null)
        {
            KeyValuePair<PreviewActorPlaybackState, CameraOrigin> targetPair = resolvedActorOrigins
                .FirstOrDefault(pair => string.Equals(GetInterpGroupName(pair.Key.TrackMove?.Group),
                    trackMove.LookAtGroupName, StringComparison.OrdinalIgnoreCase));
            if (targetPair.Key is not null)
            {
                target = targetPair.Value;
            }
        }
        if (target is null)
        {
            PreviewActorConfiguration trackedActor = previewActors.FirstOrDefault(actor =>
                previewActorTrackAssignments.TryGetValue(actor, out TrackMovePlaybackOption assignment)
                && string.Equals(GetInterpGroupName(assignment.Group), trackMove.LookAtGroupName,
                    StringComparison.OrdinalIgnoreCase));
            target = trackedActor?.Origin;
        }
        if (target is null)
        {
            TrackMovePlaybackOption targetTrack = availableTrackMoves.FirstOrDefault(option =>
                string.Equals(GetInterpGroupName(option.Group), trackMove.LookAtGroupName,
                    StringComparison.OrdinalIgnoreCase));
            if (targetTrack is not null)
            {
                target = ResolveCameraTrackOrigin(targetTrack, EvaluateTrackMove(targetTrack, time));
            }
        }
        if (target is null)
        {
            PreviewActorConfiguration targetActor = previewActors.FirstOrDefault(actor =>
                string.Equals(actor.ActorTag, trackMove.LookAtGroupName, StringComparison.OrdinalIgnoreCase));
            target = targetActor?.Origin;
        }

        Vector3 direction = target?.Location - origin.Location ?? Vector3.Zero;
        return direction.LengthSquared() > 0.0001f
            ? new CameraOrigin(origin.Location, Rotator.FromDirectionVector(direction).GetDegreesVector())
            : origin;
    }

    private void ApplyActorPlaybackCameraAtTime(float time)
    {
        if (playDirectorMulticam)
        {
            ApplyPlaybackViewportCameraAtTime(time);
        }
        else if (playExtraTrackMove && selectedExtraTrackMove?.Model is not null)
        {
            ApplyViewportCameraAtTime(selectedExtraTrackMove, time);
        }
        else
        {
            return;
        }

        RenderContext.Camera.FocusDepth = 0f;
        UpdateCameraPositionText();
        UpdateCameraRotationText();
    }

    private string GetPlaybackKeyframeStatus(float time)
    {
        int keyframeCount = ActiveModel.Keyframes.Count;
        if (keyframeCount == 0)
        {
            return "Not playing";
        }

        int currentIndex = 0;
        for (int i = 1; i < keyframeCount; i++)
        {
            if (ActiveModel.Keyframes[i].Time > time)
            {
                break;
            }

            currentIndex = i;
        }

        CurveEditor3DKeyframe currentKeyframe = ActiveModel.Keyframes[currentIndex];
        return $"Keyframe {currentIndex + 1} of {keyframeCount} (InVal {currentKeyframe.Time:0.###})";
    }

    private void StopPlayback(bool restoreStatus = true)
    {
        if (!isPlayingMove)
        {
            return;
        }

        isPlayingMove = false;
        bool stoppedActorPlayback = isPlayingActor;
        isPlayingActor = false;
        playbackElapsed = 0f;
        PlaybackKeyframeStatus = "Not playing";
        RenderContext.ForceContinuousRendering = false;
        if (playMoveButton is not null)
        {
            playMoveButton.Content = "Play";
        }
        if (playActorButton is not null)
        {
            playActorButton.Content = "Play Actors on Tracks";
        }
        if (stoppedActorPlayback)
        {
            DialoguePreviewSoundpanel.StopPlaying();
            FaceOnlyVoSoundpanel.StopPlaying();
            if (activeFaceOnlyVoEvent is not null)
            {
                EndActiveFaceOnlyVo();
            }
            else
            {
                dialoguePreviewAudioStarted = false;
            }
            RestorePlaybackActorOrigins();
        }
        playbackActors.Clear();
        if (previewActorWidgetActive && selectedPreviewActor is not null)
        {
            previewActorWidgetTarget.SetTransform(selectedPreviewActor.Origin);
        }
        RenderContext.TransformWidget.Attach = CameraFramingMode
            ? null
            : previewActorWidgetActive ? previewActorWidgetTarget : SelectedKeyframe;
        if (restoreStatus)
        {
            SceneStatus = $"{ActiveModel.Keyframes.Count} trajectory keyframe(s); {levelPaths.Count} level backdrop file(s).";
        }
        SceneViewer?.MarkRenderDirty();
    }

    private void RestorePlaybackActorOrigins()
    {
        foreach (PreviewActorPlaybackState state in playbackActors)
        {
            state.Actor.Origin = state.OriginalOrigin;
            ApplyAssignedGestureToActor(state.Actor);
            if (ReferenceEquals(selectedPreviewActor, state.Actor))
            {
                updatingPreviewActorControls = true;
                SetPreviewActorOriginFields(state.OriginalOrigin);
                UpdatePreviewActorRotationDialIndicator();
                updatingPreviewActorControls = false;
            }
        }
    }

    private void UpdatePlaybackButton()
    {
        if (playMoveButton is not null)
        {
            playMoveButton.IsEnabled = ActiveModel.Keyframes.Count > 0;
            if (!isPlayingMove)
            {
                playMoveButton.Content = "Play";
            }
        }
        if (playActorButton is not null)
        {
            playActorButton.IsEnabled = previewActors.Any(actor =>
                previewActorTrackAssignments.TryGetValue(actor, out TrackMovePlaybackOption trackMove)
                && trackMove.Model?.Keyframes.Count > 0);
            if (!isPlayingMove)
            {
                playActorButton.Content = "Play Actors on Tracks";
            }
        }
    }

    private void ExtraTrackMove_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingMulticamControls)
        {
            return;
        }

        selectedExtraTrackMove = ExtraTrackMoveComboBox.SelectedItem as TrackMovePlaybackOption ?? TrackMovePlaybackOption.None;
        playExtraTrackMove = ExtraTrackMoveCheckBox.IsChecked == true && selectedExtraTrackMove.TrackMove is not null;
        RefreshKeyframeTrackMoveTabs();
        SceneViewer?.MarkRenderDirty();
    }

    private void CharacterTrackMoveTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingKeyframeTrackTabs && SelectedPreviewEditorCategory == "Characters")
        {
            PauseDialogueTimelineForTrackEditing();
            ActivateSelectedTrackMove();
        }
    }

    private void CameraTrackMoveTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingKeyframeTrackTabs && SelectedPreviewEditorCategory == "Cameras")
        {
            PauseDialogueTimelineForTrackEditing();
            ActivateSelectedTrackMove();
        }
    }

    private void PreviewEditorCategoryTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActorEditorTabContent is null)
        {
            return;
        }

        string category = SelectedPreviewEditorCategory;
        ActorEditorTabContent.Visibility = category == "Actors" ? Visibility.Visible : Visibility.Collapsed;
        CharacterMovementTabContent.Visibility = category == "Characters"
            ? Visibility.Visible
            : Visibility.Collapsed;
        CameraMovementTabContent.Visibility = category == "Cameras"
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (category is "Characters" or "Cameras")
        {
            PauseDialogueTimelineForTrackEditing();
        }
        ActivateSelectedTrackMove();
        if (category == "Actors")
        {
            SelectPreviewActor(PreviewActorListBox.SelectedIndex);
        }
    }

    private void SelectedTrackKeyVisibility_Changed(object sender, RoutedEventArgs e)
    {
        if (updatingTrackKeyVisibilityControls || ActiveTrackMoveOption?.TrackMove is not { } trackMove)
        {
            return;
        }

        bool enabled = sender switch
        {
            CheckBox checkBox => checkBox.IsChecked == true,
            _ => false,
        };
        string key = GetTrackMoveEditingKey(trackMove);
        if (enabled)
        {
            tracksWithVisibleKeys.Add(key);
        }
        else
        {
            tracksWithVisibleKeys.Remove(key);
        }
        SceneViewer?.MarkRenderDirty();
    }

    private void PauseDialogueTimelineForTrackEditing()
    {
        if (ShouldPauseDialogueTimelineForTrackEditing(isPlayingDialogueTimeline,
                suppressDialogueCacheEditTracking))
        {
            PauseDialogueTimeline();
        }
    }

    internal static bool ShouldPauseDialogueTimelineForTrackEditing(bool isPlayingDialogueTimeline,
        bool suppressDialogueCacheEditTracking) =>
        isPlayingDialogueTimeline && !suppressDialogueCacheEditTracking;

    private static string GetTrackMoveEditingKey(ExportEntry trackMove) => trackMove is null
        ? string.Empty
        : $"{trackMove.FileRef?.FilePath}|{trackMove.UIndex}";

    private void SynchronizeTrackKeyframeEditingControls()
    {
        updatingTrackKeyVisibilityControls = true;
        try
        {
            if (CharacterTrackKeyVisibilityCheckBox is not null)
            {
                TrackMovePlaybackOption character =
                    CharacterTrackMoveTabs.SelectedItem as TrackMovePlaybackOption;
                CharacterTrackKeyVisibilityCheckBox.IsEnabled = character?.TrackMove is not null;
                CharacterTrackKeyVisibilityCheckBox.IsChecked = character?.TrackMove is { } characterTrack
                    && tracksWithVisibleKeys.Contains(GetTrackMoveEditingKey(characterTrack));
            }
            if (CameraTrackKeyVisibilityCheckBox is not null)
            {
                TrackMovePlaybackOption camera = CameraTrackMoveTabs.SelectedItem as TrackMovePlaybackOption;
                CameraTrackKeyVisibilityCheckBox.IsEnabled = camera?.TrackMove is not null;
                CameraTrackKeyVisibilityCheckBox.IsChecked = camera?.TrackMove is { } cameraTrack
                    && tracksWithVisibleKeys.Contains(GetTrackMoveEditingKey(cameraTrack));
            }
        }
        finally
        {
            updatingTrackKeyVisibilityControls = false;
        }
    }

    private void ActivateSelectedTrackMove()
    {
        StopPlayback(false);
        UnregisterKeyframes();
        SynchronizeTrackKeyframeEditingControls();
        KeyframeEditorPanel.Visibility = IsTrackEditorCategorySelected && ActiveTrackMoveOption?.TrackMove is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        CameraPlaybackPanel.Visibility = SelectedPreviewEditorCategory == "Cameras"
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!IsTrackEditorCategorySelected || ActiveTrackMoveOption?.TrackMove is null)
        {
            KeyframeList.ItemsSource = null;
            FovKeyframeList.ItemsSource = null;
            FovTrackPanel.Visibility = Visibility.Collapsed;
            SelectedFovKeyframe = null;
            SelectedKeyframe = null;
            RenderContext.TransformWidget.Attach = null;
            SceneViewer?.MarkRenderDirty();
            return;
        }

        trajectorySamplesDirty = true;
        foreach (CurveEditor3DKeyframe keyframe in ActiveModel.Keyframes)
        {
            keyframe.SetCoordinateBasis(ActiveTrackCoordinateBasis);
        }
        KeyframeList.ItemsSource = ActiveModel.Keyframes;
        FovKeyframeList.ItemsSource = ActiveFovModel?.Keyframes;
        FovTrackPanel.Visibility = SelectedPreviewEditorCategory == "Cameras" && ActiveFovModel is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (ActiveFovModel?.Export is { } fovExport)
        {
            string title = fovExport.GetProperty<StrProperty>("TrackTitle")?.Value ?? fovExport.ObjectName.Instanced;
            FovTrackNameTextBlock.Text = $"{fovExport.UIndex}: {title}";
            FovTrackNameTextBlock.ToolTip = fovExport.InstancedFullPath;
        }
        RegisterKeyframes();
        SelectedFovKeyframe = null;
        SelectedKeyframe = ActiveModel.Keyframes.FirstOrDefault();
        UpdatePlaybackButton();
        SceneStatus = $"{ActiveModel.Keyframes.Count} trajectory keyframe(s); {levelPaths.Count} level backdrop file(s).";
        SceneViewer?.MarkRenderDirty();
    }

    private void DirectorTrack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingMulticamControls)
        {
            return;
        }

        selectedDirectorPlayback = DirectorTrackComboBox.SelectedItem as DirectorPlaybackOption ?? DirectorPlaybackOption.None;
        playDirectorMulticam = DirectorMulticamCheckBox.IsChecked == true && selectedDirectorPlayback.Cuts.Count > 0;
        RefreshKeyframeTrackMoveTabs();
        SceneViewer?.MarkRenderDirty();
    }

    private void MulticamPlaybackOption_Changed(object sender, RoutedEventArgs e)
    {
        if (updatingMulticamControls)
        {
            return;
        }

        playExtraTrackMove = ExtraTrackMoveCheckBox.IsChecked == true && selectedExtraTrackMove?.TrackMove is not null;
        playDirectorMulticam = DirectorMulticamCheckBox.IsChecked == true && selectedDirectorPlayback?.Cuts.Count > 0;
        SceneViewer?.MarkRenderDirty();
    }

    private void Model_Changed()
    {
        if (isDialogueConversationPreview && !suppressDialogueCacheEditTracking)
        {
            PauseDialogueTimeline();
            UpdateDialogueNodeCommitButton();
        }
        StopPlayback();
        suppressTrackVisualizationForCameraPreview = false;
        UpdatePlaybackButton();
        trajectorySamplesDirty = true;
        RefreshKeyframePanel();
        if (CameraFramingMode && SelectedKeyframe is not null)
        {
            PreviewSelectedCameraTrackValue();
        }
        SceneViewer?.MarkRenderDirty();
    }

    private void FovModel_Changed()
    {
        if (isDialogueConversationPreview && !suppressDialogueCacheEditTracking)
        {
            PauseDialogueTimeline();
            UpdateDialogueNodeCommitButton();
        }
        StopPlayback();
        suppressTrackVisualizationForCameraPreview = false;
        if (CameraFramingMode && SelectedFovKeyframe is not null)
        {
            PreviewSelectedCameraTrackValue();
        }
        RefreshFovKeyframePanel();
        SceneViewer?.MarkRenderDirty();
    }

    private void RenderScene(object sender, EventArgs e)
    {
        foreach (RenderPass pass in RenderPasses)
        {
            foreach (ActorProxy actor in RenderContext.DrawList_3D)
            {
                if (actor is EmitterActorProxy) continue;
                if (actor is SFXPointOfInterestProxy) continue;
                if (actor.IsVolume && !ShowVolumes) continue;
                if (actor.IsVolumetricMesh && !ShowVolumetrics) continue;
                int hitId = actor.HitID;
                RenderContext.CurrentHitTestId = new Vector3((hitId & 0xFF) / 255f, ((hitId >> 8) & 0xFF) / 255f, ((hitId >> 16) & 0xFF) / 255f);
                actor.Render(RenderContext, pass);
            }
        }

        bool trackPlaybackActive = isPlayingMove || isPlayingActor || isPlayingDialogueTimeline;
        if (ShouldDrawTrackVisualization(trackPlaybackActive, CameraFramingMode,
                suppressTrackVisualizationForCameraPreview, AreActiveTrackKeysVisible))
        {
            DrawTrajectory(ActiveModel, AreActiveTrackKeysVisible);
            if (!trackPlaybackActive && ShowFovIcons)
            {
                DrawFovKeyframes(ActiveTrackMoveOption);
            }
        }
        RenderPreviewActors();
        RenderContext.DrawUI();
    }

    internal static bool ShouldDrawTrackVisualization(bool trackPlaybackActive, bool cameraFramingMode,
        bool suppressForCameraPreview, bool showSelectedTrackKeys) =>
        !cameraFramingMode
        && !suppressForCameraPreview
        && (!trackPlaybackActive || showSelectedTrackKeys);

    private void RenderPreviewActors()
    {
        bool previousCameraRelative = RenderContext.UseCameraRelativeNativeRendering;
        RenderContext.UseCameraRelativeNativeRendering = true;
        try
        {
            int actorCount = Math.Min(previewActorModels.Count, previewActors.Count);
            for (int actorIndex = 0; actorIndex < actorCount; actorIndex++)
            {
                ActorModelSet actorModels = previewActorModels[actorIndex];
                if (actorModels is null)
                {
                    continue;
                }
                UpdatePreviewActorSkinning(previewActors[actorIndex]);
                foreach (ActorModelSet.Component component in actorModels.Components)
                {
                    ModelPreview<LEVertex> actorModel = component.Model;
                    actorModel.UpdateLocalToWorld(CreatePreviewActorTransform(previewActors[actorIndex].Origin,
                        component.LocalTransform));
                    actorModel.Render(RenderPass.Base, RenderContext, 0);
                    actorModel.Render(RenderPass.Hair, RenderContext, 0);
                }
            }
        }
        finally
        {
            RenderContext.UseCameraRelativeNativeRendering = previousCameraRelative;
        }
    }

    private static Matrix4x4 CreatePreviewActorTransform(CameraOrigin transform)
    {
        return Matrix4x4.CreateTranslation(0, 0, PreviewBodyMeshRelativeZ)
               * Rotator.FromDegreesVector(transform.Rotation).ToRotationMatrix()
               * Matrix4x4.CreateTranslation(transform.Location);
    }

    private static Matrix4x4 CreatePreviewActorTransform(CameraOrigin transform,
        Matrix4x4? componentLocalTransform)
    {
        return (componentLocalTransform ?? Matrix4x4.CreateTranslation(0, 0, PreviewBodyMeshRelativeZ))
               * Rotator.FromDegreesVector(transform.Rotation).ToRotationMatrix()
               * Matrix4x4.CreateTranslation(transform.Location);
    }

    private void DrawTrajectory(CurveEditor3DModel activeModel, bool drawKeyframes)
    {
        IReadOnlyList<Vector3> samples = GetTrajectorySamples(activeModel);
        CameraOrigin? coordinateBasis = ActiveTrackCoordinateBasis;
        Vector3 ToDisplayLocation(Vector3 location) => coordinateBasis is { } basis
            ? InterpTrackMoveTransform.ToWorld(basis, new CameraOrigin(location, Vector3.Zero)).Location
            : location;
        Vector4 pathColor = new(1f, 0.65f, 0.05f, 1f);
        for (int i = 1; i < samples.Count; i++)
        {
            RenderContext.Primitives.AddLine(ToDisplayLocation(samples[i - 1]), ToDisplayLocation(samples[i]),
                pathColor, 0);
        }

        Vector4 connectorColor = new(1f, 0.85f, 0.2f, 1f);
        for (int i = 1; i < activeModel.Keyframes.Count; i++)
        {
            RenderContext.Primitives.AddLine(activeModel.Keyframes[i - 1].DisplayOrigin.Location,
                activeModel.Keyframes[i].DisplayOrigin.Location, connectorColor, 0);
        }

        if (!drawKeyframes)
        {
            return;
        }
        foreach (CurveEditor3DKeyframe keyframe in activeModel.Keyframes)
        {
            DrawKeyframe(keyframe);
        }
    }

    private IReadOnlyList<Vector3> GetTrajectorySamples(CurveEditor3DModel activeModel)
    {
        if (trajectorySamplesDirty)
        {
            trajectorySamples = activeModel.SampleTrajectory();
            trajectorySamplesDirty = false;
        }

        return trajectorySamples;
    }

    private void DrawKeyframe(CurveEditor3DKeyframe keyframe)
    {
        const float cubeHalfSize = 22f;
        const float axisLength = 55f;
        CameraOrigin displayOrigin = keyframe.DisplayOrigin;
        Vector3 position = displayOrigin.Location;
        Vector4 markerColor = keyframe == SelectedKeyframe ? new Vector4(1f, 1f, 1f, 1f) : new Vector4(1f, 0.8f, 0.1f, 1f);
        Quaternion orientation = Rotator.FromDegreesVector(displayOrigin.Rotation).ToQuaternion();
        Matrix4x4 cubeTransform = Matrix4x4.CreateFromQuaternion(orientation) * Matrix4x4.CreateTranslation(position);
        var cube = RenderContext.Primitives.BuildMesh(markerColor, keyframe.HitID, cubeTransform);
        cube.AddVertex(-cubeHalfSize, -cubeHalfSize, -cubeHalfSize);
        cube.AddVertex(cubeHalfSize, -cubeHalfSize, -cubeHalfSize);
        cube.AddVertex(cubeHalfSize, cubeHalfSize, -cubeHalfSize);
        cube.AddVertex(-cubeHalfSize, cubeHalfSize, -cubeHalfSize);
        cube.AddVertex(-cubeHalfSize, -cubeHalfSize, cubeHalfSize);
        cube.AddVertex(cubeHalfSize, -cubeHalfSize, cubeHalfSize);
        cube.AddVertex(cubeHalfSize, cubeHalfSize, cubeHalfSize);
        cube.AddVertex(-cubeHalfSize, cubeHalfSize, cubeHalfSize);
        cube.AddTriangle(0, 2, 1);
        cube.AddTriangle(0, 3, 2);
        cube.AddTriangle(4, 5, 6);
        cube.AddTriangle(4, 6, 7);
        cube.AddTriangle(0, 1, 5);
        cube.AddTriangle(0, 5, 4);
        cube.AddTriangle(1, 2, 6);
        cube.AddTriangle(1, 6, 5);
        cube.AddTriangle(2, 3, 7);
        cube.AddTriangle(2, 7, 6);
        cube.AddTriangle(3, 0, 4);
        cube.AddTriangle(3, 4, 7);

        Vector3 forward = Vector3.Transform(Vector3.UnitX, orientation);
        Vector3 right = Vector3.Transform(Vector3.UnitY, orientation);
        Vector3 up = Vector3.Transform(Vector3.UnitZ, orientation);
        RenderContext.Primitives.AddLine(position, position + forward * axisLength, new Vector4(1f, 0.15f, 0.15f, 1f), keyframe.HitID);
        RenderContext.Primitives.AddLine(position, position + right * axisLength, new Vector4(0.15f, 1f, 0.15f, 1f), keyframe.HitID);
        RenderContext.Primitives.AddLine(position, position + up * axisLength, new Vector4(0.2f, 0.45f, 1f, 1f), keyframe.HitID);
    }

    private void DrawFovKeyframes(TrackMovePlaybackOption trackMove)
    {
        if (trackMove?.FovModel?.Keyframes is not { Count: > 0 } fovKeyframes
            || trackMove.Model is null)
        {
            return;
        }

        foreach (CurveEditor3DFovKeyframe fovKeyframe in fovKeyframes)
        {
            CameraOrigin origin = ResolveCameraTrackOrigin(trackMove,
                EvaluateTrackMove(trackMove, fovKeyframe.Time));
            DrawFovKeyframe(fovKeyframe, origin);
        }
    }

    private void DrawFovKeyframe(CurveEditor3DFovKeyframe keyframe, CameraOrigin origin)
    {
        const float markerRadius = 13f;
        const float frustumDepth = 55f;
        const float degreesToRadians = 0.017453292519943295f;
        Vector4 color = keyframe == SelectedFovKeyframe
            ? new Vector4(1f, 1f, 1f, 1f)
            : new Vector4(0.1f, 0.85f, 1f, 1f);
        Quaternion orientation = Rotator.FromDegreesVector(origin.Rotation).ToQuaternion();
        Vector3 position = origin.Location;
        Vector3 forward = Vector3.Transform(Vector3.UnitX, orientation);
        Vector3 right = Vector3.Transform(Vector3.UnitY, orientation);
        Vector3 up = Vector3.Transform(Vector3.UnitZ, orientation);

        var marker = RenderContext.Primitives.BuildMesh(color, keyframe.HitID,
            Matrix4x4.CreateTranslation(position));
        marker.AddVertex(markerRadius, 0, 0);
        marker.AddVertex(-markerRadius, 0, 0);
        marker.AddVertex(0, markerRadius, 0);
        marker.AddVertex(0, -markerRadius, 0);
        marker.AddVertex(0, 0, markerRadius);
        marker.AddVertex(0, 0, -markerRadius);
        marker.AddTriangle(0, 2, 4);
        marker.AddTriangle(0, 4, 3);
        marker.AddTriangle(0, 3, 5);
        marker.AddTriangle(0, 5, 2);
        marker.AddTriangle(1, 4, 2);
        marker.AddTriangle(1, 3, 4);
        marker.AddTriangle(1, 5, 3);
        marker.AddTriangle(1, 2, 5);

        float clampedFov = Math.Clamp(keyframe.Value, 1f, 179f);
        float halfHeight = Math.Clamp(MathF.Tan(clampedFov * degreesToRadians * 0.5f) * 18f, 8f, 70f);
        float halfWidth = halfHeight * 16f / 9f;
        Vector3 center = position + forward * frustumDepth;
        Vector3 topRight = center + right * halfWidth + up * halfHeight;
        Vector3 topLeft = center - right * halfWidth + up * halfHeight;
        Vector3 bottomRight = center + right * halfWidth - up * halfHeight;
        Vector3 bottomLeft = center - right * halfWidth - up * halfHeight;
        RenderContext.Primitives.AddLine(position, topRight, color, keyframe.HitID);
        RenderContext.Primitives.AddLine(position, topLeft, color, keyframe.HitID);
        RenderContext.Primitives.AddLine(position, bottomRight, color, keyframe.HitID);
        RenderContext.Primitives.AddLine(position, bottomLeft, color, keyframe.HitID);
        RenderContext.Primitives.AddLine(topLeft, topRight, color, keyframe.HitID);
        RenderContext.Primitives.AddLine(topRight, bottomRight, color, keyframe.HitID);
        RenderContext.Primitives.AddLine(bottomRight, bottomLeft, color, keyframe.HitID);
        RenderContext.Primitives.AddLine(bottomLeft, topLeft, color, keyframe.HitID);
    }

    private void RegisterKeyframes()
    {
        registeredKeyframeModel = ActiveModel;
        foreach (CurveEditor3DKeyframe keyframe in registeredKeyframeModel.Keyframes)
        {
            RenderContext.AddHitProxy(keyframe);
        }
        registeredFovModel = ActiveFovModel;
        if (registeredFovModel is not null)
        {
            foreach (CurveEditor3DFovKeyframe keyframe in registeredFovModel.Keyframes)
            {
                RenderContext.AddHitProxy(keyframe);
            }
        }
    }

    private void UnregisterKeyframes()
    {
        if (registeredKeyframeModel is null && registeredFovModel is null)
        {
            return;
        }

        if (registeredKeyframeModel is not null)
        {
            foreach (CurveEditor3DKeyframe keyframe in registeredKeyframeModel.Keyframes)
            {
                RenderContext.RemoveHitProxy(keyframe);
            }
        }
        if (registeredFovModel is not null)
        {
            foreach (CurveEditor3DFovKeyframe keyframe in registeredFovModel.Keyframes)
            {
                RenderContext.RemoveHitProxy(keyframe);
            }
        }
        registeredKeyframeModel = null;
        registeredFovModel = null;
    }

    private async void OpenLevel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = AppDirectories.GetOpenPackageDialog();
        if (DirectoryMemory.ShowDialog(dialog) == true)
        {
            await LoadLevelAsync(dialog.FileName, replace: true).ConfigureAwait(true);
        }
    }

    private async void AddLevel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = AppDirectories.GetOpenPackageDialog();
        if (DirectoryMemory.ShowDialog(dialog) == true)
        {
            await LoadLevelAsync(dialog.FileName, replace: false).ConfigureAwait(true);
        }
    }

    private void UnloadLevel_Click(object sender, RoutedEventArgs e)
    {
        CloseLevels();
        UpdateSessionLevelPaths();
        SceneStatus = $"{ActiveModel.Keyframes.Count} trajectory keyframe(s); {levelPaths.Count} level backdrop file(s).";
    }

    private void RecentLevelsButton_Click(object sender, RoutedEventArgs e)
    {
        RecentLevelsMenu.PlacementTarget = RecentLevelsButton;
        RecentLevelsMenu.IsOpen = true;
    }

    private void RecentLevelsMenu_Opened(object sender, RoutedEventArgs e)
    {
        RecentLevelsMenu.Items.Clear();
        List<RecentFileSet> recentSets = LoadRecentSets();
        if (recentSets.Count == 0)
        {
            RecentLevelsMenu.Items.Add(new MenuItem { Header = "No recent levels", IsEnabled = false });
            return;
        }

        foreach (RecentFileSet set in recentSets)
        {
            var item = new MenuItem { Header = set.DisplayName.Replace("_", "__"), ToolTip = set.TooltipText, Tag = set };
            item.Click += RecentLevel_Click;
            RecentLevelsMenu.Items.Add(item);
        }
    }

    private async void RecentLevel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: RecentFileSet set })
        {
            return;
        }

        List<string> existingPaths = set.FilePaths.Where(File.Exists).ToList();
        if (existingPaths.Count == 0)
        {
            MessageBox.Show("None of the recent level files exist anymore.");
            return;
        }

        for (int i = 0; i < existingPaths.Count; i++)
        {
            await LoadLevelAsync(existingPaths[i], replace: i == 0).ConfigureAwait(true);
        }
    }

    private async Task LoadLevelAsync(string path, bool replace, bool updateSession = true)
    {
        try
        {
            if (replace)
            {
                CloseLevels();
                if (updateSession)
                {
                    UpdateSessionLevelPaths();
                }
            }

            path = Path.GetFullPath(path);
            if (levelPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            SceneStatus = $"Loading {Path.GetFileName(path)}...";
            await Task.Delay(1).ConfigureAwait(true);
            IMEPackage package = MEPackageHandler.OpenMEPackage(path);
            ExportEntry levelExport = package.Exports.FirstOrDefault(export => export.ClassName == "Level");
            if (levelExport is null)
            {
                package.Dispose();
                MessageBox.Show($"{Path.GetFileName(path)} is not a level file.");
                return;
            }

            Level level = levelExport.GetBinaryData<Level>();
            List<ActorProxy> actors = LoadActors(level);
            levelPackages.Add(package);
            levelPaths.Add(path);
            levelActors.AddRange(actors);
            RenderContext.LoadActors(actors);
            if (updateSession)
            {
                UpdateSessionLevelPaths();
            }
            RecordRecentSet();
            SceneStatus = $"{ActiveModel.Keyframes.Count} trajectory keyframe(s); {levelPaths.Count} level backdrop file(s).";
            SceneViewer.SetShouldRender(true);
            SceneViewer.MarkRenderDirty();
        }
        catch (Exception exception)
        {
            SceneStatus = $"Failed to load {Path.GetFileName(path)}.";
            MessageBox.Show($"Unable to open level file:\n{exception.Message}");
        }
    }

    private List<ActorProxy> LoadActors(Level level)
    {
        var actors = new List<ActorProxy>();
        IEnumerable<ExportEntry> actorExports = level.Actors.Where(level.Export.FileRef.IsUExport).Select(level.Export.FileRef.GetUExport);
        foreach (ExportEntry actorExport in actorExports)
        {
            if (actorExport.ClassName == "StaticMeshCollectionActor")
            {
                var collection = actorExport.GetBinaryData<StaticMeshCollectionActor>();
                for (int index = 0; index < collection.Components.Count; index++)
                {
                    if (level.Export.FileRef.TryGetUExport(collection.Components[index], out ExportEntry component))
                    {
                        actors.Add(new StaticMeshComponentActorProxy(this, component, collection, index));
                    }
                }
            }
            else if (actorExport.ClassName == "StaticLightCollectionActor")
            {
                var collection = actorExport.GetBinaryData<StaticLightCollectionActor>();
                for (int index = 0; index < collection.Components.Count; index++)
                {
                    if (!level.Export.FileRef.TryGetUExport(collection.Components[index], out ExportEntry lightExport))
                    {
                        continue;
                    }

                    ActorProxy light = GlobalUnrealObjectInfo.IsA(lightExport.ClassName, "SpotLightComponent", lightExport.Game)
                        ? new SpotLightComponentActorProxy(this, lightExport, collection, index)
                        : GlobalUnrealObjectInfo.IsA(lightExport.ClassName, "DirectionalLightComponent", lightExport.Game)
                            ? new DirectionalLightComponentActorProxy(this, lightExport, collection, index)
                            : new PointLightComponentActorProxy(this, lightExport, collection, index);
                    actors.Add(light);
                }
            }
            else if (ActorProxy.Create(this, actorExport) is { } actor)
            {
                actors.Add(actor);
            }
        }

        foreach (ActorProxy actor in actors)
        {
            actor.ResolveAttachment(actors);
        }

        return actors.OrderBy(actor => actor.Export.UIndex).ToList();
    }

    private void CloseLevels()
    {
        RenderContext.UnloadLevel();
        RenderContext.EnableTransformWidget();
        levelActors.Clear();
        foreach (IMEPackage package in levelPackages)
        {
            package.Dispose();
        }
        levelPackages.Clear();
        levelPaths.Clear();
        dialoguePlacedCameras.Clear();
        dialogueAuthoredCameraDefaults.Clear();
        dialogueLookAtTargets.Clear();

        foreach (CurveEditor3DKeyframe keyframe in ActiveModel.Keyframes)
        {
            keyframe.HitID = 0;
        }
        if (ActiveFovModel is not null)
        {
            foreach (CurveEditor3DFovKeyframe keyframe in ActiveFovModel.Keyframes)
            {
                keyframe.HitID = 0;
            }
        }
        RegisterKeyframes();
        SceneViewer?.MarkRenderDirty();
    }

    private void UpdateSessionLevelPaths()
    {
        lock (sessionLevelPathsLock)
        {
            sessionLevelPaths.Clear();
            sessionLevelPaths.AddRange(levelPaths);
            if (sessionLevelPaths.Count == 0)
            {
                TrackSessionSourcePackage(null);
            }
            else if (CurrentLoadedExport?.FileRef is { } package)
            {
                TrackSessionSourcePackage(package);
            }
        }
    }

    private static void TrackSessionSourcePackage(IMEPackage package)
    {
        if (ReferenceEquals(sessionSourcePackage, package))
        {
            return;
        }

        if (sessionSourcePackage is not null)
        {
            sessionSourcePackage.NoLongerOpenInTools -= SessionSourcePackage_NoLongerOpenInTools;
        }

        sessionSourcePackage = package;
        if (sessionSourcePackage is not null)
        {
            sessionSourcePackage.NoLongerOpenInTools += SessionSourcePackage_NoLongerOpenInTools;
        }
    }

    private static void SessionSourcePackage_NoLongerOpenInTools(UnrealPackageFile sender)
    {
        lock (sessionLevelPathsLock)
        {
            if (!ReferenceEquals(sessionSourcePackage, sender))
            {
                return;
            }

            IMEPackage replacement = MEPackageHandler.PackagesInTools.FirstOrDefault(package =>
                package.Users.Count > 0
                && string.Equals(package.FilePath, sender.FilePath, StringComparison.OrdinalIgnoreCase));
            if (replacement is not null)
            {
                TrackSessionSourcePackage(replacement);
                return;
            }

            TrackSessionSourcePackage(null);
            sessionLevelPaths.Clear();
        }
    }

    private static string RecentSetsFile => Path.Combine(
        Directory.CreateDirectory(Path.Combine(AppDirectories.AppDataFolder, "LevelEditor")).FullName,
        "RECENTSETS");

    private static List<RecentFileSet> LoadRecentSets()
    {
        if (!File.Exists(RecentSetsFile))
        {
            return [];
        }

        try
        {
            List<RecentFileSet> sets = JsonConvert.DeserializeObject<List<RecentFileSet>>(File.ReadAllText(RecentSetsFile)) ?? [];
            foreach (RecentFileSet set in sets)
            {
                set.FilePaths.RemoveAll(path => !File.Exists(path));
                set.ReadOnlyFilePaths.RemoveAll(path => !File.Exists(path));
            }
            return sets.Where(set => set.FilePaths.Count > 0).ToList();
        }
        catch
        {
            return [];
        }
    }

    private void RecordRecentSet()
    {
        if (levelPaths.Count == 0)
        {
            return;
        }

        List<RecentFileSet> sets = LoadRecentSets();
        sets.RemoveAll(set => set.FilePaths.Count > 0 && set.FilePaths[0].Equals(levelPaths[0], StringComparison.OrdinalIgnoreCase));
        sets.Insert(0, new RecentFileSet
        {
            Game = levelPackages[0].Game,
            FilePaths = [.. levelPaths],
            ReadOnlyFilePaths = []
        });
        if (sets.Count > 10)
        {
            sets.RemoveRange(10, sets.Count - 10);
        }
        File.WriteAllText(RecentSetsFile, JsonConvert.SerializeObject(sets, Formatting.Indented));
    }

    private void LoadPreviewActorModel(int actorIndex, PreviewActorModelComponent component, ExportEntry skeletalMeshExport)
        => LoadPreviewActorModel(actorIndex, component, skeletalMeshExport, null, null, false, null, null);

    private void LoadPreviewActorModel(int actorIndex, PreviewActorModelComponent component,
        ExportEntry skeletalMeshExport, IReadOnlyList<IEntry> materialOverrides, ExportEntry morphHead,
        bool useStoredMorphLods, string slotName, Matrix4x4? localTransform)
    {
        if (actorIndex < 0 || skeletalMeshExport is null)
        {
            return;
        }

        SkeletalMesh skeletalMesh = skeletalMeshExport.GetBinaryData<SkeletalMesh>();
        ModelPreview<LEVertex> modelPreview = new(RenderContext, skeletalMesh, materialOverrides,
            loadOnlyFirstLod: true);
        modelPreview.PrepareGraphicsResources(RenderContext);
        while (previewActorModels.Count <= actorIndex)
        {
            previewActorModels.Add(new ActorModelSet());
        }
        SkeletalMesh bodySkeleton = component is PreviewActorModelComponent.Body
            ? skeletalMesh
            : previewActorAnimationStates.GetValueOrDefault(previewActors[actorIndex])?.SkeletalMesh;
        var componentRenderer = new SkinnedMeshRenderer();
        componentRenderer.BuildFromSkeletalMesh(skeletalMeshExport.Game, skeletalMesh.LODModels[0],
            skeletalMesh.RefSkeleton, bodySkeleton?.RefSkeleton);
        if (morphHead is not null)
        {
            (LegendaryExplorerCore.Unreal.Classes.BonePosition[] bonePositions, Vector3[][] morphLods) =
                LegendaryExplorerCore.Unreal.Classes.BioMorphFace.GetBoneAndVertexPositions(morphHead);
            Vector3[] morphPositions = useStoredMorphLods && morphLods?.Length > 0 ? morphLods[0] : null;
            componentRenderer.ApplyMorph(skeletalMesh.RefSkeleton, bonePositions, morphPositions);
            ApplyPreviewMorphMaterialOverrides(modelPreview, morphHead);
        }
        previewActorModels[actorIndex].Set(component, modelPreview, componentRenderer, slotName, localTransform);
        if (component is PreviewActorModelComponent.Body && actorIndex < previewActors.Count && skeletalMesh.LODModels.Length > 0)
        {
            var animationState = new PreviewActorAnimationState
            {
                SkeletalMesh = skeletalMesh,
                Renderer = componentRenderer,
                Player = new PreviewActorAnimationState.LayeredAnimationPlayer(skeletalMesh),
            };
            previewActorAnimationStates[previewActors[actorIndex]] = animationState;
            ApplyAssignedGestureToActor(previewActors[actorIndex]);
        }
        SceneViewer.MarkRenderDirty();
    }

    private void ApplyPreviewMorphMaterialOverrides(ModelPreview<LEVertex> modelPreview, ExportEntry morphHead)
    {
        if (morphHead.GetProperty<ObjectProperty>("m_oMaterialOverrides") is not { Value: not 0 } reference
            || RenderContext.ResolveExportCached(morphHead.FileRef, reference.Value) is not { } materialOverride)
        {
            return;
        }

        List<MaterialRenderProxy> materials = modelPreview.Materials.Values
            .OfType<LEShaderPreviewMaterial>()
            .Select(material => material.RenderProxy)
            .Distinct()
            .ToList();
        PropertyCollection properties = materialOverride.GetProperties(packageCache: RenderContext.PackageCache);
        foreach (MaterialRenderProxy material in materials)
        {
            material.ResetPreviewParameterOverrides();
            foreach (StructProperty scalar in properties.GetProp<ArrayProperty<StructProperty>>("m_aScalarOverrides")
                         ?? Enumerable.Empty<StructProperty>())
            {
                string name = scalar.GetProp<NameProperty>("nName")?.Value.Instanced;
                if (!string.IsNullOrEmpty(name))
                {
                    material.SetScalarParameter(name, scalar.GetProp<FloatProperty>("sValue")?.Value ?? 0f);
                }
            }
            foreach (StructProperty color in properties.GetProp<ArrayProperty<StructProperty>>("m_aColorOverrides")
                         ?? Enumerable.Empty<StructProperty>())
            {
                string name = color.GetProp<NameProperty>("nName")?.Value.Instanced;
                if (!string.IsNullOrEmpty(name))
                {
                    LinearColor value = color.GetProp<StructProperty>("cValue") is { } linearColor
                        ? CommonStructs.GetLinearColor(linearColor)
                        : LinearColor.White;
                    material.SetVectorParameter(name, value);
                }
            }
        }

        foreach (StructProperty texture in properties.GetProp<ArrayProperty<StructProperty>>("m_aTextureOverrides")
                     ?? Enumerable.Empty<StructProperty>())
        {
            string name = texture.GetProp<NameProperty>("nName")?.Value.Instanced;
            IEntry textureEntry = texture.GetProp<ObjectProperty>("m_pTexture")?.ResolveToEntry(materialOverride.FileRef);
            ExportEntry textureExport = textureEntry switch
            {
                ExportEntry export when export.IsTexture() => export,
                ImportEntry import when RenderContext.ResolveExportCached(import) is { } resolved
                                        && resolved.IsTexture() => resolved,
                _ => null,
            };
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            PreviewTextureCache.TextureEntry cachedTexture = textureExport is not null
                ? RenderContext.TextureCache.LoadTexture(textureExport, RenderContext.PackageCache)
                : null;
            foreach (MaterialRenderProxy material in materials)
            {
                material.SetTextureParameter(name, textureExport?.InstancedFullPath, cachedTexture);
            }
        }
    }

    private void RemovePreviewActorModel(int actorIndex)
    {
        if (actorIndex < 0 || actorIndex >= previewActorModels.Count)
        {
            return;
        }
        previewActorModels[actorIndex]?.Dispose();
        previewActorModels.RemoveAt(actorIndex);
        SceneViewer.MarkRenderDirty();
    }

    private void ClearPreviewActorModels()
    {
        foreach (ActorModelSet actorModel in previewActorModels)
        {
            actorModel?.Dispose();
        }
        previewActorModels.Clear();
        previewActorAnimationStates.Clear();
        SceneViewer?.MarkRenderDirty();
    }

    private void PreviewActorListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedPreviewActor = PreviewActorListBox.SelectedItem as PreviewActorConfiguration;
        if (!ReferenceEquals(DialoguePreviewActorListBox.SelectedItem, selectedPreviewActor))
        {
            DialoguePreviewActorListBox.SelectedItem = selectedPreviewActor;
        }
        SynchronizePreviewActorControls();
        UpdatePreviewActorTrackAssignmentControls();
        SelectPreviewActor(PreviewActorListBox.SelectedIndex);
        UpdatePlaybackButton();
    }

    private void PreviewActorListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        FocusPreviewActor(PreviewActorListBox.SelectedIndex);
    }

    private void SelectPreviewActor(int actorIndex)
    {
        if (actorIndex < 0 || actorIndex >= previewActors.Count)
        {
            previewActorWidgetActive = false;
            RenderContext.TransformWidget.Attach = CameraFramingMode ? null : SelectedKeyframe;
            SceneViewer.MarkRenderDirty();
            return;
        }
        previewActorWidgetTarget.SetTransform(previewActors[actorIndex].Origin);
        previewActorWidgetActive = true;
        RenderContext.TransformWidget.Attach = isPlayingMove || CameraFramingMode ? null : previewActorWidgetTarget;
        SceneViewer.MarkRenderDirty();
    }

    private void FocusPreviewActor(int actorIndex)
    {
        if (actorIndex < 0 || actorIndex >= previewActorModels.Count || actorIndex >= previewActors.Count
            || previewActorModels[actorIndex]?.Body is not { LODs.Count: > 0 } actorModel)
        {
            return;
        }

        CameraOrigin transform = previewActors[actorIndex].Origin;
        BoxSphereBounds bounds = actorModel.LODs[0].Mesh.BaseBounds.TransformBy(CreatePreviewActorTransform(transform));
        float distance = MathF.Max(bounds.SphereRadius, 50) * 2;
        (float sin, float cos) = MathF.SinCos(MathF.PI / 2.5f);
        StopPlayback(false);
        RenderContext.Camera.Position = new Vector3(bounds.Origin.X, bounds.Origin.Y + sin * distance,
            bounds.Origin.Z + cos * distance);
        RenderContext.Camera.OrientTowards(bounds.Origin);
        RenderContext.Camera.FocusDepth = 0;
        UpdateCameraPositionText();
        UpdateCameraRotationText();
        SceneViewer.MarkRenderDirty();
    }

    private void SynchronizePreviewActorControls()
    {
        if (selectedPreviewActor is null)
        {
            return;
        }

        updatingPreviewActorControls = true;
        SetPreviewActorOriginFields(selectedPreviewActor.Origin);
        PreviewActorTextBox.Text = selectedPreviewActor.ModelName;
        PreviewActorHeadTextBox.Text = string.IsNullOrEmpty(selectedPreviewActor.HeadModelName) ? PreviewActorModelDefaults.NoneMeshName : selectedPreviewActor.HeadModelName;
        PreviewActorHairTextBox.Text = string.IsNullOrEmpty(selectedPreviewActor.HairModelName) ? PreviewActorModelDefaults.NoneMeshName : selectedPreviewActor.HairModelName;
        PreviewActorGestureComboBox.Items.Refresh();
        PreviewActorGestureComboBox.SelectedItem = previewActorGestureAssignments.GetValueOrDefault(selectedPreviewActor)
                                                ?? GestureTrackOption.None;
        UpdatePreviewActorGestureStatus();
        UpdatePreviewActorRotationDialIndicator();
        updatingPreviewActorControls = false;
    }

    private void DialoguePreviewActorListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(PreviewActorListBox.SelectedItem, DialoguePreviewActorListBox.SelectedItem))
        {
            PreviewActorListBox.SelectedItem = DialoguePreviewActorListBox.SelectedItem;
        }
    }

    private void StartDialoguePreviewAudio(float time)
    {
        PreviewActorConfiguration speakingActor = previewActors.FirstOrDefault(IsDialogueNodeSpeaker);
        if (speakingActor is null)
        {
            return;
        }

        if (!LoadDialoguePreviewAudio())
        {
            return;
        }
        DialoguePreviewSoundpanel.StopPlaying();
        float audioTime = Math.Max(0, time - dialogueNodePreview.VoStartTime);
        dialoguePreviewAudioStarted = DialoguePreviewSoundpanel.StartOrPausePlaying(audioTime);
    }

    private void UpdateDialoguePreviewAudio(float time)
    {
        if (dialogueNodePreview is null || dialoguePreviewAudioStarted || time < dialogueNodePreview.VoStartTime)
        {
            return;
        }

        if (faceOnlyVoAudioStarted && activeFaceOnlyVoEvent is { } faceOnlyVo
            && faceOnlyVo.Node.LineStrRef == dialogueNodePreview.Node.LineStrRef)
        {
            return;
        }

        StartDialoguePreviewAudio(time);
    }

    private bool LoadDialoguePreviewAudio()
    {
        ExportEntry audio = activeDialogueSegmentRuntime?.DialogueAudio
                            ?? GetDialogueNodeAudio(dialogueNodePreview?.Node);
        if (audio is null)
        {
            DialoguePreviewSoundpanel.UnloadExport();
            return false;
        }
        DialoguePreviewSoundpanel.LoadExport(audio);
        return true;
    }

    private void PreviewActorTrackMove_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPreviewActor is null || sender is not Button button)
        {
            return;
        }

        TrackMovePlaybackOption assignedTrack = previewActorTrackAssignments.GetValueOrDefault(selectedPreviewActor);
        var menu = new ContextMenu { PlacementTarget = button };
        var unassignedItem = new MenuItem
        {
            Header = "Unassigned",
            IsCheckable = true,
            IsChecked = assignedTrack is null,
            Tag = TrackMovePlaybackOption.None,
        };
        unassignedItem.Click += PreviewActorTrackMoveMenuItem_Click;
        menu.Items.Add(unassignedItem);
        menu.Items.Add(new Separator());
        ExportEntry[] cameraTrackMoves = dialoguePreviewCameraActors
            .Where(option => option?.TrackMove is not null)
            .Select(option => option.TrackMove)
            .ToArray();
        foreach (TrackMovePlaybackOption trackMove in availableTrackMoves.Where(option =>
                     dialogueNodePreview is null
                     || IsEligibleActorTrackMove(option.TrackMove, cameraTrackMoves)
                     && IsEligibleActorTrackGroup(option.Group, selectedPreviewActor.ActorTag)))
        {
            var item = new MenuItem
            {
                Header = trackMove.DisplayName.Replace("_", "__"),
                IsCheckable = true,
                IsChecked = IsSameExport(trackMove.TrackMove, assignedTrack?.TrackMove),
                Tag = trackMove,
            };
            item.Click += PreviewActorTrackMoveMenuItem_Click;
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void PreviewActorTrackMoveMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPreviewActor is null || sender is not MenuItem { Tag: TrackMovePlaybackOption trackMove })
        {
            return;
        }

        if (trackMove.TrackMove is null)
        {
            previewActorTrackAssignments.Remove(selectedPreviewActor);
            activeDialogueSegmentRuntime?.ActorTrackAssignments.Remove(selectedPreviewActor.ActorTag);
        }
        else if (dialogueNodePreview is null
                 || IsEligibleActorTrackGroup(trackMove.Group, selectedPreviewActor.ActorTag))
        {
            previewActorTrackAssignments[selectedPreviewActor] = trackMove;
            if (activeDialogueSegmentRuntime is not null)
            {
                activeDialogueSegmentRuntime.ActorTrackAssignments[selectedPreviewActor.ActorTag] = trackMove;
            }
        }

        MarkActiveDialogueNodePreviewChanged();
        if (playbackActors.FirstOrDefault(state => ReferenceEquals(state.Actor, selectedPreviewActor)) is { } playbackState)
        {
            playbackState.TrackMove = trackMove.TrackMove is null ? null : trackMove;
            playbackState.MoveFrame = GetTrackMoveFrame(playbackState.TrackMove);
        }
        UpdatePreviewActorTrackAssignmentControls();
        RefreshKeyframeTrackMoveTabs();
        UpdatePlaybackButton();
    }

    private void UpdatePreviewActorTrackAssignmentControls()
    {
        TrackMovePlaybackOption trackMove = selectedPreviewActor is null
            ? null
            : previewActorTrackAssignments.GetValueOrDefault(selectedPreviewActor);
        PreviewActorTrackMoveTextBlock.Text = trackMove?.DisplayName ?? "Unassigned";
        PreviewActorTrackMoveButton.IsEnabled = selectedPreviewActor is not null && availableTrackMoves.Count > 0;
    }

    private void PreviewActorGesture_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Activating a cached node clears and repopulates this ComboBox. That transient selection
        // change must not remove the selected actor's gesture from the node we are leaving.
        if (updatingPreviewActorControls || suppressDialogueCacheEditTracking || selectedPreviewActor is null)
        {
            return;
        }

        if (PreviewActorGestureComboBox.SelectedItem is GestureTrackOption { Track: not null } gesture)
        {
            previewActorGestureAssignments[selectedPreviewActor] = gesture;
            if (activeDialogueSegmentRuntime is not null)
            {
                activeDialogueSegmentRuntime.ActorGestureAssignments[selectedPreviewActor.ActorTag] = gesture;
            }
        }
        else
        {
            previewActorGestureAssignments.Remove(selectedPreviewActor);
            activeDialogueSegmentRuntime?.ActorGestureAssignments.Remove(selectedPreviewActor.ActorTag);
        }

        MarkActiveDialogueNodePreviewChanged();

        ApplyAssignedGestureToActor(selectedPreviewActor);
        UpdatePreviewActorGestureStatus();
        SceneViewer.MarkRenderDirty();
    }

    private void ApplyAssignedGestureToActor(PreviewActorConfiguration actor, float? playbackDuration = null,
        float? startingPoseTimeOverride = null)
    {
        if (!previewActorAnimationStates.TryGetValue(actor, out PreviewActorAnimationState animationState))
        {
            return;
        }

        if (!previewActorGestureAssignments.TryGetValue(actor, out GestureTrackOption gesture)
            || gesture.Timeline.Count == 0 && gesture.StartingPose is null)
        {
            animationState.Clear();
            UpdatePreviewActorSkinning(actor);
            return;
        }

        int movementKeyCount = previewActorTrackAssignments.TryGetValue(actor,
                                   out TrackMovePlaybackOption movementTrack)
            ? movementTrack?.Model?.Keyframes?.Count ?? 0
            : 0;
        animationState.SetTimeline(gesture.StartingPose, gesture.Timeline, previewActorGesturePackageCache,
            playbackDuration ?? activeDialogueSegmentRuntime?.Segment.Duration, gesture,
            maskDialogueOverlayStaticBones: isDialogueConversationPreview,
            // Keep authored locomotion on the skeletal Root during visible playback. Constant
            // TrackMove segments such as R3 deliberately pair a held actor anchor with a walking
            // gesture, then resynchronize the actor at the next key. Normalizing that Root here
            // freezes the walk and applying only the post-track delta moves it from the wrong
            // origin. Cache evaluation separately extracts the final persistent displacement.
            extractRootTranslation: ShouldExtractDialogueGestureRootTranslation(isDialogueConversationPreview,
                movementKeyCount, isCacheEvaluation: false),
            startingPoseTimeOverride: startingPoseTimeOverride);
        UpdatePreviewActorSkinning(actor);
    }

    internal static bool ShouldExtractDialogueGestureRootTranslation(bool isConversationPreview,
        int movementKeyCount, bool isCacheEvaluation = true)
    {
        // Only a multi-key TrackMove establishes an actor-space locomotion path. Its gesture root
        // may continue that motion after the final spline key. Without a TrackMove, keep motion on
        // the skeletal Root so it cannot displace the pawn inherited by the next dialogue node. A
        // one-key TrackMove is an authored anchor and likewise must not accumulate gesture motion.
        // Visible playback retains the Root on the skeleton because constant TrackMove segments
        // use that local translation to travel between their authored synchronization keys.
        return isConversationPreview && isCacheEvaluation && movementKeyCount > 1;
    }

    internal static CameraOrigin ApplyDialogueGestureRootMotion(CameraOrigin origin, Vector3 localTranslation)
    {
        if (localTranslation == Vector3.Zero)
        {
            return origin;
        }

        Matrix4x4 actorRotation = Rotator.FromDegreesVector(origin.Rotation).ToRotationMatrix();
        Vector3 worldTranslation = Vector3.TransformNormal(localTranslation, actorRotation);
        return new CameraOrigin(origin.Location + worldTranslation, origin.Rotation);
    }

    private void UpdatePreviewActorGestureStatus()
    {
        if (selectedPreviewActor is null)
        {
            SetPreviewActorStatus("Select or add an actor to assign a gesture.");
            return;
        }

        if (!previewActorGestureAssignments.TryGetValue(selectedPreviewActor, out GestureTrackOption gesture))
        {
            SetPreviewActorStatus(availableGestureTracks.Count > 1
                ? "No gesture assigned to this actor."
                : "No gesture tracks were found under this InterpData.");
            return;
        }

        SetPreviewActorStatus(gesture.Status);
    }

    private void UpdatePreviewActorSkinning(PreviewActorConfiguration actor)
    {
        int actorIndex = previewActors.IndexOf(actor);
        if (actorIndex < 0 || actorIndex >= previewActorModels.Count
            || !previewActorAnimationStates.TryGetValue(actor, out PreviewActorAnimationState animationState)
            || previewActorModels[actorIndex]?.Body is not { LODs.Count: > 0 } actorModel)
        {
            return;
        }

        if (animationState.Renderer.NeedsUpdate)
        {
            foreach (ActorModelSet.Component component in previewActorModels[actorIndex].Components)
            {
                if (component.Model is { LODs.Count: > 0 })
                {
                    component.Renderer.UpdateSkinning(RenderContext.ImmediateContext,
                        component.Model.LODs[0].Mesh, animationState.Player);
                }
            }
        }
    }

    private void PreviewActorTransform_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (updatingPreviewActorControls || selectedPreviewActor is null
            || !TryReadPreviewActorOrigin(out CameraOrigin origin))
        {
            return;
        }
        selectedPreviewActor.Origin = origin;
        RecordActiveDialogueActorOrigin(selectedPreviewActor, origin);
        previewActorWidgetTarget.SetTransform(origin);
        UpdatePreviewActorRotationDialIndicator();
        SavePreviewActorLayout();
        SceneViewer.MarkRenderDirty();
    }

    private bool TryReadPreviewActorOrigin(out CameraOrigin origin)
    {
        origin = default;
        if (!float.TryParse(PreviewActorXTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out float x)
            || !float.TryParse(PreviewActorYTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out float y)
            || !float.TryParse(PreviewActorZTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out float z)
            || !float.TryParse(PreviewActorRollTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out float roll)
            || !float.TryParse(PreviewActorPitchTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out float pitch)
            || !float.TryParse(PreviewActorYawTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out float yaw))
        {
            return false;
        }
        origin = new CameraOrigin(new Vector3(x, y, z), new Vector3(roll, pitch, yaw));
        return true;
    }

    private void SetPreviewActorOriginFields(CameraOrigin origin)
    {
        PreviewActorXTextBox.Text = origin.Location.X.ToString("0.###", CultureInfo.CurrentCulture);
        PreviewActorYTextBox.Text = origin.Location.Y.ToString("0.###", CultureInfo.CurrentCulture);
        PreviewActorZTextBox.Text = origin.Location.Z.ToString("0.###", CultureInfo.CurrentCulture);
        PreviewActorRollTextBox.Text = origin.Rotation.X.ToString("0.###", CultureInfo.CurrentCulture);
        PreviewActorPitchTextBox.Text = origin.Rotation.Y.ToString("0.###", CultureInfo.CurrentCulture);
        PreviewActorYawTextBox.Text = origin.Rotation.Z.ToString("0.###", CultureInfo.CurrentCulture);
    }

    private void SetSelectedPreviewActorOrigin(CameraOrigin origin)
    {
        if (selectedPreviewActor is null)
        {
            return;
        }
        selectedPreviewActor.Origin = origin;
        RecordActiveDialogueActorOrigin(selectedPreviewActor, origin);
        previewActorWidgetTarget.SetTransform(origin);
        SynchronizePreviewActorControls();
        SavePreviewActorLayout();
        SceneViewer.MarkRenderDirty();
    }

    private void PreviewActorLocationScrubAxis_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string axes })
        {
            previewActorLocationScrubAxes = axes;
        }
    }

    private void PreviewActorLocationScrub_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (selectedPreviewActor is null)
        {
            e.Handled = true;
            return;
        }
        previewActorLocationScrubAccumulator = 0;
        previewActorLocationScrubPreviousHorizontalChange = 0;
    }

    private void PreviewActorLocationScrub_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (selectedPreviewActor is null || !double.IsFinite(e.HorizontalChange))
        {
            return;
        }

        double horizontalChange = e.HorizontalChange - previewActorLocationScrubPreviousHorizontalChange;
        previewActorLocationScrubPreviousHorizontalChange = e.HorizontalChange;
        previewActorLocationScrubAccumulator += horizontalChange;
        double dragStep = SystemParameters.MinimumHorizontalDragDistance;
        int stepCount = (int)(previewActorLocationScrubAccumulator / dragStep);
        if (stepCount == 0)
        {
            return;
        }

        previewActorLocationScrubAccumulator -= stepCount * dragStep;
        Vector3 location = selectedPreviewActor.Origin.Location;
        if (previewActorLocationScrubAxes is "X" or "All") location.X += stepCount;
        if (previewActorLocationScrubAxes is "Y" or "All") location.Y += stepCount;
        if (previewActorLocationScrubAxes is "Z" or "All") location.Z += stepCount;
        SetSelectedPreviewActorOrigin(new CameraOrigin(location, selectedPreviewActor.Origin.Rotation));
    }

    private void PreviewActorLocationScrub_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        SavePreviewActorLayout();
    }

    private void PreviewActorRotationDialAxis_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string axes })
        {
            previewActorRotationDialAxes = axes;
            UpdatePreviewActorRotationDialIndicator();
        }
    }

    private void PreviewActorRotationDial_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (selectedPreviewActor is null)
        {
            return;
        }
        previewActorRotationDialPreviousAngle = GetPreviewActorRotationDialPointerAngle(e.GetPosition(PreviewActorRotationDial));
        previewActorRotationDialAngleAccumulator = 0;
        previewActorRotationDialDragging = PreviewActorRotationDial.CaptureMouse();
        e.Handled = true;
    }

    private void PreviewActorRotationDial_MouseMove(object sender, MouseEventArgs e)
    {
        if (!previewActorRotationDialDragging || selectedPreviewActor is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        double pointerAngle = GetPreviewActorRotationDialPointerAngle(e.GetPosition(PreviewActorRotationDial));
        double angleDelta = NormalizeAngle(pointerAngle - previewActorRotationDialPreviousAngle);
        previewActorRotationDialPreviousAngle = pointerAngle;
        previewActorRotationDialAngleAccumulator += angleDelta;
        const float increment = 5f;
        int stepCount = (int)(previewActorRotationDialAngleAccumulator / increment);
        if (stepCount == 0)
        {
            return;
        }

        previewActorRotationDialAngleAccumulator -= stepCount * increment;
        float delta = stepCount * increment;
        Vector3 rotation = selectedPreviewActor.Origin.Rotation;
        if (previewActorRotationDialAxes is "Roll" or "All") rotation.X += delta;
        if (previewActorRotationDialAxes is "Pitch" or "All") rotation.Y += delta;
        if (previewActorRotationDialAxes is "Yaw" or "All") rotation.Z += delta;
        SetSelectedPreviewActorOrigin(new CameraOrigin(selectedPreviewActor.Origin.Location, rotation));
        e.Handled = true;
    }

    private void PreviewActorRotationDial_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!previewActorRotationDialDragging)
        {
            return;
        }
        previewActorRotationDialDragging = false;
        PreviewActorRotationDial.ReleaseMouseCapture();
        SavePreviewActorLayout();
        e.Handled = true;
    }

    private void PreviewActorRotationDial_LostMouseCapture(object sender, MouseEventArgs e)
    {
        previewActorRotationDialDragging = false;
    }

    private void UpdatePreviewActorRotationDialIndicator()
    {
        if (PreviewActorRotationDialIndicator?.RenderTransform is not System.Windows.Media.RotateTransform transform)
        {
            return;
        }
        Vector3 rotation = selectedPreviewActor?.Origin.Rotation ?? Vector3.Zero;
        transform.Angle = previewActorRotationDialAxes switch
        {
            "Roll" => rotation.X,
            "Pitch" => rotation.Y,
            "Yaw" => rotation.Z,
            _ => (rotation.X + rotation.Y + rotation.Z) / 3f
        };
    }

    private static double GetPreviewActorRotationDialPointerAngle(Point point)
        => Math.Atan2(point.Y - 45d, point.X - 45d) * 180d / Math.PI + 90d;

    private void PreviewActorMoveGizmo_Checked(object sender, RoutedEventArgs e)
    {
        RenderContext.TransformWidget.Mode = EWidgetMode.Translate;
        RenderContext.TransformWidget.VisibleAxes = EWidgetAxis.XYZ;
        SceneViewer?.MarkRenderDirty();
    }

    private void PreviewActorRotateGizmo_Checked(object sender, RoutedEventArgs e)
    {
        RenderContext.TransformWidget.Mode = EWidgetMode.Rotate;
        RenderContext.TransformWidget.VisibleAxes = EWidgetAxis.XYZ;
        SceneViewer?.MarkRenderDirty();
    }

    private void PreviewActorGizmo_TransformChanged(CameraOrigin origin)
    {
        if (selectedPreviewActor is null)
        {
            return;
        }
        selectedPreviewActor.Origin = origin;
        RecordActiveDialogueActorOrigin(selectedPreviewActor, origin);
        updatingPreviewActorControls = true;
        SetPreviewActorOriginFields(origin);
        UpdatePreviewActorRotationDialIndicator();
        updatingPreviewActorControls = false;
        SavePreviewActorLayout();
        SceneViewer.MarkRenderDirty();
    }

    private void RecordActiveDialogueActorOrigin(PreviewActorConfiguration actor, CameraOrigin origin)
    {
        if (!isDialogueConversationPreview || suppressDialogueCacheEditTracking
            || activeDialogueSegmentRuntime is null || actor is null)
        {
            return;
        }
        activeDialogueSegmentRuntime.ActorOriginOverrides[actor.ActorTag] = origin;
        if (playbackActors.FirstOrDefault(state => ReferenceEquals(state.Actor, actor)) is { } playbackState)
        {
            playbackState.OriginalOrigin = origin;
        }
        MarkActiveDialogueNodePreviewChanged();
    }

    private void MarkActiveDialogueNodePreviewChanged()
    {
        if (!isDialogueConversationPreview || suppressDialogueCacheEditTracking
            || activeDialogueSegmentRuntime is null)
        {
            return;
        }
        if (isPlayingDialogueTimeline)
        {
            PauseDialogueTimeline();
        }
        activeDialogueSegmentRuntime.HasPendingPreviewChanges = true;
        UpdateDialogueNodeCommitButton();
    }

    private void UseSelectedKeyframeForPreviewActor_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedKeyframe is { } keyframe)
        {
            SetSelectedPreviewActorOrigin(new CameraOrigin(keyframe.Location, keyframe.Rotation));
        }
    }

    private void UseViewportLocationForPreviewActor_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPreviewActor is not null)
        {
            SetSelectedPreviewActorOrigin(new CameraOrigin(RenderContext.Camera.Position, selectedPreviewActor.Origin.Rotation));
        }
    }

    private void UseViewportTransformForPreviewActor_Click(object sender, RoutedEventArgs e)
    {
        const float radiansToDegrees = 180f / MathF.PI;
        SetSelectedPreviewActorOrigin(new CameraOrigin(RenderContext.Camera.Position,
            new Vector3(RenderContext.Camera.Roll, RenderContext.Camera.Pitch, RenderContext.Camera.Yaw) * radiansToDegrees));
    }

    private void ResetPreviewActor_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPreviewActor is null)
        {
            return;
        }
        ResetSelectedPreviewActorModels();
        CameraOrigin origin = SelectedKeyframe is null
            ? new CameraOrigin(RenderContext.Camera.Position, Vector3.Zero)
            : new CameraOrigin(SelectedKeyframe.Location, SelectedKeyframe.Rotation);
        SetSelectedPreviewActorOrigin(origin);
        LoadSelectedPreviewActorModel();
    }

    private void ResetPreviewActorModels_Click(object sender, RoutedEventArgs e)
    {
        ResetSelectedPreviewActorModels();
        LoadSelectedPreviewActorModel();
        SynchronizePreviewActorControls();
        SavePreviewActorLayout();
    }

    private void ResetSelectedPreviewActorModels()
    {
        if (selectedPreviewActor is null || previewAssetDatabase is null
            || previewActorMeshes.Count == 0)
        {
            return;
        }
        foreach (PreviewActorModelComponent component in Enum.GetValues<PreviewActorModelComponent>())
        {
            MeshRecord mesh = PreviewActorModelDefaults.FindDefaultMesh(previewActorMeshes, previewAssetDatabase, component, previewActorGame);
            if (mesh is not null)
            {
                SetPreviewActorModelName(selectedPreviewActor, component, mesh.MeshName);
                TryLoadPreviewActorModel(previewActors.IndexOf(selectedPreviewActor), component, mesh, true, out _);
            }
        }
    }
}
