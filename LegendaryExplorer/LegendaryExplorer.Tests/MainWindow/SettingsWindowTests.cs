using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.MainWindow;
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

            var presetButton = (Button)window.FindName("ApplyScottinaPresetButton");
            Assert.AreEqual("Apply Scottina's preset", presetButton.Content);
            foreach (string presetSearch in new[] { "Scottina", "favorites" })
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
            Assert.AreSame(general, tabs.SelectedItem, "Search must also match dropdown option labels.");
            Assert.AreEqual(1, ((StackPanel)((ScrollViewer)general.Content).Content).Children
                .Cast<FrameworkElement>().Count(element => element.Visibility == Visibility.Visible));

            search.Text = "v7110";
            Assert.AreEqual("Audio", ((TabItem)tabs.SelectedItem).Header);
            var pathRow = (StackPanel)((TextBox)window.FindName("Wwise_7110Path")).Parent;
            var audioPanel = (StackPanel)pathRow.Parent;
            var pathLabel = (TextBlock)audioPanel.Children[audioPanel.Children.IndexOf(pathRow) - 1];
            Assert.AreEqual(Visibility.Visible, pathRow.Visibility);
            Assert.AreEqual(Visibility.Visible, pathLabel.Visibility, "Path labels and editor rows must filter together.");
            search.Text = "v3773";
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
            loadedField.SetValue(null, previousLoaded);
        }
    }

    private static void FlushDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
