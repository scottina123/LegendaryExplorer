using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.Dialogue_Editor;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Dialogs;

public partial class SavedDialogueNodeLibraryDialog : Window
{
    private readonly List<SavedDialogueNode> _items;
    private readonly MEGame _destinationGame;

    public SavedDialogueNode SelectedNode { get; private set; }

    public SavedDialogueNodeLibraryDialog(MEGame destinationGame)
    {
        InitializeComponent();
        CustomWindowChrome.ApplyCustomChrome(this);
        _destinationGame = destinationGame;
        _items = SavedDialogueNodeLibrary.Load().ToList();
        RefreshItems();
        SearchTextBox.Focus();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshItems();

    private void RefreshItems()
    {
        if (NodeListBox == null)
        {
            return;
        }

        string search = SearchTextBox?.Text.Trim() ?? string.Empty;
        List<SavedDialogueNode> filtered = _items
            .Where(item => search.Length == 0
                || item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Game.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.NodeType.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (item.LinePreview?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        NodeListBox.ItemsSource = filtered;
        StatusTextBlock.Text = filtered.Count == _items.Count
            ? $"{filtered.Count} saved node(s)"
            : $"{filtered.Count} of {_items.Count} saved node(s)";
        NodeListBox.SelectedIndex = filtered.Count > 0 ? 0 : -1;
    }

    private void NodeListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool selected = NodeListBox.SelectedItem is SavedDialogueNode;
        RenameButton.IsEnabled = selected;
        DeleteButton.IsEnabled = selected;
        ApplyButton.IsEnabled = selected
            && ((SavedDialogueNode)NodeListBox.SelectedItem).Game == _destinationGame;
        if (selected && !ApplyButton.IsEnabled)
        {
            StatusTextBlock.Text = $"This node targets {((SavedDialogueNode)NodeListBox.SelectedItem).Game}; the current conversation is {_destinationGame}.";
        }
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (NodeListBox.SelectedItem is not SavedDialogueNode item || item.Game != _destinationGame)
        {
            return;
        }
        SelectedNode = item;
        DialogResult = true;
    }

    private void NodeListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ApplyButton.IsEnabled)
        {
            ApplyButton_Click(sender, e);
        }
    }

    private void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        if (NodeListBox.SelectedItem is not SavedDialogueNode item)
        {
            return;
        }
        string name = PromptDialog.Prompt(this, "Saved node name:", "Rename Saved Dialogue Node", item.Name)?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        SavedDialogueNodeLibrary.Rename(item, name);
        RefreshItems();
        NodeListBox.SelectedItem = item;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (NodeListBox.SelectedItem is not SavedDialogueNode item
            || MessageBox.Show(this, $"Delete saved node '{item.Name}'?", "Delete Saved Dialogue Node",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        SavedDialogueNodeLibrary.Delete(item);
        _items.Remove(item);
        RefreshItems();
    }
}
