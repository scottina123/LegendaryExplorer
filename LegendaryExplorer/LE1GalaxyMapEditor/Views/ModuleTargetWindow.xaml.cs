using System.Windows;
using System.Windows.Input;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Infrastructure;

namespace LE1GalaxyMapEditor.Views;

public partial class ModuleTargetWindow : Window
{
    public ModuleTargetWindow(IReadOnlyList<GalaxyMapModule> modules, GalaxyMapModule? preferred = null)
    {
        global::LE1GalaxyMapEditor.Theming.EditorThemeManager.Initialize(this);
        InitializeComponent();
        ModuleList.ItemsSource = modules;
        ModuleList.SelectedItem = preferred is not null && modules.Contains(preferred)
            ? preferred
            : modules.FirstOrDefault();
    }

    public GalaxyMapModule? SelectedModule { get; private set; }

    private void AcceptButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ModuleList.SelectedItem is not GalaxyMapModule module)
        {
            return;
        }

        SelectedModule = module;
        DialogResult = true;
    }

    private void ModuleList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
        => AcceptButton_OnClick(sender, e);
}
