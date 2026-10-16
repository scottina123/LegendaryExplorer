using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FontAwesome5;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.Tools.Meshplorer;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.Meshplorer;

[TestClass]
public class MeshplorerAnimationPickerPreviewTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void PickerPreviewsSelectedMeshAndHandlesSelectionCancellationAndRecovery()
    {
        InitializeApplicationResources();
        // Application.Current belongs to this STA dispatcher; keep all UI scenarios on that thread.
        SelectionBeforeViewportLoadsAnimatesTheIntactSelectedMesh();
        SupersededLoadCannotReplaceLatestPreviewAndReleasesItsPackage();
        ClearingOrClosingPendingPreviewDiscardsLateLoadAndReleasesItsPackage();
        MissingSourceReportsErrorAndAllowsAnotherSelection();
        IncompatibleAnimationReportsItsSkeletonErrorAndCanRecover();
    }

    private static void SelectionBeforeViewportLoadsAnimatesTheIntactSelectedMesh()
    {
        using var synchronization = new DispatcherSynchronizationScope();
        using var meshPackage = MEPackageHandler.CreateMemoryEmptyPackage("PickerSelectedMesh.pcc", MEGame.LE3);
        ExportEntry mesh = CreateMesh(meshPackage);
        byte[] originalMesh = mesh.Data.ToArray();
        using var source = new AnimationSource("Walk");
        var originalSourceEntries = source.Loaded.Sequence.Export.FileRef.Exports
            .Select(export => (Export: export, Header: export.Header.ToArray(), Data: export.Data.ToArray())).ToArray();
        using var picker = new MeshplorerAnimationPickerPreview((_, _) => Task.FromResult(source.Loaded), mesh);

        picker.SelectAnimation(source.Entry);
        Assert.IsTrue(picker.PreviewLoadingTask.IsCompleted);
        Assert.IsNull(picker.Preview.CurrentMesh,
            "Selecting before Loaded must retain the animation until the viewport can create mesh buffers.");
        Assert.AreEqual(0d, picker.Preview.AnimSliderMax);

        InitializeViewport(picker);
        Assert.AreSame(mesh, picker.Preview.CurrentMesh.Export);
        Assert.HasCount(3, picker.Preview.CurrentMesh.LODModels[0].VertexBufferGPUSkin.VertexData);
        Assert.AreSame(source.Loaded.Sequence.Export, LastAnimationExport(picker));
        Assert.AreEqual("Walk", picker.StatusText);
        Assert.AreEqual(1d, picker.Preview.AnimSliderMax);
        Assert.AreEqual(EFontAwesomeIcon.Solid_Pause, picker.Preview.PlayPauseIcon);

        var wireframe = (CheckBox)picker.Preview.FindName("WireframeCheckbox");
        var skeleton = (CheckBox)picker.Preview.FindName("SkeletonCheckbox");
        var gameShader = (CheckBox)picker.Preview.FindName("InGameShaderCheckbox");
        Assert.IsNotNull(wireframe);
        Assert.IsNotNull(skeleton);
        Assert.IsNotNull(gameShader);
        Assert.IsTrue(picker.Preview.CanUseGameShaders);
        Assert.IsTrue(gameShader.IsEnabled);
        wireframe.IsChecked = true;
        skeleton.IsChecked = true;
        gameShader.IsChecked = true;
        PumpUntilCompleted(picker.Preview.GameShaderLoadingTask);
        Assert.IsTrue(picker.Preview.RenderWireframe);
        Assert.IsTrue(picker.Preview.ShowSkeleton);
        Assert.IsTrue(picker.Preview.RenderGameShader);
        Assert.IsFalse(picker.Preview.IsGameShaderLoading);
        Assert.IsNull(PreviewContext(picker).ErrorText, PreviewContext(picker).ErrorText);
        Assert.IsNotNull(GameShaderPreviewModel(picker).LODs[0].Mesh.VertexBuffer);
        Assert.IsNotNull(typeof(AnimationPreviewControl).GetField("_skeletonVertexBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(picker.Preview), "The skeleton option must create actual viewport line buffers.");

        Vector3 referencePosition = PreviewModel(picker).LODs[0].Mesh.Vertices[0].Position;
        Vector3[] referenceSkeleton = SkeletonPositions(picker);
        Assert.IsTrue(referenceSkeleton.Length >= 2, "The child bone must produce a visible skeleton segment.");
        Assert.AreEqual(referencePosition, GameShaderPreviewModel(picker).LODs[0].Mesh.Vertices[0].Position);
        picker.Preview.SceneViewer.Context.Update(0.5f);
        Vector3 animatedPosition = PreviewModel(picker).LODs[0].Mesh.Vertices[0].Position;
        Assert.IsGreaterThan(0.1f, Vector3.Distance(referencePosition, animatedPosition),
            "The selected animation must update actual preview mesh buffers, not only the playback labels.");
        Assert.AreEqual(animatedPosition, GameShaderPreviewModel(picker).LODs[0].Mesh.Vertices[0].Position,
            "In-game shader geometry must follow the same animated pose as the textured preview.");
        Assert.IsTrue(referenceSkeleton.Zip(SkeletonPositions(picker)).Any(pair => Vector3.Distance(pair.First, pair.Second) > 0.1f),
            "Skeleton lines must follow the current animation instead of showing the reference pose.");

        picker.Preview.Pause();
        picker.Preview.AnimSliderValue = 1;
        Vector3 scrubbedPosition = PreviewModel(picker).LODs[0].Mesh.Vertices[0].Position;
        Assert.IsGreaterThan(0.1f, Vector3.Distance(animatedPosition, scrubbedPosition));
        Assert.AreEqual(scrubbedPosition, GameShaderPreviewModel(picker).LODs[0].Mesh.Vertices[0].Position);
        Assert.IsTrue(referenceSkeleton.Zip(SkeletonPositions(picker)).Any(pair => Vector3.Distance(pair.First, pair.Second) > 0.1f));
        picker.Preview.AnimSliderValue = 0;
        Assert.AreEqual(referencePosition, GameShaderPreviewModel(picker).LODs[0].Mesh.Vertices[0].Position);
        CollectionAssert.AreEqual(referenceSkeleton, SkeletonPositions(picker), "Scrubbing to the first frame must reset the skeleton pose.");
        picker.Preview.AnimSliderValue = 1;

        CollectionAssert.AreEqual(originalMesh, mesh.Data,
            "Preview skinning must not edit the selected mesh's geometry, UVs, or skin weights.");
        foreach (var snapshot in originalSourceEntries)
        {
            CollectionAssert.AreEqual(snapshot.Header, snapshot.Export.Header);
            CollectionAssert.AreEqual(snapshot.Data, snapshot.Export.Data,
                "Viewport render options and playback must not edit the animation source package.");
        }
        Assert.AreEqual(0, source.ReleaseCount);

        picker.SelectAnimation(null);
        Assert.AreEqual(referencePosition, PreviewModel(picker).LODs[0].Mesh.Vertices[0].Position,
            "Clearing the selection must restore the reference pose rather than freeze the previous animation frame.");
        Assert.AreEqual(referencePosition, GameShaderPreviewModel(picker).LODs[0].Mesh.Vertices[0].Position,
            "Clearing the selection must reset the shader preview too.");
        CollectionAssert.AreEqual(referenceSkeleton, SkeletonPositions(picker));
        CollectionAssert.AreEqual(originalMesh, mesh.Data);
        Assert.AreEqual(EFontAwesomeIcon.Solid_Play, picker.Preview.PlayPauseIcon);
        Assert.IsNull(LastAnimationExport(picker));
        Assert.AreEqual("Select an animation to preview.", picker.StatusText);
        Assert.AreEqual(1, source.ReleaseCount);
        picker.Dispose();
        Assert.AreEqual(1, source.ReleaseCount);
    }

    private static void SupersededLoadCannotReplaceLatestPreviewAndReleasesItsPackage()
    {
        using var synchronization = new DispatcherSynchronizationScope();
        using var meshPackage = MEPackageHandler.CreateMemoryEmptyPackage("PickerRaceMesh.pcc", MEGame.LE3);
        using var first = new AnimationSource("First");
        using var second = new AnimationSource("Second");
        var firstCompletion = new TaskCompletionSource<MeshplorerAnimationCatalog.LoadedAnimation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompletion = new TaskCompletionSource<MeshplorerAnimationCatalog.LoadedAnimation>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken firstToken = default;
        using var picker = new MeshplorerAnimationPickerPreview((entry, token) =>
        {
            if (ReferenceEquals(entry, first.Entry))
            {
                firstToken = token;
                return firstCompletion.Task;
            }
            return secondCompletion.Task;
        }, CreateMesh(meshPackage));

        picker.SelectAnimation(first.Entry);
        Task firstTask = picker.PreviewLoadingTask;
        picker.SelectAnimation(second.Entry);
        Task secondTask = picker.PreviewLoadingTask;
        Assert.IsTrue(firstToken.IsCancellationRequested);

        secondCompletion.SetResult(second.Loaded);
        PumpUntilCompleted(secondTask);
        InitializeViewport(picker);
        Assert.AreEqual("Second", picker.StatusText);
        Assert.AreSame(second.Loaded.Sequence.Export, LastAnimationExport(picker));

        // Simulate a disk read that returns despite the earlier cancellation request.
        firstCompletion.SetResult(first.Loaded);
        PumpUntilCompleted(firstTask);
        Assert.AreEqual("Second", picker.StatusText);
        Assert.AreSame(second.Loaded.Sequence.Export, LastAnimationExport(picker));
        Assert.AreEqual(1, first.ReleaseCount);
        Assert.AreEqual(0, second.ReleaseCount);

        picker.Dispose();
        Assert.AreEqual(1, first.ReleaseCount);
        Assert.AreEqual(1, second.ReleaseCount);
    }

    private static void ClearingOrClosingPendingPreviewDiscardsLateLoadAndReleasesItsPackage()
    {
        using var synchronization = new DispatcherSynchronizationScope();
        using var meshPackage = MEPackageHandler.CreateMemoryEmptyPackage("PickerCloseMesh.pcc", MEGame.LE3);
        ExportEntry mesh = CreateMesh(meshPackage);
        foreach (bool close in new[] { false, true })
        {
            using var source = new AnimationSource(close ? "Closing" : "Clearing");
            var completion = new TaskCompletionSource<MeshplorerAnimationCatalog.LoadedAnimation>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken loadingToken = default;
            using var picker = new MeshplorerAnimationPickerPreview((_, token) =>
            {
                loadingToken = token;
                return completion.Task;
            }, mesh);
            picker.SelectAnimation(source.Entry);
            Task loadingTask = picker.PreviewLoadingTask;
            if (close) picker.Dispose();
            else picker.SelectAnimation(null);
            string statusAfterCancellation = picker.StatusText;
            Assert.IsTrue(loadingToken.IsCancellationRequested);

            completion.SetResult(source.Loaded);
            PumpUntilCompleted(loadingTask);
            Assert.IsNull(LastAnimationExport(picker));
            Assert.AreEqual(statusAfterCancellation, picker.StatusText);
            Assert.AreEqual(1, source.ReleaseCount,
                "A source package returned after cancellation must be released even if the picker has closed.");
            if (!close)
                Assert.AreEqual("Select an animation to preview.", picker.StatusText);
        }
    }

    private static void MissingSourceReportsErrorAndAllowsAnotherSelection()
    {
        using var meshPackage = MEPackageHandler.CreateMemoryEmptyPackage("PickerMissingSourceMesh.pcc", MEGame.LE3);
        using var source = new AnimationSource("Installed");
        var missing = new MeshplorerAnimationCatalog.Entry(new AnimationRecord { AnimSequence = "Missing" });
        using var picker = new MeshplorerAnimationPickerPreview((entry, _) => ReferenceEquals(entry, missing)
            ? Task.FromException<MeshplorerAnimationCatalog.LoadedAnimation>(new InvalidOperationException("No installed source package was found."))
            : Task.FromResult(source.Loaded), CreateMesh(meshPackage));

        picker.SelectAnimation(missing);
        StringAssert.Contains(picker.StatusText, "No installed source package");
        Assert.IsNull(LastAnimationExport(picker));
        picker.SelectAnimation(source.Entry);
        InitializeViewport(picker);
        Assert.AreEqual("Installed", picker.StatusText);
        Assert.AreSame(source.Loaded.Sequence.Export, LastAnimationExport(picker));
    }

    private static void IncompatibleAnimationReportsItsSkeletonErrorAndCanRecover()
    {
        using var meshPackage = MEPackageHandler.CreateMemoryEmptyPackage("PickerIncompatibleMesh.pcc", MEGame.LE3);
        using var incompatible = new AnimationSource("Unrelated", "unrelated_bone");
        using var compatible = new AnimationSource("Compatible");
        using var picker = new MeshplorerAnimationPickerPreview((entry, _) => Task.FromResult(
            ReferenceEquals(entry, incompatible.Entry) ? incompatible.Loaded : compatible.Loaded), CreateMesh(meshPackage));

        // Load the mesh first so that the incompatibility is reported as soon as the animation is selected.
        InitializeViewport(picker);
        picker.SelectAnimation(incompatible.Entry);
        StringAssert.Contains(picker.StatusText, "no bones in common");
        Assert.AreEqual(EFontAwesomeIcon.Solid_Play, picker.Preview.PlayPauseIcon,
            "An incompatible animation must not claim to be playing on a motionless mesh.");

        picker.SelectAnimation(null);
        Assert.IsNull(PreviewContext(picker).ErrorText,
            "Clearing a failed preview must remove its error overlay.");
        picker.SelectAnimation(compatible.Entry);
        Assert.AreEqual("Compatible", picker.StatusText);
        Assert.AreSame(compatible.Loaded.Sequence.Export, LastAnimationExport(picker));
        Assert.AreEqual(EFontAwesomeIcon.Solid_Pause, picker.Preview.PlayPauseIcon);
        Assert.IsNull(PreviewContext(picker).ErrorText);
    }

    private static ExportEntry CreateMesh(IMEPackage package)
    {
        ExportEntry export = package.CreateExport("SelectedMesh", "SkeletalMesh", indexed: false);
        GPUSkinVertex Vertex(Vector3 position) => new()
        {
            Position = position,
            TangentX = (PackedNormal)new Vector4(1, 0, 0, 1),
            TangentZ = (PackedNormal)new Vector4(0, 0, 1, -1),
            InfluenceWeights = new Influences(255, 0, 0, 0),
            UV = new Vector2(0.25f, 0.75f),
        };
        var binary = SkeletalMesh.Create();
        binary.RefSkeleton =
        [
            new MeshBone { Name = "root", Orientation = Quaternion.Identity, ParentIndex = 0, NumChildren = 1 },
            new MeshBone { Name = "child", Position = Vector3.UnitX, Orientation = Quaternion.Identity, ParentIndex = 0 },
        ];
        binary.LODModels = [new StaticLODModel
        {
            Sections = [],
            IndexBuffer = [0, 1, 2],
            ShadowIndices = [],
            ActiveBoneIndices = [0],
            ShadowTriangleDoubleSided = [],
            Chunks = [new SkelMeshChunk
            {
                RigidVertices = [], SoftVertices = [], BoneMap = [0], NumSoftVertices = 3, MaxBoneInfluences = 1,
            }],
            NumVertices = 3,
            Edges = [],
            RequiredBones = [0],
            RawPointIndices = [],
            VertexBufferGPUSkin = new SkeletalMeshVertexBuffer
            {
                NumTexCoords = 1,
                VertexData = [Vertex(Vector3.UnitX), Vertex(Vector3.UnitY), Vertex(Vector3.UnitZ)],
            },
        }];
        export.WriteBinary(binary);
        return export;
    }

    private static void InitializeViewport(MeshplorerAnimationPickerPreview picker)
    {
        var viewport = picker.Preview.SceneViewer;
        // Exercise the real Loaded handlers and GPU buffers without native window/image interop.
        var nativeLoaded = typeof(LegacySceneRenderControl)
            .GetMethod("SceneRenderControl_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<RoutedEventHandler>(viewport);
        viewport.Loaded -= nativeLoaded;
        viewport.Context.CreateResources();
        viewport.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, viewport));
        Assert.IsNull(PreviewContext(picker).ErrorText, PreviewContext(picker).ErrorText);
    }

    private static MeshRenderContext PreviewContext(MeshplorerAnimationPickerPreview picker) =>
        (MeshRenderContext)picker.Preview.SceneViewer.Context;

    private static ExportEntry LastAnimationExport(MeshplorerAnimationPickerPreview picker) =>
        (ExportEntry)typeof(AnimationPreviewControl)
            .GetField("_lastAnimExport", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker.Preview);

    private static ModelPreview<WorldVertex> PreviewModel(MeshplorerAnimationPickerPreview picker) =>
        (ModelPreview<WorldVertex>)typeof(AnimationPreviewControl)
            .GetField("_meshPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker.Preview);

    private static ModelPreview<LEVertex> GameShaderPreviewModel(MeshplorerAnimationPickerPreview picker) =>
        (ModelPreview<LEVertex>)typeof(AnimationPreviewControl)
            .GetField("_gameShaderPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker.Preview);

    private static Vector3[] SkeletonPositions(MeshplorerAnimationPickerPreview picker) =>
        ((WorldVertex[])typeof(AnimationPreviewControl)
            .GetField("_skeletonVertices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker.Preview))
        .Select(vertex => vertex.Position).ToArray();

    private static void PumpUntilCompleted(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var timeout = DateTime.UtcNow.AddSeconds(5);
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                if (task.IsCompleted || DateTime.UtcNow >= timeout) frame.Continue = false;
            };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
        }
        Assert.IsTrue(task.IsCompleted, "Timed out waiting for the preview load's UI continuation.");
        task.GetAwaiter().GetResult();
    }

    private static void InitializeApplicationResources()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(MeshplorerWindow).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
    }

    private sealed class DispatcherSynchronizationScope : IDisposable
    {
        private readonly SynchronizationContext previous = SynchronizationContext.Current;
        public DispatcherSynchronizationScope() => SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        public void Dispose() => SynchronizationContext.SetSynchronizationContext(previous);
    }

    private sealed class AnimationSource : IDisposable
    {
        private readonly IMEPackage package;
        public MeshplorerAnimationCatalog.Entry Entry { get; }
        public MeshplorerAnimationCatalog.LoadedAnimation Loaded { get; }
        public byte[] OriginalExport { get; }
        public int ReleaseCount { get; private set; }

        public AnimationSource(string name, string bone = "root")
        {
            package = MEPackageHandler.CreateMemoryEmptyPackage($"PickerAnimation-{name}-{Guid.NewGuid():N}.pcc", MEGame.LE3);
            package.NoLongerUsed += _ => ReleaseCount++;
            ExportEntry export = package.CreateExport(name, "AnimSequence", indexed: false);
            ExportEntry data = package.CreateExport($"{name}Data", "BioAnimSetData", indexed: false);
            data.WriteProperty(new ArrayProperty<NameProperty>([new NameProperty(bone)], "TrackBoneNames"));
            export.WriteProperty(new ObjectProperty(data, "m_pBioAnimSetData"));
            var animation = new AnimSequence
            {
                Export = export,
                Name = name,
                Bones = [bone],
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
            PropertyCollection properties = export.GetProperties();
            animation.UpdateProps(properties, MEGame.LE3);
            export.WritePropertiesAndBinary(properties, animation);
            OriginalExport = export.Data.ToArray();
            Loaded = new MeshplorerAnimationCatalog.LoadedAnimation(package, animation);
            Entry = new MeshplorerAnimationCatalog.Entry(new AnimationRecord { AnimSequence = name });
        }

        public void Dispose()
        {
            // The picker owns successful loader results; reclaim only when a failed assertion prevented handoff.
            if (((UnrealPackageFile)package).RefCount > 0) package.Dispose();
        }
    }
}
