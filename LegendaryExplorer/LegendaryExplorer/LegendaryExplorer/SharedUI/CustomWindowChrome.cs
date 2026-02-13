using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
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

        // DWMWA_USE_IMMERSIVE_DARK_MODE - Windows 10 20H1+ and Windows 11
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        // DWMWA_CAPTION_COLOR - Windows 11 only
        private const int DWMWA_CAPTION_COLOR = 35;

        // DWMWA_BORDER_COLOR - Windows 11 only
        private const int DWMWA_BORDER_COLOR = 34;

        #endregion

        #region Window Tracking

        // Track registered windows for theme updates using weak references
        private static readonly List<WeakReference<Window>> _registeredWindows = new();
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
            // Clean up dead references and update all live windows
            _registeredWindows.RemoveAll(wr => !wr.TryGetTarget(out _));

            foreach (var weakRef in _registeredWindows)
            {
                if (weakRef.TryGetTarget(out var window))
                {
                    ApplyThemeToWindowHandle(window, isDarkMode);
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

            // Ensure we're subscribed to theme changes
            EnsureThemeChangeSubscription();

            // Register this window for theme updates
            RegisterWindow(window);

            // If the window is already loaded, apply immediately
            if (window.IsLoaded)
            {
                ApplyThemeToWindowHandle(window, Settings.Global_DarkMode_Enabled);
            }
            else
            {
                // Otherwise, wait for the window to load
                window.Loaded += (s, e) => ApplyThemeToWindowHandle(window, Settings.Global_DarkMode_Enabled);
            }
        }

        private static unsafe void ApplyThemeToWindowHandle(Window window, bool isDarkMode)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int useDarkMode = isDarkMode ? 1 : 0;

            // Try the Windows 10 20H1+ / Windows 11 attribute first
            int result = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, &useDarkMode, sizeof(int));

            // If that fails, try the older attribute for earlier Windows 10 builds
            if (result != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, &useDarkMode, sizeof(int));
            }
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
