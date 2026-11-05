using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Unreal;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;
using Point = System.Windows.Point;

namespace LegendaryExplorer.Tools.LevelEditor;

public partial class LevelEditor
{
    private VolumeGeometryEdit _volumeEdit;
    private Matrix4x4 _volumeEditActorTransform;
    private int _volumeEditVertex = -1, _volumeEditAxis;
    private float _volumeVertexX, _volumeVertexY, _volumeVertexZ;
    private bool _settingVolumeVertex;
    private Vector3 _volumeEditDragStart, _volumeEditDragOffset;
    private ModelDrawingPlane _volumeEditDragPlane;
    public bool IsEditingVolume => _volumeEdit != null;
    public bool HasSelectedVolumeVertex => IsEditingVolume && _volumeEditVertex >= 0;
    public bool CanEditSelectedVolume => !IsDrawingVolume && SelectedActor is { IsReadOnly: false }
        && VolumeGeometryEdit.FindComponent(SelectedActor.Export) != null;
    public string VolumeDrawingTitle => IsEditingVolume ? "Edit volume • changes are preview-only until commit" : "Draw in loaded level";
    public string VolumeCommitLabel => IsEditingVolume ? "Commit volume edit" : "Create volume";
    public string VolumeUndoLabel => IsEditingVolume ? "Undo edit" : "Undo corner";
    public int VolumeEditAxis { get => _volumeEditAxis; set => SetProperty(ref _volumeEditAxis, value); }
    public float VolumeVertexX { get => _volumeVertexX; set { if (SetProperty(ref _volumeVertexX, value)) SetEditedVertex(); } }
    public float VolumeVertexY { get => _volumeVertexY; set { if (SetProperty(ref _volumeVertexY, value)) SetEditedVertex(); } }
    public float VolumeVertexZ { get => _volumeVertexZ; set { if (SetProperty(ref _volumeVertexZ, value)) SetEditedVertex(); } }

    private void NotifyVolumeEditMode()
    {
        OnPropertyChanged(nameof(IsEditingVolume)); OnPropertyChanged(nameof(CanEditSelectedVolume));
        OnPropertyChanged(nameof(HasSelectedVolumeVertex));
        OnPropertyChanged(nameof(VolumeDrawingTitle)); OnPropertyChanged(nameof(VolumeCommitLabel)); OnPropertyChanged(nameof(VolumeUndoLabel));
    }
    private void EditVolume_Click(object sender, RoutedEventArgs e) => BeginVolumeEdit(SelectedActor);
    private void BeginVolumeEdit(ActorProxy actor)
    {
        if (IsBusy || IsDrawingVolume || actor is not { IsReadOnly: false } || actor.OwningFile == null) return;
        try
        {
            var component = VolumeGeometryEdit.FindComponent(actor.Export);
            if (component == null) return;
            var proxy = actor.Components.FirstOrDefault(c => c.Export == component)
                ?? throw new InvalidOperationException("The volume's BrushComponent is not available in the viewport.");
            var edit = new VolumeGeometryEdit(actor.Export, proxy.LocalToWorld, actor.GetPropertiesForInterpreter());
            CloseLevelLiveMaterialEditor();
            _volumeDrawFile = actor.OwningFile;
            _volumeEditActorTransform = actor.LocalToWorld;
            _volumeEdit = edit;
            _volumeCorners.Clear(); _volumeDrag = -1; _volumeEditVertex = -1; _pickVolumeSurface = false;
            IsDrawingVolume = true;
            NotifyVolumeEditMode(); OnPropertyChanged(nameof(VolumeDrawTarget));
            RebuildVolumeDraft(); SceneViewer.Focus();
        }
        catch (Exception exception) { MessageBox.Show(this, $"Cannot edit this volume:\n{exception.Message}", "Edit volume"); }
    }

    private void SelectVolumeVertex(int index)
    {
        _volumeEditVertex = index;
        OnPropertyChanged(nameof(HasSelectedVolumeVertex));
        if (index < 0) return;
        _settingVolumeVertex = true;
        Vector3 p = _volumeEdit.Vertices[index];
        VolumeVertexX = p.X; VolumeVertexY = p.Y; VolumeVertexZ = p.Z;
        _settingVolumeVertex = false;
    }
    private void SetEditedVertex()
    {
        if (_settingVolumeVertex || _volumeEdit == null || _volumeEditVertex < 0) return;
        _volumeEdit.SaveUndo();
        _volumeEdit.Vertices[_volumeEditVertex] = new Vector3(VolumeVertexX, VolumeVertexY, VolumeVertexZ);
        RebuildVolumeDraft();
    }
    private void RebuildVolumeEdit()
    {
        _volumeDraftValid = false;
        try
        {
            _volumeEdit.Rebuild(); _volumeDraftValid = true;
            VolumeDrawStatus = $"{_volumeEdit.Vertices.Count} vertices, {_volumeEdit.Hulls.Count} convex pieces. Select and drag a vertex, or enter X/Y/Z. Commit creates new Model geometry for this actor only.";
        }
        catch (ArgumentException exception) { VolumeDrawStatus = exception.Message; }
        OnPropertyChanged(nameof(CanFinishVolume)); SceneViewer.MarkRenderDirty();
    }

    private void BeginVolumeVertexDrag(Point pixel, Vector3 rayOrigin, Vector3 direction)
    {
        int vertex = -1;
        double bestDistance = 12;
        float bestDepth = float.PositiveInfinity;
        for (int i = 0; i < _volumeEdit.Vertices.Count; i++)
        {
            Vector3 point = _volumeEdit.Vertices[i];
            if (!ProjectVolumePoint(point, out Point screen)) continue;
            double distance = (screen - pixel).Length;
            float depth = Vector3.Dot(point - rayOrigin, direction);
            if (depth < 0 || distance > bestDistance) continue;
            if (Math.Abs(distance - bestDistance) < 0.5 && depth > bestDepth) continue;
            vertex = i; bestDistance = distance; bestDepth = depth;
        }
        if (vertex < 0) { VolumeDrawStatus = "Click a visible vertex marker to edit it. Empty-space clicks do not add geometry in edit mode."; return; }
        SelectVolumeVertex(vertex); _volumeDrag = vertex;
        _volumeEdit.SaveUndo(); _volumeEditDragStart = _volumeEdit.Vertices[vertex];
        _volumeEditDragPlane = ModelDrawingPlane.FromNormal(_volumeEditDragStart, RenderContext.Camera.CameraForward);
        _volumeEditDragOffset = Vector3.Zero;
        if (_volumeEditDragPlane.IntersectRay(rayOrigin, direction, out Vector2 hit))
            _volumeEditDragOffset = _volumeEditDragStart - _volumeEditDragPlane.Point(hit);
        SceneViewer.CaptureMouse(); SceneViewer.MarkRenderDirty();
    }
    private void MoveVolumeVertex(Vector3 rayOrigin, Vector3 direction)
    {
        Vector3 target = _volumeEditDragStart;
        if (VolumeEditAxis == 0)
        {
            if (!_volumeEditDragPlane.IntersectRay(rayOrigin, direction, out Vector2 hit)) return;
            target = _volumeEditDragPlane.Point(hit) + _volumeEditDragOffset;
        }
        else
        {
            Vector3 axis = VolumeEditAxis == 1 ? Vector3.UnitX : VolumeEditAxis == 2 ? Vector3.UnitY : Vector3.UnitZ;
            Vector3 diff = _volumeEditDragStart - rayOrigin;
            float b = Vector3.Dot(axis, direction), denominator = 1 - b * b;
            if (denominator < 0.0001f) return;
            target += axis * ((b * Vector3.Dot(direction, diff) - Vector3.Dot(axis, diff)) / denominator);
        }
        if (VolumeDrawing.Snap && VolumeDrawing.Step > 0)
        {
            Vector3 delta = target - _volumeEditDragStart;
            float step = VolumeDrawing.Step;
            target = _volumeEditDragStart + new Vector3(MathF.Round(delta.X / step) * step, MathF.Round(delta.Y / step) * step, MathF.Round(delta.Z / step) * step);
        }
        _volumeEdit.Vertices[_volumeDrag] = target;
        SelectVolumeVertex(_volumeDrag); RebuildVolumeDraft();
    }

    private async void CommitVolumeEdit()
    {
        if (!CanFinishVolume || _volumeEdit == null || _volumeDrawFile is not { IsReadOnly: false } file || !OpenFiles.Contains(file)) return;
        var edit = _volumeEdit;
        IsBusy = true; BusyText = "Creating replacement Model and cooking collision...";
        try
        {
            await Task.Yield();
            if (_volumeEdit != edit || !OpenFiles.Contains(file) || file.IsReadOnly) return;
            var actor = Actors.FirstOrDefault(a => a.Export == edit.Actor);
            if (actor == null || actor.LocalToWorld != _volumeEditActorTransform)
                throw new InvalidOperationException("The actor's transform changed during editing. Cancel and reopen the volume edit.");
            edit.Commit();
            file.IsDirty = true;
            CancelVolumeDrawing();
            RefreshActorInViewport(actor);
            UndoHistory.Clear(); _preEditSnapshot = null;
        }
        catch (Exception exception) { VolumeDrawStatus = $"Could not commit volume edit: {exception.Message}"; }
        finally { IsBusy = false; BusyText = null; OnPropertyChanged(nameof(CanFinishVolume)); SceneViewer.MarkRenderDirty(); }
    }

    private void RenderVolumeEdit()
    {
        var lines = new List<WorldVertex>();
        Vector4 color = _volumeDraftValid ? new Vector4(0.1f, 0.8f, 1, 1) : new Vector4(1, 0.2f, 0.1f, 1);
        void Line(Vector3 a, Vector3 b, Vector4 c) { lines.Add(new WorldVertex(a, c, Vector3.Zero)); lines.Add(new WorldVertex(b, c, Vector3.Zero)); }
        foreach (var hull in _volumeEdit.Hulls)
            for (int i = 0; i < hull.FaceTriData.Count; i += 3)
            {
                Vector3 a = Vector3.Transform(hull.VertexData[hull.FaceTriData[i]], _volumeEdit.ComponentToWorld);
                Vector3 b = Vector3.Transform(hull.VertexData[hull.FaceTriData[i + 1]], _volumeEdit.ComponentToWorld);
                Vector3 c = Vector3.Transform(hull.VertexData[hull.FaceTriData[i + 2]], _volumeEdit.ComponentToWorld);
                Line(a, b, color); Line(b, c, color); Line(c, a, color);
            }
        for (int i = 0; i < _volumeEdit.Vertices.Count; i++)
        {
            Vector3 p = _volumeEdit.Vertices[i];
            float size = MathF.Max(Vector3.Distance(RenderContext.Camera.EyePosition, p) * 0.004f, 0.5f);
            Vector4 marker = i == _volumeEditVertex ? new Vector4(1, 0.85f, 0.1f, 1) : Vector4.One;
            Line(p - Vector3.UnitX * size, p + Vector3.UnitX * size, marker);
            Line(p - Vector3.UnitY * size, p + Vector3.UnitY * size, marker);
            Line(p - Vector3.UnitZ * size, p + Vector3.UnitZ * size, marker);
            if ((_volumeEdit.Vertices.Count <= 64 || i == _volumeEditVertex) && ProjectVolumePoint(p, out Point screen))
                RenderContext.ScreenLabels.Add(new ScreenLabel((float)(screen.X * RenderContext.Width / Math.Max(SceneViewer.ActualWidth, 1)),
                    (float)(screen.Y * RenderContext.Height / Math.Max(SceneViewer.ActualHeight, 1)), $"V{i + 1}"));
        }
        RenderVolumeLines(lines);
    }
}
