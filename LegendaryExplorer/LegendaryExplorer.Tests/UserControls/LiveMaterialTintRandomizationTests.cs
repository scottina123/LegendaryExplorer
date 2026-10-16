using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class LiveMaterialTintRandomizationTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void CatalogRequiresEveryMaterialCopyToHaveKnownOfficialProvenance()
    {
        var database = new AssetDB
        {
            Materials =
            [
                Material("Official", [new(1, 101, false)]),
                Material("Mod", [new(2, 102, false)]),
                Material("Mixed", [new(1, 101, false), new(2, 102, false)]),
                Material("Unknown", [new(3, 103, false)]),
                Material("WrongExport", [new(1, 104, false)]),
                Material("WrongFile", [new(9, 101, false)]),
                Material("ConflictingProvenance", [new(4, 104, false)]),
                Material("NoUsages", [])
            ],
            ClassRecords =
            [
                new() { Usages = [new(1, 101, false, false), new(2, 102, false, true), new(4, 104, false, false)] },
                new() { Usages = [new(4, 104, false, true)] }
            ]
        };

        CollectionAssert.AreEqual(new[] { "Official" },
            LiveMaterialTintCatalog.BuildProfiles(database).Select(profile => profile.MaterialName).ToArray());
    }

    [TestMethod]
    public void CatalogIncludesOfficialDlcAndPreservesTheParentMaterialKey()
    {
        var material = Material("OfficialDlc", [new(1, 101, true), new(2, 102, true)]);
        material.ParentMaterialKey = "BIOG_HMM_ARM_CTH.Materials.Base";
        material.IsDLCOnly = true;

        var profiles = LiveMaterialTintCatalog.BuildProfiles(OfficialDatabase(material));

        Assert.HasCount(1, profiles);
        Assert.AreEqual(material.ParentMaterialKey, profiles[0].ParentMaterialKey);
        AssertColor(profiles[0].Tints["TNT_Primary"], 0.25f, 0.5f, 0.75f, 1f);
    }

    [TestMethod]
    public void CatalogReadsVectorOverridesAndDefinitionsButIgnoresUnrelatedSettings()
    {
        var material = Material("Palette", [new(1, 101, false)],
            new("VectorParameter", "TNT_Primary", "R:0.25 G:0.5 B:0.75 A:1"),
            new("MaterialExpressionVectorParameter", "tnt_Secondary", "R:2 G:-0.5 B:0.125 A:0.25"),
            new("VectorParameter", "Diffuse", "R:1 G:0 B:0 A:1"),
            new("ScalarParameter", "TNT_Scalar", "R:1 G:0 B:0 A:1"),
            new("TextureParameterValue", "TNT_Texture", "R:1 G:0 B:0 A:1"));

        var profile = LiveMaterialTintCatalog.BuildProfiles(OfficialDatabase(material)).Single();

        CollectionAssert.AreEquivalent(new[] { "TNT_Primary", "tnt_Secondary" }, profile.Tints.Keys.ToArray());
        AssertColor(profile.Tints["tnt_Secondary"], 2f, -0.5f, 0.125f, 0.25f);
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("de-DE")]
    public void CatalogReadsInvariantAndDecimalCommaValuesIndependentlyOfCurrentCulture(string culture)
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var material = Material("Localized", [new(1, 101, false)],
                new("VectorParameter", "TNT_Invariant", "R:2.5 G:-0.25 B:1.25e-1 A:0.75"),
                new("VectorParameter", "TNT_Comma", "R:2,5 G:-0,25 B:1,25e-1 A:0,75"));

            var profile = LiveMaterialTintCatalog.BuildProfiles(OfficialDatabase(material)).Single();

            Assert.HasCount(2, profile.Tints);
            AssertColor(profile.Tints["TNT_Invariant"], 2.5f, -0.25f, 0.125f, 0.75f);
            AssertColor(profile.Tints["TNT_Comma"], 2.5f, -0.25f, 0.125f, 0.75f);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [TestMethod]
    [DataRow("R:NaN G:0 B:0 A:1")]
    [DataRow("R:0 G:Infinity B:0 A:1")]
    [DataRow("R:0 G:0 B:0 A:-Infinity")]
    [DataRow("R:1e50 G:0 B:0 A:1")]
    [DataRow("R:0 G:0 B:0")]
    [DataRow("R:0 G:0 B:0 B:1")]
    [DataRow("0 0 0 1")]
    [DataRow("")]
    [DataRow(null)]
    public void CatalogIgnoresMalformedAndNonfiniteColorsWithoutDiscardingValidTints(string value)
    {
        var material = Material("Palette", [new(1, 101, false)],
            new("VectorParameter", "TNT_Valid", "R:0.25 G:0.5 B:0.75 A:1"),
            new("VectorParameter", "TNT_Invalid", value));

        var profile = LiveMaterialTintCatalog.BuildProfiles(OfficialDatabase(material)).Single();

        CollectionAssert.AreEqual(new[] { "TNT_Valid" }, profile.Tints.Keys.ToArray());
    }

    [TestMethod]
    public void ProfilesPreferCompatibleParentsThenTheMostMatchingTintSlots()
    {
        var partialParent = Profile("Partial", "Materials.Base", "TNT_Primary");
        var fullParent = Profile("Full", "materials.base", "tnt_primary", "TNT_Secondary");
        var unrelatedParent = Profile("Unrelated", "Materials.Other", "TNT_Primary", "TNT_Secondary", "TNT_Tertiary");
        var incompatibleParent = Profile("Incompatible", "Materials.Base", "TNT_Unused");

        var profiles = LiveMaterialTintRandomization.SelectProfiles(
            [partialParent, fullParent, unrelatedParent, incompatibleParent],
            ["TNT_Primary", "TNT_Secondary", "TNT_Tertiary"], "MATERIALS.BASE");

        CollectionAssert.AreEqual(new[] { fullParent }, profiles.ToArray());
    }

    [TestMethod]
    public void ProfilesFallBackAcrossParentsUsingExactCaseInsensitiveTintNames()
    {
        var partial = Profile("Partial", "Materials.Other", "TNT_Primary");
        var first = Profile("First", null, "tnt_primary", "TNT_Secondary");
        var second = Profile("Second", "Materials.Other", "TNT_Primary", "tnt_secondary");
        var wrongSuffix = Profile("Suffix", null, "TNT_Primary_1");
        var nonTint = Profile("NonTint", null, "Diffuse");

        var profiles = LiveMaterialTintRandomization.SelectProfiles(
            [partial, first, second, wrongSuffix, nonTint],
            ["TNT_Primary", "tnt_secondary", "TNT_Primary", "Diffuse"], "Materials.Missing");

        CollectionAssert.AreEqual(new[] { first, second }, profiles.ToArray());
        Assert.IsEmpty(LiveMaterialTintRandomization.SelectProfiles([wrongSuffix, nonTint],
            ["TNT_Primary", "Diffuse"], null));
    }

    [TestMethod]
    public void BlendingPreservesPaletteRelationshipsAndCurrentAlphaWithoutChangingOtherParameters()
    {
        var current = new Dictionary<string, LinearColor>
        {
            ["TNT_Primary"] = new(0.5f, 0.5f, 0.5f, 0.25f),
            ["tnt_Secondary"] = new(0.5f, 0.5f, 0.5f, 0.5f),
            ["TNT_Tertiary"] = new(0.5f, 0.5f, 0.5f, 0.75f),
            ["TNT_Unmatched"] = new(0.125f, 0.25f, 0.5f, 1f),
            ["Diffuse"] = new(0.125f, 0.25f, 0.5f, 1f)
        };
        var first = new LiveMaterialTintProfile("First", "First", null, new Dictionary<string, LinearColor>
        {
            ["tnt_primary"] = new(4, -2, 0, 0),
            ["TNT_Secondary"] = new(0.25f, 2, -0.5f, 0),
            ["Diffuse"] = new(1),
            ["TNT_DonorOnly"] = new(1)
        });
        var second = new LiveMaterialTintProfile("Second", "Second", null, new Dictionary<string, LinearColor>
        {
            ["TNT_Primary"] = new(0, 2, 8, 1),
            ["TNT_Tertiary"] = new(2, 0.125f, 0.25f, 1),
            ["Diffuse"] = new(0)
        });

        var values = LiveMaterialTintRandomization.BlendTints(current, first, second, 0.25f);

        CollectionAssert.AreEquivalent(new[] { "TNT_Primary", "tnt_Secondary", "TNT_Tertiary" }, values.Keys.ToArray());
        AssertColor(values["TNT_Primary"], 1, 1, 6, 0.25f);
        AssertColor(values["tnt_Secondary"], 0.25f, 2, -0.5f, 0.5f);
        AssertColor(values["TNT_Tertiary"], 2, 0.125f, 0.25f, 0.75f);
        AssertColor(current["TNT_Primary"], 0.5f, 0.5f, 0.5f, 0.25f);
        AssertColor(current["TNT_Unmatched"], 0.125f, 0.25f, 0.5f, 1f);
        AssertColor(current["Diffuse"], 0.125f, 0.25f, 0.5f, 1f);
    }

    [STATestMethod]
    public async Task DefaultRandomizationUpdatesOnlyTintPreviewRgbWithoutLoadingTheCatalogOrEditingThePackage()
    {
        using var fixture = new PreviewFixture();
        var randomization = new LiveMaterialTintRandomization(_ =>
            throw new AssertFailedException("The default mode must not load the Asset Database."));
        byte[] originalData = fixture.Proxy.MaterialExport.Data.ToArray();

        await randomization.RandomizeAsync(fixture.Material, () => true);

        LinearColor tint = fixture.Proxy.VectorParameters["TNT_Primary"];
        Assert.IsTrue(tint.R is >= 0 and < 1 && tint.G is >= 0 and < 1 && tint.B is >= 0 and < 1);
        Assert.AreEqual(0.25f, tint.A);
        AssertColor(fixture.Proxy.VectorParameters["Diffuse"], 0.125f, 0.25f, 0.5f, 1f);
        Assert.AreEqual(1, fixture.Proxy.VectorWriteCount);
        Assert.AreEqual(0, fixture.Proxy.OtherWriteCount);
        Assert.IsTrue(fixture.Material.HasUnsavedChanges);
        Assert.IsTrue(randomization.IsIdle);
        CollectionAssert.AreEqual(originalData, fixture.Proxy.MaterialExport.Data);
    }

    [STATestMethod]
    public async Task VanillaRandomizationStopsWhenTheSelectedMaterialChangesDuringCatalogLoading()
    {
        using var fixture = new PreviewFixture();
        var catalog = new TaskCompletionSource<LiveMaterialTintCatalogResult>();
        var randomization = new LiveMaterialTintRandomization(_ => catalog.Task) { UseBioWareTintValues = true };
        bool isCurrent = true;

        Task operation = randomization.RandomizeAsync(fixture.Material, () => isCurrent);
        Assert.IsFalse(randomization.IsIdle);
        isCurrent = false;
        catalog.SetResult(new LiveMaterialTintCatalogResult([Profile("Official", null, "TNT_Primary")]));
        await operation;

        Assert.AreEqual(0, fixture.Proxy.VectorWriteCount);
        Assert.IsFalse(fixture.Material.HasUnsavedChanges);
        Assert.IsTrue(randomization.IsIdle);
        Assert.IsNull(randomization.Status);
    }

    [STATestMethod]
    public async Task VanillaCatalogErrorsLeaveThePreviewUnchangedAndExplainTheFailure()
    {
        using var fixture = new PreviewFixture();
        const string error = "The LE3 Asset Database is not installed. Generate it from Tools > Asset Database, then try again.";
        var randomization = new LiveMaterialTintRandomization(_ =>
            Task.FromResult(new LiveMaterialTintCatalogResult([], error))) { UseBioWareTintValues = true };

        await randomization.RandomizeAsync(fixture.Material, () => true);

        Assert.AreEqual(error, randomization.Status);
        Assert.AreEqual(0, fixture.Proxy.VectorWriteCount);
        Assert.IsFalse(fixture.Material.HasUnsavedChanges);
        Assert.IsTrue(randomization.IsIdle);
    }

    [STATestMethod]
    public async Task VanillaRandomizationAppliesCompatibleRgbToThePreviewAndPreservesCurrentAlpha()
    {
        using var fixture = new PreviewFixture();
        byte[] originalData = fixture.Proxy.MaterialExport.Data.ToArray();
        var randomization = new LiveMaterialTintRandomization(_ => Task.FromResult(
            new LiveMaterialTintCatalogResult([Profile("Official", null, "tnt_primary")]))) { UseBioWareTintValues = true };

        await randomization.RandomizeAsync(fixture.Material, () => true);

        AssertColor(fixture.Proxy.VectorParameters["TNT_Primary"], 1, 1, 1, 0.25f);
        AssertColor(fixture.Proxy.VectorParameters["Diffuse"], 0.125f, 0.25f, 0.5f, 1f);
        Assert.AreEqual(1, fixture.Proxy.VectorWriteCount);
        Assert.AreEqual(0, fixture.Proxy.OtherWriteCount);
        Assert.IsTrue(fixture.Material.HasUnsavedChanges);
        Assert.IsTrue(randomization.IsIdle);
        StringAssert.Contains(randomization.Status, "Randomized 1 tints");
        CollectionAssert.AreEqual(originalData, fixture.Proxy.MaterialExport.Data);
    }

    private static MaterialRecord Material(string name, List<MatUsage> usages, params MatSetting[] settings) =>
        new(name, "Package", false, settings.Length > 0 ? settings :
            [new("VectorParameter", "TNT_Primary", "R:0.25 G:0.5 B:0.75 A:1")]) { Usages = usages };

    private static AssetDB OfficialDatabase(params MaterialRecord[] materials) => new()
    {
        Materials = materials.ToList(),
        ClassRecords = [new() { Usages = materials.SelectMany(material => material.Usages)
            .Select(usage => new ClassUsage(usage.FileKey, usage.UIndex, false, false)).ToArray() }]
    };

    private static LiveMaterialTintProfile Profile(string name, string parent, params string[] tintNames) =>
        new(name, name, parent, tintNames.ToDictionary(tint => tint, _ => LinearColor.White));

    private static void AssertColor(LinearColor actual, float r, float g, float b, float a)
    {
        Assert.AreEqual(r, actual.R, 0.000001f, "Red");
        Assert.AreEqual(g, actual.G, 0.000001f, "Green");
        Assert.AreEqual(b, actual.B, 0.000001f, "Blue");
        Assert.AreEqual(a, actual.A, "Alpha must be preserved exactly.");
    }

    private sealed class PreviewFixture : IDisposable
    {
        private readonly IMEPackage _package = MEPackageHandler.CreateMemoryEmptyPackage("TintPreview.pcc", MEGame.LE3);
        public FakeRenderProxy Proxy { get; }
        public LiveMaterialEditorMaterial Material { get; }

        public PreviewFixture()
        {
            ExportEntry export = _package.CreateExport("Material", "MaterialInstanceConstant", indexed: false);
            export.WriteProperty(new IntProperty(123, "Sentinel"));
            Proxy = new FakeRenderProxy(export);
            Material = new LiveMaterialEditorMaterial(Proxy, export);
        }

        public void Dispose() => _package.Dispose();
    }

    private sealed class FakeRenderProxy(ExportEntry export) : ILiveMaterialRenderProxy
    {
        private readonly Dictionary<string, float> _scalars = new() { ["Roughness"] = 0.5f };
        private readonly Dictionary<string, LinearColor> _vectors = new()
        {
            ["TNT_Primary"] = new(-1, -1, -1, 0.25f),
            ["Diffuse"] = new(0.125f, 0.25f, 0.5f, 1f)
        };

        public ExportEntry MaterialExport => export;
        public IReadOnlyDictionary<string, float> ScalarParameters => _scalars;
        public IReadOnlyDictionary<string, LinearColor> VectorParameters => _vectors;
        public int VectorWriteCount { get; private set; }
        public int OtherWriteCount { get; private set; }

        public void SetVectorParameter(string parameterName, LinearColor value)
        {
            VectorWriteCount++;
            _vectors[parameterName] = value;
        }

        public void SetScalarParameter(string parameterName, float value) => OtherWriteCount++;
        public void RemoveScalarParameter(string parameterName) => OtherWriteCount++;
        public void RemoveVectorParameter(string parameterName) => OtherWriteCount++;
        public void CommitPreviewParameterOverrides() => OtherWriteCount++;
        public void ResetPreviewParameterOverrides() => OtherWriteCount++;
    }
}
