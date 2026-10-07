using System;
using System.Collections.Generic;
using System.IO;
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
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.TLK.ME2ME3;
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
        AssertSaveAllTlksRefreshesTheLastSavedTimeWithoutNotifications();
    }

    private static void AssertSaveAllTlksRefreshesTheLastSavedTimeWithoutNotifications()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"TLKEditorSaveAll_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var editor = new TLKEditorExportLoader();
        ExportLoaderHostedWindow window = null;
        try
        {
            string activeFile = Path.Combine(directory, "Test_INT.tlk");
            string siblingFile = Path.Combine(directory, "Test_FRA.tlk");
            HuffmanCompression.SaveToTlkFile(activeFile, [new TLKStringRef(100, "Active line")]);
            HuffmanCompression.SaveToTlkFile(siblingFile, [new TLKStringRef(100, "Original sibling line")]);

            // Load while embedded, and keep the hosted test from changing recents or persisted tabs.
            editor.LoadFile(siblingFile);
            editor.LoadFile(activeFile);
            typeof(TLKEditorExportLoader).GetField("_suppressTabPersistence", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(editor, true);
            typeof(TLKEditorExportLoader).GetField("_restoredPersistedTabs", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(editor, true);
            // Keep the host's recents disabled while testing the real editor and status binding.
            var fileLoader = new FileLoaderWithoutRecents { LoadedFile = activeFile };
            window = new ExportLoaderHostedWindow(fileLoader)
            {
                Width = 1000, Height = 600,
                ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000
            };
            window.ContentGrid.Children.Remove(fileLoader);
            editor.PoppedOut(window);
            window.ContentGrid.Children.Add(editor);
            window.Show();
            FlushDispatcher();

            var status = (TextBlock)window.FindName("StatusBar_RightSide_LastSaved");
            MenuItem saveAll = window.MainMenu.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "Save All TLKs"));
            var existingWindows = Application.Current.Windows.OfType<Window>().ToHashSet();
            var notifications = new List<string>();
            var notificationWatch = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(10)
            };
            notificationWatch.Tick += (_, _) =>
            {
                // A reintroduced modal must fail this test instead of leaving the test runner blocked.
                foreach (Window notification in Application.Current.Windows.OfType<Window>()
                             .Where(candidate => !existingWindows.Contains(candidate)).ToArray())
                {
                    notifications.Add(notification.Title);
                    notification.Close();
                }
            };
            notificationWatch.Start();
            try
            {
                for (int save = 0; save < 2; save++)
                {
                    Assert.IsFalse(editor.FileModified, "The timestamp must refresh even when the active TLK is already unmodified.");
                    File.SetLastWriteTime(activeFile, new DateTime(2001 + save, 1, 2, 3, 4, 5));
                    status.GetBindingExpression(TextBlock.TextProperty)!.UpdateTarget();
                    string previousText = status.Text;
                    Assert.IsTrue(saveAll.Command.CanExecute(null));
                    saveAll.Command.Execute(null);
                    FlushDispatcher();

                    Assert.AreEqual($"Last saved at {File.GetLastWriteTime(activeFile):G}", status.Text,
                        "Save All TLKs must refresh the bottom-right bound timestamp after every successful save.");
                    Assert.AreNotEqual(previousText, status.Text);
                    Assert.IsEmpty(notifications, "Successful Save All TLKs must complete without a notification dialog.");
                    foreach (string file in new[] { activeFile, siblingFile })
                    {
                        Assert.AreEqual("Active line", new ME2ME3TalkFile(file).StringRefs.Single(line => line.StringID == 100).Data);
                    }
                    Assert.IsTrue(editor.OpenTabs.All(tab => !tab.IsModified));
                }
            }
            finally
            {
                notificationWatch.Stop();
            }
        }
        finally
        {
            window?.Close();
            File.Delete(Path.Combine(directory, "Test_INT.tlk"));
            File.Delete(Path.Combine(directory, "Test_FRA.tlk"));
            Directory.Delete(directory);
        }
    }

    private sealed class FileLoaderWithoutRecents() : FileExportLoaderControl("TLK save-all test host")
    {
        public override string Toolname => null;
        public override bool CanParse(ExportEntry exportEntry) => false;
        public override bool CanLoadFile() => false;
        public override bool CanSave() => false;
        internal override bool CanLoadFileExtension(string extension) => false;
        public override void LoadFile(string filepath) => throw new NotSupportedException();
        public override void LoadExport(ExportEntry exportEntry) => throw new NotSupportedException();
        public override void Save() => throw new NotSupportedException();
        public override void SaveAs() => throw new NotSupportedException();
        internal override void OpenFile() => throw new NotSupportedException();
        public override void PopOut() => throw new NotSupportedException();
        public override void UnloadExport() { }
        public override void Dispose() { }
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
