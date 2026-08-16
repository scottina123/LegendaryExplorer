using SharpDX;

namespace LE1GalaxyMapEditor.Rendering;

internal sealed class PreviewTransformSettings
{
    public TransformSettings Planet { get; } = new()
    {
        X = -5406.77295f,
        Y = 13571.8086f,
        Z = -40187.1992f,
        Pitch = 45,
        Yaw = 18046 * (360.0f / 65536.0f),
        Roll = -31162 * (360.0f / 65536.0f),
        Scale = 5.481f
    };

    public TransformSettings Corona { get; } = new()
    {
        X = -5406.77295f,
        Y = 13571.8086f,
        Z = -40187.1992f,
        Pitch = -5024 * (360.0f / 65536.0f),
        Yaw = -1469 * (360.0f / 65536.0f),
        Roll = -3365 * (360.0f / 65536.0f),
        Scale = 5.5f * 0.99000001f
    };
}

internal sealed class PreviewLightingSettings
{
    public float SkyLightBrightness = 0.25f;
    public Vector4 SkyLightColor = new(171, 189, 197, 0);

    public PointLightSettings Light1 { get; } = new()
    {
        X = -4855.09912f,
        Y = 13333.7559f,
        Z = -39852.4414f,
        Radius = 1024,
        FalloffExponent = 2
    };

    public PointLightSettings Light2 { get; } = new()
    {
        X = -5756.09912f,
        Y = 12637.3496f,
        Z = -39954.3008f,
        Radius = 1000,
        FalloffExponent = 2
    };
}

internal sealed class PointLightSettings
{
    public float X;
    public float Y;
    public float Z;
    public float Radius;
    public float FalloffExponent = 2;
}

internal sealed class TransformSettings
{
    public float X;
    public float Y;
    public float Z;
    public float Pitch;
    public float Yaw;
    public float Roll;
    public float Scale = 1;
}
