using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LegendaryExplorer.Tools.LevelEditor;

internal readonly record struct MeshImportResult(IEntry Entry, IReadOnlyList<string> RelinkWarnings);

/// <summary>
/// Imports meshes selected by the Level Editor mesh pickers while preserving their package hierarchy.
/// </summary>
internal static class MeshImportHelper
{
    public static MeshImportResult GetOrImportMesh(
        (string FilePath, int UIndex) selection,
        IMEPackage destinationPackage,
        string expectedMeshClass)
    {
        var (sourcePath, sourceUIndex) = selection;
        if (sourcePath is null)
        {
            IEntry localEntry = destinationPackage.GetEntry(sourceUIndex);
            ValidateMesh(localEntry, expectedMeshClass);
            return new MeshImportResult(localEntry, []);
        }

        using IMEPackage sourcePackage = MEPackageHandler.OpenMEPackage(sourcePath);
        ExportEntry sourceMesh = sourcePackage.GetUExport(sourceUIndex);
        ValidateMesh(sourceMesh, expectedMeshClass);

        ExportEntry importParent = ResolveImportParent(sourceMesh, destinationPackage);
        using var packageCache = new PackageCache();
        var relinkerOptions = new RelinkerOptionsPackage
        {
            ImportExportDependencies = true,
            PortImportsMemorySafe = true,
            Cache = packageCache,
            CustomRelinkUIndex = PreserveRelinkedMaterialTextureReference
        };

        var relinkResults = EntryImporter.ImportAndRelinkEntries(
            EntryImporter.PortingOption.CloneAllDependencies,
            sourceMesh,
            destinationPackage,
            importParent,
            true,
            relinkerOptions,
            out IEntry importedEntry);

        if (importedEntry is null)
        {
            throw new InvalidOperationException($"Could not import {sourceMesh.InstancedFullPath}.");
        }

        if (importedEntry is ExportEntry importedExport
            && importParent is not null
            && importedExport.Parent != importParent)
        {
            importedExport.Parent = importParent;
        }

        return new MeshImportResult(
            importedEntry,
            relinkResults?.Select(result => result.Message).ToList() ?? []);
    }

    private static void ValidateMesh(IEntry entry, string expectedMeshClass)
    {
        if (entry is not ExportEntry meshExport || !meshExport.IsA(expectedMeshClass))
        {
            string actualClass = entry?.ClassName ?? "missing entry";
            throw new InvalidOperationException(
                $"The selected entry is {actualClass}, not a {expectedMeshClass}.");
        }
    }

    private static ExportEntry ResolveImportParent(ExportEntry sourceMesh, IMEPackage destinationPackage)
    {
        List<ExportEntry> sourcePackageChain = [];
        for (IEntry entry = sourceMesh.Parent; entry is not null; entry = entry.Parent)
        {
            if (entry is ExportEntry { ClassName: "Package" } packageExport)
            {
                sourcePackageChain.Add(packageExport);
            }
        }

        sourcePackageChain.Reverse();

        ExportEntry currentParent = null;
        foreach (ExportEntry sourcePackageExport in sourcePackageChain)
        {
            currentParent = destinationPackage.CreatePackageExport(sourcePackageExport.ObjectName, currentParent);
        }

        return currentParent;
    }

    private static bool PreserveRelinkedMaterialTextureReference(
        IMEPackage sourcePackage,
        ExportEntry destinationExport,
        ref int uIndex,
        string propertyName,
        string prefix,
        RelinkerOptionsPackage options,
        out EntryStringPair result)
    {
        result = null;
        string fullPropertyPath = $"{prefix}{propertyName}";
        return fullPropertyPath.Contains("UniformExpressionTextures[", StringComparison.Ordinal)
               && sourcePackage.GetEntry(uIndex) is null
               && destinationExport.FileRef.GetEntry(uIndex) is not null;
    }
}
