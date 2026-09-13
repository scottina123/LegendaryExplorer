using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.ExportLoaderControls.MaterialEditor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.Meshplorer;

[TestClass]
public class MeshplorerMaterialTextureTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow(MEGame.LE3)]
    [DataRow(MEGame.ME2)]
    public void TextureRowsReadAndWriteTheirMaterialSlotsWithoutChangingTheMesh(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MeshTextures.pcc", game);
        var oldTexture = package.CreateExport("OldTexture", "Texture2D", indexed: false);
        var replacement = package.CreateExport("Replacement", "Texture2D", indexed: false);
        var parent = package.CreateExport("ParentMaterial", "Material", indexed: false);
        var parentBinary = Material.Create();
        parentBinary.SM3MaterialResource.UniformExpressionTextures = [oldTexture.UIndex];
        parentBinary.SM3MaterialResource.Uniform2DTextureExpressions =
        [
            new MaterialUniformExpressionTexture { ExpressionType = "FMaterialUniformExpressionTexture", TextureIndex = oldTexture.UIndex }
        ];
        parent.WriteBinary(parentBinary);
        var material = package.CreateExport("MeshMaterial", "MaterialInstanceConstant", indexed: false);
        material.WriteProperties(new PropertyCollection
        {
            new ObjectProperty(parent, "Parent"),
            new ArrayProperty<StructProperty>(
            [
                new TextureParameter { ParameterName = "Diffuse", ParameterValue = oldTexture.UIndex }.ToStruct(),
                new TextureParameter { ParameterName = "Normal", ParameterValue = oldTexture.UIndex }.ToStruct()
            ], "TextureParameterValues")
        });
        var mesh = package.CreateExport("Mesh", "SkeletalMesh", indexed: false);
        var meshBinary = SkeletalMesh.Create();
        meshBinary.Materials = [material.UIndex];
        mesh.WriteBinary(meshBinary);
        byte[] originalMesh = mesh.Data.ToArray();

        // Isolate control loading; retain the production scan, inline initialization, and commit code.
        var interpreter = (TestBinaryInterpreter)RuntimeHelpers.GetUninitializedObject(typeof(TestBinaryInterpreter));
        interpreter.LoadExport(mesh);
        object[] scanArgs = [mesh.Data, mesh.propsEnd()];
        var scan = (System.Collections.Generic.List<LegendaryExplorer.SharedUI.Interfaces.ITreeItem>)
            Invoke(interpreter, "StartSkeletalMeshScan", scanArgs);
        var materialNode = ((BinInterpNode)scan.Single(item => item is BinInterpNode node && node.Header.Contains("Materials (")))
            .Items.OfType<BinInterpNode>().Single();
        var textures = materialNode.Items.OfType<BinInterpNode>().ToArray();
        Assert.HasCount(3, textures);
        foreach (var node in textures)
        {
            Assert.IsTrue(node.IsTextureReference);
            Assert.AreEqual(-1, node.Offset, "A material reference must never be treated as an offset in the mesh.");
            Invoke(interpreter, "BeginInlineEdit", node);
            Assert.AreEqual(oldTexture.UIndex.ToString(), node.InlineObjectIndexValue);
            Assert.AreEqual(package.GetEntryString(oldTexture.UIndex), node.InlineObjectDisplayValue);
        }

        int changedEvents = 0;
        interpreter.MaterialTextureChanged += (_, _) => changedEvents++;
        var diffuse = textures.Single(node => node.MaterialTexture.Label == "Diffuse");
        Commit(interpreter, diffuse, replacement.UIndex);
        var values = material.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues");
        Assert.AreEqual(replacement.UIndex, values[0].GetProp<ObjectProperty>("ParameterValue").Value);
        Assert.AreEqual(oldTexture.UIndex, values[1].GetProp<ObjectProperty>("ParameterValue").Value,
            "Changing one parameter must preserve other parameters using the same texture.");
        Assert.AreEqual(oldTexture.UIndex, ObjectBinary.From<Material>(parent).SM3MaterialResource.UniformExpressionTextures.Single());

        var uniform = textures.Single(node => node.MaterialTexture.Material == parent);
        Commit(interpreter, uniform, replacement.UIndex);
        Assert.AreEqual(replacement.UIndex, ObjectBinary.From<Material>(parent).SM3MaterialResource.UniformExpressionTextures.Single());

        Commit(interpreter, diffuse, 0);
        Invoke(interpreter, "BeginInlineEdit", diffuse);
        Assert.AreEqual("None", diffuse.InlineObjectDisplayValue);
        Assert.AreEqual(0, diffuse.GetObjectRefValue(mesh));
        Assert.AreEqual(3, changedEvents);
        Assert.AreEqual(4, interpreter.LoadCount, "Each edit must refresh the rows and notify the mesh preview.");
        Assert.Throws<ArgumentException>(() => diffuse.MaterialTexture.WriteIndex(material.UIndex));
        CollectionAssert.AreEqual(originalMesh, mesh.Data);
        Assert.IsTrue(package.IsModified);
    }

    private static object Invoke(BinaryInterpreterWPF interpreter, string name, params object[] args) =>
        typeof(BinaryInterpreterWPF).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(interpreter, args);

    private static void Commit(BinaryInterpreterWPF interpreter, BinInterpNode node, int value) =>
        Assert.IsTrue((bool)Invoke(interpreter, "TryWriteNodeValue", node, null, value.ToString(), null, null));

    private sealed class TestBinaryInterpreter : BinaryInterpreterWPF
    {
        public int LoadCount { get; private set; }
        public override ExportEntry CurrentLoadedExport { get; protected set; }
        public override void LoadExport(ExportEntry exportEntry)
        {
            CurrentLoadedExport = exportEntry;
            LoadCount++;
        }
    }
}
