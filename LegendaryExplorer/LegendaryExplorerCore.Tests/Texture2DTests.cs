using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Memory;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Classes;
using LegendaryExplorerCore.Textures;

namespace LegendaryExplorerCore.Tests
{
    [TestClass]
    public class Texture2DTests
    {
        [TestMethod]
        public void TestTextureOperations()
        {
            GlobalTest.Init();
            var packagesPath = GlobalTest.GetTestTexturesDirectory();
            var packages = Directory.GetFiles(packagesPath, "*.*", SearchOption.AllDirectories);
            foreach (var p in packages)
            {
                if (p.RepresentsPackageFilePath())
                {
                    // Do not use package caching in tests
                    Console.WriteLine($"Opening package {p}");
                    (var game, var platform) = GlobalTest.GetExpectedTypes(p);
                    if (platform == MEPackage.GamePlatform.PC)
                    {
                        var loadedPackage = MEPackageHandler.OpenMEPackage(p, forceLoadFromDisk: true);
                        foreach (var textureExp in loadedPackage.Exports.Where(x => x.IsTexture()))
                        {
                            Texture2D.GetTextureCRC(textureExp);

                            var t2d = new Texture2D(textureExp);
                            var mips = Texture2D.GetTexture2DMipInfos(textureExp, t2d.GetTopMip().TextureCacheName);
                            foreach (var v in t2d.Mips)
                            {
                                var displayStr = v.MipDisplayString;
                                var texCache = v.TextureCacheName;
                                var textureData = Texture2D.GetTextureData(v, v.Export.Game);
                                var imageDataFromInternal = t2d.GetImageBytesForMip(v, v.Export.Game, false, out _);
                                if (!textureData.AsSpan().SequenceEqual(imageDataFromInternal))
                                {
                                    Assert.Fail($"Texture data accessed using wrapper and internal method did not match! Export: {textureExp.InstancedFullPath} in {p}. Static size: {textureData.Length} Instance size: {imageDataFromInternal.Length}");
                                }
                            }
                            t2d.RemoveEmptyMipsFromMipList();
                            using MemoryStream ms = MemoryManager.GetMemoryStream();
                            t2d.SerializeNewData(ms);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Unit Test for the CalculateStorageType method of Texture2D
        /// </summary>
        [TestMethod]
        public void TestStorageTypeDetermination()
        {
            // Empty and LZMA always return the same
            Assert.AreEqual(Texture2D.CalculateStorageType(StorageTypes.empty, MEGame.ME3, false), StorageTypes.empty);
            Assert.AreEqual(Texture2D.CalculateStorageType(StorageTypes.empty, MEGame.ME3, true), StorageTypes.empty);
            Assert.AreEqual(Texture2D.CalculateStorageType(StorageTypes.extLZMA, MEGame.ME3, false), StorageTypes.extLZMA);

            // ME3 - Following storage types should become Zlib
            Assert.AreEqual(Texture2D.CalculateStorageType(StorageTypes.extLZO, MEGame.ME3, false), StorageTypes.extZlib);
            Assert.AreEqual(Texture2D.CalculateStorageType(StorageTypes.extUnc, MEGame.ME3, false), StorageTypes.extZlib);
            Assert.AreEqual(Texture2D.CalculateStorageType(StorageTypes.pccLZO, MEGame.ME3, false), StorageTypes.extZlib);
            Assert.AreEqual(Texture2D.CalculateStorageType(StorageTypes.pccUnc, MEGame.ME3, false), StorageTypes.extZlib);

            // ME2 - Following storage types should become LZO
            Assert.AreEqual(Texture2D.CalculateStorageType(StorageTypes.extZlib, MEGame.ME2, false), StorageTypes.extLZO);
            Assert.AreEqual(Texture2D.CalculateStorageType(StorageTypes.extUnc, MEGame.ME2, false), StorageTypes.extLZO);
            Assert.AreEqual(Texture2D.CalculateStorageType(StorageTypes.pccZlib, MEGame.ME2, false), StorageTypes.extLZO);
            Assert.AreEqual(Texture2D.CalculateStorageType(StorageTypes.pccUnc, MEGame.ME2, false), StorageTypes.extLZO);

            // LE - Following storage types should become Oodle
            Assert.AreEqual(Texture2D.CalculateStorageType(StorageTypes.extUnc, MEGame.LE3, false), StorageTypes.extOodle);
            Assert.AreEqual(Texture2D.CalculateStorageType(StorageTypes.pccUnc, MEGame.LE3, false), StorageTypes.extOodle);

            var pccTypes = new List<StorageTypes>()
                { StorageTypes.pccOodle, StorageTypes.pccUnc, StorageTypes.pccZlib, StorageTypes.pccLZO };
            
            var extTypes = new List<StorageTypes>()
                { StorageTypes.extOodle, StorageTypes.extUnc, StorageTypes.extZlib, StorageTypes.extLZO }; // extLZMA not included - only for console games
            
            // All ext types should become pcc when isPackageStored is true
            foreach (var t in extTypes)
            {
                Assert.IsTrue(pccTypes.Contains(Texture2D.CalculateStorageType(t, MEGame.LE3, true)));
            }
            
            // All pcc types should become ext types when isPackageStored is false
            foreach (var t in pccTypes)
            {
                Assert.IsTrue(extTypes.Contains(Texture2D.CalculateStorageType(t, MEGame.LE3, false)));
            }
        }

        [TestMethod]
        public void Replace_AppendsMultipleTexturesThroughSharedTfcStream()
        {
            GlobalTest.Init();
            using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("SharedTfcStream.pcc", MEGame.ME3);
            Guid cacheGuid = Guid.NewGuid();
            using var cacheStream = new MemoryStream();
            cacheStream.WriteGuid(cacheGuid);

            ExportEntry first = CreateTexture("LightMap_0");
            Replace(first);
            long secondOffset = cacheStream.Length;
            ExportEntry second = CreateTexture("LightMap_1");
            Replace(second);

            UTexture2D firstBinary = first.GetBinaryData<UTexture2D>();
            UTexture2D secondBinary = second.GetBinaryData<UTexture2D>();
            Assert.AreEqual(16, firstBinary.Mips[0].DataOffset);
            Assert.AreEqual(checked((int)secondOffset), secondBinary.Mips[0].DataOffset);
            Assert.IsTrue(cacheStream.Length > secondOffset);
            Assert.AreEqual(cacheGuid, CommonStructs.GetGuid(first.GetProperty<StructProperty>("TFCFileGuid")));
            Assert.AreEqual(cacheGuid, CommonStructs.GetGuid(second.GetProperty<StructProperty>("TFCFileGuid")));

            ExportEntry CreateTexture(string name)
            {
                ExportEntry export = package.CreateExport(name, "Texture2D", null, indexed: false);
                export.WriteProperties([
                    new EnumProperty(Image.getEngineFormatType(PixelFormat.DXT1), "EPixelFormat", package.Game,
                        "Format"),
                    new IntProperty(64, "SizeX"),
                    new IntProperty(64, "SizeY")
                ]);
                export.WriteBinary(new UTexture2D
                {
                    Mips = [new UTexture2D.Texture2DMipMap([], 64, 64)],
                    TextureGuid = Guid.NewGuid()
                });
                return export;
            }

            void Replace(ExportEntry export)
            {
                var image = Image.LoadFromRaw(new byte[64 * 64 * 4], PixelFormat.ARGB, 64, 64);
                new Texture2D(export).Replace(image, export.GetProperties(),
                    forcedTFCName: "Textures_DLC_MOD_Shared", forcedNewFormat: PixelFormat.DXT1,
                    forceMipping: true, forcedTFCStream: cacheStream);
            }
        }

        [TestMethod]
        public void GetTextureData_LoadsTfcFromDisabledDlcFolder()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"LEX_Texture2DTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(tempRoot, "BioGame", "CookedPCConsole"));
            string cookedPath = Path.Combine(tempRoot, "BioGame", "DLC", "OFFDLC_MOD_ProjectVariety2", "CookedPCConsole");
            Directory.CreateDirectory(cookedPath);

            string tfcPath = Path.Combine(cookedPath, "Textures_DLC_MOD_ProjectVariety2.tfc");
            byte[] expectedData = [1, 2, 3, 4, 5, 6];
            File.WriteAllBytes(tfcPath, expectedData);

            try
            {
                byte[] textureData = Texture2D.GetTextureData(
                    MEGame.LE3,
                    Array.Empty<byte>(),
                    StorageTypes.extUnc,
                    false,
                    expectedData.Length,
                    expectedData.Length,
                    0,
                    "Textures_DLC_MOD_ProjectVariety2",
                    tempRoot,
                    null,
                    null);

                CollectionAssert.AreEqual(expectedData, textureData);
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
        }
    }
}
