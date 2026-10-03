using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.TLK.ME2ME3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

internal static class TLKEditorTabTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void AssertWindowedTabSelectionKeepsPositionsAndDragReorderingStillWorks()
    {
        foreach (string theme in new[] { "Dark", "Light" })
        {
            Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
                new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
            Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri($"/LegendaryExplorer;component/{theme}Theme.xaml", UriKind.Relative)));
            AssertWindowedTabBehavior(theme);
        }
    }

    private static void AssertWindowedTabBehavior(string theme)
    {
        // The editor stays embedded so these in-memory tabs never persist to user settings.
        using var editor = new TLKEditorExportLoader();
        for (int index = 0; index < 8; index++)
        {
            var talkFile = new ME2ME3TalkFile { StringRefs = [new TLKStringRef(100 + index, $"Line {index}")] };
            editor.OpenTabs.Add(new TLKEditorExportLoader.TLKEditorTab($"Test_{index}_INT.tlk", talkFile));
        }
        var originalOrder = editor.OpenTabs.ToArray();
        editor.ActiveTab = originalOrder[0];
        var window = new Window
        {
            Content = editor, Width = 520, Height = 500, WindowState = WindowState.Normal,
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000
        };
        try
        {
            window.Show();
            FlushDispatcher();
            TabControl tabs = FindVisualDescendants<TabControl>(editor).Single();
            var containers = originalOrder.Select(tab => (TabItem)tabs.ItemContainerGenerator.ContainerFromItem(tab)).ToArray();

            foreach (double width in new[] { 520d, 760d })
            {
                window.Width = width;
                editor.ActiveTab = originalOrder[0];
                FlushDispatcher();
                Point[] before = containers.Select(tab => tab.TranslatePoint(default, editor)).ToArray();
                Assert.IsTrue(before.All(point => Math.Abs(point.Y - before[0].Y) < 0.001),
                    $"{theme}: a narrow window must retain a single header row instead of wrapping selected rows.");

                for (int index = 0; index < 2; index++)
                {
                    containers[index].RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    {
                        RoutedEvent = Mouse.MouseDownEvent
                    });
                    FlushDispatcher();
                    Assert.AreSame(originalOrder[index], editor.ActiveTab);
                    for (int tabIndex = 0; tabIndex < containers.Length; tabIndex++)
                    {
                        Point after = containers[tabIndex].TranslatePoint(default, editor);
                        Assert.AreEqual(before[tabIndex].X, after.X, 0.001, $"{theme}: clicking visible tabs must preserve their horizontal positions.");
                        Assert.AreEqual(before[tabIndex].Y, after.Y, 0.001, $"{theme}: clicking tabs must not promote or rearrange header rows.");
                    }
                    CollectionAssert.AreEqual(originalOrder, editor.OpenTabs.ToArray());
                }
            }

            ScrollViewer headerScroll = FindAncestor<ScrollViewer>(containers[0]);
            Assert.IsNotNull(headerScroll, "Overflow tabs must remain accessible by horizontal scrolling.");
            Assert.IsTrue(headerScroll.ScrollableWidth > 0);
            headerScroll.ScrollToRightEnd();
            FlushDispatcher();
            var last = containers[^1];
            Point lastPosition = last.TranslatePoint(default, headerScroll);
            Assert.IsTrue(lastPosition.X >= -0.001 && lastPosition.X + last.ActualWidth <= headerScroll.ViewportWidth + 1,
                "Scrolling to the end must expose the complete last tab and its close button.");

            var headerText = FindVisualDescendants<TextBlock>(containers[0])
                .Single(text => text.Text == originalOrder[0].HeaderText);
            Invoke(editor, "TabControl_PreviewMouseLeftButtonDown", tabs, MouseArgs(headerText, Mouse.PreviewMouseDownEvent));
            Assert.AreSame(originalOrder[0], PendingDrag(editor));
            Invoke(editor, "TabControl_PreviewMouseLeftButtonUp", tabs, MouseArgs(headerText, Mouse.PreviewMouseUpEvent));
            Assert.IsNull(PendingDrag(editor), "Releasing a click must cancel the pending drag.");

            Button close = FindVisualDescendants<Button>(containers[0]).Single();
            Invoke(editor, "TabControl_PreviewMouseLeftButtonDown", tabs, MouseArgs(close, Mouse.PreviewMouseDownEvent));
            Assert.IsNull(PendingDrag(editor), "Clicking the close button must not begin a tab drag.");

            Invoke(editor, "MoveTab", originalOrder[0], originalOrder[2]);
            Assert.AreSame(originalOrder[0], editor.OpenTabs[2], "Intentional drag reordering must retain its existing behavior.");
            Assert.AreSame(originalOrder[0], editor.ActiveTab);
        }
        finally
        {
            window.Close();
        }
    }

    private static MouseButtonEventArgs MouseArgs(DependencyObject source, RoutedEvent routedEvent) =>
        new(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routedEvent, Source = source };

    private static object PendingDrag(TLKEditorExportLoader editor) =>
        typeof(TLKEditorExportLoader).GetField("_tabPendingDrag", PrivateInstance)!.GetValue(editor);

    private static void Invoke(TLKEditorExportLoader editor, string methodName, params object[] arguments) =>
        typeof(TLKEditorExportLoader).GetMethod(methodName, PrivateInstance)!.Invoke(editor, arguments);

    private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
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

    private static void FlushDispatcher() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
