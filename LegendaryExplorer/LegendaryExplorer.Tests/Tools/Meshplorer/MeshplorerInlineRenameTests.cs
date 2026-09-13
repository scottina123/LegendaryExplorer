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
            editor.Text = "  Renamed_7  ";
            editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            Assert.AreEqual("Original", mesh.ObjectName.Name, "Clicking inside the editor must not commit.");

            // A blank-area click must commit even when it does not move keyboard focus.
            list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            Assert.AreEqual(new NameReference("Renamed_7", 3), mesh.ObjectName);
            Assert.AreEqual("Other", other.ObjectName.Name, "Rename must use the context menu's row, not CurrentExport.");
            Assert.IsTrue(package.IsModified);
            Assert.AreEqual(Visibility.Collapsed, editor.Visibility);
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(mesh);
            Assert.AreEqual("Renamed_7_2", FindControl<TextBlock>(row, "MeshNameDisplay").Text);
            FlushDispatcher();

            typeof(IMEPackage).GetProperty(nameof(IMEPackage.IsModified))!.SetValue(package, false);
            editor = BeginRename(list, mesh);
            editor.Text = "Discarded";
            EndRename(window, commit: false);
            Assert.AreEqual(new NameReference("Renamed_7", 3), mesh.ObjectName);
            Assert.IsFalse(package.IsModified, "Cancelling must leave the package unchanged.");
            Assert.AreEqual(Visibility.Collapsed, editor.Visibility);

            // Repeated renames must update the label even when the export is already dirty.
            editor = BeginRename(list, mesh);
            editor.Text = "Again";
            EndRename(window, commit: true);
            row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(mesh);
            Assert.AreEqual("Again_2", FindControl<TextBlock>(row, "MeshNameDisplay").Text);
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

    private static TextBox BeginRename(ListBox list, ExportEntry mesh)
    {
        list.UpdateLayout();
        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(mesh);
        Assert.IsNotNull(row);
        row.ContextMenu.PlacementTarget = row;
        row.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Rename"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var editor = FindControl<TextBox>(row, "MeshNameEditor");
        Assert.AreEqual(Visibility.Visible, editor.Visibility);
        return editor;
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
