using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Animation;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class ActorPreviewAnimationTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void EntireActorLoopsPausesScrubsAndClearsWithoutEditingExports()
    {
        using var fixture = new PreviewFixture();
        AnimSequence animation = CreateAnimation(fixture.Package);
        byte[][] original = fixture.Package.Exports.Select(export => export.Data.ToArray()).ToArray();
        fixture.Preview.SetPreviewAnimation(animation);
        Assert.IsTrue(fixture.Preview.HasPreviewAnimation);
        Assert.IsTrue(fixture.Preview.RenderContext.ForceContinuousRendering);

        fixture.Preview.UpdatePreviewAnimation(1.25f);
        Assert.AreEqual(0.25, fixture.Preview.AnimationPosition, 0.0001);
        AssertSynchronized(fixture, 0.25f);
        fixture.Preview.PausePreviewAnimation();
        fixture.Preview.UpdatePreviewAnimation(0.5f);
        AssertSynchronized(fixture, 0.25f);
        Assert.IsFalse(fixture.Preview.RenderContext.ForceContinuousRendering);

        fixture.Preview.AnimationPosition = 0.75;
        AssertSynchronized(fixture, 0.75f);
        fixture.Preview.TogglePreviewAnimationPlayback();
        fixture.Preview.UpdatePreviewAnimation(0.5f);
        AssertSynchronized(fixture, 0.25f);
        fixture.Preview.AnimationPosition = double.NaN;
        AssertSynchronized(fixture, 0.25f);

        fixture.Preview.SetPreviewAnimation(null);
        Assert.IsFalse(fixture.Preview.HasPreviewAnimation);
        Assert.IsFalse(fixture.Preview.RenderContext.ForceContinuousRendering);
        foreach (SkeletalMeshComponentProxy component in fixture.Components)
        {
            Assert.IsFalse(GetPlayer(component).HasAnimation);
            Assert.AreEqual(Matrix4x4.Identity, GetPlayer(component).ComputeSkinningMatrices()[0]);
        }
        for (int i = 0; i < original.Length; i++)
            CollectionAssert.AreEqual(original[i], fixture.Package.Exports[i].Data);
    }

    [STATestMethod]
    public void DifferentSequencesWithTheSameNameReplaceEveryComponentAnimation()
    {
        using var fixture = new PreviewFixture();
        using IMEPackage secondPackage = MEPackageHandler.CreateMemoryEmptyPackage("SecondAnimation.pcc", MEGame.LE3);
        fixture.Preview.SetPreviewAnimation(CreateAnimation(fixture.Package));
        fixture.Preview.AnimationPosition = 0.5;
        Matrix4x4 firstPose = GetPlayer(fixture.Components[0]).ComputeSkinningMatrices()[0];

        fixture.Preview.SetPreviewAnimation(CreateAnimation(secondPackage, angle: -MathF.PI / 2));
        fixture.Preview.AnimationPosition = 0.5;

        AssertSynchronized(fixture, 0.5f);
        Assert.AreNotEqual(firstPose, GetPlayer(fixture.Components[0]).ComputeSkinningMatrices()[0]);
    }

    [STATestMethod]
    public void IncompatibleAnimationRecoversAndUnloadStopsEveryComponent()
    {
        using var fixture = new PreviewFixture();
        fixture.Preview.SetPreviewAnimation(CreateAnimation(fixture.Package, bone: "unrelated"));
        Assert.IsFalse(fixture.Preview.HasPreviewAnimation);
        StringAssert.Contains(fixture.Preview.AnimationPreviewStatus, "no bones in common");

        fixture.Preview.SetPreviewAnimation(CreateAnimation(fixture.Package));
        Assert.IsTrue(fixture.Preview.HasPreviewAnimation);
        Assert.IsNull(fixture.Preview.AnimationPreviewStatus);
        fixture.Preview.UnloadExport();
        Assert.IsFalse(fixture.Preview.HasPreviewAnimation);
        Assert.IsFalse(fixture.Preview.CanPreviewActorAnimations);
        Assert.IsFalse(fixture.Preview.RenderContext.ForceContinuousRendering);
        Assert.IsTrue(fixture.Components.All(component => !GetPlayer(component).HasAnimation));
    }

    [STATestMethod]
    public void ClearingAnimationRestoresTheMorphedReferencePose()
    {
        using var fixture = new PreviewFixture();
        fixture.Preview.SetPreviewAnimation(CreateAnimation(fixture.Package));
        SkeletalMeshComponentProxy head = fixture.Components[1];
        var renderer = (SkinnedMeshRenderer)typeof(SkeletalMeshComponentProxy)
            .GetField("skinnedMeshRenderer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(head)!;
        MeshBone[] bones = [new MeshBone { Name = "root", Orientation = Quaternion.Identity, ParentIndex = 0 }];
        renderer.ApplyMorph(bones, [], [new Vector3(2, 0, 0)]);
        fixture.Preview.AnimationPosition = 0.5;

        using var device = new SharpDX.Direct3D11.Device(SharpDX.Direct3D.DriverType.Warp,
            SharpDX.Direct3D11.DeviceCreationFlags.None);
        using var mesh = new Mesh<WorldVertex>(device, [new Triangle(0, 0, 0)],
            [new WorldVertex(Vector3.UnitX, new Vector4(0, 0, 1, 1), Vector2.Zero)]);
        renderer.UpdateSkinning(device.ImmediateContext, mesh, GetPlayer(head));
        Assert.AreNotEqual(new Vector3(2, 0, 0), mesh.Vertices[0].Position);

        fixture.Preview.SetPreviewAnimation(null);
        renderer.UpdateSkinning(device.ImmediateContext, mesh, GetPlayer(head));
        Assert.AreEqual(new Vector3(2, 0, 0), mesh.Vertices[0].Position);
    }

    private static void AssertSynchronized(PreviewFixture fixture, float time)
    {
        Matrix4x4 firstPose = GetPlayer(fixture.Components[0]).ComputeSkinningMatrices()[0];
        foreach (SkeletalMeshComponentProxy component in fixture.Components)
        {
            AnimSequencePlayer player = GetPlayer(component);
            Assert.IsTrue(player.HasAnimation);
            Assert.AreEqual(time, player.CurrentTime, 0.0001f);
            Assert.AreEqual(firstPose, player.ComputeSkinningMatrices()[0]);
        }
    }

    private static AnimSequencePlayer GetPlayer(SkeletalMeshComponentProxy component) =>
        (AnimSequencePlayer)typeof(SkeletalMeshComponentProxy)
            .GetField("animPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!;

    private static AnimSequence CreateAnimation(IMEPackage package, string bone = "root", float angle = MathF.PI / 2) => new()
    {
        Export = package.CreateExport("Walk", "AnimSequence", indexed: false),
        Name = "Walk", Bones = [bone],
        RawAnimationData = [new AnimTrack
        {
            Positions = [Vector3.Zero],
            Rotations = [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle)],
        }],
        CompressedAnimationData = [], NumFrames = 2, SequenceLength = 1, RateScale = 1,
    };

    private sealed class PreviewFixture : IDisposable
    {
        public IMEPackage Package { get; } = MEPackageHandler.CreateMemoryEmptyPackage("ActorAnimation.pcc", MEGame.LE3);
        public ActorPreviewControl Preview { get; }
        public SkeletalMeshComponentProxy[] Components { get; }
        private readonly PreviewActor actor;
        private readonly PreviewActor attached;

        public PreviewFixture()
        {
            typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, typeof(ActorPreviewControl).Assembly);
            Preview = new ActorPreviewControl();
            actor = new PreviewActor(Preview, Package.CreateExport("Actor", "SFXStuntActor", indexed: false));
            attached = new PreviewActor(Preview, Package.CreateExport("Accessory", "SkeletalMeshActor", indexed: false));
            actor.Attached.Add(attached);
            attached.Attached.Add(actor); // Traversal must not loop or apply components twice.
            Components = [AddComponent(actor, "Body", "root"), AddComponent(actor, "Head", "ROOT"),
                AddComponent(actor, "Hair", "root"), AddComponent(attached, "AccessoryMesh", "root")];
            Preview.LoadExport(actor.Export);
            Preview.InitializeActorAnimationPreview(actor);
        }

        private SkeletalMeshComponentProxy AddComponent(PreviewActor owner, string name, string rootBone)
        {
            ExportEntry export = Package.CreateExport(name, "SkeletalMeshComponent", owner.Export, indexed: false);
            var component = new SkeletalMeshComponentProxy(Preview.RenderContext, export, owner);
            var mesh = new SkeletalMesh
            {
                RefSkeleton = [new MeshBone { Name = rootBone, Orientation = Quaternion.Identity, ParentIndex = 0 }],
                LODModels = [new StaticLODModel
                {
                    NumVertices = 1,
                    Chunks = [new SkelMeshChunk { NumSoftVertices = 1, BoneMap = [0] }],
                    VertexBufferGPUSkin = new SkeletalMeshVertexBuffer { VertexData = [new GPUSkinVertex
                    {
                        Position = Vector3.UnitX, TangentZ = (PackedNormal)new Vector4(0, 0, 1, 1),
                        InfluenceWeights = new Influences(255, 0, 0, 0),
                    }] },
                }],
            };
            typeof(SkeletalMeshComponentProxy).GetField("skeletalMesh", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(component, mesh);
            typeof(SkeletalMeshComponentProxy).GetField("skeletalMeshGame", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(component, MEGame.LE3);
            owner.Components.Add(component);
            return component;
        }

        public void Dispose()
        {
            Preview.Dispose();
            actor.Dispose();
            attached.Dispose();
            Package.Dispose();
        }
    }

    private sealed class PreviewActor(IActorEditorContext context, ExportEntry export) : ActorProxy(context, export);
}
