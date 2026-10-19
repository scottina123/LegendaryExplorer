using System;
using LegendaryExplorer.Misc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpDX.D3DCompiler;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class PreviewShaderInputSignatureTests
{
    [TestMethod]
    public void RestoresWidenedInputWithoutChangingDecompilerBody()
    {
        const string original = """
            void main(float3 v0 : TEXCOORD10, float4 v1 : TEXCOORD11, out float4 o0 : SV_Target0)
            {
                o0 = float4(v0.xyz + v1.xyz, 1);
            }
            """;
        const string widened = """
            void main(float4 v0 : TEXCOORD10, float4 v1 : TEXCOORD11, out float4 o0 : SV_Target0)
            {
                o0 = float4(v0.xyz + v1.xyz, v0.w + 1);
            }
            """;
        using ShaderBytecode originalBytecode = ShaderBytecode.Compile(original, "main", "ps_5_0").Bytecode;
        using ShaderBytecode widenedBytecode = ShaderBytecode.Compile(widened, "main", "ps_5_0").Bytecode;
        using var widenedReflection = new ShaderReflection(widenedBytecode);
        Assert.AreEqual(15, (int)widenedReflection.GetInputParameterDescription(0).UsageMask);

        string restored = PreviewShaderInputSignature.Restore(widened, originalBytecode.Data);
        using ShaderBytecode restoredBytecode = ShaderBytecode.Compile(restored, "main", "ps_5_0").Bytecode;

        StringAssert.Contains(restored, "float3 lexPreviewInput_v0 : TEXCOORD10");
        StringAssert.Contains(restored, "float4 v0 = float4(lexPreviewInput_v0, 0);");
        StringAssert.Contains(restored, "o0 = float4(v0.xyz + v1.xyz, v0.w + 1);");
        AssertSameInputs(originalBytecode, restoredBytecode);
    }

    [TestMethod]
    public void KeepsPackedSemanticComponentsAndRegisters()
    {
        const string source = """
            void main(float3 v0 : TEXCOORD10, float w0 : TEXCOORD7,
                      float4 v1 : TEXCOORD0, out float4 o0 : SV_Target0)
            {
                o0 = float4(v0.xyz + v1.xyz, w0.x);
            }
            """;
        using ShaderBytecode bytecode = ShaderBytecode.Compile(source, "main", "ps_5_0").Bytecode;
        using var reflection = new ShaderReflection(bytecode);
        ShaderParameterDescription packed = reflection.GetInputParameterDescription(1);
        Assert.AreEqual(0, packed.Register);
        Assert.AreEqual(8, (int)packed.UsageMask);

        string restored = PreviewShaderInputSignature.Restore(source, bytecode.Data);

        Assert.AreEqual(source, restored, "Packed declarations should remain byte-for-byte unchanged.");
        using ShaderBytecode restoredBytecode = ShaderBytecode.Compile(restored, "main", "ps_5_0").Bytecode;
        AssertSameInputs(bytecode, restoredBytecode);

        string widened = source.Replace("float3 v0", "float4 v0", StringComparison.Ordinal);
        string repaired = PreviewShaderInputSignature.Restore(widened, bytecode.Data);
        StringAssert.Contains(repaired, "float w0 : TEXCOORD7");
        using ShaderBytecode repairedBytecode = ShaderBytecode.Compile(repaired, "main", "ps_5_0").Bytecode;
        AssertSameInputs(bytecode, repairedBytecode);
    }

    private static void AssertSameInputs(ShaderBytecode expectedBytecode, ShaderBytecode actualBytecode)
    {
        using var expected = new ShaderReflection(expectedBytecode);
        using var actual = new ShaderReflection(actualBytecode);
        Assert.AreEqual(expected.Description.InputParameters, actual.Description.InputParameters);
        for (int i = 0; i < expected.Description.InputParameters; i++)
        {
            ShaderParameterDescription expectedInput = expected.GetInputParameterDescription(i);
            ShaderParameterDescription actualInput = actual.GetInputParameterDescription(i);
            Assert.AreEqual(expectedInput.SemanticName, actualInput.SemanticName);
            Assert.AreEqual(expectedInput.SemanticIndex, actualInput.SemanticIndex);
            Assert.AreEqual(expectedInput.Register, actualInput.Register);
            Assert.AreEqual(expectedInput.UsageMask, actualInput.UsageMask);
            Assert.AreEqual(expectedInput.ComponentType, actualInput.ComponentType);
        }
    }
}
