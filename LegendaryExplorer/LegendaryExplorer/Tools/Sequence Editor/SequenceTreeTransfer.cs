using System;
using System.Linq;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorer.Tools.Sequence_Editor;

internal static class SequenceTreeTransfer
{
    internal static bool CanCopy(ExportEntry source, ExportEntry destination)
    {
        return source != null && destination != null
               && source.FileRef != destination.FileRef
               && !source.IsDefaultObject && !destination.IsDefaultObject
               && source.IsA("Sequence") && destination.IsA("Sequence");
    }

    internal static ExportEntry Copy(ExportEntry source, ExportEntry destination, RelinkerOptionsPackage rop)
    {
        if (!CanCopy(source, destination))
        {
            throw new ArgumentException("The source and destination must be sequences in different packages.");
        }

        rop.IsCrossGame = source.Game != destination.Game;
        rop.ImportExportDependencies = true;
        var sourceParent = KismetHelper.GetParentSequence(source);
        if (sourceParent != null)
        {
            // References to the old owner must resolve to the drop target without overwriting its data.
            rop.CrossPackageMap[sourceParent] = destination;
            rop.RelinkMapEntriesToSkip.Add(sourceParent);
        }

        var package = destination.FileRef;
        var name = source.ObjectName;
        if (destination.GetChildren().Any(entry => entry.ObjectName == name))
        {
            name = package.GetNextIndexedName(name.Name);
        }

        // Import the root explicitly so an existing source path cannot cause an unrelated sequence
        // in the destination to be reused. Only the new copy's name changes on a collision.
        var imported = (ExportEntry)EntryImporter.ImportExport(package, source, destination.UIndex, rop);
        imported.ObjectName = name;
        EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.MergeTreeChildren,
            source, package, imported, true, rop, out _);

        KismetHelper.AddObjectToSequence(imported, destination, keepPositioning: true);

        // Dependencies linked outside the copied sequence can include siblings of the source.
        // They also need membership in their new owning sequence, as in Package Editor.
        if (sourceParent != null)
        {
            var importedSiblings = rop.CrossPackageMap
                .Where(pair => pair.Key is ExportEntry sourceObject
                               && sourceObject != source
                               && sourceObject.IsA("SequenceObject")
                               && KismetHelper.GetParentSequence(sourceObject) == sourceParent
                               && pair.Value is ExportEntry)
                .Select(pair => (ExportEntry)pair.Value)
                .Where(export => export != destination)
                .Distinct()
                .ToList();
            foreach (var sibling in importedSiblings)
            {
                KismetHelper.AddObjectToSequence(sibling, destination, keepPositioning: true);
            }
        }

        return imported;
    }
}
