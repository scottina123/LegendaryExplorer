using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.Tools.Meshplorer;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Xceed.Wpf.Toolkit;

namespace LegendaryExplorer.Tests.Tools.Meshplorer;

[TestClass]
public class MeshplorerInlineRenameTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void ContextMenuRenamesItsMeshAndSupportsCommitCancelAndFiltering()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(MeshplorerWindow).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));

        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MeshRename.pcc", MEGame.LE3);
        var other = package.CreateExport("Other", "StaticMesh", indexed: false);
        var mesh = package.CreateExport(new NameReference("Original", 3), "StaticMesh", indexed: false);
        var window = new MeshplorerWindow(enableRecents: false);
        try
        {
            // Avoid package-update timers and renderer loading in this list interaction test.
            typeof(WPFBase).GetProperty(nameof(WPFBase.Pcc))!.SetValue(window, package);
            window.MeshExports.Add(other);
            window.MeshExports.Add(mesh);
            typeof(MeshplorerWindow).GetField("_currentExport", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, other);
            var list = (ListBox)window.FindName("MeshExportsList");
            list.Measure(new Size(280, 400));
            list.Arrange(new Rect(0, 0, 280, 400));
            list.UpdateLayout();
            typeof(IMEPackage).GetProperty(nameof(IMEPackage.IsModified))!.SetValue(package, false);

            TextBox editor = BeginRename(list, mesh);
            Assert.AreEqual("Original", editor.Text);
            var indexEditor = FindControl<IntegerUpDown>((Panel)editor.Parent, "MeshNameIndexEditor");
            Assert.AreEqual(mesh.indexValue, indexEditor.Value, "The field must match Package Editor's Object index, not the displayed suffix.");
            editor.Text = "  Renamed_7  ";
            editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            Assert.AreEqual("Original", mesh.ObjectName.Name, "Clicking inside the editor must not commit.");
            indexEditor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            Assert.AreEqual("Original", mesh.ObjectName.Name, "Moving from the name to the index must keep both edits pending.");

            // A blank-area click must commit even when it does not move keyboard focus.
            list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            Assert.AreEqual(new NameReference("Renamed_7", 3), mesh.ObjectName);
            Assert.AreEqual("Other", other.ObjectName.Name, "Rename must use the context menu's row, not CurrentExport.");
            Assert.IsTrue(package.IsModified);
            Assert.AreEqual(Visibility.Collapsed, ((Panel)editor.Parent).Visibility);
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(mesh);
            Assert.AreEqual("Renamed_7_2", FindControl<TextBlock>(row, "MeshNameDisplay").Text);
            FlushDispatcher();

            typeof(IMEPackage).GetProperty(nameof(IMEPackage.IsModified))!.SetValue(package, false);
            editor = BeginRename(list, mesh);
            editor.Text = "Discarded";
            FindControl<IntegerUpDown>((Panel)editor.Parent, "MeshNameIndexEditor").Value = 99;
            EndRename(window, commit: false);
            Assert.AreEqual(new NameReference("Renamed_7", 3), mesh.ObjectName);
            Assert.IsFalse(package.IsModified, "Cancelling must leave the package unchanged.");
            Assert.AreEqual(Visibility.Collapsed, ((Panel)editor.Parent).Visibility);

            // Repeated renames must update the label even when the export is already dirty.
            editor = BeginRename(list, mesh);
            editor.Text = "Again";
            EndRename(window, commit: true);
            row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(mesh);
            Assert.AreEqual("Again_2", FindControl<TextBlock>(row, "MeshNameDisplay").Text);
            FlushDispatcher();

            int exportIndex = mesh.UIndex;
            indexEditor = BeginIndexEdit(list, mesh);
            indexEditor.Text = "9";
            EndRename(window, commit: true);
            Assert.AreEqual(new NameReference("Again", 9), mesh.ObjectName);
            Assert.AreEqual(exportIndex, mesh.UIndex, "Changing the Object index must preserve the export table index.");
            row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(mesh);
            Assert.AreEqual("Again_8", FindControl<TextBlock>(row, "MeshNameDisplay").Text);
            Assert.AreEqual(new NameReference("Other"), other.ObjectName);
            FlushDispatcher();

            indexEditor = BeginIndexEdit(list, mesh);
            Assert.AreEqual(9, indexEditor.Value);
            indexEditor.Text = "";
            EndRename(window, commit: true);
            Assert.AreEqual(new NameReference("Again"), mesh.ObjectName);
            FlushDispatcher();

            indexEditor = BeginIndexEdit(list, mesh);
            Assert.AreEqual(0, indexEditor.Value);
            indexEditor.Value = 0;
            EndRename(window, commit: true);
            Assert.AreEqual("Again", mesh.ObjectName.Instanced);
            FlushDispatcher();

            indexEditor = BeginIndexEdit(list, mesh);
            indexEditor.Value = 1;
            EndRename(window, commit: true);
            Assert.AreEqual(1, mesh.indexValue);
            Assert.AreEqual("Again_0", mesh.ObjectName.Instanced);
            FlushDispatcher();

            indexEditor = BeginIndexEdit(list, mesh);
            Assert.AreEqual(1, indexEditor.Value, "A mesh ending in _0 must show Object index 1, as in Package Editor.");
            indexEditor.Value = int.MaxValue;
            EndRename(window, commit: true);
            Assert.AreEqual(int.MaxValue, mesh.indexValue, "The stored index must match the entered value without adding one.");
            FlushDispatcher();

            BeginIndexEdit(list, mesh).Value = 3;
            EndRename(window, commit: true);
            FlushDispatcher();

            window.MeshSearchText = "Again";
            editor = BeginRename(list, mesh);
            editor.Text = "FilteredOut";
            EndRename(window, commit: true);
            Assert.IsTrue(list.Items.Contains(mesh), "Filtering must wait until the current input event finishes.");
            FlushDispatcher();
            Assert.IsFalse(list.Items.Contains(mesh));
            Assert.AreEqual(new NameReference("FilteredOut", 3), mesh.ObjectName);
        }
        finally
        {
            EndRename(window, commit: false);
            typeof(MeshplorerWindow).GetField("_currentExport", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, null);
            typeof(WPFBase).GetProperty(nameof(WPFBase.Pcc))!.SetValue(window, null);
            FlushDispatcher();
            GC.KeepAlive(window);
        }
    }

    private static TextBox BeginRename(ListBox list, ExportEntry mesh, string header = "Rename")
    {
        list.UpdateLayout();
        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(mesh);
        Assert.IsNotNull(row);
        row.ContextMenu.PlacementTarget = row;
        row.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, header))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var editor = FindControl<TextBox>(row, "MeshNameEditor");
        Assert.AreEqual(Visibility.Visible, ((Panel)editor.Parent).Visibility);
        return editor;
    }

    private static IntegerUpDown BeginIndexEdit(ListBox list, ExportEntry mesh)
    {
        var editor = BeginRename(list, mesh, "Change index");
        return FindControl<IntegerUpDown>((Panel)editor.Parent, "MeshNameIndexEditor");
    }

    private static void EndRename(MeshplorerWindow window, bool commit) =>
        Assert.IsTrue((bool)typeof(MeshplorerWindow)
            .GetMethod("EndInlineMeshNameEdit", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [commit])!);

    private static T FindControl<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T element && element.Name == name)
                return element;
            if (FindControl<T>(child, name) is { } match)
                return match;
        }
        return null;
    }

    private static void FlushDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
