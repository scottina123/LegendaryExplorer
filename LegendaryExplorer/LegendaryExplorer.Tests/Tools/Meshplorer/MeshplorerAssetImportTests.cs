using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.Tools.Meshplorer;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.Meshplorer;

[TestClass]
public class MeshplorerAssetImportTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void AssetQueueImportsBothMeshTypesAndContinuesAfterInvalidAssets()
    {
        string path = Path.Combine(Path.GetTempPath(), $"MeshImport-{Guid.NewGuid():N}.pcc");
        try
        {
            int staticIndex, skeletalIndex, nonMeshIndex;
            using (var source = MEPackageHandler.CreateMemoryEmptyPackage(path, MEGame.LE3))
            {
                var parent = source.CreateExport(new NameReference("Folder", 2), "Package", indexed: false);
                var staticMesh = source.CreateExport("Static", "StaticMesh", indexed: false);
                staticMesh.Parent = parent;
                var child = source.CreateExport("Child", "Object", indexed: false);
                child.Parent = staticMesh;
                var staticBinary = StaticMesh.Create();
                staticBinary.BodySetup = child.UIndex;
                staticMesh.WriteBinary(staticBinary);
                var dependency = source.CreateExport("Dependency", "Object", indexed: false);
                var skeletalMesh = source.CreateExport("Skeletal", "SkeletalMesh", indexed: false);
                skeletalMesh.Parent = parent;
                var skeletalBinary = SkeletalMesh.Create();
                skeletalBinary.Materials = [dependency.UIndex];
                skeletalMesh.WriteBinary(skeletalBinary);
                staticIndex = staticMesh.UIndex;
                skeletalIndex = skeletalMesh.UIndex;
                nonMeshIndex = dependency.UIndex;
                source.Save(path);
            }
            var originalBytes = File.ReadAllBytes(path);
            AssetDatabaseWindow.AssetImportQueueItem Item(string name, int index, string file = null) => new()
            {
                DisplayName = name, AssetType = "Mesh", ResolvedFilePath = file ?? path, UIndex = index
            };
            var validItems = new[] { Item("Static", staticIndex), Item("Skeletal", skeletalIndex) };
            using var destination = MEPackageHandler.CreateMemoryEmptyPackage("Destination.pcc", MEGame.LE3);
            destination.CreateExport("Padding", "Object", indexed: false);
            var (meshes, issues) = MeshplorerWindow.ImportMeshAssets(destination,
            [
                validItems[0],
                Item("Non-mesh", nonMeshIndex),
                Item("Stale export", int.MaxValue),
                Item("Missing file", 1, path + ".missing"),
                validItems[1]
            ]);

            Assert.HasCount(2, meshes);
            Assert.HasCount(3, issues);
            CollectionAssert.AreEqual(new[] { "StaticMesh", "SkeletalMesh" }, meshes.Select(mesh => mesh.ClassName).ToArray());
            Assert.IsTrue(meshes.All(mesh => mesh.ParentInstancedFullPath == "Folder_1"));
            var clonedChild = meshes[0].GetAllDescendants().Single();
            Assert.AreEqual(clonedChild.UIndex, ObjectBinary.From<StaticMesh>(meshes[0]).BodySetup);
            Assert.AreEqual(destination.FindExport("Dependency").UIndex, ObjectBinary.From<SkeletalMesh>(meshes[1]).Materials.Single());
            Assert.IsTrue(destination.IsModified);

            var (repeated, repeatedIssues) = MeshplorerWindow.ImportMeshAssets(destination, validItems);
            Assert.IsEmpty(repeatedIssues);
            Assert.HasCount(2, repeated);
            Assert.IsTrue(repeated.All(mesh => mesh.ObjectName.Number == 1));
            Assert.HasCount(4, meshes.Concat(repeated).Select(mesh => mesh.InstancedFullPath).Distinct());

            using var wrongGame = MEPackageHandler.CreateMemoryEmptyPackage("WrongGame.pcc", MEGame.LE2);
            var (rejected, wrongGameIssues) = MeshplorerWindow.ImportMeshAssets(wrongGame, validItems);
            Assert.IsEmpty(rejected);
            Assert.HasCount(2, wrongGameIssues);
            Assert.AreEqual(0, wrongGame.ExportCount);

            // An asset can also come from the PCC already open as the destination.
            using var samePackage = MEPackageHandler.OpenMEPackage(path);
            var (localClones, localIssues) = MeshplorerWindow.ImportMeshAssets(samePackage, validItems);
            Assert.IsEmpty(localIssues);
            Assert.HasCount(2, localClones);
            Assert.IsTrue(localClones.All(mesh => mesh.FileRef == samePackage && mesh.ObjectName.Number == 1));
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(path), "Importing must not save or modify the source file on disk.");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
