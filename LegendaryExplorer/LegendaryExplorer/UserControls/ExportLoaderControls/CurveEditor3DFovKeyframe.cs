using System;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

/// <summary>
/// An independently selectable key in the FOV float track paired with an InterpTrackMove.
/// Its viewport position is supplied by the move track evaluated at <see cref="Time"/>.
/// </summary>
public sealed class CurveEditor3DFovKeyframe : NotifyPropertyChangedBase, IHitProxy
{
    private readonly Action<CurveEditor3DFovKeyframe, float?, string> changed;
    private float time;
    private float value;
    private float arriveTangent;
    private float leaveTangent;
    private EInterpCurveMode interpMode;

    internal CurveEditor3DFovKeyframe(
        InterpCurvePoint<float> point,
        Action<CurveEditor3DFovKeyframe, float?, string> changed)
    {
        Point = point;
        time = point.InVal;
        value = point.OutVal;
        arriveTangent = point.ArriveTangent;
        leaveTangent = point.LeaveTangent;
        interpMode = point.InterpMode;
        this.changed = changed;
    }

    internal InterpCurvePoint<float> Point { get; }

    public float Time
    {
        get => time;
        set
        {
            if (time == value || !float.IsFinite(value))
            {
                return;
            }

            float previousTime = time;
            time = value;
            OnPropertyChanged();
            changed(this, previousTime, nameof(Time));
        }
    }

    public float Value
    {
        get => value;
        set
        {
            if (this.value == value || !float.IsFinite(value))
            {
                return;
            }

            this.value = value;
            OnPropertyChanged();
            changed(this, null, nameof(Value));
        }
    }

    public float ArriveTangent
    {
        get => arriveTangent;
        set
        {
            if (arriveTangent == value || !float.IsFinite(value))
            {
                return;
            }

            arriveTangent = value;
            OnPropertyChanged();
            changed(this, null, nameof(ArriveTangent));
        }
    }

    public float LeaveTangent
    {
        get => leaveTangent;
        set
        {
            if (leaveTangent == value || !float.IsFinite(value))
            {
                return;
            }

            leaveTangent = value;
            OnPropertyChanged();
            changed(this, null, nameof(LeaveTangent));
        }
    }

    public EInterpCurveMode InterpMode
    {
        get => interpMode;
        set
        {
            if (!SetProperty(ref interpMode, value))
            {
                return;
            }

            changed(this, null, nameof(InterpMode));
        }
    }

    internal void SynchronizeTangentsFromPoint()
    {
        if (arriveTangent != Point.ArriveTangent)
        {
            arriveTangent = Point.ArriveTangent;
            OnPropertyChanged(nameof(ArriveTangent));
        }
        if (leaveTangent != Point.LeaveTangent)
        {
            leaveTangent = Point.LeaveTangent;
            OnPropertyChanged(nameof(LeaveTangent));
        }
    }

    public int HitID { get; set; }

    public int HitPriority => IHitProxy.UIPriority;
}
