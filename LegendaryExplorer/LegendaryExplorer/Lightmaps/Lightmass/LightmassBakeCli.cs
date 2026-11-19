using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorerCore;
using LegendaryExplorerCore.GameFilesystem;

namespace LegendaryExplorer.Lightmaps.Lightmass;

/// <summary>
/// Runs a <see cref="LightmassBakeJob"/> without any UI: <c>LegendaryExplorer.exe --bake-lighting job.json [--log file]</c>, with the job's solver.
/// It starts before the WPF app (and its single-instance handoff), so it can run while LEX is open. Game and UDK paths come
/// from LEX's settings. The log goes to the console it was started from, if any, and to a file (job.json.log by default).
/// Exit codes: 0 when everything was baked and saved, 2 when it was saved but some mappings failed (they keep their original
/// lighting), 1 when nothing was saved. LEX is a Windows program, so a console doesn't wait for it: use <c>start /wait</c> in
/// cmd, or <c>Start-Process -Wait</c> in PowerShell, to get the exit code.
/// <para>
/// <c>--compare-lighting job.json [--log file] [--heatmaps folder]</c> runs <see cref="LightmassCompare"/> instead (log: job.json.compare.log by
/// default; exit code 0 when both bakes finished, 1 otherwise).
/// </para>
/// </summary>
public static class LightmassBakeCli
{
    public const string Switch = "--bake-lighting";

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    public const int Saved = 0, NothingSaved = 1, SavedIncomplete = 2;

    /// <summary>Also <see cref="LightmassCompare.Switch"/>: <c>--compare-lighting job.json [--log file] [--heatmaps folder]</c> bakes the job with UDK's
    /// Lightmass and with LEX's solver and reports their differences, saving nothing</summary>
    public static bool IsInvocation(string[] args) => args.Length > 1
        && (args[1].Equals(Switch, StringComparison.OrdinalIgnoreCase) || args[1].Equals(LightmassCompare.Switch, StringComparison.OrdinalIgnoreCase));

    /// <param name="args">The process's arguments, as <see cref="Environment.GetCommandLineArgs"/> gives them.</param>
    public static int Run(string[] args)
    {
        bool hasConsole = AttachConsole(-1);
        bool compare = args[1].Equals(LightmassCompare.Switch, StringComparison.OrdinalIgnoreCase);
        string jobPath = args.Length > 2 ? Path.GetFullPath(args[2]) : null;
        if (jobPath == null || !File.Exists(jobPath))
        {
            if (hasConsole)
                Console.WriteLine(compare ? $"Usage: LegendaryExplorer.exe {LightmassCompare.Switch} <job.json> [--log <file>] [--heatmaps <folder>]"
                    : $"Usage: LegendaryExplorer.exe {Switch} <job.json> [--log <file>]");
            return NothingSaved;
        }
        string Option(string name)
        {
            int index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return index > 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
        }
        string Text(string name)
        {
            int index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return index > 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        string logPath = Option("--log") ?? jobPath + (compare ? ".compare.log" : ".log");

        using var logFile = OpenLog(logPath, hasConsole);
        if (logFile == null) return NothingSaved;
        var logLock = new object();
        var start = DateTime.Now;
        void Log(string message)
        {
            string line = $"[{DateTime.Now - start:hh\\:mm\\:ss}] {message}";
            lock (logLock)
            {
                logFile.WriteLine(line);
                if (hasConsole) Console.WriteLine(line);
            }
        }

        using var cancel = new CancellationTokenSource();
        if (hasConsole)
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Log("Cancelling...");
                cancel.Cancel();
            };
        }
        try
        {
            Settings.LoadSettings();
            LegendaryExplorerCoreLib.InitLib(null, message => Log($"Save failed: {message}"));
            CoreLibSettingsBridge.MapSettingsIntoBridge();
            LE1Directory.ReloadDefaultGamePath();
            LE2Directory.ReloadDefaultGamePath();
            LE3Directory.ReloadDefaultGamePath();

            var job = LightmassBakeJob.Load(jobPath);
            if (compare) return LightmassCompare.Run(job, Option("--heatmaps"), Log, cancel.Token, Option("--reference-cache"), Text("--only")) ? Saved : NothingSaved;
            Log($"Solver: {job.Solver}");
            foreach (string warning in job.Warnings()) Log($"Warning: {warning}");
            var result = job.Run(Log, cancel.Token);
            foreach (string line in LightmassBakeReport.Summary(result).Split('\n')) Log(line);
            return result.SavedFiles.Count == 0 ? NothingSaved : result.Complete ? Saved : SavedIncomplete;
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled. Nothing was saved.");
            return NothingSaved;
        }
        catch (Exception e)
        {
            Log($"Failed: {e}");
            return NothingSaved;
        }
    }

    static StreamWriter OpenLog(string path, bool hasConsole)
    {
        try
        {
            return new StreamWriter(path, append: false) { AutoFlush = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (hasConsole) Console.WriteLine($"The log file {path} can't be written: {e.Message}");
            return null;
        }
    }
}
