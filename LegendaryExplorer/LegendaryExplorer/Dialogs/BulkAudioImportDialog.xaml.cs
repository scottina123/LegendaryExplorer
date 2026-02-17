using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using LegendaryExplorer.SharedUI;
using Microsoft.Win32;

namespace LegendaryExplorer.Dialogs
{
    public partial class BulkAudioImportDialog : Window
    {
        public ObservableCollection<string> WavFiles { get; } = new();

        public BulkAudioImportDialog()
        {
            InitializeComponent();
            DataContext = this;
            CustomWindowChrome.ApplyCustomChrome(this);
        }

        private void AddFilesButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "WAV files (*.wav)|*.wav",
                Multiselect = true,
                Title = "Select WAV files to import"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                foreach (var file in openFileDialog.FileNames)
                {
                    if (!WavFiles.Contains(file))
                    {
                        WavFiles.Add(file);
                    }
                }
            }
        }

        private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = WavFilesListBox.SelectedItems.Cast<string>().ToList();
            foreach (var item in selectedItems)
            {
                WavFiles.Remove(item);
            }
        }

        private void ClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            WavFiles.Clear();
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (WavFiles.Count == 0)
            {
                MessageBox.Show("No WAV files have been added.", "No files", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // TODO: Implement bulk audio import logic
            MessageBox.Show($"{WavFiles.Count} file(s) ready for import.\nImport functionality is not yet implemented.", "Not implemented", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
