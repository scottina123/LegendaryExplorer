using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Services;
using LE1GalaxyMapEditor.Workflows.Ports;
using LegendaryExplorerCore.Packages;

namespace LE1GalaxyMapEditor.Views;

public sealed record PlanetTemplateOption(PlanetCreationTemplate Value, string Label, string Detail)
{
    public override string ToString() => Label;
}

public partial class PlanetCreationWindow : Window
{
    private static readonly PlanetTemplateOption[] Options =
    [
        new(PlanetCreationTemplate.GenericPlanet, "Generic planet", "Ordinary planet, moon or giant. Scale is the only structural difference."),
        new(PlanetCreationTemplate.RingedPlanet, "Ringed planet", "Planet rendered with rings; configure Ring colour after creation if required."),
        new(PlanetCreationTemplate.AsteroidBelt, "Asteroid belt", "Vanilla belt structure: OrbitRing 2, no selection model and scale 0.01."),
        new(PlanetCreationTemplate.HiddenAnomaly, "Hidden anomaly", "Hidden anomaly such as a metallic/rocky asteroid or asteroid cluster."),
        new(PlanetCreationTemplate.AnomalyOrShip, "Anomaly / ship", "Visible anomaly or ship entry, including MSV-style destinations.")
    ];

    public PlanetCreationWindow(
        GalaxyMapTlkService? tlkService = null,
        MELocalization locale = MELocalization.INT)
    {
        global::LE1GalaxyMapEditor.Theming.EditorThemeManager.Initialize(this);
        InitializeComponent();
        TemplateBox.ItemsSource = Options;
        TemplateBox.SelectedIndex = 0;
        NameLookup.TlkService = tlkService;
        NameLookup.Locale = locale;
        ButtonLabelLookup.TlkService = tlkService;
        ButtonLabelLookup.Locale = locale;
    }

    public PlanetCreationRequest? Result { get; private set; }

    private void TemplateBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplateBox.SelectedItem is not PlanetTemplateOption option || ScaleBox is null)
        {
            return;
        }
        var probe = new Planet();
        GalaxyMapDefaults.ApplyPlanetTemplate(probe, option.Value);
        ScaleBox.Text = GalaxyMapNumber.FormatDisplay(probe.Scale);
        TemplateDetailText.Text = option.Detail;
        var canBeLandable = option.Value != PlanetCreationTemplate.AsteroidBelt;
        LandableContainer.Visibility = canBeLandable ? Visibility.Visible : Visibility.Collapsed;
        if (!canBeLandable)
        {
            LandableBox.IsChecked = false;
        }
        if (string.IsNullOrWhiteSpace(NameTextBox.Text) || NameTextBox.Text.StartsWith("New ", StringComparison.Ordinal))
        {
            NameTextBox.Text = GalaxyMapDefaults.DefaultPlanetName(option.Value);
        }
    }

    private void LandableBox_OnChanged(object sender, RoutedEventArgs e)
        => LandablePanel.Visibility = LandableBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void Create_OnClick(object sender, RoutedEventArgs e)
    {
        if (TemplateBox.SelectedItem is not PlanetTemplateOption option ||
            string.IsNullOrWhiteSpace(NameTextBox.Text) ||
            !int.TryParse(NameBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var name) ||
            !double.TryParse(ScaleBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) ||
            !GalaxyMapNumber.HasSupportedPrecision(scale) || scale <= 0)
        {
            ErrorText.Text = "Choose a template and enter an internal name, whole-number TLK ID, and positive scale using no more than two decimal places.";
            return;
        }

        LandableDestinationRequest? destination = null;
        if (option.Value != PlanetCreationTemplate.AsteroidBelt && LandableBox.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(MapNameBox.Text) || string.IsNullOrWhiteSpace(StartPointBox.Text) ||
                string.IsNullOrWhiteSpace(EventBox.Text))
            {
                ErrorText.Text = "A landable destination needs a persistent level, StartPoint and Remote Event.";
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
            destination = new LandableDestinationRequest(
                MapNameBox.Text.Trim(), StartPointBox.Text.Trim(), EventBox.Text.Trim(), buttonLabel,
                PlotPlanetBox.IsChecked == true);
        }

        Result = new PlanetCreationRequest(option.Value, NameTextBox.Text.Trim(), name, scale, destination);
        DialogResult = true;
    }
}
