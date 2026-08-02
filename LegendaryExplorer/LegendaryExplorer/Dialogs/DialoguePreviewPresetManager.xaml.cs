using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.Dialogue_Editor;
using LegendaryExplorerCore.Dialogue;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Dialogs;

public partial class DialoguePreviewPresetManager : Window
{
    private readonly ConversationExtended conversation;
    private readonly DialogueNodeExtended startNode;
    private readonly IReadOnlyList<string> levelPaths;
    private readonly string storageFolder;
    private readonly List<DialoguePreviewPreset> presets;

    public DialoguePreviewPreset SelectedPreset { get; private set; }

    public DialoguePreviewPresetManager(
        ConversationExtended conversation,
        DialogueNodeExtended startNode,
        IReadOnlyList<string> levelPaths)
    {
        this.conversation = conversation;
        this.startNode = startNode;
        this.levelPaths = levelPaths;
        storageFolder = DialoguePreviewPresetLibrary.GetStorageFolder(conversation, levelPaths);
        presets = DialoguePreviewPresetLibrary.Load(storageFolder).ToList();

        InitializeComponent();
        CustomWindowChrome.ApplyCustomChrome(this);
        RefreshItems();
    }

    private void RefreshItems(DialoguePreviewPreset selection = null)
    {
        PresetListBox.ItemsSource = null;
        PresetListBox.ItemsSource = presets.OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        PresetListBox.SelectedItem = selection;
        StatusTextBlock.Text = $"{presets.Count} preset(s) in {storageFolder}";
    }

    private void PresetListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool selected = PresetListBox.SelectedItem is DialoguePreviewPreset;
        RenameButton.IsEnabled = selected;
        DeleteButton.IsEnabled = selected;
        LoadButton.IsEnabled = selected;
    }

    private void PresetListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LoadButton.IsEnabled)
        {
            LoadButton_Click(sender, e);
        }
    }

    private void SaveCurrentButton_Click(object sender, RoutedEventArgs e)
    {
        string defaultName = conversation.Export.ObjectName.Name;
        string name = PromptDialog.Prompt(this, "Preset name:", "Save Dialogue Preview Preset", defaultName)?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            DialoguePreviewPreset preset = DialoguePreviewPresetLibrary.Capture(conversation, startNode, name, levelPaths);
            presets.Add(preset);
            RefreshItems(preset);
            SelectedPreset = preset;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"The preset could not be saved.\n\n{exception.Message}",
                "Save Dialogue Preview Preset", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        if (PresetListBox.SelectedItem is not DialoguePreviewPreset preset)
        {
            return;
        }

        string name = PromptDialog.Prompt(this, "Preset name:", "Rename Dialogue Preview Preset", preset.Name)?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        DialoguePreviewPresetLibrary.Rename(preset, name);
        RefreshItems(preset);
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (PresetListBox.SelectedItem is not DialoguePreviewPreset preset
            || MessageBox.Show(this, $"Delete dialogue preview preset '{preset.Name}'?", "Delete Dialogue Preview Preset",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        DialoguePreviewPresetLibrary.Delete(preset);
        presets.Remove(preset);
        RefreshItems();
    }

    private void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (PresetListBox.SelectedItem is not DialoguePreviewPreset preset)
        {
            return;
        }

        string[] missingLevels = preset.LevelPaths.Where(path => !File.Exists(path)).ToArray();
        if (missingLevels.Length > 0)
        {
            MessageBox.Show(this,
                $"This preset references missing level packages:\n\n{string.Join("\n", missingLevels)}",
                "Missing Preview Levels", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedPreset = preset;
        DialogResult = true;
    }
}
