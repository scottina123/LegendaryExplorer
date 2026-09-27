using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.Soundplorer;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Gammtek.IO;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HuffmanCompression = LegendaryExplorerCore.TLK.ME2ME3.HuffmanCompression;

namespace LegendaryExplorer.Tests.Tools.Soundplorer;

[TestClass]
public class AFCTlkResolverTests
{
    private string testDirectory;

    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestInitialize]
    public void SetUp()
    {
        testDirectory = Path.Combine(Path.GetTempPath(), "SoundplorerTlkTests-" + Guid.NewGuid());
        Directory.CreateDirectory(testDirectory);
    }

    [TestCleanup]
    public void CleanUp() => Directory.Delete(testDirectory, true);

    [TestMethod]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void ResolvesOnlyMatchingExternalAudioAndUsesPackageGame(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(testDirectory, "Dialogue.pcc"), game);
        AddStream(package, "VO_692097_f", "speech", 100, 80);
        AddStream(package, "VO_692098_m", "other", 200, 80);
        AddStream(package, "VO_692099_m", "speech", 300, 81);
        AddStream(package, "VO_692100_m", null, 400, 80);
        AddStream(package, "Ambient_692101", "speech", 500, 80);
        package.Save();
        AFCFileEntry[] entries = [Entry("SPEECH.afc", 100), Entry("SPEECH.afc", 200), Entry("SPEECH.afc", 300), Entry("SPEECH.afc", 400), Entry("SPEECH.afc", 500)];
        int lookups = 0;

        AFCTlkResolver.Resolve(entries, "INT", resolveStringRef: (id, actualGame) =>
        {
            lookups++;
            Assert.AreEqual(game, actualGame);
            Assert.AreEqual(692097, id);
            return "\"I should go.\"";
        });

        Assert.AreEqual(1, lookups);
        Assert.AreEqual("I should go.", entries[0].TLKString);
        Assert.AreEqual(692097, entries[0].TLKStringRef);
        for (int i = 1; i < entries.Length; i++)
        {
            Assert.IsNull(entries[i].TLKString);
            Assert.IsNull(entries[i].TLKStringRef);
        }
    }

    [TestMethod]
    public void FindsParentModTlkInSelectedLanguageAndSurvivesUnreadableFiles()
    {
        string cooked = Path.Combine(testDirectory, "CookedPCConsole");
        string audio = Path.Combine(cooked, "1_CitHub");
        Directory.CreateDirectory(audio);
        using var package = MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(audio, "Dialogue.pcc"), MEGame.LE3);
        AddStream(package, "cit_james_00692097_m_wav", "speech", 100, 80);
        package.Save();
        File.WriteAllText(Path.Combine(audio, "Broken.pcc"), "Not a package");
        File.WriteAllText(Path.Combine(cooked, "Broken_INT.tlk"), "Not a TLK");
        HuffmanCompression.SaveToTlkFile(Path.Combine(cooked, "Mod_INT.tlk"), [new TLKStringRef(692097, "Mod dialogue.")]);
        HuffmanCompression.SaveToTlkFile(Path.Combine(cooked, "Mod_FRA.tlk"), [new TLKStringRef(692097, "French dialogue.")]);
        var entry = new AFCFileEntry(Path.Combine(audio, "speech.afc"), 100, 80, 0, Endian.Little);
        bool notified = false;
        entry.PropertyChanged += (_, e) => notified |= e.PropertyName == nameof(AFCFileEntry.TLKString);

        AFCTlkResolver.Resolve([entry], "INT", resolveStringRef: (_, _) => "Global dialogue.");

        Assert.AreEqual("Mod dialogue.", entry.TLKString);
        Assert.IsTrue(entry.MatchesTLKSearch("  MOD dialogue  "));
        Assert.IsTrue(entry.MatchesTLKSearch("692097"));
        Assert.IsTrue(notified);
    }

    [TestMethod]
    public void DoesNotUseReferencesToAnotherAfcWithSameFilename()
    {
        string cooked = Path.Combine(testDirectory, "CookedPCConsole");
        string audio = Path.Combine(cooked, "Audio");
        string other = Path.Combine(cooked, "Other");
        Directory.CreateDirectory(audio);
        Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(other, "speech.afc"), []);
        using var package = MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(other, "Dialogue.pcc"), MEGame.LE3);
        AddStream(package, "VO_692097_m", "speech", 100, 80);
        package.Save();
        var entry = new AFCFileEntry(Path.Combine(audio, "speech.afc"), 100, 80, 0, Endian.Little);

        AFCTlkResolver.Resolve([entry], "INT", resolveStringRef: (_, _) => "Wrong audio.");

        Assert.IsNull(entry.TLKString);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("No Data")]
    [DataRow("\"\"")]
    public void MissingTextLeavesSubtitleHidden(string missingText)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(testDirectory, "Dialogue.pcc"), MEGame.LE3);
        AddStream(package, "VO_692097_m", "speech", 100, 80);
        package.Save();
        var entry = Entry("speech.afc", 100);

        AFCTlkResolver.Resolve([entry], "INT", resolveStringRef: (_, _) => missingText);

        Assert.IsNull(entry.TLKString);
        Assert.AreEqual(692097, entry.TLKStringRef);
        Assert.IsTrue(entry.MatchesTLKSearch("692097"));
        Assert.IsFalse(entry.MatchesTLKSearch("No Data"));
        Assert.AreEqual("AFC Entry @ 0x000064", entry.DisplayString);
    }

    [TestMethod]
    public void SearchesResolvedAfcEntriesByExactIdOrPartialText()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(testDirectory, "Dialogue.pcc"), MEGame.LE3);
        AddStream(package, "VO_692097_m", "speech", 100, 80);
        AddStream(package, "VO_692098_f", "speech", 200, 80);
        package.Save();
        AFCFileEntry[] entries = [Entry("speech.afc", 100), Entry("speech.afc", 200), Entry("speech.afc", 300)];
        AFCTlkResolver.Resolve(entries, "INT", resolveStringRef: (id, _) => id == 692097 ? "I should go." : "We'll cover your flank.");

        Assert.AreSame(entries[0], entries.FirstOrDefault(entry => entry.MatchesTLKSearch(" 00692097 ")));
        Assert.AreSame(entries[1], entries.FirstOrDefault(entry => entry.MatchesTLKSearch("692098")));
        Assert.AreSame(entries[1], entries.FirstOrDefault(entry => entry.MatchesTLKSearch("  COVER YOUR FLANK  ")));
        foreach (string query in new[] { null, "", "   ", "69209", "0", "-692097", "100", "unrelated dialogue" })
            Assert.IsFalse(entries.Any(entry => entry.MatchesTLKSearch(query)), query);
    }

    [TestMethod]
    public void CancellationStopsBeforeOpeningPackages()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(testDirectory, "Dialogue.pcc"), MEGame.LE3);
        AddStream(package, "VO_692097_m", "speech", 100, 80);
        package.Save();
        var entry = Entry("speech.afc", 100);
        int lookups = 0;

        AFCTlkResolver.Resolve([entry], "INT", () => true, resolveStringRef: (_, _) => { lookups++; return "Dialogue."; });

        Assert.AreEqual(0, lookups);
        Assert.IsNull(entry.TLKString);
    }

    private AFCFileEntry Entry(string name, int offset) => new(Path.Combine(testDirectory, name), offset, 80, 0, Endian.Little);

    private static void AddStream(IMEPackage package, string name, string afc, int offset, int size)
    {
        var export = package.CreateExport(name, "WwiseStream", indexed: false);
        var properties = new PropertyCollection { new IntProperty(1, "Id") };
        if (afc != null)
            properties.Add(new NameProperty(afc, "Filename"));
        export.WritePropertiesAndBinary(properties, new WwiseStream
        {
            Unk1 = 1,
            Filename = afc,
            DataOffset = offset,
            DataSize = size,
            EmbeddedData = afc == null ? new byte[size] : null
        });
    }
}
