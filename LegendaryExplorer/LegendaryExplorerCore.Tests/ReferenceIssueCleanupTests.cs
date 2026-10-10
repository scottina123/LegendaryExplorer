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
public class ReferenceIssueCleanupTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void RemovesInvalidAndTrashedPropertiesWhilePreservingValidValuesAndHeaders()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceCleanup.pcc", MEGame.LE3);
        var valid = package.CreateExport("Valid", "Object", indexed: false);
        var imported = package.CreateImport("Object", "Imported");
        var trash = package.CreatePackageExport(UnrealPackageFile.TrashPackageName);
        var legacyTrash = package.CreatePackageExport("Trash");
        var source = package.CreateExport("Source", "Object", indexed: false);
        byte[] sourceHeader = source.Header;
        BitConverter.GetBytes(int.MaxValue).CopyTo(sourceHeader, ExportEntry.OFFSET_idxArchetype);
        source.Header = sourceHeader;
        source.WriteProperties(new PropertyCollection
        {
            new ObjectProperty(int.MaxValue, "BadExport"),
            new ObjectProperty(int.MinValue, "BadImport"),
            new ObjectProperty(trash, "Trash"),
            new ObjectProperty(legacyTrash, "LegacyTrash"),
            new ObjectProperty(valid, "Valid"),
            new ObjectProperty(imported, "Imported"),
            new ObjectProperty(0, "Null"),
            new IntProperty(int.MaxValue, "NotAReference"),
            new DelegateProperty("Function", int.MaxValue, "BadDelegate"),
            new DelegateProperty("Function", valid.UIndex, "GoodDelegate"),
            new StructProperty("TestStruct", new PropertyCollection
            {
                new ObjectProperty(int.MaxValue, "BadNested"),
                new ObjectProperty(valid, "GoodNested"),
                new IntProperty(123, "Sibling")
            }, "Nested")
        });
        var originalValidData = valid.Data;

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(6, result.RemovedPropertyCount);
        Assert.AreEqual(0, result.ClearedPropertyReferenceCount);
        Assert.AreEqual(0, result.ClearedBinaryReferenceCount);
        Assert.AreEqual(0, result.Failures.Count, string.Join("; ", result.Failures));
        CollectionAssert.AreEqual(new[] { source }, result.ChangedExports.ToArray());
        var properties = source.GetProperties();
        foreach (string name in new[] { "BadExport", "BadImport", "Trash", "LegacyTrash", "BadDelegate" })
            Assert.IsFalse(properties.Any(property => property.Name == name));
        Assert.AreEqual(valid.UIndex, properties.GetProp<ObjectProperty>("Valid").Value);
        Assert.AreEqual(imported.UIndex, properties.GetProp<ObjectProperty>("Imported").Value);
        Assert.AreEqual(0, properties.GetProp<ObjectProperty>("Null").Value);
        Assert.AreEqual(int.MaxValue, properties.GetProp<IntProperty>("NotAReference").Value);
        Assert.AreEqual(valid.UIndex, properties.GetProp<DelegateProperty>("GoodDelegate").Value.ContainingObjectUIndex);
        var nested = properties.GetProp<StructProperty>("Nested");
        Assert.IsNull(nested.GetProp<ObjectProperty>("BadNested"));
        Assert.AreEqual(valid.UIndex, nested.GetProp<ObjectProperty>("GoodNested").Value);
        Assert.AreEqual(123, nested.GetProp<IntProperty>("Sibling").Value);
        Assert.AreEqual(int.MaxValue, source.idxArchetype, "Header problems require a separate decision.");
        CollectionAssert.AreEqual(originalValidData, valid.Data);
        Assert.AreEqual(0, ReferenceIssueCleaner.RemoveBadReferences(package).ChangedExports.Count);
    }

    [TestMethod]
    public void CleansObjectArraysAndNestedStructArraysWithoutRemovingValidElements()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceArrays.pcc", MEGame.LE3);
        var valid = package.CreateImport("SequenceObject", "Valid");
        var validTexture = package.CreateImport("Texture2D", "ValidTexture");
        var sequence = package.CreateExport("Sequence", "Sequence", indexed: false);
        sequence.WriteProperty(new ArrayProperty<ObjectProperty>(new[]
        {
            new ObjectProperty(valid), new ObjectProperty(int.MaxValue), new ObjectProperty(0), new ObjectProperty(int.MinValue)
        }, "SequenceObjects"));
        var material = package.CreateExport("Material", "MaterialInstanceConstant", indexed: false);
        material.WriteProperty(new ArrayProperty<StructProperty>(new[]
        {
            Parameter("Bad", int.MaxValue), Parameter("Good", validTexture.UIndex)
        }, "TextureParameterValues"));

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(3, result.RemovedPropertyCount);
        Assert.AreEqual(0, result.Failures.Count, string.Join("; ", result.Failures));
        CollectionAssert.AreEqual(new[] { valid.UIndex, 0 }, sequence.GetProperty<ArrayProperty<ObjectProperty>>("SequenceObjects").Select(p => p.Value).ToArray());
        var parameters = material.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues");
        Assert.AreEqual(2, parameters.Count);
        Assert.AreEqual("Bad", parameters[0].GetProp<NameProperty>("ParameterName").Value.Name);
        Assert.IsNull(parameters[0].GetProp<ObjectProperty>("ParameterValue"));
        Assert.AreEqual(validTexture.UIndex, parameters[1].GetProp<ObjectProperty>("ParameterValue").Value);
    }

    [TestMethod]
    public void ClearsImmutableStructReferencesAndRetainsTheirSerializedLayout()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceImmutable.pcc", MEGame.LE3);
        var source = package.CreateExport("Source", "Object", indexed: false);
        var fields = GlobalUnrealObjectInfo.getDefaultStructValue(MEGame.LE3, "ActorReference", true, package);
        var actor = fields.OfType<ObjectProperty>().Single();
        actor.Value = int.MaxValue;
        var guid = fields.OfType<StructProperty>().Single();
        guid.Properties.OfType<IntProperty>().First().Value = 12345;
        var originalGuid = guid.DeepClone();
        source.WriteProperty(new StructProperty("ActorReference", fields, "Reference", isImmutable: true));
        int originalLength = source.Data.Length;

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(0, result.RemovedPropertyCount);
        Assert.AreEqual(1, result.ClearedPropertyReferenceCount);
        Assert.AreEqual(0, result.Failures.Count, string.Join("; ", result.Failures));
        var repaired = source.GetProperty<StructProperty>("Reference");
        Assert.AreEqual(0, repaired.Properties.OfType<ObjectProperty>().Single().Value);
        Assert.IsTrue(originalGuid.Equivalent(repaired.Properties.OfType<StructProperty>().Single()));
        Assert.AreEqual(originalLength, source.Data.Length);
    }

    [TestMethod]
    [DataRow(MEGame.ME1)]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void ClearsTypedMaterialBinaryReferencesAndPreservesValidReferencesAcrossSave(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceBinary.pcc", game);
        var valid = package.CreateExport("Valid", "Object", indexed: false);
        var imported = package.CreateImport("Object", "Imported");
        var trash = package.CreatePackageExport(UnrealPackageFile.TrashPackageName);
        var material = package.CreateExport("Material", "Material", indexed: false);
        var binary = Material.Create();
        int[] references = [valid.UIndex, int.MaxValue, imported.UIndex, int.MinValue, 0, trash.UIndex];
        foreach (var resource in new[] { binary.SM3MaterialResource, binary.SM2MaterialResource })
        {
            resource.UniformExpressionTextures = references.ToArray();
            resource.Uniform2DTextureExpressions = references.Select(index => new MaterialUniformExpressionTexture
            {
                ExpressionType = "FMaterialUniformExpressionTexture", TextureIndex = index
            }).ToArray();
            for (int i = 0; i < references.Length; i++)
                resource.TextureDependencyLengthMap.Add(references[i], i + 1);
        }
        material.WritePropertiesAndBinary(new PropertyCollection { new ObjectProperty(int.MaxValue, "BadProperty") }, binary);

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(1, result.RemovedPropertyCount);
        Assert.AreEqual(12, result.ClearedBinaryReferenceCount);
        Assert.AreEqual(0, result.Failures.Count, string.Join("; ", result.Failures));
        using var stream = package.SaveToStream(compress: false);
        stream.Position = 0;
        using var reopened = MEPackageHandler.OpenMEPackageFromStream(stream);
        var repaired = reopened.GetUExport(material.UIndex).GetBinaryData<Material>();
        foreach (var resource in new[] { repaired.SM3MaterialResource, repaired.SM2MaterialResource })
        {
            CollectionAssert.AreEqual(new[] { valid.UIndex, 0, imported.UIndex, 0, 0, 0 }, resource.UniformExpressionTextures);
            CollectionAssert.AreEqual(new[] { valid.UIndex, 0, imported.UIndex, 0, 0, 0 }, resource.TextureDependencyLengthMap.Select(pair => pair.Key).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6 }, resource.TextureDependencyLengthMap.Select(pair => pair.Value).ToArray());
        }
    }

    [TestMethod]
    public void ReportsMalformedExportsWithoutPartiallyRewritingThemAndContinuesOtherExports()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceFailures.pcc", MEGame.LE3);
        var brokenBinary = package.CreateExport("BrokenBinary", "Material", indexed: false);
        brokenBinary.WriteProperty(new ObjectProperty(int.MaxValue, "BadProperty"));
        brokenBinary.WriteBinary(new byte[] { 1, 2, 3 });
        var brokenProperties = package.CreateExport("BrokenProperties", "Object", indexed: false);
        brokenProperties.Data = new byte[] { 0, 0, 0, 0, 255, 255, 255, 127, 0, 0, 0, 0 };
        var repairable = package.CreateExport("Repairable", "Object", indexed: false);
        repairable.WriteProperty(new ObjectProperty(int.MaxValue, "BadProperty"));
        byte[] originalBinaryData = brokenBinary.Data;
        byte[] originalPropertyData = brokenProperties.Data;

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(2, result.Failures.Count);
        CollectionAssert.AreEquivalent(new IEntry[] { brokenBinary, brokenProperties }, result.Failures.Select(failure => failure.Entry).ToArray());
        CollectionAssert.AreEqual(originalBinaryData, brokenBinary.Data);
        CollectionAssert.AreEqual(originalPropertyData, brokenProperties.Data);
        Assert.AreEqual(1, result.RemovedPropertyCount);
        CollectionAssert.AreEqual(new[] { repairable }, result.ChangedExports.ToArray());
        Assert.IsNull(repairable.GetProperty<ObjectProperty>("BadProperty"));
    }

    [TestMethod]
    public void ClearsPrePropertyReferencesAndKeepsOtherPrePropertyBytes()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferencePreProperties.pcc", MEGame.LE3);
        var valid = package.CreateExport("Valid", "Object", indexed: false);
        var stack = package.CreateExport("Stack", "Object", indexed: false);
        var stackData = new byte[30];
        BitConverter.GetBytes(int.MaxValue).CopyTo(stackData, 0);
        BitConverter.GetBytes(valid.UIndex).CopyTo(stackData, 4);
        stackData[12] = 123;
        stack.SetPrePropBinary(stackData, isChangingSize: true);
        stack.ObjectFlags |= UnrealFlags.EObjectFlags.HasStack;
        var component = package.CreateExport("Component", "ActorComponent", indexed: false, prePropBinary: new byte[8]);
        byte[] componentData = component.GetPrePropBinary();
        BitConverter.GetBytes(int.MinValue).CopyTo(componentData, component.TemplateOwnerClassIdx);
        BitConverter.GetBytes(1234).CopyTo(componentData, componentData.Length - 4);
        component.SetPrePropBinary(componentData);

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(0, result.Failures.Count, string.Join("; ", result.Failures));
        Assert.AreEqual(2, result.ClearedBinaryReferenceCount);
        stackData.AsSpan(0, 4).Clear();
        componentData.AsSpan(component.TemplateOwnerClassIdx, 4).Clear();
        CollectionAssert.AreEqual(stackData, stack.GetPrePropBinary());
        CollectionAssert.AreEqual(componentData, component.GetPrePropBinary());
    }

    [TestMethod]
    public void RefusesLossyBinaryReserialization()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceTrailingBinary.pcc", MEGame.LE3);
        var material = package.CreateExport("Material", "Material", indexed: false);
        material.WritePropertiesAndBinary(new PropertyCollection { new ObjectProperty(int.MaxValue, "BadProperty") }, Material.Create());
        material.Data = material.Data.Concat(new byte[] { 1, 2, 3, 4 }).ToArray();
        byte[] originalData = material.Data;

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(1, result.Failures.Count);
        Assert.AreSame(material, result.Failures.Single().Entry);
        Assert.AreEqual(0, result.ChangedExports.Count);
        CollectionAssert.AreEqual(originalData, material.Data);
    }

    [TestMethod]
    public void PreservesMalformedNamesWithoutAddingNameTableEntries()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceBadNames.pcc", MEGame.LE3);
        var source = package.CreateExport("Source", "Object", indexed: false);
        source.WriteProperties(new PropertyCollection
        {
            new NameProperty("OriginalName", "Name"), new ObjectProperty(int.MaxValue, "BadProperty")
        });
        int nameOffset = source.GetProperty<NameProperty>("Name").ValueOffset;
        byte[] damagedData = source.Data;
        BitConverter.GetBytes(int.MaxValue).CopyTo(damagedData, nameOffset);
        source.Data = damagedData;
        int originalNameCount = package.NameCount;

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(1, result.Failures.Count);
        Assert.AreSame(source, result.Failures.Single().Entry);
        Assert.AreEqual(0, result.ChangedExports.Count);
        Assert.AreEqual(originalNameCount, package.NameCount);
        CollectionAssert.AreEqual(damagedData, source.Data);
    }

    [TestMethod]
    [Timeout(10000)]
    public void SkipsCyclicComponentOutersAndContinuesRepairingOtherExports()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceOuterCycles.pcc", MEGame.LE3);
        var self = package.CreateExport("Self", "ActorComponent", indexed: false, prePropBinary: new byte[8]);
        var first = package.CreateExport("First", "ActorComponent", indexed: false, prePropBinary: new byte[8]);
        var second = package.CreateExport("Second", "ActorComponent", indexed: false, prePropBinary: new byte[8]);
        var invalidOuter = package.CreateExport("InvalidOuter", "ActorComponent", indexed: false, prePropBinary: new byte[8]);
        var repairable = package.CreateExport("Repairable", "Object", indexed: false);
        repairable.WriteProperty(new ObjectProperty(int.MaxValue, "BadProperty"));
        var importSelf = package.CreateImport("Object", "ImportSelf");
        var importFirst = package.CreateImport("Object", "ImportFirst");
        var importSecond = package.CreateImport("Object", "ImportSecond");
        ExportEntry[] malformed = [self, first, second, invalidOuter];
        byte[][] originalData = malformed.Select(entry => entry.Data).ToArray();
        SetMalformedOuter(self, self.UIndex);
        SetMalformedOuter(first, second.UIndex);
        SetMalformedOuter(second, first.UIndex);
        SetMalformedOuter(invalidOuter, int.MaxValue);
        SetMalformedOuter(importSelf, importSelf.UIndex);
        SetMalformedOuter(importFirst, importSecond.UIndex);
        SetMalformedOuter(importSecond, importFirst.UIndex);

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(4, result.Failures.Count);
        CollectionAssert.AreEquivalent(malformed, result.Failures.Select(failure => failure.Entry).ToArray());
        for (int i = 0; i < malformed.Length; i++)
            CollectionAssert.AreEqual(originalData[i], malformed[i].Data);
        Assert.AreEqual(self.UIndex, self.idxLink);
        Assert.AreEqual(second.UIndex, first.idxLink);
        Assert.AreEqual(first.UIndex, second.idxLink);
        Assert.AreEqual(int.MaxValue, invalidOuter.idxLink);
        Assert.AreEqual(1, result.RemovedPropertyCount);
        CollectionAssert.AreEqual(new[] { repairable }, result.ChangedExports.ToArray());
        Assert.IsNull(repairable.GetProperty<ObjectProperty>("BadProperty"));

        var refreshed = new ReferenceCheckPackage();
        EntryChecker.CheckReferences(refreshed, package, LECLocalizationShim.NonLocalizedStringConverter);
        var issues = refreshed.GetBlockingErrors().Concat(refreshed.GetSignificantIssues()).OfType<ReferenceIssue>().ToArray();
        IEntry[] expectedEntries = [self, first, second, invalidOuter, importSelf, importFirst, importSecond];
        CollectionAssert.AreEquivalent(expectedEntries, issues.Select(issue => issue.Entry).ToArray());
        Assert.IsTrue(issues.All(issue => issue.Location == ReferenceIssueLocation.Header));
        Assert.IsTrue(issues.All(issue => issue.Offset == (issue.Entry is ExportEntry ? ExportEntry.OFFSET_idxLink : ImportEntry.OFFSET_idxLink)));
    }

    [TestMethod]
    [DataRow("StaticMeshCollectionActor", false)]
    [DataRow("StaticLightCollectionActor", false)]
    [DataRow("StaticMeshCollectionActor", true)]
    [DataRow("StaticLightCollectionActor", true)]
    public void KeepsStaticCollectionTransformsPairedWithRetainedComponents(string className, bool removeAll)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceCollectionTransforms.pcc", MEGame.LE3);
        string componentClass = className == "StaticMeshCollectionActor" ? "StaticMeshComponent" : "PointLightComponent";
        var firstValid = package.CreateImport(componentClass, "FirstValid");
        var secondValid = package.CreateImport(componentClass, "SecondValid");
        var source = package.CreateExport("Collection", className, indexed: false);
        int[] components = [int.MaxValue, removeAll ? int.MaxValue - 1 : firstValid.UIndex,
            int.MinValue, removeAll ? int.MinValue + 1 : secondValid.UIndex];
        Matrix4x4[] transforms =
        [
            Matrix4x4.CreateTranslation(10, 20, 30),
            Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(40, 50, 60),
            Matrix4x4.CreateTranslation(70, 80, 90),
            Matrix4x4.CreateScale(5, 6, 7) * Matrix4x4.CreateTranslation(100, 110, 120)
        ];
        StaticCollectionActor binary = className == "StaticMeshCollectionActor"
            ? new StaticMeshCollectionActor { Export = source }
            : new StaticLightCollectionActor { Export = source };
        binary.Components = components.ToList();
        binary.LocalToWorldTransforms = transforms.ToList();
        source.WriteProperty(new ArrayProperty<ObjectProperty>(components.Select(index => new ObjectProperty(index)), binary.ComponentPropName));
        source.WriteBinary(binary);

        var result = ReferenceIssueCleaner.RemoveBadReferences(package);

        Assert.AreEqual(0, result.Failures.Count, string.Join("; ", result.Failures));
        Assert.AreEqual(removeAll ? 4 : 2, result.RemovedPropertyCount);
        Assert.AreEqual(0, result.ClearedBinaryReferenceCount);
        Assert.AreEqual(removeAll ? 0 : 2 * 16 * sizeof(float), source.GetBinaryData().Length);
        using var stream = package.SaveToStream(compress: false);
        stream.Position = 0;
        using var reopened = MEPackageHandler.OpenMEPackageFromStream(stream);
        var repairedExport = reopened.GetUExport(source.UIndex);
        var repaired = (StaticCollectionActor)ObjectBinary.From(repairedExport);
        int[] expectedComponents = removeAll ? [] : [firstValid.UIndex, secondValid.UIndex];
        Matrix4x4[] expectedTransforms = removeAll ? [] : [transforms[1], transforms[3]];
        CollectionAssert.AreEqual(expectedComponents,
            repairedExport.GetProperty<ArrayProperty<ObjectProperty>>(binary.ComponentPropName).Select(property => property.Value).ToArray());
        CollectionAssert.AreEqual(expectedComponents, repaired.Components);
        CollectionAssert.AreEqual(expectedTransforms, repaired.LocalToWorldTransforms);
        Assert.AreEqual(expectedTransforms.Length * 16 * sizeof(float), repairedExport.GetBinaryData().Length);
    }

    private static StructProperty Parameter(string name, int reference) => new("TextureParameterValue", new PropertyCollection
    {
        new NameProperty(name, "ParameterName"), new ObjectProperty(reference, "ParameterValue")
    });

    private static void SetMalformedOuter(IEntry entry, int outer)
    {
        byte[] header = entry.Header;
        BitConverter.GetBytes(outer).CopyTo(header, entry is ExportEntry ? ExportEntry.OFFSET_idxLink : ImportEntry.OFFSET_idxLink);
        entry.Header = header;
    }
}
