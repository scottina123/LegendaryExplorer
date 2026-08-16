using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LE1GalaxyMapEditor.ViewModels;

namespace LE1GalaxyMapEditor.Views;

public partial class ClusterView : UserControl
{
    public ClusterView()
    {
        InitializeComponent();
    }

    private void System_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HierarchyNodeViewModel node } &&
            DataContext is ClusterViewModel viewModel && viewModel.EnterSystemCommand.CanExecute(node))
        {
            viewModel.EnterSystemCommand.Execute(node);
            e.Handled = true;
        }
    }
}
