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
using LegendaryExplorer.SharedUI.Controls;
using LegendaryExplorer.Tools.Sequence_Editor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.SequenceEditor;

[TestClass]
public class SequencePanelFilterTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void PanelFiltersKeepNestedMatchesAndPreserveGraphSelection()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(SequenceEditorWPF).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));

        using var package = MEPackageHandler.CreateMemoryEmptyPackage("PanelFilters.pcc", MEGame.LE3);
        var main = CreateSequence(package, "Main_Sequence");
        var nested = CreateSequence(package, "Nested_Sequence", main);
        nested.WriteProperty(new StrProperty("Friendly name", "ObjName"));
        var sibling = CreateSequence(package, "Sibling_Sequence", main);
        var unrelated = CreateSequence(package, "Unrelated_Sequence");
        var settingsLoaded = typeof(Settings).GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
        object previousLoaded = settingsLoaded.GetValue(null);
        settingsLoaded.SetValue(null, false);
        var window = new SequenceEditorWPF(enableRecents: false, loadCustomSources: false)
        {
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
            UseSavedViews = false
        };
        try
        {
            typeof(WPFBase).GetProperty(nameof(WPFBase.Pcc))!.SetValue(window, package);
            ((MenuItem)window.FindName("AutoSaveView_MenuItem")).IsChecked = false;
            ReloadTree(window);
            window.Show();
            FlushLayout(window);

            var tree = (TreeView)window.FindName("Sequences_TreeView");
            var treeSearch = (SearchBox)window.FindName("SequencesSearchBox");
            var objectSearch = (SearchBox)window.FindName("CurrentObjectsSearchBox");
            var list = (ListBox)window.FindName("CurrentObjects_ListBox");
            var root = window.TreeViewRootNodes.Single(node => node.Entry == main);
            var child = root.Sublinks.Single(node => node.Entry == nested);
            root.IsExpanded = false;
            window.SelectedItem = root;

            SetSearch(treeSearch, "  FRIENDLY  ");
            FlushLayout(window);
            Assert.IsTrue(root.IsVisibleInTree && root.IsExpanded && child.IsVisibleInTree);
            Assert.IsFalse(root.Sublinks.Single(node => node.Entry == sibling).IsVisibleInTree);
            var unrelatedNode = window.TreeViewRootNodes.Single(node => node.Entry == unrelated);
            Assert.AreEqual(Visibility.Collapsed,
                ((TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(unrelatedNode)).Visibility);

            SetSearch(treeSearch, "nested_sequence");
            Assert.IsTrue(child.IsVisibleInTree, "Object names must match even when ObjName supplies a different label.");
            SetSearch(treeSearch, $"#{nested.UIndex}");
            ReloadTree(window);
            root = window.TreeViewRootNodes.Single(node => node.Entry == main);
            Assert.IsTrue(root.IsVisibleInTree && root.Sublinks.Single(node => node.Entry == nested).IsVisibleInTree);
            Assert.IsFalse(root.Sublinks.Single(node => node.Entry == sibling).IsVisibleInTree,
                "The tree filter must survive a tree rebuild.");
            treeSearch.Clear();
            root.IsExpanded = false;
            SetSearch(treeSearch, nested.UIndex.ToString());
            Assert.IsTrue(root.IsExpanded);
            treeSearch.Clear();
            Assert.IsFalse(root.IsExpanded, "Clearing search must restore the previous expansion state.");
            Assert.IsTrue(window.TreeViewRootNodes.SelectMany(node => node.FlattenTree()).All(node => node.IsVisibleInTree));

            window.SelectedItem = root;
            FlushLayout(window);
            var nestedObject = window.CurrentObjects.Single(obj => obj.Export == nested);
            var siblingObject = window.CurrentObjects.Single(obj => obj.Export == sibling);
            list.SelectedItem = nestedObject;
            SetSearch(objectSearch, "  SIBLING_sequence  ");
            FlushLayout(window);
            Assert.AreEqual(Visibility.Collapsed, ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(nestedObject)).Visibility);
            Assert.AreEqual(Visibility.Visible, ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(siblingObject)).Visibility);
            Assert.AreSame(nestedObject, list.SelectedItem);
            Assert.HasCount(2, window.CurrentObjects, "Filtering must not remove graph objects.");

            SetSearch(objectSearch, "no match");
            list.SelectedItem = siblingObject;
            FlushLayout(window);
            Assert.AreSame(siblingObject, window.SelectedObjects.Single(),
                "Graph/navigation selection must still accept objects hidden by the list filter.");
            Assert.IsTrue(list.Items.Cast<object>().All(item =>
                ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item)).Visibility == Visibility.Collapsed));
            SetSearch(objectSearch, $"#{nested.UIndex}");
            FlushLayout(window);
            Assert.AreEqual(Visibility.Visible, ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(nestedObject)).Visibility);
            Assert.AreEqual(Visibility.Collapsed, ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(siblingObject)).Visibility);
            objectSearch.Clear();
            FlushLayout(window);
            Assert.IsTrue(list.Items.Cast<object>().All(item =>
                ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item)).Visibility == Visibility.Visible));

            // The read-only preview reparents the object list; the search bar must travel with it.
            var preview = (Grid)window.TakeReadOnlyPreviewContent(null, null);
            var panel = (DockPanel)window.FindName("CurrentObjectsPanel");
            Assert.IsTrue(preview.Children.Contains(panel));
            Assert.IsTrue(panel.Children.Contains(objectSearch));
            Assert.IsTrue(panel.Children.Contains(list));
        }
        finally
        {
            window.DisposeEmbeddedContent();
            window.Close();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            settingsLoaded.SetValue(null, previousLoaded);
        }
    }

    private static ExportEntry CreateSequence(IMEPackage package, string name, ExportEntry parent = null)
    {
        var sequence = package.CreateExport(name, "Sequence", indexed: false);
        sequence.WriteProperty(new ArrayProperty<ObjectProperty>("SequenceObjects"));
        if (parent != null)
        {
            KismetHelper.AddObjectToSequence(sequence, parent);
        }
        return sequence;
    }

    private static void ReloadTree(SequenceEditorWPF window) => typeof(SequenceEditorWPF)
        .GetMethod("LoadSequences", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);

    private static void SetSearch(SearchBox search, string text) => ((TextBox)search.FindName("searchBox")).Text = text;

    private static void FlushLayout(Window window)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }
}
