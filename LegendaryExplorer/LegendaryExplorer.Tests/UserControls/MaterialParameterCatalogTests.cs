using System;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorer.UserControls.ExportLoaderControls.MaterialEditor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class MaterialParameterCatalogTests
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
    public void NamesComeFromTypedDefinitionsRatherThanThePackageNameTableOrExistingOverrides(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MaterialParameterDefinitions.pcc", game);
        var material = package.CreateExport("BaseMaterial", "Material", indexed: false);
        var scalar = CreateExpression(package, "Roughness", "MaterialExpressionScalarParameter");
        var vector = CreateExpression(package, "Tint", "MaterialExpressionVectorParameter");
        var texture = CreateExpression(package, "Diffuse", "MaterialExpressionTextureSampleParameter2D");
        var ordinaryTexture = CreateExpression(package, "NotAParameter", "MaterialExpressionTextureSample");
        SetExpressions(material, scalar, vector, texture, ordinaryTexture);
        var mic = CreateInstance(package, "Instance", material);
        mic.WriteProperty(new ArrayProperty<StructProperty>([
            new StructProperty("ScalarParameterValue", false, new NameProperty("InvalidOverride", "ParameterName"),
                new FloatProperty(0.5f, "ParameterValue"))
        ], "ScalarParameterValues"));
        package.FindNameOrAdd("AnUnrelatedName");
        int nameCount = package.NameCount;
        var originalData = package.Exports.ToDictionary(export => export, export => export.Data);
        using var cache = new PackageCache();

        CollectionAssert.AreEqual(new NameReference[] { "Roughness" },
            MaterialParameterCatalog.GetNames(mic, "ScalarParameterValues", cache).ToArray());
        CollectionAssert.AreEqual(new NameReference[] { "Tint" },
            MaterialParameterCatalog.GetNames(mic, "VectorParameterValues", cache).ToArray());
        CollectionAssert.AreEqual(new NameReference[] { "Diffuse" },
            MaterialParameterCatalog.GetNames(mic, "TextureParameterValues", cache).ToArray());
        Assert.AreEqual(nameCount, package.NameCount);
        foreach (var (export, data) in originalData)
            CollectionAssert.AreEqual(data, export.Data, "Reading the catalog must not change material data.");
    }

    [TestMethod]
    public void ResolvesExternalParentInstancesAndImportedExpressionsWithoutAddingTheirNamesToThePcc()
    {
        using var local = MEPackageHandler.CreateMemoryEmptyPackage("LocalMic.pcc", MEGame.LE3);
        using var external = MEPackageHandler.CreateMemoryEmptyPackage("ExternalMaterials.pcc", MEGame.LE3);
        using var definitions = MEPackageHandler.CreateMemoryEmptyPackage("ExternalDefinitions.pcc", MEGame.LE3);
        var baseMaterial = external.CreateExport("ExternalBase", "Material", indexed: false);
        var expression = CreateExpression(definitions, "ExternalDiffuse", "MaterialExpressionTextureSampleParameter2D");
        var importedExpression = CreateImport(external, "ImportedExpression", expression.ClassName);
        baseMaterial.WriteProperty(new ArrayProperty<ObjectProperty>([
            new ObjectProperty(importedExpression.UIndex)
        ], "Expressions"));
        var parentMic = CreateInstance(external, "ExternalInstance", baseMaterial);
        var importedParent = CreateImport(local, "ImportedParent", "MaterialInstanceConstant");
        var mic = CreateInstance(local, "LocalInstance", importedParent);
        Assert.IsFalse(local.Names.Contains("ExternalDiffuse"));
        int localNameCount = local.NameCount;

        var names = MaterialParameterCatalog.GetNames(mic, "TextureParameterValues", entry =>
        {
            if (ReferenceEquals(entry, importedParent)) return parentMic;
            if (ReferenceEquals(entry, importedExpression)) return expression;
            Assert.Fail($"Unexpected import {entry.InstancedFullPath}.");
            return null;
        });

        CollectionAssert.AreEqual(new NameReference[] { "ExternalDiffuse" }, names.ToArray());
        Assert.AreEqual(localNameCount, local.NameCount, "The dropdown must not eagerly add names to the current PCC.");
        Assert.IsFalse(local.Names.Contains("ExternalDiffuse"));
    }

    [TestMethod]
    public void TextureCatalogIncludesCubeAndSpecializedSampleParametersWithoutReadingTextures()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MaterialTextureDefinitions.pcc", MEGame.LE3);
        var material = package.CreateExport("Base", "Material", indexed: false);
        var cube = CreateExpression(package, "Cube", "MaterialExpressionTextureSampleParameterCube");
        var normal = CreateExpression(package, "Normal", "MaterialExpressionTextureSampleParameterNormal");
        var movie = CreateExpression(package, "Movie", "MaterialExpressionTextureSampleParameterMovie");
        foreach (var expression in new[] { cube, normal, movie })
            expression.WriteProperty(new ObjectProperty(int.MaxValue, "Texture"));
        SetExpressions(material, cube, normal, movie);

        CollectionAssert.AreEqual(new NameReference[] { "Cube", "Movie", "Normal" },
            MaterialParameterCatalog.GetNames(material, "TextureParameterValues", _ => null).ToArray());
    }

    [TestMethod]
    public void CaseInsensitiveDeduplicationPreservesExactFNameNumbersAndLiteralSuffixes()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MaterialParameterNames.pcc", MEGame.LE3);
        var material = package.CreateExport("Base", "Material", indexed: false);
        SetExpressions(material,
            CreateExpression(package, "Tint", "MaterialExpressionVectorParameter"),
            CreateExpression(package, "tint", "MaterialExpressionVectorParameter"),
            CreateExpression(package, new NameReference("Tint", 2), "MaterialExpressionVectorParameter"),
            CreateExpression(package, new NameReference("Tint_1", 0), "MaterialExpressionVectorParameter"));

        CollectionAssert.AreEqual(new[] { new NameReference("Tint"), new NameReference("Tint", 2), new NameReference("Tint_1") },
            MaterialParameterCatalog.GetNames(material, "VectorParameterValues", _ => null).ToArray());
    }

    [TestMethod]
    public void TraversesRvrMaterialUserAndReturnsAnEmptyListForANonParameterizedBase()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("RvrMaterialDefinitions.pcc", MEGame.LE3);
        var material = package.CreateExport("Base", "Material", indexed: false);
        var rvr = package.CreateExport("MaterialUser", "RvrEffectsMaterialUser", indexed: false);
        rvr.WriteProperty(new ObjectProperty(material, "m_pBaseMaterial"));
        var mic = CreateInstance(package, "Instance", rvr);

        Assert.IsEmpty(MaterialParameterCatalog.GetNames(mic, "TextureParameterValues", _ => null));
    }

    [TestMethod]
    public void MissingImportedParentsAndExpressionsReportWhichReferenceCouldNotBeResolved()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MissingMaterialDefinitions.pcc", MEGame.LE3);
        var importedParent = CreateImport(package, "MissingParent", "Material");
        var mic = CreateInstance(package, "Instance", importedParent);
        var parentError = Assert.ThrowsExactly<InvalidOperationException>(() =>
            MaterialParameterCatalog.GetNames(mic, "TextureParameterValues", _ => null));
        StringAssert.Contains(parentError.Message, "MissingParent");
        StringAssert.Contains(parentError.Message, "parent material");

        var material = package.CreateExport("Base", "Material", indexed: false);
        var importedExpression = CreateImport(package, "MissingExpression", "MaterialExpressionScalarParameter");
        material.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(importedExpression.UIndex)], "Expressions"));
        var expressionError = Assert.ThrowsExactly<InvalidOperationException>(() =>
            MaterialParameterCatalog.GetNames(material, "ScalarParameterValues", _ => null));
        StringAssert.Contains(expressionError.Message, "MissingExpression");
        StringAssert.Contains(expressionError.Message, "material expression");
    }

    [TestMethod]
    public void MissingUnrelatedImportedExpressionsDoNotHideValidParameterDefinitions()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MaterialUnrelatedImports.pcc", MEGame.LE3);
        var material = package.CreateExport("Base", "Material", indexed: false);
        var texture = CreateExpression(package, "Diffuse", "MaterialExpressionTextureSampleParameter2D");
        var nonParameter = CreateImport(package, "MissingNonParameter", "MaterialExpressionTextureSample");
        var scalar = CreateImport(package, "MissingScalar", "MaterialExpressionScalarParameter");
        material.WriteProperty(new ArrayProperty<ObjectProperty>([
            new ObjectProperty(texture.UIndex), new ObjectProperty(nonParameter.UIndex), new ObjectProperty(scalar.UIndex)
        ], "Expressions"));

        CollectionAssert.AreEqual(new NameReference[] { "Diffuse" },
            MaterialParameterCatalog.GetNames(material, "TextureParameterValues", entry =>
            {
                Assert.Fail($"Unrelated expression {entry.InstancedFullPath} must not be resolved.");
                return null;
            }).ToArray());
    }

    [TestMethod]
    public void ParentCyclesAndInvalidReferencesCannotProduceAnArbitraryParameterList()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("BrokenMaterialDefinitions.pcc", MEGame.LE3);
        var first = package.CreateExport("First", "MaterialInstanceConstant", indexed: false);
        var second = CreateInstance(package, "Second", first);
        first.WriteProperty(new ObjectProperty(second, "Parent"));
        var cycleError = Assert.ThrowsExactly<InvalidOperationException>(() =>
            MaterialParameterCatalog.GetNames(first, "ScalarParameterValues", _ => null));
        StringAssert.Contains(cycleError.Message, "cycle");

        first.WriteProperty(new ObjectProperty(int.MaxValue, "Parent"));
        var invalidError = Assert.ThrowsExactly<InvalidOperationException>(() =>
            MaterialParameterCatalog.GetNames(first, "ScalarParameterValues", _ => null));
        StringAssert.Contains(invalidError.Message, "invalid");

        first.WriteProperty(new ObjectProperty(0, "Parent"));
        var missingError = Assert.ThrowsExactly<InvalidOperationException>(() =>
            MaterialParameterCatalog.GetNames(first, "ScalarParameterValues", _ => null));
        StringAssert.Contains(missingError.Message, "no parent");
    }

    private static ExportEntry CreateExpression(IMEPackage package, NameReference parameterName, string className)
    {
        var expression = package.CreateExport("Expression", className);
        expression.WriteProperty(new NameProperty(parameterName, "ParameterName"));
        return expression;
    }

    private static ExportEntry CreateInstance(IMEPackage package, string name, IEntry parent)
    {
        var material = package.CreateExport(name, "MaterialInstanceConstant", indexed: false);
        material.WriteProperty(new ObjectProperty(parent, "Parent"));
        return material;
    }

    private static ImportEntry CreateImport(IMEPackage package, string name, string className)
    {
        var import = new ImportEntry(package) { ObjectName = name, ClassName = className, PackageFile = "Engine" };
        package.AddImport(import);
        return import;
    }

    private static void SetExpressions(ExportEntry material, params ExportEntry[] expressions) =>
        material.WriteProperty(new ArrayProperty<ObjectProperty>(expressions.Select(expression => new ObjectProperty(expression.UIndex)), "Expressions"));
}
