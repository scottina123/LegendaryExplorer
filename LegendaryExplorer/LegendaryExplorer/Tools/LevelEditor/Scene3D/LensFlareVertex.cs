using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Packages;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

/// <summary>The cooked FLensFlareVertexFactory stream, distinct from sprite-particle vertices.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LensFlareVertex : IVertexBase
{
    private Vector4 position;
    public Vector4 SizeAndScaling;
    public float Rotation;
    public Vector2 UV;
    public Vector4 Color;
    public Vector4 FlareParameters;

    public readonly Vector3 Position => new(position.X, position.Y, position.Z);

    public LensFlareVertex(Vector3 center, Vector2 size, float rotation, Vector2 uv,
        Vector4 flareParameters, Vector4 color)
    {
        position = new Vector4(center, 1);
        SizeAndScaling = new Vector4(size, 1, 1);
        Rotation = rotation;
        UV = uv;
        FlareParameters = flareParameters;
        Color = color;
    }

    public void ToFloats(Span<float> floats) =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<LensFlareVertex, float>(ref this), Stride / 4).CopyTo(floats);

    public static IVertexBase Create(MEGame game, Vector3 position, Vector3 tangent, Vector4 normal, Fixed4<Vector4> uvs) =>
        new LensFlareVertex(position, Vector2.One, 0, new Vector2(uvs[0].X, uvs[0].Y), Vector4.One, Vector4.One);

    public static int Stride => 76;
    public static InputElement[] InputElements { get; } =
    [
        new("POSITION", 0, Format.R32G32B32A32_Float, 0),
        new("TANGENT", 0, Format.R32G32B32A32_Float, 0),
        new("BLENDWEIGHT", 0, Format.R32_Float, 0),
        new("TEXCOORD", 0, Format.R32G32_Float, 0),
        new("TEXCOORD", 1, Format.R32G32B32A32_Float, 0),
        new("TEXCOORD", 2, Format.R32G32B32A32_Float, 0)
    ];
}
