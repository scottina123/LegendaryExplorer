using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Services;

public static class GalaxyMapStrRefSchema
{
    public static bool IsStrRef(GalaxyMapTable table, string column)
        => (table, column.ToUpperInvariant()) switch
        {
            (GalaxyMapTable.Cluster, "NAME") => true,
            (GalaxyMapTable.System, "NAME") => true,
            (GalaxyMapTable.Planet, "NAME" or "DESCRIPTION" or "BUTTONLABEL") => true,
            (GalaxyMapTable.PlotPlanet, "NAME") => true,
            _ => false
        };
}
