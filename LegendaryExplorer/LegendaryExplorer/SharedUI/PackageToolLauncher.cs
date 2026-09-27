using System;
using System.Threading.Tasks;
using System.Windows;
using LegendaryExplorer.DialogueEditor;
using LegendaryExplorer.Tools.AnimationImporterExporter;
using LegendaryExplorer.Tools.FaceFXEditor;
using LegendaryExplorer.Tools.GalaxyMapEditor;
using LegendaryExplorer.Tools.InterpEditor;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.Meshplorer;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.Tools.PathfindingEditor;
using LegendaryExplorer.Tools.PathfindingNetworkEditor;
using LegendaryExplorer.Tools.PlotEditor;
using LegendaryExplorer.Tools.Sequence_Editor;
using LegendaryExplorer.Tools.SFXGalaxyEditor;
using LegendaryExplorer.Tools.Soundplorer;
using LegendaryExplorer.Tools.TextureStudio;
using LegendaryExplorer.Tools.WwiseEditor;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.SharedUI;

/// <summary>Opens another package in a fresh instance of the originating tool.</summary>
internal static class PackageToolLauncher
{
    internal static Task<Window> OpenAsync(Window source, string filePath) => source switch
    {
        PackageEditorWindow editor => Open<PackageEditorWindow>(window =>
            window.LoadFile(filePath, goToEntry: editor.SelectedItem?.Entry?.InstancedFullPath)),
        SequenceEditorWPF editor => Open<SequenceEditorWPF>(window =>
            window.LoadFileAndGoTo(filePath, goToEntry: (editor.SelectedItem?.Entry ?? editor.SelectedSequence)?.InstancedFullPath)),
        DialogueEditorWindow => Open<DialogueEditorWindow>(window => window.LoadFile(filePath)),
        AnimationImporterExporterWindow => Open<AnimationImporterExporterWindow>(window => window.LoadFile(filePath)),
        FaceFXEditorWindow => Open<FaceFXEditorWindow>(window => window.LoadFile(filePath)),
        MeshplorerWindow => Open<MeshplorerWindow>(window => window.LoadFile(filePath)),
        WwiseEditorWindow => Open<WwiseEditorWindow>(window => window.LoadFile(filePath)),
        InterpEditorWindow => Open<InterpEditorWindow>(window => window.LoadFile(filePath)),
        SoundplorerWPF => Open<SoundplorerWPF>(window => window.LoadFile(filePath)),
        PlotEditorWindow => Open<PlotEditorWindow>(window => window.LoadFile(filePath)),
        MasterTextureSelector => Open<MasterTextureSelector>(window => window.LoadFile(filePath)),
        SFXGalaxyEditorWindow => Open<SFXGalaxyEditorWindow>(window => window.LoadFile(filePath)),
        PathfindingEditorWindow => Show(new PathfindingEditorWindow(filePath)),
        PathfindingNetworkEditorWindow => Open<PathfindingNetworkEditorWindow>(window => window.LoadFile(filePath)),
        LevelEditor => OpenAsync<LevelEditor>(window => window.LoadFileAsync(filePath)),
        GalaxyMapEditor => OpenAsync<GalaxyMapEditor>(window => window.LoadFileAsync(filePath)),
        TextureStudioWindow editor => OpenAsync<TextureStudioWindow>(window =>
            window.LoadPackageAsync(filePath, editor.SelectedInstance?.ExportPath)),
        ExportLoaderHostedWindow editor => Show(editor.CreateForPackageVersion(filePath)),
        LECLDataEditorWindow => Show(CreateMetadataEditor(source, filePath)),
        _ => throw new NotSupportedException($"Opening another package is not supported by {source?.GetType().Name ?? "this tool"}.")
    };

    private static Window CreateMetadataEditor(Window source, string filePath)
    {
        using var package = MEPackageHandler.OpenMEPackage(filePath);
        return new LECLDataEditorWindow(source.Owner, package);
    }

    private static Task<Window> Show(Window window)
    {
        window.Show();
        return Task.FromResult(window);
    }

    private static Task<Window> Open<T>(Action<T> load) where T : Window, new() => OpenAsync<T>(window =>
    {
        load(window);
        return Task.CompletedTask;
    });

    private static async Task<Window> OpenAsync<T>(Func<T, Task> load) where T : Window, new()
    {
        var window = new T();
        try
        {
            window.Show();
            await load(window);
            return window;
        }
        catch
        {
            window.Close();
            throw;
        }
    }
}
