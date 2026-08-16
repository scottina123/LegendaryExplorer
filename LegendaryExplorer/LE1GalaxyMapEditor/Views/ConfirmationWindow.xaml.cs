using System.Windows;
using LE1GalaxyMapEditor.Infrastructure;

namespace LE1GalaxyMapEditor.Views;

public enum ConfirmationChoice
{
    Primary,
    Secondary,
    Cancel
}

public partial class ConfirmationWindow : Window
{
    public ConfirmationWindow(
        string title,
        string message,
        string primaryText,
        string secondaryText,
        string? cancelText = null)
    {
        global::LE1GalaxyMapEditor.Theming.EditorThemeManager.Initialize(this);
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;
        SecondaryButton.Content = secondaryText;
        CancelButton.Content = cancelText ?? "Cancel";
        CancelButton.Visibility = cancelText is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public ConfirmationChoice Choice { get; private set; } = ConfirmationChoice.Cancel;

    private void PrimaryButton_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = ConfirmationChoice.Primary;
        DialogResult = true;
    }

    private void SecondaryButton_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = ConfirmationChoice.Secondary;
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = ConfirmationChoice.Cancel;
        DialogResult = false;
    }
}
