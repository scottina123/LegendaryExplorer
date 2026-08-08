using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Textures;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Classes;
using LegendaryExplorerCore.Unreal.Collections;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CoreImage = LegendaryExplorerCore.Textures.Image;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Installs baked data exclusively through LegendaryExplorerCore's existing Unreal object, property,
/// texture, and TFC serialization APIs.
/// </summary>
public static class StaticLightingWriter
{
    public static StaticLightingWriteResult Write(StaticLightingBakeResult bake,
        StaticLightingGenerationSettings settings)
    {
        settings.Validate();
        int lightMapTextures = 0;
        int shadowMaps = 0;
        int replacedExistingComponents = 0;
        var cachePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<OpenLevelFile, StaticLightingComponentBake> fileGroup in
                 bake.Components.GroupBy(component => component.Target.File))
        {
            OpenLevelFile file = fileGroup.Key;
            IMEPackage package = file.Package;
            string cacheName = package.Game == MEGame.ME1 ? null : ResolveTextureCacheName(package, settings.TextureCacheName);
            string cachePath = cacheName is null ? null : Path.Combine(Path.GetDirectoryName(package.FilePath)!, cacheName + ".tfc");
            if (cachePath is not null && fileGroup.Any(component => component.Texture is not null))
            {
                EnsureLocalTextureCache(cachePath);
                cachePaths.Add(cachePath);
            }

            var streamingTextures = new List<(ExportEntry Texture, StaticLightingMeshTarget Target, int Resolution)>();
            foreach (StaticLightingComponentBake componentBake in fileGroup)
            {
                if (HasExistingStaticLighting(componentBake.Target.ComponentBinary))
                    replacedExistingComponents++;
                if (componentBake.Texture is { } textureBake)
                {
                    InstallTextureLightMap(componentBake, textureBake, cacheName, cachePath,
                        streamingTextures, ref lightMapTextures, ref shadowMaps);
                }
                else if (componentBake.Vertex is { } vertexBake)
                {
                    InstallVertexLightMap(componentBake, vertexBake, ref shadowMaps);
                }
                file.IsDirty = true;
            }
            AddStreamingTextureInstances(file, streamingTextures);
        }

        return new StaticLightingWriteResult
        {
            ComponentCount = bake.Components.Count,
            LightMapTextureCount = lightMapTextures,
            ShadowMapCount = shadowMaps,
            TextureCachePaths = cachePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            ReplacedExistingComponentCount = replacedExistingComponents
        };
    }

    private static bool HasExistingStaticLighting(StaticMeshComponent component) =>
        component.LODData is { Length: > 0 } &&
        (component.LODData[0].LightMap is { LightMapType: not ELightMapType.LMT_None } ||
         component.LODData[0].ShadowMaps is { Length: > 0 } ||
         component.LODData[0].ShadowVertexBuffers is { Length: > 0 });

    public static string ResolveTextureCacheName(IMEPackage package, string requestedName)
    {
        if (!string.IsNullOrWhiteSpace(requestedName))
            return Path.GetFileNameWithoutExtension(requestedName.Trim());

        string directory = Path.GetDirectoryName(package.FilePath)!;
        string existing = Directory.EnumerateFiles(directory, "*.tfc", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => Path.GetFileName(path).StartsWith("Textures_", StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (existing is not null)
            return Path.GetFileNameWithoutExtension(existing);

        DirectoryInfo current = new(directory);
        string modName = current.Parent?.Name.StartsWith("DLC_", StringComparison.OrdinalIgnoreCase) == true
            ? current.Parent.Name
            : package.FileNameNoExtension;
        return $"Textures_{SanitizeName(modName)}_Lightmass";
    }

    private static void EnsureLocalTextureCache(string cachePath)
    {
        if (File.Exists(cachePath))
        {
            if (new FileInfo(cachePath).Length < 16)
                throw new InvalidDataException($"Texture cache has no valid GUID header: {cachePath}");
            return;
        }

        // This is the same existing TFC header layout and StreamIO helper used by Texture2D.Replace.
        using var stream = new FileStream(cachePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.WriteGuid(Guid.NewGuid());
    }

    private static void InstallTextureLightMap(StaticLightingComponentBake componentBake,
        StaticLightingTextureBake textureBake, string cacheName, string cachePath,
        List<(ExportEntry Texture, StaticLightingMeshTarget Target, int Resolution)> streamingTextures,
        ref int lightMapTextureCount, ref int shadowMapCount)
    {
        StaticLightingMeshTarget target = componentBake.Target;
        IMEPackage package = target.Component.FileRef;
        bool isGame3 = package.Game >= MEGame.ME3;
        int expectedCoefficientCount = isGame3 ? 3 : 4;
        if (textureBake.CoefficientImages.Count != expectedCoefficientCount ||
            textureBake.ScaleVectors.Count != expectedCoefficientCount)
            throw new InvalidDataException($"Unexpected coefficient count for {package.Game} lightmap.");

        var textures = new ExportEntry[expectedCoefficientCount];
        for (int coefficient = 0; coefficient < expectedCoefficientCount; coefficient++)
        {
            bool simple = coefficient == expectedCoefficientCount - 1;
            string name = $"LEX_Lightmass_LM_{target.Component.UIndex}_{coefficient}";
            textures[coefficient] = CreateOrUpdateTexture(package, name, "LightMapTexture2D", null,
                textureBake.Resolution, textureBake.CoefficientImages[coefficient], PixelFormat.ARGB,
                package.Game.IsLEGame() ? PixelFormat.BC7 : PixelFormat.DXT1,
                simple ? "TEXTUREGROUP_Lightmap" : "TEXTUREGROUP_Lightmap",
                cacheName, cachePath, simple, null);
            streamingTextures.Add((textures[coefficient], target, textureBake.Resolution));
            lightMapTextureCount++;
        }

        var lightMap = new LightMap_2D
        {
            LightMapType = ELightMapType.LMT_2D,
            LightGuids = componentBake.LightGuids,
            Texture1 = textures[0].UIndex,
            ScaleVector1 = textureBake.ScaleVectors[0],
            Texture2 = textures[1].UIndex,
            ScaleVector2 = textureBake.ScaleVectors[1],
            Texture3 = textures[2].UIndex,
            ScaleVector3 = textureBake.ScaleVectors[2],
            Texture4 = isGame3 ? 0 : textures[3].UIndex,
            ScaleVector4 = isGame3 ? Vector3.Zero : textureBake.ScaleVectors[3],
            CoordinateScale = textureBake.CoordinateScale,
            CoordinateBias = textureBake.CoordinateBias
        };

        StaticMeshComponent component = target.Component.GetBinaryData<StaticMeshComponent>();
        EnsureLodData(component);
        component.LODData[0].LightMap = lightMap;
        component.LODData[0].ShadowVertexBuffers = [];
        if (textureBake.ShadowMaps.Count > 0)
        {
            var shadows = new int[textureBake.ShadowMaps.Count];
            for (int index = 0; index < textureBake.ShadowMaps.Count; index++)
            {
                StaticLightingShadowBake shadow = textureBake.ShadowMaps[index];
                string name = GetShadowMapName("LEX_Lightmass_SM", target.Component.UIndex,
                    textureBake.ShadowMaps, index);
                ExportEntry texture = CreateOrUpdateTexture(package, name, "ShadowMapTexture2D", target.Component,
                    textureBake.Resolution, shadow.Visibility, PixelFormat.G8, PixelFormat.G8,
                    "TEXTUREGROUP_Shadowmap", cacheName, cachePath, false, shadow.LightGuid);
                shadows[index] = texture.UIndex;
                streamingTextures.Add((texture, target, textureBake.Resolution));
                shadowMapCount++;
            }
            component.LODData[0].ShadowMaps = shadows;
        }
        else
        {
            component.LODData[0].ShadowMaps = [];
        }
        target.Component.WriteBinary(component);
    }

    private static void InstallVertexLightMap(StaticLightingComponentBake componentBake,
        StaticLightingVertexBake vertexBake, ref int shadowMapCount)
    {
        StaticLightingMeshTarget target = componentBake.Target;
        StaticMeshComponent component = target.Component.GetBinaryData<StaticMeshComponent>();
        EnsureLodData(component);
        IReadOnlyList<Vector3> scales = vertexBake.ScaleVectors;
        bool isGame3 = target.Component.Game >= MEGame.ME3;
        component.LODData[0].LightMap = new LightMap_1D
        {
            LightMapType = ELightMapType.LMT_1D,
            LightGuids = componentBake.LightGuids,
            Owner = target.Component.UIndex,
            DirectionalSamples = vertexBake.DirectionalSamples,
            ScaleVector1 = scales[0],
            ScaleVector2 = scales[1],
            ScaleVector3 = scales[2],
            ScaleVector4 = isGame3 ? Vector3.Zero : scales[3],
            SimpleSamples = vertexBake.SimpleSamples
        };
        component.LODData[0].ShadowVertexBuffers = [];

        var shadowReferences = new int[vertexBake.ShadowMaps.Count];
        for (int index = 0; index < vertexBake.ShadowMaps.Count; index++)
        {
            StaticLightingShadowBake shadow = vertexBake.ShadowMaps[index];
            string name = GetShadowMapName("LEX_Lightmass_SM1D", target.Component.UIndex,
                vertexBake.ShadowMaps, index);
            string path = target.Component.InstancedFullPath + "." + name;
            ExportEntry export = target.Component.FileRef.FindExport(path, "ShadowMap1D");
            if (export is null)
            {
                export = target.Component.FileRef.CreateExport(name, "ShadowMap1D", target.Component,
                    indexed: false);
                export.WriteProperties([]);
            }
            export.WriteBinary(new ShadowMap1D
            {
                LightGuid = shadow.LightGuid,
                Samples = shadow.Visibility.Select(PackVisibility).ToArray()
            });
            shadowReferences[index] = export.UIndex;
            shadowMapCount++;
        }
        component.LODData[0].ShadowMaps = shadowReferences;
        target.Component.WriteBinary(component);
    }

    private static string GetShadowMapName(string prefix, int componentIndex,
        IReadOnlyList<StaticLightingShadowBake> shadowMaps, int index)
    {
        Guid guid = shadowMaps[index].LightGuid;
        int occurrence = 1;
        for (int previous = 0; previous < index; previous++)
        {
            if (shadowMaps[previous].LightGuid == guid)
                occurrence++;
        }
        string suffix = occurrence == 1 ? "" : $"_{occurrence}";
        return $"{prefix}_{componentIndex}_{guid:N}{suffix}";
    }

    private static ExportEntry CreateOrUpdateTexture(IMEPackage package, string name, string className,
        IEntry parent, int resolution, byte[] sourceData, PixelFormat sourceFormat, PixelFormat destinationFormat,
        NameReference textureGroup, string cacheName, string cachePath, bool simpleLightMap, Guid? textureGuid)
    {
        string path = parent is null ? name : parent.InstancedFullPath + "." + name;
        ExportEntry export = package.FindExport(path, className) ??
                             package.CreateExport(name, className, parent, indexed: false);
        var properties = new PropertyCollection
        {
            new EnumProperty(CoreImage.getEngineFormatType(destinationFormat), "EPixelFormat", package.Game, "Format"),
            new IntProperty(resolution, "SizeX"),
            new IntProperty(resolution, "SizeY"),
            new EnumProperty(textureGroup, "TextureGroup", package.Game, "LODGroup")
        };
        export.WriteProperties(properties);

        UTexture2D binary;
        if (className == "LightMapTexture2D")
        {
            binary = new LightMapTexture2D
            {
                Mips = [],
                TextureGuid = textureGuid ?? Guid.NewGuid(),
                LightMapFlags = ELightMapFlags.LMF_Streamed |
                                (simpleLightMap ? ELightMapFlags.LMF_SimpleLightmap : ELightMapFlags.LMF_None)
            };
        }
        else
        {
            binary = new UTexture2D { Mips = [], TextureGuid = textureGuid ?? Guid.NewGuid() };
        }
        foreach (MipMap mip in Texture2D.CreateBlankTextureMips(resolution, resolution, destinationFormat))
            binary.Mips.Add(new UTexture2D.Texture2DMipMap(mip.data, mip.width, mip.height));
        export.WriteBinary(binary);

        var image = CoreImage.LoadFromRaw(sourceData, sourceFormat, resolution, resolution);
        var texture = new Texture2D(export);
        texture.Replace(image, export.GetProperties(), forcedTFCName: cacheName, forcedTFCPath: cachePath,
            isPackageStored: package.Game == MEGame.ME1, forcedNewFormat: destinationFormat, forceMipping: true);
        return export;
    }

    private static void AddStreamingTextureInstances(OpenLevelFile file,
        IReadOnlyList<(ExportEntry Texture, StaticLightingMeshTarget Target, int Resolution)> textures)
    {
        if (textures.Count == 0) return;
        Level level = file.LevelExport.GetBinaryData<Level>();
        level.TextureToInstancesMap ??= new UMultiMap<int, StreamableTextureInstanceList>();
        foreach ((ExportEntry texture, StaticLightingMeshTarget target, int resolution) in textures)
        {
            (Vector3 center, float radius) = CalculateBounds(target.Vertices);
            level.TextureToInstancesMap.Remove(texture.UIndex);
            level.TextureToInstancesMap[texture.UIndex] = new StreamableTextureInstanceList
            {
                Instances =
                [
                    new StreamableTextureInstance
                    {
                        BoundingSphere = new Sphere { Center = center, W = radius },
                        TexelFactor = resolution / MathF.Max(radius, 1f)
                    }
                ]
            };
        }
        file.LevelExport.WriteBinary(level);
    }

    private static (Vector3 Center, float Radius) CalculateBounds(IReadOnlyList<StaticLightingVertex> vertices)
    {
        if (vertices.Count == 0) return (Vector3.Zero, 1f);
        Vector3 minimum = vertices[0].Position;
        Vector3 maximum = minimum;
        foreach (StaticLightingVertex vertex in vertices)
        {
            minimum = Vector3.Min(minimum, vertex.Position);
            maximum = Vector3.Max(maximum, vertex.Position);
        }
        Vector3 center = (minimum + maximum) * 0.5f;
        return (center, MathF.Max(1f, (maximum - center).Length()));
    }

    private static void EnsureLodData(StaticMeshComponent component)
    {
        if (component.LODData is { Length: > 0 }) return;
        component.LODData =
        [
            new StaticMeshComponentLODInfo
            {
                LightMap = new LightMap(),
                ShadowMaps = [],
                ShadowVertexBuffers = []
            }
        ];
    }

    private static int PackVisibility(byte value) => value | value << 8 | value << 16 | value << 24;

    private static string SanitizeName(string value)
    {
        char[] result = value.Select(character => char.IsLetterOrDigit(character) || character == '_'
            ? character
            : '_').ToArray();
        return new string(result);
    }
}
