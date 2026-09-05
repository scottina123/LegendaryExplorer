using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class MorphViewportPickingTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void TrianglePickReturnsSurfaceDistanceAndInterpolatedPosition()
    {
        Vector3 a = new(0, 0, 2), b = new(1, 0, 2), c = new(0, 1, 2);
        Assert.IsTrue(MorphViewportPicking.IntersectTriangle(new Vector3(0.25f, 0.5f, 0), Vector3.UnitZ,
            a, b, c, out float distance, out var weights));
        Assert.AreEqual(2f, distance, 0.0001f);
        Assert.AreEqual(new Vector3(0.25f, 0.25f, 0.5f), weights);
        Assert.AreEqual(new Vector3(0.25f, 0.5f, 2), a * weights.X + b * weights.Y + c * weights.Z);
        Assert.IsFalse(MorphViewportPicking.IntersectTriangle(new Vector3(2, 2, 0), Vector3.UnitZ, a, b, c, out _, out _));
        Assert.IsFalse(MorphViewportPicking.IntersectTriangle(new Vector3(0.25f, 0.5f, 3), Vector3.UnitZ, a, b, c, out _, out _));
        Assert.IsFalse(MorphViewportPicking.IntersectTriangle(Vector3.Zero, Vector3.UnitZ, a, a, a, out _, out _));
    }

    [TestMethod]
    public void BoneSelectionBlendsTriangleWeightsAndIncludesParentsForFeatures()
    {
        var weights = MorphViewportPicking.BlendBoneWeights([(1, 1f)], [(2, 1f)], [(1, 0.5f), (2, 0.5f)],
            new Vector3(0.5f, 0.25f, 0.25f));
        Assert.AreEqual(0.625f, weights[1], 0.0001f);
        Assert.AreEqual(0.375f, weights[2], 0.0001f);
        MeshBone[] bones = [new() { Name = "root", ParentIndex = 0 }, new() { Name = "jaw", ParentIndex = 0 },
            new() { Name = "lip", ParentIndex = 1 }];
        var inherited = MorphViewportPicking.IncludeParentWeights(bones, weights);
        Assert.AreEqual(1f, inherited["root"], 0.0001f);
        Assert.AreEqual(1f, inherited["jaw"], 0.0001f);
        Assert.AreEqual(0.375f, inherited["lip"], 0.0001f);
    }

    [TestMethod]
    public void FeatureRankingUsesClickedRegionAndRetainsOpposingDisplacements()
    {
        MorphTarget.MorphVertex[] deltas = [new() { SourceIdx = 0, PositionDelta = Vector3.UnitX },
            new() { SourceIdx = 1, PositionDelta = -Vector3.UnitX }, new() { SourceIdx = 99, PositionDelta = Vector3.One * 1000 }];
        float strength = MorphViewportPicking.FeatureStrength(deltas, [], 0, 1, 2, new Vector3(0.5f, 0.5f, 0),
            new Dictionary<string, float>());
        Assert.AreEqual(1f, strength, 0.0001f);
        Assert.AreEqual(0f, MorphViewportPicking.FeatureStrength(deltas, [], 3, 4, 5, Vector3.UnitX, new Dictionary<string, float>()));
        Assert.AreEqual(2f, MorphViewportPicking.FeatureStrength([], [new() { Bone = "jaw", Offset = new Vector3(4, 0, 0) }],
            0, 1, 2, Vector3.UnitX, new Dictionary<string, float> { ["jaw"] = 0.5f }), 0.0001f);
    }

    [STATestMethod]
    public void SelectingFeatureOrBoneFiltersAndFocusesCategoryWithoutEditing()
    {
        using var editor = CreateEditor();
        var nose = new MorphFeatureEditorItem("NoseWidth", 0.2f, () => Assert.Fail("Picking must not edit the morph")) { HasMorphTarget = true };
        var mouth = new MorphFeatureEditorItem("MouthWidth", 0.4f, () => Assert.Fail("Picking must not edit the morph")) { HasMorphTarget = true };
        editor.MorphFeatureItems.Add(nose);
        editor.MorphFeatureItems.Add(mouth);
        editor.MorphEditorSearchText = "mouth";
        editor.SelectedMorphViewportMatch = new MorphViewportMatch { Mode = MorphViewportPickMode.Features, Feature = nose, TargetName = nose.Name };
        CollectionAssert.AreEqual(new[] { nose }, editor.MatchedMorphFeatureItems.ToArray());
        Assert.AreSame(editor.MorphFeaturesTab, editor.MorphEditorTabs.SelectedItem);
        Assert.IsNull(editor.MorphEditorSearchText);
        editor.FilterMorphViewportSelection = false;
        Assert.HasCount(2, editor.MatchedMorphFeatureItems.ToArray());
        editor.FilterMorphViewportSelection = true;

        var jaw = new MorphBoneEditorItem("jaw", Vector3.One, () => Assert.Fail("Picking must not edit the skeleton"));
        editor.MorphSkeletonItems.Add(jaw);
        editor.MorphSkeletonItems.Add(new MorphBoneEditorItem("head", Vector3.Zero, () => { }));
        editor.MorphViewportPickMode = MorphViewportPickMode.Skeleton;
        editor.SelectedMorphViewportMatch = new MorphViewportMatch { Mode = MorphViewportPickMode.Skeleton, Bone = jaw, TargetName = jaw.Name };
        Assert.AreSame(editor.MorphSkeletonTab, editor.MorphEditorTabs.SelectedItem);
        CollectionAssert.AreEqual(new[] { jaw }, editor.FilteredMorphSkeletonItems.ToArray());
        Assert.IsTrue(jaw.IsViewportSelected);
        Assert.IsFalse(editor.HasUnsavedMorphChanges);
        editor.SelectedMorphViewportMatch = null;
        Assert.IsFalse(jaw.IsViewportSelected);
        Assert.HasCount(2, editor.FilteredMorphSkeletonItems.ToArray());
    }

    [STATestMethod]
    public void MaterialSelectionFiltersByDefinitionsBeforeBroadcastMorphOverrides()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MorphMaterialPicking.pcc", MEGame.LE3);
        var export = package.CreateExport("Skin", "MaterialInstanceConstant", indexed: false);
        export.WriteProperty(new ArrayProperty<StructProperty>([
            new StructProperty("ScalarParameterValue", false, new NameProperty("Roughness", "ParameterName"), new FloatProperty(0.5f, "ParameterValue"))
        ], "ScalarParameterValues"));
        export.WriteProperty(new ArrayProperty<StructProperty>([
            new StructProperty("TextureParameterValue", false, new NameProperty("Diffuse", "ParameterName"), new ObjectProperty(0, "ParameterValue"))
        ], "TextureParameterValues"));
        var material = new MaterialRenderProxy(export);
        material.SetScalarParameter("Roughness", 0.7f);
        material.SetScalarParameter("HairShine", 0.9f);
        material.SetVectorParameter("HairTint", new LinearColor(1, 0, 0, 1));
        material.SetTextureParameter("HairDiffuse", "SomeTexture", null);
        Assert.IsTrue(material.DefinesScalarParameter("Roughness"));
        Assert.IsFalse(material.DefinesScalarParameter("HairShine"));
        Assert.IsFalse(material.DefinesVectorParameter("HairTint"));
        Assert.IsTrue(material.DefinesTextureParameter("Diffuse"));
        Assert.IsFalse(material.DefinesTextureParameter("HairDiffuse"));
        using var editor = CreateEditor();
        var roughness = new MorphScalarOverrideItem("Roughness", 0.7f, () => { });
        editor.MorphScalarOverrides.Add(roughness);
        editor.MorphScalarOverrides.Add(new MorphScalarOverrideItem("HairShine", 0.9f, () => { }));
        editor.MorphViewportPickMode = MorphViewportPickMode.Materials;
        editor.SelectedMorphViewportMatch = new MorphViewportMatch { Mode = MorphViewportPickMode.Materials, Material = material, TargetName = "Skin" };
        Assert.AreSame(editor.MorphMaterialsTab, editor.MorphEditorTabs.SelectedItem);
        CollectionAssert.AreEqual(new[] { roughness }, editor.FilteredMorphScalarOverrides.ToArray());
        Assert.IsFalse(editor.HasUnsavedMorphChanges);
    }

    private static BioMorphFaceEditor CreateEditor()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(BioMorphFaceEditor).Assembly);
        return new BioMorphFaceEditor();
    }
}
