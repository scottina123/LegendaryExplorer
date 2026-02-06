using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using static LegendaryExplorer.UserControls.ExportLoaderControls.FaceFXAnimSetEditorControl;

namespace LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator
{
    /// <summary>
    /// Dialog for configuring auto FaceFX generation
    /// </summary>
    public partial class AutoFaceFXGenerationDialog : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        // Input data
        private readonly IFaceFXBinary _faceFX;
        private readonly LegendaryExplorerCore.Unreal.BinaryConverters.FaceFXLine _line;
        private readonly ExportEntry _audioExport;

        // Properties for binding
        public string LineName => _line?.NameAsString ?? "Unknown";
        public int TLKID { get; }

        private string _tlkText;
        public string TLKText
        {
            get => _tlkText;
            set { _tlkText = value; OnPropertyChanged(); }
        }

        private string _audioDurationText;
        public string AudioDurationText
        {
            get => _audioDurationText;
            set { _audioDurationText = value; OnPropertyChanged(); }
        }

        // Character type
        public List<string> CharacterTypes { get; } = new() { "Human Female", "Human Male" };
        
        private string _selectedCharacterType = "Human Female";
        public string SelectedCharacterType
        {
            get => _selectedCharacterType;
            set { _selectedCharacterType = value; OnPropertyChanged(); }
        }

        // Generation options
        public bool GenerateLipSync { get; } = true; // Always true, shown for info

        private bool _generateJawAnimation = true;
        public bool GenerateJawAnimation
        {
            get => _generateJawAnimation;
            set { _generateJawAnimation = value; OnPropertyChanged(); }
        }

        private bool _generateBlinkAnimation = true;
        public bool GenerateBlinkAnimation
        {
            get => _generateBlinkAnimation;
            set { _generateBlinkAnimation = value; OnPropertyChanged(); }
        }

        private bool _generateEyebrowAnimation = true;
        public bool GenerateEyebrowAnimation
        {
            get => _generateEyebrowAnimation;
            set { _generateEyebrowAnimation = value; OnPropertyChanged(); }
        }

        private bool _generateHeadMovement = false;
        public bool GenerateHeadMovement
        {
            get => _generateHeadMovement;
            set { _generateHeadMovement = value; OnPropertyChanged(); }
        }

        private bool _useAudioAmplitude = true;
        public bool UseAudioAmplitude
        {
            get => _useAudioAmplitude;
            set { _useAudioAmplitude = value; OnPropertyChanged(); }
        }

        private float _lipSyncIntensity = 1.0f;
        public float LipSyncIntensity
        {
            get => _lipSyncIntensity;
            set { _lipSyncIntensity = value; OnPropertyChanged(); }
        }

        private float _blinkFrequency = 0.2f;
        public float BlinkFrequency
        {
            get => _blinkFrequency;
            set { _blinkFrequency = value; OnPropertyChanged(); }
        }

        // Result
        public bool WasGenerated { get; private set; }

        public AutoFaceFXGenerationDialog(
            IFaceFXBinary faceFX, 
            LegendaryExplorerCore.Unreal.BinaryConverters.FaceFXLine line, 
            int tlkId, 
            string tlkText, 
            ExportEntry audioExport,
            Window owner = null)
        {
            _faceFX = faceFX;
            _line = line;
            _audioExport = audioExport;
            TLKID = tlkId;
            TLKText = tlkText ?? "";

            InitializeComponent();
            DataContext = this;

            if (owner != null)
            {
                Owner = owner;
            }

            // Get audio duration
            float duration = AudioAnalyzer.GetAudioDuration(audioExport);
            if (duration > 0)
            {
                AudioDurationText = $"{duration:F2} seconds";
            }
            else
            {
                AudioDurationText = "Unable to determine (will estimate from text)";
            }
        }

        private void GenerateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var options = new FaceFXGenerationOptions
                {
                    CharacterType = SelectedCharacterType == "Human Female" 
                        ? CharacterType.HumanFemale 
                        : CharacterType.HumanMale,
                    GenerateJawAnimation = GenerateJawAnimation,
                    GenerateBlinkAnimation = GenerateBlinkAnimation,
                    GenerateEyebrowAnimation = GenerateEyebrowAnimation,
                    GenerateHeadMovement = GenerateHeadMovement,
                    LipSyncIntensity = LipSyncIntensity,
                    BlinkFrequency = BlinkFrequency,
                    UseAudioAmplitude = UseAudioAmplitude
                };

                var generator = new FaceFXGenerator(_faceFX, _line, TLKText, _audioExport, options);
                bool success = generator.Generate();

                if (success)
                {
                    WasGenerated = true;
                    DialogResult = true;
                    Close();
                }
                else
                {
                    string errorMessage = "Failed to generate FaceFX animations.";
                    if (!string.IsNullOrEmpty(generator.LastError))
                    {
                        errorMessage += $"\n\nError: {generator.LastError}";
                    }
                    else
                    {
                        errorMessage += "\n\nPlease check that:\n• A line is selected\n• The TLK text is not empty\n• The FaceFX data is valid";
                    }
                    MessageBox.Show(errorMessage, "Generation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred during generation:\n\n{ex.Message}\n\n{ex.StackTrace}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
