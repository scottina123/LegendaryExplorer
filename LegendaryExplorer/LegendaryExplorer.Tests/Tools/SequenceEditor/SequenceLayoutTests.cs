using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.Tools.Sequence_Editor;
using LegendaryExplorer.Tools.SequenceObjects;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Piccolo;
using Piccolo.Event;
using Piccolo.Util;

namespace LegendaryExplorer.Tests.Tools.SequenceEditor;

[TestClass]
public class SequenceLayoutTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SavedViewPreferenceSurvivesReopeningWithoutASelectedPackage(bool useSavedViews)
    {
        InitializeApplication();
        using var settingsPersistence = new SettingsPersistenceScope();
        Settings.SequenceEditor_UseSavedViews = !useSavedViews;
        SequenceEditorWPF window = null;
        SequenceEditorWPF reopenedWindow = null;
        try
        {
            window = new SequenceEditorWPF(enableRecents: false, loadCustomSources: false)
            {
                ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000
            };
            window.Show();
            FlushDispatcher();
            var menu = (MenuItem)window.FindName("UseSavedViews_MenuItem");
            Assert.IsNull(window.Pcc);
            Assert.IsNull(window.SelectedSequence);
            Assert.AreEqual(!useSavedViews, window.UseSavedViews);
            Assert.AreEqual(!useSavedViews, menu.IsChecked);

            ClickMenu(menu);
            Assert.AreEqual(useSavedViews, menu.IsChecked);
            Assert.AreEqual(useSavedViews, window.UseSavedViews);
            Assert.AreEqual(useSavedViews, Settings.SequenceEditor_UseSavedViews,
                "Changing saved views must update the preference even before opening a package.");
            CloseWindow(window);
            window = null;

            reopenedWindow = new SequenceEditorWPF(enableRecents: false, loadCustomSources: false)
            {
                ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000
            };
            reopenedWindow.Show();
            FlushDispatcher();
            Assert.AreEqual(useSavedViews, reopenedWindow.UseSavedViews,
                "A new editor must restore the saved-view preference.");
            Assert.AreEqual(useSavedViews,
                ((MenuItem)reopenedWindow.FindName("UseSavedViews_MenuItem")).IsChecked);
        }
        finally
        {
            if (window != null) CloseWindow(window);
            if (reopenedWindow != null) CloseWindow(reopenedWindow);
        }
    }

    [STATestMethod]
    public void SavedViewMenuControlsPackageAndSidecarPositionsOnEveryLoad()
    {
        InitializeApplication();
        using var settingsPersistence = new SettingsPersistenceScope();
        var temporaryDirectory = Directory.CreateTempSubdirectory("SequenceLayoutTests-");
        try
        {
            foreach (var game in new[] { MEGame.LE1, MEGame.LE2, MEGame.LE3, MEGame.ME1, MEGame.ME2, MEGame.ME3 })
            {
                PackagePositionsAndPartialAutomaticLayout(game, temporaryDirectory.FullName);
            }
            MenuTogglesSidecarPrecedenceNavigationAndRefresh(temporaryDirectory.FullName);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [STATestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public void ViewportQuickCreateKeepsClickedPositionAfterMenuClosesAndViewRefreshes(bool useSavedViews, bool emptySequence)
    {
        InitializeApplication();
        using var settingsPersistence = new SettingsPersistenceScope();
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ViewportCreation.pcc", MEGame.LE3);
        var sequence = CreateSequence(package, "Main");
        var otherSequence = CreateSequence(package, "Other");
        var existingNode = emptySequence ? null : CreateNode(sequence, "Existing", 15000, -22000);
        // A local class import keeps quick creation independent of installed game files.
        package.CreateImport("Class", "SeqVar_Int");
        var temporaryDirectory = Directory.CreateTempSubdirectory("SequenceLayoutTests-");
        var window = CreateWindow(package, Path.Combine(temporaryDirectory.FullName, "ViewportCreation.pcc"));
        try
        {
            SelectSequence(window, sequence);
            window.UseSavedViews = useSavedViews;
            var graph = (SequenceGraphEditor)typeof(SequenceEditorWPF)
                .GetField("graphEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            graph.Camera.ViewScale = 2;
            graph.Camera.SetViewOffset(-4000, 8000);
            var canvasPosition = new PointF(240, 360);
            var clickedPosition = graph.Camera.LocalToView(canvasPosition);
            Assert.AreNotEqual(canvasPosition, clickedPosition, "The test must exercise graph-coordinate conversion.");
            Assert.AreNotEqual(new PointF(graph.Camera.ViewCenterX, graph.Camera.ViewCenterY), clickedPosition);

            var contextMenu = OpenViewportContextMenu(window, graph.Camera, canvasPosition);
            var variableMenu = contextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Variables"));
            var createItem = variableMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "SeqVarInt"));
            contextMenu.IsOpen = false;
            FlushDispatcher();
            Assert.IsNull(typeof(SequenceEditorWPF)
                .GetField("backgroundContextMenuGraphLocation", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window), "Closing the menu must release its transient mouse position.");

            // WPF may close a context menu before dispatching its item click. Moving the camera also
            // makes a fallback to the current view center observably different from the original click.
            graph.Camera.SetViewOffset(12000, -16000);
            ClickMenu(createItem);
            var createdNode = package.Exports.Single(export => export.ClassName == "SeqVar_Int");
            for (int refresh = 0; refresh < 3; refresh++)
            {
                window.RefreshView();
                AssertPosition(window, createdNode, clickedPosition);
            }

            var movedPosition = new PointF(clickedPosition.X + 500, clickedPosition.Y - 750);
            Node(window, createdNode).SetOffset(movedPosition.X, movedPosition.Y);
            if (existingNode != null) Node(window, existingNode).SetOffset(42000, -43000);
            window.RefreshView();
            AssertPosition(window, createdNode, movedPosition);
            window.RefreshView();
            AssertPosition(window, createdNode, movedPosition);
            if (existingNode != null)
            {
                AssertPosition(window, existingNode, new PointF(42000, -43000));
            }
            Assert.IsNull(createdNode.GetProperty<IntProperty>("ObjPosX"));
            Assert.IsNull(createdNode.GetProperty<IntProperty>("ObjPosY"));
            if (!useSavedViews)
            {
                SelectSequence(window, otherSequence);
                SelectSequence(window, sequence);
                Assert.AreNotEqual(movedPosition, Position(window, createdNode),
                    "Returning to the sequence with saved views disabled must restore automatic layout.");
            }
        }
        finally
        {
            CloseWindow(window);
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [STATestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void LinkEditsAndBreaksPreserveTheCurrentLayoutUntilAutomaticLayoutIsRequested(bool useSavedViews)
    {
        InitializeApplication();
        using var settingsPersistence = new SettingsPersistenceScope();
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("LinkLayout.pcc", MEGame.LE3);
        var sequence = CreateSequence(package, "Main");
        var source = CreateNode(sequence, "Source", 15000, -22000);
        var target = CreateNode(sequence, "Target", 16000, -23000);
        var replacement = CreateNode(sequence, "Replacement", 17000, -24000);
        KismetHelper.CreateNewOutputLink(source, "Out", target);
        var temporaryDirectory = Directory.CreateTempSubdirectory("SequenceLayoutTests-");
        var window = CreateWindow(package, Path.Combine(temporaryDirectory.FullName, "LinkLayout.pcc"));
        try
        {
            SelectSequence(window, sequence);
            window.UseSavedViews = useSavedViews;
            var positions = MoveNodes(window);
            var graph = Graph(window);
            graph.Camera.ViewScale = 1.5f;
            graph.Camera.SetViewOffset(-4000, 8000);
            var cameraBounds = graph.Camera.ViewBounds;
            string savedJson = JsonConvert.SerializeObject(new SequenceEditorWPF.SavedViewData(
                new Dictionary<int, PointF> { [source.UIndex] = PointF.Empty }, RectangleF.Empty));
            File.WriteAllText(window.JSONpath, savedJson);
            Assert.AreEqual(1, graph.edgeLayer.ChildrenCount);

            // Exercise the serialized link edit and the current-node HandleUpdate branch.
            KismetHelper.ChangeOutputLink(source, 0, 0, replacement.UIndex);
            var outputLinks = source.GetProperty<ArrayProperty<StructProperty>>("OutputLinks");
            outputLinks[0].GetProp<StrProperty>("LinkDesc").Value = "Edited";
            source.WriteProperty(outputLinks);
            NotifyExportChanges(window, source);
            var sourceBox = (SBox)Node(window, source);
            Assert.AreEqual("Edited", sourceBox.Outlinks.Single().Desc);
            CollectionAssert.AreEqual(new[] { replacement.UIndex }, sourceBox.Outlinks.Single().Links);
            Assert.AreEqual(0, ((SAction)Node(window, target)).InputEdges.Count);
            Assert.AreEqual(1, ((SAction)Node(window, replacement)).InputEdges.Count);
            AssertPositions(window, positions);
            Assert.AreEqual(cameraBounds, graph.Camera.ViewBounds);

            // This is the same operation used by the graph's Break link menu item.
            sourceBox.RemoveOutlink(0, 0);
            NotifyExportChanges(window, source);
            Assert.IsEmpty(((SBox)Node(window, source)).Outlinks.Single().Links);
            Assert.AreEqual(0, graph.edgeLayer.ChildrenCount);
            AssertPositions(window, positions);
            Assert.AreEqual(cameraBounds, graph.Camera.ViewBounds);

            // Removing a named pin refreshes directly rather than waiting for HandleUpdate.
            typeof(SequenceEditorWPF).GetMethod("RemoveNamedLinkEntry", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { source, "OutputLinks", 0 });
            FlushDispatcher();
            Assert.IsEmpty(((SBox)Node(window, source)).Outlinks);
            AssertPositions(window, positions);
            Assert.AreEqual(cameraBounds, graph.Camera.ViewBounds);

            typeof(SequenceEditorWPF).GetMethod("LoadSequences", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);
            SelectSequence(window, sequence);
            AssertPositions(window, positions);
            Assert.AreEqual(cameraBounds, graph.Camera.ViewBounds,
                "Reselecting the current sequence after rebuilding the tree must preserve the current view.");

            var dataBeforeAutomaticLayout = package.Exports.ToDictionary(export => export, export => export.Data.ToArray());
            Assert.IsTrue(window.AutoLayoutCommand.CanExecute(null));
            window.AutoLayoutCommand.Execute(null);
            Assert.IsTrue(positions.Any(pair => Position(window, pair.Key) != pair.Value),
                "Explicit automatic layout must still rearrange the graph.");
            var automaticPositions = window.CurrentObjects.ToDictionary(obj => obj.Export, obj => Position(window, obj.Export));
            window.RefreshView();
            AssertPositions(window, automaticPositions);
            AssertPackageData(dataBeforeAutomaticLayout);
            Assert.AreEqual(savedJson, File.ReadAllText(window.JSONpath), "Graph edits must not write saved layouts.");
            Assert.AreEqual(15000, source.GetProperty<IntProperty>("ObjPosX").Value);
            Assert.AreEqual(-22000, source.GetProperty<IntProperty>("ObjPosY").Value);
            Assert.AreEqual(16000, target.GetProperty<IntProperty>("ObjPosX").Value);
            Assert.AreEqual(-23000, target.GetProperty<IntProperty>("ObjPosY").Value);
            Assert.AreEqual(17000, replacement.GetProperty<IntProperty>("ObjPosX").Value);
            Assert.AreEqual(-24000, replacement.GetProperty<IntProperty>("ObjPosY").Value);
        }
        finally
        {
            CloseWindow(window);
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [STATestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void CloneAndTrashUpdatesPreserveExistingNodesAndTheCamera(bool useSavedViews)
    {
        InitializeApplication();
        using var settingsPersistence = new SettingsPersistenceScope();
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MembershipLayout.pcc", MEGame.LE3);
        var sequence = CreateSequence(package, "Main");
        var source = CreateNode(sequence, "Source", 15000, -22000);
        var neighbor = CreateNode(sequence, "Neighbor", 16000, -23000);
        var temporaryDirectory = Directory.CreateTempSubdirectory("SequenceLayoutTests-");
        var window = CreateWindow(package, Path.Combine(temporaryDirectory.FullName, "MembershipLayout.pcc"));
        try
        {
            SelectSequence(window, sequence);
            window.UseSavedViews = useSavedViews;
            var positions = MoveNodes(window);
            SelectNodeWithoutPanning(window, source);
            var graph = Graph(window);
            graph.Camera.ViewScale = 1.5f;
            graph.Camera.SetViewOffset(-4000, 8000);
            var cameraBounds = graph.Camera.ViewBounds;
            var expectedClonePosition = new PointF(graph.Camera.ViewCenterX, graph.Camera.ViewCenterY);
            var exportsBeforeClone = package.Exports.ToHashSet();

            typeof(SequenceEditorWPF).GetMethod("CloneObject_Clicked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { null, new RoutedEventArgs() });
            var clone = package.Exports.Single(export => !exportsBeforeClone.Contains(export));
            // Membership updates take the SelectedSequence HandleUpdate branch, which also rebuilds the tree.
            NotifyExportChanges(window, sequence);
            Assert.AreEqual(3, window.CurrentObjects.Count);
            AssertPosition(window, clone, expectedClonePosition);
            AssertPositions(window, positions);
            Assert.AreEqual(cameraBounds, graph.Camera.ViewBounds);
            Assert.IsNull(clone.GetProperty<IntProperty>("ObjPosX"));
            Assert.IsNull(clone.GetProperty<IntProperty>("ObjPosY"));

            SelectNodeWithoutPanning(window, clone);
            typeof(SequenceEditorWPF).GetMethod("TrashAndRemoveFromSequence_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { null, new RoutedEventArgs() });
            NotifyExportChanges(window, sequence, clone);
            Assert.AreEqual(2, window.CurrentObjects.Count);
            Assert.IsFalse(window.CurrentObjects.Any(obj => obj.Export == clone));
            Assert.IsFalse(clone.IsA("SequenceObject"));
            CollectionAssert.AreEqual(new[] { source.UIndex, neighbor.UIndex },
                sequence.GetProperty<ArrayProperty<ObjectProperty>>("SequenceObjects").Select(obj => obj.Value).ToArray());
            AssertPositions(window, positions);
            Assert.AreEqual(cameraBounds, graph.Camera.ViewBounds);
            Assert.IsFalse(File.Exists(window.JSONpath), "Cloning and trashing must not write saved layouts.");
            Assert.AreEqual(15000, source.GetProperty<IntProperty>("ObjPosX").Value);
            Assert.AreEqual(-22000, source.GetProperty<IntProperty>("ObjPosY").Value);
            Assert.AreEqual(16000, neighbor.GetProperty<IntProperty>("ObjPosX").Value);
            Assert.AreEqual(-23000, neighbor.GetProperty<IntProperty>("ObjPosY").Value);
        }
        finally
        {
            CloseWindow(window);
            temporaryDirectory.Delete(recursive: true);
        }
    }

    private static void PackagePositionsAndPartialAutomaticLayout(MEGame game, string temporaryDirectory)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("PackagePositions.pcc", game);
        var sequence = CreateSequence(package, "Main");
        var positioned = CreateNode(sequence, "Positioned", 15000, -22000);
        var zeroXOnly = CreateNode(sequence, "ZeroXOnly", 0, null);
        var yOnly = CreateNode(sequence, "YOnly", null, -33000);
        var missingFirst = CreateNode(sequence, "MissingFirst", null, null);
        var missingSecond = CreateNode(sequence, "MissingSecond", null, null);
        var originalData = package.Exports.ToDictionary(export => export, export => export.Data.ToArray());
        var window = CreateWindow(package, Path.Combine(temporaryDirectory, $"{game}-PackagePositions.pcc"));
        try
        {
            SelectSequence(window, sequence);
            Assert.IsTrue(window.UseSavedViews);
            AssertPosition(window, positioned, new PointF(15000, -22000));
            AssertPosition(window, zeroXOnly, PointF.Empty);
            AssertPosition(window, yOnly, new PointF(0, -33000));
            Assert.AreNotEqual(Position(window, missingFirst), Position(window, missingSecond),
                $"{game}: nodes without either position must receive automatic layout.");

            foreach (var (export, data) in originalData)
            {
                CollectionAssert.AreEqual(data, export.Data, $"{game}: layout must preserve package data.");
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    private static void MenuTogglesSidecarPrecedenceNavigationAndRefresh(string temporaryDirectory)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("LayoutModes.pcc", MEGame.LE3);
        var main = CreateSequence(package, "Main");
        var positioned = CreateNode(main, "Positioned", 15000, -22000);
        var packageFallback = CreateNode(main, "PackageFallback", -16000, 23000);
        var missing = CreateNode(main, "Missing", null, null);
        var other = CreateSequence(package, "Other");
        var otherNode = CreateNode(other, "OtherNode", 32000, 42000);
        var originalData = package.Exports.ToDictionary(export => export, export => export.Data.ToArray());
        var window = CreateWindow(package, Path.Combine(temporaryDirectory, "LayoutModes.pcc"));
        try
        {
            SelectSequence(window, main);
            var menu = (MenuItem)window.FindName("UseSavedViews_MenuItem");
            Assert.IsTrue(menu.IsCheckable);
            Assert.IsTrue(menu.IsChecked, "The default saved-view mode must have a visible checkmark.");
            AssertPosition(window, positioned, new PointF(15000, -22000));

            // A rooted CurrentFile keeps SetupJSON's generated sidecar in this temporary directory.
            Assert.AreEqual(Path.GetFullPath(temporaryDirectory), Path.GetDirectoryName(window.JSONpath));
            var savedPosition = new PointF(37000, 45000);
            string savedJson = JsonConvert.SerializeObject(new SequenceEditorWPF.SavedViewData(
                new Dictionary<int, PointF> { [positioned.UIndex] = savedPosition }, RectangleF.Empty));
            string sidecarPath = window.JSONpath;
            File.WriteAllText(sidecarPath, savedJson);
            SelectSequence(window, other);
            AssertPosition(window, otherNode, new PointF(32000, 42000));
            SelectSequence(window, main);
            AssertPosition(window, positioned, savedPosition);
            AssertPosition(window, packageFallback, new PointF(-16000, 23000));

            // OnClick performs WPF's real checkable-menu toggle and command handling exactly once.
            ClickMenu(menu);
            Assert.IsFalse(menu.IsChecked);
            Assert.IsFalse(window.UseSavedViews, "A single menu click must disable saved views.");
            var automaticPositions = window.CurrentObjects.ToDictionary(obj => obj.Export, obj => Position(window, obj.Export));
            Assert.AreNotEqual(savedPosition, automaticPositions[positioned]);
            Assert.AreNotEqual(new PointF(15000, -22000), automaticPositions[positioned]);
            Assert.AreNotEqual(new PointF(-16000, 23000), automaticPositions[packageFallback]);
            Assert.AreNotEqual(automaticPositions[positioned], automaticPositions[missing]);

            var movedPosition = new PointF(12345, 67890);
            Node(window, positioned).SetOffset(movedPosition.X, movedPosition.Y);
            window.RefreshView();
            AssertPosition(window, positioned, movedPosition);
            AssertPosition(window, packageFallback, automaticPositions[packageFallback]);
            AssertPosition(window, missing, automaticPositions[missing]);
            SelectSequence(window, other);
            Assert.AreNotEqual(new PointF(32000, 42000), Position(window, otherNode));
            SelectSequence(window, main);
            AssertPositions(window, automaticPositions);

            ClickMenu(menu);
            Assert.IsTrue(menu.IsChecked);
            Assert.IsTrue(window.UseSavedViews, "A single menu click must enable saved views.");
            AssertPosition(window, positioned, savedPosition);
            AssertPosition(window, packageFallback, new PointF(-16000, 23000));

            var editedPosition = new PointF(12345, 67890);
            Node(window, positioned).SetOffset(editedPosition.X, editedPosition.Y);
            window.RefreshView();
            AssertPosition(window, positioned, editedPosition);
            Assert.AreEqual(savedJson, File.ReadAllText(sidecarPath), "Refresh must not save temporary edits to disk.");
            foreach (var (export, data) in originalData)
            {
                CollectionAssert.AreEqual(data, export.Data, "Switching view modes must preserve package data.");
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    private static ExportEntry CreateSequence(IMEPackage package, string name)
    {
        var sequence = package.CreateExport(name, "Sequence", indexed: false);
        sequence.WriteProperty(new ArrayProperty<ObjectProperty>("SequenceObjects"));
        return sequence;
    }

    private static ExportEntry CreateNode(ExportEntry sequence, string name, int? x, int? y)
    {
        var node = sequence.FileRef.CreateExport(name, "SeqAct_Log", indexed: false);
        if (x.HasValue) node.WriteProperty(new IntProperty(x.Value, "ObjPosX"));
        if (y.HasValue) node.WriteProperty(new IntProperty(y.Value, "ObjPosY"));
        KismetHelper.AddObjectToSequence(node, sequence, keepPositioning: true);
        return node;
    }

    private static SequenceEditorWPF CreateWindow(IMEPackage package, string currentFile)
    {
        var window = new SequenceEditorWPF(enableRecents: false, loadCustomSources: false)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -10000,
            Top = -10000,
            CurrentFile = currentFile
        };
        typeof(WPFBase).GetProperty(nameof(WPFBase.Pcc))!.SetValue(window, package);
        ((MenuItem)window.FindName("AutoSaveView_MenuItem")).IsChecked = false;
        typeof(SequenceEditorWPF).GetMethod("LoadSequences", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);
        window.Show();
        FlushDispatcher();
        return window;
    }

    private static void SelectSequence(SequenceEditorWPF window, ExportEntry sequence)
    {
        window.SelectedItem = window.TreeViewRootNodes.Single(root => root.Entry == sequence);
        FlushDispatcher();
        Assert.AreSame(sequence, window.SelectedSequence);
    }

    private static void ClickMenu(MenuItem menu)
    {
        typeof(MenuItem).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(menu, null);
        FlushDispatcher();
    }

    private static ContextMenu OpenViewportContextMenu(SequenceEditorWPF window, PCamera camera, PointF canvasPosition)
    {
        var inputManager = new PInputManager();
        typeof(PInputManager).GetField("currentCanvasPosition", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(inputManager, canvasPosition);
        var path = new PPickPath(camera, new RectangleF(canvasPosition, new SizeF(1, 1)));
        path.PushNode(camera);
        path.PushMatrix(camera.MatrixReference);
        var mouseEvent = new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Right,
            1, (int)canvasPosition.X, (int)canvasPosition.Y, 0);
        var inputEvent = new PInputEventArgs(inputManager, mouseEvent, PInputType.MouseDown) { Path = path };
        typeof(SequenceEditorWPF).GetMethod("backMouseDown_Handler", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { camera, inputEvent });
        FlushDispatcher();
        var contextMenu = (ContextMenu)window.FindResource("backContextMenu");
        Assert.IsTrue(contextMenu.IsOpen);
        return contextMenu;
    }

    private static SequenceGraphEditor Graph(SequenceEditorWPF window) =>
        (SequenceGraphEditor)typeof(SequenceEditorWPF)
            .GetField("graphEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static Dictionary<ExportEntry, PointF> MoveNodes(SequenceEditorWPF window)
    {
        int index = 0;
        foreach (var node in window.CurrentObjects)
        {
            node.SetOffset(42000 + index * 1500, -43000 - index * 2500);
            index++;
        }
        return window.CurrentObjects.ToDictionary(obj => obj.Export, obj => Position(window, obj.Export));
    }

    private static void SelectNodeWithoutPanning(SequenceEditorWPF window, ExportEntry export)
    {
        typeof(SequenceEditorWPF).GetField("panToSelection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, false);
        ((ListBox)window.FindName("CurrentObjects_ListBox")).SelectedItem = Node(window, export);
        FlushDispatcher();
    }

    private static void NotifyExportChanges(SequenceEditorWPF window, params ExportEntry[] exports)
    {
        var packageData = window.Pcc.Exports.ToDictionary(export => export, export => export.Data.ToArray());
        window.HandleUpdate(exports.Select(export => new PackageUpdate(PackageChange.ExportData, export.UIndex)).ToList());
        FlushDispatcher();
        AssertPackageData(packageData);
    }

    private static void AssertPackageData(Dictionary<ExportEntry, byte[]> expected)
    {
        foreach (var (export, data) in expected)
        {
            CollectionAssert.AreEqual(data, export.Data, "Refreshing the graph must not change package data.");
        }
    }

    private static SObj Node(SequenceEditorWPF window, ExportEntry export) =>
        window.CurrentObjects.Single(obj => obj.Export == export);

    private static PointF Position(SequenceEditorWPF window, ExportEntry export)
    {
        var node = Node(window, export);
        return new PointF(node.X + node.Offset.X, node.Y + node.Offset.Y);
    }

    private static void AssertPosition(SequenceEditorWPF window, ExportEntry export, PointF expected) =>
        Assert.AreEqual(expected, Position(window, export), $"{export.Game}: {export.ObjectName}");

    private static void AssertPositions(SequenceEditorWPF window, Dictionary<ExportEntry, PointF> expected)
    {
        foreach (var (export, position) in expected) AssertPosition(window, export, position);
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

    private static void CloseWindow(SequenceEditorWPF window)
    {
        window.DisposeEmbeddedContent();
        window.Close();
        FlushDispatcher();
    }

    private static void FlushDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private sealed class SettingsPersistenceScope : IDisposable
    {
        private readonly FieldInfo loaded = typeof(Settings).GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object previousValue;
        private readonly bool previousAutoSave = Settings.SequenceEditor_AutoSaveViewV2;
        private readonly bool previousOutputNumbers = Settings.SequenceEditor_ShowOutputNumbers;
        private readonly bool previousUseSavedViews = Settings.SequenceEditor_UseSavedViews;

        public SettingsPersistenceScope()
        {
            previousValue = loaded.GetValue(null);
            loaded.SetValue(null, false);
            Settings.SequenceEditor_UseSavedViews = true;
        }

        public void Dispose()
        {
            Settings.SequenceEditor_AutoSaveViewV2 = previousAutoSave;
            Settings.SequenceEditor_ShowOutputNumbers = previousOutputNumbers;
            Settings.SequenceEditor_UseSavedViews = previousUseSavedViews;
            loaded.SetValue(null, previousValue);
        }
    }
}
