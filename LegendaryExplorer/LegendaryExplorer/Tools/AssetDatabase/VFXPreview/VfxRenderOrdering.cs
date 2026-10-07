using System.Collections.Generic;

namespace LegendaryExplorer.Tools.AssetDatabase.VFXPreview;

internal static class VfxRenderOrdering
{
    /// <summary>
    /// Interleaves ordered emitter sequences by their next drawable's depth. A depth-sorted emitter stays
    /// back-to-front; unsorted and age-sorted emitters retain their authored order without preventing other
    /// emitters from sorting. Equal depths keep emitter order stable.
    /// </summary>
    internal static IEnumerable<(int BatchIndex, int ParticleIndex)> MergeBackToFront(
        IReadOnlyList<IReadOnlyList<float>> depths)
    {
        var pending = new PriorityQueue<(int BatchIndex, int ParticleIndex), (float Depth, int BatchIndex)>();
        for (int batchIndex = 0; batchIndex < depths.Count; batchIndex++)
        {
            if (depths[batchIndex].Count > 0)
            {
                pending.Enqueue((batchIndex, 0), (-depths[batchIndex][0], batchIndex));
            }
        }

        while (pending.TryDequeue(out var draw, out _))
        {
            yield return draw;
            int nextParticle = draw.ParticleIndex + 1;
            if (nextParticle < depths[draw.BatchIndex].Count)
            {
                pending.Enqueue((draw.BatchIndex, nextParticle),
                    (-depths[draw.BatchIndex][nextParticle], draw.BatchIndex));
            }
        }
    }
}
