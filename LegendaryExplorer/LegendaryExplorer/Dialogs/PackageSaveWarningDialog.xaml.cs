using System.Windows;
using LegendaryExplorer.SharedUI;

namespace LegendaryExplorer.Dialogs;

public partial class PackageSaveWarningDialog : Window
{
    internal PackageSaveChoice Choice { get; private set; } = PackageSaveChoice.Cancel;
    public string CurrentPath { get; }
    public string HighestPathDisplay { get; }
    public bool CanSaveHighestMounted { get; }
    public string WarningMessage { get; }

    internal PackageSaveWarningDialog(PackageSaveWarning warning)
    {
        CustomWindowChrome.ApplyCustomChrome(this);
        CurrentPath = warning.CurrentPath;
        HighestPathDisplay = warning.HighestMountedPath ?? "No matching file was found in the configured game installation.";
        CanSaveHighestMounted = warning.CanSaveHighestMounted;
        WarningMessage = warning.IsOutsideGame
            ? "This file is outside the configured game installation. Saving it here will not update the file used by the game."
            : "This file is not the highest mounted version. Saving it here will not update the file used by the game.";
        DataContext = this;
        InitializeComponent();
    }

    private void SaveHighest_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSaveHighestMounted) return;
        Choice = PackageSaveChoice.HighestMountedFile;
        DialogResult = true;
    }

    private void SaveCurrent_Click(object sender, RoutedEventArgs e)
    {
        Choice = PackageSaveChoice.CurrentFile;
        DialogResult = true;
    }
}
