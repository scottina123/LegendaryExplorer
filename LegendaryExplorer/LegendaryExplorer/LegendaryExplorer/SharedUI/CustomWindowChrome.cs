using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;

namespace LegendaryExplorer.SharedUI
{
    /// <summary>
    /// Provides attached properties for custom window chrome behavior that integrates with the app's theming system.
    /// This class enables dark mode title bars on Windows 10/11 using the DWM API.
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

        #region EnableDarkMode Attached Property

        public static readonly DependencyProperty EnableDarkModeProperty =
            DependencyProperty.RegisterAttached(
                "EnableDarkMode",
                typeof(bool),
                typeof(CustomWindowChrome),
                new PropertyMetadata(false, OnEnableDarkModeChanged));

        public static bool GetEnableDarkMode(DependencyObject obj) =>
            (bool)obj.GetValue(EnableDarkModeProperty);

        public static void SetEnableDarkMode(DependencyObject obj, bool value) =>
            obj.SetValue(EnableDarkModeProperty, value);

        private static void OnEnableDarkModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Window window && (bool)e.NewValue)
            {
                ApplyDarkModeChrome(window);
            }
        }

        #endregion

        #region EnableCustomChrome Attached Property (kept for backward compatibility)

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
        /// Applies dark mode to the window's native title bar using Windows DWM API.
        /// This keeps the standard Windows minimize/maximize/close buttons but renders
        /// them in dark mode style, matching the app's dark theme.
        /// </summary>
        public static void ApplyDarkModeChrome(Window window)
        {
            if (window == null) return;

            // If the window is already loaded, apply immediately
            if (window.IsLoaded)
            {
                ApplyDarkModeToWindowHandle(window);
            }
            else
            {
                // Otherwise, wait for the window to load
                window.Loaded += (s, e) => ApplyDarkModeToWindowHandle(window);
            }
        }

        private static unsafe void ApplyDarkModeToWindowHandle(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            // Try the Windows 10 20H1+ / Windows 11 attribute first
            int useDarkMode = 1;
            int result = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, &useDarkMode, sizeof(int));

            // If that fails, try the older attribute for earlier Windows 10 builds
            if (result != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, &useDarkMode, sizeof(int));
            }
        }

        /// <summary>
        /// Applies custom chrome to a window, which can be used if you want to completely
        /// replace the native title bar with a custom one. For most cases, use ApplyDarkModeChrome instead.
        /// </summary>
        public static void ApplyCustomChrome(Window window)
        {
            if (window == null) return;

            // First apply dark mode to the native chrome
            ApplyDarkModeChrome(window);
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
