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
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

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

            var prompt = new PromptDialog("Enter text to find anywhere in a loaded TLK string:", "Find StringRef by Text", selectText: true)
            {
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            if (prompt.ShowDialog() != true)
            {
                return null;
            }

            string searchText = prompt.ResponseText;
            if (string.IsNullOrWhiteSpace(searchText))
            {
                MessageBox.Show(owner, "Enter text to search for.", "TLK Text Search", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            return SelectStringRef(owner, game, localTalkFiles, searchText);
        }

        private static int? SelectStringRef(Window owner, MEGame game, IEnumerable<ME1TalkFile> localTalkFiles, string searchText)
        {
            List<TlkTextMatch> matches = FindMatches(game, localTalkFiles, searchText);
            if (matches.Count == 0)
            {
                MessageBox.Show(owner, "That text was not found in any loaded TLK for this game. Try another search.", "TLK Text Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            return EntrySelector.GetItem(owner, matches, "Select the TLK string reference to apply:",
                searchHelpText: "Filter results by string ID, TLK name, or text")?.StringRef;
        }

        private static List<TlkTextMatch> FindMatches(MEGame game, IEnumerable<ME1TalkFile> localTalkFiles, string searchText)
        {
            var matches = new List<TlkTextMatch>();

            void AddME1Matches(IEnumerable<ME1TalkFile> talkFiles)
            {
                foreach (ME1TalkFile talkFile in talkFiles.Distinct())
                {
                    string source = $"{Path.GetFileName(talkFile.FilePath)} -> {talkFile.BioTlkSetName}.{talkFile.Name}";
                    matches.AddRange(talkFile.StringRefs
                        .Where(stringRef => stringRef.Data?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true)
                        .Select(stringRef => new TlkTextMatch(stringRef.StringID, source, stringRef.Data)));
                }
            }

            void AddLazyMatches(IEnumerable<ME2ME3LazyTLK> talkFiles)
            {
                foreach (ME2ME3LazyTLK talkFile in talkFiles)
                {
                    matches.AddRange(talkFile.FindIdsByData(searchText, StringComparison.OrdinalIgnoreCase)
                        .Select(stringRef => new TlkTextMatch(stringRef, talkFile.FileName,
                            talkFile.FindDataById(stringRef, returnNullIfNotFound: true, noQuotes: true))));
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
            public override string ToString() => $"{StringRef}: {Source} — {Text.ReplaceLineEndings(" ")}";
        }
    }
}
