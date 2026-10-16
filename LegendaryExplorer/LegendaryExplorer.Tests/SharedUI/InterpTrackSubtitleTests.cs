using System;
using System.Threading.Tasks;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.TLK.ME2ME3;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.SharedUI;

[TestClass]
public class InterpTrackSubtitleTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void InterpDataIncludesSubtitleTracksAndImportedWwiseDialogueEvents()
    {
        using var subtitles = new SubtitleTestSettings();
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpTrackSubtitles.pcc", MEGame.LE3);
        ExportEntry interpData = package.CreateExport("InterpData", "InterpData", indexed: false);
        ExportEntry group = package.CreateExport("InterpGroup", "InterpGroup", interpData, indexed: false);
        ExportEntry voTrack = package.CreateExport("VO", "BioEvtSysTrackVOElements", group, indexed: false);
        ExportEntry subtitleTrack = package.CreateExport("Subtitles", "BioEvtSysTrackSubtitles", group, indexed: false);
        ExportEntry wwiseTrack = package.CreateExport("Audio", "InterpTrackWwiseEvent", group, indexed: false);
        ImportEntry audioEvent = CreateAudioEventImport(package, "Play_VO_692099_f");
        voTrack.WriteProperty(new IntProperty(692097, "m_nStrRefID"));
        subtitleTrack.WriteProperty(new ArrayProperty<StructProperty>([
            SubtitleKey(692098), SubtitleKey(0), SubtitleKey(123456789)], "m_aSubtitleData"));
        wwiseTrack.WriteProperty(new ArrayProperty<StructProperty>([
            EventKey(audioEvent.UIndex), EventKey(0), EventKey(audioEvent.UIndex)], "WwiseEvents"));
        group.WriteProperty(new ArrayProperty<ObjectProperty>([
            new ObjectProperty(voTrack), new ObjectProperty(0), new ObjectProperty(subtitleTrack), new ObjectProperty(wwiseTrack)], "InterpTracks"));
        interpData.WriteProperty(new ArrayProperty<ObjectProperty>([
            new ObjectProperty(0), new ObjectProperty(group)], "InterpGroups"));

        using var interpEntry = new TreeViewEntry(interpData);
        using var subtitleEntry = new TreeViewEntry(subtitleTrack);
        using var wwiseEntry = new TreeViewEntry(wwiseTrack);

        Assert.AreEqual("\"Existing VO line\"\n\"Subtitle track line\"\n\"Wwise event line\"", interpEntry.SubText);
        Assert.AreEqual("\"Subtitle track line\"", subtitleEntry.SubText);
        Assert.AreEqual("\"Wwise event line\"", wwiseEntry.SubText);
    }

    [TestMethod]
    [DataRow("VO_692099_m_Play", "WwiseEvent", true)]
    [DataRow("pLaY_vO_00692099_F", "WwiseEvent", true)]
    [DataRow("VO_692099_m_Stop", "WwiseEvent", false)]
    [DataRow("VO_692099_m_Play_Extra", "WwiseEvent", false)]
    [DataRow("VO_692099_male_Play", "WwiseEvent", false)]
    [DataRow("VO_692099_m_Play", "Object", false)]
    public void WwiseTrackSubtitlesOnlyUseDialoguePlayEvents(string eventName, string className, bool expectedSubtitle)
    {
        using var subtitles = new SubtitleTestSettings();
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseTrackSubtitles.pcc", MEGame.LE3);
        ExportEntry track = package.CreateExport("Track", "InterpTrackWwiseEvent", indexed: false);
        ExportEntry audioEvent = package.CreateExport(eventName, className, indexed: false);
        track.WriteProperty(new ArrayProperty<StructProperty>([EventKey(audioEvent.UIndex)], "WwiseEvents"));

        using var entry = new TreeViewEntry(track);

        Assert.AreEqual(expectedSubtitle ? "\"Wwise event line\"" : null, entry.SubText);
    }

    private static StructProperty SubtitleKey(int stringRef) =>
        new("BioSubtitleTrackData", new PropertyCollection { new IntProperty(stringRef, "nStrRefID") });

    private static StructProperty EventKey(int eventIndex) =>
        new("WwiseEventTrackKey", new PropertyCollection { new ObjectProperty(eventIndex, "Event") });

    private static ImportEntry CreateAudioEventImport(IMEPackage package, string name)
    {
        var import = new ImportEntry(package, 0, name)
        {
            ClassName = "WwiseEvent",
            PackageFile = "WwiseAudio"
        };
        package.AddImport(import);
        return import;
    }

    private sealed class SubtitleTestSettings : IDisposable
    {
        private readonly bool _previousShowSubtitles = Settings.PackageEditor_ShowTreeEntrySubText;
        private readonly ME2ME3LazyTLK _talkFile = new();

        public SubtitleTestSettings()
        {
            using var stream = HuffmanCompression.SaveToTlkStream([
                new TLKStringRef(692097, "Existing VO line"),
                new TLKStringRef(692098, "Subtitle track line"),
                new TLKStringRef(692099, "Wwise event line")]);
            stream.Position = 0;
            _talkFile.LoadTlkDataFromStream(stream);
            LE3TalkFiles.LoadedTlks.Add(_talkFile);
            Settings.PackageEditor_ShowTreeEntrySubText = true;
        }

        public void Dispose()
        {
            LE3TalkFiles.LoadedTlks.Remove(_talkFile);
            Settings.PackageEditor_ShowTreeEntrySubText = _previousShowSubtitles;
        }
    }
}
