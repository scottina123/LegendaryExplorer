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
using LegendaryExplorer.Misc;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using Microsoft.Win32;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Dialogs;

public sealed record LevelPresetFile(string Path, bool IsReadOnly)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Status => !File.Exists(Path) ? "Missing file" : IsReadOnly ? "Read only" : "";
}

/// <summary>Manages the level library used by the Level Editor and 3D previews.</summary>
public partial class LevelPresetsDialog : NotifyPropertyChangedWindowBase
{
    private readonly LevelPresetStore store;
    private readonly MEGame? gameFilter;
    private readonly List<string> currentFiles;
    private readonly List<string> currentReadOnlyFiles;
    private Guid? selectedId;
    private bool busy;
    private bool closed;

    public ICollectionView PresetView { get; }
    public ObservableCollectionExtended<LevelPresetFile> PresetFiles { get; } = [];

    private LevelPreset selectedPreset;
    public LevelPreset SelectedPreset
    {
        get => selectedPreset;
        set
        {
            if (!SetProperty(ref selectedPreset, value)) return;
            if (value != null) selectedId = value.Id;
            EditorName = value?.Name ?? "";
            RefreshFiles();
            UpdateButtons();
        }
    }

    private string presetSearchText = "";
    public string PresetSearchText
    {
        get => presetSearchText;
        set
        {
            if (SetProperty(ref presetSearchText, value ?? "")) PresetView.Refresh();
        }
    }

    private string editorName = "";
    public string EditorName
    {
        get => editorName;
        set { if (SetProperty(ref editorName, value ?? "")) UpdateButtons(); }
    }

    private string statusText;
    public string StatusText { get => statusText; set => SetProperty(ref statusText, value); }

    public LevelPresetsDialog(MEGame? game = null, IEnumerable<string> currentFiles = null,
        IEnumerable<string> readOnlyFiles = null, LevelPresetStore store = null)
    {
        this.store = store ?? LevelPresetStore.Shared;
        gameFilter = game is null or MEGame.Unknown ? null : game;
        this.currentFiles = currentFiles?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        currentReadOnlyFiles = readOnlyFiles?.ToList() ?? [];
        PresetView = new ListCollectionView(this.store.Presets)
        {
            Filter = item => item is LevelPreset preset && (gameFilter == null || preset.Game == gameFilter)
                && LevelPresetStore.MatchesSearch(preset, PresetSearchText)
        };
        InitializeComponent();
        StatusText = this.store.LoadError ?? "Create a named preset from files, then open all its levels together. File changes are saved immediately.";
        CollectionChangedEventManager.AddHandler(this.store.Presets, PresetsChanged);
        Closed += (_, _) =>
        {
            closed = true;
            CollectionChangedEventManager.RemoveHandler(this.store.Presets, PresetsChanged);
        };
        SelectedPreset = PresetView.Cast<LevelPreset>().FirstOrDefault();
        RefreshFiles();
        UpdateButtons();
    }

    private void PresetsChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        PresetView.Refresh();
        SelectedPreset = PresetView.Cast<LevelPreset>().FirstOrDefault(preset => preset.Id == selectedId)
            ?? PresetView.Cast<LevelPreset>().FirstOrDefault();
    }

    private void RefreshFiles()
    {
        var readOnly = (SelectedPreset?.ReadOnlyFilePaths ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        PresetFiles.ReplaceAll(SelectedPreset?.FilePaths.Select(path => new LevelPresetFile(path, readOnly.Contains(path)))
            ?? Enumerable.Empty<LevelPresetFile>());
    }

    private void PresetContextMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LevelPreset preset } element)
            element.ContextMenu = FileReferenceMenu.Create(preset.FilePaths);
    }

    private void FileContextMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LevelPresetFile file } element)
            element.ContextMenu = FileReferenceMenu.CreateForFile(file.Path);
    }

    private void UpdateButtons()
    {
        if (NewPresetButton == null) return;
        bool writable = !busy && store.LoadError == null;
        bool selected = SelectedPreset != null;
        NewPresetButton.IsEnabled = writable;
        SaveCurrentLevelsButton.IsEnabled = writable && currentFiles.Count > 0;
        SaveNameButton.IsEnabled = writable && selected && !string.IsNullOrWhiteSpace(EditorName)
            && EditorName.Trim() != SelectedPreset.Name;
        AddFilesButton.IsEnabled = writable && selected;
        RemoveFilesButton.IsEnabled = writable && selected;
        DeletePresetButton.IsEnabled = writable && selected;
        OpenPresetButton.IsEnabled = !busy && selected;
        PresetsList.IsEnabled = !busy;
        PresetSearchTextBox.IsEnabled = !busy;
    }

    private string[] PickFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add levels to a preset",
            Filter = GameFileFilters.OpenFileFilter,
            Multiselect = true,
            CheckFileExists = true
        };
        return DirectoryMemory.ShowDialog(dialog, this) == true ? dialog.FileNames : [];
    }

    private string AskName(string suggestedName) => PromptDialog.Prompt(this, "Preset name:", "Save Level Preset", suggestedName,
        selectText: true, validator: name =>
        {
            if (string.IsNullOrWhiteSpace(name)) return (false, "Enter a name for this preset.");
            return store.Presets.Any(preset => string.Equals(preset.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                ? (false, "A preset with this name already exists.") : (true, null);
        })?.Trim();

    private async void NewPreset_Click(object sender, RoutedEventArgs e)
    {
        string[] paths = PickFiles();
        if (paths.Length == 0) return;
        string name = AskName(System.IO.Path.GetFileNameWithoutExtension(paths[0]));
        if (name == null) return;
        await RunOperation(async () =>
        {
            MEGame game = await ValidateFilesAsync(paths, gameFilter);
            if (closed) return;
            SelectSaved(store.Save(new LevelPreset { Name = name, Game = game, FilePaths = paths.ToList() }));
        });
    }

    private async void SaveCurrentLevels_Click(object sender, RoutedEventArgs e)
    {
        string name = AskName(System.IO.Path.GetFileNameWithoutExtension(currentFiles[0]));
        if (name == null) return;
        await RunOperation(async () =>
        {
            MEGame game = await ValidateFilesAsync(currentFiles, gameFilter);
            if (closed) return;
            SelectSaved(store.Save(new LevelPreset
            {
                Name = name, Game = game, FilePaths = currentFiles.ToList(), ReadOnlyFilePaths = currentReadOnlyFiles.ToList()
            }));
        });
    }

    private async void SaveName_Click(object sender, RoutedEventArgs e) => await RunOperation(() =>
    {
        SelectSaved(store.Save(SelectedPreset with { Name = EditorName }));
        return Task.CompletedTask;
    });

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        string[] paths = PickFiles();
        if (paths.Length == 0) return;
        LevelPreset preset = SelectedPreset;
        string name = EditorName;
        await RunOperation(async () =>
        {
            await ValidateFilesAsync(paths, preset.Game);
            if (closed) return;
            SelectSaved(store.Save(preset with { Name = name, FilePaths = preset.FilePaths.Concat(paths).ToList() }));
        });
    }

    private async void RemoveFiles_Click(object sender, RoutedEventArgs e)
    {
        var removed = PresetFilesList.SelectedItems.Cast<LevelPresetFile>().Select(file => file.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (removed.Count == 0) return;
        await RunOperation(() =>
        {
            SelectSaved(store.Save(SelectedPreset with
            {
                Name = EditorName,
                FilePaths = SelectedPreset.FilePaths.Where(path => !removed.Contains(path)).ToList()
            }));
            return Task.CompletedTask;
        });
    }

    private async void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Delete the level preset '{SelectedPreset.Name}'?", "Delete level preset",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunOperation(() =>
        {
            store.Delete(SelectedPreset);
            selectedId = null;
            SelectedPreset = PresetView.Cast<LevelPreset>().FirstOrDefault();
            StatusText = "Preset deleted.";
            return Task.CompletedTask;
        });
    }

    private async void OpenPreset_Click(object sender, RoutedEventArgs e)
    {
        LevelPreset preset = SelectedPreset;
        await RunOperation(async () =>
        {
            if (!ReportMissingFiles(this, preset))
            {
                StatusText = "No files in this preset are currently available. Add replacement files to open it.";
                return;
            }
            await ValidateFilesAsync(GetAvailableFiles(preset), preset.Game);
            if (!closed) DialogResult = true;
        });
    }

    public static IReadOnlyList<string> GetAvailableFiles(LevelPreset preset)
        => preset.FilePaths.Where(File.Exists).ToArray();

    /// <summary>Reports unavailable files without removing them from the saved preset.</summary>
    public static bool ReportMissingFiles(Window owner, LevelPreset preset)
    {
        string[] missing = preset.FilePaths.Where(path => !File.Exists(path)).ToArray();
        int availableCount = preset.FilePaths.Count - missing.Length;
        if (missing.Length > 0)
        {
            string continuation = availableCount > 0
                ? $"The remaining {availableCount} level file(s) will open."
                : "None of the files in this preset are currently available.";
            MessageBox.Show(owner, $"Missing files in '{preset.Name}':\n\n{string.Join("\n", missing)}\n\n{continuation}\nMissing entries are kept so you can update the preset later.",
                "Missing preset files", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return availableCount > 0;
    }

    private void SelectSaved(LevelPreset preset)
    {
        PresetSearchText = "";
        SelectedPreset = preset;
        StatusText = $"Saved '{preset.Name}' with {preset.FilePaths.Count} level file(s).";
    }

    private async Task RunOperation(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        StatusText = "Checking level files…";
        UpdateButtons();
        try { await action(); }
        catch (Exception exception)
        {
            if (closed) return;
            StatusText = exception.Message;
            RefreshFiles();
            MessageBox.Show(this, exception.Message, "Level presets", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            busy = false;
            if (!closed) UpdateButtons();
        }
    }

    /// <summary>Checks every package before a preset replaces an editor's current scene.</summary>
    public static Task<MEGame> ValidateFilesAsync(IEnumerable<string> files, MEGame? expectedGame = null)
    {
        string[] paths = files.ToArray();
        return Task.Run(() =>
        {
            if (paths.Length == 0) throw new InvalidDataException("A preset needs at least one level file.");
            string[] missing = paths.Where(path => !File.Exists(path)).ToArray();
            if (missing.Length > 0)
                throw new FileNotFoundException($"These level files could not be found:\n\n{string.Join("\n", missing)}\n\nAdd replacement files or remove them from the preset.");
            MEGame? game = expectedGame;
            foreach (string path in paths)
            {
                using var package = MEPackageHandler.UnsafePartialLoad(path, _ => false);
                game ??= package.Game;
                if (package.Game != game)
                    throw new InvalidDataException($"'{System.IO.Path.GetFileName(path)}' is for {package.Game}. All files in this preset must be for {game}.");
                if (!package.Exports.Any(export => export.ClassName == "Level"))
                    throw new InvalidDataException($"'{System.IO.Path.GetFileName(path)}' does not contain a level.");
            }
            return game.Value;
        });
    }
}
