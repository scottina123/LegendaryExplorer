using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpDX;
using Vector3 = System.Numerics.Vector3;
using NativeSetters = LegendaryExplorer.Tools.LevelEditor.Scene3D.ShaderParameterSetters;
using LegacySetters = LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D.ShaderParameterSetters;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class PreviewShaderMatrixTests
{
    private const int MatrixOffset = 16;
    private const int MatrixSize = 44;

    [DataTestMethod]
    [DataRow(0f, -45f)]
    [DataRow(0f, 0f)]
    [DataRow(0f, 45f)]
    [DataRow(90f, -45f)]
    [DataRow(-120f, 45f)]
    public void ActorVertexFactoryPreservesCameraDirection(float actorYaw, float cameraYaw)
    {
        byte[] buffer = Enumerable.Repeat((byte)0xCD, 80).ToArray();
        using var mesh = new Mesh<LEVertex>(null, [], []);
        mesh.LocalToWorld = Matrix4x4.CreateRotationZ(actorYaw * MathF.PI / 180)
                            * Matrix4x4.CreateTranslation(1200, -800, 75);
        var context = new MeshRenderContext();
        NativeSetters.WriteValues(CreateFactoryParameters(), buffer, context, mesh, null);

        Vector3 localDirection = CameraDirection(cameraYaw);
        Vector3 worldDirection = Vector3.TransformNormal(localDirection, mesh.LocalToWorld);
        AssertDirection(localDirection, ReadShaderDirection(buffer, worldDirection));
        AssertNeighboringConstantsUnchanged(buffer);
    }

    [DataTestMethod]
    [DataRow(-60f)]
    [DataRow(-45f)]
    [DataRow(0f)]
    [DataRow(45f)]
    [DataRow(60f)]
    public void MorphAndMeshVertexFactoryPreservesCameraDirection(float cameraYaw)
    {
        byte[] buffer = Enumerable.Repeat((byte)0xCD, 80).ToArray();
        LegacySetters.WriteValues(CreateFactoryParameters(), buffer, null, null, null);

        Vector3 direction = CameraDirection(cameraYaw);
        AssertDirection(direction, ReadShaderDirection(buffer, direction));
        AssertNeighboringConstantsUnchanged(buffer);
    }

    [DataTestMethod]
    [DataRow(44)]
    [DataRow(48)]
    public void MatrixUsesRegisterPaddingWithoutOverwritingNextParameter(int byteCount)
    {
        byte[] buffer = Enumerable.Repeat((byte)0xCD, 80).ToArray();
        buffer.AsSpan().WriteVal(new FShaderParameter { BaseIndex = MatrixOffset, NumBytes = (ushort)byteCount },
            new Matrix3x3(1, 2, 3, 4, 5, 6, 7, 8, 9));
        ReadOnlySpan<float> registers = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(MatrixOffset, byteCount));
        float[] expected = [1, 2, 3, 0, 4, 5, 6, 0, 7, 8, 9, 0];
        CollectionAssert.AreEqual(expected[..registers.Length], registers.ToArray());
        Assert.IsTrue(buffer.AsSpan(MatrixOffset + byteCount).ToArray().All(value => value == 0xCD));
    }

    [TestMethod]
    public void UnboundMatrixLeavesConstantsUntouched()
    {
        byte[] buffer = Enumerable.Repeat((byte)0xCD, 80).ToArray();
        buffer.AsSpan().WriteVal(new FShaderParameter { BaseIndex = ushort.MaxValue, NumBytes = 0 }, Matrix3x3.Identity);
        Assert.IsTrue(buffer.All(value => value == 0xCD));
    }

    private static FLocalVertexFactoryShaderParameters CreateFactoryParameters() => new()
    {
        WorldToLocal = new FShaderParameter { BaseIndex = MatrixOffset, NumBytes = MatrixSize }
    };

    private static Vector3 CameraDirection(float yaw)
        => new(MathF.Cos(yaw * MathF.PI / 180), MathF.Sin(yaw * MathF.PI / 180), 0.15f);

    private static Vector3 ReadShaderDirection(byte[] buffer, Vector3 direction)
    {
        // Read the three 16-byte HLSL registers, as the native vertex shader does.
        ReadOnlySpan<float> f = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(MatrixOffset, MatrixSize));
        return new Vector3(
            f[0] * direction.X + f[4] * direction.Y + f[8] * direction.Z,
            f[1] * direction.X + f[5] * direction.Y + f[9] * direction.Z,
            f[2] * direction.X + f[6] * direction.Y + f[10] * direction.Z);
    }

    private static void AssertDirection(Vector3 expected, Vector3 actual)
        => Assert.IsTrue(Vector3.Distance(expected, actual) < 0.00001f, $"Expected {expected}, got {actual}");

    private static void AssertNeighboringConstantsUnchanged(byte[] buffer)
    {
        Assert.IsTrue(buffer.Take(MatrixOffset).All(value => value == 0xCD));
        Assert.IsTrue(buffer.Skip(MatrixOffset + MatrixSize).All(value => value == 0xCD));
    }
}
