using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Point = System.Windows.Point;

namespace LegendaryExplorer.Tools.Meshplorer;

public partial class DrawModelWindow : Window
{
    private readonly List<Vector2> _corners = new();
    private int _dragCorner = -1;
    private bool _ready;
    private Point? _previewDrag;
    private double _previewYaw = -0.8960553846;
    private double _previewPitch = 0.5585993153;
    private double _previewDistance = 3.7749172176;
    private (Model Model, Polys Polys)? _draft;
    public Model ResultModel { get; private set; }
    public Polys ResultPolys { get; private set; }
    public string ResultName { get; private set; }
    public IEntry ResultParent { get; }

    public DrawModelWindow(IMEPackage package, IEntry parent)
    {
        InitializeComponent();
        while (parent is ExportEntry outer && outer.ClassName is not ("Level" or "Package") && outer.Parent != null)
            parent = outer.Parent;
        ResultParent = parent ?? package.Exports.FirstOrDefault(e => e.ClassName == "Level");
        _ready = true;
        UpdatePreviewCamera();
        RefreshDrawing();
    }

    internal void OrbitPreview(double horizontalPixels, double verticalPixels)
    {
        _previewYaw -= horizontalPixels * 0.01;
        _previewPitch = Math.Clamp(_previewPitch + verticalPixels * 0.01, -Math.PI / 2 + 0.02, Math.PI / 2 - 0.02);
        UpdatePreviewCamera();
    }

    private void UpdatePreviewCamera()
    {
        var camera = (PerspectiveCamera)Preview.Camera;
        double horizontal = _previewDistance * Math.Cos(_previewPitch);
        camera.Position = new Point3D(horizontal * Math.Cos(_previewYaw), horizontal * Math.Sin(_previewYaw), _previewDistance * Math.Sin(_previewPitch));
        camera.LookDirection = new Vector3D(-camera.Position.X, -camera.Position.Y, -camera.Position.Z);
    }

    private void Preview_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _previewDrag = e.GetPosition(PreviewSurface);
        PreviewSurface.CaptureMouse();
        e.Handled = true;
    }
    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (_previewDrag is not { } previous || e.LeftButton != MouseButtonState.Pressed) return;
        Point current = e.GetPosition(PreviewSurface);
        OrbitPreview(current.X - previous.X, current.Y - previous.Y);
        _previewDrag = current;
    }
    private void Preview_MouseUp(object sender, MouseButtonEventArgs e) { PreviewSurface.ReleaseMouseCapture(); e.Handled = true; }
    private void Preview_LostMouseCapture(object sender, MouseEventArgs e) => _previewDrag = null;
    private void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _previewDistance = Math.Clamp(_previewDistance * Math.Exp(-e.Delta / 120.0 * 0.12), 1.5, 20);
        UpdatePreviewCamera();
        e.Handled = true;
    }
    private void ResetPreview_Click(object sender, RoutedEventArgs e)
    {
        _previewYaw = -0.8960553846;
        _previewPitch = 0.5585993153;
        _previewDistance = 3.7749172176;
        UpdatePreviewCamera();
    }

    private static float Number(TextBox box)
    {
        if (!float.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out float value)
            || !float.IsFinite(value)) throw new ArgumentException("Enter finite numbers for the origin, base, height and grid.");
        return value;
    }

    // A grid cell is always 32 screen pixels. Changing its size zooms the drawing.
    private float UnitsPerPixel => Math.Clamp(Number(GridStep), 0.01f, 100000f) / 32;
    private Point Screen(Vector2 point) => new(320 + point.X / UnitsPerPixel, 240 - point.Y / UnitsPerPixel);
    private Vector2 World(Point point)
    {
        float x = (float)(point.X - 320) * UnitsPerPixel, y = (float)(240 - point.Y) * UnitsPerPixel;
        if (Snap.IsChecked == true)
        {
            float step = Number(GridStep);
            x = MathF.Round(x / step) * step;
            y = MathF.Round(y / step) * step;
        }
        return new Vector2(x, y);
    }

    private void SettingsChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready) RefreshDrawing();
    }

    private void RefreshDrawing()
    {
        Drawing.Children.Clear();
        Preview.Children.Clear();
        _draft = null;
        CreateButton.IsEnabled = false;
        try
        {
            float grid = Number(GridStep);
            if (grid < 0.01f || grid > 100000) throw new ArgumentException("Grid size must be between 0.01 and 100000 units.");
            for (int x = 0; x <= 640; x += 32)
                Drawing.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = 480, Stroke = x == 320 ? Brushes.SlateGray : Brushes.DarkSlateGray, IsHitTestVisible = false });
            for (int y = 16; y <= 480; y += 32)
                Drawing.Children.Add(new Line { X1 = 0, X2 = 640, Y1 = y, Y2 = y, Stroke = y == 240 ? Brushes.SlateGray : Brushes.DarkSlateGray, IsHitTestVisible = false });
            var outline = new Polygon {
                Points = new PointCollection(_corners.Select(Screen)), Stroke = Brushes.DeepSkyBlue,
                StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(60, 0, 180, 255)), IsHitTestVisible = false
            };
            Drawing.Children.Add(outline);
            for (int i = 0; i < _corners.Count; i++)
            {
                Point p = Screen(_corners[i]);
                var dot = new Ellipse { Width = 10, Height = 10, Fill = Brushes.White, IsHitTestVisible = false };
                Canvas.SetLeft(dot, p.X - 5); Canvas.SetTop(dot, p.Y - 5); Drawing.Children.Add(dot);
                var label = new TextBlock { Text = $"{i + 1}: {_corners[i].X:G5}, {_corners[i].Y:G5}", Foreground = Brushes.White, IsHitTestVisible = false };
                Canvas.SetLeft(label, p.X + 9); Canvas.SetTop(label, p.Y + 6); Drawing.Children.Add(label);
            }
            _draft = ModelGeometry.CreatePrism(_corners, Number(BaseZ), Number(ExtrusionHeight), new Vector2(Number(OriginX), Number(OriginY)));
            DrawPreview(_draft.Value.Model);
            CreateButton.IsEnabled = true;
            DrawStatus.Text = $"{_corners.Count} corners • {_draft.Value.Model.Nodes.Length} faces • closed Model. Coordinates use Unreal units and are local to the owning brush.";
        }
        catch (ArgumentException exception)
        {
            if (Drawing.Children.OfType<Polygon>().FirstOrDefault() is { } outline)
            {
                outline.Stroke = Brushes.OrangeRed;
                outline.Fill = Brushes.Transparent;
            }
            DrawStatus.Text = exception.Message;
        }
    }

    private void DrawPreview(Model model)
    {
        var mesh = new MeshGeometry3D();
        Vector3 center = model.Bounds.Origin;
        float scale = MathF.Max(model.Bounds.SphereRadius, 1);
        foreach (var face in ModelGeometry.GetFaces(model))
        {
            int first = mesh.Positions.Count;
            foreach (var point in face.Vertices)
            {
                Vector3 p = (point - center) / scale;
                mesh.Positions.Add(new Point3D(p.X, p.Y, p.Z));
                mesh.Normals.Add(new Vector3D(face.Normal.X, face.Normal.Y, face.Normal.Z));
            }
            foreach (int index in ModelGeometry.Triangulate(face.Vertices))
                mesh.TriangleIndices.Add(first + index);
        }
        var material = new DiffuseMaterial(Brushes.DeepSkyBlue);
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(90, 90, 90)));
        group.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-2, 3, -4)));
        group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
        Preview.Children.Add(new ModelVisual3D { Content = group });
    }

    private void Drawing_MouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Point position = e.GetPosition(Drawing);
            _dragCorner = _corners.FindIndex(p => (Screen(p) - position).Length < 12);
            if (_dragCorner < 0)
            {
                if (_corners.Count >= 64) { DrawStatus.Text = "Maximum 64 corners. Drag an existing corner or undo."; return; }
                _corners.Add(World(position));
                _dragCorner = _corners.Count - 1;
            }
            Drawing.CaptureMouse();
            RefreshDrawing();
        }
        catch (ArgumentException exception) { DrawStatus.Text = exception.Message; }
        e.Handled = true;
    }

    private void Drawing_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCorner < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        try { _corners[_dragCorner] = World(e.GetPosition(Drawing)); RefreshDrawing(); }
        catch (ArgumentException exception) { DrawStatus.Text = exception.Message; }
    }
    private void Drawing_MouseUp(object sender, MouseButtonEventArgs e) { _dragCorner = -1; Drawing.ReleaseMouseCapture(); }
    private void Drawing_Undo(object sender, MouseButtonEventArgs e) { Undo_Click(sender, e); e.Handled = true; }
    private void Undo_Click(object sender, RoutedEventArgs e) { if (_corners.Count > 0) _corners.RemoveAt(_corners.Count - 1); RefreshDrawing(); }
    private void Clear_Click(object sender, RoutedEventArgs e) { _corners.Clear(); RefreshDrawing(); }
    private void Rectangle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            float size = Number(GridStep) * 3;
            _corners.Clear();
            _corners.AddRange([new(-size, -size), new(size, -size), new(size, size), new(-size, size)]);
            RefreshDrawing();
        }
        catch (ArgumentException exception) { DrawStatus.Text = exception.Message; }
    }
    private void Create_Click(object sender, RoutedEventArgs e)
    {
        RefreshDrawing();
        if (_draft == null) return;
        string name = ModelName.Text.Trim();
        if (name.Length == 0 || name.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
        {
            DrawStatus.Text = "Use a Model name containing only letters, numbers and underscores.";
            return;
        }
        ResultName = name;
        ResultModel = _draft.Value.Model;
        ResultPolys = _draft.Value.Polys;
        DialogResult = true;
    }
}
