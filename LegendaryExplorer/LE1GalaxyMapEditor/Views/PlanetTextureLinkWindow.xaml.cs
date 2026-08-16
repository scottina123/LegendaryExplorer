using System.Windows;
using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Workflows.Editing;
using Microsoft.Win32;

namespace LE1GalaxyMapEditor.Views;

public partial class PlanetTextureLinkWindow : Window
{
    public PlanetTextureLinkWindow()
    {
        global::LE1GalaxyMapEditor.Theming.EditorThemeManager.Initialize(this);
        InitializeComponent();
        Loaded += (_, _) => InMemoryPathBox.Focus();
    }

    public PlanetTextureLinkRequest Request { get; private set; } =
        new(string.Empty, string.Empty, PlanetTextureCategory.None);

    private void BrowseButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose Planet preview texture",
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            SourcePathBox.Text = dialog.FileName;
        }
    }

    private void AcceptButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var categories = PlanetTextureCategory.None;
        if (ContinentCheckBox.IsChecked == true) categories |= PlanetTextureCategory.Continent;
        if (NormalsCheckBox.IsChecked == true) categories |= PlanetTextureCategory.Normals;
        if (OceanCheckBox.IsChecked == true) categories |= PlanetTextureCategory.Ocean;
        if (CityEmissiveCheckBox.IsChecked == true) categories |= PlanetTextureCategory.CityEmissive;
        if (AtmosphereCheckBox.IsChecked == true) categories |= PlanetTextureCategory.Atmosphere;

        var validationError = string.IsNullOrWhiteSpace(InMemoryPathBox.Text)
            ? "Enter the full in-memory texture path."
            : categories == PlanetTextureCategory.None
                ? "Select at least one material category."
                : string.IsNullOrWhiteSpace(SourcePathBox.Text)
                    ? "Browse for a local preview image."
                    : null;
        if (validationError is not null)
        {
            ValidationText.Text = validationError;
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        Request = new PlanetTextureLinkRequest(
            InMemoryPathBox.Text.Trim(),
            SourcePathBox.Text,
            categories);
        DialogResult = true;
    }
}
