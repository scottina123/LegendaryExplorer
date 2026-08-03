using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
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
using LegendaryExplorer.Tools.PackageEditor.Experiments;
using LegendaryExplorer.UserControls.Interfaces;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Dialogue;
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
using StageConversationContext = LegendaryExplorer.Tools.InterpEditor.StageConversationContext;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;
using InterpCurveFloat = LegendaryExplorerCore.Unreal.BinaryConverters.InterpCurve<float>;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public sealed partial class CurveEditor3D : ExportLoaderControl, IActorEditorContext, ISceneRenderContextConfigurable
{
    private enum DialoguePreviewAudioGender
    {
        Male,
        Female
    }

    public sealed record DialogueNodePreviewActor(string ActorTag, CameraOrigin Origin);
    public sealed record DialoguePreviewRecentLevelSet(string DisplayName, IReadOnlyList<string> FilePaths);
    public sealed record DialogueNodeReference(bool IsReply, int Index);

    private sealed record DialogueNodePreviewConfiguration(
        ConversationExtended Conversation,
        DialogueNodeExtended Node,
        IReadOnlyList<DialogueNodePreviewActor> Actors,
        IReadOnlyList<string> LevelPaths,
        StageConversationContext StageContext,
        float VoStartTime);

    public sealed class DialogueTimelineSegment
    {
        public DialogueNodeExtended Node { get; init; }
        public DialogueNodeReference Reference { get; init; }
        public float StartTime { get; init; }
        public float Duration { get; init; }
        public float EndTime => StartTime + Duration;
        public string NodeLabel => $"{(Node.IsReply ? "R" : "E")}{Node.NodeCount}";
        public string LineLabel => string.IsNullOrWhiteSpace(Node.Line) ? $"StrRef {Node.LineStrRef}" : Node.Line;
        public string DisplayLabel => $"{NodeLabel}  {LineLabel}";
    }

    public sealed class DialogueBranchOption
    {
        public DialogueNodeExtended Source { get; init; }
        public DialogueNodeExtended Target { get; init; }
        public DialogueNodeReference TargetReference { get; init; }
        public string BranchKey { get; init; }
        public string Category { get; init; }
        public string NodeLabel => $"{(Target.IsReply ? "R" : "E")}{Target.NodeCount}";
        public string LineLabel => string.IsNullOrWhiteSpace(Target.Line) ? $"StrRef {Target.LineStrRef}" : Target.Line;
        public string DisplayLabel => string.IsNullOrWhiteSpace(Category)
            ? $"{NodeLabel}: {LineLabel}"
            : $"{Category} — {NodeLabel}: {LineLabel}";
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

    private sealed record ActorDirectionKey(float Time, string TargetActorTag);

    private sealed record ActorDirectionTrack(
        PreviewActorConfiguration Actor,
        bool IsLookAt,
        IReadOnlyList<ActorDirectionKey> Keys);

    private sealed class ActorModelSet : IDisposable
    {
        public sealed class Component : IDisposable
        {
            public ModelPreview<WorldVertex> Model { get; init; }
            public SkinnedMeshRenderer Renderer { get; init; }
            public void Dispose() => Model?.Dispose();
        }

        private readonly Dictionary<PreviewActorModelComponent, Component> components = [];
        public ModelPreview<WorldVertex> Body => components.GetValueOrDefault(PreviewActorModelComponent.Body)?.Model;
        public IEnumerable<Component> Components => components.Values;

        public void Set(PreviewActorModelComponent component, ModelPreview<WorldVertex> model,
            SkinnedMeshRenderer renderer)
        {
            if (components.Remove(component, out Component previous)) previous.Dispose();
            components[component] = new Component { Model = model, Renderer = renderer };
        }

        public void Remove(PreviewActorModelComponent component)
        {
            if (components.Remove(component, out Component previous)) previous.Dispose();
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

    private sealed class PreviewActorAnimationState
    {
        public sealed class LayeredAnimationPlayer : AnimPlayer
        {
            public AnimSequencePlayer GesturePlayer { get; }
            public FaceFxPlayer FaceFxPlayer { get; }
            public float FaceFxTimelineOffset { get; set; }
            public override bool HasAnimation => GesturePlayer.HasAnimation || FaceFxPlayer.HasAnimation;
            public override float Duration => Math.Max(GesturePlayer.Duration, FaceFxPlayer.Duration);
            public override float StartTime => Math.Min(GesturePlayer.StartTime, FaceFxTimelineOffset);
            public override float EndTime => Math.Max(GesturePlayer.EndTime, FaceFxTimelineOffset + FaceFxPlayer.Duration);

            public LayeredAnimationPlayer(SkeletalMesh skeletalMesh) : base(skeletalMesh)
            {
                GesturePlayer = new AnimSequencePlayer(skeletalMesh);
                FaceFxPlayer = new FaceFxPlayer(skeletalMesh);
            }

            public override void SetCurrentTime(float time)
            {
                CurrentTime = time;
                GesturePlayer.SetCurrentTime(time);
                FaceFxPlayer.SetCurrentTime(FaceFxPlayer.StartTime + time - FaceFxTimelineOffset);
            }

            public override Matrix4x4[] ComputeSkinningMatrices()
            {
                bool hasGesture = GesturePlayer.HasAnimation;
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

                    _boneComponentSpace[index] = bone.ParentIndex >= 0 && bone.ParentIndex < index
                        ? finalLocal * _boneComponentSpace[bone.ParentIndex]
                        : finalLocal;
                    _skinningMatrices[index] = _inverseBindPose[index] * _boneComponentSpace[index];
                }
                return _skinningMatrices;
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
        public bool HasTimeline => Player?.HasAnimation == true;

        public void SetTimeline(GesturePreviewExportLoader.GestureAnimationItem startingPose,
            IEnumerable<AnimationPreviewControl.AnimationTimelineClip> timeline, PackageCache packageCache)
        {
            if (startingPose?.AnimationExport is not null)
            {
                var startingPoseAnimation = ObjectBinary.From<AnimSequence>(startingPose.AnimationExport);
                startingPoseAnimation.DecompressAnimationData();
                Player.GesturePlayer.SetAnimation(startingPoseAnimation, packageCache);
                Player.GesturePlayer.SetCurrentTime(startingPose.Settings.StartOffset);
                Player.GesturePlayer.ComputeSkinningMatrices();
            }

            List<AnimSequencePlayer.ScheduledAnimationClip> scheduledClips = [];
            foreach (AnimationPreviewControl.AnimationTimelineClip clip in timeline)
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

        public void SetTime(float time)
        {
            Player.SetCurrentTime(time);
            Renderer.NeedsUpdate = true;
        }

        public void Clear()
        {
            Player.GesturePlayer.SetAnimation(null);
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
        public DialoguePreviewAudioGender AudioGender { get; set; }
        public string FaceFxAssetName { get; set; } = "SFX_HumanFemale_FaceFX";
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
        public InterpCurveFloat FovTrack { get; init; }
    }

    private sealed class PreviewActorPlaybackState
    {
        public PreviewActorConfiguration Actor { get; init; }
        public TrackMovePlaybackOption TrackMove { get; init; }
        public CameraOrigin OriginalOrigin { get; init; }
        public CameraOrigin TrackStartOrigin { get; init; }
        public EInterpTrackMoveFrame MoveFrame { get; init; }
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
    }

    private const float PreviewBodyMeshRelativeZ = -88f;
    private static readonly RenderPass[] RenderPasses = [RenderPass.Base, RenderPass.Hair];
    private static readonly object sessionLevelPathsLock = new();
    private static readonly List<string> sessionLevelPaths = [];
    private static IMEPackage sessionSourcePackage;

    private readonly CurveEditor3DModel model = new();
    private readonly List<IMEPackage> levelPackages = [];
    private readonly List<ActorProxy> levelActors = [];
    private readonly List<string> levelPaths = [];
    private readonly ObservableCollection<PreviewActorConfiguration> previewActors = [];
    private readonly ObservableCollection<string> dialoguePreviewFaceFxAssetNames = ["SFX_HumanFemale_FaceFX"];
    private readonly List<ActorModelSet> previewActorModels = [];
    private readonly PreviewActorWidgetTarget previewActorWidgetTarget = new();
    private readonly PackageCache previewActorGesturePackageCache = new();
    private IMEPackage dialoguePreviewFaceFxPackage;
    private readonly Dictionary<PreviewActorConfiguration, FaceFXAnimSet> dialoguePreviewFaceFxAnimSets = [];
    private readonly ObservableCollection<GestureTrackOption> availableGestureTracks = [];
    private readonly Dictionary<PreviewActorConfiguration, GestureTrackOption> previewActorGestureAssignments = [];
    private readonly Dictionary<PreviewActorConfiguration, PreviewActorAnimationState> previewActorAnimationStates = [];
    private readonly Dictionary<PreviewActorConfiguration, TrackMovePlaybackOption> previewActorTrackAssignments = [];
    private readonly List<TrackMovePlaybackOption> availableTrackMoves = [];
    private readonly ObservableCollection<TrackMovePlaybackOption> availableExtraTrackMoves = [];
    private readonly ObservableCollection<DirectorPlaybackOption> availableDirectorTracks = [];
    private readonly ObservableCollection<TrackMovePlaybackOption> keyframeTrackMoves = [];
    private readonly List<TrackMovePlaybackOption> dialoguePreviewCameraActors = [];
    private AssetDB previewAssetDatabase;
    private List<MeshRecord> previewActorMeshes = [];
    private List<(string FileName, string ContentDir)> previewAssetFiles = [];
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
    private bool resumeDialogueTimelineAfterBranch;
    private bool updatingDialogueTimelineSlider;
    private float dialogueTimelineCurrentTime;
    private DialogueTimelineSegment activeDialogueTimelineSegment;
    private bool updatingMulticamControls;
    private bool playExtraTrackMove;
    private bool playDirectorMulticam;
    private bool sessionLevelsRestored;
    private bool trajectorySamplesDirty;
    private Button playMoveButton;
    private Button playActorButton;
    private readonly List<PreviewActorPlaybackState> playbackActors = [];
    private readonly List<ActorDirectionTrack> actorDirectionTracks = [];
    private readonly ObservableCollection<DialogueTimelineSegment> dialogueTimelineSegments = [];
    private readonly ObservableCollection<DialogueBranchOption> dialogueBranchOptions = [];
    private readonly Dictionary<string, DialogueNodeReference> dialogueBranchSelections = new(StringComparer.Ordinal);
    private CurveEditor3DKeyframe selectedKeyframe;
    private string currentExportName;
    private string sceneStatus = "Select an InterpTrackMove export, then optionally open a level backdrop.";
    private string playbackKeyframeStatus = "Not playing";
    private float playbackStartTime;
    private float playbackEndTime;
    private float playbackElapsed;
    private float playbackCurrentTime;
    private bool dialoguePreviewAudioStarted;
    private TrackMovePlaybackOption selectedExtraTrackMove;
    private DirectorPlaybackOption selectedDirectorPlayback;
    private TrackMovePlaybackOption primaryTrackMove;
    private DialogueNodePreviewConfiguration dialogueNodePreview;
    private bool isDialogueConversationPreview;
    private CurveEditor3DModel registeredKeyframeModel;
    private bool updatingKeyframeTrackTabs;
    private Vector3 pendingViewportKeyframeLocation;
    private Vector3 pendingViewportSelectedKeyframeLocation;
    private bool showCollision = Settings.LevelEditor_ShowCollision;
    private bool showLightIcons;
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
    private string locationScrubAxes = "X";
    private double locationScrubDragAccumulator;
    private double locationScrubPreviousHorizontalChange;
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
    public IEnumerable<DialogueBranchOption> DialogueBranchOptions => dialogueBranchOptions;

    private string SavedPreviewActorsPath => Path.Combine(AppDirectories.AppDataFolder,
        $"CurveEditor3DPreviewActors_{previewActorGame}.json");

    private CurveEditor3DModel ActiveModel
        => (KeyframeTrackMoveTabs?.SelectedItem as TrackMovePlaybackOption)?.Model ?? model;

    private ExportEntry ActiveTrackMoveExport => ActiveModel.Export ?? CurrentLoadedExport;

    public CurveEditor3D() : base("3D Curve Editor")
    {
        RenderContext = new LevelEditorRenderContext();
        backgroundColor = LevelEditor.GetThemeDefaultBackgroundColor();
        RenderContext.BackgroundColor = backgroundColor;
        RenderContext.ShowLightIcons = showLightIcons;
        if (unlit)
        {
            RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.Unlit;
        }
        InterpModes = Enum.GetValues<EInterpCurveMode>();
        LoadCommands();
        InitializeComponent();
        PreviewActorListBox.ItemsSource = previewActors;
        DialoguePreviewActorListBox.ItemsSource = previewActors;
        DialoguePreviewAudioGenderComboBox.ItemsSource = Enum.GetValues<DialoguePreviewAudioGender>();
        DialoguePreviewFaceFxAssetComboBox.ItemsSource = dialoguePreviewFaceFxAssetNames;
        PreviewActorGestureComboBox.ItemsSource = availableGestureTracks;
        ExtraTrackMoveComboBox.ItemsSource = availableExtraTrackMoves;
        DirectorTrackComboBox.ItemsSource = availableDirectorTracks;
        KeyframeTrackMoveTabs.ItemsSource = keyframeTrackMoves;
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
        StageConversationContext stageContext)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(levelPaths);
        ArgumentNullException.ThrowIfNull(stageContext);
        isDialogueConversationPreview = false;
        dialogueNodePreview = new DialogueNodePreviewConfiguration(conversation, node, actors, levelPaths, stageContext,
            GetDialoguePreviewVoStartTime(node.InterpData));
        BuildDialogueTimeline(node);
        DialoguePreviewActorPanel.Visibility = Visibility.Visible;
        DialoguePreviewActorPanelSplitter.Visibility = Visibility.Visible;
    }

    internal void ConfigureDialogueConversationPreview(ConversationExtended conversation, DialogueNodeExtended startNode,
        IReadOnlyList<DialogueNodePreviewActor> actors, IReadOnlyList<string> levelPaths,
        StageConversationContext stageContext)
    {
        dialogueBranchSelections.Clear();
        ConfigureDialogueNodePreview(conversation, startNode, actors, levelPaths, stageContext);
        isDialogueConversationPreview = true;
    }

    private void BuildDialogueTimeline(DialogueNodeExtended startNode)
    {
        dialogueTimelineSegments.Clear();
        dialogueBranchOptions.Clear();
        if (dialogueNodePreview?.Conversation is not { } conversation || startNode is null)
        {
            return;
        }

        float timelineTime = 0;
        DialogueNodeExtended current = startNode;
        var visited = new HashSet<DialogueNodeReference>();
        for (int nodeCount = 0; current is not null && nodeCount < 512; nodeCount++)
        {
            DialogueNodeReference currentReference = GetDialogueNodeReference(conversation, current);
            if (!visited.Add(currentReference))
            {
                break;
            }

            float duration = GetDialogueNodeTimelineDuration(current);
            dialogueTimelineSegments.Add(new DialogueTimelineSegment
            {
                Node = current,
                Reference = currentReference,
                StartTime = timelineTime,
                Duration = duration
            });
            timelineTime += duration;

            List<DialogueBranchOption> outgoing = GetDialogueBranchOptions(conversation, current);
            if (outgoing.Count == 0)
            {
                break;
            }
            if (outgoing.Count == 1)
            {
                current = outgoing[0].Target;
                continue;
            }

            string branchKey = GetDialogueBranchKey(currentReference);
            if (dialogueBranchSelections.TryGetValue(branchKey, out DialogueNodeReference selectedReference)
                && outgoing.FirstOrDefault(option => option.TargetReference == selectedReference) is { } selected)
            {
                current = selected.Target;
                continue;
            }

            foreach (DialogueBranchOption option in outgoing)
            {
                dialogueBranchOptions.Add(option);
            }
            break;
        }
        UpdateDialogueTimelineControls();
    }

    private static float GetDialogueNodeTimelineDuration(DialogueNodeExtended node)
    {
        float interpLength = node.InterpData?.GetProperty<FloatProperty>("InterpLength")?.Value ?? node.InterpLength;
        return MathF.Max(interpLength, 0.1f);
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
            bool selectionChanged = SetProperty(ref selectedKeyframe, value);
            previewActorWidgetActive = false;
            SelectedKeyframeInVal = value?.Time.ToString(CultureInfo.CurrentCulture);
            SnapToKeyButton.IsEnabled = value is not null;
            KeyframeList.SelectedItem = value;
            if (value is not null && (selectionChanged || !KeyframeList.IsKeyboardFocusWithin))
            {
                KeyframeList.ScrollIntoView(value);
            }
            RenderContext.TransformWidget.Attach = value;
            UpdateRotationDialIndicator();
            SceneViewer?.MarkRenderDirty();
        }
    }

    public string SelectedKeyframeInVal
    {
        get => selectedKeyframeInVal;
        set => SetProperty(ref selectedKeyframeInVal, value);
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
        model.Load(exportEntry);
        RefreshAvailableGestureTracks(exportEntry);
        RefreshMulticamPlaybackOptions(exportEntry);
        InitializePreviewActorLayout(exportEntry.Game);
        EnsurePreviewActorTrackAssignments();
        RefreshKeyframeTrackMoveTabs();
        trajectorySamplesDirty = true;
        SelectedKeyframe = selectedKeyframeTime.HasValue && model.Keyframes.Count > 0
            ? model.Keyframes.MinBy(keyframe => MathF.Abs(keyframe.Time - selectedKeyframeTime.Value))
            : model.Keyframes.FirstOrDefault();
        if (!hasSnappedInitialCamera && model.Keyframes.MinBy(keyframe => keyframe.Time) is { } earliestKeyframe)
        {
            SnapCameraToKey(earliestKeyframe);
            hasSnappedInitialCamera = true;
        }
        CurrentExportName = $"{exportEntry.UIndex}: {exportEntry.InstancedFullPath}";
        SceneStatus = $"{model.Keyframes.Count} trajectory keyframe(s); {levelPaths.Count} level backdrop file(s).";
        UpdatePlaybackButton();
        SceneViewer?.MarkRenderDirty();
        _ = RestoreSessionLevelsAsync();
    }

    public override void UnloadExport()
    {
        StopPlayback(false);
        UnregisterKeyframes();
        model.Clear();
        trajectorySamples = [];
        trajectorySamplesDirty = false;
        PlaybackKeyframeStatus = "Not playing";
        KeyframeList.ItemsSource = null;
        SelectedKeyframe = null;
        CurrentLoadedExport = null;
        CurrentExportName = null;
        previewActorGestureAssignments.Clear();
        previewActorTrackAssignments.Clear();
        availableGestureTracks.Clear();
        availableTrackMoves.Clear();
        availableExtraTrackMoves.Clear();
        availableDirectorTracks.Clear();
        keyframeTrackMoves.Clear();
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
        dialogueNodePreview?.StageContext.Dispose();
        SceneViewer.Dispose();
    }

    private void RefreshAvailableGestureTracks(ExportEntry trackMove)
    {
        availableGestureTracks.Clear();
        availableGestureTracks.Add(GestureTrackOption.None);
        previewActorGestureAssignments.Clear();
        foreach (KeyValuePair<PreviewActorConfiguration, PreviewActorAnimationState> pair in previewActorAnimationStates)
        {
            pair.Value.Clear();
            UpdatePreviewActorSkinning(pair.Key);
        }
        previewActorGesturePackageCache.ReleasePackages();

        foreach (ExportEntry gestureTrack in FindGestureTracksInSameInterpData(trackMove))
        {
            List<GesturePreviewExportLoader.GestureAnimationItem> animations = GesturePreviewExportLoader
                .BuildAnimationTimeline(gestureTrack, previewActorGesturePackageCache);
            List<GesturePreviewExportLoader.GestureAnimationItem> resolvedAnimations = animations
                .Where(animation => animation.AnimationExport is not null)
                .ToList();
            GesturePreviewExportLoader.GestureAnimationItem startingPose = resolvedAnimations
                .FirstOrDefault(animation => !animation.GestureIndex.HasValue);
            List<AnimationPreviewControl.AnimationTimelineClip> timeline = GesturePreviewExportLoader
                .BuildPlaybackTimeline(resolvedAnimations.Where(animation => animation.GestureIndex.HasValue).ToList());
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

    private void RefreshMulticamPlaybackOptions(ExportEntry trackMove)
    {
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

        ExportEntry interpData = FindOwningInterpData(trackMove);
        if (interpData is not null)
        {
            Dictionary<string, TrackMovePlaybackOption> cameraOptionsByGroup = new(StringComparer.OrdinalIgnoreCase);
            foreach (ExportEntry group in GetReferencedExports(interpData, "InterpGroups"))
            {
                string groupName = GetInterpGroupName(group);
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
                        FovTrack = LoadCameraFovTrack(group),
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
                List<DirectorCameraCut> cuts = BuildDirectorCameraCuts(directorTrack, cameraOptionsByGroup);
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
                cameraGroupNames.UnionWith(availableTrackMoves
                    .Where(option => GetInterpGroupName(option.Group).StartsWith("Cam", StringComparison.OrdinalIgnoreCase))
                    .Select(option => GetInterpGroupName(option.Group)));
                dialoguePreviewCameraActors.AddRange(availableTrackMoves
                    .Where(option => cameraGroupNames.Contains(GetInterpGroupName(option.Group)))
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
        primaryTrackMove ??= new TrackMovePlaybackOption
        {
            DisplayName = trackMove.GetProperty<StrProperty>("TrackTitle")?.Value ?? trackMove.ObjectName.Instanced,
            Group = trackMove.Parent as ExportEntry,
            TrackMove = trackMove,
            Model = model,
            FovTrack = LoadCameraFovTrack(trackMove.Parent as ExportEntry),
        };
        if (availableTrackMoves.All(option => !IsSameExport(option.TrackMove, primaryTrackMove.TrackMove)))
        {
            availableTrackMoves.Insert(0, primaryTrackMove);
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
        ExportEntry previouslySelectedTrack = (KeyframeTrackMoveTabs.SelectedItem as TrackMovePlaybackOption)?.TrackMove;
        List<TrackMovePlaybackOption> tabs = [];
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

        updatingKeyframeTrackTabs = true;
        keyframeTrackMoves.Clear();
        foreach (TrackMovePlaybackOption tab in tabs)
        {
            keyframeTrackMoves.Add(tab);
        }

        KeyframeTrackMoveTabs.SelectedItem = tabs.FirstOrDefault(tab => IsSameExport(tab.TrackMove, previouslySelectedTrack))
                                                 ?? primaryTrackMove;
        updatingKeyframeTrackTabs = false;
        ActivateSelectedTrackMove();
    }

    private static void AddDistinctTrackMove(List<TrackMovePlaybackOption> tabs, TrackMovePlaybackOption option)
    {
        if (option?.TrackMove is not null && tabs.All(tab => !IsSameExport(tab.TrackMove, option.TrackMove)))
        {
            tabs.Add(option);
        }
    }

    private static bool IsSameExport(ExportEntry left, ExportEntry right)
        => left is not null && right is not null && left.FileRef == right.FileRef && left.UIndex == right.UIndex;

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

    private static InterpCurveFloat LoadCameraFovTrack(ExportEntry group)
    {
        ExportEntry fovTrack = GetReferencedExports(group, "InterpTracks").FirstOrDefault(track =>
            track.ClassName == "InterpTrackFloatProp"
            && (string.Equals(track.GetProperty<NameProperty>("PropertyName")?.Value.Instanced, "FOVAngle",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(track.GetProperty<StrProperty>("TrackTitle")?.Value, "FOVAngle",
                    StringComparison.OrdinalIgnoreCase)));
        StructProperty floatTrack = fovTrack?.GetProperty<StructProperty>("FloatTrack");
        return floatTrack is null ? null : InterpCurveFloat.FromStructProperty(floatTrack, fovTrack.Game);
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

    private static List<DirectorCameraCut> BuildDirectorCameraCuts(ExportEntry directorTrack,
        IReadOnlyDictionary<string, TrackMovePlaybackOption> cameraOptionsByGroup)
    {
        ArrayProperty<StructProperty> cutTrack = directorTrack.GetProperty<ArrayProperty<StructProperty>>("CutTrack");
        if (cutTrack is null)
        {
            return [];
        }

        return cutTrack
            .Select(cut => new
            {
                Time = cut.GetProp<FloatProperty>("Time")?.Value ?? 0,
                GroupName = cut.GetProp<NameProperty>("TargetCamGroup")?.Value.Instanced,
            })
            .Where(cut => !string.IsNullOrWhiteSpace(cut.GroupName)
                          && cameraOptionsByGroup.TryGetValue(cut.GroupName, out _))
            .OrderBy(cut => cut.Time)
            .Select(cut => new DirectorCameraCut
            {
                Time = cut.Time,
                GroupName = cut.GroupName,
                Camera = cameraOptionsByGroup[cut.GroupName],
            })
            .ToList();
    }

    private static IEnumerable<ExportEntry> FindGestureTracksInSameInterpData(ExportEntry trackMove)
    {
        if (trackMove?.Parent is not ExportEntry interpGroup
            || interpGroup.Parent is not ExportEntry interpData)
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
            previewActorMeshes = meshes;

            for (int actorIndex = 0; actorIndex < previewActors.Count; actorIndex++)
            {
                PreviewActorConfiguration actor = previewActors[actorIndex];
                foreach (PreviewActorModelComponent component in Enum.GetValues<PreviewActorModelComponent>())
                {
                    if (component is not PreviewActorModelComponent.Body
                        && string.IsNullOrEmpty(GetPreviewActorModelName(actor, component)))
                    {
                        previewActorModels.ElementAtOrDefault(actorIndex)?.Remove(component);
                        continue;
                    }
                    MeshRecord configuredMesh = FindConfiguredPreviewActorMesh(meshes, actor, component);
                    MeshRecord mesh = actor.BaseGameModelsOnly
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
                await LoadDialoguePreviewLevelsAsync().ConfigureAwait(true);
                ConfigureDialoguePreviewPlayback();
                if (isDialogueConversationPreview)
                {
                    ApplyDialogueTimelineAtTime(0, reconstruct: true);
                    StartDialogueTimelinePlayback();
                }
                else
                {
                    PlayActor_Click(this, new RoutedEventArgs());
                }
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
    }

    private void ConfigureDialoguePreviewPlayback()
    {
        LoadDialoguePreviewFaceFxAssets();
        foreach (PreviewActorConfiguration actor in previewActors)
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
            if (IsDialogueNodeSpeaker(actor))
            {
                ApplyDialoguePreviewFaceFx(actor);
            }
        }

        updatingMulticamControls = true;
        selectedDirectorPlayback = availableDirectorTracks.FirstOrDefault(option => option.DirectorTrack is not null)
                                   ?? DirectorPlaybackOption.None;
        playDirectorMulticam = selectedDirectorPlayback.Cuts.Count > 0;
        selectedExtraTrackMove = TrackMovePlaybackOption.None;
        playExtraTrackMove = false;
        if (!playDirectorMulticam)
        {
            selectedExtraTrackMove = availableTrackMoves.FirstOrDefault(option =>
                GetInterpGroupName(option.Group).StartsWith("Cam", StringComparison.OrdinalIgnoreCase))
                ?? TrackMovePlaybackOption.None;
            playExtraTrackMove = selectedExtraTrackMove.TrackMove is not null;
        }
        DirectorTrackComboBox.SelectedItem = selectedDirectorPlayback;
        ExtraTrackMoveComboBox.SelectedItem = selectedExtraTrackMove;
        DirectorMulticamCheckBox.IsChecked = playDirectorMulticam;
        ExtraTrackMoveCheckBox.IsChecked = playExtraTrackMove;
        updatingMulticamControls = false;
        ActorPlaybackTrackZCheckBox.IsChecked = true;
        BuildActorDirectionTracks();
        RefreshKeyframeTrackMoveTabs();
    }

    private void LoadDialoguePreviewFaceFxAssets()
    {
        dialoguePreviewFaceFxPackage?.Dispose();
        dialoguePreviewFaceFxPackage = null;
        dialoguePreviewFaceFxAssetNames.Clear();
        string cookedPath = MEDirectories.GetCookedPath(CurrentLoadedExport.Game);
        string packagePath = Directory.Exists(cookedPath)
            ? Directory.EnumerateFiles(cookedPath, "BIOG_FaceFX_Assets.*", SearchOption.TopDirectoryOnly).FirstOrDefault()
            : null;
        if (packagePath is null)
        {
            dialoguePreviewFaceFxAssetNames.Add("SFX_HumanFemale_FaceFX");
            return;
        }

        dialoguePreviewFaceFxPackage = MEPackageHandler.OpenMEPackage(packagePath);
        foreach (string assetName in dialoguePreviewFaceFxPackage.Exports
                     .Where(export => export.ClassName == "FaceFXAsset" && !export.IsDefaultObject)
                     .Select(export => export.ObjectNameString)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            dialoguePreviewFaceFxAssetNames.Add(assetName);
        }
        DialoguePreviewFaceFxAssetComboBox.Items.Refresh();
    }

    private void ApplyDialoguePreviewFaceFx(PreviewActorConfiguration actor)
    {
        if (!IsDialogueNodeSpeaker(actor)
            || !previewActorAnimationStates.TryGetValue(actor, out PreviewActorAnimationState animationState)
            || dialoguePreviewFaceFxPackage is null)
        {
            return;
        }

        ExportEntry assetExport = dialoguePreviewFaceFxPackage.Exports.FirstOrDefault(export =>
            export.ClassName == "FaceFXAsset"
            && export.ObjectNameString.Equals(actor.FaceFxAssetName, StringComparison.OrdinalIgnoreCase));
        IEntry animSetEntry = actor.AudioGender == DialoguePreviewAudioGender.Female
            ? dialogueNodePreview.Node.SpeakerTag?.FaceFX_Female
            : dialogueNodePreview.Node.SpeakerTag?.FaceFX_Male;
        ExportEntry animSetExport = animSetEntry switch
        {
            ExportEntry export => export,
            ImportEntry import => EntryImporter.ResolveImport(import, previewActorGesturePackageCache),
            _ => null,
        };
        string lineName = actor.AudioGender == DialoguePreviewAudioGender.Female
            ? dialogueNodePreview.Node.FaceFX_Female
            : dialogueNodePreview.Node.FaceFX_Male;
        if (assetExport is null || animSetExport is null || string.IsNullOrWhiteSpace(lineName))
        {
            return;
        }

        FaceFXAnimSet animSet = animSetExport.GetBinaryData<FaceFXAnimSet>();
        FaceFXLine line = animSet.Lines.FirstOrDefault(candidate =>
            candidate.NameAsString.Equals(lineName, StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            return;
        }

        dialoguePreviewFaceFxAnimSets[actor] = animSet;
        animationState.SetFaceFx(assetExport.GetBinaryData<FaceFXAsset>(), animSet, line, dialogueNodePreview.VoStartTime);
        UpdatePreviewActorSkinning(actor);
    }

    private bool IsDialogueNodeSpeaker(PreviewActorConfiguration actor) =>
        dialogueNodePreview?.Node.SpeakerTag is { } speaker
        && actor.ActorTag.Equals(speaker.SpeakerName, StringComparison.OrdinalIgnoreCase);

    private void BuildActorDirectionTracks()
    {
        actorDirectionTracks.Clear();
        ExportEntry interpData = dialogueNodePreview?.Node.InterpData;
        if (interpData is null)
        {
            return;
        }

        foreach (ExportEntry group in GetReferencedExports(interpData, "InterpGroups"))
        {
            PreviewActorConfiguration actor = previewActors
                .Select(candidate => new { Actor = candidate, Score = GetActorGroupMatchScore(group, candidate.ActorTag) })
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .Select(candidate => candidate.Actor)
                .FirstOrDefault();
            if (actor is null)
            {
                continue;
            }

            foreach (ExportEntry track in GetReferencedExports(group, "InterpTracks")
                         .Where(track => track.IsA("BioEvtSysTrackSetFacing") || track.IsA("BioEvtSysTrackLookAt")))
            {
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
                    string targetActorTag = FindDirectionTargetActor(data[index], actor.ActorTag);
                    if (targetActorTag is not null)
                    {
                        keys.Add(new ActorDirectionKey(
                            times[index].GetProp<FloatProperty>("fTime")?.Value ?? 0,
                            targetActorTag));
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

    private string FindDirectionTargetActor(StructProperty keyData, string sourceActorTag)
    {
        IEnumerable<string> candidates = keyData.Properties.OfType<NameProperty>()
            .Select(property => property.Value.Instanced)
            .Concat(keyData.Properties.OfType<StrProperty>().Select(property => property.Value));
        return candidates.FirstOrDefault(candidate => !string.Equals(candidate, sourceActorTag,
                                                        StringComparison.OrdinalIgnoreCase)
                                                    && previewActors.Any(actor => string.Equals(actor.ActorTag,
                                                        candidate, StringComparison.OrdinalIgnoreCase)));
    }

    private void ApplyActorDirectionTracks(float time)
    {
        foreach (ActorDirectionTrack track in actorDirectionTracks)
        {
            ActorDirectionKey key = track.Keys.LastOrDefault(candidate => candidate.Time <= time);
            PreviewActorConfiguration target = key is null
                ? null
                : previewActors.FirstOrDefault(actor => string.Equals(actor.ActorTag, key.TargetActorTag,
                    StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                continue;
            }

            Vector3 direction = target.Origin.Location - track.Actor.Origin.Location;
            if (direction.LengthSquared() <= float.Epsilon)
            {
                continue;
            }
            float yaw = MathF.Atan2(direction.Y, direction.X) * (180f / MathF.PI);
            Vector3 rotation = track.Actor.Origin.Rotation;
            if (track.IsLookAt)
            {
                float horizontalDistance = MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
                rotation.Y = MathF.Atan2(direction.Z, horizontalDistance) * (180f / MathF.PI);
            }
            rotation.Z = yaw;
            track.Actor.Origin = new CameraOrigin(track.Actor.Origin.Location, rotation);
        }
    }

    private static int GetGestureActorMatchScore(GestureTrackOption gesture, string actorTag)
    {
        string findActor = gesture.Track?.GetProperty<NameProperty>("m_nmFindActor")?.Value.Instanced;
        int score = GetActorGroupMatchScore(gesture.Group, actorTag);
        if (string.Equals(findActor, actorTag, StringComparison.OrdinalIgnoreCase))
        {
            score += 4;
        }
        return score;
    }

    private void SetPreviewActorStatus(string status)
    {
        PreviewActorStatusTextBlock.Text = status;
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

        string gamePath = MEDirectories.GetDefaultGamePath(CurrentLoadedExport.Game);
        if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
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

            (string fileName, string contentDir) = previewAssetFiles[usage.FileKey];
            string filePath = Directory.EnumerateFiles(gamePath, $"{fileName}.*", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.Contains(contentDir, StringComparison.OrdinalIgnoreCase));
            if (filePath is null)
            {
                continue;
            }

            using IMEPackage meshPackage = MEPackageHandler.OpenMEPackage(filePath);
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
        foreach (DialogueNodePreviewActor previewActor in dialogueNodePreview.Actors
                     .Where(actor => !string.IsNullOrWhiteSpace(actor.ActorTag))
                     .DistinctBy(actor => actor.ActorTag, StringComparer.OrdinalIgnoreCase))
        {
            bool isPlayer = string.Equals(previewActor.ActorTag, "player", StringComparison.OrdinalIgnoreCase);
            previewActors.Add(new PreviewActorConfiguration
            {
                ActorTag = previewActor.ActorTag,
                DisplayName = previewActor.ActorTag,
                BaseGameModelsOnly = isPlayer,
                ModelName = isPlayer ? "HMF_ARM_CTHb_MDL" : PreviewActorModelDefaults.BodyMeshName,
                HeadModelName = isPlayer ? "HMF_HED_PROShepard_MDL" : PreviewActorModelDefaults.HeadMeshName,
                HairModelName = isPlayer ? "HMF_HIR_PROShepard_MDL" : PreviewActorModelDefaults.HairMeshName,
                AudioGender = isPlayer ? DialoguePreviewAudioGender.Female : DialoguePreviewAudioGender.Male,
                FaceFxAssetName = "SFX_HumanFemale_FaceFX",
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

    private void AssignDialoguePreviewTrackMoves()
    {
        previewActorTrackAssignments.Clear();
        foreach (PreviewActorConfiguration actor in previewActors)
        {
            TrackMovePlaybackOption trackMove = availableTrackMoves
                .Select(option => new { Option = option, Score = GetActorGroupMatchScore(option.Group, actor.ActorTag) })
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .Select(candidate => candidate.Option)
                .FirstOrDefault();
            if (trackMove is not null)
            {
                previewActorTrackAssignments[actor] = trackMove;
            }
        }
    }

    private static int GetActorGroupMatchScore(ExportEntry group, string actorTag)
    {
        if (group is null || string.IsNullOrWhiteSpace(actorTag))
        {
            return 0;
        }

        bool groupNameMatches = string.Equals(GetInterpGroupName(group), actorTag,
            StringComparison.OrdinalIgnoreCase);
        bool findActorMatches = string.Equals(
            group.GetProperty<NameProperty>("m_nmSFXFindActor")?.Value.Instanced, actorTag,
            StringComparison.OrdinalIgnoreCase);
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
    }

    private void RightClickHitProxy(IHitProxy hitProxy)
    {
        if (hitProxy is CurveEditor3DKeyframe keyframe)
        {
            SelectedKeyframe = keyframe;
            ShowKeyframeContextMenu(SceneViewer);
        }
        else
        {
            ShowViewportContextMenu(SceneViewer);
        }
    }

    private void IgnoreActorSelection(ActorProxy actor)
    {
        RenderContext.TransformWidget.Attach = previewActorWidgetActive ? previewActorWidgetTarget : SelectedKeyframe;
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
            ActiveModel.TranslateAllKeyframes(locationDelta);
        }
        else
        {
            SelectedKeyframe.Location += locationDelta;
            SnapCameraToKey(SelectedKeyframe, focusViewport: false);
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
            ActiveModel.RotateAllKeyframes(rotationDeltaVector);
        }
        else
        {
            SelectedKeyframe.SetRotation(SelectedKeyframe.Rotation + rotationDeltaVector, commit: true);
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
            nameof(CurveEditor3DKeyframe.Pitch) => SelectedKeyframe?.Pitch ?? 0,
            nameof(CurveEditor3DKeyframe.Roll) => SelectedKeyframe?.Roll ?? 0,
            nameof(CurveEditor3DKeyframe.Yaw) => SelectedKeyframe?.Yaw ?? 0,
            "All" => SelectedKeyframe?.Pitch ?? 0,
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
            : GetViewportKeyframeLocation(viewportPoint, selectedPreviewActor.Origin.Location);
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

    private Vector3 GetViewportKeyframeLocation(Point viewportPoint, Vector3? depthReference = null)
    {
        if (depthReference is null && ActiveModel.Keyframes.Count == 0)
        {
            return RenderContext.Camera.Position + RenderContext.Camera.CameraForward * 100f;
        }

        Vector3 referenceLocation = depthReference ?? ActiveModel.Keyframes[^1].Location;
        float width = MathF.Max(RenderContext.Width, 1f);
        float height = MathF.Max(RenderContext.Height, 1f);
        float normalizedX = ((float)viewportPoint.X / width * 2f) - 1f;
        float normalizedY = 1f - ((float)viewportPoint.Y / height * 2f);
        Vector3 forward = RenderContext.Camera.CameraForward;
        Vector3 right = RenderContext.Camera.CameraRight;
        Vector3 up = RenderContext.Camera.CameraUp;
        Vector3 cameraPosition = RenderContext.Camera.Position;

        if (RenderContext.Camera.IsOrthographic)
        {
            return cameraPosition
                   + (right * (normalizedX * RenderContext.Camera.OrthoWidth * 0.5f))
                   + (up * (normalizedY * RenderContext.Camera.OrthoWidth / MathF.Max(RenderContext.Camera.aspect, float.Epsilon) * 0.5f))
                   + (forward * Vector3.Dot(referenceLocation - cameraPosition, forward));
        }

        float halfHeightAtUnitDepth = MathF.Tan(RenderContext.Camera.FOV * 0.5f);
        Vector3 rayDirection = Vector3.Normalize(forward + right * normalizedX * halfHeightAtUnitDepth * RenderContext.Camera.aspect + up * normalizedY * halfHeightAtUnitDepth);
        float denominator = Vector3.Dot(rayDirection, forward);
        if (MathF.Abs(denominator) < 0.0001f)
        {
            return referenceLocation;
        }

        float distance = Vector3.Dot(referenceLocation - cameraPosition, forward) / denominator;
        if (distance <= 0f || float.IsNaN(distance) || float.IsInfinity(distance))
        {
            return referenceLocation;
        }

        return cameraPosition + rayDirection * distance;
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
        const float degreesToRadians = 0.017453292519943295f;
        const float cameraDistance = 150f;
        RenderContext.Camera.Roll = keyframe.Roll * degreesToRadians;
        RenderContext.Camera.Pitch = keyframe.Pitch * degreesToRadians;
        RenderContext.Camera.Yaw = keyframe.Yaw * degreesToRadians;
        RenderContext.Camera.Position = keyframe.Location - RenderContext.Camera.CameraForward * cameraDistance;
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
                TrackStartOrigin = GetTrackStartOrigin(trackMove),
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
                resumeDialogueTimelineAfterBranch = dialogueBranchOptions.Count > 0;
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
            ApplyDialogueTimelineAtTime(0, reconstruct: true);
        }

        StartDialogueTimelinePlayback();
    }

    private void StartDialogueTimelinePlayback()
    {
        isPlayingDialogueTimeline = true;
        if (FindName("DialogueTimelinePlayButton") is Button playButton)
        {
            playButton.Content = "Pause";
        }
        RenderContext.ForceContinuousRendering = true;
        SceneViewer?.MarkRenderDirty();
        SceneViewer.Focus();
    }

    private void DialogueTimelineRewind_Click(object sender, RoutedEventArgs e)
    {
        PauseDialogueTimeline();
        ApplyDialogueTimelineAtTime(0, reconstruct: true);
    }

    private void DialogueTimelineSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        PauseDialogueTimeline();

    private void DialogueTimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!updatingDialogueTimelineSlider && isDialogueConversationPreview)
        {
            ApplyDialogueTimelineAtTime((float)e.NewValue, reconstruct: e.NewValue < dialogueTimelineCurrentTime);
        }
    }

    private void DialogueTimelineNode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DialogueTimelineSegment segment })
        {
            PauseDialogueTimeline();
            ApplyDialogueTimelineAtTime(segment.StartTime, reconstruct: segment.StartTime < dialogueTimelineCurrentTime);
        }
    }

    private void DialogueBranchChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DialogueBranchOption option }
            || dialogueTimelineSegments.FirstOrDefault()?.Node is not { } startNode)
        {
            return;
        }

        bool resume = resumeDialogueTimelineAfterBranch;
        dialogueBranchSelections[option.BranchKey] = option.TargetReference;
        BuildDialogueTimeline(startNode);
        DialogueTimelineSegment targetSegment = dialogueTimelineSegments.FirstOrDefault(segment => segment.Reference == option.TargetReference);
        ApplyDialogueTimelineAtTime(targetSegment?.StartTime ?? dialogueTimelineCurrentTime, reconstruct: false);
        resumeDialogueTimelineAfterBranch = false;
        if (resume)
        {
            isPlayingDialogueTimeline = true;
            if (FindName("DialogueTimelinePlayButton") is Button playButton)
            {
                playButton.Content = "Pause";
            }
            RenderContext.ForceContinuousRendering = true;
        }
    }

    private void ApplyDialogueTimelineAtTime(float globalTime, bool reconstruct)
    {
        if (dialogueTimelineSegments.Count == 0)
        {
            return;
        }

        float endTime = GetDialogueTimelineEndTime();
        globalTime = Math.Clamp(globalTime, 0, endTime);
        DialogueTimelineSegment target = dialogueTimelineSegments
            .FirstOrDefault(segment => globalTime < segment.EndTime)
            ?? dialogueTimelineSegments[^1];

        if (reconstruct)
        {
            activeDialogueTimelineSegment = null;
            foreach (DialogueNodePreviewActor actor in dialogueNodePreview.Actors)
            {
                PreviewActorConfiguration configuredActor = previewActors.FirstOrDefault(candidate =>
                    string.Equals(candidate.ActorTag, actor.ActorTag, StringComparison.OrdinalIgnoreCase));
                if (configuredActor is not null)
                {
                    configuredActor.Origin = actor.Origin;
                }
            }

            foreach (DialogueTimelineSegment segment in dialogueTimelineSegments.TakeWhile(segment => segment.StartTime < target.StartTime))
            {
                ActivateDialogueTimelineSegment(segment);
                ApplyPlaybackAtTime(segment.Duration);
            }
        }

        ActivateDialogueTimelineSegment(target);
        float localTime = Math.Clamp(globalTime - target.StartTime, 0, target.Duration);
        ApplyPlaybackAtTime(localTime);
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

        if (activeDialogueTimelineSegment is not null
            && segment.StartTime >= activeDialogueTimelineSegment.EndTime)
        {
            ApplyPlaybackAtTime(activeDialogueTimelineSegment.Duration);
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
            LoadExport(trackMove);
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
        activeDialogueTimelineSegment = segment;
    }

    private void PrepareDialogueTimelineActorPlayback()
    {
        playbackActors.Clear();
        foreach (PreviewActorConfiguration actor in previewActors)
        {
            previewActorTrackAssignments.TryGetValue(actor, out TrackMovePlaybackOption trackMove);
            CameraOrigin originalOrigin = actor.Origin;
            playbackActors.Add(new PreviewActorPlaybackState
            {
                Actor = actor,
                TrackMove = trackMove,
                OriginalOrigin = originalOrigin,
                TrackStartOrigin = GetTrackStartOrigin(trackMove),
                MoveFrame = GetTrackMoveFrame(trackMove)
            });
        }
        foreach (PreviewActorPlaybackState state in playbackActors)
        {
            ApplyAssignedGestureToActor(state.Actor);
        }
        isPlayingActor = playbackActors.Count > 0;
        isPlayingMove = false;
        dialoguePreviewAudioStarted = false;
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
        dialoguePreviewAudioStarted = false;
        SceneViewer?.MarkRenderDirty();
    }

    private float GetDialogueTimelineEndTime() => dialogueTimelineSegments.LastOrDefault()?.EndTime ?? 0;

    private void UpdateDialogueTimelineControls()
    {
        if (FindName("DialogueTimelineSlider") is not Slider slider
            || FindName("DialogueTimelineTimeText") is not TextBlock timeText
            || FindName("DialogueBranchChoicePanel") is not Border branchPanel)
        {
            return;
        }
        float endTime = GetDialogueTimelineEndTime();
        updatingDialogueTimelineSlider = true;
        slider.Maximum = endTime;
        slider.Value = Math.Clamp(dialogueTimelineCurrentTime, 0, endTime);
        updatingDialogueTimelineSlider = false;
        timeText.Text = $"{dialogueTimelineCurrentTime:0.00} / {endTime:0.00}";
        branchPanel.Visibility = dialogueBranchOptions.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyPlaybackAtTime(float time)
    {
        playbackCurrentTime = time;
        if (isPlayingActor)
        {
            ApplyActorsAtTime(time);
            UpdateDialoguePreviewAudio(time);
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
                }
            }
        }
    }

    private TrackMovePlaybackOption GetPlaybackCameraOption(float time)
    {
        if (playDirectorMulticam && selectedDirectorPlayback?.Cuts is { Count: > 0 } cuts)
        {
            DirectorCameraCut cut = cuts[0];
            foreach (DirectorCameraCut candidate in cuts)
            {
                if (candidate.Time > time)
                {
                    break;
                }

                cut = candidate;
            }

            return cut.Camera;
        }

        return availableTrackMoves.FirstOrDefault(option => ReferenceEquals(option.Model, ActiveModel))
               ?? primaryTrackMove;
    }

    private static CameraOrigin EvaluateTrackMove(CurveEditor3DModel trackModel, float time)
    {
        Vector3 location = trackModel.PositionTrack?.Eval(time, Vector3.Zero) ?? Vector3.Zero;
        Vector3 rotation = trackModel.RotationTrack?.Eval(time, Vector3.Zero) ?? Vector3.Zero;
        return new CameraOrigin(location, rotation);
    }

    private static EInterpTrackMoveFrame GetTrackMoveFrame(TrackMovePlaybackOption trackMove) =>
        trackMove?.TrackMove?.GetProperty<EnumProperty>("MoveFrame")
            .GetEnumValOrDefault(EInterpTrackMoveFrame.IMF_World)
        ?? EInterpTrackMoveFrame.IMF_World;

    private static CameraOrigin GetTrackStartOrigin(TrackMovePlaybackOption trackMove) =>
        trackMove?.Model?.Keyframes is { Count: > 0 } keys
            ? EvaluateTrackMove(trackMove.Model, keys[0].Time)
            : default;

    private CameraOrigin ResolveActorTrackOrigin(PreviewActorPlaybackState state, CameraOrigin trackOrigin)
    {
        CameraOrigin origin = state.MoveFrame switch
        {
            EInterpTrackMoveFrame.IMF_RelativeToInitial => ComposeRelativeOrigin(state.OriginalOrigin, trackOrigin),
            _ => trackOrigin
        };
        Vector3 location = origin.Location;
        if (ActorPlaybackTrackZCheckBox.IsChecked != true)
        {
            location.Z = state.OriginalOrigin.Location.Z;
        }
        return new CameraOrigin(location, origin.Rotation);
    }

    private static CameraOrigin ComposeRelativeOrigin(CameraOrigin basis, CameraOrigin relative)
    {
        Quaternion rotation = Rotator.FromDegreesVector(basis.Rotation).ToQuaternion();
        Vector3 location = basis.Location + Vector3.Transform(relative.Location, rotation);
        return new CameraOrigin(location, basis.Rotation + relative.Rotation);
    }

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
        CurveEditor3DModel cameraModel = camera?.Model ?? ActiveModel;
        ApplyViewportCameraOrigin(EvaluateTrackMove(cameraModel, time));
        RenderContext.Camera.FOV = (camera?.FovTrack?.Eval(time, defaultFovDegrees) ?? defaultFovDegrees)
                                   * degreesToRadians;
    }

    private void UpdateAdditionalCameraPlayback(float time)
    {
        playbackCurrentTime = time;
    }

    private void ApplyCameraAtTime(float time)
    {
        ApplyViewportCameraAtTime(GetPlaybackCameraOption(time), time);
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

        foreach (PreviewActorPlaybackState state in playbackActors)
        {
            if (state.TrackMove?.Model?.Keyframes is { Count: > 0 } actorKeys)
            {
                float actorTrackTime = Math.Clamp(time, actorKeys[0].Time, actorKeys[^1].Time);
                CameraOrigin trackOrigin = EvaluateTrackMove(state.TrackMove.Model, actorTrackTime);
                state.Actor.Origin = ResolveActorTrackOrigin(state, trackOrigin);
            }
            if (ReferenceEquals(selectedPreviewActor, state.Actor))
            {
                updatingPreviewActorControls = true;
                SetPreviewActorOriginFields(state.Actor.Origin);
                UpdatePreviewActorRotationDialIndicator();
                updatingPreviewActorControls = false;
            }
            if (previewActorAnimationStates.TryGetValue(state.Actor, out PreviewActorAnimationState animationState)
                && animationState.HasTimeline)
            {
                animationState.SetTime(time);
                UpdatePreviewActorSkinning(state.Actor);
            }
        }
        ApplyActorDirectionTracks(time);
        ApplyActorPlaybackCameraAtTime(time);
        PlaybackKeyframeStatus = GetPlaybackKeyframeStatus(time);
        string cameraMode = playDirectorMulticam ? " with director multicam" : playExtraTrackMove ? " with extra camera" : string.Empty;
        SceneStatus = $"Playing {playbackActors.Count} actor(s){cameraMode} at InVal {time:0.###} / {playbackEndTime:0.###}; {levelPaths.Count} level backdrop file(s).";
        SceneViewer.MarkRenderDirty();
    }

    private void ApplyActorPlaybackCameraAtTime(float time)
    {
        if (playDirectorMulticam)
        {
            ApplyViewportCameraAtTime(GetPlaybackCameraOption(time), time);
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
            dialoguePreviewAudioStarted = false;
            RestorePlaybackActorOrigins();
        }
        playbackActors.Clear();
        if (previewActorWidgetActive && selectedPreviewActor is not null)
        {
            previewActorWidgetTarget.SetTransform(selectedPreviewActor.Origin);
        }
        RenderContext.TransformWidget.Attach = previewActorWidgetActive ? previewActorWidgetTarget : SelectedKeyframe;
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

    private void KeyframeTrackMoveTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingKeyframeTrackTabs)
        {
            ActivateSelectedTrackMove();
        }
    }

    private void ActivateSelectedTrackMove()
    {
        StopPlayback(false);
        UnregisterKeyframes();
        trajectorySamplesDirty = true;
        KeyframeList.ItemsSource = ActiveModel.Keyframes;
        RegisterKeyframes();
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
        StopPlayback();
        UpdatePlaybackButton();
        trajectorySamplesDirty = true;
        RefreshKeyframePanel();
        SceneViewer?.MarkRenderDirty();
    }

    private void RenderScene(object sender, EventArgs e)
    {
        foreach (RenderPass pass in RenderPasses)
        {
            foreach (ActorProxy actor in RenderContext.DrawList_3D)
            {
                if (actor.IsVolume && !ShowVolumes) continue;
                if (actor.IsVolumetricMesh && !ShowVolumetrics) continue;
                int hitId = actor.HitID;
                RenderContext.CurrentHitTestId = new Vector3((hitId & 0xFF) / 255f, ((hitId >> 8) & 0xFF) / 255f, ((hitId >> 16) & 0xFF) / 255f);
                actor.Render(RenderContext, pass);
            }
        }

        if (!isPlayingMove && !isPlayingActor && !isPlayingDialogueTimeline)
        {
            DrawTrajectory(ActiveModel);
        }
        RenderPreviewActors();
        RenderContext.DrawUI();
    }

    private void RenderPreviewActors()
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
            Matrix4x4 transform = CreatePreviewActorTransform(previewActors[actorIndex].Origin);
            foreach (ActorModelSet.Component component in actorModels.Components)
            {
                ModelPreview<WorldVertex> actorModel = component.Model;
                actorModel.UpdateLocalToWorld(transform);
                actorModel.Render(RenderPass.Base, RenderContext, 0);
                actorModel.Render(RenderPass.Hair, RenderContext, 0);
            }
        }
    }

    private static Matrix4x4 CreatePreviewActorTransform(CameraOrigin transform)
    {
        return Matrix4x4.CreateTranslation(0, 0, PreviewBodyMeshRelativeZ)
               * Rotator.FromDegreesVector(transform.Rotation).ToRotationMatrix()
               * Matrix4x4.CreateTranslation(transform.Location);
    }

    private void DrawTrajectory(CurveEditor3DModel activeModel)
    {
        IReadOnlyList<Vector3> samples = GetTrajectorySamples(activeModel);
        Vector4 pathColor = new(1f, 0.65f, 0.05f, 1f);
        for (int i = 1; i < samples.Count; i++)
        {
            RenderContext.Primitives.AddLine(samples[i - 1], samples[i], pathColor, 0);
        }

        Vector4 connectorColor = new(1f, 0.85f, 0.2f, 1f);
        for (int i = 1; i < activeModel.Keyframes.Count; i++)
        {
            RenderContext.Primitives.AddLine(activeModel.Keyframes[i - 1].Location, activeModel.Keyframes[i].Location, connectorColor, 0);
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
        Vector3 position = keyframe.Location;
        Vector4 markerColor = keyframe == SelectedKeyframe ? new Vector4(1f, 1f, 1f, 1f) : new Vector4(1f, 0.8f, 0.1f, 1f);
        Quaternion orientation = Rotator.FromDegreesVector(keyframe.Rotation).ToQuaternion();
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

    private void RegisterKeyframes()
    {
        registeredKeyframeModel = ActiveModel;
        foreach (CurveEditor3DKeyframe keyframe in registeredKeyframeModel.Keyframes)
        {
            RenderContext.AddHitProxy(keyframe);
        }
    }

    private void UnregisterKeyframes()
    {
        if (registeredKeyframeModel is null)
        {
            return;
        }

        foreach (CurveEditor3DKeyframe keyframe in registeredKeyframeModel.Keyframes)
        {
            RenderContext.RemoveHitProxy(keyframe);
        }
        registeredKeyframeModel = null;
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

        foreach (CurveEditor3DKeyframe keyframe in ActiveModel.Keyframes)
        {
            keyframe.HitID = 0;
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
    {
        if (actorIndex < 0 || skeletalMeshExport is null)
        {
            return;
        }

        SkeletalMesh skeletalMesh = skeletalMeshExport.GetBinaryData<SkeletalMesh>();
        ModelPreview<WorldVertex> modelPreview = new(RenderContext, skeletalMesh);
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
        previewActorModels[actorIndex].Set(component, modelPreview, componentRenderer);
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
            RenderContext.TransformWidget.Attach = SelectedKeyframe;
            SceneViewer.MarkRenderDirty();
            return;
        }
        previewActorWidgetTarget.SetTransform(previewActors[actorIndex].Origin);
        previewActorWidgetActive = true;
        RenderContext.TransformWidget.Attach = isPlayingMove ? null : previewActorWidgetTarget;
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
        DialoguePreviewAudioGenderComboBox.SelectedItem = selectedPreviewActor.AudioGender;
        DialoguePreviewFaceFxAssetComboBox.Text = selectedPreviewActor.FaceFxAssetName;
        if (dialogueNodePreview is not null)
        {
            LoadDialoguePreviewAudio(selectedPreviewActor);
        }
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

    private void DialoguePreviewAudioGender_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingPreviewActorControls && selectedPreviewActor is not null
            && DialoguePreviewAudioGenderComboBox.SelectedItem is DialoguePreviewAudioGender gender)
        {
            selectedPreviewActor.AudioGender = gender;
            LoadDialoguePreviewAudio(selectedPreviewActor);
            ApplyDialoguePreviewFaceFx(selectedPreviewActor);
            if (isPlayingActor && IsDialogueNodeSpeaker(selectedPreviewActor))
            {
                DialoguePreviewSoundpanel.StopPlaying();
                dialoguePreviewAudioStarted = false;
                UpdateDialoguePreviewAudio(playbackCurrentTime);
            }
        }
    }

    private void DialoguePreviewFaceFxAsset_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyDialoguePreviewFaceFxAssetSelection();

    private void DialoguePreviewFaceFxAsset_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        ApplyDialoguePreviewFaceFxAssetSelection();

    private void ApplyDialoguePreviewFaceFxAssetSelection()
    {
        if (!updatingPreviewActorControls && selectedPreviewActor is not null
            && !string.IsNullOrWhiteSpace(DialoguePreviewFaceFxAssetComboBox.Text))
        {
            selectedPreviewActor.FaceFxAssetName = DialoguePreviewFaceFxAssetComboBox.Text.Trim();
            ApplyDialoguePreviewFaceFx(selectedPreviewActor);
            if (isPlayingActor && previewActorAnimationStates.TryGetValue(selectedPreviewActor, out PreviewActorAnimationState state))
            {
                state.SetTime(playbackCurrentTime);
                UpdatePreviewActorSkinning(selectedPreviewActor);
            }
        }
    }

    private void StartDialoguePreviewAudio(float time)
    {
        PreviewActorConfiguration speakingActor = previewActors.FirstOrDefault(IsDialogueNodeSpeaker);
        if (speakingActor is null)
        {
            return;
        }

        LoadDialoguePreviewAudio(speakingActor);
        DialoguePreviewSoundpanel.StopPlaying();
        float audioTime = Math.Max(0, time - dialogueNodePreview.VoStartTime);
        DialoguePreviewSoundpanel.StartOrPausePlaying(audioTime);
        dialoguePreviewAudioStarted = true;
    }

    private void UpdateDialoguePreviewAudio(float time)
    {
        if (dialogueNodePreview is null || dialoguePreviewAudioStarted || time < dialogueNodePreview.VoStartTime)
        {
            return;
        }

        StartDialoguePreviewAudio(time);
    }

    private void LoadDialoguePreviewAudio(PreviewActorConfiguration actor)
    {
        ExportEntry audio = actor.AudioGender == DialoguePreviewAudioGender.Female
            ? dialogueNodePreview?.Node.WwiseStream_Female
            : dialogueNodePreview?.Node.WwiseStream_Male;
        if (audio is null)
        {
            DialoguePreviewSoundpanel.UnloadExport();
        }
        else
        {
            DialoguePreviewSoundpanel.LoadExport(audio);
        }
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
        foreach (TrackMovePlaybackOption trackMove in availableTrackMoves)
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
        }
        else
        {
            previewActorTrackAssignments[selectedPreviewActor] = trackMove;
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
        if (updatingPreviewActorControls || selectedPreviewActor is null)
        {
            return;
        }

        if (PreviewActorGestureComboBox.SelectedItem is GestureTrackOption { Track: not null } gesture)
        {
            previewActorGestureAssignments[selectedPreviewActor] = gesture;
        }
        else
        {
            previewActorGestureAssignments.Remove(selectedPreviewActor);
        }

        ApplyAssignedGestureToActor(selectedPreviewActor);
        UpdatePreviewActorGestureStatus();
        SceneViewer.MarkRenderDirty();
    }

    private void ApplyAssignedGestureToActor(PreviewActorConfiguration actor)
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

        animationState.SetTimeline(gesture.StartingPose, gesture.Timeline, previewActorGesturePackageCache);
        UpdatePreviewActorSkinning(actor);
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
        updatingPreviewActorControls = true;
        SetPreviewActorOriginFields(origin);
        UpdatePreviewActorRotationDialIndicator();
        updatingPreviewActorControls = false;
        SavePreviewActorLayout();
        SceneViewer.MarkRenderDirty();
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
