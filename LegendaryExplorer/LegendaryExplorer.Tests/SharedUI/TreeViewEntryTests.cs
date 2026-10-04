using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.TLK.ME1;
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
    [DataRow("SFXStuntActor", null, "ActorTemplate\nInherited: inherited_actor_tag")]
    [DataRow("BioStage", "None", "ActorTemplate\nInherited: inherited_actor_tag")]
    [DataRow("SFXStuntActor", "actor_tag", "actor_tag\nActorTemplate")]
    [DataRow("BioStage", "inherited_actor_tag", "inherited_actor_tag\nActorTemplate")]
    public void PersistentActorOnlyShowsInheritedTagWhenItsOwnTagIsMissing(string className, string ownTag, string expected)
    {
        using var subtitles = new SubtitleTestSettings();
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InheritedActorTagTest.pcc", MEGame.LE1);
        ExportEntry level = CreateSubtitleExport(package, "PersistentLevel", "Level");
        ExportEntry archetype = CreateSubtitleExport(package, "ActorTemplate", className);
        ExportEntry actor = CreateSubtitleExport(package, "Actor", className, level);
        archetype.WriteProperty(new NameProperty("inherited_actor_tag", "Tag"));
        actor.Archetype = archetype;
        if (ownTag != null)
        {
            actor.WriteProperty(new NameProperty(ownTag, "Tag"));
        }

        using var treeEntry = new TreeViewEntry(actor);

        Assert.AreEqual(expected, treeEntry.SubText);
    }

    [TestMethod]
    [DataRow(false, "module_tag\n\"Local game name\"")]
    [DataRow(true, "module_tag\nInherited: \"Module template game name\"")]
    public void SimpleUseModuleSubtitlePrefersOwnGameNameAndFallsBackFromZero(bool useZeroIntProperty, string expected)
    {
        using var subtitles = new SubtitleTestSettings();
        using IMEPackage package = CreateGameNamePackage();
        ExportEntry archetype = CreateSubtitleExport(package, "ModuleTemplate", "SFXSimpleUseModule");
        ExportEntry module = CreateSubtitleExport(package, "UseModule", "SFXSimpleUseModule");
        archetype.WriteProperty(new StringRefProperty(692098, "m_srGameName"));
        module.Archetype = archetype;
        module.WriteProperty(new NameProperty("module_tag", "Tag"));
        if (useZeroIntProperty)
        {
            module.WriteProperty(new IntProperty(0, "m_srGameName"));
        }
        else
        {
            module.WriteProperty(new StringRefProperty(692097, "m_srGameName"));
        }

        using var treeEntry = new TreeViewEntry(module);

        Assert.AreEqual(expected, treeEntry.SubText);
    }

    [TestMethod]
    public void ActorSubtitlePrefersAnyLocalModuleGameNameOverInheritedModuleNames()
    {
        using var subtitles = new SubtitleTestSettings();
        using IMEPackage package = CreateGameNamePackage();
        ExportEntry level = CreateSubtitleExport(package, "PersistentLevel", "Level");
        ExportEntry actorArchetype = CreateSubtitleExport(package, "ActorTemplate", "SFXStuntActor");
        ExportEntry actor = CreateSubtitleExport(package, "Actor", "SFXStuntActor", level);
        ExportEntry moduleArchetype = CreateSubtitleExport(package, "ModuleTemplate", "SFXSimpleUseModule");
        ExportEntry firstModule = CreateSubtitleExport(package, "InheritedUseModule", "SFXSimpleUseModule", actor);
        ExportEntry secondModule = CreateSubtitleExport(package, "LocalUseModule", "SFXSimpleUseModule", actor);
        ExportEntry actorArchetypeModule = CreateSubtitleExport(package, "ActorTemplateModule", "SFXSimpleUseModule", actorArchetype);
        actor.Archetype = actorArchetype;
        actor.WriteProperty(new NameProperty("actor_tag", "Tag"));
        actorArchetype.WriteProperty(new NameProperty("inherited_actor_tag", "Tag"));
        firstModule.Archetype = moduleArchetype;
        firstModule.WriteProperty(new StringRefProperty(0, "m_srGameName"));
        moduleArchetype.WriteProperty(new StringRefProperty(692098, "m_srGameName"));
        secondModule.WriteProperty(new IntProperty(692097, "m_srGameName"));
        actorArchetypeModule.WriteProperty(new StringRefProperty(692099, "m_srGameName"));
        actor.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(firstModule), new ObjectProperty(secondModule)], "Modules"));
        actorArchetype.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(actorArchetypeModule)], "Modules"));

        using var treeEntry = new TreeViewEntry(actor);

        Assert.AreEqual("actor_tag\n\"Local game name\"\nActorTemplate", treeEntry.SubText);
    }

    [TestMethod]
    [DataRow(true, "Inherited: \"Module template game name\"")]
    [DataRow(false, "Inherited: \"Actor template game name\"")]
    public void ActorSubtitleFallsBackThroughModuleAndActorArchetypes(bool hasOwnModule, string expected)
    {
        using var subtitles = new SubtitleTestSettings();
        using IMEPackage package = CreateGameNamePackage();
        ExportEntry actor = CreateSubtitleExport(package, "PointOfInterest", "SFXPointOfInterest");
        ExportEntry actorArchetype = CreateSubtitleExport(package, "ActorTemplate", "SFXPointOfInterest");
        ExportEntry actorBase = CreateSubtitleExport(package, "ActorBase", "SFXPointOfInterest");
        ExportEntry actorBaseModule = CreateSubtitleExport(package, "ActorBaseModule", "SFXSimpleUseModule", actorBase);
        actor.Archetype = actorArchetype;
        actorArchetype.Archetype = actorBase;
        actorBaseModule.WriteProperty(new StringRefProperty(692099, "m_srGameName"));
        actorBase.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(actorBaseModule)], "Modules"));
        if (hasOwnModule)
        {
            ExportEntry module = CreateSubtitleExport(package, "UseModule", "SFXSimpleUseModule", actor);
            ExportEntry moduleArchetype = CreateSubtitleExport(package, "ModuleTemplate", "SFXSimpleUseModule");
            ExportEntry moduleBase = CreateSubtitleExport(package, "ModuleBase", "SFXSimpleUseModule");
            module.Archetype = moduleArchetype;
            moduleArchetype.Archetype = moduleBase;
            module.WriteProperty(new StringRefProperty(0, "m_srGameName"));
            moduleBase.WriteProperty(new StringRefProperty(692098, "m_srGameName"));
            actor.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(module)], "Modules"));
        }

        using var treeEntry = new TreeViewEntry(actor);

        Assert.AreEqual(expected, treeEntry.SubText);
    }

    [TestMethod]
    public void SimpleUseModuleSubtitleHidesUnresolvedNamesAndStopsAtArchetypeCycles()
    {
        using var subtitles = new SubtitleTestSettings();
        using IMEPackage package = CreateGameNamePackage();
        ExportEntry missingNameModule = CreateSubtitleExport(package, "MissingNameModule", "SFXSimpleUseModule");
        missingNameModule.WriteProperty(new StringRefProperty(123456789, "m_srGameName"));
        ExportEntry cyclicModule = CreateSubtitleExport(package, "CyclicModule", "SFXSimpleUseModule");
        ExportEntry cyclicArchetype = CreateSubtitleExport(package, "CyclicTemplate", "SFXSimpleUseModule");
        cyclicModule.Archetype = cyclicArchetype;
        cyclicArchetype.Archetype = cyclicModule;

        using var missingNameTreeEntry = new TreeViewEntry(missingNameModule);
        using var cyclicTreeEntry = new TreeViewEntry(cyclicModule);

        Assert.IsNull(missingNameTreeEntry.SubText);
        Assert.IsNull(cyclicTreeEntry.SubText);
    }

    private static ExportEntry CreateSubtitleExport(IMEPackage package, string name, string className, IEntry parent = null)
    {
        // These synthetic imports keep the fixtures independent of installed game packages.
        package.GetEntryOrAddImport($"SubtitleTestClasses.{className}", "Class");
        return package.CreateExport(name, className, parent, indexed: false);
    }

    private static IMEPackage CreateGameNamePackage()
    {
        IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("GameNameSubtitleTest.pcc", MEGame.LE1);
        ExportEntry talkFile = package.CreateExport("tlk", "BioTlkFile", indexed: false);
        var compressor = new HuffmanCompression();
        compressor.LoadInputData([
            new TLKStringRef(692097, "Local game name"),
            new TLKStringRef(692098, "Module template game name"),
            new TLKStringRef(692099, "Actor template game name")]);
        compressor.SerializeTalkfileToExport(talkFile);
        package.LocalTalkFiles.Clear();
        package.LocalTalkFiles.Add(new ME1TalkFile(talkFile));
        return package;
    }

    private sealed class SubtitleTestSettings : IDisposable
    {
        private readonly bool _previousShowSubtitles = Settings.PackageEditor_ShowTreeEntrySubText;
        private readonly bool _previousParseUnknownArraysAsObjects = LegendaryExplorerCoreLibSettings.Instance.ParseUnknownArrayTypesAsObject;

        public SubtitleTestSettings()
        {
            Settings.PackageEditor_ShowTreeEntrySubText = true;
            // The LE1 fixtures use synthetic SFX classes so their module references have no game metadata.
            LegendaryExplorerCoreLibSettings.Instance.ParseUnknownArrayTypesAsObject = true;
        }

        public void Dispose()
        {
            Settings.PackageEditor_ShowTreeEntrySubText = _previousShowSubtitles;
            LegendaryExplorerCoreLibSettings.Instance.ParseUnknownArrayTypesAsObject = _previousParseUnknownArraysAsObjects;
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
