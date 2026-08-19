using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using LegendaryExplorer.Dialogs;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.TLK.ME1;
using LegendaryExplorerCore.TLK.ME2ME3;

namespace LegendaryExplorer.Misc
{
    internal static class TlkStringRefSelector
    {
        public static int? SelectStringRef(Window owner, IMEPackage package)
        {
            if (package is null)
            {
                return null;
            }

            return SelectStringRef(owner, package.Game, package.LocalTalkFiles);
        }

        public static IReadOnlyList<int> FindStringRefs(IMEPackage package, string searchText)
        {
            if (package is null || string.IsNullOrWhiteSpace(searchText))
            {
                return [];
            }

            if (int.TryParse(searchText, out int stringRef))
            {
                return [stringRef];
            }

            return FindMatches(package.Game, package.LocalTalkFiles, searchText)
                .Select(match => match.StringRef)
                .Distinct()
                .ToList();
        }

        public static int? SelectStringRef(Window owner, MEGame game)
        {
            return game == MEGame.Unknown ? null : SelectStringRef(owner, game, []);
        }

        private static int? SelectStringRef(Window owner, MEGame game, IEnumerable<ME1TalkFile> localTalkFiles)
        {
            return EntrySelector.SearchForItem(owner,
                searchText => FindMatches(game, localTalkFiles, searchText),
                "Search for and select the TLK string reference to apply:",
                "Enter an exact string ID or TLK text, then press Enter or Search",
                "TLK StringRef Picker")?.StringRef;
        }

        private static List<TlkTextMatch> FindMatches(MEGame game, IEnumerable<ME1TalkFile> localTalkFiles, string searchText)
        {
            var matches = new List<TlkTextMatch>();
            bool searchByStringRef = int.TryParse(searchText, out int searchedStringRef);

            void AddME1Matches(IEnumerable<ME1TalkFile> talkFiles)
            {
                foreach (ME1TalkFile talkFile in talkFiles.Distinct())
                {
                    string source = $"{Path.GetFileName(talkFile.FilePath)} -> {talkFile.BioTlkSetName}.{talkFile.Name}";
                    matches.AddRange(talkFile.StringRefs
                        .Where(stringRef => searchByStringRef
                            ? stringRef.StringID == searchedStringRef
                            : stringRef.Data?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true)
                        .Select(stringRef => new TlkTextMatch(stringRef.StringID, source, stringRef.Data)));
                }
            }

            void AddLazyMatches(IEnumerable<ME2ME3LazyTLK> talkFiles)
            {
                foreach (ME2ME3LazyTLK talkFile in talkFiles)
                {
                    if (searchByStringRef)
                    {
                        string text = talkFile.FindDataById(searchedStringRef, returnNullIfNotFound: true, noQuotes: true);
                        if (text is not null)
                        {
                            matches.Add(new TlkTextMatch(searchedStringRef, talkFile.FileName, text));
                        }
                    }
                    else
                    {
                        matches.AddRange(talkFile.FindIdsByData(searchText, StringComparison.OrdinalIgnoreCase)
                            .Select(stringRef => new TlkTextMatch(stringRef, talkFile.FileName,
                                talkFile.FindDataById(stringRef, returnNullIfNotFound: true, noQuotes: true))));
                    }
                }
            }

            switch (game)
            {
                case MEGame.ME1:
                    AddME1Matches(localTalkFiles.Concat(ME1TalkFiles.LoadedTlks));
                    break;
                case MEGame.ME2:
                    AddLazyMatches(ME2TalkFiles.LoadedTlks);
                    break;
                case MEGame.ME3:
                    AddLazyMatches(ME3TalkFiles.LoadedTlks);
                    break;
                case MEGame.LE1:
                    AddME1Matches(localTalkFiles.Concat(LE1TalkFiles.LoadedTlks));
                    break;
                case MEGame.LE2:
                    AddLazyMatches(LE2TalkFiles.LoadedTlks);
                    break;
                case MEGame.LE3:
                    AddLazyMatches(LE3TalkFiles.LoadedTlks);
                    break;
            }

            return matches.DistinctBy(match => (match.StringRef, match.Source)).ToList();
        }

        private sealed record TlkTextMatch(int StringRef, string Source, string Text)
        {
            public override string ToString() => $"{StringRef}: {Source} — {Text?.ReplaceLineEndings(" ")}";
        }
    }
}
