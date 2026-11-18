using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LegendaryExplorer.SharedUI;
using SharpDX.Direct3D11;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

/// <summary>
/// Fork API kept on upstream's viewport host. Upstream draws every viewport once per desktop composition (<see cref="FramePacer"/>),
/// so the fork's demand-rendering hooks are no-ops here; the rest map onto upstream's control.
/// </summary>
public sealed partial class SceneRenderControl
{
    /// <summary>
    /// Fork: signalled that the scene changed and needed a new frame. Upstream renders continuously, so this does nothing
    /// </summary>
    public void MarkRenderDirty() { }

    private bool _isMouseInputEnabled = true;

    /// <summary>
    /// Whether mouse input reaches the render context. Disabling it releases capture and ends any camera or widget drag in progress
    /// </summary>
    public bool IsMouseInputEnabled
    {
        get => _isMouseInputEnabled;
        set
        {
            if (SetProperty(ref _isMouseInputEnabled, value) && !value)
            {
                if (IsMouseCaptured) ReleaseMouseCapture();
                Context?.LostMouseFocus();
            }
        }
    }

    private bool RejectMouseInput(InputEventArgs e)
    {
        if (_isMouseInputEnabled) return false;
        e.Handled = true;
        return true;
    }

    /// <summary>
    /// Maximum frames drawn per second. Zero (the default) draws a frame every composition, as upstream does
    /// </summary>
    public double MaximumFramesPerSecond { get; set; }

    private long _lastFrameRequestTimestamp;

    private bool IsFrameDue()
    {
        if (MaximumFramesPerSecond <= 0) return true;
        long now = Stopwatch.GetTimestamp();
        //(a little early is fine: the pacer only asks once a composition)
        if (_lastFrameRequestTimestamp != 0
            && Stopwatch.GetElapsedTime(_lastFrameRequestTimestamp, now).TotalSeconds < 0.9 / MaximumFramesPerSecond) return false;
        _lastFrameRequestTimestamp = now;
        return true;
    }

    /// <summary>
    /// Details of a failure to create the viewport's resources, which happens before the renderer can show its own error text
    /// </summary>
    public string ResourceErrorText { get; private set; }

    public ICommand CreateCopyViewportIssuesCommand(Func<string> additionalIssues, Action<string> copyText = null)
    {
        copyText ??= Clipboard.SetText;
        return new GenericCommand(() => copyText(GetViewportIssuesText(additionalIssues?.Invoke())));
    }

    public string GetViewportIssuesText(string additionalIssues = null)
    {
        var sections = new List<string>();
        if (!string.IsNullOrWhiteSpace(ResourceErrorText))
            sections.Add("Viewport resource error:" + Environment.NewLine + ResourceErrorText);
        if (Context is MeshRenderContext { ErrorText: { } errorText } && !string.IsNullOrWhiteSpace(errorText))
            sections.Add("Viewport rendering error:" + Environment.NewLine + errorText);
        if (!string.IsNullOrWhiteSpace(additionalIssues))
            sections.Add("Level issues:" + Environment.NewLine + additionalIssues);
        return sections.Count == 0
            ? "No viewport errors or level issues have been recorded."
            : string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    /// <summary>
    /// Copies the last rendered frame (the backbuffer) into a frozen bitmap. Null before the first frame
    /// </summary>
    public unsafe BitmapSource CaptureBitmap()
    {
        if (Context?.Backbuffer is null)
        {
            return null;
        }

        Texture2DDescription description = Context.Backbuffer.Description;
        description.BindFlags = BindFlags.None;
        description.CpuAccessFlags = CpuAccessFlags.Read;
        description.OptionFlags = ResourceOptionFlags.None;
        description.Usage = ResourceUsage.Staging;

        using var stagingTexture = new Texture2D(Context.Device, description);
        Context.ImmediateContext.CopyResource(Context.Backbuffer, stagingTexture);

        int stride = description.Width * 4;
        byte[] pixels = new byte[stride * description.Height];
        SharpDX.DataBox mapped = Context.ImmediateContext.MapSubresource(stagingTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
        try
        {
            fixed (byte* destination = pixels)
            {
                for (int y = 0; y < description.Height; y++)
                {
                    System.Buffer.MemoryCopy((mapped.DataPointer + y * mapped.RowPitch).ToPointer(), destination + y * stride, stride, stride);
                }
            }
        }
        finally
        {
            Context.ImmediateContext.UnmapSubresource(stagingTexture, 0);
        }

        BitmapSource bitmap = BitmapSource.Create(description.Width, description.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
