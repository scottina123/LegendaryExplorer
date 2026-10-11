using System;
using System.Reflection;
using System.Runtime.InteropServices;
using LegendaryExplorer.Tools.AssetDatabase.VFXPreview;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Color = System.Windows.Media.Color;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class SharedRendererCompatibilityTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SharedAndVfxContextsRenderOffscreenWithCompatibleTargetsAndDisplayBackground(bool vfxPreview)
    {
        MeshRenderContext context = vfxPreview
            ? new VfxPreviewRenderContext { ShowAxis = false, ShowGrid = false, ShowOrigin = false }
            : new MeshRenderContext();
        context.BackgroundColor = Color.FromRgb(28, 70, 150);
        context.ShowCameraPositionInStatsOverlay = false;
        Texture2D backbuffer = null;
        bool sizeResourcesCreated = false;
        try
        {
            context.CreateResources();
            backbuffer = new Texture2D(context.Device, new Texture2DDescription
            {
                Width = 32,
                Height = 32,
                ArraySize = 1,
                MipLevels = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                Usage = ResourceUsage.Default
            });
            context.CreateSizeDependentResources(32, 32, backbuffer);
            sizeResourcesCreated = true;
            AssertCompatibleSamples(context, "SceneColor");
            AssertCompatibleSamples(context, "HitBufferMS");

            if (context is VfxPreviewRenderContext vfx)
            {
                Assert.AreEqual(1, context.SampleCount,
                    "VFX's custom Hable scene buffer must share the depth and hit targets' sample count.");
                Assert.IsTrue(context.UseGameShaders);
                Assert.AreEqual(ViewportLightingMode.Preview, context.LightingMode);
                Assert.AreSame(context.BlackTexView, vfx.PreviewSceneDepthTextureView);
                Assert.AreEqual(Comparison.GreaterEqual, vfx.GetVfxDepthState(true, true).Description.DepthComparison);
                Assert.AreEqual(Comparison.GreaterEqual, vfx.GetVfxDepthState(true, false).Description.DepthComparison);
                Assert.AreEqual(DepthWriteMask.All, vfx.GetVfxDepthState(true, true).Description.DepthWriteMask);
                Assert.AreEqual(DepthWriteMask.Zero, vfx.GetVfxDepthState(true, false).Description.DepthWriteMask);
                vfx.UseGameShader = false;
                Assert.IsFalse(context.UseGameShaders);
                vfx.UseGameShader = true;
                Assert.IsTrue(context.UseGameShaders);
            }

            context.Render();
            Assert.IsNull(context.ErrorText, context.ErrorText);
            byte[] pixel = ReadPixel(context, backbuffer, 1, 31);
            Assert.AreEqual(150, pixel[0], 1, "Blue background must survive the context's display transfer.");
            Assert.AreEqual(70, pixel[1], 1, "Green background must survive the context's display transfer.");
            Assert.AreEqual(28, pixel[2], 1, "Red background must survive the context's display transfer.");

            // Feed the active scene target directly, so this verifies each context's resolve routing
            // without depending on cooked material availability. Alpha 2 marks a native game surface.
            context.RenderScene += (_, _) =>
            {
                using RenderTargetView activeSceneTarget = context.ImmediateContext.OutputMerger.GetRenderTargets(1)[0];
                context.ImmediateContext.ClearRenderTargetView(activeSceneTarget, new RawColor4(4, 2, 0.5f, 2));
            };
            context.Render();
            Assert.IsNull(context.ErrorText, context.ErrorText);
            pixel = ReadPixel(context, backbuffer, 1, 31);
            byte[] expectedHdr = vfxPreview ? [115, 186, 220] : [215, 247, 252];
            for (int channel = 0; channel < expectedHdr.Length; channel++)
            {
                Assert.AreEqual(expectedHdr[channel], pixel[channel], 1,
                    "Shared previews use the LE3 display transfer, while VFX retains its existing Hable transfer.");
            }
        }
        finally
        {
            context.ImmediateContext?.ClearState();
            if (sizeResourcesCreated)
            {
                context.DisposeSizeDependentResources();
                backbuffer = null; // Owned and disposed by the context after a successful size setup.
            }
            backbuffer?.Dispose();
            if (context.IsReady)
            {
                context.DisposeResources();
            }
        }
    }

    private static void AssertCompatibleSamples(MeshRenderContext context, string fieldName)
    {
        var target = (Texture2D)typeof(MeshRenderContext)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
        Assert.AreEqual(context.SampleCount, target.Description.SampleDescription.Count, fieldName);
        Assert.AreEqual(context.DepthBuffer.Description.SampleDescription, target.Description.SampleDescription,
            $"{fieldName} and depth must be compatible when bound together.");
    }

    private static byte[] ReadPixel(MeshRenderContext context, Texture2D texture, int x, int y)
    {
        Texture2DDescription description = texture.Description;
        description.BindFlags = BindFlags.None;
        description.Usage = ResourceUsage.Staging;
        description.CpuAccessFlags = CpuAccessFlags.Read;
        description.OptionFlags = ResourceOptionFlags.None;
        using var staging = new Texture2D(context.Device, description);
        context.ImmediateContext.OutputMerger.SetRenderTargets((DepthStencilView)null, Array.Empty<RenderTargetView>());
        context.ImmediateContext.CopyResource(texture, staging);
        var mapped = context.ImmediateContext.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
        try
        {
            var pixel = new byte[4];
            Marshal.Copy(IntPtr.Add(mapped.DataPointer, y * mapped.RowPitch + x * 4), pixel, 0, 4);
            return pixel;
        }
        finally
        {
            context.ImmediateContext.UnmapSubresource(staging, 0);
        }
    }
}
