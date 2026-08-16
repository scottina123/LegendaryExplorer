using LE1GalaxyMapEditor.Infrastructure;

namespace LE1GalaxyMapEditor.Models;

public abstract class GalaxyMapRow : ObservableObject
{
    private int _rowId;
    private GalaxyMapRowOrigin? _origin;
    private CsvRowSnapshot? _csvSnapshot;
    private readonly List<string> _extraFieldOrder = [];

    public virtual GalaxyMapTable Table
        => throw new InvalidOperationException($"{GetType().Name} is not a persisted galaxy-map table row.");

    public GalaxyMapRowKey Key => new(Table, RowId);

    public int RowId
    {
        get => _rowId;
        set
        {
            if (SetProperty(ref _rowId, value))
            {
                OnPropertyChanged(nameof(Key));
            }
        }
    }

    public GalaxyMapRowOrigin? Origin
    {
        get => _origin;
        internal set => SetProperty(ref _origin, value);
    }

    public CsvRowSnapshot? CsvSnapshot
    {
        get => _csvSnapshot;
        internal set => SetProperty(ref _csvSnapshot, value);
    }

    public Dictionary<string, string> ExtraFields { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> ExtraFieldOrder => _extraFieldOrder;

    public void AddExtraField(string name, string value)
    {
        if (!ExtraFields.ContainsKey(name))
        {
            _extraFieldOrder.Add(name);
        }

        ExtraFields[name] = value;
    }

    public void SetExtraField(string name, string value)
    {
        if (!ExtraFields.ContainsKey(name))
        {
            _extraFieldOrder.Add(name);
        }

        if (ExtraFields.TryGetValue(name, out var current) && current == value)
        {
            return;
        }

        ExtraFields[name] = value;
        OnPropertyChanged($"ExtraFields[{name}]");
    }

    protected static string FirstUseful(
        string first,
        string second,
        string third,
        string fallback)
        => !string.IsNullOrWhiteSpace(first) ? first
            : !string.IsNullOrWhiteSpace(second) ? second
            : !string.IsNullOrWhiteSpace(third) ? third
            : fallback;
}
