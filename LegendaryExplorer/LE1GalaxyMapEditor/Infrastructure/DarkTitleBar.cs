using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using LE1GalaxyMapEditor.Theming;

namespace LE1GalaxyMapEditor.Infrastructure;

/// <summary>Requests native dark caption chrome while retaining Windows resize, snap and accessibility behaviour.</summary>
public static class DarkTitleBar
{
    private const int EraseBackgroundMessage = 0x0014;
    private const int ImmersiveDarkMode = 20;
    private const int ImmersiveDarkModeLegacy = 19;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;
    private const uint RedrawInvalidate = 0x0001;
    private const uint RedrawErase = 0x0004;
    private const uint RedrawUpdateNow = 0x0100;
    private static readonly Dictionary<IntPtr, int> BackgroundColors = [];

    public static void Apply(Window window, EditorTheme theme)
    {
        void ApplyNow(object? sender = null, EventArgs? args = null)
        {
            window.SourceInitialized -= ApplyNow;
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            var background = theme switch
            {
                EditorTheme.Light => Color.FromRgb(0xFF, 0xFF, 0xFF),
                EditorTheme.Dark => Color.FromRgb(0x1E, 0x1E, 0x1E),
                _ => Color.FromRgb(0x0A, 0x10, 0x18)
            };
            if (HwndSource.FromHwnd(handle) is { } source)
            {
                source.CompositionTarget.BackgroundColor = background;
                source.RemoveHook(PaintAppBackground);
                source.AddHook(PaintAppBackground);
                lock (BackgroundColors)
                {
                    BackgroundColors[handle] = ToColorRef(background);
                }

                // WPF can spend a noticeable amount of time measuring complex
                // content after the HWND exists. Ensure any native erase that
                // occurs before its first render already matches the editor.
                RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero,
                    RedrawInvalidate | RedrawErase | RedrawUpdateNow);
            }
            var enabled = theme == EditorTheme.Light ? 0 : 1;
            if (DwmSetWindowAttribute(handle, ImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, ImmersiveDarkModeLegacy, ref enabled, sizeof(int));

            var captionColor = theme switch
            {
                EditorTheme.Light => Color.FromRgb(0xF5, 0xF5, 0xF5),
                EditorTheme.Dark => Color.FromRgb(0x25, 0x25, 0x26),
                _ => Color.FromRgb(0x0D, 0x17, 0x21)
            };
            var textColor = theme switch
            {
                EditorTheme.Light => Color.FromRgb(0x1E, 0x1E, 0x1E),
                EditorTheme.Dark => Color.FromRgb(0xE0, 0xE0, 0xE0),
                _ => Color.FromRgb(0xE8, 0xF0, 0xF5)
            };
            var borderColor = theme switch
            {
                EditorTheme.Light => Color.FromRgb(0xC8, 0xC8, 0xC8),
                EditorTheme.Dark => Color.FromRgb(0x3F, 0x3F, 0x46),
                _ => Color.FromRgb(0x2A, 0x3A, 0x49)
            };
            var caption = ToColorRef(captionColor);
            var text = ToColorRef(textColor);
            var border = ToColorRef(borderColor);
            DwmSetWindowAttribute(handle, CaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, TextColor, ref text, sizeof(int));
            DwmSetWindowAttribute(handle, BorderColor, ref border, sizeof(int));
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) ApplyNow();
        else window.SourceInitialized += ApplyNow;
        window.Closed -= Window_OnClosed;
        window.Closed += Window_OnClosed;
    }

    private static void Window_OnClosed(object? sender, EventArgs eventArgs)
    {
        if (sender is not Window window) return;
        window.Closed -= Window_OnClosed;
        var handle = new WindowInteropHelper(window).Handle;
        lock (BackgroundColors)
        {
            BackgroundColors.Remove(handle);
        }
    }

    private static IntPtr PaintAppBackground(
        IntPtr window,
        int message,
        IntPtr deviceContext,
        IntPtr parameter,
        ref bool handled)
    {
        if (message != EraseBackgroundMessage || deviceContext == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        int backgroundColor;
        lock (BackgroundColors)
        {
            if (!BackgroundColors.TryGetValue(window, out backgroundColor))
            {
                return IntPtr.Zero;
            }
        }

        if (GetClientRect(window, out var clientRect))
        {
            var brush = CreateSolidBrush(backgroundColor);
            if (brush != IntPtr.Zero)
            {
                FillRect(deviceContext, ref clientRect, brush);
                DeleteObject(brush);
            }
        }

        handled = true;
        return new IntPtr(1);
    }

    private static int ToColorRef(Color color) =>
        color.R | color.G << 8 | color.B << 16;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(
        IntPtr window,
        IntPtr updateRect,
        IntPtr updateRegion,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect clientRect);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr deviceContext, ref NativeRect rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int colorRef);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr graphicsObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
