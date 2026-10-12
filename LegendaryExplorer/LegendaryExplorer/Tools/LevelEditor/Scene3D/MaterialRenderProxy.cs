using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using LegendaryExplorerCore.Unreal.Classes;
using SharpDX.Direct3D11;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorer.UserControls.SharedToolControls;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

//update the name strings too
using PixelShaderType = TBasePassPixelShader<FNullPolicy>;
using VertexShaderType = TBasePassVertexShader<FNullPolicy, FNullPolicy>;
public class MaterialRenderProxy : MaterialInstanceConstantLevelEditor, ILiveMaterialRenderProxy
{
    public const string LocalVertexFactoryType = "FLocalVertexFactory";
    private const string VERTEX_SHADER_TYPE_NAME = "TBasePassVertexShaderFNoLightMapPolicyFNoDensityPolicy";
    private const string LIT_PIXEL_SHADER_TYPE_NAME = "TBasePassPixelShaderFNoLightMapPolicySkyLight";
    private const string UNLIT_PIXEL_SHADER_TYPE_NAME = "TBasePassPixelShaderFNoLightMapPolicyNoSkyLight";
    private const string HUMAN_LASH_MASTER_MATERIAL_NAME = "HMN_HED_LASH_Unlit_MASTER_MAT";
    private const string HUMAN_LASH_OPACITY_PARAMETER_NAME = "HED_Lash_Diff";
    internal const string VERTEX_FACTORY_TYPE_NAME = "FLocalVertexFactory";

    /// <summary>
    /// A base pass vertex shader and pixel shader that render together. Which pair the game uses is determined by the light-map policy.
    /// </summary>
    private readonly record struct BasePassShaderTypes(string VertexShaderType, string PixelShaderType);

    /// <summary>
    /// Light-map policies that render one directional light plus sky lighting (see <see cref="PreviewLighting"/>), in order of preference
    /// </summary>
    private static readonly BasePassShaderTypes[] LitShaderTypes =
    [
        new("TBasePassVertexShaderFDirectionalLightLightMapPolicyFNoDensityPolicy", "TBasePassPixelShaderFDirectionalLightLightMapPolicySkyLight"),
        //SH light is the directional light plus an SH ambient term. For preview lighting the SH is left at 0, since the sky provides ambient; light environments set it
        new("TBasePassVertexShaderFSHLightLightMapPolicyFNoDensityPolicy", "TBasePassPixelShaderFSHLightLightMapPolicySkyLight"),
    ];

    /// <summary>
    /// Light-map policies with no direct light, in order of preference. Unlit materials only have these
    /// </summary>
    private static readonly BasePassShaderTypes[] UnlitShaderTypes =
    [
        new("TBasePassVertexShaderFNoLightMapPolicyFNoDensityPolicy", "TBasePassPixelShaderFNoLightMapPolicySkyLight"),
        new("TBasePassVertexShaderFNoLightMapPolicyFNoDensityPolicy", "TBasePassPixelShaderFNoLightMapPolicyNoSkyLight"),
    ];

    /// <summary>
    /// The light-map policy the game renders each type of static light-map with. BioWare added LMT_3 to LMT_6
    /// </summary>
    private static string GetLightMapPolicyName(ELightMapType lightMapType) => lightMapType switch
    {
        ELightMapType.LMT_1D => "FDirectionalVertexLightMapPolicy",
        ELightMapType.LMT_2D => "FDirectionalLightMapTexturePolicy",
        ELightMapType.LMT_3 => "FCustomVectorVertexLightMapPolicy",
        ELightMapType.LMT_4 => "FCustomVectorLightMapTexturePolicy",
        ELightMapType.LMT_5 => "FCustomSimpleVertexLightMapPolicy",
        ELightMapType.LMT_6 => "FCustomSimpleLightMapTexturePolicy",
        _ => null
    };

    /// <summary>
    /// Shaders that render a static light-map, in order of preference. The sky light is only for primitives the game lights with a dynamic sky light,
    /// so the variant without it is preferred.
    /// </summary>
    private static BasePassShaderTypes[] GetLightMapShaderTypes(ELightMapType lightMapType)
    {
        if (GetLightMapPolicyName(lightMapType) is not string policy)
        {
            return [];
        }
        string vertexShaderType = $"TBasePassVertexShader{policy}FNoDensityPolicy";
        return
        [
            new(vertexShaderType, $"TBasePassPixelShader{policy}NoSkyLight"),
            new(vertexShaderType, $"TBasePassPixelShader{policy}SkyLight"),
        ];
    }

    /// <summary>
    /// Every shader type that <see cref="SelectShaders"/> can choose from, for primitives without a static light-map
    /// </summary>
    internal static readonly string[] ShaderTypeNames = GetShaderTypeNames(ELightMapType.LMT_None);

    private static string[] GetShaderTypeNames(ELightMapType lightMapType) => GetLightMapShaderTypes(lightMapType).Concat(LitShaderTypes).Concat(UnlitShaderTypes)
        .SelectMany(types => new[] { types.VertexShaderType, types.PixelShaderType })
        .Distinct().ToArray();

    /// <summary>
    /// Picks the vertex and pixel shader to render with. Lit materials use a lit pair if they have one, otherwise they fall back to an unlit pair.
    /// </summary>
    /// <param name="shaders">A material's shaders. Nulls are ignored</param>
    /// <returns>Nulls if the material has no usable pair</returns>
    internal static (Shader vertexShader, Shader pixelShader) SelectShaders(IEnumerable<Shader> shaders, bool isUnlit) =>
        SelectShaders(shaders, isUnlit, ELightMapType.LMT_None, out _);

    /// <summary>
    /// Picks the vertex and pixel shader to render with. Lit materials use the shaders for <paramref name="lightMapType"/> if they have them,
    /// then a dynamically lit pair, then an unlit pair.
    /// </summary>
    /// <param name="shaders">A material's shaders. Nulls are ignored</param>
    /// <param name="usesLightMap">Whether the chosen shaders render the light-map</param>
    /// <returns>Nulls if the material has no usable pair</returns>
    internal static (Shader vertexShader, Shader pixelShader) SelectShaders(IEnumerable<Shader> shaders, bool isUnlit, ELightMapType lightMapType, out bool usesLightMap)
    {
        var shadersByType = new Dictionary<string, Shader>();
        foreach (Shader shader in shaders)
        {
            if (shader is not null)
            {
                shadersByType.TryAdd(shader.ShaderType.Name, shader);
            }
        }

        //Unlit materials ignore light-maps
        BasePassShaderTypes[] lightMapShaderTypes = isUnlit ? [] : GetLightMapShaderTypes(lightMapType);
        IEnumerable<BasePassShaderTypes> candidates = isUnlit ? UnlitShaderTypes : lightMapShaderTypes.Concat(LitShaderTypes).Concat(UnlitShaderTypes);
        foreach (BasePassShaderTypes types in candidates)
        {
            if (shadersByType.TryGetValue(types.VertexShaderType, out Shader vertexShader)
                && shadersByType.TryGetValue(types.PixelShaderType, out Shader pixelShader))
            {
                usesLightMap = lightMapShaderTypes.Contains(types);
                return (vertexShader, pixelShader);
            }
        }
        usesLightMap = false;
        return (null, null);
    }

    public EBlendMode BlendMode;
    public bool UseHairPass;
    public bool CastLitTranslucencyShadowAsMasked;
    public bool IsUnlit;
    public bool IsHumanLashMaterial;
    public bool HasFrameDependentUniforms { get; private set; }
    private readonly Dictionary<string, float> ScalarParameterValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LinearColor> VectorParameterValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> TextureParameterValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IEntry> TextureParameterEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> DefaultTextureParameterNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (bool Exists, float Value)> PreviewScalarBaselines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (bool Exists, LinearColor Value)> PreviewVectorBaselines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (bool Exists, string Value)> PreviewTextureBaselines = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Backfaces aren't culled
    /// </summary>
    public bool IsTwoSided;

    // materials that displace vertices disqualify the mesh from culling
    public bool HasUndisplacedVertices { get; private set; }

    /// <summary>
    /// The type of static light-map the mesh this material is on has. Set by <see cref="QueueGameShaderLoad"/>, since it determines which shaders are loaded
    /// </summary>
    public ELightMapType LightMapType { get; private set; }

    /// <summary>
    /// Whether <see cref="UnrealVertexShader"/> and <see cref="UnrealPixelShader"/> render a static light-map (of type <see cref="LightMapType"/>).
    /// If false, they're lit by <see cref="PreviewLighting"/> instead.
    /// </summary>
    public bool UsesLightMap { get; private set; }
    private readonly List<string> Uniform2DTextureExpressions = [];
    public Dictionary<string, PreviewTextureCache.TextureEntry> TextureMap;
    private MaterialShaderMap ShaderMap;
    private MaterialUniformExpression[] PixelVectorExpressions = [];
    private MaterialUniformExpression[] PixelScalarExpressions = [];
    private MaterialUniformExpressionTexture[] Pixel2DTextureExpressions = [];
    private MaterialUniformExpressionTexture[] PixelCubeTextureExpressions = [];
    private MaterialUniformExpression[] VertexVectorExpressions = [];
    private MaterialUniformExpression[] VertexScalarExpressions = [];
    private uint CachedPixelFrameNumber = uint.MaxValue;
    private uint CachedVertexFrameNumber = uint.MaxValue;
    private readonly List<Vector4> CachedVertexScalarParameters = [];
    private readonly List<Vector4> CachedVertexVectorParameters = [];
    private readonly List<Vector4> CachedPixelScalarParameters = [];
    private readonly List<Vector4> CachedPixelVectorParameters = [];
    private readonly List<PreviewTextureCache.TextureEntry> CachedTexture2DParameters = [];
    private readonly List<PreviewTextureCache.TextureEntry> CachedCubeTextureParameters = [];
    // A non-static MIC owns parameter overrides but no cooked shader resource. In that case the base material
    // reached by MaterialInstanceConstantLevelEditor is the shader owner. Static MICs replace this with their
    // own permutation resource when bHasStaticPermutationResource is true.
    private ExportEntry ShaderOwner;
    private MaterialResource ShaderOwnerResource;

    /// <summary>
    /// A TBasePassVertexShader. See <see cref="SelectShaders"/>
    /// </summary>
    public Shader UnrealVertexShader;
    /// <summary>
    /// A TBasePassPixelShader. See <see cref="SelectShaders"/>
    /// </summary>
    public Shader UnrealPixelShader;

    /// <summary>
    /// For lit materials, base pass shaders without any lighting (FNoLightMapPolicy). The game uses these for meshes without a light-map,
    /// which the level's lights are then rendered on in separate passes. Also used for the Unlit viewport mode.
    /// </summary>
    public Shader NoLightMapVertexShader;
    /// <inheritdoc cref="NoLightMapVertexShader"/>
    public Shader NoLightMapPixelShader;

    /// <summary>
    /// The variant of <see cref="UnrealPixelShader"/> that also adds sky lighting, for meshes the level's sky lights aren't baked into the light-map of.
    /// Null if <see cref="UsesLightMap"/> is false, or the material doesn't have it.
    /// </summary>
    public Shader LightMapSkyLightPixelShader;

    //Always excludes baked lighting, even for materials whose default pair uses a light-map.
    public Shader PreviewVertexShader;
    public Shader PreviewPixelShader;

    /// <summary>
    /// The base pass shaders with a directional light and an SH light (FSHLightLightMapPolicy), which light environments with an SH light render with.
    /// Null if the material doesn't have them
    /// </summary>
    public Shader SHLightVertexShader;
    /// <inheritdoc cref="SHLightVertexShader"/>
    public Shader SHLightPixelShader;

    /// <summary>
    /// The base pass shaders for a mesh lit by a light environment: its directional light, plus its SH light or sky light
    /// </summary>
    public (Shader vertexShader, Shader pixelShader) GetLightEnvironmentShaders(MeshRenderContext context, LightEnvironmentLighting lighting)
    {
        if (context.IsUnlit || IsUnlit)
            return (NoLightMapVertexShader, NoLightMapPixelShader);
        if (lighting.UsesSHLight && SHLightVertexShader is not null && SHLightPixelShader is not null)
            return (SHLightVertexShader, SHLightPixelShader);
        return (PreviewVertexShader, PreviewPixelShader);
    }

    /// <summary>
    /// Selects a cached base-pass pair without changing the material or reloading the level.
    /// Missing variants return null so the caller can use the preview shader for this draw only.
    /// </summary>
    /// <param name="hasSkyLighting">Whether the level's sky lights add to the mesh's lighting (see <see cref="MeshStaticLighting.GetSkyLighting"/>)</param>
    public (Shader vertexShader, Shader pixelShader) GetBasePassShaders(MeshRenderContext context, bool usesLevelLights, out bool usePreviewLighting, bool hasSkyLighting = false)
    {
        usePreviewLighting = false;
        if (context.IsUnlit || IsUnlit)
            return (NoLightMapVertexShader, NoLightMapPixelShader);
        if (context.AreLightMapsActive && UsesLightMap)
            return (UnrealVertexShader, hasSkyLighting && LightMapSkyLightPixelShader is not null ? LightMapSkyLightPixelShader : UnrealPixelShader);
        if (!context.IsDynamicLightingActive || usesLevelLights)
            return (NoLightMapVertexShader, NoLightMapPixelShader);
        usePreviewLighting = true;
        return (PreviewVertexShader, PreviewPixelShader);
    }

    //Light shaders are loaded as they're needed, since there are many combinations of light type and shadowing, and most are never used.
    private ExportEntry LightShaderMapOwner;
    private readonly ConcurrentDictionary<(SceneLightType, StaticShadowingType), (Shader, Shader)> LightShaders = new();

    //The material chain is read from most to least derived. The first Material, or MaterialInstance with a StaticPermutationResource,
    //owns the shaders the game will use. Everything after that point only contributes parameter values.
    private bool FoundShaderMapOwner;
    private ExportEntry ShaderMapOwner;
    //Shaders aren't loaded in the constructor, since that's expensive and they aren't needed if game shaders are never turned on.
    //See MeshRenderContext.LoadPendingGameShaders
    private volatile bool AttemptedShaderLoad;
    private readonly object ShaderLoadLock = new();
    private readonly MeshRenderContext Context;

    private string gameShaderError;
    /// <summary>
    /// Why this material can't be rendered with the game's shaders. Null if it can. Loads the shaders if they haven't been already.
    /// </summary>
    public string GameShaderError
    {
        get
        {
            LoadGameShaders();
            return gameShaderError;
        }
    }

    /// <summary>
    /// Loads the shaders if they haven't been already.
    /// </summary>
    public bool CanRenderWithGameShaders => GameShaderError is null;
    public string VertexFactoryType { get; private set; } = LocalVertexFactoryType;
    public ParticleVertexFactoryRenderParameters ParticleFactoryParameters { get; } = new();
    public float LensFlareOcclusion { get; set; } = 1f;
    public Vector3 LensFlareCameraPosition { get; set; }
    public ExportEntry MaterialExport { get; }
    public bool UseSrgbColorManagement { get; }

    // The fork's generic VFX draw API preserves its optional color management. The selected
    // mesh/light shader APIs render into upstream's linear HDR target and always use linear views.
    internal bool UsesLinearTextureSampling { get; private set; } = true;

    /// <summary>
    /// Effective scalar parameters used by the preview, including inherited defaults and instance overrides.
    /// </summary>
    public IReadOnlyDictionary<string, float> ScalarParameters => ScalarParameterValues;

    /// <summary>
    /// Effective vector parameters used by the preview, including inherited defaults and instance overrides.
    /// </summary>
    public IReadOnlyDictionary<string, LinearColor> VectorParameters => VectorParameterValues;

    public MaterialRenderProxy(MeshRenderContext context, ExportEntry export,
        MaterialRenderProxy parameterSource = null, bool useSrgbColorManagement = false)
        : this(context, export, parameterSource, useSrgbColorManagement, ELightMapType.LMT_None)
    {
    }

    public MaterialRenderProxy(MeshRenderContext context, ExportEntry export,
        MaterialRenderProxy parameterSource, bool useSrgbColorManagement, ELightMapType lightMapType)
        : base(export, context.PackageCache, true)
    {
        Context = context;
        MaterialExport = export;
        UseSrgbColorManagement = useSrgbColorManagement;
        LightMapType = lightMapType;
        LoadGameShaders();
        if (parameterSource is not null)
        {
            CopyEffectiveParametersFrom(parameterSource);
        }
    }

    public MaterialRenderProxy(MeshRenderContext context, ExportEntry export, string vertexFactoryType)
        : base(export, context.PackageCache, true)
    {
        Context = context;
        MaterialExport = export;
        VertexFactoryType = string.IsNullOrWhiteSpace(vertexFactoryType)
            ? LocalVertexFactoryType
            : vertexFactoryType;
        // ReadBaseMaterial runs during the base constructor, before a derived constructor can select its
        // particle factory. Reload from the actual shader owner rather than blindly parsing the selected MIC.
        // Non-static MICs have no serialized MaterialInstance binary and must inherit their parent's shaders.
        LoadShaders(ShaderMapOwner ?? ShaderOwner ?? export, ShaderOwnerResource);
        AttemptedShaderLoad = true;
    }

    public void SetScalarParameter(string parameterName, float value)
    {
        PreviewScalarBaselines.TryAdd(parameterName, ScalarParameterValues.TryGetValue(parameterName, out float baseline)
            ? (true, baseline)
            : (false, default));
        ScalarParameterValues[parameterName] = value;
        CachedPixelFrameNumber = CachedVertexFrameNumber = uint.MaxValue;
    }

    public void SetVectorParameter(string parameterName, LinearColor value)
    {
        PreviewVectorBaselines.TryAdd(parameterName, VectorParameterValues.TryGetValue(parameterName, out LinearColor baseline)
            ? (true, baseline)
            : (false, default));
        VectorParameterValues[parameterName] = value;
        CachedPixelFrameNumber = CachedVertexFrameNumber = uint.MaxValue;
    }

    public void RemoveScalarParameter(string parameterName)
    {
        PreviewScalarBaselines.TryAdd(parameterName, ScalarParameterValues.TryGetValue(parameterName, out float baseline)
            ? (true, baseline)
            : (false, default));
        ScalarParameterValues.Remove(parameterName);
        CachedPixelFrameNumber = CachedVertexFrameNumber = uint.MaxValue;
    }

    public void RemoveVectorParameter(string parameterName)
    {
        PreviewVectorBaselines.TryAdd(parameterName, VectorParameterValues.TryGetValue(parameterName, out LinearColor baseline)
            ? (true, baseline)
            : (false, default));
        VectorParameterValues.Remove(parameterName);
        CachedPixelFrameNumber = CachedVertexFrameNumber = uint.MaxValue;
    }

    public void SetTextureParameter(string parameterName, string texturePath, PreviewTextureCache.TextureEntry texture)
    {
        PreviewTextureBaselines.TryAdd(parameterName, TextureParameterValues.TryGetValue(parameterName, out string baseline)
            ? (true, baseline)
            : (false, default));
        if (string.IsNullOrEmpty(texturePath) || texture is null)
        {
            TextureParameterValues.Remove(parameterName);
        }
        else
        {
            TextureMap[texturePath] = texture;
            TextureParameterValues[parameterName] = texturePath;
        }
        CachedPixelFrameNumber = uint.MaxValue;
    }

    public void CommitPreviewParameterOverrides()
    {
        PreviewScalarBaselines.Clear();
        PreviewVectorBaselines.Clear();
        PreviewTextureBaselines.Clear();
    }

    public void ResetPreviewParameterOverrides()
    {
        foreach ((string name, (bool exists, float value)) in PreviewScalarBaselines)
        {
            if (exists) ScalarParameterValues[name] = value;
            else ScalarParameterValues.Remove(name);
        }
        foreach ((string name, (bool exists, LinearColor value)) in PreviewVectorBaselines)
        {
            if (exists) VectorParameterValues[name] = value;
            else VectorParameterValues.Remove(name);
        }
        foreach ((string name, (bool exists, string value)) in PreviewTextureBaselines)
        {
            if (exists) TextureParameterValues[name] = value;
            else TextureParameterValues.Remove(name);
        }
        PreviewScalarBaselines.Clear();
        PreviewVectorBaselines.Clear();
        PreviewTextureBaselines.Clear();
        CachedPixelFrameNumber = CachedVertexFrameNumber = uint.MaxValue;
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
        foreach ((string name, IEntry value) in source.TextureParameterEntries)
        {
            TextureParameterEntries[name] = value;
        }
        Textures.UnionWith(source.Textures);
        CachedPixelFrameNumber = CachedVertexFrameNumber = uint.MaxValue;
    }

    /// <summary>
    /// Queues this material's shaders to be loaded by <see cref="MeshRenderContext.LoadPendingGameShaders"/>.
    /// (Otherwise they're loaded the first time they're needed.)
    /// </summary>
    /// <param name="lightMapType">The type of static light-map the mesh this material is on has</param>
    public void QueueGameShaderLoad(ELightMapType lightMapType)
    {
        if (LightMapType != lightMapType)
        {
            LightMapType = lightMapType;
            gameShaderError = null;
            AttemptedShaderLoad = false;
            ShaderMapOwner = ShaderOwner;
        }
        if (!AttemptedShaderLoad)
        {
            Context.AddPendingGameShaderLoad(this);
        }
    }

    /// <summary>
    /// Call if rendering with game shaders fails, so that this material falls back to the LEX shader
    /// </summary>
    public void MarkGameShadersFailed(Exception e)
    {
        gameShaderError = e.Message;
    }

    /// <summary>
    /// Loads the game's shaders for this material, if they haven't been already. Thread-safe.
    /// </summary>
    public void LoadGameShaders()
    {
        if (AttemptedShaderLoad) return;
        lock (ShaderLoadLock)
        {
            if (AttemptedShaderLoad) return;
            if (gameShaderError is null)
            {
                if (ShaderMapOwner is null)
                {
                    gameShaderError = "Could not find the Material that owns this material's shaders";
                }
                else
                {
                    LoadShaders(ShaderMapOwner, ShaderOwnerResource);
                    LightShaderMapOwner = ShaderMapOwner;
                    ShaderMapOwner = null;
                    if (gameShaderError is null && (ShaderMap is null || UnrealVertexShader is null || UnrealPixelShader is null))
                    {
                        gameShaderError = "Could not find the material's shaders";
                    }
                }
            }
            //set last, so that other threads don't see a partially loaded material
            AttemptedShaderLoad = true;
        }
    }

    protected override void ReadBaseMaterial(ExportEntry mat, PackageCache assetCache, Material parsedMaterial)
    {
        base.ReadBaseMaterial(mat, assetCache, parsedMaterial);

        var props = mat.GetProperties(packageCache: assetCache);
        Enum.TryParse(props.GetProp<EnumProperty>("BlendMode")?.Value ?? "BLEND_Opaque", out BlendMode);

        //if a MIC had a StaticPermutationResource, the shaders came from that instead
        bool isShaderMapOwner = !FoundShaderMapOwner;
        if (isShaderMapOwner)
        {
            HasUndisplacedVertices = !parsedMaterial.SM3MaterialResource.bUsesMaterialVertexPositionOffset;
            FoundShaderMapOwner = true;
            foreach (int uIndex in parsedMaterial.SM3MaterialResource.UniformExpressionTextures)
            {
                Uniform2DTextureExpressions.Add(mat.FileRef.GetEntry(uIndex)?.InstancedFullPath);
            }
        }

        UseHairPass = props.GetProp<BoolProperty>("bHairPass") is { Value: true };
        CastLitTranslucencyShadowAsMasked = props.GetProp<BoolProperty>("bCastLitTranslucencyShadowAsMasked") is { Value: true };
        IsTwoSided = props.GetProp<BoolProperty>("TwoSided") is { Value: true };
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
                    else if (expressionProps.GetProp<ObjectProperty>("Texture") is {} textureProp
                             && mat.FileRef.GetEntry(textureProp.Value) is {} texEntry)
                    {
                        string parameterName = paramNameProp.Value.Instanced;
                        RegisterDefaultTextureParameter(texEntry, parameterName);
                        if (!TextureParameterValues.ContainsKey(parameterName))
                        {
                            Textures.Add(texEntry);
                            TextureParameterValues.Add(parameterName, texEntry.InstancedFullPath);
                            TextureParameterEntries.TryAdd(parameterName, texEntry);
                        }
                    }
                }
            }
        }

        if (isShaderMapOwner)
        {
            ShaderMapOwner = mat;
            ShaderOwner = mat;
            ShaderOwnerResource = parsedMaterial.SM3MaterialResource;
        }
    }

    private void LoadShaders(ExportEntry mat, MaterialResource legacyMaterialResource = null)
    {
        try
        {
        string factoryType = VertexFactoryType ?? LocalVertexFactoryType;
        (ShaderMap, Shader[] shaders) = ShaderCacheManipulator.GetMaterialShaderMapAndShadersForVertexFactory(
            mat, factoryType, Context.GetSeekFreeShaderCache, GetShaderTypeNames(LightMapType));

        ShaderOwner = mat;
        ShaderOwnerResource = legacyMaterialResource;

        if (mat.Game < MEGame.ME3)
        {
            // ME1 and ME2 store uniform expressions on the material resource. Their shader maps contain
            // only shader references, so reading expressions from the map drops the actor's parameters.
            PixelVectorExpressions = legacyMaterialResource?.UniformPixelVectorExpressions ?? [];
            PixelScalarExpressions = legacyMaterialResource?.UniformPixelScalarExpressions ?? [];
            Pixel2DTextureExpressions = legacyMaterialResource?.Uniform2DTextureExpressions ?? [];
            PixelCubeTextureExpressions = legacyMaterialResource?.UniformCubeTextureExpressions ?? [];
            VertexVectorExpressions = [];
            VertexScalarExpressions = [];
        }
        else
        {
            PixelVectorExpressions = ShaderMap.UniformPixelVectorExpressions ?? [];
            PixelScalarExpressions = ShaderMap.UniformPixelScalarExpressions ?? [];
            Pixel2DTextureExpressions = ShaderMap.Uniform2DTextureExpressions ?? [];
            PixelCubeTextureExpressions = ShaderMap.UniformCubeTextureExpressions ?? [];
            VertexVectorExpressions = ShaderMap.UniformVertexVectorExpressions ?? [];
            VertexScalarExpressions = ShaderMap.UniformVertexScalarExpressions ?? [];
        }

        HasFrameDependentUniforms = VertexScalarExpressions
                                        .Concat(VertexVectorExpressions)
                                        .Concat(PixelScalarExpressions)
                                        .Concat(PixelVectorExpressions)
                                        .Concat(Pixel2DTextureExpressions)
                                        .Concat(PixelCubeTextureExpressions)
                                        .Any(expression => !expression.IsNotFrameDependent);

        foreach (MaterialUniformExpression expression in VertexScalarExpressions
                     .Concat(VertexVectorExpressions)
                     .Concat(PixelScalarExpressions)
                     .Concat(PixelVectorExpressions))
        {
            AddUniformExpressionParameters(expression);
        }

            (UnrealVertexShader, UnrealPixelShader) = SelectShaders(shaders, IsUnlit, LightMapType, out bool usesLightMap);
            UsesLightMap = usesLightMap;
            if (usesLightMap)
            {
                string skyLightPixelShaderType = $"TBasePassPixelShader{GetLightMapPolicyName(LightMapType)}SkyLight";
                LightMapSkyLightPixelShader = shaders.FirstOrDefault(shader => shader?.ShaderType.Name == skyLightPixelShaderType);
            }
            (NoLightMapVertexShader, NoLightMapPixelShader) = SelectShaders(shaders, isUnlit: true);
            (PreviewVertexShader, PreviewPixelShader) = SelectShaders(shaders, IsUnlit);
            BasePassShaderTypes shLightTypes = LitShaderTypes[1];
            SHLightVertexShader = shaders.FirstOrDefault(shader => shader?.ShaderType.Name == shLightTypes.VertexShaderType);
            SHLightPixelShader = shaders.FirstOrDefault(shader => shader?.ShaderType.Name == shLightTypes.PixelShaderType);
        }
        catch (Exception e)
        {
            ShaderMap = null;
            UnrealVertexShader = null;
            UnrealPixelShader = null;
            gameShaderError = $"Failed to load shaders for {mat.InstancedFullPath}: {e.Message}";
        }
    }

    /// <summary>
    /// Gets the shaders that render a light on this material in its own pass (TLightVertexShader and TLightPixelShader), loading them if they haven't been. Thread-safe.
    /// </summary>
    /// <returns>Nulls if the material doesn't have them</returns>
    public (Shader vertexShader, Shader pixelShader) GetLightShaders(SceneLightType lightType, StaticShadowingType shadowing)
    {
        if (!CanRenderWithGameShaders || IsUnlit)
        {
            return (null, null);
        }
        if (LightShaders.TryGetValue((lightType, shadowing), out (Shader, Shader) lightShaders))
        {
            return lightShaders;
        }
        lock (LightShaders)
        {
            if (LightShaders.TryGetValue((lightType, shadowing), out lightShaders))
            {
                return lightShaders;
            }
            string lightPolicy = lightType switch
            {
                SceneLightType.Spot => "FSpotLightPolicy",
                SceneLightType.Directional => "FDirectionalLightPolicy",
                SceneLightType.Point => "FPointLightPolicy",
                //sky lights are part of the base pass
                _ => throw new ArgumentOutOfRangeException(nameof(lightType), lightType, null)
            };
            string shadowingPolicy = shadowing switch
            {
                StaticShadowingType.ShadowTexture => "FShadowTexturePolicy",
                StaticShadowingType.DistanceFieldShadowTexture => "FSignedDistanceFieldShadowTexturePolicy",
                StaticShadowingType.ShadowVertexBuffer => "FShadowVertexBufferPolicy",
                _ => "FNoStaticShadowingPolicy",
            };
            lightShaders = LoadLightPassShaders($"TLightVertexShader{lightPolicy}{shadowingPolicy}", $"TLightPixelShader{lightPolicy}{shadowingPolicy}");
            LightShaders[(lightType, shadowing)] = lightShaders;
            return lightShaders;
        }
    }

    private bool LoadedSHLightPassShaders;
    private (Shader, Shader) SHLightPassShaders;

    /// <summary>
    /// The shaders that render an SH light on this material in its own pass (FSphericalHarmonicLightPolicy), loading them if they haven't been. Thread-safe.
    /// Light environments that cast a shadow render their SH light with these, after the modulated shadows
    /// </summary>
    /// <returns>Nulls if the material doesn't have them</returns>
    public (Shader vertexShader, Shader pixelShader) GetSHLightPassShaders()
    {
        if (!CanRenderWithGameShaders || IsUnlit)
        {
            return (null, null);
        }
        lock (LightShaders)
        {
            if (!LoadedSHLightPassShaders)
            {
                SHLightPassShaders = LoadLightPassShaders("TLightVertexShaderFSphericalHarmonicLightPolicyFNoStaticShadowingPolicy",
                    "TLightPixelShaderFSphericalHarmonicLightPolicyFNoStaticShadowingPolicy");
                LoadedSHLightPassShaders = true;
            }
            return SHLightPassShaders;
        }
    }

    //Call under the LightShaders lock
    private (Shader, Shader) LoadLightPassShaders(string vertexShaderType, string pixelShaderType)
    {
        if (LightShaderMapOwner is null)
        {
            return (null, null);
        }
        try
        {
            (_, Shader[] shaders) = ShaderCacheManipulator.GetMaterialShaderMapAndShaders(LightShaderMapOwner, Context.GetSeekFreeShaderCache,
                [vertexShaderType, pixelShaderType]);
            Shader vertexShader = shaders.FirstOrDefault(shader => shader?.ShaderType.Name == vertexShaderType);
            Shader pixelShader = shaders.FirstOrDefault(shader => shader?.ShaderType.Name == pixelShaderType);
            if (vertexShader is null || pixelShader is null)
            {
                return (null, null);
            }
            if (Context.Device is not null)
            {
                Context.GetCachedVertexShader(vertexShader.Guid, vertexShader.ShaderByteCode);
                Context.GetCachedMeshPixelShader(pixelShader.Guid, pixelShader.ShaderByteCode, UseSrgbColorManagement);
            }
            return (vertexShader, pixelShader);
        }
        catch (Exception e)
        {
            //the light just isn't rendered on this material
            System.Diagnostics.Debug.WriteLine($"Could not load {pixelShaderType} for {InstancedFullPath}: {e.Message}");
            return (null, null);
        }
    }

    private bool LoadedLightFunctionShader;
    private Shader LightFunctionPixelShader;

    /// <summary>
    /// For a material used as a light function: its FLightFunctionPixelShader, loading it if it hasn't been. Null if it doesn't have one.
    /// (These materials have no base pass shaders, so <see cref="CanRenderWithGameShaders"/> is false for them.)
    /// </summary>
    public Shader GetLightFunctionPixelShader()
    {
        LoadGameShaders();
        lock (LightShaders)
        {
            if (!LoadedLightFunctionShader)
            {
                LoadedLightFunctionShader = true;
                if (LightShaderMapOwner is not null && ShaderMap is not null)
                {
                    try
                    {
                        (_, Shader[] shaders) = ShaderCacheManipulator.GetMaterialShaderMapAndMaterialShaders(LightShaderMapOwner, Context.GetSeekFreeShaderCache, "FLightFunctionPixelShader");
                        LightFunctionPixelShader = shaders[0];
                    }
                    catch (Exception e)
                    {
                        //the light function just isn't rendered
                        System.Diagnostics.Debug.WriteLine($"Could not load the light function shader of {InstancedFullPath}: {e.Message}");
                    }
                }
            }
            return LightFunctionPixelShader;
        }
    }

    private void AddUniformExpressionParameters(MaterialUniformExpression expression)
    {
        switch (expression)
        {
            case MaterialUniformExpressionScalarParameter scalarParameter:
                ScalarParameterValues.TryAdd(scalarParameter.ParameterName.Instanced, scalarParameter.DefaultValue);
                break;
            case MaterialUniformExpressionVectorParameter vectorParameter:
                VectorParameterValues.TryAdd(vectorParameter.ParameterName.Instanced, vectorParameter.DefaultValue);
                break;
            case MaterialUniformExpressionUnaryOp unaryOperation:
                AddUniformExpressionParameters(unaryOperation.X);
                break;
            case MaterialUniformExpressionBinaryOp binaryOperation:
                AddUniformExpressionParameters(binaryOperation.A);
                AddUniformExpressionParameters(binaryOperation.B);
                break;
            case MaterialUniformExpressionClamp clamp:
                AddUniformExpressionParameters(clamp.Input);
                AddUniformExpressionParameters(clamp.Min);
                AddUniformExpressionParameters(clamp.Max);
                break;
        }
    }

    protected override void ReadMaterialInstanceConstant(ExportEntry matInst, PropertyCollection props)
    {
        base.ReadMaterialInstanceConstant(matInst, props);

        if (props.GetProp<ArrayProperty<StructProperty>>("ScalarParameterValues") is { } scalarValues)
        {
            foreach (StructProperty scalarValue in scalarValues)
            {
                if (scalarValue.GetProp<NameProperty>("ParameterName") is { } paramNameProp
                    && scalarValue.GetProp<FloatProperty>("ParameterValue") is { } valProp)
                {
                    ScalarParameterValues.TryAdd(paramNameProp.Value.Instanced, valProp.Value);
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
                    VectorParameterValues.TryAdd(paramNameProp.Value.Instanced, CommonStructs.GetLinearColor(valProp));
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
                    IEntry textureEntry = valProp.ResolveToEntry(matInst.FileRef);
                    TextureParameterValues.TryAdd(paramNameProp.Value.Instanced, textureEntry?.InstancedFullPath);
                    if (textureEntry is not null)
                    {
                        TextureParameterEntries.TryAdd(paramNameProp.Value.Instanced, textureEntry);
                    }
                }
            }
        }

        if (!FoundShaderMapOwner && props.GetProp<BoolProperty>("bHasStaticPermutationResource") is { Value: true })
        {
            FoundShaderMapOwner = true;
            MaterialInstance binary;
            try
            {
                binary = ObjectBinary.From<MaterialInstance>(matInst);
            }
            catch (Exception e)
            {
                gameShaderError = $"Failed to parse {matInst.InstancedFullPath}: {e.Message}";
                return;
            }
            HasUndisplacedVertices = !binary.SM3StaticPermutationResource.bUsesMaterialVertexPositionOffset;
            foreach (int uIndex in binary.SM3StaticPermutationResource.UniformExpressionTextures)
            {
                Uniform2DTextureExpressions.Add(matInst.FileRef.GetEntry(uIndex)?.InstancedFullPath);
            }
            ShaderMapOwner = matInst;
            ShaderOwner = matInst;
            ShaderOwnerResource = binary.SM3StaticPermutationResource;
        }
    }

    public void UpdateShaderParams<TVertex>(Span<byte> vertexConstantBuffer, Span<byte> pixelConstantBuffer,
        MeshRenderContext context, Mesh<TVertex> mesh) where TVertex : IVertexBase
    {
        context.LEEffect.EnsureFrame(context);
        context.LEEffect.PixelShaderResources.Reset();
        vertexConstantBuffer.Clear();
        pixelConstantBuffer.Clear();
        UsesLinearTextureSampling = !context.UsesLegacyNativeMeshColorPipeline || UseSrgbColorManagement;
        try
        {
            if (mesh is Mesh<LEVertex> leMesh)
            {
                ShaderParameterSetters.WriteBasePassVertexShaderValues(UnrealVertexShader, vertexConstantBuffer, context, leMesh, this, null);
                ShaderParameterSetters.WriteBasePassPixelShaderValues(UnrealPixelShader, pixelConstantBuffer, context, leMesh, this, null, true, default);
            }
            else
            {
                if (UnrealVertexShader is TBasePassVertexShader<FNullPolicy, FNullPolicy> vertexShader)
                    vertexShader.WriteValues(vertexConstantBuffer, context, mesh, this);
                if (UnrealPixelShader is TBasePassPixelShader<FNullPolicy> pixelShader)
                    pixelShader.WriteValues(pixelConstantBuffer, context, mesh, this);
            }
        }
        finally
        {
            UsesLinearTextureSampling = true;
        }
    }

    /// <summary>
    /// Writes the parameters of the base pass shaders
    /// </summary>
    /// <param name="vertexShader">The base pass vertex shader being rendered with: <see cref="UnrealVertexShader"/> or <see cref="NoLightMapVertexShader"/></param>
    /// <param name="pixelShader">The matching pixel shader</param>
    /// <param name="staticLighting">The mesh's precomputed lighting. Its light-map is required if <see cref="UsesLightMap"/></param>
    /// <param name="usePreviewLighting">Light the mesh with <see cref="MeshRenderContext.Lighting"/>, rather than the level's lighting</param>
    /// <param name="skyLighting">The level's sky lighting on the mesh. Unused if <paramref name="usePreviewLighting"/></param>
    /// <param name="lightEnvironment">The lights of the light environment that lights the mesh, which replace the preview lighting. Null if it isn't lit by one</param>
    public void UpdateShaderParams(Span<byte> vertexConstantBuffer, Span<byte> pixelConstantBuffer, MeshRenderContext context, Mesh<LEVertex> mesh,
        Shader vertexShader, Shader pixelShader, MeshStaticLighting staticLighting, bool usePreviewLighting, SkyLighting skyLighting = default,
        LightEnvironmentLighting lightEnvironment = null)
    {

        bool previousTextureSampling = UsesLinearTextureSampling;
        UsesLinearTextureSampling = !context.UsesLegacyNativeMeshColorPipeline || UseSrgbColorManagement;
        try
        {
            vertexConstantBuffer.Clear();
            pixelConstantBuffer.Clear();
            ShaderParameterSetters.WriteBasePassVertexShaderValues(vertexShader, vertexConstantBuffer, context, mesh, this, staticLighting, lightEnvironment);
            ShaderParameterSetters.WriteBasePassPixelShaderValues(pixelShader, pixelConstantBuffer, context, mesh, this, staticLighting, usePreviewLighting, skyLighting,
                lightEnvironment);
        }
        finally
        {
            UsesLinearTextureSampling = previousTextureSampling;
        }
    }

    /// <summary>
    /// Writes the parameters of the shaders that render a light on this material (see <see cref="GetLightShaders"/>)
    /// </summary>
    /// <param name="lightAttenuation">The light's dynamic shadows and light function, rendered in screen space (see <see cref="LightAttenuationRenderer"/>). Null if it has neither</param>
    public void UpdateLightPassShaderParams(Span<byte> vertexConstantBuffer, Span<byte> pixelConstantBuffer, MeshRenderContext context, Mesh<LEVertex> mesh,
        Shader vertexShader, Shader pixelShader, LightInteraction interaction, ShaderResourceView lightAttenuation = null)
    {
        bool previousTextureSampling = UsesLinearTextureSampling;
        UsesLinearTextureSampling = !context.UsesLegacyNativeMeshColorPipeline || UseSrgbColorManagement;
        try
        {
            vertexConstantBuffer.Clear();
            pixelConstantBuffer.Clear();
            ShaderParameterSetters.WriteLightVertexShaderValues(vertexShader, vertexConstantBuffer, context, mesh, this, interaction);
            ShaderParameterSetters.WriteLightPixelShaderValues(pixelShader, pixelConstantBuffer, context, mesh, this, interaction, lightAttenuation);
        }
        finally
        {
            UsesLinearTextureSampling = previousTextureSampling;
        }
    }

    /// <summary>
    /// Writes the parameters of the shaders that render an SH light on this material (see <see cref="GetSHLightPassShaders"/>)
    /// </summary>
    public void UpdateSHLightPassShaderParams(Span<byte> vertexConstantBuffer, Span<byte> pixelConstantBuffer, MeshRenderContext context, Mesh<LEVertex> mesh,
        Shader vertexShader, Shader pixelShader, in SHVectorRGB incidentLighting)
    {
        bool previousTextureSampling = UsesLinearTextureSampling;
        UsesLinearTextureSampling = !context.UsesLegacyNativeMeshColorPipeline || UseSrgbColorManagement;
        try
        {
            vertexConstantBuffer.Clear();
            pixelConstantBuffer.Clear();
            ShaderParameterSetters.WriteSHLightPassValues(vertexShader, pixelShader, vertexConstantBuffer, pixelConstantBuffer, context, mesh, this, incidentLighting);
        }
        finally
        {
            UsesLinearTextureSampling = previousTextureSampling;
        }
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
            context.Time, context.Time, GetFlipBookTextureOffset, GetFlipBookTextureScale);

        UpdateExpressions(uniformContext,
            VertexVectorExpressions, VertexScalarExpressions,
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
            context.Time, context.Time, GetFlipBookTextureOffset, GetFlipBookTextureScale);

        UpdateExpressions(uniformContext,
            PixelVectorExpressions, PixelScalarExpressions,
            CachedPixelScalarParameters, CachedPixelVectorParameters);

        UpdateTextureExpressions(Pixel2DTextureExpressions, CachedTexture2DParameters, context);
        UpdateTextureExpressions(PixelCubeTextureExpressions, CachedCubeTextureParameters, context);
    }

    private LinearColor GetFlipBookTextureOffset(UniformExpressionRenderContext context, int texIndex)
    {
        return GetFlipBookTexture(texIndex)?.GetTextureOffset(context) ?? LinearColor.Black;
    }

    private LinearColor GetFlipBookTextureScale(UniformExpressionRenderContext context, int texIndex)
    {
        return GetFlipBookTexture(texIndex)?.GetTextureScale() ?? LinearColor.Black;
    }

    private PreviewTextureCache.FlipBookTextureEntry GetFlipBookTexture(int texIndex)
    {
        if ((uint)texIndex < Uniform2DTextureExpressions.Count
            && Uniform2DTextureExpressions[texIndex] is { } texifp
            && TextureMap.TryGetValue(texifp, out var texture))
        {
            return texture as PreviewTextureCache.FlipBookTextureEntry;
        }
        return null;
    }

    private void UpdateTextureExpressions(MaterialUniformExpressionTexture[] textureExpressions,
        List<PreviewTextureCache.TextureEntry> textureCache, MeshRenderContext context)
    {
        foreach (MaterialUniformExpressionTexture texExpression in textureExpressions)
        {
            PreviewTextureCache.TextureEntry texture = null;
            // The human-lash shader serializes its opacity texture as a plain uniform expression. Meshplorer
            // deliberately resolves that slot from the effective child-MIC parameter instead of the master's
            // default lash texture, which otherwise produces an opaque white shell around the eyes.
            if (IsHumanLashMaterial && TextureParameterValues.ContainsKey(HUMAN_LASH_OPACITY_PARAMETER_NAME))
            {
                textureCache.Add(ResolveTextureParameter(HUMAN_LASH_OPACITY_PARAMETER_NAME, context));
                continue;
            }
            switch (texExpression)
            {
                case MaterialUniformExpressionTextureParameter texParamExpression:
                    texture = ResolveTextureParameter(texParamExpression.ParameterName.Instanced, context);
                    break;
                default:
                    if ((uint)texExpression.TextureIndex < Uniform2DTextureExpressions.Count
                        && Uniform2DTextureExpressions[texExpression.TextureIndex] is {} texifp)
                    {
                        texture = ResolveDefaultTextureOverride(texifp, context);
                        texture ??= TextureMap.GetValueOrDefault(texifp);
                    }
                    break;
            }
            textureCache.Add(texture);
        }
    }

    private void RegisterDefaultTextureParameter(IEntry textureEntry, string parameterName)
    {
        foreach (string texturePath in new[] { textureEntry.FullPath, textureEntry.InstancedFullPath })
        {
            if (string.IsNullOrEmpty(texturePath)) continue;
            if (!DefaultTextureParameterNames.TryGetValue(texturePath, out HashSet<string> parameterNames))
            {
                parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                DefaultTextureParameterNames.Add(texturePath, parameterNames);
            }
            parameterNames.Add(parameterName);
        }
    }

    private PreviewTextureCache.TextureEntry ResolveDefaultTextureOverride(string defaultTexturePath,
        MeshRenderContext context)
    {
        if (!DefaultTextureParameterNames.TryGetValue(defaultTexturePath, out HashSet<string> parameterNames))
        {
            return null;
        }

        PreviewTextureCache.TextureEntry selectedTexture = null;
        foreach (string parameterName in parameterNames)
        {
            if (!TextureParameterValues.TryGetValue(parameterName, out string overridePath)
                || string.IsNullOrEmpty(overridePath)
                || string.Equals(overridePath, defaultTexturePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            PreviewTextureCache.TextureEntry candidate = ResolveTextureParameter(parameterName, context);
            if (candidate is null)
            {
                continue;
            }
            if (selectedTexture is not null && !ReferenceEquals(selectedTexture, candidate))
            {
                return null;
            }
            selectedTexture = candidate;
        }
        return selectedTexture;
    }

    private PreviewTextureCache.TextureEntry ResolveTextureParameter(string parameterName, MeshRenderContext context)
    {
        if (!TextureParameterValues.TryGetValue(parameterName, out string texturePath)
            || string.IsNullOrEmpty(texturePath))
        {
            return null;
        }
        if (TextureMap.TryGetValue(texturePath, out PreviewTextureCache.TextureEntry texture))
        {
            return texture;
        }
        if (!TextureParameterEntries.TryGetValue(parameterName, out IEntry textureEntry)
            || (!string.Equals(texturePath, textureEntry.InstancedFullPath, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(texturePath, textureEntry.FullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        ExportEntry textureExport = textureEntry as ExportEntry;
        if (textureEntry is ImportEntry textureImport)
        {
            textureExport = context.ResolveExportCached(textureImport);
        }
        if (textureExport is null)
        {
            return null;
        }

        texture = context.TextureCache.LoadTexture(textureExport, context.PackageCache, UseSrgbColorManagement);
        if (texture is null)
        {
            return null;
        }
        TextureMap[textureEntry.FullPath] = texture;
        TextureMap[textureEntry.InstancedFullPath] = texture;
        TextureMap[textureExport.FullPath] = texture;
        TextureMap[textureExport.InstancedFullPath] = texture;
        return texture;
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
