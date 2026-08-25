using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    public void GetsReferencedWwiseStreamsFromGame3Binary()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseEventNavigationTest.pcc", MEGame.LE3);
        ExportEntry firstStream = package.CreateExport("citprs_miranda_00692084_f_wav", "WwiseStream", indexed: false);
        ExportEntry secondStream = package.CreateExport("citprs_miranda_00692109_m_wav", "WwiseStream", indexed: false);
        ExportEntry notAStream = package.CreateExport("Bank", "WwiseBank", indexed: false);
        ExportEntry wwiseEvent = package.CreateExport("VO_692084_f_Play", "WwiseEvent", indexed: false);
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

        IReadOnlyList<ExportEntry> streams = WwiseHelper.GetReferencedWwiseStreams(wwiseEvent);

        CollectionAssert.AreEqual(new[] { firstStream, secondStream }, streams.ToArray());
        Assert.AreSame(firstStream, WwiseHelper.GetMatchingReferencedWwiseStream(wwiseEvent, streams));
    }

    [TestMethod]
    public void GetsReferencedWwiseStreamsFromLe2Properties()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseEventNavigationTest.pcc", MEGame.LE2);
        ExportEntry firstStream = package.CreateExport("FirstStream", "WwiseStream", indexed: false);
        ExportEntry secondStream = package.CreateExport("SecondStream", "WwiseStream", indexed: false);
        ExportEntry wwiseEvent = package.CreateExport("Play", "WwiseEvent", indexed: false);
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

        IReadOnlyList<ExportEntry> streams = WwiseHelper.GetReferencedWwiseStreams(wwiseEvent);

        CollectionAssert.AreEqual(new[] { firstStream, secondStream }, streams.ToArray());
        Assert.IsNull(WwiseHelper.GetMatchingReferencedWwiseStream(wwiseEvent, streams));
    }
}
