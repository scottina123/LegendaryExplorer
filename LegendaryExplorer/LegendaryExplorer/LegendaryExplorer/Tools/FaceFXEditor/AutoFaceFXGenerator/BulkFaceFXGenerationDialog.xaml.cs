using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator
{
    /// <summary>
    /// Dialog for configuring bulk FaceFX generation options
    /// </summary>
    public partial class BulkFaceFXGenerationDialog : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public string LineCountText { get; }

        // Species selection
        public List<string> AvailableSpecies { get; }

        private string _selectedSpecies = "Human Female";
        public string SelectedSpecies
        {
            get => _selectedSpecies;
            set { _selectedSpecies = value; OnPropertyChanged(); }
        }

        private float _lipSyncIntensity = 1.0f;
        public float LipSyncIntensity
        {
            get => _lipSyncIntensity;
            set { _lipSyncIntensity = value; OnPropertyChanged(); }
        }

        private float _blinkFrequency = 0.2f;
        public float BlinkFrequency
        {
            get => _blinkFrequency;
            set { _blinkFrequency = value; OnPropertyChanged(); }
        }

        private bool _generateBlinkAnimation = true;
        public bool GenerateBlinkAnimation
        {
            get => _generateBlinkAnimation;
            set { _generateBlinkAnimation = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// The selected species as enum value
        /// </summary>
        public FaceFXSpecies SelectedSpeciesEnum => FaceFXSpeciesCatalog.FromDisplayName(SelectedSpecies);

        /// <summary>
        /// Whether the user confirmed generation
        /// </summary>
        public bool Confirmed { get; private set; }

        public BulkFaceFXGenerationDialog(int lineCount, Window owner = null, FaceFXSpecies? defaultSpecies = null,
            MEGame game = MEGame.LE3)
        {
            LineCountText = $"Generate FaceFX for {lineCount} lines";
            AvailableSpecies = FaceFXSpeciesCatalog.GetForGame(game)
                .Select(FaceFXSpeciesCatalog.GetDisplayName)
                .ToList();

            if (defaultSpecies.HasValue && FaceFXSpeciesCatalog.GetForGame(game).Contains(defaultSpecies.Value))
            {
                SelectedSpecies = FaceFXSpeciesCatalog.GetDisplayName(defaultSpecies.Value);
            }

            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
            DataContext = this;

            if (owner != null)
            {
                Owner = owner;
            }
        }

        private void GenerateButton_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            DialogResult = false;
            Close();
        }
    }
}
