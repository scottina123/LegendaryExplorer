using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class StaticCollectionActorTests
{
    [TestMethod]
    public void EmptyCollectionActorsHaveEmptyComponentLists()
    {
        GlobalTest.Init();

        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("EmptyCollectionActors.pcc", MEGame.LE3);
        ExportEntry meshCollection = package.CreateExport("EmptyMeshCollection", "StaticMeshCollectionActor", indexed: false);
        ExportEntry lightCollection = package.CreateExport("EmptyLightCollection", "StaticLightCollectionActor", indexed: false);
        lightCollection.WriteProperty(new ArrayProperty<ObjectProperty>("LightComponents"));

        StaticMeshCollectionActor meshBinary = meshCollection.GetBinaryData<StaticMeshCollectionActor>();
        StaticLightCollectionActor lightBinary = lightCollection.GetBinaryData<StaticLightCollectionActor>();

        Assert.IsNotNull(meshBinary.Components);
        Assert.HasCount(0, meshBinary.Components);
        Assert.IsNotNull(meshBinary.LocalToWorldTransforms);
        Assert.HasCount(0, meshBinary.LocalToWorldTransforms);
        Assert.IsNotNull(lightBinary.Components);
        Assert.HasCount(0, lightBinary.Components);
        Assert.IsNotNull(lightBinary.LocalToWorldTransforms);
        Assert.HasCount(0, lightBinary.LocalToWorldTransforms);
    }
}
