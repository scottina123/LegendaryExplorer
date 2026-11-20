using System;
using System.Threading;

namespace LegendaryExplorer.Lightmaps;

/// <summary>
/// Keeps a bake's garbage from piling up. Its steps (solver batches, lightmap atlases) each allocate hundreds of megabytes to gigabytes of
/// short-lived arrays; at its own pace the GC lets them build up to double a bake's memory. A full collection after a step costs a fraction of
/// a second, so one runs whenever a few gigabytes have been allocated since the last
/// </summary>
public static class BakeMemory
{
    const long Budget = 4L << 30;
    static long collectedAt;
    static readonly Lock gate = new();

    /// <summary>Collects if <see cref="Budget"/> bytes have been allocated (by any thread) since the last collection here</summary>
    public static void Step()
    {
        long allocated = GC.GetTotalAllocatedBytes();
        if (allocated - Interlocked.Read(ref collectedAt) < Budget) return;
        lock (gate)
        {
            if (allocated - collectedAt < Budget) return;
            collectedAt = allocated;
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
    }
}
