using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LE1GalaxyMapEditor.Controls;

public sealed class CoordinateGridLayer : FrameworkElement
{
    public const int DivisionCount = 40;
    public const int MajorDivisionInterval = 10;
    public const double MinorIncrement = 1d / DivisionCount;
    public const double MajorIncrement = MajorDivisionInterval * MinorIncrement;

    public static IReadOnlyList<string> AxisLabels { get; } = Enumerable.Range(0, (DivisionCount / MajorDivisionInterval) + 1)
        .Select(index => (index * MajorIncrement).ToString("0.00", CultureInfo.InvariantCulture))
        .ToArray();

    public static IReadOnlyList<string> BottomAxisLabels { get; } = AxisLabels.Skip(1).ToArray();

    public static IReadOnlyList<string> LeftAxisLabels { get; } = AxisLabels.Take(AxisLabels.Count - 1).ToArray();

    private static readonly Brush LabelBrush = CreateBrush(Color.FromRgb(218, 239, 247));
    private static readonly Brush LabelBackground = CreateBrush(Color.FromArgb(185, 5, 13, 20));
    private static readonly Pen MinorGridPen = CreatePen(Color.FromArgb(45, 144, 211, 232), 0.75);
    private static readonly Pen MajorGridPen = CreatePen(Color.FromArgb(112, 144, 211, 232), 1);
    private static readonly Pen BorderPen = CreatePen(Color.FromArgb(185, 170, 229, 246), 1.5);
    private static readonly Pen CursorBorderPen = CreatePen(Color.FromArgb(185, 170, 229, 246), 1);
    private static readonly Typeface AxisTypeface = new("Segoe UI");
    private static readonly Typeface CursorTypeface = new("Consolas");

    private Point? _cursorPosition;

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid),
        typeof(bool),
        typeof(CoordinateGridLayer),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public CoordinateGridLayer()
    {
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                HideCursor();
            }
        };
    }

    public Point? CursorPosition => _cursorPosition;

    public bool ShowGrid
    {
        get => (bool)GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public static Point NormalizePosition(Point position, Size surfaceSize)
    {
        var x = NormalizeAxis(position.X, surfaceSize.Width);
        var y = NormalizeAxis(position.Y, surfaceSize.Height);
        return new Point(x, y);
    }

    public static string FormatCoordinates(Point normalizedPosition)
    {
        var x = Math.Clamp(normalizedPosition.X, 0, 1);
        var y = Math.Clamp(normalizedPosition.Y, 0, 1);
        return FormattableString.Invariant($"X {x:0.00}   Y {y:0.00}");
    }

    public static Point RoundNormalizedPosition(Point normalizedPosition)
        => new(
            Math.Round(Math.Clamp(normalizedPosition.X, 0, 1), 2, MidpointRounding.AwayFromZero),
            Math.Round(Math.Clamp(normalizedPosition.Y, 0, 1), 2, MidpointRounding.AwayFromZero));

    public void ShowCursor(Point position)
    {
        _cursorPosition = position;
        InvalidateVisual();
    }

    public void HideCursor()
    {
        if (_cursorPosition is null)
        {
            return;
        }

        _cursorPosition = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        if (ShowGrid)
        {
            for (var index = 0; index <= DivisionCount; index++)
            {
                var fraction = index / (double)DivisionCount;
                var x = PixelAligned(fraction * ActualWidth, ActualWidth);
                var y = PixelAligned(fraction * ActualHeight, ActualHeight);
                var pen = index is 0 or DivisionCount
                    ? BorderPen
                    : index % MajorDivisionInterval == 0 ? MajorGridPen : MinorGridPen;
                drawingContext.DrawLine(pen, new Point(x, 0), new Point(x, ActualHeight));
                drawingContext.DrawLine(pen, new Point(0, y), new Point(ActualWidth, y));
            }

            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (var labelIndex = 0; labelIndex < AxisLabels.Count; labelIndex++)
            {
                var text = AxisLabels[labelIndex];
                var formatted = new FormattedText(
                    text,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    AxisTypeface,
                    10,
                    LabelBrush,
                    pixelsPerDip);

                var fraction = labelIndex * MajorIncrement;
                var x = Math.Clamp((fraction * ActualWidth) - (formatted.Width / 2), 3, ActualWidth - formatted.Width - 3);
                var y = Math.Clamp((fraction * ActualHeight) - (formatted.Height / 2), 3, ActualHeight - formatted.Height - 3);

                if (labelIndex > 0)
                {
                    var xLabelTop = ActualHeight - formatted.Height - 3;
                    DrawLabel(drawingContext, formatted, new Point(x, xLabelTop), LabelBackground);
                }

                if (labelIndex < AxisLabels.Count - 1)
                {
                    DrawLabel(drawingContext, formatted, new Point(3, y), LabelBackground);
                }
            }
        }

        if (_cursorPosition is Point cursorPosition)
        {
            DrawCursorCoordinates(
                drawingContext,
                cursorPosition,
                LabelBrush,
                LabelBackground,
                CursorBorderPen,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }
    }

    private void DrawCursorCoordinates(
        DrawingContext drawingContext,
        Point cursorPosition,
        Brush foreground,
        Brush background,
        Pen border,
        double pixelsPerDip)
    {
        var normalizedPosition = NormalizePosition(cursorPosition, new Size(ActualWidth, ActualHeight));
        var formatted = new FormattedText(
            FormatCoordinates(normalizedPosition),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            CursorTypeface,
            11,
            foreground,
            pixelsPerDip);

        const double horizontalPadding = 6;
        const double verticalPadding = 3;
        const double cursorOffset = 14;
        const double edgePadding = 3;
        var width = formatted.Width + (horizontalPadding * 2);
        var height = formatted.Height + (verticalPadding * 2);

        var left = cursorPosition.X + cursorOffset;
        if (left + width > ActualWidth - edgePadding)
        {
            left = cursorPosition.X - cursorOffset - width;
        }

        var top = cursorPosition.Y + cursorOffset;
        if (top + height > ActualHeight - edgePadding)
        {
            top = cursorPosition.Y - cursorOffset - height;
        }

        left = ClampToSurface(left, width, ActualWidth, edgePadding);
        top = ClampToSurface(top, height, ActualHeight, edgePadding);

        var rect = new Rect(left, top, width, height);
        drawingContext.DrawRoundedRectangle(background, border, rect, 3, 3);
        drawingContext.DrawText(
            formatted,
            new Point(left + horizontalPadding, top + verticalPadding));
    }

    private static void DrawLabel(
        DrawingContext drawingContext,
        FormattedText text,
        Point origin,
        Brush background)
    {
        var rect = new Rect(origin.X - 2, origin.Y - 1, text.Width + 4, text.Height + 2);
        drawingContext.DrawRoundedRectangle(background, null, rect, 2, 2);
        drawingContext.DrawText(text, origin);
    }

    private static double PixelAligned(double value, double maximum)
        => Math.Clamp(Math.Round(value) + 0.5, 0.5, Math.Max(0.5, maximum - 0.5));

    private static double NormalizeAxis(double value, double length)
        => length > 0 && double.IsFinite(length) && double.IsFinite(value)
            ? Math.Clamp(value / length, 0, 1)
            : 0;

    private static double ClampToSurface(double value, double extent, double surfaceExtent, double padding)
    {
        var maximum = Math.Max(padding, surfaceExtent - extent - padding);
        return Math.Clamp(value, padding, maximum);
    }

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(Color color, double thickness)
    {
        var pen = new Pen(CreateBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
