using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.Tools.InterpEditor;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Packages;
using Microsoft.Win32;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Dialogs;

public partial class DialoguePreviewLevelPicker : TrackingNotifyPropertyChangedWindowBase
{
    private bool restoringCacheOptions;
    private readonly List<DialogueCachePreset> compatibleCachePresets = [];
    private DialogueCachePreset selectedCachePreset;

    public sealed record HenchmanChoice(string ActorTag, string DisplayName);

    public sealed class HenchmanSlotSelection
    {
        public CurveEditor3D.DialoguePreviewHenchmanSlot Slot { get; init; }
        public IReadOnlyList<HenchmanChoice> Choices { get; init; }
        public string SelectedHenchmanTag { get; set; }
    }

    private sealed record RecentLevelSetItem(string DisplayName, IReadOnlyList<string> FilePaths)
    {
        public string FileCountText => $"{FilePaths.Count} package{(FilePaths.Count == 1 ? string.Empty : "s")}";
        public string TooltipText => string.Join("\n", FilePaths.Select(Path.GetFileName));
    }

    public ObservableCollection<string> SelectedFiles { get; } = [];
    public ObservableCollection<HenchmanSlotSelection> HenchmanSlots { get; } = [];
    public IReadOnlyList<string> SelectedLevelPaths => SelectedFiles.ToArray();
    public IReadOnlyDictionary<string, string> HenchmanAssignments => HenchmanSlots
        .Where(slot => !string.IsNullOrWhiteSpace(slot.SelectedHenchmanTag))
        .ToDictionary(slot => slot.Slot.SlotTag, slot => slot.SelectedHenchmanTag,
            StringComparer.OrdinalIgnoreCase);
    public DialogueCachePreset SelectedCachePreset => CacheGroupBox.Visibility == Visibility.Visible
                                                      && UseSelectedCacheRadio.IsChecked == true
        ? selectedCachePreset
        : null;
    public string NewCacheLabel => CacheGroupBox.Visibility == Visibility.Visible
                                   && BuildNewCacheRadio.IsChecked == true
                                   && SaveNewCacheCheckBox.IsChecked == true
        ? NewCacheLabelTextBox.Text?.Trim()
        : null;
    public CurveEditor3D.DialoguePreviewPlayerSelection PlayerSelection
    {
        get
        {
            string genderName = (PlayerGenderComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
            return CurveEditor3D.DialoguePreviewPlayerSelection.ForGender(
                Enum.TryParse(genderName, out CurveEditor3D.DialoguePreviewPlayerGender gender)
                    ? gender
                    : CurveEditor3D.DialoguePreviewPlayerGender.Female);
        }
    }

    public DialoguePreviewLevelPicker() : this(MEGame.Unknown, null, null, false)
    {
    }

    public DialoguePreviewLevelPicker(MEGame game, ConversationExtended conversation,
        DialogueNodeExtended startNode, bool includeCache,
        bool requirePlayerGenderSelection = false) : base("Dialogue Preview Options", false)
    {
        InitializeComponent();
        if (requirePlayerGenderSelection)
        {
            Title = "Scene Actor Generation Options";
            PlayerGenderComboBox.SelectedIndex = -1;
            LoadPreviewButton.Content = "Generate Actors";
        }
        HenchmanChoice[] henchmanChoices = CurveEditor3D.GetDialoguePreviewHenchmanTags()
            .Select(tag => new HenchmanChoice(tag, GetHenchmanDisplayName(tag)))
            .ToArray();
        foreach (CurveEditor3D.DialoguePreviewHenchmanSlot slot in
                 CurveEditor3D.GetDialoguePreviewHenchmanSlots(conversation, startNode, includeCache))
        {
            HenchmanSlots.Add(new HenchmanSlotSelection
            {
                Slot = slot,
                Choices = henchmanChoices,
            });
        }
        HenchmanGroupBox.Visibility = HenchmanSlots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (HenchmanSlots.Count == 0)
        {
            Height = 720;
        }
        RecentSetsList.ItemsSource = CurveEditor3D.GetDialoguePreviewRecentLevelSets()
            .Select(set => new RecentLevelSetItem(set.DisplayName, set.FilePaths))
            .ToArray();
        CacheGroupBox.Visibility = includeCache ? Visibility.Visible : Visibility.Collapsed;
        CacheRow.Height = includeCache ? new GridLength(190) : new GridLength(0);
        if (!includeCache)
        {
            Height = HenchmanSlots.Count > 0 ? 650 : 520;
            MinHeight = 400;
        }
        if (!includeCache || conversation?.Export is null || startNode is null)
        {
            return;
        }

        compatibleCachePresets.AddRange(SavedDialogueCachePresetManager.LoadHeaders()
            .Where(preset => IsCacheIdentityCompatible(preset, conversation, startNode)
                             && HasCompatibleHenchmanAssignments(preset)));
        RefreshCachePresetList();
        NewCacheLabelTextBox.Text = conversation.ConvName;
        if (compatibleCachePresets.Count > 0)
        {
            CachePresetList.SelectedIndex = 0;
            UseSelectedCacheRadio.IsChecked = true;
        }
        else
        {
            BuildNewCacheRadio.IsChecked = true;
        }
    }

    private static string GetHenchmanDisplayName(string actorTag)
    {
        string name = actorTag.StartsWith("hench_", StringComparison.OrdinalIgnoreCase)
            ? actorTag["hench_".Length..]
            : actorTag;
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.Replace('_', ' '));
    }

    private bool HasCompatibleHenchmanAssignments(DialogueCachePreset preset) => HenchmanSlots.All(slot =>
        preset.HenchmanAssignments?.TryGetValue(slot.Slot.SlotTag, out string henchmanTag) == true
        && CurveEditor3D.GetDialoguePreviewHenchmanTags().Contains(henchmanTag,
            StringComparer.OrdinalIgnoreCase));

    private void CacheSearchTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshCachePresetList();

    private void RefreshCachePresetList(DialogueCachePreset preferredSelection = null)
    {
        if (CachePresetList is null)
        {
            return;
        }

        DialogueCachePreset previousSelection = preferredSelection
                                                        ?? CachePresetList.SelectedItem as DialogueCachePreset;
        string search = CacheSearchTextBox?.Text.Trim() ?? string.Empty;
        DialogueCachePreset[] filtered = compatibleCachePresets
            .Where(preset => CachePresetMatchesSearch(preset, search))
            .OrderByDescending(preset => preset.SavedUtc)
            .ToArray();
        CachePresetList.ItemsSource = filtered;
        CachePresetList.SelectedItem = previousSelection is not null && filtered.Contains(previousSelection)
            ? previousSelection
            : filtered.FirstOrDefault();
        DeleteCachePresetButton.IsEnabled = CachePresetList.SelectedItem is DialogueCachePreset;
        if (filtered.Length == 0)
        {
            CacheDetailsText.Text = search.Length == 0
                ? "No compatible saved cache is available for this starting node."
                : "No compatible cache presets match the search.";
        }
    }

    internal static bool CachePresetMatchesSearch(DialogueCachePreset preset, string search)
    {
        if (preset is null)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        search = search.Trim();
        return preset.Label?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true
               || preset.PccName?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true
               || preset.DialogueName?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true
               || preset.SourceFilePath?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true
               || preset.SavedDisplay.Contains(search, StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool IsCacheIdentityCompatible(DialogueCachePreset preset, ConversationExtended conversation,
        DialogueNodeExtended startNode)
    {
        int startIndex = startNode.IsReply
            ? conversation.ReplyList.IndexOf(startNode)
            : conversation.EntryList.IndexOf(startNode);
        string sourcePath = conversation.Export.FileRef.FilePath;
        return preset is not null && startIndex >= 0 && PathsEqual(preset.SourceFilePath, sourcePath)
               && preset.Game == conversation.Export.Game
               && preset.DialogueUIndex == conversation.Export.UIndex
               && string.Equals(preset.DialogueExportPath, conversation.Export.InstancedFullPath,
                   StringComparison.OrdinalIgnoreCase)
               && preset.StartNodeIsReply == startNode.IsReply
               && preset.StartNodeIndex == startIndex;
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                          or PathTooLongException)
        {
            return false;
        }
    }

    private void AddRecentSet_Click(object sender, RoutedEventArgs e) => AddSelectedRecentSet();

    private void RecentSetsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AddSelectedRecentSet();

    private void AddSelectedRecentSet()
    {
        if (RecentSetsList.SelectedItem is RecentLevelSetItem set)
        {
            AddPaths(set.FilePaths);
        }
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "Add level packages",
            Filter = "Unreal packages|*.pcc;*.upk;*.u|All files|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            AddPaths(dialog.FileNames);
        }
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        foreach (string path in paths.Where(File.Exists))
        {
            if (!SelectedFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                SelectedFiles.Add(path);
            }
        }
    }

    private void RemoveFiles_Click(object sender, RoutedEventArgs e)
    {
        foreach (string path in SelectedFilesList.SelectedItems.Cast<string>().ToArray())
        {
            SelectedFiles.Remove(path);
        }
    }

    private void CachePresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedCachePreset = null;
        DeleteCachePresetButton.IsEnabled = CachePresetList.SelectedItem is DialogueCachePreset;
        if (CachePresetList.SelectedItem is not DialogueCachePreset preset)
        {
            CacheDetailsText.Text = string.IsNullOrWhiteSpace(CacheSearchTextBox?.Text)
                ? "No compatible saved cache is available for this starting node."
                : "No compatible cache presets match the search.";
            return;
        }
        string nodeCount = preset.CachedNodeCount >= 0 ? $"{preset.NodeCount} node(s), " : string.Empty;
        CacheDetailsText.Text = $"{preset.SavedDisplay} — {nodeCount}"
                                + $"{preset.LevelPaths.Count} remembered level(s)";
        restoringCacheOptions = true;
        try
        {
            PlayerGenderComboBox.SelectedIndex = preset.PlayerGender == CurveEditor3D.DialoguePreviewPlayerGender.Male
                ? 1
                : 0;
            foreach (HenchmanSlotSelection slot in HenchmanSlots)
            {
                slot.SelectedHenchmanTag = preset.HenchmanAssignments.GetValueOrDefault(slot.Slot.SlotTag);
            }
            HenchmanItemsControl.Items.Refresh();
            SelectedFiles.Clear();
            AddPaths(preset.LevelPaths);
            UseSelectedCacheRadio.IsChecked = true;
        }
        finally
        {
            restoringCacheOptions = false;
        }
    }

    private void DeleteCachePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (CachePresetList.SelectedItem is not DialogueCachePreset preset
            || MessageBox.Show(this, $"Delete dialogue cache preset '{preset.Label}'?",
                "Delete Dialogue Cache", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SavedDialogueCachePresetManager.Delete(preset);
            compatibleCachePresets.Remove(preset);
            RefreshCachePresetList();
            if (CachePresetList.SelectedItem is null)
            {
                BuildNewCacheRadio.IsChecked = true;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Unable to delete the dialogue cache: {exception.Message}",
                "Delete Dialogue Cache", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PlayerGenderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (restoringCacheOptions || UseSelectedCacheRadio?.IsChecked != true
                                  || CachePresetList?.SelectedItem is not DialogueCachePreset preset
                                  || preset.PlayerGender == PlayerSelection.Gender)
        {
            return;
        }
        BuildNewCacheRadio.IsChecked = true;
        CacheDetailsText.Text = "The player changed, so a new cache will be built using the remembered levels.";
    }

    private void HenchmanComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (restoringCacheOptions || UseSelectedCacheRadio?.IsChecked != true
                                  || CachePresetList?.SelectedItem is not DialogueCachePreset preset)
        {
            return;
        }
        bool matchesPreset = HenchmanSlots.All(slot =>
            preset.HenchmanAssignments.TryGetValue(slot.Slot.SlotTag, out string cachedTag)
            && string.Equals(cachedTag, slot.SelectedHenchmanTag, StringComparison.OrdinalIgnoreCase));
        if (!matchesPreset)
        {
            BuildNewCacheRadio.IsChecked = true;
            CacheDetailsText.Text =
                "The squad assignment changed, so a new cache will be built using the remembered levels.";
        }
    }

    private void CacheChoice_Changed(object sender, RoutedEventArgs e)
    {
        if (SaveNewCacheCheckBox is null || NewCacheLabelTextBox is null)
        {
            return;
        }
        bool build = BuildNewCacheRadio.IsChecked == true;
        CachePresetList.IsEnabled = !build;
        SaveNewCacheCheckBox.IsEnabled = build;
        NewCacheLabelTextBox.IsEnabled = build && SaveNewCacheCheckBox.IsChecked == true;
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (PlayerGenderComboBox.SelectedItem is not ComboBoxItem)
        {
            MessageBox.Show(this, "Choose whether the player actor should be male or female.",
                "Player Gender Required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (HenchmanSlots.Any(slot => string.IsNullOrWhiteSpace(slot.SelectedHenchmanTag)))
        {
            MessageBox.Show(this, "Choose a squadmate for every detected henchman slot.",
                "Squad Assignment Required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (CacheGroupBox.Visibility == Visibility.Visible && UseSelectedCacheRadio.IsChecked == true)
        {
            if (CachePresetList.SelectedItem is not DialogueCachePreset header)
            {
                return;
            }

            LoadPreviewButton.IsEnabled = false;
            CacheGroupBox.IsEnabled = false;
            CacheDetailsText.Text = $"Loading '{header.Label}'...";
            try
            {
                selectedCachePreset = await Task.Run(() => SavedDialogueCachePresetManager.Load(header));
            }
            finally
            {
                LoadPreviewButton.IsEnabled = true;
                CacheGroupBox.IsEnabled = true;
            }
            if (!IsVisible)
            {
                return;
            }
            if (selectedCachePreset is null)
            {
                MessageBox.Show(this, "The selected cache could not be read. It may have been moved or damaged.",
                    "Load Dialogue Cache", MessageBoxButton.OK, MessageBoxImage.Error);
                CachePresetList_SelectionChanged(CachePresetList, null);
                return;
            }
        }
        DialogResult = true;
    }
}
