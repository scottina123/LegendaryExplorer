using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.InterpEditor;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Dialogs;

public partial class DialogueCachePresetDialog : Window
{
    private readonly Func<string, DialogueCachePreset> saveCurrent;
    private readonly Func<DialogueCachePreset, bool> canLoad;
    private List<DialogueCachePreset> presets;

    public DialogueCachePreset SelectedPreset { get; private set; }

    public DialogueCachePresetDialog(Func<string, DialogueCachePreset> saveCurrent,
        Func<DialogueCachePreset, bool> canLoad, bool chooseBeforeBuild = false)
    {
        InitializeComponent();
        CustomWindowChrome.ApplyCustomChrome(this);
        this.saveCurrent = saveCurrent;
        this.canLoad = canLoad;
        SaveCurrentButton.IsEnabled = saveCurrent is not null;
        if (chooseBeforeBuild)
        {
            Title = "Choose Dialogue Cache";
            SaveCurrentButton.Visibility = Visibility.Collapsed;
            CloseButton.Content = "Build New Cache";
            CloseButton.MinWidth = 120;
        }
        presets = SavedDialogueCachePresetManager.LoadAll().ToList();
        RefreshItems();
        SearchTextBox.Focus();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshItems();

    private void RefreshItems(DialogueCachePreset select = null)
    {
        if (PresetListBox is null)
        {
            return;
        }

        string search = SearchTextBox?.Text.Trim() ?? string.Empty;
        List<DialogueCachePreset> filtered = presets
            .Where(preset => search.Length == 0
                || preset.Label.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || (preset.PccName?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || (preset.DialogueName?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || (preset.SourceFilePath?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || preset.SavedDisplay.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(preset => preset.SavedUtc)
            .ToList();
        PresetListBox.ItemsSource = filtered;
        StatusTextBlock.Text = filtered.Count == presets.Count
            ? $"{filtered.Count} saved cache preset(s)"
            : $"{filtered.Count} of {presets.Count} saved cache preset(s)";
        PresetListBox.SelectedItem = select is not null && filtered.Contains(select)
            ? select
            : filtered.FirstOrDefault();
    }

    private void PresetListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DialogueCachePreset preset = PresetListBox.SelectedItem as DialogueCachePreset;
        bool selected = preset is not null;
        DeleteButton.IsEnabled = selected;
        LoadButton.IsEnabled = selected && (canLoad?.Invoke(preset) ?? false);
        SourcePathTextBlock.Text = preset?.SourceFilePath ?? "Select a cache preset to see its source file.";
        DialogueTextBlock.Text = preset is null
            ? string.Empty
            : $"{preset.DialogueName} ({preset.Nodes.Count} cached node(s))";
        SavedTextBlock.Text = preset is null
            ? string.Empty
            : $"{preset.SavedDisplay}  |  PCC timestamp: {preset.SourceLastWriteUtc.ToLocalTime():g}";
        CachePathTextBlock.Text = preset?.CacheFilePath ?? string.Empty;
        if (selected && !LoadButton.IsEnabled)
        {
            StatusTextBlock.Text = "This preset belongs to a different PCC, dialogue, or starting node.";
        }
    }

    private void SaveCurrentButton_Click(object sender, RoutedEventArgs e)
    {
        string label = PromptDialog.Prompt(this, "Cache label:", "Save Dialogue Cache Preset")?.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        try
        {
            DialogueCachePreset preset = saveCurrent?.Invoke(label);
            if (preset is null)
            {
                return;
            }
            presets = SavedDialogueCachePresetManager.LoadAll().ToList();
            DialogueCachePreset saved = presets.FirstOrDefault(item => item.Id == preset.Id);
            RefreshItems(saved);
            StatusTextBlock.Text = $"Saved '{preset.Label}'.";
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or System.IO.InvalidDataException
                                          or System.IO.IOException
                                          or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Unable to save the dialogue cache: {exception.Message}",
                "Save Dialogue Cache", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (PresetListBox.SelectedItem is not DialogueCachePreset preset
            || MessageBox.Show(this,
                $"Delete dialogue cache preset '{preset.Label}'?\n\n{preset.CacheFilePath}",
                "Delete Dialogue Cache", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SavedDialogueCachePresetManager.Delete(preset);
            presets.Remove(preset);
            RefreshItems();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or System.IO.IOException
                                          or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Unable to delete the dialogue cache: {exception.Message}",
                "Delete Dialogue Cache", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (PresetListBox.SelectedItem is not DialogueCachePreset preset
            || !(canLoad?.Invoke(preset) ?? false))
        {
            return;
        }
        SelectedPreset = preset;
        DialogResult = true;
    }

    private void PresetListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LoadButton.IsEnabled)
        {
            LoadButton_Click(sender, e);
        }
    }
}
