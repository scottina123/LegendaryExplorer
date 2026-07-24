using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorerCore.Coalesced;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Xml;
using System.Xml.Linq;
using LegendaryExplorer.Tools.ConditionalsEditor;
using LegendaryExplorer.Tools.PlotDatabase;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorerCore.GameFilesystem;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Editing;
using LegendaryExplorerCore.PlotDatabase;
using LegendaryExplorerCore.PlotDatabase.PlotElements;
using ICSharpCode.AvalonEdit.Rendering;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Tools.CoalescedEditor
{
    /// <summary>
    /// A direct in-editor coalesced file editor that opens .bin files without extracting to XML.
    /// </summary>
    public partial class CoalescedEditorWindow : TrackingNotifyPropertyChangedWindowBase
    {
        private static readonly string StateFilePath = Path.Combine(AppDirectories.AppDataFolder, "CoalescedEditorState.json");
        private const string DefaultGame3ManifestBaseName = "Coalesced";
        private static readonly Regex TlkReferenceRegex = new(@"\b\d{5,10}\b", RegexOptions.Compiled);
        private static readonly Regex PlotAssignmentRegex = new(@"(?<key>\b[A-Za-z_][A-Za-z0-9_]*\b)\s*=\s*(?<id>\d+)", RegexOptions.Compiled);
        private static readonly Regex PlotXmlPropertyRegex = new(@"<Property\s+name=\""(?<key>[^\""]+)\""[^>]*>\s*(?<id>\d+)\s*</Property>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public ObservableCollection<OpenCoalescedFile> OpenFiles { get; } = new();

        private OpenCoalescedFile _selectedFile;
        public OpenCoalescedFile SelectedFile
        {
            get => _selectedFile;
            set
            {
                if (SetProperty(ref _selectedFile, value))
                {
                    OnSelectedFileChanged();
                }
            }
        }

        private string _statusText = "Ready";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private bool _isFindReplaceVisible;
        public bool IsFindReplaceVisible
        {
            get => _isFindReplaceVisible;
            set => SetProperty(ref _isFindReplaceVisible, value);
        }

        private string _findText;
        public string FindText
        {
            get => _findText;
            set
            {
                if (SetProperty(ref _findText, value))
                    UpdateSearchStatus();
            }
        }

        private string _replaceText = string.Empty;
        public string ReplaceText
        {
            get => _replaceText;
            set => SetProperty(ref _replaceText, value);
        }

        private bool _findMatchCase;
        public bool FindMatchCase
        {
            get => _findMatchCase;
            set
            {
                if (SetProperty(ref _findMatchCase, value))
                    UpdateSearchStatus();
            }
        }

        private bool _findWholeWord;
        public bool FindWholeWord
        {
            get => _findWholeWord;
            set
            {
                if (SetProperty(ref _findWholeWord, value))
                    UpdateSearchStatus();
            }
        }

        private bool _findUseRegex;
        public bool FindUseRegex
        {
            get => _findUseRegex;
            set
            {
                if (SetProperty(ref _findUseRegex, value))
                    UpdateSearchStatus();
            }
        }

        private bool _findWrapAround = true;
        public bool FindWrapAround
        {
            get => _findWrapAround;
            set => SetProperty(ref _findWrapAround, value);
        }

        private string _searchStatusText = "Enter text to search.";
        public string SearchStatusText
        {
            get => _searchStatusText;
            set => SetProperty(ref _searchStatusText, value);
        }

        private bool _showTlkBoxes = true;
        public bool ShowTlkBoxes
        {
            get => _showTlkBoxes;
            set
            {
                if (!SetProperty(ref _showTlkBoxes, value))
                    return;

                UpdateTlkInlineAnnotations();
                ResetSearchState();
                UpdateSearchStatus();
                SaveOpenFilesList();
            }
        }

        public ICommand OpenFileCommand { get; set; }
        public ICommand SaveCommand { get; set; }
        public ICommand SaveAsCommand { get; set; }
        public ICommand ExportToFolderCommand { get; set; }
        public ICommand CloseTabCommand { get; set; }
        public ICommand ShowFindReplaceCommand { get; set; }
        public ICommand FindNextCommand { get; set; }
        public ICommand FindPreviousCommand { get; set; }
        public ICommand ReplaceCommand { get; set; }
        public ICommand ReplaceAllCommand { get; set; }
        public ICommand CloseFindReplaceCommand { get; set; }

        private bool _suppressEditorEvents;
        private bool _isRestoringState;
        private readonly XmlTagMatchRenderer _xmlTagMatchRenderer = new();
        private readonly SelectionMatchRenderer _selectionMatchRenderer = new();
        private readonly TlkInlineAnnotationGenerator _tlkInlineAnnotationGenerator;
        private CoalescedSearchMatch? _currentSearchMatch;
        private Point _tabDragStartPoint;
        private OpenCoalescedFile _tabDragSource;

        public CoalescedEditorWindow() : base("Coalesced Editor", true)
        {
            _tlkInlineAnnotationGenerator = new TlkInlineAnnotationGenerator(SelectTlkStringRef);
            LoadCommands();
            InitializeComponent();
            DataContext = this;
            TextEditor.TextArea.TextView.BackgroundRenderers.Add(_xmlTagMatchRenderer);
            TextEditor.TextArea.TextView.BackgroundRenderers.Add(_selectionMatchRenderer);
            TextEditor.TextArea.TextView.ElementGenerators.Add(_tlkInlineAnnotationGenerator);
            TextEditor.TextChanged += TextEditor_TextChanged;
            TextEditor.TextArea.Caret.PositionChanged += TextArea_Caret_PositionChanged;
            TextEditor.TextArea.SelectionChanged += TextArea_SelectionChanged;
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
            ShowFindReplaceCommand = new GenericCommand(ShowFindReplace, () => SelectedFile != null);
            FindNextCommand = new GenericCommand(FindNext, () => SelectedFile != null);
            FindPreviousCommand = new GenericCommand(FindPrevious, () => SelectedFile != null);
            ReplaceCommand = new GenericCommand(ReplaceCurrent, () => SelectedFile != null);
            ReplaceAllCommand = new GenericCommand(ReplaceAll, () => SelectedFile != null);
            CloseFindReplaceCommand = new GenericCommand(CloseFindReplace);
        }

        private void OpenFile()
        {
            var dlg = new CommonOpenFileDialog("Open Coalesced File");
            dlg.Filters.Add(new CommonFileDialogFilter("Coalesced Files", "*.bin"));
            if (dlg.ShowDialog(this) != CommonFileDialogResult.Ok)
                return;

            LoadCoalescedFile(dlg.FileName);
        }

        private void EditorContextMenu_Cut_Click(object sender, RoutedEventArgs e)
        {
            TextEditor?.Focus();
            TextEditor?.Cut();
        }

        private void EditorContextMenu_Copy_Click(object sender, RoutedEventArgs e)
        {
            TextEditor?.Focus();
            TextEditor?.Copy();
        }

        private void EditorContextMenu_Paste_Click(object sender, RoutedEventArgs e)
        {
            TextEditor?.Focus();
            TextEditor?.Paste();
        }

        private void EditorContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu contextMenu)
                return;

            // Remove previously added dynamic items
            for (int i = contextMenu.Items.Count - 1; i >= 0; i--)
            {
                if (contextMenu.Items[i] is FrameworkElement { Tag: "PlotNav" })
                    contextMenu.Items.RemoveAt(i);
            }

            if (TextEditor?.Document == null)
                return;

            var game = GetSelectedFileGame();
            if (game == MEGame.Unknown)
                return;

            var reference = FindPlotReferenceAtCaret();
            if (reference == null)
                return;

            var (key, plotId) = reference.Value;
            var inferredTypes = GetPlotElementTypesForKey(key);
            var (resolvedType, plotElement) = TryResolvePlotElement(inferredTypes, plotId, game);

            // If nothing was inferred from the key and nothing resolved from the database, nothing to show
            if (inferredTypes.Count == 0 && resolvedType == null)
                return;

            bool addedSeparator = false;
            void EnsureSeparator()
            {
                if (!addedSeparator)
                {
                    contextMenu.Items.Add(new Separator { Tag = "PlotNav" });
                    addedSeparator = true;
                }
            }

            // Offer CND editor if the key name suggests a conditional OR the database resolved it as one
            bool isConditional = inferredTypes.Contains(PlotElementType.Conditional) || resolvedType == PlotElementType.Conditional;
            if (isConditional && game.IsGame3())
            {
                EnsureSeparator();
                int capturedId = plotId;
                var capturedGame = game;
                var cndItem = new MenuItem
                {
                    Header = $"Open Conditional {plotId} in Conditionals Editor",
                    Tag = "PlotNav"
                };
                cndItem.Click += (_, _) => OpenConditionalInEditor(capturedId, capturedGame);
                contextMenu.Items.Add(cndItem);
            }

            if (plotElement != null)
            {
                EnsureSeparator();
                var capturedElement = plotElement;
                var capturedGame = game;
                var displayType = resolvedType ?? inferredTypes.FirstOrDefault();
                var dbItem = new MenuItem
                {
                    Header = $"Open {displayType} {plotId} in Plot Database",
                    Tag = "PlotNav"
                };
                dbItem.Click += (_, _) => OpenPlotElementInDatabase(capturedElement, capturedGame);
                contextMenu.Items.Add(dbItem);
            }
        }

        private (string key, int plotId)? FindPlotReferenceAtCaret()
        {
            var document = TextEditor?.Document;
            if (document == null)
                return null;

            int caretOffset = TextEditor.CaretOffset;
            var line = document.GetLineByOffset(caretOffset);
            string lineText = document.GetText(line.Offset, line.Length);
            int lineRelativeOffset = caretOffset - line.Offset;

            // Try matches where the caret is within the match range
            foreach (Match match in PlotAssignmentRegex.Matches(lineText))
            {
                if (lineRelativeOffset < match.Index || lineRelativeOffset > match.Index + match.Length)
                    continue;

                if (int.TryParse(match.Groups["id"].Value, out int plotId))
                    return (match.Groups["key"].Value, plotId);
            }

            foreach (Match match in PlotXmlPropertyRegex.Matches(lineText))
            {
                var idGroup = match.Groups["id"];
                if (lineRelativeOffset < idGroup.Index || lineRelativeOffset > idGroup.Index + idGroup.Length)
                    continue;

                if (int.TryParse(idGroup.Value, out int plotId))
                    return (match.Groups["key"].Value, plotId);
            }

            return null;
        }

        private static (PlotElementType? resolvedType, PlotElement plotElement) TryResolvePlotElement(List<PlotElementType> inferredTypes, int plotId, MEGame game)
        {
            // First try types inferred from key name
            foreach (var plotType in inferredTypes)
            {
                var element = PlotDatabases.FindPlotElementFromID(plotId, plotType, game);
                if (element != null)
                    return (plotType, element);
            }

            // Fall back to trying common types when key name is unrecognized
            PlotElementType[] fallbackTypes = [PlotElementType.Conditional, PlotElementType.Integer, PlotElementType.State, PlotElementType.Float, PlotElementType.Transition];
            foreach (var plotType in fallbackTypes)
            {
                var element = PlotDatabases.FindPlotElementFromID(plotId, plotType, game);
                if (element != null)
                    return (plotType, element);
            }

            return (null, null);
        }

        private void OpenConditionalInEditor(int conditionalId, MEGame game)
        {
            var cookedDirs = MELoadedDLC.GetEnabledDLCFolders(game)
                .OrderByDescending(dir => MELoadedDLC.GetMountPriority(dir, game))
                .Select(dir => Path.Combine(dir, game.CookedDirName()))
                .Append(MEDirectories.GetCookedPath(game))
                .Where(Directory.Exists);

            var cndFiles = cookedDirs.SelectMany(dir => Directory.EnumerateFiles(dir, "*.cnd"));

            string matchedFile = null;
            foreach (var cndFile in cndFiles)
            {
                var cnd = CNDFile.FromFile(cndFile);
                if (cnd.ConditionalEntries.Any(c => c.ID == conditionalId))
                {
                    matchedFile = cndFile;
                    break;
                }
            }

            if (matchedFile != null)
            {
                var cndEd = new ConditionalsEditorWindow();
                cndEd.Show();
                cndEd.LoadFile(matchedFile, conditionalId);
            }
            else
            {
                MessageBox.Show(this, $"Could not find conditional {conditionalId} in any mounted .cnd file.");
            }
        }

        private static void OpenPlotElementInDatabase(PlotElement element, MEGame game)
        {
            var plotDb = new PlotManagerWindow(game, element);
            plotDb.Show();
        }

        public void LoadCoalescedFile(string filePath)
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
                    var manifestName = GetGame3ManifestFileName(filePath);
                    coalFile.FileNames.Add(manifestName);
                    coalFile.FileContents[manifestName] = BuildGame3ManifestXml(filePath, xmlMap.Keys);

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
                    coalFile.SelectedFileName = isGame3 && coalFile.FileNames.Count > 1 ? coalFile.FileNames[1] : coalFile.FileNames[0];

                coalFile.LastSaved = File.GetLastWriteTime(filePath);
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

        public void NavigateToReference(string filePath, string innerFileName, string searchText)
        {
            LoadCoalescedFile(filePath);

            var openFile = OpenFiles.FirstOrDefault(f => f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
            if (openFile == null)
            {
                return;
            }

            SelectedFile = openFile;
            if (!string.IsNullOrWhiteSpace(innerFileName))
            {
                var matchingInnerFile = openFile.FileNames.FirstOrDefault(name => name.Equals(innerFileName, StringComparison.OrdinalIgnoreCase));
                if (matchingInnerFile != null)
                {
                    _suppressEditorEvents = true;
                    try
                    {
                        openFile.SelectedFileName = matchingInnerFile;
                        FileListBox.SelectedItem = matchingInnerFile;
                        UpdateEditorContent();
                    }
                    finally
                    {
                        _suppressEditorEvents = false;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(searchText) || string.IsNullOrWhiteSpace(TextEditor.Text))
            {
                return;
            }

            int matchIndex = TextEditor.Text.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                return;
            }

            TextEditor.Select(matchIndex, searchText.Length);
            TextEditor.CaretOffset = matchIndex + searchText.Length;
            var location = TextEditor.Document.GetLocation(matchIndex);
            TextEditor.ScrollToLine(location.Line);
            TextEditor.Focus();
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
                    ms = CompileGame3CoalescedFromMemory(coalFile, destinationPath);
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

                coalFile.LastSaved = File.GetLastWriteTime(destinationPath);
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
                    if (!TryGetGame3ManifestFileName(SelectedFile, out var manifestName))
                    {
                        manifestName = GetGame3ManifestFileName(SelectedFile.FilePath);
                    }

                    if (!SelectedFile.FileContents.ContainsKey(manifestName))
                    {
                        var assetNames = SelectedFile.FileContents.Keys.Where(name => !name.Equals(manifestName, StringComparison.OrdinalIgnoreCase));
                        File.WriteAllText(Path.Combine(destPath, manifestName), BuildGame3ManifestXml(SelectedFile.FilePath, assetNames));
                    }

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

        private void TabHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: OpenCoalescedFile file })
                return;

            if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null)
            {
                _tabDragSource = null;
                return;
            }

            _tabDragStartPoint = e.GetPosition(this);
            _tabDragSource = file;
        }

        private void TabHeader_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _tabDragSource == null || sender is not FrameworkElement fe)
                return;

            Point currentPosition = e.GetPosition(this);
            if (Math.Abs(currentPosition.X - _tabDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(currentPosition.Y - _tabDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            DragDrop.DoDragDrop(fe, new DataObject(typeof(OpenCoalescedFile), _tabDragSource), DragDropEffects.Move);
            _tabDragSource = null;
        }

        private void TabHeader_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _tabDragSource = null;
        }

        private void TabHeader_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(OpenCoalescedFile)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void TabHeader_Drop(object sender, DragEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: OpenCoalescedFile targetFile } ||
                e.Data.GetData(typeof(OpenCoalescedFile)) is not OpenCoalescedFile sourceFile ||
                ReferenceEquals(sourceFile, targetFile))
            {
                return;
            }

            int sourceIndex = OpenFiles.IndexOf(sourceFile);
            int targetIndex = OpenFiles.IndexOf(targetFile);
            if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
                return;

            OpenFiles.Move(sourceIndex, targetIndex);
            SelectedFile = sourceFile;
            SaveOpenFilesList();
            e.Handled = true;
        }

        private static T FindAncestor<T>(DependencyObject dependencyObject) where T : DependencyObject
        {
            while (dependencyObject != null)
            {
                if (dependencyObject is T found)
                    return found;

                dependencyObject = dependencyObject switch
                {
                    Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(dependencyObject),
                    _ => LogicalTreeHelper.GetParent(dependencyObject)
                };
            }

            return null;
        }

        private void UpdateWelcomeVisibility()
        {
            if (WelcomeText != null)
                WelcomeText.Visibility = OpenFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (ContentArea != null)
                ContentArea.Visibility = OpenFiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (FilesTabControl != null)
                FilesTabControl.Visibility = OpenFiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (OpenFiles.Count == 0)
                IsFindReplaceVisible = false;
        }

        #region Editor Content Management

        private void OnSelectedFileChanged()
        {
            if (FileListBox == null || TextEditor == null) return;

            _suppressEditorEvents = true;
            try
            {
                if (_selectedFile != null)
                {
                    FileListBox.ItemsSource = _selectedFile.FileNames;
                    FileListBox.SelectedItem = _selectedFile.SelectedFileName;
                }
                else
                {
                    FileListBox.ItemsSource = null;
                    FileListBox.SelectedItem = null;
                }
                UpdateEditorContent();
            }
            finally
            {
                _suppressEditorEvents = false;
            }
        }

        private void FileListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEditorEvents) return;
            if (SelectedFile != null && FileListBox.SelectedItem is string selectedFile)
            {
                CaptureCurrentEditorViewState();
                SelectedFile.SelectedFileName = selectedFile;
                _suppressEditorEvents = true;
                try { UpdateEditorContent(); }
                finally { _suppressEditorEvents = false; }
            }
        }

        private void UpdateEditorContent()
        {
            bool wasSuppressed = _suppressEditorEvents;
            _suppressEditorEvents = true;
            try
            {
                if (SelectedFile?.SelectedFileName != null &&
                    SelectedFile.FileContents.TryGetValue(SelectedFile.SelectedFileName, out var content))
                {
                    TextEditor.Text = content;
                    TextEditor.SyntaxHighlighting = SelectedFile.IsGame3
                        ? GetXmlHighlighting()
                        : GetIniHighlighting();
                    SelectedFileHeader.Text = SelectedFile.SelectedFileName;
                }
                else
                {
                    TextEditor.Text = "";
                    TextEditor.SyntaxHighlighting = null;
                    SelectedFileHeader.Text = "(no file selected)";
                }

                UpdateXmlTagMatchHighlight();
                UpdateSelectionMatchHighlight();
                UpdateTlkInlineAnnotations();
                RestoreCurrentEditorViewState();
                ResetSearchState();
                UpdateSearchStatus();
            }
            finally
            {
                _suppressEditorEvents = wasSuppressed;
            }
        }

        private void TextEditor_TextChanged(object sender, EventArgs e)
        {
            if (_suppressEditorEvents) return;
            if (SelectedFile?.SelectedFileName != null)
            {
                SelectedFile.FileContents[SelectedFile.SelectedFileName] = TextEditor.Text;
                if (!SelectedFile.HasUnsavedChanges)
                    SelectedFile.HasUnsavedChanges = true;
            }

            UpdateXmlTagMatchHighlight();
            UpdateSelectionMatchHighlight();
            UpdateTlkInlineAnnotations();
            ResetSearchState();
            UpdateSearchStatus();
        }

        private void TextArea_Caret_PositionChanged(object sender, EventArgs e)
        {
            if (_suppressEditorEvents)
                return;

            UpdateXmlTagMatchHighlight();
        }

        private void TextArea_SelectionChanged(object sender, EventArgs e)
        {
            if (_suppressEditorEvents)
                return;

            UpdateSelectionMatchHighlight();
        }

        private void UpdateXmlTagMatchHighlight()
        {
            if (TextEditor?.Document is null || SelectedFile?.IsGame3 != true)
            {
                _xmlTagMatchRenderer.Clear();
                return;
            }

            var text = TextEditor.Text;
            var caretOffset = TextEditor.CaretOffset;
            var match = FindMatchingXmlTagPair(text, caretOffset);
            if (match is null)
            {
                _xmlTagMatchRenderer.Clear();
                return;
            }

            _xmlTagMatchRenderer.SetMatches(match.Value.openIndex, match.Value.openLength, match.Value.closeIndex, match.Value.closeLength);
        }

        private void UpdateSelectionMatchHighlight()
        {
            if (TextEditor?.Document is null)
            {
                _selectionMatchRenderer.Clear();
                return;
            }

            var selection = TextEditor.TextArea.Selection;
            if (selection is null || selection.IsEmpty)
            {
                _selectionMatchRenderer.Clear();
                return;
            }

            string selectedText = selection.GetText();
            if (string.IsNullOrWhiteSpace(selectedText) || selectedText.Contains('\r') || selectedText.Contains('\n'))
            {
                _selectionMatchRenderer.Clear();
                return;
            }

            int selectionStart = TextEditor.SelectionStart;
            int selectionLength = TextEditor.SelectionLength;
            if (selectionLength <= 0)
            {
                _selectionMatchRenderer.Clear();
                return;
            }

            var segments = new List<(int Offset, int Length)>();
            string text = TextEditor.Text;
            int startIndex = 0;

            while (startIndex < text.Length)
            {
                int matchIndex = text.IndexOf(selectedText, startIndex, StringComparison.Ordinal);
                if (matchIndex < 0)
                    break;

                if (matchIndex != selectionStart || selectedText.Length != selectionLength)
                {
                    segments.Add((matchIndex, selectedText.Length));
                }

                startIndex = matchIndex + Math.Max(selectedText.Length, 1);
            }

            _selectionMatchRenderer.SetMatches(segments);
        }

        private static (int openIndex, int openLength, int closeIndex, int closeLength)? FindMatchingXmlTagPair(string text, int caretOffset)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            var tagRegex = new Regex(@"<(?<close>/)?(?<name>[A-Za-z_][A-Za-z0-9_:\-.]*)(?<attributes>[^<>]*?)(?<selfclose>/)?>", RegexOptions.Compiled);
            var stack = new Stack<(string name, Match match)>();
            var pairs = new Dictionary<int, (Match openMatch, Match closeMatch)>();
            Match containingMatch = null;

            foreach (Match tagMatch in tagRegex.Matches(text))
            {
                if (caretOffset >= tagMatch.Index && caretOffset <= tagMatch.Index + tagMatch.Length)
                {
                    containingMatch = tagMatch;
                }

                var tagName = tagMatch.Groups["name"].Value;
                var isClosing = tagMatch.Groups["close"].Success;
                var isSelfClosing = tagMatch.Groups["selfclose"].Success || tagMatch.Value.EndsWith("/>", StringComparison.Ordinal);

                if (string.IsNullOrEmpty(tagName) || isSelfClosing)
                    continue;

                if (!isClosing)
                {
                    stack.Push((tagName, tagMatch));
                    continue;
                }

                while (stack.Count > 0)
                {
                    var openTag = stack.Pop();
                    if (!openTag.name.Equals(tagName, StringComparison.Ordinal))
                        continue;

                    pairs[openTag.match.Index] = (openTag.match, tagMatch);
                    break;
                }
            }

            if (containingMatch is null)
                return null;

            if (containingMatch.Groups["close"].Success)
            {
                foreach (var pair in pairs.Values)
                {
                    if (pair.closeMatch.Index == containingMatch.Index)
                    {
                        return (pair.openMatch.Index, pair.openMatch.Length, pair.closeMatch.Index, pair.closeMatch.Length);
                    }
                }

                return null;
            }

            return pairs.TryGetValue(containingMatch.Index, out var openingPair)
                ? (openingPair.openMatch.Index, openingPair.openMatch.Length, openingPair.closeMatch.Index, openingPair.closeMatch.Length)
                : null;
        }

        #endregion

        #region Find and Replace

        private readonly record struct TextReplacement(int StartOffset, int Length, string ReplacementText);
        private readonly record struct ResolvedTlkAnnotation(int AnchorOffset, int AnchorLength, string Text, bool IsTlkReference = false);
        private readonly record struct CoalescedSearchMatch(int AnchorOffset, int AnchorLength, int SortOffset, int SortSubOffset, int MatchLength, bool IsFriendly);

        private void ShowFindReplace()
        {
            if (SelectedFile == null || TextEditor == null)
                return;

            IsFindReplaceVisible = true;
            if (!string.IsNullOrEmpty(TextEditor.SelectedText))
                FindText = TextEditor.SelectedText;
            else
                UpdateSearchStatus();

            Dispatcher.InvokeAsync(() =>
            {
                FindTextBox.Focus();
                FindTextBox.SelectAll();
            });
        }

        private void CloseFindReplace()
        {
            IsFindReplaceVisible = false;
            TextEditor?.Focus();
        }

        private void FindNext()
        {
            FindNextInternal(searchBackward: false);
        }

        private void FindPrevious()
        {
            FindNextInternal(searchBackward: true);
        }

        private bool FindNextInternal(bool searchBackward)
        {
            if (TextEditor?.Document == null)
                return false;

            if (!TryCreateSearchRegex(out var regex))
                return false;

            var text = TextEditor.Text;
            if (string.IsNullOrEmpty(text))
            {
                SearchStatusText = "Current document is empty.";
                StatusText = SearchStatusText;
                return false;
            }

            var matches = GetSearchMatches(regex, text);
            if (matches.Count == 0)
            {
                SearchStatusText = "Text not found in current document.";
                StatusText = SearchStatusText;
                return false;
            }

            CoalescedSearchMatch? match = searchBackward
                ? FindPreviousSearchMatch(matches)
                : FindNextSearchMatch(matches);

            if (match is null)
            {
                SearchStatusText = "Text not found in current document.";
                StatusText = SearchStatusText;
                return false;
            }

            ApplySearchMatch(match.Value);
            var location = TextEditor.Document.GetLocation(match.Value.AnchorOffset);
            StatusText = match.Value.IsFriendly
                ? $"Found in resolved reference text at line {location.Line}, column {location.Column}."
                : $"Found at line {location.Line}, column {location.Column}.";
            SearchStatusText = BuildMatchCountText(matches.Count);
            return true;
        }

        private void ReplaceCurrent()
        {
            if (TextEditor?.Document == null)
                return;

            if (!TryCreateSearchRegex(out var regex))
                return;

            if (_currentSearchMatch is { IsFriendly: true })
            {
                SearchStatusText = "Resolved reference text is read-only.";
                StatusText = SearchStatusText;
                return;
            }

            if (!TryGetSelectedMatch(regex, out var selectedMatch))
            {
                if (!FindNextInternal(searchBackward: false) || !TryGetSelectedMatch(regex, out selectedMatch))
                    return;
            }

            if (!TryGetReplacementText(selectedMatch, out var replacementText))
                return;

            TextEditor.Document.Replace(selectedMatch.Index, selectedMatch.Length, replacementText);
            TextEditor.Select(selectedMatch.Index, replacementText.Length);
            TextEditor.CaretOffset = selectedMatch.Index + replacementText.Length;
            ResetSearchState();
            StatusText = "Replaced current match.";
            UpdateSearchStatus();
            FindNextInternal(searchBackward: false);
        }

        private void ReplaceAll()
        {
            if (TextEditor?.Document == null)
                return;

            if (!TryCreateSearchRegex(out var regex))
                return;

            var text = TextEditor.Text;
            var replacements = new List<TextReplacement>();
            int startIndex = 0;

            while (startIndex <= text.Length)
            {
                var match = FindNextMatch(regex, text, startIndex);
                if (match == null)
                    break;

                if (!TryGetReplacementText(match, out var replacementText))
                    return;

                replacements.Add(new TextReplacement(match.Index, match.Length, replacementText));
                startIndex = match.Index + Math.Max(match.Length, 1);
            }

            if (replacements.Count == 0)
            {
                StatusText = "No matches to replace.";
                SearchStatusText = StatusText;
                return;
            }

            TextEditor.Document.BeginUpdate();
            try
            {
                for (int i = replacements.Count - 1; i >= 0; i--)
                {
                    var replacement = replacements[i];
                    TextEditor.Document.Replace(replacement.StartOffset, replacement.Length, replacement.ReplacementText);
                }
            }
            finally
            {
                TextEditor.Document.EndUpdate();
            }

            TextEditor.Select(0, 0);
            TextEditor.CaretOffset = 0;
            TextEditor.Focus();
            ResetSearchState();
            StatusText = $"Replaced {replacements.Count} match{(replacements.Count == 1 ? string.Empty : "es")}.";
            UpdateSearchStatus();
        }

        private void FindReplaceTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseFindReplace();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Enter)
                return;

            var returnFocusTextBox = sender as TextBox;
            int selectionStart = returnFocusTextBox?.SelectionStart ?? 0;
            int selectionLength = returnFocusTextBox?.SelectionLength ?? 0;

            if (sender == ReplaceTextBox)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    ReplaceAll();
                else
                    ReplaceCurrent();
            }
            else
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    FindPrevious();
                else
                    FindNext();
            }

            if (returnFocusTextBox != null)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    returnFocusTextBox.Focus();
                    returnFocusTextBox.Select(selectionStart, selectionLength);
                });
            }

            e.Handled = true;
        }

        private void UpdateSearchStatus()
        {
            if (TextEditor?.Document == null)
            {
                SearchStatusText = "No document selected.";
                return;
            }

            if (string.IsNullOrEmpty(FindText))
            {
                SearchStatusText = "Enter text to search.";
                return;
            }

            if (!TryCreateSearchRegex(out var regex, updateStatusOnError: true))
                return;

            SearchStatusText = BuildMatchCountText(GetSearchMatches(regex, TextEditor.Text).Count);
        }

        private bool TryCreateSearchRegex(out Regex regex, bool updateStatusOnError = false)
        {
            regex = null;
            if (string.IsNullOrEmpty(FindText))
            {
                if (updateStatusOnError)
                    SearchStatusText = "Enter text to search.";
                return false;
            }

            try
            {
                var pattern = FindUseRegex ? FindText : Regex.Escape(FindText);
                if (FindWholeWord)
                    pattern = $@"\b(?:{pattern})\b";

                var options = RegexOptions.CultureInvariant | RegexOptions.Multiline;
                if (!FindMatchCase)
                    options |= RegexOptions.IgnoreCase;

                regex = new Regex(pattern, options);
                return true;
            }
            catch (ArgumentException ex)
            {
                if (updateStatusOnError)
                    SearchStatusText = $"Invalid regular expression: {ex.Message}";
                StatusText = "Invalid search pattern.";
                return false;
            }
        }

        private string BuildMatchCountText(int count)
        {
            return count == 0
                ? "No matches in current document."
                : $"{count} match{(count == 1 ? string.Empty : "es")} in current document.";
        }

        private void ResetSearchState()
        {
            _currentSearchMatch = null;
        }

        private List<CoalescedSearchMatch> GetSearchMatches(Regex regex, string text)
        {
            var matches = new List<CoalescedSearchMatch>();

            int startIndex = 0;
            while (startIndex <= text.Length)
            {
                var match = FindNextMatch(regex, text, startIndex);
                if (match == null)
                    break;

                matches.Add(new CoalescedSearchMatch(match.Index, match.Length, match.Index, match.Index, match.Length, false));
                startIndex = match.Index + Math.Max(match.Length, 1);
            }

            foreach (var annotation in GetResolvedTlkAnnotations(text))
            {
                startIndex = 0;
                while (startIndex <= annotation.Text.Length)
                {
                    var match = FindNextMatch(regex, annotation.Text, startIndex);
                    if (match == null)
                        break;

                    matches.Add(new CoalescedSearchMatch(annotation.AnchorOffset, annotation.AnchorLength, annotation.AnchorOffset, match.Index, match.Length, true));
                    startIndex = match.Index + Math.Max(match.Length, 1);
                }
            }

            return matches.OrderBy(m => m.SortOffset)
                         .ThenBy(m => m.IsFriendly ? 1 : 0)
                         .ThenBy(m => m.SortSubOffset)
                         .ToList();
        }

        private CoalescedSearchMatch? FindNextSearchMatch(List<CoalescedSearchMatch> matches)
        {
            if (_currentSearchMatch is CoalescedSearchMatch currentMatch)
            {
                int currentIndex = matches.FindIndex(match => match.Equals(currentMatch));
                if (currentIndex >= 0)
                {
                    if (currentIndex + 1 < matches.Count)
                        return matches[currentIndex + 1];

                    return FindWrapAround ? matches[0] : null;
                }
            }

            int startOffset = GetForwardSearchStart();
            foreach (var match in matches)
            {
                if (match.SortOffset >= startOffset)
                    return match;
            }

            return FindWrapAround ? matches[0] : null;
        }

        private CoalescedSearchMatch? FindPreviousSearchMatch(List<CoalescedSearchMatch> matches)
        {
            if (_currentSearchMatch is CoalescedSearchMatch currentMatch)
            {
                int currentIndex = matches.FindIndex(match => match.Equals(currentMatch));
                if (currentIndex >= 0)
                {
                    if (currentIndex > 0)
                        return matches[currentIndex - 1];

                    return FindWrapAround ? matches[^1] : null;
                }
            }

            int startOffset = GetBackwardSearchStart();
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                if (matches[i].SortOffset < startOffset)
                    return matches[i];
            }

            return FindWrapAround ? matches[^1] : null;
        }

        private void ApplySearchMatch(CoalescedSearchMatch match)
        {
            _currentSearchMatch = match;
            TextEditor.Focus();
            TextEditor.Select(match.AnchorOffset, match.AnchorLength);
            TextEditor.CaretOffset = match.AnchorOffset + match.AnchorLength;
            var location = TextEditor.Document.GetLocation(match.AnchorOffset);
            TextEditor.ScrollToLine(location.Line);
        }

        private int GetForwardSearchStart()
        {
            return TextEditor.SelectionLength > 0
                ? TextEditor.SelectionStart + TextEditor.SelectionLength
                : TextEditor.CaretOffset;
        }

        private int GetBackwardSearchStart()
        {
            return TextEditor.SelectionLength > 0
                ? TextEditor.SelectionStart
                : TextEditor.CaretOffset;
        }

        private void SelectMatch(Match match)
        {
            TextEditor.Focus();
            TextEditor.Select(match.Index, match.Length);
            TextEditor.CaretOffset = match.Index + match.Length;
            var location = TextEditor.Document.GetLocation(match.Index);
            TextEditor.ScrollToLine(location.Line);
        }

        private bool TryGetSelectedMatch(Regex regex, out Match selectedMatch)
        {
            selectedMatch = null;
            if (TextEditor.SelectionLength == 0)
                return false;

            var match = regex.Match(TextEditor.Text, TextEditor.SelectionStart);
            if (!match.Success || match.Length == 0)
                return false;

            if (match.Index != TextEditor.SelectionStart || match.Length != TextEditor.SelectionLength)
                return false;

            selectedMatch = match;
            return true;
        }

        private bool TryGetReplacementText(Match match, out string replacementText)
        {
            replacementText = ReplaceText ?? string.Empty;
            if (!FindUseRegex)
                return true;

            try
            {
                replacementText = match.Result(replacementText);
                return true;
            }
            catch (ArgumentException ex)
            {
                StatusText = $"Invalid replacement pattern: {ex.Message}";
                SearchStatusText = StatusText;
                return false;
            }
        }

        private static Match FindNextMatch(Regex regex, string text, int startIndex)
        {
            startIndex = Math.Clamp(startIndex, 0, text.Length);
            var match = regex.Match(text, startIndex);

            while (match.Success && match.Length == 0)
            {
                if (match.Index >= text.Length)
                    return null;

                match = regex.Match(text, match.Index + 1);
            }

            return match.Success ? match : null;
        }

        private static Match FindPreviousMatch(Regex regex, string text, int startIndex)
        {
            startIndex = Math.Clamp(startIndex, 0, text.Length);
            Match previousMatch = null;
            int currentIndex = 0;

            while (currentIndex <= text.Length)
            {
                var match = FindNextMatch(regex, text, currentIndex);
                if (match == null || match.Index >= startIndex)
                    break;

                previousMatch = match;
                currentIndex = match.Index + Math.Max(match.Length, 1);
            }

            return previousMatch;
        }

        #endregion

        #region TLK Reference Resolution

        private void UpdateTlkInlineAnnotations()
        {
            var text = TextEditor?.Text;
            var game = GetSelectedFileGame();
            if (string.IsNullOrWhiteSpace(text) || game == MEGame.Unknown)
            {
                _tlkInlineAnnotationGenerator.SetAnnotations([]);
                TextEditor?.TextArea.TextView.Redraw();
                return;
            }

            var annotations = GetResolvedTlkAnnotations(text)
                .Select(annotation => new TlkInlineAnnotation(
                    annotation.AnchorOffset + annotation.AnchorLength,
                    annotation.AnchorOffset,
                    annotation.AnchorLength,
                    annotation.Text,
                    annotation.IsTlkReference))
                .ToList();

            _tlkInlineAnnotationGenerator.SetAnnotations(annotations);
            TextEditor.TextArea.TextView.Redraw();
        }

        private List<ResolvedTlkAnnotation> GetResolvedTlkAnnotations(string text)
        {
            var game = GetSelectedFileGame();
            if (!ShowTlkBoxes || string.IsNullOrWhiteSpace(text) || game == MEGame.Unknown)
                return [];

            var annotations = new Dictionary<int, ResolvedTlkAnnotation>();

            foreach (var plotAnnotation in GetResolvedPlotAnnotations(text, game))
            {
                annotations[plotAnnotation.AnchorOffset] = plotAnnotation;
            }

            var seenOffsets = new HashSet<int>();
            foreach (Match match in TlkReferenceRegex.Matches(text))
            {
                if (!int.TryParse(match.Value, out int tlkId) || !seenOffsets.Add(match.Index))
                    continue;

                if (annotations.ContainsKey(match.Index))
                    continue;

                var friendly = TLKManagerWPF.GlobalFindStrRefbyID(tlkId, game);
                if (string.IsNullOrWhiteSpace(friendly) || friendly == "No Data" || friendly == "UDK String Refs Not Supported")
                    continue;

                annotations[match.Index] = new ResolvedTlkAnnotation(match.Index, match.Length, StripWrappingQuotes(friendly), true);
            }

            return annotations.Values.OrderBy(annotation => annotation.AnchorOffset).ToList();
        }

        private void SelectTlkStringRef(int anchorOffset, int anchorLength)
        {
            var game = GetSelectedFileGame();
            if (game == MEGame.Unknown || TextEditor?.Document == null || anchorOffset < 0 || anchorLength <= 0 || anchorOffset + anchorLength > TextEditor.Document.TextLength)
                return;

            int? selectedStringRef = TlkStringRefSelector.SelectStringRef(this, game);
            if (selectedStringRef is not int stringRef)
                return;

            TextEditor.Document.Replace(anchorOffset, anchorLength, stringRef.ToString(CultureInfo.InvariantCulture));
        }

        private List<ResolvedTlkAnnotation> GetResolvedPlotAnnotations(string text, MEGame game)
        {
            var annotations = new Dictionary<int, ResolvedTlkAnnotation>();

            foreach (Match match in PlotXmlPropertyRegex.Matches(text))
            {
                if (TryCreatePlotAnnotation(match.Groups["key"].Value, match.Groups["id"].Value, match.Groups["id"].Index, match.Groups["id"].Length, game, out var annotation))
                {
                    annotations[annotation.AnchorOffset] = annotation;
                }
            }

            foreach (Match match in PlotAssignmentRegex.Matches(text))
            {
                if (TryCreatePlotAnnotation(match.Groups["key"].Value, match.Groups["id"].Value, match.Groups["id"].Index, match.Groups["id"].Length, game, out var annotation))
                {
                    annotations.TryAdd(annotation.AnchorOffset, annotation);
                }
            }

            return annotations.Values.OrderBy(annotation => annotation.AnchorOffset).ToList();
        }

        private static bool TryCreatePlotAnnotation(string key, string idText, int anchorOffset, int anchorLength, MEGame game, out ResolvedTlkAnnotation annotation)
        {
            annotation = default;

            if (!int.TryParse(idText, out int plotId))
                return false;

            foreach (var plotType in GetPlotElementTypesForKey(key))
            {
                var plotElement = PlotDatabases.FindPlotElementFromID(plotId, plotType, game);
                if (plotElement == null || string.IsNullOrWhiteSpace(plotElement.Path))
                    continue;

                annotation = new ResolvedTlkAnnotation(anchorOffset, anchorLength, plotElement.Path);
                return true;
            }

            return false;
        }

        private static List<PlotElementType> GetPlotElementTypesForKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return [];

            var normalizedKey = new string(key.Where(char.IsLetter).ToArray()).ToLowerInvariant();
            if (string.IsNullOrEmpty(normalizedKey))
                return [];

            var plotTypes = new List<PlotElementType>();

            void AddPlotType(PlotElementType plotType)
            {
                if (!plotTypes.Contains(plotType))
                    plotTypes.Add(plotType);
            }

            if (normalizedKey.Contains("conditional"))
            {
                AddPlotType(PlotElementType.Conditional);
            }

            if (normalizedKey.Contains("consequence"))
            {
                AddPlotType(PlotElementType.Consequence);
            }

            if (normalizedKey.Contains("transition"))
            {
                AddPlotType(PlotElementType.Transition);
            }

            if (normalizedKey.Contains("substate"))
            {
                AddPlotType(PlotElementType.SubState);
            }

            if (normalizedKey.Contains("integer") || normalizedKey.EndsWith("int", StringComparison.Ordinal))
            {
                AddPlotType(PlotElementType.Integer);
            }

            if (normalizedKey.EndsWith("plot", StringComparison.Ordinal))
            {
                AddPlotType(PlotElementType.Integer);
            }

            if (normalizedKey.Contains("float"))
            {
                AddPlotType(PlotElementType.Float);
            }

            if (normalizedKey.Contains("state") || normalizedKey.Contains("flag") || normalizedKey.Contains("bool"))
            {
                AddPlotType(PlotElementType.State);
            }

            return plotTypes;
        }

        private static string StripWrappingQuotes(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2)
                return text ?? string.Empty;

            return (text[0], text[^1]) switch
            {
                ('"', '"') => text[1..^1],
                ('\'', '\'') => text[1..^1],
                ('“', '”') => text[1..^1],
                ('‘', '’') => text[1..^1],
                _ => text
            };
        }

        private MEGame GetSelectedFileGame()
        {
            var filePath = SelectedFile?.FilePath;
            if (string.IsNullOrWhiteSpace(filePath))
                return MEGame.Unknown;

            string fullPath = Path.GetFullPath(filePath);
            if (IsUnderDirectory(fullPath, ME1Directory.BioGamePath)) return MEGame.ME1;
            if (IsUnderDirectory(fullPath, ME2Directory.BioGamePath)) return MEGame.ME2;
            if (IsUnderDirectory(fullPath, ME3Directory.BioGamePath)) return MEGame.ME3;
            if (IsUnderDirectory(fullPath, LE1Directory.BioGamePath)) return MEGame.LE1;
            if (IsUnderDirectory(fullPath, LE2Directory.BioGamePath)) return MEGame.LE2;
            if (IsUnderDirectory(fullPath, LE3Directory.BioGamePath)) return MEGame.LE3;

            return MEGame.Unknown;
        }

        private static bool IsUnderDirectory(string path, string directory)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
                return false;

            string normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalizedPath.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(normalizedPath, normalizedDirectory, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

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
                CaptureCurrentEditorViewState();
                var state = new CoalescedEditorState
                {
                    OpenFiles = OpenFiles.Select(f => new CoalescedEditorFileState
                    {
                        FilePath = f.FilePath,
                        SelectedInnerFileName = f.SelectedFileName,
                        DocumentViews = f.DocumentViewStates.Values
                            .Select(view => new CoalescedEditorDocumentViewState
                            {
                                FileName = view.FileName,
                                CaretOffset = view.CaretOffset,
                                VerticalOffset = view.VerticalOffset,
                                HorizontalOffset = view.HorizontalOffset
                            })
                            .ToList()
                    }).ToList(),
                    SelectedOpenFilePath = SelectedFile?.FilePath,
                    ShowTlkBoxes = ShowTlkBoxes
                };

                var json = JsonSerializer.Serialize(state);
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

                _isRestoringState = true;
                var json = File.ReadAllText(StateFilePath);
                var state = JsonSerializer.Deserialize<CoalescedEditorState>(json);
                if (state?.OpenFiles != null)
                {
                    ShowTlkBoxes = state.ShowTlkBoxes;
                    foreach (var fileState in state.OpenFiles)
                    {
                        if (File.Exists(fileState.FilePath))
                        {
                            LoadCoalescedFile(fileState.FilePath);
                            var openFile = OpenFiles.FirstOrDefault(f => f.FilePath.Equals(fileState.FilePath, StringComparison.OrdinalIgnoreCase));
                            if (openFile == null)
                                continue;

                            foreach (var viewState in fileState.DocumentViews ?? [])
                            {
                                if (string.IsNullOrWhiteSpace(viewState.FileName))
                                    continue;

                                openFile.DocumentViewStates[viewState.FileName] = viewState;
                            }

                            if (!string.IsNullOrWhiteSpace(fileState.SelectedInnerFileName) && openFile.FileContents.ContainsKey(fileState.SelectedInnerFileName))
                            {
                                openFile.SelectedFileName = fileState.SelectedInnerFileName;
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(state.SelectedOpenFilePath))
                    {
                        SelectedFile = OpenFiles.FirstOrDefault(f => f.FilePath.Equals(state.SelectedOpenFilePath, StringComparison.OrdinalIgnoreCase)) ?? SelectedFile;
                    }

                    return;
                }

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
            finally
            {
                _isRestoringState = false;
            }
        }

        private void CaptureCurrentEditorViewState()
        {
            if (SelectedFile?.SelectedFileName == null || TextEditor?.Document == null)
                return;

            SelectedFile.DocumentViewStates[SelectedFile.SelectedFileName] = new CoalescedEditorDocumentViewState
            {
                FileName = SelectedFile.SelectedFileName,
                CaretOffset = TextEditor.CaretOffset,
                VerticalOffset = TextEditor.VerticalOffset,
                HorizontalOffset = TextEditor.HorizontalOffset
            };
        }

        private void RestoreCurrentEditorViewState()
        {
            if (SelectedFile?.SelectedFileName == null || TextEditor?.Document == null)
                return;

            if (!SelectedFile.DocumentViewStates.TryGetValue(SelectedFile.SelectedFileName, out var state))
                return;

            int caretOffset = Math.Clamp(state.CaretOffset, 0, TextEditor.Document.TextLength);
            TextEditor.Select(0, 0);
            TextEditor.CaretOffset = caretOffset;

            Dispatcher.InvokeAsync(() =>
            {
                TextEditor.ScrollToVerticalOffset(Math.Max(0, state.VerticalOffset));
                TextEditor.ScrollToHorizontalOffset(Math.Max(0, state.HorizontalOffset));
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        #endregion

        #region Game3 Manifest Support

        private static string GetGame3ManifestFileName(string filePath)
        {
            var baseName = Path.GetFileNameWithoutExtension(filePath);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = DefaultGame3ManifestBaseName;

            return $"{baseName}.xml";
        }

        private static bool TryGetGame3ManifestFileName(OpenCoalescedFile coalFile, out string manifestName)
        {
            foreach (var fileName in coalFile.FileNames)
            {
                if (!coalFile.FileContents.TryGetValue(fileName, out var content) || string.IsNullOrWhiteSpace(content))
                    continue;

                try
                {
                    if (XDocument.Parse(content).Root?.Name.LocalName == "CoalesceFile")
                    {
                        manifestName = fileName;
                        return true;
                    }
                }
                catch
                {
                }
            }

            manifestName = null;
            return false;
        }

        private static string BuildGame3ManifestXml(string filePath, IEnumerable<string> assetFileNames)
        {
            var manifestName = Path.GetFileName(filePath) ?? "Coalesced.bin";
            var manifestId = Path.GetFileNameWithoutExtension(filePath);
            if (string.IsNullOrWhiteSpace(manifestId))
                manifestId = DefaultGame3ManifestBaseName;

            var root = new XElement("CoalesceFile");
            root.SetAttributeValue("id", manifestId);
            root.SetAttributeValue("name", manifestName);

            var assetsElement = new XElement("Assets");
            foreach (var assetFileName in assetFileNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                assetsElement.Add(new XElement("Asset", new XAttribute("source", assetFileName)));
            }

            root.Add(assetsElement);
            var document = new XDocument(root);
            using var writer = new Utf8StringWriter();
            document.Save(writer, SaveOptions.None);
            return writer.ToString();
        }

        private static MemoryStream CompileGame3CoalescedFromMemory(OpenCoalescedFile coalFile, string destinationPath)
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "LegendaryExplorer", "CoalescedEditor", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                if (!TryGetGame3ManifestFileName(coalFile, out var manifestName))
                {
                    manifestName = GetGame3ManifestFileName(destinationPath);
                }

                var assetFileNames = coalFile.FileContents.Keys.Where(name => !name.Equals(manifestName, StringComparison.OrdinalIgnoreCase)).ToList();

                foreach (var kvp in coalFile.FileContents)
                {
                    File.WriteAllText(Path.Combine(tempDirectory, kvp.Key), kvp.Value);
                }

                var manifestPath = Path.Combine(tempDirectory, manifestName);
                if (!File.Exists(manifestPath))
                {
                    File.WriteAllText(manifestPath, BuildGame3ManifestXml(destinationPath, assetFileNames));
                }

                var output = new MemoryStream();
                CoalescedConverter.ConvertToBin(manifestPath, destinationPath, outStream: output);
                output.Position = 0;
                return output;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDirectory))
                        Directory.Delete(tempDirectory, true);
                }
                catch
                {
                }
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

        #region Syntax Highlighting

        private static IHighlightingDefinition _iniHighlighting;
        private static IHighlightingDefinition _xmlHighlighting;

        private static IHighlightingDefinition GetXmlHighlighting()
        {
            if (_xmlHighlighting != null) return _xmlHighlighting;

            var xshd = @"<?xml version=""1.0""?>
<SyntaxDefinition name=""CoalescedXml"" xmlns=""http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"">
    <Color name=""XmlTag"" foreground=""#C586C0"" />
    <Color name=""XmlElementName"" foreground=""#4FC1FF"" fontWeight=""bold"" />
    <Color name=""XmlAttributeName"" foreground=""#9CDCFE"" />
    <Color name=""XmlAttributeValue"" foreground=""#CE9178"" />
    <Color name=""XmlComment"" foreground=""#6A9955"" />
    <Color name=""XmlCData"" foreground=""#DCDCAA"" />
    <Color name=""XmlText"" foreground=""#D4D4D4"" />
    <RuleSet>
        <Span color=""XmlComment"">
            <Begin>&lt;!--</Begin>
            <End>--&gt;</End>
        </Span>
        <Span color=""XmlCData"">
            <Begin>&lt;!\[CDATA\[</Begin>
            <End>\]\]&gt;</End>
        </Span>
        <Span color=""XmlTag"">
            <Begin>&lt;\?</Begin>
            <End>\?&gt;</End>
            <RuleSet>
                <Rule color=""XmlTag"">[&lt;&gt;\?/=]</Rule>
                <Rule color=""XmlAttributeName"">[A-Za-z_:@][A-Za-z0-9_:\-\.]*</Rule>
                <Span color=""XmlAttributeValue"">
                    <Begin>""</Begin>
                    <End>""</End>
                </Span>
                <Span color=""XmlAttributeValue"">
                    <Begin>'</Begin>
                    <End>'</End>
                </Span>
            </RuleSet>
        </Span>
        <Span color=""XmlTag"">
            <Begin>&lt;</Begin>
            <End>&gt;</End>
            <RuleSet>
                <Rule color=""XmlTag"">[&lt;&gt;/=]</Rule>
                <Rule color=""XmlElementName"">/?[A-Za-z_][A-Za-z0-9_:\-\.]*</Rule>
                <Rule color=""XmlAttributeName"">[A-Za-z_:@][A-Za-z0-9_:\-\.]*</Rule>
                <Span color=""XmlAttributeValue"">
                    <Begin>""</Begin>
                    <End>""</End>
                </Span>
                <Span color=""XmlAttributeValue"">
                    <Begin>'</Begin>
                    <End>'</End>
                </Span>
            </RuleSet>
        </Span>
        <Rule color=""XmlText"">[^&lt;]+</Rule>
    </RuleSet>
</SyntaxDefinition>";

            using var reader = XmlReader.Create(new StringReader(xshd));
            _xmlHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            return _xmlHighlighting;
        }

        private static IHighlightingDefinition GetIniHighlighting()
        {
            if (_iniHighlighting != null) return _iniHighlighting;

            var xshd = @"<?xml version=""1.0""?>
<SyntaxDefinition name=""CoalescedIni"" xmlns=""http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"">
    <Color name=""Section"" foreground=""Teal"" fontWeight=""bold"" />
    <Color name=""Comment"" foreground=""Green"" fontStyle=""italic"" />
    <Color name=""MultilineMarker"" foreground=""DarkOrange"" fontWeight=""bold"" />
    <RuleSet>
        <Span color=""Comment""><Begin>;</Begin></Span>
        <Span color=""Section""><Begin>\[</Begin><End>\]</End></Span>
        <Rule color=""MultilineMarker"">\|\|=</Rule>
    </RuleSet>
</SyntaxDefinition>";

            using var reader = XmlReader.Create(new StringReader(xshd));
            _iniHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            return _iniHighlighting;
        }

        #endregion

        /// <summary>
        /// Returns true if the given file path looks like a coalesced .bin file based on its name.
        /// Matches: Coalesced.bin, Default_DLC_MOD_*.bin, Default_DLC_*.bin
        /// </summary>
        public static bool IsCoalescedBinFileName(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            if (fileName == null) return false;
            if (fileName.Equals("Coalesced.bin", StringComparison.OrdinalIgnoreCase))
                return true;
            if (fileName.StartsWith("Default_DLC_", StringComparison.OrdinalIgnoreCase) &&
                fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private sealed class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
        }
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

        private DateTime? _lastSaved;
        public DateTime? LastSaved
        {
            get => _lastSaved;
            set
            {
                if (_lastSaved != value)
                {
                    _lastSaved = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LastSavedDisplay));
                }
            }
        }

        public string LastSavedDisplay => LastSaved.HasValue
            ? $"Last saved: {LastSaved.Value:G}"
            : string.Empty;

        public bool IsGame3 { get; set; }

        public Dictionary<string, string> FileContents { get; set; } = new();
        public Dictionary<string, CoalescedEditorDocumentViewState> DocumentViewStates { get; } = new(StringComparer.OrdinalIgnoreCase);

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

    public class IndentedPathDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string path || string.IsNullOrWhiteSpace(path))
                return string.Empty;

            var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1)
                return path;

            var sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                    sb.AppendLine();

                sb.Append(' ', i * 2);
                sb.Append(parts[i]);
            }

            return sb.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    public class XmlTagMatchRenderer : IBackgroundRenderer
    {
        private readonly Brush _backgroundBrush = new SolidColorBrush(Color.FromArgb(70, 51, 153, 255));
        private readonly Pen _borderPen = new(new SolidColorBrush(Color.FromArgb(140, 110, 190, 255)), 1);
        private readonly TextSegmentCollection<TextSegment> _segments;
        private TextView _textView;

        public XmlTagMatchRenderer()
        {
            _backgroundBrush.Freeze();
            _borderPen.Freeze();
            _segments = new TextSegmentCollection<TextSegment>();
        }

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            _textView = textView;
            if (_segments.Count == 0 || !textView.VisualLinesValid)
                return;

            foreach (var segment in _segments)
            {
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                {
                    var geometry = new RectangleGeometry(new Rect(rect.Location, new Size(rect.Width, rect.Height)));
                    drawingContext.DrawGeometry(_backgroundBrush, _borderPen, geometry);
                }
            }
        }

        public void SetMatches(int firstOffset, int firstLength, int secondOffset, int secondLength)
        {
            _segments.Clear();
            _segments.Add(new TextSegment { StartOffset = firstOffset, Length = firstLength });
            _segments.Add(new TextSegment { StartOffset = secondOffset, Length = secondLength });
            _textView?.InvalidateLayer(Layer);
        }

        public void Clear()
        {
            if (_segments.Count == 0)
                return;

            _segments.Clear();
            _textView?.InvalidateLayer(Layer);
        }
    }

    public class SelectionMatchRenderer : IBackgroundRenderer
    {
        private readonly Brush _backgroundBrush = new SolidColorBrush(Color.FromArgb(90, 214, 157, 0));
        private readonly Pen _borderPen = new(new SolidColorBrush(Color.FromArgb(180, 255, 204, 102)), 1);
        private readonly TextSegmentCollection<TextSegment> _segments;
        private TextView _textView;

        public SelectionMatchRenderer()
        {
            _backgroundBrush.Freeze();
            _borderPen.Freeze();
            _segments = new TextSegmentCollection<TextSegment>();
        }

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            _textView = textView;
            if (_segments.Count == 0 || !textView.VisualLinesValid)
                return;

            foreach (var segment in _segments)
            {
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                {
                    var geometry = new RectangleGeometry(new Rect(rect.Location, new Size(rect.Width, rect.Height)));
                    drawingContext.DrawGeometry(_backgroundBrush, _borderPen, geometry);
                }
            }
        }

        public void SetMatches(IEnumerable<(int Offset, int Length)> matches)
        {
            _segments.Clear();

            foreach (var match in matches)
            {
                if (match.Length <= 0)
                    continue;

                _segments.Add(new TextSegment { StartOffset = match.Offset, Length = match.Length });
            }

            _textView?.InvalidateLayer(Layer);
        }

        public void Clear()
        {
            if (_segments.Count == 0)
                return;

            _segments.Clear();
            _textView?.InvalidateLayer(Layer);
        }
    }

    internal readonly record struct TlkInlineAnnotation(int Offset, int AnchorOffset, int AnchorLength, string Text, bool HasTlkLookup);

    public class TlkInlineAnnotationGenerator : VisualLineElementGenerator
    {
        private const double CollapsedWidth = 364;
        private const double CollapsedHeight = 24;
        private const double ExpandedWidth = 364;
        private const double ExpandedHeight = 96;
        private readonly Dictionary<int, TlkInlineAnnotation> _annotations = new();
        private readonly List<int> _offsets = new();
        private readonly Action<int, int> _selectTlkStringRef;

        public TlkInlineAnnotationGenerator(Action<int, int> selectTlkStringRef)
        {
            _selectTlkStringRef = selectTlkStringRef;
        }

        internal void SetAnnotations(IEnumerable<TlkInlineAnnotation> annotations)
        {
            _annotations.Clear();
            _offsets.Clear();

            foreach (var annotation in annotations)
            {
                if (annotation.Offset < 0 || string.IsNullOrWhiteSpace(annotation.Text))
                    continue;

                _annotations[annotation.Offset] = annotation;
                _offsets.Add(annotation.Offset);
            }

            _offsets.Sort();
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (_offsets.Count == 0)
                return -1;

            int endOffset = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
            int index = _offsets.BinarySearch(startOffset);
            if (index < 0)
                index = ~index;

            if (index >= _offsets.Count)
                return -1;

            int offset = _offsets[index];
            return offset >= startOffset && offset <= endOffset ? offset : -1;
        }

        public override VisualLineElement ConstructElement(int offset)
        {
            if (!_annotations.TryGetValue(offset, out var annotation))
                return null;

            var textBox = new TextBox
            {
                Text = annotation.Text,
                IsReadOnly = true,
                Focusable = true,
                IsTabStop = false,
                Margin = new Thickness(0),
                Padding = new Thickness(6, 1, 6, 1),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(122, 122, 122)),
                Foreground = new SolidColorBrush(Color.FromRgb(241, 241, 241)),
                VerticalContentAlignment = VerticalAlignment.Top,
                Width = annotation.HasTlkLookup ? CollapsedWidth - 34 : CollapsedWidth,
                Height = CollapsedHeight,
                MinWidth = annotation.HasTlkLookup ? CollapsedWidth - 34 : CollapsedWidth,
                MaxWidth = annotation.HasTlkLookup ? CollapsedWidth - 34 : CollapsedWidth,
                MinHeight = CollapsedHeight,
                MaxHeight = CollapsedHeight,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var contextMenu = new ContextMenu();
            var copyMenuItem = new MenuItem { Header = "Copy" };
            copyMenuItem.Click += (_, _) =>
            {
                string textToCopy = string.IsNullOrEmpty(textBox.SelectedText) ? textBox.Text : textBox.SelectedText;
                if (!string.IsNullOrEmpty(textToCopy))
                {
                    Clipboard.SetText(textToCopy);
                }
            };
            contextMenu.Items.Add(copyMenuItem);
            var expandMenuItem = new MenuItem { Header = "Expand" };
            expandMenuItem.Click += (_, _) => ShowExpandedPopup(textBox, annotation.Text);
            contextMenu.Items.Add(expandMenuItem);
            textBox.ContextMenu = contextMenu;

            if (!annotation.HasTlkLookup)
            {
                textBox.Margin = new Thickness(6, -1, 0, -1);
                return new InlineObjectElement(0, textBox);
            }

            var lookupButton = new Button
            {
                Content = "...",
                Width = 28,
                Height = CollapsedHeight,
                Margin = new Thickness(6, -1, 0, -1),
                Padding = new Thickness(0),
                ToolTip = "Find a StringRef by text in the loaded TLKs",
                Focusable = false
            };
            lookupButton.Click += (_, _) => _selectTlkStringRef(annotation.AnchorOffset, annotation.AnchorLength);

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(6, 0, 0, 0)
            };
            panel.Children.Add(textBox);
            panel.Children.Add(lookupButton);
            return new InlineObjectElement(0, panel);
        }

        private static void ShowExpandedPopup(TextBox placementTarget, string annotation)
        {
            var expandedTextBox = new TextBox
            {
                Text = annotation,
                IsReadOnly = true,
                Focusable = true,
                Width = ExpandedWidth,
                Height = ExpandedHeight,
                MinWidth = ExpandedWidth,
                MaxWidth = ExpandedWidth,
                MinHeight = ExpandedHeight,
                MaxHeight = ExpandedHeight,
                Padding = new Thickness(6, 4, 6, 4),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(122, 122, 122)),
                Foreground = new SolidColorBrush(Color.FromRgb(241, 241, 241)),
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var contextMenu = new ContextMenu();
            var copyMenuItem = new MenuItem { Header = "Copy" };
            copyMenuItem.Click += (_, _) =>
            {
                string textToCopy = string.IsNullOrEmpty(expandedTextBox.SelectedText) ? expandedTextBox.Text : expandedTextBox.SelectedText;
                if (!string.IsNullOrEmpty(textToCopy))
                {
                    Clipboard.SetText(textToCopy);
                }
            };
            contextMenu.Items.Add(copyMenuItem);
            expandedTextBox.ContextMenu = contextMenu;

            var popupBorder = new Border
            {
                Background = expandedTextBox.Background,
                BorderBrush = expandedTextBox.BorderBrush,
                BorderThickness = new Thickness(1),
                Child = expandedTextBox
            };

            var popup = new Popup
            {
                PlacementTarget = placementTarget,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = popupBorder,
                IsOpen = true
            };

            expandedTextBox.LostKeyboardFocus += (_, _) => popup.IsOpen = false;
            popup.Closed += (_, _) =>
            {
                if (expandedTextBox.IsKeyboardFocusWithin)
                {
                    Keyboard.ClearFocus();
                }

                expandedTextBox.ClearValue(TextBox.TextProperty);
            };

            expandedTextBox.Focus();
            expandedTextBox.Select(0, 0);
        }
    }

    internal sealed class CoalescedEditorState
    {
        public List<CoalescedEditorFileState> OpenFiles { get; set; } = [];
        public string SelectedOpenFilePath { get; set; }
        public bool ShowTlkBoxes { get; set; } = true;
    }

    internal sealed class CoalescedEditorFileState
    {
        public string FilePath { get; set; }
        public string SelectedInnerFileName { get; set; }
        public List<CoalescedEditorDocumentViewState> DocumentViews { get; set; } = [];
    }

    public sealed class CoalescedEditorDocumentViewState
    {
        public string FileName { get; set; }
        public int CaretOffset { get; set; }
        public double VerticalOffset { get; set; }
        public double HorizontalOffset { get; set; }
    }
}
