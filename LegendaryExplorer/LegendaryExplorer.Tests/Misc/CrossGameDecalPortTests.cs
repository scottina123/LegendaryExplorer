using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LegendaryExplorerCore;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Misc;

/// <summary>
/// Static decals ported into LE3 from another game lose their stored receivers (the source game's pre-clipped geometry and lighting),
/// so LE3 projects them at load. LE2's Udina leaf decals stored geometry covering whole walls, which LE3 drew as large dark shapes.
/// </summary>
[TestClass]
public class CrossGameDecalPortTests
{
    private static string UdinaLE2 => Path.Combine(LE2Directory.CookedPCPath ?? "", "BioA_CitHub_500Udina.pcc");

    [TestInitialize]
    public void Setup()
    {
        if (!File.Exists(UdinaLE2)) Assert.Inconclusive($"{UdinaLE2} wasn't found.");
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), _ => { });
    }

    [TestMethod]
    public void LE2ToLE3_DropsStoredDecalReceivers()
    {
        var (source, ported) = PortDecalActor(MEGame.LE3, "DecalActor_18");
        using (source)
        using (var dest = ported.FileRef)
        {
            var sourceComponent = DecalComponentOf(source, "DecalActor_18");
            var sourceReceivers = ObjectBinary.From<DecalComponent>(sourceComponent).StaticReceivers;
            Assert.IsTrue(sourceReceivers.Length > 0, "The LE2 decal has no stored receivers to drop.");
            // Porting an actor brings much of its level along: every decal ported into LE3 must have lost its stored receivers
            var decals = dest.Exports.Where(e => e.ClassName == "DecalComponent" && !e.IsDefaultObject).ToList();
            Assert.IsTrue(decals.Any(d => d.Parent?.ObjectName.Instanced == "DecalActor_18"), "DecalActor_18's component wasn't ported.");
            foreach (var decal in decals)
                Assert.AreEqual(0, ObjectBinary.From<DecalComponent>(decal).StaticReceivers.Length, $"{decal.InstancedFullPath}: stored receivers were ported into LE3.");
            // The shadow maps only the stored receivers referenced aren't ported
            var decalShadowMaps = sourceReceivers.SelectMany(r => r.ShadowMap1D ?? []).Select(u => source.GetEntry(u)?.InstancedFullPath).ToList();
            foreach (string path in decalShadowMaps)
                Assert.IsNull(dest.FindExport(path), $"{path}, which only the dropped receivers used, was still ported.");
        }
    }

    [TestMethod]
    public void SameGame_KeepsStoredDecalReceivers()
    {
        var (source, ported) = PortDecalActor(MEGame.LE2, "DecalActor_18");
        using (source)
        using (var dest = ported.FileRef)
        {
            Assert.IsTrue(ObjectBinary.From<DecalComponent>(DecalComponentOf(dest, "DecalActor_18")).StaticReceivers.Length > 0, "A same-game port dropped the stored receivers.");
        }
    }

    private static ExportEntry DecalComponentOf(IMEPackage package, string actorName) =>
        package.Exports.Single(e => e.ClassName == "DecalComponent" && !e.IsDefaultObject && e.Parent?.ObjectName.Instanced == actorName);

    private static (IMEPackage Source, ExportEntry Ported) PortDecalActor(MEGame destGame, string actorName)
    {
        var source = MEPackageHandler.OpenMEPackage(UdinaLE2, forceLoadFromDisk: true);
        var actor = source.Exports.Single(e => e.ClassName == "DecalActor" && e.ObjectName.Instanced == actorName);
        var dest = MEPackageHandler.CreateMemoryEmptyPackage($"LEXDecalPort_{destGame}.pcc", destGame);
        var rop = new RelinkerOptionsPackage { IsCrossGame = destGame != source.Game, Cache = new PackageCache(), ImportExportDependencies = true, PortImportsMemorySafe = true };
        EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies, actor, dest, null, true, rop, out IEntry newEntry);
        return (source, (ExportEntry)newEntry);
    }
}
