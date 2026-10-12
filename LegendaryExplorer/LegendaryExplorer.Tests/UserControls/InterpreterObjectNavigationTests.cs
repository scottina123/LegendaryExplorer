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
    public void ShiftNavigationUsesNewWindowCallbackAndNeverFallsBackToCurrentEditor() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterShiftObjectNavigation.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Source", "Object", indexed: false);
        ExportEntry target = package.CreateExport("Target", "Object", indexed: false);
        source.WriteProperty(new ObjectProperty(target.UIndex, "Reference"));
        var currentEditor = new List<IEntry>();
        var newWindows = new List<IEntry>();
        using var view = new InterpreterTestView(source,
            entry => currentEditor.Add((IEntry)entry), entry => newWindows.Add((IEntry)entry));
        UPropertyTreeViewEntry node = view.Node("Reference");

        Navigate(view.Interpreter, node, false);
        Navigate(view.Interpreter, node, true);
        CollectionAssert.AreEqual(new IEntry[] { target }, currentEditor);
        CollectionAssert.AreEqual(new IEntry[] { target }, newWindows);

        view.Interpreter.OpenEntryInNewWindowCommand = new RelayCommand(_ => Assert.Fail("Disabled callback executed."), _ => false);
        Navigate(view.Interpreter, node, true);
        view.Interpreter.OpenEntryInNewWindowCommand = null;
        Navigate(view.Interpreter, node, true);
        view.Interpreter.NavigateToEntryCommand = new RelayCommand(_ => Assert.Fail("Disabled callback executed."), _ => false);
        Navigate(view.Interpreter, node, false);
        Assert.IsFalse(view.Interpreter.GoToObjectCommand.CanExecute(node));
        CollectionAssert.AreEqual(new IEntry[] { target }, currentEditor);
        CollectionAssert.AreEqual(new IEntry[] { target }, newWindows);
    });

    [TestMethod]
    public void StandaloneInterpreterUsesItsNewWindowCallbackForNormalAndShiftNavigation() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterStandaloneCallback.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Source", "Object", indexed: false);
        ExportEntry target = package.CreateExport("Target", "Object", indexed: false);
        source.WriteProperty(new ObjectProperty(target.UIndex, "Reference"));
        var newWindows = new List<IEntry>();
        using var view = new InterpreterTestView(source, null, entry => newWindows.Add((IEntry)entry));
        UPropertyTreeViewEntry node = view.Node("Reference");

        Assert.IsTrue(view.GoToButton(node).IsEnabled);
        InvokeButton(view.GoToButton(node));
        Navigate(view.Interpreter, node, true);
        CollectionAssert.AreEqual(new IEntry[] { target, target }, newWindows);

        view.Interpreter.OpenEntryInNewWindowCommand = new RelayCommand(_ => Assert.Fail("Disabled callback executed."), _ => false);
        Assert.IsFalse(view.Interpreter.GoToObjectCommand.CanExecute(node));
        Navigate(view.Interpreter, node, false);
        Navigate(view.Interpreter, node, true);
        CollectionAssert.AreEqual(new IEntry[] { target, target }, newWindows);
    });

    [TestMethod]
    [Timeout(90_000)]
    public void StandaloneInterpreterGoToButtonsOpenNewEditorsWithTheSameUnsavedPackage() => RunOnSta(() =>
    {
        FieldInfo settingsLoaded = typeof(Settings).GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
        FieldInfo deferSettingsSaving = typeof(Settings).GetField("_deferSaving", BindingFlags.Static | BindingFlags.NonPublic)!;
        object previousSettingsLoaded = settingsLoaded.GetValue(null);
        object previousDeferSettingsSaving = deferSettingsSaving.GetValue(null);
        bool previousLiveFiltering = Settings.PackageEditor_LiveFiltering;
        settingsLoaded.SetValue(null, false);
        deferSettingsSaving.SetValue(null, true);
        InitializeApplicationResources();
        SyntaxInfo.LoadFromSettings();
        DispatcherHelper.Initialize();
        SynchronizationContext previousSynchronizationContext = SynchronizationContext.Current;
        TaskScheduler previousScheduler = LegendaryExplorerCoreLib.SYNCHRONIZATION_CONTEXT;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        LegendaryExplorerCoreLib.SetSynchronizationContext(TaskScheduler.FromCurrentSynchronizationContext());
        Settings.PackageEditor_LiveFiltering = false;
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("StandaloneInterpreterReferenceWindows.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Source", "Object", indexed: false);
        ExportEntry binarySource = package.CreateExport("BinarySource", "Object", indexed: false);
        ExportEntry target = package.CreateExport("Target", "Object", indexed: false);
        var import = new ImportEntry(package) { ObjectName = "ImportedTarget", ClassName = "Object", PackageFile = "Engine" };
        package.AddImport(import);
        source.WriteProperties(new PropertyCollection
        {
            new ObjectProperty(target.UIndex, "ExportReference"),
            new ObjectProperty(import.UIndex, "ImportReference"),
            new ObjectProperty(0, "NullReference"),
            new ObjectProperty(int.MaxValue, "InvalidReference")
        });
        target.WriteProperty(new IntProperty(314, "UnsavedValue"));
        binarySource.Data = new[] { target.UIndex, import.UIndex, 0, int.MaxValue }.SelectMany(BitConverter.GetBytes).ToArray();
        byte[] originalProperties = source.Data.ToArray();
        byte[] originalBinary = binarySource.Data.ToArray();
        var binaryNodes = new[]
        {
            new BinInterpNode(0, "Export reference", BinaryInterpreterWPF.NodeType.StructLeafObject),
            new BinInterpNode(4, "Import reference", BinaryInterpreterWPF.NodeType.ArrayLeafObject),
            new BinInterpNode(8, "Null reference", BinaryInterpreterWPF.NodeType.StructLeafObject),
            new BinInterpNode(12, "Invalid reference", BinaryInterpreterWPF.NodeType.StructLeafObject)
        };
        var previousWindows = Application.Current.Windows.OfType<PackageEditorWindow>().ToHashSet();
        try
        {
            using var propertyView = new InterpreterTestView(source, null);
            using var binaryView = new StandaloneBinaryTestView(binarySource, binaryNodes);
            Assert.IsNull(propertyView.Interpreter.NavigateToEntryCommand);
            Assert.IsNull(propertyView.Interpreter.OpenEntryInNewWindowCommand);
            Assert.IsNull(binaryView.Interpreter.NavigateToEntryCommand);
            Assert.IsNull(binaryView.Interpreter.OpenEntryInNewWindowCommand);
            Assert.IsTrue(package.IsModified, "The fixture must contain unsaved content.");
            var openedWindows = new List<PackageEditorWindow>();

            foreach ((string propertyName, int binaryNodeIndex, IEntry entry) in new[]
                     {
                         ("ExportReference", 0, (IEntry)target),
                         ("ImportReference", 1, (IEntry)import)
                     })
            {
                foreach (Button button in new[]
                         {
                             propertyView.GoToButton(propertyView.Node(propertyName)),
                             binaryView.GoToButton(binaryNodes[binaryNodeIndex])
                         })
                {
                    Assert.IsTrue(button.IsEnabled);
                    PackageEditorWindow openedWindow = OpenReferenceWindow(() => InvokeButton(button, waitForIdle: false), entry);
                    Assert.IsFalse(openedWindows.Contains(openedWindow), "Each Go to click must create a new editor instance.");
                    openedWindows.Add(openedWindow);
                    Assert.AreSame(package, openedWindow.Pcc);
                    Assert.AreSame(entry, openedWindow.SelectedItem.Entry);
                    if (entry is ExportEntry)
                    {
                        var loader = (InterpreterExportLoader)openedWindow.FindName("InterpreterTab_Interpreter");
                        Assert.AreSame(target, loader.CurrentLoadedExport);
                        Assert.AreEqual(314, ((IntProperty)loader.PropertyNodes[0].ChildrenProperties
                            .Single(node => node.Property?.Name.Name == "UnsavedValue").Property).Value);
                    }
                    else
                    {
                        var loader = (EntryMetadataExportLoader)openedWindow.FindName("MetadataTab_MetadataEditor");
                        Assert.AreSame(import, loader.CurrentLoadedEntry);
                    }
                    Assert.AreSame(source, propertyView.Interpreter.CurrentLoadedExport);
                    Assert.AreSame(binarySource, binaryView.Interpreter.CurrentLoadedExport);
                }
            }

            foreach (string propertyName in new[] { "NullReference", "InvalidReference" })
            {
                UPropertyTreeViewEntry node = propertyView.Node(propertyName);
                Assert.IsFalse(propertyView.Interpreter.GoToObjectCommand.CanExecute(node));
                Assert.IsFalse(propertyView.GoToButton(node).IsEnabled);
                Navigate(propertyView.Interpreter, node, false);
            }
            foreach (BinInterpNode node in binaryNodes.Skip(2))
            {
                Assert.IsFalse(binaryView.Interpreter.GoToReferenceCommand.CanExecute(node));
                Assert.IsFalse(binaryView.GoToButton(node).IsEnabled);
                typeof(BinaryInterpreterWPF).GetMethod("NavigateToReference", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(object), typeof(bool) }, null)!.Invoke(binaryView.Interpreter, new object[] { node, false });
            }
            CollectionAssert.AreEquivalent(openedWindows, Application.Current.Windows.OfType<PackageEditorWindow>()
                .Where(window => !previousWindows.Contains(window)).ToArray());
            CollectionAssert.AreEqual(originalProperties, source.Data);
            CollectionAssert.AreEqual(originalBinary, binarySource.Data);
            Assert.IsTrue(package.IsModified);
        }
        finally
        {
            typeof(UnrealPackageFile).GetProperty(nameof(IMEPackage.IsModified))!.SetValue(package, false);
            foreach (PackageEditorWindow window in Application.Current.Windows.OfType<PackageEditorWindow>()
                         .Where(candidate => !previousWindows.Contains(candidate)).ToArray())
            {
                window.Close();
            }
            Settings.PackageEditor_LiveFiltering = previousLiveFiltering;
            settingsLoaded.SetValue(null, previousSettingsLoaded);
            deferSettingsSaving.SetValue(null, previousDeferSettingsSaving);
            LegendaryExplorerCoreLib.SetSynchronizationContext(previousScheduler);
            SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
        }
    });

    [TestMethod]
    [Timeout(90_000)]
    public void PackageEditorReferenceWindowsShareUnsavedPackageAndKeepIndependentSelections() => RunOnSta(() =>
    {
        FieldInfo settingsLoaded = typeof(Settings).GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
        FieldInfo deferSettingsSaving = typeof(Settings).GetField("_deferSaving", BindingFlags.Static | BindingFlags.NonPublic)!;
        object previousSettingsLoaded = settingsLoaded.GetValue(null);
        object previousDeferSettingsSaving = deferSettingsSaving.GetValue(null);
        bool previousLiveFiltering = Settings.PackageEditor_LiveFiltering;
        settingsLoaded.SetValue(null, false);
        deferSettingsSaving.SetValue(null, true);
        InitializeApplicationResources();
        SyntaxInfo.LoadFromSettings();
        DispatcherHelper.Initialize();
        SynchronizationContext previousSynchronizationContext = SynchronizationContext.Current;
        TaskScheduler previousScheduler = LegendaryExplorerCoreLib.SYNCHRONIZATION_CONTEXT;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        LegendaryExplorerCoreLib.SetSynchronizationContext(TaskScheduler.FromCurrentSynchronizationContext());
        Settings.PackageEditor_LiveFiltering = false;
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("PackageEditorReferenceWindows.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Source", "Object", indexed: false);
        ExportEntry target = package.CreateExport("Target", "Object", indexed: false);
        source.WriteProperty(new ObjectProperty(target.UIndex, "Reference"));
        target.WriteProperty(new IntProperty(314, "UnsavedValue"));
        var import = new ImportEntry(package) { ObjectName = "ImportedTarget", ClassName = "Object", PackageFile = "Engine" };
        package.AddImport(import);
        using IMEPackage nextPackage = MEPackageHandler.CreateMemoryEmptyPackage("PackageEditorNextReferenceWindow.pcc", MEGame.LE3);
        ExportEntry nextSource = nextPackage.CreateExport("NextSource", "Object", indexed: false);
        var previousWindows = Application.Current.Windows.OfType<PackageEditorWindow>().ToHashSet();
        var window = new PackageEditorWindow(submitTelemetry: false, enableRecents: false)
        {
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
            Width = 1200, Height = 700
        };
        try
        {
            window.Show();
            TestContext.WriteLine("Loading source window");
            window.LoadPackage(package, source.UIndex);
            WaitForPackageEditor(window, () => !window.IsBusy && ReferenceEquals(source, window.SelectedItem?.Entry));
            var tabs = (TabControl)window.FindName("EditorTabs");
            int originalTabCount = tabs.Items.Count;
            object originalSelectedTab = tabs.SelectedItem;
            var interpreter = (InterpreterExportLoader)window.FindName("InterpreterTab_Interpreter");
            var binaryInterpreter = (BinaryInterpreterWPF)window.FindName("BinaryInterpreterTab_BinaryInterpreter");
            Assert.AreSame(window.OpenEntryInNewWindowCommand, interpreter.OpenEntryInNewWindowCommand);
            Assert.AreSame(window.OpenEntryInNewWindowCommand, binaryInterpreter.OpenEntryInNewWindowCommand);
            Assert.IsTrue(package.IsModified, "The fixture must contain unsaved content.");

            TestContext.WriteLine("Opening export window");
            Assert.IsTrue(window.OpenEntryInNewWindowCommand.CanExecute(target));
            PackageEditorWindow exportWindow = OpenReferenceWindow(window, target);
            var exportLoader = (InterpreterExportLoader)exportWindow.FindName("InterpreterTab_Interpreter");
            Assert.AreSame(package, exportWindow.Pcc);
            Assert.AreSame(target, exportLoader.CurrentLoadedExport);
            Assert.AreEqual(314, ((IntProperty)exportLoader.PropertyNodes[0].ChildrenProperties
                .Single(node => node.Property?.Name.Name == "UnsavedValue").Property).Value);
            Assert.AreSame(source, window.SelectedItem.Entry);
            Assert.AreSame(originalSelectedTab, tabs.SelectedItem);
            Assert.AreEqual(originalTabCount, tabs.Items.Count);

            TestContext.WriteLine("Opening import window");
            PackageEditorWindow importWindow = OpenReferenceWindow(window, import);
            var importLoader = (EntryMetadataExportLoader)importWindow.FindName("MetadataTab_MetadataEditor");
            Assert.AreNotSame(exportWindow, importWindow);
            Assert.AreSame(package, importWindow.Pcc);
            Assert.AreSame(import, importLoader.CurrentLoadedEntry);
            Assert.AreSame(importWindow.FindName("Metadata_Tab"),
                ((TabControl)importWindow.FindName("EditorTabs")).SelectedItem);
            Assert.AreSame(source, window.SelectedItem.Entry);
            Assert.AreSame(originalSelectedTab, tabs.SelectedItem);
            Assert.AreEqual(originalTabCount, tabs.Items.Count);
            CollectionAssert.AreEquivalent(new[] { window, exportWindow, importWindow },
                package.Users.OfType<PackageEditorWindow>().ToArray());

            TestContext.WriteLine("Navigating from Names");
            // Preview uses tab visibility to choose its properties tab; keep the window shown for each navigation.
            window.Show();
            window.NavigateToEntryCommand.Execute(target);
            window.Hide();
            FlushDispatcher();
            window.Show();
            window.CurrentView = PackageEditorWindow.CurrentViewMode.Names;
            window.NavigateToEntryCommand.Execute(target);
            window.Hide();
            FlushDispatcher();
            Assert.AreEqual(PackageEditorWindow.CurrentViewMode.Tree, window.CurrentView);
            Assert.AreSame(target, window.SelectedItem.Entry);
            Assert.AreSame(window.FindName("Interpreter_Tab"), tabs.SelectedItem,
                "Navigating in the current instance must reveal its primary properties tab.");
            Assert.AreSame(target, interpreter.CurrentLoadedExport,
                "Returning from Names to the same selected object must reload its properties.");
            Assert.AreSame(target, exportLoader.CurrentLoadedExport,
                "Changing the source selection must preserve the new window's target.");
            Assert.AreSame(import, importLoader.CurrentLoadedEntry);

            TestContext.WriteLine("Switching the source package");
            window.LoadPackage(nextPackage, nextSource.UIndex);
            WaitForPackageEditor(window, () => !window.IsBusy && ReferenceEquals(nextSource, window.SelectedItem?.Entry));
            Assert.AreEqual(originalTabCount, tabs.Items.Count);
            Assert.AreSame(target, exportWindow.SelectedItem.Entry);
            Assert.AreSame(target, exportLoader.CurrentLoadedExport);
            Assert.AreSame(import, importWindow.SelectedItem.Entry);
            Assert.AreSame(import, importLoader.CurrentLoadedEntry);
            Assert.IsFalse(window.OpenEntryInNewWindowCommand.CanExecute(target),
                "A stale entry must not open from a source window that has switched packages.");

            TestContext.WriteLine("Closing the source window");
            typeof(UnrealPackageFile).GetProperty(nameof(IMEPackage.IsModified))!.SetValue(nextPackage, false);
            window.Close();
            Assert.IsTrue(Application.Current.Windows.OfType<PackageEditorWindow>().Contains(exportWindow));
            Assert.IsTrue(Application.Current.Windows.OfType<PackageEditorWindow>().Contains(importWindow));
            Assert.AreSame(package, exportWindow.Pcc);
            Assert.AreSame(package, importWindow.Pcc);
            Assert.AreSame(target, exportWindow.SelectedItem.Entry);
            Assert.AreSame(import, importWindow.SelectedItem.Entry);
            Assert.IsTrue(package.IsModified);
        }
        finally
        {
            TestContext.WriteLine("Closing editor");
            // These in-memory fixtures have no file to save when the editor closes.
            var modified = typeof(UnrealPackageFile).GetProperty(nameof(IMEPackage.IsModified))!;
            modified.SetValue(package, false);
            modified.SetValue(nextPackage, false);
            foreach (PackageEditorWindow openedWindow in Application.Current.Windows.OfType<PackageEditorWindow>()
                         .Where(candidate => !previousWindows.Contains(candidate)).ToArray())
            {
                openedWindow.Close();
            }
            Settings.PackageEditor_LiveFiltering = previousLiveFiltering;
            settingsLoaded.SetValue(null, previousSettingsLoaded);
            deferSettingsSaving.SetValue(null, previousDeferSettingsSaving);
            LegendaryExplorerCoreLib.SetSynchronizationContext(previousScheduler);
            SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
        }
    });

    private static PackageEditorWindow OpenReferenceWindow(PackageEditorWindow sourceWindow, IEntry entry)
        => OpenReferenceWindow(() => sourceWindow.OpenEntryInNewWindowCommand.Execute(entry), entry);

    private static PackageEditorWindow OpenReferenceWindow(Action navigate, IEntry entry)
    {
        var existingWindows = Application.Current.Windows.OfType<PackageEditorWindow>().ToHashSet();
        navigate();
        PackageEditorWindow window = null;
        WaitForDispatcher(() =>
        {
            if (window is not null)
            {
                return true;
            }
            window = Application.Current.Windows.OfType<PackageEditorWindow>()
                .SingleOrDefault(candidate => !existingWindows.Contains(candidate));
            if (window is null)
            {
                return false;
            }
            Assert.IsTrue(window.IsVisible, "Reference navigation must show its new package editor.");
            window.ShowInTaskbar = false;
            window.Left = -10000;
            window.Top = -10000;
            window.Hide();
            return true;
        }, "Reference navigation did not open a package editor.");
        WaitForPackageEditor(window, () => !window.IsBusy && ReferenceEquals(entry, window.SelectedItem?.Entry));
        return window;
    }

    private static void WaitForPackageEditor(PackageEditorWindow window, Func<bool> ready)
    {
        // Keep native previews hidden while testing selection and package ownership.
        window.Hide();
        WaitForDispatcher(ready, "Package editor did not finish loading and selecting its target.");
    }

    private static void WaitForDispatcher(Func<bool> ready, string failureMessage)
    {
        if (ready())
        {
            return;
        }
        var frame = new DispatcherFrame();
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        // A higher priority timer can still check the deadline while previews render.
        var timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };
        timer.Tick += (_, _) =>
        {
            if (ready() || DateTime.UtcNow >= deadline)
            {
                frame.Continue = false;
            }
        };
        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }
        Assert.IsTrue(ready(), failureMessage);
    }

    private static void Navigate(InterpreterExportLoader interpreter, UPropertyTreeViewEntry node, bool newWindow)
    {
        MethodInfo method = typeof(InterpreterExportLoader).GetMethod("NavigateToObject",
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(UPropertyTreeViewEntry), typeof(bool) }, null);
        Assert.IsNotNull(method);
        method.Invoke(interpreter, new object[] { node, newWindow });
    }

    private static void InvokeButton(Button button, bool waitForIdle = true)
    {
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        if (waitForIdle)
        {
            FlushDispatcher();
        }
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

        public InterpreterTestView(ExportEntry export, Action<object> navigate, Action<object> newWindow = null,
            PropertyCollection properties = null)
        {
            InitializeApplicationResources();
            Interpreter = new InterpreterExportLoader
            {
                ReloadPropertyDataAfterWrite = false,
                NavigateToEntryCommand = navigate is null ? null : new RelayCommand(navigate),
                OpenEntryInNewWindowCommand = newWindow is null ? null : new RelayCommand(newWindow)
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

    private sealed class StandaloneBinaryTestView : IDisposable
    {
        private readonly Window window;
        public BinaryInterpreterWPF Interpreter { get; }

        public StandaloneBinaryTestView(ExportEntry export, BinInterpNode[] nodes)
        {
            Interpreter = new BinaryInterpreterWPF { HideHexBox = true };
            typeof(ExportLoaderControl).GetProperty(nameof(ExportLoaderControl.CurrentLoadedExport))!
                .SetValue(Interpreter, export);
            window = new Window
            {
                Content = Interpreter, Width = 1500, Height = 700,
                ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000
            };
            window.Show();
            var root = new BinInterpNode("Binary start") { IsExpanded = true };
            root.Items.AddRange(nodes);
            Interpreter.TreeViewItems.Add(root);
            FlushDispatcher();
            window.UpdateLayout();
        }

        public Button GoToButton(BinInterpNode node) => FindVisualDescendants<Button>(Interpreter)
            .Single(button => button.IsVisible && ReferenceEquals(button.Command, Interpreter.GoToReferenceCommand)
                && ReferenceEquals(button.CommandParameter, node));

        public void Dispose()
        {
            window.Close();
            Interpreter.Dispose();
        }
    }
}
