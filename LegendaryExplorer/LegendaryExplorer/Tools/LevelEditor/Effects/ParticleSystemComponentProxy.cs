using System.Collections.Generic;
using System.Numerics;
using LegendaryExplorer.Tools.AssetDatabase.VFXPreview;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// An Emitter's ParticleSystemComponent: its particle system, simulated and drawn with the game's shaders (see <see cref="LevelVfxRenderer"/>).
/// It draws in upstream's passes: opaque and masked emitters in <see cref="RenderPass.Base"/>, blended ones from the context's sorted translucent
/// queue (<see cref="LevelEditorRenderContext.QueueTranslucentEffect"/>). Only components that are shown, within the view frustum and drawn in
/// the last frame are simulated; their particle system is prepared the first time they're drawn
/// </summary>
public sealed class ParticleSystemComponentProxy : PrimitiveComponentProxy, ITranslucentEffect
{
    private readonly LevelEditorRenderContext renderContext;
    private readonly IEntry particleSystemEntry;
    private LevelVfxRenderer.Instance instance;
    private bool instanceResolved;
    private uint lastDrawnFrame = uint.MaxValue;

    /// <summary>The ParticleSystem it plays (its Template)</summary>
    public IEntry ParticleSystem => particleSystemEntry;

    /// <summary>Whether its particle system has been prepared and has something to draw</summary>
    internal bool HasRenderableVfx => instance is not null && !instance.System.Disposed;

    internal ParticleSystemComponentProxy(LevelEditorRenderContext context, ExportEntry componentExport, ActorProxy parent)
        : base(context, componentExport, parent)
    {
        renderContext = context;
        particleSystemEntry = Properties.GetProp<ObjectProperty>("Template")?.ResolveToEntry(Export.FileRef);
    }

    /// <summary>
    /// For a new Emitter, until its commit makes its component: a stand-in (an export that isn't in the package) playing <paramref name="particleSystem"/>,
    /// the one chosen for it, at the actor's transform. For a copy of one, with <paramref name="componentValues"/>, its source's component's values (its
    /// transform relative to the actor, HiddenGame), which its commit's copy of the component has
    /// </summary>
    internal ParticleSystemComponentProxy(LevelEditorRenderContext context, ExportEntry standIn, ActorProxy parent, IEntry particleSystem,
        PropertyCollection componentValues = null)
        : base(context, standIn, parent, previewProperties: componentValues ?? [])
    {
        renderContext = context;
        particleSystemEntry = particleSystem;
    }

    private bool ShowsVfx => renderContext.ShowEmitters && renderContext.ShowEmitterVfx && renderContext.CanDrawEffects && particleSystemEntry is not null;

    /// <summary>The simulation, once it can be made. False while its preparation waits for a later frame</summary>
    private bool EnsureInstance()
    {
        if (instance is { System.Disposed: true })
        {
            //its system was released (the level's effects were cleared, or the particle system was edited): made again from the new data
            instance = null;
            instanceResolved = false;
        }
        if (instanceResolved) return true;
        if (!renderContext.VfxRenderer.TryCreateInstance(particleSystemEntry, out instance)) return false;
        instanceResolved = true;
        return true;
    }

    public override void UpdateScene(MeshRenderContext context, float deltaTime)
    {
        // Only what was drawn last frame is simulated: hidden and culled emitters cost nothing. (Update runs before the frame's Render, so
        // NumFrames is still the last frame's)
        if (instance is { System.Disposed: false } && lastDrawnFrame == context.NumFrames && ShowsVfx)
        {
            instance.Simulation.Tick(deltaTime);
        }
    }

    public override void Render(MeshRenderContext context, RenderPass pass)
    {
        switch (pass)
        {
            case RenderPass.Base:
                if (!ShowsVfx) return;
                BoxSphereBounds bounds = GetBounds();
                if (context.CullingFrustum is { } frustum && !frustum.Intersects(bounds)) return;
                if (!EnsureInstance() || instance is null || instance.System.Failed) return;
                lastDrawnFrame = context.NumFrames;
                instance.System.RenderOpaque(renderContext, instance, LocalToWorld);
                if (instance.System.HasTranslucentEmitters)
                {
                    renderContext.QueueTranslucentEffect(this, bounds.Origin);
                }
                break;
            case RenderPass.Translucent:
                //the first effect drawn in the translucent pass draws every queued one, sorted
                renderContext.EndTranslucentPass();
                break;
        }
    }

    void ITranslucentEffect.DrawTranslucent(LevelEditorRenderContext context)
    {
        if (instance is { System.Disposed: false } current)
        {
            current.System.RenderTranslucent(context, current, LocalToWorld);
        }
    }

    /// <summary>
    /// A package was edited: if it changed the particle system (or its emitters and modules), the prepared copy is released and the next frame
    /// prepares it again. <paramref name="force"/> (the context released its preview resources) releases every prepared system
    /// </summary>
    internal void OnPackageUpdated(IMEPackage package, HashSet<int> updatedExports, bool force)
    {
        if (force)
        {
            renderContext.VfxRenderer.Clear();
        }
        else
        {
            renderContext.VfxRenderer.Invalidate(package, updatedExports);
        }
        if (instance is { System.Disposed: true } || !instanceResolved)
        {
            instance = null;
            instanceResolved = false;
        }
    }

    public override BoxSphereBounds GetBounds()
    {
        if (instance?.System.Definition.FixedLocalBounds is { IsValid: true } localBounds)
        {
            VfxBounds worldBounds = VfxBoundsMath.Transform(localBounds, LocalToWorld);
            Vector3 extent = (worldBounds.Maximum - worldBounds.Minimum) * 0.5f;
            return new BoxSphereBounds
            {
                Origin = (worldBounds.Minimum + worldBounds.Maximum) * 0.5f,
                BoxExtent = extent,
                SphereRadius = extent.Length()
            };
        }
        // No authored bounds: the live particles' (cheaply: their centers, grown by the largest particle and a margin for where they'll be by the
        // next frame), and always the area around the emitter, where new particles spawn and beams start
        Vector3 origin = LocalToWorld.Translation;
        Vector3 min = origin - new Vector3(256f), max = origin + new Vector3(256f);
        if (instance is not null)
        {
            foreach (VfxEmitterState emitter in instance.Simulation.Emitters)
            {
                if (emitter.Definition.RenderMode is VfxEmitterRenderMode.Beam)
                {
                    min = Vector3.Min(min, origin - new Vector3(2048f));
                    max = Vector3.Max(max, origin + new Vector3(2048f));
                }
                if (emitter.Particles.Count == 0) continue;
                Vector3 localMin = new(float.MaxValue), localMax = new(float.MinValue);
                float largest = 0;
                foreach (VfxParticle particle in emitter.Particles)
                {
                    Vector3 position = particle.Position + particle.OrbitOffset;
                    localMin = Vector3.Min(localMin, position);
                    localMax = Vector3.Max(localMax, position);
                    largest = System.MathF.Max(largest, System.MathF.Max(System.MathF.Abs(particle.Size.X), System.MathF.Abs(particle.Size.Y)));
                }
                if (emitter.Definition.MeshEmitter?.LocalBounds is { IsValid: true } meshBounds)
                {
                    //a mesh particle's size scales its mesh
                    largest *= System.MathF.Max(meshBounds.Minimum.Length(), meshBounds.Maximum.Length());
                }
                Vector3 margin = new(largest + 64f);
                VfxBounds world = VfxBoundsMath.Transform(new VfxBounds(localMin - margin, localMax + margin),
                    emitter.Definition.AttachmentTransform * LocalToWorld);
                if (!world.IsValid) continue;
                min = Vector3.Min(min, world.Minimum);
                max = Vector3.Max(max, world.Maximum);
            }
        }
        Vector3 halfSize = (max - min) * 0.5f;
        return new BoxSphereBounds { Origin = (min + max) * 0.5f, BoxExtent = halfSize, SphereRadius = halfSize.Length() };
    }

    protected override void Dispose(bool disposing)
    {
        instance = null;
        base.Dispose(disposing);
    }
}
