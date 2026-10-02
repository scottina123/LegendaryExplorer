using System;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class MaterialInstanceCloneTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow(MEGame.ME1)]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void CloningParametersCreatesIndependentTexturesWithoutChangingSharedMaterials(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CloneMaterial.pcc", game);
        var textureFolder = package.CreatePackageExport("Textures");
        var diffuse = CreateTexture(package, "Diffuse", textureFolder);
        var normal = CreateTexture(package, "Normal", textureFolder);
        var parent = package.CreateExport("ParentMaterial", "Material", indexed: false);
        var parentBinary = Material.Create();
        SetTextureReferences(parentBinary.SM3MaterialResource, diffuse.UIndex);
        SetTextureReferences(parentBinary.SM2MaterialResource, diffuse.UIndex);
        parent.WriteBinary(parentBinary);
        var material = CreateMaterial(package, "Material", diffuse.UIndex, normal.UIndex, diffuse.UIndex);
        material.ObjectName = new NameReference("Material", 7);
        material.WriteProperty(new ObjectProperty(parent, "Parent"));
        var otherMaterial = CreateMaterial(package, "OtherMaterial", diffuse.UIndex);
        var originalData = package.Exports.ToDictionary(entry => entry, entry => entry.Data);
        int originalExportCount = package.ExportCount;

        var clone = EntryCloner.CloneMaterialInstanceWithTextures(material, "custom");
        int[] clonedParameters = ParameterIndices(clone);

        Assert.AreEqual(originalExportCount + 3, package.ExportCount,
            "Repeated parameters must share one cloned texture within the cloned MIC.");
        Assert.AreEqual(new NameReference("Material_custom", 0), clone.ObjectName);
        Assert.AreEqual(new NameReference("Material", 7), material.ObjectName);
        Assert.AreEqual(clonedParameters[0], clonedParameters[2]);
        Assert.AreNotEqual(diffuse.UIndex, clonedParameters[0]);
        Assert.AreNotEqual(normal.UIndex, clonedParameters[1]);
        Assert.AreNotEqual(clonedParameters[0], clonedParameters[1]);
        Assert.AreEqual(parent.UIndex, clone.GetProperty<ObjectProperty>("Parent").Value);
        var clonedDiffuse = package.GetUExport(clonedParameters[0]);
        var clonedNormal = package.GetUExport(clonedParameters[1]);
        Assert.AreEqual("Diffuse_custom", clonedDiffuse.ObjectName.Name);
        Assert.AreEqual("Normal_custom", clonedNormal.ObjectName.Name);
        Assert.AreEqual(textureFolder.UIndex, clonedDiffuse.idxLink);
        Assert.AreEqual(textureFolder.UIndex, clonedNormal.idxLink);
        CollectionAssert.AreEqual(diffuse.GetBinaryData<UTexture2D>().Mips[0].Mip,
            clonedDiffuse.GetBinaryData<UTexture2D>().Mips[0].Mip);
        CollectionAssert.AreEqual(normal.GetBinaryData<UTexture2D>().Mips[0].Mip,
            clonedNormal.GetBinaryData<UTexture2D>().Mips[0].Mip);
        foreach (var (entry, data) in originalData)
        {
            CollectionAssert.AreEqual(data, entry.Data, $"Cloning must preserve {entry.InstancedFullPath}.");
        }
        CollectionAssert.AreEqual(new[] { diffuse.UIndex }, ParameterIndices(otherMaterial));

        clonedDiffuse.WriteProperty(new IntProperty(16, "SizeX"));
        Assert.AreEqual(1, diffuse.GetProperty<IntProperty>("SizeX").Value,
            "Editing a cloned texture must not change its source.");
    }

    [TestMethod]
    [DataRow("custom")]
    [DataRow("_custom")]
    public void SuffixClonesResolveNameCollisionsAndEachCloneGetsItsOwnTextures(string suffix)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CloneNames.pcc", MEGame.LE3);
        var textures = package.CreatePackageExport("Textures");
        var otherFolder = package.CreatePackageExport("OtherTextures");
        var source = CreateTexture(package, "Diffuse", textures);
        var existing = CreateTexture(package, "diffuse_custom", textures);
        CreateTexture(package, new NameReference("Diffuse_custom", 1), textures);
        CreateTexture(package, new NameReference("Diffuse_custom", 50), otherFolder);
        var material = CreateMaterial(package, "Material", source.UIndex);
        byte[] existingData = existing.Data;

        var first = EntryCloner.CloneMaterialInstanceWithTextures(material, suffix);
        var second = EntryCloner.CloneMaterialInstanceWithTextures(material, suffix);
        var firstTexture = package.GetUExport(ParameterIndices(first).Single());
        var secondTexture = package.GetUExport(ParameterIndices(second).Single());

        Assert.AreEqual(new NameReference("Material_custom", 0), first.ObjectName);
        Assert.AreEqual(new NameReference("Material_custom_2", 0), second.ObjectName);
        Assert.AreEqual("Diffuse_custom", firstTexture.ObjectName.Name, StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual(2, firstTexture.ObjectName.Number);
        Assert.AreEqual("Diffuse_custom", secondTexture.ObjectName.Name, StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual(3, secondTexture.ObjectName.Number);
        Assert.AreNotEqual(firstTexture.UIndex, secondTexture.UIndex);
        Assert.AreNotEqual(first.InstancedFullPath, second.InstancedFullPath);
        CollectionAssert.AreEqual(existingData, existing.Data);
        firstTexture.WriteProperty(new IntProperty(16, "SizeX"));
        Assert.AreEqual(1, secondTexture.GetProperty<IntProperty>("SizeX").Value);
        Assert.AreEqual(1, source.GetProperty<IntProperty>("SizeX").Value);
        CollectionAssert.AreEqual(new[] { source.UIndex }, ParameterIndices(material));
    }

    [TestMethod]
    public void BlankSuffixKeepsOriginalBaseNamesAndOrdinaryCloningKeepsSharedTextures()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CloneWithoutRename.pcc", MEGame.LE3);
        var texture = CreateTexture(package, "Diffuse");
        var material = CreateMaterial(package, "Material", texture.UIndex);
        material.ObjectName = new NameReference("Material", 7);
        var ordinaryClone = EntryCloner.CloneEntry(material);
        CollectionAssert.AreEqual(new[] { texture.UIndex }, ParameterIndices(ordinaryClone));
        var materialClone = EntryCloner.CloneMaterialInstance(material);
        CollectionAssert.AreEqual(new[] { texture.UIndex }, ParameterIndices(materialClone));

        var clone = EntryCloner.CloneMaterialInstanceWithTextures(material, "  ");
        var clonedTexture = package.GetUExport(ParameterIndices(clone).Single());

        Assert.AreEqual(material.ObjectName.Name, ordinaryClone.ObjectName.Name);
        Assert.IsGreaterThan(material.ObjectName.Number, ordinaryClone.ObjectName.Number);
        Assert.AreEqual(material.ObjectName.Name, materialClone.ObjectName.Name);
        Assert.IsGreaterThan(ordinaryClone.ObjectName.Number, materialClone.ObjectName.Number);
        Assert.AreEqual(material.ObjectName.Name, clone.ObjectName.Name);
        Assert.IsGreaterThan(materialClone.ObjectName.Number, clone.ObjectName.Number);
        Assert.AreEqual(texture.ObjectName.Name, clonedTexture.ObjectName.Name);
        Assert.IsGreaterThan(texture.ObjectName.Number, clonedTexture.ObjectName.Number);
        Assert.AreNotEqual(texture.InstancedFullPath, clonedTexture.InstancedFullPath);
        CollectionAssert.AreEqual(new[] { texture.UIndex }, ParameterIndices(material));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SuffixRenamesMaterialWithoutCloningSharedTextures(bool cloneTree)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CloneMaterialOnly.pcc", MEGame.LE3);
        var texture = CreateTexture(package, "Diffuse");
        var material = CreateMaterial(package, "Material", texture.UIndex);
        material.ObjectName = new NameReference("Material", 7);
        var child = package.CreateExport("ChildObject", "Object", material, indexed: false);
        child.WriteProperty(new ObjectProperty(texture, "Texture"));
        byte[] sourceData = material.Data;
        int originalExportCount = package.ExportCount;

        var clone = EntryCloner.CloneMaterialInstance(material, "_custom", cloneTree: cloneTree);

        Assert.AreEqual(new NameReference("Material_custom", 0), clone.ObjectName);
        Assert.AreEqual(new NameReference("Material", 7), material.ObjectName);
        Assert.AreEqual(originalExportCount + (cloneTree ? 2 : 1), package.ExportCount);
        CollectionAssert.AreEqual(new[] { texture.UIndex }, ParameterIndices(clone));
        CollectionAssert.AreEqual(sourceData, material.Data);
        if (cloneTree)
        {
            var clonedChild = package.Exports.Single(export => export.idxLink == clone.UIndex);
            Assert.AreEqual(texture.UIndex, clonedChild.GetProperty<ObjectProperty>("Texture").Value);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void SuffixedMaterialNameCollisionsUseUniqueBaseNamesAndKeepIndexZero(bool cloneTextures, bool cloneTree)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CloneMaterialNameCollisions.pcc", MEGame.LE3);
        var materialFolder = package.CreatePackageExport("Materials");
        var otherFolder = package.CreatePackageExport("OtherMaterials");
        var texture = CreateTexture(package, "Diffuse");
        var material = CreateMaterial(package, "Material", texture.UIndex);
        material.idxLink = materialFolder.UIndex;
        material.ObjectName = new NameReference("Material", 7);
        var existing = package.CreateExport("material_custom", "MaterialInstanceConstant", materialFolder, indexed: false);
        package.CreateExport("Material_custom_2", "MaterialInstanceConstant", materialFolder, indexed: false);
        package.CreateExport("Material_custom_3", "MaterialInstanceConstant", otherFolder, indexed: false);
        var existingName = existing.ObjectName;
        int originalExportCount = package.ExportCount;

        var first = EntryCloner.CloneMaterialInstance(material, "custom", cloneTextures, cloneTree);
        var second = EntryCloner.CloneMaterialInstance(material, "_custom", cloneTextures, cloneTree);

        Assert.AreEqual(new NameReference("Material_custom_3", 0), first.ObjectName);
        Assert.AreEqual(new NameReference("Material_custom_4", 0), second.ObjectName);
        Assert.AreEqual(materialFolder.UIndex, first.idxLink);
        Assert.AreEqual(materialFolder.UIndex, second.idxLink);
        Assert.AreSame(first, package.FindExport(first.InstancedFullPath));
        Assert.AreSame(second, package.FindExport(second.InstancedFullPath));
        Assert.AreEqual(existingName, existing.ObjectName);
        Assert.AreEqual(new NameReference("Material", 7), material.ObjectName);
        Assert.AreEqual(originalExportCount + (cloneTextures ? 4 : 2), package.ExportCount);
        if (cloneTextures)
        {
            Assert.AreNotEqual(texture.UIndex, ParameterIndices(first).Single());
            Assert.AreNotEqual(ParameterIndices(first).Single(), ParameterIndices(second).Single());
        }
        else
        {
            CollectionAssert.AreEqual(new[] { texture.UIndex }, ParameterIndices(first));
            CollectionAssert.AreEqual(new[] { texture.UIndex }, ParameterIndices(second));
        }
    }

    [TestMethod]
    public void OnlyValidTextureParameterExportsAreClonedAndOtherReferencesArePreserved()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CloneMixedReferences.pcc", MEGame.LE3);
        var texture = CreateTexture(package, "LocalTexture");
        var importedTexture = package.CreateImport("Texture2D", "ImportedTexture");
        var nonTexture = package.CreateExport("OtherObject", "Object", indexed: false);
        var material = CreateMaterial(package, "Material", texture.UIndex, 0, importedTexture.UIndex,
            nonTexture.UIndex, int.MaxValue, texture.UIndex);
        var parameters = material.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues");
        parameters.Add(new StructProperty("TextureParameterValue", new PropertyCollection
        {
            new NameProperty("MissingValue", "ParameterName")
        }));
        material.WriteProperty(parameters);
        byte[] originalData = material.Data;
        int originalExportCount = package.ExportCount;
        int originalImportCount = package.ImportCount;

        CollectionAssert.AreEqual(new IEntry[] { texture, importedTexture },
            EntryCloner.GetMaterialInstanceTextureReferences(material).ToArray());
        var clone = EntryCloner.CloneMaterialInstanceWithTextures(material, "custom");
        int[] clonedParameters = ParameterIndices(clone);

        Assert.AreEqual(originalExportCount + 2, package.ExportCount);
        Assert.AreEqual(originalImportCount, package.ImportCount);
        Assert.AreNotEqual(texture.UIndex, clonedParameters[0]);
        Assert.AreEqual(clonedParameters[0], clonedParameters[5]);
        CollectionAssert.AreEqual(new[] { 0, importedTexture.UIndex, nonTexture.UIndex, int.MaxValue },
            clonedParameters.Skip(1).Take(4).ToArray());
        Assert.AreEqual(int.MinValue, clonedParameters[6], "A missing ParameterValue must remain absent.");
        CollectionAssert.AreEqual(originalData, material.Data);
    }

    [TestMethod]
    [DataRow(MEGame.ME1)]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void StaticPermutationBinaryReferencesFollowClonedParameters(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CloneMaterialBinary.pcc", game);
        var texture = CreateTexture(package, "ParameterTexture");
        var sharedTexture = CreateTexture(package, "BinaryOnlyTexture");
        var material = CreateMaterial(package, "Material", texture.UIndex);
        material.WriteProperty(new BoolProperty(true, "bHasStaticPermutationResource"));
        var binary = MaterialInstance.Create();
        SetTextureReferences(binary.SM3StaticPermutationResource, texture.UIndex, sharedTexture.UIndex);
        SetTextureReferences(binary.SM2StaticPermutationResource, texture.UIndex, sharedTexture.UIndex);
        material.WriteBinary(binary);
        byte[] originalData = material.Data;

        var clone = EntryCloner.CloneMaterialInstanceWithTextures(material, "custom");
        int clonedTextureIndex = ParameterIndices(clone).Single();
        var clonedBinary = clone.GetBinaryData<MaterialInstance>();

        foreach (var resource in new[] { clonedBinary.SM3StaticPermutationResource, clonedBinary.SM2StaticPermutationResource })
        {
            CollectionAssert.AreEqual(new[] { clonedTextureIndex, sharedTexture.UIndex }, resource.UniformExpressionTextures);
            CollectionAssert.AreEqual(new[] { clonedTextureIndex, sharedTexture.UIndex },
                resource.TextureDependencyLengthMap.Select(pair => pair.Key).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 2 }, resource.TextureDependencyLengthMap.Select(pair => pair.Value).ToArray());
        }
        CollectionAssert.AreEqual(originalData, material.Data);
        Assert.AreEqual(1, package.Exports.Count(export => export.ObjectName.Name == sharedTexture.ObjectName.Name),
            "Textures outside TextureParameterValues must stay shared.");
    }

    [TestMethod]
    public void CloningAMaterialTreeReusesTexturesAlreadyClonedAsChildren()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CloneMaterialTree.pcc", MEGame.LE3);
        var material = CreateMaterial(package, "Material");
        var texture = CreateTexture(package, "ChildTexture", material);
        var child = package.CreateExport("ChildObject", "Object", material, indexed: false);
        child.WriteProperty(new ObjectProperty(texture, "Texture"));
        material.WriteProperty(new ArrayProperty<StructProperty>(new[] { Parameter(0, texture.UIndex) }, "TextureParameterValues"));
        var originalData = package.Exports.ToDictionary(entry => entry, entry => entry.Data);
        int originalExportCount = package.ExportCount;

        var clone = EntryCloner.CloneMaterialInstanceWithTextures(material, "custom", cloneTree: true);
        var clonedTexture = package.GetUExport(ParameterIndices(clone).Single());
        var clonedChild = package.Exports.Single(export => export.idxLink == clone.UIndex && export.ClassName == "Object");

        Assert.AreEqual(originalExportCount + 3, package.ExportCount);
        Assert.AreEqual(new NameReference("Material_custom", 0), clone.ObjectName);
        Assert.AreEqual(clone.UIndex, clonedTexture.idxLink);
        Assert.AreEqual("ChildTexture_custom", clonedTexture.ObjectName.Name);
        Assert.AreEqual(clonedTexture.UIndex, clonedChild.GetProperty<ObjectProperty>("Texture").Value);
        foreach (var (entry, data) in originalData)
        {
            CollectionAssert.AreEqual(data, entry.Data);
        }
    }

    [TestMethod]
    [DataRow(MEGame.ME1)]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void ClonedTextureReferencesAndMipDataSurvivePackageSaveAndReload(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CloneRoundTrip.pcc", game);
        var textures = package.CreatePackageExport("Textures");
        var localTexture = CreateTexture(package, "LocalTexture", textures);
        var externalTexture = CreateTexture(package, "ExternalTexture", textures);
        externalTexture.WriteProperty(new NameProperty("Textures_SharedCache", "TextureFileCacheName"));
        var externalBinary = externalTexture.GetBinaryData<UTexture2D>();
        externalBinary.Mips[0].StorageType = StorageTypes.extUnc;
        externalBinary.Mips[0].DataOffset = 4096;
        externalTexture.WriteBinary(externalBinary);
        var material = CreateMaterial(package, "Material", localTexture.UIndex, externalTexture.UIndex);
        material.ObjectName = new NameReference("Material", 7);
        var clone = EntryCloner.CloneMaterialInstanceWithTextures(material, "custom");
        int[] clonedIndices = ParameterIndices(clone);

        using var saved = package.SaveToStream(false);
        saved.Position = 0;
        using var reopened = MEPackageHandler.OpenMEPackageFromStream(saved);
        var reopenedMaterial = reopened.GetUExport(material.UIndex);
        var reopenedClone = reopened.GetUExport(clone.UIndex);
        Assert.AreEqual(new NameReference("Material", 7), reopenedMaterial.ObjectName);
        Assert.AreEqual(new NameReference("Material_custom", 0), reopenedClone.ObjectName);
        CollectionAssert.AreEqual(new[] { localTexture.UIndex, externalTexture.UIndex }, ParameterIndices(reopenedMaterial));
        CollectionAssert.AreEqual(clonedIndices, ParameterIndices(reopenedClone));
        var reopenedLocal = reopened.GetUExport(clonedIndices[0]);
        var reopenedExternal = reopened.GetUExport(clonedIndices[1]);
        Assert.AreEqual("LocalTexture_custom", reopenedLocal.ObjectName.Name);
        Assert.AreEqual("ExternalTexture_custom", reopenedExternal.ObjectName.Name);
        var reopenedLocalMip = reopenedLocal.GetBinaryData<UTexture2D>().Mips.Single();
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, reopenedLocalMip.Mip);
        Assert.AreEqual(StorageTypes.pccUnc, reopenedLocalMip.StorageType);
        Assert.AreEqual("Textures_SharedCache", reopenedExternal.GetProperty<NameProperty>("TextureFileCacheName").Value.Name);
        var reopenedExternalMip = reopenedExternal.GetBinaryData<UTexture2D>().Mips.Single();
        Assert.AreEqual(StorageTypes.extUnc, reopenedExternalMip.StorageType);
        Assert.AreEqual(4096, reopenedExternalMip.DataOffset,
            "Renaming the export must preserve its external texture storage reference.");
        if (game == MEGame.ME1)
        {
            Assert.AreEqual(reopenedLocal.DataOffset + reopenedLocal.propsEnd() + reopenedLocalMip.MipInfoOffsetFromBinStart + 16,
                reopenedLocalMip.DataOffset);
        }
    }

    [TestMethod]
    public void CubeFacesAreClonedOnceAndImportedFacesStayShared()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CloneCube.pcc", MEGame.LE3);
        var cube = package.CreateExport("Cube", "TextureCube", indexed: false);
        var localFace = CreateTexture(package, "LocalFace", cube);
        var importedFace = package.CreateImport("Texture2D", "ImportedFace");
        cube.WriteProperties(new PropertyCollection
        {
            new ObjectProperty(localFace, "FacePosX"),
            new ObjectProperty(localFace, "FaceNegX"),
            new ObjectProperty(importedFace, "FacePosY")
        });
        cube.WriteBinary(new UTextureCube { SourceArt = Array.Empty<byte>() });
        var material = CreateMaterial(package, "Material", cube.UIndex, localFace.UIndex);
        var originalData = package.Exports.ToDictionary(entry => entry, entry => entry.Data);
        int originalExportCount = package.ExportCount;

        var clone = EntryCloner.CloneMaterialInstanceWithTextures(material, "custom");
        int[] clonedParameters = ParameterIndices(clone);
        var clonedCube = package.GetUExport(clonedParameters[0]);
        var clonedFace = package.GetUExport(clonedParameters[1]);

        Assert.AreEqual(originalExportCount + 3, package.ExportCount,
            "A face referenced by the cube and a parameter must be cloned only once.");
        Assert.AreEqual("Cube_custom", clonedCube.ObjectName.Name);
        Assert.AreEqual("LocalFace_custom", clonedFace.ObjectName.Name);
        Assert.AreEqual(clonedCube.UIndex, clonedFace.idxLink);
        Assert.AreEqual(clonedFace.UIndex, clonedCube.GetProperty<ObjectProperty>("FacePosX").Value);
        Assert.AreEqual(clonedFace.UIndex, clonedCube.GetProperty<ObjectProperty>("FaceNegX").Value);
        Assert.AreEqual(importedFace.UIndex, clonedCube.GetProperty<ObjectProperty>("FacePosY").Value);
        foreach (var (entry, data) in originalData)
        {
            CollectionAssert.AreEqual(data, entry.Data);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ME1ExternalMipChildrenKeepTheirMasterParentAndReceiveUniqueNames(bool cloneTree)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CloneME1ExternalChild.pcc", MEGame.ME1);
        var material = CreateMaterial(package, "Material");
        var texture = CreateTexture(package, "ChildTexture", material);
        var binary = texture.GetBinaryData<UTexture2D>();
        binary.Mips[0].StorageType = StorageTypes.extLZO;
        binary.Mips[0].DataOffset = 4096;
        texture.WriteBinary(binary);
        material.WriteProperty(new ArrayProperty<StructProperty>(new[] { Parameter(0, texture.UIndex) }, "TextureParameterValues"));
        byte[] originalMaterialData = material.Data;
        byte[] originalTextureData = texture.Data;
        string originalTexturePath = texture.InstancedFullPath;
        int originalExportCount = package.ExportCount;

        var clone = EntryCloner.CloneMaterialInstanceWithTextures(material, cloneTree: cloneTree);
        var clonedTexture = package.GetUExport(ParameterIndices(clone).Single());

        Assert.AreNotSame(texture, clonedTexture,
            $"The cloned MIC must reference its cloned texture. Parameter {clonedTexture.UIndex}, source {texture.UIndex}, MIC {clone.UIndex}. " +
            string.Join("; ", package.Exports.Select(entry => $"{entry.UIndex}: {entry.InstancedFullPath} (parent {entry.idxLink})")));
        Assert.AreEqual(originalExportCount + 2, package.ExportCount);
        Assert.AreEqual(texture.idxLink, clonedTexture.idxLink,
            "ME1 uses the top-level parent to find the package holding externally stored mip data.");
        Assert.AreEqual(texture.ObjectName.Name, clonedTexture.ObjectName.Name);
        Assert.IsGreaterThan(texture.ObjectName.Number, clonedTexture.ObjectName.Number,
            $"Source {texture.UIndex}: {texture.InstancedFullPath}; clone {clonedTexture.UIndex}: {clonedTexture.InstancedFullPath}; MIC {clone.UIndex}. " +
            string.Join("; ", package.Exports.Select(entry => $"{entry.UIndex}: {entry.InstancedFullPath} (parent {entry.idxLink})")));
        Assert.AreNotEqual(originalTexturePath, clonedTexture.InstancedFullPath);
        Assert.AreSame(texture, package.FindExport(originalTexturePath));
        Assert.AreSame(clonedTexture, package.FindExport(clonedTexture.InstancedFullPath));
        Assert.AreEqual(1, package.Exports.Count(entry => entry.InstancedFullPath.Equals(clonedTexture.InstancedFullPath,
            StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(StorageTypes.extLZO, clonedTexture.GetBinaryData<UTexture2D>().Mips.Single().StorageType);
        Assert.AreEqual(4096, clonedTexture.GetBinaryData<UTexture2D>().Mips.Single().DataOffset);
        CollectionAssert.AreEqual(originalMaterialData, material.Data);
        CollectionAssert.AreEqual(originalTextureData, texture.Data);
    }

    private static ExportEntry CreateTexture(IMEPackage package, NameReference name, IEntry parent = null)
    {
        var texture = package.CreateExport(name, "Texture2D", parent, indexed: false);
        texture.WriteProperties(new PropertyCollection
        {
            new IntProperty(1, "SizeX"),
            new IntProperty(1, "SizeY")
        });
        var binary = UTexture2D.Create();
        binary.Mips.Add(new UTexture2D.Texture2DMipMap(new byte[] { 1, 2, 3, 4 }, 1, 1));
        texture.WriteBinary(binary);
        return texture;
    }

    private static ExportEntry CreateMaterial(IMEPackage package, string name, params int[] textureIndices)
    {
        var material = package.CreateExport(name, "MaterialInstanceConstant", indexed: false);
        material.WriteProperty(new ArrayProperty<StructProperty>(textureIndices.Select((index, position) => Parameter(position, index)),
            "TextureParameterValues"));
        return material;
    }

    private static StructProperty Parameter(int position, int textureIndex) => new("TextureParameterValue", new PropertyCollection
    {
        new NameProperty($"Parameter{position}", "ParameterName"),
        new ObjectProperty(textureIndex, "ParameterValue"),
        StructProperty.FromGuid(Guid.Empty, "ExpressionGUID")
    });

    private static int[] ParameterIndices(ExportEntry material) => material.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues")
        .Select(parameter => parameter.GetProp<ObjectProperty>("ParameterValue")?.Value ?? int.MinValue).ToArray();

    private static void SetTextureReferences(MaterialResource resource, params int[] indices)
    {
        resource.UniformExpressionTextures = indices;
        resource.Uniform2DTextureExpressions = indices.Select(index => new MaterialUniformExpressionTexture
        {
            ExpressionType = "FMaterialUniformExpressionTexture",
            TextureIndex = index
        }).ToArray();
        for (int position = 0; position < indices.Length; position++)
        {
            resource.TextureDependencyLengthMap.Add(indices[position], position + 1);
        }
    }
}
