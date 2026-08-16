namespace LE1GalaxyMapEditor.Models;

/// <summary>
/// Creates a detached data copy of a physical or effective row. Derived
/// relationship properties are deliberately omitted and rebuilt after composition.
/// </summary>
public static class GalaxyMapRowCloner
{
    public static T Clone<T>(T source) where T : GalaxyMapRow
        => (T)Clone((GalaxyMapRow)source);

    public static GalaxyMapRow Clone(GalaxyMapRow source)
    {
        ArgumentNullException.ThrowIfNull(source);
        GalaxyMapRow clone = source switch
        {
            Cluster row => new Cluster
            {
                RowId = row.RowId,
                Label = row.Label,
                X = row.X,
                Y = row.Y,
                Name = row.Name,
                NameText = row.NameText,
                SphereSize = row.SphereSize,
                Background = row.Background
            },
            GalaxySystem row => new GalaxySystem
            {
                RowId = row.RowId,
                Label = row.Label,
                ClusterRowId = row.ClusterRowId,
                X = row.X,
                Y = row.Y,
                Name = row.Name,
                NameText = row.NameText,
                Scale = row.Scale,
                ShowNebula = row.ShowNebula
            },
            Planet row => new Planet
            {
                RowId = row.RowId,
                Label = row.Label,
                SystemRowId = row.SystemRowId,
                X = row.X,
                Y = row.Y,
                Name = row.Name,
                NameText = row.NameText,
                ActiveWorld = row.ActiveWorld,
                Description = row.Description,
                ButtonLabel = row.ButtonLabel,
                MapRowId = row.MapRowId,
                Scale = row.Scale,
                RingColor = row.RingColor,
                OrbitRing = row.OrbitRing,
                SystemLevelType = row.SystemLevelType,
                PlanetLevelType = row.PlanetLevelType,
                Event = row.Event,
                ImageIndex = row.ImageIndex
            },
            PlotPlanetEntry row => new PlotPlanetEntry
            {
                RowId = row.RowId,
                Code = row.Code,
                Name = row.Name,
                NameText = row.NameText
            },
            MapEntry row => new MapEntry
            {
                RowId = row.RowId,
                MapName = row.MapName,
                StartPoint = row.StartPoint
            },
            RelayConnection row => new RelayConnection
            {
                RowId = row.RowId,
                StartClusterEncoded = row.StartClusterEncoded,
                EndClusterEncoded = row.EndClusterEncoded
            },
            _ => throw new ArgumentException(
                $"Unsupported galaxy-map row type {source.GetType().Name}.", nameof(source))
        };

        foreach (var name in source.ExtraFieldOrder)
        {
            clone.AddExtraField(name, source.ExtraFields[name]);
        }

        clone.Origin = source.Origin;
        clone.CsvSnapshot = source.CsvSnapshot?.Clone();
        return clone;
    }

    /// <summary>
    /// Copies the effective lower-layer row for insertion into the active layer.
    /// Its values remain identical and no column becomes dirty until the editor
    /// applies the requested change.
    /// </summary>
    public static GalaxyMapRow CloneForOverride(GalaxyMapRow source, GalaxyMapModule targetModule)
    {
        ArgumentNullException.ThrowIfNull(targetModule);
        var clone = Clone(source);
        clone.Origin = new GalaxyMapRowOrigin(targetModule, OverridesLowerLayer: true);
        clone.CsvSnapshot = source.CsvSnapshot?.CloneForOverride();
        return clone;
    }
}
