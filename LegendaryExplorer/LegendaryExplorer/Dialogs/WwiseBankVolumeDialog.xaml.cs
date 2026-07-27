using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.SharedUI;

namespace LegendaryExplorer.Dialogs
{
    public partial class WwiseBankVolumeDialog : Window
    {
        private bool _updatingVolume;

        public float SelectedVolume => (float)VolumeSlider.Value;

        public WwiseBankVolumeDialog(float currentVolume)
        {
            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);

            VolumeSlider.Minimum = Math.Min(-96, currentVolume);
            VolumeSlider.Maximum = Math.Max(12, currentVolume);
            _updatingVolume = true;
            VolumeSlider.Value = currentVolume;
            VolumeTextBox.Text = currentVolume.ToString("0.##", CultureInfo.InvariantCulture);
            _updatingVolume = false;
            CurrentVolumeRun.Text = $"{currentVolume.ToString("0.0", CultureInfo.InvariantCulture)} dB";
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingVolume || VolumeTextBox == null)
            {
                return;
            }

            _updatingVolume = true;
            VolumeTextBox.Text = e.NewValue.ToString("0.##", CultureInfo.InvariantCulture);
            VolumeTextBox.CaretIndex = VolumeTextBox.Text.Length;
            _updatingVolume = false;
            SetValidationState(true, null);
        }

        private void VolumeTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updatingVolume || VolumeSlider == null)
            {
                return;
            }

            if (!double.TryParse(VolumeTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double volume))
            {
                SetValidationState(false, "Enter a valid number using a period as the decimal separator.");
                return;
            }

            if (volume < VolumeSlider.Minimum || volume > VolumeSlider.Maximum)
            {
                SetValidationState(false, $"Volume must be between {VolumeSlider.Minimum:0.##} and {VolumeSlider.Maximum:0.##} dB.");
                return;
            }

            _updatingVolume = true;
            VolumeSlider.Value = volume;
            _updatingVolume = false;
            SetValidationState(true, null);
        }

        private void SetValidationState(bool isValid, string message)
        {
            if (ApplyButton == null || ValidationText == null)
            {
                return;
            }

            ApplyButton.IsEnabled = isValid;
            ValidationText.Text = message ?? string.Empty;
            ValidationText.Visibility = isValid ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
