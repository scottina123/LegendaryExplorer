using System;
using System.Collections.Generic;
using System.IO;
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
using LegendaryExplorerCore.Sound.Wwise;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class VoTrackAudioImportResolverTests
{
    private const int StringRef = 37252219;
    private const int AudioId = 810321;
    private static readonly byte[] EmbeddedAudio = [0x52, 0x49, 0x46, 0x46, 0x10, 0x20, 0x30, 0x40];

    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [DataTestMethod]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void ResolvesSavedExternalEventsAndTheirImportedStreamsWithFullBinaryData(MEGame game)
    {
        using var fixture = new ExternalAudioFixture(game);
        using var resolver = new VoTrackAudioImportResolver();

        ExportEntry audioEvent = resolver.Resolve(fixture.EventImport);

        Assert.IsNotNull(audioEvent, "The event must resolve from its sibling .pcc file.");
        Assert.AreEqual(fixture.EventPath, audioEvent.FileRef.FilePath);
        Assert.AreEqual("WwiseEvent", audioEvent.ClassName);
        IEntry[] references = WwiseHelper.GetReferencedWwiseStreamEntries(audioEvent).ToArray();
        Assert.AreEqual(1, references.Length);
        Assert.IsInstanceOfType<ImportEntry>(references[0]);
        ExportEntry audioStream = resolver.Resolve((ImportEntry)references[0]);

        Assert.IsNotNull(audioStream, "The stream import must resolve from a second .pcc file.");
        Assert.AreEqual(fixture.StreamPath, audioStream.FileRef.FilePath);
        Assert.AreEqual("WwiseStream", audioStream.ClassName);
        Assert.AreEqual(AudioId, audioStream.GetProperty<IntProperty>("Id").Value);
        CollectionAssert.AreEqual(EmbeddedAudio, audioStream.GetBinaryData<WwiseStream>().EmbeddedData,
            "Import resolution must retain full export data for playback.");
        Assert.AreSame(audioEvent, resolver.Resolve(fixture.EventImport));
        Assert.AreSame(audioStream, resolver.Resolve((ImportEntry)references[0]));
    }

    [DataTestMethod]
    [DataRow(MEGame.LE2, "BioEvtSysTrackSubtitles")]
    [DataRow(MEGame.LE3, "BioEvtSysTrackSubtitles")]
    [DataRow(MEGame.LE2, "InterpTrackWwiseEvent")]
    [DataRow(MEGame.LE3, "InterpTrackWwiseEvent")]
    public void TrackLookupFindsOpaqueStreamBehindSavedImportedEvent(MEGame game, string className)
    {
        using var fixture = new ExternalAudioFixture(game);
        using var resolver = new VoTrackAudioImportResolver();
        ExportEntry track = fixture.CreateTrack(className);
        (Property property, StructProperty parent) = GetTrackProperty(track);

        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, property, parent, false, true),
            "The source package deliberately has no stream name that can satisfy the fallback lookup.");
        IEntry stream = VoTrackAudioNavigation.FindAudioEntry(track, property, parent, false, true, resolver.Resolve);

        Assert.IsNotNull(stream);
        Assert.AreEqual("ActualAudio", stream.ObjectName.Name);
        Assert.AreEqual(fixture.EventPath, stream.FileRef.FilePath);
        Assert.IsInstanceOfType<ImportEntry>(stream);
        Assert.AreEqual(fixture.StreamPath, resolver.Resolve((ImportEntry)stream).FileRef.FilePath);
        Assert.AreSame(fixture.EventImport,
            VoTrackAudioNavigation.FindAudioEntry(track, property, parent, false, false, resolver.Resolve));
    }

    [TestMethod]
    public void ReusesResolvedImportsAfterTheirSourceFilesAreRemovedAndReleasesCacheOnDispose()
    {
        using var fixture = new ExternalAudioFixture(MEGame.LE3);
        var resolver = new VoTrackAudioImportResolver();
        try
        {
            ExportEntry audioEvent = resolver.Resolve(fixture.EventImport);
            Assert.IsNotNull(audioEvent);
            var streamImport = (ImportEntry)WwiseHelper.GetReferencedWwiseStreamEntries(audioEvent).Single();
            ExportEntry stream = resolver.Resolve(streamImport);
            Assert.IsNotNull(stream);
            File.Delete(fixture.EventPath);
            File.Delete(fixture.StreamPath);

            Assert.AreSame(audioEvent, resolver.Resolve(fixture.EventImport));
            Assert.AreSame(stream, resolver.Resolve(streamImport));
            CollectionAssert.AreEqual(EmbeddedAudio, stream.GetBinaryData<WwiseStream>().EmbeddedData);
            Assert.IsTrue(GetResolverCache(resolver).Cache.Count >= 2);
        }
        finally
        {
            resolver.Dispose();
        }

        Assert.AreEqual(0, GetResolverCache(resolver).Cache.Count);
    }

    [DataTestMethod]
    [DataRow("BioEvtSysTrackSubtitles")]
    [DataRow("InterpTrackWwiseEvent")]
    public void InterpreterPlayCommandFindsImportedEventStreamWithoutChangingSelection(string className) => RunOnSta(() =>
    {
        using var fixture = new ExternalAudioFixture(MEGame.LE3);
        ExportEntry track = fixture.CreateTrack(className);
        using var view = new InterpreterTestView();
        InterpreterExportLoader interpreter = view.Interpreter;
        var played = new List<IEntry>();
        interpreter.VoAudioPreviewCommand = new RelayCommand(parameter => played.Add((IEntry)parameter));
        interpreter.LoadExport(track);
        UPropertyTreeViewEntry node = AllNodes(interpreter).Single(entry => entry.ShowVoTrackAudioNavigation);
        interpreter.SelectedItem = AllNodes(interpreter).First(entry => entry.Property?.Name.Name == "UnrelatedSelection");
        byte[] originalTrackData = track.Data.ToArray();

        Assert.IsTrue(interpreter.PlayMaleVoAudioCommand.CanExecute(node));
        Assert.IsFalse(interpreter.PlayFemaleVoAudioCommand.CanExecute(node));
        interpreter.PlayMaleVoAudioCommand.Execute(node);

        Assert.AreEqual(1, played.Count);
        Assert.IsInstanceOfType<ImportEntry>(played[0]);
        Assert.AreEqual("ActualAudio", played[0].ObjectName.Name);
        VoTrackAudioImportResolver resolver = GetInterpreterResolver(interpreter);
        Assert.IsNotNull(resolver);
        ExportEntry playable = resolver.Resolve((ImportEntry)played[0]);
        Assert.IsNotNull(playable);
        CollectionAssert.AreEqual(EmbeddedAudio, playable.GetBinaryData<WwiseStream>().EmbeddedData);
        CollectionAssert.AreEqual(originalTrackData, track.Data);
        Assert.AreEqual("UnrelatedSelection", interpreter.SelectedItem.Property.Name.Name);
    });

    [TestMethod]
    public void DefaultPreviewLoadsImportedStreamMetadataBeforeReportingUnavailableAfc() => RunOnSta(() =>
    {
        using var fixture = new ExternalAudioFixture(MEGame.LE3, unavailableAudio: true);
        ExportEntry track = fixture.CreateTrack("InterpTrackWwiseEvent");
        using var view = new InterpreterTestView();
        InterpreterExportLoader interpreter = view.Interpreter;
        interpreter.LoadExport(track);
        UPropertyTreeViewEntry node = AllNodes(interpreter).Single(entry => entry.ShowVoTrackAudioNavigation);

        Assert.IsTrue(interpreter.PlayMaleVoAudioCommand.CanExecute(node));
        interpreter.PlayMaleVoAudioCommand.Execute(node);

        var player = (Soundpanel)typeof(InterpreterExportLoader)
            .GetField("voAudioPreviewPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(interpreter);
        Assert.IsNotNull(player, "The default preview must reach the sound player through both imports.");
        Assert.IsNotNull(player.CurrentLoadedExport);
        Assert.AreEqual(fixture.StreamPath, player.CurrentLoadedExport.FileRef.FilePath);
        Assert.AreEqual("ActualAudio", player.CurrentLoadedExport.ObjectName.Name);
        Assert.AreEqual(AudioId, player.CurrentLoadedExport.GetProperty<IntProperty>("Id").Value);
        var status = (TextBlock)interpreter.FindName("VoAudioPreviewStatus");
        Assert.AreEqual("Could not play this audio. Check that its audio data and AFC file are available.", status.Text,
            "An absent AFC must report unavailable audio after successful import resolution.");
        Assert.IsNull(typeof(Soundpanel).GetField("_audioPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(player), "No output audio device should be initialized for this missing-AFC fixture.");

        var previewCache = (PackageCache)typeof(InterpreterExportLoader)
            .GetField("voAudioPreviewPackageCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(interpreter);
        Assert.IsNotNull(previewCache);
        Assert.IsTrue(previewCache.Cache.Count > 0);
        interpreter.UnloadExport();
        Assert.AreEqual(0, previewCache.Cache.Count);
        Assert.IsNull(player.CurrentLoadedExport);
    });

    [DataTestMethod]
    [DataRow("Load")]
    [DataRow("Unload")]
    [DataRow("Dispose")]
    public void InterpreterReleasesImportedPackagesWhenItsTrackLifetimeEnds(string action) => RunOnSta(() =>
    {
        using var fixture = new ExternalAudioFixture(MEGame.LE3);
        ExportEntry track = fixture.CreateTrack("InterpTrackWwiseEvent");
        using var view = new InterpreterTestView();
        InterpreterExportLoader interpreter = view.Interpreter;
        interpreter.LoadExport(track);
        UPropertyTreeViewEntry node = AllNodes(interpreter).Single(entry => entry.ShowVoTrackAudioNavigation);
        Assert.IsTrue(interpreter.PlayMaleVoAudioCommand.CanExecute(node));
        VoTrackAudioImportResolver resolver = GetInterpreterResolver(interpreter);
        Assert.IsNotNull(resolver);
        Assert.IsTrue(GetResolverCache(resolver).Cache.Count > 0);

        switch (action)
        {
            case "Load":
                interpreter.LoadExport(fixture.Package.CreateExport("OtherObject", "Object", indexed: false));
                break;
            case "Unload":
                interpreter.UnloadExport();
                break;
            case "Dispose":
                view.DisposeInterpreter();
                break;
        }

        Assert.IsNull(GetInterpreterResolver(interpreter));
        Assert.AreEqual(0, GetResolverCache(resolver).Cache.Count);
        Assert.IsFalse(interpreter.PlayMaleVoAudioCommand.CanExecute(node), "Stale track nodes must not recreate the cache.");
    });

    private static (Property Property, StructProperty Parent) GetTrackProperty(ExportEntry track)
    {
        var keys = track.GetProperty<ArrayProperty<StructProperty>>(
            track.ClassName == "BioEvtSysTrackSubtitles" ? "m_aSubtitleData" : "WwiseEvents");
        StructProperty key = keys.Single();
        return (key.Properties[0], key);
    }

    private static PackageCache GetResolverCache(VoTrackAudioImportResolver resolver) =>
        (PackageCache)typeof(VoTrackAudioImportResolver).GetField("cache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(resolver);

    private static VoTrackAudioImportResolver GetInterpreterResolver(InterpreterExportLoader interpreter) =>
        (VoTrackAudioImportResolver)typeof(InterpreterExportLoader)
            .GetField("voTrackAudioImportResolver", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(interpreter);

    private static IEnumerable<UPropertyTreeViewEntry> AllNodes(InterpreterExportLoader interpreter) =>
        interpreter.PropertyNodes.SelectMany(DescendantsAndSelf);

    private static IEnumerable<UPropertyTreeViewEntry> DescendantsAndSelf(UPropertyTreeViewEntry node)
    {
        yield return node;
        foreach (UPropertyTreeViewEntry child in node.ChildrenProperties.SelectMany(DescendantsAndSelf))
            yield return child;
    }

    private static void RunOnSta(Action action)
    {
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

    private sealed class InterpreterTestView : IDisposable
    {
        private readonly Window window;
        private bool interpreterDisposed;
        public InterpreterExportLoader Interpreter { get; }

        public InterpreterTestView()
        {
            InitializeApplicationResources();
            Interpreter = new InterpreterExportLoader();
            window = new Window
            {
                Content = Interpreter, Width = 1500, Height = 700,
                ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000
            };
            window.Show();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }

        public void DisposeInterpreter()
        {
            if (interpreterDisposed)
                return;
            Interpreter.Dispose();
            interpreterDisposed = true;
        }

        public void Dispose()
        {
            window.Close();
            DisposeInterpreter();
        }
    }

    private sealed class ExternalAudioFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"LEX_VoAudioImports_{Guid.NewGuid():N}");
        public IMEPackage Package { get; }
        public ImportEntry EventImport { get; }
        public string EventPath { get; }
        public string StreamPath { get; }

        public ExternalAudioFixture(MEGame game, bool unavailableAudio = false)
        {
            Directory.CreateDirectory(directory);
            string unique = Guid.NewGuid().ToString("N");
            EventPath = Path.Combine(directory, $"VoiceEvents_{unique}.pcc");
            StreamPath = Path.Combine(directory, $"VoiceStreams_{unique}.pcc");
            using (IMEPackage streamPackage = CreatePackage(StreamPath, game))
            {
                ExportEntry audio = streamPackage.CreateExport("Audio", "Package", indexed: false);
                ExportEntry stream = streamPackage.CreateExport("ActualAudio", "WwiseStream", audio, indexed: false);
                var properties = new PropertyCollection { new IntProperty(AudioId, "Id") };
                string filename = unavailableAudio ? $"MissingVoice_{unique}" : null;
                if (filename != null)
                    properties.Add(new NameProperty(filename, "Filename"));
                stream.WritePropertiesAndBinary(properties, new WwiseStream
                {
                    Unk1 = 1,
                    Filename = filename,
                    EmbeddedData = unavailableAudio ? null : EmbeddedAudio.ToArray()
                });
                streamPackage.Save();
            }
            using (IMEPackage eventPackage = CreatePackage(EventPath, game))
            {
                ImportEntry stream = ImportAudio(eventPackage, Path.GetFileNameWithoutExtension(StreamPath), "ActualAudio", "WwiseStream");
                ExportEntry audio = eventPackage.CreateExport("Audio", "Package", indexed: false);
                ExportEntry audioEvent = eventPackage.CreateExport($"VO_{StringRef}_m_Play", "WwiseEvent", audio, indexed: false);
                var properties = new PropertyCollection();
                if (game == MEGame.LE2)
                {
                    var relationships = new StructProperty("WwiseRelationships", false,
                        new ArrayProperty<ObjectProperty>([new ObjectProperty(stream)], "Streams")) { Name = "Relationships" };
                    properties.Add(new ArrayProperty<StructProperty>("References")
                    {
                        new("WwisePlatformRelationships", false, relationships)
                    });
                }
                audioEvent.WritePropertiesAndBinary(properties, new WwiseEvent
                {
                    WwiseEventID = 123,
                    Links = [new WwiseEvent.WwiseEventLink { WwiseBanks = [], WwiseStreams = [stream.UIndex] }]
                });
                eventPackage.Save();
            }

            Package = CreatePackage(Path.Combine(directory, $"Track_{unique}.pcc"), game);
            EventImport = ImportAudio(Package, Path.GetFileNameWithoutExtension(EventPath), $"VO_{StringRef}_m_Play", "WwiseEvent");
        }

        public ExportEntry CreateTrack(string className)
        {
            bool subtitles = className == "BioEvtSysTrackSubtitles";
            ExportEntry track = Package.CreateExport("Track", className, indexed: false);
            track.WriteProperties(new PropertyCollection
            {
                new ArrayProperty<StructProperty>(subtitles ? "m_aSubtitleData" : "WwiseEvents")
                {
                    new(subtitles ? "BioSubtitleTrackData" : "WwiseEventTrackKey", new PropertyCollection
                    {
                        subtitles ? new IntProperty(StringRef, "nStrRefID") : new ObjectProperty(EventImport, "Event")
                    })
                },
                new IntProperty(7, "UnrelatedSelection")
            });
            return track;
        }

        private static IMEPackage CreatePackage(string path, MEGame game)
        {
            IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage(path, game);
            package.setFlags(package.Flags & ~UnrealFlags.EPackageFlags.RequireImportsAlreadyLoaded);
            return package;
        }

        private static ImportEntry ImportAudio(IMEPackage package, string packageName, string objectName, string className)
        {
            ImportEntry root = AddImport(package, null, packageName, "Package");
            ImportEntry audio = AddImport(package, root, "Audio", "Package");
            return AddImport(package, audio, objectName, className);
        }

        private static ImportEntry AddImport(IMEPackage package, IEntry parent, string name, string className)
        {
            var entry = new ImportEntry(package, parent, name)
            {
                ClassName = className,
                PackageFile = className == "Package" ? "Core" : "WwiseAudio"
            };
            package.AddImport(entry);
            return entry;
        }

        public void Dispose()
        {
            Package.Dispose();
            File.Delete(EventPath);
            File.Delete(StreamPath);
            Directory.Delete(directory);
        }
    }
}
