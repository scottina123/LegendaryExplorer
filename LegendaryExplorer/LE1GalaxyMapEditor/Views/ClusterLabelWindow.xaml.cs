using System.Windows;
using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Workflows.Ports;

namespace LE1GalaxyMapEditor.Views;

public partial class ClusterLabelWindow : Window
{
    private readonly ClusterLabelRequest _request;

    public ClusterLabelWindow(ClusterLabelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        global::LE1GalaxyMapEditor.Theming.EditorThemeManager.Initialize(this);
        InitializeComponent();
        _request = request;
        ClusterLabelBox.Text = request.SuggestedLabel;
        MountedLabelsText.Text = request.MountedLabels.Count == 0
            ? "No numbered Cluster labels are currently mounted."
            : string.Join(Environment.NewLine, request.MountedLabels);
        Loaded += (_, _) =>
        {
            ClusterLabelBox.Focus();
            ClusterLabelBox.SelectAll();
        };
    }

    public string ClusterLabel => ClusterLabelBox.Text.Trim();

    private void AcceptButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var error = _request.Validate(ClusterLabel);
        if (!string.IsNullOrWhiteSpace(error))
        {
            ValidationText.Text = error;
            ValidationText.Visibility = Visibility.Visible;
            ClusterLabelBox.Focus();
            ClusterLabelBox.SelectAll();
            return;
        }

        DialogResult = true;
    }
}
