namespace LE1GalaxyMapEditor.Models;

/// <summary>
/// Composes BASEGAME plus ordered module layers. A later row with the same table
/// and Row ID intentionally mounts above the earlier row; the source rows remain
/// present in their layers and read-only BASEGAME is never modified.
/// </summary>
public sealed class GalaxyMapWorkspace
{
    private readonly List<GalaxyMapLayer> _moduleLayers = [];
    private IReadOnlyDictionary<GalaxyMapRowKey, IReadOnlyList<GalaxyMapRow>> _overrideChains =
        new Dictionary<GalaxyMapRowKey, IReadOnlyList<GalaxyMapRow>>();

    public GalaxyMapWorkspace(GalaxyMapLayer baseLayer, IEnumerable<GalaxyMapLayer>? moduleLayers = null)
    {
        BaseLayer = baseLayer ?? throw new ArgumentNullException(nameof(baseLayer));
        if (!ReferenceEquals(BaseLayer.Module, GalaxyMapModule.BaseGame) && !BaseLayer.Module.IsBaseGame)
        {
            throw new ArgumentException("The workspace base layer must use the BASEGAME module.", nameof(baseLayer));
        }

        if (moduleLayers is not null)
        {
            foreach (var layer in moduleLayers)
            {
                Mount(layer, recompose: false);
            }
        }

        EffectiveDocument = new GalaxyMapDocument();
        Recompose();
    }

    public GalaxyMapLayer BaseLayer { get; }
    public GalaxyMapModule BaseGame => BaseLayer.Module;
    public IReadOnlyList<GalaxyMapLayer> ModuleLayers => _moduleLayers;
    public IReadOnlyList<GalaxyMapModule> Modules => _moduleLayers.Select(layer => layer.Module).ToArray();
    public IEnumerable<GalaxyMapLayer> Layers => new[] { BaseLayer }.Concat(OrderedModuleLayers());
    public GalaxyMapModule? ActiveModule { get; private set; }
    public GalaxyMapLayer? ActiveLayer => ActiveModule is null
        ? null
        : _moduleLayers.FirstOrDefault(layer => ReferenceEquals(layer.Module, ActiveModule));
    public GalaxyMapDocument EffectiveDocument { get; private set; }

    /// <summary>
    /// Monotonically identifies successful effective-document compositions. This is
    /// deliberately separate from the editor-session revision: a session change can
    /// be presentation-only, and this counter makes repeated composition observable.
    /// </summary>
    public long CompositionRevision { get; private set; }

    public void Mount(GalaxyMapLayer layer) => Mount(layer, recompose: true);

    /// <summary>Mounts several layers and composes once after the complete load-order stack is present.</summary>
    public void MountRange(IEnumerable<GalaxyMapLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        foreach (var layer in layers)
        {
            Mount(layer, recompose: false);
        }

        Recompose();
    }

    public bool Unmount(GalaxyMapModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var layer = _moduleLayers.FirstOrDefault(candidate => ReferenceEquals(candidate.Module, module));
        if (layer is null)
        {
            return false;
        }

        _moduleLayers.Remove(layer);
        if (ReferenceEquals(ActiveModule, module))
        {
            ActiveModule = null;
        }

        Recompose();
        return true;
    }

    public void SetActiveModule(GalaxyMapModule? module)
    {
        if (module is null)
        {
            ActiveModule = null;
            return;
        }

        if (module.IsBaseGame || module.IsReadOnly)
        {
            throw new InvalidOperationException("The active editing module must be writable and cannot be BASEGAME.");
        }

        if (_moduleLayers.All(layer => !ReferenceEquals(layer.Module, module)))
        {
            throw new InvalidOperationException("The active editing module must be mounted in this workspace.");
        }

        ActiveModule = module;
    }

    public GalaxyMapLayer ReplaceModule(GalaxyMapModule current, GalaxyMapModule replacement)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        var layer = _moduleLayers.FirstOrDefault(candidate => ReferenceEquals(candidate.Module, current))
            ?? throw new InvalidOperationException($"Module {current.Tag} is not mounted.");
        if (_moduleLayers.Any(candidate => !ReferenceEquals(candidate, layer) &&
                string.Equals(candidate.Module.Tag, replacement.Tag, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A module tagged {replacement.Tag} is already mounted.");
        }

        var wasActive = ReferenceEquals(ActiveModule, current);
        layer.ReplaceModule(replacement);
        if (wasActive)
        {
            ActiveModule = replacement;
        }

        Recompose();
        return layer;
    }

    public GalaxyMapRow? Resolve(GalaxyMapRowKey key) => key.Table switch
    {
        GalaxyMapTable.Cluster => EffectiveDocument.ClustersByRowId.GetValueOrDefault(key.RowId),
        GalaxyMapTable.System => EffectiveDocument.SystemsByRowId.GetValueOrDefault(key.RowId),
        GalaxyMapTable.Planet => EffectiveDocument.PlanetsByRowId.GetValueOrDefault(key.RowId),
        GalaxyMapTable.PlotPlanet => EffectiveDocument.PlotPlanetsByRowId.GetValueOrDefault(key.RowId),
        GalaxyMapTable.Map => EffectiveDocument.MapsByRowId.GetValueOrDefault(key.RowId),
        GalaxyMapTable.Relay => EffectiveDocument.Relays.FirstOrDefault(row => row.RowId == key.RowId),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
    };

    public IReadOnlyList<GalaxyMapRow> GetOverrideChain(GalaxyMapRowKey key)
        => _overrideChains.GetValueOrDefault(key) ?? Array.Empty<GalaxyMapRow>();

    public void Recompose()
    {
        var result = GalaxyMapComposer.Compose(BaseLayer, OrderedModuleLayers());
        EffectiveDocument = result.Document;
        _overrideChains = result.OverrideChains;
        CompositionRevision++;
    }

    private void Mount(GalaxyMapLayer layer, bool recompose)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (layer.Module.IsBaseGame)
        {
            throw new InvalidOperationException("A workspace can contain only its single BASEGAME layer.");
        }

        if (_moduleLayers.Any(existing =>
                string.Equals(existing.Module.Tag, layer.Module.Tag, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A module tagged {layer.Module.Tag} is already mounted.");
        }

        _moduleLayers.Add(layer);
        if (recompose)
        {
            Recompose();
        }
    }

    private IEnumerable<GalaxyMapLayer> OrderedModuleLayers()
        => _moduleLayers.OrderBy(layer => layer.Module.LoadOrder)
            .ThenBy(layer => layer.Module.Tag, StringComparer.OrdinalIgnoreCase);
}

public sealed record GalaxyMapCompositionResult(
    GalaxyMapDocument Document,
    IReadOnlyDictionary<GalaxyMapRowKey, IReadOnlyList<GalaxyMapRow>> OverrideChains);

public static class GalaxyMapComposer
{
    public static GalaxyMapCompositionResult Compose(
        GalaxyMapLayer baseLayer,
        IEnumerable<GalaxyMapLayer> moduleLayers)
    {
        ArgumentNullException.ThrowIfNull(baseLayer);
        ArgumentNullException.ThrowIfNull(moduleLayers);

        var layers = new[] { baseLayer }.Concat(moduleLayers).ToArray();
        var document = new GalaxyMapDocument { SourceFolder = DescribeSources(layers) };
        var chains = new Dictionary<GalaxyMapRowKey, List<GalaxyMapRow>>();

        foreach (var table in Enum.GetValues<GalaxyMapTable>())
        {
            var effective = new Dictionary<int, GalaxyMapRow>();
            var lowerLayerIds = new HashSet<int>();
            foreach (var layer in layers)
            {
                foreach (var row in layer.Rows(table))
                {
                    var overridesLowerLayer = lowerLayerIds.Contains(row.RowId);
                    row.Origin = new GalaxyMapRowOrigin(layer.Module, overridesLowerLayer);
                    var key = row.Key;
                    if (!chains.TryGetValue(key, out var chain))
                    {
                        chain = [];
                        chains[key] = chain;
                    }

                    chain.Add(row);
                    effective[row.RowId] = row;
                }

                lowerLayerIds.UnionWith(layer.Rows(table).Select(row => row.RowId));
            }

            foreach (var physicalRow in effective.Values.OrderBy(row => row.RowId))
            {
                AddEffectiveRow(document, GalaxyMapRowCloner.Clone(physicalRow));
            }
        }

        document.RebuildRelationships();
        var readOnlyChains = chains.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<GalaxyMapRow>)pair.Value.ToArray());
        return new GalaxyMapCompositionResult(document, readOnlyChains);
    }

    private static void AddEffectiveRow(GalaxyMapDocument document, GalaxyMapRow row)
    {
        switch (row)
        {
            case Cluster cluster:
                document.Clusters.Add(cluster);
                break;
            case GalaxySystem system:
                document.Systems.Add(system);
                break;
            case Planet planet:
                document.Planets.Add(planet);
                break;
            case PlotPlanetEntry plotPlanet:
                document.PlotPlanets.Add(plotPlanet);
                break;
            case MapEntry map:
                document.Maps.Add(map);
                break;
            case RelayConnection relay:
                document.Relays.Add(relay);
                break;
            default:
                throw new InvalidOperationException($"Unsupported galaxy-map row type {row.GetType().Name}.");
        }
    }

    private static string DescribeSources(IEnumerable<GalaxyMapLayer> layers)
        => string.Join(" + ", layers.Select(layer => layer.Module.Tag));
}
