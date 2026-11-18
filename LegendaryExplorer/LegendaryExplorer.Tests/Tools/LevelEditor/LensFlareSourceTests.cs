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
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void FactorySupportsLensFlareSourcesAndCustomSubclasses(MEGame game)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("LensFlare.pcc", game);
        ExportEntry source = package.CreateExport("Flare", "LensFlareSource", indexed: false);
        var context = new EditorContext();
        // Lens flare sources are helpers (Effects helpers category) in upstream's classification
        Assert.IsTrue(ActorProxy.CanCreate(source, includeHelpers: true));
        using ActorProxy actor = ActorProxy.Create(context, source);
        Assert.IsInstanceOfType<LensFlareSourceProxy>(actor);

        var subclass = new ExportEntry(package, 0, "CustomLensFlareSource", isClass: true)
        {
            SuperClass = source.Class
        };
        package.AddExport(subclass);
        var customSource = new ExportEntry(package, 0, "CustomFlare") { Class = subclass };
        package.AddExport(customSource);
        Assert.IsTrue(ActorProxy.CanCreate(customSource, includeHelpers: true));
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
        using var actor = CreateSource(source);

        Assert.AreSame(component, actor.LensFlareComponentExport);
        ExportEntry[] properties = actor.GetPropertyExports().ToArray();
        Assert.AreSame(source, properties[0]);
        Assert.AreSame(component, properties[1]);
        CollectionAssert.AreEquivalent(new[] { source, component, preview, sprite }, properties);
        Assert.IsTrue(actor.TestUIndexes([preview.UIndex]));
        Assert.IsFalse(actor.TestUIndexes([unrelated.UIndex]));
    }

    /// <summary>
    /// Component edits made through the inspector's exports, then a transform edit committed by the marker, all survive the rebuild
    /// upstream does when an actor's own exports change
    /// </summary>
    [TestMethod]
    public void ComponentPropertyEditsSurviveActorMovementAndRebuild()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("LensFlare.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Flare", "LensFlareSource", indexed: false);
        ExportEntry component = package.CreateExport("FlareComponent", "LensFlareComponent", source, indexed: false);
        ExportEntry template = package.CreateExport("SunFlare", "LensFlare", indexed: false);
        source.WriteProperty(new ObjectProperty(component, "LensFlareComp"));
        source.WriteProperty(CommonStructs.Vector3Prop(Vector3.Zero, "Location"));
        using var actor = CreateSource(source);

        // These are the same exports used by the right-column property editor.
        ExportEntry editableComponent = actor.GetPropertyExports().Single(export => export == component);
        editableComponent.WriteProperties(new PropertyCollection
        {
            new ObjectProperty(template, "Template"),
            new BoolProperty(false, "bAutoActivate"),
            CommonStructs.Vector3Prop(new Vector3(5, 10, 15), "Translation")
        });
        source.WriteProperty(new BoolProperty(false, "bCurrentlyActive"));
        Assert.IsTrue(actor.TestUIndexes([component.UIndex]), "A component edit must refresh the actor.");
        Assert.IsTrue(actor.GetPropertyExports().Contains(template), "The flare template in the same file is offered too.");

        using (var rebuilt = CreateSource(source))
        {
            Assert.AreEqual("SunFlare", rebuilt.DisplaySubtitle);
            Assert.IsFalse(rebuilt.IsFlareActive);
            rebuilt.Location = new Vector3(100, 200, 300);
            Assert.AreEqual(new Vector3(100, 200, 300), rebuilt.GetBounds().Origin);
            rebuilt.CommitChanges();
        }

        using var committed = CreateSource(source);
        Assert.AreEqual(new Vector3(100, 200, 300), committed.Location);
        Assert.IsFalse(source.GetProperty<BoolProperty>("bCurrentlyActive").Value);
        Assert.IsFalse(component.GetProperty<BoolProperty>("bAutoActivate").Value);
        Assert.AreEqual(template.UIndex, component.GetProperty<ObjectProperty>("Template").Value);
    }

    [TestMethod]
    public void RebuildFollowsReplacedComponentReference()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("LensFlare.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Flare", "LensFlareSource", indexed: false);
        ExportEntry first = package.CreateExport("FirstComponent", "LensFlareComponent", source, indexed: false);
        ExportEntry second = package.CreateExport("SecondComponent", "LensFlareComponent", source, indexed: false);
        source.WriteProperty(new ObjectProperty(first, "LensFlareComp"));
        using var actor = CreateSource(source);
        Assert.AreSame(first, actor.LensFlareComponentExport);

        source.WriteProperty(new ObjectProperty(second, "LensFlareComp"));
        Assert.IsTrue(actor.TestUIndexes([source.UIndex]));
        using var rebuilt = CreateSource(source);

        Assert.AreSame(second, rebuilt.LensFlareComponentExport);
        Assert.AreSame(second, rebuilt.GetPropertyExports().ElementAt(1));
    }

    [TestMethod]
    public void PointOfInterestPropertiesStillIncludeSimpleUseModules()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("PointOfInterest.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("PointOfInterest", "SFXPointOfInterest", indexed: false);
        ExportEntry module = package.CreateExport("UseModule", "SFXSimpleUseModule", source, indexed: false);
        source.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(module)], "Modules"));
        using ActorProxy actor = ActorProxy.Create(new EditorContext(), source);

        Assert.IsInstanceOfType<SFXPointOfInterestProxy>(actor);
        CollectionAssert.AreEqual(new[] { source, module }, actor.GetPropertyExports().ToArray());
    }

    private static LensFlareSourceProxy CreateSource(ExportEntry source)
    {
        ActorProxy actor = ActorProxy.Create(new EditorContext(), source);
        Assert.IsInstanceOfType<LensFlareSourceProxy>(actor, actor.Coverage.Reason);
        return (LensFlareSourceProxy)actor;
    }

    private sealed class EditorContext : IActorEditorContext
    {
        // Marker actors and their property exports need no GPU resources: without a Level Editor view, the flare isn't previewed
        public LevelEditorRenderContext RenderContext => null;
        public bool IsApplyingUndoRedo => true;
        public PackageCache PackageCache { get; } = new();
    }
}
