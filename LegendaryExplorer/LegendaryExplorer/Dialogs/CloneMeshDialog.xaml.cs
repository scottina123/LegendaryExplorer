using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;

namespace LegendaryExplorer.Dialogs
{
    public partial class CloneMeshDialog : Window
    {
        private readonly bool hasLocalAssets;

        public bool CloneMaterialsAndTextures { get; private set; }

        public string NameSuffix => SuffixTextBox.Text.Trim();

        public CloneMeshDialog(Window owner, string meshType, MeshMaterialCloneInfo info, int numClones)
        {
            int localMaterialCount = info.Materials.OfType<ExportEntry>().Count();
            int localTextureCount = info.Textures.OfType<ExportEntry>().Count();
            hasLocalAssets = localMaterialCount > 0 || localTextureCount > 0;
            Owner = owner;
            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
            Title = $"Clone {meshType}";

            QuestionText.Text = numClones == 1
                ? "Do you also want to clone all material slots and their textures?"
                : $"Do you also want to clone all material slots and their textures for each of the {numClones} mesh clones?";
            AssetDetailsText.Text = hasLocalAssets
                ? $"Across {info.MaterialSlotCount} material slot(s), {localMaterialCount} local material(s) and {localTextureCount} local texture(s) will be cloned and relinked to each new mesh. This includes parent materials and their textures. Repeated references share one clone per mesh copy."
                : "This mesh has no materials or textures stored in this package that can be cloned.";
            if (info.SharedImports.Count > 0)
            {
                ImportedAssetsText.Text = $"{info.SharedImports.Count} asset(s) are imports from other packages and will keep their existing references. This includes imported materials, textures and material expressions.";
                ImportedAssetsText.Visibility = Visibility.Visible;
            }
            UpdateSuffixValidation();
        }

        private void SuffixTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSuffixValidation();
        }

        private void UpdateSuffixValidation()
        {
            if (CloneMeshOnlyButton == null || CloneWithAssetsButton == null || SuffixValidationText == null)
            {
                return;
            }

            bool valid = NameSuffix.All(character => char.IsLetterOrDigit(character) || character == '_');
            CloneMeshOnlyButton.IsEnabled = valid;
            CloneWithAssetsButton.IsEnabled = hasLocalAssets && valid;
            SuffixValidationText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        }

        private void CloneMeshOnly_Click(object sender, RoutedEventArgs e)
        {
            CloneMaterialsAndTextures = false;
            DialogResult = true;
        }

        private void CloneWithAssets_Click(object sender, RoutedEventArgs e)
        {
            CloneMaterialsAndTextures = true;
            DialogResult = true;
        }
    }
}
