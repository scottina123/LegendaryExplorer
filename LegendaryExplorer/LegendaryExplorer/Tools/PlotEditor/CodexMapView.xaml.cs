using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using Gammtek.Conduit.MassEffect3.SFXGame.CodexMap;
using LegendaryExplorer.Tools.PlotEditor.Dialogs;
using LegendaryExplorer.SharedUI.Controls;
using static LegendaryExplorer.Tools.TlkManagerNS.TLKManagerWPF;
using LegendaryExplorer.Misc;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Packages;

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

        public abstract class CodexTreeItemBase : NotifyPropertyChangedBase
        {
            private bool _isExpanded;
            private bool _isSelected;

            protected CodexTreeItemBase(CodexSectionTreeItem parent = null)
            {
                Parent = parent;
            }

            public CodexSectionTreeItem Parent { get; }

            public bool IsExpanded
            {
                get => _isExpanded;
                set => SetProperty(ref _isExpanded, value);
            }

            public bool IsSelected
            {
                get => _isSelected;
                set => SetProperty(ref _isSelected, value);
            }
        }

        public sealed class CodexSectionTreeItem : CodexTreeItemBase
        {
            public CodexSectionTreeItem(KeyValuePair<int, BioCodexSection> codexSection)
            {
                CodexSection = codexSection;
                Pages = InitCollection<CodexPageTreeItem>();
            }

            public KeyValuePair<int, BioCodexSection> CodexSection { get; }

            public ObservableCollection<CodexPageTreeItem> Pages { get; }
        }

        public sealed class CodexPageTreeItem : CodexTreeItemBase
        {
            public CodexPageTreeItem(KeyValuePair<int, BioCodexPage> codexPage, CodexSectionTreeItem parent = null)
                : base(parent)
            {
                CodexPage = codexPage;
            }

            public KeyValuePair<int, BioCodexPage> CodexPage { get; }
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
    }
}
