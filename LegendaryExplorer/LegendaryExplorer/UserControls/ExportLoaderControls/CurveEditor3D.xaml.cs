using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Newtonsoft.Json;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public sealed partial class CurveEditor3D : ExportLoaderControl, IActorEditorContext
{
    private readonly CurveEditor3DModel model = new();
    private readonly List<IMEPackage> levelPackages = [];
    private readonly List<ActorProxy> levelActors = [];
    private readonly List<string> levelPaths = [];
    private bool eventsAttached;
    private CurveEditor3DKeyframe selectedKeyframe;
    private string currentExportName;
    private string sceneStatus = "Select an InterpTrackMove export, then optionally open a level backdrop.";

    public CurveEditor3D() : base("3D Curve Editor")
    {
        RenderContext = new LevelEditorRenderContext();
        InterpModes = Enum.GetValues<EInterpCurveMode>();
        InitializeComponent();
        SceneViewer.Context = RenderContext;
        RenderContext.EnableTransformWidget();
        model.Changed += Model_Changed;
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
        UnregisterKeyframes();
        CurrentLoadedExport = exportEntry;
        model.Load(exportEntry);
        KeyframeList.ItemsSource = model.Keyframes;
        RegisterKeyframes();
        SelectedKeyframe = model.Keyframes.FirstOrDefault();
        CurrentExportName = $"{exportEntry.UIndex}: {exportEntry.InstancedFullPath}";
        SceneStatus = $"{model.Keyframes.Count} trajectory keyframe(s); {levelPaths.Count} level backdrop file(s).";
        FocusCameraOnKey(SelectedKeyframe);
        SceneViewer?.MarkRenderDirty();
    }

    public override void UnloadExport()
    {
        UnregisterKeyframes();
        model.Clear();
        KeyframeList.ItemsSource = null;
        SelectedKeyframe = null;
        CurrentLoadedExport = null;
        CurrentExportName = null;
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
        SceneViewer.Dispose();
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
        RenderContext.SelectActor += IgnoreActorSelection;
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
        RenderContext.SelectActor -= IgnoreActorSelection;
        eventsAttached = false;
    }

    private void SelectHitProxy(IHitProxy hitProxy)
    {
        if (hitProxy is CurveEditor3DKeyframe keyframe)
        {
            SelectedKeyframe = keyframe;
        }
    }

    private void IgnoreActorSelection(ActorProxy actor)
    {
        RenderContext.TransformWidget.Attach = SelectedKeyframe;
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

    private void SnapCameraToKey_Click(object sender, RoutedEventArgs e)
    {
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
        KeyframeList?.Items.Refresh();
        SceneViewer?.MarkRenderDirty();
    }

    private void RenderScene(object sender, EventArgs e)
    {
        RenderContext.RefreshSceneLights();
        foreach (RenderPass pass in new[] { RenderPass.Base, RenderPass.Hair })
        {
            foreach (ActorProxy actor in RenderContext.DrawList_3D)
            {
                int hitId = actor.HitID;
                RenderContext.CurrentHitTestId = new Vector3((hitId & 0xFF) / 255f, ((hitId >> 8) & 0xFF) / 255f, ((hitId >> 16) & 0xFF) / 255f);
                actor.Render(RenderContext, pass);
            }
        }

        DrawTrajectory();
        RenderContext.DrawUI();
    }

    private void DrawTrajectory()
    {
        IReadOnlyList<Vector3> samples = model.SampleTrajectory();
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

    private void RecentLevelsMenu_SubmenuOpened(object sender, RoutedEventArgs e)
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
