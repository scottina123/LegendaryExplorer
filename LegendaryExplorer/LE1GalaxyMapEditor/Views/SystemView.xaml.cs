using System.Windows.Controls;
using System.Windows.Input;
using LE1GalaxyMapEditor.ViewModels;

namespace LE1GalaxyMapEditor.Views;

public partial class SystemView : UserControl
{
    public SystemView()
    {
        InitializeComponent();
    }

    private void PlanetButton_OnMouseDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (sender is Button { DataContext: HierarchyNodeViewModel node } &&
            node.OpenPlanetDesignerCommand.CanExecute(null))
        {
            node.OpenPlanetDesignerCommand.Execute(null);
            eventArgs.Handled = true;
        }
    }
}
