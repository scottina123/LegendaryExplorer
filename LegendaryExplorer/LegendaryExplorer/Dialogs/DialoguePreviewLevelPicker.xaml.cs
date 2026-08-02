using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using Microsoft.Win32;

namespace LegendaryExplorer.Dialogs;

public partial class DialoguePreviewLevelPicker : TrackingNotifyPropertyChangedWindowBase
{
    private sealed record RecentLevelSetItem(string DisplayName, IReadOnlyList<string> FilePaths)
    {
        public string FileCountText => $"{FilePaths.Count} package{(FilePaths.Count == 1 ? string.Empty : "s")}";
        public string TooltipText => string.Join("\n", FilePaths.Select(Path.GetFileName));
    }

    public ObservableCollection<string> SelectedFiles { get; } = [];
    public IReadOnlyList<string> SelectedLevelPaths => SelectedFiles.ToArray();

    public DialoguePreviewLevelPicker() : base("Dialogue Preview Level Picker", false)
    {
        InitializeComponent();
        RecentSetsList.ItemsSource = CurveEditor3D.GetDialoguePreviewRecentLevelSets()
            .Select(set => new RecentLevelSetItem(set.DisplayName, set.FilePaths))
            .ToArray();
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

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
