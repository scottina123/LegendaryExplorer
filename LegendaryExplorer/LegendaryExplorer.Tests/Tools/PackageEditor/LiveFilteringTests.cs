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
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.UserControls.ExportLoaderControls.ScriptEditor.IDE;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.TLK.ME1;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.PackageEditor;

[TestClass]
public class LiveFilteringTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void LiveFiltersCombineQueriesPreserveAncestorsAndRestoreTheView()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(PackageEditorWindow).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
        SyntaxInfo.LoadFromSettings();

        var loadedField = typeof(Settings).GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
        object previousLoaded = loadedField.GetValue(null);
        bool previousLiveFiltering = Settings.PackageEditor_LiveFiltering;
        loadedField.SetValue(null, false); // Never write the user's settings from a test.
        Settings.PackageEditor_LiveFiltering = true;

        using var package = MEPackageHandler.CreateMemoryEmptyPackage("LiveFiltering.pcc", MEGame.ME1);
        var parent = package.CreateExport("Parent", "Package", indexed: false);
        var matching = package.CreateExport("MatchingObject", "Object", parent, indexed: false);
        matching.WriteProperty(new StringRefProperty(692097, "Subtitle"));
        var sibling = package.CreateExport("OtherObject", "Object", parent, indexed: false);
        var tlk = package.CreateExport("tlk", "BioTlkFile", indexed: false);
        var compressor = new HuffmanCompression();
        compressor.LoadInputData([new TLKStringRef(692097, "I should go.")]);
        compressor.SerializeTalkfileToExport(tlk);
        package.LocalTalkFiles.Add(new ME1TalkFile(tlk));
        package.FindNameOrAdd("VO_692097_f_Play");

        var window = new PackageEditorWindow(submitTelemetry: false, enableRecents: false);
        try
        {
            typeof(WPFBase).GetProperty(nameof(WPFBase.Pcc))!.SetValue(window, package);
            var root = new TreeViewEntry(null, "LiveFiltering") { IsExpanded = true };
            var parentNode = new TreeViewEntry(parent) { Parent = root, IsExpanded = false };
            var matchNode = new TreeViewEntry(matching) { Parent = parentNode };
            var siblingNode = new TreeViewEntry(sibling) { Parent = parentNode };
            root.Sublinks.Add(parentNode);
            parentNode.Sublinks.AddRange([matchNode, siblingNode]);
            window.AllTreeViewNodesX.Add(root);
            var search = (WatermarkTextBox)window.FindName("Search_TextBox");
            var index = (WatermarkTextBox)window.FindName("Goto_TextBox");
            var checkbox = (CheckBox)window.FindName("LiveFiltering_CheckBox");
            Flush();
            Assert.IsTrue(checkbox.IsChecked == true, "The toolbar must read the saved preference.");

            // Exercise the inner text box as typing/pasting would, without manually refreshing the filter.
            ((TextBox)search.FindName("PART_TextBox")).Text = "matching";
            Pump(Task.Delay(350));
            Assert.IsTrue(matchNode.IsVisibleInTree);
            Assert.IsFalse(siblingNode.IsVisibleInTree, "Typing must apply the filter automatically.");

            search.Text = "  MATCHING  ";
            Apply();
            Assert.IsTrue(root.IsVisibleInTree && parentNode.IsVisibleInTree && parentNode.IsExpanded);
            Assert.IsTrue(matchNode.IsVisibleInTree);
            Assert.IsFalse(siblingNode.IsVisibleInTree);
            index.Text = sibling.UIndex.ToString();
            Apply();
            Assert.IsFalse(parentNode.IsVisibleInTree, "Filled fields must be combined.");
            index.Clear();
            window.StringRefSearchText = "692097";
            Apply();
            Assert.IsTrue(matchNode.IsVisibleInTree);
            window.StringRefSearchText = "SHOULD GO";
            Apply();
            Assert.IsTrue(matchNode.IsVisibleInTree, "StringRef filtering must resolve TLK text.");
            window.StringRefSearchText = "692098";
            Apply();
            Assert.IsFalse(matchNode.IsVisibleInTree);

            window.StringRefSearchText = null;
            search.Clear();
            index.Text = package.Imports[0].UIndex.ToString();
            Apply();
            Assert.IsTrue(window.LiveFilterMatches.SetEquals([package.Imports[0]]), "Negative UIndices must match imports exactly.");
            index.Text = "not an index";
            Apply();
            Assert.AreEqual(0, window.LiveFilterMatches.Count);
            index.Clear();
            Assert.IsNull(window.LiveFilterMatches, "Clearing all queries must immediately restore the view.");
            Assert.IsFalse(parentNode.IsExpanded, "Clearing restores the pre-filter expansion state.");

            foreach (var entry in package.Exports)
                entry.EntryHasPendingChanges = false;
            window.SetComparedChangedEntries([new EntryStringPair(matching, "Changed")]);
            search.Text = "object";
            window.ShowOnlyEditedTreeViewItems = true;
            Apply();
            Assert.IsTrue(matchNode.IsVisibleInTree && parentNode.IsVisibleInTree);
            Assert.IsFalse(siblingNode.IsVisibleInTree);
            window.ShowOnlyEditedTreeViewItems = false;

            // A pending query must not replace the newer result when its delay completes.
            search.Text = "matching";
            Task oldQuery = window.UpdateLiveFilterAsync();
            search.Text = "other";
            Apply();
            Pump(oldQuery);
            Assert.IsFalse(matchNode.IsVisibleInTree);
            Assert.IsTrue(siblingNode.IsVisibleInTree);

            checkbox.IsChecked = false;
            Flush();
            Assert.IsFalse(Settings.PackageEditor_LiveFiltering, "Toggling the checkbox must update the persisted setting.");
            Assert.IsNull(window.LiveFilterMatches);
            Assert.IsTrue(matchNode.IsVisibleInTree && siblingNode.IsVisibleInTree);
            search.Text = "matching";
            Apply();
            Assert.IsNull(window.LiveFilterMatches, "Typing with live filtering disabled keeps the full view.");

            checkbox.IsChecked = true;
            Flush();
            search.Clear();
            typeof(PackageEditorWindow).GetMethod("RefreshNames", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [null]);
            // Switching to Names on a hidden window would otherwise clear unloaded native preview controls.
            typeof(PackageEditorWindow).GetField("_currentView", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, PackageEditorWindow.CurrentViewMode.Names);
            typeof(PackageEditorWindow).GetMethod("RefreshView", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);
            search.Text = "should go";
            Apply();
            var dialogueName = window.NamesList.Single(name => name.Name == "VO_692097_f_Play");
            Assert.IsTrue(window.LiveFilterMatches.SetEquals([dialogueName]));
            var list = (ListBox)window.FindName("LeftSide_ListView");
            Assert.AreEqual(package.NameCount, list.Items.Count, "Filtering must preserve the package's index mapping.");
            index.Text = dialogueName.Index.ToString();
            window.StringRefSearchText = "692097";
            Apply();
            Assert.IsTrue(window.LiveFilterMatches.SetEquals([dialogueName]));

            list.Measure(new Size(600, 600));
            list.Arrange(new Rect(0, 0, 600, 600));
            list.UpdateLayout();
            Flush();
            Assert.AreEqual(Visibility.Visible, ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(dialogueName)).Visibility);
            var hiddenName = window.NamesList.First(name => name != dialogueName);
            Assert.AreEqual(Visibility.Collapsed, ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(hiddenName)).Visibility);

            // A query waiting for its debounce must be canceled when the window closes.
            Task closingQuery = window.UpdateLiveFilterAsync();
            window.Close();
            Pump(closingQuery);
        }
        finally
        {
            window.Close();
            Settings.PackageEditor_LiveFiltering = previousLiveFiltering;
            loadedField.SetValue(null, previousLoaded);
        }

        void Apply() => Pump(window.UpdateLiveFilterAsync(debounce: false));
    }

    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
