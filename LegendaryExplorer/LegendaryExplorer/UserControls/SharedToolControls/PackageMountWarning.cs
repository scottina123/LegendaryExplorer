using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.UserControls.SharedToolControls;

/// <summary>
/// A package status indicator independent of each tool's filename and progress messages.
/// </summary>
public sealed class PackageMountWarning : StatusBarItem
{
    public static readonly DependencyProperty PackageProperty = DependencyProperty.Register(
        nameof(Package), typeof(IMEPackage), typeof(PackageMountWarning),
        new PropertyMetadata(null, OnPackageChanged));

    public IMEPackage Package
    {
        get => (IMEPackage)GetValue(PackageProperty);
        set => SetValue(PackageProperty, value);
    }

    public PackageMountWarning()
    {
        DockPanel.SetDock(this, Dock.Left);
        HorizontalAlignment = HorizontalAlignment.Left;
        var link = new Hyperlink(new Run(PackageMountStatus.WarningText))
        {
            Cursor = Cursors.Hand
        };
        link.SetResourceReference(TextElement.ForegroundProperty, SystemColors.HotTrackBrushKey);
        link.Click += OpenHighestMountedVersion_Click;
        Content = new TextBlock(link);
        Visibility = Visibility.Collapsed;
    }

    private async void OpenHighestMountedVersion_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!IsEnabled) return;
        IsEnabled = false;
        try
        {
            await OpenHighestMountedVersionAsync();
        }
        catch (Exception ex)
        {
            Xceed.Wpf.Toolkit.MessageBox.Show(Window.GetWindow(this),
                $"Could not open the highest mounted version:\n{ex.Message}", "Open highest mounted version");
        }
        finally
        {
            IsEnabled = true;
        }
    }

    internal Task<Window> OpenHighestMountedVersionAsync()
    {
        // Recheck on activation in case the installed DLC changed since the package was opened.
        string mountedPath = RefreshWarning();
        if (mountedPath == null) return Task.FromResult<Window>(null);
        var source = DataContext as Window ?? Window.GetWindow(this);
        return PackageToolLauncher.OpenAsync(source, mountedPath);
    }

    private static void OnPackageChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var indicator = (PackageMountWarning)sender;
        if (e.OldValue is INotifyPropertyChanged oldPackage)
        {
            PropertyChangedEventManager.RemoveHandler(oldPackage, indicator.PackagePropertyChanged, string.Empty);
        }
        if (e.NewValue is INotifyPropertyChanged newPackage)
        {
            PropertyChangedEventManager.AddHandler(newPackage, indicator.PackagePropertyChanged, string.Empty);
        }
        indicator.RefreshWarning();
    }

    private void PackagePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(IMEPackage.FilePath) or nameof(IMEPackage.LastSaved))
        {
            // Package saves can finish on a background thread.
            if (Dispatcher.CheckAccess())
            {
                RefreshWarning();
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => RefreshWarning()));
            }
        }
    }

    private string RefreshWarning()
    {
        string mountedPath = PackageMountStatus.GetOverridingFilePath(Package);
        ToolTip = mountedPath == null ? null
            : $"Open the highest mounted version in a new instance of this tool:\n{mountedPath}";
        Visibility = mountedPath == null ? Visibility.Collapsed : Visibility.Visible;
        return mountedPath;
    }
}
