using System;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.SharpDX;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Fork: the toolbar's camera position and rotation boxes with their steps and ▲/▼ buttons, the (hidden) camera pad, Snap to Camera, snapping to a
/// viewport point, and the arrow keys' first-person camera input
/// </summary>
public partial class LevelEditor
{
    #region Camera position and rotation boxes

    private string _cameraPositionX = "0";
    public string CameraPositionX { get => _cameraPositionX; set => SetProperty(ref _cameraPositionX, value); }

    private string _cameraPositionY = "0";
    public string CameraPositionY { get => _cameraPositionY; set => SetProperty(ref _cameraPositionY, value); }

    private string _cameraPositionZ = "0";
    public string CameraPositionZ { get => _cameraPositionZ; set => SetProperty(ref _cameraPositionZ, value); }

    private string _cameraRotationX = "0";
    public string CameraRotationX { get => _cameraRotationX; set => SetProperty(ref _cameraRotationX, value); }

    private string _cameraRotationY = "0";
    public string CameraRotationY { get => _cameraRotationY; set => SetProperty(ref _cameraRotationY, value); }

    private string _cameraRotationZ = "0";
    public string CameraRotationZ { get => _cameraRotationZ; set => SetProperty(ref _cameraRotationZ, value); }

    private float _cameraPositionStep = 10f;
    public float CameraPositionStep { get => _cameraPositionStep; set => SetProperty(ref _cameraPositionStep, value); }

    private float _cameraRotationStep = 5f;
    public float CameraRotationStep { get => _cameraRotationStep; set => SetProperty(ref _cameraRotationStep, value); }

    private bool _updatingCameraPositionText;
    private int _cameraPositionEditorsFocused;
    private bool _updatingCameraRotationText;
    private int _cameraRotationEditorsFocused;

    private bool AreCameraPositionBoxesFocused() => _cameraPositionEditorsFocused > 0;
    private bool AreCameraRotationBoxesFocused() => _cameraRotationEditorsFocused > 0;

    private void CameraPositionAdjustButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        string[] parts = tag.Split(',');
        if (parts.Length != 2 || !float.TryParse(parts[1], out float direction)) return;

        Vector3 position = RenderContext.Camera.Position;
        if (float.TryParse(CameraPositionX, out float x)) position.X = x;
        if (float.TryParse(CameraPositionY, out float y)) position.Y = y;
        if (float.TryParse(CameraPositionZ, out float z)) position.Z = z;

        float delta = CameraPositionStep * direction;
        switch (parts[0])
        {
            case "X": position.X += delta; break;
            case "Y": position.Y += delta; break;
            case "Z": position.Z += delta; break;
            default: return;
        }

        CameraPositionX = position.X.ToString("0.##");
        CameraPositionY = position.Y.ToString("0.##");
        CameraPositionZ = position.Z.ToString("0.##");
        MoveCameraToEnteredPosition();
    }

    private void CameraRotationAdjustButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        string[] parts = tag.Split(',');
        if (parts.Length != 2 || !float.TryParse(parts[1], out float direction)) return;

        float x = RenderContext.Camera.Roll.RadiansToDegrees();
        float y = RenderContext.Camera.Pitch.RadiansToDegrees();
        float z = RenderContext.Camera.Yaw.RadiansToDegrees();
        if (float.TryParse(CameraRotationX, out float enteredX)) x = enteredX;
        if (float.TryParse(CameraRotationY, out float enteredY)) y = enteredY;
        if (float.TryParse(CameraRotationZ, out float enteredZ)) z = enteredZ;

        float delta = CameraRotationStep * direction;
        switch (parts[0])
        {
            case "X": x += delta; break;
            case "Y": y += delta; break;
            case "Z": z += delta; break;
            default: return;
        }

        CameraRotationX = x.ToString("0.##");
        CameraRotationY = y.ToString("0.##");
        CameraRotationZ = z.ToString("0.##");
        MoveCameraToEnteredRotation();
    }

    private void UpdateCameraPositionText()
    {
        if (AreCameraPositionBoxesFocused()) return;

        _updatingCameraPositionText = true;
        try
        {
            Vector3 position = RenderContext.Camera.Position;
            CameraPositionX = position.X.ToString("0.##");
            CameraPositionY = position.Y.ToString("0.##");
            CameraPositionZ = position.Z.ToString("0.##");
        }
        finally
        {
            _updatingCameraPositionText = false;
        }
    }

    private void UpdateCameraRotationText()
    {
        if (AreCameraRotationBoxesFocused()) return;

        _updatingCameraRotationText = true;
        try
        {
            CameraRotationX = RenderContext.Camera.Roll.RadiansToDegrees().ToString("0.##");
            CameraRotationY = RenderContext.Camera.Pitch.RadiansToDegrees().ToString("0.##");
            CameraRotationZ = RenderContext.Camera.Yaw.RadiansToDegrees().ToString("0.##");
        }
        finally
        {
            _updatingCameraRotationText = false;
        }
    }

    private void ResetCameraRotationText()
    {
        _updatingCameraRotationText = true;
        try
        {
            CameraRotationX = "0";
            CameraRotationY = "0";
            CameraRotationZ = "0";
        }
        finally
        {
            _updatingCameraRotationText = false;
        }
    }

    private void ResetCameraPositionText()
    {
        _updatingCameraPositionText = true;
        try
        {
            CameraPositionX = "0";
            CameraPositionY = "0";
            CameraPositionZ = "0";
        }
        finally
        {
            _updatingCameraPositionText = false;
        }
    }

    private void CameraPositionBoxes_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            MoveCameraToEnteredPosition();
            e.Handled = true;
        }
    }

    private void CameraPositionBoxes_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => _cameraPositionEditorsFocused++;

    private void CameraPositionBoxes_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _cameraPositionEditorsFocused = Math.Max(0, _cameraPositionEditorsFocused - 1);
        if (_updatingCameraPositionText) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_updatingCameraPositionText && !AreCameraPositionBoxesFocused())
            {
                MoveCameraToEnteredPosition();
            }
        }));
    }

    private void MoveCameraToEnteredPosition()
    {
        if (!HasAnyFileOpen) return;

        if (!float.TryParse(CameraPositionX, out float x)
            || !float.TryParse(CameraPositionY, out float y)
            || !float.TryParse(CameraPositionZ, out float z))
        {
            UpdateCameraPositionText();
            return;
        }

        RenderContext.Camera.Position = new Vector3(x, y, z);
        if (RenderContext.Camera.IsOrthographic) OnPropertyChanged(nameof(TopDownClipHeight));
        UpdateCameraPositionText();
        UpdateCameraRotationText();
        SceneViewer?.Focus();
    }

    private void CameraRotationBoxes_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            MoveCameraToEnteredRotation();
            e.Handled = true;
        }
    }

    private void CameraRotationBoxes_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => _cameraRotationEditorsFocused++;

    private void CameraRotationBoxes_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _cameraRotationEditorsFocused = Math.Max(0, _cameraRotationEditorsFocused - 1);
        if (_updatingCameraRotationText) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_updatingCameraRotationText && !AreCameraRotationBoxesFocused())
            {
                MoveCameraToEnteredRotation();
            }
        }));
    }

    private void MoveCameraToEnteredRotation()
    {
        if (!HasAnyFileOpen) return;

        if (!float.TryParse(CameraRotationX, out float x)
            || !float.TryParse(CameraRotationY, out float y)
            || !float.TryParse(CameraRotationZ, out float z))
        {
            UpdateCameraRotationText();
            return;
        }

        RenderContext.Camera.Roll = x.DegreesToRadians();
        RenderContext.Camera.Pitch = y.DegreesToRadians().Clamp(-MathF.PI / 2 + 0.01f, MathF.PI / 2 - 0.01f);
        RenderContext.Camera.Yaw = z.DegreesToRadians();
        UpdateCameraRotationText();
        SceneViewer?.Focus();
    }

    private void CoordinateEditor_GotFocus(object sender, RoutedEventArgs e)
    {
        if (Keyboard.PrimaryDevice.IsKeyDown(Key.Tab) && sender is TextBox tb)
        {
            tb.SelectAll();
        }
    }

    #endregion

    #region Camera pad (hidden in the fork's layout; kept for its handlers)

    private const float CameraButtonMoveStep = 256f;
    private bool _isRotatingCameraFromPad;
    private Point _lastCameraRotatePadPoint;

    private void MoveCameraPlanar(float forwardAmount, float rightAmount)
    {
        if (!HasAnyFileOpen) return;

        float yaw = RenderContext.Camera.Yaw;
        Vector3 planarForward = new(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
        Vector3 planarRight = new(-MathF.Sin(yaw), MathF.Cos(yaw), 0f);
        Vector3 direction = (planarForward * forwardAmount) + (planarRight * rightAmount);
        if (direction.LengthSquared() > 1f)
        {
            direction = Vector3.Normalize(direction);
        }

        RenderContext.Camera.Position += direction * CameraButtonMoveStep;
        UpdateCameraPositionText();
        UpdateCameraRotationText();
        SceneViewer?.Focus();
    }

    private void MoveCameraVertical(float amount)
    {
        if (!HasAnyFileOpen) return;

        RenderContext.Camera.Position += Vector3.UnitZ * (amount * CameraButtonMoveStep);
        UpdateCameraPositionText();
        UpdateCameraRotationText();
        SceneViewer?.Focus();
    }

    private void CameraMoveXYButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        string[] parts = tag.Split(',');
        if (parts.Length != 2) return;

        if (float.TryParse(parts[0], out float forwardAmount)
            && float.TryParse(parts[1], out float rightAmount))
        {
            MoveCameraPlanar(forwardAmount, rightAmount);
        }
    }

    private void CameraMoveZButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && float.TryParse(tag, out float amount))
        {
            MoveCameraVertical(amount);
        }
    }

    private void CameraRotatePad_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasAnyFileOpen || sender is not Border element) return;

        _isRotatingCameraFromPad = true;
        _lastCameraRotatePadPoint = e.GetPosition(element);
        element.CaptureMouse();
        SceneViewer?.Focus();
        e.Handled = true;
    }

    private void CameraRotatePad_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isRotatingCameraFromPad || sender is not Border element) return;

        Point currentPoint = e.GetPosition(element);
        double deltaX = currentPoint.X - _lastCameraRotatePadPoint.X;
        double deltaY = currentPoint.Y - _lastCameraRotatePadPoint.Y;
        _lastCameraRotatePadPoint = currentPoint;

        RenderContext.Camera.Yaw += (float)(deltaX * 0.01);
        RenderContext.Camera.Pitch = (RenderContext.Camera.Pitch - (float)(deltaY * 0.01))
            .Clamp(-MathF.PI / 2 + 0.01f, MathF.PI / 2 - 0.01f);

        UpdateCameraPositionText();
        UpdateCameraRotationText();
        e.Handled = true;
    }

    private void CameraRotatePad_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isRotatingCameraFromPad || sender is not Border element) return;

        _isRotatingCameraFromPad = false;
        element.ReleaseMouseCapture();
        e.Handled = true;
    }

    #endregion

    #region Snapping actors to the camera or a viewport point

    public ICommand SnapActorToCameraCommand { get; private set; }

    private bool CanMoveSelectedActor() => CanMutateScene && SelectedActor is { IsReadOnly: false, CanEditTransform: true, HasSpatialRepresentation: true };

    private void SnapActorToCamera()
    {
        if (!CanMoveSelectedActor()) return;
        // (an undoable edit: OnActorPropertyChanged records it)
        SelectedActor.Location = RenderContext.Camera.Position;
    }

    private void SnapSelectedActorToViewportPoint(Point viewportPoint)
    {
        if (!CanMoveSelectedActor()) return;
        SelectedActor.Location = GetViewportLocationAtSelectedActorDepth(viewportPoint);
    }

    private Vector3 GetViewportLocationAtSelectedActorDepth(Point viewportPoint)
    {
        Vector3 referenceLocation = SelectedActor is { HasSpatialRepresentation: true } actor
            ? actor.Location
            : RenderContext.Camera.EyePosition + RenderContext.Camera.CameraForward * 100f;
        return GetViewportLocationAtDepth(viewportPoint, referenceLocation);
    }

    /// <summary>The point under <paramref name="viewportPoint"/> as deep in the view as <paramref name="referenceLocation"/></summary>
    private Vector3 GetViewportLocationAtDepth(Point viewportPoint, Vector3 referenceLocation)
    {
        float width = MathF.Max((float)SceneViewer.ActualWidth, 1f);
        float height = MathF.Max((float)SceneViewer.ActualHeight, 1f);
        float normalizedX = ((float)viewportPoint.X / width * 2f) - 1f;
        float normalizedY = 1f - ((float)viewportPoint.Y / height * 2f);
        var camera = RenderContext.Camera;
        Vector3 forward = camera.CameraForward;
        Vector3 right = camera.CameraRight;
        Vector3 up = camera.CameraUp;
        Vector3 cameraPosition = camera.EyePosition;

        if (camera.IsOrthographic)
        {
            return cameraPosition
                   + (right * (normalizedX * camera.OrthoWidth * 0.5f))
                   + (up * (normalizedY * camera.OrthoWidth / MathF.Max(camera.aspect, float.Epsilon) * 0.5f))
                   + (forward * Vector3.Dot(referenceLocation - cameraPosition, forward));
        }

        float halfHeightAtUnitDepth = MathF.Tan(camera.FOV * 0.5f);
        Vector3 rayDirection = Vector3.Normalize(forward + right * normalizedX * halfHeightAtUnitDepth * camera.aspect + up * normalizedY * halfHeightAtUnitDepth);
        float denominator = Vector3.Dot(rayDirection, forward);
        if (MathF.Abs(denominator) < 0.0001f)
        {
            return referenceLocation;
        }

        float distance = Vector3.Dot(referenceLocation - cameraPosition, forward) / denominator;
        if (distance <= 0f || float.IsNaN(distance) || float.IsInfinity(distance))
        {
            return referenceLocation;
        }

        return cameraPosition + rayDirection * distance;
    }

    #endregion

    #region Keyboard

    /// <summary>
    /// Window PreviewKeyDown: volume drawing's keys (Esc, Enter, Backspace, Ctrl+Z), and the arrow keys while the viewport has focus (they move the
    /// first-person camera, not keyboard focus)
    /// </summary>
    private void LevelEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        RenderContext.NotifyUserActivity();
        VolumeDrawing_PreviewKeyDown(sender, e);
        if (e.Handled) return;
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right && SceneViewer.IsKeyboardFocusWithin && HasAnyFileOpen)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Fork: in the first-person view, the arrow keys move the camera while the viewport has focus: Up/Down raise and lower it, Left/Right turn it
    /// </summary>
    private void ApplyArrowKeyCameraInput(float timestep)
    {
        if (!HasAnyFileOpen || SceneViewer is null || !SceneViewer.IsKeyboardFocusWithin || !IsActive) return;
        var camera = RenderContext.Camera;
        if (camera.IsOrthographic || !camera.FirstPerson) return;
        if (Keyboard.Modifiers is not ModifierKeys.None and not ModifierKeys.Shift) return;
        float speed = CameraSpeed * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10f : 1f);
        if (Keyboard.IsKeyDown(Key.Up)) camera.Position += Vector3.UnitZ * timestep * speed;
        if (Keyboard.IsKeyDown(Key.Down)) camera.Position -= Vector3.UnitZ * timestep * speed;
        if (Keyboard.IsKeyDown(Key.Left)) camera.Yaw -= timestep * 1.5f;
        if (Keyboard.IsKeyDown(Key.Right)) camera.Yaw += timestep * 1.5f;
    }

    #endregion
}
