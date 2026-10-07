using System.Collections.Generic;
using System.Reflection;
using LegendaryExplorer.Misc.AppSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.SharedUI;

[TestClass]
public class SettingsThemeTests
{
    [TestMethod]
    public void FreshProfilesUseDarkTheme()
    {
        Assert.AreEqual("Dark", GetSavedTheme(new Dictionary<string, object>()));
        Assert.AreEqual("Dark", GetSavedTheme(new Dictionary<string, object>
        {
            ["global_analytics_enabled"] = "False"
        }));
    }

    [TestMethod]
    [DataRow("True", "Dark")]
    [DataRow("False", "Light")]
    public void LegacyProfilesPreserveTheirTheme(string legacyDarkMode, string expectedTheme)
    {
        Assert.AreEqual(expectedTheme, GetSavedTheme(new Dictionary<string, object>
        {
            ["global_darkmode_enabled"] = legacyDarkMode
        }));
    }

    [TestMethod]
    [DataRow("Light", "True")]
    [DataRow("Dark", "False")]
    [DataRow("ModernDark", "False")]
    public void SavedThemeTakesPrecedenceOverLegacyPreference(string theme, string legacyDarkMode)
    {
        Assert.AreEqual(theme, GetSavedTheme(new Dictionary<string, object>
        {
            ["global_theme"] = theme,
            ["global_darkmode_enabled"] = legacyDarkMode
        }));
    }

    [TestMethod]
    public void UnknownSavedThemePreservesLightFallback()
    {
        Assert.AreEqual("Light", GetSavedTheme(new Dictionary<string, object>
        {
            ["global_theme"] = "unsupported",
            ["global_darkmode_enabled"] = "True"
        }));
    }

    private static string GetSavedTheme(Dictionary<string, object> settings)
    {
        MethodInfo method = typeof(Settings).GetMethod("GetThemeSetting", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.IsNotNull(method, "Settings must resolve the theme before applying saved settings.");
        return (string)method.Invoke(null, new object[] { settings })!;
    }
}
