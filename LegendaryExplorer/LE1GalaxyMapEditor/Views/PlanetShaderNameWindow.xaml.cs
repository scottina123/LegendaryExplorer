using System.Windows;
using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Workflows.Ports;

namespace LE1GalaxyMapEditor.Views;

public partial class PlanetShaderNameWindow : Window
{
    private readonly PlanetShaderNameRequest _request;

    public PlanetShaderNameWindow(PlanetShaderNameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        global::LE1GalaxyMapEditor.Theming.EditorThemeManager.Initialize(this);
        InitializeComponent();
        _request = request;
        ExplanationText.Text =
            $"{request.PlanetName} (Planet row {request.PlanetRowId}) will be copied into " +
            $"{request.TargetModule.Tag}. Third-party Planet appearances require a unique Shader name.";
        ShaderNameBox.Text = request.SuggestedName;
        Loaded += (_, _) =>
        {
            ShaderNameBox.Focus();
            ShaderNameBox.SelectAll();
        };
    }

    public string ShaderName => ShaderNameBox.Text.Trim();

    private void AcceptButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var validationError = _request.Validate(ShaderName);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            ValidationText.Text = validationError;
            ValidationText.Visibility = Visibility.Visible;
            ShaderNameBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
