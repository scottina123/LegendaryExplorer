using System.Windows;
using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.ViewModels;

namespace LE1GalaxyMapEditor.Views;

public partial class PlanetTextureManagerWindow : Window
{
    public PlanetTextureManagerWindow(IReadOnlyList<PlanetTextureLinkOption> options)
    {
        global::LE1GalaxyMapEditor.Theming.EditorThemeManager.Initialize(this);
        InitializeComponent();
        DataContext = options;
        ValidationText.Text = options.Count == 0
            ? "No custom Planet textures are linked in the mounted workspace."
            : string.Empty;
        if (options.Count > 0)
        {
            TextureList.SelectedIndex = 0;
        }
    }

    public PlanetTextureLinkOption? SelectedOption { get; private set; }

    private void UnlinkButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (TextureList.SelectedItem is not PlanetTextureLinkOption { CanUnlink: true } option)
        {
            ValidationText.Text = "Select a texture from a writable module.";
            return;
        }

        SelectedOption = option;
        DialogResult = true;
    }
}
