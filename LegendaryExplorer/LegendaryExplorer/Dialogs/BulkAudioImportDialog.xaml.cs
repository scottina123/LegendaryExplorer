using System;
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
using Microsoft.Win32;

namespace LegendaryExplorer.Dialogs
{
    public partial class BulkAudioImportDialog : Window
    {
        public ObservableCollection<string> WavFiles { get; } = new();

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

            var isDialogue = IsDialogueBankCheckBox.IsChecked == true;

            ImportButton.IsEnabled = false;
            AddFilesButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            StatusTextBlock.Text = "Setting up Wwise project...";

            try
            {
                var result = await Task.Run(() => RunBulkAudioImport(bankName, isDialogue));
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

        private string RunBulkAudioImport(string bankName, bool isDialogue)
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

                var actorMixerDoc = XDocument.Load(actorMixerPath);
                var eventsDoc = XDocument.Load(eventsPath);
                var soundBanksDoc = XDocument.Load(soundBanksPath);

                var actorMixerWuId = actorMixerDoc.Root.Attribute("ID")?.Value;
                var eventsWuId = eventsDoc.Root.Attribute("ID")?.Value;

                // 4. Build Actor-Mixer Hierarchy XML
                var actorMixerId = $"{{{Guid.NewGuid()}}}";
                var actorMixerXml = BuildActorMixerXml(actorMixerWuId, bankName, actorMixerId, wavFiles);
                File.WriteAllText(actorMixerPath, actorMixerXml);

                // 5. Build Events XML
                var eventsXml = BuildEventsXml(eventsWuId, actorMixerWuId, wavFiles);
                File.WriteAllText(eventsPath, eventsXml);

                // 6. Build SoundBanks XML
                var soundBanksXml = BuildSoundBanksXml(soundBanksDoc.Root.Attribute("ID")?.Value,
                    bankName, actorMixerWuId, eventsWuId);
                File.WriteAllText(soundBanksPath, soundBanksXml);

                Dispatcher.Invoke(() => StatusTextBlock.Text = "Running WwiseCLI to generate soundbank...");

                // 7. Run WwiseCLI to generate soundbanks
                string projFile = Path.Combine(projectDir, "TemplateProject.wproj");
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

                // 8. Find the generated .bnk file
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

                // 9. Import the bank into the package using existing WwiseBankImport
                var importResult = WwiseBankImport.ImportBank(bnkPath, isDialogue, _package);
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
            System.Collections.Generic.List<string> wavFiles)
        {
            // Factory "Vorbis Quality High" conversion setting (from Factory Conversion Settings.wwu in template)
            const string vorbisHighId = "{53A9DE0F-3F4F-4B59-8614-3F9E3C7358FC}";
            const string vorbisHighWuId = "{F6B2880C-85E5-47FA-A126-645B5DFD9ACC}";
            // Master Audio Bus from the template's Master-Mixer Hierarchy
            const string masterBusId = "{1514A4D8-1DA6-412A-A17E-75CA0C2149F3}";
            const string masterBusWuId = "{DC056BE9-DEF6-455F-87D6-60D9DF9D80AD}";

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
            xml.AppendLine("\t\t\t\t\t\t\t\t<Value>-10</Value>");
            xml.AppendLine("\t\t\t\t\t\t\t</ValueList>");
            xml.AppendLine("\t\t\t\t\t\t</Property>");
            xml.AppendLine("\t\t\t\t\t</PropertyList>");
            xml.AppendLine("\t\t\t\t\t<ReferenceList>");
            xml.AppendLine("\t\t\t\t\t\t<Reference Name=\"Conversion\">");
            xml.AppendLine($"\t\t\t\t\t\t\t<ObjectRef Name=\"Vorbis Quality High\" ID=\"{vorbisHighId}\" WorkUnitID=\"{vorbisHighWuId}\"/>");
            xml.AppendLine("\t\t\t\t\t\t</Reference>");
            xml.AppendLine("\t\t\t\t\t\t<Reference Name=\"OutputBus\">");
            xml.AppendLine($"\t\t\t\t\t\t\t<ObjectRef Name=\"Master Audio Bus\" ID=\"{masterBusId}\" WorkUnitID=\"{masterBusWuId}\"/>");
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
        /// Builds the Events XML with a Play event per Sound.
        /// Event names follow the format {SoundName}_Play.
        /// </summary>
        private static string BuildEventsXml(string workUnitId, string actorMixerWuId,
            System.Collections.Generic.List<string> wavFiles)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var wavPath in wavFiles)
            {
                var soundName = Path.GetFileNameWithoutExtension(wavPath);
                var eventName = $"{soundName}_Play";
                var eventId = $"{{{Guid.NewGuid()}}}";
                var actionId = $"{{{Guid.NewGuid()}}}";
                var soundId = $"{{{GenerateDeterministicGuid(soundName)}}}";

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
