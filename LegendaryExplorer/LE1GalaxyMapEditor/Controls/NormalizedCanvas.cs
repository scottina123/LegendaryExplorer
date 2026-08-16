using System.Windows;
using System.Windows.Controls;

namespace LE1GalaxyMapEditor.Controls;

public sealed class NormalizedCanvas : Panel
{
    public static readonly DependencyProperty XProperty = DependencyProperty.RegisterAttached(
        "X", typeof(double), typeof(NormalizedCanvas),
        new FrameworkPropertyMetadata(0.5d, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty YProperty = DependencyProperty.RegisterAttached(
        "Y", typeof(double), typeof(NormalizedCanvas),
        new FrameworkPropertyMetadata(0.5d, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty AnchorYProperty = DependencyProperty.RegisterAttached(
        "AnchorY", typeof(double), typeof(NormalizedCanvas),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty AnchorFromBottomProperty = DependencyProperty.RegisterAttached(
        "AnchorFromBottom", typeof(bool), typeof(NormalizedCanvas),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static void SetX(DependencyObject element, double value) => element.SetValue(XProperty, value);
    public static double GetX(DependencyObject element) => (double)element.GetValue(XProperty);
    public static void SetY(DependencyObject element, double value) => element.SetValue(YProperty, value);
    public static double GetY(DependencyObject element) => (double)element.GetValue(YProperty);
    public static void SetAnchorY(DependencyObject element, double value) => element.SetValue(AnchorYProperty, value);
    public static double GetAnchorY(DependencyObject element) => (double)element.GetValue(AnchorYProperty);
    public static void SetAnchorFromBottom(DependencyObject element, bool value) =>
        element.SetValue(AnchorFromBottomProperty, value);
    public static bool GetAnchorFromBottom(DependencyObject element) =>
        (bool)element.GetValue(AnchorFromBottomProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            var desired = child.DesiredSize;
            var left = GetX(child) * finalSize.Width - (desired.Width / 2);
            var anchorY = GetAnchorY(child);
            if (!double.IsNaN(anchorY) && GetAnchorFromBottom(child))
            {
                anchorY = desired.Height - anchorY;
            }
            var top = GetY(child) * finalSize.Height -
                      (double.IsNaN(anchorY) ? desired.Height / 2 : anchorY);
            child.Arrange(new Rect(new Point(left, top), desired));
        }

        return finalSize;
    }
}
