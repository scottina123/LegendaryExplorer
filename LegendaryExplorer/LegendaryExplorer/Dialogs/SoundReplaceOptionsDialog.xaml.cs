using System.IO;
using System.Windows;
using System.Windows.Input;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Misc;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace LegendaryExplorer.Dialogs
{
    /// <summary>
    /// Interaction logic for SoundReplaceOptionsDialog.xaml
    /// </summary>
    public partial class SoundReplaceOptionsDialog : NotifyPropertyChangedWindowBase
    {
        public ObservableCollectionExtended<int> SampleRates { get; } = new();
        private static readonly int[] AcceptedSampleRates = { 24000, 32000, 44100 }; //may add more later

        public ObservableCollectionExtended<MEGame> SupportedGames { get; } = new()
        {
            MEGame.ME3, MEGame.LE2, MEGame.LE3
        };

        private bool _showBulkReplaceOption;

        public bool ShowBulkReplaceOption
        {
            get => _showBulkReplaceOption;
            set
            {
                if (SetProperty(ref _showBulkReplaceOption, value))
                {
                    OnPropertyChanged(nameof(ShowBulkReplaceFolderPicker));
                }
            }
        }

        private bool _bulkReplaceSameExportName;

        public bool BulkReplaceSameExportName
        {
            get => _bulkReplaceSameExportName;
            set
            {
                if (SetProperty(ref _bulkReplaceSameExportName, value))
                {
                    OnPropertyChanged(nameof(ShowBulkReplaceFolderPicker));
                }
            }
        }

        private string _bulkReplaceFolder;

        public string BulkReplaceFolder
        {
            get => _bulkReplaceFolder;
            set => SetProperty(ref _bulkReplaceFolder, value);
        }

        public bool ShowBulkReplaceFolderPicker => ShowBulkReplaceOption && BulkReplaceSameExportName;

        private bool _showUpdateEventsCheckbox;

        public bool ShowUpdateEventsCheckbox
        {
            get => _showUpdateEventsCheckbox;
            set => SetProperty(ref _showUpdateEventsCheckbox, value);
        }

        private bool _showDestAFCFile;

        public bool ShowDestAFCFile
        {
            get => _showDestAFCFile;
            set => SetProperty(ref _showDestAFCFile, value);
        }

        private MEGame _selectedGame;

        public MEGame SelectedGame
        {
            get => _selectedGame;
            set
            {
                if (SupportedGames.Contains(value))
                {
                    SetProperty(ref _selectedGame, value);
                }
            }
        }

        public WwiseConversionSettingsPackage ChosenSettings;

        public SoundReplaceOptionsDialog(bool showUpdateEvents = true, MEGame game = MEGame.LE3, string destAFCFile = null, bool showBulkReplaceOption = false)
        {
            DataContext = this;
            SampleRates.AddRange(AcceptedSampleRates);
            LoadCommands();
            InitializeComponent();
            ShowUpdateEventsCheckbox = showUpdateEvents;
            SelectedGame = game;
            SampleRate_Combobox.SelectedIndex = 0;
            AfcFileDest_TextBox.Text = destAFCFile;
            ShowDestAFCFile = !string.IsNullOrWhiteSpace(destAFCFile);
            ShowBulkReplaceOption = showBulkReplaceOption;
        }

        public SoundReplaceOptionsDialog(Window w, bool showUpdateEvents = true, MEGame game = MEGame.LE3, string destAFCFile = null, bool showBulkReplaceOption = false) : this(showUpdateEvents, game, destAFCFile, showBulkReplaceOption)
        {
            Owner = w;
        }

        public ICommand ConvertAudioCommand { get; private set; }
        public ICommand BrowseBulkReplaceFolderCommand { get; private set; }

        void LoadCommands()
        {
            ConvertAudioCommand = new GenericCommand(ReturnSettings, CanReturnSettings);
            BrowseBulkReplaceFolderCommand = new GenericCommand(BrowseBulkReplaceFolder);
        }

        private void ReturnSettings()
        {
            ChosenSettings = new WwiseConversionSettingsPackage
            {
                TargetSamplerate = (int)SampleRate_Combobox.SelectedItem,
                UpdateReferencedEvents = UpdateEvents_CheckBox.IsChecked.GetValueOrDefault(false),
                TargetGame = SelectedGame,
                DestinationAFCFile = Path.GetFileNameWithoutExtension(AfcFileDest_TextBox.Text), // Just remove it so we don't have to deal with it
                BulkReplaceSameExportName = BulkReplaceSameExportName,
                BulkReplaceFolder = BulkReplaceSameExportName ? Path.GetFullPath(BulkReplaceFolder) : null
            };
            DialogResult = true;
            Close();
        }

        private bool CanReturnSettings()
        {
            if (SampleRate_Combobox.SelectedIndex < 0)
            {
                return false;
            }

            if (ShowDestAFCFile && string.IsNullOrWhiteSpace(AfcFileDest_TextBox.Text))
            {
                return false;
            }

            if (BulkReplaceSameExportName && (string.IsNullOrWhiteSpace(BulkReplaceFolder) || !Directory.Exists(BulkReplaceFolder)))
            {
                return false;
            }

            return true;
        }

        private void BrowseBulkReplaceFolder()
        {
            var dlg = new CommonOpenFileDialog("Select folder containing package files")
            {
                IsFolderPicker = true
            };

            if (Directory.Exists(BulkReplaceFolder))
            {
                dlg.InitialDirectory = BulkReplaceFolder;
            }

            if (dlg.ShowDialog(this) == CommonFileDialogResult.Ok)
            {
                BulkReplaceFolder = dlg.FileName;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
    public class WwiseConversionSettingsPackage
    {
        public int TargetSamplerate = 0;
        public bool UpdateReferencedEvents = true;
        public MEGame TargetGame = MEGame.LE3;
        public string DestinationAFCFile; // No default to ensure it must be set.
        public bool BulkReplaceSameExportName;
        public string BulkReplaceFolder;
    }
}
