using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace LegendaryExplorer.Tools.AssetDatabase;

/// <summary>Immutable searchable text, built and queried without touching WPF or live database collections.</summary>
internal sealed class LineSearchIndex
{
    internal sealed record SourceLine(ConvoLine Source, int StringRef, string Speaker, string Text, string Conversation);
    internal sealed record SourceConversation(int FileKey, string OwnerName);
    private sealed record Entry(SourceLine Line, string SpeakerDisplay, string SpeakerFilter, string StringRef, string Files, string Locations);

    private readonly Entry[] entries;
    private readonly Dictionary<ConvoLine, Entry> byLine;

    private LineSearchIndex(Entry[] entries)
    {
        this.entries = entries;
        byLine = entries.ToDictionary(entry => entry.Line.Source);
    }

    public static LineSearchIndex Build(SourceLine[] lines, AssetDatabaseWindow.FileDirPair[] files,
        IReadOnlyDictionary<string, SourceConversation> conversations, IReadOnlyDictionary<int, TlkUsage[]> usages,
        CancellationToken cancellationToken)
    {
        var entries = new Entry[lines.Length];
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        for (int i = 0; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceLine line = lines[i];
            SourceConversation conversation = null;
            if (!string.IsNullOrEmpty(line.Conversation)) conversations.TryGetValue(line.Conversation, out conversation);
            string owner = string.Equals(line.Speaker, "Owner", StringComparison.OrdinalIgnoreCase)
                ? conversation?.OwnerName : null;
            bool hasOwner = !string.IsNullOrWhiteSpace(owner);
            var keys = new HashSet<int>();
            if (line.StringRef > 0 && usages.TryGetValue(line.StringRef, out TlkUsage[] lineUsages))
            {
                foreach (TlkUsage usage in lineUsages)
                    if (usage.FileKey >= 0 && usage.FileKey < files.Length) keys.Add(usage.FileKey);
            }
            // The conversation already provides the file index: no full file-list search is necessary.
            if (keys.Count == 0 && conversation != null && conversation.FileKey >= 0 && conversation.FileKey < files.Length)
                keys.Add(conversation.FileKey);

            var usageFiles = keys.Select(key => files[key]).OrderBy(file => file.FileName, comparer)
                .ThenBy(file => file.Directory, comparer).ToArray();
            entries[i] = new Entry(line,
                hasOwner ? $"{line.Speaker} ({owner})" : line.Speaker,
                hasOwner ? owner : line.Speaker, line.StringRef.ToString(CultureInfo.CurrentCulture),
                JoinValues(usageFiles.Select(file => file.FileName), comparer),
                JoinValues(usageFiles.Select(file => file.Directory), comparer));
        }
        return new LineSearchIndex(entries);
    }

    private static string JoinValues(IEnumerable<string> values, StringComparer comparer) =>
        string.Join(", ", values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(comparer));

    public HashSet<ConvoLine> Search(string text, string column, string speaker, CancellationToken cancellationToken)
    {
        var matches = new HashSet<ConvoLine>();
        bool hasText = !string.IsNullOrWhiteSpace(text);
        foreach (Entry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (speaker != null && !EqualsText(entry.Line.Speaker, speaker)
                && !EqualsText(entry.SpeakerDisplay, speaker) && !EqualsText(entry.SpeakerFilter, speaker)) continue;
            if (!hasText || Matches(entry, text, column)) matches.Add(entry.Line.Source);
        }
        return matches;
    }

    private static bool EqualsText(string left, string right) => string.Equals(left, right, StringComparison.CurrentCultureIgnoreCase);
    private static bool Contains(string value, string text) => !string.IsNullOrEmpty(value)
        && value.Contains(text, StringComparison.CurrentCultureIgnoreCase);

    private static bool Matches(Entry entry, string text, string column) => column switch
    {
        "Speaker" => Contains(entry.SpeakerDisplay, text),
        "TLK String Ref" => Contains(entry.StringRef, text),
        "Line" => Contains(entry.Line.Text, text),
        "Line Conversation" => Contains(entry.Line.Conversation, text),
        "File" => Contains(entry.Files, text),
        "Location" => Contains(entry.Locations, text),
        _ => Contains(entry.SpeakerDisplay, text) || Contains(entry.StringRef, text)
             || Contains(entry.Line.Text, text) || Contains(entry.Line.Conversation, text)
             || Contains(entry.Files, text) || Contains(entry.Locations, text)
    };

    public bool TryGetUsageText(ConvoLine line, out string files, out string locations)
    {
        if (line != null && byLine.TryGetValue(line, out Entry entry))
        {
            files = entry.Files;
            locations = entry.Locations;
            return true;
        }
        files = locations = null;
        return false;
    }
}
