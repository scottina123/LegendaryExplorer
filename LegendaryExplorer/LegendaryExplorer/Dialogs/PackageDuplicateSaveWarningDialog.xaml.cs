using System.Windows;
using LegendaryExplorer.SharedUI;

namespace LegendaryExplorer.Dialogs;

public partial class PackageDuplicateSaveWarningDialog : Window
{
    public string DestinationPath { get; }
    public string WarningMessage { get; }
    internal bool OpenDuplicateIssues { get; private set; }

    internal PackageDuplicateSaveWarningDialog(string destinationPath, int issueCount)
    {
        CustomWindowChrome.ApplyCustomChrome(this);
        DestinationPath = destinationPath;
        WarningMessage = $"This package contains {issueCount} duplicate index {(issueCount == 1 ? "issue" : "issues")}.";
        DataContext = this;
        InitializeComponent();
    }

    private void SaveAnyway_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OpenIssues_Click(object sender, RoutedEventArgs e)
    {
        OpenDuplicateIssues = true;
        DialogResult = false;
    }
}
