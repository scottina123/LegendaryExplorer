using System.Windows;
using System.Windows.Controls;
using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Workflows.Ports;

namespace LE1GalaxyMapEditor.Views;

public partial class MoveDestinationWindow : Window
{
    public MoveDestinationWindow(GalaxyMapRow source, IReadOnlyList<MoveDestinationOption> options)
    {
        global::LE1GalaxyMapEditor.Theming.EditorThemeManager.Initialize(this);
        InitializeComponent();
        var isSystem = source is GalaxySystem;
        HeadingText.Text = isSystem ? "MOVE SYSTEM" : "MOVE PLANET / OBJECT";
        SummaryText.Text = isSystem
            ? "Choose the Cluster that should contain this module-owned System."
            : "Choose the System that should contain this module-owned Planet or object.";
        DestinationBox.ItemsSource = options;
        DestinationBox.SelectedIndex = options.Count > 0 ? 0 : -1;
        UpdatePreview();
    }

    public MoveDestinationOption? Result { get; private set; }

    private void Destination_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdatePreview();

    private void UpdatePreview()
    {
        if (DestinationBox.SelectedItem is not MoveDestinationOption option)
        {
            DestinationDetailText.Text = string.Empty;
            LabelResultText.Text = string.Empty;
            return;
        }

        DestinationDetailText.Text = option.Detail;
        LabelResultText.Text = string.Equals(option.CurrentLabel, option.ResultingLabel, StringComparison.OrdinalIgnoreCase)
            ? $"Label retained: {option.ResultingLabel}"
            : $"Label collision resolved automatically: {option.CurrentLabel} → {option.ResultingLabel}";
    }

    private void Move_OnClick(object sender, RoutedEventArgs e)
    {
        if (DestinationBox.SelectedItem is not MoveDestinationOption option)
        {
            return;
        }

        Result = option;
        DialogResult = true;
    }
}
