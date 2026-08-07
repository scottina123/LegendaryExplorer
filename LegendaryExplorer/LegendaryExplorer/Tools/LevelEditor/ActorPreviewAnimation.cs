using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using System.Collections.Generic;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Lightweight, lazily resolved description of the animation pose configured on a level actor.
/// Keeping this as references and a name avoids animation decompression during the initial actor scan.
/// </summary>
internal sealed class ActorPreviewAnimation(IMEPackage sourcePackage, NameReference animationName,
    IReadOnlyList<int> animSetUIndexes)
{
    internal const float RepresentativePoseFraction = 0.35f;

    public string DisplayName => animationName.Instanced;

    public AnimSequence Resolve(MeshRenderContext context) =>
        context.ResolveConfiguredAnimation(sourcePackage, animSetUIndexes, animationName);
}
