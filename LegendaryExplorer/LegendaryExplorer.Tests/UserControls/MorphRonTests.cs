using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Save;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class MorphRonTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void ImportStagesEveryFieldAndExportsLiveEditsWithoutChangingThePackage()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("RonEditor.pcc", MEGame.LE3);
        ExportEntry morph = CreateMorph(package);
        using var editor = CreateEditor(morph);
        byte[] original = morph.Data.ToArray();
        int imports = package.ImportCount, exports = package.ExportCount;
        HeadMorph input = CreateRon();
        Invoke(editor, "ImportMorphRon", input);
        Assert.IsTrue(editor.HasUnsavedMorphChanges);
        Assert.AreEqual(imports, package.ImportCount);
        Assert.AreEqual(exports, package.ExportCount);
        CollectionAssert.AreEqual(original, morph.Data);

        editor.MorphScalarOverrides.Single().Value = 0.625f;
        editor.MorphSkeletonItems.Single().X = 4.5f;
        HeadMorph output = (HeadMorph)Invoke(editor, "CreateMorphRon");
        Assert.AreEqual(morph.GetProperty<ObjectProperty>("m_oHairMesh").ResolveToEntry(package).MemoryFullPath, output.HairMesh);
        CollectionAssert.AreEqual(input.AccessoryMeshes, output.AccessoryMeshes);
        Assert.AreEqual(input.TextureParameters["Diffuse"], output.TextureParameters["Diffuse"]);
        Assert.AreEqual(0.625f, output.ScalarParameters["Roughness"]);
        Assert.AreEqual(4.5f, output.OffsetBones["head"].X);
        Assert.AreEqual(input.VectorParameters["SkinTone"].B, output.VectorParameters["SkinTone"].B);
        CollectionAssert.AreEqual(input.Lod0Vertices, output.Lod0Vertices);
        CollectionAssert.AreEqual(input.Lod3Vertices, output.Lod3Vertices);
        Assert.AreEqual(output.ToRon(), HeadMorph.FromRon(output.ToRon()).ToRon());
        Assert.IsTrue(editor.HasUnsavedMorphChanges);
    }

    [STATestMethod]
    [DataRow(MEGame.ME1)]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void BothSaveTargetsKeepAllLodsAndAssetReferences(MEGame game)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("RonSave.pcc", game);
        ExportEntry morph = CreateMorph(package);
        using var editor = CreateEditor(morph);
        byte[] original = morph.Data.ToArray();
        HeadMorph input = CreateRon();
        input.HairMesh = "BIOG_Test.Hair.Style";
        string existingHair = morph.GetProperty<ObjectProperty>("m_oHairMesh").ResolveToEntry(package).MemoryFullPath;
        Invoke(editor, "ImportMorphRon", input);
        ExportEntry clone = EntryCloner.CloneTree(morph);
        Invoke(editor, "WriteMorphEditorValues", clone);
        CollectionAssert.AreEqual(original, morph.Data);
        AssertSaved(clone, input, existingHair);
        Invoke(editor, "WriteMorphEditorValues", morph);
        AssertSaved(morph, input, existingHair);
        Invoke(editor, "UnloadMorphEditor");
        using var cache = new PackageCache();
        Invoke(editor, "InitializeMorphEditor", morph, cache);
        HeadMorph reloaded = (HeadMorph)Invoke(editor, "CreateMorphRon");
        input.HairMesh = existingHair;
        // Unreal's name table is case-insensitive and may retain existing capitalization.
        Assert.AreEqual(input.ToRon(), reloaded.ToRon(), StringComparer.OrdinalIgnoreCase);
    }

    [STATestMethod]
    public void BadLaterLodOrAssetTypeLeavesExistingEditsAndPackageUntouched()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("RonInvalid.pcc", MEGame.LE3);
        ExportEntry morph = CreateMorph(package);
        using var editor = CreateEditor(morph);
        string original = ((HeadMorph)Invoke(editor, "CreateMorphRon")).ToRon();
        HeadMorph input = CreateRon();
        input.Lod3Vertices.Add(Vector3.One);
        Assert.IsInstanceOfType<InvalidDataException>(Assert.ThrowsExactly<TargetInvocationException>(() => Invoke(editor, "ImportMorphRon", input)).InnerException);
        Assert.AreEqual(original, ((HeadMorph)Invoke(editor, "CreateMorphRon")).ToRon());
        Assert.IsFalse(editor.HasUnsavedMorphChanges);

        input = CreateRon();
        input.TextureParameters["Diffuse"] = morph.MemoryFullPath;
        Assert.IsInstanceOfType<InvalidDataException>(Assert.ThrowsExactly<TargetInvocationException>(() => Invoke(editor, "ImportMorphRon", input)).InnerException);
        Assert.AreEqual(original, ((HeadMorph)Invoke(editor, "CreateMorphRon")).ToRon());
    }

    [STATestMethod]
    public void ReadOnlyAllowsExportButCannotImportAndNewLoadsClearStagedReferences()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("RonReadOnly.pcc", MEGame.LE3);
        ExportEntry morph = CreateMorph(package);
        using var editor = CreateEditor(morph);
        editor.IsMorphEditorReadOnly = true;
        Assert.IsFalse(editor.ImportMorphRonButton.IsEnabled);
        Assert.IsTrue(editor.ExportMorphRonButton.IsEnabled);
        Invoke(editor, "ImportMorphRon", CreateRon());
        Assert.IsFalse(editor.HasUnsavedMorphChanges);
        editor.IsMorphEditorReadOnly = false;
        Invoke(editor, "ImportMorphRon", CreateRon());
        Invoke(editor, "UnloadMorphEditor");
        using var cache = new PackageCache();
        Invoke(editor, "InitializeMorphEditor", morph, cache);
        HeadMorph result = (HeadMorph)Invoke(editor, "CreateMorphRon");
        Assert.AreEqual(morph.GetProperty<ObjectProperty>("m_oHairMesh").ResolveToEntry(package).MemoryFullPath, result.HairMesh);
        Assert.IsEmpty(result.AccessoryMeshes);
        Assert.IsEmpty(result.TextureParameters);
    }

    [STATestMethod]
    public void LocalMemoryPathsReuseExistingAssetsAndBaldinatorOverridesImportedHair()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("RonLocal.pcc", MEGame.LE3);
        ExportEntry morph = CreateMorph(package);
        ExportEntry texture = package.CreateExport("Texture", "Texture2D");
        using var editor = CreateEditor(morph);
        HeadMorph input = CreateRon();
        IEntry hair = morph.GetProperty<ObjectProperty>("m_oHairMesh").ResolveToEntry(package);
        input.HairMesh = hair.MemoryFullPath;
        input.AccessoryMeshes = [hair.MemoryFullPath];
        input.TextureParameters["Diffuse"] = texture.MemoryFullPath;
        Invoke(editor, "ImportMorphRon", input);
        Invoke(editor, "WriteMorphEditorValues", morph);
        Assert.IsFalse(package.Imports.Any(import => import.InstancedFullPath == hair.MemoryFullPath));
        Assert.IsFalse(package.Imports.Any(import => import.InstancedFullPath == texture.MemoryFullPath));
        Assert.AreEqual(hair.UIndex, morph.GetProperty<ObjectProperty>("m_oHairMesh").Value);
        Invoke(editor, "BaldinatorMorph_Click", null, new RoutedEventArgs());
        Assert.AreEqual("None", ((HeadMorph)Invoke(editor, "CreateMorphRon")).HairMesh);
        Invoke(editor, "WriteMorphEditorValues", morph);
        Assert.AreEqual(0, morph.GetProperty<ObjectProperty>("m_oHairMesh").Value);
    }

    private static void AssertSaved(ExportEntry morph, HeadMorph input, string expectedHair)
    {
        PropertyCollection props = morph.GetProperties();
        Assert.AreEqual(expectedHair, props.GetProp<ObjectProperty>("m_oHairMesh").ResolveToEntry(morph.FileRef).MemoryFullPath);
        var accessories = props.GetProp<ArrayProperty<ObjectProperty>>("m_oOtherMeshes");
        Assert.AreEqual(input.AccessoryMeshes[0], morph.FileRef.GetEntry(accessories[0].Value).MemoryFullPath);
        Assert.HasCount(4, ObjectBinary.From<BioMorphFace>(morph).LODs);
        CollectionAssert.AreEqual(input.Lod3Vertices.ToArray(), ObjectBinary.From<BioMorphFace>(morph).LODs[3]);
        var material = (ExportEntry)props.GetProp<ObjectProperty>("m_oMaterialOverrides").ResolveToEntry(morph.FileRef);
        var texture = material.GetProperty<ArrayProperty<StructProperty>>("m_aTextureOverrides").Single();
        Assert.AreEqual(input.TextureParameters["Diffuse"], morph.FileRef.GetEntry(texture.GetProp<ObjectProperty>("m_pTexture").Value).MemoryFullPath);
    }

    [STATestMethod]
    public void ResolvesSkinMaskFromNamedPackageWithoutEditingNpcAndPortsItWhenSaved()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MorphRon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string sourceFile = Path.Combine(directory, "RonSkinAssets.pcc");
            using (IMEPackage source = MEPackageHandler.CreateMemoryEmptyPackage(sourceFile, MEGame.LE3))
            {
                ExportEntry parent = source.CreateExport("Masks", "Package", indexed: false);
                ExportEntry mask = source.CreateExport("SkinMask", "Texture2D", parent, indexed: false);
                mask.WriteBinary(new UTexture2D { Mips = [], TextureGuid = Guid.NewGuid() });
                source.Save(sourceFile);
            }
            using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(directory, "BioNPC_Test.pcc"), MEGame.LE3);
            ExportEntry morph = CreateMorph(package);
            using var editor = CreateEditor(morph);
            using var cache = new PackageCache();
            const string path = "RonSkinAssets.Masks.SkinMask";
            var flags = package.Flags;
            int imports = package.ImportCount, exports = package.ExportCount;
            var resolved = (ExportEntry)Invoke(editor, "ResolveMorphRonAsset", path, "Texture2D", cache);
            Assert.IsNotNull(resolved, "The NPC's RequireImportsAlreadyLoaded flag must not prevent lookup of RON skin textures.");
            Assert.AreEqual(path, resolved.MemoryFullPath);
            Assert.AreEqual(flags, package.Flags);
            Assert.AreEqual(imports, package.ImportCount);
            Assert.AreEqual(exports, package.ExportCount);

            HeadMorph input = CreateRon();
            input.TextureParameters["Diffuse"] = path;
            Invoke(editor, "ImportMorphRon", input);
            Invoke(editor, "WriteMorphEditorValues", morph);
            var material = (ExportEntry)morph.GetProperty<ObjectProperty>("m_oMaterialOverrides").ResolveToEntry(package);
            var parameter = material.GetProperty<ArrayProperty<StructProperty>>("m_aTextureOverrides").Single();
            IEntry savedTexture = parameter.GetProp<ObjectProperty>("m_pTexture").ResolveToEntry(package);
            Assert.IsInstanceOfType<ExportEntry>(savedTexture, "Seek-free NPC packages need the texture copied in, not an unresolved runtime import.");
            Assert.AreEqual(path, savedTexture.InstancedFullPath);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow("MissingPackage.Mask", false, true)]
    [DataRow("ExistingPackage.Mask", true, false)]
    [DataRow("None", false, false)]
    [DataRow(null, false, false)]
    public void MissingRonTextureKeepsMaterialBaselineButExplicitNoneStillClearsIt(string path, bool loaded, bool keep)
        => Assert.AreEqual(keep, MeshRenderer.ShouldKeepMorphMaterialTexture(path, loaded));

    [STATestMethod]
    public void RetainedHairMeshKeepsItsTextureReferencesWhenRonUsesAnUnavailableCustomHairstyle()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("HairRon.pcc", MEGame.LE3);
        ExportEntry morph = CreateMorph(package);
        ExportEntry hairTexture = package.CreateExport("ExistingHairTexture", "Texture2D");
        using var editor = CreateEditor(morph);
        editor.MorphTextureOverrides.Add(new MorphTextureOverrideItem("HAIR_Diff", hairTexture.UIndex, () => { }));
        HeadMorph input = CreateRon();
        input.HairMesh = "MissingCustomHair.Style.Mesh";
        input.TextureParameters["HAIR_Diff"] = "MissingCustomHair.Style.Diffuse";
        input.TextureParameters["HAIR_Norm"] = "None";
        Invoke(editor, "ImportMorphRon", input);
        HeadMorph output = (HeadMorph)Invoke(editor, "CreateMorphRon");
        Assert.AreEqual(hairTexture.MemoryFullPath, output.TextureParameters["HAIR_Diff"]);
        Assert.IsFalse(output.TextureParameters.ContainsKey("HAIR_Norm"));
        Assert.AreEqual(input.VectorParameters["SkinTone"], output.VectorParameters["SkinTone"]);
        Invoke(editor, "WriteMorphEditorValues", morph);
        Assert.IsFalse(package.Imports.Any(import => import.InstancedFullPath.StartsWith("MissingCustomHair", StringComparison.OrdinalIgnoreCase)));
    }

    private static HeadMorph CreateRon() => new()
    {
        HairMesh = "None", AccessoryMeshes = ["BIOG_Test.Accessories.Visor"],
        MorphFeatures = new() { ["Nose"] = 0.3f }, OffsetBones = new() { ["head"] = new(1, 2, 3) },
        Lod0Vertices = [new(2, 3, 4)], Lod1Vertices = [new(5, 6, 7)], Lod2Vertices = [new(8, 9, 10)], Lod3Vertices = [new(11, 12, 13)],
        ScalarParameters = new() { ["Roughness"] = 0.5f }, VectorParameters = new() { ["SkinTone"] = new(0.1f, 0.2f, 0.3f, 1) },
        TextureParameters = new() { ["Diffuse"] = "BIOG_Test.Textures.Face_Diff" }
    };

    private static ExportEntry CreateMorph(IMEPackage package, string name = "Morph")
    {
        ExportEntry head = package.FindExport("Head") ?? package.CreateExport("Head", "SkeletalMesh");
        ExportEntry hair = package.FindExport("Hair") ?? package.CreateExport("Hair", "SkeletalMesh");
        ExportEntry morph = package.CreateExport(name, "BioMorphFace");
        morph.WritePropertiesAndBinary(new PropertyCollection { new ObjectProperty(head, "m_oBaseHead"), new ObjectProperty(hair, "m_oHairMesh") },
            new BioMorphFace { LODs = [[Vector3.Zero], [Vector3.Zero], [Vector3.Zero], [Vector3.Zero]] });
        return morph;
    }

    private static BioMorphFaceEditor CreateEditor(ExportEntry morph)
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, typeof(BioMorphFaceEditor).Assembly);
        var editor = new BioMorphFaceEditor();
        typeof(ExportLoaderControl).GetProperty(nameof(ExportLoaderControl.CurrentLoadedExport))!.SetValue(editor, morph);
        using var cache = new PackageCache();
        Invoke(editor, "InitializeMorphEditor", morph, cache);
        return editor;
    }

    private static object Invoke(MeshRenderer editor, string name, params object[] args) => typeof(MeshRenderer)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, args);
}
