using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Converters;
using LegendaryExplorer.SharedUI.Interfaces;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace LegendaryExplorer.UserControls.SharedToolControls
{
    /// <summary>
    /// Control that handles the 'Recents' system, including the main no-open-file panel and the menu system for windows. All calls must be done from a UI thread.
    /// </summary>
    public partial class RecentsControl : NotifyPropertyChangedControlBase, IDisposable
    {
        public class RecentItem
        {
            public RecentItem(string path, MEGame? game)
            {
                Path = path;
                Game = game;
            }

            public RecentItem() { }

            public string ConvertToRecentEntry()
            {
                // Null coalescing doesn't work here apparently
                return $"{(Game == null ? "NUL" : Game)} {Path}";
            }

            public static RecentItem FromRecentEntryString(string entry)
            {
#if DEBUG
                // TRANSITION TO NEW RECENT SYSTEM ONLY CODE!!
                // Remove later. This is debug only cause it was made when LEX was in dev
                if (File.Exists(entry))
                {
                    return new RecentItem(entry, null);
                }

#endif
                var gameId = entry.Substring(0, 3);
                MEGame? game = null;
                if (Enum.TryParse<MEGame>(gameId, false, out var _game))
                {
                    game = _game;
                }
                return new RecentItem(entry.Substring(4), game);
            }

            public MEGame? Game { get; }
            public string Path { get; }
            public bool IsAfc => string.Equals(System.IO.Path.GetExtension(Path), ".afc", StringComparison.OrdinalIgnoreCase);
        }
        private Action<string> RecentItemClicked;

        public ObservableCollectionExtended<RecentItem> RecentItems { get; } = new();
        public ObservableCollectionExtended<RecentItem> PinnedItems { get; } = new();

        // Weak references allow open windows (including different hosted tools) to share
        // pins by tool name without keeping closed windows alive.
        private static readonly List<WeakReference<RecentsControl>> PinControls = new();
        private RecentsControl pinnedMenuControl;
        private MenuItem pinMenuAnchor;
        private MenuItem pinFileMenu;
        private MenuItem pinFileMenuParent;

        public class RecentItemGroup(MEGame? game, bool isPinned = false)
        {
            public MEGame? Game { get; } = game;
            public bool IsPinned { get; } = isPinned;
            public string Header => IsPinned ? "Pinned" : Game?.ToString() ?? "Misc";
            public string EmptyMessage => IsPinned
                ? "No pinned files to show. Use Pin file… or right-click a recent item."
                : "No recently opened items in this tab.";
            public ObservableCollectionExtended<RecentItem> Items { get; } = new();
        }

        public IReadOnlyList<RecentItemGroup> RecentGroups { get; } = new[]
        {
            new RecentItemGroup(MEGame.ME1),
            new RecentItemGroup(MEGame.ME2),
            new RecentItemGroup(MEGame.ME3),
            new RecentItemGroup(MEGame.LE1),
            new RecentItemGroup(MEGame.LE2),
            new RecentItemGroup(MEGame.LE3),
            new RecentItemGroup(null),
            new RecentItemGroup(null, isPinned: true)
        };

        public RecentItemGroup PinnedGroup => RecentGroups.Last();
        public bool ShowPinnedSearch => PinnedItems.Count > 9;
        public string PinBrowseLabel => IsFolderRecents ? "Pin folder…" : "Pin file…";
        public string PinnedFileFilter { get; set; } = GameFileFilters.OpenFileFilter;

        private string pinnedSearchText = "";
        public string PinnedSearchText
        {
            get => pinnedSearchText;
            set
            {
                if (SetProperty(ref pinnedSearchText, value ?? ""))
                {
                    RefreshPinnedGroup();
                }
            }
        }

        private RecentItemGroup selectedRecentGroup;
        public RecentItemGroup SelectedRecentGroup
        {
            get => selectedRecentGroup;
            set => SetProperty(ref selectedRecentGroup, value);
        }

        private RecentItemGroup GetRecentGroup(RecentItem item)
        {
            // Non-PCC files can also carry a game (for example, ME3 SFARs).
            MEGame? game = !IsFolderRecents
                           && string.Equals(Path.GetExtension(item.Path), ".pcc", StringComparison.OrdinalIgnoreCase)
                           && item.Game is MEGame knownGame && knownGame.IsMEGame()
                ? item.Game
                : null;
            return RecentGroups.First(group => !group.IsPinned && group.Game == game);
        }

        private void RefreshRecentGroups(RecentItem preferredItem = null)
        {
            foreach (var group in RecentGroups.Where(group => !group.IsPinned))
            {
                group.Items.ReplaceAll(RecentItems.Where(item => GetRecentGroup(item) == group));
            }

            if (preferredItem != null)
            {
                SelectedRecentGroup = GetRecentGroup(preferredItem);
            }
            else if (SelectedRecentGroup == null || (!SelectedRecentGroup.IsPinned && !SelectedRecentGroup.Items.Any))
            {
                SelectedRecentGroup = RecentItems.Count > 0 ? GetRecentGroup(RecentItems[0]) : RecentGroups[0];
            }
        }

        public bool IsFolderRecents
        {
            get => (bool)GetValue(IsFolderRecentsProperty);
            set => SetValue(IsFolderRecentsProperty, value);
        }

        public static readonly DependencyProperty IsFolderRecentsProperty = DependencyProperty.Register(
            nameof(IsFolderRecents), typeof(bool), typeof(RecentsControl), new PropertyMetadata(false, (sender, _) =>
            {
                var control = (RecentsControl)sender;
                control.OnPropertyChanged(nameof(PinBrowseLabel));
                if (control.pinFileMenu != null) control.pinFileMenu.Header = control.PinBrowseLabel;
            }));

        public bool ShowRecentTabs
        {
            get => (bool)GetValue(ShowRecentTabsProperty);
            set => SetValue(ShowRecentTabsProperty, value);
        }

        public static readonly DependencyProperty ShowRecentTabsProperty = DependencyProperty.Register(
            nameof(ShowRecentTabs), typeof(bool), typeof(RecentsControl), new PropertyMetadata(true, (sender, args) =>
            {
                var control = (RecentsControl)sender;
                if (control.RecentTabs == null) return;
                // Keep the normal theme for visible tabs. Pins-only views have no headers.
                var hiddenTabStyle = new Style(typeof(TabItem));
                hiddenTabStyle.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
                control.RecentTabs.ItemContainerStyle = (bool)args.NewValue ? null : hiddenTabStyle;
            }));

        public RecentsControl()
        {
            SelectedRecentGroup = RecentGroups[0];
            LoadCommands();
            InitializeComponent();
        }

        private void RecentsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var scrollViewer = (ScrollViewer)sender;
            if (e.Delta > 0 ? scrollViewer.VerticalOffset > 0 : scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight)
            {
                return;
            }

            // Let the welcome page scroll when this panel has no more content to scroll.
            for (var parent = VisualTreeHelper.GetParent(this); parent != null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is ScrollViewer outerScrollViewer)
                {
                    e.Handled = true;
                    outerScrollViewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                    {
                        RoutedEvent = Mouse.MouseWheelEvent
                    });
                    return;
                }
            }
        }

        public RelayCommand RecentFileOpenCommand { get; private set; }
        public RelayCommand OpenRecentItemLocationCommand { get; private set; }
        public RelayCommand CopyRecentItemPathCommand { get; private set; }
        public RelayCommand CopyRecentItemNameCommand { get; private set; }
        public RelayCommand BrowsePinnedItemsCommand { get; private set; }

        private void LoadCommands()
        {
            RecentFileOpenCommand = new RelayCommand(filePath => RecentItemClicked?.Invoke((string)filePath), CanAccessRecentItem);
            OpenRecentItemLocationCommand = new RelayCommand(OpenRecentItemLocation, CanAccessRecentItem);
            CopyRecentItemPathCommand = new RelayCommand(CopyRecentItemPath, CanAccessRecentItem);
            CopyRecentItemNameCommand = new RelayCommand(CopyRecentItemName, CanAccessRecentItem);
            BrowsePinnedItemsCommand = new RelayCommand(_ => BrowsePinnedItems(), _ => RecentsFoldername != null);
        }

        private static bool CanAccessRecentItem(object pathObj)
        {
            if (pathObj is not string path || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return File.Exists(path) || Directory.Exists(path);
        }

        private static void OpenRecentItemLocation(object pathObj)
        {
            if (pathObj is not string path || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            path = Path.GetFullPath(path);
            if (Directory.Exists(path))
            {
                DirectoryMemory.RememberExplorerLocation("RecentsControl.OpenRecentItemLocation", path);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                return;
            }

            if (File.Exists(path))
            {
                DirectoryMemory.RememberExplorerLocation("RecentsControl.OpenRecentItemLocation", path);
                LegendaryExplorerCoreUtilities.OpenAndSelectFileInExplorer(path);
            }
        }

        private static void CopyRecentItemPath(object pathObj)
        {
            if (pathObj is string path && !string.IsNullOrWhiteSpace(path))
            {
                Clipboard.SetText(Path.GetFullPath(path));
            }
        }

        private static void CopyRecentItemName(object pathObj)
        {
            if (pathObj is string path && !string.IsNullOrWhiteSpace(path))
            {
                Clipboard.SetText(GetItemName(path));
            }
        }

        private static string GetItemName(string path)
        {
            path = Path.GetFullPath(path);

            if (Directory.Exists(path))
            {
                return new DirectoryInfo(path).Name;
            }

            return Path.GetFileName(path);
        }

        private void OpenRecentItemLocation_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: RecentItem item })
            {
                OpenRecentItemLocation(item.Path);
            }
        }

        private void CopyRecentItemPath_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: RecentItem item })
            {
                CopyRecentItemPath(item.Path);
            }
        }

        private void CopyRecentItemName_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: RecentItem item })
            {
                CopyRecentItemName(item.Path);
            }
        }

        private void RecentItemContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu { DataContext: RecentItem item } menu
                && menu.Items[0] is MenuItem pinItem)
            {
                pinItem.Header = IsPinned(item.Path) ? "Unpin" : "Pin";
                pinItem.IsEnabled = RecentsFoldername != null;
            }
        }

        private void TogglePinnedItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: RecentItem item })
            {
                TogglePinItem(item);
            }
        }

        public bool IsPinned(string path) => PinnedItems.Any(item =>
            string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));

        public void TogglePinItem(RecentItem item)
        {
            if (IsPinned(item.Path)) UnpinItem(item);
            else PinItem(item);
        }

        public void PinItem(RecentItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path) || IsPinned(item.Path)) return;
            PinnedItems.Add(new RecentItem(Path.GetFullPath(item.Path), item.Game));
            RefreshPinnedGroup();
            SavePinnedList();
        }

        public void UnpinItem(RecentItem item)
        {
            if (item == null) return;
            PinnedItems.ReplaceAll(PinnedItems.Where(pin => !string.Equals(pin.Path, item.Path, StringComparison.OrdinalIgnoreCase)).ToList());
            RefreshPinnedGroup();
            SavePinnedList();
        }

        private void RefreshPinnedGroup()
        {
            // A hidden search field must not leave the short list filtered.
            if (!ShowPinnedSearch && pinnedSearchText.Length > 0)
            {
                pinnedSearchText = "";
                OnPropertyChanged(nameof(PinnedSearchText));
            }
            string search = PinnedSearchText.Trim();
            PinnedGroup.Items.ReplaceAll(PinnedItems.Where(item =>
                item.Path.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList());
            OnPropertyChanged(nameof(ShowPinnedSearch));
        }

        private void SetPinnedItems(IEnumerable<RecentItem> items)
        {
            PinnedItems.ReplaceAll(items.Where(item => !string.IsNullOrWhiteSpace(item.Path))
                .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList());
            RefreshPinnedGroup();
        }

        private void RegisterPinControl()
        {
            PinControls.RemoveAll(reference => !reference.TryGetTarget(out var control) || ReferenceEquals(control, this));
            PinControls.Add(new WeakReference<RecentsControl>(this));
            // A static handler uses the existing weak registry so closed controls
            // do not stay alive when a hosted tool omits its Dispose call.
            ScottinaPreset.Applied -= OnScottinaPresetApplied;
            ScottinaPreset.Applied += OnScottinaPresetApplied;
        }

        private static void OnScottinaPresetApplied(object sender, EventArgs e)
        {
            PinControls.RemoveAll(reference => !reference.TryGetTarget(out _));
            foreach (var reference in PinControls.ToList())
            {
                if (reference.TryGetTarget(out var control))
                {
                    control.ReloadPinnedPreferences();
                }
            }
            if (PinControls.Count == 0)
                ScottinaPreset.Applied -= OnScottinaPresetApplied;
        }

        private void ReloadPinnedPreferences()
        {
            if (RecentsFoldername == null)
                return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ReloadPinnedPreferences);
                return;
            }

            SetPinnedItems(File.Exists(PinnedAppDataFile)
                ? File.ReadAllLines(PinnedAppDataFile).Where(entry => entry.Length > 4)
                    .Select(RecentItem.FromRecentEntryString)
                : []);
            RefreshRecentsMenu();
        }

        private void SavePinnedList()
        {
            if (RecentsFoldername == null) return;
            File.WriteAllLines(PinnedAppDataFile, PinnedItems.Select(item => item.ConvertToRecentEntry()));
            foreach (var reference in PinControls.ToList())
            {
                if (reference.TryGetTarget(out var control) && !ReferenceEquals(control, this)
                    && string.Equals(control.RecentsFoldername, RecentsFoldername, StringComparison.OrdinalIgnoreCase))
                {
                    control.SetPinnedItems(PinnedItems);
                }
            }
        }

        public void BrowsePinnedItems()
        {
            if (RecentsFoldername == null) return;
            string title = $"Pin {(IsFolderRecents ? "folder" : "files")} — {Path.GetFileName(RecentsFoldername)}";
            if (IsFolderRecents)
            {
                using var dialog = new CommonOpenFileDialog { Title = title, IsFolderPicker = true, Multiselect = true };
                if (DirectoryMemory.ShowDialog(dialog, Window.GetWindow(this)) != CommonFileDialogResult.Ok) return;
                foreach (string path in dialog.FileNames) PinItem(new RecentItem(path, null));
            }
            else
            {
                var dialog = new OpenFileDialog { Title = title, Filter = PinnedFileFilter, Multiselect = true, CheckFileExists = true };
                if (DirectoryMemory.ShowDialog(dialog, Window.GetWindow(this)) != true) return;
                foreach (string path in dialog.FileNames)
                {
                    MEGame? game = RecentItems.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))?.Game;
                    if (game == null && new[] { ".pcc", ".u", ".upk", ".sfm", ".udk", ".xxx" }
                        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using var package = MEPackageHandler.QuickOpenMEPackage(path);
                            game = package.Game;
                        }
                        catch (Exception)
                        {
                            // Files other than packages can still be pinned, without a game icon.
                        }
                    }
                    PinItem(new RecentItem(path, game));
                }
            }
            SelectedRecentGroup = PinnedGroup;
        }

        /// <summary>Also used by tools with custom recent-file menus.</summary>
        public ContextMenu CreatePinContextMenu(RecentItem item)
        {
            var menu = new ContextMenu { DataContext = item };
            var pin = new MenuItem();
            menu.Opened += (_, _) =>
            {
                pin.Header = IsPinned(item.Path) ? "Unpin" : "Pin";
                pin.IsEnabled = RecentsFoldername != null;
            };
            pin.Click += (_, _) => TogglePinItem(item);
            menu.Items.Add(pin);
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Open file location", Icon = FindResource("WindowsExplorerMenuIcon"), Command = OpenRecentItemLocationCommand, CommandParameter = item.Path });
            menu.Items.Add(new MenuItem { Header = "Copy file path", Command = CopyRecentItemPathCommand, CommandParameter = item.Path });
            menu.Items.Add(new MenuItem { Header = "Copy file name", Command = CopyRecentItemNameCommand, CommandParameter = item.Path });
            return menu;
        }

        public MenuItem CreatePinnedMenu()
        {
            pinnedMenuControl?.Dispose();
            pinnedMenuControl = new RecentsControl
            {
                RecentsFoldername = RecentsFoldername,
                RecentItemClicked = RecentItemClicked,
                IsFolderRecents = IsFolderRecents,
                PinnedFileFilter = PinnedFileFilter,
                ShowRecentTabs = false,
                Width = 540
            };
            pinnedMenuControl.SetPinnedItems(PinnedItems);
            pinnedMenuControl.SelectedRecentGroup = pinnedMenuControl.PinnedGroup;
            pinnedMenuControl.RegisterPinControl();
            var menu = new MenuItem { Header = "Pinned files" };
            menu.Items.Add(new MenuItem { Header = pinnedMenuControl, StaysOpenOnClick = true });
            return menu;
        }

        /// <summary>Adds the file picker directly to the File menu next to Recents.</summary>
        public void AttachPinFileMenu(MenuItem recentsMenu)
        {
            if (!ReferenceEquals(pinMenuAnchor, recentsMenu))
            {
                DetachPinFileMenu();
                pinMenuAnchor = recentsMenu;
                pinMenuAnchor.Loaded += PinMenuAnchor_Loaded;
            }
            TryAttachPinFileMenu();
        }

        private void PinMenuAnchor_Loaded(object sender, RoutedEventArgs e) => TryAttachPinFileMenu();

        private void TryAttachPinFileMenu()
        {
            if (RecentsFoldername == null || pinFileMenu != null || pinMenuAnchor == null) return;
            var parent = ItemsControl.ItemsControlFromItemContainer(pinMenuAnchor) as MenuItem
                ?? pinMenuAnchor.Parent as MenuItem;
            if (parent == null) return;
            pinFileMenu = new MenuItem
            {
                Header = PinBrowseLabel,
                Command = BrowsePinnedItemsCommand
            };
            pinFileMenuParent = parent;
            parent.Items.Insert(parent.Items.IndexOf(pinMenuAnchor) + 1, pinFileMenu);
        }

        private void DetachPinFileMenu()
        {
            if (pinMenuAnchor != null) pinMenuAnchor.Loaded -= PinMenuAnchor_Loaded;
            pinFileMenuParent?.Items.Remove(pinFileMenu);
            pinMenuAnchor = null;
            pinFileMenu = null;
            pinFileMenuParent = null;
        }

        private string RecentsAppDataFile => Path.Combine(Directory.CreateDirectory(Path.Combine(AppDirectories.AppDataFolder, RecentsFoldername)).FullName, "RECENTFILES");
        private string PinnedAppDataFile => Path.Combine(Path.GetDirectoryName(RecentsAppDataFile), "PINNEDFILES");

        /// <summary>
        /// Must be called before the control will properly work
        /// </summary>
        /// <param name="filename">Recents filename. Stored in the appdata. Do not pass an extension, just the name.</param>
        /// <param name="openFileCallback">The callback to invoke when a recents item is clicked.</param>
        public void InitRecentControl(string toolname, MenuItem recentsMenu, Action<string> openFileCallback,
            string fileFilter = GameFileFilters.OpenFileFilter, bool loadRecents = true)
        {
            DetachPinFileMenu();
            pinnedMenuControl?.Dispose();
            pinnedMenuControl = null;
            RecentsMenu = recentsMenu;
            RecentsMenu.IsEnabled = false; //Default to false as there may be no recents
            RecentItemClicked = null;
            RecentsFoldername = toolname;
            RecentItems.ClearEx();
            PinnedItems.ClearEx();
            PinnedSearchText = "";
            RefreshRecentGroups();
            RefreshPinnedGroup();
            if (toolname == null)
            {
                // Recents is disabled
                RecentItems.ClearEx();
                RefreshRecentGroups();
                RecentsMenu.Items.Clear();
                return;
            }

            // Init the control
            RecentItemClicked = openFileCallback;
            PinnedFileFilter = fileFilter;
            RegisterPinControl();
            AttachPinFileMenu(recentsMenu);
            
            // Load recents list
            if (loadRecents && File.Exists(RecentsAppDataFile))
            {
                string[] recents = File.ReadAllLines(RecentsAppDataFile);
                SetRecents(recents.Select(RecentItem.FromRecentEntryString), selectMostRecent: true);
            }
            if (File.Exists(PinnedAppDataFile))
            {
                SetPinnedItems(File.ReadAllLines(PinnedAppDataFile).Where(entry => entry.Length > 4)
                    .Select(RecentItem.FromRecentEntryString));
            }
            RefreshRecentsMenu();
        }

        /// <summary>Loads pins for tools that maintain their own recent-file format.</summary>
        public void InitPinnedControl(string toolname, Action<string> openFileCallback, string fileFilter = GameFileFilters.OpenFileFilter)
            => InitRecentControl(toolname, new MenuItem(), openFileCallback, fileFilter, loadRecents: false);

        /// <summary>
        /// Sets the whole recents list. Does not propogate.
        /// </summary>
        /// <param name="recents"></param>
        private void SetRecents(IEnumerable<RecentItem> recents, bool selectMostRecent = false)
        {
            var recentItems = recents.ToList();
            RecentItems.ClearEx();
            foreach (var referencedFile in recentItems)
            {
                if (IsFolderRecents)
                {
                    if (Directory.Exists(referencedFile.Path))
                    {
                        AddRecent(referencedFile.Path, true, referencedFile.Game);
                    }
                }
                else if (File.Exists(referencedFile.Path))
                {
                    AddRecent(referencedFile.Path, true, referencedFile.Game);
                }
            }
            RefreshRecentGroups(selectMostRecent ? RecentItems.FirstOrDefault() : null);
            RefreshRecentsMenu();
        }

        /// <summary>
        /// Appdata subfolder that will hold the RECENTS file
        /// </summary>
        public string RecentsFoldername { get; set; }

        /// <summary>
        /// Menu that is associated with the recents and is updated when the recents change
        /// </summary>
        public MenuItem RecentsMenu { get; set; }

        /// <summary>
        /// Refreshes the recents menu items
        /// </summary>
        /// <param name="recentsContainer"></param>
        private void RefreshRecentsMenu()
        {
            if (RecentsMenu == null) return;
            RecentsMenu.Items.Clear();
            // Browsing for a pin is available even before this tool has any history.
            RecentsMenu.IsEnabled = RecentsFoldername != null || RecentItems.Count > 0;
            RecentsMenu.Items.Add(CreatePinnedMenu());
            foreach (var recentItem in RecentItems)
            {
                var iconBitmap = GameToImageIconConverter.StaticConvert(recentItem.Game);
                var fr = new MenuItem
                {
                    Icon = recentItem.IsAfc ? new StatusBarGameIDIndicator { GameType = "AFC" } : iconBitmap == null ? null : new Image { Source = iconBitmap },
                    Header = recentItem.Path.Replace("_", "__"),
                    Tag = recentItem.Path,
                    ContextMenu = CreatePinContextMenu(recentItem)
                };
                fr.Click += (x, y) => RecentItemClicked?.Invoke((string)fr.Tag);
                RecentsMenu.Items.Add(fr);
            }
        }

        /// <summary>
        /// Adds a new item to the recents list in the appropriate position.
        /// </summary>
        /// <param name="path">The file path of the file that is being added</param>
        /// <param name="isLoading">If the control is loading, and the list shouldn't be cleared, rather just appended to. </param>
        public void AddRecent(string path, bool isLoading, MEGame? game)
        {
            var recentItem = new RecentItem(path, game);
            if (isLoading)
            {
                RecentItems.Add(recentItem); //in order
            }
            else
            {
                // Remove the new recent from the list if it exists - as we will re-insert it (at the front)
                RecentItems.ReplaceAll(RecentItems.Where(x =>
                    !x.Path.Equals(path, StringComparison.InvariantCultureIgnoreCase)).ToList());
                RecentItems.Insert(0, recentItem); //put at front
            }
            // Each tab keeps its own history, so opening one game doesn't evict another.
            var group = GetRecentGroup(recentItem);
            foreach (var olderItem in RecentItems.Where(item => GetRecentGroup(item) == group).Skip(10).ToList())
            {
                RecentItems.Remove(olderItem);
            }

            RecentsMenu.IsEnabled = true; //An item exists in the menu
            if (!isLoading)
            {
                RefreshRecentGroups(recentItem);
                RefreshRecentsMenu();
                SaveRecentList(true);
            }
        }

        /// <summary>
        /// Commits the list of recent files to disk.
        /// </summary>
        /// <param name="propogate">If the list of recents from this instance should be shared to other instances that are hosted by the same type of window</param>
        public void SaveRecentList(bool propogate)
        {
            if (RecentsFoldername != null)
            {
                File.WriteAllLines(RecentsAppDataFile, RecentItems.Select(x => x.ConvertToRecentEntry()));
                if (propogate)
                {
                    PropogateRecentsChange(true, RecentItems);
                }
            }
        }

        public void PropogateRecentsChange(bool outboundPropogation, IEnumerable<RecentItem> newRecents)
        {
            if (outboundPropogation)
            {
                var propogationSource = Window.GetWindow(this);
                //we are posting an update to other instances
                foreach (var form in Application.Current.Windows)
                {
                    if (form.GetType() == propogationSource.GetType() && !ReferenceEquals(form, propogationSource) && form is IRecents recentsSupportedWindow && ((Window)form).IsLoaded)
                    {
                        recentsSupportedWindow.PropogateRecentsChange(RecentsFoldername, newRecents);
                    }
                }
            }
            else
            {
                // Inbound, we are receiving an update
                SetRecents(newRecents);
            }
        }

        public void Dispose()
        {
            DetachPinFileMenu();
            PinControls.RemoveAll(reference => !reference.TryGetTarget(out var control) || ReferenceEquals(control, this));
            pinnedMenuControl?.Dispose();
            pinnedMenuControl = null;
            if (PinControls.Count == 0)
                ScottinaPreset.Applied -= OnScottinaPresetApplied;
            RecentItemClicked = null;
            RecentsMenu = null;
            RecentsFoldername = null;
        }
    }
}
