using System;
using System.IO;
using System.Linq;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class ConversationImportedAudioTests
{
    [TestMethod]
    [DataRow(MEGame.LE3)]
    [DataRow(MEGame.ME3)]
    public void ImportedEventResolvesBankInTheEventsPackage(MEGame game)
    {
        GlobalTest.Init();
        string directory = Path.Combine(Path.GetTempPath(), "LEXConversationAudio_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var source = MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(directory, "AudioSource.pcc"), game);
            var voice = source.CreatePackageExport("Voice");
            var bank = source.CreateExport("Bank", "WwiseBank", voice, indexed: false);
            bank.WriteBinary(WwiseBank.Create());
            var audioEvent = source.CreateExport("Event", "WwiseEvent", voice, indexed: false);
            audioEvent.WriteProperty(new StructProperty("WwiseRelationships", new PropertyCollection
            {
                new ObjectProperty(bank.UIndex, "Bank")
            }, "Relationships"));
            audioEvent.WriteBinary(WwiseEvent.Create());
            source.Save();

            using var consumer = MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(directory, "PrivateDialogue.pcc"), game);
            consumer.LECLTagData.ImportHintFiles.Add("AudioSource.pcc");
            var voiceImport = new ImportEntry(voice, 0, consumer);
            consumer.AddImport(voiceImport);
            var eventImport = new ImportEntry(audioEvent, voiceImport.UIndex, consumer);
            consumer.AddImport(eventImport);
            var conversation = consumer.CreateExport("PrivateDialogue", "BioConversation", indexed: false);
            conversation.WriteProperty(new ArrayProperty<ObjectProperty>(new[] { new ObjectProperty(eventImport.UIndex) }, "m_aMaleSoundObjects"));
            // This export occupies the bank's numeric index in the wrong package.
            consumer.CreateExport("IndexTrap", "Object", indexed: false);

            var parsed = new ConversationExtended(conversation);
            parsed.ParseWwiseBank();

            Assert.IsNotNull(parsed.WwiseBank);
            Assert.AreEqual("Voice.Bank", parsed.WwiseBank.InstancedFullPath);
            Assert.AreEqual(source.FilePath, parsed.WwiseBank.FileRef.FilePath);
            Assert.AreNotSame(consumer, parsed.WwiseBank.FileRef);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void UnresolvedImportedEventDoesNotIndexTheExportTable()
    {
        GlobalTest.Init();
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("UnresolvedAudio.pcc", MEGame.LE3);
        var missingEvent = package.CreateImport("WwiseEvent", "MissingAudioEvent");
        var conversation = package.CreateExport("Dialogue", "BioConversation", indexed: false);
        conversation.WriteProperty(new ArrayProperty<ObjectProperty>(new[] { new ObjectProperty(missingEvent.UIndex) }, "m_aMaleSoundObjects"));

        var parsed = new ConversationExtended(conversation);
        parsed.ParseWwiseBank();

        Assert.IsNull(parsed.WwiseBank);
    }

    [TestMethod]
    public void FaceFxWithImportedEventResolvesExternalBank()
    {
        GlobalTest.Init();
        string directory = Path.Combine(Path.GetTempPath(), "LEXFaceFxAudio_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var source = MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(directory, "AudioSource.pcc"), MEGame.LE3);
            var voice = source.CreatePackageExport("Voice");
            var bank = source.CreateExport("Bank", "WwiseBank", voice, indexed: false);
            bank.WriteBinary(WwiseBank.Create());
            var audioEvent = source.CreateExport("Event", "WwiseEvent", voice, indexed: false);
            audioEvent.WriteProperty(new StructProperty("WwiseRelationships", new PropertyCollection { new ObjectProperty(bank.UIndex, "Bank") }, "Relationships"));
            audioEvent.WriteBinary(WwiseEvent.Create());
            var face = source.CreateExport("Face", "FaceFXAnimSet", voice, indexed: false);
            face.WriteProperty(new ArrayProperty<ObjectProperty>(new[] { new ObjectProperty(audioEvent.UIndex) }, "ReferencedSoundCues"));
            source.Save();

            using var consumer = MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(directory, "PrivateDialogue.pcc"), MEGame.LE3);
            consumer.LECLTagData.ImportHintFiles.Add("AudioSource.pcc");
            var voiceImport = new ImportEntry(voice, 0, consumer);
            consumer.AddImport(voiceImport);
            var faceImport = new ImportEntry(face, voiceImport.UIndex, consumer);
            consumer.AddImport(faceImport);
            var conversation = consumer.CreateExport("Dialogue", "BioConversation", indexed: false);
            conversation.WriteProperty(new ArrayProperty<ObjectProperty>(new[] { new ObjectProperty(0), new ObjectProperty(faceImport.UIndex) }, "m_aMaleFaceSets"));

            var parsed = new ConversationExtended(conversation);
            parsed.ParseWwiseBank();

            Assert.IsNotNull(parsed.WwiseBank);
            Assert.AreEqual("Voice.Bank", parsed.WwiseBank.InstancedFullPath);
            Assert.AreEqual(source.FilePath, parsed.WwiseBank.FileRef.FilePath);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
