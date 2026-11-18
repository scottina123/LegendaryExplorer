using System;
using System.Collections.Generic;
using System.Linq;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

public abstract partial class ModelPreviewMaterial<Vertex>
{
    /// <summary>The material export this preview material was made from</summary>
    public ExportEntry MaterialExport { get; }

    /// <summary>Whether its uniform expressions change with time (fork: frame reuse checks)</summary>
    public virtual bool HasFrameDependentUniforms => false;
}

internal partial class LEShaderPreviewMaterial
{
    /// <summary>
    /// Fork: whether the game's base pass shaders for the local vertex factory were found, so the material can draw with them.
    /// Loads them if they haven't been
    /// </summary>
    public bool CanRender
    {
        get
        {
            MaterialRenderProxy material = RenderProxy;
            if (material.UnrealVertexShader is null || material.UnrealPixelShader is null) material.LoadGameShaders();
            return material.UnrealVertexShader is not null && material.UnrealPixelShader is not null;
        }
    }

    public override bool HasFrameDependentUniforms => RenderProxy.HasFrameDependentUniforms;

    /// <summary>
    /// Fork: a preview of <paramref name="effectMaterial"/> (an Rvr receiver effect) drawn in this material's place, with this material's
    /// parameter values and light-map type
    /// </summary>
    internal LEShaderPreviewMaterial CreateEffectOverride(MeshRenderContext renderContext, ExportEntry effectMaterial)
    {
        var effect = new LEShaderPreviewMaterial(renderContext, effectMaterial, RenderProxy.LightMapType) { Pass = Pass };
        effect.RenderProxy.CopyEffectiveParametersFrom(RenderProxy);
        return effect;
    }

    /// <summary>
    /// Fork: loads the material's shaders and creates their D3D objects ahead of the first draw (from a background thread is fine: upstream's
    /// shader caches lock on creation)
    /// </summary>
    internal void PrepareGraphicsResources(MeshRenderContext context)
    {
        MaterialRenderProxy material = RenderProxy;
        material.LoadGameShaders();
        if (material.UnrealVertexShader is null || material.UnrealPixelShader is null) return;
        context.GetCachedVertexShader(material.UnrealVertexShader.Guid, material.UnrealVertexShader.ShaderByteCode, material.VertexInputElements);
        context.GetCachedPixelShader(material.UnrealPixelShader.Guid, material.UnrealPixelShader.ShaderByteCode);
    }
}

public partial class ModelPreview<TVertex>
{
    /// <summary>
    /// Fork: whether the fork's sRGB pipeline would be used. Upstream renders linearly regardless; informational
    /// </summary>
    public bool UsesSrgbColorManagement => RenderContext.UseSrgbColorManagement && typeof(TVertex) == typeof(LEVertex);

    /// <summary>
    /// Fork's argument order for a skeletal mesh preview (material overrides first). Same as upstream's constructor
    /// </summary>
    public ModelPreview(MeshRenderContext renderContext, LegendaryExplorerCore.Unreal.BinaryConverters.SkeletalMesh m, IReadOnlyList<IEntry> materialOverrides,
        bool loadOnlyFirstLod = false, MeshStaticLighting staticLighting = null, int lightingLOD = 0)
        : this(renderContext, m, staticLighting, lightingLOD, materialOverrides, loadOnlyFirstLod) { }

    private readonly Dictionary<string, ModelPreviewMaterial<TVertex>> materialEffectBaselines = [];

    /// <summary>Forgets the LODs' resolved section materials, after <see cref="Materials"/> or a section's material changed</summary>
    private void InvalidateSectionMaterials()
    {
        foreach (ModelPreviewLOD<TVertex> lod in LODs) lod.SectionMaterials = null;
    }

    /// <summary>
    /// Fork: loads every game-shader material's shaders and creates their D3D objects, so the first frame that draws this preview doesn't
    /// </summary>
    internal void PrepareGraphicsResources(MeshRenderContext context)
    {
        foreach (LEShaderPreviewMaterial material in Materials.Values.Cast<object>().OfType<LEShaderPreviewMaterial>())
        {
            material.PrepareGraphicsResources(context);
        }
    }

    /// <summary>
    /// Fork (galaxy map): draws every materialed section with one material instead of its own
    /// </summary>
    public bool OverrideSectionMaterials(MeshRenderContext renderContext, ExportEntry materialExport)
    {
        if (materialExport is null || !AddMaterial(renderContext, materialExport)) return false;

        string materialName = materialExport.InstancedFullPath;
        foreach (ModelPreviewLOD<TVertex> lod in LODs)
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
        InvalidateSectionMaterials();
        return true;
    }

    public int ApplyNamedRvrMaterialEffect(MeshRenderContext renderContext, string effectName) => ApplyNamedRvrMaterialEffects(renderContext, [effectName]);

    /// <summary>
    /// Fork (VFX preview): draws an Rvr receiver effect on the sections whose effects-material multiplexor exposes one of the names. Other
    /// sections keep their material. Returns how many materials were replaced
    /// </summary>
    public int ApplyNamedRvrMaterialEffects(MeshRenderContext renderContext, IEnumerable<string> effectNames)
    {
        ClearNamedRvrMaterialEffect();
        string[] names = effectNames?.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        if (names.Length == 0 || Materials is not Dictionary<string, ModelPreviewMaterial<LEVertex>> gameShaderMaterials)
        {
            return 0;
        }

        foreach ((string materialName, ModelPreviewMaterial<LEVertex> material) in gameShaderMaterials.ToList())
        {
            if (material is not LEShaderPreviewMaterial shaderMaterial || material.MaterialExport is null) continue;
            ExportEntry effectMaterial = names.Select(name => material.MaterialExport.ResolveRvrEffectMaterial(name, renderContext.PackageCache))
                .FirstOrDefault(candidate => candidate is not null);
            if (effectMaterial is null) continue;

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
                effectPreview.RenderProxy.CancelPendingShaderLoad();
                continue;
            }

            materialEffectBaselines[materialName] = (ModelPreviewMaterial<TVertex>)(object)material;
            gameShaderMaterials[materialName] = effectPreview;
        }
        if (materialEffectBaselines.Count > 0) InvalidateSectionMaterials();
        return materialEffectBaselines.Count;
    }

    /// <summary>Restores the materials <see cref="ApplyNamedRvrMaterialEffects"/> replaced</summary>
    public void ClearNamedRvrMaterialEffect()
    {
        if (materialEffectBaselines.Count == 0) return;
        foreach ((string materialName, ModelPreviewMaterial<TVertex> material) in materialEffectBaselines)
        {
            if (Materials.TryGetValue(materialName, out var effect) && effect is LEShaderPreviewMaterial effectPreview)
            {
                effectPreview.RenderProxy.CancelPendingShaderLoad();
            }
            Materials[materialName] = material;
        }
        materialEffectBaselines.Clear();
        InvalidateSectionMaterials();
    }
}
