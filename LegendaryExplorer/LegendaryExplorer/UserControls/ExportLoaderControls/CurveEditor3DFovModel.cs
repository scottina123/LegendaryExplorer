using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

/// <summary>
/// Editable model for the FOVAngle InterpTrackFloatProp paired with a camera move track.
/// </summary>
public sealed class CurveEditor3DFovModel
{
    private const float KeyTimeTolerance = 0.0001f;

    public ExportEntry Export { get; private set; }

    /// <summary>
    /// When false, edits remain in this model until <see cref="CommitChanges"/> is called.
    /// </summary>
    public bool AutoCommit { get; set; } = true;

    public bool HasPendingChanges { get; private set; }

    public InterpCurve<float> Track { get; private set; }

    public ObservableCollection<CurveEditor3DFovKeyframe> Keyframes { get; } = [];

    public event Action Changed;

    public CurveEditor3DFovModelSnapshot CreateCacheSnapshot() => new()
    {
        InterpMethod = Track?.InterpMethod ?? EInterpMethodType.IMT_UseFixedTangentEvalAndNewAutoTangents,
        Points = Track?.Points.Select(CurveEditor3DFloatPointSnapshot.FromPoint).ToList() ?? [],
        HasPendingChanges = HasPendingChanges,
    };

    public void LoadCacheSnapshot(ExportEntry export, CurveEditor3DFovModelSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(snapshot);
        Load(export);
        Track.Points.Clear();
        Track.InterpMethod = snapshot.InterpMethod;
        Track.Points.AddRange(snapshot.Points.Select(point => point.ToPoint()));
        RebuildKeyframes();
        HasPendingChanges = snapshot.HasPendingChanges;
    }

    public void Load(ExportEntry export)
    {
        ArgumentNullException.ThrowIfNull(export);
        Export = export;
        StructProperty floatTrack = export.GetProperty<StructProperty>("FloatTrack")
                                    ?? new InterpCurve<float>().ToStructProperty(export.Game, "FloatTrack");
        Track = InterpCurve<float>.FromStructProperty(floatTrack, export.Game);
        RebuildKeyframes();
        HasPendingChanges = false;
    }

    public void Clear()
    {
        Export = null;
        Track = null;
        Keyframes.Clear();
        HasPendingChanges = false;
    }

    public void CommitChanges()
    {
        if (Export is null || !HasPendingChanges)
        {
            return;
        }

        WriteTrackToExport();
        HasPendingChanges = false;
    }

    public bool HasKeyframeAtTime(float time, CurveEditor3DFovKeyframe excludedKeyframe = null)
        => Keyframes.Any(keyframe => keyframe != excludedKeyframe
                                    && MathF.Abs(keyframe.Time - time) <= KeyTimeTolerance);

    public CurveEditor3DFovKeyframe AddKeyframe(float time, float value)
    {
        if (Export is null || !float.IsFinite(time) || !float.IsFinite(value) || HasKeyframeAtTime(time))
        {
            return null;
        }

        EInterpCurveMode mode = Keyframes.LastOrDefault()?.InterpMode ?? EInterpCurveMode.CIM_CurveAuto;
        int pointIndex = Track.AddPoint(time, value, 0, 0, mode);
        var keyframe = new CurveEditor3DFovKeyframe(Track.Points[pointIndex], CommitKeyframe);
        Keyframes.Add(keyframe);
        CommitKeyframe(keyframe, null, null);
        return keyframe;
    }

    public CurveEditor3DFovKeyframe DeleteKeyframe(CurveEditor3DFovKeyframe keyframe)
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

        Track.Points.Remove(keyframe.Point);
        Keyframes.RemoveAt(index);
        Track.ReCalculateTangents();
        foreach (CurveEditor3DFovKeyframe item in Keyframes)
        {
            item.SynchronizeTangentsFromPoint();
        }
        WriteTrack();
        Changed?.Invoke();
        return Keyframes.Count == 0 ? null : Keyframes[Math.Min(index, Keyframes.Count - 1)];
    }

    private void RebuildKeyframes()
    {
        Keyframes.Clear();
        foreach (InterpCurvePoint<float> point in Track.Points.OrderBy(point => point.InVal))
        {
            Keyframes.Add(new CurveEditor3DFovKeyframe(point, CommitKeyframe));
        }
    }

    private void CommitKeyframe(CurveEditor3DFovKeyframe keyframe, float? previousTime, string changedProperty)
    {
        if (Export is null)
        {
            return;
        }

        bool tangentEdited = changedProperty is nameof(CurveEditor3DFovKeyframe.ArriveTangent)
            or nameof(CurveEditor3DFovKeyframe.LeaveTangent);
        float requestedArriveTangent = keyframe.ArriveTangent;
        float requestedLeaveTangent = keyframe.LeaveTangent;
        keyframe.Point.InVal = keyframe.Time;
        keyframe.Point.OutVal = keyframe.Value;
        keyframe.Point.ArriveTangent = requestedArriveTangent;
        keyframe.Point.LeaveTangent = requestedLeaveTangent;
        keyframe.Point.InterpMode = keyframe.InterpMode;
        Track.Points.Sort((left, right) => left.InVal.CompareTo(right.InVal));
        SortKeyframes();
        Track.ReCalculateTangents();
        if (tangentEdited)
        {
            // ReCalculateTangents keeps automatic modes coherent after time/value edits, but a direct
            // tangent edit must preserve the exact value entered by the user.
            keyframe.Point.ArriveTangent = requestedArriveTangent;
            keyframe.Point.LeaveTangent = requestedLeaveTangent;
        }
        foreach (CurveEditor3DFovKeyframe item in Keyframes)
        {
            item.SynchronizeTangentsFromPoint();
        }
        WriteTrack();
        Changed?.Invoke();
    }

    private void SortKeyframes()
    {
        List<CurveEditor3DFovKeyframe> sorted = Keyframes.OrderBy(keyframe => keyframe.Time).ToList();
        for (int targetIndex = 0; targetIndex < sorted.Count; targetIndex++)
        {
            int currentIndex = Keyframes.IndexOf(sorted[targetIndex]);
            if (currentIndex != targetIndex)
            {
                Keyframes.Move(currentIndex, targetIndex);
            }
        }
    }

    private void WriteTrack()
    {
        if (!AutoCommit)
        {
            HasPendingChanges = true;
            return;
        }

        WriteTrackToExport();
        HasPendingChanges = false;
    }

    private void WriteTrackToExport() => Export.WriteProperty(Track.ToStructProperty(Export.Game, "FloatTrack"));
}

public sealed class CurveEditor3DFovModelSnapshot
{
    public EInterpMethodType InterpMethod { get; set; }
    public List<CurveEditor3DFloatPointSnapshot> Points { get; set; } = [];
    public bool HasPendingChanges { get; set; }
}

public sealed class CurveEditor3DFloatPointSnapshot
{
    public float Time { get; set; }
    public float Value { get; set; }
    public float ArriveTangent { get; set; }
    public float LeaveTangent { get; set; }
    public EInterpCurveMode InterpMode { get; set; }

    internal static CurveEditor3DFloatPointSnapshot FromPoint(InterpCurvePoint<float> point) => new()
    {
        Time = point.InVal,
        Value = point.OutVal,
        ArriveTangent = point.ArriveTangent,
        LeaveTangent = point.LeaveTangent,
        InterpMode = point.InterpMode,
    };

    internal InterpCurvePoint<float> ToPoint() =>
        new(Time, Value, ArriveTangent, LeaveTangent, InterpMode);
}
