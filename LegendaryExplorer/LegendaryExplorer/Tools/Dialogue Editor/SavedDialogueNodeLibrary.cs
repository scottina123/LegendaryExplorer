using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegendaryExplorer.Misc;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using Newtonsoft.Json;

namespace LegendaryExplorer.Tools.Dialogue_Editor;

public sealed class SavedDialogueNode
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public MEGame Game { get; set; }
    public bool IsReply { get; set; }
    public int NodeIndex { get; set; }
    public int ConversationUIndex { get; set; }
    public string LinePreview { get; set; }
    public DateTime SavedAtUtc { get; set; }

    [JsonIgnore]
    public string NodeType => IsReply ? "Reply" : "Entry";

    [JsonIgnore]
    public string Details => $"{Game} • {NodeType} • {LinePreview}";
}

public static class SavedDialogueNodeLibrary
{
    private static string LibraryFolder => Directory.CreateDirectory(
        Path.Combine(AppDirectories.AppDataFolder, "DialogueEditor", "SavedNodes")).FullName;

    private static string IndexPath => Path.Combine(LibraryFolder, "index.json");

    public static IReadOnlyList<SavedDialogueNode> Load()
    {
        if (!File.Exists(IndexPath))
        {
            return [];
        }

        try
        {
            return JsonConvert.DeserializeObject<List<SavedDialogueNode>>(File.ReadAllText(IndexPath))?
                .Where(item => item != null && item.Id != Guid.Empty && File.Exists(GetSnapshotPath(item)))
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string GetSnapshotPath(SavedDialogueNode item) =>
        Path.Combine(LibraryFolder, $"{item.Id:N}.pcc");

    public static SavedDialogueNode Capture(
        ConversationExtended conversation,
        DialogueNodeExtended node,
        string name)
    {
        int nodeIndex = node.IsReply
            ? conversation.ReplyList.IndexOf(node)
            : conversation.EntryList.IndexOf(node);
        if (nodeIndex < 0)
        {
            throw new InvalidOperationException("The selected node is not part of the active conversation.");
        }

        var item = new SavedDialogueNode
        {
            Id = Guid.NewGuid(),
            Name = name,
            Game = conversation.Export.Game,
            IsReply = node.IsReply,
            NodeIndex = nodeIndex,
            LinePreview = string.IsNullOrWhiteSpace(node.Line) ? $"StrRef {node.LineStrRef}" : node.Line,
            SavedAtUtc = DateTime.UtcNow
        };

        string snapshotPath = GetSnapshotPath(item);
        try
        {
            EntryExporter.ExportExportToFile(conversation.Export, snapshotPath, out IEntry exportedConversation);
            item.ConversationUIndex = exportedConversation.UIndex;
            Add(item);
            return item;
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

    public static SavedDialogueNodeSnapshot OpenSnapshot(SavedDialogueNode item)
    {
        IMEPackage package = MEPackageHandler.OpenMEPackage(GetSnapshotPath(item));
        try
        {
            ExportEntry conversationExport = package.GetUExport(item.ConversationUIndex);
            var conversation = new ConversationExtended(conversationExport);
            conversation.LoadConversation(detailedParse: true);
            DialogueNodeExtended node = item.IsReply
                ? conversation.ReplyList.ElementAtOrDefault(item.NodeIndex)
                : conversation.EntryList.ElementAtOrDefault(item.NodeIndex);
            if (node == null)
            {
                throw new InvalidDataException("The saved dialogue node could not be found in its snapshot.");
            }
            return new SavedDialogueNodeSnapshot(package, conversation, node);
        }
        catch
        {
            package.Dispose();
            throw;
        }
    }

    public static void Add(SavedDialogueNode item)
    {
        var items = Load().ToList();
        items.Add(item);
        SaveIndex(items);
    }

public sealed class SavedDialogueNodeSnapshot(
    IMEPackage package,
    ConversationExtended conversation,
    DialogueNodeExtended node) : IDisposable
{
    public IMEPackage Package { get; } = package;
    public ConversationExtended Conversation { get; } = conversation;
    public DialogueNodeExtended Node { get; } = node;

    public void Dispose() => Package.Dispose();
}

    public static void Rename(SavedDialogueNode item, string name)
    {
        var items = Load().ToList();
        SavedDialogueNode storedItem = items.FirstOrDefault(candidate => candidate.Id == item.Id);
        if (storedItem == null)
        {
            return;
        }

        storedItem.Name = name;
        item.Name = name;
        SaveIndex(items);
    }

    public static void Delete(SavedDialogueNode item)
    {
        var items = Load().Where(candidate => candidate.Id != item.Id).ToList();
        SaveIndex(items);
        string snapshotPath = GetSnapshotPath(item);
        if (File.Exists(snapshotPath))
        {
            File.Delete(snapshotPath);
        }
    }

    private static void SaveIndex(IReadOnlyCollection<SavedDialogueNode> items)
    {
        string temporaryPath = IndexPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(items, Formatting.Indented));
        File.Move(temporaryPath, IndexPath, true);
    }
}
