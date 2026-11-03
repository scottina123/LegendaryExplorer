using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using LegendaryExplorer.MainWindow;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.MainWindow;

[TestClass]
public class SettingsWindowTests
{
    [STATestMethod]
    public void SearchFiltersEveryTabKeepsEditorsWorkingAndRestoresTheView()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(SettingsWindow).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));

        var loadedField = typeof(Settings).GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
        object previousLoaded = loadedField.GetValue(null);
        bool previousAutoSave = Settings.SequenceEditor_AutoSaveViewV2;
        string previousTheme = Settings.Global_Theme;
        loadedField.SetValue(null, false); // Exercise editing without saving the user's preferences.
        SettingsWindow window = null;
        try
        {
            window = new SettingsWindow { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            FlushDispatcher();
            var search = (TextBox)window.FindName("SettingsSearchBox");
            var tabs = (TabControl)window.FindName("SettingsTabs");
            var noMatches = (TextBlock)window.FindName("NoMatchingSettingsText");
            var categories = tabs.Items.Cast<TabItem>().ToArray();
            var general = categories.Single(tab => Equals(tab.Header, "General"));
            tabs.SelectedItem = general;
            VerifyThemeButtons(window);

            var presetButton = (Button)window.FindName("ApplyScottinaPresetButton");
            Assert.AreEqual("Apply Scottina's preset", presetButton.Content);
            Assert.AreEqual(1, ((StackPanel)presetButton.Parent).Children.Count,
                "The preset row must contain only the button, without a description.");
            foreach (string presetSearch in new[] { "Scottina", "preset" })
            {
                search.Text = presetSearch;
                Assert.AreSame(general, tabs.SelectedItem);
                Assert.AreEqual(1, categories.Count(tab => tab.Visibility == Visibility.Visible));
                Assert.AreEqual(Visibility.Visible, ((FrameworkElement)presetButton.Parent).Visibility);
            }

            search.Text = "  AUTO-save  ";
            CollectionAssert.AreEqual(new[] { "Sequence Editor", "Audio" },
                categories.Where(tab => tab.Visibility == Visibility.Visible).Select(tab => (string)tab.Header).ToArray());
            Assert.AreEqual("Sequence Editor", ((TabItem)tabs.SelectedItem).Header,
                "Search must select a matching tab when the current tab has no matches.");
            var autoSave = (CheckBox)window.FindName("SequenceEditor_AutoSaveViewV2");
            Assert.AreEqual(Visibility.Visible, ((FrameworkElement)autoSave.Parent).Visibility);
            Assert.AreEqual(Visibility.Collapsed,
                ((FrameworkElement)((CheckBox)window.FindName("SequenceEditor_ShowParsedInfo")).Parent).Visibility);
            Assert.AreEqual(Visibility.Visible,
                ((FrameworkElement)((CheckBox)window.FindName("WwiseGraphEditor_AutoSaveView")).Parent).Visibility,
                "The filter must reach controls in tabs that have never been selected.");

            autoSave.IsChecked = !previousAutoSave;
            FlushDispatcher();
            Assert.AreEqual(!previousAutoSave, Settings.SequenceEditor_AutoSaveViewV2,
                "Filtered controls must retain their original settings bindings.");

            search.Text = "  audio\tVIEW  ";
            Assert.AreEqual("Audio", ((TabItem)tabs.SelectedItem).Header);
            Assert.AreEqual(1, categories.Count(tab => tab.Visibility == Visibility.Visible));
            Assert.AreEqual(Visibility.Collapsed,
                ((FrameworkElement)((CheckBox)window.FindName("Soundplorer_AutoplayEntriesOnSelection")).Parent).Visibility);

            search.Text = "modern dark";
            Assert.AreSame(general, tabs.SelectedItem, "Search must also match theme button labels.");
            Assert.AreEqual(1, ((StackPanel)((ScrollViewer)general.Content).Content).Children
                .Cast<FrameworkElement>().Count(element => element.Visibility == Visibility.Visible));

            search.Text = "Mass Effect 3";
            Assert.AreSame(general, tabs.SelectedItem);
            var pathRow = (StackPanel)((TextBox)window.FindName("Global_ME3Directory")).Parent;
            var generalPanel = (StackPanel)pathRow.Parent;
            var pathLabel = (TextBlock)generalPanel.Children[generalPanel.Children.IndexOf(pathRow) - 1];
            Assert.AreEqual(Visibility.Visible, pathRow.Visibility);
            Assert.AreEqual(Visibility.Visible, pathLabel.Visibility, "Path labels and editor rows must filter together.");
            search.Text = "Mass Effect 2";
            Assert.AreEqual(Visibility.Collapsed, pathRow.Visibility);
            Assert.AreEqual(Visibility.Collapsed, pathLabel.Visibility);

            search.Text = "coalesced editor";
            Assert.AreEqual("Files", ((TabItem)tabs.SelectedItem).Header,
                "File association descriptions must be searchable too.");

            search.Text = "no-setting-could-match-this";
            Assert.IsTrue(categories.All(tab => tab.Visibility == Visibility.Collapsed));
            Assert.AreEqual(Visibility.Collapsed, tabs.Visibility);
            Assert.AreEqual(Visibility.Visible, noMatches.Visibility);
            search.Text = "   ";
            Assert.AreEqual(Visibility.Collapsed, noMatches.Visibility);
            Assert.AreEqual(Visibility.Visible, tabs.Visibility);
            Assert.AreSame(general, tabs.SelectedItem, "Clearing the filter must restore the original tab.");
            Assert.IsTrue(categories.All(tab => tab.Visibility == Visibility.Visible &&
                ((StackPanel)((ScrollViewer)tab.Content).Content).Children.Cast<FrameworkElement>()
                    .All(element => element.Visibility == Visibility.Visible)));

            var packageEditor = categories.Single(tab => Equals(tab.Header, "Package Editor"));
            tabs.SelectedItem = packageEditor;
            search.Text = "analytics";
            ((Button)window.FindName("ClearSettingsSearchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual("", search.Text);
            Assert.AreSame(packageEditor, tabs.SelectedItem);
        }
        finally
        {
            window?.Close();
            Settings.SequenceEditor_AutoSaveViewV2 = previousAutoSave;
            Settings.Global_Theme = previousTheme;
            loadedField.SetValue(null, previousLoaded);
        }
    }

    private static void VerifyThemeButtons(SettingsWindow window)
    {
        var options = new[]
        {
            (Theme: "Light", Button: (RadioButton)window.FindName("LightThemeButton")),
            (Theme: "Dark", Button: (RadioButton)window.FindName("DarkThemeButton")),
            (Theme: "ModernDark", Button: (RadioButton)window.FindName("ModernDarkThemeButton"))
        };
        var row = (StackPanel)options[0].Button.Parent;
        var tile = new ToolTile { IsFlat = true, DataContext = new Tool { Abbreviation = "pkg" } };
        tile.Measure(new Size(96, 80));
        tile.Arrange(new Rect(0, 0, 96, 80));
        var tileHost = new Window
        {
            Content = tile, Width = 96, Height = 80, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None
        };
        tileHost.Show();
        try
        {
            Assert.AreEqual(Orientation.Horizontal, row.Orientation);
            Assert.HasCount(3, row.Children.OfType<RadioButton>().ToArray());
            foreach (var option in options)
            {
                option.Button.SetCurrentValue(RadioButton.IsCheckedProperty, true);
                FlushDispatcher();
                Assert.AreEqual(option.Theme, Settings.Global_Theme, "Theme buttons must update the saved setting.");
                Assert.AreEqual(1, options.Count(item => item.Button.IsChecked == true));
                ThemeManager.ApplyTheme(Enum.Parse<AppTheme>(option.Theme));
                FlushDispatcher();
                var label = Descendants<TextBlock>(tile).Single();
                Color actual = ((SolidColorBrush)label.Foreground).Color;
                Color expected = ((SolidColorBrush)Application.Current.FindResource(SystemColors.WindowTextBrushKey)).Color;
                Assert.AreEqual(expected, actual, $"Flat menu labels must follow {option.Theme} text colors.");
            }
            Settings.Global_Theme = "Light";
            FlushDispatcher();
            Assert.IsTrue(options[0].Button.IsChecked, "External setting changes must update the selected button.");
            Settings.Global_Theme = "Dark";
            FlushDispatcher();
            Assert.IsTrue(options[1].Button.IsChecked);
            ThemeManager.ApplyTheme(AppTheme.Dark);
        }
        finally
        {
            tileHost.Close();
        }
    }

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void FlushDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
