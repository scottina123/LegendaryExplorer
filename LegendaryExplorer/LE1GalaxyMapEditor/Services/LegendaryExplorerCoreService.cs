using LegendaryExplorerCore;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;

namespace LE1GalaxyMapEditor.Services;

/// <summary>Initialises the shared Legendary Explorer Core runtime once per process.</summary>
public static class LegendaryExplorerCoreService
{
    private static readonly object Sync = new();
    private static bool _initialized;

    public static event Action<string>? PackageSaveFailed;

    public static void Initialize(TaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            LegendaryExplorerCoreLib.InitLib(
                scheduler,
                message => PackageSaveFailed?.Invoke(message),
                objectDBsToLoad: [MEGame.LE1],
                usePropertyDBLazyLoad: true);
            LE1Directory.ReloadDefaultGamePath();
            _initialized = true;
        }
    }
}
