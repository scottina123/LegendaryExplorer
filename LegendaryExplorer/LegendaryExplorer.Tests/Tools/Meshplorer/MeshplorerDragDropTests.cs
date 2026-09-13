using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
public class MeshplorerDragDropTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void CrossPackageClonePreservesSourceAndRelinksEachNewTree()
    {
        using var source = MEPackageHandler.CreateMemoryEmptyPackage("Source.pcc", MEGame.LE3);
        using var destination = MEPackageHandler.CreateMemoryEmptyPackage("Destination.pcc", MEGame.LE3);
        var folder = source.CreateExport("Folder", "Package", indexed: false);
        var mesh = source.CreateExport(new NameReference("Mesh", 1), "StaticMesh", indexed: false);
        mesh.Parent = folder;
        var child = source.CreateExport("Child", "Object", indexed: false);
        child.Parent = mesh;
        var grandchild = source.CreateExport("Grandchild", "Object", indexed: false);
        grandchild.Parent = child;
        grandchild.WriteProperty(new ObjectProperty(mesh, "OwnerMesh"));
        var unreferencedChild = source.CreateExport("UnreferencedChild", "Object", indexed: false);
        unreferencedChild.Parent = mesh;
        var shared = source.CreateExport("Shared", "Object", indexed: false);
        var binary = StaticMesh.Create();
        binary.BodySetup = child.UIndex;
        mesh.WritePropertiesAndBinary(new PropertyCollection
        {
            new ObjectProperty(child, "OwnedObject"),
            new ObjectProperty(shared, "SharedObject")
        }, binary);

        // Deliberately use different export indices in the destination.
        destination.CreateExport("Padding", "Object", indexed: false);
        var targetFolder = destination.CreateExport("Folder", "Package", indexed: false);
        var existing = destination.CreateExport(mesh.ObjectName, "StaticMesh", indexed: false);
        existing.Parent = targetFolder;
        existing.WriteBinary(StaticMesh.Create());
        var existingData = existing.Data.ToArray();
        var existingHeader = existing.Header.ToArray();
        var sourceEntries = source.Exports.Select(e => (Entry: e, Header: e.Header.ToArray(), Data: e.Data.ToArray())).ToArray();
        var sourceNames = source.Names.ToArray();
        var sourceImportCount = source.ImportCount;
        typeof(IMEPackage).GetProperty(nameof(IMEPackage.IsModified))!.SetValue(source, false);

        for (int index = 2; index <= 3; index++)
        {
            var clone = MeshplorerWindow.CloneMeshToPackage(mesh, destination, out var report);
            Assert.IsEmpty(report, string.Join(Environment.NewLine, report.Select(r => r.Message)));
            Assert.AreSame(destination, clone.FileRef);
            Assert.AreSame(targetFolder, clone.Parent);
            Assert.AreEqual(new NameReference("Mesh", index), clone.ObjectName);
            var descendants = clone.GetAllDescendants().ToArray();
            Assert.HasCount(3, descendants, "Clone the entire tree, including unreferenced children.");
            var clonedChild = descendants.Single(e => e.ObjectName.Name == "Child");
            var clonedGrandchild = (ExportEntry)descendants.Single(e => e.ObjectName.Name == "Grandchild");
            Assert.AreSame(clonedChild, clonedGrandchild.Parent);
            Assert.AreEqual(clonedChild.UIndex, ObjectBinary.From<StaticMesh>(clone).BodySetup);
            Assert.AreEqual(clonedChild.UIndex, clone.GetProperty<ObjectProperty>("OwnedObject").Value);
            Assert.AreEqual(clone.UIndex, clonedGrandchild.GetProperty<ObjectProperty>("OwnerMesh").Value);
            Assert.AreEqual(destination.FindExport("Shared").UIndex, clone.GetProperty<ObjectProperty>("SharedObject").Value);
        }

        Assert.HasCount(1, destination.Exports.Where(e => e.ObjectName.Name == "Shared"));
        CollectionAssert.AreEqual(existingData, existing.Data);
        CollectionAssert.AreEqual(existingHeader, existing.Header);
        Assert.IsTrue(destination.IsModified);
        Assert.IsFalse(source.IsModified);
        Assert.AreEqual(sourceEntries.Length, source.ExportCount);
        Assert.AreEqual(sourceImportCount, source.ImportCount);
        CollectionAssert.AreEqual(sourceNames, source.Names.ToArray());
        foreach (var snapshot in sourceEntries)
        {
            CollectionAssert.AreEqual(snapshot.Header, snapshot.Entry.Header);
            CollectionAssert.AreEqual(snapshot.Data, snapshot.Entry.Data);
        }
    }

    [STATestMethod]
    public void RoutedDropCopiesIntoEmptyListAndKeepsClickAndFileDropBehavior()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(MeshplorerWindow).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));

        using var source = MEPackageHandler.CreateMemoryEmptyPackage("DragSource.pcc", MEGame.LE3);
        using var destination = MEPackageHandler.CreateMemoryEmptyPackage("DropTarget.pcc", MEGame.LE3);
        using var foreign = MEPackageHandler.CreateMemoryEmptyPackage("WrongGame.pcc", MEGame.LE2);
        var mesh = source.CreateExport("Mesh", "StaticMesh", indexed: false);
        mesh.WriteBinary(StaticMesh.Create());
        var from = CreateWindow(source);
        var to = CreateWindow(destination);
        try
        {
            from.MeshExports.Add(mesh);
            var sourceList = (ListBox)from.FindName("MeshExportsList");
            var targetList = (ListBox)to.FindName("MeshExportsList");
            sourceList.Measure(new Size(280, 400));
            sourceList.Arrange(new Rect(0, 0, 280, 400));
            sourceList.UpdateLayout();
            var row = (ListBoxItem)sourceList.ItemContainerGenerator.ContainerFromItem(mesh);
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            Assert.IsNull(from.CurrentExport, "Do not load a preview before the user can start dragging an unselected row.");
            sourceList.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseUpEvent
            });
            Assert.AreSame(mesh, from.CurrentExport, "A normal click must still select and display the mesh on release.");

            var payload = new MeshplorerWindow.MeshDragData(from, mesh);
            var data = new DataObject(MeshplorerWindow.MeshDragFormat, payload);
            Assert.IsFalse(from.CanAcceptMeshDrop(payload));
            Assert.IsTrue(to.CanAcceptMeshDrop(payload));
            SetPackage(to, source);
            Assert.IsFalse(to.CanAcceptMeshDrop(payload));
            SetPackage(to, null);
            Assert.IsFalse(to.CanAcceptMeshDrop(payload));
            SetPackage(to, foreign);
            Assert.IsFalse(to.CanAcceptMeshDrop(payload));
            SetPackage(to, destination);
            to.IsBusy = true;
            Assert.IsFalse(to.CanAcceptMeshDrop(payload));
            to.IsBusy = false;
            to.IsRendererBusy = true;
            Assert.IsFalse(to.CanAcceptMeshDrop(payload));
            to.IsRendererBusy = false;
            from.IsBusy = true;
            Assert.IsFalse(to.CanAcceptMeshDrop(payload));
            from.IsBusy = false;
            var rejected = RaiseDrag(targetList, data, DragDrop.PreviewDropEvent, DragDropEffects.Move);
            Assert.AreEqual(DragDropEffects.None, rejected.Effects);
            Assert.AreEqual(0, destination.ExportCount);

            to.ShowStaticMeshes = false;
            to.MeshSearchText = "hidden";
            var over = RaiseDrag(targetList, data, DragDrop.PreviewDragOverEvent);
            Assert.IsTrue(over.Handled);
            Assert.AreEqual(DragDropEffects.Copy, over.Effects);
            var drop = RaiseDrag(targetList, data, DragDrop.PreviewDropEvent);
            Assert.IsTrue(drop.Handled);
            Assert.AreEqual(DragDropEffects.Copy, drop.Effects);
            Assert.HasCount(1, to.MeshExports);
            Assert.AreSame(destination, to.CurrentExport.FileRef);
            Assert.AreSame(to.CurrentExport, targetList.SelectedItem);
            Assert.AreSame(to.CurrentExport, ((PreviewMeshRenderer)to.Mesh3DViewer).DisplayedExport);
            Assert.IsTrue(to.ShowStaticMeshes);
            Assert.AreEqual("", to.MeshSearchText);
            Assert.AreSame(mesh, from.CurrentExport);
            Assert.AreEqual(1, source.ExportCount);

            // File drags must continue through to the existing package-open handler.
            var fileData = new DataObject(DataFormats.FileDrop, new[] { "Other.pcc" });
            var fileOver = RaiseDrag(targetList, fileData, DragDrop.PreviewDragOverEvent);
            Assert.IsFalse(fileOver.Handled);
            fileOver.RoutedEvent = DragDrop.DragOverEvent;
            targetList.RaiseEvent(fileOver);
            Assert.IsFalse(fileOver.Handled);
            Assert.AreEqual(DragDropEffects.Copy, fileOver.Effects);
        }
        finally
        {
            SetPackage(from, null);
            SetPackage(to, null);
            from.CurrentExport = null;
            to.CurrentExport = null;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            GC.KeepAlive(from);
            GC.KeepAlive(to);
        }
    }

    private static DragEventArgs RaiseDrag(ListBox list, IDataObject data, RoutedEvent routedEvent,
        DragDropEffects allowedEffects = DragDropEffects.Copy)
    {
        // WPF constructs these internally during an OS drag; route the same event through the real XAML list.
        var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { data, DragDropKeyStates.None, allowedEffects, list, new Point() }, null)!;
        args.RoutedEvent = routedEvent;
        list.RaiseEvent(args);
        return args;
    }

    private static void SetPackage(MeshplorerWindow window, IMEPackage package) =>
        typeof(WPFBase).GetProperty(nameof(WPFBase.Pcc))!.SetValue(window, package);

    private static MeshplorerWindow CreateWindow(IMEPackage package)
    {
        // Keep actual WPF lists and events, without starting Direct3D or interpreter previews.
        var window = new MeshplorerWindow(enableRecents: false)
        {
            Mesh3DViewer = (PreviewMeshRenderer)RuntimeHelpers.GetUninitializedObject(typeof(PreviewMeshRenderer)),
            InterpreterTab_Interpreter = (PreviewInterpreter)RuntimeHelpers.GetUninitializedObject(typeof(PreviewInterpreter)),
            BinaryInterpreterTab_BinaryInterpreter = (PreviewBinaryInterpreter)RuntimeHelpers.GetUninitializedObject(typeof(PreviewBinaryInterpreter))
        };
        SetPackage(window, package);
        return window;
    }

    private sealed class PreviewMeshRenderer : MeshRenderer
    {
        public ExportEntry DisplayedExport { get; private set; }
        public override void LoadExport(ExportEntry exportEntry) => DisplayedExport = exportEntry;
        public override void UnloadExport() => DisplayedExport = null;
    }

    private sealed class PreviewInterpreter : InterpreterExportLoader
    {
        public override void LoadExport(ExportEntry export) { }
        public override void UnloadExport() { }
    }

    private sealed class PreviewBinaryInterpreter : BinaryInterpreterWPF
    {
        public override void LoadExport(ExportEntry exportEntry) { }
        public override void UnloadExport() { }
    }
}
