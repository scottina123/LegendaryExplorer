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
        private bool _initializingEffects;
        private readonly bool _stopAllEventExists;

        public float SelectedVolume => (float)VolumeSlider.Value;
        public bool? LoopAudio => LoopAudioCheckBox.IsChecked;
        public bool FactoryRadioEffect => FactoryRadioEffectCheckBox.IsChecked == true;
        public bool BioWareRadioEffect => BioWareRadioEffectCheckBox.IsChecked == true;
        public bool QecEffect => QecEffectCheckBox.IsChecked == true;
        public bool HelmetEffect => HelmetEffectCheckBox.IsChecked == true;
        public bool CreateStopAllEvent => !_stopAllEventExists && StopAllEventCheckBox.IsChecked == true;

        public WwiseBankVolumeDialog(float currentVolume, bool? loopAudio,
            bool factoryRadioEffect, bool canApplyFactoryRadioEffect,
            bool bioWareRadioEffect, bool canApplyBioWareRadioEffect,
            bool qecEffect, bool canApplyQecEffect,
            bool helmetEffect, bool canApplyHelmetEffect,
            bool stopAllEventExists, bool canCreateStopAllEvent)
        {
            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
            _stopAllEventExists = stopAllEventExists;

            VolumeSlider.Minimum = Math.Min(-96, currentVolume);
            VolumeSlider.Maximum = Math.Max(12, currentVolume);
            _updatingVolume = true;
            VolumeSlider.Value = currentVolume;
            VolumeTextBox.Text = currentVolume.ToString("0.##", CultureInfo.InvariantCulture);
            _updatingVolume = false;
            CurrentVolumeRun.Text = $"{currentVolume.ToString("0.0", CultureInfo.InvariantCulture)} dB";
            LoopAudioCheckBox.IsChecked = loopAudio;

            _initializingEffects = true;
            FactoryRadioEffectCheckBox.IsChecked = factoryRadioEffect;
            BioWareRadioEffectCheckBox.IsChecked = bioWareRadioEffect;
            QecEffectCheckBox.IsChecked = qecEffect;
            HelmetEffectCheckBox.IsChecked = helmetEffect;
            _initializingEffects = false;

            FactoryRadioEffectCheckBox.IsEnabled = canApplyFactoryRadioEffect || factoryRadioEffect;
            BioWareRadioEffectCheckBox.IsEnabled = canApplyBioWareRadioEffect || bioWareRadioEffect;
            QecEffectCheckBox.IsEnabled = canApplyQecEffect || qecEffect;
            HelmetEffectCheckBox.IsEnabled = canApplyHelmetEffect || helmetEffect;
            StopAllEventCheckBox.IsChecked = stopAllEventExists;
            StopAllEventCheckBox.IsEnabled = !stopAllEventExists && canCreateStopAllEvent;
            if (stopAllEventExists)
            {
                StopAllEventCheckBox.Content = "Stop event for all bank audio already exists";
            }

            EffectUnavailableText.Visibility = canApplyFactoryRadioEffect || canApplyBioWareRadioEffect ||
                                               canApplyQecEffect || canApplyHelmetEffect || canCreateStopAllEvent
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void EffectCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_initializingEffects)
            {
                return;
            }

            _initializingEffects = true;
            if (sender != FactoryRadioEffectCheckBox)
            {
                FactoryRadioEffectCheckBox.IsChecked = false;
            }
            if (sender != BioWareRadioEffectCheckBox)
            {
                BioWareRadioEffectCheckBox.IsChecked = false;
            }
            if (sender != QecEffectCheckBox)
            {
                QecEffectCheckBox.IsChecked = false;
            }
            if (sender != HelmetEffectCheckBox)
            {
                HelmetEffectCheckBox.IsChecked = false;
            }
            _initializingEffects = false;

            if (sender == BioWareRadioEffectCheckBox)
            {
                VolumeSlider.Value = 12;
            }
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
