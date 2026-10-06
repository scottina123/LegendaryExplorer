using System;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.PackageEditor.Experiments;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.PackageEditor;

[TestClass]
public class BlankBioConversationTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    [DataRow(MEGame.ME3)]
    public void BlankConversationRoundTripsWithGameSpecificSchemaAndEditableFaceFx(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("BlankConversation.pcc", game);
        var (generated, _) = PackageEditorExperimentsScottina.GenerateBlankBioConversationAssets(
            package, "TestDialogue", "Blank");

        using var bytes = package.SaveToStream(false);
        bytes.Position = 0;
        using var reopened = MEPackageHandler.OpenMEPackageFromStream(bytes, package.FilePath);
        var export = reopened.GetUExport(generated.UIndex);
        var properties = export.GetProperties();
        AssertSchemaProperties(game, "BioConversation", properties);

        if (game.IsGame3())
        {
            Assert.IsEmpty(properties.GetProp<ArrayProperty<NameProperty>>("m_aSpeakerList"));
            Assert.IsNull(properties.GetProp<Property>("m_SpeakerList"));
        }
        else
        {
            Assert.IsEmpty(properties.GetProp<ArrayProperty<StructProperty>>("m_SpeakerList"));
            Assert.IsNull(properties.GetProp<Property>("m_aSpeakerList"));
        }

        string sequenceProperty = game.IsGame1() ? "m_pEvtSystemSeq" : "MatineeSequence";
        string nonSpeakerProperty = game.IsGame1() ? "m_pConvFaceFXSet" : "m_pNonSpeakerFaceFXSet";
        Assert.IsNotNull(properties.GetProp<ObjectProperty>(sequenceProperty));
        Assert.IsNotNull(properties.GetProp<ObjectProperty>(nonSpeakerProperty));
        Assert.IsNull(properties.GetProp<Property>(game.IsGame1() ? "MatineeSequence" : "m_pEvtSystemSeq"));

        var conversation = new ConversationExtended(export);
        conversation.LoadConversation((_, _) => "Blank dialogue", detailedParse: true);

        Assert.IsTrue(conversation.IsParsed);
        Assert.AreEqual("TestDialogue.Blank_dlg", export.InstancedFullPath);
        Assert.HasCount(1, conversation.EntryList);
        Assert.IsEmpty(conversation.ReplyList);
        Assert.AreEqual(0, conversation.StartingList[0]);
        Assert.AreEqual(-1, conversation.EntryList[0].SpeakerIndex);
        Assert.AreEqual("Sequence", conversation.Sequence.ClassName);
        Assert.AreEqual(properties.GetProp<ObjectProperty>(sequenceProperty).Value, conversation.Sequence.UIndex);
        Assert.AreEqual(properties.GetProp<ObjectProperty>(nonSpeakerProperty).Value, conversation.NonSpkrFFX.UIndex);
        Assert.IsNull(conversation.WwiseBank);

        foreach (var node in properties.GetProp<ArrayProperty<StructProperty>>("m_EntryList"))
        {
            AssertSchemaProperties(game, "BioDialogEntryNode", node.Properties);
        }

        Assert.HasCount(2, conversation.Speakers);
        foreach (var speaker in conversation.Speakers)
        {
            string role = speaker.SpeakerID == -2 ? "Player" : "Owner";
            Assert.AreEqual($"FXA_Blank_{role}_M", speaker.FaceFX_Male.ObjectName.Name);
            Assert.AreEqual($"FXA_Blank_{role}_F", speaker.FaceFX_Female.ObjectName.Name);
            Assert.AreEqual(speaker.FaceFX_Male.UIndex, conversation.GetFaceFX(speaker.SpeakerID, true).UIndex);
            Assert.AreEqual(speaker.FaceFX_Female.UIndex, conversation.GetFaceFX(speaker.SpeakerID, false).UIndex);
        }

        var faceFxExports = reopened.Exports.Where(entry => entry.ClassName == "FaceFXAnimSet").ToArray();
        Assert.HasCount(5, faceFxExports);
        foreach (var faceFxExport in faceFxExports)
        {
            var faceFx = ObjectBinary.From<FaceFXAnimSet>(faceFxExport);
            Assert.AreEqual(1731, faceFx.Version);
            Assert.IsEmpty(faceFx.Names);
            Assert.IsEmpty(faceFx.Lines);
        }

        conversation.SerializeNodes();
        var reparsed = new ConversationExtended(export);
        reparsed.LoadConversation((_, _) => "Blank dialogue", detailedParse: true);
        Assert.HasCount(1, reparsed.EntryList);
        Assert.HasCount(2, reparsed.Speakers);
        Assert.AreEqual(conversation.Sequence.UIndex, reparsed.Sequence.UIndex);
    }

    [TestMethod]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    [DataRow(MEGame.ME3)]
    public void LocConversationReferencesAndLevelImportsSurviveRoundTrip(MEGame game)
    {
        using var locPackage = MEPackageHandler.CreateMemoryEmptyPackage("BioD_Test_LOC_INT.pcc", game);
        locPackage.CreateObjectReferencer();
        var (conversation, referencedExports) = PackageEditorExperimentsScottina.GenerateBlankBioConversationAssets(
            locPackage, "TestDialogue", "Blank");
        locPackage.AddObjectsToReferencer(referencedExports);

        using var levelPackage = MEPackageHandler.CreateMemoryEmptyPackage("BioD_Test.pcc", game);
        var topPackageImport = new ImportEntry((ExportEntry)conversation.Parent, 0, levelPackage);
        levelPackage.AddImport(topPackageImport);
        var conversationImport = new ImportEntry(conversation, topPackageImport.UIndex, levelPackage);
        levelPackage.AddImport(conversationImport);

        using var locBytes = locPackage.SaveToStream(false);
        locBytes.Position = 0;
        using var reopenedLoc = MEPackageHandler.OpenMEPackageFromStream(locBytes, locPackage.FilePath);
        var referencer = reopenedLoc.Exports.Single(entry => entry.ClassName == "ObjectReferencer");
        var referencedObjects = referencer.GetProperty<ArrayProperty<ObjectProperty>>("ReferencedObjects");
        CollectionAssert.AreEquivalent(referencedExports.Select(entry => entry.UIndex).ToArray(),
            referencedObjects.Select(property => property.Value).ToArray());

        var parsedConversation = new ConversationExtended(reopenedLoc.GetUExport(conversation.UIndex));
        parsedConversation.LoadConversation((_, _) => "Blank dialogue", detailedParse: true);
        int[] neededExports =
        [
            parsedConversation.UIndex,
            parsedConversation.Sequence.UIndex,
            parsedConversation.NonSpkrFFX.UIndex,
            .. parsedConversation.Speakers.SelectMany(speaker => new[] { speaker.FaceFX_Male.UIndex, speaker.FaceFX_Female.UIndex })
        ];
        CollectionAssert.AreEquivalent(neededExports, referencedObjects.Select(property => property.Value).ToArray(),
            "The LOC referencer must retain the conversation, sequence, and every blank FaceFX asset.");

        using var levelBytes = levelPackage.SaveToStream(false);
        levelBytes.Position = 0;
        using var reopenedLevel = MEPackageHandler.OpenMEPackageFromStream(levelBytes, levelPackage.FilePath);
        var importedConversation = reopenedLevel.GetEntry(conversationImport.UIndex);
        Assert.IsInstanceOfType<ImportEntry>(importedConversation);
        Assert.AreEqual("BioConversation", importedConversation.ClassName);
        Assert.AreEqual(conversation.InstancedFullPath, importedConversation.InstancedFullPath);
        Assert.AreEqual("Package", importedConversation.Parent.ClassName);
        Assert.AreEqual(topPackageImport.UIndex, importedConversation.Parent.UIndex);
    }

    [TestMethod]
    [DataRow(MEGame.ME1)]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.UDK)]
    public void UnsupportedGamesAreRejectedWithoutModifyingPackage(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("UnsupportedConversation.pcc", game);
        int exportCount = package.ExportCount;
        int importCount = package.ImportCount;
        var names = package.Names.ToArray();
        bool wasModified = package.IsModified;

        Assert.ThrowsExactly<ArgumentException>(() =>
            PackageEditorExperimentsScottina.GenerateBlankBioConversationAssets(package, "TestDialogue", "Blank"));

        Assert.AreEqual(exportCount, package.ExportCount);
        Assert.AreEqual(importCount, package.ImportCount);
        CollectionAssert.AreEqual(names, package.Names.ToArray());
        Assert.AreEqual(wasModified, package.IsModified);
    }

    private static void AssertSchemaProperties(MEGame game, string containerType, PropertyCollection properties)
    {
        foreach (var property in properties.Where(property => property is not NoneProperty))
        {
            Assert.IsNotNull(GlobalUnrealObjectInfo.GetPropertyInfo(game, property.Name, containerType),
                $"{property.Name} is not a valid {containerType} property in {game}.");
        }
    }
}
