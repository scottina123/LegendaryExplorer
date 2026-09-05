using System;
using System.Numerics;
using LegendaryExplorerCore.Gammtek.Extensions;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using SharpDX;

namespace LegendaryExplorer.Misc;

internal static class PreviewShaderParameterWriter
{
    public static void WriteVal(this Span<byte> buffer, FShaderParameter parameter, Matrix3x3 value)
    {
        // HLSL constant-buffer matrix vectors occupy 16-byte registers. Matrix3x3 stores nine
        // tightly packed floats; copying those directly corrupts world-to-local camera directions.
        // The reflected size can be 44 bytes, excluding the final register's unused padding.
        var padded = new Matrix4x4(
            value.M11, value.M12, value.M13, 0,
            value.M21, value.M22, value.M23, 0,
            value.M31, value.M32, value.M33, 0,
            0, 0, 0, 0);
        buffer.WriteVal(parameter, padded);
    }

    public static unsafe void WriteVal<T>(this Span<byte> buffer, FShaderParameter parameter, T value) where T : unmanaged
    {
        if (!parameter.IsBound()) return;
        int bytesToWrite = Math.Min(sizeof(T), parameter.NumBytes);
        value.AsBytes()[..bytesToWrite].CopyTo(buffer[parameter.BaseIndex..]);
    }
}
