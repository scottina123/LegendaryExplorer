using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.SharedUI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Dialogs;

[TestClass]
public class EntrySelectorItemPreviewTests
{
    [STATestMethod]
    public void ItemPreviewFollowsSelectionAndFilteringAndDetachesOnDisposal()
    {
        InitializeApplicationResources();
        ItemPreviewFollowsInitialSelectionAndFilteringAndDetachesOnDisposal();
        EmptyItemListInitializesPreviewWithNoSelection();
        PreviewOnlyBrowseKeepsDoubleClickAndEnterInsideThePreview();
    }

    private static void ItemPreviewFollowsInitialSelectionAndFilteringAndDetachesOnDisposal()
    {
        using var selector = CreateSelector(["Walk", "Run", "Idle"]);
        try
        {
            // GetItem establishes the requested default before attaching the preview callback.
            typeof(EntrySelector).GetMethod("SetInitialSelection", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(selector, ["Run", false]);
            var preview = new TextBlock { Text = "Animation preview" };
            var selections = new List<string>();
            selector.ConfigureItemPreview<string>(preview, selections.Add);

            var host = (ContentControl)selector.FindName("PreviewHost");
            Assert.AreSame(preview, host.Content);
            Assert.AreEqual(Visibility.Visible, host.Visibility);
            Assert.AreEqual(Visibility.Visible, ((GridSplitter)selector.FindName("PreviewSplitter")).Visibility);
            Assert.IsGreaterThan(0d, ((ColumnDefinition)selector.FindName("PreviewColumn")).Width.Value);
            Assert.AreEqual(5d, ((ColumnDefinition)selector.FindName("PreviewSplitterColumn")).Width.Value);
            CollectionAssert.AreEqual(new[] { "Run" }, selections,
                "The final initial selection must be previewed once, without a transient first-item preview.");

            selector.SelectedEntryItem = "Walk";
            selector.SelectedEntryItem = "Walk";
            CollectionAssert.AreEqual(new[] { "Run", "Walk" }, selections,
                "Reassigning the same selection must not reload the preview.");

            selector.SearchText = "Idle";
            Assert.AreEqual("Idle", selector.SelectedEntryItem);
            Assert.AreEqual("Idle", selections[^1]);
            selector.SearchText = "Missing animation";
            Assert.IsNull(selector.SelectedEntryItem);
            Assert.IsNull(selections[^1], "An empty filtered list must clear the preview.");
            selector.SearchText = string.Empty;
            Assert.AreEqual("Walk", selector.SelectedEntryItem);
            Assert.AreEqual("Walk", selections[^1]);

            int notificationsBeforeDisposal = selections.Count;
            selector.Dispose();
            Assert.IsNull(host.Content, "The selector must detach the caller-owned preview from its host.");
            selector.SelectedEntryItem = "Run";
            Assert.AreEqual(notificationsBeforeDisposal, selections.Count,
                "A closed selector must release its preview callback.");
        }
        finally
        {
            selector.Close();
        }
    }

    private static void EmptyItemListInitializesPreviewWithNoSelection()
    {
        using var selector = CreateSelector([]);
        try
        {
            var selections = new List<string>();
            selector.ConfigureItemPreview<string>(new TextBlock(), selections.Add);
            Assert.AreEqual(1, selections.Count);
            Assert.IsNull(selections[0]);
            Assert.AreEqual(Visibility.Visible, ((ContentControl)selector.FindName("PreviewHost")).Visibility);
            Assert.IsFalse(selector.OKCommand.CanExecute(null), "Normal selectors still require a selection to accept.");
            selector.ConfigureItemPreview<string>(new TextBlock(), selections.Add, previewOnly: true);
            Assert.IsTrue(selector.OKCommand.CanExecute(null), "A preview-only browser can always be closed.");
        }
        finally
        {
            selector.Close();
        }
    }

    private static void PreviewOnlyBrowseKeepsDoubleClickAndEnterInsideThePreview()
    {
        using var selector = CreateSelector(["Walk", "Run"]);
        try
        {
            var list = (ListView)selector.FindName("EntrySelectorListView");
            list.Measure(new Size(400, 200));
            list.Arrange(new Rect(0, 0, 400, 200));
            list.UpdateLayout();
            var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem("Walk");
            Assert.IsNotNull(row, "The double-click regression test needs a real generated list row.");
            Assert.AreSame(row, ItemsControl.ContainerFromElement(list, row));
            var acceptButton = (Button)selector.FindName("AcceptButton");
            var cancelButton = (Button)selector.FindName("CancelButton");
            Assert.AreEqual("OK", acceptButton.Content);
            Assert.AreEqual(Visibility.Visible, cancelButton.Visibility);

            int accepted = 0;
            var closeCommand = selector.OKCommand;
            selector.OKCommand = new GenericCommand(() => accepted++, () => true);
            list.RaiseEvent(DoubleClickArgs(row));
            Assert.AreEqual(1, accepted, "Normal selectors must still accept a double-click on a valid row.");

            var selections = new List<string>();
            selector.ConfigureItemPreview<string>(new TextBlock(), selections.Add, previewOnly: true);
            Assert.AreEqual("Preview", selector.Title);
            Assert.AreEqual("Close", acceptButton.Content);
            Assert.AreEqual(Visibility.Collapsed, cancelButton.Visibility);
            var doubleClick = DoubleClickArgs(row);
            list.RaiseEvent(doubleClick);
            Assert.IsTrue(doubleClick.Handled);
            Assert.AreEqual(1, accepted, "Double-click must leave a preview-only browser open.");
            Assert.AreEqual("Walk", selector.SelectedEntryItem);
            CollectionAssert.AreEqual(new[] { "Walk" }, selections,
                "Double-clicking the current animation must preserve its active preview.");

            var tree = (TreeView)selector.FindName("EntrySelectorTreeView");
            var treeDoubleClick = DoubleClickArgs(row);
            tree.RaiseEvent(treeDoubleClick);
            Assert.IsTrue(treeDoubleClick.Handled);
            Assert.AreEqual(1, accepted);

            var presentationSource = new TestPresentationSource();
            foreach (string handler in new[] { "EntrySelectorListView_KeyDown", "EntrySelectorTreeView_KeyDown",
                         "EntrySearchTextBox_KeyDown", "EntryFilterTextBox_KeyDown" })
            {
                var enter = new KeyEventArgs(Keyboard.PrimaryDevice, presentationSource, Environment.TickCount, Key.Enter)
                {
                    RoutedEvent = Keyboard.KeyDownEvent,
                };
                typeof(EntrySelector).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(selector, [list, enter]);
                Assert.IsTrue(enter.Handled, $"{handler} must consume Enter in preview-only mode.");
                Assert.AreEqual(1, accepted, "Enter must not accept or close the animation preview.");
            }
            selector.SelectedEntryItem = "Run";
            CollectionAssert.AreEqual(new[] { "Walk", "Run" }, selections,
                "Single selection changes must continue to update the live preview.");
            selector.OKCommand = closeCommand;
        }
        finally
        {
            selector.Close();
        }
    }

    private static MouseButtonEventArgs DoubleClickArgs(DependencyObject source) =>
        new(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Control.MouseDoubleClickEvent,
            Source = source,
        };

    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null;
    }

    private static EntrySelector CreateSelector(IEnumerable<object> items)
    {
        var constructor = typeof(EntrySelector).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, [typeof(Window), typeof(IEnumerable<object>), typeof(string), typeof(string)], modifiers: null);
        Assert.IsNotNull(constructor);
        return (EntrySelector)constructor.Invoke([null, items, null, null]);
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
