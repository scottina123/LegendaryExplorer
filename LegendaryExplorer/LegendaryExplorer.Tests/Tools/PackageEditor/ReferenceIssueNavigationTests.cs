using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.SharedUI.PeregrineTreeView;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.ExportLoaderControls.ScriptEditor.IDE;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Localization;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.PackageEditor;

[TestClass]
public class ReferenceIssueNavigationTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void IssueClickSelectsNestedPropertyAndRepeatedBinaryFieldsAfterAsyncScan()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(PackageEditorWindow).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherHelper.Initialize();
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
        SyntaxInfo.LoadFromSettings();
        var previousSynchronizationContext = SynchronizationContext.Current;
        var previousScheduler = LegendaryExplorerCoreLib.SYNCHRONIZATION_CONTEXT;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        LegendaryExplorerCoreLib.SetSynchronizationContext(TaskScheduler.FromCurrentSynchronizationContext());

        var settingsLoaded = typeof(Settings).GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
        object previousLoaded = settingsLoaded.GetValue(null);
        bool previousLiveFiltering = Settings.PackageEditor_LiveFiltering;
        bool previousAutoParse = Settings.BinaryInterpreter_SkipAutoParseSizeCheck;
        settingsLoaded.SetValue(null, false);
        Settings.PackageEditor_LiveFiltering = false;
        Settings.BinaryInterpreter_SkipAutoParseSizeCheck = false;

        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceIssueNavigation.pcc", MEGame.LE3);
        var propertiesExport = package.CreateExport("Properties", "MaterialInstanceConstant", indexed: false);
        propertiesExport.WriteProperty(new ArrayProperty<StructProperty>(new[]
        {
            new StructProperty("TextureParameterValue", new PropertyCollection
            {
                new NameProperty("Diffuse", "ParameterName"),
                new ObjectProperty(-999, "ParameterValue"),
                StructProperty.FromGuid(Guid.Empty, "ExpressionGUID")
            }),
            new StructProperty("TextureParameterValue", new PropertyCollection
            {
                new NameProperty("Normal", "ParameterName"),
                new ObjectProperty(-999, "ParameterValue"),
                StructProperty.FromGuid(Guid.Empty, "ExpressionGUID")
            })
        }, "TextureParameterValues"));
        var binaryExport = package.CreateExport("Binary", "Material", indexed: false);
        var binary = Material.Create();
        binary.SM3MaterialResource.UniformExpressionTextures = [-999, -999];
        binaryExport.WriteBinary(binary);
        var largeData = binaryExport.Data;
        Array.Resize(ref largeData, 25000);
        binaryExport.Data = largeData;
        var results = new ReferenceCheckPackage();
        EntryChecker.CheckReferences(results, package, LECLocalizationShim.NonLocalizedStringConverter);
        var propertyIssues = results.GetSignificantIssues().OfType<ReferenceIssue>()
            .Where(issue => issue.Entry == propertiesExport && issue.Location == ReferenceIssueLocation.Property).ToArray();
        var binaryIssues = results.GetSignificantIssues().OfType<ReferenceIssue>()
            .Where(issue => issue.Entry == binaryExport && issue.Location == ReferenceIssueLocation.Binary).ToArray();
        Assert.HasCount(2, propertyIssues);
        Assert.HasCount(2, binaryIssues);

        var window = new PackageEditorWindow(submitTelemetry: false, enableRecents: false)
        {
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
            Width = 1000, Height = 700
        };
        try
        {
            window.Show();
            typeof(WPFBase).GetProperty(nameof(WPFBase.Pcc))!.SetValue(window, package);
            var root = new TreeViewEntry(null, "ReferenceIssueNavigation") { IsExpanded = true, PackageRef = package };
            foreach (var entry in package.Exports.Cast<IEntry>().Concat(package.Imports))
            {
                root.Sublinks.Add(new TreeViewEntry(entry) { Parent = root });
            }
            window.AllTreeViewNodesX.Add(root);
            Invoke(window, "RefreshView");
            Pump(Task.Delay(250));
            var interpreter = (InterpreterExportLoader)window.FindName("InterpreterTab_Interpreter");
            var binaryInterpreter = (BinaryInterpreterWPF)window.FindName("BinaryInterpreterTab_BinaryInterpreter");
            var binaryTree = (TreeView)binaryInterpreter.FindName("BinaryInterpreter_TreeView");

            Invoke(window, "objectReferenceDoubleClick", propertyIssues[1]);
            WaitUntil(() => interpreter.SelectedItem?.Property?.ValueOffset == propertyIssues[1].ValueOffset);
            Assert.AreSame(propertiesExport, window.SelectedItem.Entry);
            Assert.IsTrue(((TabItem)window.FindName("Interpreter_Tab")).IsSelected);
            Assert.AreEqual(-999, ((ObjectProperty)interpreter.SelectedItem.Property).Value);
            for (var parent = interpreter.SelectedItem.UPParent; parent is not null; parent = parent.UPParent)
            {
                Assert.IsTrue(parent.IsExpanded, "Nested properties must expand their parent rows.");
            }

            Invoke(window, "objectReferenceDoubleClick", binaryIssues[1]);
            WaitUntil(() => binaryTree.SelectedItem is BinInterpNode node && node.Offset == binaryIssues[1].Offset,
                () => $"Expected offset {binaryIssues[1].Offset}; export {binaryInterpreter.CurrentLoadedExport?.ObjectName}; selected {binaryTree.SelectedItem}; "
                    + $"tab {((TabItem)window.FindName("BinaryInterpreter_Tab")).IsSelected}; "
                    + string.Join("\n", binaryInterpreter.TreeViewItems.SelectMany(Flatten).Select(node =>
                        $"{node.Offset}: {node.Header}; selected={node.IsSelected}; expanded={node.IsExpanded}; parent={node.Parent?.GetType().Name}")));
            Assert.AreSame(binaryExport, window.SelectedItem.Entry);
            Assert.IsTrue(((TabItem)window.FindName("BinaryInterpreter_Tab")).IsSelected);
            Assert.AreEqual(binaryIssues[1].Offset, ((BinInterpNode)binaryTree.SelectedItem).Offset,
                "The large export must finish parsing before the requested duplicate reference is selected.");

            Invoke(window, "objectReferenceDoubleClick", binaryIssues[0]);
            WaitUntil(() => binaryTree.SelectedItem is BinInterpNode node && node.Offset == binaryIssues[0].Offset);

            Invoke(window, "objectReferenceDoubleClick", propertyIssues[0]);
            Invoke(window, "objectReferenceDoubleClick", binaryIssues[1]);
            WaitUntil(() => window.SelectedItem?.Entry == binaryExport
                && binaryTree.SelectedItem is BinInterpNode node && node.Offset == binaryIssues[1].Offset);
            Assert.IsTrue(((TabItem)window.FindName("BinaryInterpreter_Tab")).IsSelected,
                "A superseded property-navigation callback must not change the active tab.");
        }
        finally
        {
            window.Close();
            Settings.PackageEditor_LiveFiltering = previousLiveFiltering;
            Settings.BinaryInterpreter_SkipAutoParseSizeCheck = previousAutoParse;
            settingsLoaded.SetValue(null, previousLoaded);
            LegendaryExplorerCoreLib.SetSynchronizationContext(previousScheduler);
            SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
        }
    }

    private static void Invoke(PackageEditorWindow window, string method, params object[] arguments) =>
        typeof(PackageEditorWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);

    private static System.Collections.Generic.IEnumerable<BinInterpNode> Flatten(BinInterpNode node)
    {
        yield return node;
        foreach (var child in node.Items.OfType<BinInterpNode>())
            foreach (var descendant in Flatten(child))
                yield return descendant;
    }

    private static void WaitUntil(Func<bool> predicate, Func<string> diagnostics = null)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate() && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            Pump(Task.Delay(30));
        }
        Assert.IsTrue(predicate(), "The requested reference field was not selected before the timeout. " + diagnostics?.Invoke());
    }

    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
}
