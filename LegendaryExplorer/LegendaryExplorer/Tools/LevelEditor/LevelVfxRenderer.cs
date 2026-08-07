using LegendaryExplorer.Tools.AssetDatabase.VFXPreview;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpDX.Direct3D11;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Shared, native-shader particle resources for the Level Editor. Particle definitions and cooked shader
/// resources are cached per ParticleSystem, while each placed component keeps its own lightweight simulation.
/// </summary>
internal sealed class LevelVfxRenderer : IDisposable
{
    internal sealed class Instance
    {
        public PreparedSystem System { get; }
        public VfxSimulation Simulation { get; } = new();

        public Instance(PreparedSystem system)
        {
            System = system;
            Simulation.Load(system.Definition);
        }
    }

    internal sealed class PreparedSystem : IDisposable
    {
        private readonly VfxGameShaderRenderer gameShaderRenderer = new();
        private readonly Dictionary<VfxEmitterDefinition, VfxMeshRenderer.MeshEmitterResources> meshEmitters = [];

        public VfxPreviewDefinition Definition { get; }
        public bool HasRenderableEmitter { get; private set; }

        public PreparedSystem(VfxPreviewDefinition definition)
        {
            Definition = definition;
        }

        public void Prepare(LevelEditorRenderContext context, LevelVfxRenderer owner)
        {
            foreach (VfxEmitterDefinition emitter in Definition.Emitters)
            {
                bool loaded = false;
                try
                {
                    ExportEntry materialExport = context.ResolveExportCached(emitter.Material);
                    if (materialExport is not null)
                    {
                        owner.PopulateNativeMaterial(context, materialExport, emitter.ParticleMaterial);
                    }

                    switch (emitter.RenderMode)
                    {
                        case VfxEmitterRenderMode.Sprite when materialExport is not null:
                            loaded = gameShaderRenderer.TryLoadSprite(context, emitter, materialExport, out string spriteWarning);
                            TraceWarning(emitter, spriteWarning);
                            break;
                        case VfxEmitterRenderMode.Beam when materialExport is not null:
                        case VfxEmitterRenderMode.Trail when materialExport is not null:
                            loaded = gameShaderRenderer.TryLoadBeamTrail(context, emitter, materialExport, out string beamWarning);
                            TraceWarning(emitter, beamWarning);
                            break;
                        case VfxEmitterRenderMode.Mesh:
                            loaded = PrepareMeshEmitter(context, owner, emitter);
                            break;
                    }
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Level Editor VFX: {emitter.Name}: {exception.Message}");
                }
                HasRenderableEmitter |= loaded;
            }
        }

        private bool PrepareMeshEmitter(LevelEditorRenderContext context, LevelVfxRenderer owner,
            VfxEmitterDefinition emitter)
        {
            VfxMeshEmitterDefinition meshDefinition = emitter.MeshEmitter;
            if (meshDefinition?.Mesh is null)
            {
                return false;
            }

            VfxMeshRenderer.MeshEmitterResources resources = VfxMeshRenderer.LoadMesh(context, meshDefinition);
            if (resources is null)
            {
                return false;
            }
            try
            {
                for (int sectionIndex = 0; sectionIndex < resources.Sections.Count; sectionIndex++)
                {
                    VfxMeshRenderer.MeshSection section = resources.Sections[sectionIndex];
                    IEntry materialEntry = ResolveMeshSectionMaterial(emitter, meshDefinition, section, sectionIndex);
                    ExportEntry materialExport = context.ResolveExportCached(materialEntry);
                    if (materialExport is null)
                    {
                        resources.Dispose();
                        return false;
                    }

                    section.GameShaderMaterial = materialExport;
                    owner.PopulateNativeMaterial(context, materialExport, section.Material);
                    section.IsOpaque = section.Material.BlendMode is VfxBlendMode.Opaque or VfxBlendMode.Masked;
                }

                if (!VfxMeshRenderer.TryLoadGameShaderPreview(context, resources, out string warning))
                {
                    TraceWarning(emitter, warning);
                    resources.Dispose();
                    return false;
                }

                // Level Editor never uses the standard VFX fallback. Release the duplicate WorldVertex copy once
                // the Meshplorer/Morph Editor native-material copy is ready.
                resources.Preview?.Dispose();
                resources.Preview = null;
                resources.Mesh = null;
                meshEmitters[emitter] = resources;
                return true;
            }
            catch
            {
                resources.Dispose();
                throw;
            }
        }

        private static IEntry ResolveMeshSectionMaterial(VfxEmitterDefinition emitter,
            VfxMeshEmitterDefinition meshDefinition, VfxMeshRenderer.MeshSection section, int sectionIndex)
        {
            if (sectionIndex < meshDefinition.SectionMaterialOverrides.Count
                && meshDefinition.SectionMaterialOverrides[sectionIndex] is { } sectionOverride)
            {
                return sectionOverride;
            }
            if (meshDefinition.OverrideMaterial && emitter.Material is not null)
            {
                return emitter.Material;
            }
            if (section.Section.MaterialName is { } materialName
                && meshDefinition.Mesh?.FileRef.FindEntry(materialName) is { } meshMaterial)
            {
                return meshMaterial;
            }
            return emitter.Material;
        }

        public void Render(LevelEditorRenderContext context, Instance instance, Matrix4x4 componentTransform)
        {
            foreach (VfxEmitterState emitter in instance.Simulation.Emitters)
            {
                Matrix4x4 emitterTransform = emitter.Definition.AttachmentTransform * componentTransform;
                switch (emitter.Definition.RenderMode)
                {
                    case VfxEmitterRenderMode.Sprite:
                        gameShaderRenderer.TryRenderSprite(context, emitter, emitterTransform);
                        break;
                    case VfxEmitterRenderMode.Beam:
                    case VfxEmitterRenderMode.Trail:
                        gameShaderRenderer.TryRenderBeamTrail(context, emitter, emitterTransform);
                        break;
                    case VfxEmitterRenderMode.Mesh when meshEmitters.TryGetValue(emitter.Definition,
                        out VfxMeshRenderer.MeshEmitterResources resources):
                        bool previousCameraRelative = context.UseCameraRelativeNativeRendering;
                        context.UseCameraRelativeNativeRendering = true;
                        try
                        {
                            VfxMeshRenderer.RenderGameShader(context, emitter, resources, emitterTransform);
                        }
                        finally
                        {
                            context.UseCameraRelativeNativeRendering = previousCameraRelative;
                        }
                        break;
                }
            }
        }

        private static void TraceWarning(VfxEmitterDefinition emitter, string warning)
        {
            if (!string.IsNullOrWhiteSpace(warning))
            {
                Debug.WriteLine($"Level Editor VFX: {emitter.Name}: {warning}");
            }
        }

        public void Dispose()
        {
            gameShaderRenderer.Dispose();
            foreach (VfxMeshRenderer.MeshEmitterResources resources in meshEmitters.Values)
            {
                resources.Dispose();
            }
            meshEmitters.Clear();
        }
    }

    private readonly LevelEditorRenderContext context;
    private readonly Dictionary<ExportEntry, PreparedSystem> preparedSystems = [];
    private readonly Dictionary<string, (bool DisableDepthTest, bool UsesDynamicParameter,
        VfxBlendMode BlendMode, IEntry Texture)> materialFlags
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(bool DepthTest, bool DepthWrite), DepthStencilState> depthStates = [];
    private readonly object depthStateLock = new();

    public LevelVfxRenderer(LevelEditorRenderContext context)
    {
        this.context = context;
    }

    public Instance CreateInstance(IEntry particleSystemEntry)
    {
        ExportEntry particleSystem = context.ResolveExportCached(particleSystemEntry);
        if (particleSystem is null || particleSystem.ClassName != "ParticleSystem")
        {
            return null;
        }

        if (!preparedSystems.TryGetValue(particleSystem, out PreparedSystem prepared))
        {
            EnsureDepthStates();
            try
            {
                VfxPreviewDefinition definition = new ParticleSystemSourceAdapter()
                    .CreateDefinition(particleSystem, context.PackageCache);
                prepared = new PreparedSystem(definition);
                prepared.Prepare(context, this);
                preparedSystems.Add(particleSystem, prepared);
            }
            catch (Exception exception)
            {
                prepared?.Dispose();
                Debug.WriteLine($"Level Editor VFX: {particleSystem.InstancedFullPath}: {exception}");
                return null;
            }
        }

        return prepared.HasRenderableEmitter ? new Instance(prepared) : null;
    }

    public void Render(Instance instance, Matrix4x4 componentTransform)
    {
        if (instance is null)
        {
            return;
        }
        bool previousDepthFallback = context.UseVfxSceneDepthFallback;
        context.UseVfxSceneDepthFallback = true;
        try
        {
            instance.System.Render(context, instance, componentTransform);
        }
        finally
        {
            context.UseVfxSceneDepthFallback = previousDepthFallback;
        }
    }

    public DepthStencilState GetDepthState(bool depthTest, bool depthWrite)
    {
        EnsureDepthStates();
        return depthStates.GetValueOrDefault((depthTest, depthWrite));
    }

    private void EnsureDepthStates()
    {
        if (depthStates.Count == 4)
        {
            return;
        }
        lock (depthStateLock)
        {
            if (depthStates.Count == 4)
            {
                return;
            }
            foreach (bool depthTest in new[] { false, true })
            {
                foreach (bool depthWrite in new[] { false, true })
                {
                    depthStates[(depthTest, depthWrite)] = new DepthStencilState(context.Device,
                        new DepthStencilStateDescription
                        {
                            IsDepthEnabled = depthTest,
                            DepthComparison = Comparison.LessEqual,
                            DepthWriteMask = depthWrite ? DepthWriteMask.All : DepthWriteMask.Zero
                        });
                }
            }
        }
    }

    private void PopulateNativeMaterial(LevelEditorRenderContext renderContext, ExportEntry materialExport,
        VfxParticleMaterialDefinition material)
    {
        string cacheKey = $"{materialExport.FileRef.FilePath}|{materialExport.UIndex}";
        if (!materialFlags.TryGetValue(cacheKey, out var flags))
        {
            ExportEntry baseMaterial = ResolveBaseMaterial(renderContext, materialExport);
            PropertyCollection properties = baseMaterial?.GetProperties(packageCache: renderContext.PackageCache);
            string blendMode = properties?.GetProp<EnumProperty>("BlendMode")?.Value.Name;
            VfxBlendMode resolvedBlend = blendMode switch
            {
                "BLEND_Opaque" => VfxBlendMode.Opaque,
                "BLEND_Masked" => VfxBlendMode.Masked,
                "BLEND_Additive" => VfxBlendMode.Additive,
                "BLEND_Modulate" => VfxBlendMode.Modulate,
                "BLEND_ModulateAndAdd" => VfxBlendMode.ModulateAndAdd,
                "BLEND_SoftMasked" => VfxBlendMode.SoftMasked,
                "BLEND_AlphaComposite" => VfxBlendMode.AlphaComposite,
                _ => VfxBlendMode.Translucent
            };
            bool usesDynamicParameter = false;
            if (baseMaterial?.ClassName == "Material")
            {
                try
                {
                    usesDynamicParameter = ObjectBinary.From<Material>(baseMaterial)
                        .SM3MaterialResource.bUsesDynamicParameter;
                }
                catch
                {
                    // The native renderer tries both compatible factory variants if cooked metadata is incomplete.
                }
            }
            flags = (properties?.GetProp<BoolProperty>("bDisableDepthTest")?.Value == true,
                usesDynamicParameter, resolvedBlend, ResolveParticleTexture(renderContext, materialExport));
            materialFlags[cacheKey] = flags;
        }

        material.DisableDepthTest = flags.DisableDepthTest;
        material.UsesDynamicParameter = flags.UsesDynamicParameter;
        material.BlendMode = flags.BlendMode;
        material.BlendModeResolved = true;
        material.Texture = flags.Texture;
    }

    private static IEntry ResolveParticleTexture(LevelEditorRenderContext context, ExportEntry materialExport)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ExportEntry current = materialExport;
        while (current is not null && visited.Add($"{current.FileRef.FilePath}|{current.UIndex}"))
        {
            PropertyCollection properties = current.GetProperties(packageCache: context.PackageCache);
            if (properties.GetProp<ArrayProperty<StructProperty>>("TextureParameterValues") is { } textureParameters)
            {
                IEntry parameterTexture = SelectParticleTexture(textureParameters
                    .Select(parameter => parameter.GetProp<ObjectProperty>("ParameterValue")
                        ?.ResolveToEntry(current.FileRef))
                    .Where(IsSupportedParticleTexture), current);
                if (parameterTexture is not null)
                {
                    return parameterTexture;
                }
            }

            if (current.ClassName == "Material")
            {
                try
                {
                    Material material = ObjectBinary.From<Material>(current);
                    IEnumerable<IEntry> textures = material.SM3MaterialResource.UniformExpressionTextures
                        .Select(current.FileRef.GetEntry)
                        .Where(IsSupportedParticleTexture);
                    if (material.SM3MaterialResource.TextureDependencyLengthMap is { } dependencies)
                    {
                        textures = textures.Concat(dependencies.Keys.Select(current.FileRef.GetEntry)
                            .Where(IsSupportedParticleTexture));
                    }
                    return SelectParticleTexture(textures.Distinct(), current);
                }
                catch
                {
                    return null;
                }
            }

            IEntry parent = properties.GetProp<ObjectProperty>("Parent")?.ResolveToEntry(current.FileRef);
            current = context.ResolveExportCached(parent);
        }
        return null;
    }

    private static bool IsSupportedParticleTexture(IEntry texture)
        => texture?.ClassName is "Texture2D" or "TextureFlipBook";

    private static IEntry SelectParticleTexture(IEnumerable<IEntry> textures, ExportEntry material)
    {
        string materialName = material.ObjectName.Name.Replace("M_", string.Empty,
            StringComparison.OrdinalIgnoreCase);
        List<IEntry> candidates = [.. textures];
        return candidates
                   .Where(texture => !VfxPreviewRenderContext.IsAuxiliaryParticleTexture(
                       texture.InstancedFullPath, texture.ObjectName.Name))
                   .OrderByDescending(texture => TextureScore(texture, materialName))
                   .FirstOrDefault()
               ?? candidates.OrderByDescending(texture => TextureScore(texture, materialName)).FirstOrDefault();
    }

    private static int TextureScore(IEntry texture, string materialName)
    {
        string path = texture.InstancedFullPath;
        string textureName = texture.ObjectName.Name;
        int score = texture.ClassName == "TextureFlipBook" ? 20 : 0;
        if (path.Contains("Diffuse", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Albedo", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Opacity", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Emissive", StringComparison.OrdinalIgnoreCase)) score += 100;
        if (textureName.Contains(materialName, StringComparison.OrdinalIgnoreCase)
            || materialName.Contains(textureName, StringComparison.OrdinalIgnoreCase)) score += 50;
        return score;
    }

    private static ExportEntry ResolveBaseMaterial(LevelEditorRenderContext context, ExportEntry material)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ExportEntry current = material;
        while (current is not null && current.ClassName.Contains("MaterialInstance", StringComparison.Ordinal)
               && visited.Add($"{current.FileRef.FilePath}|{current.UIndex}"))
        {
            IEntry parent = current.GetProperty<ObjectProperty>("Parent")?.ResolveToEntry(current.FileRef);
            current = context.ResolveExportCached(parent);
        }
        return current;
    }

    public void Clear()
    {
        foreach (PreparedSystem prepared in preparedSystems.Values)
        {
            prepared.Dispose();
        }
        preparedSystems.Clear();
        materialFlags.Clear();
    }

    public void Dispose()
    {
        Clear();
        foreach (DepthStencilState depthState in depthStates.Values)
        {
            depthState.Dispose();
        }
        depthStates.Clear();
    }
}
