using System;
using System.Numerics;

namespace LegendaryExplorer.Tools.AssetDatabase.VFXPreview;

/// <summary>
/// Builds the per-particle transform for ParticleModuleTypeDataMesh emitters, following the
/// EMeshScreenAlignment / EMeshCameraFacingOptions / EParticleAxisLock semantics declared in Engine.
/// </summary>
public static class VfxMeshMath
{
    public static Matrix4x4 CreateParticleTransform(
        in VfxParticle particle,
        VfxEmitterDefinition emitter,
        VfxMeshEmitterDefinition mesh,
        Vector3 cameraPosition,
        Vector3 cameraRight,
        Vector3 cameraUp,
        Vector3 cameraForward,
        Matrix4x4 previewTransform)
    {
        Vector3 localPosition = particle.Position + particle.OrbitOffset;
        Vector3 scale = particle.Size;
        Quaternion preRotation = Quaternion.CreateFromYawPitchRoll(
            DegreesToRadians(mesh.PreRotation.Y),
            DegreesToRadians(mesh.PreRotation.X),
            DegreesToRadians(mesh.PreRotation.Z));
        Quaternion meshRotation = Quaternion.CreateFromYawPitchRoll(
            particle.MeshRotation.Y,
            particle.MeshRotation.X,
            particle.MeshRotation.Z);

        Vector3 worldPosition = Vector3.Transform(localPosition, previewTransform);
        Matrix4x4 alignment = CreateAlignmentBasis(
            particle,
            emitter,
            mesh,
            worldPosition,
            cameraPosition,
            cameraRight,
            cameraUp,
            cameraForward);

        Matrix4x4 local = Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(preRotation)
            * Matrix4x4.CreateFromQuaternion(meshRotation)
            * alignment
            * Matrix4x4.CreateTranslation(localPosition);
        return local * previewTransform;
    }

    private static Matrix4x4 CreateAlignmentBasis(
        in VfxParticle particle,
        VfxEmitterDefinition emitter,
        VfxMeshEmitterDefinition mesh,
        Vector3 worldPosition,
        Vector3 cameraPosition,
        Vector3 cameraRight,
        Vector3 cameraUp,
        Vector3 cameraForward)
    {
        Vector3 lockedAxis = GetLockedAxis(mesh.AxisLockOption);
        if (mesh.CameraFacing)
        {
            return CreateCameraFacingBasis(particle, mesh, worldPosition, cameraPosition, cameraRight, cameraUp, lockedAxis);
        }

        if (emitter.ScreenAlignment != VfxScreenAlignment.TypeSpecific)
        {
            return lockedAxis == Vector3.Zero ? Matrix4x4.Identity : CreateBasis(SafeNormalize(cameraPosition - worldPosition, -cameraForward), lockedAxis);
        }

        Vector3 toCamera = SafeNormalize(cameraPosition - worldPosition, -cameraForward);
        return mesh.MeshAlignment switch
        {
            VfxMeshAlignment.FaceCameraWithLockedAxis when lockedAxis != Vector3.Zero => CreateBasis(toCamera, lockedAxis),
            VfxMeshAlignment.FaceCameraWithSpin => CreateBasis(toCamera, RotateAround(cameraUp, toCamera, particle.Rotation)),
            _ => CreateBasis(toCamera, RotateAround(cameraUp, toCamera, particle.Rotation))
        };
    }

    private static Matrix4x4 CreateCameraFacingBasis(
        in VfxParticle particle,
        VfxMeshEmitterDefinition mesh,
        Vector3 worldPosition,
        Vector3 cameraPosition,
        Vector3 cameraRight,
        Vector3 cameraUp,
        Vector3 lockedAxis)
    {
        Vector3 toCamera = SafeNormalize(cameraPosition - worldPosition, Vector3.UnitX);
        Vector3 velocity = SafeNormalize(particle.Velocity, toCamera);
        return mesh.CameraFacingOption switch
        {
            VfxMeshCameraFacing.XAxisFacingNoUp => CreateBasis(toCamera, cameraUp),
            VfxMeshCameraFacing.XAxisFacingZUp => CreateBasis(toCamera, Vector3.UnitZ),
            VfxMeshCameraFacing.XAxisFacingNegativeZUp => CreateBasis(toCamera, -Vector3.UnitZ),
            VfxMeshCameraFacing.XAxisFacingYUp => CreateBasis(toCamera, Vector3.UnitY),
            VfxMeshCameraFacing.XAxisFacingNegativeYUp => CreateBasis(toCamera, -Vector3.UnitY),
            VfxMeshCameraFacing.LockedAxisZAxisFacing => CreateBasis(lockedAxis == Vector3.Zero ? toCamera : lockedAxis, Vector3.UnitZ),
            VfxMeshCameraFacing.LockedAxisNegativeZAxisFacing => CreateBasis(lockedAxis == Vector3.Zero ? toCamera : lockedAxis, -Vector3.UnitZ),
            VfxMeshCameraFacing.LockedAxisYAxisFacing => CreateBasis(lockedAxis == Vector3.Zero ? toCamera : lockedAxis, Vector3.UnitY),
            VfxMeshCameraFacing.LockedAxisNegativeYAxisFacing => CreateBasis(lockedAxis == Vector3.Zero ? toCamera : lockedAxis, -Vector3.UnitY),
            VfxMeshCameraFacing.VelocityAlignedZAxisFacing => CreateBasis(velocity, Vector3.UnitZ),
            VfxMeshCameraFacing.VelocityAlignedNegativeZAxisFacing => CreateBasis(velocity, -Vector3.UnitZ),
            VfxMeshCameraFacing.VelocityAlignedYAxisFacing => CreateBasis(velocity, Vector3.UnitY),
            VfxMeshCameraFacing.VelocityAlignedNegativeYAxisFacing => CreateBasis(velocity, -Vector3.UnitY),
            _ => CreateBasis(toCamera, cameraRight)
        };
    }

    /// <summary>
    /// Creates an orthonormal basis whose X axis points along <paramref name="forward"/> and whose Z axis is
    /// as close to <paramref name="up"/> as possible, which matches how UE3 orients mesh particles.
    /// </summary>
    private static Matrix4x4 CreateBasis(Vector3 forward, Vector3 up)
    {
        Vector3 x = SafeNormalize(forward, Vector3.UnitX);
        Vector3 desiredUp = SafeNormalize(up, Vector3.UnitZ);
        Vector3 y = Vector3.Cross(desiredUp, x);
        if (y.LengthSquared() < 0.000001f)
        {
            desiredUp = MathF.Abs(x.Z) > 0.99f ? Vector3.UnitY : Vector3.UnitZ;
            y = Vector3.Cross(desiredUp, x);
        }
        y = SafeNormalize(y, Vector3.UnitY);
        Vector3 z = Vector3.Cross(x, y);
        return new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
    }

    private static Vector3 RotateAround(Vector3 value, Vector3 axis, float radians)
        => Vector3.Transform(value, Quaternion.CreateFromAxisAngle(SafeNormalize(axis, Vector3.UnitZ), radians));

    private static Vector3 GetLockedAxis(VfxAxisLock axisLock) => axisLock switch
    {
        VfxAxisLock.PositiveX or VfxAxisLock.RotateX => Vector3.UnitX,
        VfxAxisLock.NegativeX => -Vector3.UnitX,
        VfxAxisLock.PositiveY or VfxAxisLock.RotateY => Vector3.UnitY,
        VfxAxisLock.NegativeY => -Vector3.UnitY,
        VfxAxisLock.PositiveZ or VfxAxisLock.RotateZ => Vector3.UnitZ,
        VfxAxisLock.NegativeZ => -Vector3.UnitZ,
        _ => Vector3.Zero
    };

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
        => value.LengthSquared() < 0.000001f ? fallback : Vector3.Normalize(value);

    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);
}
