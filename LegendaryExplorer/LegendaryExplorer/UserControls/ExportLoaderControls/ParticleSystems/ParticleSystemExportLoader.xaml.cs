using System.Linq;
using System.Windows;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;

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

        public override bool CanParse(ExportEntry exportEntry) => CanPreview(exportEntry);

        /// <summary>
        /// Determines whether the selected export is, or is connected to, a particle system that can be previewed.
        /// </summary>
        public static bool CanPreview(ExportEntry exportEntry) =>
            exportEntry is { IsDefaultObject: false } && FindParticleSystemEntry(exportEntry) is not null;

        /// <summary>
        /// Resolves the particle system represented by a direct particle-system selection, an Emitter actor's
        /// ParticleSystemComponent, or a ParticleEmitter referenced by a particle system.
        /// </summary>
        public static ExportEntry ResolveParticleSystem(ExportEntry exportEntry, PackageCache packageCache = null) =>
            FindParticleSystemEntry(exportEntry) switch
            {
                ExportEntry particleSystem => particleSystem,
                ImportEntry particleSystemImport when packageCache is not null =>
                    EntryImporter.ResolveImport(particleSystemImport, packageCache),
                _ => null
            };

        private static IEntry FindParticleSystemEntry(ExportEntry exportEntry)
        {
            if (exportEntry is null)
            {
                return null;
            }

            if (exportEntry.ClassName == "ParticleSystem")
            {
                return exportEntry;
            }

            if (exportEntry.IsA("Emitter"))
            {
                IEntry component = exportEntry.GetProperty<ObjectProperty>("ParticleSystemComponent")
                    ?.ResolveToEntry(exportEntry.FileRef);
                if (component is ExportEntry particleSystemComponent)
                {
                    IEntry template = particleSystemComponent.GetProperty<ObjectProperty>("Template")
                        ?.ResolveToEntry(particleSystemComponent.FileRef);
                    if (template?.ClassName == "ParticleSystem")
                    {
                        return template;
                    }
                }
            }

            if (exportEntry.IsA("ParticleEmitter"))
            {
                return exportEntry.FileRef.Exports.FirstOrDefault(particleSystem =>
                    particleSystem.ClassName == "ParticleSystem"
                    && particleSystem.GetProperty<ArrayProperty<ObjectProperty>>("Emitters")
                        ?.Any(emitterReference => emitterReference.Value == exportEntry.UIndex) == true);
            }

            return null;
        }

        public override void LoadExport(ExportEntry exportEntry)
        {
            CurrentLoadedExport = exportEntry;
            if (VfxPreviewEnabledCheckBox.IsChecked == true)
            {
                LoadParticleSystemPreview(exportEntry);
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
                LoadParticleSystemPreview(exportEntry);
            }
            else
            {
                VfxPreview.UnloadExport();
            }
        }

        private void LoadParticleSystemPreview(ExportEntry exportEntry)
        {
            try
            {
                ExportEntry particleSystem = ResolveParticleSystem(exportEntry, VfxPreview.RenderContext.PackageCache);
                if (particleSystem is not null)
                {
                    VfxPreview.LoadExport(particleSystem);
                }
                else
                {
                    VfxPreview.ShowUnavailable("The selected emitter's connected particle system could not be resolved.");
                }
            }
            catch (System.Exception exception)
            {
                VfxPreview.ShowUnavailable($"The selected emitter's connected particle system could not be resolved: {exception.Message}");
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
