using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LE1GalaxyMapEditor.Controls;

public sealed class ColorWheelControl : FrameworkElement
{
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor), typeof(Color), typeof(ColorWheelControl),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Color SelectedColor { get => (Color)GetValue(SelectedColorProperty); set => SetValue(SelectedColorProperty, value); }
    public event EventHandler? ColorChanged;

    protected override void OnRender(DrawingContext dc)
    {
        var radius = Math.Max(1, Math.Min(ActualWidth, ActualHeight) / 2 - 5);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        for (var hue = 0; hue < 360; hue++)
        {
            var angle1 = (hue - 90) * Math.PI / 180;
            var angle2 = (hue + 1 - 90) * Math.PI / 180;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(center, true, true);
                context.LineTo(new Point(center.X + Math.Cos(angle1) * radius, center.Y + Math.Sin(angle1) * radius), true, false);
                context.LineTo(new Point(center.X + Math.Cos(angle2) * radius, center.Y + Math.Sin(angle2) * radius), true, false);
            }
            dc.DrawGeometry(Hsv(hue, 1, 1), null, geometry);
        }
        dc.DrawEllipse(new RadialGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255)), null, center, radius, radius);
        var (h, s, _) = ToHsv(SelectedColor);
        var a = (h - 90) * Math.PI / 180;
        var marker = new Point(center.X + Math.Cos(a) * radius * s, center.Y + Math.Sin(a) * radius * s);
        dc.DrawEllipse(null, new Pen(Brushes.White, 2), marker, 5, 5);
        dc.DrawEllipse(null, new Pen(Brushes.Black, 1), marker, 6, 6);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e) { CaptureMouse(); Pick(e.GetPosition(this)); }
    protected override void OnMouseMove(MouseEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) Pick(e.GetPosition(this)); }
    protected override void OnMouseUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); }

    private void Pick(Point point)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(1, Math.Min(ActualWidth, ActualHeight) / 2 - 5);
        var dx = point.X - center.X; var dy = point.Y - center.Y;
        var saturation = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / radius);
        var hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 90 + 360) % 360;
        var color = HsvColor(hue, saturation, 1); color.A = SelectedColor.A; SelectedColor = color;
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    public static SolidColorBrush Hsv(double hue, double saturation, double value) => new(HsvColor(hue, saturation, value));
    public static Color HsvColor(double hue, double saturation, double value)
    {
        var c = value * saturation; var x = c * (1 - Math.Abs((hue / 60) % 2 - 1)); var m = value - c;
        var (r, g, b) = hue switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
    public static (double Hue, double Saturation, double Value) ToHsv(Color color)
    {
        var r = color.R / 255d; var g = color.G / 255d; var b = color.B / 255d; var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min; var h = delta == 0 ? 0 : max == r ? 60 * (((g - b) / delta) % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (h < 0) h += 360; return (h, max == 0 ? 0 : delta / max, max);
    }
}
