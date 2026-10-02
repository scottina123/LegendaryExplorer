using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.SharedUI;

namespace LegendaryExplorer.Dialogs
{
    public partial class CloneMaterialInstanceDialog : Window
    {
        private readonly int localTextureCount;

        public bool CloneTextures { get; private set; }

        public string NameSuffix => SuffixTextBox.Text.Trim();

        public CloneMaterialInstanceDialog(Window owner, int localTextureCount, int importedTextureCount, int numClones)
        {
            this.localTextureCount = localTextureCount;
            Owner = owner;
            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);

            QuestionText.Text = numClones == 1
                ? "Do you also want to clone this MIC's textures in TextureParameterValues?"
                : $"Do you also want to clone the textures in TextureParameterValues for each of the {numClones} MIC clones?";
            TextureDetailsText.Text = localTextureCount == 0
                ? "This MIC has no texture parameters stored in this package that can be cloned."
                : $"{localTextureCount} texture(s) in this package will be cloned and relinked to each new MIC. Parameters using the same texture will use a single texture clone per MIC.";
            if (importedTextureCount > 0)
            {
                ImportedTexturesText.Text = $"{importedTextureCount} texture(s) are imports from other packages and will keep their existing references.";
                ImportedTexturesText.Visibility = Visibility.Visible;
            }
            UpdateSuffixValidation();
        }

        private void SuffixTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSuffixValidation();
        }

        private void UpdateSuffixValidation()
        {
            if (CloneMaterialOnlyButton == null || CloneWithTexturesButton == null || SuffixValidationText == null)
            {
                return;
            }

            bool valid = NameSuffix.All(character => char.IsLetterOrDigit(character) || character == '_');
            CloneMaterialOnlyButton.IsEnabled = valid;
            CloneWithTexturesButton.IsEnabled = localTextureCount > 0 && valid;
            SuffixValidationText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        }

        private void CloneMaterialOnly_Click(object sender, RoutedEventArgs e)
        {
            CloneTextures = false;
            DialogResult = true;
        }

        private void CloneWithTextures_Click(object sender, RoutedEventArgs e)
        {
            CloneTextures = true;
            DialogResult = true;
        }
    }
}
