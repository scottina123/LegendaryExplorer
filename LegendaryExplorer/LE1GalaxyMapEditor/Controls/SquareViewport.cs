using System.Windows;
using System.Windows.Controls;

namespace LE1GalaxyMapEditor.Controls;

public sealed class SquareViewport : Decorator
{
    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null)
        {
            return new Size();
        }

        var width = double.IsInfinity(constraint.Width) ? double.PositiveInfinity : Math.Max(0, constraint.Width);
        var height = double.IsInfinity(constraint.Height) ? double.PositiveInfinity : Math.Max(0, constraint.Height);
        var side = Math.Min(width, height);

        if (double.IsInfinity(side))
        {
            Child.Measure(constraint);
            side = Math.Max(Child.DesiredSize.Width, Child.DesiredSize.Height);
        }
        else
        {
            Child.Measure(new Size(side, side));
        }

        var desiredWidth = double.IsInfinity(width) ? side : width;
        var desiredHeight = double.IsInfinity(height) ? side : height;
        return new Size(desiredWidth, desiredHeight);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is null)
        {
            return arrangeSize;
        }

        var side = Math.Max(0, Math.Min(arrangeSize.Width, arrangeSize.Height));
        var left = (arrangeSize.Width - side) / 2;
        var top = (arrangeSize.Height - side) / 2;
        Child.Arrange(new Rect(left, top, side, side));
        return arrangeSize;
    }
}
