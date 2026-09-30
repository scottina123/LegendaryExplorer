using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.Meshplorer;

[TestClass]
public class MeshToolsCommandsTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void EmbeddedMenuFollowsTheCurrentMeshAndDisablesUnavailableActions()
    {
        InitializeResources();
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MeshTools.pcc", MEGame.LE3);
        using var otherPackage = MEPackageHandler.CreateMemoryEmptyPackage("OtherMeshes.pcc", MEGame.LE3);
        var skeletalMesh = package.CreateExport("Skeletal", "SkeletalMesh", indexed: false);
        var staticMesh = package.CreateExport("Static", "StaticMesh", indexed: false);
        var brush = package.CreateExport("Brush", "Brush", indexed: false);
        var externalMesh = otherPackage.CreateExport("External", "SkeletalMesh", indexed: false);
        using var renderer = new MeshRenderer { ShowMeshTools = true };
        using var host = new TestHost(package) { Content = renderer };
        var menu = (Menu)renderer.FindName("MeshToolsMenu");
        var toolsItem = (MenuItem)menu.Items[0];
        FlushBindings();

        Assert.AreEqual(Visibility.Visible, menu.Visibility);
        Assert.AreSame(renderer.MeshTools, toolsItem.DataContext);
        Assert.HasCount(11, toolsItem.Items);
        Assert.IsTrue(toolsItem.Items.OfType<MenuItem>().All(item => item.Command != null));
        var replaceItem = toolsItem.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Replace Mesh from UDK"));
        Assert.AreSame(renderer.MeshTools.ReplaceFromUDKCommand, replaceItem.Command);
        Assert.IsFalse(replaceItem.Command.CanExecute(null));
        Assert.IsTrue(renderer.MeshTools.ImportFromUDKCommand.CanExecute(null));
        Assert.IsTrue(renderer.MeshTools.ImportNewFromGltfCommand.CanExecute(null));

        SelectExport(renderer, skeletalMesh);
        Assert.IsTrue(replaceItem.Command.CanExecute(null));
        Assert.IsTrue(renderer.MeshTools.ReplaceLODFromUDKCommand.CanExecute(null));
        Assert.IsTrue(renderer.MeshTools.ConvertToStaticMeshCommand.CanExecute(null));
        Assert.IsTrue(renderer.MeshTools.ExportToPSKCommand.CanExecute(null));

        SelectExport(renderer, staticMesh);
        Assert.IsTrue(replaceItem.Command.CanExecute(null));
        Assert.IsFalse(renderer.MeshTools.ReplaceLODFromUDKCommand.CanExecute(null));
        Assert.IsFalse(renderer.MeshTools.ConvertToStaticMeshCommand.CanExecute(null));
        Assert.IsFalse(renderer.MeshTools.ExportToPSKCommand.CanExecute(null));
        Assert.IsTrue(renderer.MeshTools.ExportToGltfTexturesCommand.CanExecute(null));

        renderer.IsBusy = true;
        Assert.IsFalse(replaceItem.Command.CanExecute(null));
        Assert.IsFalse(renderer.MeshTools.ImportFromUDKCommand.CanExecute(null));
        renderer.IsBusy = false;
        host.IsBusy = true;
        Assert.IsFalse(renderer.MeshTools.ImportNewFromGltfCommand.CanExecute(null));
        host.IsBusy = false;

        SelectExport(renderer, brush);
        Assert.IsFalse(replaceItem.Command.CanExecute(null));
        SelectExport(renderer, externalMesh);
        Assert.IsFalse(replaceItem.Command.CanExecute(null), "A component preview must not edit a mesh in another package.");
        SelectExport(renderer, null);
        Assert.IsFalse(renderer.MeshTools.ExportToUDKCommand.CanExecute(null));

        renderer.ShowMeshTools = false;
        FlushBindings();
        Assert.AreEqual(Visibility.Collapsed, menu.Visibility);

        foreach (var (game, canConvert) in new[]
        {
            (MEGame.ME1, false), (MEGame.ME2, false), (MEGame.ME3, true),
            (MEGame.LE1, true), (MEGame.LE2, true), (MEGame.LE3, true), (MEGame.UDK, false)
        })
        {
            using var gamePackage = MEPackageHandler.CreateMemoryEmptyPackage("Conversion.pcc", game);
            using var gameRenderer = new MeshRenderer();
            using var gameHost = new TestHost(gamePackage) { Content = gameRenderer };
            SelectExport(gameRenderer, gamePackage.CreateExport("Mesh", "SkeletalMesh", indexed: false));
            Assert.AreEqual(canConvert, gameRenderer.MeshTools.ConvertToStaticMeshCommand.CanExecute(null), game.ToString());
        }
    }

    private static void InitializeResources()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(MeshRenderer).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
    }

    private static void FlushBindings() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    // Exercise the real bindings without starting the renderer's asynchronous GPU loading.
    private static void SelectExport(MeshRenderer renderer, ExportEntry export) =>
        typeof(ExportLoaderControl).GetProperty(nameof(ExportLoaderControl.CurrentLoadedExport))!.SetValue(renderer, export);

    private sealed class TestHost : WPFBase, IDisposable
    {
        public TestHost(IMEPackage package) : base("Mesh tools test", submitTelemetry: false) => RegisterPackage(package);
        public override void HandleUpdate(List<PackageUpdate> updates) { }
        public void Dispose()
        {
            Content = null;
            UnLoadMEPackage();
        }
    }
}
