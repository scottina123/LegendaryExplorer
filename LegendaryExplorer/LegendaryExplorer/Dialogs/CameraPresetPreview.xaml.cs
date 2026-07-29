using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows.Controls;
using LegendaryExplorer.Tools.InterpEditor;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.Dialogs;

public partial class CameraPresetPreview : UserControl, IDisposable
{
    private readonly LevelEditorRenderContext _renderContext;
    private IReadOnlyList<GeneratedCameraKey> _keys = [];
    private InterpCurve<Vector3> _positionCurve;
    private InterpCurve<Vector3> _rotationCurve;
    private float _elapsed;
    private float _duration;
    private bool _isDynamic;
    private bool _disposed;
    private Vector3 _sceneOrigin;

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

    public void SetPreview(CameraPreset preset, CameraOrigin origin, IReadOnlyList<GeneratedCameraKey> keys)
    {
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

    private void RenderScene(object sender, EventArgs e)
    {
        if (_isDynamic)
        {
            DrawCube(_sceneOrigin + new Vector3(0, -110, 60), 55, new Vector4(0.2f, 0.55f, 1f, 1f));
            DrawCube(_sceneOrigin + new Vector3(80, 75, 45), 45, new Vector4(1f, 0.55f, 0.2f, 1f));
            DrawCube(_sceneOrigin + new Vector3(-100, 65, 35), 35, new Vector4(0.35f, 0.9f, 0.45f, 1f));
        }
        else
        {
            DrawCube(_sceneOrigin + new Vector3(0, 0, 65), 65, new Vector4(0.25f, 0.65f, 1f, 1f));
        }

        _renderContext.DrawUI();
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
        SceneViewer.Dispose();
    }
}
