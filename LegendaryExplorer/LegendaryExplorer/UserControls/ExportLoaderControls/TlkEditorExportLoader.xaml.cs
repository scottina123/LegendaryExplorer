using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.IO;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Microsoft.Win32;
using System.Media;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.TLK.ME1;
using LegendaryExplorerCore.TLK.ME2ME3;
using HuffmanCompression = LegendaryExplorerCore.TLK.ME1.HuffmanCompression;
using ME2ME3HuffmanCompression = LegendaryExplorerCore.TLK.ME2ME3.HuffmanCompression;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.UserControls.ExportLoaderControls
{
    /// <summary>
    /// Interaction logic for TLKEditor.xaml
    /// </summary>
    public partial class TLKEditorExportLoader : FileExportLoaderControl
    {
        private sealed class CopiedTLKLine
        {
            public CopiedTLKLine(int stringId, string data, int flags)
            {
                StringID = stringId;
                Data = data;
                Flags = flags;
            }

            public int StringID { get; }
            public string Data { get; }
            public int Flags { get; }
        }

        private static CopiedTLKLine _copiedLine;

        public sealed class TLKEditorTab : NotifyPropertyChangedBase
        {
            private string _filePath;
            private bool _isModified;

            public TLKEditorTab(string filePath, ME2ME3TalkFile talkFile)
            {
                _filePath = filePath;
                TalkFile = talkFile;
                LoadedStrings = talkFile.StringRefs.ToList();
            }

            public string FilePath
            {
                get => _filePath;
                set
                {
                    if (SetProperty(ref _filePath, value))
                    {
                        OnPropertyChanged(nameof(DisplayName));
                        OnPropertyChanged(nameof(HeaderText));
                    }
                }
            }

            public string DisplayName => string.IsNullOrWhiteSpace(FilePath) ? "Unsaved TLK" : Path.GetFileName(FilePath);
            public string HeaderText => IsModified ? $"{DisplayName} *" : DisplayName;

            public ME2ME3TalkFile TalkFile { get; set; }
            public List<TLKStringRef> LoadedStrings { get; set; }

            public bool IsModified
            {
                get => _isModified;
                set
                {
                    if (SetProperty(ref _isModified, value))
                    {
                        OnPropertyChanged(nameof(HeaderText));
                    }
                }
            }
        }

        private ME2ME3TalkFile _currentMe2Me3Me2Me3TalkFile;
        private ExportLoaderHostedWindow _hostedWindow;
        private TLKEditorTab _activeTab;
        private Point _tabDragStartPoint;
        private TLKEditorTab _tabPendingDrag;
        private bool _restoredPersistedTabs;
        private bool _suppressTabPersistence;
        private bool _suppressInlineEditEvents;
        private bool _suppressSearchSelectionReset;
        private TlkSearchMatch? _currentSearchMatch;
        private string _lastSearchSignature;
        public List<TLKStringRef> LoadedStrings; //Loaded TLK
        public ObservableCollectionExtended<TLKStringRef> CleanedStrings { get; } = new(); // Displayed
        public ObservableCollectionExtended<TLKEditorTab> OpenTabs { get; } = new();
        private bool xmlUp;

        private const string OpenHighestMountedBaseTlksMenuHeader = "Open highest mounted base TLKs";
        private static readonly string[] BaseTlkFileNames =
        [
            "BIOGame_INT.tlk",
            "DLC_EXP_Pack001_INT.tlk",
            "DLC_EXP_Pack002_INT.tlk",
            "DLC_EXP_Pack003_INT.tlk",
            "DLC_EXP_Pack003_Base_INT.tlk",
            "DLC_CON_END_INT.tlk",
            "DLC_HEN_PR_INT.tlk",
            "DLC_Shared_INT.tlk"
        ];

        private readonly record struct TlkSearchMatch(int RowIndex, TLKStringRef Item, int ColumnIndex, int StartIndex, int Length);

        public TLKEditorTab ActiveTab
        {
            get => _activeTab;
            set
            {
                if (!ReferenceEquals(_activeTab, value))
                {
                    SwitchToTab(value);
                }
            }
        }

        public bool HasOpenTabs => OpenTabs.Count > 0;

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
                {
                    ResetSearchState();
                    UpdateSearchStatus();
                }
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
                {
                    ResetSearchState();
                    UpdateSearchStatus();
                }
            }
        }

        private bool _findWholeWord;
        public bool FindWholeWord
        {
            get => _findWholeWord;
            set
            {
                if (SetProperty(ref _findWholeWord, value))
                {
                    ResetSearchState();
                    UpdateSearchStatus();
                }
            }
        }

        private bool _findUseRegex;
        public bool FindUseRegex
        {
            get => _findUseRegex;
            set
            {
                if (SetProperty(ref _findUseRegex, value))
                {
                    ResetSearchState();
                    UpdateSearchStatus();
                }
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

        public bool StringSelected
        {
            get
            {
                return GetActiveString() is not null;
            }
        }

        public TLKEditorExportLoader() : base("TLKEditor")
        {
            DataContext = this;
            LoadCommands();
            InitializeComponent();
            Loaded += TLKEditorExportLoader_Loaded;
            OpenTabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOpenTabs));
        }

        public ICommand CommitCommand { get; set; }
        public ICommand SetIDCommand { get; set; }
        public ICommand ExportXmlCommand { get; set; }
        public ICommand ImportXmlCommand { get; set; }
        public ICommand ViewXmlCommand { get; set; }
        public ICommand DeleteStringCommand { get; set; }
        public ICommand SearchCommand { get; set; }
        public ICommand OpenTabCommand { get; set; }
        public ICommand AddStringCommand { get; set; }
        public ICommand AddStringRangeCommand { get; set; }
        public ICommand ShowFindReplaceCommand { get; set; }
        public ICommand FindNextCommand { get; set; }
        public ICommand FindPreviousCommand { get; set; }
        public ICommand ReplaceCommand { get; set; }
        public ICommand ReplaceAllCommand { get; set; }
        public ICommand CloseFindReplaceCommand { get; set; }

        private void LoadCommands()
        {
            CommitCommand = new RelayCommand(CommitTLK, CanCommitTLK);
            SetIDCommand = new RelayCommand(SetStringID, StringIsSelected);
            DeleteStringCommand = new RelayCommand(DeleteString, StringIsSelected);

            OpenTabCommand = new GenericCommand(OpenTab, CanLoadFile);
            SearchCommand = new GenericCommand(TextSearch, HasTLKLoaded);
            AddStringCommand = new GenericCommand(AddString, HasTLKLoaded);
            AddStringRangeCommand = new GenericCommand(AddStringRange, HasTLKLoaded);
            ShowFindReplaceCommand = new GenericCommand(ShowFindReplace, HasTLKLoaded);
            FindNextCommand = new GenericCommand(FindNext, HasTLKLoaded);
            FindPreviousCommand = new GenericCommand(FindPrevious, HasTLKLoaded);
            ReplaceCommand = new GenericCommand(ReplaceCurrent, HasTLKLoaded);
            ReplaceAllCommand = new GenericCommand(ReplaceAll, HasTLKLoaded);
            CloseFindReplaceCommand = new GenericCommand(CloseFindReplace);

            ExportXmlCommand = new GenericCommand(ExportToXml, HasTLKLoaded);
            ImportXmlCommand = new GenericCommand(ImportFromXml, HasTLKLoaded);
            ViewXmlCommand = new GenericCommand(ViewAsXml, HasTLKLoaded);
        }

        private void OpenTab()
        {
            OpenFile();
        }

        private void TLKEditorExportLoader_Loaded(object sender, RoutedEventArgs e)
        {
            RestorePersistedTabs();
        }

        private void RestorePersistedTabs()
        {
            if (_restoredPersistedTabs || !IsPoppedOut || CurrentLoadedExport != null || HasTLKLoaded() || OpenTabs.Count > 0)
            {
                return;
            }

            _restoredPersistedTabs = true;
            var restoredPaths = (Settings.TLKEditor_OpenTabs ?? new List<string>())
                                .Where(path => !string.IsNullOrWhiteSpace(path))
                                .Select(Path.GetFullPath)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .Where(File.Exists)
                                .ToList();
            if (restoredPaths.Count == 0)
            {
                PersistOpenTabs();
                return;
            }

            _suppressTabPersistence = true;
            try
            {
                foreach (string path in restoredPaths)
                {
                    try
                    {
                        OpenTabs.Add(CreateTab(path));
                    }
                    catch
                    {
                    }
                }

                if (OpenTabs.Count > 0)
                {
                    string selectedPath = Settings.TLKEditor_SelectedTab;
                    ActiveTab = OpenTabs.FirstOrDefault(tab => string.Equals(tab.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? OpenTabs[0];
                }
            }
            finally
            {
                _suppressTabPersistence = false;
            }

            PersistOpenTabs();
        }

        private static TLKStringRef GetContextMenuString(object sender)
        {
            return sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: TLKStringRef item } } }
                ? item
                : null;
        }

        private int GetNextAvailableStringId(int preferredId)
        {
            preferredId = Math.Max(1, preferredId);
            HashSet<int> existingIds = LoadedStrings.Where(x => x is not null)
                                                  .Select(x => x.StringID)
                                                  .Where(id => id > 0)
                                                  .ToHashSet();

            while (existingIds.Contains(preferredId))
            {
                preferredId++;
            }

            return preferredId;
        }

        private bool StringIdExistsInCurrentTlk(int stringId)
        {
            return stringId > 0 && LoadedStrings.Any(x => x is not null && x.StringID == stringId);
        }

        private int GetCleanedInsertIndex(int stringId)
        {
            for (int i = 0; i < CleanedStrings.Count; i++)
            {
                if (CleanedStrings[i].StringID > stringId)
                {
                    return i;
                }
            }

            return CleanedStrings.Count;
        }

        private int GetLoadedInsertIndex(int stringId)
        {
            for (int i = 0; i < LoadedStrings.Count; i++)
            {
                if (LoadedStrings[i].StringID > 0 && LoadedStrings[i].StringID > stringId)
                {
                    return i;
                }
            }

            for (int i = LoadedStrings.Count - 1; i >= 0; i--)
            {
                if (LoadedStrings[i].StringID > 0)
                {
                    return i + 1;
                }
            }

            return LoadedStrings.Count;
        }

        private void CopyLineMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (GetContextMenuString(sender) is TLKStringRef sourceString)
            {
                _copiedLine = new CopiedTLKLine(sourceString.StringID, sourceString.Data, sourceString.Flags);
            }
        }

        private void PasteLineMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_copiedLine is null || GetContextMenuString(sender) is not TLKStringRef targetString)
            {
                return;
            }

            int pastedId = !StringIdExistsInCurrentTlk(_copiedLine.StringID)
                ? _copiedLine.StringID
                : GetNextAvailableStringId(targetString.StringID > 0 ? targetString.StringID + 1 : 1);

            TLKStringRef pastedLine = new(pastedId, _copiedLine.Data, _copiedLine.Flags);
            LoadedStrings.Insert(GetLoadedInsertIndex(pastedId), pastedLine);
            CleanedStrings.Insert(GetCleanedInsertIndex(pastedId), pastedLine);
            FocusString(pastedLine, 1);
            SetFileModified(true);
        }

        private void MoveTab(TLKEditorTab sourceTab, TLKEditorTab targetTab)
        {
            if (sourceTab is null || targetTab is null || ReferenceEquals(sourceTab, targetTab))
            {
                return;
            }

            int sourceIndex = OpenTabs.IndexOf(sourceTab);
            int targetIndex = OpenTabs.IndexOf(targetTab);
            if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
            {
                return;
            }

            OpenTabs.Move(sourceIndex, targetIndex);
            ActiveTab = sourceTab;
            PersistOpenTabs();
        }

        private void TabControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _tabDragStartPoint = e.GetPosition(null);
            _tabPendingDrag = null;

            if (e.OriginalSource is DependencyObject sourceElement && FindAncestor<Button>(sourceElement) is null)
            {
                _tabPendingDrag = FindAncestor<TabItem>(sourceElement)?.DataContext as TLKEditorTab;
            }
        }

        private void TabControl_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _tabPendingDrag is null)
            {
                return;
            }

            Point currentPosition = e.GetPosition(null);
            if (Math.Abs(currentPosition.X - _tabDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(currentPosition.Y - _tabDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            TLKEditorTab draggedTab = _tabPendingDrag;
            _tabPendingDrag = null;
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(TLKEditorTab), draggedTab), DragDropEffects.Move);
        }

        private void TabControl_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(TLKEditorTab)) || e.OriginalSource is not DependencyObject sourceElement)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            TLKEditorTab targetTab = FindAncestor<TabItem>(sourceElement)?.DataContext as TLKEditorTab;
            e.Effects = targetTab is null ? DragDropEffects.None : DragDropEffects.Move;
            e.Handled = true;
        }

        private void TabControl_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(TLKEditorTab)) || e.OriginalSource is not DependencyObject sourceElement)
            {
                return;
            }

            TLKEditorTab sourceTab = e.Data.GetData(typeof(TLKEditorTab)) as TLKEditorTab;
            TLKEditorTab targetTab = FindAncestor<TabItem>(sourceElement)?.DataContext as TLKEditorTab;
            MoveTab(sourceTab, targetTab);
            e.Handled = true;
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current is not null)
            {
                if (current is T match)
                {
                    return match;
                }

                current = current switch
                {
                    Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(current),
                    _ => LogicalTreeHelper.GetParent(current)
                };
            }

            return null;
        }

        private TLKEditorTab CreateTab(string filepath)
        {
            filepath = Path.GetFullPath(filepath);
            return new TLKEditorTab(filepath, new ME2ME3TalkFile(filepath));
        }

        private TLKEditorTab FindTab(string filepath)
        {
            if (string.IsNullOrWhiteSpace(filepath))
            {
                return null;
            }

            filepath = Path.GetFullPath(filepath);
            return OpenTabs.FirstOrDefault(tab => string.Equals(tab.FilePath, filepath, StringComparison.OrdinalIgnoreCase));
        }

        private void SwitchToTab(TLKEditorTab tab)
        {
            if (_activeTab is not null)
            {
                _activeTab.TalkFile = _currentMe2Me3Me2Me3TalkFile;
                _activeTab.LoadedStrings = LoadedStrings;
                _activeTab.IsModified = FileModified;
            }

            _activeTab = tab;
            OnPropertyChanged(nameof(ActiveTab));

            if (tab is null)
            {
                SetCurrentLoadedFilePath(null);
                SetLoadedFilePath(null);
                CurrentLoadedExport = null;
                _currentMe2Me3Me2Me3TalkFile = null;
                LoadedStrings = null;
                _suppressInlineEditEvents = true;
                CleanedStrings.ClearEx();
                _suppressInlineEditEvents = false;
                SetFileModified(false, false);
                IsFindReplaceVisible = false;
            }
            else
            {
                CurrentLoadedExport = null;
                SetCurrentLoadedFilePath(tab.FilePath);
                SetLoadedFilePath(tab.FilePath);
                _currentMe2Me3Me2Me3TalkFile = tab.TalkFile;
                LoadedStrings = tab.LoadedStrings;
                RefreshVisibleStrings();
                SetFileModified(tab.IsModified, false);
            }

            OnPropertyChanged(nameof(StringSelected));
            ResetSearchState();
            UpdateSearchStatus();
            UpdateWindowTitle();
            PersistOpenTabs();
        }

        private void SetCurrentLoadedFilePath(string filepath)
        {
            if (string.Equals(CurrentLoadedFile, filepath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CurrentLoadedFile = filepath;
            OnPropertyChanged(nameof(CurrentLoadedFile));
        }

        private void SetLoadedFilePath(string filepath)
        {
            if (string.Equals(LoadedFile, filepath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            LoadedFile = filepath;
            OnPropertyChanged(nameof(LoadedFile));
        }

        private void RefreshVisibleStrings()
        {
            _suppressInlineEditEvents = true;
            CleanedStrings.ReplaceAll(LoadedStrings?.Where(x => x.StringID > 0).ToList() ?? new List<TLKStringRef>());
            _suppressInlineEditEvents = false;
        }

        private void SetFileModified(bool value, bool updateActiveTab = true)
        {
            FileModified = value;
            if (updateActiveTab && ActiveTab is not null)
            {
                ActiveTab.IsModified = value;
            }
        }

        private void PersistOpenTabs()
        {
            if (_suppressTabPersistence || !IsPoppedOut)
            {
                return;
            }

            Settings.TLKEditor_OpenTabs = OpenTabs.Where(tab => !string.IsNullOrWhiteSpace(tab.FilePath))
                                                  .Select(tab => tab.FilePath)
                                                  .Distinct(StringComparer.OrdinalIgnoreCase)
                                                  .ToList();
            Settings.TLKEditor_SelectedTab = ActiveTab?.FilePath ?? string.Empty;
        }

        private void UpdateWindowTitle()
        {
            var window = _hostedWindow ?? Window.GetWindow(this);
            if (window is null)
            {
                return;
            }

            window.Title = CurrentLoadedExport != null
                ? $"TLK Editor - {CurrentLoadedExport.UIndex} {CurrentLoadedExport.InstancedFullPath} - {CurrentLoadedExport.FileRef.FilePath}"
                : string.IsNullOrWhiteSpace(CurrentLoadedFile) ? "TLK Editor" : "TLK Editor - " + CurrentLoadedFile;
        }

        private TLKEditorTab GetAdjacentTab(TLKEditorTab tab)
        {
            int index = OpenTabs.IndexOf(tab);
            if (index < 0)
            {
                return null;
            }

            if (index + 1 < OpenTabs.Count)
            {
                return OpenTabs[index + 1];
            }

            return index > 0 ? OpenTabs[index - 1] : null;
        }

        private void CloseTab(TLKEditorTab tab)
        {
            if (tab is null)
            {
                return;
            }

            if (ReferenceEquals(ActiveTab, tab))
            {
                ActiveTab = GetAdjacentTab(tab);
            }

            OpenTabs.Remove(tab);
            if (OpenTabs.Count == 0)
            {
                ActiveTab = null;
            }

            PersistOpenTabs();
        }

        private void DeleteString(object obj)
        {
            var selectedItem = GetActiveString();
            if (selectedItem is null)
            {
                return;
            }

            CleanedStrings.Remove(selectedItem);
            LoadedStrings.Remove(selectedItem);
            SetFileModified(true);
        }

        private void SetStringID(object obj)
        {
            SetNewID();
        }

        public override void PopOut()
        {
            if (CurrentLoadedExport != null)
            {
                var elhw = new ExportLoaderHostedWindow(new TLKEditorExportLoader(), CurrentLoadedExport)
                {
                    Title = $"TLK Editor - {CurrentLoadedExport.UIndex} {CurrentLoadedExport.InstancedFullPath} - {CurrentLoadedExport.FileRef.FilePath}"
                };
                elhw.Show();
            }
        }

        private bool StringIsSelected(object obj)
        {
            return StringSelected;
        }

        private bool CanCommitTLK(object obj)
        {
            return FileModified;
        }

        private void CommitTLK(object obj)
        {
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            NormalizeLoadedStrings();
            var huff = new HuffmanCompression();
            huff.LoadInputData(LoadedStrings);
            huff.SerializeTalkfileToExport(CurrentLoadedExport);
            SetFileModified(false);
        }

        //SirC "efficiency is next to godliness" way of Checking export is ME1/TLK
        public override bool CanParse(ExportEntry exportEntry) => exportEntry.FileRef.Game.IsGame1() && exportEntry.ClassName == "BioTlkFile" && !exportEntry.IsDefaultObject;
        public override void PoppedOut(ExportLoaderHostedWindow elhw)
        {
            _hostedWindow = elhw;
            AddOpenHighestMountedBaseTlksMenuItem(elhw);
        }

        private void AddOpenHighestMountedBaseTlksMenuItem(ExportLoaderHostedWindow elhw)
        {
            MenuItem fileMenu = elhw.MainMenu.Items.OfType<MenuItem>()
                                       .FirstOrDefault(menuItem => string.Equals(menuItem.Header?.ToString()?.Replace("_", ""), "File", StringComparison.OrdinalIgnoreCase));
            if (fileMenu is null || fileMenu.Items.OfType<MenuItem>().Any(menuItem => string.Equals(menuItem.Header?.ToString(), OpenHighestMountedBaseTlksMenuHeader, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var menuItem = new MenuItem
            {
                Header = OpenHighestMountedBaseTlksMenuHeader,
                ToolTip = "Open the highest mounted LE3/ME3 version of each base TLK in tabs."
            };
            menuItem.Click += OpenHighestMountedBaseTlksMenuItem_Click;

            int openItemIndex = fileMenu.Items.OfType<MenuItem>()
                                        .Select((item, index) => new { item, index })
                                        .FirstOrDefault(x => string.Equals(x.item.Header?.ToString(), "Open", StringComparison.OrdinalIgnoreCase))?.index ?? -1;
            fileMenu.Items.Insert(openItemIndex >= 0 ? openItemIndex + 1 : fileMenu.Items.Count, menuItem);
        }

        private void OpenHighestMountedBaseTlksMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenHighestMountedBaseTlks();
        }

        private void OpenHighestMountedBaseTlks()
        {
            MEGame game = GetBaseTlkGame();
            if (game is not (MEGame.ME3 or MEGame.LE3))
            {
                MessageBox.Show("Could not find an installed ME3 or LE3 game path.", "Open Base TLKs");
                return;
            }

            CaseInsensitiveDictionary<string> loadedFiles;
            try
            {
                loadedFiles = MELoadedFiles.GetFilesLoadedInGame(game, true, additionalExtensions: [".tlk"]);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not enumerate mounted TLK files for {game}:\n{ex.Message}", "Open Base TLKs");
                return;
            }

            var missingFiles = new List<string>();
            var failedFiles = new List<string>();
            var filesToOpen = new List<string>();
            foreach (string fileName in BaseTlkFileNames)
            {
                if (loadedFiles.TryGetValue(fileName, out string filePath) && File.Exists(filePath))
                {
                    filesToOpen.Add(filePath);
                }
                else
                {
                    missingFiles.Add(fileName);
                }
            }

            if (filesToOpen.Count == 0)
            {
                MessageBox.Show($"Could not find any mounted base TLK files for {game}.", "Open Base TLKs");
                return;
            }

            foreach (string filePath in filesToOpen)
            {
                try
                {
                    LoadFile(filePath);
                }
                catch (Exception ex)
                {
                    failedFiles.Add($"{Path.GetFileName(filePath)}: {ex.Message}");
                }
            }

            if (missingFiles.Count > 0 || failedFiles.Count > 0)
            {
                var message = new StringBuilder();
                if (missingFiles.Count > 0)
                {
                    message.AppendLine("These base TLKs were not found:");
                    message.AppendLine(string.Join(Environment.NewLine, missingFiles));
                }

                if (failedFiles.Count > 0)
                {
                    if (message.Length > 0)
                    {
                        message.AppendLine();
                    }

                    message.AppendLine("These TLKs could not be opened:");
                    message.AppendLine(string.Join(Environment.NewLine, failedFiles));
                }

                MessageBox.Show(message.ToString(), "Open Base TLKs");
            }
        }

        private MEGame GetBaseTlkGame()
        {
            MEGame currentGame = CurrentLoadedExport?.FileRef.Game ?? GetCurrentLoadedFileGame();
            if (currentGame is MEGame.ME3 or MEGame.LE3)
            {
                return currentGame;
            }

            if (IsGameInstalled(MEGame.LE3)) return MEGame.LE3;
            if (IsGameInstalled(MEGame.ME3)) return MEGame.ME3;
            return MEGame.Unknown;
        }

        private static bool IsGameInstalled(MEGame game)
        {
            string bioGamePath = MEDirectories.GetBioGamePath(game);
            return !string.IsNullOrWhiteSpace(bioGamePath) && Directory.Exists(bioGamePath);
        }

        /// <summary>
        /// Memory cleanup when this control is unloaded
        /// </summary>
        public override void Dispose()
        {
            CurrentLoadedExport = null;
            _currentMe2Me3Me2Me3TalkFile = null;
            LoadedStrings?.Clear();
            CleanedStrings?.ClearEx();
            _hostedWindow = null;
        }

        public override void LoadExport(ExportEntry exportEntry)
        {
            SetCurrentLoadedFilePath(null);
            SetLoadedFilePath(null);
            var tlkFile = new ME1TalkFile(exportEntry); // Setup object as TalkFile
            LoadedStrings = tlkFile.StringRefs.ToList(); //This is not binded to so reassigning is fine
            RefreshVisibleStrings();
            CurrentLoadedExport = exportEntry;
            SetFileModified(false, false);
            UpdateWindowTitle();
        }

        public string CurrentLoadedFile { get; set; }

        public override void UnloadExport()
        {
            SetFileModified(false);
        }

        public bool HasTLKLoaded() => CurrentLoadedFile != null || CurrentLoadedExport != null;

        private void DisplayedString_ListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_suppressSearchSelectionReset)
            {
                ResetSearchState();
            }

            OnPropertyChanged(nameof(StringSelected)); //Propogate this change
        }

        public int DlgStringID(int curID) //Dialog tlkstring id
        {
            int newID;
            while (true)
            {
                var inst = new PromptDialog("Set new string ID", "TLK Editor", curID.ToString(), true)
                {
                    Owner = Window.GetWindow(this)
                };
                //center to parent
                if (inst.ShowDialog() == true)
                {
                    if (int.TryParse(inst.ResponseText, out int newIDInt) &&
                        newIDInt > 0) //test result is an acceptable input
                    { 
                        if (LoadedStrings.Any(x => x.StringID == newIDInt))
                        {
                            MessageBox.Show($"String ID must be unique.\n{newIDInt} is currently in use in this TLK.");
                            continue;
                        }

                        newID = newIDInt;
                        break;
                    }

                    MessageBox.Show("String ID must be a positive integer");
                }
                else
                {
                    return curID; //cancel
                }
            }
            return newID;
        }

        private void AddString()
        {
            var blankstringref = new TLKStringRef(100, "New Blank Line", 1);
            LoadedStrings.Add(blankstringref);
            CleanedStrings.Add(blankstringref);
            FocusString(blankstringref, 1);
            SetNewID();
            SetFileModified(true);
        }

        private void AddStringRange()
        {
            // Get set of existing string IDs for efficient duplicate checking
            var existingIDs = LoadedStrings.Select(x => x.StringID).ToHashSet();

            // Show dialog
            var dialog = new AddStringRangeDialog(existingIDs)
            {
                Owner = Window.GetWindow(this)
            };

            if (dialog.ShowDialog() == true)
            {
                int startID = dialog.StartStringID;
                int endID = dialog.EndStringID;

                // Add strings in the range
                var newStrings = new List<TLKStringRef>();
                for (int id = startID; id <= endID; id++)
                {
                    var blankstringref = new TLKStringRef(id, "", 1);
                    newStrings.Add(blankstringref);
                }

                // Add all strings at once for better performance
                LoadedStrings.AddRange(newStrings);
                CleanedStrings.AddRange(newStrings);

                // Select the first added string
                FocusString(newStrings[0], 1);

                SetFileModified(true);
            }
        }

        private void ExportToXml()
        {
            var fnameBase = CurrentLoadedExport?.ObjectName.Name;
            if (fnameBase == null && CurrentLoadedFile != null) fnameBase = Path.GetFileNameWithoutExtension(CurrentLoadedFile);
            if (fnameBase == null) fnameBase = "TalkFile";
            var saveFileDialog = new SaveFileDialog
            {
                Filter = "XML Files (*.xml)|*.xml",
                FileName = fnameBase + ".xml"
            };
            if (saveFileDialog.ShowDialog() == true)
            {
                if (CurrentLoadedExport != null)
                {
                    var talkfile = new ME1TalkFile(CurrentLoadedExport);
                    talkfile.SaveToXML(saveFileDialog.FileName);
                } 
                else if (_currentMe2Me3Me2Me3TalkFile is not null)
                {
                    if (FileModified)
                    {
                        _currentMe2Me3Me2Me3TalkFile.LoadTlkDataFromStream(ME2ME3HuffmanCompression.SaveToTlkStream(LoadedStrings).SeekBegin());
                    }
                    _currentMe2Me3Me2Me3TalkFile?.SaveToXML(saveFileDialog.FileName);
                }
            }
        }

        private void ImportFromXml()
        {
            var openFileDialog = new OpenFileDialog
            {
                Multiselect = false,
                Filter = "XML Files (*.xml)|*.xml",
                CustomPlaces = AppDirectories.GameCustomPlaces
            };
            if (openFileDialog.ShowDialog() == true)
            {
                if (CurrentLoadedExport is not null)
                {
                    var compressor = new HuffmanCompression();
                    compressor.LoadInputData(openFileDialog.FileName);
                    compressor.SerializeTalkfileToExport(CurrentLoadedExport);
                }
                else if (_currentMe2Me3Me2Me3TalkFile is not null)
                {
                    ME2ME3HuffmanCompression compressor = new ();
                    compressor.LoadInputData(openFileDialog.FileName);
                    _currentMe2Me3Me2Me3TalkFile.LoadTlkDataFromStream(compressor.SaveToStream().SeekBegin());
                    RefreshME2ME3TLK();
                }
                SetFileModified(true); //this is not always technically true, but we'll assume it is
            }
        }

        private void ViewAsXml()
        {
            if (!xmlUp)
            {
                string xmlString = "";
                if (CurrentLoadedExport is not null)
                {
                    xmlString = ME1TalkFile.TLKtoXmlstring(CurrentLoadedExport.InstancedFullPath, LoadedStrings);
                }
                else if (_currentMe2Me3Me2Me3TalkFile is not null)
                {
                    if (FileModified)
                    {
                        _currentMe2Me3Me2Me3TalkFile.LoadTlkDataFromStream(ME2ME3HuffmanCompression.SaveToTlkStream(LoadedStrings).SeekBegin());
                    }
                    xmlString = _currentMe2Me3Me2Me3TalkFile.WriteXMLString();
                }
                popoutXmlBox.Text = xmlString;

                popupDlg.Height = ActualHeight;
                popupDlg.Width = ActualWidth;
                btnViewXML.ToolTip = "Close XML View.";
                popupDlg.IsOpen = true;
                xmlUp = true;
            }
        }

        private async void Evt_CloseXML(object sender, EventArgs e)
        {
            await System.Threading.Tasks.Task.Delay(100);  //Catch double clicks of XML button 
            xmlUp = false;
            btnViewXML.ToolTip = "View as XML.";
        }

        private void SetNewID()
        {
            if (GetActiveString() is TLKStringRef selectedItem)
            {
                var stringRefNewID = DlgStringID(selectedItem.StringID); //Run popout box to set tlkstring id
                if (selectedItem.StringID != stringRefNewID)
                {
                    selectedItem.StringID = stringRefNewID;
                    SetFileModified(true);
                }
            }
        }

        private void Evt_KeyUp(object sender, KeyEventArgs k)
        {
            if (k.Key == Key.Return)
            {
                TextSearch();
            }
        }

        private void TextSearch()
        {
            string searchTerm = boxSearch.Text.Trim().ToLower();
            if (searchTerm == "") return; //don't search blank

            int pos = CleanedStrings.IndexOf(GetActiveString());
            pos += 1; //search this and 1 forward
            for (int i = 0; i < CleanedStrings.Count; i++)
            {
                int curIndex = (i + pos) % CleanedStrings.Count;
                TLKStringRef node = CleanedStrings[curIndex];

                if (node.StringID.ToString().Contains(searchTerm))
                {
                    //ID Search
                    FocusString(node, 0);
                    return;
                }
                else if (node.Data != null && node.Data.ToLower().Contains(searchTerm))
                {
                    FocusString(node, 1);
                    return;
                }
            }
            //Not found
            SystemSounds.Beep.Play();
        }

        public override void LoadFile(string filepath)
        {
            UnloadExport();
            CurrentLoadedExport = null;
            filepath = Path.GetFullPath(filepath);

            var existingTab = FindTab(filepath);
            if (existingTab is null)
            {
                existingTab = CreateTab(filepath);
                OpenTabs.Add(existingTab);
            }

            ActiveTab = existingTab;
            OnFileLoaded(EventArgs.Empty);
        }

        private void RefreshME2ME3TLK()
        {
            LoadedStrings = _currentMe2Me3Me2Me3TalkFile.StringRefs.ToList(); //This is not bound to so reassigning is fine
            if (ActiveTab is not null)
            {
                ActiveTab.TalkFile = _currentMe2Me3Me2Me3TalkFile;
                ActiveTab.LoadedStrings = LoadedStrings;
            }

            RefreshVisibleStrings();
        }

        public void LoadFileFromStream(Stream stream, string source)
        {
            UnloadExport();
            SetCurrentLoadedFilePath(null);
            SetLoadedFilePath(null);
            _currentMe2Me3Me2Me3TalkFile = new ME2ME3TalkFile(stream, source);

            // Need way to load a file without having it show up in the recents

            RefreshME2ME3TLK();
            SetFileModified(false, false);
            UpdateWindowTitle();
        }

        public override bool CanLoadFile()
        {
            //this doesn't do any background threading so we can always load files
            return true;
        }

        internal override void OpenFile()
        {
            var d = new OpenFileDialog
            {
                Title = "Open TLK file",
                Filter = "ME2/ME3/LE2/LE3 Talk Files|*.tlk",
                CustomPlaces = AppDirectories.GameCustomPlaces
            };
            if (d.ShowDialog() == true)
            {
#if !DEBUG
                try
                {
#endif
                LoadFile(d.FileName);
#if !DEBUG
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Unable to open file:\n" + ex.Message);
                }
#endif
            }
        }

        public override void Save()
        {
            if (CurrentLoadedExport != null)
            {
                if (FileModified)
                {
                    CommitTLK(null);
                }

                CurrentLoadedExport.FileRef.Save();
                RefreshLoadedTlksAfterSave(CurrentLoadedExport.FileRef.Game, CurrentLoadedExport.FileRef.FilePath, CurrentLoadedExport.UIndex);
            }
            else if (_currentMe2Me3Me2Me3TalkFile is not null)
            {
                if (CurrentLoadedFile is null)
                {
                    MessageBox.Show("Cannot save TLK File loaded from an SFAR. Use the Save As option to save your changes to a new file.");
                    return;
                }
                // CurrentME2ME3TalkFile.
                NormalizeLoadedStrings();
                ME2ME3HuffmanCompression.SaveToTlkFile(_currentMe2Me3Me2Me3TalkFile.FilePath, LoadedStrings);
                _currentMe2Me3Me2Me3TalkFile = new ME2ME3TalkFile(CurrentLoadedFile);
                RefreshME2ME3TLK();
                SetFileModified(false); //you can only commit to file, not to export and then file in file mode.
                RefreshLoadedTlksAfterSave(GetCurrentLoadedFileGame(), CurrentLoadedFile);
            }
            //throw new NotImplementedException();

        }

        private void RefreshLoadedTlksAfterSave(MEGame game, string tlkPath, int exportNumber = 0)
        {
            if (game == MEGame.Unknown)
            {
                return;
            }

            try
            {
                TLKManagerWPF.AutoFindAndReloadTlks(game, tlkPath, exportNumber);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"TLK was saved, but refreshing loaded TLKs failed:\n{ex.Message}", "TLK Refresh Error");
            }
        }

        private MEGame GetCurrentLoadedFileGame()
        {
            if (string.IsNullOrEmpty(CurrentLoadedFile))
            {
                return MEGame.Unknown;
            }

            if (ME2TalkFiles.LoadedTlks.Any(x => string.Equals(x.FilePath, CurrentLoadedFile, StringComparison.OrdinalIgnoreCase))) return MEGame.ME2;
            if (ME3TalkFiles.LoadedTlks.Any(x => string.Equals(x.FilePath, CurrentLoadedFile, StringComparison.OrdinalIgnoreCase))) return MEGame.ME3;
            if (LE2TalkFiles.LoadedTlks.Any(x => string.Equals(x.FilePath, CurrentLoadedFile, StringComparison.OrdinalIgnoreCase))) return MEGame.LE2;
            if (LE3TalkFiles.LoadedTlks.Any(x => string.Equals(x.FilePath, CurrentLoadedFile, StringComparison.OrdinalIgnoreCase))) return MEGame.LE3;

            string fullPath = Path.GetFullPath(CurrentLoadedFile);
            if (IsUnderDirectory(fullPath, ME2Directory.BioGamePath)) return MEGame.ME2;
            if (IsUnderDirectory(fullPath, ME3Directory.BioGamePath)) return MEGame.ME3;
            if (IsUnderDirectory(fullPath, LE2Directory.BioGamePath)) return MEGame.LE2;
            if (IsUnderDirectory(fullPath, LE3Directory.BioGamePath)) return MEGame.LE3;

            return MEGame.Unknown;
        }

        private static bool IsUnderDirectory(string path, string directory)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            string normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalizedPath.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(normalizedPath, normalizedDirectory, StringComparison.OrdinalIgnoreCase);
        }

        public override void SaveAs()
        {
            if (CurrentLoadedExport != null)
            {
                SaveFileDialog d = new() { Filter = $"*{Path.GetExtension(CurrentLoadedExport.FileRef.FilePath)}|*{Path.GetExtension(CurrentLoadedExport.FileRef.FilePath)}" };
                if (d.ShowDialog() == true)
                {
                    if (FileModified)
                    {
                        CommitTLK(null);
                    }

                    CurrentLoadedExport.FileRef.Save(d.FileName);
                }
            }
            else if (_currentMe2Me3Me2Me3TalkFile is not null)
            {
                SaveFileDialog d = new() { Filter = "ME2/ME3/LE2/LE3 talk files|*.tlk" };
                if (d.ShowDialog() == true)
                {
                    // CurrentME2ME3TalkFile.
                    NormalizeLoadedStrings();
                    ME2ME3HuffmanCompression.SaveToTlkFile(d.FileName, LoadedStrings);

                    if (ActiveTab is not null)
                    {
                        ActiveTab.FilePath = Path.GetFullPath(d.FileName);
                        SetCurrentLoadedFilePath(ActiveTab.FilePath);
                        SetLoadedFilePath(ActiveTab.FilePath);
                        _currentMe2Me3Me2Me3TalkFile = new ME2ME3TalkFile(ActiveTab.FilePath);
                        RefreshME2ME3TLK();
                        SetFileModified(false);
                        PersistOpenTabs();
                        UpdateWindowTitle();
                        OnFileLoaded(EventArgs.Empty);
                    }
                }
            }
        }

        public override bool CanSave() => CurrentLoadedExport is not null || _currentMe2Me3Me2Me3TalkFile is not null;

        //internal override void RecentFile_click(object sender, RoutedEventArgs e)
        //{
        //    string s = ((FrameworkElement)sender).Tag.ToString();
        //    if (File.Exists(s))
        //    {
        //        LoadFile(s);
        //    }
        //    else
        //    {
        //        MessageBox.Show("File does not exist: " + s);
        //    }
        //}

        public override string Toolname => "TLKEditor";

        internal override bool CanLoadFileExtension(string extension)
        {
            switch (extension)
            {
                case ".sfm":
                case ".u":
                case ".upk":
                case ".pcc":
                case ".tlk":
                    return true;
                default:
                    return false;
            }
        }

        private void InlineEditor_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: TLKStringRef item })
            {
                FocusString(item, 1);
            }
        }

        private void StringIdTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox textBox && textBox.DataContext is TLKStringRef item)
            {
                FocusString(item, 0);
                textBox.Tag = item.StringID;
                textBox.SelectAll();
            }
        }

        private TLKStringRef GetActiveString()
        {
            if (DisplayedString_ListBox?.CurrentCell.Item is TLKStringRef currentItem)
            {
                return currentItem;
            }

            return DisplayedString_ListBox?.SelectedItem as TLKStringRef;
        }

        private void FocusString(TLKStringRef item, int columnIndex)
        {
            if (DisplayedString_ListBox == null || item == null || DisplayedString_ListBox.Columns.Count == 0)
            {
                return;
            }

            columnIndex = Math.Clamp(columnIndex, 0, DisplayedString_ListBox.Columns.Count - 1);
            var cellInfo = new DataGridCellInfo(item, DisplayedString_ListBox.Columns[columnIndex]);
            DisplayedString_ListBox.CurrentCell = cellInfo;
            DisplayedString_ListBox.SelectedCells.Clear();
            DisplayedString_ListBox.SelectedCells.Add(cellInfo);
            DisplayedString_ListBox.ScrollIntoView(item, DisplayedString_ListBox.Columns[columnIndex]);
        }

        private void StringDataTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressInlineEditEvents)
            {
                return;
            }

            if (sender is TextBox textBox && textBox.IsKeyboardFocusWithin)
            {
                SetFileModified(true);
            }

            UpdateSearchStatus();
        }

        private void StringIdTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = e.Text.Any(c => !char.IsDigit(c));
        }

        private void StringIdTextBox_OnPasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.SourceDataObject.GetDataPresent(DataFormats.Text))
            {
                e.CancelCommand();
                return;
            }

            if (e.SourceDataObject.GetData(DataFormats.Text) is not string pastedText || pastedText.Any(c => !char.IsDigit(c)))
            {
                e.CancelCommand();
            }
        }

        private void StringIdTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox textBox)
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                CommitStringIdEdit(textBox);
                e.Handled = true;
                textBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            }
            else if (e.Key == Key.Escape)
            {
                ResetStringIdText(textBox);
                e.Handled = true;
            }
        }

        private void StringIdTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                CommitStringIdEdit(textBox);
            }
        }

        private void CommitStringIdEdit(TextBox textBox)
        {
            if (_suppressInlineEditEvents || textBox.DataContext is not TLKStringRef item)
            {
                return;
            }

            int originalId = textBox.Tag is int taggedId ? taggedId : item.StringID;
            string proposedText = textBox.Text.Trim();

            if (string.IsNullOrEmpty(proposedText) || !int.TryParse(proposedText, out int newId) || newId <= 0)
            {
                MessageBox.Show("String ID must be a positive integer");
                ResetStringIdText(textBox, originalId);
                return;
            }

            if (LoadedStrings.Any(x => !ReferenceEquals(x, item) && x.StringID == newId))
            {
                MessageBox.Show($"String ID must be unique.\n{newId} is currently in use in this TLK.");
                ResetStringIdText(textBox, originalId);
                return;
            }

            if (newId != originalId)
            {
                item.StringID = newId;
                SetFileModified(true);
            }

            textBox.Tag = item.StringID;
            textBox.Text = item.StringID.ToString();
        }

        private void ResetStringIdText(TextBox textBox, int? originalId = null)
        {
            int resetId = originalId ?? (textBox.Tag is int taggedId ? taggedId : 0);
            textBox.Text = resetId > 0 ? resetId.ToString() : string.Empty;
        }

        private void NormalizeLoadedStrings()
        {
            if (LoadedStrings == null)
            {
                return;
            }

            foreach (TLKStringRef stringRef in LoadedStrings)
            {
                if (stringRef.Data is not null)
                {
                    stringRef.Data = stringRef.Data.Replace("\r\n", "\n");
                }
            }
        }

        private void CloneLineMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (GetContextMenuString(sender) is not TLKStringRef sourceString)
            {
                return;
            }

            string response = PromptDialog.Prompt(
                this,
                "How many copies of this TLK line should be created?",
                "Clone TLK Line",
                "1",
                selectText: true,
                validator: text => int.TryParse(text, out int count) && count > 0
                    ? (true, null)
                    : (false, "Enter a positive integer."));

            if (response is null || !int.TryParse(response, out int cloneCount) || cloneCount <= 0)
            {
                return;
            }

            HashSet<int> clonedIds = Enumerable.Range(1, cloneCount)
                                               .Select(i => sourceString.StringID + i)
                                               .ToHashSet();

            int? conflictingId = LoadedStrings.Where(x => !ReferenceEquals(x, sourceString))
                                              .Select(x => (int?)x.StringID)
                                              .FirstOrDefault(id => id.HasValue && clonedIds.Contains(id.Value));
            if (conflictingId.HasValue)
            {
                MessageBox.Show($"Cannot clone line because TLK ID {conflictingId.Value} already exists.", "Clone TLK Line");
                return;
            }

            int loadedInsertIndex = LoadedStrings.IndexOf(sourceString);
            int cleanedInsertIndex = CleanedStrings.IndexOf(sourceString);
            if (loadedInsertIndex < 0 || cleanedInsertIndex < 0)
            {
                return;
            }

            var clones = Enumerable.Range(1, cloneCount)
                                   .Select(i => new TLKStringRef(sourceString.StringID + i, sourceString.Data, sourceString.Flags))
                                   .ToList();

            foreach (TLKStringRef clone in clones)
            {
                LoadedStrings.Insert(++loadedInsertIndex, clone);
                CleanedStrings.Insert(++cleanedInsertIndex, clone);
            }

            FocusString(clones[0], 1);
            SetFileModified(true);
        }

        private void CloseTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: TLKEditorTab tab })
            {
                CloseTab(tab);
                e.Handled = true;
            }
        }

        private void CloseViewAsXml(object sender, RoutedEventArgs e)
        {
            Evt_CloseXML(sender, e);
        }

        private void ShowFindReplace()
        {
            if (!HasTLKLoaded())
            {
                return;
            }

            IsFindReplaceVisible = true;
            if (Keyboard.FocusedElement is TextBox { DataContext: TLKStringRef, SelectedText.Length: > 0 } textBox)
            {
                FindText = textBox.SelectedText;
            }
            else
            {
                UpdateSearchStatus();
            }

            Dispatcher.InvokeAsync(() =>
            {
                FindTextBox.Focus();
                FindTextBox.SelectAll();
            });
        }

        private void CloseFindReplace()
        {
            IsFindReplaceVisible = false;
            DisplayedString_ListBox?.Focus();
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
            if (!TryCreateSearchRegex(out var regex))
            {
                return false;
            }

            if (CleanedStrings.Count == 0)
            {
                SearchStatusText = "No TLK strings loaded.";
                return false;
            }

            TlkSearchMatch? match = searchBackward ? FindPreviousMatch(regex) : FindNextMatch(regex);
            if (match is null)
            {
                SearchStatusText = "Text not found in current TLK.";
                return false;
            }

            ApplySearchMatch(match.Value);
            SearchStatusText = BuildMatchCountText(regex);
            return true;
        }

        private TlkSearchMatch? FindNextMatch(Regex regex)
        {
            var matches = GetAllSearchMatches(regex);
            if (matches.Count == 0)
            {
                return null;
            }

            if (HasReusableCurrentMatch())
            {
                int currentIndex = matches.FindIndex(m => MatchesSameLocation(m, _currentSearchMatch!.Value));
                if (currentIndex >= 0)
                {
                    if (currentIndex + 1 < matches.Count)
                    {
                        return matches[currentIndex + 1];
                    }

                    return FindWrapAround ? matches[0] : null;
                }
            }

            var startLocation = GetSearchStartLocation();
            foreach (var match in matches)
            {
                if (CompareMatchLocation(match, startLocation.RowIndex, startLocation.ColumnIndex, startLocation.StartIndex) >= 0)
                {
                    return match;
                }
            }

            return FindWrapAround ? matches[0] : null;
        }

        private TlkSearchMatch? FindPreviousMatch(Regex regex)
        {
            var matches = GetAllSearchMatches(regex);
            if (matches.Count == 0)
            {
                return null;
            }

            if (HasReusableCurrentMatch())
            {
                int currentIndex = matches.FindIndex(m => MatchesSameLocation(m, _currentSearchMatch!.Value));
                if (currentIndex >= 0)
                {
                    if (currentIndex > 0)
                    {
                        return matches[currentIndex - 1];
                    }

                    return FindWrapAround ? matches[^1] : null;
                }
            }

            var startLocation = GetSearchStartLocation();
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                if (CompareMatchLocation(matches[i], startLocation.RowIndex, startLocation.ColumnIndex, startLocation.StartIndex) <= 0)
                {
                    return matches[i];
                }
            }

            return FindWrapAround ? matches[^1] : null;
        }

        private void ReplaceCurrent()
        {
            if (!TryCreateSearchRegex(out var regex))
            {
                return;
            }

            if (_currentSearchMatch is null && !FindNextInternal(searchBackward: false))
            {
                return;
            }

            if (!HasReusableCurrentMatch())
            {
                if (!FindNextInternal(searchBackward: false) || !HasReusableCurrentMatch())
                {
                    return;
                }
            }

            var match = _currentSearchMatch!.Value;
            string currentText = GetMatchSourceText(match);
            Match regexMatch = regex.Match(currentText, match.StartIndex);
            if (!regexMatch.Success || regexMatch.Index != match.StartIndex || regexMatch.Length != match.Length)
            {
                ResetSearchState();
                if (!FindNextInternal(searchBackward: false) || !HasReusableCurrentMatch())
                {
                    return;
                }

                match = _currentSearchMatch!.Value;
                currentText = GetMatchSourceText(match);
                regexMatch = regex.Match(currentText, match.StartIndex);
                if (!regexMatch.Success || regexMatch.Index != match.StartIndex || regexMatch.Length != match.Length)
                {
                    return;
                }
            }

            if (!TryGetReplacementText(regexMatch, out var replacementText))
            {
                return;
            }

            if (!ApplyReplacement(match, currentText, replacementText))
            {
                return;
            }

            SetFileModified(true);
            _currentSearchMatch = new TlkSearchMatch(match.RowIndex, match.Item, match.ColumnIndex, match.StartIndex, replacementText.Length);
            FocusSearchMatch(_currentSearchMatch.Value);
            SearchStatusText = BuildMatchCountText(regex);
        }

        private void ReplaceAll()
        {
            if (!TryCreateSearchRegex(out var regex))
            {
                return;
            }

            int replacementCount = 0;
            foreach (TLKStringRef item in CleanedStrings)
            {
                string text = item.Data ?? string.Empty;
                if (text.Length == 0)
                {
                    continue;
                }

                var matches = regex.Matches(text);
                if (matches.Count == 0)
                {
                    continue;
                }

                string replacedText;
                try
                {
                    replacedText = regex.Replace(text, match =>
                    {
                        replacementCount++;
                        return match.Result(ReplaceText ?? string.Empty);
                    });
                }
                catch (ArgumentException ex)
                {
                    SearchStatusText = $"Invalid replacement pattern: {ex.Message}";
                    return;
                }

                item.Data = replacedText;
            }

            if (replacementCount == 0)
            {
                SearchStatusText = "No matches to replace.";
                return;
            }

            SetFileModified(true);
            ResetSearchState();
            SearchStatusText = $"Replaced {replacementCount} match{(replacementCount == 1 ? string.Empty : "es")}.";
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
            {
                return;
            }

            var returnFocusTextBox = sender as TextBox;
            int selectionStart = returnFocusTextBox?.SelectionStart ?? 0;
            int selectionLength = returnFocusTextBox?.SelectionLength ?? 0;

            if (sender == ReplaceTextBox)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    ReplaceAll();
                }
                else
                {
                    ReplaceCurrent();
                }
            }
            else
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    FindPrevious();
                }
                else
                {
                    FindNext();
                }
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

        private void ResetSearchState()
        {
            _currentSearchMatch = null;
            _lastSearchSignature = GetSearchSignature();
        }

        private bool HasReusableCurrentMatch()
        {
            return _currentSearchMatch is not null && string.Equals(_lastSearchSignature, GetSearchSignature(), StringComparison.Ordinal);
        }

        private string GetSearchSignature()
        {
            return $"{FindText}\n{FindMatchCase}\n{FindWholeWord}\n{FindUseRegex}";
        }

        private void UpdateSearchStatus()
        {
            if (!HasTLKLoaded())
            {
                SearchStatusText = "No TLK loaded.";
                return;
            }

            if (string.IsNullOrEmpty(FindText))
            {
                SearchStatusText = "Enter text to search.";
                return;
            }

            if (!TryCreateSearchRegex(out var regex, updateStatusOnError: true))
            {
                return;
            }

            SearchStatusText = BuildMatchCountText(regex);
        }

        private bool TryCreateSearchRegex(out Regex regex, bool updateStatusOnError = false)
        {
            regex = null;
            if (string.IsNullOrEmpty(FindText))
            {
                if (updateStatusOnError)
                {
                    SearchStatusText = "Enter text to search.";
                }

                return false;
            }

            try
            {
                string pattern = FindUseRegex ? FindText : Regex.Escape(FindText);
                if (FindWholeWord)
                {
                    pattern = $@"\b(?:{pattern})\b";
                }

                var options = RegexOptions.CultureInvariant | RegexOptions.Multiline;
                if (!FindMatchCase)
                {
                    options |= RegexOptions.IgnoreCase;
                }

                regex = new Regex(pattern, options);
                _lastSearchSignature = GetSearchSignature();
                return true;
            }
            catch (ArgumentException ex)
            {
                if (updateStatusOnError)
                {
                    SearchStatusText = $"Invalid regular expression: {ex.Message}";
                }

                return false;
            }
        }

        private string BuildMatchCountText(Regex regex)
        {
            int count = GetAllSearchMatches(regex).Count;

            return count == 0
                ? "No matches in current TLK."
                : $"{count} match{(count == 1 ? string.Empty : "es")} in current TLK.";
        }

        private void ApplySearchMatch(TlkSearchMatch match)
        {
            _currentSearchMatch = match;
            FocusSearchMatch(match);
        }

        private void FocusSearchMatch(TlkSearchMatch match)
        {
            FocusString(match.Item, match.ColumnIndex);
            if (TryGetMatchTextBox(match, out TextBox textBox))
            {
                textBox.Focus();
                textBox.Select(match.StartIndex, match.Length);
            }
        }

        private bool TryGetMatchTextBox(TlkSearchMatch match, out TextBox textBox)
        {
            return match.ColumnIndex == 0
                ? TryGetStringIdTextBox(match.Item, out textBox)
                : TryGetStringDataTextBox(match.Item, out textBox);
        }

        private bool TryGetStringIdTextBox(TLKStringRef item, out TextBox textBox)
        {
            return TryGetRowTextBox(item, 0, "IdColumnEditor", out textBox);
        }

        private bool TryGetStringDataTextBox(TLKStringRef item, out TextBox textBox)
        {
            return TryGetRowTextBox(item, 1, "TextColumnEditor", out textBox);
        }

        private bool TryGetRowTextBox(TLKStringRef item, int columnIndex, string tag, out TextBox textBox)
        {
            textBox = null;
            if (DisplayedString_ListBox is null || DisplayedString_ListBox.Columns.Count <= columnIndex)
            {
                return false;
            }

            DisplayedString_ListBox.ScrollIntoView(item, DisplayedString_ListBox.Columns[columnIndex]);
            DisplayedString_ListBox.UpdateLayout();

            if (DisplayedString_ListBox.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row)
            {
                return false;
            }

            textBox = FindVisualChild<TextBox>(row, candidate => candidate.DataContext == item && Equals(candidate.Tag, tag));
            return textBox is not null;
        }

        private List<TlkSearchMatch> GetAllSearchMatches(Regex regex)
        {
            var matches = new List<TlkSearchMatch>();
            for (int row = 0; row < CleanedStrings.Count; row++)
            {
                TLKStringRef item = CleanedStrings[row];
                AddMatches(matches, regex, item.StringID.ToString(), row, item, 0);
                AddMatches(matches, regex, item.Data ?? string.Empty, row, item, 1);
            }

            return matches;
        }

        private static void AddMatches(List<TlkSearchMatch> matches, Regex regex, string text, int rowIndex, TLKStringRef item, int columnIndex)
        {
            int startIndex = 0;
            while (startIndex <= text.Length)
            {
                Match match = FindNextMatchInText(regex, text, startIndex);
                if (match == null)
                {
                    break;
                }

                matches.Add(new TlkSearchMatch(rowIndex, item, columnIndex, match.Index, match.Length));
                startIndex = match.Index + Math.Max(match.Length, 1);
            }
        }

        private (int RowIndex, int ColumnIndex, int StartIndex) GetSearchStartLocation()
        {
            int rowIndex = GetActiveString() is TLKStringRef active ? Math.Max(0, CleanedStrings.IndexOf(active)) : 0;
            int columnIndex = Math.Clamp(DisplayedString_ListBox?.CurrentCell.Column?.DisplayIndex ?? 0, 0, 1);
            return (rowIndex, columnIndex, 0);
        }

        private static int CompareMatchLocation(TlkSearchMatch match, int rowIndex, int columnIndex, int startIndex)
        {
            int rowComparison = match.RowIndex.CompareTo(rowIndex);
            if (rowComparison != 0)
            {
                return rowComparison;
            }

            int columnComparison = match.ColumnIndex.CompareTo(columnIndex);
            if (columnComparison != 0)
            {
                return columnComparison;
            }

            return match.StartIndex.CompareTo(startIndex);
        }

        private static bool MatchesSameLocation(TlkSearchMatch left, TlkSearchMatch right)
        {
            return left.RowIndex == right.RowIndex
                && left.ColumnIndex == right.ColumnIndex
                && left.StartIndex == right.StartIndex
                && left.Length == right.Length;
        }

        private static string GetMatchSourceText(TlkSearchMatch match)
        {
            return match.ColumnIndex == 0 ? match.Item.StringID.ToString() : match.Item.Data ?? string.Empty;
        }

        private bool ApplyReplacement(TlkSearchMatch match, string currentText, string replacementText)
        {
            string updatedText = currentText.Remove(match.StartIndex, match.Length).Insert(match.StartIndex, replacementText);
            if (match.ColumnIndex == 0)
            {
                if (!int.TryParse(updatedText, out int newId) || newId <= 0)
                {
                    SearchStatusText = "Replacing this TLK ID would produce an invalid ID.";
                    return false;
                }

                if (LoadedStrings.Any(x => !ReferenceEquals(x, match.Item) && x.StringID == newId))
                {
                    SearchStatusText = $"Replacing this TLK ID would duplicate existing ID {newId}.";
                    return false;
                }

                match.Item.StringID = newId;
                return true;
            }

            match.Item.Data = updatedText;
            return true;
        }

        private static T FindVisualChild<T>(DependencyObject parent, Func<T, bool> predicate = null) where T : DependencyObject
        {
            if (parent is null)
            {
                return null;
            }

            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild && (predicate is null || predicate(typedChild)))
                {
                    return typedChild;
                }

                T result = FindVisualChild(child, predicate);
                if (result is not null)
                {
                    return result;
                }
            }

            return null;
        }

        private bool TryGetReplacementText(Match match, out string replacementText)
        {
            replacementText = ReplaceText ?? string.Empty;
            if (!FindUseRegex)
            {
                return true;
            }

            try
            {
                replacementText = match.Result(replacementText);
                return true;
            }
            catch (ArgumentException ex)
            {
                SearchStatusText = $"Invalid replacement pattern: {ex.Message}";
                return false;
            }
        }

        private static Match FindNextMatchInText(Regex regex, string text, int startIndex)
        {
            startIndex = Math.Clamp(startIndex, 0, text.Length);
            Match match = regex.Match(text, startIndex);
            while (match.Success && match.Length == 0)
            {
                if (match.Index >= text.Length)
                {
                    return null;
                }

                match = regex.Match(text, match.Index + 1);
            }

            return match.Success ? match : null;
        }

        private static Match FindPreviousMatchInText(Regex regex, string text, int startIndex)
        {
            startIndex = Math.Clamp(startIndex, 0, text.Length);
            Match previousMatch = null;
            int currentIndex = 0;

            while (currentIndex <= text.Length)
            {
                Match match = FindNextMatchInText(regex, text, currentIndex);
                if (match == null || match.Index >= startIndex)
                {
                    break;
                }

                previousMatch = match;
                currentIndex = match.Index + Math.Max(match.Length, 1);
            }

            return previousMatch;
        }
    }
}