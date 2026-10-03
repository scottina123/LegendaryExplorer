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
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.TLK;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class TLKEditorInteractionTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void StringContextActionsKeepTheClickedRowAndSearchSelection()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(TLKEditorExportLoader).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));

        AssertCommandsUseTheSuppliedObject();
        AssertRowMenusAndSearchKeepTheirTargets();
        TLKEditorSearchTests.AssertToolbarSearchScopesMatchesAndEnterRepeatsTheLastMode();
        TLKEditorTabTests.AssertWindowedTabSelectionKeepsPositionsAndDragReorderingStillWorks();
    }

    private static void AssertCommandsUseTheSuppliedObject()
    {
        using var editor = new TLKEditorExportLoader();
        var first = new TLKStringRef(100, "First line");
        var sameValue = new TLKStringRef(200, "Same line");
        var clicked = new TLKStringRef(200, "Same line");
        editor.LoadedStrings = [first, sameValue, clicked];
        editor.CleanedStrings.AddRange(editor.LoadedStrings);
        var grid = (DataGrid)editor.FindName("DisplayedString_ListBox");
        grid.CurrentCell = new DataGridCellInfo(first, grid.Columns[0]);
        grid.SelectedItem = first;

        Assert.IsTrue(editor.SetIDCommand.CanExecute(clicked));
        Assert.IsTrue(editor.DeleteStringCommand.CanExecute(clicked));
        Assert.IsFalse(editor.SetIDCommand.CanExecute(new TLKStringRef(200, "Same line")),
            "A stale or detached row must not become a context-menu target through value equality.");
        editor.DeleteStringCommand.Execute(clicked);

        Assert.HasCount(2, editor.LoadedStrings);
        Assert.HasCount(2, editor.CleanedStrings);
        Assert.AreSame(first, editor.LoadedStrings[0]);
        Assert.AreSame(sameValue, editor.LoadedStrings[1]);
        Assert.AreSame(sameValue, editor.CleanedStrings[1]);
        Assert.IsFalse(editor.DeleteStringCommand.CanExecute(clicked));
    }

    private static void AssertRowMenusAndSearchKeepTheirTargets()
    {
        using var editor = new TLKEditorExportLoader { CurrentLoadedFile = "ContextMenuTest_INT.tlk" };
        var first = new TLKStringRef(100, "alpha first line");
        var clicked = new TLKStringRef(200, "alpha second line");
        editor.LoadedStrings = [first, clicked];
        editor.CleanedStrings.AddRange(editor.LoadedStrings);
        var grid = (DataGrid)editor.FindName("DisplayedString_ListBox");
        var window = new Window
        {
            Content = editor, Width = 1000, Height = 600,
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000
        };
        try
        {
            window.Show();
            FlushDispatcher();
            grid.UpdateLayout();
            var clickedRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(clicked);
            var clickedText = FindVisualDescendants<TextBox>(clickedRow)
                .Single(textBox => Equals(textBox.Tag, "TextColumnEditor"));
            grid.CurrentCell = new DataGridCellInfo(first, grid.Columns[0]);
            grid.SelectedItem = first;
            clickedText.Select(6, 6);
            clickedText.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });

            Assert.AreSame(clicked, grid.SelectedItem, "Right-clicking an unselected string must select that row.");
            Assert.AreSame(grid.Columns[1], grid.CurrentCell.Column);
            Assert.AreEqual(6, clickedText.SelectionStart);
            Assert.AreEqual(6, clickedText.SelectionLength, "Selecting the row must preserve inline text selection.");

            grid.CurrentCell = new DataGridCellInfo(first, grid.Columns[1]);
            grid.SelectedItem = first;
            editor.FindText = "alpha";
            editor.FindNextCommand.Execute(null);
            Assert.AreSame(first, grid.CurrentCell.Item);
            editor.FindNextCommand.Execute(null);
            Assert.AreSame(clicked, grid.CurrentCell.Item, "Find Next must advance beyond the selected match.");
            editor.ReplaceText = "changed";
            editor.ReplaceCommand.Execute(null);
            Assert.AreEqual("alpha first line", first.Data);
            Assert.AreEqual("changed second line", clicked.Data);

            // The menu remains associated with the string even if grid selection changes before the action runs.
            grid.CurrentCell = new DataGridCellInfo(first, grid.Columns[0]);
            grid.SelectedItem = first;
            clickedText.ContextMenu.PlacementTarget = clickedText;
            MenuItem delete = clickedText.ContextMenu.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "Delete String"));
            delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.HasCount(1, editor.LoadedStrings);
            Assert.AreSame(first, editor.LoadedStrings[0]);
        }
        finally
        {
            window.Close();
        }
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (T descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void FlushDispatcher() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
