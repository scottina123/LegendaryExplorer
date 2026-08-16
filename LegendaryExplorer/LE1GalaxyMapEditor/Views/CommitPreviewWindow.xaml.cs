using System.Windows;
using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Workflows.Queries;

namespace LE1GalaxyMapEditor.Views;

public partial class CommitPreviewWindow : Window
{
    public CommitPreviewWindow(CommitPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        global::LE1GalaxyMapEditor.Theming.EditorThemeManager.Initialize(this);
        InitializeComponent();
        DataContext = preview;
    }

    private void CommitButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
