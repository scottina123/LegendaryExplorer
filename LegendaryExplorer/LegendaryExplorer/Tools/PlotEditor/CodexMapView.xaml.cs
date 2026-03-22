using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Gammtek.Conduit.MassEffect3.SFXGame.CodexMap;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Tools.PlotEditor.Dialogs;
using LegendaryExplorer.SharedUI.Controls;
using LegendaryExplorer.UnrealExtensions;
using LegendaryExplorer.UnrealExtensions.Classes;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using static LegendaryExplorer.Tools.TlkManagerNS.TLKManagerWPF;
using LegendaryExplorer.Misc;
using LegendaryExplorerCore.Audio;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Sound.Wwise;
using LegendaryExplorerCore.Unreal;
using Microsoft.Win32;
using WwiseEventBinary = LegendaryExplorerCore.Unreal.BinaryConverters.WwiseEvent;
using WwiseStreamBinary = LegendaryExplorerCore.Unreal.BinaryConverters.WwiseStream;

namespace LegendaryExplorer.Tools.PlotEditor
{
	/// <summary>
	///   Interaction logic for CodexMapView.xaml.
	/// </summary>
	public partial class CodexMapView : NotifyPropertyChangedControlBase
	{
        public static IMEPackage package;

		/// <summary>
		///   Initializes a new instance of the <see cref="CodexMapView" /> class.
		/// </summary>
		public CodexMapView()
		{
            InitializeComponent();
            Unloaded += CodexMapView_Unloaded;
            SetFromCodexMap(new BioCodexMap());
        }

        private ObservableCollection<KeyValuePair<int, BioCodexPage>> _codexPages;
        private ObservableCollection<KeyValuePair<int, BioCodexSection>> _codexSections;
        private ObservableCollection<object> _primaryCodexTreeItems;
        private ObservableCollection<object> _secondaryCodexTreeItems;
        private KeyValuePair<int, BioCodexPage> _selectedCodexPage;
        private KeyValuePair<int, BioCodexSection> _selectedCodexSection;
        private int _selectedCodexTreeTabIndex;
        private string _searchText;
        private bool _isRefreshingCodexHierarchy;
        private bool _isRefreshingSelectedText;
        private SoundpanelAudioPlayer _codexAudioPlayer;
        private string _currentCodexPageWwiseEventDisplay = string.Empty;

        public bool CanRemoveCodexPage
        {
            get
            {
                if (CodexPages == null || CodexPages.Count <= 0)
                {
                    return false;
                }

                return SelectedCodexPage.Value != null;
            }
        }

        public bool CanRemoveCodexSection
        {
            get
            {
                if (CodexSections == null || CodexSections.Count <= 0)
                {
                    return false;
                }

                return SelectedCodexSection.Value != null;
            }
        }

        public bool CanRemoveSelectedCodexItem => SelectedCodexPage.Value != null || SelectedCodexSection.Value != null;

        public bool IsCodexPageSelected => SelectedCodexPage.Value != null;

        public bool IsCodexSectionSelected => SelectedCodexSection.Value != null;

        public bool CanAddCodexPageAudio => package != null && SelectedCodexPage.Value != null && package.Game is MEGame.LE2 or MEGame.LE3;

        public bool HasCodexPageAudioReference => SelectedCodexPage.Value != null && SelectedCodexPage.Value.CodexSound != 0;

        public bool CanStopCodexPageAudio => HasCodexPageAudioReference;

        public string CurrentCodexPageWwiseEventDisplay
        {
            get => _currentCodexPageWwiseEventDisplay;
            set => SetProperty(ref _currentCodexPageWwiseEventDisplay, value ?? string.Empty);
        }

        public ObservableCollection<KeyValuePair<int, BioCodexPage>> CodexPages
        {
            get => _codexPages;
            set
            {
                SetProperty(ref _codexPages, value);
                OnPropertyChanged(nameof(CanRemoveCodexPage));
                RefreshCodexHierarchy();
            }
        }

        public ObservableCollection<KeyValuePair<int, BioCodexSection>> CodexSections
        {
            get => _codexSections;
            set
            {
                SetProperty(ref _codexSections, value);
                OnPropertyChanged(nameof(CanRemoveCodexSection));
                RefreshCodexHierarchy();
            }
        }

        public ObservableCollection<object> PrimaryCodexTreeItems
        {
            get => _primaryCodexTreeItems;
            set => SetProperty(ref _primaryCodexTreeItems, value);
        }

        public ObservableCollection<object> SecondaryCodexTreeItems
        {
            get => _secondaryCodexTreeItems;
            set => SetProperty(ref _secondaryCodexTreeItems, value);
        }

        public int SelectedCodexTreeTabIndex
        {
            get => _selectedCodexTreeTabIndex;
            set => SetProperty(ref _selectedCodexTreeTabIndex, value);
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (!SetProperty(ref _searchText, value ?? string.Empty))
                {
                    return;
                }

                RefreshCodexHierarchy();
            }
        }

        public KeyValuePair<int, BioCodexPage> SelectedCodexPage
        {
            get => _selectedCodexPage;
            set
            {
                SetProperty(ref _selectedCodexPage, value);
                OnPropertyChanged(nameof(CanRemoveCodexPage));
                OnPropertyChanged(nameof(CanRemoveSelectedCodexItem));
                OnPropertyChanged(nameof(IsCodexPageSelected));
                StopCodexAudioPlayback();
                RefreshCodexPageAudioState();
            }
        }

        public KeyValuePair<int, BioCodexSection> SelectedCodexSection
        {
            get => _selectedCodexSection;
            set
            {
                SetProperty(ref _selectedCodexSection, value);
                OnPropertyChanged(nameof(CanRemoveCodexSection));
                OnPropertyChanged(nameof(CanRemoveSelectedCodexItem));
                OnPropertyChanged(nameof(IsCodexSectionSelected));
                StopCodexAudioPlayback();
                RefreshCodexPageAudioState();
            }
        }

        public void AddCodexPage()
        {
            if (CodexPages == null)
            {
                CodexPages = InitCollection<KeyValuePair<int, BioCodexPage>>();
            }

            var dlg = new NewObjectDialog
            {
                ContentText = "New codex page",
                ObjectId = GetMaxCodexPageId() + 1
            };

            if (dlg.ShowDialog() == false || dlg.ObjectId < 0)
            {
                return;
            }

            AddCodexPage(dlg.ObjectId);
        }

        public void AddCodexPage(int id, BioCodexPage codexPage = null)
        {
            if (CodexPages == null)
            {
                CodexPages = InitCollection<KeyValuePair<int, BioCodexPage>>();
            }

            if (id < 0)
            {
                return;
            }

            var codexPagePair = new KeyValuePair<int, BioCodexPage>(id, codexPage ?? new BioCodexPage());
            if (package?.Game == MEGame.LE1)
            {
                codexPagePair.Value.IsLE1 = true;
            }
            
            CodexPages.Add(codexPagePair);

            SelectedCodexPage = codexPagePair;
            SelectedCodexSection = default;
            RefreshCodexHierarchy();
        }

        public void addCodexSection()
        {
            if (CodexSections == null)
            {
                CodexSections = InitCollection<KeyValuePair<int, BioCodexSection>>();
            }

            var dlg = new NewObjectDialog
            {
                ContentText = "New codex section",
                ObjectId = GetMaxCodexSectionId() + 1
            };

            if (dlg.ShowDialog() == false || dlg.ObjectId < 0)
            {
                return;
            }

            addCodexSection(dlg.ObjectId);
        }

        // Does not replace existing
        public void addCodexSection(int id, BioCodexSection codexSection = null)
        {
            if (CodexSections == null)
            {
                CodexSections = InitCollection<KeyValuePair<int, BioCodexSection>>();
            }

            if (CodexSections.Any(pair => pair.Key == id))
            {
                return;
            }

            var codexSectionPair = new KeyValuePair<int, BioCodexSection>(id, codexSection ?? new BioCodexSection());

            CodexSections.Add(codexSectionPair);

            SelectedCodexSection = codexSectionPair;
            SelectedCodexPage = default;
            RefreshCodexHierarchy();
        }

        public void ChangeCodexPageId()
        {
            if (SelectedCodexPage.Value == null)
            {
                return;
            }

            var dlg = new ChangeObjectIdDialog
            {
                ContentText = $"Change id of codex page #{SelectedCodexPage.Key}",
                ObjectId = SelectedCodexPage.Key
            };

            if (dlg.ShowDialog() == false || dlg.ObjectId < 0 || dlg.ObjectId == SelectedCodexPage.Key)
            {
                return;
            }

            var codexSection = SelectedCodexPage.Value;

            CodexPages.Remove(SelectedCodexPage);

            AddCodexPage(dlg.ObjectId, codexSection);
        }

        public void ChangeCodexSectionId()
        {
            if (SelectedCodexSection.Value == null)
            {
                return;
            }

            var dlg = new ChangeObjectIdDialog
            {
                ContentText = $"Change id of codex section #{SelectedCodexSection.Key}",
                ObjectId = SelectedCodexSection.Key
            };

            if (dlg.ShowDialog() == false || dlg.ObjectId < 0 || dlg.ObjectId == SelectedCodexSection.Key)
            {
                return;
            }

            var codexSection = SelectedCodexSection.Value;

            CodexSections.Remove(SelectedCodexSection);

            addCodexSection(dlg.ObjectId, codexSection);
        }

        public void CopyCodexPage()
        {
            if (SelectedCodexPage.Value == null)
            {
                return;
            }

            var dlg = new CopyObjectDialog
            {
                ContentText = $"Copy codex page #{SelectedCodexPage.Key}",
                ObjectId = GetMaxCodexPageId() + 1
            };

            if (dlg.ShowDialog() == false || dlg.ObjectId < 0 || SelectedCodexPage.Key == dlg.ObjectId)
            {
                return;
            }

            AddCodexPage(dlg.ObjectId, new BioCodexPage(SelectedCodexPage.Value));
        }

        public void CopyCodexSection()
        {
            if (SelectedCodexSection.Value == null)
            {
                return;
            }

            var dlg = new CopyObjectDialog
            {
                ContentText = $"Copy codex section #{SelectedCodexSection.Key}",
                ObjectId = GetMaxCodexSectionId() + 1
            };

            if (dlg.ShowDialog() == false || dlg.ObjectId < 0 || SelectedCodexSection.Key == dlg.ObjectId)
            {
                return;
            }

            addCodexSection(dlg.ObjectId, new BioCodexSection(SelectedCodexSection.Value));
        }

        public void GoToCodexPage(KeyValuePair<int, BioCodexPage> codexPage)
        {
            SelectedCodexPage = codexPage;
            SelectedCodexSection = default;
            var treeItem = FindPageTreeItem(codexPage.Key);
            SelectTabForTreeItem(treeItem);
            SelectTreeItem(treeItem);
            GetSelectedCodexTreeView()?.Focus();
        }

        public void GoToCodexSection(KeyValuePair<int, BioCodexSection> codexSection)
        {
            SelectedCodexSection = codexSection;
            SelectedCodexPage = default;
            var treeItem = FindSectionTreeItem(codexSection.Key);
            SelectTabForTreeItem(treeItem);
            SelectTreeItem(treeItem);
            GetSelectedCodexTreeView()?.Focus();
        }

        public static bool TryFindCodexMap(IMEPackage pcc, out ExportEntry export, out int dataOffset)
        {
            export = null;
            dataOffset = -1;

            try
            {
                export = pcc.Exports.First(exp => exp.ClassName == "BioCodexMap");
            }
            catch
            {
                return false;
            }

            dataOffset = export.propsEnd();

            return true;
        }

        public void Open(IMEPackage pcc)
        {
            if (!TryFindCodexMap(pcc, out ExportEntry export, out int dataOffset))
            {
                return;
            }

            using (var stream = new MemoryStream(export.Data))
            {
                stream.Seek(dataOffset, SeekOrigin.Begin);
                var codexMap = BinaryBioCodexMap.Load(stream, pcc.Game is MEGame.ME3 or MEGame.LE3 ? Encoding.UTF8 : Encoding.Latin1);

                CodexPages = InitCollection(codexMap.Pages.OrderBy(pair => pair.Key));
                CodexSections = InitCollection(codexMap.Sections.OrderBy(pair => pair.Key));
            }

            foreach (var page in CodexPages)
            {
                page.Value.TitleAsString = StripWrappingQuotes(GlobalFindStrRefbyID(page.Value.Title, pcc.Game, null));
            }

            foreach (var section in CodexSections)
            {
                section.Value.TitleAsString = StripWrappingQuotes(GlobalFindStrRefbyID(section.Value.Title, pcc.Game, null));
            }

            package = pcc;
            RefreshCodexPageAudioState();
            RefreshCodexHierarchy();
            RefreshSelectedText();
        }

        public void RemoveCodexPage()
        {
            if (CodexPages == null || SelectedCodexPage.Value == null)
            {
                return;
            }

            var index = CodexPages.IndexOf(SelectedCodexPage);

            if (!CodexPages.Remove(SelectedCodexPage))
            {
                return;
            }

            if (CodexPages.Any())
            {
                SelectedCodexPage = ((index - 1) >= 0)
                    ? CodexPages[index - 1]
                    : CodexPages.First();
            }
            else
            {
                SelectedCodexPage = default;
            }

            RefreshCodexHierarchy();
        }

        public void removeCodexSection()
        {
            if (CodexSections == null || SelectedCodexSection.Value == null)
            {
                return;
            }

            var index = CodexSections.IndexOf(SelectedCodexSection);

            if (!CodexSections.Remove(SelectedCodexSection))
            {
                return;
            }

            if (CodexSections.Any())
            {
                SelectedCodexSection = ((index - 1) >= 0)
                    ? CodexSections[index - 1]
                    : CodexSections.First();
            }
            else
            {
                SelectedCodexSection = default;
            }

            RefreshCodexHierarchy();
        }

        public void SaveToPcc(IMEPackage pcc)
        {
            ExportEntry export;
            try
            {
                export = pcc.Exports.First(exp => exp.ClassName == "BioCodexMap");
            }
            catch
            {
                return;
            }

            byte[] codexMapData = export.Data;

            if (!export.GetProperties(includeNoneProperties: true).Any())
            {
                return;
            }

            var codexMapDataOffset = export.propsEnd();

            byte[] bytes;
            var codexMap = new BioCodexMap(CodexSections.ToDictionary(pair => pair.Key, pair => pair.Value),
                CodexPages.ToDictionary(pair => pair.Key, pair => pair.Value));

            // CodexMap
            using (var stream = new MemoryStream())
            {
                ((BinaryBioCodexMap)codexMap).Save(stream);

                bytes = stream.ToArray();
            }

            Array.Resize(ref codexMapData, codexMapDataOffset + bytes.Length);
            bytes.CopyTo(codexMapData, codexMapDataOffset);

            export.Data = codexMapData;
        }
        
        public BioCodexMap ToCodexMap()
        {
            var codexMap = new BioCodexMap
            {
                Pages = CodexPages.ToDictionary(pair => pair.Key, pair => pair.Value),
                Sections = CodexSections.ToDictionary(pair => pair.Key, pair => pair.Value)
            };

            return codexMap;
        }

        protected void SetFromCodexMap(BioCodexMap codexMap)
        {
            if (codexMap == null)
            {
                return;
            }

            CodexPages = InitCollection(codexMap.Pages.OrderBy(pair => pair.Key));
            CodexSections = InitCollection(codexMap.Sections.OrderBy(pair => pair.Key));
            RefreshCodexHierarchy();
        }
        
        private static ObservableCollection<T> InitCollection<T>()
        {
            return new ObservableCollection<T>();
        }

        
        private static ObservableCollection<T> InitCollection<T>(IEnumerable<T> collection)
        {
            if (collection == null)
            {
                ThrowHelper.ThrowArgumentNullException(nameof(collection));
            }

            return new ObservableCollection<T>(collection);
        }

        private int GetMaxCodexPageId()
        {
            return CodexPages.Any() ? CodexPages.Max(pair => pair.Key) : -1;
        }

        private int GetMaxCodexSectionId()
        {
            return CodexSections.Any() ? CodexSections.Max(pair => pair.Key) : -1;
        }

        private void RefreshCodexHierarchy()
        {
            if (_isRefreshingCodexHierarchy)
            {
                return;
            }

            _isRefreshingCodexHierarchy = true;

            try
            {
                var selectedPageId = SelectedCodexPage.Value != null ? SelectedCodexPage.Key : (int?)null;
                var selectedSectionId = SelectedCodexSection.Value != null ? SelectedCodexSection.Key : (int?)null;
                var sections = CodexSections?.OrderBy(pair => pair.Key).ToList() ?? new List<KeyValuePair<int, BioCodexSection>>();
                var sectionIds = new HashSet<int>(sections.Select(pair => pair.Key));
                var pages = CodexPages?.OrderBy(pair => pair.Key).ToList() ?? new List<KeyValuePair<int, BioCodexPage>>();
                var standalonePages = pages.Where(pair => !sectionIds.Contains(pair.Value.Section)).ToList();

                PrimaryCodexTreeItems = BuildCodexTreeItems(
                    sections.Where(pair => pair.Value.IsPrimary),
                    pages,
                    Enumerable.Empty<KeyValuePair<int, BioCodexPage>>());

                SecondaryCodexTreeItems = BuildCodexTreeItems(
                    sections.Where(pair => !pair.Value.IsPrimary),
                    pages,
                    standalonePages);

                if (selectedPageId.HasValue)
                {
                    var treeItem = FindPageTreeItem(selectedPageId.Value);
                    SelectTabForTreeItem(treeItem);
                    SelectTreeItem(treeItem);
                }
                else if (selectedSectionId.HasValue)
                {
                    var treeItem = FindSectionTreeItem(selectedSectionId.Value);
                    SelectTabForTreeItem(treeItem);
                    SelectTreeItem(treeItem);
                }
            }
            finally
            {
                _isRefreshingCodexHierarchy = false;
            }
        }

        private ObservableCollection<object> BuildCodexTreeItems(IEnumerable<KeyValuePair<int, BioCodexSection>> sections, IEnumerable<KeyValuePair<int, BioCodexPage>> pages, IEnumerable<KeyValuePair<int, BioCodexPage>> standalonePages)
        {
            var treeItems = new List<object>();
            var hasSearchText = !string.IsNullOrWhiteSpace(SearchText);

            foreach (var section in sections)
            {
                var matchingPages = pages.Where(pair => pair.Value.Section == section.Key && MatchesSearch(pair.Value)).ToList();
                if (hasSearchText && !MatchesSearch(section.Value) && matchingPages.Count == 0)
                {
                    continue;
                }

                var sectionItem = new CodexSectionTreeItem(section);

                foreach (var page in hasSearchText ? matchingPages : pages.Where(pair => pair.Value.Section == section.Key))
                {
                    sectionItem.Pages.Add(new CodexPageTreeItem(page, sectionItem));
                }

                treeItems.Add(sectionItem);
            }

            foreach (var page in hasSearchText ? standalonePages.Where(pair => MatchesSearch(pair.Value)) : standalonePages)
            {
                treeItems.Add(new CodexPageTreeItem(page));
            }

            return InitCollection(treeItems);
        }

        private bool MatchesSearch(BioCodexEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(SearchText))
            {
                return true;
            }

            return entry.Title.ToString().Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                   || entry.Description.ToString().Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                   || ResolveStrRefText(entry.Title).Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                   || ResolveStrRefText(entry.Description).Contains(SearchText, StringComparison.OrdinalIgnoreCase);
        }

        private string ResolveStrRefText(int strRef)
        {
            return package != null
                ? StripWrappingQuotes(GlobalFindStrRefbyID(strRef, package))
                : string.Empty;
        }

        private void RefreshCodexPageAudioState()
        {
            OnPropertyChanged(nameof(CanAddCodexPageAudio));
            OnPropertyChanged(nameof(HasCodexPageAudioReference));
            CurrentCodexPageWwiseEventDisplay = GetCurrentCodexPageWwiseEventDisplay();
        }

        private string GetCurrentCodexPageWwiseEventDisplay()
        {
            if (SelectedCodexPage.Value == null)
            {
                return string.Empty;
            }

            if (SelectedCodexPage.Value.CodexSound == 0)
            {
                return "None";
            }

            if (package == null || !package.TryGetEntry(SelectedCodexPage.Value.CodexSound, out var entry) || entry == null)
            {
                return $"Unresolved ({SelectedCodexPage.Value.CodexSound})";
            }

            return $"{entry.InstancedFullPath} ({(entry is ImportEntry ? "Import" : "Export")} {entry.UIndex})";
        }

        private string GetCodexPageAudioBaseName()
        {
            if (SelectedCodexPage.Value == null)
            {
                return null;
            }

            var strRef = SelectedCodexPage.Value.Description > 0
                ? SelectedCodexPage.Value.Description
                : SelectedCodexPage.Value.Title > 0
                    ? SelectedCodexPage.Value.Title
                    : SelectedCodexPage.Key;

            return $"VO_{strRef}";
        }

        private string GetPreferredCodexAudioPackageName()
        {
            if (package != null && SelectedCodexPage.Value != null && SelectedCodexPage.Value.CodexSound != 0 && package.TryGetEntry(SelectedCodexPage.Value.CodexSound, out var entry) && entry != null)
            {
                return entry.GetRootName();
            }

            return "audio";
        }

        private void StopCodexAudioPlayback()
        {
            if (_codexAudioPlayer != null)
            {
                _codexAudioPlayer.PlaybackStopType = SoundpanelAudioPlayer.PlaybackStopTypes.PlaybackStoppedByUser;
                _codexAudioPlayer.Stop();
                _codexAudioPlayer.Dispose();
                _codexAudioPlayer = null;
            }

            RefreshCodexPageAudioState();
        }

        private bool TryResolveCodexPageWwiseEvent(out ExportEntry wwiseEventExport, out IMEPackage sourcePackage, out bool releasePackage, out string errorMessage)
        {
            wwiseEventExport = null;
            sourcePackage = null;
            releasePackage = false;
            errorMessage = null;

            if (package == null || SelectedCodexPage.Value == null)
            {
                errorMessage = "No codex page is selected.";
                return false;
            }

            if (SelectedCodexPage.Value.CodexSound == 0)
            {
                errorMessage = "This codex page does not currently reference a WwiseEvent.";
                return false;
            }

            if (!package.TryGetEntry(SelectedCodexPage.Value.CodexSound, out var entry) || entry == null)
            {
                errorMessage = $"Could not resolve codex sound reference {SelectedCodexPage.Value.CodexSound}.";
                return false;
            }

            if (entry.ClassName != "WwiseEvent")
            {
                errorMessage = $"Codex sound points to {entry.ClassName}, not a WwiseEvent.";
                return false;
            }

            if (entry is ExportEntry export)
            {
                wwiseEventExport = export;
                sourcePackage = export.FileRef;
                return true;
            }

            if (entry is ImportEntry import && TryOpenImportSourcePackage(import, out sourcePackage, out var sourcePath))
            {
                releasePackage = true;
                wwiseEventExport = sourcePackage.FindExport(import.InstancedFullPath, "WwiseEvent");
                if (wwiseEventExport != null)
                {
                    return true;
                }

                sourcePackage.Release();
                sourcePackage = null;
                releasePackage = false;
                errorMessage = $"Found source package '{sourcePath}', but could not resolve export '{import.InstancedFullPath}'.";
                return false;
            }

            errorMessage = $"Could not locate source package for imported WwiseEvent '{entry.InstancedFullPath}'.";
            return false;
        }

        private bool TryResolveCodexPageWwiseStream(out ExportEntry wwiseStreamExport, out IMEPackage sourcePackage, out bool releasePackage, out string errorMessage)
        {
            wwiseStreamExport = null;
            if (!TryResolveCodexPageWwiseEvent(out var wwiseEventExport, out sourcePackage, out releasePackage, out errorMessage))
            {
                return false;
            }

            if (TryGetLinkedWwiseStreamExport(wwiseEventExport, out wwiseStreamExport))
            {
                return true;
            }

            if (releasePackage)
            {
                sourcePackage.Release();
                sourcePackage = null;
                releasePackage = false;
            }

            errorMessage = $"Could not resolve a linked WwiseStream for '{wwiseEventExport.InstancedFullPath}'.";
            return false;
        }

        private static bool TryGetLinkedWwiseStreamExport(ExportEntry wwiseEventExport, out ExportEntry wwiseStreamExport)
        {
            wwiseStreamExport = null;

            if (wwiseEventExport == null)
            {
                return false;
            }

            int streamUIndex = 0;
            if (wwiseEventExport.Game.IsGame3())
            {
                var eventBinary = wwiseEventExport.GetBinaryData<WwiseEventBinary>();
                streamUIndex = eventBinary.Links?.SelectMany(link => link.WwiseStreams ?? Enumerable.Empty<int>()).FirstOrDefault() ?? 0;
            }
            else if (wwiseEventExport.Game is MEGame.LE2 or MEGame.ME2)
            {
                var references = wwiseEventExport.GetProperty<ArrayProperty<StructProperty>>("References");
                var streams = references?.FirstOrDefault()?.Properties.GetProp<StructProperty>("Relationships")?.Properties.GetProp<ArrayProperty<ObjectProperty>>("Streams");
                streamUIndex = streams?.FirstOrDefault()?.Value ?? 0;
            }

            return streamUIndex != 0
                   && wwiseEventExport.FileRef.TryGetUExport(streamUIndex, out wwiseStreamExport)
                   && wwiseStreamExport.ClassName == "WwiseStream";
        }

        private bool TryOpenImportSourcePackage(ImportEntry importEntry, out IMEPackage sourcePackage, out string sourcePath)
        {
            sourcePackage = null;
            sourcePath = FindImportSourcePackagePath(importEntry);
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return false;
            }

            try
            {
                sourcePackage = MEPackageHandler.OpenMEPackage(sourcePath);
                return true;
            }
            catch
            {
                sourcePackage?.Release();
                sourcePackage = null;
                return false;
            }
        }

        private string FindImportSourcePackagePath(ImportEntry importEntry)
        {
            if (package == null || importEntry == null)
            {
                return null;
            }

            var rootName = importEntry.GetRootName();
            if (string.IsNullOrWhiteSpace(rootName))
            {
                return null;
            }

            var extensions = new[] { Path.GetExtension(package.FilePath), ".pcc", ".upk", ".u" }
                .Where(ext => !string.IsNullOrWhiteSpace(ext))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var searchRoots = new[]
            {
                Path.GetDirectoryName(package.FilePath),
                MEDirectories.GetCookedPath(package.Game),
                MEDirectories.GetDLCPath(package.Game)
            }
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

            foreach (var root in searchRoots)
            {
                foreach (var ext in extensions)
                {
                    var candidate = Path.Combine(root, rootName + ext);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            foreach (var root in searchRoots)
            {
                foreach (var ext in extensions)
                {
                    try
                    {
                        var candidate = Directory.EnumerateFiles(root, rootName + ext, SearchOption.AllDirectories).FirstOrDefault();
                        if (candidate != null)
                        {
                            return candidate;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return null;
        }

        private async Task AddCodexPageAudio()
        {
            if (!CanAddCodexPageAudio)
            {
                return;
            }

            var wavDialog = new OpenFileDialog
            {
                Filter = "Wave PCM|*.wav",
                Title = "Select WAV file for codex page audio",
                CustomPlaces = AppDirectories.GameCustomPlaces
            };
            if (wavDialog.ShowDialog() != true)
            {
                return;
            }

            var audioBaseName = GetCodexPageAudioBaseName();
            var audioPackageName = GetPreferredCodexAudioPackageName();
            var tempDir = Path.Combine(Path.GetTempPath(), $"LEX_CodexPageAudio_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var stagedWavPath = Path.Combine(tempDir, audioBaseName + ".wav");
            File.Copy(wavDialog.FileName, stagedWavPath, true);

            try
            {
                var dialog = new BulkAudioImportDialog(
                    package,
                    bankPackageName: audioPackageName,
                    initialWavFiles: new[] { stagedWavPath },
                    initialBankName: audioPackageName,
                    isDialogueBank: true,
                    generateGenderedEvents: false)
                {
                    Owner = Window.GetWindow(this)
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                var eventName = $"{audioBaseName}_Play";
                var eventPath = $"{audioPackageName}.{eventName}";
                var wwiseEventExport = package.FindExport(eventPath, "WwiseEvent")
                    ?? package.Exports.FirstOrDefault(exp => exp.ClassName == "WwiseEvent" && exp.ObjectNameString.Equals(eventName, StringComparison.OrdinalIgnoreCase));

                if (wwiseEventExport == null)
                {
                    MessageBox.Show($"Imported audio completed, but the new WwiseEvent '{eventName}' could not be found.", "Codex Audio", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SelectedCodexPage.Value.CodexSound = wwiseEventExport.UIndex;
                RefreshCodexPageAudioState();
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
                catch
                {
                }
            }
        }

        private async Task ReplaceCodexPageAudio()
        {
            if (!HasCodexPageAudioReference)
            {
                return;
            }

            if (!TryResolveCodexPageWwiseStream(out var wwiseStreamExport, out var sourcePackage, out var releasePackage, out var errorMessage))
            {
                MessageBox.Show(errorMessage, "Codex Audio", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                if (!ReferenceEquals(sourcePackage, package))
                {
                    MessageBox.Show("Replace Audio is only supported for codex audio that already points to a local WwiseStream export. Use Add Audio to create a local override first.", "Codex Audio", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (!WwiseCliHandler.CheckWwisePathForGame(package.Game))
                {
                    return;
                }

                var openFileDialog = new OpenFileDialog
                {
                    Filter = "Wave PCM|*.wav",
                    CustomPlaces = AppDirectories.GameCustomPlaces,
                    Title = "Select replacement WAV file"
                };
                if (openFileDialog.ShowDialog() != true)
                {
                    return;
                }

                var replaceDialog = new SoundReplaceOptionsDialog(Window.GetWindow(this), package.Game.IsGame3(), package.Game, wwiseStreamExport.GetProperty<NameProperty>("Filename")?.Value);
                if (replaceDialog.ShowDialog() != true)
                {
                    return;
                }

                Mouse.OverrideCursor = Cursors.Wait;
                try
                {
                    var convertedFile = await WwiseCliHandler.RunWwiseConversion(package.Game, openFileDialog.FileName, replaceDialog.ChosenSettings);
                    ReplaceCodexPageAudioFromEncodedFile(wwiseStreamExport, convertedFile, replaceDialog.ChosenSettings);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                }
            }
            finally
            {
                if (releasePackage)
                {
                    sourcePackage.Release();
                }
            }
        }

        private static void ReplaceCodexPageAudioFromEncodedFile(ExportEntry wwiseStreamExport, string filePath, WwiseConversionSettingsPackage conversionSettings)
        {
            var wwiseStream = wwiseStreamExport.GetBinaryData<WwiseStreamBinary>();
            wwiseStream.ImportFromFile(filePath, wwiseStream.GetPathToAFC(conversionSettings?.DestinationAFCFile), conversionSettings?.DestinationAFCFile);
            wwiseStreamExport.WriteBinary(wwiseStream);

            if (conversionSettings?.UpdateReferencedEvents == true)
            {
                var audioInfo = wwiseStream.GetAudioInfo();
                if (audioInfo != null)
                {
                    WwiseHelper.UpdateReferencedWwiseEventLengths(wwiseStreamExport, (float)audioInfo.GetLength().TotalMilliseconds);
                }
            }
        }

        private void PlayCodexPageAudio()
        {
            if (!TryResolveCodexPageWwiseStream(out var wwiseStreamExport, out var sourcePackage, out var releasePackage, out var errorMessage))
            {
                MessageBox.Show(errorMessage, "Codex Audio", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var stream = wwiseStreamExport.GetBinaryData<WwiseStreamBinary>().CreateWaveStream();
                if (stream == null)
                {
                    MessageBox.Show("Could not decode the linked WwiseStream.", "Codex Audio", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                StopCodexAudioPlayback();
                _codexAudioPlayer = new SoundpanelAudioPlayer(stream, 1f);
                _codexAudioPlayer.PlaybackStopped += () =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _codexAudioPlayer?.Dispose();
                        _codexAudioPlayer = null;
                        RefreshCodexPageAudioState();
                    });
                };
                _codexAudioPlayer.Play(NAudio.Wave.PlaybackState.Stopped, 1f);
                RefreshCodexPageAudioState();
            }
            finally
            {
                if (releasePackage)
                {
                    sourcePackage.Release();
                }
            }
        }

        private void RemoveCodexPageAudio()
        {
            if (!HasCodexPageAudioReference)
            {
                return;
            }

            StopCodexAudioPlayback();
            SelectedCodexPage.Value.CodexSound = 0;
            RefreshCodexPageAudioState();
        }

        private void ExtractCodexPageAudio()
        {
            if (!TryResolveCodexPageWwiseStream(out var wwiseStreamExport, out var sourcePackage, out var releasePackage, out var errorMessage))
            {
                MessageBox.Show(errorMessage, "Codex Audio", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var saveFileDialog = new SaveFileDialog
                {
                    Filter = "Wave PCM File|*.wav",
                    FileName = wwiseStreamExport.ObjectNameString + ".wav"
                };
                if (saveFileDialog.ShowDialog() != true)
                {
                    return;
                }

                var wavPath = wwiseStreamExport.GetBinaryData<WwiseStreamBinary>().CreateWave();
                if (wavPath == null || !File.Exists(wavPath))
                {
                    MessageBox.Show("Could not extract the linked WwiseStream.", "Codex Audio", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                File.Copy(wavPath, saveFileDialog.FileName, true);
            }
            finally
            {
                if (releasePackage)
                {
                    sourcePackage.Release();
                }
            }
        }

        private static string StripWrappingQuotes(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2)
            {
                return text ?? string.Empty;
            }

            return (text[0], text[^1]) switch
            {
                ('"', '"') => text[1..^1],
                ('\'', '\'') => text[1..^1],
                ('“', '”') => text[1..^1],
                ('‘', '’') => text[1..^1],
                _ => text
            };
        }

        private CodexSectionTreeItem FindSectionTreeItem(int sectionId)
        {
            return EnumerateCodexTreeItems().OfType<CodexSectionTreeItem>().FirstOrDefault(item => item.CodexSection.Key == sectionId);
        }

        private CodexPageTreeItem FindPageTreeItem(int pageId)
        {
            foreach (var sectionItem in EnumerateCodexTreeItems().OfType<CodexSectionTreeItem>())
            {
                var pageItem = sectionItem.Pages.FirstOrDefault(item => item.CodexPage.Key == pageId);
                if (pageItem != null)
                {
                    return pageItem;
                }
            }

            return EnumerateCodexTreeItems().OfType<CodexPageTreeItem>().FirstOrDefault(item => item.CodexPage.Key == pageId);
        }

        private IEnumerable<object> EnumerateCodexTreeItems()
        {
            foreach (var treeItem in PrimaryCodexTreeItems ?? Enumerable.Empty<object>())
            {
                yield return treeItem;
            }

            foreach (var treeItem in SecondaryCodexTreeItems ?? Enumerable.Empty<object>())
            {
                yield return treeItem;
            }
        }

        private void SelectTabForTreeItem(CodexTreeItemBase treeItem)
        {
            SelectedCodexTreeTabIndex = treeItem switch
            {
                CodexSectionTreeItem sectionItem => sectionItem.CodexSection.Value.IsPrimary ? 0 : 1,
                CodexPageTreeItem { Parent: not null } pageItem => pageItem.Parent.CodexSection.Value.IsPrimary ? 0 : 1,
                _ => 1
            };
        }

        private System.Windows.Controls.TreeView GetSelectedCodexTreeView()
        {
            return SelectedCodexTreeTabIndex == 0 ? PrimaryCodexTreeView : SecondaryCodexTreeView;
        }

        private static void SelectTreeItem(CodexTreeItemBase treeItem)
        {
            if (treeItem == null)
            {
                return;
            }

            if (treeItem.Parent != null)
            {
                treeItem.Parent.IsExpanded = true;
            }

            treeItem.IsSelected = true;
        }

        private void RefreshSelectedText()
        {
            if (_isRefreshingSelectedText)
            {
                return;
            }

            _isRefreshingSelectedText = true;

            try
            {
                if (package != null)
                {
                    txt_cdxPgeDesc.Text = ResolveStrRefText(SelectedCodexPage.Value?.Description ?? 0);
                    txt_cdxPgeTitle.Text = ResolveStrRefText(SelectedCodexPage.Value?.Title ?? 0);
                    txt_cdxSecDesc.Text = ResolveStrRefText(SelectedCodexSection.Value?.Description ?? 0);
                    txt_cdxSecTitle.Text = ResolveStrRefText(SelectedCodexSection.Value?.Title ?? 0);

                    if (SelectedCodexPage.Value != null)
                    {
                        SelectedCodexPage.Value.TitleAsString = txt_cdxPgeTitle.Text;
                    }

                    if (SelectedCodexSection.Value != null)
                    {
                        SelectedCodexSection.Value.TitleAsString = txt_cdxSecTitle.Text;
                    }

                    return;
                }

                txt_cdxPgeDesc.Text = string.Empty;
                txt_cdxPgeTitle.Text = string.Empty;
                txt_cdxSecDesc.Text = string.Empty;
                txt_cdxSecTitle.Text = string.Empty;
            }
            finally
            {
                _isRefreshingSelectedText = false;
            }
        }

        private void ChangeCodexPageId_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            ChangeCodexPageId();
        }

        private void CopyCodexPage_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            CopyCodexPage();
        }

        private void RemoveCodexPage_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            RemoveCodexPage();
        }

        private void ChangeCodexSectionId_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            ChangeCodexSectionId();
        }

        private void CopyCodexSection_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            CopyCodexSection();
        }

        private void RemoveCodexSection_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            removeCodexSection();
        }

        private void AddCodexSection_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            addCodexSection();
        }

        private void AddCodexPage_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            AddCodexPage();
        }

        private void RemoveSelectedCodexItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (SelectedCodexPage.Value != null)
            {
                RemoveCodexPage();
                return;
            }

            if (SelectedCodexSection.Value != null)
            {
                removeCodexSection();
            }
        }

        private void CodexTreeView_SelectedItemChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<object> e)
        {
            if (_isRefreshingCodexHierarchy)
            {
                return;
            }

            switch (e.NewValue)
            {
                case CodexPageTreeItem pageItem:
                    SelectedCodexSection = default;
                    SelectedCodexPage = pageItem.CodexPage;
                    break;
                case CodexSectionTreeItem sectionItem:
                    SelectedCodexPage = default;
                    SelectedCodexSection = sectionItem.CodexSection;
                    break;
                default:
                    SelectedCodexPage = default;
                    SelectedCodexSection = default;
                    break;
            }

            RefreshSelectedText();
        }

        private void CodexPageSection_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<object> e)
        {
            if (_isRefreshingCodexHierarchy)
            {
                return;
            }

            RefreshCodexHierarchy();
        }

        private void CodexSearchBox_TextChanged(SearchBox sender, string newText)
        {
            SearchText = newText;
        }

        private void CodexSectionIsPrimary_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_isRefreshingCodexHierarchy)
            {
                return;
            }

            RefreshCodexHierarchy();
        }

        private void txt_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<object> e)
        {
            RefreshSelectedText();
            RefreshCodexHierarchy();
        }

        private async void AddCodexPageAudio_Click(object sender, RoutedEventArgs e)
        {
            await AddCodexPageAudio();
        }

        private void PlayCodexPageAudio_Click(object sender, RoutedEventArgs e)
        {
            PlayCodexPageAudio();
        }

        private async void ReplaceCodexPageAudio_Click(object sender, RoutedEventArgs e)
        {
            await ReplaceCodexPageAudio();
        }

        private void ExtractCodexPageAudio_Click(object sender, RoutedEventArgs e)
        {
            ExtractCodexPageAudio();
        }

        private void StopCodexPageAudio_Click(object sender, RoutedEventArgs e)
        {
            StopCodexAudioPlayback();
        }

        private void RemoveCodexPageAudio_Click(object sender, RoutedEventArgs e)
        {
            RemoveCodexPageAudio();
        }

        private void CodexMapView_Unloaded(object sender, RoutedEventArgs e)
        {
            StopCodexAudioPlayback();
        }
    }
}
