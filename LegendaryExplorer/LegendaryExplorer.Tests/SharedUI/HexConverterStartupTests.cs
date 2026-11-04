using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.SharedUI;

[TestClass]
[DoNotParallelize]
public class HexConverterStartupTests
{
    [STATestMethod]
    public void StartupStaysCloakedUntilTheFirstContentFrameAndThenReveals()
    {
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        foreach (Color color in new[] { Color.FromRgb(37, 37, 38), Color.FromRgb(10, 16, 24), Colors.White })
        {
            var window = new Window
            {
                Style = new Style(typeof(Window)),
                Background = new SolidColorBrush(color),
                Content = new Border { Background = new SolidColorBrush(color), Width = 80, Height = 40 },
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowActivated = false,
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            bool rendered = false;
            window.SourceInitialized += (_, _) => HexConverter.App.ApplyNativeTheme(window);
            window.ContentRendered += (_, _) => rendered = true;
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
                window.Show();
                Assert.IsFalse(rendered, "The startup probe must run before WPF's first content frame.");
                Assert.AreEqual(1, GetCloakedState(hwnd) & 1,
                    "The native surface must remain hidden even after Show until its themed content is ready.");
                Assert.AreEqual(color, HwndSource.FromHwnd(hwnd)!.CompositionTarget!.BackgroundColor);

                var frame = new DispatcherFrame();
                var timeout = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background,
                    (_, _) => frame.Continue = false, window.Dispatcher);
                window.ContentRendered += (_, _) => frame.Continue = false;
                try
                {
                    Dispatcher.PushFrame(frame);
                }
                finally
                {
                    timeout.Stop();
                }
                Assert.IsTrue(rendered, "Cloaking must allow the first frame to render without hanging startup.");
                Assert.AreEqual(0, GetCloakedState(hwnd) & 1, "The completed window must be revealed.");
                Assert.AreEqual(1d, window.Opacity);
                Assert.IsTrue(window.IsVisible);
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static int GetCloakedState(IntPtr hwnd)
    {
        int result = DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int));
        Assert.AreEqual(0, result);
        return cloaked;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
