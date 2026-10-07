using System.Linq;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.Tools.AssetDatabase.Filters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.AssetDatabase;

[TestClass]
public class MorphFaceAssetTests
{
    [TestMethod]
    public void PackageCopiesOfTheSameInstancedPathProduceOneMorphAsset()
    {
        var first = CreateMorph("BioChar.Faces.Person_2", 4, 31);
        var second = CreateMorph("biochar.faces.person_2", 9, 72);

        var assets = MorphFaceAsset.GroupRecords([first, second]);

        Assert.HasCount(1, assets);
        Assert.HasCount(2, assets[0].Usages);
        CollectionAssert.AreEquivalent(new[] { 4, 9 }, assets[0].Usages.Select(usage => usage.FileKey).ToArray());
        Assert.HasCount(2, assets[0].AssetUsages.ToArray());
    }

    [TestMethod]
    public void MatchingTerminalNamesInDifferentPathsRemainSeparateInstances()
    {
        var assets = MorphFaceAsset.GroupRecords(
        [
            CreateMorph("BioChar.Crew.Person_2", 4, 31),
            CreateMorph("BioChar.Civilians.Person_2", 9, 72),
            CreateMorph("BioChar.Crew.Person_3", 12, 15)
        ]);

        Assert.HasCount(3, assets);
        Assert.IsTrue(assets.All(asset => asset.Usages.Count == 1));
    }

    [TestMethod]
    public void ModCopiesRetainTheirOwnBaseHeadAndMorphParameters()
    {
        var original = CreateMorph("BioChar.Faces.Person", 4, 31);
        var mod = CreateModMorph();

        var asset = MorphFaceAsset.GroupRecords([mod, original]).Single();
        var originalUsage = asset.Usages.Single(usage => usage.FileKey == original.FileKey);
        var modUsage = asset.Usages.Single(usage => usage.FileKey == mod.FileKey);

        Assert.AreSame(original, originalUsage.Morph);
        Assert.AreSame(mod, modUsage.Morph);
        Assert.AreEqual("HMF_HED_ORIGINAL_MDL", originalUsage.Morph.BaseHeadName);
        Assert.AreEqual("ASA_HED_ALIEN_MDL", modUsage.Morph.BaseHeadName);
        Assert.AreEqual(BioMorphSpecies.Asari, modUsage.Morph.Species);
        Assert.AreEqual("scar_feature", modUsage.Morph.Features.Single().Name);
        Assert.AreEqual(0.8f, modUsage.Morph.Features.Single().Value);
        Assert.AreEqual(0.4f, modUsage.Morph.ScalarOverrides.Single().Value);
        Assert.AreEqual(0.3f, modUsage.Morph.ColorOverrides.Single().B);
        Assert.IsTrue(modUsage.IsInMod);
        Assert.AreEqual(mod.UIndex, modUsage.UIndex);
        Assert.AreSame(original, asset.Usages[0].Morph);
        Assert.AreEqual(original.BaseHeadName, asset.BaseHeadName);
    }

    [TestMethod]
    public void OnlyAssetsWithNoOfficialCopyAreMarkedModOnly()
    {
        var mixedAsset = MorphFaceAsset.GroupRecords(
        [
            CreateModMorph(),
            CreateMorph("BioChar.Faces.Person", 4, 31)
        ]).Single();
        var modOnlyAsset = MorphFaceAsset.GroupRecords(
        [
            CreateModMorph(),
            CreateModMorph() with { FileKey = 18, UIndex = 85 }
        ]).Single();

        Assert.IsFalse(mixedAsset.IsModOnly);
        Assert.IsTrue(modOnlyAsset.IsModOnly);
    }

    [TestMethod]
    public void RepeatedFileExportPairsDoNotDuplicateUsageRows()
    {
        var original = CreateMorph("BioChar.Faces.Person", 4, 31);

        var asset = MorphFaceAsset.GroupRecords(
        [
            original,
            original with { Features = [new BioMorphFeatureRecord("duplicate", 1)] },
            original with { UIndex = 32 },
            original with { FileKey = 9 }
        ]).Single();

        Assert.HasCount(3, asset.Usages);
        Assert.AreSame(original, asset.Usages.Single(usage => usage.FileKey == 4 && usage.UIndex == 31).Morph);
        Assert.IsTrue(asset.Usages.Any(usage => usage.FileKey == 4 && usage.UIndex == 32));
        Assert.IsTrue(asset.Usages.Any(usage => usage.FileKey == 9 && usage.UIndex == 31));
    }

    [DataTestMethod]
    [DataRow("asa_hed_alien_mdl")]
    [DataRow("ASARI")]
    [DataRow("SCAR_FEATURE")]
    [DataRow("roughness")]
    [DataRow("skin_tint")]
    [DataRow("modded_faces.pcc")]
    [DataRow("417")]
    public void SearchFindsMetadataAndSourceFilesOfNonrepresentativeCopies(string query)
    {
        var asset = MorphFaceAsset.GroupRecords(
        [
            CreateMorph("BioChar.Faces.Person", 4, 31),
            CreateModMorph()
        ]).Single();

        var matches = asset.GetMatchingUsages(query, GetFileName).ToArray();

        Assert.HasCount(1, matches);
        Assert.AreEqual(17, matches[0].FileKey);
        Assert.AreEqual(417, matches[0].UIndex);
    }

    [TestMethod]
    public void FileListFilterCanMatchANonrepresentativeCopy()
    {
        var asset = MorphFaceAsset.GroupRecords(
        [
            CreateMorph("BioChar.Faces.Person", 4, 31),
            CreateModMorph()
        ]).Single();
        var filter = new FileListSpecification();
        filter.CustomFileList.Add(17, "modded_faces.pcc");

        Assert.IsTrue(filter.MatchesSpecification(asset));
        filter.CustomFileList.Clear();
        filter.CustomFileList.Add(99, "unrelated.pcc");
        Assert.IsFalse(filter.MatchesSpecification(asset));
    }

    [TestMethod]
    public void QueryAndFileRestrictionMustMatchTheSamePackageCopy()
    {
        var asset = MorphFaceAsset.GroupRecords(
        [
            CreateMorph("BioChar.Faces.Person", 4, 31),
            CreateModMorph()
        ]).Single();

        Assert.IsEmpty(asset.GetMatchingUsages("scar_feature", GetFileName, key => key == 4).ToArray());
        var matchingMod = asset.GetMatchingUsages("scar_feature", GetFileName, key => key == 17).ToArray();
        Assert.HasCount(1, matchingMod);
        Assert.AreEqual(17, matchingMod[0].FileKey);

        var sharedNameMatch = asset.GetMatchingUsages("BioChar.Faces.Person", GetFileName, key => key == 4).ToArray();
        Assert.HasCount(1, sharedNameMatch);
        Assert.AreEqual(4, sharedNameMatch[0].FileKey);
        Assert.HasCount(1, asset.GetMatchingUsages(null, GetFileName, key => key == 17).ToArray());
    }

    private static BioMorphFaceRecord CreateMorph(string name, int fileKey, int uIndex) => new(
        name, "HMF_HED_ORIGINAL_MDL", BioMorphSpecies.HumanFemale, fileKey, uIndex, false,
        [new BioMorphFeatureRecord("original_feature", 0.2f)], [], []);

    private static BioMorphFaceRecord CreateModMorph() => new(
        "BioChar.Faces.Person", "ASA_HED_ALIEN_MDL", BioMorphSpecies.Asari, 17, 417, true,
        [new BioMorphFeatureRecord("scar_feature", 0.8f)],
        [new BioMorphScalarRecord("roughness", 0.4f)],
        [new BioMorphColorRecord("skin_tint", 0.1f, 0.2f, 0.3f, 1)]);

    private static string GetFileName(int fileKey) => fileKey switch
    {
        4 => "original_faces.pcc",
        17 => "modded_faces.pcc",
        _ => "other_faces.pcc"
    };
}
