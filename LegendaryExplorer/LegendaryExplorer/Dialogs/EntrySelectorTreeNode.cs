using System.ComponentModel;
using System.IO;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Interfaces;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Dialogs
{
    /// <summary>A selectable entry or a navigation branch in the entry selector's package tree.</summary>
    public sealed class EntrySelectorTreeNode : NotifyPropertyChangedBase, ITreeItem
    {
        private readonly string label;

        internal EntrySelectorTreeNode(object item, TreeViewEntry metadata = null, string label = null)
        {
            Item = item;
            Metadata = metadata;
            this.label = label;
            if (metadata is not null)
            {
                metadata.PropertyChanged += Metadata_PropertyChanged;
            }
        }

        public object Item { get; }
        public TreeViewEntry Metadata { get; }
        public IEntry Entry => Metadata?.Entry;
        public string DisplayName => Metadata?.DisplayName ?? label ?? Item?.ToString();
        public EntrySelectorTreeNode Parent { get; internal set; }
        ITreeItem ITreeItem.Parent { get => Parent; set => Parent = (EntrySelectorTreeNode)value; }
        public ObservableCollectionExtended<EntrySelectorTreeNode> Children { get; } = new();

        private bool canSelect;
        public bool CanSelect { get => canSelect; internal set => SetProperty(ref canSelect, value); }

        private bool isExpanded;
        public bool IsExpanded { get => isExpanded; set => SetProperty(ref isExpanded, value); }

        private bool isSelected;
        public bool IsSelected { get => isSelected; set => SetProperty(ref isSelected, value); }

        private void Metadata_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TreeViewEntry.DisplayName))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }

        internal void DetachMetadata()
        {
            if (Metadata is not null)
            {
                Metadata.PropertyChanged -= Metadata_PropertyChanged;
            }
        }

        public void PrintPretty(string indent, TextWriter writer, bool last, ExportEntry associatedExport)
        {
            writer.WriteLine($"{indent}{DisplayName}");
            foreach (EntrySelectorTreeNode child in Children)
            {
                child.PrintPretty(indent + "  ", writer, false, associatedExport);
            }
        }
    }
}
