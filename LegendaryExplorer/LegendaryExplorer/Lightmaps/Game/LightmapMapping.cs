using System;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.Lightmaps.Game;

/// <summary>Mappings the baker can rewrite without allocating a new lightmap or changing cooked material shaders.</summary>
public static class LightmapMapping
{
    /// <summary>Reuse a shadow-only receiver's mapping kind and resolution without allocating a lightmap.
    /// Mixed vertex/texture policies and inconsistent layouts cannot be represented by a single solve.</summary>
    public static (int Width, int Height)? ShadowAllocation(LightmapRef receiver, StaticMeshRenderData lod, int coordinateIndex)
    {
        var shadows = PackageShadowMaps.Of(receiver);
        if (shadows.Count == 0) return null;
        if (shadows.All(s => s.PerVertex))
            return shadows.All(s => ObjectBinary.From<ShadowMap1D>(s.Export).Samples.Length == lod.PositionVertexBuffer.VertexData.Length)
                && lod.PositionVertexBuffer.VertexData.Length > 0 ? (0, 0) : null;
        if (shadows.Any(s => s.PerVertex) || coordinateIndex < 0 || coordinateIndex >= lod.VertexBuffer.NumTexCoords) return null;
        (int Width, int Height)? size = null;
        foreach (var shadow in shadows)
        {
            var props = shadow.Export.GetProperties();
            if (props.GetProp<ObjectProperty>("Texture") is not { Value: > 0 } texture
                || !receiver.Owner.FileRef.TryGetUExport(texture.Value, out var export)) return null;
            int width = export.GetProperty<IntProperty>("SizeX")?.Value ?? 0;
            int height = export.GetProperty<IntProperty>("SizeY")?.Value ?? 0;
            if (width <= 0 || height <= 0 || props.GetProp<StructProperty>("CoordinateScale") is not { } scaleProp
                || props.GetProp<StructProperty>("CoordinateBias") is not { } biasProp) return null;
            static Vector2 Vector(StructProperty p) => new(p.GetProp<FloatProperty>("X")?.Value ?? 0, p.GetProp<FloatProperty>("Y")?.Value ?? 0);
            var scale = Vector(scaleProp);
            var bias = Vector(biasProp);
            if (!float.IsFinite(scale.X + scale.Y + bias.X + bias.Y) || scale.X <= 0 || scale.Y <= 0
                || bias.X < 0 || bias.Y < 0 || bias.X + scale.X > 1.00001f || bias.Y + scale.Y > 1.00001f) return null;
            var rect = AtlasRect.FromCoordinates(scale, bias, width, height, padding: 1);
            if (rect.Width <= 2 || rect.Height <= 2 || size is { } previous && previous != (rect.Width, rect.Height)) return null;
            size = (rect.Width, rect.Height);
        }
        return size;
    }

    public static bool IsRebakeable(LightMap map) => map?.LightMapType is ELightMapType.LMT_1D or ELightMapType.LMT_2D
        or ELightMapType.LMT_3 or ELightMapType.LMT_4 or ELightMapType.LMT_6;

    public static int VertexSampleCount(LightMap map) => map switch
    {
        LightMap_1D m => m.DirectionalSamples?.Length ?? 0,
        LightMap_3 m => m.DirectionalSamples?.Length ?? 0,
        _ => -1
    };

    /// <summary>
    /// The largest a bake grows a lightmap to. LE3 draws 4096 textures, but levels whose meshes already have 1024 lightmaps each (as Scottina's
    /// Lightmass Baker lays them out) would then need ~120 GB to bake and several GB of texture cache, past a TFC's 2 GiB
    /// </summary>
    public const int MaxTextureSize = 2048;

    /// <summary>
    /// A texture allocation's interior (mapped texels) after a bake's lightmap scale: padded allocations (static meshes) keep LE3's 4k - 2 interior
    /// sizes, unpadded ones (BSP surface groups) whole DXT blocks, grown to at most <see cref="MaxTextureSize"/> (one already larger keeps its size).
    /// The bake solves each mapping at this size and the rewriter repacks it at the same size, so the two must agree.
    /// </summary>
    public static int ScaledInterior(int interior, float scale, bool padded)
    {
        if (scale == 1 || interior <= 0) return interior;
        int wanted = Math.Max(1, (int)MathF.Round(interior * scale));
        int scaled = padded ? Math.Min(AtlasPacker.InteriorSize(wanted), MaxTextureSize - 2) : Math.Min((wanted + 3) & ~3, MaxTextureSize);
        return Math.Max(interior, scaled);
    }

    /// <summary>The atlas size a bake packs scaled lightmaps into: LE3's 1024 at scale 1, growing with the scale up to <see cref="MaxTextureSize"/></summary>
    public static int AtlasSize(float scale)
    {
        int size = 1024;
        while (size < MaxTextureSize && size < 1024 * scale) size <<= 1;
        return size;
    }

    public static (int Width, int Height)? TextureAllocation(IMEPackage package, LightMap map)
    {
        var (texture, scale, bias) = map switch
        {
            LightMap_2D m => (m.Texture1, m.CoordinateScale, m.CoordinateBias),
            LightMap_4or6 m => (m.Texture1, m.CoordinateScale, m.CoordinateBias),
            _ => (0, default, default)
        };
        if (texture == 0 || !package.TryGetUExport(texture, out var export)) return null;
        var props = export.GetProperties();
        int width = props.GetProp<IntProperty>("SizeX")?.Value ?? 0, height = props.GetProp<IntProperty>("SizeY")?.Value ?? 0;
        var rect = AtlasRect.FromCoordinates(scale, bias, width, height, padding: 1);
        return (rect.Width, rect.Height);
    }

    public static bool IsCompatible(IMEPackage package, LightMap map, StaticMeshRenderData lod, int coordinateIndex) =>
        IsRebakeable(map) && FitsMesh(map, lod, coordinateIndex)
        && (VertexSampleCount(map) > 0 || TextureAllocation(package, map) is { Width: > 2, Height: > 2 });

    /// <summary>
    /// Whether the game can draw the lightmap on the mesh LOD, whatever its type: a vertex lightmap has a sample for each vertex, and a texture lightmap's
    /// coordinate channel is one the mesh has
    /// </summary>
    public static bool FitsMesh(LightMap map, StaticMeshRenderData lod, int coordinateIndex) => VertexSampleCount(map) is >= 0 and var samples
        ? samples == lod.PositionVertexBuffer.VertexData.Length
        : coordinateIndex >= 0 && coordinateIndex < lod.VertexBuffer.NumTexCoords;
}
