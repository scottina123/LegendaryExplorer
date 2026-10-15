using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class VoTrackAudioNavigationTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow(MEGame.ME1, false)]
    [DataRow(MEGame.ME2, true)]
    [DataRow(MEGame.ME3, true)]
    [DataRow(MEGame.LE1, false)]
    [DataRow(MEGame.LE2, true)]
    [DataRow(MEGame.LE3, true)]
    public void AudioActionsAreScopedToVoTrackStringReferencesInWwiseGames(MEGame game, bool supported)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("VoTrackScope.pcc", game);
        ExportEntry track = CreateTrack(package);
        ExportEntry other = package.CreateExport("Other", "Object", indexed: false);

        Assert.AreEqual(supported, VoTrackAudioNavigation.IsTrackStringRef(track, new IntProperty(42, "m_nStrRefID")));
        Assert.AreEqual(supported, VoTrackAudioNavigation.IsTrackStringRef(track, new StringRefProperty(42, "m_nStrRefID")));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackStringRef(track, new IntProperty(42, "OtherStringRef")));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackStringRef(track, new FloatProperty(42, "m_nStrRefID")));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackStringRef(other, new IntProperty(42, "m_nStrRefID")));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackStringRef(null, new IntProperty(42, "m_nStrRefID")));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackStringRef(track, null));
    }

    [TestMethod]
    [DataRow("VO_42_m_Play", false)]
    [DataRow("Play_VO_42_m", false)]
    [DataRow("vo_00000042_F_pLaY", true)]
    [DataRow("pLaY_vO_00000042_F", true)]
    public void EventLookupRecognizesBothPlayNameFormatsAndPaddedIds(string name, bool female)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("VoTrackEventNames.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        ExportEntry target = package.CreateExport(name, "WwiseEvent", indexed: false);
        package.CreateExport("VO_42_m_Stop", "WwiseEvent", indexed: false);
        package.CreateExport("VO_42_f_Stop", "WwiseEvent", indexed: false);

        Assert.AreSame(target, VoTrackAudioNavigation.FindAudioEntry(track, 42, female, stream: false));
        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 42, !female, stream: false));
    }

    [TestMethod]
    [DataRow("VO_142_m_Play")]
    [DataRow("VO_421_m_Play")]
    [DataRow("VO_42_m_Stop")]
    [DataRow("Stop_VO_42_m")]
    [DataRow("VO_42_m_Play_Extra")]
    [DataRow("Prefix_VO_42_m_Play")]
    [DataRow("Play_VO_42_m_Extra")]
    [DataRow("VO_42_male_Play")]
    [DataRow("VO_42_f_Play")]
    [DataRow("VO_42a_m_Play")]
    public void EventLookupRejectsPartialIdsWrongGenderAndNonPlayEvents(string name)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("VoTrackWrongEvents.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        package.CreateExport(name, "WwiseEvent", indexed: false);

        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 42, isFemale: false, stream: false));
    }

    [TestMethod]
    public void EventLookupCanNavigateAnImportAndIgnoresOtherClasses()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("VoTrackImportedEvents.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        ImportEntry target = CreateImport(package, "VO_42_m_Play", "WwiseEvent");
        package.CreateExport("Play_VO_42_m", "Object", indexed: false);

        Assert.AreSame(target, VoTrackAudioNavigation.FindAudioEntry(track, 42, isFemale: false, stream: false));
    }

    [TestMethod]
    public void MultipleMatchingPlayEventsDisableEventAndStreamNavigation()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("VoTrackAmbiguousEvents.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        package.CreateExport("VO_42_m_Play", "WwiseEvent", indexed: false);
        CreateImport(package, "Play_VO_00000042_m", "WwiseEvent");
        package.CreateExport("player_00000042_m_wav", "WwiseStream", indexed: false);

        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 42, isFemale: false, stream: false));
        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 42, isFemale: false, stream: true));
    }

    [TestMethod]
    [DataRow("player_00000042_m_wav", false)]
    [DataRow("player_42_M", false)]
    [DataRow("player,00000042,F,wav", true)]
    [DataRow("42_f", true)]
    public void StreamFallbackMatchesCompleteNumericAndGenderTokens(string name, bool female)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("VoTrackStreamNames.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        ExportEntry target = package.CreateExport(name, "WwiseStream", indexed: false);

        Assert.AreSame(target, VoTrackAudioNavigation.FindAudioEntry(track, 42, female, stream: true));
        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 42, !female, stream: true));
    }

    [TestMethod]
    [DataRow("player_142_m_wav")]
    [DataRow("player_421_m_wav")]
    [DataRow("player_00000421_m_wav")]
    [DataRow("player_42a_m_wav")]
    [DataRow("player_a42_m_wav")]
    [DataRow("player_42_male_wav")]
    [DataRow("player_42_f_wav")]
    [DataRow("player_42_voice_m_wav")]
    public void StreamFallbackRejectsPartialIdsAndNonAdjacentGenderTokens(string name)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("VoTrackWrongStreams.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        package.CreateExport(name, "WwiseStream", indexed: false);

        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 42, isFemale: false, stream: true));
    }

    [TestMethod]
    public void ImportedEventFallsBackToImportedStreamAndAmbiguousStreamsDisableNavigation()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("VoTrackImportedStreams.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        CreateImport(package, "VO_42_m_Play", "WwiseEvent");
        ImportEntry target = CreateImport(package, "player_00000042_m_wav", "WwiseStream");
        package.CreateExport("other_42_m_wav", "Object", indexed: false);

        Assert.AreSame(target, VoTrackAudioNavigation.FindAudioEntry(track, 42, isFemale: false, stream: true));

        package.CreateExport("other_42_m_wav", "WwiseStream", indexed: false);
        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 42, isFemale: false, stream: true));
    }

    [TestMethod]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE3)]
    public void StreamLookupUsesEventBinaryReferencesBeforeNamedFallback(MEGame game)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("VoTrackReferencedStream.pcc", game);
        ExportEntry track = CreateTrack(package);
        ExportEntry target = package.CreateExport("ActualAudio", "WwiseStream", indexed: false);
        package.CreateExport("player_00000042_f_wav", "WwiseStream", indexed: false);
        ExportEntry wwiseEvent = package.CreateExport("VO_42_f_Play", "WwiseEvent", indexed: false);
        WriteBinaryReferences(wwiseEvent, target);

        Assert.AreSame(target, VoTrackAudioNavigation.FindAudioEntry(track, 42, isFemale: true, stream: true));
    }

    [TestMethod]
    public void FemaleEventCanReferenceSharedNpcAudioWithMaleSuffix()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("VoTrackSharedNpcStream.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        ExportEntry target = package.CreateExport("npc_00000042_m_wav", "WwiseStream", indexed: false);
        ExportEntry maleEvent = package.CreateExport("VO_42_m_Play", "WwiseEvent", indexed: false);
        ExportEntry femaleEvent = package.CreateExport("VO_42_f_Play", "WwiseEvent", indexed: false);
        WriteBinaryReferences(maleEvent, target);
        WriteBinaryReferences(femaleEvent, target);

        Assert.AreSame(target, VoTrackAudioNavigation.FindAudioEntry(track, 42, isFemale: false, stream: true));
        Assert.AreSame(target, VoTrackAudioNavigation.FindAudioEntry(track, 42, isFemale: true, stream: true));
    }

    [TestMethod]
    public void MultipleEventReferencesCannotResolveByPartialPaddedId()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("VoTrackPartialReferencedId.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        ExportEntry wrongId = package.CreateExport("scene_900001234_m_wav", "WwiseStream", indexed: false);
        ExportEntry unrelated = package.CreateExport("OtherAudio", "WwiseStream", indexed: false);
        ExportEntry wwiseEvent = package.CreateExport("VO_1234_m_Play", "WwiseEvent", indexed: false);
        WriteBinaryReferences(wwiseEvent, wrongId, unrelated);

        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 1234, isFemale: false, stream: true));
    }

    [TestMethod]
    public void Le2StreamLookupUsesReferencesProperty()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("VoTrackLe2References.pcc", MEGame.LE2);
        ExportEntry track = CreateTrack(package);
        ExportEntry target = package.CreateExport("ActualAudio", "WwiseStream", indexed: false);
        ExportEntry wwiseEvent = package.CreateExport("Play_VO_42_f", "WwiseEvent", indexed: false);
        var relationships = new StructProperty("WwiseRelationships", false,
            new ArrayProperty<ObjectProperty>([new ObjectProperty(target)], "Streams"))
        {
            Name = "Relationships"
        };
        wwiseEvent.WritePropertiesAndBinary(new PropertyCollection
        {
            new ArrayProperty<StructProperty>("References")
            {
                new("WwisePlatformRelationships", false, relationships)
            }
        }, new WwiseEvent { WwiseEventID = 123, Links = [] });

        Assert.AreSame(target, VoTrackAudioNavigation.FindAudioEntry(track, 42, isFemale: true, stream: true));
    }

    [TestMethod]
    public void MalformedOrUnresolvableEventFallsBackToNamedStreamWithoutChangingPackage()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("VoTrackMalformedEvent.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        ExportEntry target = package.CreateExport("player_00000042_m_wav", "WwiseStream", indexed: false);
        ExportEntry wwiseEvent = package.CreateExport("VO_42_m_Play", "WwiseEvent", indexed: false);
        wwiseEvent.WriteProperties(new PropertyCollection());
        wwiseEvent.WriteBinary([0x7f]);
        byte[] originalData = wwiseEvent.Data.ToArray();

        Assert.AreSame(target, VoTrackAudioNavigation.FindAudioEntry(track, 42, isFemale: false, stream: true));
        CollectionAssert.AreEqual(originalData, wwiseEvent.Data);

        wwiseEvent.WritePropertiesAndBinary(new PropertyCollection(), new WwiseEvent
        {
            Links = [new WwiseEvent.WwiseEventLink { WwiseBanks = [], WwiseStreams = [int.MaxValue] }]
        });
        Assert.AreSame(target, VoTrackAudioNavigation.FindAudioEntry(track, 42, isFemale: false, stream: true));
    }

    [TestMethod]
    [DataRow(MEGame.ME1)]
    [DataRow(MEGame.LE1)]
    public void UnsupportedGamesNeverResolveAudio(MEGame game)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("VoTrackUnsupportedGame.pcc", game);
        ExportEntry track = CreateTrack(package);
        CreateImport(package, "VO_42_m_Play", "WwiseEvent");
        CreateImport(package, "player_00000042_m_wav", "WwiseStream");

        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 42, isFemale: false, stream: false));
        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 42, isFemale: false, stream: true));
    }

    [TestMethod]
    public void NullWrongClassAndInvalidStringReferencesNeverResolveAudio()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("VoTrackInvalidReference.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        ExportEntry other = package.CreateExport("Other", "Object", indexed: false);
        package.CreateExport("VO_42_m_Play", "WwiseEvent", indexed: false);
        package.CreateExport("player_00000042_m_wav", "WwiseStream", indexed: false);

        foreach (bool stream in new[] { false, true })
        {
            Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(null, 42, false, stream));
            Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(other, 42, false, stream));
            Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 0, false, stream));
            Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, -42, false, stream));
        }
    }

    private static ExportEntry CreateTrack(IMEPackage package) =>
        package.CreateExport("Track", "BioEvtSysTrackVOElements", indexed: false);

    private static ImportEntry CreateImport(IMEPackage package, string name, string className)
    {
        var entry = new ImportEntry(package) { ObjectName = name, ClassName = className, PackageFile = "WwiseAudio" };
        package.AddImport(entry);
        return entry;
    }

    private static void WriteBinaryReferences(ExportEntry wwiseEvent, params ExportEntry[] streams) =>
        wwiseEvent.WritePropertiesAndBinary(new PropertyCollection(), new WwiseEvent
        {
            Links = [new WwiseEvent.WwiseEventLink { WwiseBanks = [], WwiseStreams = streams.Select(stream => stream.UIndex).ToList() }]
        });
}
