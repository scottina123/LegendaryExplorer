using System.Collections.Generic;
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
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackStringRef(track, new IntProperty(42, "nLineStrRef"),
            new StructProperty("BioFOVOTrackData", new PropertyCollection(), "TrackData")));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackStringRef(track, new FloatProperty(42, "m_nStrRefID")));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackStringRef(other, new IntProperty(42, "m_nStrRefID")));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackStringRef(null, new IntProperty(42, "m_nStrRefID")));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackStringRef(track, null));
    }

    [TestMethod]
    [DataRow(MEGame.ME1, false)]
    [DataRow(MEGame.ME2, true)]
    [DataRow(MEGame.ME3, true)]
    [DataRow(MEGame.LE1, false)]
    [DataRow(MEGame.LE2, true)]
    [DataRow(MEGame.LE3, true)]
    public void FaceOnlyVoActionsRequireLineStringReferenceInsideTrackData(MEGame game, bool supported)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("FaceOnlyVoScope.pcc", game);
        ExportEntry track = package.CreateExport("Track", "SFXInterpTrackPlayFaceOnlyVO", indexed: false);
        var trackData = new StructProperty("BioFOVOTrackData", new PropertyCollection(), "TrackData");
        var wrongStruct = new StructProperty("OtherTrackData", new PropertyCollection(), "TrackData");

        Assert.AreEqual(supported, VoTrackAudioNavigation.IsTrackStringRef(track, new IntProperty(42, "nLineStrRef"), trackData));
        Assert.AreEqual(supported, VoTrackAudioNavigation.IsTrackStringRef(track, new StringRefProperty(42, "nLineStrRef"), trackData));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackStringRef(track, new IntProperty(42, "nLineStrRef")));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackStringRef(track, new IntProperty(42, "nLineStrRef"), wrongStruct));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackStringRef(track, new IntProperty(42, "nLineStrRef"), new IntProperty(0, "TrackData")));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackStringRef(track, new StringRefProperty(42, "srActorNameOverride"), trackData));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackStringRef(track, new IntProperty(42, "m_nStrRefID"), trackData));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackStringRef(track, new FloatProperty(42, "nLineStrRef"), trackData));
    }

    [TestMethod]
    [DataRow(MEGame.ME1, false)]
    [DataRow(MEGame.ME2, true)]
    [DataRow(MEGame.ME3, true)]
    [DataRow(MEGame.LE1, false)]
    [DataRow(MEGame.LE2, true)]
    [DataRow(MEGame.LE3, true)]
    public void SubtitleActionsRequireStringReferenceInsideSubtitleTrackData(MEGame game, bool supported)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("SubtitleTrackScope.pcc", game);
        ExportEntry track = package.CreateExport("Track", "BioEvtSysTrackSubtitles", indexed: false);
        ExportEntry voTrack = CreateTrack(package);
        var trackData = new StructProperty("BioSubtitleTrackData", new PropertyCollection(), "m_aSubtitleData");

        foreach (Property property in new Property[] { new IntProperty(42, "nStrRefID"), new StringRefProperty(42, "nStrRefID") })
        {
            Assert.AreEqual(supported, VoTrackAudioNavigation.IsTrackStringRef(track, property, trackData));
            Assert.AreEqual(supported, VoTrackAudioNavigation.IsTrackAudioProperty(track, property, trackData));
            Assert.IsFalse(VoTrackAudioNavigation.IsTrackAudioProperty(track, property));
            Assert.IsFalse(VoTrackAudioNavigation.IsTrackAudioProperty(track, property,
                new StructProperty("BioFOVOTrackData", new PropertyCollection(), "TrackData")));
            Assert.IsFalse(VoTrackAudioNavigation.IsTrackAudioProperty(voTrack, property, trackData));
        }

        Assert.IsFalse(VoTrackAudioNavigation.IsTrackAudioProperty(track, new IntProperty(42, "m_nStrRefID"), trackData));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackAudioProperty(track, new FloatProperty(42, "nStrRefID"), trackData));
    }

    [TestMethod]
    [DataRow(MEGame.ME1, false)]
    [DataRow(MEGame.ME2, true)]
    [DataRow(MEGame.ME3, true)]
    [DataRow(MEGame.LE1, false)]
    [DataRow(MEGame.LE2, true)]
    [DataRow(MEGame.LE3, true)]
    public void WwiseTrackActionsRequireEventObjectInsideWwiseEventTrackKey(MEGame game, bool supported)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseTrackScope.pcc", game);
        ExportEntry track = package.CreateExport("Track", "InterpTrackWwiseEvent", indexed: false);
        var key = new StructProperty("WwiseEventTrackKey", new PropertyCollection(), "WwiseEvents");
        var property = new ObjectProperty(1, "Event");

        Assert.AreEqual(supported, VoTrackAudioNavigation.IsTrackAudioProperty(track, property, key));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackStringRef(track, property, key));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackAudioProperty(track, property));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackAudioProperty(track, property,
            new StructProperty("OtherTrackKey", new PropertyCollection(), "WwiseEvents")));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackAudioProperty(track, property, new IntProperty(0, "WwiseEvents")));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackAudioProperty(track, new IntProperty(1, "Event"), key));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackAudioProperty(track, new ObjectProperty(1, "OtherEvent"), key));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackAudioProperty(CreateTrack(package), property, key));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackAudioProperty(null, property, key));
        Assert.IsFalse(VoTrackAudioNavigation.IsTrackAudioProperty(track, null, key));
    }

    [TestMethod]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void SubtitlePropertiesResolveGenderedEventsAndNamedStreams(MEGame game)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("SubtitleTrackAudio.pcc", game);
        ExportEntry track = package.CreateExport("Track", "BioEvtSysTrackSubtitles", indexed: false);
        var trackData = new StructProperty("BioSubtitleTrackData", new PropertyCollection(), "m_aSubtitleData");
        ExportEntry male = package.CreateExport("VO_42_m_Play", "WwiseEvent", indexed: false);
        ExportEntry female = package.CreateExport("Play_VO_42_f", "WwiseEvent", indexed: false);
        ExportEntry maleStream = package.CreateExport("player_00000042_m_wav", "WwiseStream", indexed: false);
        ExportEntry femaleStream = package.CreateExport("player_00000042_f_wav", "WwiseStream", indexed: false);

        foreach (Property property in new Property[] { new IntProperty(42, "nStrRefID"), new StringRefProperty(42, "nStrRefID") })
        {
            Assert.AreSame(male, VoTrackAudioNavigation.FindAudioEntry(track, property, trackData, false, stream: false));
            Assert.AreSame(female, VoTrackAudioNavigation.FindAudioEntry(track, property, trackData, true, stream: false));
            Assert.AreSame(maleStream, VoTrackAudioNavigation.FindAudioEntry(track, property, trackData, false, stream: true));
            Assert.AreSame(femaleStream, VoTrackAudioNavigation.FindAudioEntry(track, property, trackData, true, stream: true));
            Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, property, null, false, stream: false));
        }

        foreach (int invalidId in new[] { 0, -42 })
        {
            Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, new IntProperty(invalidId, "nStrRefID"),
                trackData, false, stream: false));
            Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, new StringRefProperty(invalidId, "nStrRefID"),
                trackData, false, stream: true));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExplicitWwiseEventReferenceOverridesDuplicateNamesAndUsesItsStream(bool female)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseTrackDirectEvent.pcc", MEGame.LE3);
        ExportEntry track = package.CreateExport("Track", "InterpTrackWwiseEvent", indexed: false);
        var key = new StructProperty("WwiseEventTrackKey", new PropertyCollection(), "WwiseEvents");
        string name = $"VO_42_{(female ? "f" : "m")}_Play";
        ExportEntry decoy = package.CreateExport(name, "WwiseEvent", indexed: false);
        ExportEntry target = package.CreateExport(name, "WwiseEvent", indexed: false);
        CreateImport(package, name, "WwiseEvent");
        ExportEntry decoyStream = package.CreateExport("DecoyAudio", "WwiseStream", indexed: false);
        ExportEntry targetStream = package.CreateExport("ActualAudio", "WwiseStream", indexed: false);
        package.CreateExport($"player_42_{(female ? "f" : "m")}_wav", "WwiseStream", indexed: false);
        WriteBinaryReferences(decoy, decoyStream);
        WriteBinaryReferences(target, targetStream);
        var eventRef = new ObjectProperty(target.UIndex, "Event");

        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 42, female, stream: false));
        Assert.AreSame(target, VoTrackAudioNavigation.FindAudioEntry(track, eventRef, key, female, stream: false));
        Assert.AreSame(targetStream, VoTrackAudioNavigation.FindAudioEntry(track, eventRef, key, female, stream: true));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ImportedWwiseEventReferenceUsesNamedStreamAndFindsOppositeGender(bool female)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseTrackImportedEvent.pcc", MEGame.LE3);
        ExportEntry track = package.CreateExport("Track", "InterpTrackWwiseEvent", indexed: false);
        var key = new StructProperty("WwiseEventTrackKey", new PropertyCollection(), "WwiseEvents");
        string gender = female ? "f" : "m";
        string oppositeGender = female ? "m" : "f";
        ImportEntry target = CreateImport(package, $"Play_VO_00000042_{gender}", "WwiseEvent");
        ImportEntry stream = CreateImport(package, $"player_42_{gender}_wav", "WwiseStream");
        ImportEntry counterpart = CreateImport(package, $"VO_42_{oppositeGender}_Play", "WwiseEvent");
        ImportEntry counterpartStream = CreateImport(package, $"player_42_{oppositeGender}_wav", "WwiseStream");
        var eventRef = new ObjectProperty(target.UIndex, "Event");

        Assert.AreSame(target, VoTrackAudioNavigation.FindAudioEntry(track, eventRef, key, female, stream: false));
        Assert.AreSame(stream, VoTrackAudioNavigation.FindAudioEntry(track, eventRef, key, female, stream: true));
        Assert.AreSame(counterpart, VoTrackAudioNavigation.FindAudioEntry(track, eventRef, key, !female, stream: false));
        Assert.AreSame(counterpartStream, VoTrackAudioNavigation.FindAudioEntry(track, eventRef, key, !female, stream: true));
    }

    [TestMethod]
    public void ExportedEventCanReferenceAnArbitrarilyNamedImportedStream()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseImportedStreamReference.pcc", MEGame.LE3);
        ExportEntry track = package.CreateExport("Track", "InterpTrackWwiseEvent", indexed: false);
        ExportEntry wwiseEvent = package.CreateExport("VO_42_m_Play", "WwiseEvent", indexed: false);
        ImportEntry audioPackage = CreateImport(package, "DialogueAudio", "Package");
        ImportEntry audioGroup = CreateImport(package, "Audio", "Package", audioPackage);
        ImportEntry target = CreateImport(package, "OpaqueSharedNpcAudio", "WwiseStream", audioGroup);
        package.CreateExport("player_42_m_wav", "WwiseStream", indexed: false);
        WriteBinaryReferences(wwiseEvent, target);
        var key = new StructProperty("WwiseEventTrackKey", new PropertyCollection(), "WwiseEvents");

        Assert.AreSame(target, VoTrackAudioNavigation.FindAudioEntry(track, 42, false, stream: true));
        Assert.AreSame(target, VoTrackAudioNavigation.FindAudioEntry(track,
            new ObjectProperty(wwiseEvent.UIndex, "Event"), key, false, stream: true));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ResolvedImportedEventUsesReferencedStreamAndPrefersMatchingLocalImport(bool importedStream, bool localStream)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseResolvedEventTrack.pcc", MEGame.LE3);
        using IMEPackage externalPackage = MEPackageHandler.CreateMemoryEmptyPackage("DialogueAudio.pcc", MEGame.LE3);
        ExportEntry track = package.CreateExport("Track", "InterpTrackWwiseEvent", indexed: false);
        ExportEntry externalRoot = externalPackage.CreateExport("DialogueAudio", "Package", indexed: false);
        ExportEntry externalAudio = externalPackage.CreateExport("Audio", "Package", externalRoot, indexed: false);
        ExportEntry externalEvent = externalPackage.CreateExport("VO_42_m_Play", "WwiseEvent", externalAudio, indexed: false);
        ImportEntry localRoot = CreateImport(package, "DialogueAudio", "Package");
        ImportEntry localAudio = CreateImport(package, "Audio", "Package", localRoot);
        ImportEntry eventRef = CreateImport(package, "VO_42_m_Play", "WwiseEvent", localAudio);
        IEntry externalStream;
        IEntry localStreamParent = localAudio;
        if (importedStream)
        {
            ImportEntry streamRoot = CreateImport(externalPackage, "StreamAudio", "Package");
            ImportEntry streamAudio = CreateImport(externalPackage, "Audio", "Package", streamRoot);
            externalStream = CreateImport(externalPackage, "OpaqueAudio", "WwiseStream", streamAudio);
            ImportEntry localStreamRoot = CreateImport(package, "StreamAudio", "Package");
            localStreamParent = CreateImport(package, "Audio", "Package", localStreamRoot);
        }
        else
        {
            externalStream = externalPackage.CreateExport("OpaqueAudio", "WwiseStream", externalAudio, indexed: false);
            ExportEntry localExportRoot = package.CreateExport("DialogueAudio", "Package", indexed: false);
            ExportEntry localExportAudio = package.CreateExport("Audio", "Package", localExportRoot, indexed: false);
            ExportEntry unrelatedLocalExport = package.CreateExport("OpaqueAudio", "WwiseStream", localExportAudio, indexed: false);
            Assert.AreEqual(externalStream.InstancedFullPath, unrelatedLocalExport.InstancedFullPath);
        }

        WriteBinaryReferences(externalEvent, externalStream);
        CreateImport(package, "OpaqueAudio", "Object", localStreamParent);
        CreateImport(package, "OpaqueAudio", "WwiseStream");
        IEntry expected = externalStream;
        if (localStream)
        {
            expected = CreateImport(package, "OpaqueAudio", "WwiseStream", localStreamParent);
            Assert.AreEqual(externalStream.InstancedFullPath, expected.InstancedFullPath);
        }

        var resolutions = new Dictionary<ImportEntry, ExportEntry> { [eventRef] = externalEvent };
        var key = new StructProperty("WwiseEventTrackKey", new PropertyCollection(), "WwiseEvents");

        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 42, false, stream: true));
        Assert.AreSame(expected, VoTrackAudioNavigation.FindAudioEntry(track, 42, false, stream: true,
            resolveImport: import => resolutions.GetValueOrDefault(import)));
        Assert.AreSame(expected, VoTrackAudioNavigation.FindAudioEntry(track,
            new ObjectProperty(eventRef.UIndex, "Event"), key, false, stream: true,
            resolveImport: import => resolutions.GetValueOrDefault(import)));
    }

    [TestMethod]
    public void NativeExternalStreamMapsToImportWithSourcePackageFilenamePrefix()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseNativeStreamTrack.pcc", MEGame.LE3);
        using IMEPackage externalPackage = MEPackageHandler.CreateMemoryEmptyPackage("DialogueAudio.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        ImportEntry eventRef = CreateImport(package, "VO_42_m_Play", "WwiseEvent");
        ExportEntry externalAudio = externalPackage.CreateExport("Audio", "Package", indexed: false);
        ExportEntry externalEvent = externalPackage.CreateExport("VO_42_m_Play", "WwiseEvent", externalAudio, indexed: false);
        ExportEntry externalStream = externalPackage.CreateExport("OpaqueAudio", "WwiseStream", externalAudio, indexed: false);
        WriteBinaryReferences(externalEvent, externalStream);
        ImportEntry localRoot = CreateImport(package, "DialogueAudio", "Package");
        ImportEntry localAudio = CreateImport(package, "Audio", "Package", localRoot);
        ImportEntry target = CreateImport(package, "OpaqueAudio", "WwiseStream", localAudio);
        ExportEntry decoyAudio = package.CreateExport("Audio", "Package", indexed: false);
        ExportEntry decoy = package.CreateExport("OpaqueAudio", "WwiseStream", decoyAudio, indexed: false);
        var resolutions = new Dictionary<ImportEntry, ExportEntry> { [eventRef] = externalEvent };

        Assert.IsFalse(externalStream.IsForcedExport);
        Assert.AreEqual("Audio.OpaqueAudio", externalStream.InstancedFullPath);
        Assert.AreEqual("DialogueAudio.Audio.OpaqueAudio", target.InstancedFullPath);
        Assert.AreEqual(externalStream.InstancedFullPath, decoy.InstancedFullPath);
        Assert.AreSame(target, VoTrackAudioNavigation.FindAudioEntry(track, 42, false, stream: true,
            resolveImport: import => resolutions.GetValueOrDefault(import)));
    }

    [TestMethod]
    public void ResolverFollowsOppositeGenderEventAndRejectsAmbiguousCounterparts()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseResolvedCounterpart.pcc", MEGame.LE3);
        using IMEPackage externalPackage = MEPackageHandler.CreateMemoryEmptyPackage("DialogueAudio.pcc", MEGame.LE3);
        ExportEntry track = package.CreateExport("Track", "InterpTrackWwiseEvent", indexed: false);
        ImportEntry maleRef = CreateImport(package, "VO_42_m_Play", "WwiseEvent");
        ImportEntry femaleRef = CreateImport(package, "VO_42_f_Play", "WwiseEvent");
        ExportEntry maleEvent = externalPackage.CreateExport("VO_42_m_Play", "WwiseEvent", indexed: false);
        ExportEntry femaleEvent = externalPackage.CreateExport("VO_42_f_Play", "WwiseEvent", indexed: false);
        ExportEntry maleStream = externalPackage.CreateExport("OpaqueMaleAudio", "WwiseStream", indexed: false);
        ImportEntry femaleStream = CreateImport(externalPackage, "OpaqueFemaleAudio", "WwiseStream");
        WriteBinaryReferences(maleEvent, maleStream);
        WriteBinaryReferences(femaleEvent, femaleStream);
        var resolutions = new Dictionary<ImportEntry, ExportEntry> { [maleRef] = maleEvent, [femaleRef] = femaleEvent };
        var key = new StructProperty("WwiseEventTrackKey", new PropertyCollection(), "WwiseEvents");
        var eventProperty = new ObjectProperty(maleRef.UIndex, "Event");

        Assert.AreSame(maleStream, VoTrackAudioNavigation.FindAudioEntry(track, eventProperty, key, false, stream: true,
            resolveImport: import => resolutions.GetValueOrDefault(import)));
        Assert.AreSame(femaleStream, VoTrackAudioNavigation.FindAudioEntry(track, eventProperty, key, true, stream: true,
            resolveImport: import => resolutions.GetValueOrDefault(import)));

        CreateImport(package, "Play_VO_00000042_f", "WwiseEvent");
        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, eventProperty, key, true, stream: true,
            resolveImport: import => resolutions.GetValueOrDefault(import)));
        Assert.AreSame(maleStream, VoTrackAudioNavigation.FindAudioEntry(track, eventProperty, key, false, stream: true,
            resolveImport: import => resolutions.GetValueOrDefault(import)));
    }

    [TestMethod]
    public void ResolvedEventWithAmbiguousReferencedStreamsDoesNotSelectTheFirstImport()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseResolvedAmbiguousStreams.pcc", MEGame.LE3);
        using IMEPackage externalPackage = MEPackageHandler.CreateMemoryEmptyPackage("DialogueAudio.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        ImportEntry eventRef = CreateImport(package, "VO_42_m_Play", "WwiseEvent");
        ExportEntry externalEvent = externalPackage.CreateExport("VO_42_m_Play", "WwiseEvent", indexed: false);
        ImportEntry first = CreateImport(externalPackage, "first_42_m_wav", "WwiseStream");
        ImportEntry second = CreateImport(externalPackage, "second_00000042_m_wav", "WwiseStream");
        WriteBinaryReferences(externalEvent, first, second);
        var resolutions = new Dictionary<ImportEntry, ExportEntry> { [eventRef] = externalEvent };

        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 42, false, stream: true,
            resolveImport: import => resolutions.GetValueOrDefault(import)));
    }

    [TestMethod]
    public void InvalidWwiseEventReferencesDoNotFallBackToOtherDialogueAudio()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseTrackInvalidEvent.pcc", MEGame.LE3);
        ExportEntry track = package.CreateExport("Track", "InterpTrackWwiseEvent", indexed: false);
        var key = new StructProperty("WwiseEventTrackKey", new PropertyCollection(), "WwiseEvents");
        package.CreateExport("VO_42_m_Play", "WwiseEvent", indexed: false);
        package.CreateExport("player_42_m_wav", "WwiseStream", indexed: false);
        ExportEntry nonVo = package.CreateExport("Ambient_Play", "WwiseEvent", indexed: false);
        ExportEntry stop = package.CreateExport("VO_42_m_Stop", "WwiseEvent", indexed: false);
        ImportEntry wrongClass = CreateImport(package, "Play_VO_42_m", "Object");

        foreach (int reference in new[] { 0, int.MaxValue, int.MinValue, nonVo.UIndex, stop.UIndex, wrongClass.UIndex })
        {
            var eventRef = new ObjectProperty(reference, "Event");
            Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, eventRef, key, false, stream: false));
            Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, eventRef, key, false, stream: true));
        }
    }

    [TestMethod]
    [DataRow(MEGame.ME2, false)]
    [DataRow(MEGame.ME3, false)]
    [DataRow(MEGame.LE2, true)]
    [DataRow(MEGame.LE3, true)]
    public void FaceOnlyVoTrackResolvesGenderedEventAndStream(MEGame game, bool female)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("FaceOnlyVoNavigation.pcc", game);
        ExportEntry track = package.CreateExport("Track", "SFXInterpTrackPlayFaceOnlyVO", indexed: false);
        string gender = female ? "f" : "m";
        ExportEntry stream = package.CreateExport($"player_00000042_{gender}_wav", "WwiseStream", indexed: false);
        ExportEntry wwiseEvent = package.CreateExport($"VO_42_{gender}_Play", "WwiseEvent", indexed: false);
        WriteBinaryReferences(wwiseEvent, stream);

        Assert.AreSame(wwiseEvent, VoTrackAudioNavigation.FindAudioEntry(track, 42, female, stream: false));
        Assert.AreSame(stream, VoTrackAudioNavigation.FindAudioEntry(track, 42, female, stream: true));
        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 42, !female, stream: false));
        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 42, !female, stream: true));
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

        Assert.IsTrue(VoTrackAudioNavigation.TryGetPlayEventStringRef(name, out int stringRef));
        Assert.AreEqual(42, stringRef);
        Assert.IsTrue(VoTrackAudioNavigation.TryGetPlayEventInfo(name, out stringRef, out bool parsedFemale));
        Assert.AreEqual(42, stringRef);
        Assert.AreEqual(female, parsedFemale);
        Assert.AreSame(target, VoTrackAudioNavigation.FindAudioEntry(track, 42, female, stream: false));
        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 42, !female, stream: false));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("VO_0_m_Play")]
    [DataRow("VO_-42_m_Play")]
    [DataRow("VO_+42_m_Play")]
    [DataRow("VO_ 42_m_Play")]
    [DataRow("VO_42 _m_Play")]
    [DataRow("VO_2147483648_m_Play")]
    [DataRow("Play_VO_42_male")]
    [DataRow("VO_42_m_Stop")]
    [DataRow("VO_42_m_Play_Extra")]
    public void PlayEventParserRejectsInvalidIdsGenderAndNameShape(string name)
    {
        Assert.IsFalse(VoTrackAudioNavigation.TryGetPlayEventStringRef(name, out _));
        Assert.IsFalse(VoTrackAudioNavigation.TryGetPlayEventInfo(name, out _, out _));
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
        ExportEntry faceOnlyTrack = package.CreateExport("FaceOnlyTrack", "SFXInterpTrackPlayFaceOnlyVO", indexed: false);
        ExportEntry subtitleTrack = package.CreateExport("SubtitleTrack", "BioEvtSysTrackSubtitles", indexed: false);
        ExportEntry wwiseTrack = package.CreateExport("WwiseTrack", "InterpTrackWwiseEvent", indexed: false);
        ImportEntry wwiseEvent = CreateImport(package, "VO_42_m_Play", "WwiseEvent");
        CreateImport(package, "player_00000042_m_wav", "WwiseStream");

        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 42, isFemale: false, stream: false));
        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(track, 42, isFemale: false, stream: true));
        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(faceOnlyTrack, 42, isFemale: false, stream: false));
        Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(faceOnlyTrack, 42, isFemale: false, stream: true));
        foreach (bool stream in new[] { false, true })
        {
            Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(subtitleTrack, new IntProperty(42, "nStrRefID"),
                new StructProperty("BioSubtitleTrackData", new PropertyCollection(), "m_aSubtitleData"), false, stream));
            Assert.IsNull(VoTrackAudioNavigation.FindAudioEntry(wwiseTrack, new ObjectProperty(wwiseEvent.UIndex, "Event"),
                new StructProperty("WwiseEventTrackKey", new PropertyCollection(), "WwiseEvents"), false, stream));
        }
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

    private static ImportEntry CreateImport(IMEPackage package, string name, string className, IEntry parent = null)
    {
        var entry = new ImportEntry(package)
        {
            ObjectName = name, ClassName = className, PackageFile = "WwiseAudio", idxLink = parent?.UIndex ?? 0
        };
        package.AddImport(entry);
        return entry;
    }

    private static void WriteBinaryReferences(ExportEntry wwiseEvent, params IEntry[] streams) =>
        wwiseEvent.WritePropertiesAndBinary(new PropertyCollection(), new WwiseEvent
        {
            Links = [new WwiseEvent.WwiseEventLink { WwiseBanks = [], WwiseStreams = streams.Select(stream => stream.UIndex).ToList() }]
        });
}
