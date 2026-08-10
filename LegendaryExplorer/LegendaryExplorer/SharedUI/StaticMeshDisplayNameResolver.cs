using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorer.SharedUI;

/// <summary>
/// Resolves the static-mesh asset used by an actor or component export.
/// </summary>
public static class StaticMeshDisplayNameResolver
{
    public static IEntry ResolveStaticMeshEntry(ExportEntry export)
    {
        if (export == null)
        {
            return null;
        }

        if (ResolveStaticMeshProperty(export) is { } staticMesh)
        {
            return staticMesh;
        }

        ObjectProperty componentProperty = export.GetProperty<ObjectProperty>("StaticMeshComponent");
        if (componentProperty != null
            && export.FileRef.TryGetUExport(componentProperty.Value, out ExportEntry component))
        {
            return ResolveStaticMeshProperty(component);
        }

        return null;
    }

    private static IEntry ResolveStaticMeshProperty(ExportEntry export)
    {
        ObjectProperty staticMeshProperty = export.GetProperty<ObjectProperty>("StaticMesh");
        if (staticMeshProperty != null
            && export.FileRef.TryGetEntry(staticMeshProperty.Value, out IEntry staticMesh)
            && staticMesh.IsA("StaticMesh"))
        {
            return staticMesh;
        }

        return null;
    }
}
