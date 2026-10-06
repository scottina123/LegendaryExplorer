using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.UserControls.ExportLoaderControls.MaterialEditor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class GameTextureParameterCatalogTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void DatabaseCatalogIncludesParametersFromEveryMaterialAndBothDefinitionAndOverrideSettings()
    {
        var first = Material("First", new MatSetting("TextureSampleParameter2D", "Diffuse", null));
        var second = Material("Second", new MatSetting("TextureParameterValue", "TNT_Norm_Stack", null));
        var third = Material("Third", new MatSetting("TextureSampleParameterCube", "Environment", null));

        CollectionAssert.AreEqual(new NameReference[] { "Diffuse", "Environment", "TNT_Norm_Stack" },
            GameTextureParameterCatalog.GetNames(new[] { first, second, third }).ToArray());
    }

    [TestMethod]
    public void DatabaseCatalogExcludesUnrelatedPropertiesAndOrdinaryTextureSamples()
    {
        var record = Material("Material",
            new MatSetting("Texture", "ObjectProperty", "A_Texture"),
            new MatSetting("TextureSample", "NonParameter", null),
            new MatSetting("ScalarParameter", "Scalar", null),
            new MatSetting("TextureSampleParameter2D", "n/a", null),
            new MatSetting("TextureParameterValue", " ", null),
            new MatSetting("TextureSampleParameter2D", null, null),
            new MatSetting("TextureParameterValue", "Diffuse", null));

        CollectionAssert.AreEqual(new NameReference[] { "Diffuse" },
            GameTextureParameterCatalog.GetNames(new[] { record }).ToArray());
    }

    [TestMethod]
    public void DatabaseCatalogKeepsLiteralSuffixesAndExplicitNoneWhileDeduplicatingCaseInsensitively()
    {
        var record = Material("Material",
            new MatSetting("TextureParameterValue", "Texture_1", null),
            new MatSetting("TextureParameterValue", "texture_1", null),
            new MatSetting("TextureParameterValue", "None", null));

        var names = GameTextureParameterCatalog.GetNames(new[] { record });

        CollectionAssert.AreEqual(new[] { NameReference.None, new NameReference("Texture_1", 0) }, names.ToArray());
        Assert.IsTrue(names.All(name => name.Number == 0), "The AssetDB string schema does not preserve separate FName numbers.");
    }

    [TestMethod]
    [DataRow("TextureSampleParameterNormal")]
    [DataRow("TextureSampleParameterMovie")]
    [DataRow("TextureSampleParameterSubUV")]
    [DataRow("TextureSampleParameterMeshSubUV")]
    [DataRow("TextureSampleParameterMeshSubUVBlend")]
    [DataRow("TextureObjectParameter")]
    [DataRow("MaterialExpressionTextureSampleParameter2D")]
    public void DatabaseCatalogIncludesSpecializedTextureDefinitions(string settingName)
    {
        CollectionAssert.AreEqual(new NameReference[] { "SpecializedTexture" },
            GameTextureParameterCatalog.GetNames(new[] { Material("Material", new MatSetting(settingName, "SpecializedTexture", null)) }).ToArray());
    }

    [TestMethod]
    public void DatabaseCatalogHandlesMissingMaterialSettings()
    {
        Assert.IsEmpty(GameTextureParameterCatalog.GetNames((IEnumerable<MaterialRecord>)null));
        Assert.IsEmpty(GameTextureParameterCatalog.GetNames(new MaterialRecord[] { null, new() { MatSettings = null } }));
    }

    [TestMethod]
    public void PackageScanReadsIndependentMaterialsAndExactOverridesWithoutUsingTheNameTable()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("AllTextureParameters.pcc", MEGame.LE3);
        var first = package.CreateExport("FirstExpression", "MaterialExpressionTextureSampleParameter2D");
        first.WriteProperty(new NameProperty("Diffuse", "ParameterName"));
        var second = package.CreateExport("SecondExpression", "MaterialExpressionTextureSampleParameterCube");
        second.WriteProperty(new NameProperty("Environment", "ParameterName"));
        var ordinary = package.CreateExport("OrdinarySample", "MaterialExpressionTextureSample");
        ordinary.WriteProperty(new NameProperty("NotAParameter", "ParameterName"));
        var instance = package.CreateExport("IndependentInstance", "MaterialInstanceConstant");
        instance.WriteProperty(new ArrayProperty<StructProperty>([
            new StructProperty("TextureParameterValue", false, new NameProperty(new NameReference("Mask", 2), "ParameterName")),
            new StructProperty("TextureParameterValue", false, new NameProperty(new NameReference("Mask_1"), "ParameterName")),
            new StructProperty("TextureParameterValue", false, new NameProperty(NameReference.None, "ParameterName"))
        ], "TextureParameterValues"));
        package.FindNameOrAdd("AnUnrelatedName");
        int originalNameCount = package.NameCount;
        var originalData = package.Exports.ToDictionary(export => export, export => export.Data);

        CollectionAssert.AreEqual(new[] { new NameReference("Diffuse"), new NameReference("Environment"),
            new NameReference("Mask", 2), new NameReference("Mask_1"), NameReference.None },
            GameTextureParameterCatalog.GetNames(package).ToArray());
        Assert.AreEqual(originalNameCount, package.NameCount);
        foreach (var (export, data) in originalData)
            CollectionAssert.AreEqual(data, export.Data);
    }

    private static MaterialRecord Material(string name, params MatSetting[] settings) =>
        new(name, "AnyPackage", false, settings);

    [TestMethod]
    public void PackageScanReadsNestedTextureFNamesFromEveryCompiledExpressionArray()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("NestedTextureParameters.pcc", MEGame.LE3);
        var export = package.CreateExport("SeekFreeShaderCache", "ShaderCache", indexed: false);
        var shaderCache = ShaderCache.Create();
        Guid materialId = Guid.NewGuid();
        var expected = new[] { new NameReference("PixelScalar", 2), new NameReference("PixelVector_1"),
            new NameReference("VertexScalar", 4), new NameReference("VertexVector_3") };
        var map = new MaterialShaderMap
        {
            ID = materialId, FriendlyName = "Independent nested texture uniforms", StaticParameters = (StaticParameterSet)materialId,
            Shaders = [], MeshShaderMaps = [], Uniform2DTextureExpressions = [], UniformCubeTextureExpressions = [],
            UniformPixelScalarExpressions = [NestedTexture(expected[0])],
            UniformPixelVectorExpressions = [NestedTexture(expected[1])],
            UniformVertexScalarExpressions = [NestedTexture(expected[2])],
            UniformVertexVectorExpressions = [NestedTexture(expected[3])]
        };
        shaderCache.MaterialShaderMaps.Add((StaticParameterSet)materialId, map);
        export.WriteBinary(shaderCache);

        CollectionAssert.AreEquivalent(expected, GameTextureParameterCatalog.GetNames(package).ToArray());
    }

    private static MaterialUniformExpression NestedTexture(NameReference name) => new MaterialUniformExpressionAbs
    {
        ExpressionType = "FMaterialUniformExpressionAbs",
        X = new MaterialUniformExpressionTextureParameter
        {
            ExpressionType = "FMaterialUniformExpressionTextureParameter", ParameterName = name
        }
    };
}
