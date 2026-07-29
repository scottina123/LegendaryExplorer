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
        public ICollectionView PropNamesView { get; }
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
            PropNamesView = CollectionViewSource.GetDefaultView(PropNames);
            ChoicesView = CollectionViewSource.GetDefaultView(choiceList);
            InitializeComponent();
            if (owner?.IsLoaded == true && PresentationSource.FromVisual(owner) != null)
            {
                Owner = owner;
            }

            Loaded += (_, _) =>
            {
                PropListBox.SelectedItem = PropNames.FirstOrDefault(prop =>
                                               prop.Equals(_initialProp, StringComparison.OrdinalIgnoreCase))
                                           ?? PropNames.FirstOrDefault();
                if (PropListBox.SelectedItem is not null)
                {
                    PropListBox.ScrollIntoView(PropListBox.SelectedItem);
                }

                PropFilterTextBox.Focus();
            };
        }

        private void PropFilterTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (PropNamesView is null || PropFilterTextBox is null || PropListBox is null)
            {
                return;
            }

            string selectedProp = PropListBox.SelectedItem as string;
            string filter = PropFilterTextBox.Text.Trim();
            PropNamesView.Filter = item => item is string prop
                && (filter.Length == 0 || prop.Contains(filter, StringComparison.OrdinalIgnoreCase));
            PropNamesView.Refresh();
            PropListBox.SelectedItem = PropNamesView.Cast<string>()
                .FirstOrDefault(prop => prop.Equals(selectedProp, StringComparison.OrdinalIgnoreCase))
                ?? PropNamesView.Cast<string>().FirstOrDefault();
        }

        private void PropFilterTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && PropListBox.Items.Count > 0)
            {
                PropListBox.SelectedItem ??= PropListBox.Items[0];
                PropListBox.Focus();
                e.Handled = true;
            }
        }

        private void FilterTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            RefreshChoiceFilter();
        }

        private void PropListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshChoiceFilter();

        private void RefreshChoiceFilter()
        {
            if (ChoicesView is null || FilterTextBox is null || PropListBox is null)
            {
                return;
            }

            string prop = PropListBox.SelectedItem as string;
            string filter = FilterTextBox.Text.Trim();
            ChoicesView.Filter = item => item is PropActionChoice choice
                && prop is not null
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
