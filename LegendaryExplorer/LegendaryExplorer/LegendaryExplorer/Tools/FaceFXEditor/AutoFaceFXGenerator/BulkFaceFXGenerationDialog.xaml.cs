using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using LegendaryExplorer.SharedUI;

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
        public List<string> AvailableSpecies { get; } = new List<string>
        {
            "Human Female",
            "Human Male",
            "Human Child",
            "Asari",
            "Krogan",
            "Drell",
            "Turian",
            "Salarian",
            "Quarian",
            "Geth",
            "Elcor",
            "Hanar",
            "Volus",
            "Batarian",
            "Vorcha",
            "Prothean",
            "Yahg"
        };

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

        /// <summary>
        /// The selected species as enum value
        /// </summary>
        public FaceFXSpecies SelectedSpeciesEnum => SelectedSpecies switch
        {
            "Human Male" => FaceFXSpecies.HumanMale,
            "Human Child" => FaceFXSpecies.HumanChild,
            "Asari" => FaceFXSpecies.Asari,
            "Krogan" => FaceFXSpecies.Krogan,
            "Drell" => FaceFXSpecies.Drell,
            "Turian" => FaceFXSpecies.Turian,
            "Salarian" => FaceFXSpecies.Salarian,
            "Quarian" => FaceFXSpecies.Quarian,
            "Geth" => FaceFXSpecies.Geth,
            "Elcor" => FaceFXSpecies.Elcor,
            "Hanar" => FaceFXSpecies.Hanar,
            "Volus" => FaceFXSpecies.Volus,
            "Batarian" => FaceFXSpecies.Batarian,
            "Vorcha" => FaceFXSpecies.Vorcha,
            "Prothean" => FaceFXSpecies.Prothean,
            "Yahg" => FaceFXSpecies.Yahg,
            _ => FaceFXSpecies.HumanFemale
        };

        /// <summary>
        /// Whether the user confirmed generation
        /// </summary>
        public bool Confirmed { get; private set; }

        public BulkFaceFXGenerationDialog(int lineCount, Window owner = null)
        {
            LineCountText = $"Generate FaceFX for {lineCount} lines";

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
