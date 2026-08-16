using System;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorerCore.Packages;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Tools.SFXGalaxyEditor;

public partial class SFXGalaxyAddObjectDialog : Window
{
    private readonly IMEPackage _package;
    private readonly bool _usesDisplayNameTlk;

    public string ObjectName { get; private set; }
    public int DisplayNameStringRef { get; private set; }

    public SFXGalaxyAddObjectDialog(Window owner, string kindName, string defaultObjectName, IMEPackage package,
        bool usesDisplayNameTlk)
    {
        InitializeComponent();
        CustomWindowChrome.ApplyCustomChrome(this);
        Owner = owner;
        Title = $"Add {kindName}";
        _package = package;
        _usesDisplayNameTlk = usesDisplayNameTlk;

        ObjectNameTextBox.Text = defaultObjectName;
        TlkTextBox.Text = "0";
        TlkInputPanel.Visibility = usesDisplayNameTlk ? Visibility.Visible : Visibility.Collapsed;
        TlkPreviewPanel.Visibility = usesDisplayNameTlk ? Visibility.Visible : Visibility.Collapsed;

        Loaded += (_, _) =>
        {
            ObjectNameTextBox.Focus();
            ObjectNameTextBox.SelectAll();
        };
    }

    private void TlkTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string value = TlkTextBox.Text.Trim();
        if (string.IsNullOrEmpty(value) || value == "0")
        {
            TlkPreviewTextBlock.Text = "No TLK StringRef selected.";
            return;
        }

        if (!int.TryParse(value, out int stringRef) || stringRef < 0)
        {
            TlkPreviewTextBlock.Text = "Enter a non-negative numeric TLK StringRef ID.";
            return;
        }

        string preview = TLKManagerWPF.GlobalFindStrRefbyID(stringRef, _package)?.Trim().Trim('"');
        TlkPreviewTextBlock.Text = string.IsNullOrWhiteSpace(preview)
            || preview.Equals("No Data", StringComparison.OrdinalIgnoreCase)
            || preview.StartsWith("No TLK", StringComparison.OrdinalIgnoreCase)
                ? $"No TLK text found for StringRef {stringRef}."
                : preview;
    }

    private void FindTlkText_Click(object sender, RoutedEventArgs e)
    {
        if (TlkStringRefSelector.SelectStringRef(this, _package) is int stringRef)
        {
            TlkTextBox.Text = stringRef.ToString();
            TlkTextBox.CaretIndex = TlkTextBox.Text.Length;
            TlkTextBox.Focus();
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        string objectName = ObjectNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(objectName))
        {
            MessageBox.Show(this, "Enter a custom object name.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            ObjectNameTextBox.Focus();
            return;
        }

        int displayNameStringRef = 0;
        if (_usesDisplayNameTlk)
        {
            string value = TlkTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(value)
                && (!int.TryParse(value, out displayNameStringRef) || displayNameStringRef < 0))
            {
                MessageBox.Show(this, "Enter a non-negative numeric TLK StringRef ID, or use 0 to set it later.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                TlkTextBox.Focus();
                TlkTextBox.SelectAll();
                return;
            }
        }

        ObjectName = objectName;
        DisplayNameStringRef = displayNameStringRef;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
