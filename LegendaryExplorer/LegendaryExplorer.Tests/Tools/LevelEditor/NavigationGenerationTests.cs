using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Pathing;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class NavigationGenerationTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void CollisionScene_RaycastAndCapsuleClearanceUseTriangleGeometry()
    {
        LevelCollisionScene scene = CreateFloorAndWall();

        Assert.IsTrue(scene.Raycast(new Vector3(0, 0, 100), -Vector3.UnitZ, 200, out LevelCollisionHit floor));
        Assert.AreEqual(100f, floor.Distance, 0.001f);
        Assert.IsTrue(floor.Normal.Z > 0.99f);
        Assert.IsFalse(scene.OverlapCapsule(new Vector3(0, 0, 1), 10, 40));
        Assert.IsTrue(scene.OverlapCapsule(new Vector3(45, 0, 1), 10, 40));
    }

    [TestMethod]
    public void CollisionScene_SphereAndCapsuleSweepsRejectWallCrossing()
    {
        LevelCollisionScene scene = CreateFloorAndWall();

        Assert.IsTrue(scene.SphereCast(new Vector3(0, 0, 20), new Vector3(100, 0, 20), 10, out _));
        Assert.IsTrue(scene.CapsuleSweep(new Vector3(0, 0, 1), new Vector3(100, 0, 1), 10, 40, out _));
        Assert.IsFalse(scene.CapsuleSweep(new Vector3(-80, -80, 1), new Vector3(0, -80, 1), 10, 40, out _));
    }

    [TestMethod]
    public void Generator_ProducesConnectedPrunedGraphOnWalkableFloor()
    {
        LevelCollisionScene scene = LevelCollisionScene.FromTriangles(CreateQuad(-192, -192, 192, 192, 0));
        var settings = new NavigationGenerationSettings
        {
            PawnRadius = 10,
            PawnHeight = 20,
            GridSpacing = 64,
            ConnectionDistance = 100,
            MaximumStepUp = 20,
            MaximumStepDown = 20,
            MaximumSafeDrop = 60,
            GenerateCover = false,
            GenerationRadius = 0
        };

        NavigationGenerationResult generated = new NavigationGenerator(scene, settings).Generate(Vector3.Zero);

        Assert.IsTrue(generated.Nodes.Count >= 2);
        Assert.IsTrue(generated.Nodes.Count < 49, "The simplifier should remove redundant samples.");
        Assert.IsTrue(generated.Edges.Count > 0);
        Assert.IsTrue(generated.Edges.All(edge => edge.StartNode >= 0 && edge.StartNode < generated.Nodes.Count &&
                                                        edge.EndNode >= 0 && edge.EndNode < generated.Nodes.Count));
        AssertGraphConnected(generated);
    }

    [TestMethod]
    public void Generator_AcceptsWalkableSlopeWithoutTreatingItAsOverheadCollision()
    {
        const float rise = 96;
        LevelCollisionScene scene = LevelCollisionScene.FromTriangles(
        [
            (new Vector3(-128, -128, 0), new Vector3(128, -128, rise), new Vector3(128, 128, rise)),
            (new Vector3(-128, -128, 0), new Vector3(128, 128, rise), new Vector3(-128, 128, 0))
        ]);
        var settings = new NavigationGenerationSettings
        {
            PawnRadius = 10,
            PawnHeight = 20,
            GridSpacing = 64,
            ConnectionDistance = 100,
            MaximumSlopeDegrees = 45,
            MaximumStepUp = 30,
            MaximumStepDown = 30,
            MaximumSafeDrop = 60,
            GenerateCover = false,
            GenerationRadius = 0
        };

        NavigationGenerationResult generated = new NavigationGenerator(scene, settings).Generate(Vector3.Zero);

        Assert.IsTrue(generated.Nodes.Count >= 2);
        Assert.IsTrue(generated.Nodes.Any(node => node.FloorNormal.Z < 0.99f));
        Assert.IsTrue(generated.Edges.Count > 0);
    }

    [TestMethod]
    public void Generator_CreatesDirectionalConnectionForSafeDrop()
    {
        var triangles = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        triangles.AddRange(CreateQuad(-192, -128, 0, 128, 60));
        triangles.AddRange(CreateQuad(0, -128, 192, 128, 0));
        LevelCollisionScene scene = LevelCollisionScene.FromTriangles(triangles);
        var settings = new NavigationGenerationSettings
        {
            PawnRadius = 10,
            PawnHeight = 20,
            GridSpacing = 64,
            ConnectionDistance = 140,
            MaximumStepUp = 20,
            MaximumStepDown = 20,
            MaximumSafeDrop = 80,
            GenerateCover = false,
            GenerationRadius = 0
        };

        NavigationGenerationResult generated = new NavigationGenerator(scene, settings).Generate(Vector3.Zero);

        Assert.IsTrue(generated.Edges.Any(edge =>
            generated.Nodes[edge.StartNode].Position.Z - generated.Nodes[edge.EndNode].Position.Z > 40));
        Assert.IsFalse(generated.Edges.Any(edge =>
            generated.Nodes[edge.EndNode].Position.Z - generated.Nodes[edge.StartNode].Position.Z > 40));
    }

    [TestMethod]
    public void Serializer_WritesNavigationCoverAndReachSpecsToLevelBinary()
    {
        string packagePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "LegendaryExplorer", "Resources", "exec", "ME3AnimViewer.pcc"));
        using IMEPackage package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        ExportEntry levelExport = package.Exports.FirstOrDefault(export => export.ClassName == "Level");
        Assert.IsNotNull(levelExport);
        var generated = new NavigationGenerationResult(
            [
                new GeneratedNavigationNode(new Vector3(0, 0, 1), Vector3.UnitZ),
                new GeneratedNavigationNode(new Vector3(100, 0, 1), Vector3.UnitZ)
            ],
            [new GeneratedNavigationEdge(0, 1), new GeneratedNavigationEdge(1, 0)],
            [
                new GeneratedCoverLink(new Vector3(0, 50, 1), Vector3.UnitY,
                [
                    new GeneratedCoverSlot(new Vector3(0, 50, 1), Vector3.UnitY, true, true, false, 0),
                    new GeneratedCoverSlot(new Vector3(100, 50, 1), Vector3.UnitY, false, false, true, 1)
                ])
            ], 2, 0, 0);
        var settings = new NavigationGenerationSettings
        {
            PawnRadius = 34,
            PawnHeight = 90,
            GridSpacing = 128,
            ConnectionDistance = 200
        };

        NavigationSerializationResult serialized = NavigationSerializer.Write(package, levelExport, generated, settings);

        Assert.AreEqual(2, serialized.PathNodeCount);
        Assert.AreEqual(1, serialized.CoverLinkCount);
        Assert.AreEqual(2, serialized.CoverSlotCount);
        Assert.IsTrue(serialized.ReachSpecCount >= 12,
            $"Reported {serialized.ReachSpecCount}; generated classes: {string.Join(", ", package.Exports.TakeLast(20).Select(export => export.ClassName))}");
        Level level = levelExport.GetBinaryData<Level>();
        Assert.IsTrue(level.NavListStart > 0);
        Assert.IsTrue(level.NavListEnd > 0);
        Assert.IsTrue(level.CoverListStart > 0);
        Assert.IsTrue(level.CoverListEnd > 0);
        Assert.AreEqual(1, level.CoverLinkRefs.Count);

        ExportEntry pathNode = package.Exports.First(export => export.ClassName == "PathNode");
        Assert.IsTrue(pathNode.GetProperty<ArrayProperty<ObjectProperty>>("PathList")?.Count > 0);
        ExportEntry otherPathNode = package.Exports.First(export =>
            export.ClassName == "PathNode" && !ReferenceEquals(export, pathNode));
        PathTools.CreateReachSpec(pathNode, false, otherPathNode, "Engine.ReachSpec", 34, 90);
        Assert.AreEqual("ReachSpec", package.Exports[^1].ClassName);
        ExportEntry coverLink = package.Exports.First(export => export.ClassName == "CoverLink");
        ArrayProperty<StructProperty> slots = coverLink.GetProperty<ArrayProperty<StructProperty>>("Slots");
        Assert.AreEqual(2, slots?.Count);
        Assert.AreEqual("CT_Standing", slots[0].GetProp<EnumProperty>("ForceCoverType")?.Value.Name);
        List<ExportEntry> markers = package.Exports.Where(export => export.ClassName == "CoverSlotMarker").ToList();
        Assert.AreEqual(2, markers.Count);
        ExportEntry marker = markers[0];
        Assert.AreEqual(coverLink.UIndex,
            marker.GetProperty<StructProperty>("OwningSlot")?.GetProp<ObjectProperty>("Link")?.Value);

        Assert.AreEqual(2, GetSlotDirection(package, markers[0]));
        Assert.AreEqual(1, GetSlotDirection(package, markers[1]));

        var chain = new HashSet<int>();
        int chainIndex = level.NavListStart;
        while (chainIndex > 0 && chain.Add(chainIndex))
            chainIndex = package.GetUExport(chainIndex).GetProperty<ObjectProperty>("nextNavigationPoint")?.Value ?? 0;
        Assert.IsTrue(chain.Contains(coverLink.UIndex));
        Assert.IsTrue(markers.All(item => chain.Contains(item.UIndex)));

        int previousNavigationEnd = level.NavListEnd;
        int previousCoverEnd = level.CoverListEnd;
        var appended = new NavigationGenerationResult(
            [new GeneratedNavigationNode(new Vector3(250, 0, 1), Vector3.UnitZ)], [],
            [
                new GeneratedCoverLink(new Vector3(250, 50, 1), Vector3.UnitY,
                [new GeneratedCoverSlot(new Vector3(250, 50, 1), Vector3.UnitY, true, true, true, 0)])
            ], 1, 0, 0);
        NavigationSerializer.Write(package, levelExport, appended, settings);
        Level appendedLevel = levelExport.GetBinaryData<Level>();
        ExportEntry appendedPathNode = package.Exports.Last(export => export.ClassName == "PathNode");
        ExportEntry appendedCoverLink = package.Exports.Last(export => export.ClassName == "CoverLink");
        Assert.AreEqual(appendedPathNode.UIndex,
            package.GetUExport(previousNavigationEnd)
                .GetProperty<ObjectProperty>("nextNavigationPoint")?.Value);
        Assert.AreEqual(appendedCoverLink.UIndex,
            package.GetUExport(previousCoverEnd).GetProperty<ObjectProperty>("NextCoverLink")?.Value);
        Assert.AreEqual(2, appendedLevel.CoverLinkRefs.Count);

        using var saved = package.SaveToStream(false);
        saved.Position = 0;
        using IMEPackage reopened = MEPackageHandler.OpenMEPackageFromStream(saved, "NavigationSerializationTest.pcc");
        ExportEntry reopenedLevel = reopened.Exports.FirstOrDefault(export => export.ClassName == "Level");
        Assert.IsNotNull(reopenedLevel);
        Assert.AreEqual(2, reopenedLevel.GetBinaryData<Level>().CoverLinkRefs.Count);
        Assert.AreEqual(3, reopened.Exports.Count(export => export.ClassName == "PathNode"));
        Assert.AreEqual(2, reopened.Exports.Count(export => export.ClassName == "CoverLink"));
        Assert.AreEqual(3, reopened.Exports.Count(export => export.ClassName == "CoverSlotMarker"));
    }

    private static LevelCollisionScene CreateFloorAndWall()
    {
        var triangles = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        triangles.AddRange(CreateQuad(-100, -100, 100, 100, 0));
        triangles.Add((new Vector3(50, -100, 0), new Vector3(50, 100, 0), new Vector3(50, 100, 100)));
        triangles.Add((new Vector3(50, -100, 0), new Vector3(50, 100, 100), new Vector3(50, -100, 100)));
        return LevelCollisionScene.FromTriangles(triangles);
    }

    private static IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> CreateQuad(
        float minimumX, float minimumY, float maximumX, float maximumY, float z)
    {
        Vector3 a = new(minimumX, minimumY, z);
        Vector3 b = new(maximumX, minimumY, z);
        Vector3 c = new(maximumX, maximumY, z);
        Vector3 d = new(minimumX, maximumY, z);
        yield return (a, b, c);
        yield return (a, c, d);
    }

    private static void AssertGraphConnected(NavigationGenerationResult generated)
    {
        var adjacency = Enumerable.Range(0, generated.Nodes.Count).Select(_ => new List<int>()).ToArray();
        foreach (GeneratedNavigationEdge edge in generated.Edges)
        {
            adjacency[edge.StartNode].Add(edge.EndNode);
            adjacency[edge.EndNode].Add(edge.StartNode);
        }

        var visited = new HashSet<int> { 0 };
        var queue = new Queue<int>();
        queue.Enqueue(0);
        while (queue.TryDequeue(out int current))
        {
            foreach (int neighbor in adjacency[current])
            {
                if (visited.Add(neighbor)) queue.Enqueue(neighbor);
            }
        }
        Assert.AreEqual(generated.Nodes.Count, visited.Count);
    }

    private static byte GetSlotDirection(IMEPackage package, ExportEntry marker)
    {
        ObjectProperty reference = marker.GetProperty<ArrayProperty<ObjectProperty>>("PathList")
            ?.FirstOrDefault(item => package.IsUExport(item.Value) &&
                                    package.GetUExport(item.Value).ClassName == "SlotToSlotReachSpec");
        return reference is null ? (byte)0 :
            package.GetUExport(reference.Value).GetProperty<ByteProperty>("SpecDirection")?.Value ?? 0;
    }
}
