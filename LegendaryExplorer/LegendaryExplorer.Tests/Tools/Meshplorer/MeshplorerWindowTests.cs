using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.Tools.Meshplorer;
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
    public void ExportDataUpdateWithNoCurrentMeshDoesNotThrow()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(MeshplorerWindow).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));

        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("MeshplorerUpdateTest.pcc", MEGame.LE3);
        ExportEntry nonMeshExport = package.CreateExport("not_a_mesh", "Texture2D", indexed: false);
        var window = new MeshplorerWindow
        {
            ShowActivated = false,
            ShowInTaskbar = false
        };
        try
        {
            typeof(WPFBase).GetMethod("RegisterPackage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [package]);

            Assert.IsNull(window.CurrentExport);
            window.HandleUpdate([new PackageUpdate(PackageChange.ExportData, nonMeshExport.UIndex)]);
        }
        finally
        {
            typeof(WPFBase).GetMethod("UnLoadMEPackage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);
            GC.KeepAlive(window);
        }
    }
}
