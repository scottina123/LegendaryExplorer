using System.Windows;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Scottina's Lightmass Baker: the fork's static lighting baker (C# and native backends), opened from the Level Editor's Build menu. It edits the Level
/// Editor's own lightmass settings (its DataContext), and its Create Lightmass runs the editor's bake, whose progress the editor shows
/// </summary>
public partial class ScottinaLightmassBakerWindow : Window
{
    private readonly LevelEditor editor;

    public ScottinaLightmassBakerWindow(LevelEditor editor)
    {
        this.editor = editor;
        Owner = editor;
        DataContext = editor;
        InitializeComponent();
    }

    private void Bake_Click(object sender, RoutedEventArgs e)
    {
        if (editor.GenerateStaticLightingCommand?.CanExecute(null) != true) return;
        // Closed first: the bake shows its progress in the Level Editor, and may ask questions there
        Close();
        editor.GenerateStaticLightingCommand.Execute(null);
    }
}
