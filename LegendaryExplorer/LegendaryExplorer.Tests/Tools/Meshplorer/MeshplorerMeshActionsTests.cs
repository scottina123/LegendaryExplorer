using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.Tools.Meshplorer;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.Meshplorer;

[TestClass]
public class MeshplorerMeshActionsTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void ContextMenuClonesAndTrashesTheTargetTreeAndClearsTheLastMesh()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(MeshplorerWindow).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));

        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MeshActions.pcc", MEGame.LE3);
        var mesh = package.CreateExport(new NameReference("Mesh", 3), "StaticMesh", indexed: false);
        var other = package.CreateExport(new NameReference("Mesh", 4), "StaticMesh", indexed: false);
        other.WriteBinary(StaticMesh.Create());
        var child = package.CreateExport("Child", "Object", indexed: false);
        child.Parent = mesh;
        var grandchild = package.CreateExport("Grandchild", "Object", indexed: false);
        grandchild.Parent = child;
        grandchild.WriteProperty(new ObjectProperty(mesh, "OwnerMesh"));
        var shared = package.CreateExport("Shared", "Object", indexed: false);
        var binary = StaticMesh.Create();
        binary.BodySetup = child.UIndex;
        mesh.WritePropertiesAndBinary(new PropertyCollection
        {
            new ObjectProperty(child, "OwnedObject"),
            new ObjectProperty(shared, "SharedObject")
        }, binary);

        // Keep real list controls and package operations; isolate only the preview panels.
        // These record-only stand-ins do not load the base controls' XAML or start renderers.
        var renderer = (PreviewMeshRenderer)RuntimeHelpers.GetUninitializedObject(typeof(PreviewMeshRenderer));
        var properties = (PreviewInterpreter)RuntimeHelpers.GetUninitializedObject(typeof(PreviewInterpreter));
        var binaryInterpreter = (PreviewBinaryInterpreter)RuntimeHelpers.GetUninitializedObject(typeof(PreviewBinaryInterpreter));
        var window = new MeshplorerWindow(enableRecents: false)
        {
            Mesh3DViewer = renderer,
            InterpreterTab_Interpreter = properties,
            BinaryInterpreterTab_BinaryInterpreter = binaryInterpreter
        };
        try
        {
            typeof(WPFBase).GetProperty(nameof(WPFBase.Pcc))!.SetValue(window, package);
            window.MeshExports.Add(mesh);
            window.MeshExports.Add(other);
            window.CurrentExport = other;
            var list = (ListBox)window.FindName("MeshExportsList");
            list.Measure(new Size(280, 400));
            list.Arrange(new Rect(0, 0, 280, 400));

            ClickMenu(window, mesh, "Clone Tree");
            var clone = window.CurrentExport;
            Assert.AreNotSame(mesh, clone);
            Assert.AreNotSame(other, clone);
            Assert.AreEqual("Mesh", clone.ObjectName.Name);
            Assert.AreNotEqual(mesh.ObjectName, clone.ObjectName);
            Assert.AreNotEqual(other.ObjectName, clone.ObjectName);
            Assert.HasCount(3, window.MeshExports);
            var clonedChild = clone.GetAllDescendants().Single(entry => entry.Parent == clone);
            var clonedGrandchild = (ExportEntry)clone.GetAllDescendants().Single(entry => entry.Parent == clonedChild);
            Assert.AreEqual(clonedChild.UIndex, ObjectBinary.From<StaticMesh>(clone).BodySetup);
            Assert.AreEqual(clonedChild.UIndex, clone.GetProperty<ObjectProperty>("OwnedObject").Value);
            Assert.AreEqual(clone.UIndex, clonedGrandchild.GetProperty<ObjectProperty>("OwnerMesh").Value);
            Assert.AreEqual(shared.UIndex, clone.GetProperty<ObjectProperty>("SharedObject").Value);
            Assert.AreEqual(child.UIndex, ObjectBinary.From<StaticMesh>(mesh).BodySetup);
            Assert.AreEqual(mesh.UIndex, grandchild.GetProperty<ObjectProperty>("OwnerMesh").Value);
            Assert.AreSame(clone, renderer.DisplayedExport);

            var tree = mesh.GetAllDescendants().Prepend(mesh).ToHashSet();
            Assert.IsNull(MeshplorerWindow.FindExternalMeshTreeReference(tree), "References within the tree must not trigger the trash warning.");
            shared.WriteProperty(new ObjectProperty(child, "ReferencedChild"));
            Assert.AreSame(child, MeshplorerWindow.FindExternalMeshTreeReference(tree), "References to descendants must trigger the warning too.");
            shared.RemoveProperty("ReferencedChild");

            // Trash a row while another mesh is selected and displayed.
            ClickMenu(window, mesh, "Trash entry and children");
            Assert.AreSame(clone, window.CurrentExport);
            Assert.AreSame(clone, renderer.DisplayedExport);
            Assert.HasCount(2, window.MeshExports);
            Assert.IsTrue(tree.All(entry => !package.Exports.Contains(entry) || entry.IsTrash()));
            Assert.AreEqual("Object", shared.ClassName);
            Assert.AreEqual(clonedChild.UIndex, ObjectBinary.From<StaticMesh>(clone).BodySetup);

            ClickMenu(window, other, "Trash entry and children");
            ClickMenu(window, clone, "Trash entry and children");
            Assert.IsEmpty(window.MeshExports);
            Assert.IsNull(window.CurrentExport);
            Assert.IsNull(list.SelectedItem);
            Assert.IsNull(renderer.DisplayedExport);
            Assert.IsNull(properties.DisplayedExport);
            Assert.IsNull(binaryInterpreter.DisplayedExport);
            Assert.IsTrue(package.IsModified);
        }
        finally
        {
            typeof(WPFBase).GetProperty(nameof(WPFBase.Pcc))!.SetValue(window, null);
            window.CurrentExport = null;
            FlushDispatcher();
            GC.KeepAlive(window);
        }
    }

    private static void ClickMenu(MeshplorerWindow window, ExportEntry mesh, string header)
    {
        var list = (ListBox)window.FindName("MeshExportsList");
        list.UpdateLayout();
        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(mesh);
        Assert.IsNotNull(row);
        row.ContextMenu.PlacementTarget = row;
        Dispatcher.CurrentDispatcher.Invoke(() => row.ContextMenu.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, header)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
        var timeout = Stopwatch.StartNew();
        do
        {
            FlushDispatcher();
            Assert.IsLessThan(10000, timeout.ElapsedMilliseconds, "The mesh action did not finish.");
            if (window.IsBusy)
                Thread.Sleep(10);
        } while (window.IsBusy);
        FlushDispatcher();
    }

    private static void FlushDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private sealed class PreviewMeshRenderer : MeshRenderer
    {
        public ExportEntry DisplayedExport { get; private set; }
        public override void LoadExport(ExportEntry exportEntry) => DisplayedExport = exportEntry;
        public override void UnloadExport() => DisplayedExport = null;
    }

    private sealed class PreviewInterpreter : InterpreterExportLoader
    {
        public ExportEntry DisplayedExport { get; private set; }
        public override void LoadExport(ExportEntry export) => DisplayedExport = export;
        public override void UnloadExport() => DisplayedExport = null;
    }

    private sealed class PreviewBinaryInterpreter : BinaryInterpreterWPF
    {
        public ExportEntry DisplayedExport { get; private set; }
        public override void LoadExport(ExportEntry exportEntry) => DisplayedExport = exportEntry;
        public override void UnloadExport() => DisplayedExport = null;
    }
}
