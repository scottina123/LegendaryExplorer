using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
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
    private readonly LevelCameraPreset currentCamera;
    private readonly ObservableCollectionExtended<LevelCameraPreset> cameraPresets = [];
    private Guid? selectedId;
    private bool busy;
    private bool closed;
    private bool newCamera;
    private bool initialized;

    public ICollectionView PresetView { get; }
    public ICollectionView CameraPresetView { get; }
    public ObservableCollectionExtended<LevelPresetFile> PresetFiles { get; } = [];

    private LevelPreset selectedPreset;
    public LevelPreset SelectedPreset
    {
        get => selectedPreset;
        set
        {
            Guid? cameraId = selectedPreset?.Id == value?.Id ? SelectedCameraPreset?.Id : null;
            bool changedPreset = selectedPreset?.Id != value?.Id;
            if (!SetProperty(ref selectedPreset, value)) return;
            if (value != null) selectedId = value.Id;
            EditorName = value?.Name ?? "";
            RefreshFiles();
            if (changedPreset) CameraSearchText = "";
            RefreshCameras(cameraId);
            UpdateButtons();
        }
    }

    private LevelCameraPreset selectedCameraPreset;
    public LevelCameraPreset SelectedCameraPreset
    {
        get => selectedCameraPreset;
        set
        {
            if (!SetProperty(ref selectedCameraPreset, value)) return;
            newCamera = false;
            SetCameraEditor(value);
            UpdateButtons();
        }
    }

    private string cameraSearchText = "";
    public string CameraSearchText
    {
        get => cameraSearchText;
        set
        {
            if (!SetProperty(ref cameraSearchText, value ?? "")) return;
            CameraPresetView.Refresh();
            if (SelectedCameraPreset != null && !CameraPresetView.Contains(SelectedCameraPreset))
                SelectedCameraPreset = null;
            UpdateButtons();
        }
    }

    private string cameraName = "";
    private string cameraX = "", cameraY = "", cameraZ = "";
    private string cameraRoll = "", cameraPitch = "", cameraYaw = "";
    public string CameraName { get => cameraName; set { if (SetProperty(ref cameraName, value ?? "")) UpdateButtons(); } }
    public string CameraX { get => cameraX; set { if (SetProperty(ref cameraX, value ?? "")) UpdateButtons(); } }
    public string CameraY { get => cameraY; set { if (SetProperty(ref cameraY, value ?? "")) UpdateButtons(); } }
    public string CameraZ { get => cameraZ; set { if (SetProperty(ref cameraZ, value ?? "")) UpdateButtons(); } }
    public string CameraRoll { get => cameraRoll; set { if (SetProperty(ref cameraRoll, value ?? "")) UpdateButtons(); } }
    public string CameraPitch { get => cameraPitch; set { if (SetProperty(ref cameraPitch, value ?? "")) UpdateButtons(); } }
    public string CameraYaw { get => cameraYaw; set { if (SetProperty(ref cameraYaw, value ?? "")) UpdateButtons(); } }

    private string cameraEditorStatus;
    public string CameraEditorStatus { get => cameraEditorStatus; private set => SetProperty(ref cameraEditorStatus, value); }

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
        IEnumerable<string> readOnlyFiles = null, LevelPresetStore store = null, LevelCameraPreset currentCamera = null)
    {
        this.store = store ?? LevelPresetStore.Shared;
        gameFilter = game is null or MEGame.Unknown ? null : game;
        this.currentFiles = currentFiles?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        currentReadOnlyFiles = readOnlyFiles?.ToList() ?? [];
        this.currentCamera = currentCamera;
        // Restore the selected camera before the list view processes a collection replacement.
        CollectionChangedEventManager.AddHandler(this.store.Presets, PresetsChanged);
        CameraPresetView = new ListCollectionView(cameraPresets)
        {
            Filter = item => item is LevelCameraPreset camera && LevelPresetStore.MatchesCameraSearch(camera, CameraSearchText)
        };
        PresetView = new ListCollectionView(this.store.Presets)
        {
            Filter = item => item is LevelPreset preset && (gameFilter == null || preset.Game == gameFilter)
                && LevelPresetStore.MatchesSearch(preset, PresetSearchText)
        };
        InitializeComponent();
        initialized = true;
        StatusText = this.store.LoadError ?? "Create a named preset from files, then open all its levels together. File changes are saved immediately.";
        Closed += (_, _) =>
        {
            closed = true;
            CollectionChangedEventManager.RemoveHandler(this.store.Presets, PresetsChanged);
        };
        SelectedPreset = PresetView.Cast<LevelPreset>().FirstOrDefault();
        RefreshFiles();
        RefreshCameras();
        UpdateButtons();
    }

    private void PresetsChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        CameraEditorState cameraState = CaptureCameraEditor();
        PresetView.Refresh();
        SelectedPreset = PresetView.Cast<LevelPreset>().FirstOrDefault(preset => preset.Id == selectedId)
            ?? PresetView.Cast<LevelPreset>().FirstOrDefault();
        RestoreCameraEditor(cameraState);
    }

    private void RefreshFiles()
    {
        var readOnly = (SelectedPreset?.ReadOnlyFilePaths ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        PresetFiles.ReplaceAll(SelectedPreset?.FilePaths.Select(path => new LevelPresetFile(path, readOnly.Contains(path)))
            ?? Enumerable.Empty<LevelPresetFile>());
    }

    private void RefreshCameras(Guid? cameraId = null)
    {
        SelectedCameraPreset = null;
        newCamera = false;
        SetCameraEditor(null);
        cameraPresets.ReplaceAll(SelectedPreset?.CameraPresets ?? []);
        if (cameraId.HasValue)
            SelectedCameraPreset = CameraPresetView.Cast<LevelCameraPreset>().FirstOrDefault(camera => camera.Id == cameraId);
    }

    private void SetCameraEditor(LevelCameraPreset camera)
    {
        CameraName = camera?.Name ?? "";
        CameraX = FormatCoordinate(camera?.X);
        CameraY = FormatCoordinate(camera?.Y);
        CameraZ = FormatCoordinate(camera?.Z);
        CameraRoll = FormatCoordinate(camera?.Roll);
        CameraPitch = FormatCoordinate(camera?.Pitch);
        CameraYaw = FormatCoordinate(camera?.Yaw);
    }

    private static string FormatCoordinate(float? value) => value?.ToString("R", CultureInfo.CurrentCulture) ?? "";

    private bool TryReadCamera(out LevelCameraPreset camera, out string error)
    {
        camera = null;
        if (string.IsNullOrWhiteSpace(CameraName))
        {
            error = "Enter a name for this camera location.";
            return false;
        }
        string[] names = ["X", "Y", "Z", "Roll", "Pitch", "Yaw"];
        string[] fields = [CameraX, CameraY, CameraZ, CameraRoll, CameraPitch, CameraYaw];
        float[] values = new float[6];
        for (int i = 0; i < fields.Length; i++)
        {
            if (!float.TryParse(fields[i], NumberStyles.Float, CultureInfo.CurrentCulture, out values[i])
                || !float.IsFinite(values[i]))
            {
                error = $"Enter a finite number for {names[i]}.";
                return false;
            }
        }
        camera = new LevelCameraPreset
        {
            Id = SelectedCameraPreset?.Id ?? Guid.NewGuid(), Name = CameraName.Trim(),
            X = values[0], Y = values[1], Z = values[2], Roll = values[3], Pitch = values[4], Yaw = values[5]
        };
        LevelCameraPreset candidate = camera;
        if (SelectedPreset?.CameraPresets.Any(existing => existing.Id != candidate.Id
                && string.Equals(existing.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)) == true)
        {
            error = "A camera location with this name already exists in this level preset.";
            return false;
        }
        error = null;
        return true;
    }

    private bool HasCameraEdits => newCamera || (SelectedCameraPreset != null
        && (!TryReadCamera(out LevelCameraPreset edited, out _) || edited != SelectedCameraPreset));

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
        if (!initialized) return;
        bool writable = !busy && store.LoadError == null;
        bool selected = SelectedPreset != null;
        NewPresetButton.IsEnabled = writable;
        SaveCurrentLevelsButton.IsEnabled = writable && currentFiles.Count > 0;
        SaveNameButton.IsEnabled = writable && selected && !string.IsNullOrWhiteSpace(EditorName)
            && EditorName.Trim() != SelectedPreset.Name;
        AddFilesButton.IsEnabled = writable && selected;
        RemoveFilesButton.IsEnabled = writable && selected;
        DeletePresetButton.IsEnabled = writable && selected;
        bool cameraEditorActive = selected && (newCamera || SelectedCameraPreset != null);
        bool validCamera = TryReadCamera(out _, out string cameraError);
        bool cameraDirty = HasCameraEdits;
        OpenPresetButton.IsEnabled = !busy && selected && !cameraDirty;
        PresetsList.IsEnabled = !busy;
        PresetSearchTextBox.IsEnabled = !busy;
        PresetNameTextBox.IsEnabled = writable && selected;
        NewCameraButton.IsEnabled = writable && selected;
        SaveCameraButton.IsEnabled = writable && cameraEditorActive && validCamera && cameraDirty;
        DeleteCameraButton.IsEnabled = writable && SelectedCameraPreset != null;
        UseCurrentCameraButton.IsEnabled = writable && cameraEditorActive && currentCamera != null;
        DefaultCameraButton.IsEnabled = !busy && selected;
        CameraPresetsList.IsEnabled = !busy && selected;
        CameraSearchTextBox.IsEnabled = !busy && selected;
        CameraEditorFields.IsEnabled = writable && cameraEditorActive;
        CameraEditorStatus = !cameraEditorActive ? "No camera location selected. Levels open at their usual location."
            : !validCamera ? cameraError
            : cameraDirty ? "Save these camera changes before opening, or choose Use default location."
            : $"Levels will open at '{SelectedCameraPreset.Name}'.";
    }

    public void ShowCameraLocations() => CameraLocationsTab.IsSelected = true;

    private void NewCamera_Click(object sender, RoutedEventArgs e)
    {
        if (busy || store.LoadError != null || SelectedPreset == null) return;
        SelectedCameraPreset = null;
        SetCameraEditor(currentCamera ?? new LevelCameraPreset());
        CameraName = "";
        newCamera = true;
        UpdateButtons();
        CameraNameTextBox.Focus();
    }

    private void UseCurrentCamera_Click(object sender, RoutedEventArgs e)
    {
        if (busy || store.LoadError != null || currentCamera == null || (!newCamera && SelectedCameraPreset == null)) return;
        string name = CameraName;
        SetCameraEditor(currentCamera);
        CameraName = name;
    }

    private void DefaultCamera_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SelectedCameraPreset = null;
        newCamera = false;
        SetCameraEditor(null);
        UpdateButtons();
    }

    private async void SaveCamera_Click(object sender, RoutedEventArgs e)
    {
        if (busy || store.LoadError != null || SelectedPreset == null || (!newCamera && SelectedCameraPreset == null)
            || !TryReadCamera(out LevelCameraPreset camera, out _)) return;
        LevelPreset preset = SelectedPreset;
        var cameras = preset.CameraPresets.ToList();
        int index = cameras.FindIndex(existing => existing.Id == camera.Id);
        if (index < 0) cameras.Add(camera);
        else cameras[index] = camera;
        await RunOperation(() =>
        {
            SelectSaved(store.Save(preset with { CameraPresets = cameras }));
            CameraSearchText = "";
            SelectedCameraPreset = SelectedPreset.CameraPresets.First(saved => saved.Id == camera.Id);
            StatusText = $"Saved camera location '{camera.Name}' in '{preset.Name}'.";
            return Task.CompletedTask;
        });
    }

    private async void DeleteCamera_Click(object sender, RoutedEventArgs e)
    {
        LevelCameraPreset camera = SelectedCameraPreset;
        if (busy || store.LoadError != null || camera == null) return;
        if (MessageBox.Show(this, $"Delete the camera location '{camera.Name}' from '{SelectedPreset.Name}'?", "Delete camera location",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        LevelPreset preset = SelectedPreset;
        await RunOperation(() =>
        {
            SelectSaved(store.Save(preset with { CameraPresets = preset.CameraPresets.Where(existing => existing.Id != camera.Id).ToList() }));
            SelectedCameraPreset = null;
            newCamera = false;
            SetCameraEditor(null);
            StatusText = $"Deleted camera location '{camera.Name}'. Levels will open at their usual location.";
            return Task.CompletedTask;
        });
    }

    // Saving a parent replaces the store collection. Keep its active camera and unfinished camera edits intact.
    private void SaveParentPreset(LevelPreset preset)
    {
        CameraEditorState cameraState = CaptureCameraEditor();
        SelectSaved(store.Save(preset));
        RestoreCameraEditor(cameraState);
    }

    private sealed record CameraEditorState(Guid? PresetId, Guid? CameraId, bool NewCamera, string Search, string[] Fields);

    private CameraEditorState CaptureCameraEditor() => new(SelectedPreset?.Id, SelectedCameraPreset?.Id, newCamera,
        CameraSearchText, [CameraName, CameraX, CameraY, CameraZ, CameraRoll, CameraPitch, CameraYaw]);

    private void RestoreCameraEditor(CameraEditorState state)
    {
        if (state.PresetId == null || state.PresetId != SelectedPreset?.Id) return;
        CameraSearchText = state.Search;
        if (state.CameraId.HasValue)
            SelectedCameraPreset = CameraPresetView.Cast<LevelCameraPreset>().FirstOrDefault(camera => camera.Id == state.CameraId);
        if (state.NewCamera || (state.CameraId.HasValue && SelectedCameraPreset != null))
        {
            newCamera = state.NewCamera;
            string[] fields = state.Fields;
            CameraName = fields[0]; CameraX = fields[1]; CameraY = fields[2]; CameraZ = fields[3];
            CameraRoll = fields[4]; CameraPitch = fields[5]; CameraYaw = fields[6];
        }
        UpdateButtons();
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
        SaveParentPreset(SelectedPreset with { Name = EditorName });
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
            SaveParentPreset(preset with { Name = name, FilePaths = preset.FilePaths.Concat(paths).ToList() });
        });
    }

    private async void RemoveFiles_Click(object sender, RoutedEventArgs e)
    {
        var removed = PresetFilesList.SelectedItems.Cast<LevelPresetFile>().Select(file => file.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (removed.Count == 0) return;
        await RunOperation(() =>
        {
            SaveParentPreset(SelectedPreset with
            {
                Name = EditorName,
                FilePaths = SelectedPreset.FilePaths.Where(path => !removed.Contains(path)).ToList()
            });
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
        if (SelectedPreset == null || HasCameraEdits) return;
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
