using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using InterpCurveVector = LegendaryExplorerCore.Unreal.BinaryConverters.InterpCurve<System.Numerics.Vector3>;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public sealed class CurveEditor3DModel
{
    private const float KeyTimeTolerance = 0.0001f;
    private readonly Dictionary<InterpCurvePoint<Vector3>, StructProperty> lookupPointsByPositionPoint = [];
    private StructProperty lookupTrack;
    private ArrayProperty<StructProperty> lookupPoints;

    public ExportEntry Export { get; private set; }

    public InterpCurveVector PositionTrack { get; private set; }

    public InterpCurveVector RotationTrack { get; private set; }

    public List<CurveEditor3DKeyframe> Keyframes { get; } = [];

    public event Action Changed;

    public void Load(ExportEntry export)
    {
        Export = export;
        PropertyCollection properties = export.GetProperties();
        var emptyCurve = new StructProperty("InterpCurveVector", false);
        PositionTrack = InterpCurveVector.FromStructProperty(properties.GetProp<StructProperty>("PosTrack") ?? emptyCurve, export.Game);
        RotationTrack = InterpCurveVector.FromStructProperty(properties.GetProp<StructProperty>("EulerTrack") ?? emptyCurve, export.Game);
        lookupTrack = properties.GetProp<StructProperty>("LookupTrack") ?? new StructProperty("InterpLookupTrack", new PropertyCollection
        {
            new ArrayProperty<StructProperty>("Points")
        }, "LookupTrack");
        lookupPoints = lookupTrack.GetProp<ArrayProperty<StructProperty>>("Points");
        RebuildKeyframes();
    }

    public void Clear()
    {
        Export = null;
        PositionTrack = null;
        RotationTrack = null;
        lookupTrack = null;
        lookupPoints = null;
        lookupPointsByPositionPoint.Clear();
        Keyframes.Clear();
    }

    public IReadOnlyList<Vector3> SampleTrajectory(int samplesPerSegment = 16)
    {
        if (Keyframes.Count == 0)
        {
            return [];
        }

        if (Keyframes.Count == 1)
        {
            return [Keyframes[0].Location];
        }

        var samples = new List<Vector3>((Keyframes.Count - 1) * samplesPerSegment + 1);
        for (int keyIndex = 0; keyIndex < Keyframes.Count - 1; keyIndex++)
        {
            float start = Keyframes[keyIndex].Time;
            float end = Keyframes[keyIndex + 1].Time;
            for (int sampleIndex = 0; sampleIndex < samplesPerSegment; sampleIndex++)
            {
                float time = start + ((end - start) * sampleIndex / samplesPerSegment);
                samples.Add(PositionTrack.Eval(time, Vector3.Zero));
            }
        }

        samples.Add(PositionTrack.Eval(Keyframes[^1].Time, Vector3.Zero));
        return samples;
    }

    public bool HasKeyframeAtTime(float time, CurveEditor3DKeyframe excludedKeyframe = null)
        => Keyframes.Any(keyframe => keyframe != excludedKeyframe && MathF.Abs(keyframe.Time - time) <= KeyTimeTolerance);

    public CurveEditor3DKeyframe AddKeyframe(CurveEditor3DKeyframe selectedKeyframe, float time)
    {
        if (Export is null || selectedKeyframe is null || !float.IsFinite(time) || HasKeyframeAtTime(time))
        {
            return null;
        }

        Vector3 newLocation = selectedKeyframe.Location + new Vector3(100f, 100f, 100f);
        Vector3 newRotation = selectedKeyframe.Rotation;
        InterpCurvePoint<Vector3> positionPoint = AddPoint(PositionTrack, time, newLocation, selectedKeyframe.PosTrackInterpMode);
        InterpCurvePoint<Vector3> rotationPoint = AddPoint(RotationTrack, time, newRotation, selectedKeyframe.EulerTrackInterpMode);
        AddLookupPoint(positionPoint, time);
        var keyframe = new CurveEditor3DKeyframe(positionPoint, rotationPoint, newRotation, CommitKeyframe);
        Keyframes.Add(keyframe);
        CommitKeyframe(keyframe, null);
        return keyframe;
    }

    public CurveEditor3DKeyframe AddKeyframeAfterLast(Vector3 location, float time)
    {
        if (Export is null || Keyframes.Count == 0 || !float.IsFinite(time) || HasKeyframeAtTime(time))
        {
            return null;
        }

        CurveEditor3DKeyframe lastKeyframe = Keyframes[^1];
        Vector3 newRotation = lastKeyframe.Rotation;
        InterpCurvePoint<Vector3> positionPoint = AddPoint(PositionTrack, time, location, lastKeyframe.PosTrackInterpMode);
        InterpCurvePoint<Vector3> rotationPoint = AddPoint(RotationTrack, time, newRotation, lastKeyframe.EulerTrackInterpMode);
        AddLookupPoint(positionPoint, time);
        var keyframe = new CurveEditor3DKeyframe(positionPoint, rotationPoint, newRotation, CommitKeyframe);
        Keyframes.Add(keyframe);
        CommitKeyframe(keyframe, null);
        return keyframe;
    }

    public CurveEditor3DKeyframe DeleteKeyframe(CurveEditor3DKeyframe keyframe)
    {
        if (Export is null || keyframe is null)
        {
            return null;
        }

        int index = Keyframes.IndexOf(keyframe);
        if (index < 0)
        {
            return null;
        }

        PositionTrack.Points.Remove(keyframe.PositionPoint);
        if (keyframe.RotationPoint is not null)
        {
            RotationTrack.Points.Remove(keyframe.RotationPoint);
        }
        if (lookupPointsByPositionPoint.Remove(keyframe.PositionPoint, out StructProperty lookupPoint))
        {
            lookupPoints.Remove(lookupPoint);
        }
        Keyframes.RemoveAt(index);
        PositionTrack.ReCalculateTangents();
        RotationTrack.ReCalculateTangents();
        WriteTracks();
        Changed?.Invoke();

        if (Keyframes.Count == 0)
        {
            return null;
        }

        return Keyframes[Math.Min(index, Keyframes.Count - 1)];
    }

    public void SetAllPosTrackInterpModes(EInterpCurveMode interpMode)
    {
        if (Export is null)
        {
            return;
        }

        foreach (CurveEditor3DKeyframe keyframe in Keyframes)
        {
            keyframe.SetPosTrackInterpMode(interpMode, commit: false);
        }

        foreach (InterpCurvePoint<Vector3> point in PositionTrack.Points)
        {
            point.InterpMode = interpMode;
        }

        PositionTrack.ReCalculateTangents();
        WriteTracks();
        Changed?.Invoke();
    }

    public void SetAllEulerTrackInterpModes(EInterpCurveMode interpMode)
    {
        if (Export is null)
        {
            return;
        }

        foreach (CurveEditor3DKeyframe keyframe in Keyframes)
        {
            keyframe.SetEulerTrackInterpMode(interpMode, commit: false);
        }

        foreach (InterpCurvePoint<Vector3> point in RotationTrack.Points)
        {
            point.InterpMode = interpMode;
        }

        RotationTrack.ReCalculateTangents();
        WriteTracks();
        Changed?.Invoke();
    }

    public void TranslateAllKeyframes(Vector3 delta)
    {
        if (Export is null || delta == Vector3.Zero)
        {
            return;
        }

        foreach (CurveEditor3DKeyframe keyframe in Keyframes)
        {
            Vector3 location = keyframe.Location + delta;
            keyframe.SetLocation(location, commit: false);
            keyframe.PositionPoint.OutVal = location;
        }

        PositionTrack.ReCalculateTangents();
        WriteTracks();
        Changed?.Invoke();
    }

    public void RotateAllKeyframes(Vector3 delta)
    {
        if (Export is null || delta == Vector3.Zero)
        {
            return;
        }

        foreach (CurveEditor3DKeyframe keyframe in Keyframes)
        {
            Vector3 rotation = keyframe.Rotation + delta;
            keyframe.SetRotation(rotation, commit: false);
            InterpCurvePoint<Vector3> rotationPoint = keyframe.RotationPoint ??= AddPoint(
                RotationTrack,
                keyframe.Time,
                rotation,
                keyframe.EulerTrackInterpMode);
            rotationPoint.OutVal = rotation;
        }

        RotationTrack.ReCalculateTangents();
        WriteTracks();
        Changed?.Invoke();
    }

    private void RebuildKeyframes()
    {
        Keyframes.Clear();
        lookupPointsByPositionPoint.Clear();
        for (int index = 0; index < Math.Min(PositionTrack.Points.Count, lookupPoints.Count); index++)
        {
            lookupPointsByPositionPoint[PositionTrack.Points[index]] = lookupPoints[index];
        }

        foreach (InterpCurvePoint<Vector3> positionPoint in PositionTrack.Points.OrderBy(point => point.InVal))
        {
            float time = positionPoint.InVal;
            InterpCurvePoint<Vector3> rotationPoint = FindPoint(RotationTrack, time);
            Keyframes.Add(new CurveEditor3DKeyframe(
                positionPoint,
                rotationPoint,
                RotationTrack.Eval(time, Vector3.Zero),
                CommitKeyframe));
        }
    }

    private void CommitKeyframe(CurveEditor3DKeyframe keyframe, float? previousTime)
    {
        if (Export is null)
        {
            return;
        }

        InterpCurvePoint<Vector3> positionPoint = keyframe.PositionPoint;
        InterpCurvePoint<Vector3> rotationPoint = keyframe.RotationPoint ??= AddPoint(
            RotationTrack,
            previousTime ?? keyframe.Time,
            keyframe.Rotation,
            keyframe.EulerTrackInterpMode);

        positionPoint.InVal = keyframe.Time;
        positionPoint.OutVal = keyframe.Location;
        positionPoint.InterpMode = keyframe.PosTrackInterpMode;
        if (!lookupPointsByPositionPoint.TryGetValue(positionPoint, out StructProperty lookupPoint))
        {
            lookupPoint = AddLookupPoint(positionPoint, keyframe.Time);
        }
        lookupPoint.GetProp<FloatProperty>("Time").Value = keyframe.Time;
        rotationPoint.InVal = keyframe.Time;
        rotationPoint.OutVal = keyframe.Rotation;
        rotationPoint.InterpMode = keyframe.EulerTrackInterpMode;
        PositionTrack.Points.Sort((left, right) => left.InVal.CompareTo(right.InVal));
        RotationTrack.Points.Sort((left, right) => left.InVal.CompareTo(right.InVal));
        List<StructProperty> sortedLookupPoints = lookupPoints.OrderBy(point => point.GetProp<FloatProperty>("Time").Value).ToList();
        lookupPoints.Clear();
        foreach (StructProperty sortedLookupPoint in sortedLookupPoints)
        {
            lookupPoints.Add(sortedLookupPoint);
        }
        Keyframes.Sort((left, right) => left.Time.CompareTo(right.Time));
        PositionTrack.ReCalculateTangents();
        RotationTrack.ReCalculateTangents();
        WriteTracks();
        Changed?.Invoke();
    }

    private void WriteTracks()
    {
        PropertyCollection properties = Export.GetProperties();
        properties.AddOrReplaceProp(PositionTrack.ToStructProperty(Export.Game, "PosTrack"));
        properties.AddOrReplaceProp(RotationTrack.ToStructProperty(Export.Game, "EulerTrack"));
        properties.AddOrReplaceProp(lookupTrack);
        Export.WriteProperties(properties);
    }

    private StructProperty AddLookupPoint(InterpCurvePoint<Vector3> positionPoint, float time)
    {
        var lookupPoint = new StructProperty("InterpLookupPoint", false,
            new NameProperty("None", "GroupName"),
            new FloatProperty(time, "Time"));
        lookupPoints.Add(lookupPoint);
        lookupPointsByPositionPoint[positionPoint] = lookupPoint;
        return lookupPoint;
    }

    private static InterpCurvePoint<Vector3> FindPoint(InterpCurveVector track, float time)
        => track.Points.FirstOrDefault(point => MathF.Abs(point.InVal - time) <= KeyTimeTolerance);

    private static InterpCurvePoint<Vector3> AddPoint(InterpCurveVector track, float time, Vector3 value, EInterpCurveMode interpMode)
    {
        int index = track.AddPoint(time, value, Vector3.Zero, Vector3.Zero, interpMode);
        return track.Points[index];
    }
}
