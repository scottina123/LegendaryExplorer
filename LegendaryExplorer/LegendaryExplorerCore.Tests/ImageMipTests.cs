using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegendaryExplorerCore.Textures;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests
{
    [TestClass]
    public class ImageMipTests
    {
        private static readonly PixelFormat[] BlockFormats =
        [
            PixelFormat.DXT1,
            PixelFormat.DXT3,
            PixelFormat.DXT5,
            PixelFormat.ATI2,
            PixelFormat.BC5,
            PixelFormat.BC7
        ];

        [TestInitialize]
        public void Initialize() => GlobalTest.Init();

        /// <summary>
        /// Mips smaller than a 4x4 block must contain compressed pixel data in a whole block.
        /// </summary>
        [TestMethod]
        public void TestSmallMipGeneration()
        {
            foreach (PixelFormat format in BlockFormats)
            {
                Image image = CreateImage(16, 16);
                image.correctMips(format);

                AssertMipChain(image, format, [(16, 16), (8, 8), (4, 4), (2, 2), (1, 1)]);
            }
        }

        /// <summary>
        /// Block padding must also work when only one mip dimension is smaller than four.
        /// </summary>
        [TestMethod]
        public void TestRectangularSmallMipGeneration()
        {
            foreach (PixelFormat format in BlockFormats)
            {
                Image image = CreateImage(16, 8);
                image.correctMips(format);

                AssertMipChain(image, format, [(16, 8), (8, 4), (4, 2), (2, 1), (1, 1)]);
            }
        }

        [TestMethod]
        public void TestAti2DdsRoundTripPreservesSmallMipBlocks()
        {
            Image image = CreateImage(16, 16);
            image.correctMips(PixelFormat.ATI2);

            byte[] dds = image.StoreImageToDDS();
            var reloaded = new Image(new MemoryStream(dds), Image.ImageFormat.DDS);

            AssertMipChain(reloaded, PixelFormat.ATI2, [(16, 16), (8, 8), (4, 4), (2, 2), (1, 1)]);
        }

        [TestMethod]
        public void TestLegacyUndersizedSmallMipsDoNotThrow()
        {
            foreach (PixelFormat format in new[] { PixelFormat.DXT1, PixelFormat.DXT3, PixelFormat.DXT5, PixelFormat.ATI2, PixelFormat.BC5 })
            {
                int w = 1;
                int h = 1;
                byte[] legacyData = new byte[MipMap.getBufferSize(w, h, format)];

                byte[] argb = Image.convertRawToARGB(legacyData, ref w, ref h, format);

                Assert.AreEqual(4, w, $"{format}: fallback width was not block-aligned");
                Assert.AreEqual(4, h, $"{format}: fallback height was not block-aligned");
                Assert.HasCount(4 * 4 * 4, argb, $"{format}: fallback returned the wrong buffer size");
                Assert.IsFalse(argb.Any(b => b != 0), $"{format}: legacy fallback should remain a black image");
            }
        }

        private static Image CreateImage(int width, int height)
        {
            return new Image(
                new List<MipMap> { new(CreateGradientARGB(width, height), width, height, PixelFormat.ARGB) },
                PixelFormat.ARGB);
        }

        private static void AssertMipChain(Image image, PixelFormat format, (int Width, int Height)[] expectedDimensions)
        {
            Assert.HasCount(expectedDimensions.Length, image.mipMaps, $"{format}: unexpected mip count");

            for (int i = 0; i < expectedDimensions.Length; i++)
            {
                MipMap mip = image.mipMaps[i];
                (int expectedWidth, int expectedHeight) = expectedDimensions[i];
                Assert.AreEqual(expectedWidth, mip.origWidth, $"{format}: unexpected mip width at level {i}");
                Assert.AreEqual(expectedHeight, mip.origHeight, $"{format}: unexpected mip height at level {i}");

                int blockWidth = System.Math.Max(4, expectedWidth);
                int blockHeight = System.Math.Max(4, expectedHeight);
                int expectedDataSize = format == PixelFormat.DXT1
                    ? blockWidth * blockHeight / 2
                    : blockWidth * blockHeight;
                Assert.HasCount(expectedDataSize, mip.data, $"{format}: wrong data size for {expectedWidth}x{expectedHeight} mip");
                Assert.IsTrue(mip.data.Any(b => b != 0), $"{format}: {expectedWidth}x{expectedHeight} mip is all zeros");

                int w = mip.width;
                int h = mip.height;
                byte[] argb = Image.convertRawToARGB(mip.data, ref w, ref h, format);
                Assert.HasCount(w * h * 4, argb, $"{format}: decompressed mip has the wrong size");
                Assert.IsTrue(Enumerable.Range(0, argb.Length).Any(pixelByte => pixelByte % 4 != 3 && argb[pixelByte] != 0),
                    $"{format}: decompressed {expectedWidth}x{expectedHeight} mip is all black");
            }
        }

        private static byte[] CreateGradientARGB(int width, int height)
        {
            byte[] data = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    data[i] = (byte)(64 + (x + y) * 4);
                    data[i + 1] = (byte)(255 - x * 8);
                    data[i + 2] = (byte)(64 + y * 8);
                    data[i + 3] = 255;
                }
            }
            return data;
        }
    }
}
