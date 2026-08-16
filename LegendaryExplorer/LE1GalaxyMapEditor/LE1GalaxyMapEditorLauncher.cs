using System.IO;
using System.Windows;
using LE1GalaxyMapEditor.Services;
using LE1GalaxyMapEditor.ViewModels;

namespace LE1GalaxyMapEditor;

/// <summary>Creates editor windows for an initialized desktop host such as LEX.</summary>
public static class LE1GalaxyMapEditorLauncher
{
    public static Window CreateWindow(string? sourceFolder = null)
    {
        LegendaryExplorerCoreService.Initialize(TaskScheduler.FromCurrentSynchronizationContext());

        var baseGameSettings = new BaseGameSettingsStore();
        var viewModel = new MainViewModel(
            new CsvGalaxyMapLoader(),
            new GalaxyMapTextureService(),
            baseGameTlkLocale: baseGameSettings.LoadLocale(),
            saveBaseGameLocale: baseGameSettings.SaveLocale);

        viewModel.LoadRememberedWorkspace();
        if (!string.IsNullOrWhiteSpace(sourceFolder) && Directory.Exists(sourceFolder))
        {
            viewModel.LoadFolder(sourceFolder);
        }

        var editor = new MainWindow { DataContext = viewModel };
        editor.ContentRendered += Editor_OnContentRendered;
        return editor;
    }

    private static void Editor_OnContentRendered(object? sender, EventArgs eventArgs)
    {
        if (sender is not MainWindow { DataContext: MainViewModel viewModel } editor)
        {
            return;
        }

        editor.ContentRendered -= Editor_OnContentRendered;
        viewModel.WarmPlanetPreviewTextures();
    }
}
