using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using LegendaryExplorer.Dialogs;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Tools.AssetDatabase;

public enum PreviewActorModelComponent
{
    Body,
    Head,
    Hair
}

public static class PreviewActorModelDefaults
{
    public const string NoneMeshName = "None";
    public const string BodyMeshName = "HMF_ARM_MIRa_MDL";
    public const string HeadMeshName = "HMF_HED_PROMiranda_MDL";
    public const string HairMeshName = "HMF_HIR_PROMiranda_MDL";

    public static string GetMeshName(PreviewActorModelComponent component) => component switch
    {
        PreviewActorModelComponent.Body => BodyMeshName,
        PreviewActorModelComponent.Head => HeadMeshName,
        PreviewActorModelComponent.Hair => HairMeshName,
        _ => throw new ArgumentOutOfRangeException(nameof(component))
    };

    public static bool IsNone(MeshRecord mesh) =>
        string.Equals(mesh?.MeshName, NoneMeshName, StringComparison.Ordinal);

    public static IReadOnlyList<MeshRecord> GetPickerMeshes(IEnumerable<MeshRecord> meshes,
        PreviewActorModelComponent component)
    {
        List<MeshRecord> pickerMeshes = meshes.ToList();
        if (component is not PreviewActorModelComponent.Body)
        {
            pickerMeshes.RemoveAll(IsNone);
            pickerMeshes.Insert(0, new MeshRecord(NoneMeshName, false, false, 0));
        }
        return pickerMeshes;
    }

    public static MeshRecord SelectMesh(Control owner, IEnumerable<MeshRecord> meshes,
        PreviewActorModelComponent component, string currentMeshName)
    {
        IReadOnlyList<MeshRecord> pickerMeshes = GetPickerMeshes(meshes, component);
        string selectedName = StringSelectorDialog.GetValue(owner,
            $"Select the preview actor {component.ToString().ToLowerInvariant()} model.",
            $"Select {component} Model",
            pickerMeshes.Select(mesh => mesh.MeshName),
            string.IsNullOrEmpty(currentMeshName) && component is not PreviewActorModelComponent.Body
                ? NoneMeshName
                : currentMeshName);
        return string.IsNullOrEmpty(selectedName)
            ? null
            : pickerMeshes.FirstOrDefault(mesh => string.Equals(mesh.MeshName, selectedName,
                StringComparison.OrdinalIgnoreCase));
    }

    public static MeshRecord FindDefaultMesh(IEnumerable<MeshRecord> meshes, AssetDB database,
        PreviewActorModelComponent component, MEGame game)
    {
        MeshRecord mesh = meshes.FirstOrDefault(candidate =>
            string.Equals(candidate.MeshName, GetMeshName(component), StringComparison.OrdinalIgnoreCase)
            && candidate.Usages.Any(usage => IsBaseGameUsage(database, usage)));
        if (mesh is not null || component is not PreviewActorModelComponent.Body)
        {
            return mesh;
        }

        string fallbackName = game switch
        {
            MEGame.LE1 or MEGame.ME1 => "QRN_FAC_ARM_LGTa_MDL",
            MEGame.LE2 or MEGame.ME2 => "QRN_TLI_LGTa_MDL",
            _ => "QRN_ARM_TLIa_MDL"
        };
        return meshes.FirstOrDefault(candidate =>
            string.Equals(candidate.MeshName, fallbackName, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<MeshUsage> GetUsages(MeshRecord mesh, AssetDB database, bool baseGameOnly)
    {
        return baseGameOnly
            ? mesh.Usages.Where(usage => IsBaseGameUsage(database, usage))
            : mesh.Usages.OrderByDescending(usage => IsBaseGameUsage(database, usage));
    }

    public static bool IsBaseGameUsage(AssetDB database, MeshUsage usage)
    {
        if (usage.IsInMod || usage.FileKey < 0 || usage.FileKey >= database.FileList.Count)
        {
            return false;
        }

        int directoryKey = database.FileList[usage.FileKey].DirectoryKey;
        if (directoryKey < 0 || directoryKey >= database.ContentDir.Count)
        {
            return false;
        }

        string contentDirectory = database.ContentDir[directoryKey];
        return !contentDirectory.Contains("DLC", StringComparison.OrdinalIgnoreCase);
    }
}
