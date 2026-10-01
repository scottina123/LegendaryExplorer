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
        private MenuItem selectedCategory;

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

            if (ToolSet.Items.Any((t) => t.IsFavorited))
            {
                selectedCategory = FavoritesMenuItem;
            }
            else
            {
                selectedCategory = CoreEditorsMenuItem;
            }
            RefreshToolList();
            ToolSet.FavoritesChanged += ToolSet_FavoritesChanged;
            // Keep the tool description panel available, but disable showing it on hover.
            // mainToolPanel.ToolMouseOver += Tool_MouseOver;

#if DEBUG
            MaxWidth = 915;
            Width = MaxWidth;
            ToolsetDevsMenuItem.Visibility = Visibility.Visible;
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

        private void ToolSet_FavoritesChanged(object sender, EventArgs e)
        {
            if (selectedCategory == FavoritesMenuItem)
            {
                RefreshToolList();
            }
        }

        private void Tool_MouseOver(object sender, Tool t)
        {
            toolInfoPanel.Visibility = Visibility.Visible;
            toolInfoIcon.Source = t.icon;
            toolInfoText.Text = t.description;
        }

        private void ToolScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ViewportWidthChange == 0 && e.ViewportHeightChange == 0)
            {
                return;
            }

            // Fit six columns and four rows of square icons into the available tool area.
            Thickness margin = mainToolPanel.ItemMargin;
            double width = (ToolScrollViewer.ViewportWidth - 12) / 6 - margin.Left - margin.Right;
            double height = (ToolScrollViewer.ViewportHeight - 10) / 4 - margin.Top - margin.Bottom - 7;
            mainToolPanel.ItemSize = Math.Max(96, Math.Floor(Math.Min(width, height)));
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

        private void CollapseSearch()
        {
            if (SearchBox.IsKeyboardFocusWithin)
            {
                Keyboard.ClearFocus();
            }
            SearchBorder.Visibility = Visibility.Collapsed;
            SearchButton.Visibility = Visibility.Visible;
        }

        private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (SearchBorder.Visibility == Visibility.Visible && !SearchBorder.IsMouseOver)
            {
                // Keep the results in place so this click can still open the tool under the pointer.
                CollapseSearch();
            }
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && SearchBorder.Visibility == Visibility.Visible)
            {
                CollapseSearch();
                SearchButton.Focus();
                e.Handled = true;
            }
        }

        private void MainWindow_Deactivated(object sender, EventArgs e)
        {
            CollapseSearch();
        }

        private void CategoriesButton_Click(object sender, RoutedEventArgs e)
        {
            CollapseSearch();
            CategoriesMenu.PlacementTarget = CategoriesButton;
            CategoriesMenu.IsOpen = true;
        }

        private void CategoryMenuItem_Click(object sender, RoutedEventArgs e)
        {
            selectedCategory = (MenuItem)sender;
            if (SearchBox.Text.Length > 0)
            {
                SearchBox.Clear();
            }
            else
            {
                RefreshToolList();
            }
        }

        private void RefreshToolList()
        {
            if (selectedCategory == null)
            {
                return;
            }

            bool isSearching = !string.IsNullOrWhiteSpace(SearchBox.Text);
            foreach (MenuItem item in CategoriesMenu.Items.OfType<MenuItem>())
            {
                item.IsChecked = !isSearching && item == selectedCategory;
            }

            if (isSearching)
            {
                ApplySearch();
            }
            else if (selectedCategory == FavoritesMenuItem)
            {
                SetToolListFromFavorites();
            }
            else
            {
                SetToolList((string)selectedCategory.Tag);
            }
        }

        private void ApplySearch()
        {
            var results = new List<Tool>();
            string[] words = SearchBox.Text.ToLower().Split(' ');
            foreach (Tool tool in ToolSet.Items)
            {
                if (tool.open != null)
                {
                    //for each word we've typed in
                    if (words.Any(word => tool.tags.FuzzyMatch(word) || tool.name.ToLower().Contains(word) || tool.name.ToLower().Split(' ').FuzzyMatch(word)))
                    {
                        results.Add(tool);
                    }
                }
            }

            SetToolList(results);
        }

        private void SetToolListFromFavorites()
        {
            var favorites = ToolSet.Items.Where(tool => tool.IsFavorited);
            SetToolList(favorites);
            if (!favorites.Any())
            {
                favoritesHint.Visibility = Visibility.Visible;
            }
        }

        private void SetToolList(string category)
        {
            SetToolList(ToolSet.Items.Where((t) => t.category == category || t.category2 == category));
        }

        private void SetToolList(IEnumerable<Tool> tools)
        {
            mainToolPanel.setToolList(tools);
            favoritesHint.Visibility = Visibility.Collapsed;
            toolInfoPanel.Visibility = Visibility.Collapsed;
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
