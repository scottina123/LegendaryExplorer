using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Tools.PackageEditor;

public partial class PackageEditorWindow
{
    private CancellationTokenSource _liveFilterCancellation;
    private Dictionary<TreeViewEntry, bool> _expansionBeforeLiveFilter;
    private bool _liveFilteringInitialized;
    private IReadOnlySet<object> _liveFilterMatches;

    // Keep the underlying lists intact: their positions are used as package indices by navigation.
    public IReadOnlySet<object> LiveFilterMatches
    {
        get => _liveFilterMatches;
        private set => SetProperty(ref _liveFilterMatches, value);
    }

    private void LiveFilter_TextChanged(object sender, TextChangedEventArgs e) => ScheduleLiveFilter();

    private void LiveFilterSettingChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Settings.PackageEditor_LiveFiltering))
        {
            if (Dispatcher.CheckAccess())
                ScheduleLiveFilter();
            else
                Dispatcher.BeginInvoke(new Action(ScheduleLiveFilter));
        }
    }

    private async void ScheduleLiveFilter() => await UpdateLiveFilterAsync();

    private void CancelLiveFilter()
    {
        var cancellation = _liveFilterCancellation;
        _liveFilterCancellation = null;
        cancellation?.Cancel();
    }

    internal async Task UpdateLiveFilterAsync(bool debounce = true)
    {
        CancelLiveFilter();
        if (!_liveFilteringInitialized)
            return;

        string objectName = Search_TextBox.Text.Trim();
        string indexText = Goto_TextBox.Text.Trim();
        string stringRefText = StringRefSearchText?.Trim() ?? string.Empty;
        if (!Settings.PackageEditor_LiveFiltering || Pcc == null || IsLoadingFile
            || (objectName.Length == 0 && indexText.Length == 0 && stringRefText.Length == 0))
        {
            bool wasFiltering = LiveFilterMatches != null;
            LiveFilterMatches = null;
            if (wasFiltering)
                ApplyTreeViewEditedFilter();
            return;
        }

        CancelEntrySearch();
        var cancellation = new CancellationTokenSource();
        _liveFilterCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        IMEPackage package = Pcc;
        CurrentViewMode view = CurrentView;
        try
        {
            // Coalesce keystrokes, pastes, and package updates before parsing any exports.
            if (debounce)
                await Task.Delay(250, token);

            int? index = int.TryParse(indexText, out int parsedIndex) ? parsedIndex : null;
            int? stringRef = int.TryParse(stringRefText, out int parsedStringRef) ? parsedStringRef : null;
            var matches = new HashSet<object>();
            var resolvedText = new Dictionary<int, string>();
            var usages = new List<EntryStringPair>();
            var batchTime = Stopwatch.StartNew();
            // Snapshot the collection because package updates can arrive while yielding to the UI.
            object[] candidates = view == CurrentViewMode.Names
                ? NamesList.Cast<object>().ToArray()
                : package.Exports.Cast<object>().Concat(package.Imports).ToArray();
            foreach (object candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(package, Pcc) || view != CurrentView)
                    return;

                bool matchesQuery = false;
                if (candidate is IndexedName name)
                {
                    matchesQuery = (indexText.Length == 0 || name.Index == index)
                        && (objectName.Length == 0 || NameTlkLookup.MatchesSearch(name, objectName))
                        && (stringRefText.Length == 0 || (stringRef.HasValue
                            ? NameTlkLookup.GetStringRef(name.Name) == stringRef
                            : name.TlkText?.Contains(stringRefText, StringComparison.OrdinalIgnoreCase) == true));
                }
                else if (candidate is IEntry entry)
                {
                    matchesQuery = (indexText.Length == 0 || entry.UIndex == index)
                        && entry.ObjectName.Instanced.Contains(objectName, StringComparison.InvariantCultureIgnoreCase);
                    if (matchesQuery && stringRefText.Length > 0)
                    {
                        usages.Clear();
                        if (entry is ExportEntry export)
                            CollectExportStringRefUsages(export, usages, stringRefText, stringRef, resolvedText, token);
                        matchesQuery = usages.Count > 0;
                    }
                }

                if (matchesQuery)
                    matches.Add(candidate);

                // Stay on the UI thread to avoid reading properties concurrently with package edits.
                if (batchTime.ElapsedMilliseconds >= 10)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    batchTime.Restart();
                }
            }

            token.ThrowIfCancellationRequested();
            if (ReferenceEquals(package, Pcc) && view == CurrentView)
            {
                LiveFilterMatches = matches;
                ApplyTreeViewEditedFilter();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer query, a package change, or closing the window superseded this scan.
        }
        finally
        {
            if (ReferenceEquals(_liveFilterCancellation, cancellation))
                _liveFilterCancellation = null;
            cancellation.Dispose();
        }
    }

    private bool MatchesLiveFilter(object item) => LiveFilterMatches == null || LiveFilterMatches.Contains(item);

    private void PreserveLiveFilterExpansion()
    {
        if (LiveFilterMatches != null)
        {
            _expansionBeforeLiveFilter ??= [];
            foreach (TreeViewEntry node in AllTreeViewNodesX[0].FlattenTree())
                _expansionBeforeLiveFilter.TryAdd(node, node.IsExpanded);
        }
        else if (_expansionBeforeLiveFilter != null)
        {
            foreach (var (node, expanded) in _expansionBeforeLiveFilter)
                node.IsExpanded = expanded;
            _expansionBeforeLiveFilter = null;
        }
    }
}
