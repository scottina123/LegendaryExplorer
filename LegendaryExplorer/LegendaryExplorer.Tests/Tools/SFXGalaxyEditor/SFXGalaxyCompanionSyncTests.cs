using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.SFXGalaxyEditor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.SFXGalaxyEditor;

[TestClass]
public class SFXGalaxyCompanionSyncTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void FullSyncIncludesSharedPlanetAndCloudMaterialsOutsideGalaxyExactlyOnce()
    {
        using var sourcePackage = MEPackageHandler.CreateMemoryEmptyPackage("GalaxyMap.pcc", MEGame.LE3);
        using var companionPackage = MEPackageHandler.CreateMemoryEmptyPackage("CIC.pcc", MEGame.LE3);
        var sourceGalaxy = sourcePackage.CreateExport("GalaxyMap", "SFXGalaxy", indexed: false);
        var companionGalaxy = companionPackage.CreateExport("GalaxyMap", "SFXGalaxy", indexed: false);
        var cluster = sourcePackage.CreateExport("Cluster", "SFXCluster", sourceGalaxy, indexed: false);
        var system = sourcePackage.CreateExport("System", "SFXSystem", cluster, indexed: false);
        var firstPlanet = sourcePackage.CreateExport("FirstPlanet", "BioPlanet", system, indexed: false);
        var secondPlanet = sourcePackage.CreateExport("SecondPlanet", "BioPlanet", system, indexed: false);
        var thirdPlanet = sourcePackage.CreateExport("ThirdPlanet", "BioPlanet", system, indexed: false);
        // Retail LE3 keeps these referenced materials outside the galaxy's outer hierarchy.
        var planetMaterial = sourcePackage.CreateExport("PlanetMaterial", "MaterialInstanceConstant", indexed: false);
        var cloudMaterial = sourcePackage.CreateExport("CloudMaterial", "MaterialInstanceConstant", indexed: false);
        var ownedMaterial = sourcePackage.CreateExport("OwnedMaterial", "MaterialInstanceConstant", firstPlanet, indexed: false);
        WriteMaterials(firstPlanet, planetMaterial, cloudMaterial);
        WriteMaterials(secondPlanet, planetMaterial, cloudMaterial);
        WriteMaterials(thirdPlanet, ownedMaterial, planetMaterial);
        var sourceData = sourcePackage.Exports.ToDictionary(export => export, export => export.Data.ToArray());

        var exports = SFXGalaxyEditorWindow.PrepareFullGalaxySync(sourceGalaxy, companionGalaxy);

        Assert.AreSame(sourceGalaxy, exports[0]);
        CollectionAssert.AreEquivalent(new[]
        {
            sourceGalaxy, cluster, system, firstPlanet, secondPlanet, thirdPlanet,
            ownedMaterial, planetMaterial, cloudMaterial
        }, exports);
        Assert.AreEqual(exports.Count, exports.Select(export => export.UIndex).Distinct().Count());
        foreach (var (export, data) in sourceData)
        {
            CollectionAssert.AreEqual(data, export.Data);
        }
    }

    [TestMethod]
    public void FullSyncIgnoresMissingNullImportedInvalidAndTrashedMaterialReferences()
    {
        using var sourcePackage = MEPackageHandler.CreateMemoryEmptyPackage("GalaxyMap.pcc", MEGame.LE3);
        using var companionPackage = MEPackageHandler.CreateMemoryEmptyPackage("CIC.pcc", MEGame.LE3);
        var sourceGalaxy = sourcePackage.CreateExport("GalaxyMap", "SFXGalaxy", indexed: false);
        var companionGalaxy = companionPackage.CreateExport("GalaxyMap", "SFXGalaxy", indexed: false);
        var missingPlanet = sourcePackage.CreateExport("MissingMaterials", "BioPlanet", sourceGalaxy, indexed: false);
        var nullPlanet = sourcePackage.CreateExport("NullMaterials", "BioPlanet", sourceGalaxy, indexed: false);
        var importedPlanet = sourcePackage.CreateExport("ImportedMaterials", "BioPlanet", sourceGalaxy, indexed: false);
        var invalidPlanet = sourcePackage.CreateExport("InvalidMaterials", "BioPlanet", sourceGalaxy, indexed: false);
        var trashedPlanet = sourcePackage.CreateExport("TrashedMaterials", "BioPlanet", sourceGalaxy, indexed: false);
        var importedMaterial = sourcePackage.CreateImport("MaterialInstanceConstant", "ImportedMaterial");
        var trashPackage = sourcePackage.CreatePackageExport(UnrealPackageFile.TrashPackageName);
        var trashedMaterial = sourcePackage.CreateExport("Trash", "MaterialInstanceConstant", trashPackage, indexed: false);
        WriteMaterials(nullPlanet, null, null);
        WriteMaterials(importedPlanet, importedMaterial, importedMaterial);
        invalidPlanet.WriteProperties(new PropertyCollection
        {
            new ObjectProperty(int.MaxValue, "PlanetMaterial"),
            new ObjectProperty(int.MinValue + 1, "CloudMaterial")
        });
        WriteMaterials(trashedPlanet, trashedMaterial, trashedMaterial);
        Assert.IsTrue(trashedMaterial.IsTrash());

        var exports = SFXGalaxyEditorWindow.PrepareFullGalaxySync(sourceGalaxy, companionGalaxy);

        CollectionAssert.AreEquivalent(new[]
        {
            sourceGalaxy, missingPlanet, nullPlanet, importedPlanet, invalidPlanet, trashedPlanet
        }, exports);
    }

    [TestMethod]
    public void FullSyncPrunesCompanionOnlySubtreeAndPreservesMatchingAndExternalExports()
    {
        using var sourcePackage = MEPackageHandler.CreateMemoryEmptyPackage("GalaxyMap.pcc", MEGame.LE3);
        using var companionPackage = MEPackageHandler.CreateMemoryEmptyPackage("CIC.pcc", MEGame.LE3);
        var sourceGalaxy = sourcePackage.CreateExport("GalaxyMap", "SFXGalaxy", indexed: false);
        var sourceCluster = sourcePackage.CreateExport("KeepCluster", "SFXCluster", sourceGalaxy, indexed: false);
        var sourceSystem = sourcePackage.CreateExport("KeepSystem", "SFXSystem", sourceCluster, indexed: false);
        var companionGalaxy = companionPackage.CreateExport("GalaxyMap", "SFXGalaxy", indexed: false);
        var companionCluster = companionPackage.CreateExport("KeepCluster", "SFXCluster", companionGalaxy, indexed: false);
        var companionSystem = companionPackage.CreateExport("KeepSystem", "SFXSystem", companionCluster, indexed: false);
        var extraCluster = companionPackage.CreateExport("ExtraCluster", "SFXCluster", companionGalaxy, indexed: false);
        var extraSystem = companionPackage.CreateExport("ExtraSystem", "SFXSystem", extraCluster, indexed: false);
        var extraPlanet = companionPackage.CreateExport("ExtraPlanet", "BioPlanet", extraSystem, indexed: false);
        var externalExport = companionPackage.CreateExport("ExternalObject", "Object", indexed: false);
        companionSystem.WriteProperty(new IntProperty(123, "PosX"));
        var preservedData = companionSystem.Data.ToArray();
        var externalData = externalExport.Data.ToArray();

        var exports = SFXGalaxyEditorWindow.PrepareFullGalaxySync(sourceGalaxy, companionGalaxy);

        CollectionAssert.AreEquivalent(new[] { sourceGalaxy, sourceCluster, sourceSystem }, exports);
        Assert.IsTrue(extraCluster.IsTrash());
        Assert.IsTrue(extraSystem.IsTrash());
        Assert.IsTrue(extraPlanet.IsTrash());
        Assert.IsFalse(companionGalaxy.IsTrash());
        Assert.IsFalse(companionCluster.IsTrash());
        Assert.IsFalse(companionSystem.IsTrash());
        Assert.AreSame(companionGalaxy, companionCluster.Parent);
        Assert.AreSame(companionCluster, companionSystem.Parent);
        CollectionAssert.AreEqual(preservedData, companionSystem.Data);
        Assert.IsFalse(externalExport.IsTrash());
        CollectionAssert.AreEqual(externalData, externalExport.Data);
    }

    private static void WriteMaterials(ExportEntry planet, IEntry planetMaterial, IEntry cloudMaterial) =>
        planet.WriteProperties(new PropertyCollection
        {
            new ObjectProperty(planetMaterial?.UIndex ?? 0, "PlanetMaterial"),
            new ObjectProperty(cloudMaterial?.UIndex ?? 0, "CloudMaterial")
        });
}
