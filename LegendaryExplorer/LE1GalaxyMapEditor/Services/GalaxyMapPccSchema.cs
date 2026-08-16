using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Services;

/// <summary>Explicit PCC types for supported galaxy-map columns.</summary>
public static class GalaxyMapPccSchema
{
    private static readonly IReadOnlyDictionary<GalaxyMapTable, IReadOnlyDictionary<string, GalaxyMapCellType>> Types
        = BuildTypes();

    public static IReadOnlyDictionary<string, GalaxyMapCellType> DefaultCellTypes(
        GalaxyMapTable table,
        IEnumerable<string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var supported = Types[table];
        return headers.ToDictionary(
            header => string.IsNullOrWhiteSpace(header) ? CsvRowSnapshot.RowIdColumnName : header,
            header => string.IsNullOrWhiteSpace(header)
                ? GalaxyMapCellType.Int
                : supported.GetValueOrDefault(header, FallbackType(table)),
            StringComparer.OrdinalIgnoreCase);
    }

    public static GalaxyMapCellType DefaultCellType(GalaxyMapTable table, string columnName)
        => string.Equals(columnName, CsvRowSnapshot.RowIdColumnName, StringComparison.OrdinalIgnoreCase)
            ? GalaxyMapCellType.Int
            : Types[table].GetValueOrDefault(columnName, FallbackType(table));

    private static IReadOnlyDictionary<GalaxyMapTable, IReadOnlyDictionary<string, GalaxyMapCellType>> BuildTypes()
    {
        var tables = new Dictionary<GalaxyMapTable, IReadOnlyDictionary<string, GalaxyMapCellType>>
        {
            [GalaxyMapTable.Cluster] = Map(
                Names("Label", "NameText", "Background"),
                Floats("X", "Y"),
                Ints("Name")),
            [GalaxyMapTable.System] = Map(
                Names("Label", "NameText"),
                Floats("X", "Y"),
                Ints("Cluster", "Name", "ShowNebula")),
            [GalaxyMapTable.Planet] = PlanetTypes(),
            [GalaxyMapTable.PlotPlanet] = Map(
                Names("NameText"),
                [],
                Ints("Code", "Name")),
            [GalaxyMapTable.Map] = Map(
                Names("Map", "StartPoint"),
                [],
                []),
            [GalaxyMapTable.Relay] = Map(
                [],
                [],
                Ints("StartCluster", "EndCluster"))
        };
        return tables;
    }

    private static IReadOnlyDictionary<string, GalaxyMapCellType> PlanetTypes()
    {
        var result = new Dictionary<string, GalaxyMapCellType>(StringComparer.OrdinalIgnoreCase);
        Add(result, GalaxyMapCellType.Name, Names("Label", "NameText", "Event", "EventMessage"));
        Add(result, GalaxyMapCellType.Float, Floats("X", "Y", "Scale"));
        Add(result, GalaxyMapCellType.Int, Ints(
            "System", "Name", "ActiveWorld", "Description", "ButtonLabel", "Map", "RingColor",
            "OrbitRing", "SystemLevelType", "PlanetLevelType", "ImageIndex"));

        foreach (var property in PlanetAppearanceSchema.Properties)
        {
            var type = property.Editor switch
            {
                PlanetAppearanceEditorKind.Shader or PlanetAppearanceEditorKind.Texture => GalaxyMapCellType.Name,
                PlanetAppearanceEditorKind.PackedColor => GalaxyMapCellType.Int,
                _ => GalaxyMapCellType.Float
            };
            Add(result, type, property.Columns);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, GalaxyMapCellType> Map(
        IEnumerable<string> names,
        IEnumerable<string> floats,
        IEnumerable<string> ints)
    {
        var result = new Dictionary<string, GalaxyMapCellType>(StringComparer.OrdinalIgnoreCase);
        Add(result, GalaxyMapCellType.Name, names);
        Add(result, GalaxyMapCellType.Float, floats);
        Add(result, GalaxyMapCellType.Int, ints);
        return result;
    }

    private static void Add(
        IDictionary<string, GalaxyMapCellType> result,
        GalaxyMapCellType type,
        IEnumerable<string> columns)
    {
        foreach (var column in columns)
        {
            result[column] = type;
        }
    }

    private static string[] Names(params string[] columns) => columns;
    private static string[] Floats(params string[] columns) => columns;
    private static string[] Ints(params string[] columns) => columns;

    private static GalaxyMapCellType FallbackType(GalaxyMapTable table)
        => table == GalaxyMapTable.Map ? GalaxyMapCellType.Name : GalaxyMapCellType.Int;
}
