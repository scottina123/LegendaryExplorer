using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using LegendaryExplorerCore.Save;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class HeadMorphRonTests
{
    // TSE HeadMorph/Vector/LinearColor schema and PrettyConfig(enumerate_arrays: true).
    // https://github.com/KarlitosVII/trilogy-save-editor/blob/6451f23d1fba62358328dce4e87733970f36ebf1/src/save_data/shared/appearance.rs
    private const string Fixture = """
        (
            hair_mesh: "BIOG_HMF_HIR_PRO.Hair.HMF_HIR_PROShort_MDL",
            accessory_mesh: [
                "BIOG_HMF_HED_PROMorph_R.Accessories.Visor", // [0]
            ],
            morph_features: {"Nose": 0.125, "Jaw": -0.25},
            offset_bones: {"head": (
                x: 1.25,
                y: -2.5,
                z: 3.75,
            )},
            lod0_vertices: [
                (x: 1.0, y: 2.0, z: 3.0), // [0]
                (x: 4.0, y: 5.0, z: 6.0), // [1]
            ],
            lod1_vertices: [(x: 7.0, y: 8.0, z: 9.0)],
            lod2_vertices: [(x: 10.0, y: 11.0, z: 12.0)],
            lod3_vertices: [(x: 13.0, y: 14.0, z: 15.0)],
            scalar_parameters: {"Roughness": 0.12345679},
            vector_parameters: {"SkinTone": (0.125, 0.25, 0.5, 1.0)},
            texture_parameters: {"Diffuse": "BIOG_HMF_HED_PROMorph_R.Textures.Face_Diff"},
        )
        """;

    [TestMethod]
    public void ReadsEveryTrilogySaveEditorField()
    {
        HeadMorph morph = HeadMorph.FromRon(Fixture);
        Assert.AreEqual("BIOG_HMF_HIR_PRO.Hair.HMF_HIR_PROShort_MDL", morph.HairMesh);
        CollectionAssert.AreEqual(new[] { "BIOG_HMF_HED_PROMorph_R.Accessories.Visor" }, morph.AccessoryMeshes);
        Assert.AreEqual(0.125f, morph.MorphFeatures["Nose"]);
        Assert.AreEqual(new Vector3(1.25f, -2.5f, 3.75f), morph.OffsetBones["head"]);
        Assert.HasCount(2, morph.Lod0Vertices);
        Assert.AreEqual(new Vector3(7, 8, 9), morph.Lod1Vertices[0]);
        Assert.AreEqual(new Vector3(10, 11, 12), morph.Lod2Vertices[0]);
        Assert.AreEqual(new Vector3(13, 14, 15), morph.Lod3Vertices[0]);
        Assert.AreEqual(0.12345679f, morph.ScalarParameters["Roughness"]);
        Assert.AreEqual(0.5f, morph.VectorParameters["SkinTone"].B);
        Assert.AreEqual(1f, morph.VectorParameters["SkinTone"].A);
        Assert.AreEqual("BIOG_HMF_HED_PROMorph_R.Textures.Face_Diff", morph.TextureParameters["Diffuse"]);
    }

    [TestMethod]
    public void AcceptsCompactReorderedNamedStructsCommentsAndExponentNumbers()
    {
        // Rearrange the vector explicitly; neither line boundaries nor field order have meaning in RON.
        string input = Fixture;
        int start = input.IndexOf("\"head\": (", StringComparison.Ordinal);
        int end = input.IndexOf(")}", start, StringComparison.Ordinal);
        input = input[..start] + "\"head\": Vector(z: 3.75, /* nested /* comment */ ok */ x: 1.25e0, y: -2.5E+0)" + input[(end + 1)..];
        HeadMorph morph = HeadMorph.FromRon("// file comment\nHeadMorph" + input);
        Assert.AreEqual(new Vector3(1.25f, -2.5f, 3.75f), morph.OffsetBones["head"]);
        Assert.AreEqual(0.125f, morph.MorphFeatures["Nose"]);
    }

    [TestMethod]
    public void ExportRoundTripsEveryFieldUnderCommaDecimalCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            HeadMorph morph = HeadMorph.FromRon(Fixture);
            morph.MorphFeatures["Small"] = float.Epsilon;
            morph.MorphFeatures["Large"] = float.MaxValue;
            morph.MorphFeatures["NegativeZero"] = -0f;
            string ron = morph.ToRon();
            StringAssert.Contains(ron, "\"SkinTone\": (0.125, 0.25, 0.5, 1.0)");
            StringAssert.Contains(ron, "accessory_mesh: [");
            HeadMorph roundTrip = HeadMorph.FromRon(ron);
            Assert.AreEqual(ron, roundTrip.ToRon());
            Assert.AreEqual(BitConverter.SingleToInt32Bits(-0f), BitConverter.SingleToInt32Bits(roundTrip.MorphFeatures["NegativeZero"]));
            Assert.AreEqual(float.Epsilon, roundTrip.MorphFeatures["Small"]);
            Assert.AreEqual(float.MaxValue, roundTrip.MorphFeatures["Large"]);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [TestMethod]
    public void EmptyCollectionsAndEscapedStringsRoundTripWithoutPhantomAccessories()
    {
        var morph = new HeadMorph { HairMesh = "None" };
        morph.TextureParameters["quote\"colon:"] = "back\\slash\n\t\r\0\u0001😀";
        string ron = morph.ToRon();
        HeadMorph parsed = HeadMorph.FromRon(ron);
        Assert.IsEmpty(parsed.AccessoryMeshes);
        Assert.IsEmpty(parsed.Lod3Vertices);
        Assert.AreEqual(morph.TextureParameters["quote\"colon:"], parsed.TextureParameters["quote\"colon:"]);
        Assert.AreEqual("None", HeadMorph.FromRon(new HeadMorph().ToRon().Replace("\"None\"", "r#\"None\"#")).HairMesh);
    }

    [TestMethod]
    [DataRow("(hair_mesh: \"None\")")]
    [DataRow("(hair_mesh: \"unterminated")]
    [DataRow("/* unterminated")]
    [DataRow("() trailing")]
    public void MalformedOrIncompleteFilesHaveActionableErrors(string input)
    {
        var error = Assert.ThrowsExactly<InvalidDataException>(() => HeadMorph.FromRon(input));
        StringAssert.Contains(error.Message, "line");
        StringAssert.Contains(error.Message, "column");
    }

    [TestMethod]
    public void RejectsBadNumbersDuplicateFieldsAndTruncatedGeometry()
    {
        foreach (string bad in new[] { "NaN", "inf", "1e100", "word" })
            Assert.ThrowsExactly<InvalidDataException>(() => HeadMorph.FromRon(Fixture.Replace("0.125", bad)));
        Assert.ThrowsExactly<InvalidDataException>(() => HeadMorph.FromRon(Fixture.Replace("hair_mesh:", "hair_mesh: \"None\", hair_mesh:")));
        Assert.ThrowsExactly<InvalidDataException>(() => HeadMorph.FromRon(Fixture.Replace("z: 3.0", "bad: 3.0")));
        Assert.ThrowsExactly<InvalidDataException>(() => HeadMorph.FromRon(Fixture[..^5]));
        var morph = new HeadMorph();
        morph.ScalarParameters["Invalid"] = float.NaN;
        Assert.ThrowsExactly<InvalidDataException>(() => morph.ToRon());
    }
}
