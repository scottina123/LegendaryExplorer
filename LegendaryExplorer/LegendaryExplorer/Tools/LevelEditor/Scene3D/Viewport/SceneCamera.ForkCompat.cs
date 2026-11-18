using System;
using System.Numerics;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.SharpDX;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

/// <summary>
/// Fork camera API on upstream's camera
/// </summary>
public partial class SceneCamera
{
    private float roll;

    /// <summary>
    /// Roll around the view direction, in radians (fork: cinematic camera previews, matinee camera keys). Zero keeps upstream's world-up view
    /// </summary>
    public float Roll
    {
        get => roll;
        set
        {
            roll = value.Wrap(0, MathF.PI * 2);
            if (MathF.Abs(roll) < 1e-6f || MathF.Abs(roll - MathF.PI * 2) < 1e-6f) roll = 0;
            CalcViewMatrix();
        }
    }

    /// <summary>
    /// Fork: half the orthographic view's height in world units. Upstream stores the width (<see cref="OrthoWidth"/>); this converts with <see cref="aspect"/>
    /// </summary>
    public float OrthoSize
    {
        get => OrthoWidth / (2f * MathF.Max(aspect, float.Epsilon));
        set => OrthoWidth = value * 2f * MathF.Max(aspect, float.Epsilon);
    }

    /// <summary>
    /// Fork: the far plane. Upstream's perspective projection has none (infinite reversed Z), so this is the orthographic view's depth range
    /// (<see cref="OrthoDepthRange"/>); setting it doesn't clip perspective views
    /// </summary>
    public float ZFar
    {
        get => OrthoDepthRange;
        set => OrthoDepthRange = value;
    }

    /// <summary>
    /// Scales clip-space X and Y, so the view fills only a centered part of the render target (letterboxing for
    /// <see cref="MeshRenderContext.ConstrainedAspectRatio"/>). One for a view that fills it
    /// </summary>
    internal Vector2 ViewportNdcScale { get; set; } = Vector2.One;

    private Matrix4x4 ApplyViewportScale(Matrix4x4 projection)
    {
        Vector2 scale = ViewportNdcScale;
        if (scale == Vector2.One) return projection;
        //row vectors: clip.x is the dot of the point with column 1, clip.y with column 2
        projection.M11 *= scale.X; projection.M21 *= scale.X; projection.M31 *= scale.X; projection.M41 *= scale.X;
        projection.M12 *= scale.Y; projection.M22 *= scale.Y; projection.M32 *= scale.Y; projection.M42 *= scale.Y;
        return projection;
    }
}
