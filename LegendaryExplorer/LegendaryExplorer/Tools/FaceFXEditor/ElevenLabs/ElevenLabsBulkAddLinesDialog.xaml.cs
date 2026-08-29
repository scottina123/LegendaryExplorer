using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using LegendaryExplorer.SharedUI;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Tools.FaceFXEditor.ElevenLabs
{
    public partial class ElevenLabsBulkAddLinesDialog : Window
    {
        public ElevenLabsBulkAddLinesDialog(int startingTlkId, Window owner = null)
        {
            InitializeComponent();
            Owner = owner;
            CustomWindowChrome.ApplyCustomChrome(this);
            StartingTlkIdTextBox.Text = Math.Max(1, startingTlkId).ToString(CultureInfo.InvariantCulture);
            Loaded += (_, _) => LinesTextBox.Focus();
        }

        public int StartingTlkId { get; private set; }
        public IReadOnlyList<string> LineTexts { get; private set; } = [];

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(StartingTlkIdTextBox.Text, NumberStyles.None, CultureInfo.InvariantCulture,
                    out int startingTlkId) || startingTlkId <= 0)
            {
                MessageBox.Show(this, "Starting TLK ID must be a positive integer.", "Invalid TLK ID",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var lines = LinesTextBox.Text.ReplaceLineEndings("\n").Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
            if (lines.Count == 0)
            {
                MessageBox.Show(this, "Enter at least one line of text.", "No lines",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if ((long)startingTlkId + lines.Count - 1 > int.MaxValue)
            {
                MessageBox.Show(this, "The TLK ID range exceeds the maximum supported value.", "Invalid TLK range",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StartingTlkId = startingTlkId;
            LineTexts = lines;
            DialogResult = true;
        }
    }
}
