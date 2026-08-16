using System.Globalization;
using System.Windows;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Workflows.Ports;

namespace LE1GalaxyMapEditor.Views;

public partial class CloneContentWindow : Window
{
    public CloneContentWindow(GalaxyMapRow source, int suggestedRowId, string suggestedLabel)
    {
        global::LE1GalaxyMapEditor.Theming.EditorThemeManager.Initialize(this);
        InitializeComponent();
        SummaryText.Text = $"Create a new module-owned copy of {source.Table} row {source.RowId}. The original remains untouched.";
        RowIdBox.Text = suggestedRowId.ToString(CultureInfo.InvariantCulture);
        LabelBox.Text = suggestedLabel;
        NameBox.Text = "0";
        NameTextBox.Text = $"Copy of {source switch { Cluster c => c.DisplayName, GalaxySystem s => s.DisplayName, Planet p => p.DisplayName, _ => source.Table.ToString() }}";
        ChildrenBox.Visibility = source is Planet ? Visibility.Collapsed : Visibility.Visible;
        ChildrenBox.Content = source is Cluster ? "Clone all Systems, Planets and linked rows" : "Clone all Planets and linked rows";
    }

    public CloneContentRequest? Result { get; private set; }

    private void Clone_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RowIdBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowId) || rowId < 0 ||
            !int.TryParse(NameBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var name) ||
            string.IsNullOrWhiteSpace(LabelBox.Text) || string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            ErrorText.Text = "Enter a non-negative Row ID, a whole-number TLK ID, a label and a display name.";
            return;
        }
        Result = new CloneContentRequest(rowId, LabelBox.Text.Trim(), name, NameTextBox.Text.Trim(), ChildrenBox.IsChecked == true);
        DialogResult = true;
    }
}
