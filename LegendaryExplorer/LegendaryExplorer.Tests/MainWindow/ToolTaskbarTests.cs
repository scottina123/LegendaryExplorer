using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.MainWindow;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.MainWindow;

[TestClass]
[DoNotParallelize]
public class ToolTaskbarTests
{
    [STATestMethod]
    public void PinnedShortcutsSupportLaunchMenusOverflowAndDragReordering()
    {
        // All visual fixtures share this STA so Application remains owned by one dispatcher.
        ShortcutsPreservePinOrderLaunchOnceAndUseTheSharedContextMenu();
        OverflowArrowsReachBothEndsAndDisappearWhenTheRemainingPinsFit();
        BringingAnOffscreenShortcutIntoViewScrollsWithoutVisibleScrollbars();
        DraggingSuppressesLaunchAndPositionsTheInsertionMarker();
    }

    [DataTestMethod]
    [DataRow(-100.0, 4, 0)]
    [DataRow(0.0, 4, 0)]
    [DataRow(20.9, 4, 0)]
    [DataRow(21.0, 4, 1)]
    [DataRow(42.0, 4, 1)]
    [DataRow(104.9, 4, 2)]
    [DataRow(105.0, 4, 3)]
    [DataRow(146.9, 4, 3)]
    [DataRow(147.0, 4, 4)]
    [DataRow(1000.0, 4, 4)]
    [DataRow(1000.0, 0, 0)]
    public void DropsChooseTheNearestGapAndClampToThePinList(double contentX, int count, int expectedGap)
    {
        Assert.AreEqual(expectedGap, ToolTaskbar.GetDropInsertionIndex(contentX, count));
    }

    private static void ShortcutsPreservePinOrderLaunchOnceAndUseTheSharedContextMenu()
    {
        int launches = 0;
        var lastAlphabetically = new Tool { name = "Zulu", open = () => launches++ };
        var firstAlphabetically = new Tool { name = "Alpha", open = () => { } };
        var unavailable = new Tool { name = "Coming soon" };
        using var host = new TaskbarHost([lastAlphabetically, unavailable, firstAlphabetically], width: 240);

        CollectionAssert.AreEqual(new[] { lastAlphabetically, firstAlphabetically },
            host.Items.Items.Cast<Tool>().ToArray(), "The taskbar must preserve pin order rather than sort by name.");
        Button[] shortcuts = host.Shortcuts;
        CollectionAssert.AreEqual(new[] { lastAlphabetically, firstAlphabetically },
            shortcuts.Select(button => (Tool)button.DataContext).ToArray());
        Assert.AreEqual(lastAlphabetically.name, shortcuts[0].ToolTip);

        foreach (Button shortcut in shortcuts)
        {
            Assert.IsInstanceOfType<ToolContextMenu>(shortcut.ContextMenu,
                "Pinned shortcuts must expose the same favorite and taskbar actions as the tool grid.");
            var menu = (ToolContextMenu)shortcut.ContextMenu;
            menu.PlacementTarget = shortcut;
            menu.RefreshTool();
            Assert.AreEqual("Favorite", ((MenuItem)menu.Items[0]).Header);
            Assert.AreEqual("Pin to taskbar", ((MenuItem)menu.Items[1]).Header);
            Assert.IsTrue(((MenuItem)menu.Items[1]).IsEnabled);
        }

        var click = new RoutedEventArgs(Button.ClickEvent);
        shortcuts[0].RaiseEvent(click);
        Assert.IsTrue(click.Handled);
        Assert.AreEqual(1, launches, "A single click on a pinned shortcut must launch its tool.");
        shortcuts[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(1, launches, "Taskbar shortcuts must retain the shared launch handler's duplicate-click protection.");
    }

    private static void OverflowArrowsReachBothEndsAndDisappearWhenTheRemainingPinsFit()
    {
        Tool[] tools = CreateTools(12);
        using var host = new TaskbarHost(tools, width: 240);
        Assert.IsTrue(host.Viewer.ScrollableWidth > 0);
        Assert.AreEqual(Visibility.Visible, host.LeftArrow.Visibility);
        Assert.AreEqual(Visibility.Visible, host.RightArrow.Visibility);
        Assert.IsFalse(host.LeftArrow.IsEnabled);
        Assert.IsTrue(host.RightArrow.IsEnabled);

        ScrollToEnd(host, host.RightArrow, tools.Length);
        Assert.AreEqual(host.Viewer.ScrollableWidth, host.Viewer.HorizontalOffset, 0.5,
            "Repeated right-arrow clicks must reach the last pinned shortcut.");
        Assert.IsFalse(host.RightArrow.IsEnabled);
        Assert.IsTrue(host.LeftArrow.IsEnabled);

        ScrollToEnd(host, host.LeftArrow, tools.Length);
        Assert.AreEqual(0, host.Viewer.HorizontalOffset, 0.5);
        Assert.IsFalse(host.LeftArrow.IsEnabled);
        Assert.IsTrue(host.RightArrow.IsEnabled);

        // Shrink while scrolled to the end, where a stale arrow width or offset can otherwise keep overflow visible.
        ScrollToEnd(host, host.RightArrow, tools.Length);
        host.Taskbar.setToolList(tools.Take(5));
        host.FlushLayout();
        Assert.AreEqual(5, host.Items.Items.Count);
        Assert.AreEqual(Visibility.Collapsed, host.LeftArrow.Visibility);
        Assert.AreEqual(Visibility.Collapsed, host.RightArrow.Visibility);
        Assert.AreEqual(0, host.Viewer.ScrollableWidth, 0.5);
        Assert.AreEqual(0, host.Viewer.HorizontalOffset, 0.5);
    }

    private static void BringingAnOffscreenShortcutIntoViewScrollsWithoutVisibleScrollbars()
    {
        using var host = new TaskbarHost(CreateTools(12), width: 240);
        Button[] shortcuts = host.Shortcuts;
        Assert.AreEqual(ScrollBarVisibility.Hidden, host.Viewer.HorizontalScrollBarVisibility);
        Assert.AreEqual(0, host.Viewer.HorizontalOffset, 0.5);

        // Keyboard navigation uses WPF's bring-into-view path. Exercise it without activating the offscreen window.
        shortcuts[^1].BringIntoView();
        host.FlushLayout();
        Assert.IsTrue(host.Viewer.HorizontalOffset > 0, "The final shortcut must remain reachable when pins overflow.");
        AssertVisibleInViewport(shortcuts[^1], host.Viewer);

        shortcuts[0].BringIntoView();
        host.FlushLayout();
        Assert.AreEqual(0, host.Viewer.HorizontalOffset, 1);
        AssertVisibleInViewport(shortcuts[0], host.Viewer);
    }

    private static void DraggingSuppressesLaunchAndPositionsTheInsertionMarker()
    {
        int launches = 0;
        Tool[] tools = CreateTools(4);
        tools[0].open = () => launches++;
        using var host = new TaskbarHost(tools, width: 240);
        Assert.IsTrue(host.Taskbar.AllowDrop);
        PropertyInfo dragging = typeof(ToolTaskbar).GetProperty("IsDragging", BindingFlags.Instance | BindingFlags.NonPublic)!;
        dragging.SetValue(host.Taskbar, true);
        try
        {
            var click = new RoutedEventArgs(Button.ClickEvent);
            host.Shortcuts[0].RaiseEvent(click);
            Assert.IsTrue(click.Handled);
            Assert.AreEqual(0, launches, "Releasing a dragged shortcut must not launch its tool.");

            FieldInfo position = typeof(ToolTaskbar).GetField("dragPosition", BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo updateMarker = typeof(ToolTaskbar).GetMethod("UpdateDropIndicator", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var marker = (FrameworkElement)host.Taskbar.FindName("DropIndicator");
            foreach ((double pointerX, double markerX) in new[]
                     {
                         (10.0, 0.0), (30.0, 42.0), // Before and after the first icon.
                         (95.0, 84.0), (115.0, 126.0), // Before and after a middle icon.
                         (135.0, 126.0), (155.0, 168.0) // Before and after the last icon.
                     })
            {
                position.SetValue(host.Taskbar, new Point(pointerX, 20));
                updateMarker.Invoke(host.Taskbar, null);
                Assert.AreEqual(Visibility.Visible, marker.Visibility);
                Assert.AreEqual(markerX, ((TranslateTransform)marker.RenderTransform).X, 0.5,
                    "The insertion marker must identify the chosen gap before or after an icon.");
            }
            Assert.AreEqual(0, launches);
        }
        finally
        {
            dragging.SetValue(host.Taskbar, false);
        }
        host.Shortcuts[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(1, launches, "Normal shortcut clicks must still launch after dragging ends.");
    }

    private static Tool[] CreateTools(int count) => Enumerable.Range(0, count)
        .Select(index => new Tool { name = $"Tool {index}", open = () => { } }).ToArray();

    private static void ScrollToEnd(TaskbarHost host, Button arrow, int maximumClicks)
    {
        for (int clicks = 0; arrow.IsEnabled && clicks < maximumClicks; clicks++)
        {
            arrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            host.FlushLayout();
        }
        Assert.IsFalse(arrow.IsEnabled, "The scroll arrow must disable at the corresponding end of the taskbar.");
    }

    private static void AssertVisibleInViewport(Button shortcut, ScrollViewer viewer)
    {
        var viewport = FindVisualDescendants<ScrollContentPresenter>(viewer).Single();
        Rect bounds = shortcut.TransformToAncestor(viewport).TransformBounds(new Rect(shortcut.RenderSize));
        Assert.IsTrue(bounds.Left >= -0.5 && bounds.Right <= viewport.ActualWidth + 0.5,
            $"Shortcut bounds {bounds} must fit inside the {viewport.ActualWidth}-pixel viewport.");
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (T descendant in FindVisualDescendants<T>(child)) yield return descendant;
        }
    }

    private sealed class TaskbarHost : IDisposable
    {
        private readonly Window window;
        public ToolTaskbar Taskbar { get; }
        public ItemsControl Items => (ItemsControl)Taskbar.FindName("PinnedToolList");
        public ScrollViewer Viewer => (ScrollViewer)Taskbar.FindName("TaskbarScrollViewer");
        public Button LeftArrow => (Button)Taskbar.FindName("ScrollLeftButton");
        public Button RightArrow => (Button)Taskbar.FindName("ScrollRightButton");
        public Button[] Shortcuts => FindVisualDescendants<Button>(Items).ToArray();

        public TaskbarHost(IEnumerable<Tool> tools, double width)
        {
            typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, typeof(ToolTaskbar).Assembly);
            _ = Application.Current ?? new Application();
            if (Application.Current.CheckAccess())
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
                new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));

            // Host only the control: do not initialize the real launcher or load/save the user's settings.
            Taskbar = new ToolTaskbar();
            Taskbar.setToolList(tools);
            window = new Window
            {
                Content = Taskbar,
                Width = width,
                Height = 40,
                Style = new Style(typeof(Window)),
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Left = -10000,
                Top = -10000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            try
            {
                window.Show();
                FlushLayout();
            }
            catch
            {
                window.Close();
                throw;
            }
        }

        public void FlushLayout()
        {
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }

        public void Dispose() => window.Close();
    }
}
