using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;

namespace LegendaryExplorer.Dialogs
{
    public partial class OpenPccToolSelector : Window
    {
        public string SelectedTool { get; private set; }

        private OpenPccToolSelector(string fileName)
        {
            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
            DirectionsTextBlock.Text = $"Open {Path.GetFileName(fileName)} with:";
        }

        public static string GetTool(string fileName)
        {
            var dialog = new OpenPccToolSelector(fileName);
            var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
                        ?? Application.Current.MainWindow;

            if (owner?.IsLoaded == true && PresentationSource.FromVisual(owner) != null)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                owner.RestoreAndBringToFront();
            }

            dialog.Loaded += (_, _) =>
            {
                dialog.RestoreAndBringToFront();
                dialog.Activate();
                dialog.Focus();
            };

            return dialog.ShowDialog() == true ? dialog.SelectedTool : null;
        }

        private void ToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string tool })
            {
                SelectedTool = tool;
                DialogResult = true;
            }
        }
    }
}
