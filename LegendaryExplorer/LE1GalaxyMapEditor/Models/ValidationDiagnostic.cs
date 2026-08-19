namespace LE1GalaxyMapEditor.Models;

public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// Identifies a managed identity cell that may be edited while a diagnostic is
/// present. Validation owns this metadata because it knows which manual repairs
/// can resolve an ambiguous relationship; UI surfaces only consume the result.
/// </summary>
public sealed record ValidationRepairTarget(
    GalaxyMapRowKey Key,
    string ColumnName,
    string ModuleTag = "")
{
    public static ValidationRepairTarget For(GalaxyMapRow row, string columnName)
        => new(row.Key, columnName, row.Origin?.ModuleTag ?? GalaxyMapModule.BaseGameTag);
}

/// <summary>
/// A stable, navigable validation result. Codes are intended for tests and future
/// suppression support; the message remains the human-readable explanation.
/// </summary>
public sealed record ValidationDiagnostic(
    string Code,
    ValidationSeverity Severity,
    string Message,
    string ModuleTag = "",
    string TableName = "",
    int? RowId = null,
    string ColumnName = "",
    int? CsvLine = null,
    IReadOnlyList<ValidationRepairTarget>? RepairTargets = null)
{
    public bool IsBlocking => Severity == ValidationSeverity.Error;
    public IReadOnlyList<ValidationRepairTarget> Repairs => RepairTargets ?? [];

    public string Location
    {
        get
        {
            var tableAndRow = string.IsNullOrWhiteSpace(TableName)
                ? string.Empty
                : RowId is { } rowId ? $"{TableName} row {rowId}" : TableName;
            var column = string.IsNullOrWhiteSpace(ColumnName) ? string.Empty : ColumnName;
            return string.Join(", ", new[] { ModuleTag, tableAndRow, column }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
        }
    }
}
