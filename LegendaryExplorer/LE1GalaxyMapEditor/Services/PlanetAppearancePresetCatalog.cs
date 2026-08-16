using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Services;

public sealed record PlanetAppearancePreset(
    string ModuleTag,
    string ModuleName,
    int ClusterRowId,
    int SystemRowId,
    int PlanetRowId,
    string ClusterName,
    string SystemName,
    string PlanetName,
    string Shader,
    PlanetVisualKind VisualKind,
    ModuleColor ModuleColor,
    PlanetAppearance Appearance)
{
    public GalaxyMapRowKey PlanetKey => new(GalaxyMapTable.Planet, PlanetRowId);
    public string SearchText => string.Join(' ',
        ModuleTag, ModuleName, ClusterName, SystemName, PlanetName, Shader, PlanetRowId);
    public bool IsExpanded => false;
}

public sealed record PlanetPresetSystemGroup(
    int RowId,
    string Name,
    ModuleColor ModuleColor,
    IReadOnlyList<PlanetAppearancePreset> Planets,
    bool IsExpanded = false);

public sealed record PlanetPresetClusterGroup(
    int RowId,
    string Name,
    ModuleColor ModuleColor,
    IReadOnlyList<PlanetPresetSystemGroup> Systems,
    bool IsExpanded = false);

public sealed record PlanetPresetModuleGroup(
    string Tag,
    string Name,
    ModuleColor ModuleColor,
    IReadOnlyList<PlanetPresetClusterGroup> Clusters,
    bool IsExpanded = false);

public static class PlanetAppearancePresetCatalog
{
    public static IReadOnlyList<PlanetAppearancePreset> Build(GalaxyMapWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var document = workspace.EffectiveDocument;
        var presets = new List<PlanetAppearancePreset>();
        foreach (var layer in workspace.Layers)
        {
            foreach (var planet in layer.Planets.Where(PlanetAppearanceCodec.IsAppearanceCapable))
            {
                var system = document.SystemsByRowId.GetValueOrDefault(planet.SystemRowId);
                var cluster = system is null
                    ? null
                    : document.ClustersByRowId.GetValueOrDefault(system.ClusterRowId);
                var appearance = PlanetAppearanceCodec.Decode(planet);
                presets.Add(new PlanetAppearancePreset(
                    layer.Module.Tag,
                    layer.Module.Name,
                    cluster?.RowId ?? int.MaxValue,
                    system?.RowId ?? planet.SystemRowId,
                    planet.RowId,
                    cluster?.DisplayName ?? "Missing Cluster",
                    system?.DisplayName ?? $"System row {planet.SystemRowId}",
                    planet.DisplayName,
                    appearance.Shader,
                    planet.VisualKind,
                    layer.Module.Color,
                    appearance));
            }
        }

        return presets;
    }

    public static IReadOnlyList<PlanetPresetModuleGroup> Group(
        IEnumerable<PlanetAppearancePreset> presets,
        string? search = null)
    {
        ArgumentNullException.ThrowIfNull(presets);
        var words = (search ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var expandModules = words.Length > 0;
        const bool expandNestedGroups = true;
        return presets
            .Where(preset => words.All(word => preset.SearchText.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(preset => (preset.ModuleTag, preset.ModuleName))
            .Select(module => new PlanetPresetModuleGroup(
                module.Key.ModuleTag,
                module.Key.ModuleName,
                module.First().ModuleColor,
                module.GroupBy(preset => (preset.ClusterRowId, preset.ClusterName))
                    .OrderBy(cluster => cluster.Key.ClusterRowId)
                    .Select(cluster => new PlanetPresetClusterGroup(
                        cluster.Key.ClusterRowId,
                        cluster.Key.ClusterName,
                        cluster.First().ModuleColor,
                        cluster.GroupBy(preset => (preset.SystemRowId, preset.SystemName))
                            .OrderBy(system => system.Key.SystemRowId)
                            .Select(system => new PlanetPresetSystemGroup(
                                system.Key.SystemRowId,
                                system.Key.SystemName,
                                system.First().ModuleColor,
                                system.OrderBy(preset => preset.PlanetRowId)
                                    .ToArray(),
                                expandNestedGroups))
                            .ToArray(),
                        expandNestedGroups))
                    .ToArray(),
                expandModules))
            .ToArray();
    }
}
