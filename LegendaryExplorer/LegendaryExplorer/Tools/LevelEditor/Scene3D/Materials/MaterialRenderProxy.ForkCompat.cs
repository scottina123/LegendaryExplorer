using System;
using System.Collections.Generic;
using System.Linq;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

/// <summary>
/// Fork material API on upstream's <see cref="MaterialRenderProxy"/>: the constructors the fork's VFX/lens-flare/morph code uses, and live
/// parameter editing (<see cref="ILiveMaterialRenderProxy"/>). Shaders still load through upstream's shader map lookup and the parameters feed its
/// uniform expressions.
/// </summary>
public partial class MaterialRenderProxy : ILiveMaterialRenderProxy
{
    /// <summary>The material export this was read from. Null for compiled resources (terrain)</summary>
    public ExportEntry MaterialExport { get; private set; }

    /// <summary>Fork: whether the fork's sRGB pipeline was requested. Upstream samples linear views regardless</summary>
    public bool UseSrgbColorManagement { get; private set; }

    /// <summary>
    /// Fork: a material, optionally starting from another proxy's effective parameters (a live-edited copy). Its shaders load as upstream's do
    /// (queued or on first use)
    /// </summary>
    public MaterialRenderProxy(MeshRenderContext context, ExportEntry export, MaterialRenderProxy parameterSource, bool useSrgbColorManagement = false)
        : this(context, export)
    {
        UseSrgbColorManagement = useSrgbColorManagement;
        if (parameterSource is not null) CopyEffectiveParametersFrom(parameterSource);
    }

    /// <summary>
    /// Fork: as above, for a mesh with a static light-map of <paramref name="lightMapType"/>; its shaders are queued for loading
    /// </summary>
    public MaterialRenderProxy(MeshRenderContext context, ExportEntry export, MaterialRenderProxy parameterSource, bool useSrgbColorManagement,
        ELightMapType lightMapType)
        : this(context, export, parameterSource, useSrgbColorManagement)
    {
        QueueGameShaderLoad(lightMapType);
    }

    /// <summary>
    /// Fork: a material drawn with another vertex factory (particle sprites, beams and trails, mesh emitters, lens flares). Its base pass
    /// shaders for that factory are loaded now, so the caller can check them (<see cref="UnrealVertexShader"/>, <see cref="GameShaderError"/>)
    /// </summary>
    public MaterialRenderProxy(MeshRenderContext context, ExportEntry export, string vertexFactoryType)
        : this(context, export)
    {
        VertexFactoryType = string.IsNullOrWhiteSpace(vertexFactoryType) ? VERTEX_FACTORY_TYPE_NAME : vertexFactoryType;
        LoadBaseShaders();
    }

    /// <summary>
    /// Whether any of its uniform expressions change with time, so frames that draw it can't be reused
    /// </summary>
    public bool HasFrameDependentUniforms => ShaderMap is { } map
        && (map.UniformVertexScalarExpressions ?? []).Concat(map.UniformVertexVectorExpressions ?? [])
            .Concat(map.UniformPixelScalarExpressions ?? []).Concat(map.UniformPixelVectorExpressions ?? [])
            .Concat(map.Uniform2DTextureExpressions ?? []).Concat(map.UniformCubeTextureExpressions ?? [])
            .Any(expression => !expression.IsNotFrameDependent);

    /// <summary>
    /// Fork: writes the base pass parameters of <see cref="UnrealVertexShader"/> and <see cref="UnrealPixelShader"/>, lit by the preview lighting.
    /// For VFX and lens-flare draws, which have no static lighting or light environment
    /// </summary>
    internal void UpdateShaderParams(Span<byte> vertexConstantBuffer, Span<byte> pixelConstantBuffer, MeshRenderContext context, IShaderPrimitive mesh)
        => UpdateShaderParams(vertexConstantBuffer, pixelConstantBuffer, context, mesh, UnrealVertexShader, UnrealPixelShader, staticLighting: null,
            usePreviewLighting: true);

    /// <summary>
    /// Fork: whether every texture the material samples is loaded (the fork skipped human eyelash materials until theirs were). Upstream binds
    /// a white texture for any that isn't, so materials can always draw
    /// </summary>
    public bool HasRequiredTextures(MeshRenderContext context) => true;

    #region Live parameter editing

    /// <summary>Effective scalar parameters: inherited defaults, instance values and preview overrides</summary>
    public IReadOnlyDictionary<string, float> ScalarParameters => ScalarParameterValues;

    /// <summary>Effective vector parameters: inherited defaults, instance values and preview overrides</summary>
    public IReadOnlyDictionary<string, LinearColor> VectorParameters => VectorParameterValues;

    private readonly Dictionary<string, (bool Exists, float Value)> PreviewScalarBaselines = [];
    private readonly Dictionary<string, (bool Exists, LinearColor Value)> PreviewVectorBaselines = [];
    private readonly Dictionary<string, (bool Exists, string Value)> PreviewTextureBaselines = [];

    private void InvalidateUniformParameters() => CachedPixelFrameNumber = CachedVertexFrameNumber = uint.MaxValue;

    public void SetScalarParameter(string parameterName, float value)
    {
        PreviewScalarBaselines.TryAdd(parameterName, ScalarParameterValues.TryGetValue(parameterName, out float baseline) ? (true, baseline) : (false, default));
        ScalarParameterValues[parameterName] = value;
        InvalidateUniformParameters();
    }

    public void SetVectorParameter(string parameterName, LinearColor value)
    {
        PreviewVectorBaselines.TryAdd(parameterName, VectorParameterValues.TryGetValue(parameterName, out LinearColor baseline) ? (true, baseline) : (false, default));
        VectorParameterValues[parameterName] = value;
        InvalidateUniformParameters();
    }

    public void RemoveScalarParameter(string parameterName)
    {
        PreviewScalarBaselines.TryAdd(parameterName, ScalarParameterValues.TryGetValue(parameterName, out float baseline) ? (true, baseline) : (false, default));
        ScalarParameterValues.Remove(parameterName);
        InvalidateUniformParameters();
    }

    public void RemoveVectorParameter(string parameterName)
    {
        PreviewVectorBaselines.TryAdd(parameterName, VectorParameterValues.TryGetValue(parameterName, out LinearColor baseline) ? (true, baseline) : (false, default));
        VectorParameterValues.Remove(parameterName);
        InvalidateUniformParameters();
    }

    /// <summary>
    /// Previews a texture parameter. <paramref name="texture"/> is the loaded texture for <paramref name="texturePath"/>; null (or no path) removes it
    /// </summary>
    public void SetTextureParameter(string parameterName, string texturePath, PreviewTextureCache.TextureEntry texture)
    {
        PreviewTextureBaselines.TryAdd(parameterName, TextureParameterValues.TryGetValue(parameterName, out string baseline) ? (true, baseline) : (false, default));
        if (string.IsNullOrEmpty(texturePath) || texture is null)
        {
            TextureParameterValues.Remove(parameterName);
        }
        else
        {
            TextureMap ??= new Dictionary<string, PreviewTextureCache.TextureEntry>(StringComparer.OrdinalIgnoreCase);
            TextureMap[texturePath] = texture;
            TextureParameterValues[parameterName] = texturePath;
        }
        InvalidateUniformParameters();
    }

    /// <summary>Keeps the preview overrides as the material's values (after they've been written to the package)</summary>
    public void CommitPreviewParameterOverrides()
    {
        PreviewScalarBaselines.Clear();
        PreviewVectorBaselines.Clear();
        PreviewTextureBaselines.Clear();
    }

    /// <summary>Puts back the values the preview overrides replaced</summary>
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
        CommitPreviewParameterOverrides();
        InvalidateUniformParameters();
    }

    internal void CopyEffectiveParametersFrom(MaterialRenderProxy source)
    {
        foreach ((string name, float value) in source.ScalarParameterValues) ScalarParameterValues[name] = value;
        foreach ((string name, LinearColor value) in source.VectorParameterValues) VectorParameterValues[name] = value;
        foreach ((string name, string value) in source.TextureParameterValues) TextureParameterValues[name] = value;
        if (source.TextureMap is not null)
        {
            TextureMap ??= new Dictionary<string, PreviewTextureCache.TextureEntry>(StringComparer.OrdinalIgnoreCase);
            foreach ((string path, PreviewTextureCache.TextureEntry texture) in source.TextureMap) TextureMap.TryAdd(path, texture);
        }
        InvalidateUniformParameters();
    }

    #endregion
}
