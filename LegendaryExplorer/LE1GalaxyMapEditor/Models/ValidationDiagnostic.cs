namespace LE1GalaxyMapEditor.Models;

public enum ValidationSeverity
{
    Info,
    Warning,
    Error
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
    int? CsvLine = null)
{
    public bool IsBlocking => Severity == ValidationSeverity.Error;

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
