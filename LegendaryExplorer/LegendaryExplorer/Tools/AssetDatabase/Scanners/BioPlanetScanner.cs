using LegendaryExplorer.UserControls.ExportLoaderControls.MaterialEditor;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LegendaryExplorer.Tools.AssetDatabase.Scanners;

/// <summary>
/// Captures the scalar/vector MIC override populations actually used by BioPlanet surface and
/// cloud layers. The Asset Database consumer filters mod records out before randomization.
/// </summary>
internal sealed class BioPlanetScanner : AssetScanner
{
    public override void ScanExport(ExportScanInfo e, ConcurrentAssetDB db, AssetDBScanOptions options)
    {
        if (e.IsDefault || e.ClassName != "BioPlanet")
        {
            return;
        }

        ScanLayer(e, db, "PlanetMaterial", BioPlanetMaterialLayer.Planet);
        ScanLayer(e, db, "CloudMaterial", BioPlanetMaterialLayer.Cloud);
    }

    private static void ScanLayer(ExportScanInfo e, ConcurrentAssetDB db, string propertyName,
        BioPlanetMaterialLayer layer)
    {
        if (e.Properties.GetProp<ObjectProperty>(propertyName)?.ResolveToEntry(e.Export.FileRef)
            is not ExportEntry material)
        {
            return;
        }

        var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var vectors = new Dictionary<string, BioPlanetVectorParameterRecord>(StringComparer.OrdinalIgnoreCase);
        ReadMaterialOverrides(material, scalars, vectors, []);
        if (scalars.Count == 0 && vectors.Count == 0)
        {
            return;
        }

        var profile = new BioPlanetMaterialProfileRecord(
            e.Export.InstancedFullPath,
            material.InstancedFullPath,
            layer,
            e.FileKey,
            e.Export.UIndex,
            material.UIndex,
            e.IsMod,
            e.IsDlc,
            scalars.OrderBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase)
                .Select(parameter => new BioPlanetScalarParameterRecord(parameter.Key, parameter.Value))
                .ToArray(),
            vectors.Values.OrderBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase).ToArray());
        db.GeneratedBioPlanetMaterials.TryAdd($"{e.FileKey}:{e.Export.UIndex}:{(byte)layer}", profile);
    }

    private static void ReadMaterialOverrides(ExportEntry material,
        IDictionary<string, float> scalars,
        IDictionary<string, BioPlanetVectorParameterRecord> vectors,
        HashSet<int> visited)
    {
        if (!visited.Add(material.UIndex))
        {
            return;
        }

        if (material.GetProperty<ObjectProperty>("Parent")?.ResolveToEntry(material.FileRef)
            is ExportEntry parent)
        {
            ReadMaterialOverrides(parent, scalars, vectors, visited);
        }

        foreach (ScalarParameter parameter in ScalarParameter.GetScalarParameters(material, true) ?? [])
        {
            if (IsValidParameter(parameter.ParameterName) && float.IsFinite(parameter.ParameterValue))
            {
                scalars[parameter.ParameterName] = parameter.ParameterValue;
            }
        }

        foreach (VectorParameter parameter in VectorParameter.GetVectorParameters(material, true) ?? [])
        {
            // CFVector4 follows the existing Material Editor layout: W=R, X=G, Y=B, Z=A.
            float r = parameter.ParameterValue.W;
            float g = parameter.ParameterValue.X;
            float b = parameter.ParameterValue.Y;
            float a = parameter.ParameterValue.Z;
            if (IsValidParameter(parameter.ParameterName)
                && float.IsFinite(r) && float.IsFinite(g) && float.IsFinite(b) && float.IsFinite(a))
            {
                vectors[parameter.ParameterName] = new BioPlanetVectorParameterRecord(
                    parameter.ParameterName, r, g, b, a);
            }
        }
    }

    private static bool IsValidParameter(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.Equals(NameReference.None.Name, StringComparison.OrdinalIgnoreCase);
}
