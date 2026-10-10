using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorerCore.Localization;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Dialogs;

public partial class ReferenceIssuesDialog : TrackingNotifyPropertyChangedWindowBase
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

    public ReferenceIssuesDialog(WPFBase owner, IMEPackage package, IEnumerable<EntryStringPair> issues,
        Action<EntryStringPair> navigate, Action refreshEditor) : base("Reference Issues", false)
    {
        this.package = package;
        this.navigate = navigate;
        this.refreshEditor = refreshEditor;
        editor = owner;
        DataContext = this;
        InitializeComponent();
        Owner = owner;
        Title = $"Reference issues in {package.FilePath}";
        Issues.ReplaceAll(issues);
        StatusText = $"{Issues.Count} reference issues.";
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

        var check = new ReferenceCheckPackage();
        EntryChecker.CheckReferences(check, package, LECLocalizationShim.NonLocalizedStringConverter);
        Issues.ReplaceAll(check.GetBlockingErrors().Concat(check.GetSignificantIssues()));
        StatusText = $"{Issues.Count} reference issues.";
    }

    public ReferenceCleanupResult RemoveBadReferences()
    {
        if (!IsCurrentPackage)
            return null;

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);
        refreshEditor?.Invoke();
        RefreshIssues();
        StatusText = $"Removed {result.RemovedPropertyCount} properties/array entries; "
            + $"cleared {result.ClearedPropertyReferenceCount} fixed-layout references and {result.ClearedBinaryReferenceCount} binary references. "
            + $"{Issues.Count} issues remain. Changes are not saved.";
        if (result.Failures.Count > 0)
        {
            Issues.AddRange(result.Failures);
            StatusText += $" {result.Failures.Count} cleanup failures; see list for details.";
        }
        return result;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshIssues();

    private void RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        if (!IsCurrentPackage)
            return;

        if (MessageBox.Show(this,
                "Remove all out-of-range, trashed, or wrong-type object references in this package?\n\n"
                + "Bad properties and array entries will be removed. Binary references and fields in fixed-layout structs will be set to 0. "
                + "Header references and name errors require manual repair.\n\nChanges will remain unsaved until you save the package.",
                "Remove all bad references", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            RemoveBadReferences();
    }

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
