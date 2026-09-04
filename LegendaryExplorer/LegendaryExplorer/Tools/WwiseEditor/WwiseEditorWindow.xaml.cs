using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BinarySerialization;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorer.ToolsetDev.MemoryAnalyzer;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Interfaces;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorer.UnrealExtensions;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.AppCenter.Analytics;
using Microsoft.Win32;
using Newtonsoft.Json;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using Piccolo;
using Piccolo.Event;
using Piccolo.Nodes;
using ME3Tweaks.Wwiser;
using ME3Tweaks.Wwiser.Formats;
using ME3Tweaks.Wwiser.Model.Action;
using ME3Tweaks.Wwiser.Model.Hierarchy.Enums;
using ME3Tweaks.Wwiser.Model.ParameterNode;
using ME3Tweaks.Wwiser.Model.RTPC;
using WwiserAction = ME3Tweaks.Wwiser.Model.Hierarchy.Action;
using WwiserAttenuation = ME3Tweaks.Wwiser.Model.Hierarchy.Attenuation;
using WwiserActiveFlags = ME3Tweaks.Wwiser.Model.Action.Specific.ActiveFlags;
using WwiserBankSourceData = ME3Tweaks.Wwiser.Model.Hierarchy.BankSourceData;
using WwiserPauseResume = ME3Tweaks.Wwiser.Model.Action.Specific.PauseResume;
using WwiserEvent = ME3Tweaks.Wwiser.Model.Hierarchy.Event;
using WwiserEmptyHircItem = ME3Tweaks.Wwiser.Model.Hierarchy.EmptyHircItem;
using WwiserHircItem = ME3Tweaks.Wwiser.Model.Hierarchy.HircItem;
using WwiserHircItemContainer = ME3Tweaks.Wwiser.Model.Hierarchy.HircItemContainer;
using WwiserIHasNode = ME3Tweaks.Wwiser.Model.Hierarchy.IHasNode;
using WwiserSound = ME3Tweaks.Wwiser.Model.Hierarchy.Sound;
using Brushes = System.Drawing.Brushes;
using Color = System.Drawing.Color;
using Image = System.Drawing.Image;
using Path = System.IO.Path;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;
using CoreWwiseBank = LegendaryExplorerCore.Unreal.BinaryConverters.WwiseBank;
using CoreWwiseEvent = LegendaryExplorerCore.Unreal.BinaryConverters.WwiseEvent;

namespace LegendaryExplorer.Tools.WwiseEditor
{
    /// <summary>
    /// Interaction logic for WwiseEditorWPF.xaml
    /// </summary>
    public partial class WwiseEditorWindow : WPFBase, IRecents
    {
        private const uint StopAllEventId = 788884573;
        private const int MaximumEffectSlots = 4;

        private sealed class EditableOpaqueMusicNode : WwiserHircItem, WwiserIHasNode
        {
            internal required WwiserEmptyHircItem SourceItem { get; init; }
            internal required byte[] PrefixData { get; init; }
            internal required byte[] SuffixData { get; init; }
            internal required HircType SourceType { get; init; }
            public NodeBaseParameters NodeBaseParameters { get; set; } = new();
            public override HircType HircType => SourceType;

            internal void Commit(uint version)
            {
                using var nodeData = new MemoryStream();
                new BinarySerializer().Serialize(nodeData, NodeBaseParameters,
                    new BankSerializationContext(version));
                SourceItem.Data = PrefixData.Concat(nodeData.ToArray()).Concat(SuffixData).ToArray();
            }
        }

        private struct SaveData
        {
            public uint ID;
            public float X;
            public float Y;
        }
        private readonly WwiseGraphEditor graphEditor;
        public WwiseEditorWindow() : base("Wwise Editor")
        {
            DataContext = this;
            StatusText = "Select package file to load";
            LoadCommands();
            InitializeComponent();

            RecentsController.InitRecentControl(Toolname, Recents_MenuItem, fileName=>LoadFile(fileName));

            // Apply theme-appropriate colors based on current dark mode setting
            ApplyThemeDefaults();

            // Subscribe to theme changes to update graph colors dynamically
            ThemeManager.ThemeChanged += OnThemeChanged;

            graphEditor = (WwiseGraphEditor)GraphHost.Child;
            graphEditor.BackColor = GraphEditorBackColor;

            AutoSaveView_MenuItem.IsChecked = Misc.AppSettings.Settings.WwiseGraphEditor_AutoSaveView;

            // Initialize color pickers with loaded colors
            ClrPcker_Background.SelectedColor = GraphEditorBackColor.ToWPFColor();
            ClrPcker_BoxFill.SelectedColor = BoxFillColor.ToWPFColor();
            ClrPcker_TitleBox.SelectedColor = TitleBoxColor.ToWPFColor();
            ClrPcker_CommentText.SelectedColor = CommentTextColor.ToWPFColor();
            ClrPcker_BoxText.SelectedColor = BoxTextColor.ToWPFColor();
            ClrPcker_BoxOutline.SelectedColor = BoxOutlineColor.ToWPFColor();
            ClrPcker_Connection.SelectedColor = ConnectionColor.ToWPFColor();

            soundPanel.SoundPanel_TabsControl.SelectedIndex = 1;
            soundPanel.HIRCObjectSelected += SoundPanel_HIRCObjectSelected;
            soundPanel.HIRCEventSettingsRequested += SoundPanel_HIRCEventSettingsRequested;
        }

        public WwiseEditorWindow(ExportEntry exportToLoad) : this()
        {
            FileQueuedForLoad = exportToLoad.FileRef.FilePath;
            ExportQueuedForFocusing = exportToLoad;
        }

        public WwiseEditorWindow(string filePath, int uIndex = 0) : this()
        {
            FileQueuedForLoad = filePath;
            ExportQueuedForFocusing = null;
            UIndexQueuedForFocusing = uIndex;
        }

        public ObservableCollectionExtended<ExportEntry> WwiseBankExports { get; } = new();
        public ObservableCollectionExtended<WwiseHircObjNode> CurrentObjects { get; } = new();

        private List<SaveData> SavedPositions;

        private string FileQueuedForLoad;
        private ExportEntry ExportQueuedForFocusing;
        private readonly int UIndexQueuedForFocusing;
        public string CurrentFile;
        public string JSONpath;

        #region Graph Color Properties

        private Color _graphEditorBackColor = Color.FromArgb(167, 167, 167);
        public Color GraphEditorBackColor
        {
            get => _graphEditorBackColor;
            set
            {
                if (_graphEditorBackColor != value)
                {
                    _graphEditorBackColor = value;
                    if (graphEditor != null)
                    {
                        graphEditor.BackColor = value;
                        if (CurrentObjects.Any())
                        {
                            RefreshView();
                        }
                    }
                }
            }
        }

        private Color _boxFillColor = Color.FromArgb(140, 140, 140);
        public Color BoxFillColor
        {
            get => _boxFillColor;
            set
            {
                if (_boxFillColor != value)
                {
                    _boxFillColor = value;
                    WwiseHircObjNode.NodeBrushColor = value;
                    if (CurrentObjects.Any())
                    {
                        RefreshView();
                    }
                }
            }
        }

        private Color _titleBoxColor = Color.FromArgb(112, 112, 112);
        public Color TitleBoxColor
        {
            get => _titleBoxColor;
            set
            {
                if (_titleBoxColor != value)
                {
                    _titleBoxColor = value;
                    WwiseHircObjNode.TitleBoxBrushColor = value;
                    if (CurrentObjects.Any())
                    {
                        RefreshView();
                    }
                }
            }
        }

        private Color _commentTextColor = Color.FromArgb(74, 63, 190);
        public Color CommentTextColor
        {
            get => _commentTextColor;
            set
            {
                if (_commentTextColor != value)
                {
                    _commentTextColor = value;
                    WwiseHircObjNode.CommentTextColor = value;
                    if (CurrentObjects.Any())
                    {
                        RefreshView();
                    }
                }
            }
        }

        private Color _boxTextColor = Color.FromArgb(255, 255, 128);
        public Color BoxTextColor
        {
            get => _boxTextColor;
            set
            {
                if (_boxTextColor != value)
                {
                    _boxTextColor = value;
                    WwiseHircObjNode.BoxTextColor = value;
                    if (CurrentObjects.Any())
                    {
                        RefreshView();
                    }
                }
            }
        }

        private Color _boxOutlineColor = Color.Black;
        public Color BoxOutlineColor
        {
            get => _boxOutlineColor;
            set
            {
                if (_boxOutlineColor != value)
                {
                    _boxOutlineColor = value;
                    WwiseHircObjNode.BoxOutlineColor = value;
                    if (CurrentObjects.Any())
                    {
                        RefreshView();
                    }
                }
            }
        }

        private Color _connectionColor = Color.Black;
        public Color ConnectionColor
        {
            get => _connectionColor;
            set
            {
                if (_connectionColor != value)
                {
                    _connectionColor = value;
                    WwiseHircObjNode.ConnectionColor = value;
                    if (CurrentObjects.Any())
                    {
                        RefreshView();
                    }
                }
            }
        }

        /// <summary>
        /// Applies theme-appropriate default colors based on the current dark mode setting.
        /// </summary>
        private void ApplyThemeDefaults()
        {
            bool isDarkMode = Settings.Global_DarkMode_Enabled;

            if (isDarkMode)
            {
                // Dark theme - matching Sequence Editor dark mode colors
                _graphEditorBackColor = ThemeManager.DarkCanvasDrawingColor;
                _boxFillColor = ThemeManager.IsModernDark
                    ? Color.FromArgb(22, 36, 51)
                    : Color.FromArgb(45, 45, 48);
                _titleBoxColor = ThemeManager.IsModernDark
                    ? Color.FromArgb(16, 26, 37)
                    : Color.FromArgb(37, 37, 38);
                _commentTextColor = ThemeManager.IsModernDark
                    ? Color.FromArgb(71, 180, 213)
                    : Color.FromArgb(87, 166, 74);
                _boxTextColor = ThemeManager.IsModernDark
                    ? Color.FromArgb(232, 240, 245)
                    : Color.FromArgb(220, 220, 220);
                _boxOutlineColor = Color.FromArgb(35, 34, 34);
                _connectionColor = Color.White;
            }
            else
            {
                // Light theme defaults (original Wwise Editor colors)
                _graphEditorBackColor = Color.FromArgb(167, 167, 167);
                _boxFillColor = Color.FromArgb(140, 140, 140);
                _titleBoxColor = Color.FromArgb(112, 112, 112);
                _commentTextColor = Color.FromArgb(74, 63, 190);
                _boxTextColor = Color.FromArgb(255, 255, 128);
                _boxOutlineColor = Color.Black;
                _connectionColor = Color.Black;
            }

            // Apply to static properties used by WwiseHircObjNode
            WwiseHircObjNode.NodeBrushColor = _boxFillColor;
            WwiseHircObjNode.TitleBoxBrushColor = _titleBoxColor;
            WwiseHircObjNode.CommentTextColor = _commentTextColor;
            WwiseHircObjNode.BoxTextColor = _boxTextColor;
            WwiseHircObjNode.BoxOutlineColor = _boxOutlineColor;
            WwiseHircObjNode.ConnectionColor = _connectionColor;
        }

        /// <summary>
        /// Handles theme changes from the ThemeManager.
        /// </summary>
        private void OnThemeChanged(object sender, bool isDarkMode)
        {
            ApplyThemeDefaults();

            if (graphEditor != null)
            {
                graphEditor.BackColor = GraphEditorBackColor;
            }

            // Update color pickers to reflect the new theme colors
            ClrPcker_Background.SelectedColor = GraphEditorBackColor.ToWPFColor();
            ClrPcker_BoxFill.SelectedColor = BoxFillColor.ToWPFColor();
            ClrPcker_TitleBox.SelectedColor = TitleBoxColor.ToWPFColor();
            ClrPcker_CommentText.SelectedColor = CommentTextColor.ToWPFColor();
            ClrPcker_BoxText.SelectedColor = BoxTextColor.ToWPFColor();
            ClrPcker_BoxOutline.SelectedColor = BoxOutlineColor.ToWPFColor();
            ClrPcker_Connection.SelectedColor = ConnectionColor.ToWPFColor();

            if (CurrentObjects.Any())
            {
                RefreshView();
            }
        }

        private void ColorPicker_SelectedColorChanged(object sender, RoutedPropertyChangedEventArgs<System.Windows.Media.Color?> e)
        {
            var source = (Xceed.Wpf.Toolkit.ColorPicker)sender;
            if (e.NewValue is not null)
            {
                var newColor = e.NewValue.Value.ToWinformsColor();
                switch (source.Name)
                {
                    case "ClrPcker_Background":
                        GraphEditorBackColor = newColor;
                        Settings.WwiseGraphEditor_BackgroundColor = newColor.ToArgb();
                        break;
                    case "ClrPcker_BoxFill":
                        BoxFillColor = newColor;
                        Settings.WwiseGraphEditor_BoxFillColor = newColor.ToArgb();
                        break;
                    case "ClrPcker_TitleBox":
                        TitleBoxColor = newColor;
                        Settings.WwiseGraphEditor_TitleBoxColor = newColor.ToArgb();
                        break;
                    case "ClrPcker_CommentText":
                        CommentTextColor = newColor;
                        Settings.WwiseGraphEditor_CommentTextColor = newColor.ToArgb();
                        break;
                    case "ClrPcker_BoxText":
                        BoxTextColor = newColor;
                        Settings.WwiseGraphEditor_BoxTextColor = newColor.ToArgb();
                        break;
                    case "ClrPcker_BoxOutline":
                        BoxOutlineColor = newColor;
                        Settings.WwiseGraphEditor_BoxOutlineColor = newColor.ToArgb();
                        break;
                    case "ClrPcker_Connection":
                        ConnectionColor = newColor;
                        Settings.WwiseGraphEditor_ConnectionColor = newColor.ToArgb();
                        break;
                }
                Settings.Save();
            }
        }

        #endregion

        private ExportEntry _currentExport;
        public ExportEntry CurrentExport
        {
            get => _currentExport;
            set
            {
                if (AutoSaveView_MenuItem.IsChecked)
                {
                    SaveView();
                }
                if (SetProperty(ref _currentExport, value))
                {
                    LoadBank(value, true);
                }
            }
        }

        private WwiseHircObjNode _selectedNode;
        public WwiseHircObjNode SelectedNode
        {
            get => _selectedNode;
            private set
            {
                if (value != _selectedNode && _selectedNode != null)
                {
                    _selectedNode.IsSelected = false;
                }
                if (SetProperty(ref _selectedNode, value) && value != null)
                {
                    value.IsSelected = true;
                    if (panToSelection)
                    {
                        graphEditor.Camera.AnimateViewToCenterBounds(value.GlobalFullBounds, false, 100);
                    }

                    if (!(value is WExport))
                    {
                        int hircIndex = FindHircListIndexById(soundPanel.HIRCObjects.Select(hirc => hirc.ID), value.ID);
                        if (hircIndex >= 0)
                        {
                            soundPanel.SelectHircObject(value.ID);
                        }
                    }
                }
            }
        }

        internal static int FindHircListIndexById(IEnumerable<uint> hircIds, uint selectedId)
        {
            int index = 0;
            foreach (uint hircId in hircIds)
            {
                if (hircId == selectedId)
                {
                    return index;
                }

                index++;
            }

            return -1;
        }

        private WwiseBankParsed CurrentWwiseBank;

        public ICommand OpenCommand { get; set; }
        public ICommand SaveCommand { get; set; }
        public ICommand SaveAsCommand { get; set; }
        public ICommand SaveImageCommand { get; set; }
        public ICommand SaveViewCommand { get; set; }
        public ICommand AutoLayoutCommand { get; set; }

        private void LoadCommands()
        {
            OpenCommand = new GenericCommand(OpenFile);
            SaveCommand = new GenericCommand(SavePackage, IsPackageLoaded);
            SaveAsCommand = new GenericCommand(SavePackageAs, IsPackageLoaded);
            SaveImageCommand = new GenericCommand(SaveImage, () => CurrentObjects.Any);
            SaveViewCommand = new GenericCommand(() => SaveView(), () => CurrentObjects.Any);
            AutoLayoutCommand = new GenericCommand(AutoLayout, () => CurrentObjects.Any);
        }

        private bool IsPackageLoaded() => Pcc != null;

        private async void SavePackageAs()
        {
            string extension = Path.GetExtension(Pcc.FilePath);
            SaveFileDialog d = new () { Filter = $"*{extension}|*{extension}" };
            if (DirectoryMemory.ShowDialog(d) == true)
            {
                await Pcc.SaveAsync(d.FileName);
                MessageBox.Show(this, "Done.");
            }
        }

        private async void SavePackage()
        {
            await Pcc.SaveAsync();
        }

        private void OpenFile()
        {
            OpenFileDialog d = new ()
            {
                Filter = GameFileFilters.ME3ME2SaveFileFilter,
                CustomPlaces = AppDirectories.GameCustomPlaces
            };
            if (DirectoryMemory.ShowDialog(d) == true)
            {
#if !DEBUG
                try
                {
#endif

                LoadFile(d.FileName);
#if !DEBUG
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Unable to open file:\n" + ex.Message);
                }
#endif
            }
        }

        private void AdjustBankSettings_Click(object sender, RoutedEventArgs e)
        {
            if (TryAdjustBankSettings(this, CurrentExport))
            {
                RefreshView();
                StatusBar_LeftMostText.Text = $"Updated settings for {CurrentExport.ObjectName.Instanced}";
            }
        }

        internal static bool TryAdjustBankSettings(Window owner, ExportEntry bankExport)
        {
            if (bankExport?.ClassName != "WwiseBank")
            {
                return false;
            }

            try
            {
                var rawBank = bankExport.GetBinaryData<CoreWwiseBank>();
                using var input = new MemoryStream(rawBank.BnkFile, false);
                var bank = WwiseBankParser.Deserialize(input);
                var parameterNodes = GetEditableParameterNodes(bank);
                var settingNodes = GetRuntimeOverrideNodes(parameterNodes);

                if (settingNodes.Count == 0)
                {
                    MessageBox.Show(owner, "No editable audio nodes were found in this bank.", "Settings not adjusted",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                var sounds = bank.HIRC?.Items
                    .Select(item => item.Item)
                    .OfType<WwiserSound>()
                    .ToList() ?? [];
                return EditAudioSettings(owner, bankExport, bank, bankExport.ObjectName.Instanced, true,
                    settingNodes, settingNodes, sounds, parameterNodes, StopAllEventId, "Stop");
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"Unable to adjust bank settings:\n{ex.Message}", "Bank settings failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void AdjustEventSettings_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedNode is WExport { Export.ClassName: "WwiseEvent" } eventNode &&
                TryAdjustEventSettings(this, eventNode.Export, CurrentExport))
            {
                RefreshView();
                StatusBar_LeftMostText.Text = $"Updated settings for {eventNode.Export.ObjectName.Instanced}";
            }
        }

        internal static bool TryAdjustEventSettings(Window owner, ExportEntry eventExport,
            ExportEntry bankExport = null)
        {
            if (eventExport?.ClassName != "WwiseEvent")
            {
                return false;
            }

            try
            {
                bankExport ??= FindReferencedBank(eventExport);
                if (bankExport == null)
                {
                    MessageBox.Show(owner,
                        "The WwiseBank referenced by this event could not be found in the package.",
                        "Event settings unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                var rawBank = bankExport.GetBinaryData<CoreWwiseBank>();
                using var input = new MemoryStream(rawBank.BnkFile, false);
                var bank = WwiseBankParser.Deserialize(input);
                uint eventId = WExport.GetExportId(eventExport);
                var parameterNodes = GetEditableParameterNodes(bank);
                var targetNodes = GetEventTargetAudioNodes(bank, eventId, parameterNodes);
                if (targetNodes.Count == 0)
                {
                    MessageBox.Show(owner,
                        "This WwiseEvent has no editable audio targets. Only Play actions that resolve to audio hierarchy nodes can be adjusted.",
                        "Event settings unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                var targetSounds = targetNodes.OfType<WwiserSound>().ToList();
                var effectScopeNodes = GetEventEffectScopeNodes(targetNodes);
                string stopEventName = GetStopEventName(eventExport.ObjectName.Name, eventExport.Game);
                uint stopEventId = WwiseOutputBusOptions.GenerateShortId(stopEventName);
                return EditAudioSettings(owner, bankExport, bank, eventExport.ObjectName.Instanced, false,
                    targetNodes, effectScopeNodes, targetSounds, parameterNodes, stopEventId, stopEventName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"Unable to adjust event settings:\n{ex.Message}", "Event settings failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private static ExportEntry FindReferencedBank(ExportEntry eventExport)
        {
            int bankIndex = 0;
            if (eventExport.Game == MEGame.LE2)
            {
                bankIndex = eventExport.GetProperty<ArrayProperty<StructProperty>>("References")?
                    .Select(reference => reference.Properties.GetProp<StructProperty>("Relationships")?.Properties
                        .GetProp<ObjectProperty>("Bank")?.Value ?? 0)
                    .FirstOrDefault(index => index > 0) ?? 0;
            }
            else if (eventExport.Game.IsGame3())
            {
                bankIndex = eventExport.GetProperty<StructProperty>("Relationships")?.Properties
                    .GetProp<ObjectProperty>("Bank")?.Value ?? 0;
            }
            else if (eventExport.Game == MEGame.ME2)
            {
                bankIndex = eventExport.GetBinaryData<CoreWwiseEvent>().Links?
                    .SelectMany(link => link.WwiseBanks ?? [])
                    .FirstOrDefault(index => index > 0) ?? 0;
            }

            if (eventExport.FileRef.TryGetUExport(bankIndex, out ExportEntry referencedBank) &&
                referencedBank.ClassName == "WwiseBank")
            {
                return referencedBank;
            }

            uint eventId = WExport.GetExportId(eventExport);
            var matchingBanks = new List<ExportEntry>();
            foreach (ExportEntry candidate in eventExport.FileRef.Exports.Where(export =>
                         export.ClassName == "WwiseBank"))
            {
                try
                {
                    var rawBank = candidate.GetBinaryData<CoreWwiseBank>();
                    using var input = new MemoryStream(rawBank.BnkFile, false);
                    var bank = WwiseBankParser.Deserialize(input);
                    if (bank.HIRC?.Items.Any(item => item.Item is WwiserEvent && item.Item.Id == eventId) == true)
                    {
                        matchingBanks.Add(candidate);
                    }
                }
                catch
                {
                    // Ignore unrelated banks that cannot be parsed by this Wwise bank model.
                }
            }

            return matchingBanks.Count == 1 ? matchingBanks[0] : null;
        }

        private static bool EditAudioSettings(Window owner, ExportEntry bankExport,
            ME3Tweaks.Wwiser.WwiseBank bank, string scopeName, bool isBankWide,
            IReadOnlyCollection<WwiserIHasNode> settingNodes,
            IReadOnlyCollection<WwiserIHasNode> effectScopeNodes,
            IReadOnlyCollection<WwiserSound> sounds,
            IReadOnlyCollection<(uint Id, WwiserIHasNode Node)> parameterNodes,
            uint stopEventId, string stopEventName)
        {
            MEGame game = bankExport.Game;
            float currentVolume = GetCommonNodeVolume(settingNodes, out bool volumeIsMixed);
            uint? currentOutputBusId = GetCommonOutputBusId(settingNodes);
            bool? currentLoopAudio = GetLoopAudioState(sounds.ToList());
            WwiseEditorEffectPreset currentEffect = GetCurrentEffectPreset(effectScopeNodes,
                parameterNodes, game, isBankWide);
            string currentEffectSummary = GetCurrentEffectSummary(effectScopeNodes, parameterNodes, game);
            bool? currentDucking = GetDuckingState(bank, settingNodes, game);
            bool? currentAttenuation = GetAttenuationState(bank, settingNodes,
                out double currentAttenuationScalePercent);
            bool stopEventExists = HasCompleteStopEvent(bank, bankExport, sounds, stopEventId);
            bool canCreateStopEvent = CanCreateStopEvent(bank, bankExport, sounds, stopEventId, stopEventName);
            bool supportsPresetData = bank.BKHD.BankGeneratorVersion == WwiseBankEffectPresets.BankVersion &&
                                      game is MEGame.LE2 or MEGame.LE3;

            string effectiveInheritedOutputBus = null;
            if (!isBankWide)
            {
                var inheritedNames = settingNodes.Select(node =>
                        GetEffectiveOutputBusName(node, parameterNodes, game))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (inheritedNames.Count == 1)
                {
                    effectiveInheritedOutputBus = inheritedNames[0];
                }
            }

            var settings = new WwiseEditorAudioSettings
            {
                Game = game,
                ScopeName = scopeName,
                TargetSummary = isBankWide
                    ? sounds.Count > 0
                        ? $"Changes apply to {sounds.Count} Sound object(s) in this bank."
                        : $"Changes apply across {settingNodes.Count} music hierarchy node(s) in this bank."
                    : sounds.Count > 0
                        ? $"Changes apply to {sounds.Count} Sound object(s) reached by this event. Other events that reuse the same Sound objects will hear the same changes."
                        : $"Changes apply to {settingNodes.Count} music hierarchy node(s) reached by this event. Other events that reuse the same nodes will hear the same changes.",
                IsBankWide = isBankWide,
                Volume = currentVolume,
                VolumeIsMixed = volumeIsMixed,
                OutputBusId = currentOutputBusId,
                EffectiveInheritedOutputBus = effectiveInheritedOutputBus,
                LoopAudio = currentLoopAudio,
                CanLoopAudio = sounds.Count > 0,
                EffectPreset = currentEffect,
                EffectSummary = currentEffectSummary,
                DuckAudio = currentDucking,
                Attenuation = currentAttenuation,
                AttenuationScalePercent = currentAttenuationScalePercent,
                CanApplyEffects = supportsPresetData,
                CanApplyDucking = supportsPresetData,
                CanApplyAttenuation = supportsPresetData,
                StopEventExists = stopEventExists,
                CanCreateStopEvent = canCreateStopEvent
            };
            var settingsDialog = new WwiseBankVolumeDialog(settings) { Owner = owner };
            if (settingsDialog.ShowDialog() != true)
            {
                return false;
            }

            bool volumeChanged = settingsDialog.VolumeWasEdited;
            bool outputBusChanged = settingsDialog.OutputBusWasEdited && settingsDialog.SelectedOutputBusId.HasValue;
            bool loopChanged = settingsDialog.LoopAudio.HasValue &&
                               settingsDialog.LoopAudio != currentLoopAudio;
            bool effectChanged = settingsDialog.SelectedEffectPreset != WwiseEditorEffectPreset.Preserve &&
                                 settingsDialog.SelectedEffectPreset != currentEffect;
            bool duckingChanged = settingsDialog.DuckAudio.HasValue &&
                                  settingsDialog.DuckAudio != currentDucking;
            bool attenuationChanged = settingsDialog.Attenuation.HasValue &&
                                      settingsDialog.Attenuation != currentAttenuation;
            bool attenuationScaleChanged = settingsDialog.Attenuation == true &&
                                           Math.Abs(settingsDialog.AttenuationScalePercent -
                                                    currentAttenuationScalePercent) > 0.0001;
            bool createStopEvent = settingsDialog.CreateStopEvent;
            if (!volumeChanged && !outputBusChanged && !loopChanged && !effectChanged &&
                !duckingChanged && !attenuationChanged && !attenuationScaleChanged && !createStopEvent)
            {
                return false;
            }

            if (effectChanged && settingsDialog.SelectedEffectPreset is not
                    (WwiseEditorEffectPreset.Inherit or WwiseEditorEffectPreset.None))
            {
                var (effectName, effectChain, _) = GetEffectDefinition(settingsDialog.SelectedEffectPreset);
                if (!CanApplyEffect(bank, effectScopeNodes, effectChain) ||
                    !WwiseBankEffectPresets.EnsureEffectData(bank, effectChain))
                {
                    MessageBox.Show(owner,
                        $"The {effectName} effect data could not be added to this bank version or the target has no free effect slots.",
                        "Effect unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            if (duckingChanged && settingsDialog.DuckAudio == true)
            {
                bool duckingDataReady = game == MEGame.LE2
                    ? WwiseBankEffectPresets.SetLe2MusicDuckingOnTargets(bank,
                        settingNodes.Select(GetNodeId).ToList(), true)
                    : WwiseBankEffectPresets.EnsureMusicDuckingData(bank);
                if (!duckingDataReady)
                {
                    MessageBox.Show(owner, "The shipped music ducking data conflicts with an object already in this bank.",
                        "Ducking unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            if (volumeChanged)
            {
                foreach (var node in settingNodes)
                {
                    SetInitialParameter(node.NodeBaseParameters.InitialParams62, PropId.Volume,
                        settingsDialog.SelectedVolume, true);
                }
            }

            if (outputBusChanged)
            {
                foreach (var node in settingNodes)
                {
                    node.NodeBaseParameters.OverrideBusId = settingsDialog.SelectedOutputBusId.Value;
                }
            }

            if (effectChanged)
            {
                ApplyEffectPresetToScopes(effectScopeNodes, settingsDialog.SelectedEffectPreset, game,
                    isBankWide ? parameterNodes.Select(item => item.Node) : null);
            }

            if (loopChanged)
            {
                foreach (var sound in sounds)
                {
                    SetLoopAudio(sound, settingsDialog.LoopAudio == true);
                }
            }

            if (duckingChanged)
            {
                if (game == MEGame.LE2)
                {
                    if (settingsDialog.DuckAudio != true)
                    {
                        WwiseBankEffectPresets.SetLe2MusicDuckingOnTargets(bank,
                            settingNodes.Select(GetNodeId).ToList(), false);
                    }
                }
                else
                {
                    WwiseBankEffectPresets.SetMusicDuckingOnScopes(settingNodes,
                        settingsDialog.DuckAudio == true);
                }
            }

            if (attenuationChanged || attenuationScaleChanged)
            {
                if (settingsDialog.Attenuation == true)
                {
                    float distanceScale = checked((float)(settingsDialog.AttenuationScalePercent / 100d));
                    if (isBankWide)
                    {
                        if (!WwiseBankEffectPresets.EnsureStandardAttenuationData(bank, game,
                                distanceScale, out uint attenuationId))
                        {
                            throw new InvalidOperationException("The standard attenuation data could not be added to this bank.");
                        }
                        WwiseBankEffectPresets.SetStandardAttenuationOnScopes(settingNodes, attenuationId, true,
                            enableDiffraction: game == MEGame.LE2);
                    }
                    else
                    {
                        foreach (var node in settingNodes)
                        {
                            uint nodeId = GetNodeId(node);
                            if (!WwiseBankEffectPresets.EnsureStandardAttenuationDataForScope(bank, game,
                                    distanceScale, nodeId, out uint attenuationId))
                            {
                                throw new InvalidOperationException(
                                    $"The standard attenuation data could not be added for Sound 0x{nodeId:X8}.");
                            }
                            WwiseBankEffectPresets.SetStandardAttenuationOnScopes([node], attenuationId, true,
                                enableDiffraction: game == MEGame.LE2);
                        }
                    }
                }
                else
                {
                    WwiseBankEffectPresets.SetStandardAttenuationOnScopes(settingNodes, 0, false);
                }
            }

            if (createStopEvent)
            {
                EnsureStopEventInBank(bank, stopEventId, stopEventName, sounds);
            }

            CommitOpaqueMusicNodes(parameterNodes, bank.BKHD.BankGeneratorVersion);
            using var output = new MemoryStream();
            WwiseBankParser.Serialize(bank, output);
            CoreWwiseBank.WriteBankRaw(output.ToArray(), bankExport);
            if (createStopEvent)
            {
                EnsureStopEventExport(bankExport, stopEventId, stopEventName, sounds);
            }
            return true;
        }

        private static uint GetNodeId(WwiserIHasNode node) => ((WwiserHircItem)node).Id;

        private static List<(uint Id, WwiserIHasNode Node)> GetEditableParameterNodes(
            ME3Tweaks.Wwiser.WwiseBank bank)
        {
            var parameterNodes = bank.HIRC?.Items
                .Where(item => item.Item is WwiserIHasNode)
                .Select(item => (item.Item.Id, Node: (WwiserIHasNode)item.Item))
                .ToList() ?? [];
            if (bank.BKHD.BankGeneratorVersion != WwiseBankEffectPresets.BankVersion || bank.HIRC == null)
            {
                return parameterNodes;
            }

            foreach (var item in bank.HIRC.Items.Where(item =>
                         item.Item is WwiserEmptyHircItem &&
                         item.Type.Value is HircType.MusicSegment or HircType.MusicTrack or
                             HircType.MusicSwitch or HircType.MusicRandomSequence))
            {
                var sourceItem = (WwiserEmptyHircItem)item.Item;
                if (!TryGetOpaqueMusicNodeOffset(item.Type.Value, sourceItem,
                        bank.BKHD.BankGeneratorVersion, out int nodeOffset))
                {
                    continue;
                }

                try
                {
                    using var musicData = new MemoryStream(sourceItem.Data, false);
                    musicData.Position = nodeOffset;
                    var node = new BinarySerializer().Deserialize<NodeBaseParameters>(musicData,
                        new BankSerializationContext(bank.BKHD.BankGeneratorVersion));
                    parameterNodes.Add((sourceItem.Id, new EditableOpaqueMusicNode
                    {
                        Id = sourceItem.Id,
                        SourceItem = sourceItem,
                        SourceType = item.Type.Value,
                        PrefixData = sourceItem.Data[..nodeOffset],
                        NodeBaseParameters = node,
                        SuffixData = sourceItem.Data[(int)musicData.Position..]
                    }));
                }
                catch
                {
                    // Preserve music objects whose node prefix does not match the supported v134 layout.
                }
            }

            return parameterNodes;
        }

        private static bool TryGetOpaqueMusicNodeOffset(HircType type, WwiserEmptyHircItem sourceItem,
            uint version, out int nodeOffset)
        {
            nodeOffset = 0;
            if (sourceItem.Data.Length <= 1)
            {
                return false;
            }

            if (type != HircType.MusicTrack)
            {
                nodeOffset = 1; // v134 music MIDI behavior byte
                return true;
            }

            try
            {
                using var musicData = new MemoryStream(sourceItem.Data, false);
                using var reader = new BinaryReader(musicData);
                reader.ReadByte(); // MIDI behavior

                uint sourceCount = reader.ReadUInt32();
                ValidateMusicItemCount(sourceCount, musicData, 14);
                var serializer = new BinarySerializer();
                var context = new BankSerializationContext(version);
                for (uint index = 0; index < sourceCount; index++)
                {
                    serializer.Deserialize<WwiserBankSourceData>(musicData, context);
                }

                uint timeParameterCount = reader.ReadUInt32();
                ValidateMusicItemCount(timeParameterCount, musicData, 44);
                SkipMusicData(musicData, checked((long)timeParameterCount * 44));
                if (timeParameterCount > 0)
                {
                    SkipMusicData(musicData, sizeof(uint)); // sub-track count
                }

                uint curveCount = reader.ReadUInt32();
                ValidateMusicItemCount(curveCount, musicData, 12);
                for (uint index = 0; index < curveCount; index++)
                {
                    SkipMusicData(musicData, sizeof(uint) * 2); // time parameter index and curve type
                    uint pointCount = reader.ReadUInt32();
                    ValidateMusicItemCount(pointCount, musicData, 12);
                    SkipMusicData(musicData, checked((long)pointCount * 12));
                }

                nodeOffset = checked((int)musicData.Position);
                return nodeOffset < sourceItem.Data.Length;
            }
            catch
            {
                nodeOffset = 0;
                return false;
            }
        }

        private static void ValidateMusicItemCount(uint count, Stream stream, int minimumItemSize)
        {
            if (count > (stream.Length - stream.Position) / minimumItemSize)
            {
                throw new InvalidDataException("The music hierarchy item contains an invalid element count.");
            }
        }

        private static void SkipMusicData(Stream stream, long byteCount)
        {
            if (byteCount < 0 || byteCount > stream.Length - stream.Position)
            {
                throw new EndOfStreamException();
            }
            stream.Position += byteCount;
        }

        private static void CommitOpaqueMusicNodes(
            IEnumerable<(uint Id, WwiserIHasNode Node)> parameterNodes, uint version)
        {
            foreach (var musicNode in parameterNodes.Select(item => item.Node)
                         .OfType<EditableOpaqueMusicNode>())
            {
                musicNode.Commit(version);
            }
        }

        private static List<WwiserIHasNode> GetEventTargetAudioNodes(ME3Tweaks.Wwiser.WwiseBank bank, uint eventId,
            IReadOnlyCollection<(uint Id, WwiserIHasNode Node)> parameterNodes)
        {
            if (bank.HIRC?.Items.FirstOrDefault(item => item.Item.Id == eventId)?.Item is not WwiserEvent hircEvent)
            {
                return [];
            }

            var actionsById = bank.HIRC.Items.Select(item => item.Item).OfType<WwiserAction>()
                .ToDictionary(action => action.Id);
            var nodesById = parameterNodes.ToDictionary(item => item.Id, item => item.Node);
            var childIdsByParent = parameterNodes
                .Where(item => item.Node.NodeBaseParameters.DirectParentId != 0)
                .ToLookup(item => item.Node.NodeBaseParameters.DirectParentId, item => item.Id);
            var targetIds = hircEvent.ActionIds
                .Where(actionsById.ContainsKey)
                .Select(actionId => actionsById[actionId])
                .Where(action => action.Type.Value is ActionTypeValue.Play or ActionTypeValue.PlayAndContinue)
                .Select(action => action.TargetId)
                .Distinct();

            var queue = new Queue<uint>(targetIds);
            var visited = new HashSet<uint>();
            var targetNodes = new List<WwiserIHasNode>();
            while (queue.TryDequeue(out uint targetId))
            {
                if (!visited.Add(targetId) || !nodesById.TryGetValue(targetId, out var targetNode))
                {
                    continue;
                }

                var childIds = childIdsByParent[targetId].ToList();
                if (targetNode is WwiserSound || childIds.Count == 0)
                {
                    targetNodes.Add(targetNode);
                    continue;
                }

                foreach (uint childId in childIds)
                {
                    queue.Enqueue(childId);
                }
            }

            return targetNodes;
        }

        private static string GetStopEventName(string playEventName, MEGame game)
        {
            string soundName = playEventName;
            if (soundName.EndsWith("_Play", StringComparison.OrdinalIgnoreCase))
            {
                soundName = soundName[..^5];
            }
            else if (soundName.StartsWith("Play_", StringComparison.OrdinalIgnoreCase))
            {
                soundName = soundName[5..];
            }
            return WwiseEventNaming.GetPerAudioStopEventName(game, soundName);
        }

        private void WwiseBanks_ListBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is not ListBox listBox ||
                ItemsControl.ContainerFromElement(listBox, e.OriginalSource as DependencyObject) is not ListBoxItem item ||
                item.DataContext is not ExportEntry bankExport)
            {
                e.Handled = true;
                return;
            }

            item.IsSelected = true;
            CurrentExport = bankExport;
        }

        private static float GetNodeVolume(WwiserIHasNode node)
        {
            var parameters = node.NodeBaseParameters.InitialParams62;
            int volumeIndex = parameters.ParameterIds.FindIndex(id => id.PropValue == PropId.Volume);
            return volumeIndex >= 0 ? parameters.ParameterValues[volumeIndex].Value : 0;
        }

        private static float GetCommonNodeVolume(IReadOnlyCollection<WwiserIHasNode> nodes,
            out bool isMixed)
        {
            float volume = nodes.Count == 0 ? 0 : GetNodeVolume(nodes.First());
            isMixed = nodes.Any(node => Math.Abs(GetNodeVolume(node) - volume) > 0.0001f);
            return volume;
        }

        private static uint? GetCommonOutputBusId(IReadOnlyCollection<WwiserIHasNode> nodes)
        {
            if (nodes.Count == 0)
            {
                return 0;
            }

            uint outputBusId = nodes.First().NodeBaseParameters.OverrideBusId;
            return nodes.All(node => node.NodeBaseParameters.OverrideBusId == outputBusId)
                ? outputBusId
                : null;
        }

        private static string GetEffectiveOutputBusName(WwiserIHasNode node,
            IReadOnlyCollection<(uint Id, WwiserIHasNode Node)> parameterNodes, MEGame game)
        {
            var nodesById = parameterNodes.ToDictionary(item => item.Id, item => item.Node);
            var visited = new HashSet<uint>();
            WwiserIHasNode current = node;
            while (current != null)
            {
                uint outputBusId = current.NodeBaseParameters.OverrideBusId;
                if (outputBusId != 0)
                {
                    return WwiseOutputBusOptions.GetOutputBusName(game, outputBusId);
                }

                uint parentId = current.NodeBaseParameters.DirectParentId;
                if (parentId == 0 || !visited.Add(parentId) || !nodesById.TryGetValue(parentId, out current))
                {
                    return WwiseOutputBusOptions.MasterAudioBus;
                }
            }

            return WwiseOutputBusOptions.MasterAudioBus;
        }

        private static bool? GetDuckingState(ME3Tweaks.Wwiser.WwiseBank bank,
            IReadOnlyCollection<WwiserIHasNode> nodes, MEGame game)
        {
            var states = nodes.Select(node => game == MEGame.LE2
                    ? WwiseBankEffectPresets.HasLe2MusicDuckingOnAllTargets(bank, [GetNodeId(node)])
                    : WwiseBankEffectPresets.HasMusicDuckingOnAllScopes([node]))
                .ToList();
            return GetMixedBooleanState(states);
        }

        private static bool? GetAttenuationState(ME3Tweaks.Wwiser.WwiseBank bank,
            IReadOnlyCollection<WwiserIHasNode> nodes, out double distanceScalePercent)
        {
            var attenuationIds = nodes.Select(GetEnabledAttenuationId).ToList();
            var enabledStates = attenuationIds.Select(id => id.HasValue).ToList();
            bool? state = GetMixedBooleanState(enabledStates);
            distanceScalePercent = 100;

            var distinctIds = attenuationIds.Where(id => id.HasValue).Select(id => id.Value).Distinct().ToList();
            if (distinctIds.Count == 1 && bank.HIRC?.Items.FirstOrDefault(item =>
                    item.Item.Id == distinctIds[0])?.Item is WwiserAttenuation attenuation)
            {
                float maximumDistance = attenuation.Curves.SelectMany(curve => curve.Graph)
                    .Select(point => point.From)
                    .DefaultIfEmpty(WwiseBankEffectPresets.StandardAttenuationOriginalMaxDistance)
                    .Max();
                distanceScalePercent = maximumDistance /
                                       WwiseBankEffectPresets.StandardAttenuationOriginalMaxDistance * 100d;
            }

            return state;
        }

        private static uint? GetEnabledAttenuationId(WwiserIHasNode node)
        {
            var initialParams = node.NodeBaseParameters.InitialParams62;
            for (int index = 0; index < initialParams.ParameterIds.Count; index++)
            {
                if (initialParams.ParameterIds[index].PropValue != PropId.AttenuationID)
                {
                    continue;
                }

                uint attenuationId = initialParams.ParameterValues[index].StoredAsFloat
                    ? BitConverter.SingleToUInt32Bits(initialParams.ParameterValues[index].Float)
                    : initialParams.ParameterValues[index].Integer;
                if (WwiseBankEffectPresets.HasStandardAttenuationOnAllScopes([node], attenuationId))
                {
                    return attenuationId;
                }
            }

            return null;
        }

        private static bool? GetMixedBooleanState(IReadOnlyCollection<bool> states)
        {
            if (states.Count == 0 || states.All(state => !state))
            {
                return false;
            }
            return states.All(state => state) ? true : null;
        }

        private readonly record struct EffectiveEffectState(WwiserIHasNode Target, WwiserIHasNode Source);

        private static WwiseEditorEffectPreset GetCurrentEffectPreset(
            IReadOnlyCollection<WwiserIHasNode> effectScopeNodes,
            IReadOnlyCollection<(uint Id, WwiserIHasNode Node)> parameterNodes,
            MEGame game, bool isBankWide)
        {
            var effectStates = GetEffectiveEffectStates(effectScopeNodes, parameterNodes);
            var effectiveEffectNodes = effectStates
                .Select(state => state.Source ?? state.Target)
                .ToList();
            if (game == MEGame.LE2)
            {
                if (HasEffectOnAllScopes(effectiveEffectNodes, WwiseBankEffectPresets.Le2HelmetFilter) &&
                    WwiseBankEffectPresets.HasLe2HelmetRtpcOnAllScopes(effectiveEffectNodes))
                {
                    return WwiseEditorEffectPreset.Le2Helmet;
                }
                if (HasEffectOnAllScopes(effectiveEffectNodes, WwiseBankEffectPresets.Le2Radio))
                {
                    return WwiseEditorEffectPreset.Le2Radio;
                }
                if (HasEffectOnAllScopes(effectiveEffectNodes, WwiseBankEffectPresets.Le2Hologram))
                {
                    return WwiseEditorEffectPreset.Le2Hologram;
                }
            }
            else
            {
                bool helmetEffect = HasEffectOnAllScopes(effectiveEffectNodes,
                                        WwiseBankEffectPresets.HelmetFilter) &&
                                    WwiseBankEffectPresets.HasHelmetRtpcOnAllScopes(effectiveEffectNodes);
                if (helmetEffect)
                {
                    return WwiseEditorEffectPreset.Helmet;
                }
                if (HasEffectOnAllScopes(effectiveEffectNodes, WwiseBankEffectPresets.BioWareRadio))
                {
                    return WwiseEditorEffectPreset.BioWareRadio;
                }
                if (HasEffectOnAllScopes(effectiveEffectNodes, WwiseBankEffectPresets.HackettQec))
                {
                    return WwiseEditorEffectPreset.Qec;
                }
            }

            if (HasEffectOnAllScopes(effectiveEffectNodes, WwiseBankEffectPresets.FactoryRadio))
            {
                return WwiseEditorEffectPreset.FactoryRadio;
            }

            if (effectStates.All(state => state.Source?.NodeBaseParameters.FxParams.FxChunks.Count is null or 0))
            {
                bool allExplicitlyEmpty = effectStates.All(state =>
                    ReferenceEquals(state.Source, state.Target) &&
                    state.Target.NodeBaseParameters.FxParams.IsOverrideParentFx);
                return isBankWide || allExplicitlyEmpty
                    ? WwiseEditorEffectPreset.None
                    : WwiseEditorEffectPreset.Inherit;
            }

            return WwiseEditorEffectPreset.Preserve;
        }

        private static List<EffectiveEffectState> GetEffectiveEffectStates(
            IReadOnlyCollection<WwiserIHasNode> effectScopeNodes,
            IReadOnlyCollection<(uint Id, WwiserIHasNode Node)> parameterNodes)
        {
            var nodesById = parameterNodes
                .GroupBy(item => item.Id)
                .ToDictionary(group => group.Key, group => group.First().Node);
            var states = new List<EffectiveEffectState>();
            foreach (WwiserIHasNode target in effectScopeNodes)
            {
                WwiserIHasNode current = target;
                WwiserIHasNode source = null;
                var visited = new HashSet<uint>();
                while (current != null)
                {
                    var effects = current.NodeBaseParameters.FxParams;
                    if (effects.IsOverrideParentFx || effects.FxChunks.Count > 0)
                    {
                        source = current;
                        break;
                    }

                    uint parentId = current.NodeBaseParameters.DirectParentId;
                    if (parentId == 0 || !visited.Add(parentId) ||
                        !nodesById.TryGetValue(parentId, out current))
                    {
                        break;
                    }
                }

                states.Add(new EffectiveEffectState(target, source));
            }
            return states;
        }

        private static string GetCurrentEffectSummary(
            IReadOnlyCollection<WwiserIHasNode> effectScopeNodes,
            IReadOnlyCollection<(uint Id, WwiserIHasNode Node)> parameterNodes, MEGame game)
        {
            var descriptions = GetEffectiveEffectStates(effectScopeNodes, parameterNodes)
                .Select(state =>
                {
                    if (state.Source == null || state.Source.NodeBaseParameters.FxParams.FxChunks.Count == 0)
                    {
                        return state.Source != null ? "None (explicit override)" : "None";
                    }

                    uint[] effectIds = state.Source.NodeBaseParameters.FxParams.FxChunks
                        .OrderBy(chunk => chunk.FxIndex)
                        .Select(chunk => chunk.Id)
                        .ToArray();
                    string knownName = GetKnownEffectChainName(effectIds, game);
                    string idList = string.Join(" + ", effectIds.Select(id => $"0x{id:X8}"));
                    string inherited = ReferenceEquals(state.Source, state.Target) ? string.Empty : " (inherited)";
                    return string.IsNullOrEmpty(knownName)
                        ? $"{idList}{inherited}"
                        : $"{knownName} [{idList}]{inherited}";
                })
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return descriptions.Count switch
            {
                0 => "None",
                1 => descriptions[0],
                _ => $"Mixed: {string.Join("; ", descriptions)}"
            };
        }

        private static string GetKnownEffectChainName(IReadOnlyList<uint> effectIds, MEGame game)
        {
            var candidates = game == MEGame.LE2
                ? new[]
                {
                    WwiseEditorEffectPreset.Le2Radio, WwiseEditorEffectPreset.Le2Helmet,
                    WwiseEditorEffectPreset.Le2Hologram, WwiseEditorEffectPreset.FactoryRadio
                }
                : new[]
                {
                    WwiseEditorEffectPreset.BioWareRadio, WwiseEditorEffectPreset.Qec,
                    WwiseEditorEffectPreset.Helmet, WwiseEditorEffectPreset.FactoryRadio
                };
            foreach (WwiseEditorEffectPreset candidate in candidates)
            {
                var (name, chain, _) = GetEffectDefinition(candidate);
                if (effectIds.SequenceEqual(chain.Select(effect => effect.Id)))
                {
                    return name;
                }
            }
            return null;
        }

        private static (string Name, IReadOnlyList<WwiseBankEffect> Chain, bool HelmetRtpc)
            GetEffectDefinition(WwiseEditorEffectPreset preset) => preset switch
            {
                WwiseEditorEffectPreset.FactoryRadio =>
                    ("Dual_Filters_Radio_Comm", WwiseBankEffectPresets.FactoryRadio, false),
                WwiseEditorEffectPreset.BioWareRadio =>
                    ("BioWare radio", WwiseBankEffectPresets.BioWareRadio, false),
                WwiseEditorEffectPreset.Qec =>
                    ("Hackett QEC", WwiseBankEffectPresets.HackettQec, false),
                WwiseEditorEffectPreset.Helmet =>
                    ("helmet voice", WwiseBankEffectPresets.HelmetFilter, true),
                WwiseEditorEffectPreset.Le2Radio =>
                    ("LE2 radio", WwiseBankEffectPresets.Le2Radio, false),
                WwiseEditorEffectPreset.Le2Helmet =>
                    ("LE2 helmet voice", WwiseBankEffectPresets.Le2HelmetFilter, true),
                WwiseEditorEffectPreset.Le2Hologram =>
                    ("Illusive Man hologram", WwiseBankEffectPresets.Le2Hologram, false),
                _ => throw new InvalidOperationException($"{preset} does not identify an effect preset.")
            };

        private static void ApplyEffectPresetToScopes(IEnumerable<WwiserIHasNode> effectScopeNodes,
            WwiseEditorEffectPreset preset, MEGame game,
            IEnumerable<WwiserIHasNode> additionalCleanupNodes = null)
        {
            var scopes = effectScopeNodes.Distinct().ToList();
            if (preset == WwiseEditorEffectPreset.Inherit)
            {
                ClearEffectsOnScopes(scopes, false);
                WwiseBankEffectPresets.SetHelmetRtpcOnScopes(scopes, false);
                WwiseBankEffectPresets.SetLe2HelmetRtpcOnScopes(scopes, false);
                return;
            }

            if (preset == WwiseEditorEffectPreset.None)
            {
                var allBankNodes = additionalCleanupNodes?.Distinct().ToList();
                if (allBankNodes != null)
                {
                    ClearEffectsOnScopes(allBankNodes, false);
                    WwiseBankEffectPresets.SetHelmetRtpcOnScopes(allBankNodes, false);
                    WwiseBankEffectPresets.SetLe2HelmetRtpcOnScopes(allBankNodes, false);
                }
                ClearEffectsOnScopes(scopes, true);
                WwiseBankEffectPresets.SetHelmetRtpcOnScopes(scopes, false);
                WwiseBankEffectPresets.SetLe2HelmetRtpcOnScopes(scopes, false);
                return;
            }

            // Clean up root-level effects written by older WwiseEditor builds before applying the
            // replacement to the runtime-effective BioWare branch scopes.
            var cleanupScopes = scopes.Concat(additionalCleanupNodes ?? []).Distinct().ToList();
            foreach (var effectChain in GetKnownEffectChains())
            {
                SetEffectOnScopes(cleanupScopes, effectChain, false);
            }
            WwiseBankEffectPresets.SetHelmetRtpcOnScopes(cleanupScopes, false);
            WwiseBankEffectPresets.SetLe2HelmetRtpcOnScopes(cleanupScopes, false);

            var (_, selectedEffectChain, helmetRtpc) = GetEffectDefinition(preset);
            SetEffectOnScopes(scopes, selectedEffectChain, true);
            if (helmetRtpc)
            {
                if (game == MEGame.LE2)
                {
                    WwiseBankEffectPresets.SetLe2HelmetRtpcOnScopes(scopes, true);
                }
                else
                {
                    WwiseBankEffectPresets.SetHelmetRtpcOnScopes(scopes, true);
                }
            }
        }

        private static void ClearEffectsOnScopes(IEnumerable<WwiserIHasNode> effectScopeNodes,
            bool overrideParent)
        {
            foreach (WwiserIHasNode node in effectScopeNodes.Distinct())
            {
                var effects = node.NodeBaseParameters.FxParams;
                effects.FxChunks.Clear();
                effects.NumFx = 0;
                effects.BitsFxBypass = 0;
                effects.IsOverrideParentFx = overrideParent;
            }
        }

        private static IEnumerable<IReadOnlyList<WwiseBankEffect>> GetKnownEffectChains()
        {
            yield return WwiseBankEffectPresets.FactoryRadio;
            yield return WwiseBankEffectPresets.BioWareRadio;
            yield return WwiseBankEffectPresets.HackettQec;
            yield return WwiseBankEffectPresets.HelmetFilter;
            yield return WwiseBankEffectPresets.Le2Radio;
            yield return WwiseBankEffectPresets.Le2HelmetFilter;
            yield return WwiseBankEffectPresets.Le2Hologram;
        }

        private static bool? GetLoopAudioState(List<WwiserSound> sounds)
        {
            if (sounds.Count == 0)
            {
                return false;
            }

            int loopingSounds = sounds.Count(IsLooping);
            return loopingSounds switch
            {
                0 => false,
                var count when count == sounds.Count => true,
                _ => null
            };
        }

        private static bool IsLooping(WwiserSound sound) =>
            sound.NodeBaseParameters.InitialParams62.ParameterIds.Any(id => id.PropValue == PropId.Loop);

        private static List<WwiserIHasNode> GetRootNodes(
            IReadOnlyCollection<(uint Id, WwiserIHasNode Node)> parameterNodes)
        {
            var parameterNodeIds = parameterNodes.Select(item => item.Id).ToHashSet();
            return parameterNodes
                .Where(item => item.Node.NodeBaseParameters.DirectParentId == 0 ||
                               !parameterNodeIds.Contains(item.Node.NodeBaseParameters.DirectParentId))
                .Select(item => item.Node)
                .Distinct()
                .ToList();
        }

        private static List<WwiserIHasNode> GetRuntimeOverrideNodes(
            IReadOnlyCollection<(uint Id, WwiserIHasNode Node)> parameterNodes)
        {
            var parentNodeIds = parameterNodes
                .Select(item => item.Node.NodeBaseParameters.DirectParentId)
                .ToHashSet();
            // BioWare reuses its roots and Actor-Mixer hierarchy IDs in multiple banks. Those
            // shared objects can be replaced when another bank is loaded. Leaf Sound/music IDs
            // are bank-specific, so an explicit FX override there cannot be masked by any of the
            // inherited buses, mixers, or duplicate parent definitions.
            return parameterNodes
                .Where(item => item.Node is WwiserSound || !parentNodeIds.Contains(item.Id))
                .Select(item => item.Node)
                .Distinct()
                .ToList();
        }

        private static List<WwiserIHasNode> GetEventEffectScopeNodes(
            IReadOnlyCollection<WwiserIHasNode> targetNodes)
        {
            // GetEventTargetAudioNodes already resolves each Play action through the hierarchy to
            // its terminal Sound/music nodes. Override FX on those unique leaves so every inherited
            // BioWare bus and Actor-Mixer definition is superseded for this event.
            return targetNodes.Distinct().ToList();
        }

        private static void SetLoopAudio(WwiserSound sound, bool enabled)
        {
            var parameters = sound.NodeBaseParameters.InitialParams62;
            if (enabled)
            {
                SetInitialParameter(parameters, PropId.Loop, 0, false);
            }
            else
            {
                RemoveInitialParameter(parameters, PropId.Loop);
            }
        }

        private static bool HasEffectOnAllScopes(IReadOnlyCollection<WwiserIHasNode> effectScopeNodes,
            IReadOnlyList<WwiseBankEffect> effectChain)
        {
            if (effectScopeNodes.Count == 0)
            {
                return false;
            }

            var effectIds = effectChain.Select(effect => effect.Id).ToArray();
            return effectScopeNodes.All(node =>
            {
                var appliedIds = node.NodeBaseParameters.FxParams.FxChunks
                    .Where(chunk => effectIds.Contains(chunk.Id))
                    .OrderBy(chunk => chunk.FxIndex)
                    .Select(chunk => chunk.Id)
                    .ToArray();
                return appliedIds.SequenceEqual(effectIds);
            });
        }

        private static bool CanApplyEffect(ME3Tweaks.Wwiser.WwiseBank bank,
            IReadOnlyCollection<WwiserIHasNode> effectScopeNodes, IReadOnlyList<WwiseBankEffect> effectChain)
        {
            if (!WwiseBankEffectPresets.CanEnsureEffectData(bank, effectChain))
            {
                return false;
            }

            var replaceableEffectIds = GetKnownEffectChains()
                .SelectMany(chain => chain)
                .Select(effect => effect.Id)
                .ToHashSet();
            return effectScopeNodes.All(node =>
                node.NodeBaseParameters.FxParams.FxChunks.Count(chunk => !replaceableEffectIds.Contains(chunk.Id)) +
                effectChain.Count <= MaximumEffectSlots);
        }

        private static void SetEffectOnScopes(IEnumerable<WwiserIHasNode> effectScopeNodes,
            IReadOnlyList<WwiseBankEffect> effectChain, bool enabled)
        {
            var effectIds = effectChain.Select(effect => effect.Id).ToHashSet();
            foreach (var node in effectScopeNodes)
            {
                var effects = node.NodeBaseParameters.FxParams;
                effects.FxChunks.RemoveAll(chunk => effectIds.Contains(chunk.Id));

                if (enabled)
                {
                    var usedSlots = effects.FxChunks.Select(chunk => (int)chunk.FxIndex).ToHashSet();
                    var availableSlots = Enumerable.Range(0, MaximumEffectSlots)
                        .Where(slot => !usedSlots.Contains(slot))
                        .Take(effectChain.Count)
                        .ToArray();
                    if (availableSlots.Length != effectChain.Count)
                    {
                        throw new InvalidOperationException(
                            "This bank has no room for the selected effect chain. Wwise supports four effect slots per audio node.");
                    }

                    for (int i = 0; i < effectChain.Count; i++)
                    {
                        effects.FxChunks.Add(new FxChunk
                        {
                            FxIndex = checked((byte)availableSlots[i]),
                            Id = effectChain[i].Id,
                            IsShareSet = true
                        });
                    }
                    effects.BitsFxBypass = 0;
                    effects.IsOverrideParentFx = true;
                }
                else if (effects.FxChunks.Count == 0)
                {
                    effects.IsOverrideParentFx = false;
                }

                effects.FxChunks.Sort((left, right) => left.FxIndex.CompareTo(right.FxIndex));
                effects.NumFx = checked((byte)effects.FxChunks.Count);
            }
        }

        private static void SetEffectPresetOnScopes(IEnumerable<WwiserIHasNode> effectScopeNodes,
            IReadOnlyList<WwiseBankEffect> selectedEffectChain)
        {
            var scopes = effectScopeNodes.ToList();
            WwiseBankEffectPresets.SetHelmetRtpcOnScopes(scopes, false);
            SetEffectOnScopes(scopes, WwiseBankEffectPresets.FactoryRadio, false);
            SetEffectOnScopes(scopes, WwiseBankEffectPresets.BioWareRadio, false);
            SetEffectOnScopes(scopes, WwiseBankEffectPresets.HackettQec, false);
            SetEffectOnScopes(scopes, WwiseBankEffectPresets.HelmetFilter, false);
            SetEffectOnScopes(scopes, selectedEffectChain, true);
            if (selectedEffectChain.Select(effect => effect.Id)
                .SequenceEqual(WwiseBankEffectPresets.HelmetFilter.Select(effect => effect.Id)))
            {
                WwiseBankEffectPresets.SetHelmetRtpcOnScopes(scopes, true);
            }
        }

        private static bool HasCompleteStopAllEvent(ME3Tweaks.Wwiser.WwiseBank bank,
            ExportEntry bankExport, IReadOnlyCollection<WwiserSound> sounds) =>
            HasCompleteStopEvent(bank, bankExport, sounds, StopAllEventId);

        private static bool HasCompleteStopEvent(ME3Tweaks.Wwiser.WwiseBank bank,
            ExportEntry bankExport, IReadOnlyCollection<WwiserSound> sounds, uint stopEventId)
        {
            if (bank.HIRC == null || sounds.Count == 0 ||
                bank.HIRC.Items.FirstOrDefault(item => item.Item.Id == stopEventId)?.Item is not WwiserEvent stopEvent)
            {
                return false;
            }

            var actionsById = bank.HIRC.Items
                .Select(item => item.Item)
                .OfType<WwiserAction>()
                .ToDictionary(action => action.Id);
            var stoppedSoundIds = stopEvent.ActionIds
                .Where(actionsById.ContainsKey)
                .Select(actionId => actionsById[actionId])
                .Where(action => action.Type.Value == ActionTypeValue.Stop)
                .Select(action => action.TargetId)
                .ToHashSet();
            return sounds.All(sound => stoppedSoundIds.Contains(sound.Id)) &&
                   HasStopEventExport(bankExport, stopEventId);
        }

        private static bool CanCreateStopAllEvent(ME3Tweaks.Wwiser.WwiseBank bank,
            ExportEntry bankExport, IReadOnlyCollection<WwiserSound> sounds) =>
            CanCreateStopEvent(bank, bankExport, sounds, StopAllEventId, "Stop");

        private static bool CanCreateStopEvent(ME3Tweaks.Wwiser.WwiseBank bank,
            ExportEntry bankExport, IReadOnlyCollection<WwiserSound> sounds, uint stopEventId, string stopEventName)
        {
            if (bank.HIRC == null || bank.BKHD.BankGeneratorVersion != WwiseBankEffectPresets.BankVersion ||
                sounds.Count == 0)
            {
                return false;
            }

            var existingHirc = bank.HIRC.Items.FirstOrDefault(item => item.Item.Id == stopEventId);
            if (existingHirc != null && existingHirc.Item is not WwiserEvent)
            {
                return false;
            }

            var existingExport = bankExport.FileRef.Exports.FirstOrDefault(export =>
                export.Parent == bankExport.Parent && export.ObjectNameString == stopEventName);
            return existingExport == null || existingExport.ClassName == "WwiseEvent" &&
                   WExport.GetExportId(existingExport) == stopEventId;
        }

        private static void EnsureStopAllEventInBank(ME3Tweaks.Wwiser.WwiseBank bank,
            IReadOnlyCollection<WwiserSound> sounds) =>
            EnsureStopEventInBank(bank, StopAllEventId, "Stop", sounds);

        private static void EnsureStopEventInBank(ME3Tweaks.Wwiser.WwiseBank bank, uint stopEventId,
            string stopEventName, IReadOnlyCollection<WwiserSound> sounds)
        {
            if (bank.HIRC == null)
            {
                throw new InvalidOperationException("The bank has no audio hierarchy.");
            }

            var usedIds = bank.HIRC.Items.Select(item => item.Item.Id).ToHashSet();
            var stopEventItem = bank.HIRC.Items.FirstOrDefault(item => item.Item.Id == stopEventId);
            WwiserEvent stopEvent;
            bool addStopEvent = false;
            if (stopEventItem == null)
            {
                stopEvent = new WwiserEvent
                {
                    Id = stopEventId,
                    ActionCount = new VarCount(),
                    ActionIds = []
                };
                stopEventItem = new WwiserHircItemContainer
                {
                    Type = new HircSmartType { Value = HircType.Event },
                    Item = stopEvent
                };
                addStopEvent = true;
                usedIds.Add(stopEventId);
            }
            else if (stopEventItem.Item is WwiserEvent existingStopEvent)
            {
                stopEvent = existingStopEvent;
            }
            else
            {
                throw new InvalidOperationException($"HIRC ID {stopEventId} is already used by another object.");
            }

            var actionsById = bank.HIRC.Items
                .Select(item => item.Item)
                .OfType<WwiserAction>()
                .ToDictionary(action => action.Id);
            var stoppedSoundIds = stopEvent.ActionIds
                .Where(actionsById.ContainsKey)
                .Select(actionId => actionsById[actionId])
                .Where(action => action.Type.Value == ActionTypeValue.Stop)
                .Select(action => action.TargetId)
                .ToHashSet();

            foreach (var sound in sounds.Where(sound => !stoppedSoundIds.Contains(sound.Id)))
            {
                uint actionId = GenerateUniqueHircId($"{stopEventName}_{sound.Id}_StopAction", usedIds);
                var stopAction = new WwiserAction
                {
                    Id = actionId,
                    Type = new ActionType
                    {
                        Value = ActionTypeValue.Stop,
                        Data = ActionFlagsUnk.Unk4
                    },
                    TargetId = sound.Id,
                    IsBus = false,
                    PropBundle = new InitialParamsV62(),
                    ActionParams = new Active
                    {
                        CurveInterpolation = CurveInterpolation.Linear,
                        SpecificParams = new WwiserPauseResume
                        {
                            Flags = new WwiserActiveFlags
                            {
                                ApplyToStateTransitions = true,
                                ApplyToDynamicSequence = true
                            }
                        },
                        ExceptParams = new ExceptParams()
                    }
                };
                bank.HIRC.Items.Add(new WwiserHircItemContainer
                {
                    Type = new HircSmartType { Value = HircType.Action },
                    Item = stopAction
                });
                stopEvent.ActionIds.Add(actionId);
            }

            stopEvent.ActionCount.Value = checked((uint)stopEvent.ActionIds.Count);
            if (addStopEvent)
            {
                bank.HIRC.Items.Add(stopEventItem);
            }
            bank.HIRC.ItemCount = checked((uint)bank.HIRC.Items.Count);
        }

        private static bool HasStopAllEventExport(ExportEntry bankExport) =>
            HasStopEventExport(bankExport, StopAllEventId);

        private static bool HasStopEventExport(ExportEntry bankExport, uint stopEventId) =>
            bankExport.FileRef.Exports.Any(export => export.ClassName == "WwiseEvent" &&
                export.Parent == bankExport.Parent &&
                WExport.GetExportId(export) == stopEventId && EventReferencesBank(export, bankExport));

        private static bool EventReferencesBank(ExportEntry eventExport, ExportEntry bankExport)
        {
            if (eventExport.FileRef.Game == MEGame.LE2)
            {
                return eventExport.GetProperty<ArrayProperty<StructProperty>>("References")?.Any(reference =>
                    reference.Properties.GetProp<StructProperty>("Relationships")?.Properties
                        .GetProp<ObjectProperty>("Bank")?.Value == bankExport.UIndex) == true;
            }

            return eventExport.GetProperty<StructProperty>("Relationships")?.Properties
                .GetProp<ObjectProperty>("Bank")?.Value == bankExport.UIndex;
        }

        private static void EnsureStopAllEventExport(ExportEntry bankExport,
            IReadOnlyCollection<WwiserSound> sounds) =>
            EnsureStopEventExport(bankExport, StopAllEventId, "Stop", sounds);

        private static void EnsureStopEventExport(ExportEntry bankExport, uint stopEventId,
            string stopEventName, IReadOnlyCollection<WwiserSound> sounds)
        {
            var package = bankExport.FileRef;
            var eventExport = package.Exports.FirstOrDefault(export => export.ClassName == "WwiseEvent" &&
                                  export.Parent == bankExport.Parent &&
                                  WExport.GetExportId(export) == stopEventId)
                              ?? package.FindExport($"{bankExport.Parent.InstancedFullPath}.{stopEventName}", "WwiseEvent")
                              ?? ExportCreator.CreateExport(package, stopEventName, "WwiseEvent", bankExport.Parent,
                                  indexed: false);

            var sourceIds = sounds.Select(sound => sound.BankSourceData.MediaInformation.SourceId).ToHashSet();
            var streamIndexes = package.Exports
                .Where(export => export.ClassName == "WwiseStream" &&
                                 sourceIds.Contains(unchecked((uint)(export.GetProperty<IntProperty>("Id")?.Value ?? 0))))
                .Select(export => export.UIndex)
                .ToList();

            var properties = eventExport.GetProperties();
            var eventBinary = CoreWwiseEvent.Create();
            if (package.Game == MEGame.LE2)
            {
                var references = new ArrayProperty<StructProperty>("References");
                var relationshipProperties = new PropertyCollection
                {
                    new ArrayProperty<ObjectProperty>(streamIndexes.Select(index => new ObjectProperty(index)),
                        "Streams"),
                    new ObjectProperty(bankExport, "Bank")
                };
                var platformProperties = new PropertyCollection
                {
                    new StructProperty("WwiseRelationships", relationshipProperties, "Relationships"),
                    new IntProperty(1, "Platform")
                };
                references.Add(new StructProperty("WwisePlatformRelationships", platformProperties));
                properties.AddOrReplaceProp(references);
                eventBinary.WwiseEventID = stopEventId;
            }
            else
            {
                properties.AddOrReplaceProp(new StructProperty("WwiseRelationships", false,
                    new ObjectProperty(bankExport, "Bank")) { Name = "Relationships" });
                properties.AddOrReplaceProp(new IntProperty(unchecked((int)stopEventId), "Id"));
                if (bankExport.GetProperty<BoolProperty>("IsLocalised")?.Value == true)
                {
                    properties.AddOrReplaceProp(new BoolProperty(true, "IsLocalised"));
                }
                eventBinary.Links[0].WwiseStreams = streamIndexes;
            }
            eventExport.WritePropertiesAndBinary(properties, eventBinary);
        }

        private static uint GenerateUniqueHircId(string name, HashSet<uint> usedIds)
        {
            uint id = 2166136261;
            foreach (char character in name.ToLowerInvariant())
            {
                id *= 16777619;
                id ^= character;
            }

            while (!usedIds.Add(id))
            {
                id++;
            }
            return id;
        }

        private static void SetInitialParameter(InitialParamsV62 parameters, PropId id, float value, bool storedAsFloat)
        {
            int parameterIndex = parameters.ParameterIds.FindIndex(parameter => parameter.PropValue == id);
            var parameterValue = parameterIndex >= 0
                ? parameters.ParameterValues[parameterIndex]
                : new InitialParamsV62.ParameterValue();
            parameterValue.Float = storedAsFloat ? value : 0;
            parameterValue.Integer = storedAsFloat ? 0 : (uint)value;
            parameterValue.StoredAsFloat = storedAsFloat;

            if (parameterIndex < 0)
            {
                parameters.AddParameter(id, parameterValue);
                parameters.ParamLength = checked((byte)parameters.ParameterIds.Count);
            }
        }

        private static void RemoveInitialParameter(InitialParamsV62 parameters, PropId id)
        {
            int parameterIndex = parameters.ParameterIds.FindIndex(parameter => parameter.PropValue == id);
            if (parameterIndex < 0)
            {
                return;
            }

            parameters.ParameterIds.RemoveAt(parameterIndex);
            parameters.ParameterValues.RemoveAt(parameterIndex);
            parameters.ParamLength = checked((byte)parameters.ParameterIds.Count);
        }

        public void LoadFile(string s, int goToIndex = 0)
        {
            try
            {
                Properties_InterpreterWPF.UnloadExport();
                binaryInterpreter.UnloadExport();
                soundPanel.FreeAudioResources();
                SelectedNode = null;

                StatusBar_LeftMostText.Text =
                    $"Loading {Path.GetFileName(s)} ({FileSize.FormatSize(new FileInfo(s).Length)})";
                Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ContextIdle, null);
                LoadMEPackage(s);
                CurrentFile = Path.GetFileName(s);

                graphEditor.nodeLayer.RemoveAllChildren();
                graphEditor.edgeLayer.RemoveAllChildren();

                WwiseBankExports.ReplaceAll(Pcc.Exports.Where(exp => exp.ClassName == "WwiseBank"));

                if (WwiseBankExports.IsEmpty())
                {
                    UnLoadMEPackage();
                    MessageBox.Show(this, "This file does not contain any WwiseBanks!");
                    StatusText = "Select a package file to load";
                    Title = "Wwise Editor";
                    CurrentFile = null;
                    soundPanelColumn.Width = GridLength.Auto;
                    return;
                }

                StatusBar_LeftMostText.Text = Path.GetFileName(s);
                Title = $"Wwise Editor - {s}";

                RecentsController.AddRecent(s, false, Pcc?.Game);
                RecentsController.SaveRecentList(true);
                if (goToIndex != 0)
                {
                    CurrentExport = WwiseBankExports.FirstOrDefault(x => x.UIndex == goToIndex);
                    ExportQueuedForFocusing = CurrentExport;
                }
                else
                {
                    CurrentExport = null;
                }

                soundPanelColumn.Width = new GridLength(425);
            }
            catch (Exception e)
            {
                StatusBar_LeftMostText.Text = "Failed to load " + Path.GetFileName(s);
                MessageBox.Show($"Error loading {Path.GetFileName(s)}:\n{e.Message}");
                UnLoadMEPackage();
                Title = "Wwise Editor";
                CurrentFile = null;
                soundPanelColumn.Width = GridLength.Auto;
            }
        }

        public void LoadBank(ExportEntry export, bool fromFile = false)
        {
            if (export == null)
            {
                return;
            }
            graphEditor.Enabled = false;
            graphEditor.UseWaitCursor = true;

            CurrentWwiseBank = export.GetBinaryData<WwiseBankParsed>();
            SetupJSON(export);
            Properties_InterpreterWPF.LoadExport(export);
            binaryInterpreter.LoadExport(export);
            soundPanel.LoadExport(export);
            soundPanel.SetHircEventPreviews(BuildHircEventPreviews(
                export.FileRef.Exports, TLKManagerWPF.GlobalFindStrRefbyID));

            if (fromFile)
            {
                if (File.Exists(JSONpath))
                {
                    SavedPositions = JsonConvert.DeserializeObject<List<SaveData>>(File.ReadAllText(JSONpath));
                }
                else
                {
                    SavedPositions = new List<SaveData>();
                }
            }
            try
            {
                GenerateGraph();
            }
            catch (Exception e) when (!App.IsDebug)
            {
                MessageBox.Show(this, $"Error loading WwiseBank:\n{e.Message}");
            }
            graphEditor.Enabled = true;
            graphEditor.UseWaitCursor = false;
        }

        private void GenerateGraph()
        {
            graphEditor.nodeLayer.RemoveAllChildren();
            graphEditor.edgeLayer.RemoveAllChildren();
            GetObjects(CurrentWwiseBank);
            Layout();
            foreach (var o in CurrentObjects)
            {
                o.MouseDown += Node_MouseDown;
            }

            // HIRC node dimensions and relationships depend on the selected bank (including
            // semantic details and TLK previews). Always recalculate the layout when a bank is
            // loaded so stale saved coordinates cannot stack expanded nodes on top of each other.
            AutoLayout();
        }

        private void GetObjects(WwiseBankParsed bank)
        {
            IReadOnlyDictionary<uint, WwiseHircSemanticInfo> semanticInfoById =
                new Dictionary<uint, WwiseHircSemanticInfo>();
            try
            {
                using var input = new MemoryStream(bank.BnkFile, false);
                var wwiserBank = WwiseBankParser.Deserialize(input);
                semanticInfoById = WwiseHircSemanticFormatter.BuildInfoById(wwiserBank.HIRC?.Items ?? [],
                    CurrentExport?.Game);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Wwise Editor: Could not decode semantic HIRC information: {exception.Message}");
            }

            var displayInfoById = soundPanel.HIRCObjects
                .GroupBy(hirc => hirc.ID)
                .ToDictionary(group => group.Key, group => group.First());
            var newObjs = new List<WwiseHircObjNode>();
            foreach ((uint id, WwiseBankParsed.HIRCObject hircObject) in CurrentWwiseBank.HIRCObjects)
            {
                WwiseHircSemanticInfo? semanticInfo = semanticInfoById.TryGetValue(id, out var info)
                    ? info
                    : null;
                if (displayInfoById.TryGetValue(id, out var displayInfo))
                {
                    semanticInfo = semanticInfo is { } parsedInfo
                        ? parsedInfo with { EventPreview = displayInfo.EventPreview }
                        : new WwiseHircSemanticInfo(displayInfo.SemanticTypeName,
                            displayInfo.SemanticDescription, ParentId: displayInfo.DirectParentID == 0
                                ? null
                                : displayInfo.DirectParentID, EventPreview: displayInfo.EventPreview);
                }

                newObjs.Add(hircObject switch
                {
                    WwiseBankParsed.Event evt => new WEvent(evt, 0, 0, graphEditor, semanticInfo),
                    WwiseBankParsed.EventAction evtAct => new WEventAction(evtAct, 0, 0, graphEditor,
                        semanticInfo),
                    WwiseBankParsed.SoundSFXVoice sfxvoice => new WSoundSFXVoice(sfxvoice, 0, 0,
                        graphEditor, semanticInfo),
                    _ => new WGeneric(hircObject, 0, 0, graphEditor, semanticInfo)
                });
            }

            CurrentObjects.ReplaceAll(newObjs);
        }

        internal static IReadOnlyDictionary<uint, string> BuildHircEventPreviews(
            IEnumerable<ExportEntry> exports, Func<int, IMEPackage, string> resolveTlk)
        {
            return exports
                .Where(export => export.ClassName == "WwiseEvent")
                .GroupBy(WExport.GetExportId)
                .ToDictionary(group => group.Key,
                    group => string.Join(Environment.NewLine,
                        group.Select(export => WExport.BuildDisplayValue(export, resolveTlk))));
        }

        public void Layout()
        {
            if (CurrentObjects != null && CurrentObjects.Any())
            {
                var wwiseEvents = new Dictionary<uint, List<ExportEntry>>();
                var wwiseStreams = new Dictionary<uint, ExportEntry>();
                foreach (ExportEntry exportEntry in Pcc.Exports)
                {
                    switch (exportEntry.ClassName)
                    {
                        case "WwiseEvent":
                            wwiseEvents.AddToListAt(WExport.GetExportId(exportEntry), exportEntry);
                            break;
                        case "WwiseStream":
                            wwiseStreams.TryAdd((exportEntry.GetProperty<IntProperty>("Id")?.Value ?? 0).ReinterpretAsUint(), exportEntry);
                            break;
                    }
                }
                var referencedExports = new Dictionary<uint, List<WExport>>();
                foreach (var obj in CurrentObjects)
                {
                    graphEditor.AddNode(obj);
                    switch (obj)
                    {
                        case WEvent wEvent:
                        {
                            if (!referencedExports.TryGetValue(wEvent.ID, out List<WExport> wExports))
                            {
                                if (!wwiseEvents.TryGetValue(wEvent.ID, out List<ExportEntry> wwiseEventExports))
                                {
                                    continue;
                                }

                                wExports = new List<WExport>();
                                foreach (var wwiseEventExp in wwiseEventExports)
                                {
                                    var wExp = new WExport(wwiseEventExp, 0, 0, graphEditor);
                                    wExports.Add(wExp);
                                    referencedExports.AddToListAt(wEvent.ID, wExp);
                                    graphEditor.AddNode(wExp);
                                }
                            }
                            obj.Varlinks[0].Links.AddRange(wExports.Select(x => (uint)x.Export.UIndex));
                            break;
                        }
                        case WSoundSFXVoice wSound:
                        {
                            if (!referencedExports.TryGetValue(wSound.SoundSFXVoice.AudioID, out List<WExport> wExports))
                            {
                                if (!wwiseStreams.TryGetValue(wSound.SoundSFXVoice.AudioID, out ExportEntry wwiseSoundExport))
                                {
                                    continue;
                                }

                                wExports = new List<WExport>();
                                var wExp = new WExport(wwiseSoundExport, 0, 0, graphEditor);
                                wExports.Add(wExp);
                                referencedExports.AddToListAt(wSound.SoundSFXVoice.AudioID, wExp);
                                graphEditor.AddNode(wExp);
                            }
                            obj.Varlinks[0].Links.Clear();
                            obj.Varlinks[0].Links.AddRange(wExports.Select(x => (uint)x.Export.UIndex));
                            break;
                        }
                    }
                }
                CurrentObjects.AddRange(referencedExports.Values.SelectMany(vals => vals));
                foreach (var obj in CurrentObjects)
                {
                    obj.CreateConnections(CurrentObjects);
                }

                foreach (WwiseHircObjNode obj in CurrentObjects)
                {
                    SaveData savedInfo = default;
                    uint id = obj is WExport wExp ? wExp.Export.UIndex.ReinterpretAsUint() : obj.ID;
                    if (SavedPositions.Any())
                    {
                        savedInfo = SavedPositions.FirstOrDefault(p => id == p.ID);
                    }

                    bool hasSavedPosition = savedInfo.ID == id;
                    if (hasSavedPosition)
                    {
                        obj.Layout(savedInfo.X, savedInfo.Y);
                    }
                    else
                    {
                        obj.Layout();
                    }
                }

                foreach (WwiseEdEdge edge in graphEditor.edgeLayer)
                {
                    WwiseGraphEditor.UpdateEdge(edge);
                }
            }
        }

        private void AutoLayout()
        {
            foreach (WwiseHircObjNode obj in CurrentObjects)
            {
                obj.SetOffset(0, 0); //remove existing positioning
            }

            const float HORIZONTAL_SPACING = 80;
            const float VERTICAL_SPACING = 100;
            const float ATTACHMENT_SPACING = 20;
            var eventNodes = CurrentObjects.OfType<WEvent>().ToList();
            var exportNodeLookup = CurrentObjects.OfType<WExport>()
                .GroupBy(node => node.Export.UIndex)
                .ToDictionary(group => group.Key, group => group.First());
            var operationNodeLookup = CurrentObjects.OfType<WGeneric>()
                .GroupBy(node => node.ID)
                .ToDictionary(group => group.Key, group => group.First());
            var placedNodes = new HashSet<WwiseHircObjNode>();
            var rows = new List<List<(int Column, IReadOnlyList<PNode> Nodes)>>();

            // Each event/action/voice path is a horizontal chain. Independent paths are stacked vertically.
            foreach (WEvent eventNode in eventNodes)
            {
                var actionNodes = eventNode.Event.EventActions
                    .Select(id => operationNodeLookup.TryGetValue(id, out WGeneric node) ? node : null)
                    .Where(node => node != null)
                    .ToList();

                if (actionNodes.Count == 0)
                {
                    AddRow(eventNode);
                    continue;
                }

                bool eventPlaced = false;
                foreach (WGeneric actionNode in actionNodes)
                {
                    foreach (List<WGeneric> path in BuildOperationPaths(actionNode, []))
                    {
                        var row = new List<(int Column, IReadOnlyList<PNode> Nodes)>();
                        if (!eventPlaced)
                        {
                            AddCell(row, 0, eventNode);
                            eventPlaced = true;
                        }

                        for (int column = 0; column < path.Count; column++)
                        {
                            AddCell(row, column + 1, path[column]);
                        }

                        if (row.Count > 0)
                        {
                            rows.Add(row);
                        }
                    }
                }

                if (!eventPlaced)
                {
                    AddRow(eventNode);
                }
            }

            // Lay out operation chains that are not reachable from an event after the event rows.
            var orphanRoots = CurrentObjects.OfType<WGeneric>().Where(node => node.InputEdges.IsEmpty()).ToList();
            foreach (WGeneric orphan in orphanRoots)
            {
                if (!placedNodes.Contains(orphan))
                {
                    AddOperationRows(orphan);
                }
            }

            // Cover cyclic or otherwise disconnected HIRC objects without recursing indefinitely.
            foreach (WGeneric remainingNode in CurrentObjects.OfType<WGeneric>().Where(node => !placedNodes.Contains(node)))
            {
                AddOperationRows(remainingNode);
            }

            foreach (WwiseHircObjNode remainingNode in CurrentObjects.Where(node => !placedNodes.Contains(node)))
            {
                AddRow(remainingNode);
            }

            ArrangeNodeRows(rows, HORIZONTAL_SPACING, VERTICAL_SPACING, ATTACHMENT_SPACING);

            if (eventNodes.FirstOrDefault() is { } firstEvent)
            {
                graphEditor.Camera.ViewScale = Math.Max(graphEditor.Camera.ViewScale, 0.75f);
                graphEditor.Camera.AnimateViewToCenterBounds(firstEvent.GlobalFullBounds, false, 0);
            }

            foreach (WwiseEdEdge edge in graphEditor.edgeLayer)
                WwiseGraphEditor.UpdateEdge(edge);

            void AddRow(WwiseHircObjNode node)
            {
                var row = new List<(int Column, IReadOnlyList<PNode> Nodes)>();
                if (AddCell(row, 0, node))
                {
                    rows.Add(row);
                }
            }

            bool AddCell(List<(int Column, IReadOnlyList<PNode> Nodes)> row, int column,
                WwiseHircObjNode node)
            {
                if (!placedNodes.Add(node))
                {
                    return false;
                }

                var cellNodes = new List<PNode> { node };
                foreach (uint id in node.Varlinks.SelectMany(link => link.Links))
                {
                    if (exportNodeLookup.TryGetValue(unchecked((int)id), out WExport exportNode)
                        && placedNodes.Add(exportNode))
                    {
                        cellNodes.Add(exportNode);
                    }
                    else if (operationNodeLookup.TryGetValue(id, out WGeneric linkedNode)
                             && placedNodes.Add(linkedNode))
                    {
                        cellNodes.Add(linkedNode);
                    }
                }

                row.Add((column, cellNodes));
                return true;
            }

            void AddOperationRows(WGeneric root)
            {
                foreach (List<WGeneric> path in BuildOperationPaths(root, []))
                {
                    var row = new List<(int Column, IReadOnlyList<PNode> Nodes)>();
                    for (int column = 0; column < path.Count; column++)
                    {
                        AddCell(row, column, path[column]);
                    }

                    if (row.Count > 0)
                    {
                        rows.Add(row);
                    }
                }
            }

            List<List<WGeneric>> BuildOperationPaths(WGeneric root, HashSet<uint> pathIds)
            {
                if (!pathIds.Add(root.ID))
                {
                    return [];
                }

                var paths = new List<List<WGeneric>>();
                var children = root.Outlinks.SelectMany(link => link.Links)
                    .Distinct()
                    .Where(id => !pathIds.Contains(id))
                    .Select(id => operationNodeLookup.TryGetValue(id, out WGeneric node) ? node : null)
                    .Where(node => node != null)
                    .ToList();
                foreach (WGeneric child in children)
                {
                    foreach (List<WGeneric> childPath in BuildOperationPaths(child, new HashSet<uint>(pathIds)))
                    {
                        childPath.Insert(0, root);
                        paths.Add(childPath);
                    }
                }

                if (paths.Count == 0)
                {
                    paths.Add([root]);
                }

                return paths;
            }
        }

        /// <summary>
        /// Places each relationship path on one horizontal row and stacks subsequent paths vertically.
        /// A cell's first node is the HIRC object; any remaining nodes are linked package exports shown below it.
        /// </summary>
        internal static void ArrangeNodeRows(
            IEnumerable<IEnumerable<(int Column, IReadOnlyList<PNode> Nodes)>> nodeRows,
            float horizontalSpacing, float verticalSpacing, float attachmentSpacing)
        {
            if (horizontalSpacing < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(horizontalSpacing));
            }
            if (verticalSpacing < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(verticalSpacing));
            }
            if (attachmentSpacing < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(attachmentSpacing));
            }

            var rows = nodeRows
                .Select(row => row
                    .Where(cell => cell.Column >= 0 && cell.Nodes?.Count > 0)
                    .OrderBy(cell => cell.Column)
                    .Select(cell =>
                    {
                        var nodes = cell.Nodes.Where(node => node != null).Distinct().ToList();
                        var bounds = nodes.Select(node => node.GlobalFullBounds)
                            .Where(bounds => bounds != RectangleF.Empty)
                            .ToList();
                        return (cell.Column, Nodes: nodes, Bounds: bounds,
                            Width: bounds.Count > 0 ? bounds.Max(bounds => bounds.Width) : 0,
                            Height: bounds.Sum(bounds => bounds.Height)
                                    + Math.Max(0, bounds.Count - 1) * attachmentSpacing);
                    })
                    .Where(cell => cell.Nodes.Count > 0 && cell.Width > 0 && cell.Height > 0)
                    .ToList())
                .Where(row => row.Count > 0)
                .ToList();
            if (rows.Count == 0)
            {
                return;
            }

            int columnCount = rows.SelectMany(row => row).Max(cell => cell.Column) + 1;
            var columnWidths = new float[columnCount];
            foreach (var cell in rows.SelectMany(row => row))
            {
                columnWidths[cell.Column] = Math.Max(columnWidths[cell.Column], cell.Width);
            }

            var columnLefts = new float[columnCount];
            for (int column = 1; column < columnCount; column++)
            {
                columnLefts[column] = columnLefts[column - 1] + columnWidths[column - 1] + horizontalSpacing;
            }

            float rowTop = 0;
            foreach (var row in rows)
            {
                float rowHeight = row.Max(cell => cell.Height);
                foreach (var cell in row)
                {
                    float cellLeft = columnLefts[cell.Column]
                                     + (columnWidths[cell.Column] - cell.Width) / 2;
                    float nodeTop = rowTop;
                    foreach (PNode node in cell.Nodes)
                    {
                        RectangleF bounds = node.GlobalFullBounds;
                        float nodeLeft = cellLeft + (cell.Width - bounds.Width) / 2;
                        node.OffsetBy(nodeLeft - bounds.Left, nodeTop - bounds.Top);
                        nodeTop += bounds.Height + attachmentSpacing;
                    }
                }

                rowTop += rowHeight + verticalSpacing;
            }
        }

        private void SoundPanel_HIRCObjectSelected(uint id)
        {
            if (CurrentObjects.Where(node => !(node is WExport)).FirstOrDefault(node => node.ID == id) is {} nodeToSelect)
            {
                panToSelection = true;
                SelectedNode = nodeToSelect;
            }
        }

        private void SoundPanel_HIRCEventSettingsRequested(uint eventId)
        {
            ExportEntry eventExport = FindEventExportForBank(Pcc?.Exports, eventId, CurrentExport);
            if (eventExport == null)
            {
                MessageBox.Show(this,
                    $"No WwiseEvent export linked to HIRC event 0x{eventId:X8} was found for this bank.",
                    "Event settings unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (TryAdjustEventSettings(this, eventExport, CurrentExport))
            {
                RefreshView();
                StatusBar_LeftMostText.Text = $"Updated settings for {eventExport.ObjectName.Instanced}";
            }
        }

        private static ExportEntry FindEventExportForBank(IEnumerable<ExportEntry> exports,
            uint eventId, ExportEntry bankExport)
        {
            var candidates = exports?.Where(export => export.ClassName == "WwiseEvent" &&
                                                       WExport.GetExportId(export) == eventId)
                .ToList() ?? [];
            ExportEntry linkedCandidate = candidates.FirstOrDefault(candidate =>
                ReferenceEquals(FindReferencedBank(candidate), bankExport));
            return linkedCandidate ?? (candidates.Count == 1 ? candidates[0] : null);
        }

        private bool panToSelection = true;
        protected void Node_MouseDown(object sender, PInputEventArgs e)
        {
            if (sender is WwiseHircObjNode obj)
            {
                obj.posAtDragStart = obj.GlobalFullBounds;
                if (e.Button == System.Windows.Forms.MouseButtons.Right)
                {
                    panToSelection = false;

                    SelectedNode = obj;
                    OpenNodeContextMenu(obj);
                }
                else if (!obj.IsSelected)
                {
                    panToSelection = false;
                    SelectedNode = obj;
                }
            }
        }

        private bool AllowWindowRefocus = true;
        public void OpenNodeContextMenu(WwiseHircObjNode obj)
        {
            if (FindResource("nodeContextMenu") is ContextMenu contextMenu)
            {
                bool showContextMenu = ConfigureHircNavigationMenu(contextMenu, obj);
                if (contextMenu.GetChild("adjustEventSettingsMenuItem") is MenuItem adjustEventSettingsMenuItem)
                {
                    bool canAdjustEvent = obj is WExport { Export.ClassName: "WwiseEvent" };
                    adjustEventSettingsMenuItem.Visibility = canAdjustEvent
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    showContextMenu |= canAdjustEvent;
                }
                if (contextMenu.GetChild("openInPackEdMenuItem") is MenuItem openInPackEdMenuItem)
                {
                    if (obj is WExport)
                    {
                        openInPackEdMenuItem.Visibility = Visibility.Visible;
                        showContextMenu = true;
                    }
                    else
                    {
                        openInPackEdMenuItem.Visibility = Visibility.Collapsed;
                    }
                }

                if (showContextMenu)
                {
                    contextMenu.IsOpen = true;
                    graphEditor.DisableDragging();
                }
            }
        }

        private bool ConfigureHircNavigationMenu(ContextMenu contextMenu, WwiseHircObjNode obj)
        {
            var hircNodesById = CurrentObjects
                .Where(node => node is not WExport)
                .GroupBy(node => node.ID)
                .ToDictionary(group => group.Key, group => group.First());

            bool hasParent = false;
            if (contextMenu.GetChild("goToParentMenuItem") is MenuItem parentMenuItem)
            {
                if (obj.ParentId is uint parentId && hircNodesById.TryGetValue(parentId, out var parentNode))
                {
                    parentMenuItem.Header = $"Go to parent: {BuildHircNavigationLabel(parentNode)}";
                    parentMenuItem.Tag = parentId;
                    parentMenuItem.Visibility = Visibility.Visible;
                    hasParent = true;
                }
                else
                {
                    parentMenuItem.Tag = null;
                    parentMenuItem.Visibility = Visibility.Collapsed;
                }
            }

            bool hasChildren = ConfigureNavigationSubmenu(contextMenu, "goToChildMenuItem",
                "Go to child", obj.ChildIds, hircNodesById);
            bool hasEffects = ConfigureNavigationSubmenu(contextMenu, "goToEffectMenuItem",
                "Go to applied effect", obj.EffectIds, hircNodesById);
            bool hasAttenuation = ConfigureNavigationSubmenu(contextMenu, "goToAttenuationMenuItem",
                "Go to attenuation", obj.AttenuationId is uint attenuationId ? [attenuationId] : [],
                hircNodesById);
            bool hasOutputBus = ConfigureNavigationSubmenu(contextMenu, "goToOutputBusMenuItem",
                "Go to output bus", obj.OutputBusId is uint outputBusId ? [outputBusId] : [],
                hircNodesById);

            bool hasNavigation = hasParent || hasChildren || hasEffects || hasAttenuation || hasOutputBus;
            if (contextMenu.GetChild("hircNavigationSeparator") is Separator separator)
            {
                separator.Visibility = hasNavigation ? Visibility.Visible : Visibility.Collapsed;
            }

            return hasNavigation;
        }

        private bool ConfigureNavigationSubmenu(ContextMenu contextMenu, string menuName, string header,
            IEnumerable<uint> targetIds, IReadOnlyDictionary<uint, WwiseHircObjNode> hircNodesById)
        {
            if (contextMenu.GetChild(menuName) is not MenuItem submenu)
            {
                return false;
            }

            submenu.Header = header;
            submenu.Items.Clear();
            foreach (uint targetId in targetIds.Distinct())
            {
                if (!hircNodesById.TryGetValue(targetId, out WwiseHircObjNode targetNode))
                {
                    continue;
                }

                var navigationItem = new MenuItem
                {
                    Header = BuildHircNavigationLabel(targetNode),
                    Tag = targetId
                };
                navigationItem.Click += HircNavigationMenuItem_Click;
                submenu.Items.Add(navigationItem);
            }

            submenu.Visibility = submenu.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            return submenu.Items.Count > 0;
        }

        internal static string BuildHircNavigationLabel(WwiseHircObjNode node)
        {
            string typeName = string.IsNullOrWhiteSpace(node.SemanticTypeName)
                ? "HIRC"
                : node.SemanticTypeName;
            string preview = WGeneric.CompactEventPreview(node.EventPreview, 4);
            return string.IsNullOrWhiteSpace(preview)
                ? $"{typeName} 0x{WwiseHircObjNode.GetIDString(node.ID)}"
                : $"{typeName} 0x{WwiseHircObjNode.GetIDString(node.ID)}{Environment.NewLine}{preview}";
        }

        private void HircNavigationMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: uint targetId })
            {
                return;
            }

            WwiseHircObjNode targetNode = CurrentObjects.FirstOrDefault(node =>
                node is not WExport && node.ID == targetId);
            if (targetNode == null)
            {
                return;
            }

            panToSelection = true;
            if (ReferenceEquals(SelectedNode, targetNode))
            {
                graphEditor.Camera.AnimateViewToCenterBounds(targetNode.GlobalFullBounds, false, 100);
                soundPanel.SelectHircObject(targetId);
            }
            else
            {
                SelectedNode = targetNode;
            }
        }

        private void ContextMenu_Closed(object sender, RoutedEventArgs e)
        {
            graphEditor.AllowDragging();
            if (AllowWindowRefocus)
            {
                Focus(); //this will make window bindings work, as context menu is not part of the visual tree, and focus will be on there if the user clicked it.
            }

            AllowWindowRefocus = true;
        }

        private void OpenInPackageEditor_Clicked(object sender, RoutedEventArgs e)
        {
            if (SelectedNode is WExport wExport)
            {
                AllowWindowRefocus = false; //prevents flicker effect when windows try to focus and then package editor activates
                var p = new PackageEditorWindow();
                p.Show();
                p.LoadFile(wExport.Export.FileRef.FilePath, wExport.Export.UIndex);
                p.Activate(); //bring to front
            }
        }

        public void RefreshView()
        {
            //saveView(false);
            LoadBank(CurrentExport, false);
        }

        public override void HandleUpdate(List<PackageUpdate> updates)
        {
            if (Pcc == null)
            {
                return;
            }

            IEnumerable<PackageUpdate> relevantUpdates = updates.Where(update => update.Change.HasFlag(PackageChange.Export));
            List<int> updatedExports = relevantUpdates.Select(x => x.Index).ToList();
            if (CurrentExport != null && updatedExports.Contains(CurrentExport.UIndex))
            {
                if (CurrentExport.ClassName != "WwiseBank")
                {
                    CurrentExport = null;
                    graphEditor.nodeLayer.RemoveAllChildren();
                    graphEditor.edgeLayer.RemoveAllChildren();
                    CurrentObjects.ClearEx();
                    Properties_InterpreterWPF.UnloadExport();
                }

                RefreshView();
                WwiseBankExports.ReplaceAll(Pcc.Exports.Where(exp => exp.ClassName == "WwiseBank"));
                return;
            }

            bool refreshedBanks = false, refreshedView = false;
            foreach (var uIndex in updatedExports)
            {
                if (Pcc.IsUExport(uIndex))
                {
                    string className = Pcc.GetUExport(uIndex).ClassName;

                    if (!refreshedBanks && className == "WwiseBank")
                    {
                        WwiseBankExports.ReplaceAll(Pcc.Exports.Where(exp => exp.ClassName == "WwiseBank"));
                        refreshedBanks = true;
                    }

                    if (!refreshedView && (className == "WwiseStream" || className == "WwiseEvent"))
                    {
                        RefreshView();
                        refreshedView = true;
                    }

                    if (refreshedView && refreshedBanks)
                    {
                        break;
                    }
                }
            }
        }

        private void WwiseEditorWPF_OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(FileQueuedForLoad))
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    //Wait for all children to finish loading
                    LoadFile(FileQueuedForLoad);
                    FileQueuedForLoad = null;

                    if (ExportQueuedForFocusing is null && Pcc.IsUExport(UIndexQueuedForFocusing))
                    {
                        ExportQueuedForFocusing = Pcc.GetUExport(UIndexQueuedForFocusing);
                    }

                    if (WwiseBankExports.Contains(ExportQueuedForFocusing))
                    {
                        CurrentExport = ExportQueuedForFocusing;
                    }
                    ExportQueuedForFocusing = null;

                    Activate();
                }));
            }
        }

        public static readonly string WwiseEditorDataFolder = Path.Combine(AppDirectories.AppDataFolder, "WwiseEditor");
        public static readonly string OptionsPath = Path.Combine(WwiseEditorDataFolder, "WwiseEditorOptions.JSON");
        public static readonly string ME3ViewsPath = Path.Combine(WwiseEditorDataFolder, "ME3Views");
        public static readonly string ME2ViewsPath = Path.Combine(WwiseEditorDataFolder, "ME2Views");
        public static readonly string LE3ViewsPath = Path.Combine(WwiseEditorDataFolder, "LE3Views");
        public static readonly string LE2ViewsPath = Path.Combine(WwiseEditorDataFolder, "LE2Views");

        private void SetupJSON(ExportEntry export)
        {
            string objectName = System.Text.RegularExpressions.Regex.Replace(export.ObjectName.Name, @"[<>:""/\\|?*]", "");

            var bankID = BitConverter.ToUInt32(BitConverter.GetBytes(export.GetProperty<IntProperty>("Id")), 0);
            string viewsPath = export.Game switch
            {
                MEGame.LE2 => LE2ViewsPath,
                MEGame.LE3 => LE3ViewsPath,
                MEGame.ME2 => ME2ViewsPath,
                _ => ME3ViewsPath
            };

            JSONpath = Path.Combine(viewsPath, $"{CurrentFile}.#{export.UIndex}.{bankID:X8}.{objectName}.JSON");
        }

        private void SaveView(bool toFile = true)
        {
            if (CurrentObjects.Count == 0)
                return;
            SavedPositions = new List<SaveData>();
            foreach (WwiseHircObjNode obj in CurrentObjects)
            {
                if (obj.Pickable)
                {
                    SavedPositions.Add(new SaveData
                    {
                        ID = obj is WExport wExp ? wExp.Export.UIndex.ReinterpretAsUint() : obj.ID,
                        X = obj.X + obj.Offset.X,
                        Y = obj.Y + obj.Offset.Y
                    });
                }
            }

            if (toFile)
            {
                string outputFile = JsonConvert.SerializeObject(SavedPositions);
                if (!Directory.Exists(Path.GetDirectoryName(JSONpath)))
                    Directory.CreateDirectory(Path.GetDirectoryName(JSONpath));
                File.WriteAllText(JSONpath, outputFile);
                SavedPositions.Clear();
            }
        }

        private void SaveImage()
        {
            if (CurrentObjects.Count == 0)
                return;
            string objectName = System.Text.RegularExpressions.Regex.Replace(CurrentExport.ObjectName.Instanced, @"[<>:""/\\|?*]", "");
            SaveFileDialog d = new ()
            {
                Filter = "PNG Files (*.png)|*.png",
                FileName = $"{CurrentFile}.{objectName}"
            };
            if (DirectoryMemory.ShowDialog(d) == true)
            {
                PNode r = graphEditor.Root;
                RectangleF rr = r.GlobalFullBounds;
                PNode p = PPath.CreateRectangle(rr.X, rr.Y, rr.Width, rr.Height);
                p.Brush = Brushes.White;
                graphEditor.AddBack(p);
                graphEditor.Camera.Visible = false;
                Image image = graphEditor.Root.ToImage();
                graphEditor.Camera.Visible = true;
                image.Save(d.FileName, ImageFormat.Png);
                graphEditor.backLayer.RemoveAllChildren();
                MessageBox.Show(this, "Done.");
            }
        }

        #region Busy

        public override void SetBusy(string text = null)
        {
            Image graphImage = graphEditor.Camera.ToImage((int)graphEditor.Camera.GlobalFullWidth, (int)graphEditor.Camera.GlobalFullHeight, new SolidBrush(GraphEditorBackColor));
            graphImageSub.Source = graphImage.ToBitmapImage();
            graphImageSub.Width = graphGrid.ActualWidth;
            graphImageSub.Height = graphGrid.ActualHeight;
            graphImageSub.Visibility = Visibility.Visible;
            GraphHost.Visibility = Visibility.Collapsed;
            BusyText = text;
            IsBusy = true;
        }

        public override void EndBusy()
        {
            IsBusy = false;
            GraphHost.Visibility = Visibility.Visible;
            graphImageSub.Visibility = Visibility.Collapsed;
        }

        #endregion

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, $"{CurrentFile} {value}");
        }

        private void WwiseEditorWPF_OnClosing(object sender, CancelEventArgs e)
        {
            if (e.Cancel)
            {
                return;
            }
            if (AutoSaveView_MenuItem.IsChecked)
                SaveView();

            Misc.AppSettings.Settings.WwiseGraphEditor_AutoSaveView = AutoSaveView_MenuItem.IsChecked;
            ThemeManager.ThemeChanged -= OnThemeChanged;
            soundPanel.HIRCObjectSelected -= SoundPanel_HIRCObjectSelected;
            soundPanel.HIRCEventSettingsRequested -= SoundPanel_HIRCEventSettingsRequested;
            soundPanel.Dispose();
            
            foreach (var x in CurrentObjects)
            {
                x.MouseDown -= Node_MouseDown;
                x.Dispose();
            }

            CurrentObjects.Clear();
            graphEditor.Dispose();

            RecentsController?.Dispose();
        }

        public void PropogateRecentsChange(string propogationSource, IEnumerable<RecentsControl.RecentItem> newRecents)
        {
            RecentsController.PropogateRecentsChange(false, newRecents);
        }

        public string Toolname => "WwiseEditor";
    }
}
