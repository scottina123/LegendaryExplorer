using System.Windows;
using System.Windows.Controls;

namespace LegendaryExplorer.MainWindow;

public sealed class ToolContextMenu : ContextMenu
{
    private readonly MenuItem favoriteItem = new();
    private readonly MenuItem taskbarPinItem = new();

    public ToolContextMenu()
    {
        SetResourceReference(StyleProperty, typeof(ContextMenu));
        Items.Add(favoriteItem);
        Items.Add(taskbarPinItem);
        favoriteItem.Click += FavoriteItem_Click;
        taskbarPinItem.Click += TaskbarPinItem_Click;
        Opened += (_, _) => RefreshTool();
        RefreshTool();
    }

    private Tool TargetTool => (PlacementTarget as FrameworkElement)?.DataContext as Tool;

    /// <summary>Refreshes actions for the tool under the menu.</summary>
    public void RefreshTool()
    {
        Tool tool = TargetTool;
        favoriteItem.Header = tool?.IsFavorited == true ? "Unfavorite" : "Favorite";
        favoriteItem.IsEnabled = tool != null;
        taskbarPinItem.Header = tool?.IsPinnedToTaskbar == true ? "Unpin from taskbar" : "Pin to taskbar";
        taskbarPinItem.IsEnabled = tool?.open != null;
    }

    private void FavoriteItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (TargetTool is Tool tool)
        {
            tool.IsFavorited = !tool.IsFavorited;
            RefreshTool();
        }
    }

    private void TaskbarPinItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (TargetTool is Tool { open: not null } tool)
        {
            tool.IsPinnedToTaskbar = !tool.IsPinnedToTaskbar;
            RefreshTool();
        }
    }
}
