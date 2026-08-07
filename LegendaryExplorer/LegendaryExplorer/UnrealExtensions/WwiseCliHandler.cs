using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Xml;
using System.Xml.Linq;
using LegendaryExplorer.Audio;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.GameInterop;
using LegendaryExplorer.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.Tools.WwiseEditor;
using LegendaryExplorerCore.Audio;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.UnrealExtensions
{
    public static class WwiseCliHandler
    {
        public static string GetWwiseCliPath(MEGame game) => game switch
        {
            MEGame.ME3 => Settings.Wwise_3773Path,
            MEGame.LE2 => Settings.Wwise_7110Path,
            MEGame.LE3 => Settings.Wwise_7110Path,
            _ => throw new NotImplementedException($"Wwise CLI path unavailable for {game}")
        };

        public static string GetWwiseGUIPath(MEGame game) => game switch
        {
            MEGame.ME3 => Path.Combine(Directory.GetParent(Settings.Wwise_3773Path).FullName, "Wwise.exe"),
            MEGame.LE2 => Path.Combine(Directory.GetParent(Settings.Wwise_7110Path).FullName, "Wwise.exe"),
            MEGame.LE3 => Path.Combine(Directory.GetParent(Settings.Wwise_7110Path).FullName, "Wwise.exe"),
            _ => throw new NotImplementedException($"Wwise GUI path unavailable for {game}")
        };

        public static string GetWwiseTemplateProject(MEGame game) => game switch
        {
            MEGame.ME3 => Path.Combine(AppDirectories.ExecFolder, "WwiseTemplateProjectV3773.zip"),
            MEGame.LE2 => Path.Combine(AppDirectories.ExecFolder, "WwiseTemplateProjectV7110.zip"),
            MEGame.LE3 => Path.Combine(AppDirectories.ExecFolder, "WwiseTemplateProjectV7110.zip"),
            _ => throw new NotImplementedException($"Wwise template project unavailable for {game}")
        };

        /// <summary>
        /// Checks the Wwise path from settings for the selected game, and shows the WwisePathDialog if it is incorrect
        /// </summary>
        /// <param name="game">Game to check path for</param>
        /// <returns>True if the Wwise path is correctly set</returns>
        public static bool CheckWwisePathForGame(MEGame game)
        {
            var path = WwiseCliHandler.GetWwiseCliPath(game);
            if (string.IsNullOrEmpty(path) || !WwiseVersions.IsCorrectWwiseVersion(game, path))
            {
                SetWwisePathDialog swpd = new();
                swpd.ShowDialog();
                return swpd.PathCorrect;
            }
            return true;
        }

        /// <summary>
        /// Returns true if the specified WwiseCLI paths are of the correct version,
        /// Shows a dialog box if they are not
        /// </summary>
        /// <param name="Wwise7110">Optional: path to WwiseCLI v7110</param>
        /// <param name="Wwise3773">Optional: path to WwiseCLI v3773</param>
        /// <returns>True if all installed versions are correct</returns>
        public static bool EnsureWwiseVersions(string Wwise7110 = "", string Wwise3773 = "")
        {
            var result3773 = CheckWwiseCLIVersion(Wwise3773, MEGame.ME3);
            if (result3773 is false) return false;

            var result7110 = CheckWwiseCLIVersion(Wwise7110, MEGame.LE3);
            if (result7110 is false) return false;

            return true;

            bool CheckWwiseCLIVersion(string path, MEGame game)
            {
                // Return true if file does not exist - we want to continue if user does not have Wwise installed
                if (File.Exists(path))
                {
                    var wwiseVersion = WwiseVersions.WwiseVersion(game);
                    var wwiseFullVersion = WwiseVersions.WwiseFullVersion(game);

                    var fileInfo = new FileInfo(path);
                    var versionInfo = FileVersionInfo.GetVersionInfo(path);
                    string version = versionInfo.ProductVersion;
                    if (!fileInfo.Name.Equals("WwiseCLI.exe", StringComparison.CurrentCultureIgnoreCase))
                    {
                        MessageBox.Show($"The selected executable is not WwiseCLI.exe. \nPlease select WwiseCLI.exe build {wwiseVersion} to use this feature.");
                        return false;
                    }

                    if (version != wwiseFullVersion)
                    {
                        //wrong version
                        MessageBox.Show($"WwiseCLI.exe found, but it's the wrong version: {version}" +
                                        $".\nInstall Wwise Build {wwiseVersion} 64bit to use this feature.");
                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// Converts a WAV/MP3 file or folder of WAV/MP3 files to Wwise encoded audio for the specified game.
        /// MP3 inputs are decoded to 16-bit PCM WAV before being passed to WwiseCLI.
        /// </summary>
        /// <param name="game">Game to convert for - Wwise path for game must be configured</param>
        /// <param name="fileOrFolderPath">Path of a supported audio file or a folder containing supported audio files</param>
        /// <param name="conversionSettings">Settings to place into the templated project that will be used when CLI runs</param>
        /// <returns></returns>
        public static async Task<string> RunWwiseConversion(MEGame game, string fileOrFolderPath, WwiseConversionSettingsPackage conversionSettings)
        {
            /* The process for converting is going to be pretty in depth but will make converting files much easier and faster.
                         * 1. User chooses a folder of .wav/.mp3 files (or this method is passed one file and we return that conversion)
                         * 2. Conversion takes place
                         * 
                         * Program steps when conversion starts:
                         * 1. Extract the Wwise TemplateProject as it is required for command line. This is extracted to the root of %Temp%.
                         * 2. Generate the external sources file that points to the folder and each item to convert within it
                         * 3. Run the generate command
                         * 4. Move files from OutputFiles directory in the project
                         * 5. Delete the project
                         * */

            string wwiseCLIPath = GetWwiseCliPath(game);
            if (string.IsNullOrEmpty(wwiseCLIPath)) throw new ArgumentException("Wwise CLI path not configured");

            //Extract the template project to temp
            string templateproject = GetWwiseTemplateProject(game);
            string templatefolder = Path.Combine(Path.GetTempPath(), "TemplateProject");

            using (StreamReader stream = new StreamReader(templateproject))
            {
                await TryDeleteDirectory(templatefolder);
                ZipArchive archive = new ZipArchive(stream.BaseStream);
                archive.ExtractToDirectory(Path.GetTempPath());
            }

            // Normalize every input into a private staging directory. WwiseCLI receives only
            // 16-bit PCM WAV files regardless of whether the user selected WAV or MP3 input.
            string[] inputFiles;
            string outputParent;
            bool isSingleFile = !Directory.Exists(fileOrFolderPath);
            if (Directory.Exists(fileOrFolderPath))
            {
                inputFiles = Directory.EnumerateFiles(fileOrFolderPath, "*", SearchOption.TopDirectoryOnly)
                    .Where(AudioInputConverter.IsSupportedAudioFile)
                    .ToArray();
                outputParent = fileOrFolderPath;
            }
            else
            {
                if (!AudioInputConverter.IsSupportedAudioFile(fileOrFolderPath))
                {
                    throw new NotSupportedException("Only WAV and MP3 files can be converted to Wwise audio.");
                }

                inputFiles = [fileOrFolderPath];
                outputParent = Directory.GetParent(fileOrFolderPath)?.FullName ?? Path.GetTempPath();
            }

            if (inputFiles.Length == 0)
            {
                throw new InvalidOperationException("No WAV or MP3 files were found to convert.");
            }

            var duplicateOutputName = inputFiles
                .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateOutputName != null)
            {
                throw new InvalidOperationException(
                    $"Multiple input files would produce the same output name '{duplicateOutputName.Key}'. Rename one of those files and try again.");
            }

            string stagedAudioDirectory = Path.Combine(templatefolder, "AudioInput");
            Directory.CreateDirectory(stagedAudioDirectory);
            string[] filesToConvert = inputFiles.Select(inputFile =>
            {
                var stagedWavePath = Path.Combine(stagedAudioDirectory,
                    Path.GetFileNameWithoutExtension(inputFile) + ".wav");
                AudioInputConverter.ConvertToPcmWave(inputFile, stagedWavePath);
                return stagedWavePath;
            }).ToArray();

            XElement externalSourcesList = new XElement("ExternalSourcesList", new XAttribute("SchemaVersion", 1.ToString()), new XAttribute("Root", stagedAudioDirectory));
            foreach (string file in filesToConvert)
            {
                XElement source = new XElement("Source", new XAttribute("Path", Path.GetFileName(file)), new XAttribute("Conversion", "Vorbis"));
                externalSourcesList.Add(source);
            }

            //Write ExternalSources.wsources
            string wsourcesFile = Path.Combine(templatefolder, "ExternalSources.wsources");

            File.WriteAllText(wsourcesFile, externalSourcesList.ToString());
            Debug.WriteLine(externalSourcesList.ToString());

            string conversionSettingsFile = Path.Combine(templatefolder, "Conversion Settings", "Default Work Unit.wwu");
            XmlDocument conversionDoc = new XmlDocument();
            conversionDoc.Load(conversionSettingsFile);

            //Samplerate
            string XmlConversion3773 =
                "/WwiseDocument/Conversions/Conversion/PropertyList/Property[@Name='SampleRate']/ValueList/Value[@Platform='Windows']";
            string XmlConversion7110 =
                "/WwiseDocument/Conversions/WorkUnit/ChildrenList/Conversion/PropertyList/Property[@Name='SampleRate']/ValueList/Value[@Platform='Windows']";
            XmlNode node = conversionDoc.DocumentElement.SelectSingleNode(game is MEGame.ME3 ? XmlConversion3773 : XmlConversion7110);
            node.InnerText = conversionSettings.TargetSamplerate.ToString();
            conversionDoc.Save(conversionSettingsFile);
            //Run Conversion

            string projFile = Path.Combine(templatefolder, "TemplateProject.wproj");
            Process process = new Process
            {
                StartInfo =
                {
                    FileName = wwiseCLIPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    Arguments = $"\"{projFile}\" -ConvertExternalSources Windows",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true
                }
            };
            //uncomment the following lines to view output from wwisecli
            //DebugOutput.StartDebugger("Wwise Wav to Ogg Converter");
            //process.OutputDataReceived += (s, eventArgs) => { Debug.WriteLine(eventArgs.Data);};
            //process.ErrorDataReceived += (s, eventArgs) => { Debug.WriteLine(eventArgs.Data); };

            process.Start();
            //process.BeginOutputReadLine();
            process.WaitForExit();
            Debug.WriteLine("Process output: \n" + process.StandardOutput.ReadToEnd());
            process.Close();

            //Files generates
            string outputDirectory = Path.Combine(Path.GetTempPath(), "TemplateProject", "OutputFiles");
            string copyToDirectory = Path.Combine(outputParent, "Converted");
            Directory.CreateDirectory(copyToDirectory);

            var extension = game is MEGame.ME3 ? ".ogg" : ".wem";

            foreach (string file in filesToConvert)
            {
                string fileName = Path.GetFileName(Path.ChangeExtension(file, extension));
                if (fileName is null) break;
                File.Copy(Path.Combine(outputDirectory, fileName), Path.Combine(copyToDirectory, fileName), true);
            }

            var deleteResult = await TryDeleteDirectory(templatefolder);
            Debug.WriteLine("Deleted templatedproject: " + deleteResult);

            if (isSingleFile)
            {
                return Path.Combine(copyToDirectory, Path.GetFileNameWithoutExtension(inputFiles[0]) + extension);
            }

            return copyToDirectory;
        }

        public static async Task<bool> TryDeleteDirectory(string directoryPath, int maxRetries = 10, int millisecondsDelay = 30)
        {
            if (directoryPath == null)
                throw new ArgumentNullException(nameof(directoryPath));
            if (maxRetries < 1)
                throw new ArgumentOutOfRangeException(nameof(maxRetries));
            if (millisecondsDelay < 1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsDelay));

            for (int i = 0; i < maxRetries; ++i)
            {
                try
                {
                    if (Directory.Exists(directoryPath))
                    {
                        Directory.Delete(directoryPath, true);
                    }

                    return true;
                }
                catch (IOException)
                {
                    await Task.Delay(millisecondsDelay);
                }
                catch (UnauthorizedAccessException)
                {
                    await Task.Delay(millisecondsDelay);
                }
            }

            return false;
        }

        internal static async void DeleteTemplateProjectDirectory()
        {
            var templateDirectory = Path.Combine(Path.GetTempPath(), "TemplateProject");
            if (Directory.Exists(templateDirectory))
            {
                await TryDeleteDirectory(templateDirectory);
            }
        }

        public static string CreateNewProjectForBank(ExportEntry export, string projectOutputDirectory)
        {
            return "";
            //WwiseIO.ExportBankToProject(export, projectOutputDirectory);
        }

        /// <summary>
        /// LE ONLY - Generates a new WwiseProject with 7110 Wwise at the specified path
        /// </summary>
        /// <param name="projectDir"></param>
        public static string CreateNewProject(string projectDir)
        {
            InteropModInstaller.DeleteFilesAndFoldersRecursively(projectDir);

            var wwiseCLIPath = GetWwiseCliPath(MEGame.LE3);
            var projFile = Path.GetFileNameWithoutExtension(projectDir) + ".wproj";
            var projPath = Path.Combine(projectDir, projFile);
            Process process = new Process
            {
                StartInfo =
                {
                    FileName = wwiseCLIPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    Arguments = $"\"{projPath}\" -CreateNewProject -Platform Windows",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true
                }
            };
            process.Start();
            process.WaitForExit();

            // Read output.

            return projPath;
        }

        public static void RunWwiseInAutomatedMode(MEGame game, string projectPath)
        {
            var wwiseGui = GetWwiseGUIPath(game);
            Process.Start(wwiseGui, $"\"{projectPath}\" -AutomationMode -SetTitleBarText \"Automated by Legendary Explorer\"");
        }
    }
}
