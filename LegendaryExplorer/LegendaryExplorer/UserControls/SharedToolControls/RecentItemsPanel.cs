using System;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace LegendaryExplorer.UserControls.SharedToolControls
{
    /// <summary>
    /// Fits recent items into equal-sized cells that fill the available space.
    /// </summary>
    public class RecentItemsPanel : UniformGrid
    {
        // Preserve the previous button width (252) and its horizontal margins (6)
        // as the preferred column width, then distribute any remaining space.
        private const double PreferredColumnWidth = 258;

        protected override Size MeasureOverride(Size availableSize)
        {
            int itemCount = Math.Max(1, InternalChildren.Count);
            Columns = double.IsInfinity(availableSize.Width)
                ? itemCount
                : Math.Max(1, (int)Math.Min(itemCount, Math.Floor(availableSize.Width / PreferredColumnWidth)));
            return base.MeasureOverride(availableSize);
        }
    }
}
