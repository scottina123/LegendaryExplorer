using System.Windows;
using System.Windows.Controls;

namespace LegendaryExplorer.MainWindow;

public sealed class ToolContextMenu : ContextMenu
{
    private readonly MenuItem favoriteItem = new();

    public ToolContextMenu()
    {
        SetResourceReference(StyleProperty, typeof(ContextMenu));
        Items.Add(favoriteItem);
        favoriteItem.Click += FavoriteItem_Click;
        Opened += (_, _) => RefreshTool();
        RefreshTool();
    }

    private Tool TargetTool => (PlacementTarget as FrameworkElement)?.DataContext as Tool;

    /// <summary>Refreshes actions for the tool under the menu.</summary>
    public void RefreshTool()
    {
        Tool tool = TargetTool;
        favoriteItem.Header = tool?.IsFavorited == true ? "Unfavorite" : "Favorite";
        favoriteItem.IsEnabled = tool?.open != null;
        favoriteItem.ToolTip = "Favorites appear in the taskbar. Shift-click a tool to toggle it.";
    }

    private void FavoriteItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (TargetTool is Tool { open: not null } tool)
        {
            tool.IsFavorited = !tool.IsFavorited;
            RefreshTool();
        }
    }

}
