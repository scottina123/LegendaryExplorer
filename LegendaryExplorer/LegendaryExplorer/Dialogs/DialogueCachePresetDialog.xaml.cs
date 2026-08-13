using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    private readonly Dictionary<Guid, DialogueCachePreset> loadedPresets = [];
    private readonly Dictionary<Guid, Task<DialogueCachePreset>> presetLoadTasks = [];
    private int selectionLoadVersion;

    public DialogueCachePreset SelectedPreset { get; private set; }

    public DialogueCachePresetDialog(Func<string, DialogueCachePreset> saveCurrent,
        Func<DialogueCachePreset, bool> canLoad)
    {
        InitializeComponent();
        CustomWindowChrome.ApplyCustomChrome(this);
        this.saveCurrent = saveCurrent;
        this.canLoad = canLoad;
        SaveCurrentButton.IsEnabled = saveCurrent is not null;
        presets = SavedDialogueCachePresetManager.LoadHeaders().ToList();
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

    private async void PresetListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int loadVersion = ++selectionLoadVersion;
        DialogueCachePreset preset = PresetListBox.SelectedItem as DialogueCachePreset;
        bool selected = preset is not null;
        DeleteButton.IsEnabled = selected;
        LoadButton.IsEnabled = false;
        SourcePathTextBlock.Text = preset?.SourceFilePath ?? "Select a cache preset to see its source file.";
        DialogueTextBlock.Text = preset is null
            ? string.Empty
            : preset.CachedNodeCount >= 0
                ? $"{preset.DialogueName} ({preset.NodeCount} cached node(s))"
                : preset.DialogueName;
        SavedTextBlock.Text = preset is null
            ? string.Empty
            : $"{preset.SavedDisplay}  |  PCC timestamp: {preset.SourceLastWriteUtc.ToLocalTime():g}";
        CachePathTextBlock.Text = preset?.CacheFilePath ?? string.Empty;
        if (!selected)
        {
            return;
        }

        StatusTextBlock.Text = $"Loading '{preset.Label}' details...";
        DialogueCachePreset loaded = loadedPresets.GetValueOrDefault(preset.Id);
        if (loaded is null)
        {
            if (!presetLoadTasks.TryGetValue(preset.Id, out Task<DialogueCachePreset> loadTask))
            {
                loadTask = Task.Run(() => SavedDialogueCachePresetManager.Load(preset));
                presetLoadTasks[preset.Id] = loadTask;
            }
            loaded = await loadTask;
        }
        if (!IsVisible || loadVersion != selectionLoadVersion
                       || !ReferenceEquals(PresetListBox.SelectedItem, preset))
        {
            return;
        }
        if (loaded is null)
        {
            StatusTextBlock.Text = "This cache file could not be read.";
            return;
        }

        loadedPresets[preset.Id] = loaded;
        DialogueTextBlock.Text = $"{loaded.DialogueName} ({loaded.NodeCount} cached node(s))";
        LoadButton.IsEnabled = canLoad?.Invoke(loaded) ?? false;
        if (!LoadButton.IsEnabled)
        {
            StatusTextBlock.Text = "This preset belongs to a different PCC, dialogue, or starting node.";
        }
        else
        {
            StatusTextBlock.Text = $"Ready to load '{loaded.Label}'.";
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
            loadedPresets[preset.Id] = preset;
            presets = SavedDialogueCachePresetManager.LoadHeaders().ToList();
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
            loadedPresets.Remove(preset.Id);
            presetLoadTasks.Remove(preset.Id);
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
            || !loadedPresets.TryGetValue(preset.Id, out DialogueCachePreset loaded)
            || !(canLoad?.Invoke(loaded) ?? false))
        {
            return;
        }
        SelectedPreset = loaded;
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
