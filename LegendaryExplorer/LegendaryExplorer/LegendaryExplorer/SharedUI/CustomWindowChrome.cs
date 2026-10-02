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
    /// This class enables shared caption buttons and dark/light mode title bars,
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
        private static ResourceDictionary _captionChromeResources;
        private static ControlTemplate _captionWindowTemplate;

        /// <summary>
        /// Ensures we're subscribed to theme changes.
        /// </summary>
        private static void EnsureThemeChangeSubscription()
        {
            if (!_themeChangedSubscribed)
            {
                ThemeManager.ThemeChanged += OnThemeChanged;
                // Include tools from referenced assemblies and plain Window dialogs,
                // which do not necessarily call ApplyCustomChrome themselves.
                EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnWindowLoaded));
                _themeChangedSubscribed = true;
            }
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is Window window && ReferenceEquals(e.OriginalSource, window)
                && !window.AllowsTransparency && window.WindowStyle != WindowStyle.None)
            {
                window.Loaded -= OnWindowLoaded;
                if (_captionWindowTemplate != null && window.Template == _captionWindowTemplate)
                    return;
                ApplyCaptionChrome(window);
                ApplyCustomChrome(window);
            }
        }

        /// <summary>
        /// Installs the shared title bar after a window's XAML has set its final
        /// style, preserving the tool's own styles and content resources.
        /// </summary>
        internal static void ApplyCaptionChrome(Window window)
        {
            if (window.AllowsTransparency || window.WindowStyle == WindowStyle.None
                || (_captionWindowTemplate != null && window.Template == _captionWindowTemplate))
                return;

            _captionChromeResources ??= (ResourceDictionary)Application.LoadComponent(
                new Uri("/LegendaryExplorer;component/LegendaryExplorer/CustomWindowChromeStyles.xaml", UriKind.Relative));
            var style = (Style)_captionChromeResources["CustomChromeWindowStyle"];
            style.Seal();
            foreach (Setter setter in style.Setters)
            {
                if (setter.Property == Control.TemplateProperty)
                {
                    _captionWindowTemplate = (ControlTemplate)setter.Value;
                    window.SetCurrentValue(Control.TemplateProperty, setter.Value);
                }
                else if (setter.Property == WindowChrome.WindowChromeProperty)
                    WindowChrome.SetWindowChrome(window, (WindowChrome)((WindowChrome)setter.Value).Clone());
            }
            window.SetResourceReference(Control.BorderBrushProperty, SystemColors.ActiveBorderBrushKey);
            window.SetCurrentValue(Control.BorderThicknessProperty, new Thickness(1));
            window.ApplyTemplate();

            if (window.Template.FindName("WindowIcon", window) is Image icon)
            {
                icon.MouseLeftButtonDown -= OnWindowIconMouseDown;
                icon.MouseLeftButtonDown += OnWindowIconMouseDown;
            }
            InstallMaximizeButtonHook(window);
        }

        public static readonly DependencyProperty NativeCaptionButtonStateProperty =
            DependencyProperty.RegisterAttached("NativeCaptionButtonState", typeof(int),
                typeof(CustomWindowChrome), new PropertyMetadata(0));

        public static int GetNativeCaptionButtonState(DependencyObject obj) =>
            (int)obj.GetValue(NativeCaptionButtonStateProperty);

        public static void SetNativeCaptionButtonState(DependencyObject obj, int value) =>
            obj.SetValue(NativeCaptionButtonStateProperty, value);

        private static void OnCaptionSourceInitialized(object sender, EventArgs e)
        {
            var window = (Window)sender;
            window.SourceInitialized -= OnCaptionSourceInitialized;
            InstallMaximizeButtonHook(window);
        }

        private static unsafe void InstallMaximizeButtonHook(Window window)
        {
            var handle = new WindowInteropHelper(window).Handle;
            var source = handle == IntPtr.Zero ? null : HwndSource.FromHwnd(handle);
            if (source == null)
            {
                window.SourceInitialized -= OnCaptionSourceInitialized;
                window.SourceInitialized += OnCaptionSourceInitialized;
                return;
            }

            // Windows 11 recognizes custom maximize buttons through HTMAXBUTTON.
            // Install after WindowChrome so this hook runs before its HTCLIENT result.
            // https://learn.microsoft.com/windows/apps/desktop/modernize/ui/apply-snap-layout-menu
            const int hitMaxButton = 9;
            bool pressed = false;
            HwndSourceHook hook = (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message is not (0x0084 or 0x00A0 or 0x00A1 or 0x00A2 or 0x00A3 or 0x02A2 or 0x0200))
                    return IntPtr.Zero;
                if (window.Template.FindName("MaximizeRestoreButton", window) is not Button button)
                    return IntPtr.Zero;

                // Client movement and nonclient leave cancel the native hover/press.
                if (message is 0x02A2 or 0x0200 || !button.IsVisible || !button.IsEnabled)
                {
                    pressed = false;
                    SetNativeCaptionButtonState(button, 0);
                    return IntPtr.Zero;
                }

                long coordinates = lParam.ToInt64();
                var screenPoint = new Point((short)(coordinates & 0xFFFF), (short)((coordinates >> 16) & 0xFFFF));
                Point localPoint = button.PointFromScreen(screenPoint);
                bool inside = localPoint.X >= 0 && localPoint.X < button.ActualWidth
                    && localPoint.Y >= 0 && localPoint.Y < button.ActualHeight;
                if (message == 0x0084 && inside) // WM_NCHITTEST
                {
                    handled = true;
                    return (IntPtr)hitMaxButton;
                }
                if (wParam.ToInt64() != hitMaxButton)
                {
                    pressed = false;
                    SetNativeCaptionButtonState(button, 0);
                    return IntPtr.Zero;
                }

                if (message == 0x00A0 && inside) // WM_NCMOUSEMOVE
                {
                    SetNativeCaptionButtonState(button, pressed ? 2 : 1);
                    var tracking = new TRACKMOUSEEVENT
                    {
                        Size = sizeof(TRACKMOUSEEVENT),
                        Flags = 0x00000012, // TME_LEAVE | TME_NONCLIENT
                        Window = hwnd
                    };
                    TrackMouseEvent(&tracking);
                }
                else if (message is 0x00A1 or 0x00A3) // WM_NCLBUTTONDOWN / DBLCLK
                {
                    pressed = inside;
                    SetNativeCaptionButtonState(button, inside ? 2 : 0);
                    handled = true;
                }
                else if (message == 0x00A2) // WM_NCLBUTTONUP
                {
                    bool execute = pressed && inside;
                    pressed = false;
                    SetNativeCaptionButtonState(button, inside ? 1 : 0);
                    handled = true;
                    if (execute && WindowCommands.MaximizeRestore.CanExecute(null, window))
                        WindowCommands.MaximizeRestore.Execute(null, window);
                }
                return IntPtr.Zero;
            };
            source.AddHook(hook);
            EventHandler closed = null;
            closed = (s, e) =>
            {
                window.Closed -= closed;
                source.RemoveHook(hook);
            };
            window.Closed += closed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TRACKMOUSEEVENT
        {
            public int Size;
            public uint Flags;
            public IntPtr Window;
            public uint HoverTime;
        }

        [DllImport("user32.dll")]
        private static extern unsafe int TrackMouseEvent(TRACKMOUSEEVENT* tracking);

        private static void OnWindowIconMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Image icon && Window.GetWindow(icon) is Window window)
            {
                if (e.ClickCount == 2)
                    SystemCommands.CloseWindow(window);
                else
                    SystemCommands.ShowSystemMenu(window, window.PointToScreen(new Point(0, 30)));
                e.Handled = true;
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
        /// Applies themed chrome using Windows DWM and the shared caption template.
        /// Caption buttons are installed when the window loads, after its XAML sets
        /// WindowStyle and AllowsTransparency, so custom borderless windows retain their chrome.
        /// The title bar will automatically update when the theme changes.
        /// </summary>
        public static void ApplyCustomChrome(Window window)
        {
            if (window == null) return;

            bool isDarkMode = Settings.Global_DarkMode_Enabled;
            ApplyPreferredAppMode(isDarkMode);

            // Ensure we're subscribed to theme changes
            EnsureThemeChangeSubscription();

            // A window created before the class Loaded handler was registered
            // also needs an instance handler so its first load gets the template.
            if (window.IsLoaded)
                ApplyCaptionChrome(window);
            else
            {
                window.Loaded -= OnWindowLoaded;
                window.Loaded += OnWindowLoaded;
            }

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

            // Keep the native frame and shadow while matching the application structure.
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
            if (sender is Window window)
            {
                e.CanExecute = e.Command == Close
                    || (e.Command == Minimize && window.ResizeMode != ResizeMode.NoResize)
                    || (e.Command == MaximizeRestore
                        && window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip);
                e.Handled = true;
            }
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
