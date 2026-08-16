using System.Windows;
using System.Windows.Threading;
using LE1GalaxyMapEditor.Workflows.Ports;

namespace LE1GalaxyMapEditor.Presentation;

public sealed class DispatcherDeferredScheduler : IDeferredScheduler
{
    private DispatcherTimer? _timer;

    public void Schedule(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || !dispatcher.CheckAccess())
        {
            action();
            return;
        }

        Cancel();
        _timer = new DispatcherTimer(delay, DispatcherPriority.Background, OnTick, dispatcher);
        _timer.Tag = action;
        _timer.Start();
    }

    public void Cancel()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    public void Dispose() => Cancel();

    private void OnTick(object? sender, EventArgs eventArgs)
    {
        var action = _timer?.Tag as Action;
        Cancel();
        action?.Invoke();
    }
}
