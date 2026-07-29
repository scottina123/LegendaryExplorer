using System;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.InterpEditor;

namespace LegendaryExplorer.Dialogs;

public partial class MulticamPresetSaveDialog : Window
{
    private readonly Func<string, bool> _nameExists;

    public string PresetName => PresetNameTextBox.Text.Trim();
    public string Description => DescriptionTextBox.Text.Trim();
    public MulticamPresetType PresetType =>
        TypeComboBox.SelectedItem is ComboBoxItem { Tag: string tag }
        && Enum.TryParse(tag, out MulticamPresetType type)
            ? type
            : MulticamPresetType.StaticToDynamic;

    public MulticamPresetSaveDialog(MulticamPresetType inferredType, Func<string, bool> nameExists)
    {
        _nameExists = nameExists;
        InitializeComponent();
        CustomWindowChrome.ApplyCustomChrome(this);
        TypeComboBox.SelectedIndex = inferredType == MulticamPresetType.StaticToDynamic ? 0 : 1;
        Loaded += (_, _) => PresetNameTextBox.Focus();
        Validate();
    }

    private void Input_Changed(object sender, TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        if (SaveButton is null)
        {
            return;
        }

        string message = string.IsNullOrWhiteSpace(PresetNameTextBox.Text)
            ? "Enter a preset name."
            : _nameExists?.Invoke(PresetNameTextBox.Text.Trim()) == true
                ? $"A multicam preset named '{PresetNameTextBox.Text.Trim()}' already exists."
                : null;
        ValidationTextBlock.Text = message ?? string.Empty;
        SaveButton.IsEnabled = message is null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Validate();
        if (SaveButton.IsEnabled)
        {
            DialogResult = true;
        }
    }
}
