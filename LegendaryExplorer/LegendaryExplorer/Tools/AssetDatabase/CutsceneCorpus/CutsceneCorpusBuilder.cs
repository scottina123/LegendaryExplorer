using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.InterpEditor;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK.ME2ME3;
using Newtonsoft.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace LegendaryExplorer.Tools.AssetDatabase.CutsceneCorpus
{
    public sealed class CutsceneCorpusOptions
    {
        public MEGame Game { get; init; } = MEGame.LE3;
        public string OutputDirectory { get; init; }
        public MELocalization Localization { get; init; } = MELocalization.INT;
        public bool IncludeMods { get; init; } = true;
        /// <summary>Reuse per-file results for packages whose size and timestamp have not changed.</summary>
        public bool UseCache { get; init; } = true;
        public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 1, 4);
        /// <summary>Optional explicit package list (used by tests); when null, the game's loaded files are used.</summary>
        public IReadOnlyList<string> FileOverride { get; init; }
    }

    /// <summary>
    /// Builds the cutscene corpus: a folder of JSONL files describing every conversation, node, shot, stage and
    /// camera move in the game, for agents to crawl. Separate from the Asset Database scan.
    /// </summary>
    public static class CutsceneCorpusBuilder
    {
        public const int CorpusVersion = 1;

        public static string DefaultOutputDirectory(MEGame game) =>
            Path.Combine(AppDirectories.AppDataFolder, "CutsceneCorpus", game.ToString());

        internal static readonly JsonSerializerOptions JsonLineOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            WriteIndented = false
        };

        private static readonly JsonSerializerOptions JsonIndentedOptions = new(JsonLineOptions) { WriteIndented = true };

        public static async Task<CorpusManifest> BuildAsync(CutsceneCorpusOptions options, IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            if (!options.Game.IsGame3() || !options.Game.IsLEGame())
            {
                throw new NotSupportedException("The cutscene corpus currently supports Legendary Edition Mass Effect 3 (LE3) only.");
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory);

            var stopwatch = Stopwatch.StartNew();
            string outputDirectory = Path.GetFullPath(options.OutputDirectory);
            string cacheDirectory = Path.Combine(outputDirectory, ".cache");
            Directory.CreateDirectory(cacheDirectory);
            var manifest = new CorpusManifest
            {
                CorpusVersion = CorpusVersion,
                Game = options.Game.ToString(),
                Localization = options.Localization.ToString(),
                IncludesMods = options.IncludeMods,
                GeneratedUtc = DateTime.UtcNow.ToString("u")
            };

            progress?.Report("Finding conversation and stage packages...");
            List<string> files = await Task.Run(() => GetCandidateFiles(options), cancellationToken);
            manifest.FilesConsidered = files.Count;

            progress?.Report("Loading TLK strings...");
            Dictionary<int, string> tlkStrings = await Task.Run(() => LoadTlk(options, files), cancellationToken);
            string Tlk(int strRef) => tlkStrings.TryGetValue(strRef, out string text) ? text : null;

            // Pass 1
            var relevance = LoadRelevanceIndex(cacheDirectory);
            var summaries = new ConcurrentBag<(CorpusFileResult Summary, string CachePath)>();
            var errors = new ConcurrentBag<string>();
            int processed = 0;
            int scanned = 0;
            int fromCache = 0;
            await Task.Run(() => Parallel.ForEach(files, new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaxParallelism,
                CancellationToken = cancellationToken
            }, file =>
            {
                try
                {
                    var info = new FileInfo(file);
                    string fileKey = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{CorpusVersion}:{options.Localization}";
                    string cachePath = Path.Combine(cacheDirectory, CacheFileName(file));
                    if (options.UseCache && relevance.TryGetValue(file, out string known) && known == "irrelevant:" + fileKey)
                    {
                        return;
                    }

                    CorpusFileResult result = options.UseCache ? TryReadCache(cachePath, fileKey) : null;
                    if (result is not null)
                    {
                        Interlocked.Increment(ref fromCache);
                    }
                    else
                    {
                        if (!IsRelevant(file))
                        {
                            relevance[file] = "irrelevant:" + fileKey;
                            return;
                        }
                        result = ScanFile(file, info, Tlk);
                        result.Localization = options.Localization.ToString();
                        result.CacheKey = fileKey;
                        WriteJson(cachePath, result, JsonLineOptions);
                        Interlocked.Increment(ref scanned);
                    }
                    relevance[file] = "relevant:" + fileKey;
                    foreach (string error in result.Errors)
                    {
                        errors.Add(error);
                    }

                    // Keep only the small parts in memory; conversations are re-read from the cache in pass 2.
                    var summary = new CorpusFileResult
                    {
                        FileName = result.FileName,
                        FilePath = result.FilePath,
                        Dlc = result.Dlc,
                        IsMod = result.IsMod,
                        Stages = result.Stages,
                        ConversationStages = result.ConversationStages,
                        Animations = result.Animations,
                        LevelCameras = result.LevelCameras,
                        Conversations = result.Conversations.Count > 0 ? [new ConversationRecord()] : []
                    };
                    summaries.Add((summary, cachePath));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    errors.Add($"{Path.GetFileName(file)}: scan failed: {exception.Message}");
                }
                finally
                {
                    int done = Interlocked.Increment(ref processed);
                    if (done % 25 == 0 || done == files.Count)
                    {
                        progress?.Report($"Pass 1: {done}/{files.Count} packages checked ({scanned} scanned, {fromCache} from cache)");
                    }
                }
            }), cancellationToken);
            SaveRelevanceIndex(cacheDirectory, relevance);
            manifest.FilesScanned = scanned;
            manifest.FilesFromCache = fromCache;

            // Pass 2
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Pass 2: joining conversations with stages and measuring shots...");
            List<(CorpusFileResult Summary, string CachePath)> ordered = summaries
                .OrderBy(entry => entry.Summary.FileName, StringComparer.OrdinalIgnoreCase).ToList();
            await Task.Run(() => WriteOutputs(outputDirectory, ordered, errors, manifest, progress, cancellationToken), cancellationToken);

            manifest.ErrorCount = errors.Count;
            manifest.ScanSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
            manifest.Files = ordered.Select(entry => entry.Summary.FileName).ToList();
            File.WriteAllLines(Path.Combine(outputDirectory, "errors.log"), errors.OrderBy(error => error, StringComparer.Ordinal));
            WriteJson(Path.Combine(outputDirectory, "manifest.json"), manifest, JsonIndentedOptions);
            CutsceneCorpusDocs.Write(outputDirectory);
            progress?.Report($"Cutscene corpus written to {outputDirectory}");
            return manifest;
        }

        #region Pass 1

        private static List<string> GetCandidateFiles(CutsceneCorpusOptions options)
        {
            IEnumerable<string> files = (IEnumerable<string>)options.FileOverride
                ?? MELoadedFiles.GetFilesLoadedInGame(options.Game, forceReload: true, includeModDLC: options.IncludeMods).Values;
            return files
                .Where(file => file.EndsWith(".pcc", StringComparison.OrdinalIgnoreCase))
                .Where(file =>
                {
                    MELocalization localization = Path.GetFileName(file).GetUnrealLocalization();
                    return localization == MELocalization.None || localization == options.Localization;
                })
                .Where(file => !Path.GetFileName(file).StartsWith("RefShaderCache", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsRelevant(string file)
        {
            // Reads only the name/import/export tables; no export data.
            using IMEPackage header = MEPackageHandler.UnsafePartialLoad(file, _ => false);
            return header.Names.Any(name => CorpusFileExtractor.RelevantClasses.Contains(name));
        }

        private static CorpusFileResult ScanFile(string file, FileInfo info, Func<int, string> tlk)
        {
            (string dlc, bool isMod) = GetDlc(file);
            var result = new CorpusFileResult
            {
                CorpusVersion = CorpusVersion,
                FileName = Path.GetFileName(file),
                FilePath = file,
                Dlc = dlc,
                IsMod = isMod,
                FileSize = info.Length,
                FileTimeUtcTicks = info.LastWriteTimeUtc.Ticks
            };
            using IMEPackage pcc = MEPackageHandler.OpenMEPackage(file, forceLoadFromDisk: true);
            CorpusFileExtractor.Extract(pcc, result, tlk);
            return result;
        }

        private static (string Dlc, bool IsMod) GetDlc(string file)
        {
            string[] parts = Path.GetFullPath(file).Split(Path.DirectorySeparatorChar);
            int dlcIndex = Array.FindIndex(parts, part => part.Equals("DLC", StringComparison.OrdinalIgnoreCase));
            if (dlcIndex >= 0 && dlcIndex + 1 < parts.Length)
            {
                string dlc = parts[dlcIndex + 1];
                return (dlc, dlc.StartsWith("DLC_MOD_", StringComparison.OrdinalIgnoreCase));
            }
            return ("BaseGame", false);
        }

        private static Dictionary<int, string> LoadTlk(CutsceneCorpusOptions options, List<string> packageFiles)
        {
            string suffix = $"_{options.Localization}.tlk";
            IEnumerable<string> tlkFiles;
            if (options.FileOverride is not null)
            {
                tlkFiles = packageFiles.Select(Path.GetDirectoryName).Distinct()
                    .SelectMany(directory => Directory.EnumerateFiles(directory, "*" + suffix));
            }
            else
            {
                tlkFiles = MELoadedFiles.GetFilesLoadedInGame(options.Game, includeModDLC: options.IncludeMods, additionalExtensions: [".tlk"])
                    .Values.Where(file => file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            }

            var strings = new Dictionary<int, string>();
            foreach (string tlkFile in tlkFiles.OrderBy(file => MountPriority(file, options.Game)))
            {
                try
                {
                    var talkFile = new ME2ME3TalkFile(tlkFile);
                    foreach (var stringRef in talkFile.StringRefs)
                    {
                        if (stringRef.Data is not null)
                        {
                            strings[stringRef.StringID] = stringRef.Data;
                        }
                    }
                }
                catch (Exception)
                {
                    // A broken mod TLK should not stop the corpus.
                }
            }
            return strings;
        }

        private static int MountPriority(string file, MEGame game)
        {
            string cookedDirectory = Path.GetDirectoryName(file);
            string dlcDirectory = Path.GetDirectoryName(cookedDirectory);
            if (dlcDirectory is null || !Path.GetFileName(dlcDirectory).StartsWith("DLC_", StringComparison.OrdinalIgnoreCase))
            {
                return int.MinValue;
            }
            try
            {
                return MELoadedFiles.GetMountPriority(dlcDirectory, game);
            }
            catch
            {
                return 0;
            }
        }

        private static string CacheFileName(string file)
        {
            byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(file).ToLowerInvariant()));
            return $"{Path.GetFileNameWithoutExtension(file)}.{Convert.ToHexString(hash, 0, 4)}.json";
        }

        private static CorpusFileResult TryReadCache(string cachePath, string fileKey)
        {
            try
            {
                if (!File.Exists(cachePath))
                {
                    return null;
                }
                using FileStream stream = File.OpenRead(cachePath);
                var result = JsonSerializer.Deserialize<CorpusFileResult>(stream, JsonLineOptions);
                return result?.CorpusVersion == CorpusVersion && result.CacheKey == fileKey ? result : null;
            }
            catch
            {
                return null;
            }
        }

        private static ConcurrentDictionary<string, string> LoadRelevanceIndex(string cacheDirectory)
        {
            string path = Path.Combine(cacheDirectory, "_relevance.json");
            try
            {
                if (File.Exists(path))
                {
                    var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                    if (stored is not null)
                    {
                        return new ConcurrentDictionary<string, string>(stored, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch
            {
                // Rebuild the index.
            }
            return new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static void SaveRelevanceIndex(string cacheDirectory, ConcurrentDictionary<string, string> relevance) =>
            File.WriteAllText(Path.Combine(cacheDirectory, "_relevance.json"),
                JsonSerializer.Serialize(relevance.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(pair => pair.Key, pair => pair.Value)));

        #endregion

        #region Pass 2

        private static void WriteOutputs(string outputDirectory, List<(CorpusFileResult Summary, string CachePath)> files,
            ConcurrentBag<string> errors, CorpusManifest manifest, IProgress<string> progress, CancellationToken cancellationToken)
        {
            // Stages, contexts and animations from every file.
            var stagesById = new Dictionary<string, StageRecord>(StringComparer.OrdinalIgnoreCase);
            var contextsByConversation = new Dictionary<string, List<ConversationStageRecord>>(StringComparer.OrdinalIgnoreCase);
            var animations = new Dictionary<string, AnimationRecord>(StringComparer.OrdinalIgnoreCase);
            var levelCamerasByFile = new Dictionary<string, List<LevelCameraRecord>>(StringComparer.OrdinalIgnoreCase);
            using (var stageWriter = new JsonlWriter(Path.Combine(outputDirectory, "stages.jsonl")))
            using (var contextWriter = new JsonlWriter(Path.Combine(outputDirectory, "conversation_stages.jsonl")))
            using (var levelCameraWriter = new JsonlWriter(Path.Combine(outputDirectory, "level_cameras.jsonl")))
            {
                foreach ((CorpusFileResult summary, _) in files)
                {
                    if (summary.LevelCameras.Count > 0)
                    {
                        levelCamerasByFile[summary.FileName] = summary.LevelCameras;
                        foreach (LevelCameraRecord camera in summary.LevelCameras)
                        {
                            levelCameraWriter.Write(camera);
                        }
                    }
                    foreach (StageRecord stage in summary.Stages)
                    {
                        if (stagesById.TryAdd(stage.Id, stage))
                        {
                            stageWriter.Write(stage);
                        }
                    }
                    foreach (ConversationStageRecord context in summary.ConversationStages)
                    {
                        contextWriter.Write(context);
                        if (string.IsNullOrEmpty(context.ConversationName)) continue;
                        if (!contextsByConversation.TryGetValue(context.ConversationName, out var list))
                        {
                            contextsByConversation[context.ConversationName] = list = [];
                        }
                        list.Add(context);
                    }
                    foreach (AnimationRecord animation in summary.Animations)
                    {
                        animations.TryAdd($"{animation.Set}|{animation.Sequence}", animation);
                    }
                }
                manifest.Counts["stages"] = stageWriter.Count;
                manifest.Counts["conversationStages"] = contextWriter.Count;
                manifest.Counts["levelCameras"] = levelCameraWriter.Count;
            }
            using (var animationWriter = new JsonlWriter(Path.Combine(outputDirectory, "animations.jsonl")))
            {
                foreach (AnimationRecord animation in animations.Values.OrderBy(a => a.Set, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Sequence, StringComparer.OrdinalIgnoreCase))
                {
                    animationWriter.Write(animation);
                }
                manifest.Counts["animations"] = animationWriter.Count;
            }
            var animationsBySequence = animations.Values.GroupBy(a => a.Sequence ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

            string scriptsDirectory = Path.Combine(outputDirectory, "scripts");
            if (Directory.Exists(scriptsDirectory))
            {
                Directory.Delete(scriptsDirectory, true);
            }
            Directory.CreateDirectory(scriptsDirectory);

            var presets = new List<CameraPreset>();
            var presetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int shotCount = 0;
            int measuredShots = 0;
            using var conversationWriter = new JsonlWriter(Path.Combine(outputDirectory, "conversations.jsonl"));
            using var nodeWriter = new JsonlWriter(Path.Combine(outputDirectory, "nodes.jsonl"));
            using var moveWriter = new JsonlWriter(Path.Combine(outputDirectory, "camera_moves.jsonl"));
            int fileIndex = 0;
            foreach ((CorpusFileResult summary, string cachePath) in files.Where(entry => entry.Summary.Conversations.Count > 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                fileIndex++;
                CorpusFileResult result;
                try
                {
                    using FileStream stream = File.OpenRead(cachePath);
                    result = JsonSerializer.Deserialize<CorpusFileResult>(stream, JsonLineOptions);
                }
                catch (Exception exception)
                {
                    errors.Add($"{summary.FileName}: could not read cached result: {exception.Message}");
                    continue;
                }

                foreach (ConversationRecord conversation in result.Conversations)
                {
                    try
                    {
                        (ConversationStageRecord context, StageRecord stage) =
                            ChooseContext(conversation, contextsByConversation, stagesById);
                        List<LevelCameraRecord> cameras = levelCamerasByFile.GetValueOrDefault(context?.File ?? conversation.MainPackage)
                                                          ?? levelCamerasByFile.GetValueOrDefault(conversation.MainPackage);
                        var analyzer = new CorpusShotAnalyzer(context, stage, cameras);
                        var moves = new List<CameraMoveRecord>();
                        // Walk the conversation in flow order so actors start each node where the previous node left them.
                        var exitPoses = new Dictionary<string, Dictionary<string, CameraOrigin>>(StringComparer.OrdinalIgnoreCase);
                        foreach ((NodeRecord node, string predecessor) in FlowOrder(conversation))
                        {
                            AnnotateGestureLengths(node, animations, animationsBySequence);
                            Dictionary<string, CameraOrigin> carried = predecessor is not null
                                ? exitPoses.GetValueOrDefault(predecessor)
                                : null;
                            node.Shots = analyzer.Analyze(node, moves, conversation.Id, carried, out Dictionary<string, CameraOrigin> exit);
                            exitPoses[node.Id] = exit;
                            if (node.Shots is not null)
                            {
                                shotCount += node.Shots.Count;
                                measuredShots += node.Shots.Count(shot => shot.Subject is not null);
                            }
                        }
                        foreach (NodeRecord node in conversation.Nodes)
                        {
                            nodeWriter.Write(node);
                        }
                        foreach (CameraMoveRecord move in moves)
                        {
                            moveWriter.Write(move);
                            string name = $"LE3 {conversation.Name} {move.NodeId} {move.Group} ({move.Size} {string.Join("+", move.Move ?? [])})";
                            string unique = name;
                            for (int suffix = 2; !presetNames.Add(unique); suffix++)
                            {
                                unique = $"{name} #{suffix}";
                            }
                            if (move.LocalKeys.Count > 0)
                            {
                                presets.Add(CorpusShotAnalyzer.ToCameraPreset(move, unique));
                            }
                        }

                        CutsceneScreenplayWriter.Write(Path.Combine(scriptsDirectory, SafeFileName(conversation.Id) + ".txt"),
                            conversation, context, stage);
                        List<NodeRecord> nodes = conversation.Nodes;
                        conversation.Nodes = null;
                        conversationWriter.Write(conversation);
                        conversation.Nodes = nodes;
                    }
                    catch (Exception exception)
                    {
                        errors.Add($"{result.FileName} {conversation.Name}: pass 2 failed: {exception.Message}");
                    }
                }
                progress?.Report($"Pass 2: {fileIndex} conversation packages written ({nodeWriter.Count} nodes, {shotCount} shots)");
            }

            manifest.Counts["conversations"] = conversationWriter.Count;
            manifest.Counts["nodes"] = nodeWriter.Count;
            manifest.Counts["shots"] = shotCount;
            manifest.Counts["shotsWithSubject"] = measuredShots;
            manifest.Counts["cameraMoves"] = moveWriter.Count;

            File.WriteAllText(Path.Combine(outputDirectory, "camera_presets_game.json"),
                JsonConvert.SerializeObject(new { Version = 1, Presets = presets }, Formatting.Indented));
        }

        private static (ConversationStageRecord Context, StageRecord Stage) ChooseContext(ConversationRecord conversation,
            Dictionary<string, List<ConversationStageRecord>> contextsByConversation, Dictionary<string, StageRecord> stagesById)
        {
            if (!contextsByConversation.TryGetValue(conversation.Name, out List<ConversationStageRecord> candidates) || candidates.Count == 0)
            {
                conversation.ChosenStageReason = "no StartConversation with a linked BioStage references this conversation";
                return (null, null);
            }

            var referencedCameras = conversation.Nodes
                .SelectMany(node => node.Timeline?.StageCameraSwitches ?? [])
                .Select(cut => cut.Target)
                .Where(tag => !string.IsNullOrEmpty(tag))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string mainPackage = conversation.MainPackage;

            var scored = candidates.Select(context =>
            {
                stagesById.TryGetValue(context.StageId ?? "", out StageRecord stage);
                int matched = stage?.Cameras.Count(camera => referencedCameras.Contains(camera.Tag)) ?? 0;
                bool inMain = string.Equals(context.File, mainPackage, StringComparison.OrdinalIgnoreCase);
                return (Context: context, Stage: stage, Matched: matched, InMain: inMain);
            })
            .OrderByDescending(entry => entry.Matched)
            .ThenByDescending(entry => entry.InMain)
            .ThenBy(entry => entry.Context.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

            conversation.StageContextIds = scored.Select(entry => entry.Context.Id).ToList();
            var best = scored[0];
            conversation.ChosenStageContextId = best.Context.Id;
            conversation.ChosenStageReason = $"{best.Matched}/{referencedCameras.Count} referenced stage cameras found; "
                                             + (best.InMain ? "in main package" : $"from {best.Context.File}")
                                             + (scored.Count > 1 ? $"; {scored.Count} candidate contexts" : "");
            return (best.Context, best.Stage);
        }

        /// <summary>Breadth-first from the start nodes; each node is paired with the node it was first reached from.</summary>
        private static IEnumerable<(NodeRecord Node, string Predecessor)> FlowOrder(ConversationRecord conversation)
        {
            Dictionary<string, NodeRecord> nodes = conversation.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string Id, string Predecessor)>(
                conversation.StartNodes.Where(nodes.ContainsKey).Select(id => (id, (string)null)));
            while (queue.Count > 0)
            {
                (string id, string predecessor) = queue.Dequeue();
                if (!visited.Add(id))
                {
                    continue;
                }
                NodeRecord node = nodes[id];
                yield return (node, predecessor);
                foreach (string next in (node.Replies?.Select(reply => reply.Target) ?? []).Concat(node.Next ?? []))
                {
                    if (nodes.ContainsKey(next) && !visited.Contains(next))
                    {
                        queue.Enqueue((next, id));
                    }
                }
            }
            foreach (NodeRecord node in conversation.Nodes.Where(node => !visited.Contains(node.Id)))
            {
                yield return (node, null);
            }
        }

        private static void AnnotateGestureLengths(NodeRecord node, Dictionary<string, AnimationRecord> animations,
            Dictionary<string, List<AnimationRecord>> bySequence)
        {
            foreach (TrackRecord track in node.Timeline?.Tracks.Where(track => track.Kind == "gesture") ?? [])
            {
                foreach (JsonObject key in track.Keys)
                {
                    string set = key["nmGestureSet"]?.GetValue<string>();
                    string anim = key["nmGestureAnim"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(anim))
                    {
                        continue;
                    }
                    if (animations.TryGetValue($"{set}|{anim}", out AnimationRecord exact))
                    {
                        key["gestureAnimLength"] = exact.Length;
                    }
                    else if (bySequence.TryGetValue(anim, out List<AnimationRecord> matches)
                             && matches.Select(match => match.Length).Distinct().Count() == 1)
                    {
                        key["gestureAnimLength"] = matches[0].Length;
                    }
                }
            }
        }

        internal static string SafeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        #endregion

        internal static void WriteJson<T>(string path, T value, JsonSerializerOptions options)
        {
            string temp = path + ".tmp";
            using (FileStream stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, value, options);
            }
            File.Move(temp, path, true);
        }

        private sealed class JsonlWriter : IDisposable
        {
            private readonly StreamWriter writer;
            public int Count { get; private set; }

            public JsonlWriter(string path)
            {
                writer = new StreamWriter(path, false, new UTF8Encoding(false));
            }

            public void Write<T>(T value)
            {
                writer.WriteLine(JsonSerializer.Serialize(value, JsonLineOptions));
                Count++;
            }

            public void Dispose() => writer.Dispose();
        }
    }
}
