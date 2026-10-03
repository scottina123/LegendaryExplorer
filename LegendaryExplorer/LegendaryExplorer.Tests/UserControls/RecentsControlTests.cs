using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class RecentsControlTests
{
    private static RecentsControl CreateControl()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(RecentsControl).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
        return new RecentsControl { RecentsMenu = new MenuItem() };
    }

    [STATestMethod]
    public void RecentTabsPreserveHistoryAndStayInSync()
    {
        // Application and the shared icon resources must stay on the same STA thread.
        TabsKeepSeparateHistoriesAndRouteNonPccFilesToMisc();
        SavedHistoryLoadsTabsAndIncomingUpdatesPreserveSelection();
        PinnedFilesPersistWithoutHistoryLimitsAndFilterByPath();
        PinnedFilesStayInSyncOnlyForTheSameTool();
        FileMenuOffersPinningWithoutRecentHistory();
        PinsOnlyInitializationPreservesLegacyRecentFiles();
    }

    private static void TabsKeepSeparateHistoriesAndRouteNonPccFilesToMisc()
    {
        using var control = CreateControl();
        CollectionAssert.AreEqual(new[] { "ME1", "ME2", "ME3", "LE1", "LE2", "LE3", "Misc", "Pinned" },
            control.RecentGroups.Select(group => group.Header).ToArray());
        Assert.IsTrue(control.PinnedGroup.IsPinned);

        foreach (var game in new[] { MEGame.ME1, MEGame.ME2, MEGame.ME3, MEGame.LE1, MEGame.LE2, MEGame.LE3 })
        {
            for (int index = 0; index < 12; index++)
            {
                control.AddRecent($@"C:\RecentsTests\{game}_{index}.pcc", false, game);
            }
        }

        foreach (var group in control.RecentGroups.Where(group => group.Game != null))
        {
            Assert.HasCount(10, group.Items);
            Assert.AreEqual($@"C:\RecentsTests\{group.Game}_11.pcc", group.Items[0].Path);
            Assert.AreEqual($@"C:\RecentsTests\{group.Game}_2.pcc", group.Items[9].Path);
        }

        control.AddRecent(@"C:\RECENTSTESTS\LE3_5.PCC", false, MEGame.LE3);
        Assert.HasCount(60, control.RecentItems, "Reopening a path with different casing must not duplicate it.");
        Assert.AreEqual(@"C:\RECENTSTESTS\LE3_5.PCC", control.SelectedRecentGroup.Items[0].Path);
        Assert.AreEqual(MEGame.LE3, control.SelectedRecentGroup.Game);

        control.AddRecent(@"C:\RecentsTests\Default.sfar", false, MEGame.ME3);
        control.AddRecent(@"C:\RecentsTests\Audio.AFC", false, MEGame.LE3);
        control.AddRecent(@"C:\RecentsTests\Unknown.pcc", false, null);
        control.AddRecent(@"C:\RecentsTests\UnknownGame.pcc", false, MEGame.Unknown);
        control.AddRecent(@"C:\RecentsTests\Editor.pcc", false, MEGame.UDK);
        var misc = control.RecentGroups.Single(group => group.Game == null && !group.IsPinned);
        Assert.AreSame(misc, control.SelectedRecentGroup);
        Assert.HasCount(5, misc.Items);
        Assert.IsTrue(misc.Items.Single(item => item.Path.EndsWith(".AFC")).IsAfc);

        for (int index = 0; index < 12; index++)
        {
            control.AddRecent($@"C:\RecentsTests\Conditions{index}.cnd", false, null);
        }
        Assert.HasCount(10, misc.Items);
        Assert.HasCount(70, control.RecentItems);
        Assert.HasCount(71, control.RecentsMenu.Items);

        using var folderControl = CreateControl();
        folderControl.IsFolderRecents = true;
        folderControl.AddRecent(@"C:\RecentsTests\Folder.pcc", false, MEGame.LE3);
        Assert.AreEqual("Misc", folderControl.SelectedRecentGroup.Header);
    }

    private static void SavedHistoryLoadsTabsAndIncomingUpdatesPreserveSelection()
    {
        using var control = CreateControl();
        string directory = Path.Combine(Path.GetTempPath(), $"LEXRecentsTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string le3Path = Path.Combine(directory, "LE3.pcc");
            string me2Path = Path.Combine(directory, "ME2.pcc");
            string miscPath = Path.Combine(directory, "Default.sfar");
            foreach (string path in new[] { le3Path, me2Path, miscPath })
            {
                File.WriteAllText(path, "");
            }
            var entries = new[]
            {
                new RecentsControl.RecentItem(le3Path, MEGame.LE3),
                new RecentsControl.RecentItem(me2Path, MEGame.ME2),
                new RecentsControl.RecentItem(miscPath, MEGame.ME3)
            };
            string recentsFile = Path.Combine(directory, "RECENTFILES");
            File.WriteAllLines(recentsFile, entries.Select(item => item.ConvertToRecentEntry()));

            string openedPath = null;
            control.InitRecentControl(directory, new MenuItem(), path => openedPath = path);
            Assert.AreEqual(MEGame.LE3, control.SelectedRecentGroup.Game);
            control.RecentFileOpenCommand.Execute(le3Path);
            Assert.AreEqual(le3Path, openedPath);

            var tabs = (TabControl)control.FindName("RecentTabs");
            Assert.HasCount(8, tabs.Items);
            var me2Group = control.RecentGroups.Single(group => group.Game == MEGame.ME2);
            tabs.SelectedItem = me2Group;
            Assert.AreSame(me2Group, control.SelectedRecentGroup);

            control.PropogateRecentsChange(false, entries.Reverse());
            Assert.AreSame(me2Group, control.SelectedRecentGroup);
            Assert.AreEqual(me2Path, me2Group.Items.Single().Path);
            control.SaveRecentList(false);
            CollectionAssert.AreEqual(entries.Reverse().Select(item => item.ConvertToRecentEntry()).ToArray(),
                File.ReadAllLines(recentsFile));

            File.Delete(me2Path);
            control.PropogateRecentsChange(false, entries);
            Assert.AreEqual(MEGame.LE3, control.SelectedRecentGroup.Game);
            Assert.IsEmpty(me2Group.Items);
            Assert.HasCount(3, control.RecentsMenu.Items);

            control.PropogateRecentsChange(false, []);
            Assert.IsTrue(control.RecentGroups.All(group => group.Items.Count == 0));
            Assert.IsTrue(control.RecentsMenu.IsEnabled, "An empty history must still offer the pinned-file picker.");
        }
        finally
        {
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                File.Delete(path);
            }
            Directory.Delete(directory);
        }
    }

    private static void PinnedFilesPersistWithoutHistoryLimitsAndFilterByPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"LEXPinnedTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var control = CreateControl();
            control.InitRecentControl(directory, new MenuItem(), _ => { });
            string[] paths = Enumerable.Range(0, 12)
                .Select(index => Path.Combine(directory, $"Bookmark_{index}.pcc")).ToArray();
            foreach (string path in paths)
            {
                File.WriteAllText(path, "");
            }

            control.AddRecent(paths[0], false, MEGame.LE3);
            var recentMenuItem = control.RecentsMenu.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Tag, paths[0]));
            var contextMenu = recentMenuItem.ContextMenu;
            contextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            var pinMenuItem = (MenuItem)contextMenu.Items[0];
            Assert.AreEqual("Pin", pinMenuItem.Header);
            pinMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.IsTrue(control.IsPinned(paths[0]));
            contextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.AreEqual("Unpin", pinMenuItem.Header);
            pinMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.IsFalse(control.IsPinned(paths[0]));
            contextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.AreEqual("Pin", pinMenuItem.Header);
            pinMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            control.PinItem(new RecentsControl.RecentItem(paths[0].ToUpperInvariant(), MEGame.LE3));
            Assert.HasCount(1, control.PinnedItems, "Pins must deduplicate paths without regard to casing.");
            for (int index = 1; index < 9; index++)
            {
                control.PinItem(new RecentsControl.RecentItem(paths[index], MEGame.LE3));
            }
            Assert.IsFalse(control.ShowPinnedSearch);
            var tabs = (TabControl)control.FindName("RecentTabs");
            tabs.SelectedItem = control.PinnedGroup;
            UpdateControlLayout(control);
            var searchBox = VisualDescendants<TextBox>(control).Single();
            Assert.AreEqual(Visibility.Collapsed, ((Grid)searchBox.Parent).Visibility);
            control.PinItem(new RecentsControl.RecentItem(paths[9], MEGame.LE3));
            Assert.IsTrue(control.ShowPinnedSearch, "The search field must appear when the list exceeds nine pins.");
            UpdateControlLayout(control);
            Assert.AreEqual(Visibility.Visible, ((Grid)searchBox.Parent).Visibility);
            control.PinItem(new RecentsControl.RecentItem(paths[10], MEGame.LE3));
            control.PinItem(new RecentsControl.RecentItem(paths[11], MEGame.LE3));
            Assert.HasCount(12, control.PinnedItems, "The ten-item history limit must not apply to pins.");
            Assert.HasCount(12, control.PinnedGroup.Items);
            UpdateControlLayout(control);
            var pinnedScrollViewer = VisualDescendants<ScrollViewer>(control)
                .Single(viewer => viewer.Name == "PinnedItemsScrollViewer");
            Assert.AreEqual(ScrollBarVisibility.Auto, pinnedScrollViewer.VerticalScrollBarVisibility);
            Assert.IsGreaterThan(0, pinnedScrollViewer.ScrollableHeight,
                "Pins past the ninth row must remain accessible through a scrollbar.");
            Assert.IsLessThanOrEqualTo(288, pinnedScrollViewer.ActualHeight);

            control.PinnedSearchText = "BOOKMARK_1.PCC";
            Assert.AreEqual(paths[1], control.PinnedGroup.Items.Single().Path);
            control.PinnedSearchText = directory.ToUpperInvariant();
            Assert.HasCount(12, control.PinnedGroup.Items, "Search must include the directory portion of the path.");
            control.PinnedSearchText = "Bookmark_1";
            CollectionAssert.AreEqual(new[] { paths[1], paths[10], paths[11] },
                control.PinnedGroup.Items.Select(item => item.Path).ToArray());
            control.PinnedSearchText = "No matching pin";
            Assert.IsEmpty(control.PinnedGroup.Items);
            Assert.IsTrue(control.ShowPinnedSearch, "Filtering must not hide the search field.");
            Assert.HasCount(12, control.PinnedItems);
            control.PinnedSearchText = "";

            foreach (string path in paths.Skip(1))
            {
                control.AddRecent(path, false, MEGame.LE3);
            }
            Assert.IsFalse(control.RecentItems.Any(item => item.Path == paths[0]));
            Assert.IsTrue(control.IsPinned(paths[0]), "Evicting a recent entry must preserve its pin.");
            control.PropogateRecentsChange(false, []);
            Assert.HasCount(12, control.PinnedItems, "Replacing recent history must preserve pins.");

            string pinnedFile = Path.Combine(directory, "PINNEDFILES");
            Assert.HasCount(12, File.ReadAllLines(pinnedFile));
            File.Delete(paths[2]);
            using var reloaded = CreateControl();
            reloaded.InitRecentControl(directory, new MenuItem(), _ => { });
            CollectionAssert.AreEqual(control.PinnedItems.Select(item => item.ConvertToRecentEntry()).ToArray(),
                reloaded.PinnedItems.Select(item => item.ConvertToRecentEntry()).ToArray());
            Assert.IsTrue(reloaded.IsPinned(paths[2]), "A missing file must stay pinned until explicitly unpinned.");
            Assert.IsFalse(reloaded.RecentFileOpenCommand.CanExecute(paths[2]));
            Assert.IsTrue(reloaded.RecentFileOpenCommand.CanExecute(paths[1]));
            Assert.AreEqual(MEGame.LE3, reloaded.PinnedItems[0].Game);

            reloaded.UnpinItem(new RecentsControl.RecentItem(paths[2].ToUpperInvariant(), null));
            Assert.IsFalse(reloaded.IsPinned(paths[2]));
            reloaded.PinnedSearchText = "Bookmark_1";
            reloaded.UnpinItem(new RecentsControl.RecentItem(paths[0], null));
            reloaded.UnpinItem(new RecentsControl.RecentItem(paths[3], null));
            Assert.HasCount(9, reloaded.PinnedItems);
            Assert.IsFalse(reloaded.ShowPinnedSearch);
            Assert.AreEqual("", reloaded.PinnedSearchText, "A hidden search field must not leave the short list filtered.");
            Assert.HasCount(9, reloaded.PinnedGroup.Items);

            using var afterUnpin = CreateControl();
            afterUnpin.InitRecentControl(directory, new MenuItem(), _ => { });
            CollectionAssert.AreEqual(reloaded.PinnedItems.Select(item => item.ConvertToRecentEntry()).ToArray(),
                File.ReadAllLines(pinnedFile));
            Assert.HasCount(9, afterUnpin.PinnedItems);
            Assert.IsFalse(afterUnpin.IsPinned(paths[2]));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void PinnedFilesStayInSyncOnlyForTheSameTool()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"LEXPinSyncTests-{Guid.NewGuid():N}");
        string firstToolDirectory = Path.Combine(directory, "FirstTool");
        string otherToolDirectory = Path.Combine(directory, "OtherTool");
        Directory.CreateDirectory(directory);
        try
        {
            using var first = CreateControl();
            using var second = CreateControl();
            using var otherTool = CreateControl();
            first.InitRecentControl(firstToolDirectory, new MenuItem(), _ => { });
            second.InitRecentControl(firstToolDirectory.ToUpperInvariant(), new MenuItem(), _ => { });
            otherTool.InitRecentControl(otherToolDirectory, new MenuItem(), _ => { });

            var sharedItem = new RecentsControl.RecentItem(Path.Combine(directory, "Shared.pcc"), MEGame.LE3);
            first.PinItem(sharedItem);
            Assert.IsTrue(second.IsPinned(sharedItem.Path));
            Assert.IsEmpty(otherTool.PinnedItems);

            var tabs = (TabControl)second.FindName("RecentTabs");
            tabs.SelectedItem = second.PinnedGroup;
            var morePins = Enumerable.Range(0, 10)
                .Select(index => new RecentsControl.RecentItem(Path.Combine(directory, $"Second_{index}.pcc"), MEGame.LE3))
                .ToArray();
            foreach (var item in morePins)
            {
                first.PinItem(item);
            }
            second.PinnedSearchText = "SECOND_5";
            Assert.AreEqual(morePins[5].Path, second.PinnedGroup.Items.Single().Path);
            first.UnpinItem(sharedItem);
            Assert.HasCount(10, first.PinnedItems);
            Assert.HasCount(10, second.PinnedItems);
            Assert.IsFalse(second.IsPinned(sharedItem.Path));
            Assert.AreEqual("SECOND_5", second.PinnedSearchText);
            Assert.AreSame(second.PinnedGroup, second.SelectedRecentGroup);
            Assert.AreEqual(morePins[5].Path, second.PinnedGroup.Items.Single().Path);

            otherTool.PinItem(sharedItem);
            Assert.HasCount(1, otherTool.PinnedItems);
            Assert.IsFalse(first.IsPinned(sharedItem.Path), "Another tool must maintain its own independent pinned list.");
            CollectionAssert.AreEqual(first.PinnedItems.Select(item => item.ConvertToRecentEntry()).ToArray(),
                File.ReadAllLines(Path.Combine(firstToolDirectory, "PINNEDFILES")));
            CollectionAssert.AreEqual(new[] { sharedItem.ConvertToRecentEntry() },
                File.ReadAllLines(Path.Combine(otherToolDirectory, "PINNEDFILES")));

            using var otherReloaded = CreateControl();
            otherReloaded.InitRecentControl(otherToolDirectory, new MenuItem(), _ => { });
            Assert.AreEqual(sharedItem.Path, otherReloaded.PinnedItems.Single().Path);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void FileMenuOffersPinningWithoutRecentHistory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"LEXPinMenuTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var fileMenu = new MenuItem { Header = "File" };
            var recentsMenu = new MenuItem { Header = "Recent files" };
            fileMenu.Items.Add(recentsMenu);
            using (var control = CreateControl())
            {
                control.InitRecentControl(directory, recentsMenu, _ => { });
                Assert.IsEmpty(control.RecentItems);
                Assert.IsTrue(recentsMenu.IsEnabled);
                Assert.HasCount(2, fileMenu.Items);
                var pinFileMenu = (MenuItem)fileMenu.Items[1];
                Assert.AreEqual("Pin file…", pinFileMenu.Header);
                Assert.AreSame(control.BrowsePinnedItemsCommand, pinFileMenu.Command);
                Assert.IsTrue(pinFileMenu.IsEnabled);
                Assert.IsTrue(pinFileMenu.Command.CanExecute(null));
                Assert.AreEqual("Pinned files", ((MenuItem)recentsMenu.Items[0]).Header);

                control.InitRecentControl(directory, recentsMenu, _ => { });
                control.AttachPinFileMenu(recentsMenu);
                Assert.HasCount(2, fileMenu.Items, "Reinitializing must not duplicate the File menu command.");
                Assert.AreSame(control.BrowsePinnedItemsCommand, ((MenuItem)fileMenu.Items[1]).Command);
            }
            Assert.HasCount(1, fileMenu.Items, "Disposing a control must remove the command it inserted.");
            Assert.AreSame(recentsMenu, fileMenu.Items[0]);

            using (var folderControl = CreateControl())
            {
                folderControl.IsFolderRecents = true;
                string openedFolder = null;
                folderControl.InitRecentControl(Path.Combine(directory, "FolderTool"), recentsMenu,
                    path => openedFolder = path);
                Assert.AreEqual("Pin folder…", folderControl.PinBrowseLabel);
                var pinFolderMenu = (MenuItem)fileMenu.Items[1];
                Assert.AreEqual("Pin folder…", pinFolderMenu.Header);
                Assert.AreSame(folderControl.BrowsePinnedItemsCommand, pinFolderMenu.Command);
                Assert.IsTrue(pinFolderMenu.Command.CanExecute(null));
                string pinnedFolder = Directory.CreateDirectory(Path.Combine(directory, "PinnedFolder")).FullName;
                folderControl.PinItem(new RecentsControl.RecentItem(pinnedFolder, null));
                Assert.IsTrue(folderControl.RecentFileOpenCommand.CanExecute(pinnedFolder));
                folderControl.RecentFileOpenCommand.Execute(pinnedFolder);
                Assert.AreEqual(pinnedFolder, openedFolder);
            }
            Assert.HasCount(1, fileMenu.Items);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void PinsOnlyInitializationPreservesLegacyRecentFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"LEXLegacyPinTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string existingPath = Path.Combine(directory, "Existing.pcc");
            File.WriteAllText(existingPath, "");
            string recentsFile = Path.Combine(directory, "RECENTFILES");
            File.WriteAllLines(recentsFile, ["", "x", existingPath]);
            byte[] originalRecents = File.ReadAllBytes(recentsFile);
            var pin = new RecentsControl.RecentItem(existingPath, MEGame.LE3);
            string pinnedFile = Path.Combine(directory, "PINNEDFILES");
            File.WriteAllLines(pinnedFile, [pin.ConvertToRecentEntry()]);

            using var control = CreateControl();
            control.InitPinnedControl(directory, _ => { });
            Assert.IsEmpty(control.RecentItems, "Tools with a legacy recent-file format must load only their pins.");
            Assert.AreEqual(existingPath, control.PinnedItems.Single().Path);
            Assert.AreEqual(MEGame.LE3, control.PinnedItems.Single().Game);
            CollectionAssert.AreEqual(originalRecents, File.ReadAllBytes(recentsFile));

            control.PinItem(new RecentsControl.RecentItem(Path.Combine(directory, "Missing.pcc"), MEGame.LE3));
            Assert.HasCount(2, File.ReadAllLines(pinnedFile));
            CollectionAssert.AreEqual(originalRecents, File.ReadAllBytes(recentsFile),
                "Saving pins must leave the tool's legacy recent-file bytes unchanged.");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void UpdateControlLayout(RecentsControl control)
    {
        control.ApplyTemplate();
        control.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        control.Measure(new Size(560, 600));
        control.Arrange(new Rect(0, 0, 560, 600));
        control.UpdateLayout();
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T matchingChild) yield return matchingChild;
            foreach (T descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }
}
