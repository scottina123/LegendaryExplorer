using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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
    }

    private static void TabsKeepSeparateHistoriesAndRouteNonPccFilesToMisc()
    {
        using var control = CreateControl();
        CollectionAssert.AreEqual(new[] { "ME1", "ME2", "ME3", "LE1", "LE2", "LE3", "Misc" },
            control.RecentGroups.Select(group => group.Header).ToArray());

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
        var misc = control.RecentGroups.Single(group => group.Game == null);
        Assert.AreSame(misc, control.SelectedRecentGroup);
        Assert.HasCount(5, misc.Items);
        Assert.IsTrue(misc.Items.Single(item => item.Path.EndsWith(".AFC")).IsAfc);

        for (int index = 0; index < 12; index++)
        {
            control.AddRecent($@"C:\RecentsTests\Conditions{index}.cnd", false, null);
        }
        Assert.HasCount(10, misc.Items);
        Assert.HasCount(70, control.RecentItems);
        Assert.HasCount(70, control.RecentsMenu.Items);

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
            Assert.HasCount(7, tabs.Items);
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
            Assert.HasCount(2, control.RecentsMenu.Items);

            control.PropogateRecentsChange(false, []);
            Assert.IsTrue(control.RecentGroups.All(group => group.Items.Count == 0));
            Assert.IsFalse(control.RecentsMenu.IsEnabled);
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
}
