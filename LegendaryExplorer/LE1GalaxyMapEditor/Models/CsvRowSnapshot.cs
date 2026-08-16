using System.Collections.ObjectModel;

namespace LE1GalaxyMapEditor.Models;

public enum GalaxyMapCellType
{
    Text,
    Int,
    Float,
    Name,
    Null
}

/// <summary>Storage-neutral cell value retained from CSV or PCC source data.</summary>
public readonly record struct GalaxyMapSourceCell(
    GalaxyMapCellType Type,
    string Text,
    int IntValue = 0,
    float FloatValue = 0,
    string? NameValue = null)
{
    public static GalaxyMapSourceCell Csv(string? value)
        => new(GalaxyMapCellType.Text, value ?? string.Empty);

    public static GalaxyMapSourceCell Int(int value)
        => new(GalaxyMapCellType.Int, value.ToString(System.Globalization.CultureInfo.InvariantCulture), IntValue: value);

    public static GalaxyMapSourceCell Float(float value)
        => new(GalaxyMapCellType.Float, value.ToString("R", System.Globalization.CultureInfo.InvariantCulture), FloatValue: value);

    public static GalaxyMapSourceCell Name(string? value)
        => new(GalaxyMapCellType.Name, value ?? string.Empty, NameValue: value ?? string.Empty);

    public static GalaxyMapSourceCell Null()
        => new(GalaxyMapCellType.Null, string.Empty);
}

public sealed record GalaxyMapTableSourceIdentity(
    string PackagePath,
    string ExportObjectName,
    string ExportClassName);

/// <summary>
/// Preserves source cell values and types so an override can copy every untouched
/// value. The legacy name remains while BASEGAME is sourced from embedded CSV.
/// </summary>
public sealed class CsvRowSnapshot
{
    public const string RowIdColumnName = "Row ID";

    private readonly string[] _headers;
    private readonly string[] _originalValues;
    private readonly GalaxyMapSourceCell[] _originalCells;
    private readonly HashSet<string> _dirtyColumns;
    private readonly ReadOnlyCollection<string> _headerView;
    private readonly ReadOnlyCollection<string> _valueView;
    private readonly ReadOnlyCollection<GalaxyMapSourceCell> _cellView;

    public CsvRowSnapshot(
        string sourceName,
        int sourceRowNumber,
        IEnumerable<string> headers,
        IEnumerable<string> originalValues)
        : this(
            sourceName,
            sourceRowNumber,
            headers,
            originalValues.Select(GalaxyMapSourceCell.Csv),
            [])
    {
    }

    private CsvRowSnapshot(
        string sourceName,
        int sourceRowNumber,
        IEnumerable<string> headers,
        IEnumerable<GalaxyMapSourceCell> originalCells,
        IEnumerable<string> dirtyColumns)
    {
        SourceName = sourceName ?? string.Empty;
        SourceRowNumber = sourceRowNumber;
        _headers = headers.ToArray();
        _originalCells = originalCells.ToArray();
        _originalValues = _originalCells.Select(cell => cell.Text).ToArray();
        if (_headers.Length != _originalCells.Length)
        {
            throw new ArgumentException("Source headers and row values must have the same length.");
        }

        _dirtyColumns = new HashSet<string>(dirtyColumns, StringComparer.OrdinalIgnoreCase);
        _headerView = Array.AsReadOnly(_headers);
        _valueView = Array.AsReadOnly(_originalValues);
        _cellView = Array.AsReadOnly(_originalCells);
    }

    private CsvRowSnapshot(
        string sourceName,
        int sourceRowNumber,
        string[] headers,
        string[] originalValues,
        GalaxyMapSourceCell[] originalCells,
        ReadOnlyCollection<string> headerView,
        ReadOnlyCollection<string> valueView,
        ReadOnlyCollection<GalaxyMapSourceCell> cellView,
        IEnumerable<string> dirtyColumns)
    {
        SourceName = sourceName;
        SourceRowNumber = sourceRowNumber;
        _headers = headers;
        _originalValues = originalValues;
        _originalCells = originalCells;
        _dirtyColumns = new HashSet<string>(dirtyColumns, StringComparer.OrdinalIgnoreCase);
        _headerView = headerView;
        _valueView = valueView;
        _cellView = cellView;
    }

    public string SourceName { get; }
    public int SourceRowNumber { get; }
    public IReadOnlyList<string> Headers => _headerView;
    public IReadOnlyList<string> OriginalValues => _valueView;
    public IReadOnlyList<GalaxyMapSourceCell> OriginalCells => _cellView;
    public IReadOnlySet<string> DirtyColumns => _dirtyColumns;
    public bool HasChanges => _dirtyColumns.Count > 0;

    public void MarkDirty(string columnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        _dirtyColumns.Add(NormalizeColumnName(columnName));
    }

    public bool IsDirty(string columnName)
        => !string.IsNullOrWhiteSpace(columnName) && _dirtyColumns.Contains(NormalizeColumnName(columnName));

    public string? GetOriginalValue(string columnName)
    {
        var index = FindColumnIndex(columnName);
        return index >= 0 ? _originalValues[index] : null;
    }

    public GalaxyMapSourceCell? GetOriginalCell(string columnName)
    {
        var index = FindColumnIndex(columnName);
        return index >= 0 ? _originalCells[index] : null;
    }

    public CsvRowSnapshot Clone()
        => new(
            SourceName,
            SourceRowNumber,
            _headers,
            _originalValues,
            _originalCells,
            _headerView,
            _valueView,
            _cellView,
            _dirtyColumns);

    /// <summary>
    /// Creates the pristine snapshot used when materialising a same-ID override.
    /// The editor marks only the newly changed column dirty afterwards.
    /// </summary>
    public CsvRowSnapshot CloneForOverride()
        => new(
            SourceName,
            SourceRowNumber,
            _headers,
            _originalValues,
            _originalCells,
            _headerView,
            _valueView,
            _cellView,
            []);

    /// <summary>
    /// Transfers arrays created by the CSV parser into an immutable snapshot.
    /// The parser never mutates these arrays after this call, so later row clones
    /// can safely share them and allocate only their independent dirty-column set.
    /// </summary>
    internal static CsvRowSnapshot FromParsedRow(
        string sourceName,
        int sourceRowNumber,
        string[] headers,
        string[] originalValues)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(originalValues);
        if (headers.Length != originalValues.Length)
        {
            throw new ArgumentException("CSV headers and row values must have the same length.");
        }

        return new CsvRowSnapshot(
            sourceName ?? string.Empty,
            sourceRowNumber,
            headers,
            originalValues.Select(GalaxyMapSourceCell.Csv).ToArray(),
            []);
    }

    internal static CsvRowSnapshot FromPccRow(
        string sourceName,
        int sourceRowNumber,
        string[] headers,
        GalaxyMapSourceCell[] originalCells)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(originalCells);
        return new CsvRowSnapshot(
            sourceName ?? string.Empty,
            sourceRowNumber,
            headers,
            originalCells,
            []);
    }

    private int FindColumnIndex(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return -1;
        }

        var normalized = NormalizeColumnName(columnName);
        for (var index = 0; index < _headers.Length; index++)
        {
            var header = index == 0 && string.IsNullOrWhiteSpace(_headers[index])
                ? RowIdColumnName
                : _headers[index];
            if (string.Equals(header, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static string NormalizeColumnName(string columnName)
        => string.Equals(columnName.Trim(), RowIdColumnName, StringComparison.OrdinalIgnoreCase)
            ? RowIdColumnName
            : columnName.Trim();
}

/// <summary>Column and physical-source metadata for one galaxy-map table.</summary>
public sealed class CsvTableSchema
{
    private readonly ReadOnlyCollection<string> _headers;
    private readonly IReadOnlyDictionary<string, GalaxyMapCellType> _defaultCellTypes;

    public CsvTableSchema(
        GalaxyMapTable table,
        IEnumerable<string> headers,
        IReadOnlyDictionary<string, GalaxyMapCellType>? defaultCellTypes = null,
        GalaxyMapTableSourceIdentity? sourceIdentity = null)
    {
        Table = table;
        _headers = Array.AsReadOnly(headers.ToArray());
        if (_headers.Count == 0)
        {
            throw new ArgumentException("A CSV table schema must contain at least its Row ID column.", nameof(headers));
        }

        _defaultCellTypes = new Dictionary<string, GalaxyMapCellType>(
            defaultCellTypes ?? new Dictionary<string, GalaxyMapCellType>(),
            StringComparer.OrdinalIgnoreCase);
        SourceIdentity = sourceIdentity;
    }

    public GalaxyMapTable Table { get; }
    public IReadOnlyList<string> Headers => _headers;
    public IReadOnlyDictionary<string, GalaxyMapCellType> DefaultCellTypes => _defaultCellTypes;
    public GalaxyMapTableSourceIdentity? SourceIdentity { get; }

    public GalaxyMapCellType DefaultCellType(string columnName)
        => _defaultCellTypes.GetValueOrDefault(columnName, GalaxyMapCellType.Name);
}
