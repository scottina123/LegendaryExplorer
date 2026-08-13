using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LegendaryExplorer.Misc;

/// <summary>
/// Hosts the native window created by a user-installed Bink player inside a WinForms viewport.
/// The player remains a separate process because RAD's decoder cannot be redistributed with LEX.
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
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const int SwRestore = 9;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const int VkSpace = 0x20;

    private readonly Control _viewport;
    private CancellationTokenSource _windowSearchCancellation;
    private Process _process;
    private nint _playerWindow;
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
    }

    public void Start(ProcessStartInfo startInfo)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(startInfo);

        if (IsRunning)
        {
            FocusPlayer();
            return;
        }

        StopProcess();
        if (!_viewport.IsHandleCreated)
        {
            _viewport.CreateControl();
        }
        // MainWindowHandle ignores a window started with SW_HIDE. Minimized keeps it discoverable
        // while reducing the brief top-level flash before LEX re-parents it into the viewport.
        startInfo.WindowStyle = ProcessWindowStyle.Minimized;

        Process process = Process.Start(startInfo)
                          ?? throw new InvalidOperationException("The Bink player did not start.");
        _process = process;
        process.EnableRaisingEvents = true;
        process.Exited += Process_Exited;
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

        int x = Math.Clamp((int)Math.Round((clientRect.Right - 1) * position), 0, Math.Max(0, clientRect.Right - 1));
        int y = Math.Max(0, clientRect.Bottom - 2);
        var seekPoint = new NativePoint { X = x, Y = y };
        if (!ClientToScreen(_playerWindow, ref seekPoint) || !GetCursorPos(out NativePoint originalCursor))
        {
            return false;
        }

        try
        {
            // RAD reads the cursor position while handling its private bottom-edge slider.
            // Supplying the matching screen position and window messages activates that native
            // seek path without generating a real desktop mouse click.
            if (!SetCursorPos(seekPoint.X, seekPoint.Y))
            {
                return false;
            }

            nint point = MakeLParam(x, y);
            SendMessage(_playerWindow, WmLButtonDown, 1, point);
            SendMessage(_playerWindow, WmLButtonUp, 0, point);
            return true;
        }
        finally
        {
            SetCursorPos(originalCursor.X, originalCursor.Y);
        }
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

                process.Refresh();
                window = process.MainWindowHandle;
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

        nint style = GetWindowLongPtr(window, GwlStyle);
        nint childStyle = (style & ~TopLevelWindowStyles) | WsChild | WsVisible;
        SetWindowLongPtr(window, GwlStyle, childStyle);
        nint extendedStyle = GetWindowLongPtr(window, GwlExStyle);
        SetWindowLongPtr(window, GwlExStyle, extendedStyle & ~TopLevelExtendedStyles);

        Marshal.SetLastPInvokeError(0);
        SetParent(window, _viewport.Handle);
        int error = Marshal.GetLastPInvokeError();
        if (error != 0)
        {
            throw new Win32Exception(error, "The Bink player window could not be attached to the LEX viewport.");
        }

        _playerWindow = window;
        ShowWindow(window, SwRestore);
        ResizePlayer();
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

    private void Viewport_Resize(object sender, EventArgs e) => ResizePlayer();

    private void ResizePlayer()
    {
        if (_playerWindow != 0 && _viewport.IsHandleCreated)
        {
            SetWindowPos(_playerWindow, 0, 0, 0,
                Math.Max(1, _viewport.ClientSize.Width),
                Math.Max(1, _viewport.ClientSize.Height),
                SwpNoZOrder | SwpFrameChanged | SwpShowWindow);
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
        if (!_viewport.IsDisposed)
        {
            _viewport.Invalidate();
        }
    }

    private void RunOnViewportThread(Action action)
    {
        if (_disposed || _viewport.IsDisposed)
        {
            return;
        }

        try
        {
            if (_viewport.InvokeRequired)
            {
                _viewport.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch (InvalidOperationException)
        {
            // The viewport was destroyed while an asynchronous player event was being delivered.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        _viewport.Resize -= Viewport_Resize;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr(nint window, int index, nint newValue);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetParent(nint childWindow, nint newParentWindow);

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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref NativePoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
