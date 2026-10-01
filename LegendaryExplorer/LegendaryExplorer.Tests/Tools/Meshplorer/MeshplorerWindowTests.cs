using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.Tools.Meshplorer;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;

namespace LegendaryExplorer.Tests.Tools.Meshplorer;

[TestClass]
public class MeshplorerWindowTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void PackageUpdatesAndRecentsVisibilityStayInSync()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(MeshplorerWindow).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));

        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("MeshplorerUpdateTest.pcc", MEGame.LE3);
        ExportEntry nonMeshExport = package.CreateExport("not_a_mesh", "Texture2D", indexed: false);
        var window = new MeshplorerWindow(enableRecents: false)
        {
            ShowActivated = false,
            ShowInTaskbar = false
        };
        var recents = (RecentsControl)window.FindName("RecentsController");
        var renderer = (MeshRenderer)window.FindName("Mesh3DViewer");
        var content = (FrameworkElement)window.Content;
        try
        {
            recents.AddRecent(@"C:\RecentsTests\Mesh.pcc", false, MEGame.LE3);
            Layout(new Size(1200, 550));
            Assert.AreSame(recents, recents.DataContext);
            Assert.AreEqual(Visibility.Visible, recents.Visibility);

            // The panel's own data context must not hide the window's package from its visibility binding.
            typeof(WPFBase).GetMethod("RegisterPackage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [package]);
            renderer.IsSkeletalMesh = true;
            Layout(new Size(1200, 550));
            Assert.AreEqual(Visibility.Collapsed, recents.Visibility,
                "Recents must disappear instead of overlapping the renderer and animation controls.");
            Assert.AreEqual(Visibility.Visible, ((FrameworkElement)renderer.Parent).Visibility);

            Layout(new Size(1600, 900));
            Assert.AreEqual(Visibility.Collapsed, recents.Visibility);

            Assert.IsNull(window.CurrentExport);
            window.HandleUpdate([new PackageUpdate(PackageChange.ExportData, nonMeshExport.UIndex)]);

            typeof(WPFBase).GetMethod("UnLoadMEPackage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);
            Layout(new Size(1200, 550));
            Assert.AreEqual(Visibility.Visible, recents.Visibility);
            Assert.HasCount(1, recents.SelectedRecentGroup.Items);
        }
        finally
        {
            typeof(WPFBase).GetMethod("UnLoadMEPackage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);
            renderer.Dispose();
            recents.Dispose();
            GC.KeepAlive(window);
        }

        void Layout(Size size)
        {
            content.Measure(size);
            content.Arrange(new Rect(size));
            content.UpdateLayout();
        }
    }

}
