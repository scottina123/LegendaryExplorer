using System;
using LegendaryExplorer.Tools.AssetDatabase.VFXPreview;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpDX.Direct3D11;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Fork API on upstream's Level Editor render context. The fork's BioStage, curve, camera preset, galaxy map and actor preview tools, and the
/// fork's VFX and lens flare renderers, were written against it. Upstream's frame pacing draws every viewport each composition, so the fork's
/// demand-rendering hooks are no-ops; show flags map onto upstream's outliner categories where one exists.
/// </summary>
public partial class LevelEditorRenderContext : IVfxDepthStateProvider
{
    #region Events

    /// <summary>An actor was right-clicked (not dragged), after it was selected. (Upstream's <see cref="ActorMenuRequested"/> is raised too)</summary>
    public event Action<ActorProxy> RightClickActor;

    /// <summary>A hit proxy that isn't an actor, a widget axis, a light handle or a spline handle was left-clicked (BioStage bones, curve keys)</summary>
    public event Action<IHitProxy> SelectHitProxy;

    /// <summary>As <see cref="SelectHitProxy"/>, right-clicked</summary>
    public event Action<IHitProxy> RightClickHitProxy;

    /// <summary>Empty space in the viewport was right-clicked</summary>
    public event Action RightClickViewport;

    #endregion

    #region Hit proxies

    /// <summary>Registers a hit proxy drawn by the caller (with <see cref="IHitProxy.HitID"/> as its hit color). Returns its ID</summary>
    public int AddHitProxy(IHitProxy hitProxy)
    {
        hitProxy.HitID = HitProxies.Add(hitProxy);
        return hitProxy.HitID;
    }

    public void RemoveHitProxy(IHitProxy hitProxy)
    {
        if (hitProxy.HitID > 0)
        {
            HitProxies.RemoveAt(hitProxy.HitID);
            hitProxy.HitID = 0;
        }
    }

    #endregion

    #region Primitives

    private bool isDrawingUI;

    /// <summary>
    /// Fork: one batch of lines and meshes. Primitives added while the scene is drawn are hidden behind it (<see cref="WorldPrimitives"/>); those
    /// added by UI elements during <see cref="DrawUI"/> are drawn over it (<see cref="UIPrimitives"/>), as the fork drew them
    /// </summary>
    public BatchedPrimitives Primitives => isDrawingUI ? UIPrimitives : WorldPrimitives;

    #endregion

    #region Show flags

    /// <summary>Shows volume brushes: the outliner's "Volumes" category</summary>
    public bool ShowVolumes
    {
        get => GetCategoryState("Volumes").IsVisible;
        set => GetCategoryState("Volumes").IsVisible = value;
    }

    /// <summary>Shows volumetric light meshes: the outliner's "Volumetric meshes" category</summary>
    public bool ShowVolumetrics
    {
        get => GetCategoryState("Volumetric meshes").IsVisible;
        set => GetCategoryState("Volumetric meshes").IsVisible = value;
    }

    /// <summary>Shows lights' icons (upstream draws lights only as icons): the outliner's "Lights" category. Hidden lights still light the scene</summary>
    public bool ShowLightIcons
    {
        get => GetCategoryState("Lights").IsVisible;
        set => GetCategoryState("Lights").IsVisible = value;
    }

    /// <summary>Shows Emitter actors (their markers, and with <see cref="ShowEmitterVfx"/> their particles). Within the "Effects helpers" category</summary>
    public bool ShowEmitters = true;

    /// <summary>Shows Emitter markers. Within the "Effects helpers" category</summary>
    public bool ShowEmitterIcons = true;

    /// <summary>Shows LensFlareSource markers. Within the "Effects helpers" category</summary>
    public bool ShowLensFlareIcons = true;

    /// <summary>Shows decal actors. Within upstream's decal visibility ("Effects helpers" or "Decals")</summary>
    public bool ShowDecalActors = true;

    /// <summary>Shows SFXPointOfInterest markers. Within the "Gameplay" category</summary>
    public bool ShowPointsOfInterest = true;

    /// <summary>Fork: the selected light's radius or cone. Upstream's <see cref="LightGizmo"/> draws it with its handles</summary>
    public bool ShowSelectedLightWireframe = true;

    /// <summary>Fork light icon budget. Upstream budgets helpers itself (<see cref="MaxHelperMarkers"/>, <see cref="HelperDrawDistance"/>)</summary>
    public float LightIconRadius = 20000f;

    public int MaxLightIcons = 200;

    private bool IsHiddenByForkShowFlags(ActorProxy actor) => actor switch
    {
        EmitterActorProxy => !ShowEmitters, //icons are hidden per-marker by EmitterActorProxy, not the effect
        LensFlareSourceProxy => false, //ShowLensFlareIcons hides only the icon (LensFlareSourceProxy)
        DecalActorProxy => !ShowDecalActors,
        SFXPointOfInterestProxy => !ShowPointsOfInterest,
        _ => false
    };

    /// <summary>Fork: draws emitters' particle systems. Set by <see cref="SetShowEmitterVfx"/>; the VFX renderer reads it</summary>
    public bool ShowEmitterVfx { get; private set; }

    public void SetShowEmitterVfx(bool show) => ShowEmitterVfx = show;

    #endregion

    #region Rendering hooks

    /// <summary>
    /// Fork: kept the viewport rendering while set (animation playback). Upstream renders every composition, so this changes nothing
    /// </summary>
    public bool ForceContinuousRendering { get; set; }

    /// <summary>Fork: gave input priority over its background resource worker. Upstream has none, so this does nothing</summary>
    internal void NotifyUserActivity() { }

    /// <summary>
    /// Fork: game-shader previews of actors' meshes. Upstream's mesh components always draw with the game's shaders when <see cref="MeshRenderContext.UseGameShaders"/>
    /// is on, so this is that flag
    /// </summary>
    public bool UseGameShaderMeshPreviews
    {
        get => UseGameShaders;
        set => UseGameShaders = value;
    }

    /// <summary>
    /// Fork: let static meshes use the game-shader preview (dialogue previews turned it off to keep frame rates up). Upstream draws all meshes the
    /// same way and is fast enough to, so this is kept but changes nothing
    /// </summary>
    public bool UseGameShaderStaticMeshPreviews { get; set; } = true;

    private int staticSceneRevision;

    /// <summary>Fork: changed whenever the drawn actors did, for the (now disabled) static scene cache's keys</summary>
    internal int StaticSceneRevision => staticSceneRevision ^ DrawList_3D.Count;

    /// <summary>An actor's bounds (upstream computes them on demand, the fork cached them)</summary>
    internal BoxSphereBounds GetActorBounds(ActorProxy actor) => actor.GetBounds();

    #endregion

    #region Fork VFX and lens flare renderers

    private LevelVfxRenderer vfxRenderer;
    private LevelLensFlareRenderer lensFlareRenderer;

    /// <summary>The fork's particle renderer, drawing through this context's game-shader path. Created on first use</summary>
    internal LevelVfxRenderer VfxRenderer => vfxRenderer ??= new LevelVfxRenderer(this);

    /// <summary>The fork's lens flare renderer, drawing through this context's game-shader path. Created on first use</summary>
    internal LevelLensFlareRenderer LensFlareRenderer => lensFlareRenderer ??= new LevelLensFlareRenderer(this);

    public DepthStencilState GetVfxDepthState(bool depthTest, bool depthWrite) => VfxRenderer.GetDepthState(depthTest, depthWrite);

    private void UnloadForkRenderers()
    {
        staticSceneRevision++;
        vfxRenderer?.Clear();
        lensFlareRenderer?.Clear();
    }

    private void DisposeForkRenderers()
    {
        vfxRenderer?.Dispose();
        vfxRenderer = null;
        lensFlareRenderer?.Dispose();
        lensFlareRenderer = null;
    }

    #endregion
}
