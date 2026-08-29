using System;
using System.IO;
using System.Linq;
using LegendaryExplorer.Tools.FaceFXEditor.ElevenLabs.Rvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.FaceFXEditor;

[TestClass]
public class RvcInstallationTests
{
    private string _root;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "LEX-RVC-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "runtime"));
        Directory.CreateDirectory(Path.Combine(_root, "infer", "modules", "vc"));
        Directory.CreateDirectory(Path.Combine(_root, "assets", "weights"));
        Directory.CreateDirectory(Path.Combine(_root, "assets", "indices"));
        Directory.CreateDirectory(Path.Combine(_root, "assets", "rmvpe"));
        Directory.CreateDirectory(Path.Combine(_root, "logs", "voice-b"));
        File.WriteAllText(Path.Combine(_root, "runtime", "python.exe"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "infer", "modules", "vc", "modules.py"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "assets", "rmvpe", "rmvpe.pt"), string.Empty);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void DiscoversVoicesOnlyFromPredeterminedWeightsFolder()
    {
        File.WriteAllText(Path.Combine(_root, "assets", "weights", "voice-b.pth"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "assets", "weights", "voice-a.pth"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "voice-outside.pth"), string.Empty);

        Assert.IsTrue(RvcInstallation.IsCompatibleRoot(_root, out string problem), problem);
        var models = RvcInstallation.DiscoverVoiceModels(_root);

        CollectionAssert.AreEqual(new[] { "voice-a", "voice-b" },
            models.Select(model => model.DisplayName).ToArray());
    }

    [TestMethod]
    public void DiscoversStandardIndexesAndAutomaticallyMatchesVoiceName()
    {
        string modelPath = Path.Combine(_root, "assets", "weights", "voice-b.pth");
        string matchingIndex = Path.Combine(_root, "logs", "voice-b", "added_IVF_voice-b.index");
        File.WriteAllText(modelPath, string.Empty);
        File.WriteAllText(matchingIndex, string.Empty);
        File.WriteAllText(Path.Combine(_root, "assets", "indices", "other.index"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "outside.index"), string.Empty);

        var indexes = RvcInstallation.DiscoverIndexes(_root);
        string resolved = RvcInstallation.ResolveIndexPath(
            indexes.Single(index => index.Kind == RvcIndexSelectionKind.Automatic),
            new RvcVoiceModel(modelPath), indexes);

        Assert.AreEqual(Path.GetFullPath(matchingIndex), resolved);
        Assert.AreEqual(4, indexes.Count); // automatic, disabled, and two standard-location files
    }

    [TestMethod]
    public void DisabledIndexAlwaysResolvesToNoIndex()
    {
        string modelPath = Path.Combine(_root, "assets", "weights", "voice.pth");
        File.WriteAllText(modelPath, string.Empty);
        var disabled = new RvcIndexChoice(RvcIndexSelectionKind.Disabled);

        Assert.IsNull(RvcInstallation.ResolveIndexPath(disabled, new RvcVoiceModel(modelPath), []));
    }
}
