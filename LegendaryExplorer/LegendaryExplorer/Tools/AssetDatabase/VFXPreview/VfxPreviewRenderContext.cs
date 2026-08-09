using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorer.Misc.AppSettings;
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

public sealed class VfxPreviewRenderContext : MeshRenderContext, IVfxDepthStateProvider
{
    /// <summary>
    /// Native soft-particle materials require scene depth even when there is no opaque preview scene behind them.
    /// Sampling 1.0 represents the far plane and preserves the authored particle instead of fading it to zero.
    /// </summary>
    public override ShaderResourceView PreviewSceneDepthTextureView => WhiteTexView;

    private sealed class ActorMeshResources : IDisposable
    {
        public SkeletalMesh SkeletalMesh;
        public ModelPreview<WorldVertex> StandardPreview;
        public ModelPreview<LEVertex> GameShaderPreview;

        public void UpdateLocalToWorld(Matrix4x4 transform)
        {
            StandardPreview?.UpdateLocalToWorld(transform);
            GameShaderPreview?.UpdateLocalToWorld(transform);
        }

        public void Dispose()
        {
            StandardPreview?.Dispose();
            GameShaderPreview?.Dispose();
            StandardPreview = null;
            GameShaderPreview = null;
            SkeletalMesh = null;
        }
    }

    private readonly VfxBillboardRenderer billboardRenderer = new();
    private readonly VfxMeshRenderer meshRenderer = new();
    private readonly VfxGameShaderRenderer gameShaderRenderer = new();
    private readonly BatchedPrimitives primitives = new();
    private readonly Dictionary<VfxEmitterDefinition, PreviewTextureCache.TextureEntry> textures = [];
    private readonly Dictionary<VfxEmitterDefinition, VfxMeshRenderer.MeshEmitterResources> meshEmitters = [];
    private readonly Dictionary<VfxBlendMode, BlendState> blendStates = [];
    private readonly Dictionary<(bool DepthTest, bool DepthWrite), DepthStencilState> depthStates = [];

    public DepthStencilState GetVfxDepthState(bool depthTest, bool depthWrite)
        => depthStates.GetValueOrDefault((depthTest, depthWrite));
    private readonly Dictionary<string, bool> luminanceOpacityCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PreviewActorModelComponent, ActorMeshResources> actorModels = [];
    private readonly Dictionary<string, Matrix4x4> actorBoneTransforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> missingAttachmentBones = new(StringComparer.OrdinalIgnoreCase);
    private Func<ImportEntry, IEnumerable<VfxImportFallback>> importFallbackResolver;
    private VfxPreviewBackground background = VfxPreviewBackground.NeutralGray;
    private VfxPreviewShadingMode shadingMode = VfxPreviewShadingMode.Unlit;
    private bool useGameShader = true;
    private string standardRuntimeWarning;
    private bool isDarkMode;
    private bool autoFramePending;
    private bool actorEffectsClearedManually;
    private int autoFrameElapsed;
    private bool autoFrameHasPeak;
    private SkeletalMesh actorSkeleton;
    private Matrix4x4 actorTransform = Matrix4x4.Identity;
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
    public bool HideActor { get; set; }
    public string RuntimeWarning { get; private set; }

    /// <summary>
    /// Uses the compiled native material and vertex-factory preview shared with Meshplorer and Morph Editor.
    /// This is the default VFX preview path. If cooked native resources are genuinely unavailable, the emitter
    /// remains visible through the standard VFX preview and the status panel explains the fallback.
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
        Camera.FirstPerson = !Settings.Global_UseOrbitCameraControls;
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

    public void LoadActorMesh(PreviewActorModelComponent component, ExportEntry skeletalMeshExport)
    {
        if (skeletalMeshExport is null)
        {
            ClearActorMesh(component);
            return;
        }

        SkeletalMesh skeletalMesh = ObjectBinary.From<SkeletalMesh>(skeletalMeshExport);
        if (skeletalMesh.LODModels.Length == 0)
        {
            throw new InvalidOperationException($"{skeletalMeshExport.ObjectName.Instanced} has no skeletal-mesh LODs.");
        }

        var resources = new ActorMeshResources
        {
            SkeletalMesh = skeletalMesh,
            StandardPreview = new ModelPreview<WorldVertex>(this, skeletalMesh),
            GameShaderPreview = TryCreateActorGameShaderPreview(skeletalMesh)
        };
        if (actorModels.Remove(component, out ActorMeshResources previous))
        {
            previous.Dispose();
        }
        actorModels[component] = resources;
        if (component == PreviewActorModelComponent.Body)
        {
            actorSkeleton = skeletalMesh;
            UpdateActorPlacement(resources.StandardPreview);
            RebuildActorBoneTransforms();
        }
        resources.UpdateLocalToWorld(actorTransform);
        ApplyActorMaterialEffects(resources);
        ErrorText = null;
    }

    /// <summary>
    /// Builds the same LEVertex/MaterialRenderProxy representation used by Meshplorer's in-game shader preview.
    /// The standard mesh remains resident as a fallback for original-game assets and materials without a compiled
    /// local-vertex-factory shader.
    /// </summary>
    private ModelPreview<LEVertex> TryCreateActorGameShaderPreview(SkeletalMesh skeletalMesh)
    {
        ModelPreview<LEVertex> preview = null;
        try
        {
            preview = new ModelPreview<LEVertex>(this, skeletalMesh);
            if (preview.LODs.Count == 0)
            {
                preview.Dispose();
                return null;
            }

            bool hasRenderableSection = preview.LODs[0].Sections.Any(section => section.MaterialName is not null
                && preview.Materials.TryGetValue(section.MaterialName, out ModelPreviewMaterial<LEVertex> material)
                && material is LEShaderPreviewMaterial { CanRender: true });
            if (!hasRenderableSection)
            {
                preview.Dispose();
                return null;
            }
            return preview;
        }
        catch
        {
            preview?.Dispose();
            return null;
        }
    }

    public void ClearActorMesh(PreviewActorModelComponent component)
    {
        if (component == PreviewActorModelComponent.Body)
        {
            DisposeActorMeshes();
            return;
        }
        if (actorModels.Remove(component, out ActorMeshResources model))
        {
            model.Dispose();
        }
    }

    private void UpdateActorPlacement(ModelPreview<WorldVertex> bodyPreview)
    {
        BoxSphereBounds bounds = bodyPreview.LODs[0].Mesh.BaseBounds;
        Vector3 minimum = bounds.Origin - bounds.BoxExtent;
        actorTransform = Matrix4x4.CreateTranslation(-bounds.Origin.X, -bounds.Origin.Y, -minimum.Z);
        foreach (ActorMeshResources actorModel in actorModels.Values)
        {
            actorModel.UpdateLocalToWorld(actorTransform);
        }
    }

    private void RebuildActorBoneTransforms()
    {
        actorBoneTransforms.Clear();
        missingAttachmentBones.Clear();
        if (actorSkeleton?.RefSkeleton is not { Length: > 0 } bones)
        {
            return;
        }

        var componentSpace = new Matrix4x4[bones.Length];
        for (int index = 0; index < bones.Length; index++)
        {
            MeshBone bone = bones[index];
            Matrix4x4 local = Matrix4x4.CreateFromQuaternion(bone.Orientation)
                * Matrix4x4.CreateTranslation(bone.Position);
            componentSpace[index] = bone.ParentIndex >= 0 && bone.ParentIndex < index
                ? local * componentSpace[bone.ParentIndex]
                : local;
            actorBoneTransforms[bone.Name.Instanced] = componentSpace[index] * actorTransform;
        }
    }

    private Matrix4x4 GetEmitterPreviewTransform(VfxEmitterDefinition emitter, Matrix4x4 systemTransform)
    {
        if (string.IsNullOrWhiteSpace(emitter.AttachmentBone))
        {
            return emitter.AttachmentTransform * systemTransform;
        }
        if (actorBoneTransforms.TryGetValue(emitter.AttachmentBone, out Matrix4x4 boneTransform))
        {
            return emitter.AttachmentTransform * boneTransform * systemTransform;
        }

        if (missingAttachmentBones.Add(emitter.AttachmentBone))
        {
            RuntimeWarning = AppendWarning(RuntimeWarning,
                $"Actor bone {emitter.AttachmentBone} was not found; {emitter.Name} is attached to the actor root.");
        }
        return emitter.AttachmentTransform * actorTransform * systemTransform;
    }

    private bool HasActorAttachments => Simulation.Definition?.Emitters.Any(
        emitter => !string.IsNullOrWhiteSpace(emitter.AttachmentBone)) == true;

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
        ClearActorMaterialEffects();
        actorEffectsClearedManually = false;
        importFallbackResolver = fallbackResolver;
        Simulation.Load(definition);
        RuntimeWarning = definition.Warnings.Count == 0 ? null : string.Join(Environment.NewLine, definition.Warnings.Distinct());
        ErrorText = null;
        RefreshTextures();
        ApplyActorMaterialEffects();
        Focus();
    }

    public void Unload()
    {
        ClearActorMaterialEffects();
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

    private void ApplyActorMaterialEffects()
    {
        foreach (ActorMeshResources actorModel in actorModels.Values)
        {
            ApplyActorMaterialEffects(actorModel);
        }
    }

    private void ApplyActorMaterialEffects(ActorMeshResources actorModel)
    {
        if (actorEffectsClearedManually)
        {
            return;
        }
        string[] effectNames = Simulation.Definition?.ActorMaterialEffects
            .Select(effect => effect.EffectName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        actorModel.GameShaderPreview?.ApplyNamedRvrMaterialEffects(this, effectNames);
    }

    private void ClearActorMaterialEffects()
    {
        foreach (ActorMeshResources actorModel in actorModels.Values)
        {
            actorModel.GameShaderPreview?.ClearNamedRvrMaterialEffect();
        }
    }

    /// <summary>
    /// Removes the active client-effect material override from every loaded actor component and prevents the
    /// current selection from reapplying it if body, head, or hair resources are rebuilt. Selecting another VFX
    /// starts a new preview and enables its authored actor effects again.
    /// </summary>
    public void ClearAllActorEffects()
    {
        actorEffectsClearedManually = true;
        // Rebuild every native actor preview from its original skeletal mesh. Restoring only the named-effect
        // dictionary is insufficient after a failed material/MIC shader load because that preview may already
        // contain partially constructed white fallback materials.
        foreach (ActorMeshResources actorModel in actorModels.Values)
        {
            actorModel.GameShaderPreview?.Dispose();
            actorModel.GameShaderPreview = actorModel.SkeletalMesh is null
                ? null
                : TryCreateActorGameShaderPreview(actorModel.SkeletalMesh);
            actorModel.UpdateLocalToWorld(actorTransform);
        }
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
            FrameBoundsIncludingActor(minimum, maximum);
        }
        else if (!HideActor && TryGetActorBounds(out minimum, out maximum))
        {
            FrameBounds(minimum, maximum);
        }
        else
        {
            Camera.FocusDepth = DefaultFocusDepth;
            Camera.Position = -Camera.CameraForward * DefaultFocusDepth;
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
        // A bone-attached effect and its actor form one authored coordinate system. Scaling only the particles
        // would tear the effect away from the attachment point, so attached wrappers keep actor-space scale.
        Simulation.Definition.SystemTransform = HasActorAttachments
            ? Matrix4x4.Identity
            : VfxPreviewDefinition.CreateGridFittingTransform(rawBounds);
        if (frameCamera)
        {
            VfxBounds fittedBounds = VfxBoundsMath.Transform(rawBounds, Simulation.Definition.SystemTransform);
            FrameBoundsIncludingActor(fittedBounds.Minimum, fittedBounds.Maximum);
        }
    }

    private void FrameBoundsIncludingActor(Vector3 minimum, Vector3 maximum)
    {
        if (!HideActor && TryGetActorBounds(out Vector3 actorMinimum, out Vector3 actorMaximum))
        {
            minimum = Vector3.Min(minimum, actorMinimum);
            maximum = Vector3.Max(maximum, actorMaximum);
        }
        FrameBounds(minimum, maximum);
    }

    private bool TryGetActorBounds(out Vector3 minimum, out Vector3 maximum)
    {
        minimum = new Vector3(float.MaxValue);
        maximum = new Vector3(float.MinValue);
        bool found = false;
        foreach (ActorMeshResources actorModel in actorModels.Values)
        {
            ModelPreview<WorldVertex> preview = actorModel.StandardPreview;
            if (preview?.LODs.Count is not > 0)
            {
                continue;
            }
            BoxSphereBounds bounds = preview.LODs[0].Mesh.TransformedBounds;
            minimum = Vector3.Min(minimum, bounds.Origin - bounds.BoxExtent);
            maximum = Vector3.Max(maximum, bounds.Origin + bounds.BoxExtent);
            found = true;
        }
        return found;
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

        Camera.FocusDepth = Math.Clamp(distance * FocusPadding, MinimumFocusRadius, MaximumFocusDepth);
        Camera.Position = Camera.FirstPerson
            ? center - Camera.CameraForward * Camera.FocusDepth
            : center;
    }

    public void Restart()
    {
        Simulation.Restart();
        ResetPreviewCamera();
        Focus();
    }

    private void ResetPreviewCamera()
    {
        Camera.FirstPerson = !Settings.Global_UseOrbitCameraControls;
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
                case VfxEmitterRenderMode.Beam:
                case VfxEmitterRenderMode.Trail:
                case VfxEmitterRenderMode.Procedural:
                    // Beams and trails use the standalone line/strip preview and do not require a sprite texture.
                    continue;
                default:
                    RuntimeWarning = AppendWarning(RuntimeWarning, $"{emitter.Name}: {emitter.RenderMode} has no preview renderer.");
                    continue;
            }
            try
            {
                ExportEntry materialExport = ResolvePreviewExport(emitter.Material);
                if (materialExport is null)
                {
                    // Materialless cooked helper emitters are intentionally invisible. A named but unresolved
                    // material is still reported because that indicates missing preview data rather than design.
                    string materialName = emitter.Material?.InstancedFullPath;
                    if (materialName is not null)
                    {
                        RuntimeWarning = AppendWarning(RuntimeWarning,
                            $"{emitter.Name}: material {materialName} could not be resolved, so no sprites can be drawn.");
                    }
                    continue;
                }

                VfxParticleMaterialDefinition particleMaterial = emitter.ParticleMaterial;
                PopulateMaterialProperties(materialExport, particleMaterial);
                IEntry textureEntry = ResolveParticleTexture(materialExport);
                // Uniform-expression and parameter tables commonly point at an import. Follow the same
                // EntryImporter "find definition of import" path used by Package Editor before asking the
                // texture cache to decode it; the cache itself only accepts an exported texture definition.
                textureEntry = ResolvePreviewExport(textureEntry) ?? textureEntry;
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
            bool gameShaderLoaded = false;
            try
            {
                switch (emitter.RenderMode)
                {
                    case VfxEmitterRenderMode.Sprite:
                        ExportEntry materialExport = ResolvePreviewExport(emitter.Material);
                        if (materialExport is not null)
                        {
                            gameShaderLoaded = gameShaderRenderer.TryLoadSprite(this, emitter, materialExport, out warning);
                        }
                        else
                        {
                            warning = "The particle material or import definition could not be resolved.";
                        }
                        break;
                    case VfxEmitterRenderMode.Beam:
                    case VfxEmitterRenderMode.Trail:
                        materialExport = ResolvePreviewExport(emitter.Material);
                        if (materialExport is not null)
                        {
                            gameShaderLoaded = gameShaderRenderer.TryLoadBeamTrail(this, emitter, materialExport, out warning);
                        }
                        else
                        {
                            warning = "The beam/trail material or import definition could not be resolved.";
                        }
                        break;
                    case VfxEmitterRenderMode.Mesh:
                        if (meshEmitters.TryGetValue(emitter, out VfxMeshRenderer.MeshEmitterResources resources))
                        {
                            gameShaderLoaded = VfxMeshRenderer.TryLoadGameShaderPreview(this, resources, out warning);
                        }
                        else
                        {
                            warning = "The mesh emitter geometry could not be resolved.";
                        }
                        break;
                    case VfxEmitterRenderMode.Procedural:
                        warning = "This helper visualization does not expose a cooked native particle material.";
                        break;
                }
            }
            catch (Exception exception)
            {
                warning = $"The in-game shader could not be loaded ({exception.Message}).";
            }

            // A compiled procedural material can intentionally have no texture at all (for example
            // BioVFX_Z_MATERIALS.sparks.Dot_Math_Spark). The standard billboard renderer cannot reproduce its
            // material graph, but the successfully loaded game shader can. Remove only that emitter's standard
            // fallback warning while the game-shader path is active; disabling the option restores it from
            // standardRuntimeWarning.
            if (gameShaderLoaded && !emitter.ParticleMaterial.IsSupported
                && !string.IsNullOrWhiteSpace(emitter.ParticleMaterial.Warning))
            {
                RuntimeWarning = RemoveWarning(RuntimeWarning,
                    $"{emitter.Name}: {emitter.ParticleMaterial.Warning}");
            }

            if (!string.IsNullOrWhiteSpace(warning))
            {
                bool hasVisibleFallback = emitter.RenderMode switch
                {
                    VfxEmitterRenderMode.Sprite => emitter.ParticleMaterial.IsSupported,
                    VfxEmitterRenderMode.Mesh => meshEmitters.ContainsKey(emitter),
                    _ => false
                };
                RuntimeWarning = AppendWarning(RuntimeWarning,
                    hasVisibleFallback
                        ? $"{emitter.Name}: {warning} Using the standard visible preview fallback."
                        : $"{emitter.Name}: {warning}");
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
                textureEntry = ResolvePreviewExport(textureEntry) ?? textureEntry;
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
                if (ResolvePreviewExport(expressionReference.ResolveToEntry(baseMaterial.FileRef)) is { } expression
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
                var candidates = new List<IEntry>();
                candidates.AddRange(binary.SM3MaterialResource.UniformExpressionTextures
                    .Select(current.FileRef.GetEntry)
                    .Where(IsSupportedParticleTexture));

                // Some cooked materials omit the uniform-expression table but retain their texture dependencies.
                // Treat those keys as secondary candidates; imported entries are resolved after selection through
                // the same EntryImporter path used by Package Editor's "Find definition of import" action.
                if (binary.SM3MaterialResource.TextureDependencyLengthMap is { } textureDependencies)
                {
                    candidates.AddRange(textureDependencies
                        .Select(dependency => current.FileRef.GetEntry(dependency.Key))
                        .Where(IsSupportedParticleTexture));
                }

                // Uncooked and partially cooked materials can keep the authoritative texture only on a material
                // expression. Resolve imported expression nodes first, then preserve an imported Texture reference
                // for ResolvePreviewExport to follow into its defining package.
                if (properties.GetProp<ArrayProperty<ObjectProperty>>("Expressions") is { } expressions)
                {
                    foreach (ObjectProperty expressionReference in expressions)
                    {
                        ExportEntry expression = ResolvePreviewExport(expressionReference.ResolveToEntry(current.FileRef));
                        IEntry texture = expression?.GetProperty<ObjectProperty>("Texture")?.ResolveToEntry(expression.FileRef);
                        if (IsSupportedParticleTexture(texture))
                        {
                            candidates.Add(texture);
                        }
                    }
                }

                return SelectParticleTexture(candidates.Distinct(), current);
            }

            current = ResolveMaterialParent(current, properties);
        }
        return null;
    }

    private static bool IsSupportedParticleTexture(IEntry texture) =>
        texture?.ClassName is "Texture2D" or "TextureFlipBook";

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
                    && IsCompatibleImportDefinition(indexed, import)
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

    private static bool IsCompatibleImportDefinition(ExportEntry export, ImportEntry import) =>
        export?.ClassName == import.ClassName
        && (string.Equals(export.InstancedFullPath, import.InstancedFullPath, StringComparison.OrdinalIgnoreCase)
            || export.ObjectName == import.ObjectName);

    private static string AppendWarning(string current, string warning) =>
        string.Join(Environment.NewLine, new[] { current, warning }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());

    private static string RemoveWarning(string current, string warning)
    {
        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(warning))
        {
            return current;
        }
        string filtered = string.Join(Environment.NewLine, current
            .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !string.Equals(line, warning, StringComparison.Ordinal)));
        return string.IsNullOrWhiteSpace(filtered) ? null : filtered;
    }

    private void RenderPreview(object sender, EventArgs args)
    {
        DrawHelpers();
        RenderActor();
        Matrix4x4 systemTransform = Simulation.Definition?.SystemTransform ?? Matrix4x4.Identity;
        var blendedParticles = new List<(VfxEmitterState Emitter, VfxParticle Particle, PreviewTextureCache.TextureEntry Texture, BlendState BlendState, DepthStencilState DepthState, Matrix4x4 Transform)>();
        foreach (VfxEmitterState emitter in Simulation.Emitters)
        {
            Matrix4x4 previewTransform = GetEmitterPreviewTransform(emitter.Definition, systemTransform);
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
            if (emitter.Definition.RenderMode == VfxEmitterRenderMode.Beam)
            {
                if (useGameShader)
                {
                    gameShaderRenderer.TryRenderBeamTrail(this, emitter, previewTransform);
                }
                else
                {
                    AddBeamPreview(emitter, previewTransform);
                }
                continue;
            }
            if (emitter.Definition.RenderMode == VfxEmitterRenderMode.Trail)
            {
                if (useGameShader)
                {
                    gameShaderRenderer.TryRenderBeamTrail(this, emitter, previewTransform);
                }
                else
                {
                    AddTrailPreview(emitter, previewTransform);
                }
                continue;
            }
            if (emitter.Definition.RenderMode == VfxEmitterRenderMode.Procedural)
            {
                if (!useGameShader)
                {
                    AddProceduralPreview(emitter.Definition.Procedural, previewTransform);
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
                blendedParticles.Add((emitter, particle, texture, blendState, depthState, previewTransform));
            }
        }

        // The standalone beam/trail path is deliberately independent of compiled material vertex factories.
        // It preserves authored position, color, lifetime and motion even when the original renderer depended on
        // game-only trail buffers or beam endpoint actors.
        primitives.Render(this, false);

        // Blended particles from every emitter still have to interleave back-to-front, so the shared pass keeps a
        // depth sort. Emitters that request an age-based order are pre-ordered in GetDrawableParticles and are
        // excluded from this sort so their authored order survives.
        if (!blendedParticles.Any(entry => entry.Emitter.Definition.SortMode is VfxSortMode.AgeOldestFirst or VfxSortMode.AgeNewestFirst))
        {
            blendedParticles.Sort((left, right) => VfxBillboardRenderer.DistanceSquared(right.Particle, Camera.Position, right.Transform)
                .CompareTo(VfxBillboardRenderer.DistanceSquared(left.Particle, Camera.Position, left.Transform)));
        }

        // Draw contiguous runs that share the same emitter and render state as a single batch. Sorting stays
        // back-to-front, but emitters with a single material no longer cost one draw call per particle.
        var batch = new List<VfxParticle>();
        for (int index = 0; index < blendedParticles.Count; index++)
        {
            (VfxEmitterState emitter, VfxParticle particle, PreviewTextureCache.TextureEntry texture, BlendState blendState, DepthStencilState depthState, Matrix4x4 previewTransform) = blendedParticles[index];
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

    private void AddBeamPreview(VfxEmitterState emitter, Matrix4x4 transform)
    {
        VfxBeamDefinition beam = emitter.Definition.Beam;
        if (beam is null)
        {
            return;
        }
        if (emitter.Particles.Count == 0)
        {
            Vector3 source = Vector3.Transform(beam.Source.Evaluate(0, 0.5f), transform);
            Vector3 target = Vector3.Transform(beam.Target.Evaluate(0, 0.5f), transform);
            primitives.AddLine(source, target, Vector4.One, 0);
            return;
        }
        foreach (VfxParticle particle in emitter.Particles)
        {
            Vector3 source = Vector3.Transform(beam.Source.Evaluate(particle.RelativeTime, particle.Random), transform);
            Vector3 target = Vector3.Transform(beam.Target.Evaluate(particle.RelativeTime, particle.Random), transform);
            if (Vector3.DistanceSquared(source, target) < 0.0001f)
            {
                float distance = Math.Max(1, beam.Distance.Evaluate(particle.RelativeTime, particle.Random));
                target = source + Vector3.TransformNormal(Vector3.UnitZ * distance, transform);
            }
            Vector4 color = particle.Color == Vector4.Zero ? Vector4.One : particle.Color;
            int segmentCount = Math.Max(1, beam.InterpolationPoints + 1);
            Vector3 previous = source;
            for (int segment = 1; segment <= segmentCount; segment++)
            {
                Vector3 next = Vector3.Lerp(source, target, segment / (float)segmentCount);
                primitives.AddLine(previous, next, color, 0);
                previous = next;
            }
        }
    }

    private void AddTrailPreview(VfxEmitterState emitter, Matrix4x4 transform)
    {
        if (emitter.Particles.Count < 2)
        {
            return;
        }
        VfxParticle[] ordered = emitter.Particles.OrderByDescending(particle => particle.Age).ToArray();
        for (int index = 1; index < ordered.Length; index++)
        {
            Vector3 start = Vector3.Transform(ordered[index - 1].Position + ordered[index - 1].OrbitOffset, transform);
            Vector3 end = Vector3.Transform(ordered[index].Position + ordered[index].OrbitOffset, transform);
            Vector4 color = (ordered[index - 1].Color + ordered[index].Color) * 0.5f;
            primitives.AddLine(start, end, color == Vector4.Zero ? Vector4.One : color, 0);
        }
    }

    private void AddProceduralPreview(VfxProceduralDefinition definition, Matrix4x4 transform)
    {
        if (definition is null)
        {
            return;
        }
        float size = definition.Scale;
        Vector4 color = definition.Color;
        switch (definition.Kind)
        {
            case VfxProceduralKind.LensFlare:
                AddCircle(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, size * 0.45f, color, transform);
                for (int ray = 0; ray < 8; ray++)
                {
                    float angle = ray * MathF.Tau / 8;
                    Vector3 direction = (Vector3.UnitX * MathF.Cos(angle)) + (Vector3.UnitZ * MathF.Sin(angle));
                    primitives.AddLine(
                        Vector3.Transform(direction * size * 0.2f, transform),
                        Vector3.Transform(direction * size, transform), color, 0);
                }
                break;
            case VfxProceduralKind.Framebuffer:
                AddRectangle(new Vector3(-size, 0, -size * 0.6f), new Vector3(size, 0, size * 0.6f), color, transform);
                break;
            case VfxProceduralKind.EffectsMaterial:
                AddCircle(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, size, color, transform);
                AddCircle(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, size, color, transform);
                AddCircle(Vector3.Zero, Vector3.UnitY, Vector3.UnitZ, size, color, transform);
                break;
            case VfxProceduralKind.Decal:
                AddRectangle(new Vector3(-size, -size, 1), new Vector3(size, size, 1), color, transform);
                primitives.AddLine(
                    Vector3.Transform(new Vector3(-size, -size, 1), transform),
                    Vector3.Transform(new Vector3(size, size, 1), transform), color, 0);
                primitives.AddLine(
                    Vector3.Transform(new Vector3(-size, size, 1), transform),
                    Vector3.Transform(new Vector3(size, -size, 1), transform), color, 0);
                break;
            case VfxProceduralKind.CameraShake:
                for (int index = -2; index <= 2; index++)
                {
                    float offset = index * size * 0.15f;
                    primitives.AddLine(Vector3.Transform(new Vector3(-size, offset, 0), transform),
                        Vector3.Transform(new Vector3(size, -offset, 0), transform), color, 0);
                }
                break;
            case VfxProceduralKind.SkeletalMesh:
            case VfxProceduralKind.SpawnActor:
                AddWireBox(new Vector3(-size * 0.35f, -size * 0.25f, 0),
                    new Vector3(size * 0.35f, size * 0.25f, size * 2), color, transform);
                break;
        }
    }

    private void AddCircle(Vector3 center, Vector3 axisA, Vector3 axisB, float radius, Vector4 color, Matrix4x4 transform)
    {
        const int segments = 24;
        Vector3 previous = center + (axisA * radius);
        for (int index = 1; index <= segments; index++)
        {
            float angle = index * MathF.Tau / segments;
            Vector3 current = center + ((axisA * MathF.Cos(angle) + axisB * MathF.Sin(angle)) * radius);
            primitives.AddLine(Vector3.Transform(previous, transform), Vector3.Transform(current, transform), color, 0);
            previous = current;
        }
    }

    private void AddRectangle(Vector3 minimum, Vector3 maximum, Vector4 color, Matrix4x4 transform)
    {
        Vector3[] corners =
        [
            new(minimum.X, minimum.Y, minimum.Z), new(maximum.X, minimum.Y, minimum.Z),
            new(maximum.X, maximum.Y, maximum.Z), new(minimum.X, maximum.Y, maximum.Z)
        ];
        for (int index = 0; index < corners.Length; index++)
        {
            primitives.AddLine(Vector3.Transform(corners[index], transform),
                Vector3.Transform(corners[(index + 1) % corners.Length], transform), color, 0);
        }
    }

    private void AddWireBox(Vector3 minimum, Vector3 maximum, Vector4 color, Matrix4x4 transform)
    {
        Vector3[] corners = new Vector3[8];
        for (int index = 0; index < corners.Length; index++)
        {
            corners[index] = Vector3.Transform(new Vector3(
                (index & 1) == 0 ? minimum.X : maximum.X,
                (index & 2) == 0 ? minimum.Y : maximum.Y,
                (index & 4) == 0 ? minimum.Z : maximum.Z), transform);
        }
        int[] edges = [0, 1, 0, 2, 0, 4, 1, 3, 1, 5, 2, 3, 2, 6, 3, 7, 4, 5, 4, 6, 5, 7, 6, 7];
        for (int index = 0; index < edges.Length; index += 2)
        {
            primitives.AddLine(corners[edges[index]], corners[edges[index + 1]], color, 0);
        }
    }

    private void RenderActor()
    {
        if (HideActor)
        {
            return;
        }

        if (!useGameShader)
        {
            foreach (ActorMeshResources actorModel in actorModels.Values)
            {
                actorModel.StandardPreview?.Render(RenderPass.ANY, this, 0);
            }
            return;
        }

        // Match Meshplorer's pass ordering across the assembled actor: all opaque/base surfaces first,
        // followed by all hair and translucent surfaces. Components without a compiled shader fall back
        // to the standard textured mesh without forcing the rest of the actor off the game-shader path.
        foreach (ActorMeshResources actorModel in actorModels.Values)
        {
            if (actorModel.GameShaderPreview is { } gameShaderPreview)
            {
                gameShaderPreview.Render(RenderPass.Base, this, 0);
            }
            else
            {
                actorModel.StandardPreview?.Render(RenderPass.ANY, this, 0);
            }
        }
        foreach (ActorMeshResources actorModel in actorModels.Values)
        {
            actorModel.GameShaderPreview?.Render(RenderPass.Hair, this, 0);
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
        bool canUseFixedBounds = allowFixedBounds
            && !HasActorAttachments
            && Simulation.Definition?.FixedLocalBounds is { IsValid: true };
        bool found = canUseFixedBounds
            ? Simulation.TryGetBounds(previewTransform, out minimum, out maximum)
            : Simulation.TryGetDynamicBounds(
                emitter => GetEmitterPreviewTransform(emitter, previewTransform),
                out minimum,
                out maximum);
        foreach (VfxEmitterState emitter in Simulation.Emitters)
        {
            Matrix4x4 emitterTransform = GetEmitterPreviewTransform(emitter.Definition, previewTransform);
            if (emitter.Definition.RenderMode != VfxEmitterRenderMode.Mesh
                || !meshEmitters.TryGetValue(emitter.Definition, out VfxMeshRenderer.MeshEmitterResources resources)
                || !VfxMeshRenderer.TryGetBounds(emitter, resources, emitterTransform, Camera.Position, Camera.CameraRight, Camera.CameraUp, Camera.CameraForward, out VfxBounds meshBounds))
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
        DisposeActorMeshes();
        textures.Clear();
        base.DisposeResources();
    }

    private void DisposeActorMeshes()
    {
        foreach (ActorMeshResources actorModel in actorModels.Values)
        {
            actorModel.Dispose();
        }
        actorModels.Clear();
        actorSkeleton = null;
        actorBoneTransforms.Clear();
        missingAttachmentBones.Clear();
        actorTransform = Matrix4x4.Identity;
    }
}
