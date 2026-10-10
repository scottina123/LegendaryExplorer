using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Dialogs;

[TestClass]
public class DuplicateIssuesDialogTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void DuplicateIssueActionsNavigateRefreshRepairAndCloseWithTheirPackage()
    {
        InitializeApplicationResources();
        ClickAndEnterNavigateToTheRequestedEntry();
        RefreshMenuAndF5ReflectChangedNamesAndIndices();
        FixAllRescansNestedAndImportedDuplicatesWithoutChangingOtherData();
        IncompleteChecksStayRefreshableWithoutChangingThePackage();
        PackageSwitchClosesTheDialogAndRejectsStaleRepairs();
    }

    private static void ClickAndEnterNavigateToTheRequestedEntry()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("DuplicateNavigation.pcc", MEGame.LE3);
        using var otherPackage = MEPackageHandler.CreateMemoryEmptyPackage("OtherPackage.pcc", MEGame.LE3);
        var first = new EntryStringPair(package.CreateExport("First", "Object", indexed: false), "First duplicate");
        var second = new EntryStringPair(package.CreateExport("Second", "Object", indexed: false), "Second duplicate");
        var withoutEntry = new EntryStringPair("Package-level information");
        var foreign = new EntryStringPair(otherPackage.CreateExport("Foreign", "Object", indexed: false), "Foreign duplicate");
        var navigated = new List<EntryStringPair>();
        var dialog = new DuplicateIssuesDialog(null, package, [first, second, withoutEntry, foreign], navigated.Add, null);
        try
        {
            ShowOffscreen(dialog);
            var list = (ListView)dialog.FindName("IssuesList");
            list.SelectedItem = first;
            FlushBindings(dialog);
            var clickedText = Descendants((ListViewItem)list.ItemContainerGenerator.ContainerFromItem(second))
                .OfType<TextBlock>().Single(text => text.Text == second.Message);
            list.RaiseEvent(ClickArgs(clickedText));
            CollectionAssert.AreEqual(new[] { second }, navigated,
                "A row click must navigate to that entry even while a different entry is selected.");

            list.SelectedItem = first;
            list.RaiseEvent(KeyArgs(dialog, Key.Enter, Keyboard.KeyDownEvent));
            CollectionAssert.AreEqual(new[] { second, first }, navigated);

            list.RaiseEvent(ClickArgs(list));
            list.SelectedItem = withoutEntry;
            list.RaiseEvent(KeyArgs(dialog, Key.Enter, Keyboard.KeyDownEvent));
            list.SelectedItem = foreign;
            list.RaiseEvent(KeyArgs(dialog, Key.Enter, Keyboard.KeyDownEvent));
            Assert.HasCount(2, navigated, "Background clicks, information rows and foreign entries must not navigate.");
        }
        finally
        {
            dialog.Close();
        }
    }

    private static void RefreshMenuAndF5ReflectChangedNamesAndIndices()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("DuplicateRefresh.pcc", MEGame.LE3);
        var first = package.CreateExport("Twin", "Object", indexed: false);
        var second = package.CreateExport("Twin", "Object", indexed: false);
        var dialog = new DuplicateIssuesDialog(null, package, [], null, null);
        try
        {
            ShowOffscreen(dialog);
            var originalCollection = dialog.Issues;
            var list = (ListView)dialog.FindName("IssuesList");
            var refresh = (MenuItem)dialog.FindName("RefreshMenuItem");
            Assert.IsNotNull(dialog.FindName("FixAllMenuItem"));

            refresh.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            FlushBindings(dialog);
            Assert.AreSame(originalCollection, dialog.Issues);
            Assert.AreSame(originalCollection, list.ItemsSource);
            Assert.HasCount(1, dialog.Issues);
            Assert.AreSame(second, dialog.Issues.Single().Entry);
            Assert.HasCount(1, list.Items);

            second.ObjectName = new NameReference("Twin", 1);
            var key = KeyArgs(dialog, Key.F5, Keyboard.PreviewKeyDownEvent);
            dialog.RaiseEvent(key);
            FlushBindings(dialog);
            Assert.IsTrue(key.Handled);
            Assert.HasCount(0, dialog.Issues);
            Assert.HasCount(0, list.Items);

            first.ObjectName = "Renamed";
            second.ObjectName = "Renamed";
            refresh.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            FlushBindings(dialog);
            Assert.HasCount(1, dialog.Issues);
            Assert.AreSame(second, dialog.Issues.Single().Entry);
            StringAssert.Contains(dialog.Issues.Single().Message, "Renamed");
        }
        finally
        {
            dialog.Close();
        }
    }

    private static void FixAllRescansNestedAndImportedDuplicatesWithoutChangingOtherData()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("DuplicateRepair.pcc", MEGame.LE3);
        var firstParent = package.CreateExport("Group", "Package", indexed: false);
        var secondParent = package.CreateExport("Group", "Package", indexed: false);
        var firstChild = package.CreateExport("Child", "Object", firstParent, indexed: false);
        var secondChild = package.CreateExport("Child", "Object", secondParent, indexed: false);
        var sibling1 = package.CreateExport("Twin", "Object", firstParent, indexed: false);
        var sibling2 = package.CreateExport("Twin", "Object", firstParent, indexed: false);
        var sibling3 = package.CreateExport("Twin", "Object", firstParent, indexed: false);
        var reservedSibling = package.CreateExport("Twin", "Object", firstParent, indexed: false);
        reservedSibling.ObjectName = new NameReference("Twin", 1);
        var unaffected = package.CreateExport("Unrelated", "Object", indexed: false);
        unaffected.WriteProperty(new ObjectProperty(secondChild.UIndex, "ReferenceToRenamedParentChild"));
        unaffected.WriteProperty(new IntProperty(12345, "Value"));
        var imported1 = new ImportEntry(package) { ObjectName = "ImportedTwin", ClassName = "Object", PackageFile = "Core" };
        var imported2 = new ImportEntry(package) { ObjectName = "ImportedTwin", ClassName = "Object", PackageFile = "Core" };
        package.AddImport(imported1);
        package.AddImport(imported2);
        IEntry[] entries = package.Exports.Cast<IEntry>().Concat(package.Imports).ToArray();
        var originalNames = entries.Select(entry => entry.ObjectName).ToArray();
        var originalIndexes = entries.Select(entry => entry.UIndex).ToArray();
        var originalParents = entries.Select(entry => entry.idxLink).ToArray();
        var originalData = package.Exports.ToDictionary(export => export, export => export.Data);
        int refreshCount = 0;
        var dialog = new DuplicateIssuesDialog(null, package, EntryChecker.CheckForDuplicateIndices(package), null, () => refreshCount++);
        try
        {
            ShowOffscreen(dialog);
            Assert.IsTrue(dialog.Issues.Count >= 5, "The fixture must include parent, child, sibling and import duplicates.");
            int changed = dialog.FixDuplicateIssues();
            FlushBindings(dialog);

            Assert.IsTrue(changed > 0);
            Assert.AreEqual(entries.Where((entry, index) => entry.ObjectName != originalNames[index]).Count(), changed);
            Assert.HasCount(0, EntryChecker.CheckForDuplicateIndices(package));
            Assert.HasCount(0, dialog.Issues);
            Assert.HasCount(0, ((ListView)dialog.FindName("IssuesList")).Items);
            Assert.AreEqual(1, refreshCount);
            StringAssert.Contains(dialog.StatusText, "Changes are not saved.");
            Assert.AreEqual(new NameReference("Group"), firstParent.ObjectName);
            Assert.AreEqual(new NameReference("Child"), firstChild.ObjectName);
            Assert.AreEqual(new NameReference("Child"), secondChild.ObjectName,
                "Repairing the duplicate parent path should resolve its child's conflict without renaming the child.");
            Assert.AreEqual(new NameReference("Twin"), sibling1.ObjectName);
            Assert.AreEqual(new NameReference("Twin", 1), reservedSibling.ObjectName,
                "A pre-existing unique instance number must not be reused or renumbered.");
            Assert.AreEqual(new NameReference("Unrelated"), unaffected.ObjectName);
            Assert.AreEqual(new NameReference("ImportedTwin"), imported1.ObjectName);
            CollectionAssert.AreEqual(originalIndexes, entries.Select(entry => entry.UIndex).ToArray());
            CollectionAssert.AreEqual(originalParents, entries.Select(entry => entry.idxLink).ToArray());
            foreach (var (export, data) in originalData)
                CollectionAssert.AreEqual(data, export.Data, "Fixing names must preserve export data and all object references.");

            var repairedNames = entries.Select(entry => entry.ObjectName).ToArray();
            Assert.AreEqual(0, dialog.FixDuplicateIssues());
            CollectionAssert.AreEqual(repairedNames, entries.Select(entry => entry.ObjectName).ToArray());
        }
        finally
        {
            dialog.Close();
        }
    }

    private static void IncompleteChecksStayRefreshableWithoutChangingThePackage()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CyclicDuplicates.pcc", MEGame.LE3);
        var first = package.CreateExport("First", "Object", indexed: false);
        var second = package.CreateExport("Second", "Object", indexed: false);
        first.idxLink = second.UIndex;
        second.idxLink = first.UIndex;
        byte[] firstHeader = first.Header;
        byte[] secondHeader = second.Header;
        var dialog = new DuplicateIssuesDialog(null, package, [], null, null);
        try
        {
            ShowOffscreen(dialog);
            ((MenuItem)dialog.FindName("RefreshMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.HasCount(1, dialog.Issues);
            Assert.IsNull(dialog.Issues.Single().Entry);
            StringAssert.Contains(dialog.Issues.Single().Message, "Duplicate index checking could not finish");
            StringAssert.Contains(dialog.StatusText, "Could not check duplicate indexes");

            var key = KeyArgs(dialog, Key.F5, Keyboard.PreviewKeyDownEvent);
            dialog.RaiseEvent(key);
            Assert.IsTrue(key.Handled);
            Assert.HasCount(1, dialog.Issues);
            CollectionAssert.AreEqual(firstHeader, first.Header);
            CollectionAssert.AreEqual(secondHeader, second.Header);
        }
        finally
        {
            dialog.Close();
        }
    }

    private static void PackageSwitchClosesTheDialogAndRejectsStaleRepairs()
    {
        using var originalPackage = MEPackageHandler.CreateMemoryEmptyPackage("OriginalDuplicates.pcc", MEGame.LE3);
        using var nextPackage = MEPackageHandler.CreateMemoryEmptyPackage("NextPackage.pcc", MEGame.LE3);
        var first = originalPackage.CreateExport("Twin", "Object", indexed: false);
        var second = originalPackage.CreateExport("Twin", "Object", indexed: false);
        var owner = new TestPackageOwner();
        owner.SetPackage(originalPackage);
        ShowOffscreen(owner);
        int refreshCount = 0;
        var dialog = new DuplicateIssuesDialog(owner, originalPackage, EntryChecker.CheckForDuplicateIndices(originalPackage), null, () => refreshCount++);
        bool closed = false;
        dialog.Closed += (_, _) => closed = true;
        try
        {
            ShowOffscreen(dialog);
            owner.SetPackage(nextPackage);
            Assert.IsTrue(closed);
            Assert.IsFalse(dialog.IsVisible);
            Assert.AreEqual(0, dialog.FixDuplicateIssues());
            Assert.AreEqual(0, refreshCount);
            Assert.AreEqual(first.ObjectName, second.ObjectName, "A stale dialog must not mutate the old package.");
            second.ObjectName = "ManuallyChanged";
            dialog.RefreshIssues();
            Assert.HasCount(1, dialog.Issues, "A stale refresh must not silently reload the previous package.");
        }
        finally
        {
            dialog.Close();
            owner.SetPackage(null);
            owner.Close();
        }
    }

    private sealed class TestPackageOwner() : WPFBase("Duplicate issues test", false)
    {
        public void SetPackage(IMEPackage package) => typeof(WPFBase).GetProperty(nameof(Pcc))!.SetValue(this, package);
        public override void HandleUpdate(List<PackageUpdate> updates) { }
    }

    private static MouseButtonEventArgs ClickArgs(DependencyObject source) =>
        new(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
            Source = source
        };

    private static KeyEventArgs KeyArgs(Window window, Key key, RoutedEvent routedEvent) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key) { RoutedEvent = routedEvent };

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static void ShowOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowActivated = window.ShowInTaskbar = false;
        window.Left = window.Top = -10000;
        window.Show();
        FlushBindings(window);
    }

    private static void FlushBindings(Window window)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void InitializeApplicationResources()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(DuplicateIssuesDialog).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
    }
}
