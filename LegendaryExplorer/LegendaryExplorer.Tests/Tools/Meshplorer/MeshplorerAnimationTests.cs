using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.Tools.Meshplorer;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Animation;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.Meshplorer;

[TestClass]
public class MeshplorerAnimationTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void CatalogIncludesModAndMissingSequencesWithSearchableNames()
    {
        var database = new AssetDB { Game = MEGame.LE3 };
        database.Animations.AddRange([
            new AnimationRecord { AnimSequence = "Walk", SeqName = "Forward", AnimData = "Human" },
            new AnimationRecord { AnimSequence = "Custom", IsModOnly = true },
            new AnimationRecord { AnimSequence = "Performance", IsAmbPerf = true },
        ]);
        var catalog = new MeshplorerAnimationCatalog(database, new Dictionary<int, string>(), DateTime.MinValue);

        CollectionAssert.AreEqual(new[] { "Custom", "Walk" }, catalog.Entries.Select(e => e.Record.AnimSequence).ToArray());
        StringAssert.Contains(catalog.Entries[1].ToString(), "Forward");
        StringAssert.Contains(catalog.Entries[1].ToString(), "Human");
    }

    [TestMethod]
    public async Task CatalogRejectsStaleExportAndTriesAnotherUsage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"MeshplorerAnimation-{Guid.NewGuid():N}.pcc");
        try
        {
            int wrongIndex;
            int animationIndex;
            using (IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage(path, MEGame.LE3))
            {
                wrongIndex = package.CreateExport("DifferentAnimation", "AnimSequence", indexed: false).UIndex;
                AnimSequence animation = CreateAnimation(package);
                animationIndex = animation.Export.UIndex;
                ExportEntry data = package.CreateExport("WalkData", "BioAnimSetData", indexed: false);
                data.WriteProperty(new ArrayProperty<NameProperty>([new NameProperty("root")], "TrackBoneNames"));
                animation.Export.WriteProperty(new ObjectProperty(data, "m_pBioAnimSetData"));
                PropertyCollection properties = animation.Export.GetProperties();
                animation.UpdateProps(properties, MEGame.LE3);
                animation.Export.WritePropertiesAndBinary(properties, animation);
                package.Save(path);
            }
            var record = new AnimationRecord { AnimSequence = "Walk", Usages = [new(0, wrongIndex, false), new(0, animationIndex, false)] };
            var database = new AssetDB { Game = MEGame.LE3, Animations = [record] };
            var catalog = new MeshplorerAnimationCatalog(database, new Dictionary<int, string> { [0] = path }, DateTime.MinValue);
            byte[] original = File.ReadAllBytes(path);
            using (var loaded = await catalog.LoadAnimationAsync(catalog.Entries[0], CancellationToken.None))
            {
                Assert.AreEqual(animationIndex, loaded.Sequence.Export.UIndex);
                Assert.AreEqual(2, loaded.Sequence.NumFrames);
            }
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));

            record.Usages.RemoveAt(1);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.LoadAnimationAsync(catalog.Entries[0], CancellationToken.None));
            StringAssert.Contains(error.Message, "indexed export has changed");
            await Assert.ThrowsAsync<TaskCanceledException>(() => catalog.LoadAnimationAsync(catalog.Entries[0], new CancellationToken(true)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [STATestMethod]
    public void PreviewLoopsPausesScrubsAndReappliesToAnotherSkeletonWithoutEditingPackages()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("AnimationPreview.pcc", MEGame.LE3);
        AnimSequence animation = CreateAnimation(package);
        byte[] original = animation.Export.Data.ToArray();
        MeshRenderer renderer = CreateRenderer();
        try
        {
            // Queue animation before the asynchronous viewport mesh load finishes.
            renderer.SetPreviewAnimation(animation);
            Assert.IsFalse(renderer.HasPreviewAnimation);
            InitializeMesh(renderer, "root");
            Assert.IsTrue(renderer.HasPreviewAnimation);
            Assert.IsTrue(renderer.IsPreviewAnimationPlaying);
            Advance(renderer, 1.25f);
            Assert.AreEqual(0.25, renderer.AnimationPosition, 0.0001);
            renderer.TogglePreviewAnimationPlayback();
            Advance(renderer, 0.5f);
            Assert.AreEqual(0.25, renderer.AnimationPosition, 0.0001);

            renderer.AnimationPosition = 0.75;
            AnimSequencePlayer player = GetPlayer(renderer);
            player.ComputeSkinningMatrices();
            Assert.AreNotEqual(Matrix4x4.Identity, player.BoneComponentSpaceTransforms[0]);
            renderer.UnloadExport();
            Assert.IsFalse(renderer.HasPreviewAnimation);
            InitializeMesh(renderer, "ROOT");
            Assert.IsTrue(renderer.HasPreviewAnimation);
            Assert.IsFalse(renderer.IsPreviewAnimationPlaying);
            Assert.AreEqual(0.75, GetPlayer(renderer).CurrentTime, 0.0001);

            renderer.SetPreviewAnimation(null);
            Assert.IsFalse(renderer.HasPreviewAnimation);
            Assert.IsFalse(renderer.IsPreviewAnimationPlaying);
            Assert.AreEqual(0, renderer.AnimationPosition);
            CollectionAssert.AreEqual(original, animation.Export.Data);
        }
        finally { renderer.Dispose(); }
    }

    [STATestMethod]
    public void IncompatibleSkeletonReportsErrorAndCanRecover()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("AnimationPreview.pcc", MEGame.LE3);
        MeshRenderer renderer = CreateRenderer();
        try
        {
            InitializeMesh(renderer, "unrelated_bone");
            renderer.SetPreviewAnimation(CreateAnimation(package));
            Assert.IsFalse(renderer.HasPreviewAnimation);
            StringAssert.Contains(renderer.AnimationPreviewStatus, "no bones in common");
            InitializeMesh(renderer, "root");
            Assert.IsTrue(renderer.HasPreviewAnimation);
            Assert.IsNull(renderer.AnimationPreviewStatus);
        }
        finally { renderer.Dispose(); }
    }

    [TestMethod]
    [DataRow(MEGame.LE3)]
    [DataRow(MEGame.ME3)]
    public void SkinnedGameShaderGeometryUpdatesTangentAndNormalWhilePreservingUvs(MEGame game)
    {
        Fixed4<Vector4> uvs = default;
        uvs[0] = new Vector4(0.25f, 0.75f, 0, 0);
        var vertex = (LEVertex)LEVertex.Create(game, Vector3.Zero, Vector3.UnitX, new Vector4(0, 0, 1, -1), uvs);
        LEVertex skinned = vertex.WithSkinnedGeometry(game, new Vector3(5, 6, 7), Vector3.UnitY, new Vector4(1, 0, 0, -1));
        var expected = (LEVertex)LEVertex.Create(game, new Vector3(5, 6, 7), Vector3.UnitY, new Vector4(1, 0, 0, -1), uvs);
        float[] actualFloats = new float[LEVertex.Stride / 4];
        float[] expectedFloats = new float[LEVertex.Stride / 4];
        skinned.ToFloats(actualFloats);
        expected.ToFloats(expectedFloats);
        CollectionAssert.AreEqual(expectedFloats, actualFloats);
    }

    [TestMethod]
    [DataRow(MEGame.LE3)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.ME1)]
    public void SkinningUpdatesBothPreviewBuffersAndRestoresReferencePose(MEGame game)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("Skinning.pcc", MEGame.LE3);
        var player = new AnimSequencePlayer(new SkeletalMesh
        {
            RefSkeleton = [new MeshBone { Name = "root", Orientation = Quaternion.Identity, ParentIndex = 0 }],
        });
        player.SetAnimation(CreateAnimation(package));
        player.CurrentTime = 0.5f;
        var vertex = new GPUSkinVertex
        {
            Position = Vector3.UnitX,
            TangentX = (PackedNormal)new Vector4(1, 0, 0, 1),
            TangentZ = (PackedNormal)new Vector4(0, 0, 1, -1),
            InfluenceWeights = new Influences(255, 0, 0, 0),
            UV = new Vector2(0.25f, 0.75f),
        };
        var lod = new StaticLODModel
        {
            NumVertices = 1,
            Chunks = [new SkelMeshChunk { NumSoftVertices = 1, BoneMap = [0] }],
            VertexBufferGPUSkin = new SkeletalMeshVertexBuffer { VertexData = [vertex] },
            ME1VertexBufferGPUSkin = [new SoftSkinVertex
            {
                Position = vertex.Position, TangentX = vertex.TangentX, TangentZ = vertex.TangentZ,
                InfluenceWeights = vertex.InfluenceWeights, UV = vertex.UV,
            }],
        };
        var renderer = new LegacySkinnedMeshRenderer();
        renderer.BuildFromSkeletalMesh(game, lod);
        using var device = new SharpDX.Direct3D11.Device(SharpDX.Direct3D.DriverType.Warp,
            SharpDX.Direct3D11.DeviceCreationFlags.None);
        Fixed4<Vector4> uvs = default;
        uvs[0] = new Vector4(0.25f, 0.75f, 0, 0);
        using var standard = new Mesh<WorldVertex>(device, [new Triangle(0, 0, 0)],
            [new WorldVertex(-Vector3.UnitX, Vector3.UnitY, new Vector2(0.25f, 0.75f))]);
        using var shader = new Mesh<LEVertex>(device, [new Triangle(0, 0, 0)],
            [(LEVertex)LEVertex.Create(game, -Vector3.UnitX, -Vector3.UnitX, new Vector4(0, 1, 0, -1), uvs)]);
        renderer.UpdateSkinning(device.ImmediateContext, standard, player);
        renderer.UpdateSkinning(device.ImmediateContext, shader, player, game);
        Vector3 expected = new(-MathF.Sqrt(0.5f), 0, -MathF.Sqrt(0.5f));
        Assert.AreEqual(0, Vector3.Distance(expected, standard.Vertices[0].Position), 0.0001);
        Assert.AreEqual(0, Vector3.Distance(expected, shader.Vertices[0].Position), 0.0001);
        Assert.IsNotNull(standard.VertexBuffer);
        Assert.IsNotNull(shader.VertexBuffer);

        player.SetAnimation(null);
        renderer.UpdateSkinning(device.ImmediateContext, standard, player);
        renderer.UpdateSkinning(device.ImmediateContext, shader, player, game);
        Assert.AreEqual(-Vector3.UnitX, standard.Vertices[0].Position);
        Assert.AreEqual(-Vector3.UnitX, shader.Vertices[0].Position);
    }

    private static MeshRenderer CreateRenderer()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(MeshplorerWindow).Assembly);
        return new MeshRenderer();
    }

    private static void InitializeMesh(MeshRenderer renderer, string bone) =>
        typeof(MeshRenderer).GetMethod("InitializeAnimationPreview", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(renderer, [new SkeletalMesh
            {
                RefSkeleton = [new MeshBone { Name = bone, Orientation = Quaternion.Identity, ParentIndex = 0 }],
                LODModels = [],
            }]);

    private static void Advance(MeshRenderer renderer, float delta) =>
        typeof(MeshRenderer).GetMethod("UpdatePreviewAnimation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(renderer, [delta]);

    private static AnimSequencePlayer GetPlayer(MeshRenderer renderer) => (AnimSequencePlayer)typeof(MeshRenderer)
        .GetField("_previewAnimationPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;

    private static AnimSequence CreateAnimation(IMEPackage package) => new()
    {
        Export = package.CreateExport("Walk", "AnimSequence", indexed: false),
        Name = "Walk",
        Bones = ["root"],
        RawAnimationData = [new AnimTrack
        {
            Positions = [Vector3.Zero],
            Rotations = [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)],
        }],
        CompressedAnimationData = [],
        NumFrames = 2,
        SequenceLength = 1,
        RateScale = 1,
    };
}
