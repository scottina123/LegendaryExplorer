using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class InterpreterVoAudioPreviewTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void ClosePreviewReleasesTheAudioExportWithoutChangingEditorSelection() => RunOnSta(() =>
    {
        using var view = new PreviewTestView();
        object selectedTab = view.HostTabs.SelectedItem;
        object selectedProperty = view.Interpreter.SelectedItem;
        object treeSelectedProperty = view.Interpreter.TreeSelectedItem;
        byte[] originalData = view.Track.Data.ToArray();

        view.Interpreter.StopVoAudioPreviewCommand.Execute(null);

        view.AssertPreviewCleared();
        Assert.AreSame(view.Track, view.Interpreter.CurrentLoadedExport);
        Assert.AreSame(selectedTab, view.HostTabs.SelectedItem);
        Assert.AreSame(selectedProperty, view.Interpreter.SelectedItem);
        Assert.AreSame(treeSelectedProperty, view.Interpreter.TreeSelectedItem);
        Assert.AreEqual(0, view.NavigationRequests);
        CollectionAssert.AreEqual(originalData, view.Track.Data);
        Assert.AreSame(view.Player, view.PreviewHost.Content,
            "Closing a preview should retain its reusable, unloaded player.");
    });

    [DataTestMethod]
    [DataRow("Reload")]
    [DataRow("Switch")]
    [DataRow("UnloadExport")]
    [DataRow("UnloadControl")]
    public void ChangingTheLoadedTrackOrUnloadingTheControlClearsItsPreview(string action) => RunOnSta(() =>
    {
        using var view = new PreviewTestView();
        ExportEntry expectedExport = view.Track;
        switch (action)
        {
            case "Reload":
                view.Interpreter.LoadExport(view.Track);
                break;
            case "Switch":
                expectedExport = view.Package.CreateExport("OtherTrack", "BioEvtSysTrackVOElements", indexed: false);
                expectedExport.WriteProperty(new IntProperty(37252220, "m_nStrRefID"));
                view.Interpreter.LoadExport(expectedExport);
                break;
            case "UnloadExport":
                expectedExport = null;
                view.Interpreter.UnloadExport();
                break;
            case "UnloadControl":
                view.DetachControl();
                break;
            default:
                Assert.Fail($"Unknown test action: {action}");
                break;
        }

        view.AssertPreviewCleared();
        Assert.AreSame(expectedExport, view.Interpreter.CurrentLoadedExport);
        Assert.AreEqual(0, view.NavigationRequests);
    });

    [TestMethod]
    public void DisposingTheInterpreterSeversThePreviewPlayerAndContentReferences() => RunOnSta(() =>
    {
        using var view = new PreviewTestView();

        view.Dispose();

        view.AssertPreviewCleared();
        Assert.IsNull(PreviewPlayerField.GetValue(view.Interpreter));
        Assert.IsNull(view.PreviewHost.Content);
        Assert.IsNull(view.Interpreter.CurrentLoadedExport);
    });

    [TestMethod]
    public void MiniPlayerLoadsTheRequestedStreamWithoutReadingUnrelatedMalformedEvents() => RunOnSta(() =>
    {
        InitializeApplicationResources();
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("MiniVoPreview.pcc", MEGame.LE3);
        ExportEntry stream = CreateStream(package);
        ExportEntry malformedEvent = package.CreateExport("UnrelatedMalformedEvent", "WwiseEvent", indexed: false);
        malformedEvent.WriteProperties(new PropertyCollection());
        // One stream is declared, but the array's only UIndex is missing.
        malformedEvent.WriteBinary(new byte[] { 1, 0, 0, 0 });
        using var player = CreatePlayer();

        player.LoadExport(stream);

        Assert.AreSame(stream, player.CurrentLoadedExport,
            "Hidden navigation must not prevent a mini preview from loading valid stream metadata.");
        Assert.AreEqual(0, player.NavigationTargets.Count);
        Assert.IsFalse(player.IsPlaying);
        player.UnloadExport();
        Assert.IsNull(player.CurrentLoadedExport);
    });

    private static readonly FieldInfo PreviewPlayerField = typeof(InterpreterExportLoader)
        .GetField("voAudioPreviewPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static Soundpanel CreatePlayer() => new()
    {
        MiniPlayerMode = true,
        PlayBackOnlyMode = true,
        GenerateWaveformGraph = false
    };

    private static ExportEntry CreateStream(IMEPackage package)
    {
        ExportEntry stream = package.CreateExport("scene_37252219_m_wav", "WwiseStream", indexed: false);
        stream.WritePropertiesAndBinary(new PropertyCollection { new IntProperty(123, "Id") }, WwiseStream.Create());
        return stream;
    }

    private static void RunOnSta(Action action)
    {
        // Share the existing Application's owning STA rather than creating another WPF Application.
        var dispatcher = (Lazy<Dispatcher>)typeof(InterpreterObjectNavigationTests)
            .GetField("StaDispatcher", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
        dispatcher.Value.Invoke(action);
    }

    private static void InitializeApplicationResources()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(InterpreterExportLoader).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
    }

    private static void FlushDispatcher() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private sealed class PreviewTestView : IDisposable
    {
        private readonly Window window;
        private bool disposed;
        public IMEPackage Package { get; }
        public ExportEntry Track { get; }
        public InterpreterExportLoader Interpreter { get; }
        public Soundpanel Player { get; }
        public ContentControl PreviewHost { get; }
        public TabControl HostTabs { get; }
        public int NavigationRequests { get; private set; }

        public PreviewTestView()
        {
            InitializeApplicationResources();
            Package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterVoAudioPreview.pcc", MEGame.LE3);
            Track = Package.CreateExport("Track", "BioEvtSysTrackVOElements", indexed: false);
            Track.WriteProperties(new PropertyCollection
            {
                new IntProperty(37252219, "m_nStrRefID"),
                new IntProperty(42, "UnrelatedSelection")
            });
            ExportEntry stream = CreateStream(Package);
            Interpreter = new InterpreterExportLoader
            {
                ReloadPropertyDataAfterWrite = false,
                NavigateToEntryCommand = new RelayCommand(_ => NavigationRequests++),
                OpenEntryInNewWindowCommand = new RelayCommand(_ => NavigationRequests++)
            };
            var propertyTab = new TabItem { Header = "Properties", Content = Interpreter };
            HostTabs = new TabControl();
            HostTabs.Items.Add(propertyTab);
            HostTabs.Items.Add(new TabItem { Header = "Other editor" });
            HostTabs.SelectedItem = propertyTab;
            window = new Window
            {
                Content = HostTabs, Width = 1200, Height = 700,
                ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000
            };
            window.Show();
            Interpreter.LoadExport(Track);
            UPropertyTreeViewEntry selected = Interpreter.PropertyNodes[0].ChildrenProperties
                .Single(node => node.Property?.Name.Name == "UnrelatedSelection");
            Interpreter.SelectedItem = selected;
            Interpreter.TreeSelectedItem = selected;

            Player = CreatePlayer();
            // Load actual stream metadata, but never start decoding or touch an audio output device.
            Player.LoadExport(stream);
            Assert.AreSame(stream, Player.CurrentLoadedExport);
            PreviewPlayerField.SetValue(Interpreter, Player);
            PreviewHost = (ContentControl)Interpreter.FindName("VoAudioPreviewHost");
            PreviewHost.Content = Player;
            ((Border)Interpreter.FindName("VoAudioPreviewBorder")).Visibility = Visibility.Visible;
            ((TextBlock)Interpreter.FindName("VoAudioPreviewDescription")).Text = stream.InstancedFullPath;
            ((TextBlock)Interpreter.FindName("VoAudioPreviewDescription")).ToolTip = stream.InstancedFullPath;
            var status = (TextBlock)Interpreter.FindName("VoAudioPreviewStatus");
            status.Text = "Preview status";
            status.Visibility = Visibility.Visible;
            FlushDispatcher();
            window.UpdateLayout();
        }

        public void DetachControl()
        {
            window.Content = null;
            FlushDispatcher();
        }

        public void AssertPreviewCleared()
        {
            Assert.IsNull(Player.CurrentLoadedExport);
            Assert.IsFalse(Player.IsPlaying);
            Assert.AreEqual(Visibility.Collapsed, ((Border)Interpreter.FindName("VoAudioPreviewBorder")).Visibility);
            var description = (TextBlock)Interpreter.FindName("VoAudioPreviewDescription");
            Assert.AreEqual(string.Empty, description.Text);
            Assert.IsNull(description.ToolTip);
            var status = (TextBlock)Interpreter.FindName("VoAudioPreviewStatus");
            Assert.AreEqual(string.Empty, status.Text);
            Assert.AreEqual(Visibility.Collapsed, status.Visibility);
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            window.Close();
            Interpreter.Dispose();
            Package.Dispose();
        }
    }
}
