using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;

namespace LegendaryExplorer.SharedUI
{
    /// <summary>
    /// Provides attached properties for custom window chrome behavior that integrates with the app's theming system.
    /// This class enables dark/light mode title bars on Windows 10/11 using the DWM API,
    /// and automatically updates when the app's theme setting changes.
    /// </summary>
    public static class CustomWindowChrome
    {
        #region Win32 DWM Interop for dark mode title bar

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern unsafe int DwmSetWindowAttribute(IntPtr hwnd, int attr, int* attrValue, int attrSize);

        [DllImport("kernel32.dll", EntryPoint = "GetProcAddress")]
        private static extern nint GetProcAddress(nint hModule, nint procName);

        [DllImport("user32.dll")]
        private static extern unsafe int GetClientRect(IntPtr hWnd, RECT* lpRect);

        [DllImport("user32.dll")]
        private static extern unsafe int FillRect(IntPtr hDC, RECT* lprc, IntPtr hbr);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(int crColor);

        [DllImport("gdi32.dll")]
        private static extern int DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const int WM_ERASEBKGND = 0x0014;

        // DWMWA_USE_IMMERSIVE_DARK_MODE - Windows 10 20H1+ and Windows 11
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        // DWMWA_CAPTION_COLOR - Windows 11 only
        private const int DWMWA_CAPTION_COLOR = 35;

        // DWMWA_BORDER_COLOR - Windows 11 only
        private const int DWMWA_BORDER_COLOR = 34;

        // DWMWA_TEXT_COLOR - Windows 11 only
        private const int DWMWA_TEXT_COLOR = 36;

        private const int DwmColorDefault = unchecked((int)0xFFFFFFFF);
        private const int ModernDarkCaptionColor = 0x0021170D; // #0D1721 as COLORREF (BBGGRR)
        private const int ModernDarkCaptionTextColor = 0x00F5F0E8; // #E8F0F5
        private const int ModernDarkWindowBorderColor = 0x00493A2A; // #2A3A49
        private const int ModernDarkClientBackgroundColor = 0x0018100A; // #0A1018
        private const int TraditionalDarkClientBackgroundColor = 0x001E1E1E; // #1E1E1E

        // DWMWA_CLOAK - hides window at compositor level (Windows 8+)
        private const int DWMWA_CLOAK = 13;

        private enum PreferredAppMode
        {
            Default,
            AllowDark,
            ForceDark,
            ForceLight,
            Max
        }

        private static readonly nint _uxThemeModule = LoadUxThemeModule();
        private static readonly nint _setPreferredAppMode = GetUxThemeProcAddress(135);
        private static readonly nint _allowDarkModeForWindow = GetUxThemeProcAddress(133);
        private static readonly nint _flushMenuThemes = GetUxThemeProcAddress(136);

        private static nint LoadUxThemeModule()
        {
            return NativeLibrary.TryLoad("uxtheme.dll", out nint moduleHandle) ? moduleHandle : nint.Zero;
        }

        private static nint GetUxThemeProcAddress(int ordinal)
        {
            if (_uxThemeModule == nint.Zero)
            {
                return nint.Zero;
            }

            return GetProcAddress(_uxThemeModule, (nint)ordinal);
        }

        private static unsafe void ApplyPreferredAppMode(bool isDarkMode)
        {
            if (_setPreferredAppMode != nint.Zero)
            {
                var setPreferredAppMode = (delegate* unmanaged[Stdcall]<PreferredAppMode, PreferredAppMode>)_setPreferredAppMode;
                _ = setPreferredAppMode(isDarkMode ? PreferredAppMode.AllowDark : PreferredAppMode.Default);
            }

            if (_flushMenuThemes != nint.Zero)
            {
                var flushMenuThemes = (delegate* unmanaged[Stdcall]<void>)_flushMenuThemes;
                flushMenuThemes();
            }
        }

        #endregion

        #region Window Tracking

        // Track registered windows for theme updates using weak references
        private static readonly List<WeakReference<Window>> _registeredWindows = new();
        private static readonly HashSet<Window> _cloakedWindows = new();
        private static readonly HashSet<Window> _eraseBkgndHookedWindows = new();
        private static bool _themeChangedSubscribed;

        /// <summary>
        /// Ensures we're subscribed to theme changes.
        /// </summary>
        private static void EnsureThemeChangeSubscription()
        {
            if (!_themeChangedSubscribed)
            {
                ThemeManager.ThemeChanged += OnThemeChanged;
                _themeChangedSubscribed = true;
            }
        }

        /// <summary>
        /// Handler for theme changes - updates all registered windows.
        /// </summary>
        private static void OnThemeChanged(object sender, bool isDarkMode)
        {
            ApplyPreferredAppMode(isDarkMode);

            // Clean up dead references and update all live windows
            _registeredWindows.RemoveAll(wr => !wr.TryGetTarget(out _));

            foreach (var weakRef in _registeredWindows)
            {
                if (weakRef.TryGetTarget(out var window))
                {
                    ApplyWindowTheme(window, isDarkMode);
                }
            }
        }

        /// <summary>
        /// Registers a window for theme management.
        /// </summary>
        private static void RegisterWindow(Window window)
        {
            if (window == null) return;

            // Clean up dead references
            _registeredWindows.RemoveAll(wr => !wr.TryGetTarget(out _));

            // Check if already registered
            foreach (var weakRef in _registeredWindows)
            {
                if (weakRef.TryGetTarget(out var existingWindow) && existingWindow == window)
                    return;
            }

            _registeredWindows.Add(new WeakReference<Window>(window));

            // Remove from tracking when window closes
            window.Closed += (s, e) =>
            {
                _registeredWindows.RemoveAll(wr => 
                    !wr.TryGetTarget(out var w) || w == window);
            };
        }

        #endregion

        #region EnableCustomChrome Attached Property

        public static readonly DependencyProperty EnableCustomChromeProperty =
            DependencyProperty.RegisterAttached(
                "EnableCustomChrome",
                typeof(bool),
                typeof(CustomWindowChrome),
                new PropertyMetadata(false, OnEnableCustomChromeChanged));

        public static bool GetEnableCustomChrome(DependencyObject obj) =>
            (bool)obj.GetValue(EnableCustomChromeProperty);

        public static void SetEnableCustomChrome(DependencyObject obj, bool value) =>
            obj.SetValue(EnableCustomChromeProperty, value);

        private static void OnEnableCustomChromeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Window window && (bool)e.NewValue)
            {
                ApplyCustomChrome(window);
            }
        }

        #endregion

        #region TitleBarHeight Attached Property

        public static readonly DependencyProperty TitleBarHeightProperty =
            DependencyProperty.RegisterAttached(
                "TitleBarHeight",
                typeof(double),
                typeof(CustomWindowChrome),
                new PropertyMetadata(30.0));

        public static double GetTitleBarHeight(DependencyObject obj) =>
            (double)obj.GetValue(TitleBarHeightProperty);

        public static void SetTitleBarHeight(DependencyObject obj, double value) =>
            obj.SetValue(TitleBarHeightProperty, value);

        #endregion

        /// <summary>
        /// Applies themed chrome to the window's native title bar using Windows DWM API.
        /// This keeps the standard Windows minimize/maximize/close buttons but renders
        /// them in dark or light mode style based on the app's current theme setting.
        /// The title bar will automatically update when the theme changes.
        /// </summary>
        public static void ApplyCustomChrome(Window window)
        {
            if (window == null) return;

            bool isDarkMode = Settings.Global_DarkMode_Enabled;
            ApplyPreferredAppMode(isDarkMode);

            // Ensure we're subscribed to theme changes
            EnsureThemeChangeSubscription();

            // Register this window for theme updates
            RegisterWindow(window);

            var helper = new WindowInteropHelper(window);
            if (helper.Handle == IntPtr.Zero)
            {
                helper.EnsureHandle();
            }

            // Apply theme attributes (dark title bar, composition background, etc.)
            ApplyWindowTheme(window, isDarkMode);

            // In dark mode, cloak the window at the DWM compositor level so it is
            // completely invisible until WPF has rendered its first frame. This
            // prevents the white flash because ShowWindow cannot make a cloaked
            // window visible — only uncloaking reveals it, and we defer that until
            // ContentRendered fires (i.e. the first dark frame is ready).
            if (isDarkMode && _cloakedWindows.Add(window))
            {
                CloakWindow(helper.Handle);

                EventHandler uncloakOnRender = null;
                EventHandler uncloakOnClose = null;

                uncloakOnRender = (s, e) =>
                {
                    window.ContentRendered -= uncloakOnRender;
                    window.Closed -= uncloakOnClose;
                    if (_cloakedWindows.Remove(window))
                    {
                        var hwnd = new WindowInteropHelper(window).Handle;
                        if (hwnd != IntPtr.Zero)
                            UncloakWindow(hwnd);
                    }
                };
                uncloakOnClose = (s, e) =>
                {
                    window.ContentRendered -= uncloakOnRender;
                    window.Closed -= uncloakOnClose;
                    if (_cloakedWindows.Remove(window))
                    {
                        var hwnd = new WindowInteropHelper(window).Handle;
                        if (hwnd != IntPtr.Zero)
                            UncloakWindow(hwnd);
                    }
                };

                window.ContentRendered += uncloakOnRender;
                window.Closed += uncloakOnClose;
            }
        }

        private static unsafe void CloakWindow(IntPtr hwnd)
        {
            int cloaked = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, &cloaked, sizeof(int));
        }

        private static unsafe void UncloakWindow(IntPtr hwnd)
        {
            int cloaked = 0;
            DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, &cloaked, sizeof(int));
        }

        private static void ApplyWindowTheme(Window window, bool isDarkMode)
        {
            ApplyThemeToWindowHandle(window, isDarkMode);
            SetCompositionBackgroundColor(window, isDarkMode);

            if (isDarkMode)
            {
                InstallEraseBkgndHook(window);
            }
        }

        /// <summary>
        /// Sets the composition target background color to match the theme.
        /// This controls the DWM compositing surface color shown before WPF renders its first frame.
        /// </summary>
        private static void SetCompositionBackgroundColor(Window window, bool isDarkMode)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: not null } hwndSource)
            {
                hwndSource.CompositionTarget.BackgroundColor = isDarkMode
                    ? ThemeManager.IsModernDark
                        ? Color.FromRgb(0x0A, 0x10, 0x18)
                        : Color.FromRgb(0x1E, 0x1E, 0x1E)
                    : Colors.White;
            }
        }

        /// <summary>
        /// Installs a temporary WM_ERASEBKGND hook that paints the client area dark via GDI.
        /// This provides a synchronous fallback: the GDI surface is painted dark before the
        /// first WPF frame is composited, so even if CompositionTarget.BackgroundColor hasn't
        /// been processed yet the user never sees a white flash. The hook removes itself after
        /// the window's first ContentRendered event.
        /// </summary>
        private static unsafe void InstallEraseBkgndHook(Window window)
        {
            if (!_eraseBkgndHookedWindows.Add(window)) return;

            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                _eraseBkgndHookedWindows.Remove(window);
                return;
            }

            var hwndSource = HwndSource.FromHwnd(hwnd);
            if (hwndSource == null)
            {
                _eraseBkgndHookedWindows.Remove(window);
                return;
            }

            HwndSourceHook hook = null;
            hook = (IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == WM_ERASEBKGND)
                {
                    RECT rc;
                    if (GetClientRect(h, &rc) != 0)
                    {
                        // COLORREF is 0x00BBGGRR — dark background #1E1E1E
                        int backgroundColor = ThemeManager.IsModernDark
                            ? ModernDarkClientBackgroundColor
                            : TraditionalDarkClientBackgroundColor;
                        IntPtr brush = CreateSolidBrush(backgroundColor);
                        _ = FillRect(wParam, &rc, brush);
                        _ = DeleteObject(brush);
                    }
                    handled = true;
                    return (IntPtr)1;
                }
                return IntPtr.Zero;
            };

            hwndSource.AddHook(hook);

            // Remove the hook once WPF has rendered its first frame
            EventHandler rendered = null;
            EventHandler closed = null;
            rendered = (s, e) =>
            {
                window.ContentRendered -= rendered;
                window.Closed -= closed;
                hwndSource.RemoveHook(hook);
                _eraseBkgndHookedWindows.Remove(window);
            };
            closed = (s, e) =>
            {
                window.ContentRendered -= rendered;
                window.Closed -= closed;
                hwndSource.RemoveHook(hook);
                _eraseBkgndHookedWindows.Remove(window);
            };

            window.ContentRendered += rendered;
            window.Closed += closed;
        }

        private static unsafe void ApplyThemeToWindowHandle(Window window, bool isDarkMode)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (_allowDarkModeForWindow != nint.Zero)
            {
                var allowDarkModeForWindow = (delegate* unmanaged[Stdcall]<nint, int, int>)_allowDarkModeForWindow;
                _ = allowDarkModeForWindow(hwnd, isDarkMode ? 1 : 0);
            }

            int useDarkMode = isDarkMode ? 1 : 0;

            // Try the Windows 10 20H1+ / Windows 11 attribute first
            int result = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, &useDarkMode, sizeof(int));

            // If that fails, try the older attribute for earlier Windows 10 builds
            if (result != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, &useDarkMode, sizeof(int));
            }

            // Keep native Windows chrome while matching the application structure.
            // Unsupported colour attributes are harmlessly ignored on Windows 10.
            bool useModernColors = isDarkMode && ThemeManager.IsModernDark;
            int captionColor = useModernColors ? ModernDarkCaptionColor : DwmColorDefault;
            int captionTextColor = useModernColors ? ModernDarkCaptionTextColor : DwmColorDefault;
            int borderColor = useModernColors ? ModernDarkWindowBorderColor : DwmColorDefault;
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, &captionColor, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, &captionTextColor, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, &borderColor, sizeof(int));
        }
    }

    /// <summary>
    /// Commands for custom window chrome title bar buttons.
    /// </summary>
    public static class WindowCommands
    {
        public static readonly RoutedCommand Minimize = new RoutedCommand("Minimize", typeof(WindowCommands));
        public static readonly RoutedCommand MaximizeRestore = new RoutedCommand("MaximizeRestore", typeof(WindowCommands));
        public static readonly RoutedCommand Close = new RoutedCommand("Close", typeof(WindowCommands));

        static WindowCommands()
        {
            // Register command bindings at the application level
            CommandManager.RegisterClassCommandBinding(typeof(Window), 
                new CommandBinding(Minimize, OnMinimizeExecuted, OnCanExecute));
            CommandManager.RegisterClassCommandBinding(typeof(Window), 
                new CommandBinding(MaximizeRestore, OnMaximizeRestoreExecuted, OnCanExecute));
            CommandManager.RegisterClassCommandBinding(typeof(Window), 
                new CommandBinding(Close, OnCloseExecuted, OnCanExecute));
        }

        private static void OnCanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = true;
        }

        private static void OnMinimizeExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (sender is Window window)
            {
                SystemCommands.MinimizeWindow(window);
            }
        }

        private static void OnMaximizeRestoreExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (sender is Window window)
            {
                if (window.WindowState == WindowState.Maximized)
                {
                    SystemCommands.RestoreWindow(window);
                }
                else
                {
                    SystemCommands.MaximizeWindow(window);
                }
            }
        }

        private static void OnCloseExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (sender is Window window)
            {
                SystemCommands.CloseWindow(window);
            }
        }
    }
}
