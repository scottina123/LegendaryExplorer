using System;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Threading;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.AssetDatabase;

[TestClass]
[DoNotParallelize]
public class LineVirtualizationTests
{
    public TestContext TestContext { get; set; }

    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void LinesOnlyRealizeViewportRowsWhenOpeningSearchingSortingAndScrolling()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(AssetDatabaseWindow).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
        ThemeManager.ApplyTheme(AppTheme.Dark);

        // Keep this test independent of installed games, saved databases, and user preferences.
        FieldInfo deferSaving = typeof(Settings).GetField("_deferSaving", BindingFlags.Static | BindingFlags.NonPublic)!;
        object previousDeferSaving = deferSaving.GetValue(null);
        var savedSettings = typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.CanRead && property.CanWrite)
            .ToDictionary(property => property, property => property.GetValue(null));
        deferSaving.SetValue(null, true);
        AssetDatabaseWindow window = null;
        try
        {
            window = new AssetDatabaseWindow();
            var loadHandler = (RoutedEventHandler)typeof(AssetDatabaseWindow)
                .GetMethod("AssetDB_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate(typeof(RoutedEventHandler), window);
            window.Loaded -= loadHandler;
            window.Left = -10000;
            window.Top = -10000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            const int lineCount = 50000;
            window.CurrentDataBase.Lines.AddRange(Enumerable.Range(0, lineCount).Select(index =>
                new ConvoLine(index, "Speaker " + index % 10, "Conversation " + index)
                {
                    Line = index % 2 == 0 ? "Matching dialogue " + index : "Other dialogue " + index
                }));
            ((TabControl)window.FindName("MainTabControl")).SelectedIndex = 8;
            var lines = (ListView)window.FindName("lstbx_Lines");
            var timer = Stopwatch.StartNew();
            window.Show();
            Flush(window);
            Assert.HasCount(lineCount, lines.Items);
            AssertViewportRows(lines, "Opening", timer.Elapsed);
            var scroll = Descendants<ScrollViewer>(lines).First();
            Assert.IsTrue(scroll.CanContentScroll, "The inner scrolling template must preserve virtualization.");
            var firstRows = Descendants<ListViewItem>(lines).ToArray();

            lines.ScrollIntoView(lines.Items[^1]);
            Flush(window);
            AssertViewportRows(lines, "Scrolling", timer.Elapsed);
            Assert.IsNotNull(lines.ItemContainerGenerator.ContainerFromIndex(lineCount - 1));
            Assert.IsNull(lines.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.IsTrue(firstRows.Intersect(Descendants<ListViewItem>(lines)).Any(), "Scrolling should reuse row containers.");

            window.SelectedLineSearchColumn = "Line";
            window.LineSearchText = "Matching dialogue";
            typeof(AssetDatabaseWindow).GetMethod("Filter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            WaitForSearch(window);
            Flush(window);
            Assert.HasCount(lineCount / 2, lines.Items, "Search must include all rows, including those never rendered.");
            AssertViewportRows(lines, "Searching", timer.Elapsed);
            var view = (ListCollectionView)CollectionViewSource.GetDefaultView(lines.ItemsSource);
            view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(ConvoLine.StrRef), System.ComponentModel.ListSortDirection.Descending));
            Flush(window);
            Assert.AreEqual(lineCount - 2, ((ConvoLine)lines.Items[0]).StrRef);
            AssertViewportRows(lines, "Sorting", timer.Elapsed);

            lines.ScrollIntoView(lines.Items[0]);
            Flush(window);
            var first = (ListViewItem)lines.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.IsNotNull(first);
            Assert.AreSame(lines.Items[0], first.DataContext, "Recycled rows must show the current search and sort result.");
            scroll.ScrollToHorizontalOffset(300);
            Flush(window);
            Assert.IsGreaterThan(0d, scroll.HorizontalOffset);

            window.LineSearchText = "";
            typeof(AssetDatabaseWindow).GetMethod("Filter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            WaitForSearch(window);
            Flush(window);
            Assert.HasCount(lineCount, lines.Items);
            AssertViewportRows(lines, "Clearing search", timer.Elapsed);

            foreach (AppTheme theme in new[] { AppTheme.Light, AppTheme.ModernDark })
            {
                ThemeManager.ApplyTheme(theme);
                Flush(window);
                lines.ScrollIntoView(lines.Items[^1]);
                Flush(window);
                AssertViewportRows(lines, theme + " scrolling", timer.Elapsed);
            }

            // Optional local profiling uses the saved database read-only; normal CI remains self-contained.
            string databasePath = Environment.GetEnvironmentVariable("LEX_LINE_SEARCH_DATABASE");
            if (!string.IsNullOrEmpty(databasePath) && File.Exists(databasePath)) ProfileSavedDatabase(window, lines, databasePath);
        }
        finally
        {
            window?.Close();
            foreach (var setting in savedSettings.OrderBy(setting => setting.Key.Name == nameof(Settings.Global_Theme)))
                setting.Key.SetValue(null, setting.Value);
            deferSaving.SetValue(null, previousDeferSaving);
        }
    }

    private void AssertViewportRows(ListView list, string operation, TimeSpan elapsed)
    {
        int realized = Descendants<ListViewItem>(list).Count();
        TestContext.WriteLine($"{operation}: {realized} realized rows / {list.Items.Count} results; elapsed {elapsed.TotalMilliseconds:F0} ms.");
        Assert.IsGreaterThan(0, realized);
        Assert.IsLessThan(200, realized, $"{operation} must create only viewport rows and a small scroll buffer.");
    }

    private static void Flush(Window window)
    {
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static void WaitForSearch(AssetDatabaseWindow window)
    {
        Task search = window.PendingLineSearch;
        if (!search.IsCompleted)
        {
            var frame = new DispatcherFrame();
            search.ContinueWith(_ => window.Dispatcher.InvokeAsync(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        search.GetAwaiter().GetResult();
    }

    private void ProfileSavedDatabase(AssetDatabaseWindow window, ListView list, string path)
    {
        window.CurrentDataBase.Clear();
        Task.Run(() => AssetDatabaseWindow.LoadDatabase(path, MEGame.LE3, window.CurrentDataBase, CancellationToken.None))
            .GetAwaiter().GetResult();
        window.FileListExtended.Clear();
        foreach (var file in window.CurrentDataBase.FileList)
            window.FileListExtended.Add(new(file.FileName, window.CurrentDataBase.ContentDir[file.DirectoryKey], 0));
        void Invoke(string method) => typeof(AssetDatabaseWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        Invoke("RebuildConversationLookup");
        Invoke("RefreshTlkLookup");
        window.SelectedLineSearchColumn = "All Columns";
        const string query = "zzlexnomatch";
        var watch = Stopwatch.StartNew();
        int oldMatches = window.CurrentDataBase.Lines.Count(line =>
            Contains(window.GetSpeakerDisplay(line), query) || Contains(line.StrRef.ToString(), query)
            || Contains(line.DisplayLine, query) || Contains(line.Convo, query)
            || Contains(window.GetLineFileDisplay(line), query) || Contains(window.GetLineLocationDisplay(line), query));
        TestContext.WriteLine($"Saved database: {window.CurrentDataBase.Lines.Count} lines; original full-text pass {watch.Elapsed.TotalMilliseconds:F1} ms.");

        window.LineSearchText = query;
        watch.Restart();
        Invoke("Filter");
        WaitForSearch(window);
        Assert.HasCount(oldMatches, list.Items);
        TestContext.WriteLine($"First indexed search (including index build): {watch.Elapsed.TotalMilliseconds:F1} ms.");
        watch.Restart();
        foreach (string text in new[] { "s", "sh", "she", "shep", "shepa", "shepar", "shepard", query })
            window.LineSearchText = text;
        TestContext.WriteLine($"Eight rapid text changes returned in {watch.Elapsed.TotalMilliseconds:F1} ms.");
        watch.Restart();
        Invoke("Filter");
        WaitForSearch(window);
        Assert.HasCount(oldMatches, list.Items);
        TestContext.WriteLine($"Cached full-text search and applying results: {watch.Elapsed.TotalMilliseconds:F1} ms.");
        window.LineSearchText = "Shepard";
        Invoke("Filter");
        Task obsoleteSearch = window.PendingLineSearch;
        window.LineSearchText = query;
        Invoke("Filter");
        WaitForSearch(window);
        Assert.HasCount(oldMatches, list.Items, "An older search must not replace the latest result.");
        obsoleteSearch.GetAwaiter().GetResult();
    }

    private static bool Contains(string text, string query) => text?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true;

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
