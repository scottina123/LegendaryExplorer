using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using LegendaryExplorerCore.Localization;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class ReferenceIssueTypeCleanupTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void RemovesWrongTypeCubeFacesAndPreservesTextureImportsExportsSubclassesAndNull()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("WrongTypeCubeFaces.pcc", MEGame.LE3);
        var wrongExport = package.CreateExport("MeshComponent", "StaticMeshComponent", indexed: false, prePropBinary: new byte[8]);
        wrongExport.WriteBinary(StaticMeshComponent.Create());
        var wrongImport = package.CreateImport("StaticMeshComponent", "ImportedMeshComponent");
        var textureExport = package.CreateExport("Texture", "Texture2D", indexed: false);
        textureExport.WriteBinary(UTexture2D.Create());
        var textureImport = package.CreateImport("Texture2D", "ImportedTexture");
        var subclassImport = package.CreateImport("LightMapTexture2D", "ImportedLightMap");
        var cube = package.CreateExport("Cube", "TextureCube", indexed: false);
        cube.WriteProperties(new PropertyCollection
        {
            new ObjectProperty(wrongExport, "FacePosX"),
            new ObjectProperty(wrongImport, "FaceNegX"),
            new ObjectProperty(textureExport, "FacePosY"),
            new ObjectProperty(textureImport, "FaceNegY"),
            new ObjectProperty(subclassImport, "FacePosZ"),
            new ObjectProperty(0, "FaceNegZ")
        });
        byte[] originalTextureData = textureExport.Data;
        byte[] originalComponentData = wrongExport.Data;
        Assert.AreEqual(2, PropertyIssues(cube).Length);

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(2, result.RemovedPropertyCount);
        Assert.AreEqual(0, result.ClearedPropertyReferenceCount);
        Assert.AreEqual(0, result.ClearedBinaryReferenceCount);
        Assert.AreEqual(0, result.Failures.Count, string.Join("; ", result.Failures));
        CollectionAssert.AreEqual(new[] { cube }, result.ChangedExports.ToArray());
        Assert.IsNull(cube.GetProperty<ObjectProperty>("FacePosX"));
        Assert.IsNull(cube.GetProperty<ObjectProperty>("FaceNegX"));
        Assert.AreEqual(textureExport.UIndex, cube.GetProperty<ObjectProperty>("FacePosY").Value);
        Assert.AreEqual(textureImport.UIndex, cube.GetProperty<ObjectProperty>("FaceNegY").Value);
        Assert.AreEqual(subclassImport.UIndex, cube.GetProperty<ObjectProperty>("FacePosZ").Value);
        Assert.AreEqual(0, cube.GetProperty<ObjectProperty>("FaceNegZ").Value);
        CollectionAssert.AreEqual(originalTextureData, textureExport.Data);
        CollectionAssert.AreEqual(originalComponentData, wrongExport.Data);
        Assert.AreEqual(0, PropertyIssues(cube).Length);
        Assert.AreEqual(0, ReferenceIssueCleaner.RemoveBadReferences(package).ChangedExports.Count);
    }

    [TestMethod]
    public void RemovesWrongTypeReferencesInStructArraysWithoutRemovingTheirOtherFields()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("WrongTypeStructArrays.pcc", MEGame.LE3);
        var wrong = package.CreateImport("StaticMeshComponent", "MeshComponent");
        var valid = package.CreateImport("Texture2D", "Texture");
        var material = package.CreateExport("Material", "MaterialInstanceConstant", indexed: false);
        material.WriteProperty(new ArrayProperty<StructProperty>(new[]
        {
            TextureParameter("WrongType", wrong.UIndex),
            TextureParameter("Valid", valid.UIndex),
            TextureParameter("Null", 0)
        }, "TextureParameterValues"));
        Assert.AreEqual(1, PropertyIssues(material).Length);

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(1, result.RemovedPropertyCount);
        Assert.AreEqual(0, result.Failures.Count, string.Join("; ", result.Failures));
        var parameters = material.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues");
        Assert.AreEqual(3, parameters.Count);
        Assert.AreEqual("WrongType", parameters[0].GetProp<NameProperty>("ParameterName").Value.Name);
        Assert.IsNotNull(parameters[0].GetProp<StructProperty>("ExpressionGUID"));
        Assert.IsNull(parameters[0].GetProp<ObjectProperty>("ParameterValue"));
        Assert.AreEqual(valid.UIndex, parameters[1].GetProp<ObjectProperty>("ParameterValue").Value);
        Assert.AreEqual(0, parameters[2].GetProp<ObjectProperty>("ParameterValue").Value);
        Assert.AreEqual(0, PropertyIssues(material).Length);
    }

    [TestMethod]
    public void ClearsWrongTypeImmutableStructFieldWithoutChangingItsGuidOrLayout()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("WrongTypeActorReference.pcc", MEGame.LE3);
        var wrong = package.CreateImport("Texture2D", "Texture");
        var source = package.CreateExport("Source", "Object", indexed: false);
        var fields = GlobalUnrealObjectInfo.getDefaultStructValue(MEGame.LE3, "ActorReference", true, package);
        fields.GetProp<ObjectProperty>("Actor").Value = wrong.UIndex;
        var guid = fields.GetProp<StructProperty>("Guid");
        guid.Properties.OfType<IntProperty>().First().Value = 12345;
        var originalGuid = guid.DeepClone();
        source.WriteProperty(new StructProperty("ActorReference", fields, "Reference", isImmutable: true));
        int originalLength = source.Data.Length;
        Assert.AreEqual(1, PropertyIssues(source).Length);

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(0, result.RemovedPropertyCount);
        Assert.AreEqual(1, result.ClearedPropertyReferenceCount);
        Assert.AreEqual(0, result.Failures.Count, string.Join("; ", result.Failures));
        var repaired = source.GetProperty<StructProperty>("Reference");
        Assert.AreEqual(0, repaired.GetProp<ObjectProperty>("Actor").Value);
        Assert.IsTrue(originalGuid.Equivalent(repaired.GetProp<StructProperty>("Guid")));
        Assert.AreEqual(originalLength, source.Data.Length);
        Assert.AreEqual(0, PropertyIssues(source).Length);
    }

    [TestMethod]
    public void RemovesWrongTypeObjectArrayElementsUsingTheArrayDeclaredType()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("WrongTypeObjectArray.pcc", MEGame.LE3);
        var wrong = package.CreateImport("Texture2D", "Texture");
        var valid = package.CreateImport("SequenceObject", "ImportedSequenceObject");
        var sequence = package.CreateExport("Sequence", "Sequence", indexed: false);
        sequence.WriteProperties(new PropertyCollection
        {
            new ArrayProperty<ObjectProperty>(new[]
            {
                new ObjectProperty(wrong), new ObjectProperty(valid), new ObjectProperty(0), new ObjectProperty(sequence)
            }, "SequenceObjects"),
            new ObjectProperty(wrong, "UnknownReference")
        });
        Assert.AreEqual(1, PropertyIssues(sequence).Length);

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(1, result.RemovedPropertyCount);
        Assert.AreEqual(0, result.Failures.Count, string.Join("; ", result.Failures));
        CollectionAssert.AreEqual(new[] { valid.UIndex, 0, sequence.UIndex },
            sequence.GetProperty<ArrayProperty<ObjectProperty>>("SequenceObjects").Select(property => property.Value).ToArray());
        Assert.AreEqual(wrong.UIndex, sequence.GetProperty<ObjectProperty>("UnknownReference").Value,
            "A reference with unavailable property metadata must not be guessed to have the wrong type.");
        Assert.AreEqual(0, PropertyIssues(sequence).Length);
    }

    [TestMethod]
    public void ChecksReferencedClassInheritanceInsteadOfTreatingClassImportsAsInstances()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("WrongTypeClassReference.pcc", MEGame.LE3);
        var actorClass = package.CreateImport("Class", "Actor");
        var actorSubclass = package.CreateImport("Class", "StaticMeshActor");
        var wrongClass = package.CreateImport("Class", "Texture2D");
        var factory = package.CreateExport("Factory", "ActorFactoryActor", indexed: false);
        factory.WriteProperties(new PropertyCollection
        {
            new ObjectProperty(actorSubclass, "ActorClass"),
            new ObjectProperty(actorClass, "GameplayActorClass"),
            new ObjectProperty(wrongClass, "NewActorClass")
        });
        Assert.AreEqual(1, PropertyIssues(factory).Length);

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(1, result.RemovedPropertyCount);
        Assert.AreEqual(0, result.Failures.Count, string.Join("; ", result.Failures));
        Assert.AreEqual(actorSubclass.UIndex, factory.GetProperty<ObjectProperty>("ActorClass").Value);
        Assert.AreEqual(actorClass.UIndex, factory.GetProperty<ObjectProperty>("GameplayActorClass").Value);
        Assert.IsNull(factory.GetProperty<ObjectProperty>("NewActorClass"));
        Assert.AreEqual(0, PropertyIssues(factory).Length);
    }

    [TestMethod]
    [DataRow("StaticMeshCollectionActor", "StaticMeshComponent", false)]
    [DataRow("StaticLightCollectionActor", "PointLightComponent", false)]
    [DataRow("StaticMeshCollectionActor", "StaticMeshComponent", true)]
    [DataRow("StaticLightCollectionActor", "PointLightComponent", true)]
    public void RemovesTransformsPairedWithWrongTypeStaticCollectionComponents(string collectionClass, string componentClass, bool removeAll)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("WrongTypeCollection.pcc", MEGame.LE3);
        var wrong = package.CreateImport("Texture2D", "Texture");
        var firstValid = package.CreateImport(componentClass, "FirstComponent");
        var secondValid = package.CreateImport(componentClass, "SecondComponent");
        var source = package.CreateExport("Collection", collectionClass, indexed: false);
        int[] components = [wrong.UIndex, removeAll ? wrong.UIndex : firstValid.UIndex,
            wrong.UIndex, removeAll ? wrong.UIndex : secondValid.UIndex];
        Matrix4x4[] transforms =
        [
            Matrix4x4.CreateTranslation(10, 20, 30),
            Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(40, 50, 60),
            Matrix4x4.CreateTranslation(70, 80, 90),
            Matrix4x4.CreateScale(5, 6, 7) * Matrix4x4.CreateTranslation(100, 110, 120)
        ];
        StaticCollectionActor binary = collectionClass == "StaticMeshCollectionActor"
            ? new StaticMeshCollectionActor { Export = source }
            : new StaticLightCollectionActor { Export = source };
        binary.Components = components.ToList();
        binary.LocalToWorldTransforms = transforms.ToList();
        source.WriteProperty(new ArrayProperty<ObjectProperty>(components.Select(index => new ObjectProperty(index)), binary.ComponentPropName));
        source.WriteBinary(binary);
        Assert.AreEqual(removeAll ? 4 : 2, PropertyIssues(source).Length);

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(removeAll ? 4 : 2, result.RemovedPropertyCount);
        Assert.AreEqual(0, result.ClearedBinaryReferenceCount);
        Assert.AreEqual(0, result.Failures.Count, string.Join("; ", result.Failures));
        using var stream = package.SaveToStream(compress: false);
        stream.Position = 0;
        using var reopened = MEPackageHandler.OpenMEPackageFromStream(stream);
        var repairedSource = reopened.GetUExport(source.UIndex);
        var repaired = (StaticCollectionActor)ObjectBinary.From(repairedSource);
        int[] expectedComponents = removeAll ? [] : [firstValid.UIndex, secondValid.UIndex];
        Matrix4x4[] expectedTransforms = removeAll ? [] : [transforms[1], transforms[3]];
        CollectionAssert.AreEqual(expectedComponents,
            repairedSource.GetProperty<ArrayProperty<ObjectProperty>>(binary.ComponentPropName).Select(property => property.Value).ToArray());
        CollectionAssert.AreEqual(expectedComponents, repaired.Components);
        CollectionAssert.AreEqual(expectedTransforms, repaired.LocalToWorldTransforms);
        Assert.AreEqual(expectedTransforms.Length * 16 * sizeof(float), repairedSource.GetBinaryData().Length);
        Assert.AreEqual(0, PropertyIssues(repairedSource).Length);
    }

    private static StructProperty TextureParameter(string name, int reference) => new("TextureParameterValue", new PropertyCollection
    {
        new NameProperty(name, "ParameterName"),
        new ObjectProperty(reference, "ParameterValue"),
        StructProperty.FromGuid(Guid.Empty, "ExpressionGUID")
    });

    private static ReferenceIssue[] PropertyIssues(ExportEntry export)
    {
        var results = new ReferenceCheckPackage();
        EntryChecker.CheckReferences(results, export.FileRef, LECLocalizationShim.NonLocalizedStringConverter);
        return results.GetSignificantIssues().OfType<ReferenceIssue>()
            .Where(issue => issue.Entry == export && issue.Location == ReferenceIssueLocation.Property).ToArray();
    }
}
