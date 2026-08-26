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
}
