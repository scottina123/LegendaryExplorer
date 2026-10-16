using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LegendaryExplorerCore.Scaleform;

namespace LegendaryExplorer.UserControls.ExportLoaderControls.Scaleform;

/// <summary>Native WPF design canvas with world-coordinate dragging, resizing, zoom and pan.</summary>
public sealed class ScaleformDesignSurface : FrameworkElement
{
    internal ScaleformVisualEditor Editor { get; set; }
    public bool ShowOutlines { get; set; }
    private double zoom = 1;
    private Vector pan;
    private bool fitPending = true;
    private bool dragging, resizing, panning;
    private Point dragStart;
    private Vector originalPan;
    private ScaleformMatrix originalMatrix;
    private Rect originalBounds;
    private Matrix parentInverse;
    private static readonly Brush Accent = (Brush)new SolidColorBrush(Color.FromRgb(69, 190, 238)).GetAsFrozen();

    public ScaleformDesignSurface()
    {
        ClipToBounds = true;
        Focusable = true;
        SizeChanged += (_, _) => { if (fitPending) Fit(); else InvalidateVisual(); };
    }

    public Point ToWorld(Point point) => new((point.X - pan.X) / zoom, (point.Y - pan.Y) / zoom);
    public Point ToScreen(Point point) => new(point.X * zoom + pan.X, point.Y * zoom + pan.Y);
    public void Fit()
    {
        if (ActualWidth < 1 || ActualHeight < 1) { fitPending = true; return; }
        var bounds = Editor?.GetStageBounds() ?? new Rect(0, 0, 1280, 720);
        zoom = Math.Clamp(Math.Min(Math.Max(40, ActualWidth - 64) / Math.Max(1, bounds.Width), Math.Max(40, ActualHeight - 64) / Math.Max(1, bounds.Height)), 0.02, 8);
        pan = new Vector((ActualWidth - bounds.Width * zoom) / 2 - bounds.X * zoom, (ActualHeight - bounds.Height * zoom) / 2 - bounds.Y * zoom);
        fitPending = false; Editor?.NotifyZoom(zoom); InvalidateVisual();
    }

    public void ActualSize()
    {
        Point center = ToWorld(new Point(ActualWidth / 2, ActualHeight / 2));
        zoom = 1; pan = new Vector(ActualWidth / 2 - center.X, ActualHeight / 2 - center.Y);
        fitPending = false; Editor?.NotifyZoom(zoom); InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(12, 18, 26)), null, new Rect(RenderSize));
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(30, 120, 155, 175)), 1);
        double grid = 32;
        for (double x = pan.X % grid; x < ActualWidth; x += grid) dc.DrawLine(gridPen, new Point(x, 0), new Point(x, ActualHeight));
        for (double y = pan.Y % grid; y < ActualHeight; y += grid) dc.DrawLine(gridPen, new Point(0, y), new Point(ActualWidth, y));
        if (Editor?.Draft == null)
        {
            DrawHint(dc, "Select a Scaleform movie to open its canvas.", new Point(24, 28));
            return;
        }
        dc.PushTransform(new MatrixTransform(zoom, 0, 0, zoom, pan.X, pan.Y));
        Rect stage = Editor.GetStageBounds();
        dc.DrawRectangle(Editor.GetBackground(), new Pen(new SolidColorBrush(Color.FromRgb(80, 105, 122)), 1 / zoom), stage);
        try
        {
            var artwork = Editor.GetDrawing();
            if (artwork != null) dc.DrawDrawing(artwork);
        }
        catch (Exception ex) { DrawHint(dc, "Preview unavailable: " + ex.Message, new Point(stage.Left + 12, stage.Top + 20)); }
        if (ShowOutlines)
        {
            var outline = new Pen(new SolidColorBrush(Color.FromArgb(100, 69, 190, 238)), 1 / zoom) { DashStyle = DashStyles.Dash };
            foreach (var element in Editor.VisibleElements)
                if (!element.WorldBounds.IsEmpty) dc.DrawRectangle(null, outline, element.WorldBounds);
        }
        var selected = Editor.SelectedElement;
        if (selected != null && !selected.WorldBounds.IsEmpty)
        {
            Rect bounds = selected.WorldBounds;
            dc.DrawRectangle(null, new Pen(Accent, 1.5 / zoom), bounds);
            double handle = 7 / zoom;
            dc.DrawRectangle(Accent, new Pen(Brushes.White, 1 / zoom), new Rect(bounds.BottomRight - new Vector(handle / 2, handle / 2), new Size(handle, handle)));
        }
        dc.Pop();
        DrawHint(dc, "DESIGN PREVIEW", new Point(12, 12));
        if (!Editor.HasVisibleElements)
            DrawHint(dc, "Nothing is visible on this frame. Try the timeline or element outlines.", new Point(12, 33));
        if (Editor.PreviewWarnings.Count > 0)
            DrawHint(dc, "Some artwork uses an approximate preview. " + Editor.PreviewWarnings.Count + " preview notices.", new Point(12, Math.Max(12, ActualHeight - 23)));
    }

    private void DrawHint(DrawingContext dc, string text, Point point)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, new SolidColorBrush(Color.FromRgb(140, 158, 173)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        formatted.MaxTextWidth = Math.Max(20, ActualWidth - 24);
        dc.DrawText(formatted, point);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var pointer = e.GetPosition(this); var world = ToWorld(pointer);
        zoom = Math.Clamp(zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.02, 8);
        pan = new Vector(pointer.X - world.X * zoom, pointer.Y - world.Y * zoom);
        fitPending = false; Editor?.NotifyZoom(zoom); InvalidateVisual(); e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e); Focus();
        dragStart = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space))
        {
            panning = true; originalPan = pan; Cursor = Cursors.ScrollAll; CaptureMouse(); e.Handled = true; return;
        }
        if (e.ChangedButton != MouseButton.Left || Editor?.Draft == null) return;
        Point world = ToWorld(dragStart);
        var current = Editor.SelectedElement;
        resizing = current != null && (ToScreen(current.WorldBounds.BottomRight) - dragStart).Length < 10;
        var hit = resizing ? current : Editor.VisibleElements.Reverse().FirstOrDefault(element => !element.Item.ClipDepth.HasValue &&
            (ShowOutlines || element.Item.Visible && element.Item.ColorTransform.AlphaMultiplier + element.Item.ColorTransform.AlphaOffset / 255.0 > .01) && element.WorldBounds.Contains(world));
        // An element selected inside a group in the Layers panel stays individually draggable.
        if (!resizing && current?.OwnerSpriteId.HasValue == true && current.WorldBounds.Contains(world)) hit = current;
        Editor.Select(hit);
        if (hit == null) return;
        if (e.ClickCount == 2)
        {
            if (hit.Item.Definition?.Code == 39) Editor.EnterGroup(hit.CharacterId);
            else Editor.FocusSelectedText();
            e.Handled = true; return;
        }
        parentInverse = hit.ParentMatrix;
        if (!parentInverse.HasInverse) return;
        parentInverse.Invert();
        originalMatrix = hit.Matrix; originalBounds = hit.LocalBounds;
        dragging = true; Editor.BeginGesture(); CaptureMouse();
        Cursor = resizing ? Cursors.SizeNWSE : Cursors.SizeAll; e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point pointer = e.GetPosition(this);
        if (panning)
        {
            pan = originalPan + (pointer - dragStart); fitPending = false; InvalidateVisual(); return;
        }
        if (!dragging || Editor?.SelectedElement == null) return;
        Vector worldDelta = new((pointer.X - dragStart.X) / zoom, (pointer.Y - dragStart.Y) / zoom);
        var next = originalMatrix;
        if (resizing)
        {
            var matrix = ScaleformVisualEditor.ToWpf(originalMatrix);
            Point anchor = matrix.Transform(originalBounds.TopLeft), corner = matrix.Transform(originalBounds.BottomRight);
            Vector delta = parentInverse.Transform(worldDelta);
            double sx = Math.Abs(corner.X - anchor.X) < 0.0001 ? 1 : (corner.X + delta.X - anchor.X) / (corner.X - anchor.X);
            double sy = Math.Abs(corner.Y - anchor.Y) < 0.0001 ? 1 : (corner.Y + delta.Y - anchor.Y) / (corner.Y - anchor.Y);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) sy = sx;
            if (Math.Abs(sx) < 0.01 || Math.Abs(sy) < 0.01) return;
            next = originalMatrix with { M11 = originalMatrix.M11 * sx, M21 = originalMatrix.M21 * sx,
                M12 = originalMatrix.M12 * sy, M22 = originalMatrix.M22 * sy };
            next = next with { OffsetX = anchor.X - originalBounds.Left * next.M11 - originalBounds.Top * next.M21,
                OffsetY = anchor.Y - originalBounds.Left * next.M12 - originalBounds.Top * next.M22 };
        }
        else
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                if (Math.Abs(worldDelta.X) >= Math.Abs(worldDelta.Y)) worldDelta.Y = 0; else worldDelta.X = 0;
            }
            Vector delta = parentInverse.Transform(worldDelta);
            double x = originalMatrix.OffsetX + delta.X, y = originalMatrix.OffsetY + delta.Y;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { x = Math.Round(x / 10) * 10; y = Math.Round(y / 10) * 10; }
            next = originalMatrix with { OffsetX = x, OffsetY = y };
        }
        Editor.PreviewGesture(next); e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        FinishGesture(); ReleaseMouseCapture(); e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); FinishGesture(); }
    private void FinishGesture()
    {
        if (dragging) { dragging = false; Editor?.EndGesture(); }
        panning = resizing = false; Cursor = Cursors.Arrow;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Editor?.Draft == null) return;
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        try
        {
            if (control && e.Key == Key.Z) Editor.Undo();
            else if (control && e.Key == Key.Y) Editor.Redo();
            else if (e.Key == Key.Delete) Editor.RemoveSelected();
            else if (e.Key == Key.Left) Editor.MoveSelectedBy(-step, 0);
            else if (e.Key == Key.Right) Editor.MoveSelectedBy(step, 0);
            else if (e.Key == Key.Up) Editor.MoveSelectedBy(0, -step);
            else if (e.Key == Key.Down) Editor.MoveSelectedBy(0, step);
            else if (e.Key == Key.F) Fit();
            else return;
            e.Handled = true;
        }
        catch (Exception ex) { Editor.ReportStatus("Could not edit this element: " + ex.Message); }
    }
}
