using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using System;
using System.Numerics;
using System.Windows.Input;

namespace LegendaryExplorer.Tools.GalaxyMapEditor;

/// <summary>
/// A render context for the Galaxy Map Editor that provides a locked top-down
/// orthographic (2D) view with pan and zoom instead of the normal 3D fly-through.
/// </summary>
internal sealed class GalaxyMap2DRenderContext : LevelEditorRenderContext
{
    private System.Drawing.Point _lastMouse;
    private System.Drawing.Point _mouseDownPos;
    private MouseButtons _panButton = MouseButtons.None;

    public GalaxyMap2DRenderContext() : base()
    {
        Camera.FirstPerson = true;
        Camera.Pitch = -MathF.PI / 2f; // Look straight down
        Camera.Yaw = 0f;
        Camera.IsOrthographic = true;
        Camera.OrthoSize = 500f;
    }

    public override bool IsActivelyUpdating() => _panButton is not MouseButtons.None || base.IsActivelyUpdating();

    public override bool MouseDown(MouseButtons button, int x, int y)
    {
        _mouseDownPos = new System.Drawing.Point(x, y);
        _lastMouse = _mouseDownPos;

        // Delegate to base first so widget-axis drag detection still works.
        // base returns true only when a widget drag starts (in that case, don't pan).
        bool widgetDrag = base.MouseDown(button, x, y);
        if (!widgetDrag)
        {
            _panButton = button;
        }
        return widgetDrag;
    }

    public override bool MouseUp(MouseButtons button, int x, int y)
    {
        _panButton = MouseButtons.None;
        // Let base handle widget EndDrag, movement-check, and actor hit-test
        return base.MouseUp(button, x, y);
    }

    public override bool MouseMove(int x, int y)
    {
        // Always let base handle an active widget drag
        if (TransformWidget.IsDragging)
        {
            _lastMouse = new System.Drawing.Point(x, y);
            return base.MouseMove(x, y);
        }

        int dx = x - _lastMouse.X;
        int dy = y - _lastMouse.Y;

        if (_panButton is not MouseButtons.None)
        {
            // 2D pan: content follows the pointer
            float worldPerPixel = Camera.OrthoSize * 2f / Height;
            Camera.Position -= Camera.CameraRight * (dx * worldPerPixel);
            Camera.Position += Camera.CameraUp * (dy * worldPerPixel);
            _lastMouse = new System.Drawing.Point(x, y);
            return true;
        }

        _lastMouse = new System.Drawing.Point(x, y);
        // Call base for widget-axis hover highlighting only (no button pressed → no camera movement)
        return base.MouseMove(x, y);
    }

    public override bool MouseScroll(int delta)
    {
        // Zoom: positive delta = scroll up = zoom in (reduce OrthoSize)
        float factor = delta > 0 ? 0.85f : 1.15f;
        Camera.OrthoSize = Math.Max(10f, Camera.OrthoSize * factor);
        return true;
    }

    // Disable WASD / QE keyboard camera movement
    public override bool KeyDown(Key key) => false;
    public override bool KeyUp(Key key) => false;
    public override bool LostKeyboardFocus() => false;

    public override bool LostMouseFocus()
    {
        _panButton = MouseButtons.None;
        return base.LostMouseFocus();
    }
}
