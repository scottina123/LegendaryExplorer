using System.Globalization;
using System.IO;
using System.Text;
using LE1GalaxyMapEditor.Models;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.Classes;

namespace LE1GalaxyMapEditor.Services;

/// <summary>Loads module-owned galaxy-map partial 2DAs directly from an LE1 PCC.</summary>
public sealed class PccGalaxyMapLoader
{
    private const string ExportClassName = "Bio2DANumberedRows";

    private static readonly IReadOnlyDictionary<GalaxyMapTable, string> ExportNames
        = new Dictionary<GalaxyMapTable, string>
        {
            [GalaxyMapTable.Cluster] = "GalaxyMap_Cluster_part",
            [GalaxyMapTable.System] = "GalaxyMap_System_part",
            [GalaxyMapTable.Planet] = "GalaxyMap_Planet_part",
            [GalaxyMapTable.PlotPlanet] = "GalaxyMap_PlotPlanet_part",
            [GalaxyMapTable.Map] = "GalaxyMap_Map_part",
            [GalaxyMapTable.Relay] = "GalaxyMap_Relay_part"
        };

    private readonly CsvGalaxyMapLoader _modelLoader;

    public PccGalaxyMapLoader(CsvGalaxyMapLoader? modelLoader = null)
    {
        _modelLoader = modelLoader ?? new CsvGalaxyMapLoader();
    }

    public GalaxyMapLayer Load(
        string packagePath,
        GalaxyMapModule module,
        bool allowEmpty = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(module);
        var fullPath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPath))
        {
            throw new GalaxyMapLoadException($"The galaxy-map PCC does not exist: {fullPath}");
        }

        var initialFingerprint = GalaxyMapPackageFingerprint.Capture(fullPath);
        var tables = new Dictionary<GalaxyMapTable, LoadedPccTable>();

        try
        {
            using var package = MEPackageHandler.OpenLE1Package(fullPath, forceLoadFromDisk: true);
            foreach (var pair in ExportNames)
            {
                var matches = package.Exports.Where(export =>
                    !export.IsDefaultObject &&
                    string.Equals(export.ClassName, ExportClassName, StringComparison.Ordinal) &&
                    string.Equals(export.ObjectName.Name, pair.Value, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length > 1)
                {
                    throw new GalaxyMapLoadException(
                        $"'{fullPath}' contains more than one {ExportClassName} export named '{pair.Value}'.");
                }

                if (matches.Length == 1)
                {
                    tables[pair.Key] = ReadTable(fullPath, pair.Key, matches[0]);
                }
            }
        }
        catch (GalaxyMapLoadException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new GalaxyMapLoadException(
                $"Could not open the LE1 galaxy-map PCC '{fullPath}': {exception.Message}",
                exception);
        }

        if (tables.Count == 0 && !allowEmpty)
        {
            throw new GalaxyMapLoadException(
                $"'{fullPath}' does not contain a supported galaxy-map partial 2DA export.");
        }

        var finalFingerprint = GalaxyMapPackageFingerprint.Capture(fullPath);
        if (initialFingerprint != finalFingerprint)
        {
            throw new GalaxyMapLoadException(
                $"'{fullPath}' changed while it was being loaded. Refresh and try again.");
        }

        if (tables.Count == 0)
        {
            var emptyLayer = new GalaxyMapLayer(module);
            emptyLayer.SetPackageSource(fullPath, finalFingerprint);
            return emptyLayer;
        }

        var projected = tables.ToDictionary(
            pair => pair.Key,
            pair => new GalaxyMapTextTableData(pair.Value.SourceIdentity.ExportObjectName, pair.Value.ProjectedCsv));
        var layer = _modelLoader.LoadProjectedTables(projected, module);
        layer.SetPackageSource(fullPath, finalFingerprint);

        foreach (var pair in tables)
        {
            ApplyPccSource(layer, pair.Key, pair.Value);
        }

        return layer;
    }

    public static IReadOnlyDictionary<GalaxyMapTable, string> SupportedExports => ExportNames;

    private static LoadedPccTable ReadTable(
        string packagePath,
        GalaxyMapTable table,
        ExportEntry export)
    {
        var bio2Da = new Bio2DA(export);
        var canonicalColumns = CsvGalaxyMapLoader.GetCanonicalSchema(table).Headers.Skip(1).ToArray();
        var actualColumns = new HashSet<string>(bio2Da.ColumnNames, StringComparer.OrdinalIgnoreCase);
        var missingColumns = canonicalColumns.Where(column => !actualColumns.Contains(column)).ToArray();
        if (missingColumns.Length > 0)
        {
            throw new GalaxyMapLoadException(
                $"Export '{export.ObjectName.Instanced}' in '{packagePath}' is missing canonical {table} " +
                $"column(s): {string.Join(", ", missingColumns)}.");
        }

        var rowIds = new int[bio2Da.RowCount];
        var cells = new GalaxyMapSourceCell[bio2Da.RowCount][];
        for (var rowIndex = 0; rowIndex < bio2Da.RowCount; rowIndex++)
        {
            if (!int.TryParse(
                    bio2Da.RowNames[rowIndex],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out rowIds[rowIndex]))
            {
                throw new GalaxyMapLoadException(
                    $"Export '{export.ObjectName.Instanced}' contains invalid numbered row ID " +
                    $"'{bio2Da.RowNames[rowIndex]}'.");
            }

            cells[rowIndex] = new GalaxyMapSourceCell[bio2Da.ColumnCount];
            for (var columnIndex = 0; columnIndex < bio2Da.ColumnCount; columnIndex++)
            {
                cells[rowIndex][columnIndex] = SourceCell(bio2Da.Cells[rowIndex, columnIndex]);
            }
        }

        if (rowIds.Distinct().Count() != rowIds.Length)
        {
            throw new GalaxyMapLoadException(
                $"Export '{export.ObjectName.Instanced}' contains duplicate numbered row IDs.");
        }

        var identity = new GalaxyMapTableSourceIdentity(
            packagePath,
            export.ObjectName.Name,
            export.ClassName);
        return new LoadedPccTable(
            identity,
            bio2Da.ColumnNames.ToArray(),
            rowIds,
            cells,
            ProjectCsv(bio2Da.ColumnNames, rowIds, cells));
    }

    private static void ApplyPccSource(
        GalaxyMapLayer layer,
        GalaxyMapTable table,
        LoadedPccTable source)
    {
        var headers = new[] { string.Empty }.Concat(source.ColumnNames).ToArray();
        var defaults = GalaxyMapPccSchema.DefaultCellTypes(table, headers);
        layer.SetSchema(new CsvTableSchema(table, headers, defaults, source.SourceIdentity));
        layer.SetSourceRowOrder(table, source.RowIds);

        var rows = layer.Rows(table).ToDictionary(row => row.RowId);
        for (var index = 0; index < source.RowIds.Length; index++)
        {
            var rowId = source.RowIds[index];
            if (!rows.TryGetValue(rowId, out var row))
            {
                throw new GalaxyMapLoadException(
                    $"The projected {table} row {rowId} could not be materialised.");
            }

            var typedCells = new GalaxyMapSourceCell[headers.Length];
            typedCells[0] = GalaxyMapSourceCell.Int(rowId);
            source.Cells[index].CopyTo(typedCells, 1);
            row.CsvSnapshot = CsvRowSnapshot.FromPccRow(
                source.SourceIdentity.ExportObjectName,
                sourceRowNumber: index + 1,
                headers,
                typedCells);
        }
    }

    private static GalaxyMapSourceCell SourceCell(Bio2DACell cell)
        => cell.Type switch
        {
            Bio2DACell.Bio2DADataType.TYPE_INT => GalaxyMapSourceCell.Int(cell.IntValue),
            Bio2DACell.Bio2DADataType.TYPE_FLOAT => GalaxyMapSourceCell.Float(cell.FloatValue),
            Bio2DACell.Bio2DADataType.TYPE_NAME => GalaxyMapSourceCell.Name(cell.NameValue.Instanced),
            Bio2DACell.Bio2DADataType.TYPE_NULL => GalaxyMapSourceCell.Null(),
            _ => throw new GalaxyMapLoadException($"Unsupported Bio2DA cell type '{cell.Type}'.")
        };

    private static byte[] ProjectCsv(
        IReadOnlyList<string> columns,
        IReadOnlyList<int> rowIds,
        IReadOnlyList<GalaxyMapSourceCell[]> cells)
    {
        var text = new StringBuilder();
        AppendCsvRow(text, new[] { string.Empty }.Concat(columns));
        for (var rowIndex = 0; rowIndex < rowIds.Count; rowIndex++)
        {
            AppendCsvRow(
                text,
                new[] { rowIds[rowIndex].ToString(CultureInfo.InvariantCulture) }
                    .Concat(cells[rowIndex].Select(cell => cell.Text)));
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text.ToString());
    }

    private static void AppendCsvRow(StringBuilder target, IEnumerable<string> fields)
    {
        var first = true;
        foreach (var field in fields)
        {
            if (!first)
            {
                target.Append(',');
            }
            first = false;
            AppendCsvField(target, field);
        }
        target.AppendLine();
    }

    private static void AppendCsvField(StringBuilder target, string? value)
    {
        value ??= string.Empty;
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            target.Append(value);
            return;
        }

        target.Append('"');
        target.Append(value.Replace("\"", "\"\"", StringComparison.Ordinal));
        target.Append('"');
    }

    private sealed record LoadedPccTable(
        GalaxyMapTableSourceIdentity SourceIdentity,
        string[] ColumnNames,
        int[] RowIds,
        GalaxyMapSourceCell[][] Cells,
        byte[] ProjectedCsv);
}
