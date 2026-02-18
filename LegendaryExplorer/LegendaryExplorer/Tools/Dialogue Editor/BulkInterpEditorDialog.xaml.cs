using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.DialogueEditor
{
    /// <summary>
    /// A data item representing an InterpGroup or Track and its editable properties.
    /// </summary>
    public class InterpGroupItem : INotifyPropertyChanged
    {
        /// <summary>
        /// The type of item this represents.
        /// </summary>
        public enum ItemType
        {
            InterpGroup,
            Track
        }

        public ItemType Type { get; set; }
        public ExportEntry Export { get; set; }
        public ExportEntry SeqActInterp { get; set; }
        public ExportEntry ParentInterpGroup { get; set; }

        public string ExportName => Export?.ObjectName.Instanced ?? "Unknown";
        public string ExportClass => Export?.ClassName ?? "";

        private string _groupName;
        public string GroupName
        {
            get => _groupName;
            set
            {
                if (_groupName != value)
                {
                    _groupName = value;
                    IsModified = true;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsModified));
                }
            }
        }

        private string _originalGroupName;
        public string OriginalGroupName
        {
            get => _originalGroupName;
            set => _originalGroupName = value;
        }

        private string _sfxFindActor;
        public string SFXFindActor
        {
            get => _sfxFindActor;
            set
            {
                if (_sfxFindActor != value)
                {
                    _sfxFindActor = value;
                    IsModified = true;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsModified));
                }
            }
        }

        private string _originalSFXFindActor;
        public string OriginalSFXFindActor
        {
            get => _originalSFXFindActor;
            set => _originalSFXFindActor = value;
        }

        private string _trackFindActor;
        public string TrackFindActor
        {
            get => _trackFindActor;
            set
            {
                if (_trackFindActor != value)
                {
                    _trackFindActor = value;
                    IsModified = true;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsModified));
                }
            }
        }

        private string _originalTrackFindActor;
        public string OriginalTrackFindActor
        {
            get => _originalTrackFindActor;
            set => _originalTrackFindActor = value;
        }

        public bool IsModified { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Dialog for bulk editing InterpGroup properties (GroupName, m_nmSFXFindActor, m_nmFindActor)
    /// in an InterpData.
    /// </summary>
    public partial class BulkInterpEditorDialog : Window
    {
        public ObservableCollectionExtended<InterpGroupItem> InterpGroupItems { get; } = new();

        private readonly ExportEntry _interpData;
        private readonly IMEPackage _pcc;

        public BulkInterpEditorDialog(Window owner, DialogueNodeExtended dialogueNode, ConversationExtended conversation)
        {
            _interpData = dialogueNode?.InterpData;
            _pcc = conversation.Export.FileRef;

            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
            Owner = owner;

            LoadInterpGroups();
        }

        public BulkInterpEditorDialog(Window owner, ExportEntry interpData)
        {
            _interpData = interpData;
            _pcc = interpData.FileRef;

            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
            Owner = owner;

            LoadInterpGroups();
        }

        /// <summary>
        /// Loads all InterpGroups and tracks from the InterpData.
        /// </summary>
        private void LoadInterpGroups()
        {
            InterpGroupItems.ClearEx();

            if (_interpData == null)
            {
                return;
            }

            ExportEntry interpData = _interpData;
            ExportEntry seqActInterp = FindSeqActInterp(interpData);

            // Get the InterpGroups from the InterpData
            var interpGroups = interpData.GetProperty<ArrayProperty<ObjectProperty>>("InterpGroups");
            if (interpGroups == null) return;

            foreach (var groupRef in interpGroups)
            {
                if (!_pcc.TryGetUExport(groupRef.Value, out ExportEntry interpGroup))
                    continue;

                // Add the InterpGroup item
                var groupItem = new InterpGroupItem
                {
                    Type = InterpGroupItem.ItemType.InterpGroup,
                    Export = interpGroup,
                    SeqActInterp = seqActInterp
                };

                // Get GroupName
                var groupNameProp = interpGroup.GetProperty<NameProperty>("GroupName");
                groupItem.GroupName = groupNameProp?.Value.Instanced ?? "";
                groupItem.OriginalGroupName = groupItem.GroupName;

                // Get m_nmSFXFindActor (Game 3 specific)
                var sfxFindActorProp = interpGroup.GetProperty<NameProperty>("m_nmSFXFindActor");
                groupItem.SFXFindActor = sfxFindActorProp?.Value.Instanced ?? "";
                groupItem.OriginalSFXFindActor = groupItem.SFXFindActor;

                // Reset the modified flag after initial load
                groupItem.IsModified = false;
                InterpGroupItems.Add(groupItem);

                // Now look for ALL tracks with m_nmFindActor
                var interpTracks = interpGroup.GetProperty<ArrayProperty<ObjectProperty>>("InterpTracks");
                if (interpTracks != null)
                {
                    foreach (var trackRef in interpTracks)
                    {
                        if (!_pcc.TryGetUExport(trackRef.Value, out ExportEntry track))
                            continue;

                        var findActorProp = track.GetProperty<NameProperty>("m_nmFindActor");
                        if (findActorProp != null)
                        {
                            var trackItem = new InterpGroupItem
                            {
                                Type = InterpGroupItem.ItemType.Track,
                                Export = track,
                                ParentInterpGroup = interpGroup,
                                SeqActInterp = seqActInterp,
                                TrackFindActor = findActorProp.Value.Instanced,
                                OriginalTrackFindActor = findActorProp.Value.Instanced,
                                IsModified = false
                            };
                            InterpGroupItems.Add(trackItem);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Finds the SeqAct_Interp that references the given InterpData.
        /// </summary>
        private ExportEntry FindSeqActInterp(ExportEntry interpData)
        {
            var refs = interpData.GetEntriesThatReferenceThisOne();
            foreach (var entry in refs.Keys)
            {
                if (entry.ClassName == "SeqAct_Interp")
                {
                    return entry as ExportEntry;
                }
            }
            return null;
        }

        /// <summary>
        /// Applies bulk find/replace and then writes all changes to the package.
        /// </summary>
        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            // Apply bulk find/replace first if there is text in the find box
            string findText = FindTextBox.Text;
            string replaceText = ReplaceTextBox.Text;

            if (!string.IsNullOrEmpty(findText))
            {
                foreach (var item in InterpGroupItems)
                {
                    if (item.Type == InterpGroupItem.ItemType.InterpGroup)
                    {
                        if (ReplaceGroupName.IsChecked == true && !string.IsNullOrEmpty(item.GroupName))
                        {
                            string newValue = item.GroupName.Replace(findText, replaceText);
                            if (newValue != item.GroupName)
                            {
                                item.GroupName = newValue;
                            }
                        }

                        if (ReplaceSFXFindActor.IsChecked == true && !string.IsNullOrEmpty(item.SFXFindActor))
                        {
                            string newValue = item.SFXFindActor.Replace(findText, replaceText);
                            if (newValue != item.SFXFindActor)
                            {
                                item.SFXFindActor = newValue;
                            }
                        }
                    }
                    else if (item.Type == InterpGroupItem.ItemType.Track)
                    {
                        if (ReplaceTrackFindActor.IsChecked == true && !string.IsNullOrEmpty(item.TrackFindActor))
                        {
                            string newValue = item.TrackFindActor.Replace(findText, replaceText);
                            if (newValue != item.TrackFindActor)
                            {
                                item.TrackFindActor = newValue;
                            }
                        }
                    }
                }
            }

            int changesApplied = 0;

            foreach (var item in InterpGroupItems.Where(i => i.IsModified))
            {
                if (item.Type == InterpGroupItem.ItemType.InterpGroup)
                {
                    var groupProps = item.Export.GetProperties();

                    // Update GroupName
                    if (item.GroupName != item.OriginalGroupName)
                    {
                        if (!string.IsNullOrEmpty(item.GroupName))
                        {
                            groupProps.AddOrReplaceProp(new NameProperty(item.GroupName, "GroupName"));
                        }
                        else
                        {
                            groupProps.RemoveNamedProperty("GroupName");
                        }

                        // Also update the SeqAct_Interp VariableLinks if GroupName changed
                        UpdateSeqActInterpVariableLink(item);

                        changesApplied++;
                    }

                    // Update m_nmSFXFindActor
                    if (item.SFXFindActor != item.OriginalSFXFindActor)
                    {
                        if (!string.IsNullOrEmpty(item.SFXFindActor))
                        {
                            groupProps.AddOrReplaceProp(new NameProperty(item.SFXFindActor, "m_nmSFXFindActor"));
                        }
                        else
                        {
                            groupProps.RemoveNamedProperty("m_nmSFXFindActor");
                        }
                        changesApplied++;
                    }

                    item.Export.WriteProperties(groupProps);
                }
                else if (item.Type == InterpGroupItem.ItemType.Track)
                {
                    // Update m_nmFindActor on the track
                    if (item.TrackFindActor != item.OriginalTrackFindActor)
                    {
                        var trackProps = item.Export.GetProperties();
                        if (!string.IsNullOrEmpty(item.TrackFindActor))
                        {
                            trackProps.AddOrReplaceProp(new NameProperty(item.TrackFindActor, "m_nmFindActor"));
                        }
                        else
                        {
                            trackProps.RemoveNamedProperty("m_nmFindActor");
                        }
                        item.Export.WriteProperties(trackProps);
                        changesApplied++;
                    }
                }
            }

            if (changesApplied > 0)
            {
                MessageBox.Show($"Applied {changesApplied} change(s).", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            DialogResult = true;
            Close();
        }

        /// <summary>
        /// Updates the SeqAct_Interp's VariableLinks to match the new group name.
        /// </summary>
        private void UpdateSeqActInterpVariableLink(InterpGroupItem item)
        {
            if (item.SeqActInterp == null || string.IsNullOrEmpty(item.OriginalGroupName))
                return;

            var varLinksProp = item.SeqActInterp.GetProperty<ArrayProperty<StructProperty>>("VariableLinks");
            if (varLinksProp == null) return;

            bool modified = false;
            foreach (var varLink in varLinksProp)
            {
                var linkDesc = varLink.GetProp<StrProperty>("LinkDesc");
                if (linkDesc != null && linkDesc.Value == item.OriginalGroupName)
                {
                    linkDesc.Value = item.GroupName;
                    modified = true;
                }
            }

            if (modified)
            {
                item.SeqActInterp.WriteProperty(varLinksProp);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
