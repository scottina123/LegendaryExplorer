using LegendaryExplorerCore.Misc;

namespace LegendaryExplorerCore.Packages.CloningImportingAndRelinking
{
    public enum ReferenceIssueLocation
    {
        Entry,
        Property,
        Binary,
        Header
    }

    /// <summary>
    /// A reference-check result with a navigation target independent of its localized message.
    /// Data offsets are relative to the beginning of the export data; header offsets are relative to the header.
    /// </summary>
    public sealed class ReferenceIssue(IEntry entry, string message, ReferenceIssueLocation location,
        int? offset = null, int? valueOffset = null, int? referencedUIndex = null) : EntryStringPair(entry, message)
    {
        public ReferenceIssueLocation Location { get; } = location;
        public int? Offset { get; } = offset;
        public int? ValueOffset { get; } = valueOffset;
        public int? ReferencedUIndex { get; } = referencedUIndex;
    }
}
