using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;
using LegendaryExplorer.Audio;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UnrealExtensions;
using LegendaryExplorerCore.Audio;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.Win32;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;
using LegendaryExplorerCore.Helpers;
using ME3Tweaks.Wwiser;
using ME3Tweaks.Wwiser.Model.ParameterNode;
using WwiserActorMixer = ME3Tweaks.Wwiser.Model.Hierarchy.ActorMixer;

namespace LegendaryExplorer.Dialogs
{
    public partial class BulkAudioImportDialog : Window
    {
        public ObservableCollection<AudioImportItem> WavFileItems { get; } = new();
        public IEnumerable<string> WavFiles => WavFileItems.Select(item => item.FilePath);

        public sealed class AudioImportItem
        {
            public AudioImportItem(string filePath)
            {
                FilePath = filePath;
            }

            public string FilePath { get; }
            public bool CreateStopEvent { get; set; }
        }

        /// <summary>
        /// Known ME3/LE3 Wwise output buses. Master Audio Bus is always available in the template.
        /// Other buses will be injected into the template's Master-Mixer Hierarchy when selected.
        /// </summary>
        public List<string> OutputBuses { get; } = new()
        {
            "Master Audio Bus",
            "Env-VO-Conversation",
            "Env-VO-Ambient-Duck",
            "Env-VO-Ambient-NonDuck",
            "Env-VO-Ambient-Critical",
            "Env-VO-SoundSet-Duck",
            "Env-VO-SoundSet-NonDuck",
            "Env-VO-Exertions",
            "Env-Music",
            "Env-Snd-0-CineDesign",
            "Env-Snd-0-CineAnim",
            "Env-Snd-0-CineD-SkipKill",
            "Env-Snd-0-CineD-SkipNoKill",
            "Env-Snd-0-LevelEvents",
            "Env-Snd-0-LevelTransitions",
            "Env-Snd-0-ProceduralFoley",
            "Env-Snd-1-Amb-Stream",
            "Env-Snd-1-Amb-NonStream",
            "Env-Snd-1-Creatures",
            "Env-Snd-1-Foley",
            "Env-Snd-1-Footsteps",
            "Env-Snd-1-Physics",
            "Env-Snd-1-Placeables",
            "Env-Snd-1-Powers",
            "Env-Snd-1-Vehicles",
            "Env-Snd-1-VFX",
            "Env-Snd-1-Weapons",
            "Env-Snd-1-Bullets",
            "Env-Snd-2-PlayerWeapons",
            "Env-Snd-2-PlayerPowers",
            "Env-Snd-3-CreatureCritical",
            "Env-Snd-4-Explosions",
            "Env-Snd-5-Critical",
            "NonEnv-Snd-0-CineAnim",
            "NonEnv-Snd-0-CineDes",
            "NonEnv-Snd-0-LevelEvents",
            "NonEnv-VO-Radio-Convo",
            "NonEnv-VO-Radio-Critical",
            "NonSlowdown-GUI Sounds",
            "NonSlowdown-Music",
            "NonSlowdown-Dialog",
        };

        public string SelectedOutputBus { get; set; } = "Env-VO-Conversation";

        private readonly IMEPackage _package;
        private readonly string _bankPackageName;
        private readonly string _bankStreamingAudioPackageName;
        private readonly bool _allowFaceFxAssetCreation;
        private bool _syncFaceFxAssetNames = true;
        private bool _updatingFaceFxAssetNames;

        public BulkAudioImportDialog(
            IMEPackage package,
            string bankPackageName = "audio",
            string bankStreamingAudioPackageName = "int",
            IEnumerable<string> initialWavFiles = null,
            string initialBankName = null,
            bool? isDialogueBank = null,
            bool? generateGenderedEvents = null,
            bool allowFaceFxAssetCreation = true)
        {
            _package = package;
            _bankPackageName = bankPackageName;
            _bankStreamingAudioPackageName = bankStreamingAudioPackageName;
            _allowFaceFxAssetCreation = allowFaceFxAssetCreation;
            InitializeComponent();
            DataContext = this;
            CustomWindowChrome.ApplyCustomChrome(this);

            if (_package.Game != MEGame.LE3)
            {
                RadioEffectCheckBox.IsEnabled = false;
                RadioEffectCheckBox.ToolTip = "The BioWare radio effect is only available for LE3 banks.";
                QecEffectCheckBox.IsEnabled = false;
                QecEffectCheckBox.ToolTip = "The QEC effect is only available for LE3 banks.";
            }

            if (!_allowFaceFxAssetCreation)
            {
                SetNamedElementVisibility("CreateFaceFXAssetsLabel", Visibility.Collapsed);
                SetNamedElementVisibility("CreateFaceFXAssetsCheckBox", Visibility.Collapsed);
                SetNamedElementVisibility("TopFolderLabel", Visibility.Collapsed);
                SetNamedElementVisibility("TopFolderTextBox", Visibility.Collapsed);
                SetNamedElementVisibility("FemaleFaceFXAssetNameLabel", Visibility.Collapsed);
                SetNamedElementVisibility("FemaleFaceFXAssetNameTextBox", Visibility.Collapsed);
                SetNamedElementVisibility("MaleFaceFXAssetNameLabel", Visibility.Collapsed);
                SetNamedElementVisibility("MaleFaceFXAssetNameTextBox", Visibility.Collapsed);
            }

            if (!string.IsNullOrWhiteSpace(initialBankName))
            {
                BankNameTextBox.Text = initialBankName;
            }

            if (initialWavFiles != null)
            {
                foreach (var file in initialWavFiles
                             .Where(file => File.Exists(file) && AudioInputConverter.IsSupportedAudioFile(file))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    WavFileItems.Add(new AudioImportItem(file));
                }
            }

            if (isDialogueBank.HasValue)
            {
                IsDialogueBankCheckBox.IsChecked = isDialogueBank.Value;
            }

            if (generateGenderedEvents.HasValue)
            {
                GenerateGenderedEventsCheckBox.IsChecked = generateGenderedEvents.Value;
            }
        }

        private void SetNamedElementVisibility(string elementName, Visibility visibility)
        {
            if (FindName(elementName) is FrameworkElement element)
            {
                element.Visibility = visibility;
            }
        }

        private void AddFilesButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = AudioInputConverter.OpenFileDialogFilter,
                Multiselect = true,
                Title = "Select WAV or MP3 files to import"
            };

            if (DirectoryMemory.ShowDialog(openFileDialog) == true)
            {
                foreach (var file in openFileDialog.FileNames)
                {
                    if (!WavFileItems.Any(item => item.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase)))
                    {
                        WavFileItems.Add(new AudioImportItem(file));
                    }
                }
            }
        }

        private void RadioEffectCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (QecEffectCheckBox == null || VolumeTextBox == null)
            {
                return;
            }

            if (RadioEffectCheckBox.IsChecked == true)
            {
                QecEffectCheckBox.IsChecked = false;
                VolumeTextBox.Text = "12";
            }
            else if (QecEffectCheckBox.IsChecked != true)
            {
                VolumeTextBox.Text = "-10";
            }
        }

        private void QecEffectCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (RadioEffectCheckBox == null || VolumeTextBox == null)
            {
                return;
            }

            if (QecEffectCheckBox.IsChecked == true)
            {
                RadioEffectCheckBox.IsChecked = false;
                VolumeTextBox.Text = "-10";
            }
            else if (RadioEffectCheckBox.IsChecked != true)
            {
                VolumeTextBox.Text = "-10";
            }
        }

        private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = WavFilesListBox.SelectedItems.Cast<AudioImportItem>().ToList();
            foreach (var item in selectedItems)
            {
                WavFileItems.Remove(item);
            }
        }

        private void ClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            WavFileItems.Clear();
        }

        private void TopFolderTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (!_syncFaceFxAssetNames || FemaleFaceFXAssetNameTextBox == null || MaleFaceFXAssetNameTextBox == null)
            {
                return;
            }

            var topFolderName = TopFolderTextBox.Text.Trim();
            _updatingFaceFxAssetNames = true;
            FemaleFaceFXAssetNameTextBox.Text = string.IsNullOrWhiteSpace(topFolderName) ? "_F" : $"{topFolderName}_F";
            MaleFaceFXAssetNameTextBox.Text = string.IsNullOrWhiteSpace(topFolderName) ? "_M" : $"{topFolderName}_M";
            _updatingFaceFxAssetNames = false;
        }

        private void FaceFXAssetNameTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (IsLoaded && !_updatingFaceFxAssetNames)
            {
                _syncFaceFxAssetNames = false;
            }
        }

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (WavFileItems.Count == 0)
            {
                MessageBox.Show("No WAV or MP3 files have been added.", "No files", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var bankName = BankNameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(bankName))
            {
                MessageBox.Show("Please enter a bank name.", "Missing bank name", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_package.Game != MEGame.LE2 && _package.Game != MEGame.LE3)
            {
                MessageBox.Show("This feature only supports LE2 and LE3 packages.", "Unsupported game", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!WwiseCliHandler.CheckWwisePathForGame(_package.Game))
            {
                return;
            }

            if (!double.TryParse(VolumeTextBox.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var volume))
            {
                MessageBox.Show("Please enter a valid number for volume (e.g. -10).", "Invalid volume", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedBusItem = OutputBusComboBox.SelectedItem as string;
            var outputBusName = selectedBusItem ?? "Master Audio Bus";

            var isDialogue = IsDialogueBankCheckBox.IsChecked == true;
            var generateGenderedEvents = GenerateGenderedEventsCheckBox.IsChecked == true;
            var loopAudio = LoopAudioCheckBox.IsChecked == true;
            var applyRadioEffect = RadioEffectCheckBox.IsChecked == true;
            var applyQecEffect = QecEffectCheckBox.IsChecked == true;
            var createSharedStopEvent = CreateSharedStopEventCheckBox.IsChecked == true;
            var createFaceFxAssets = _allowFaceFxAssetCreation && CreateFaceFXAssetsCheckBox.IsChecked == true;
            var topFolderName = TopFolderTextBox.Text.Trim();
            var femaleFaceFxAssetName = FemaleFaceFXAssetNameTextBox.Text.Trim();
            var maleFaceFxAssetName = MaleFaceFXAssetNameTextBox.Text.Trim();

            if (createFaceFxAssets && !IsValidPackageObjectName(topFolderName))
            {
                MessageBox.Show("Please enter a valid top folder name. Use letters, numbers, or underscores only.",
                    "Invalid top folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (createFaceFxAssets && (!IsValidPackageObjectName(femaleFaceFxAssetName) || !IsValidPackageObjectName(maleFaceFxAssetName)))
            {
                MessageBox.Show("Please enter valid female and male FaceFX asset names. Use letters, numbers, or underscores only.",
                    "Invalid FaceFX asset name", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (createFaceFxAssets && femaleFaceFxAssetName.Equals(maleFaceFxAssetName, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Female and male FaceFX asset names must be different.",
                    "Duplicate FaceFX asset name", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ImportButton.IsEnabled = false;
            AddFilesButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            StatusTextBlock.Text = "Setting up Wwise project...";

            try
            {
                var result = await Task.Run(() => RunBulkAudioImport(bankName, isDialogue, volume, outputBusName,
                    generateGenderedEvents, loopAudio, applyRadioEffect, applyQecEffect, createSharedStopEvent, createFaceFxAssets, topFolderName,
                    femaleFaceFxAssetName, maleFaceFxAssetName));
                if (result != null)
                {
                    StatusTextBlock.Text = $"Error: {result}";
                    MessageBox.Show(result, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    StatusTextBlock.Text = "Import complete!";
                    MessageBox.Show("Audio imported successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    DialogResult = true;
                    Close();
                }
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Error: {ex.Message}";
                MessageBox.Show($"An error occurred during import:\n{ex.Message}", "Import error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ImportButton.IsEnabled = true;
                AddFilesButton.IsEnabled = true;
                CancelButton.IsEnabled = true;
            }
        }

        private string RunBulkAudioImport(string bankName, bool isDialogue, double volume, string outputBusName,
            bool generateGenderedEvents, bool loopAudio, bool applyRadioEffect, bool applyQecEffect, bool createSharedStopEvent, bool createFaceFxAssets,
            string topFolderName, string femaleFaceFxAssetName, string maleFaceFxAssetName)
        {
            var audioImportItems = Dispatcher.Invoke(() => WavFileItems
                .Select(item => new AudioImportItem(item.FilePath) { CreateStopEvent = item.CreateStopEvent })
                .ToList());

            // Sort audio inputs by TLK number so exports are created in numerical order
            audioImportItems.Sort((a, b) => ExtractTlkNumber(Path.GetFileNameWithoutExtension(a.FilePath))
                .CompareTo(ExtractTlkNumber(Path.GetFileNameWithoutExtension(b.FilePath))));
            var wavFiles = audioImportItems.Select(item => item.FilePath).ToList();
            var perAudioStopEventFiles = audioImportItems
                .Where(item => item.CreateStopEvent)
                .Select(item => item.FilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 1. Extract template project
            string templateZip = WwiseCliHandler.GetWwiseTemplateProject(_package.Game);
            string tempDir = Path.Combine(Path.GetTempPath(), $"LEX_BulkAudio_{Guid.NewGuid():N}");
            string projectDir = Path.Combine(tempDir, "TemplateProject");

            try
            {
                ZipFile.ExtractToDirectory(templateZip, tempDir);

                // 2. Normalize inputs into the project's Originals/SFX folder.
                // WwiseCLI only receives 16-bit PCM WAV, even when the user selected MP3 files.
                string originalsDir = Path.Combine(projectDir, "Originals", "SFX");
                Directory.CreateDirectory(originalsDir);
                foreach (var audioPath in wavFiles)
                {
                    var projectWavePath = Path.Combine(originalsDir,
                        Path.GetFileNameWithoutExtension(audioPath) + ".wav");
                    AudioInputConverter.ConvertToPcmWave(audioPath, projectWavePath);
                }

                // 3. Read existing WorkUnit IDs from the template
                string actorMixerPath = Path.Combine(projectDir, "Actor-Mixer Hierarchy", "Default Work Unit.wwu");
                string eventsPath = Path.Combine(projectDir, "Events", "Default Work Unit.wwu");
                string soundBanksPath = Path.Combine(projectDir, "SoundBanks", "Default Work Unit.wwu");
                string masterMixerPath = Path.Combine(projectDir, "Master-Mixer Hierarchy", "Default Work Unit.wwu");

                var actorMixerDoc = XDocument.Load(actorMixerPath);
                var eventsDoc = XDocument.Load(eventsPath);
                var soundBanksDoc = XDocument.Load(soundBanksPath);
                var masterMixerDoc = XDocument.Load(masterMixerPath);

                var actorMixerWuId = actorMixerDoc.Root.Attribute("ID")?.Value;
                var eventsWuId = eventsDoc.Root.Attribute("ID")?.Value;
                var masterMixerWuId = masterMixerDoc.Root.Attribute("ID")?.Value;

                // Master Audio Bus is always present in the template
                const string masterBusId = "{1514A4D8-1DA6-412A-A17E-75CA0C2149F3}";

                // Determine the output bus ID/name for the ActorMixer
                string outputBusId = masterBusId;
                string outputBusWuId = masterMixerWuId;

                if (outputBusName != "Master Audio Bus")
                {
                    // Inject the custom bus into the Master-Mixer Hierarchy as a child of Master Audio Bus
                    outputBusId = $"{{{Guid.NewGuid()}}}";
                    InjectBusIntoMasterMixer(masterMixerDoc, masterBusId, outputBusName, outputBusId);
                    masterMixerDoc.Save(masterMixerPath);
                }

                // 4. Build Actor-Mixer Hierarchy XML
                var actorMixerId = $"{{{Guid.NewGuid()}}}";
                var actorMixerXml = BuildActorMixerXml(actorMixerWuId, bankName, actorMixerId, wavFiles,
                    volume, outputBusName, outputBusId, outputBusWuId, generateGenderedEvents, loopAudio);
                File.WriteAllText(actorMixerPath, actorMixerXml);

                // 5. Build Events XML
                var eventsXml = BuildEventsXml(eventsWuId, actorMixerWuId, wavFiles, generateGenderedEvents,
                    createSharedStopEvent, perAudioStopEventFiles);
                File.WriteAllText(eventsPath, eventsXml);

                // 6. Build SoundBanks XML
                var soundBanksXml = BuildSoundBanksXml(soundBanksDoc.Root.Attribute("ID")?.Value,
                    bankName, actorMixerWuId, eventsWuId);
                File.WriteAllText(soundBanksPath, soundBanksXml);

                Dispatcher.Invoke(() => StatusTextBlock.Text = "Running WwiseCLI to generate soundbank...");

                // 7. Enable required project settings:
                //    - SoundBankGenerateEstimatedDuration: includes DurationMin/DurationMax in SoundbanksInfo.xml
                //    - SoundBankGenerateContentTXT: generates BankName.txt required by WwiseBankImport for
                //      dialogue banks to map stream IDs to their Wwise object names
                string projFile = Path.Combine(projectDir, "TemplateProject.wproj");
                EnableProjectSettings(projFile);

                // 8. Run WwiseCLI to generate soundbanks
                string wwiseCLIPath = WwiseCliHandler.GetWwiseCliPath(_package.Game);

                var process = new Process
                {
                    StartInfo =
                    {
                        FileName = wwiseCLIPath,
                        Arguments = $"\"{projFile}\" -GenerateSoundbanks",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                Debug.WriteLine($"WwiseCLI stdout: {stdout}");
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    Debug.WriteLine($"WwiseCLI stderr: {stderr}");
                }

                // 9. Find the generated .bnk file
                string generatedDir = Path.Combine(projectDir, "GeneratedSoundBanks", "Windows");
                if (!Directory.Exists(generatedDir))
                {
                    return $"WwiseCLI did not produce output. Check that Wwise CLI is configured correctly.\nStdout: {stdout}\nStderr: {stderr}";
                }

                string bnkPath = Path.Combine(generatedDir, $"{bankName}.bnk");
                if (!File.Exists(bnkPath))
                {
                    var availableFiles = string.Join(", ", Directory.GetFiles(generatedDir).Select(Path.GetFileName));
                    return $"Generated bank '{bankName}.bnk' not found in output. Available files: {availableFiles}";
                }

                if (applyRadioEffect)
                {
                    ApplyBioWareRadioEffectToBank(bnkPath);
                }
                else if (applyQecEffect)
                {
                    ApplyQecEffectToBank(bnkPath);
                }

                Dispatcher.Invoke(() => StatusTextBlock.Text = "Importing soundbank into package...");

                var effectiveBankPackageName = _bankPackageName;
                var effectiveStreamingAudioPackageName = _bankStreamingAudioPackageName;
                ExportEntry topFolderExport = null;
                ExportEntry audioFolderExport = null;

                if (createFaceFxAssets)
                {
                    topFolderExport = ExportCreator.CreatePackageExport(_package, topFolderName);
                    audioFolderExport = ExportCreator.CreatePackageExport(_package, "audio", topFolderExport);
                    effectiveBankPackageName = audioFolderExport.InstancedFullPath;
                    effectiveStreamingAudioPackageName = "int";
                }

                // 10. Import the bank into the package using existing WwiseBankImport
                //     Place everything under a top-level "audio" folder:
                //     - WwiseBank and WwiseEvents go directly under "audio"
                //     - WwiseStreams go under "audio.int"
                var importResult = WwiseBankImport.ImportBank(bnkPath, isDialogue, _package,
                    bankPackageName: effectiveBankPackageName, bankStreamingAudioPackageName: effectiveStreamingAudioPackageName);

                // 11. Set DurationSeconds on events from WAV file headers.
                //     This is critical for dialogue: without DurationSeconds the game's dialogue
                //     system cannot determine when a line has finished, causing nodes to get stuck.
                //     We always override whatever WwiseBankImport set because the Wwise estimated
                //     duration data is often missing for streaming sounds.
                if (importResult == null)
                {
                    var eventDurations = BuildEventDurationMap(wavFiles, generateGenderedEvents);
                    SetEventDurations(eventDurations);

                    if (createFaceFxAssets)
                    {
                        Dispatcher.Invoke(() => StatusTextBlock.Text = "Creating FaceFX assets and generating animations...");
                        CreateAndGenerateFaceFxAssets(topFolderExport, audioFolderExport, femaleFaceFxAssetName, maleFaceFxAssetName);
                    }
                }

                return importResult;
            }
            finally
            {
                // Cleanup temp directory
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
                catch
                {
                    // Best-effort cleanup
                }
            }
        }

        /// <summary>
        /// Adds the two ShareSets used by Admiral Hackett's QEC dialogue in
        /// BioD_Nor_204CallHackett_LOC_INT and applies them to the generated root ActorMixer.
        /// The McDSP FutzBox authoring plug-in is not available in Wwise 2019, so the exact
        /// version-134 HIRC data is injected after WwiseCLI has generated the bank.
        /// </summary>
        private static void ApplyQecEffectToBank(string bnkPath)
        {
            ApplyExactEffectChainToBank(bnkPath, "Hackett QEC", WwiseBankEffectPresets.HackettQec);
        }

        /// <summary>
        /// Adds the FutzBox and Parametric EQ ShareSets used by the root ActorMixers in
        /// cit001_postbridge_lovei_b_dlg and applies the complete chain to the generated
        /// root ActorMixer. The exact version-134 HIRC data is injected so Wwise does not
        /// substitute its factory Dual_Filters_Radio_Comm preset.
        /// </summary>
        private static void ApplyBioWareRadioEffectToBank(string bnkPath)
        {
            ApplyExactEffectChainToBank(bnkPath, "BioWare radio", WwiseBankEffectPresets.BioWareRadio);
        }

        private static void ApplyExactEffectChainToBank(string bnkPath, string effectName,
            IReadOnlyList<WwiseBankEffect> effectChain)
        {
            ME3Tweaks.Wwiser.WwiseBank bank;
            using (var input = new MemoryStream(File.ReadAllBytes(bnkPath), false))
            {
                bank = WwiseBankParser.Deserialize(input);
            }

            if (bank.BKHD.BankGeneratorVersion != WwiseBankEffectPresets.BankVersion)
            {
                throw new InvalidOperationException(
                    $"The {effectName} effect requires a version-{WwiseBankEffectPresets.BankVersion} LE3 Wwise bank, " +
                    $"but WwiseCLI generated version {bank.BKHD.BankGeneratorVersion}.");
            }

            if (bank.HIRC == null)
            {
                throw new InvalidOperationException("WwiseCLI generated a bank without an audio hierarchy.");
            }

            if (!WwiseBankEffectPresets.EnsureEffectData(bank, effectChain))
            {
                throw new InvalidOperationException(
                    $"The generated bank already uses a {effectName} ShareSet ID for another object.");
            }

            var actorMixers = bank.HIRC.Items
                .Select(item => item.Item)
                .OfType<WwiserActorMixer>()
                .ToList();
            var actorMixerIds = actorMixers.Select(actorMixer => actorMixer.Id).ToHashSet();
            var rootActorMixers = actorMixers
                .Where(actorMixer => actorMixer.NodeBaseParameters.DirectParentId == 0 ||
                                     !actorMixerIds.Contains(actorMixer.NodeBaseParameters.DirectParentId))
                .ToList();
            if (rootActorMixers.Count == 0)
            {
                throw new InvalidOperationException("WwiseCLI generated a bank without a root ActorMixer.");
            }

            foreach (var actorMixer in rootActorMixers)
            {
                var effects = actorMixer.NodeBaseParameters.FxParams;
                effects.FxChunks.Clear();
                for (var effectIndex = 0; effectIndex < effectChain.Count; effectIndex++)
                {
                    effects.FxChunks.Add(new FxChunk
                    {
                        FxIndex = checked((byte)effectIndex),
                        Id = effectChain[effectIndex].Id,
                        IsShareSet = true
                    });
                }
                effects.BitsFxBypass = 0;
                effects.NumFx = checked((byte)effects.FxChunks.Count);
                effects.IsOverrideParentFx = true;
            }

            bank.HIRC.ItemCount = checked((uint)bank.HIRC.Items.Count);
            using var output = new MemoryStream();
            WwiseBankParser.Serialize(bank, output);
            File.WriteAllBytes(bnkPath, output.ToArray());
        }

        /// <summary>
        /// Builds the Actor-Mixer Hierarchy XML with an ActorMixer containing a Sound per WAV file,
        /// each configured for streaming with Vorbis Quality High conversion.
        /// When generateGenderedEvents is true, two Sound nodes are created per WAV file
        /// (baseName_m and baseName_f), each with a unique ID but referencing the same WAV.
        /// This is necessary because in Wwise, each event must target its own Sound node —
        /// sharing a Sound between events breaks event completion tracking in the bank.
        /// </summary>
        private static string BuildActorMixerXml(string workUnitId, string bankName, string actorMixerId,
            List<string> wavFiles, double volume, string outputBusName, string outputBusId, string outputBusWuId,
            bool generateGenderedEvents, bool loopAudio)
        {
            // Factory "Vorbis Quality High" conversion setting (from Factory Conversion Settings.wwu in template)
            const string vorbisHighId = "{53A9DE0F-3F4F-4B59-8614-3F9E3C7358FC}";
            const string vorbisHighWuId = "{F6B2880C-85E5-47FA-A126-645B5DFD9ACC}";
            // Master Audio Bus from the template's Master-Mixer Hierarchy (used for individual Sound nodes)
            const string masterBusId = "{1514A4D8-1DA6-412A-A17E-75CA0C2149F3}";

            var volumeStr = volume.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var sb = new System.Text.StringBuilder();
            var pairedGenderBases = generateGenderedEvents ? GetBasesWithBothGenderedInputs(wavFiles) : [];
            foreach (var wavPath in wavFiles)
            {
                var soundName = Path.GetFileNameWithoutExtension(wavPath);

                if (generateGenderedEvents)
                {
                    foreach (var genderedSoundName in GetGenderedNamesForInput(soundName, pairedGenderBases))
                    {
                        AppendSoundXml(sb, genderedSoundName, soundName, vorbisHighId, vorbisHighWuId, masterBusId, outputBusWuId, loopAudio);
                    }
                }
                else
                {
                    AppendSoundXml(sb, soundName, soundName, vorbisHighId, vorbisHighWuId, masterBusId, outputBusWuId, loopAudio);
                }
            }

            var xml = new System.Text.StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            xml.AppendLine($"<WwiseDocument Type=\"WorkUnit\" ID=\"{workUnitId}\" SchemaVersion=\"94\">");
            xml.AppendLine("\t<AudioObjects>");
            xml.AppendLine($"\t\t<WorkUnit Name=\"Default Work Unit\" ID=\"{workUnitId}\" PersistMode=\"Standalone\">");
            xml.AppendLine("\t\t\t<ChildrenList>");
            xml.AppendLine($"\t\t\t\t<ActorMixer Name=\"{bankName}\" ID=\"{actorMixerId}\" ShortID=\"{GenerateShortId(bankName)}\">");
            xml.AppendLine("\t\t\t\t\t<PropertyList>");
            xml.AppendLine("\t\t\t\t\t\t<Property Name=\"UseGameAuxSends\" Type=\"bool\" Value=\"True\"/>");
            xml.AppendLine("\t\t\t\t\t\t<Property Name=\"Volume\" Type=\"Real64\">");
            xml.AppendLine("\t\t\t\t\t\t\t<ValueList>");
            xml.AppendLine($"\t\t\t\t\t\t\t\t<Value>{volumeStr}</Value>");
            xml.AppendLine("\t\t\t\t\t\t\t</ValueList>");
            xml.AppendLine("\t\t\t\t\t\t</Property>");
            xml.AppendLine("\t\t\t\t\t</PropertyList>");
            xml.AppendLine("\t\t\t\t\t<ReferenceList>");
            xml.AppendLine("\t\t\t\t\t\t<Reference Name=\"Conversion\">");
            xml.AppendLine($"\t\t\t\t\t\t\t<ObjectRef Name=\"Vorbis Quality High\" ID=\"{vorbisHighId}\" WorkUnitID=\"{vorbisHighWuId}\"/>");
            xml.AppendLine("\t\t\t\t\t\t</Reference>");
            xml.AppendLine("\t\t\t\t\t\t<Reference Name=\"OutputBus\">");
            xml.AppendLine($"\t\t\t\t\t\t\t<ObjectRef Name=\"{outputBusName}\" ID=\"{outputBusId}\" WorkUnitID=\"{outputBusWuId}\"/>");
            xml.AppendLine("\t\t\t\t\t\t</Reference>");
            xml.AppendLine("\t\t\t\t\t</ReferenceList>");
            xml.AppendLine("\t\t\t\t\t<ChildrenList>");
            xml.Append(sb);
            xml.AppendLine("\t\t\t\t\t</ChildrenList>");
            xml.AppendLine("\t\t\t\t</ActorMixer>");
            xml.AppendLine("\t\t\t</ChildrenList>");
            xml.AppendLine("\t\t</WorkUnit>");
            xml.AppendLine("\t</AudioObjects>");
            xml.AppendLine("</WwiseDocument>");
            return xml.ToString();
        }

        /// <summary>
        /// Appends a single Sound XML block to the Actor-Mixer hierarchy.
        /// </summary>
        /// <param name="sb">StringBuilder to append to.</param>
        /// <param name="soundName">Name for the Sound node (used for ID generation and Wwise object name).</param>
        /// <param name="wavFileName">Base name of the WAV file (without extension) this Sound references.</param>
        /// <param name="vorbisHighId">Vorbis Quality High conversion ID.</param>
        /// <param name="vorbisHighWuId">Vorbis Quality High WorkUnit ID.</param>
        /// <param name="masterBusId">Master Audio Bus ID.</param>
        /// <param name="outputBusWuId">Master-Mixer Hierarchy WorkUnit ID.</param>
        private static void AppendSoundXml(System.Text.StringBuilder sb, string soundName, string wavFileName,
            string vorbisHighId, string vorbisHighWuId, string masterBusId, string outputBusWuId, bool loopAudio)
        {
            var soundId = $"{{{GenerateDeterministicGuid(soundName)}}}";
            var sourceId = $"{{{Guid.NewGuid()}}}";

            sb.AppendLine($"\t\t\t\t\t\t<Sound Name=\"{soundName}\" ID=\"{soundId}\" ShortID=\"{GenerateShortId(soundName)}\">");
            sb.AppendLine("\t\t\t\t\t\t\t<PropertyList>");
            sb.AppendLine("\t\t\t\t\t\t\t\t<Property Name=\"IsStreamingEnabled\" Type=\"bool\">");
            sb.AppendLine("\t\t\t\t\t\t\t\t\t<ValueList>");
            sb.AppendLine("\t\t\t\t\t\t\t\t\t\t<Value>True</Value>");
            sb.AppendLine("\t\t\t\t\t\t\t\t\t</ValueList>");
            sb.AppendLine("\t\t\t\t\t\t\t\t</Property>");
            if (loopAudio)
            {
                sb.AppendLine("\t\t\t\t\t\t\t\t<Property Name=\"IsLoopingEnabled\" Type=\"bool\" Value=\"True\"/>");
                sb.AppendLine("\t\t\t\t\t\t\t\t<Property Name=\"IsLoopingInfinite\" Type=\"bool\" Value=\"True\"/>");
            }
            sb.AppendLine("\t\t\t\t\t\t\t</PropertyList>");
            sb.AppendLine("\t\t\t\t\t\t\t<ReferenceList>");
            sb.AppendLine("\t\t\t\t\t\t\t\t<Reference Name=\"Conversion\">");
            sb.AppendLine($"\t\t\t\t\t\t\t\t\t<ObjectRef Name=\"Vorbis Quality High\" ID=\"{vorbisHighId}\" WorkUnitID=\"{vorbisHighWuId}\"/>");
            sb.AppendLine("\t\t\t\t\t\t\t\t</Reference>");
            sb.AppendLine("\t\t\t\t\t\t\t\t<Reference Name=\"OutputBus\">");
            sb.AppendLine($"\t\t\t\t\t\t\t\t\t<ObjectRef Name=\"Master Audio Bus\" ID=\"{masterBusId}\" WorkUnitID=\"{outputBusWuId}\"/>");
            sb.AppendLine("\t\t\t\t\t\t\t\t</Reference>");
            sb.AppendLine("\t\t\t\t\t\t\t</ReferenceList>");
            sb.AppendLine("\t\t\t\t\t\t\t<ChildrenList>");
            sb.AppendLine($"\t\t\t\t\t\t\t\t<AudioFileSource Name=\"{soundName}\" ID=\"{sourceId}\" ShortID=\"{GenerateShortId(soundName + "_src")}\">");
            sb.AppendLine("\t\t\t\t\t\t\t\t\t<Language>SFX</Language>");
            sb.AppendLine($"\t\t\t\t\t\t\t\t\t<AudioFile>{wavFileName}.wav</AudioFile>");
            sb.AppendLine("\t\t\t\t\t\t\t\t</AudioFileSource>");
            sb.AppendLine("\t\t\t\t\t\t\t</ChildrenList>");
            sb.AppendLine("\t\t\t\t\t\t\t<ActiveSourceList>");
            sb.AppendLine($"\t\t\t\t\t\t\t\t<ActiveSource Name=\"{soundName}\" ID=\"{sourceId}\" Platform=\"Linked\"/>");
            sb.AppendLine("\t\t\t\t\t\t\t</ActiveSourceList>");
            sb.AppendLine("\t\t\t\t\t\t</Sound>");
        }

        /// <summary>
        /// Builds the Events XML with Play events per Sound and optional Stop events.
        /// When generateGenderedEvents is true, two events are created per audio input:
        /// {baseName}_m_Play targeting the {baseName}_m Sound, and
        /// {baseName}_f_Play targeting the {baseName}_f Sound.
        /// Each event must target its own Sound node — sharing a Sound between events
        /// breaks event completion tracking in the generated Wwise bank.
        /// A per-audio Stop event targets every Sound generated from that input, while the shared
        /// Stop event targets every Sound generated by the import.
        /// </summary>
        private static string BuildEventsXml(string workUnitId, string actorMixerWuId,
            List<string> wavFiles, bool generateGenderedEvents, bool createSharedStopEvent,
            HashSet<string> perAudioStopEventFiles)
        {
            var sb = new System.Text.StringBuilder();
            var pairedGenderBases = generateGenderedEvents ? GetBasesWithBothGenderedInputs(wavFiles) : [];
            var sharedStopTargets = new List<(string SoundName, string SoundId)>();
            foreach (var wavPath in wavFiles)
            {
                var soundName = Path.GetFileNameWithoutExtension(wavPath);
                var soundTargets = new List<(string SoundName, string SoundId)>();

                if (generateGenderedEvents)
                {
                    foreach (var genderedSoundName in GetGenderedNamesForInput(soundName, pairedGenderBases))
                    {
                        var soundId = $"{{{GenerateDeterministicGuid(genderedSoundName)}}}";
                        AppendEventXml(sb, $"{genderedSoundName}_Play", genderedSoundName, soundId, actorMixerWuId);
                        soundTargets.Add((genderedSoundName, soundId));
                    }
                }
                else
                {
                    var soundId = $"{{{GenerateDeterministicGuid(soundName)}}}";
                    AppendEventXml(sb, $"{soundName}_Play", soundName, soundId, actorMixerWuId);
                    soundTargets.Add((soundName, soundId));
                }

                if (perAudioStopEventFiles.Contains(wavPath))
                {
                    AppendStopEventXml(sb, $"{soundName}_Stop", soundTargets, actorMixerWuId);
                }

                sharedStopTargets.AddRange(soundTargets);
            }

            if (createSharedStopEvent)
            {
                AppendStopEventXml(sb, "Stop", sharedStopTargets, actorMixerWuId);
            }

            var xml = new System.Text.StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            xml.AppendLine($"<WwiseDocument Type=\"WorkUnit\" ID=\"{workUnitId}\" SchemaVersion=\"94\">");
            xml.AppendLine("\t<Events>");
            xml.AppendLine($"\t\t<WorkUnit Name=\"Default Work Unit\" ID=\"{workUnitId}\" PersistMode=\"Standalone\">");
            xml.AppendLine("\t\t\t<ChildrenList>");
            xml.Append(sb);
            xml.AppendLine("\t\t\t</ChildrenList>");
            xml.AppendLine("\t\t</WorkUnit>");
            xml.AppendLine("\t</Events>");
            xml.AppendLine("</WwiseDocument>");
            return xml.ToString();
        }

        /// <summary>
        /// Appends a single Event XML block targeting a Sound.
        /// </summary>
        private static void AppendEventXml(System.Text.StringBuilder sb, string eventName,
            string soundName, string soundId, string actorMixerWuId)
        {
            var eventId = $"{{{Guid.NewGuid()}}}";
            var actionId = $"{{{Guid.NewGuid()}}}";

            sb.AppendLine($"\t\t\t\t<Event Name=\"{eventName}\" ID=\"{eventId}\">");
            sb.AppendLine("\t\t\t\t\t<ChildrenList>");
            sb.AppendLine($"\t\t\t\t\t\t<Action Name=\"\" ID=\"{actionId}\" ShortID=\"{GenerateShortId(eventName)}\">");
            sb.AppendLine("\t\t\t\t\t\t\t<ReferenceList>");
            sb.AppendLine("\t\t\t\t\t\t\t\t<Reference Name=\"Target\">");
            sb.AppendLine($"\t\t\t\t\t\t\t\t\t<ObjectRef Name=\"{soundName}\" ID=\"{soundId}\" WorkUnitID=\"{actorMixerWuId}\"/>");
            sb.AppendLine("\t\t\t\t\t\t\t\t</Reference>");
            sb.AppendLine("\t\t\t\t\t\t\t</ReferenceList>");
            sb.AppendLine("\t\t\t\t\t\t</Action>");
            sb.AppendLine("\t\t\t\t\t</ChildrenList>");
            sb.AppendLine("\t\t\t\t</Event>");
        }

        /// <summary>
        /// Appends an Event containing Wwise Stop actions for all supplied Sound targets.
        /// Wwise 2019.1 serializes a Stop action from ActionType value 2 in the work unit.
        /// </summary>
        private static void AppendStopEventXml(System.Text.StringBuilder sb, string eventName,
            IReadOnlyList<(string SoundName, string SoundId)> soundTargets, string actorMixerWuId)
        {
            var eventId = $"{{{Guid.NewGuid()}}}";

            sb.AppendLine($"\t\t\t\t<Event Name=\"{eventName}\" ID=\"{eventId}\">");
            sb.AppendLine("\t\t\t\t\t<ChildrenList>");

            for (int i = 0; i < soundTargets.Count; i++)
            {
                var (soundName, soundId) = soundTargets[i];
                var actionId = $"{{{Guid.NewGuid()}}}";
                var actionShortId = GenerateShortId($"{eventName}_{soundName}_{i}_StopAction");

                sb.AppendLine($"\t\t\t\t\t\t<Action Name=\"\" ID=\"{actionId}\" ShortID=\"{actionShortId}\">");
                sb.AppendLine("\t\t\t\t\t\t\t<PropertyList>");
                sb.AppendLine("\t\t\t\t\t\t\t\t<Property Name=\"ActionType\" Type=\"int16\" Value=\"2\"/>");
                sb.AppendLine("\t\t\t\t\t\t\t</PropertyList>");
                sb.AppendLine("\t\t\t\t\t\t\t<ReferenceList>");
                sb.AppendLine("\t\t\t\t\t\t\t\t<Reference Name=\"Target\">");
                sb.AppendLine($"\t\t\t\t\t\t\t\t\t<ObjectRef Name=\"{soundName}\" ID=\"{soundId}\" WorkUnitID=\"{actorMixerWuId}\"/>");
                sb.AppendLine("\t\t\t\t\t\t\t\t</Reference>");
                sb.AppendLine("\t\t\t\t\t\t\t</ReferenceList>");
                sb.AppendLine("\t\t\t\t\t\t</Action>");
            }

            sb.AppendLine("\t\t\t\t\t</ChildrenList>");
            sb.AppendLine("\t\t\t\t</Event>");
        }

        /// <summary>
        /// Strips a trailing gender suffix (_m or _f) from a sound name to get the base name.
        /// e.g. "VO_17250592_m" -> "VO_17250592", "VO_17250592_f" -> "VO_17250592",
        /// "VO_17250592" -> "VO_17250592" (no suffix, returned as-is).
        /// </summary>
        private static string StripGenderSuffix(string soundName)
        {
            if (soundName.EndsWith("_m", StringComparison.OrdinalIgnoreCase) ||
                soundName.EndsWith("_f", StringComparison.OrdinalIgnoreCase))
            {
                return soundName[..^2];
            }
            return soundName;
        }

        private static IEnumerable<string> GetGenderedNamesForInput(string soundName, HashSet<string> pairedGenderBases)
        {
            var baseName = StripGenderSuffix(soundName);
            if (pairedGenderBases.Contains(baseName) && TryGetGenderSuffix(soundName, out var genderSuffix))
            {
                yield return $"{baseName}_{genderSuffix}";
                yield break;
            }

            yield return $"{baseName}_m";
            yield return $"{baseName}_f";
        }

        private static HashSet<string> GetBasesWithBothGenderedInputs(IEnumerable<string> wavFiles)
        {
            var genderedBases = new Dictionary<string, (bool HasMale, bool HasFemale)>(StringComparer.OrdinalIgnoreCase);

            foreach (var wavPath in wavFiles)
            {
                var soundName = Path.GetFileNameWithoutExtension(wavPath);
                if (string.IsNullOrWhiteSpace(soundName) || !TryGetGenderSuffix(soundName, out var genderSuffix))
                {
                    continue;
                }

                var baseName = StripGenderSuffix(soundName);
                genderedBases.TryGetValue(baseName, out var genders);
                if (genderSuffix == "m")
                {
                    genders.HasMale = true;
                }
                else
                {
                    genders.HasFemale = true;
                }
                genderedBases[baseName] = genders;
            }

            return genderedBases
                .Where(pair => pair.Value.HasMale && pair.Value.HasFemale)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static bool TryGetGenderSuffix(string soundName, out string genderSuffix)
        {
            if (soundName.EndsWith("_m", StringComparison.OrdinalIgnoreCase))
            {
                genderSuffix = "m";
                return true;
            }

            if (soundName.EndsWith("_f", StringComparison.OrdinalIgnoreCase))
            {
                genderSuffix = "f";
                return true;
            }

            genderSuffix = null;
            return false;
        }

        /// <summary>
        /// Builds the SoundBanks XML with a single bank that includes both the Actor-Mixer and Events work units.
        /// </summary>
        private static string BuildSoundBanksXml(string workUnitId, string bankName,
            string actorMixerWuId, string eventsWuId)
        {
            var soundBankId = $"{{{Guid.NewGuid()}}}";

            var xml = new System.Text.StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            xml.AppendLine($"<WwiseDocument Type=\"WorkUnit\" ID=\"{workUnitId}\" SchemaVersion=\"94\">");
            xml.AppendLine("\t<SoundBanks>");
            xml.AppendLine($"\t\t<WorkUnit Name=\"Default Work Unit\" ID=\"{workUnitId}\" PersistMode=\"Standalone\">");
            xml.AppendLine("\t\t\t<ChildrenList>");
            xml.AppendLine($"\t\t\t\t<SoundBank Name=\"{bankName}\" ID=\"{soundBankId}\">");
            xml.AppendLine("\t\t\t\t\t<ObjectInclusionList>");
            xml.AppendLine($"\t\t\t\t\t\t<ObjectRef Name=\"Default Work Unit\" ID=\"{actorMixerWuId}\" WorkUnitID=\"{actorMixerWuId}\" Filter=\"7\" Origin=\"Manual\"/>");
            xml.AppendLine($"\t\t\t\t\t\t<ObjectRef Name=\"Default Work Unit\" ID=\"{eventsWuId}\" WorkUnitID=\"{eventsWuId}\" Filter=\"7\" Origin=\"Manual\"/>");
            xml.AppendLine("\t\t\t\t\t</ObjectInclusionList>");
            xml.AppendLine("\t\t\t\t\t<ObjectExclusionList/>");
            xml.AppendLine("\t\t\t\t\t<GameSyncExclusionList/>");
            xml.AppendLine("\t\t\t\t</SoundBank>");
            xml.AppendLine("\t\t\t</ChildrenList>");
            xml.AppendLine("\t\t</WorkUnit>");
            xml.AppendLine("\t</SoundBanks>");
            xml.AppendLine("</WwiseDocument>");
            return xml.ToString();
        }

        /// <summary>
        /// Injects a custom bus as a child of "Master Audio Bus" in the template's Master-Mixer Hierarchy.
        /// This is necessary because the template only defines "Master Audio Bus", but the game uses
        /// many sub-buses for audio routing (e.g. Env-VO-Conversation). Without adding the bus to the
        /// project, WwiseCLI would fail to resolve the bus reference in the Actor-Mixer XML.
        /// </summary>
        private static void InjectBusIntoMasterMixer(XDocument masterMixerDoc, string masterBusId, string busName, string busId)
        {
            // Find the Master Audio Bus element by its ID attribute
            var masterBusElement = masterMixerDoc.Descendants("Bus")
                .FirstOrDefault(e => e.Attribute("ID")?.Value == masterBusId);

            if (masterBusElement == null)
                return;

            // Add or get the ChildrenList under Master Audio Bus
            var childrenList = masterBusElement.Element("ChildrenList");
            if (childrenList == null)
            {
                childrenList = new XElement("ChildrenList");
                masterBusElement.Add(childrenList);
            }

            // Add the custom bus
            var busElement = new XElement("Bus",
                new XAttribute("Name", busName),
                new XAttribute("ID", busId));
            childrenList.Add(busElement);
        }

        /// <summary>
        /// Enables required SoundBank generation settings in the Wwise project file (.wproj):
        /// - SoundBankGenerateEstimatedDuration: includes DurationMin/DurationMax on events in
        ///   the generated SoundbanksInfo.xml, used by WwiseBankImport to set DurationSeconds.
        /// - SoundBankGenerateContentTXT: generates BankName.txt with Wwise object names mapped
        ///   to stream IDs, required by WwiseBankImport for dialogue banks so events can be
        ///   properly linked to their WwiseStream exports.
        /// </summary>
        private static void EnableProjectSettings(string wprojPath)
        {
            var doc = XDocument.Load(wprojPath);
            var propertyList = doc.Descendants("PropertyList").FirstOrDefault();
            if (propertyList != null)
            {
                propertyList.Add(new XElement("Property",
                    new XAttribute("Name", "SoundBankGenerateEstimatedDuration"),
                    new XAttribute("Type", "bool"),
                    new XAttribute("Value", "True")));
                propertyList.Add(new XElement("Property",
                    new XAttribute("Name", "SoundBankGenerateContentTXT"),
                    new XAttribute("Type", "bool"),
                    new XAttribute("Value", "True")));
                doc.Save(wprojPath);
            }
        }

        /// <summary>
        /// Builds a mapping from expected event names to their WAV file durations.
        /// Used as a fallback to set DurationSeconds on events if SoundbanksInfo.xml
        /// did not contain estimated duration data.
        /// </summary>
        private Dictionary<string, float> BuildEventDurationMap(List<string> wavFiles, bool generateGenderedEvents)
        {
            var map = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var pairedGenderBases = generateGenderedEvents ? GetBasesWithBothGenderedInputs(wavFiles) : [];
            foreach (var wavPath in wavFiles)
            {
                var soundName = Path.GetFileNameWithoutExtension(wavPath);
                var duration = ReadAudioDurationSeconds(wavPath);

                if (generateGenderedEvents)
                {
                    foreach (var genderedSoundName in GetGenderedNamesForInput(soundName, pairedGenderBases))
                    {
                        map[$"{genderedSoundName}_Play"] = duration;
                    }
                }
                else
                {
                    map[$"{soundName}_Play"] = duration;
                }
            }
            return map;
        }

        /// <summary>
        /// Sets DurationSeconds on WwiseEvent exports in the package that match the given event names.
        /// This always overrides any value previously set by WwiseBankImport, because the Wwise
        /// estimated duration data may be missing for streaming sounds and without DurationSeconds
        /// the dialogue system cannot determine when a line has finished, causing nodes to get stuck.
        /// </summary>
        private void SetEventDurations(Dictionary<string, float> eventDurations)
        {
            foreach (var export in _package.Exports.Where(e => e.ClassName == "WwiseEvent"))
            {
                if (eventDurations.TryGetValue(export.ObjectNameString, out var duration))
                {
                    if (duration > 0f)
                    {
                        var props = export.GetProperties();
                        props.AddOrReplaceProp(new FloatProperty(duration, "DurationSeconds"));
                        export.WriteProperties(props);
                    }
                    else
                    {
                        Debug.WriteLine($"BulkAudioImport: Could not determine WAV duration for event '{export.ObjectNameString}', DurationSeconds will not be set. Dialogue nodes using this event may get stuck.");
                    }
                }
            }
        }

        private void CreateAndGenerateFaceFxAssets(ExportEntry topFolderExport, ExportEntry audioFolderExport,
            string femaleFaceFxAssetName, string maleFaceFxAssetName)
        {
            var femaleFaceFx = GetOrCreateFaceFxAnimSetExport(topFolderExport, femaleFaceFxAssetName);
            var maleFaceFx = GetOrCreateFaceFxAnimSetExport(topFolderExport, maleFaceFxAssetName);

            AddAudioAndGenerateFaceFx(femaleFaceFx, audioFolderExport, isFemaleAsset: true, FaceFXSpecies.HumanFemale);
            AddAudioAndGenerateFaceFx(maleFaceFx, audioFolderExport, isFemaleAsset: false, FaceFXSpecies.HumanMale);
        }

        private ExportEntry GetOrCreateFaceFxAnimSetExport(ExportEntry parent, string assetName)
        {
            var existingExport = _package.FindExport($"{parent.InstancedFullPath}.{assetName}", "FaceFXAnimSet") as ExportEntry;
            if (existingExport != null)
            {
                return existingExport;
            }

            var faceFxExport = ExportCreator.CreateExport(_package, assetName, "FaceFXAnimSet", parent, indexed: false);
            faceFxExport.WritePropertiesAndBinary(new PropertyCollection(), FaceFXAnimSet.Create(_package.Game));
            return faceFxExport;
        }

        private void AddAudioAndGenerateFaceFx(ExportEntry faceFxExport, ExportEntry audioFolderExport, bool isFemaleAsset, FaceFXSpecies species)
        {
            var faceFx = faceFxExport.GetBinaryData<FaceFXAnimSet>();
            AddAudioFromFolderExport(faceFxExport, faceFx, audioFolderExport, isFemaleAsset);

            var options = GetBulkFaceFxGenerationOptions(faceFxExport.ObjectNameString, faceFx.Lines.Count, species, isFemaleAsset);
            if (options != null)
            {
                GenerateFaceFxForAllLines(faceFxExport, faceFx, isFemaleAsset, options);
            }

            faceFxExport.WriteBinary(faceFx);
        }

        private FaceFXGenerationOptions GetBulkFaceFxGenerationOptions(string assetName, int lineCount, FaceFXSpecies defaultSpecies, bool isFemaleAsset)
        {
            if (lineCount == 0)
            {
                return null;
            }

            FaceFXGenerationOptions options = null;
            Dispatcher.Invoke(() =>
            {
                StatusTextBlock.Text = $"Configure FaceFX generation for {assetName}...";
                var bulkDialog = new BulkFaceFXGenerationDialog(lineCount, this, defaultSpecies)
                {
                    Title = $"Bulk FaceFX Generation - {assetName}"
                };

                if (bulkDialog.ShowDialog() == true && bulkDialog.Confirmed)
                {
                    options = new FaceFXGenerationOptions
                    {
                        CharacterType = isFemaleAsset ? CharacterType.HumanFemale : CharacterType.HumanMale,
                        Species = bulkDialog.SelectedSpeciesEnum,
                        GenerateJawAnimation = true,
                        GenerateBlinkAnimation = bulkDialog.GenerateBlinkAnimation,
                        GenerateEyebrowAnimation = true,
                        GenerateHeadMovement = false,
                        LipSyncIntensity = bulkDialog.LipSyncIntensity,
                        BlinkFrequency = bulkDialog.BlinkFrequency,
                        UseAudioAmplitude = true,
                        FxaData = null,
                        UseTextFallback = true
                    };
                }
            });

            return options;
        }

        private int AddAudioFromFolderExport(ExportEntry faceFxExport, FaceFXAnimSet faceFx, ExportEntry folderExport, bool isFemaleAsset)
        {
            var entryTree = new EntryTree(_package);
            var filteredEvents = entryTree.FlattenTreeOf(folderExport, includeRoot: false)
                .OfType<ExportEntry>()
                .Where(exp => exp.ClassName == "WwiseEvent")
                .Where(exp => exp.ObjectName.Name.Contains(isFemaleAsset ? "f_play" : "m_play", StringComparison.OrdinalIgnoreCase))
                .OrderBy(exp => ExtractTlkNumber(exp.ObjectName.Name))
                .ToList();

            if (filteredEvents.Count == 0)
            {
                return 0;
            }

            var props = faceFxExport.GetProperties();
            var referencedSoundCues = props.GetProp<ArrayProperty<ObjectProperty>>("ReferencedSoundCues")
                                      ?? new ArrayProperty<ObjectProperty>("ReferencedSoundCues");
            var existingReferences = new HashSet<int>(referencedSoundCues.Select(op => op.Value));
            var existingTlkIds = faceFx.Lines
                .Select(line => int.TryParse(line.ID, out var tlkId) ? tlkId : -1)
                .Where(tlkId => tlkId > 0)
                .ToHashSet();

            int linesAdded = 0;
            int lineIndex = faceFx.Lines.Count;

            foreach (var wwiseEvent in filteredEvents)
            {
                if (existingReferences.Contains(wwiseEvent.UIndex))
                {
                    continue;
                }

                int tlkID = ExtractTlkIdFromWwiseEventName(wwiseEvent.ObjectName.Name);
                if (tlkID <= 0 || existingTlkIds.Contains(tlkID))
                {
                    continue;
                }

                var lineName = $"FXA_{tlkID}_{(isFemaleAsset ? "F" : "M")}";
                var line = new FaceFXLine
                {
                    NameIndex = faceFx.Names.FindOrAdd(lineName),
                    NameAsString = lineName,
                    AnimationNames = [],
                    Points = [],
                    NumKeys = [],
                    FadeInTime = 0.16f,
                    FadeOutTime = 0.22f,
                    Path = wwiseEvent.InstancedFullPath,
                    ID = tlkID.ToString(),
                    Index = lineIndex
                };

                while (referencedSoundCues.Count <= lineIndex)
                {
                    referencedSoundCues.Add(new ObjectProperty(0));
                }

                referencedSoundCues[lineIndex] = new ObjectProperty(wwiseEvent.UIndex);
                existingReferences.Add(wwiseEvent.UIndex);
                existingTlkIds.Add(tlkID);
                faceFx.Lines.Add(line);

                lineIndex++;
                linesAdded++;
            }

            if (linesAdded > 0)
            {
                props.AddOrReplaceProp(referencedSoundCues);
                faceFxExport.WriteProperties(props);
                faceFxExport.WriteBinary(faceFx);
            }

            return linesAdded;
        }

        private void GenerateFaceFxForAllLines(ExportEntry faceFxExport, FaceFXAnimSet faceFx, bool isFemaleAsset, FaceFXGenerationOptions options)
        {
            var faceFxBinary = new FaceFxAnimSetBinary(faceFx);
            foreach (var line in faceFx.Lines)
            {
                if (!int.TryParse(line.ID, out int tlkID))
                {
                    continue;
                }

                var tlkString = TLKManagerWPF.GlobalFindStrRefbyID(tlkID, _package);
                if (string.IsNullOrWhiteSpace(tlkString))
                {
                    continue;
                }

                var audioExport = FindVoiceStreamForLine(faceFxExport, line, isMale: !isFemaleAsset);
                var generator = new FaceFXGenerator(faceFxBinary, line, tlkString, audioExport, options);
                if (!generator.Generate())
                {
                    Debug.WriteLine($"BulkAudioImport: FaceFX generation failed for {faceFxExport.InstancedFullPath}.{line.NameAsString}: {generator.LastError ?? "Unknown error"}");
                }
            }

            faceFxExport.WriteBinary(faceFx);
        }

        private ExportEntry FindVoiceStreamForLine(ExportEntry faceFxExport, FaceFXLine line, bool isMale)
        {
            if (line?.ID == null || !int.TryParse(line.ID, out int tlkID))
            {
                return null;
            }

            var wwiseEventSearchName = $"VO_{tlkID:D6}_{(isMale ? "m" : "f")}";
            var wwiseStreamSearchName = $"{tlkID:D8}";
            var wwiseStreamSearchNameGendered = $"{wwiseStreamSearchName}_{(isMale ? "m" : "f")}";
            var wwiseStreamSearchNameWithUnderscores = $"_{tlkID}_";
            var wwiseStreamSearchNameWithUnderscoresGendered = $"{wwiseStreamSearchNameWithUnderscores}{(isMale ? "m" : "f")}";
            var wwiseEventExp = _package.Exports.FirstOrDefault(x => x.ClassName == "WwiseEvent" &&
                                                                      x.ObjectName.Name.Contains(wwiseEventSearchName, StringComparison.InvariantCultureIgnoreCase));
            if (wwiseEventExp != null)
            {
                var wwiseEvent = ObjectBinary.From<WwiseEvent>(wwiseEventExp);
                if (wwiseEvent.Links != null)
                {
                    foreach (var link in wwiseEvent.Links)
                    {
                        var possibleExports = link.WwiseStreams
                            .Where(x => faceFxExport.FileRef.IsUExport(x))
                            .Select(x => faceFxExport.FileRef.GetUExport(x))
                            .ToList();

                        var possible = possibleExports.FirstOrDefault(x => x.ObjectName.Name.Contains(wwiseStreamSearchNameGendered, StringComparison.InvariantCultureIgnoreCase));
                        if (possible != null) return possible;

                        possible = possibleExports.FirstOrDefault(x => x.ObjectName.Name.Contains(wwiseStreamSearchNameWithUnderscoresGendered, StringComparison.InvariantCultureIgnoreCase));
                        if (possible != null) return possible;

                        possible = possibleExports.FirstOrDefault(x => x.ObjectName.Name.Contains(wwiseStreamSearchName, StringComparison.InvariantCultureIgnoreCase));
                        if (possible != null) return possible;

                        possible = possibleExports.FirstOrDefault(x => x.ObjectName.Name.Contains(wwiseStreamSearchNameWithUnderscores, StringComparison.InvariantCultureIgnoreCase));
                        if (possible != null) return possible;
                    }
                }
            }

            return _package.Exports.FirstOrDefault(x => x.ClassName == "WwiseStream" &&
                                                        (x.ObjectName.Name.Contains(wwiseStreamSearchNameGendered, StringComparison.InvariantCultureIgnoreCase) ||
                                                         x.ObjectName.Name.Contains(wwiseStreamSearchNameWithUnderscoresGendered, StringComparison.InvariantCultureIgnoreCase) ||
                                                         x.ObjectName.Name.Contains(wwiseStreamSearchName, StringComparison.InvariantCultureIgnoreCase) ||
                                                         x.ObjectName.Name.Contains(wwiseStreamSearchNameWithUnderscores, StringComparison.InvariantCultureIgnoreCase)));
        }

        private static int ExtractTlkIdFromWwiseEventName(string eventName)
        {
            var match = Regex.Match(eventName, @"VO_(\d+)", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int tlkId))
            {
                return tlkId;
            }

            match = Regex.Match(eventName, @"(\d{6,})");
            if (match.Success && int.TryParse(match.Groups[1].Value, out tlkId))
            {
                return tlkId;
            }

            return -1;
        }

        private sealed class FaceFxAnimSetBinary : FaceFXAnimSetEditorControl.IFaceFXBinary
        {
            private readonly FaceFXAnimSet _animSet;

            public FaceFxAnimSetBinary(FaceFXAnimSet animSet)
            {
                _animSet = animSet;
            }

            public List<string> Names => _animSet.Names;
            public List<FaceFXLine> Lines => _animSet.Lines;
            public ObjectBinary Binary => _animSet;
        }

        /// <summary>
        /// Reads the duration in seconds from a supported WAV or MP3 input file.
        /// Returns 0 if the file cannot be parsed.
        /// </summary>
        private static float ReadAudioDurationSeconds(string audioPath)
        {
            try
            {
                return AudioInputConverter.GetDurationSeconds(audioPath);
            }
            catch
            {
                // If we can't read the file, return 0
            }
            return 0f;
        }

        /// <summary>
        /// Extracts the largest numeric sequence from a filename, which typically corresponds
        /// to the TLK string ID (e.g. "VO_14242616_m" → 14242616).
        /// Returns 0 if no number is found.
        /// </summary>
        private static long ExtractTlkNumber(string filename)
        {
            long maxNum = 0;
            foreach (Match match in Regex.Matches(filename, @"\d+"))
            {
                if (long.TryParse(match.Value, out var num) && num > maxNum)
                    maxNum = num;
            }
            return maxNum;
        }

        private static bool IsValidPackageObjectName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$");
        }

        /// <summary>
        /// Generates a deterministic GUID from a string seed, used to keep Sound IDs consistent
        /// between Actor-Mixer and Events XML generation.
        /// </summary>
        private static Guid GenerateDeterministicGuid(string seed)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seed));
            return new Guid(hash);
        }

        /// <summary>
        /// Generates a Wwise-compatible ShortID (uint) from a name using FNV-1 hash.
        /// </summary>
        private static uint GenerateShortId(string name)
        {
            // FNV-1 hash, matching Wwise's ID generation for names
            uint hash = 2166136261;
            foreach (char c in name.ToLowerInvariant())
            {
                hash *= 16777619;
                hash ^= c;
            }
            return hash;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
