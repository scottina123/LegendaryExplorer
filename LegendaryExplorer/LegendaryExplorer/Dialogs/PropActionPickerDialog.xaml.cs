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
        private readonly int _initialClientEffectUIndex;
        private readonly string _initialClientEffectPath;

        public sealed record PropActionChoice(
            string Prop,
            string Action,
            string SourcePackagePath = null,
            int SourceTrackUIndex = 0,
            int SourceKeyIndex = -1,
            int SourceWeaponUIndex = 0,
            string SourceWeaponPackagePath = null,
            int SourceClientEffectUIndex = 0,
            string SourceClientEffectPackagePath = null,
            string ClientEffectPath = null,
            bool HasEffects = false)
        {
            public bool HasWeaponClass => SourceWeaponUIndex != 0;
        }

        public sealed record ClientEffectChoice(
            string Prop,
            string SourcePackagePath,
            int SourceUIndex,
            string ClientEffectPath)
        {
            public string DisplayName => SourceUIndex == 0
                ? "None"
                : string.IsNullOrWhiteSpace(ClientEffectPath)
                    ? $"Entry {SourceUIndex}"
                    : ClientEffectPath;
        }

        public ICollectionView ChoicesView { get; }
        public IReadOnlyList<string> PropNames { get; }
        public ICollectionView PropNamesView { get; }
        public ICollectionView ClientEffectChoicesView { get; }
        public PropActionChoice SelectedChoice { get; private set; }
        public ClientEffectChoice SelectedClientEffectChoice { get; private set; }

        public PropActionPickerDialog(
            IEnumerable<PropActionChoice> choices,
            Window owner,
            string initialProp = null,
            string initialAction = null,
            int initialClientEffectUIndex = 0,
            string initialClientEffectPath = null)
        {
            _initialProp = initialProp;
            _initialAction = initialAction;
            _initialClientEffectUIndex = initialClientEffectUIndex;
            _initialClientEffectPath = initialClientEffectPath;
            List<PropActionChoice> allChoices = choices.ToList();
            List<PropActionChoice> choiceList = allChoices
                .GroupBy(choice => $"{choice.Prop}\0{choice.Action}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(choice => choice.SourceTrackUIndex != 0)
                    .ThenByDescending(choice => choice.SourceClientEffectUIndex != 0)
                    .ThenByDescending(choice => choice.HasEffects)
                    .First())
                .OrderBy(choice => choice.Prop, StringComparer.OrdinalIgnoreCase)
                .ThenBy(choice => choice.Action, StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<ClientEffectChoice> clientEffectChoices = allChoices
                .Where(choice => choice.SourceClientEffectUIndex != 0)
                .Select(choice => new ClientEffectChoice(
                    choice.Prop,
                    choice.SourceClientEffectPackagePath ?? choice.SourcePackagePath,
                    choice.SourceClientEffectUIndex,
                    choice.ClientEffectPath))
                .GroupBy(choice => $"{choice.Prop}\0{choice.SourcePackagePath}\0{choice.SourceUIndex}\0{choice.ClientEffectPath}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(choice => choice.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            clientEffectChoices.Insert(0, new ClientEffectChoice(null, null, 0, null));
            DataContext = this;
            PropNames = choiceList
                .Select(choice => choice.Prop)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(prop => prop, StringComparer.OrdinalIgnoreCase)
                .ToList();
            PropNamesView = CollectionViewSource.GetDefaultView(PropNames);
            ChoicesView = CollectionViewSource.GetDefaultView(choiceList);
            ClientEffectChoicesView = CollectionViewSource.GetDefaultView(clientEffectChoices);
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

        private void ClientEffectFilterTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshClientEffectFilter();

        private void ClientEffectFilterTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && ClientEffectListBox.Items.Count > 0)
            {
                ClientEffectListBox.SelectedItem ??= ClientEffectListBox.Items[0];
                ClientEffectListBox.Focus();
                e.Handled = true;
            }
        }

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

            RefreshClientEffectFilter();
        }

        private void ChoicesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            SelectClientEffectForCurrentAction();
        }

        private void RefreshClientEffectFilter()
        {
            if (ClientEffectChoicesView is null || ClientEffectFilterTextBox is null || ClientEffectListBox is null)
            {
                return;
            }

            ClientEffectChoice selectedEffect = ClientEffectListBox.SelectedItem as ClientEffectChoice;
            string prop = PropListBox.SelectedItem as string;
            string filter = ClientEffectFilterTextBox.Text.Trim();
            ClientEffectChoicesView.Filter = item => item is ClientEffectChoice effect
                && (effect.SourceUIndex == 0 || effect.Prop.Equals(prop, StringComparison.OrdinalIgnoreCase))
                && (filter.Length == 0 || effect.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase));
            ClientEffectChoicesView.Refresh();

            ClientEffectListBox.SelectedItem = ClientEffectChoicesView.Cast<ClientEffectChoice>()
                .FirstOrDefault(effect => effect == selectedEffect);
            if (ClientEffectListBox.SelectedItem is null)
            {
                SelectClientEffectForCurrentAction();
            }
        }

        private void SelectClientEffectForCurrentAction()
        {
            if (ClientEffectChoicesView is null || ClientEffectListBox is null)
            {
                return;
            }

            PropActionChoice action = ChoicesGrid?.SelectedItem as PropActionChoice;
            ClientEffectChoice effectToSelect = ClientEffectChoicesView.Cast<ClientEffectChoice>()
                .FirstOrDefault(effect => !string.IsNullOrWhiteSpace(_initialClientEffectPath)
                                          && string.Equals(effect.ClientEffectPath, _initialClientEffectPath, StringComparison.OrdinalIgnoreCase)
                                          || string.IsNullOrWhiteSpace(_initialClientEffectPath)
                                          && _initialClientEffectUIndex != 0
                                          && effect.SourceUIndex == _initialClientEffectUIndex)
                ?? ClientEffectChoicesView.Cast<ClientEffectChoice>()
                    .FirstOrDefault(effect => action is not null
                                              && action.SourceClientEffectUIndex != 0
                                              && effect.SourceUIndex == action.SourceClientEffectUIndex
                                              && string.Equals(effect.SourcePackagePath, action.SourceClientEffectPackagePath ?? action.SourcePackagePath, StringComparison.OrdinalIgnoreCase))
                ?? ClientEffectChoicesView.Cast<ClientEffectChoice>().FirstOrDefault();
            ClientEffectListBox.SelectedItem = effectToSelect;
            if (effectToSelect is not null)
            {
                ClientEffectListBox.ScrollIntoView(effectToSelect);
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
            SelectedClientEffectChoice = ClientEffectListBox.SelectedItem as ClientEffectChoice;
            DialogResult = true;
        }
    }
}
