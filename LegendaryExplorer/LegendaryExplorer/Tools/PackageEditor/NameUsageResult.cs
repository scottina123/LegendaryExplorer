using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Tools.PackageEditor;

/// <summary>
/// Keeps the navigation path separate from the dialogue displayed in a name usage result.
/// </summary>
internal sealed class NameUsageResult : EntryStringPair
{
    public string UsageDetail { get; }

    public NameUsageResult(IEntry entry, string usageDetail, string tlkText)
        : base(entry, $"#{entry.UIndex} {entry.ObjectName.Instanced}: {usageDetail}")
    {
        UsageDetail = usageDetail;
        if (!string.IsNullOrWhiteSpace(tlkText))
        {
            Message += $"\n{tlkText}";
        }
    }
}
