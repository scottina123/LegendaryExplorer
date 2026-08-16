using System.IO;
using LE1GalaxyMapEditor.Models;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Classes;

namespace LE1GalaxyMapEditor.Services;

/// <summary>Creates a galaxy-map PCC containing the selected row-empty partial tables.</summary>
public sealed class GalaxyMapTemplatePackageService
{
    public const string TemplateFileName = "GXM_2DA_Template.pcc";

    public string Create(
        string destinationPath,
        IEnumerable<GalaxyMapTable>? includedTables = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var included = includedTables is null
            ? Enum.GetValues<GalaxyMapTable>().ToHashSet()
            : includedTables.ToHashSet();
        var destination = Path.GetFullPath(destinationPath);
        if (File.Exists(destination))
        {
            throw new IOException($"A file already exists at '{destination}'.");
        }

        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("The destination must have a parent directory.", nameof(destinationPath));
        if (!Directory.Exists(destinationDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The destination directory does not exist: {destinationDirectory}");
        }

        var templatePath = ApplicationResourcePaths.GetDataFilePath(TemplateFileName);
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("The galaxy-map PCC template is missing.", templatePath);
        }

        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileNameWithoutExtension(destination)}.{Guid.NewGuid():N}.tmp.pcc");
        try
        {
            using (var package = MEPackageHandler.OpenLE1Package(templatePath, forceLoadFromDisk: true))
            {
                var excludedExports = new List<ExportEntry>();
                foreach (var pair in PccGalaxyMapLoader.SupportedExports)
                {
                    var matches = package.Exports.Where(export =>
                        !export.IsDefaultObject &&
                        string.Equals(export.ClassName, "Bio2DANumberedRows", StringComparison.Ordinal) &&
                        string.Equals(export.ObjectName.Name, pair.Value, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (matches.Length != 1)
                    {
                        throw new GalaxyMapLoadException(
                            $"The PCC template must contain exactly one supported export named '{pair.Value}'.");
                    }

                    if (!included.Contains(pair.Key))
                    {
                        excludedExports.Add(matches[0]);
                        continue;
                    }

                    var schemaTable = new Bio2DA(matches[0]);
                    var blankTable = new Bio2DA
                    {
                        Export = matches[0],
                        IsIndexed = schemaTable.IsIndexed
                    };
                    foreach (var column in schemaTable.ColumnNames)
                    {
                        blankTable.AddColumn(column);
                    }
                    blankTable.Write2DAToExport(matches[0]);
                }

                EntryPruner.TrashEntries(package, excludedExports);

                package.Save(temporaryPath);
            }

            ValidateBlankPackage(temporaryPath, included);
            File.Move(temporaryPath, destination);
            return destination;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ValidateBlankPackage(
        string packagePath,
        IReadOnlySet<GalaxyMapTable> includedTables)
    {
        using var package = MEPackageHandler.OpenLE1Package(packagePath, forceLoadFromDisk: true);
        foreach (var pair in PccGalaxyMapLoader.SupportedExports)
        {
            var matches = package.Exports.Where(export =>
                !export.IsDefaultObject &&
                string.Equals(export.ClassName, "Bio2DANumberedRows", StringComparison.Ordinal) &&
                string.Equals(export.ObjectName.Name, pair.Value, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var isValid = includedTables.Contains(pair.Key)
                ? matches.Length == 1 && new Bio2DA(matches[0]).RowCount == 0
                : matches.Length == 0;
            if (!isValid)
            {
                throw new GalaxyMapLoadException(
                    $"The created PCC failed validation for export '{pair.Value}'.");
            }
        }
    }
}
