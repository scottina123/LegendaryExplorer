using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;
using Point = System.Windows.Point;
using ShaderFlags = LegendaryExplorer.Tools.LevelEditor.Scene3D.RenderContext.ShaderFlags;

namespace LegendaryExplorer.Tools.LevelEditor;

public partial class LevelEditor
{
    public VolumeDrawSettings VolumeDrawing { get; } = new();
    private readonly List<Vector2> _volumeCorners = [];
    private OpenLevelFile _volumeDrawFile;
    private Vector3 _volumeSurfaceNormal = Vector3.UnitZ;
    private bool _isDrawingVolume, _pickVolumeSurface, _volumeDraftValid;
    private int _volumeDrag = -1;
    private DepthStencilState _volumeXrayState;
    private string _volumeDrawStatus;
    public bool IsDrawingVolume { get => _isDrawingVolume; private set => SetProperty(ref _isDrawingVolume, value); }
    public bool CanFinishVolume => IsDrawingVolume && _volumeDraftValid && !IsBusy;
    public string VolumeDrawTarget => _volumeDrawFile?.FileName;
    public string VolumeDrawStatus { get => _volumeDrawStatus; private set => SetProperty(ref _volumeDrawStatus, value); }

    private void DrawVolume_Click(object sender, RoutedEventArgs e)
    {
        if (IsDrawingVolume) { CancelVolumeDrawing(); return; }
        BeginVolumeDrawing(SelectedActor?.Location ?? RenderContext.Camera.EyePosition + RenderContext.Camera.CameraForward * 500);
    }

    private void BeginVolumeDrawing(Vector3 origin)
    {
        if (IsBusy || ActiveFile is not { IsReadOnly: false } file) return;
        if (!file.Package.Game.IsLEGame())
        {
            MessageBox.Show(this, "Drawing placed volumes with cooked collision is currently available for Legendary Edition levels.", "Draw volume");
            return;
        }
        CloseLevelLiveMaterialEditor();
        _volumeDrawFile = file;
        _volumeCorners.Clear();
        _volumeDrag = -1;
        _pickVolumeSurface = false;
        _volumeSurfaceNormal = Vector3.UnitZ;
        VolumeDrawing.SetOrigin(origin);
        IsDrawingVolume = true;
        NotifyVolumeEditMode();
        OnPropertyChanged(nameof(VolumeDrawTarget));
        RebuildVolumeDraft();
        SceneViewer.Focus();
        SceneViewer.MarkRenderDirty();
    }

    private void CancelVolumeDrawing()
    {
        _volumeDrag = -1;
        if (SceneViewer.IsMouseCaptured) SceneViewer.ReleaseMouseCapture();
        _volumeCorners.Clear();
        _volumeDrawFile = null;
        _pickVolumeSurface = false;
        _volumeDraftValid = false;
        _volumeEdit = null;
        _volumeEditVertex = -1;
        IsDrawingVolume = false;
        NotifyVolumeEditMode();
        OnPropertyChanged(nameof(CanFinishVolume));
        SceneViewer.MarkRenderDirty();
    }

    private void CancelVolumeDrawing_Click(object sender, RoutedEventArgs e) => CancelVolumeDrawing();
    private void VolumeDrawingSettingsChanged(object sender, PropertyChangedEventArgs e) { if (IsDrawingVolume) RebuildVolumeDraft(); }
    private void RebuildVolumeDraft()
    {
        if (_volumeEdit != null) { RebuildVolumeEdit(); return; }
        _volumeDraftValid = false;
        try
        {
            if (!float.IsFinite(VolumeDrawing.Step) || VolumeDrawing.Step < 0.01f || VolumeDrawing.Step > 100000)
                throw new ArgumentException("Grid spacing must be between 0.01 and 100000 units.");
            _ = VolumeDrawing.GetPlane(_volumeSurfaceNormal).Extrude(_volumeCorners, VolumeDrawing.Height);
            _volumeDraftValid = true;
            VolumeDrawStatus = $"{_volumeCorners.Count} corners. Drag corners or the yellow height handle. Enter creates the volume; Esc cancels.";
        }
        catch (ArgumentException exception) { VolumeDrawStatus = exception.Message; }
        OnPropertyChanged(nameof(CanFinishVolume));
        SceneViewer.MarkRenderDirty();
    }

    private void UndoVolumeCorner_Click(object sender, RoutedEventArgs e)
    {
        if (_volumeEdit != null) { _volumeEdit.Undo(); SelectVolumeVertex(_volumeEditVertex); RebuildVolumeDraft(); return; }
        if (_volumeCorners.Count > 0) _volumeCorners.RemoveAt(_volumeCorners.Count - 1);
        RebuildVolumeDraft();
    }
    private void PickVolumeSurface_Click(object sender, RoutedEventArgs e)
    {
        _pickVolumeSurface = true;
        VolumeDrawStatus = "Click a surface in the loaded level to position the work plane. Choose Surface to align it with that face.";
        SceneViewer.Focus();
    }
    private void VolumeAtSelection_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedActor != null) VolumeDrawing.SetOrigin(SelectedActor.GetBounds().Origin);
    }

    private void VolumeDrawing_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsDrawingVolume || Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox) return;
        if (e.Key == Key.Escape) { CancelVolumeDrawing(); e.Handled = true; }
        else if (e.Key == Key.Delete) { e.Handled = true; }
        else if (e.Key == Key.Enter) { FinishVolumeDrawing_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Back || e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        { UndoVolumeCorner_Click(sender, e); e.Handled = true; }
    }

    private (Vector3 Origin, Vector3 Direction) VolumeRay(Point pixel)
        => GetVolumeDrawingRay(RenderContext.Camera, SceneViewer.ActualWidth, SceneViewer.ActualHeight, pixel);

    internal static (Vector3 Origin, Vector3 Direction) GetVolumeDrawingRay(SceneCamera camera, double width, double height, Point pixel)
    {
        float x = (float)(2 * pixel.X / Math.Max(width, 1) - 1);
        float y = (float)(1 - 2 * pixel.Y / Math.Max(height, 1));
        if (camera.IsOrthographic)
            return (camera.Position + camera.CameraRight * (x * camera.OrthoWidth / 2)
                    + camera.CameraUp * (y * camera.OrthoWidth / MathF.Max(camera.aspect, 0.001f) / 2), camera.CameraForward);
        float tangent = MathF.Tan(camera.FOV / 2);
        return (camera.EyePosition, Vector3.Normalize(camera.CameraForward + camera.CameraRight * (x * tangent * camera.aspect) + camera.CameraUp * (y * tangent)));
    }

    private bool ProjectVolumePoint(Vector3 world, out Point point)
    {
        Vector4 p = Vector4.Transform(new Vector4(world, 1), RenderContext.Camera.ViewProjectionMatrix);
        point = default;
        if (p.W <= 0.00001f || !float.IsFinite(p.W)) return false;
        point = new Point((p.X / p.W + 1) * SceneViewer.ActualWidth / 2, (1 - p.Y / p.W) * SceneViewer.ActualHeight / 2);
        return true;
    }

    private Vector2 VolumeHeightCorner => _volumeCorners.Count == 0 ? Vector2.Zero
        : _volumeCorners.Aggregate(Vector2.Zero, (sum, p) => sum + p) / _volumeCorners.Count;

    private bool VolumeDrawingMouseDown(MouseButtonEventArgs e)
    {
        if (!IsDrawingVolume || e.ChangedButton != MouseButton.Left) return false;
        e.Handled = true;
        SceneViewer.Focus();
        Point pixel = e.GetPosition(SceneViewer);
        var ray = VolumeRay(pixel);
        if (_volumeEdit != null) { BeginVolumeVertexDrag(pixel, ray.Origin, ray.Direction); return true; }
        if (_pickVolumeSurface)
        {
            float nearest = float.PositiveInfinity;
            Vector3 hit = default, normal = default;
            foreach (var actor in Actors.Where(RenderContext.IsActorVisible))
                foreach (var component in actor.Components.OfType<MeshComponentProxy>())
                    if (component.TryGetDrawingSurfaceHit(ray.Origin, ray.Direction, out Vector3 p, out Vector3 n, out float distance) && distance < nearest)
                    { nearest = distance; hit = p; normal = n; }
            if (float.IsFinite(nearest))
            {
                _volumeSurfaceNormal = normal;
                VolumeDrawing.SetOrigin(hit);
                _pickVolumeSurface = false;
                RebuildVolumeDraft();
            }
            else VolumeDrawStatus = "No mesh surface under the cursor. Click visible level geometry or enter the work-plane position.";
            return true;
        }
        try
        {
            var plane = VolumeDrawing.GetPlane(_volumeSurfaceNormal);
            if (_volumeCorners.Count >= 3 && ProjectVolumePoint(plane.Point(VolumeHeightCorner, VolumeDrawing.Height), out Point heightPixel)
                && (heightPixel - pixel).Length < 12)
                _volumeDrag = -2;
            else
            {
                _volumeDrag = _volumeCorners.FindIndex(p => ProjectVolumePoint(plane.Point(p), out Point projected) && (projected - pixel).Length < 12);
                if (_volumeDrag < 0)
                {
                    if (_volumeCorners.Count >= 64) { VolumeDrawStatus = "Maximum 64 corners. Drag a corner or undo."; return true; }
                    if (!plane.IntersectRay(ray.Origin, ray.Direction, out Vector2 corner))
                    { VolumeDrawStatus = "The work plane is edge-on or behind the camera. Move the camera or change the plane."; return true; }
                    if (VolumeDrawing.Snap) corner = ModelDrawingPlane.Snap(corner, VolumeDrawing.Step);
                    _volumeCorners.Add(corner);
                    _volumeDrag = _volumeCorners.Count - 1;
                    RebuildVolumeDraft();
                }
            }
            SceneViewer.CaptureMouse();
        }
        catch (ArgumentException exception) { VolumeDrawStatus = exception.Message; }
        return true;
    }

    private void VolumeDrawing_MouseMove(object sender, MouseEventArgs e)
    {
        if (!IsDrawingVolume || _volumeDrag == -1 || e.LeftButton != MouseButtonState.Pressed) return;
        e.Handled = true;
        try
        {
            var ray = VolumeRay(e.GetPosition(SceneViewer));
            if (_volumeEdit != null) { MoveVolumeVertex(ray.Origin, ray.Direction); return; }
            var plane = VolumeDrawing.GetPlane(_volumeSurfaceNormal);
            if (_volumeDrag == -2)
            {
                Vector3 difference = plane.Point(VolumeHeightCorner) - ray.Origin;
                float b = Vector3.Dot(plane.Normal, ray.Direction), denominator = 1 - b * b;
                if (denominator > 0.0001f)
                {
                    float height = (b * Vector3.Dot(ray.Direction, difference) - Vector3.Dot(plane.Normal, difference)) / denominator;
                    if (VolumeDrawing.Snap) height = MathF.Round(height / VolumeDrawing.Step) * VolumeDrawing.Step;
                    VolumeDrawing.Height = MathF.Max(height, 0.1f);
                }
            }
            else if (plane.IntersectRay(ray.Origin, ray.Direction, out Vector2 corner))
            {
                _volumeCorners[_volumeDrag] = VolumeDrawing.Snap ? ModelDrawingPlane.Snap(corner, VolumeDrawing.Step) : corner;
                RebuildVolumeDraft();
            }
        }
        catch (ArgumentException exception) { VolumeDrawStatus = exception.Message; }
    }

    private bool VolumeDrawingMouseUp(MouseButtonEventArgs e)
    {
        if (!IsDrawingVolume || e.ChangedButton != MouseButton.Left) return false;
        _volumeDrag = -1;
        SceneViewer.ReleaseMouseCapture();
        e.Handled = true;
        return true;
    }

    private async void FinishVolumeDrawing_Click(object sender, RoutedEventArgs e)
    {
        if (_volumeEdit != null) { CommitVolumeEdit(); return; }
        if (!CanFinishVolume || _volumeDrawFile is not { IsReadOnly: false } file || !OpenFiles.Contains(file)) return;
        var corners = _volumeCorners.ToArray();
        var plane = VolumeDrawing.GetPlane(_volumeSurfaceNormal);
        string name = VolumeDrawing.Name, kind = VolumeDrawing.KindIndex == 0 ? "BlockingVolume" : "TriggerVolume";
        float height = VolumeDrawing.Height;
        IsBusy = true;
        BusyText = "Creating volume model and cooking collision...";
        try
        {
            // Package edits stay on the UI thread, matching the other actor creation tools.
            await Task.Yield();
            if (!OpenFiles.Contains(file) || file.IsReadOnly || _volumeDrawFile != file) return;
            ExportEntry export = DrawnVolumeBuilder.Create(file.LevelExport, name, kind, plane, corners, height);
            ActorProxy actor = Actors.FirstOrDefault(a => a.Export == export);
            if (actor == null)
            {
                actor = ActorProxy.Create(this, export) ?? throw new InvalidOperationException("The created volume cannot be displayed.");
                actor.OwningFile = file;
                AddActor(actor);
            }
            SelectActor(actor, false);
            file.IsDirty = true;
            UndoHistory.Clear();
            _preEditSnapshot = null;
            CancelVolumeDrawing();
        }
        catch (Exception exception) { VolumeDrawStatus = $"Could not create volume: {exception.Message}"; }
        finally { IsBusy = false; BusyText = null; OnPropertyChanged(nameof(CanFinishVolume)); SceneViewer.MarkRenderDirty(); }
    }

    private void RenderVolumeDrawing()
    {
        if (!IsDrawingVolume || !RenderContext.IsReady) return;
        if (_volumeEdit != null) { RenderVolumeEdit(); return; }
        try
        {
            var plane = VolumeDrawing.GetPlane(_volumeSurfaceNormal);
            float step = VolumeDrawing.Step;
            if (!float.IsFinite(step) || step <= 0) return;
            var lines = new List<WorldVertex>();
            void Line(Vector3 a, Vector3 b, Vector4 color)
            { lines.Add(new WorldVertex(a, color, Vector3.Zero)); lines.Add(new WorldVertex(b, color, Vector3.Zero)); }
            for (int i = -10; i <= 10; i++)
            {
                var grid = new Vector4(0.3f, 0.38f, 0.4f, 1);
                Line(plane.Point(new Vector2(-10 * step, i * step)), plane.Point(new Vector2(10 * step, i * step)), i == 0 ? new Vector4(1, 0.25f, 0.2f, 1) : grid);
                Line(plane.Point(new Vector2(i * step, -10 * step)), plane.Point(new Vector2(i * step, 10 * step)), i == 0 ? new Vector4(0.2f, 1, 0.3f, 1) : grid);
            }
            Vector4 outline = _volumeDraftValid ? new Vector4(0.1f, 0.8f, 1, 1) : new Vector4(1, 0.25f, 0.1f, 1);
            for (int i = 0; i < _volumeCorners.Count; i++)
            {
                Vector3 bottom = plane.Point(_volumeCorners[i]), top = plane.Point(_volumeCorners[i], VolumeDrawing.Height);
                if (_volumeCorners.Count > 1)
                {
                    Line(bottom, plane.Point(_volumeCorners[(i + 1) % _volumeCorners.Count]), outline);
                    Line(top, plane.Point(_volumeCorners[(i + 1) % _volumeCorners.Count], VolumeDrawing.Height), outline);
                }
                Line(bottom, top, outline);
                Cross(bottom, new Vector4(1, 1, 1, 1));
                Label(bottom, (i + 1).ToString());
            }
            if (_volumeCorners.Count >= 3)
            {
                Vector3 handle = plane.Point(VolumeHeightCorner, VolumeDrawing.Height);
                Cross(handle, new Vector4(1, 0.85f, 0.1f, 1));
                Label(handle, $"Height {VolumeDrawing.Height:G5}");
            }
            void Cross(Vector3 p, Vector4 color)
            {
                float size = MathF.Max(step * 0.15f, 1);
                Line(p - plane.U * size, p + plane.U * size, color);
                Line(p - plane.V * size, p + plane.V * size, color);
                Line(p - plane.Normal * size, p + plane.Normal * size, color);
            }
            void Label(Vector3 p, string text)
            {
                if (ProjectVolumePoint(p, out Point screen)) RenderContext.ScreenLabels.Add(new ScreenLabel(
                    (float)(screen.X * RenderContext.Width / Math.Max(SceneViewer.ActualWidth, 1)),
                    (float)(screen.Y * RenderContext.Height / Math.Max(SceneViewer.ActualHeight, 1)), text));
            }
            RenderVolumeLines(lines);
        }
        catch (ArgumentException) { /* Invalid numeric settings are reported in the drawing panel. */ }
    }
    private void RenderVolumeLines(List<WorldVertex> lines)
    {
        if (_volumeXrayState == null) _volumeXrayState = new DepthStencilState(RenderContext.Device, new DepthStencilStateDescription { IsDepthEnabled = false, IsStencilEnabled = false });
        var constants = RenderContext.GetWorldConstants(Matrix4x4.Identity);
        constants.Flags |= ShaderFlags.PrimitiveRendering;
        bool wireframe = RenderContext.Wireframe;
        try
        {
            RenderContext.Wireframe = false;
            if (VolumeDrawing.Xray) RenderContext.ImmediateContext.OutputMerger.SetDepthStencilState(_volumeXrayState);
            RenderContext.DefaultEffect.PrepDraw(RenderContext.ImmediateContext, RenderContext.AlphaBlendState, constants);
            RenderContext.DefaultEffect.PrepPrimitiveBuffers(RenderContext, CollectionsMarshal.AsSpan(lines), [], []);
            RenderContext.DefaultEffect.RenderPrimitives(RenderContext, PrimitiveTopology.LineList, 0, lines.Count);
        }
        finally
        {
            RenderContext.ImmediateContext.OutputMerger.SetDepthStencilState(RenderContext.DefaultDepthState);
            RenderContext.Wireframe = wireframe;
        }
    }

}
