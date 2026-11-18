using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

/// <summary>
/// A draft copy (Duplicate, Alt+drag, Paste) is lit as its commit's copy will be: by a light environment of its own with its source's settings, not by its
/// source's, which would then take in both actors' bounds and light each where neither is
/// </summary>
[TestClass]
public class DraftLightEnvironmentTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void DraftCopyGetsItsOwnLightEnvironmentWithItsSourcesSettings()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("DraftLightEnvironment.pcc", MEGame.LE3);
        var context = new MeshRenderContext();
        try
        {
            ExportEntry level = package.CreateExport("PersistentLevel", "Level", indexed: false);
            ExportEntry actor = package.CreateExport("Pawn_0", "Pawn", level, indexed: false);
            ExportEntry environmentExport = package.CreateExport("Environment", "BioDynamicLightEnvironmentComponent", actor, indexed: false);
            environmentExport.WriteProperties([new BoolProperty(false, "bCastShadows")]);
            ExportEntry component = package.CreateExport("MeshComponent", "SkeletalMeshComponent", actor, indexed: false);
            component.WriteProperties([new ObjectProperty(environmentExport, "LightEnvironment")]);
            var draft = new object();

            Assert.IsTrue(context.ResolveLightEnvironment(component, component.GetProperties(), actor, out DynamicLightEnvironment sourceEnvironment));
            Assert.IsTrue(context.ResolveLightEnvironment(component, component.GetProperties(), actor, out DynamicLightEnvironment draftEnvironment, out _,
                previewInstance: draft, previewLevel: package));
            Assert.AreNotSame(sourceEnvironment, draftEnvironment, "The draft's light environment is its own.");
            Assert.AreSame(environmentExport, draftEnvironment.Export, "It has its source's light environment's settings.");
            Assert.AreSame(draft, draftEnvironment.TraceOwner, "Its traces pass through the draft's own meshes, not its source's.");
            Assert.AreSame(actor, sourceEnvironment.TraceOwner);

            var sourcePrimitive = Primitive(Vector3.Zero);
            var draftPrimitive = Primitive(new Vector3(1000, 0, 0));
            sourceEnvironment = context.AddLightEnvironmentPrimitive(sourceEnvironment, sourcePrimitive);
            draftEnvironment = context.AddLightEnvironmentPrimitive(draftEnvironment, draftPrimitive);
            Assert.AreNotSame(sourceEnvironment, draftEnvironment, "Joining keeps them apart.");
            Assert.AreEqual(1, sourceEnvironment.GetPrimitives().Length, "The source's light environment doesn't take in the draft's mesh.");
            Assert.AreEqual(2, context.GetLightEnvironments().Length);

            //the same draft's other meshes join its light environment, as a committed actor's meshes join its one
            Assert.IsTrue(context.ResolveLightEnvironment(component, component.GetProperties(), actor, out DynamicLightEnvironment again, out _,
                previewInstance: draft, previewLevel: package));
            Assert.AreSame(draftEnvironment, again);

            context.RemoveLightEnvironmentPrimitive(draftEnvironment, draftPrimitive);
            Assert.AreSame(sourceEnvironment, context.GetLightEnvironments().Single(), "A deleted or undone draft's light environment goes, its source's stays.");
        }
        finally
        {
            context.EmptyCaches();
            context.PackageCache.Dispose();
        }
    }

    [TestMethod]
    public void ACopyInAnotherLevelDropsAnotherActorsSharedLightEnvironment()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("SharedLightEnvironment.pcc", MEGame.LE3);
        using IMEPackage otherLevel = MEPackageHandler.CreateMemoryEmptyPackage("OtherLevel.pcc", MEGame.LE3);
        var context = new MeshRenderContext();
        try
        {
            ExportEntry level = package.CreateExport("PersistentLevel", "Level", indexed: false);
            ExportEntry sharer = package.CreateExport("InterpActor_0", "InterpActor", level, indexed: false);
            ExportEntry environmentExport = package.CreateExport("Environment", "BioDynamicLightEnvironmentComponent", sharer, indexed: false);
            environmentExport.WriteProperties([new BoolProperty(true, "bEnabled")]);
            ExportEntry actor = package.CreateExport("InterpActor_1", "InterpActor", level, indexed: false);
            ExportEntry component = package.CreateExport("MeshComponent", "StaticMeshComponent", actor, indexed: false);
            component.WriteProperties([new ObjectProperty(environmentExport, "LightEnvironment")]);
            var draft = new object();

            Assert.IsTrue(context.ResolveLightEnvironment(component, component.GetProperties(), actor, out DynamicLightEnvironment shared));
            Assert.IsTrue(context.ResolveLightEnvironment(component, component.GetProperties(), actor, out DynamicLightEnvironment inLevel, out _,
                previewInstance: draft, previewLevel: package));
            Assert.AreSame(sharer, inLevel.Owner, "In its own level, a copy shares the other actor's light environment, as its commit's copy's reference to it does.");
            Assert.IsNull(inLevel.PreviewInstance);
            Assert.IsNotNull(shared);

            Assert.IsFalse(context.ResolveLightEnvironment(component, component.GetProperties(), actor, out _, out _, previewInstance: draft, previewLevel: otherLevel),
                "In another level, the reference to another actor's light environment is dropped, so the copy has none.");
        }
        finally
        {
            context.EmptyCaches();
            context.PackageCache.Dispose();
        }
    }

    private static LightEnvironmentPrimitive Primitive(Vector3 origin) => new()
    {
        GetBounds = () => new BoxSphereBounds { Origin = origin, BoxExtent = new Vector3(10), SphereRadius = 18 },
        GetLocalToWorld = () => Matrix4x4.CreateTranslation(origin),
        LightingChannels = LightingChannels.DynamicPrimitiveDefault
    };
}
