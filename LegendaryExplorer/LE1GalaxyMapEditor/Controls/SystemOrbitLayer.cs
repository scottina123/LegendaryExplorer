using System.Collections;
using System.Windows;
using System.Windows.Media;
using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Controls;

public sealed class SystemOrbitLayer : FrameworkElement
{
    private static readonly Pen AsteroidGuidePen = CreatePen(Color.FromArgb(42, 0xCC, 0xB7, 0x91), 1);
    private static readonly Brush AsteroidBrush = CreateBrush(Color.FromArgb(185, 0xCC, 0xB7, 0x91));
    private static readonly Brush AsteroidHighlightBrush = CreateBrush(Color.FromArgb(235, 0xE8, 0xD8, 0xB8));

    public static readonly DependencyProperty PlanetsProperty = DependencyProperty.Register(
        nameof(Planets), typeof(IEnumerable), typeof(SystemOrbitLayer),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RefreshTokenProperty = DependencyProperty.Register(
        nameof(RefreshToken), typeof(int), typeof(SystemOrbitLayer),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Planets
    {
        get => (IEnumerable?)GetValue(PlanetsProperty);
        set => SetValue(PlanetsProperty, value);
    }

    public int RefreshToken
    {
        get => (int)GetValue(RefreshTokenProperty);
        set => SetValue(RefreshTokenProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Planets is null)
        {
            return;
        }

        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        foreach (var planet in Planets.OfType<Planet>().Where(planet => planet.OrbitRing != 0))
        {
            var point = new Point(planet.X * ActualWidth, planet.Y * ActualHeight);
            var radius = Math.Sqrt(Math.Pow(point.X - centre.X, 2) + Math.Pow(point.Y - centre.Y, 2));
            if (radius < 3)
            {
                continue;
            }

            if (planet.OrbitRing == 2)
            {
                DrawAsteroidBelt(drawingContext, centre, radius, planet.RowId);
            }
            else
            {
                var alpha = planet.OrbitRing == 1 ? (byte)65 : (byte)105;
                var thickness = planet.OrbitRing == 1 ? 1d : 1.5d;
                var pen = new Pen(
                    new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF)),
                    thickness);
                drawingContext.DrawEllipse(null, pen, centre, radius, radius);
            }
        }
    }

    private static void DrawAsteroidBelt(
        DrawingContext drawingContext,
        Point centre,
        double radius,
        int seed)
    {
        // Belt particles retain their original neutral sandstone colour. Only
        // the triangular anchor marker is provenance-coloured in SystemView.
        drawingContext.DrawEllipse(null, AsteroidGuidePen, centre, radius, radius);
        var count = Math.Clamp((int)(2 * Math.PI * radius / 8), 42, 150);
        var state = unchecked((uint)(seed * 747796405 + 2891336453));

        for (var index = 0; index < count; index++)
        {
            state = unchecked((state * 1664525) + 1013904223);
            var angleJitter = ((state >> 8) & 0xFFFF) / 65535d;
            state = unchecked((state * 1664525) + 1013904223);
            var radialJitter = ((((state >> 8) & 0xFFFF) / 65535d) - 0.5) * 12;
            state = unchecked((state * 1664525) + 1013904223);
            var size = 1.2 + (((state >> 8) & 0xFFFF) / 65535d * 2.2);

            var angle = ((index + (angleJitter * 0.7)) / count) * Math.PI * 2;
            var asteroidRadius = radius + radialJitter;
            var point = new Point(
                centre.X + (Math.Cos(angle) * asteroidRadius),
                centre.Y + (Math.Sin(angle) * asteroidRadius));
            drawingContext.DrawEllipse(
                index % 7 == 0 ? AsteroidHighlightBrush : AsteroidBrush,
                null,
                point,
                size,
                size * 0.75);
        }
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
