using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Dialogs;

public partial class DuplicateIssuesDialog : TrackingNotifyPropertyChangedWindowBase
{
    private readonly WPFBase editor;
    private readonly IMEPackage package;
    private readonly Action<EntryStringPair> navigate;
    private readonly Action refreshEditor;
    private string statusText;

    public ObservableCollectionExtended<EntryStringPair> Issues { get; } = new();

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public DuplicateIssuesDialog(WPFBase owner, IMEPackage package, IEnumerable<EntryStringPair> issues,
        Action<EntryStringPair> navigate, Action refreshEditor) : base("Duplicate Indexes", false)
    {
        this.package = package;
        this.navigate = navigate;
        this.refreshEditor = refreshEditor;
        editor = owner;
        DataContext = this;
        InitializeComponent();
        Owner = owner;
        Title = $"Duplicate indexes in {package.FilePath}";
        SetIssues(issues);
        PreviewKeyDown += Dialog_PreviewKeyDown;
        if (editor != null)
            editor.PropertyChanged += Editor_PropertyChanged;
    }

    private bool IsCurrentPackage => editor == null || ReferenceEquals(editor.Pcc, package);

    private void Editor_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WPFBase.Pcc) && !IsCurrentPackage)
            Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (editor != null)
            editor.PropertyChanged -= Editor_PropertyChanged;
        base.OnClosed(e);
    }

    public void RefreshIssues()
    {
        if (!IsCurrentPackage)
            return;

        try
        {
            SetIssues(EntryChecker.CheckForDuplicateIndices(package));
        }
        catch (Exception ex)
        {
            SetIssues([new EntryStringPair($"Duplicate index checking could not finish: {ex.Message}")]);
            StatusText = "Could not check duplicate indexes; see list for details.";
        }
    }

    internal void SetIssues(IEnumerable<EntryStringPair> issues)
    {
        Issues.ReplaceAll(issues);
        StatusText = $"{Issues.Count} duplicate issues.";
    }

    public int FixDuplicateIssues()
    {
        if (!IsCurrentPackage)
            return 0;

        int changed = DuplicateIndexRepairer.FixDuplicateIndices(package);
        refreshEditor?.Invoke();
        RefreshIssues();
        StatusText = $"Reindexed {changed} entries. {Issues.Count} duplicate issues remain. Changes are not saved.";
        return changed;
    }

    internal void ConfirmAndFixDuplicateIssues()
    {
        if (!IsCurrentPackage)
            return;

        RefreshIssues();
        if (Issues.Count == 0)
            return;

        if (MessageBox.Show(this,
                "Fix all duplicate object indexes in this package?\n\n"
                + "Duplicate exports and imports will be assigned unused object indexes. This changes their object paths. "
                + "The first occurrence of each duplicate will keep its index.\n\nChanges will remain unsaved until you save the package.",
                "Fix all duplicate issues", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
        {
            try
            {
                FixDuplicateIssues();
            }
            catch (Exception ex)
            {
                StatusText = $"Could not finish fixing duplicate indexes: {ex.Message}";
                MessageBox.Show(this, StatusText, "Fix all duplicate issues", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshIssues();

    private void FixAll_Click(object sender, RoutedEventArgs e) => ConfirmAndFixDuplicateIssues();

    private void NavigateToIssue(EntryStringPair issue)
    {
        if (IsCurrentPackage && issue?.Entry != null && ReferenceEquals(issue.Entry.FileRef, package))
            navigate?.Invoke(issue);
    }

    private void IssuesList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(IssuesList, source) is ListViewItem { DataContext: EntryStringPair issue })
        {
            NavigateToIssue(issue);
            e.Handled = true;
        }
    }

    private void IssuesList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && IssuesList.SelectedItem is EntryStringPair issue)
        {
            NavigateToIssue(issue);
            e.Handled = true;
        }
    }

    private void Dialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            RefreshIssues();
            e.Handled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, Issues));
            StatusText = $"Copied {Issues.Count} issues to clipboard.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not copy issues to clipboard:\n{ex.Message}");
        }
    }
}
