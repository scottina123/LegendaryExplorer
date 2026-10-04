using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.SharedUI.Controls;
using LegendaryExplorer.SharedUI.PeregrineTreeView;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.UserControls.ExportLoaderControls.ScriptEditor.IDE;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Xaml.Behaviors;

namespace LegendaryExplorer.Tests.Tools.PackageEditor;

[TestClass]
public class PackageChangeLinkNavigationTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void ChangedLinksRevealTheNewPathAndPreserveSelectionThroughDelayedUpdatesAndFilters()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(PackageEditorWindow).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
        SyntaxInfo.LoadFromSettings();

        var settingsLoaded = typeof(Settings).GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
        object previousLoaded = settingsLoaded.GetValue(null);
        bool previousLiveFiltering = Settings.PackageEditor_LiveFiltering;
        settingsLoaded.SetValue(null, false);
        Settings.PackageEditor_LiveFiltering = false;

        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ChangeLinkNavigation.pcc", MEGame.LE3);
        var source = Create("Source", "Package");
        for (int index = 0; index < 150; index++)
            Create($"RootEntry{index}");
        var destination = Create("Destination", "Package");
        var nestedDestination = Create("NestedDestination", "Package", destination);
        for (int index = 0; index < 200; index++)
            Create($"DestinationEntry{index}", parent: nestedDestination);
        var firstMoved = Create("MovedFirst", parent: source);
        var primaryMoved = Create("MovedPrimary", parent: source);
        for (int index = 0; index < 100; index++)
            Create($"ChildEntry{index}", parent: primaryMoved);

        var window = new PackageEditorWindow(submitTelemetry: false, enableRecents: false)
        {
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
            Width = 1000, Height = 700
        };
        try
        {
            window.Show();
            typeof(WPFBase).GetProperty(nameof(WPFBase.Pcc))!.SetValue(window, package);
            var root = new TreeViewEntry(null, "ChangeLinkNavigation") { IsExpanded = true, PackageRef = package };
            var nodes = package.Exports.Cast<IEntry>().Concat(package.Imports)
                .ToDictionary(entry => entry.UIndex, entry => new TreeViewEntry(entry));
            nodes.Add(0, root);
            foreach (var node in nodes.Values.Where(node => node.Entry is not null))
            {
                node.Parent = nodes[node.Entry.idxLink];
                node.Parent.Sublinks.Add(node);
            }
            nodes[source.UIndex].IsExpanded = true;
            nodes[primaryMoved.UIndex].IsExpanded = true;
            window.AllTreeViewNodesX.Add(root);
            typeof(PackageEditorWindow).GetMethod("RefreshView", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);
            var tree = (TreeView)window.FindName("LeftSide_TreeView");
            var behavior = Interaction.GetBehaviors(tree).OfType<NodeTreeSelectionBehavior>().Single();
            var search = (WatermarkTextBox)window.FindName("Search_TextBox");
            Settle();

            Assert.IsTrue(window.GoToNumber(primaryMoved.UIndex));
            Settle();
            var selectedNode = window.SelectedItem;
            Assert.AreSame(nodes[primaryMoved.UIndex], selectedNode);
            var rootContainer = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(root);
            Assert.IsNull(rootContainer.ItemContainerGenerator.ContainerFromItem(nodes[destination.UIndex]),
                "The destination must start outside the realized viewport so the test exercises virtualized navigation.");
            Assert.IsFalse(nodes[destination.UIndex].IsExpanded);
            Assert.IsFalse(nodes[nestedDestination.UIndex].IsExpanded);

            // Package header notifications arrive later than the Change Links command.
            // Restore must reveal the current idxLink without relying on that notification.
            primaryMoved.idxLink = nestedDestination.UIndex;
            Pump(window.RestoreSelectionAfterLinkChangeAsync([primaryMoved]));
            Settle();
            Assert.AreSame(selectedNode, window.SelectedItem, "Moving the selected entry must retain its tree model.");
            Assert.AreSame(nodes[nestedDestination.UIndex], selectedNode.Parent,
                "Navigation must use the new parent before delayed package updates arrive.");
            Assert.IsTrue(selectedNode.IsExpanded, "Moving an expanded entry must retain its expanded descendants.");
            Assert.AreEqual(100, selectedNode.Sublinks.Count, "Moving the entry must preserve its complete subtree.");
            Assert.IsFalse(nodes[source.UIndex].Sublinks.Contains(selectedNode));
            Assert.AreEqual(1, nodes[nestedDestination.UIndex].Sublinks.Count(node => ReferenceEquals(node, selectedNode)));
            AssertSelection(primaryMoved);

            window.HandleUpdate([new PackageUpdate(PackageChange.ExportHeader, primaryMoved.UIndex)]);
            Settle();
            AssertSelection(primaryMoved);

            // Input order determines the primary selection, even though tree sorting
            // places the lower UIndex first and moving it replaces existing containers.
            firstMoved.idxLink = nestedDestination.UIndex;
            Pump(window.RestoreSelectionAfterLinkChangeAsync([primaryMoved, firstMoved]));
            Settle();
            AssertSelection(primaryMoved);
            Assert.IsTrue(nodes[firstMoved.UIndex].IsMultiSelected);
            Assert.IsTrue(nodes[primaryMoved.UIndex].IsMultiSelected);
            Assert.AreSame(primaryMoved, window.SelectedItem.Entry,
                "Restoring multiple entries must preserve the requested primary rather than tree order.");
            window.HandleUpdate([
                new PackageUpdate(PackageChange.ExportHeader, firstMoved.UIndex),
                new PackageUpdate(PackageChange.ExportHeader, primaryMoved.UIndex)
            ]);
            Settle();
            AssertSelection(primaryMoved);
            Assert.IsTrue(nodes[firstMoved.UIndex].IsMultiSelected);

            // Return both entries to the source and filter the other branch away.
            firstMoved.idxLink = source.UIndex;
            primaryMoved.idxLink = source.UIndex;
            Pump(window.RestoreSelectionAfterLinkChangeAsync([primaryMoved, firstMoved]));
            foreach (IEntry entry in package.Exports.Cast<IEntry>().Concat(package.Imports))
                entry.EntryHasPendingChanges = false;
            window.SetComparedChangedEntries([
                new EntryStringPair(primaryMoved, "Changed"),
                new EntryStringPair(firstMoved, "Changed")
            ]);
            window.ShowOnlyEditedTreeViewItems = true;
            Settings.PackageEditor_LiveFiltering = true;
            search.Text = "Moved";
            Pump(window.UpdateLiveFilterAsync(debounce: false));
            Settle();
            Assert.IsFalse(nodes[destination.UIndex].IsVisibleInTree,
                "The destination branch must begin hidden by the active filters.");

            firstMoved.idxLink = nestedDestination.UIndex;
            primaryMoved.idxLink = nestedDestination.UIndex;
            Pump(window.RestoreSelectionAfterLinkChangeAsync([primaryMoved, firstMoved]));
            Settle();
            AssertSelection(primaryMoved);
            Assert.AreEqual("Moved", search.Text, "Moving an entry must preserve the user's live query.");
            Assert.IsTrue(Settings.PackageEditor_LiveFiltering && window.ShowOnlyEditedTreeViewItems);
            Assert.IsTrue(window.LiveFilterMatches.SetEquals([firstMoved, primaryMoved]));
            Assert.IsTrue(nodes[destination.UIndex].IsVisibleInTree);
            Assert.IsTrue(nodes[nestedDestination.UIndex].IsVisibleInTree);
            Assert.IsFalse(nodes[source.UIndex].IsVisibleInTree,
                "Filters must be recomputed using the moved entries' new ancestry.");
            Assert.IsTrue(nodes[firstMoved.UIndex].IsMultiSelected);
            Assert.IsFalse(nodes.Values.First(node => node.Entry?.ObjectName.Name == "DestinationEntry0").IsVisibleInTree,
                "Revealing the destination must retain filtering for unrelated siblings.");
            Assert.IsTrue(BindingOperations.IsDataBound(behavior, NodeTreeSelectionBehavior.SelectedItemProperty),
                "Explicit restoration must retain the two-way selection binding.");

            window.HandleUpdate([
                new PackageUpdate(PackageChange.ExportHeader, firstMoved.UIndex),
                new PackageUpdate(PackageChange.ExportHeader, primaryMoved.UIndex)
            ]);
            Settle();
            AssertSelection(primaryMoved);
        }
        finally
        {
            window.Close();
            Settings.PackageEditor_LiveFiltering = previousLiveFiltering;
            settingsLoaded.SetValue(null, previousLoaded);
        }

        ExportEntry Create(string name, string className = "Object", ExportEntry parent = null)
        {
            var export = package.CreateExport(name, className, parent, indexed: false);
            export.WriteProperties(new PropertyCollection());
            return export;
        }

        void AssertSelection(IEntry entry)
        {
            var tree = (TreeView)window.FindName("LeftSide_TreeView");
            var behavior = Interaction.GetBehaviors(tree).OfType<NodeTreeSelectionBehavior>().Single();
            Assert.AreSame(entry, window.SelectedItem?.Entry);
            Assert.AreSame(window.SelectedItem, tree.SelectedItem,
                "The displayed selection must follow the moved entry to its new container.");
            Assert.AreSame(window.SelectedItem, behavior.SelectedItem);
            Assert.IsTrue(window.SelectedItem.IsSelected);
            Assert.IsTrue(window.GetSelected(out int selectedIndex));
            Assert.AreEqual(entry.UIndex, selectedIndex);

            var ancestors = new Stack<TreeViewEntry>();
            for (var node = window.SelectedItem; node is not null; node = node.Parent)
                ancestors.Push(node);
            ItemsControl container = tree;
            foreach (var node in ancestors)
            {
                if (!ReferenceEquals(node, window.SelectedItem))
                    Assert.IsTrue(node.IsExpanded, "Every ancestor of the moved entry must expand.");
                Assert.IsTrue(node.IsVisibleInTree, "The moved entry's complete path must remain visible.");
                container = container.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
                Assert.IsNotNull(container, "Restoration must realize every container along the new path.");
            }
            var position = container.TranslatePoint(new Point(), tree);
            Assert.IsTrue(position.Y >= 0 && position.Y < tree.ActualHeight,
                $"The moved entry's header must scroll into the viewport even with expanded descendants; its row begins at {position.Y} of {tree.ActualHeight}.");
        }
    }

    private static void Settle()
    {
        Pump(Task.Delay(300));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
}
