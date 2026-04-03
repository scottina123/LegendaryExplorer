using LegendaryExplorer.Dialogs;
using LegendaryExplorer.DialogueEditor;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Tools.Dialogue_Editor.DialogueEditorExperiments
{
    /// <summary>
    /// Mgamerz Experiments in Dialogue Editor.
    /// </summary>
    static class DialogueEditorExperimentsM
    {
        public static void AddSpeakerWithSharedFXAToAllConvos(WPFBase window)
        {
            if (window.Pcc == null)
            {
                return;
            }

            var conversations = window.Pcc.Exports.Where(e => e.ClassName == "BioConversation").ToList();

            if (conversations.Count == 0)
            {
                MessageBox.Show("This file doesn't contain any converations.");
                return;
            }

            var speakerSelections = PromptForSharedFxaSelectionAndSpeakerTag(window as Window, window.Pcc);
            if (speakerSelections is null)
            {
                return;
            }

            var (newTag, fxaM, fxaF) = speakerSelections.Value;

            foreach (var convo in conversations)
            {
                var bioconvo = convo.GetProperties();
                var speakerList = bioconvo.GetProp<ArrayProperty<NameProperty>>("m_aSpeakerList");
                var fxaMs = bioconvo.GetProp<ArrayProperty<ObjectProperty>>("m_aMaleFaceSets");
                var fxaFs = bioconvo.GetProp<ArrayProperty<ObjectProperty>>("m_aFemaleFaceSets");

                if (speakerList == null)
                {
                    speakerList = new ArrayProperty<NameProperty>("m_aSpeakerList");
                    bioconvo.AddOrReplaceProp(speakerList);
                }

                if (fxaMs == null)
                {
                    fxaMs = new ArrayProperty<ObjectProperty>("m_aMaleFaceSets");
                    bioconvo.AddOrReplaceProp(fxaMs);
                }

                if (fxaFs == null)
                {
                    fxaFs = new ArrayProperty<ObjectProperty>("m_aFemaleFaceSets");
                    bioconvo.AddOrReplaceProp(fxaFs);
                }

                speakerList.Add(new NameProperty(newTag));
                fxaMs.Add(new ObjectProperty(fxaM));
                fxaFs.Add(new ObjectProperty(fxaF));
                convo.WriteProperties(bioconvo);
            }

            MessageBox.Show("Done.");
        }

        public static (string speakerTag, ExportEntry male, ExportEntry female)? PromptForSharedFxaSelectionAndSpeakerTag(Window owner, IMEPackage package, ExportEntry preferredMale = null, ExportEntry preferredFemale = null)
        {
            var faceFxEntries = package.Exports.Where(e => e.ClassName == "FaceFXAnimSet").ToList();
            if (faceFxEntries.Count == 0)
            {
                MessageBox.Show(owner, "This file doesn't contain any FaceFXAnimSet exports.", "No FaceFXAnimSets", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            var faceFxOptions = faceFxEntries
                .Select(e => new EntryStringPair(e, $"{e.UIndex,-9}\t{e.InstancedFullPath}"))
                .ToList();

            var defaultMaleSelection = faceFxOptions.FirstOrDefault(o =>
                (o.Entry as ExportEntry)?.UIndex == preferredMale?.UIndex)
                ?? faceFxOptions.LastOrDefault(o =>
                ((o.Entry as ExportEntry)?.InstancedFullPath?.EndsWith("_m", StringComparison.OrdinalIgnoreCase)).GetValueOrDefault())
                ?? faceFxOptions[faceFxOptions.Count - 1];
            var defaultFemaleSelection = faceFxOptions.FirstOrDefault(o =>
                (o.Entry as ExportEntry)?.UIndex == preferredFemale?.UIndex)
                ?? faceFxOptions.LastOrDefault(o =>
                ((o.Entry as ExportEntry)?.InstancedFullPath?.EndsWith("_f", StringComparison.OrdinalIgnoreCase)).GetValueOrDefault())
                ?? faceFxOptions[faceFxOptions.Count - 1];

            var dialog = new Window
            {
                Title = "Add Speaker With Shared FaceFXAnimSets",
                Width = 700,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow
            };
            dialog.SetResourceReference(Window.BackgroundProperty, SystemColors.WindowBrushKey);
            dialog.SetResourceReference(Window.ForegroundProperty, SystemColors.WindowTextBrushKey);
            CustomWindowChrome.ApplyCustomChrome(dialog);

            var directions = new System.Windows.Controls.TextBlock
            {
                Text = "Enter the speaker tag and select the male and female FaceFXAnimSets to assign to it.",
                Margin = new Thickness(10, 15, 10, 10),
                TextWrapping = TextWrapping.Wrap
            };
            directions.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, SystemColors.WindowTextBrushKey);

            var selectionGrid = new System.Windows.Controls.Grid
            {
                Margin = new Thickness(10, 0, 10, 10)
            };
            selectionGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            selectionGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            selectionGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            selectionGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            selectionGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

            var tagLabel = new System.Windows.Controls.TextBlock
            {
                Text = "Tag:",
                Margin = new Thickness(0, 0, 10, 10),
                VerticalAlignment = VerticalAlignment.Center
            };
            var tagTextBox = new System.Windows.Controls.TextBox
            {
                Margin = new Thickness(0, 0, 0, 10),
                MinWidth = 500
            };

            var maleLabel = new System.Windows.Controls.TextBlock
            {
                Text = "Male:",
                Margin = new Thickness(0, 0, 10, 10),
                VerticalAlignment = VerticalAlignment.Center
            };
            var femaleLabel = new System.Windows.Controls.TextBlock
            {
                Text = "Female:",
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var maleCombo = new System.Windows.Controls.ComboBox
            {
                ItemsSource = faceFxOptions,
                Margin = new Thickness(0, 0, 0, 10),
                MinWidth = 500,
                SelectedItem = defaultMaleSelection
            };
            var femaleCombo = new System.Windows.Controls.ComboBox
            {
                ItemsSource = faceFxOptions,
                MinWidth = 500,
                SelectedItem = defaultFemaleSelection
            };

            dialog.Loaded += (_, _) =>
            {
                tagTextBox.Focus();
                System.Windows.Input.Keyboard.Focus(tagTextBox);
            };

            System.Windows.Controls.Grid.SetRow(tagLabel, 0);
            System.Windows.Controls.Grid.SetColumn(tagLabel, 0);
            System.Windows.Controls.Grid.SetRow(tagTextBox, 0);
            System.Windows.Controls.Grid.SetColumn(tagTextBox, 1);
            System.Windows.Controls.Grid.SetRow(maleLabel, 1);
            System.Windows.Controls.Grid.SetColumn(maleLabel, 0);
            System.Windows.Controls.Grid.SetRow(maleCombo, 1);
            System.Windows.Controls.Grid.SetColumn(maleCombo, 1);
            System.Windows.Controls.Grid.SetRow(femaleLabel, 2);
            System.Windows.Controls.Grid.SetColumn(femaleLabel, 0);
            System.Windows.Controls.Grid.SetRow(femaleCombo, 2);
            System.Windows.Controls.Grid.SetColumn(femaleCombo, 1);

            selectionGrid.Children.Add(tagLabel);
            selectionGrid.Children.Add(tagTextBox);
            selectionGrid.Children.Add(maleLabel);
            selectionGrid.Children.Add(maleCombo);
            selectionGrid.Children.Add(femaleLabel);
            selectionGrid.Children.Add(femaleCombo);

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(10, 0, 10, 10)
            };

            var okButton = new System.Windows.Controls.Button
            {
                Content = "OK",
                Width = 80,
                Margin = new Thickness(5, 0, 0, 0),
                IsDefault = true
            };
            var cancelButton = new System.Windows.Controls.Button
            {
                Content = "Cancel",
                Width = 80,
                Margin = new Thickness(5, 0, 0, 0),
                IsCancel = true
            };

            okButton.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(tagTextBox.Text))
                {
                    MessageBox.Show(dialog, "Enter a speaker tag.", "Missing Speaker Tag", MessageBoxButton.OK, MessageBoxImage.Warning);
                    tagTextBox.Focus();
                    return;
                }

                if (maleCombo.SelectedItem is not EntryStringPair || femaleCombo.SelectedItem is not EntryStringPair)
                {
                    MessageBox.Show(dialog, "Select both FaceFXAnimSets.", "Missing FaceFXAnimSet Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                dialog.DialogResult = true;
            };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            var mainPanel = new System.Windows.Controls.StackPanel();
            mainPanel.Children.Add(directions);
            mainPanel.Children.Add(selectionGrid);
            mainPanel.Children.Add(buttonPanel);
            dialog.Content = mainPanel;

            if (dialog.ShowDialog() != true)
            {
                return null;
            }

            return (tagTextBox.Text.Trim(), ((EntryStringPair)maleCombo.SelectedItem).Entry as ExportEntry, ((EntryStringPair)femaleCombo.SelectedItem).Entry as ExportEntry);
        }

        /// <summary>
        /// Adds a speaker with shared FaceFXAnimSets to all BioConversation exports in the given package.
        /// This is the headless (no-UI) variant used for batch operations.
        /// </summary>
        /// <param name="package">The package to modify.</param>
        /// <param name="speakerTag">The speaker tag name to add.</param>
        /// <param name="maleFxaInstancedFullPath">The InstancedFullPath of the male FaceFXAnimSet in the target package.</param>
        /// <param name="femaleFxaInstancedFullPath">The InstancedFullPath of the female FaceFXAnimSet in the target package.</param>
        /// <returns>The number of conversations modified, or -1 if FXAs were not found.</returns>
        public static int AddSpeakerWithSharedFXAToAllConvos(IMEPackage package, string speakerTag, string maleFxaInstancedFullPath, string femaleFxaInstancedFullPath)
        {
            var fxaM = package.FindExport(maleFxaInstancedFullPath);
            var fxaF = package.FindExport(femaleFxaInstancedFullPath);
            if (fxaM == null || fxaF == null)
            {
                return -1;
            }

            var conversations = package.Exports.Where(e => e.ClassName == "BioConversation").ToList();
            if (conversations.Count == 0)
            {
                return 0;
            }

            foreach (var convo in conversations)
            {
                var bioconvo = convo.GetProperties();
                var speakerList = bioconvo.GetProp<ArrayProperty<NameProperty>>("m_aSpeakerList");
                var fxaMs = bioconvo.GetProp<ArrayProperty<ObjectProperty>>("m_aMaleFaceSets");
                var fxaFs = bioconvo.GetProp<ArrayProperty<ObjectProperty>>("m_aFemaleFaceSets");

                if (speakerList == null)
                {
                    speakerList = new ArrayProperty<NameProperty>("m_aSpeakerList");
                    bioconvo.AddOrReplaceProp(speakerList);
                }

                if (fxaMs == null)
                {
                    fxaMs = new ArrayProperty<ObjectProperty>("m_aMaleFaceSets");
                    bioconvo.AddOrReplaceProp(fxaMs);
                }

                if (fxaFs == null)
                {
                    fxaFs = new ArrayProperty<ObjectProperty>("m_aFemaleFaceSets");
                    bioconvo.AddOrReplaceProp(fxaFs);
                }

                speakerList.Add(new NameProperty(speakerTag));
                fxaMs.Add(new ObjectProperty(fxaM));
                fxaFs.Add(new ObjectProperty(fxaF));
                convo.WriteProperties(bioconvo);
            }

            return conversations.Count;
        }

        public static void ExtractAllAudioFromSpeakerByTag(WPFBase window)
        {
            if (window.Pcc == null)
            {
                return;
            }

            if (window.Pcc.Game is not MEGame.LE2 and not MEGame.LE3)
            {
                MessageBox.Show("This feature only works on LE2 and LE3.");
                return;
            }

            // Prompt for speaker tag
            var speakerTag = PromptDialog.Prompt(window, "Enter the speaker tag to extract audio for.\nEnter 'player' to extract Shepard audio.\nEnter 'owner' to extract audio by conversation owner.", "Extract Speaker Audio");
            if (string.IsNullOrWhiteSpace(speakerTag))
            {
                return;
            }

            // Determine which genders to extract
            bool extractMale = true;
            bool extractFemale = true;

            if (speakerTag.Equals("player", StringComparison.OrdinalIgnoreCase))
            {
                var genderDialog = new System.Windows.Window
                {
                    Title = "Select Genders",
                    Width = 350,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = window as System.Windows.Window,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStyle = WindowStyle.ToolWindow
                };
                genderDialog.SetResourceReference(System.Windows.Window.BackgroundProperty, SystemColors.WindowBrushKey);
                genderDialog.SetResourceReference(System.Windows.Window.ForegroundProperty, SystemColors.WindowTextBrushKey);
                CustomWindowChrome.ApplyCustomChrome(genderDialog);

                string genderChoice = null;

                var textBlock = new System.Windows.Controls.TextBlock
                {
                    Text = "Which audio files would you like to extract?",
                    Margin = new Thickness(10, 15, 10, 10),
                    TextWrapping = TextWrapping.Wrap
                };
                textBlock.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, SystemColors.WindowTextBrushKey);

                var buttonPanel = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 10)
                };

                var bothBtn = new System.Windows.Controls.Button { Content = "Both", Width = 70, Margin = new Thickness(5) };
                var maleBtn = new System.Windows.Controls.Button { Content = "Male", Width = 70, Margin = new Thickness(5) };
                var femaleBtn = new System.Windows.Controls.Button { Content = "Female", Width = 70, Margin = new Thickness(5) };
                var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Width = 70, Margin = new Thickness(5), IsCancel = true };

                bothBtn.Click += (_, _) => { genderChoice = "both"; genderDialog.DialogResult = true; };
                maleBtn.Click += (_, _) => { genderChoice = "male"; genderDialog.DialogResult = true; };
                femaleBtn.Click += (_, _) => { genderChoice = "female"; genderDialog.DialogResult = true; };

                buttonPanel.Children.Add(bothBtn);
                buttonPanel.Children.Add(maleBtn);
                buttonPanel.Children.Add(femaleBtn);
                buttonPanel.Children.Add(cancelBtn);

                var mainPanel = new System.Windows.Controls.StackPanel();
                mainPanel.Children.Add(textBlock);
                mainPanel.Children.Add(buttonPanel);
                genderDialog.Content = mainPanel;

                if (genderDialog.ShowDialog() != true)
                {
                    return; // User cancelled
                }

                extractMale = genderChoice is "both" or "male";
                extractFemale = genderChoice is "both" or "female";
            }

            // Prompt for output folder
            var dlg = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                EnsurePathExists = true,
                Title = "Select Output Folder for Extracted Audio"
            };
            if (dlg.ShowDialog(window as System.Windows.Window) != CommonFileDialogResult.Ok)
            {
                return;
            }
            var outputFolder = dlg.FileName;

            // Prompt for dialogue text inclusion
            var includeText = MessageBox.Show(window as System.Windows.Window,
                "Include dialogue text in filenames?",
                "Filename Options",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;

            var game = window.Pcc.Game;

            // Run extraction in background task with progress updates
            window.IsBusy = true;
            window.BusyText = "Preparing to extract audio...";
            Task.Run(() =>
            {
                var allFiles = MELoadedFiles.GetFilesLoadedInGame(game);
                int totalFiles = allFiles.Count;
                int totalExtracted = 0;
                int conversationsProcessed = 0;
                int filesProcessed = -1;

                var loc = window.Pcc.Localization is MELocalization.None ? MELocalization.INT : window.Pcc.Localization;

                foreach (var file in allFiles)
                {
                    filesProcessed++;

                    if (file.Key.GetUnrealLocalization() != loc)
                    {
                        continue;
                    }

                    try
                    {
                        // Update progress on UI thread
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            window.BusyText = $"Scanning files [{filesProcessed + 1}/{totalFiles}]: {Path.GetFileName(file.Key)}";
                        });

                        using var package = MEPackageHandler.OpenMEPackage(file.Value);

                        var conversations = package.Exports.Where(e => e.ClassName == "BioConversation").ToList();

                        foreach (var convo in conversations)
                        {
                            try
                            {
                                // Parse conversation to get dialogue nodes
                                var convoData = new ConversationExtended(convo);
                                convoData.LoadConversation(TLKManagerWPF.GlobalFindStrRefbyID, true);

                                // Filter nodes by speaker tag
                                var isPlayerTag = speakerTag.Equals("player", StringComparison.OrdinalIgnoreCase);
                                var sourceList = isPlayerTag ? convoData.ReplyList : convoData.EntryList;
                                var speakerNodes = sourceList
                                    .Where(n => n.SpeakerTag?.SpeakerName?.Equals(speakerTag, StringComparison.OrdinalIgnoreCase) == true)
                                    .ToList();

                                if (speakerNodes.Any())
                                {
                                    // Update progress for this conversation
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        window.BusyText = $"Extracting audio from {convo.ObjectName.Name}...\n" +
                                                         $"Files: [{filesProcessed + 1}/{totalFiles}] | " +
                                                         $"Conversations: {conversationsProcessed + 1} | " +
                                                         $"Audio files: {totalExtracted}";
                                    });

                                    // Create subfolder for this conversation
                                    var convoFolder = Path.Combine(outputFolder, convo.ObjectName.Name);
                                    Directory.CreateDirectory(convoFolder);

                                    // Extract audio
                                    int extracted = DialogueEditorWindow.ExtractAudioFilesForSpeaker(
                                        speakerNodes, speakerTag, includeText, extractMale, extractFemale, convoFolder);

                                    totalExtracted += extracted;
                                    conversationsProcessed++;
                                }
                            }
                            catch (Exception ex)
                            {
                                // Log or skip problematic conversations
                                System.Diagnostics.Debug.WriteLine($"Error processing conversation {convo.ObjectName} in {file.Key}: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log or skip problematic files
                        System.Diagnostics.Debug.WriteLine($"Error processing file {file.Key}: {ex.Message}");
                    }
                }

                return new { totalExtracted, conversationsProcessed, filesProcessed };
            }).ContinueWith(task =>
            {
                // Update UI on main thread when complete
                Application.Current.Dispatcher.Invoke(() =>
                {
                    window.IsBusy = false;

                    if (task.IsFaulted)
                    {
                        MessageBox.Show(window as Window,
                            $"Error during extraction:\n{task.Exception?.InnerException?.Message ?? task.Exception?.Message}",
                            "Extraction Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                    else
                    {
                        var result = task.Result;
                        MessageBox.Show(window as Window,
                            $"Extraction complete!\n" +
                            $"Files scanned: {result.filesProcessed}\n" +
                            $"Conversations processed: {result.conversationsProcessed}\n" +
                            $"Audio files extracted: {result.totalExtracted}",
                            "Extraction Complete",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                });
            });
        }
    }
}
