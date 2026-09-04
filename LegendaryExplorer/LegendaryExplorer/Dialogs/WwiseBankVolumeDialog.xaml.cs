using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.UnrealExtensions;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Dialogs
{
    internal enum WwiseEditorEffectPreset
    {
        Preserve,
        Inherit,
        None,
        FactoryRadio,
        BioWareRadio,
        Qec,
        Helmet,
        Le2Radio,
        Le2Helmet,
        Le2Hologram
    }

    internal sealed record WwiseEditorEffectOption(string Name, WwiseEditorEffectPreset Preset);

    internal sealed class WwiseEditorAudioSettings
    {
        internal MEGame Game { get; init; }
        internal string ScopeName { get; init; }
        internal string TargetSummary { get; init; }
        internal bool IsBankWide { get; init; }
        internal float Volume { get; init; }
        internal bool VolumeIsMixed { get; init; }
        internal uint? OutputBusId { get; init; }
        internal string EffectiveInheritedOutputBus { get; init; }
        internal bool? LoopAudio { get; init; }
        internal bool CanLoopAudio { get; init; }
        internal WwiseEditorEffectPreset EffectPreset { get; init; }
        internal string EffectSummary { get; init; }
        internal bool? DuckAudio { get; init; }
        internal bool? Attenuation { get; init; }
        internal double AttenuationScalePercent { get; init; } = 100;
        internal bool CanApplyEffects { get; init; }
        internal bool CanApplyDucking { get; init; }
        internal bool CanApplyAttenuation { get; init; }
        internal bool StopEventExists { get; init; }
        internal bool CanCreateStopEvent { get; init; }
    }

    internal sealed record WwiseEditorOutputBusOption(string Name, uint? Id, string ResolvedName);

    public partial class WwiseBankVolumeDialog : Window
    {
        private bool _updatingVolume;
        private bool _initializing;
        private readonly WwiseEditorAudioSettings _settings;

        public float SelectedVolume => (float)VolumeSlider.Value;
        public bool VolumeWasEdited { get; private set; }
        internal uint? SelectedOutputBusId =>
            (OutputBusComboBox.SelectedItem as WwiseEditorOutputBusOption)?.Id;
        public bool OutputBusWasEdited { get; private set; }
        public bool? LoopAudio => LoopAudioCheckBox.IsChecked;
        internal WwiseEditorEffectPreset SelectedEffectPreset =>
            (EffectPresetComboBox.SelectedItem as WwiseEditorEffectOption)?.Preset ??
            WwiseEditorEffectPreset.Preserve;
        public bool? DuckAudio => DuckAudioCheckBox.IsChecked;
        public bool? Attenuation => AttenuationCheckBox.IsChecked;
        public double AttenuationScalePercent => AttenuationScaleSlider.Value;
        public bool CreateStopEvent => !_settings.StopEventExists && StopEventCheckBox.IsChecked == true;

        internal WwiseBankVolumeDialog(WwiseEditorAudioSettings settings)
        {
            _settings = settings;
            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);

            _initializing = true;
            Title = settings.IsBankWide ? "Adjust Bank Settings" : "Adjust Wwise Event Settings";
            DescriptionText.Text = settings.IsBankWide
                ? $"Set routing and audio processing for bank {settings.ScopeName}."
                : $"Set routing and audio processing for event {settings.ScopeName}.";
            TargetSummaryText.Text = settings.TargetSummary;

            VolumeSlider.Minimum = Math.Min(-96, settings.Volume);
            VolumeSlider.Maximum = Math.Max(12, settings.Volume);
            _updatingVolume = true;
            VolumeSlider.Value = settings.Volume;
            VolumeTextBox.Text = settings.Volume.ToString("0.##", CultureInfo.InvariantCulture);
            _updatingVolume = false;
            CurrentVolumeRun.Text = settings.VolumeIsMixed
                ? $"mixed (showing {settings.Volume.ToString("0.0", CultureInfo.InvariantCulture)} dB)"
                : $"{settings.Volume.ToString("0.0", CultureInfo.InvariantCulture)} dB";

            PopulateOutputBuses(settings);
            PopulateEffects(settings);
            LoopAudioCheckBox.IsChecked = settings.LoopAudio;
            LoopAudioCheckBox.IsEnabled = settings.CanLoopAudio;
            DuckAudioCheckBox.IsChecked = settings.DuckAudio;
            AttenuationCheckBox.IsChecked = settings.Attenuation;
            AttenuationScaleSlider.Value = Math.Clamp(settings.AttenuationScalePercent, 10, 500);

            EffectPresetComboBox.IsEnabled = settings.CanApplyEffects ||
                                             settings.EffectPreset is not (WwiseEditorEffectPreset.Inherit or
                                                 WwiseEditorEffectPreset.None);
            AttenuationCheckBox.IsEnabled = settings.CanApplyAttenuation || settings.Attenuation != false;
            StopEventCheckBox.IsChecked = settings.StopEventExists;
            StopEventCheckBox.IsEnabled = !settings.StopEventExists && settings.CanCreateStopEvent;
            StopEventCheckBox.Content = settings.StopEventExists
                ? settings.IsBankWide
                    ? "A shared Stop event already covers all bank audio"
                    : "A matching Stop event already covers this event's audio"
                : settings.IsBankWide
                    ? "Create one Stop event for all bank audio"
                    : "Create a Stop event for this event's audio";

            var unavailable = new List<string>();
            if (!settings.CanApplyEffects)
            {
                unavailable.Add("shipped effect presets");
            }
            if (!settings.CanApplyDucking)
            {
                unavailable.Add("music ducking");
            }
            if (!settings.CanApplyAttenuation)
            {
                unavailable.Add("standard attenuation");
            }
            if (unavailable.Count > 0)
            {
                UnavailableText.Text = $"Unavailable for this bank version or target: {string.Join(", ", unavailable)}.";
                UnavailableText.Visibility = Visibility.Visible;
            }

            UpdateDuckingAvailability();
            UpdateAttenuationDisplay();
            _initializing = false;
        }

        private void PopulateOutputBuses(WwiseEditorAudioSettings settings)
        {
            var options = new List<WwiseEditorOutputBusOption>();
            if (!settings.OutputBusId.HasValue)
            {
                options.Add(new WwiseEditorOutputBusOption("Mixed output buses (preserve)", null, null));
            }

            if (!settings.IsBankWide)
            {
                options.Add(new WwiseEditorOutputBusOption("Bank-wide/default (inherit)", 0,
                    settings.EffectiveInheritedOutputBus));
            }

            foreach (string outputBus in WwiseOutputBusOptions.GetOutputBuses(settings.Game))
            {
                if (!settings.IsBankWide && outputBus == WwiseOutputBusOptions.MasterAudioBus)
                {
                    continue;
                }

                options.Add(new WwiseEditorOutputBusOption(outputBus,
                    WwiseOutputBusOptions.GetOutputBusId(outputBus), outputBus));
            }

            if (settings.OutputBusId is uint currentId && currentId != 0 &&
                options.All(option => option.Id != currentId))
            {
                options.Insert(0, new WwiseEditorOutputBusOption($"Unknown bus 0x{currentId:X8} (preserve)",
                    currentId, null));
            }

            OutputBusComboBox.ItemsSource = options;
            OutputBusComboBox.SelectedItem = options.FirstOrDefault(option => option.Id == settings.OutputBusId)
                                                  ?? options[0];
        }

        private void PopulateEffects(WwiseEditorAudioSettings settings)
        {
            CurrentEffectRun.Text = settings.EffectSummary;
            var options = new List<WwiseEditorEffectOption>();
            if (!settings.IsBankWide)
            {
                options.Add(new WwiseEditorEffectOption("Bank-wide/default (inherit)",
                    WwiseEditorEffectPreset.Inherit));
            }
            options.Add(new WwiseEditorEffectOption(
                settings.IsBankWide ? "No effects" : "No effects (override inherited)",
                WwiseEditorEffectPreset.None));
            if (settings.Game == MEGame.LE2)
            {
                options.Add(new WwiseEditorEffectOption("LE2 radio", WwiseEditorEffectPreset.Le2Radio));
                options.Add(new WwiseEditorEffectOption("Helmet", WwiseEditorEffectPreset.Le2Helmet));
                options.Add(new WwiseEditorEffectOption("Illusive Man hologram", WwiseEditorEffectPreset.Le2Hologram));
            }
            else
            {
                options.Add(new WwiseEditorEffectOption("BioWare radio", WwiseEditorEffectPreset.BioWareRadio));
                options.Add(new WwiseEditorEffectOption("Hackett QEC", WwiseEditorEffectPreset.Qec));
                options.Add(new WwiseEditorEffectOption("Helmet", WwiseEditorEffectPreset.Helmet));
            }
            options.Add(new WwiseEditorEffectOption("Dual_Filters_Radio_Comm", WwiseEditorEffectPreset.FactoryRadio));

            if (settings.EffectPreset == WwiseEditorEffectPreset.Preserve)
            {
                options.Insert(0, new WwiseEditorEffectOption("Mixed/custom effects (preserve)",
                    WwiseEditorEffectPreset.Preserve));
            }

            EffectPresetComboBox.ItemsSource = options;
            EffectPresetComboBox.SelectedItem = options.First(option => option.Preset == settings.EffectPreset);
        }

        private void OutputBusComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initializing)
            {
                OutputBusWasEdited = true;
            }
            UpdateDuckingAvailability();
        }

        private void UpdateDuckingAvailability()
        {
            if (DuckAudioCheckBox == null)
            {
                return;
            }

            string outputBus = (OutputBusComboBox?.SelectedItem as WwiseEditorOutputBusOption)?.ResolvedName;
            bool busSupportsDucking = WwiseOutputBusOptions.SupportsMusicDucking(_settings.Game, outputBus);
            DuckAudioCheckBox.IsEnabled = _settings.DuckAudio != false ||
                                          _settings.CanApplyDucking && busSupportsDucking;
            if (!_initializing && !DuckAudioCheckBox.IsEnabled)
            {
                DuckAudioCheckBox.IsChecked = false;
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
            if (!_initializing)
            {
                VolumeWasEdited = true;
            }
            SetValidationState(true, null);
        }

        private void VolumeTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updatingVolume || VolumeSlider == null)
            {
                return;
            }

            if (!double.TryParse(VolumeTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double volume))
            {
                SetValidationState(false, "Enter a valid number using a period as the decimal separator.");
                return;
            }

            if (volume < VolumeSlider.Minimum || volume > VolumeSlider.Maximum)
            {
                SetValidationState(false,
                    $"Volume must be between {VolumeSlider.Minimum:0.##} and {VolumeSlider.Maximum:0.##} dB.");
                return;
            }

            _updatingVolume = true;
            VolumeSlider.Value = volume;
            _updatingVolume = false;
            if (!_initializing)
            {
                VolumeWasEdited = true;
            }
            SetValidationState(true, null);
        }

        private void AttenuationScaleSlider_ValueChanged(object sender,
            RoutedPropertyChangedEventArgs<double> e) => UpdateAttenuationDisplay();

        private void UpdateAttenuationDisplay()
        {
            if (AttenuationScaleValueTextBlock == null || AttenuationScaleSlider == null)
            {
                return;
            }

            double maximumDistance = WwiseBankEffectPresets.StandardAttenuationOriginalMaxDistance *
                                     AttenuationScaleSlider.Value / 100d;
            AttenuationScaleValueTextBlock.Text =
                $"{AttenuationScaleSlider.Value:0}% ({maximumDistance:0.#} max)";
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

        private void ApplyButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
