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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

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

            Node(window, positioned).SetOffset(12345, 67890);
            window.RefreshView();
            AssertPositions(window, automaticPositions);
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
