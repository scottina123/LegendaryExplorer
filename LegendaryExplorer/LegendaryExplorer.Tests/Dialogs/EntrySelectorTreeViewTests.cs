using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Dialogs;

[TestClass]
public class EntrySelectorTreeViewTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void TreeModePersistsAndPreservesValidSelectionAndPackageHierarchy()
    {
        var loaded = typeof(Settings).GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
        object previousLoaded = loaded.GetValue(null);
        bool previousTreePreference = Settings.EntrySelector_UseTreeView;
        loaded.SetValue(null, false);
        try
        {
            InitializeApplicationResources();
            ViewChoicePersistsAcrossSelectorsAndPreservesSelection();
            FilteredTreeRetainsAncestorsAndRejectsContextSelection();
            IdenticalIndexesInSeparatePackagesRemainDistinct();
            GenericItemsKeepListModeWithoutChangingTreePreference();
            SearchRefreshPreservesSelectionAndBuildsNewTreeResults();
            NestedHeaderDoubleClickAcceptsOnlySelectableEntries();
            ClassFilterPreservesSelectionAcrossLargeRealizedTreeUpdates();
        }
        finally
        {
            Settings.EntrySelector_UseTreeView = previousTreePreference;
            loaded.SetValue(null, previousLoaded);
        }
    }

    private static void ViewChoicePersistsAcrossSelectorsAndPreservesSelection()
    {
        Settings.EntrySelector_UseTreeView = false;
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("SelectorMode.pcc", MEGame.LE3);
        var parent = package.CreateExport("Content", "Package", indexed: false);
        var leaf = package.CreateExport("FirstLeaf", "SeqVar_Object", parent, indexed: false);
        var other = package.CreateExport("SecondLeaf", "SeqAct_Gate", parent, indexed: false);
        using (var selector = CreateItemSelector([leaf, other]))
        {
            try
            {
                ShowOffscreen(selector);
                AssertViewVisibility(selector, tree: false);
                selector.SelectedEntryItem = other;
                var viewChoice = (ComboBox)selector.FindName("EntryViewComboBox");
                viewChoice.SelectedIndex = 1;
                FlushBindings(selector);
                Assert.IsTrue(selector.UseTreeView);
                Assert.IsTrue(Settings.EntrySelector_UseTreeView);
                AssertViewVisibility(selector, tree: true);
                AssertSelectedEntry(selector, other);

                viewChoice.SelectedIndex = 0;
                FlushBindings(selector);
                Assert.IsFalse(selector.UseTreeView);
                Assert.IsFalse(Settings.EntrySelector_UseTreeView);
                AssertViewVisibility(selector, tree: false);
                Assert.AreSame(other, selector.SelectedEntryItem);
                Assert.AreSame(other, ((ListView)selector.FindName("EntrySelectorListView")).SelectedItem);

                viewChoice.SelectedIndex = 1;
                FlushBindings(selector);
                AssertSelectedEntry(selector, other);
            }
            finally
            {
                selector.Close();
            }
        }

        using (var reopened = CreateItemSelector([leaf, other]))
        {
            try
            {
                Assert.IsTrue(reopened.UseTreeView, "A later selector must restore the previous tree choice.");
                ShowOffscreen(reopened);
                AssertViewVisibility(reopened, tree: true);
                reopened.UseTreeView = false;
                Assert.IsFalse(Settings.EntrySelector_UseTreeView);
            }
            finally
            {
                reopened.Close();
            }
        }

        using var listReopened = CreateItemSelector([leaf, other]);
        try
        {
            Assert.IsFalse(listReopened.UseTreeView, "The saved list choice must also be restored.");
            FlushBindings(listReopened);
            AssertViewVisibility(listReopened, tree: false);
        }
        finally
        {
            listReopened.Close();
        }
    }

    private static void FilteredTreeRetainsAncestorsAndRejectsContextSelection()
    {
        Settings.EntrySelector_UseTreeView = true;
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("SelectorHierarchy.pcc", MEGame.LE3);
        var outer = package.CreateExport("Content", "Package", indexed: false);
        var parent = package.CreateExport("NestedSequence", "Sequence", outer, indexed: false);
        var leaf = package.CreateExport("NeedleLeaf", "SeqVar_Object", parent, indexed: false);
        leaf.WriteProperty(new StrProperty("Tree searchable caption", "ObjName"));
        var sibling = package.CreateExport("OtherLeaf", "SeqAct_Gate", parent, indexed: false);
        var importParent = new ImportEntry(package)
        {
            ObjectName = "ImportedContent", ClassName = "Package", PackageFile = "Core"
        };
        package.AddImport(importParent);
        var imported = new ImportEntry(package, importParent, "ImportedLeaf")
        {
            ClassName = "SeqAct_Delay", PackageFile = "Engine"
        };
        package.AddImport(imported);
        const string nullOption = "0 Null";
        using var selector = CreateItemSelector([leaf, sibling, imported, nullOption]);
        try
        {
            ShowOffscreen(selector);
            var leafNode = FindEntryNode(selector, leaf);
            Assert.AreSame(parent, leafNode.Parent.Entry);
            Assert.AreSame(outer, leafNode.Parent.Parent.Entry);
            var group = leafNode.Parent.Parent.Parent;
            Assert.IsNull(group.Parent);
            Assert.IsFalse(group.CanSelect);
            StringAssert.Contains(group.DisplayName, Path.GetFileName(package.FilePath));
            Assert.IsTrue(leafNode.CanSelect);
            Assert.IsFalse(leafNode.Parent.CanSelect);
            Assert.IsFalse(leafNode.Parent.Parent.CanSelect);
            Assert.AreSame(leaf, leafNode.Item);
            Assert.AreSame(selector.GetEntrySubtitle(leaf), leafNode.Metadata);
            StringAssert.Contains(leafNode.DisplayName, leaf.ObjectName.Instanced);
            Assert.AreSame(importParent, FindEntryNode(selector, imported).Parent.Entry);
            Assert.IsFalse(FindEntryNode(selector, importParent).CanSelect);
            AssertSelectedEntry(selector, leaf);

            SelectTreeNode(selector, leafNode.Parent);
            Assert.IsNull(selector.SelectedEntryItem,
                "An ancestor displayed for context cannot be accepted as an object reference.");
            Assert.IsFalse(selector.OKCommand.CanExecute(null));

            var nullNode = selector.TreeEntries.Single(node => Equals(node.Item, nullOption));
            Assert.IsNull(nullNode.Parent);
            Assert.IsTrue(nullNode.CanSelect);
            SelectTreeNode(selector, nullNode);
            Assert.AreEqual(nullOption, selector.SelectedEntryItem);
            Assert.IsTrue(selector.OKCommand.CanExecute(null));
            SelectTreeNode(selector, FindEntryNode(selector, leaf));
            AssertSelectedEntry(selector, leaf);

            ((ComboBox)selector.FindName("ClassFilterComboBox")).SelectedItem = "SeqVar_Object";
            FlushBindings(selector);
            CollectionAssert.AreEquivalent(new object[] { leaf, nullOption }, selector.FilteredEntriesList.ToArray());
            Assert.AreSame(parent, FindEntryNode(selector, leaf).Parent.Entry);
            Assert.IsFalse(Flatten(selector.TreeEntries).Any(node => ReferenceEquals(node.Entry, sibling)));
            Assert.IsFalse(Flatten(selector.TreeEntries).Any(node => ReferenceEquals(node.Entry, imported)));

            selector.SearchText = "NeedleLeaf";
            FlushBindings(selector);
            CollectionAssert.AreEqual(new object[] { leaf }, selector.FilteredEntriesList.ToArray());
            Assert.AreSame(outer, FindEntryNode(selector, leaf).Parent.Parent.Entry);
            AssertSelectedEntry(selector, leaf);

            selector.SelectedClass = "All classes";
            selector.SearchText = "Tree searchable caption";
            FlushBindings(selector);
            CollectionAssert.AreEqual(new object[] { leaf }, selector.FilteredEntriesList.ToArray());
            Assert.AreSame(parent, FindEntryNode(selector, leaf).Parent.Entry);
            AssertSelectedEntry(selector, leaf);
        }
        finally
        {
            selector.Close();
        }
    }

    private static void IdenticalIndexesInSeparatePackagesRemainDistinct()
    {
        Settings.EntrySelector_UseTreeView = true;
        using var firstPackage = MEPackageHandler.CreateMemoryEmptyPackage("SameFilename.pcc", MEGame.LE3);
        using var secondPackage = MEPackageHandler.CreateMemoryEmptyPackage("SameFilename.pcc", MEGame.LE3);
        var first = firstPackage.CreateExport("Twin", "SeqVar_Object", indexed: false);
        var second = secondPackage.CreateExport("Twin", "SeqVar_Object", indexed: false);
        Assert.AreEqual(first.UIndex, second.UIndex);
        using var selector = CreateItemSelector([first, second]);
        try
        {
            ShowOffscreen(selector);
            Assert.HasCount(2, selector.TreeEntries,
                "Packages with equal filenames and entry indexes must have separate roots.");
            var firstNode = FindEntryNode(selector, first);
            var secondNode = FindEntryNode(selector, second);
            Assert.AreNotSame(firstNode.Parent, secondNode.Parent);
            SelectTreeNode(selector, secondNode);
            AssertSelectedEntry(selector, second);
            SelectTreeNode(selector, firstNode);
            AssertSelectedEntry(selector, first);
        }
        finally
        {
            selector.Close();
        }
    }

    private static void GenericItemsKeepListModeWithoutChangingTreePreference()
    {
        Settings.EntrySelector_UseTreeView = true;
        using var selector = CreateItemSelector(["0 Null", "First item", "Second item"]);
        try
        {
            ShowOffscreen(selector);
            AssertViewVisibility(selector, tree: false);
            Assert.IsTrue(Settings.EntrySelector_UseTreeView,
                "Opening a generic item selector must not overwrite the saved entry view choice.");
            selector.SearchText = "Second";
            FlushBindings(selector);
            Assert.AreEqual("Second item", selector.SelectedEntryItem);
            Assert.IsTrue(Settings.EntrySelector_UseTreeView);
        }
        finally
        {
            selector.Close();
        }
    }

    private static void SearchRefreshPreservesSelectionAndBuildsNewTreeResults()
    {
        Settings.EntrySelector_UseTreeView = true;
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("SelectorSearchTree.pcc", MEGame.LE3);
        var parent = package.CreateExport("Content", "Package", indexed: false);
        var first = package.CreateExport("FirstResult", "SeqVar_Object", parent, indexed: false);
        var second = package.CreateExport("SecondResult", "SeqAct_Gate", parent, indexed: false);
        Func<string, IEnumerable<object>> provider = query => query == "first" ? [first] : [first, second];
        using var selector = CreateSelector(
            [typeof(Window), typeof(Func<string, IEnumerable<object>>), typeof(string), typeof(string), typeof(string)],
            [null, provider, null, null, null]);
        try
        {
            selector.SearchText = "first";
            InvokePrivate(selector, "RunItemSearch");
            ShowOffscreen(selector);
            AssertViewVisibility(selector, tree: true);
            AssertSelectedEntry(selector, first);
            selector.SearchText = "both";
            InvokePrivate(selector, "RunItemSearch");
            FlushBindings(selector);
            AssertSelectedEntry(selector, first);
            Assert.IsTrue(FindEntryNode(selector, second).CanSelect);
            selector.ItemFilterText = "FirstResult";
            FlushBindings(selector);
            AssertSelectedEntry(selector, first);
            Assert.AreSame(parent, FindEntryNode(selector, first).Parent.Entry);
            Assert.IsFalse(Flatten(selector.TreeEntries).Any(node => ReferenceEquals(node.Entry, second)));
        }
        finally
        {
            selector.Close();
        }
    }

    private static void NestedHeaderDoubleClickAcceptsOnlySelectableEntries()
    {
        Settings.EntrySelector_UseTreeView = true;
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("SelectorDoubleClick.pcc", MEGame.LE3);
        var context = package.CreateExport("NavigationContext", "Package", indexed: false);
        var parent = package.CreateExport("EligibleParent", "Sequence", context, indexed: false);
        var leaf = package.CreateExport("NestedLeaf", "SeqVar_Object", parent, indexed: false);
        using (var accepted = CreateItemSelector([parent, leaf]))
        {
            bool? result = RunModalInteraction(accepted, () =>
            {
                var node = FindEntryNode(accepted, leaf);
                var tree = (TreeView)accepted.FindName("EntrySelectorTreeView");
                var header = FindHeader(GetContainer(accepted, tree, node), node);
                var args = DoubleClickArgs(header);
                tree.RaiseEvent(args);
                Assert.IsTrue(args.Handled, "A double-click on a nested eligible header must accept it.");
            });
            Assert.IsTrue(result);
            Assert.AreSame(leaf, GetChosenEntry(accepted),
                "Double-click must accept the nested header's entry rather than an ancestor or previous selection.");
        }

        using var rejected = CreateItemSelector([parent, leaf]);
        bool? rejectedResult = RunModalInteraction(rejected, () =>
        {
            rejected.SelectedEntryItem = leaf;
            AssertSelectedEntry(rejected, leaf);
            var tree = (TreeView)rejected.FindName("EntrySelectorTreeView");
            var contextNode = FindEntryNode(rejected, context);
            Assert.IsFalse(contextNode.CanSelect);
            var contextHeader = FindHeader(GetContainer(rejected, tree, contextNode), contextNode);
            tree.RaiseEvent(DoubleClickArgs(contextHeader));
            Assert.IsTrue(rejected.IsVisible, "A navigation-only ancestor must not accept the dialog.");
            Assert.IsNull(GetChosenEntry(rejected));

            var parentNode = FindEntryNode(rejected, parent);
            Assert.IsTrue(parentNode.CanSelect,
                "The expander guard must also protect branches whose entry could otherwise be selected.");
            var parentContainer = GetContainer(rejected, tree, parentNode);
            var expander = VisualDescendants(parentContainer).OfType<ToggleButton>()
                .FirstOrDefault(control => ReferenceEquals(control.TemplatedParent, parentContainer));
            Assert.IsNotNull(expander, "The eligible parent must have a realized tree expander.");
            tree.RaiseEvent(DoubleClickArgs(expander));
            Assert.IsTrue(rejected.IsVisible, "Double-clicking an eligible branch's expander must not accept it.");
            Assert.IsNull(GetChosenEntry(rejected));
        });
        Assert.IsFalse(rejectedResult);
        Assert.IsNull(GetChosenEntry(rejected));
    }

    private static void ClassFilterPreservesSelectionAcrossLargeRealizedTreeUpdates()
    {
        Settings.EntrySelector_UseTreeView = true;
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("SelectorLargeTreeFilter.pcc", MEGame.LE3);
        var content = package.CreateExport("Content", "Package", indexed: false);
        var sequence = package.CreateExport("ShuttleSequence", "Sequence", content, indexed: false);
        var delay = package.CreateExport("DelayAction", "SeqAct_Delay", sequence, indexed: false);
        delay.WriteProperty(new StrProperty("Wait for the shuttle to arrive.", "ObjName"));
        var target = package.CreateExport("ActorReference", "SeqVar_Object", sequence, indexed: false);
        target.WriteProperty(new NameProperty("shuttle_arrival_target", "Tag"));
        var manyActions = package.CreateExport("ManyActions", "Sequence", sequence, indexed: false);
        var distantEntries = Enumerable.Range(0, 240).Select(index =>
            package.CreateExport($"Action{index:000}", "SeqAct_Delay", manyActions, indexed: false)).ToArray();
        var importParent = new ImportEntry(package)
        {
            ObjectName = "ImportedContent", ClassName = "Package", PackageFile = "Core"
        };
        package.AddImport(importParent);
        var imported = new ImportEntry(package, importParent, "ImportedAction")
        {
            ClassName = "SeqAct_Gate", PackageFile = "Engine"
        };
        package.AddImport(imported);
        using var selector = CreateItemSelector(new object[] { delay, target, imported, "0 Null" }.Concat(distantEntries));
        try
        {
            ShowOffscreen(selector);
            FindEntryNode(selector, importParent).IsExpanded = true;
            selector.SelectedEntryItem = delay;
            PumpContextIdle();
            selector.UpdateLayout();
            PumpContextIdle();
            AssertSelectedEntry(selector, delay);

            var classes = (ComboBox)selector.FindName("ClassFilterComboBox");
            classes.SelectedItem = "SeqAct_Delay";
            PumpContextIdle();
            selector.UpdateLayout();
            PumpContextIdle();
            Assert.AreEqual("SeqAct_Delay", selector.SelectedClass);
            Assert.Contains(delay, selector.FilteredEntriesList);
            Assert.DoesNotContain(target, selector.FilteredEntriesList);
            Assert.DoesNotContain(imported, selector.FilteredEntriesList);
            AssertSelectedEntry(selector, delay);
            Assert.AreSame(FindEntryNode(selector, delay), selector.SelectedTreeNode,
                "Deferred native selection during a filter shrink must not replace the leaf with its context ancestor.");

            classes.SelectedItem = "All classes";
            PumpContextIdle();
            selector.UpdateLayout();
            PumpContextIdle();
            Assert.Contains(target, selector.FilteredEntriesList);
            Assert.Contains(imported, selector.FilteredEntriesList);
            AssertSelectedEntry(selector, delay);
            Assert.AreSame(FindEntryNode(selector, delay), selector.SelectedTreeNode,
                "Clearing the class filter must retain the same selectable leaf.");

            SelectTreeNode(selector, FindEntryNode(selector, target));
            AssertSelectedEntry(selector, target);
            classes.SelectedItem = "SeqVar_Object";
            PumpContextIdle();
            selector.UpdateLayout();
            PumpContextIdle();
            Assert.Contains(target, selector.FilteredEntriesList);
            Assert.DoesNotContain(delay, selector.FilteredEntriesList);
            AssertSelectedEntry(selector, target);
            Assert.AreSame(FindEntryNode(selector, target), selector.SelectedTreeNode,
                "A non-first selected leaf must survive filtering to its own class.");

            classes.SelectedItem = "All classes";
            PumpContextIdle();
            selector.UpdateLayout();
            PumpContextIdle();
            AssertSelectedEntry(selector, target);
            Assert.AreSame(FindEntryNode(selector, target), selector.SelectedTreeNode,
                "Clearing the filter must not replace a valid non-first selection with the first entry.");

            classes.SelectedItem = "SeqAct_Delay";
            PumpContextIdle();
            selector.UpdateLayout();
            PumpContextIdle();
            Assert.DoesNotContain(target, selector.FilteredEntriesList);
            Assert.AreSame(delay, selector.FilteredEntriesList.First());
            AssertSelectedEntry(selector, delay);
            Assert.AreSame(FindEntryNode(selector, delay), selector.SelectedTreeNode,
                "An excluded selection must fall back to the first valid entry in both the model and native tree.");
        }
        finally
        {
            selector.Close();
        }
    }

    private static void PumpContextIdle()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static bool? RunModalInteraction(EntrySelector selector, Action interaction)
    {
        selector.WindowStartupLocation = WindowStartupLocation.Manual;
        selector.ShowActivated = selector.ShowInTaskbar = false;
        selector.Left = selector.Top = -10000;
        Exception failure = null;
        selector.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            try
            {
                FlushBindings(selector);
                interaction();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                // A missing acceptance or failed assertion must not leave a modal test waiting indefinitely.
                if (selector.IsVisible)
                {
                    selector.Close();
                }
            }
        }));
        bool? result = selector.ShowDialog();
        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return result;
    }

    private static MouseButtonEventArgs DoubleClickArgs(DependencyObject source) =>
        new(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Control.MouseDoubleClickEvent,
            Source = source
        };

    private static IEntry GetChosenEntry(EntrySelector selector) => (IEntry)typeof(EntrySelector)
        .GetField("ChosenEntry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(selector);

    private static TextBlock FindHeader(TreeViewItem container, EntrySelectorTreeNode node) =>
        VisualDescendants(container).OfType<TextBlock>().First(text => ReferenceEquals(text.DataContext, node)
                                                                     && text.Text == node.DisplayName);

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in VisualDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static EntrySelector CreateItemSelector(IEnumerable<object> items) => CreateSelector(
        [typeof(Window), typeof(IEnumerable<object>), typeof(string), typeof(string)], [null, items, null, null]);

    private static EntrySelector CreateSelector(Type[] argumentTypes, object[] arguments)
    {
        var constructor = typeof(EntrySelector).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, argumentTypes, modifiers: null);
        Assert.IsNotNull(constructor);
        return (EntrySelector)constructor.Invoke(arguments);
    }

    private static void InvokePrivate(EntrySelector selector, string method) => typeof(EntrySelector)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(selector, null);

    private static IEnumerable<EntrySelectorTreeNode> Flatten(IEnumerable<EntrySelectorTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
            {
                yield return child;
            }
        }
    }

    private static EntrySelectorTreeNode FindEntryNode(EntrySelector selector, IEntry entry) =>
        Flatten(selector.TreeEntries).Single(node => ReferenceEquals(node.Entry, entry));

    private static void AssertSelectedEntry(EntrySelector selector, IEntry entry)
    {
        FlushBindings(selector);
        Assert.AreSame(entry, selector.SelectedEntryItem);
        var node = FindEntryNode(selector, entry);
        Assert.IsTrue(node.IsSelected);
        Assert.AreSame(node, ((TreeView)selector.FindName("EntrySelectorTreeView")).SelectedItem);
        for (var ancestor = node.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            Assert.IsTrue(ancestor.IsExpanded, "Selected entries must expand every ancestor.");
        }
        Assert.IsTrue(selector.OKCommand.CanExecute(null));
    }

    private static void SelectTreeNode(EntrySelector selector, EntrySelectorTreeNode node)
    {
        var tree = (TreeView)selector.FindName("EntrySelectorTreeView");
        GetContainer(selector, tree, node).IsSelected = true;
        FlushBindings(selector);
    }

    private static TreeViewItem GetContainer(EntrySelector selector, TreeView tree, EntrySelectorTreeNode node)
    {
        ItemsControl parentControl = tree;
        if (node.Parent != null)
        {
            var parent = GetContainer(selector, tree, node.Parent);
            parent.IsExpanded = true;
            FlushBindings(selector);
            parentControl = parent;
        }
        var container = parentControl.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
        Assert.IsNotNull(container, $"The tree must realize '{node.DisplayName}' under its expanded parent.");
        return container;
    }

    private static void AssertViewVisibility(EntrySelector selector, bool tree)
    {
        Assert.AreEqual(tree ? Visibility.Visible : Visibility.Collapsed,
            ((TreeView)selector.FindName("EntrySelectorTreeView")).Visibility);
        Assert.AreEqual(tree ? Visibility.Collapsed : Visibility.Visible,
            ((ListView)selector.FindName("EntrySelectorListView")).Visibility);
    }

    private static void ShowOffscreen(EntrySelector selector)
    {
        selector.WindowStartupLocation = WindowStartupLocation.Manual;
        selector.ShowActivated = selector.ShowInTaskbar = false;
        selector.Left = selector.Top = -10000;
        selector.Show();
        FlushBindings(selector);
    }

    private static void FlushBindings(EntrySelector selector)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        selector.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void InitializeApplicationResources()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(EntrySelector).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
    }
}
