using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Sound.Wwise;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WwiserSound = ME3Tweaks.Wwiser.Model.Hierarchy.Sound;
using WwiserActorMixer = ME3Tweaks.Wwiser.Model.Hierarchy.ActorMixer;
using WwiserStreamType = ME3Tweaks.Wwiser.Model.Hierarchy.Enums.StreamType;

namespace LegendaryExplorer.Tests.Audio;

[TestClass]
public class WwiseHelperTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void ExtractsAndFiltersWwiseStreamTlkMetadata()
    {
        int? tlkId = WwiseHelper.GetTlkIdFromWwiseStreamName("citprs_miranda_talk1_m_00692109_m_wav");

        Assert.AreEqual(692109, tlkId);
        Assert.AreEqual(692109, WwiseHelper.GetTlkIdFromWwiseEventName("VO_692109_m_Play"));
        Assert.AreEqual(12345, WwiseHelper.GetTlkIdFromWwiseEventName("vo_12345_f_play"));
        Assert.IsNull(WwiseHelper.GetTlkIdFromWwiseEventName("Play_Ambience_12345"));
        Assert.IsNull(WwiseHelper.GetTlkIdFromWwiseStreamName("ambience_stream_42"));
        Assert.IsTrue(WwiseHelper.MatchesWwiseStreamTlkFilter(tlkId, "This is the resolved subtitle.", "692109"));
        Assert.IsTrue(WwiseHelper.MatchesWwiseStreamTlkFilter(tlkId, "This is the resolved subtitle.", "00692109"));
        Assert.IsTrue(WwiseHelper.MatchesWwiseStreamTlkFilter(tlkId, "This is the resolved subtitle.", "RESOLVED subtitle"));
        Assert.IsFalse(WwiseHelper.MatchesWwiseStreamTlkFilter(tlkId, "This is the resolved subtitle.", "different line"));
    }

    [TestMethod]
    public void GetsReferencedWwiseStreamsFromGame3Binary()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseEventNavigationTest.pcc", MEGame.LE3);
        ExportEntry firstStream = package.CreateExport("citprs_miranda_00692084_f_wav", "WwiseStream", indexed: false);
        ExportEntry secondStream = package.CreateExport("citprs_miranda_00692109_m_wav", "WwiseStream", indexed: false);
        ExportEntry notAStream = package.CreateExport("Bank", "WwiseBank", indexed: false);
        ExportEntry wwiseEvent = package.CreateExport("VO_692084_f_Play", "WwiseEvent", indexed: false);
        ExportEntry maleWwiseEvent = package.CreateExport("VO_692109_m_Play", "WwiseEvent", indexed: false);
        wwiseEvent.WritePropertiesAndBinary(new PropertyCollection(), new WwiseEvent
        {
            Links =
            [
                new WwiseEvent.WwiseEventLink
                {
                    WwiseStreams = [firstStream.UIndex, firstStream.UIndex, notAStream.UIndex, 999, secondStream.UIndex]
                }
            ]
        });
        maleWwiseEvent.WritePropertiesAndBinary(new PropertyCollection(), new WwiseEvent
        {
            Links =
            [
                new WwiseEvent.WwiseEventLink
                {
                    WwiseStreams = [firstStream.UIndex, secondStream.UIndex]
                }
            ]
        });

        IReadOnlyList<ExportEntry> streams = WwiseHelper.GetReferencedWwiseStreams(wwiseEvent);

        CollectionAssert.AreEqual(new[] { firstStream, secondStream }, streams.ToArray());
        Assert.AreSame(firstStream, WwiseHelper.GetMatchingReferencedWwiseStream(wwiseEvent, streams));
        Assert.AreSame(firstStream, Soundpanel.ResolveAudioExport(wwiseEvent));
        Assert.IsTrue(Soundpanel.CanParseStatic(wwiseEvent));
        CollectionAssert.AreEqual(new[] { wwiseEvent }, WwiseHelper.GetMatchingWwiseEvents(firstStream).ToArray());
        CollectionAssert.AreEqual(new[] { maleWwiseEvent }, WwiseHelper.GetMatchingWwiseEvents(secondStream).ToArray());
    }

    [TestMethod]
    public void GetsReferencedWwiseStreamsFromLe2Properties()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseEventNavigationTest.pcc", MEGame.LE2);
        ExportEntry firstStream = package.CreateExport("FirstStream", "WwiseStream", indexed: false);
        ExportEntry secondStream = package.CreateExport("SecondStream", "WwiseStream", indexed: false);
        ExportEntry wwiseEvent = package.CreateExport("Play", "WwiseEvent", indexed: false);
        ExportEntry singleStreamEvent = package.CreateExport("PlayFirstStream", "WwiseEvent", indexed: false);
        var relationships = new StructProperty("WwiseRelationships", false,
            new ArrayProperty<ObjectProperty>(
                [new ObjectProperty(firstStream), new ObjectProperty(secondStream), new ObjectProperty(firstStream)],
                "Streams"))
        {
            Name = "Relationships"
        };
        var references = new ArrayProperty<StructProperty>("References")
        {
            new("WwisePlatformRelationships", false, relationships)
        };
        wwiseEvent.WritePropertiesAndBinary(new PropertyCollection { references }, new WwiseEvent
        {
            WwiseEventID = 123,
            Links = []
        });
        var singleStreamRelationships = new StructProperty("WwiseRelationships", false,
            new ArrayProperty<ObjectProperty>([new ObjectProperty(firstStream)], "Streams"))
        {
            Name = "Relationships"
        };
        singleStreamEvent.WritePropertiesAndBinary(new PropertyCollection
        {
            new ArrayProperty<StructProperty>("References")
            {
                new("WwisePlatformRelationships", false, singleStreamRelationships)
            }
        }, new WwiseEvent
        {
            WwiseEventID = 456,
            Links = []
        });

        IReadOnlyList<ExportEntry> streams = WwiseHelper.GetReferencedWwiseStreams(wwiseEvent);

        CollectionAssert.AreEqual(new[] { firstStream, secondStream }, streams.ToArray());
        Assert.IsNull(WwiseHelper.GetMatchingReferencedWwiseStream(wwiseEvent, streams));
        Assert.IsNull(Soundpanel.ResolveAudioExport(wwiseEvent));
        Assert.IsFalse(Soundpanel.CanParseStatic(wwiseEvent));
        CollectionAssert.AreEqual(new[] { singleStreamEvent }, WwiseHelper.GetMatchingWwiseEvents(firstStream).ToArray());
    }

    [TestMethod]
    public void ResolvesPlayableSoundFromHircEvent()
    {
        const uint eventId = 0x0AA1A555;
        const uint stopActionId = 0x11111111;
        const uint playActionId = 0x144590D7;
        const uint soundId = 0x0A4B0329;
        const uint audioId = 0x12345678;
        const uint bankId = 0x87654321;
        var hircObjects = new[]
        {
            new HIRCDisplayObject(0, new WwiseBankParsed.Event
            {
                Type = HIRCType.Event,
                ID = eventId,
                EventActions = [stopActionId, playActionId]
            }, MEGame.LE3),
            new HIRCDisplayObject(1, new WwiseBankParsed.EventAction
            {
                Type = HIRCType.EventAction,
                ID = stopActionId,
                ActionType = WwiseBankParsed.EventActionType.Stop_LE,
                ReferencedObjectID = soundId,
                unparsed = []
            }, MEGame.LE3),
            new HIRCDisplayObject(2, new WwiseBankParsed.EventAction
            {
                Type = HIRCType.EventAction,
                ID = playActionId,
                ActionType = WwiseBankParsed.EventActionType.Play_LE,
                ReferencedObjectID = soundId,
                unparsed = []
            }, MEGame.LE3),
            new HIRCDisplayObject(3, new WwiseBankParsed.HIRCObject
            {
                Type = HIRCType.SoundSXFSoundVoice,
                ID = soundId,
                unparsed = []
            }, MEGame.LE3)
        };
        var wwiserSound = new WwiserSound { Id = soundId };
        wwiserSound.BankSourceData.StreamType.Value = WwiserStreamType.StreamTypeInner.Streaming;
        wwiserSound.BankSourceData.MediaInformation.SourceId = audioId;

        Soundpanel.ApplyWwiserSoundMetadata(hircObjects, [wwiserSound], bankId);

        HIRCDisplayObject playableSound = Soundpanel.ResolvePlayableHircSound(hircObjects[0], hircObjects);

        Assert.IsNotNull(playableSound);
        Assert.AreEqual(soundId, playableSound.ID);
        Assert.AreEqual(audioId, playableSound.AudioID);
        Assert.AreEqual((uint)WwiseBankParsed.SoundState.Streamed, playableSound.State);
        Assert.AreEqual(0U, playableSound.SourceID);

        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("HircPlaybackTest.pcc", MEGame.LE3);
        ExportEntry wwiseStream = package.CreateExport("Stream", "WwiseStream", indexed: false);
        wwiseStream.WriteProperty(new IntProperty(unchecked((int)audioId), "Id"));
        Assert.AreSame(wwiseStream, Soundpanel.FindWwiseStreamById(package, audioId));
    }

    [TestMethod]
    public void FiltersHircObjectsByIdentityTypeAndEventPreview()
    {
        var hirc = new HIRCDisplayObject(9, new WwiseBankParsed.Event
        {
            Type = HIRCType.Event,
            ID = 0x18F06680,
            EventActions = []
        }, MEGame.LE3)
        {
            EventPreview = "#2083 VO_17251537_f_Play\nTLK 17251537: Hold up."
        };

        Assert.IsTrue(Soundpanel.MatchesHircFilter(hirc, "18F06680"));
        Assert.IsTrue(Soundpanel.MatchesHircFilter(hirc, "event hold"));
        Assert.IsTrue(Soundpanel.MatchesHircFilter(hirc, "17251537"));
        Assert.IsTrue(Soundpanel.MatchesHircFilter(hirc, "VO_17251537_f"));
        Assert.IsTrue(Soundpanel.MatchesHircFilter(hirc, string.Empty));
        Assert.IsFalse(Soundpanel.MatchesHircFilter(hirc, "streamed"));
    }

    [TestMethod]
    public void PropagatesEventPreviewToActionsAndReferencedHierarchy()
    {
        const uint eventId = 0x11111111;
        const uint secondEventId = 0x22222222;
        const uint actionId = 0x33333333;
        const uint secondActionId = 0x44444444;
        const uint actorMixerId = 0x55555555;
        const uint soundId = 0x66666666;
        var hircObjects = new[]
        {
            CreateEventDisplay(0, eventId, actionId),
            CreateEventDisplay(1, secondEventId, secondActionId),
            CreateActionDisplay(2, actionId, actorMixerId),
            CreateActionDisplay(3, secondActionId, actorMixerId),
            CreateGenericDisplay(4, HIRCType.ActorMixer, actorMixerId),
            CreateGenericDisplay(5, HIRCType.SoundSXFSoundVoice, soundId)
        };
        var actorMixer = new WwiserActorMixer { Id = actorMixerId };
        var sound = new WwiserSound { Id = soundId };
        sound.NodeBaseParameters.DirectParentId = actorMixerId;
        Soundpanel.ApplyWwiserHircMetadata(hircObjects, [actorMixer, sound], 0);

        IReadOnlyDictionary<uint, string> connectedPreviews = Soundpanel.BuildConnectedHircEventPreviews(
            hircObjects, new Dictionary<uint, string>
            {
                [eventId] = "TLK 100: First line.",
                [secondEventId] = "TLK 200: Second line."
            });

        Assert.AreEqual("TLK 100: First line.", connectedPreviews[actionId]);
        Assert.AreEqual("TLK 200: Second line.", connectedPreviews[secondActionId]);
        Assert.AreEqual($"TLK 100: First line.{System.Environment.NewLine}TLK 200: Second line.",
            connectedPreviews[actorMixerId]);
        Assert.AreEqual(connectedPreviews[actorMixerId], connectedPreviews[soundId]);
        Assert.AreEqual(actorMixerId, hircObjects[^1].DirectParentID);
    }

    private static HIRCDisplayObject CreateEventDisplay(int index, uint eventId, uint actionId) =>
        new(index, new WwiseBankParsed.Event
        {
            Type = HIRCType.Event,
            ID = eventId,
            EventActions = [actionId]
        }, MEGame.LE3);

    private static HIRCDisplayObject CreateActionDisplay(int index, uint actionId, uint targetId) =>
        new(index, new WwiseBankParsed.EventAction
        {
            Type = HIRCType.EventAction,
            ID = actionId,
            ActionType = WwiseBankParsed.EventActionType.Play_LE,
            ReferencedObjectID = targetId,
            unparsed = []
        }, MEGame.LE3);

    private static HIRCDisplayObject CreateGenericDisplay(int index, HIRCType type, uint id) =>
        new(index, new WwiseBankParsed.HIRCObject
        {
            Type = type,
            ID = id,
            unparsed = []
        }, MEGame.LE3);
}
