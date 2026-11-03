using System.Windows;
using System.Windows.Controls;

namespace LegendaryExplorer.MainWindow;

public partial class ToolTile : UserControl
{
    public static readonly DependencyProperty IsFlatProperty = DependencyProperty.Register(
        nameof(IsFlat), typeof(bool), typeof(ToolTile), new PropertyMetadata(false));

    public bool IsFlat
    {
        get => (bool)GetValue(IsFlatProperty);
        set => SetValue(IsFlatProperty, value);
    }

    public ToolTile() => InitializeComponent();
}
