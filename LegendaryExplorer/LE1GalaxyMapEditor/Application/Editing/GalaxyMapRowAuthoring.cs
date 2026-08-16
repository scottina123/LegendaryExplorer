using System.Globalization;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Services;

namespace LE1GalaxyMapEditor.Workflows.Editing;

public static class GalaxyMapRowAuthoring
{
    private static readonly IReadOnlyDictionary<GalaxyMapTable, IReadOnlySet<string>> KnownColumnsByTable =
        new Dictionary<GalaxyMapTable, IReadOnlySet<string>>
        {
            [GalaxyMapTable.Cluster] = Fields("Label", "X", "Y", "Name", "NameText", "SphereSize", "Background"),
            [GalaxyMapTable.System] = Fields("Label", "Cluster", "X", "Y", "Name", "NameText", "Scale", "ShowNebula"),
            [GalaxyMapTable.Planet] = Fields("Label", "System", "X", "Y", "Name", "NameText", "ActiveWorld",
                "Description", "ButtonLabel", "Map", "Scale", "RingColor", "OrbitRing", "SystemLevelType",
                "PlanetLevelType", "Event", "ImageIndex"),
            [GalaxyMapTable.PlotPlanet] = Fields("Code", "Name", "NameText"),
            [GalaxyMapTable.Map] = Fields("Map", "StartPoint"),
            [GalaxyMapTable.Relay] = Fields("StartCluster", "EndCluster")
        };

    private static readonly string[] PlotPlanetAvailabilityColumns =
    [
        "VisibleConditional", "VisibleFunction", "VisibleParameter",
        "UsableConditional", "UsableFunction", "UsableParameter"
    ];

    public static void PrepareNewRow(GalaxyMapLayer layer, GalaxyMapRow row)
    {
        var schema = CsvGalaxyMapLoader.GetCanonicalSchema(row.Table);
        layer.SetSchema(schema);
        var known = KnownColumns(row.Table);
        foreach (var header in schema.Headers.Skip(1).Where(header => !known.Contains(header)))
        {
            if (!row.ExtraFields.ContainsKey(header))
            {
                row.AddExtraField(header, GalaxyMapDefaults.ExtraValue(row.Table, header));
            }
        }

        row.Origin = new GalaxyMapRowOrigin(layer.Module, OverridesLowerLayer: false);
        var values = schema.Headers.Select((_, index) => index == 0
            ? row.RowId.ToString(CultureInfo.InvariantCulture)
            : string.Empty).ToArray();
        var snapshot = new CsvRowSnapshot(
            $"GalaxyMap_{row.Table}_part.csv",
            sourceRowNumber: 0,
            schema.Headers,
            values);
        for (var index = 0; index < schema.Headers.Count; index++)
        {
            snapshot.MarkDirty(index == 0 ? CsvRowSnapshot.RowIdColumnName : schema.Headers[index]);
        }

        row.CsvSnapshot = snapshot;
    }

    public static CsvRowSnapshot EnsureSnapshot(GalaxyMapRow row)
        => row.CsvSnapshot ?? throw new InvalidOperationException(
            $"{row.Table} row {row.RowId} has no source snapshot and cannot be written safely.");

    public static void MarkDirty(GalaxyMapRow row, params string[] columns)
    {
        var snapshot = EnsureSnapshot(row);
        foreach (var column in columns)
        {
            snapshot.MarkDirty(column);
        }
    }

    public static PlotPlanetEntry CreatePlotPlanetRow(GalaxyMapLayer layer, Planet planet)
    {
        var plot = new PlotPlanetEntry
        {
            RowId = planet.RowId,
            Code = planet.ActiveWorld,
            Name = planet.Name,
            NameText = planet.NameText
        };
        PrepareNewRow(layer, plot);
        foreach (var column in PlotPlanetAvailabilityColumns)
        {
            if (planet.ExtraFields.TryGetValue(column, out var value))
            {
                plot.SetExtraField(column, value);
            }
        }

        return plot;
    }

    private static IReadOnlySet<string> KnownColumns(GalaxyMapTable table)
        => KnownColumnsByTable.GetValueOrDefault(table)
           ?? throw new ArgumentOutOfRangeException(nameof(table), table, null);

    private static HashSet<string> Fields(params string[] fields)
        => new(fields, StringComparer.OrdinalIgnoreCase);
}
