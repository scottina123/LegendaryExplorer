using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>A translucent effect (a particle system's blended emitters, a lens flare) drawn from the context's sorted translucent queue</summary>
internal interface ITranslucentEffect
{
    /// <summary>Draws the effect's translucent parts. Called once per frame it was queued in, back to front among the queued effects</summary>
    void DrawTranslucent(LevelEditorRenderContext context);
}

// fork-vfx: the translucent effects queue. Effects are drawn by their actors' Render, in upstream's passes: in RenderPass.Base each visible, unculled
// effect draws its opaque parts and queues its translucent ones here (it has been drawn by then, so frustum culling and visibility are already
// decided); in RenderPass.Translucent the first effect to render draws the whole queue, back to front, after copying the scene depth for soft
// particles. So effects are sorted among themselves without the window's pass loop knowing about them, and always draw before DrawUI (whose
// UIPrimitives clear the depth buffer). A window that calls EndTranslucentPass after its translucent pass gets the same result.
public partial class LevelEditorRenderContext
{
    private readonly record struct QueuedTranslucentEffect(float Depth, int Order, ITranslucentEffect Effect);

    private readonly List<QueuedTranslucentEffect> translucentEffects = [];
    private uint translucentEffectsFrame = uint.MaxValue;
    private bool translucentEffectsDrawn;

    //farthest first; equal depths keep the order they were queued in
    private static readonly Comparison<QueuedTranslucentEffect> BackToFront = static (a, b) =>
        a.Depth != b.Depth ? b.Depth.CompareTo(a.Depth) : a.Order.CompareTo(b.Order);

    private void BeginEffectsFrame()
    {
        if (translucentEffectsFrame == NumFrames) return;
        translucentEffectsFrame = NumFrames;
        translucentEffects.Clear();
        translucentEffectsDrawn = false;
    }

    /// <summary>
    /// Queues an effect's translucent parts to be drawn this frame, sorted back to front by <paramref name="position"/> (its bounds' center).
    /// Called from <see cref="RenderPass.Base"/>, before the translucent pass draws the queue
    /// </summary>
    internal void QueueTranslucentEffect(ITranslucentEffect effect, Vector3 position)
    {
        BeginEffectsFrame();
        if (translucentEffectsDrawn) return;
        float depth = Camera.IsOrthographic
            ? Vector3.Dot(position, Camera.CameraForward)
            : Vector3.Dot(position - Camera.EyePosition, Camera.CameraForward);
        translucentEffects.Add(new QueuedTranslucentEffect(float.IsFinite(depth) ? depth : 0, translucentEffects.Count, effect));
    }

    /// <summary>Whether effects should be drawn: they draw only with the game's shaders, whose view constants each frame sets up</summary>
    internal bool CanDrawEffects => UseGameShaders;

    /// <summary>
    /// Draws this frame's queued translucent effects, back to front, once: the first call in a frame draws them and later ones do nothing.
    /// Effects call it from <see cref="RenderPass.Translucent"/>; a window can call it right after its translucent pass instead
    /// </summary>
    public void EndTranslucentPass()
    {
        BeginEffectsFrame();
        if (translucentEffectsDrawn) return;
        translucentEffectsDrawn = true;
        if (translucentEffects.Count == 0) return;

        translucentEffects.Sort(BackToFront);
        //soft particles and depth-biased alpha fade against what's behind them
        CopySceneDepthForEffects();
        //drawn from inside one actor's Render: its selection tint and hit ID mustn't apply to the others
        ShaderFlags flags = RenderFlags;
        Vector3 hitId = CurrentHitTestId;
        RenderFlags &= ~(ShaderFlags.Selected | ShaderFlags.Highlighted);
        try
        {
            foreach (QueuedTranslucentEffect queued in translucentEffects)
            {
                try
                {
                    queued.Effect.DrawTranslucent(this);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Debug.WriteLine($"Translucent effect failed to draw: {e}");
                }
            }
        }
        finally
        {
            RenderFlags = flags;
            CurrentHitTestId = hitId;
            translucentEffects.Clear();
        }
    }
}
