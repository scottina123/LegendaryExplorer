using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LegendaryExplorer.MainWindow;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.MainWindow;

[TestClass]
[DoNotParallelize]
public class InitialSetupThemeTests
{
    [STATestMethod]
    public void FreshSetupUsesDarkAndTracksThemeChangesWithoutClippingControls()
    {
        Assert.AreEqual("Dark", Settings.Global_Theme,
            "A new process with no loaded preferences must start with the Dark theme.");
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(InitialSetup).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));

        var loadedField = typeof(Settings).GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
        object previousLoaded = loadedField.GetValue(null);
        string previousTheme = Settings.Global_Theme;
        loadedField.SetValue(null, false); // Theme changes must not save the user's preferences.
        InitialSetup window = null;
        try
        {
            ThemeManager.ApplyTheme();
            window = new InitialSetup
            {
                Left = -10000,
                Top = -10000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            var pathBoxes = new[] { "me1PathBox", "me2PathBox", "me3PathBox", "melePathBox" }
                .Select(name => (TextBox)window.FindName(name)).ToArray();
            foreach (TextBox box in pathBoxes)
            {
                // Emulate undetected game paths without displaying local machine paths.
                box.Clear();
                box.ClearValue(Control.BorderBrushProperty);
            }
            window.Show();
            FlushDispatcher();

            var content = (StackPanel)window.Content;
            var setupImage = (Image)window.FindName("setupImage");
            Assert.IsInstanceOfType<BitmapSource>(setupImage.Source,
                "The setup logo must load from the embedded splash image.");
            Assert.IsTrue(((BitmapSource)setupImage.Source).PixelWidth > 0);
            var welcomePanel = (Grid)((StackPanel)content.Children[0]).Children[1];
            var welcomeText = (TextBlock)welcomePanel.Children[0];
            var heading = (TextBlock)window.FindName("step1TextBlock");
            var pathGrid = (Grid)pathBoxes[0].Parent;
            var browseButtons = pathGrid.Children.OfType<Button>().ToArray();
            var continueButton = (Button)window.FindName("doneButton");
            Assert.AreEqual(4, browseButtons.Length);
            Assert.IsNotNull(window.Template.FindName("TitleBar", window),
                "The real setup preview must include the shared window title bar.");

            foreach (AppTheme theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.ModernDark })
            {
                Settings.Global_Theme = theme.ToString();
                ThemeManager.ApplyTheme();
                FlushDispatcher();

                AssertResourceBrush(window, window.Background, SystemColors.WindowBrushKey, $"{theme} window background");
                AssertResourceBrush(window, window.Foreground, SystemColors.WindowTextBrushKey, $"{theme} window text");
                AssertResourceBrush(window, welcomePanel.Background, SystemColors.ControlBrushKey, $"{theme} welcome background");
                AssertResourceBrush(window, welcomeText.Foreground, SystemColors.ControlTextBrushKey, $"{theme} welcome text");
                AssertResourceBrush(window, heading.Foreground, SystemColors.ControlTextBrushKey, $"{theme} heading text");
                AssertResourceBrush(window, ((Label)heading.Parent).Background, SystemColors.ControlLightBrushKey, $"{theme} heading background");

                foreach (TextBlock label in pathGrid.Children.OfType<TextBlock>())
                    AssertResourceBrush(window, label.Foreground, SystemColors.WindowTextBrushKey, $"{theme} game label");
                foreach (TextBox box in pathBoxes)
                {
                    Assert.AreEqual("", box.Text);
                    AssertResourceBrush(window, box.Background, SystemColors.WindowBrushKey, $"{theme} path background");
                    AssertResourceBrush(window, box.Foreground, SystemColors.WindowTextBrushKey, $"{theme} path text");
                    AssertResourceBrush(window, box.CaretBrush, SystemColors.WindowTextBrushKey, $"{theme} path caret");
                }
                foreach (Button button in browseButtons.Append(continueButton))
                {
                    AssertResourceBrush(window, button.Background, SystemColors.ControlBrushKey, $"{theme} button background");
                    AssertResourceBrush(window, button.Foreground, SystemColors.ControlTextBrushKey, $"{theme} button text");
                    AssertFits(button, content, theme);
                }

                if (theme == AppTheme.Dark)
                    SaveRequestedPreview(window);
            }
        }
        finally
        {
            window?.Close();
            Settings.Global_Theme = previousTheme;
            ThemeManager.ApplyTheme();
            loadedField.SetValue(null, previousLoaded);
        }
    }

    private static void AssertResourceBrush(FrameworkElement scope, Brush actual, object key, string message)
    {
        var expected = scope.FindResource(key) as SolidColorBrush;
        Assert.IsNotNull(expected, $"{message}: resource must be a solid brush.");
        Assert.IsInstanceOfType<SolidColorBrush>(actual, message);
        Assert.AreEqual(expected.Color, ((SolidColorBrush)actual).Color, message);
    }

    private static void AssertFits(FrameworkElement element, FrameworkElement content, AppTheme theme)
    {
        Rect bounds = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
        const double tolerance = 0.01;
        Assert.IsTrue(bounds.Left >= -tolerance && bounds.Top >= -tolerance
                      && bounds.Right <= content.ActualWidth + tolerance
                      && bounds.Bottom <= content.ActualHeight + tolerance,
            $"{theme}: {element.Name} ({element.GetType().Name}) must fit inside the setup content. Bounds: {bounds}; content: {content.RenderSize}.");
        Assert.IsTrue(element.ActualWidth > 0 && element.ActualHeight > 0);
    }

    private static void SaveRequestedPreview(Window window)
    {
        string path = Environment.GetEnvironmentVariable("LEX_SETUP_PREVIEW_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        Assert.IsTrue(Path.IsPathFullyQualified(path), "The setup preview path must be absolute.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        const double scale = 1.5;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(window.ActualWidth * scale),
            (int)Math.Ceiling(window.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void FlushDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
