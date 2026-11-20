using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class LevelEditorUpstreamIntegrationTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void NativeVisibilityPreservesExistingVolumeControlsAndHiddenActorEditing()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("Visibility.pcc", MEGame.LE3);
        ExportEntry export = package.CreateExport("Actor", "Actor", indexed: false);
        export.WriteProperties([new BoolProperty(true, "bHidden")]);
        var editor = new TestEditor();
        LevelEditorRenderContext context = editor.RenderContext;
        using var hidden = new TestActor(editor, export);
        using var volume = new TestActor(editor, export, volume: true);
        using var volumetric = new TestActor(editor, export, volumetric: true);
        // The window's View options > Show > Hidden actors (upstream's default is off)
        context.ShowHidden = true;
        // Upstream shows the Volumes and Volumetric meshes categories by default; the fork's View options can still hide them
        Assert.IsTrue(context.ShowVolumes);
        Assert.IsTrue(context.ShowVolumetrics);
        context.ShowVolumes = false;
        context.ShowVolumetrics = false;

        Assert.IsTrue(context.IsActorVisible(hidden));
        Assert.IsFalse(context.IsActorVisible(volume));
        Assert.IsFalse(context.IsActorVisible(volumetric));
        context.ShowVolumes = true;
        context.ShowVolumetrics = true;
        Assert.IsTrue(context.IsActorVisible(volume));
        Assert.IsTrue(context.IsActorVisible(volumetric));
        context.ShowHidden = false;
        Assert.IsFalse(context.IsActorVisible(hidden));
    }

    [DataTestMethod]
    [DataRow("Head")]
    [DataRow("Hair")]
    public void AnimationParentDoesNotApplyInheritedCharacterOffsetsTwice(string childName)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("Character.pcc", MEGame.LE3);
        ExportEntry actorExport = package.CreateExport("Actor", "Actor", indexed: false);
        actorExport.WriteProperties([]);
        ExportEntry bodyExport = package.CreateExport("Body", "SkeletalMeshComponent", actorExport, indexed: false);
        bodyExport.WriteProperties([CommonStructs.Vector3Prop(new Vector3(0, 0, -88), "Translation")]);
        ExportEntry childExport = package.CreateExport(childName, "SkeletalMeshComponent", actorExport, indexed: false);
        childExport.WriteProperties([
            new ObjectProperty(bodyExport, "ParentAnimComponent"),
            new BoolProperty(true, "bTransformFromAnimParent"),
            CommonStructs.Vector3Prop(new Vector3(0, 0, -88), "Translation")
        ]);
        var editor = new TestEditor();
        LevelEditorRenderContext context = editor.RenderContext;
        using var actor = new TestActor(editor, actorExport);
        var body = new SkeletalMeshComponentProxy(context, bodyExport, actor);
        actor.Components.Add(body);
        var child = new SkeletalMeshComponentProxy(context, childExport, actor);
        actor.Components.Add(child);

        Assert.AreEqual(body.LocalToWorld, child.LocalToWorld);
        Assert.AreEqual(-88f, child.LocalToWorld.Translation.Z);
        actor.LocalToWorld = Matrix4x4.CreateRotationZ(0.4f) * Matrix4x4.CreateTranslation(100, 200, 300);
        body.UpdateLocalToWorld();
        child.UpdateLocalToWorld();
        Assert.AreEqual(body.LocalToWorld, child.LocalToWorld);
    }

    [TestMethod]
    public void UncommittedLightEditsUpdateNativeLightingWithoutLosingBakedLightIdentity()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("Lighting.pcc", MEGame.LE3);
        ExportEntry actorExport = package.CreateExport("Light", "PointLight", indexed: false);
        ExportEntry component = package.CreateExport("LightComponent", "PointLightComponent", actorExport, indexed: false);
        Guid lightGuid = Guid.NewGuid();
        Guid lightmapGuid = Guid.NewGuid();
        component.WriteProperties([
            new FloatProperty(1024, "Radius"), new FloatProperty(1, "Brightness"),
            CommonStructs.GuidProp(lightGuid, "LightGuid"),
            CommonStructs.GuidProp(lightmapGuid, "LightmapGuid"),
            CommonStructs.MatrixProp(Matrix4x4.Identity, "CachedParentToWorld")
        ]);
        actorExport.WriteProperties([new ObjectProperty(component, "LightComponent")]);
        var editor = new TestEditor();
        using var actor = new PointLightActorProxy(editor, actorExport);
        // Upstream rebuilds an edited light into the scene through the window (OnLightEdited -> OpenLevelFile.UpdateLight); the light the actor
        // adds, with its uncommitted edits, is the same one that rebuild makes
        actor.Location = new Vector3(300, 400, 500);
        actor.LightRadius = 2048;
        actor.Brightness = 2;
        Assert.IsTrue(actor.TryGetSceneLight(out SceneLight light));

        Assert.AreEqual(actor.Location, light.Position);
        Assert.AreEqual(2048f, light.Radius);
        Assert.AreEqual(new Vector3(2), light.Color);
        Assert.AreEqual(lightGuid, light.LightGuid);
        Assert.AreEqual(lightmapGuid, light.LightmapGuid);
        Assert.AreEqual(1024f, component.GetProperty<FloatProperty>("Radius").Value);
        Assert.AreEqual(1f, component.GetProperty<FloatProperty>("Brightness").Value);

        actor.CommitChanges();
        Assert.AreEqual(actor.LocalToWorld,
            CommonStructs.GetMatrix(component.GetProperty<StructProperty>("CachedParentToWorld")));
        Assert.AreEqual(2048f, component.GetProperty<FloatProperty>("Radius").Value);
    }

    private sealed class TestActor : ActorProxy
    {
        private readonly bool volume;
        public override bool IsVolume => volume;

        public TestActor(IActorEditorContext editor, ExportEntry export, bool volume = false, bool volumetric = false) : base(editor, export)
        {
            this.volume = volume;
            IsVolumetricMesh = volumetric;
        }
    }

    private sealed class TestEditor : IActorEditorContext
    {
        public LevelEditorRenderContext RenderContext { get; } = new();
        public bool IsApplyingUndoRedo => true;
    }
}
