using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Workflows;

public sealed record WorkflowResult(
    bool Succeeded,
    string Message,
    GalaxyMapRowKey? SelectionKey = null,
    NavigationTarget? Navigation = null,
    ChangeImpact? Impact = null,
    string? Error = null)
{
    public static WorkflowResult Success(
        string message,
        GalaxyMapRowKey? selectionKey = null,
        NavigationTarget? navigation = null,
        ChangeImpact? impact = null)
        => new(true, message, selectionKey, navigation, impact);

    public static WorkflowResult Failure(string error, GalaxyMapRowKey? selectionKey = null)
        => new(false, error, selectionKey, Error: error);
}

public readonly record struct NavigationTarget(int? ClusterRowId, int? SystemRowId)
{
    public static NavigationTarget Galaxy => new(null, null);
}

public sealed record ChangeImpact(
    IReadOnlySet<GalaxyMapTable> Tables,
    IReadOnlySet<GalaxyMapRowKey> Rows,
    bool IsStructural)
{
    public static ChangeImpact Empty { get; } = new(
        new HashSet<GalaxyMapTable>(),
        new HashSet<GalaxyMapRowKey>(),
        false);

    public static ChangeImpact StructuralAll { get; } = new(
        Enum.GetValues<GalaxyMapTable>().ToHashSet(),
        new HashSet<GalaxyMapRowKey>(),
        true);

    public static ChangeImpact For(
        IEnumerable<GalaxyMapTable> tables,
        IEnumerable<GalaxyMapRowKey>? rows = null,
        bool isStructural = false)
        => new(
            tables.ToHashSet(),
            rows?.ToHashSet() ?? new HashSet<GalaxyMapRowKey>(),
            isStructural);
}

public sealed class SessionChangedEventArgs(long revision, ChangeImpact impact) : EventArgs
{
    public long Revision { get; } = revision;
    public ChangeImpact Impact { get; } = impact;
}
