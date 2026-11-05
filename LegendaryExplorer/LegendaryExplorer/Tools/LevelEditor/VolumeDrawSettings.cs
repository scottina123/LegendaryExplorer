using LegendaryExplorer.Misc;
using LegendaryExplorerCore.Unreal;
using System.Numerics;

namespace LegendaryExplorer.Tools.LevelEditor;

public sealed class VolumeDrawSettings : NotifyPropertyChangedBase
{
    private float _x, _y, _z, _pitch, _yaw, _roll;
    private float _height = 200, _step = 25;
    private int _plane, _kind;
    private bool _snap = true, _xray = true;
    private string _name = "DrawnVolume";
    public float X { get => _x; set => SetProperty(ref _x, value); }
    public float Y { get => _y; set => SetProperty(ref _y, value); }
    public float Z { get => _z; set => SetProperty(ref _z, value); }
    public float Pitch { get => _pitch; set => SetProperty(ref _pitch, value); }
    public float Yaw { get => _yaw; set => SetProperty(ref _yaw, value); }
    public float Roll { get => _roll; set => SetProperty(ref _roll, value); }
    public float Height { get => _height; set => SetProperty(ref _height, value); }
    public float Step { get => _step; set => SetProperty(ref _step, value); }
    public int PlaneIndex { get => _plane; set => SetProperty(ref _plane, value); }
    public int KindIndex { get => _kind; set => SetProperty(ref _kind, value); }
    public bool Snap { get => _snap; set => SetProperty(ref _snap, value); }
    public bool Xray { get => _xray; set => SetProperty(ref _xray, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public Vector3 Origin => new(X, Y, Z);
    public void SetOrigin(Vector3 origin) { X = origin.X; Y = origin.Y; Z = origin.Z; }
    public ModelDrawingPlane GetPlane(Vector3 surfaceNormal)
    {
        if (PlaneIndex != 3) return ModelDrawingPlane.Create(Origin, PlaneIndex, Pitch, Yaw, Roll);
        var basis = ModelDrawingPlane.Create(Vector3.Zero, 0, Pitch, Yaw, Roll);
        var plane = ModelDrawingPlane.FromNormal(Origin, surfaceNormal);
        Vector3 Rotate(Vector3 v) => basis.U * v.X + basis.V * v.Y + basis.Normal * v.Z;
        return new ModelDrawingPlane(Origin, Rotate(plane.U), Rotate(plane.V));
    }
}
