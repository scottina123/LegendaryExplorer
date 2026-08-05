using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Classes;
using SharpDX.Direct3D11;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Color = System.Windows.Media.Color;

namespace LegendaryExplorer.Tools.AssetDatabase.VFXPreview;

public sealed class VfxPreviewRenderContext : MeshRenderContext
{
    private readonly VfxBillboardRenderer billboardRenderer = new();
    private readonly VfxMeshRenderer meshRenderer = new();
    private readonly VfxGameShaderRenderer gameShaderRenderer = new();
    private readonly BatchedPrimitives primitives = new();
    private readonly Dictionary<VfxEmitterDefinition, PreviewTextureCache.TextureEntry> textures = [];
    private readonly Dictionary<VfxEmitterDefinition, VfxMeshRenderer.MeshEmitterResources> meshEmitters = [];
    private readonly Dictionary<VfxBlendMode, BlendState> blendStates = [];
    private readonly Dictionary<(bool DepthTest, bool DepthWrite), DepthStencilState> depthStates = [];
    private readonly Dictionary<string, bool> luminanceOpacityCache = new(StringComparer.OrdinalIgnoreCase);
    private Func<ImportEntry, IEnumerable<VfxImportFallback>> importFallbackResolver;
    private VfxPreviewBackground background = VfxPreviewBackground.NeutralGray;
    private VfxPreviewShadingMode shadingMode = VfxPreviewShadingMode.Unlit;
    private bool useGameShader = true;
    private string standardRuntimeWarning;
    private bool isDarkMode;
    private bool autoFramePending;
    private int autoFrameElapsed;
    private bool autoFrameHasPeak;
    private Vector3 autoFramePeakMinimum;
    private Vector3 autoFramePeakMaximum;

    private const float DefaultFocusDepth = 250;
    private const float MinimumFocusRadius = 10;
    private const float MaximumFocusDepth = 100000;
    private const float FocusPadding = 1.15f;
    private const int AutoFrameSettleFrames = 30;
    private const int AutoFrameMaxFrames = 300;

    public VfxSimulation Simulation { get; } = new();
    public bool ShowAxis { get; set; } = true;
    public bool ShowGrid { get; set; } = true;
    public bool ShowGroundPlane { get; set; }
    public bool ShowBoundingBox { get; set; }
    public bool ShowOrigin { get; set; } = true;
    public string RuntimeWarning { get; private set; }

    /// <summary>
    /// Uses the experimental compiled-material preview shared with Meshplorer and Morph Editor.
    /// This is the default VFX preview path; incompatible emitters fall back to the standard shader.
    /// </summary>
    public bool UseGameShader
    {
        get => useGameShader;
        set
        {
            if (useGameShader == value)
            {
                return;
            }
            useGameShader = value;
            if (!IsReady || Simulation.Definition is null)
            {
                return;
            }
            if (value)
            {
                RefreshGameShaderResources();
            }
            else
            {
                DisposeGameShaderResources();
                RuntimeWarning = standardRuntimeWarning;
            }
        }
    }

    public VfxPreviewBackground Background
    {
        get => background;
        set
        {
            background = value;
            UpdateBackgroundColor();
        }
    }

    public VfxPreviewShadingMode ShadingMode
    {
        get => shadingMode;
        set
        {
            shadingMode = value;
            Wireframe = value == VfxPreviewShadingMode.Wireframe;
            if (value == VfxPreviewShadingMode.Unlit)
            {
                RenderFlags |= ShaderFlags.Unlit;
            }
            else
            {
                RenderFlags &= ~ShaderFlags.Unlit;
            }
        }
    }

    public VfxPreviewRenderContext()
    {
        Camera.FirstPerson = false;
        Camera.Yaw = MathF.PI;
        Camera.Pitch = -0.15f;
        Camera.FocusDepth = DefaultFocusDepth;
        Background = VfxPreviewBackground.NeutralGray;
        RenderFlags |= ShaderFlags.Unlit | ShaderFlags.PreserveTextureAlpha;
        SceneLights.Add(new SceneLight(new Vector3(-200, -200, 300), 1200, Vector3.One, 1.25f, false, Vector3.Zero, 0, 0));
        SceneLights.Add(new SceneLight(new Vector3(250, 100, 100), 900, new Vector3(0.55f, 0.65f, 1), 0.6f, false, Vector3.Zero, 0, 0));
        RenderScene += RenderPreview;
    }

    public void ApplyTheme(bool darkMode)
    {
        isDarkMode = darkMode;
        UpdateBackgroundColor();
    }

    private void UpdateBackgroundColor()
    {
        BackgroundColor = background switch
        {
            VfxPreviewBackground.Transparent => Color.FromArgb(0, 0, 0, 0),
            VfxPreviewBackground.Black => Color.FromRgb(0, 0, 0),
            _ when isDarkMode => Color.FromRgb(0x1E, 0x1E, 0x1E),
            _ => Color.FromRgb(0x66, 0x66, 0x66)
        };
    }

    public override void CreateResources()
    {
        base.CreateResources();
        billboardRenderer.CreateResources(this);
        meshRenderer.CreateResources(this);
        CreateBlendStates();
        CreateDepthStates();
        RefreshTextures();
    }

    public override void Update(float timestep)
    {
        base.Update(timestep);
        Simulation.Tick(timestep);
        TryAutoFrame();
    }

    /// <summary>
    /// Effects often spawn nothing on the first frame, so the initial Focus() may have no live bounds to use.
    /// The camera settles once geometry appears, while the preview transform keeps tracking the peak live bounds
    /// so delayed or expanding particles remain inside the grid without overriding later camera input.
    /// </summary>
    private void TryAutoFrame()
    {
        if (!TryGetPreviewBounds(false, Matrix4x4.Identity, out Vector3 minimum, out Vector3 maximum))
        {
            // Give the effect a reasonable window to start emitting (delays/warmup can hold off spawning).
            if (autoFramePending && ++autoFrameElapsed > AutoFrameMaxFrames)
            {
                autoFramePending = false;
            }
            return;
        }

        bool frameCamera = autoFramePending || !autoFrameHasPeak;
        AccumulateAutoFrameBounds(ref minimum, ref maximum);
        FitPreviewToGrid(minimum, maximum, frameCamera);
        if (autoFramePending && ++autoFrameElapsed > AutoFrameSettleFrames)
        {
            autoFramePending = false;
        }
    }

    /// <summary>
    /// Accumulates the largest extent seen so far during the settle window so the camera does not oscillate as
    /// particles spawn and die, and settles on a framing that contains the effect at its widest.
    /// </summary>
    private void AccumulateAutoFrameBounds(ref Vector3 minimum, ref Vector3 maximum)
    {
        if (autoFrameHasPeak)
        {
            autoFramePeakMinimum = Vector3.Min(autoFramePeakMinimum, minimum);
            autoFramePeakMaximum = Vector3.Max(autoFramePeakMaximum, maximum);
        }
        else
        {
            autoFramePeakMinimum = minimum;
            autoFramePeakMaximum = maximum;
            autoFrameHasPeak = true;
        }
        minimum = autoFramePeakMinimum;
        maximum = autoFramePeakMaximum;
    }

    public override bool IsActivelyUpdating() => Simulation.IsPlaying || base.IsActivelyUpdating();

    public void Load(VfxPreviewDefinition definition, Func<ImportEntry, IEnumerable<VfxImportFallback>> fallbackResolver = null)
    {
        ResetPreviewCamera();
        importFallbackResolver = fallbackResolver;
        Simulation.Load(definition);
        RuntimeWarning = definition.Warnings.Count == 0 ? null : string.Join(Environment.NewLine, definition.Warnings.Distinct());
        ErrorText = null;
        RefreshTextures();
        Focus();
    }

    public void Unload()
    {
        Simulation.Clear();
        autoFramePending = false;
        textures.Clear();
        gameShaderRenderer.Clear();
        DisposeMeshEmitters();
        RuntimeWarning = null;
        standardRuntimeWarning = null;
        importFallbackResolver = null;
        luminanceOpacityCache.Clear();
        TextureCache?.ExpungeStaleCacheItems();
        PackageCache?.ReleasePackages();
    }

    public void Focus()
    {
        if (TryGetPreviewBounds(false, Matrix4x4.Identity, out Vector3 minimum, out Vector3 maximum))
        {
            autoFramePeakMinimum = minimum;
            autoFramePeakMaximum = maximum;
            autoFrameHasPeak = true;
            autoFrameElapsed = 0;
            autoFramePending = true;
            FitPreviewToGrid(minimum, maximum, true);
            return;
        }

        // Use the authored bounds for the initial camera when available. Live bounds deliberately start a
        // separate peak below, so an inaccurate authored box cannot permanently make the effect appear tiny.
        if (TryGetPreviewBounds(true, out minimum, out maximum))
        {
            FrameBounds(minimum, maximum);
        }
        else
        {
            Camera.Position = Vector3.Zero;
            Camera.FocusDepth = DefaultFocusDepth;
        }
        autoFramePending = true;
        autoFrameElapsed = 0;
        autoFrameHasPeak = false;
    }

    private void FitPreviewToGrid(Vector3 rawMinimum, Vector3 rawMaximum, bool frameCamera)
    {
        if (Simulation.Definition is null)
        {
            return;
        }

        var rawBounds = new VfxBounds(rawMinimum, rawMaximum);
        Simulation.Definition.SystemTransform = VfxPreviewDefinition.CreateGridFittingTransform(rawBounds);
        if (frameCamera)
        {
            VfxBounds fittedBounds = VfxBoundsMath.Transform(rawBounds, Simulation.Definition.SystemTransform);
            FrameBounds(fittedBounds.Minimum, fittedBounds.Maximum);
        }
    }

    /// <summary>
    /// Positions the orbit camera so the given world-space bounds fill the viewport without overflowing it,
    /// taking the camera's vertical FOV and viewport aspect into account.
    /// </summary>
    private void FrameBounds(Vector3 minimum, Vector3 maximum)
    {
        Vector3 center = (minimum + maximum) * 0.5f;
        float radius = Math.Max((maximum - minimum).Length() * 0.5f, MinimumFocusRadius);

        // Fit against whichever of the vertical/horizontal FOV is tighter so wide effects stay inside the viewport.
        float verticalFov = Math.Clamp(Camera.FOV, 0.01f, MathF.PI - 0.01f);
        float horizontalFov = 2f * MathF.Atan(MathF.Tan(verticalFov * 0.5f) * Math.Max(Camera.aspect, 0.0001f));
        float limitingFov = Math.Min(verticalFov, horizontalFov);
        float distance = radius / MathF.Tan(limitingFov * 0.5f);

        Camera.Position = center;
        Camera.FocusDepth = Math.Clamp(distance * FocusPadding, MinimumFocusRadius, MaximumFocusDepth);
    }

    public void Restart()
    {
        Simulation.Restart();
        ResetPreviewCamera();
        Focus();
    }

    private void ResetPreviewCamera()
    {
        Camera.FirstPerson = false;
        Camera.IsOrthographic = false;
        Camera.Position = Vector3.Zero;
        Camera.Roll = 0;
        Camera.Yaw = MathF.PI;
        Camera.Pitch = -0.15f;
        Camera.FocusDepth = DefaultFocusDepth;
    }

    private void RefreshTextures()
    {
        textures.Clear();
        luminanceOpacityCache.Clear();
        gameShaderRenderer.Clear();
        DisposeMeshEmitters();
        if (!IsReady || Simulation.Definition is null)
        {
            return;
        }

        foreach (VfxEmitterDefinition emitter in Simulation.Definition.Emitters)
        {
            switch (emitter.RenderMode)
            {
                case VfxEmitterRenderMode.Sprite:
                    break;
                case VfxEmitterRenderMode.Mesh:
                    LoadMeshEmitter(emitter);
                    continue;
                default:
                    RuntimeWarning = AppendWarning(RuntimeWarning, $"{emitter.Name}: {emitter.RenderMode} emitters are not rendered yet.");
                    continue;
            }
            try
            {
                ExportEntry materialExport = ResolvePreviewExport(emitter.Material);
                if (materialExport is null)
                {
                    string materialName = emitter.Material?.InstancedFullPath;
                    RuntimeWarning = AppendWarning(RuntimeWarning, materialName is null
                        ? $"{emitter.Name}: no material is assigned, so no sprites can be drawn."
                        : $"{emitter.Name}: material {materialName} could not be resolved, so no sprites can be drawn.");
                    continue;
                }

                VfxParticleMaterialDefinition particleMaterial = emitter.ParticleMaterial;
                PopulateMaterialProperties(materialExport, particleMaterial);
                IEntry textureEntry = ResolveParticleTexture(materialExport);
                particleMaterial.Texture = textureEntry;
                particleMaterial.OpacitySource = ResolveOpacitySource(materialExport, textureEntry, particleMaterial.BlendMode);
                ApplyUnresolvedBlendModeFallback(particleMaterial);
                PreviewTextureCache.TextureEntry texture = TextureCache.LoadTexture(textureEntry, PackageCache);
                if (texture is not null)
                {
                    textures[emitter] = texture;
                    particleMaterial.IsSupported = true;
                }
                else
                {
                    particleMaterial.Warning = "No supported particle texture could be resolved.";
                    RuntimeWarning = AppendWarning(RuntimeWarning, $"{emitter.Name}: {particleMaterial.Warning}");
                }
            }
            catch (Exception exception)
            {
                RuntimeWarning = AppendWarning(RuntimeWarning, $"{emitter.Name}: material unsupported ({exception.Message})");
            }
        }

        standardRuntimeWarning = RuntimeWarning;
        if (useGameShader)
        {
            RefreshGameShaderResources();
        }
    }

    private void RefreshGameShaderResources()
    {
        DisposeGameShaderResources();
        RuntimeWarning = standardRuntimeWarning;
        if (!useGameShader || !IsReady || Simulation.Definition is null)
        {
            return;
        }

        foreach (VfxEmitterDefinition emitter in Simulation.Definition.Emitters)
        {
            string warning = null;
            try
            {
                switch (emitter.RenderMode)
                {
                    case VfxEmitterRenderMode.Sprite:
                        // ParticleModuleParameterDynamic is evaluated by the simulator, but Meshplorer's material
                        // renderer supplies FLocalVertexFactory streams. A material compiled with dynamic particle
                        // inputs must use the standard VFX renderer until the native particle VF is available;
                        // sending it through the local VF produces the solid white/yellow cards seen in BioticBadass.
                        if (emitter.ParticleMaterial.UsesDynamicParameter)
                        {
                            break;
                        }
                        ExportEntry materialExport = ResolvePreviewExport(emitter.Material);
                        if (materialExport is not null)
                        {
                            gameShaderRenderer.TryLoadSprite(this, emitter, materialExport, out warning);
                        }
                        break;
                    case VfxEmitterRenderMode.Mesh:
                        if (meshEmitters.TryGetValue(emitter, out VfxMeshRenderer.MeshEmitterResources resources))
                        {
                            VfxMeshRenderer.TryLoadGameShaderPreview(this, resources, out warning);
                        }
                        break;
                }
            }
            catch (Exception exception)
            {
                warning = $"The in-game shader could not be loaded ({exception.Message}).";
            }

            if (!string.IsNullOrWhiteSpace(warning))
            {
                RuntimeWarning = AppendWarning(RuntimeWarning,
                    $"{emitter.Name}: {warning} The standard VFX shader will be used for this emitter.");
            }
        }
    }

    private void DisposeGameShaderResources()
    {
        gameShaderRenderer.Clear();
        foreach (VfxMeshRenderer.MeshEmitterResources resources in meshEmitters.Values)
        {
            resources.GameShaderPreview?.Dispose();
            resources.GameShaderPreview = null;
        }
    }

    /// <summary>
    /// Loads the StaticMesh of a ParticleModuleTypeDataMesh emitter and resolves the material for every mesh section.
    /// Section materials come from the mesh itself, but can be overridden per section by ParticleModuleMeshMaterial,
    /// or wholesale by the required/emitter material when bOverrideMaterial is set.
    /// </summary>
    private void LoadMeshEmitter(VfxEmitterDefinition emitter)
    {
        VfxMeshEmitterDefinition meshDefinition = emitter.MeshEmitter;
        if (meshDefinition?.Mesh is null)
        {
            RuntimeWarning = AppendWarning(RuntimeWarning, $"{emitter.Name}: the mesh type data module has no mesh assigned, so nothing can be drawn.");
            return;
        }

        try
        {
            VfxMeshRenderer.MeshEmitterResources resources = VfxMeshRenderer.LoadMesh(this, meshDefinition);
            if (resources is null)
            {
                RuntimeWarning = AppendWarning(RuntimeWarning, $"{emitter.Name}: mesh {meshDefinition.Mesh.InstancedFullPath} could not be loaded.");
                return;
            }

            for (int index = 0; index < resources.Sections.Count; index++)
            {
                VfxMeshRenderer.MeshSection section = resources.Sections[index];
                IEntry materialEntry = ResolveMeshSectionMaterial(emitter, meshDefinition, section, index);
                ExportEntry materialExport = ResolvePreviewExport(materialEntry);
                if (materialExport is null)
                {
                    RuntimeWarning = AppendWarning(RuntimeWarning, $"{emitter.Name}: no material could be resolved for mesh section {index}.");
                    continue;
                }

                section.GameShaderMaterial = materialExport;

                PopulateMaterialProperties(materialExport, section.Material);
                IEntry textureEntry = ResolveParticleTexture(materialExport);
                section.Material.Texture = textureEntry;
                section.Material.OpacitySource = ResolveOpacitySource(materialExport, textureEntry, section.Material.BlendMode);
                ApplyUnresolvedBlendModeFallback(section.Material);
                section.Texture = TextureCache.LoadTexture(textureEntry, PackageCache);
                if (section.Texture is null)
                {
                    section.Material.Warning = "No supported texture could be resolved.";
                    RuntimeWarning = AppendWarning(RuntimeWarning, $"{emitter.Name}: mesh section {index}: {section.Material.Warning}");
                    continue;
                }

                section.Material.IsSupported = true;
                section.IsOpaque = section.Material.BlendMode is VfxBlendMode.Opaque or VfxBlendMode.Masked;
                blendStates.TryGetValue(section.Material.BlendMode, out BlendState blendState);
                section.BlendState = blendState;
                depthStates.TryGetValue((!section.Material.DisableDepthTest, section.IsOpaque), out DepthStencilState depthState);
                section.DepthState = depthState;
            }

            meshEmitters[emitter] = resources;
        }
        catch (Exception exception)
        {
            RuntimeWarning = AppendWarning(RuntimeWarning, $"{emitter.Name}: mesh emitter unsupported ({exception.Message})");
        }
    }

    private IEntry ResolveMeshSectionMaterial(VfxEmitterDefinition emitter, VfxMeshEmitterDefinition meshDefinition, VfxMeshRenderer.MeshSection section, int sectionIndex)
    {
        if (sectionIndex < meshDefinition.SectionMaterialOverrides.Count && meshDefinition.SectionMaterialOverrides[sectionIndex] is { } sectionOverride)
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

    private void DisposeMeshEmitters()
    {
        foreach (VfxMeshRenderer.MeshEmitterResources resources in meshEmitters.Values)
        {
            resources.Dispose();
        }
        meshEmitters.Clear();
    }

    private void PopulateMaterialProperties(ExportEntry materialExport, VfxParticleMaterialDefinition material)
    {
        ExportEntry baseMaterial = ResolveBaseMaterial(materialExport);
        PropertyCollection properties = baseMaterial?.GetProperties(packageCache: PackageCache);
        string blendMode = properties?.GetProp<EnumProperty>("BlendMode")?.Value.Name;
        material.BlendModeResolved = blendMode is not null;
        material.BlendMode = blendMode switch
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
        material.IsUnlit = properties?.GetProp<EnumProperty>("LightingModel")?.Value.Name == "MLM_Unlit";
        material.TwoSided = properties?.GetProp<BoolProperty>("TwoSided")?.Value == true;
        material.DisableDepthTest = properties?.GetProp<BoolProperty>("bDisableDepthTest")?.Value == true;
        material.OpacityMaskClipValue = properties?.GetProp<FloatProperty>("OpacityMaskClipValue")?.Value ?? 0.333f;
        material.UsesDynamicParameter = baseMaterial?.ClassName == "Material"
            && ObjectBinary.From<Material>(baseMaterial).SM3MaterialResource.bUsesDynamicParameter;
        if (properties?.GetProp<ArrayProperty<ObjectProperty>>("Expressions") is { } expressions)
        {
            foreach (ObjectProperty expressionReference in expressions)
            {
                if (expressionReference.ResolveToEntry(baseMaterial.FileRef) is ExportEntry expression
                    && expression.ClassName == "MaterialExpressionVectorParameter"
                    && expression.GetProperty<NameProperty>("ParameterName")?.Value.Name == "Color"
                    && expression.GetProperty<StructProperty>("DefaultValue") is { } color)
                {
                    material.EmissiveTint = new Vector4(
                        color.GetProp<FloatProperty>("R")?.Value ?? 1,
                        color.GetProp<FloatProperty>("G")?.Value ?? 1,
                        color.GetProp<FloatProperty>("B")?.Value ?? 1,
                        color.GetProp<FloatProperty>("A")?.Value ?? 1);
                }
            }
        }
        material.EmissiveTint = ResolveMaterialInstanceVectorOverride(materialExport, "Color", material.EmissiveTint);
        material.EmissiveTint = NormalizeEmissiveTint(material.EmissiveTint);
    }

    /// <summary>
    /// The preview multiplies the material's Color parameter into every sprite. In game that parameter is usually
    /// driven at runtime, so an authored black default would only ever blank out the effect (most visibly on additive
    /// flame cards). Fall back to an untinted card in that case.
    /// </summary>
    private static Vector4 NormalizeEmissiveTint(Vector4 tint)
    {
        const float visibleTintThreshold = 0.004f;
        return tint.X <= visibleTintThreshold && tint.Y <= visibleTintThreshold && tint.Z <= visibleTintThreshold
            ? new Vector4(1, 1, 1, tint.W)
            : tint;
    }

    private Vector4 ResolveMaterialInstanceVectorOverride(ExportEntry materialExport, string parameterName, Vector4 value)
    {
        var visited = new HashSet<string>();
        ExportEntry current = materialExport;
        while (current is not null && visited.Add($"{current.FileRef.FilePath}:{current.UIndex}"))
        {
            PropertyCollection properties = current.GetProperties(packageCache: PackageCache);
            if (properties.GetProp<ArrayProperty<StructProperty>>("VectorParameterValues") is { } vectorParameters)
            {
                foreach (StructProperty parameter in vectorParameters)
                {
                    if (parameter.GetProp<NameProperty>("ParameterName")?.Value.Name == parameterName
                        && parameter.GetProp<StructProperty>("ParameterValue") is { } parameterValue)
                    {
                        return new Vector4(
                            parameterValue.GetProp<FloatProperty>("R")?.Value ?? value.X,
                            parameterValue.GetProp<FloatProperty>("G")?.Value ?? value.Y,
                            parameterValue.GetProp<FloatProperty>("B")?.Value ?? value.Z,
                            parameterValue.GetProp<FloatProperty>("A")?.Value ?? value.W);
                    }
                }
            }

            current = ResolveMaterialParent(current, properties);
        }
        return value;
    }

    private IEntry ResolveParticleTexture(ExportEntry materialExport)
    {
        var visited = new HashSet<string>();
        ExportEntry current = materialExport;
        while (current is not null && visited.Add($"{current.FileRef.FilePath}:{current.UIndex}"))
        {
            PropertyCollection properties = current.GetProperties(packageCache: PackageCache);
            if (properties.GetProp<ArrayProperty<StructProperty>>("TextureParameterValues") is { } textureParameters)
            {
                var candidates = new List<IEntry>();
                foreach (StructProperty parameter in textureParameters)
                {
                    IEntry texture = parameter.GetProp<ObjectProperty>("ParameterValue")?.ResolveToEntry(current.FileRef);
                    if (texture?.ClassName is "Texture2D" or "TextureFlipBook")
                    {
                        candidates.Add(texture);
                    }
                }
                if (candidates.Count > 0)
                {
                    return SelectParticleTexture(candidates, current);
                }
            }

            if (current.ClassName == "Material")
            {
                Material binary = ObjectBinary.From<Material>(current);
                return SelectParticleTexture(binary.SM3MaterialResource.UniformExpressionTextures
                    .Select(current.FileRef.GetEntry)
                    .Where(texture => texture?.ClassName is "Texture2D" or "TextureFlipBook"), current);
            }

            current = ResolveMaterialParent(current, properties);
        }
        return null;
    }

    private static IEntry SelectParticleTexture(IEnumerable<IEntry> textures, ExportEntry material)
    {
        string materialName = material.ObjectName.Name.Replace("M_", string.Empty, StringComparison.OrdinalIgnoreCase);
        List<IEntry> candidates = textures.ToList();
        IEntry selected = candidates
            .Where(texture => !IsAuxiliaryParticleTexture(texture.InstancedFullPath, texture.ObjectName.Name))
            .OrderByDescending(texture => TextureScore(texture, materialName))
            .FirstOrDefault();
        return selected ?? candidates
            .OrderByDescending(texture => TextureScore(texture, materialName))
            .FirstOrDefault();
    }

    private static int TextureScore(IEntry texture, string materialName)
    {
        string path = texture.InstancedFullPath;
        string textureName = texture.ObjectName.Name;
        int score = texture.ClassName == "TextureFlipBook" ? 20 : 0;
        if (IsAuxiliaryParticleTexture(path, textureName)) score -= 1000;
        if (path.Contains("Diffuse", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Albedo", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Opacity", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Emissive", StringComparison.OrdinalIgnoreCase)) score += 100;
        if (textureName.Contains(materialName, StringComparison.OrdinalIgnoreCase)
            || materialName.Contains(textureName, StringComparison.OrdinalIgnoreCase)) score += 50;
        return score;
    }

    private VfxOpacitySource ResolveOpacitySource(ExportEntry materialExport, IEntry texture, VfxBlendMode blendMode)
    {
        if (blendMode == VfxBlendMode.Opaque)
        {
            return VfxOpacitySource.One;
        }

        VfxOpacitySource opacitySource = VfxOpacitySource.TextureAlpha;
        ExportEntry baseMaterial = ResolveBaseMaterial(materialExport);
        StructProperty opacity = baseMaterial?.GetProperty<StructProperty>(blendMode == VfxBlendMode.Masked ? "OpacityMask" : "Opacity");
        if (opacity is not null && HasExpression(opacity))
        {
            if (opacity.GetProp<IntProperty>("MaskR")?.Value != 0) opacitySource = VfxOpacitySource.TextureRed;
            else if (opacity.GetProp<IntProperty>("MaskG")?.Value != 0) opacitySource = VfxOpacitySource.TextureGreen;
            else if (opacity.GetProp<IntProperty>("MaskB")?.Value != 0) opacitySource = VfxOpacitySource.TextureBlue;
            else if (opacity.GetProp<IntProperty>("MaskA")?.Value != 0) opacitySource = VfxOpacitySource.TextureAlpha;
        }

        if (opacitySource != VfxOpacitySource.TextureAlpha
            || blendMode is not (VfxBlendMode.Translucent or VfxBlendMode.Additive or VfxBlendMode.SoftMasked))
        {
            return opacitySource;
        }

        bool hasAlphaChannel = TextureHasAlphaChannel(texture);
        bool alphaNeedsLuminance = hasAlphaChannel && TextureAlphaNeedsLuminance(texture);
        return ApplyTextureOpacityFallback(opacitySource, blendMode, hasAlphaChannel, alphaNeedsLuminance);
    }

    public static VfxOpacitySource ApplyTextureOpacityFallback(
        VfxOpacitySource authoredSource,
        VfxBlendMode blendMode,
        bool hasAlphaChannel,
        bool alphaNeedsLuminance)
    {
        return blendMode is VfxBlendMode.Translucent or VfxBlendMode.Additive or VfxBlendMode.SoftMasked
               && authoredSource == VfxOpacitySource.TextureAlpha
               && (!hasAlphaChannel || alphaNeedsLuminance)
            ? VfxOpacitySource.TextureLuminance
            : authoredSource;
    }

    /// <summary>
    /// Returns false when the texture is known to carry no usable alpha channel, or when the format could not be
    /// determined at all. Treating an unknown format as "has alpha" makes sprites render as hard opaque quads.
    /// </summary>
    private bool TextureHasAlphaChannel(IEntry texture)
    {
        ExportEntry textureExport = ResolvePreviewExport(texture);
        string format = textureExport?.GetProperty<EnumProperty>("Format")?.Value.Name;
        return format is not null && TextureFormatHasAlpha(format);
    }

    /// <summary>
    /// Some DXT5 particle atlases use RGB as coverage while their alpha channel has a nonzero value in every texel.
    /// Treating that alpha as opacity makes every billboard's rectangular edge visible. Prefer luminance only when
    /// alpha has no transparent texels but RGB contains a meaningful transparent background.
    /// </summary>
    private bool TextureAlphaNeedsLuminance(IEntry texture)
    {
        ExportEntry textureExport = ResolvePreviewExport(texture);
        if (textureExport is null)
        {
            return false;
        }

        string key = $"{textureExport.FileRef.FilePath}:{textureExport.UIndex}";
        if (luminanceOpacityCache.TryGetValue(key, out bool cached))
        {
            return cached;
        }

        bool useLuminance = false;
        try
        {
            var unrealTexture = new LegendaryExplorerCore.Unreal.Classes.Texture2D(textureExport);
            LegendaryExplorerCore.Textures.Image image = unrealTexture.ToImage(LegendaryExplorerCore.Textures.PixelFormat.ARGB);
            useLuminance = ShouldUseLuminanceForOpacity(image.mipMaps[0].data);
        }
        catch
        {
            // TextureCache will report an actionable warning if the texture itself cannot be loaded. Opacity analysis
            // is only a quality improvement, so retain the authored alpha path when the source mip is unavailable.
        }

        luminanceOpacityCache[key] = useLuminance;
        return useLuminance;
    }

    public static bool ShouldUseLuminanceForOpacity(ReadOnlySpan<byte> argbPixels)
    {
        int pixelCount = argbPixels.Length / 4;
        if (pixelCount == 0)
        {
            return false;
        }

        int transparentPixelCount = 0;
        int darkPixelCount = 0;
        for (int offset = 0; offset + 3 < argbPixels.Length; offset += 4)
        {
            if (argbPixels[offset + 3] <= 2)
            {
                transparentPixelCount++;
            }
            // Block compression leaves noise in the nominally black background of particle cards, so a
            // strict zero test misses most real atlases.
            if (argbPixels[offset] <= 8 && argbPixels[offset + 1] <= 8 && argbPixels[offset + 2] <= 8)
            {
                darkPixelCount++;
            }
        }

        // A handful of stray transparent texels is not authored coverage: only treat alpha as the opacity
        // channel when a meaningful portion of the texture is actually transparent.
        int coverageThreshold = Math.Max(1, pixelCount / 100);
        return transparentPixelCount < coverageThreshold && darkPixelCount >= coverageThreshold;
    }

    /// <summary>
    /// When a material's BlendMode could not be read, the preview previously assumed BLEND_Translucent. Combined with a
    /// texture that has no alpha this draws opaque squares. Particle materials in this situation are overwhelmingly
    /// additive, so prefer that: black texels then correctly contribute nothing.
    /// </summary>
    private static void ApplyUnresolvedBlendModeFallback(VfxParticleMaterialDefinition material)
    {
        if (!material.BlendModeResolved
            && material.BlendMode == VfxBlendMode.Translucent
            && material.OpacitySource == VfxOpacitySource.TextureLuminance)
        {
            material.BlendMode = VfxBlendMode.Additive;
        }
    }

    private static bool HasExpression(StructProperty input) =>
        input.GetProp<ObjectProperty>("Expression")?.Value != 0;

    public static bool TextureFormatHasAlpha(string format) => format is
        "PF_DXT3" or "PF_DXT5" or "PF_A8R8G8B8" or "PF_A8" or "PF_G8" or "PF_BC7";

    public static bool IsAuxiliaryParticleTexture(string path, string textureName)
    {
        string name = $"{path}.{textureName}";
        return name.Contains("Distort", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Normal", StringComparison.OrdinalIgnoreCase)
            || name.Contains("_NRM", StringComparison.OrdinalIgnoreCase)
            || name.Contains("_NORM", StringComparison.OrdinalIgnoreCase)
            || name.Contains("NormalMap", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Cube_Map", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Cubemap", StringComparison.OrdinalIgnoreCase);
    }

    private ExportEntry ResolveMaterialParent(ExportEntry material, PropertyCollection properties)
    {
        IEntry parent = properties.GetProp<ObjectProperty>("Parent")?.ResolveToEntry(material.FileRef);
        return ResolvePreviewExport(parent);
    }

    private ExportEntry ResolveBaseMaterial(ExportEntry material)
    {
        var visited = new HashSet<string>();
        ExportEntry current = material;
        while (current is not null && current.ClassName.Contains("MaterialInstance", StringComparison.Ordinal) && visited.Add($"{current.FileRef.FilePath}:{current.UIndex}"))
        {
            IEntry parent = current.GetProperty<ObjectProperty>("Parent")?.ResolveToEntry(current.FileRef);
            current = ResolvePreviewExport(parent);
        }
        return current;
    }

    private ExportEntry ResolvePreviewExport(IEntry entry)
    {
        if (entry is ExportEntry export)
        {
            return export;
        }
        if (entry is not ImportEntry import)
        {
            return null;
        }

        try
        {
            if (EntryImporter.ResolveImport(import, PackageCache) is { } resolved)
            {
                return resolved;
            }
        }
        catch
        {
            // Seek-free level imports are often intentionally absent from the normal associated-package search.
            // The asset database fallbacks below can still provide an equivalent exported copy.
        }

        IEnumerable<VfxImportFallback> fallbacks = importFallbackResolver?.Invoke(import);
        if (fallbacks is null)
        {
            return null;
        }

        foreach (VfxImportFallback fallback in fallbacks)
        {
            try
            {
                IMEPackage package = PackageCache?.GetCachedPackage(fallback.FilePath);
                if (package is null)
                {
                    continue;
                }

                ExportEntry candidate = package.TryGetUExport(fallback.UIndex, out ExportEntry indexed)
                    && IsMatchingImportedExport(indexed, import)
                        ? indexed
                        : package.Exports.FirstOrDefault(exportCandidate => IsMatchingImportedExport(exportCandidate, import));
                if (candidate is not null)
                {
                    return candidate;
                }
            }
            catch
            {
                // A stale or unavailable database usage must not prevent trying the remaining material copies.
            }
        }
        return null;
    }

    private static bool IsMatchingImportedExport(ExportEntry export, ImportEntry import) =>
        export?.ClassName == import.ClassName
        && string.Equals(export.InstancedFullPath, import.InstancedFullPath, StringComparison.Ordinal);

    private static string AppendWarning(string current, string warning) =>
        string.Join(Environment.NewLine, new[] { current, warning }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());

    private void RenderPreview(object sender, EventArgs args)
    {
        DrawHelpers();
        Matrix4x4 previewTransform = Simulation.Definition?.SystemTransform ?? Matrix4x4.Identity;
        var blendedParticles = new List<(VfxEmitterState Emitter, VfxParticle Particle, PreviewTextureCache.TextureEntry Texture, BlendState BlendState, DepthStencilState DepthState)>();
        foreach (VfxEmitterState emitter in Simulation.Emitters)
        {
            if (emitter.Definition.RenderMode == VfxEmitterRenderMode.Mesh)
            {
                if (meshEmitters.TryGetValue(emitter.Definition, out VfxMeshRenderer.MeshEmitterResources meshResources))
                {
                    if (!useGameShader || !VfxMeshRenderer.RenderGameShader(this, emitter, meshResources, previewTransform))
                    {
                        meshRenderer.Render(this, emitter, meshResources, null, previewTransform);
                    }
                }
                continue;
            }
            if (emitter.Definition.RenderMode != VfxEmitterRenderMode.Sprite)
            {
                continue;
            }
            if (useGameShader && gameShaderRenderer.TryRenderSprite(this, emitter, previewTransform))
            {
                continue;
            }
            VfxParticleMaterialDefinition material = emitter.Definition.ParticleMaterial;
            textures.TryGetValue(emitter.Definition, out PreviewTextureCache.TextureEntry texture);
            blendStates.TryGetValue(material.BlendMode, out BlendState blendState);
            bool depthWrite = material.BlendMode is VfxBlendMode.Opaque or VfxBlendMode.Masked;
            depthStates.TryGetValue((!material.DisableDepthTest, depthWrite), out DepthStencilState depthState);
            if (!material.IsSupported || texture?.TextureView is null)
            {
                continue;
            }
            if (depthWrite)
            {
                billboardRenderer.Render(this, emitter, texture.TextureView, blendState, depthState, previewTransform: previewTransform);
                continue;
            }
            foreach (VfxParticle particle in GetDrawableParticles(emitter, previewTransform))
            {
                blendedParticles.Add((emitter, particle, texture, blendState, depthState));
            }
        }

        // Blended particles from every emitter still have to interleave back-to-front, so the shared pass keeps a
        // depth sort. Emitters that request an age-based order are pre-ordered in GetDrawableParticles and are
        // excluded from this sort so their authored order survives.
        if (!blendedParticles.Any(entry => entry.Emitter.Definition.SortMode is VfxSortMode.AgeOldestFirst or VfxSortMode.AgeNewestFirst))
        {
            blendedParticles.Sort((left, right) => VfxBillboardRenderer.DistanceSquared(right.Particle, Camera.Position, previewTransform)
                .CompareTo(VfxBillboardRenderer.DistanceSquared(left.Particle, Camera.Position, previewTransform)));
        }

        // Draw contiguous runs that share the same emitter and render state as a single batch. Sorting stays
        // back-to-front, but emitters with a single material no longer cost one draw call per particle.
        var batch = new List<VfxParticle>();
        for (int index = 0; index < blendedParticles.Count; index++)
        {
            (VfxEmitterState emitter, VfxParticle particle, PreviewTextureCache.TextureEntry texture, BlendState blendState, DepthStencilState depthState) = blendedParticles[index];
            batch.Add(particle);
            bool endOfRun = index + 1 == blendedParticles.Count
                || blendedParticles[index + 1].Emitter != emitter
                || blendedParticles[index + 1].Texture != texture
                || blendedParticles[index + 1].BlendState != blendState
                || blendedParticles[index + 1].DepthState != depthState;
            if (!endOfRun)
            {
                continue;
            }
            billboardRenderer.Render(this, emitter, texture.TextureView, blendState, depthState, batch, previewTransform);
            batch = [];
        }
    }

    /// <summary>
    /// Applies ParticleModuleRequired.SortMode and MaxDrawCount before an emitter's particles are queued
    /// into the shared blended pass.
    /// </summary>
    private List<VfxParticle> GetDrawableParticles(VfxEmitterState emitter, Matrix4x4 previewTransform)
    {
        var particles = new List<VfxParticle>(emitter.Particles);
        VfxBillboardRenderer.SortParticles(particles, emitter.Definition.SortMode, Camera.Position, previewTransform);
        if (emitter.Definition.UseMaxDrawCount && emitter.Definition.MaxDrawCount >= 0 && particles.Count > emitter.Definition.MaxDrawCount)
        {
            particles.RemoveRange(emitter.Definition.MaxDrawCount, particles.Count - emitter.Definition.MaxDrawCount);
        }
        return particles;
    }

    private void CreateBlendStates()
    {
        DisposeBlendStates();
        blendStates[VfxBlendMode.Opaque] = CreateBlendState(BlendOption.One, BlendOption.Zero, false);
        blendStates[VfxBlendMode.Masked] = CreateBlendState(BlendOption.One, BlendOption.Zero, false);
        blendStates[VfxBlendMode.Translucent] = CreateBlendState(BlendOption.SourceAlpha, BlendOption.InverseSourceAlpha, true);
        // UE3's BLEND_Additive is Source + Destination. SourceAlpha here attenuates dark fire atlases twice
        // (once in their RGB and again through luminance-derived alpha), which makes their flames disappear.
        blendStates[VfxBlendMode.Additive] = CreateBlendState(BlendOption.One, BlendOption.One, true);
        blendStates[VfxBlendMode.Modulate] = CreateBlendState(BlendOption.DestinationColor, BlendOption.Zero, true);
        blendStates[VfxBlendMode.ModulateAndAdd] = CreateBlendState(BlendOption.DestinationColor, BlendOption.One, true);
        blendStates[VfxBlendMode.SoftMasked] = CreateBlendState(BlendOption.SourceAlpha, BlendOption.InverseSourceAlpha, true);
        blendStates[VfxBlendMode.AlphaComposite] = CreateBlendState(BlendOption.One, BlendOption.InverseSourceAlpha, true);
    }

    private BlendState CreateBlendState(BlendOption source, BlendOption destination, bool enabled)
    {
        var description = new BlendStateDescription();
        description.RenderTarget[0] = new RenderTargetBlendDescription
        {
            IsBlendEnabled = enabled,
            SourceBlend = source,
            DestinationBlend = destination,
            BlendOperation = BlendOperation.Add,
            SourceAlphaBlend = BlendOption.One,
            DestinationAlphaBlend = BlendOption.InverseSourceAlpha,
            AlphaBlendOperation = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteMaskFlags.All
        };
        return new BlendState(Device, description);
    }

    private void DisposeBlendStates()
    {
        foreach (BlendState blendState in blendStates.Values)
        {
            blendState.Dispose();
        }
        blendStates.Clear();
    }

    private void CreateDepthStates()
    {
        DisposeDepthStates();
        foreach (bool depthTest in new[] { false, true })
        {
            foreach (bool depthWrite in new[] { false, true })
            {
                depthStates[(depthTest, depthWrite)] = new DepthStencilState(Device, new DepthStencilStateDescription
                {
                    IsDepthEnabled = depthTest,
                    DepthComparison = Comparison.LessEqual,
                    DepthWriteMask = depthWrite ? DepthWriteMask.All : DepthWriteMask.Zero
                });
            }
        }
    }

    private void DisposeDepthStates()
    {
        foreach (DepthStencilState depthState in depthStates.Values)
        {
            depthState.Dispose();
        }
        depthStates.Clear();
    }

    private void DrawHelpers()
    {
        const float gridHalfExtent = VfxPreviewDefinition.PreviewGridHalfExtent;
        if (ShowGroundPlane)
        {
            var ground = primitives.BuildMesh(new Vector4(0.16f, 0.16f, 0.16f, 0.45f), 0, Matrix4x4.Identity);
            ground.AddVertex(-gridHalfExtent, -gridHalfExtent, 0);
            ground.AddVertex(gridHalfExtent, -gridHalfExtent, 0);
            ground.AddVertex(gridHalfExtent, gridHalfExtent, 0);
            ground.AddVertex(-gridHalfExtent, gridHalfExtent, 0);
            ground.AddTriangle(0, 1, 2);
            ground.AddTriangle(0, 2, 3);
        }

        if (ShowGrid)
        {
            for (int coordinate = -(int)gridHalfExtent; coordinate <= gridHalfExtent; coordinate += 50)
            {
                Vector4 color = coordinate == 0 ? new Vector4(0.45f, 0.45f, 0.45f, 0.8f) : new Vector4(0.28f, 0.28f, 0.28f, 0.55f);
                primitives.AddLine(new Vector3(coordinate, -gridHalfExtent, 0), new Vector3(coordinate, gridHalfExtent, 0), color, 0);
                primitives.AddLine(new Vector3(-gridHalfExtent, coordinate, 0), new Vector3(gridHalfExtent, coordinate, 0), color, 0);
            }
        }

        if (ShowAxis)
        {
            primitives.AddLine(Vector3.Zero, Vector3.UnitX * 75, new Vector4(1, 0.15f, 0.15f, 1), 0);
            primitives.AddLine(Vector3.Zero, Vector3.UnitY * 75, new Vector4(0.15f, 1, 0.15f, 1), 0);
            primitives.AddLine(Vector3.Zero, Vector3.UnitZ * 75, new Vector4(0.15f, 0.45f, 1, 1), 0);
        }

        if (ShowOrigin)
        {
            const float size = 8;
            primitives.AddLine(new Vector3(-size, 0, 0), new Vector3(size, 0, 0), Vector4.One, 0);
            primitives.AddLine(new Vector3(0, -size, 0), new Vector3(0, size, 0), Vector4.One, 0);
            primitives.AddLine(new Vector3(0, 0, -size), new Vector3(0, 0, size), Vector4.One, 0);
        }

        if (ShowBoundingBox && TryGetPreviewBounds(out Vector3 minimum, out Vector3 maximum))
        {
            AddBounds(minimum, maximum);
        }
        primitives.Render(this, false);
    }

    /// <summary>
    /// Combines the sprite/simulation bounds with the transformed bounds of every mesh emitter's particles.
    /// </summary>
    private bool TryGetPreviewBounds(out Vector3 minimum, out Vector3 maximum)
        => TryGetPreviewBounds(true, out minimum, out maximum);

    /// <summary>
    /// Combines the sprite/simulation bounds with the transformed bounds of every mesh emitter's particles.
    /// </summary>
    /// <param name="allowFixedBounds">
    /// When false, the authored FixedRelativeBoundingBox is ignored and only the bounds of the geometry that is
    /// actually drawn are considered. The fixed box is a culling volume and frequently does not match the visible
    /// extent, so camera framing must not use it.
    /// </param>
    private bool TryGetPreviewBounds(bool allowFixedBounds, out Vector3 minimum, out Vector3 maximum)
        => TryGetPreviewBounds(
            allowFixedBounds,
            Simulation.Definition?.SystemTransform ?? Matrix4x4.Identity,
            out minimum,
            out maximum);

    private bool TryGetPreviewBounds(bool allowFixedBounds, Matrix4x4 previewTransform, out Vector3 minimum, out Vector3 maximum)
    {
        bool found = allowFixedBounds
            ? Simulation.TryGetBounds(previewTransform, out minimum, out maximum)
            : Simulation.TryGetDynamicBounds(previewTransform, out minimum, out maximum);
        foreach (VfxEmitterState emitter in Simulation.Emitters)
        {
            if (emitter.Definition.RenderMode != VfxEmitterRenderMode.Mesh
                || !meshEmitters.TryGetValue(emitter.Definition, out VfxMeshRenderer.MeshEmitterResources resources)
                || !VfxMeshRenderer.TryGetBounds(emitter, resources, previewTransform, Camera.Position, Camera.CameraRight, Camera.CameraUp, Camera.CameraForward, out VfxBounds meshBounds))
            {
                continue;
            }
            minimum = found ? Vector3.Min(minimum, meshBounds.Minimum) : meshBounds.Minimum;
            maximum = found ? Vector3.Max(maximum, meshBounds.Maximum) : meshBounds.Maximum;
            found = true;
        }
        return found;
    }

    private void AddBounds(Vector3 minimum, Vector3 maximum)
    {
        Vector4 color = new(1, 0.75f, 0.1f, 1);
        Vector3[] corners =
        [
            new(minimum.X, minimum.Y, minimum.Z), new(maximum.X, minimum.Y, minimum.Z),
            new(maximum.X, maximum.Y, minimum.Z), new(minimum.X, maximum.Y, minimum.Z),
            new(minimum.X, minimum.Y, maximum.Z), new(maximum.X, minimum.Y, maximum.Z),
            new(maximum.X, maximum.Y, maximum.Z), new(minimum.X, maximum.Y, maximum.Z)
        ];
        int[] edges = [0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7];
        for (int index = 0; index < edges.Length; index += 2)
        {
            primitives.AddLine(corners[edges[index]], corners[edges[index + 1]], color, 0);
        }
    }

    public override void DisposeResources()
    {
        RenderScene -= RenderPreview;
        DisposeBlendStates();
        DisposeDepthStates();
        billboardRenderer.Dispose();
        meshRenderer.Dispose();
        gameShaderRenderer.Dispose();
        DisposeMeshEmitters();
        textures.Clear();
        base.DisposeResources();
    }
}
