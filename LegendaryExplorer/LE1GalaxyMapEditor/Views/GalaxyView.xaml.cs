using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LE1GalaxyMapEditor.ViewModels;

namespace LE1GalaxyMapEditor.Views;

public partial class GalaxyView : UserControl
{
    public GalaxyView()
    {
        InitializeComponent();
    }

    private void Cluster_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is GalaxyViewModel { IsAddingRelay: true })
        {
            e.Handled = true;
            return;
        }

        if (sender is FrameworkElement { DataContext: HierarchyNodeViewModel node } &&
            DataContext is GalaxyViewModel viewModel && viewModel.EnterClusterCommand.CanExecute(node))
        {
            viewModel.EnterClusterCommand.Execute(node);
            e.Handled = true;
        }
    }
}
