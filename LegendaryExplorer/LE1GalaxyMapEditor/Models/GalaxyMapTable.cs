namespace LE1GalaxyMapEditor.Models;

/// <summary>
/// Identifies one of the six galaxy-map 2DA tables supported by the editor.
/// Row IDs are unique within a table, not across every table.
/// </summary>
public enum GalaxyMapTable
{
    Cluster,
    System,
    Planet,
    PlotPlanet,
    Map,
    Relay
}

public readonly record struct GalaxyMapRowKey(GalaxyMapTable Table, int RowId)
{
    public override string ToString() => $"{Table}:{RowId}";
}
