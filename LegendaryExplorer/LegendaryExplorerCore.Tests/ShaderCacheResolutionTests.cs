using System;
using System.Threading.Tasks;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class ShaderCacheResolutionTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow("Material")]
    [DataRow("DecalMaterial")]
    public void CachedLookupRetainsParticleFactoriesAndMaterialShaders(string materialClass)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ShaderFactoryResolution.pcc", MEGame.LE3);
        var material = package.CreateExport("Material", materialClass, indexed: false);
        var binary = Material.Create();
        material.WriteBinary(binary);

        var localShader = new UnparsedShader { Guid = Guid.NewGuid() };
        var particleShader = new UnparsedShader { Guid = Guid.NewGuid() };
        var materialShader = new UnparsedShader { Guid = Guid.NewGuid() };
        var map = new MaterialShaderMap
        {
            Shaders = [],
            MeshShaderMaps =
            [
                new MeshShaderMap { VertexFactoryType = "FLocalVertexFactory", Shaders = [] },
                new MeshShaderMap { VertexFactoryType = "FParticleSpriteVertexFactory", Shaders = [] }
            ]
        };
        map.MeshShaderMaps[0].Shaders.Add("VertexShader", new ShaderReference { Id = localShader.Guid });
        map.MeshShaderMaps[1].Shaders.Add("VertexShader", new ShaderReference { Id = particleShader.Guid });
        map.Shaders.Add("FLightFunctionPixelShader", new ShaderReference { Id = materialShader.Guid });
        var cache = ShaderCache.Create();
        cache.MaterialShaderMaps.Add((StaticParameterSet)binary.SM3MaterialResource.ID, map);
        cache.Shaders.Add(localShader.Guid, localShader);
        cache.Shaders.Add(particleShader.Guid, particleShader);
        cache.Shaders.Add(materialShader.Guid, materialShader);
        int cacheCalls = 0;
        ShaderCache GetCache(IMEPackage requestedPackage)
        {
            Assert.AreSame(package, requestedPackage);
            cacheCalls++;
            return cache;
        }

        var local = ShaderCacheManipulator.GetMaterialShaderMapAndShaders(material, GetCache, "VertexShader");
        var particle = ShaderCacheManipulator.GetMaterialShaderMapAndShadersForVertexFactory(
            material, "FParticleSpriteVertexFactory", GetCache, "VertexShader");
        var lightFunction = ShaderCacheManipulator.GetMaterialShaderMapAndMaterialShaders(
            material, GetCache, "FLightFunctionPixelShader");
        var missingFactory = ShaderCacheManipulator.GetMaterialShaderMapAndShadersForVertexFactory(
            material, "MissingVertexFactory", GetCache, "VertexShader");

        Assert.AreSame(map, local.Item1);
        Assert.AreSame(localShader, local.Item2[0]);
        Assert.AreSame(particleShader, particle.Item2[0]);
        Assert.AreSame(materialShader, lightFunction.Item2[0]);
        Assert.IsNull(missingFactory.Item2[0]);
        Assert.AreEqual(4, cacheCalls);
    }
}
