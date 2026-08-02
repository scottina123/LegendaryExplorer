using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows;
using LegendaryExplorer.Dialogs;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.Tools.InterpEditor;

internal static class StageBoneOriginResolver
{
    private sealed record StageOption(ExportEntry Stage, ExportEntry StartConversation,
        IReadOnlyDictionary<string, string> VariableLinkSubtitles)
    {
        public string StageSubtitle => VariableLinkSubtitles.GetValueOrDefault("Stage");
        public override string ToString() => $"{Stage.ObjectName.Instanced} ({Stage.InstancedFullPath}, StartConversation #{StartConversation.UIndex})";
    }

    private sealed record BoneOption(ExportEntry Mesh, int Index, MeshBone Bone, string AttachmentSubtitle)
    {
        public override string ToString()
        {
            string attachment = string.IsNullOrWhiteSpace(AttachmentSubtitle) ? null : $" — {AttachmentSubtitle}";
            return $"{Bone.Name.Instanced}{attachment} [{Index}] — {Mesh.InstancedFullPath} ({Bone.Position.X:0.###}, {Bone.Position.Y:0.###}, {Bone.Position.Z:0.###})";
        }
    }

    public static bool TrySelectOrigin(Window owner, IMEPackage sourcePackage, ExportEntry contextExport,
        ConversationExtended conversation, out CameraOrigin origin, out string message)
    {
        origin = default;
        message = null;
        if (sourcePackage is null)
        {
            message = "No package is available for stage resolution.";
            return false;
        }

        string conversationName = conversation?.Export?.ObjectName.Instanced
                                  ?? FindConversationNameViaInterpData(FindOwningInterpData(contextExport));
        if (string.IsNullOrWhiteSpace(conversationName))
        {
            message = "The BioConversation associated with the selected dialogue sequence could not be determined.";
            return false;
        }

        string mainPackagePath = FindMainPackagePath(sourcePackage);
        if (mainPackagePath is null)
        {
            message = $"The non-localized PCC corresponding to '{Path.GetFileName(sourcePackage.FilePath)}' could not be found.";
            return false;
        }

        IMEPackage openedPackage = null;
        try
        {
            IMEPackage mainPackage = PathsEqual(mainPackagePath, sourcePackage.FilePath)
                ? sourcePackage
                : openedPackage = MEPackageHandler.OpenMEPackage(mainPackagePath);
            using var cache = new PackageCache();
            List<StageOption> stages = FindStages(mainPackage, conversationName, cache);
            if (stages.Count == 0)
            {
                message = $"No StartConversation referencing '{conversationName}' has a linked BioStage in '{Path.GetFileName(mainPackagePath)}'.";
                return false;
            }

            StageOption selectedStage = SelectStage(owner, stages, "Choose Linked BioStage",
                "Choose the BioStage linked to the matching StartConversation.");
            if (selectedStage is null)
            {
                return false;
            }

            List<BoneOption> bones = FindBones(selectedStage.Stage, selectedStage.VariableLinkSubtitles, cache);
            if (bones.Count == 0)
            {
                message = $"No skeletal mesh RefSkeleton could be resolved from '{selectedStage.Stage.InstancedFullPath}'.";
                return false;
            }

            BoneOption selectedBone = Select(owner, bones, "Choose Stage Origin Bone",
                "Choose the RefSkeleton bone whose raw Position will offset the BioStage Location.");
            if (selectedBone is null)
            {
                return false;
            }

            PropertyCollection stageProperties = GetPropertiesIncludingArchetypes(selectedStage.Stage, cache);
            StructProperty locationProperty = stageProperties.GetProp<StructProperty>("location")
                                              ?? stageProperties.GetProp<StructProperty>("Location");
            StructProperty rotationProperty = stageProperties.GetProp<StructProperty>("Rotation");
            Vector3 stageLocation = locationProperty is null ? Vector3.Zero : CommonStructs.GetVector3(locationProperty);
            Vector3 stageRotation = rotationProperty is null
                ? Vector3.Zero
                : CommonStructs.GetRotator(rotationProperty).GetDegreesVector();
            Vector3 boneRotation = new(selectedBone.Bone.Orientation.X, selectedBone.Bone.Orientation.Y,
                selectedBone.Bone.Orientation.Z);
            origin = new CameraOrigin(stageLocation + selectedBone.Bone.Position, stageRotation + boneRotation);
            message = $"Origin set to {selectedStage.Stage.ObjectName.Instanced} transform plus {selectedBone.Bone.Name.Instanced} position and orientation.";
            return true;
        }
        catch (Exception exception)
        {
            message = $"Stage origin resolution failed: {exception.Message}";
            return false;
        }
        finally
        {
            openedPackage?.Dispose();
        }
    }

    private static T Select<T>(Window owner, IReadOnlyList<T> options, string title, string prompt) where T : class
    {
        if (options.Count == 1)
        {
            return options[0];
        }

        string[] labels = options.Select(option => option.ToString()).ToArray();
        string selected = StringSelectorDialog.GetValue(owner, prompt, title, labels, labels[0]);
        int index = Array.IndexOf(labels, selected);
        return index >= 0 ? options[index] : null;
    }

    private static StageOption SelectStage(Window owner, IReadOnlyList<StageOption> options, string title, string prompt)
    {
        if (options.Count == 1)
        {
            return options[0];
        }

        StringSelectorItem[] items = options.Select((option, index) => new StringSelectorItem(
            index.ToString(), option.ToString(), option.StageSubtitle)).ToArray();
        string selected = StringSelectorDialog.GetValue(owner, prompt, title, items, items[0].Value);
        return int.TryParse(selected, out int index) && index >= 0 && index < options.Count ? options[index] : null;
    }

    private static List<StageOption> FindStages(IMEPackage package, string conversationName, PackageCache cache)
    {
        var stages = new List<StageOption>();
        foreach (ExportEntry startConversation in package.Exports.Where(export =>
                     export.ClassName is "BioSeqAct_StartConversation" or "SFXSeqAct_StartConversation" or "SFXSeqAct_StartAmbientConv"))
        {
            IEntry conversationEntry = startConversation.GetProperty<ObjectProperty>("Conv")?.ResolveToEntry(package);
            if (!string.Equals(conversationEntry?.ObjectName.Instanced, conversationName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            List<VarLinkInfo> variableLinks = KismetHelper.GetVariableLinks(startConversation.GetProperties(), package);
            VarLinkInfo stageLink = variableLinks
                .FirstOrDefault(link => string.Equals(link.LinkDesc, "Stage", StringComparison.OrdinalIgnoreCase));
            IReadOnlyDictionary<string, string> variableLinkSubtitles = ResolveVariableLinkSubtitles(variableLinks, cache);
            foreach (IEntry linkedNode in stageLink?.LinkedNodes ?? [])
            {
                ExportEntry stage = ResolveLinkedStage(linkedNode, cache);
                if (stage is not null && stages.All(option => option.Stage != stage))
                {
                    stages.Add(new StageOption(stage, startConversation, variableLinkSubtitles));
                }
            }
        }

        return stages;
    }

    private static IReadOnlyDictionary<string, string> ResolveVariableLinkSubtitles(IEnumerable<VarLinkInfo> variableLinks,
        PackageCache cache)
    {
        var subtitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (VarLinkInfo variableLink in variableLinks.Where(link => !string.IsNullOrWhiteSpace(link.LinkDesc)))
        {
            string[] values = variableLink.LinkedNodes
                .Select(linkedNode => ResolveVariableAttachment(linkedNode, cache))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (values.Length > 0)
            {
                subtitles[variableLink.LinkDesc] = string.Join(" • ", values);
            }
        }
        return subtitles;
    }

    private static string ResolveVariableAttachment(IEntry linkedNode, PackageCache cache)
    {
        ExportEntry linkedExport = ResolveExport(linkedNode, cache);
        if (linkedExport is null)
        {
            return null;
        }
        if (linkedExport.ClassName == "SeqVar_Player")
        {
            return "Player";
        }

        PropertyCollection properties = GetPropertiesIncludingArchetypes(linkedExport, cache);
        if (properties.GetProp<NameProperty>("m_sObjectTagToFind") is { } nameTag)
        {
            return nameTag.Value.Instanced;
        }
        if (properties.GetProp<StrProperty>("m_sObjectTagToFind") is { } stringTag)
        {
            return stringTag.Value;
        }
        if (properties.GetProp<ObjectProperty>("ObjValue") is { } objectValue)
        {
            return FormatObjectAttachment(objectValue.ResolveToEntry(linkedExport.FileRef), cache);
        }
        if (linkedExport.ClassName == "BioStage")
        {
            return FormatObjectAttachment(linkedExport, cache);
        }

        return $"{linkedExport.ObjectName.Instanced} ({linkedExport.ClassName})";
    }

    private static string FormatObjectAttachment(IEntry entry, PackageCache cache)
    {
        ExportEntry export = ResolveExport(entry, cache);
        if (export is null)
        {
            return entry?.ObjectName.Instanced;
        }

        string subtitle = $"#{export.UIndex} {export.ObjectName.Instanced}";
        NameProperty tag = GetPropertiesIncludingArchetypes(export, cache).GetProp<NameProperty>("Tag");
        return tag is not null && tag.Value != export.ObjectName
            ? $"{subtitle} — Tag: {tag.Value.Instanced}"
            : subtitle;
    }

    private static ExportEntry ResolveLinkedStage(IEntry linkedNode, PackageCache cache)
    {
        ExportEntry linkedExport = ResolveExport(linkedNode, cache);
        if (linkedExport is null)
        {
            return null;
        }
        if (linkedExport.ClassName == "BioStage")
        {
            return linkedExport;
        }

        ObjectProperty objectValue = GetPropertiesIncludingArchetypes(linkedExport, cache).GetProp<ObjectProperty>("ObjValue");
        return objectValue is null ? null : ResolveExport(objectValue.ResolveToEntry(linkedExport.FileRef), cache);
    }

    private static List<BoneOption> FindBones(ExportEntry stage,
        IReadOnlyDictionary<string, string> variableLinkSubtitles, PackageCache cache)
    {
        var meshes = new HashSet<ExportEntry>();
        var visited = new HashSet<ExportEntry>();
        foreach (ExportEntry descendant in stage.FileRef.Exports.Where(export => export == stage || export.IsDescendantOf(stage)))
        {
            AddMeshesFromExport(descendant, cache, meshes, visited);
        }
        AddMeshesFromExport(stage, cache, meshes, visited);

        var bones = new List<BoneOption>();
        foreach (ExportEntry mesh in meshes.OrderBy(entry => entry.InstancedFullPath, StringComparer.OrdinalIgnoreCase))
        {
            SkeletalMesh skeletalMesh = ObjectBinary.From<SkeletalMesh>(mesh, cache);
            if (skeletalMesh?.RefSkeleton is null)
            {
                continue;
            }
            bones.AddRange(skeletalMesh.RefSkeleton.Select((bone, index) =>
            {
                string subtitle = variableLinkSubtitles.GetValueOrDefault(bone.Name.Instanced);
                if (index == 0 && string.IsNullOrWhiteSpace(subtitle))
                {
                    subtitle = variableLinkSubtitles.GetValueOrDefault("Stage");
                }
                return new BoneOption(mesh, index, bone, subtitle);
            }));
        }
        return bones;
    }

    private static void AddMeshesFromExport(ExportEntry export, PackageCache cache, ISet<ExportEntry> meshes,
        ISet<ExportEntry> visited)
    {
        if (!visited.Add(export))
        {
            return;
        }
        if (export.ClassName == "SkeletalMesh")
        {
            meshes.Add(export);
        }

        for (ExportEntry current = export; current is not null; current = ResolveExport(current.Archetype, cache))
        {
            PropertyCollection properties = current.GetProperties();
            ObjectProperty meshProperty = properties.GetProp<ObjectProperty>("SkeletalMesh");
            ExportEntry mesh = meshProperty is null ? null : ResolveExport(meshProperty.ResolveToEntry(current.FileRef), cache);
            if (mesh?.ClassName == "SkeletalMesh")
            {
                meshes.Add(mesh);
            }

            foreach (ObjectProperty objectProperty in properties.OfType<ObjectProperty>())
            {
                ExportEntry referenced = ResolveExport(objectProperty.ResolveToEntry(current.FileRef), cache);
                if (referenced?.ClassName == "SkeletalMesh")
                {
                    meshes.Add(referenced);
                }
                else if (referenced?.ClassName == "SkeletalMeshComponent")
                {
                    AddMeshesFromExport(referenced, cache, meshes, visited);
                }
            }
        }
    }

    private static PropertyCollection GetPropertiesIncludingArchetypes(ExportEntry export, PackageCache cache)
    {
        PropertyCollection properties = export.GetProperties();
        for (ExportEntry archetype = ResolveExport(export.Archetype, cache); archetype is not null;
             archetype = ResolveExport(archetype.Archetype, cache))
        {
            foreach (Property property in archetype.GetProperties())
            {
                if (!properties.ContainsNamedProp(property.Name, property.StaticArrayIndex))
                {
                    properties.Add(property);
                }
            }
        }
        return properties;
    }

    private static ExportEntry ResolveExport(IEntry entry, PackageCache cache) => entry switch
    {
        ExportEntry export => export,
        ImportEntry import => EntryImporter.ResolveImport(import, cache),
        _ => null
    };

    private static string FindMainPackagePath(IMEPackage package)
    {
        if (string.IsNullOrWhiteSpace(package.FilePath))
        {
            return null;
        }
        if (Path.GetFileName(package.FilePath).GetUnrealLocalization() == MELocalization.None)
        {
            return package.FilePath;
        }

        string baseFileName = Path.GetFileName(package.FilePath).StripUnrealLocalization();
        string siblingPath = Path.Combine(Path.GetDirectoryName(package.FilePath)!, baseFileName);
        if (File.Exists(siblingPath))
        {
            return siblingPath;
        }
        return MELoadedFiles.TryGetHighestMountedFile(package.Game, baseFileName, out string mountedPath)
            ? mountedPath
            : null;
    }

    private static bool PathsEqual(string first, string second) =>
        !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second)
        && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static ExportEntry FindOwningInterpData(ExportEntry export)
    {
        for (IEntry current = export; current is ExportEntry currentExport; current = current.Parent)
        {
            if (currentExport.ClassName == "InterpData")
            {
                return currentExport;
            }
        }
        return export?.ClassName == "InterpData" ? export : null;
    }

    private static string FindConversationNameViaInterpData(ExportEntry interpData)
    {
        if (interpData is null)
        {
            return null;
        }

        IMEPackage package = interpData.FileRef;
        string sequencePropertyName = package.Game.IsGame1() ? "m_pEvtSystemSeq" : "MatineeSequence";
        foreach (ExportEntry interpAction in package.Exports.Where(export => export.ClassName == "SeqAct_Interp"))
        {
            VarLinkInfo dataLink = KismetHelper.GetVariableLinks(interpAction.GetProperties(), package)
                .FirstOrDefault(link => string.Equals(link.LinkDesc, "Data", StringComparison.OrdinalIgnoreCase));
            if (dataLink?.LinkedNodes.All(entry => entry?.UIndex != interpData.UIndex) != false
                || interpAction.Parent is not ExportEntry sequence)
            {
                continue;
            }

            foreach (ExportEntry conversation in package.Exports.Where(export => export.ClassName == "BioConversation"))
            {
                IEntry linkedSequence = conversation.GetProperty<ObjectProperty>(sequencePropertyName)?.ResolveToEntry(package);
                if (linkedSequence?.UIndex == sequence.UIndex
                    || string.Equals(linkedSequence?.ObjectName.Instanced, sequence.ObjectName.Instanced, StringComparison.OrdinalIgnoreCase))
                {
                    return conversation.ObjectName.Instanced;
                }
            }
        }
        return null;
    }
}
