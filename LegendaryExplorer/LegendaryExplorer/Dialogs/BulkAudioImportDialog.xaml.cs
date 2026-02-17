using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.UnrealExtensions;
using LegendaryExplorerCore.Audio;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.Win32;

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

        public BulkAudioImportDialog(IMEPackage package)
        {
            _package = package;
            InitializeComponent();
            DataContext = this;
            CustomWindowChrome.ApplyCustomChrome(this);
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

            ImportButton.IsEnabled = false;
            AddFilesButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            StatusTextBlock.Text = "Setting up Wwise project...";

            try
            {
                var result = await Task.Run(() => RunBulkAudioImport(bankName, isDialogue, volume, outputBusName, generateGenderedEvents));
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

        private string RunBulkAudioImport(string bankName, bool isDialogue, double volume, string outputBusName, bool generateGenderedEvents)
        {
            var wavFiles = Dispatcher.Invoke(() => WavFiles.ToList());

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

                // 4. Build Actor-Mixer Hierarchy XML
                var actorMixerId = $"{{{Guid.NewGuid()}}}";
                var actorMixerXml = BuildActorMixerXml(actorMixerWuId, bankName, actorMixerId, wavFiles,
                    volume, outputBusName, outputBusId, outputBusWuId);
                File.WriteAllText(actorMixerPath, actorMixerXml);

                // 5. Build Events XML
                var eventsXml = BuildEventsXml(eventsWuId, actorMixerWuId, wavFiles, generateGenderedEvents);
                File.WriteAllText(eventsPath, eventsXml);

                // 6. Build SoundBanks XML
                var soundBanksXml = BuildSoundBanksXml(soundBanksDoc.Root.Attribute("ID")?.Value,
                    bankName, actorMixerWuId, eventsWuId);
                File.WriteAllText(soundBanksPath, soundBanksXml);

                Dispatcher.Invoke(() => StatusTextBlock.Text = "Running WwiseCLI to generate soundbank...");

                // 7. Enable estimated duration in the Wwise project so SoundbanksInfo.xml
                //    includes DurationMin/DurationMax for events.
                string projFile = Path.Combine(projectDir, "TemplateProject.wproj");
                EnableEstimatedDuration(projFile);

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
                var importResult = WwiseBankImport.ImportBank(bnkPath, isDialogue, _package);

                // 11. Set DurationSeconds on events if not already set by the importer.
                //     WwiseBankImport sets it when SoundbanksInfo.xml has DurationMin/DurationMax,
                //     but as a fallback we read the WAV file headers directly.
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
        /// </summary>
        private static string BuildActorMixerXml(string workUnitId, string bankName, string actorMixerId,
            List<string> wavFiles, double volume, string outputBusName, string outputBusId, string outputBusWuId)
        {
            // Factory "Vorbis Quality High" conversion setting (from Factory Conversion Settings.wwu in template)
            const string vorbisHighId = "{53A9DE0F-3F4F-4B59-8614-3F9E3C7358FC}";
            const string vorbisHighWuId = "{F6B2880C-85E5-47FA-A126-645B5DFD9ACC}";
            // Master Audio Bus from the template's Master-Mixer Hierarchy (used for individual Sound nodes)
            const string masterBusId = "{1514A4D8-1DA6-412A-A17E-75CA0C2149F3}";
            const string masterBusWuId = "{DC056BE9-DEF6-455F-87D6-60D9DF9D80AD}";

            var volumeStr = volume.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var sb = new System.Text.StringBuilder();
            foreach (var wavPath in wavFiles)
            {
                var soundName = Path.GetFileNameWithoutExtension(wavPath);
                var soundId = $"{{{GenerateDeterministicGuid(soundName)}}}";
                var sourceId = $"{{{Guid.NewGuid()}}}";

                sb.AppendLine($"\t\t\t\t\t\t<Sound Name=\"{soundName}\" ID=\"{soundId}\" ShortID=\"{GenerateShortId(soundName)}\">");
                sb.AppendLine("\t\t\t\t\t\t\t<PropertyList>");
                sb.AppendLine("\t\t\t\t\t\t\t\t<Property Name=\"IsStreamingEnabled\" Type=\"bool\">");
                sb.AppendLine("\t\t\t\t\t\t\t\t\t<ValueList>");
                sb.AppendLine("\t\t\t\t\t\t\t\t\t\t<Value>True</Value>");
                sb.AppendLine("\t\t\t\t\t\t\t\t\t</ValueList>");
                sb.AppendLine("\t\t\t\t\t\t\t\t</Property>");
                sb.AppendLine("\t\t\t\t\t\t\t</PropertyList>");
                sb.AppendLine("\t\t\t\t\t\t\t<ReferenceList>");
                sb.AppendLine("\t\t\t\t\t\t\t\t<Reference Name=\"Conversion\">");
                sb.AppendLine($"\t\t\t\t\t\t\t\t\t<ObjectRef Name=\"Vorbis Quality High\" ID=\"{vorbisHighId}\" WorkUnitID=\"{vorbisHighWuId}\"/>");
                sb.AppendLine("\t\t\t\t\t\t\t\t</Reference>");
                sb.AppendLine("\t\t\t\t\t\t\t\t<Reference Name=\"OutputBus\">");
                sb.AppendLine($"\t\t\t\t\t\t\t\t\t<ObjectRef Name=\"Master Audio Bus\" ID=\"{masterBusId}\" WorkUnitID=\"{masterBusWuId}\"/>");
                sb.AppendLine("\t\t\t\t\t\t\t\t</Reference>");
                sb.AppendLine("\t\t\t\t\t\t\t</ReferenceList>");
                sb.AppendLine("\t\t\t\t\t\t\t<ChildrenList>");
                sb.AppendLine($"\t\t\t\t\t\t\t\t<AudioFileSource Name=\"{soundName}\" ID=\"{sourceId}\" ShortID=\"{GenerateShortId(soundName + "_src")}\">");
                sb.AppendLine("\t\t\t\t\t\t\t\t\t<Language>SFX</Language>");
                sb.AppendLine($"\t\t\t\t\t\t\t\t\t<AudioFile>{soundName}.wav</AudioFile>");
                sb.AppendLine("\t\t\t\t\t\t\t\t</AudioFileSource>");
                sb.AppendLine("\t\t\t\t\t\t\t</ChildrenList>");
                sb.AppendLine("\t\t\t\t\t\t\t<ActiveSourceList>");
                sb.AppendLine($"\t\t\t\t\t\t\t\t<ActiveSource Name=\"{soundName}\" ID=\"{sourceId}\" Platform=\"Linked\"/>");
                sb.AppendLine("\t\t\t\t\t\t\t</ActiveSourceList>");
                sb.AppendLine("\t\t\t\t\t\t</Sound>");
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
        /// Builds the Events XML with Play events per Sound.
        /// When generateGenderedEvents is true, two events are created per sound:
        /// {baseName}_m_Play and {baseName}_f_Play (both targeting the same Sound).
        /// If the sound name already ends with _m or _f, the suffix is replaced for each variant.
        /// </summary>
        private static string BuildEventsXml(string workUnitId, string actorMixerWuId,
            List<string> wavFiles, bool generateGenderedEvents)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var wavPath in wavFiles)
            {
                var soundName = Path.GetFileNameWithoutExtension(wavPath);
                var soundId = $"{{{GenerateDeterministicGuid(soundName)}}}";

                if (generateGenderedEvents)
                {
                    var baseName = StripGenderSuffix(soundName);
                    AppendEventXml(sb, $"{baseName}_m_Play", soundName, soundId, actorMixerWuId);
                    AppendEventXml(sb, $"{baseName}_f_Play", soundName, soundId, actorMixerWuId);
                }
                else
                {
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
        /// Enables the "Generate Estimated Duration" setting in the Wwise project file (.wproj).
        /// This causes WwiseCLI to include DurationMin/DurationMax attributes on events in
        /// the generated SoundbanksInfo.xml, which WwiseBankImport uses to set DurationSeconds.
        /// </summary>
        private static void EnableEstimatedDuration(string wprojPath)
        {
            var doc = XDocument.Load(wprojPath);
            var propertyList = doc.Descendants("PropertyList").FirstOrDefault();
            if (propertyList != null)
            {
                propertyList.Add(new XElement("Property",
                    new XAttribute("Name", "SoundBankGenerateEstimatedDuration"),
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
        /// Only sets the property if it is not already present (i.e. not set by WwiseBankImport from
        /// SoundbanksInfo.xml duration data).
        /// </summary>
        private void SetEventDurations(Dictionary<string, float> eventDurations)
        {
            foreach (var export in _package.Exports.Where(e => e.ClassName == "WwiseEvent"))
            {
                if (eventDurations.TryGetValue(export.ObjectNameString, out var duration) && duration > 0f)
                {
                    var props = export.GetProperties();
                    if (!props.ContainsNamedProp("DurationSeconds"))
                    {
                        props.AddOrReplaceProp(new FloatProperty(duration, "DurationSeconds"));
                        export.WriteProperties(props);
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
