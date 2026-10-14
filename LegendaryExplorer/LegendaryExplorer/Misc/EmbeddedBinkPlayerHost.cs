using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LegendaryExplorer.Misc;

/// <summary>
/// Hosts the native window created by the Bink player inside a WinForms viewport.
/// The player runs in a separate process, with its window attached to the viewport.
/// </summary>
internal sealed partial class EmbeddedBinkPlayerHost : IDisposable
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const nint WsChild = 0x40000000;
    private const nint WsVisible = 0x10000000;
    private static readonly nint TopLevelWindowStyles = unchecked((nint)0xA1CF0000);
    private static readonly nint TopLevelExtendedStyles = unchecked((nint)0x00000301);
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int RgnDiff = 4;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 8;
    private const uint GwOwner = 4;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const int VkSpace = 0x20;

    private readonly Control _viewport;
    private CancellationTokenSource _windowSearchCancellation;
    private Process _process;
    private nint _playerWindow;
    private Size _movieSize;
    private bool _disposed;

    public event EventHandler PlayerAttached;
    public event EventHandler PlayerExited;
    public event Action<Exception> EmbeddingFailed;

    public bool IsRunning
    {
        get
        {
            try
            {
                return _process is { HasExited: false };
            }
            catch
            {
                return false;
            }
        }
    }

    public bool CanControl => _playerWindow != 0 && IsRunning;

    public EmbeddedBinkPlayerHost(Control viewport)
    {
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        _viewport.Resize += Viewport_Resize;
        _viewport.HandleDestroyed += Viewport_HandleDestroyed;
        _viewport.Disposed += Viewport_Disposed;
    }

    public void Start(ProcessStartInfo startInfo, string moviePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(moviePath);

        if (IsRunning)
        {
            FocusPlayer();
            return;
        }

        StopProcess();
        _movieSize = ReadMovieSize(moviePath);
        if (!_viewport.IsHandleCreated)
        {
            _viewport.CreateControl();
            _ = _viewport.Handle;
        }
        // Discover the native HWND directly so the player can stay hidden until it is a child
        // of the viewport. Process.MainWindowHandle deliberately excludes hidden windows.
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;

        Process process = Process.Start(startInfo)
                          ?? throw new InvalidOperationException("The Bink player did not start.");
        _process = process;
        process.Exited += Process_Exited;
        process.EnableRaisingEvents = true;
        _windowSearchCancellation = new CancellationTokenSource();
        _ = AttachPlayerWindowAsync(process, _windowSearchCancellation.Token);
    }

    public void FocusPlayer()
    {
        if (_playerWindow != 0)
        {
            SetFocus(_playerWindow);
        }
    }

    public bool TogglePause()
    {
        if (_playerWindow == 0)
        {
            return false;
        }

        PostKey(VkSpace);
        return true;
    }

    /// <summary>
    /// Uses RAD's native click-to-seek control surface. Position is normalized to 0..1.
    /// </summary>
    public bool Seek(double position)
    {
        if (_playerWindow == 0)
        {
            return false;
        }

        position = Math.Clamp(position, 0, 1);
        if (!GetClientRect(_playerWindow, out NativeRect clientRect))
        {
            return false;
        }

        var clientSize = new Size(clientRect.Right, clientRect.Bottom);
        Rectangle video = BinkPlayerViewport.CalculateVideoBounds(clientSize, _movieSize);
        if (video.IsEmpty)
        {
            return false;
        }

        Point seekPoint = BinkPlayerViewport.CalculateSeekPoint(clientSize, _movieSize, position);
        // Refresh RAD's mouse-active seek control even though its drawing strip is clipped.
        // Native window messages reach that control without moving the user's desktop cursor.
        nint point = MakeLParam(seekPoint.X, seekPoint.Y);
        SendMessage(_playerWindow, WmMouseMove, 0, point);
        SendMessage(_playerWindow, WmLButtonDown, 1, point);
        SendMessage(_playerWindow, WmLButtonUp, 0, point);
        return true;
    }

    private static Size ReadMovieSize(string moviePath)
    {
        using FileStream movie = File.OpenRead(moviePath);
        Span<byte> header = stackalloc byte[28];
        movie.ReadExactly(header);
        bool isBink = header[0] == 'B' && header[1] == 'I' && header[2] == 'K'
                      || header[0] == 'K' && header[1] == 'B' && header[2] == '2';
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]);
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]);
        if (!isBink || width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            throw new InvalidDataException("The movie does not contain valid Bink dimensions.");
        }

        return new Size((int)width, (int)height);
    }

    public void StopPlayback()
    {
        StopProcess();
        ClearViewport();
    }

    /// <summary>
    /// Stops the process launched by this host. Call only when the current export is being left.
    /// </summary>
    public void Stop()
    {
        StopProcess();
        ClearViewport();
    }

    private async Task AttachPlayerWindowAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            nint window = 0;
            for (int attempt = 0; attempt < 200 && window == 0; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    return;
                }

                window = FindPlayerWindow(process.Id);
                if (window == 0)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }

            if (window == 0)
            {
                throw new InvalidOperationException("The Bink player did not create a window that LEX could embed.");
            }

            RunOnViewportThread(() =>
            {
                try
                {
                    AttachPlayerWindow(process, window);
                }
                catch (Exception ex)
                {
                    HandleEmbeddingFailure(process, ex);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // The export was changed or the viewer was closed while the player was starting.
        }
        catch (Exception ex)
        {
            RunOnViewportThread(() => HandleEmbeddingFailure(process, ex));
        }
    }

    private static unsafe nint FindPlayerWindow(int processId)
    {
        Rectangle desktop = SystemInformation.VirtualScreen;
        var search = new PlayerWindowSearch
        {
            ProcessId = (uint)processId,
            DesktopLeft = desktop.Left,
            DesktopTop = desktop.Top
        };
        EnumWindows(&FindPlayerWindowCallback, (nint)(&search));
        return search.Window;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int FindPlayerWindowCallback(nint window, nint parameter)
    {
        var search = (PlayerWindowSearch*)parameter;
        GetWindowThreadProcessId(window, out uint processId);
        if (processId != search->ProcessId || GetWindow(window, GwOwner) != 0)
        {
            return 1;
        }

        // RAD creates a BinkWin for playback. Ignore its hidden helper windows, including IME
        // windows, rather than relying on visibility to identify the movie window.
        Span<char> className = stackalloc char[64];
        fixed (char* classNamePointer = className)
        {
            int length = GetClassName(window, classNamePointer, className.Length);
            if (!className[..length].SequenceEqual("BinkWin")
                || !GetClientRect(window, out NativeRect clientRect)
                || clientRect.Right <= 0 || clientRect.Bottom <= 0)
            {
                return 1;
            }
        }

        // RAD first creates a placeholder window, then applies /X and /Y after initializing
        // the movie. Attaching before that step would let it move the child off-screen later.
        if (!GetWindowRect(window, out NativeRect windowRect)
            || windowRect.Right > search->DesktopLeft || windowRect.Bottom > search->DesktopTop)
        {
            return 1;
        }

        search->Window = window;
        return 0;
    }

    private void HandleEmbeddingFailure(Process process, Exception exception)
    {
        if (ReferenceEquals(_process, process))
        {
            StopProcess();
            EmbeddingFailed?.Invoke(exception);
        }
    }

    private void AttachPlayerWindow(Process process, nint window)
    {
        if (_disposed || !ReferenceEquals(_process, process) || process.HasExited)
        {
            return;
        }

        GetWindowThreadProcessId(window, out uint processId);
        if (processId != (uint)process.Id)
        {
            throw new InvalidOperationException("The Bink player window closed before it could be embedded.");
        }

        ShowWindow(window, SwHide);
        nint style = GetWindowLongPtr(window, GwlStyle);
        nint childStyle = (style & ~TopLevelWindowStyles & ~WsVisible) | WsChild;
        SetWindowLongPtr(window, GwlStyle, childStyle);
        nint extendedStyle = GetWindowLongPtr(window, GwlExStyle);
        SetWindowLongPtr(window, GwlExStyle, extendedStyle & ~TopLevelExtendedStyles);

        Marshal.SetLastPInvokeError(0);
        SetParent(window, _viewport.Handle);
        int error = Marshal.GetLastPInvokeError();
        if (error != 0 || GetParent(window) != _viewport.Handle)
        {
            throw new Win32Exception(error, "The Bink player window could not be attached to the LEX viewport.");
        }

        _playerWindow = window;
        ResizePlayer(showWindow: false);
        ShowWindow(window, SwShowNoActivate);
        PlayerAttached?.Invoke(this, EventArgs.Empty);
    }

    private void Process_Exited(object sender, EventArgs e)
    {
        Process exitedProcess = (Process)sender;
        RunOnViewportThread(() =>
        {
            if (!ReferenceEquals(_process, exitedProcess))
            {
                return;
            }

            exitedProcess.Exited -= Process_Exited;
            _process = null;
            _playerWindow = 0;
            exitedProcess.Dispose();
            ClearViewport();
            PlayerExited?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Viewport_Resize(object sender, EventArgs e)
    {
        Process process = _process;
        try
        {
            ResizePlayer();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            if (process != null && ReferenceEquals(_process, process))
            {
                if (process.HasExited)
                {
                    return; // The queued exit event will clear the viewport.
                }
                HandleEmbeddingFailure(process, exception);
            }
        }
    }

    private void ResizePlayer(bool showWindow = true)
    {
        if (_playerWindow != 0 && _viewport.IsHandleCreated)
        {
            if (!SetWindowPos(_playerWindow, 0, 0, 0,
                Math.Max(1, _viewport.ClientSize.Width),
                Math.Max(1, _viewport.ClientSize.Height),
                SwpNoZOrder | SwpNoActivate | SwpFrameChanged))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The Bink player window could not be resized.");
            }
            HideNativeSlider();
            if (showWindow)
            {
                ShowWindow(_playerWindow, SwShowNoActivate);
            }
        }
    }

    private void HideNativeSlider()
    {
        if (!GetClientRect(_playerWindow, out NativeRect clientRect))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The Bink player viewport could not be measured.");
        }

        Rectangle slider = BinkPlayerViewport.CalculateSliderBounds(
            new Size(clientRect.Right, clientRect.Bottom), _movieSize);
        nint region = CreateRectRgn(0, 0, clientRect.Right, clientRect.Bottom);
        nint sliderRegion = CreateRectRgn(slider.Left, slider.Top, slider.Right, slider.Bottom);
        try
        {
            // The native slider is painted into the video, including when it is letterboxed.
            // Clip only that strip; /U8 would also disable the seek input used by LEX.
            if (region == 0 || sliderRegion == 0 || CombineRgn(region, region, sliderRegion, RgnDiff) == 0
                || SetWindowRgn(_playerWindow, region, true) == 0)
            {
                throw new InvalidOperationException("The Bink player's native slider could not be hidden.");
            }

            region = 0; // Windows owns the region after SetWindowRgn succeeds.
        }
        finally
        {
            if (region != 0)
            {
                DeleteObject(region);
            }
            if (sliderRegion != 0)
            {
                DeleteObject(sliderRegion);
            }
        }
    }

    private void PostKey(int virtualKey)
    {
        PostMessage(_playerWindow, WmKeyDown, virtualKey, 1);
        PostMessage(_playerWindow, WmKeyUp, virtualKey, unchecked((nint)0xC0000001));
    }

    private static nint MakeLParam(int lowWord, int highWord)
        => (nint)((highWord << 16) | (lowWord & 0xffff));

    private void StopProcess()
    {
        _windowSearchCancellation?.Cancel();
        _windowSearchCancellation?.Dispose();
        _windowSearchCancellation = null;

        Process process = _process;
        _process = null;
        _playerWindow = 0;
        if (process == null)
        {
            return;
        }

        process.Exited -= Process_Exited;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the checks.
        }
        finally
        {
            process.Dispose();
        }
    }

    private void ClearViewport()
    {
        if (!_viewport.IsDisposed && !_viewport.Disposing && _viewport.IsHandleCreated)
        {
            _viewport.Invalidate();
        }
    }

    private void RunOnViewportThread(Action action)
    {
        if (_disposed || _viewport.IsDisposed || _viewport.Disposing || !_viewport.IsHandleCreated)
        {
            return;
        }

        try
        {
            void InvokeIfAlive()
            {
                // BeginInvoke may have been queued before the owner closed the viewport.
                if (!_disposed && !_viewport.IsDisposed && !_viewport.Disposing && _viewport.IsHandleCreated)
                {
                    action();
                }
            }

            if (_viewport.InvokeRequired)
            {
                _viewport.BeginInvoke((Action)InvokeIfAlive);
            }
            else
            {
                InvokeIfAlive();
            }
        }
        catch (InvalidOperationException)
        {
            // The viewport was destroyed while an asynchronous player event was being delivered.
        }
    }

    private void Viewport_HandleDestroyed(object sender, EventArgs e)
    {
        StopProcess();
    }

    private void Viewport_Disposed(object sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewport.Resize -= Viewport_Resize;
        _viewport.HandleDestroyed -= Viewport_HandleDestroyed;
        _viewport.Disposed -= Viewport_Disposed;
        StopProcess();
        ClearViewport();
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr(nint window, int index, nint newValue);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetParent(nint childWindow, nint newParentWindow);

    [LibraryImport("user32.dll")]
    private static partial nint GetParent(nint window);

    [LibraryImport("user32.dll")]
    private static unsafe partial int EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint parameter);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint window, uint command);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassName(nint window, char* className, int maximumCount);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    private static partial nint SetFocus(nint window);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out NativeRect rect);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    private static partial int CombineRgn(nint destination, nint first, nint second, int mode);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct PlayerWindowSearch
    {
        public uint ProcessId;
        public int DesktopLeft;
        public int DesktopTop;
        public nint Window;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

}
