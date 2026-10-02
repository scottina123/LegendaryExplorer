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
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.ExportLoaderControls.ScriptEditor.IDE;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Xaml.Behaviors;

namespace LegendaryExplorer.Tests.Tools.PackageEditor;

[TestClass]
public class PackageSearchNavigationTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void SearchAndGotoSelectAndRevealEntriesAfterManualSelection()
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

        using var package = MEPackageHandler.CreateMemoryEmptyPackage("SearchNavigation.pcc", MEGame.LE3);
        var parent = package.CreateExport("Parent", "Package", indexed: false);
        var exports = Enumerable.Range(0, 200).Select(index =>
        {
            var export = package.CreateExport($"Entry{index}", "Object", parent, indexed: false);
            export.WriteProperties(new PropertyCollection());
            return export;
        }).ToArray();
        exports[^1].ObjectName = new NameReference("Target", 13);

        var window = new PackageEditorWindow(submitTelemetry: false, enableRecents: false)
        {
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
            Width = 1000, Height = 700
        };
        try
        {
            window.Show();
            typeof(WPFBase).GetProperty(nameof(WPFBase.Pcc))!.SetValue(window, package);
            var root = new TreeViewEntry(null, "SearchNavigation") { IsExpanded = true, PackageRef = package };
            var parentNode = new TreeViewEntry(parent) { Parent = root };
            root.Sublinks.Add(parentNode);
            foreach (var export in exports)
                parentNode.Sublinks.Add(new TreeViewEntry(export) { Parent = parentNode });
            foreach (var import in package.Imports)
                root.Sublinks.Add(new TreeViewEntry(import) { Parent = root });
            window.AllTreeViewNodesX.Add(root);
            typeof(PackageEditorWindow).GetMethod("RefreshView", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);
            var tree = (TreeView)window.FindName("LeftSide_TreeView");
            var behavior = Interaction.GetBehaviors(tree).OfType<NodeTreeSelectionBehavior>().Single();
            var search = (WatermarkTextBox)window.FindName("Search_TextBox");
            var objectIndex = (WatermarkTextBox)window.FindName("ObjectUiIndex_TextBox");
            var gotoIndex = (WatermarkTextBox)window.FindName("Goto_TextBox");
            Settle();

            var rootContainer = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(root);
            var parentContainer = (TreeViewItem)rootContainer.ItemContainerGenerator.ContainerFromItem(parentNode);
            parentContainer.IsSelected = true;
            Settle();
            Assert.AreSame(parentNode, window.SelectedItem, "Manual tree selection must update the editor after startup reloads the window template.");
            Assert.IsTrue(BindingOperations.IsDataBound(behavior, NodeTreeSelectionBehavior.SelectedItemProperty));

            search.Text = "Target";
            Search();
            AssertSelection(exports[^1], "Object-name search must select and reveal an off-screen export.");
            Assert.IsTrue(parentNode.IsExpanded, "Search must expand the matching export's ancestors.");

            Assert.IsTrue(window.GoToNumber(parent.UIndex));
            Settle();
            parentNode.IsExpanded = false;
            search.Clear();
            objectIndex.Text = "13";
            Search();
            AssertSelection(exports[^1], "Object-index search must navigate to the matching export.");

            objectIndex.Clear();
            gotoIndex.Text = exports[0].UIndex.ToString();
            typeof(PackageEditorWindow).GetMethod("GotoButton_Clicked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [null, null]);
            Settle();
            AssertSelection(exports[0], "The UIndex toolbar must navigate to the requested export.");

            Assert.IsTrue(window.GoToNumber(exports[10].UIndex));
            Assert.IsTrue(window.GoToNumber(exports[^1].UIndex));
            Settle();
            AssertSelection(exports[^1], "Rapid navigation must reveal the latest selection.");

            // A temporary unload must not permanently disable the behavior.
            tree.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.IsTrue(window.GoToNumber(package.Imports[0].UIndex));
            tree.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Settle();
            AssertSelection(package.Imports[0], "Navigation requested while unloaded must resume after the tree reloads.");

            Assert.IsTrue(BindingOperations.IsDataBound(behavior, NodeTreeSelectionBehavior.SelectedItemProperty),
                "Navigation must preserve the editor's selection binding.");
        }
        finally
        {
            window.Close();
            Settings.PackageEditor_LiveFiltering = previousLiveFiltering;
            settingsLoaded.SetValue(null, previousLoaded);
        }

        void Search()
        {
            Pump((Task)typeof(PackageEditorWindow).GetMethod("SearchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [false])!);
            Settle();
        }

        void AssertSelection(IEntry entry, string message)
        {
            Assert.AreSame(entry, window.SelectedItem?.Entry, message);
            var tree = (TreeView)window.FindName("LeftSide_TreeView");
            Assert.AreSame(window.SelectedItem, tree.SelectedItem, message);
            Assert.IsTrue(window.SelectedItem.IsSelected, message);
            Assert.IsTrue(window.GetSelected(out int index));
            Assert.AreEqual(entry.UIndex, index);

            var ancestors = new Stack<TreeViewEntry>();
            for (var node = window.SelectedItem; node is not null; node = node.Parent)
                ancestors.Push(node);
            ItemsControl container = tree;
            foreach (var node in ancestors)
            {
                container = container.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
                Assert.IsNotNull(container, "Navigation must realize the selected entry's container.");
            }
            var position = container.TranslatePoint(new Point(), tree);
            Assert.IsTrue(position.Y >= 0 && position.Y < tree.ActualHeight,
                "Navigation must scroll the entry into the visible viewport.");
            if (entry is ExportEntry export)
                Assert.AreSame(export, ((InterpreterExportLoader)window.FindName("InterpreterTab_Interpreter")).CurrentLoadedExport,
                    "Navigation must load the requested export in the properties pane.");
        }

        void Settle()
        {
            Pump(Task.Delay(250));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
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
