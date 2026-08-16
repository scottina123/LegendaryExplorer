using System.Globalization;
using System.IO;
using LE1GalaxyMapEditor.Models;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Classes;

namespace LE1GalaxyMapEditor.Services;

/// <summary>Commits all requested tables in one PCC replacement transaction.</summary>
public sealed class PccGalaxyMapWriter
{
    private const string TableContainerName = "BIOG_2DA_GalaxyMap_X";
    private readonly PccGalaxyMapLoader _loader;

    public PccGalaxyMapWriter(PccGalaxyMapLoader? loader = null)
    {
        _loader = loader ?? new PccGalaxyMapLoader();
    }

    public void WriteTables(GalaxyMapLayer layer, IEnumerable<GalaxyMapTable> tables)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(tables);
        var requestedTables = tables.Distinct().ToArray();
        if (requestedTables.Length == 0)
        {
            return;
        }

        if (layer.Module.IsBaseGame || layer.Module.IsReadOnly)
        {
            throw new InvalidOperationException("PCC output is only permitted for a writable module layer.");
        }
        if (layer.SourcePackagePath is null || layer.SourcePackageFingerprint is null)
        {
            throw new InvalidOperationException("The module layer is not linked to a loaded PCC.");
        }

        var packagePath = Path.GetFullPath(layer.SourcePackagePath);
        EnsureUnchanged(packagePath, layer.SourcePackageFingerprint);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(packagePath)!,
            $".{Path.GetFileNameWithoutExtension(packagePath)}.{Guid.NewGuid():N}.commit.pcc");
        var writtenTables = new Dictionary<GalaxyMapTable, SerializedTable>();

        try
        {
            using (var package = MEPackageHandler.OpenLE1Package(packagePath, forceLoadFromDisk: true))
            {
                foreach (var table in requestedTables)
                {
                    var schema = layer.GetSchema(table);
                    ExportEntry export;
                    GalaxyMapTableSourceIdentity identity;
                    if (schema?.SourceIdentity is { } existingIdentity)
                    {
                        identity = existingIdentity;
                        export = ResolveExport(package, identity);
                    }
                    else
                    {
                        export = ImportTemplateExport(package, table);
                        identity = new GalaxyMapTableSourceIdentity(
                            packagePath,
                            export.ObjectName.Name,
                            export.ClassName);
                        var canonical = CsvGalaxyMapLoader.GetCanonicalSchema(table);
                        schema = new CsvTableSchema(
                            table,
                            canonical.Headers,
                            canonical.DefaultCellTypes,
                            identity);
                    }
                    var source = new Bio2DA(export);
                    ValidateColumns(schema, source, identity);
                    var serialized = SerializeTable(layer, table, schema, source, package);
                    serialized.Table.Write2DAToExport(export);
                    writtenTables[table] = serialized;
                }

                package.Save(temporaryPath);
            }

            if (!File.Exists(temporaryPath))
            {
                throw new IOException("Legendary Explorer Core did not create the temporary PCC.");
            }

            var verifiedLayer = _loader.Load(temporaryPath, layer.Module);
            VerifyWrittenTables(verifiedLayer, writtenTables);
            EnsureUnchanged(packagePath, layer.SourcePackageFingerprint);
            File.Replace(temporaryPath, packagePath, destinationBackupFileName: null, ignoreMetadataErrors: true);

            var committedFingerprint = GalaxyMapPackageFingerprint.Capture(packagePath);
            ApplyCommittedSnapshots(layer, verifiedLayer, writtenTables.Keys, packagePath, committedFingerprint);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static ExportEntry ImportTemplateExport(IMEPackage destination, GalaxyMapTable table)
    {
        var exportName = PccGalaxyMapLoader.SupportedExports[table];
        var templatePath = ApplicationResourcePaths.GetDataFilePath(
            GalaxyMapTemplatePackageService.TemplateFileName);
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException(
                $"Cannot create {exportName} because the galaxy-map PCC template is missing.",
                templatePath);
        }

        using var template = MEPackageHandler.OpenLE1Package(templatePath, forceLoadFromDisk: true);
        var matches = template.Exports.Where(candidate =>
            !candidate.IsDefaultObject &&
            string.Equals(candidate.ClassName, "Bio2DANumberedRows", StringComparison.Ordinal) &&
            string.Equals(candidate.ObjectName.Name, exportName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"The PCC template must contain exactly one export named '{exportName}'.");
        }

        var containers = destination.Exports.Where(candidate =>
            !candidate.IsDefaultObject &&
            string.Equals(candidate.ClassName, "Package", StringComparison.Ordinal) &&
            string.Equals(candidate.ObjectName.Name, TableContainerName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (containers.Length > 1)
        {
            throw new InvalidOperationException(
                $"The PCC contains multiple package exports named '{TableContainerName}'.");
        }
        var container = containers.SingleOrDefault()
            ?? destination.CreatePackageExport(new NameReference(TableContainerName));

        EntryImporter.ImportAndRelinkEntries(
            EntryImporter.PortingOption.CloneAllDependencies,
            matches[0],
            destination,
            targetLinkEntry: container,
            shouldRelink: true,
            rop: new RelinkerOptionsPackage { ImportExportDependencies = true },
            newEntry: out var imported);
        if (imported is not ExportEntry importedExport ||
            !string.Equals(importedExport.ClassName, "Bio2DANumberedRows", StringComparison.Ordinal) ||
            !string.Equals(importedExport.ObjectName.Name, exportName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Legendary Explorer Core did not create the expected export '{exportName}'.");
        }
        if (!ReferenceEquals(importedExport.Parent, container))
        {
            throw new InvalidOperationException(
                $"Legendary Explorer Core did not place '{exportName}' under '{TableContainerName}'.");
        }

        return importedExport;
    }

    private static SerializedTable SerializeTable(
        GalaxyMapLayer layer,
        GalaxyMapTable table,
        CsvTableSchema schema,
        Bio2DA source,
        IMEPackage package)
    {
        var rows = OrderedRows(layer, table);
        var duplicate = rows.GroupBy(row => row.RowId).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"The active {table} layer contains duplicate row ID {duplicate.Key}.");
        }

        var target = new Bio2DA
        {
            Export = source.Export,
            // Null cells are omitted from Bio2DA binary data. Indexed storage is
            // therefore required to preserve their positions instead of shifting
            // every subsequent value left into the blank column.
            IsIndexed = true
        };
        foreach (var column in source.ColumnNames)
        {
            target.AddColumn(column);
        }

        var serializedRows = new Dictionary<int, GalaxyMapSourceCell[]>();
        foreach (var row in rows)
        {
            var rowIndex = target.AddRow(row.RowId.ToString(CultureInfo.InvariantCulture));
            var cells = new GalaxyMapSourceCell[source.ColumnCount];
            for (var columnIndex = 0; columnIndex < source.ColumnCount; columnIndex++)
            {
                var column = source.ColumnNames[columnIndex];
                var cell = SerializeCell(row, table, column, schema);
                cells[columnIndex] = cell;
                target[rowIndex, columnIndex] = ToBio2DaCell(cell, package);
            }
            serializedRows.Add(row.RowId, cells);
        }

        return new SerializedTable(target, rows.Select(row => row.RowId).ToArray(), serializedRows);
    }

    private static GalaxyMapRow[] OrderedRows(GalaxyMapLayer layer, GalaxyMapTable table)
    {
        var rows = layer.Rows(table).ToDictionary(row => row.RowId);
        var result = new List<GalaxyMapRow>(rows.Count);
        foreach (var rowId in layer.GetSourceRowOrder(table))
        {
            if (rows.Remove(rowId, out var row))
            {
                result.Add(row);
            }
        }
        result.AddRange(layer.Rows(table).Where(row => rows.ContainsKey(row.RowId)));
        return result.ToArray();
    }

    private static GalaxyMapSourceCell SerializeCell(
        GalaxyMapRow row,
        GalaxyMapTable table,
        string column,
        CsvTableSchema schema)
    {
        var snapshot = row.CsvSnapshot;
        var original = snapshot?.GetOriginalCell(column);
        if (snapshot is not null && !snapshot.IsDirty(column) &&
            original is { } untouched && untouched.Type != GalaxyMapCellType.Text)
        {
            return untouched;
        }

        var token = snapshot is not null && !snapshot.IsDirty(column) && original is { } textCell
            ? textCell.Text
            : GalaxyMapRowValueAccessor.GetCsvToken(row, column);
        if (string.IsNullOrEmpty(token))
        {
            return GalaxyMapSourceCell.Null();
        }

        var type = original is { Type: not GalaxyMapCellType.Null and not GalaxyMapCellType.Text } typed
            ? typed.Type
            : schema.DefaultCellType(column);
        return type switch
        {
            GalaxyMapCellType.Int or GalaxyMapCellType.Float =>
                SerializeNumeric(token, table, column, row.RowId),
            GalaxyMapCellType.Name => GalaxyMapSourceCell.Name(token),
            GalaxyMapCellType.Null => GalaxyMapSourceCell.Null(),
            _ => throw new InvalidOperationException(
                $"No PCC cell type is available for {table} row {row.RowId}, column '{column}'.")
        };
    }

    private static GalaxyMapSourceCell SerializeNumeric(
        string token,
        GalaxyMapTable table,
        string column,
        int rowId)
    {
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return GalaxyMapSourceCell.Int(integer);
        }
        if (string.Equals(column, "RingColor", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned))
        {
            return GalaxyMapSourceCell.Int(unchecked((int)unsigned));
        }
        if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
            float.IsFinite(number))
        {
            return GalaxyMapSourceCell.Float(number);
        }

        throw new InvalidOperationException(
            $"{table} row {rowId}, column '{column}' must be a finite PCC numeric value; found '{token}'.");
    }

    private static Bio2DACell ToBio2DaCell(GalaxyMapSourceCell cell, IMEPackage package)
        => cell.Type switch
        {
            GalaxyMapCellType.Int => new Bio2DACell(cell.IntValue) { package = package },
            GalaxyMapCellType.Float => new Bio2DACell(cell.FloatValue) { package = package },
            GalaxyMapCellType.Name => new Bio2DACell(
                NameReference.FromInstancedString(cell.NameValue ?? cell.Text), package) { package = package },
            GalaxyMapCellType.Null => new Bio2DACell { package = package },
            _ => throw new InvalidOperationException("CSV text must be converted before PCC serialization.")
        };

    private static ExportEntry ResolveExport(IMEPackage package, GalaxyMapTableSourceIdentity identity)
    {
        var matches = package.Exports.Where(export =>
            !export.IsDefaultObject &&
            string.Equals(export.ClassName, identity.ExportClassName, StringComparison.Ordinal) &&
            string.Equals(export.ObjectName.Name, identity.ExportObjectName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                $"The PCC no longer contains export '{identity.ExportObjectName}'."),
            _ => throw new InvalidOperationException(
                $"The PCC now contains multiple exports named '{identity.ExportObjectName}'.")
        };
    }

    private static void ValidateColumns(
        CsvTableSchema schema,
        Bio2DA source,
        GalaxyMapTableSourceIdentity identity)
    {
        var expected = schema.Headers.Skip(1).ToArray();
        if (!expected.SequenceEqual(source.ColumnNames, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The columns in export '{identity.ExportObjectName}' changed after the module was loaded.");
        }
    }

    private static void VerifyWrittenTables(
        GalaxyMapLayer verifiedLayer,
        IReadOnlyDictionary<GalaxyMapTable, SerializedTable> expected)
    {
        foreach (var pair in expected)
        {
            var actualRows = verifiedLayer.Rows(pair.Key).ToDictionary(row => row.RowId);
            if (!pair.Value.RowIds.SequenceEqual(verifiedLayer.GetSourceRowOrder(pair.Key)) ||
                actualRows.Count != pair.Value.RowIds.Length)
            {
                throw new IOException($"Temporary PCC verification failed for the {pair.Key} row order.");
            }

            foreach (var rowId in pair.Value.RowIds)
            {
                if (!actualRows.TryGetValue(rowId, out var row) || row.CsvSnapshot is null)
                {
                    throw new IOException(
                        $"Temporary PCC verification could not find {pair.Key} row {rowId}.");
                }
                var expectedCells = pair.Value.Cells[rowId];
                var actualCells = row.CsvSnapshot.OriginalCells.Skip(1).ToArray();
                for (var index = 0; index < expectedCells.Length; index++)
                {
                    if (expectedCells[index] == actualCells[index])
                    {
                        continue;
                    }

                    var column = verifiedLayer.GetSchema(pair.Key)?.Headers.ElementAtOrDefault(index + 1)
                        ?? $"column {index + 1}";
                    throw new IOException(
                        $"Temporary PCC verification found a changed cell in {pair.Key} row {rowId}, " +
                        $"column '{column}': wrote {expectedCells[index].Type} '{expectedCells[index].Text}', " +
                        $"read {actualCells[index].Type} '{actualCells[index].Text}'.");
                }
            }
        }
    }

    private static void ApplyCommittedSnapshots(
        GalaxyMapLayer layer,
        GalaxyMapLayer verifiedLayer,
        IEnumerable<GalaxyMapTable> committedTables,
        string packagePath,
        GalaxyMapPackageFingerprint fingerprint)
    {
        foreach (var table in committedTables)
        {
            var verifiedRows = verifiedLayer.Rows(table).ToDictionary(row => row.RowId);
            foreach (var row in layer.Rows(table))
            {
                row.CsvSnapshot = verifiedRows[row.RowId].CsvSnapshot?.Clone();
            }
            var verifiedSchema = verifiedLayer.GetSchema(table)!;
            var identity = verifiedSchema.SourceIdentity is null
                ? null
                : verifiedSchema.SourceIdentity with { PackagePath = packagePath };
            layer.SetSchema(new CsvTableSchema(
                table,
                verifiedSchema.Headers,
                verifiedSchema.DefaultCellTypes,
                identity));
            layer.SetSourceRowOrder(table, verifiedLayer.GetSourceRowOrder(table));
        }
        layer.SetPackageSource(packagePath, fingerprint);
    }

    private static void EnsureUnchanged(
        string packagePath,
        GalaxyMapPackageFingerprint expectedFingerprint)
    {
        var actual = GalaxyMapPackageFingerprint.Capture(packagePath);
        if (actual != expectedFingerprint)
        {
            throw new InvalidOperationException(
                $"'{packagePath}' changed outside the editor. Refresh the module before committing.");
        }
    }

    private sealed record SerializedTable(
        Bio2DA Table,
        int[] RowIds,
        IReadOnlyDictionary<int, GalaxyMapSourceCell[]> Cells);
}
