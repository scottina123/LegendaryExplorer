using System;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorerCore.Gammtek.IO;
using LegendaryExplorerCore.Localization;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class ReferenceIssueNavigationTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void NestedPropertyIssuesKeepExactLeafOffsetsWithoutParsingLocalizedMessages()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceNavigation.pcc", MEGame.LE3);
        var material = package.CreateExport("Material", "MaterialInstanceConstant", indexed: false);
        material.WriteProperty(new ArrayProperty<StructProperty>(new[]
        {
            new StructProperty("TextureParameterValue", new PropertyCollection
            {
                new NameProperty("Diffuse", "ParameterName"),
                new ObjectProperty(-999, "ParameterValue"),
                StructProperty.FromGuid(Guid.Empty, "ExpressionGUID")
            }),
            new StructProperty("TextureParameterValue", new PropertyCollection
            {
                new NameProperty("Normal", "ParameterName"),
                new ObjectProperty(-999, "ParameterValue"),
                StructProperty.FromGuid(Guid.Empty, "ExpressionGUID")
            })
        }, "TextureParameterValues"));
        var properties = material.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues")
            .Select(parameter => parameter.GetProp<ObjectProperty>("ParameterValue")).ToList();
        var results = new ReferenceCheckPackage();

        EntryChecker.CheckReferences(results, package, (_, _) => "localized message");

        var issues = results.GetSignificantIssues().OfType<ReferenceIssue>()
            .Where(issue => issue.Entry == material && issue.Location == ReferenceIssueLocation.Property).ToList();
        Assert.HasCount(2, issues);
        for (int i = 0; i < issues.Count; i++)
        {
            Assert.AreEqual(properties[i].StartOffset, issues[i].Offset);
            Assert.AreEqual(properties[i].ValueOffset, issues[i].ValueOffset);
            Assert.AreEqual(-999, issues[i].ReferencedUIndex);
            Assert.AreEqual("localized message", issues[i].Message);
        }
        Assert.AreNotEqual(issues[0].Offset, issues[1].Offset);
    }

    [TestMethod]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void RepeatedMaterialBinaryReferencesHaveDistinctVerifiedOffsets(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("BinaryNavigation.pcc", game);
        var material = package.CreateExport("Material", "Material", indexed: false);
        var binary = Material.Create();
        binary.SM3MaterialResource.UniformExpressionTextures = [-999, -998, -999];
        binary.SM2MaterialResource.UniformExpressionTextures = [-999];
        binary.SM3MaterialResource.TextureDependencyLengthMap.Add(-999, 1);
        binary.SM2MaterialResource.TextureDependencyLengthMap.Add(-999, 2);
        material.WriteBinary(binary);
        byte[] originalData = material.Data;
        string[] originalNames = package.Names.ToArray();
        var results = new ReferenceCheckPackage();

        EntryChecker.CheckReferences(results, package, LECLocalizationShim.NonLocalizedStringConverter);

        var issues = results.GetSignificantIssues().OfType<ReferenceIssue>()
            .Where(issue => issue.Entry == material && issue.Location == ReferenceIssueLocation.Binary).ToList();
        Assert.HasCount(6, issues);
        Assert.AreEqual(6, issues.Select(issue => issue.Offset).Distinct().Count());
        foreach (var issue in issues)
        {
            Assert.IsNotNull(issue.Offset);
            Assert.AreEqual(issue.ReferencedUIndex,
                EndianReader.ToInt32(material.DataReadOnly, issue.Offset.Value, package.Endian));
        }
        CollectionAssert.AreEqual(originalData, material.Data);
        CollectionAssert.AreEqual(originalNames, package.Names.ToArray());
    }

    [TestMethod]
    public void ReferenceOffsetReaderPreservesModifiedBinaryInstance()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferenceOffsets.pcc", MEGame.LE3);
        var material = package.CreateExport("Material", "Material", indexed: false);
        var source = Material.Create();
        source.SM3MaterialResource.UniformExpressionTextures = [-999];
        material.WriteBinary(source);
        var parsed = ObjectBinary.From<Material>(material);
        parsed.SM3MaterialResource.UniformExpressionTextures[0] = 42;

        var offsets = parsed.GetUIndexOffsets();

        Assert.IsTrue(offsets.Any(reference => reference.UIndex == -999));
        Assert.AreEqual(42, parsed.SM3MaterialResource.UniformExpressionTextures[0]);
    }

    [TestMethod]
    public void MissingReferenceOffsetsDoNotSuppressLegacyBinaryWarnings()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("LegacyReference.pcc", MEGame.LE3);
        var redirector = package.CreateExport("Redirector", "ObjectRedirector", indexed: false);
        var binary = ObjectRedirector.Create();
        binary.DestinationObject = -999;
        redirector.WriteBinary(binary);
        var results = new ReferenceCheckPackage();

        EntryChecker.CheckReferences(results, package, LECLocalizationShim.NonLocalizedStringConverter);

        var issue = results.GetSignificantIssues().OfType<ReferenceIssue>()
            .Single(result => result.Entry == redirector && result.Location == ReferenceIssueLocation.Binary);
        Assert.AreEqual(-999, issue.ReferencedUIndex);
        Assert.IsNull(issue.Offset);
    }
}
