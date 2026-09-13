using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
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
        private readonly bool _emotionsOnly;
        private readonly float _audioDuration;
        private FaceFXGenerationDraft _generationDraft;

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
                _addEmotionToExistingLine = _emotionsOnly || value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(GenerationWarningText));
                OnPropertyChanged(nameof(ShowLipSyncOptions));
                OnPropertyChanged(nameof(GenerateButtonText));
            }
        }

        public string GenerationWarningText => AddEmotionToExistingLine
            ? UseCustomEmotionRange
                ? "Adds the selected emotion within this interval. Other tracks and emotion keys outside the interval are preserved."
                : "Adds or replaces the selected emotion across the line. Other animation tracks are preserved."
            : "Existing lip-sync curves on this line will be replaced.";

        public bool ShowLipSyncOptions => !AddEmotionToExistingLine;
        public bool CanChooseGenerationMode => !_emotionsOnly;
        public string DialogTitle => _emotionsOnly ? "Add Emotions Only" : "Auto FaceFX Generation";
        public string DialogDescription => _emotionsOnly
            ? "Insert an expression and preview it on a head mesh with the dialogue audio."
            : "Generate lip sync and preview timed emotional expressions.";
        public string GenerateButtonText => AddEmotionToExistingLine ? "Apply Emotion" : "Generate";

        private bool _useCustomEmotionRange;
        public bool UseCustomEmotionRange
        {
            get => _useCustomEmotionRange;
            set
            {
                _useCustomEmotionRange = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(GenerationWarningText));
            }
        }

        private string _emotionStartTimeText = "0.00";
        public string EmotionStartTimeText
        {
            get => _emotionStartTimeText;
            set { _emotionStartTimeText = value; OnPropertyChanged(); }
        }

        private string _emotionEndTimeText = "2.00";
        public string EmotionEndTimeText
        {
            get => _emotionEndTimeText;
            set { _emotionEndTimeText = value; OnPropertyChanged(); }
        }

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
        internal ExportEntry PreviewHeadMesh => emotionPreview.SelectedHeadMesh;
        internal FaceFXAsset PreviewRig => emotionPreview.SelectedRig;

        public AutoFaceFXGenerationDialog(
            IFaceFXBinary faceFX, 
            LegendaryExplorerCore.Unreal.BinaryConverters.FaceFXLine line, 
            int tlkId, 
            string tlkText, 
            ExportEntry audioExport,
            Window owner = null,
            MEGame game = MEGame.LE3,
            bool emotionsOnly = false,
            FaceFXAsset previewActor = null,
            ExportEntry previewMesh = null,
            Func<Window, FaceFXAsset> previewRigPicker = null,
            double initialPosition = 0)
        {
            _faceFX = faceFX;
            _line = line;
            _audioExport = audioExport;
            _game = game;
            _emotionsOnly = emotionsOnly;
            _addEmotionToExistingLine = emotionsOnly;
            _useCustomEmotionRange = emotionsOnly;
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
            _audioDuration = AudioAnalyzer.GetAudioDuration(audioExport);
            float duration = _audioDuration > 0 ? _audioDuration
                : line.Points.Select(point => point.time).DefaultIfEmpty().Max();
            if (duration <= 0) duration = FaceFXGenerator.EstimateDurationFromText(TLKText);
            double start = Math.Clamp(double.IsFinite(initialPosition) ? initialPosition : 0, 0, Math.Max(0, duration - 0.02));
            EmotionStartTimeText = start.ToString("F2", CultureInfo.CurrentCulture);
            EmotionEndTimeText = Math.Min(duration, start + 1.5).ToString("F2", CultureInfo.CurrentCulture);
            emotionPreview.Initialize(game, new FaceFXGenerationDraft(faceFX, line, game), audioExport,
                duration, previewActor, previewMesh, previewRigPicker);
            emotionPreview.Position = start;
            if (_audioDuration > 0)
            {
                AudioDurationText = $"{_audioDuration:F2} seconds";
            }
            else
            {
                AudioDurationText = $"{duration:F2} seconds (line/text estimate)";
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
                if (TryGenerateDraft(out var draft, out string error))
                {
                    draft.ApplyTo(_faceFX, _line);
                    WasGenerated = true;
                    DialogResult = true;
                    Close();
                }
                else
                {
                    MessageBox.Show(this, error, "Generation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred during generation:\n\n{ex.Message}\n\n{ex.StackTrace}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        internal bool TryGenerateDraft(out FaceFXGenerationDraft draft, out string error)
        {
            draft = null;
            error = null;
            bool hasEmotion = SelectedEmotion != null && !SelectedEmotion.IsNone;
            if (AddEmotionToExistingLine && (!hasEmotion || !float.IsFinite(EmotionIntensity) || EmotionIntensity <= 0f))
            {
                error = "Select an emotion and an intensity above zero.";
                return false;
            }

            float? start = null;
            float? end = null;
            if (UseCustomEmotionRange && hasEmotion)
            {
                if (!float.TryParse(EmotionStartTimeText, NumberStyles.Float, CultureInfo.CurrentCulture, out float startValue)
                    || !float.TryParse(EmotionEndTimeText, NumberStyles.Float, CultureInfo.CurrentCulture, out float endValue)
                    || !float.IsFinite(startValue) || !float.IsFinite(endValue) || startValue < 0f || endValue <= startValue)
                {
                    error = "Enter a non-negative start time and an end time after the start (in seconds).";
                    return false;
                }
                if (_audioDuration > 0f && endValue > _audioDuration + 0.005f)
                {
                    error = $"The emotion must fit within the {_audioDuration:F2}-second audio track.";
                    return false;
                }
                start = startValue;
                end = _audioDuration > 0f ? Math.Min(endValue, _audioDuration) : endValue;
                if (end <= start)
                {
                    error = "The start must be before the end of the audio track.";
                    return false;
                }
            }

            if (_generationDraft != null)
            {
                draft = _generationDraft;
                return true;
            }
            var options = new FaceFXGenerationOptions
            {
                Game = _game,
                CharacterType = CharacterType.HumanFemale,
                Species = FaceFXSpeciesCatalog.FromDisplayName(SelectedSpecies),
                GenerateJawAnimation = true,
                GenerateBlinkAnimation = GenerateBlinkAnimation,
                GenerateEyebrowAnimation = GenerateEyebrowAnimation,
                GenerateHeadMovement = GenerateHeadMovement,
                LipSyncIntensity = LipSyncIntensity,
                BlinkFrequency = BlinkFrequency,
                UseAudioAmplitude = true,
                EmotionChoice = SelectedEmotion,
                EmotionIntensity = EmotionIntensity,
                EmotionStartTime = start,
                EmotionEndTime = end,
                AddEmotionToExistingLine = AddEmotionToExistingLine,
                FxaData = CombineFxaAndFxtData(),
                UseTextFallback = UseTextAnalysis
            };
            var candidate = new FaceFXGenerationDraft(_faceFX, _line, _game);
            var generator = new FaceFXGenerator(candidate, candidate.Line, TLKText, _audioExport, options);
            if (!generator.Generate())
            {
                error = generator.LastError ?? "Failed to generate FaceFX animations.";
                return false;
            }
            draft = _generationDraft = candidate;
            return true;
        }

        private void PreviewEmotion_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedEmotion == null || SelectedEmotion.IsNone)
            {
                MessageBox.Show(this, "Select an emotion to preview.", "Emotion Preview");
                return;
            }
            if (!TryGenerateDraft(out var draft, out string error))
            {
                MessageBox.Show(this, error, "Emotion Preview");
                return;
            }
            emotionPreview.SetDraft(draft);
            double start = UseCustomEmotionRange ? double.Parse(EmotionStartTimeText, CultureInfo.CurrentCulture) : 0;
            emotionPreview.PlayFrom(Math.Max(0, start - 0.2));
        }

        private void UsePlayheadForStart_Click(object sender, RoutedEventArgs e)
        {
            UseCustomEmotionRange = true;
            EmotionStartTimeText = emotionPreview.Position.ToString("F2", CultureInfo.CurrentCulture);
        }

        private void UsePlayheadForEnd_Click(object sender, RoutedEventArgs e)
        {
            UseCustomEmotionRange = true;
            EmotionEndTimeText = emotionPreview.Position.ToString("F2", CultureInfo.CurrentCulture);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Release meshes before the viewport's window-closing handler releases its device.
            emotionPreview.Dispose();
            base.OnClosing(e);
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
            _generationDraft = null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
