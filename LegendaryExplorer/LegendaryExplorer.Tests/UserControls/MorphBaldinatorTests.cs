using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Classes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BinaryMorph = LegendaryExplorerCore.Unreal.BinaryConverters.BioMorphFace;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class MorphBaldinatorTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void RemovesOnlyWeightedHairDisplacementsAndKeepsSeamDuplicatesTogether()
    {
        Vector3[][] source = [[new(10, 2, 3), new(10, 2, 3), new(5, 8, 9)], [new(4, 5, 6)], [Vector3.UnitZ]];
        var first = Target(Lod(3, 0, 1), Lod(1, 0));
        first.BoneOffsets = [new() { Bone = "head", Offset = Vector3.UnitZ }];
        var second = Target(Lod(3, 2));
        var result = BioMorphHair.RemoveHairMorphs(source,
            new Dictionary<string, float> { ["Afro"] = 0.5f, ["BuzzCut"] = 0.25f, ["Eastwood"] = 0.713f, ["Nose"] = 1 },
            new Dictionary<string, MorphTarget> { ["Afro"] = first, ["BuzzCut"] = second });
        Assert.AreEqual(new Vector3(9.5f, 2, 3), result.Lods[0][0]);
        Assert.AreEqual(result.Lods[0][0], result.Lods[0][1]);
        Assert.AreEqual(new Vector3(4.75f, 8, 9), result.Lods[0][2]);
        Assert.AreEqual(new Vector3(3.5f, 5, 6), result.Lods[1][0]);
        CollectionAssert.AreEqual(source[2], result.Lods[2]);
        Assert.AreEqual(-Vector3.UnitZ * 0.5f, result.BoneDeltas["head"]);
        Assert.AreEqual(new Vector3(10, 2, 3), source[0][0]);
    }

    [TestMethod]
    public void HenryEastwoodFacialSculptMustNotBeTreatedAsHair()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("HenryTest.pcc", MEGame.LE3);
        ExportEntry morph = CreateMorph(package, "HMM_HED_PROAverage_MDL");
        morph.WriteProperty(new ArrayProperty<StructProperty>([Feature("Eastwood", 0.713f), Feature("eyes_LidLower", 1)], "m_aMorphFeatures"));
        var result = BioMorphHair.MakeBald(morph, [[new Vector3(1, 2, 3)]]);
        Assert.IsFalse(BioMorphHair.IsHairFeature("Eastwood"));
        Assert.IsFalse(result.RemovedHairMorphs);
        Assert.AreEqual(new Vector3(1, 2, 3), result.Lods[0][0]);
        PropertyCollection properties = morph.GetProperties();
        BioMorphHair.ApplyProperties(morph, properties, result);
        CollectionAssert.AreEqual(new[] { "Eastwood", "eyes_LidLower" }, properties.GetProp<ArrayProperty<StructProperty>>("m_aMorphFeatures")
            .Select(f => f.GetProp<NameProperty>("sFeatureName").Value.Name).ToArray());
        Assert.AreEqual(0, properties.GetProp<ObjectProperty>("m_oHairMesh").Value);
    }

    [TestMethod]
    public void RejectsBadLaterLodWithoutPartiallyEditingInput()
    {
        Vector3[][] source = [[Vector3.Zero], [Vector3.Zero]];
        var weights = new Dictionary<string, float> { ["Afro"] = 1 };
        Assert.ThrowsExactly<InvalidDataException>(() => BioMorphHair.RemoveHairMorphs(source, weights,
            new Dictionary<string, MorphTarget> { ["Afro"] = Target(Lod(1, 0), Lod(2, 0)) }));
        Assert.AreEqual(Vector3.Zero, source[0][0]);
        Assert.ThrowsExactly<InvalidDataException>(() => BioMorphHair.RemoveHairMorphs(source, weights,
            new Dictionary<string, MorphTarget> { ["Afro"] = Target(Lod(1, 0), Lod(1, 2)) }));
    }

    [TestMethod]
    public void MissingTargetsAndEmptyGeometryCannotSilentlyRemoveActiveHairMetadata()
    {
        var weights = new Dictionary<string, float> { ["Afro"] = 1 };
        Assert.ThrowsExactly<InvalidDataException>(() => BioMorphHair.RemoveHairMorphs([[Vector3.One]], weights, new Dictionary<string, MorphTarget>()));
        Assert.ThrowsExactly<InvalidDataException>(() => BioMorphHair.RemoveHairMorphs([], weights,
            new Dictionary<string, MorphTarget> { ["Afro"] = Target(Lod(1, 0)) }));
    }

    [TestMethod]
    public void ApplyingRemovalKeepsFaceMetadataAndAdjustsOnlyHairBoneOffsets()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("HairPropertiesTest.pcc", MEGame.LE3);
        ExportEntry morph = CreateMorph(package, "HMM_HED_PROAverage_MDL");
        var result = new BioMorphHair.Result([], new Dictionary<string, Vector3> { ["head"] = Vector3.UnitY }, true);
        PropertyCollection properties = morph.GetProperties();
        properties.AddOrReplaceProp(new ArrayProperty<StructProperty>([Feature("aFrO", 1), Feature("Eastwood", 0.713f), Feature("Nose", 0.4f)], "m_aMorphFeatures"));
        properties.AddOrReplaceProp(new ArrayProperty<StructProperty>([new("OffsetBonePos", false,
            new NameProperty("head", "nName"), CommonStructs.Vector3Prop(new Vector3(1, 2, 3), "vPos"))], "m_aFinalSkeleton"));
        BioMorphHair.ApplyProperties(morph, properties, result);
        CollectionAssert.AreEqual(new[] { "Eastwood", "Nose" }, properties.GetProp<ArrayProperty<StructProperty>>("m_aMorphFeatures")
            .Select(f => f.GetProp<NameProperty>("sFeatureName").Value.Name).ToArray());
        Assert.AreEqual(new Vector3(1, 3, 3), CommonStructs.GetVector3(properties.GetProp<ArrayProperty<StructProperty>>("m_aFinalSkeleton")[0].GetProp<StructProperty>("vPos")));
    }

    [TestMethod]
    public void LiveWeightsTakePrecedenceOverSavedHairstyleAndMissingMetadataDoesNotCopyADonorHead()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("LiveHairTest.pcc", MEGame.LE3);
        ExportEntry morph = CreateMorph(package, "HMM_HED_PROAverage_MDL");
        var noMetadata = BioMorphHair.MakeBald(morph, [[Vector3.UnitZ]]);
        Assert.IsFalse(noMetadata.RemovedHairMorphs);
        Assert.AreEqual(Vector3.UnitZ, noMetadata.Lods[0][0]);
        morph.WriteProperty(new ArrayProperty<StructProperty>([Feature("Afro", 1)], "m_aMorphFeatures"));
        var result = BioMorphHair.MakeBald(morph, [[Vector3.UnitY]], new Dictionary<string, float> { ["Afro"] = 0 });
        Assert.IsFalse(result.RemovedHairMorphs);
        Assert.AreEqual(Vector3.UnitY, result.Lods[0][0]);
    }
    [TestMethod]
    [DataRow(MEGame.ME1, "HMF_HED_PROBase")]
    [DataRow(MEGame.ME2, "ASA_HED_PROBase")]
    [DataRow(MEGame.ME3, "TUR_HED_PROBase")]
    [DataRow(MEGame.LE1, "HMF_HED_PROBase")]
    [DataRow(MEGame.LE2, "ASA_HED_PROBase")]
    [DataRow(MEGame.LE3, "TUR_HED_PROBase")]
    public void OtherHeadsDoNotNeedMaleReferencePackagesOrChangeFaceGeometry(MEGame game, string headName)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("BaldTest.pcc", game);
        ExportEntry morph = CreateMorph(package, headName);
        byte[] original = morph.Data.ToArray();
        var result = BioMorphHair.MakeBald(morph, [[Vector3.UnitZ]]);
        Assert.IsFalse(result.RemovedHairMorphs);
        Assert.AreEqual(Vector3.UnitZ, result.Lods[0][0]);
        CollectionAssert.AreEqual(original, morph.Data);
    }

    [STATestMethod]
    public void ButtonStagesHairRemovalAndBothSaveTargetsUseIt()
    {
        SetWpfResources();
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("BaldEditorTest.pcc", MEGame.LE3);
        ExportEntry morph = CreateMorph(package, "HMM_HED_PROAverage_MDL");
        morph.WriteProperty(new ArrayProperty<StructProperty>([Feature("Eastwood", 0.713f)], "m_aMorphFeatures"));
        byte[] original = morph.Data.ToArray();
        using var editor = new BioMorphFaceEditor();
        SetExport(editor, morph);
        using var cache = new PackageCache();
        Invoke(editor, "InitializeMorphEditor", morph, cache);
        Vector3[][] normalDeltas = [[Vector3.UnitY]];
        typeof(MeshRenderer).GetField("WorkingMorphNormalDeltas", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, normalDeltas);
        Assert.IsTrue(editor.BaldinatorMorphButton.IsEnabled);
        editor.BaldinatorMorphButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsTrue(editor.HasUnsavedMorphChanges);
        editor.BaldinatorMorphButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreSame(normalDeltas, typeof(MeshRenderer).GetField("WorkingMorphNormalDeltas", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor));
        Assert.AreEqual("Eastwood", editor.MorphFeatureItems.Single().Name);
        CollectionAssert.AreEqual(original, morph.Data);

        ExportEntry clone = CreateMorph(package, "HMF_HED_PROBase", "NewMorph");
        Invoke(editor, "WriteMorphEditorValues", clone);
        Assert.AreEqual(0, clone.GetProperty<ObjectProperty>("m_oHairMesh").Value);
        Assert.AreEqual(0.713f, clone.GetProperty<ArrayProperty<StructProperty>>("m_aMorphFeatures").Single().GetProp<FloatProperty>("Offset").Value);
        CollectionAssert.AreEqual(original, morph.Data);
        Invoke(editor, "WriteMorphEditorValues", morph);
        Assert.AreEqual(0, morph.GetProperty<ObjectProperty>("m_oHairMesh").Value);
        Assert.AreEqual(Vector3.UnitZ, ObjectBinary.From<BinaryMorph>(morph).LODs[0][0]);

        Invoke(editor, "UnloadMorphEditor");
        ExportEntry fresh = CreateMorph(package, "HMF_HED_PROBase", "FreshMorph");
        SetExport(editor, fresh);
        Invoke(editor, "InitializeMorphEditor", fresh, cache);
        Invoke(editor, "WriteMorphEditorValues", fresh);
        Assert.AreNotEqual(0, fresh.GetProperty<ObjectProperty>("m_oHairMesh").Value);
    }

    [STATestMethod]
    public void ScalpReplacementSurvivesFurtherEditsAndPreservesEditedBones()
    {
        SetWpfResources();
        using var editor = new BioMorphFaceEditor();
        var bone = new MorphBoneEditorItem("head", Vector3.One, () => { });
        bone.X = 4;
        editor.MorphSkeletonItems.Add(bone);
        editor.MorphFeatureItems.Add(new MorphFeatureEditorItem("Afro", 1, () => { }));
        editor.MorphFeatureItems.Add(new MorphFeatureEditorItem("Nose", 0.4f, () => { }));
        Invoke(editor, "ApplyBaldMorph", new BioMorphHair.Result([[Vector3.UnitY]], new Dictionary<string, Vector3>(), true));
        Invoke(editor, "RecalculateMorphFromFeatures");
        var positions = (Vector3[][])typeof(MeshRenderer).GetField("WorkingMorphLods", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor);
        Assert.AreEqual(Vector3.UnitY, positions[0][0]);
        Assert.AreEqual(new Vector3(4, 1, 1), editor.MorphSkeletonItems.Single().Position);
        Assert.AreEqual("Nose", editor.MorphFeatureItems.Single().Name);
        Assert.AreEqual(0.4f, editor.MorphFeatureItems.Single().Value);
    }

    [STATestMethod]
    public void ReadOnlyPreviewCannotRunBaldinator()
    {
        SetWpfResources();
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("ReadOnlyBaldTest.pcc", MEGame.LE3);
        ExportEntry morph = CreateMorph(package, "HMF_HED_PROBase");
        using var editor = new BioMorphFaceEditor { IsMorphEditorReadOnly = true };
        SetExport(editor, morph);
        using var cache = new PackageCache();
        Invoke(editor, "InitializeMorphEditor", morph, cache);
        Assert.IsFalse(editor.BaldinatorMorphButton.IsEnabled);
        Invoke(editor, "BaldinatorMorph_Click", null, new RoutedEventArgs());
        Assert.IsFalse(editor.HasUnsavedMorphChanges);
        Assert.AreNotEqual(0, morph.GetProperty<ObjectProperty>("m_oHairMesh").Value);
    }

    private static ExportEntry CreateMorph(IMEPackage package, string headName, string name = "TestMorph")
    {
        ExportEntry head = package.CreateExport(headName, "SkeletalMesh");
        ExportEntry hair = package.CreateExport("Hair", "SkeletalMesh");
        ExportEntry morph = package.CreateExport(name, "BioMorphFace");
        morph.WritePropertiesAndBinary(new PropertyCollection
        {
            new ObjectProperty(head, "m_oBaseHead"), new ObjectProperty(hair, "m_oHairMesh")
        }, new BinaryMorph { LODs = [[Vector3.UnitZ]] });
        return morph;
    }

    private static MorphTarget Target(params MorphTarget.MorphLODModel[] lods) => new() { MorphLODModels = lods, BoneOffsets = [] };
    private static MorphTarget.MorphLODModel Lod(int count, params ushort[] indices) => new()
    {
        NumBaseMeshVerts = count, Vertices = indices.Select(index => new MorphTarget.MorphVertex { SourceIdx = index, PositionDelta = Vector3.UnitX }).ToArray()
    };
    private static StructProperty Feature(string name, float value) => new("MorphFeature", false,
        new NameProperty(name, "sFeatureName"), new FloatProperty(value, "Offset"));
    private static object Invoke(MeshRenderer editor, string name, params object[] args) => typeof(MeshRenderer)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, args);
    private static void SetExport(MeshRenderer editor, ExportEntry morph) => typeof(ExportLoaderControl)
        .GetProperty(nameof(ExportLoaderControl.CurrentLoadedExport))!.SetValue(editor, morph);
    private static void SetWpfResources() => typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
        .SetValue(null, typeof(BioMorphFaceEditor).Assembly);
}
