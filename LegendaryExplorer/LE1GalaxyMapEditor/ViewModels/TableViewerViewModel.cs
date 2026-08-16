using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Services;
using LE1GalaxyMapEditor.Workflows;
using LE1GalaxyMapEditor.Workflows.Queries;
using LegendaryExplorerCore.Packages;

namespace LE1GalaxyMapEditor.ViewModels;

public sealed class TableViewerViewModel : ObservableObject
{
    private static readonly GalaxyMapTable[] TabOrder =
    [
        GalaxyMapTable.Cluster,
        GalaxyMapTable.Relay,
        GalaxyMapTable.System,
        GalaxyMapTable.Planet,
        GalaxyMapTable.PlotPlanet,
        GalaxyMapTable.Map
    ];

    private readonly TableProjectionService _projection;
    private readonly Func<GalaxyMapRowKey, string, string, WorkflowResult> _applyEdit;
    private readonly Func<bool> _canEdit;
    private readonly GalaxyMapTlkService? _tlk;
    private readonly Func<GalaxyMapModule, MELocalization> _localeForModule;
    private IReadOnlyList<TableColumn> _columns = [];
    private GalaxyMapTable _selectedTable = GalaxyMapTable.Cluster;
    private bool _needsRefresh = true;
    private bool _isEditingAvailable;
    private long _sessionRevision;

    public TableViewerViewModel(
        TableProjectionService projection,
        Func<GalaxyMapRowKey, string, string, WorkflowResult> applyEdit,
        Func<bool> canEdit,
        GalaxyMapTlkService? tlk = null,
        Func<GalaxyMapModule, MELocalization>? localeForModule = null)
    {
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _applyEdit = applyEdit ?? throw new ArgumentNullException(nameof(applyEdit));
        _canEdit = canEdit ?? throw new ArgumentNullException(nameof(canEdit));
        _tlk = tlk;
        _localeForModule = localeForModule ?? (module => module.TlkLocale);
        Tabs = new ObservableCollection<TableTabViewModel>(TabOrder.Select(table =>
            new TableTabViewModel(table, () => SelectedTable = table)));
        UpdateTabSelection();
    }

    public ObservableCollection<TableTabViewModel> Tabs { get; }
    public BulkObservableCollection<TableRowViewModel> Rows { get; } = [];
    public IReadOnlyList<TableColumn> Columns
    {
        get => _columns;
        private set
        {
            _columns = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ColumnCount));
        }
    }

    public GalaxyMapTable SelectedTable
    {
        get => _selectedTable;
        set
        {
            if (!SetProperty(ref _selectedTable, value))
            {
                return;
            }

            UpdateTabSelection();
            Invalidate();
            RefreshIfNeeded();
            OnPropertyChanged(nameof(Title));
        }
    }

    public int RowCount => Rows.Count;
    public int ColumnCount => Columns.Count;
    public string Title => $"GalaxyMap_{SelectedTable}";
    public long SessionRevision => _sessionRevision;
    public bool IsEditingAvailable
    {
        get => _isEditingAvailable;
        private set
        {
            if (SetProperty(ref _isEditingAvailable, value))
            {
                OnPropertyChanged(nameof(EditingHint));
            }
        }
    }

    public string EditingHint => IsEditingAvailable
        ? "Double-click or press F2 to edit • Enter/Tab applies • Esc cancels • Shift + wheel scrolls horizontally"
        : "Read-only preview • Create or open a writable module to edit • Shift + wheel scrolls horizontally";

    public void Invalidate(ChangeImpact? impact = null)
    {
        if (impact is null || impact.IsStructural || impact.Tables.Contains(SelectedTable))
        {
            _needsRefresh = true;
        }
    }

    public void RefreshIfNeeded(bool force = false)
    {
        if (!force && !_needsRefresh)
        {
            return;
        }

        var snapshot = _projection.Project(SelectedTable);
        // Column generation queries this value synchronously when Columns changes.
        // Publish edit availability first so writable columns are not created read-only.
        var canEdit = _canEdit();
        var editingAvailabilityChanged = IsEditingAvailable != canEdit;
        IsEditingAvailable = canEdit;
        if (editingAvailabilityChanged || !Columns.SequenceEqual(snapshot.Columns))
        {
            Columns = snapshot.Columns;
        }
        Rows.ReplaceAll(snapshot.Rows.Select(row => ProjectRow(row, snapshot.Columns)));
        _sessionRevision = snapshot.SessionRevision;
        _needsRefresh = false;
        OnPropertyChanged(nameof(RowCount));
        OnPropertyChanged(nameof(SessionRevision));
        OnPropertyChanged(nameof(EditingHint));
    }

    public bool IsColumnReadOnly(TableColumn column)
        => !IsEditingAvailable ||
           string.Equals(column.Name, CsvRowSnapshot.RowIdColumnName, StringComparison.OrdinalIgnoreCase) ||
           SelectedTable == GalaxyMapTable.Planet &&
           string.Equals(column.Name, "ActiveWorld", StringComparison.OrdinalIgnoreCase);

    public WorkflowResult CommitCellEdit(TableRowViewModel row, int columnIndex, string token)
    {
        if (columnIndex < 0 || columnIndex >= Columns.Count || columnIndex >= row.Cells.Count)
        {
            return WorkflowResult.Failure("The selected table cell is no longer available.", row.Key);
        }

        var column = Columns[columnIndex];
        var cell = row.Cells[columnIndex];
        if (IsColumnReadOnly(column))
        {
            var message = !IsEditingAvailable
                ? "Create or open a writable module before editing table cells."
                : $"{column.Name} is managed by the editor and is read-only here.";
            cell.SetValidationError(message);
            return WorkflowResult.Failure(message, row.Key);
        }

        var result = _applyEdit(row.Key, column.Name, token);
        cell.SetValidationError(result.Succeeded ? null : result.Error ?? result.Message);
        if (result.Succeeded)
        {
            Invalidate(result.Impact);
        }
        return result;
    }

    public void CancelCellEdit(TableRowViewModel row, int columnIndex)
    {
        if (columnIndex < 0 || columnIndex >= row.Cells.Count)
        {
            return;
        }

        var cell = row.Cells[columnIndex];
        cell.EditValue = cell.DisplayValue;
        cell.SetValidationError(null);
    }

    private TableRowViewModel ProjectRow(MergedTableRow row, IReadOnlyList<TableColumn> columns)
        => new(
            row.Key,
            columns.Select(column =>
            {
                var cell = row.Cells[column.Name];
                return new TableCellViewModel(
                    cell.DisplayValue,
                    cell.EffectiveModuleTag,
                    cell.EffectiveModuleColor,
                    cell.IsStaged,
                    cell.DiffersFromLowerInstance,
                    cell.OverrideChain.Count,
                    ResolveTlkToolTip(column, cell, cell.EffectiveModule));
            }).ToArray());

    private string? ResolveTlkToolTip(TableColumn column, MergedTableCell cell, GalaxyMapModule module)
    {
        if (_tlk is null || !GalaxyMapStrRefSchema.IsStrRef(SelectedTable, column.Name) ||
            !int.TryParse(cell.DisplayValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringRef) ||
            stringRef < 0)
        {
            return null;
        }

        var locale = _localeForModule(module);
        var lookup = _tlk.Find(locale, stringRef);
        return lookup is null
            ? $"No {locale} TLK string was found for StrRef {stringRef}."
            : $"Effective value supplied by {Path.GetFileName(lookup.SourcePackage)}.\n\n{lookup.Text}";
    }

    private void UpdateTabSelection()
    {
        foreach (var tab in Tabs)
        {
            tab.IsSelected = tab.Table == SelectedTable;
        }
    }
}

public sealed class TableTabViewModel : ObservableObject
{
    private bool _isSelected;

    public TableTabViewModel(GalaxyMapTable table, Action select)
    {
        Table = table;
        Label = table.ToString();
        SelectCommand = new RelayCommand(select);
    }

    public GalaxyMapTable Table { get; }
    public string Label { get; }
    public RelayCommand SelectCommand { get; }
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}

public sealed class TableRowViewModel
{
    public TableRowViewModel(GalaxyMapRowKey key, IReadOnlyList<TableCellViewModel> cells)
    {
        Key = key;
        Cells = cells;
    }

    public GalaxyMapRowKey Key { get; }
    public IReadOnlyList<TableCellViewModel> Cells { get; }
}

public sealed class TableCellViewModel : ObservableObject
{
    private string _editValue;
    private string? _validationError;

    public TableCellViewModel(
        string displayValue,
        string effectiveModuleTag,
        ModuleColor effectiveModuleColor,
        bool isStaged,
        bool differsFromLowerInstance,
        int overrideCount,
        string? tlkToolTipText = null)
    {
        DisplayValue = displayValue;
        _editValue = displayValue;
        EffectiveModuleTag = effectiveModuleTag;
        EffectiveModuleColor = effectiveModuleColor;
        IsStaged = isStaged;
        DiffersFromLowerInstance = differsFromLowerInstance;
        OverrideCount = overrideCount;
        TlkToolTipText = tlkToolTipText;
    }

    public string DisplayValue { get; }
    public string EditValue
    {
        get => _editValue;
        set => SetProperty(ref _editValue, value);
    }

    public string EffectiveModuleTag { get; }
    public ModuleColor EffectiveModuleColor { get; }
    public bool IsStaged { get; }
    public bool DiffersFromLowerInstance { get; }
    public int OverrideCount { get; }
    public string? TlkToolTipText { get; }
    public bool HasError => !string.IsNullOrWhiteSpace(ValidationError);
    public string? ValidationError
    {
        get => _validationError;
        private set
        {
            if (SetProperty(ref _validationError, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(ToolTipText));
            }
        }
    }

    public string ToolTipText
    {
        get
        {
            if (ValidationError is { Length: > 0 })
            {
                return WrapToolTip(ValidationError);
            }

            var provenance = $"Effective value supplied by {EffectiveModuleTag}.";
            var overrides = OverrideCount > 1
                ? $" {OverrideCount} mounted instances exist for this row."
                : string.Empty;
            var comparison = DiffersFromLowerInstance
                ? " This value differs from the next lower instance."
                : string.Empty;
            var staged = IsStaged ? " This cell has an uncommitted change." : string.Empty;
            var cellContext = provenance + overrides + comparison + staged;
            var toolTip = string.IsNullOrWhiteSpace(TlkToolTipText)
                ? cellContext
                : TlkToolTipText;
            return WrapToolTip(toolTip);
        }
    }

    private static string WrapToolTip(string text, int maximumLineLength = 60)
    {
        var result = new StringBuilder(text.Length + 16);
        var logicalLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var lineIndex = 0; lineIndex < logicalLines.Length; lineIndex++)
        {
            var remaining = logicalLines[lineIndex].TrimEnd();
            while (remaining.Length > maximumLineLength)
            {
                var breakAt = remaining.LastIndexOf(' ', maximumLineLength);
                if (breakAt <= 0)
                {
                    breakAt = maximumLineLength;
                }

                result.Append(remaining[..breakAt].TrimEnd()).Append('\n');
                remaining = remaining[breakAt..].TrimStart();
            }

            result.Append(remaining);
            if (lineIndex < logicalLines.Length - 1)
            {
                result.Append('\n');
            }
        }

        return result.ToString();
    }

    internal void SetValidationError(string? error) => ValidationError = error;
}
