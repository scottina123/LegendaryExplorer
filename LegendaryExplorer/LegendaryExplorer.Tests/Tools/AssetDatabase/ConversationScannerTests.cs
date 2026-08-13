using System.IO;
using BinaryPack;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.Tools.AssetDatabase.Scanners;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.AssetDatabase;

[TestClass]
public class ConversationScannerTests
{
    [TestMethod]
    public void ExtractsSparseStageDirectionsInSourceOrder()
    {
        var stageDirections = new ArrayProperty<StructProperty>("m_aStageDirections")
        {
            CreateStageDirection(123, "Looks toward the Normandy."),
            CreateStageDirection(789, "Pauses, then nods.")
        };
        var properties = new PropertyCollection { stageDirections };

        var result = ConversationScanner.GetStageDirections(properties);

        Assert.HasCount(2, result);
        Assert.AreEqual(123, result[0].StrRef);
        Assert.AreEqual("Looks toward the Normandy.", result[0].Text);
        Assert.AreEqual(789, result[1].StrRef);
        Assert.AreEqual("Pauses, then nods.", result[1].Text);
    }

    [TestMethod]
    public void MissingStageDirectionsProduceAnEmptyCollection()
    {
        var result = ConversationScanner.GetStageDirections(new PropertyCollection());

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void StageDirectionsRoundTripWithTheAssetDatabase()
    {
        var source = new AssetDB();
        source.Conversations.Add(new Conversation(
            "test_conversation",
            false,
            new FileKeyExportPair(4, 27),
            stageDirections:
            [
                new ConversationStageDirection(123, "Looks toward the Normandy."),
                new ConversationStageDirection(789, "Pauses, then nods.")
            ]));

        using var stream = new MemoryStream();
        BinaryConverter.Serialize(source, stream);
        var roundTripped = BinaryConverter.Deserialize<AssetDB>(stream.ToArray());

        Assert.HasCount(1, roundTripped.Conversations);
        Assert.HasCount(2, roundTripped.Conversations[0].StageDirections);
        Assert.AreEqual(123, roundTripped.Conversations[0].StageDirections[0].StrRef);
        Assert.AreEqual("Looks toward the Normandy.", roundTripped.Conversations[0].StageDirections[0].Text);
        Assert.AreEqual(789, roundTripped.Conversations[0].StageDirections[1].StrRef);
        Assert.AreEqual("Pauses, then nods.", roundTripped.Conversations[0].StageDirections[1].Text);
    }

    private static StructProperty CreateStageDirection(int strRef, string text)
    {
        return new StructProperty("BioStageDirection", new PropertyCollection
        {
            new StringRefProperty(strRef, "srStrRef"),
            new StrProperty(text, "sText")
        });
    }
}
