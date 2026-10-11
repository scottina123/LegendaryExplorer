using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Numerics;
using System.Reflection;
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
        var context = new LevelEditorRenderContext();
        using var hidden = new TestActor(export);
        using var volume = new TestActor(export, volume: true);
        using var volumetric = new TestActor(export, volumetric: true);

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
        var context = new LevelEditorRenderContext();
        using var actor = new TestActor(actorExport);
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
        editor.RenderContext.LoadActors([actor]);
        try
        {
            actor.Location = new Vector3(300, 400, 500);
            actor.LightRadius = 2048;
            actor.Brightness = 2;
            typeof(LevelEditorRenderContext).GetMethod("FlushPendingActorVisualInvalidations",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor.RenderContext, null);
            var lights = (SceneLight[])typeof(MeshRenderContext).GetField("Lights",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor.RenderContext)!;

            Assert.HasCount(1, lights);
            Assert.AreEqual(actor.Location, lights[0].Position);
            Assert.AreEqual(2048f, lights[0].Radius);
            Assert.AreEqual(new Vector3(2), lights[0].Color);
            Assert.AreEqual(lightGuid, lights[0].LightGuid);
            Assert.AreEqual(lightmapGuid, lights[0].LightmapGuid);
            Assert.AreEqual(1024f, component.GetProperty<FloatProperty>("Radius").Value);
            Assert.AreEqual(1f, component.GetProperty<FloatProperty>("Brightness").Value);

            actor.CommitChanges();
            Assert.AreEqual(actor.LocalToWorld,
                CommonStructs.GetMatrix(component.GetProperty<StructProperty>("CachedParentToWorld")));
            editor.RenderContext.RemoveActor(actor);
            lights = (SceneLight[])typeof(MeshRenderContext).GetField("Lights",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor.RenderContext)!;
            Assert.HasCount(0, lights);
        }
        finally
        {
            editor.RenderContext.UnloadLevel();
        }
    }

    private sealed class TestActor : ActorProxy
    {
        private readonly bool volume;
        public override bool IsVolume => volume;

        public TestActor(ExportEntry export, bool volume = false, bool volumetric = false) : base(null, export)
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
