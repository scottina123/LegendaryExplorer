using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorer.Tools.PackageEditor.Experiments;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Newtonsoft.Json;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public sealed partial class CurveEditor3D : ExportLoaderControl, IActorEditorContext
{
    private static readonly RenderPass[] RenderPasses = [RenderPass.Base, RenderPass.Hair];

    private readonly CurveEditor3DModel model = new();
    private readonly List<IMEPackage> levelPackages = [];
    private readonly List<ActorProxy> levelActors = [];
    private readonly List<string> levelPaths = [];
    private IReadOnlyList<Vector3> trajectorySamples = [];
    private bool eventsAttached;
    private bool isPlayingMove;
    private bool trajectorySamplesDirty;
    private Button playMoveButton;
    private CurveEditor3DKeyframe selectedKeyframe;
    private string currentExportName;
    private string sceneStatus = "Select an InterpTrackMove export, then optionally open a level backdrop.";
    private float playbackStartTime;
    private float playbackEndTime;
    private float playbackElapsed;
    private Vector3 pendingViewportKeyframeLocation;

    public CurveEditor3D() : base("3D Curve Editor")
    {
        RenderContext = new LevelEditorRenderContext();
        RenderContext.BackgroundColor = LevelEditor.GetThemeDefaultBackgroundColor();
        InterpModes = Enum.GetValues<EInterpCurveMode>();
        InitializeComponent();
        ConfigureKeyframeContextMenu();
        SceneViewer.Context = RenderContext;
        RenderContext.EnableTransformWidget();
        ThemeManager.ThemeChanged += OnThemeChanged;
        model.Changed += Model_Changed;
        RenderContext.UpdateScene += UpdatePlayback;
    }

    public LevelEditorRenderContext RenderContext { get; }

    public bool IsApplyingUndoRedo => false;

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

    public CurveEditor3DKeyframe SelectedKeyframe
    {
        get => selectedKeyframe;
        private set
        {
            if (SetProperty(ref selectedKeyframe, value))
            {
                SnapToKeyButton.IsEnabled = value is not null;
                KeyframeList.SelectedItem = value;
                if (value is not null)
                {
                    KeyframeList.ScrollIntoView(value);
                }
                RenderContext.TransformWidget.Attach = value;
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    public override bool CanParse(ExportEntry exportEntry)
        => exportEntry?.ClassName == "InterpTrackMove"
           && exportEntry.GetProperty<StructProperty>("PosTrack") is not null
           && exportEntry.GetProperty<StructProperty>("EulerTrack") is not null;

    public override void LoadExport(ExportEntry exportEntry)
    {
        StopPlayback(false);
        UnregisterKeyframes();
        CurrentLoadedExport = exportEntry;
        model.Load(exportEntry);
        trajectorySamplesDirty = true;
        KeyframeList.ItemsSource = model.Keyframes;
        RegisterKeyframes();
        SelectedKeyframe = model.Keyframes.FirstOrDefault();
        CurrentExportName = $"{exportEntry.UIndex}: {exportEntry.InstancedFullPath}";
        SceneStatus = $"{model.Keyframes.Count} trajectory keyframe(s); {levelPaths.Count} level backdrop file(s).";
        UpdatePlaybackButton();
        FocusCameraOnKey(SelectedKeyframe);
        SceneViewer?.MarkRenderDirty();
    }

    public override void UnloadExport()
    {
        StopPlayback(false);
        UnregisterKeyframes();
        model.Clear();
        trajectorySamples = [];
        trajectorySamplesDirty = false;
        KeyframeList.ItemsSource = null;
        SelectedKeyframe = null;
        CurrentLoadedExport = null;
        CurrentExportName = null;
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
        UnloadExport();
        CloseLevels();
        DetachEvents();
        model.Changed -= Model_Changed;
        RenderContext.UpdateScene -= UpdatePlayback;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        KeyframeList.PreviewMouseRightButtonDown -= KeyframeList_PreviewMouseRightButtonDown;
        SceneViewer.Dispose();
    }

    private void ConfigureKeyframeContextMenu()
    {
        KeyframeList.PreviewMouseRightButtonDown += KeyframeList_PreviewMouseRightButtonDown;
        KeyframeList.ContextMenu = CreateKeyframeContextMenu();
    }

    private ContextMenu CreateKeyframeContextMenu()
    {
        var menu = new ContextMenu();

        var addItem = new MenuItem { Header = "Add Keyframe..." };
        addItem.Click += AddKeyframe_Click;
        menu.Items.Add(addItem);

        var deleteItem = new MenuItem { Header = "Delete Keyframe" };
        deleteItem.Click += DeleteKeyframe_Click;
        menu.Items.Add(deleteItem);

        menu.Items.Add(new Separator());

        var shiftItem = new MenuItem { Header = "Shift InterpTrack..." };
        shiftItem.Click += ShiftInterpTrack_Click;
        menu.Items.Add(shiftItem);

        return menu;
    }

    private void OnThemeChanged(object sender, bool isDarkMode)
    {
        RenderContext.BackgroundColor = LevelEditor.GetThemeDefaultBackgroundColor();
        SceneViewer?.MarkRenderDirty();
    }

    private void CurveEditor3D_Loaded(object sender, RoutedEventArgs e)
    {
        AttachEvents();
        SceneViewer.SetShouldRender(true);
        FocusCameraOnKey(SelectedKeyframe ?? model.Keyframes.FirstOrDefault());
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
        RenderContext.TransformWidget.Attach = SelectedKeyframe;
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
        SceneViewer.MarkRenderDirty();
        SceneViewer.Focus();
    }

    private void RotateMode_Click(object sender, RoutedEventArgs e)
    {
        RenderContext.TransformWidget.Mode = EWidgetMode.Rotate;
        SceneViewer.MarkRenderDirty();
        SceneViewer.Focus();
    }

    private void KeyframeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KeyframeList.SelectedItem is CurveEditor3DKeyframe keyframe && keyframe != SelectedKeyframe)
        {
            SelectedKeyframe = keyframe;
        }
    }

    private void AddKeyframe_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        if (SelectedKeyframe is not { } keyframe)
        {
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            "Add keyframe before or after the selected keyframe?\n\nYes = before\nNo = after",
            "Add Keyframe",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        bool addAfter;
        switch (result)
        {
            case MessageBoxResult.Yes:
                addAfter = false;
                break;
            case MessageBoxResult.No:
                addAfter = true;
                break;
            default:
                return;
        }

        CurveEditor3DKeyframe newKeyframe = model.AddKeyframe(keyframe, addAfter);
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

    private void AddKeyframeAfterLast_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        CurveEditor3DKeyframe newKeyframe = model.AddKeyframeAfterLast(pendingViewportKeyframeLocation);
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
        pendingViewportKeyframeLocation = GetViewportKeyframeLocation(Mouse.GetPosition(SceneViewer));
        var menu = new ContextMenu { PlacementTarget = placementTarget };
        var addItem = new MenuItem
        {
            Header = "Add Keyframe After Last",
            IsEnabled = model.Keyframes.Count > 0
        };
        addItem.Click += AddKeyframeAfterLast_Click;
        menu.Items.Add(addItem);
        menu.IsOpen = true;
    }

    private Vector3 GetViewportKeyframeLocation(Point viewportPoint)
    {
        if (model.Keyframes.Count == 0)
        {
            return RenderContext.Camera.Position + RenderContext.Camera.CameraForward * 100f;
        }

        Vector3 lastLocation = model.Keyframes[^1].Location;
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
                   + (forward * Vector3.Dot(lastLocation - cameraPosition, forward));
        }

        float halfHeightAtUnitDepth = MathF.Tan(RenderContext.Camera.FOV * 0.5f);
        Vector3 rayDirection = Vector3.Normalize(forward + right * normalizedX * halfHeightAtUnitDepth * RenderContext.Camera.aspect + up * normalizedY * halfHeightAtUnitDepth);
        float denominator = Vector3.Dot(rayDirection, forward);
        if (MathF.Abs(denominator) < 0.0001f)
        {
            return lastLocation;
        }

        float distance = Vector3.Dot(lastLocation - cameraPosition, forward) / denominator;
        if (distance <= 0f || float.IsNaN(distance) || float.IsInfinity(distance))
        {
            return lastLocation;
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

    private void SnapCameraToKey(CurveEditor3DKeyframe keyframe)
    {
        const float degreesToRadians = 0.017453292519943295f;
        RenderContext.Camera.Position = keyframe.Location;
        RenderContext.Camera.Roll = keyframe.Roll * degreesToRadians;
        RenderContext.Camera.Pitch = keyframe.Pitch * degreesToRadians;
        RenderContext.Camera.Yaw = keyframe.Yaw * degreesToRadians;
        RenderContext.Camera.FocusDepth = 0f;
        SceneViewer.MarkRenderDirty();
        SceneViewer.Focus();
    }

    private void PlayMoveButton_Loaded(object sender, RoutedEventArgs e)
    {
        playMoveButton = (Button)sender;
        UpdatePlaybackButton();
    }

    private void PlayMove_Click(object sender, RoutedEventArgs e)
    {
        if (isPlayingMove)
        {
            StopPlayback();
            return;
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
        isPlayingMove = true;
        RenderContext.ForceContinuousRendering = true;
        ApplyCameraAtTime(playbackStartTime);
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
            ApplyCameraAtTime(playbackEndTime);
            StopPlayback();
            return;
        }

        ApplyCameraAtTime(time);
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
        SceneStatus = $"Playing camera at InVal {time:0.###} / {playbackEndTime:0.###}; {levelPaths.Count} level backdrop file(s).";
        SceneViewer.MarkRenderDirty();
    }

    private void StopPlayback(bool restoreStatus = true)
    {
        if (!isPlayingMove)
        {
            return;
        }

        isPlayingMove = false;
        playbackElapsed = 0f;
        RenderContext.ForceContinuousRendering = false;
        if (playMoveButton is not null)
        {
            playMoveButton.Content = "Play";
        }
        RenderContext.TransformWidget.Attach = SelectedKeyframe;
        if (restoreStatus)
        {
            SceneStatus = $"{model.Keyframes.Count} trajectory keyframe(s); {levelPaths.Count} level backdrop file(s).";
        }
        SceneViewer?.MarkRenderDirty();
    }

    private void UpdatePlaybackButton()
    {
        if (playMoveButton is null)
        {
            return;
        }

        playMoveButton.IsEnabled = model.Keyframes.Count > 0;
        if (!isPlayingMove)
        {
            playMoveButton.Content = "Play";
        }
    }

    private void FocusCameraOnKey(CurveEditor3DKeyframe keyframe)
    {
        if (keyframe is null)
        {
            return;
        }

        Rotator rotator = Rotator.FromDegreesVector(keyframe.Rotation);
        const float degreesToRadians = 0.017453292519943295f;
        RenderContext.Camera.Roll = keyframe.Roll * degreesToRadians;
        RenderContext.Camera.Pitch = keyframe.Pitch * degreesToRadians;
        RenderContext.Camera.Yaw = keyframe.Yaw * degreesToRadians;
        Vector3 forward = rotator.GetDirectionalVector();
        RenderContext.Camera.Position = keyframe.Location - (forward * 600f);
        RenderContext.Camera.FocusDepth = 600f;
        SceneViewer?.MarkRenderDirty();
    }

    private void Model_Changed()
    {
        StopPlayback();
        UpdatePlaybackButton();
        trajectorySamplesDirty = true;
        KeyframeList?.Items.Refresh();
        SceneViewer?.MarkRenderDirty();
    }

    private void RenderScene(object sender, EventArgs e)
    {
        foreach (RenderPass pass in RenderPasses)
        {
            foreach (ActorProxy actor in RenderContext.DrawList_3D)
            {
                int hitId = actor.HitID;
                RenderContext.CurrentHitTestId = new Vector3((hitId & 0xFF) / 255f, ((hitId >> 8) & 0xFF) / 255f, ((hitId >> 16) & 0xFF) / 255f);
                actor.Render(RenderContext, pass);
            }
        }

        if (!isPlayingMove)
        {
            DrawTrajectory();
        }
        RenderContext.DrawUI();
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
        if (dialog.ShowDialog() == true)
        {
            await LoadLevelAsync(dialog.FileName, replace: true).ConfigureAwait(true);
        }
    }

    private async void AddLevel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = AppDirectories.GetOpenPackageDialog();
        if (dialog.ShowDialog() == true)
        {
            await LoadLevelAsync(dialog.FileName, replace: false).ConfigureAwait(true);
        }
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

    private async Task LoadLevelAsync(string path, bool replace)
    {
        try
        {
            if (replace)
            {
                CloseLevels();
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
}
