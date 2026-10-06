using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorer.UserControls.ExportLoaderControls.MaterialEditor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class CompiledMaterialParameterNamesTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow(MEGame.ME1)]
    [DataRow(MEGame.ME2)]
    public void LegacyMaterialResourcesSupplyNamesWithoutAnEditorGraph(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("LegacyParameters.pcc", game);
        var material = package.CreateExport("Material", "Material", indexed: false);
        var binary = Material.Create();
        binary.SM3MaterialResource.UniformPixelScalarExpressions =
        [
            Scalar("ScalarFromResource")
        ];
        binary.SM2MaterialResource.UniformCubeTextureExpressions =
        [
            Texture(new NameReference("Cube_2"))
        ];
        if (game == MEGame.ME1)
        {
            binary.SM3MaterialResource.Me1MaterialUniformExpressionsList =
            [
                new ME1MaterialUniformExpressionsElement
                {
                    UniformPixelScalarExpressions = [],
                    UniformPixelVectorExpressions = [Vector("VectorFromExtraGroup")],
                    Uniform2DTextureExpressions = [],
                    UniformCubeTextureExpressions = []
                }
            ];
        }
        material.WriteBinary(binary);

        CollectionAssert.AreEqual(new[] { "ScalarFromResource" },
            CompiledMaterialParameterNames.GetNames(material, "ScalarParameterValues").Select(name => name.Instanced).ToArray());
        var textureNames = CompiledMaterialParameterNames.GetNames(material, "TextureParameterValues");
        Assert.HasCount(1, textureNames);
        Assert.AreEqual(new NameReference("Cube_2"), textureNames[0], "Literal numeric suffixes must keep their original FName representation.");
        if (game == MEGame.ME1)
        {
            CollectionAssert.AreEqual(new[] { "VectorFromExtraGroup" },
                CompiledMaterialParameterNames.GetNames(material, "VectorParameterValues").Select(name => name.Instanced).ToArray());
        }
    }

    [TestMethod]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void CookedShaderMapsSupplyTypedPixelAndVertexParameterNames(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CookedParameters.pcc", game);
        var material = package.CreateExport("Material", "Material", indexed: false);
        var binary = Material.Create();
        binary.SM3MaterialResource.ID = Guid.NewGuid();
        material.WriteBinary(binary);
        WriteShaderCache(package, (StaticParameterSet)binary.SM3MaterialResource.ID,
            CreateShaderMap(binary.SM3MaterialResource.ID));

        CollectionAssert.AreEquivalent(new[] { "PixelScalar", "VertexScalar" },
            CompiledMaterialParameterNames.GetNames(material, "ScalarParameterValues").Select(name => name.Instanced).ToArray());
        CollectionAssert.AreEquivalent(new[] { "PixelVector", "VertexVector" },
            CompiledMaterialParameterNames.GetNames(material, "VectorParameterValues").Select(name => name.Instanced).ToArray());
        CollectionAssert.AreEquivalent(new[] { "Diffuse", "Reflection" },
            CompiledMaterialParameterNames.GetNames(material, "TextureParameterValues").Select(name => name.Instanced).ToArray());
    }

    [TestMethod]
    public void ShaderMapsCanBeLocatedInOtherResolvedPackages()
    {
        using var materialPackage = MEPackageHandler.CreateMemoryEmptyPackage("MaterialParameters.pcc", MEGame.LE3);
        using var sharedPackage = MEPackageHandler.CreateMemoryEmptyPackage("SharedParameterShaders.pcc", MEGame.LE3);
        using var cache = new PackageCache();
        cache.InsertIntoCache(sharedPackage);
        var material = materialPackage.CreateExport("Material", "Material", indexed: false);
        var binary = Material.Create();
        binary.SM3MaterialResource.ID = Guid.NewGuid();
        material.WriteBinary(binary);
        WriteShaderCache(sharedPackage, (StaticParameterSet)binary.SM3MaterialResource.ID,
            CreateShaderMap(binary.SM3MaterialResource.ID));

        CollectionAssert.AreEquivalent(new[] { "Diffuse", "Reflection" },
            CompiledMaterialParameterNames.GetNames(material, "TextureParameterValues", cache).Select(name => name.Instanced).ToArray());
    }

    [TestMethod]
    public void ImportedBaseMaterialsCanUseShaderMapsFromTheEditedMicPackage()
    {
        using var editedPackage = MEPackageHandler.CreateMemoryEmptyPackage("EditedMaterialInstance.pcc", MEGame.LE3);
        using var sourcePackage = MEPackageHandler.CreateMemoryEmptyPackage("ExternalBaseMaterial.pcc", MEGame.LE3);
        var material = sourcePackage.CreateExport("Material", "Material", indexed: false);
        var binary = Material.Create();
        binary.SM3MaterialResource.ID = Guid.NewGuid();
        material.WriteBinary(binary);
        WriteShaderCache(editedPackage, (StaticParameterSet)binary.SM3MaterialResource.ID,
            CreateShaderMap(binary.SM3MaterialResource.ID));

        CollectionAssert.AreEquivalent(new[] { "Diffuse", "Reflection" },
            CompiledMaterialParameterNames.GetNames(material, "TextureParameterValues", contextPackage: editedPackage)
                .Select(name => name.Instanced).ToArray());
    }

    [TestMethod]
    public void StaticPermutationShaderMapsSupplyNamesForMaterialInstances()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("StaticPermutationParameters.pcc", MEGame.LE3);
        var material = package.CreateExport("MaterialInstance", "MaterialInstanceConstant", indexed: false);
        material.WriteProperty(new BoolProperty(true, "bHasStaticPermutationResource"));
        var binary = MaterialInstance.Create();
        binary.SM3StaticParameterSet.BaseMaterialId = Guid.NewGuid();
        binary.SM3StaticPermutationResource.ID = binary.SM3StaticParameterSet.BaseMaterialId;
        material.WriteBinary(binary);
        WriteShaderCache(package, binary.SM3StaticParameterSet,
            CreateShaderMap(binary.SM3StaticParameterSet.BaseMaterialId));

        CollectionAssert.AreEquivalent(new[] { "Diffuse", "Reflection" },
            CompiledMaterialParameterNames.GetNames(material, "TextureParameterValues").Select(name => name.Instanced).ToArray());
    }

    [TestMethod]
    public void NestedUniformExpressionsPreserveNamesAndIgnoreCycles()
    {
        var literalName = new NameReference("Tiling_2");
        var instancedName = new NameReference("Scale", 3);
        var nested = new MaterialUniformExpressionClamp
        {
            Input = new MaterialUniformExpressionAbs { X = Scalar(literalName) },
            Min = new MaterialUniformExpressionFoldedMath { A = Scalar(instancedName), B = Vector("Color") },
            Max = Scalar(new NameReference("tiling_2"))
        };
        var cyclic = new MaterialUniformExpressionAbs();
        cyclic.X = cyclic;
        var names = new List<NameReference>();

        CompiledMaterialParameterNames.AddExpressions([nested, cyclic, null], "ScalarParameterValues", names);

        Assert.HasCount(2, names);
        Assert.IsTrue(names.Contains(literalName));
        Assert.IsTrue(names.Contains(instancedName));
        Assert.IsFalse(names.Any(name => name.Name == "Color"));
        Assert.IsTrue(names.Single(name => name.Name.Equals("Tiling_2", StringComparison.OrdinalIgnoreCase)).Number == 0);
    }

    [TestMethod]
    public void MissingMaterialBinaryAndUnsupportedParameterArraysAreSafe()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("IncompleteParameters.pcc", MEGame.LE3);
        var material = package.CreateExport("Material", "Material", indexed: false);

        Assert.HasCount(0, CompiledMaterialParameterNames.GetNames(material, "TextureParameterValues"));
        Assert.HasCount(0, CompiledMaterialParameterNames.GetNames(material, "OtherValues"));
    }

    [TestMethod]
    public void CompiledParametersCanDefineTheDefaultNoneName()
    {
        var names = new List<NameReference>();
        CompiledMaterialParameterNames.AddExpressions([Texture(NameReference.None)], "TextureParameterValues", names);
        CollectionAssert.AreEqual(new[] { NameReference.None }, names);
    }

    private static MaterialUniformExpressionScalarParameter Scalar(NameReference name) => new()
    {
        ParameterName = name,
        ExpressionType = "FMaterialUniformExpressionScalarParameter"
    };

    private static MaterialUniformExpressionVectorParameter Vector(NameReference name) => new()
    {
        ParameterName = name,
        ExpressionType = "FMaterialUniformExpressionVectorParameter"
    };

    private static MaterialUniformExpressionTextureParameter Texture(NameReference name) => new()
    {
        ParameterName = name,
        ExpressionType = "FMaterialUniformExpressionTextureParameter"
    };

    private static MaterialShaderMap CreateShaderMap(Guid materialId) => new()
    {
        ID = materialId,
        FriendlyName = "Test parameter shader map",
        StaticParameters = (StaticParameterSet)materialId,
        Shaders = [],
        MeshShaderMaps = [],
        UniformPixelScalarExpressions = [Scalar("PixelScalar")],
        UniformPixelVectorExpressions = [Vector("PixelVector")],
        UniformVertexScalarExpressions = [Scalar("VertexScalar")],
        UniformVertexVectorExpressions = [Vector("VertexVector")],
        Uniform2DTextureExpressions = [Texture("Diffuse")],
        UniformCubeTextureExpressions = [Texture("Reflection")]
    };

    private static void WriteShaderCache(IMEPackage package, StaticParameterSet parameterSet, MaterialShaderMap shaderMap)
    {
        var shaderCacheExport = package.CreateExport("SeekFreeShaderCache", "ShaderCache", indexed: false);
        var shaderCache = ShaderCache.Create();
        shaderCache.MaterialShaderMaps.Add(parameterSet, shaderMap);
        shaderCacheExport.WriteBinary(shaderCache);
    }
}
