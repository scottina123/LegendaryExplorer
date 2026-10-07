using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
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
    public void CameraPresetsPreserveNamesLocationsRotationsAndOrderAfterReload()
    {
        using var files = new TestFiles();
        var store = new LevelPresetStore(files.StorePath);
        var first = new LevelCameraPreset
        {
            Name = "  Market entrance  ", X = 123.5f, Y = -987.25f, Z = 456,
            Roll = -15.5f, Pitch = 120, Yaw = 270.25f
        };
        var second = new LevelCameraPreset { Name = "Upper balcony", X = 20, Y = 30, Z = 40, Pitch = -35 };

        LevelPreset saved = store.Save(new LevelPreset
        {
            Name = "Citadel", Game = MEGame.LE3, FilePaths = [files.Level("Citadel.pcc")],
            CameraPresets = [first, second]
        });

        LevelPreset restored = new LevelPresetStore(files.StorePath).Presets.Single();
        Assert.HasCount(2, restored.CameraPresets);
        Assert.AreEqual(first with { Name = "Market entrance" }, restored.CameraPresets[0]);
        Assert.AreEqual(second, restored.CameraPresets[1]);
        CollectionAssert.AreEqual(saved.CameraPresets.ToArray(), restored.CameraPresets.ToArray());
        Assert.AreEqual(1, JsonNode.Parse(File.ReadAllText(files.StorePath))!["Version"]!.GetValue<int>());
    }

    [TestMethod]
    public void LegacyPresetWithoutCameraEntriesKeepsItsDefaultSpawnBehavior()
    {
        using var files = new TestFiles();
        new LevelPresetStore(files.StorePath).Save(new LevelPreset
        {
            Name = "Legacy", Game = MEGame.LE3, FilePaths = [files.Level("Legacy.pcc")]
        });
        var document = JsonNode.Parse(File.ReadAllText(files.StorePath))!;
        document["Presets"]![0]!.AsObject().Remove("CameraPresets");
        File.WriteAllText(files.StorePath, document.ToJsonString());

        var restored = new LevelPresetStore(files.StorePath);

        Assert.IsNull(restored.LoadError);
        Assert.IsEmpty(restored.Presets.Single().CameraPresets);
        LevelPreset saved = restored.Save(restored.Presets.Single() with { Name = "Legacy renamed" });
        Assert.IsEmpty(saved.CameraPresets);
        Assert.IsEmpty(new LevelPresetStore(files.StorePath).Presets.Single().CameraPresets);
    }

    [TestMethod]
    public void CameraCollectionsBelongToTheirParentAndAreClonedWhenSaved()
    {
        using var files = new TestFiles();
        var store = new LevelPresetStore(files.StorePath);
        var camera = new LevelCameraPreset { Name = "Entrance", X = 10 };
        var firstInput = new LevelPreset
        {
            Name = "First", Game = MEGame.LE3, FilePaths = [files.Level("First.pcc")], CameraPresets = [camera]
        };
        LevelPreset first = store.Save(firstInput);
        LevelPreset second = store.Save(new LevelPreset
        {
            Name = "Second", Game = MEGame.LE3, FilePaths = [files.Level("Second.pcc")],
            CameraPresets = [camera with { X = 20 }]
        });

        firstInput.CameraPresets.Clear();

        Assert.HasCount(1, first.CameraPresets, "Changing the caller's list must not change the saved preset.");
        Assert.AreNotSame(first.CameraPresets, second.CameraPresets);
        Assert.AreEqual(10f, first.CameraPresets.Single().X);
        Assert.AreEqual(20f, second.CameraPresets.Single().X);
        store.Save(first with { CameraPresets = [] });
        LevelPreset[] restored = new LevelPresetStore(files.StorePath).Presets.ToArray();
        Assert.IsEmpty(restored.Single(preset => preset.Id == first.Id).CameraPresets);
        Assert.AreEqual(camera with { X = 20 }, restored.Single(preset => preset.Id == second.Id).CameraPresets.Single());
        Assert.AreNotSame(new LevelPreset().CameraPresets, new LevelPreset().CameraPresets);
    }

    [TestMethod]
    public void DuplicateCameraNamesAndIdentifiersDoNotAlterSavedPresets()
    {
        using var files = new TestFiles();
        var store = new LevelPresetStore(files.StorePath);
        var camera = new LevelCameraPreset { Name = "Entrance", X = 15 };
        LevelPreset existing = store.Save(new LevelPreset
        {
            Name = "Valid", Game = MEGame.LE3, FilePaths = [files.Level("Valid.pcc")], CameraPresets = [camera]
        });
        byte[] originalBytes = File.ReadAllBytes(files.StorePath);

        Assert.Throws<ArgumentException>(() => store.Save(existing with
        {
            CameraPresets = [camera, new LevelCameraPreset { Name = "  ENTRANCE  " }]
        }));
        Assert.Throws<ArgumentException>(() => store.Save(existing with
        {
            CameraPresets = [camera, camera with { Name = "Another location" }]
        }));

        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(files.StorePath));
        Assert.AreSame(existing, store.Presets.Single());
        Assert.AreEqual(camera, store.Presets.Single().CameraPresets.Single());
    }

    [TestMethod]
    public void InvalidCameraNamesAndNonfiniteValuesDoNotAlterSavedPresets()
    {
        using var files = new TestFiles();
        var store = new LevelPresetStore(files.StorePath);
        var camera = new LevelCameraPreset { Name = "Entrance", X = 15 };
        LevelPreset existing = store.Save(new LevelPreset
        {
            Name = "Valid", Game = MEGame.LE3, FilePaths = [files.Level("Valid.pcc")], CameraPresets = [camera]
        });
        byte[] originalBytes = File.ReadAllBytes(files.StorePath);
        LevelCameraPreset[] invalidCameras =
        [
            camera with { Name = "   " }, camera with { Name = null },
            camera with { X = float.NaN }, camera with { Y = float.PositiveInfinity },
            camera with { Z = float.NegativeInfinity }, camera with { Roll = float.NaN },
            camera with { Pitch = float.PositiveInfinity }, camera with { Yaw = float.NegativeInfinity }, null
        ];

        foreach (LevelCameraPreset invalid in invalidCameras)
        {
            Assert.Throws<ArgumentException>(() => store.Save(existing with { CameraPresets = [invalid] }));
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(files.StorePath));
            Assert.AreSame(existing, store.Presets.Single());
        }
    }

    [TestMethod]
    public void InvalidSavedCameraEntriesReportAnErrorAndPreserveTheLibrary()
    {
        using var files = new TestFiles();
        var camera = new LevelCameraPreset { Name = "Entrance" };
        new LevelPresetStore(files.StorePath).Save(new LevelPreset
        {
            Name = "Valid", Game = MEGame.LE3, FilePaths = [files.Level("Valid.pcc")], CameraPresets = [camera]
        });
        var document = JsonNode.Parse(File.ReadAllText(files.StorePath))!;
        document["Presets"]![0]!["CameraPresets"]!.AsArray().Add(
            JsonNode.Parse(document["Presets"]![0]!["CameraPresets"]![0]!.ToJsonString()));
        File.WriteAllText(files.StorePath, document.ToJsonString());

        AssertUnreadableStorePreserved(files);
    }

    [TestMethod]
    public void CameraSearchMatchesNamesIgnoringCaseAndOuterWhitespace()
    {
        var camera = new LevelCameraPreset { Name = "Market entrance" };

        Assert.IsTrue(LevelPresetStore.MatchesCameraSearch(camera, null));
        Assert.IsTrue(LevelPresetStore.MatchesCameraSearch(camera, "  "));
        Assert.IsTrue(LevelPresetStore.MatchesCameraSearch(camera, "  ENTRANCE  "));
        Assert.IsFalse(LevelPresetStore.MatchesCameraSearch(camera, "balcony"));
        Assert.IsFalse(LevelPresetStore.MatchesCameraSearch(null, ""));
    }

    [TestMethod]
    public void ApplyingCameraPresetsConvertsDegreesAndKeepsUnclampedPitch()
    {
        var camera = new SceneCamera { Position = new Vector3(-10, 20, 30), FocusDepth = 100 };
        var preset = new LevelCameraPreset
        {
            Name = "Tilted view", X = 123, Y = -456, Z = 789, Roll = 30, Pitch = 120, Yaw = 270
        };

        preset.ApplyTo(camera);

        Assert.AreEqual(new Vector3(123, -456, 789), camera.Position);
        Assert.AreEqual(0f, camera.FocusDepth);
        Assert.AreEqual(MathF.PI / 6f, camera.Roll, 0.00001f);
        Assert.AreEqual(MathF.PI * 2f / 3f, camera.Pitch, 0.00001f);
        Assert.AreEqual(MathF.PI * 1.5f, camera.Yaw, 0.00001f);
    }

    [TestMethod]
    public void CapturingOrbitCameraPreservesTheEyeLocationAndViewWhenApplied()
    {
        var original = new SceneCamera
        {
            Position = new Vector3(100, -200, 300), FocusDepth = 500,
            Roll = MathF.PI / 6, Pitch = -MathF.PI / 4, Yaw = MathF.PI * 1.5f
        };
        Matrix4x4 view = original.ViewMatrix;

        LevelCameraPreset preset = LevelCameraPreset.FromCamera(original, "Orbit view");
        var restored = new SceneCamera();
        preset.ApplyTo(restored);

        Assert.AreEqual("Orbit view", preset.Name);
        Vector3 expectedEye = original.Position - original.CameraForward * original.FocusDepth;
        Assert.AreEqual(expectedEye.X, preset.X, 0.00001f);
        Assert.AreEqual(expectedEye.Y, preset.Y, 0.00001f);
        Assert.AreEqual(expectedEye.Z, preset.Z, 0.00001f);
        Assert.AreEqual(30f, preset.Roll, 0.00001f);
        Assert.AreEqual(-45f, preset.Pitch, 0.00001f);
        Assert.AreEqual(270f, preset.Yaw, 0.0001f);
        AssertMatricesEqual(view, restored.ViewMatrix);
    }

    [TestMethod]
    public void CapturingFirstPersonCameraUsesItsPositionWithoutOrbitOffset()
    {
        var camera = new SceneCamera
        {
            FirstPerson = true, Position = new Vector3(100, 200, 300), FocusDepth = 500,
            Roll = MathF.PI / 6, Pitch = MathF.PI / 4, Yaw = MathF.PI / 2
        };

        LevelCameraPreset preset = LevelCameraPreset.FromCamera(camera, "First person");

        Assert.AreEqual(camera.Position, new Vector3(preset.X, preset.Y, preset.Z));
        var restored = new SceneCamera { FirstPerson = true };
        preset.ApplyTo(restored);
        AssertMatricesEqual(camera.ViewMatrix, restored.ViewMatrix);
    }

    [TestMethod]
    public void CapturingOrthographicCameraPreservesItsPositionAndTopDownOrientation()
    {
        var camera = new SceneCamera
        {
            IsOrthographic = true, Position = new Vector3(100, 200, 300), FocusDepth = 500,
            Roll = MathF.PI / 6, Pitch = MathF.PI / 4, Yaw = MathF.PI
        };

        LevelCameraPreset preset = LevelCameraPreset.FromCamera(camera, "Top down");
        var restored = new SceneCamera();
        preset.ApplyTo(restored);

        Assert.AreEqual(camera.Position, restored.Position);
        Assert.AreEqual(0f, preset.Roll);
        Assert.AreEqual(-90f, preset.Pitch);
        Assert.AreEqual(90f, preset.Yaw);
        Assert.IsFalse(restored.IsOrthographic);
        AssertMatricesEqual(camera.ViewMatrix, restored.ViewMatrix);
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

    private static void AssertMatricesEqual(Matrix4x4 expected, Matrix4x4 actual)
    {
        Assert.AreEqual(expected.M11, actual.M11, 0.0001f);
        Assert.AreEqual(expected.M12, actual.M12, 0.0001f);
        Assert.AreEqual(expected.M13, actual.M13, 0.0001f);
        Assert.AreEqual(expected.M14, actual.M14, 0.0001f);
        Assert.AreEqual(expected.M21, actual.M21, 0.0001f);
        Assert.AreEqual(expected.M22, actual.M22, 0.0001f);
        Assert.AreEqual(expected.M23, actual.M23, 0.0001f);
        Assert.AreEqual(expected.M24, actual.M24, 0.0001f);
        Assert.AreEqual(expected.M31, actual.M31, 0.0001f);
        Assert.AreEqual(expected.M32, actual.M32, 0.0001f);
        Assert.AreEqual(expected.M33, actual.M33, 0.0001f);
        Assert.AreEqual(expected.M34, actual.M34, 0.0001f);
        Assert.AreEqual(expected.M41, actual.M41, 0.001f);
        Assert.AreEqual(expected.M42, actual.M42, 0.001f);
        Assert.AreEqual(expected.M43, actual.M43, 0.001f);
        Assert.AreEqual(expected.M44, actual.M44, 0.0001f);
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
