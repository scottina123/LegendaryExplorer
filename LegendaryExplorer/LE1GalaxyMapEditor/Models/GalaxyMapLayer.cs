using System.Collections.ObjectModel;
using System.IO;

namespace LE1GalaxyMapEditor.Models;

/// <summary>
/// The physical rows supplied by one module before load-order composition.
/// A part layer may contain any subset of the six tables.
/// </summary>
public sealed class GalaxyMapLayer
{
    private readonly Dictionary<GalaxyMapTable, CsvTableSchema> _schemas = [];
    private readonly Dictionary<GalaxyMapTable, IReadOnlyList<int>> _sourceRowOrder = [];

    public GalaxyMapLayer(GalaxyMapModule module)
    {
        Module = module ?? throw new ArgumentNullException(nameof(module));
    }

    public GalaxyMapModule Module { get; private set; }
    public string? SourcePackagePath { get; private set; }
    public GalaxyMapPackageFingerprint? SourcePackageFingerprint { get; private set; }
    public ObservableCollection<Cluster> Clusters { get; } = [];
    public ObservableCollection<GalaxySystem> Systems { get; } = [];
    public ObservableCollection<Planet> Planets { get; } = [];
    public ObservableCollection<PlotPlanetEntry> PlotPlanets { get; } = [];
    public ObservableCollection<MapEntry> Maps { get; } = [];
    public ObservableCollection<RelayConnection> Relays { get; } = [];

    public void ReplaceModule(GalaxyMapModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        Module = module;
        foreach (var row in AllRows())
        {
            row.Origin = new GalaxyMapRowOrigin(module, row.Origin?.OverridesLowerLayer == true);
        }
    }

    public void SetPackageSource(string packagePath, GalaxyMapPackageFingerprint fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(fingerprint);
        SourcePackagePath = Path.GetFullPath(packagePath);
        SourcePackageFingerprint = fingerprint;
    }

    public IReadOnlyDictionary<GalaxyMapTable, CsvTableSchema> Schemas => _schemas;

    public IEnumerable<GalaxyMapRow> Rows(GalaxyMapTable table) => table switch
    {
        GalaxyMapTable.Cluster => Clusters,
        GalaxyMapTable.System => Systems,
        GalaxyMapTable.Planet => Planets,
        GalaxyMapTable.PlotPlanet => PlotPlanets,
        GalaxyMapTable.Map => Maps,
        GalaxyMapTable.Relay => Relays,
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, null)
    };

    public IEnumerable<GalaxyMapRow> AllRows()
        => Enum.GetValues<GalaxyMapTable>().SelectMany(Rows);

    public GalaxyMapRow? Find(GalaxyMapRowKey key)
        => Rows(key.Table).FirstOrDefault(row => row.RowId == key.RowId);

    public void Add(GalaxyMapRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.Origin = new GalaxyMapRowOrigin(Module, OverridesLowerLayer: false);
        switch (row)
        {
            case Cluster cluster:
                Clusters.Add(cluster);
                break;
            case GalaxySystem system:
                Systems.Add(system);
                break;
            case Planet planet:
                Planets.Add(planet);
                break;
            case PlotPlanetEntry plotPlanet:
                PlotPlanets.Add(plotPlanet);
                break;
            case MapEntry map:
                Maps.Add(map);
                break;
            case RelayConnection relay:
                Relays.Add(relay);
                break;
            default:
                throw new ArgumentException($"Unsupported galaxy-map row type {row.GetType().Name}.", nameof(row));
        }
    }

    /// <summary>Replaces the same-table/same-ID physical row or appends a new one.</summary>
    public GalaxyMapRow? Upsert(GalaxyMapRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.Origin = new GalaxyMapRowOrigin(Module, row.Origin?.OverridesLowerLayer == true);
        return row switch
        {
            Cluster cluster => Upsert(Clusters, cluster),
            GalaxySystem system => Upsert(Systems, system),
            Planet planet => Upsert(Planets, planet),
            PlotPlanetEntry plotPlanet => Upsert(PlotPlanets, plotPlanet),
            MapEntry map => Upsert(Maps, map),
            RelayConnection relay => Upsert(Relays, relay),
            _ => throw new ArgumentException(
                $"Unsupported galaxy-map row type {row.GetType().Name}.", nameof(row))
        };
    }

    public bool Remove(GalaxyMapRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row switch
        {
            Cluster cluster => Clusters.Remove(cluster),
            GalaxySystem system => Systems.Remove(system),
            Planet planet => Planets.Remove(planet),
            PlotPlanetEntry plotPlanet => PlotPlanets.Remove(plotPlanet),
            MapEntry map => Maps.Remove(map),
            RelayConnection relay => Relays.Remove(relay),
            _ => false
        };
    }

    public void SetSchema(CsvTableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schemas[schema.Table] = schema;
    }

    public CsvTableSchema? GetSchema(GalaxyMapTable table) => _schemas.GetValueOrDefault(table);

    /// <summary>
    /// Records row IDs in their physical file order before the effective document
    /// is sorted. Validators use this to detect out-of-order part CSV rows.
    /// </summary>
    public void SetSourceRowOrder(GalaxyMapTable table, IEnumerable<int> rowIds)
        => _sourceRowOrder[table] = Array.AsReadOnly(rowIds.ToArray());

    public IReadOnlyList<int> GetSourceRowOrder(GalaxyMapTable table)
        => _sourceRowOrder.GetValueOrDefault(table) ?? Array.Empty<int>();

    private static T? Upsert<T>(ObservableCollection<T> rows, T row) where T : GalaxyMapRow
    {
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].RowId != row.RowId)
            {
                continue;
            }

            var previous = rows[index];
            rows[index] = row;
            return previous;
        }

        rows.Add(row);
        return null;
    }
}
