using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Dialogs;

[TestClass]
public class EntrySelectorFilteringTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void ClassFiltersAndPackageEditorSubtitlesFollowSelectorContents()
    {
        InitializeApplicationResources();
        ClassFilterCombinesWithSearchAndInitialFilter();
        GenericItemSelectorHidesClassFilter();
        SearchProviderRefreshesAndResetsClassChoices();
        SubtitlesReusePackageEditorMetadataWithoutChangingItsPreference();
    }

    private static void ClassFilterCombinesWithSearchAndInitialFilter()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("EntryClassFilters.pcc", MEGame.LE3);
        var alpha = package.CreateExport("Alpha", "SeqAct_Delay", indexed: false);
        var beta = package.CreateExport("Beta", "SeqAct_Delay", indexed: false);
        var target = package.CreateExport("Target", "SeqVar_Object", indexed: false);
        var imported = new ImportEntry(package)
        {
            ObjectName = "ImportedDelay", ClassName = "SeqAct_Delay", PackageFile = "Engine"
        };
        package.AddImport(imported);
        IEntry[] eligible = [alpha, beta, target, imported];
        using var selector = CreatePackageSelector(package, entry => eligible.Contains(entry), entry => entry != target);
        try
        {
            const string root = "[Package root]";
            Assert.AreEqual(Visibility.Visible, selector.ClassFilterVisibility);
            AssertClasses(selector, "All classes", "SeqAct_Delay");
            selector.ShowAllEntries = true;
            AssertClasses(selector, "All classes", "SeqAct_Delay", "SeqVar_Object");

            selector.SelectedClass = "seqact_delay";
            CollectionAssert.AreEquivalent(new object[] { root, imported, alpha, beta }, selector.FilteredEntriesList.ToArray());
            selector.SelectedEntryItem = beta;
            selector.SearchText = "Beta";
            CollectionAssert.AreEqual(new object[] { beta }, selector.FilteredEntriesList.ToArray());
            Assert.AreSame(beta, selector.SelectedEntryItem);
            AssertClasses(selector, "All classes", "SeqAct_Delay", "SeqVar_Object");
            selector.SearchText = string.Empty;
            Assert.AreSame(beta, selector.SelectedEntryItem,
                "Changing filters must retain a selection that is still visible.");

            selector.SelectedClass = "SeqVar_Object";
            CollectionAssert.AreEqual(new object[] { root, target }, selector.FilteredEntriesList.ToArray());
            Assert.AreEqual(root, selector.SelectedEntryItem,
                "An excluded selection must fall back to the first remaining option.");
            selector.SelectedEntryItem = target;
            selector.ShowAllEntries = false;
            AssertClasses(selector, "All classes", "SeqAct_Delay");
            Assert.AreEqual("All classes", selector.SelectedClass,
                "A class excluded by the initial filter must reset to All classes.");
            Assert.AreEqual(root, selector.SelectedEntryItem);
        }
        finally
        {
            selector.Close();
        }
    }

    private static void GenericItemSelectorHidesClassFilter()
    {
        using var selector = CreateItemSelector(["0 Null", "First item", "Second item"]);
        try
        {
            Assert.AreEqual(Visibility.Collapsed, selector.ClassFilterVisibility);
            selector.SearchText = "Second";
            CollectionAssert.AreEqual(new object[] { "Second item" }, selector.FilteredEntriesList.ToArray());
        }
        finally
        {
            selector.Close();
        }
    }

    private static void SearchProviderRefreshesAndResetsClassChoices()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("EntrySearchClasses.pcc", MEGame.LE3);
        var action = package.CreateExport("ActionResult", "SeqAct_Delay", indexed: false);
        var variable = package.CreateExport("VariableResult", "SeqVar_Object", indexed: false);
        Func<string, IEnumerable<object>> provider = query => query == "actions" ? [action] : [variable];
        using var selector = CreateSelector([typeof(Window), typeof(Func<string, IEnumerable<object>>), typeof(string), typeof(string), typeof(string)],
            [null, provider, null, null, null]);
        try
        {
            selector.SearchText = "actions";
            InvokePrivate(selector, "RunItemSearch");
            AssertClasses(selector, "All classes", "SeqAct_Delay");
            selector.SelectedClass = "SeqAct_Delay";
            selector.SearchText = "variables";
            InvokePrivate(selector, "RunItemSearch");
            AssertClasses(selector, "All classes", "SeqVar_Object");
            Assert.AreEqual("All classes", selector.SelectedClass);
            Assert.AreSame(variable, selector.SelectedEntryItem);

            selector.ItemFilterText = "does not match";
            Assert.IsEmpty(selector.FilteredEntriesList);
            AssertClasses(selector, "All classes", "SeqVar_Object");
            selector.SearchText = string.Empty;
            InvokePrivate(selector, "RunItemSearch");
            AssertClasses(selector, "All classes");
            Assert.AreEqual(Visibility.Collapsed, selector.ClassFilterVisibility);
            Assert.IsNull(selector.SelectedEntryItem);
        }
        finally
        {
            selector.Close();
        }
    }

    private static void SubtitlesReusePackageEditorMetadataWithoutChangingItsPreference()
    {
        bool previousSetting = Settings.PackageEditor_ShowTreeEntrySubText;
        try
        {
            Settings.PackageEditor_ShowTreeEntrySubText = false;
            using var package = MEPackageHandler.CreateMemoryEmptyPackage("EntrySubtitles.pcc", MEGame.LE3);
            var action = package.CreateExport("DelayAction", "SeqAct_Delay", indexed: false);
            action.WriteProperty(new StrProperty("Wait for the shuttle", "ObjName"));
            var tagged = package.CreateExport("ActorReference", "SeqVar_Object", indexed: false);
            tagged.WriteProperty(new NameProperty("shuttle_arrival_target", "Tag"));
            using var selector = CreateItemSelector([action, tagged, "0 Null"]);
            try
            {
                using var packageEditorAction = new TreeViewEntry(action, alwaysShowSubText: true);
                using var packageEditorTagged = new TreeViewEntry(tagged, alwaysShowSubText: true);
                TreeViewEntry actionSubtitle = selector.GetEntrySubtitle(action);
                TreeViewEntry taggedSubtitle = selector.GetEntrySubtitle(tagged);
                Assert.AreEqual("Wait for the shuttle", actionSubtitle.SubText);
                Assert.AreEqual("shuttle_arrival_target", taggedSubtitle.SubText);
                Assert.AreEqual(packageEditorAction.SubText, actionSubtitle.SubText);
                Assert.AreEqual(packageEditorTagged.SubText, taggedSubtitle.SubText);
                Assert.AreSame(actionSubtitle, selector.GetEntrySubtitle(action), "Rows and search must share cached metadata.");
                Assert.IsNull(selector.GetEntrySubtitle("0 Null"));
                Assert.IsFalse(Settings.PackageEditor_ShowTreeEntrySubText);

                selector.WindowStartupLocation = WindowStartupLocation.Manual;
                selector.ShowActivated = selector.ShowInTaskbar = false;
                selector.Left = selector.Top = -10000;
                selector.Show();
                FlushBindings(selector);
                AssertRenderedSubtitle(selector, action, "Wait for the shuttle");
                AssertRenderedSubtitle(selector, tagged, "shuttle_arrival_target");
                var classes = (ComboBox)selector.FindName("ClassFilterComboBox");
                classes.SelectedItem = "SeqVar_Object";
                FlushBindings(selector);
                Assert.AreEqual("SeqVar_Object", selector.SelectedClass);
                var entries = (ListView)selector.FindName("EntrySelectorListView");
                CollectionAssert.AreEqual(new object[] { tagged, "0 Null" }, entries.Items.Cast<object>().ToArray());
                classes.SelectedItem = "All classes";
                FlushBindings(selector);

                selector.SearchText = "shuttle_arrival_target";
                CollectionAssert.AreEqual(new object[] { tagged }, selector.FilteredEntriesList.ToArray());
                selector.SearchText = "Wait for the shuttle";
                CollectionAssert.AreEqual(new object[] { action }, selector.FilteredEntriesList.ToArray());
                selector.SearchText = string.Empty;
                FlushBindings(selector);
                tagged.WriteProperty(new NameProperty("updated_target", "Tag"));
                Assert.AreEqual("updated_target", taggedSubtitle.SubText,
                    "Entry changes must refresh metadata even when Package Editor subtitles are disabled.");
                FlushBindings(selector);
                AssertRenderedSubtitle(selector, tagged, "updated_target");
                selector.Dispose();
                Assert.IsNull(actionSubtitle.Entry, "Disposal must detach cached entry listeners.");
                Assert.IsNull(taggedSubtitle.Entry);
            }
            finally
            {
                selector.Close();
            }
        }
        finally
        {
            Settings.PackageEditor_ShowTreeEntrySubText = previousSetting;
        }
    }

    private static EntrySelector CreatePackageSelector(IMEPackage package, Predicate<IEntry> eligible, Predicate<IEntry> initial) =>
        CreateSelector([typeof(Window), typeof(IMEPackage), typeof(EntrySelector.SupportedTypes), typeof(string),
            typeof(Predicate<IEntry>), typeof(bool), typeof(string), typeof(string), typeof(Predicate<IEntry>),
            typeof(string), typeof(ExportEntry), typeof(bool), typeof(bool)],
            [null, package, EntrySelector.SupportedTypes.ExportsAndImports, null, eligible, true,
                "[Package root]", null, initial, null, null, false, false]);

    private static EntrySelector CreateItemSelector(IEnumerable<object> items) =>
        CreateSelector([typeof(Window), typeof(IEnumerable<object>), typeof(string), typeof(string)], [null, items, null, null]);

    private static EntrySelector CreateSelector(Type[] parameters, object[] arguments)
    {
        var constructor = typeof(EntrySelector).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, parameters, modifiers: null);
        Assert.IsNotNull(constructor);
        return (EntrySelector)constructor.Invoke(arguments);
    }

    private static object InvokePrivate(EntrySelector selector, string method, params object[] arguments) =>
        typeof(EntrySelector).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(selector, arguments);

    private static void AssertClasses(EntrySelector selector, params string[] expected) =>
        CollectionAssert.AreEqual(expected, selector.AvailableClasses.ToArray());

    private static void FlushBindings(EntrySelector selector)
    {
        selector.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        selector.UpdateLayout();
        selector.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    private static void AssertRenderedSubtitle(EntrySelector selector, object item, string expected)
    {
        var list = (ListView)selector.FindName("EntrySelectorListView");
        var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(item);
        Assert.IsNotNull(row, "The list must generate a visible row for the entry.");
        var subtitle = VisualDescendants(row).OfType<TextBlock>()
            .SingleOrDefault(text => ReferenceEquals(text.DataContext, selector.GetEntrySubtitle(item)));
        Assert.IsNotNull(subtitle, "The rendered row must bind to its cached Package Editor metadata.");
        Assert.AreEqual(expected, subtitle.Text);
        Assert.AreEqual(Visibility.Visible, subtitle.Visibility);
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject descendant in VisualDescendants(child))
                yield return descendant;
        }
    }

    private static void InitializeApplicationResources()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(EntrySelector).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
    }
}
