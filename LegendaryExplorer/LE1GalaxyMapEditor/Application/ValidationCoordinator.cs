using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Services;
using LE1GalaxyMapEditor.Workflows.Ports;

namespace LE1GalaxyMapEditor.Workflows;

public sealed record ValidationSnapshot(
    IReadOnlyList<ValidationDiagnostic> Diagnostics,
    int ErrorCount,
    int WarningCount);

public sealed record ValidationCompletedEventArgs(
    ValidationSnapshot Snapshot,
    string? DeferredStatus);

public sealed class ValidationCoordinator(
    IDeferredScheduler scheduler,
    GalaxyMapValidator? validator = null) : IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(250);
    private readonly GalaxyMapValidator _validator = validator ?? new GalaxyMapValidator();

    public event EventHandler<ValidationCompletedEventArgs>? Completed;

    public ValidationSnapshot Validate(
        GalaxyMapWorkspace? workspace,
        GalaxyMapDocument? document,
        IEnumerable<ValidationDiagnostic> startupDiagnostics)
    {
        var diagnostics = workspace is not null
            ? _validator.Validate(workspace)
            : document is not null
                ? _validator.Validate(document)
                : [];
        var combined = startupDiagnostics.Concat(diagnostics).ToArray();
        var errorCount = 0;
        var warningCount = 0;
        foreach (var diagnostic in combined)
        {
            if (diagnostic.Severity == ValidationSeverity.Error)
            {
                errorCount++;
            }
            else if (diagnostic.Severity == ValidationSeverity.Warning)
            {
                warningCount++;
            }
        }

        return new ValidationSnapshot(
            combined,
            errorCount,
            warningCount);
    }

    public void Schedule(
        Func<ValidationSnapshot> validation,
        string? deferredStatus)
    {
        ArgumentNullException.ThrowIfNull(validation);
        scheduler.Schedule(Delay, () =>
            Completed?.Invoke(this, new ValidationCompletedEventArgs(validation(), deferredStatus)));
    }

    public void Cancel() => scheduler.Cancel();
    public void Dispose() => scheduler.Dispose();
}
