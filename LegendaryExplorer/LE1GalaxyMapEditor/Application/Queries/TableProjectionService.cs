using System.Globalization;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Services;

namespace LE1GalaxyMapEditor.Workflows.Queries;

public sealed record TableColumn(string Name, int Ordinal, bool IsCanonical);

public sealed record CellInstanceValue(
    object? RawValue,
    string DisplayValue,
    string ModuleTag,
    ModuleColor ModuleColor);

public sealed record MergedTableCell(
    string DisplayValue,
    object? RawValue,
    string EffectiveModuleTag,
    ModuleColor EffectiveModuleColor,
    GalaxyMapModule EffectiveModule,
    bool IsStaged,
    bool DiffersFromLowerInstance,
    IReadOnlyList<CellInstanceValue> OverrideChain);

public sealed record MergedTableRow(
    GalaxyMapRowKey Key,
    IReadOnlyDictionary<string, MergedTableCell> Cells);

public sealed record MergedTableSnapshot(
    GalaxyMapTable Table,
    IReadOnlyList<TableColumn> Columns,
    IReadOnlyList<MergedTableRow> Rows,
    long SessionRevision);

/// <summary>
/// Builds immutable table-viewer read models from the live editor session. It
/// never reparses CSV files, so staged values and effective provenance exactly
/// match the map, hierarchy, and inspector.
/// </summary>
public sealed class TableProjectionService(EditorSession session)
{
    public MergedTableSnapshot Project(GalaxyMapTable table)
    {
        var workspace = session.Workspace;
        if (workspace is null)
        {
            var document = session.Document;
            if (document is null)
            {
                return new MergedTableSnapshot(table, CanonicalColumns(table), [], session.Revision);
            }

            var referenceColumns = Columns(document, table);
            var referenceRows = EffectiveRows(document, table)
                .OrderBy(row => row.RowId)
                .Select(row => ProjectReferenceRow(row, referenceColumns))
                .ToArray();
            return new MergedTableSnapshot(table, referenceColumns, referenceRows, session.Revision);
        }

        // Writable module CSVs are accepted only when their header count, names and
        // order match the canonical 2DA schema. Do not manufacture editable columns
        // from malformed in-memory rows: Legendary Explorer could not import them.
        var columns = CanonicalColumns(table);
        var rows = EffectiveRows(workspace.EffectiveDocument, table)
            .OrderBy(row => row.RowId)
            .Select(row => ProjectRow(workspace, row, columns))
            .ToArray();
        return new MergedTableSnapshot(table, columns, rows, session.Revision);
    }

    private MergedTableRow ProjectRow(
        GalaxyMapWorkspace workspace,
        GalaxyMapRow effectiveRow,
        IReadOnlyList<TableColumn> columns)
    {
        var chain = workspace.GetOverrideChain(effectiveRow.Key);
        var winningModule = chain.LastOrDefault()?.Origin?.Module ??
                            effectiveRow.Origin?.Module ??
                            GalaxyMapModule.BaseGame;
        var tableIsStaged = session.Changes.DirtyTables.TryGetValue(winningModule.Tag, out var dirtyTables) &&
                            dirtyTables.Contains(effectiveRow.Table);
        var cells = new Dictionary<string, MergedTableCell>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            var instances = chain.Select(row =>
            {
                var raw = GalaxyMapRowValueAccessor.GetValue(row, column.Name);
                var module = row.Origin?.Module ?? GalaxyMapModule.BaseGame;
                return new CellInstanceValue(raw, Display(raw), module.Tag, module.Color);
            }).ToArray();
            var rawValue = GalaxyMapRowValueAccessor.GetValue(effectiveRow, column.Name);
            var differs = instances.Length > 1 && !ValuesEqual(
                instances[^1].RawValue,
                instances[^2].RawValue);
            cells[column.Name] = new MergedTableCell(
                Display(rawValue),
                rawValue,
                winningModule.Tag,
                winningModule.Color,
                winningModule,
                tableIsStaged && effectiveRow.CsvSnapshot?.IsDirty(column.Name) == true,
                differs,
                instances);
        }

        return new MergedTableRow(effectiveRow.Key, cells);
    }

    private static MergedTableRow ProjectReferenceRow(
        GalaxyMapRow row,
        IReadOnlyList<TableColumn> columns)
    {
        var module = row.Origin?.Module ?? GalaxyMapModule.BaseGame;
        var cells = columns.ToDictionary(
            column => column.Name,
            column =>
            {
                var raw = GalaxyMapRowValueAccessor.GetValue(row, column.Name);
                var instance = new CellInstanceValue(raw, Display(raw), module.Tag, module.Color);
                return new MergedTableCell(
                    instance.DisplayValue,
                    raw,
                    module.Tag,
                    module.Color,
                    module,
                    IsStaged: false,
                    DiffersFromLowerInstance: false,
                    [instance]);
            },
            StringComparer.OrdinalIgnoreCase);
        return new MergedTableRow(row.Key, cells);
    }

    private static IReadOnlyList<TableColumn> Columns(GalaxyMapDocument document, GalaxyMapTable table)
    {
        var canonical = CanonicalColumns(table);
        var names = canonical.Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = canonical.ToList();
        foreach (var row in EffectiveRows(document, table))
        {
            var headers = row.CsvSnapshot?.Headers ?? [];
            foreach (var header in headers.Concat(row.ExtraFieldOrder))
            {
                var name = string.IsNullOrWhiteSpace(header) ? CsvRowSnapshot.RowIdColumnName : header;
                if (names.Add(name))
                {
                    result.Add(new TableColumn(name, result.Count, IsCanonical: false));
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<TableColumn> CanonicalColumns(GalaxyMapTable table)
        => CsvGalaxyMapLoader.GetCanonicalSchema(table).Headers
            .Select((header, index) => new TableColumn(
                index == 0 && string.IsNullOrWhiteSpace(header) ? CsvRowSnapshot.RowIdColumnName : header,
                index,
                IsCanonical: true))
            .ToArray();

    private static IEnumerable<GalaxyMapRow> EffectiveRows(GalaxyMapDocument document, GalaxyMapTable table)
        => table switch
        {
            GalaxyMapTable.Cluster => document.Clusters,
            GalaxyMapTable.System => document.Systems,
            GalaxyMapTable.Planet => document.Planets,
            GalaxyMapTable.PlotPlanet => document.PlotPlanets,
            GalaxyMapTable.Map => document.Maps,
            GalaxyMapTable.Relay => document.Relays,
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, null)
        };

    private static bool ValuesEqual(object? left, object? right)
        => left is double leftNumber && right is double rightNumber
            ? leftNumber.Equals(rightNumber)
            : Equals(left, right);

    private static string Display(object? value) => value switch
    {
        null => string.Empty,
        double number => GalaxyMapNumber.FormatDisplay(number),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };
}
