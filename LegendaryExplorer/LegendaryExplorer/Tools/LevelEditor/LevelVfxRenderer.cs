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
/// The Level Editor's particle effects. Particle definitions and their materials' cooked shaders are prepared once per ParticleSystem
/// (<see cref="PreparedSystem"/>), while each placed component keeps its own lightweight simulation (<see cref="Instance"/>).
/// Nothing here renders on its own: each Emitter's <see cref="ParticleSystemComponentProxy"/> draws through the Level Editor's passes,
/// opaque and masked emitters in <see cref="RenderPass.Base"/> and the rest from the context's sorted translucent queue
/// (<see cref="LevelEditorRenderContext.QueueTranslucentEffect"/>), every draw going through the context's game-shader path
/// (<see cref="VfxGameShaderRenderer.RenderNativeMaterial{TVertex}"/>).
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
        public ExportEntry Source { get; }
        public bool HasRenderableEmitter { get; private set; }
        /// <summary>Whether any emitter draws in the opaque (depth-writing) pass, and whether any blends</summary>
        public bool HasOpaqueEmitters { get; private set; }
        public bool HasTranslucentEmitters { get; private set; }
        /// <summary>Set if drawing it threw: it isn't drawn again (until the level is reloaded or the system is edited)</summary>
        public bool Failed { get; private set; }
        /// <summary>Released (the level was unloaded, or the system edited): instances of it must be made again</summary>
        public bool Disposed { get; private set; }
        /// <summary>The UIndexes, in <see cref="Source"/>'s package, of the ParticleSystem and everything under it (emitters, LODs, modules)</summary>
        public HashSet<int> SourceUIndexes { get; }

        public PreparedSystem(VfxPreviewDefinition definition, ExportEntry source)
        {
            Definition = definition;
            Source = source;
            SourceUIndexes = [source.UIndex, .. source.GetAllDescendants().Select(entry => entry.UIndex)];
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

                    bool depthWrite = false;
                    switch (emitter.RenderMode)
                    {
                        case VfxEmitterRenderMode.Sprite when materialExport is not null:
                            loaded = gameShaderRenderer.TryLoadSprite(context, emitter, materialExport, out string spriteWarning);
                            TraceWarning(emitter, spriteWarning);
                            loaded = loaded && gameShaderRenderer.TryGetSpriteDepthWrite(emitter, out depthWrite);
                            break;
                        case VfxEmitterRenderMode.Beam when materialExport is not null:
                        case VfxEmitterRenderMode.Trail when materialExport is not null:
                            loaded = gameShaderRenderer.TryLoadBeamTrail(context, emitter, materialExport, out string beamWarning);
                            TraceWarning(emitter, beamWarning);
                            loaded = loaded && gameShaderRenderer.TryGetBeamTrailDepthWrite(emitter, out depthWrite);
                            break;
                        case VfxEmitterRenderMode.Mesh:
                            loaded = PrepareMeshEmitter(context, owner, emitter);
                            if (loaded)
                            {
                                VfxMeshRenderer.MeshEmitterResources resources = meshEmitters[emitter];
                                HasOpaqueEmitters |= resources.HasOpaqueGameShaderSections;
                                HasTranslucentEmitters |= resources.HasBlendedGameShaderSections;
                            }
                            break;
                    }
                    if (loaded && emitter.RenderMode is not VfxEmitterRenderMode.Mesh)
                    {
                        HasOpaqueEmitters |= depthWrite;
                        HasTranslucentEmitters |= !depthWrite;
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

                // The Level Editor only draws mesh emitters with their game shaders: release the standard preview's copy of the mesh
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

        /// <summary>
        /// Draws the emitters that write depth (opaque and masked sprites, ribbons and mesh sections), in <see cref="RenderPass.Base"/>.
        /// Mesh sections also draw their hit proxies, so clicking one selects the Emitter
        /// </summary>
        public void RenderOpaque(LevelEditorRenderContext context, Instance instance, Matrix4x4 componentTransform)
            => Render(context, instance, componentTransform, opaque: true);

        /// <summary>Draws the blended emitters, from the translucent queue (back to front among effects), in authored emitter order</summary>
        public void RenderTranslucent(LevelEditorRenderContext context, Instance instance, Matrix4x4 componentTransform)
            => Render(context, instance, componentTransform, opaque: false);

        private void Render(LevelEditorRenderContext context, Instance instance, Matrix4x4 componentTransform, bool opaque)
        {
            if (Failed || Disposed || (opaque ? !HasOpaqueEmitters : !HasTranslucentEmitters)) return;
            try
            {
                foreach (VfxEmitterState emitter in instance.Simulation.Emitters)
                {
                    if (emitter.Particles.Count == 0 && emitter.Definition.RenderMode is not VfxEmitterRenderMode.Beam) continue;
                    Matrix4x4 emitterTransform = emitter.Definition.AttachmentTransform * componentTransform;
                    switch (emitter.Definition.RenderMode)
                    {
                        case VfxEmitterRenderMode.Sprite
                            when gameShaderRenderer.TryGetSpriteDepthWrite(emitter.Definition, out bool depthWrite) && depthWrite == opaque:
                            gameShaderRenderer.TryRenderSprite(context, emitter, emitterTransform);
                            break;
                        case VfxEmitterRenderMode.Beam or VfxEmitterRenderMode.Trail
                            when gameShaderRenderer.TryGetBeamTrailDepthWrite(emitter.Definition, out bool depthWrite) && depthWrite == opaque:
                            gameShaderRenderer.TryRenderBeamTrail(context, emitter, emitterTransform);
                            break;
                        case VfxEmitterRenderMode.Mesh when meshEmitters.TryGetValue(emitter.Definition,
                            out VfxMeshRenderer.MeshEmitterResources resources):
                            VfxMeshRenderer.RenderGameShader(context, emitter, resources, emitterTransform, opaquePass: opaque,
                                writeHitProxy: opaque);
                            break;
                    }
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // One bad material mustn't take the viewport down (an exception from a render pass replaces the scene with its message)
                Failed = true;
                Debug.WriteLine($"Level Editor VFX: {Source.InstancedFullPath} stopped drawing: {exception}");
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
            Disposed = true;
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
    private readonly HashSet<ExportEntry> unrenderableSystems = [];
    private readonly Dictionary<string, (bool DisableDepthTest, bool UsesDynamicParameter,
        VfxBlendMode BlendMode, IEntry Texture)> materialFlags
        = new(StringComparer.OrdinalIgnoreCase);

    // Preparing a system (converting it, loading its materials' shaders and textures) is spread across frames, so that a level
    // full of emitters doesn't stall the viewport the first time they come into view
    private const double PreparationBudgetMilliseconds = 12;
    private uint preparationFrame = uint.MaxValue;
    private long preparationTicks;

    public LevelVfxRenderer(LevelEditorRenderContext context)
    {
        this.context = context;
    }

    /// <summary>
    /// Makes a simulation of a ParticleSystem, preparing the system first if this is its first instance. False if that has to wait for a later
    /// frame (this frame's preparation budget is spent); otherwise true, with a null <paramref name="instance"/> if it has nothing that can be drawn
    /// </summary>
    public bool TryCreateInstance(IEntry particleSystemEntry, out Instance instance)
    {
        instance = null;
        ExportEntry particleSystem = context.ResolveExportCached(particleSystemEntry);
        if (particleSystem is null || particleSystem.ClassName != "ParticleSystem" || unrenderableSystems.Contains(particleSystem))
        {
            return true;
        }

        if (!preparedSystems.TryGetValue(particleSystem, out PreparedSystem prepared))
        {
            if (preparationFrame != context.NumFrames)
            {
                preparationFrame = context.NumFrames;
                preparationTicks = 0;
            }
            else if (Stopwatch.GetElapsedTime(0, preparationTicks).TotalMilliseconds >= PreparationBudgetMilliseconds)
            {
                return false;
            }
            long start = Stopwatch.GetTimestamp();
            try
            {
                VfxPreviewDefinition definition = new ParticleSystemSourceAdapter()
                    .CreateDefinition(particleSystem, context.PackageCache);
                prepared = new PreparedSystem(definition, particleSystem);
                prepared.Prepare(context, this);
            }
            catch (Exception exception)
            {
                prepared?.Dispose();
                prepared = null;
                Debug.WriteLine($"Level Editor VFX: {particleSystem.InstancedFullPath}: {exception}");
            }
            finally
            {
                preparationTicks += Stopwatch.GetTimestamp() - start;
            }
            if (prepared is null || !prepared.HasRenderableEmitter)
            {
                prepared?.Dispose();
                unrenderableSystems.Add(particleSystem);
                return true;
            }
            preparedSystems.Add(particleSystem, prepared);
        }

        instance = prepared.Failed ? null : new Instance(prepared);
        return true;
    }

    /// <summary>
    /// Forgets the prepared ParticleSystems that any of <paramref name="uIndexes"/> in <paramref name="package"/> belong to (edited in the package
    /// editor), so their next instances are prepared from the new data. True if any was
    /// </summary>
    public bool Invalidate(IMEPackage package, HashSet<int> uIndexes)
    {
        bool any = false;
        foreach (PreparedSystem prepared in preparedSystems.Values.ToList())
        {
            if (prepared.Source.FileRef == package && prepared.SourceUIndexes.Overlaps(uIndexes))
            {
                preparedSystems.Remove(prepared.Source);
                prepared.Dispose();
                any = true;
            }
        }
        if (unrenderableSystems.RemoveWhere(system => system.FileRef == package && uIndexes.Contains(system.UIndex)) > 0) any = true;
        if (any) materialFlags.Clear();
        return any;
    }

    /// <summary>Whether a ParticleSystem's source exports include any of <paramref name="uIndexes"/>: its prepared copy is out of date</summary>
    public bool IsAffectedBy(ExportEntry particleSystem, HashSet<int> uIndexes)
        => particleSystem is not null && (uIndexes.Contains(particleSystem.UIndex)
            || preparedSystems.TryGetValue(particleSystem, out PreparedSystem prepared) && prepared.SourceUIndexes.Overlaps(uIndexes));

    /// <summary>The context's depth states (reversed-Z): see <see cref="MeshRenderContext.GetEffectsDepthState"/></summary>
    public DepthStencilState GetDepthState(bool depthTest, bool depthWrite) => context.GetEffectsDepthState(depthTest, depthWrite);

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

    /// <summary>Forgets every prepared system (the level was unloaded). Placed components prepare theirs again as they're drawn</summary>
    public void Clear()
    {
        foreach (PreparedSystem prepared in preparedSystems.Values)
        {
            prepared.Dispose();
        }
        preparedSystems.Clear();
        unrenderableSystems.Clear();
        materialFlags.Clear();
    }

    public void Dispose()
    {
        Clear();
        context.DisposeEffectsResources();
    }
}
