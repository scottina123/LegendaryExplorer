using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using LegendaryExplorerCore.Unreal.Classes;
using SharpDX.Direct3D11;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

// MODEL RENDERING OVERVIEW:
// Construct a ModelPreview instance with an existing SkeletalMesh or StaticMesh.
// Call ModelPreview.Render(...) every frame. Boom.
namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

/// <summary>
/// Stores the material information of triangles in a <see cref="ModelPreviewLOD"/> mesh.
/// </summary>
public struct ModelPreviewSection
{
    /// <summary>
    /// The name of the material to be applied to the triangles in this section.
    /// </summary>
    public string MaterialName;

    /// <summary>
    /// The first index into the LOD mesh index buffer that this section describes.
    /// </summary>
    public uint StartIndex;

    /// <summary>
    /// How many triangles, starting from the vertex at <see cref="StartIndex"/>, that this section describes.
    /// </summary>
    public uint TriangleCount;

    /// <summary>
    /// Constructs a new MaterialPreviewSection.
    /// </summary>
    /// <param name="materialname">The name of the material to be applied to the triangles in this section.</param>
    /// <param name="startindex">The first index into the LOD mesh index buffer that this section describes.</param>
    /// <param name="trianglecount">How many triangles, starting from the vertex at <see cref="StartIndex"/>, that this section describes.</param>
    public ModelPreviewSection(string materialname, uint startindex, uint trianglecount)
    {
        MaterialName = materialname;
        StartIndex = startindex;
        TriangleCount = trianglecount;
    }
}

/// <summary>
/// Stores the geometry and the associated material information for a single level-of-detail in a <see cref="ModelPreview"/>.
/// </summary>
public class ModelPreviewLOD<Vertex> where Vertex : IVertexBase
{
    /// <summary>
    /// The geometry of this level of detail.
    /// </summary>
    public Mesh<Vertex> Mesh;

    /// <summary>
    /// A list of which materials are applied to which triangles.
    /// </summary>
    public List<ModelPreviewSection> Sections;

    /// <summary>
    /// The precomputed lighting of this LOD, if it has any. Only used by the game's shaders
    /// </summary>
    public MeshStaticLighting StaticLighting;

    /// <summary>
    /// The light environment that lights this LOD, if it's lit by one (and they're supported for the game). Only used by the game's shaders
    /// </summary>
    public DynamicLightEnvironment LightEnvironment;

    /// <summary>
    /// Creates a new ModelPreviewLOD.
    /// </summary>
    /// <param name="mesh">The geometry of this level of detail.</param>
    /// <param name="sections">A list of which materials are applied to which triangles.</param>
    public ModelPreviewLOD(Mesh<Vertex> mesh, List<ModelPreviewSection> sections)
    {
        Mesh = mesh;
        Sections = sections;
    }
}

public enum RenderPass
{
    //material types
    Base,
    Hair,
    //The level's lights, each added onto the opaque geometry they light in its own pass. Must be rendered after the opaque passes
    Lighting,
    //materials that blend with what's behind them. Must be rendered after the opaque passes, since they don't write depth
    Translucent,

    //special types
    Collision,
    HitTest,

    //override, most always be last
    ANY
}

/// <summary>
/// ModelPreviewMaterial is responsible for rendering sections of meshes.
/// </summary>
public abstract class ModelPreviewMaterial<Vertex> where Vertex : IVertexBase
{

    public RenderPass Pass;

    public virtual bool CanCullByBounds(MeshRenderContext context) => true;

    protected readonly MaterialInstanceConstantLevelEditor Material;
    public ExportEntry MaterialExport { get; }
    public string InstancedFullPath => Material.InstancedFullPath;

    /// <summary>
    /// True when the material must be evaluated again as frame time advances.
    /// </summary>
    public virtual bool HasFrameDependentUniforms => false;

    /// <summary>
    /// A Dictionary of string properties. Useful because some materials have properties that others don't.
    /// </summary>
    public readonly Dictionary<string, string> Properties = [];

    /// <summary>
    /// Creates a ModelPreviewMaterial that renders as close to what the given <see cref="MaterialInstanceConstantLevelEditor"/> looks like as possible. 
    /// </summary>
    public ModelPreviewMaterial(MeshRenderContext renderContext, ExportEntry export)
        : this(renderContext, export, null)
    {
    }

    protected ModelPreviewMaterial(MeshRenderContext renderContext, ExportEntry export,
        MaterialInstanceConstantLevelEditor material)
    {
        MaterialExport = export;
        Material = material ?? CreateMaterial(renderContext, export);
        Pass = RenderPass.Base;
    }

    /// <summary>
    /// Renders the given <see cref="ModelPreviewSection"/> of a <see cref="ModelPreviewLOD"/>. 
    /// </summary>
    /// <param name="lod">The LOD to render.</param>
    /// <param name="s">Which faces to render.</param>
    public abstract void RenderSection(ModelPreviewLOD<Vertex> lod, ModelPreviewSection s,  MeshRenderContext context);

    /// <summary>
    /// Renders the level's lights on the given <see cref="ModelPreviewSection"/>, for <see cref="RenderPass.Lighting"/>.
    /// </summary>
    public virtual void RenderLighting(ModelPreviewLOD<Vertex> lod, ModelPreviewSection s, MeshRenderContext context) { }

    ///Only call from constructor
    protected abstract MaterialInstanceConstantLevelEditor CreateMaterial(MeshRenderContext renderContext, ExportEntry export);
}

public class TexturedPreviewMaterial : ModelPreviewMaterial<WorldVertex>
{
    private readonly PreviewTextureCache.TextureEntry DiffTexture;
    private readonly bool IsOpacityOnly;

    public TexturedPreviewMaterial(MeshRenderContext renderContext, ExportEntry export) : base(renderContext, export)
    {
        Properties.Add("Name", export.ObjectName.Instanced);
        if (FindDiffuse(export, Material) is IEntry diffEntry)
        {
            DiffTexture = renderContext.TextureCache.LoadTexture(diffEntry, renderContext.PackageCache);
        }
        // The simple standard shader cannot reproduce opacity-only shells such as human eyelashes.
        // Rendering them with a stand-in diffuse texture paints an opaque shell over the eyes, so omit
        // only that unsupported shell in standard mode. The in-game shader path still renders it normally.
        IsOpacityOnly = DiffTexture is null
                        && Material.Textures.Any(texture => texture.ObjectName.Name.Contains("opac", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Guesses which of a material's textures is the diffuse texture, based on texture names.
    /// </summary>
    internal static IEntry FindDiffuse(ExportEntry matExport, MaterialInstanceConstantLevelEditor mat)
    {
        string matPackage = matExport.Parent?.InstancedFullPath.ToLower();
        string matName = matExport.ObjectName.Instanced;
        foreach (var textureEntry in mat.Textures)
        {
            var texObjectName = textureEntry.InstancedFullPath.ToLower();
            if ((matPackage == null || texObjectName.StartsWith(matPackage)) && texObjectName.Contains("diff"))
            {
                // we have found the diffuse texture!
                Debug.WriteLine("Diffuse texture of new material <" + matName + "> is " + textureEntry.InstancedFullPath);
                return textureEntry;
            }
        }

        foreach (var textureEntry in mat.Textures.Reverse())
        {
            var texObjectName = textureEntry.ObjectName.Name.ToLower();
            if (texObjectName.Contains("diff") || texObjectName.Contains("tex"))
            {
                // we have found the diffuse texture!
                Debug.WriteLine("Diffuse texture of new material <" + matName + "> is " + textureEntry.InstancedFullPath);
                return textureEntry;
            }
        }
        foreach (var texparam in mat.Textures)
        {
            var texObjectName = texparam.ObjectName.Name.ToLower();
            if (texObjectName.Contains("detail"))
            {
                // I guess a detail texture is good enough if we didn't return for a diffuse texture earlier...
                Debug.WriteLine("Diffuse (Detail) texture of new material <" + matName + "> is " + texparam.InstancedFullPath);
                return texparam;
            }
        }
        foreach (var texparam in mat.Textures)
        {
            var texObjectName = texparam.ObjectName.Name.ToLower();
            if (!texObjectName.Contains("norm") && !texObjectName.Contains("opac"))
            {
                //Anything is better than nothing I suppose
                Debug.WriteLine("Using first found texture (last resort)  of new material <" + matName + "> as diffuse: " + texparam.InstancedFullPath);
                return texparam;
            }
        }
        return null;
    }

    /// <summary>
    /// Uses LEX's default shader to render the given <see cref="ModelPreviewSection"/> of a <see cref="ModelPreviewLOD"/>. 
    /// </summary>
    /// <param name="lod">The LOD to render.</param>
    /// <param name="s">Which faces to render.</param>
    public override void RenderSection(ModelPreviewLOD<WorldVertex> lod, ModelPreviewSection s, MeshRenderContext context)
    {
        if (IsOpacityOnly) return;
        context.DefaultEffect.PrepDraw(context.ImmediateContext, context.AlphaBlendState, context.GetWorldConstants(lod.Mesh.LocalToWorld));

        ShaderResourceView diffTextureView = DiffTexture?.TextureView ?? context.DefaultTextureView;

        context.DefaultEffect.RenderObject(
            context.ImmediateContext,
            lod.Mesh,
            (int)s.StartIndex,
            (int)s.TriangleCount * 3,
            context.Wireframe ? null : diffTextureView);
    }

    protected override MaterialInstanceConstantLevelEditor CreateMaterial(MeshRenderContext renderContext, ExportEntry export)
    {
        return new MaterialInstanceConstantLevelEditor(export, renderContext.PackageCache, true);
    }
}

internal class LEShaderPreviewMaterial : ModelPreviewMaterial<LEVertex>
{
    private readonly RenderTargetBlendDescription BlendDescription;
    private readonly bool UseSrgbColorManagement;

    public readonly Dictionary<string, PreviewTextureCache.TextureEntry> TextureMap = new(StringComparer.OrdinalIgnoreCase);

    private readonly PreviewTextureCache.TextureEntry FallbackDiffTexture;
    /// <summary>
    /// The in-game shader preview is only available when the material supplied both halves of the
    /// local-vertex-factory base pass used by Meshplorer and Morph Editor.
    /// </summary>
    public bool CanRender => Material is MaterialRenderProxy
    {
        UnrealVertexShader: not null,
        UnrealPixelShader: not null
    };

    public override bool HasFrameDependentUniforms => RenderProxy.HasFrameDependentUniforms;

    public LEShaderPreviewMaterial(MeshRenderContext renderContext, ExportEntry export,
        bool useSrgbColorManagement = false, ELightMapType lightMapType = ELightMapType.LMT_None)
        : this(renderContext, export, null, useSrgbColorManagement, lightMapType)
    {
    }

    private LEShaderPreviewMaterial(MeshRenderContext renderContext, ExportEntry export,
        MaterialRenderProxy parameterSource, bool useSrgbColorManagement, ELightMapType lightMapType = ELightMapType.LMT_None)
        : base(renderContext, export, new MaterialRenderProxy(renderContext, export, parameterSource,
            useSrgbColorManagement))
    {
        UseSrgbColorManagement = useSrgbColorManagement;
        foreach (IEntry textureEntry in Material.Textures)
        {
            if (!TextureMap.ContainsKey(textureEntry.InstancedFullPath))
            {
                PreviewTextureCache.TextureEntry texture = renderContext.TextureCache.LoadTexture(textureEntry,
                    renderContext.PackageCache, useSrgbColorManagement);
                if (texture is not null)
                {
                    // Shader uniforms use InstancedFullPath while older material data often uses FullPath.
                    // Meshplorer registers both names so inherited MIC texture overrides resolve reliably.
                    TextureMap[textureEntry.FullPath] = texture;
                    TextureMap[textureEntry.InstancedFullPath] = texture;
                }
            }
        }
        var mat = (MaterialRenderProxy)Material;
        mat.TextureMap = TextureMap;
        mat.QueueGameShaderLoad(lightMapType);
        if (TexturedPreviewMaterial.FindDiffuse(export, Material) is IEntry diffEntry)
        {
            TextureMap.TryGetValue(diffEntry.InstancedFullPath, out FallbackDiffTexture);
        }
        // Translucent hair does not write depth, so composite it after opaque lighting and shadows.
        // Only opaque/masked hair can safely use the earlier, depth-writing hair pass.
        Pass = IsTranslucent ? RenderPass.Translucent
            : mat.UseHairPass ? RenderPass.Hair
            : RenderPass.Base;
        BlendDescription = mat.BlendMode switch
        {
            EBlendMode.BLEND_Opaque => new RenderTargetBlendDescription
            {
                RenderTargetWriteMask = ColorWriteMaskFlags.All,
                BlendOperation = BlendOperation.Add,
                AlphaBlendOperation = BlendOperation.Add,
                SourceBlend = BlendOption.One,
                DestinationBlend = BlendOption.Zero,
                SourceAlphaBlend = BlendOption.One,
                DestinationAlphaBlend = BlendOption.Zero,
                IsBlendEnabled = false
            },
            EBlendMode.BLEND_Masked => new RenderTargetBlendDescription
            {
                RenderTargetWriteMask = ColorWriteMaskFlags.All,
                BlendOperation = BlendOperation.Add,
                AlphaBlendOperation = BlendOperation.Add,
                SourceBlend = BlendOption.One,
                DestinationBlend = BlendOption.Zero,
                SourceAlphaBlend = BlendOption.One,
                DestinationAlphaBlend = BlendOption.Zero,
                IsBlendEnabled = false
            },
            EBlendMode.BLEND_Translucent => new RenderTargetBlendDescription
            {
                RenderTargetWriteMask = ColorWriteMaskFlags.All,
                BlendOperation = BlendOperation.Add,
                AlphaBlendOperation = BlendOperation.Add,
                SourceBlend = BlendOption.SourceAlpha,
                DestinationBlend = BlendOption.InverseSourceAlpha,
                SourceAlphaBlend = BlendOption.SourceAlphaSaturate,
                DestinationAlphaBlend = BlendOption.InverseSourceAlpha,
                IsBlendEnabled = true
            },
            //TODO: the ones above this comment seem to work properly, but the rest need verifying
            EBlendMode.BLEND_Additive => new RenderTargetBlendDescription
            {
                RenderTargetWriteMask = ColorWriteMaskFlags.All,
                BlendOperation = BlendOperation.Add,
                AlphaBlendOperation = BlendOperation.Add,
                SourceBlend = BlendOption.One,
                DestinationBlend = BlendOption.One,
                SourceAlphaBlend = BlendOption.Zero,
                DestinationAlphaBlend = BlendOption.One,
                IsBlendEnabled = true
            },
            EBlendMode.BLEND_Modulate => new RenderTargetBlendDescription
            {
                RenderTargetWriteMask = ColorWriteMaskFlags.All,
                BlendOperation = BlendOperation.Add,
                AlphaBlendOperation = BlendOperation.Add,
                SourceBlend = BlendOption.DestinationColor,
                DestinationBlend = BlendOption.Zero,
                SourceAlphaBlend = BlendOption.Zero,
                DestinationAlphaBlend = BlendOption.One,
                IsBlendEnabled = true
            },
            EBlendMode.BLEND_SoftMasked => new RenderTargetBlendDescription
            {
                RenderTargetWriteMask = ColorWriteMaskFlags.All,
                BlendOperation = BlendOperation.Add,
                AlphaBlendOperation = BlendOperation.Add,
                SourceBlend = BlendOption.SourceAlpha,
                DestinationBlend = BlendOption.InverseSourceAlpha,
                SourceAlphaBlend = BlendOption.Zero,
                DestinationAlphaBlend = BlendOption.InverseSourceAlpha,
                IsBlendEnabled = true
            },
            EBlendMode.BLEND_AlphaComposite => new RenderTargetBlendDescription
            {
                RenderTargetWriteMask = ColorWriteMaskFlags.All,
                BlendOperation = BlendOperation.Add,
                AlphaBlendOperation = BlendOperation.Add,
                SourceBlend = BlendOption.One,
                DestinationBlend = BlendOption.InverseSourceAlpha,
                SourceAlphaBlend = BlendOption.One,
                DestinationAlphaBlend = BlendOption.InverseSourceAlpha,
                IsBlendEnabled = false
            },
            _ => throw new ArgumentOutOfRangeException(),
        };
        //The game's base pass shaders write 0 to the alpha channel of the color target.
        //Don't let that through to the backbuffer, or the scene will be transparent when composited.
        BlendDescription.RenderTargetWriteMask = ColorWriteMaskFlags.Red | ColorWriteMaskFlags.Green | ColorWriteMaskFlags.Blue;
    }

    internal LEShaderPreviewMaterial CreateEffectOverride(MeshRenderContext renderContext, ExportEntry effectMaterial)
        => new(renderContext, effectMaterial, (MaterialRenderProxy)Material, UseSrgbColorManagement, RenderProxy.LightMapType);

    internal void PrepareGraphicsResources(MeshRenderContext context)
    {
        var material = (MaterialRenderProxy)Material;
        context.PrepareGameShaderMaterial(material);
        if (!CanRender)
        {
            return;
        }
        material.HasRequiredTextures(context);
        context.GetCachedMeshPixelShader(material.UnrealPixelShader.Guid, material.UnrealPixelShader.ShaderByteCode, material.UseSrgbColorManagement);
        context.GetCachedVertexShader(material.UnrealVertexShader.Guid, material.UnrealVertexShader.ShaderByteCode);
        context.GetCachedBlendState(BlendDescription);
    }

    private bool LoggedFallback;

    public MaterialRenderProxy RenderProxy => (MaterialRenderProxy)Material;

    public override bool CanCullByBounds(MeshRenderContext context) =>
        !context.UseGameShaders || RenderProxy.HasUndisplacedVertices;

    //same as UE3's IsTranslucentBlendMode
    private bool IsTranslucent => ((MaterialRenderProxy)Material).BlendMode is not (EBlendMode.BLEND_Opaque or EBlendMode.BLEND_Masked);

    /// <summary>
    /// Renders the given <see cref="ModelPreviewSection"/> of a <see cref="ModelPreviewLOD"/> using the game's shader, if <see cref="MeshRenderContext.UseGameShaders"/> is set.
    /// Otherwise, or if that isn't possible, renders with LEX's shader.
    /// </summary>
    /// <param name="lod">The LOD to render.</param>
    /// <param name="s">Which faces to render.</param>
    /// <param name="context"></param>
    public override void RenderSection(ModelPreviewLOD<LEVertex> lod, ModelPreviewSection s, MeshRenderContext context)
    {
        if (context.UseGameShaders && context.HasDefaultTextureChannels)
        {
            var material = (MaterialRenderProxy)Material;
            if (material.CanRenderWithGameShaders)
            {
                try
                {
                    if (RenderSectionWithGameShaders(lod, s, context))
                        return;
                }
                catch (Exception e)
                {
                    material.MarkGameShadersFailed(e);
                }
            }
            if (!material.CanRenderWithGameShaders && !LoggedFallback)
            {
                LoggedFallback = true;
                Debug.WriteLine($"{InstancedFullPath} will be rendered with the LEX shader: {material.GameShaderError}");
            }
        }
        context.RenderMeshWithDefaultEffect(lod.Mesh, s, FallbackDiffTexture?.TextureView);
    }

    /// <summary>
    /// Whether the mesh is lit by the level's lights in separate passes (see <see cref="RenderLighting"/>), as the game does for static meshes without a light-map.
    /// If not (and it has no light-map), it's lit by <see cref="MeshRenderContext.Lighting"/>
    /// </summary>
    private static bool UsesLevelLighting(ModelPreviewLOD<LEVertex> lod, MaterialRenderProxy material, MeshRenderContext context) =>
        context.LightingMode == ViewportLightingMode.Level && !material.IsUnlit && lod.StaticLighting is { UsesLightEnvironment: false };

    private static bool IncludesLightMappedLights(MaterialRenderProxy material, MeshRenderContext context)
        => !context.AreLightMapsActive || !material.UsesLightMap;

    // A replacement or newly baked material may have no cooked shader for the map's policy.
    // Render those baked lights dynamically only when the material has all required light shaders;
    // otherwise the established diffuse preview remains available instead of showing an unlit mesh.
    private static bool CanRenderLightingWithoutLightMap(ModelPreviewLOD<LEVertex> lod, MaterialRenderProxy material, MeshRenderContext context)
    {
        if (!UsesLevelLighting(lod, material, context) || context.IsUnlit
            || lod.StaticLighting.LightMapType == ELightMapType.LMT_None || !IncludesLightMappedLights(material, context))
            return true;
        // If baked lighting is enabled but its cooked shader is absent, turning dynamic lights off
        // cannot reconstruct it. Preserve the diffuse preview in that case; intentional unlit and
        // disabled light-map modes keep their normal base-pass selection.
        if (!context.IsLevelLightingActive) return !context.AreLightMapsActive;
        LightInteraction[] interactions = lod.StaticLighting.GetLightInteractions(lod.Mesh.TransformedBounds, includeLightMappedLights: true);
        SkyLighting sky = lod.StaticLighting.GetSkyLighting(lod.Mesh.TransformedBounds, includeLightMappedLights: true);
        if (interactions.Length == 0 && sky.IsBlack) return false;
        if (!sky.IsBlack && material.NoLightMapPixelShader?.ShaderType.Name != "TBasePassPixelShaderFNoLightMapPolicySkyLight") return false;
        foreach (LightInteraction interaction in interactions)
        {
            if (material.GetLightShaders(interaction.Light.Type, interaction.Shadowing) is not ({ }, { })) return false;
        }
        return true;
    }

    private bool RenderSectionWithGameShaders(ModelPreviewLOD<LEVertex> lod, ModelPreviewSection s, MeshRenderContext context)
    {
        Mesh<LEVertex> mesh = lod.Mesh;
        var material = (MaterialRenderProxy)Material;
        LEEffect effect = context.LEEffect;
        effect.EnsureFrame(context);
        if (material.HasFrameDependentUniforms) context.RejectStaticSceneCache();
        effect.PixelShaderResources.Reset();
        bool usesLevelLighting = UsesLevelLighting(lod, material, context);
        if (!CanRenderLightingWithoutLightMap(lod, material, context)) return false;
        //sky lights that aren't baked into the light-map are added in the base pass
        SkyLighting skyLighting = usesLevelLighting && context.IsLevelLightingActive ? lod.StaticLighting.GetSkyLighting(mesh.TransformedBounds,
            includeLightMappedLights: IncludesLightMappedLights(material, context)) : default;
        //a mesh lit by a light environment gets its directional light and SH (or sky) light in the base pass
        LightEnvironmentLighting lightEnvironment = !usesLevelLighting && lod.LightEnvironment is not null && context.IsLevelLightingActive && !material.IsUnlit
            ? lod.LightEnvironment.GetLighting() : null;
        //UE3 renders a shadow-casting light environment's SH light in its own pass after the modulated shadows, so that they don't darken it,
        //and the base pass has just the directional light. (Translucency isn't darkened by them, so it keeps the SH light in the base pass)
        (Shader vertexShader, Shader pixelShader) shLightPassShaders = default;
        SHVectorRGB shIncidentLighting = default;
        if (lightEnvironment is { UsesSHLight: true, RendersSHLightAfterModulatedShadows: true } && !IsTranslucent && context.AreModulatedShadowsActive
            && material.GetSHLightPassShaders() is ({ } shVertexShader, { } shPixelShader))
        {
            shLightPassShaders = (shVertexShader, shPixelShader);
            shIncidentLighting = lightEnvironment.SHIncidentLighting;
            lightEnvironment = lightEnvironment.DirectionalOnly;
        }
        bool usePreviewLighting = false;
        (Shader vertexShader, Shader pixelShader) = lightEnvironment is not null ? material.GetLightEnvironmentShaders(context, lightEnvironment)
            : material.GetBasePassShaders(context, usesLevelLighting, out usePreviewLighting, !skyLighting.IsBlack);
        if (vertexShader is null || pixelShader is null)
            return false;
        PixelShader ps = context.GetCachedMeshPixelShader(pixelShader.Guid, pixelShader.ShaderByteCode, material.UseSrgbColorManagement);
        (VertexShader vs, InputLayout inputLayout) = context.GetCachedVertexShader(vertexShader.Guid, vertexShader.ShaderByteCode);
        effect.PrepDraw(context.ImmediateContext, vs, ps, inputLayout, context.GetCachedBlendState(BlendDescription));
        //light-map UVs and samples. Unbound if there aren't any, since the shaders won't read them
        context.ImmediateContext.InputAssembler.SetVertexBuffers(MeshStaticLighting.VertexStreamSlot,
            new VertexBufferBinding(lod.StaticLighting?.VertexStream, MeshStaticLighting.VertexStreamStride, 0));
        //translucency is depth tested against the opaque geometry, but doesn't occlude anything itself
        DepthStencilState basePassDepthState = IsTranslucent ? context.TranslucentDepthState : context.DefaultDepthState;
        context.ImmediateContext.OutputMerger.SetDepthStencilState(basePassDepthState);
        //meshes whose transform mirrors them have reversed winding
        bool reverseCulling = mesh.LocalToWorld.GetDeterminant() < 0;
        context.SetMeshRasterizerState(material.IsTwoSided, reverseCulling);

        try
        {
            material.UpdateShaderParams(effect.VertexShaderConstantBuffer, effect.PixelShaderConstantBuffer, context, mesh, vertexShader, pixelShader,
                lod.StaticLighting, usePreviewLighting, skyLighting, lightEnvironment);

            effect.RenderObject(context.ImmediateContext, mesh, (int)s.StartIndex, (int)s.TriangleCount * 3);

            var hitProxyConstants = new LEHitProxyConstants
            {
                HitProxyID = context.CurrentHitTestId,
                Flags = context.RenderFlags
            };
            effect.RenderHitProxy(context.ImmediateContext, hitProxyConstants, (int)s.StartIndex, (int)s.TriangleCount * 3, basePassDepthState);
        }
        finally
        {
            context.ImmediateContext.OutputMerger.SetDepthStencilState(context.DefaultDepthState);
            context.RestoreRasterizerState();
            //the material's textures may have changed some samplers' address modes, which LEX's shaders don't expect
            context.RestoreDefaultSamplers();
        }
        if (shLightPassShaders.vertexShader is not null)
        {
            bool useCameraRelative = context.UseCameraRelativeNativeRendering;
            context.QueuePostModulatedShadowPass(() => DrawSHLightPass(lod, s, context, material, shLightPassShaders.vertexShader, shLightPassShaders.pixelShader,
                shIncidentLighting, useCameraRelative));
        }
        return true;
    }

    /// <summary>
    /// Adds a light environment's SH light to a section, in its own pass
    /// </summary>
    private static void DrawSHLightPass(ModelPreviewLOD<LEVertex> lod, ModelPreviewSection s, MeshRenderContext context, MaterialRenderProxy material,
        Shader vertexShader, Shader pixelShader, SHVectorRGB incidentLighting, bool useCameraRelative)
    {
        if (!material.CanRenderWithGameShaders)
        {
            return;
        }
        Mesh<LEVertex> mesh = lod.Mesh;
        LEEffect effect = context.LEEffect;
        bool previousCameraRelative = context.UseCameraRelativeNativeRendering;
        context.UseCameraRelativeNativeRendering = useCameraRelative;
        try
        {
            effect.EnsureFrame(context);
            effect.PixelShaderResources.Reset();
            context.ImmediateContext.InputAssembler.SetVertexBuffers(MeshStaticLighting.VertexStreamSlot,
                new VertexBufferBinding(lod.StaticLighting?.VertexStream, MeshStaticLighting.VertexStreamStride, 0));
            context.ImmediateContext.OutputMerger.SetDepthStencilState(context.LightPassDepthState);
            context.SetMeshRasterizerState(material.IsTwoSided, mesh.LocalToWorld.GetDeterminant() < 0);
            PixelShader ps = context.GetCachedMeshPixelShader(pixelShader.Guid, pixelShader.ShaderByteCode, material.UseSrgbColorManagement);
            (VertexShader vs, InputLayout inputLayout) = context.GetCachedVertexShader(vertexShader.Guid, vertexShader.ShaderByteCode);
            effect.PrepDraw(context.ImmediateContext, vs, ps, inputLayout, context.GetCachedBlendState(AdditiveBlendDescription));
            material.UpdateSHLightPassShaderParams(effect.VertexShaderConstantBuffer, effect.PixelShaderConstantBuffer, context, mesh, vertexShader, pixelShader,
                incidentLighting);
            effect.RenderObject(context.ImmediateContext, mesh, (int)s.StartIndex, (int)s.TriangleCount * 3);
        }
        catch (Exception e)
        {
            MarkLightPassFailed(material, e);
        }
        finally
        {
            context.UseCameraRelativeNativeRendering = previousCameraRelative;
            context.ImmediateContext.OutputMerger.SetDepthStencilState(context.DefaultDepthState);
            context.RestoreRasterizerState();
            context.RestoreDefaultSamplers();
        }
    }

    //lights add to what's already been rendered
    private static readonly RenderTargetBlendDescription AdditiveBlendDescription = new()
    {
        RenderTargetWriteMask = ColorWriteMaskFlags.Red | ColorWriteMaskFlags.Green | ColorWriteMaskFlags.Blue,
        BlendOperation = BlendOperation.Add,
        AlphaBlendOperation = BlendOperation.Add,
        SourceBlend = BlendOption.One,
        DestinationBlend = BlendOption.One,
        SourceAlphaBlend = BlendOption.Zero,
        DestinationAlphaBlend = BlendOption.One,
        IsBlendEnabled = true
    };

    /// <summary>
    /// Adds each of the level's lights that this section isn't light-mapped for, as UE3's TMeshLightingDrawingPolicy does.
    /// The passes are queued, and drawn by <see cref="MeshRenderContext.EndLightingPass"/> grouped by light
    /// </summary>
    public override void RenderLighting(ModelPreviewLOD<LEVertex> lod, ModelPreviewSection s, MeshRenderContext context)
    {
        var material = (MaterialRenderProxy)Material;
        //UE3 renders lit translucency differently, and unlit materials have no light shaders
        if (!context.UseGameShaders || !context.HasDefaultTextureChannels || !context.IsLevelLightingActive || context.Wireframe || IsTranslucent
            || !UsesLevelLighting(lod, material, context) || !material.CanRenderWithGameShaders
            || !CanRenderLightingWithoutLightMap(lod, material, context)
            || material.GetBasePassShaders(context, usesLevelLights: true, out _).vertexShader is null)
        {
            return;
        }
        foreach (LightInteraction interaction in lod.StaticLighting.GetLightInteractions(lod.Mesh.TransformedBounds,
                     includeLightMappedLights: IncludesLightMappedLights(material, context)))
        {
            (Shader vertexShader, Shader pixelShader) = material.GetLightShaders(interaction.Light.Type, interaction.Shadowing);
            if (vertexShader is not null)
            {
                bool useCameraRelative = context.UseCameraRelativeNativeRendering;
                context.QueueLightPass(interaction.Light, lightAttenuation => DrawLightPass(lod, s, context, material, interaction, vertexShader, pixelShader, lightAttenuation,
                    useCameraRelative));
            }
        }
    }

    private static void DrawLightPass(ModelPreviewLOD<LEVertex> lod, ModelPreviewSection s, MeshRenderContext context, MaterialRenderProxy material, LightInteraction interaction,
        Shader vertexShader, Shader pixelShader, ShaderResourceView lightAttenuation, bool useCameraRelative)
    {
        if (!material.CanRenderWithGameShaders)
        {
            return;
        }
        Mesh<LEVertex> mesh = lod.Mesh;
        LEEffect effect = context.LEEffect;
        bool previousCameraRelative = context.UseCameraRelativeNativeRendering;
        context.UseCameraRelativeNativeRendering = useCameraRelative;
        try
        {
            effect.EnsureFrame(context);
            effect.PixelShaderResources.Reset();
            context.ImmediateContext.InputAssembler.SetVertexBuffers(MeshStaticLighting.VertexStreamSlot,
                new VertexBufferBinding(lod.StaticLighting.VertexStream, MeshStaticLighting.VertexStreamStride, 0));
            context.ImmediateContext.OutputMerger.SetDepthStencilState(context.LightPassDepthState);
            context.SetMeshRasterizerState(material.IsTwoSided, mesh.LocalToWorld.GetDeterminant() < 0);
            PixelShader ps = context.GetCachedMeshPixelShader(pixelShader.Guid, pixelShader.ShaderByteCode, material.UseSrgbColorManagement);
            (VertexShader vs, InputLayout inputLayout) = context.GetCachedVertexShader(vertexShader.Guid, vertexShader.ShaderByteCode);
            effect.PrepDraw(context.ImmediateContext, vs, ps, inputLayout, context.GetCachedBlendState(AdditiveBlendDescription));
            context.ImmediateContext.InputAssembler.SetVertexBuffers(MeshStaticLighting.ShadowVertexStreamSlot,
                new VertexBufferBinding(interaction.ShadowVertexBuffer, MeshStaticLighting.ShadowVertexStreamStride, 0));
            material.UpdateLightPassShaderParams(effect.VertexShaderConstantBuffer, effect.PixelShaderConstantBuffer, context, mesh, vertexShader, pixelShader, interaction,
                lightAttenuation);
            effect.RenderObject(context.ImmediateContext, mesh, (int)s.StartIndex, (int)s.TriangleCount * 3);
        }
        catch (Exception e)
        {
            MarkLightPassFailed(material, e);
        }
        finally
        {
            context.UseCameraRelativeNativeRendering = previousCameraRelative;
            context.ImmediateContext.InputAssembler.SetVertexBuffers(MeshStaticLighting.ShadowVertexStreamSlot, new VertexBufferBinding(null, 0, 0));
            context.ImmediateContext.OutputMerger.SetDepthStencilState(context.DefaultDepthState);
            context.RestoreRasterizerState();
            context.RestoreDefaultSamplers();
        }
    }

    /// <summary>
    /// Like a failed base pass, a failed light pass makes the material fall back to LEX's shader, rather than the error stopping the whole scene from rendering.
    /// (Its passes already queued this frame are skipped)
    /// </summary>
    private static void MarkLightPassFailed(MaterialRenderProxy material, Exception e)
    {
        Debug.WriteLine($"A light pass of {material.InstancedFullPath} failed, so it will be rendered with the LEX shader: {e.Message}");
        material.MarkGameShadersFailed(e);
    }

    protected override MaterialInstanceConstantLevelEditor CreateMaterial(MeshRenderContext renderContext, ExportEntry export)
    {
        return new MaterialRenderProxy(renderContext, export);
    }
}

/// <summary>
/// Contains all the necessary resources (minus textures, which are cached in a <see cref="PreviewTextureCache"/>) needed to render a static preview of <see cref="SkeletalMesh"/> or <see cref="StaticMesh"/> instances.  
/// </summary>
public class ModelPreview<TVertex> : IDisposable where TVertex : IVertexBase
{
    private readonly Dictionary<string, ModelPreviewMaterial<TVertex>> materialEffectBaselines = [];
    private readonly Dictionary<(int Lod, RenderPass Pass),
        (ModelPreviewSection Section, ModelPreviewMaterial<TVertex> Material)[]> renderSectionsCache = [];
    /// <summary>
    /// Contains the geometry and section information for each level-of-detail in the model.
    /// </summary>
    public List<ModelPreviewLOD<TVertex>> LODs { get; } = [];

    /// <summary>
    /// Stores materials for this preview, stored by material name.
    /// </summary>
    public Dictionary<string, ModelPreviewMaterial<TVertex>> Materials { get; } = [];

    /// <summary>
    /// Whether the currently assigned materials can be retained in a static GPU scene cache.
    /// </summary>
    public bool IsStaticSceneCacheSafe => Materials.Values.All(material => !material.HasFrameDependentUniforms);

    /// <summary>
    /// Effective material entry for each mesh material slot. Component overrides are represented by
    /// their real entries rather than by copying package-local UIndexes into an external mesh binary.
    /// </summary>
    public List<IEntry> MaterialSlots { get; } = [];

    /// <summary>
    /// True only for skeletal previews that opted into the character color pipeline. Static previews
    /// keep their legacy shader and texture variants even when they share this render context.
    /// </summary>
    public bool UsesSrgbColorManagement { get; private set; }

    /// <summary>
    /// Creates a preview of a generic untextured mesh
    /// </summary>
    public ModelPreview(MeshRenderContext renderContext, Mesh<TVertex> mesh, PreloadedModelData preloadedData = null)
    {
        //Preloaded
        var sections = new List<ModelPreviewSection>();
        if (preloadedData != null)
        {
            sections = preloadedData.sections;
            foreach (ExportEntry mat in preloadedData.Materials.Distinct())
            {
                AddMaterial(renderContext, mat);
            }
        }
        LODs.Add(new ModelPreviewLOD<TVertex>(mesh, sections));
    }

    /// <summary>
    /// Creates a preview of the given <see cref="StaticMesh"/>.
    /// </summary>
    public ModelPreview(MeshRenderContext renderContext, StaticMesh m, int selectedLOD,
        IReadOnlyList<IEntry> materialOverrides = null, MeshStaticLighting staticLighting = null)
    {
        if (selectedLOD < 0)  //PREVIEW BUG WORKAROUND
            return;

        // STEP 1: MESH. Geometry and immutable GPU buffers are shared between components that use
        // the same asset/LOD; transforms and materials remain per component.
        var lodModel = m.LODModels[selectedLOD];
        Mesh<TVertex> mesh = renderContext.GetOrCreateCachedMesh<TVertex>(m.Export, selectedLOD, () =>
        {
            var triangles = new List<Triangle>(lodModel.IndexBuffer.Length / 3);
            var vertices = new List<TVertex>((int)lodModel.NumVertices);
            StaticMeshVertexBuffer vertexBuffer = lodModel.VertexBuffer;
            for (int i = 0; i < lodModel.NumVertices; i++)
            {
                var position = lodModel.PositionVertexBuffer.VertexData[i];
                var vertex = vertexBuffer.VertexData[i];
                Fixed4<Vector4> uvs = default;
                if (vertexBuffer.bUseFullPrecisionUVs)
                {
                    for (int j = 0; j < uvs.Length && j < vertex.FullPrecisionUVs.Length; j++)
                    {
                        uvs[j] = new Vector4(vertex.FullPrecisionUVs[j], 0, 0);
                    }
                }
                else
                {
                    for (int j = 0; j < uvs.Length && j < vertex.HalfPrecisionUVs.Length; j++)
                    {
                        uvs[j] = new Vector4(vertex.HalfPrecisionUVs[j], 0, 0);
                    }
                }
                TVertex created = (TVertex)TVertex.Create(m.Export.Game, new Vector3(position.X, position.Y, position.Z), (Vector3)vertex.TangentX, (Vector4)vertex.TangentZ, uvs);
                if (created is LEVertex leVertex && lodModel.ColorVertexBuffer is { VertexData: { Length: > 0 } colors } && i < colors.Length)
                {
                    var c = colors[i];
                    created = (TVertex)(object)leVertex.WithColor(new Vector4(c.R, c.G, c.B, c.A) / 255f);
                }
                vertices.Add(created);
            }

            if (lodModel.IndexBuffer.Length > 0)
            {
                for (int i = 0; i < lodModel.IndexBuffer.Length; i += 3)
                {
                    triangles.Add(new Triangle(lodModel.IndexBuffer[i], lodModel.IndexBuffer[i + 1], lodModel.IndexBuffer[i + 2]));
                }
            }
            else if (m.Export.Game <= MEGame.ME2)
            {
                var kdop = m.kDOPTreeME1ME2;
                for (int i = 0; i < kdop.Triangles.Length; i++)
                {
                    triangles.Add(new Triangle(kdop.Triangles[i].Vertex1, kdop.Triangles[i].Vertex2, kdop.Triangles[i].Vertex3));
                }
            }
            else
            {
                var kdop = m.kDOPTreeME3UDKLE;
                for (int i = 0; i < kdop.Triangles.Length; i++)
                {
                    triangles.Add(new Triangle(kdop.Triangles[i].Vertex1, kdop.Triangles[i].Vertex2, kdop.Triangles[i].Vertex3));
                }
            }
            return (triangles, vertices);
        });

        // STEP 3: SECTIONS

        var sections = new List<ModelPreviewSection>();
        for (int materialSlot = 0; materialSlot < lodModel.Elements.Length; materialSlot++)
        {
            StaticMeshElement element = lodModel.Elements[materialSlot];
            if (element.Material is 0 || !m.Export.FileRef.IsEntry(element.Material))
            {
                IEntry overrideEntry = materialOverrides is not null && materialSlot < materialOverrides.Count
                    ? materialOverrides[materialSlot]
                    : null;
                MaterialSlots.Add(overrideEntry);
                if (overrideEntry is not null && AddMaterial(renderContext, overrideEntry, staticLighting?.LightMapType ?? ELightMapType.LMT_None))
                {
                    sections.Add(new ModelPreviewSection(overrideEntry.InstancedFullPath, element.FirstIndex, element.NumTriangles));
                }
                else
                {
                    sections.Add(new ModelPreviewSection(null, element.FirstIndex, element.NumTriangles));
                }
            }
            else
            {
                IEntry matEntry = materialOverrides is not null && materialSlot < materialOverrides.Count
                                  && materialOverrides[materialSlot] is { } overrideEntry
                    ? overrideEntry
                    : m.Export.FileRef.GetEntry(element.Material);
                MaterialSlots.Add(matEntry);
                if (AddMaterial(renderContext, matEntry, staticLighting?.LightMapType ?? ELightMapType.LMT_None))
                {
                    sections.Add(new ModelPreviewSection(matEntry.InstancedFullPath, element.FirstIndex, element.NumTriangles));
                }
                else
                {
                    sections.Add(new ModelPreviewSection(null, element.FirstIndex, element.NumTriangles));
                }
            }
        }
        LODs.Add(new ModelPreviewLOD<TVertex>(mesh, sections) { StaticLighting = staticLighting });
        if (staticLighting is not null)
        {
            staticLighting.GetBounds = () => mesh.TransformedBounds;
            staticLighting.GetLocalToWorld = () => mesh.LocalToWorld;
            staticLighting.Materials = Materials.Values.Cast<object>().OfType<LEShaderPreviewMaterial>().Select(mat => mat.RenderProxy).ToArray();
            staticLighting.DrawShadowCaster = ctx => DrawShadowCasterSections(ctx, mesh, sections);
        }
    }

    /// <summary>
    /// For shadow depth passes: binds a LOD's buffers and draws its sections, except translucent ones, which don't cast dynamic shadows. The caller sets the shaders
    /// </summary>
    internal void DrawShadowCaster(DeviceContext ctx, int lod)
    {
        if (lod < LODs.Count)
        {
            DrawShadowCasterSections(ctx, LODs[lod].Mesh, LODs[lod].Sections);
        }
    }

    /// <summary>
    /// For shadow depth passes: binds the mesh's buffers and draws its sections, except translucent ones, which don't cast dynamic shadows. The caller sets the shaders
    /// </summary>
    private void DrawShadowCasterSections(DeviceContext ctx, Mesh<TVertex> mesh, List<ModelPreviewSection> sections)
    {
        if (mesh.Vertices.Count is 0)
        {
            return;
        }
        ctx.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
        ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(mesh.VertexBuffer, TVertex.Stride, 0));
        ctx.InputAssembler.SetIndexBuffer(mesh.IndexBuffer, SharpDX.DXGI.Format.R32_UInt, 0);
        foreach (ModelPreviewSection section in sections)
        {
            if (section.MaterialName is not null && Materials.TryGetValue(section.MaterialName, out ModelPreviewMaterial<TVertex> material)
                && material is LEShaderPreviewMaterial { RenderProxy.BlendMode: not (EBlendMode.BLEND_Opaque or EBlendMode.BLEND_Masked) })
            {
                continue;
            }
            ctx.DrawIndexed((int)section.TriangleCount * 3, (int)section.StartIndex, 0);
        }
    }

    /// <summary>
    /// Creates a preview of the given <see cref="SkeletalMesh"/>.
    /// </summary>
    public ModelPreview(MeshRenderContext renderContext, SkeletalMesh m,
        IReadOnlyList<IEntry> materialOverrides = null, bool loadOnlyFirstLod = false, MeshStaticLighting staticLighting = null, int lightingLOD = 0)
    {
        UsesSrgbColorManagement = renderContext.UseSrgbColorManagement && typeof(TVertex) == typeof(LEVertex);
        var mats = new string[m.Materials.Length];
        // STEP 1: MATERIALS
        for (int i = 0; i < m.Materials.Length; i++)
        {
            int materialUIndex = m.Materials[i];
            if (materialUIndex is not 0)
            {
                IEntry matEntry = materialOverrides is not null && i < materialOverrides.Count
                                  && materialOverrides[i] is { } overrideEntry
                    ? overrideEntry
                    : m.Export.FileRef.GetEntry(materialUIndex);
                MaterialSlots.Add(matEntry);
                if (AddMaterial(renderContext, matEntry, staticLighting?.LightMapType ?? ELightMapType.LMT_None))
                {
                    mats[i] = matEntry.InstancedFullPath;
                }
            }
            else
            {
                IEntry overrideEntry = materialOverrides is not null && i < materialOverrides.Count
                    ? materialOverrides[i]
                    : null;
                MaterialSlots.Add(overrideEntry);
                if (overrideEntry is not null && AddMaterial(renderContext, overrideEntry, staticLighting?.LightMapType ?? ELightMapType.LMT_None))
                {
                    mats[i] = overrideEntry.InstancedFullPath;
                }
            }
        }

        // STEP 2: LODS
        int lodCount = loadOnlyFirstLod ? Math.Min(1, m.LODModels.Length) : m.LODModels.Length;
        for (int lodIndex = 0; lodIndex < lodCount; lodIndex++)
        {
            var lodmodel = m.LODModels[lodIndex];
            Mesh<TVertex> mesh = renderContext.GetOrCreateCachedMesh<TVertex>(m.Export, lodIndex, () =>
            {
                // Vertices
                var vertices = new List<TVertex>(m.Export.Game == MEGame.ME1 ? lodmodel.ME1VertexBufferGPUSkin.Length : lodmodel.VertexBufferGPUSkin.VertexData.Length);
                Fixed4<Vector4> uvs = default;
                if (m.Export.Game == MEGame.ME1)
                {
                    foreach (SoftSkinVertex vertex in lodmodel.ME1VertexBufferGPUSkin)
                    {
                        uvs[0] = new Vector4(vertex.UV, 0, 0);
                        vertices.Add((TVertex)TVertex.Create(m.Export.Game, new Vector3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z), (Vector3)vertex.TangentX, (Vector4)vertex.TangentZ, uvs));
                    }
                }
                else
                {
                    foreach (GPUSkinVertex vertex in lodmodel.VertexBufferGPUSkin.VertexData)
                    {
                        uvs[0] = new Vector4(vertex.UV, 0, 0);
                        vertices.Add((TVertex)TVertex.Create(m.Export.Game, new Vector3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z), (Vector3)vertex.TangentX, (Vector4)vertex.TangentZ, uvs));
                    }
                }
                // Triangles
                var triangles = new List<Triangle>(lodmodel.IndexBuffer.Length / 3);
                for (int i = 0; i < lodmodel.IndexBuffer.Length; i += 3)
                {
                    triangles.Add(new Triangle(lodmodel.IndexBuffer[i], lodmodel.IndexBuffer[i + 1], lodmodel.IndexBuffer[i + 2]));
                }
                return (triangles, vertices);
            });
            // Sections
            var sections = new List<ModelPreviewSection>();
            foreach (var section in lodmodel.Sections)
            {
                if (section.MaterialIndex < mats.Length)
                {
                    sections.Add(new ModelPreviewSection(mats[section.MaterialIndex], section.BaseIndex, (uint)section.NumTriangles));
                }
            }
            bool isLightingLOD = staticLighting is not null && LODs.Count == lightingLOD;
            LODs.Add(new ModelPreviewLOD<TVertex>(mesh, sections) { StaticLighting = isLightingLOD ? staticLighting : null });
            if (isLightingLOD)
            {
                staticLighting.GetBounds = () => mesh.TransformedBounds;
                staticLighting.GetLocalToWorld = () => mesh.LocalToWorld;
                staticLighting.Materials = Materials.Values.Cast<object>().OfType<LEShaderPreviewMaterial>().Select(mat => mat.RenderProxy).ToArray();
                staticLighting.DrawShadowCaster = ctx => DrawShadowCasterSections(ctx, mesh, sections);
            }
        }
        if (staticLighting is not null && LODs.Count <= lightingLOD)
        {
            staticLighting.Dispose();
        }
    }

    /// <summary>
    /// Adds a <see cref="ModelPreviewMaterial"/> to this model, or adds another reference of any conflicting material.
    /// </summary>
    internal bool AddMaterial(MeshRenderContext renderContext, IEntry matEntry, ELightMapType lightMapType = ELightMapType.LMT_None)
    {
        if (matEntry is null)
        {
            Debug.WriteLine("Could not find material entry in the mesh's package.");
            return false;
        }

        string ifp = matEntry.InstancedFullPath;
        if (!Materials.ContainsKey(ifp))
        {
            if (matEntry is not ExportEntry matExport)
            {
                matExport = renderContext.ResolveExportCached(matEntry);
                if (matExport is null)
                {
                    Debug.WriteLine("Could not find import material.");
                    Debug.WriteLine($"Import material: '{ifp}' from '{matEntry.FileRef.FilePath}'");
                    return false;
                }
            }
            switch (Materials)
            {
                case Dictionary<string, ModelPreviewMaterial<WorldVertex>> worldVertMats:
                    worldVertMats.Add(ifp, new TexturedPreviewMaterial(renderContext, matExport));
                    break;
                case Dictionary<string, ModelPreviewMaterial<LEVertex>> leVertMats:
                    leVertMats.Add(ifp, new LEShaderPreviewMaterial(renderContext, matExport,
                        UsesSrgbColorManagement, lightMapType));
                    break;
            }
            renderSectionsCache.Clear();
        }

        return true;
    }

    public bool OverrideSectionMaterials(MeshRenderContext renderContext, ExportEntry materialExport)
    {
        if (materialExport is null || !AddMaterial(renderContext, materialExport))
            return false;

        string materialName = materialExport.InstancedFullPath;
        foreach (var lod in LODs)
        {
            for (int i = 0; i < lod.Sections.Count; i++)
            {
                ModelPreviewSection section = lod.Sections[i];
                if (section.MaterialName is not null)
                {
                    section.MaterialName = materialName;
                    lod.Sections[i] = section;
                }
            }
        }

        renderSectionsCache.Clear();

        return true;
    }

    /// <summary>
    /// Applies an Rvr receiver effect only to mesh sections whose effects-material multiplexor exposes the
    /// requested name. Unsupported materials remain unchanged, preserving the effect's authored shape/coverage.
    /// </summary>
    public int ApplyNamedRvrMaterialEffect(MeshRenderContext renderContext, string effectName)
        => ApplyNamedRvrMaterialEffects(renderContext, [effectName]);

    public int ApplyNamedRvrMaterialEffects(MeshRenderContext renderContext, IEnumerable<string> effectNames)
    {
        ClearNamedRvrMaterialEffect();
        string[] names = effectNames?.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        if (names.Length == 0 || Materials is not Dictionary<string, ModelPreviewMaterial<LEVertex>> gameShaderMaterials)
        {
            return 0;
        }

        foreach ((string materialName, ModelPreviewMaterial<LEVertex> material) in gameShaderMaterials.ToList())
        {
            if (material is not LEShaderPreviewMaterial shaderMaterial)
            {
                continue;
            }
            ExportEntry effectMaterial = names.Select(name => material.MaterialExport.ResolveRvrEffectMaterial(
                    name, renderContext.PackageCache))
                .FirstOrDefault(candidate => candidate is not null);
            if (effectMaterial is null)
            {
                continue;
            }

            LEShaderPreviewMaterial effectPreview;
            try
            {
                effectPreview = shaderMaterial.CreateEffectOverride(renderContext, effectMaterial);
            }
            catch
            {
                continue;
            }
            if (!effectPreview.CanRender)
            {
                continue;
            }

            materialEffectBaselines[materialName] = (ModelPreviewMaterial<TVertex>)(object)material;
            gameShaderMaterials[materialName] = effectPreview;
        }
        if (materialEffectBaselines.Count > 0)
        {
            renderSectionsCache.Clear();
        }
        return materialEffectBaselines.Count;
    }

    /// <summary>
    /// Restores the actor's authored section materials. Called before every new VFX selection so effects cannot
    /// leak from one receiver/client effect into the next particle system or client effect.
    /// </summary>
    public void ClearNamedRvrMaterialEffect()
    {
        bool changed = materialEffectBaselines.Count > 0;
        foreach ((string materialName, ModelPreviewMaterial<TVertex> material) in materialEffectBaselines)
        {
            Materials[materialName] = material;
        }
        materialEffectBaselines.Clear();
        if (changed)
        {
            renderSectionsCache.Clear();
        }
    }
    /// <summary>
    /// Renders the ModelPreview at the specified level of detail
    /// </summary>
    /// <param name="renderPass">Only render materials that use this pass.</param>
    /// <param name="view">The SceneRenderControl to render the preview into.</param>
    /// <param name="lod">Which level of detail to render at. Level 0 is traditionally the most detailed.</param>
    public void Render(RenderPass renderPass, MeshRenderContext view, int lod)
    {
        if (lod >= LODs.Count) return;

        bool outsideView = view.CullingFrustum is { } frustum && !frustum.Intersects(LODs[lod].Mesh.TransformedBounds);
        if (renderPass is RenderPass.Base or RenderPass.Hair)
        {
            if (!renderSectionsCache.TryGetValue((lod, renderPass), out var renderSections))
            {
                var matchingSections = new List<(ModelPreviewSection, ModelPreviewMaterial<TVertex>)>();
                foreach (ModelPreviewSection section in LODs[lod].Sections)
                {
                    if (section.MaterialName is not null
                        && Materials.TryGetValue(section.MaterialName, out ModelPreviewMaterial<TVertex> material)
                        && material.Pass == renderPass)
                    {
                        matchingSections.Add((section, material));
                    }
                }
                renderSections = matchingSections.ToArray();
                renderSectionsCache[(lod, renderPass)] = renderSections;
            }

            foreach ((ModelPreviewSection section, ModelPreviewMaterial<TVertex> material) in renderSections)
            {
                if (!outsideView || !material.CanCullByBounds(view))
                    material.RenderSection(LODs[lod], section, view);
            }

            if (renderPass is RenderPass.Base && view.Wireframe && LODs[lod].Mesh is Mesh<WorldVertex> wireMesh)
            {
                foreach (ModelPreviewSection section in LODs[lod].Sections)
                {
                    if (section.MaterialName is null)
                    {
                        view.RenderMeshAsWireframe(wireMesh, section);
                    }
                }
            }
            return;
        }

        foreach (ModelPreviewSection section in LODs[lod].Sections)
        {
            if (section.MaterialName is null)
            {
                if (outsideView) continue;
                if (view.Wireframe && LODs[lod].Mesh is Mesh<WorldVertex> mesh && renderPass is RenderPass.Base or RenderPass.ANY)
                {
                    view.RenderMeshAsWireframe(mesh, section);
                }
            }
            else if (Materials.TryGetValue(section.MaterialName, out ModelPreviewMaterial<TVertex> material))
            {
                // Test before shader setup, light interactions, and the base pass's hit-proxy draw.
                if (outsideView && material.CanCullByBounds(view)) continue;
                if (renderPass is RenderPass.Lighting)
                {
                    material.RenderLighting(LODs[lod], section, view);
                }
                else if (material.Pass == renderPass || renderPass is RenderPass.ANY)
                {
                    material.RenderSection(LODs[lod], section, view);
                }
            }
        }
    }

    public void UpdateLocalToWorld(Matrix4x4 ltw)
    {
        foreach (var lod in LODs)
        {
            lod.Mesh.LocalToWorld = ltw;
        }
    }

    /// <summary>
    /// Creates the immutable D3D shader/layout/blend objects needed by this preview before it is published
    /// to the render thread. Non-game-shader materials have no lazy graphics objects to prepare here.
    /// </summary>
    internal void PrepareGraphicsResources(MeshRenderContext context)
    {
        foreach (LEShaderPreviewMaterial material in Materials.Values.OfType<LEShaderPreviewMaterial>())
        {
            material.PrepareGraphicsResources(context);
        }
        foreach (MeshStaticLighting staticLighting in LODs.Select(lod => lod.StaticLighting).Where(lighting => lighting is not null).Distinct())
        {
            staticLighting.PrepareLightShaders();
            if (staticLighting.UsesLightEnvironment)
            {
                foreach (MaterialRenderProxy material in staticLighting.Materials)
                {
                    material.GetSHLightPassShaders();
                }
            }
        }
    }

    /// <summary>
    /// Disposes any outstanding resources.
    /// </summary>
    public void Dispose()
    {
        ClearNamedRvrMaterialEffect();
        Materials.Clear();
        foreach (var lod in LODs)
        {
            lod.Mesh.Dispose();
            lod.StaticLighting?.Dispose();
        }
        LODs.Clear();
    }
}

public class PreloadedModelData
{
    public object meshObject;
    public List<ModelPreviewSection> sections;
    public List<IEntry> Materials;

    public static PreloadedModelData LoadModel(ExportEntry export, PackageCache assetCache)
    {
        List<string> alreadyLoadedImportMaterials = [];
        var modelComp = ObjectBinary.From<Model>(export);
        var pmd = new PreloadedModelData
        {
            meshObject = modelComp,
            sections = [],
            Materials = [],
        };
        foreach (var mcExp in modelComp.Export.FileRef.Exports.Where(x =>
            x.ClassName == "ModelComponent" && !x.IsDefaultObject))
        {
            var mc = ObjectBinary.From<ModelComponent>(mcExp);
            if (mc.Model == modelComp.Self)
            {
                foreach (var element in mc.Elements)
                {
                    if (export == null) return pmd;
                    if (export.FileRef.IsUExport(element.Material))
                    {
                        ExportEntry entry = export.FileRef.GetUExport(element.Material);
                        AddMaterial(pmd.Materials, entry);
                    }
                    else if (export.FileRef.TryGetImport(element.Material, out var matImp) &&
                             alreadyLoadedImportMaterials.All(x => x != matImp.InstancedFullPath))
                    {
                        var extMaterialExport = EntryImporter.ResolveImport(matImp, assetCache);
                        if (extMaterialExport != null)
                        {
                            AddMaterial(pmd.Materials, extMaterialExport);
                            alreadyLoadedImportMaterials.Add(extMaterialExport.InstancedFullPath);
                        }
                        else
                        {
                            Debug.WriteLine("Could not find import material from FModelElement.");
                            Debug.WriteLine("Import material: " +
                                            export.FileRef.GetEntryString(element.Material));
                        }
                    }
                }
            }
        }
        return pmd;
    }
    public static PreloadedModelData LoadModelComponent(ExportEntry export, PackageCache assetCache)
    {
        var modelComp = ObjectBinary.From<ModelComponent>(export);
        var pmd = new PreloadedModelData
        {
            meshObject = modelComp,
            sections = [],
            Materials = [],
        };

        foreach (var element in modelComp.Elements)
        {
            if (export != null)
            {
                if (export.FileRef.TryGetUExport(element.Material, out var matExp))
                {
                    AddMaterial(pmd.Materials, matExp);
                    pmd.sections.Add(new ModelPreviewSection(matExp.InstancedFullPath, 0, 3)); //???
                }
                else if (export.FileRef.TryGetImport(element.Material, out var matImp))
                {
                    var extMaterialExport = EntryImporter.ResolveImport(matImp, assetCache);
                    //var extMaterialExport = ModelPreview.FindExternalAsset(matImp, pmd.texturePreviewMaterials.Select(x => x.Mip.Export).ToList(), cachedPackages);
                    if (extMaterialExport != null)
                    {
                        AddMaterial(pmd.Materials, extMaterialExport);
                    }
                    else
                    {
                        Debug.WriteLine("Could not find import material from section.");
                        Debug.WriteLine("Import material: " + export.FileRef.GetEntryString(element.Material));
                    }
                }
            }
        }
        return pmd;
    }

    private static void AddMaterial(List<IEntry> texturePreviewMaterials, ExportEntry entry)
    {
        if (texturePreviewMaterials.Any(x => x.InstancedFullPath == entry.InstancedFullPath))
            return; //already cached

        texturePreviewMaterials.Add(entry);
    }
}
