using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorerCore.Coalesced;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace LegendaryExplorer.Tools.CoalescedEditor
{
    /// <summary>
    /// A direct in-editor coalesced file editor that opens .bin files without extracting to XML.
    /// </summary>
    public partial class CoalescedEditorWindow : TrackingNotifyPropertyChangedWindowBase
    {
        private static readonly string StateFilePath = Path.Combine(AppDirectories.AppDataFolder, "CoalescedEditorState.json");

        public ObservableCollection<OpenCoalescedFile> OpenFiles { get; } = new();

        private OpenCoalescedFile _selectedFile;
        public OpenCoalescedFile SelectedFile
        {
            get => _selectedFile;
            set => SetProperty(ref _selectedFile, value);
        }

        private string _statusText = "Ready";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public ICommand OpenFileCommand { get; set; }
        public ICommand SaveCommand { get; set; }
        public ICommand SaveAsCommand { get; set; }
        public ICommand ExportToFolderCommand { get; set; }
        public ICommand CloseTabCommand { get; set; }

        public CoalescedEditorWindow() : base("Coalesced Editor", true)
        {
            LoadCommands();
            InitializeComponent();
            DataContext = this;
            RestoreOpenFiles();
            UpdateWelcomeVisibility();
        }

        private void LoadCommands()
        {
            OpenFileCommand = new GenericCommand(OpenFile);
            SaveCommand = new GenericCommand(SaveCurrentFile, () => SelectedFile != null);
            SaveAsCommand = new GenericCommand(SaveCurrentFileAs, () => SelectedFile != null);
            ExportToFolderCommand = new GenericCommand(ExportToFolder, () => SelectedFile != null);
            CloseTabCommand = new GenericCommand(CloseCurrentTab, () => SelectedFile != null);
        }

        private void OpenFile()
        {
            var dlg = new CommonOpenFileDialog("Open Coalesced File");
            dlg.Filters.Add(new CommonFileDialogFilter("Coalesced Files", "*.bin"));
            if (dlg.ShowDialog(this) != CommonFileDialogResult.Ok)
                return;

            LoadCoalescedFile(dlg.FileName);
        }

        private void LoadCoalescedFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    StatusText = $"File not found: {filePath}";
                    return;
                }

                // Check if already open
                var existing = OpenFiles.FirstOrDefault(f => f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    SelectedFile = existing;
                    StatusText = $"Switched to already-open file: {Path.GetFileName(filePath)}";
                    return;
                }

                bool isGame3 = CoalescedConverter.IsGame3Coalesced(filePath);
                var coalFile = new OpenCoalescedFile { FilePath = filePath, IsGame3 = isGame3 };

                using var fs = File.OpenRead(filePath);

                if (isGame3)
                {
                    var xmlMap = CoalescedConverter.DecompileGame3ToMemory(fs);
                    foreach (var kvp in xmlMap.OrderBy(k => k.Key))
                    {
                        coalFile.FileNames.Add(kvp.Key);
                        coalFile.FileContents[kvp.Key] = kvp.Value;
                    }
                }
                else
                {
                    var name = Path.GetFileName(filePath);
                    var iniMap = CoalescedConverter.DecompileLE1LE2ToMemory(fs, name);
                    foreach (var kvp in iniMap.OrderBy(k => k.Key))
                    {
                        coalFile.FileNames.Add(kvp.Key);
                        coalFile.FileContents[kvp.Key] = IniToDisplayString(kvp.Value);
                    }
                }

                if (coalFile.FileNames.Count > 0)
                    coalFile.SelectedFileName = coalFile.FileNames[0];

                coalFile.HasUnsavedChanges = false;

                OpenFiles.Add(coalFile);
                SelectedFile = coalFile;
                UpdateWelcomeVisibility();
                StatusText = $"Opened: {Path.GetFileName(filePath)} ({coalFile.FileNames.Count} files)";
            }
            catch (Exception ex)
            {
                StatusText = $"Error opening file: {ex.Message}";
                MessageBox.Show($"Failed to open coalesced file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveCurrentFile()
        {
            if (SelectedFile == null) return;
            SaveCoalescedFile(SelectedFile, SelectedFile.FilePath);
        }

        private void SaveCurrentFileAs()
        {
            if (SelectedFile == null) return;

            var dlg = new SaveFileDialog
            {
                Filter = "Coalesced Binary Files (*.bin)|*.bin",
                FileName = Path.GetFileName(SelectedFile.FilePath),
                InitialDirectory = Path.GetDirectoryName(SelectedFile.FilePath)
            };

            if (dlg.ShowDialog() == true)
            {
                SaveCoalescedFile(SelectedFile, dlg.FileName);
                SelectedFile.FilePath = dlg.FileName;
            }
        }

        private void SaveCoalescedFile(OpenCoalescedFile coalFile, string destinationPath)
        {
            try
            {
                MemoryStream ms;
                if (coalFile.IsGame3)
                {
                    var fileMapping = new Dictionary<string, string>(coalFile.FileContents);
                    ms = CoalescedConverter.CompileFromMemory(fileMapping);
                }
                else
                {
                    var iniMap = new Dictionary<string, DuplicatingIni>();
                    foreach (var kvp in coalFile.FileContents)
                    {
                        iniMap[kvp.Key] = DisplayStringToIni(kvp.Value);
                    }

                    var loc = destinationPath.GetUnrealLocalization();
                    if (loc == MELocalization.None)
                        loc = MELocalization.INT;

                    ms = CoalescedConverter.CompileLE1LE2FromMemory(iniMap, loc);
                }

                using var outFs = File.Create(destinationPath);
                ms.CopyTo(outFs);
                ms.Dispose();

                coalFile.HasUnsavedChanges = false;
                StatusText = $"Saved: {Path.GetFileName(destinationPath)}";
            }
            catch (Exception ex)
            {
                StatusText = $"Error saving: {ex.Message}";
                MessageBox.Show($"Failed to save coalesced file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportToFolder()
        {
            if (SelectedFile == null) return;

            var dlg = new CommonOpenFileDialog("Select Export Destination Folder")
            {
                IsFolderPicker = true
            };

            if (dlg.ShowDialog(this) != CommonFileDialogResult.Ok)
                return;

            try
            {
                var destPath = dlg.FileName;

                if (SelectedFile.IsGame3)
                {
                    Directory.CreateDirectory(destPath);
                    foreach (var kvp in SelectedFile.FileContents)
                    {
                        var outPath = Path.Combine(destPath, kvp.Key);
                        File.WriteAllText(outPath, kvp.Value);
                    }
                }
                else
                {
                    var iniMap = new CaseInsensitiveDictionary<DuplicatingIni>();
                    foreach (var kvp in SelectedFile.FileContents)
                    {
                        iniMap[kvp.Key] = DisplayStringToIni(kvp.Value);
                    }

                    var bundle = new LECoalescedBundle(Path.GetFileName(SelectedFile.FilePath));
                    foreach (var kvp in iniMap)
                    {
                        bundle.Files[kvp.Key] = kvp.Value;
                    }
                    bundle.WriteToDirectory(destPath);
                }

                StatusText = $"Exported to: {destPath}";
            }
            catch (Exception ex)
            {
                StatusText = $"Error exporting: {ex.Message}";
                MessageBox.Show($"Failed to export:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloseCurrentTab()
        {
            if (SelectedFile == null) return;
            CloseTab(SelectedFile);
        }

        private void CloseTab(OpenCoalescedFile file)
        {
            if (file.HasUnsavedChanges)
            {
                var result = MessageBox.Show($"'{file.DisplayName}' has unsaved changes. Save before closing?",
                    "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Cancel) return;
                if (result == MessageBoxResult.Yes)
                {
                    SaveCoalescedFile(file, file.FilePath);
                }
            }

            int idx = OpenFiles.IndexOf(file);
            OpenFiles.Remove(file);

            if (OpenFiles.Count > 0)
                SelectedFile = OpenFiles[Math.Min(idx, OpenFiles.Count - 1)];

            UpdateWelcomeVisibility();
            StatusText = "Ready";
        }

        private void CloseTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is OpenCoalescedFile file)
            {
                CloseTab(file);
            }
        }

        private void UpdateWelcomeVisibility()
        {
            if (WelcomeText != null)
                WelcomeText.Visibility = OpenFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        #region Drag and Drop

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                string ext = Path.GetExtension(files[0]).ToLower();
                if (ext != ".bin")
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                }
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (var file in files)
                {
                    if (Path.GetExtension(file).Equals(".bin", StringComparison.OrdinalIgnoreCase))
                    {
                        LoadCoalescedFile(file);
                    }
                }
            }
        }

        #endregion

        #region Session Persistence

        private void Root_Closed(object sender, EventArgs e)
        {
            SaveOpenFilesList();
        }

        private void SaveOpenFilesList()
        {
            try
            {
                var paths = OpenFiles.Select(f => f.FilePath).ToList();
                var json = JsonSerializer.Serialize(paths);
                Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath));
                File.WriteAllText(StateFilePath, json);
            }
            catch
            {
                // Silently fail - not critical
            }
        }

        private void RestoreOpenFiles()
        {
            try
            {
                if (!File.Exists(StateFilePath)) return;

                var json = File.ReadAllText(StateFilePath);
                var paths = JsonSerializer.Deserialize<List<string>>(json);
                if (paths == null) return;

                foreach (var path in paths)
                {
                    if (File.Exists(path))
                    {
                        LoadCoalescedFile(path);
                    }
                }
            }
            catch
            {
                // Silently fail - not critical
            }
        }

        #endregion

        #region INI <-> Display String Conversion (LE1/LE2)

        /// <summary>
        /// Converts a DuplicatingIni to an editable display string using the ||= multiline format.
        /// </summary>
        private static string IniToDisplayString(DuplicatingIni ini)
        {
            var sb = new StringBuilder();
            bool isFirst = true;

            foreach (var section in ini.Sections)
            {
                if (!isFirst)
                    sb.AppendLine();
                isFirst = false;

                sb.AppendLine($"[{section.Header}]");

                foreach (var entry in section.Entries)
                {
                    if (!entry.HasValue)
                    {
                        sb.AppendLine(entry.RawText);
                        continue;
                    }

                    var lines = SplitMultilineValue(entry.Value);
                    if (lines == null || lines.Count <= 1)
                    {
                        sb.AppendLine($"{entry.Key}={entry.Value}");
                    }
                    else
                    {
                        foreach (var line in lines)
                        {
                            sb.AppendLine($"{entry.Key}||={line}");
                        }
                    }
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Parses a display string back into a DuplicatingIni, handling ||= multiline format.
        /// </summary>
        private static DuplicatingIni DisplayStringToIni(string text)
        {
            var ini = new DuplicatingIni();
            if (string.IsNullOrEmpty(text)) return ini;

            DuplicatingIni.Section currentSection = null;

            using var reader = new StringReader(text);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Section header
                if (line.StartsWith('[') && line.TrimEnd().EndsWith(']'))
                {
                    var header = line.Trim().TrimStart('[').TrimEnd(']');
                    if (currentSection != null)
                        ini.Sections.Add(currentSection);
                    currentSection = new DuplicatingIni.Section { Header = header };
                    continue;
                }

                if (currentSection == null) continue;

                // Key=Value pair
                var eqIdx = line.IndexOf('=');
                if (eqIdx <= 0)
                {
                    currentSection.Entries.Add(new DuplicatingIni.IniEntry(line));
                    continue;
                }

                var rawKey = line.Substring(0, eqIdx);
                var value = line.Substring(eqIdx + 1);

                if (rawKey.EndsWith("||"))
                {
                    var strippedKey = rawKey.Substring(0, rawKey.Length - 2);

                    if (currentSection.Entries.Count > 0 && currentSection.Entries[^1].Key == strippedKey)
                    {
                        var last = currentSection.Entries[^1];
                        currentSection.Entries[^1] = new DuplicatingIni.IniEntry(last.Key, last.Value + "\r\n" + value);
                    }
                    else
                    {
                        currentSection.Entries.Add(new DuplicatingIni.IniEntry(strippedKey, value));
                    }
                }
                else
                {
                    currentSection.Entries.Add(new DuplicatingIni.IniEntry(rawKey, value));
                }
            }

            if (currentSection != null)
                ini.Sections.Add(currentSection);

            return ini;
        }

        private static List<string> SplitMultilineValue(string val)
        {
            if (val.Contains("\r\n"))
                return val.Split("\r\n").ToList();
            if (val.Contains('\r') && !val.Contains('\n'))
                return val.Split('\r').ToList();
            if (!val.Contains('\r') && val.Contains('\n'))
                return val.Split('\n').ToList();
            return null;
        }

        #endregion
    }

    /// <summary>
    /// Represents an open coalesced file in the editor.
    /// </summary>
    public class OpenCoalescedFile : INotifyPropertyChanged
    {
        private bool _isLoadingContent;

        private string _filePath;
        public string FilePath
        {
            get => _filePath;
            set { _filePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(TabHeader)); }
        }

        public string DisplayName => Path.GetFileName(FilePath);

        public bool IsGame3 { get; set; }

        public Dictionary<string, string> FileContents { get; set; } = new();

        public ObservableCollection<string> FileNames { get; } = new();

        private string _selectedFileName;
        public string SelectedFileName
        {
            get => _selectedFileName;
            set
            {
                _isLoadingContent = true;
                _selectedFileName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentContent));
                _isLoadingContent = false;
            }
        }

        public string CurrentContent
        {
            get => SelectedFileName != null && FileContents.TryGetValue(SelectedFileName, out var c) ? c : "";
            set
            {
                if (SelectedFileName != null && FileContents.ContainsKey(SelectedFileName))
                {
                    FileContents[SelectedFileName] = value;
                    if (!_isLoadingContent && !HasUnsavedChanges)
                        HasUnsavedChanges = true;
                }
            }
        }

        private bool _hasUnsavedChanges;
        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            set
            {
                if (_hasUnsavedChanges != value)
                {
                    _hasUnsavedChanges = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TabHeader));
                }
            }
        }

        public string TabHeader => HasUnsavedChanges ? $"{DisplayName} *" : DisplayName;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
