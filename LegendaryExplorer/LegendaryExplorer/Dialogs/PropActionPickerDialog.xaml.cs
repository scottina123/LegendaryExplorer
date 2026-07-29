using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using LegendaryExplorer.Misc;

namespace LegendaryExplorer.Dialogs
{
    public partial class PropActionPickerDialog : NotifyPropertyChangedWindowBase
    {
        private readonly string _initialProp;
        private readonly string _initialAction;

        public sealed record PropActionChoice(
            string Prop,
            string Action,
            string SourcePackagePath = null,
            int SourceTrackUIndex = 0,
            int SourceKeyIndex = -1,
            int SourceWeaponUIndex = 0,
            string SourceWeaponPackagePath = null,
            bool HasEffects = false)
        {
            public bool HasWeaponClass => SourceWeaponUIndex != 0;
        }

        public ICollectionView ChoicesView { get; }
        public IReadOnlyList<string> PropNames { get; }
        public PropActionChoice SelectedChoice { get; private set; }

        public PropActionPickerDialog(IEnumerable<PropActionChoice> choices, Window owner, string initialProp = null, string initialAction = null)
        {
            _initialProp = initialProp;
            _initialAction = initialAction;
            List<PropActionChoice> choiceList = choices.ToList();
            DataContext = this;
            PropNames = choiceList
                .Select(choice => choice.Prop)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(prop => prop, StringComparer.OrdinalIgnoreCase)
                .ToList();
            ChoicesView = CollectionViewSource.GetDefaultView(choiceList);
            InitializeComponent();
            if (owner?.IsLoaded == true && PresentationSource.FromVisual(owner) != null)
            {
                Owner = owner;
            }

            Loaded += (_, _) =>
            {
                int initialPropIndex = string.IsNullOrWhiteSpace(_initialProp)
                    ? -1
                    : PropNames.ToList().FindIndex(prop => prop.Equals(_initialProp, StringComparison.OrdinalIgnoreCase));
                PropComboBox.SelectedIndex = initialPropIndex >= 0 ? initialPropIndex : PropNames.Count > 0 ? 0 : -1;
                PropComboBox.Focus();
            };
        }

        private void FilterTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            RefreshChoiceFilter();
        }

        private void PropComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshChoiceFilter();

        private void RefreshChoiceFilter()
        {
            if (ChoicesView is null || FilterTextBox is null || PropComboBox is null)
            {
                return;
            }

            string prop = PropComboBox.SelectedItem as string ?? PropComboBox.Text;
            string filter = FilterTextBox.Text.Trim();
            ChoicesView.Filter = item => item is PropActionChoice choice
                && choice.Prop.Equals(prop, StringComparison.OrdinalIgnoreCase)
                && (filter.Length == 0 || choice.Action.Contains(filter, StringComparison.OrdinalIgnoreCase));
            ChoicesView.Refresh();
            PropActionChoice actionToSelect = ChoicesView.Cast<PropActionChoice>()
                .FirstOrDefault(choice => choice.Action.Equals(_initialAction, StringComparison.OrdinalIgnoreCase))
                ?? ChoicesView.Cast<PropActionChoice>().FirstOrDefault();
            ChoicesGrid.SelectedItem = actionToSelect;
            if (actionToSelect is not null)
            {
                ChoicesGrid.Dispatcher.BeginInvoke(() => ChoicesGrid.ScrollIntoView(actionToSelect));
            }
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e) => AcceptSelection();

        private void ChoicesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AcceptSelection();

        private void AcceptSelection()
        {
            if (ChoicesGrid.SelectedItem is not PropActionChoice choice)
            {
                return;
            }

            SelectedChoice = choice;
            DialogResult = true;
        }
    }
}
