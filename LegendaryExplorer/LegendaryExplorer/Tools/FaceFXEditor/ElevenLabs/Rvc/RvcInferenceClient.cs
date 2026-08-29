using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LegendaryExplorer.Tools.FaceFXEditor.ElevenLabs.Rvc
{
    public sealed record RvcVoiceModel(string FilePath)
    {
        public string DisplayName => Path.GetFileNameWithoutExtension(FilePath);
    }

    public enum RvcIndexSelectionKind
    {
        Automatic,
        Disabled,
        File
    }

    public sealed record RvcIndexChoice(RvcIndexSelectionKind Kind, string FilePath = null)
    {
        public const string AutomaticKey = "__automatic__";
        public const string DisabledKey = "__disabled__";

        public string SelectionKey => Kind switch
        {
            RvcIndexSelectionKind.Automatic => AutomaticKey,
            RvcIndexSelectionKind.Disabled => DisabledKey,
            _ => FilePath
        };

        public string DisplayName => Kind switch
        {
            RvcIndexSelectionKind.Automatic => "Automatic model-name match",
            RvcIndexSelectionKind.Disabled => "Disabled (do not use an index)",
            _ => Path.GetFileName(FilePath)
        };
    }

    internal sealed record RvcInferenceRequest
    {
        public string ModelPath { get; init; }
        public string InputPath { get; init; }
        public string OutputPath { get; init; }
        public int SpeakerId { get; init; }
        public int Pitch { get; init; }
        public string F0Method { get; init; }
        public string F0CurvePath { get; init; }
        public string IndexPath { get; init; }
        public double IndexRate { get; init; }
        public int FilterRadius { get; init; }
        public int ResampleSampleRate { get; init; }
        public double RmsMixRate { get; init; }
        public double Protect { get; init; }
    }

    internal static class RvcInstallation
    {
        public static readonly IReadOnlyList<string> F0Methods =
            ["rmvpe", "pm", "harvest", "crepe"];

        public static string GetRuntimePythonPath(string rootPath) =>
            Path.Combine(rootPath ?? string.Empty, "runtime", "python.exe");

        public static string GetWeightsPath(string rootPath) =>
            Path.Combine(rootPath ?? string.Empty, "assets", "weights");

        public static bool IsCompatibleRoot(string rootPath, out string problem)
        {
            problem = null;
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                problem = "Select the extracted RVC20240604Nvidia50x0 folder.";
                return false;
            }

            if (!File.Exists(GetRuntimePythonPath(rootPath)))
            {
                problem = @"The selected folder does not contain runtime\python.exe.";
                return false;
            }

            bool has2024Inference = File.Exists(Path.Combine(rootPath, "infer", "modules", "vc", "modules.py"));
            bool hasCurrentInference = File.Exists(Path.Combine(rootPath, "infer", "vc", "modules.py"));
            if (!has2024Inference && !hasCurrentInference)
            {
                problem = "The selected folder does not contain the RVC WebUI inference engine.";
                return false;
            }

            if (!Directory.Exists(GetWeightsPath(rootPath)))
            {
                problem = @"The selected folder does not contain assets\weights.";
                return false;
            }

            if (!File.Exists(Path.Combine(rootPath, "assets", "rmvpe", "rmvpe.pt")))
            {
                problem = @"The selected self-contained package does not contain assets\rmvpe\rmvpe.pt.";
                return false;
            }

            return true;
        }

        public static IReadOnlyList<RvcVoiceModel> DiscoverVoiceModels(string rootPath)
        {
            string weightsPath = GetWeightsPath(rootPath);
            if (!Directory.Exists(weightsPath)) return [];
            return Directory.EnumerateFiles(weightsPath, "*.pth", SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Select(path => new RvcVoiceModel(Path.GetFullPath(path)))
                .ToList();
        }

        public static IReadOnlyList<RvcIndexChoice> DiscoverIndexes(string rootPath)
        {
            var choices = new List<RvcIndexChoice>
            {
                new(RvcIndexSelectionKind.Automatic),
                new(RvcIndexSelectionKind.Disabled)
            };
            if (string.IsNullOrWhiteSpace(rootPath)) return choices;

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string relativeRoot in new[] { Path.Combine("assets", "indices"), "logs" })
            {
                string searchRoot = Path.Combine(rootPath, relativeRoot);
                if (!Directory.Exists(searchRoot)) continue;
                foreach (string path in Directory.EnumerateFiles(searchRoot, "*.index", SearchOption.AllDirectories))
                {
                    paths.Add(Path.GetFullPath(path));
                }
            }

            choices.AddRange(paths.OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Select(path => new RvcIndexChoice(RvcIndexSelectionKind.File, path)));
            return choices;
        }

        public static string ResolveIndexPath(RvcIndexChoice selection, RvcVoiceModel model,
            IEnumerable<RvcIndexChoice> availableChoices)
        {
            if (selection == null || selection.Kind == RvcIndexSelectionKind.Disabled) return null;
            if (selection.Kind == RvcIndexSelectionKind.File) return selection.FilePath;
            if (model == null) return null;

            string modelName = Path.GetFileNameWithoutExtension(model.FilePath);
            var files = (availableChoices ?? []).Where(choice => choice.Kind == RvcIndexSelectionKind.File)
                .ToList();
            return files.FirstOrDefault(choice =>
                       Path.GetFileNameWithoutExtension(choice.FilePath).Contains(modelName,
                           StringComparison.OrdinalIgnoreCase))?.FilePath
                   ?? files.FirstOrDefault(choice =>
                       modelName.Contains(Path.GetFileNameWithoutExtension(choice.FilePath),
                           StringComparison.OrdinalIgnoreCase))?.FilePath;
        }

        public static string FindDefaultRoot()
        {
            string folderName = "RVC20240604Nvidia50x0";
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, folderName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LegendaryExplorer", folderName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), folderName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", folderName)
            };
            return candidates.FirstOrDefault(path => IsCompatibleRoot(path, out _));
        }

        public static bool IsPathInside(string path, string directory)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory)) return false;
            string fullPath = Path.GetFullPath(path);
            string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class RvcInferenceClient : IDisposable
    {
        private const string ProtocolPrefix = "LEX_RVC_RESULT:";
        private const string ProgressPrefix = "LEX_RVC_PROGRESS:";
        private const int MaximumLogLength = 12000;
        internal static readonly TimeSpan InferenceTimeout = TimeSpan.FromMinutes(3);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly string _rootPath;
        private readonly SemaphoreSlim _requestLock = new(1, 1);
        private readonly StringBuilder _recentLog = new();
        private Process _process;
        private int _requestId;
        private bool _disposed;

        public RvcInferenceClient(string rootPath)
        {
            if (!RvcInstallation.IsCompatibleRoot(rootPath, out string problem))
            {
                throw new DirectoryNotFoundException(problem);
            }
            _rootPath = Path.GetFullPath(rootPath);
        }

        public async Task ConvertAsync(RvcInferenceRequest request, CancellationToken cancellationToken,
            IProgress<string> progress = null)
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateRequest(request);
            await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            using var timeoutSource = new CancellationTokenSource(InferenceTimeout);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                timeoutSource.Token);
            CancellationToken inferenceToken = linkedSource.Token;
            string requestFilePath = null;
            try
            {
                ThrowIfDisposed();
                int requestId = Interlocked.Increment(ref _requestId);
                var command = new
                {
                    id = requestId,
                    command = "infer",
                    request.ModelPath,
                    request.InputPath,
                    request.OutputPath,
                    request.SpeakerId,
                    request.Pitch,
                    request.F0Method,
                    request.F0CurvePath,
                    request.IndexPath,
                    request.IndexRate,
                    request.FilterRadius,
                    request.ResampleSampleRate,
                    request.RmsMixRate,
                    request.Protect
                };
                string json = JsonSerializer.Serialize(command, JsonOptions);
                string outputDirectory = Path.GetDirectoryName(request.OutputPath)
                                         ?? throw new InvalidDataException("The RVC output path has no directory.");
                Directory.CreateDirectory(outputDirectory);
                requestFilePath = Path.Combine(outputDirectory,
                    $".{Path.GetFileNameWithoutExtension(request.OutputPath)}.{Guid.NewGuid():N}.rvc-request.json");
                await File.WriteAllTextAsync(requestFilePath, json, new UTF8Encoding(false), inferenceToken)
                    .ConfigureAwait(false);
                progress?.Report("Starting an isolated bundled RVC worker");
                StartWorker(requestFilePath);

                while (true)
                {
                    string line = await _process.StandardOutput.ReadLineAsync(inferenceToken)
                        .ConfigureAwait(false);
                    if (line == null)
                    {
                        string exitText = _process is { HasExited: true }
                            ? $" Exit code: {_process.ExitCode}."
                            : string.Empty;
                        throw new InvalidOperationException(
                            $"The RVC worker exited before producing audio.{exitText} {GetRecentLog()}");
                    }

                    if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
                    {
                        RvcWorkerProgress workerProgress = JsonSerializer.Deserialize<RvcWorkerProgress>(
                            line[ProgressPrefix.Length..], JsonOptions);
                        if (workerProgress?.Id == requestId && !string.IsNullOrWhiteSpace(workerProgress.Stage))
                        {
                            progress?.Report(workerProgress.Stage);
                        }
                        continue;
                    }

                    if (!line.StartsWith(ProtocolPrefix, StringComparison.Ordinal))
                    {
                        AppendLog(line);
                        continue;
                    }

                    RvcWorkerResponse response = JsonSerializer.Deserialize<RvcWorkerResponse>(
                        line[ProtocolPrefix.Length..], JsonOptions);
                    if (response?.Id != requestId) continue;
                    if (!response.Ok)
                    {
                        throw new InvalidOperationException($"RVC inference failed: {response.Error}");
                    }
                    if (!File.Exists(request.OutputPath) || new FileInfo(request.OutputPath).Length <= 44)
                    {
                        throw new InvalidDataException("RVC reported success but did not produce usable WAV audio.");
                    }
                    return;
                }
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested &&
                                                     !cancellationToken.IsCancellationRequested)
            {
                string log = GetRecentLog();
                StopWorker();
                string diagnostics = string.IsNullOrWhiteSpace(log) ? string.Empty : $" Worker log: {log}";
                throw new TimeoutException(
                    $"RVC inference did not finish within {InferenceTimeout.TotalMinutes:0} minutes.{diagnostics}");
            }
            catch (OperationCanceledException)
            {
                StopWorker();
                throw;
            }
            finally
            {
                StopWorker();
                TryDeleteRequestFile(requestFilePath);
                _requestLock.Release();
            }
        }

        private void ValidateRequest(RvcInferenceRequest request)
        {
            if (!File.Exists(request.InputPath)) throw new FileNotFoundException("RVC input audio was not found.", request.InputPath);
            if (!File.Exists(request.ModelPath) ||
                !RvcInstallation.IsPathInside(request.ModelPath, RvcInstallation.GetWeightsPath(_rootPath)))
            {
                throw new InvalidDataException(@"The RVC voice must be a .pth model in assets\weights.");
            }
            if (!string.Equals(Path.GetExtension(request.ModelPath), ".pth", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The selected RVC voice is not a .pth model.");
            }
            if (!string.IsNullOrWhiteSpace(request.IndexPath) && (!File.Exists(request.IndexPath) ||
                !IsStandardIndexPath(request.IndexPath)))
            {
                throw new InvalidDataException(@"The RVC index must come from assets\indices or logs.");
            }
            if (!RvcInstallation.F0Methods.Contains(request.F0Method, StringComparer.Ordinal))
                throw new ArgumentOutOfRangeException(nameof(request.F0Method));
            if (request.SpeakerId < 0) throw new ArgumentOutOfRangeException(nameof(request.SpeakerId));
            if (request.Pitch is < -48 or > 48) throw new ArgumentOutOfRangeException(nameof(request.Pitch));
            if (request.IndexRate is < 0d or > 1d) throw new ArgumentOutOfRangeException(nameof(request.IndexRate));
            if (request.FilterRadius is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(request.FilterRadius));
            if (request.ResampleSampleRate != 0 && request.ResampleSampleRate is < 16000 or > 192000)
                throw new ArgumentOutOfRangeException(nameof(request.ResampleSampleRate));
            if (request.RmsMixRate is < 0d or > 1d) throw new ArgumentOutOfRangeException(nameof(request.RmsMixRate));
            if (request.Protect is < 0d or > 0.5d) throw new ArgumentOutOfRangeException(nameof(request.Protect));
            if (!string.IsNullOrWhiteSpace(request.F0CurvePath) && !File.Exists(request.F0CurvePath))
                throw new FileNotFoundException("The custom F0 curve file was not found.", request.F0CurvePath);
        }

        private bool IsStandardIndexPath(string path) =>
            RvcInstallation.IsPathInside(path, Path.Combine(_rootPath, "assets", "indices")) ||
            RvcInstallation.IsPathInside(path, Path.Combine(_rootPath, "logs"));

        private void StartWorker(string requestFilePath)
        {
            StopWorker();
            string bridgePath = Path.Combine(AppContext.BaseDirectory, "rvc_lex_infer.py");
            if (!File.Exists(bridgePath))
            {
                throw new FileNotFoundException("LEX's RVC inference bridge is missing.", bridgePath);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = RvcInstallation.GetRuntimePythonPath(_rootPath),
                WorkingDirectory = _rootPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add(bridgePath);
            startInfo.ArgumentList.Add("--request-file");
            startInfo.ArgumentList.Add(requestFilePath);
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            startInfo.Environment["weight_root"] = RvcInstallation.GetWeightsPath(_rootPath);
            startInfo.Environment["index_root"] = Path.Combine(_rootPath, "logs");
            startInfo.Environment["outside_index_root"] = Path.Combine(_rootPath, "assets", "indices");
            startInfo.Environment["rmvpe_root"] = Path.Combine(_rootPath, "assets", "rmvpe");

            _recentLog.Clear();
            _process = new Process { StartInfo = startInfo };
            _process.ErrorDataReceived += (_, args) => AppendLog(args.Data);
            if (!_process.Start()) throw new InvalidOperationException("Could not start the bundled RVC runtime.");
            _process.BeginErrorReadLine();
        }

        private static void TryDeleteRequestFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                File.Delete(path);
            }
            catch
            {
                // A stale request file is harmless and must not hide the inference result.
            }
        }

        private void AppendLog(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            lock (_recentLog)
            {
                _recentLog.AppendLine(value);
                if (_recentLog.Length > MaximumLogLength)
                    _recentLog.Remove(0, _recentLog.Length - MaximumLogLength);
            }
        }

        private string GetRecentLog()
        {
            lock (_recentLog)
            {
                return _recentLog.ToString().Trim();
            }
        }

        private void StopWorker()
        {
            Process process = _process;
            _process = null;
            if (process == null) return;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            process.Dispose();
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopWorker();
            _requestLock.Dispose();
        }

        private sealed class RvcWorkerResponse
        {
            public int Id { get; set; }
            public bool Ok { get; set; }
            public string Error { get; set; }
        }

        private sealed class RvcWorkerProgress
        {
            public int Id { get; set; }
            public string Stage { get; set; }
        }
    }
}
