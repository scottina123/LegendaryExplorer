using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore;
using LegendaryExplorerCore.GameFilesystem;
using Microsoft.WindowsAPICodePack.Dialogs;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;
using Path = System.IO.Path;

namespace LegendaryExplorer.MainWindow
{
    /// <summary>
    /// Interaction logic for SettingsWindow.xaml
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly List<SettingsSearchCategory> _searchCategories = new();
        private TabItem _tabBeforeSearch;
        private bool _isSearching;

        private sealed record SettingsSearchEntry(FrameworkElement[] Elements, string SearchText);
        private sealed record SettingsSearchCategory(TabItem Tab, ScrollViewer Scroller, List<SettingsSearchEntry> Entries);

        public SettingsWindow()
        {
            InitializeComponent();
            InitializeSettingsSearch();
            CustomWindowChrome.ApplyCustomChrome(this);
        }

        private void InitializeSettingsSearch()
        {
            // Read the logical content of every tab, including tabs that have never been selected.
            foreach (var tab in SettingsTabs.Items.OfType<TabItem>())
            {
                var scroller = (ScrollViewer)tab.Content;
                var panel = (StackPanel)scroller.Content;
                var entries = new List<SettingsSearchEntry>();
                for (int i = 0; i < panel.Children.Count; i++)
                {
                    var elements = new List<FrameworkElement> { (FrameworkElement)panel.Children[i] };
                    // File/directory settings have a separate label above their editor row.
                    if (panel.Children[i] is TextBlock && i + 1 < panel.Children.Count && panel.Children[i + 1] is StackPanel)
                    {
                        elements.Add((FrameworkElement)panel.Children[++i]);
                    }

                    var searchText = new StringBuilder(tab.Header?.ToString());
                    foreach (var element in elements)
                    {
                        AppendSearchText(element, searchText);
                    }
                    entries.Add(new SettingsSearchEntry(elements.ToArray(), searchText.ToString()));
                }
                _searchCategories.Add(new SettingsSearchCategory(tab, scroller, entries));
            }
        }

        private static void AppendSearchText(DependencyObject element, StringBuilder text)
        {
            if (element is TextBlock label)
            {
                text.Append(' ').Append(label.Text);
            }
            else if (element is ContentControl { Content: string content })
            {
                text.Append(' ').Append(content);
            }
            if (element is FrameworkElement { ToolTip: string toolTip })
            {
                text.Append(' ').Append(toolTip);
            }

            // Search labels, options and descriptions, rather than editable setting values.
            if (element is TextBox) return;
            foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            {
                AppendSearchText(child, text);
            }
        }

        private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_searchCategories.Count == 0) return; // XAML may raise TextChanged during initialization.

            var terms = SettingsSearchBox.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            bool isSearching = terms.Length > 0;
            if (isSearching && !_isSearching)
            {
                _tabBeforeSearch = SettingsTabs.SelectedItem as TabItem;
            }

            TabItem firstMatch = null;
            foreach (var category in _searchCategories)
            {
                bool hasMatches = false;
                foreach (var entry in category.Entries)
                {
                    bool matches = terms.All(term => entry.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase));
                    foreach (var element in entry.Elements)
                    {
                        element.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
                    }
                    hasMatches |= matches;
                }
                category.Tab.Visibility = hasMatches ? Visibility.Visible : Visibility.Collapsed;
                if (hasMatches) firstMatch ??= category.Tab;
                if (isSearching) category.Scroller.ScrollToTop();
            }

            SettingsTabs.Visibility = firstMatch != null ? Visibility.Visible : Visibility.Collapsed;
            NoMatchingSettingsText.Visibility = firstMatch == null ? Visibility.Visible : Visibility.Collapsed;
            if (!isSearching && _isSearching && _tabBeforeSearch != null)
            {
                SettingsTabs.SelectedItem = _tabBeforeSearch;
            }
            else if (SettingsTabs.SelectedItem is not TabItem { Visibility: Visibility.Visible })
            {
                SettingsTabs.SelectedItem = firstMatch;
            }
            _isSearching = isSearching;
        }

        private void ClearSettingsSearch_Click(object sender, RoutedEventArgs e)
        {
            SettingsSearchBox.Clear();
            SettingsSearchBox.Focus();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SettingsSearchBox.Focus();
                SettingsSearchBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _isSearching)
            {
                ClearSettingsSearch_Click(sender, e);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Handles clicking on 'Browse' button for a directory text box.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DirectoryBrowse_Click(object sender, RoutedEventArgs e)
        {
            // Find the sibling textbox (this is a generic function for all potential browse buttons)
            var browseButton = sender as Button;
            foreach (var objChild in LogicalTreeHelper.GetChildren(browseButton.Parent))
            {
                if (objChild is TextBox t)
                {
                    var dlg = new CommonOpenFileDialog("Select folder") { IsFolderPicker = true };
                    if (DirectoryMemory.ShowDialog(dlg, this, t.Text) != CommonFileDialogResult.Ok) { return; }
                    t.Text = dlg.FileName;
                }
            }
        }

        /// <summary>
        /// Handles clicking on 'Browse' button for a file selection text box.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void FileBrowse_Click(object sender, RoutedEventArgs e)
        {
            // Find the sibling textbox (this is a generic function for all potential browse buttons)
            var browseButton = sender as Button;
            foreach (var objChild in LogicalTreeHelper.GetChildren(browseButton.Parent))
            {
                if (objChild is TextBox t)
                {
                    var dlg = new CommonOpenFileDialog("Select file")
                        {Filters = {new CommonFileDialogFilter("", browseButton.Tag.ToString() ?? "")}};
                    if (DirectoryMemory.ShowDialog(dlg, this, t.Text) != CommonFileDialogResult.Ok) { return; }
                    t.Text = dlg.FileName;
                }
            }
        }

        private void Setting_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            var t = sender as TextBox;
            t?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

            // Handle setting of game paths
            if (t.Parent is StackPanel parentPanel && !string.IsNullOrEmpty(parentPanel.Tag.ToString()))
            {
                switch (parentPanel.Tag.ToString())
                {
                    case "Global_ME1Directory" when ME1Directory.IsValidGameDir(t.Text):
                        LegendaryExplorerCoreLibSettings.Instance.ME1Directory = t.Text;
                        ME1Directory.DefaultGamePath = t.Text;
                        break;
                    case "Global_ME2Directory" when ME2Directory.IsValidGameDir(t.Text):
                        LegendaryExplorerCoreLibSettings.Instance.ME2Directory = t.Text;
                        ME2Directory.DefaultGamePath = t.Text;
                        break;
                    case "Global_ME3Directory" when ME3Directory.IsValidGameDir(t.Text):
                        LegendaryExplorerCoreLibSettings.Instance.ME3Directory = t.Text;
                        ME3Directory.DefaultGamePath = t.Text;
                        break;
                    case "Global_LEDirectory" when LEDirectory.IsValidGameDir(t.Text):
                        LegendaryExplorerCoreLibSettings.Instance.LEDirectory = t.Text;
                        LE1Directory.ReloadDefaultGamePath();
                        LE2Directory.ReloadDefaultGamePath();
                        LE3Directory.ReloadDefaultGamePath();
                        break;
                    case nameof(Settings.Global_UDKCustomDirectory):
                        if (UDKDirectory.IsValidGameDir(t.Text))
                        {
                            LegendaryExplorerCoreLibSettings.Instance.UDKCustomDirectory = t.Text;
                            UDKDirectory.ReloadDefaultGamePath();
                            break;
                        }
                        var rootPath = Path.GetDirectoryName(t.Text);
                        if (UDKDirectory.IsValidGameDir(rootPath))
                        {
                            LegendaryExplorerCoreLibSettings.Instance.UDKCustomDirectory = rootPath;
                            UDKDirectory.ReloadDefaultGamePath();
                        }
                        break;
                }
            }
        }

        private void SaveFile_Click(object sender, RoutedEventArgs e)
        {
            Settings.Save();
        }

        private void ApplyScottinaPreset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ScottinaPreset.Apply();
                MessageBox.Show(this,
                    "Scottina's preset has been applied and saved. Restart LEX to refresh tools that only read settings when they launch.",
                    "Scottina's preset", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, $"Unable to apply Scottina's preset: {exception.Message}",
                    "Scottina's preset", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AssociatePCCSFM_Click(object sender, RoutedEventArgs e)
        {
            FileAssociations.AssociatePCCSFM();
        }

        private void AssociateUPKUDK_Click(object sender, RoutedEventArgs e)
        {
            FileAssociations.AssociateUPKUDK();
        }

        private void AssociateOthers_Click(object sender, RoutedEventArgs e)
        {
            FileAssociations.AssociateOthers();
        }

        private void AssociateCoalescedBin_Click(object sender, RoutedEventArgs e)
        {
            FileAssociations.AssociateCoalescedBin();
        }
    }
}
