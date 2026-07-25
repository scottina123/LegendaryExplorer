using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Gammtek.Conduit.MassEffect3.SFXGame.CodexMap;
using Gammtek.Conduit.MassEffect3.SFXGame.QuestMap;
using Gammtek.Conduit.MassEffect3.SFXGame.StateEventMap;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.ToolsetDev.MemoryAnalyzer;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Interfaces;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using Microsoft.Win32;

namespace LegendaryExplorer.Tools.PlotEditor
{
    public partial class PlotEditorWindow : WPFBase, IRecents
    {
        private static readonly string[] VanillaPlotFiles =
        [
            "SFXGameInfoSP_SF.pcc",
            "Startup_HEN_PR_INT.pcc",
            "Startup_EXP_Pack003_Base_INT.pcc",
            "Startup_EXP_Pack003_INT.pcc",
            "Startup_EXP_Pack002_INT.pcc",
            "Startup_EXP_Pack001_INT.pcc",
            "Startup_CON_END_INT.pcc",
            "Startup_CON_DH1_INT.pcc",
        ];

        public PlotEditorWindow() : base("Plot Editor")
        {
            GotoCommand = new GenericCommand(FocusGoto, () => Pcc != null);

            InitializeComponent();
            PopulateVanillaPlotFilesMenu();
            RecentsController.InitRecentControl(Toolname, Recents_MenuItem, LoadFile);

            FindObjectUsagesControl.parentRef = this;
        }

        public string CurrentFile => Pcc != null ? Path.GetFileName(Pcc.FilePath) : "Select a file to load";

        public ICommand GotoCommand { get; set; }

        public void OpenFile()
        {
            var dlg = AppDirectories.GetOpenPackageDialog();

            if (DirectoryMemory.ShowDialog(dlg) != true)
            {
                return;
            }

            LoadFile(dlg.FileName);
        }

        private void PopulateVanillaPlotFilesMenu()
        {
            foreach (string fileName in VanillaPlotFiles)
            {
                var menuItem = new MenuItem
                {
                    Header = fileName,
                    Tag = fileName,
                };
                menuItem.Click += VanillaPlotFileMenuItem_Click;
                VanillaPlotFiles_MenuItem.Items.Add(menuItem);
            }
        }

        private void VanillaPlotFileMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string fileName })
            {
                return;
            }

            string le3Path = MEDirectories.GetDefaultGamePath(MEGame.LE3);
            if (string.IsNullOrWhiteSpace(le3Path))
            {
                MessageBox.Show(this,
                    "Vanilla plot files require a configured Mass Effect Legendary Edition 3 install path.",
                    "Plot Editor",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            string filePath = FindVanillaPlotFile(fileName, le3Path);
            if (filePath == null)
            {
                MessageBox.Show(this,
                    $"Could not locate '{fileName}' in the configured Mass Effect Legendary Edition 3 installation.",
                    "Plot Editor",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            LoadFile(filePath);
        }

        private static string FindVanillaPlotFile(string fileName, string le3Path)
        {
            string basegamePath = Path.Combine(LE3Directory.GetCookedPCPath(le3Path), fileName);
            if (File.Exists(basegamePath))
            {
                return basegamePath;
            }

            string dlcRoot = LE3Directory.GetDLCPath(le3Path);
            foreach (string officialDlc in MEDirectories.OfficialDLC(MEGame.LE3))
            {
                string dlcPath = Path.Combine(dlcRoot, officialDlc);
                if (!Directory.Exists(dlcPath))
                {
                    continue;
                }

                string match = Directory.EnumerateFiles(dlcPath, fileName, SearchOption.AllDirectories).FirstOrDefault();
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        public void LoadFile(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (!File.Exists(path))
            {
                return;
            }

            LoadMEPackage(path);

            CodexMapControl?.Open(Pcc);

            QuestMapControl?.Open(Pcc);

            StateEventMapControl?.Open(Pcc);

            ConsequenceMapControl?.Open(Pcc, "ConsequenceMap");

            RecentsController.AddRecent(path, false, Pcc?.Game);
            RecentsController.SaveRecentList(true);
            Title = $"Plot Editor - {path}";
            OnPropertyChanged(nameof(CurrentFile));

            //Hiding "Recents" panel
            if (MainTabControl.SelectedIndex == 0)
            {
                MainTabControl.SelectedIndex = 1;
            }
        }

        public static bool CanOpenExport(ExportEntry export)
        {
            if (export == null || export.IsDefaultObject)
            {
                return false;
            }

            if (export.ClassName == "BioStateEventMap")
            {
                return export.ObjectName == "StateTransitionMap" || export.ObjectName == "ConsequenceMap";
            }

            return export.ClassName is "BioCodexMap" or "BioQuestMap" or "BioConsequenceMap" or "BioOutcomeMap";
        }

        public static void OpenExportInPlotEditor(ExportEntry export)
        {
            if (!CanOpenExport(export))
            {
                return;
            }

            if (GetExistingToolInstance(export.FileRef.FilePath, out PlotEditorWindow plotEditor))
            {
                plotEditor.RestoreAndBringToFront();
                plotEditor.OpenExport(export);
                return;
            }

            PlotEditorWindow newPlotEditor = new();
            newPlotEditor.Show();
            newPlotEditor.OpenExport(export);
            newPlotEditor.Activate();
        }

        public void OpenExport(ExportEntry export)
        {
            if (!CanOpenExport(export))
            {
                return;
            }

            if (!string.Equals(Pcc?.FilePath, export.FileRef.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                LoadFile(export.FileRef.FilePath);
            }

            switch (export.ClassName)
            {
                case "BioCodexMap":
                    MainTabControl.SelectedValue = CodexMapControl;
                    break;
                case "BioQuestMap":
                    MainTabControl.SelectedValue = QuestMapControl;
                    break;
                case "BioConsequenceMap":
                    MainTabControl.SelectedValue = ConsequenceMapControl;
                    break;
                case "BioStateEventMap":
                    MainTabControl.SelectedValue = export.ObjectName == "ConsequenceMap" ? ConsequenceMapControl : StateEventMapControl;
                    break;
            }
        }

        public async void SaveFile(string filepath = null)
        {
            if (Pcc == null)
            {
                return;
            }

            if (CodexMapControl != null)
            {
                if (CodexMapView.TryFindCodexMap(Pcc, out ExportEntry export, out int _))
                {
                    using var stream = new MemoryStream();
                    var codexMap = CodexMapControl.ToCodexMap();
                    var binaryCodexMap = new BinaryBioCodexMap(codexMap.Sections, codexMap.Pages);

                    binaryCodexMap.Save(stream);

                    export.WriteBinary(stream.ToArray());
                }
            }

            if (QuestMapControl != null)
            {
                if (QuestMapControl.TryFindQuestMap(Pcc, out ExportEntry export, out int _))
                {
                    using var stream = new MemoryStream();
                    var questMap = QuestMapControl.ToQuestMap();
                    var binaryQuestMap = new BinaryBioQuestMap(questMap.Quests, questMap.BoolTaskEvals, questMap.IntTaskEvals, questMap.FloatTaskEvals);

                    binaryQuestMap.Save(stream);

                    export.WriteBinary(stream.ToArray());
                }
            }

            if (StateEventMapControl != null)
            {
                if (StateEventMapView.TryFindStateEventMap(Pcc, out ExportEntry export))
                {
                    using var stream = new MemoryStream();
                    var stateEventMap = StateEventMapControl.ToStateEventMap();
                    var binaryStateEventMap = new BinaryBioStateEventMap(stateEventMap.StateEvents);

                    binaryStateEventMap.Save(stream, Pcc.Game);

                    export.WriteBinary(stream.ToArray());
                }
            }

            if (ConsequenceMapControl != null)
            {
                if (StateEventMapView.TryFindStateEventMap(Pcc, out ExportEntry export, "ConsequenceMap"))
                {
                    using var stream = new MemoryStream();
                    var consequenceMap = ConsequenceMapControl.ToStateEventMap();
                    var binaryConsequenceMap = new BinaryBioStateEventMap(consequenceMap.StateEvents);

                    binaryConsequenceMap.Save(stream, Pcc.Game);

                    export.WriteBinary(stream.ToArray());
                }
            }

            filepath ??= Pcc.FilePath;

            await Pcc.SaveAsync(filepath);
        }

        public void SaveFileAs()
        {
            var dlg = new SaveFileDialog { Filter = "Support files|*.pcc;*.upk" };

            if (DirectoryMemory.ShowDialog(dlg) != true)
            {
                return;
            }

            SaveFile(dlg.FileName);
        }

        public override void HandleUpdate(List<PackageUpdate> updates)
        {
            if (Pcc == null || updates is null || updates.Count == 0)
            {
                return;
            }

            HashSet<int> updatedExports = updates
                .Where(update => update.Change.HasFlag(PackageChange.Export))
                .Select(update => update.Index)
                .ToHashSet();

            if (updatedExports.Count == 0)
            {
                return;
            }

            HashSet<int> plotMapExports = [];
            if (CodexMapView.TryFindCodexMap(Pcc, out ExportEntry codexMapExport, out _))
            {
                plotMapExports.Add(codexMapExport.UIndex);
            }

            if (QuestMapControl?.TryFindQuestMap(Pcc, out ExportEntry questMapExport, out _) == true)
            {
                plotMapExports.Add(questMapExport.UIndex);
            }

            if (StateEventMapView.TryFindStateEventMap(Pcc, out ExportEntry stateEventMapExport))
            {
                plotMapExports.Add(stateEventMapExport.UIndex);
            }

            if (StateEventMapView.TryFindStateEventMap(Pcc, out ExportEntry consequenceMapExport, "ConsequenceMap"))
            {
                plotMapExports.Add(consequenceMapExport.UIndex);
            }

            if (!updatedExports.Overlaps(plotMapExports))
            {
                return;
            }

            int selectedTabIndex = MainTabControl.SelectedIndex;
            int selectedCodexTreeTabIndex = CodexMapControl?.SelectedCodexTreeTabIndex ?? 0;
            int? selectedCodexPageId = CodexMapControl?.SelectedCodexPage.Value != null ? CodexMapControl.SelectedCodexPage.Key : null;
            int? selectedCodexSectionId = CodexMapControl?.SelectedCodexSection.Value != null ? CodexMapControl.SelectedCodexSection.Key : null;
            string codexSearchText = CodexMapControl?.SearchText;
            int? selectedQuestId = QuestMapControl?.SelectedQuest.Value != null ? QuestMapControl.SelectedQuest.Key : null;
            string questSearchText = QuestMapControl?.QuestSearchText;
            int? selectedStateEventId = StateEventMapControl?.SelectedStateEvent.Value != null ? StateEventMapControl.SelectedStateEvent.Key : null;
            string stateEventSearchText = StateEventMapControl?.StateEventSearchText;
            int? selectedConsequenceId = ConsequenceMapControl?.SelectedStateEvent.Value != null ? ConsequenceMapControl.SelectedStateEvent.Key : null;
            string consequenceSearchText = ConsequenceMapControl?.StateEventSearchText;

            CodexMapControl?.Open(Pcc);
            QuestMapControl?.Open(Pcc);
            StateEventMapControl?.Open(Pcc);
            ConsequenceMapControl?.Open(Pcc, "ConsequenceMap");

            if (CodexMapControl != null)
            {
                CodexMapControl.SearchText = codexSearchText;
                CodexMapControl.SelectedCodexTreeTabIndex = selectedCodexTreeTabIndex;
                if (selectedCodexPageId is int codexPageId)
                {
                    CodexMapControl.SelectedCodexPage = CodexMapControl.CodexPages?.FirstOrDefault(pair => pair.Key == codexPageId) ?? default;
                    CodexMapControl.SelectedCodexSection = default;
                }
                else if (selectedCodexSectionId is int codexSectionId)
                {
                    CodexMapControl.SelectedCodexSection = CodexMapControl.CodexSections?.FirstOrDefault(pair => pair.Key == codexSectionId) ?? default;
                    CodexMapControl.SelectedCodexPage = default;
                }
            }

            if (QuestMapControl != null)
            {
                QuestMapControl.QuestSearchText = questSearchText;
                QuestMapControl.SelectedQuest = selectedQuestId is int questId
                    ? QuestMapControl.Quests?.FirstOrDefault(pair => pair.Key == questId) ?? default
                    : default;
            }

            if (StateEventMapControl != null)
            {
                StateEventMapControl.StateEventSearchText = stateEventSearchText;
                StateEventMapControl.SelectedStateEvent = selectedStateEventId is int stateEventId
                    ? StateEventMapControl.StateEvents?.FirstOrDefault(pair => pair.Key == stateEventId) ?? default
                    : default;
            }

            if (ConsequenceMapControl != null)
            {
                ConsequenceMapControl.StateEventSearchText = consequenceSearchText;
                ConsequenceMapControl.SelectedStateEvent = selectedConsequenceId is int consequenceId
                    ? ConsequenceMapControl.StateEvents?.FirstOrDefault(pair => pair.Key == consequenceId) ?? default
                    : default;
            }

            if (selectedTabIndex >= 0 && selectedTabIndex < MainTabControl.Items.Count)
            {
                MainTabControl.SelectedIndex = selectedTabIndex;
            }
        }

        private void Save_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = Pcc != null;
        }

        private void Save_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            SaveFile();
        }

        private void SaveAs_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            SaveFileAs();
        }

        private void Open_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = true;
        }

        private void Open_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            OpenFile();
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Note that you can have more than one file.
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                string ext = Path.GetExtension(files[0]).ToLower();
                if (ext != ".upk" && ext != ".pcc")
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                }
            }
            else
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Note that you can have more than one file.
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                string ext = Path.GetExtension(files[0]).ToLower();
                if (ext == ".upk" || ext == ".pcc")
                {
                    LoadFile(files[0]);
                }
            }
        }

        public void GoToCodex(int id)
        {
            var targetCodex = CodexMapControl.CodexPages.FirstOrDefault(kvp => kvp.Key == id);
            if (!targetCodex.Equals(default(KeyValuePair<int, BioCodexPage>)))
            {
                MainTabControl.SelectedValue = CodexMapControl;
                CodexMapControl.GoToCodexPage(targetCodex);
            }
        }

        public void GoToQuest(int id)
        {
            var targetQuest = QuestMapControl.Quests.FirstOrDefault(kvp => kvp.Key == id);
            if (!targetQuest.Equals(default(KeyValuePair<int, BioQuest>)))
            {
                MainTabControl.SelectedValue = QuestMapControl;
                QuestMapControl.GoToQuest(targetQuest);
            }
        }

        public void GoToStateEvent(int id)
        {
            var targetEvent = StateEventMapControl.StateEvents.FirstOrDefault(kvp => kvp.Key == id);

            // If the ID is the default, try the consequence map
            if (targetEvent.Equals(default(KeyValuePair<int, BioStateEvent>)))
            {
                targetEvent = ConsequenceMapControl.StateEvents.FirstOrDefault(kvp => kvp.Key == id);
            }

            GoToStateEvent(targetEvent);
        }

        public void GoToStateEvent(KeyValuePair<int, BioStateEvent> targetEvent)
        {
            if ((bool)ConsequenceMapControl?.StateEvents.Contains(targetEvent))
            {
                MainTabControl.SelectedValue = ConsequenceMapControl;
                ConsequenceMapControl.SelectStateEvent(targetEvent);
            }
            else
            {
                MainTabControl.SelectedValue = StateEventMapControl;
                StateEventMapControl.SelectStateEvent(targetEvent);
            }
        }

        public void PropogateRecentsChange(string propogationSource, IEnumerable<RecentsControl.RecentItem> newRecents)
        {
            RecentsController.PropogateRecentsChange(false, newRecents);
        }

        private void FocusGoto()
        {
            Goto_TextBox.Focus();
            Goto_TextBox.SelectAll();
        }

        private void GotoButton_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(Goto_TextBox.Text, out int n))
            {
                GoToStateEvent(n);
            }
        }

        private void OpenInPackageEditor_Click(object sender, RoutedEventArgs e)
        {
            if (Pcc == null)
            {
                return;
            }

            if (GetExistingToolInstance(Pcc.FilePath, out PackageEditorWindow packageEditor))
            {
                packageEditor.RestoreAndBringToFront();
                return;
            }

            PackageEditorWindow newPackageEditor = new();
            newPackageEditor.Show();
            newPackageEditor.LoadFile(Pcc.FilePath);
            newPackageEditor.Activate();
        }

        private void Goto_TextBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return && !e.IsRepeat)
            {
                GotoButton_Click(null, null);
            }
        }

        public string Toolname => "NativesEditor";

        private void PlotEditorWindow_OnClosing(object sender, CancelEventArgs e)
        {
            if (e.Cancel)
            {
                return;
            }
            RecentsController?.Dispose();
        }
    }
}
