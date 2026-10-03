using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class LevelPresetTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public async Task FileValidationRejectsEmptyListsAndReportsEveryMissingFile()
    {
        using var files = new TestFiles();
        await Assert.ThrowsAsync<InvalidDataException>(() => LevelPresetsDialog.ValidateFilesAsync([]));
        string firstMissing = files.Level("FirstMissing.pcc"), secondMissing = files.Level("SecondMissing.pcc");

        var error = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            LevelPresetsDialog.ValidateFilesAsync([firstMissing, secondMissing]));

        StringAssert.Contains(error.Message, firstMissing);
        StringAssert.Contains(error.Message, secondMissing);
    }

    [TestMethod]
    public async Task FileValidationChecksLaterFilesAndRejectsPackagesWithoutLevels()
    {
        using var files = new TestFiles();
        string level = CreateValidationPackage(files.Level("ValidLevel.pcc"), MEGame.ME3);
        string nonLevel = CreateValidationPackage(files.Level("NoLevel.pcc"), MEGame.ME3, containsLevel: false);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            LevelPresetsDialog.ValidateFilesAsync([level, nonLevel]));

        StringAssert.Contains(error.Message, "NoLevel.pcc");
        StringAssert.Contains(error.Message, "does not contain a level");
    }

    [TestMethod]
    public async Task FileValidationRejectsMixedGamesAndAnUnexpectedGame()
    {
        using var files = new TestFiles();
        string me3Level = CreateValidationPackage(files.Level("ME3Level.pcc"), MEGame.ME3);
        string me2Level = CreateValidationPackage(files.Level("ME2Level.pcc"), MEGame.ME2);

        var mixedError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            LevelPresetsDialog.ValidateFilesAsync([me3Level, me2Level]));
        StringAssert.Contains(mixedError.Message, "ME2Level.pcc");
        StringAssert.Contains(mixedError.Message, "ME2");
        StringAssert.Contains(mixedError.Message, "ME3");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LevelPresetsDialog.ValidateFilesAsync([me2Level], MEGame.ME3));
    }

    [TestMethod]
    public async Task FileValidationAcceptsEveryLevelFromTheSameGameAndReturnsItsGame()
    {
        using var files = new TestFiles();
        string[] levels =
        [
            CreateValidationPackage(files.Level("FirstLevel.pcc"), MEGame.ME3),
            CreateValidationPackage(files.Level("SecondLevel.pcc"), MEGame.ME3),
            CreateValidationPackage(files.Level("ThirdLevel.pcc"), MEGame.ME3)
        ];

        Assert.AreEqual(MEGame.ME3, await LevelPresetsDialog.ValidateFilesAsync(levels));
        Assert.AreEqual(MEGame.ME3, await LevelPresetsDialog.ValidateFilesAsync(levels, MEGame.ME3));
    }

    [TestMethod]
    public void AvailableFilesKeepPresetOrderAndRetainMissingFilesInTheSavedPreset()
    {
        using var files = new TestFiles();
        string first = files.Level("First.pcc"), missing = files.Level("Missing.pcc"), second = files.Level("Second.pcc");
        File.WriteAllText(first, "");
        File.WriteAllText(second, "");
        var store = new LevelPresetStore(files.StorePath);
        LevelPreset preset = store.Save(new LevelPreset
        {
            Name = "Partly available", Game = MEGame.ME3,
            FilePaths = [missing, second, first], ReadOnlyFilePaths = [missing, first]
        });
        byte[] savedBytes = File.ReadAllBytes(files.StorePath);

        CollectionAssert.AreEqual(new[] { second, first }, LevelPresetsDialog.GetAvailableFiles(preset).ToArray());

        CollectionAssert.AreEqual(new[] { missing, second, first }, preset.FilePaths.ToArray());
        CollectionAssert.AreEqual(new[] { missing, first }, preset.ReadOnlyFilePaths.ToArray());
        CollectionAssert.AreEqual(savedBytes, File.ReadAllBytes(files.StorePath));
        CollectionAssert.AreEqual(preset.FilePaths.ToArray(), new LevelPresetStore(files.StorePath).Presets.Single().FilePaths.ToArray());
        File.Delete(first);
        File.Delete(second);
        Assert.IsEmpty(LevelPresetsDialog.GetAvailableFiles(preset));
        Assert.HasCount(3, preset.FilePaths);
    }

    [TestMethod]
    public void SavingSeveralLevelsPreservesTheirOrderAndGameAfterReload()
    {
        using var files = new TestFiles();
        var store = new LevelPresetStore(files.StorePath);
        string[] levels = [files.Level("BioD_Hub.pcc"), files.Level("BioA_Hub.pcc"), files.Level("BioD_Props.pcc")];

        LevelPreset saved = store.Save(new LevelPreset
        {
            Name = "  Citadel hub  ", Game = MEGame.LE3, FilePaths = [.. levels], ReadOnlyFilePaths = [levels[2]]
        });

        Assert.AreEqual("Citadel hub", saved.Name);
        Assert.AreNotEqual(Guid.Empty, saved.Id);
        CollectionAssert.AreEqual(levels, saved.FilePaths.ToArray());
        var reloaded = new LevelPresetStore(files.StorePath);
        LevelPreset restored = reloaded.Presets.Single();
        Assert.AreEqual(saved.Id, restored.Id);
        Assert.AreEqual(saved.Name, restored.Name);
        Assert.AreEqual(MEGame.LE3, restored.Game);
        CollectionAssert.AreEqual(levels, restored.FilePaths.ToArray());
        CollectionAssert.AreEqual(new[] { levels[2] }, restored.ReadOnlyFilePaths.ToArray());
        Assert.IsTrue(string.IsNullOrEmpty(reloaded.LoadError));
    }

    [TestMethod]
    public void EditingPresetRenamesAndAddsOrRemovesLevelsWithoutCreatingAnotherPreset()
    {
        using var files = new TestFiles();
        var store = new LevelPresetStore(files.StorePath);
        string first = files.Level("First.pcc"), removed = files.Level("Removed.pcc"), added = files.Level("Added.pcc");
        LevelPreset original = store.Save(new LevelPreset
        {
            Name = "Original", Game = MEGame.LE2, FilePaths = [first, removed], ReadOnlyFilePaths = [removed]
        });

        LevelPreset edited = store.Save(original with
        {
            Name = "  Renamed  ", FilePaths = [added, first], ReadOnlyFilePaths = [first]
        });

        Assert.HasCount(1, store.Presets);
        Assert.AreEqual(original.Id, edited.Id);
        Assert.AreEqual("Renamed", edited.Name);
        LevelPreset reloaded = new LevelPresetStore(files.StorePath).Presets.Single();
        CollectionAssert.AreEqual(new[] { added, first }, reloaded.FilePaths.ToArray());
        CollectionAssert.AreEqual(new[] { first }, reloaded.ReadOnlyFilePaths.ToArray());
        Assert.AreEqual("Renamed", reloaded.Name);
    }

    [TestMethod]
    public void SavingNormalizesPathsAndDeduplicatesReadOnlyFilesWithinThePreset()
    {
        using var files = new TestFiles();
        var store = new LevelPresetStore(files.StorePath);
        string first = files.Level("First.pcc"), second = files.Level("Second.pcc");
        string aliasedFirst = Path.Combine(files.DirectoryPath, "UnusedFolder", "..", "First.pcc");

        LevelPreset saved = store.Save(new LevelPreset
        {
            Name = "Paths", Game = MEGame.LE1,
            FilePaths = [aliasedFirst, first.ToUpperInvariant(), second, second.ToUpperInvariant()],
            ReadOnlyFilePaths = [second.ToUpperInvariant(), second, files.Level("NotInPreset.pcc")]
        });

        CollectionAssert.AreEqual(new[] { first, second }, saved.FilePaths.ToArray());
        CollectionAssert.AreEqual(new[] { second }, saved.ReadOnlyFilePaths.ToArray());
        CollectionAssert.AreEqual(saved.FilePaths.ToArray(), new LevelPresetStore(files.StorePath).Presets.Single().FilePaths.ToArray());
    }

    [TestMethod]
    public void MissingLevelsRemainInThePresetUntilExplicitlyRemoved()
    {
        using var files = new TestFiles();
        var store = new LevelPresetStore(files.StorePath);
        string existing = files.Level("Existing.pcc"), missing = files.Level("Missing.pcc");
        File.WriteAllText(existing, "");
        store.Save(new LevelPreset
        {
            Name = "Moved installation", Game = MEGame.LE3, FilePaths = [existing, missing], ReadOnlyFilePaths = [missing]
        });
        File.Delete(existing);

        LevelPreset restored = new LevelPresetStore(files.StorePath).Presets.Single();

        CollectionAssert.AreEqual(new[] { existing, missing }, restored.FilePaths.ToArray());
        CollectionAssert.AreEqual(new[] { missing }, restored.ReadOnlyFilePaths.ToArray());
    }

    [TestMethod]
    public void SearchMatchesPresetNameGameAndFullLevelPathIgnoringCase()
    {
        using var files = new TestFiles();
        var preset = new LevelPreset
        {
            Name = "Citadel market", Game = MEGame.LE3, FilePaths = [files.Level("BioD_Shops.pcc")]
        };

        Assert.IsTrue(LevelPresetStore.MatchesSearch(preset, ""));
        Assert.IsTrue(LevelPresetStore.MatchesSearch(preset, "  "));
        Assert.IsTrue(LevelPresetStore.MatchesSearch(preset, "CITADEL"));
        Assert.IsTrue(LevelPresetStore.MatchesSearch(preset, "LE3"));
        Assert.IsTrue(LevelPresetStore.MatchesSearch(preset, "BIOD_SHOPS"));
        Assert.IsTrue(LevelPresetStore.MatchesSearch(preset, files.DirectoryPath.ToUpperInvariant()));
        Assert.IsFalse(LevelPresetStore.MatchesSearch(preset, "Normandy engineering"));
    }

    [TestMethod]
    public void PresetCollectionHasNoRecentFilesLimit()
    {
        using var files = new TestFiles();
        var store = new LevelPresetStore(files.StorePath);
        for (int index = 0; index < 17; index++)
        {
            store.Save(new LevelPreset
            {
                Name = $"Location {index}", Game = MEGame.LE3, FilePaths = [files.Level($"Location_{index}.pcc")]
            });
        }

        var reloaded = new LevelPresetStore(files.StorePath);
        Assert.HasCount(17, store.Presets);
        Assert.HasCount(17, reloaded.Presets);
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 17).Select(index => $"Location {index}").ToArray(),
            reloaded.Presets.Select(preset => preset.Name).ToArray());
    }

    [TestMethod]
    public void DuplicateNamesAreRejectedWithoutChangingSavedPresets()
    {
        using var files = new TestFiles();
        var store = new LevelPresetStore(files.StorePath);
        LevelPreset existing = store.Save(new LevelPreset
        {
            Name = "Citadel", Game = MEGame.LE3, FilePaths = [files.Level("Original.pcc")]
        });
        LevelPreset another = store.Save(new LevelPreset
        {
            Name = "Normandy", Game = MEGame.LE3, FilePaths = [files.Level("Another.pcc")]
        });
        byte[] originalBytes = File.ReadAllBytes(files.StorePath);

        Assert.ThrowsExactly<InvalidOperationException>(() => store.Save(new LevelPreset
        {
            Name = "  CITADEL  ", Game = MEGame.LE3, FilePaths = [files.Level("Unexpected.pcc")]
        }));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Save(another with { Name = "citadel" }));
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(files.StorePath));
        Assert.HasCount(2, store.Presets);
        Assert.AreEqual(existing.Name, store.Presets.Single(preset => preset.Id == existing.Id).Name);
        Assert.AreEqual(another.Name, store.Presets.Single(preset => preset.Id == another.Id).Name);

        store.Save(existing with { Name = "CITADEL" });
        Assert.HasCount(2, store.Presets, "Changing the casing of a preset's own name must update its existing entry.");
    }

    [TestMethod]
    public void InvalidNamesOrEmptyFileListsDoNotAlterSavedPresets()
    {
        using var files = new TestFiles();
        var store = new LevelPresetStore(files.StorePath);
        LevelPreset existing = store.Save(new LevelPreset
        {
            Name = "Valid", Game = MEGame.LE3, FilePaths = [files.Level("Valid.pcc")]
        });
        byte[] originalBytes = File.ReadAllBytes(files.StorePath);

        Assert.Throws<ArgumentException>(() => store.Save(existing with { Name = "   " }));
        Assert.Throws<ArgumentException>(() => store.Save(existing with { FilePaths = [] }));
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(files.StorePath));
        Assert.AreEqual("Valid", store.Presets.Single().Name);
        CollectionAssert.AreEqual(existing.FilePaths.ToArray(), store.Presets.Single().FilePaths.ToArray());
    }

    [TestMethod]
    public void DeletingAPresetPersistsAndLeavesOtherPresetsIntact()
    {
        using var files = new TestFiles();
        var store = new LevelPresetStore(files.StorePath);
        LevelPreset removed = store.Save(new LevelPreset
        {
            Name = "Remove", Game = MEGame.LE3, FilePaths = [files.Level("Removed.pcc")]
        });
        LevelPreset retained = store.Save(new LevelPreset
        {
            Name = "Keep", Game = MEGame.LE2, FilePaths = [files.Level("Retained.pcc")]
        });

        store.Delete(removed);

        Assert.AreEqual(retained.Id, store.Presets.Single().Id);
        var reloaded = new LevelPresetStore(files.StorePath);
        Assert.AreEqual(retained.Id, reloaded.Presets.Single().Id);
        reloaded.Delete(reloaded.Presets.Single());
        Assert.IsEmpty(new LevelPresetStore(files.StorePath).Presets);
    }

    [TestMethod]
    public void CorruptAndUnsupportedCollectionsReportErrorsAndCannotBeOverwritten()
    {
        using var files = new TestFiles();
        File.WriteAllText(files.StorePath, "{ damaged preset collection");
        AssertUnreadableStorePreserved(files);

        File.Delete(files.StorePath);
        new LevelPresetStore(files.StorePath).Save(new LevelPreset
        {
            Name = "Existing", Game = MEGame.LE3, FilePaths = [files.Level("Existing.pcc")]
        });
        var document = JsonNode.Parse(File.ReadAllText(files.StorePath))!.AsObject();
        string versionProperty = document.Select(property => property.Key)
            .Single(key => key.Equals("Version", StringComparison.OrdinalIgnoreCase));
        document[versionProperty] = 999;
        File.WriteAllText(files.StorePath, document.ToJsonString());
        AssertUnreadableStorePreserved(files);
    }

    private static void AssertUnreadableStorePreserved(TestFiles files)
    {
        byte[] originalBytes = File.ReadAllBytes(files.StorePath);
        var store = new LevelPresetStore(files.StorePath);
        Assert.IsFalse(string.IsNullOrWhiteSpace(store.LoadError));
        Assert.IsEmpty(store.Presets);
        Assert.ThrowsExactly<InvalidDataException>(() => store.Save(new LevelPreset
        {
            Name = "New", Game = MEGame.LE3, FilePaths = [files.Level("New.pcc")]
        }));
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(files.StorePath),
            "An unreadable collection must remain available for recovery.");
    }

    private static string CreateValidationPackage(string path, MEGame game, bool containsLevel = true)
    {
        MEPackageHandler.CreateAndSavePackage(path, game);
        if (!containsLevel) return path;
        using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        var engine = new ImportEntry(package)
        {
            ObjectName = "Engine", ClassName = "Package", PackageFile = "Core"
        };
        package.AddImport(engine);
        var levelClass = new ImportEntry(package)
        {
            ObjectName = "Level", ClassName = "Class", PackageFile = "Core", idxLink = engine.UIndex
        };
        package.AddImport(levelClass);
        package.AddExport(new ExportEntry(package, 0, "PersistentLevel") { Class = levelClass });
        package.Save(compress: false);
        return path;
    }

    private sealed class TestFiles : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"LEXLevelPresetTests-{Guid.NewGuid():N}");
        public string StorePath => Path.Combine(DirectoryPath, "LevelPresets.json");

        public TestFiles() => Directory.CreateDirectory(DirectoryPath);

        public string Level(string fileName) => Path.Combine(DirectoryPath, fileName);

        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
}
