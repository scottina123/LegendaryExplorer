using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using MessageBox = LegendaryExplorer.SharedUI.ThemedMessageBox;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// A set of level files opened together, as the recent sets remember it. Persisted as JSON in <c>AppData\LevelEditor\RECENTSETS</c> (upstream's file and
/// format; the fork groups the sets by game on its landing page)
/// </summary>
public class RecentFileSet
{
    public MEGame Game { get; set; }
    public List<string> FilePaths { get; set; } = [];
    public List<string> ReadOnlyFilePaths { get; set; } = [];

    [JsonIgnore]
    public string DisplayName => FilePaths.Count == 0
        ? "(empty)"
        : string.Join("\n", FilePaths.Select(Path.GetFileName));

    [JsonIgnore]
    public bool HasMultipleLevels => FilePaths.Count > 1;

    [JsonIgnore]
    public string TooltipText => string.Join("\n", FilePaths);
}

/// <summary>A tab of the fork's landing page: one game's recent sets, the pinned files, or the level presets</summary>
public class RecentFileSetGroup(MEGame? game, bool isPinned = false, bool isLevelPresets = false)
{
    public MEGame? Game { get; } = game;
    public bool IsPinned { get; } = isPinned;
    public bool IsLevelPresets { get; } = isLevelPresets;
    public string Header => IsLevelPresets ? "Level presets" : IsPinned ? "Pinned files" : Game?.ToString() ?? "Misc";
    public ObservableCollectionExtended<RecentFileSet> Items { get; } = [];
}

public partial class LevelEditor
{
    /// <summary>Each game keeps this many recent sets</summary>
    private const int RecentSetsPerGame = 10;

    public ObservableCollectionExtended<RecentFileSet> RecentSets { get; } = [];
    public RecentsControl PinnedFilesControl { get; } = new() { ShowRecentTabs = false };

    public IReadOnlyList<RecentFileSetGroup> RecentSetGroups { get; } =
    [
        new RecentFileSetGroup(MEGame.ME1),
        new RecentFileSetGroup(MEGame.ME2),
        new RecentFileSetGroup(MEGame.ME3),
        new RecentFileSetGroup(MEGame.LE1),
        new RecentFileSetGroup(MEGame.LE2),
        new RecentFileSetGroup(MEGame.LE3),
        new RecentFileSetGroup(null),
        new RecentFileSetGroup(null, isPinned: true),
        new RecentFileSetGroup(null, isLevelPresets: true)
    ];

    private RecentFileSetGroup _selectedRecentSetGroup;
    public RecentFileSetGroup SelectedRecentSetGroup
    {
        get => _selectedRecentSetGroup;
        set => SetProperty(ref _selectedRecentSetGroup, value);
    }

    private static string RecentSetsFile => Path.Combine(
        Directory.CreateDirectory(Path.Combine(AppDirectories.AppDataFolder, "LevelEditor")).FullName,
        "RECENTSETS");

    private bool pinnedControlInitialized;

    /// <summary>The pinned files (stored by the shared recents control under the "LevelEditor" tool name) and the Recent menu's pinned submenu</summary>
    private void EnsurePinnedControlInitialized()
    {
        if (pinnedControlInitialized) return;
        pinnedControlInitialized = true;
        SelectedRecentSetGroup ??= RecentSetGroups[0];
        PinnedFilesControl.InitPinnedControl("LevelEditor", path => _ = LoadFileAsync(path));
        PinnedFilesControl.SelectedRecentGroup = PinnedFilesControl.PinnedGroup;
        PinnedFilesControl.AttachPinFileMenu(Recents_MenuItem);
    }

    private void InitializeForkLandingPage()
    {
        EnsurePinnedControlInitialized();
        RefreshRecentsMenu();
    }

    private void DisposeForkLandingPage()
    {
        PinnedFilesControl.Dispose();
        if (LevelPresetsView is not null) LevelPresetStore.Shared.Presets.CollectionChanged -= LevelPresets_CollectionChanged;
    }

    private RecentFileSetGroup GetRecentSetGroup(RecentFileSet set)
    {
        MEGame? game = set.Game.IsMEGame() ? set.Game : null;
        return RecentSetGroups.First(group => !group.IsPinned && !group.IsLevelPresets && group.Game == game);
    }

    private void RefreshRecentSetGroups(RecentFileSet preferredSet = null)
    {
        foreach (var group in RecentSetGroups.Where(group => !group.IsPinned && !group.IsLevelPresets))
        {
            group.Items.ReplaceAll(RecentSets.Where(set => GetRecentSetGroup(set) == group));
        }

        if (preferredSet != null)
        {
            SelectedRecentSetGroup = GetRecentSetGroup(preferredSet);
        }
        else if (SelectedRecentSetGroup == null || (!SelectedRecentSetGroup.IsPinned && !SelectedRecentSetGroup.IsLevelPresets && !SelectedRecentSetGroup.Items.Any))
        {
            SelectedRecentSetGroup = RecentSets.Count > 0 ? GetRecentSetGroup(RecentSets[0]) : RecentSetGroups[0];
        }
    }

    private void LoadRecentSets()
    {
        EnsurePinnedControlInitialized();
        if (File.Exists(RecentSetsFile))
        {
            try
            {
                var json = File.ReadAllText(RecentSetsFile);
                var sets = JsonConvert.DeserializeObject<List<RecentFileSet>>(json);
                if (sets is not null)
                {
                    foreach (var set in sets)
                    {
                        set.FilePaths.RemoveAll(p => !File.Exists(p));
                        if (set.FilePaths.Count > 0)
                            RecentSets.Add(set);
                    }
                }
            }
            catch { /* corrupt file, ignore */ }
        }
        RefreshRecentsMenu(RecentSets.FirstOrDefault());
    }

    private void SaveRecentSets()
    {
        var json = JsonConvert.SerializeObject(RecentSets.ToList(), Formatting.Indented);
        File.WriteAllText(RecentSetsFile, json);
        RefreshRecentsMenu(RecentSets.FirstOrDefault());
    }

    private void RecordCurrentFilesAsRecent()
    {
        if (OpenFiles.Count == 0) return;
        var currentPaths = OpenFiles.Select(f => f.FilePath).ToList();

        for (int i = RecentSets.Count - 1; i >= 0; i--)
        {
            var existing = RecentSets[i].FilePaths;
            if (existing.Count > 0 && string.Equals(existing[0], currentPaths[0], StringComparison.OrdinalIgnoreCase))
            {
                RecentSets.RemoveAt(i);
            }
        }

        RecentSets.Insert(0, new RecentFileSet
        {
            Game = Game,
            FilePaths = currentPaths,
            ReadOnlyFilePaths = OpenFiles.Where(f => f.IsReadOnly).Select(f => f.FilePath).ToList()
        });

        // Each game keeps its own history, like the shared recent-items control
        var group = GetRecentSetGroup(RecentSets[0]);
        foreach (var olderSet in RecentSets.Where(set => GetRecentSetGroup(set) == group).Skip(RecentSetsPerGame).ToList())
        {
            RecentSets.Remove(olderSet);
        }

        SaveRecentSets();
    }

    private async void OpenRecentFileSet(RecentFileSet set) => await OpenRecentFileSetAsync(set);

    /// <summary>Opens a recent set (or a level preset's files) in place of the open files, then puts the camera at <paramref name="cameraPreset"/>, if given</summary>
    private async Task OpenRecentFileSetAsync(RecentFileSet set, LevelCameraPreset cameraPreset = null, bool closeConfirmed = false)
    {
        if (!closeConfirmed && !ConfirmCloseFiles("Open the new files anyway?")) return;
        string loadingPath = null;
        try
        {
            CloseAllFiles();

            using var guard = new RenderGuard(this);

            var paths = set.FilePaths.Where(File.Exists).ToList();
            for (int i = 0; i < paths.Count; i++)
            {
                string path = loadingPath = paths[i];
                await AddLevelFile(path, i + 1, paths.Count).ConfigureAwait(true);
                var openFile = OpenFiles.LastOrDefault(f => f.FilePath.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                if (openFile is not null && set.ReadOnlyFilePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    openFile.IsReadOnly = true;
            }

            if (cameraPreset != null && OpenFiles.Count > 0)
            {
                IsOrthographicView = false;
                cameraPreset.ApplyTo(RenderContext.Camera);
                UpdateCameraPositionText();
                UpdateCameraRotationText();
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Unable to load {Path.GetFileName(loadingPath) ?? "the level set"}:\n\n{exception.Message}",
                "Open levels", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshRecentsMenu(RecentFileSet preferredSet = null)
    {
        RefreshRecentSetGroups(preferredSet);
        if (Recents_MenuItem is null) return;
        Recents_MenuItem.Items.Clear();
        Recents_MenuItem.IsEnabled = true;
        if (pinnedControlInitialized) Recents_MenuItem.Items.Add(PinnedFilesControl.CreatePinnedMenu());
        if (RecentSets.Count > 0 && Recents_MenuItem.Items.Count > 0) Recents_MenuItem.Items.Add(new Separator());
        foreach (var set in RecentSets)
        {
            var mi = new MenuItem
            {
                Header = set.DisplayName.Replace("_", "__"),
                ToolTip = set.TooltipText,
                Tag = set,
                ContextMenu = CreateRecentSetPinContextMenu(set)
            };
            mi.Click += (_, _) => OpenRecentFileSet((RecentFileSet)mi.Tag);
            Recents_MenuItem.Items.Add(mi);
        }
        Recents_MenuItem.IsEnabled = Recents_MenuItem.Items.Count > 0;
    }

    private ContextMenu CreateRecentSetPinContextMenu(RecentFileSet set)
    {
        var menu = set.FilePaths.Count == 1
            ? PinnedFilesControl.CreatePinContextMenu(new RecentsControl.RecentItem(set.FilePaths[0], set.Game))
            : new ContextMenu();
        if (set.FilePaths.Count > 1)
        {
            foreach (string path in set.FilePaths)
            {
                var item = new RecentsControl.RecentItem(path, set.Game);
                var pin = new MenuItem { ToolTip = path };
                menu.Opened += (_, _) => pin.Header = $"{(PinnedFilesControl.IsPinned(path) ? "Unpin" : "Pin")} {Path.GetFileName(path).Replace("_", "__")}";
                pin.Click += (_, _) => PinnedFilesControl.TogglePinItem(item);
                menu.Items.Add(pin);
            }
            menu.Items.Add(new Separator());
            FileReferenceMenu.AddActions(menu, set.FilePaths);
        }
        menu.Items.Add(new Separator());
        var savePreset = new MenuItem { Header = "Save as level preset..." };
        savePreset.Click += (_, _) => ShowLevelPresets(set.Game, set.FilePaths, set.ReadOnlyFilePaths);
        menu.Items.Add(savePreset);
        return menu;
    }

    private void RecentSetButton_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is Button { DataContext: RecentFileSet set } button)
            button.ContextMenu = CreateRecentSetPinContextMenu(set);
    }

    #region Level presets

    public ICollectionView LevelPresetsView { get; private set; }
    public string LevelPresetsLoadError => LevelPresetStore.Shared.LoadError;

    private Guid? _selectedLevelPresetId;
    private bool _openingLevelPreset;
    private LevelPreset _selectedLevelPreset;
    public LevelPreset SelectedLevelPreset
    {
        get => _selectedLevelPreset;
        set
        {
            if (SetProperty(ref _selectedLevelPreset, value) && value != null)
                _selectedLevelPresetId = value.Id;
        }
    }

    private string _levelPresetSearchText = "";
    public string LevelPresetSearchText
    {
        get => _levelPresetSearchText;
        set
        {
            if (SetProperty(ref _levelPresetSearchText, value))
            {
                LevelPresetsView?.Refresh();
                RestoreLevelPresetSelection();
            }
        }
    }

    private ICollectionView CreateLevelPresetsView()
    {
        var view = new CollectionViewSource { Source = LevelPresetStore.Shared.Presets }.View;
        view.Filter = item => item is LevelPreset preset && LevelPresetStore.MatchesSearch(preset, LevelPresetSearchText);
        view.SortDescriptions.Add(new SortDescription(nameof(LevelPreset.Name), ListSortDirection.Ascending));
        LevelPresetStore.Shared.Presets.CollectionChanged += LevelPresets_CollectionChanged;
        return view;
    }

    private void LevelPresets_Click(object sender, RoutedEventArgs e) => ShowLevelPresets();

    private void SaveLevelPreset_Click(object sender, RoutedEventArgs e) => ShowLevelPresets(
        Game, OpenFiles.Select(file => file.FilePath), OpenFiles.Where(file => file.IsReadOnly).Select(file => file.FilePath));

    private void ShowLevelPresets(MEGame? game = null, IEnumerable<string> currentFiles = null,
        IEnumerable<string> readOnlyFiles = null, LevelPreset selectedPreset = null)
    {
        var dialog = new LevelPresetsDialog(game, currentFiles, readOnlyFiles,
            currentCamera: HasAnyFileOpen ? LevelCameraPreset.FromCamera(RenderContext.Camera) : null) { Owner = this };
        if (selectedPreset != null)
        {
            dialog.SelectedPreset = selectedPreset;
            dialog.ShowCameraLocations();
        }
        if (dialog.ShowDialog() == true && dialog.SelectedPreset is { } preset)
            OpenLevelPreset(preset, missingFilesReported: true, cameraPreset: dialog.SelectedCameraPreset);
    }

    private void LevelPresetCameraLocations_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LevelPreset preset })
            ShowLevelPresets(preset.Game, selectedPreset: preset);
    }

    private void OpenLevelPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LevelPreset preset })
        {
            SelectedLevelPreset = preset;
            OpenLevelPreset(preset);
        }
    }

    private void LevelPresetButton_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is Button { DataContext: LevelPreset preset } button)
        {
            var menu = FileReferenceMenu.Create(preset.FilePaths);
            menu.Items.Add(new Separator());
            var cameraLocations = new MenuItem { Header = "Camera locations..." };
            cameraLocations.Click += (_, _) => ShowLevelPresets(preset.Game, selectedPreset: preset);
            menu.Items.Add(cameraLocations);
            button.ContextMenu = menu;
        }
    }

    private async void OpenLevelPreset(LevelPreset preset, bool missingFilesReported = false, LevelCameraPreset cameraPreset = null)
    {
        if (_openingLevelPreset || IsBusy) return;
        _openingLevelPreset = true;
        try
        {
            if (!missingFilesReported && !LevelPresetsDialog.ReportMissingFiles(this, preset)) return;
            var availablePaths = preset.FilePaths.Where(File.Exists).ToList();
            if (availablePaths.Count == 0) return;
            await LevelPresetsDialog.ValidateFilesAsync(availablePaths, preset.Game);

            if (!ConfirmCloseFiles("Open the preset and discard these changes?")) return;

            await OpenRecentFileSetAsync(new RecentFileSet
            {
                Game = preset.Game,
                FilePaths = preset.FilePaths.ToList(),
                ReadOnlyFilePaths = preset.ReadOnlyFilePaths.ToList()
            }, cameraPreset, closeConfirmed: true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Unable to open '{preset.Name}':\n\n{exception.Message}\n\nEdit the preset to update or remove these files.",
                "Level preset", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _openingLevelPreset = false;
        }
    }

    private void LevelPresets_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        // ReplaceAll produces a reset when a preset is edited; select its replacement by its stable ID
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(RestoreLevelPresetSelection));
    }

    private void RestoreLevelPresetSelection()
    {
        SelectedLevelPreset = LevelPresetStore.Shared.Presets.FirstOrDefault(preset =>
            preset.Id == _selectedLevelPresetId && LevelPresetStore.MatchesSearch(preset, LevelPresetSearchText));
    }

    #endregion
}
