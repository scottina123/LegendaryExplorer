using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

/// <summary>
/// A copy of an effect or gameplay helper (Duplicate, Alt+drag, Paste) is a draft of the fork's subtype its committed view will be, drawing what that view
/// draws, from its source's components' values: an Emitter plays its source's particle system, a LensFlareSource draws its source's flare
/// </summary>
[TestClass]
public class DraftEffectProxyTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void EmitterCopyPlaysItsSourcesParticleSystemWhereItsCommittedViewWould()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("EmitterCopy.pcc", MEGame.LE3);
        var editor = new EditorContext();
        ExportEntry system = package.CreateExport("Sparks", "ParticleSystem", indexed: false);
        ExportEntry emitter = package.CreateExport("Emitter_0", "Emitter", indexed: false);
        ExportEntry component = package.CreateExport("ParticleSystemComponent_0", "ParticleSystemComponent", emitter, indexed: false);
        component.WriteProperties([new ObjectProperty(system, "Template"), CommonStructs.Vector3Prop(new Vector3(0, 0, 50), "Translation")]);
        var location = new Vector3(100, 200, 300);
        emitter.WriteProperties([new ObjectProperty(component, "ParticleSystemComponent"), CommonStructs.Vector3Prop(location, "location")]);

        using var committed = (EmitterActorProxy)ActorProxy.Create(editor, emitter);
        using var draft = Copy(editor, emitter);

        Assert.IsInstanceOfType<EmitterActorProxy>(draft, "Shown and hidden by Show emitters, as its committed view is.");
        var copy = (EmitterActorProxy)draft;
        Assert.IsTrue(copy.IsDraft);
        Assert.IsNotNull(copy.ParticleSystemComponent, "It plays its particle system before its commit.");
        Assert.AreSame(committed.ParticleSystemComponent.ParticleSystem, copy.ParticleSystemComponent.ParticleSystem);
        Assert.AreEqual(committed.ParticleSystemComponent.LocalToWorld, copy.ParticleSystemComponent.LocalToWorld,
            "Its component's own transform is its source's component's.");
        Assert.AreEqual(location + new Vector3(0, 0, 50), copy.ParticleSystemComponent.LocalToWorld.Translation);
        Assert.AreEqual("Sparks", copy.DisplaySubtitle, "Its outliner line names its particle system, as its committed view's does.");
        Assert.AreEqual(committed.Coverage, copy.Coverage);
        Assert.IsTrue(typeof(EmitterActorProxy).IsAssignableFrom(typeof(EmitterDraftProxy)), "A placed Emitter's draft is one too.");
    }

    [TestMethod]
    public void LensFlareCopyDrawsItsSourcesFlare()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("FlareCopy.pcc", MEGame.LE3);
        var editor = new EditorContext();
        ExportEntry template = package.CreateExport("SunFlare", "LensFlare", indexed: false);
        ExportEntry source = package.CreateExport("Flare", "LensFlareSource", indexed: false);
        ExportEntry component = package.CreateExport("FlareComponent", "LensFlareComponent", source, indexed: false);
        component.WriteProperties([new ObjectProperty(template, "Template"), CommonStructs.Vector3Prop(new Vector3(5, 10, 15), "Translation")]);
        source.WriteProperties([new ObjectProperty(component, "LensFlareComp"), CommonStructs.Vector3Prop(new Vector3(1, 2, 3), "location")]);

        using var committed = (LensFlareSourceProxy)ActorProxy.Create(editor, source);
        using var draft = Copy(editor, source);

        Assert.IsInstanceOfType<LensFlareSourceProxy>(draft);
        var copy = (LensFlareSourceProxy)draft;
        Assert.IsNotNull(copy.LensFlareComponent, "It draws its flare before its commit.");
        Assert.AreSame(template, copy.LensFlareComponent.Template);
        Assert.AreEqual(committed.LensFlareComponent.LocalToWorld, copy.LensFlareComponent.LocalToWorld);
        Assert.AreEqual("SunFlare", copy.DisplaySubtitle);
        Assert.IsNull(copy.LensFlareComponentExport, "It has no component export until its commit.");
        Assert.AreEqual(0, copy.GetPropertyExports().Count());
        Assert.IsFalse(copy.TestUIndexes([component.UIndex]), "Its source's exports aren't its own.");
    }

    [TestMethod]
    public void PointOfInterestCopyIsAPointOfInterest()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("PoiCopy.pcc", MEGame.LE3);
        var editor = new EditorContext();
        ExportEntry source = package.CreateExport("PointOfInterest", "SFXPointOfInterest", indexed: false);
        source.WriteProperties([CommonStructs.Vector3Prop(new Vector3(1, 2, 3), "location")]);
        using var draft = Copy(editor, source);
        Assert.IsInstanceOfType<SFXPointOfInterestProxy>(draft, "Shown and hidden by Show points of interest, as its committed view is.");
        Assert.AreEqual(0, draft.GetPropertyExports().Count());
    }

    [TestMethod]
    [DataRow("PointLightComponent", typeof(PointLightActorProxy))]
    [DataRow("SpotLightComponent", typeof(SpotLightActorProxy))]
    [DataRow("DirectionalLightComponent", typeof(DirectionalLightActorProxy))]
    public void NewAndCopiedLightsAreOfTheirCommittedViewsSubtype(string componentClass, System.Type expected)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("LightDraft.pcc", MEGame.LE3);
        var editor = new EditorContext();
        ExportEntry light = package.CreateExport("Light_0", "PointLight", indexed: false);
        ExportEntry component = package.CreateExport("LightComponent_0", componentClass, light, indexed: false);
        component.WriteProperties([new FloatProperty(2, "Brightness")]);
        var record = editor.RenderContext.Registry.CreateDraft(package, light.Class, "Light_1", isSpline: false, isLoft: false);
        using var draft = ForkActorProxies.CreateDraftLight(editor, record, [CommonStructs.Vector3Prop(Vector3.Zero, "location")], component, cloneSource: light);
        Assert.IsInstanceOfType(draft, expected);
        Assert.AreSame(light, draft.CloneSource);
        Assert.AreEqual(2, draft.Brightness, "Its light is its source's component's.");
    }

    //as ActorCloning makes a copy of a helper's view
    private static EditorActorProxy Copy(EditorContext editor, ExportEntry saved)
    {
        var record = editor.RenderContext.Registry.CreateDraft(saved.FileRef, saved.Class, saved.ObjectName.Instanced + "_Copy", isSpline: false, isLoft: false);
        return ForkActorProxies.CreateDraftHelper(editor, record, saved.GetCondensedProperties(), ActorClassification.Get(saved), saved);
    }

    private sealed class EditorContext : IActorEditorContext
    {
        public LevelEditorRenderContext RenderContext { get; } = new();
        public bool IsApplyingUndoRedo => true;
    }
}
