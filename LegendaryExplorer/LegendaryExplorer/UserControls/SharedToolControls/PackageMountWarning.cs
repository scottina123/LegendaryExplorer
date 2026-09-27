using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        Content = PackageMountStatus.WarningText;
        Visibility = Visibility.Collapsed;
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
                Dispatcher.BeginInvoke(new Action(RefreshWarning));
            }
        }
    }

    private void RefreshWarning()
    {
        string mountedPath = PackageMountStatus.GetOverridingFilePath(Package);
        ToolTip = mountedPath == null ? null : $"The game loads this version instead:\n{mountedPath}";
        Visibility = mountedPath == null ? Visibility.Collapsed : Visibility.Visible;
    }
}
