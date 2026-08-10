using System.Collections.Generic;
using System.Threading.Tasks;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.SharedUI;

[TestClass]
public class TreeViewEntryTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void FlattenTreeReturnsDepthFirstSublinkOrder()
    {
        var root = new TreeViewEntry(null, "root");
        var first = new TreeViewEntry(null, "first") { Parent = root };
        var second = new TreeViewEntry(null, "second") { Parent = root };
        var firstChild = new TreeViewEntry(null, "first child") { Parent = first };
        var firstGrandchild = new TreeViewEntry(null, "first grandchild") { Parent = firstChild };
        var secondChild = new TreeViewEntry(null, "second child") { Parent = second };

        root.Sublinks.Add(first);
        root.Sublinks.Add(second);
        first.Sublinks.Add(firstChild);
        firstChild.Sublinks.Add(firstGrandchild);
        second.Sublinks.Add(secondChild);

        List<TreeViewEntry> flattened = root.FlattenTree();

        CollectionAssert.AreEqual(
            new[] { root, first, firstChild, firstGrandchild, second, secondChild },
            flattened);
    }

    [TestMethod]
    public void EmitterSubtitleShowsLinkedParticleSystemTemplateName()
    {
        bool previousSetting = Settings.PackageEditor_ShowTreeEntrySubText;
        try
        {
            Settings.PackageEditor_ShowTreeEntrySubText = true;
            using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("EmitterSubtitleTest.pcc", MEGame.LE3);
            ExportEntry particleSystem = package.CreateExport("PS_Afterlife_Smoke", "ParticleSystem", indexed: false);
            ExportEntry component = package.CreateExport("ParticleSystemComponent_0", "ParticleSystemComponent", indexed: false);
            ExportEntry emitter = package.CreateExport("Emitter_0", "Emitter", indexed: false);
            component.WriteProperty(new ObjectProperty(particleSystem, "Template"));
            emitter.WriteProperty(new ObjectProperty(component, "ParticleSystemComponent"));
            emitter.WriteProperty(new NameProperty("Emitter", "Tag"));

            using var treeEntry = new TreeViewEntry(emitter);

            Assert.AreEqual("PS_Afterlife_Smoke", treeEntry.SubText);
        }
        finally
        {
            Settings.PackageEditor_ShowTreeEntrySubText = previousSetting;
        }
    }

    [TestMethod]
    public void StaticMeshActorAndComponentSubtitlesShowReferencedMeshName()
    {
        bool previousSetting = Settings.PackageEditor_ShowTreeEntrySubText;
        try
        {
            Settings.PackageEditor_ShowTreeEntrySubText = true;
            using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("StaticMeshSubtitleTest.pcc", MEGame.LE3);
            ExportEntry staticMesh = package.CreateExport("BIOG_Example_StaticMesh", "StaticMesh", indexed: false);
            ExportEntry component = package.CreateExport("StaticMeshComponent_0", "StaticMeshComponent", indexed: false);
            ExportEntry actor = package.CreateExport("StaticMeshActor_0", "StaticMeshActor", indexed: false);
            component.WriteProperty(new ObjectProperty(staticMesh, "StaticMesh"));
            actor.WriteProperty(new ObjectProperty(component, "StaticMeshComponent"));

            using var componentTreeEntry = new TreeViewEntry(component);
            using var actorTreeEntry = new TreeViewEntry(actor);

            Assert.AreEqual("BIOG_Example_StaticMesh", componentTreeEntry.SubText);
            Assert.AreEqual("BIOG_Example_StaticMesh", actorTreeEntry.SubText);
        }
        finally
        {
            Settings.PackageEditor_ShowTreeEntrySubText = previousSetting;
        }
    }

    [TestMethod]
    public void InterpreterObjectReferenceShowsMeshUsedByStaticMeshComponentSubclass()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("StaticMeshInterpreterTest.pcc", MEGame.LE3);
        ExportEntry staticMesh = package.CreateExport("BIOG_Example_StaticMesh", "StaticMesh", indexed: false);
        ExportEntry component = package.CreateExport("FracturedStaticMeshComponent_0", "FracturedStaticMeshComponent", indexed: false);
        ExportEntry actor = package.CreateExport("StaticMeshActor_0", "StaticMeshActor", indexed: false);
        component.WriteProperty(new ObjectProperty(staticMesh, "StaticMesh"));
        var componentProperty = new ObjectProperty(component, "StaticMeshComponent");

        var parent = new UPropertyTreeViewEntry { AttachedExport = actor };
        UPropertyTreeViewEntry propertyEntry = InterpreterExportLoader.GenerateUPropertyTreeViewEntry(
            componentProperty,
            parent,
            actor);

        StringAssert.Contains(propertyEntry.EditableValue, "(BIOG_Example_StaticMesh)");
    }
}
