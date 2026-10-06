using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class InterpreterMaterialParameterNameTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow("TextureParameterValues", "TextureParameterValue")]
    [DataRow("ScalarParameterValues", "ScalarParameterValue")]
    [DataRow("VectorParameterValues", "VectorParameterValue")]
    public void MicParameterNamesUseMaterialPicker(string arrayName, string structType)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MicParameterPickerScope.pcc", MEGame.LE3);
        var mic = package.CreateExport("MaterialInstance", "MaterialInstanceConstant", indexed: false);
        var node = CreateParameterNode(mic, new NameReference("Diffuse"), arrayName, structType);

        Assert.IsTrue(node.ShowMaterialParameterNamePicker);
        Assert.IsFalse(node.ShowStandardNamePicker);
        Assert.AreEqual(arrayName, node.MaterialParameterArrayName);
    }

    [TestMethod]
    public void UnrelatedNamesKeepTheStandardPicker()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("GenericParameterNameScope.pcc", MEGame.LE3);
        var mic = package.CreateExport("MaterialInstance", "MaterialInstanceConstant", indexed: false);
        var material = package.CreateExport("Material", "Material", indexed: false);
        var unrelatedNodes = new[]
        {
            CreateParameterNode(material, new NameReference("Diffuse")),
            CreateParameterNode(mic, new NameReference("Diffuse"), arrayName: "OtherValues"),
            CreateParameterNode(mic, new NameReference("Diffuse"), structType: "OtherParameterValue"),
            CreateParameterNode(mic, new NameReference("Diffuse"), propertyName: "OtherName"),
            new UPropertyTreeViewEntry(new NameProperty("Diffuse", "ParameterName")) { AttachedExport = mic }
        };

        foreach (var node in unrelatedNodes)
        {
            Assert.IsFalse(node.ShowMaterialParameterNamePicker);
            Assert.IsTrue(node.ShowStandardNamePicker);
        }
    }

    [TestMethod]
    public void ChoicesExcludeArbitraryPackageNamesAndInvalidCurrentNames()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MicParameterChoices.pcc", MEGame.LE3);
        var mic = package.CreateExport("MaterialInstance", "MaterialInstanceConstant", indexed: false);
        package.FindNameOrAdd("UnrelatedPackageName");
        var original = new NameReference("UnsupportedExistingParameter");
        var node = CreateParameterNode(mic, original);
        var supported = new[] { new NameReference("Normal"), new NameReference("Diffuse") };

        node.SetMaterialParameterNames(supported);

        CollectionAssert.AreEquivalent(supported, node.MaterialParameterNames.ToArray());
        Assert.IsFalse(node.MaterialParameterNames.Any(name => name.Name == "UnrelatedPackageName"));
        Assert.IsFalse(node.MaterialParameterNames.Contains(original));
        Assert.IsNull(node.SelectedMaterialParameterName);
        Assert.AreEqual(original, ((NameProperty)node.Property).Value,
            "Loading available choices must preserve an unsupported existing parameter.");
    }

    [TestMethod]
    public void ExternalParameterIsAddedToPackageOnlyWhenCommitted()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ExternalMicParameter.pcc", MEGame.LE3);
        var mic = package.CreateExport("MaterialInstance", "MaterialInstanceConstant", indexed: false);
        var original = new NameReference("OriginalParameter");
        var replacement = new NameReference("ExternalMaterialParameter");
        var node = CreateParameterNode(mic, original);
        node.SetMaterialParameterNames(new[] { replacement });
        int initialNameCount = package.Names.Count;

        Assert.AreEqual(-1, package.findName(replacement.Name));
        Assert.IsTrue(node.SelectMaterialParameterName(replacement));
        Assert.AreEqual(original, ((NameProperty)node.Property).Value);
        Assert.AreEqual(-1, package.findName(replacement.Name));
        Assert.AreEqual(initialNameCount, package.Names.Count);

        Assert.IsTrue(node.CommitInlineNameEdit());
        Assert.AreEqual(replacement, ((NameProperty)node.Property).Value);
        Assert.AreEqual(replacement, node.SelectedMaterialParameterName);
        Assert.IsTrue(package.findName(replacement.Name) >= 0);
        Assert.AreEqual(initialNameCount + 1, package.Names.Count);
    }

    [TestMethod]
    [DataRow("Texture_2", 0)]
    [DataRow("Texture", 3)]
    [DataRow("0", 0)]
    public void CommitPreservesTheExactSupportedNameReference(string name, int number)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ExactMicParameterName.pcc", MEGame.LE3);
        var mic = package.CreateExport("MaterialInstance", "MaterialInstanceConstant", indexed: false);
        var replacement = new NameReference(name, number);
        var node = CreateParameterNode(mic, new NameReference("OriginalParameter"));
        node.SetMaterialParameterNames(new[] { replacement });

        Assert.IsTrue(node.SelectMaterialParameterName(replacement));
        Assert.IsTrue(node.CommitInlineNameEdit());

        var committed = ((NameProperty)node.Property).Value;
        Assert.AreEqual(name, committed.Name);
        Assert.AreEqual(number, committed.Number);
        Assert.AreEqual(replacement, node.SelectedMaterialParameterName);
        Assert.IsTrue(package.findName(name) >= 0);
    }

    [TestMethod]
    public void EquivalentDisplayNamesRemainDistinctChoices()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("DistinctMicParameters.pcc", MEGame.LE3);
        var mic = package.CreateExport("MaterialInstance", "MaterialInstanceConstant", indexed: false);
        var literal = new NameReference("Texture_2");
        var numbered = new NameReference("Texture", 3);
        var node = CreateParameterNode(mic, literal);

        Assert.AreEqual(literal.Instanced, numbered.Instanced);
        node.SetMaterialParameterNames(new[] { literal, numbered, new NameReference("texture_2") });

        Assert.AreEqual(2, node.MaterialParameterNames.Count,
            "Case duplicates may be removed, but references with different base names and numbers must remain distinct.");
        Assert.IsTrue(node.MaterialParameterNames.Contains(literal));
        Assert.IsTrue(node.MaterialParameterNames.Contains(numbered));
        Assert.IsTrue(node.SelectMaterialParameterName(numbered));
        Assert.IsTrue(node.CommitInlineNameEdit());
        Assert.AreEqual(numbered, ((NameProperty)node.Property).Value);
    }

    [TestMethod]
    public void UnsupportedSelectionAndCommitLeavePropertyAndPackageUntouched()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("InvalidMicParameterSelection.pcc", MEGame.LE3);
        var mic = package.CreateExport("MaterialInstance", "MaterialInstanceConstant", indexed: false);
        var original = new NameReference("Diffuse");
        var node = CreateParameterNode(mic, original);
        node.SetMaterialParameterNames(new[] { original });
        int initialNameCount = package.Names.Count;

        Assert.IsFalse(node.SelectMaterialParameterName(new NameReference("UnrelatedName")));
        Assert.AreEqual(original.Name, node.InlineNameValue);
        Assert.AreEqual("0", node.InlineNameIndexValue);
        Assert.AreEqual(original, ((NameProperty)node.Property).Value);

        node.InlineNameValue = "UnrelatedName";
        Assert.IsFalse(node.CommitInlineNameEdit());
        Assert.AreEqual(original, ((NameProperty)node.Property).Value);
        Assert.AreEqual(initialNameCount, package.Names.Count);
        Assert.AreEqual(-1, package.findName("UnrelatedName"));

        node.InlineNameValue = original.Name;
        node.InlineNameIndexValue = "3";
        Assert.IsFalse(node.CommitInlineNameEdit(), "A different instance number is a different parameter.");
        Assert.AreEqual(original, ((NameProperty)node.Property).Value);
    }

    [TestMethod]
    public void EmptyCatalogCannotMutateAnExistingParameter()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("EmptyMicParameterChoices.pcc", MEGame.LE3);
        var mic = package.CreateExport("MaterialInstance", "MaterialInstanceConstant", indexed: false);
        var original = new NameReference("ExistingParameter");
        var node = CreateParameterNode(mic, original);
        node.SetMaterialParameterNames(System.Array.Empty<NameReference>());

        Assert.AreEqual(0, node.MaterialParameterNames.Count);
        Assert.IsNull(node.SelectedMaterialParameterName);
        Assert.IsFalse(node.SelectMaterialParameterName(original));
        Assert.IsFalse(node.CommitInlineNameEdit());
        Assert.AreEqual(original, ((NameProperty)node.Property).Value);
    }

    private static UPropertyTreeViewEntry CreateParameterNode(ExportEntry owner, NameReference name,
        string arrayName = "TextureParameterValues", string structType = "TextureParameterValue",
        string propertyName = "ParameterName")
    {
        var nameProperty = new NameProperty(name, propertyName);
        var parameter = new StructProperty(structType, new PropertyCollection { nameProperty });
        var array = new ArrayProperty<StructProperty>(new[] { parameter }, arrayName);
        var arrayNode = new UPropertyTreeViewEntry(array) { AttachedExport = owner };
        var parameterNode = new UPropertyTreeViewEntry(parameter) { AttachedExport = owner, UPParent = arrayNode };
        var nameNode = new UPropertyTreeViewEntry(nameProperty) { AttachedExport = owner, UPParent = parameterNode };
        arrayNode.ChildrenProperties.Add(parameterNode);
        parameterNode.ChildrenProperties.Add(nameNode);
        return nameNode;
    }
}
