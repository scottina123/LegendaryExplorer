namespace LE1GalaxyMapEditor.Models;

public static class GalaxyMapLayerCloner
{
    public static GalaxyMapLayer Clone(GalaxyMapLayer source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var clone = new GalaxyMapLayer(source.Module);
        if (source.SourcePackagePath is not null && source.SourcePackageFingerprint is not null)
        {
            clone.SetPackageSource(source.SourcePackagePath, source.SourcePackageFingerprint);
        }

        foreach (var schema in source.Schemas.Values)
        {
            clone.SetSchema(new CsvTableSchema(
                schema.Table,
                schema.Headers,
                schema.DefaultCellTypes,
                schema.SourceIdentity));
        }

        foreach (var table in Enum.GetValues<GalaxyMapTable>())
        {
            foreach (var row in source.Rows(table))
            {
                clone.Add(GalaxyMapRowCloner.Clone(row));
            }

            clone.SetSourceRowOrder(table, source.GetSourceRowOrder(table));
        }

        return clone;
    }
}
