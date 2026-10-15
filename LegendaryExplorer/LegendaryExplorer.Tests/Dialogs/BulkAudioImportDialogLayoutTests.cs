using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LegendaryExplorer.Dialogs;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Dialogs;

[TestClass]
[DoNotParallelize]
public class BulkAudioImportDialogLayoutTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void AddedFilesRemainVisibleWhenSettingsExceedTheWindowHeight()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(BulkAudioImportDialog).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));

        foreach (MEGame game in new[] { MEGame.LE2, MEGame.LE3 })
        {
            using var package = MEPackageHandler.CreateMemoryEmptyPackage("BulkAudioLayout.pcc", game);
            var dialog = new BulkAudioImportDialog(package)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false,
                Width = 800, Height = 600
            };
            try
            {
                dialog.Show();
                var files = (ListBox)dialog.FindName("WavFilesListBox");
                dialog.WavFileItems.Add(new BulkAudioImportDialog.AudioImportItem(@"C:\Audio\Music.wav"));
                dialog.WavFileItems.Add(new BulkAudioImportDialog.AudioImportItem(@"C:\Audio\Added.mp3"));
                FlushLayout(dialog);

                Assert.AreEqual(2, files.Items.Count, "Added files must reach the bound list.");
                AssertVisibleFile(dialog, files, 1);

                dialog.AudioFileFilterText = "Added";
                FlushLayout(dialog);
                Assert.AreEqual(1, files.Items.Count);
                AssertVisibleFile(dialog, files, 0);
                dialog.AudioFileFilterText = "";
                dialog.WavFileItems.Add(new BulkAudioImportDialog.AudioImportItem(@"C:\Audio\Another.wav"));
                FlushLayout(dialog);
                Assert.AreEqual(3, files.Items.Count);

                foreach (double height in new[] { 480d, 600d, 720d, 920d })
                {
                    dialog.Height = height;
                    dialog.FontSize = 18;
                    FlushLayout(dialog);
                    AssertVisibleFile(dialog, files, 2);
                    var settings = (ScrollViewer)dialog.FindName("SettingsScrollViewer");
                    Assert.IsNotNull(settings);
                    Assert.IsGreaterThan(0d, settings.ViewportHeight);
                    if (height == 600)
                        Assert.IsGreaterThan(0d, settings.ScrollableHeight, "Tall settings must scroll inside the window.");
                    settings.ScrollToEnd();
                    FlushLayout(dialog);
                    AssertVisibleFile(dialog, files, 2);
                    AssertInsideWindow(dialog, (Button)dialog.FindName("AddFilesButton"));
                    AssertInsideWindow(dialog, (Button)dialog.FindName("ImportButton"));
                    SavePreview(dialog, $"{game}-{height:0}");
                }
            }
            finally
            {
                dialog.Close();
            }
        }
    }

    private static void AssertVisibleFile(Window window, ListBox files, int index)
    {
        Assert.IsGreaterThanOrEqualTo(130d, files.ActualHeight,
            "The settings must not collapse the audio list, even in a short window.");
        files.ScrollIntoView(files.Items[index]);
        FlushLayout(window);
        var row = (ListBoxItem)files.ItemContainerGenerator.ContainerFromIndex(index);
        Assert.IsNotNull(row, "A newly added file must have a visible row.");
        Assert.IsGreaterThan(0d, row.ActualHeight);
        Rect bounds = row.TransformToAncestor(files).TransformBounds(new Rect(row.RenderSize));
        Assert.IsTrue(bounds.Bottom <= files.ActualHeight + 1 && bounds.Top >= -1,
            "The complete audio row must fit in the visible list viewport.");
    }

    private static void AssertInsideWindow(Window window, FrameworkElement element)
    {
        Rect bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        Assert.IsTrue(element.IsVisible && bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight,
            $"{element.Name} must remain accessible without scrolling away from the file list.");
    }

    private static void FlushLayout(Window window)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static void SavePreview(Window dialog, string name)
    {
        string directory = Environment.GetEnvironmentVariable("LEX_BULK_AUDIO_LAYOUT_CAPTURE");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight,
            96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(dialog);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }
}
