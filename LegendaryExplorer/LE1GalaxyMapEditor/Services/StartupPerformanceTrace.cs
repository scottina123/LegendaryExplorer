using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace LE1GalaxyMapEditor.Services;

/// <summary>
/// Collects low-overhead startup timings in memory. The trace is written only
/// after the UI becomes usable (or startup fails), so diagnostics do not add
/// file-system work to the critical startup path.
/// </summary>
internal sealed class StartupPerformanceTrace
{
    private const int RetainedTraceCount = 10;

    private readonly Lock _sync = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<TraceEntry> _entries = [];
    private readonly DateTimeOffset _processStartUtc;

    public StartupPerformanceTrace()
    {
        _processStartUtc = ReadProcessStartUtc();
        Mark("Startup trace created");
    }

    public TimeSpan ProcessAge => DateTimeOffset.UtcNow - _processStartUtc;

    public void Mark(string stage, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        var entry = new TraceEntry(
            stage,
            _clock.Elapsed,
            DateTimeOffset.UtcNow - _processStartUtc,
            null,
            detail);
        lock (_sync)
        {
            _entries.Add(entry);
        }

        Debug.WriteLine(FormatEntry(entry));
    }

    public IDisposable Measure(string stage, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        return new Measurement(this, stage, detail, _clock.Elapsed);
    }

    public void RecordDuration(string stage, TimeSpan duration, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        var entry = new TraceEntry(
            stage,
            _clock.Elapsed,
            DateTimeOffset.UtcNow - _processStartUtc,
            duration,
            detail);
        lock (_sync)
        {
            _entries.Add(entry);
        }

        Debug.WriteLine(FormatEntry(entry));
    }

    public Task<string?> SaveAsync(Exception? startupFailure = null)
    {
        TraceEntry[] entries;
        lock (_sync)
        {
            entries = [.. _entries];
        }

        return Task.Run(() => Save(entries, startupFailure));
    }

    private string? Save(IReadOnlyList<TraceEntry> entries, Exception? startupFailure)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LE1GalaxyMapEditor",
                "Logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, $"startup-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");

            var assembly = Assembly.GetExecutingAssembly().GetName();
            var lines = new List<string>
            {
                "LE1 Galaxy Map Editor startup performance trace",
                $"Recorded: {DateTimeOffset.Now:O}",
                $"Version: {assembly.Version}",
                $"Runtime: {Environment.Version}",
                $"OS: {Environment.OSVersion}",
                $"Process path: {Environment.ProcessPath ?? "Unknown"}",
                $"Process start (UTC): {_processStartUtc:O}",
                string.Empty,
                "Timings (process age includes CLR/WPF work before App startup):"
            };
            lines.AddRange(entries.Select(FormatEntry));

            if (startupFailure is not null)
            {
                lines.Add(string.Empty);
                lines.Add("Startup failure:");
                lines.Add(startupFailure.ToString());
            }

            File.WriteAllLines(logPath, lines);
            RemoveOldTraces(logDirectory);
            return logPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not save startup performance trace: {exception.Message}");
            return null;
        }
    }

    private void CompleteMeasurement(string stage, string? detail, TimeSpan startedAt)
    {
        var completedAt = _clock.Elapsed;
        var entry = new TraceEntry(
            stage,
            completedAt,
            DateTimeOffset.UtcNow - _processStartUtc,
            completedAt - startedAt,
            detail);
        lock (_sync)
        {
            _entries.Add(entry);
        }

        Debug.WriteLine(FormatEntry(entry));
    }

    private static string FormatEntry(TraceEntry entry)
    {
        var duration = entry.Duration is { } value
            ? $" | duration {value.TotalMilliseconds,9:F1} ms"
            : string.Empty;
        var detail = string.IsNullOrWhiteSpace(entry.Detail) ? string.Empty : $" | {entry.Detail}";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[process +{entry.ProcessAge.TotalMilliseconds,10:F1} ms | trace +{entry.TraceAge.TotalMilliseconds,10:F1} ms{duration}] {entry.Stage}{detail}");
    }

    private static DateTimeOffset ReadProcessStartUtc()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or NotSupportedException
                                           or System.ComponentModel.Win32Exception)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static void RemoveOldTraces(string logDirectory)
    {
        try
        {
            foreach (var oldTrace in Directory.EnumerateFiles(logDirectory, "startup-*.log")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(RetainedTraceCount))
            {
                File.Delete(oldTrace);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not prune startup performance traces: {exception.Message}");
        }
    }

    private readonly record struct TraceEntry(
        string Stage,
        TimeSpan TraceAge,
        TimeSpan ProcessAge,
        TimeSpan? Duration,
        string? Detail);

    private sealed class Measurement(
        StartupPerformanceTrace owner,
        string stage,
        string? detail,
        TimeSpan startedAt) : IDisposable
    {
        private int _completed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                owner.CompleteMeasurement(stage, detail, startedAt);
            }
        }
    }
}
