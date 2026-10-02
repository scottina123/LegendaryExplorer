using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using LegendaryExplorer.SharedUI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.SharedUI;

[TestClass]
[DoNotParallelize]
public class CustomWindowChromeTests
{
    [STATestMethod]
    public void MaximizeAndRestoreKeepOneSquareAndCloseHonorsCancellation()
    {
        var window = CreateWindow();
        int cancelledCloseCount = 0;
        CancelEventHandler cancelClose = (_, args) =>
        {
            args.Cancel = true;
            cancelledCloseCount++;
        };

        try
        {
            CustomWindowChrome.ApplyCustomChrome(window);
            window.Show();
            FlushDispatcher();

            var button = FindCaptionElement<Button>(window, "MaximizeRestoreButton");
            var path = FindCaptionElement<Path>(window, "MaximizeRestorePath");
            AssertSingleSquare(path.Data);
            Assert.AreSame(WindowCommands.MaximizeRestore, button.Command);
            Assert.AreEqual("Maximize", button.ToolTip);

            ExecuteCaptionCommand(button);
            Assert.AreEqual(WindowState.Maximized, window.WindowState);
            Assert.AreEqual("Restore", button.ToolTip);
            AssertSingleSquare(path.Data);

            ExecuteCaptionCommand(button);
            Assert.AreEqual(WindowState.Normal, window.WindowState);
            Assert.AreEqual("Maximize", button.ToolTip);
            AssertSingleSquare(path.Data);

            window.Closing += cancelClose;
            ExecuteCaptionCommand(FindCaptionElement<Button>(window, "CloseButton"));
            Assert.AreEqual(1, cancelledCloseCount);
            Assert.IsTrue(window.IsVisible, "Cancelling a tool's close guard must leave its window open.");
        }
        finally
        {
            window.Closing -= cancelClose;
            window.Close();
        }
    }

    [STATestMethod]
    public void CaptionButtonsAndCommandsRespectResizeModesAndToolWindows()
    {
        var window = CreateWindow();
        try
        {
            CustomWindowChrome.ApplyCaptionChrome(window);
            window.ApplyTemplate();

            var minimize = FindCaptionElement<Button>(window, "MinimizeButton");
            var maximize = FindCaptionElement<Button>(window, "MaximizeRestoreButton");

            foreach (ResizeMode mode in Enum.GetValues<ResizeMode>())
            {
                window.ResizeMode = mode;
                CommandManager.InvalidateRequerySuggested();
                FlushDispatcher();

                bool canMinimize = mode != ResizeMode.NoResize;
                bool canMaximize = mode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
                Visibility expectedVisibility = canMinimize ? Visibility.Visible : Visibility.Collapsed;
                Assert.AreEqual(expectedVisibility, minimize.Visibility, $"Minimize visibility for {mode}.");
                Assert.AreEqual(expectedVisibility, maximize.Visibility, $"Maximize visibility for {mode}.");
                Assert.AreEqual(canMinimize, WindowCommands.Minimize.CanExecute(null, window),
                    $"Minimize command availability for {mode}.");
                Assert.AreEqual(canMaximize, WindowCommands.MaximizeRestore.CanExecute(null, window),
                    $"Maximize command availability for {mode}.");
                if (mode == ResizeMode.CanMinimize)
                {
                    Assert.IsFalse(maximize.IsEnabled);
                }
            }

            window.ResizeMode = ResizeMode.CanResize;
            window.WindowStyle = WindowStyle.ToolWindow;
            FlushDispatcher();
            Assert.AreEqual(Visibility.Collapsed, minimize.Visibility);
            Assert.AreEqual(Visibility.Collapsed, maximize.Visibility);
            Assert.AreEqual(Visibility.Visible, FindCaptionElement<Button>(window, "CloseButton").Visibility);
        }
        finally
        {
            window.Close();
        }
    }

    [STATestMethod]
    public void BorderlessAndTransparentWindowsKeepTheirExistingChrome()
    {
        foreach (bool allowsTransparency in new[] { false, true })
        {
            var window = CreateWindow();
            window.WindowStyle = WindowStyle.None;
            window.AllowsTransparency = allowsTransparency;
            ControlTemplate originalTemplate = window.Template;
            WindowChrome originalChrome = WindowChrome.GetWindowChrome(window);
            try
            {
                CustomWindowChrome.ApplyCaptionChrome(window);
                Assert.AreSame(originalTemplate, window.Template);
                Assert.AreSame(originalChrome, WindowChrome.GetWindowChrome(window));
            }
            finally
            {
                window.Close();
            }
        }
    }

    [STATestMethod]
    public void LoadedHandlerCoversPlainWindowsWithoutReplacingTheirStyleOrContent()
    {
        var registrationWindow = CreateWindow();
        var window = CreateWindow();
        var content = new Border { Child = new TextBlock { Text = "External tool content" } };
        var style = new Style(typeof(Window));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 17d));
        window.Style = style;
        window.Content = content;

        try
        {
            // Register through the common entry point, then show a plain Window that
            // never calls it, as tools from other referenced projects do.
            CustomWindowChrome.ApplyCustomChrome(registrationWindow);
            window.Show();
            FlushDispatcher();

            Assert.AreSame(style, window.Style);
            Assert.AreSame(content, window.Content);
            Assert.AreEqual(17d, window.FontSize);
            Assert.IsNotNull(WindowChrome.GetWindowChrome(window));
            Assert.IsFalse(WindowChrome.GetWindowChrome(window).UseAeroCaptionButtons);
            AssertSingleSquare(FindCaptionElement<Path>(window, "MaximizeRestorePath").Data);
            Assert.AreSame(WindowCommands.MaximizeRestore,
                FindCaptionElement<Button>(window, "MaximizeRestoreButton").Command);
        }
        finally
        {
            window.Close();
            registrationWindow.Close();
        }
    }

    [STATestMethod]
    public void NativeHitTestingSupportsSnapAndCaptionClicksRespectTheirBounds()
    {
        const uint hitTestMessage = 0x0084;
        const uint pointerMoveMessage = 0x00A0;
        const uint pointerLeaveMessage = 0x02A2;
        const uint pointerDownMessage = 0x00A1;
        const uint pointerUpMessage = 0x00A2;
        var maximizeHit = new IntPtr(9);
        var window = CreateWindow();
        try
        {
            CustomWindowChrome.ApplyCustomChrome(window);
            window.Show();
            FlushDispatcher();

            IntPtr handle = new WindowInteropHelper(window).Handle;
            var maximize = FindCaptionElement<Button>(window, "MaximizeRestoreButton");
            var close = FindCaptionElement<Button>(window, "CloseButton");
            Point maximizePoint = ElementCenterOnScreen(maximize);
            Point closePoint = ElementCenterOnScreen(close);
            Point contentPoint = window.PointToScreen(new Point(window.ActualWidth / 2, window.ActualHeight / 2));
            Assert.AreEqual(maximizeHit, SendMessage(handle, hitTestMessage, IntPtr.Zero, PackScreenPoint(maximizePoint)),
                "Windows must recognize the custom square as its maximize button to offer Snap Layouts.");
            Assert.AreNotEqual(maximizeHit, SendMessage(handle, hitTestMessage, IntPtr.Zero, PackScreenPoint(closePoint)));
            Assert.AreNotEqual(maximizeHit, SendMessage(handle, hitTestMessage, IntPtr.Zero, PackScreenPoint(contentPoint)));

            SendMessage(handle, pointerMoveMessage, maximizeHit, PackScreenPoint(maximizePoint));
            SendMessage(handle, pointerDownMessage, maximizeHit, PackScreenPoint(maximizePoint));
            SendMessage(handle, pointerUpMessage, maximizeHit, PackScreenPoint(maximizePoint));
            FlushDispatcher();
            Assert.AreEqual(WindowState.Maximized, window.WindowState);

            maximizePoint = ElementCenterOnScreen(maximize);
            Assert.AreEqual(maximizeHit, SendMessage(handle, hitTestMessage, IntPtr.Zero, PackScreenPoint(maximizePoint)));
            SendMessage(handle, pointerDownMessage, maximizeHit, PackScreenPoint(maximizePoint));
            SendMessage(handle, pointerUpMessage, maximizeHit, PackScreenPoint(maximizePoint));
            FlushDispatcher();
            Assert.AreEqual(WindowState.Normal, window.WindowState);

            maximizePoint = ElementCenterOnScreen(maximize);
            closePoint = ElementCenterOnScreen(close);
            SendMessage(handle, pointerDownMessage, maximizeHit, PackScreenPoint(maximizePoint));
            SendMessage(handle, pointerUpMessage, maximizeHit, PackScreenPoint(closePoint));
            FlushDispatcher();
            Assert.AreEqual(WindowState.Normal, window.WindowState,
                "Releasing outside the square must cancel the maximize click.");

            SendMessage(handle, pointerDownMessage, maximizeHit, PackScreenPoint(closePoint));
            SendMessage(handle, pointerUpMessage, maximizeHit, PackScreenPoint(closePoint));
            SendMessage(handle, pointerLeaveMessage, IntPtr.Zero, IntPtr.Zero);
            FlushDispatcher();
            Assert.AreEqual(WindowState.Normal, window.WindowState,
                "Nonclient messages outside the square must not maximize a window.");

            window.ResizeMode = ResizeMode.CanMinimize;
            CommandManager.InvalidateRequerySuggested();
            FlushDispatcher();
            Assert.IsFalse(maximize.IsEnabled);
            Assert.AreNotEqual(maximizeHit, SendMessage(handle, hitTestMessage, IntPtr.Zero, PackScreenPoint(maximizePoint)),
                "A disabled maximize button must not advertise Snap Layouts.");

            window.ResizeMode = ResizeMode.CanResize;
            window.WindowStyle = WindowStyle.ToolWindow;
            FlushDispatcher();
            Assert.AreEqual(Visibility.Collapsed, maximize.Visibility);
            Assert.AreNotEqual(maximizeHit, SendMessage(handle, hitTestMessage, IntPtr.Zero, PackScreenPoint(maximizePoint)),
                "A hidden maximize button must not leave behind a native hit target.");
        }
        finally
        {
            window.Close();
        }
    }

    private static Window CreateWindow()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(App).Assembly);

        return new Window
        {
            Width = 360,
            Height = 240,
            Left = -10000,
            Top = -10000,
            Opacity = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
            Resources = (ResourceDictionary)Application.LoadComponent(
                new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative))
        };
    }

    private static T FindCaptionElement<T>(Window window, string name) where T : FrameworkElement
    {
        Assert.IsNotNull(window.Template);
        var element = window.Template.FindName(name, window) as T;
        Assert.IsNotNull(element, $"The shared caption must contain {name}.");
        return element;
    }

    private static void AssertSingleSquare(Geometry geometry)
    {
        PathGeometry path = geometry.GetFlattenedPathGeometry();
        Assert.AreEqual(1, path.Figures.Count, "The window button must draw one outline.");
        Assert.IsTrue(path.Figures[0].IsClosed);
        Assert.IsTrue(geometry.Bounds.Width > 0);
        Assert.AreEqual(geometry.Bounds.Width, geometry.Bounds.Height, 0.001d);
        Assert.AreEqual(geometry.Bounds.Width * geometry.Bounds.Height, geometry.GetArea(), 0.001d,
            "The outline must enclose the complete square without an overlapping restore shape.");
    }

    private static void ExecuteCaptionCommand(Button button)
    {
        var command = button.Command as RoutedCommand;
        Assert.IsNotNull(command);
        IInputElement target = button.CommandTarget ?? button;
        Assert.IsTrue(command.CanExecute(button.CommandParameter, target));
        command.Execute(button.CommandParameter, target);
        FlushDispatcher();
    }

    private static Point ElementCenterOnScreen(FrameworkElement element) =>
        element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));

    private static IntPtr PackScreenPoint(Point point)
    {
        // Native screen coordinates are signed 16-bit words, including the
        // negative coordinates used by these invisible off-screen windows.
        uint packed = (ushort)(short)Math.Round(point.X)
                      | ((uint)(ushort)(short)Math.Round(point.Y) << 16);
        return new IntPtr(unchecked((int)packed));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    private static void FlushDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
