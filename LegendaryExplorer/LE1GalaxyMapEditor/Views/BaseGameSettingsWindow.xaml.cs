using System.Windows;
using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Models;
using LegendaryExplorerCore.Packages;

namespace LE1GalaxyMapEditor.Views;

public partial class BaseGameSettingsWindow : Window
{
    public BaseGameSettingsWindow(MELocalization locale)
    {
        global::LE1GalaxyMapEditor.Theming.EditorThemeManager.Initialize(this);
        InitializeComponent();
        LocaleBox.ItemsSource = GalaxyMapModule.SupportedTlkLocales.OrderBy(value => value.ToString());
        LocaleBox.SelectedItem = locale;
    }

    public MELocalization SelectedLocale { get; private set; }

    private void Apply_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (LocaleBox.SelectedItem is not MELocalization locale)
        {
            return;
        }

        SelectedLocale = locale;
        DialogResult = true;
    }
}
