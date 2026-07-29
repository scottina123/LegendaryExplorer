using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore.Matinee;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.Tools.InterpEditor;

public enum CameraPresetCategory
{
    StaticShots,
    DynamicShots,
    ReactionShots,
    SavedTrackMoves
}

public static class MulticamCameraPresetApplicator
{
    public static bool TryApply(MulticamCameraPreset preset, ExportEntry directorTrack, ExportEntry interpData,
        CameraOrigin origin, float destinationDuration, out string error)
    {
        error = null;
        if (preset is null || directorTrack?.ClassName != "InterpTrackDirector" || interpData?.ClassName != "InterpData")
        {
            error = "Select a valid multicam preset and destination Director track.";
            return false;
        }
        if (preset.Duration <= 0 || destinationDuration <= 0 || preset.CameraGroups is not { Count: >= 2 }
            || preset.DirectorKeys is not { Count: >= 2 })
        {
            error = "The multicam preset and destination must have positive durations and at least two cameras and cuts.";
            return false;
        }

        float timeScale = destinationDuration / preset.Duration;
        var destinationGroups = new Dictionary<string, ExportEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (MulticamCameraGroup cameraGroup in preset.CameraGroups)
        {
            if (string.IsNullOrWhiteSpace(cameraGroup.GroupName) || cameraGroup.TrackMoveKeys is not { Count: > 0 })
            {
                error = "Every multicam camera requires a name and TrackMove keys.";
                return false;
            }

            ExportEntry destinationGroup = FindMatchingGroup(interpData, cameraGroup.GroupName)
                ?? MatineeHelper.AddPreset("Camera", interpData, interpData.Game, cameraGroup.GroupName);
            if (destinationGroup is null)
            {
                error = $"Unable to create camera group '{cameraGroup.GroupName}'.";
                return false;
            }

            var groupProperties = destinationGroup.GetProperties();
            groupProperties.AddOrReplaceProp(new NameProperty(cameraGroup.GroupName, "GroupName"));
            if (!string.IsNullOrWhiteSpace(cameraGroup.FindActorName))
            {
                groupProperties.AddOrReplaceProp(new NameProperty(cameraGroup.FindActorName, "m_nmSFXFindActor"));
            }
            destinationGroup.WriteProperties(groupProperties);

            ExportEntry trackMove = FindTrack(destinationGroup, "InterpTrackMove")
                ?? CreateTrack(destinationGroup, "InterpTrackMove");
            WriteTrackMove(trackMove, cameraGroup.TrackMoveKeys, origin, timeScale, destinationDuration);

            if (cameraGroup.FovKeys is { Count: > 0 })
            {
                ExportEntry fovTrack = FindFovTrack(destinationGroup) ?? CreateFovTrack(destinationGroup);
                WriteFovTrack(fovTrack, cameraGroup.FovKeys, timeScale, destinationDuration);
            }
            destinationGroups[cameraGroup.GroupName] = destinationGroup;
        }

        if (preset.DirectorKeys.Any(key => !destinationGroups.ContainsKey(key.GroupName)))
        {
            error = "A Director key references a camera group that is not included in the preset.";
            return false;
        }

        WriteDirectorTrack(directorTrack, preset.DirectorKeys, destinationGroups, timeScale, destinationDuration);
        return true;
    }

    private static ExportEntry FindMatchingGroup(ExportEntry interpData, string groupName)
    {
        ArrayProperty<ObjectProperty> groupRefs = interpData.GetProperty<ArrayProperty<ObjectProperty>>("InterpGroups");
        return groupRefs?.Select(reference => interpData.FileRef.TryGetUExport(reference.Value, out ExportEntry group) ? group : null)
            .Where(group => group?.ClassName == "InterpGroup")
            .FirstOrDefault(group => string.Equals(group.GetProperty<NameProperty>("GroupName")?.Value.Instanced,
                groupName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<ExportEntry> GetTracks(ExportEntry group) =>
        group.GetProperty<ArrayProperty<ObjectProperty>>("InterpTracks")?
            .Select(reference => group.FileRef.TryGetUExport(reference.Value, out ExportEntry track) ? track : null)
            .Where(track => track is not null) ?? [];

    private static ExportEntry FindTrack(ExportEntry group, string className) =>
        GetTracks(group).FirstOrDefault(track => track.ClassName == className);

    private static ExportEntry FindFovTrack(ExportEntry group) =>
        GetTracks(group).FirstOrDefault(track => track.ClassName == "InterpTrackFloatProp"
            && (string.Equals(track.GetProperty<NameProperty>("PropertyName")?.Value.Instanced, "FOVAngle", StringComparison.OrdinalIgnoreCase)
                || string.Equals(track.GetProperty<StrProperty>("TrackTitle")?.Value, "FOVAngle", StringComparison.OrdinalIgnoreCase)));

    private static ExportEntry CreateTrack(ExportEntry group, string className)
    {
        ExportEntry track = MatineeHelper.AddNewTrackToGroup(group, className);
        MatineeHelper.AddDefaultPropertiesToTrack(track);
        return track;
    }

    private static ExportEntry CreateFovTrack(ExportEntry group)
    {
        ExportEntry track = CreateTrack(group, "InterpTrackFloatProp");
        track.WriteProperty(new StrProperty("FOVAngle", "TrackTitle"));
        track.WriteProperty(new NameProperty("FOVAngle", "PropertyName"));
        return track;
    }

    private static void WriteTrackMove(ExportEntry trackMove, IReadOnlyList<CameraPresetLocalKey> keys,
        CameraOrigin origin, float timeScale, float destinationDuration)
    {
        var properties = trackMove.GetProperties();
        StructProperty lookupTrack = properties.GetProp<StructProperty>("LookupTrack");
        StructProperty positionTrack = properties.GetProp<StructProperty>("PosTrack");
        StructProperty rotationTrack = properties.GetProp<StructProperty>("EulerTrack");
        if (lookupTrack is null || positionTrack is null || rotationTrack is null)
        {
            MatineeHelper.AddDefaultPropertiesToTrack(trackMove);
            properties = trackMove.GetProperties();
            lookupTrack = properties.GetProp<StructProperty>("LookupTrack");
            positionTrack = properties.GetProp<StructProperty>("PosTrack");
            rotationTrack = properties.GetProp<StructProperty>("EulerTrack");
        }

        ArrayProperty<StructProperty> lookupPoints = lookupTrack.GetProp<ArrayProperty<StructProperty>>("Points");
        ArrayProperty<StructProperty> positionPoints = positionTrack.GetProp<ArrayProperty<StructProperty>>("Points");
        ArrayProperty<StructProperty> rotationPoints = rotationTrack.GetProp<ArrayProperty<StructProperty>>("Points");
        lookupPoints.Clear();
        positionPoints.Clear();
        rotationPoints.Clear();

        CameraPresetGenerator.BuildBasis(origin.Rotation, out Vector3 forward, out Vector3 right, out Vector3 up);
        foreach (CameraPresetLocalKey key in keys.OrderBy(key => key.TimeOffset))
        {
            float time = Math.Clamp(key.TimeOffset * timeScale, 0, destinationDuration);
            Vector3 position = origin.Location + ToWorldVector(key.LocalPosition, forward, right, up);
            Vector3 rotation = CameraPresetGenerator.LocalRotationToWorld(key.LocalRotation, origin.Rotation);
            EInterpCurveMode mode = MapInterpolation(key.Interpolation, trackMove.Game);
            lookupPoints.Add(new StructProperty("InterpLookupPoint", false,
                new NameProperty("None", "GroupName"), new FloatProperty(time, "Time")));
            positionPoints.Add(new InterpCurvePoint<Vector3>(time, position,
                ToWorldVector(key.PositionArriveTangent, forward, right, up) / Math.Max(timeScale, float.Epsilon),
                ToWorldVector(key.PositionLeaveTangent, forward, right, up) / Math.Max(timeScale, float.Epsilon), mode)
                .ToStructProperty(trackMove.Game));
            rotationPoints.Add(new InterpCurvePoint<Vector3>(time, rotation,
                key.RotationArriveTangent / Math.Max(timeScale, float.Epsilon),
                key.RotationLeaveTangent / Math.Max(timeScale, float.Epsilon), mode)
                .ToStructProperty(trackMove.Game));
        }
        trackMove.WriteProperties(properties);
    }

    private static void WriteFovTrack(ExportEntry fovTrack, IReadOnlyList<MulticamFovKey> keys,
        float timeScale, float destinationDuration)
    {
        StructProperty floatTrack = fovTrack.GetProperty<StructProperty>("FloatTrack");
        if (floatTrack is null)
        {
            floatTrack = new InterpCurve<float>().ToStructProperty(fovTrack.Game, "FloatTrack");
        }
        ArrayProperty<StructProperty> points = floatTrack.GetProp<ArrayProperty<StructProperty>>("Points");
        points.Clear();
        foreach (MulticamFovKey key in keys.OrderBy(key => key.TimeOffset))
        {
            float time = Math.Clamp(key.TimeOffset * timeScale, 0, destinationDuration);
            points.Add(new InterpCurvePoint<float>(time, key.Value,
                key.ArriveTangent / Math.Max(timeScale, float.Epsilon),
                key.LeaveTangent / Math.Max(timeScale, float.Epsilon), MapInterpolation(key.Interpolation, fovTrack.Game))
                .ToStructProperty(fovTrack.Game));
        }
        fovTrack.WriteProperty(floatTrack);
    }

    private static void WriteDirectorTrack(ExportEntry directorTrack, IReadOnlyList<MulticamDirectorKey> keys,
        IReadOnlyDictionary<string, ExportEntry> destinationGroups, float timeScale, float destinationDuration)
    {
        MulticamDirectorKey[] cuts = keys.Select(key =>
        {
            ExportEntry destinationGroup = destinationGroups[key.GroupName];
            string actualGroupName = destinationGroup.GetProperty<NameProperty>("GroupName")?.Value.Instanced ?? key.GroupName;
            return new MulticamDirectorKey(Math.Clamp(key.TimeOffset * timeScale, 0, destinationDuration), actualGroupName);
        }).ToArray();
        new InterpTrackDirector(directorTrack).ReplaceCuts(cuts);
    }

    private static Vector3 ToWorldVector(Vector3 local, Vector3 forward, Vector3 right, Vector3 up) =>
        forward * local.X + right * local.Y + up * local.Z;

    private static EInterpCurveMode MapInterpolation(CameraKeyInterpolation interpolation, MEGame game) => interpolation switch
    {
        CameraKeyInterpolation.Constant => EInterpCurveMode.CIM_Constant,
        CameraKeyInterpolation.Linear => EInterpCurveMode.CIM_Linear,
        CameraKeyInterpolation.SmoothClamped when game.IsGame3() => EInterpCurveMode.CIM_CurveAutoClamped,
        _ => EInterpCurveMode.CIM_CurveAuto
    };
}

public static class MulticamCameraPresetCapture
{
    public static bool TryCapture(ExportEntry directorTrack, ExportEntry interpData, CameraOrigin origin,
        string name, string description, MulticamPresetType? typeOverride,
        out MulticamCameraPreset preset, out string error)
    {
        preset = null;
        error = null;
        if (directorTrack?.ClassName != "InterpTrackDirector" || interpData?.ClassName != "InterpData")
        {
            error = "Select a Director track that belongs to an InterpData.";
            return false;
        }

        ArrayProperty<StructProperty> cutTrack = directorTrack.GetProperty<ArrayProperty<StructProperty>>("CutTrack");
        if (cutTrack is not { Count: >= 2 })
        {
            error = "The selected Director must contain at least two camera cuts.";
            return false;
        }

        float firstTime = cutTrack.Min(cut => cut.GetProp<FloatProperty>("Time")?.Value ?? 0);
        var directorKeys = cutTrack
            .Select(cut => new MulticamDirectorKey(
                (cut.GetProp<FloatProperty>("Time")?.Value ?? firstTime) - firstTime,
                cut.GetProp<NameProperty>("TargetCamGroup")?.Value.Instanced))
            .OrderBy(key => key.TimeOffset)
            .ToArray();
        if (directorKeys.Any(key => string.IsNullOrWhiteSpace(key.GroupName)))
        {
            error = "Every Director key must reference a named camera group.";
            return false;
        }

        var groups = new List<MulticamCameraGroup>();
        foreach (string groupName in directorKeys.Select(key => key.GroupName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ExportEntry group = FindReferencedGroup(interpData, groupName);
            if (group is null)
            {
                error = $"The Director references camera group '{groupName}', but that group was not found.";
                return false;
            }

            ExportEntry trackMove = FindTrack(group, "InterpTrackMove");
            if (trackMove is null || !TryCaptureTrackMove(trackMove, origin, firstTime, out IReadOnlyList<CameraPresetLocalKey> keys))
            {
                error = $"Camera group '{groupName}' does not contain a valid synchronized TrackMove.";
                return false;
            }

            IReadOnlyList<MulticamFovKey> fovKeys = CaptureFovKeys(group, firstTime);
            bool isStatic = IsStatic(keys, fovKeys);
            string findActorName = group.GetProperty<NameProperty>("m_nmSFXFindActor")?.Value.Instanced ?? groupName;
            string movementName = isStatic ? "Static" : GetMovementName(group, trackMove);
            groups.Add(new MulticamCameraGroup(groupName, findActorName, isStatic, movementName, keys, fovKeys));
        }

        float capturedEnd = Math.Max(directorKeys.Max(key => key.TimeOffset), groups
            .SelectMany(group => group.TrackMoveKeys.Select(key => key.TimeOffset))
            .Concat(groups.SelectMany(group => group.FovKeys ?? []).Select(key => key.TimeOffset))
            .DefaultIfEmpty(0).Max());
        float interpLength = interpData.GetProperty<FloatProperty>("InterpLength")?.Value ?? 0;
        float duration = Math.Max(capturedEnd, interpLength - firstTime);
        if (duration <= float.Epsilon)
        {
            error = "The Director sequence has no positive duration.";
            return false;
        }

        MulticamCameraGroup firstGroup = groups.First(group =>
            string.Equals(group.GroupName, directorKeys[0].GroupName, StringComparison.OrdinalIgnoreCase));
        MulticamCameraGroup lastGroup = groups.First(group =>
            string.Equals(group.GroupName, directorKeys[^1].GroupName, StringComparison.OrdinalIgnoreCase));
        MulticamPresetType inferredType = (firstGroup.IsStatic, lastGroup.IsStatic) switch
        {
            (true, true) => MulticamPresetType.StaticToStatic,
            (true, false) => MulticamPresetType.StaticToDynamic,
            (false, true) => MulticamPresetType.DynamicToStatic,
            _ => MulticamPresetType.DynamicToDynamic
        };
        preset = new MulticamCameraPreset(name.Trim(), typeOverride ?? inferredType, duration,
            directorKeys, groups, description?.Trim(),
            groups.Select(group => group.MovementName).Concat(groups.Select(group => group.GroupName)).Distinct().ToArray());
        return true;
    }

    private static ExportEntry FindReferencedGroup(ExportEntry interpData, string groupName)
    {
        ArrayProperty<ObjectProperty> groupRefs = interpData.GetProperty<ArrayProperty<ObjectProperty>>("InterpGroups");
        return groupRefs?.Select(reference => interpData.FileRef.TryGetUExport(reference.Value, out ExportEntry group) ? group : null)
            .Where(group => group?.ClassName == "InterpGroup")
            .FirstOrDefault(group => string.Equals(group.GetProperty<NameProperty>("GroupName")?.Value.Instanced,
                groupName, StringComparison.OrdinalIgnoreCase));
    }

    private static ExportEntry FindTrack(ExportEntry group, string className)
    {
        ArrayProperty<ObjectProperty> trackRefs = group.GetProperty<ArrayProperty<ObjectProperty>>("InterpTracks");
        return trackRefs?.Select(reference => group.FileRef.TryGetUExport(reference.Value, out ExportEntry track) ? track : null)
            .FirstOrDefault(track => track?.ClassName == className);
    }

    private static bool TryCaptureTrackMove(ExportEntry trackMove, CameraOrigin origin, float sequenceStart,
        out IReadOnlyList<CameraPresetLocalKey> keys)
    {
        keys = null;
        ArrayProperty<StructProperty> lookupPoints = trackMove.GetProperty<StructProperty>("LookupTrack")?
            .GetProp<ArrayProperty<StructProperty>>("Points");
        ArrayProperty<StructProperty> positionPoints = trackMove.GetProperty<StructProperty>("PosTrack")?
            .GetProp<ArrayProperty<StructProperty>>("Points");
        ArrayProperty<StructProperty> rotationPoints = trackMove.GetProperty<StructProperty>("EulerTrack")?
            .GetProp<ArrayProperty<StructProperty>>("Points");
        if (lookupPoints is not { Count: > 0 } || positionPoints is null || rotationPoints is null
            || lookupPoints.Count != positionPoints.Count || lookupPoints.Count != rotationPoints.Count)
        {
            return false;
        }

        CameraPresetGenerator.BuildBasis(origin.Rotation, out Vector3 forward, out Vector3 right, out Vector3 up);
        var captured = new List<CameraPresetLocalKey>(lookupPoints.Count);
        for (int i = 0; i < lookupPoints.Count; i++)
        {
            InterpCurvePoint<Vector3> position = InterpCurvePoint<Vector3>.FromStructProperty(positionPoints[i]);
            InterpCurvePoint<Vector3> rotation = InterpCurvePoint<Vector3>.FromStructProperty(rotationPoints[i]);
            Vector3 delta = position.OutVal - origin.Location;
            captured.Add(new CameraPresetLocalKey(
                Math.Max(0, (lookupPoints[i].GetProp<FloatProperty>("Time")?.Value ?? position.InVal) - sequenceStart),
                CameraPresetTrackCapture.ToLocalVector(delta, forward, right, up),
                CameraPresetGenerator.WorldRotationToLocal(rotation.OutVal, origin.Rotation),
                MapInterpolation(position.InterpMode),
                CameraPresetTrackCapture.ToLocalVector(position.ArriveTangent, forward, right, up),
                CameraPresetTrackCapture.ToLocalVector(position.LeaveTangent, forward, right, up),
                rotation.ArriveTangent, rotation.LeaveTangent));
        }
        keys = captured;
        return true;
    }

    private static IReadOnlyList<MulticamFovKey> CaptureFovKeys(ExportEntry group, float sequenceStart)
    {
        ExportEntry fovTrack = GetTracks(group).FirstOrDefault(track => track.ClassName == "InterpTrackFloatProp"
            && (string.Equals(track.GetProperty<NameProperty>("PropertyName")?.Value.Instanced, "FOVAngle", StringComparison.OrdinalIgnoreCase)
                || string.Equals(track.GetProperty<StrProperty>("TrackTitle")?.Value, "FOVAngle", StringComparison.OrdinalIgnoreCase)));
        StructProperty floatTrack = fovTrack?.GetProperty<StructProperty>("FloatTrack");
        ArrayProperty<StructProperty> points = floatTrack?.GetProp<ArrayProperty<StructProperty>>("Points");
        return points?.Select(point => InterpCurvePoint<float>.FromStructProperty(point))
            .Select(point => new MulticamFovKey(Math.Max(0, point.InVal - sequenceStart), point.OutVal,
                point.ArriveTangent, point.LeaveTangent, MapInterpolation(point.InterpMode))).ToArray() ?? [];
    }

    private static IEnumerable<ExportEntry> GetTracks(ExportEntry group) =>
        group.GetProperty<ArrayProperty<ObjectProperty>>("InterpTracks")?
            .Select(reference => group.FileRef.TryGetUExport(reference.Value, out ExportEntry track) ? track : null)
            .Where(track => track is not null) ?? [];

    private static bool IsStatic(IReadOnlyList<CameraPresetLocalKey> keys, IReadOnlyList<MulticamFovKey> fovKeys)
    {
        CameraPresetLocalKey first = keys[0];
        bool transformIsStatic = keys.All(key => Vector3.DistanceSquared(key.LocalPosition, first.LocalPosition) < 0.0001f
            && Vector3.DistanceSquared(key.LocalRotation, first.LocalRotation) < 0.0001f);
        bool fovIsStatic = fovKeys is not { Count: > 1 }
            || fovKeys.All(key => Math.Abs(key.Value - fovKeys[0].Value) < 0.0001f);
        return transformIsStatic && fovIsStatic;
    }

    private static string GetMovementName(ExportEntry group, ExportEntry trackMove) =>
        trackMove.GetProperty<StrProperty>("TrackTitle")?.Value
        ?? group.GetProperty<NameProperty>("GroupName")?.Value.Instanced
        ?? "Dynamic Camera";

    internal static CameraKeyInterpolation MapInterpolation(EInterpCurveMode mode) => mode switch
    {
        EInterpCurveMode.CIM_Constant => CameraKeyInterpolation.Constant,
        EInterpCurveMode.CIM_Linear => CameraKeyInterpolation.Linear,
        EInterpCurveMode.CIM_CurveAutoClamped => CameraKeyInterpolation.SmoothClamped,
        _ => CameraKeyInterpolation.Smooth
    };
}

public enum CameraPathKind
{
    Static,
    Push,
    Pull,
    DollyLeft,
    DollyRight,
    ArcLeft,
    ArcRight,
    OrbitLeft,
    OrbitRight,
    CraneUp,
    CraneDown,
    RiseAndPush,
    LowerAndPush,
    SlideLeft,
    SlideRight,
    SlidePush,
    SlidePull,
    Follow,
    Lead,
    SideFollow,
    RevealLeft,
    RevealRight,
    OrbitPush,
    OrbitPull,
    PushCrane,
    PullCrane,
    MoveThenHold,
    HoldThenMove,
    Reframe,
    TiltDown,
    TiltUp,
    ZoomIn,
    ZoomOut,
    Tracking,
    Drift
}

public enum CameraKeyInterpolation
{
    Constant,
    Linear,
    Smooth,
    SmoothClamped
}

public readonly record struct CameraOrigin(Vector3 Location, Vector3 Rotation);

public readonly record struct GeneratedCameraKey(float TimeOffset, Vector3 Location, Vector3 Rotation,
    CameraKeyInterpolation Interpolation);

public readonly record struct CameraPresetLocalKey(float TimeOffset, Vector3 LocalPosition, Vector3 LocalRotation,
    CameraKeyInterpolation Interpolation, Vector3 PositionArriveTangent = default,
    Vector3 PositionLeaveTangent = default, Vector3 RotationArriveTangent = default,
    Vector3 RotationLeaveTangent = default);

public sealed record CameraPreset(
    string Name,
    CameraPresetCategory Category,
    float ForwardDistance,
    float SideOffset,
    float HeightOffset,
    float LookAtHeight,
    float LocalYaw = 0,
    float LocalPitch = 0,
    float LocalRoll = 0,
    float Duration = 0,
    int KeyCount = 1,
    CameraPathKind PathKind = CameraPathKind.Static,
    float MovementAmount = 0,
    CameraKeyInterpolation Interpolation = CameraKeyInterpolation.Constant,
    IReadOnlyList<CameraPresetLocalKey> LocalKeys = null)
{
    public bool IsSavedTrackMove => Category == CameraPresetCategory.SavedTrackMoves && LocalKeys is { Count: > 0 };
}

public enum MulticamPresetType
{
    StaticToStatic,
    StaticToDynamic,
    DynamicToStatic,
    DynamicToDynamic
}

public readonly record struct MulticamDirectorKey(float TimeOffset, string GroupName);

public readonly record struct MulticamFovKey(float TimeOffset, float Value, float ArriveTangent, float LeaveTangent,
    CameraKeyInterpolation Interpolation);

public sealed record MulticamCameraGroup(
    string GroupName,
    string FindActorName,
    bool IsStatic,
    string MovementName,
    IReadOnlyList<CameraPresetLocalKey> TrackMoveKeys,
    IReadOnlyList<MulticamFovKey> FovKeys = null,
    string AnchorRole = null);

public sealed record MulticamCameraPreset(
    string Name,
    MulticamPresetType Type,
    float Duration,
    IReadOnlyList<MulticamDirectorKey> DirectorKeys,
    IReadOnlyList<MulticamCameraGroup> CameraGroups,
    string Description = null,
    IReadOnlyList<string> SearchableMetadata = null,
    bool IsBuiltIn = false)
{
    public string TypeDisplay => Type switch
    {
        MulticamPresetType.StaticToStatic => "Static → Static",
        MulticamPresetType.StaticToDynamic => "Static → Dynamic",
        MulticamPresetType.DynamicToStatic => "Dynamic → Static",
        MulticamPresetType.DynamicToDynamic => "Dynamic → Dynamic",
        _ => Type.ToString()
    };
}

public static class CameraPresetCatalog
{
    public static IReadOnlyList<CameraPreset> All { get; } = BuildPresets();

    public static IReadOnlyList<CameraPreset> GetByCategory(CameraPresetCategory category) =>
        All.Where(preset => preset.Category == category).ToList();

    private static IReadOnlyList<CameraPreset> BuildPresets()
    {
        var presets = new List<CameraPreset>();

        void Static(string name, float distance = 180, float side = 0, float height = 70, float lookHeight = 70,
            float yaw = 0, float pitch = 0, float roll = 0) =>
            presets.Add(new CameraPreset(name, CameraPresetCategory.StaticShots, distance, side, height, lookHeight, yaw, pitch, roll));

        Static("Front Close-Up", 115, 0, 78, 78);
        Static("Left Three-Quarter Close-Up", 130, -55, 78, 76);
        Static("Right Three-Quarter Close-Up", 130, 55, 78, 76);
        Static("Front Medium Shot", 220, 0, 72, 68);
        Static("Left Medium Shot", 225, -75, 72, 68);
        Static("Right Medium Shot", 225, 75, 72, 68);
        Static("Medium Wide Shot", 340, 0, 95, 65);
        Static("Full Body Shot", 410, 0, 95, 50);
        Static("Cowboy Shot", 300, 0, 85, 62);
        Static("Over-the-Shoulder Left", 180, -105, 92, 68);
        Static("Over-the-Shoulder Right", 180, 105, 92, 68);
        Static("Dirty Over-the-Shoulder Left", 145, -75, 85, 70);
        Static("Dirty Over-the-Shoulder Right", 145, 75, 85, 70);
        Static("Reverse Over-the-Shoulder", -190, 100, 90, 68);
        Static("Two-Shot", 330, 0, 90, 67);
        Static("Tight Two-Shot", 230, 0, 78, 70);
        Static("Profile Left", 20, -220, 75, 70);
        Static("Profile Right", 20, 220, 75, 70);
        Static("Rear Three-Quarter", -180, -110, 88, 65);
        Static("Rear Profile", -25, -230, 82, 68);
        Static("Centered Wide Shot", 520, 0, 130, 60);
        Static("Symmetrical Shot", 360, 0, 80, 65);
        Static("Rule-of-Thirds Left", 260, -90, 78, 68);
        Static("Rule-of-Thirds Right", 260, 90, 78, 68);
        Static("Negative Space Left", 360, -180, 90, 65);
        Static("Negative Space Right", 360, 180, 90, 65);
        Static("Hero Low-Angle Shot", 230, 0, 20, 85);
        Static("Low-Angle Shot", 260, 0, 35, 78);
        Static("Eye-Level Shot", 240, 0, 72, 72);
        Static("High-Angle Shot", 240, 0, 155, 62);
        Static("Bird's-Eye Shot", 80, 0, 520, 45);
        Static("Dutch-Angle Left", 220, -30, 78, 68, roll: -12);
        Static("Dutch-Angle Right", 220, 30, 78, 68, roll: 12);
        Static("Silhouette Shot", 420, 0, 100, 70);
        Static("Environmental Portrait", 500, 100, 130, 65);
        Static("Insert Shot", 80, 0, 45, 45);
        Static("Reaction Shot", 155, 35, 76, 74);
        Static("Custom Static Shot", 220, 0, 75, 70);

        void Dynamic(string name, CameraPathKind path, float distance = 360, float side = 0, float height = 90,
            float lookHeight = 70, float duration = 3, float movement = 120, int keys = 8,
            CameraKeyInterpolation interpolation = CameraKeyInterpolation.SmoothClamped) =>
            presets.Add(new CameraPreset(name, CameraPresetCategory.DynamicShots, distance, side, height, lookHeight,
                Duration: duration, KeyCount: keys, PathKind: path, MovementAmount: movement, Interpolation: interpolation));

        Dynamic("Slow Push-In", CameraPathKind.Push, duration: 5, movement: 180, keys: 12);
        Dynamic("Fast Push-In", CameraPathKind.Push, duration: 1.5f, movement: 200, keys: 6, interpolation: CameraKeyInterpolation.Linear);
        Dynamic("Emotional Push-In", CameraPathKind.Push, 240, 25, 78, 74, 4, 100, 10);
        Dynamic("Slow Pull-Out", CameraPathKind.Pull, 180, duration: 5, movement: 220, keys: 12);
        Dynamic("Reveal Pull-Out", CameraPathKind.Pull, 130, 70, 85, 70, 4, 300, 10);
        Dynamic("Dolly Left", CameraPathKind.DollyLeft, movement: 220, interpolation: CameraKeyInterpolation.Linear);
        Dynamic("Dolly Right", CameraPathKind.DollyRight, movement: 220, interpolation: CameraKeyInterpolation.Linear);
        Dynamic("Dolly Forward", CameraPathKind.Push, movement: 180, interpolation: CameraKeyInterpolation.Linear);
        Dynamic("Dolly Backward", CameraPathKind.Pull, 180, movement: 180, interpolation: CameraKeyInterpolation.Linear);
        Dynamic("Arc Left", CameraPathKind.ArcLeft, movement: 35);
        Dynamic("Arc Right", CameraPathKind.ArcRight, movement: 35);
        Dynamic("Wide Arc Left", CameraPathKind.ArcLeft, 480, duration: 5, movement: 70, keys: 14);
        Dynamic("Wide Arc Right", CameraPathKind.ArcRight, 480, duration: 5, movement: 70, keys: 14);
        Dynamic("Orbit Left", CameraPathKind.OrbitLeft, 300, duration: 5, movement: 120, keys: 16);
        Dynamic("Orbit Right", CameraPathKind.OrbitRight, 300, duration: 5, movement: 120, keys: 16);
        Dynamic("Crane Up", CameraPathKind.CraneUp, movement: 260);
        Dynamic("Crane Down", CameraPathKind.CraneDown, height: 300, movement: 240);
        Dynamic("Rise and Push-In", CameraPathKind.RiseAndPush, movement: 180);
        Dynamic("Lower and Push-In", CameraPathKind.LowerAndPush, height: 230, movement: 170);
        Dynamic("Side Slide Left", CameraPathKind.SlideLeft, movement: 240, interpolation: CameraKeyInterpolation.Linear);
        Dynamic("Side Slide Right", CameraPathKind.SlideRight, movement: 240, interpolation: CameraKeyInterpolation.Linear);
        Dynamic("Side Slide with Push-In", CameraPathKind.SlidePush, movement: 180);
        Dynamic("Side Slide with Pull-Out", CameraPathKind.SlidePull, 220, movement: 180);
        Dynamic("Walk-and-Talk Follow", CameraPathKind.Follow, 260, 0, 90, 72, 5, 300, 12, CameraKeyInterpolation.Linear);
        Dynamic("Walk-and-Talk Lead", CameraPathKind.Lead, 260, 0, 90, 72, 5, 300, 12, CameraKeyInterpolation.Linear);
        Dynamic("Walk-and-Talk Side", CameraPathKind.SideFollow, 180, -220, 90, 72, 5, 300, 12, CameraKeyInterpolation.Linear);
        Dynamic("Character Follow", CameraPathKind.Follow, duration: 4, movement: 260, keys: 10, interpolation: CameraKeyInterpolation.Linear);
        Dynamic("Character Lead", CameraPathKind.Lead, duration: 4, movement: 260, keys: 10, interpolation: CameraKeyInterpolation.Linear);
        Dynamic("Reveal Around Corner", CameraPathKind.RevealLeft, 260, -240, movement: 220);
        Dynamic("Reveal Behind Character", CameraPathKind.RevealRight, 180, 180, movement: 200);
        Dynamic("Reveal Over Shoulder", CameraPathKind.RevealLeft, 160, -150, 105, 70, movement: 130);
        Dynamic("Orbit with Push-In", CameraPathKind.OrbitPush, 420, duration: 5, movement: 150, keys: 14);
        Dynamic("Orbit with Pull-Out", CameraPathKind.OrbitPull, 220, duration: 5, movement: 150, keys: 14);
        Dynamic("Push-In with Crane Up", CameraPathKind.PushCrane, movement: 180);
        Dynamic("Pull-Out with Crane Down", CameraPathKind.PullCrane, 180, height: 220, movement: 180);
        Dynamic("Push-In into Static Hold", CameraPathKind.MoveThenHold, movement: 180, duration: 4, keys: 10);
        Dynamic("Static Start into Push-In", CameraPathKind.HoldThenMove, movement: 180, duration: 4, keys: 10);
        Dynamic("Dolly into Static Hold", CameraPathKind.MoveThenHold, side: -160, movement: 220, duration: 4, keys: 10);
        Dynamic("Arc into Static Hold", CameraPathKind.MoveThenHold, side: -180, movement: 180, duration: 4, keys: 10);
        Dynamic("Reframe Between Two Compositions", CameraPathKind.Reframe, 260, -120, 82, 70, 3, 240, 8);

        void Reaction(string name, float distance = 160, float side = 25, float height = 76, float lookHeight = 74,
            float roll = 0) =>
            presets.Add(new CameraPreset(name, CameraPresetCategory.ReactionShots, distance, side, height, lookHeight,
                LocalRoll: roll));

        Reaction("Listener Close-Up", 120, 30, 78, 77);
        Reaction("Listener Medium Shot", 220, 45, 75, 70);
        Reaction("Listener Over-the-Shoulder", 180, -105, 92, 68);
        Reaction("Listener Side Profile", 30, 210, 78, 72);
        Reaction("Silent Reaction", 150, 20, 76, 74);
        Reaction("Emotional Reaction", 110, 15, 77, 77);
        Reaction("Shock Reaction", 125, -20, 72, 79, -3);
        Reaction("Conflicted Reaction", 145, 45, 80, 70, -5);
        Reaction("Amused Reaction", 155, -35, 76, 75);
        Reaction("Concerned Reaction", 140, 30, 82, 72);
        Reaction("Romantic Reaction", 110, -25, 76, 75);
        Reaction("Determined Reaction", 135, 0, 65, 78);
        Reaction("Angry Reaction", 120, 20, 60, 80, 4);
        Reaction("Downward Reflection", 145, -15, 90, 55);
        Reaction("Looking Away", 170, 85, 78, 70);
        Reaction("Eye Contact Hold", 115, 0, 77, 77);
        Reaction("Shared Reaction Two-Shot", 260, 0, 82, 68);
        Reaction("Reverse Reaction", -165, -35, 78, 72);
        Reaction("Delayed Reaction", 175, 35, 78, 72);
        Reaction("Custom Reaction Shot", 160, 25, 76, 74);

        return presets;
    }
}

public static class MulticamCameraPresetCatalog
{
    private const float TemplateDuration = 4f;
    private const float CutTime = TemplateDuration / 2f;

    public static IReadOnlyList<MulticamCameraPreset> All { get; } = BuildPresets();

    public static IReadOnlyList<MulticamCameraPreset> GetByType(MulticamPresetType type) =>
        All.Where(preset => preset.Type == type).ToList();

    private static IReadOnlyList<MulticamCameraPreset> BuildPresets()
    {
        var presets = new List<MulticamCameraPreset>();

        string[] staticToStaticNames =
        [
            "Wide → Wide", "Wide → Medium", "Wide → Close", "Wide → Two Shot", "Wide → OTS",
            "Medium → Wide", "Medium → Medium", "Medium → Close", "Medium → Two Shot", "Medium → OTS",
            "Close → Wide", "Close → Medium", "Close → Close", "Close → Two Shot", "Close → OTS",
            "Two Shot → Wide", "Two Shot → Medium", "Two Shot → Close", "Two Shot → OTS",
            "OTS → Reverse OTS", "OTS → Wide", "OTS → Medium", "OTS → Close", "OTS → Two Shot",
            "Speaker → Listener", "Listener → Speaker", "Speaker Close → Listener Close",
            "Speaker Medium → Listener Medium", "Speaker Wide → Listener Wide", "Reaction → Speaker",
            "Speaker → Reaction", "Two Shot → Speaker Close", "Two Shot → Listener Close",
            "Establishing → Speaker", "Establishing → Two Shot", "Profile → Profile", "Front → Profile",
            "Profile → Front", "High Angle → Low Angle", "Low Angle → High Angle"
        ];
        foreach (string name in staticToStaticNames)
        {
            string[] shots = name.Split(" → ", StringSplitOptions.TrimEntries);
            AddStaticToStatic(name, shots[0], shots[1]);
        }

        string[] dynamicToDynamicNames =
        [
            "Push In → Push In", "Push In → Push Out", "Push In → Dolly In", "Push In → Dolly Out",
            "Push In → Orbit Left", "Push In → Orbit Right", "Push In → Arc Left", "Push In → Arc Right",
            "Push In → Slide Left", "Push In → Slide Right", "Push In → Tracking", "Push In → Follow",
            "Push In → Drift", "Dolly In → Push In", "Dolly In → Dolly Out", "Dolly In → Orbit",
            "Dolly In → Tracking", "Dolly Out → Push In", "Dolly Out → Orbit", "Orbit Left → Orbit Right",
            "Orbit Right → Orbit Left", "Orbit → Push In", "Orbit → Push Out", "Orbit → Orbit",
            "Arc Left → Arc Right", "Arc Right → Arc Left", "Arc → Push In", "Arc → Orbit",
            "Slide Left → Slide Right", "Slide Right → Slide Left", "Slide → Push In", "Slide → Orbit",
            "Tracking → Tracking", "Tracking → Push In", "Tracking → Orbit", "Tracking → Follow",
            "Follow → Tracking", "Follow → Push In", "Follow → Orbit", "Drift → Drift", "Drift → Push In",
            "Drift → Orbit", "Crane Up → Crane Down", "Crane Down → Crane Up", "Zoom In → Push In",
            "Zoom Out → Orbit", "Pull Back → Push In", "Reveal Orbit → Push In", "Walk Follow → Tracking",
            "Walk Follow → Push In", "Tracking → Walk Follow"
        ];
        foreach (string name in dynamicToDynamicNames)
        {
            string[] movements = name.Split(" → ", StringSplitOptions.TrimEntries);
            AddDynamicToDynamic(name, movements[0], movements[1]);
        }

        AddDynamicToStatic("Push In → Static Close", CameraPathKind.Push, "Push In", "Close");
        AddDynamicToStatic("Push In → Static Medium", CameraPathKind.Push, "Push In", "Medium");
        AddDynamicToStatic("Push In → Static Wide", CameraPathKind.Push, "Push In", "Wide");
        AddDynamicToStatic("Dolly In → Static Close", CameraPathKind.Push, "Dolly In", "Close");
        AddDynamicToStatic("Dolly Out → Static Wide", CameraPathKind.Pull, "Dolly Out", "Wide");
        AddDynamicToStatic("Arc Left → Static Close", CameraPathKind.ArcLeft, "Arc Left", "Close");
        AddDynamicToStatic("Arc Right → Static Close", CameraPathKind.ArcRight, "Arc Right", "Close");
        AddDynamicToStatic("Lateral Slide Left → Static", CameraPathKind.SlideLeft, "Lateral Slide Left");
        AddDynamicToStatic("Lateral Slide Right → Static", CameraPathKind.SlideRight, "Lateral Slide Right");
        AddDynamicToStatic("Orbit Left → Static", CameraPathKind.OrbitLeft, "Orbit Left");
        AddDynamicToStatic("Orbit Right → Static", CameraPathKind.OrbitRight, "Orbit Right");
        AddDynamicToStatic("Crane Down → Static", CameraPathKind.CraneDown, "Crane Down");
        AddDynamicToStatic("Crane Up → Static", CameraPathKind.CraneUp, "Crane Up");
        AddDynamicToStatic("Tilt Down → Static", CameraPathKind.TiltDown, "Tilt Down");
        AddDynamicToStatic("Tilt Up → Static", CameraPathKind.TiltUp, "Tilt Up");
        AddDynamicToStatic("Zoom In → Static", CameraPathKind.ZoomIn, "Zoom In");
        AddDynamicToStatic("Zoom Out → Static", CameraPathKind.ZoomOut, "Zoom Out");
        AddDynamicToStatic("Tracking → Static", CameraPathKind.Tracking, "Tracking");
        AddDynamicToStatic("Drift → Static", CameraPathKind.Drift, "Drift");
        AddDynamicToStatic("Follow → Static", CameraPathKind.Follow, "Follow");

        AddStaticToDynamic("Static Close → Push In", "Close", CameraPathKind.Push, "Push In");
        AddStaticToDynamic("Static Medium → Push In", "Medium", CameraPathKind.Push, "Push In");
        AddStaticToDynamic("Static Wide → Push In", "Wide", CameraPathKind.Push, "Push In");
        AddStaticToDynamic("Static Close → Dolly In", "Close", CameraPathKind.Push, "Dolly In");
        AddStaticToDynamic("Static Wide → Dolly Out", "Wide", CameraPathKind.Pull, "Dolly Out");
        AddStaticToDynamic("Static → Arc Left", null, CameraPathKind.ArcLeft, "Arc Left");
        AddStaticToDynamic("Static → Arc Right", null, CameraPathKind.ArcRight, "Arc Right");
        AddStaticToDynamic("Static → Slide Left", null, CameraPathKind.SlideLeft, "Slide Left");
        AddStaticToDynamic("Static → Slide Right", null, CameraPathKind.SlideRight, "Slide Right");
        AddStaticToDynamic("Static → Orbit Left", null, CameraPathKind.OrbitLeft, "Orbit Left");
        AddStaticToDynamic("Static → Orbit Right", null, CameraPathKind.OrbitRight, "Orbit Right");
        AddStaticToDynamic("Static → Crane Up", null, CameraPathKind.CraneUp, "Crane Up");
        AddStaticToDynamic("Static → Crane Down", null, CameraPathKind.CraneDown, "Crane Down");
        AddStaticToDynamic("Static → Tilt Up", null, CameraPathKind.TiltUp, "Tilt Up");
        AddStaticToDynamic("Static → Tilt Down", null, CameraPathKind.TiltDown, "Tilt Down");
        AddStaticToDynamic("Static → Zoom In", null, CameraPathKind.ZoomIn, "Zoom In");
        AddStaticToDynamic("Static → Zoom Out", null, CameraPathKind.ZoomOut, "Zoom Out");
        AddStaticToDynamic("Static → Tracking", null, CameraPathKind.Tracking, "Tracking");
        AddStaticToDynamic("Static → Drift", null, CameraPathKind.Drift, "Drift");
        AddStaticToDynamic("Static → Follow", null, CameraPathKind.Follow, "Follow");

        return presets;

        void AddDynamicToStatic(string name, CameraPathKind pathKind, string movementName, string framing = null)
        {
            MulticamCameraGroup dynamicGroup = BuildDynamicGroup("Cam1", pathKind, movementName, 0);
            MulticamCameraGroup staticGroup = BuildStaticGroup("Cam2", framing, CutTime);
            presets.Add(BuildPreset(name, MulticamPresetType.DynamicToStatic, dynamicGroup, staticGroup));
        }

        void AddStaticToDynamic(string name, string framing, CameraPathKind pathKind, string movementName)
        {
            MulticamCameraGroup staticGroup = BuildStaticGroup("Cam1", framing, 0);
            MulticamCameraGroup dynamicGroup = BuildDynamicGroup("Cam2", pathKind, movementName, CutTime);
            presets.Add(BuildPreset(name, MulticamPresetType.StaticToDynamic, staticGroup, dynamicGroup));
        }

        void AddStaticToStatic(string name, string firstShot, string secondShot)
        {
            MulticamCameraGroup firstGroup = BuildStaticGroup("Cam1", firstShot, 0);
            MulticamCameraGroup secondGroup = BuildStaticGroup("Cam2", secondShot, CutTime);
            presets.Add(BuildPreset(name, MulticamPresetType.StaticToStatic, firstGroup, secondGroup));
        }

        void AddDynamicToDynamic(string name, string firstMovement, string secondMovement)
        {
            MulticamCameraGroup firstGroup = BuildDynamicGroup("Cam1", MapDynamicPath(firstMovement), firstMovement, 0);
            MulticamCameraGroup secondGroup = BuildDynamicGroup("Cam2", MapDynamicPath(secondMovement), secondMovement, CutTime);
            presets.Add(BuildPreset(name, MulticamPresetType.DynamicToDynamic, firstGroup, secondGroup));
        }
    }

    private static MulticamCameraPreset BuildPreset(string name, MulticamPresetType type,
        MulticamCameraGroup firstGroup, MulticamCameraGroup secondGroup) =>
        new(name, type, TemplateDuration,
            [new MulticamDirectorKey(0, "Cam1"), new MulticamDirectorKey(CutTime, "Cam2")],
            [firstGroup, secondGroup],
            $"Two-camera {type switch
            {
                MulticamPresetType.StaticToStatic => "static-to-static",
                MulticamPresetType.StaticToDynamic => "static-to-dynamic",
                MulticamPresetType.DynamicToStatic => "dynamic-to-static",
                MulticamPresetType.DynamicToDynamic => "dynamic-to-dynamic",
                _ => "multicam"
            }} sequence.",
            [firstGroup.MovementName, secondGroup.MovementName, "Cam1", "Cam2"], true);

    private static MulticamCameraGroup BuildStaticGroup(string groupName, string framing, float startTime)
    {
        string presetName = framing switch
        {
            "Wide" or "Speaker Wide" => "Medium Wide Shot",
            "Listener Wide" => "Centered Wide Shot",
            "Close" or "Speaker Close" => "Left Three-Quarter Close-Up",
            "Listener Close" => "Right Three-Quarter Close-Up",
            "Medium" or "Speaker" or "Speaker Medium" => "Left Medium Shot",
            "Listener" or "Listener Medium" => "Right Medium Shot",
            "Two Shot" => "Two-Shot",
            "OTS" => "Over-the-Shoulder Left",
            "Reverse OTS" => "Over-the-Shoulder Right",
            "Reaction" => "Reaction Shot",
            "Establishing" => "Centered Wide Shot",
            "Profile" => groupName == "Cam1" ? "Profile Left" : "Profile Right",
            "Front" => "Front Medium Shot",
            "High Angle" => "High-Angle Shot",
            "Low Angle" => "Low-Angle Shot",
            _ => "Front Medium Shot"
        };
        CameraPreset preset = CameraPresetCatalog.All.First(item => item.Name == presetName);
        return new MulticamCameraGroup(groupName, groupName, true, preset.Name,
            ToLocalKeys(preset, startTime, 0),
            [new MulticamFovKey(startTime, 60, 0, 0, CameraKeyInterpolation.Constant)]);
    }

    private static MulticamCameraGroup BuildDynamicGroup(string groupName, CameraPathKind pathKind,
        string movementName, float startTime)
    {
        var preset = new CameraPreset(movementName, CameraPresetCategory.DynamicShots,
            300, 0, 90, 70, Duration: CutTime, KeyCount: 6, PathKind: pathKind, MovementAmount: 140,
            Interpolation: CameraKeyInterpolation.SmoothClamped);
        IReadOnlyList<CameraPresetLocalKey> localKeys = ToLocalKeys(preset, startTime, CutTime);
        if (pathKind is CameraPathKind.TiltDown or CameraPathKind.TiltUp)
        {
            float direction = pathKind == CameraPathKind.TiltUp ? 1 : -1;
            localKeys = localKeys.Select((key, index) => key with
            {
                LocalRotation = key.LocalRotation with
                {
                    Y = key.LocalRotation.Y + direction * 25f * index / Math.Max(1, localKeys.Count - 1)
                }
            }).ToArray();
        }

        IReadOnlyList<MulticamFovKey> fovKeys = pathKind switch
        {
            CameraPathKind.ZoomIn =>
            [new(startTime, 65, 0, 0, CameraKeyInterpolation.SmoothClamped),
                new(startTime + CutTime, 38, 0, 0, CameraKeyInterpolation.SmoothClamped)],
            CameraPathKind.ZoomOut =>
            [new(startTime, 38, 0, 0, CameraKeyInterpolation.SmoothClamped),
                new(startTime + CutTime, 65, 0, 0, CameraKeyInterpolation.SmoothClamped)],
            _ => [new MulticamFovKey(startTime, 60, 0, 0, CameraKeyInterpolation.Constant)]
        };
        return new MulticamCameraGroup(groupName, groupName, false, movementName,
            localKeys, fovKeys);
    }

    private static CameraPathKind MapDynamicPath(string movementName) => movementName switch
    {
        "Push In" or "Dolly In" => CameraPathKind.Push,
        "Push Out" or "Dolly Out" or "Pull Back" => CameraPathKind.Pull,
        "Orbit Left" => CameraPathKind.OrbitLeft,
        "Orbit Right" => CameraPathKind.OrbitRight,
        "Orbit" => CameraPathKind.OrbitPush,
        "Arc Left" => CameraPathKind.ArcLeft,
        "Arc Right" => CameraPathKind.ArcRight,
        "Arc" or "Reveal Orbit" => CameraPathKind.RevealLeft,
        "Slide Left" => CameraPathKind.SlideLeft,
        "Slide Right" => CameraPathKind.SlideRight,
        "Slide" => CameraPathKind.SlidePush,
        "Tracking" => CameraPathKind.Tracking,
        "Follow" or "Walk Follow" => CameraPathKind.Follow,
        "Drift" => CameraPathKind.Drift,
        "Crane Up" => CameraPathKind.CraneUp,
        "Crane Down" => CameraPathKind.CraneDown,
        "Zoom In" => CameraPathKind.ZoomIn,
        "Zoom Out" => CameraPathKind.ZoomOut,
        _ => CameraPathKind.Tracking
    };

    private static IReadOnlyList<CameraPresetLocalKey> ToLocalKeys(CameraPreset preset, float startTime, float duration)
    {
        IReadOnlyList<GeneratedCameraKey> generated = CameraPresetGenerator.Generate(preset,
            new CameraOrigin(Vector3.Zero, Vector3.Zero));
        float sourceDuration = generated.Count > 0 ? generated[^1].TimeOffset : 0;
        return generated.Select(key => new CameraPresetLocalKey(
            startTime + (sourceDuration <= float.Epsilon ? 0 : key.TimeOffset / sourceDuration * duration),
            key.Location, key.Rotation, key.Interpolation)).ToArray();
    }
}

public static class CameraPresetGenerator
{
    private const int PathMeasurementSegments = 256;

    public static int GetKeyCount(CameraPreset preset)
    {
        if (preset.IsSavedTrackMove)
        {
            return preset.LocalKeys.Count;
        }

        if (preset.Category != CameraPresetCategory.DynamicShots || preset.Duration <= 0)
        {
            return 1;
        }

        float intervalsPerSecond = RequiresDetailedSampling(preset.PathKind) ? 2f : 1f;
        return Math.Max(2, (int)MathF.Ceiling(preset.Duration * intervalsPerSecond) + 1);
    }

    public static float GetPathLength(CameraPreset preset, CameraOrigin origin, float distanceScale = 1f)
    {
        distanceScale = Math.Max(0f, distanceScale);
        if (preset.IsSavedTrackMove)
        {
            IReadOnlyList<GeneratedCameraKey> keys = GenerateSavedTrackMove(preset, origin, 1f, distanceScale);
            float length = 0;
            for (int i = 1; i < keys.Count; i++)
            {
                length += Vector3.Distance(keys[i - 1].Location, keys[i].Location);
            }
            return length;
        }

        return BuildMeasuredPath(preset, origin, distanceScale).TotalLength;
    }

    public static IReadOnlyList<GeneratedCameraKey> Generate(CameraPreset preset, CameraOrigin origin, int? sampleCount = null,
        float pathFraction = 1f, float distanceScale = 1f)
    {
        int count = sampleCount is > 0 ? sampleCount.Value : GetKeyCount(preset);
        pathFraction = Math.Clamp(pathFraction, 0f, 1f);
        distanceScale = Math.Max(0f, distanceScale);
        if (preset.IsSavedTrackMove)
        {
            return GenerateSavedTrackMove(preset, origin, pathFraction, distanceScale);
        }

        BuildBasis(origin.Rotation, out Vector3 forward, out Vector3 right, out Vector3 up);
        MeasuredPath measuredPath = BuildMeasuredPath(preset, origin, distanceScale);
        var keys = new List<GeneratedCameraKey>(count);
        for (int i = 0; i < count; i++)
        {
            float elapsedFraction = count == 1 ? 0 : i / (float)(count - 1);
            float normalizedDistance = elapsedFraction * pathFraction;
            float t = measuredPath.GetPathProgress(normalizedDistance);
            GetLocalPosition(preset, t, distanceScale, out float distance, out float side, out float height);
            Vector3 location = origin.Location + forward * distance + right * side + up * height;
            Vector3 target = origin.Location + up * preset.LookAtHeight;
            Vector3 rotation = LookAtRotation(location, target, origin.Rotation.X + preset.LocalRoll);
            rotation.Y += preset.LocalPitch;
            rotation.Z += preset.LocalYaw;
            keys.Add(new GeneratedCameraKey(preset.Duration * elapsedFraction, location, rotation, GetInterpolation(preset, t)));
        }

        return keys;
    }

    private static IReadOnlyList<GeneratedCameraKey> GenerateSavedTrackMove(CameraPreset preset, CameraOrigin origin,
        float pathFraction, float distanceScale)
    {
        IReadOnlyList<CameraPresetLocalKey> sourceKeys = preset.LocalKeys;
        float sourceDuration = sourceKeys[^1].TimeOffset;
        float cutoff = sourceDuration * pathFraction;
        var localKeys = sourceKeys.Where(key => key.TimeOffset <= cutoff).ToList();
        if (localKeys.Count == 0)
        {
            localKeys.Add(sourceKeys[0]);
        }
        if (pathFraction < 1f && cutoff > localKeys[^1].TimeOffset)
        {
            int upperIndex = sourceKeys.ToList().FindIndex(key => key.TimeOffset > cutoff);
            if (upperIndex > 0)
            {
                CameraPresetLocalKey lower = sourceKeys[upperIndex - 1];
                CameraPresetLocalKey upper = sourceKeys[upperIndex];
                float fraction = (cutoff - lower.TimeOffset) / Math.Max(upper.TimeOffset - lower.TimeOffset, float.Epsilon);
                localKeys.Add(new CameraPresetLocalKey(cutoff,
                    Vector3.Lerp(lower.LocalPosition, upper.LocalPosition, fraction),
                    LerpRotation(lower.LocalRotation, upper.LocalRotation, fraction), lower.Interpolation));
            }
        }

        BuildBasis(origin.Rotation, out Vector3 forward, out Vector3 right, out Vector3 up);
        float outputSourceDuration = Math.Max(cutoff, float.Epsilon);
        return localKeys.Select(key =>
        {
            Vector3 localPosition = key.LocalPosition with { X = key.LocalPosition.X * distanceScale };
            Vector3 location = origin.Location + forward * localPosition.X + right * localPosition.Y + up * localPosition.Z;
            Vector3 rotation = LocalRotationToWorld(key.LocalRotation, origin.Rotation);
            float time = sourceDuration <= float.Epsilon ? 0 : key.TimeOffset / outputSourceDuration * preset.Duration;
            return new GeneratedCameraKey(time, location, rotation, key.Interpolation);
        }).ToArray();
    }

    private static MeasuredPath BuildMeasuredPath(CameraPreset preset, CameraOrigin origin, float distanceScale)
    {
        BuildBasis(origin.Rotation, out Vector3 forward, out Vector3 right, out Vector3 up);
        var progress = new float[PathMeasurementSegments + 1];
        var cumulativeDistance = new float[PathMeasurementSegments + 1];
        Vector3 previous = GetWorldPosition(preset, origin.Location, forward, right, up, 0, distanceScale);
        for (int i = 1; i <= PathMeasurementSegments; i++)
        {
            float t = i / (float)PathMeasurementSegments;
            Vector3 current = GetWorldPosition(preset, origin.Location, forward, right, up, t, distanceScale);
            progress[i] = t;
            cumulativeDistance[i] = cumulativeDistance[i - 1] + Vector3.Distance(previous, current);
            previous = current;
        }

        return new MeasuredPath(progress, cumulativeDistance);
    }

    private static Vector3 GetWorldPosition(CameraPreset preset, Vector3 origin, Vector3 forward, Vector3 right, Vector3 up,
        float t, float distanceScale)
    {
        GetLocalPosition(preset, t, distanceScale, out float distance, out float side, out float height);
        return origin + forward * distance + right * side + up * height;
    }

    private sealed class MeasuredPath(float[] progress, float[] cumulativeDistance)
    {
        internal float TotalLength => cumulativeDistance[^1];

        internal float GetPathProgress(float normalizedDistance)
        {
            if (TotalLength <= float.Epsilon || normalizedDistance <= 0)
            {
                return 0;
            }

            if (normalizedDistance >= 1)
            {
                return 1;
            }

            float targetDistance = normalizedDistance * TotalLength;
            int upperIndex = Array.BinarySearch(cumulativeDistance, targetDistance);
            if (upperIndex >= 0)
            {
                return progress[upperIndex];
            }

            upperIndex = ~upperIndex;
            int lowerIndex = upperIndex - 1;
            float segmentLength = cumulativeDistance[upperIndex] - cumulativeDistance[lowerIndex];
            float segmentProgress = segmentLength > float.Epsilon
                ? (targetDistance - cumulativeDistance[lowerIndex]) / segmentLength
                : 0;
            return progress[lowerIndex] + (progress[upperIndex] - progress[lowerIndex]) * segmentProgress;
        }
    }

    private static bool RequiresDetailedSampling(CameraPathKind pathKind) => pathKind is
        CameraPathKind.ArcLeft or CameraPathKind.ArcRight or
        CameraPathKind.OrbitLeft or CameraPathKind.OrbitRight or
        CameraPathKind.RiseAndPush or CameraPathKind.LowerAndPush or
        CameraPathKind.SlidePush or CameraPathKind.SlidePull or
        CameraPathKind.RevealLeft or CameraPathKind.RevealRight or
        CameraPathKind.OrbitPush or CameraPathKind.OrbitPull or
        CameraPathKind.PushCrane or CameraPathKind.PullCrane or
        CameraPathKind.MoveThenHold or CameraPathKind.HoldThenMove or
        CameraPathKind.Reframe;

    private static CameraKeyInterpolation GetInterpolation(CameraPreset preset, float normalizedTime)
    {
        if (preset.Category != CameraPresetCategory.DynamicShots)
        {
            return CameraKeyInterpolation.Constant;
        }

        return preset.PathKind switch
        {
            CameraPathKind.MoveThenHold when normalizedTime >= 0.65f => CameraKeyInterpolation.Constant,
            CameraPathKind.HoldThenMove when normalizedTime < 0.35f => CameraKeyInterpolation.Constant,
            _ => preset.Interpolation
        };
    }

    private static void GetLocalPosition(CameraPreset preset, float t, float distanceScale, out float distance, out float side,
        out float height)
    {
        float baseDistance = preset.ForwardDistance * distanceScale;
        distance = baseDistance;
        side = preset.SideOffset;
        height = preset.HeightOffset;
        float amount = preset.MovementAmount;
        float smooth = t * t * (3 - 2 * t);

        switch (preset.PathKind)
        {
            case CameraPathKind.Push: distance -= amount * smooth; break;
            case CameraPathKind.Pull: distance += amount * smooth; break;
            case CameraPathKind.DollyLeft:
            case CameraPathKind.SlideLeft: side -= amount * smooth; break;
            case CameraPathKind.DollyRight:
            case CameraPathKind.SlideRight: side += amount * smooth; break;
            case CameraPathKind.ArcLeft: ApplyArc(-amount, baseDistance, smooth, ref distance, ref side); break;
            case CameraPathKind.ArcRight: ApplyArc(amount, baseDistance, smooth, ref distance, ref side); break;
            case CameraPathKind.OrbitLeft: ApplyArc(-amount, baseDistance, smooth, ref distance, ref side); break;
            case CameraPathKind.OrbitRight: ApplyArc(amount, baseDistance, smooth, ref distance, ref side); break;
            case CameraPathKind.CraneUp: height += amount * smooth; break;
            case CameraPathKind.CraneDown: height -= amount * smooth; break;
            case CameraPathKind.RiseAndPush: distance -= amount * smooth; height += amount * 0.65f * smooth; break;
            case CameraPathKind.LowerAndPush: distance -= amount * smooth; height -= amount * 0.65f * smooth; break;
            case CameraPathKind.SlidePush: side += amount * smooth; distance -= amount * 0.6f * smooth; break;
            case CameraPathKind.SlidePull: side += amount * smooth; distance += amount * 0.6f * smooth; break;
            case CameraPathKind.Follow: distance += amount * smooth; break;
            case CameraPathKind.Lead: distance -= amount * smooth; break;
            case CameraPathKind.SideFollow: side += amount * smooth; break;
            case CameraPathKind.RevealLeft: side += amount * smooth; break;
            case CameraPathKind.RevealRight: side -= amount * smooth; break;
            case CameraPathKind.OrbitPush: ApplyArc(-70, baseDistance - amount * 0.7f * smooth, smooth, ref distance, ref side); break;
            case CameraPathKind.OrbitPull: ApplyArc(70, baseDistance + amount * 0.7f * smooth, smooth, ref distance, ref side); break;
            case CameraPathKind.PushCrane: distance -= amount * smooth; height += amount * 0.55f * smooth; break;
            case CameraPathKind.PullCrane: distance += amount * smooth; height -= amount * 0.55f * smooth; break;
            case CameraPathKind.MoveThenHold:
                distance -= amount * SmoothSegment(t, 0, 0.65f);
                side += amount * 0.35f * SmoothSegment(t, 0, 0.65f);
                break;
            case CameraPathKind.HoldThenMove: distance -= amount * SmoothSegment(t, 0.35f, 1); break;
            case CameraPathKind.Reframe: side += amount * smooth; distance -= amount * 0.25f * smooth; height += amount * 0.12f * smooth; break;
            case CameraPathKind.Tracking: side += amount * smooth; break;
            case CameraPathKind.Drift: side += amount * 0.4f * smooth; height += amount * 0.1f * smooth; break;
        }
    }

    private static void ApplyArc(float degrees, float radius, float t, ref float distance, ref float side)
    {
        float angle = DegreesToRadians(degrees * t);
        distance = MathF.Cos(angle) * radius;
        side += MathF.Sin(angle) * radius;
    }

    private static float SmoothSegment(float value, float start, float end)
    {
        float normalized = Math.Clamp((value - start) / Math.Max(end - start, float.Epsilon), 0, 1);
        return normalized * normalized * (3 - 2 * normalized);
    }

    private static Vector3 LookAtRotation(Vector3 location, Vector3 target, float roll)
    {
        Vector3 direction = Vector3.Normalize(target - location);
        float pitch = MathF.Atan2(direction.Z, MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y));
        float yaw = MathF.Atan2(direction.Y, direction.X);
        return new Vector3(roll, RadiansToDegrees(pitch), RadiansToDegrees(yaw));
    }

    internal static void BuildBasis(Vector3 rotation, out Vector3 forward, out Vector3 right, out Vector3 up)
    {
        float roll = DegreesToRadians(rotation.X);
        float pitch = DegreesToRadians(rotation.Y);
        float yaw = DegreesToRadians(rotation.Z);
        float sr = MathF.Sin(roll);
        float sp = MathF.Sin(pitch);
        float sy = MathF.Sin(yaw);
        float cr = MathF.Cos(roll);
        float cp = MathF.Cos(pitch);
        float cy = MathF.Cos(yaw);

        forward = Vector3.Normalize(new Vector3(cp * cy, cp * sy, sp));
        right = Vector3.Normalize(new Vector3(sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp));
        up = Vector3.Normalize(new Vector3(-(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp));
    }

    internal static Vector3 WorldRotationToLocal(Vector3 worldRotation, Vector3 originRotation)
    {
        BuildBasis(originRotation, out Vector3 originForward, out Vector3 originRight, out Vector3 originUp);
        BuildBasis(worldRotation, out Vector3 worldForward, out Vector3 worldRight, out Vector3 worldUp);
        return BasisToRotation(
            new Vector3(Vector3.Dot(worldForward, originForward), Vector3.Dot(worldForward, originRight), Vector3.Dot(worldForward, originUp)),
            new Vector3(Vector3.Dot(worldRight, originForward), Vector3.Dot(worldRight, originRight), Vector3.Dot(worldRight, originUp)),
            new Vector3(Vector3.Dot(worldUp, originForward), Vector3.Dot(worldUp, originRight), Vector3.Dot(worldUp, originUp)));
    }

    internal static Vector3 LocalRotationToWorld(Vector3 localRotation, Vector3 originRotation)
    {
        BuildBasis(originRotation, out Vector3 originForward, out Vector3 originRight, out Vector3 originUp);
        BuildBasis(localRotation, out Vector3 localForward, out Vector3 localRight, out Vector3 localUp);
        Vector3 Transform(Vector3 local) =>
            originForward * local.X + originRight * local.Y + originUp * local.Z;
        return BasisToRotation(Transform(localForward), Transform(localRight), Transform(localUp));
    }

    private static Vector3 BasisToRotation(Vector3 forward, Vector3 right, Vector3 up)
    {
        float pitch = MathF.Atan2(forward.Z, MathF.Sqrt(forward.X * forward.X + forward.Y * forward.Y));
        float yaw = MathF.Atan2(forward.Y, forward.X);
        float roll = MathF.Atan2(-right.Z, up.Z);
        return new Vector3(RadiansToDegrees(roll), RadiansToDegrees(pitch), RadiansToDegrees(yaw));
    }

    private static Vector3 LerpRotation(Vector3 start, Vector3 end, float fraction) => new(
        start.X + ShortestAngleDelta(start.X, end.X) * fraction,
        start.Y + ShortestAngleDelta(start.Y, end.Y) * fraction,
        start.Z + ShortestAngleDelta(start.Z, end.Z) * fraction);

    private static float ShortestAngleDelta(float start, float end)
    {
        float delta = (end - start) % 360f;
        if (delta > 180f) delta -= 360f;
        if (delta < -180f) delta += 360f;
        return delta;
    }

    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);
    private static float RadiansToDegrees(float radians) => radians * (180f / MathF.PI);
}

public static class CameraPresetTrackCapture
{
    public static bool TryCapture(ExportEntry trackMove, CameraOrigin origin, string name,
        out CameraPreset preset, out string error)
    {
        preset = null;
        error = null;
        if (trackMove?.ClassName != "InterpTrackMove")
        {
            error = "The selected export is not an InterpTrackMove.";
            return false;
        }

        var lookupPoints = trackMove.GetProperty<StructProperty>("LookupTrack")?
            .GetProp<ArrayProperty<StructProperty>>("Points");
        var positionPoints = trackMove.GetProperty<StructProperty>("PosTrack")?
            .GetProp<ArrayProperty<StructProperty>>("Points");
        var rotationPoints = trackMove.GetProperty<StructProperty>("EulerTrack")?
            .GetProp<ArrayProperty<StructProperty>>("Points");
        if (lookupPoints is not { Count: > 0 } || positionPoints is null || rotationPoints is null
            || lookupPoints.Count != positionPoints.Count || lookupPoints.Count != rotationPoints.Count)
        {
            error = "The TrackMove must contain synchronized lookup, position, and rotation keys.";
            return false;
        }

        CameraPresetGenerator.BuildBasis(origin.Rotation, out Vector3 forward, out Vector3 right, out Vector3 up);
        float firstTime = lookupPoints[0].GetProp<FloatProperty>("Time")?.Value ?? 0;
        var localKeys = new List<CameraPresetLocalKey>(lookupPoints.Count);
        for (int i = 0; i < lookupPoints.Count; i++)
        {
            InterpCurvePoint<Vector3> positionPoint = InterpCurvePoint<Vector3>.FromStructProperty(positionPoints[i]);
            InterpCurvePoint<Vector3> rotationPoint = InterpCurvePoint<Vector3>.FromStructProperty(rotationPoints[i]);
            Vector3 worldPosition = positionPoint.OutVal;
            Vector3 worldRotation = rotationPoint.OutVal;
            Vector3 delta = worldPosition - origin.Location;
            Vector3 localPosition = new(Vector3.Dot(delta, forward), Vector3.Dot(delta, right), Vector3.Dot(delta, up));
            Vector3 localRotation = CameraPresetGenerator.WorldRotationToLocal(worldRotation, origin.Rotation);
            float time = (lookupPoints[i].GetProp<FloatProperty>("Time")?.Value ?? firstTime) - firstTime;
            localKeys.Add(new CameraPresetLocalKey(time, localPosition, localRotation, MapInterpolation(positionPoint.InterpMode),
                ToLocalVector(positionPoint.ArriveTangent, forward, right, up),
                ToLocalVector(positionPoint.LeaveTangent, forward, right, up),
                rotationPoint.ArriveTangent, rotationPoint.LeaveTangent));
        }

        float duration = Math.Max(0, localKeys[^1].TimeOffset);
        CameraPresetLocalKey first = localKeys[0];
        preset = new CameraPreset(name.Trim(), CameraPresetCategory.SavedTrackMoves,
            first.LocalPosition.X, first.LocalPosition.Y, first.LocalPosition.Z, 0,
            Duration: duration, KeyCount: localKeys.Count,
            Interpolation: first.Interpolation, LocalKeys: localKeys);
        return true;
    }

    private static CameraKeyInterpolation MapInterpolation(EInterpCurveMode mode) => mode switch
    {
        EInterpCurveMode.CIM_Constant => CameraKeyInterpolation.Constant,
        EInterpCurveMode.CIM_Linear => CameraKeyInterpolation.Linear,
        EInterpCurveMode.CIM_CurveAutoClamped => CameraKeyInterpolation.SmoothClamped,
        _ => CameraKeyInterpolation.Smooth
    };

    internal static Vector3 ToLocalVector(Vector3 value, Vector3 forward, Vector3 right, Vector3 up) =>
        new(Vector3.Dot(value, forward), Vector3.Dot(value, right), Vector3.Dot(value, up));
}
