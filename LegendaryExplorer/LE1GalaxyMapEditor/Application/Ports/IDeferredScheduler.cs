namespace LE1GalaxyMapEditor.Workflows.Ports;

public interface IDeferredScheduler : IDisposable
{
    void Schedule(TimeSpan delay, Action action);
    void Cancel();
}
