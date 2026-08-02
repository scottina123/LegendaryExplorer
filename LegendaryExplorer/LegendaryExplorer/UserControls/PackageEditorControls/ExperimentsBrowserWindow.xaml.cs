using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryExplorer.SharedUI;

namespace LegendaryExplorer.UserControls.PackageEditorControls
{
    public partial class ExperimentsBrowserWindow : Window
    {
        private readonly IReadOnlyList<ExperimentBrowserItem> _experiments;

        internal ExperimentBrowserItem SelectedExperiment { get; private set; }

        internal ExperimentsBrowserWindow(Window owner, string title,
            IReadOnlyList<ExperimentBrowserItem> experiments)
        {
            _experiments = experiments;
            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
            Owner = owner;
            Title = title;

            CategoriesListBox.ItemsSource = experiments
                .Select(experiment => experiment.Category)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
                .ToList();
            CategoriesListBox.SelectedItem = CategoriesListBox.Items
                .Cast<string>()
                .FirstOrDefault(category => category == "General")
                ?? CategoriesListBox.Items.Cast<string>().FirstOrDefault();

            Loaded += (_, _) =>
            {
                SearchTextBox.Focus();
                RefreshExperiments();
            };
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshExperiments();
        }

        private void CategoriesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SearchTextBox?.Text))
            {
                RefreshExperiments();
            }
        }

        private void ExperimentsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            SelectExperiment();
        }

        private void ExperimentsListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SelectExperiment();
            }
        }

        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            SelectExperiment();
        }

        private void RefreshExperiments()
        {
            if (ExperimentsListBox == null || ResultsHeaderTextBlock == null || ResultCountTextBlock == null)
            {
                return;
            }

            string searchText = SearchTextBox?.Text?.Trim();
            IEnumerable<ExperimentBrowserItem> filteredExperiments = _experiments;

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                filteredExperiments = filteredExperiments.Where(experiment =>
                    experiment.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                    || experiment.Category.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                    || experiment.Description?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true);
                ResultsHeaderTextBlock.Text = "Search results";
            }
            else if (CategoriesListBox?.SelectedItem is string category)
            {
                filteredExperiments = filteredExperiments.Where(experiment =>
                    experiment.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
                ResultsHeaderTextBlock.Text = category;
            }

            List<ExperimentBrowserItem> results = filteredExperiments
                .OrderBy(experiment => experiment.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            ExperimentsListBox.ItemsSource = results;
            ExperimentsListBox.SelectedItem = results.FirstOrDefault(experiment => experiment.IsEnabled)
                                                   ?? results.FirstOrDefault();
            ResultCountTextBlock.Text = $"{results.Count} experiment{(results.Count == 1 ? string.Empty : "s")}";
        }

        private void SelectExperiment()
        {
            if (ExperimentsListBox.SelectedItem is not ExperimentBrowserItem { IsEnabled: true } experiment)
            {
                return;
            }

            SelectedExperiment = experiment;
            DialogResult = true;
        }
    }
}
