using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LegendaryExplorer.Tools.InterpEditor;

public enum CameraPresetCategory
{
    StaticShots,
    DynamicShots,
    ReactionShots
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
    Reframe
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
    CameraKeyInterpolation Interpolation = CameraKeyInterpolation.Constant);

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

public static class CameraPresetGenerator
{
    private const int PathMeasurementSegments = 256;

    public static int GetKeyCount(CameraPreset preset)
    {
        if (preset.Category != CameraPresetCategory.DynamicShots || preset.Duration <= 0)
        {
            return 1;
        }

        float intervalsPerSecond = RequiresDetailedSampling(preset.PathKind) ? 2f : 1f;
        return Math.Max(2, (int)MathF.Ceiling(preset.Duration * intervalsPerSecond) + 1);
    }

    public static float GetPathLength(CameraPreset preset, CameraOrigin origin, float distanceScale = 1f) =>
        BuildMeasuredPath(preset, origin, Math.Max(0f, distanceScale)).TotalLength;

    public static IReadOnlyList<GeneratedCameraKey> Generate(CameraPreset preset, CameraOrigin origin, int? sampleCount = null,
        float pathFraction = 1f, float distanceScale = 1f)
    {
        int count = sampleCount is > 0 ? sampleCount.Value : GetKeyCount(preset);
        pathFraction = Math.Clamp(pathFraction, 0f, 1f);
        distanceScale = Math.Max(0f, distanceScale);

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

    private static void BuildBasis(Vector3 rotation, out Vector3 forward, out Vector3 right, out Vector3 up)
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

    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);
    private static float RadiansToDegrees(float radians) => radians * (180f / MathF.PI);
}
