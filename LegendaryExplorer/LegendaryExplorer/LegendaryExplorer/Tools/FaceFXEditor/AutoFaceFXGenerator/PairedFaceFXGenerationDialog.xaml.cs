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
    /// Dialog for configuring female and male FaceFX generation in one step.
    /// </summary>
    public partial class PairedFaceFXGenerationDialog : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public PairedFaceFXGenerationTarget Female { get; }
        public PairedFaceFXGenerationTarget Male { get; }
        public bool CanGenerate => Female.Generate || Male.Generate;
        public bool Confirmed { get; private set; }

        public PairedFaceFXGenerationDialog(int femaleLineCount, int maleLineCount, Window owner = null,
            MEGame game = MEGame.LE3, string femaleTargetText = null, string maleTargetText = null)
        {
            var availableSpecies = FaceFXSpeciesCatalog.GetForGame(game)
                .Select(FaceFXSpeciesCatalog.GetDisplayName)
                .ToList();
            Female = new PairedFaceFXGenerationTarget("Female", femaleLineCount, femaleTargetText,
                FaceFXSpecies.HumanFemale, availableSpecies);
            Male = new PairedFaceFXGenerationTarget("Male", maleLineCount, maleTargetText,
                FaceFXSpecies.HumanMale, availableSpecies);
            Female.PropertyChanged += Target_PropertyChanged;
            Male.PropertyChanged += Target_PropertyChanged;

            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
            DataContext = this;

            if (owner != null)
            {
                Owner = owner;
            }
        }

        private void Target_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PairedFaceFXGenerationTarget.Generate))
            {
                OnPropertyChanged(nameof(CanGenerate));
            }
        }

        private void GenerateButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CanGenerate)
            {
                return;
            }

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

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class PairedFaceFXGenerationTarget : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public string GenderName { get; }
        public int LineCount { get; }
        public string LineCountText => $"{LineCount} {GenderName.ToLowerInvariant()} line{(LineCount == 1 ? string.Empty : "s")}";
        public string TargetText { get; }
        public Visibility TargetVisibility => string.IsNullOrWhiteSpace(TargetText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        public bool IsAvailable => LineCount > 0;
        public List<string> AvailableSpecies { get; }
        public FaceFXSpecies SelectedSpeciesEnum => FaceFXSpeciesCatalog.FromDisplayName(SelectedSpecies);

        private bool _generate;
        public bool Generate
        {
            get => _generate;
            set
            {
                if (_generate == value || value && !IsAvailable)
                {
                    return;
                }

                _generate = value;
                OnPropertyChanged();
            }
        }

        private string _selectedSpecies;
        public string SelectedSpecies
        {
            get => _selectedSpecies;
            set
            {
                _selectedSpecies = value;
                OnPropertyChanged();
            }
        }

        private float _lipSyncIntensity = 1.0f;
        public float LipSyncIntensity
        {
            get => _lipSyncIntensity;
            set
            {
                _lipSyncIntensity = value;
                OnPropertyChanged();
            }
        }

        private float _blinkFrequency = 0.2f;
        public float BlinkFrequency
        {
            get => _blinkFrequency;
            set
            {
                _blinkFrequency = value;
                OnPropertyChanged();
            }
        }

        private bool _generateBlinkAnimation = true;
        public bool GenerateBlinkAnimation
        {
            get => _generateBlinkAnimation;
            set
            {
                _generateBlinkAnimation = value;
                OnPropertyChanged();
            }
        }

        internal PairedFaceFXGenerationTarget(string genderName, int lineCount, string targetText,
            FaceFXSpecies defaultSpecies, List<string> availableSpecies)
        {
            GenderName = genderName;
            LineCount = lineCount;
            TargetText = targetText;
            AvailableSpecies = new List<string>(availableSpecies);
            SelectedSpecies = FaceFXSpeciesCatalog.GetDisplayName(defaultSpecies);
            _generate = IsAvailable;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
