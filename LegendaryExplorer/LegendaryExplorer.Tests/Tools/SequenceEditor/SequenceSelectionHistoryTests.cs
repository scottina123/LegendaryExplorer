using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.Tools.Sequence_Editor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.SequenceEditor;

[TestClass]
public class SequenceSelectionHistoryTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void HistoryMenusLimitDestinationsAndJumpWithoutChangingThePackage()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(SequenceEditorWPF).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));

        using var package = MEPackageHandler.CreateMemoryEmptyPackage("SelectionHistory.pcc", MEGame.LE3);
        var main = CreateObject(package, "Main_Sequence", "Sequence");
        var other = CreateObject(package, "Other_Sequence", "Sequence");
        var nodes = Enumerable.Range(0, 13)
            .Select(index => CreateObject(package, $"Node_{index}", index == 0 ? "Sequence" : "SeqAct_Log", main))
            .ToArray();
        var otherNode = CreateObject(package, "Other_Node", "SeqAct_Log", other);
        var originalData = package.Exports.ToDictionary(export => export, export => export.Data.ToArray());
        foreach (var export in package.Exports)
        {
            export.EntryHasPendingChanges = false;
        }

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
            typeof(SequenceEditorWPF).GetMethod("LoadSequences", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);
            window.Show();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var list = (ListBox)window.FindName("CurrentObjects_ListBox");
            void SelectNode(ExportEntry parent, ExportEntry node)
            {
                window.SelectedItem = window.TreeViewRootNodes.Single(root => root.Entry == parent);
                list.SelectedItem = window.CurrentObjects.Single(obj => obj.Export == node);
            }

            var backMenu = ((Button)window.FindName("SelectionHistoryBackButton")).ContextMenu;
            var forwardMenu = ((Button)window.FindName("SelectionHistoryForwardButton")).ContextMenu;
            foreach (var node in nodes)
            {
                SelectNode(main, node);
            }

            backMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.HasCount(10, backMenu.Items);
            StringAssert.Contains(((TextBlock)((MenuItem)backMenu.Items[0]).Header).Text, nodes[11].InstancedFullPath);
            ((MenuItem)backMenu.Items[9]).Command.Execute(null);
            Assert.AreSame(nodes[2], window.SelectedObjects.Single().Export);

            forwardMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.HasCount(10, forwardMenu.Items);
            StringAssert.Contains(((TextBlock)((MenuItem)forwardMenu.Items[0]).Header).Text, nodes[3].InstancedFullPath);
            ((MenuItem)forwardMenu.Items[9]).Command.Execute(null);
            Assert.AreSame(nodes[12], window.SelectedObjects.Single().Export);
            Assert.IsFalse(window.NavigateSelectionForwardCommand.CanExecute(null));
            window.NavigateSelectionBackCommand.Execute(null);
            Assert.AreSame(nodes[11], window.SelectedObjects.Single().Export);

            backMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            ((MenuItem)backMenu.Items[9]).Command.Execute(null);
            window.NavigateSelectionBackCommand.Execute(null);
            Assert.AreSame(main, window.SelectedSequence, "History must select the subsequence node in its parent graph.");
            Assert.AreSame(nodes[0], window.SelectedObjects.Single().Export);
            Assert.IsFalse(window.NavigateSelectionBackCommand.CanExecute(null));

            SelectNode(other, otherNode);
            forwardMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.IsEmpty(forwardMenu.Items, "A new selection must replace the forward branch.");
            backMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.HasCount(1, backMenu.Items);
            ((MenuItem)backMenu.Items[0]).Command.Execute(null);
            Assert.AreSame(main, window.SelectedSequence);
            Assert.AreSame(nodes[0], window.SelectedObjects.Single().Export);
            forwardMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            ((MenuItem)forwardMenu.Items[0]).Command.Execute(null);
            Assert.AreSame(other, window.SelectedSequence);
            Assert.AreSame(otherNode, window.SelectedObjects.Single().Export);

            foreach (var (export, data) in originalData)
            {
                Assert.IsFalse(export.EntryHasPendingChanges, export.InstancedFullPath);
                CollectionAssert.AreEqual(data, export.Data);
            }

            SequenceEditorWPF.TrashSequenceTreeExport(nodes[0]);
            backMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.IsEmpty(backMenu.Items, "Trashed nodes must be omitted from history destinations.");
            Assert.IsFalse(window.NavigateSelectionBackCommand.CanExecute(null));
        }
        finally
        {
            window.DisposeEmbeddedContent();
            window.Close();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            settingsLoaded.SetValue(null, previousLoaded);
        }
    }

    private static ExportEntry CreateObject(IMEPackage package, string name, string className, ExportEntry parent = null)
    {
        var export = package.CreateExport(name, className, indexed: false);
        if (className == "Sequence")
        {
            export.WriteProperty(new ArrayProperty<ObjectProperty>("SequenceObjects"));
        }
        if (parent != null)
        {
            KismetHelper.AddObjectToSequence(export, parent);
        }
        return export;
    }
}
