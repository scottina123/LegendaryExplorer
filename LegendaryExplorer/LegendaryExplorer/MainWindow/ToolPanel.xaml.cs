using System.Collections.Generic;

namespace LegendaryExplorer.MainWindow;

public partial class ToolPanel : ToolListControl
{
    public ToolPanel() => InitializeComponent();

    public override void setToolList(IEnumerable<Tool> enumerable)
    {
        base.setToolList(enumerable);
        ToolList.ItemsSource = tools;
    }
}
