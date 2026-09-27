using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Threading;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.SharedUI;

[TestClass]
[DoNotParallelize]
public class PackageMountWarningTests
{
    private string testRoot;
    private string previousGamePath;
    private string basePath;
    private string lowerDlcPath;
    private string highestPath;
    private readonly List<IMEPackage> packages = new();

    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestInitialize]
    public void CreateInstallation()
    {
        previousGamePath = LE3Directory.DefaultGamePath;
        testRoot = Path.Combine(Path.GetTempPath(), $"LEX_MountWarning_{Guid.NewGuid():N}");
        LE3Directory.DefaultGamePath = Path.Combine(testRoot, "Game");
        basePath = Path.Combine(LE3Directory.CookedPCPath, "TestPackage.pcc");
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        File.WriteAllBytes(basePath, []);
        lowerDlcPath = CreateDlc("DLC_MOD_Lower", 1000);
        highestPath = CreateDlc("DLC_MOD_Higher", 2000);
        MELoadedFiles.InvalidateCaches();
    }

    [TestCleanup]
    public void RestoreInstallation()
    {
        foreach (var package in packages) package.Dispose();
        LE3Directory.DefaultGamePath = previousGamePath;
        MELoadedFiles.InvalidateCaches();
        Directory.Delete(testRoot, recursive: true);
    }

    [TestMethod]
    public void WarnsForOverriddenBasegameAndDlcButNotWinningOrExternalPackages()
    {
        Assert.AreEqual(highestPath, PackageMountStatus.GetOverridingFilePath(Package(basePath)));
        Assert.AreEqual(highestPath, PackageMountStatus.GetOverridingFilePath(Package(lowerDlcPath)));
        Assert.IsNull(PackageMountStatus.GetOverridingFilePath(Package(highestPath)));
        Assert.IsNull(PackageMountStatus.GetOverridingFilePath(Package(
            Path.Combine(Path.GetDirectoryName(highestPath)!, "TESTPACKAGE.PCC"))),
            "Windows filename casing must not produce a false warning.");
        Assert.IsNull(PackageMountStatus.GetOverridingFilePath(Package(Path.Combine(testRoot, "TestPackage.pcc"))));
        Assert.IsNull(PackageMountStatus.GetOverridingFilePath(Package(Path.Combine(LE3Directory.CookedPCPath, "Unique.pcc"))));
        Assert.IsNull(PackageMountStatus.GetOverridingFilePath(Package(null)));
        Assert.IsNull(PackageMountStatus.GetOverridingFilePath(Package(basePath, MEGame.UDK)));
        Assert.IsNull(PackageMountStatus.GetOverridingFilePath(null));

        Directory.Move(Path.GetDirectoryName(Path.GetDirectoryName(highestPath)!)!,
            Path.Combine(LE3Directory.DLCPath, "offDLC_MOD_Higher"));
        Assert.AreEqual(lowerDlcPath, PackageMountStatus.GetOverridingFilePath(Package(basePath)),
            "Disabled DLC must not be treated as the mounted winner.");
    }

    [STATestMethod]
    public void IndicatorTracksPackageSwitchesAndSavesWithoutReplacingToolMessages()
    {
        var window = new PackageStatusTestWindow();
        var message = new TextBlock { Text = "Loading exports..." };
        var indicator = new PackageMountWarning();
        var statusBar = new StatusBar
        {
            Items = { message, indicator, new StatusBarItem { Content = "Last saved", HorizontalAlignment = HorizontalAlignment.Right } }
        };
        window.Content = statusBar;
        indicator.SetBinding(PackageMountWarning.PackageProperty, new Binding(nameof(WPFBase.Pcc)) { Source = window });
        try
        {
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);
            var package = Package(basePath);
            window.SetPackage(package);
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);
            var warningText = (TextBlock)indicator.Content;
            Assert.AreEqual(PackageMountStatus.WarningText, new TextRange(warningText.ContentStart, warningText.ContentEnd).Text);
            StringAssert.Contains(indicator.ToolTip.ToString(), highestPath);
            Assert.AreEqual("Loading exports...", message.Text);
            statusBar.Measure(new Size(900, 24));
            statusBar.Arrange(new Rect(0, 0, 900, 24));
            statusBar.UpdateLayout();
            Assert.IsGreaterThan(100, indicator.ActualWidth);
            double warningLeft = indicator.TranslatePoint(new Point(), statusBar).X;
            double messageRight = message.TranslatePoint(new Point(message.ActualWidth, 0), statusBar).X;
            Assert.IsGreaterThanOrEqualTo(messageRight, warningLeft);
            Assert.IsLessThanOrEqualTo(messageRight + 12, warningLeft,
                "The warning belongs beside the filename/message on the left of the status bar.");
            StringAssert.Contains(window.GetStatusBarText(), PackageMountStatus.WarningText);
            Assert.IsFalse(window.GetStatusBarText(includeMountWarning: false).Contains(PackageMountStatus.WarningText),
                "Editors using the indicator must be able to omit the duplicate inline warning.");

            window.SetPackage(Package(highestPath));
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);
            Assert.IsNull(indicator.ToolTip);
            window.SetPackage(package);
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);

            package.SetInternalFilepath(Path.Combine(testRoot, "SavedCopy.pcc"));
            typeof(UnrealPackageFile).GetMethod("AfterSave", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(package, null);
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility,
                "Saving or changing the package path must refresh the warning.");

            window.SetPackage(null);
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);
            Assert.IsNull(indicator.ToolTip);
        }
        finally
        {
            window.SetPackage(null);
            window.Close();
        }
    }

    [STATestMethod]
    public void WarningOpensHighestMountedPackageInANewMatchingEditor()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(App).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));

        var package = Package(basePath);
        var export = package.CreateExport("TestExport", "Object", indexed: false);
        var highestPackage = Package(highestPath);
        highestPackage.CreateExport("DifferentExportAtOriginalIndex", "Object", indexed: false);
        var highestExport = highestPackage.CreateExport("TestExport", "Object", indexed: false);
        using (var stream = highestPackage.SaveToStream(compress: false)) File.WriteAllBytes(highestPath, stream.ToArray());
        typeof(UnrealPackageFile).GetMethod("AfterSave", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(package, null);
        var window = new ExportLoaderHostedWindow(new TestExportLoader(), export);
        var openedWindows = new List<Window>();
        try
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var indicator = LogicalChildren(window).OfType<PackageMountWarning>().Single();
            Assert.AreSame(package, indicator.Package);
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);
            StringAssert.Contains(indicator.ToolTip.ToString(), highestPath);

            var existingWindows = Application.Current.Windows.Cast<Window>().ToList();
            var link = ((TextBlock)indicator.Content).Inlines.OfType<Hyperlink>().Single();
            link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            openedWindows.AddRange(Application.Current.Windows.Cast<Window>().Except(existingWindows));
            var newWindow = openedWindows.OfType<ExportLoaderHostedWindow>().Single();
            Assert.AreNotSame(window, newWindow);
            Assert.AreEqual(window.HostedControl.GetType(), newWindow.HostedControl.GetType());
            Assert.AreNotSame(window.HostedControl, newWindow.HostedControl);
            Assert.AreEqual(highestPath, newWindow.Pcc.FilePath);
            Assert.AreEqual(export.InstancedFullPath, newWindow.HostedControl.CurrentLoadedExport.InstancedFullPath);
            Assert.AreEqual(highestExport.UIndex, newWindow.HostedControl.CurrentLoadedExport.UIndex);
            Assert.AreNotEqual(export.UIndex, newWindow.HostedControl.CurrentLoadedExport.UIndex,
                "Match the export's path rather than its index, which can differ between package versions.");
            Assert.AreSame(package, window.Pcc);
            Assert.AreSame(export, window.HostedControl.CurrentLoadedExport);
            Assert.AreEqual(Visibility.Collapsed, LogicalChildren(newWindow).OfType<PackageMountWarning>().Single().Visibility);
            Assert.IsTrue(indicator.IsEnabled);

            var secondWindow = indicator.OpenHighestMountedVersionAsync().GetAwaiter().GetResult();
            openedWindows.Add(secondWindow);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.AreNotSame(newWindow, secondWindow, "Each activation must open a new tool instance.");

            var metadataWindow = new LECLDataEditorWindow(null, package);
            openedWindows.Add(metadataWindow);
            var newMetadataWindow = PackageToolLauncher.OpenAsync(metadataWindow, highestPath).GetAwaiter().GetResult();
            openedWindows.Add(newMetadataWindow);
            Assert.IsInstanceOfType<LECLDataEditorWindow>(newMetadataWindow);
            Assert.AreNotSame(metadataWindow, newMetadataWindow);
            Assert.AreEqual(highestPath, ((LECLDataEditorWindow)newMetadataWindow).Pcc.FilePath);
            Assert.AreSame(package, metadataWindow.Pcc);

            var missingPackage = Package(lowerDlcPath);
            missingPackage.CreateExport("UnrelatedExport", "Object", indexed: false);
            using (var stream = missingPackage.SaveToStream(compress: false)) File.WriteAllBytes(lowerDlcPath, stream.ToArray());
            Assert.Throws<InvalidOperationException>(() => window.CreateForPackageVersion(lowerDlcPath),
                "A missing matching export must not silently open an unrelated export with the same ID.");

            File.Delete(highestPath);
            File.Delete(lowerDlcPath);
            Assert.IsNull(indicator.OpenHighestMountedVersionAsync().GetAwaiter().GetResult(),
                "Activation must recheck mount order if the installed files have changed.");
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);
        }
        finally
        {
            foreach (var opened in openedWindows) opened.Close();
            window.Close();
        }
    }

    private static IEnumerable<DependencyObject> LogicalChildren(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in LogicalChildren(child)) yield return descendant;
        }
    }

    private sealed class TestExportLoader() : ExportLoaderControl("Mount warning test")
    {
        public override bool CanParse(ExportEntry exportEntry) => true;
        public override void LoadExport(ExportEntry exportEntry) => CurrentLoadedExport = exportEntry;
        public override void UnloadExport() => CurrentLoadedExport = null;
        public override void PopOut() { }
        public override void Dispose() => UnloadExport();
    }

    private string CreateDlc(string name, int priority)
    {
        string cooked = Path.Combine(LE3Directory.DLCPath, name, "CookedPCConsole");
        Directory.CreateDirectory(cooked);
        new MountFile(MEGame.LE3)
        {
            MountPriority = priority,
            MountFlags = new MountFlag(EME3MountFileFlag.LoadsInSingleplayer)
        }.WriteMountFile(Path.Combine(cooked, "Mount.dlc"));
        string package = Path.Combine(cooked, "TestPackage.pcc");
        File.WriteAllBytes(package, []);
        return package;
    }

    private IMEPackage Package(string path, MEGame game = MEGame.LE3)
    {
        var package = MEPackageHandler.CreateMemoryEmptyPackage(path, game);
        packages.Add(package);
        return package;
    }

    private sealed class PackageStatusTestWindow() : WPFBase("Mount warning test", submitTelemetry: false)
    {
        public void SetPackage(IMEPackage package) => typeof(WPFBase).GetProperty(nameof(Pcc))!
            .SetValue(this, package);

        public override void HandleUpdate(List<PackageUpdate> updates) { }
    }
}
