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
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Tools.PackageEditor;
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
using WwiserActiveFlags = ME3Tweaks.Wwiser.Model.Action.Specific.ActiveFlags;
using WwiserPauseResume = ME3Tweaks.Wwiser.Model.Action.Specific.PauseResume;
using WwiserEvent = ME3Tweaks.Wwiser.Model.Hierarchy.Event;
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
                _graphEditorBackColor = Color.FromArgb(30, 30, 30);
                _boxFillColor = Color.FromArgb(45, 45, 48);
                _titleBoxColor = Color.FromArgb(37, 37, 38);
                _commentTextColor = Color.FromArgb(87, 166, 74);
                _boxTextColor = Color.FromArgb(220, 220, 220);
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
                        soundPanel.HIRC_ListBox.SelectedIndex = CurrentObjects.IndexOf(value);
                    }
                }
            }
        }

        private WwiseBankParsed CurrentWwiseBank;

        public ICommand OpenCommand { get; set; }
        public ICommand SaveCommand { get; set; }
        public ICommand SaveAsCommand { get; set; }
        public ICommand SaveImageCommand { get; set; }
        public ICommand SaveViewCommand { get; set; }

        private void LoadCommands()
        {
            OpenCommand = new GenericCommand(OpenFile);
            SaveCommand = new GenericCommand(SavePackage, IsPackageLoaded);
            SaveAsCommand = new GenericCommand(SavePackageAs, IsPackageLoaded);
            SaveImageCommand = new GenericCommand(SaveImage, () => CurrentObjects.Any);
            SaveViewCommand = new GenericCommand(() => SaveView(), () => CurrentObjects.Any);
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
            if (CurrentExport == null)
            {
                return;
            }

            try
            {
                var rawBank = CurrentExport.GetBinaryData<CoreWwiseBank>();
                using var input = new MemoryStream(rawBank.BnkFile, false);
                var bank = WwiseBankParser.Deserialize(input);
                var parameterNodes = bank.HIRC?.Items
                    .Where(item => item.Item is WwiserIHasNode)
                    .Select(item => (item.Item.Id, Node: (WwiserIHasNode)item.Item))
                    .ToList() ?? [];
                var rootNodes = GetRootNodes(parameterNodes);
                if (rootNodes.Count == 0)
                {
                    rootNodes.AddRange(parameterNodes.Select(item => item.Node));
                }

                var effectScopeNodes = GetEffectScopeNodes(parameterNodes);

                if (rootNodes.Count == 0)
                {
                    MessageBox.Show(this, "No editable audio nodes were found in this bank.", "Settings not adjusted",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var sounds = bank.HIRC?.Items
                    .Select(item => item.Item)
                    .OfType<WwiserSound>()
                    .ToList() ?? [];
                bool? loopAudio = GetLoopAudioState(sounds);
                bool factoryRadioEffect = HasEffectOnAllScopes(effectScopeNodes, WwiseBankEffectPresets.FactoryRadio);
                bool helmetEffect = HasEffectOnAllScopes(effectScopeNodes, WwiseBankEffectPresets.HelmetFilter) &&
                                    WwiseBankEffectPresets.HasHelmetRtpcOnAllScopes(effectScopeNodes);
                bool bioWareRadioEffect = !helmetEffect &&
                                           HasEffectOnAllScopes(effectScopeNodes, WwiseBankEffectPresets.BioWareRadio);
                bool qecEffect = HasEffectOnAllScopes(effectScopeNodes, WwiseBankEffectPresets.HackettQec);
                bool canApplyFactoryRadioEffect = CanApplyEffect(bank, effectScopeNodes, WwiseBankEffectPresets.FactoryRadio);
                bool canApplyBioWareRadioEffect = Pcc.Game == MEGame.LE3 &&
                                                   CanApplyEffect(bank, effectScopeNodes, WwiseBankEffectPresets.BioWareRadio);
                bool canApplyQecEffect = Pcc.Game == MEGame.LE3 &&
                                         CanApplyEffect(bank, effectScopeNodes, WwiseBankEffectPresets.HackettQec);
                bool canApplyHelmetEffect = Pcc.Game == MEGame.LE3 &&
                                            CanApplyEffect(bank, effectScopeNodes, WwiseBankEffectPresets.HelmetFilter);
                bool stopAllEventExists = HasCompleteStopAllEvent(bank, CurrentExport, sounds);
                bool canCreateStopAllEvent = Pcc.Game == MEGame.LE3 && CanCreateStopAllEvent(bank, CurrentExport, sounds);
                float currentVolume = GetNodeVolume(rootNodes[0]);
                var settingsDialog = new WwiseBankVolumeDialog(currentVolume, loopAudio,
                    factoryRadioEffect, canApplyFactoryRadioEffect,
                    bioWareRadioEffect, canApplyBioWareRadioEffect,
                    qecEffect, canApplyQecEffect,
                    helmetEffect, canApplyHelmetEffect,
                    stopAllEventExists, canCreateStopAllEvent)
                {
                    Owner = this
                };
                if (settingsDialog.ShowDialog() != true)
                {
                    return;
                }

                bool volumeChanged = settingsDialog.SelectedVolume != currentVolume;
                bool loopChanged = settingsDialog.LoopAudio.HasValue && settingsDialog.LoopAudio != loopAudio;
                bool factoryRadioChanged = settingsDialog.FactoryRadioEffect != factoryRadioEffect;
                bool bioWareRadioChanged = settingsDialog.BioWareRadioEffect != bioWareRadioEffect;
                bool qecChanged = settingsDialog.QecEffect != qecEffect;
                bool helmetChanged = settingsDialog.HelmetEffect != helmetEffect;
                bool createStopAllEvent = settingsDialog.CreateStopAllEvent;
                if (!volumeChanged && !loopChanged && !factoryRadioChanged && !bioWareRadioChanged &&
                    !qecChanged && !helmetChanged && !createStopAllEvent)
                {
                    return;
                }

                foreach (var (enabled, changed, name, effectChain) in new[]
                         {
                             (settingsDialog.FactoryRadioEffect, factoryRadioChanged,
                                 "Dual_Filters_Radio_Comm", WwiseBankEffectPresets.FactoryRadio),
                             (settingsDialog.BioWareRadioEffect, bioWareRadioChanged,
                                 "BioWare radio", WwiseBankEffectPresets.BioWareRadio),
                             (settingsDialog.QecEffect, qecChanged,
                                 "Hackett QEC", WwiseBankEffectPresets.HackettQec),
                             (settingsDialog.HelmetEffect, helmetChanged,
                                 "helmet voice", WwiseBankEffectPresets.HelmetFilter)
                         })
                {
                    if (changed && enabled && !WwiseBankEffectPresets.EnsureEffectData(bank, effectChain))
                    {
                        MessageBox.Show(this,
                            $"The {name} effect data could not be added to this bank version.",
                            "Effect unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                foreach (var node in rootNodes)
                {
                    if (volumeChanged)
                    {
                        SetInitialParameter(node.NodeBaseParameters.InitialParams62, PropId.Volume,
                            settingsDialog.SelectedVolume, true);
                    }
                }

                if (factoryRadioChanged && settingsDialog.FactoryRadioEffect)
                {
                    SetEffectPresetOnScopes(effectScopeNodes, WwiseBankEffectPresets.FactoryRadio);
                }
                else if (bioWareRadioChanged && settingsDialog.BioWareRadioEffect)
                {
                    SetEffectPresetOnScopes(effectScopeNodes, WwiseBankEffectPresets.BioWareRadio);
                }
                else if (qecChanged && settingsDialog.QecEffect)
                {
                    SetEffectPresetOnScopes(effectScopeNodes, WwiseBankEffectPresets.HackettQec);
                }
                else if (helmetChanged && settingsDialog.HelmetEffect)
                {
                    SetEffectPresetOnScopes(effectScopeNodes, WwiseBankEffectPresets.HelmetFilter);
                }
                else
                {
                    if (factoryRadioChanged)
                    {
                        SetEffectOnScopes(effectScopeNodes, WwiseBankEffectPresets.FactoryRadio, false);
                    }
                    if (bioWareRadioChanged)
                    {
                        SetEffectOnScopes(effectScopeNodes, WwiseBankEffectPresets.BioWareRadio, false);
                    }
                    if (qecChanged)
                    {
                        SetEffectOnScopes(effectScopeNodes, WwiseBankEffectPresets.HackettQec, false);
                    }
                    if (helmetChanged)
                    {
                        SetEffectOnScopes(effectScopeNodes, WwiseBankEffectPresets.HelmetFilter, false);
                        WwiseBankEffectPresets.SetHelmetRtpcOnScopes(effectScopeNodes, false);
                    }
                }

                if (loopChanged)
                {
                    foreach (var sound in sounds)
                    {
                        SetLoopAudio(sound, settingsDialog.LoopAudio == true);
                    }
                }

                if (createStopAllEvent)
                {
                    EnsureStopAllEventInBank(bank, sounds);
                }

                using var output = new MemoryStream();
                WwiseBankParser.Serialize(bank, output);
                CoreWwiseBank.WriteBankRaw(output.ToArray(), CurrentExport);
                if (createStopAllEvent)
                {
                    EnsureStopAllEventExport(CurrentExport, sounds);
                }
                RefreshView();
                StatusBar_LeftMostText.Text = $"Updated settings for {CurrentExport.ObjectName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Unable to adjust bank settings:\n{ex.Message}", "Bank settings failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

        private static List<WwiserIHasNode> GetEffectScopeNodes(
            IReadOnlyCollection<(uint Id, WwiserIHasNode Node)> parameterNodes)
        {
            var rootNodes = GetRootNodes(parameterNodes);
            return rootNodes
                .Concat(parameterNodes
                    .Where(item => item.Node.NodeBaseParameters.FxParams.IsOverrideParentFx)
                    .Select(item => item.Node))
                .Distinct()
                .ToList();
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

            var replaceableEffectIds = WwiseBankEffectPresets.FactoryRadio
                .Concat(WwiseBankEffectPresets.BioWareRadio)
                .Concat(WwiseBankEffectPresets.HackettQec)
                .Concat(WwiseBankEffectPresets.HelmetFilter)
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
            ExportEntry bankExport, IReadOnlyCollection<WwiserSound> sounds)
        {
            if (bank.HIRC == null || sounds.Count == 0 ||
                bank.HIRC.Items.FirstOrDefault(item => item.Item.Id == StopAllEventId)?.Item is not WwiserEvent stopEvent)
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
            return sounds.All(sound => stoppedSoundIds.Contains(sound.Id)) && HasStopAllEventExport(bankExport);
        }

        private static bool CanCreateStopAllEvent(ME3Tweaks.Wwiser.WwiseBank bank,
            ExportEntry bankExport, IReadOnlyCollection<WwiserSound> sounds)
        {
            if (bank.HIRC == null || bank.BKHD.BankGeneratorVersion != WwiseBankEffectPresets.BankVersion ||
                sounds.Count == 0)
            {
                return false;
            }

            var existingHirc = bank.HIRC.Items.FirstOrDefault(item => item.Item.Id == StopAllEventId);
            if (existingHirc != null && existingHirc.Item is not WwiserEvent)
            {
                return false;
            }

            var existingExport = bankExport.FileRef.Exports.FirstOrDefault(export =>
                export.Parent == bankExport.Parent && export.ObjectNameString == "Stop");
            return existingExport == null || existingExport.ClassName == "WwiseEvent";
        }

        private static void EnsureStopAllEventInBank(ME3Tweaks.Wwiser.WwiseBank bank,
            IReadOnlyCollection<WwiserSound> sounds)
        {
            if (bank.HIRC == null)
            {
                throw new InvalidOperationException("The bank has no audio hierarchy.");
            }

            var usedIds = bank.HIRC.Items.Select(item => item.Item.Id).ToHashSet();
            var stopEventItem = bank.HIRC.Items.FirstOrDefault(item => item.Item.Id == StopAllEventId);
            WwiserEvent stopEvent;
            bool addStopEvent = false;
            if (stopEventItem == null)
            {
                stopEvent = new WwiserEvent
                {
                    Id = StopAllEventId,
                    ActionCount = new VarCount(),
                    ActionIds = []
                };
                stopEventItem = new WwiserHircItemContainer
                {
                    Type = new HircSmartType { Value = HircType.Event },
                    Item = stopEvent
                };
                addStopEvent = true;
                usedIds.Add(StopAllEventId);
            }
            else if (stopEventItem.Item is WwiserEvent existingStopEvent)
            {
                stopEvent = existingStopEvent;
            }
            else
            {
                throw new InvalidOperationException($"HIRC ID {StopAllEventId} is already used by another object.");
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
                uint actionId = GenerateUniqueHircId($"Stop_{sound.Id}_StopAction", usedIds);
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
                        Params = new ActiveParams
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
            bankExport.FileRef.Exports.Any(export => export.ClassName == "WwiseEvent" &&
                export.Parent == bankExport.Parent &&
                unchecked((uint)(export.GetProperty<IntProperty>("Id")?.Value ?? 0)) == StopAllEventId &&
                export.GetProperty<StructProperty>("Relationships")?.Properties
                    .GetProp<ObjectProperty>("Bank")?.Value == bankExport.UIndex);

        private static void EnsureStopAllEventExport(ExportEntry bankExport,
            IReadOnlyCollection<WwiserSound> sounds)
        {
            var package = bankExport.FileRef;
            var eventExport = package.Exports.FirstOrDefault(export => export.ClassName == "WwiseEvent" &&
                                  export.Parent == bankExport.Parent &&
                                  unchecked((uint)(export.GetProperty<IntProperty>("Id")?.Value ?? 0)) == StopAllEventId)
                              ?? package.FindExport($"{bankExport.Parent.InstancedFullPath}.Stop", "WwiseEvent")
                              ?? ExportCreator.CreateExport(package, "Stop", "WwiseEvent", bankExport.Parent, indexed: false);

            var properties = eventExport.GetProperties();
            properties.AddOrReplaceProp(new StructProperty("WwiseRelationships", false,
                new ObjectProperty(bankExport, "Bank")) { Name = "Relationships" });
            properties.AddOrReplaceProp(new IntProperty(unchecked((int)StopAllEventId), "Id"));
            if (bankExport.GetProperty<BoolProperty>("IsLocalised")?.Value == true)
            {
                properties.AddOrReplaceProp(new BoolProperty(true, "IsLocalised"));
            }

            var sourceIds = sounds.Select(sound => sound.BankSourceData.MediaInformation.SourceId).ToHashSet();
            var streamIndexes = package.Exports
                .Where(export => export.ClassName == "WwiseStream" &&
                                 sourceIds.Contains(unchecked((uint)(export.GetProperty<IntProperty>("Id")?.Value ?? 0))))
                .Select(export => export.UIndex)
                .ToList();
            var eventBinary = CoreWwiseEvent.Create();
            eventBinary.Links[0].WwiseStreams = streamIndexes;
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

            if (SavedPositions.IsEmpty())
            {
                AutoLayout();
            }
        }

        private void GetObjects(WwiseBankParsed bank)
        {
            var newObjs = new List<WwiseHircObjNode>();
            foreach ((uint id, WwiseBankParsed.HIRCObject hircObject) in CurrentWwiseBank.HIRCObjects)
            {
                newObjs.Add(hircObject switch
                {
                    WwiseBankParsed.Event evt => new WEvent(evt, 0, 0, graphEditor),
                    WwiseBankParsed.EventAction evtAct => new WEventAction(evtAct, 0, 0, graphEditor),
                    WwiseBankParsed.SoundSFXVoice sfxvoice => new WSoundSFXVoice(sfxvoice, 0, 0, graphEditor),
                    _ => new WGeneric(hircObject, 0, 0, graphEditor)
                });
            }

            CurrentObjects.ReplaceAll(newObjs);
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
                            wwiseEvents.AddToListAt((exportEntry.GetProperty<IntProperty>("Id")?.Value ?? 0).ReinterpretAsUint(), exportEntry);
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

            const float HORIZONTAL_SPACING = 40;
            const float VERTICAL_SPACING = 20;
            const float VAR_SPACING = 10;
            var visitedNodes = new HashSet<uint>();
            var eventNodes = CurrentObjects.OfType<WEvent>().ToList();
            WwiseHircObjNode firstNode = eventNodes.FirstOrDefault();
            var varNodeLookup = CurrentObjects.OfType<WExport>().ToDictionary(obj => obj.Export.UIndex);
            var opNodeLookup = CurrentObjects.OfType<WGeneric>().ToDictionary(obj => obj.ID);
            var rootTree = new List<WwiseHircObjNode>();
            //WEvents are natural root nodes. ALmost everything will proceed from one of these
            foreach (WEvent eventNode in eventNodes)
            {
                LayoutTree(eventNode, 5 * VERTICAL_SPACING);
            }

            //Find WGenerics with no inputs. These will not have been reached from an WEvent
            var orphanRoots = CurrentObjects.OfType<WGeneric>().Where(node => node.InputEdges.IsEmpty());
            foreach (WGeneric orphan in orphanRoots)
            {
                if (!visitedNodes.Contains(orphan.ID))
                {
                    LayoutTree(orphan, VERTICAL_SPACING);
                }
            }

            //It's possible that there are groups of otherwise unconnected WGenerics that form cycles.
            //Might be possible to make a better heuristic for choosing a root than sequence order, but this situation is so rare it's not worth the effort
            var cycleNodes = CurrentObjects.OfType<WGeneric>().Where(node => !visitedNodes.Contains(node.ID));
            foreach (WGeneric cycleNode in cycleNodes)
            {
                LayoutTree(cycleNode, VERTICAL_SPACING);
            }

            if (firstNode != null) CurrentObjects.OffsetBy(0, -firstNode.OffsetY);

            foreach (WwiseEdEdge edge in graphEditor.edgeLayer)
                WwiseGraphEditor.UpdateEdge(edge);

            void LayoutTree(WwiseHircObjNode WGeneric, float verticalSpacing)
            {
                if (firstNode == null) firstNode = WGeneric;
                visitedNodes.Add(WGeneric.ID);
                var subTree = LayoutSubTree(WGeneric);
                float width = subTree.BoundingRect().Width + HORIZONTAL_SPACING;
                //ignore nodes that are further to the right than this subtree is wide. This allows tighter spacing
                float dy = rootTree.Where(node => node.GlobalFullBounds.Left < width).BoundingRect().Bottom;
                if (dy > 0) dy += verticalSpacing;
                subTree.OffsetBy(0, dy);
                rootTree.AddRange(subTree);
            }

            List<WwiseHircObjNode> LayoutSubTree(WwiseHircObjNode root)
            {
                var tree = new List<WwiseHircObjNode>();
                var vars = new List<WwiseHircObjNode>();
                foreach (var varLink in root.Varlinks)
                {
                    float dx = varLink.node.GlobalFullBounds.X - WExport.RADIUS;
                    float dy = root.GlobalFullHeight + VAR_SPACING;
                    foreach (uint id in varLink.Links.Where(id => !visitedNodes.Contains(id)))
                    {
                        visitedNodes.Add(id);
                        if (varNodeLookup.TryGetValue((int)id, out WExport WExport))
                        {
                            WExport.OffsetBy(dx, dy);
                            dy += WExport.GlobalFullHeight + VAR_SPACING;
                            vars.Add(WExport);
                        }
                        else if (opNodeLookup.TryGetValue(id, out WGeneric node))
                        {
                            node.OffsetBy(dx, dy);
                            dy += node.GlobalFullHeight + VAR_SPACING;
                            vars.Add(node);
                        }
                    }
                }

                var childTrees = new List<List<WwiseHircObjNode>>();
                var children = root.Outlinks.SelectMany(link => link.Links).Where(id => !visitedNodes.Contains(id));
                foreach (uint id in children)
                {
                    visitedNodes.Add(id);
                    if (opNodeLookup.TryGetValue(id, out WGeneric node))
                    {
                        List<WwiseHircObjNode> subTree = LayoutSubTree(node);
                        childTrees.Add(subTree);
                    }
                }

                if (childTrees.Any())
                {
                    float dx = root.GlobalFullWidth + (HORIZONTAL_SPACING * (1 + childTrees.Count * 0.4f));
                    foreach (List<WwiseHircObjNode> subTree in childTrees)
                    {
                        float subTreeWidth = subTree.BoundingRect().Width + HORIZONTAL_SPACING + dx;
                        //ignore nodes that are further to the right than this subtree is wide. This allows tighter spacing
                        float dy = tree.Where(node => node.GlobalFullBounds.Left < subTreeWidth).BoundingRect().Bottom;
                        if (dy > 0) dy += VERTICAL_SPACING;
                        subTree.OffsetBy(dx, dy);
                        //TODO: fix this so it doesn't screw up some sequences. eg: BioD_ProEar_310BigFall.pcc
                        /*float treeWidth = tree.BoundingRect().Width + HORIZONTAL_SPACING;
                        //tighten spacing when this subtree is wider than existing tree. 
                        dy -= subTree.Where(node => node.GlobalFullBounds.Left < treeWidth).BoundingRect().Top;
                        if (dy < 0) dy += VERTICAL_SPACING;
                        subTree.OffsetBy(0, dy);*/

                        tree.AddRange(subTree);
                    }

                    //center the root on its children
                    float centerOffset = tree.OfType<WGeneric>().BoundingRect().Height / 2 - root.GlobalFullHeight / 2;
                    root.OffsetBy(0, centerOffset);
                    vars.OffsetBy(0, centerOffset);
                }

                tree.AddRange(vars);
                tree.Add(root);
                return tree;
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
                bool showContextMenu = false;
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
