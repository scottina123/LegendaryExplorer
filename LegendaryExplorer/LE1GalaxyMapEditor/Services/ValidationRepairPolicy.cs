using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Services;

/// <summary>
/// Central policy for temporarily unlocking managed identity cells. Ordinary
/// editability remains a UI/session concern; this policy only answers whether
/// current validation explicitly offers a manual identity repair.
/// </summary>
public sealed class ValidationRepairPolicy(Func<IReadOnlyList<ValidationDiagnostic>> diagnostics)
{
    public bool CanRepair(GalaxyMapRow row, string columnName)
        => CanRepair(row.Key, row.Origin?.Module ?? GalaxyMapModule.BaseGame, columnName);

    public bool CanRepair(GalaxyMapRowKey key, GalaxyMapModule module, string columnName)
    {
        if (!IsManagedIdentity(key.Table, columnName))
        {
            return false;
        }

        // Re-keying an inherited row cannot remove the source identity; it would
        // merely create a second override row and leave the reported fault intact.
        if (string.Equals(columnName, CsvRowSnapshot.RowIdColumnName, StringComparison.OrdinalIgnoreCase) &&
            (module.IsBaseGame || module.IsReadOnly))
        {
            return false;
        }

        return diagnostics().SelectMany(diagnostic => diagnostic.Repairs).Any(target =>
            target.Key == key &&
            string.Equals(target.ColumnName, columnName, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(target.ModuleTag) ||
             string.Equals(target.ModuleTag, module.Tag, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool IsManagedIdentity(GalaxyMapRow row, string columnName)
        => IsManagedIdentity(row.Table, columnName);

    public static bool IsManagedIdentity(GalaxyMapTable table, string columnName)
        => string.Equals(columnName, CsvRowSnapshot.RowIdColumnName, StringComparison.OrdinalIgnoreCase) ||
           table == GalaxyMapTable.Planet &&
           string.Equals(columnName, nameof(Planet.ActiveWorld), StringComparison.OrdinalIgnoreCase);
}
