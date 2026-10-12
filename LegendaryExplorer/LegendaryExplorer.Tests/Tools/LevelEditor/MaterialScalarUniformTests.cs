using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NativeSetters = LegendaryExplorer.Tools.LevelEditor.Scene3D.ShaderParameterSetters;
using LegacyScene = LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D;
using LegacySetters = LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D.ShaderParameterSetters;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class MaterialScalarUniformTests
{
    private const int ScalarOffset = 432;
    private const byte Sentinel = 0xCD;

    // The seven scalar uniforms of LIQ_Ground01_MatInst in BioA_IntHub_100Apartment.pcc.
    // Their LE2 pixel shader binds individual four-byte values at offsets 432 through 456.
    private static readonly string[] ScalarNames =
        ["Triplanar Blend Sharpness", "Texture Scale", "Tile_G", "Tile_Detail", "Tile_R", "Tile_G_Z", "Tile_R_Z"];
    private static readonly float[] TerrainValues = [10f, 1200f, 1f, 4f, 3.5f, 1f, 2f];

    [DataTestMethod]
    [DataRow(MEGame.ME1)]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void TerrainScalarsSurviveMaterialEvaluationAndBothPreviewSetters(MEGame game)
    {
        using var fixture = new MaterialFixture(game);
        TUniformParameter<FShaderParameter>[] bindings = Enumerable.Range(0, TerrainValues.Length)
            .Select(index => Binding(index, ScalarOffset + index * sizeof(float), sizeof(float))).ToArray();

        var nativePixelBuffer = CreateBuffer();
        WriteScalars(nativePixelBuffer, bindings, fixture.NativeMaterial.GetCachedPixelParameters(fixture.NativeContext).scalar, game);
        AssertBuffer(nativePixelBuffer, TerrainValues);

        var legacyPixelBuffer = CreateBuffer();
        WriteScalars(legacyPixelBuffer, bindings, fixture.LegacyMaterial.GetCachedPixelParameters(fixture.LegacyContext).scalar, game);
        AssertBuffer(legacyPixelBuffer, TerrainValues);

        var parameters = new FMaterialVertexShaderParameters
        {
            UniformVertexScalarShaderParameters = bindings,
            UniformVertexVectorShaderParameters = []
        };
        using var nativeMesh = new Mesh<LEVertex>(null, [], []);
        var nativeVertexBuffer = CreateBuffer();
        NativeSetters.WriteValues(ref parameters, nativeVertexBuffer, fixture.NativeContext, nativeMesh, fixture.NativeMaterial);
        AssertBuffer(nativeVertexBuffer, TerrainValues);

        using var legacyMesh = new LegacyScene.Mesh<LegacyScene.LEVertex>(null, [], []);
        var legacyVertexBuffer = CreateBuffer();
        LegacySetters.WriteValues(ref parameters, legacyVertexBuffer, fixture.LegacyContext, legacyMesh, fixture.LegacyMaterial);
        AssertBuffer(legacyVertexBuffer, TerrainValues);
    }

    [TestMethod]
    public void Le3WholeRegistersRetainEveryScalarAndZeroTheUnusedTail()
    {
        using var fixture = new MaterialFixture(MEGame.LE3);
        TUniformParameter<FShaderParameter>[] bindings =
        [
            Binding(0, ScalarOffset, 16),
            Binding(1, ScalarOffset + 16, 16)
        ];
        float[] expected = [.. TerrainValues, 0f];
        var nativeBuffer = CreateBuffer();
        WriteScalars(nativeBuffer, bindings, fixture.NativeMaterial.GetCachedPixelParameters(fixture.NativeContext).scalar, MEGame.LE3);
        AssertBuffer(nativeBuffer, expected);

        var legacyBuffer = CreateBuffer();
        WriteScalars(legacyBuffer, bindings, fixture.LegacyMaterial.GetCachedPixelParameters(fixture.LegacyContext).scalar, MEGame.LE3);
        AssertBuffer(legacyBuffer, expected);
    }

    [DataTestMethod]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void UnboundScalarParametersLeaveBufferUntouchedWithoutReadingTheCache(MEGame game)
    {
        var buffer = CreateBuffer();
        buffer.AsSpan().WriteScalarUniform(Binding(int.MaxValue, ushort.MaxValue, 0), [], game);
        var sharedBufferParameter = Binding(int.MaxValue, ushort.MaxValue, sizeof(float));
        sharedBufferParameter.Param.BufferIndex = 1;
        buffer.AsSpan().WriteScalarUniform(sharedBufferParameter, [], game);
        Assert.IsTrue(buffer.All(value => value == Sentinel));
    }

    private static TUniformParameter<FShaderParameter> Binding(int index, int offset, int size) => new()
    {
        Index = index,
        Param = new FShaderParameter { BaseIndex = (ushort)offset, NumBytes = (ushort)size }
    };

    private static byte[] CreateBuffer() => Enumerable.Repeat(Sentinel, 512).ToArray();

    private static void WriteScalars(byte[] buffer, IEnumerable<TUniformParameter<FShaderParameter>> bindings,
        IReadOnlyList<Vector4> values, MEGame game)
    {
        foreach (var binding in bindings)
        {
            buffer.AsSpan().WriteScalarUniform(binding, values, game);
        }
    }

    private static void AssertBuffer(byte[] buffer, float[] expected)
    {
        int size = expected.Length * sizeof(float);
        CollectionAssert.AreEqual(expected, MemoryMarshal.Cast<byte, float>(buffer.AsSpan(ScalarOffset, size)).ToArray());
        Assert.IsTrue(buffer.Take(ScalarOffset).All(value => value == Sentinel), "Constants preceding the scalar bindings changed.");
        Assert.IsTrue(buffer.Skip(ScalarOffset + size).All(value => value == Sentinel), "Constants following the scalar bindings changed.");
    }

    private sealed class MaterialFixture : IDisposable
    {
        private readonly IMEPackage package;
        public MeshRenderContext NativeContext { get; } = new();
        public LegacyScene.MeshRenderContext LegacyContext { get; } = new();
        public MaterialRenderProxy NativeMaterial { get; }
        public LegacyScene.MaterialRenderProxy LegacyMaterial { get; }

        public MaterialFixture(MEGame game)
        {
            LegendaryExplorerCore.LegendaryExplorerCoreLib.InitLib(System.Threading.Tasks.TaskScheduler.Default);
            package = MEPackageHandler.CreateMemoryEmptyPackage("TerrainScalarUniforms.pcc", game);
            // An orphan non-static MIC needs no cooked shader cache or installed game. The injected
            // uniform expressions stand in for its shader map while the real material evaluators run.
            ExportEntry export = package.CreateExport("Terrain", "MaterialInstanceConstant", indexed: false);
            export.WriteProperties([]);
            NativeMaterial = new MaterialRenderProxy(NativeContext, export);
            LegacyMaterial = new LegacyScene.MaterialRenderProxy(export);
            InjectExpressions(NativeMaterial);
            InjectExpressions(LegacyMaterial);
            NativeMaterial.SetScalarParameter("Texture Scale", TerrainValues[1]);
            LegacyMaterial.SetScalarParameter("Texture Scale", TerrainValues[1]);
        }

        private static void InjectExpressions(object material)
        {
            MaterialUniformExpression[] expressions = ScalarNames.Select((name, index) => (MaterialUniformExpression)new MaterialUniformExpressionScalarParameter
            {
                ParameterName = name,
                DefaultValue = index == 1 ? 1000f : TerrainValues[index]
            }).ToArray();
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            material.GetType().GetField("PixelScalarExpressions", flags)!.SetValue(material, expressions);
            material.GetType().GetField("VertexScalarExpressions", flags)!.SetValue(material, expressions);
        }

        public void Dispose() => package.Dispose();
    }
}
