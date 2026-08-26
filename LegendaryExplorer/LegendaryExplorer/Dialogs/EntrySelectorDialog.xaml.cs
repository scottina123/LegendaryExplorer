using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.Sequence_Editor;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Sound.Wwise;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Dialogs
{
    /// <summary>
    /// A dialog for selecting entries (imports and/or exports) from a package file.
    /// Provides filtering capabilities and support for optional package root selection.
    /// </summary>
    public partial class EntrySelector : NotifyPropertyChangedWindowBase, IDisposable
    {
        /// <summary>
        /// Flags indicating which types of entries can be selected in the dialog.
        /// </summary>
        [Flags]
        public enum SupportedTypes
        {
            /// <summary>Only export entries can be selected.</summary>
            Exports = 1,
            /// <summary>Only import entries can be selected.</summary>
            Imports = 2,
            /// <summary>Both export and import entries can be selected.</summary>
            ExportsAndImports = 3
        }

        /// <summary>
        /// The package file from which entries are loaded.
        /// </summary>
        private IMEPackage Pcc;

        /// <summary>
        /// Optional provider used by selectors whose entries are too expensive to load until the user searches.
        /// </summary>
        private Func<string, IEnumerable<object>> ItemSearch;

        /// <summary>
        /// Optional filter applied when the dialog opens. The user can disable it to see every valid entry.
        /// </summary>
        private Predicate<IEntry> InitialEntryFilter;

        private SequenceEditorWPF SequencePreviewEditor;
        private bool SequencePreviewSelectionPending;
        private bool SynchronizingSequencePreviewSelection;
        private TextureViewerExportLoader TexturePreviewViewer;
        private TextBlock TexturePreviewHeader;
        private PackageCache TexturePreviewPackageCache;
        private Task TexturePreviewResolutionTask = Task.CompletedTask;
        private int TexturePreviewRequestVersion;
        private Soundpanel AudioPreviewPlayer;
        private TextBlock AudioPreviewHeader;
        private TextBlock AudioPreviewMessage;
        private PackageCache AudioPreviewPackageCache;
        private Task AudioPreviewResolutionTask = Task.CompletedTask;
        private int AudioPreviewRequestVersion;
        private readonly Dictionary<(IMEPackage Package, int StringRef), string> WwiseTlkSubtitleCache = new();

        public Visibility ItemSearchVisibility => ItemSearch is null ? Visibility.Collapsed : Visibility.Visible;

        public Visibility ShowAllEntriesOptionVisibility => InitialEntryFilter is null ? Visibility.Collapsed : Visibility.Visible;

        public string ShowAllEntriesOptionLabel { get; private set; }

        private bool showAllEntries;
        public bool ShowAllEntries
        {
            get => showAllEntries;
            set
            {
                if (SetProperty(ref showAllEntries, value))
                {
                    UpdateFilteredEntries();
                }
            }
        }
        
        /// <summary>
        /// Gets the collection of all entries available for selection, filtered by the provided predicate.
        /// </summary>
        public ObservableCollectionExtended<object> AllEntriesList { get; } = new();

        public ObservableCollectionExtended<object> FilteredEntriesList { get; } = new();

        private string searchText;
        public string SearchText
        {
            get => searchText;
            set
            {
                if (SetProperty(ref searchText, value))
                {
                    if (ItemSearch is null)
                    {
                        UpdateFilteredEntries();
                    }
                }
            }
        }

        private string itemFilterText;
        public string ItemFilterText
        {
            get => itemFilterText;
            set
            {
                if (SetProperty(ref itemFilterText, value) && ItemSearch is not null)
                {
                    UpdateFilteredEntries();
                }
            }
        }

        public string ItemFilterHelpText => "Filter current results by string ID, TLK name, or text";

        private object selectedEntryItem;
        public object SelectedEntryItem
        {
            get => selectedEntryItem;
            set
            {
                if (!SetProperty(ref selectedEntryItem, value))
                {
                    return;
                }

                if (!SynchronizingSequencePreviewSelection && value is ExportEntry export)
                {
                    SequencePreviewEditor?.FocusReadOnlyPreviewObject(export);
                }

                UpdateTexturePreview(value as IEntry);
                UpdateAudioPreview(value as IEntry);
            }
        }

        /// <summary>
        /// Instantiates a EntrySelectorDialog WPF dialog
        /// </summary>
        /// <param name="owner">WPF owning window. Used for centering. Set to null if the calling window is not WPF based</param>
        /// <param name="pcc">Package file to load entries from</param>
        /// <param name="supportedInputTypes">Supported selection types</param>
        /// <param name="directionsText">Optional custom text to display as directions to the user</param>
        /// <param name="entryPredicate">A predicate to narrow the displayed entries</param>
        /// <param name="supportRootSelection">Whether to include a special option in the selection list</param>
        /// <param name="rootSelectionLabel">Label for the special option when <paramref name="supportRootSelection"/> is true</param>
        private EntrySelector(Window owner, IMEPackage pcc, SupportedTypes supportedInputTypes, string directionsText = null,
            Predicate<IEntry> entryPredicate = null, bool supportRootSelection = false,
            string rootSelectionLabel = "[Package root]", string searchHelpText = null,
            Predicate<IEntry> initialEntryFilter = null, string showAllEntriesOptionLabel = null,
            ExportEntry sequencePreview = null, bool texturePreview = false)
        {
            this.Pcc = pcc;
            this.SupportedInputTypes = supportedInputTypes;
            this.DirectionsTextOverride = directionsText;
            this.SearchHelpTextOverride = searchHelpText;
            InitialEntryFilter = initialEntryFilter;
            ShowAllEntriesOptionLabel = showAllEntriesOptionLabel ?? "Show full list";

            var allEntriesBuilding = new List<object>();
            if (SupportedInputTypes.HasFlag(SupportedTypes.Imports))
            {
                for (int i = Pcc.Imports.Count - 1; i >= 0; i--)
                {
                    if (entryPredicate?.Invoke(Pcc.Imports[i]) ?? true)
                    {
                        allEntriesBuilding.Add(Pcc.Imports[i]);
                    }
                }
            }
            if (SupportedInputTypes.HasFlag(SupportedTypes.Exports))
            {
                foreach (ExportEntry exp in Pcc.Exports)
                {
                    if (entryPredicate?.Invoke(exp) ?? true)
                    {
                        allEntriesBuilding.Add(exp);
                    }
                }
            }

            if (supportRootSelection)
            {
                allEntriesBuilding.Insert(0, rootSelectionLabel);
            }
            AllEntriesList.ReplaceAll(allEntriesBuilding);
            Owner = owner;
            DataContext = this;
            LoadCommands();
            InitializeComponent();
            InitializeSequencePreview(sequencePreview);
            InitializeTexturePreview(texturePreview);
            InitializeAudioPreview();
            UpdateFilteredEntries();
            EntrySearchTextBox.Focus();
        }

        private void ShowPreviewPane(GridLength previewWidth)
        {
            PreviewSplitterColumn.Width = new GridLength(5);
            PreviewColumn.Width = previewWidth;
            PreviewSplitter.Visibility = Visibility.Visible;
            PreviewHost.Visibility = Visibility.Visible;
            Width = Math.Min(1600, Math.Max(1000, SystemParameters.WorkArea.Width - 80));
            Height = Math.Min(800, Math.Max(600, SystemParameters.WorkArea.Height - 80));
            MinWidth = Math.Min(1000, Width);
            MinHeight = Math.Min(600, Height);
        }

        private void InitializeSequencePreview(ExportEntry sequencePreview)
        {
            if (sequencePreview is null)
            {
                return;
            }

            ShowPreviewPane(new GridLength(5, GridUnitType.Star));

            SequencePreviewEditor = new SequenceEditorWPF(enableRecents: false);
            PreviewHost.Content = SequencePreviewEditor.TakeReadOnlyPreviewContent(
                SynchronizeEntrySelectionFromSequencePreview,
                ApplySequencePreviewSelection);
            SequencePreviewEditor.LoadEmbeddedPackage(Pcc, sequencePreview);
        }

        private void InitializeTexturePreview(bool texturePreview)
        {
            if (!texturePreview)
            {
                return;
            }

            ShowPreviewPane(new GridLength(3, GridUnitType.Star));
            TexturePreviewPackageCache = new PackageCache();
            TexturePreviewViewer = new TextureViewerExportLoader
            {
                ViewerModeOnly = true
            };
            TexturePreviewHeader = new TextBlock
            {
                Text = "Texture preview",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = "The currently selected texture"
            };

            var previewPanel = new DockPanel();
            DockPanel.SetDock(TexturePreviewHeader, Dock.Top);
            previewPanel.Children.Add(TexturePreviewHeader);
            previewPanel.Children.Add(TexturePreviewViewer);
            PreviewHost.Content = previewPanel;
            SetTexturePreviewMessage("Select a texture to preview");
        }

        private void UpdateTexturePreview(IEntry entry)
        {
            if (TexturePreviewViewer is null)
            {
                return;
            }

            int requestVersion = Interlocked.Increment(ref TexturePreviewRequestVersion);
            TexturePreviewViewer.UnloadExport();
            TexturePreviewHeader.Text = entry is null
                ? "Texture preview"
                : $"Texture preview — {entry.InstancedFullPath}";
            TexturePreviewHeader.ToolTip = entry?.InstancedFullPath ?? "The currently selected texture";

            if (entry is null)
            {
                SetTexturePreviewMessage("No texture selected");
                return;
            }

            if (!entry.IsTexture() && entry.ClassName != "TextureCube")
            {
                SetTexturePreviewMessage("The selected entry is not a previewable texture");
                return;
            }

            if (entry is ExportEntry textureExport && textureExport.IsTexture())
            {
                LoadTexturePreview(textureExport);
                return;
            }

            SetTexturePreviewMessage("Loading texture preview…");
            PackageCache packageCache = TexturePreviewPackageCache;
            Task<(ExportEntry texture, string error)> resolutionTask = TexturePreviewResolutionTask.ContinueWith(
                _ => ResolveTexturePreview(entry, packageCache, requestVersion),
                TaskScheduler.Default);
            TexturePreviewResolutionTask = resolutionTask;
            resolutionTask.ContinueWithOnUIThread(task =>
            {
                if (disposedValue || requestVersion != TexturePreviewRequestVersion)
                {
                    return;
                }

                if (task.IsFaulted)
                {
                    SetTexturePreviewMessage($"Could not load texture preview: {task.Exception?.GetBaseException().Message}");
                }
                else if (task.Result.texture is null)
                {
                    SetTexturePreviewMessage(task.Result.error ?? "Could not resolve the selected texture");
                }
                else
                {
                    LoadTexturePreview(task.Result.texture);
                }
            });
        }

        private (ExportEntry texture, string error) ResolveTexturePreview(
            IEntry entry, PackageCache packageCache, int requestVersion)
        {
            try
            {
                if (requestVersion != Volatile.Read(ref TexturePreviewRequestVersion))
                {
                    return (null, null);
                }

                ExportEntry textureExport = entry switch
                {
                    ExportEntry export => export,
                    ImportEntry import when EntryImporter.TryResolveImport(import, out ExportEntry resolved, cache: packageCache) => resolved,
                    _ => null
                };
                if (textureExport is null)
                {
                    return (null, "Could not resolve the selected texture import");
                }

                if (textureExport.ClassName == "TextureCube")
                {
                    ExportEntry cubeFace = textureExport.GetProperty<ObjectProperty>("FacePosX")?
                        .ResolveToExport(textureExport.FileRef, packageCache);
                    return cubeFace?.IsTexture() == true
                        ? (cubeFace, null)
                        : (null, "Could not resolve the TextureCube's positive-X face");
                }

                return textureExport.IsTexture()
                    ? (textureExport, null)
                    : (null, "The resolved entry is not a previewable texture");
            }
            catch (Exception exception)
            {
                return (null, $"Could not load texture preview: {exception.Message}");
            }
        }

        private void LoadTexturePreview(ExportEntry textureExport)
        {
            SetTexturePreviewMessage("Loading texture preview…");
            TexturePreviewViewer.LoadExport(textureExport);
        }

        private void SetTexturePreviewMessage(string message)
        {
            TexturePreviewViewer.CannotShowTextureText = message;
            TexturePreviewViewer.CannotShowTextureTextVisibility = Visibility.Visible;
        }

        private void InitializeAudioPreview()
        {
            if (AudioPreviewPlayer is not null
                || SequencePreviewEditor is not null
                || TexturePreviewViewer is not null
                || !AllEntriesList.OfType<IEntry>().Any(IsWwiseAudioEntry))
            {
                return;
            }

            ShowPreviewPane(new GridLength(2, GridUnitType.Star));
            AudioPreviewPackageCache = new PackageCache();
            AudioPreviewPlayer = new Soundpanel
            {
                MiniPlayerMode = true,
                PlayBackOnlyMode = true,
                GenerateWaveformGraph = true,
                VerticalAlignment = VerticalAlignment.Top
            };
            AudioPreviewHeader = new TextBlock
            {
                Text = "Audio preview",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = "The currently selected Wwise audio entry"
            };
            AudioPreviewMessage = new TextBlock
            {
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            };

            var previewPanel = new DockPanel();
            DockPanel.SetDock(AudioPreviewHeader, Dock.Top);
            DockPanel.SetDock(AudioPreviewMessage, Dock.Top);
            previewPanel.Children.Add(AudioPreviewHeader);
            previewPanel.Children.Add(AudioPreviewMessage);
            previewPanel.Children.Add(AudioPreviewPlayer);
            PreviewHost.Content = previewPanel;
            SetAudioPreviewMessage("Select a WwiseEvent or WwiseStream to preview its audio");
        }

        private static bool IsWwiseAudioEntry(IEntry entry)
            => entry?.ClassName is "WwiseEvent" or "WwiseStream";

        private void UpdateAudioPreview(IEntry entry)
        {
            if (AudioPreviewPlayer is null)
            {
                return;
            }

            int requestVersion = Interlocked.Increment(ref AudioPreviewRequestVersion);
            AudioPreviewPlayer.StopPlaying();
            AudioPreviewPlayer.UnloadExport();
            AudioPreviewHeader.Text = entry is null
                ? "Audio preview"
                : $"Audio preview — {entry.InstancedFullPath}";
            AudioPreviewHeader.ToolTip = entry?.InstancedFullPath ?? "The currently selected Wwise audio entry";

            if (entry is null)
            {
                SetAudioPreviewMessage("No entry selected");
                return;
            }

            if (!IsWwiseAudioEntry(entry))
            {
                SetAudioPreviewMessage("The selected entry is not a WwiseEvent or WwiseStream");
                return;
            }

            if (entry is ExportEntry audioExport)
            {
                (ExportEntry resolvedExport, string error) = ResolveAudioPreview(audioExport, null, requestVersion);
                if (resolvedExport is null)
                {
                    SetAudioPreviewMessage(error);
                }
                else
                {
                    LoadAudioPreview(resolvedExport);
                }

                return;
            }

            SetAudioPreviewMessage("Resolving audio import…");
            PackageCache packageCache = AudioPreviewPackageCache;
            Task<(ExportEntry audioExport, string error)> resolutionTask = AudioPreviewResolutionTask.ContinueWith(
                _ => ResolveAudioPreview(entry, packageCache, requestVersion),
                TaskScheduler.Default);
            AudioPreviewResolutionTask = resolutionTask;
            resolutionTask.ContinueWithOnUIThread(task =>
            {
                if (disposedValue || requestVersion != AudioPreviewRequestVersion)
                {
                    return;
                }

                if (task.IsFaulted)
                {
                    SetAudioPreviewMessage($"Could not load audio preview: {task.Exception?.GetBaseException().Message}");
                }
                else if (task.Result.audioExport is null)
                {
                    SetAudioPreviewMessage(task.Result.error ?? "Could not resolve the selected audio import");
                }
                else
                {
                    LoadAudioPreview(task.Result.audioExport);
                }
            });
        }

        private (ExportEntry audioExport, string error) ResolveAudioPreview(
            IEntry entry, PackageCache packageCache, int requestVersion)
        {
            try
            {
                if (requestVersion != Volatile.Read(ref AudioPreviewRequestVersion))
                {
                    return (null, null);
                }

                ExportEntry requestedExport = entry switch
                {
                    ExportEntry export => export,
                    ImportEntry import when packageCache is not null
                                            && EntryImporter.TryResolveImport(import, out ExportEntry resolved,
                                                cache: packageCache) => resolved,
                    _ => null
                };
                if (requestedExport is null)
                {
                    return (null, "Could not resolve the selected audio import");
                }

                if (!IsWwiseAudioEntry(requestedExport))
                {
                    return (null, "The resolved entry is not a WwiseEvent or WwiseStream");
                }

                if (Soundpanel.ResolveAudioExport(requestedExport) is null)
                {
                    return requestedExport.ClassName == "WwiseEvent"
                        ? (null, "Could not resolve a unique WwiseStream referenced by this WwiseEvent")
                        : (null, "The selected WwiseStream cannot be previewed");
                }

                return (requestedExport, null);
            }
            catch (Exception exception)
            {
                return (null, $"Could not load audio preview: {exception.Message}");
            }
        }

        private void LoadAudioPreview(ExportEntry audioExport)
        {
            SetAudioPreviewMessage("Loading audio preview…");
            AudioPreviewPlayer.LoadExport(audioExport);
            SetAudioPreviewMessage(AudioPreviewPlayer.CurrentLoadedExport is null
                ? "Could not load audio for the selected entry"
                : null);
        }

        private void SetAudioPreviewMessage(string message)
        {
            AudioPreviewMessage.Text = message;
            AudioPreviewMessage.Visibility = string.IsNullOrWhiteSpace(message)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void SynchronizeEntrySelectionFromSequencePreview(ExportEntry entry)
        {
            if (entry is null
                || AllEntriesList.OfType<IEntry>().FirstOrDefault(candidate => candidate.UIndex == entry.UIndex) is not { } selectedEntry)
            {
                return;
            }

            SynchronizingSequencePreviewSelection = true;
            try
            {
                if (!FilteredEntriesList.Contains(selectedEntry))
                {
                    SearchText = string.Empty;
                    ItemFilterText = string.Empty;
                }

                if (!FilteredEntriesList.Contains(selectedEntry))
                {
                    return;
                }

                SelectedEntryItem = selectedEntry;
                EntrySelectorListView.ScrollIntoView(selectedEntry);
            }
            finally
            {
                SynchronizingSequencePreviewSelection = false;
            }
        }

        private void ApplySequencePreviewSelection(ExportEntry entry)
        {
            if (entry is null
                || AllEntriesList.OfType<IEntry>().FirstOrDefault(candidate => candidate.UIndex == entry.UIndex) is not { } selectedEntry)
            {
                return;
            }

            SynchronizeEntrySelectionFromSequencePreview(entry);
            if (!SequencePreviewSelectionPending)
            {
                SequencePreviewSelectionPending = true;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(AcceptSelection));
            }
        }

        private EntrySelector(Window owner, Func<string, IEnumerable<object>> itemSearch, string directionsText = null,
            string searchHelpText = null, string windowTitle = null)
        {
            Pcc = null;
            ItemSearch = itemSearch ?? throw new ArgumentNullException(nameof(itemSearch));
            SupportedInputTypes = SupportedTypes.ExportsAndImports;
            DirectionsTextOverride = directionsText;
            SearchHelpTextOverride = searchHelpText;
            Owner = owner;
            DataContext = this;
            LoadCommands();
            InitializeComponent();
            InitializeAudioPreview();
            if (!string.IsNullOrWhiteSpace(windowTitle))
            {
                Title = windowTitle;
            }
            EntrySearchTextBox.Focus();
        }

        private EntrySelector(Window owner, IEnumerable<object> items, string directionsText = null, string searchHelpText = null)
        {
            Pcc = null;
            SupportedInputTypes = SupportedTypes.ExportsAndImports;
            DirectionsTextOverride = directionsText;
            SearchHelpTextOverride = searchHelpText;
            AllEntriesList.ReplaceAll(items ?? []);
            Owner = owner;
            DataContext = this;
            LoadCommands();
            InitializeComponent();
            InitializeAudioPreview();
            UpdateFilteredEntries();
            EntrySearchTextBox.Focus();
        }

        /// <summary>
        /// Displays an entry selection dialog that includes a "[Package root]" option in addition to entries.
        /// </summary>
        /// <typeparam name="T">The type of entry to select (ExportEntry, ImportEntry, or IEntry)</typeparam>
        /// <param name="owner">WPF owning window for centering. Set to null if the calling window is not WPF based</param>
        /// <param name="pcc">Package file to load entries from</param>
        /// <param name="directionsText">Optional custom text to display as directions to the user</param>
        /// <param name="predicate">Optional predicate to filter the displayed entries</param>
        /// <returns>A tuple indicating whether the package root was selected and the selected entry (null if root was selected or dialog was cancelled)</returns>
        public static (bool selectedPackageRoot, T selectedEntry) GetEntryWithNoOption<T>(Window owner,
            IMEPackage pcc, string directionsText = null, Predicate<T> predicate = null, object defaultItem = null,
            bool selectLastItemByDefault = false, string noOptionLabel = "[Package root]",
            Predicate<T> initialFilterPredicate = null, string showAllEntriesOptionLabel = null,
            ExportEntry sequencePreview = null, bool texturePreview = false) where T : class, IEntry
        {
            SupportedTypes supportedInputTypes = SupportedTypes.ExportsAndImports;
            if (typeof(T) == typeof(ExportEntry))
            {
                supportedInputTypes = SupportedTypes.Exports;
            }
            else if (typeof(T) == typeof(ImportEntry))
            {
                supportedInputTypes = SupportedTypes.Imports;
            }

            Predicate<IEntry> entryPredicate = null;
            if (predicate != null)
            {
                entryPredicate = entry => predicate((T)entry);
            }
            Predicate<IEntry> initialEntryFilter = initialFilterPredicate is null
                ? null
                : entry => initialFilterPredicate((T)entry);
            using var dlg = new EntrySelector(owner, pcc, supportedInputTypes, directionsText, entryPredicate, true,
                noOptionLabel, initialEntryFilter: initialEntryFilter,
                showAllEntriesOptionLabel: showAllEntriesOptionLabel, sequencePreview: sequencePreview,
                texturePreview: texturePreview);
            dlg.SetInitialSelection(defaultItem, selectLastItemByDefault);
            if (dlg.ShowDialog() == true)
            {
                return (dlg.ChoseRoot, dlg.ChosenEntry as T);
            }

            return (false,null); //No option was picked.
        }

        /// <summary>
        /// Displays an entry selection dialog and returns the selected entry.
        /// </summary>
        /// <typeparam name="T">The type of entry to select (ExportEntry, ImportEntry, or IEntry)</typeparam>
        /// <param name="owner">WPF owning window for centering. Set to null if the calling window is not WPF based</param>
        /// <param name="pcc">Package file to load entries from</param>
        /// <param name="directionsText">Optional custom text to display as directions to the user</param>
        /// <param name="predicate">Optional predicate to filter the displayed entries</param>
        /// <param name="defaultItem">Optional entry to pre-select in the dialog</param>
        /// <returns>The selected entry, or null if the dialog was cancelled</returns>
        public static T GetEntry<T>(Window owner, IMEPackage pcc, string directionsText = null,
            Predicate<T> predicate = null, IEntry defaultItem = null, bool selectLastItemByDefault = false,
            Predicate<T> initialFilterPredicate = null, string showAllEntriesOptionLabel = null,
            ExportEntry sequencePreview = null, bool texturePreview = false) where T : class, IEntry
        {
            SupportedTypes supportedInputTypes = SupportedTypes.ExportsAndImports;
            if (typeof(T) == typeof(ExportEntry))
            {
                supportedInputTypes = SupportedTypes.Exports;
            }
            else if (typeof(T) == typeof(ImportEntry))
            {
                supportedInputTypes = SupportedTypes.Imports;
            }

            Predicate<IEntry> entryPredicate = null;
            if (predicate != null)
            {
                entryPredicate = entry => predicate((T)entry);
            }
            Predicate<IEntry> initialEntryFilter = initialFilterPredicate is null
                ? null
                : entry => initialFilterPredicate((T)entry);
            using var dlg = new EntrySelector(owner, pcc, supportedInputTypes, directionsText, entryPredicate,
                initialEntryFilter: initialEntryFilter, showAllEntriesOptionLabel: showAllEntriesOptionLabel,
                sequencePreview: sequencePreview, texturePreview: texturePreview);
            dlg.SetInitialSelection(defaultItem, selectLastItemByDefault);
            if (dlg.ShowDialog() == true)
            {
                return dlg.ChosenEntry as T;
            }

            return null;
        }

        public static T GetItem<T>(Window owner, IEnumerable<T> items, string directionsText = null, T defaultItem = default, bool selectLastItemByDefault = false, string searchHelpText = null) where T : class
        {
            using var dlg = new EntrySelector(owner, items?.Cast<object>() ?? [], directionsText, searchHelpText);
            dlg.SetInitialSelection(defaultItem, selectLastItemByDefault);
            if (dlg.ShowDialog() == true)
            {
                return dlg.SelectedEntryItem as T;
            }

            return null;
        }

        /// <summary>
        /// Displays an item selector whose list is populated by the supplied search provider.
        /// </summary>
        public static T SearchForItem<T>(Window owner, Func<string, IEnumerable<T>> itemSearch,
            string directionsText = null, string searchHelpText = null, string windowTitle = null) where T : class
        {
            ArgumentNullException.ThrowIfNull(itemSearch);
            using var dlg = new EntrySelector(owner,
                search => itemSearch(search)?.Cast<object>() ?? [], directionsText, searchHelpText, windowTitle);
            if (dlg.ShowDialog() == true)
            {
                return dlg.SelectedEntryItem as T;
            }

            return null;
        }

        /// <summary>
        /// Gets the command for accepting the current selection.
        /// </summary>
        public ICommand OKCommand { get; set; }
        
        private void LoadCommands()
        {
            OKCommand = new GenericCommand(AcceptSelection, CanAcceptSelection);
        }

        /// <summary>
        /// Determines whether the current selection can be accepted.
        /// </summary>
        /// <returns>True if an item is selected; otherwise, false</returns>
        private bool CanAcceptSelection()
        {
            return SelectedEntryItem != null;
        }

        /// <summary>
        /// Accepts the current selection and closes the dialog with a positive result.
        /// </summary>
        private void AcceptSelection()
        {
            DialogResult = true;
            ChosenEntry = SelectedEntryItem as IEntry;
            ChoseRoot = SelectedEntryItem is string;
            Dispose();
        }

        private void SetInitialSelection(object defaultItem, bool selectLastItemByDefault = false)
        {
            SelectedEntryItem = defaultItem is not null && FilteredEntriesList.Contains(defaultItem) ? defaultItem : null;
            if (SelectedEntryItem is null && FilteredEntriesList.Count > 0)
            {
                SelectedEntryItem = selectLastItemByDefault ? FilteredEntriesList[^1] : FilteredEntriesList[0];
            }

            if (SelectedEntryItem is not null)
            {
                EntrySelectorListView.ScrollIntoView(SelectedEntryItem);
            }
        }

        private void UpdateFilteredEntries()
        {
            IEnumerable<object> filteredEntries = AllEntriesList;
            if (!ShowAllEntries && InitialEntryFilter is not null)
            {
                filteredEntries = filteredEntries.Where(entry => entry is not IEntry packageEntry
                                                                  || InitialEntryFilter(packageEntry));
            }

            string search = (ItemSearch is null ? SearchText : ItemFilterText)?.Trim();
            if (!string.IsNullOrWhiteSpace(search))
            {
                filteredEntries = filteredEntries.Where(entry => EntryMatchesSearch(entry, search));
            }

            FilteredEntriesList.ReplaceAll(filteredEntries);

            if (SelectedEntryItem is not null && FilteredEntriesList.Contains(SelectedEntryItem))
            {
                return;
            }

            SelectedEntryItem = FilteredEntriesList.FirstOrDefault();
        }

        private void RunItemSearch()
        {
            if (ItemSearch is null)
            {
                return;
            }

            string search = SearchText?.Trim();
            if (string.IsNullOrWhiteSpace(search))
            {
                AllEntriesList.Clear();
                FilteredEntriesList.Clear();
                SelectedEntryItem = null;
                return;
            }

            Cursor previousCursor = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                List<object> results = ItemSearch(search).ToList();
                AllEntriesList.ReplaceAll(results);
                InitializeAudioPreview();
                UpdateFilteredEntries();
                if (SelectedEntryItem is not null)
                {
                    EntrySelectorListView.ScrollIntoView(SelectedEntryItem);
                    EntryFilterTextBox.Focus();
                    EntryFilterTextBox.SelectAll();
                }
            }
            finally
            {
                Mouse.OverrideCursor = previousCursor;
            }
        }

        private bool EntryMatchesSearch(object item, string search)
        {
            if (item is string rootLabel)
            {
                return rootLabel.Contains(search, StringComparison.OrdinalIgnoreCase);
            }

            if (item is not IEntry entry)
            {
                return item?.ToString()?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
            }

            string normalizedSearch = search.TrimStart('#');
            if (int.TryParse(normalizedSearch, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedUIndex)
                && entry.UIndex == parsedUIndex)
            {
                return true;
            }

            string uIndexText = entry.UIndex.ToString(CultureInfo.InvariantCulture);
            return uIndexText.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
                   || entry.ObjectName.Instanced.Contains(search, StringComparison.OrdinalIgnoreCase)
                   || entry.ClassName.Contains(search, StringComparison.OrdinalIgnoreCase)
                   || entry.InstancedFullPath.Contains(search, StringComparison.OrdinalIgnoreCase)
                   || TryGetWwiseTlkMetadata(entry, out int tlkId, out string subtitle)
                   && WwiseHelper.MatchesWwiseStreamTlkFilter(tlkId, subtitle, normalizedSearch);
        }

        private bool TryGetWwiseTlkMetadata(IEntry entry, out int tlkId, out string subtitle)
        {
            tlkId = 0;
            subtitle = null;
            if (entry?.FileRef is not { } package)
            {
                return false;
            }

            int? parsedTlkId = entry.ClassName switch
            {
                "WwiseEvent" => WwiseHelper.GetTlkIdFromWwiseEventName(entry.ObjectName.Name),
                "WwiseStream" => WwiseHelper.GetTlkIdFromWwiseStreamName(entry.ObjectName.Name),
                _ => null
            };
            if (parsedTlkId is not > 0)
            {
                return false;
            }

            tlkId = parsedTlkId.Value;
            var cacheKey = (package, tlkId);
            if (!WwiseTlkSubtitleCache.TryGetValue(cacheKey, out subtitle))
            {
                string resolvedText = TLKManagerWPF.GlobalFindStrRefbyID(tlkId, package);
                subtitle = string.IsNullOrWhiteSpace(resolvedText) || resolvedText == "No Data"
                    ? null
                    : resolvedText.ReplaceLineEndings(" ").Trim();
                WwiseTlkSubtitleCache[cacheKey] = subtitle;
            }

            return true;
        }

        internal string GetWwiseTlkDisplayText(object item)
        {
            if (item is not IEntry entry
                || !TryGetWwiseTlkMetadata(entry, out int tlkId, out string subtitle))
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(subtitle)
                ? $"TLK {tlkId}"
                : $"TLK {tlkId}: {subtitle}";
        }

        /// <summary>
        /// The entry that was selected by the user.
        /// </summary>
        private IEntry ChosenEntry;
        
        /// <summary>
        /// Indicates whether the user selected the "[Package root]" option.
        /// </summary>
        private bool ChoseRoot;

        /// <summary>
        /// The types of entries that can be selected in this dialog instance.
        /// </summary>
        private readonly SupportedTypes SupportedInputTypes;

        /// <summary>
        /// Custom directions text to display to the user, overriding the default.
        /// </summary>
        private string DirectionsTextOverride;
        private string SearchHelpTextOverride;
        
        /// <summary>
        /// Gets the directions text to display to the user based on the supported input types.
        /// </summary>
        public string DirectionsText
        {
            get
            {
                if (DirectionsTextOverride != null) return DirectionsTextOverride;
                switch (SupportedInputTypes)
                {
                    case SupportedTypes.Exports:
                        return "Select an export";
                    case SupportedTypes.Imports:
                        return "Select an import";
                    case SupportedTypes.ExportsAndImports:
                        return "Select an import or export";
                }
                return "Unknown input type selected";
            }
        }

        public string SearchHelpText => SearchHelpTextOverride ?? "Search by export/import number, object name, class, full path, TLK ID, or subtitle text";

        #region IDisposable Support
        /// <summary>
        /// Tracks whether this object has been disposed.
        /// </summary>
        private bool disposedValue = false; // To detect redundant calls

        /// <summary>
        /// Releases resources used by this dialog.
        /// </summary>
        /// <param name="disposing">True if called from Dispose(); false if called from a finalizer</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects).
                    SequencePreviewEditor?.DisposeEmbeddedContent();
                    SequencePreviewEditor = null;
                    TexturePreviewViewer?.Dispose();
                    TexturePreviewViewer = null;
                    Interlocked.Increment(ref TexturePreviewRequestVersion);
                    PackageCache texturePreviewPackageCache = TexturePreviewPackageCache;
                    TexturePreviewPackageCache = null;
                    if (texturePreviewPackageCache is not null)
                    {
                        TexturePreviewResolutionTask.ContinueWith(_ => texturePreviewPackageCache.Dispose(),
                            TaskScheduler.Default);
                    }
                    AudioPreviewPlayer?.Dispose();
                    AudioPreviewPlayer = null;
                    Interlocked.Increment(ref AudioPreviewRequestVersion);
                    PackageCache audioPreviewPackageCache = AudioPreviewPackageCache;
                    AudioPreviewPackageCache = null;
                    if (audioPreviewPackageCache is not null)
                    {
                        AudioPreviewResolutionTask.ContinueWith(_ => audioPreviewPackageCache.Dispose(),
                            TaskScheduler.Default);
                    }
                    if (PreviewHost is not null)
                    {
                        PreviewHost.Content = null;
                    }
                    Pcc = null;
                }

                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        /// <summary>
        /// Releases all resources used by this dialog.
        /// </summary>
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
        }
        #endregion

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
        }

        /// <summary>
        /// Handles the Cancel button click event.
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Dispose();
        }

        /// <summary>
        /// Handles key down events in the entry selector combo box, allowing Enter key to accept selection.
        /// </summary>
        private void EntrySearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && FilteredEntriesList.Count > 0)
            {
                EntrySelectorListView.Focus();
                if (SelectedEntryItem is null)
                {
                    SelectedEntryItem = FilteredEntriesList[0];
                }

                if (SelectedEntryItem is not null)
                {
                    EntrySelectorListView.ScrollIntoView(SelectedEntryItem);
                }

                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && ItemSearch is not null)
            {
                RunItemSearch();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && OKCommand.CanExecute(null))
            {
                OKCommand.Execute(null);
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            RunItemSearch();
        }

        private void EntryFilterTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && FilteredEntriesList.Count > 0)
            {
                EntrySelectorListView.Focus();
                if (SelectedEntryItem is null)
                {
                    SelectedEntryItem = FilteredEntriesList[0];
                }

                EntrySelectorListView.ScrollIntoView(SelectedEntryItem);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && OKCommand.CanExecute(null))
            {
                OKCommand.Execute(null);
            }
        }

        private void EntrySelectorListView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && OKCommand.CanExecute(null))
            {
                OKCommand.Execute(null);
            }
        }

        private void EntrySelectorListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source
                && ItemsControl.ContainerFromElement(EntrySelectorListView, source) is ListViewItem
                && OKCommand.CanExecute(null))
            {
                OKCommand.Execute(null);
            }
        }
    }

    public sealed class EntrySelectorWwiseTlkTextConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            return values.Length >= 2 && values[1] is EntrySelector selector
                ? selector.GetWwiseTlkDisplayText(values[0])
                : null;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
