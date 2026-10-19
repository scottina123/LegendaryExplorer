using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using LegacyScene = LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class LegacyMaterialTextureUniformTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ChildPermutationKeepsItsShadersAndTextureTable(bool emptyChildTextureTable)
    {
        using var fixture = new MaterialFixture(emptyChildTextureTable);
        LegacyScene.MaterialRenderProxy material = fixture.CreatePreview();

        Assert.AreEqual(fixture.ChildVertexId, material.VertexShader.Guid);
        Assert.AreEqual(fixture.ChildPixelId, material.PixelShader.Guid);
        var textures = material.GetCachedPixelParameters(fixture.Context).tex2d;
        Assert.HasCount(1, textures);
        if (emptyChildTextureTable)
        {
            Assert.IsNull(textures[0], "An empty child table must not adopt the parent's texture at the same index.");
        }
        else
        {
            Assert.AreSame(fixture.ChildTexture, textures[0],
                "Cooked parameter defaults must use the texture table belonging to the selected child permutation.");
        }
    }

    [TestMethod]
    public void InheritedMicParametersOverrideCompiledChildDefaults()
    {
        using var fixture = new MaterialFixture();
        LegacyScene.MaterialRenderProxy material = fixture.CreatePreview();

        Assert.AreEqual(0.75f, material.ScalarParameters["InheritedScalar"]);
        Assert.AreEqual(0.1f, material.VectorParameters["InheritedColor"].R);
        Assert.AreEqual(0.2f, material.VectorParameters["InheritedColor"].G);
        Assert.AreEqual(0.3f, material.VectorParameters["InheritedColor"].B);
        Assert.AreEqual(0.5f, material.ScalarParameters["CompiledOnlyScalar"]);
    }

    [TestMethod]
    public void MissingTextureParameterUsesCookedDefaultAndLiveNoneCanBeReset()
    {
        using var fixture = new MaterialFixture();
        LegacyScene.MaterialRenderProxy material = fixture.CreatePreview();

        Assert.AreSame(fixture.ChildTexture, material.GetCachedPixelParameters(fixture.Context).tex2d[0]);
        material.SetTextureParameter("CookedDiffuse", null, null);
        Assert.IsNull(material.GetCachedPixelParameters(fixture.Context).tex2d[0],
            "An explicit None override must remain different from an absent parameter.");
        material.ResetPreviewParameterOverrides();
        Assert.AreSame(fixture.ChildTexture, material.GetCachedPixelParameters(fixture.Context).tex2d[0]);
    }

    [TestMethod]
    public void SerializedNoneTextureOverrideDoesNotUseCookedDefault()
    {
        using var fixture = new MaterialFixture(explicitNone: true);
        LegacyScene.MaterialRenderProxy material = fixture.CreatePreview();

        Assert.IsNull(material.GetCachedPixelParameters(fixture.Context).tex2d[0]);
    }

    [DataTestMethod]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(3, false)]
    [DataRow(3, true)]
    public void DirectionalPreviewRequiresACompletePairAndALitMaterial(int directionalStages, bool unlit)
    {
        using var fixture = new MaterialFixture(directionalStages: directionalStages);
        LegacyScene.MaterialRenderProxy material = fixture.CreatePreview();
        material.IsUnlit = unlit;

        var (vertexShader, pixelShader) = material.GetPreviewShaders();
        bool directional = directionalStages == 3 && !unlit;
        Assert.AreEqual(directional ? fixture.ChildDirectionalVertexId : fixture.ChildVertexId, vertexShader.Guid);
        Assert.AreEqual(directional ? fixture.ChildDirectionalPixelId : fixture.ChildPixelId, pixelShader.Guid);
    }

    [TestMethod]
    public void DirectionalPreviewWritesCameraRelativeKeyLightAndSkinLightingConstants()
    {
        using var fixture = new MaterialFixture(directionalStages: 3);
        LegacyScene.MaterialRenderProxy material = fixture.CreatePreview();
        fixture.Context.GameShaderLightScale = 1.5f;
        fixture.Context.Camera.Pitch = 0.3f;
        fixture.Context.Camera.Yaw = -0.7f;
        var vertexBuffer = new byte[256];
        var pixelBuffer = new byte[256];
        using var mesh = new LegacyScene.Mesh<LegacyScene.LEVertex>(null, [], []);

        material.UpdateShaderParams(vertexBuffer, pixelBuffer, fixture.Context, mesh);

        Vector4 direction = MemoryMarshal.Read<Vector4>(vertexBuffer.AsSpan(16));
        Assert.AreEqual(1f, direction.W);
        Vector3 cameraRelative = Vector3.TransformNormal(new Vector3(direction.X, direction.Y, direction.Z), fixture.Context.Camera.ViewMatrix);
        Assert.IsTrue(Vector3.Distance(Vector3.Normalize(new Vector3(-1, 1, -1)), cameraRelative) < 0.00001f,
            "The light must stay above and to the left of the camera when it orbits.");
        Assert.AreEqual(new Vector4(1.5f, 0.97f * 1.5f, 0.92f * 1.5f, 0), MemoryMarshal.Read<Vector4>(pixelBuffer.AsSpan(32)));
        Assert.AreEqual(0, MemoryMarshal.Read<int>(pixelBuffer.AsSpan(48)));
        Assert.AreEqual(new Vector3(0.26f, 0.28f, 0.32f) * 1.5f, MemoryMarshal.Read<Vector3>(pixelBuffer.AsSpan(64)));
        Assert.AreEqual(new Vector3(0.08f, 0.07f, 0.06f) * 1.5f, MemoryMarshal.Read<Vector3>(pixelBuffer.AsSpan(80)));
        Assert.AreEqual(new Vector4(0.33f, 0.05f, 0, 0), MemoryMarshal.Read<Vector4>(pixelBuffer.AsSpan(96)));
    }

    private sealed class MaterialFixture : IDisposable
    {
        private readonly IMEPackage package;
        private readonly ExportEntry child;
        private readonly ExportEntry childTextureExport;
        private readonly ExportEntry parentTextureExport;
        private readonly ExportEntry baseTextureExport;
        public LegacyScene.MeshRenderContext Context { get; } = new();
        public Guid ChildVertexId { get; } = Guid.NewGuid();
        public Guid ChildPixelId { get; } = Guid.NewGuid();
        public Guid ChildDirectionalVertexId { get; } = Guid.NewGuid();
        public Guid ChildDirectionalPixelId { get; } = Guid.NewGuid();
        // Texture evaluation needs only cached entry identity; no Direct3D device or installed game.
        public LegacyScene.PreviewTextureCache.TextureEntry ChildTexture { get; } = TexturePlaceholder();
        private readonly LegacyScene.PreviewTextureCache.TextureEntry parentTexture = TexturePlaceholder();
        private readonly LegacyScene.PreviewTextureCache.TextureEntry baseTexture = TexturePlaceholder();

        public MaterialFixture(bool emptyChildTextureTable = false, bool explicitNone = false, int directionalStages = 0)
        {
            LegendaryExplorerCore.LegendaryExplorerCoreLib.InitLib(System.Threading.Tasks.TaskScheduler.Default);
            package = MEPackageHandler.CreateMemoryEmptyPackage("LegacyMaterialTextureUniforms.pcc", MEGame.LE3);
            childTextureExport = package.CreateExport("ChildDiffuse", "Texture2D", indexed: false);
            parentTextureExport = package.CreateExport("ParentDiffuse", "Texture2D", indexed: false);
            baseTextureExport = package.CreateExport("BaseDiffuse", "Texture2D", indexed: false);

            var master = package.CreateExport("Master", "Material", indexed: false);
            var masterBinary = Material.Create();
            masterBinary.SM3MaterialResource.ID = Guid.NewGuid();
            masterBinary.SM3MaterialResource.UniformExpressionTextures = [baseTextureExport.UIndex];
            master.WriteBinary(masterBinary);

            var parent = package.CreateExport("Parent", "MaterialInstanceConstant", indexed: false);
            var parentBinary = MaterialInstance.Create();
            parentBinary.SM3StaticPermutationResource.ID = Guid.NewGuid();
            parentBinary.SM3StaticParameterSet = (StaticParameterSet)parentBinary.SM3StaticPermutationResource.ID;
            parentBinary.SM3StaticPermutationResource.UniformExpressionTextures = [parentTextureExport.UIndex];
            parent.WritePropertiesAndBinary(new PropertyCollection
            {
                new ObjectProperty(master, "Parent"),
                new BoolProperty(true, "bHasStaticPermutationResource"),
                new ArrayProperty<StructProperty>(
                [
                    new StructProperty("ScalarParameterValue", false,
                        new NameProperty("InheritedScalar", "ParameterName"), new FloatProperty(0.75f, "ParameterValue"))
                ], "ScalarParameterValues"),
                new ArrayProperty<StructProperty>(
                [
                    new StructProperty("VectorParameterValue", false,
                        new NameProperty("InheritedColor", "ParameterName"),
                        CommonStructs.LinearColorProp(0.1f, 0.2f, 0.3f, 1f, "ParameterValue"))
                ], "VectorParameterValues")
            }, parentBinary);

            child = package.CreateExport("Child", "MaterialInstanceConstant", indexed: false);
            var childBinary = MaterialInstance.Create();
            childBinary.SM3StaticPermutationResource.ID = Guid.NewGuid();
            childBinary.SM3StaticParameterSet = (StaticParameterSet)childBinary.SM3StaticPermutationResource.ID;
            childBinary.SM3StaticPermutationResource.UniformExpressionTextures = emptyChildTextureTable ? [] : [childTextureExport.UIndex];
            var childProperties = new PropertyCollection
            {
                new ObjectProperty(parent, "Parent"), new BoolProperty(true, "bHasStaticPermutationResource")
            };
            if (explicitNone)
            {
                childProperties.Add(new ArrayProperty<StructProperty>(
                [
                    new StructProperty("TextureParameterValue", false,
                        new NameProperty("CookedDiffuse", "ParameterName"), new ObjectProperty(0, "ParameterValue"))
                ], "TextureParameterValues"));
            }
            child.WritePropertiesAndBinary(childProperties, childBinary);

            var cache = ShaderCache.Create();
            AddMap(childBinary.SM3StaticParameterSet, ChildVertexId, ChildPixelId);
            AddMap(parentBinary.SM3StaticParameterSet, Guid.NewGuid(), Guid.NewGuid());
            AddMap((StaticParameterSet)masterBinary.SM3MaterialResource.ID, Guid.NewGuid(), Guid.NewGuid());
            package.CreateExport("SeekFreeShaderCache", "ShaderCache", indexed: false).WriteBinary(cache);

            void AddMap(StaticParameterSet parameters, Guid vertexId, Guid pixelId)
            {
                var map = new MaterialShaderMap
                {
                    ID = parameters.BaseMaterialId,
                    FriendlyName = "Legacy texture ownership regression",
                    StaticParameters = parameters,
                    Shaders = [],
                    MeshShaderMaps = [new MeshShaderMap { VertexFactoryType = "FLocalVertexFactory", Shaders = [] }],
                    UniformPixelScalarExpressions =
                    [
                        new MaterialUniformExpressionScalarParameter
                        {
                            ExpressionType = "FMaterialUniformExpressionScalarParameter",
                            ParameterName = "InheritedScalar", DefaultValue = 0.25f
                        },
                        new MaterialUniformExpressionScalarParameter
                        {
                            ExpressionType = "FMaterialUniformExpressionScalarParameter",
                            ParameterName = "CompiledOnlyScalar", DefaultValue = 0.5f
                        }
                    ],
                    UniformPixelVectorExpressions =
                    [
                        new MaterialUniformExpressionVectorParameter
                        {
                            ExpressionType = "FMaterialUniformExpressionVectorParameter",
                            ParameterName = "InheritedColor", DefaultValue = LinearColor.White
                        }
                    ],
                    UniformVertexScalarExpressions = [], UniformVertexVectorExpressions = [],
                    Uniform2DTextureExpressions =
                    [
                        new MaterialUniformExpressionTextureParameter
                        {
                            ExpressionType = "FMaterialUniformExpressionTextureParameter",
                            ParameterName = "CookedDiffuse", TextureIndex = 0
                        }
                    ],
                    UniformCubeTextureExpressions = []
                };
                cache.MaterialShaderMaps.Add(parameters, map);
                AddShader(new TBasePassVertexShader<FNullPolicy, FNullPolicy>
                {
                    Guid = vertexId, ShaderType = "TBasePassVertexShaderFNoLightMapPolicyFNoDensityPolicy",
                    Frequency = Shader.ShaderFrequency.Vertex,
                    VertexFactoryParameters = new FVertexFactoryParameterRef
                    {
                        VertexFactoryType = "FLocalVertexFactory", Parameters = new FLocalVertexFactoryShaderParameters()
                    },
                    MaterialParameters = new FMaterialVertexShaderParameters
                    {
                        UniformVertexScalarShaderParameters = [], UniformVertexVectorShaderParameters = []
                    }
                });
                AddShader(new TBasePassPixelShader<FNullPolicy>
                {
                    Guid = pixelId, ShaderType = "TBasePassPixelShaderFNoLightMapPolicySkyLight",
                    Frequency = Shader.ShaderFrequency.Pixel,
                    MaterialParameters = new FMaterialPixelShaderParameters
                    {
                        UniformPixelScalarShaderParameters = [], UniformPixelVectorShaderParameters = [],
                        UniformPixel2DShaderResourceParameters = [], UniformPixelCubeShaderResourceParameters = []
                    }
                });
                if ((directionalStages & 1) != 0)
                {
                    AddShader(new TBasePassVertexShader<FDirectionalLightPolicy.VertexParametersType, FNullPolicy>
                    {
                        Guid = vertexId == ChildVertexId ? ChildDirectionalVertexId : Guid.NewGuid(),
                        ShaderType = "TBasePassVertexShaderFDirectionalLightLightMapPolicyFNoDensityPolicy",
                        Frequency = Shader.ShaderFrequency.Vertex,
                        VertexFactoryParameters = new FVertexFactoryParameterRef
                        {
                            VertexFactoryType = "FLocalVertexFactory", Parameters = new FLocalVertexFactoryShaderParameters()
                        },
                        MaterialParameters = new FMaterialVertexShaderParameters
                        {
                            UniformVertexScalarShaderParameters = [], UniformVertexVectorShaderParameters = []
                        },
                        LightMapVertexParams = new FDirectionalLightPolicy.VertexParametersType
                        {
                            LightDirection = Binding(16, 16)
                        }
                    });
                }
                if ((directionalStages & 2) != 0)
                {
                    AddShader(new TBasePassPixelShader<FDirectionalLightLightMapPolicy.PixelParametersType>
                    {
                        Guid = pixelId == ChildPixelId ? ChildDirectionalPixelId : Guid.NewGuid(),
                        ShaderType = "TBasePassPixelShaderFDirectionalLightLightMapPolicySkyLight",
                        Frequency = Shader.ShaderFrequency.Pixel,
                        MaterialParameters = new FMaterialPixelShaderParameters
                        {
                            UniformPixelScalarShaderParameters = [], UniformPixelVectorShaderParameters = [],
                            UniformPixel2DShaderResourceParameters = [], UniformPixelCubeShaderResourceParameters = [],
                            WrapLightingParameters = Binding(96, 16)
                        },
                        PixelParams = new FDirectionalLightLightMapPolicy.PixelParametersType
                        {
                            LightColorAndFalloffExponent = Binding(32, 16), bReceiveDynamicShadows = Binding(48, 4)
                        },
                        UpperSkyColor = Binding(64, 12), LowerSkyColor = Binding(80, 12)
                    });
                }

                void AddShader(Shader shader)
                {
                    shader.ShaderByteCode = [];
                    shader.Platform = 5;
                    cache.Shaders.Add(shader.Guid, shader);
                    map.MeshShaderMaps[0].Shaders.Add(shader.ShaderType,
                        new ShaderReference { Id = shader.Guid, ShaderType = shader.ShaderType });
                }
            }
        }

        public LegacyScene.MaterialRenderProxy CreatePreview()
        {
            var material = new LegacyScene.MaterialRenderProxy(child)
            {
                TextureMap = new Dictionary<string, LegacyScene.PreviewTextureCache.TextureEntry>(StringComparer.OrdinalIgnoreCase)
            };
            // Match ModelPreview's discovery path: only textures requested by the material can be
            // uploaded. Prepopulating every fixture texture would hide a missing static-MIC default.
            foreach (IEntry texture in material.Textures)
            {
                material.TextureMap[texture.InstancedFullPath] = texture == childTextureExport ? ChildTexture
                    : texture == parentTextureExport ? parentTexture : baseTexture;
            }
            return material;
        }

        private static LegacyScene.PreviewTextureCache.TextureEntry TexturePlaceholder() =>
            (LegacyScene.PreviewTextureCache.TextureEntry)RuntimeHelpers.GetUninitializedObject(typeof(LegacyScene.PreviewTextureCache.TextureEntry));

        private static FShaderParameter Binding(ushort offset, ushort size) => new() { BaseIndex = offset, NumBytes = size };

        public void Dispose() => package.Dispose();
    }
}
