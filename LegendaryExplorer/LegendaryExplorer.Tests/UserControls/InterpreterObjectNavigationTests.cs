using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.SharedUI.PeregrineTreeView;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.ExportLoaderControls.ScriptEditor.IDE;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class InterpreterObjectNavigationTests
{
    public TestContext TestContext { get; set; }
    // WPF allows one Application per process, so all tests must share its owning STA thread.
    private static readonly Lazy<Dispatcher> StaDispatcher = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = nameof(InterpreterObjectNavigationTests) };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    });

    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    private static void RunOnSta(Action action) => StaDispatcher.Value.Invoke(action);

    [TestMethod]
    public void GoToButtonsNavigateTheirOwnNormalAndLinkedReferencesWithoutUsingSelection() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterLinkedObjectNavigation.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Source", "Object", indexed: false);
        ExportEntry first = package.CreateExport("First", "Object", indexed: false);
        var second = new ImportEntry(package) { ObjectName = "Second", ClassName = "Object", PackageFile = "Engine" };
        package.AddImport(second);
        ExportEntry third = package.CreateExport("Third", "Object", indexed: false);
        source.WriteProperties(new PropertyCollection
        {
            new ObjectProperty(first.UIndex, "FirstReference"),
            new ObjectProperty(second.UIndex, "SecondReference"),
            new ObjectProperty(third.UIndex, "ThirdReference"),
            new IntProperty(42, "UnrelatedSelection")
        });
        byte[] originalData = source.Data.ToArray();
        var navigated = new List<IEntry>();
        using var view = new InterpreterTestView(source, entry => navigated.Add((IEntry)entry));
        UPropertyTreeViewEntry firstNode = view.Node("FirstReference");
        UPropertyTreeViewEntry secondNode = view.Node("SecondReference");
        UPropertyTreeViewEntry thirdNode = view.Node("ThirdReference");
        firstNode.LinkedNode = secondNode;
        firstNode.SecondLinkedNode = thirdNode;
        secondNode.IsVisible = false;
        thirdNode.IsVisible = false;
        UPropertyTreeViewEntry unrelated = view.Node("UnrelatedSelection");
        view.Interpreter.TreeSelectedItem = unrelated;
        view.Interpreter.SelectedItem = unrelated;
        view.RefreshLayout();

        foreach (UPropertyTreeViewEntry node in new[] { firstNode, secondNode, thirdNode })
        {
            Button button = view.GoToButton(node);
            Assert.AreEqual("Go to", button.Content);
            Assert.IsTrue(button.IsEnabled);
            InvokeButton(button);
        }

        CollectionAssert.AreEqual(new IEntry[] { first, second, third }, navigated);
        CollectionAssert.AreEqual(originalData, source.Data);
    });

    [TestMethod]
    public void ArrayAndStructObjectReferencesExposeWorkingGoToButtons() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterNestedObjectNavigation.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Source", "Object", indexed: false);
        ExportEntry first = package.CreateExport("First", "Object", indexed: false);
        ExportEntry second = package.CreateExport("Second", "Object", indexed: false);
        ExportEntry third = package.CreateExport("Third", "Object", indexed: false);
        var properties = new PropertyCollection
        {
            new ArrayProperty<ObjectProperty>("References") { new(first.UIndex), new(second.UIndex) },
            new StructProperty("ReferenceContainer", new PropertyCollection
            {
                new ObjectProperty(third.UIndex, "NestedReference")
            }, "ReferenceStruct")
        };
        source.WriteProperties(properties);
        byte[] originalData = source.Data.ToArray();
        var navigated = new List<IEntry>();
        // Supply the properties directly because this fixture uses a custom array without game metadata.
        using var view = new InterpreterTestView(source, entry => navigated.Add((IEntry)entry), properties: properties);
        UPropertyTreeViewEntry array = view.Node("References");
        UPropertyTreeViewEntry structure = view.Node("ReferenceStruct");
        array.IsExpanded = true;
        structure.IsExpanded = true;
        view.RefreshLayout();

        foreach (UPropertyTreeViewEntry node in array.ChildrenProperties.Concat(structure.ChildrenProperties)
                     .Where(node => node.Property is ObjectProperty))
        {
            InvokeButton(view.GoToButton(node));
        }

        CollectionAssert.AreEqual(new IEntry[] { first, second, third }, navigated);
        CollectionAssert.AreEqual(originalData, source.Data);
    });

    [TestMethod]
    public void GoToUsesCommittedReferenceAndDoesNotApplyStagedIndexEdits() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterStagedObjectNavigation.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Source", "Object", indexed: false);
        ExportEntry original = package.CreateExport("Original", "Object", indexed: false);
        ExportEntry replacement = package.CreateExport("Replacement", "Object", indexed: false);
        source.WriteProperty(new ObjectProperty(original.UIndex, "Reference"));
        byte[] originalData = source.Data.ToArray();
        var navigated = new List<IEntry>();
        using var view = new InterpreterTestView(source, entry => navigated.Add((IEntry)entry));
        UPropertyTreeViewEntry node = view.Node("Reference");
        TextBox indexEditor = FindVisualDescendants<TextBox>(view.Interpreter)
            .Single(textBox => ReferenceEquals(textBox.Tag, node) && !textBox.IsReadOnly && textBox.Width == 60);

        foreach (string stagedValue in new[] { replacement.UIndex.ToString(), "invalid" })
        {
            indexEditor.Text = stagedValue;
            view.RefreshLayout();
            Assert.IsTrue(view.Interpreter.GoToObjectCommand.CanExecute(node));
            InvokeButton(view.GoToButton(node));
            Assert.AreEqual(original.UIndex, ((ObjectProperty)node.Property).Value);
            Assert.AreEqual(original.UIndex, source.GetProperty<ObjectProperty>("Reference").Value);
            CollectionAssert.AreEqual(originalData, source.Data);
        }

        CollectionAssert.AreEqual(new IEntry[] { original, original }, navigated);
    });

    [TestMethod]
    public void NullInvalidAndStaleReferencesCannotNavigate() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterInvalidObjectNavigation.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Source", "Object", indexed: false);
        ExportEntry target = package.CreateExport("Target", "Object", indexed: false);
        source.WriteProperties(new PropertyCollection
        {
            new ObjectProperty(0, "NullReference"),
            new ObjectProperty(int.MaxValue, "InvalidExport"),
            new ObjectProperty(int.MinValue, "InvalidImport"),
            new IntProperty(1, "NotAnObject")
        });
        int callbacks = 0;
        using var view = new InterpreterTestView(source, _ => callbacks++, _ => callbacks++);
        foreach (string property in new[] { "NullReference", "InvalidExport", "InvalidImport" })
        {
            UPropertyTreeViewEntry node = view.Node(property);
            Assert.IsFalse(view.Interpreter.GoToObjectCommand.CanExecute(node));
            Assert.IsFalse(view.GoToButton(node).IsEnabled);
            Navigate(view.Interpreter, node, false);
            Navigate(view.Interpreter, node, true);
        }

        foreach (UPropertyTreeViewEntry node in new[]
                 {
                     view.Node("NotAnObject"),
                     new UPropertyTreeViewEntry(new ObjectProperty(target.UIndex, "Detached")),
                     new UPropertyTreeViewEntry(new ObjectProperty(target.UIndex, "Stale")) { AttachedExport = target }
                 })
        {
            Assert.IsFalse(view.Interpreter.GoToObjectCommand.CanExecute(node));
            Navigate(view.Interpreter, node, false);
            Navigate(view.Interpreter, node, true);
        }

        Assert.AreEqual(0, callbacks);
    });

    [TestMethod]
    public void ShiftNavigationUsesNewTabCallbackAndNeverFallsBackToCurrentEditor() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterShiftObjectNavigation.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Source", "Object", indexed: false);
        ExportEntry target = package.CreateExport("Target", "Object", indexed: false);
        source.WriteProperty(new ObjectProperty(target.UIndex, "Reference"));
        var currentEditor = new List<IEntry>();
        var newTabs = new List<IEntry>();
        using var view = new InterpreterTestView(source,
            entry => currentEditor.Add((IEntry)entry), entry => newTabs.Add((IEntry)entry));
        UPropertyTreeViewEntry node = view.Node("Reference");

        Navigate(view.Interpreter, node, false);
        Navigate(view.Interpreter, node, true);
        CollectionAssert.AreEqual(new IEntry[] { target }, currentEditor);
        CollectionAssert.AreEqual(new IEntry[] { target }, newTabs);

        view.Interpreter.OpenEntryInNewTabCommand = new RelayCommand(_ => Assert.Fail("Disabled callback executed."), _ => false);
        Navigate(view.Interpreter, node, true);
        view.Interpreter.OpenEntryInNewTabCommand = null;
        Navigate(view.Interpreter, node, true);
        view.Interpreter.NavigateToEntryCommand = new RelayCommand(_ => Assert.Fail("Disabled callback executed."), _ => false);
        Navigate(view.Interpreter, node, false);
        Assert.IsFalse(view.Interpreter.GoToObjectCommand.CanExecute(node));
        CollectionAssert.AreEqual(new IEntry[] { target }, currentEditor);
        CollectionAssert.AreEqual(new IEntry[] { target }, newTabs);
    });

    [TestMethod]
    [Timeout(45_000)]
    public void PackageEditorObjectTabsKeepTheirTargetsAndUnloadOnCloseOrPackageSwitch() => RunOnSta(() =>
    {
        InitializeApplicationResources();
        SyntaxInfo.LoadFromSettings();
        DispatcherHelper.Initialize();
        SynchronizationContext previousSynchronizationContext = SynchronizationContext.Current;
        TaskScheduler previousScheduler = LegendaryExplorerCoreLib.SYNCHRONIZATION_CONTEXT;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        LegendaryExplorerCoreLib.SetSynchronizationContext(TaskScheduler.FromCurrentSynchronizationContext());
        FieldInfo settingsLoaded = typeof(Settings).GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
        object previousSettingsLoaded = settingsLoaded.GetValue(null);
        bool previousLiveFiltering = Settings.PackageEditor_LiveFiltering;
        settingsLoaded.SetValue(null, false);
        Settings.PackageEditor_LiveFiltering = false;
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("PackageEditorObjectTabs.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Source", "Object", indexed: false);
        ExportEntry target = package.CreateExport("Target", "Object", indexed: false);
        source.WriteProperty(new ObjectProperty(target.UIndex, "Reference"));
        var import = new ImportEntry(package) { ObjectName = "ImportedTarget", ClassName = "Object", PackageFile = "Engine" };
        package.AddImport(import);
        using IMEPackage nextPackage = MEPackageHandler.CreateMemoryEmptyPackage("PackageEditorNextObjectTabs.pcc", MEGame.LE3);
        ExportEntry nextSource = nextPackage.CreateExport("NextSource", "Object", indexed: false);
        var window = new PackageEditorWindow(submitTelemetry: false, enableRecents: false)
        {
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
            Width = 1200, Height = 700
        };
        try
        {
            window.Show();
            TestContext.WriteLine("Loading first package");
            SetUpPackageTree(window, package, source);
            TestContext.WriteLine("Opening export tab");
            var tabs = (TabControl)window.FindName("EditorTabs");
            int originalTabCount = tabs.Items.Count;
            Assert.IsTrue(window.OpenEntryInNewTabCommand.CanExecute(target));
            window.OpenEntryInNewTabCommand.Execute(target);
            FlushPackageEditorDispatcher();
            var exportTab = (TabItem)tabs.SelectedItem;
            var exportLoader = (InterpreterExportLoader)exportTab.Content;
            Assert.AreSame(target, exportLoader.CurrentLoadedExport);
            Assert.AreSame(source, window.SelectedItem.Entry);
            Assert.AreEqual(originalTabCount + 1, tabs.Items.Count);

            TestContext.WriteLine("Opening import tab");
            window.OpenEntryInNewTabCommand.Execute(import);
            FlushPackageEditorDispatcher();
            var importTab = (TabItem)tabs.SelectedItem;
            var importLoader = (EntryMetadataExportLoader)importTab.Content;
            Assert.AreSame(import, importLoader.CurrentLoadedEntry);
            Assert.AreSame(source, window.SelectedItem.Entry);
            Assert.AreEqual(originalTabCount + 2, tabs.Items.Count);

            TestContext.WriteLine("Closing export tab");
            Button close = ((StackPanel)exportTab.Header).Children.OfType<Button>().Single();
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsFalse(tabs.Items.Contains(exportTab));
            Assert.IsNull(exportTab.Content);
            Assert.IsNull(exportTab.Header);
            Assert.IsNull(exportLoader.CurrentLoadedExport);
            Assert.AreSame(import, importLoader.CurrentLoadedEntry);

            TestContext.WriteLine("Navigating from Names");
            window.OpenEntryInNewTabCommand.Execute(target);
            FlushPackageEditorDispatcher();
            exportTab = (TabItem)tabs.SelectedItem;
            exportLoader = (InterpreterExportLoader)exportTab.Content;
            window.CurrentView = PackageEditorWindow.CurrentViewMode.Names;
            exportLoader.NavigateToEntryCommand.Execute(source);
            FlushPackageEditorDispatcher();
            Assert.AreEqual(PackageEditorWindow.CurrentViewMode.Tree, window.CurrentView);
            Assert.AreSame(source, window.SelectedItem.Entry);
            Assert.AreSame(window.FindName("Interpreter_Tab"), tabs.SelectedItem,
                "Navigating in the current instance must reveal its primary properties tab.");
            Assert.AreSame(source, ((InterpreterExportLoader)((TabItem)tabs.SelectedItem).Content).CurrentLoadedExport,
                "Returning from Names to the same selected object must reload its properties.");
            Assert.AreSame(target, exportLoader.CurrentLoadedExport,
                "Changing the primary selection must preserve the reference tab's target.");

            TestContext.WriteLine("Switching package");
            typeof(PackageEditorWindow).GetMethod("preloadPackage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { "Next", 0L, false });
            SetUpPackageTree(window, nextPackage, nextSource);
            Assert.AreEqual(originalTabCount, tabs.Items.Count);
            Assert.IsNull(exportTab.Content);
            Assert.IsNull(importTab.Content);
            Assert.IsNull(exportLoader.CurrentLoadedExport);
            Assert.IsNull(importLoader.CurrentLoadedEntry);
            Assert.IsFalse(window.OpenEntryInNewTabCommand.CanExecute(target),
                "An entry from the previous package must not open in the new package's tabs.");
        }
        finally
        {
            TestContext.WriteLine("Closing editor");
            // These in-memory fixtures have no file to save when the editor closes.
            var modified = typeof(UnrealPackageFile).GetProperty(nameof(IMEPackage.IsModified))!;
            modified.SetValue(package, false);
            modified.SetValue(nextPackage, false);
            window.Close();
            Settings.PackageEditor_LiveFiltering = previousLiveFiltering;
            settingsLoaded.SetValue(null, previousSettingsLoaded);
            LegendaryExplorerCoreLib.SetSynchronizationContext(previousScheduler);
            SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
        }
    });

    private static void SetUpPackageTree(PackageEditorWindow window, IMEPackage package, IEntry selectedEntry)
    {
        // Build the fixture synchronously; package loading's background tree scan is tested elsewhere.
        typeof(WPFBase).GetProperty(nameof(WPFBase.Pcc))!.SetValue(window, package);
        var root = new TreeViewEntry(null, package.FilePath) { IsExpanded = true, PackageRef = package };
        var nodes = package.Exports.Cast<IEntry>().Concat(package.Imports)
            .ToDictionary(entry => entry.UIndex, entry => new TreeViewEntry(entry));
        nodes.Add(0, root);
        foreach (TreeViewEntry node in nodes.Values.Where(node => node.Entry is not null))
        {
            node.Parent = nodes[node.Entry.idxLink];
            node.Parent.Sublinks.Add(node);
        }
        window.AllTreeViewNodesX.Add(root);
        typeof(PackageEditorWindow).GetMethod("RefreshView", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);
        typeof(PackageEditorWindow).GetField("IsLoadingFile", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, false);
        window.IsBusy = false;
        FlushPackageEditorDispatcher();
        Assert.IsTrue(window.GoToNumber(selectedEntry.UIndex));
        FlushPackageEditorDispatcher();
        Assert.AreSame(selectedEntry, window.SelectedItem?.Entry);
    }

    private static void FlushPackageEditorDispatcher()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }
    }

    private static void Navigate(InterpreterExportLoader interpreter, UPropertyTreeViewEntry node, bool newTab)
    {
        MethodInfo method = typeof(InterpreterExportLoader).GetMethod("NavigateToObject",
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(UPropertyTreeViewEntry), typeof(bool) }, null);
        Assert.IsNotNull(method);
        method.Invoke(interpreter, new object[] { node, newTab });
    }

    private static void InvokeButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        FlushDispatcher();
    }

    private static void InitializeApplicationResources()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(InterpreterExportLoader).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (T descendant in FindVisualDescendants<T>(child))
                yield return descendant;
        }
    }

    private static void FlushDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private sealed class InterpreterTestView : IDisposable
    {
        private readonly Window window;
        public InterpreterExportLoader Interpreter { get; }

        public InterpreterTestView(ExportEntry export, Action<object> navigate, Action<object> newTab = null,
            PropertyCollection properties = null)
        {
            InitializeApplicationResources();
            Interpreter = new InterpreterExportLoader
            {
                ReloadPropertyDataAfterWrite = false,
                NavigateToEntryCommand = new RelayCommand(navigate),
                OpenEntryInNewTabCommand = newTab is null ? null : new RelayCommand(newTab)
            };
            window = new Window
            {
                Content = Interpreter, Width = 1500, Height = 700,
                ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000
            };
            window.Show();
            Interpreter.LoadExport(export, properties);
            Interpreter.PropertyNodes[0].IsExpanded = true;
            RefreshLayout();
        }

        public UPropertyTreeViewEntry Node(string name) =>
            Interpreter.PropertyNodes[0].ChildrenProperties.Single(node => node.Property?.Name.Name == name);

        public Button GoToButton(UPropertyTreeViewEntry node) =>
            FindVisualDescendants<Button>(Interpreter).Single(button => button.IsVisible
                && ReferenceEquals(button.Command, Interpreter.GoToObjectCommand)
                && ReferenceEquals(button.CommandParameter, node));

        public void RefreshLayout()
        {
            FlushDispatcher();
            window.UpdateLayout();
        }

        public void Dispose()
        {
            window.Close();
            Interpreter.Dispose();
        }
    }
}
