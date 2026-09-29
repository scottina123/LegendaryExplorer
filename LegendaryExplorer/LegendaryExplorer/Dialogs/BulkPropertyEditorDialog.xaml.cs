using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryExplorer.SharedUI;
using static LegendaryExplorer.Tools.PackageEditor.Experiments.PackageEditorExperimentsScottina;

namespace LegendaryExplorer.Dialogs
{
    public partial class BulkPropertyEditorDialog : Window
    {
        private readonly IReadOnlyList<BulkPropertyClassTarget> classTargets;
        private List<BulkPropertyTarget> properties = [];
        private bool refreshingClasses;

        internal BulkPropertyEditorDialog(Window owner, IReadOnlyList<BulkPropertyClassTarget> classTargets, string defaultClassName)
        {
            this.classTargets = classTargets;
            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
            Owner = owner;
            ClassesListBox.ItemsSource = classTargets;
            ClassesListBox.SelectedItem = classTargets.FirstOrDefault(target =>
                string.Equals(target.ClassName, defaultClassName, StringComparison.OrdinalIgnoreCase)) ?? classTargets.FirstOrDefault();
            Loaded += (_, _) =>
            {
                ClassFilterBox.Focus();
                if (ClassesListBox.SelectedItem != null)
                {
                    ClassesListBox.ScrollIntoView(ClassesListBox.SelectedItem);
                }
            };
        }

        private void ClassFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ClassesListBox == null || PropertyFilterBox == null)
            {
                return;
            }

            var selectedClass = ClassesListBox.SelectedItem as BulkPropertyClassTarget;
            string filter = ClassFilterBox.Text.Trim();
            var visibleClasses = classTargets.Where(target =>
                target.ClassName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            refreshingClasses = true;
            ClassesListBox.ItemsSource = visibleClasses;
            ClassesListBox.SelectedItem = visibleClasses.Contains(selectedClass) ? selectedClass : visibleClasses.FirstOrDefault();
            refreshingClasses = false;
            if (!ReferenceEquals(ClassesListBox.SelectedItem, selectedClass))
            {
                PropertiesListBox.SelectedItem = null;
            }
            RefreshProperties();
        }

        private void ClassesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!refreshingClasses)
            {
                // A property selected on one class must never remain selected on another.
                PropertiesListBox.SelectedItem = null;
                RefreshProperties();
            }
        }

        private void RefreshProperties()
        {
            var selectedProperty = PropertiesListBox.SelectedItem as BulkPropertyTarget;
            properties = [];
            if (ClassesListBox.SelectedItem is BulkPropertyClassTarget targetClass)
            {
                PropertiesHeader.Text = $"{targetClass.ClassName} properties";
                ScopeText.Text = $"Changes apply to {targetClass.DisplayName}. Edit and remove affect only exports containing the selected property.";
                var failures = new List<string>();
                properties = GetBulkPropertyTargets(targetClass.Exports, failures);
                if (failures.Count > 0)
                {
                    new ListDialog(failures, Title, "Some exports could not be read and will be skipped.", IsLoaded ? this : Owner).Show();
                }
            }
            else
            {
                PropertiesHeader.Text = "Properties";
                ScopeText.Text = "Select a class to manage its properties.";
            }

            FilterProperties(selectedProperty);
        }

        private void PropertyFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (PropertiesListBox != null && EmptyPropertiesText != null)
            {
                FilterProperties(PropertiesListBox.SelectedItem as BulkPropertyTarget);
            }
        }

        private void FilterProperties(BulkPropertyTarget selectedProperty)
        {
            string filter = PropertyFilterBox.Text.Trim();
            var visibleProperties = properties.Where(target =>
                target.PropertyName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || target.Property.PropType.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            PropertiesListBox.ItemsSource = visibleProperties;
            PropertiesListBox.SelectedItem = selectedProperty == null ? null : visibleProperties.FirstOrDefault(target =>
                target.Property.Name == selectedProperty.Property.Name
                && target.Property.StaticArrayIndex == selectedProperty.Property.StaticArrayIndex);
            EmptyPropertiesText.Text = ClassesListBox.SelectedItem == null
                ? "No matching classes."
                : properties.Count == 0
                    ? "No existing properties. Use Add Property to add one."
                    : "No properties match the filter.";
            EmptyPropertiesText.Visibility = visibleProperties.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateButtons();
        }

        private void PropertiesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

        private void UpdateButtons()
        {
            if (AddButton == null || EditButton == null || RemoveButton == null)
            {
                return;
            }

            AddButton.IsEnabled = ClassesListBox.SelectedItem is BulkPropertyClassTarget;
            EditButton.IsEnabled = RemoveButton.IsEnabled = AddButton.IsEnabled && PropertiesListBox.SelectedItem is BulkPropertyTarget;
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (ClassesListBox.SelectedItem is BulkPropertyClassTarget targetClass)
            {
                AddBulkProperties(this, targetClass);
                RefreshProperties();
            }
        }

        private void EditButton_Click(object sender, RoutedEventArgs e) => EditOrRemoveProperty(delete: false);

        private void RemoveButton_Click(object sender, RoutedEventArgs e) => EditOrRemoveProperty(delete: true);

        private void EditOrRemoveProperty(bool delete)
        {
            if (ClassesListBox.SelectedItem is BulkPropertyClassTarget targetClass
                && PropertiesListBox.SelectedItem is BulkPropertyTarget target)
            {
                EditOrDeleteBulkProperty(this, targetClass, target, delete);
                RefreshProperties();
            }
        }

        private void PropertiesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject source
                && ItemsControl.ContainerFromElement(PropertiesListBox, source) is ListBoxItem)
            {
                e.Handled = true;
                EditOrRemoveProperty(delete: false);
            }
        }

        private void PropertiesListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                EditOrRemoveProperty(delete: false);
            }
        }
    }
}
