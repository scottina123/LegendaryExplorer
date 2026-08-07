using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LegendaryExplorer.Tools.LevelEditor;

internal readonly record struct AssetImportResult(IEntry Entry, IReadOnlyList<string> RelinkWarnings);

/// <summary>
/// Imports assets selected by Level Editor pickers while preserving their package hierarchy.
/// </summary>
internal static class AssetImportHelper
{
    public static AssetImportResult GetOrImportAsset(
        (string FilePath, int UIndex) selection,
        IMEPackage destinationPackage,
        string expectedAssetClass)
    {
        var (sourcePath, sourceUIndex) = selection;
        if (sourcePath is null)
        {
            IEntry localEntry = destinationPackage.GetEntry(sourceUIndex);
            ValidateAsset(localEntry, expectedAssetClass);
            return new AssetImportResult(localEntry, []);
        }

        using IMEPackage sourcePackage = MEPackageHandler.OpenMEPackage(sourcePath);
        ExportEntry sourceAsset = sourcePackage.GetUExport(sourceUIndex);
        ValidateAsset(sourceAsset, expectedAssetClass);

        ExportEntry importParent = ResolveImportParent(sourceAsset, destinationPackage);
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
            sourceAsset,
            destinationPackage,
            importParent,
            true,
            relinkerOptions,
            out IEntry importedEntry);

        if (importedEntry is null)
        {
            throw new InvalidOperationException($"Could not import {sourceAsset.InstancedFullPath}.");
        }

        if (importedEntry is ExportEntry importedExport
            && importParent is not null
            && importedExport.Parent != importParent)
        {
            importedExport.Parent = importParent;
        }

        return new AssetImportResult(
            importedEntry,
            relinkResults?.Select(result => result.Message).ToList() ?? []);
    }

    private static void ValidateAsset(IEntry entry, string expectedAssetClass)
    {
        if (entry is not ExportEntry assetExport || !assetExport.IsA(expectedAssetClass))
        {
            string actualClass = entry?.ClassName ?? "missing entry";
            throw new InvalidOperationException(
                $"The selected entry is {actualClass}, not a {expectedAssetClass}.");
        }
    }

    private static ExportEntry ResolveImportParent(ExportEntry sourceAsset, IMEPackage destinationPackage)
    {
        List<ExportEntry> sourcePackageChain = [];
        for (IEntry entry = sourceAsset.Parent; entry is not null; entry = entry.Parent)
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
