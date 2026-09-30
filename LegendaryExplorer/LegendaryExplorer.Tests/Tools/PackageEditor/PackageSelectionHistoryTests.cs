using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.SharedUI.PeregrineTreeView;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.UserControls.ExportLoaderControls.ScriptEditor.IDE;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Xaml.Behaviors;

namespace LegendaryExplorer.Tests.Tools.PackageEditor;

[TestClass]
public class PackageSelectionHistoryTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void HistoryMenusLimitEntriesAndPreserveBothDirectionsWhenJumping()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(PackageEditorWindow).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
        SyntaxInfo.LoadFromSettings();

        using var package = MEPackageHandler.CreateMemoryEmptyPackage("SelectionHistory.pcc", MEGame.LE3);
        var exports = Enumerable.Range(0, 12).Select(index =>
        {
            var export = package.CreateExport($"Entry_{index}", "Object", indexed: false);
            export.WriteProperties(new PropertyCollection());
            export.EntryHasPendingChanges = false;
            return export;
        }).ToArray();
        IEntry[] entries = exports.Cast<IEntry>().Append(package.Imports[0]).ToArray();
        var originalData = exports.ToDictionary(export => export, export => export.Data.ToArray());
        var settingsLoaded = typeof(Settings).GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
        object previousLoaded = settingsLoaded.GetValue(null);
        settingsLoaded.SetValue(null, false);
        var window = new PackageEditorWindow(submitTelemetry: false, enableRecents: false)
        {
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000
        };
        try
        {
            window.Show();
            typeof(WPFBase).GetProperty(nameof(WPFBase.Pcc))!.SetValue(window, package);
            var root = new TreeViewEntry(null, "SelectionHistory") { IsExpanded = true, PackageRef = package };
            foreach (IEntry entry in package.Exports.Cast<IEntry>().Concat(package.Imports))
            {
                root.Sublinks.Add(new TreeViewEntry(entry) { Parent = root });
            }
            window.AllTreeViewNodesX.Add(root);

            var back = window.NavigateBackCommand;
            var forward = window.NavigateForwardCommand;
            var backMenu = ((Button)window.FindName("SelectionHistoryBackButton")).ContextMenu;
            var forwardMenu = ((Button)window.FindName("SelectionHistoryForwardButton")).ContextMenu;
            Assert.IsFalse(back.CanExecute(null));
            Assert.IsFalse(forward.CanExecute(null));
            Assert.IsFalse(window.FocusSelectedCommand.CanExecute(null));
            foreach (IEntry entry in entries)
            {
                Assert.IsTrue(window.GoToNumber(entry.UIndex));
            }

            Assert.HasCount(12, window.BackwardsEntries, "The full history must remain uncapped.");
            for (int index = entries.Length - 2; index >= 0; index--)
            {
                back.Execute(null);
                Assert.AreSame(entries[index], window.SelectedItem.Entry);
            }
            Assert.IsFalse(back.CanExecute(null));
            Assert.HasCount(12, window.ForwardsEntries);
            for (int index = 1; index < entries.Length; index++)
            {
                forward.Execute(null);
                Assert.AreSame(entries[index], window.SelectedItem.Entry);
            }

            backMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.HasCount(10, backMenu.Items);
            StringAssert.Contains(((TextBlock)((MenuItem)backMenu.Items[0]).Header).Text, entries[11].InstancedFullPath);
            ((MenuItem)backMenu.Items[9]).Command.Execute(null);
            Assert.AreSame(entries[2], window.SelectedItem.Entry);
            CollectionAssert.AreEqual(entries.Take(2).Reverse().ToArray(), window.BackwardsEntries.ToArray());
            CollectionAssert.AreEqual(entries.Skip(3).ToArray(), window.ForwardsEntries.ToArray());

            forwardMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.HasCount(10, forwardMenu.Items);
            StringAssert.Contains(((TextBlock)((MenuItem)forwardMenu.Items[9]).Header).Text, $"#{entries[12].UIndex} ");
            ((MenuItem)forwardMenu.Items[9]).Command.Execute(null);
            Assert.AreSame(entries[12], window.SelectedItem.Entry, "History must support imports as well as exports.");
            Assert.IsFalse(forward.CanExecute(null));
            CollectionAssert.AreEqual(entries.Take(12).Reverse().ToArray(), window.BackwardsEntries.ToArray());

            back.Execute(null);
            Assert.AreSame(entries[11], window.SelectedItem.Entry);
            forward.Execute(null);
            Assert.AreSame(entries[12], window.SelectedItem.Entry);
            Assert.IsFalse(window.IsBackForwardsNavigationEvent, "Forward navigation must resume normal history recording.");
            back.Execute(null);
            Assert.IsTrue(window.GoToNumber(entries[0].UIndex));
            forwardMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.IsEmpty(forwardMenu.Items, "A new selection must discard the previous forward branch.");
            Assert.IsFalse(forward.CanExecute(null));

            Assert.IsTrue(window.GoToNumber(entries[1].UIndex));
            Assert.IsTrue(window.GoToNumber(entries[0].UIndex));
            Assert.IsTrue(window.GoToNumber(entries[2].UIndex));
            backMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            ((MenuItem)backMenu.Items[2]).Command.Execute(null);
            Assert.AreSame(entries[0], window.SelectedItem.Entry);
            CollectionAssert.AreEqual(new[] { entries[1], entries[0], entries[2] }, window.ForwardsEntries.ToArray(),
                "Choosing an older visit to the same entry must preserve all intervening visits.");
            forward.Execute(null);
            Assert.AreSame(entries[1], window.SelectedItem.Entry);

            var selectedNode = window.SelectedItem;
            var backwardsBeforeFocus = window.BackwardsEntries.ToArray();
            var forwardsBeforeFocus = window.ForwardsEntries.ToArray();
            var tree = (TreeView)window.FindName("LeftSide_TreeView");
            var behavior = Interaction.GetBehaviors(tree).OfType<NodeTreeSelectionBehavior>().Single();
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.AreSame(selectedNode, behavior.SelectedItem);
            Assert.IsTrue(window.FocusSelectedCommand.CanExecute(null));
            window.FocusSelectedCommand.Execute(null);
            root.IsExpanded = false;
            var revealTask = behavior.BringSelectedItemIntoViewAsync();
            if (!revealTask.IsCompleted)
            {
                var frame = new DispatcherFrame();
                revealTask.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
            }
            revealTask.GetAwaiter().GetResult();
            Assert.IsTrue(root.IsExpanded, "Refocusing must expand collapsed ancestors even when the selection has not changed.");
            Assert.AreSame(selectedNode, window.SelectedItem);
            CollectionAssert.AreEqual(backwardsBeforeFocus, window.BackwardsEntries.ToArray());
            CollectionAssert.AreEqual(forwardsBeforeFocus, window.ForwardsEntries.ToArray());
            selectedNode.IsVisibleInTree = false;
            Assert.IsTrue(behavior.BringSelectedItemIntoViewAsync().IsCompletedSuccessfully);
            Assert.IsFalse(selectedNode.IsVisibleInTree, "Refocusing must not override live filtering.");
            selectedNode.IsVisibleInTree = true;

            foreach (var (export, data) in originalData)
            {
                Assert.IsFalse(export.EntryHasPendingChanges, export.InstancedFullPath);
                CollectionAssert.AreEqual(data, export.Data);
            }
        }
        finally
        {
            window.Close();
            settingsLoaded.SetValue(null, previousLoaded);
        }
    }
}
