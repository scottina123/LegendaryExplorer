using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpDX.D3DCompiler;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class LensFlareRenderingTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void NativeLensFlareStreamKeepsVertexColorSeparateFromFlareParameters()
    {
        using var shader = ShaderBytecode.Compile("""
            struct Input { float4 position : POSITION; float4 size : TANGENT; float rotation : BLENDWEIGHT;
                float2 uv : TEXCOORD0; float4 color : TEXCOORD1; float4 flare : TEXCOORD2; };
            float4 main(Input i) : SV_Position { return i.position + i.size + i.rotation + i.uv.x + i.color + i.flare; }
            """, "main", "vs_5_0");
        Assert.IsTrue(MeshRenderContext.ValidateVertexFactoryInputLayout<LensFlareVertex>(
            "FLensFlareVertexFactory", shader.Bytecode.Data, out string error), error);
        Assert.IsFalse(MeshRenderContext.ValidateVertexFactoryInputLayout<ParticleVertex>(
            "FLensFlareVertexFactory", shader.Bytecode.Data, out _));
        Assert.AreEqual(LensFlareVertex.Stride, Marshal.SizeOf<LensFlareVertex>());
        var color = new Vector4(1, 0.3f, 0.06f, 0.5f);
        var parameters = new Vector4(0, 0.2f, 300, 1);
        var vertex = new LensFlareVertex(Vector3.One, Vector2.One, 0, Vector2.Zero, parameters, color);
        float[] stream = new float[LensFlareVertex.Stride / sizeof(float)];
        vertex.ToFloats(stream);
        CollectionAssert.AreEqual(new[] { color.X, color.Y, color.Z, color.W }, stream.Skip(11).Take(4).ToArray());
        CollectionAssert.AreEqual(new[] { parameters.X, parameters.Y, parameters.Z, parameters.W }, stream.Skip(15).Take(4).ToArray());
    }

    [TestMethod]
    public void ReflectionElementsRenderEvenWhenSourceHasNoMaterial()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("Flare.pcc", MEGame.LE3);
        ExportEntry template = package.CreateExport("Flare", "LensFlare", indexed: false);
        ExportEntry material = package.CreateExport("Halo", "Material", indexed: false);
        template.WriteProperties(new PropertyCollection
        {
            new StructProperty("LensFlareElement", new PropertyCollection(), "SourceElement"),
            new ArrayProperty<StructProperty>([new StructProperty("LensFlareElement", new PropertyCollection
            {
                new ArrayProperty<ObjectProperty>([new ObjectProperty(material)], "LFMaterials"),
                CommonStructs.Vector3Prop(new Vector3(3, 0.5f, 0), "Size"),
                RawFloat("Scaling", 0.1f), RawFloat("Alpha", 0.5f),
                new BoolProperty(true, "bModulateColorBySource")
            })], "Reflections")
        });
        var definition = new LensFlarePreview(template);
        Assert.HasCount(2, definition.Elements);
        Assert.IsNull(definition.Elements[0].Evaluate(0, 300, Vector4.One).Material);
        var sourceColor = new Vector4(1, 0.3f, 0.06f, 1);
        LensFlareElementSample sample = definition.Elements[1].Evaluate(0, 300, sourceColor);
        Assert.AreSame(material, sample.Material);
        Assert.AreEqual(new Vector2(0.3f, 0.05f), sample.Size);
        Assert.AreEqual(new Vector4(1, 0.3f, 0.06f, 0.5f), sample.Color);
    }

    [TestMethod]
    public void DistanceDistributionsUseWorldDistanceAndRetainAuthoredBrightness()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("Flare.pcc", MEGame.LE3);
        ExportEntry template = package.CreateExport("Flare", "LensFlare", indexed: false);
        var element = new StructProperty("LensFlareElement", new PropertyCollection
        {
            new StructProperty("RawDistributionFloat", new PropertyCollection
            {
                new ArrayProperty<FloatProperty>([new(0), new(1), new(1), new(0)], "LookupTable"),
                new FloatProperty(0.001f, "LookupTableTimeScale")
            }, "DistMap_Alpha"),
            RawFloat("Scaling", 2)
        });
        var preview = new LensFlareElementPreview(element, template, false);
        Assert.AreEqual(1f, preview.Evaluate(0, 0, Vector4.One).Color.W, 0.0001f);
        Assert.AreEqual(0.5f, preview.Evaluate(0, 500, Vector4.One).Color.W, 0.0001f);
        Assert.AreEqual(0f, preview.Evaluate(0, 1000, Vector4.One).Color.W, 0.0001f);
        Assert.AreEqual(new Vector2(2), preview.Evaluate(0, 500, Vector4.One).Size);
    }

    [TestMethod]
    public void FlareRadiusAndDirectionalConeLimitVisibility()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("Flare.pcc", MEGame.LE3);
        ExportEntry template = package.CreateExport("Flare", "LensFlare", indexed: false);
        template.WriteProperties(new PropertyCollection
        {
            new FloatProperty(1000, "Radius"), new FloatProperty(30, "InnerCone"), new FloatProperty(60, "OuterCone")
        });
        var preview = new LensFlarePreview(template);
        Assert.AreEqual(1f, preview.GetIntensity(new Vector3(500, 0, 0), Vector3.UnitX));
        Assert.AreEqual(0f, preview.GetIntensity(new Vector3(1001, 0, 0), Vector3.UnitX));
        Assert.AreEqual(0f, preview.GetIntensity(new Vector3(-500, 0, 0), Vector3.UnitX));
        Assert.AreEqual(0.5f, preview.GetIntensity(new Vector3(500, 500, 0), Vector3.UnitX), 0.001f);
    }

    [TestMethod]
    public void SharedFlareResourcesAreReusedUntilTemplateOrChildPropertiesChange()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("Flare.pcc", MEGame.LE3);
        ExportEntry template = package.CreateExport("Flare", "LensFlare", indexed: false);
        ExportEntry distribution = package.CreateExport("Scaling", "DistributionFloatConstant", template, indexed: false);
        var context = new LevelEditorRenderContext();
        object renderer = typeof(LevelEditorRenderContext).GetProperty("LensFlareRenderer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(context)!;
        MethodInfo prepare = renderer.GetType().GetMethod("Prepare")!;
        object Prepare() => prepare.Invoke(renderer, [template])!;
        try
        {
            object original = Prepare();
            Assert.AreSame(original, Prepare());
            distribution.WriteProperty(new FloatProperty(2, "Constant"));
            object childEdited = Prepare();
            Assert.AreNotSame(original, childEdited);
            Assert.AreSame(childEdited, Prepare());
            template.WriteProperty(new FloatProperty(2000, "Radius"));
            Assert.AreNotSame(childEdited, Prepare());
        }
        finally
        {
            ((IDisposable)renderer).Dispose();
        }
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public void ReflectionParallaxUsesTheDisplayedView(bool firstPerson, bool orthographic)
    {
        var camera = new SceneCamera { FirstPerson = firstPerson, IsOrthographic = orthographic,
            FocusDepth = 100, Position = new Vector3(20, 30, 40) };
        Matrix4x4.Invert(camera.ViewMatrix, out Matrix4x4 viewToWorld);
        Vector3 source = Vector3.Transform(new Vector3(25, 10, 200), viewToWorld);
        Vector3 center = Vector3.Transform(new Vector3(0, 0, 200), viewToWorld);
        Assert.IsLessThan(0.0001f, Vector3.Distance(source, LensFlarePreview.GetElementPosition(source, camera, 0)));
        Assert.IsLessThan(0.0001f, Vector3.Distance(center, LensFlarePreview.GetElementPosition(source, camera, 1)));
        Assert.IsLessThan(0.0001f, Vector3.Distance(center * 2 - source, LensFlarePreview.GetElementPosition(source, camera, 2)));
    }

    private static StructProperty RawFloat(string name, float value) => new("RawDistributionFloat", new PropertyCollection
    {
        new ArrayProperty<FloatProperty>([new(value), new(value), new(value), new(value)], "LookupTable")
    }, name);
}
