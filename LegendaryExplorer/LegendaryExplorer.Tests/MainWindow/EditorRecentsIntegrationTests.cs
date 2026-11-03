using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.Tools.CoalescedEditor;
using LegendaryExplorer.Tools.MountEditor;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore.Coalesced;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.MainWindow;

[TestClass]
[DoNotParallelize]
public class EditorRecentsIntegrationTests
{
    [STATestMethod]
    public void EditorRecentsReopenPersistPinAndSynchronizeWithoutMixingTools()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(CoalescedEditorWindow).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
        string root = Path.Combine(Path.GetTempPath(), "LEXEditorRecents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var windows = new List<Window>();
        try
        {
            string coalescedProfile = Path.Combine(root, "CoalescedEditor");
            string mountProfile = Path.Combine(root, "MountEditor");
            string state = Path.Combine(root, "state.json");
            string first = WriteCoalesced(root, "First.bin");
            string second = WriteCoalesced(root, "Second.bin");
            var coalesced = Show(new CoalescedEditorWindow(coalescedProfile, state, false), windows);
            var coalescedPeer = Show(new CoalescedEditorWindow(coalescedProfile, Path.Combine(root, "peer.json"), false), windows);
            var mount = Show(new MountEditorWindow(mountProfile), windows);
            var mountPeer = Show(new MountEditorWindow(mountProfile), windows);

            Assert.AreEqual(Visibility.Visible, Recents(coalesced).Visibility);
            AssertHeaders(Recents(coalesced), "ME3", "LE3", "Pinned");
            Assert.AreEqual(MEGame.LE3, Recents(coalesced).SelectedRecentGroup.Game);
            AssertHeaders(Recents(mount), "ME2", "ME3", "LE2", "LE3", "Pinned");
            Assert.IsTrue(Recents(mount).IsVisible, "Mount history must be visible on first opening.");
            Assert.IsGreaterThan(350, Recents(mount).ActualWidth);
            Assert.IsTrue(((Button)FindVisuals<Button>(mount).Single(button => Equals(button.Content, "Load mount file"))).IsVisible);
            coalesced.LoadCoalescedFile(first);
            coalesced.LoadCoalescedFile(second);
            Assert.AreEqual(Visibility.Collapsed, Recents(coalesced).Visibility);
            OpenRecent(coalesced, first);
            Assert.AreEqual(first, coalesced.SelectedFile.FilePath);
            Assert.HasCount(2, coalesced.OpenFiles, "Reopening a recent switches the existing tab.");
            AssertPaths(Recents(coalesced), first, second);
            AssertPaths(Recents(coalescedPeer), first, second);
            Assert.AreEqual(MEGame.LE3, Recents(coalescedPeer).SelectedRecentGroup.Game);
            Assert.HasCount(2, Recents(coalescedPeer).SelectedRecentGroup.Items);
            Assert.IsTrue(Recents(coalescedPeer).RecentItems.All(item => item.Game == null),
                "Standalone files can use the LE3 tab without assigning an unverified game to history.");
            Assert.HasCount(0, Recents(mount).RecentItems);
            Recents(coalesced).PinItem(Recents(coalesced).RecentItems[0]);
            Assert.HasCount(1, Recents(coalescedPeer).PinnedItems);
            Assert.HasCount(0, Recents(mount).PinnedItems);

            coalesced.Close(); // Persist its open tabs, then restore without changing MRU order.
            var restored = Show(new CoalescedEditorWindow(coalescedProfile, state, true), windows);
            Assert.HasCount(2, restored.OpenFiles);
            AssertPaths(Recents(restored), first, second);
            Assert.HasCount(1, Recents(restored).PinnedItems);
            string legacyState = Path.Combine(root, "legacy-state.json");
            File.Copy(state, legacyState);
            var migrated = Show(new CoalescedEditorWindow(Path.Combine(root, "legacy-recents"), legacyState, true), windows);
            Assert.HasCount(2, migrated.OpenFiles);
            AssertPaths(Recents(migrated), first, second);
            string output = Path.Combine(root, "Saved.bin");
            typeof(CoalescedEditorWindow).GetMethod("SaveCoalescedFile", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(restored, new object[] { restored.SelectedFile, output });
            Assert.IsTrue(File.Exists(output));
            AssertPaths(Recents(restored), output, first, second);
            AssertPaths(Recents(coalescedPeer), output, first, second);
            restored.LoadCoalescedFile(Path.Combine(root, "Missing.bin"));
            AssertPaths(Recents(restored), output, first, second);

            string firstMount = WriteMount(root, "First.dlc", 1500);
            string secondMount = WriteMount(root, "Second.dlc", 1600);
            Assert.ThrowsExactly<FileNotFoundException>(() => mount.LoadFile(Path.Combine(root, "Missing.dlc")));
            Assert.IsTrue(Recents(mount).IsVisible, "A failed open must keep the start view available.");
            mount.LoadFile(firstMount);
            Flush();
            Assert.IsFalse(Recents(mount).IsVisible, "Opening a file closes the recent and pinned panel.");
            Assert.AreEqual(500d, mount.ActualWidth, "The editing window should not keep an empty history column.");
            mount.LoadFile(secondMount);
            Assert.AreEqual(MEGame.LE3, Recents(mount).SelectedRecentGroup.Game);
            Assert.HasCount(2, Recents(mount).SelectedRecentGroup.Items);
            OpenFromPanel(Recents(mountPeer), firstMount);
            Assert.IsFalse(Recents(mountPeer).IsVisible, "Opening a visible recent also closes the start view.");
            OpenRecent(mount, firstMount);
            Assert.AreEqual(firstMount, mount.CurrentMountFileText);
            Assert.AreEqual(MEGame.LE3, mount.SelectedGame.Game);
            AssertPaths(Recents(mount), firstMount, secondMount);
            AssertPaths(Recents(mountPeer), firstMount, secondMount);
            Assert.IsTrue(Recents(mount).RecentItems.All(item => item.Game == MEGame.LE3));
            Recents(mount).PinItem(Recents(mount).RecentItems[0]);
            Assert.HasCount(1, Recents(mountPeer).PinnedItems);
            var pinnedMount = Show(new MountEditorWindow(mountProfile), windows);
            Recents(pinnedMount).SelectedRecentGroup = Recents(pinnedMount).PinnedGroup;
            OpenFromPanel(Recents(pinnedMount), firstMount);
            Assert.AreEqual(firstMount, pinnedMount.CurrentMountFileText);
            Assert.IsFalse(Recents(pinnedMount).IsVisible, "Opening a pinned file also closes the start view.");
            string mountOutput = Path.Combine(root, "Saved.dlc");
            mount.SaveFile(mountOutput);
            Assert.AreEqual(1500, new MountFile(mountOutput).MountPriority);
            Assert.AreEqual(mountOutput, mount.CurrentMountFileText);
            AssertPaths(Recents(mountPeer), mountOutput, firstMount, secondMount);
            Assert.ThrowsExactly<FileNotFoundException>(() => mount.LoadFile(Path.Combine(root, "Missing.dlc")));
            AssertPaths(Recents(mount), mountOutput, firstMount, secondMount);
            mount.Close();
            var mountRestored = Show(new MountEditorWindow(mountProfile), windows);
            AssertPaths(Recents(mountRestored), mountOutput, firstMount, secondMount);
            Assert.HasCount(1, Recents(mountRestored).PinnedItems);
            Assert.IsTrue(Recents(mountRestored).IsVisible);
            AssertHeaders(Recents(mountRestored), "ME2", "ME3", "LE2", "LE3", "Pinned");
            AssertPaths(Recents(restored), output, first, second);
        }
        finally
        {
            foreach (Window window in windows) window.Close();
            Directory.Delete(root, true);
        }
    }

    private static T Show<T>(T window, List<Window> windows) where T : Window
    {
        windows.Add(window);
        window.Left = -10000;
        window.Top = -10000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        Flush();
        return window;
    }

    private static RecentsControl Recents(Window window) => (RecentsControl)window.FindName("RecentsController");

    private static void AssertHeaders(RecentsControl control, params string[] expected)
    {
        var tabs = (TabControl)control.FindName("RecentTabs");
        CollectionAssert.AreEqual(expected, tabs.Items.Cast<RecentsControl.RecentItemGroup>().Select(group => group.Header).ToArray());
    }

    private static void OpenFromPanel(RecentsControl control, string path)
    {
        Flush();
        Button button = FindVisuals<Button>(control).Single(item => Equals(item.CommandParameter, path));
        Assert.IsTrue(button.IsVisible);
        Assert.IsTrue(button.Command.CanExecute(path));
        button.Command.Execute(path);
        Flush();
    }

    private static IEnumerable<T> FindVisuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (T descendant in FindVisuals<T>(child)) yield return descendant;
        }
    }

    private static void OpenRecent(Window window, string path)
    {
        var menu = (MenuItem)window.FindName("Recents_MenuItem");
        MenuItem item = menu.Items.OfType<MenuItem>().Single(entry => Equals(entry.Tag, path));
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Flush();
    }

    private static void AssertPaths(RecentsControl control, params string[] expected)
    {
        Flush();
        CollectionAssert.AreEqual(expected, control.RecentItems.Select(item => item.Path).ToArray());
    }

    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static string WriteCoalesced(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        using var data = CoalescedConverter.CompileLE1LE2FromMemory(new Dictionary<string, DuplicatingIni>
        {
            ["Engine.ini"] = DuplicatingIni.ParseIni("[Engine.Test]\r\nValue=Hello\r\n")
        }, MELocalization.INT);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static string WriteMount(string directory, string name, int priority)
    {
        string path = Path.Combine(directory, name);
        new MountFile(MEGame.LE3) { MountPriority = priority, TLKID = 0, MountFlags = new MountFlag(0, false) }
            .WriteMountFile(path);
        return path;
    }
}
