using System.Globalization;
using System.IO;
using System.Text;
using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Services;

/// <summary>
/// Writes only the active module's physical Legendary Explorer partial CSVs.
/// Lower layers and the deployed BASEGAME source are never accepted as targets.
/// </summary>
public sealed class GalaxyMapCsvWriter
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public void WriteTable(GalaxyMapLayer layer, GalaxyMapTable table)
    {
        var folder = RequireWritableModuleFolder(layer);
        var schema = CsvGalaxyMapLoader.GetCanonicalSchema(table);
        var rows = layer.Rows(table).OrderBy(row => row.RowId).ToArray();
        var duplicate = rows.GroupBy(row => row.RowId).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"The active {table} layer contains duplicate row ID {duplicate.Key} and cannot be written.");
        }

        var serializedRows = rows.Select(row => SerializeRow(row, schema)).ToArray();
        var text = new StringBuilder();
        AppendCsvRecord(text, schema.Headers);
        foreach (var values in serializedRows)
        {
            AppendCsvRecord(text, values);
        }

        var body = Utf8WithBom.GetBytes(text.ToString());
        var preamble = Utf8WithBom.GetPreamble();
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);

        var fileName = PartialFileName(table);
        AtomicFileWriter.Write(Path.Combine(folder, fileName), bytes);

        layer.SetSchema(new CsvTableSchema(table, schema.Headers));
        layer.SetSourceRowOrder(table, rows.Select(row => row.RowId));
        for (var index = 0; index < rows.Length; index++)
        {
            rows[index].CsvSnapshot = new CsvRowSnapshot(
                fileName,
                sourceRowNumber: index + 2,
                schema.Headers,
                serializedRows[index]);
        }
    }

    /// <summary>Writes each requested table once. Every individual file replacement is atomic.</summary>
    public void WriteTables(
        GalaxyMapLayer layer,
        IEnumerable<GalaxyMapTable> tables,
        Action<GalaxyMapTable>? tableWritten = null)
    {
        ArgumentNullException.ThrowIfNull(tables);
        foreach (var table in tables.Distinct().OrderBy(DependencySafeWriteOrder))
        {
            WriteTable(layer, table);
            tableWritten?.Invoke(table);
        }
    }

    private static string[] SerializeRow(GalaxyMapRow row, CsvTableSchema schema)
    {
        if (row.Table != schema.Table)
        {
            throw new InvalidOperationException(
                $"A {row.Table} row cannot be written with the {schema.Table} schema.");
        }

        var values = new string[schema.Headers.Count];
        for (var index = 0; index < schema.Headers.Count; index++)
        {
            var header = schema.Headers[index];
            if (index == 0)
            {
                var originalRowId = row.CsvSnapshot?.GetOriginalValue(CsvRowSnapshot.RowIdColumnName);
                values[index] = row.CsvSnapshot is not null &&
                                !row.CsvSnapshot.IsDirty(CsvRowSnapshot.RowIdColumnName) &&
                                int.TryParse(originalRowId, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                    out var parsedRowId) &&
                                parsedRowId == row.RowId
                    ? originalRowId!
                    : row.RowId.ToString(CultureInfo.InvariantCulture);
                continue;
            }

            var original = row.CsvSnapshot?.GetOriginalValue(header);
            values[index] = row.CsvSnapshot is not null &&
                            !row.CsvSnapshot.IsDirty(header) &&
                            original is not null
                ? original
                : GalaxyMapRowValueAccessor.GetCsvToken(row, header);
        }

        return values;
    }

    private static string RequireWritableModuleFolder(GalaxyMapLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var module = layer.Module;
        if (module.IsBaseGame || module.IsReadOnly || module.FolderPath is null)
        {
            throw new InvalidOperationException(
                "CSV output is only permitted for a writable, non-BASEGAME module with its own folder.");
        }

        var folder = Path.GetFullPath(module.FolderPath);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string PartialFileName(GalaxyMapTable table) => $"GalaxyMap_{table}_part.csv";

    private static int DependencySafeWriteOrder(GalaxyMapTable table) => table switch
    {
        // Write referenced rows before the rows that point at them. This does not
        // make several files one transaction, but leaves only harmless orphans if
        // the process is interrupted between two individually atomic replacements.
        GalaxyMapTable.Cluster => 0,
        GalaxyMapTable.System => 1,
        GalaxyMapTable.Map => 2,
        GalaxyMapTable.Planet => 3,
        GalaxyMapTable.PlotPlanet => 4,
        GalaxyMapTable.Relay => 5,
        _ => 6
    };

    private static void AppendCsvRecord(StringBuilder output, IEnumerable<string> values)
    {
        output.AppendJoin(',', values.Select(Escape));
        output.Append("\r\n");
    }

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

}
