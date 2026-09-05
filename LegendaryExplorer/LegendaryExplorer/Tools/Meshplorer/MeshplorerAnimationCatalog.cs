using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.Tools.Meshplorer;

internal sealed class MeshplorerAnimationCatalog
{
    internal sealed class Entry(AnimationRecord record)
    {
        public AnimationRecord Record { get; } = record;
        public override string ToString() =>
            $"{Record.AnimSequence} | {Record.SeqName} | {Record.AnimData} | {Record.Length:F2}s | {Record.Frames} frames";
    }

    internal sealed class LoadedAnimation(IMEPackage package, AnimSequence sequence) : IDisposable
    {
        public AnimSequence Sequence { get; } = sequence;
        public void Dispose() => package.Dispose();
    }

    private readonly IReadOnlyDictionary<int, string> _filePaths;
    public MEGame Game { get; }
    public DateTime DatabaseStamp { get; }
    public IReadOnlyList<Entry> Entries { get; }

    internal MeshplorerAnimationCatalog(AssetDB database, IReadOnlyDictionary<int, string> filePaths,
        DateTime databaseStamp)
    {
        Game = database.Game;
        DatabaseStamp = databaseStamp;
        _filePaths = filePaths;
        // Ambient performance records describe sets of animations, not playable AnimSequence exports.
        // Keep every sequence, including mod animations and records whose source is currently missing.
        Entries = database.Animations.Where(record => !record.IsAmbPerf)
            .OrderBy(record => record.AnimSequence, StringComparer.OrdinalIgnoreCase)
            .Select(record => new Entry(record)).ToArray();
    }

    public static async Task<MeshplorerAnimationCatalog> LoadAsync(MEGame game, CancellationToken token)
    {
        string path = AssetDatabaseWindow.GetDBPath(game);
        if (!File.Exists(path))
            throw new InvalidOperationException($"No Asset Database found for {game}. Generate it in Asset Database, then choose an animation again.");

        DateTime stamp = File.GetLastWriteTimeUtc(path);
        var database = new AssetDB();
        await AssetDatabaseWindow.LoadDatabase(path, game, database, token);
        token.ThrowIfCancellationRequested();
        if (database.Game != game || database.DatabaseVersion != AssetDatabaseWindow.dbCurrentBuild)
            throw new InvalidOperationException($"The {game} Asset Database could not be loaded or is out of date. Rebuild it in Asset Database.");

        var filePaths = await AssetDatabaseFilePathResolver.BuildIndexAsync(database, game, token);
        return new MeshplorerAnimationCatalog(database, filePaths, stamp);
    }

    public Task<LoadedAnimation> LoadAnimationAsync(Entry entry, CancellationToken token) => Task.Run(() =>
    {
        Exception lastError = null;
        foreach (AnimUsage usage in entry.Record.Usages)
        {
            token.ThrowIfCancellationRequested();
            if (!_filePaths.TryGetValue(usage.FileKey, out string path) || !File.Exists(path)) continue;
            IMEPackage package = null;
            try
            {
                // Preview an isolated copy without registering or modifying the user's mesh package.
                package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
                if (package.Game != Game || !package.IsUExport(usage.UIndex))
                    throw new InvalidDataException("The indexed animation export is no longer available.");
                ExportEntry export = package.GetUExport(usage.UIndex);
                if (export.ClassName != "AnimSequence"
                    || !string.Equals(export.ObjectName.Instanced, entry.Record.AnimSequence, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The indexed export has changed. Rebuild the Asset Database.");
                var animation = ObjectBinary.From<AnimSequence>(export);
                animation.DecompressAnimationData();
                if (animation.NumFrames <= 0 || !float.IsFinite(animation.SequenceLength)
                    || animation.SequenceLength < 0 || animation.RawAnimationData is not { Count: > 0 })
                    throw new InvalidDataException("The animation contains no playable frames.");
                token.ThrowIfCancellationRequested();
                return new LoadedAnimation(package, animation);
            }
            catch (OperationCanceledException)
            {
                package?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                package?.Dispose();
                lastError = ex;
            }
        }
        throw new InvalidOperationException(lastError == null
            ? "No installed source package was found for this animation. Check the game path and installed DLC, or rebuild the Asset Database."
            : $"Could not load this animation: {lastError.Message}", lastError);
    }, token);
}
