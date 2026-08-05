using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using LegendaryExplorerCore.Unreal.Classes;
using SharpDX.Direct3D11;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

//update the name strings too
using PixelShaderType = TBasePassPixelShader<FNullPolicy>;
using VertexShaderType = TBasePassVertexShader<FNullPolicy, FNullPolicy>;
public class MaterialRenderProxy : MaterialInstanceConstantLevelEditor
{
    public const string LocalVertexFactoryType = "FLocalVertexFactory";
    private const string VERTEX_SHADER_TYPE_NAME = "TBasePassVertexShaderFNoLightMapPolicyFNoDensityPolicy";
    private const string LIT_PIXEL_SHADER_TYPE_NAME = "TBasePassPixelShaderFNoLightMapPolicySkyLight";
    private const string UNLIT_PIXEL_SHADER_TYPE_NAME = "TBasePassPixelShaderFNoLightMapPolicyNoSkyLight";
    private const string HUMAN_LASH_MASTER_MATERIAL_NAME = "HMN_HED_LASH_Unlit_MASTER_MAT";
    private const string HUMAN_LASH_OPACITY_PARAMETER_NAME = "HED_Lash_Diff";

    public EBlendMode BlendMode;
    public bool UseHairPass;
    public bool IsUnlit;
    public bool IsHumanLashMaterial;
    private readonly Dictionary<string, float> ScalarParameterValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LinearColor> VectorParameterValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> TextureParameterValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> Uniform2DTextureExpressions = [];
    public Dictionary<string, PreviewTextureCache.TextureEntry> TextureMap;
    private MaterialShaderMap ShaderMap;
    private uint CachedPixelFrameNumber = uint.MaxValue;
    private uint CachedVertexFrameNumber = uint.MaxValue;
    private readonly List<Vector4> CachedVertexScalarParameters = [];
    private readonly List<Vector4> CachedVertexVectorParameters = [];
    private readonly List<Vector4> CachedPixelScalarParameters = [];
    private readonly List<Vector4> CachedPixelVectorParameters = [];
    private readonly List<PreviewTextureCache.TextureEntry> CachedTexture2DParameters = [];
    private readonly List<PreviewTextureCache.TextureEntry> CachedCubeTextureParameters = [];

    public VertexShaderType UnrealVertexShader;
    public PixelShaderType UnrealPixelShader;
    public string VertexFactoryType { get; private set; } = LocalVertexFactoryType;
    public ParticleVertexFactoryRenderParameters ParticleFactoryParameters { get; } = new();

    public MaterialRenderProxy(MeshRenderContext context, ExportEntry export,
        MaterialRenderProxy parameterSource = null) : base(export, context.PackageCache, true)
    {
        if (parameterSource is not null)
        {
            CopyEffectiveParametersFrom(parameterSource);
        }
    }

    public MaterialRenderProxy(MeshRenderContext context, ExportEntry export, string vertexFactoryType)
        : base(export, context.PackageCache, true)
    {
        VertexFactoryType = string.IsNullOrWhiteSpace(vertexFactoryType)
            ? LocalVertexFactoryType
            : vertexFactoryType;
        // ReadBaseMaterial runs during the base constructor, before a derived constructor can select its
        // particle factory. Reload only the shader references now; the uniform-expression map is shared.
        LoadShaders(export);
    }

    /// <summary>
    /// Keeps the selected actor material instance's effective parameters while using another compiled shader.
    /// Rvr material effects use this to render the multiplexor's CombinedEffect material without losing the
    /// character-specific face, armor, eye, or hair textures supplied by the mesh's MIC.
    /// </summary>
    private void CopyEffectiveParametersFrom(MaterialRenderProxy source)
    {
        foreach ((string name, float value) in source.ScalarParameterValues)
        {
            ScalarParameterValues[name] = value;
        }
        foreach ((string name, LinearColor value) in source.VectorParameterValues)
        {
            VectorParameterValues[name] = value;
        }
        foreach ((string name, string value) in source.TextureParameterValues)
        {
            TextureParameterValues[name] = value;
        }
        Textures.UnionWith(source.Textures);
        CachedPixelFrameNumber = CachedVertexFrameNumber = uint.MaxValue;
    }

    protected override void ReadBaseMaterial(ExportEntry mat, PackageCache assetCache, Material parsedMaterial)
    {
        base.ReadBaseMaterial(mat, assetCache, parsedMaterial);

        if (!Game.IsLEGame()) return;

        var props = mat.GetProperties(packageCache: assetCache);
        Enum.TryParse(props.GetProp<EnumProperty>("BlendMode")?.Value ?? "BLEND_Opaque", out BlendMode);

        //if the MIC had a StaticPermutationResource, this is already set
        if (Uniform2DTextureExpressions.IsEmpty())
        {
            foreach (int uIndex in parsedMaterial.SM3MaterialResource.UniformExpressionTextures)
            {
                Uniform2DTextureExpressions.Add(mat.FileRef.GetEntry(uIndex)?.InstancedFullPath);
            }
        }

        UseHairPass = props.GetProp<BoolProperty>("bHairPass") is { Value: true };
        IsUnlit = props.GetProp<EnumProperty>("LightingModel") is {} lightingModelProp && lightingModelProp.Value == "MLM_Unlit";
        IsHumanLashMaterial = mat.ObjectName.Name.Equals(HUMAN_LASH_MASTER_MATERIAL_NAME, StringComparison.OrdinalIgnoreCase);

        var expressionsProp = props.GetProp<ArrayProperty<ObjectProperty>>("Expressions");
        if (expressionsProp is not null)
        {
            foreach (ObjectProperty expressionProp in expressionsProp)
            {
                ExportEntry expressionExport = expressionProp.ResolveToExport(mat.FileRef, assetCache);
                var expressionProps = expressionExport?.GetProperties(packageCache: assetCache);
                if (expressionProps?.GetProp<NameProperty>("ParameterName") is {} paramNameProp)
                {
                    //this will run after ReadMaterialInstanceConstant, so we don't want to overwrite any values specified there
                    Property defaultValue = expressionProps.FirstOrDefault(property => property.Name == "DefaultValue");
                    if (defaultValue is FloatProperty defaultFloatProp)
                    {
                        ScalarParameterValues.TryAdd(paramNameProp.Value.Instanced, defaultFloatProp.Value);
                    }
                    else if (defaultValue is StructProperty defaultVectorProp)
                    {
                        VectorParameterValues.TryAdd(paramNameProp.Value.Instanced, CommonStructs.GetLinearColor(defaultVectorProp));
                    }
                    else if (expressionProps.GetProp<ObjectProperty>("Texture") is {} textureProp)
                    {
                        if (!TextureParameterValues.ContainsKey(paramNameProp.Value.Instanced) 
                            && mat.FileRef.GetEntry(textureProp.Value) is {} texEntry)
                        {
                            Textures.Add(texEntry);
                            TextureParameterValues.Add(paramNameProp.Value.Instanced, texEntry.InstancedFullPath);
                        }
                    }
                }
            }
        }

        //if the MIC had a StaticPermutationResource, this is already set
        if (ShaderMap is null)
        {
            LoadShaders(mat);
        }
    }

    private void LoadShaders(ExportEntry mat)
    {
        string factoryType = VertexFactoryType ?? LocalVertexFactoryType;
        (ShaderMap, Shader[] shaders) = ShaderCacheManipulator.GetMaterialShaderMapAndShadersForVertexFactory(
            mat, factoryType, VERTEX_SHADER_TYPE_NAME, LIT_PIXEL_SHADER_TYPE_NAME, UNLIT_PIXEL_SHADER_TYPE_NAME);

        UnrealVertexShader = (VertexShaderType)shaders[0];
        UnrealPixelShader = (PixelShaderType)(shaders[1] ?? shaders[2]);
    }

    protected override void ReadMaterialInstanceConstant(ExportEntry matInst, PropertyCollection props)
    {
        base.ReadMaterialInstanceConstant(matInst, props);

        if (!Game.IsLEGame()) return;

        if (props.GetProp<ArrayProperty<StructProperty>>("ScalarParameterValues") is { } scalarValues)
        {
            foreach (StructProperty scalarValue in scalarValues)
            {
                if (scalarValue.GetProp<NameProperty>("ParameterName") is { } paramNameProp
                    && scalarValue.GetProp<FloatProperty>("ParameterValue") is { } valProp)
                {
                    ScalarParameterValues[paramNameProp.Value.Instanced] = valProp.Value;
                }
            }
        }
        if (props.GetProp<ArrayProperty<StructProperty>>("VectorParameterValues") is { } vectorValues)
        {
            foreach (StructProperty vectorValue in vectorValues)
            {
                if (vectorValue.GetProp<NameProperty>("ParameterName") is { } paramNameProp
                    && vectorValue.GetProp<StructProperty>("ParameterValue") is { } valProp)
                {
                    VectorParameterValues[paramNameProp.Value.Instanced] = CommonStructs.GetLinearColor(valProp);
                }
            }
        }
        if (props.GetProp<ArrayProperty<StructProperty>>("TextureParameterValues") is { } textureValues)
        {
            foreach (StructProperty textureValue in textureValues)
            {
                if (textureValue.GetProp<NameProperty>("ParameterName") is { } paramNameProp
                    && textureValue.GetProp<ObjectProperty>("ParameterValue") is { } valProp)
                {
                    TextureParameterValues[paramNameProp.Value.Instanced] = valProp.ResolveToEntry(matInst.FileRef)?.InstancedFullPath;
                }
            }
        }

        if (ObjectBinary.From(matInst) is MaterialInstance binary)
        {
            foreach (int uIndex in binary.SM3StaticPermutationResource.UniformExpressionTextures)
            {
                Uniform2DTextureExpressions.Add(matInst.FileRef.GetEntry(uIndex)?.InstancedFullPath);
            }
            LoadShaders(matInst);
        }
    }

    public void UpdateShaderParams<TVertex>(Span<byte> vertexConstantBuffer, Span<byte> pixelConstantBuffer, MeshRenderContext context, Mesh<TVertex> mesh)
        where TVertex : IVertexBase
    {
        //Span<byte> vertBufferBytes = [0, 80, 175, 250, 100, 2, 0, 0, 96, 241, 173, 250, 100, 2, 0, 0, 160, 112, 80, 85, 249, 127, 0, 0, 160, 112, 80, 85, 249, 127, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 24, 94, 80, 85, 249, 127, 0, 0, 24, 94, 80, 85, 249, 127, 0, 0, 128, 91, 80, 85, 249, 127, 0, 0, 128, 91, 80, 85, 249, 127, 0, 0, 168, 91, 80, 85, 249, 127, 0, 0, 168, 91, 80, 85, 249, 127, 0, 0, 56, 91, 80, 85, 249, 127, 0, 0, 56, 91, 80, 85, 249, 127, 0, 0, 160, 93, 80, 85, 249, 127, 0, 0, 160, 93, 80, 85, 249, 127, 0, 0, 200, 93, 80, 85, 249, 127, 0, 0, 200, 93, 80, 85, 249, 127, 0, 0, 240, 93, 80, 85, 249, 127, 0, 0, 240, 93, 80, 85, 249, 127, 0, 0, 112, 94, 80, 85, 249, 127, 0, 0, 112, 94, 80, 85, 249, 127, 0, 0, 152, 94, 80, 85, 249, 127, 0, 0, 152, 94, 80, 85, 249, 127, 0, 0, 192, 94, 80, 85, 249, 127, 0, 0, 192, 94, 80, 85, 249, 127, 0, 0, 88, 98, 80, 85, 249, 127, 0, 0, 88, 98, 80, 85, 249, 127, 0, 0, 192, 98, 80, 85, 249, 127, 0, 0, 192, 98, 80, 85, 249, 127, 0, 0, 8, 99, 80, 85, 249, 127, 0, 0, 8, 99, 80, 85, 249, 127, 0, 0, 80, 99, 80, 85, 249, 127, 0, 0, 80, 99, 80, 85, 249, 127, 0, 0, 192, 104, 80, 85, 249, 127, 0, 0, 192, 104, 80, 85, 249, 127, 0, 0, 248, 104, 80, 85, 249, 127, 0, 0, 248, 104, 80, 85, 249, 127, 0, 0, 48, 105, 80, 85, 249, 127, 0, 0, 48, 105, 80, 85, 249, 127, 0, 0, 208, 108, 80, 85, 249, 127, 0, 0, 208, 108, 80, 85, 249, 127, 0, 0, 8, 109, 80, 85, 249, 127, 0, 0, 8, 109, 80, 85, 249, 127, 0, 0, 48, 109, 80, 85, 249, 127, 0, 0, 48, 109, 80, 85, 249, 127, 0, 0, 104, 109, 80, 85, 249, 127, 0, 0, 104, 109, 80, 85, 249, 127, 0, 0, 144, 109, 80, 85, 249, 127, 0, 0, 144, 109, 80, 85, 249, 127, 0, 0, 200, 109, 80, 85, 249, 127, 0, 0, 200, 109, 80, 85, 249, 127, 0, 0, 240, 109, 80, 85, 249, 127, 0, 0, 0, 0, 128, 63, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 63, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 63, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 63, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 63, 8, 91, 80, 85, 249, 127, 0, 0, 32, 91, 80, 85, 249, 127, 0, 0, 32, 91, 80, 85, 249, 127, 0, 0, 208, 91, 80, 85, 249, 127, 0, 0, 208, 91, 80, 85, 249, 127, 0, 0, 232, 91, 80, 85, 249, 127, 0, 0, 0, 0, 128, 63, 249, 127, 0, 0, 0, 92, 80, 85, 249, 127, 0, 0, 0, 0, 128, 63, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 249, 127, 0, 0, 96, 92, 80, 85, 249, 127, 0, 0, 96, 92, 80, 85, 249, 127, 0, 0, 120, 92, 80, 85, 249, 127, 0, 0, 120, 92, 80, 85, 249, 127, 0, 0, 144, 92, 80, 85, 249, 127, 0, 0, 144, 92, 80, 85, 249, 127, 0, 0, 184, 92, 80, 85, 249, 127, 0, 0, 184, 92, 80, 85, 249, 127, 0, 0, 224, 92, 80, 85, 249, 127, 0, 0, 224, 92, 80, 85, 249, 127, 0, 0, 248, 92, 80, 85, 249, 127, 0, 0, 248, 92, 80, 85, 249, 127, 0, 0, 16, 93, 80, 85, 249, 127, 0, 0, 16, 93, 80, 85, 249, 127, 0, 0, 40, 93, 80, 85, 249, 127, 0, 0, 40, 93, 80, 85, 249, 127, 0, 0, 64, 93, 80, 85, 249, 127, 0, 0, 64, 93, 80, 85, 249, 127, 0, 0, 88, 93, 80, 85, 249, 127, 0, 0, 88, 93, 80, 85, 249, 127, 0, 0, 112, 93, 80, 85, 249, 127, 0, 0, 112, 93, 80, 85, 249, 127, 0, 0, 136, 93, 80, 85, 249, 127, 0, 0, 136, 93, 80, 85, 249, 127, 0, 0, 64, 94, 80, 85, 249, 127, 0, 0, 64, 94, 80, 85, 249, 127, 0, 0, 88, 94, 80, 85, 249, 127, 0, 0, 88, 94, 80, 85, 249, 127, 0, 0, 232, 94, 80, 85, 249, 127, 0, 0, 232, 94, 80, 85, 249, 127, 0, 0, 0, 95, 80, 85, 249, 127, 0, 0, 0, 95, 80, 85, 249, 127, 0, 0, 24, 95, 80, 85, 249, 127, 0, 0, 24, 95, 80, 85, 249, 127, 0, 0, 64, 95, 80, 85, 249, 127, 0, 0, 64, 95, 80, 85, 249, 127, 0, 0, 104, 95, 80, 85, 249, 127, 0, 0, 104, 95, 80, 85, 249, 127, 0, 0, 144, 95, 80, 85, 249, 127, 0, 0, 144, 95, 80, 85, 249, 127, 0, 0, 184, 95, 80, 85, 249, 127, 0, 0, 184, 95, 80, 85, 249, 127, 0, 0, 208, 95, 80, 85, 249, 127, 0, 0, 208, 95, 80, 85, 249, 127, 0, 0, 248, 95, 80, 85, 249, 127, 0, 0, 248, 95, 80, 85, 249, 127, 0, 0, 32, 96, 80, 85, 249, 127, 0, 0, 32, 96, 80, 85, 249, 127, 0, 0, 56, 96, 80, 85, 249, 127, 0, 0, 56, 96, 80, 85, 249, 127, 0, 0, 80, 96, 80, 85, 249, 127, 0, 0, 80, 96, 80, 85, 249, 127, 0, 0, 120, 96, 80, 85, 249, 127, 0, 0, 120, 96, 80, 85, 249, 127, 0, 0, 160, 96, 80, 85, 249, 127, 0, 0, 160, 96, 80, 85, 249, 127, 0, 0, 200, 96, 80, 85, 249, 127, 0, 0, 200, 96, 80, 85, 249, 127, 0, 0, 240, 96, 80, 85, 249, 127, 0, 0, 240, 96, 80, 85, 249, 127, 0, 0, 24, 97, 80, 85, 249, 127, 0, 0, 24, 97, 80, 85, 249, 127, 0, 0, 64, 97, 80, 85, 249, 127, 0, 0, 64, 97, 80, 85, 249, 127, 0, 0, 104, 97, 80, 85, 249, 127, 0, 0, 104, 97, 80, 85, 249, 127, 0, 0, 144, 97, 80, 85, 249, 127, 0, 0, 144, 97, 80, 85, 249, 127, 0, 0, 184, 97, 80, 85, 249, 127, 0, 0, 184, 97, 80, 85, 249, 127, 0, 0, 224, 97, 80, 85, 249, 127, 0, 0, 224, 97, 80, 85, 249, 127, 0, 0, 8, 98, 80, 85, 249, 127, 0, 0, 8, 98, 80, 85, 249, 127, 0, 0, 48, 98, 80, 85, 249, 127, 0, 0, 48, 98, 80, 85, 249, 127, 0, 0, 152, 99, 80, 85, 249, 127, 0, 0, 152, 99, 80, 85, 249, 127, 0, 0, 192, 99, 80, 85, 249, 127, 0, 0, 192, 99, 80, 85, 249, 127, 0, 0, 232, 99, 80, 85, 249, 127, 0, 0, 232, 99, 80, 85, 249, 127, 0, 0, 16, 100, 80, 85, 249, 127, 0, 0, 16, 100, 80, 85, 249, 127, 0, 0, 56, 100, 80, 85, 249, 127, 0, 0, 56, 100, 80, 85, 249, 127, 0, 0, 96, 100, 80, 85, 249, 127, 0, 0, 96, 100, 80, 85, 249, 127, 0, 0, 136, 100, 80, 85, 249, 127, 0, 0, 136, 100, 80, 85, 249, 127, 0, 0, 176, 100, 80, 85, 249, 127, 0, 0, 176, 100, 80, 85, 249, 127, 0, 0, 216, 100, 80, 85, 249, 127, 0, 0, 216, 100, 80, 85, 249, 127, 0, 0, 0, 101, 80, 85, 249, 127, 0, 0, 0, 101, 80, 85, 249, 127, 0, 0, 40, 101, 80, 85, 249, 127, 0, 0, 40, 101, 80, 85, 249, 127, 0, 0, 80, 101, 80, 85, 249, 127, 0, 0, 80, 101, 80, 85, 249, 127, 0, 0, 120, 101, 80, 85, 249, 127, 0, 0, 120, 101, 80, 85, 249, 127, 0, 0, 160, 101, 80, 85, 249, 127, 0, 0, 160, 101, 80, 85, 249, 127, 0, 0, 200, 101, 80, 85, 249, 127, 0, 0, 200, 101, 80, 85, 249, 127, 0, 0, 240, 101, 80, 85, 249, 127, 0, 0, 240, 101, 80, 85, 249, 127, 0, 0, 24, 102, 80, 85, 249, 127, 0, 0, 24, 102, 80, 85, 249, 127, 0, 0, 64, 102, 80, 85, 249, 127, 0, 0, 64, 102, 80, 85, 249, 127, 0, 0, 104, 102, 80, 85, 249, 127, 0, 0, 104, 102, 80, 85, 249, 127, 0, 0, 144, 102, 80, 85, 249, 127, 0, 0, 144, 102, 80, 85, 249, 127, 0, 0, 184, 102, 80, 85, 249, 127, 0, 0, 184, 102, 80, 85, 249, 127, 0, 0, 224, 102, 80, 85, 249, 127, 0, 0, 224, 102, 80, 85, 249, 127, 0, 0, 8, 103, 80, 85, 249, 127, 0, 0, 8, 103, 80, 85, 249, 127, 0, 0, 48, 103, 80, 85, 249, 127, 0, 0, 48, 103, 80, 85, 249, 127, 0, 0, 88, 103, 80, 85, 249, 127, 0, 0, 88, 103, 80, 85, 249, 127, 0, 0, 128, 103, 80, 85, 249, 127, 0, 0, 128, 103, 80, 85, 249, 127, 0, 0, 168, 103, 80, 85, 249, 127, 0, 0, 168, 103, 80, 85, 249, 127, 0, 0, 208, 103, 80, 85, 249, 127, 0, 0, 208, 103, 80, 85, 249, 127, 0, 0, 248, 103, 80, 85, 249, 127, 0, 0, 248, 103, 80, 85, 249, 127, 0, 0, 32, 104, 80, 85, 249, 127, 0, 0, 32, 104, 80, 85, 249, 127, 0, 0, 72, 104, 80, 85, 249, 127, 0, 0, 72, 104, 80, 85, 249, 127, 0, 0, 112, 104, 80, 85, 249, 127, 0, 0, 112, 104, 80, 85, 249, 127, 0, 0, 152, 104, 80, 85, 249, 127, 0, 0, 152, 104, 80, 85, 249, 127, 0, 0, 104, 105, 80, 85, 249, 127, 0, 0, 104, 105, 80, 85, 249, 127, 0, 0, 144, 105, 80, 85, 249, 127, 0, 0, 144, 105, 80, 85, 249, 127, 0, 0, 184, 105, 80, 85, 249, 127, 0, 0, 184, 105, 80, 85, 249, 127, 0, 0, 224, 105, 80, 85, 249, 127, 0, 0, 224, 105, 80, 85, 249, 127, 0, 0, 8, 106, 80, 85, 249, 127, 0, 0, 8, 106, 80, 85, 249, 127, 0, 0, 48, 106, 80, 85, 249, 127, 0, 0, 48, 106, 80, 85, 249, 127, 0, 0, 88, 106, 80, 85, 249, 127, 0, 0, 88, 106, 80, 85, 249, 127, 0, 0, 128, 106, 80, 85, 249, 127, 0, 0, 128, 106, 80, 85, 249, 127, 0, 0, 200, 106, 80, 85, 249, 127, 0, 0, 200, 106, 80, 85, 249, 127, 0, 0, 240, 106, 80, 85, 249, 127, 0, 0, 240, 106, 80, 85, 249, 127, 0, 0, 24, 107, 80, 85, 249, 127, 0, 0, 24, 107, 80, 85, 249, 127, 0, 0, 64, 107, 80, 85, 249, 127, 0, 0, 64, 107, 80, 85, 249, 127, 0, 0, 104, 107, 80, 85, 249, 127, 0, 0, 104, 107, 80, 85, 249, 127, 0, 0, 144, 107, 80, 85, 249, 127, 0, 0, 144, 107, 80, 85, 249, 127, 0, 0, 184, 107, 80, 85, 249, 127, 0, 0, 184, 107, 80, 85, 249, 127, 0, 0, 224, 107, 80, 85, 249, 127, 0, 0, 224, 107, 80, 85, 249, 127, 0, 0, 8, 108, 80, 85, 249, 127, 0, 0, 8, 108, 80, 85, 249, 127, 0, 0, 48, 108, 80, 85, 249, 127, 0, 0, 48, 108, 80, 85, 249, 127, 0, 0, 88, 108, 80, 85, 249, 127, 0, 0, 88, 108, 80, 85, 249, 127, 0, 0, 128, 108, 80, 85, 249, 127, 0, 0, 128, 108, 80, 85, 249, 127, 0, 0, 168, 108, 80, 85, 249, 127, 0, 0, 168, 108, 80, 85, 249, 127, 0, 0, 152, 110, 80, 85, 249, 127, 0, 0, 152, 110, 80, 85, 249, 127, 0, 0, 192, 110, 80, 85, 249, 127, 0, 0, 192, 110, 80, 85, 249, 127, 0, 0, 232, 110, 80, 85, 249, 127, 0, 0, 232, 110, 80, 85, 249, 127, 0, 0, 16, 111, 80, 85, 249, 127, 0, 0, 16, 111, 80, 85, 249, 127, 0, 0, 56, 111, 80, 85, 249, 127, 0, 0, 56, 111, 80, 85, 249, 127, 0, 0, 96, 111, 80, 85, 249, 127, 0, 0, 96, 111, 80, 85, 249, 127, 0, 0, 136, 111, 80, 85, 249, 127, 0, 0, 136, 111, 80, 85, 249, 127, 0, 0, 176, 111, 80, 85, 249, 127, 0, 0, 176, 111, 80, 85, 249, 127, 0, 0, 216, 111, 80, 85, 249, 127, 0, 0, 216, 111, 80, 85, 249, 127, 0, 0, 0, 112, 80, 85, 249, 127, 0, 0, 0, 112, 80, 85, 249, 127, 0, 0, 40, 112, 80, 85, 249, 127, 0, 0, 40, 112, 80, 85, 249, 127, 0, 0, 80, 112, 80, 85, 249, 127, 0, 0, 80, 112, 80, 85, 249, 127, 0, 0, 120, 112, 80, 85, 249, 127, 0, 0, 120, 112, 80, 85, 249, 127, 0, 0, 88, 91, 80, 85, 249, 127, 0, 0, 88, 91, 80, 85, 249, 127, 0, 0, 144, 91, 80, 85, 249, 127, 0, 0, 144, 91, 80, 85, 249, 127, 0, 0, 184, 91, 80, 85, 249, 127, 0, 0, 184, 91, 80, 85, 249, 127, 0, 0, 176, 93, 80, 85, 249, 127, 0, 0, 176, 93, 80, 85, 249, 127, 0, 0, 216, 93, 80, 85, 249, 127, 0, 0, 216, 93, 80, 85, 249, 127, 0, 0, 0, 94, 80, 85, 249, 127, 0, 0, 0, 94, 80, 85, 249, 127, 0, 0, 40, 94, 80, 85, 249, 127, 0, 0, 40, 94, 80, 85, 249, 127, 0, 0, 128, 94, 80, 85, 249, 127, 0, 0, 128, 94, 80, 85, 249, 127, 0, 0, 168, 94, 80, 85, 249, 127, 0, 0, 168, 94, 80, 85, 249, 127, 0, 0, 208, 94, 80, 85, 249, 127, 0, 0, 208, 94, 80, 85, 249, 127, 0, 0, 120, 98, 80, 85, 249, 127, 0, 0, 120, 98, 80, 85, 249, 127, 0, 0, 224, 98, 80, 85, 249, 127, 0, 0, 224, 98, 80, 85, 249, 127, 0, 0, 40, 99, 80, 85, 249, 127, 0, 0, 40, 99, 80, 85, 249, 127, 0, 0, 112, 99, 80, 85, 249, 127, 0, 0, 112, 99, 80, 85, 249, 127, 0, 0, 54, 0, 0, 54, 96, 148, 17, 0, 176, 252, 123, 251, 100, 2, 0, 0, 160, 212, 156, 93, 100, 2, 0, 0];
        ////Span<byte> pixelBufferBytes = [80, 1, 185, 91, 36, 2, 0, 0, 96, 241, 173, 250, 100, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 15, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 7, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 0, 0, 0, 3, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 7, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 154, 21, 124, 189, 64, 58, 32, 189, 131, 81, 127, 191, 197, 131, 127, 191, 146, 19, 30, 59, 5, 228, 123, 61, 0, 0, 0, 0, 167, 205, 127, 63, 39, 136, 32, 189, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 63, 57, 151, 2, 62, 224, 190, 134, 62, 193, 2, 76, 63, 0, 0, 112, 65, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 0, 0, 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 63, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        //vertBufferBytes.CopyTo(vertexConstantBuffer);
        ////pixelBufferBytes.CopyTo(pixelConstantBuffer);
        //if (InstancedFullPath is "BIOG_HMM_HED_PROMorph.Sheppard.HMM_HED_PROSheppard_Face_Mat_1a")
        //{
        //    vertexConstantBuffer.Slice(672).Clear();
        //}
        vertexConstantBuffer.Clear();
        pixelConstantBuffer.Clear();
        UnrealVertexShader?.WriteValues(vertexConstantBuffer, context, mesh, this);
        UnrealPixelShader?.WriteValues(pixelConstantBuffer, context, mesh, this);
        //System.Diagnostics.Debug.WriteLine(string.Join(',', vertexConstantBuffer.ToArray()));
        //System.Diagnostics.Debug.WriteLine(string.Join(',', pixelConstantBuffer.ToArray()));
    }

    public (List<Vector4> scalar, List<Vector4> vector) GetCachedVertexParameters(MeshRenderContext context)
    {
        UpdateUniformVertexParameters(context);
        return (CachedVertexScalarParameters, CachedVertexVectorParameters);
    }

    public (List<Vector4> scalar, List<Vector4> vector, 
        List<PreviewTextureCache.TextureEntry> tex2d, List<PreviewTextureCache.TextureEntry> cube)
        GetCachedPixelParameters(MeshRenderContext context)
    {
        UpdateUniformPixelParameters(context);
        return (CachedPixelScalarParameters, CachedPixelVectorParameters, CachedTexture2DParameters, CachedCubeTextureParameters);
    }

    private void UpdateUniformVertexParameters(MeshRenderContext context)
    {
        if (CachedVertexFrameNumber == context.NumFrames) return;
        CachedVertexFrameNumber = context.NumFrames;
        CachedVertexScalarParameters.Clear();
        CachedVertexVectorParameters.Clear();

        var uniformContext = new UniformExpressionRenderContext(
            ScalarParameterValues, VectorParameterValues, 
            context.Time, context.Time, GetFlipBookTextureOffset);

        UpdateExpressions(uniformContext,
            ShaderMap.UniformVertexVectorExpressions, ShaderMap.UniformVertexScalarExpressions,
            CachedVertexScalarParameters, CachedVertexVectorParameters);
    }

    private void UpdateUniformPixelParameters(MeshRenderContext context)
    {
        if (CachedPixelFrameNumber == context.NumFrames) return;
        CachedPixelFrameNumber = context.NumFrames;
        CachedPixelScalarParameters.Clear();
        CachedPixelVectorParameters.Clear();
        CachedTexture2DParameters.Clear();
        CachedCubeTextureParameters.Clear();

        var uniformContext = new UniformExpressionRenderContext(
            ScalarParameterValues, VectorParameterValues, 
            context.Time, context.Time, GetFlipBookTextureOffset);

        UpdateExpressions(uniformContext,
            ShaderMap.UniformPixelVectorExpressions, ShaderMap.UniformPixelScalarExpressions,
            CachedPixelScalarParameters, CachedPixelVectorParameters);

        UpdateTextureExpressions(ShaderMap.Uniform2DTextureExpressions, CachedTexture2DParameters);
        UpdateTextureExpressions(ShaderMap.UniformCubeTextureExpressions, CachedCubeTextureParameters);
    }

    private LinearColor GetFlipBookTextureOffset(UniformExpressionRenderContext context, int texIndex)
    {
        if ((uint)texIndex < Uniform2DTextureExpressions.Count 
            && Uniform2DTextureExpressions[texIndex] is { } texifp
            && TextureMap.TryGetValue(texifp, out var texture)
            && texture is PreviewTextureCache.FlipBookTextureEntry flipBookTexture)
        {
            return flipBookTexture.GetTextureOffset(context);
        }
        return LinearColor.Black;
    }

    private void UpdateTextureExpressions(MaterialUniformExpressionTexture[] textureExpressions, List<PreviewTextureCache.TextureEntry> textureCache)
    {
        foreach (MaterialUniformExpressionTexture texExpression in textureExpressions)
        {
            PreviewTextureCache.TextureEntry texture = null;
            // The human-lash shader serializes its opacity texture as a plain uniform expression. Meshplorer
            // deliberately resolves that slot from the effective child-MIC parameter instead of the master's
            // default lash texture, which otherwise produces an opaque white shell around the eyes.
            if (IsHumanLashMaterial
                && TextureParameterValues.TryGetValue(HUMAN_LASH_OPACITY_PARAMETER_NAME, out string lashTextureIfp))
            {
                TextureMap.TryGetValue(lashTextureIfp, out texture);
                textureCache.Add(texture);
                continue;
            }
            switch (texExpression)
            {
                case MaterialUniformExpressionTextureParameter texParamExpression:
                    if (TextureParameterValues.TryGetValue(texParamExpression.ParameterName.Instanced, out string texIfp)
                        && texIfp is not null)
                    {
                        TextureMap.TryGetValue(texIfp, out texture);
                    }
                    break;
                default:
                    if ((uint)texExpression.TextureIndex < Uniform2DTextureExpressions.Count
                        && Uniform2DTextureExpressions[texExpression.TextureIndex] is {} texifp)
                    {
                        TextureMap.TryGetValue(texifp, out texture);
                    }
                    break;
            }
            textureCache.Add(texture);
        }
    }

    public bool HasRequiredTextures(MeshRenderContext context)
    {
        if (!IsHumanLashMaterial)
        {
            return true;
        }

        (_, _, List<PreviewTextureCache.TextureEntry> textures, _) = GetCachedPixelParameters(context);
        return textures.Count > 0 && textures.All(texture => texture is not null);
    }

    private void UpdateExpressions(UniformExpressionRenderContext uniformContext, 
        MaterialUniformExpression[] vectorExpressions, MaterialUniformExpression[] scalarExpressions, 
        List<Vector4> scalarCache, List<Vector4> vectorCache)
    {
        if (Game is MEGame.LE3)
        {
            // LE3 packs four scalar expressions into each float4 shader parameter.
            var enumerator = scalarExpressions.ChunkBySpan(4);
            foreach (ReadOnlySpan<MaterialUniformExpression> scalarExpression in enumerator)
            {
                LinearColor xVal = default;
                LinearColor yVal = default;
                LinearColor zVal = default;
                LinearColor wVal = default;
                scalarExpression[0].GetNumberValue(uniformContext, ref xVal);
                scalarExpression[1].GetNumberValue(uniformContext, ref yVal);
                scalarExpression[2].GetNumberValue(uniformContext, ref zVal);
                scalarExpression[3].GetNumberValue(uniformContext, ref wVal);
                scalarCache.Add(new Vector4(xVal.R, yVal.R, zVal.R, wVal.R));
            }
            if (enumerator.Current is { Length: > 0 } remainder)
            {
                LinearColor xVal = default;
                LinearColor yVal = default;
                LinearColor zVal = default;
                LinearColor wVal = default;
                remainder[0].GetNumberValue(uniformContext, ref xVal);
                if (remainder.Length > 1)
                {
                    remainder[1].GetNumberValue(uniformContext, ref yVal);
                    if (remainder.Length > 2)
                    {
                        remainder[2].GetNumberValue(uniformContext, ref zVal);
                        if (remainder.Length > 3)
                        {
                            remainder[3].GetNumberValue(uniformContext, ref wVal);
                        }
                    }
                }
                scalarCache.Add(new Vector4(xVal.R, yVal.R, zVal.R, wVal.R));
            }
        }
        else
        {
            // Every other game serializes scalar uniforms as individual 4-byte parameters.
            // Their shader indices address the uncompressed scalar-expression array directly.
            foreach (MaterialUniformExpression scalarExpression in scalarExpressions)
            {
                LinearColor value = default;
                scalarExpression.GetNumberValue(uniformContext, ref value);
                scalarCache.Add(new Vector4(value.R, 0, 0, 0));
            }
        }
        foreach (MaterialUniformExpression vectorExpression in vectorExpressions)
        {
            LinearColor val = default;
            vectorExpression.GetNumberValue(uniformContext, ref val);
            vectorCache.Add((Vector4)val);
        }
    }
}

/// <summary>
/// Runtime values consumed by UE3 particle vertex factories in addition to the per-vertex streams.
/// Defaults model an unlocked, camera-facing sprite with camera-facing normals.
/// </summary>
public sealed class ParticleVertexFactoryRenderParameters
{
    public float ScreenAlignment;
    public float NormalsType;
    public Vector4 NormalsSphereCenter;
    public Vector4 NormalsCylinderUnitDirection = new(Vector3.UnitZ, 0);
    public float AxisRotationVectorSourceIndex;
    public Fixed2<Vector4> AxisRotationVectors;
    public Vector3 ParticleUpRightResultScalars = Vector3.UnitX;
    public float NumVerticesPerInstance = 1;
    public Vector4 InstancedPreViewTranslation;
}
