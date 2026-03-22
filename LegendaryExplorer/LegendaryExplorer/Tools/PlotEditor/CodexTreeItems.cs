using System.Collections.Generic;
using System.Collections.ObjectModel;
using Gammtek.Conduit.MassEffect3.SFXGame.CodexMap;
using LegendaryExplorer.Misc;

namespace LegendaryExplorer.Tools.PlotEditor
{
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
        public CodexSectionTreeItem(KeyValuePair<int, BioCodexSection> codexSection, bool isReadOnly = false, string readOnlyMessage = null)
        {
            CodexSection = codexSection;
            IsReadOnly = isReadOnly;
            ReadOnlyMessage = readOnlyMessage ?? string.Empty;
            Pages = new ObservableCollection<CodexPageTreeItem>();
        }

        public KeyValuePair<int, BioCodexSection> CodexSection { get; }

        public bool IsReadOnly { get; }

        public bool IsEditable => !IsReadOnly;

        public string ReadOnlyMessage { get; }

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
}
