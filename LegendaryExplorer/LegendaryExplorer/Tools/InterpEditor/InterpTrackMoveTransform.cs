using System.Numerics;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.Tools.InterpEditor;

public static class InterpTrackMoveTransform
{
    public static CameraOrigin ToWorld(CameraOrigin basis, CameraOrigin local)
    {
        Matrix4x4 basisRotation = Rotator.FromDegreesVector(basis.Rotation).ToRotationMatrix();
        Matrix4x4 localRotation = Rotator.FromDegreesVector(local.Rotation).ToRotationMatrix();
        Vector3 location = basis.Location + Vector3.TransformNormal(local.Location, basisRotation);
        Vector3 rotation = (localRotation * basisRotation).GetRotator().GetDegreesVector();
        return new CameraOrigin(location, rotation);
    }

    public static CameraOrigin ToLocal(CameraOrigin basis, CameraOrigin world)
    {
        Matrix4x4 basisRotation = Rotator.FromDegreesVector(basis.Rotation).ToRotationMatrix();
        Matrix4x4.Invert(basisRotation, out Matrix4x4 inverseBasisRotation);
        Matrix4x4 worldRotation = Rotator.FromDegreesVector(world.Rotation).ToRotationMatrix();
        Vector3 location = Vector3.TransformNormal(world.Location - basis.Location, inverseBasisRotation);
        Vector3 rotation = (worldRotation * inverseBasisRotation).GetRotator().GetDegreesVector();
        return new CameraOrigin(location, rotation);
    }
}
