using System;
using System.Numerics;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>A named camera location with rotation stored in degrees.</summary>
public sealed record LevelCameraPreset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public float Roll { get; init; }
    public float Pitch { get; init; }
    public float Yaw { get; init; }

    public static LevelCameraPreset FromCamera(SceneCamera camera, string name = null)
    {
        ArgumentNullException.ThrowIfNull(camera);
        // In orbit mode Position is the pivot; save the eye location so reopening
        // with zero focus depth preserves the view rather than moving to the pivot.
        Vector3 position = camera.FirstPerson || camera.IsOrthographic
            ? camera.Position
            : camera.Position - camera.CameraForward * camera.FocusDepth;
        return new LevelCameraPreset
        {
            Name = name,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
            // The orthographic view always looks down -Z with +Y as its up axis,
            // independently of the saved perspective angles and focus depth.
            Roll = camera.IsOrthographic ? 0 : camera.Roll * (180f / MathF.PI),
            Pitch = camera.IsOrthographic ? -90 : camera.Pitch * (180f / MathF.PI),
            Yaw = camera.IsOrthographic ? 90 : camera.Yaw * (180f / MathF.PI)
        };
    }

    public void ApplyTo(SceneCamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        camera.FocusDepth = 0;
        camera.Position = new Vector3(X, Y, Z);
        camera.Roll = Roll * (MathF.PI / 180f);
        camera.Pitch = Pitch * (MathF.PI / 180f);
        camera.Yaw = Yaw * (MathF.PI / 180f);
    }
}
