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
    private ModelPreview<WorldVertex> _actorModel;
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

    public void LoadActorModel(ExportEntry skeletalMeshExport)
    {
        if (skeletalMeshExport is null)
        {
            return;
        }

        ModelPreview<WorldVertex> model = new(_renderContext, skeletalMeshExport.GetBinaryData<SkeletalMesh>());
        _actorModel?.Dispose();
        _actorModel = model;
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
            RenderActor();
            DrawMulticamPaths();
            _renderContext.DrawUI();
            return;
        }
        RenderActor();

        _renderContext.DrawUI();
    }

    private void RenderActor()
    {
        if (_actorModel is null)
        {
            return;
        }

        _actorModel.UpdateLocalToWorld(Matrix4x4.CreateTranslation(_sceneOrigin));
        _actorModel.Render(RenderPass.Base, _renderContext, 0);
        _actorModel.Render(RenderPass.Hair, _renderContext, 0);
    }

    private void DrawMulticamPaths()
    {
        if (_multicamKeys is null)
        {
            return;
        }

        Vector4[] colors =
        [
            new(0.2f, 0.65f, 1f, 1f), new(1f, 0.5f, 0.2f, 1f),
            new(0.35f, 0.9f, 0.45f, 1f), new(0.85f, 0.35f, 0.9f, 1f)
        ];
        int groupIndex = 0;
        foreach ((string groupName, IReadOnlyList<GeneratedCameraKey> keys) in _multicamKeys)
        {
            Vector4 color = colors[groupIndex++ % colors.Length];
            foreach (GeneratedCameraKey key in keys)
            {
                DrawCube(key.Location, 7, color);
            }
            for (int keyIndex = 1; keyIndex < keys.Count; keyIndex++)
            {
                for (int sample = 1; sample < 8; sample++)
                {
                    DrawCube(Vector3.Lerp(keys[keyIndex - 1].Location, keys[keyIndex].Location, sample / 8f), 2.5f, color);
                }
            }

            if (_multicamPositionCurves.TryGetValue(groupName, out InterpCurve<Vector3> positionCurve))
            {
                float cameraTime = Math.Clamp(_elapsed, keys[0].TimeOffset, keys[^1].TimeOffset);
                DrawCube(positionCurve.Eval(cameraTime, keys[0].Location), 12, color);
            }
        }
    }

    private void DrawCube(Vector3 center, float halfSize, Vector4 color)
    {
        var cube = _renderContext.Primitives.BuildMesh(color, 0, Matrix4x4.CreateTranslation(center));
        cube.AddVertex(-halfSize, -halfSize, -halfSize);
        cube.AddVertex(halfSize, -halfSize, -halfSize);
        cube.AddVertex(halfSize, halfSize, -halfSize);
        cube.AddVertex(-halfSize, halfSize, -halfSize);
        cube.AddVertex(-halfSize, -halfSize, halfSize);
        cube.AddVertex(halfSize, -halfSize, halfSize);
        cube.AddVertex(halfSize, halfSize, halfSize);
        cube.AddVertex(-halfSize, halfSize, halfSize);
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
        _actorModel?.Dispose();
        SceneViewer.Dispose();
    }
}
