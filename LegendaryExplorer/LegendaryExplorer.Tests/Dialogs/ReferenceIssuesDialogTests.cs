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
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Dialogs;

[TestClass]
public class ReferenceIssuesDialogTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void ReferenceIssueActionsNavigateRefreshRepairAndCloseWithTheirPackage()
    {
        InitializeApplicationResources();
        ClickingAndEnterNavigateToTheActualIssue();
        RefreshMenuAndF5RescanTheOpenPackage();
        CleanupRefreshesTheEditorAndPreservesValidReferences();
        SwitchingPackagesClosesTheDialogAndRejectsStaleActions();
    }

    private static void ClickingAndEnterNavigateToTheActualIssue()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceIssueNavigation.pcc", MEGame.LE3);
        using var otherPackage = MEPackageHandler.CreateMemoryEmptyPackage("OtherPackage.pcc", MEGame.LE3);
        var first = new EntryStringPair(package.CreateExport("First", "SeqVar_Object", indexed: false), "First property issue");
        var second = new EntryStringPair(package.CreateExport("Second", "SeqVar_Object", indexed: false), "Second property issue");
        var withoutEntry = new EntryStringPair("Package-level issue");
        var foreign = new EntryStringPair(otherPackage.CreateExport("Foreign", "SeqVar_Object", indexed: false), "Foreign entry");
        var navigated = new List<EntryStringPair>();
        var dialog = new ReferenceIssuesDialog(null, package, [first, second, withoutEntry, foreign], navigated.Add, null);
        try
        {
            ShowOffscreen(dialog);
            var list = (ListView)dialog.FindName("IssuesList");
            list.SelectedItem = first;
            FlushBindings(dialog);

            var clickedText = Descendants((ListViewItem)list.ItemContainerGenerator.ContainerFromItem(second))
                .OfType<TextBlock>().Single(text => text.Text == second.Message);
            list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
                Source = clickedText
            });
            CollectionAssert.AreEqual(new[] { second }, navigated,
                "Clicking a row must navigate to that row, even when another row was selected.");

            list.SelectedItem = first;
            list.RaiseEvent(KeyArgs(dialog, Key.Enter, Keyboard.KeyDownEvent));
            CollectionAssert.AreEqual(new[] { second, first }, navigated);

            // Clicking list background must not reopen the last selected issue.
            list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
                Source = list
            });
            list.SelectedItem = withoutEntry;
            list.RaiseEvent(KeyArgs(dialog, Key.Enter, Keyboard.KeyDownEvent));
            list.SelectedItem = foreign;
            list.RaiseEvent(KeyArgs(dialog, Key.Enter, Keyboard.KeyDownEvent));
            Assert.HasCount(2, navigated, "Non-entry rows and entries from another package cannot navigate.");
        }
        finally
        {
            dialog.Close();
        }
    }

    private static void RefreshMenuAndF5RescanTheOpenPackage()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceIssueRefresh.pcc", MEGame.LE3);
        var export = package.CreateExport("ObjectVariable", "SeqVar_Object", indexed: false);
        export.WriteProperty(new ObjectProperty(900000, "ObjValue"));
        var dialog = new ReferenceIssuesDialog(null, package, [], null, null);
        try
        {
            ShowOffscreen(dialog);
            var originalCollection = dialog.Issues;
            var list = (ListView)dialog.FindName("IssuesList");
            var refresh = (MenuItem)dialog.FindName("RefreshMenuItem");
            refresh.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            FlushBindings(dialog);
            Assert.AreSame(originalCollection, dialog.Issues);
            Assert.AreSame(originalCollection, list.ItemsSource);
            Assert.IsTrue(dialog.Issues.Any(issue => ReferenceEquals(issue.Entry, export)));
            Assert.AreEqual(dialog.Issues.Count, list.Items.Count);

            export.WriteProperty(new ObjectProperty(0, "ObjValue"));
            var refreshKey = KeyArgs(dialog, Key.F5, Keyboard.PreviewKeyDownEvent);
            dialog.RaiseEvent(refreshKey);
            FlushBindings(dialog);
            Assert.IsTrue(refreshKey.Handled);
            Assert.HasCount(0, dialog.Issues);
            Assert.HasCount(0, list.Items);
            StringAssert.Contains(dialog.StatusText, "0 reference issues");
            Assert.AreEqual(Visibility.Visible, Descendants(dialog).OfType<TextBlock>()
                .Single(text => text.Text == "No reference issues found.").Visibility);

            export.WriteProperty(new ObjectProperty(-900000, "ObjValue"));
            refresh.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            FlushBindings(dialog);
            Assert.IsTrue(dialog.Issues.Any(issue => ReferenceEquals(issue.Entry, export)));
            Assert.AreEqual(Visibility.Collapsed, Descendants(dialog).OfType<TextBlock>()
                .Single(text => text.Text == "No reference issues found.").Visibility);
        }
        finally
        {
            dialog.Close();
        }
    }

    private static void CleanupRefreshesTheEditorAndPreservesValidReferences()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceIssueCleanup.pcc", MEGame.LE3);
        var target = package.CreateExport("Target", "SeqVar_Object", indexed: false);
        var invalidProperty = package.CreateExport("InvalidProperty", "SeqVar_Object", indexed: false);
        invalidProperty.WriteProperty(new ObjectProperty(900000, "ObjValue"));
        invalidProperty.WriteProperty(new StrProperty("Keep this caption", "ObjName"));
        var validProperty = package.CreateExport("ValidProperty", "SeqVar_Object", indexed: false);
        validProperty.WriteProperty(new ObjectProperty(target.UIndex, "ObjValue"));
        var invalidBinary = package.CreateExport("InvalidBinary", "ObjectRedirector", indexed: false);
        invalidBinary.WriteBinary(new ObjectRedirector { DestinationObject = -900000 });
        var validBinary = package.CreateExport("ValidBinary", "ObjectRedirector", indexed: false);
        validBinary.WriteBinary(ObjectRedirector.Create(target));
        var wrongType = package.CreateExport("WrongType", "TextureCube", indexed: false);
        var validTexture = package.CreateImport("Texture2D", "ValidTexture");
        wrongType.WriteProperties(new PropertyCollection
        {
            new ObjectProperty(target.UIndex, "FacePosX"),
            new ObjectProperty(validTexture.UIndex, "FaceNegX")
        });
        var validPropertyBefore = validProperty.Data;
        var validBinaryBefore = validBinary.Data;
        int refreshCount = 0;
        var dialog = new ReferenceIssuesDialog(null, package, [], null, () => refreshCount++);
        try
        {
            ShowOffscreen(dialog);
            dialog.RefreshIssues();
            Assert.IsTrue(dialog.Issues.Any(issue => ReferenceEquals(issue.Entry, invalidProperty)));
            Assert.IsTrue(dialog.Issues.Any(issue => ReferenceEquals(issue.Entry, invalidBinary)));
            Assert.IsTrue(dialog.Issues.Any(issue => ReferenceEquals(issue.Entry, wrongType)));

            var result = dialog.RemoveBadReferences();
            FlushBindings(dialog);
            Assert.AreEqual(2, result.RemovedPropertyCount);
            Assert.AreEqual(1, result.ClearedBinaryReferenceCount);
            Assert.HasCount(0, result.Failures);
            Assert.AreEqual(1, refreshCount);
            Assert.IsNull(invalidProperty.GetProperty<ObjectProperty>("ObjValue"));
            Assert.AreEqual("Keep this caption", invalidProperty.GetProperty<StrProperty>("ObjName").Value);
            Assert.AreEqual(0, invalidBinary.GetBinaryData<ObjectRedirector>().DestinationObject);
            Assert.IsNull(wrongType.GetProperty<ObjectProperty>("FacePosX"));
            Assert.AreEqual(validTexture.UIndex, wrongType.GetProperty<ObjectProperty>("FaceNegX").Value);
            CollectionAssert.AreEqual(validPropertyBefore, validProperty.Data);
            CollectionAssert.AreEqual(validBinaryBefore, validBinary.Data);
            Assert.HasCount(0, dialog.Issues, "The dialog must rescan after repairing the package.");
            StringAssert.Contains(dialog.StatusText, "0 issues remain");
            StringAssert.Contains(dialog.StatusText, "Changes are not saved");
        }
        finally
        {
            dialog.Close();
        }
    }

    private static void SwitchingPackagesClosesTheDialogAndRejectsStaleActions()
    {
        using var originalPackage = MEPackageHandler.CreateMemoryEmptyPackage("OriginalPackage.pcc", MEGame.LE3);
        using var nextPackage = MEPackageHandler.CreateMemoryEmptyPackage("NextPackage.pcc", MEGame.LE3);
        var export = originalPackage.CreateExport("BadReference", "SeqVar_Object", indexed: false);
        export.WriteProperty(new ObjectProperty(900000, "ObjValue"));
        var owner = new TestPackageOwner();
        owner.SetPackage(originalPackage);
        ShowOffscreen(owner);
        int refreshCount = 0;
        var dialog = new ReferenceIssuesDialog(owner, originalPackage, [], null, () => refreshCount++);
        bool closed = false;
        dialog.Closed += (_, _) => closed = true;
        try
        {
            ShowOffscreen(dialog);
            owner.SetPackage(nextPackage);
            Assert.IsTrue(closed, "An issue window must not remain actionable after its editor loads another package.");
            Assert.IsFalse(dialog.IsVisible);
            Assert.IsNull(dialog.RemoveBadReferences());
            Assert.AreEqual(0, refreshCount);
            Assert.AreEqual(900000, export.GetProperty<ObjectProperty>("ObjValue").Value);
        }
        finally
        {
            dialog.Close();
            owner.SetPackage(null);
            owner.Close();
        }
    }

    private sealed class TestPackageOwner() : WPFBase("Reference issues test", false)
    {
        public void SetPackage(IMEPackage package) => typeof(WPFBase).GetProperty(nameof(Pcc))!.SetValue(this, package);
        public override void HandleUpdate(List<PackageUpdate> updates) { }
    }

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
            .SetValue(null, typeof(ReferenceIssuesDialog).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
    }
}
