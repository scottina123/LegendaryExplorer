using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using LegendaryExplorer.Misc;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Newtonsoft.Json;

namespace LegendaryExplorer.Tools.InterpEditor;

public static class SavedDialogueCachePresetManager
{
    private const int CurrentVersion = 6;
    public static string StorageDirectory => Path.Combine(AppDirectories.AppDataFolder, "DialogueConversationCaches");

    public static IReadOnlyList<DialogueCachePreset> LoadAll()
    {
        try
        {
            if (!Directory.Exists(StorageDirectory))
            {
                return [];
            }

            return Directory.EnumerateFiles(StorageDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .Select(TryRead)
                .Where(preset => preset is not null)
                .OrderByDescending(preset => preset.SavedUtc)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static DialogueCachePreset Save(DialogueCachePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (string.IsNullOrWhiteSpace(preset.Label)
            || string.IsNullOrWhiteSpace(preset.SourceFilePath)
            || string.IsNullOrWhiteSpace(preset.DialogueName)
            || preset.Nodes.Count == 0)
        {
            throw new InvalidDataException("Dialogue cache presets require a label, source package, dialogue, and cached nodes.");
        }

        preset.Id = preset.Id == Guid.Empty ? Guid.NewGuid() : preset.Id;
        preset.Version = CurrentVersion;
        preset.Label = preset.Label.Trim();
        preset.SavedUtc = DateTime.UtcNow;
        Directory.CreateDirectory(StorageDirectory);
        string path = Path.Combine(StorageDirectory, $"{preset.Id:N}.json");
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(preset, Formatting.Indented));
        File.Move(temporaryPath, path, true);
        preset.CacheFilePath = path;
        return preset;
    }

    public static void Delete(DialogueCachePreset preset)
    {
        if (preset is null)
        {
            return;
        }

        string path = string.IsNullOrWhiteSpace(preset.CacheFilePath)
            ? Path.Combine(StorageDirectory, $"{preset.Id:N}.json")
            : preset.CacheFilePath;
        string root = Path.GetFullPath(StorageDirectory).TrimEnd(Path.DirectorySeparatorChar)
                      + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(path);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete a dialogue cache outside the cache preset directory.");
        }
        if (File.Exists(target))
        {
            File.Delete(target);
        }
    }

    private static DialogueCachePreset TryRead(string path)
    {
        try
        {
            DialogueCachePreset preset = JsonConvert.DeserializeObject<DialogueCachePreset>(File.ReadAllText(path));
            if (preset is null || preset.Version != CurrentVersion || preset.Id == Guid.Empty
                || string.IsNullOrWhiteSpace(preset.Label) || preset.Nodes.Count == 0)
            {
                return null;
            }
            preset.CacheFilePath = path;
            return preset;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class DialogueCachePreset
{
    public int Version { get; set; }
    public Guid Id { get; set; }
    public string Label { get; set; }
    public string SourceFilePath { get; set; }
    public string PccName { get; set; }
    public string DialogueName { get; set; }
    public string DialogueExportPath { get; set; }
    public int DialogueUIndex { get; set; }
    public MEGame Game { get; set; }
    public DateTime SavedUtc { get; set; }
    public DateTime SourceLastWriteUtc { get; set; }
    public long SourceFileSize { get; set; }
    public bool StartNodeIsReply { get; set; }
    public int StartNodeIndex { get; set; }
    public List<DialogueCacheNodePreset> Nodes { get; set; } = [];

    [JsonIgnore]
    public string CacheFilePath { get; set; }
    [JsonIgnore]
    public string SavedDisplay => SavedUtc.ToLocalTime().ToString("g");
    [JsonIgnore]
    public string Details => $"{PccName}  |  {DialogueName}  |  {Nodes.Count} node(s)";
}

public sealed class DialogueCacheNodePreset
{
    public bool IsReply { get; set; }
    public int NodeIndex { get; set; }
    public int LineStrRef { get; set; }
    public List<PackageExportReference> InterpDatas { get; set; } = [];
    public PackageExportReference PrimaryTrackMove { get; set; }
    public List<DialogueTrackMoveCache> TrackMoves { get; set; } = [];
    public List<PackageExportReference> ExtraTrackMoves { get; set; } = [];
    public List<DialogueDirectorCache> DirectorTracks { get; set; } = [];
    public List<PackageExportReference> CameraTracks { get; set; } = [];
    public List<DialogueGestureTrackCache> GestureTracks { get; set; } = [];
    public Dictionary<string, PackageExportReference> ActorTrackAssignments { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PackageExportReference> ActorGestureAssignments { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<DialogueDirectionTrackCache> DirectionTracks { get; set; } = [];
    public List<DialogueFaceOnlyVoCache> FaceOnlyVoEvents { get; set; } = [];
    public PackageExportReference DialogueAudio { get; set; }
    public Dictionary<string, DialogueOriginCache> StartActorOrigins { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DialogueOriginCache> EndActorOrigins { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DialogueOriginCache> ActorOriginOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<DialogueMatrixCache>> StartActorGesturePoses { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<DialogueMatrixCache>> EndActorGesturePoses { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public bool HasPendingPreviewChanges { get; set; }
}

public sealed class DialogueTrackMoveCache
{
    public string DisplayName { get; set; }
    public string TabDisplayName { get; set; }
    public PackageExportReference Group { get; set; }
    public PackageExportReference TrackMove { get; set; }
    public CurveEditor3DModelSnapshot Model { get; set; }
    public PackageExportReference FovExport { get; set; }
    public CurveEditor3DFovModelSnapshot FovModel { get; set; }
}

public sealed class DialogueDirectorCache
{
    public string DisplayName { get; set; }
    public PackageExportReference DirectorTrack { get; set; }
    public List<DialogueDirectorCutCache> Cuts { get; set; } = [];
}

public sealed class DialogueDirectorCutCache
{
    public float Time { get; set; }
    public string GroupName { get; set; }
    public PackageExportReference CameraTrack { get; set; }
    public PackageExportReference SwitchCameraTrack { get; set; }
    public string CameraActorTag { get; set; }
    public PackageExportReference CameraActor { get; set; }
    public DialogueOriginCache FallbackOrigin { get; set; }
    public float? FallbackFovDegrees { get; set; }
}

public sealed class DialogueGestureTrackCache
{
    public string DisplayName { get; set; }
    public string Status { get; set; }
    public PackageExportReference Group { get; set; }
    public PackageExportReference Track { get; set; }
    public DialogueGestureStartingPoseCache StartingPose { get; set; }
    public List<DialogueGestureClipCache> Timeline { get; set; } = [];
}

public sealed class DialogueGestureStartingPoseCache
{
    public PackageExportReference Animation { get; set; }
    public DialogueGestureSettingsCache Settings { get; set; }
}

public sealed class DialogueGestureSettingsCache
{
    public float PlayRate { get; set; } = 1;
    public float StartOffset { get; set; }
    public float EndOffset { get; set; }
    public float StartBlendDuration { get; set; }
    public float EndBlendDuration { get; set; }
    public float Weight { get; set; } = 1;
    public float TransitionBlendTime { get; set; }
    public bool InvalidData { get; set; }
    public bool OneShotAnimation { get; set; }
    public bool ChainToPrevious { get; set; }
    public bool PlayUntilNext { get; set; }
    public bool TerminateAllGestures { get; set; }
    public bool UseDynamicAnimationSets { get; set; }
    public bool SnapToPose { get; set; }
    public string PoseFilter { get; set; }
    public string Pose { get; set; }
    public string GestureFilter { get; set; }
    public string Gesture { get; set; }
    public string ChainedGestures { get; set; }
}

public sealed class DialogueGestureClipCache
{
    public PackageExportReference Animation { get; set; }
    public float StartTime { get; set; }
    public float EndTime { get; set; }
    public float AnimationStartTime { get; set; }
    public float AnimationEndTime { get; set; }
    public float PlayRate { get; set; } = 1;
    public float BlendInDuration { get; set; }
    public float BlendOutDuration { get; set; }
    public float Weight { get; set; } = 1;
    public bool Loop { get; set; }
    public bool IsBaseLayer { get; set; }
    public bool UseMotionBoneMask { get; set; }
}

public sealed class DialogueDirectionTrackCache
{
    public string ActorTag { get; set; }
    public bool IsLookAt { get; set; }
    public List<DialogueDirectionKeyCache> Keys { get; set; } = [];
}

public sealed class DialogueDirectionKeyCache
{
    public float Time { get; set; }
    public bool Enabled { get; set; }
    public string TargetActorTag { get; set; }
    public string TargetStageNode { get; set; }
    public float OrientationOffset { get; set; }
}

public sealed class DialogueFaceOnlyVoCache
{
    public float StartTime { get; set; }
    public PackageExportReference Track { get; set; }
    public PackageExportReference Group { get; set; }
    public bool NodeIsReply { get; set; }
    public int NodeIndex { get; set; }
    public int LineStrRef { get; set; }
    public string ActorTag { get; set; }
}

public sealed class PackageExportReference
{
    public string PackagePath { get; set; }
    public int UIndex { get; set; }
    public string InstancedFullPath { get; set; }
    public string ClassName { get; set; }
}

public sealed class DialogueOriginCache
{
    public Vector3 Location { get; set; }
    public Vector3 Rotation { get; set; }
}

public sealed class DialogueMatrixCache
{
    public float M11 { get; set; }
    public float M12 { get; set; }
    public float M13 { get; set; }
    public float M14 { get; set; }
    public float M21 { get; set; }
    public float M22 { get; set; }
    public float M23 { get; set; }
    public float M24 { get; set; }
    public float M31 { get; set; }
    public float M32 { get; set; }
    public float M33 { get; set; }
    public float M34 { get; set; }
    public float M41 { get; set; }
    public float M42 { get; set; }
    public float M43 { get; set; }
    public float M44 { get; set; }

    public static DialogueMatrixCache FromMatrix(Matrix4x4 matrix) => new()
    {
        M11 = matrix.M11, M12 = matrix.M12, M13 = matrix.M13, M14 = matrix.M14,
        M21 = matrix.M21, M22 = matrix.M22, M23 = matrix.M23, M24 = matrix.M24,
        M31 = matrix.M31, M32 = matrix.M32, M33 = matrix.M33, M34 = matrix.M34,
        M41 = matrix.M41, M42 = matrix.M42, M43 = matrix.M43, M44 = matrix.M44,
    };

    public Matrix4x4 ToMatrix() => new(
        M11, M12, M13, M14,
        M21, M22, M23, M24,
        M31, M32, M33, M34,
        M41, M42, M43, M44);
}
