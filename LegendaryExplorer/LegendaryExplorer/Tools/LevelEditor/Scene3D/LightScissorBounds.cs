using System;
using System.Numerics;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpDX.Mathematics.Interop;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

/// <summary>
/// Conservative screen bounds for the finite radial attenuation of point and spot lights.
/// </summary>
internal static class LightScissorBounds
{
    /// <summary>
    /// Returns null when the light must be drawn without a scissor, or an empty rectangle when
    /// none of its influence reaches the viewport. Coordinates refer to the render target,
    /// including the viewport's offset and fractional dimensions.
    /// </summary>
    /// <param name="frustum">An optional camera-relative frustum updated with the camera's
    /// view translation cleared, then multiplied by its projection matrix. Depth clipping
    /// must be disabled, so materials that disable it keep their light contribution.</param>
    public static RawRectangle? Compute(SceneLight light, SceneCamera camera,
        float viewportX, float viewportY, float viewportWidth, float viewportHeight, ViewFrustum frustum = null)
    {
        if (light is null || camera is null || light.Type is not (SceneLightType.Point or SceneLightType.Spot)
            || camera.IsOrthographic || !float.IsFinite(light.Radius) || light.Radius <= 0
            || !float.IsFinite(light.FalloffExponent) || light.FalloffExponent <= 0
            || !IsFinite(light.Position) || !IsFinite(camera.EyePosition)
            || !float.IsFinite(camera.ZNear) || camera.ZNear <= 0
            || !float.IsFinite(camera.ZFar) || camera.ZFar <= camera.ZNear
            || !float.IsFinite(camera.aspect) || camera.aspect <= 0
            || !float.IsFinite(camera.FOV) || camera.FOV <= 0 || camera.FOV >= MathF.PI
            || !float.IsFinite(viewportX) || !float.IsFinite(viewportY)
            || !float.IsFinite(viewportWidth) || !float.IsFinite(viewportHeight)
            || viewportWidth <= 0 || viewportHeight <= 0)
        {
            return null;
        }

        double viewportLeft = Math.Floor(viewportX);
        double viewportTop = Math.Floor(viewportY);
        double viewportRight = Math.Ceiling((double)viewportX + viewportWidth);
        double viewportBottom = Math.Ceiling((double)viewportY + viewportHeight);
        if (viewportLeft < int.MinValue || viewportTop < int.MinValue
            || viewportRight > int.MaxValue || viewportBottom > int.MaxValue)
        {
            return null;
        }

        // Subtract the eye before constructing the box. Adding a small radius to an
        // absolute position far from the origin can round both corners to its center.
        Vector3 relativePosition = light.Position - camera.EyePosition;
        var extent = new Vector3(light.Radius);
        Vector3 min = relativePosition - extent;
        Vector3 max = relativePosition + extent;
        if (!IsFinite(relativePosition) || !IsFinite(min) || !IsFinite(max))
        {
            return null;
        }

        Matrix4x4 view = camera.ViewMatrix;
        view.Translation = Vector3.Zero;
        Matrix4x4 viewProjection = view * camera.ProjectionMatrix;
        if (frustum is null)
        {
            frustum = new ViewFrustum();
            frustum.Update(viewProjection, depthClipEnabled: false);
        }
        // The side planes remain safe even when the radius box crosses the eye or near
        // plane: a box wholly outside any side plane cannot illuminate a viewport sample.
        // Reject those lights before projection would require an unrestricted fallback.
        if (!frustum.Intersects(new BoxSphereBounds { Origin = relativePosition, BoxExtent = extent }))
        {
            return new RawRectangle(0, 0, 0, 0);
        }

        if (min.X <= 0 && max.X >= 0 && min.Y <= 0 && max.Y >= 0 && min.Z <= 0 && max.Z >= 0)
        {
            return null;
        }

        // A positive falloff exponent makes attenuation zero outside the radius. A radius
        // box contains the entire sphere, and therefore the spot cone as well.
        // When all corners are in front of the near plane, perspective division preserves
        // convexity: its projected corners bound every point the light can illuminate.
        double left = double.PositiveInfinity, top = double.PositiveInfinity;
        double right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector4((i & 1) == 0 ? min.X : max.X,
                (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z, 1);
            Vector4 clip = Vector4.Transform(corner, viewProjection);
            // SceneCamera uses reversed depth: the near plane is clip.Z == clip.W.
            // Do not trim a box that crosses the eye or near plane, even if its corners
            // happen to project outside the viewport. Its visible intersection can fill it.
            if (!float.IsFinite(clip.X) || !float.IsFinite(clip.Y)
                || !float.IsFinite(clip.Z) || !float.IsFinite(clip.W)
                || clip.W <= camera.ZNear || clip.Z >= clip.W)
            {
                return null;
            }

            double screenX = viewportX + (1 + (double)clip.X / clip.W) * viewportWidth * 0.5;
            double screenY = viewportY + (1 - (double)clip.Y / clip.W) * viewportHeight * 0.5;
            left = Math.Min(left, screenX);
            top = Math.Min(top, screenY);
            right = Math.Max(right, screenX);
            bottom = Math.Max(bottom, screenY);
        }

        // Include neighboring pixels for subpixel/MSAA coverage and projection rounding.
        left = Math.Clamp(Math.Floor(left) - 1, viewportLeft, viewportRight);
        top = Math.Clamp(Math.Floor(top) - 1, viewportTop, viewportBottom);
        right = Math.Clamp(Math.Ceiling(right) + 1, viewportLeft, viewportRight);
        bottom = Math.Clamp(Math.Ceiling(bottom) + 1, viewportTop, viewportBottom);
        return new RawRectangle((int)left, (int)top, (int)right, (int)bottom);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
