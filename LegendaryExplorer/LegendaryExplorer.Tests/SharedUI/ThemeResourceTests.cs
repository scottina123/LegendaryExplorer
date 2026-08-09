using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using LegendaryExplorer.Misc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.SharedUI;

[TestClass]
public class ThemeResourceTests
{
    [STATestMethod]
    public void DarkThemesResolveTheirOwnSemanticPalettes()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(App).Assembly);

        ResourceDictionary traditional = LoadTheme("DarkTheme.xaml");
        ResourceDictionary modern = LoadTheme("ModernDarkTheme.xaml");

        Assert.AreEqual(Color.FromRgb(0x1E, 0x1E, 0x1E), traditional["AppBackgroundColor"]);
        Assert.AreEqual(Color.FromRgb(0x0A, 0x10, 0x18), modern["AppBackgroundColor"]);
        Assert.AreEqual(Color.FromRgb(0x1E, 0x1E, 0x1E), traditional["CanvasBackgroundColor"]);
        Assert.AreEqual(Color.FromRgb(0x05, 0x08, 0x0D), modern["CanvasBackgroundColor"]);

        Assert.AreEqual(Color.FromArgb(0x44, 0xFF, 0xFE, 0xC4),
            ((SolidColorBrush)traditional["PropertyObjectSurfaceBrush"]).Color);
        Assert.AreEqual(Color.FromRgb(0x4C, 0x51, 0x0B),
            ((SolidColorBrush)modern["PropertyObjectSurfaceBrush"]).Color);
        Assert.AreEqual(Color.FromRgb(0x30, 0x32, 0x34),
            ((SolidColorBrush)traditional["ToolBoxAccentBrush"]).Color);
        Assert.AreEqual(Color.FromRgb(0x24, 0x3E, 0x4B),
            ((SolidColorBrush)modern["ToolBoxAccentBrush"]).Color);
    }

    [TestMethod]
    public void ThemeNamesAreValidatedAndClassified()
    {
        Assert.AreEqual(AppTheme.Light, ThemeManager.ParseThemeName(null));
        Assert.AreEqual(AppTheme.Light, ThemeManager.ParseThemeName("unsupported"));
        Assert.AreEqual(AppTheme.Light, ThemeManager.ParseThemeName("99"));
        Assert.AreEqual(AppTheme.Dark, ThemeManager.ParseThemeName("dark"));
        Assert.AreEqual(AppTheme.ModernDark, ThemeManager.ParseThemeName("ModernDark"));
        Assert.IsTrue(ThemeManager.IsDarkThemeName("Dark"));
        Assert.IsTrue(ThemeManager.IsDarkThemeName("ModernDark"));
        Assert.IsFalse(ThemeManager.IsDarkThemeName("Light"));
    }

    private static ResourceDictionary LoadTheme(string fileName) =>
        (ResourceDictionary)Application.LoadComponent(
            new Uri($"/LegendaryExplorer;component/{fileName}", UriKind.Relative));
}
