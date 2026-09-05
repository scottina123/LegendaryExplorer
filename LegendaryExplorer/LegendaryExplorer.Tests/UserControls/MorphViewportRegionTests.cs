using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class MorphViewportRegionTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public void FeatureCoverageUsesVertexDeltasAndInheritedBoneMovement()
    {
        MeshBone[] skeleton = [new() { Name = "head", ParentIndex = -1 }, new() { Name = "jaw", ParentIndex = 0 }];
        IReadOnlyDictionary<string, float>[] inherited =
        [
            MorphViewportPicking.IncludeParentWeights(skeleton, new Dictionary<int, float> { [1] = 0.75f }),
            new Dictionary<string, float>(), new Dictionary<string, float>()
        ];
        var weights = MorphViewportRegions.FeatureWeights(3,
            [new() { SourceIdx = 0, PositionDelta = Vector3.UnitX }, new() { SourceIdx = 1, PositionDelta = -Vector3.UnitX },
             new() { SourceIdx = 9, PositionDelta = Vector3.One }],
            [new() { Bone = "head", Offset = Vector3.UnitY * 2 }], inherited);
        CollectionAssert.AreEqual(new[] { 2.5f, 1f, 0f }, weights);
    }

    [TestMethod]
    public void OverviewDistinguishesLocalRegionsAndFocusRevealsOverlappingCoverage()
    {
        float[][] weights = [[1, 1, 1, 1, 0], [1, 0, 0, 0, 0], [0, 0, 1, 0, 0]];
        Color[] colors = [Colors.Red, Colors.Lime, Colors.Blue];
        var overview = MorphViewportRegions.SurfaceColors(5, weights, colors);
        Assert.AreEqual(new Vector4(0, 1, 0, 0.52f), overview[0]);
        Assert.AreEqual(new Vector4(1, 0, 0, 0.52f), overview[1]);
        Assert.AreEqual(new Vector4(0, 0, 1, 0.52f), overview[2]);
        Assert.AreEqual(Vector4.Zero, overview[4]);
        var focus = MorphViewportRegions.SurfaceColors(5, weights, colors, 0);
        Assert.IsTrue(focus.Take(4).All(color => color.X == 1 && color.Y == 0 && color.Z == 0 && color.W > 0));
        Assert.AreEqual(Vector4.Zero, focus[4]);
        Assert.AreEqual(136, Enumerable.Range(0, 136).Select(MorphViewportRegions.RegionColor).Distinct().Count());
    }

    [STATestMethod]
    public void LabelsPersistWithoutHoverAndBottomTogglePreservesSelection()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(BioMorphFaceEditor).Assembly);
        using var editor = new BioMorphFaceEditor();
        var nose = new MorphFeatureEditorItem("NoseWidth", 0.2f, () => Assert.Fail("Labels must not edit the morph")) { HasMorphTarget = true };
        editor.MorphFeatureItems.Add(nose);
        var selection = new MorphViewportMatch { Mode = MorphViewportPickMode.Features, Feature = nose, TargetName = nose.Name };
        editor.SelectedMorphViewportMatch = selection;
        editor.MorphEditorTabs.SelectedIndex = 3;
        object activeTab = editor.MorphEditorTabs.SelectedItem;
        var region = new MorphViewportRegion { Name = "NoseWidth", Number = 1, Brush = Brushes.Coral, Weights = [1] };
        editor.MorphViewportRegionsList.Add(region);
        Assert.AreEqual(Visibility.Visible, editor.MorphRegionCalloutCanvas.Visibility);
        Assert.IsFalse(editor.SceneViewer.IsMouseOver);
        Assert.IsNull(editor.FindName("MorphRegionLegend"));
        DependencyObject parent = editor.MorphRegionLabelsCheckbox;
        while (parent != null && parent is not StatusBar) parent = LogicalTreeHelper.GetParent(parent);
        Assert.IsInstanceOfType<StatusBar>(parent);
        editor.ShowMorphRegionLabels = false;
        Assert.AreEqual(Visibility.Collapsed, editor.MorphRegionCalloutCanvas.Visibility);
        Assert.AreEqual(Visibility.Collapsed, editor.MorphViewportMarkerLabel.Visibility);
        editor.ShowMorphRegionLabels = true;
        Assert.AreEqual(Visibility.Visible, editor.MorphRegionCalloutCanvas.Visibility);
        Assert.AreSame(region, editor.MorphViewportRegionsList.Single());
        Assert.AreSame(selection, editor.SelectedMorphViewportMatch);
        Assert.AreSame(activeTab, editor.MorphEditorTabs.SelectedItem);
        CollectionAssert.AreEqual(new[] { nose }, editor.MatchedMorphFeatureItems.ToArray());
        Assert.IsFalse(editor.HasUnsavedMorphChanges);
    }

    [TestMethod]
    public void AdjacentRegionTrianglesKeepTheirLabelColorsWithoutMixing()
    {
        float[][] weights = [[1, 0.5f, 0.5f, 0], [0, 0, 0, 3]];
        Triangle[] triangles = [new(0, 1, 2), new(1, 3, 2)];
        int[] owners = new int[2];
        var colors = MorphViewportRegions.SurfaceColors(4, weights, [Colors.Red, Colors.Lime], triangles: triangles, owners: owners);
        CollectionAssert.AreEqual(new[] { 0, 1 }, owners);
        Assert.IsTrue(colors.Take(3).All(color => color == new Vector4(1, 0, 0, 0.52f)));
        Assert.IsTrue(colors.Skip(3).All(color => color == new Vector4(0, 1, 0, 0.52f)));
        var focus = MorphViewportRegions.SurfaceColors(4, weights, [Colors.Red, Colors.Lime], 0, triangles);
        Assert.AreEqual(0f, focus[4].W); // Unaffected corner stays transparent in the full influence view.
        Assert.IsTrue(focus[3].W > 0 && focus[5].W > 0);
    }

    [STATestMethod]
    public void MaterialLabelsLoadWithoutMouseMovementAndSelectTheCorrectSectionAtSharedVertices()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(BioMorphFaceEditor).Assembly);
        using var device = new SharpDX.Direct3D11.Device(SharpDX.Direct3D.DriverType.Warp);
        using var editor = new BioMorphFaceEditor();
        var mesh = new Mesh<WorldVertex>(device, [new Triangle(0, 1, 2), new Triangle(1, 3, 2)],
        [
            new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero), new(Vector3.UnitX, Vector3.UnitZ, Vector2.Zero),
            new(Vector3.UnitY, Vector3.UnitZ, Vector2.Zero), new(Vector3.One, Vector3.UnitZ, Vector2.Zero)
        ]);
        var preview = new ModelPreview<WorldVertex>(device, mesh, null, null);
        preview.LODs[0].Sections.Add(new ModelPreviewSection("Skin", 0, 1));
        preview.LODs[0].Sections.Add(new ModelPreviewSection("Lips", 3, 1));
        var rendererType = typeof(MeshRenderer);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var renderContext = editor.MeshContext;
        typeof(LegacyRenderContext).GetProperty(nameof(LegacyRenderContext.Device))!.SetValue(renderContext, device);
        rendererType.GetField("LEXPreview", flags)!.SetValue(editor, preview);
        rendererType.GetProperty(nameof(MeshRenderer.HasMorphEditorData))!.SetValue(editor, true);
        editor.MorphViewportPickMode = MorphViewportPickMode.Materials;
        rendererType.GetMethod("UpdateMorphRegionLabels", flags)!.Invoke(editor, null);
        Assert.IsFalse(editor.SceneViewer.IsMouseOver);
        Assert.HasCount(2, editor.MorphViewportRegionsList);
        var surfaces = (System.Collections.IList)rendererType.GetField("morphRegionSurfaces", flags)!.GetValue(editor)!;
        object surface = surfaces[0]!;
        var overlay = (Mesh<WorldVertex>)surface.GetType().GetField("Overlay", flags)!.GetValue(surface)!;
        Assert.HasCount(6, overlay.Vertices);
        Assert.AreEqual(overlay.Vertices[0].Normal, overlay.Vertices[2].Normal);
        Assert.AreEqual(overlay.Vertices[3].Normal, overlay.Vertices[5].Normal);
        Assert.AreNotEqual(overlay.Vertices[0].Normal, overlay.Vertices[3].Normal);
        var lips = editor.MorphViewportRegionsList.Single(region => region.Name == "Lips");
        rendererType.GetMethod("MorphRegionLabel_Click", flags)!.Invoke(editor, [new Button { DataContext = lips }, new RoutedEventArgs()]);
        Assert.AreEqual("Lips", editor.SelectedMorphViewportMatch?.Name);
        Assert.IsFalse(editor.HasUnsavedMorphChanges);
        editor.HideMorphHair = true;
        rendererType.GetMethod("UpdateMorphRegionLabels", flags)!.Invoke(editor, null);
        Assert.AreEqual(lips.Brush.Color, editor.MorphViewportRegionsList.Single(region => region.Name == "Lips").Brush.Color);

    }

    [TestMethod]
    public void PersistentCalloutsFitDenseAndSmallViewportsWithoutOverlapping()
    {
        foreach (var viewport in new[] { new Size(900, 700), new Size(520, 340) })
        {
            var anchors = Enumerable.Range(0, 40).Select(index => new Point(180 + index % 3 * 10, 150 + index % 5 * 10)).ToArray();
            var labels = MorphRegionCalloutLayout.Place(viewport, anchors);
            Assert.HasCount(anchors.Length, labels);
            for (int i = 0; i < labels.Length; i++)
            {
                Assert.IsTrue(new Rect(viewport).Contains(labels[i]));
                for (int j = i + 1; j < labels.Length; j++) Assert.IsFalse(labels[i].IntersectsWith(labels[j]));
            }
        }
    }

    [STATestMethod]
    public void ClickingPaintedFeatureSelectsItsRightHandControlAndKeepsPointingLabelsVisible()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(BioMorphFaceEditor).Assembly);
        using var device = new SharpDX.Direct3D11.Device(SharpDX.Direct3D.DriverType.Warp);
        using var editor = new BioMorphFaceEditor { RenderGameShader = false };
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = typeof(MeshRenderer);
        var viewport = new Size(800, 600);
        editor.SceneViewer.Measure(viewport);
        editor.SceneViewer.Arrange(new Rect(viewport));
        editor.MeshContext.Camera.aspect = 800f / 600;
        editor.MeshContext.Camera.FocusDepth = 0;
        editor.MeshContext.Camera.Position = Vector3.Zero;
        editor.MeshContext.Camera.Pitch = editor.MeshContext.Camera.Yaw = 0;
        Vector3[] points = [new(-0.8f, -0.8f, 3), new(0.8f, -0.8f, 3), new(-0.8f, 0.8f, 3), new(0.8f, 0.8f, 3)];
        var mesh = new Mesh<WorldVertex>(device, [new Triangle(0, 1, 2), new Triangle(1, 3, 2)],
            points.Select(point => new WorldVertex(point, Vector3.UnitZ, Vector2.Zero)).ToList());
        var preview = new ModelPreview<WorldVertex>(device, mesh, null, null);
        preview.LODs[0].Sections.Add(new ModelPreviewSection("Skin", 0, 2));
        typeof(LegacyRenderContext).GetProperty(nameof(LegacyRenderContext.Device))!.SetValue(editor.MeshContext, device);
        type.GetField("LEXPreview", flags)!.SetValue(editor, preview);
        type.GetProperty(nameof(MeshRenderer.HasMorphEditorData))!.SetValue(editor, true);
        var targets = (IDictionary)type.GetField("MorphTargets", flags)!.GetValue(editor)!;
        AddTarget("Broad", [100, 100, 100, 100]);
        AddTarget("Local", [1, 1, 1, 0]);
        AddTarget("Unused", [1000, 1000, 1000, 1000]);
        var broad = new MorphFeatureEditorItem("Broad", 0.2f, () => Assert.Fail("Picking must not edit values")) { HasMorphTarget = true };
        var local = new MorphFeatureEditorItem("Local", 0.3f, () => Assert.Fail("Picking must not edit values")) { HasMorphTarget = true };
        editor.MorphFeatureItems.Add(broad);
        editor.MorphFeatureItems.Add(local);
        Invoke("UpdateMorphRegionLabels");
        Assert.HasCount(2, editor.MorphViewportRegionsList); // Unused catalog targets must not crowd out editable controls.
        var localRegion = editor.MorphViewportRegionsList.Single(region => region.Name == "Local");
        Assert.AreSame(localRegion.Brush, local.RegionBrush);
        SetTheme(true);
        var row = (Border)((DataTemplate)editor.FindResource("MorphFeatureEditorItemTemplate")).LoadContent();
        row.Resources = editor.Resources;
        row.DataContext = local;
        row.Measure(new Size(400, 180)); row.Arrange(new Rect(0, 0, 400, 180)); row.UpdateLayout();
        Assert.AreSame(localRegion.Brush, row.BorderBrush);
        Assert.AreEqual(Color.FromRgb(30, 30, 30), ((SolidColorBrush)row.Background).Color);
        Invoke("UpdateMorphRegionCallouts");
        Assert.IsFalse(editor.SceneViewer.IsMouseOver);
        Assert.HasCount(2, editor.MorphRegionCalloutCanvas.Children.OfType<Button>().ToArray());
        Point first = Project((points[0] + points[1] + points[2]) / 3);
        Invoke("PickMorphViewport", first);
        Assert.AreSame(local, editor.SelectedMorphViewportMatch?.Feature); // Raw movement ranks Broad and Unused above Local.
        Assert.IsTrue(local.IsViewportSelected);
        Assert.IsFalse(broad.IsViewportSelected);
        CollectionAssert.AreEqual(new[] { local }, editor.MatchedMorphFeatureItems.ToArray());
        Invoke("UpdateMorphRegionCallouts");
        Assert.HasCount(2, editor.MorphRegionCalloutCanvas.Children.OfType<Button>().ToArray());
        Assert.IsTrue(editor.MorphRegionCalloutCanvas.Children.OfType<Line>().All(line => line.X1 != line.X2));
        Capture("morph-callouts-dark.png");
        var caption = editor.MorphRegionCalloutCanvas.Children.OfType<Button>().First();
        Assert.AreEqual(Color.FromRgb(30, 30, 30), ((SolidColorBrush)caption.Background).Color);
        SetTheme(false);
        // The existing control row follows the same dynamic theme resources as the callout.
        local.IsViewportSelected = false;
        row.UpdateLayout();
        Assert.AreEqual(Colors.WhiteSmoke, ((SolidColorBrush)row.Background).Color);
        Capture("morph-callouts-light.png");
        Assert.AreEqual(Colors.WhiteSmoke, ((SolidColorBrush)caption.Background).Color);
        editor.ShowMorphRegionLabels = false;
        Assert.AreEqual(Visibility.Collapsed, editor.MorphRegionCalloutCanvas.Visibility);
        editor.ShowMorphRegionLabels = true;
        Assert.HasCount(2, editor.MorphRegionCalloutCanvas.Children.OfType<Button>().ToArray());
        Invoke("PickMorphViewport", Project((points[1] + points[3] + points[2]) / 3));
        Assert.AreSame(broad, editor.SelectedMorphViewportMatch?.Feature);
        Assert.AreEqual(0.3f, local.Value);
        Assert.IsFalse(editor.HasUnsavedMorphChanges);

        void AddTarget(string name, float[] weights)
        {
            var targetType = type.GetNestedType("MorphTargetSnapshot", BindingFlags.NonPublic)!;
            var lod = new MorphTarget.MorphLODModel { Vertices = weights.Select((weight, index) => new MorphTarget.MorphVertex
                { SourceIdx = (ushort)index, PositionDelta = Vector3.UnitX * weight }).ToArray() };
            targets.Add(name, Activator.CreateInstance(targetType, [new[] { lod }, Array.Empty<MorphTarget.BoneOffset>()]));
        }
        object Invoke(string name, params object[] args) => type.GetMethod(name, flags)!.Invoke(editor, args);
        Point Project(Vector3 position)
        {
            var clip = Vector4.Transform(new Vector4(position, 1), editor.MeshContext.Camera.ViewMatrix * editor.MeshContext.Camera.ProjectionMatrix);
            return new Point((clip.X / clip.W + 1) * viewport.Width / 2, (1 - clip.Y / clip.W) * viewport.Height / 2);
        }
        void SetTheme(bool dark)
        {
            editor.Resources[SystemColors.ControlBrushKey] = new SolidColorBrush(dark ? Color.FromRgb(30, 30, 30) : Colors.WhiteSmoke);
            editor.Resources[SystemColors.ControlTextBrushKey] = dark ? Brushes.WhiteSmoke : Brushes.Black;
        }
        void Capture(string filename)
        {
            var canvas = editor.MorphRegionCalloutCanvas;
            canvas.Measure(viewport); canvas.Arrange(new Rect(viewport)); canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle((Brush)editor.Resources[SystemColors.ControlBrushKey], null, new Rect(viewport));
                int i = 0;
                foreach (var triangle in mesh.Triangles)
                {
                    var path = new StreamGeometry();
                    using (var geometry = path.Open())
                    {
                        geometry.BeginFigure(Project(points[triangle.Vertex1]), true, true);
                        geometry.LineTo(Project(points[triangle.Vertex2]), true, false);
                        geometry.LineTo(Project(points[triangle.Vertex3]), true, false);
                    }
                    context.DrawGeometry((i++ == 0 ? local : broad).RegionBrush, null, path);
                }
            }
            bitmap.Render(drawing); bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(TestContext.TestResultsDirectory!);
            string pathName = System.IO.Path.Combine(TestContext.TestResultsDirectory, filename);
            using (var file = File.Create(pathName)) encoder.Save(file);
            TestContext.AddResultFile(pathName);
        }
    }

    [TestMethod]
    public void RegionShaderProjectsLiveVerticesAndRespectsOccludingDepth()
    {
        using var device = new SharpDX.Direct3D11.Device(SharpDX.Direct3D.DriverType.Warp);
        var context = device.ImmediateContext;
        using var effect = new GenericEffect<MeshRenderContext.WorldConstants, WorldVertex>(device, MeshRenderer.MorphRegionShader);
        var desc = new Texture2DDescription
        {
            Width = 64, Height = 64, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), BindFlags = BindFlags.RenderTarget
        };
        using var target = new Texture2D(device, desc);
        using var view = new RenderTargetView(device, target);
        desc.Format = Format.D32_Float; desc.BindFlags = BindFlags.DepthStencil;
        using var depth = new Texture2D(device, desc);
        using var depthView = new DepthStencilView(device, depth);
        using var depthState = new DepthStencilState(device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.Zero, DepthComparison = Comparison.LessEqual
        });
        using var rasterizer = new RasterizerState(device, new RasterizerStateDescription { FillMode = FillMode.Solid, CullMode = CullMode.None, IsDepthClipEnabled = true });
        context.OutputMerger.SetRenderTargets(depthView, view);
        context.OutputMerger.SetDepthStencilState(depthState);
        context.Rasterizer.State = rasterizer;
        context.Rasterizer.SetViewport(0, 0, 64, 64);
        context.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
        using var mesh = new Mesh<WorldVertex>(device, [new Triangle(0, 1, 2)],
        [
            new(new Vector3(-0.8f, -0.3f, 0.5f), Vector3.UnitX, Vector2.UnitX),
            new(new Vector3(-0.2f, -0.3f, 0.5f), Vector3.UnitX, Vector2.UnitX),
            new(new Vector3(-0.5f, 0.3f, 0.5f), Vector3.UnitX, Vector2.UnitX)
        ]);
        // Nonidentity view catches matrix convention errors that leave the tint detached from the face.
        var constants = new MeshRenderContext.WorldConstants(Matrix4x4.Identity,
            Matrix4x4.Transpose(Matrix4x4.CreateTranslation(0.5f, 0, 0)), Matrix4x4.Identity, 0);
        effect.PrepDraw(context, null);
        context.ClearRenderTargetView(view, new RawColor4(0, 0, 0, 1));
        context.ClearDepthStencilView(depthView, DepthStencilClearFlags.Depth, 1, 0);
        effect.RenderObject(context, constants, mesh);
        Assert.AreEqual((byte)255, ReadCenterRed());
        // Moving the current mesh vertices also moves the overlay.
        context.UpdateSubresource(mesh.Vertices.Select(vertex => vertex.WithPosition(vertex.Position + Vector3.UnitX)).ToArray(), mesh.VertexBuffer);
        context.ClearRenderTargetView(view, new RawColor4(0, 0, 0, 1));
        effect.RenderObject(context, constants, mesh);
        Assert.AreEqual((byte)0, ReadCenterRed());
        context.UpdateSubresource(mesh.Vertices.ToArray(), mesh.VertexBuffer);
        context.ClearDepthStencilView(depthView, DepthStencilClearFlags.Depth, 0.25f, 0);
        effect.RenderObject(context, constants, mesh);
        Assert.AreEqual((byte)0, ReadCenterRed());

        byte ReadCenterRed()
        {
            var readDesc = target.Description;
            readDesc.BindFlags = BindFlags.None; readDesc.Usage = ResourceUsage.Staging; readDesc.CpuAccessFlags = CpuAccessFlags.Read;
            using var readback = new Texture2D(device, readDesc);
            context.CopyResource(target, readback);
            var data = context.MapSubresource(readback, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            byte red = System.Runtime.InteropServices.Marshal.ReadByte(data.DataPointer, 32 * data.RowPitch + 32 * 4);
            context.UnmapSubresource(readback, 0);
            return red;
        }
    }
}
