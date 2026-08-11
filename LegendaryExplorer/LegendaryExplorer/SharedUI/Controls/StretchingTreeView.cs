using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LegendaryExplorer.SharedUI.Controls
{
    public class StretchingTreeView : TreeView
    {
        protected override DependencyObject GetContainerForItemOverride()
        {
            return new StretchingTreeViewItem();
        }

        protected override bool IsItemItsOwnContainerOverride(object item)
        {
            return item is StretchingTreeViewItem;
        }
    }

    public class StretchingTreeViewItem : TreeViewItem
    {
        public StretchingTreeViewItem()
        {
            this.Loaded += StretchingTreeViewItem_Loaded;
        }

        private void StretchingTreeViewItem_Loaded(object sender, RoutedEventArgs e)
        {
            // The purpose of this code is to stretch the Header Content all the way accross the TreeView. 
            if (VisualChildrenCount > 0)
            {
                if (GetVisualChild(0) is Grid grid && grid.ColumnDefinitions.Count == 3)
                {
                    // Remove the middle column which is set to Auto and let it get replaced with the 
                    // last column that is set to Star.
                    grid.ColumnDefinitions.RemoveAt(1);
                }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // TreeViewItem treats numpad minus as a built-in collapse command. This is
            // unexpected in LEX trees and is especially disruptive in the property editor.
            if (e.Key == Key.Subtract)
            {
                // Do not mark the event handled: text editors still need it to produce '-'.
                // Skipping the base implementation is enough to prevent the collapse.
                return;
            }

            base.OnKeyDown(e);
        }

        protected override DependencyObject GetContainerForItemOverride()
        {
            return new StretchingTreeViewItem();
        }

        protected override bool IsItemItsOwnContainerOverride(object item)
        {
            return item is StretchingTreeViewItem;
        }
    }
}
