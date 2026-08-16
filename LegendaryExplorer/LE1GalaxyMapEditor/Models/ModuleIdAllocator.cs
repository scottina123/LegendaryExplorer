namespace LE1GalaxyMapEditor.Models;

/// <summary>Allocates IDs only for genuinely new rows; same-ID overrides bypass allocation.</summary>
public sealed class ModuleIdAllocator
{
    private readonly GalaxyMapWorkspace _workspace;

    public ModuleIdAllocator(GalaxyMapWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    public int NextAvailable(GalaxyMapModule module, GalaxyMapTable table)
    {
        if (!TryNextAvailable(module, table, out var rowId))
        {
            var range = module.Reservations.GetRange(table);
            throw new InvalidOperationException(range is null
                ? $"Module {module.Tag} has no reserved {table} row ID range."
                : $"Module {module.Tag}'s reserved {table} range {range} is exhausted.");
        }

        return rowId;
    }

    public bool TryNextAvailable(GalaxyMapModule module, GalaxyMapTable table, out int rowId)
    {
        ArgumentNullException.ThrowIfNull(module);
        rowId = default;
        if (table == GalaxyMapTable.PlotPlanet)
        {
            throw new InvalidOperationException(
                "PlotPlanet rows use their Planet's existing row ID and are not allocated independently.");
        }

        var range = module.Reservations.GetRange(table);
        if (range is null)
        {
            return false;
        }

        var occupied = _workspace.Layers
            .SelectMany(layer => layer.Rows(table))
            .Select(row => row.RowId)
            .ToHashSet();
        for (long candidate = range.Value.Start; candidate <= range.Value.End; candidate++)
        {
            if (!occupied.Contains((int)candidate))
            {
                rowId = (int)candidate;
                return true;
            }
        }

        return false;
    }
}
