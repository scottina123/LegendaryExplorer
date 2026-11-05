using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class TextureFileActionsTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow(512, 512, 256, 256, true)]
    [DataRow(1024, 512, 512, 256, true)]
    [DataRow(512, 1024, 256, 1024, false)]
    [DataRow(1024, 512, 1024, 256, false)]
    [DataRow(0, 512, 512, 512, false)]
    [DataRow(512, 0, 512, 512, false)]
    [DataRow(512, 512, 512, 0, false)]
    [DataRow(2147483647, 2147483647, 2147483647, 2147483647, true)]
    public void TextureReplacementRequiresMatchingAspectRatio(int width, int height, int originalWidth, int originalHeight, bool expected)
    {
        var method = typeof(TextureViewerExportLoader).Assembly
            .GetType("LegendaryExplorer.UserControls.ExportLoaderControls.TextureViewer.TextureFileActions")!
            .GetMethod("HasSameAspectRatio", BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.AreEqual(expected, method.Invoke(null, [width, height, originalWidth, originalHeight]));
    }

    [TestMethod]
    [DataRow("Texture2D", 8, false)]
    [DataRow("Texture2D", 8, true)]
    [DataRow("LightMapTexture2D", 1, false)]
    [DataRow("ShadowMapTexture2D", 1, false)]
    public void MoveButtonCopiesCookedMipsLosslessly(string className, int mipCount, bool external)
    {
        string directory = Path.Combine(Path.GetTempPath(), "TextureFileActions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var package = MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(directory, "Test.pcc"), MEGame.LE3);
            var export = package.CreateExport("TestTexture", className, indexed: false);
            export.WriteProperties([
                new EnumProperty(className == "Texture2D" ? "PF_DXT5" : "PF_G8", "EPixelFormat", package.Game, "Format"),
                new IntProperty(128, "SizeX"), new IntProperty(128, "SizeY"),
                new IntProperty(3, "MipTailBaseIdx"), new BoolProperty(true, "NeverStream")
            ]);
            UTexture2D binary = className == "LightMapTexture2D"
                ? new LightMapTexture2D { LightMapFlags = ELightMapFlags.LMF_None }
                : new UTexture2D();
            binary.TextureGuid = Guid.NewGuid();
            binary.Mips = Enumerable.Range(0, mipCount).Select(i =>
                new UTexture2D.Texture2DMipMap(Enumerable.Repeat((byte)(19 + i), Math.Max(16, (128 >> i) * (128 >> i))).ToArray(), 128 >> i, 128 >> i)).ToList();
            byte[][] expectedMips = binary.Mips.Select(m => m.Mip.ToArray()).ToArray();
            byte[] sourceBytes = [];
            string sourcePath = Path.Combine(directory, "Textures_Source.tfc");
            if (external)
            {
                using var source = new MemoryStream();
                source.WriteGuid(Guid.NewGuid());
                for (int i = 0; i < 2; i++)
                {
                    var mip = binary.Mips[i];
                    // Opaque cooked bytes: a cache move must not invoke an Oodle or pixel decoder.
                    byte[] cooked = Enumerable.Repeat((byte)(171 + i), 37 + i).ToArray();
                    mip.DataOffset = checked((int)source.Position);
                    source.Write(cooked);
                    mip.StorageType = StorageTypes.extOodle;
                    mip.CompressedSize = cooked.Length;
                    mip.Mip = [];
                    expectedMips[i] = cooked;
                }
                sourceBytes = source.ToArray();
                File.WriteAllBytes(sourcePath, sourceBytes);
                export.WriteProperty(new NameProperty("Textures_Source", "TextureFileCacheName"));
            }
            export.WriteBinary(binary);
            string destination = Path.Combine(directory, "Textures_Destination.tfc");
            byte[] prefix = Guid.NewGuid().ToByteArray().Concat(new byte[] { 77, 81, 99 }).ToArray();
            File.WriteAllBytes(destination, prefix);

            MoveMethod.Invoke(null, [export, "Textures_Destination"]);

            int movedCount = Math.Max(1, mipCount - 6);
            CollectionAssert.AreEqual(prefix.Concat(expectedMips.Take(movedCount).SelectMany(m => m)).ToArray(), File.ReadAllBytes(destination));
            Assert.AreEqual(new Guid(prefix.AsSpan(0, 16)), CommonStructs.GetGuid(export.GetProperty<StructProperty>("TFCFileGuid")));
            Assert.AreEqual("Textures_Destination", export.GetProperty<NameProperty>("TextureFileCacheName").Value.Name);
            Assert.AreEqual(3, export.GetProperty<IntProperty>("MipTailBaseIdx").Value);
            var moved = (UTexture2D)ObjectBinary.From(export);
            Assert.AreEqual(binary.TextureGuid, moved.TextureGuid);
            Assert.AreEqual(mipCount, moved.Mips.Count);
            int offset = prefix.Length;
            for (int i = 0; i < mipCount; i++)
            {
                Assert.AreEqual(binary.Mips[i].SizeX, moved.Mips[i].SizeX);
                Assert.AreEqual(binary.Mips[i].SizeY, moved.Mips[i].SizeY);
                Assert.AreEqual(binary.Mips[i].UncompressedSize, moved.Mips[i].UncompressedSize);
                Assert.AreEqual(binary.Mips[i].CompressedSize, moved.Mips[i].CompressedSize);
                if (i < movedCount)
                {
                    Assert.AreEqual(offset, moved.Mips[i].DataOffset);
                    Assert.AreEqual(binary.Mips[i].StorageType | (StorageTypes)StorageFlags.externalFile, moved.Mips[i].StorageType);
                    offset += expectedMips[i].Length;
                }
                else CollectionAssert.AreEqual(expectedMips[i], moved.Mips[i].Mip);
            }
            if (className != "Texture2D") Assert.IsTrue(export.GetProperty<BoolProperty>("NeverStream").Value);
            if (moved is LightMapTexture2D lightMap) Assert.AreEqual(ELightMapFlags.LMF_None, lightMap.LightMapFlags);
            if (external) CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(sourcePath));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void MoveButtonDoesNotFallBackToResidentMipsWhenTheCacheIsMissing()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TextureFileActions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var package = MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(directory, "Test.pcc"), MEGame.LE3);
            var export = package.CreateExport("TestTexture", "Texture2D", indexed: false);
            export.WriteProperty(new NameProperty("Textures_Missing_" + Guid.NewGuid().ToString("N"), "TextureFileCacheName"));
            export.WriteBinary(new UTexture2D
            {
                TextureGuid = Guid.NewGuid(),
                Mips = [new UTexture2D.Texture2DMipMap { StorageType = StorageTypes.extOodle, UncompressedSize = 16384, CompressedSize = 37, DataOffset = 16, SizeX = 128, SizeY = 128, Mip = [] },
                    new UTexture2D.Texture2DMipMap(new byte[16], 4, 4)]
            });
            byte[] original = export.Data;

            Assert.ThrowsExactly<TargetInvocationException>(() => MoveMethod.Invoke(null, [export, "Textures_Destination"]));

            CollectionAssert.AreEqual(original, export.Data);
            Assert.IsFalse(File.Exists(Path.Combine(directory, "Textures_Destination.tfc")));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static MethodInfo MoveMethod => typeof(TextureViewerExportLoader).Assembly
        .GetType("LegendaryExplorer.UserControls.ExportLoaderControls.TextureViewer.TextureFileActions")!
        .GetMethod("MoveToTfc", BindingFlags.Static | BindingFlags.NonPublic)!;
}
