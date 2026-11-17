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

    [STATestMethod]
    public void CodePaletteMatchesXamlTokens()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(App).Assembly);

        foreach ((string file, DarkThemePalette palette) in new[]
                 {
                     ("DarkTheme.xaml", DarkThemePalette.Dark),
                     ("ModernDarkTheme.xaml", DarkThemePalette.ModernDark)
                 })
        {
            ResourceDictionary theme = LoadTheme(file);
            Assert.AreEqual(theme["AppBackgroundColor"], DarkThemePalette.ToMedia(palette.AppBackground), file);
            Assert.AreEqual(theme["CanvasBackgroundColor"], DarkThemePalette.ToMedia(palette.Canvas), file);
            Assert.AreEqual(theme["TextColor"], DarkThemePalette.ToMedia(palette.Text), file);
            Assert.AreEqual(theme["PanelColor"], DarkThemePalette.ToMedia(palette.Panel), file);
            Assert.AreEqual(theme["StructuralBorderColor"], DarkThemePalette.ToMedia(palette.StructuralBorder), file);
            Assert.AreEqual(theme["AccentColor"], DarkThemePalette.ToMedia(palette.Accent), file);
        }
    }

    [STATestMethod]
    public void DarkTreeViewItemsKeepVirtualization()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(App).Assembly);

        foreach (string file in new[] { "DarkTheme.xaml", "ModernDarkTheme.xaml" })
        {
            var style = (Style)LoadTheme(file)[typeof(System.Windows.Controls.TreeViewItem)];
            bool virtualizes = false;
            foreach (TriggerBase trigger in style.Triggers)
            {
                if (trigger is Trigger { Property: var property, Value: true } t
                    && property == System.Windows.Controls.VirtualizingPanel.IsVirtualizingProperty)
                {
                    foreach (SetterBase setter in t.Setters)
                        virtualizes |= setter is Setter { Property: var p } && p == System.Windows.Controls.ItemsControl.ItemsPanelProperty;
                }
            }
            Assert.IsTrue(virtualizes, $"{file} TreeViewItem style must switch to a VirtualizingStackPanel when virtualizing.");
        }
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
