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
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.SharpDX;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Animation;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Newtonsoft.Json;
using System.Windows.Threading;
using CameraOrigin = LegendaryExplorer.Tools.InterpEditor.CameraOrigin;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public sealed partial class CurveEditor3D : ExportLoaderControl, IActorEditorContext, ISceneRenderContextConfigurable
{
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

    private sealed class GestureTrackOption
    {
        public static readonly GestureTrackOption None = new() { DisplayName = "None" };

        public string DisplayName { get; init; }
        public ExportEntry Track { get; init; }
        public IReadOnlyList<GesturePreviewExportLoader.GestureAnimationItem> Animations { get; init; } = [];
        public IReadOnlyList<AnimationPreviewControl.AnimationTimelineClip> Timeline { get; init; } = [];
        public string Status { get; init; }
        public bool HasResolvedTimeline => Timeline.Count > 0;
    }

    private sealed class PreviewActorAnimationState
    {
        public SkeletalMesh SkeletalMesh { get; init; }
        public SkinnedMeshRenderer Renderer { get; init; }
        public AnimSequencePlayer Player { get; init; }
        public bool HasTimeline => Player?.HasAnimation == true;

        public void SetTimeline(IEnumerable<AnimationPreviewControl.AnimationTimelineClip> timeline, PackageCache packageCache)
        {
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

            Player.SetAnimationTimeline(scheduledClips, packageCache);
            Renderer.NeedsUpdate = true;
        }

        public void SetTime(float time)
        {
            Player.SetCurrentTime(time);
            Renderer.NeedsUpdate = true;
        }

        public void Clear()
        {
            Player.SetAnimation(null);
            Renderer.NeedsUpdate = true;
        }
    }

    private sealed class PreviewActorConfiguration
    {
        public string DisplayName { get; set; }
        public string ModelName { get; set; }
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

    private static readonly RenderPass[] RenderPasses = [RenderPass.Base, RenderPass.Hair];
    private static readonly object sessionLevelPathsLock = new();
    private static readonly List<string> sessionLevelPaths = [];
    private static IMEPackage sessionSourcePackage;

    private readonly CurveEditor3DModel model = new();
    private readonly List<IMEPackage> levelPackages = [];
    private readonly List<ActorProxy> levelActors = [];
    private readonly List<string> levelPaths = [];
    private readonly ObservableCollection<PreviewActorConfiguration> previewActors = [];
    private readonly List<ModelPreview<WorldVertex>> previewActorModels = [];
    private readonly PreviewActorWidgetTarget previewActorWidgetTarget = new();
    private readonly PackageCache previewActorGesturePackageCache = new();
    private readonly ObservableCollection<GestureTrackOption> availableGestureTracks = [];
    private readonly Dictionary<PreviewActorConfiguration, GestureTrackOption> previewActorGestureAssignments = [];
    private readonly Dictionary<PreviewActorConfiguration, PreviewActorAnimationState> previewActorAnimationStates = [];
    private AssetDB previewAssetDatabase;
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
    private bool sessionLevelsRestored;
    private bool trajectorySamplesDirty;
    private Button playMoveButton;
    private Button playActorButton;
    private PreviewActorConfiguration playbackActor;
    private CameraOrigin playbackActorOriginalOrigin;
    private bool hasPlaybackActorOriginalOrigin;
    private CurveEditor3DKeyframe selectedKeyframe;
    private string currentExportName;
    private string sceneStatus = "Select an InterpTrackMove export, then optionally open a level backdrop.";
    private string playbackKeyframeStatus = "Not playing";
    private float playbackStartTime;
    private float playbackEndTime;
    private float playbackElapsed;
    private Vector3 pendingViewportKeyframeLocation;
    private Vector3 pendingViewportSelectedKeyframeLocation;
    private bool showCollision = Settings.LevelEditor_ShowCollision;
    private bool showLightIcons = Settings.LevelEditor_ShowLightIcons;
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

    private string SavedPreviewActorsPath => Path.Combine(AppDirectories.AppDataFolder,
        $"CurveEditor3DPreviewActors_{previewActorGame}.json");

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
        PreviewActorGestureComboBox.ItemsSource = availableGestureTracks;
        ConfigureKeyframeContextMenu();
        SceneViewer.Context = RenderContext;
        RenderContext.EnableTransformWidget();
        previewActorWidgetTarget.TransformChanged = PreviewActorGizmo_TransformChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;
        model.Changed += Model_Changed;
        RenderContext.UpdateScene += UpdatePlayback;
    }

    public LevelEditorRenderContext RenderContext { get; }

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
        RefreshAvailableGestureTracks(exportEntry);
        InitializePreviewActorLayout(exportEntry.Game);
        model.Load(exportEntry);
        trajectorySamplesDirty = true;
        KeyframeList.ItemsSource = model.Keyframes;
        RegisterKeyframes();
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
        availableGestureTracks.Clear();
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
            List<AnimationPreviewControl.AnimationTimelineClip> timeline = GesturePreviewExportLoader
                .BuildPlaybackTimeline(resolvedAnimations);
            string title = gestureTrack.GetProperty<StrProperty>("TrackTitle")?.Value ?? gestureTrack.ObjectName.Instanced;
            string actor = gestureTrack.GetProperty<NameProperty>("m_nmFindActor")?.Value.Instanced ?? "None";
            availableGestureTracks.Add(new GestureTrackOption
            {
                DisplayName = $"{title} ({actor})",
                Track = gestureTrack,
                Animations = animations,
                Timeline = timeline,
                Status = timeline.Count == 0
                    ? $"{title}: no resolved gesture animation timeline."
                    : $"{title}: {resolvedAnimations.Count} resolved animation slot(s).",
            });
        }
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
            PreviewActorComboBox.ItemsSource = meshes;

            string defaultMeshName = GetDefaultPreviewModelName(game);
            for (int actorIndex = 0; actorIndex < previewActors.Count; actorIndex++)
            {
                PreviewActorConfiguration actor = previewActors[actorIndex];
                MeshRecord mesh = meshes.FirstOrDefault(item => string.Equals(item.MeshName,
                    actor.ModelName, StringComparison.OrdinalIgnoreCase))
                    ?? meshes.FirstOrDefault(item => string.Equals(item.MeshName,
                        defaultMeshName, StringComparison.OrdinalIgnoreCase));
                if (mesh is null)
                {
                    continue;
                }
                actor.ModelName = mesh.MeshName;
                TryLoadPreviewActorModel(actorIndex, mesh, out _);
            }
            SynchronizePreviewActorControls();
            SetPreviewActorStatus(meshes.Count == 0
                ? $"The {game} Asset Database contains no skeletal meshes."
                : $"{meshes.Count:N0} skeletal actor models available.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetPreviewActorStatus($"Unable to load actor models: {exception.Message}");
        }
    }

    private void SetPreviewActorStatus(string status)
    {
        PreviewActorStatusTextBlock.Text = status;
    }

    private bool TryLoadPreviewActorModel(int actorIndex, MeshRecord meshRecord, out string error)
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

        foreach (MeshUsage usage in meshRecord.Usages)
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
                LoadPreviewActorModel(actorIndex, meshExport);
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

    private static string GetDefaultPreviewModelName(MEGame game) => game switch
    {
        MEGame.LE1 or MEGame.ME1 => "QRN_FAC_ARM_LGTa_MDL",
        MEGame.LE2 or MEGame.ME2 => "QRN_TLI_LGTa_MDL",
        _ => "QRN_ARM_TLIa_MDL"
    };

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
        previewActorGame = game;
        previewAssetDatabase = null;
        previewAssetFiles = [];
        PreviewActorComboBox.ItemsSource = null;
        LoadPreviewActorLayout();
        _ = InitializePreviewActorModelsAsync();
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
                        actor.ModelName ??= GetDefaultPreviewModelName(previewActorGame);
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
        if (previewActorGame == MEGame.Unknown)
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
            ModelName = GetDefaultPreviewModelName(previewActorGame),
            Origin = origin
        };
        previewActors.Add(actor);
        RenumberPreviewActors();
        PreviewActorListBox.SelectedItem = actor;
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

        if (isPlayingActor && ReferenceEquals(playbackActor, selectedPreviewActor))
        {
            StopPlayback();
        }

        int removedIndex = previewActors.IndexOf(selectedPreviewActor);
        previewActors.RemoveAt(removedIndex);
        previewActorGestureAssignments.Remove(selectedPreviewActor);
        RemovePreviewActorModel(removedIndex);
        RenumberPreviewActors();
        PreviewActorListBox.SelectedIndex = Math.Min(removedIndex, previewActors.Count - 1);
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
        ClearPreviewActorModels();
        RenumberPreviewActors();
        PreviewActorListBox.SelectedIndex = -1;
        SetPreviewActorStatus("Preview actors cleared.");
        SavePreviewActorLayout();
    }

    private void RenumberPreviewActors()
    {
        for (int index = 0; index < previewActors.Count; index++)
        {
            previewActors[index].DisplayName = $"Actor {index + 1}";
        }
        PreviewActorListBox.Items.Refresh();
        RemovePreviewActorButton.IsEnabled = previewActors.Count > 0;
    }

    private void PreviewActorModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingPreviewActorControls || selectedPreviewActor is null
            || sender is not ComboBox { SelectedItem: MeshRecord meshRecord })
        {
            return;
        }

        int actorIndex = previewActors.IndexOf(selectedPreviewActor);
        if (!TryLoadPreviewActorModel(actorIndex, meshRecord, out string error))
        {
            SetPreviewActorStatus(error);
            return;
        }

        selectedPreviewActor.ModelName = meshRecord.MeshName;
        SetPreviewActorStatus($"{selectedPreviewActor.DisplayName}: {meshRecord.MeshName}");
        SavePreviewActorLayout();
        SceneViewer.MarkRenderDirty();
    }

    private void LoadSelectedPreviewActorModel()
    {
        if (selectedPreviewActor is null || PreviewActorComboBox.ItemsSource is not IEnumerable<MeshRecord> meshes)
        {
            return;
        }
        MeshRecord mesh = meshes.FirstOrDefault(item => string.Equals(item.MeshName,
            selectedPreviewActor.ModelName, StringComparison.OrdinalIgnoreCase));
        if (mesh is not null)
        {
            TryLoadPreviewActorModel(previewActors.IndexOf(selectedPreviewActor), mesh, out _);
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
            model.TranslateAllKeyframes(locationDelta);
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
            model.RotateAllKeyframes(rotationDeltaVector);
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

        if (model.HasKeyframeAtTime(inVal, keyframe))
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

        CurveEditor3DKeyframe newKeyframe = model.AddKeyframe(keyframe, inVal.Value);
        if (newKeyframe is null)
        {
            return;
        }

        RenderContext.AddHitProxy(newKeyframe);
        SelectedKeyframe = newKeyframe;
        SceneStatus = $"Added keyframe at InVal {newKeyframe.Time:0.###}; {model.Keyframes.Count} trajectory keyframe(s).";
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

                if (model.HasKeyframeAtTime(value))
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
        float? inVal = PromptForKeyframeInVal(model.Keyframes[^1].Time + 1f);
        if (inVal is null)
        {
            return;
        }

        CurveEditor3DKeyframe newKeyframe = model.AddKeyframeAfterLast(pendingViewportKeyframeLocation, inVal.Value);
        if (newKeyframe is null)
        {
            return;
        }

        RenderContext.AddHitProxy(newKeyframe);
        SelectedKeyframe = newKeyframe;
        SceneStatus = $"Added keyframe at InVal {newKeyframe.Time:0.###}; {model.Keyframes.Count} trajectory keyframe(s).";
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
        SelectedKeyframe = model.DeleteKeyframe(keyframe);
        SceneStatus = $"{model.Keyframes.Count} trajectory keyframe(s); {levelPaths.Count} level backdrop file(s).";
        RefreshKeyframePanel();
        SceneViewer.MarkRenderDirty();
    }

    private void ShiftInterpTrack_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        if (CurrentLoadedExport is null)
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
        PackageEditorExperimentsM.ShiftInterpTrackMove(CurrentLoadedExport, dialog.Parameters);
        ReloadCurrentExport(selectedTime + dialog.Parameters.TimeOffset);
        SceneStatus = $"Shifted {model.Keyframes.Count} trajectory keyframe(s); {levelPaths.Count} level backdrop file(s).";
    }

    private void ApplyPosTrackInterpModeToAll_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        if (SelectedKeyframe is not { } keyframe || model.Keyframes.Count == 0)
        {
            return;
        }

        model.SetAllPosTrackInterpModes(keyframe.PosTrackInterpMode);
        RefreshKeyframePanel();
        trajectorySamplesDirty = true;
        SceneStatus = $"Set PosTrack InterpMode to {keyframe.PosTrackInterpMode} for {model.Keyframes.Count} keyframe(s).";
        SceneViewer.MarkRenderDirty();
    }

    private void ApplyEulerTrackInterpModeToAll_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        if (SelectedKeyframe is not { } keyframe || model.Keyframes.Count == 0)
        {
            return;
        }

        model.SetAllEulerTrackInterpModes(keyframe.EulerTrackInterpMode);
        RefreshKeyframePanel();
        trajectorySamplesDirty = true;
        SceneStatus = $"Set EulerTrack InterpMode to {keyframe.EulerTrackInterpMode} for {model.Keyframes.Count} keyframe(s).";
        SceneViewer.MarkRenderDirty();
    }

    private void ReloadCurrentExport(float preferredSelectionTime)
    {
        UnregisterKeyframes();
        model.Load(CurrentLoadedExport);
        trajectorySamplesDirty = true;
        KeyframeList.ItemsSource = model.Keyframes;
        RegisterKeyframes();
        SelectedKeyframe = model.Keyframes.Count == 0
            ? null
            : model.Keyframes.MinBy(keyframe => MathF.Abs(keyframe.Time - preferredSelectionTime));
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
            IsEnabled = model.Keyframes.Count > 0
        };
        addItem.Click += AddKeyframeAfterLast_Click;
        menu.Items.Add(addItem);
        menu.IsOpen = true;
    }

    private Vector3 GetViewportKeyframeLocation(Point viewportPoint, Vector3? depthReference = null)
    {
        if (depthReference is null && model.Keyframes.Count == 0)
        {
            return RenderContext.Camera.Position + RenderContext.Camera.CameraForward * 100f;
        }

        Vector3 referenceLocation = depthReference ?? model.Keyframes[^1].Location;
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

        if (model.Keyframes.Count == 0)
        {
            return;
        }

        playbackStartTime = model.Keyframes[0].Time;
        playbackEndTime = model.Keyframes[^1].Time;
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
        isPlayingActor = false;
        playbackActor = null;
        hasPlaybackActorOriginalOrigin = false;
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

        if (selectedPreviewActor is null || model.Keyframes.Count == 0)
        {
            return;
        }

        playbackStartTime = model.Keyframes[0].Time;
        playbackEndTime = model.Keyframes[^1].Time;
        playbackActor = selectedPreviewActor;
        playbackActorOriginalOrigin = playbackActor.Origin;
        hasPlaybackActorOriginalOrigin = true;
        if (playbackEndTime <= playbackStartTime)
        {
            ApplyActorAtTime(playbackStartTime);
            RestorePlaybackActorOrigin();
            playbackActor = null;
            hasPlaybackActorOriginalOrigin = false;
            return;
        }

        if (playActorButton is not null)
        {
            playActorButton.Content = "Stop Actor";
        }
        RenderContext.TransformWidget.Attach = null;
        playbackElapsed = 0f;
        isPlayingActor = true;
        isPlayingMove = true;
        RenderContext.ForceContinuousRendering = true;
        ApplyActorAtTime(playbackStartTime);
        SceneViewer.Focus();
    }

    private void UpdatePlayback(object sender, float deltaTime)
    {
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

    private void ApplyPlaybackAtTime(float time)
    {
        if (isPlayingActor)
        {
            ApplyActorAtTime(time);
        }
        else
        {
            ApplyCameraAtTime(time);
        }
    }

    private void ApplyCameraAtTime(float time)
    {
        Vector3 location = model.PositionTrack?.Eval(time, Vector3.Zero) ?? Vector3.Zero;
        Vector3 rotation = model.RotationTrack?.Eval(time, Vector3.Zero) ?? Vector3.Zero;
        const float degreesToRadians = 0.017453292519943295f;
        RenderContext.Camera.Position = location;
        RenderContext.Camera.Roll = rotation.X * degreesToRadians;
        RenderContext.Camera.Pitch = rotation.Y * degreesToRadians;
        RenderContext.Camera.Yaw = rotation.Z * degreesToRadians;
        RenderContext.Camera.FocusDepth = 0f;
        PlaybackKeyframeStatus = GetPlaybackKeyframeStatus(time);
        SceneStatus = $"Playing camera at InVal {time:0.###} / {playbackEndTime:0.###}; {levelPaths.Count} level backdrop file(s).";
        UpdateCameraPositionText();
        UpdateCameraRotationText();
        SceneViewer.MarkRenderDirty();
    }

    private void ApplyActorAtTime(float time)
    {
        if (playbackActor is null)
        {
            return;
        }

        Vector3 location = model.PositionTrack?.Eval(time, Vector3.Zero) ?? Vector3.Zero;
        Vector3 rotation = model.RotationTrack?.Eval(time, Vector3.Zero) ?? Vector3.Zero;
        playbackActor.Origin = new CameraOrigin(location, rotation);
        if (ReferenceEquals(selectedPreviewActor, playbackActor))
        {
            updatingPreviewActorControls = true;
            SetPreviewActorOriginFields(playbackActor.Origin);
            UpdatePreviewActorRotationDialIndicator();
            updatingPreviewActorControls = false;
        }
        if (previewActorAnimationStates.TryGetValue(playbackActor, out PreviewActorAnimationState animationState)
            && animationState.HasTimeline)
        {
            animationState.SetTime(time);
            UpdatePreviewActorSkinning(playbackActor);
        }
        PlaybackKeyframeStatus = GetPlaybackKeyframeStatus(time);
        SceneStatus = $"Playing {playbackActor.DisplayName} at InVal {time:0.###} / {playbackEndTime:0.###}; {levelPaths.Count} level backdrop file(s).";
        SceneViewer.MarkRenderDirty();
    }

    private string GetPlaybackKeyframeStatus(float time)
    {
        int keyframeCount = model.Keyframes.Count;
        if (keyframeCount == 0)
        {
            return "Not playing";
        }

        int currentIndex = 0;
        for (int i = 1; i < keyframeCount; i++)
        {
            if (model.Keyframes[i].Time > time)
            {
                break;
            }

            currentIndex = i;
        }

        CurveEditor3DKeyframe currentKeyframe = model.Keyframes[currentIndex];
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
            playActorButton.Content = "Play Actor on Track";
        }
        if (stoppedActorPlayback && playbackActor is not null)
        {
            RestorePlaybackActorOrigin();
        }
        playbackActor = null;
        hasPlaybackActorOriginalOrigin = false;
        if (previewActorWidgetActive && selectedPreviewActor is not null)
        {
            previewActorWidgetTarget.SetTransform(selectedPreviewActor.Origin);
        }
        RenderContext.TransformWidget.Attach = previewActorWidgetActive ? previewActorWidgetTarget : SelectedKeyframe;
        if (restoreStatus)
        {
            SceneStatus = $"{model.Keyframes.Count} trajectory keyframe(s); {levelPaths.Count} level backdrop file(s).";
        }
        SceneViewer?.MarkRenderDirty();
    }

    private void RestorePlaybackActorOrigin()
    {
        if (playbackActor is null || !hasPlaybackActorOriginalOrigin)
        {
            return;
        }

        playbackActor.Origin = playbackActorOriginalOrigin;
        if (previewActorAnimationStates.TryGetValue(playbackActor, out PreviewActorAnimationState animationState))
        {
            if (animationState.HasTimeline)
            {
                animationState.SetTime(animationState.Player.StartTime);
            }
            else
            {
                animationState.Clear();
            }
            UpdatePreviewActorSkinning(playbackActor);
        }
        if (ReferenceEquals(selectedPreviewActor, playbackActor))
        {
            updatingPreviewActorControls = true;
            SetPreviewActorOriginFields(playbackActorOriginalOrigin);
            UpdatePreviewActorRotationDialIndicator();
            updatingPreviewActorControls = false;
        }
    }

    private void UpdatePlaybackButton()
    {
        if (playMoveButton is not null)
        {
            playMoveButton.IsEnabled = model.Keyframes.Count > 0;
            if (!isPlayingMove)
            {
                playMoveButton.Content = "Play";
            }
        }
        if (playActorButton is not null)
        {
            playActorButton.IsEnabled = model.Keyframes.Count > 0 && selectedPreviewActor is not null;
            if (!isPlayingMove)
            {
                playActorButton.Content = "Play Actor on Track";
            }
        }
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

        if (!isPlayingMove)
        {
            DrawTrajectory();
        }
        RenderPreviewActors();
        RenderContext.DrawUI();
    }

    private void RenderPreviewActors()
    {
        int actorCount = Math.Min(previewActorModels.Count, previewActors.Count);
        for (int actorIndex = 0; actorIndex < actorCount; actorIndex++)
        {
            ModelPreview<WorldVertex> actorModel = previewActorModels[actorIndex];
            if (actorModel is null)
            {
                continue;
            }
            UpdatePreviewActorSkinning(previewActors[actorIndex]);
            actorModel.UpdateLocalToWorld(CreatePreviewActorTransform(previewActors[actorIndex].Origin));
            actorModel.Render(RenderPass.Base, RenderContext, 0);
            actorModel.Render(RenderPass.Hair, RenderContext, 0);
        }
    }

    private static Matrix4x4 CreatePreviewActorTransform(CameraOrigin transform)
    {
        return Rotator.FromDegreesVector(transform.Rotation).ToRotationMatrix()
               * Matrix4x4.CreateTranslation(transform.Location);
    }

    private void DrawTrajectory()
    {
        IReadOnlyList<Vector3> samples = GetTrajectorySamples();
        Vector4 pathColor = new(1f, 0.65f, 0.05f, 1f);
        for (int i = 1; i < samples.Count; i++)
        {
            RenderContext.Primitives.AddLine(samples[i - 1], samples[i], pathColor, 0);
        }

        Vector4 connectorColor = new(1f, 0.85f, 0.2f, 1f);
        for (int i = 1; i < model.Keyframes.Count; i++)
        {
            RenderContext.Primitives.AddLine(model.Keyframes[i - 1].Location, model.Keyframes[i].Location, connectorColor, 0);
        }

        foreach (CurveEditor3DKeyframe keyframe in model.Keyframes)
        {
            DrawKeyframe(keyframe);
        }
    }

    private IReadOnlyList<Vector3> GetTrajectorySamples()
    {
        if (trajectorySamplesDirty)
        {
            trajectorySamples = model.SampleTrajectory();
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
        foreach (CurveEditor3DKeyframe keyframe in model.Keyframes)
        {
            RenderContext.AddHitProxy(keyframe);
        }
    }

    private void UnregisterKeyframes()
    {
        foreach (CurveEditor3DKeyframe keyframe in model.Keyframes)
        {
            RenderContext.RemoveHitProxy(keyframe);
        }
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
        SceneStatus = $"{model.Keyframes.Count} trajectory keyframe(s); {levelPaths.Count} level backdrop file(s).";
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
            SceneStatus = $"{model.Keyframes.Count} trajectory keyframe(s); {levelPaths.Count} level backdrop file(s).";
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

        foreach (CurveEditor3DKeyframe keyframe in model.Keyframes)
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

    private void LoadPreviewActorModel(int actorIndex, ExportEntry skeletalMeshExport)
    {
        if (actorIndex < 0 || skeletalMeshExport is null)
        {
            return;
        }

        SkeletalMesh skeletalMesh = skeletalMeshExport.GetBinaryData<SkeletalMesh>();
        ModelPreview<WorldVertex> modelPreview = new(RenderContext, skeletalMesh);
        while (previewActorModels.Count <= actorIndex)
        {
            previewActorModels.Add(null);
        }
        previewActorModels[actorIndex]?.Dispose();
        previewActorModels[actorIndex] = modelPreview;
        if (actorIndex < previewActors.Count && skeletalMesh.LODModels.Length > 0)
        {
            var renderer = new SkinnedMeshRenderer();
            renderer.BuildFromSkeletalMesh(skeletalMeshExport.Game, skeletalMesh.LODModels[0]);
            var animationState = new PreviewActorAnimationState
            {
                SkeletalMesh = skeletalMesh,
                Renderer = renderer,
                Player = new AnimSequencePlayer(skeletalMesh),
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
        if (actorIndex < previewActors.Count)
        {
            previewActorAnimationStates.Remove(previewActors[actorIndex]);
        }
        previewActorModels[actorIndex]?.Dispose();
        previewActorModels.RemoveAt(actorIndex);
        SceneViewer.MarkRenderDirty();
    }

    private void ClearPreviewActorModels()
    {
        foreach (ModelPreview<WorldVertex> actorModel in previewActorModels)
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
        SynchronizePreviewActorControls();
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
            || previewActorModels[actorIndex] is not { LODs.Count: > 0 } actorModel)
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
        if (PreviewActorComboBox.ItemsSource is IEnumerable<MeshRecord> meshes)
        {
            PreviewActorComboBox.SelectedItem = meshes.FirstOrDefault(mesh => string.Equals(mesh.MeshName,
                selectedPreviewActor.ModelName, StringComparison.OrdinalIgnoreCase));
        }
        PreviewActorGestureComboBox.Items.Refresh();
        PreviewActorGestureComboBox.SelectedItem = previewActorGestureAssignments.GetValueOrDefault(selectedPreviewActor)
                                                ?? GestureTrackOption.None;
        UpdatePreviewActorGestureStatus();
        UpdatePreviewActorRotationDialIndicator();
        updatingPreviewActorControls = false;
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

        if (!previewActorGestureAssignments.TryGetValue(actor, out GestureTrackOption gesture) || gesture.Timeline.Count == 0)
        {
            animationState.Clear();
            UpdatePreviewActorSkinning(actor);
            return;
        }

        animationState.SetTimeline(gesture.Timeline, previewActorGesturePackageCache);
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
            || previewActorModels[actorIndex] is not { LODs.Count: > 0 } actorModel)
        {
            return;
        }

        if (animationState.Renderer.NeedsUpdate)
        {
            animationState.Renderer.UpdateSkinning(RenderContext.ImmediateContext, actorModel.LODs[0].Mesh, animationState.Player);
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
        selectedPreviewActor.ModelName = GetDefaultPreviewModelName(previewActorGame);
        CameraOrigin origin = SelectedKeyframe is null
            ? new CameraOrigin(RenderContext.Camera.Position, Vector3.Zero)
            : new CameraOrigin(SelectedKeyframe.Location, SelectedKeyframe.Rotation);
        SetSelectedPreviewActorOrigin(origin);
        LoadSelectedPreviewActorModel();
    }
}
