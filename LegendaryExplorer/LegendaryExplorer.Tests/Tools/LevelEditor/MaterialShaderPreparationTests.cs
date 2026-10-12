using System;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class MaterialShaderPreparationTests
{
    [DataTestMethod]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void RepeatedMaterialsShareTheParsedPackageShaders(MEGame game)
    {
        using var fixture = new MaterialFixture(game);
        var first = new MaterialRenderProxy(fixture.Context, fixture.Material);
        var second = new MaterialRenderProxy(fixture.Context, fixture.Material);
        ShaderCache parsedCache = fixture.Context.GetSeekFreeShaderCache(fixture.Material.FileRef);

        Assert.IsTrue(first.CanRenderWithGameShaders, first.GameShaderError);
        Assert.IsTrue(second.CanRenderWithGameShaders, second.GameShaderError);
        Assert.AreSame(parsedCache.Shaders[fixture.PreviewVertexId], first.UnrealVertexShader);
        Assert.AreSame(first.UnrealVertexShader, second.UnrealVertexShader,
            "Repeated mesh instances must reuse the parsed package shader rather than reparsing its cache.");
        Assert.AreSame(first.UnrealPixelShader, second.UnrealPixelShader);
    }

    [DataTestMethod]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void LightmappedPreviewIsReadyWithItsAuthoredPolicyAtConstruction(MEGame game)
    {
        using var fixture = new MaterialFixture(game);
        var preview = new LEShaderPreviewMaterial(fixture.Context, fixture.Material,
            lightMapType: ELightMapType.LMT_2D);
        MaterialRenderProxy material = preview.RenderProxy;

        Assert.IsTrue(material.CanRenderWithGameShaders, material.GameShaderError);
        Assert.IsTrue(material.UsesLightMap,
            "A mesh's initial shader preparation must use its actual lightmap policy.");
        Assert.AreEqual(ELightMapType.LMT_2D, material.LightMapType);
        Assert.AreEqual(fixture.LightMapVertexId, material.UnrealVertexShader.Guid);
        Assert.AreEqual(fixture.LightMapPixelId, material.UnrealPixelShader.Guid);
        // Direct consumers retain the existing eager, no-lightmap behavior.
        var direct = new MaterialRenderProxy(fixture.Context, fixture.Material);
        Assert.IsTrue(direct.CanRenderWithGameShaders, direct.GameShaderError);
        Assert.IsFalse(direct.UsesLightMap);
        Assert.AreEqual(fixture.PreviewVertexId, direct.UnrealVertexShader.Guid);
    }

    private sealed class MaterialFixture : IDisposable
    {
        private readonly IMEPackage package;
        public MeshRenderContext Context { get; } = new();
        public ExportEntry Material { get; }
        public Guid PreviewVertexId { get; } = Guid.NewGuid();
        public Guid LightMapVertexId { get; } = Guid.NewGuid();
        public Guid LightMapPixelId { get; } = Guid.NewGuid();

        public MaterialFixture(MEGame game)
        {
            LegendaryExplorerCore.LegendaryExplorerCoreLib.InitLib(System.Threading.Tasks.TaskScheduler.Default);
            package = MEPackageHandler.CreateMemoryEmptyPackage("MaterialShaderPreparation.pcc", game);
            Material = package.CreateExport("Terrain", "Material", indexed: false);
            var binary = LegendaryExplorerCore.Unreal.BinaryConverters.Material.Create();
            binary.SM3MaterialResource.ID = Guid.NewGuid();
            Material.WriteBinary(binary);

            var map = new MaterialShaderMap
            {
                ID = binary.SM3MaterialResource.ID,
                FriendlyName = "Terrain shader preparation",
                StaticParameters = (StaticParameterSet)binary.SM3MaterialResource.ID,
                Shaders = [],
                MeshShaderMaps = [new MeshShaderMap { VertexFactoryType = "FLocalVertexFactory", Shaders = [] }],
                UniformPixelScalarExpressions = [],
                UniformPixelVectorExpressions = [],
                UniformVertexScalarExpressions = [],
                UniformVertexVectorExpressions = [],
                Uniform2DTextureExpressions = [],
                UniformCubeTextureExpressions = []
            };
            var cache = ShaderCache.Create();
            cache.MaterialShaderMaps.Add(map.StaticParameters, map);
            AddShader(new TBasePassVertexShader<FNullPolicy, FNullPolicy>
            {
                Guid = PreviewVertexId,
                ShaderType = "TBasePassVertexShaderFNoLightMapPolicyFNoDensityPolicy",
                VertexFactoryParameters = LocalFactory(),
                MaterialParameters = VertexParameters()
            });
            AddShader(new TBasePassPixelShader<FNullPolicy>
            {
                Guid = Guid.NewGuid(),
                ShaderType = "TBasePassPixelShaderFNoLightMapPolicySkyLight",
                MaterialParameters = PixelParameters()
            });
            AddShader(new TBasePassVertexShader<FLightMapTexturePolicy.VertexParametersType, FNullPolicy>
            {
                Guid = LightMapVertexId,
                ShaderType = "TBasePassVertexShaderFDirectionalLightMapTexturePolicyFNoDensityPolicy",
                VertexFactoryParameters = LocalFactory(),
                MaterialParameters = VertexParameters()
            });
            AddShader(new TBasePassPixelShader<FLightMapTexturePolicy.PixelParametersType>
            {
                Guid = LightMapPixelId,
                ShaderType = "TBasePassPixelShaderFDirectionalLightMapTexturePolicyNoSkyLight",
                MaterialParameters = PixelParameters()
            });
            package.CreateExport("SeekFreeShaderCache", "ShaderCache", indexed: false).WriteBinary(cache);

            void AddShader(Shader shader)
            {
                // These tests exercise cooked cache serialization and shader selection without a GPU.
                shader.ShaderByteCode = [];
                shader.Platform = 5;
                shader.Frequency = shader is TBasePassPixelShader<FNullPolicy>
                    or TBasePassPixelShader<FLightMapTexturePolicy.PixelParametersType>
                    ? Shader.ShaderFrequency.Pixel : Shader.ShaderFrequency.Vertex;
                cache.Shaders.Add(shader.Guid, shader);
                map.MeshShaderMaps[0].Shaders.Add(shader.ShaderType,
                    new ShaderReference { Id = shader.Guid, ShaderType = shader.ShaderType });
            }
        }

        private static FVertexFactoryParameterRef LocalFactory() => new()
        {
            VertexFactoryType = "FLocalVertexFactory",
            Parameters = new FLocalVertexFactoryShaderParameters()
        };

        private static FMaterialVertexShaderParameters VertexParameters() => new()
        {
            UniformVertexScalarShaderParameters = [],
            UniformVertexVectorShaderParameters = []
        };

        private static FMaterialPixelShaderParameters PixelParameters() => new()
        {
            UniformPixelScalarShaderParameters = [],
            UniformPixelVectorShaderParameters = [],
            UniformPixel2DShaderResourceParameters = [],
            UniformPixelCubeShaderResourceParameters = []
        };

        public void Dispose() => package.Dispose();
    }
}
