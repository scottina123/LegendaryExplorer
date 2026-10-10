using System;
using System.Collections.Generic;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorerCore.Packages.CloningImportingAndRelinking;

/// <summary>Shared property typing rules for reference reporting and bulk cleanup.</summary>
internal static class ObjectReferenceTypeChecker
{
    /// <summary>
    /// Returns a mismatch only when the property metadata establishes an expected type. Array elements use
    /// the array's property name and its containing class/struct, because the elements themselves are unnamed.
    /// Null references, unresolved entries and known vanilla exceptions are handled separately from typing.
    /// </summary>
    internal static bool TryGetMismatch(IEntry owner, ObjectProperty property, string containingClassOrStructName,
        NameReference propertyName, out string expectedType, out bool expectsClass)
    {
        expectedType = null;
        expectsClass = false;
        if (property.Value == 0 || !owner.FileRef.TryGetEntry(property.Value, out var referencedEntry))
            return false;

        ReferenceIssueCleaner.ValidateOuterChain(referencedEntry);
        if (referencedEntry.FullPath.Equals("SFXGame.BioDeprecated", StringComparison.InvariantCulture))
            return false;
        if (owner.Game == MEGame.ME2)
        {
            if (propertyName == "m_oAreaMap" && referencedEntry.ClassName == "BioSWF") return false;
            if (propertyName == "AIController" && referencedEntry.ObjectName.Name.StartsWith("BioAI_")) return false;
            if (propertyName == "TrackingSound" && owner.ClassName == "SFXSeqAct_SecurityCam"
                && referencedEntry.ClassName == "WwiseEvent") return false;
        }

        var propertyInfo = GlobalUnrealObjectInfo.GetPropertyInfo(owner.Game, propertyName,
            containingClassOrStructName, containingExport: owner as ExportEntry);
        var customClassInfos = new Dictionary<string, ClassInfo>();
        if (referencedEntry is ExportEntry { IsClass: true } classExport)
        {
            // Preserve support for classes defined by the package instead of the object database.
            var lookupEntry = classExport;
            while (lookupEntry is { IsClass: true }
                   && !GlobalUnrealObjectInfo.GetClasses(owner.Game).ContainsKey(lookupEntry.ObjectName))
            {
                if (customClassInfos.ContainsKey(lookupEntry.ObjectName))
                    break;
                customClassInfos[lookupEntry.ObjectName] = GlobalUnrealObjectInfo.generateClassInfo(lookupEntry);
                lookupEntry = lookupEntry.SuperClass as ExportEntry;
            }

            if (propertyInfo is null && customClassInfos.TryGetValue(referencedEntry.ObjectName, out var customInfo))
            {
                propertyInfo = GlobalUnrealObjectInfo.GetPropertyInfo(owner.Game, propertyName,
                    containingClassOrStructName, customInfo, containingExport: owner as ExportEntry);
            }
        }

        expectedType = propertyInfo?.Reference;
        // Native-only class imports have no reliable inheritance metadata.
        if (expectedType is null || referencedEntry.IsAKnownNativeClass())
            return false;

        expectsClass = referencedEntry.ClassName == "Class" && expectedType != "Class";
        return expectsClass
            ? !referencedEntry.InheritsFrom(expectedType, customClassInfos)
            : !referencedEntry.IsA(expectedType, customClassInfos);
    }
}
