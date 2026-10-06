using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.ExportLoaderControls.MaterialEditor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class GameTextureParameterChoicesTests
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
    public void ChoicesIncludeGameParametersAndOtherMaterialsBeyondTheSelectedParent(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("AllGameTextureChoices.pcc", game);
        var parent = package.CreateExport("Parent", "Material", indexed: false);
        var parentTexture = CreateExpression(package, "ParentDiffuse", "MaterialExpressionTextureSampleParameter2D");
        SetExpressions(parent, parentTexture);
        var mic = CreateInstance(package, "SelectedInstance", parent);
        var otherMaterial = package.CreateExport("OtherMaterial", "Material", indexed: false);
        SetExpressions(otherMaterial,
            CreateExpression(package, "OtherMaterialNormal", "MaterialExpressionTextureSampleParameter2D"));
        var otherMic = CreateInstance(package, "OtherInstance", otherMaterial);
        SetTextureOverrides(otherMic, "OtherInstanceMask");
        var gameNames = new NameReference[] { "ExternalGameTexture", "AnotherExternalGameTexture" };
        Assert.IsFalse(package.Names.Contains("ExternalGameTexture"));
        using var cache = new PackageCache();

        var choices = MaterialParameterCatalog.GetTextureChoices(mic, gameNames, cache);

        CollectionAssert.AreEquivalent(new NameReference[]
        {
            "ParentDiffuse", "OtherMaterialNormal", "OtherInstanceMask",
            "ExternalGameTexture", "AnotherExternalGameTexture"
        }, choices.ToArray());
        Assert.IsFalse(package.Names.Contains("ExternalGameTexture"));
        Assert.IsFalse(package.Names.Contains("AnotherExternalGameTexture"));
    }

    [TestMethod]
    public void GameChoiceAbsentFromThePccIsAddedOnlyWhenSelectedAndCommitted()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("DeferredGameTextureChoice.pcc", MEGame.LE3);
        var parent = package.CreateExport("Parent", "Material", indexed: false);
        SetExpressions(parent, CreateExpression(package, "CurrentTexture", "MaterialExpressionTextureSampleParameter2D"));
        var mic = CreateInstance(package, "Instance", parent);
        SetTextureOverrides(mic, "CurrentTexture");
        var replacement = new NameReference("TextureFromAnUnrelatedGameMaterial", 3);
        var node = CreateParameterNode(mic, "CurrentTexture");
        int initialNameCount = package.NameCount;
        var originalData = package.Exports.ToDictionary(export => export, export => export.Data);
        using var cache = new PackageCache();

        var choices = MaterialParameterCatalog.GetTextureChoices(mic, new[] { replacement }, cache);
        node.SetMaterialParameterNames(choices);

        Assert.IsTrue(node.MaterialParameterNames.Contains(replacement));
        Assert.AreEqual(initialNameCount, package.NameCount);
        Assert.AreEqual(-1, package.findName(replacement.Name));
        foreach (var (export, data) in originalData)
            CollectionAssert.AreEqual(data, export.Data, "Loading all-game choices must not rewrite package exports.");

        Assert.IsTrue(node.SelectMaterialParameterName(replacement));
        Assert.AreEqual(new NameReference("CurrentTexture"), ((NameProperty)node.Property).Value);
        Assert.AreEqual(initialNameCount, package.NameCount);
        Assert.AreEqual(-1, package.findName(replacement.Name));

        Assert.IsTrue(node.CommitInlineNameEdit());
        Assert.AreEqual(replacement, ((NameProperty)node.Property).Value);
        Assert.AreEqual(initialNameCount + 1, package.NameCount);
        Assert.IsTrue(package.findName(replacement.Name) >= 0);
    }

    [TestMethod]
    [DataRow(MEGame.ME1)]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void PackageScanReadsOnlyTextureParameterDefinitionsAndMaterialOverrides(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("TypedPackageTextureNames.pcc", game);
        CreateExpression(package, "Diffuse", "MaterialExpressionTextureSampleParameter2D");
        CreateExpression(package, "Cube", "MaterialExpressionTextureSampleParameterCube");
        CreateExpression(package, "TextureObject", "MaterialExpressionTextureObjectParameter");
        CreateExpression(package, "WrongScalarType", "MaterialExpressionScalarParameter");
        CreateExpression(package, "WrongVectorType", "MaterialExpressionVectorParameter");
        CreateExpression(package, "OrdinaryTextureSample", "MaterialExpressionTextureSample");
        CreateExpression(package, "TextureAssetName", "Texture2D");
        package.FindNameOrAdd("ArbitraryPackageName");
        var mic = package.CreateExport("Instance", "MaterialInstanceConstant", indexed: false);
        SetTextureOverrides(mic, "InstanceOverride");
        mic.WriteProperty(new ArrayProperty<StructProperty>([
            new StructProperty("ScalarParameterValue", false, new NameProperty("ScalarOverride", "ParameterName"),
                new FloatProperty(0.5f, "ParameterValue"))
        ], "ScalarParameterValues"));
        var unrelated = package.CreateExport("Unrelated", "Actor", indexed: false);
        SetTextureOverrides(unrelated, "UnrelatedObjectProperty");

        var names = MaterialParameterCatalog.GetPackageTextureNames(package);

        CollectionAssert.AreEquivalent(new NameReference[] { "Diffuse", "Cube", "TextureObject", "InstanceOverride" },
            names.ToArray());
    }

    [TestMethod]
    public void MergedChoicesDeduplicateFNamesWithoutCollapsingInstanceNumbersOrLiteralSuffixes()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ExactGameTextureNames.pcc", MEGame.LE3);
        var parent = package.CreateExport("Parent", "Material", indexed: false);
        var numbered = new NameReference("Diffuse", 3);
        var literal = new NameReference("Diffuse_2");
        Assert.AreEqual(numbered.Instanced, literal.Instanced);
        SetExpressions(parent,
            CreateExpression(package, "Diffuse", "MaterialExpressionTextureSampleParameter2D"),
            CreateExpression(package, numbered, "MaterialExpressionTextureSampleParameter2D"));
        var mic = CreateInstance(package, "Instance", parent);
        SetTextureOverrides(mic, literal);
        using var cache = new PackageCache();

        var names = MaterialParameterCatalog.GetTextureChoices(mic,
            new NameReference[] { "diffuse", numbered, literal, new("DIFFUSE", 3) }, cache);

        Assert.AreEqual(3, names.Count);
        Assert.IsTrue(names.Contains(new NameReference("Diffuse")));
        Assert.IsTrue(names.Contains(numbered));
        Assert.IsTrue(names.Contains(literal));
    }

    [TestMethod]
    public void UnresolvableParentDoesNotHideGlobalNamesOrExistingPackageTextureParameters()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("BrokenParentGameChoices.pcc", MEGame.LE3);
        var mic = package.CreateExport("Instance", "MaterialInstanceConstant", indexed: false);
        mic.WriteProperty(new ObjectProperty(int.MaxValue, "Parent"));
        SetTextureOverrides(mic, "ExistingTextureOverride");
        CreateExpression(package, "OtherPackageTextureDefinition", "MaterialExpressionTextureSampleParameter2D");
        package.FindNameOrAdd("UnrelatedPackageName");
        using var cache = new PackageCache();

        var names = MaterialParameterCatalog.GetTextureChoices(mic, new NameReference[] { "ExternalGameTexture" }, cache);

        CollectionAssert.AreEquivalent(new NameReference[]
        {
            "ExistingTextureOverride", "OtherPackageTextureDefinition", "ExternalGameTexture"
        }, names.ToArray());
    }

    [TestMethod]
    public void LoadedChoicesHideThePlaceholderOnlyWhenTheExactCurrentParameterIsKnown()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("TextureChoicePlaceholder.pcc", MEGame.LE3);
        var mic = package.CreateExport("Instance", "MaterialInstanceConstant", indexed: false);
        var current = new NameReference("Diffuse_2");
        var sameDisplayName = new NameReference("Diffuse", 3);
        var node = CreateParameterNode(mic, current);
        var notifications = new List<string>();
        node.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        Assert.IsFalse(node.HasMaterialParameterNamesLoaded);
        Assert.IsTrue(node.ShowMaterialParameterPlaceholder);
        Assert.AreEqual(current.Instanced, node.MaterialParameterDisplayName);

        node.SetMaterialParameterNames(new[] { sameDisplayName });

        Assert.IsTrue(node.HasMaterialParameterNamesLoaded);
        Assert.IsTrue(node.ShowMaterialParameterPlaceholder,
            "A different FName with the same visible text cannot count as the current parameter.");
        Assert.IsNull(node.SelectedMaterialParameterName);
        Assert.IsTrue(notifications.Contains(nameof(node.ShowMaterialParameterPlaceholder)));
        Assert.AreEqual(current, ((NameProperty)node.Property).Value);
        notifications.Clear();

        node.SetMaterialParameterNames(new[] { sameDisplayName, current });

        Assert.IsTrue(node.HasMaterialParameterNamesLoaded);
        Assert.IsFalse(node.ShowMaterialParameterPlaceholder);
        Assert.AreEqual(current, node.SelectedMaterialParameterName);
        Assert.IsTrue(notifications.Contains(nameof(node.ShowMaterialParameterPlaceholder)));
    }

    [TestMethod]
    public void CommittingAnExternalChoiceHidesThePlaceholderAndNotifiesItsDisplayBindings()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CommittedTextureChoicePlaceholder.pcc", MEGame.LE3);
        var mic = package.CreateExport("Instance", "MaterialInstanceConstant", indexed: false);
        var current = new NameReference("UnknownCurrentTexture");
        var external = new NameReference("ExternalTexture", 2);
        var node = CreateParameterNode(mic, current);
        node.SetMaterialParameterNames(new[] { external });
        var notifications = new List<string>();
        node.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        Assert.IsTrue(node.HasMaterialParameterNamesLoaded);
        Assert.IsTrue(node.ShowMaterialParameterPlaceholder);
        Assert.IsTrue(node.SelectMaterialParameterName(external));
        Assert.IsFalse(node.ShowMaterialParameterPlaceholder, "The dropdown must show the pending choice before confirmation.");
        Assert.AreEqual(external, node.SelectedMaterialParameterName);
        Assert.AreEqual(current.Instanced, node.MaterialParameterDisplayName);
        Assert.AreEqual(-1, package.findName(external.Name));

        Assert.IsTrue(node.CommitInlineNameEdit());

        Assert.IsTrue(node.HasMaterialParameterNamesLoaded);
        Assert.IsFalse(node.ShowMaterialParameterPlaceholder);
        Assert.AreEqual(external, node.SelectedMaterialParameterName);
        Assert.AreEqual(external.Instanced, node.MaterialParameterDisplayName);
        Assert.IsTrue(package.findName(external.Name) >= 0);
        Assert.IsTrue(notifications.Contains(nameof(node.ShowMaterialParameterPlaceholder)),
            "WPF must be told to remove the current-value overlay after the parameter is committed.");
        Assert.IsTrue(notifications.Contains(nameof(node.SelectedMaterialParameterName)));
        Assert.IsTrue(notifications.Contains(nameof(node.MaterialParameterDisplayName)));
    }

    private static ExportEntry CreateExpression(IMEPackage package, NameReference parameterName, string className)
    {
        var expression = package.CreateExport("Expression", className);
        // Some engine texture parameter classes are absent from the saved object-info database.
        // Give the fixture its actual class instead of leaving CreateExport's unresolved class null.
        if (expression.ClassName != className)
        {
            var classImport = new ImportEntry(package)
            {
                ObjectName = className, ClassName = "Class", PackageFile = "Engine"
            };
            package.AddImport(classImport);
            expression.Class = classImport;
        }
        Assert.AreEqual(className, expression.ClassName);
        expression.WriteProperty(new NameProperty(parameterName, "ParameterName"));
        return expression;
    }

    private static ExportEntry CreateInstance(IMEPackage package, string name, IEntry parent)
    {
        var mic = package.CreateExport(name, "MaterialInstanceConstant", indexed: false);
        mic.WriteProperty(new ObjectProperty(parent, "Parent"));
        return mic;
    }

    private static void SetExpressions(ExportEntry material, params ExportEntry[] expressions) =>
        material.WriteProperty(new ArrayProperty<ObjectProperty>(
            expressions.Select(expression => new ObjectProperty(expression.UIndex)), "Expressions"));

    private static void SetTextureOverrides(ExportEntry mic, params NameReference[] parameterNames) =>
        mic.WriteProperty(new ArrayProperty<StructProperty>(parameterNames.Select(name =>
            new StructProperty("TextureParameterValue", false, new NameProperty(name, "ParameterName"),
                new ObjectProperty(0, "ParameterValue"))), "TextureParameterValues"));

    private static UPropertyTreeViewEntry CreateParameterNode(ExportEntry owner, NameReference name)
    {
        var nameProperty = new NameProperty(name, "ParameterName");
        var parameter = new StructProperty("TextureParameterValue", new PropertyCollection { nameProperty });
        var array = new ArrayProperty<StructProperty>(new[] { parameter }, "TextureParameterValues");
        var arrayNode = new UPropertyTreeViewEntry(array) { AttachedExport = owner };
        var parameterNode = new UPropertyTreeViewEntry(parameter) { AttachedExport = owner, UPParent = arrayNode };
        var nameNode = new UPropertyTreeViewEntry(nameProperty) { AttachedExport = owner, UPParent = parameterNode };
        arrayNode.ChildrenProperties.Add(parameterNode);
        parameterNode.ChildrenProperties.Add(nameNode);
        return nameNode;
    }
}
