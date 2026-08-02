using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore.Misc;

namespace LegendaryExplorer.Dialogs;

public sealed record StringSelectorItem(string Value, string DisplayName, string Subtitle = null)
{
    public string SearchText => string.Join('\n', Value, DisplayName, Subtitle);
}

public partial class StringSelectorDialog : NotifyPropertyChangedWindowBase
{
    public ObservableCollectionExtended<StringSelectorItem> AllItems { get; } = [];
    public ObservableCollectionExtended<StringSelectorItem> FilteredItems { get; } = [];

    private string searchText;
    public string SearchText
    {
        get => searchText;
        set
        {
            if (SetProperty(ref searchText, value))
            {
                UpdateFilteredItems();
            }
        }
    }

    private StringSelectorItem selectedItem;
    public StringSelectorItem SelectedItem
    {
        get => selectedItem;
        set => SetProperty(ref selectedItem, value);
    }

    public string DirectionsText { get; }

    public ICommand OKCommand { get; private set; }

    private StringSelectorDialog(Control owner, string promptText, string titleText, IEnumerable<StringSelectorItem> items,
        string defaultValue = "", bool topMost = false)
    {
        DirectionsText = promptText;
        Title = titleText;
        Topmost = topMost;

        AllItems.ReplaceAll(items?.DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase) ?? []);

        DataContext = this;
        LoadCommands();
        InitializeComponent();

        if (owner != null)
        {
            Owner = owner as Window ?? GetWindow(owner);
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        UpdateFilteredItems();
        SetInitialSelection(defaultValue);
        SearchTextBox.Focus();
    }

    public static string GetValue(Control owner, string promptText, string titleText, IEnumerable<string> items, string defaultValue = "", bool topMost = false)
    {
        IEnumerable<StringSelectorItem> selectorItems = items?.Select(item => new StringSelectorItem(item, item));
        return GetValue(owner, promptText, titleText, selectorItems, defaultValue, topMost);
    }

    public static string GetValue(Control owner, string promptText, string titleText, IEnumerable<StringSelectorItem> items,
        string defaultValue = "", bool topMost = false)
    {
        var dlg = new StringSelectorDialog(owner, promptText, titleText, items, defaultValue, topMost);
        return dlg.ShowDialog() == true ? dlg.SelectedItem?.Value ?? string.Empty : string.Empty;
    }

    private void LoadCommands()
    {
        OKCommand = new GenericCommand(AcceptSelection, CanAcceptSelection);
    }

    private bool CanAcceptSelection()
    {
        return !string.IsNullOrWhiteSpace(SelectedItem?.Value);
    }

    private void AcceptSelection()
    {
        DialogResult = true;
    }

    private void SetInitialSelection(string defaultValue)
    {
        SelectedItem = FilteredItems.FirstOrDefault(item => item.Value.Equals(defaultValue, StringComparison.OrdinalIgnoreCase))
            ?? FilteredItems.FirstOrDefault();

        if (SelectedItem is not null)
        {
            SelectionListView.ScrollIntoView(SelectedItem);
        }
    }

    private void UpdateFilteredItems()
    {
        IEnumerable<StringSelectorItem> items = AllItems;
        string search = SearchText?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            items = items.Where(item => item.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        FilteredItems.ReplaceAll(items);

        if (SelectedItem is not null && FilteredItems.Contains(SelectedItem))
        {
            return;
        }

        SelectedItem = FilteredItems.FirstOrDefault();
    }

    private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && OKCommand.CanExecute(null))
        {
            OKCommand.Execute(null);
        }
        else if (e.Key == Key.Down && FilteredItems.Count > 0)
        {
            SelectionListView.Focus();
        }
    }

    private void SelectionListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (OKCommand.CanExecute(null))
        {
            OKCommand.Execute(null);
        }
    }

    private void SelectionListView_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && OKCommand.CanExecute(null))
        {
            OKCommand.Execute(null);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
