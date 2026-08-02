using System.Windows;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.UserControls.ExportLoaderControls
{
    /// <summary>
    /// Interaction logic for ParticleSystemExportLoader.xaml
    /// </summary>
    public partial class ParticleSystemExportLoader : ExportLoaderControl
    {
        public ParticleSystemExportLoader() : base("Particle System Viewer")
        {
            InitializeComponent();
        }

        public override bool CanParse(ExportEntry exportEntry) =>
            !exportEntry.IsDefaultObject && exportEntry.ClassName == "ParticleSystem";

        public override void LoadExport(ExportEntry exportEntry)
        {
            CurrentLoadedExport = exportEntry;
            if (VfxPreviewEnabledCheckBox.IsChecked == true)
            {
                VfxPreview.LoadExport(exportEntry);
            }
            else
            {
                VfxPreview.UnloadExport();
            }
        }

        private void VfxPreviewEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (VfxPreviewEnabledCheckBox.IsChecked == true && CurrentLoadedExport is { } exportEntry)
            {
                VfxPreview.LoadExport(exportEntry);
            }
            else
            {
                VfxPreview.UnloadExport();
            }
        }

        public override void UnloadExport()
        {
            VfxPreview.UnloadExport();
            CurrentLoadedExport = null;
        }

        public override void PopOut()
        {
            if (CurrentLoadedExport != null)
            {
                ExportLoaderHostedWindow elhw = new ExportLoaderHostedWindow(new ParticleSystemExportLoader(), CurrentLoadedExport)
                {
                    Title = $"Particle System - {CurrentLoadedExport.UIndex} {CurrentLoadedExport.InstancedFullPath} - {CurrentLoadedExport.FileRef.FilePath}"
                };
                elhw.Show();
            }
        }

        public override void Dispose()
        {
        }
    }
}
