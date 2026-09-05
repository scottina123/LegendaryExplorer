using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class LensFlareSourceTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void FactorySupportsLensFlareSourcesAndCustomSubclasses(MEGame game)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("LensFlare.pcc", game);
        ExportEntry source = package.CreateExport("Flare", "LensFlareSource", indexed: false);
        var context = new EditorContext();
        Assert.IsTrue(ActorProxy.CanCreate(source));
        using ActorProxy actor = ActorProxy.Create(context, source);
        Assert.IsInstanceOfType<LensFlareSourceProxy>(actor);

        var subclass = new ExportEntry(package, 0, "CustomLensFlareSource", isClass: true)
        {
            SuperClass = source.Class
        };
        package.AddExport(subclass);
        var customSource = new ExportEntry(package, 0, "CustomFlare") { Class = subclass };
        package.AddExport(customSource);
        Assert.IsTrue(ActorProxy.CanCreate(customSource));
        using ActorProxy customActor = ActorProxy.Create(context, customSource);
        Assert.IsInstanceOfType<LensFlareSourceProxy>(customActor);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void PropertiesIncludeComponentAndNestedChildrenExactlyOnce(bool hasComponentReference)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("LensFlare.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Flare", "LensFlareSource", indexed: false);
        ExportEntry component = package.CreateExport("FlareComponent", "SFXSelectionLensFlareComponent", source, indexed: false);
        ExportEntry preview = package.CreateExport("PreviewRadius", "DrawLightRadiusComponent", component, indexed: false);
        ExportEntry sprite = package.CreateExport("Sprite", "SpriteComponent", source, indexed: false);
        ExportEntry unrelated = package.CreateExport("OtherFlare", "LensFlareSource", indexed: false);
        if (hasComponentReference)
            source.WriteProperty(new ObjectProperty(component, "LensFlareComp"));
        using var actor = new LensFlareSourceProxy(new EditorContext(), source);

        Assert.AreSame(component, actor.LensFlareComp.Export);
        ExportEntry[] properties = actor.GetPropertyExports().ToArray();
        Assert.AreSame(source, properties[0]);
        Assert.AreSame(component, properties[1]);
        CollectionAssert.AreEquivalent(new[] { source, component, preview, sprite }, properties);
        Assert.IsTrue(actor.TestUIndexes([preview.UIndex]));
        Assert.IsFalse(actor.TestUIndexes([unrelated.UIndex]));
    }

    [TestMethod]
    public void ComponentPropertyEditsSurviveActorMovementAndRefresh()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("LensFlare.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Flare", "LensFlareSource", indexed: false);
        ExportEntry component = package.CreateExport("FlareComponent", "LensFlareComponent", source, indexed: false);
        ExportEntry template = package.CreateExport("SunFlare", "LensFlare", indexed: false);
        source.WriteProperty(new ObjectProperty(component, "LensFlareComp"));
        using var actor = new LensFlareSourceProxy(new EditorContext(), source);

        // These are the same exports used by the right-column property editor.
        ExportEntry editableComponent = actor.GetPropertyExports().Single(export => export == component);
        editableComponent.WriteProperties(new PropertyCollection
        {
            new ObjectProperty(template, "Template"),
            new BoolProperty(false, "bAutoActivate"),
            CommonStructs.Vector3Prop(new Vector3(5, 10, 15), "Translation")
        });
        source.WriteProperty(new BoolProperty(false, "bCurrentlyActive"));
        actor.RefreshFromExport();
        Assert.AreEqual("SunFlare", actor.DisplaySubtitle);
        Assert.IsFalse(actor.LensFlareComp.Properties.GetProp<BoolProperty>("bAutoActivate").Value);

        actor.Location = new Vector3(100, 200, 300);
        Assert.AreEqual(new Vector3(105, 210, 315), actor.GetBounds().Origin);
        actor.CommitChanges();
        actor.RefreshFromExport();

        Assert.AreEqual(new Vector3(100, 200, 300), actor.Location);
        Assert.IsFalse(source.GetProperty<BoolProperty>("bCurrentlyActive").Value);
        Assert.IsFalse(component.GetProperty<BoolProperty>("bAutoActivate").Value);
        Assert.AreEqual(template.UIndex, component.GetProperty<ObjectProperty>("Template").Value);
    }

    [TestMethod]
    public void RefreshFollowsReplacedComponentReference()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("LensFlare.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Flare", "LensFlareSource", indexed: false);
        ExportEntry first = package.CreateExport("FirstComponent", "LensFlareComponent", source, indexed: false);
        ExportEntry second = package.CreateExport("SecondComponent", "LensFlareComponent", source, indexed: false);
        source.WriteProperty(new ObjectProperty(first, "LensFlareComp"));
        using var actor = new LensFlareSourceProxy(new EditorContext(), source);

        source.WriteProperty(new ObjectProperty(second, "LensFlareComp"));
        second.WriteProperty(CommonStructs.Vector3Prop(new Vector3(20, 30, 40), "Translation"));
        actor.RefreshFromExport();

        Assert.AreSame(second, actor.LensFlareComp.Export);
        Assert.HasCount(1, actor.Components);
        Assert.AreSame(second, actor.GetPropertyExports().ElementAt(1));
        Assert.AreEqual(new Vector3(20, 30, 40), actor.GetBounds().Origin);
    }

    [TestMethod]
    public void PointOfInterestPropertiesStillIncludeSimpleUseModules()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("PointOfInterest.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("PointOfInterest", "SFXPointOfInterest", indexed: false);
        ExportEntry module = package.CreateExport("UseModule", "SFXSimpleUseModule", source, indexed: false);
        source.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(module)], "Modules"));
        using var actor = new SFXPointOfInterestProxy(new EditorContext(), source);

        CollectionAssert.AreEqual(new[] { source, module }, actor.GetPropertyExports().ToArray());
    }

    private sealed class EditorContext : IActorEditorContext
    {
        // Primitive component property/transform editing requires no GPU resources.
        public LevelEditorRenderContext RenderContext => null;
        public bool IsApplyingUndoRedo => true;
    }
}
