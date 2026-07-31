using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Misc.AppSettings;
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
    public class InterpNameReplacement
    {
        public string Find { get; set; } = "";
        public string Replace { get; set; } = "";
    }

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
        public ObservableCollectionExtended<string> FindNames { get; } = new();
        public ObservableCollectionExtended<InterpNameReplacement> NameReplacements { get; } = new();
        public bool RememberReplacements { get; set; } = Settings.DialogueEditor_RememberBulkInterpReplacements;
        public bool ChangesApplied { get; private set; }

        private const char ReplacementSeparator = '\u001F';
        private readonly ExportEntry _interpData;
        private readonly IMEPackage _pcc;

        public BulkInterpEditorDialog(Window owner, DialogueNodeExtended dialogueNode, ConversationExtended conversation)
        {
            _interpData = dialogueNode?.InterpData;
            _pcc = conversation.Export.FileRef;

            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
            Owner = owner;

            LoadNameReplacements();
            LoadInterpGroups();
        }

        public BulkInterpEditorDialog(Window owner, ExportEntry interpData)
        {
            _interpData = interpData;
            _pcc = interpData.FileRef;

            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
            Owner = owner;

            LoadNameReplacements();
            LoadInterpGroups();
        }

        private void LoadNameReplacements()
        {
            if (Settings.DialogueEditor_RememberBulkInterpReplacements)
            {
                foreach (string encodedReplacement in Settings.DialogueEditor_BulkInterpReplacements ?? [])
                {
                    int separatorIndex = encodedReplacement?.IndexOf(ReplacementSeparator) ?? -1;
                    if (separatorIndex >= 0)
                    {
                        NameReplacements.Add(new InterpNameReplacement
                        {
                            Find = encodedReplacement[..separatorIndex],
                            Replace = encodedReplacement[(separatorIndex + 1)..]
                        });
                    }
                }
            }

            if (NameReplacements.Count == 0)
            {
                NameReplacements.Add(new InterpNameReplacement());
            }
        }

        private void PersistNameReplacements()
        {
            Settings.DialogueEditor_RememberBulkInterpReplacements = RememberReplacements;
            Settings.DialogueEditor_BulkInterpReplacements = Settings.DialogueEditor_RememberBulkInterpReplacements
                ? NameReplacements.Select(row => $"{row.Find ?? string.Empty}{ReplacementSeparator}{row.Replace ?? string.Empty}").ToList()
                : [];
        }

        private void AddReplacement_Click(object sender, RoutedEventArgs e)
        {
            NameReplacements.Add(new InterpNameReplacement());
        }

        private void RemoveReplacement_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: InterpNameReplacement replacement })
            {
                NameReplacements.Remove(replacement);
            }

            if (NameReplacements.Count == 0)
            {
                NameReplacements.Add(new InterpNameReplacement());
            }
        }

        private void BulkInterpEditorDialog_Closing(object sender, CancelEventArgs e)
        {
            PersistNameReplacements();
        }

        /// <summary>
        /// Loads all InterpGroups and tracks from the InterpData.
        /// </summary>
        private void LoadInterpGroups()
        {
            InterpGroupItems.ClearEx();
            FindNames.ClearEx();

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

            RebuildFindNames();
        }

        private void RebuildFindNames()
        {
            FindNames.ClearEx();

            foreach (string name in InterpGroupItems
                         .SelectMany(item => new[] { item.GroupName, item.SFXFindActor, item.TrackFindActor })
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                FindNames.Add(name);
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

        public static int ApplyNameReplacementsToInterpData(ExportEntry interpData, IReadOnlyDictionary<string, string> replacements)
        {
            if (interpData == null || replacements == null || replacements.Count == 0)
            {
                return 0;
            }

            IMEPackage pcc = interpData.FileRef;
            if (pcc == null)
            {
                return 0;
            }

            var normalizedReplacements = replacements
                .Where(kvp => !string.IsNullOrEmpty(kvp.Key))
                .ToList();
            if (normalizedReplacements.Count == 0)
            {
                return 0;
            }

            int changesApplied = 0;
            ExportEntry seqActInterp = FindSeqActInterpForInterpData(interpData);
            foreach (ExportEntry export in pcc.Exports.Where(exp => exp == interpData || exp.IsDescendantOf(interpData)))
            {
                var props = export.GetProperties();
                string originalGroupName = export.ClassName == "InterpGroup"
                    ? props.GetProp<NameProperty>("GroupName")?.Value.Instanced
                    : null;

                int exportChanges = ApplyNameReplacementsToProperties(props, normalizedReplacements);
                if (exportChanges == 0)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(originalGroupName))
                {
                    string groupName = props.GetProp<NameProperty>("GroupName")?.Value.Instanced;
                    if (groupName != originalGroupName)
                    {
                        UpdateSeqActInterpVariableLink(seqActInterp, originalGroupName, groupName);
                    }
                }

                export.WriteProperties(props);
                changesApplied += exportChanges;
            }

            return changesApplied;
        }

        private static ExportEntry FindSeqActInterpForInterpData(ExportEntry interpData)
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

        private static int ApplyNameReplacementsToProperties(PropertyCollection properties, IReadOnlyList<KeyValuePair<string, string>> replacements)
        {
            int changesApplied = 0;
            foreach (Property property in properties)
            {
                changesApplied += ApplyNameReplacementsToProperty(property, replacements);
            }

            return changesApplied;
        }

        private static int ApplyNameReplacementsToProperty(Property property, IReadOnlyList<KeyValuePair<string, string>> replacements)
        {
            switch (property)
            {
                case NameProperty nameProperty:
                    string originalValue = nameProperty.Value.Instanced;
                    if (TryApplyNameReplacement(originalValue, replacements, out string newValue))
                    {
                        nameProperty.Value = newValue;
                        return 1;
                    }

                    return 0;
                case StructProperty structProperty:
                    return ApplyNameReplacementsToProperties(structProperty.Properties, replacements);
                case ArrayPropertyBase arrayProperty:
                    int changesApplied = 0;
                    foreach (Property arrayValue in arrayProperty.Properties)
                    {
                        changesApplied += ApplyNameReplacementsToProperty(arrayValue, replacements);
                    }

                    return changesApplied;
                default:
                    return 0;
            }
        }

        private static bool TryApplyNameReplacement(string value, IReadOnlyList<KeyValuePair<string, string>> replacements, out string newValue)
        {
            newValue = value;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (var replacement in replacements)
            {
                newValue = newValue.Replace(replacement.Key, replacement.Value ?? string.Empty);
            }

            return newValue != value;
        }

        private static void UpdateSeqActInterpVariableLink(ExportEntry seqActInterp, string originalGroupName, string groupName)
        {
            if (seqActInterp == null || string.IsNullOrEmpty(originalGroupName))
            {
                return;
            }

            var varLinksProp = seqActInterp.GetProperty<ArrayProperty<StructProperty>>("VariableLinks");
            if (varLinksProp == null)
            {
                return;
            }

            bool modified = false;
            foreach (var varLink in varLinksProp)
            {
                var linkDesc = varLink.GetProp<StrProperty>("LinkDesc");
                if (linkDesc != null && linkDesc.Value == originalGroupName)
                {
                    linkDesc.Value = groupName;
                    modified = true;
                }
            }

            if (modified)
            {
                seqActInterp.WriteProperty(varLinksProp);
            }
        }

        /// <summary>
        /// Applies bulk find/replace and then writes all changes to the package.
        /// </summary>
        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            var replacements = NameReplacements
                .Where(row => !string.IsNullOrEmpty(row.Find))
                .ToList();

            if (replacements.Count > 0)
            {
                foreach (var item in InterpGroupItems)
                {
                    if (item.Type == InterpGroupItem.ItemType.InterpGroup)
                    {
                        if (ReplaceGroupName.IsChecked == true && !string.IsNullOrEmpty(item.GroupName))
                        {
                            string newValue = ApplyReplacements(item.GroupName, replacements);
                            if (newValue != item.GroupName)
                            {
                                item.GroupName = newValue;
                            }
                        }

                        if (ReplaceSFXFindActor.IsChecked == true && !string.IsNullOrEmpty(item.SFXFindActor))
                        {
                            string newValue = ApplyReplacements(item.SFXFindActor, replacements);
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
                            string newValue = ApplyReplacements(item.TrackFindActor, replacements);
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
                ChangesApplied = true;
                RebuildFindNames();
                MessageBox.Show($"Applied {changesApplied} change(s).", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private static string ApplyReplacements(string value, IReadOnlyList<InterpNameReplacement> replacements)
        {
            foreach (InterpNameReplacement replacement in replacements)
            {
                value = value.Replace(replacement.Find, replacement.Replace ?? string.Empty);
            }

            return value;
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
