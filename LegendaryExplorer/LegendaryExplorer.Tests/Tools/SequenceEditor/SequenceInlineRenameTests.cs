using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.Tools.Sequence_Editor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.SequenceEditor;

[TestClass]
public class SequenceInlineRenameTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void TreeContextMenuSupportsInlineRenameCloneAndTrash()
    {
        InitializeApplication();
        ClickingOutsideCommitsWithoutKeyboardFocusLossIncludingTheWinFormsGraph();
        TreeContextMenuClonesAndTrashesTheTargetAndClearsTheLastSequence();
    }

    private static void ClickingOutsideCommitsWithoutKeyboardFocusLossIncludingTheWinFormsGraph()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("InlineRename.pcc", MEGame.LE3);
        var sequence = package.CreateExport(new NameReference("Original", 3), "Sequence", indexed: false);
        sequence.WriteProperty(new ArrayProperty<ObjectProperty>("SequenceObjects"));
        sequence.WriteProperty(new StrProperty("Original", "ObjName"));
        using var settingsPersistence = new SettingsPersistenceScope();
        var window = new SequenceEditorWPF(enableRecents: false, loadCustomSources: false)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -10000,
            Top = -10000
        };
        try
        {
            // Keep this in-memory fixture independent of package-update timers and saved views.
            typeof(WPFBase).GetProperty(nameof(WPFBase.Pcc))!.SetValue(window, package);
            ((MenuItem)window.FindName("AutoSaveView_MenuItem")).IsChecked = false;
            window.TreeViewRootNodes.Add(new TreeViewEntry(sequence, "Original"));
            window.Show();
            FlushDispatcher();
            window.UpdateLayout();
            var tree = (TreeView)window.FindName("Sequences_TreeView");

            TextBox editor = BeginRename(tree);
            editor.Text = "Outside click";
            editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            Assert.AreEqual("Original", sequence.ObjectName.Name, "Clicks inside the editor must keep editing.");
            Assert.AreEqual(Visibility.Visible, editor.Visibility);

            // Raising a mouse event alone does not move keyboard focus: this reproduces a blank-area click.
            object oldRow = tree.Items[0];
            tree.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            Assert.AreEqual("Outside click", sequence.ObjectName.Name);
            Assert.AreEqual("Outside click", sequence.GetProperty<StrProperty>("ObjName").Value);
            Assert.AreEqual(3, sequence.ObjectName.Number);
            Assert.AreEqual(Visibility.Collapsed, editor.Visibility);
            Assert.AreSame(oldRow, tree.Items[0], "The current click must finish before its row is rebuilt.");
            FlushDispatcher();
            window.UpdateLayout();

            editor = BeginRename(tree);
            editor.Text = "Graph click";
            var graph = (System.Windows.Forms.Control)typeof(SequenceEditorWPF)
                .GetField("graphEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            typeof(System.Windows.Forms.Control).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(graph, [new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, 10, 10, 0)]);
            Assert.AreEqual("Graph click", sequence.ObjectName.Name);
            Assert.AreEqual(Visibility.Collapsed, editor.Visibility);
        }
        finally
        {
            window.DisposeEmbeddedContent();
            window.Close();
            FlushDispatcher();
        }
    }

    private static void TreeContextMenuClonesAndTrashesTheTargetAndClearsTheLastSequence()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("TreeActions.pcc", MEGame.LE3);
        var sequence = package.CreateExport("Original", "Sequence", indexed: false);
        sequence.WriteProperty(new ArrayProperty<ObjectProperty>("SequenceObjects"));
        var nested = package.CreateExport("Nested", "Sequence", indexed: false);
        nested.WriteProperty(new ArrayProperty<ObjectProperty>("SequenceObjects"));
        KismetHelper.AddObjectToSequence(nested, sequence);
        using var settingsPersistence = new SettingsPersistenceScope();
        var window = new SequenceEditorWPF(enableRecents: false, loadCustomSources: false)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -10000,
            Top = -10000
        };
        try
        {
            typeof(WPFBase).GetProperty(nameof(WPFBase.Pcc))!.SetValue(window, package);
            ((MenuItem)window.FindName("AutoSaveView_MenuItem")).IsChecked = false;
            typeof(SequenceEditorWPF).GetMethod("LoadSequences", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);
            window.Show();
            FlushDispatcher();
            window.SelectedItem = window.TreeViewRootNodes.Single();
            var tree = (TreeView)window.FindName("Sequences_TreeView");

            ClickTreeMenu(tree, sequence, "CloneSequenceMenuItem");
            var clone = window.SelectedSequence;
            Assert.AreNotSame(sequence, clone);
            Assert.HasCount(2, window.TreeViewRootNodes);
            var clonedNested = KismetHelper.GetSequenceObjects(clone).OfType<ExportEntry>().Single();

            ClickTreeMenu(tree, clonedNested, "TrashSequenceMenuItem");
            Assert.AreSame(clone, window.SelectedSequence, "Trashing a subsequence should display its parent.");
            Assert.IsEmpty(KismetHelper.GetSequenceObjects(clone));
            Assert.IsEmpty(window.CurrentObjects);

            // The context menu must act on its row even if another sequence is displayed.
            ClickTreeMenu(tree, sequence, "TrashSequenceMenuItem");
            Assert.HasCount(1, window.TreeViewRootNodes);
            Assert.AreSame(clone, window.SelectedSequence);

            ClickTreeMenu(tree, clone, "TrashSequenceMenuItem");
            Assert.IsEmpty(window.TreeViewRootNodes);
            Assert.IsNull(window.SelectedSequence);
            Assert.IsNull(window.SelectedItem);
            Assert.IsEmpty(window.CurrentObjects);
            Assert.IsEmpty(window.SelectedObjects);
        }
        finally
        {
            window.DisposeEmbeddedContent();
            window.Close();
            FlushDispatcher();
        }
    }

    private static void InitializeApplication()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(SequenceEditorWPF).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
    }

    private static void ClickTreeMenu(TreeView tree, ExportEntry sequence, string actionName)
    {
        FlushDispatcher();
        var row = FindRow(tree, sequence);
        Assert.IsNotNull(row);
        row.ContextMenu.PlacementTarget = row;
        row.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        var action = row.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Name == actionName);
        Assert.IsTrue(action.IsEnabled);
        action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        FlushDispatcher();
    }

    private static TreeViewItem FindRow(ItemsControl tree, ExportEntry sequence)
    {
        tree.UpdateLayout();
        foreach (TreeViewEntry node in tree.Items)
        {
            var row = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(node);
            if (node.Entry == sequence)
            {
                return row;
            }
            row.IsExpanded = true;
            if (FindRow(row, sequence) is { } match)
            {
                return match;
            }
        }
        return null;
    }

    private static TextBox BeginRename(TreeView tree)
    {
        tree.UpdateLayout();
        var row = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
        row.ContextMenu.PlacementTarget = row;
        ((MenuItem)row.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return FindEditor(row);
    }

    private static TextBox FindEditor(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox { Name: "SequenceNameEditor" } editor)
            {
                return editor;
            }
            if (FindEditor(child) is { } match)
            {
                return match;
            }
        }
        return null;
    }

    private static void FlushDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private sealed class SettingsPersistenceScope : IDisposable
    {
        private readonly FieldInfo loaded = typeof(Settings).GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object previousValue;

        public SettingsPersistenceScope()
        {
            previousValue = loaded.GetValue(null);
            loaded.SetValue(null, false);
        }

        public void Dispose() => loaded.SetValue(null, previousValue);
    }
}
