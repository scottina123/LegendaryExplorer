using System;
using System.Numerics;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class SkeletalLightEnvironmentBoundsTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void ComponentPhysicsBoundsKeepCharacterLightingOutsideAnUnrelatedVertexCenterShadow()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("SkeletalLightingBounds.pcc", MEGame.LE3);
        var context = new MeshRenderContext();
        MeshStaticLighting shadow = null;
        try
        {
            SkeletalMesh skeleton = Skeleton();
            ExportEntry receiver = package.CreateExport("Receiver", "Pawn", indexed: false);
            ExportEntry environmentExport = package.CreateExport("Environment", "BioDynamicLightEnvironmentComponent", receiver, indexed: false);
            environmentExport.WriteProperties([new BoolProperty(false, "bCastShadows")]);
            ExportEntry component = package.CreateExport("MeshComponent", "SkeletalMeshComponent", receiver, indexed: false);
            ExportEntry body = Body(package, "Torso", "Torso", Aggregate(Box(Matrix4x4.Identity, Vector3.One)));
            ExportEntry asset = Asset(package, [body], [0]);
            component.WriteProperties([new ObjectProperty(asset, "PhysicsAsset"), new ObjectProperty(environmentExport, "LightEnvironment")]);
            var provider = SkeletalLightEnvironmentBounds.Create(context, component, component.GetProperties(), skeleton);
            Matrix4x4[] pose = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, 4, 0)];
            BoxSphereBounds physicsBounds = provider.GetBounds(pose, Matrix4x4.Identity);
            AssertVector(new Vector3(0, 4, 0), physicsBounds.Origin);

            ExportEntry collection = package.CreateExport("Collection", "StaticMeshCollectionActor", indexed: false);
            ExportEntry shadowComponent = package.CreateExport("Railing", "StaticMeshComponent", collection, indexed: false);
            shadowComponent.WritePropertiesAndBinary([], new StaticMeshComponent { LODData = [] });
            ExportEntry meshExport = package.CreateExport("RailingMesh", "StaticMesh", indexed: false);
            StaticMesh staticMesh = new()
            {
                Export = meshExport,
                LODModels = [new StaticMeshRenderData { Elements = [], VertexBuffer = new StaticMeshVertexBuffer { VertexData = [] } }]
            };
            shadow = MeshStaticLighting.Create(context, shadowComponent, staticMesh, 0);
            shadow.GeometryMesh = context.GetLevelGeometryMesh("railing-metal", () =>
                ([new Vector3(5, -1, -2), new Vector3(5, 1, -2), new Vector3(5, 0, 2)], [0, 1, 2]));
            shadow.GetBounds = () => new BoxSphereBounds { Origin = new Vector3(5, 0, 0), BoxExtent = new Vector3(1, 1, 2), SphereRadius = 3 };
            shadow.GetLocalToWorld = () => Matrix4x4.Identity;
            ExportEntry lightExport = package.CreateExport("Light", "PointLightComponent", indexed: false);
            lightExport.WriteProperties([
                new FloatProperty(100, "Radius"), new FloatProperty(1, "Brightness"),
                new BoolProperty(true, "CastShadows"), new BoolProperty(true, "CastStaticShadows"),
                CommonStructs.MatrixProp(Matrix4x4.CreateTranslation(10, 0, 0), "CachedParentToWorld"),
                new StructProperty("LightingChannelContainer", [new BoolProperty(true, "bInitialized"),
                    new BoolProperty(true, "CompositeDynamic")], "LightingChannels")
            ]);
            context.AddLights([SceneLight.Create(lightExport, "PointLightMovable", context.PackageCache)]);
            Assert.IsTrue(context.ResolveLightEnvironment(component, component.GetProperties(), receiver, out DynamicLightEnvironment environment));
            bool usePhysicsBounds = true;
            context.AddLightEnvironmentPrimitive(environment, new LightEnvironmentPrimitive
            {
                GetBounds = () => usePhysicsBounds ? provider.GetBounds(pose, Matrix4x4.Identity)
                    : new BoxSphereBounds { Origin = Vector3.Zero, BoxExtent = new Vector3(0.1f), SphereRadius = 0.2f },
                GetLocalToWorld = () => Matrix4x4.Identity,
                LightingChannels = LightingChannels.DynamicPrimitiveDefault
            });
            Assert.IsTrue(environment.GetLighting().DirectionalColor.LengthSquared() > 0.01f,
                "The authored posed physics body samples the light beyond the railing's metal shadow.");
            usePhysicsBounds = false;
            Assert.AreEqual(Vector3.Zero, environment.GetLighting().DirectionalColor,
                "Replacing component bounds with a tight asymmetric vertex center reproduces the lost light.");
            usePhysicsBounds = true;
            Assert.IsTrue(environment.GetLighting().DirectionalColor.LengthSquared() > 0.01f,
                "Restoring the provider must invalidate the cached environment without changing lights or geometry.");
        }
        finally
        {
            shadow?.Dispose();
            context.EmptyCaches();
            context.PackageCache.Dispose();
        }
    }

    [TestMethod]
    public void RotatedPosedCapsuleUsesCylinderLengthAndComponentScale()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("CapsuleLightingBounds.pcc", MEGame.LE3);
        var context = new MeshRenderContext();
        try
        {
            ExportEntry body = Body(package, "Body", "Root", Aggregate(Capsule(
                Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(4, 0, 0), 2, 6)));
            ExportEntry component = Component(package, Asset(package, [body], [0]));
            var provider = SkeletalLightEnvironmentBounds.Create(context, component, component.GetProperties(), Skeleton());
            Matrix4x4 localToWorld = Matrix4x4.CreateScale(2) * Matrix4x4.CreateRotationZ(MathF.PI / 2)
                                       * Matrix4x4.CreateTranslation(100, 200, 300);
            Matrix4x4[] pose = [Matrix4x4.CreateTranslation(1, 2, 3)];
            BoxSphereBounds bounds = provider.GetBounds(pose, localToWorld);
            AssertVector(new Vector3(96, 210, 306), bounds.Origin);
            AssertVector(new Vector3(4, 10, 4), bounds.BoxExtent);
            Assert.AreEqual(MathF.Sqrt(132), bounds.SphereRadius, 0.0001f);
            Assert.AreEqual(bounds, provider.GetBounds(pose, localToWorld));
            pose[0] = Matrix4x4.CreateTranslation(1, 12, 3);
            bounds = provider.GetBounds(pose, localToWorld);
            AssertVector(new Vector3(76, 210, 306), bounds.Origin,
                "Physics bounds follow the current pose and component transform, rather than cached reference geometry.");
            localToWorld.Translation += new Vector3(50, -20, 10);
            AssertVector(new Vector3(126, 190, 316), provider.GetBounds(pose, localToWorld).Origin,
                "Moving the actor invalidates the bound cache even with the same pose array.");
        }
        finally { context.PackageCache.Dispose(); }
    }

    [DataTestMethod]
    [DataRow(false, true, false)]
    [DataRow(false, false, false)]
    [DataRow(true, true, true)]
    public void CookedBoundsBodiesSelectionAndAllBodiesOverrideAreRespected(bool allBodies, bool selectBody, bool expectFarBody)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("SelectedPhysicsBounds.pcc", MEGame.LE3);
        var context = new MeshRenderContext();
        try
        {
            ExportEntry torso = Body(package, "Torso", "Torso", Aggregate(Box(Matrix4x4.Identity, new Vector3(2))), consider: false);
            ExportEntry far = Body(package, "Far", "Root", Aggregate(Box(Matrix4x4.CreateTranslation(1000, 0, 0), new Vector3(2))));
            ExportEntry asset = Asset(package, [torso, far], selectBody ? [0] : []);
            ExportEntry component = Component(package, asset, allBodies);
            var provider = SkeletalLightEnvironmentBounds.Create(context, component, component.GetProperties(), Skeleton());
            BoxSphereBounds bounds = provider.GetBounds([Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, 0, 10)], Matrix4x4.Identity);
            Assert.AreEqual(allBodies || selectBody, provider.HasPhysicsBounds);
            if (expectFarBody) Assert.IsTrue(bounds.BoxExtent.X > 400);
            else if (selectBody)
            {
                AssertVector(new Vector3(0, 0, 10), bounds.Origin);
                AssertVector(Vector3.One, bounds.BoxExtent,
                    "The cooked BoundsBodies list overrides class defaults and excludes other bodies.");
            }
            else AssertVector(Skeleton().Bounds.Origin - new Vector3(0, 0, 8), bounds.Origin,
                "An explicit empty BoundsBodies list falls back to the authored mesh bounds, displaced by root motion.");
        }
        finally { context.PackageCache.Dispose(); }
    }

    [TestMethod]
    public void MissingCookedSelectionUsesBodyFlagsAndUnknownBonesCannotShiftLighting()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("PhysicsBodyFlags.pcc", MEGame.LE3);
        var context = new MeshRenderContext();
        try
        {
            ExportEntry included = Body(package, "Included", "Torso", Aggregate(Box(Matrix4x4.Identity, new Vector3(2))));
            ExportEntry disabled = Body(package, "Disabled", "Root", Aggregate(Box(Matrix4x4.CreateTranslation(1000, 0, 0), new Vector3(2))), consider: false);
            ExportEntry unknown = Body(package, "Unknown", "MissingBone", Aggregate(Box(Matrix4x4.CreateTranslation(2000, 0, 0), new Vector3(2))));
            ExportEntry component = Component(package, Asset(package, [included, disabled, unknown], null));
            var provider = SkeletalLightEnvironmentBounds.Create(context, component, component.GetProperties(), Skeleton());
            BoxSphereBounds bounds = provider.GetBounds([], Matrix4x4.Identity);
            AssertVector(new Vector3(0, 0, 10), bounds.Origin);
            AssertVector(Vector3.One, bounds.BoxExtent);
        }
        finally { context.PackageCache.Dispose(); }
    }

    [TestMethod]
    public void AuthoredFallbackTracksRootMotionWithoutDependingOnTightSkinnedVertices()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("AuthoredSkeletalBounds.pcc", MEGame.LE3);
        var context = new MeshRenderContext();
        try
        {
            ExportEntry component = Component(package, null);
            SkeletalMesh mesh = Skeleton();
            var provider = SkeletalLightEnvironmentBounds.Create(context, component, component.GetProperties(), mesh);
            Matrix4x4 world = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(100, 200, 300);
            BoxSphereBounds reference = provider.GetBounds([], world);
            AssertVector(new Vector3(100, 200, 700), reference.Origin);
            AssertVector(new Vector3(20, 60, 120), reference.BoxExtent);
            Assert.AreEqual(mesh.Bounds.SphereRadius * 4, reference.SphereRadius, 0.0001f);
            BoxSphereBounds posed = provider.GetBounds([Matrix4x4.CreateTranslation(5, -2, 18)], world);
            AssertVector(new Vector3(110, 194, 740), posed.Origin);
            AssertVector(reference.BoxExtent, posed.BoxExtent);
            AssertVector(reference.Origin, provider.GetBounds([default(Matrix4x4)], world).Origin,
                "An uninitialized pose must not replace valid authored bounds with zeros.");
        }
        finally { context.PackageCache.Dispose(); }
    }

    private static SkeletalMesh Skeleton() => new()
    {
        Bounds = new BoxSphereBounds { Origin = new Vector3(0, 0, 100), BoxExtent = new Vector3(10, 20, 30), SphereRadius = 38 },
        RefSkeleton = [new MeshBone { Name = "Root", ParentIndex = -1, Position = new Vector3(0, 0, 8), Orientation = Quaternion.Identity },
            new MeshBone { Name = "Torso", ParentIndex = 0, Position = new Vector3(0, 0, 2), Orientation = Quaternion.Identity }]
    };

    private static ExportEntry Component(IMEPackage package, ExportEntry asset, bool allBodies = false)
    {
        ExportEntry component = package.CreateExport("Component", "SkeletalMeshComponent", indexed: false);
        PropertyCollection properties = [new BoolProperty(allBodies, "bConsiderAllBodiesForBounds")];
        if (asset is not null) properties.Add(new ObjectProperty(asset, "PhysicsAsset"));
        component.WriteProperties(properties);
        return component;
    }

    private static ExportEntry Asset(IMEPackage package, ExportEntry[] bodies, int[] selected)
    {
        ExportEntry asset = package.CreateExport("Asset", "PhysicsAsset", indexed: false);
        var bodyProperties = new ArrayProperty<ObjectProperty>("BodySetup");
        foreach (ExportEntry body in bodies) bodyProperties.Add(new ObjectProperty(body));
        PropertyCollection properties = [bodyProperties];
        if (selected is not null)
        {
            var indices = new ArrayProperty<IntProperty>("BoundsBodies");
            foreach (int index in selected) indices.Add(new IntProperty(index));
            properties.Add(indices);
        }
        asset.WriteProperties(properties);
        return asset;
    }

    private static ExportEntry Body(IMEPackage package, string name, string bone, StructProperty aggregate, bool consider = true)
    {
        ExportEntry body = package.CreateExport(name, "RB_BodySetup", indexed: false);
        body.WriteProperties([new NameProperty(bone, "BoneName"), new BoolProperty(consider, "bConsiderForBounds"), aggregate]);
        return body;
    }

    private static StructProperty Aggregate(StructProperty shape) => new("KAggregateGeom",
        [new ArrayProperty<StructProperty>([shape], shape.StructType == "KBoxElem" ? "BoxElems" : "SphylElems")], "AggGeom");

    private static StructProperty Box(Matrix4x4 transform, Vector3 dimensions) => new("KBoxElem",
        [CommonStructs.MatrixProp(transform, "TM"), new FloatProperty(dimensions.X, "X"),
            new FloatProperty(dimensions.Y, "Y"), new FloatProperty(dimensions.Z, "Z")]);

    private static StructProperty Capsule(Matrix4x4 transform, float radius, float length) => new("KSphylElem",
        [CommonStructs.MatrixProp(transform, "TM"), new FloatProperty(radius, "Radius"), new FloatProperty(length, "Length")]);

    private static void AssertVector(Vector3 expected, Vector3 actual, string message = null)
    {
        Assert.AreEqual(expected.X, actual.X, 0.0001f, message);
        Assert.AreEqual(expected.Y, actual.Y, 0.0001f, message);
        Assert.AreEqual(expected.Z, actual.Z, 0.0001f, message);
    }
}
