namespace LE1GalaxyMapEditor.Services;

public readonly record struct PlanetPreviewPixelSize(int Width, int Height);

public static class PlanetPreviewResolution
{
    public static PlanetPreviewPixelSize Fit16By9(
        double availableWidth,
        double availableHeight,
        double dpiScaleX = 1,
        double dpiScaleY = 1)
    {
        var widthUnits = Math.Max(0, availableWidth) * Math.Max(0.01, dpiScaleX) / 16;
        var heightUnits = Math.Max(0, availableHeight) * Math.Max(0.01, dpiScaleY) / 9;
        var units = Math.Max(20, (int)Math.Floor(Math.Min(widthUnits, heightUnits)));
        return new PlanetPreviewPixelSize(units * 16, units * 9);
    }
}
