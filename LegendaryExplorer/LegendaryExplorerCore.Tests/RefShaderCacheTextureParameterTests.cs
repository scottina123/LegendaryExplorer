using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Gammtek.IO;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class RefShaderCacheTextureParameterTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void ReadsAllMaterialMapsAndSkipsMalformedShaderBytecode(MEGame game)
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"RefTextureParameters-{Guid.NewGuid()}.upk");
        try
        {
            WriteFixture(filePath, game, corruptShaderBytecodeSize: true);
            var names = RefShaderCacheReader.GetTextureParameterNamesFromFile(game, filePath);

            CollectionAssert.AreEquivalent(new[] { "DIFFUSE", "REFLECTION", "TEXTURE_2", "TEXTURE_2", "NESTEDTEXTURE", "NONE" },
                names.Select(name => name.Instanced.ToUpperInvariant()).ToArray());
            Assert.IsTrue(names.Contains(new NameReference("Texture_2")), "Literal suffixes must remain literal FNames.");
            Assert.IsTrue(names.Contains(new NameReference("Texture", 3)), "Numbered FNames must retain their instance numbers.");
            Assert.IsFalse(names.Any(name => name.Name == "ArbitraryPackageName" || name.Name == "ScalarOnly"));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [TestMethod]
    public void HonorsGamePathOverridesWithoutChangingSharedOffsets()
    {
        string gameRoot = Path.Combine(Path.GetTempPath(), $"TextureParameterGame-{Guid.NewGuid()}");
        string cookedPath = MEDirectories.GetCookedPath(MEGame.LE3, gameRoot);
        Directory.CreateDirectory(cookedPath);
        string filePath = Path.Combine(cookedPath, RefShaderCacheReader.ShaderCacheName(MEGame.LE3));
        try
        {
            WriteFixture(filePath, MEGame.LE3);
            CollectionAssert.AreEquivalent(
                RefShaderCacheReader.GetTextureParameterNamesFromFile(MEGame.LE3, filePath).ToArray(),
                RefShaderCacheReader.GetTextureParameterNames(MEGame.LE3, gameRoot).ToArray());
            File.Delete(filePath);
            Assert.HasCount(0, RefShaderCacheReader.GetTextureParameterNames(MEGame.LE3, gameRoot));
        }
        finally
        {
            File.Delete(filePath);
            Directory.Delete(cookedPath);
            Directory.Delete(Path.GetDirectoryName(cookedPath));
            Directory.Delete(gameRoot);
        }
    }

    [TestMethod]
    [DataRow(MEGame.ME1)]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.UDK)]
    [DataRow(MEGame.Unknown)]
    public void GamesWithoutNamedGlobalUniformsReturnEmpty(MEGame game)
    {
        Assert.HasCount(0, RefShaderCacheReader.GetTextureParameterNames(game));
    }

    private static void WriteFixture(string filePath, MEGame game, bool corruptShaderBytecodeSize = false)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage(filePath, game);
        package.FindNameOrAdd("ArbitraryPackageName");
        var export = package.CreateExport("SeekFreeShaderCache", "ShaderCache", indexed: false);
        var cache = ShaderCache.Create();
        var shader = new FNULLPixelShader
        {
            ShaderType = "FNULLPixelShader",
            Guid = Guid.NewGuid(),
            ShaderByteCode = new byte[65536],
            Platform = game.IsLEGame() ? (byte)5 : (byte)0,
            Frequency = Shader.ShaderFrequency.Pixel
        };
        cache.Shaders.Add(shader.Guid, shader);
        var first = CreateMap();
        first.Uniform2DTextureExpressions = [Texture("Diffuse"), Texture(new NameReference("Texture_2"))];
        first.UniformCubeTextureExpressions = [Texture("Reflection")];
        first.UniformPixelScalarExpressions =
        [
            new MaterialUniformExpressionScalarParameter { ExpressionType = "FMaterialUniformExpressionScalarParameter", ParameterName = "ScalarOnly" },
            new MaterialUniformExpressionClamp
            {
                ExpressionType = "FMaterialUniformExpressionClamp",
                Input = new MaterialUniformExpressionAbs { ExpressionType = "FMaterialUniformExpressionAbs", X = Texture("NestedTexture") },
                Min = new MaterialUniformExpressionMax { ExpressionType = "FMaterialUniformExpressionMax", A = Texture("DIFFUSE"), B = Texture(NameReference.None) },
                Max = Texture("NestedTexture")
            }
        ];
        var second = CreateMap();
        second.Uniform2DTextureExpressions = [Texture(new NameReference("Texture", 3)), Texture("diffuse")];
        cache.MaterialShaderMaps.Add(first.StaticParameters, first);
        cache.MaterialShaderMaps.Add(second.StaticParameters, second);
        export.WriteBinary(cache);
        using var saved = package.SaveToStream(false);
        if (corruptShaderBytecodeSize)
        {
            // Corrupt a length that any full Shader deserializer must read. The bulk parameter
            // reader must seek directly to the shader's valid end offset and still read both maps.
            saved.Position = export.DataOffset + 12 + 1;
            int count = saved.ReadInt32();
            saved.Skip(count * 12);
            count = saved.ReadInt32();
            saved.Skip(count * 12);
            Assert.AreEqual(1, saved.ReadInt32());
            saved.Skip(30); // FName, GUID, end offset, platform, frequency
            saved.WriteInt32(int.MaxValue);
        }
        File.WriteAllBytes(filePath, saved.ToArray());
    }

    private static MaterialShaderMap CreateMap()
    {
        Guid id = Guid.NewGuid();
        return new MaterialShaderMap
        {
            ID = id,
            FriendlyName = "Texture parameter fixture",
            StaticParameters = (StaticParameterSet)id,
            Shaders = [],
            MeshShaderMaps = [],
            UniformPixelScalarExpressions = [],
            UniformPixelVectorExpressions = [],
            UniformVertexScalarExpressions = [],
            UniformVertexVectorExpressions = [],
            Uniform2DTextureExpressions = [],
            UniformCubeTextureExpressions = []
        };
    }

    private static MaterialUniformExpressionTextureParameter Texture(NameReference name) => new()
    {
        ParameterName = name,
        ExpressionType = "FMaterialUniformExpressionTextureParameter"
    };
}
