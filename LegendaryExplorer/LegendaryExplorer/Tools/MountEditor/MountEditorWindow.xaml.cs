using System;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.SharedUI.Interfaces;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.Unreal;
using Xceed.Wpf.Toolkit.Primitives;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Tools.MountEditor
{
    /// <summary>
    /// Interaction logic for MountEditorWPF.xaml
    /// </summary>
    public partial class MountEditorWindow : TrackingNotifyPropertyChangedWindowBase, IRecents
    {
        public string Toolname { get; }
        public ObservableCollectionExtended<MountFlag> MountOptions { get; } = new();
        //private readonly List<UIMountFlag> ME2MountFlags = new();
        //private readonly List<UIMountFlag> ME3MountFlags = new();

        public ObservableCollectionExtended<UIGameID> Games { get; } = new()
        {
            new UIGameID(MEGame.ME2, "Mass Effect 2"),
            new UIGameID(MEGame.ME3, "Mass Effect 3"),
            new UIGameID(MEGame.LE2, "Mass Effect 2 LE"),
            new UIGameID(MEGame.LE3, "Mass Effect 3 LE")
        };

        private UIGameID _selectedGame;
        public UIGameID SelectedGame
        {
            get => _selectedGame;
            set
            {
                if (SetProperty(ref _selectedGame, value))
                {
                    SelectedGameChanged();
                }
            }
        }

        private bool _isME2;
        public bool IsME2
        {
            get => _isME2;
            set => SetProperty(ref _isME2, value);
        }
        private string _currentTLKIDString;
        public string CurrentTLKIDString
        {
            get => _currentTLKIDString;
            set => SetProperty(ref _currentTLKIDString, value);
        }

        private string _currentMountFileText;
        public string CurrentMountFileText
        {
            get => _currentMountFileText;
            set => SetProperty(ref _currentMountFileText, value);
        }

        public MountEditorWindow() : this("MountEditor")
        {
        }

        internal MountEditorWindow(string recentsToolName) : base("Mount Editor", true)
        {
            Toolname = recentsToolName;
            CurrentMountFileText = "No mount file loaded. Mouse over fields for descriptions of their values.";
            DataContext = this;
            InitializeComponent();
            SelectedGame = Games[0];
            RecentsController.ConfigureGameTabs(Games.Select(game => game.Game), MEGame.LE3);
            RecentsController.InitRecentControl(Toolname, Recents_MenuItem, OpenRecentMountFile,
                "Mount files (*.dlc)|*.dlc");
        }

        private void OpenRecentMountFile(string path)
        {
            try
            {
                LoadFile(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Unable to open mount file:\n{ex.Message}", "Mount Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void PropogateRecentsChange(string propogationToolSource, IEnumerable<RecentsControl.RecentItem> newRecents)
        {
            if (propogationToolSource == Toolname) RecentsController.PropogateRecentsChange(false, newRecents);
        }

        private void Root_Closed(object sender, EventArgs e) => RecentsController.Dispose();

        private void PreviewIntegerInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;
            // Use SelectionStart property to find the caret position.
            // Insert the previewed text into the existing text in the textbox.
            var fullText = textBox.Text.Insert(textBox.SelectionStart, e.Text);

            // If parsing is successful, set Handled to false
            e.Handled = !double.TryParse(fullText, out double _); // Why is this double
        }

        public sealed record UIGameID(MEGame Game, string DisplayString);

        private void LoadMountFile_Click(object sender, RoutedEventArgs e)
        {
            CommonOpenFileDialog m = new()
            {
                EnsurePathExists = true,
                Title = "Select Mount.dlc file",
            };

            m.Filters.Add(new CommonFileDialogFilter("Mount files", "*.dlc"));
            if (DirectoryMemory.ShowDialog(m, this) == CommonFileDialogResult.Ok)
            {
                LoadFile(m.FileName);
            }
        }

        public void LoadFile(string fileName)
        {
            loadingNewData = true;
            var mf = new MountFile(fileName);
            SelectedGame = Games.First(uig => uig.Game == mf.Game);
            DLCFolder_TextBox.Text = IsME2 ? mf.ME2Only_DLCFolderName : "Not used in ME3"; // Update for LE3
            HumanReadable_TextBox.Text = IsME2 ? mf.ME2Only_DLCHumanName : "Not used in ME3"; // Update for LE3
            TLKID_TextBox.Text = mf.TLKID.ToString();
            MountPriority_TextBox.Text = mf.MountPriority.ToString();

            // Mount flags
            if (IsME2)
            {
                var flagset = Enum.GetValues<EME2MountFileFlag>();
                MountOptions.ReplaceAll(flagset.Select(x => new MountFlag((int)x, true)));
            }
            else
            {
                var flagset = Enum.GetValues<EME3MountFileFlag>();
                MountOptions.ReplaceAll(flagset.Select(x => new MountFlag((int)x, false)));
            }

            CurrentMountFileText = fileName;
            SetSelectedFlagsUI(mf.MountFlags.FlagValue);
            loadingNewData = false;
            RecentsController.AddRecent(fileName, false, mf.Game);
            CloseStartView();
        }

        private void CloseStartView()
        {
            if (RecentsPane.Visibility == Visibility.Collapsed) return;

            RecentsPane.Visibility = Visibility.Collapsed;
            RecentsSplitter.Visibility = Visibility.Collapsed;
            RecentsColumn.MinWidth = 0;
            RecentsColumn.Width = new GridLength(0);
            RecentsSplitterColumn.Width = new GridLength(0);
            EditorColumn.Width = new GridLength(1, GridUnitType.Star);
            MinWidth = 500;
            MinHeight = 386;
            Width = 500;
            Height = 386;
        }

        private void SetSelectedFlagsUI(int flag)
        {
            if (IsME2)
            {
                var flagset = Enum.GetValues<EME2MountFileFlag>();
                var selectedFlags = flagset.Where(testflag => ((int)testflag & flag) != 0).ToList();
                foreach (var item in MountOptions)
                {
                    item.IsUISelected = selectedFlags.Any(x => x == (EME2MountFileFlag)item.FlagValue);
                }
            }
            else
            {
                var flagset = Enum.GetValues<EME3MountFileFlag>();
                var selectedFlags = flagset.Where(testflag => ((int)testflag & flag) != 0).ToList();
                foreach (var item in MountOptions)
                {
                    item.IsUISelected = selectedFlags.Any(x => x == (EME3MountFileFlag)item.FlagValue);
                }
            }
        }

        private void SaveMountFile_Click(object sender, RoutedEventArgs e)
        {
            if (Validate())
            {
                CommonSaveFileDialog m = new()
                {
                    EnsurePathExists = true,
                    Title = "Select Mount.dlc file save destination",
                    DefaultExtension = "dlc",
                    AlwaysAppendDefaultExtension = true,
                    DefaultFileName = "mount.dlc",
                    InitialDirectory = (!string.IsNullOrEmpty(CurrentMountFileText) && File.Exists(CurrentMountFileText) ? Path.GetDirectoryName(CurrentMountFileText) : null)
                };
                m.Filters.Add(new CommonFileDialogFilter("Mount files", "*.dlc"));
                if (DirectoryMemory.ShowDialog(m) == CommonFileDialogResult.Ok)
                {
                    SaveFile(m.FileName);
                    MessageBox.Show("Done.");
                }
            }
        }

        internal void SaveFile(string fileName)
        {
            var mf = new MountFile
            {
                Game = SelectedGame.Game,
                MountPriority = ushort.Parse(MountPriority_TextBox.Text.Trim()),
                TLKID = int.Parse(TLKID_TextBox.Text.Trim()),
                MountFlags = GetCurrentMountFlag()
            };
            if (mf.Game.IsGame2())
            {
                mf.ME2Only_DLCFolderName = DLCFolder_TextBox.Text;
                mf.ME2Only_DLCHumanName = HumanReadable_TextBox.Text;
            }
            mf.WriteMountFile(fileName);
            CurrentMountFileText = fileName;
            RecentsController.AddRecent(fileName, false, mf.Game);
            CloseStartView();
        }

        private MountFlag GetCurrentMountFlag()
        {
            MountFlag mf = new MountFlag(0, IsME2);
            foreach (var flag in MountOptions.Where(x => x.IsUISelected))
            {
                mf.SetFlagBit(flag.FlagValue);
            }

            return mf;
        }

        private bool Validate()
        {
            var saveDep = IsME2 ? (int)EME2MountFileFlag.SaveFileDependency : (int)EME3MountFileFlag.SaveFileDependency;
            if ((saveDep & GetCurrentMountFlag().FlagValue) != 0)
            {
                Xceed.Wpf.Toolkit.MessageBox.Show($"Cannot save a mount file with the SaveFileDependency flag set. This flag causes serious issues with save games when used with mods.", "Validation error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (!ushort.TryParse(MountPriority_TextBox.Text, out ushort _))
            {
                Xceed.Wpf.Toolkit.MessageBox.Show("Mount priority must be a value between 1 and " + ushort.MaxValue + ".", "Validation error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (!int.TryParse(TLKID_TextBox.Text, out int valuex) || valuex <= 0)
            {
                Xceed.Wpf.Toolkit.MessageBox.Show("TLK ID must be between 1 and " + (uint.MaxValue / 2) + ".", "Validation error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (IsME2)
            {
                if (SelectedGame.Game is MEGame.ME2 && HumanReadable_TextBox.Text.Length < 5)
                {
                    Xceed.Wpf.Toolkit.MessageBox.Show("Human readable name must be at least 5 characters.\nUse the full name of your mod to prevent end-user confusion.", "Validation error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                if (!DLCFolder_TextBox.Text.StartsWith("DLC_"))
                {
                    Xceed.Wpf.Toolkit.MessageBox.Show("DLC Folder Name must start with \"DLC_\".\nMass Effect 2 will not load a DLC that does not start with this prefix.", "Validation error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            }
            return true;
        }

        private void PreviewShortInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;
            // Use SelectionStart property to find the caret position.
            // Insert the previewed text into the existing text in the textbox.
            var fullText = textBox.Text.Insert(textBox.SelectionStart, e.Text);

            //Handled means it won't appear.
            var handled = double.TryParse(fullText, out double _);

            //Validate it
            if (handled)
            {
                if (int.TryParse(fullText, out int value))
                {
                    //logic is backwards. Handled means the character won't appear
                    e.Handled = value <= 0 || value > short.MaxValue; //16-bit limit
                    return;
                }
            }
            e.Handled = true;
        }

        private void FindTlkText_Click(object sender, RoutedEventArgs e)
        {
            if (TlkStringRefSelector.SelectStringRef(this, SelectedGame.Game) is int stringRef)
            {
                TLKID_TextBox.Text = stringRef.ToString();
                TLKID_TextBox.CaretIndex = TLKID_TextBox.Text.Length;
                TLKID_TextBox.Focus();
            }
        }

        private void TLKID_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (int.TryParse(TLKID_TextBox.Text, out int tlkValue))
            {
                CurrentTLKIDString = TLKManagerWPF.GlobalFindStrRefbyID(tlkValue, SelectedGame.Game);
            }
        }

        private void SelectedGameChanged()
        {
            IsME2 = SelectedGame.Game is MEGame.ME2 or MEGame.LE2;
            DLCFolder_TextBox.Watermark = IsME2 ? "DLC Folder Name (e.g. DLC_MOD_MYMOD)" : "Not used in ME3";
            HumanReadable_TextBox.Watermark = IsME2 ? "DLC Human Readable Name (e.g. Superpowers Pack)" : "Not used in ME3";
            //MountComboBox.SelectedIndex = 0;
            TLKID_TextChanged(null, null);
        }
        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Note that you can have more than one file.
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);

                // Assuming you have one file that you care about, pass it off to whatever
                // handling code you have defined.
                LoadFile(files[0]);
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Note that you can have more than one file.
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                string ext = Path.GetExtension(files[0]).ToLower();
                if (ext != ".dlc")
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

        private void MountOptionsComboBox_GotFocus(object sender, RoutedEventArgs e)
        {
        }

        private bool loadingNewData;
        private void MountOptionsComboBox_ItemSelectionChanged(object sender, Xceed.Wpf.Toolkit.Primitives.ItemSelectionChangedEventArgs e)
        {
            //if (!loadingNewData)
            //{
            //    MountFlagsComboBox.SelectedItem = GetCurrentMountFlag();
            //}
        }
    }
}
