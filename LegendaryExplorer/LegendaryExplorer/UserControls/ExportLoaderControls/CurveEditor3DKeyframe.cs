using System;
using System.Numerics;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Tools.InterpEditor;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public sealed class CurveEditor3DKeyframe : NotifyPropertyChangedBase, IHitProxy, ITransformWidgetTarget
{
    private readonly Action<CurveEditor3DKeyframe, float?> changed;
    private float time;
    private Vector3 location;
    private Vector3 rotation;
    private CameraOrigin? coordinateBasis;
    private EInterpCurveMode posTrackInterpMode;
    private EInterpCurveMode eulerTrackInterpMode;

    internal CurveEditor3DKeyframe(
        InterpCurvePoint<Vector3> positionPoint,
        InterpCurvePoint<Vector3> rotationPoint,
        Vector3 rotation,
        Action<CurveEditor3DKeyframe, float?> changed)
    {
        PositionPoint = positionPoint;
        RotationPoint = rotationPoint;
        time = positionPoint.InVal;
        location = positionPoint.OutVal;
        this.rotation = rotation;
        posTrackInterpMode = positionPoint.InterpMode;
        eulerTrackInterpMode = rotationPoint?.InterpMode ?? positionPoint.InterpMode;
        this.changed = changed;
    }

    internal InterpCurvePoint<Vector3> PositionPoint { get; }

    internal InterpCurvePoint<Vector3> RotationPoint { get; set; }

    public float Time
    {
        get => time;
        set
        {
            if (time == value)
            {
                return;
            }

            float previousTime = time;
            time = value;
            OnPropertyChanged();
            changed(this, previousTime);
        }
    }

    public Vector3 Location
    {
        get => location;
        set => SetLocation(value, null, commit: true);
    }

    public Vector3 Rotation => rotation;

    public float X
    {
        get => location.X;
        set => SetLocation(location with { X = value }, nameof(X), commit: true);
    }

    public float Y
    {
        get => location.Y;
        set => SetLocation(location with { Y = value }, nameof(Y), commit: true);
    }

    public float Z
    {
        get => location.Z;
        set => SetLocation(location with { Z = value }, nameof(Z), commit: true);
    }

    public float Roll
    {
        get => rotation.X;
        set => SetRotation(rotation with { X = value }, nameof(Roll), commit: true);
    }

    public float Pitch
    {
        get => rotation.Y;
        set => SetRotation(rotation with { Y = value }, nameof(Pitch), commit: true);
    }

    public float Yaw
    {
        get => rotation.Z;
        set => SetRotation(rotation with { Z = value }, nameof(Yaw), commit: true);
    }

    public Vector3 DisplayLocation
    {
        get => DisplayOrigin.Location;
        set => SetDisplayLocation(value, commit: true);
    }

    public Vector3 DisplayRotation
    {
        get => DisplayOrigin.Rotation;
        set => SetDisplayRotation(value, commit: true);
    }

    public float DisplayX
    {
        get => DisplayLocation.X;
        set => SetDisplayLocation(DisplayLocation with { X = value }, commit: true);
    }

    public float DisplayY
    {
        get => DisplayLocation.Y;
        set => SetDisplayLocation(DisplayLocation with { Y = value }, commit: true);
    }

    public float DisplayZ
    {
        get => DisplayLocation.Z;
        set => SetDisplayLocation(DisplayLocation with { Z = value }, commit: true);
    }

    public float DisplayRoll
    {
        get => DisplayRotation.X;
        set => SetDisplayRotation(DisplayRotation with { X = value }, commit: true);
    }

    public float DisplayPitch
    {
        get => DisplayRotation.Y;
        set => SetDisplayRotation(DisplayRotation with { Y = value }, commit: true);
    }

    public float DisplayYaw
    {
        get => DisplayRotation.Z;
        set => SetDisplayRotation(DisplayRotation with { Z = value }, commit: true);
    }

    public EInterpCurveMode PosTrackInterpMode
    {
        get => posTrackInterpMode;
        set => SetPosTrackInterpMode(value, commit: true);
    }

    public EInterpCurveMode EulerTrackInterpMode
    {
        get => eulerTrackInterpMode;
        set => SetEulerTrackInterpMode(value, commit: true);
    }

    internal bool SetPosTrackInterpMode(EInterpCurveMode value, bool commit)
    {
        if (!SetProperty(ref posTrackInterpMode, value, nameof(PosTrackInterpMode)))
        {
            return false;
        }

        if (commit)
        {
            changed(this, null);
        }

        return true;
    }

    internal bool SetEulerTrackInterpMode(EInterpCurveMode value, bool commit)
    {
        if (!SetProperty(ref eulerTrackInterpMode, value, nameof(EulerTrackInterpMode)))
        {
            return false;
        }

        if (commit)
        {
            changed(this, null);
        }

        return true;
    }

    public int HitID { get; set; }

    public int HitPriority => IHitProxy.UIPriority;

    internal CameraOrigin DisplayOrigin => coordinateBasis is { } basis
        ? InterpTrackMoveTransform.ToWorld(basis, new CameraOrigin(location, rotation))
        : new CameraOrigin(location, rotation);

    Vector3 ITransformWidgetTarget.Location
    {
        get => DisplayOrigin.Location;
        set => SetDisplayLocation(value, commit: true);
    }

    Rotator ITransformWidgetTarget.Rotation
    {
        get => Rotator.FromDegreesVector(DisplayOrigin.Rotation);
        set => SetDisplayRotation(value.GetDegreesVector(), commit: true);
    }

    public float DrawScale { get; set; } = 1f;

    public Vector3 DrawScale3D { get; set; } = Vector3.One;

    public bool IsReadOnly => false;

    public Matrix4x4 LocalToWorld => ActorUtils.ComposeLocalToWorld(DisplayOrigin.Location,
        Rotator.FromDegreesVector(DisplayOrigin.Rotation), Vector3.One);

    public TransformSnapshot SnapshotTransform() => new(DisplayOrigin.Location,
        Rotator.FromDegreesVector(DisplayOrigin.Rotation), DrawScale, DrawScale3D);

    internal void SetCoordinateBasis(CameraOrigin? basis)
    {
        coordinateBasis = basis;
        NotifyDisplayTransformChanged();
        OnPropertyChanged(nameof(LocalToWorld));
    }

    internal void SetDisplayLocation(Vector3 value, bool commit)
    {
        Vector3 localLocation = coordinateBasis is { } basis
            ? InterpTrackMoveTransform.ToLocal(basis, new CameraOrigin(value, DisplayRotation)).Location
            : value;
        SetLocation(localLocation, null, commit);
    }

    internal void SetDisplayRotation(Vector3 value, bool commit)
    {
        Vector3 localRotation = coordinateBasis is { } basis
            ? InterpTrackMoveTransform.ToLocal(basis, new CameraOrigin(DisplayLocation, value)).Rotation
            : value;
        SetRotation(localRotation, null, commit);
    }

    internal void SetLocation(Vector3 value, bool commit)
        => SetLocation(value, null, commit);

    internal void SetRotation(Vector3 value, bool commit)
        => SetRotation(value, null, commit);

    private void SetLocation(Vector3 value, string propertyName, bool commit)
    {
        if (location == value)
        {
            return;
        }

        location = value;
        if (propertyName is not null)
        {
            OnPropertyChanged(propertyName);
        }
        else
        {
            OnPropertyChanged(nameof(X));
            OnPropertyChanged(nameof(Y));
            OnPropertyChanged(nameof(Z));
        }
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(LocalToWorld));
        NotifyDisplayLocationChanged();
        if (commit)
        {
            changed(this, null);
        }
    }

    private void SetRotation(Vector3 value, string propertyName, bool commit)
    {
        if (rotation == value)
        {
            return;
        }

        rotation = value;
        if (propertyName is not null)
        {
            OnPropertyChanged(propertyName);
        }
        else
        {
            OnPropertyChanged(nameof(Roll));
            OnPropertyChanged(nameof(Pitch));
            OnPropertyChanged(nameof(Yaw));
        }
        OnPropertyChanged(nameof(Rotation));
        OnPropertyChanged(nameof(LocalToWorld));
        NotifyDisplayRotationChanged();
        if (commit)
        {
            changed(this, null);
        }
    }

    private void NotifyDisplayTransformChanged()
    {
        NotifyDisplayLocationChanged();
        NotifyDisplayRotationChanged();
    }

    private void NotifyDisplayLocationChanged()
    {
        OnPropertyChanged(nameof(DisplayLocation));
        OnPropertyChanged(nameof(DisplayX));
        OnPropertyChanged(nameof(DisplayY));
        OnPropertyChanged(nameof(DisplayZ));
    }

    private void NotifyDisplayRotationChanged()
    {
        OnPropertyChanged(nameof(DisplayRotation));
        OnPropertyChanged(nameof(DisplayRoll));
        OnPropertyChanged(nameof(DisplayPitch));
        OnPropertyChanged(nameof(DisplayYaw));
    }
}
