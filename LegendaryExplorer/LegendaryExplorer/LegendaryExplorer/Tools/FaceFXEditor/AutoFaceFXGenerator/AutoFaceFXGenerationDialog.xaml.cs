using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using Microsoft.Win32;
using static LegendaryExplorer.UserControls.ExportLoaderControls.FaceFXAnimSetEditorControl;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

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
        private readonly MEGame _game;

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

        // FXA file support (animation curves)
        private string _fxaFilePath;
        public string FxaFilePath
        {
            get => _fxaFilePath;
            set 
            { 
                _fxaFilePath = value; 
                OnPropertyChanged();
                ValidateFxaFile();
            }
        }

        private string _fxaStatusText = "No FXA file loaded";
        public string FxaStatusText
        {
            get => _fxaStatusText;
            set { _fxaStatusText = value; OnPropertyChanged(); }
        }

        private Brush _fxaStatusColor = Brushes.Gray;
        public Brush FxaStatusColor
        {
            get => _fxaStatusColor;
            set { _fxaStatusColor = value; OnPropertyChanged(); }
        }

        // FXT file support (phoneme timing)
        private string _fxtFilePath;
        public string FxtFilePath
        {
            get => _fxtFilePath;
            set 
            { 
                _fxtFilePath = value; 
                OnPropertyChanged();
                ValidateFxtFile();
            }
        }

        private string _fxtStatusText = "No FXT file loaded";
        public string FxtStatusText
        {
            get => _fxtStatusText;
            set { _fxtStatusText = value; OnPropertyChanged(); }
        }

        private Brush _fxtStatusColor = Brushes.Gray;
        public Brush FxtStatusColor
        {
            get => _fxtStatusColor;
            set { _fxtStatusColor = value; OnPropertyChanged(); }
        }

        private bool _useTextAnalysis = true;
        public bool UseTextAnalysis
        {
            get => _useTextAnalysis;
            set { _useTextAnalysis = value; OnPropertyChanged(); }
        }

        // Parsed data
        private FxaAnimationData _fxaData;
        private FxaAnimationData _fxtData;

        // Generation options
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

        private IReadOnlyList<FaceFXEmotionChoice> _availableEmotions =
            FaceFXEmotionCatalog.GetForSpecies(FaceFXSpecies.HumanFemale);
        public IReadOnlyList<FaceFXEmotionChoice> AvailableEmotions
        {
            get => _availableEmotions;
            private set { _availableEmotions = value; OnPropertyChanged(); }
        }

        private FaceFXEmotionChoice _selectedEmotion;
        public FaceFXEmotionChoice SelectedEmotion
        {
            get => _selectedEmotion;
            set { _selectedEmotion = value; OnPropertyChanged(); }
        }

        private float _emotionIntensity = 0.5f;
        public float EmotionIntensity
        {
            get => _emotionIntensity;
            set { _emotionIntensity = value; OnPropertyChanged(); }
        }

        private bool _addEmotionToExistingLine;
        public bool AddEmotionToExistingLine
        {
            get => _addEmotionToExistingLine;
            set
            {
                _addEmotionToExistingLine = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(GenerationWarningText));
            }
        }

        public string GenerationWarningText => AddEmotionToExistingLine
            ? "Only the selected emotion will be added or replaced; all existing line animation is preserved."
            : "Existing lip-sync curves on this line will be replaced.";

        // Species selection
        public List<string> AvailableSpecies { get; }

        private string _selectedSpecies = "Human Female";
        public string SelectedSpecies
        {
            get => _selectedSpecies;
            set
            {
                _selectedSpecies = value;
                OnPropertyChanged();
                RefreshAvailableEmotions();
            }
        }

        // Result
        public bool WasGenerated { get; private set; }

        public AutoFaceFXGenerationDialog(
            IFaceFXBinary faceFX, 
            LegendaryExplorerCore.Unreal.BinaryConverters.FaceFXLine line, 
            int tlkId, 
            string tlkText, 
            ExportEntry audioExport,
            Window owner = null,
            MEGame game = MEGame.LE3)
        {
            _faceFX = faceFX;
            _line = line;
            _audioExport = audioExport;
            _game = game;
            TLKID = tlkId;
            TLKText = tlkText ?? "";
            AvailableSpecies = FaceFXSpeciesCatalog.GetForGame(game)
                .Select(FaceFXSpeciesCatalog.GetDisplayName)
                .ToList();

            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
            DataContext = this;
            RefreshAvailableEmotions();
            SelectedEmotion = AvailableEmotions[0];

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

        private void BrowseFxaButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select FXA File (Animation Curves)",
                Filter = "FXA Files (*.fxa)|*.fxa|XML Files (*.xml)|*.xml|All Files (*.*)|*.*",
                CheckFileExists = true
            };

            if (DirectoryMemory.ShowDialog(dialog) == true)
            {
                FxaFilePath = dialog.FileName;
            }
        }

        private void BrowseFxtButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select FXT File (Phoneme Timing)",
                Filter = "FXT Files (*.fxt)|*.fxt|Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                CheckFileExists = true
            };

            if (DirectoryMemory.ShowDialog(dialog) == true)
            {
                FxtFilePath = dialog.FileName;
            }
        }

        private void ValidateFxaFile()
        {
            if (string.IsNullOrEmpty(_fxaFilePath))
            {
                FxaStatusText = "No FXA file loaded";
                FxaStatusColor = Brushes.Gray;
                _fxaData = null;
                return;
            }

            try
            {
                _fxaData = FxaXmlParser.ParseFxaFile(_fxaFilePath);

                if (_fxaData != null && _fxaData.Animations.Count > 0)
                {
                    FxaStatusText = $"✓ Loaded {_fxaData.Animations.Count} animation curves";
                    FxaStatusColor = Brushes.Green;
                }
                else
                {
                    FxaStatusText = "⚠ File loaded but no animation curves found";
                    FxaStatusColor = Brushes.Orange;
                    _fxaData = null;
                }
            }
            catch (Exception ex)
            {
                FxaStatusText = $"✗ {ex.Message}";
                FxaStatusColor = Brushes.Red;
                _fxaData = null;
            }
        }

        private void ValidateFxtFile()
        {
            if (string.IsNullOrEmpty(_fxtFilePath))
            {
                FxtStatusText = "No FXT file loaded";
                FxtStatusColor = Brushes.Gray;
                _fxtData = null;
                return;
            }

            try
            {
                _fxtData = FxaXmlParser.ParseFxtFile(_fxtFilePath);

                if (_fxtData != null && _fxtData.PhonemeEvents.Count > 0)
                {
                    FxtStatusText = $"✓ Loaded {_fxtData.PhonemeEvents.Count} phoneme events";
                    FxtStatusColor = Brushes.Green;
                }
                else if (_fxtData != null && _fxtData.Animations.Count > 0)
                {
                    FxtStatusText = $"✓ Converted to {_fxtData.Animations.Count} animation curves";
                    FxtStatusColor = Brushes.Green;
                }
                else
                {
                    FxtStatusText = "⚠ File loaded but no phoneme data found";
                    FxtStatusColor = Brushes.Orange;
                    _fxtData = null;
                }
            }
            catch (Exception ex)
            {
                FxtStatusText = $"✗ {ex.Message}";
                FxtStatusColor = Brushes.Red;
                _fxtData = null;
            }
        }

        private void GenerateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (AddEmotionToExistingLine && (SelectedEmotion == null || SelectedEmotion.IsNone))
                {
                    MessageBox.Show("Select an emotion to add to the existing line.", "Emotion Required",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Parse the selected species
                FaceFXSpecies species = FaceFXSpeciesCatalog.FromDisplayName(SelectedSpecies);

                var options = new FaceFXGenerationOptions
                {
                    Game = _game,
                    CharacterType = CharacterType.HumanFemale,
                    Species = species,
                    GenerateJawAnimation = true,
                    GenerateBlinkAnimation = GenerateBlinkAnimation,
                    GenerateEyebrowAnimation = GenerateEyebrowAnimation,
                    GenerateHeadMovement = GenerateHeadMovement,
                    LipSyncIntensity = LipSyncIntensity,
                    BlinkFrequency = BlinkFrequency,
                    UseAudioAmplitude = true,
                    EmotionChoice = SelectedEmotion,
                    EmotionIntensity = EmotionIntensity,
                    AddEmotionToExistingLine = AddEmotionToExistingLine,
                    FxaData = CombineFxaAndFxtData(),
                    UseTextFallback = UseTextAnalysis
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
                        errorMessage += "\n\nPlease provide dialogue text for lip sync generation.";
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

        private void RefreshAvailableEmotions()
        {
            FaceFXSpecies species = FaceFXSpeciesCatalog.FromDisplayName(SelectedSpecies);

            string previousDisplayName = SelectedEmotion?.DisplayName;
            AvailableEmotions = FaceFXEmotionCatalog.GetForSpecies(species, _game);
            SelectedEmotion = AvailableEmotions.FirstOrDefault(emotion =>
                                  emotion.DisplayName == previousDisplayName)
                              ?? AvailableEmotions[0];
        }

        /// <summary>
        /// Combine FXA animation curves with FXT phoneme timing data
        /// </summary>
        private FxaAnimationData CombineFxaAndFxtData()
        {
            // If neither file is loaded, return null
            if (_fxaData == null && _fxtData == null)
                return null;

            // If only one is loaded, return that one
            if (_fxaData == null)
                return _fxtData;
            if (_fxtData == null)
                return _fxaData;

            // Combine both datasets
            var combined = new FxaAnimationData();

            // Start with FXA animations (these are the primary curves)
            foreach (var kvp in _fxaData.Animations)
            {
                combined.Animations[kvp.Key] = kvp.Value;
            }

            // Merge in FXT-generated animations
            // If FXT has animations that FXA doesn't have, add them
            // If both have the same animation, prefer FXA but blend with FXT
            foreach (var kvp in _fxtData.Animations)
            {
                if (!combined.Animations.ContainsKey(kvp.Key))
                {
                    // FXT has this animation but FXA doesn't - add it
                    combined.Animations[kvp.Key] = kvp.Value;
                }
                // If FXA already has this animation, we keep FXA's version
                // (FXA is considered more authoritative)
            }

            // Copy phoneme events for reference
            combined.PhonemeEvents.AddRange(_fxtData.PhonemeEvents);

            // Copy phoneme mapping
            foreach (var kvp in _fxaData.PhonemeMapping)
            {
                combined.PhonemeMapping[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in _fxtData.PhonemeMapping)
            {
                if (!combined.PhonemeMapping.ContainsKey(kvp.Key))
                {
                    combined.PhonemeMapping[kvp.Key] = kvp.Value;
                }
            }

            return combined;
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
