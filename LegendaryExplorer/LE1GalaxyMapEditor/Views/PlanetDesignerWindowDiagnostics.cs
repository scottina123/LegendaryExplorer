using System.Threading;

namespace LE1GalaxyMapEditor.Views;

/// <summary>
/// Read-only counters for observing the Planet Designer preview lifecycle.
/// The counters deliberately describe activity without imposing performance
/// thresholds or changing when the window schedules and renders frames.
/// </summary>
public sealed class PlanetDesignerWindowDiagnostics
{
    private long _previewRequests;
    private long _scheduledPreviewOperations;
    private long _scheduledPreviewDispatches;
    private long _timerTicks;
    private long _renderAttempts;
    private long _renderSkipsUnavailable;
    private long _renderSkipsBusy;
    private long _framesPresented;
    private long _rendererInitializationsStarted;
    private long _rendererInitializationsCompleted;
    private long _rendererInitializationFailures;
    private long _rendererDisposals;

    public PlanetDesignerWindowDiagnosticSnapshot Snapshot() => new(
        Interlocked.Read(ref _previewRequests),
        Interlocked.Read(ref _scheduledPreviewOperations),
        Interlocked.Read(ref _scheduledPreviewDispatches),
        Interlocked.Read(ref _timerTicks),
        Interlocked.Read(ref _renderAttempts),
        Interlocked.Read(ref _renderSkipsUnavailable),
        Interlocked.Read(ref _renderSkipsBusy),
        Interlocked.Read(ref _framesPresented),
        Interlocked.Read(ref _rendererInitializationsStarted),
        Interlocked.Read(ref _rendererInitializationsCompleted),
        Interlocked.Read(ref _rendererInitializationFailures),
        Interlocked.Read(ref _rendererDisposals));

    internal void RecordPreviewRequest() => Interlocked.Increment(ref _previewRequests);
    internal void RecordScheduledPreviewOperation() => Interlocked.Increment(ref _scheduledPreviewOperations);
    internal void RecordScheduledPreviewDispatch() => Interlocked.Increment(ref _scheduledPreviewDispatches);
    internal void RecordTimerTick() => Interlocked.Increment(ref _timerTicks);
    internal void RecordRenderAttempt() => Interlocked.Increment(ref _renderAttempts);
    internal void RecordRenderSkipUnavailable() => Interlocked.Increment(ref _renderSkipsUnavailable);
    internal void RecordRenderSkipBusy() => Interlocked.Increment(ref _renderSkipsBusy);
    internal void RecordFramePresented() => Interlocked.Increment(ref _framesPresented);
    internal void RecordRendererInitializationStarted() => Interlocked.Increment(ref _rendererInitializationsStarted);
    internal void RecordRendererInitializationCompleted() => Interlocked.Increment(ref _rendererInitializationsCompleted);
    internal void RecordRendererInitializationFailure() => Interlocked.Increment(ref _rendererInitializationFailures);
    internal void RecordRendererDisposal() => Interlocked.Increment(ref _rendererDisposals);
}

public readonly record struct PlanetDesignerWindowDiagnosticSnapshot(
    long PreviewRequests,
    long ScheduledPreviewOperations,
    long ScheduledPreviewDispatches,
    long TimerTicks,
    long RenderAttempts,
    long RenderSkipsUnavailable,
    long RenderSkipsBusy,
    long FramesPresented,
    long RendererInitializationsStarted,
    long RendererInitializationsCompleted,
    long RendererInitializationFailures,
    long RendererDisposals)
{
    public static PlanetDesignerWindowDiagnosticSnapshot operator -(
        PlanetDesignerWindowDiagnosticSnapshot current,
        PlanetDesignerWindowDiagnosticSnapshot previous) => new(
        current.PreviewRequests - previous.PreviewRequests,
        current.ScheduledPreviewOperations - previous.ScheduledPreviewOperations,
        current.ScheduledPreviewDispatches - previous.ScheduledPreviewDispatches,
        current.TimerTicks - previous.TimerTicks,
        current.RenderAttempts - previous.RenderAttempts,
        current.RenderSkipsUnavailable - previous.RenderSkipsUnavailable,
        current.RenderSkipsBusy - previous.RenderSkipsBusy,
        current.FramesPresented - previous.FramesPresented,
        current.RendererInitializationsStarted - previous.RendererInitializationsStarted,
        current.RendererInitializationsCompleted - previous.RendererInitializationsCompleted,
        current.RendererInitializationFailures - previous.RendererInitializationFailures,
        current.RendererDisposals - previous.RendererDisposals);
}
