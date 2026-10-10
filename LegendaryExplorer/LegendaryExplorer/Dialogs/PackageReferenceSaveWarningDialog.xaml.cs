using System.Windows;
using LegendaryExplorer.SharedUI;

namespace LegendaryExplorer.Dialogs;

public partial class PackageReferenceSaveWarningDialog : Window
{
    public string DestinationPath { get; }
    public string WarningMessage { get; }
    internal bool OpenReferenceIssues { get; private set; }

    internal PackageReferenceSaveWarningDialog(string destinationPath, int issueCount)
    {
        CustomWindowChrome.ApplyCustomChrome(this);
        DestinationPath = destinationPath;
        WarningMessage = $"This package contains {issueCount} reference {(issueCount == 1 ? "issue" : "issues")}.";
        DataContext = this;
        InitializeComponent();
    }

    private void SaveAnyway_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OpenIssues_Click(object sender, RoutedEventArgs e)
    {
        OpenReferenceIssues = true;
        DialogResult = false;
    }
}
