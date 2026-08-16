using System.Windows;
using LE1GalaxyMapEditor.Infrastructure;

namespace LE1GalaxyMapEditor.Views;

public partial class PlanetTemplateWindow : Window
{
    public PlanetTemplateWindow()
    {
        global::LE1GalaxyMapEditor.Theming.EditorThemeManager.Initialize(this);
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    public string TemplateName => NameBox.Text.Trim();
    public string Description => DescriptionBox.Text.Trim();

    private void Save_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (TemplateName.Length == 0)
        {
            NameBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
