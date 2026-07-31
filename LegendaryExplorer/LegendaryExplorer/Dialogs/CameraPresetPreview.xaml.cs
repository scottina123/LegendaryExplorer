using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Controls;
using LegendaryExplorer.Tools.InterpEditor;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.Dialogs;

public partial class CameraPresetPreview : UserControl, IDisposable
{
    private readonly LevelEditorRenderContext _renderContext;
    private readonly List<ModelPreview<WorldVertex>> _actorModels = [];
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

    public CameraPresetPreview()
    {
        InitializeComponent();
        _renderContext = new LevelEditorRenderContext(readOnly: true)
        {
            BackgroundColor = System.Windows.Media.Color.FromRgb(0x20, 0x24, 0x2A)
        };
        _renderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.Unlit;
        _renderContext.RenderScene += RenderScene;
        _renderContext.UpdateScene += UpdateScene;
        SceneViewer.Context = _renderContext;
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
        const float degreesToRadians = MathF.PI / 180f;
        return Matrix4x4.CreateFromYawPitchRoll(
                   transform.Rotation.Z * degreesToRadians,
                   transform.Rotation.Y * degreesToRadians,
                   transform.Rotation.X * degreesToRadians)
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
        ClearActorModels();
        SceneViewer.Dispose();
    }
}
