using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Textures;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Classes;
using LegendaryExplorerCore.Unreal.Collections;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        Stopwatch serializationTimer = Stopwatch.StartNew();
        settings.Validate();
        ValidateBakeAssociations(bake);
        int lightMapTextures = 0;
        int irrelevantLightReferences = 0;
        int replacedExistingComponents = 0;
        int excludedUnlitReceiverCount = 0;
        long lightMap1DSerializationTicks = 0;
        long lightMap2DSerializationTicks = 0;
        var cachePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var textureCaches = new Dictionary<string, (FileStream Stream, Guid Guid)>(StringComparer.OrdinalIgnoreCase);
        var removedStreamingTextures = new Dictionary<OpenLevelFile, HashSet<int>>();

        try
        {
            foreach (StaticLightingExcludedReceiver receiver in bake.SceneDiagnostics.ExcludedUnlitReceivers)
            {
                int[] generatedTextureReferences = ResetUnlitReceiver(receiver.Component);
                if (generatedTextureReferences.Length > 0)
                {
                    if (!removedStreamingTextures.TryGetValue(receiver.File, out HashSet<int> references))
                        removedStreamingTextures.Add(receiver.File, references = []);
                    references.UnionWith(generatedTextureReferences);
                }
                receiver.File.IsDirty = true;
                excludedUnlitReceiverCount++;
            }
            foreach ((OpenLevelFile file, HashSet<int> references) in removedStreamingTextures)
                RemoveStreamingTextureInstances(file, references);

            foreach (IGrouping<OpenLevelFile, StaticLightingComponentBake> fileGroup in
                     bake.Components.GroupBy(component => component.Target.File))
            {
                OpenLevelFile file = fileGroup.Key;
                IMEPackage package = file.Package;
                string cacheName = package.Game == MEGame.ME1 ? null : ResolveTextureCacheName(package, settings.TextureCacheName);
                string cachePath = cacheName is null ? null : Path.Combine(Path.GetDirectoryName(package.FilePath)!, cacheName + ".tfc");
                Stream cacheStream = null;
                Guid? cacheGuid = null;
                if (cachePath is not null && fileGroup.Any(component => component.Texture is not null))
                {
                    EnsureLocalTextureCache(cachePath);
                    cachePaths.Add(cachePath);
                    if (!textureCaches.TryGetValue(cachePath, out var cache))
                    {
                        cache = OpenTextureCacheForAppend(cachePath);
                        textureCaches.Add(cachePath, cache);
                    }
                    cacheStream = cache.Stream;
                    cacheGuid = cache.Guid;
                }

                var streamingTextures = new List<(ExportEntry Texture, StaticLightingMeshTarget Target, int Resolution)>();
                foreach (StaticLightingComponentBake componentBake in fileGroup)
                {
                    if (HasExistingStaticLighting(componentBake.Target.ComponentBinary))
                        replacedExistingComponents++;
                    ApplyStaticLightingProperties(componentBake.Target.Component,
                        componentBake.IrrelevantLightGuids);
                    irrelevantLightReferences += componentBake.IrrelevantLightGuids.Length;
                    if (componentBake.Texture is { } textureBake)
                    {
                        long lightMapStart = Stopwatch.GetTimestamp();
                        InstallTextureLightMapWithCacheGuid(componentBake, textureBake, cacheName, cachePath, cacheStream, cacheGuid,
                            streamingTextures, ref lightMapTextures);
                        lightMap2DSerializationTicks += Stopwatch.GetTimestamp() - lightMapStart;
                    }
                    else if (componentBake.Vertex is { } vertexBake)
                    {
                        long lightMapStart = Stopwatch.GetTimestamp();
                        InstallVertexLightMap(componentBake, vertexBake);
                        lightMap1DSerializationTicks += Stopwatch.GetTimestamp() - lightMapStart;
                    }
                    file.IsDirty = true;
                }
                AddStreamingTextureInstances(file, streamingTextures);
            }
        }
        finally
        {
            foreach ((FileStream stream, _) in textureCaches.Values)
                stream.Dispose();
        }
        serializationTimer.Stop();

        return new StaticLightingWriteResult
        {
            ComponentCount = bake.Components.Count,
            LightMapTextureCount = lightMapTextures,
            ShadowMapCount = 0,
            IrrelevantLightReferenceCount = irrelevantLightReferences,
            TextureCachePaths = cachePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            ReplacedExistingComponentCount = replacedExistingComponents,
            ExcludedUnlitReceiverCount = excludedUnlitReceiverCount,
            SerializationMilliseconds = serializationTimer.Elapsed.TotalMilliseconds,
            LightMap1DSerializationMilliseconds = TicksToMilliseconds(lightMap1DSerializationTicks),
            LightMap2DSerializationMilliseconds = TicksToMilliseconds(lightMap2DSerializationTicks)
        };
    }

    private static double TicksToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private static bool HasExistingStaticLighting(StaticMeshComponent component) =>
        component.LODData is { Length: > 0 } &&
        (component.LODData[0].LightMap is { LightMapType: not ELightMapType.LMT_None } ||
         component.LODData[0].ShadowMaps is { Length: > 0 } ||
         component.LODData[0].ShadowVertexBuffers is { Length: > 0 });

    private static int[] ResetUnlitReceiver(ExportEntry componentExport)
    {
        StaticMeshComponent component = componentExport.GetBinaryData<StaticMeshComponent>();
        int[] generatedTextureReferences = component.LODData is { Length: > 0 }
            ? component.LODData[0].LightMap switch
            {
                LightMap_2D map => [map.Texture1, map.Texture2, map.Texture3],
                LightMap_4or6 map => [map.Texture1, map.Texture2, map.Texture3],
                _ => []
            }
            : [];
        generatedTextureReferences = generatedTextureReferences.Where(index => index != 0 &&
                componentExport.FileRef.GetEntry(index)?.ObjectName.Name.StartsWith("LEX_Lightmass_",
                    StringComparison.Ordinal) == true)
            .Distinct().ToArray();
        if (component.LODData is { Length: > 0 })
        {
            component.LODData[0].LightMap = new LightMap { LightMapType = ELightMapType.LMT_None };
            component.LODData[0].ShadowMaps = [];
            component.LODData[0].ShadowVertexBuffers = [];
            componentExport.WriteBinary(component);
        }

        PropertyCollection properties = componentExport.GetProperties();
        properties.AddOrReplaceProp(new BoolProperty(false, "bAcceptsLights"));
        properties.AddOrReplaceProp(new BoolProperty(false, "bAcceptsDynamicLights"));
        properties.AddOrReplaceProp(new BoolProperty(false, "bForceDirectLightMap"));
        properties.AddOrReplaceProp(new BoolProperty(false, "bUsePrecomputedShadows"));
        if (componentExport.Game != MEGame.UDK)
            properties.AddOrReplaceProp(new BoolProperty(false, "bBioForcePrecomputedShadows"));
        properties.RemoveNamedProperty("IrrelevantLights");
        componentExport.WriteProperties(properties);
        return generatedTextureReferences;
    }

    private static void RemoveStreamingTextureInstances(OpenLevelFile file,
        IReadOnlyCollection<int> textureReferences)
    {
        if (textureReferences.Count == 0)
            return;
        Level level = file.LevelExport.GetBinaryData<Level>();
        if (level.TextureToInstancesMap is null)
            return;
        foreach (int textureReference in textureReferences)
            level.TextureToInstancesMap.Remove(textureReference);
        file.LevelExport.WriteBinary(level);
    }

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

    private static void ValidateBakeAssociations(StaticLightingBakeResult bake)
    {
        ExportEntry duplicateComponent = bake.Components.GroupBy(component => component.Target.Component)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateComponent is not null)
            throw new InvalidDataException($"Multiple generated mappings target {duplicateComponent.InstancedFullPath}.");

        foreach (StaticLightingComponentBake componentBake in bake.Components)
        {
            StaticLightingMeshTarget target = componentBake.Target;
            if (componentBake.Texture is not { } texture)
                continue;
            if (!target.UseTextureMapping || !target.HasTextureCoordinates || target.MappingDiagnostics.HasTextureMappingErrors)
                throw new InvalidDataException($"Invalid 2D mapping reached the writer for {target.Component.InstancedFullPath}.");
            int expectedBytes = checked(texture.Resolution * texture.Resolution * 4);
            if (texture.CoefficientImages.Any(image => image is null || image.Length != expectedBytes))
                throw new InvalidDataException($"Generated texture dimensions do not match metadata for {target.Component.InstancedFullPath}.");
            if (!float.IsFinite(texture.CoordinateScale.X) || !float.IsFinite(texture.CoordinateScale.Y) ||
                !float.IsFinite(texture.CoordinateBias.X) || !float.IsFinite(texture.CoordinateBias.Y) ||
                texture.CoordinateScale.X <= 0f || texture.CoordinateScale.Y <= 0f ||
                texture.CoordinateScale.X + texture.CoordinateBias.X > 1.0001f ||
                texture.CoordinateScale.Y + texture.CoordinateBias.Y > 1.0001f)
                throw new InvalidDataException($"Generated coordinate transform is invalid for {target.Component.InstancedFullPath}.");
        }
    }

    private static (FileStream Stream, Guid Guid) OpenTextureCacheForAppend(string cachePath)
    {
        var stream = new FileStream(cachePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        if (stream.Length < 16)
        {
            stream.Dispose();
            throw new InvalidDataException($"Texture cache has no valid GUID header: {cachePath}");
        }
        Guid guid = stream.ReadGuid();
        stream.Seek(0, SeekOrigin.End);
        return (stream, guid);
    }

    private static void ApplyStaticLightingProperties(ExportEntry component, IReadOnlyList<Guid> irrelevantLights)
    {
        PropertyCollection properties = component.GetProperties();
        properties.AddOrReplaceProp(new BoolProperty(false, "bAcceptsLights"));
        properties.AddOrReplaceProp(new BoolProperty(false, "bAcceptsDynamicLights"));
        properties.AddOrReplaceProp(new BoolProperty(true, "bForceDirectLightMap"));
        properties.AddOrReplaceProp(new BoolProperty(true, "bUsePrecomputedShadows"));
        properties.AddOrReplaceProp(new BoolProperty(true, "CastShadow"));
        properties.AddOrReplaceProp(new BoolProperty(false, "bCastDynamicShadow"));
        if (component.Game != MEGame.UDK)
            properties.AddOrReplaceProp(new BoolProperty(true, "bBioForcePrecomputedShadows"));

        var irrelevantLightProperty = new ArrayProperty<StructProperty>(
            irrelevantLights.Select(guid => CommonStructs.GuidProp(guid)), "IrrelevantLights")
        {
            Reference = "Guid"
        };
        properties.AddOrReplaceProp(irrelevantLightProperty);
        component.WriteProperties(properties);
    }

    private static void InstallTextureLightMap(StaticLightingComponentBake componentBake,
        StaticLightingTextureBake textureBake, string cacheName, string cachePath, Stream cacheStream,
        List<(ExportEntry Texture, StaticLightingMeshTarget Target, int Resolution)> streamingTextures,
        ref int lightMapTextureCount)
    {
        Guid? cacheGuid = null;
        if (cacheStream is not null)
        {
            long originalPosition = cacheStream.Position;
            cacheStream.Position = 0;
            cacheGuid = cacheStream.ReadGuid();
            cacheStream.Position = originalPosition;
        }
        InstallTextureLightMapWithCacheGuid(componentBake, textureBake, cacheName, cachePath, cacheStream,
            cacheGuid, streamingTextures, ref lightMapTextureCount);
    }

    private static void InstallTextureLightMapWithCacheGuid(StaticLightingComponentBake componentBake,
        StaticLightingTextureBake textureBake, string cacheName, string cachePath, Stream cacheStream, Guid? cacheGuid,
        List<(ExportEntry Texture, StaticLightingMeshTarget Target, int Resolution)> streamingTextures,
        ref int lightMapTextureCount)
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
            string coefficientName = isGame3
                ? coefficient switch
                {
                    0 => "NormalizedAverageColor",
                    1 => "DirectionalMaxComponent",
                    _ => "SimpleLightmap"
                }
                : $"Coefficient{coefficient + 1}";
            string name = $"LEX_Lightmass_{coefficientName}_{target.Component.UIndex}";
            textures[coefficient] = CreateOrUpdateTexture(package, name, "LightMapTexture2D", null,
                textureBake.Resolution, textureBake.CoefficientImages[coefficient], PixelFormat.ARGB,
                // LE3's base-game static lightmaps use DXT1. It is substantially faster to encode and
                // append than BC7 while also matching the format expected by the stock lightmap assets.
                PixelFormat.DXT1,
                simple ? "TEXTUREGROUP_Lightmap" : "TEXTUREGROUP_Lightmap",
                cacheName, cachePath, cacheStream, cacheGuid, simple, null);
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
        // Direct shadows are already baked into the coefficient textures. Runtime shadow references
        // would apply the same light visibility a second time even though bAcceptsLights is false.
        component.LODData[0].ShadowMaps = [];
        target.Component.WriteBinary(component);
    }

    private static void InstallVertexLightMap(StaticLightingComponentBake componentBake,
        StaticLightingVertexBake vertexBake)
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
        component.LODData[0].ShadowMaps = [];
        target.Component.WriteBinary(component);
    }

    private static ExportEntry CreateOrUpdateTexture(IMEPackage package, string name, string className,
        IEntry parent, int resolution, byte[] sourceData, PixelFormat sourceFormat, PixelFormat destinationFormat,
        NameReference textureGroup, string cacheName, string cachePath, Stream cacheStream, Guid? cacheGuid,
        bool simpleLightMap, Guid? textureGuid)
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
                Mips = [new UTexture2D.Texture2DMipMap([], resolution, resolution)],
                TextureGuid = textureGuid ?? Guid.NewGuid(),
                LightMapFlags = ELightMapFlags.LMF_Streamed |
                                (simpleLightMap ? ELightMapFlags.LMF_SimpleLightmap : ELightMapFlags.LMF_None)
            };
        }
        else
        {
            binary = new UTexture2D
            {
                Mips = [new UTexture2D.Texture2DMipMap([], resolution, resolution)],
                TextureGuid = textureGuid ?? Guid.NewGuid()
            };
        }
        export.WriteBinary(binary);

        var image = CoreImage.LoadFromRaw(sourceData, sourceFormat, resolution, resolution);
        var texture = new Texture2D(export);
        texture.Replace(image, export.GetProperties(), forcedTFCName: cacheName, forcedTFCPath: cachePath,
            isPackageStored: package.Game == MEGame.ME1, forcedNewFormat: destinationFormat, forceMipping: true,
            forcedTFCStream: cacheStream, forcedTFCGuid: cacheGuid);
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

    private static string SanitizeName(string value)
    {
        char[] result = value.Select(character => char.IsLetterOrDigit(character) || character == '_'
            ? character
            : '_').ToArray();
        return new string(result);
    }
}
