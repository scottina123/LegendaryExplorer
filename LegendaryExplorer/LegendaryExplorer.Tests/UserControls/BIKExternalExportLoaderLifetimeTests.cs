using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PropertyInfo = System.Reflection.PropertyInfo;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
[DoNotParallelize]
public class BIKExternalExportLoaderLifetimeTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void NativePreviewLifetimeAndAssetDatabaseClosingReleasePlayersSafely()
    {
        InitializeApplicationResources();
        DisposeAndUnloadCanBeRepeatedAfterNativePlayersAreReleased();
        NativePlayingCallbackUpdatesPreviewStateOnTheDispatcher();
        NativeCallbacksQueuedBeforeClosingAndDeliveredAfterClosingAreIgnored();
        PlayingCallbackFromPreviousExportCannotRestartUnloadedPreviewState();
        ExportLoadCompletionQueuedBeforeDisposalCannotUseReleasedNativePlayer();
        AssetDatabaseClosingWithASelectedTextureMovieReleasesPlayers();
    }

    private static void DisposeAndUnloadCanBeRepeatedAfterNativePlayersAreReleased()
    {
        using var viewer = new BIKExternalExportLoader();

        viewer.Dispose();
        viewer.UnloadExport();
        viewer.Dispose();

        Assert.IsNull(viewer.mediaPlayer);
        Assert.IsNull(viewer.libvlc);
        Assert.IsNull(viewer.CurrentLoadedExport);
        Assert.IsFalse(viewer.IsVLCPlaying);
    }

    private static void NativePlayingCallbackUpdatesPreviewStateOnTheDispatcher()
    {
        using var viewer = new BIKExternalExportLoader();
        Task.Run(() => InvokePlaying(viewer)).GetAwaiter().GetResult();

        Assert.IsFalse(viewer.IsVLCPlaying);
        DrainDispatcher();
        Assert.IsTrue(viewer.IsVLCPlaying);
    }

    private static void NativeCallbacksQueuedBeforeClosingAndDeliveredAfterClosingAreIgnored()
    {
        using var viewer = new BIKExternalExportLoader();
        Task.Run(() => InvokePlaying(viewer)).GetAwaiter().GetResult();
        viewer.MediaEndReached(viewer.mediaPlayer, EventArgs.Empty);

        viewer.Dispose();
        DrainDispatcher();

        InvokePlaying(viewer);
        viewer.MediaEndReached(null, EventArgs.Empty);
        DrainDispatcher();

        Assert.IsFalse(viewer.IsVLCPlaying);
        Assert.IsNull(viewer.mediaPlayer);
    }

    private static void PlayingCallbackFromPreviousExportCannotRestartUnloadedPreviewState()
    {
        using var viewer = new BIKExternalExportLoader();
        InvokePlaying(viewer);

        viewer.UnloadExport();
        DrainDispatcher();

        Assert.IsFalse(viewer.IsVLCPlaying);
    }

    private static void ExportLoadCompletionQueuedBeforeDisposalCannotUseReleasedNativePlayer()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MoviePreview.pcc", MEGame.LE3);
        var export = package.CreateExport("Movie", "TextureMovie", indexed: false);
        export.WriteBinary(new TextureMovie { EmbeddedData = [(byte)'B', (byte)'I', (byte)'K', (byte)'i'] });
        using var viewer = new BIKExternalExportLoader();
        var previousScheduler = LegendaryExplorerCoreLib.SYNCHRONIZATION_CONTEXT;
        var scheduler = new QueuedTaskScheduler();
        try
        {
            LegendaryExplorerCoreLib.SetSynchronizationContext(scheduler);
            viewer.LoadExport(export);
            Assert.IsTrue(SpinWait.SpinUntil(() => scheduler.HasPendingTask, TimeSpan.FromSeconds(5)),
                "The movie read should finish and queue its UI continuation.");

            viewer.Dispose();
            scheduler.RunPendingTasks();

            Assert.IsNull(viewer.mediaPlayer);
            Assert.IsNull(viewer.CurrentLoadedExport);
            Assert.AreEqual(0u, viewer.MovieCRC);
            Assert.IsFalse(viewer.IsBink1);
        }
        finally
        {
            LegendaryExplorerCoreLib.SetSynchronizationContext(previousScheduler);
        }
    }

    private static void AssetDatabaseClosingWithASelectedTextureMovieReleasesPlayers()
    {
        using var settingsScope = new SettingsScope();
        var window = new AssetDatabaseWindow();
        bool closed = false;
        try
        {
            ((TabControl)window.FindName("MainTabControl")).SelectedIndex = 4;
            var movie = new TextureRecord("Movie", "Package", false, false, "TextureMovie", "n/a", 1280, 720, "n/a");
            movie.Usages.Add(new TextureUsage(0, 1, false, false));
            window.CurrentDataBase.Textures.Add(movie);
            var textures = (ListBox)window.FindName("lstbx_Textures");
            textures.ItemsSource = window.CurrentDataBase.Textures;
            textures.SelectedItem = movie;
            var viewer = (BIKExternalExportLoader)window.FindName("BIKExternalExportLoaderTab_BIKExternalExportLoader");

            // Closing this unshown window exercises the real selection-clearing path
            // without loading a game database or starting an external player.
            window.Close();
            closed = true;

            Assert.IsNull(viewer.mediaPlayer);
            Assert.IsNull(viewer.libvlc);
            Assert.HasCount(0, window.CurrentDataBase.Textures);
        }
        finally
        {
            if (!closed)
            {
                window.Close();
            }
        }
    }

    private static void InvokePlaying(BIKExternalExportLoader viewer) =>
        typeof(BIKExternalExportLoader).GetMethod("OnPlaying", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewer, [viewer.mediaPlayer, EventArgs.Empty]);

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void InitializeApplicationResources()
    {
        if (Application.Current != null)
        {
            return;
        }

        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(BIKExternalExportLoader).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
    }

    private sealed class SettingsScope : IDisposable
    {
        private static readonly FieldInfo LoadedField = typeof(Settings)
            .GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
        private static readonly FieldInfo DeferSavingField = typeof(Settings)
            .GetField("_deferSaving", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object _loaded = LoadedField.GetValue(null);
        private readonly object _deferSaving = DeferSavingField.GetValue(null);
        private readonly Dictionary<PropertyInfo, object> _settings;

        public SettingsScope()
        {
            DeferSavingField.SetValue(null, true);
            LoadedField.SetValue(null, false);
            _settings = typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(property => property.CanRead && property.CanWrite)
                .ToDictionary(property => property, property => property.GetValue(null));
        }

        public void Dispose()
        {
            try
            {
                foreach (var setting in _settings.OrderBy(setting => setting.Key.Name == nameof(Settings.Global_Theme)))
                {
                    setting.Key.SetValue(null, setting.Value);
                }
            }
            finally
            {
                LoadedField.SetValue(null, _loaded);
                DeferSavingField.SetValue(null, _deferSaving);
            }
        }
    }

    private sealed class QueuedTaskScheduler : TaskScheduler
    {
        private readonly ConcurrentQueue<Task> _pending = new();
        public bool HasPendingTask => !_pending.IsEmpty;
        protected override IEnumerable<Task> GetScheduledTasks() => _pending.ToArray();
        protected override void QueueTask(Task task) => _pending.Enqueue(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        public void RunPendingTasks()
        {
            while (_pending.TryDequeue(out var task))
            {
                Assert.IsTrue(TryExecuteTask(task));
                Assert.IsFalse(task.IsFaulted, task.Exception?.ToString());
            }
        }
    }
}
