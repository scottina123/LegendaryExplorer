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
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.UnrealExtensions;
using LegendaryExplorerCore.Audio;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.Win32;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Dialogs
{
    public partial class BulkAudioImportDialog : Window
    {
        public ObservableCollection<string> WavFiles { get; } = new();

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

        public BulkAudioImportDialog(
            IMEPackage package,
            string bankPackageName = "audio",
            string bankStreamingAudioPackageName = "int",
            IEnumerable<string> initialWavFiles = null,
            string initialBankName = null,
            bool? isDialogueBank = null,
            bool? generateGenderedEvents = null)
        {
            _package = package;
            _bankPackageName = bankPackageName;
            _bankStreamingAudioPackageName = bankStreamingAudioPackageName;
            InitializeComponent();
            DataContext = this;
            CustomWindowChrome.ApplyCustomChrome(this);

            if (!string.IsNullOrWhiteSpace(initialBankName))
            {
                BankNameTextBox.Text = initialBankName;
            }

            if (initialWavFiles != null)
            {
                foreach (var file in initialWavFiles.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    WavFiles.Add(file);
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

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (WavFiles.Count == 0)
            {
                MessageBox.Show("No WAV files have been added.", "No files", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            ImportButton.IsEnabled = false;
            AddFilesButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            StatusTextBlock.Text = "Setting up Wwise project...";

            try
            {
                var result = await Task.Run(() => RunBulkAudioImport(bankName, isDialogue, volume, outputBusName, generateGenderedEvents, loopAudio, applyRadioEffect));
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

        private string RunBulkAudioImport(string bankName, bool isDialogue, double volume, string outputBusName, bool generateGenderedEvents, bool loopAudio, bool applyRadioEffect)
        {
            var wavFiles = Dispatcher.Invoke(() => WavFiles.ToList());

            // Sort WAV files by TLK number so exports are created in numerical order
            wavFiles.Sort((a, b) => ExtractTlkNumber(Path.GetFileNameWithoutExtension(a))
                .CompareTo(ExtractTlkNumber(Path.GetFileNameWithoutExtension(b))));

            // 1. Extract template project
            string templateZip = WwiseCliHandler.GetWwiseTemplateProject(_package.Game);
            string tempDir = Path.Combine(Path.GetTempPath(), $"LEX_BulkAudio_{Guid.NewGuid():N}");
            string projectDir = Path.Combine(tempDir, "TemplateProject");

            try
            {
                ZipFile.ExtractToDirectory(templateZip, tempDir);

                // 2. Copy WAV files into the project's Originals/SFX folder
                string originalsDir = Path.Combine(projectDir, "Originals", "SFX");
                Directory.CreateDirectory(originalsDir);
                foreach (var wavPath in wavFiles)
                {
                    File.Copy(wavPath, Path.Combine(originalsDir, Path.GetFileName(wavPath)), true);
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

                // 3b. Create ShareSets work unit for the radio effect if needed
                string effectWuId = null;
                if (applyRadioEffect)
                {
                    effectWuId = "{E8613F7D-BAD3-45CD-A3ED-505576F31277}";
                    string shareSetsDir = Path.Combine(projectDir, "ShareSets");
                    Directory.CreateDirectory(shareSetsDir);
                    string shareSetsPath = Path.Combine(shareSetsDir, "Default Work Unit.wwu");
                    var shareSetsXml = BuildShareSetsWorkUnitXml(effectWuId);
                    File.WriteAllText(shareSetsPath, shareSetsXml);
                }

                // 4. Build Actor-Mixer Hierarchy XML
                var actorMixerId = $"{{{Guid.NewGuid()}}}";
                var actorMixerXml = BuildActorMixerXml(actorMixerWuId, bankName, actorMixerId, wavFiles,
                    volume, outputBusName, outputBusId, outputBusWuId, generateGenderedEvents, loopAudio,
                    applyRadioEffect, effectWuId);
                File.WriteAllText(actorMixerPath, actorMixerXml);

                // 5. Build Events XML
                var eventsXml = BuildEventsXml(eventsWuId, actorMixerWuId, wavFiles, generateGenderedEvents);
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

                Dispatcher.Invoke(() => StatusTextBlock.Text = "Importing soundbank into package...");

                // 10. Import the bank into the package using existing WwiseBankImport
                //     Place everything under a top-level "audio" folder:
                //     - WwiseBank and WwiseEvents go directly under "audio"
                //     - WwiseStreams go under "audio.int"
                var importResult = WwiseBankImport.ImportBank(bnkPath, isDialogue, _package,
                    bankPackageName: _bankPackageName, bankStreamingAudioPackageName: _bankStreamingAudioPackageName);

                // 11. Set DurationSeconds on events from WAV file headers.
                //     This is critical for dialogue: without DurationSeconds the game's dialogue
                //     system cannot determine when a line has finished, causing nodes to get stuck.
                //     We always override whatever WwiseBankImport set because the Wwise estimated
                //     duration data is often missing for streaming sounds.
                if (importResult == null)
                {
                    var eventDurations = BuildEventDurationMap(wavFiles, generateGenderedEvents);
                    SetEventDurations(eventDurations);
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
        /// Builds the Actor-Mixer Hierarchy XML with an ActorMixer containing a Sound per WAV file,
        /// each configured for streaming with Vorbis Quality High conversion.
        /// When generateGenderedEvents is true, two Sound nodes are created per WAV file
        /// (baseName_m and baseName_f), each with a unique ID but referencing the same WAV.
        /// This is necessary because in Wwise, each event must target its own Sound node —
        /// sharing a Sound between events breaks event completion tracking in the bank.
        /// </summary>
        private static string BuildActorMixerXml(string workUnitId, string bankName, string actorMixerId,
            List<string> wavFiles, double volume, string outputBusName, string outputBusId, string outputBusWuId,
            bool generateGenderedEvents, bool loopAudio, bool applyRadioEffect, string effectWuId)
        {
            // Factory "Vorbis Quality High" conversion setting (from Factory Conversion Settings.wwu in template)
            const string vorbisHighId = "{53A9DE0F-3F4F-4B59-8614-3F9E3C7358FC}";
            const string vorbisHighWuId = "{F6B2880C-85E5-47FA-A126-645B5DFD9ACC}";
            // Master Audio Bus from the template's Master-Mixer Hierarchy (used for individual Sound nodes)
            const string masterBusId = "{1514A4D8-1DA6-412A-A17E-75CA0C2149F3}";

            var volumeStr = volume.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var sb = new System.Text.StringBuilder();
            foreach (var wavPath in wavFiles)
            {
                var soundName = Path.GetFileNameWithoutExtension(wavPath);

                if (generateGenderedEvents)
                {
                    // Create separate Sound nodes for _m and _f so each event targets its own Sound
                    var baseName = StripGenderSuffix(soundName);
                    AppendSoundXml(sb, $"{baseName}_m", soundName, vorbisHighId, vorbisHighWuId, masterBusId, outputBusWuId, loopAudio);
                    AppendSoundXml(sb, $"{baseName}_f", soundName, vorbisHighId, vorbisHighWuId, masterBusId, outputBusWuId, loopAudio);
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
            if (applyRadioEffect && effectWuId != null)
            {
                xml.AppendLine("\t\t\t\t\t\t<Reference Name=\"Effect0\" PluginName=\"Wwise Parametric EQ\" CompanyID=\"0\" PluginID=\"105\" PluginType=\"3\">");
                xml.AppendLine($"\t\t\t\t\t\t\t<ObjectRef Name=\"Dual_Filters_Radio_Comm\" ID=\"{{69479ACD-2C87-4007-B83E-55210A3B36B7}}\" WorkUnitID=\"{effectWuId}\"/>");
                xml.AppendLine("\t\t\t\t\t\t</Reference>");
            }
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
        /// Builds the Events XML with Play events per Sound.
        /// When generateGenderedEvents is true, two events are created per WAV file:
        /// {baseName}_m_Play targeting the {baseName}_m Sound, and
        /// {baseName}_f_Play targeting the {baseName}_f Sound.
        /// Each event must target its own Sound node — sharing a Sound between events
        /// breaks event completion tracking in the generated Wwise bank.
        /// </summary>
        private static string BuildEventsXml(string workUnitId, string actorMixerWuId,
            List<string> wavFiles, bool generateGenderedEvents)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var wavPath in wavFiles)
            {
                var soundName = Path.GetFileNameWithoutExtension(wavPath);

                if (generateGenderedEvents)
                {
                    var baseName = StripGenderSuffix(soundName);
                    var mSoundName = $"{baseName}_m";
                    var fSoundName = $"{baseName}_f";
                    var mSoundId = $"{{{GenerateDeterministicGuid(mSoundName)}}}";
                    var fSoundId = $"{{{GenerateDeterministicGuid(fSoundName)}}}";
                    AppendEventXml(sb, $"{baseName}_m_Play", mSoundName, mSoundId, actorMixerWuId);
                    AppendEventXml(sb, $"{baseName}_f_Play", fSoundName, fSoundId, actorMixerWuId);
                }
                else
                {
                    var soundId = $"{{{GenerateDeterministicGuid(soundName)}}}";
                    AppendEventXml(sb, $"{soundName}_Play", soundName, soundId, actorMixerWuId);
                }
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
        /// Builds the ShareSets work unit XML containing the Dual_Filters_Radio_Comm Parametric EQ
        /// effect. This ShareSet is referenced by the ActorMixer's Effect0 reference to apply the
        /// radio communication filter to all sounds in the bank.
        /// </summary>
        private static string BuildShareSetsWorkUnitXml(string workUnitId)
        {
            const string effectId = "{69479ACD-2C87-4007-B83E-55210A3B36B7}";

            var xml = new System.Text.StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            xml.AppendLine($"<WwiseDocument Type=\"WorkUnit\" ID=\"{workUnitId}\" SchemaVersion=\"94\">");
            xml.AppendLine("\t<ShareSets>");
            xml.AppendLine($"\t\t<WorkUnit Name=\"Default Work Unit\" ID=\"{workUnitId}\" PersistMode=\"Standalone\">");
            xml.AppendLine("\t\t\t<ChildrenList>");
            xml.AppendLine($"\t\t\t\t<Effect Name=\"Dual_Filters_Radio_Comm\" ID=\"{effectId}\" PluginName=\"Wwise Parametric EQ\" CompanyID=\"0\" PluginID=\"105\" PluginType=\"3\">");
            xml.AppendLine("\t\t\t\t\t<PropertyList>");
            xml.AppendLine("\t\t\t\t\t\t<Property Name=\"Band1FilterType\" Type=\"int32\" Value=\"3\"/>");
            xml.AppendLine("\t\t\t\t\t\t<Property Name=\"Band1Frequency\" Type=\"Real64\" Value=\"300\"/>");
            xml.AppendLine("\t\t\t\t\t\t<Property Name=\"Band1QFactor\" Type=\"Real64\" Value=\"0.707\"/>");
            xml.AppendLine("\t\t\t\t\t\t<Property Name=\"Band2FilterType\" Type=\"int32\" Value=\"4\"/>");
            xml.AppendLine("\t\t\t\t\t\t<Property Name=\"Band2Frequency\" Type=\"Real64\" Value=\"3000\"/>");
            xml.AppendLine("\t\t\t\t\t\t<Property Name=\"Band2QFactor\" Type=\"Real64\" Value=\"0.707\"/>");
            xml.AppendLine("\t\t\t\t\t\t<Property Name=\"OutputLevel\" Type=\"Real64\" Value=\"12\"/>");
            xml.AppendLine("\t\t\t\t\t</PropertyList>");
            xml.AppendLine("\t\t\t\t</Effect>");
            xml.AppendLine("\t\t\t</ChildrenList>");
            xml.AppendLine("\t\t</WorkUnit>");
            xml.AppendLine("\t</ShareSets>");
            xml.AppendLine("</WwiseDocument>");
            return xml.ToString();
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
            foreach (var wavPath in wavFiles)
            {
                var soundName = Path.GetFileNameWithoutExtension(wavPath);
                var duration = ReadWavDurationSeconds(wavPath);

                if (generateGenderedEvents)
                {
                    var baseName = StripGenderSuffix(soundName);
                    map[$"{baseName}_m_Play"] = duration;
                    map[$"{baseName}_f_Play"] = duration;
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

        /// <summary>
        /// Reads the duration in seconds from a WAV file by parsing its RIFF header.
        /// Returns 0 if the file cannot be parsed.
        /// </summary>
        private static float ReadWavDurationSeconds(string wavPath)
        {
            try
            {
                using var fs = File.OpenRead(wavPath);
                using var reader = new BinaryReader(fs);

                // RIFF header: "RIFF" (4) + file size (4) + "WAVE" (4)
                if (fs.Length < 12)
                    return 0f;
                reader.ReadBytes(4); // "RIFF"
                reader.ReadInt32();  // file size
                reader.ReadBytes(4); // "WAVE"

                int byteRate = 0;

                while (fs.Position + 8 <= fs.Length)
                {
                    var chunkId = new string(reader.ReadChars(4));
                    var chunkSize = reader.ReadInt32();

                    if (chunkId == "fmt ")
                    {
                        var startPos = fs.Position;
                        reader.ReadInt16();  // audio format
                        reader.ReadInt16();  // num channels
                        reader.ReadInt32();  // sample rate
                        byteRate = reader.ReadInt32();
                        var bytesRead = (int)(fs.Position - startPos);
                        if (chunkSize > bytesRead)
                            reader.ReadBytes(chunkSize - bytesRead);
                    }
                    else if (chunkId == "data")
                    {
                        if (byteRate > 0)
                            return (float)chunkSize / byteRate;
                        return 0f;
                    }
                    else
                    {
                        if (fs.Position + chunkSize > fs.Length)
                            return 0f;
                        reader.ReadBytes(chunkSize);
                    }

                    // WAV chunks are word-aligned
                    if (chunkSize % 2 != 0 && fs.Position < fs.Length)
                        reader.ReadByte();
                }
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
