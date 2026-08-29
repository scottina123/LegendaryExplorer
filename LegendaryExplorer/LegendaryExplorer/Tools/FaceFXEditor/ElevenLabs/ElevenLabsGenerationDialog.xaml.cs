using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LegendaryExplorer.SharedUI;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Tools.FaceFXEditor.ElevenLabs
{
    public sealed record ElevenLabsLineSeed(int TlkId, string Text);
    public sealed record ElevenLabsSpeechPrompt(string Text, int TrimPrefixCharacterCount)
    {
        public bool RequiresPrefixTrim => TrimPrefixCharacterCount > 0;
    }

    public partial class ElevenLabsGenerationDialog : Window, INotifyPropertyChanged
    {
        private readonly bool _isFemaleAsset;
        private readonly ElevenLabsPreferences _preferences;
        private readonly MediaPlayer _mediaPlayer = new();
        private CancellationTokenSource _cancellationTokenSource;
        private ElevenLabsApiClient _client;
        private ElevenLabsSubscription _subscription;
        private bool _suppressSelectionEvents;
        private bool _isBusy;
        private bool _isConnected;
        private ElevenLabsVoice _selectedVoice;
        private ElevenLabsModel _selectedModel;
        private ElevenLabsLanguage _selectedLanguage;
        private string _accountStatusText = "Enter an API key and connect to load voices, models, and credits.";
        private string _creditsText = "Credits not loaded";
        private string _statusText;
        private string _outputFolder;
        private double _stability = 0.5d;
        private double _similarityBoost = 0.75d;
        private double _style;
        private bool _useSpeakerBoost = true;
        private double _speed = 1d;
        private string _applyTextNormalization = "auto";
        private bool _applyLanguageTextNormalization;
        private bool _useAdjacentTextContext = true;
        private bool _enableLogging = true;
        private int _optimizeStreamingLatency;
        private string _seedText;
        private bool _mirrorOppositeGender;
        private int _nextTlkId = 1;
        private string _bulkEmotion = "Neutral";
        private string _bulkAccent = "None";

        private static readonly IReadOnlyDictionary<string, string> EmotionTags =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Neutral"] = null,
                ["Angry"] = "[angry]",
                ["Curious"] = "[curious]",
                ["Crying"] = "[crying]",
                ["Excited"] = "[excited]",
                ["Flirty"] = "[flirty]",
                ["Happy"] = "[happily]",
                ["Mischievous"] = "[mischievously]",
                ["Romantic"] = "[romantic]",
                ["Sad"] = "[sad]",
                ["Sarcastic"] = "[sarcastic]",
                ["Sorrowful"] = "[sorrowful]",
                ["Tired"] = "[tired]",
                ["Worried"] = "[worried]"
            };

        private static readonly IReadOnlyDictionary<string, string> AccentTags =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["None"] = null,
                ["American"] = "[strong American accent]",
                ["Australian"] = "[strong Australian accent]",
                ["British"] = "[strong British accent]",
                ["Eastern European"] = "[strong Eastern European accent]",
                ["French"] = "[strong French accent]",
                ["Irish"] = "[strong Irish accent]",
                ["New York"] = "[strong New York accent]",
                ["Romanian"] = "[strong Romanian accent]",
                ["Russian"] = "[strong Russian accent]",
                ["Scottish"] = "[strong Scottish accent]",
                ["Southern American"] = "[strong Southern American accent]"
            };

        public ElevenLabsGenerationDialog(IEnumerable<ElevenLabsLineSeed> initialLines, int selectedTlkId,
            bool isFemaleAsset, Window owner = null)
        {
            _isFemaleAsset = isFemaleAsset;
            _preferences = ElevenLabsPreferencesStore.Load();

            InitializeComponent();
            DataContext = this;
            Owner = owner;
            CustomWindowChrome.ApplyCustomChrome(this);

            RememberApiKeyCheckBox.IsChecked = _preferences.RememberApiKey;
            ApiKeyPasswordBox.Password = ElevenLabsPreferencesStore.TryDecryptApiKey(_preferences) ?? string.Empty;
            ApplyPreferences();

            var seedLines = (initialLines ?? []).Where(seed => seed.TlkId > 0)
                         .GroupBy(seed => seed.TlkId)
                         .Select(group => group.First())
                         .ToList();
            int maximumSeedTlkId = seedLines.Select(seed => seed.TlkId).DefaultIfEmpty(selectedTlkId).Max();
            _nextTlkId = maximumSeedTlkId is > 0 and < int.MaxValue ? maximumSeedTlkId + 1 : 1;
            foreach (var seed in seedLines)
            {
                AddLine(new ElevenLabsLineItem
                {
                    TlkId = seed.TlkId,
                    Text = seed.Text ?? string.Empty
                });
            }

            if (Lines.Count == 0)
            {
                AddLine(new ElevenLabsLineItem { TlkId = TakeNextTlkId() });
            }

            Lines.CollectionChanged += Lines_CollectionChanged;
            Loaded += ElevenLabsGenerationDialog_Loaded;
            UpdateBatchState();
        }

        public ObservableCollection<ElevenLabsVoice> Voices { get; } = [];
        public ObservableCollection<ElevenLabsModel> Models { get; } = [];
        public ObservableCollection<ElevenLabsLanguage> Languages { get; } = [];
        public ObservableCollection<ElevenLabsLineItem> Lines { get; } = [];
        public IReadOnlyList<string> TextNormalizationOptions { get; } = ["auto", "on", "off"];
        public IReadOnlyList<string> EmotionOptions { get; } = EmotionTags.Keys.ToList();
        public IReadOnlyList<string> AccentOptions { get; } = AccentTags.Keys.ToList();
        public IReadOnlyList<string> SelectedAudioFiles { get; private set; } = [];
        public IReadOnlyDictionary<int, string> SelectedTextsByTlkId { get; private set; } =
            new Dictionary<int, string>();

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(CanConnect));
                    OnPropertyChanged(nameof(CanGenerate));
                    OnPropertyChanged(nameof(CanImport));
                    OnPropertyChanged(nameof(CanClose));
                    OnPropertyChanged(nameof(CanEditLines));
                    OnPropertyChanged(nameof(BusyVisibility));
                }
            }
        }

        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                if (SetProperty(ref _isConnected, value))
                {
                    OnPropertyChanged(nameof(CanGenerate));
                }
            }
        }

        public bool CanConnect => !IsBusy;
        public bool CanClose => !IsBusy;
        public bool CanEditLines => !IsBusy;
        public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
        public bool CanGenerate => IsConnected && !IsBusy && SelectedVoice != null && SelectedModel != null &&
                                   Lines.Count > 0;
        public bool CanImport => !IsBusy && Lines.Count > 0 &&
                                 Lines.All(line => line.SelectedTakePath != null);
        public bool HasOutputFolder => !string.IsNullOrWhiteSpace(OutputFolder) && Directory.Exists(OutputFolder);

        public ElevenLabsVoice SelectedVoice
        {
            get => _selectedVoice;
            set
            {
                if (SetProperty(ref _selectedVoice, value))
                {
                    OnPropertyChanged(nameof(CanGenerate));
                }
            }
        }

        public ElevenLabsModel SelectedModel
        {
            get => _selectedModel;
            set
            {
                if (SetProperty(ref _selectedModel, value))
                {
                    OnPropertyChanged(nameof(CanUseStyle));
                    OnPropertyChanged(nameof(CanUseSimilarity));
                    OnPropertyChanged(nameof(CanUseSpeakerBoost));
                    OnPropertyChanged(nameof(CanUseLatencyOptimization));
                    OnPropertyChanged(nameof(IsElevenV3));
                    OnPropertyChanged(nameof(SelectedModelDescription));
                    OnPropertyChanged(nameof(CanGenerate));
                    UpdateBatchState();
                }
            }
        }

        public ElevenLabsLanguage SelectedLanguage
        {
            get => _selectedLanguage;
            set => SetProperty(ref _selectedLanguage, value);
        }

        public bool CanUseStyle => SelectedModel?.CanUseStyle == true;
        public bool CanUseSimilarity => SelectedModel != null && !IsElevenV3;
        public bool CanUseSpeakerBoost => SelectedModel?.CanUseSpeakerBoost == true;
        public bool CanUseLatencyOptimization => SelectedModel != null && !IsElevenV3;
        public bool IsElevenV3 => string.Equals(SelectedModel?.ModelId, "eleven_v3",
            StringComparison.OrdinalIgnoreCase);
        public string SelectedModelDescription
        {
            get
            {
                if (SelectedModel == null)
                {
                    return string.Empty;
                }

                var capabilities = new List<string>();
                if (IsElevenV3) capabilities.Add("similarity and deprecated latency optimization are unavailable");
                if (!SelectedModel.CanUseStyle) capabilities.Add("style is unavailable");
                if (!SelectedModel.CanUseSpeakerBoost) capabilities.Add("speaker boost is unavailable");
                string suffix = capabilities.Count == 0 ? string.Empty : $" ({string.Join("; ", capabilities)})";
                return (SelectedModel.Description ?? string.Empty) + suffix;
            }
        }

        public string AccountStatusText
        {
            get => _accountStatusText;
            private set => SetProperty(ref _accountStatusText, value);
        }

        public string CreditsText
        {
            get => _creditsText;
            private set => SetProperty(ref _creditsText, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        public string OutputFolder
        {
            get => _outputFolder;
            private set
            {
                if (SetProperty(ref _outputFolder, value))
                {
                    OnPropertyChanged(nameof(OutputFolderText));
                    OnPropertyChanged(nameof(HasOutputFolder));
                }
            }
        }

        public string OutputFolderText => string.IsNullOrWhiteSpace(OutputFolder)
            ? string.Empty
            : $"Generated audio is retained at: {OutputFolder}";

        public double Stability { get => _stability; set => SetProperty(ref _stability, value); }
        public double SimilarityBoost { get => _similarityBoost; set => SetProperty(ref _similarityBoost, value); }
        public double Style { get => _style; set => SetProperty(ref _style, value); }
        public bool UseSpeakerBoost { get => _useSpeakerBoost; set => SetProperty(ref _useSpeakerBoost, value); }
        public double Speed { get => _speed; set => SetProperty(ref _speed, value); }
        public string ApplyTextNormalization { get => _applyTextNormalization; set => SetProperty(ref _applyTextNormalization, value); }
        public bool ApplyLanguageTextNormalization { get => _applyLanguageTextNormalization; set => SetProperty(ref _applyLanguageTextNormalization, value); }
        public bool UseAdjacentTextContext { get => _useAdjacentTextContext; set => SetProperty(ref _useAdjacentTextContext, value); }
        public bool EnableLogging { get => _enableLogging; set => SetProperty(ref _enableLogging, value); }
        public int OptimizeStreamingLatency { get => _optimizeStreamingLatency; set => SetProperty(ref _optimizeStreamingLatency, value); }
        public string SeedText { get => _seedText; set => SetProperty(ref _seedText, value); }
        public bool MirrorOppositeGender { get => _mirrorOppositeGender; set => SetProperty(ref _mirrorOppositeGender, value); }
        public string BulkEmotion { get => _bulkEmotion; set => SetProperty(ref _bulkEmotion, value); }
        public string BulkAccent { get => _bulkAccent; set => SetProperty(ref _bulkAccent, value); }

        public string BatchEstimateText
        {
            get
            {
                int lineCount = Lines.Count;
                int characters = Lines.Sum(line => SelectedModel == null
                    ? line.Text?.Length ?? 0
                    : BuildSpeechPrompt(line.Text, line.Emotion, line.Accent, IsElevenV3).Text.Length);
                if (lineCount == 0)
                {
                    return "No lines to generate";
                }

                double multiplier = SelectedModel?.ModelRates?.CharacterCostMultiplier ?? 1d;
                double discount = SelectedModel?.ModelRates?.CostDiscountMultiplier ?? 1d;
                int estimatedCredits = (int)Math.Ceiling(characters * 2d * multiplier * discount);
                return $"{lineCount:N0} line(s), two takes, {characters * 2:N0} submitted characters (~{estimatedCredits:N0} credits)";
            }
        }

        private async void ElevenLabsGenerationDialog_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= ElevenLabsGenerationDialog_Loaded;
            if (!string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password))
            {
                await ConnectAsync(showMissingKeyMessage: false);
            }
            else
            {
                AccountStatusText = "Enter an API key. Future openings connect automatically when the encrypted key is remembered.";
                ApiKeyPasswordBox.Focus();
            }
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e) =>
            await ConnectAsync(showMissingKeyMessage: true);

        private async Task ConnectAsync(bool showMissingKeyMessage)
        {
            string apiKey = ApiKeyPasswordBox.Password?.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                if (showMissingKeyMessage)
                {
                    MessageBox.Show(this, "Enter an ElevenLabs API key first.", "API key required",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }

            IsBusy = true;
            AccountStatusText = "Connecting to ElevenLabs...";
            StatusText = string.Empty;
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                _client?.Dispose();
                _client = new ElevenLabsApiClient(apiKey);
                CancellationToken token = _cancellationTokenSource.Token;
                Task<ElevenLabsSubscription> subscriptionTask = _client.GetSubscriptionAsync(token);
                Task<IReadOnlyList<ElevenLabsModel>> modelsTask = _client.GetModelsAsync(token);
                Task<IReadOnlyList<ElevenLabsVoice>> voicesTask = _client.GetVoicesAsync(token);
                await Task.WhenAll(subscriptionTask, modelsTask, voicesTask);

                _suppressSelectionEvents = true;
                Voices.Clear();
                foreach (var voice in voicesTask.Result) Voices.Add(voice);
                Models.Clear();
                foreach (var model in modelsTask.Result) Models.Add(model);

                SelectedVoice = Voices.FirstOrDefault(voice => voice.VoiceId == _preferences.VoiceId)
                                ?? Voices.FirstOrDefault();
                SelectedModel = Models.FirstOrDefault(model => model.ModelId == _preferences.ModelId)
                                ?? Models.FirstOrDefault(model => model.ModelId == "eleven_v3")
                                ?? Models.FirstOrDefault(model => model.ModelId == "eleven_multilingual_v2")
                                ?? Models.FirstOrDefault();
                ConfigureLanguages(_preferences.LanguageCode);
                _suppressSelectionEvents = false;

                _subscription = subscriptionTask.Result;
                UpdateCreditsText();
                IsConnected = SelectedVoice != null && SelectedModel != null;
                AccountStatusText = IsConnected
                    ? $"Connected. Loaded {Voices.Count:N0} voices and {Models.Count:N0} text-to-speech models."
                    : "Connected, but this API key has no usable voices or text-to-speech models.";

                if (SelectedVoice != null)
                {
                    await LoadVoiceSettingsAsync(SelectedVoice.VoiceId == _preferences.VoiceId, token);
                }

                SavePreferences();
            }
            catch (OperationCanceledException)
            {
                AccountStatusText = "Connection cancelled.";
            }
            catch (Exception exception)
            {
                IsConnected = false;
                AccountStatusText = "Could not connect to ElevenLabs.";
                MessageBox.Show(this, exception.Message, "ElevenLabs connection failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _suppressSelectionEvents = false;
                IsBusy = false;
            }
        }

        private async void VoiceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionEvents || !IsConnected || SelectedVoice == null || IsBusy)
            {
                return;
            }

            IsBusy = true;
            try
            {
                await LoadVoiceSettingsAsync(false, _cancellationTokenSource?.Token ?? CancellationToken.None);
                SavePreferences();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "Could not load voice settings",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionEvents)
            {
                return;
            }

            ConfigureLanguages(null);
            SavePreferences();
        }

        private async Task LoadVoiceSettingsAsync(bool applySavedSettings, CancellationToken cancellationToken)
        {
            if (_client == null || SelectedVoice == null)
            {
                return;
            }

            AccountStatusText = $"Loading settings for {SelectedVoice.Name}...";
            ElevenLabsVoiceSettings settings = await _client.GetVoiceSettingsAsync(SelectedVoice.VoiceId,
                cancellationToken);
            if (applySavedSettings)
            {
                Stability = _preferences.Stability;
                SimilarityBoost = _preferences.SimilarityBoost;
                Style = _preferences.Style;
                UseSpeakerBoost = _preferences.UseSpeakerBoost;
                Speed = _preferences.Speed;
            }
            else
            {
                Stability = settings.Stability ?? 0.5d;
                SimilarityBoost = settings.SimilarityBoost ?? 0.75d;
                Style = settings.Style ?? 0d;
                UseSpeakerBoost = settings.UseSpeakerBoost ?? true;
                Speed = settings.Speed ?? 1d;
            }

            AccountStatusText = $"Connected. Using {SelectedVoice.DisplayName}.";
        }

        private async void GenerateButton_Click(object sender, RoutedEventArgs e)
        {
            LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var linesToGenerate = Lines.ToList();
            if (!TryValidateGeneration(linesToGenerate, out uint? seed))
            {
                return;
            }

            await GenerateLinesAsync(linesToGenerate, seed, isRegeneration: false);
        }

        private async void RegenerateLineButton_Click(object sender, RoutedEventArgs e)
        {
            LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (sender is not Button { DataContext: ElevenLabsLineItem line } ||
                !TryValidateGeneration([line], out uint? seed))
            {
                return;
            }

            await GenerateLinesAsync([line], seed, isRegeneration: true);
        }

        private async Task GenerateLinesAsync(IReadOnlyList<ElevenLabsLineItem> linesToGenerate, uint? seed,
            bool isRegeneration)
        {
            SavePreferences();
            IsBusy = true;
            _mediaPlayer.Stop();
            _mediaPlayer.Close();
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            CancellationToken cancellationToken = _cancellationTokenSource.Token;

            if (string.IsNullOrWhiteSpace(OutputFolder))
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                OutputFolder = CreateUniqueOutputDirectory(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LegendaryExplorer", "FaceFXEditor", "ElevenLabsAudio", timestamp));
            }

            int completedTakes = 0;
            int failedTakes = 0;
            int reportedCreditCost = 0;
            try
            {
                for (int targetIndex = 0; targetIndex < linesToGenerate.Count; targetIndex++)
                {
                    ElevenLabsLineItem line = linesToGenerate[targetIndex];
                    line.ClearTakes();
                    string previousText = null;
                    string nextText = null;
                    int allLinesIndex = Lines.IndexOf(line);
                    if (UseAdjacentTextContext && !SelectedModel.ModelId.Equals("eleven_v3", StringComparison.OrdinalIgnoreCase))
                    {
                        if (allLinesIndex > 0) previousText = Lines[allLinesIndex - 1].Text;
                        if (allLinesIndex >= 0 && allLinesIndex + 1 < Lines.Count) nextText = Lines[allLinesIndex + 1].Text;
                    }

                    ElevenLabsSpeechPrompt prompt = BuildSpeechPrompt(line.Text, line.Emotion, line.Accent,
                        IsElevenV3);
                    for (int take = 1; take <= 2; take++)
                    {
                        line.Status = $"Generating take {take} of 2...";
                        StatusText = $"Line {targetIndex + 1:N0} of {linesToGenerate.Count:N0}, take {take} of 2";
                        try
                        {
                            var request = BuildSpeechRequest(prompt.Text, seed, take, previousText, nextText);
                            byte[] audio;
                            int? creditCost;
                            double? trimStartSeconds = null;
                            if (prompt.RequiresPrefixTrim)
                            {
                                ElevenLabsTimedSpeechResult result = await _client.GenerateSpeechWithTimestampsAsync(
                                    SelectedVoice.VoiceId, request, cancellationToken);
                                audio = result.Audio;
                                creditCost = result.CreditCost;
                                trimStartSeconds = GetTrimStartSeconds(result.Alignment,
                                    prompt.TrimPrefixCharacterCount);
                            }
                            else
                            {
                                ElevenLabsSpeechResult result = await _client.GenerateSpeechAsync(
                                    SelectedVoice.VoiceId, request, cancellationToken);
                                audio = result.Audio;
                                creditCost = result.CreditCost;
                            }

                            if (audio == null || audio.Length == 0)
                            {
                                throw new InvalidDataException("ElevenLabs returned an empty audio file.");
                            }

                            string extension = trimStartSeconds.HasValue ? ".wav" : ".mp3";
                            string path = Path.Combine(OutputFolder,
                                BuildTakeFileName(line.TlkId, _isFemaleAsset, take, extension));
                            if (trimStartSeconds.HasValue)
                            {
                                WriteTrimmedMp3AsPcmWave(audio, trimStartSeconds.Value, path);
                            }
                            else
                            {
                                await File.WriteAllBytesAsync(path, audio, cancellationToken);
                            }
                            line.SetTake(take, path);
                            completedTakes++;
                            reportedCreditCost += creditCost ?? 0;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            failedTakes++;
                            line.Status = $"Take {take} failed: {GetShortMessage(exception)}";
                        }
                    }

                    if (line.HasTake1 && line.HasTake2)
                    {
                        line.Status = "Two takes ready";
                        line.SelectedTakeIndex = 0;
                    }
                    else if (line.HasAnyTake)
                    {
                        line.Status = "One take ready; one failed";
                        line.SelectedTakeIndex = line.HasTake1 ? 0 : 1;
                    }
                }

                try
                {
                    _subscription = await _client.GetSubscriptionAsync(cancellationToken);
                    UpdateCreditsText();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AccountStatusText = $"Generation finished, but credits could not be refreshed: {GetShortMessage(exception)}";
                }

                string costText = reportedCreditCost > 0
                    ? $" ElevenLabs reported {reportedCreditCost:N0} credits used."
                    : string.Empty;
                string operation = isRegeneration ? "Regeneration" : "Generation";
                StatusText = $"{operation} complete: {completedTakes:N0} take(s) ready, {failedTakes:N0} failed.{costText} " +
                             "Audition each pair, choose a take, then import it.";
            }
            catch (OperationCanceledException)
            {
                StatusText = "Generation cancelled. Completed takes remain in the output folder.";
            }
            finally
            {
                IsBusy = false;
                UpdateBatchState();
            }
        }

        private ElevenLabsSpeechRequest BuildSpeechRequest(string text, uint? seed, int take,
            string previousText, string nextText)
        {
            uint? takeSeed = seed.HasValue ? unchecked(seed.Value + (uint)(take - 1)) : null;
            return new ElevenLabsSpeechRequest
            {
                Text = text,
                ModelId = SelectedModel.ModelId,
                LanguageCode = SelectedLanguage?.LanguageId,
                VoiceSettings = new ElevenLabsVoiceSettings
                {
                    Stability = Math.Clamp(Stability, 0d, 1d),
                    SimilarityBoost = CanUseSimilarity ? Math.Clamp(SimilarityBoost, 0d, 1d) : null,
                    Style = CanUseStyle ? Math.Clamp(Style, 0d, 1d) : null,
                    UseSpeakerBoost = CanUseSpeakerBoost ? UseSpeakerBoost : null,
                    Speed = Math.Clamp(Speed, 0.7d, 1.2d)
                },
                Seed = takeSeed,
                PreviousText = previousText,
                NextText = nextText,
                ApplyTextNormalization = ApplyTextNormalization,
                ApplyLanguageTextNormalization = ApplyLanguageTextNormalization,
                EnableLogging = EnableLogging,
                OptimizeStreamingLatency = CanUseLatencyOptimization && OptimizeStreamingLatency > 0
                    ? OptimizeStreamingLatency
                    : null
            };
        }

        private void PlayTakeButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: ElevenLabsLineItem line } button ||
                !int.TryParse(button.Tag?.ToString(), out int take))
            {
                return;
            }

            string path = take == 1 ? line.Take1Path : line.Take2Path;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            _mediaPlayer.Stop();
            _mediaPlayer.Close();
            _mediaPlayer.Open(new Uri(path, UriKind.Absolute));
            _mediaPlayer.Play();
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var selected = Lines.ToList();
            if (selected.Count == 0 || selected.Any(line => line.TlkId <= 0 || line.SelectedTakePath == null))
            {
                MessageBox.Show(this, "Every line needs a valid TLK ID and a chosen generated take.",
                    "Select generated takes", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var duplicates = selected.GroupBy(line => line.TlkId).Where(group => group.Count() > 1)
                .Select(group => group.Key).ToList();
            if (duplicates.Count > 0)
            {
                MessageBox.Show(this, $"TLK IDs must be unique. Duplicate: {string.Join(", ", duplicates)}",
                    "Duplicate TLK IDs", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string importDirectory = CreateUniqueOutputDirectory(Path.Combine(OutputFolder, "SelectedForImport"));
            var files = new List<string>();
            var texts = new Dictionary<int, string>();
            foreach (ElevenLabsLineItem line in selected)
            {
                string destination = Path.Combine(importDirectory,
                    BuildImportFileName(line.TlkId, _isFemaleAsset, Path.GetExtension(line.SelectedTakePath)));
                File.Copy(line.SelectedTakePath, destination, false);
                files.Add(destination);
                texts[line.TlkId] = line.Text;
            }

            SelectedAudioFiles = files;
            SelectedTextsByTlkId = texts;
            DialogResult = true;
            Close();
        }

        private void AddLineButton_Click(object sender, RoutedEventArgs e)
        {
            var item = new ElevenLabsLineItem { TlkId = TakeNextTlkId() };
            AddLine(item);
            LinesGrid.SelectedItem = item;
            LinesGrid.ScrollIntoView(item);
            UpdateBatchState();
        }

        private void BulkAddLinesButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ElevenLabsBulkAddLinesDialog(PeekNextTlkId(), this);
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            ElevenLabsLineItem lastItem = null;
            for (int i = 0; i < dialog.LineTexts.Count; i++)
            {
                lastItem = new ElevenLabsLineItem
                {
                    TlkId = dialog.StartingTlkId + i,
                    Text = dialog.LineTexts[i]
                };
                AddLine(lastItem);
            }

            _nextTlkId = Math.Max(_nextTlkId, dialog.StartingTlkId + dialog.LineTexts.Count);
            if (lastItem != null)
            {
                LinesGrid.SelectedItem = lastItem;
                LinesGrid.ScrollIntoView(lastItem);
            }
        }

        private void RemoveLinesButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (ElevenLabsLineItem line in LinesGrid.SelectedItems.Cast<ElevenLabsLineItem>().ToList())
            {
                Lines.Remove(line);
            }

        }

        private void ClearAllLinesButton_Click(object sender, RoutedEventArgs e)
        {
            _mediaPlayer.Stop();
            _mediaPlayer.Close();
            Lines.Clear();
        }

        private void ApplyEmotionToAllButton_Click(object sender, RoutedEventArgs e)
        {
            string emotion = BulkEmotion != null && EmotionTags.ContainsKey(BulkEmotion) ? BulkEmotion : "Neutral";
            foreach (ElevenLabsLineItem line in Lines)
            {
                line.Emotion = emotion;
            }

            StatusText = $"Applied {emotion} emotion to {Lines.Count:N0} line(s).";
        }

        private void ApplyAccentToAllButton_Click(object sender, RoutedEventArgs e)
        {
            string accent = BulkAccent != null && AccentTags.ContainsKey(BulkAccent) ? BulkAccent : "None";
            foreach (ElevenLabsLineItem line in Lines)
            {
                line.Accent = accent;
            }

            StatusText = $"Applied {accent} accent to {Lines.Count:N0} line(s).";
        }

        private void OpenOutputFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (HasOutputFolder)
            {
                Process.Start(new ProcessStartInfo(OutputFolder) { UseShellExecute = true });
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private bool TryValidateGeneration(IReadOnlyCollection<ElevenLabsLineItem> linesToGenerate, out uint? seed)
        {
            seed = null;
            if (!IsConnected || _client == null || SelectedVoice == null || SelectedModel == null)
            {
                MessageBox.Show(this, "Connect to ElevenLabs and select a voice and model first.",
                    "Not connected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (linesToGenerate.Count == 0)
            {
                MessageBox.Show(this, "Add at least one line to generate.", "No lines",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            ElevenLabsLineItem invalid = linesToGenerate.FirstOrDefault(line => line.TlkId <= 0 ||
                                                                               string.IsNullOrWhiteSpace(line.Text));
            if (invalid != null)
            {
                MessageBox.Show(this, "Every generated row must have a positive TLK ID and non-empty text.",
                    "Incomplete line", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var duplicates = Lines.GroupBy(line => line.TlkId).Where(group => group.Count() > 1)
                .Select(group => group.Key).ToList();
            if (duplicates.Count > 0)
            {
                MessageBox.Show(this, $"TLK IDs must be unique. Duplicate: {string.Join(", ", duplicates)}",
                    "Duplicate TLK IDs", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            int maxCharacters = GetModelCharacterLimit();
            ElevenLabsLineItem tooLong = maxCharacters > 0
                ? linesToGenerate.FirstOrDefault(line =>
                    BuildSpeechPrompt(line.Text, line.Emotion, line.Accent, IsElevenV3).Text.Length > maxCharacters)
                : null;
            if (tooLong != null)
            {
                MessageBox.Show(this,
                    $"TLK {tooLong.TlkId} exceeds {SelectedModel.Name}'s {maxCharacters:N0}-character request limit after its audio tags are applied.",
                    "Text is too long", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            uint parsedSeed = 0;
            if (!string.IsNullOrWhiteSpace(SeedText) && !uint.TryParse(SeedText, NumberStyles.None,
                    CultureInfo.InvariantCulture, out parsedSeed))
            {
                MessageBox.Show(this, "Seed must be blank or an integer from 0 through 4294967295.",
                    "Invalid seed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            else if (!string.IsNullOrWhiteSpace(SeedText))
            {
                seed = parsedSeed;
            }

            return true;
        }

        private int GetModelCharacterLimit()
        {
            int accountLimit = string.Equals(_subscription?.Tier, "free", StringComparison.OrdinalIgnoreCase)
                ? SelectedModel.MaxCharactersRequestFreeUser
                : SelectedModel.MaxCharactersRequestSubscribedUser;
            if (accountLimit <= 0) accountLimit = SelectedModel.MaximumTextLengthPerRequest;
            if (SelectedModel.MaximumTextLengthPerRequest > 0 && accountLimit > 0)
            {
                accountLimit = Math.Min(accountLimit, SelectedModel.MaximumTextLengthPerRequest);
            }

            return accountLimit;
        }

        private void ConfigureLanguages(string preferredLanguageCode)
        {
            _suppressSelectionEvents = true;
            Languages.Clear();
            var automatic = new ElevenLabsLanguage { Name = "Automatic", LanguageId = null };
            Languages.Add(automatic);
            foreach (var language in SelectedModel?.Languages ?? [])
            {
                Languages.Add(language);
            }

            SelectedLanguage = Languages.FirstOrDefault(language =>
                                   language.LanguageId == preferredLanguageCode) ?? automatic;
            _suppressSelectionEvents = false;
            OnPropertyChanged(nameof(SelectedModelDescription));
        }

        private void ApplyPreferences()
        {
            Stability = _preferences.Stability;
            SimilarityBoost = _preferences.SimilarityBoost;
            Style = _preferences.Style;
            UseSpeakerBoost = _preferences.UseSpeakerBoost;
            Speed = _preferences.Speed;
            ApplyTextNormalization = TextNormalizationOptions.Contains(_preferences.ApplyTextNormalization)
                ? _preferences.ApplyTextNormalization
                : "auto";
            ApplyLanguageTextNormalization = _preferences.ApplyLanguageTextNormalization;
            UseAdjacentTextContext = _preferences.UseAdjacentTextContext;
            EnableLogging = _preferences.EnableLogging;
            OptimizeStreamingLatency = Math.Clamp(_preferences.OptimizeStreamingLatency, 0, 4);
            SeedText = _preferences.Seed;
            MirrorOppositeGender = _preferences.MirrorOppositeGender;
        }

        private void SavePreferences()
        {
            _preferences.RememberApiKey = RememberApiKeyCheckBox.IsChecked == true;
            _preferences.VoiceId = SelectedVoice?.VoiceId;
            _preferences.ModelId = SelectedModel?.ModelId;
            _preferences.LanguageCode = SelectedLanguage?.LanguageId;
            _preferences.Stability = Stability;
            _preferences.SimilarityBoost = SimilarityBoost;
            _preferences.Style = Style;
            _preferences.UseSpeakerBoost = UseSpeakerBoost;
            _preferences.Speed = Speed;
            _preferences.ApplyTextNormalization = ApplyTextNormalization;
            _preferences.ApplyLanguageTextNormalization = ApplyLanguageTextNormalization;
            _preferences.UseAdjacentTextContext = UseAdjacentTextContext;
            _preferences.EnableLogging = EnableLogging;
            _preferences.OptimizeStreamingLatency = OptimizeStreamingLatency;
            _preferences.Seed = SeedText;
            _preferences.MirrorOppositeGender = MirrorOppositeGender;

            try
            {
                ElevenLabsPreferencesStore.Save(_preferences, ApiKeyPasswordBox.Password);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Could not save ElevenLabs preferences: {exception.Message}");
            }
        }

        private void UpdateCreditsText()
        {
            if (_subscription == null)
            {
                CreditsText = "Credits not loaded";
                return;
            }

            CreditsText = $"{_subscription.CreditsRemaining:N0} credits left " +
                          $"({_subscription.CharacterCount:N0} / {_subscription.CharacterLimit:N0} used, {_subscription.Tier} tier)";
        }

        private void AddLine(ElevenLabsLineItem line)
        {
            line.PropertyChanged += Line_PropertyChanged;
            Lines.Add(line);
        }

        private void Lines_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (ElevenLabsLineItem line in e.OldItems) line.PropertyChanged -= Line_PropertyChanged;
            }

            UpdateBatchState();
        }

        private void Line_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (sender is ElevenLabsLineItem line &&
                e.PropertyName is nameof(ElevenLabsLineItem.TlkId) or nameof(ElevenLabsLineItem.Text) or
                    nameof(ElevenLabsLineItem.Emotion) or nameof(ElevenLabsLineItem.Accent) &&
                line.HasAnyTake)
            {
                line.ClearTakes();
                line.Status = "Needs regeneration";
            }

            UpdateBatchState();
        }

        private void UpdateBatchState()
        {
            OnPropertyChanged(nameof(BatchEstimateText));
            OnPropertyChanged(nameof(CanGenerate));
            OnPropertyChanged(nameof(CanImport));
        }

        private static string CreateUniqueOutputDirectory(string requestedPath)
        {
            string path = requestedPath;
            int suffix = 2;
            while (Directory.Exists(path))
            {
                path = $"{requestedPath}-{suffix++}";
            }

            Directory.CreateDirectory(path);
            return path;
        }

        private int PeekNextTlkId()
        {
            int maxExisting = Lines.Select(line => line.TlkId).Where(id => id > 0).DefaultIfEmpty(0).Max();
            return Math.Max(_nextTlkId, maxExisting < int.MaxValue ? maxExisting + 1 : int.MaxValue);
        }

        private int TakeNextTlkId()
        {
            int next = PeekNextTlkId();
            if (next < int.MaxValue)
            {
                _nextTlkId = next + 1;
            }
            return next;
        }

        public static string BuildPromptedText(string text, string emotion, string accent)
        {
            var parts = new List<string>(3);
            if (AccentTags.TryGetValue(accent ?? "None", out string accentTag) && accentTag != null)
            {
                parts.Add(accentTag);
            }
            if (EmotionTags.TryGetValue(emotion ?? "Neutral", out string emotionTag) && emotionTag != null)
            {
                parts.Add(emotionTag);
            }
            parts.Add(text?.Trim() ?? string.Empty);
            return string.Join(" ", parts.Where(part => part.Length > 0));
        }

        public static ElevenLabsSpeechPrompt BuildSpeechPrompt(string text, string emotion, string accent,
            bool isElevenV3)
        {
            string spokenText = text?.Trim() ?? string.Empty;
            if (isElevenV3)
            {
                return new ElevenLabsSpeechPrompt(BuildPromptedText(spokenText, emotion, accent), 0);
            }

            var directions = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(accent) && !string.Equals(accent, "None", StringComparison.Ordinal))
            {
                directions.Add($"They spoke in {GetIndefiniteArticle(accent)} {accent} accent.");
            }
            if (!string.IsNullOrWhiteSpace(emotion) && !string.Equals(emotion, "Neutral", StringComparison.Ordinal))
            {
                string lowerEmotion = emotion.ToLowerInvariant();
                directions.Add($"They spoke with {GetIndefiniteArticle(lowerEmotion)} {lowerEmotion} emotion.");
            }

            if (directions.Count == 0)
            {
                return new ElevenLabsSpeechPrompt(spokenText, 0);
            }

            string prefix = string.Join(" ", directions) + " ";
            return new ElevenLabsSpeechPrompt(prefix + spokenText, prefix.Length);
        }

        private static string GetIndefiniteArticle(string value) =>
            !string.IsNullOrWhiteSpace(value) && "AEIOUaeiou".Contains(value[0]) ? "an" : "a";

        public static double GetTrimStartSeconds(ElevenLabsSpeechAlignment alignment, int prefixCharacterCount)
        {
            if (alignment?.Characters == null || alignment.CharacterStartTimesSeconds == null ||
                prefixCharacterCount <= 0 || prefixCharacterCount >= alignment.Characters.Count ||
                prefixCharacterCount >= alignment.CharacterStartTimesSeconds.Count)
            {
                throw new InvalidDataException(
                    "ElevenLabs did not return enough character timing data to remove the spoken accent/emotion direction.");
            }

            double startSeconds = alignment.CharacterStartTimesSeconds[prefixCharacterCount];
            if (!double.IsFinite(startSeconds) || startSeconds < 0d)
            {
                throw new InvalidDataException(
                    "ElevenLabs returned an invalid audio boundary for the spoken accent/emotion direction.");
            }

            return startSeconds;
        }

        private static void WriteTrimmedMp3AsPcmWave(byte[] mp3Audio, double trimStartSeconds,
            string destinationPath)
        {
            try
            {
                using var stream = new MemoryStream(mp3Audio, writable: false);
                using var reader = new Mp3FileReader(stream);
                var trimmed = new OffsetSampleProvider(reader.ToSampleProvider())
                {
                    SkipOver = TimeSpan.FromSeconds(trimStartSeconds)
                };
                WaveFileWriter.CreateWaveFile16(destinationPath, trimmed);
                if (new FileInfo(destinationPath).Length <= 44)
                {
                    throw new InvalidDataException("Removing the spoken direction produced an empty audio file.");
                }
            }
            catch
            {
                if (File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }
                throw;
            }
        }

        public static string BuildTakeFileName(int tlkId, bool isFemaleAsset, int take,
            string extension = ".mp3") =>
            $"VO_{tlkId}_{(isFemaleAsset ? "f" : "m")}_take{take}{NormalizeAudioExtension(extension)}";

        public static string BuildImportFileName(int tlkId, bool isFemaleAsset, string extension = ".mp3") =>
            $"VO_{tlkId}_{(isFemaleAsset ? "f" : "m")}{NormalizeAudioExtension(extension)}";

        private static string NormalizeAudioExtension(string extension) =>
            string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase) ? ".wav" : ".mp3";

        private static string GetShortMessage(Exception exception)
        {
            string message = exception.Message.ReplaceLineEndings(" ");
            return message.Length <= 110 ? message : message[..107] + "...";
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            _mediaPlayer.Stop();
            _mediaPlayer.Close();
            SavePreferences();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _cancellationTokenSource?.Dispose();
            _client?.Dispose();
            base.OnClosed(e);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed class ElevenLabsLineItem : INotifyPropertyChanged
    {
        private int _tlkId;
        private string _text;
        private string _emotion = "Neutral";
        private string _accent = "None";
        private string _status = "Not generated";
        private string _take1Path;
        private string _take2Path;
        private int _selectedTakeIndex;

        public int TlkId { get => _tlkId; set => SetProperty(ref _tlkId, value); }
        public string Text { get => _text; set => SetProperty(ref _text, value); }
        public string Emotion { get => _emotion; set => SetProperty(ref _emotion, value); }
        public string Accent { get => _accent; set => SetProperty(ref _accent, value); }
        public string Status { get => _status; set => SetProperty(ref _status, value); }
        public string Take1Path { get => _take1Path; private set => SetTakePath(ref _take1Path, value, nameof(Take1Path), nameof(HasTake1)); }
        public string Take2Path { get => _take2Path; private set => SetTakePath(ref _take2Path, value, nameof(Take2Path), nameof(HasTake2)); }
        public bool HasTake1 => !string.IsNullOrWhiteSpace(Take1Path) && File.Exists(Take1Path);
        public bool HasTake2 => !string.IsNullOrWhiteSpace(Take2Path) && File.Exists(Take2Path);
        public bool HasAnyTake => HasTake1 || HasTake2;

        public int SelectedTakeIndex
        {
            get => _selectedTakeIndex;
            set
            {
                if (SetProperty(ref _selectedTakeIndex, value))
                {
                    OnPropertyChanged(nameof(SelectedTakePath));
                }
            }
        }

        public string SelectedTakePath => SelectedTakeIndex == 1
            ? HasTake2 ? Take2Path : null
            : HasTake1 ? Take1Path : null;

        public void SetTake(int take, string path)
        {
            if (take == 1) Take1Path = path;
            else if (take == 2) Take2Path = path;
            else throw new ArgumentOutOfRangeException(nameof(take));
        }

        public void ClearTakes()
        {
            Take1Path = null;
            Take2Path = null;
            SelectedTakeIndex = 0;
        }

        private void SetTakePath(ref string field, string value, string propertyName, string availabilityProperty)
        {
            if (field == value) return;
            field = value;
            OnPropertyChanged(propertyName);
            OnPropertyChanged(availabilityProperty);
            OnPropertyChanged(nameof(HasAnyTake));
            OnPropertyChanged(nameof(SelectedTakePath));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
