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

public partial class StringSelectorDialog : NotifyPropertyChangedWindowBase
{
    public ObservableCollectionExtended<string> AllItems { get; } = [];
    public ObservableCollectionExtended<string> FilteredItems { get; } = [];

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

    private string selectedItem;
    public string SelectedItem
    {
        get => selectedItem;
        set => SetProperty(ref selectedItem, value);
    }

    public string DirectionsText { get; }

    public ICommand OKCommand { get; private set; }

    private StringSelectorDialog(Control owner, string promptText, string titleText, IEnumerable<string> items, string defaultValue = "", bool topMost = false)
    {
        DirectionsText = promptText;
        Title = titleText;
        Topmost = topMost;

        AllItems.ReplaceAll(items?.Distinct(StringComparer.OrdinalIgnoreCase) ?? []);

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
        var dlg = new StringSelectorDialog(owner, promptText, titleText, items, defaultValue, topMost);
        return dlg.ShowDialog() == true ? dlg.SelectedItem ?? string.Empty : string.Empty;
    }

    private void LoadCommands()
    {
        OKCommand = new GenericCommand(AcceptSelection, CanAcceptSelection);
    }

    private bool CanAcceptSelection()
    {
        return !string.IsNullOrWhiteSpace(SelectedItem);
    }

    private void AcceptSelection()
    {
        DialogResult = true;
    }

    private void SetInitialSelection(string defaultValue)
    {
        SelectedItem = FilteredItems.FirstOrDefault(item => item.Equals(defaultValue, StringComparison.OrdinalIgnoreCase))
            ?? FilteredItems.FirstOrDefault();

        if (SelectedItem is not null)
        {
            SelectionListView.ScrollIntoView(SelectedItem);
        }
    }

    private void UpdateFilteredItems()
    {
        IEnumerable<string> items = AllItems;
        string search = SearchText?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            items = items.Where(item => item.Contains(search, StringComparison.OrdinalIgnoreCase));
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
