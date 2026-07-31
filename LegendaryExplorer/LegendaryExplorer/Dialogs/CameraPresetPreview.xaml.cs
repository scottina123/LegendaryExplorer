using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.InterpEditor;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorer.UserControls.Interfaces;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorer.Dialogs;

public partial class CameraPresetPreview : UserControl, IDisposable, IActorEditorContext, ISceneRenderContextConfigurable
{
    private static readonly RenderPass[] BaseRenderPasses = [RenderPass.Base, RenderPass.Hair];

    private sealed class PreviewActorWidgetTarget : ITransformWidgetTarget
    {
        private Vector3 _location;
        private Rotator _rotation;

        public Action<CameraOrigin> TransformChanged { get; set; }
        public Vector3 Location
        {
            get => _location;
            set
            {
                _location = value;
                NotifyTransformChanged();
            }
        }
        public Rotator Rotation
        {
            get => _rotation;
            set
            {
                _rotation = value;
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
            _location = origin.Location;
            _rotation = Rotator.FromDegreesVector(origin.Rotation);
        }

        private void NotifyTransformChanged()
        {
            TransformChanged?.Invoke(new CameraOrigin(_location, _rotation.GetDegreesVector()));
        }
    }

    private readonly LevelEditorRenderContext _renderContext;
    private readonly PreviewActorWidgetTarget _actorWidgetTarget = new();
    private readonly List<ModelPreview<WorldVertex>> _actorModels = [];
    private readonly List<IMEPackage> _levelPackages = [];
    private readonly List<string> _levelPaths = [];
    private IReadOnlyList<CameraOrigin> _actorTransforms = [];
    private IReadOnlyList<GeneratedCameraKey> _keys = [];
    private InterpCurve<Vector3> _positionCurve;
    private InterpCurve<Vector3> _rotationCurve;
    private float _elapsed;
    private float _duration;
    private bool _isDynamic;
    private bool _disposed;
    private Vector3 _sceneOrigin;
    private MulticamCameraPreset _multicamPreset;
    private IReadOnlyDictionary<string, IReadOnlyList<GeneratedCameraKey>> _multicamKeys;
    private readonly Dictionary<string, InterpCurve<Vector3>> _multicamPositionCurves = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InterpCurve<Vector3>> _multicamRotationCurves = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InterpCurve<float>> _multicamFovCurves = new(StringComparer.OrdinalIgnoreCase);
    private Action<GeneratedCameraKey> _activeCameraChanged;
    private string _activeMulticamGroupName;
    private bool _showCollision = Settings.LevelEditor_ShowCollision;
    private bool _showLightIcons = Settings.LevelEditor_ShowLightIcons;
    private bool _showVolumes = Settings.LevelEditor_ShowVolumes;
    private bool _showVolumetrics;
    private bool _unlit = Settings.LevelEditor_Unlit;
    private bool _setAlphaToBlack = true;
    private bool _showRedChannel = true;
    private bool _showGreenChannel = true;
    private bool _showBlueChannel = true;
    private bool _showAlphaChannel = true;
    private System.Windows.Media.Color _backgroundColor;

    public LevelEditorRenderContext RenderContext => _renderContext;
    public bool IsApplyingUndoRedo => false;
    public IReadOnlyList<string> LevelPaths => _levelPaths;
    public MEGame LevelGame => _levelPackages.Count > 0 ? _levelPackages[0].Game : MEGame.Unknown;
    public event Action<CameraOrigin> SelectedActorTransformChanged;
    public event Action<Vector3> SelectedActorSnapRequested;

    public bool ShowCollision
    {
        get => _showCollision;
        set => SetRenderOption(ref _showCollision, value);
    }

    public bool ShowLightIcons
    {
        get => _showLightIcons;
        set
        {
            if (SetRenderOption(ref _showLightIcons, value))
            {
                _renderContext.ShowLightIcons = value;
            }
        }
    }

    public bool ShowVolumes
    {
        get => _showVolumes;
        set
        {
            if (SetRenderOption(ref _showVolumes, value))
            {
                _renderContext.ShowVolumes = value;
            }
        }
    }

    public bool ShowVolumetrics
    {
        get => _showVolumetrics;
        set
        {
            if (SetRenderOption(ref _showVolumetrics, value))
            {
                _renderContext.ShowVolumetrics = value;
            }
        }
    }

    public bool Unlit
    {
        get => _unlit;
        set
        {
            if (SetRenderOption(ref _unlit, value))
            {
                SetRenderFlag(LevelEditorRenderContext.ShaderFlags.Unlit, value);
            }
        }
    }

    public bool SetAlphaToBlack
    {
        get => _setAlphaToBlack;
        set
        {
            if (SetRenderOption(ref _setAlphaToBlack, value))
            {
                SetRenderFlag(LevelEditorRenderContext.ShaderFlags.AlphaAsBlack, value);
            }
        }
    }

    public bool ShowRedChannel
    {
        get => _showRedChannel;
        set => SetChannelOption(ref _showRedChannel, value, LevelEditorRenderContext.ShaderFlags.EnableRedChannel);
    }

    public bool ShowGreenChannel
    {
        get => _showGreenChannel;
        set => SetChannelOption(ref _showGreenChannel, value, LevelEditorRenderContext.ShaderFlags.EnableGreenChannel);
    }

    public bool ShowBlueChannel
    {
        get => _showBlueChannel;
        set => SetChannelOption(ref _showBlueChannel, value, LevelEditorRenderContext.ShaderFlags.EnableBlueChannel);
    }

    public bool ShowAlphaChannel
    {
        get => _showAlphaChannel;
        set => SetChannelOption(ref _showAlphaChannel, value, LevelEditorRenderContext.ShaderFlags.EnableAlphaChannel);
    }

    public System.Windows.Media.Color BackgroundColor
    {
        get => _backgroundColor;
        set
        {
            if (_backgroundColor != value)
            {
                _backgroundColor = value;
                _renderContext.BackgroundColor = value;
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    public bool UseLocalCoordsForWidget
    {
        get => _renderContext.TransformWidget.UseLocalCoords;
        set
        {
            _renderContext.TransformWidget.UseLocalCoords = value;
            SceneViewer?.MarkRenderDirty();
        }
    }

    public ICommand ToggleTranslateCommand { get; }
    public ICommand ToggleRotateCommand { get; }
    public ICommand ToggleScaleCommand { get; }
    public ICommand ToggleUniformScaleCommand { get; }

    public CameraPresetPreview()
    {
        InitializeComponent();
        _renderContext = new LevelEditorRenderContext();
        _backgroundColor = LevelEditor.GetThemeDefaultBackgroundColor();
        _renderContext.BackgroundColor = _backgroundColor;
        _renderContext.ShowLightIcons = _showLightIcons;
        _renderContext.ShowVolumes = _showVolumes;
        _renderContext.ShowVolumetrics = _showVolumetrics;
        SetRenderFlag(LevelEditorRenderContext.ShaderFlags.Unlit, _unlit);
        SetRenderFlag(LevelEditorRenderContext.ShaderFlags.AlphaAsBlack, _setAlphaToBlack);
        _renderContext.EnableTransformWidget();
        _renderContext.TransformWidget.UseLocalCoords = true;
        ToggleTranslateCommand = new GenericCommand(() => _renderContext.TransformWidget.Mode = EWidgetMode.Translate);
        ToggleRotateCommand = new GenericCommand(() =>
        {
            _renderContext.TransformWidget.Mode = EWidgetMode.Rotate;
            _renderContext.TransformWidget.VisibleAxes = EWidgetAxis.XYZ;
        });
        ToggleScaleCommand = new GenericCommand(() => _renderContext.TransformWidget.Mode = EWidgetMode.Scale);
        ToggleUniformScaleCommand = new GenericCommand(() => _renderContext.TransformWidget.Mode = EWidgetMode.UniformScale);
        _actorWidgetTarget.TransformChanged = origin => SelectedActorTransformChanged?.Invoke(origin);
        _renderContext.RenderScene += RenderScene;
        _renderContext.UpdateScene += UpdateScene;
        _renderContext.RightClickViewport += ShowViewportContextMenu;
        _renderContext.RightClickActor += _ => ShowViewportContextMenu();
        SceneViewer.Context = _renderContext;
        PreviewOptionsControl.DataContext = this;
    }

    private bool SetRenderOption(ref bool field, bool value)
    {
        if (field == value)
        {
            return false;
        }

        field = value;
        SceneViewer?.MarkRenderDirty();
        return true;
    }

    private void SetChannelOption(ref bool field, bool value, LevelEditorRenderContext.ShaderFlags flag)
    {
        if (SetRenderOption(ref field, value))
        {
            SetRenderFlag(flag, value);
        }
    }

    private void SetRenderFlag(LevelEditorRenderContext.ShaderFlags flag, bool enabled)
    {
        if (enabled)
        {
            _renderContext.RenderFlags |= flag;
        }
        else
        {
            _renderContext.RenderFlags &= ~flag;
        }
    }

    private void ShowViewportContextMenu()
    {
        Point viewportPoint = Mouse.GetPosition(SceneViewer);
        var contextMenu = new ContextMenu
        {
            PlacementTarget = SceneViewer,
            Placement = PlacementMode.MousePoint
        };
        var snapItem = new MenuItem
        {
            Header = "Snap Selected Actor Here",
            IsEnabled = _renderContext.TransformWidget.Attach is not null
        };
        snapItem.Click += (_, _) => SelectedActorSnapRequested?.Invoke(GetViewportLocationAtSelectedActorDepth(viewportPoint));
        contextMenu.Items.Add(snapItem);
        contextMenu.IsOpen = true;
    }

    private Vector3 GetViewportLocationAtSelectedActorDepth(Point viewportPoint)
    {
        Vector3 referenceLocation = _actorWidgetTarget.Location;
        float width = MathF.Max(_renderContext.Width, 1f);
        float height = MathF.Max(_renderContext.Height, 1f);
        float normalizedX = ((float)viewportPoint.X / width * 2f) - 1f;
        float normalizedY = 1f - ((float)viewportPoint.Y / height * 2f);
        Vector3 forward = _renderContext.Camera.CameraForward;
        Vector3 right = _renderContext.Camera.CameraRight;
        Vector3 up = _renderContext.Camera.CameraUp;
        Vector3 cameraPosition = _renderContext.Camera.Position;

        if (_renderContext.Camera.IsOrthographic)
        {
            return cameraPosition
                   + right * (normalizedX * _renderContext.Camera.OrthoWidth * 0.5f)
                   + up * (normalizedY * _renderContext.Camera.OrthoWidth / MathF.Max(_renderContext.Camera.aspect, float.Epsilon) * 0.5f)
                   + forward * Vector3.Dot(referenceLocation - cameraPosition, forward);
        }

        float halfHeightAtUnitDepth = MathF.Tan(_renderContext.Camera.FOV * 0.5f);
        Vector3 rayDirection = Vector3.Normalize(forward
            + right * normalizedX * halfHeightAtUnitDepth * _renderContext.Camera.aspect
            + up * normalizedY * halfHeightAtUnitDepth);
        float denominator = Vector3.Dot(rayDirection, forward);
        if (MathF.Abs(denominator) < 0.0001f)
        {
            return referenceLocation;
        }
        float distance = Vector3.Dot(referenceLocation - cameraPosition, forward) / denominator;
        return distance > 0f && float.IsFinite(distance)
            ? cameraPosition + rayDirection * distance
            : referenceLocation;
    }

    public async Task LoadLevelAsync(string path, bool replace)
    {
        if (replace)
        {
            UnloadLevels();
        }

        path = Path.GetFullPath(path);
        if (_levelPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        await Task.Delay(1).ConfigureAwait(true);
        IMEPackage package = MEPackageHandler.OpenMEPackage(path);
        try
        {
            ExportEntry levelExport = package.Exports.FirstOrDefault(export => export.ClassName == "Level");
            if (levelExport is null)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)} is not a level file.");
            }

            Level level = levelExport.GetBinaryData<Level>();
            List<ActorProxy> actors = LoadLevelActors(level);
            _levelPackages.Add(package);
            _levelPaths.Add(path);
            _renderContext.LoadActors(actors);
            SceneViewer.SetShouldRender(true);
            SceneViewer.MarkRenderDirty();
        }
        catch
        {
            package.Dispose();
            throw;
        }
    }

    public void SelectActor(int actorIndex)
    {
        if (actorIndex < 0 || actorIndex >= _actorTransforms.Count)
        {
            _renderContext.TransformWidget.Attach = null;
            SceneViewer.MarkRenderDirty();
            return;
        }
        _actorWidgetTarget.SetTransform(_actorTransforms[actorIndex]);
        _renderContext.TransformWidget.Attach = _actorWidgetTarget;
        SceneViewer.MarkRenderDirty();
    }

    public void SetSelectedActorTransform(CameraOrigin origin)
    {
        _actorWidgetTarget.SetTransform(origin);
        SceneViewer.MarkRenderDirty();
    }

    public void SetActorGizmoMode(bool rotate)
    {
        _renderContext.TransformWidget.Mode = rotate ? EWidgetMode.Rotate : EWidgetMode.Translate;
        _renderContext.TransformWidget.VisibleAxes = EWidgetAxis.XYZ;
        SceneViewer.MarkRenderDirty();
    }

    private List<ActorProxy> LoadLevelActors(Level level)
    {
        var actors = new List<ActorProxy>();
        IEnumerable<ExportEntry> actorExports = level.Actors
            .Where(level.Export.FileRef.IsUExport)
            .Select(level.Export.FileRef.GetUExport);
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

    public void UnloadLevels()
    {
        _renderContext.UnloadLevel();
        foreach (IMEPackage package in _levelPackages)
        {
            package.Dispose();
        }
        _levelPackages.Clear();
        _levelPaths.Clear();
        SceneViewer.MarkRenderDirty();
    }

    public void LoadActorModel(int actorIndex, ExportEntry skeletalMeshExport)
    {
        if (actorIndex < 0 || skeletalMeshExport is null)
        {
            return;
        }

        ModelPreview<WorldVertex> model = new(_renderContext, skeletalMeshExport.GetBinaryData<SkeletalMesh>());
        while (_actorModels.Count <= actorIndex)
        {
            _actorModels.Add(null);
        }
        _actorModels[actorIndex]?.Dispose();
        _actorModels[actorIndex] = model;
        SceneViewer.MarkRenderDirty();
    }

    public void RemoveActorModel(int actorIndex)
    {
        if (actorIndex < 0 || actorIndex >= _actorModels.Count)
        {
            return;
        }
        _actorModels[actorIndex]?.Dispose();
        _actorModels.RemoveAt(actorIndex);
        SceneViewer.MarkRenderDirty();
    }

    public void ClearActorModels()
    {
        foreach (ModelPreview<WorldVertex> actorModel in _actorModels)
        {
            actorModel?.Dispose();
        }
        _actorModels.Clear();
        SceneViewer.MarkRenderDirty();
    }

    public void SetActorTransforms(IReadOnlyList<CameraOrigin> actorTransforms)
    {
        _actorTransforms = actorTransforms ?? [];
        SceneViewer.MarkRenderDirty();
    }

    public void FocusActor(int actorIndex)
    {
        if (actorIndex < 0 || actorIndex >= _actorModels.Count || actorIndex >= _actorTransforms.Count
            || _actorModels[actorIndex] is not { LODs.Count: > 0 } actorModel)
        {
            return;
        }

        CameraOrigin transform = _actorTransforms[actorIndex];
        Matrix4x4 localToWorld = CreateActorTransform(transform);
        BoxSphereBounds bounds = actorModel.LODs[0].Mesh.BaseBounds.TransformBy(localToWorld);
        float distance = MathF.Max(bounds.SphereRadius, 50) * 2;
        (float sin, float cos) = MathF.SinCos(MathF.PI / 2.5f);
        _isDynamic = false;
        _renderContext.ForceContinuousRendering = false;
        _renderContext.Camera.Position = new Vector3(bounds.Origin.X, bounds.Origin.Y + sin * distance,
            bounds.Origin.Z + cos * distance);
        _renderContext.Camera.OrientTowards(bounds.Origin);
        _renderContext.Camera.FocusDepth = 0;
        SceneViewer.MarkRenderDirty();
    }

    public void SetPreview(CameraPreset preset, CameraOrigin origin, IReadOnlyList<GeneratedCameraKey> keys)
    {
        _multicamPreset = null;
        _multicamKeys = null;
        _activeCameraChanged = null;
        _activeMulticamGroupName = null;
        _keys = keys ?? [];
        _sceneOrigin = origin.Location;
        _isDynamic = preset is not null
            && (preset.Category == CameraPresetCategory.DynamicShots || preset.IsSavedTrackMove)
            && _keys.Count > 1;
        _duration = _keys.Count > 0 ? _keys[^1].TimeOffset : 0;
        _elapsed = 0;
        BuildCurves();
        ApplyCamera(0);
        _renderContext.ForceContinuousRendering = _isDynamic;
        PreviewStatusTextBlock.Text = preset is null
            ? "Select a preset"
            : _isDynamic ? $"{preset.Name} — looping dynamic preview" : $"{preset.Name} — static preview";
        SceneViewer.MarkRenderDirty();
    }

    public void SetMulticamPreview(MulticamCameraPreset preset, CameraOrigin origin,
        IReadOnlyDictionary<string, IReadOnlyList<GeneratedCameraKey>> cameras,
        Action<GeneratedCameraKey> activeCameraChanged = null)
    {
        _multicamPreset = preset;
        _multicamKeys = cameras;
        _activeCameraChanged = activeCameraChanged;
        _activeMulticamGroupName = null;
        _sceneOrigin = origin.Location;
        _keys = [];
        _duration = preset?.Duration ?? 0;
        _elapsed = 0;
        _isDynamic = preset is not null && cameras is { Count: > 0 } && _duration > float.Epsilon;
        BuildMulticamCurves();
        ApplyCamera(0);
        _renderContext.ForceContinuousRendering = _isDynamic;
        PreviewStatusTextBlock.Text = preset is null
            ? "Select a multicam preset"
            : $"{preset.Name} — {preset.TypeDisplay} — looping complete Director preview";
        SceneViewer.MarkRenderDirty();
    }

    private void BuildMulticamCurves()
    {
        _multicamPositionCurves.Clear();
        _multicamRotationCurves.Clear();
        _multicamFovCurves.Clear();
        if (_multicamPreset is null || _multicamKeys is null)
        {
            return;
        }

        foreach ((string groupName, IReadOnlyList<GeneratedCameraKey> keys) in _multicamKeys)
        {
            var positionCurve = new InterpCurve<Vector3>();
            var rotationCurve = new InterpCurve<Vector3>();
            foreach (GeneratedCameraKey key in keys)
            {
                positionCurve.Points.Add(new InterpCurvePoint<Vector3>(key.TimeOffset, key.Location,
                    Vector3.Zero, Vector3.Zero, EInterpCurveMode.CIM_Linear));
                rotationCurve.Points.Add(new InterpCurvePoint<Vector3>(key.TimeOffset, key.Rotation,
                    Vector3.Zero, Vector3.Zero, EInterpCurveMode.CIM_Linear));
            }
            _multicamPositionCurves[groupName] = positionCurve;
            _multicamRotationCurves[groupName] = rotationCurve;
        }

        foreach (MulticamCameraGroup group in _multicamPreset.CameraGroups.Where(group => group.FovKeys is { Count: > 0 }))
        {
            var curve = new InterpCurve<float>();
            foreach (MulticamFovKey key in group.FovKeys)
            {
                curve.Points.Add(new InterpCurvePoint<float>(key.TimeOffset, key.Value,
                    key.ArriveTangent, key.LeaveTangent, EInterpCurveMode.CIM_Linear));
            }
            _multicamFovCurves[group.GroupName] = curve;
        }
    }

    private void BuildCurves()
    {
        _positionCurve = new InterpCurve<Vector3>();
        _rotationCurve = new InterpCurve<Vector3>();
        foreach (GeneratedCameraKey key in _keys)
        {
            _positionCurve.Points.Add(new InterpCurvePoint<Vector3>(key.TimeOffset, key.Location,
                Vector3.Zero, Vector3.Zero, EInterpCurveMode.CIM_Linear));
            _rotationCurve.Points.Add(new InterpCurvePoint<Vector3>(key.TimeOffset, key.Rotation,
                Vector3.Zero, Vector3.Zero, EInterpCurveMode.CIM_Linear));
        }
    }

    private void UpdateScene(object sender, float deltaTime)
    {
        if (!_isDynamic || _duration <= float.Epsilon)
        {
            return;
        }

        _elapsed = (_elapsed + deltaTime) % _duration;
        ApplyCamera(_elapsed);
    }

    private void ApplyCamera(float time)
    {
        if (_multicamPreset is not null)
        {
            ApplyMulticamCamera(time);
            return;
        }
        if (_keys.Count == 0)
        {
            return;
        }

        Vector3 location = _positionCurve?.Eval(time, _keys[0].Location) ?? _keys[0].Location;
        Vector3 rotation = _rotationCurve?.Eval(time, _keys[0].Rotation) ?? _keys[0].Rotation;
        const float degreesToRadians = MathF.PI / 180f;
        _renderContext.Camera.Position = location;
        _renderContext.Camera.Roll = rotation.X * degreesToRadians;
        _renderContext.Camera.Pitch = rotation.Y * degreesToRadians;
        _renderContext.Camera.Yaw = rotation.Z * degreesToRadians;
        _renderContext.Camera.FocusDepth = 0;
        SceneViewer.MarkRenderDirty();
    }

    private void ApplyMulticamCamera(float time)
    {
        MulticamDirectorKey activeCut = _multicamPreset.DirectorKeys
            .Where(key => key.TimeOffset <= time)
            .OrderBy(key => key.TimeOffset)
            .LastOrDefault(_multicamPreset.DirectorKeys.OrderBy(key => key.TimeOffset).FirstOrDefault());
        if (!_multicamKeys.TryGetValue(activeCut.GroupName, out IReadOnlyList<GeneratedCameraKey> keys)
            || keys.Count == 0
            || !_multicamPositionCurves.TryGetValue(activeCut.GroupName, out InterpCurve<Vector3> positionCurve)
            || !_multicamRotationCurves.TryGetValue(activeCut.GroupName, out InterpCurve<Vector3> rotationCurve))
        {
            return;
        }

        float cameraTime = Math.Clamp(time, keys[0].TimeOffset, keys[^1].TimeOffset);
        Vector3 location = positionCurve.Eval(cameraTime, keys[0].Location);
        Vector3 rotation = rotationCurve.Eval(cameraTime, keys[0].Rotation);
        const float degreesToRadians = MathF.PI / 180f;
        _renderContext.Camera.Position = location;
        _renderContext.Camera.Roll = rotation.X * degreesToRadians;
        _renderContext.Camera.Pitch = rotation.Y * degreesToRadians;
        _renderContext.Camera.Yaw = rotation.Z * degreesToRadians;
        if (_multicamFovCurves.TryGetValue(activeCut.GroupName, out InterpCurve<float> fovCurve)
            && fovCurve.Points.Count > 0)
        {
            float fovTime = Math.Clamp(time, fovCurve.Points[0].InVal, fovCurve.Points[^1].InVal);
            _renderContext.Camera.FOV = fovCurve.Eval(fovTime, 60) * degreesToRadians;
        }
        else if (!string.Equals(_activeMulticamGroupName, activeCut.GroupName, StringComparison.OrdinalIgnoreCase))
        {
            _renderContext.Camera.FOV = 60 * degreesToRadians;
        }
        _activeMulticamGroupName = activeCut.GroupName;
        _renderContext.Camera.FocusDepth = 0;
        _activeCameraChanged?.Invoke(new GeneratedCameraKey(time, location, rotation, CameraKeyInterpolation.Linear));
        SceneViewer.MarkRenderDirty();
    }

    private void RenderScene(object sender, EventArgs e)
    {
        ReadOnlySpan<RenderPass> renderPasses = ShowCollision
            ? [RenderPass.Base, RenderPass.Hair, RenderPass.Collision]
            : BaseRenderPasses;
        foreach (RenderPass pass in renderPasses)
        {
            foreach (ActorProxy actor in _renderContext.DrawList_3D)
            {
                if (actor.IsVolume && !ShowVolumes) continue;
                if (actor.IsVolumetricMesh && !ShowVolumetrics) continue;
                int hitId = actor.HitID;
                _renderContext.CurrentHitTestId = new Vector3((hitId & 0xFF) / 255f, ((hitId >> 8) & 0xFF) / 255f, ((hitId >> 16) & 0xFF) / 255f);
                actor.Render(_renderContext, pass);
            }
        }
        if (_multicamPreset is not null)
        {
            RenderActors();
            _renderContext.DrawUI();
            return;
        }
        RenderActors();

        _renderContext.DrawUI();
    }

    private void RenderActors()
    {
        int actorCount = Math.Min(_actorModels.Count, _actorTransforms.Count);
        for (int actorIndex = 0; actorIndex < actorCount; actorIndex++)
        {
            ModelPreview<WorldVertex> actorModel = _actorModels[actorIndex];
            if (actorModel is null)
            {
                continue;
            }
            CameraOrigin transform = _actorTransforms[actorIndex];
            actorModel.UpdateLocalToWorld(CreateActorTransform(transform));
            actorModel.Render(RenderPass.Base, _renderContext, 0);
            actorModel.Render(RenderPass.Hair, _renderContext, 0);
        }
    }

    private static Matrix4x4 CreateActorTransform(CameraOrigin transform)
    {
        return Rotator.FromDegreesVector(transform.Rotation).ToRotationMatrix()
               * Matrix4x4.CreateTranslation(transform.Location);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _renderContext.ForceContinuousRendering = false;
        _renderContext.RenderScene -= RenderScene;
        _renderContext.UpdateScene -= UpdateScene;
        _renderContext.RightClickViewport -= ShowViewportContextMenu;
        UnloadLevels();
        ClearActorModels();
        SceneViewer.Dispose();
    }
}
