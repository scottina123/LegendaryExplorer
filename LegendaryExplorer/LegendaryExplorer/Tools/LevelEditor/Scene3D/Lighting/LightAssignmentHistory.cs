using System;
using System.Collections.Generic;
using System.Linq;
using LegendaryExplorer.Lightmaps.Game;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

/// <summary>
/// The source records of each package as it was when first captured in a bake or editor session; recapturing a package keeps its first
/// records. Immutable: changes return a new history, so it can be read on any thread while the owner swaps in the next one.
/// </summary>
public sealed class LightAssignmentHistory
{
    /// <summary>One package's records, read without touching any history.</summary>
    public sealed class PackageRecords
    {
        internal readonly IMEPackage Package;
        internal readonly Dictionary<(ExportEntry, string), PrimitiveLightAssignments> Primitives = [];
        internal readonly HashSet<Guid> Lightmaps = [];
        internal readonly HashSet<Guid> Shadows = [];
        /// <summary>The lights (LightGuid) some primitive lists as irrelevant: the last bake had them, though it may have found they lit nothing</summary>
        internal readonly HashSet<Guid> Irrelevant = [];
        internal readonly List<string> Problems = [];

        internal PackageRecords(IMEPackage package) => Package = package;
    }

    public static LightAssignmentHistory Empty { get; } = new([]);

    private readonly Dictionary<IMEPackage, PackageRecords> packages;
    private readonly HashSet<Guid> lightmaps = [];
    private readonly HashSet<Guid> shadows = [];
    private readonly HashSet<Guid> irrelevant = [];
    public IReadOnlyList<string> Problems { get; }

    private LightAssignmentHistory(Dictionary<IMEPackage, PackageRecords> packages)
    {
        this.packages = packages;
        foreach (var records in packages.Values)
        {
            lightmaps.UnionWith(records.Lightmaps);
            shadows.UnionWith(records.Shadows);
            irrelevant.UnionWith(records.Irrelevant);
        }
        Problems = packages.Values.SelectMany(records => records.Problems).ToArray();
    }

    /// <summary>
    /// Whether the last bake had the light: some primitive's lightmap or shadow maps are for it, or some primitive lists it as irrelevant. One
    /// it found lit nothing anywhere then stays unlit when assignments are preserved, as the game shows it, rather than counting as a new light.
    /// </summary>
    public bool IsKnown(SceneLight light) => lightmaps.Contains(light.LightmapGuid) || shadows.Contains(light.LightGuid) || irrelevant.Contains(light.LightGuid);

    public bool Contains(IMEPackage package) => packages.ContainsKey(package);

    /// <summary>This history with records read earlier by <see cref="Read"/>. A package already captured keeps its first records.</summary>
    public LightAssignmentHistory With(PackageRecords records) => Contains(records.Package) ? this
        : new LightAssignmentHistory(new Dictionary<IMEPackage, PackageRecords>(packages) { [records.Package] = records });

    public LightAssignmentHistory Without(IMEPackage package)
    {
        if (!Contains(package)) return this;
        var remaining = new Dictionary<IMEPackage, PackageRecords>(packages);
        remaining.Remove(package);
        return new LightAssignmentHistory(remaining);
    }

    /// <summary>Reads a package's source records. Parses every primitive, so callers shouldn't hold locks around it.</summary>
    public static PackageRecords Read(IMEPackage package)
    {
        var records = new PackageRecords(package);
        foreach (var export in package.Exports)
        {
            try
            {
                foreach (var source in Sources(export))
                {
                    var record = PrimitiveLightAssignments.From(source);
                    records.Primitives[(source.Owner, source.Slot)] = record;
                    records.Lightmaps.UnionWith(record.Lightmaps);
                    records.Shadows.UnionWith(record.Shadows);
                    records.Irrelevant.UnionWith(PrimitiveLightAssignments.IrrelevantLights(source));
                }
                if (export.ClassName is "ShadowMap1D" or "ShadowMap2D")
                    records.Shadows.Add(PackageShadowMaps.Read(export).LightGuid);
            }
            catch (Exception e)
            {
                records.Problems.Add($"{package.FileNameNoExtension}.{export.InstancedFullPath}: source assignments could not be read ({e.Message})");
            }
        }
        return records;
    }

    private static IEnumerable<LightmapRef> Sources(ExportEntry component)
    {
        switch (component.ClassName)
        {
            case "StaticMeshComponent":
            case "SplineMeshComponent":
                var smc = component.GetBinaryData<StaticMeshComponent>();
                for (int i = 0; i < smc.LODData.Length; i++) yield return new(component, $"LOD{i}", smc.LODData[i].LightMap, smc);
                break;
            case "ModelComponent":
                var model = component.GetBinaryData<ModelComponent>();
                for (int i = 0; i < model.Elements.Length; i++) yield return new(component, $"Element{i}", model.Elements[i].LightMap, model);
                break;
            case "TerrainComponent":
                var terrain = component.GetBinaryData<TerrainComponent>();
                yield return new(component, "Terrain", terrain.LightMap, terrain);
                break;
        }
    }

    public PrimitiveLightAssignments Get(ExportEntry component, string slot) =>
        packages.GetValueOrDefault(component.FileRef)?.Primitives.GetValueOrDefault((component, slot)) ?? PrimitiveLightAssignments.Empty;
}

/// <param name="HasSourceRecord">Whether a bake recorded lighting for the primitive: a lightmap (LMT_None, which every unbaked primitive
/// deserializes as, isn't one) or shadow maps. Without one there is nothing to preserve, as for a newly placed mesh.</param>
public sealed record PrimitiveLightAssignments(bool HasSourceRecord, IReadOnlySet<Guid> Lightmaps, IReadOnlySet<Guid> Shadows)
{
    public static PrimitiveLightAssignments Empty { get; } = new(false, new HashSet<Guid>(), new HashSet<Guid>());
    public static PrimitiveLightAssignments From(LightmapRef source) => Create(source.LightMap, PackageShadowMaps.LightGuids(source).ToHashSet());

    public static PrimitiveLightAssignments Create(LightMap lightmap, IReadOnlySet<Guid> shadows) =>
        new(lightmap is { LightMapType: not ELightMapType.LMT_None } || shadows.Count > 0, Guids(lightmap).ToHashSet(), shadows);

    /// <summary>
    /// The primitive's IrrelevantLights (LightGuids): a static mesh's or terrain's property (one for all LODs, so read with LOD 0), or a BSP
    /// element's. A primitive that doesn't accept lights has none that count (Scottina's Lightmass Baker lists the lights it baked there).
    /// </summary>
    public static IEnumerable<Guid> IrrelevantLights(LightmapRef source)
    {
        if (source.Binary is ModelComponent model)
            return int.TryParse(source.Slot.AsSpan("Element".Length), out int e) && e < model.Elements.Length ? model.Elements[e].IrrelevantLights ?? [] : [];
        if (source.Slot is not ("LOD0" or "Terrain")) return [];
        var props = source.Owner.GetProperties();
        if (props.GetProp<BoolProperty>("bAcceptsLights") is { Value: false }) return [];
        return props.GetProp<ArrayProperty<StructProperty>>("IrrelevantLights")?.Select(CommonStructs.GetGuid).ToList() ?? [];
    }

    public static Guid[] Guids(LightMap map) => map switch
    {
        LightMap_1D m => m.LightGuids ?? [],
        LightMap_2D m => m.LightGuids ?? [],
        LightMap_3 m => m.LightGuids ?? [],
        LightMap_4or6 m => m.LightGuids ?? [],
        LightMap_5 m => m.LightGuids ?? [],
        _ => []
    };
}
