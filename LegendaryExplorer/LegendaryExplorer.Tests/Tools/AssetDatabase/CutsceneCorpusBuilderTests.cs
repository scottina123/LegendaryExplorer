using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.AssetDatabase.CutsceneCorpus;
using LegendaryExplorerCore;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.AssetDatabase;

/// <summary>
/// Runs the cutscene corpus scan on a few real LE3 Normandy packages. Skipped when LE3 is not installed.
/// Set CUTSCENE_CORPUS_TEST_OUT to keep the output for inspection.
/// </summary>
[TestClass]
public class CutsceneCorpusBuilderTests
{
    private static readonly string[] SamplePackages =
    [
        "BioD_Nor_110Tour.pcc",
        "BioD_Nor_110Tour_LOC_INT.pcc",
        "BioD_Nor_100CabinConv.pcc",
        "BioD_Nor_100CabinConv_LOC_INT.pcc"
    ];

    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public async Task BuildsCorpusFromNormandySample()
    {
        string cooked = Environment.GetEnvironmentVariable("LE3_COOKED_DIR")
                        ?? (MEDirectories.GetDefaultGamePath(MEGame.LE3) is { } root
                            ? Path.Combine(root, "BioGame", "CookedPCConsole")
                            : null);
        string[] samplePackages = Environment.GetEnvironmentVariable("CUTSCENE_CORPUS_FILES")?.Split(';') ?? SamplePackages;
        if (cooked is null || !samplePackages.All(file => File.Exists(Path.Combine(cooked, file))))
        {
            Assert.Inconclusive("LE3 sample packages are not installed.");
        }

        string output = Environment.GetEnvironmentVariable("CUTSCENE_CORPUS_TEST_OUT")
                        ?? Path.Combine(Path.GetTempPath(), "CutsceneCorpusTest_" + Guid.NewGuid().ToString("N"));
        var options = new CutsceneCorpusOptions
        {
            Game = MEGame.LE3,
            OutputDirectory = output,
            UseCache = false,
            FileOverride = samplePackages.Select(file => Path.Combine(cooked, file)).ToList()
        };

        CorpusManifest manifest = await CutsceneCorpusBuilder.BuildAsync(options, null, CancellationToken.None);

        Assert.IsTrue(manifest.Counts["conversations"] > 0, "no conversations");
        Assert.IsTrue(manifest.Counts["nodes"] > 0, "no nodes");
        Assert.IsTrue(manifest.Counts["stages"] > 0, "no stages");
        Assert.IsTrue(manifest.Counts["conversationStages"] > 0, "no StartConversation stage links");
        Assert.IsTrue(manifest.Counts["shots"] > 0, "no shots");
        Assert.IsTrue(manifest.Counts["shotsWithSubject"] > 0, "no shot could be measured against an actor");

        string firstNode = File.ReadLines(Path.Combine(output, "nodes.jsonl")).First();
        using JsonDocument node = JsonDocument.Parse(firstNode);
        Assert.IsTrue(node.RootElement.TryGetProperty("conversationId", out _));
        Assert.IsTrue(Directory.EnumerateFiles(Path.Combine(output, "scripts")).Any());

        if (Environment.GetEnvironmentVariable("CUTSCENE_CORPUS_TEST_OUT") is null)
        {
            Directory.Delete(output, true);
        }
    }

    /// <summary>
    /// Full-game run, same code path as the Asset Database menu. Only runs when CUTSCENE_CORPUS_FULL_OUT is set;
    /// LE_DIR may point at the Legendary Edition install if Legendary Explorer settings are not available.
    /// </summary>
    [TestMethod]
    public async Task BuildsFullCorpusWhenRequested()
    {
        string output = Environment.GetEnvironmentVariable("CUTSCENE_CORPUS_FULL_OUT");
        if (output is null)
        {
            Assert.Inconclusive("Set CUTSCENE_CORPUS_FULL_OUT to run the full LE3 corpus build.");
        }
        if (Environment.GetEnvironmentVariable("LE_DIR") is { } leDirectory)
        {
            LegendaryExplorerCoreLibSettings.Instance.LEDirectory = leDirectory;
        }

        var options = new CutsceneCorpusOptions { Game = MEGame.LE3, OutputDirectory = output };
        var progress = new Progress<string>(message => Console.WriteLine(message));
        CorpusManifest manifest = await CutsceneCorpusBuilder.BuildAsync(options, progress, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(manifest.Counts));
        Assert.IsTrue(manifest.Counts["conversations"] > 1000, "expected the whole game's conversations");
    }
}
