using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.Tools.AssetDatabase.Filters;
using LegendaryExplorer.Tools.Dialogue_Editor.DialogueEditorExperiments;
using LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.ExportLoaderControls.MaterialEditor;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Collections;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Microsoft.WindowsAPICodePack.Dialogs;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using Texture2D = LegendaryExplorerCore.Unreal.Classes.Texture2D;
using TextureImage = LegendaryExplorerCore.Textures.Image;

namespace LegendaryExplorer.Tools.PackageEditor.Experiments
{
    public static class PackageEditorExperimentsScottina
    {
        private static readonly string[] MaterialPropertiesToPreserve =
        [
            "Expressions",
            "ReferencedTextures"
        ];

        private static readonly string[] MaterialInstancePropertiesToPreserve =
        [
            "Parent",
            "ReferencedTextures",
            "TextureParameterValues",
            "VectorParameterValues",
            "ScalarParameterValues"
        ];

        private sealed record MaterialSearchProfile(
            string ObjectName,
            bool IsInstance,
            string ParentMaterialKey,
            HashSet<string> UsedOn,
            HashSet<string> ScalarParameters,
            HashSet<string> VectorParameters,
            HashSet<string> TextureParameters,
            Dictionary<string, int> TextureTypeCounts);

        private sealed record PreservedMaterialState(
            string[] PropertyNames,
            Dictionary<string, Property> Properties,
            Material OriginalBinary,
            MaterialInstance OriginalInstanceBinary);

        private sealed record RepairSummary(
            int FixedCount,
            int WarningCount,
            List<string> Failures,
            List<string> Warnings);

        private sealed record FaceFxLineSectionDeleteSummary(
            int ModifiedAssetCount,
            int ModifiedLineCount,
            List<string> Failures);

        private sealed record PlayerFaceFxFolderRepairSummary(
            int FilesScanned,
            int EligibleFiles,
            int ModifiedFiles,
            int ModifiedConversations,
            int ModifiedReferences,
            List<string> Failures,
            List<string> Warnings);

        private sealed record TextureMoveSummary(
            int MovedCount,
            int FailedCount,
            List<string> Messages,
            List<string> Failures);

        private sealed record FaceFxFolderScanSummary(
            List<string> PackageFiles,
            int FilesWithMatches,
            int FemaleAssetCount,
            int FemaleLineCount,
            int MaleAssetCount,
            int MaleLineCount,
            int UnknownGenderAssetCount,
            List<string> Failures);

        private sealed record FaceFxGenerationSettings(
            FaceFXSpecies Species,
            float LipSyncIntensity,
            bool GenerateBlinkAnimation,
            float BlinkFrequency);

        private sealed record FaceFxGenerationSummary(
            int FilesScanned,
            int FilesWithMatches,
            int ModifiedFileCount,
            int AssetCount,
            int LineCount,
            int SkippedLineCount,
            int ErrorCount,
            List<string> Messages);

        private sealed class BlankBioConversationDialog : Window
        {
            private readonly TextBox _topPackageTextBox = new() { MinWidth = 320 };
            private readonly TextBox _conversationNameTextBox = new() { MinWidth = 320 };
            private readonly TextBlock _validationText = new() { TextWrapping = TextWrapping.Wrap };
            private readonly Button _okButton = new() { Content = "_Create", IsDefault = true, MinWidth = 70 };

            public string TopPackageName => _topPackageTextBox.Text.Trim();
            public string ConversationName => _conversationNameTextBox.Text.Trim();

            public BlankBioConversationDialog(Window owner, string defaultTopPackageName, string defaultConversationName)
            {
                CustomWindowChrome.ApplyCustomChrome(this);
                Title = "Generate blank BioConversation";
                Owner = owner;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                SizeToContent = SizeToContent.WidthAndHeight;
                ResizeMode = ResizeMode.NoResize;
                _topPackageTextBox.Text = defaultTopPackageName;
                _conversationNameTextBox.Text = defaultConversationName;

                var content = new StackPanel { Margin = new Thickness(12) };
                content.Children.Add(new TextBlock { Text = "Top-level package name:" });
                content.Children.Add(_topPackageTextBox);
                content.Children.Add(new TextBlock { Text = "Conversation base name:", Margin = new Thickness(0, 10, 0, 0) });
                content.Children.Add(_conversationNameTextBox);
                content.Children.Add(_validationText);

                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 12, 0, 0)
                };
                _okButton.Click += (_, _) => DialogResult = true;
                buttons.Children.Add(_okButton);
                var cancelButton = new Button { Content = "_Cancel", IsCancel = true, MinWidth = 70, Margin = new Thickness(8, 0, 0, 0) };
                buttons.Children.Add(cancelButton);
                content.Children.Add(buttons);
                Content = content;

                _topPackageTextBox.TextChanged += (_, _) => ValidateInput();
                _conversationNameTextBox.TextChanged += (_, _) => ValidateInput();
                Loaded += (_, _) => _topPackageTextBox.Focus();
                ValidateInput();
            }

            private void ValidateInput()
            {
                bool topPackageValid = IsValidPackageObjectName(TopPackageName);
                bool conversationNameValid = IsValidPackageObjectName(ConversationName);
                _okButton.IsEnabled = topPackageValid && conversationNameValid;
                _validationText.Text = _okButton.IsEnabled
                    ? string.Empty
                    : "Names must start with a letter or underscore and contain only letters, numbers, or underscores.";
            }
        }

        private sealed class FaceFxAnimSetBinary(FaceFXAnimSet animSet) : FaceFXAnimSetEditorControl.IFaceFXBinary
        {
            public List<string> Names => animSet.Names;
            public List<FaceFXLine> Lines => animSet.Lines;
            public ObjectBinary Binary => animSet;
        }

        private static FaceFxFolderScanSummary ScanFolderForMatchingFaceFxAnimSets(string folderPath, string nameFragment)
        {
            List<string> packageFiles = Directory.EnumerateFiles(folderPath, "*.pcc", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            int filesWithMatches = 0;
            int femaleAssetCount = 0;
            int femaleLineCount = 0;
            int maleAssetCount = 0;
            int maleLineCount = 0;
            int unknownGenderAssetCount = 0;
            var failures = new List<string>();

            foreach (string packageFile in packageFiles)
            {
                try
                {
                    using var package = MEPackageHandler.OpenMEPackage(packageFile, forceLoadFromDisk: true);
                    List<ExportEntry> matchingAnimSets = GetMatchingFaceFxAnimSets(package, nameFragment);
                    if (matchingAnimSets.Count == 0)
                    {
                        continue;
                    }

                    filesWithMatches++;
                    foreach (ExportEntry animSetExport in matchingAnimSets)
                    {
                        FaceFXAnimSet animSet = animSetExport.GetBinaryData<FaceFXAnimSet>();
                        if (animSetExport.ObjectNameString.EndsWith("_F", StringComparison.OrdinalIgnoreCase))
                        {
                            femaleAssetCount++;
                            femaleLineCount += animSet.Lines.Count;
                        }
                        else if (animSetExport.ObjectNameString.EndsWith("_M", StringComparison.OrdinalIgnoreCase))
                        {
                            maleAssetCount++;
                            maleLineCount += animSet.Lines.Count;
                        }
                        else
                        {
                            unknownGenderAssetCount++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"FAILED TO SCAN {Path.GetFileName(packageFile)}: {ex.Message}");
                }
            }

            return new FaceFxFolderScanSummary(packageFiles, filesWithMatches, femaleAssetCount, femaleLineCount,
                maleAssetCount, maleLineCount, unknownGenderAssetCount, failures);
        }

        private static List<ExportEntry> GetMatchingFaceFxAnimSets(IMEPackage package, string nameFragment)
        {
            return package.Exports
                .Where(exp => exp.ClassName == "FaceFXAnimSet"
                              && exp.ObjectNameString.Contains(nameFragment, StringComparison.OrdinalIgnoreCase))
                .OrderBy(exp => exp.ObjectNameString, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private sealed record DuplicateIndexReindexTarget(
            string ParentPath,
            string ObjectName,
            string ClassName);

        private sealed record DuplicateIndexReindexSummary(
            int ReindexedGroupCount,
            int ReindexedEntryCount,
            List<string> RemainingDuplicates);

        private sealed record BulkPropertyClassTarget(
            string ClassName,
            List<ExportEntry> Exports)
        {
            public string DisplayName => $"{ClassName} ({Exports.Count} export{(Exports.Count == 1 ? string.Empty : "s")})";
        }

        private enum BulkPropertyValueEditResult
        {
            Applied,
            Cancelled,
            Unsupported
        }

        public static async void GenerateFaceFxForAnimSetsMatchingName(PackageEditorWindow pew)
        {
            if (pew == null)
            {
                return;
            }

            string nameFragment = PromptDialog.Prompt(pew,
                "Enter part of the FaceFXAnimSet name to generate. Matching is case-insensitive and includes both _F and _M versions.",
                "Generate matching FaceFXAnimSets");
            if (string.IsNullOrWhiteSpace(nameFragment))
            {
                return;
            }
            nameFragment = nameFragment.Trim();

            var folderDialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                EnsurePathExists = true,
                Title = "Select folder containing PCC files"
            };
            if (DirectoryMemory.ShowDialog(folderDialog, pew) != CommonFileDialogResult.Ok)
            {
                return;
            }

            try
            {
                pew.BusyText = "Scanning PCC files for matching FaceFXAnimSets";
                pew.IsBusy = true;
                FaceFxFolderScanSummary scan = await Task.Run(() =>
                    ScanFolderForMatchingFaceFxAnimSets(folderDialog.FileName, nameFragment));
                pew.IsBusy = false;

                if (scan.PackageFiles.Count == 0)
                {
                    MessageBox.Show(pew,
                        "No PCC files were found in the selected folder or its subfolders.",
                        "Generate matching FaceFXAnimSets",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                if (scan.FemaleAssetCount == 0 && scan.MaleAssetCount == 0)
                {
                    string noMatchesMessage = $"No _F or _M FaceFXAnimSet exports contain '{nameFragment}' in their name across {scan.PackageFiles.Count} PCC file(s).";
                    if (scan.UnknownGenderAssetCount > 0)
                    {
                        noMatchesMessage += $"\n\nFound {scan.UnknownGenderAssetCount} matching asset(s) without an _F or _M suffix.";
                    }
                    if (scan.Failures.Count > 0)
                    {
                        noMatchesMessage += $"\n\n{scan.Failures.Count} file(s) could not be scanned.";
                    }

                    MessageBox.Show(pew, noMatchesMessage, "Generate matching FaceFXAnimSets", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                FaceFxGenerationSettings femaleSettings = null;
                FaceFxGenerationSettings maleSettings = null;
                if (scan.FemaleAssetCount > 0
                    && !TryGetFaceFxGenerationSettings(pew, scan.FemaleLineCount, isFemale: true, out femaleSettings))
                {
                    return;
                }

                if (scan.MaleAssetCount > 0
                    && !TryGetFaceFxGenerationSettings(pew, scan.MaleLineCount, isFemale: false, out maleSettings))
                {
                    return;
                }

                string confirmation = $"Generate FaceFX in {scan.FilesWithMatches} of {scan.PackageFiles.Count} PCC file(s)?\n\n" +
                                      $"Female: {scan.FemaleAssetCount} asset(s), {scan.FemaleLineCount} line(s)\n" +
                                      $"Male: {scan.MaleAssetCount} asset(s), {scan.MaleLineCount} line(s)";
                if (scan.UnknownGenderAssetCount > 0)
                {
                    confirmation += $"\nSkipped without _F/_M suffix: {scan.UnknownGenderAssetCount} asset(s)";
                }
                confirmation += "\n\nFiles will be modified in place. Make sure you have a backup and that these files are not open elsewhere in Legendary Explorer.";
                if (MessageBox.Show(pew,
                        confirmation,
                        "Generate matching FaceFXAnimSets",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    return;
                }

                pew.BusyText = "Generating matching FaceFXAnimSets in PCC files";
                pew.IsBusy = true;
                FaceFxGenerationSummary summary = await Task.Run(() =>
                    GenerateFaceFxForFolder(scan, nameFragment, femaleSettings, maleSettings));
                pew.IsBusy = false;

                string message = $"FaceFX folder generation complete.\n\nPCC files scanned: {summary.FilesScanned}\n" +
                                 $"Files with matches: {summary.FilesWithMatches}\nFiles modified: {summary.ModifiedFileCount}\n" +
                                 $"Assets generated: {summary.AssetCount}\nLines generated: {summary.LineCount}\n" +
                                 $"Skipped lines/assets: {summary.SkippedLineCount}\nErrors: {summary.ErrorCount}";
                if (summary.Messages.Count > 0)
                {
                    message += "\n\n" + string.Join("\n", summary.Messages.Take(15));
                    if (summary.Messages.Count > 15)
                    {
                        message += $"\n...and {summary.Messages.Count - 15} more message(s).";
                    }
                }

                MessageBox.Show(pew,
                    message,
                    "Generate matching FaceFXAnimSets",
                    MessageBoxButton.OK,
                    summary.ErrorCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(pew, ex.FlattenException(), "Generate matching FaceFXAnimSets", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                pew.IsBusy = false;
            }
        }

        private static bool TryGetFaceFxGenerationSettings(PackageEditorWindow owner, int lineCount, bool isFemale,
            out FaceFxGenerationSettings settings)
        {
            string genderName = isFemale ? "Female" : "Male";
            var dialog = new BulkFaceFXGenerationDialog(
                lineCount,
                owner,
                isFemale ? FaceFXSpecies.HumanFemale : FaceFXSpecies.HumanMale)
            {
                Title = $"Bulk FaceFX Generation - All {genderName} Assets"
            };
            if (dialog.ShowDialog() != true || !dialog.Confirmed)
            {
                settings = null;
                return false;
            }

            settings = new FaceFxGenerationSettings(dialog.SelectedSpeciesEnum, dialog.LipSyncIntensity,
                dialog.GenerateBlinkAnimation, dialog.BlinkFrequency);
            return true;
        }

        private static FaceFxGenerationSummary GenerateFaceFxForFolder(FaceFxFolderScanSummary scan, string nameFragment,
            FaceFxGenerationSettings femaleSettings, FaceFxGenerationSettings maleSettings)
        {
            int filesWithMatches = 0;
            int modifiedFileCount = 0;
            int assetCount = 0;
            int lineCount = 0;
            int skippedCount = 0;
            int errorCount = scan.Failures.Count;
            var messages = new List<string>(scan.Failures);

            foreach (string packageFile in scan.PackageFiles)
            {
                try
                {
                    using var package = MEPackageHandler.OpenMEPackage(packageFile, forceLoadFromDisk: true);
                    List<ExportEntry> animSetExports = GetMatchingFaceFxAnimSets(package, nameFragment);
                    if (animSetExports.Count == 0)
                    {
                        continue;
                    }
                    filesWithMatches++;

                    foreach (ExportEntry animSetExport in animSetExports)
                    {
                        bool isFemaleAsset;
                        FaceFxGenerationSettings settings;
                        if (animSetExport.ObjectNameString.EndsWith("_F", StringComparison.OrdinalIgnoreCase))
                        {
                            isFemaleAsset = true;
                            settings = femaleSettings;
                        }
                        else if (animSetExport.ObjectNameString.EndsWith("_M", StringComparison.OrdinalIgnoreCase))
                        {
                            isFemaleAsset = false;
                            settings = maleSettings;
                        }
                        else
                        {
                            skippedCount++;
                            messages.Add($"Skipped {Path.GetFileName(packageFile)}:{animSetExport.ObjectNameString}: name does not end in _F or _M.");
                            continue;
                        }

                        if (settings == null)
                        {
                            skippedCount++;
                            messages.Add($"Skipped {Path.GetFileName(packageFile)}:{animSetExport.ObjectNameString}: no {(!isFemaleAsset ? "male" : "female")} settings were configured.");
                            continue;
                        }

                        FaceFXAnimSet animSet;
                        try
                        {
                            animSet = animSetExport.GetBinaryData<FaceFXAnimSet>();
                        }
                        catch (Exception ex)
                        {
                            errorCount++;
                            messages.Add($"{Path.GetFileName(packageFile)}:{animSetExport.ObjectNameString}: {ex.Message}");
                            continue;
                        }

                        if (animSet.Lines.Count == 0)
                        {
                            skippedCount++;
                            messages.Add($"Skipped {Path.GetFileName(packageFile)}:{animSetExport.ObjectNameString}: no FaceFX lines.");
                            continue;
                        }

                        var faceFxBinary = new FaceFxAnimSetBinary(animSet);
                        var options = new FaceFXGenerationOptions
                        {
                            CharacterType = isFemaleAsset ? CharacterType.HumanFemale : CharacterType.HumanMale,
                            Species = settings.Species,
                            GenerateJawAnimation = true,
                            GenerateBlinkAnimation = settings.GenerateBlinkAnimation,
                            GenerateEyebrowAnimation = true,
                            GenerateHeadMovement = false,
                            LipSyncIntensity = settings.LipSyncIntensity,
                            BlinkFrequency = settings.BlinkFrequency,
                            UseAudioAmplitude = true,
                            FxaData = null,
                            UseTextFallback = true
                        };
                        int generatedForAsset = 0;
                        foreach (FaceFXLine line in animSet.Lines)
                        {
                            if (!TryGetFaceFxLineTlkId(line, out int tlkId))
                            {
                                skippedCount++;
                                continue;
                            }

                            string tlkText = TLKManagerWPF.GlobalFindStrRefbyID(tlkId, package);
                            if (string.IsNullOrWhiteSpace(tlkText))
                            {
                                skippedCount++;
                                continue;
                            }

                            try
                            {
                                ExportEntry audioExport = FindFaceFxVoiceStream(animSetExport, tlkId, isMale: !isFemaleAsset);
                                var generator = new FaceFXGenerator(faceFxBinary, line, tlkText, audioExport, options);
                                if (generator.Generate())
                                {
                                    generatedForAsset++;
                                    lineCount++;
                                }
                                else
                                {
                                    errorCount++;
                                    messages.Add($"{Path.GetFileName(packageFile)}:{animSetExport.ObjectNameString}.{line.NameAsString}: {generator.LastError ?? "Unknown error"}");
                                }
                            }
                            catch (Exception ex)
                            {
                                errorCount++;
                                messages.Add($"{Path.GetFileName(packageFile)}:{animSetExport.ObjectNameString}.{line.NameAsString}: {ex.Message}");
                            }
                        }

                        animSetExport.WriteBinary(animSet);
                        assetCount++;
                        messages.Add($"{Path.GetFileName(packageFile)}:{animSetExport.ObjectNameString}: generated {generatedForAsset} of {animSet.Lines.Count} line(s).");
                    }

                    if (package.IsModified)
                    {
                        package.Save();
                        modifiedFileCount++;
                    }
                }
                catch (Exception ex)
                {
                    errorCount++;
                    messages.Add($"FAILED {Path.GetFileName(packageFile)}: {ex.Message}");
                }
            }

            return new FaceFxGenerationSummary(scan.PackageFiles.Count, filesWithMatches, modifiedFileCount,
                assetCount, lineCount, skippedCount, errorCount, messages);
        }

        private static bool TryGetFaceFxLineTlkId(FaceFXLine line, out int tlkId)
        {
            foreach (string value in new[] { line.ID, line.NameAsString })
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                string idText = value;
                int voPosition = idText.IndexOf("VO_", StringComparison.OrdinalIgnoreCase);
                if (voPosition >= 0)
                {
                    idText = idText[(voPosition + 3)..].TrimEnd('M', 'm', 'F', 'f').TrimEnd('_');
                }

                if (int.TryParse(idText, out tlkId))
                {
                    return true;
                }
            }

            tlkId = 0;
            return false;
        }

        private static ExportEntry FindFaceFxVoiceStream(ExportEntry animSetExport, int tlkId, bool isMale)
        {
            string gender = isMale ? "m" : "f";
            string eventName = $"VO_{tlkId:D6}_{gender}";
            string paddedId = $"{tlkId:D8}";
            string genderedPaddedId = $"{paddedId}_{gender}";
            string bracketedId = $"_{tlkId}_";
            string genderedBracketedId = $"{bracketedId}{gender}";
            ExportEntry eventExport = animSetExport.FileRef.Exports.FirstOrDefault(exp =>
                exp.ClassName == "WwiseEvent" && exp.ObjectName.Name.Contains(eventName, StringComparison.OrdinalIgnoreCase));
            if (eventExport != null)
            {
                WwiseEvent wwiseEvent = ObjectBinary.From<WwiseEvent>(eventExport);
                if (wwiseEvent.Links != null)
                {
                    foreach (var link in wwiseEvent.Links)
                    {
                        List<ExportEntry> streams = link.WwiseStreams
                            .Where(animSetExport.FileRef.IsUExport)
                            .Select(animSetExport.FileRef.GetUExport)
                            .ToList();
                        ExportEntry match = FindFaceFxVoiceStreamByName(streams, genderedPaddedId, genderedBracketedId, paddedId, bracketedId);
                        if (match != null)
                        {
                            return match;
                        }
                    }
                }
            }

            return FindFaceFxVoiceStreamByName(
                animSetExport.FileRef.Exports.Where(exp => exp.ClassName == "WwiseStream"),
                genderedPaddedId,
                genderedBracketedId,
                paddedId,
                bracketedId);
        }

        private static ExportEntry FindFaceFxVoiceStreamByName(IEnumerable<ExportEntry> streams, params string[] searchNames)
        {
            List<ExportEntry> streamList = streams.ToList();
            foreach (string searchName in searchNames)
            {
                ExportEntry match = streamList.FirstOrDefault(exp =>
                    exp.ObjectName.Name.Contains(searchName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        public static void DeleteSectionOfLineForAllFaceFxAssets(PackageEditorWindow pew)
        {
            if (pew?.Pcc == null)
            {
                return;
            }

            List<ExportEntry> faceFxAssets = pew.Pcc.Exports.Where(exp => exp.ClassName == "FaceFXAsset").ToList();
            if (faceFxAssets.Count == 0)
            {
                MessageBox.Show(pew, "No FaceFXAsset exports were found in the current package.", "Delete FaceFX line section", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            List<string> lineNames = GetFaceFxLineNames(faceFxAssets);
            if (lineNames.Count == 0)
            {
                MessageBox.Show(pew, "No FaceFX lines were found in the current package.", "Delete FaceFX line section", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string lineName = InputComboBoxDialog.GetValue(pew,
                "Select the FaceFX line name to edit in all FaceFXAssets.",
                "Delete FaceFX line section",
                lineNames,
                lineNames.FirstOrDefault());
            if (string.IsNullOrWhiteSpace(lineName))
            {
                return;
            }

            var timeRange = GetFaceFxTimeRange(pew, lineName);
            if (timeRange.span < 0)
            {
                return;
            }

            if (MessageBox.Show(pew,
                    $"This will delete the section from {timeRange.start} to {timeRange.end} on every FaceFXAsset line named '{lineName}' in the current package.\n\nContinue?",
                    "Delete FaceFX line section",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            pew.BusyText = "Deleting FaceFX line sections";
            pew.IsBusy = true;

            Task.Run(() => DeleteFaceFxLineSection(faceFxAssets, lineName, timeRange.start, timeRange.end, timeRange.span))
                .ContinueWithOnUIThread(task =>
                {
                    pew.IsBusy = false;

                    if (task.Exception != null)
                    {
                        MessageBox.Show(pew, task.Exception.FlattenException(), "Delete FaceFX line section", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    FaceFxLineSectionDeleteSummary summary = task.Result;
                    if (summary.Failures.Count == 0)
                    {
                        MessageBox.Show(pew,
                            $"Deleted the requested section from {summary.ModifiedLineCount} FaceFX line{(summary.ModifiedLineCount == 1 ? string.Empty : "s")} across {summary.ModifiedAssetCount} FaceFXAsset export{(summary.ModifiedAssetCount == 1 ? string.Empty : "s")}.",
                            "Delete FaceFX line section",
                            MessageBoxButton.OK,
                            summary.ModifiedLineCount > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                        return;
                    }

                    new ListDialog(summary.Failures,
                        $"FaceFX line section delete results ({summary.ModifiedLineCount} lines modified)",
                        "Some FaceFXAssets could not be processed.",
                        pew).Show();
                });
        }

        public static void MoveLargePackageStoredTexturesToTfc(PackageEditorWindow pew)
        {
            if (pew?.Pcc == null)
            {
                return;
            }

            if (pew.Pcc.Game <= MEGame.ME1)
            {
                MessageBox.Show(pew,
                    "This experiment is only supported for ME2/ME3/LE textures.",
                    "Move large package stored textures to TFC",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            List<ExportEntry> candidateTextures = pew.Pcc.Exports
                .Where(exp => exp.IsTexture())
                .Where(IsLargePackageStoredTexture)
                .ToList();
            if (candidateTextures.Count == 0)
            {
                MessageBox.Show(pew,
                    "No package stored textures that are 1024x1024 or larger were found in the current package.",
                    "Move large package stored textures to TFC",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            string preferredTfcName = GetPreferredTextureTfcName(pew.Pcc) ?? "Textures_DLC_MOD_YourModFolderNameHere";
            if (!SelectOrAddNamePromptDialog.Prompt(pew,
                    "Select or add the destination TFC name. A new .tfc file will be created automatically if needed.",
                    "Move large package stored textures to TFC",
                    pew.Pcc,
                    out NameReference targetTfcName,
                    new NameReference(preferredTfcName)))
            {
                return;
            }

            string selectedTfcName = targetTfcName.Name;
            if (string.IsNullOrWhiteSpace(selectedTfcName)
                || !selectedTfcName.StartsWith("Textures_", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(pew,
                    "TFC names must start with 'Textures_'.",
                    "Move large package stored textures to TFC",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (MEDirectories.BasegameTFCs(pew.Pcc.Game).Contains(selectedTfcName, StringComparer.InvariantCultureIgnoreCase)
                || MEDirectories.OfficialDLC(pew.Pcc.Game).Any(x => $"Textures_{x}".Equals(selectedTfcName, StringComparison.InvariantCultureIgnoreCase)))
            {
                MessageBox.Show(pew,
                    "Cannot move textures into a TFC provided by BioWare. Choose a different target TFC from the list.",
                    "Move large package stored textures to TFC",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (MessageBox.Show(pew,
                    $"This will move {candidateTextures.Count} package stored texture{(candidateTextures.Count == 1 ? string.Empty : "s")} that are 1024x1024 or larger into '{selectedTfcName}'.\n\nContinue?",
                    "Move large package stored textures to TFC",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            pew.BusyText = "Moving large package stored textures to TFC";
            pew.IsBusy = true;

            Task.Run(() => MoveTexturesToTfc(candidateTextures, selectedTfcName, pew.Pcc))
                .ContinueWithOnUIThread(task =>
                {
                    pew.IsBusy = false;

                    if (task.Exception != null)
                    {
                        MessageBox.Show(pew,
                            task.Exception.FlattenException(),
                            "Move large package stored textures to TFC",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return;
                    }

                    TextureMoveSummary summary = task.Result;
                    if (summary.Failures.Count == 0)
                    {
                        MessageBox.Show(pew,
                            $"Moved {summary.MovedCount} texture{(summary.MovedCount == 1 ? string.Empty : "s")} to '{selectedTfcName}'.",
                            "Move large package stored textures to TFC",
                            MessageBoxButton.OK,
                            summary.MovedCount > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                        return;
                    }

                    var lines = new List<string>
                    {
                        $"Moved {summary.MovedCount} texture{(summary.MovedCount == 1 ? string.Empty : "s")}.",
                        $"Failed to move {summary.FailedCount} texture{(summary.FailedCount == 1 ? string.Empty : "s")}."
                    };

                    if (summary.Messages.Count > 0)
                    {
                        lines.Add(string.Empty);
                        lines.AddRange(summary.Messages);
                    }

                    if (summary.Failures.Count > 0)
                    {
                        lines.Add(string.Empty);
                        lines.AddRange(summary.Failures);
                    }

                    new ListDialog(lines,
                        "Move large package stored textures to TFC",
                        "The bulk texture move completed with warnings or failures.",
                        pew).Show();
                });
        }

        public static void MoveDlcModTextureReferencesToCurrentDlcTfc(PackageEditorWindow pew)
        {
            if (pew?.Pcc == null)
            {
                return;
            }

            string targetTfcName = GetPreferredTextureTfcName(pew.Pcc);
            if (string.IsNullOrWhiteSpace(targetTfcName))
            {
                MessageBox.Show(pew,
                    "Could not determine the current DLC TFC name for the current package.",
                    "Move DLC MOD textures to current DLC TFC",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            List<ExportEntry> texturesToMove = FindDlcModTextureCacheUsages(pew.Pcc, targetTfcName);
            if (texturesToMove.Count == 0)
            {
                MessageBox.Show(pew,
                    $"No textures referencing another Textures_DLC_MOD TFC were found in the current package.",
                    "Move DLC MOD textures to current DLC TFC",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (MessageBox.Show(pew,
                    $"This will move {texturesToMove.Count} texture{(texturesToMove.Count == 1 ? string.Empty : "s")} referencing another Textures_DLC_MOD TFC into '{targetTfcName}'.\n\nContinue?",
                    "Move DLC MOD textures to current DLC TFC",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            pew.BusyText = "Moving DLC MOD textures to current DLC TFC";
            pew.IsBusy = true;

            Task.Run(() => MoveTexturesToTfc(texturesToMove, targetTfcName, pew.Pcc))
                .ContinueWithOnUIThread(task =>
                {
                    pew.IsBusy = false;

                    if (task.Exception != null)
                    {
                        MessageBox.Show(pew,
                            task.Exception.FlattenException(),
                            "Move DLC MOD textures to current DLC TFC",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return;
                    }

                    TextureMoveSummary summary = task.Result;
                    if (summary.Failures.Count == 0)
                    {
                        MessageBox.Show(pew,
                            $"Moved {summary.MovedCount} texture{(summary.MovedCount == 1 ? string.Empty : "s")} to '{targetTfcName}'.",
                            "Move DLC MOD textures to current DLC TFC",
                            MessageBoxButton.OK,
                            summary.MovedCount > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                        return;
                    }

                    var lines = new List<string>
                    {
                        $"Moved {summary.MovedCount} texture{(summary.MovedCount == 1 ? string.Empty : "s")}",
                        $"Failed to move {summary.FailedCount} texture{(summary.FailedCount == 1 ? string.Empty : "s")}."
                    };

                    if (summary.Messages.Count > 0)
                    {
                        lines.Add(string.Empty);
                        lines.AddRange(summary.Messages);
                    }

                    lines.Add(string.Empty);
                    lines.AddRange(summary.Failures);

                    new ListDialog(lines,
                        "Move DLC MOD textures to current DLC TFC",
                        "The DLC MOD texture move completed with warnings or failures.",
                        pew).Show();
                });
        }

        private static List<ExportEntry> FindDlcModTextureCacheUsages(IMEPackage package, string targetTfcName)
        {
            if (package == null || package.Game <= MEGame.ME1 || string.IsNullOrWhiteSpace(targetTfcName))
            {
                return [];
            }

            var matches = new List<ExportEntry>();
            foreach (ExportEntry export in package.Exports.Where(exp => exp.IsTexture()))
            {
                try
                {
                    if (export.GetProperty<NameProperty>("TextureFileCacheName") is { } tfcProp
                        && tfcProp.Value.Name.StartsWith("Textures_DLC_MOD", StringComparison.OrdinalIgnoreCase)
                        && !tfcProp.Value.Name.Equals(targetTfcName, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(export);
                    }
                }
                catch
                {
                }
            }

            return matches;
        }

        public static void AddSpeakerWithSharedFXAToAllDialogues(PackageEditorWindow pew)
        {
            DialogueEditorExperimentsM.AddSpeakerWithSharedFXAToAllConvos(pew);
        }

        public static void ImportBioConversationsFromLocInt(PackageEditorWindow pew)
        {
            if (pew?.Pcc == null)
            {
                return;
            }

            IMEPackage package = pew.Pcc;
            if (package.Localization != MELocalization.None)
            {
                MessageBox.Show(pew,
                    "Open the main, non-localized package before running this experiment.",
                    "Import BioConversations from LOC_INT",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(package.FilePath))
            {
                MessageBox.Show(pew,
                    "The open package must have a file path so its linked LOC_INT package can be found.",
                    "Import BioConversations from LOC_INT",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string currentFileName = Path.GetFileName(package.FilePath);
            string locIntFileName = currentFileName.SetUnrealLocalization(
                package.Game, MELocalization.INT, includeLOC: true);
            string locIntFilePath = Path.Combine(Path.GetDirectoryName(package.FilePath)!, locIntFileName);

            if (!File.Exists(locIntFilePath))
            {
                MELoadedFiles.TryGetHighestMountedFile(package.Game, locIntFileName, out locIntFilePath);
            }

            if (string.IsNullOrWhiteSpace(locIntFilePath) || !File.Exists(locIntFilePath))
            {
                MessageBox.Show(pew,
                    $"No linked LOC_INT package named '{locIntFileName}' was found.",
                    "Import BioConversations from LOC_INT",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                using IMEPackage locPackage = MEPackageHandler.OpenMEPackage(locIntFilePath);
                List<ExportEntry> conversations = locPackage.Exports
                    .Where(export => export.ClassName == "BioConversation")
                    .ToList();

                int addedConversationCount = 0;
                int addedPackageCount = 0;

                foreach (ExportEntry conversation in conversations)
                {
                    if (package.Imports.Any(import =>
                            import.ClassName == conversation.ClassName
                            && import.InstancedFullPath.Equals(conversation.InstancedFullPath,
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    ImportEntry parentImport = null;
                    if (conversation.Parent is ExportEntry parentExport)
                    {
                        parentImport = package.Imports.FirstOrDefault(import =>
                            import.ClassName == parentExport.ClassName
                            && import.InstancedFullPath.Equals(parentExport.InstancedFullPath,
                                StringComparison.OrdinalIgnoreCase));

                        if (parentImport == null)
                        {
                            parentImport = new ImportEntry(parentExport, 0, package);
                            package.AddImport(parentImport);
                            addedPackageCount++;
                        }
                    }

                    var conversationImport = new ImportEntry(conversation, parentImport?.UIndex ?? 0, package);
                    package.AddImport(conversationImport);
                    addedConversationCount++;
                }

                MessageBox.Show(pew,
                    $"Scanned {conversations.Count} BioConversation export(s) in '{Path.GetFileName(locIntFilePath)}'.\n\n" +
                    $"Added {addedConversationCount} BioConversation import(s) and {addedPackageCount} package import(s).",
                    "Import BioConversations from LOC_INT",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(pew,
                    ex.FlattenException(),
                    "Import BioConversations from LOC_INT",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        public static void GenerateBlankBioConversation(PackageEditorWindow pew)
        {
            if (pew?.Pcc == null)
            {
                return;
            }

            if (pew.Pcc.Game is not (MEGame.ME3 or MEGame.LE3))
            {
                MessageBox.Show(pew,
                    "Blank BioConversation generation is available only for ME3 and LE3 packages.",
                    "Generate blank BioConversation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            (string TopPackageName, string ConversationName)? names = PromptForBlankBioConversationNames(pew);
            if (names == null)
            {
                return;
            }

            string topPackageName = names.Value.TopPackageName;
            string conversationName = names.Value.ConversationName;

            if (pew.Pcc.FindExport(topPackageName, "Package") != null)
            {
                MessageBox.Show(pew,
                    $"A top-level package named '{topPackageName}' already exists. Choose a new name to avoid modifying existing assets.",
                    "Generate blank BioConversation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                (ExportEntry bioConversation, _) = GenerateBlankBioConversationAssets(
                    pew.Pcc, topPackageName, conversationName);

                MessageBox.Show(pew,
                    $"Created '{bioConversation.InstancedFullPath}' with blank player, owner, and non-speaker FaceFX assets.",
                    "Generate blank BioConversation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(pew,
                    ex.FlattenException(),
                    "Generate blank BioConversation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        public static (string TopPackageName, string ConversationName)? PromptForBlankBioConversationNames(
            Window owner, string defaultTopPackageName = "", string defaultConversationName = "Yournamehere")
        {
            var dialog = new BlankBioConversationDialog(owner, defaultTopPackageName, defaultConversationName);
            if (dialog.ShowDialog() != true)
            {
                return null;
            }

            string conversationName = dialog.ConversationName.EndsWith("_dlg", StringComparison.OrdinalIgnoreCase)
                ? dialog.ConversationName[..^4]
                : dialog.ConversationName;
            return (dialog.TopPackageName, conversationName);
        }

        public static (ExportEntry BioConversation, List<ExportEntry> ReferencedExports) GenerateBlankBioConversationAssets(
            IMEPackage package, string topPackageName, string conversationName)
        {
            ExportEntry topPackage = ExportCreator.CreatePackageExport(package, topPackageName);

            ExportEntry bioConversation = ExportCreator.CreateExport(package, $"{conversationName}_dlg",
                "BioConversation", topPackage, indexed: false);
            ExportEntry nonSpeakerFaceFx = CreateBlankFaceFx(package, topPackage, $"FXA_{conversationName}_NonSpkr");
            ExportEntry ownerFemaleFaceFx = CreateBlankFaceFx(package, topPackage, $"FXA_{conversationName}_Owner_F");
            ExportEntry ownerMaleFaceFx = CreateBlankFaceFx(package, topPackage, $"FXA_{conversationName}_Owner_M");
            ExportEntry playerFemaleFaceFx = CreateBlankFaceFx(package, topPackage, $"FXA_{conversationName}_Player_F");
            ExportEntry playerMaleFaceFx = CreateBlankFaceFx(package, topPackage, $"FXA_{conversationName}_Player_M");

            ExportEntry sequence = SequenceObjectCreator.CreateSequenceObject(package, "Sequence");
            sequence.idxLink = topPackage.UIndex;
            sequence.ObjectName = new NameReference("Node_Data_Sequence");
            sequence.WriteProperty(new StrProperty("Node_Data_Sequence", "ObjName"));

            var startingList = new ArrayProperty<IntProperty>("m_StartingList");
            startingList.Add(0);

            PropertyCollection entryProperties = GlobalUnrealObjectInfo.getDefaultStructValue(
                package.Game, "BioDialogEntryNode", true, package);
            entryProperties.AddOrReplaceProp(new EnumProperty("GUI_STYLE_NONE", "EConvGUIStyles", package.Game, "eGUIStyle"));
            entryProperties.AddOrReplaceProp(new IntProperty(-1, "nSpeakerIndex"));
            entryProperties.AddOrReplaceProp(new IntProperty(-2, "nListenerIndex"));
            entryProperties.AddOrReplaceProp(new IntProperty(-1, "nScriptIndex"));
            entryProperties.AddOrReplaceProp(new StringRefProperty(599754, "srText"));
            entryProperties.AddOrReplaceProp(new BoolProperty(true, "bFireConditional"));
            entryProperties.AddOrReplaceProp(new BoolProperty(true, "bSkippable"));
            entryProperties.AddOrReplaceProp(new IntProperty(-1, "nConditionalFunc"));
            entryProperties.AddOrReplaceProp(new IntProperty(-1, "nConditionalParam"));
            entryProperties.AddOrReplaceProp(new IntProperty(-1, "nStateTransition"));
            entryProperties.AddOrReplaceProp(new IntProperty(-1, "nStateTransitionParam"));
            entryProperties.AddOrReplaceProp(new IntProperty(1, "nCameraIntimacy"));

            var entryList = new ArrayProperty<StructProperty>("m_EntryList");
            entryList.Add(new StructProperty("BioDialogEntryNode", entryProperties));

            var maleFaceSets = new ArrayProperty<ObjectProperty>("m_aMaleFaceSets")
            {
                new(playerMaleFaceFx.UIndex),
                new(ownerMaleFaceFx.UIndex)
            };
            var femaleFaceSets = new ArrayProperty<ObjectProperty>("m_aFemaleFaceSets")
            {
                new(playerFemaleFaceFx.UIndex),
                new(ownerFemaleFaceFx.UIndex)
            };

            bioConversation.WriteProperties(new PropertyCollection
            {
                startingList,
                entryList,
                maleFaceSets,
                femaleFaceSets,
                new ObjectProperty(sequence.UIndex, "MatineeSequence"),
                new ObjectProperty(nonSpeakerFaceFx.UIndex, "m_pNonSpeakerFaceFXSet"),
                new IntProperty(GenerateConversationResourceId(package), "m_nResRefID"),
                new ArrayProperty<NameProperty>("m_aSpeakerList")
            });

            ExportCreator.CreatePackageExport(package, "Int",
                ExportCreator.CreatePackageExport(package, "Audio", topPackage));

            return (bioConversation,
            [
                bioConversation,
                nonSpeakerFaceFx,
                ownerFemaleFaceFx,
                ownerMaleFaceFx,
                playerFemaleFaceFx,
                playerMaleFaceFx,
                sequence
            ]);
        }

        private static ExportEntry CreateBlankFaceFx(IMEPackage package, IEntry parent, string name)
        {
            ExportEntry faceFx = ExportCreator.CreateExport(package, name, "FaceFXAnimSet", parent, indexed: false);
            faceFx.WritePropertiesAndBinary(new PropertyCollection(), FaceFXAnimSet.Create(package.Game));
            return faceFx;
        }

        private static int GenerateConversationResourceId(IMEPackage package)
        {
            var usedIds = package.Exports
                .Where(export => export.ClassName == "BioConversation")
                .Select(export => export.GetProperty<IntProperty>("m_nResRefID")?.Value)
                .Where(id => id.HasValue)
                .Select(id => id.Value)
                .ToHashSet();

            int resourceId;
            do
            {
                resourceId = Random.Shared.Next(1_500_000_000, int.MaxValue);
            } while (usedIds.Contains(resourceId));

            return resourceId;
        }

        private static bool IsValidPackageObjectName(string name)
        {
            return !string.IsNullOrWhiteSpace(name)
                   && (char.IsLetter(name[0]) || name[0] == '_')
                   && name.All(character => char.IsLetterOrDigit(character) || character == '_');
        }

        public static void ReindexAllDuplicateIndicesInPackage(PackageEditorWindow pew)
        {
            if (pew?.Pcc == null)
            {
                return;
            }

            List<EntryStringPair> duplicates = EntryChecker.CheckForDuplicateIndices(pew.Pcc);
            if (duplicates.Count == 0)
            {
                MessageBox.Show(pew,
                    "No duplicate indexes were found in the current package.",
                    "Reindex duplicate indexes",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            int duplicateGroupCount = GetDuplicateIndexGroups(pew.Pcc).Count;
            if (MessageBox.Show(pew,
                    $"This will reindex every export or import involved in duplicate indexing in the current package.\n\nDetected {duplicateGroupCount} duplicate group{(duplicateGroupCount == 1 ? string.Empty : "s")} and {duplicates.Count} duplicate entry warning{(duplicates.Count == 1 ? string.Empty : "s")}.\n\nMatching entries under the same parent and class will be renumbered starting at 1. Back up the file first.\n\nContinue?",
                    "Reindex duplicate indexes",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            pew.BusyText = "Reindexing duplicate indexes";
            pew.IsBusy = true;

            Task.Run(() => ReindexAllDuplicateIndices(pew.Pcc))
                .ContinueWithOnUIThread(task =>
                {
                    pew.IsBusy = false;

                    if (task.Exception != null)
                    {
                        MessageBox.Show(pew,
                            task.Exception.FlattenException(),
                            "Reindex duplicate indexes",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return;
                    }

                    DuplicateIndexReindexSummary summary = task.Result;
                    if (summary.RemainingDuplicates.Count == 0)
                    {
                        MessageBox.Show(pew,
                            $"Reindexed {summary.ReindexedEntryCount} entr{(summary.ReindexedEntryCount == 1 ? "y" : "ies")} across {summary.ReindexedGroupCount} duplicate group{(summary.ReindexedGroupCount == 1 ? string.Empty : "s")}.",
                            "Reindex duplicate indexes",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return;
                    }

                    var lines = new List<string>
                    {
                        $"Reindexed {summary.ReindexedEntryCount} entr{(summary.ReindexedEntryCount == 1 ? "y" : "ies")} across {summary.ReindexedGroupCount} duplicate group{(summary.ReindexedGroupCount == 1 ? string.Empty : "s") }.",
                        string.Empty,
                        "Remaining duplicate indexes:",
                    };
                    lines.AddRange(summary.RemainingDuplicates);

                    new ListDialog(lines,
                        "Reindex duplicate indexes",
                        "Some duplicate indexes remain after the bulk reindex.",
                        pew).Show();
                });
        }

        public static void BulkAddPropertiesToClass(PackageEditorWindow pew)
        {
            if (pew?.Pcc == null)
            {
                return;
            }

            List<BulkPropertyClassTarget> classTargets = GetBulkPropertyClassTargets(pew.Pcc);
            if (classTargets.Count == 0)
            {
                MessageBox.Show(pew,
                    "No exports were found that can receive bulk-added properties.",
                    "Bulk add properties to class",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            string defaultClassName = pew.TryGetSelectedExport(out var selectedExport)
                ? selectedExport.ClassName
                : classTargets[0].ClassName;
            string defaultSelection = classTargets.FirstOrDefault(target => string.Equals(target.ClassName, defaultClassName, StringComparison.OrdinalIgnoreCase))?.DisplayName
                                      ?? classTargets[0].DisplayName;
            string selectedClass = InputComboBoxDialog.GetValue(pew,
                "Select the class whose exports should receive added properties.",
                "Bulk add properties to class",
                classTargets.Select(target => target.DisplayName).ToList(),
                defaultSelection);
            if (string.IsNullOrWhiteSpace(selectedClass))
            {
                return;
            }

            BulkPropertyClassTarget targetClass = classTargets.FirstOrDefault(target => string.Equals(target.DisplayName, selectedClass, StringComparison.Ordinal));
            if (targetClass == null)
            {
                return;
            }

            List<PropNameStaticArrayIdxPair> existingProperties = GetCommonRootProperties(targetClass.Exports);
            AddPropertyDialog.ShowAddPropertyDialog(targetClass.Exports[0], existingProperties, pew.Pcc.Game, AddSelectedProperty, pew);

            bool AddSelectedProperty(NameReference propertyName, int staticArrayIndex, PropertyInfo propertyInfo)
            {
                using var packageCache = new PackageCache();

                int addedCount = 0;
                var failures = new List<string>();
                foreach (ExportEntry export in targetClass.Exports)
                {
                    try
                    {
                        PropertyCollection props = export.GetProperties();
                        if (RootPropertyExists(props, propertyName, staticArrayIndex))
                        {
                            continue;
                        }

                        Property newProperty = CreateDefaultRootProperty(export, propertyName, propertyInfo, packageCache);
                        if (newProperty == null)
                        {
                            failures.Add($"FAILED #{export.UIndex} {export.InstancedFullPath}: property '{GetPropertyDisplayName(propertyName, staticArrayIndex, propertyInfo)}' could not be created.");
                            continue;
                        }

                        newProperty.StaticArrayIndex = staticArrayIndex;
                        SetRootProperty(props, newProperty);
                        export.WriteProperties(props);
                        addedCount++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"FAILED #{export.UIndex} {export.InstancedFullPath}: {ex.Message}");
                    }
                }

                if (failures.Count > 0)
                {
                    new ListDialog(failures,
                        $"Bulk add properties to class ({GetPropertyDisplayName(propertyName, staticArrayIndex, propertyInfo)})",
                        "Some exports could not be updated.",
                        pew).Show();
                }

                if (addedCount == 0)
                {
                    return false;
                }

                ApplyBulkPropertyValueEdit(pew, targetClass, propertyName, staticArrayIndex, propertyInfo);
                return true;
            }
        }

        public static void FixBrokenPlayerFaceFxReferencesInFolder(PackageEditorWindow pew)
        {
            if (pew == null)
            {
                return;
            }

            if (MessageBox.Show(pew,
                    "This will scan every package file in a selected folder and its subfolders, then fix BioConversation player FaceFX references that point to non-player FaceFXAnimSets by reconnecting them to local player FaceFXAnimSets under the conversation package.\n\nMake sure you have a backup and that these files are not open elsewhere in Legendary Explorer.\n\nContinue?",
                    "Fix player FaceFX references in folder",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            var dialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                EnsurePathExists = true,
                Title = "Select folder containing package files"
            };
            if (DirectoryMemory.ShowDialog(dialog, pew) != CommonFileDialogResult.Ok)
            {
                return;
            }

            pew.BusyText = "Fixing broken player FaceFX references in folder";
            pew.IsBusy = true;

            Task.Run(() => FixBrokenPlayerFaceFxReferencesInFolder(dialog.FileName))
                .ContinueWithOnUIThread(task =>
                {
                    pew.IsBusy = false;

                    if (task.Exception != null)
                    {
                        MessageBox.Show(pew, task.Exception.FlattenException(), "Fix player FaceFX references in folder", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    PlayerFaceFxFolderRepairSummary summary = task.Result;
                    if (summary.Failures.Count == 0 && summary.Warnings.Count == 0)
                    {
                        MessageBox.Show(pew,
                            $"Scanned {summary.FilesScanned} package file(s), checked {summary.EligibleFiles} Mass Effect package file(s), and fixed {summary.ModifiedReferences} player FaceFX reference(s) across {summary.ModifiedConversations} conversation(s) in {summary.ModifiedFiles} file(s).",
                            "Fix player FaceFX references in folder",
                            MessageBoxButton.OK,
                            summary.ModifiedReferences > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                        return;
                    }

                    var lines = new List<string>
                    {
                        $"Scanned {summary.FilesScanned} package file(s).",
                        $"Checked {summary.EligibleFiles} Mass Effect package file(s).",
                        $"Fixed {summary.ModifiedReferences} player FaceFX reference(s) across {summary.ModifiedConversations} conversation(s) in {summary.ModifiedFiles} file(s)."
                    };

                    if (summary.Warnings.Count > 0)
                    {
                        lines.Add(string.Empty);
                        lines.AddRange(summary.Warnings);
                    }

                    if (summary.Failures.Count > 0)
                    {
                        lines.Add(string.Empty);
                        lines.AddRange(summary.Failures);
                    }

                    new ListDialog(lines,
                        "Fix player FaceFX references in folder",
                        "The batch repair completed with warnings or failures.",
                        pew).Show();
                });
        }

        public static void RestoreMaterialFromChosenAssetDatabase(PackageEditorWindow pew, ExportEntry export, Action onCompleted = null)
        {
            if (pew?.Pcc == null || export == null)
            {
                return;
            }

            bool isMaterial = export.ClassName == "Material";
            bool isMaterialInstance = export.IsA("MaterialInstanceConstant");
            if (!isMaterial && !isMaterialInstance)
            {
                MessageBox.Show(pew, "This action is only available for Material and MaterialInstanceConstant exports.", "Restore material from asset database", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!pew.Pcc.Game.IsMEGame())
            {
                MessageBox.Show(pew, "Material restore is only supported for Mass Effect package files.", "Restore material from asset database", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(Settings.AssetDBPath) || !File.Exists(Settings.AssetDBPath))
            {
                MessageBox.Show(pew, "Asset Database not found. Configure or build the Asset Database first.", "Restore material from asset database", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string gamePath = MEDirectories.GetDefaultGamePath(pew.Pcc.Game);
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            {
                MessageBox.Show(pew, $"No {pew.Pcc.Game} installation was found. Configure the game path first.", "Restore material from asset database", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AssetDatabaseWindow.ShowMaterialPicker(pew, pew.Pcc.Game, isMaterialInstance, selection =>
            {
                if (selection?.Material == null)
                {
                    return;
                }

                bool preserveMicUniformExpressionTextures = false;
                if (isMaterialInstance)
                {
                    var preserveResult = MessageBox.Show(pew,
                        "Preserve UniformExpressionTextures from the original MaterialInstanceConstant binary?",
                        "Restore material from asset database",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);
                    if (preserveResult == MessageBoxResult.Cancel)
                    {
                        return;
                    }

                    preserveMicUniformExpressionTextures = preserveResult == MessageBoxResult.Yes;
                }

                pew.BusyText = "Restoring material from asset database";
                pew.IsBusy = true;

                Task.Run(() => RestoreMaterialFromChosenRecord(pew.Pcc, export, Settings.AssetDBPath, gamePath, selection.Material, preserveMicUniformExpressionTextures))
                    .ContinueWithOnUIThread(task =>
                    {
                        pew.IsBusy = false;

                        if (task.Exception != null)
                        {
                            MessageBox.Show(pew, task.Exception.FlattenException(), "Restore material from asset database", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }

                        RepairSummary summary = task.Result;
                        if (summary.FixedCount > 0)
                        {
                            onCompleted?.Invoke();
                        }

                        if (summary.Failures.Count == 0 && summary.WarningCount == 0)
                        {
                            MessageBox.Show(pew,
                                $"Restored #{export.UIndex} {export.InstancedFullPath} using '{selection.Material.DisplayString}'.",
                                "Restore material from asset database",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                            return;
                        }

                        var lines = new List<string>();
                        if (summary.Failures.Count > 0)
                        {
                            lines.AddRange(summary.Failures);
                        }

                        if (summary.Warnings.Count > 0)
                        {
                            if (lines.Count > 0)
                            {
                                lines.Add(string.Empty);
                            }

                            lines.AddRange(summary.Warnings);
                        }

                        new ListDialog(lines,
                            "Restore material from asset database",
                            "The selected donor was applied, but warnings or failures were reported.",
                            pew).Show();
                    });
            }, export.ObjectName.Name);
        }

        public static void FixBrokenMaterialsUsingAssetDatabase(PackageEditorWindow pew)
        {
            if (pew?.Pcc == null)
            {
                return;
            }
            if (!pew.Pcc.Game.IsMEGame())
            {
                MessageBox.Show(pew, "Broken material repair is only supported for Mass Effect package files.", "Fix broken materials", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(Settings.AssetDBPath) || !File.Exists(Settings.AssetDBPath))
            {
                MessageBox.Show(pew, "Asset Database not found. Configure or build the Asset Database first.", "Fix broken materials", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string gamePath = MEDirectories.GetDefaultGamePath(pew.Pcc.Game);
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            {
                MessageBox.Show(pew, $"No {pew.Pcc.Game} installation was found. Configure the game path first.", "Fix broken materials", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            List<ExportEntry> brokenMaterials = ShaderCacheManipulator.GetBrokenMaterials(pew.Pcc);
            if (brokenMaterials.Count == 0)
            {
                MessageBox.Show(pew, "No broken materials were found in the current package.", "Fix broken materials", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MessageBox.Show(pew,
                    $"This will try to repair {brokenMaterials.Count} broken Material or MaterialInstance export(s) in the current package using the {pew.Pcc.Game} asset database.\n\nContinue?",
                    "Fix broken materials",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            pew.BusyText = "Fixing broken materials";
            pew.IsBusy = true;

            Task.Run(() => RepairBrokenMaterials(pew.Pcc, Settings.AssetDBPath, gamePath, brokenMaterials))
                .ContinueWithOnUIThread(task =>
                {
                    pew.IsBusy = false;

                    if (task.Exception != null)
                    {
                        MessageBox.Show(pew, task.Exception.FlattenException(), "Fix broken materials", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    RepairSummary summary = task.Result;
                    if (summary.Failures.Count == 0 && summary.WarningCount == 0)
                    {
                        MessageBox.Show(pew,
                            $"Repaired {summary.FixedCount} broken material export{(summary.FixedCount == 1 ? string.Empty : "s")}.",
                            "Fix broken materials",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return;
                    }

                    var lines = new List<string>();
                    if (summary.Failures.Count > 0)
                    {
                        lines.AddRange(summary.Failures);
                    }

                    if (summary.Warnings.Count > 0)
                    {
                        if (lines.Count > 0)
                        {
                            lines.Add(string.Empty);
                        }

                        lines.AddRange(summary.Warnings);
                    }

                    new ListDialog(lines,
                        $"Broken material repair results ({summary.FixedCount} fixed)",
                        "The following items could not be fully repaired or reported relink warnings.",
                        pew).Show();
                });
        }

        private static List<string> GetFaceFxLineNames(List<ExportEntry> faceFxAssets)
        {
            var lineNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ExportEntry export in faceFxAssets)
            {
                try
                {
                    foreach (FaceFXLine line in ObjectBinary.From<FaceFXAsset>(export).Lines ?? [])
                    {
                        if (!string.IsNullOrWhiteSpace(line.NameAsString))
                        {
                            lineNames.Add(line.NameAsString);
                        }
                    }
                }
                catch
                {
                    // Ignore exports that fail to parse; bulk operation will report processing failures later.
                }
            }

            return lineNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static (float start, float end, float span) GetFaceFxTimeRange(Window owner, string lineName)
        {
            string startPrompt = PromptDialog.Prompt(owner, $"Enter the start time to delete from '{lineName}':", "Delete FaceFX line section");
            string endPrompt = PromptDialog.Prompt(owner, $"Enter the end time to delete from '{lineName}':", "Delete FaceFX line section");
            if (!(float.TryParse(startPrompt, out float start) && float.TryParse(endPrompt, out float end)))
            {
                MessageBox.Show(owner, "You must enter two valid time values. For example, 3 and a half seconds would be entered as 3.5.", "Delete FaceFX line section", MessageBoxButton.OK, MessageBoxImage.Warning);
                return (0, 0, -1);
            }

            float span = end - start;
            if (span <= 0)
            {
                MessageBox.Show(owner, "The end time must be after the start time!", "Delete FaceFX line section", MessageBoxButton.OK, MessageBoxImage.Warning);
                return (0, 0, -1);
            }

            return (start, end, span);
        }

        private static FaceFxLineSectionDeleteSummary DeleteFaceFxLineSection(IEnumerable<ExportEntry> faceFxAssets, string lineName, float start, float end, float span)
        {
            int modifiedAssetCount = 0;
            int modifiedLineCount = 0;
            var failures = new List<string>();

            foreach (ExportEntry export in faceFxAssets)
            {
                try
                {
                    FaceFXAsset faceFxAsset = ObjectBinary.From<FaceFXAsset>(export);
                    int modifiedLinesInAsset = 0;
                    foreach (FaceFXLine line in faceFxAsset.Lines.Where(line => string.Equals(line.NameAsString, lineName, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (DeleteFaceFxLineSection(line, start, end, span))
                        {
                            modifiedLinesInAsset++;
                        }
                    }

                    if (modifiedLinesInAsset > 0)
                    {
                        export.WriteBinary(faceFxAsset);
                        modifiedAssetCount++;
                        modifiedLineCount += modifiedLinesInAsset;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"FAILED #{export.UIndex} {export.InstancedFullPath}: {ex.Message}");
                }
            }

            return new FaceFxLineSectionDeleteSummary(modifiedAssetCount, modifiedLineCount, failures);
        }

        private static PlayerFaceFxFolderRepairSummary FixBrokenPlayerFaceFxReferencesInFolder(string folderPath)
        {
            List<string> packageFiles = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(path => path.RepresentsPackageFilePath())
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int eligibleFiles = 0;
            int modifiedFiles = 0;
            int modifiedConversations = 0;
            int modifiedReferences = 0;
            var failures = new List<string>();
            var warnings = new List<string>();

            foreach (string packageFile in packageFiles)
            {
                try
                {
                    using var package = MEPackageHandler.OpenMEPackage(packageFile, forceLoadFromDisk: true);
                    if (!package.Game.IsMEGame() || package.Game.IsGame1())
                    {
                        continue;
                    }

                    eligibleFiles++;
                    int fileConversationFixes = 0;
                    int fileReferenceFixes = 0;

                    foreach (ExportEntry bioConversation in package.Exports.Where(exp => exp.ClassName == "BioConversation"))
                    {
                        if (TryFixBrokenPlayerFaceFxReference(bioConversation, out int referenceFixes, out List<string> conversationWarnings))
                        {
                            fileConversationFixes++;
                            fileReferenceFixes += referenceFixes;
                        }

                        foreach (string warning in conversationWarnings)
                        {
                            warnings.Add($"WARNING {Path.GetFileName(packageFile)}: {warning}");
                        }
                    }

                    if (!package.IsModified)
                    {
                        continue;
                    }

                    package.Save();
                    modifiedFiles++;
                    modifiedConversations += fileConversationFixes;
                    modifiedReferences += fileReferenceFixes;
                }
                catch (Exception ex)
                {
                    failures.Add($"FAILED {Path.GetFileName(packageFile)}: {ex.Message}");
                }
            }

            return new PlayerFaceFxFolderRepairSummary(packageFiles.Count, eligibleFiles, modifiedFiles, modifiedConversations, modifiedReferences, failures, warnings);
        }

        private static List<BulkPropertyClassTarget> GetBulkPropertyClassTargets(IMEPackage package)
        {
            return package.Exports
                .Where(export => export.ClassName != "Class")
                .GroupBy(export => export.ClassName, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new BulkPropertyClassTarget(group.Key, group.OrderBy(export => export.UIndex).ToList()))
                .ToList();
        }

        private static DuplicateIndexReindexSummary ReindexAllDuplicateIndices(IMEPackage package)
        {
            int reindexedGroupCount = 0;
            int reindexedEntryCount = 0;

            while (GetNextDuplicateIndexTarget(package) is { } target)
            {
                int changedEntries = ReindexDuplicateIndexTarget(package, target);
                if (changedEntries == 0)
                {
                    break;
                }

                reindexedGroupCount++;
                reindexedEntryCount += changedEntries;
            }

            List<string> remainingDuplicates = EntryChecker.CheckForDuplicateIndices(package)
                .Select(duplicate => duplicate.Message)
                .ToList();
            return new DuplicateIndexReindexSummary(reindexedGroupCount, reindexedEntryCount, remainingDuplicates);
        }

        private static DuplicateIndexReindexTarget GetNextDuplicateIndexTarget(IMEPackage package)
        {
            List<IEntry> nextDuplicateGroup = GetDuplicateIndexGroups(package).FirstOrDefault();
            return nextDuplicateGroup == null
                ? null
                : new DuplicateIndexReindexTarget(nextDuplicateGroup[0].ParentInstancedFullPath, nextDuplicateGroup[0].ObjectName.Name, nextDuplicateGroup[0].ClassName);
        }

        private static List<List<IEntry>> GetDuplicateIndexGroups(IMEPackage package)
        {
            return EnumeratePackageEntries(package)
                .Where(entry => !ShouldIgnoreDuplicateIndexEntry(entry))
                .GroupBy(entry => new { entry.InstancedFullPath, entry.ClassName })
                .Where(group => group.Count() > 1)
                .Select(group => group.OrderBy(entry => entry.UIndex).ToList())
                .OrderBy(group => GetEntryPathDepth(group[0]))
                .ThenBy(group => group[0].InstancedFullPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group[0].ClassName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group[0].UIndex)
                .ToList();
        }

        private static IEnumerable<IEntry> EnumeratePackageEntries(IMEPackage package)
        {
            foreach (ExportEntry export in package.Exports)
            {
                yield return export;
            }

            foreach (ImportEntry import in package.Imports)
            {
                yield return import;
            }
        }

        private static bool ShouldIgnoreDuplicateIndexEntry(IEntry entry)
        {
            return entry.InstancedFullPath.StartsWith(UnrealPackageFile.TrashPackageName, StringComparison.OrdinalIgnoreCase)
                   && entry.ClassName == "Package";
        }

        private static int GetEntryPathDepth(IEntry entry)
        {
            return string.IsNullOrWhiteSpace(entry?.InstancedFullPath)
                ? 0
                : entry.InstancedFullPath.Count(ch => ch == '.');
        }

        private static int ReindexDuplicateIndexTarget(IMEPackage package, DuplicateIndexReindexTarget target)
        {
            List<IEntry> entries = EnumeratePackageEntries(package)
                .Where(entry => !ShouldIgnoreDuplicateIndexEntry(entry)
                                && string.Equals(entry.ParentInstancedFullPath, target.ParentPath, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(entry.ObjectName.Name, target.ObjectName, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(entry.ClassName, target.ClassName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.indexValue)
                .ThenBy(entry => entry.UIndex)
                .ToList();

            int changedEntries = 0;
            for (int index = 1; index <= entries.Count; index++)
            {
                IEntry entry = entries[index - 1];
                if (entry.indexValue == index)
                {
                    continue;
                }

                entry.indexValue = index;
                changedEntries++;
            }

            return changedEntries;
        }

        private static List<PropNameStaticArrayIdxPair> GetCommonRootProperties(List<ExportEntry> exports)
        {
            if (exports.Count == 0)
            {
                return [];
            }

            var commonProperties = exports[0].GetProperties()
                .Where(property => property is not NoneProperty)
                .Select(property => new PropNameStaticArrayIdxPair(property.Name, property.StaticArrayIndex))
                .ToHashSet();

            foreach (ExportEntry export in exports.Skip(1))
            {
                commonProperties.IntersectWith(export.GetProperties()
                    .Where(property => property is not NoneProperty)
                    .Select(property => new PropNameStaticArrayIdxPair(property.Name, property.StaticArrayIndex)));
            }

            return commonProperties.OrderBy(property => property).ToList();
        }

        private static bool RootPropertyExists(PropertyCollection properties, NameReference propertyName, int staticArrayIndex)
        {
            return properties.Any(property => property.Name == propertyName && property.StaticArrayIndex == staticArrayIndex);
        }

        private static Property CreateDefaultRootProperty(ExportEntry export, NameReference propertyName, PropertyInfo propertyInfo, PackageCache packageCache)
        {
            return GlobalUnrealObjectInfo.GetDefaultProperty(export.Game, propertyName, propertyInfo, packageCache, export.FileRef);
        }

        private static void SetRootProperty(PropertyCollection properties, Property property)
        {
            if (properties.TryReplaceProp(property))
            {
                return;
            }

            int insertIndex = properties.Count > 0 && properties[^1] is NoneProperty
                ? properties.Count - 1
                : properties.Count;
            properties.Insert(insertIndex, property);
        }

        private static void ApplyBulkPropertyValueEdit(Window owner, BulkPropertyClassTarget targetClass, NameReference propertyName, int staticArrayIndex, PropertyInfo propertyInfo)
        {
            Property representativeProperty = targetClass.Exports
                .Select(export => export.GetProperties().GetProp<Property>(propertyName, staticArrayIndex))
                .FirstOrDefault(property => property != null);
            if (representativeProperty == null)
            {
                return;
            }

            BulkPropertyValueEditResult result = TryConfigureBulkPropertyValue(owner, targetClass, representativeProperty, propertyInfo, out Property updatedProperty);
            if (result != BulkPropertyValueEditResult.Applied || updatedProperty == null)
            {
                return;
            }

            var failures = new List<string>();
            foreach (ExportEntry export in targetClass.Exports)
            {
                try
                {
                    PropertyCollection props = export.GetProperties();
                    Property propertyClone = updatedProperty.DeepClone();
                    propertyClone.StaticArrayIndex = staticArrayIndex;
                    SetRootProperty(props, propertyClone);
                    export.WriteProperties(props);
                }
                catch (Exception ex)
                {
                    failures.Add($"FAILED #{export.UIndex} {export.InstancedFullPath}: {ex.Message}");
                }
            }

            if (failures.Count > 0)
            {
                new ListDialog(failures,
                    $"Bulk edit property value ({GetPropertyDisplayName(propertyName, staticArrayIndex, propertyInfo)})",
                    "Some exports could not be updated.",
                    owner).Show();
            }
        }

        private static BulkPropertyValueEditResult TryConfigureBulkPropertyValue(Window owner, BulkPropertyClassTarget targetClass, Property property, PropertyInfo propertyInfo, out Property updatedProperty)
        {
            updatedProperty = null;

            string propertyDisplayName = GetPropertyDisplayName(property.Name, property.StaticArrayIndex, propertyInfo);
            string title = "Bulk edit added property";
            string promptPrefix = $"Set '{propertyDisplayName}' on all {targetClass.Exports.Count} '{targetClass.ClassName}' export{(targetClass.Exports.Count == 1 ? string.Empty : "s")}.";

            switch (property)
            {
                case IntProperty intProperty:
                {
                    string response = PromptDialog.Prompt(owner,
                        $"{promptPrefix}\n\nEnter an integer value:",
                        title,
                        intProperty.Value.ToString(CultureInfo.InvariantCulture),
                        validator: value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                            ? (true, null)
                            : (false, "Enter a valid integer."));
                    if (response == null)
                    {
                        return BulkPropertyValueEditResult.Cancelled;
                    }

                    IntProperty newProperty = intProperty.DeepClone();
                    newProperty.Value = int.Parse(response, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    updatedProperty = newProperty;
                    return BulkPropertyValueEditResult.Applied;
                }
                case FloatProperty floatProperty:
                {
                    string response = PromptDialog.Prompt(owner,
                        $"{promptPrefix}\n\nEnter a floating-point value:",
                        title,
                        floatProperty.Value.ToString(CultureInfo.InvariantCulture),
                        validator: value => float.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _)
                            ? (true, null)
                            : (false, "Enter a valid floating-point value."));
                    if (response == null)
                    {
                        return BulkPropertyValueEditResult.Cancelled;
                    }

                    FloatProperty newProperty = floatProperty.DeepClone();
                    newProperty.Value = float.Parse(response, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
                    updatedProperty = newProperty;
                    return BulkPropertyValueEditResult.Applied;
                }
                case BoolProperty boolProperty:
                {
                    MessageBoxResult response = MessageBox.Show(owner,
                        $"{promptPrefix}\n\nChoose Yes to set it to True, No to set it to False, or Cancel to keep the current/default values.",
                        title,
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);
                    if (response == MessageBoxResult.Cancel)
                    {
                        return BulkPropertyValueEditResult.Cancelled;
                    }

                    BoolProperty newProperty = boolProperty.DeepClone();
                    newProperty.Value = response == MessageBoxResult.Yes;
                    updatedProperty = newProperty;
                    return BulkPropertyValueEditResult.Applied;
                }
                case StrProperty strProperty:
                {
                    string response = PromptDialog.Prompt(owner,
                        $"{promptPrefix}\n\nEnter a string value:",
                        title,
                        strProperty.Value ?? string.Empty);
                    if (response == null)
                    {
                        return BulkPropertyValueEditResult.Cancelled;
                    }

                    StrProperty newProperty = strProperty.DeepClone();
                    newProperty.Value = response;
                    updatedProperty = newProperty;
                    return BulkPropertyValueEditResult.Applied;
                }
                case NameProperty nameProperty:
                {
                    string response = PromptDialog.Prompt(owner,
                        $"{promptPrefix}\n\nEnter a name value:",
                        title,
                        nameProperty.Value.Instanced);
                    if (response == null)
                    {
                        return BulkPropertyValueEditResult.Cancelled;
                    }

                    NameProperty newProperty = nameProperty.DeepClone();
                    newProperty.Value = new NameReference(string.IsNullOrWhiteSpace(response) ? "None" : response);
                    updatedProperty = newProperty;
                    return BulkPropertyValueEditResult.Applied;
                }
                case StringRefProperty stringRefProperty:
                {
                    string response = PromptDialog.Prompt(owner,
                        $"{promptPrefix}\n\nEnter a TLK string reference:",
                        title,
                        stringRefProperty.Value.ToString(CultureInfo.InvariantCulture),
                        validator: value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                            ? (true, null)
                            : (false, "Enter a valid integer string reference."));
                    if (response == null)
                    {
                        return BulkPropertyValueEditResult.Cancelled;
                    }

                    StringRefProperty newProperty = stringRefProperty.DeepClone();
                    newProperty.Value = int.Parse(response, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    updatedProperty = newProperty;
                    return BulkPropertyValueEditResult.Applied;
                }
                case ByteProperty byteProperty:
                {
                    string response = PromptDialog.Prompt(owner,
                        $"{promptPrefix}\n\nEnter a byte value:",
                        title,
                        byteProperty.Value.ToString(CultureInfo.InvariantCulture),
                        validator: value => byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                            ? (true, null)
                            : (false, "Enter a value from 0 to 255."));
                    if (response == null)
                    {
                        return BulkPropertyValueEditResult.Cancelled;
                    }

                    ByteProperty newProperty = byteProperty.DeepClone();
                    newProperty.Value = byte.Parse(response, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    updatedProperty = newProperty;
                    return BulkPropertyValueEditResult.Applied;
                }
                case BioMask4Property bioMaskProperty:
                {
                    string response = PromptDialog.Prompt(owner,
                        $"{promptPrefix}\n\nEnter a byte value:",
                        title,
                        bioMaskProperty.Value.ToString(CultureInfo.InvariantCulture),
                        validator: value => byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                            ? (true, null)
                            : (false, "Enter a value from 0 to 255."));
                    if (response == null)
                    {
                        return BulkPropertyValueEditResult.Cancelled;
                    }

                    BioMask4Property newProperty = bioMaskProperty.DeepClone();
                    newProperty.Value = byte.Parse(response, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    updatedProperty = newProperty;
                    return BulkPropertyValueEditResult.Applied;
                }
                case EnumProperty enumProperty:
                {
                    List<NameReference> enumValues = GlobalUnrealObjectInfo.GetEnumValues(targetClass.Exports[0].Game, enumProperty.EnumType, includeNone: true);
                    if (enumValues == null || enumValues.Count == 0)
                    {
                        MessageBox.Show(owner,
                            $"'{propertyDisplayName}' was added, but bulk value editing could not load values for enum '{enumProperty.EnumType.Instanced}'. The default value was kept.",
                            title,
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return BulkPropertyValueEditResult.Unsupported;
                    }

                    string response = InputComboBoxDialog.GetValue(owner,
                        $"{promptPrefix}\n\nChoose an enum value:",
                        title,
                        enumValues.Select(value => value.Instanced).ToList(),
                        enumProperty.Value.Instanced);
                    if (string.IsNullOrWhiteSpace(response))
                    {
                        return BulkPropertyValueEditResult.Cancelled;
                    }

                    EnumProperty newProperty = enumProperty.DeepClone();
                    newProperty.Value = new NameReference(response);
                    updatedProperty = newProperty;
                    return BulkPropertyValueEditResult.Applied;
                }
                case ObjectProperty objectProperty:
                {
                    string defaultValue = objectProperty.Value == 0
                        ? "None"
                        : targetClass.Exports[0].FileRef.GetEntry(objectProperty.Value)?.InstancedFullPath ?? objectProperty.Value.ToString(CultureInfo.InvariantCulture);
                    string response = PromptDialog.Prompt(owner,
                        $"{promptPrefix}\n\nEnter None, 0, a UIndex, or an exact instanced full path:",
                        title,
                        defaultValue,
                        validator: value => TryParseObjectReferenceInput(targetClass.Exports[0].FileRef, value, out _, out string error)
                            ? (true, null)
                            : (false, error));
                    if (response == null)
                    {
                        return BulkPropertyValueEditResult.Cancelled;
                    }

                    TryParseObjectReferenceInput(targetClass.Exports[0].FileRef, response, out int objectUIndex, out _);
                    ObjectProperty newProperty = objectProperty.DeepClone();
                    newProperty.Value = objectUIndex;
                    updatedProperty = newProperty;
                    return BulkPropertyValueEditResult.Applied;
                }
                default:
                    MessageBox.Show(owner,
                        $"'{propertyDisplayName}' was added, but bulk value editing is not supported for {property.PropType} yet. The default value was kept.",
                        title,
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return BulkPropertyValueEditResult.Unsupported;
            }
        }

        private static bool TryParseObjectReferenceInput(IMEPackage package, string input, out int uIndex, out string error)
        {
            uIndex = 0;
            error = null;

            if (string.IsNullOrWhiteSpace(input) || string.Equals(input, "None", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out uIndex))
            {
                if (uIndex == 0 || package.TryGetEntry(uIndex, out _))
                {
                    return true;
                }

                error = $"No entry with UIndex {uIndex} exists in this package.";
                return false;
            }

            IEntry entry = package.FindEntry(input);
            if (entry != null)
            {
                uIndex = entry.UIndex;
                return true;
            }

            error = "Enter None, 0, a valid UIndex, or an exact instanced full path.";
            return false;
        }

        private static string GetPropertyDisplayName(NameReference propertyName, int staticArrayIndex, PropertyInfo propertyInfo)
        {
            return propertyInfo.IsStaticArray()
                ? $"{propertyName.Instanced}[{staticArrayIndex}]"
                : propertyName.Instanced;
        }

        private static bool IsLargePackageStoredTexture(ExportEntry export)
        {
            try
            {
                var texture = new Texture2D(export);
                var topMip = texture.GetTopMip();
                return topMip != null
                       && topMip.IsPackageStored
                       && topMip.width >= 1024
                       && topMip.height >= 1024;
            }
            catch
            {
                return false;
            }
        }

        private static TextureMoveSummary MoveTexturesToTfc(List<ExportEntry> textures, string targetTfcName, IMEPackage package)
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), "LegendaryExplorer", "MoveLargePackageStoredTexturesToTfc", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            int movedCount = 0;
            int failedCount = 0;
            var messages = new List<string>();
            var failures = new List<string>();
            string targetTfcPath = GetDlcCookedTfcPath(package, targetTfcName);

            try
            {
                if (!string.IsNullOrWhiteSpace(targetTfcPath))
                {
                    EnsureTfcFileExists(targetTfcPath);
                }

                foreach (ExportEntry textureExport in textures)
                {
                    try
                    {
                        var texture = new Texture2D(textureExport);
                        string tempTexturePath = Path.Combine(tempDirectory, $"{textureExport.UIndex:D8}_{SanitizeFileName(textureExport.InstancedFullPath)}.tga");
                        texture.ExportToFile(tempTexturePath);

                        var props = textureExport.GetProperties();
                        var image = TextureImage.LoadFromFile(tempTexturePath, LegendaryExplorerCore.Textures.PixelFormat.ARGB);
                        List<string> replaceMessages = texture.Replace(image, props, tempTexturePath, forcedTFCName: targetTfcName, forcedTFCPath: targetTfcPath);

                        movedCount++;
                        messages.Add($"Moved #{textureExport.UIndex} {textureExport.InstancedFullPath} to '{targetTfcName}'.");
                        messages.AddRange(replaceMessages.Select(message => $"#{textureExport.UIndex} {textureExport.ObjectName.Instanced}: {message}"));
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        failures.Add($"FAILED #{textureExport.UIndex} {textureExport.InstancedFullPath}: {ex.Message}");
                    }
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDirectory))
                    {
                        Directory.Delete(tempDirectory, true);
                    }
                }
                catch
                {
                }
            }

            return new TextureMoveSummary(movedCount, failedCount, messages, failures);
        }

        private static string GetDlcCookedTfcPath(IMEPackage package, string targetTfcName)
        {
            string cookedFolder = GetDlcCookedFolder(package);
            return string.IsNullOrWhiteSpace(cookedFolder) || string.IsNullOrWhiteSpace(targetTfcName)
                ? null
                : Path.Combine(cookedFolder, $"{targetTfcName}.tfc");
        }

        private static string GetDlcCookedFolder(IMEPackage package)
        {
            string filePath = package?.FilePath;
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            string cookedName = MEDirectories.CookedName(package.Game);
            for (DirectoryInfo directory = Directory.GetParent(filePath); directory != null; directory = directory.Parent)
            {
                if (directory.Name.Equals(cookedName, StringComparison.OrdinalIgnoreCase))
                {
                    return directory.FullName;
                }
            }

            return Path.GetDirectoryName(filePath);
        }

        private static void EnsureTfcFileExists(string targetTfcPath)
        {
            if (File.Exists(targetTfcPath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetTfcPath));
            using var fs = new FileStream(targetTfcPath, FileMode.CreateNew, FileAccess.Write);
            fs.WriteGuid(Guid.NewGuid());
        }

        private static string GetPreferredTextureTfcName(IMEPackage package)
        {
            string filePath = package?.FilePath;
            if (string.IsNullOrWhiteSpace(filePath) || package.Game <= MEGame.ME1)
            {
                return null;
            }

            string topLevelFolderName = filePath.DetermineDLCNameFromPath();
            if (string.IsNullOrWhiteSpace(topLevelFolderName))
            {
                for (DirectoryInfo directory = Directory.GetParent(filePath); directory != null; directory = directory.Parent)
                {
                    if (directory.Name.StartsWith("DLC_", StringComparison.OrdinalIgnoreCase))
                    {
                        topLevelFolderName = directory.Name;
                        break;
                    }
                }
            }

            return string.IsNullOrWhiteSpace(topLevelFolderName)
                ? null
                : $"Textures_{topLevelFolderName}";
        }

        private static string SanitizeFileName(string value)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            return new string((value ?? string.Empty).Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
        }

        private static bool TryFixBrokenPlayerFaceFxReference(ExportEntry bioConversation, out int referenceFixes, out List<string> warnings)
        {
            referenceFixes = 0;
            warnings = [];

            if (bioConversation?.FileRef == null)
            {
                return false;
            }

            PropertyCollection bioConvoProps = bioConversation.GetProperties();
            bool modified = false;

            if (TryFixBrokenPlayerFaceFxReference(bioConversation, bioConvoProps, isMale: true, out string maleWarning))
            {
                referenceFixes++;
                modified = true;
            }

            if (!string.IsNullOrWhiteSpace(maleWarning))
            {
                warnings.Add(maleWarning);
            }

            if (TryFixBrokenPlayerFaceFxReference(bioConversation, bioConvoProps, isMale: false, out string femaleWarning))
            {
                referenceFixes++;
                modified = true;
            }

            if (!string.IsNullOrWhiteSpace(femaleWarning))
            {
                warnings.Add(femaleWarning);
            }

            if (modified)
            {
                bioConversation.WriteProperties(bioConvoProps);
            }

            return modified;
        }

        private static bool TryFixBrokenPlayerFaceFxReference(ExportEntry bioConversation, PropertyCollection bioConvoProps, bool isMale, out string warning)
        {
            warning = null;

            string arrayName = isMale ? "m_aMaleFaceSets" : "m_aFemaleFaceSets";
            ArrayProperty<ObjectProperty> faceSets = bioConvoProps.GetProp<ArrayProperty<ObjectProperty>>(arrayName);
            ExportEntry currentFaceFx = faceSets != null && faceSets.Count > 0 && bioConversation.FileRef.IsUExport(faceSets[0].Value)
                ? bioConversation.FileRef.GetUExport(faceSets[0].Value)
                : null;

            if (currentFaceFx != null && IsPlayerFaceFx(currentFaceFx))
            {
                return false;
            }

            ExportEntry replacementFaceFx = FindLocalPlayerFaceFxForConversation(bioConversation, isMale);
            if (replacementFaceFx == null)
            {
                if (currentFaceFx != null)
                {
                    warning = $"#{bioConversation.UIndex} {bioConversation.InstancedFullPath}: player {(isMale ? "male" : "female")} FaceFX points to '{currentFaceFx.InstancedFullPath}', but no local player FaceFXAnimSet was found under '{bioConversation.Parent?.InstancedFullPath ?? "<root>"}'.";
                }

                return false;
            }

            if (currentFaceFx?.UIndex == replacementFaceFx.UIndex)
            {
                return false;
            }

            faceSets ??= new ArrayProperty<ObjectProperty>(arrayName);
            if (bioConvoProps.GetProp<ArrayProperty<ObjectProperty>>(arrayName) == null)
            {
                bioConvoProps.AddOrReplaceProp(faceSets);
            }

            while (faceSets.Count < 1)
            {
                faceSets.Add(new ObjectProperty(0));
            }

            faceSets[0].Value = replacementFaceFx.UIndex;
            return true;
        }

        private static ExportEntry FindLocalPlayerFaceFxForConversation(ExportEntry bioConversation, bool isMale)
        {
            string conversationParentPath = bioConversation.Parent?.InstancedFullPath;
            if (string.IsNullOrWhiteSpace(conversationParentPath))
            {
                return null;
            }

            string genderSuffix = isMale ? "_M" : "_F";
            string topPackageName = GetTopPackageName(bioConversation);
            string[] preferredNames =
            [
                $"{topPackageName}_player{genderSuffix}",
                $"FXA_{topPackageName}_player{genderSuffix}"
            ];

            List<ExportEntry> candidates = bioConversation.FileRef.Exports
                .Where(exp => !exp.IsDefaultObject
                              && exp.ClassName == "FaceFXAnimSet"
                              && IsUnderPackage(exp, conversationParentPath)
                              && exp.ObjectNameString.Contains("player", StringComparison.OrdinalIgnoreCase)
                              && exp.ObjectNameString.EndsWith(genderSuffix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (string preferredName in preferredNames)
            {
                ExportEntry exactMatch = candidates.FirstOrDefault(exp => exp.ObjectNameString.Equals(preferredName, StringComparison.OrdinalIgnoreCase));
                if (exactMatch != null)
                {
                    return exactMatch;
                }
            }

            return candidates
                .OrderByDescending(exp => string.Equals(exp.Parent?.InstancedFullPath, conversationParentPath, StringComparison.OrdinalIgnoreCase))
                .ThenBy(exp => exp.InstancedFullPath.Count(ch => ch == '.'))
                .ThenBy(exp => exp.ObjectNameString, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static bool IsPlayerFaceFx(ExportEntry faceFxExport)
        {
            return faceFxExport != null
                && faceFxExport.ClassName == "FaceFXAnimSet"
                && faceFxExport.ObjectNameString.Contains("player", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUnderPackage(IEntry entry, string packagePath)
        {
            if (entry == null || string.IsNullOrWhiteSpace(packagePath))
            {
                return false;
            }

            string entryPath = entry.InstancedFullPath;
            return entryPath.Equals(packagePath, StringComparison.OrdinalIgnoreCase)
                   || entryPath.StartsWith(packagePath + ".", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetTopPackageName(IEntry entry)
        {
            IEntry current = entry;
            while (current?.Parent != null)
            {
                current = current.Parent;
            }

            return current?.ObjectName.Instanced ?? string.Empty;
        }

        private static bool DeleteFaceFxLineSection(FaceFXLine line, float start, float end, float span)
        {
            bool modified = false;
            var newPoints = new List<FaceFXControlPoint>();
            for (int i = 0, j = 0; i < line.NumKeys.Count; i++)
            {
                int originalKeyCount = line.NumKeys[i];
                int keptPoints = 0;
                for (int k = 0; k < originalKeyCount; k++)
                {
                    FaceFXControlPoint point = line.Points[j + k];
                    if (point.time < start)
                    {
                        newPoints.Add(point);
                        keptPoints++;
                    }
                    else if (point.time > end)
                    {
                        point.time -= span;
                        newPoints.Add(point);
                        keptPoints++;
                        modified = true;
                    }
                    else
                    {
                        modified = true;
                    }
                }

                j += originalKeyCount;
                line.NumKeys[i] = keptPoints;
                if (keptPoints != originalKeyCount)
                {
                    modified = true;
                }
            }

            if (!modified)
            {
                return false;
            }

            line.Points = newPoints;
            return true;
        }

        private static RepairSummary RepairBrokenMaterials(IMEPackage package, string assetDbPath, string gamePath, List<ExportEntry> brokenMaterials)
        {
            var assetDb = new AssetDB();
            AssetDatabaseWindow.LoadDatabase(assetDbPath, package.Game, assetDb, CancellationToken.None).GetAwaiter().GetResult();
            if (assetDb.Materials.Count == 0)
            {
                throw new InvalidOperationException($"The asset database does not contain any material records for {package.Game}.");
            }

            int fixedCount = 0;
            var failures = new List<string>();
            var warnings = new List<string>();
            var filePathCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (ExportEntry brokenExport in brokenMaterials)
            {
                try
                {
                    PreservedMaterialState preservedState = CapturePreservedState(brokenExport);
                    MaterialSearchProfile profile = BuildSearchProfile(brokenExport);
                    var candidates = GetRankedCandidates(assetDb, profile).Take(64).ToList();
                    if (candidates.Count == 0)
                    {
                        failures.Add($"FAILED #{brokenExport.UIndex} {brokenExport.InstancedFullPath}: no suitable asset database candidate was found.");
                        continue;
                    }

                    using var donorPackage = TryOpenBestDonorPackage(assetDb, package.Game, gamePath, candidates, brokenExport, filePathCache, out var donorExport, out var donorDescription);
                    if (donorPackage == null || donorExport == null)
                    {
                        failures.Add($"FAILED #{brokenExport.UIndex} {brokenExport.InstancedFullPath}: unable to open a usable donor material from the asset database.");
                        continue;
                    }

                    var rop = new RelinkerOptionsPackage
                    {
                        Cache = new PackageCache(),
                        ImportExportDependencies = true,
                        PortImportsMemorySafe = true,
                        PortExportsAsImportsWhenPossible = false
                    };
                    List<EntryStringPair> relinkIssues = EntryImporter.ImportAndRelinkEntries(
                        EntryImporter.PortingOption.ReplaceSingularWithRelink,
                        donorExport,
                        package,
                        brokenExport,
                        true,
                        rop,
                        out _);

                    RestorePreservedState(brokenExport, preservedState);

                    if (ShaderCacheManipulator.IsMaterialBroken(brokenExport))
                    {
                        failures.Add($"FAILED #{brokenExport.UIndex} {brokenExport.InstancedFullPath}: donor '{donorDescription}' was applied, but the material still reports as broken.");
                        continue;
                    }

                    fixedCount++;
                    if (relinkIssues.Count > 0)
                    {
                        warnings.Add($"WARNING #{brokenExport.UIndex} {brokenExport.InstancedFullPath}: repaired using '{donorDescription}', but {relinkIssues.Count} relink issue(s) were reported.");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"FAILED #{brokenExport.UIndex} {brokenExport.InstancedFullPath}: {ex.Message}");
                }
            }

            return new RepairSummary(fixedCount, warnings.Count, failures, warnings);
        }

        private static RepairSummary RestoreMaterialFromChosenRecord(IMEPackage package, ExportEntry targetExport, string assetDbPath, string gamePath, MaterialRecord selectedMaterial, bool preserveMicUniformExpressionTextures)
        {
            var assetDb = new AssetDB();
            AssetDatabaseWindow.LoadDatabase(assetDbPath, package.Game, assetDb, CancellationToken.None).GetAwaiter().GetResult();
            if (assetDb.Materials.Count == 0)
            {
                throw new InvalidOperationException($"The asset database does not contain any material records for {package.Game}.");
            }

            PreservedMaterialState preservedState = CapturePreservedState(targetExport);
            var filePathCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var donorPackage = TryOpenBestDonorPackage(assetDb, package.Game, gamePath, [selectedMaterial], targetExport, filePathCache, out var donorExport, out var donorDescription);
            if (donorPackage == null || donorExport == null)
            {
                return new RepairSummary(0, 0,
                    [$"FAILED #{targetExport.UIndex} {targetExport.InstancedFullPath}: unable to open the selected donor material from the asset database."],
                    []);
            }

            var rop = new RelinkerOptionsPackage
            {
                Cache = new PackageCache(),
                ImportExportDependencies = true,
                PortImportsMemorySafe = true,
                PortExportsAsImportsWhenPossible = false
            };
            List<EntryStringPair> relinkIssues = EntryImporter.ImportAndRelinkEntries(
                EntryImporter.PortingOption.ReplaceSingularWithRelink,
                donorExport,
                package,
                targetExport,
                true,
                rop,
                out _);

            RestorePreservedState(targetExport, preservedState, preserveMicUniformExpressionTextures);

            if (ShaderCacheManipulator.IsMaterialBroken(targetExport))
            {
                return new RepairSummary(0, 0,
                    [$"FAILED #{targetExport.UIndex} {targetExport.InstancedFullPath}: donor '{donorDescription}' was applied, but the material still reports as broken."],
                    []);
            }

            var warnings = new List<string>();
            if (relinkIssues.Count > 0)
            {
                warnings.Add($"WARNING #{targetExport.UIndex} {targetExport.InstancedFullPath}: restored using '{donorDescription}', but {relinkIssues.Count} relink issue(s) were reported.");
            }

            return new RepairSummary(1, warnings.Count, [], warnings);
        }

        private static PreservedMaterialState CapturePreservedState(ExportEntry export)
        {
            string[] propertyNames = export.ClassName == "Material"
                ? MaterialPropertiesToPreserve
                : MaterialInstancePropertiesToPreserve;

            PropertyCollection props = export.GetProperties();
            var preservedProps = new Dictionary<string, Property>(StringComparer.OrdinalIgnoreCase);
            foreach (string propertyName in propertyNames)
            {
                if (props.GetProp<Property>(propertyName) is { } property)
                {
                    preservedProps[propertyName] = property.DeepClone();
                }
            }

            Material originalBinary = export.ClassName == "Material" ? export.GetBinaryData<Material>() : null;
            MaterialInstance originalInstanceBinary = export.IsA("MaterialInstance") ? export.GetBinaryData<MaterialInstance>() : null;
            return new PreservedMaterialState(propertyNames, preservedProps, originalBinary, originalInstanceBinary);
        }

        private static void RestorePreservedState(ExportEntry export, PreservedMaterialState state, bool preserveMicUniformExpressionTextures = false)
        {
            PropertyCollection props = export.GetProperties();
            foreach (string propertyName in state.PropertyNames)
            {
                if (state.Properties.TryGetValue(propertyName, out Property property))
                {
                    props.AddOrReplaceProp(property.DeepClone());
                }
                else
                {
                    props.RemoveNamedProperty(propertyName);
                }
            }

            if (state.OriginalBinary != null)
            {
                Material donorBinary = export.GetBinaryData<Material>();
                RestoreMaterialParameterData(state.OriginalBinary, donorBinary, export.Game);
                export.WritePropertiesAndBinary(props, donorBinary);
            }
            else if (state.OriginalInstanceBinary != null)
            {
                if (preserveMicUniformExpressionTextures)
                {
                    MaterialInstance donorBinary = export.GetBinaryData<MaterialInstance>();
                    RestoreMaterialInstanceUniformExpressionTextures(state.OriginalInstanceBinary, donorBinary, export.Game);
                    export.WritePropertiesAndBinary(props, donorBinary);
                }
                else
                {
                    export.WriteProperties(props);
                }
            }
            else
            {
                export.WriteProperties(props);
            }
        }

        private static void RestoreMaterialInstanceUniformExpressionTextures(MaterialInstance originalBinary, MaterialInstance donorBinary, MEGame game)
        {
            if (originalBinary?.SM3StaticPermutationResource != null && donorBinary?.SM3StaticPermutationResource != null)
            {
                RestoreMaterialInstanceUniformExpressionTextures(originalBinary.SM3StaticPermutationResource, donorBinary.SM3StaticPermutationResource, game);
            }

            if (game != MEGame.UDK && originalBinary?.SM2StaticPermutationResource != null && donorBinary?.SM2StaticPermutationResource != null)
            {
                RestoreMaterialInstanceUniformExpressionTextures(originalBinary.SM2StaticPermutationResource, donorBinary.SM2StaticPermutationResource, game);
            }
        }

        private static void RestoreMaterialInstanceUniformExpressionTextures(MaterialResource source, MaterialResource target, MEGame game)
        {
            target.UniformExpressionTextures = source.UniformExpressionTextures?.ToArray() ?? [];
            if (game < MEGame.ME3)
            {
                target.Uniform2DTextureExpressions = source.Uniform2DTextureExpressions?.ToArray() ?? [];
                target.UniformCubeTextureExpressions = source.UniformCubeTextureExpressions?.ToArray() ?? [];
            }
        }

        private static void RestoreMaterialParameterData(Material originalBinary, Material donorBinary, MEGame game)
        {
            if (originalBinary?.SM3MaterialResource != null && donorBinary?.SM3MaterialResource != null)
            {
                CopyMaterialParameterData(originalBinary.SM3MaterialResource, donorBinary.SM3MaterialResource, game);
            }

            if (game != MEGame.UDK && originalBinary?.SM2MaterialResource != null && donorBinary?.SM2MaterialResource != null)
            {
                CopyMaterialParameterData(originalBinary.SM2MaterialResource, donorBinary.SM2MaterialResource, game);
            }
        }

        private static void CopyMaterialParameterData(MaterialResource source, MaterialResource target, MEGame game)
        {
            target.TextureDependencyLengthMap = source.TextureDependencyLengthMap != null
                ? new UMultiMap<int, int>(source.TextureDependencyLengthMap)
                : [];
            target.MaxTextureDependencyLength = source.MaxTextureDependencyLength;
            target.NumUserTexCoords = source.NumUserTexCoords;
            target.UniformExpressionTextures = source.UniformExpressionTextures?.ToArray() ?? [];
            target.UsingTransforms = source.UsingTransforms;
            target.TextureLookups = source.TextureLookups?.Select(textureLookup => new MaterialResource.TextureLookup
            {
                TexCoordIndex = textureLookup.TexCoordIndex,
                TextureIndex = textureLookup.TextureIndex,
                UScale = textureLookup.UScale,
                VScale = textureLookup.VScale,
                Unk = textureLookup.Unk
            }).ToArray() ?? [];

            if (game <= MEGame.ME2)
            {
                target.UniformPixelVectorExpressions = source.UniformPixelVectorExpressions?.ToArray() ?? [];
                target.UniformPixelScalarExpressions = source.UniformPixelScalarExpressions?.ToArray() ?? [];
                target.Uniform2DTextureExpressions = source.Uniform2DTextureExpressions?.ToArray() ?? [];
                target.UniformCubeTextureExpressions = source.UniformCubeTextureExpressions?.ToArray() ?? [];

                if (game == MEGame.ME1)
                {
                    target.Me1MaterialUniformExpressionsList = source.Me1MaterialUniformExpressionsList?.Select(CloneMe1UniformExpressions).ToArray() ?? [];
                }
            }
        }

        private static ME1MaterialUniformExpressionsElement CloneMe1UniformExpressions(ME1MaterialUniformExpressionsElement source)
        {
            return new ME1MaterialUniformExpressionsElement
            {
                UniformPixelVectorExpressions = source.UniformPixelVectorExpressions?.ToArray() ?? [],
                UniformPixelScalarExpressions = source.UniformPixelScalarExpressions?.ToArray() ?? [],
                Uniform2DTextureExpressions = source.Uniform2DTextureExpressions?.ToArray() ?? [],
                UniformCubeTextureExpressions = source.UniformCubeTextureExpressions?.ToArray() ?? [],
                unk2 = source.unk2,
                unk3 = source.unk3,
                unk4 = source.unk4,
                unk5 = source.unk5
            };
        }

        private static MaterialSearchProfile BuildSearchProfile(ExportEntry export)
        {
            bool isInstance = export.IsA("MaterialInstance");
            var usedOn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scalarParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var vectorParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var textureParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var textureTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            PropertyCollection props = export.GetProperties();

            foreach (BoolProperty boolProperty in props.OfType<BoolProperty>())
            {
                if (boolProperty.Value && boolProperty.Name.Name.StartsWith("bUsedWith", StringComparison.OrdinalIgnoreCase))
                {
                    usedOn.Add(boolProperty.Name.Name[9..]);
                }
            }

            string parentMaterialKey = null;
            if (isInstance && props.GetProp<ObjectProperty>("Parent") is { } parentProperty && export.FileRef.TryGetEntry(parentProperty.Value, out var parentEntry))
            {
                parentMaterialKey = parentEntry.InstancedFullPath.ToLowerInvariant();
            }

            if (isInstance)
            {
                foreach (ScalarParameter scalar in ScalarParameter.GetScalarParameters(export, true) ?? [])
                {
                    AddParameterName(scalarParameters, scalar.ParameterName);
                }

                foreach (VectorParameter vector in VectorParameter.GetVectorParameters(export, true) ?? [])
                {
                    AddParameterName(vectorParameters, vector.ParameterName);
                }

                foreach (TextureParameter texture in TextureParameter.GetTextureParameters(export, true) ?? [])
                {
                    AddParameterName(textureParameters, texture.ParameterName);
                    AddTextureType(textureTypeCounts, texture.ParameterName);
                }
            }
            else if (props.GetProp<ArrayProperty<ObjectProperty>>("Expressions") is { } expressions)
            {
                foreach (ObjectProperty expressionRef in expressions)
                {
                    if (!export.FileRef.TryGetUExport(expressionRef.Value, out var expression))
                    {
                        continue;
                    }

                    string parameterName = expression.GetProperty<NameProperty>("ParameterName")?.Value.Instanced;
                    switch (expression.ClassName)
                    {
                        case "MaterialExpressionScalarParameter":
                            AddParameterName(scalarParameters, parameterName);
                            break;
                        case "MaterialExpressionVectorParameter":
                            AddParameterName(vectorParameters, parameterName);
                            break;
                        default:
                            if (expression.ClassName.Contains("TextureSampleParameter", StringComparison.OrdinalIgnoreCase))
                            {
                                AddParameterName(textureParameters, parameterName);
                                AddTextureType(textureTypeCounts, parameterName);
                            }
                            break;
                    }
                }
            }

            return new MaterialSearchProfile(
                export.ObjectName.Instanced,
                isInstance,
                parentMaterialKey,
                usedOn,
                scalarParameters,
                vectorParameters,
                textureParameters,
                textureTypeCounts);
        }

        private static IEnumerable<MaterialRecord> GetRankedCandidates(AssetDB assetDb, MaterialSearchProfile profile)
        {
            return assetDb.Materials
                .Select(record => new { Record = record, Score = ScoreCandidate(profile, record) })
                .Where(item => item.Score > int.MinValue)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Record.MaterialName, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Record);
        }

        private static int ScoreCandidate(MaterialSearchProfile profile, MaterialRecord record)
        {
            bool candidateIsInstance = IsInstanceRecord(record);
            if (candidateIsInstance != profile.IsInstance)
            {
                return int.MinValue;
            }

            int score = 0;
            score += ScoreNameSimilarity(profile.ObjectName, record.MaterialName);
            score += ScoreSetOverlap(profile.UsedOn, record.UsedOn ?? [], 22, 8, 3);

            if (!string.IsNullOrWhiteSpace(profile.ParentMaterialKey))
            {
                if (string.Equals(record.ParentMaterialKey, profile.ParentMaterialKey, StringComparison.OrdinalIgnoreCase))
                {
                    score += 250;
                }
                else if (!string.IsNullOrWhiteSpace(record.ParentMaterialKey))
                {
                    string sourceParentName = profile.ParentMaterialKey[(profile.ParentMaterialKey.LastIndexOf('.') + 1)..];
                    string candidateParentName = record.ParentMaterialKey[(record.ParentMaterialKey.LastIndexOf('.') + 1)..];
                    score += ScoreNameSimilarity(sourceParentName, candidateParentName) / 2;
                }
            }

            HashSet<string> candidateScalarParameters = GetSettingNames(record, "ScalarParameter");
            HashSet<string> candidateVectorParameters = GetSettingNames(record, "VectorParameter");
            HashSet<string> candidateTextureParameters = GetTextureParameterNames(record);
            score += ScoreSetOverlap(profile.ScalarParameters, candidateScalarParameters, 30, 10, 4);
            score += ScoreSetOverlap(profile.VectorParameters, candidateVectorParameters, 30, 10, 4);
            score += ScoreSetOverlap(profile.TextureParameters, candidateTextureParameters, 34, 12, 5);

            Dictionary<string, int> candidateTextureTypeCounts = GetTextureTypeCounts(record);
            foreach (string textureType in profile.TextureTypeCounts.Keys.Union(candidateTextureTypeCounts.Keys, StringComparer.OrdinalIgnoreCase))
            {
                int sourceCount = profile.TextureTypeCounts.GetValueOrDefault(textureType);
                int candidateCount = candidateTextureTypeCounts.GetValueOrDefault(textureType);
                score -= Math.Abs(sourceCount - candidateCount) * 7;
                if (sourceCount == candidateCount && sourceCount > 0)
                {
                    score += 15;
                }
            }

            return score;
        }

        private static int ScoreNameSimilarity(string left, string right)
        {
            string normalizedLeft = NormalizeName(left);
            string normalizedRight = NormalizeName(right);
            if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
            {
                return 0;
            }

            int score = 0;
            if (normalizedLeft == normalizedRight)
            {
                score += 240;
            }

            int distance = normalizedLeft.LevenshteinDistance(normalizedRight);
            score += Math.Max(0, 180 - (distance * 8));

            HashSet<string> leftTokens = Tokenize(left);
            HashSet<string> rightTokens = Tokenize(right);
            score += leftTokens.Intersect(rightTokens, StringComparer.OrdinalIgnoreCase).Count() * 18;
            return score;
        }

        private static int ScoreSetOverlap(HashSet<string> source, IEnumerable<string> candidateValues, int matchWeight, int exactWeight, int mismatchPenalty)
        {
            HashSet<string> candidate = new(candidateValues.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
            if (source.Count == 0 && candidate.Count == 0)
            {
                return 0;
            }

            int matches = source.Intersect(candidate, StringComparer.OrdinalIgnoreCase).Count();
            int score = matches * matchWeight;
            if (matches == source.Count && matches == candidate.Count)
            {
                score += exactWeight;
            }

            int sourceOnly = source.Except(candidate, StringComparer.OrdinalIgnoreCase).Count();
            int candidateOnly = candidate.Except(source, StringComparer.OrdinalIgnoreCase).Count();
            score -= (sourceOnly + candidateOnly) * mismatchPenalty;
            return score;
        }

        private static IMEPackage TryOpenBestDonorPackage(
            AssetDB assetDb,
            MEGame game,
            string gamePath,
            IEnumerable<MaterialRecord> candidates,
            ExportEntry sourceExport,
            Dictionary<string, string> filePathCache,
            out ExportEntry donorExport,
            out string donorDescription)
        {
            donorExport = null;
            donorDescription = null;

            foreach (MaterialRecord candidate in candidates)
            {
                foreach (MatUsage usage in candidate.Usages.OrderBy(u => u.IsInDLC).ThenBy(u => u.FileKey))
                {
                    if (!TryGetUsageInfo(assetDb, usage.FileKey, out string fileName, out string contentDir))
                    {
                        continue;
                    }

                    if (!IsAllowedAssetDbDonorLocation(game, contentDir))
                    {
                        continue;
                    }

                    string filePath = FindUsageFilePath(game, gamePath, fileName, contentDir, filePathCache);
                    if (string.IsNullOrWhiteSpace(filePath))
                    {
                        continue;
                    }

                    IMEPackage donorPackage = TryOpenUsagePackage(filePath, fileName);
                    if (donorPackage == null)
                    {
                        continue;
                    }

                    if (!donorPackage.TryGetUExport(usage.UIndex, out donorExport))
                    {
                        donorPackage.Dispose();
                        continue;
                    }

                    if (donorExport.IsA("MaterialInstance") != sourceExport.IsA("MaterialInstance")
                        || donorExport.ClassName == sourceExport.ClassName && donorExport.FileRef.FilePath == sourceExport.FileRef.FilePath && donorExport.UIndex == sourceExport.UIndex)
                    {
                        donorPackage.Dispose();
                        continue;
                    }

                    if (ShaderCacheManipulator.IsMaterialBroken(donorExport))
                    {
                        donorPackage.Dispose();
                        continue;
                    }

                    donorDescription = $"{donorExport.InstancedFullPath} in {fileName}";
                    return donorPackage;
                }
            }

            donorExport = null;
            return null;
        }

        private static bool TryGetUsageInfo(AssetDB assetDb, int fileKey, out string fileName, out string contentDir)
        {
            fileName = null;
            contentDir = null;
            if (fileKey < 0 || fileKey >= assetDb.FileList.Count)
            {
                return false;
            }

            FileNameDirKeyPair fileRecord = assetDb.FileList[fileKey];
            fileName = fileRecord.FileName;
            if (fileRecord.DirectoryKey >= 0 && fileRecord.DirectoryKey < assetDb.ContentDir.Count)
            {
                contentDir = assetDb.ContentDir[fileRecord.DirectoryKey];
            }

            return !string.IsNullOrWhiteSpace(fileName);
        }

        private static string FindUsageFilePath(MEGame game, string gamePath, string fileName, string contentDir, Dictionary<string, string> filePathCache)
        {
            string cacheKey = $"{contentDir}|{fileName}";
            if (filePathCache.TryGetValue(cacheKey, out string cachedPath))
            {
                return cachedPath;
            }

            string filePath = Directory.EnumerateFiles(gamePath, fileName, SearchOption.AllDirectories)
                .FirstOrDefault(path => IsAllowedAssetDbDonorLocation(game, path)
                    && (string.IsNullOrWhiteSpace(contentDir) || path.Contains(contentDir, StringComparison.OrdinalIgnoreCase)));

            if (filePath == null && game == MEGame.ME3)
            {
                string sfarPath = Directory.EnumerateFiles(gamePath, "Default.sfar", SearchOption.AllDirectories)
                    .FirstOrDefault(path => IsAllowedAssetDbDonorLocation(game, path)
                        && (string.IsNullOrWhiteSpace(contentDir) || path.Contains(contentDir, StringComparison.OrdinalIgnoreCase)));
                if (sfarPath != null)
                {
                    DLCPackage dlcPackage = new DLCPackage(sfarPath);
                    if (dlcPackage.FindFileEntry(fileName) != -1)
                    {
                        filePath = sfarPath;
                    }
                }
            }

            filePathCache[cacheKey] = filePath;
            return filePath;
        }

        private static bool IsAllowedAssetDbDonorLocation(MEGame game, string pathOrDirectory)
        {
            if (string.IsNullOrWhiteSpace(pathOrDirectory))
            {
                return true;
            }

            string[] pathParts = pathOrDirectory
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (pathParts.Any(part => part.Equals("Mods", StringComparison.OrdinalIgnoreCase)
                                      || part.StartsWith("DLC_MOD", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            string dlcFolderName = pathParts.FirstOrDefault(part => part.StartsWith("DLC_", StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(dlcFolderName) || MEDirectories.OfficialDLC(game).Contains(dlcFolderName);
        }

        private static IMEPackage TryOpenUsagePackage(string filePath, string realFileName)
        {
            if (Path.GetFileName(filePath).Equals("Default.sfar", StringComparison.OrdinalIgnoreCase))
            {
                DLCPackage dlcPackage = new DLCPackage(filePath);
                int fileIndex = dlcPackage.FindFileEntry(realFileName);
                if (fileIndex == -1)
                {
                    return null;
                }

                return MEPackageHandler.OpenMEPackageFromStream(dlcPackage.DecompressEntry(fileIndex), realFileName);
            }

            return MEPackageHandler.OpenMEPackage(filePath, forceLoadFromDisk: true);
        }

        private static bool IsInstanceRecord(MaterialRecord record)
        {
            return record.MatSettings.Any(setting => setting.Name == "IsInstance" && string.Equals(setting.Parm1, "true", StringComparison.OrdinalIgnoreCase));
        }

        private static HashSet<string> GetSettingNames(MaterialRecord record, string settingName)
        {
            return record.MatSettings
                .Where(setting => string.Equals(setting.Name, settingName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(setting.Parm1))
                .Select(setting => setting.Parm1)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static HashSet<string> GetTextureParameterNames(MaterialRecord record)
        {
            return MaterialFilter.GetTextureSettings(record)
                .Select(MaterialFilter.GetTextureParameterName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, int> GetTextureTypeCounts(MaterialRecord record)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (MatSetting setting in MaterialFilter.GetTextureSettings(record))
            {
                string textureType = MaterialFilter.GetTextureParameterType(setting) ?? "Other";
                counts[textureType] = counts.TryGetValue(textureType, out int count) ? count + 1 : 1;
            }

            return counts;
        }

        private static void AddParameterName(HashSet<string> parameters, string parameterName)
        {
            if (!string.IsNullOrWhiteSpace(parameterName) && parameterName != "None")
            {
                parameters.Add(parameterName);
            }
        }

        private static void AddTextureType(Dictionary<string, int> textureTypeCounts, string parameterName)
        {
            if (!MaterialFilter.TryGetTextureParameterType(parameterName, out string textureType))
            {
                textureType = "Other";
            }

            textureTypeCounts[textureType] = textureTypeCounts.TryGetValue(textureType, out int count) ? count + 1 : 1;
        }

        private static string NormalizeName(string value)
        {
            return new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        private static HashSet<string> Tokenize(string value)
        {
            return (value ?? string.Empty)
                .Split(['_', '.', '-', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(token => token.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }
}
