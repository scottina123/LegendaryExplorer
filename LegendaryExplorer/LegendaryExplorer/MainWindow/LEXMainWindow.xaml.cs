using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.UnrealExtensions;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.Startup;
using LegendaryExplorerCore;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Helpers;
using System.Diagnostics;

namespace LegendaryExplorer.MainWindow
{
    /// <summary>
    /// Interaction logic for LEXMainWindow.xaml
    /// </summary>
    public partial class LEXMainWindow : Window
    {
        private ToolMenuPages toolPages;
        private int pageIndex;

        public LEXMainWindow()
        {
            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
            DataContext = this;

            //Check that at least one game path is set. If none are, show the initial dialog.
            if (!Settings.MainWindow_CompletedInitialSetup ||
                (ME1Directory.DefaultGamePath == null && ME2Directory.DefaultGamePath == null &&
                 ME3Directory.DefaultGamePath == null && LegendaryExplorerCoreLibSettings.Instance.LEDirectory == null))
            {
                new InitialSetup().ShowDialog();
            }

            RefreshToolList();
            RefreshTaskbarPins();
            ToolSet.TaskbarPinsChanged += ToolSet_TaskbarPinsChanged;
            Closed += MainWindow_Closed;

#if DEBUG
            MaxWidth = 915;
            Width = MaxWidth;
#endif

            Task.Run(TLKLoader.LoadSavedTlkList);
        }

        public void TransitionFromSplashToMainWindow()
        {
            {
                Opacity = 1;
                Show();
                DPIAwareSplashScreen.DestroySplashScreen();
                DependencyCheck.CheckDependencies(this);
                return;
            }
        }

        private void ToolSet_TaskbarPinsChanged(object sender, EventArgs e) => RefreshTaskbarPins();

        private void RefreshTaskbarPins() => pinnedToolTaskbar.setToolList(ToolSet.PinnedTools);

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            ToolSet.TaskbarPinsChanged -= ToolSet_TaskbarPinsChanged;
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            new SettingsWindow().Show();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshToolList();
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            SearchButton.Visibility = Visibility.Collapsed;
            SearchBorder.Visibility = Visibility.Visible;
            SearchBox.Focus();
            SearchBox.SelectAll();
        }

        private void CloseSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Clear();
            CollapseSearch();
            SearchButton.Focus();
            e.Handled = true;
        }

        private void CollapseSearch()
        {
            if (!string.IsNullOrWhiteSpace(SearchBox.Text)) return;
            if (SearchBox.IsKeyboardFocusWithin)
            {
                Keyboard.ClearFocus();
            }
            SearchBorder.Visibility = Visibility.Collapsed;
            SearchButton.Visibility = Visibility.Visible;
        }

        private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (SearchBorder.Visibility == Visibility.Visible && !SearchBorder.IsMouseOver
                && !CloseSearchButton.IsMouseOver
                && !pinnedToolTaskbar.IsMouseOver)
            {
                // Keep the results in place so this click can still open the tool under the pointer.
                // Taskbar interactions retain the search field so its shortcuts do not move before release.
                CollapseSearch();
            }
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SearchButton_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && SearchBorder.Visibility == Visibility.Visible)
            {
                CloseSearch_Click(sender, e);
            }
            else if (!SearchBox.IsKeyboardFocusWithin && e.Key is Key.PageDown or Key.PageUp)
            {
                ShowPage(pageIndex + (e.Key == Key.PageDown ? 1 : -1));
                e.Handled = true;
            }
        }

        private void MainWindow_Deactivated(object sender, EventArgs e)
        {
            if (!pinnedToolTaskbar.IsDragging) CollapseSearch();
        }

        private void RefreshToolList()
        {
            if (mainToolPanel == null || ToolSet.Items == null) return;
            toolPages = new ToolMenuPages(ToolSet.Items, SearchBox.Text);
            ShowPage(0);
        }

        private void ShowPage(int index)
        {
            if (toolPages == null) return;
            pageIndex = Math.Clamp(index, 0, toolPages.PageCount - 1);
            mainToolPanel.setToolList(toolPages.GetPage(pageIndex));
            PageStatus.Text = $"{pageIndex + 1} / {toolPages.PageCount}";
            PageStatus.ToolTip = $"{toolPages.Count} tools; {ToolMenuPages.PageSize} per page";
            PreviousPageButton.IsEnabled = pageIndex > 0;
            NextPageButton.IsEnabled = pageIndex < toolPages.PageCount - 1;
            NoToolsHint.Visibility = toolPages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PreviousPage_Click(object sender, RoutedEventArgs e) => ShowPage(pageIndex - 1);
        private void NextPage_Click(object sender, RoutedEventArgs e) => ShowPage(pageIndex + 1);

        private void ToolPanel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta != 0) ShowPage(pageIndex + (e.Delta < 0 ? 1 : -1));
            e.Handled = true;
        }

        private void MainWindow_PreviewDragOver(object sender, DragEventArgs e)
        {
            // Only handle files dragged in from outside; internal drags (e.g. taskbar pin reordering) pass through
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void MainWindow_PreviewDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

            // Defer opening so the drag source (e.g. Windows Explorer) isn't blocked while the tool chooser dialog is open
            Dispatcher.BeginInvoke(() =>
            {
                foreach (string file in files.Where(System.IO.File.Exists))
                {
                    CommandLineArgs.OpenFile(file);
                }
            });
        }

        private void SystemCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = true;
        }

        /// <summary>
        /// If the main window is allowed to close - if a window is kept open this is set to false, which suppresses this window from also closing
        /// </summary>
        public static bool IsAllowedToClose;

        /// <summary>
        /// If the main window is allowed to close - if a window is kept open this is set to false, which suppresses this window from also closing
        /// </summary>
        private static bool IsCloseCommandExecuting;
        private static bool IsMainWindowTryingToClose;

        private void CloseCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            Close(); // Attempt to close the main window. This will trigger closing logic that can be aborted.
        }

        private void CloseSubWindows()
        {
            // CLOSES SUBWINDOWS
            Debug.WriteLine("CloseCommandExecuted");
            IsCloseCommandExecuting = true;
            IsAllowedToClose = true; // Reset - subwindows will handle this
            foreach (var w in Application.Current.Windows.OfType<Window>().ToList())
            {
                if (w == this)
                    continue; // We don't check on ourself
                w.Close();
            }

            IsCloseCommandExecuting = false;
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            Debug.WriteLine("MainWindowClosing");

            if (!IsCloseCommandExecuting)
            {
                Debug.WriteLine("MainWindowClosingREALS");
                // Closed via middle click in taskbar
                CloseSubWindows();
            }
            
            e.Cancel = !IsAllowedToClose;
            IsMainWindowTryingToClose = false;
            Debug.WriteLine("MainWindowClosingEND " + !IsAllowedToClose);
        }

        private void MinimizeCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            SystemCommands.MinimizeWindow(this);
        }

        private void BackgroundMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                if (SearchBox.IsFocused)
                {
                    SearchBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                }
                this.DragMove();
            }
        }

        private void MainWindow_Closing(object sender, ExecutedRoutedEventArgs e)
        {
        }
    }
}
