using System;
using System.Collections.Generic;
using System.Linq;
using LegendaryExplorerCore.Helpers;

namespace LegendaryExplorer.MainWindow;

/// <summary>Alphabetical launcher pages, shared by browsing and global search.</summary>
internal sealed class ToolMenuPages
{
    public const int PageSize = 12;
    private readonly Tool[] tools;
    public int Count => tools.Length;
    public int PageCount => Math.Max(1, (Count + PageSize - 1) / PageSize);

    public ToolMenuPages(IEnumerable<Tool> source, string search = "")
    {
        string[] words = (search ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        tools = source.Where(tool => tool.open != null && words.All(word =>
                tool.Abbreviation.Contains(word, StringComparison.OrdinalIgnoreCase)
                || tool.DisplayName.Contains(word, StringComparison.OrdinalIgnoreCase)
                || tool.name.Contains(word, StringComparison.OrdinalIgnoreCase)
                || (tool.tags?.FuzzyMatch(word) ?? false)
                || tool.DisplayName.ToLowerInvariant().Split(' ').FuzzyMatch(word)))
            .OrderBy(tool => tool.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(tool => tool.name, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<Tool> GetPage(int index) =>
        tools.Skip(Math.Clamp(index, 0, PageCount - 1) * PageSize).Take(PageSize).ToArray();
}
