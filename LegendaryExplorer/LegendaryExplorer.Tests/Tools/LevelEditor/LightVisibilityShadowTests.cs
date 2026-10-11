using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class LightVisibilityShadowTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void MixedGlassAndMetalFilterOnlyLightVisibilityAndKeepUnknownSlots()
    {
        StaticMesh mesh = CreateMixedMesh();
        bool[] mask = StaticMeshComponentProxy.CreateLightVisibilityShadowMask(mesh,
            [EBlendMode.BLEND_Opaque, EBlendMode.BLEND_Translucent]);
        var (positions, indices) = StaticMeshComponentProxy.ReadLineCheckShadowTriangles(mesh, mask);
        var geometry = LevelGeometry.TriangleMesh.Create(positions, indices);
        Assert.IsFalse(geometry.IntersectsSegment(Vector3.Zero, new Vector3(10, 0, 0), 0));
        Assert.IsTrue(geometry.IntersectsSegment(new Vector3(0, 30, 0), new Vector3(10, 30, 0), 0));

        // Physical collision still contains both the metal and the glass triangle.
        var raw = ((Vector3[] Positions, int[] Indices))typeof(StaticMeshComponentProxy)
            .GetMethod("ReadLineCheckTriangles", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [mesh]);
        Assert.HasCount(6, raw.Indices);
        Assert.IsTrue(LevelGeometry.TriangleMesh.Create(raw.Positions, raw.Indices)
            .IntersectsSegment(Vector3.Zero, new Vector3(10, 0, 0), 0));

        mesh.kDOPTreeME3UDKLE.Triangles = [.. mesh.kDOPTreeME3UDKLE.Triangles,
            new kDOPCollisionTriangle(3, 4, 5, 99)];
        (positions, indices) = StaticMeshComponentProxy.ReadLineCheckShadowTriangles(mesh, mask);
        Assert.IsTrue(LevelGeometry.TriangleMesh.Create(positions, indices)
            .IntersectsSegment(Vector3.Zero, new Vector3(10, 0, 0), 0),
            "An invalid material index must retain conservative shadow casting.");
    }

    [TestMethod]
    public void CollisionMaterialIdsCanBeReorderedSparseOrSharedBySections()
    {
        StaticMesh mesh = CreateMixedMesh();
        mesh.LODModels[0].Elements[0].MaterialIndex = 7;
        mesh.LODModels[0].Elements[1].MaterialIndex = 3;
        mesh.kDOPTreeME3UDKLE.Triangles = [new kDOPCollisionTriangle(0, 1, 2, 7),
            new kDOPCollisionTriangle(3, 4, 5, 3)];
        bool[] mask = StaticMeshComponentProxy.CreateLightVisibilityShadowMask(mesh,
            [EBlendMode.BLEND_Opaque, EBlendMode.BLEND_Translucent]);
        var (positions, indices) = StaticMeshComponentProxy.ReadLineCheckShadowTriangles(mesh, mask);
        var geometry = LevelGeometry.TriangleMesh.Create(positions, indices);
        Assert.IsFalse(geometry.IntersectsSegment(Vector3.Zero, new Vector3(10, 0, 0), 0));
        Assert.IsTrue(geometry.IntersectsSegment(new Vector3(0, 30, 0), new Vector3(10, 30, 0), 0));

        mesh.LODModels[0].Elements[0].MaterialIndex = 3;
        (positions, indices) = StaticMeshComponentProxy.ReadLineCheckShadowTriangles(mesh, mask);
        Assert.IsTrue(LevelGeometry.TriangleMesh.Create(positions, indices)
            .IntersectsSegment(Vector3.Zero, new Vector3(10, 0, 0), 0),
            "A shared collision material ID must preserve its shadow-casting section conservatively.");
    }

    [TestMethod]
    public void MaterialOverridesAndExplicitTranslucentShadowOptInKeepDistinctCachedGeometry()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("ShadowOverrides.pcc", MEGame.LE3);
        ExportEntry meshExport = package.CreateExport("Mesh", "StaticMesh", indexed: false);
        ExportEntry opaque = Material("Opaque", EBlendMode.BLEND_Opaque);
        ExportEntry glass = Material("Glass", EBlendMode.BLEND_Translucent);
        ExportEntry maskedGlass = Material("MaskedGlass", EBlendMode.BLEND_Translucent, castAsMasked: true);
        ExportEntry opaqueOverride = Instance("OpaqueOverride", opaque);
        ExportEntry glassOverride = Instance("GlassOverride", glass);
        ExportEntry maskedOverride = Instance("MaskedOverride", maskedGlass);
        ExportEntry unresolved = package.CreateExport("Unresolved", "MaterialInstanceConstant", indexed: false);
        unresolved.WriteProperties([]);
        var context = new MeshRenderContext();
        try
        {
            StaticMesh mesh = CreateMixedMesh();
            var opaquePolicy = StaticMeshComponentProxy.ReadLightVisibilityMaterialPolicy(context, opaqueOverride);
            var glassPolicy = StaticMeshComponentProxy.ReadLightVisibilityMaterialPolicy(context, glassOverride);
            var maskedPolicy = StaticMeshComponentProxy.ReadLightVisibilityMaterialPolicy(context, maskedOverride);
            var unknownPolicy = StaticMeshComponentProxy.ReadLightVisibilityMaterialPolicy(context, unresolved);
            Assert.AreEqual(EBlendMode.BLEND_Opaque, opaquePolicy.BlendMode);
            Assert.AreEqual(EBlendMode.BLEND_Translucent, glassPolicy.BlendMode);
            Assert.IsTrue(maskedPolicy.CastTranslucencyAsMasked);
            Assert.IsNull(unknownPolicy.BlendMode);

            bool[] opaqueMask = Mask(opaquePolicy);
            bool[] glassMask = Mask(glassPolicy);
            bool[] maskedMask = Mask(maskedPolicy);
            Assert.IsTrue(Mask(unknownPolicy)[1]);
            Assert.IsTrue(maskedMask[1]);
            string opaqueKey = StaticMeshComponentProxy.GetLightVisibilityGeometryKey(meshExport, opaqueMask);
            string glassKey = StaticMeshComponentProxy.GetLightVisibilityGeometryKey(meshExport, glassMask);
            var opaqueGeometry = Cached(opaqueKey, opaqueMask);
            var glassGeometry = Cached(glassKey, glassMask);
            Assert.AreNotSame(opaqueGeometry, glassGeometry);
            Assert.AreSame(glassGeometry, Cached(glassKey, glassMask));
            Assert.IsTrue(opaqueGeometry.IntersectsSegment(Vector3.Zero, new Vector3(10, 0, 0), 0));
            Assert.IsFalse(glassGeometry.IntersectsSegment(Vector3.Zero, new Vector3(10, 0, 0), 0));

            mesh.LODModels[0].Elements[1].bEnableShadowCasting = false;
            Assert.IsFalse(Mask(maskedPolicy)[1], "An explicit disabled section overrides the material's shadow opt-in.");

            bool[] Mask((EBlendMode? BlendMode, bool CastTranslucencyAsMasked) policy)
                => StaticMeshComponentProxy.CreateLightVisibilityShadowMask(mesh,
                    [EBlendMode.BLEND_Opaque, policy.BlendMode], [false, policy.CastTranslucencyAsMasked]);
            LevelGeometry.TriangleMesh Cached(string key, bool[] mask)
                => context.GetLevelGeometryMesh(key, () => StaticMeshComponentProxy.ReadLineCheckShadowTriangles(mesh, mask));
        }
        finally { context.EmptyCaches(); context.PackageCache.Dispose(); }

        ExportEntry Material(string name, EBlendMode blendMode, bool castAsMasked = false)
        {
            ExportEntry material = package.CreateExport(name, "Material", indexed: false);
            material.WriteProperties([
                new EnumProperty(blendMode.ToString(), "EBlendMode", MEGame.LE3, "BlendMode"),
                new BoolProperty(castAsMasked, "bCastLitTranslucencyShadowAsMasked")
            ]);
            return material;
        }
        ExportEntry Instance(string name, ExportEntry parent)
        {
            ExportEntry material = package.CreateExport(name, "MaterialInstanceConstant", indexed: false);
            material.WriteProperties([new ObjectProperty(parent, "Parent")]);
            return material;
        }
    }

    [DataTestMethod]
    [DataRow(EBlendMode.BLEND_Translucent, false, true, true)]
    [DataRow(EBlendMode.BLEND_Opaque, false, true, false)]
    [DataRow(EBlendMode.BLEND_Masked, false, true, false)]
    [DataRow(EBlendMode.BLEND_Translucent, true, true, false)]
    [DataRow(EBlendMode.BLEND_Opaque, false, false, true)]
    public void RealLightEnvironmentReceivesLightThroughGlassAndRetainsAuthoredOccluders(
        EBlendMode blendMode, bool castAsMasked, bool sectionCastsShadow, bool expectedLit)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("ShadowEnvironment.pcc", MEGame.LE3);
        ExportEntry meshExport = package.CreateExport("Mesh", "StaticMesh", indexed: false);
        ExportEntry collection = package.CreateExport("Collection", "StaticMeshCollectionActor", indexed: false);
        ExportEntry occluder = package.CreateExport("Occluder", "StaticMeshComponent", collection, indexed: false);
        occluder.WritePropertiesAndBinary([], new StaticMeshComponent { LODData = [] });
        ExportEntry receiver = package.CreateExport("Receiver", "Pawn", indexed: false);
        ExportEntry environmentExport = package.CreateExport("Environment", "BioDynamicLightEnvironmentComponent", receiver, indexed: false);
        environmentExport.WriteProperties([new BoolProperty(false, "bCastShadows")]);
        ExportEntry receiverComponent = package.CreateExport("MeshComponent", "SkeletalMeshComponent", receiver, indexed: false);
        receiverComponent.WriteProperties([new ObjectProperty(environmentExport, "LightEnvironment")]);
        ExportEntry lightExport = package.CreateExport("Light", "PointLightComponent", indexed: false);
        lightExport.WriteProperties([
            new FloatProperty(100, "Radius"), new FloatProperty(1, "Brightness"),
            new BoolProperty(true, "CastShadows"), new BoolProperty(true, "CastStaticShadows"),
            CommonStructs.MatrixProp(Matrix4x4.CreateTranslation(10, 0, 0), "CachedParentToWorld"),
            new StructProperty("LightingChannelContainer", [new BoolProperty(true, "bInitialized"),
                new BoolProperty(true, "CompositeDynamic")], "LightingChannels")
        ]);
        var context = new MeshRenderContext();
        MeshStaticLighting occluderLighting = null;
        try
        {
            StaticMesh mesh = CreateMixedMesh(meshExport);
            mesh.LODModels[0].Elements[1].bEnableShadowCasting = sectionCastsShadow;
            bool[] mask = StaticMeshComponentProxy.CreateLightVisibilityShadowMask(mesh,
                [EBlendMode.BLEND_Opaque, blendMode], [false, castAsMasked]);
            occluderLighting = MeshStaticLighting.Create(context, occluder, mesh, 0);
            Assert.IsNotNull(occluderLighting);
            occluderLighting.GeometryMesh = context.GetLevelGeometryMesh(
                StaticMeshComponentProxy.GetLightVisibilityGeometryKey(meshExport, mask),
                () => StaticMeshComponentProxy.ReadLineCheckShadowTriangles(mesh, mask));
            occluderLighting.GetBounds = () => new BoxSphereBounds
            { Origin = new Vector3(5, 15, 0), BoxExtent = new Vector3(1, 25, 10), SphereRadius = 30 };
            occluderLighting.GetLocalToWorld = () => Matrix4x4.Identity;
            context.AddLights([SceneLight.Create(lightExport, "PointLightMovable", context.PackageCache)]);
            Assert.IsTrue(context.ResolveLightEnvironment(receiverComponent, receiverComponent.GetProperties(), receiver,
                out DynamicLightEnvironment environment));
            context.AddLightEnvironmentPrimitive(environment, new LightEnvironmentPrimitive
            {
                GetBounds = () => new BoxSphereBounds { Origin = Vector3.Zero, BoxExtent = new Vector3(0.1f), SphereRadius = 0.2f },
                GetLocalToWorld = () => Matrix4x4.Identity,
                LightingChannels = LightingChannels.DynamicPrimitiveDefault
            });
            LightEnvironmentLighting lighting = environment.GetLighting();
            Assert.IsNotNull(lighting);
            Assert.AreEqual(expectedLit, lighting.DirectionalColor.LengthSquared() > 0.00001f,
                "Only the shadow-casting section on the light's actual visibility ray may remove the light.");
        }
        finally
        {
            occluderLighting?.Dispose();
            context.EmptyCaches();
            context.PackageCache.Dispose();
        }
    }

    private static StaticMesh CreateMixedMesh(ExportEntry export = null) => new()
    {
        Export = export,
        LODModels = [new StaticMeshRenderData
        {
            Elements = [new StaticMeshElement { MaterialIndex = 0, bEnableShadowCasting = true },
                new StaticMeshElement { MaterialIndex = 1, bEnableShadowCasting = true }],
            PositionVertexBuffer = new PositionVertexBuffer { VertexData = [
                new(5, 20, -10), new(5, 40, -10), new(5, 30, 10),
                new(5, -10, -10), new(5, 10, -10), new(5, 0, 10)] },
            VertexBuffer = new StaticMeshVertexBuffer { VertexData = [] }
        }],
        kDOPTreeME3UDKLE = new kDOPTreeCompact
        { Triangles = [new kDOPCollisionTriangle(0, 1, 2, 0), new kDOPCollisionTriangle(3, 4, 5, 1)] }
    };
}
