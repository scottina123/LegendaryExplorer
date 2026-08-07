using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Tools.LevelEditor;

public interface IActorEditorContext
{
    LevelEditorRenderContext RenderContext { get; }
    bool IsApplyingUndoRedo { get; }
    bool PreviewConfiguredActorAnimations => false;
    PackageCache PackageCache => RenderContext.PackageCache;
}
