using System;
using System.IO;
using System.Linq;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Textures;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Classes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class TextureCacheMoverTests
{
    [TestMethod]
    public void MovePreservesCookedMipBytesMetadataAndExistingCache()
    {
        GlobalTest.Init();
        string directory = NewDirectory();
        try
        {
            using var package = MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(directory, "Move.pcc"), MEGame.ME3);
            var export = CreateTexture(package, "Texture2D");
            var binary = (UTexture2D)ObjectBinary.From(export);
            binary.Mips.Insert(0, new UTexture2D.Texture2DMipMap { StorageType = StorageTypes.empty, SizeX = 2048, SizeY = 2048, Mip = [] });
            // A cooked mip chain with deliberately independent lower mip contents.
            var expected = binary.Mips.Skip(1).Select(m => m.Mip.ToArray()).ToArray();
            var compressed = TextureCompression.CompressTexture(expected[0], StorageTypes.pccZlib);
            binary.Mips[1].StorageType = StorageTypes.pccZlib;
            binary.Mips[1].Mip = compressed;
            binary.Mips[1].CompressedSize = compressed.Length;
            export.WriteBinary(binary);
            Guid textureGuid = binary.TextureGuid;
            byte[] prefix = Guid.NewGuid().ToByteArray().Concat(new byte[] { 42, 51, 19, 211 }).ToArray();
            string cache = Path.Combine(directory, "Textures_DLC_MOD_Test.tfc");
            File.WriteAllBytes(cache, prefix);

            Assert.AreEqual(2, TextureCacheMover.MoveToCache(export, cache));

            byte[] after = File.ReadAllBytes(cache);
            CollectionAssert.AreEqual(prefix, after.Take(prefix.Length).ToArray());
            CollectionAssert.AreEqual(compressed, after.Skip(prefix.Length).Take(compressed.Length).ToArray());
            Assert.AreEqual(new Guid(prefix.AsSpan(0, 16)), CommonStructs.GetGuid(export.GetProperty<StructProperty>("TFCFileGuid")));
            Assert.AreEqual("Textures_DLC_MOD_Test", export.GetProperty<NameProperty>("TextureFileCacheName").Value.Name);
            var moved = (UTexture2D)ObjectBinary.From(export);
            Assert.AreEqual(textureGuid, moved.TextureGuid);
            Assert.AreEqual(binary.Unk1, moved.Unk1);
            Assert.AreEqual(9, moved.Mips.Count);
            Assert.IsTrue(moved.Mips[0].IsEmpty);
            Assert.AreEqual(StorageTypes.extZlib, moved.Mips[1].StorageType);
            Assert.AreEqual(StorageTypes.extUnc, moved.Mips[2].StorageType);
            Assert.AreEqual(prefix.Length, moved.Mips[1].DataOffset);
            Assert.AreEqual(prefix.Length + compressed.Length, moved.Mips[2].DataOffset);
            Assert.IsNull(export.GetProperty<BoolProperty>("NeverStream"));
            AssertMetadata(export);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(binary.Mips[i + 1].SizeX, moved.Mips[i + 1].SizeX);
                Assert.AreEqual(binary.Mips[i + 1].SizeY, moved.Mips[i + 1].SizeY);
                var mip = new Texture2D(export).Mips[i + 1];
                CollectionAssert.AreEqual(expected[i], Texture2D.GetTextureData(mip, package.Game));
                if (i >= 2) Assert.IsTrue(moved.Mips[i + 1].IsLocallyStored);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow("ShadowMapTexture2D", 1, MEGame.ME3)]
    [DataRow("ShadowMapTexture2D", 4, MEGame.ME3)]
    [DataRow("ShadowMapTexture2D", 8, MEGame.ME3)]
    [DataRow("LightMapTexture2D", 1, MEGame.ME3)]
    [DataRow("LightMapTexture2D", 4, MEGame.ME3)]
    [DataRow("LightMapTexture2D", 8, MEGame.ME3)]
    [DataRow("ShadowMapTexture2D", 1, MEGame.LE3)]
    [DataRow("ShadowMapTexture2D", 4, MEGame.LE3)]
    [DataRow("ShadowMapTexture2D", 8, MEGame.LE3)]
    [DataRow("LightMapTexture2D", 1, MEGame.LE3)]
    [DataRow("LightMapTexture2D", 4, MEGame.LE3)]
    [DataRow("LightMapTexture2D", 8, MEGame.LE3)]
    public void PackageStoredBakedLightingMovesLosslesslyIncludingSingleMipMaps(string className, int mipCount, MEGame game)
    {
        GlobalTest.Init();
        string directory = NewDirectory();
        try
        {
            string packagePath = Path.Combine(directory, "Lighting.pcc");
            using var package = MEPackageHandler.CreateMemoryEmptyPackage(packagePath, game);
            var export = CreateTexture(package, className);
            var binary = (UTexture2D)ObjectBinary.From(export);
            binary.Mips = binary.Mips.Take(mipCount).ToList();
            if (binary is LightMapTexture2D originalLightMap) originalLightMap.LightMapFlags = ELightMapFlags.LMF_None;
            export.WriteBinary(binary);
            // Cover explicit true/false and the absence of a NeverStream override.
            if (mipCount == 4) export.RemoveProperty("NeverStream");
            if (mipCount == 8) export.WriteProperty(new BoolProperty(false, "NeverStream"));
            byte[][] expected = binary.Mips.Select(m => m.Mip.ToArray()).ToArray();
            byte[] prefix = Guid.NewGuid().ToByteArray().Concat(new byte[] { 17, 91, 203 }).ToArray();
            string cache = Path.Combine(directory, "Textures_Test.tfc");
            File.WriteAllBytes(cache, prefix);
            int movedCount = Math.Max(1, mipCount - 6);

            Assert.AreEqual(movedCount, TextureCacheMover.MoveToCache(export, cache));

            // Verify the saved PCC, not just the modified in-memory mip objects.
            using var saved = package.SaveToStream(false);
            saved.Position = 0;
            using var reopened = MEPackageHandler.OpenMEPackageFromStream(saved, packagePath);
            var savedExport = reopened.GetUExport(export.UIndex);
            var moved = (UTexture2D)ObjectBinary.From(savedExport);
            Assert.AreEqual(mipCount, moved.Mips.Count);
            Assert.AreEqual(binary.TextureGuid, moved.TextureGuid);
            Assert.AreEqual(binary.Unk1, moved.Unk1);
            Assert.AreEqual("Textures_Test", savedExport.GetProperty<NameProperty>("TextureFileCacheName").Value.Name);
            Assert.AreEqual(new Guid(prefix.AsSpan(0, 16)), CommonStructs.GetGuid(savedExport.GetProperty<StructProperty>("TFCFileGuid")));
            CollectionAssert.AreEqual(prefix, File.ReadAllBytes(cache).Take(prefix.Length).ToArray());
            Assert.AreEqual(prefix.Length + expected.Take(movedCount).Sum(m => m.Length), new FileInfo(cache).Length);
            if (mipCount == 4) Assert.IsNull(savedExport.GetProperty<BoolProperty>("NeverStream"));
            else Assert.AreEqual(mipCount == 1, savedExport.GetProperty<BoolProperty>("NeverStream").Value);
            if (moved is LightMapTexture2D lightMap) Assert.AreEqual(ELightMapFlags.LMF_None, lightMap.LightMapFlags);
            AssertMetadata(savedExport);
            int offset = prefix.Length;
            var mipInfos = new Texture2D(savedExport).Mips;
            for (int i = 0; i < mipCount; i++)
            {
                Assert.AreEqual(binary.Mips[i].SizeX, moved.Mips[i].SizeX);
                Assert.AreEqual(binary.Mips[i].SizeY, moved.Mips[i].SizeY);
                Assert.AreEqual(binary.Mips[i].UncompressedSize, moved.Mips[i].UncompressedSize);
                Assert.AreEqual(binary.Mips[i].CompressedSize, moved.Mips[i].CompressedSize);
                Assert.AreEqual(i >= movedCount, moved.Mips[i].IsLocallyStored);
                if (i < movedCount)
                {
                    Assert.AreEqual(offset, moved.Mips[i].DataOffset);
                    offset += expected[i].Length;
                }
                CollectionAssert.AreEqual(expected[i], Texture2D.GetTextureData(mipInfos[i], game));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow("ShadowMapTexture2D")]
    [DataRow("LightMapTexture2D")]
    public void ExistingExternalLightingRelocatesWithoutChangingResidentMipsOrStreamingFlags(string className)
    {
        GlobalTest.Init();
        string directory = NewDirectory();
        try
        {
            using var package = MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(directory, "Lighting.pcc"), MEGame.ME3);
            var export = CreateTexture(package, className);
            var binary = (UTexture2D)ObjectBinary.From(export);
            byte[] payload = binary.Mips[0].Mip;
            string source = Path.Combine(directory, "Textures_Source.tfc");
            File.WriteAllBytes(source, Guid.NewGuid().ToByteArray().Concat(payload).ToArray());
            binary.Mips[0].StorageType = StorageTypes.extUnc;
            binary.Mips[0].DataOffset = 16;
            binary.Mips[0].Mip = [];
            export.WriteBinary(binary);
            export.WriteProperty(new NameProperty("Textures_Source", "TextureFileCacheName"));
            string target = Path.Combine(directory, "Textures_Target.tfc");

            Assert.AreEqual(1, TextureCacheMover.MoveToCache(export, target));

            CollectionAssert.AreEqual(payload, File.ReadAllBytes(target).Skip(16).ToArray());
            Assert.IsTrue(export.GetProperty<BoolProperty>("NeverStream").Value);
            var moved = (UTexture2D)ObjectBinary.From(export);
            Assert.AreEqual(binary.TextureGuid, moved.TextureGuid);
            for (int i = 1; i < moved.Mips.Count; i++)
            {
                Assert.AreEqual(binary.Mips[i].StorageType, moved.Mips[i].StorageType);
                CollectionAssert.AreEqual(binary.Mips[i].Mip, moved.Mips[i].Mip);
            }
            if (moved is LightMapTexture2D lightMap)
                Assert.AreEqual(ELightMapFlags.LMF_Streamed | ELightMapFlags.LMF_SimpleLightmap, lightMap.LightMapFlags);
            AssertMetadata(export);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    public void InvalidCacheDoesNotModifyTheExportOrCache(int size)
    {
        GlobalTest.Init();
        string directory = NewDirectory();
        try
        {
            using var package = MEPackageHandler.CreateMemoryEmptyPackage("Move.pcc", MEGame.ME3);
            var export = CreateTexture(package, "Texture2D");
            byte[] original = export.Data;
            string cache = Path.Combine(directory, "Textures_Test.tfc");
            byte[] badHeader = new byte[] { 1, 2, 3 }.Take(size).ToArray();
            File.WriteAllBytes(cache, badHeader);
            Assert.ThrowsExactly<InvalidDataException>(() => TextureCacheMover.MoveToCache(export, cache));
            CollectionAssert.AreEqual(original, export.Data);
            CollectionAssert.AreEqual(badHeader, File.ReadAllBytes(cache));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static ExportEntry CreateTexture(IMEPackage package, string className)
    {
        var export = package.CreateExport("TestTexture", className, indexed: false);
        export.WriteProperties([
            new EnumProperty("PF_G8", "EPixelFormat", package.Game, "Format"),
            new BoolProperty(false, "SRGB"), new BoolProperty(true, "NeverStream"),
            new IntProperty(1024, "SizeX"), new IntProperty(1024, "SizeY"),
            new IntProperty(3, "MipTailBaseIdx"), new IntProperty(-2, "InternalFormatLodBias")
        ]);
        UTexture2D binary = className == "LightMapTexture2D"
            ? new LightMapTexture2D { LightMapFlags = ELightMapFlags.LMF_Streamed | ELightMapFlags.LMF_SimpleLightmap }
            : new UTexture2D();
        binary.TextureGuid = Guid.NewGuid();
        binary.Unk1 = 99;
        binary.Mips = Enumerable.Range(0, 8).Select(i =>
            new UTexture2D.Texture2DMipMap(Enumerable.Repeat((byte)(17 + i * 29), (1024 >> i) * (1024 >> i)).ToArray(), 1024 >> i, 1024 >> i)).ToList();
        export.WriteBinary(binary);
        return export;
    }

    private static void AssertMetadata(ExportEntry export)
    {
        Assert.AreEqual("PF_G8", export.GetProperty<EnumProperty>("Format").Value.Name);
        Assert.IsFalse(export.GetProperty<BoolProperty>("SRGB").Value);
        Assert.AreEqual(3, export.GetProperty<IntProperty>("MipTailBaseIdx").Value);
        Assert.AreEqual(-2, export.GetProperty<IntProperty>("InternalFormatLodBias").Value);
        Assert.AreEqual(1024, export.GetProperty<IntProperty>("SizeX").Value);
        Assert.AreEqual(1024, export.GetProperty<IntProperty>("SizeY").Value);
    }

    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TextureCacheMover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
