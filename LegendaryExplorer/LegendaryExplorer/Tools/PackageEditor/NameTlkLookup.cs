using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Tools.PackageEditor;

/// <summary>
/// Resolves dialogue IDs in name-table entries without treating arbitrary object numbers as TLK IDs.
/// A lookup is scoped to one package and one refresh of its names.
/// </summary>
internal sealed partial class NameTlkLookup(Func<int, string> resolveStringRef)
{
    private readonly Dictionary<int, string> _resolvedText = [];

    public NameTlkLookup(IMEPackage package) : this(id => package.Game.IsOTGame() || package.Game.IsLEGame()
        ? TLKManagerWPF.GlobalFindStrRefbyID(id, package)
        : null)
    {
    }

    // VO events/ME1 audio and gender-suffixed stream/FaceFX names, including zero-padded IDs.
    [GeneratedRegex(@"(?:^|[_:.])VO_(?<id>[0-9]+)(?=[_,.]|$)|(?:^|[_:,])(?<id>[0-9]+)_[fm](?=[_,.]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DialogueNamePattern();

    public IndexedName CreateName(int index, string name)
    {
        string text = null;
        Match match = DialogueNamePattern().Match(name);
        if (match.Success && int.TryParse(match.Groups["id"].Value, out int id) && id > 0)
        {
            if (!_resolvedText.TryGetValue(id, out text))
            {
                text = resolveStringRef(id);
                // The standard TLK lookup wraps resolved strings in quotes.
                if (string.IsNullOrWhiteSpace(text) || text.Equals("No Data", StringComparison.OrdinalIgnoreCase))
                {
                    text = null;
                }
                else if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
                {
                    text = text[1..^1];
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        text = null;
                    }
                }

                _resolvedText[id] = text;
            }
        }

        return new IndexedName(index, name) { TlkText = text };
    }

    public static bool MatchesSearch(IndexedName name, string searchTerm) =>
        name.Name.Contains(searchTerm, StringComparison.InvariantCultureIgnoreCase)
        || name.TlkText?.Contains(searchTerm, StringComparison.InvariantCultureIgnoreCase) == true;
}
