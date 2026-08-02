using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using Newtonsoft.Json;

namespace LegendaryExplorer.Tools.Dialogue_Editor;

public sealed record DialoguePreviewNodeReference(bool IsReply, int Index);

public sealed class DialoguePreviewPreset
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public MEGame Game { get; set; }
    public int ConversationUIndex { get; set; }
    public DialoguePreviewNodeReference StartNode { get; set; }
    public List<string> LevelPaths { get; set; } = [];
    public Dictionary<string, DialoguePreviewNodeReference> BranchSelections { get; set; } = [];
    public DateTime SavedAtUtc { get; set; }

    [JsonIgnore]
    public string StorageFolder { get; internal set; }

    [JsonIgnore]
    public string Details => $"{Game} • {LevelPaths.Count} level{(LevelPaths.Count == 1 ? string.Empty : "s")} • {SavedAtUtc.ToLocalTime():g}";
}

public sealed class DialoguePreviewPresetSnapshot(
    IMEPackage package,
    ConversationExtended conversation,
    DialogueNodeExtended startNode,
    DialoguePreviewPreset preset) : IDisposable
{
    public IMEPackage Package { get; } = package;
    public ConversationExtended Conversation { get; } = conversation;
    public DialogueNodeExtended StartNode { get; } = startNode;
    public DialoguePreviewPreset Preset { get; } = preset;

    public void Dispose() => Package.Dispose();
}

public static class DialoguePreviewPresetLibrary
{
    public const string FolderName = "DialoguePreviewPresets";
    private const string MetadataExtension = ".dialoguepreview.json";

    public static string GetStorageFolder(ConversationExtended conversation, IReadOnlyList<string> levelPaths)
    {
        string parentFolder = levelPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(Directory.Exists)
            ?? Path.GetDirectoryName(conversation.Export.FileRef.FilePath);
        if (string.IsNullOrWhiteSpace(parentFolder))
        {
            throw new InvalidOperationException("A preset folder could not be determined from the selected levels or conversation package.");
        }

        return Directory.CreateDirectory(Path.Combine(parentFolder, FolderName)).FullName;
    }

    public static IReadOnlyList<DialoguePreviewPreset> Load(string storageFolder)
    {
        if (string.IsNullOrWhiteSpace(storageFolder) || !Directory.Exists(storageFolder))
        {
            return [];
        }

        var presets = new List<DialoguePreviewPreset>();
        foreach (string metadataPath in Directory.EnumerateFiles(storageFolder, $"*{MetadataExtension}"))
        {
            try
            {
                DialoguePreviewPreset preset = JsonConvert.DeserializeObject<DialoguePreviewPreset>(File.ReadAllText(metadataPath));
                if (preset is null || preset.Id == Guid.Empty || !File.Exists(GetSnapshotPath(storageFolder, preset.Id)))
                {
                    continue;
                }

                preset.StorageFolder = storageFolder;
                presets.Add(preset);
            }
            catch (JsonException)
            {
            }
        }

        return presets.OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static DialoguePreviewPreset Capture(
        ConversationExtended conversation,
        DialogueNodeExtended startNode,
        string name,
        IReadOnlyList<string> levelPaths,
        IReadOnlyDictionary<string, DialoguePreviewNodeReference> branchSelections = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(startNode);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(levelPaths);

        DialoguePreviewNodeReference startReference = GetNodeReference(conversation, startNode);
        string storageFolder = GetStorageFolder(conversation, levelPaths);
        var preset = new DialoguePreviewPreset
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Game = conversation.Export.Game,
            StartNode = startReference,
            LevelPaths = levelPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            BranchSelections = branchSelections?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ?? [],
            SavedAtUtc = DateTime.UtcNow,
            StorageFolder = storageFolder
        };

        string snapshotPath = GetSnapshotPath(storageFolder, preset.Id);
        try
        {
            EntryExporter.ExportExportToFile(conversation.Export, snapshotPath, out IEntry exportedConversation);
            preset.ConversationUIndex = exportedConversation.UIndex;
            Save(preset);
            return preset;
        }
        catch
        {
            if (File.Exists(snapshotPath))
            {
                File.Delete(snapshotPath);
            }
            throw;
        }
    }

    public static DialoguePreviewPresetSnapshot OpenSnapshot(DialoguePreviewPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        IMEPackage package = MEPackageHandler.OpenMEPackage(GetSnapshotPath(preset));
        try
        {
            ExportEntry conversationExport = package.GetUExport(preset.ConversationUIndex);
            var conversation = new ConversationExtended(conversationExport);
            conversation.LoadConversation(detailedParse: true);
            DialogueNodeExtended startNode = ResolveNode(conversation, preset.StartNode)
                ?? throw new InvalidDataException("The preset start node could not be found in its BioConversation snapshot.");
            return new DialoguePreviewPresetSnapshot(package, conversation, startNode, preset);
        }
        catch
        {
            package.Dispose();
            throw;
        }
    }

    public static void Rename(DialoguePreviewPreset preset, string name)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        preset.Name = name.Trim();
        Save(preset);
    }

    public static void UpdateSelections(
        DialoguePreviewPreset preset,
        IReadOnlyDictionary<string, DialoguePreviewNodeReference> branchSelections)
    {
        ArgumentNullException.ThrowIfNull(preset);
        preset.BranchSelections = branchSelections.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        Save(preset);
    }

    public static void Delete(DialoguePreviewPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        DeleteIfExists(GetMetadataPath(preset));
        DeleteIfExists(GetSnapshotPath(preset));
    }

    public static DialoguePreviewNodeReference GetNodeReference(ConversationExtended conversation, DialogueNodeExtended node)
    {
        int index = node.IsReply ? conversation.ReplyList.IndexOf(node) : conversation.EntryList.IndexOf(node);
        return index >= 0
            ? new DialoguePreviewNodeReference(node.IsReply, index)
            : throw new InvalidOperationException("The dialogue node is not part of the selected BioConversation.");
    }

    public static DialogueNodeExtended ResolveNode(ConversationExtended conversation, DialoguePreviewNodeReference reference) =>
        reference is null
            ? null
            : reference.IsReply
                ? conversation.ReplyList.ElementAtOrDefault(reference.Index)
                : conversation.EntryList.ElementAtOrDefault(reference.Index);

    private static void Save(DialoguePreviewPreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.StorageFolder))
        {
            throw new InvalidOperationException("The preset has no storage folder.");
        }

        Directory.CreateDirectory(preset.StorageFolder);
        File.WriteAllText(GetMetadataPath(preset), JsonConvert.SerializeObject(preset, Formatting.Indented));
    }

    private static string GetMetadataPath(DialoguePreviewPreset preset) =>
        Path.Combine(preset.StorageFolder, $"{preset.Id:N}{MetadataExtension}");

    private static string GetSnapshotPath(DialoguePreviewPreset preset) => GetSnapshotPath(preset.StorageFolder, preset.Id);

    private static string GetSnapshotPath(string storageFolder, Guid id) => Path.Combine(storageFolder, $"{id:N}.pcc");

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
