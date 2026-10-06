using System.Collections.Generic;
using System.Threading.Tasks;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class InterpreterTextureParameterTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void TextureActionsAreScopedToMicTextureParameterValues()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MicTextureScope.pcc", MEGame.LE3);
        var mic = package.CreateExport("MaterialInstance", "MaterialInstanceConstant", indexed: false);
        var material = package.CreateExport("Material", "Material", indexed: false);
        var texture = package.CreateExport("Diffuse", "Texture2D", indexed: false);

        Assert.IsTrue(CreateParameterNode(mic, texture.UIndex).ShowTextureFileActions);
        Assert.IsFalse(CreateParameterNode(material, texture.UIndex).ShowTextureFileActions);
        Assert.IsFalse(CreateParameterNode(mic, texture.UIndex, arrayName: "OtherValues").ShowTextureFileActions);
        Assert.IsFalse(CreateParameterNode(mic, texture.UIndex, structType: "OtherParameterValue").ShowTextureFileActions);
        Assert.IsFalse(CreateParameterNode(mic, texture.UIndex, propertyName: "OtherValue").ShowTextureFileActions);
        Assert.IsFalse(new UPropertyTreeViewEntry(new ObjectProperty(texture.UIndex, "ParameterValue"))
        {
            AttachedExport = mic
        }.ShowTextureFileActions);
    }

    [TestMethod]
    public void InvalidTextureReferencesKeepButtonsVisibleAndDisableActions()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MicInvalidTextures.pcc", MEGame.LE3);
        var mic = package.CreateExport("MaterialInstance", "MaterialInstanceConstant", indexed: false);
        var other = package.CreateExport("OtherObject", "Object", indexed: false);
        var defaultTexture = package.CreateExport("DefaultTexture", "Texture2D", indexed: false);
        defaultTexture.ObjectFlags |= UnrealFlags.EObjectFlags.ClassDefaultObject;

        foreach (int index in new[] { 0, int.MaxValue, int.MinValue, other.UIndex, defaultTexture.UIndex })
        {
            var node = CreateParameterNode(mic, index);
            Assert.IsTrue(node.ShowTextureFileActions, $"The parameter row should retain its buttons for index {index}.");
            Assert.IsFalse(node.CanExportTextureParameter, $"Index {index} is not an editable texture.");
            Assert.IsFalse(node.CanImportTextureParameter);
            Assert.IsFalse(node.CanMoveTextureParameterToTfc);
        }

        var importedTexture = new ImportEntry(package)
        {
            ObjectName = "ImportedDiffuse", ClassName = "Texture2D", PackageFile = "Engine"
        };
        package.AddImport(importedTexture);
        var importedNode = CreateParameterNode(mic, importedTexture.UIndex);
        Assert.IsTrue(importedNode.ShowTextureFileActions);
        Assert.IsTrue(importedNode.CanExportTextureParameter, "Imported textures can be resolved for export.");
        Assert.IsFalse(importedNode.CanImportTextureParameter);
        Assert.IsFalse(importedNode.CanMoveTextureParameterToTfc);
    }

    [TestMethod]
    [DataRow(MEGame.LE3, true)]
    [DataRow(MEGame.ME1, false)]
    public void LocalTexturesEnableSupportedActions(MEGame game, bool canMoveToTfc)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MicLocalTextures.pcc", game);
        var mic = package.CreateExport("MaterialInstance", "MaterialInstanceConstant", indexed: false);
        var texture = package.CreateExport("Diffuse", "Texture2D", indexed: false);
        var node = CreateParameterNode(mic, texture.UIndex);

        Assert.IsTrue(node.CanExportTextureParameter);
        Assert.IsTrue(node.CanImportTextureParameter);
        Assert.AreEqual(canMoveToTfc, node.CanMoveTextureParameterToTfc);
    }

    [TestMethod]
    public void UncommittedReferenceDisablesActionsAndCommitRefreshesEligibility()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MicTextureReferenceEdit.pcc", MEGame.LE3);
        var mic = package.CreateExport("MaterialInstance", "MaterialInstanceConstant", indexed: false);
        var original = package.CreateExport("Original", "Texture2D", indexed: false);
        var replacement = package.CreateExport("Replacement", "Texture2D", indexed: false);
        var node = CreateParameterNode(mic, original.UIndex);
        var notifications = new List<string>();
        node.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        Assert.AreSame(original, node.TextureParameterExport);

        node.InlineObjectIndexValue = replacement.UIndex.ToString();
        Assert.IsFalse(node.CanExportTextureParameter);
        Assert.IsFalse(node.CanImportTextureParameter);
        Assert.IsFalse(node.CanMoveTextureParameterToTfc);
        Assert.AreEqual(original.UIndex, ((ObjectProperty)node.Property).Value);

        ((ObjectProperty)node.Property).Value = replacement.UIndex;
        notifications.Clear();
        node.ResetInlineEditorValues();
        Assert.IsTrue(node.CanExportTextureParameter);
        Assert.IsTrue(node.CanImportTextureParameter);
        Assert.IsTrue(node.CanMoveTextureParameterToTfc);
        Assert.AreSame(replacement, node.TextureParameterExport, "Actions must target the newly confirmed texture.");
        StringAssert.Contains(node.InlineObjectDisplayValue, replacement.ObjectName.Name);
        CollectionAssert.IsSubsetOf(new[]
        {
            nameof(UPropertyTreeViewEntry.CanExportTextureParameter),
            nameof(UPropertyTreeViewEntry.CanImportTextureParameter),
            nameof(UPropertyTreeViewEntry.CanMoveTextureParameterToTfc)
        }, notifications);

        node.InlineObjectIndexValue = "invalid";
        Assert.IsFalse(node.CanExportTextureParameter);
        node.ResetInlineEditorValues();
        Assert.IsTrue(node.CanExportTextureParameter);
    }

    private static UPropertyTreeViewEntry CreateParameterNode(ExportEntry owner, int index,
        string arrayName = "TextureParameterValues", string structType = "TextureParameterValue",
        string propertyName = "ParameterValue")
    {
        var value = new ObjectProperty(index, propertyName);
        var parameter = new StructProperty(structType, new PropertyCollection { value });
        var array = new ArrayProperty<StructProperty>(new[] { parameter }, arrayName);
        var arrayNode = new UPropertyTreeViewEntry(array) { AttachedExport = owner };
        var parameterNode = new UPropertyTreeViewEntry(parameter) { AttachedExport = owner, UPParent = arrayNode };
        var valueNode = new UPropertyTreeViewEntry(value) { AttachedExport = owner, UPParent = parameterNode };
        arrayNode.ChildrenProperties.Add(parameterNode);
        parameterNode.ChildrenProperties.Add(valueNode);
        return valueNode;
    }
}
