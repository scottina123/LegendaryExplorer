using System;
using System.Collections.Generic;
using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Gammtek.Extensions;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using SharpDX;
using Vector4 = System.Numerics.Vector4;

namespace LegendaryExplorer.Misc;

internal static class PreviewShaderParameterWriter
{
    public static void WriteScalarUniform(this Span<byte> buffer, TUniformParameter<FShaderParameter> scalarParam,
        IReadOnlyList<Vector4> scalarValues, MEGame game)
    {
        if (!scalarParam.Param.IsBound()) return;
        if (scalarParam.Param.NumBytes > sizeof(float))
        {
            // Whole-register bindings index the float4 cache directly.
            buffer.WriteVal(scalarParam.Param, scalarValues[scalarParam.Index]);
        }
        else
        {
            // Only LE3 packs four scalar expressions per cached vector. Other games cache each
            // expression separately in X, including LE2 terrain texture scale and tiling values.
            float value = game == MEGame.LE3
                ? scalarValues[scalarParam.Index / 4][scalarParam.Index % 4]
                : scalarValues[scalarParam.Index].X;
            buffer.WriteVal(scalarParam.Param, value);
        }
    }

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
