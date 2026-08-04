using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using System;
using System.Linq;

namespace LegendaryExplorer.Tools.AssetDatabase.Scanners;

internal sealed class BioMorphScanner : AssetScanner
{
    public override void ScanExport(ExportScanInfo e, ConcurrentAssetDB db, AssetDBScanOptions options)
    {
        if (e.Export.Game != MEGame.LE3 || e.IsDefault || e.ClassName != "BioMorphFace")
        {
            return;
        }

        ObjectProperty baseHeadProperty = e.Properties.GetProp<ObjectProperty>("m_oBaseHead");
        IEntry baseHead = baseHeadProperty?.ResolveToEntry(e.Export.FileRef);
        string baseHeadName = baseHead?.ObjectName.Name;
        BioMorphSpecies species = baseHeadName.GetBioMorphSpecies();
        if (species == BioMorphSpecies.Unknown
            || e.Properties.GetProp<ArrayProperty<StructProperty>>("m_aMorphFeatures") is not { Count: > 0 } featureArray)
        {
            return;
        }

        BioMorphFeatureRecord[] features = featureArray
            .Select(feature => new BioMorphFeatureRecord(
                feature.GetProp<NameProperty>("sFeatureName")?.Value.Instanced,
                feature.GetProp<FloatProperty>("Offset")?.Value ?? 0f))
            .Where(feature => !string.IsNullOrWhiteSpace(feature.Name)
                              && !feature.Name.Equals(NameReference.None.Name, StringComparison.OrdinalIgnoreCase)
                              && float.IsFinite(feature.Value))
            .GroupBy(feature => feature.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new BioMorphFeatureRecord(group.Key, group.Sum(feature => feature.Value)))
            .OrderBy(feature => feature.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (features.Length == 0)
        {
            return;
        }

        ExportEntry materialOverride = e.Properties.GetProp<ObjectProperty>("m_oMaterialOverrides")?
            .ResolveToEntry(e.Export.FileRef) as ExportEntry;
        PropertyCollection materialProperties = materialOverride?.GetProperties();
        BioMorphScalarRecord[] scalarOverrides = materialProperties?
            .GetProp<ArrayProperty<StructProperty>>("m_aScalarOverrides")?
            .Select(scalar => new BioMorphScalarRecord(
                scalar.GetProp<NameProperty>("nName")?.Value.Instanced,
                scalar.GetProp<FloatProperty>("sValue")?.Value ?? 0f))
            .Where(scalar => IsValidParameterName(scalar.Name) && float.IsFinite(scalar.Value))
            .GroupBy(scalar => scalar.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(scalar => scalar.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        BioMorphColorRecord[] colorOverrides = materialProperties?
            .GetProp<ArrayProperty<StructProperty>>("m_aColorOverrides")?
            .Select(color =>
            {
                LinearColor value = color.GetProp<StructProperty>("cValue") is { } linearColor
                    ? CommonStructs.GetLinearColor(linearColor)
                    : LinearColor.White;
                return new BioMorphColorRecord(
                    color.GetProp<NameProperty>("nName")?.Value.Instanced,
                    value.R, value.G, value.B, value.A);
            })
            .Where(color => IsValidParameterName(color.Name)
                            && float.IsFinite(color.R)
                            && float.IsFinite(color.G)
                            && float.IsFinite(color.B)
                            && float.IsFinite(color.A))
            .GroupBy(color => color.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(color => color.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        var record = new BioMorphFaceRecord(
            e.Export.InstancedFullPath,
            baseHeadName,
            species,
            e.FileKey,
            e.Export.UIndex,
            e.IsMod,
            features,
            scalarOverrides,
            colorOverrides);
        db.GeneratedMorphFaces.TryAdd($"{e.FileKey}:{e.Export.UIndex}", record);
    }

    private static bool IsValidParameterName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.Equals(NameReference.None.Name, StringComparison.OrdinalIgnoreCase);
}
