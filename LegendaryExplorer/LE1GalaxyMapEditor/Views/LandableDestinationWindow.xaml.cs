using System.Globalization;
using System.Windows;
using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Workflows.Ports;
using LE1GalaxyMapEditor.Services;
using LegendaryExplorerCore.Packages;

namespace LE1GalaxyMapEditor.Views;

public partial class LandableDestinationWindow : Window
{
    public LandableDestinationWindow(
        string mapName,
        string startPoint,
        string eventName,
        int? buttonLabel,
        bool canAddPlotPlanet,
        GalaxyMapTlkService? tlkService = null,
        MELocalization locale = MELocalization.INT)
    {
        global::LE1GalaxyMapEditor.Theming.EditorThemeManager.Initialize(this);
        InitializeComponent();
        MapNameBox.Text = string.IsNullOrWhiteSpace(mapName) ? "BIOA_NEW_MAP" : mapName;
        StartPointBox.Text = startPoint;
        EventBox.Text = string.IsNullOrWhiteSpace(eventName) ? "Land" : eventName;
        ButtonLabelBox.Text = buttonLabel?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ButtonLabelLookup.TlkService = tlkService;
        ButtonLabelLookup.Locale = locale;
        PlotPlanetBox.IsEnabled = canAddPlotPlanet;
        PlotPlanetBox.Visibility = canAddPlotPlanet ? Visibility.Visible : Visibility.Collapsed;
    }

    public LandableDestinationRequest? Result { get; private set; }

    private void Apply_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(MapNameBox.Text) || string.IsNullOrWhiteSpace(StartPointBox.Text) || string.IsNullOrWhiteSpace(EventBox.Text))
        {
            ErrorText.Text = "Enter a persistent level, StartPoint and Remote Event.";
            return;
        }
        int? buttonLabel = null;
        if (!string.IsNullOrWhiteSpace(ButtonLabelBox.Text))
        {
            if (!int.TryParse(ButtonLabelBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                ErrorText.Text = "Use button TLK must be a whole number or blank.";
                return;
            }
            buttonLabel = parsed;
        }
        Result = new LandableDestinationRequest(MapNameBox.Text.Trim(), StartPointBox.Text.Trim(), EventBox.Text.Trim(), buttonLabel, PlotPlanetBox.IsChecked == true);
        DialogResult = true;
    }
}
