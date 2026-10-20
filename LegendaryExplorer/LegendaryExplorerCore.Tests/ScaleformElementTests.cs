using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using LegendaryExplorerCore.Scaleform;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class ScaleformElementTests
{
    [TestMethod]
    public void NewPanelAndButtonAreNamedClipsAndPreserveOriginalArtworkAndScripts()
    {
        var document = Movie(Tag(12, [0]), Tag(1004, [8, 9, 1, 4]));
        var originals = document.Tags.ToArray();
        var panel = document.AddElementDefinition(new() { Kind = ScaleformElementKind.Panel, Name = "myPanel", Width = 360, Height = 120 });
        var placement = document.AddPlacement(panel.CharacterId, ScaleformMatrix.Identity with { OffsetX = 50, OffsetY = 60 });
        placement.SetPlacementName("myPanel");
        var button = document.AddElementDefinition(new() { Kind = ScaleformElementKind.Button, Name = "myButton", Width = 160, Height = 40 });
        document.AddPlacement(button.CharacterId, ScaleformMatrix.Identity).SetPlacementName("myButton");
        var reopened = ScaleformDocument.Parse(document.Compile());
        var items = ScaleformScene.Build(reopened).Items;
        Assert.AreEqual(2, items.Count);
        Assert.AreEqual("myPanel", items[0].Name); Assert.AreEqual(50, items[0].Matrix.OffsetX);
        Assert.AreEqual("myButton", items[1].Name);
        Assert.AreEqual(1, ScaleformScene.Build(reopened, button.CharacterId).Items.Count);
        foreach (var original in originals)
            Assert.IsTrue(reopened.Tags.Any(t => t.Code == original.Code && t.Payload.Span.SequenceEqual(original.Payload.Span)), original.Name);
    }

    [TestMethod]
    public void ImageUsesLosslessPremultipliedArgbAndDoesNotReuseImportedIds()
    {
        // An imported ID is reserved even though the movie dictionary does not define it locally.
        var document = Movie(Tag(57, Join(CString("shared.gfx"), U16(1), U16(1), CString("sharedArtwork"))));
        var image = document.AddElementDefinition(new() { Kind = ScaleformElementKind.Image, Name = "portrait", Width = 2, Height = 1,
            PixelWidth = 2, PixelHeight = 1, Pixels = [200, 100, 50, 128, 255, 255, 255, 0] });
        var dictionary = ScaleformScene.Build(document).Definitions;
        Assert.IsFalse(dictionary.ContainsKey(1));
        var bitmap = dictionary.Values.Single(t => t.Code == 36);
        using var compressed = new MemoryStream(bitmap.Payload.ToArray(), 7, bitmap.Payload.Length - 7);
        using var zip = new ZLibStream(compressed, CompressionMode.Decompress); using var raw = new MemoryStream(); zip.CopyTo(raw);
        CollectionAssert.AreEqual(new byte[] { 128, 100, 50, 25, 0, 0, 0, 0 }, raw.ToArray());
        document.AddPlacement(image.CharacterId, ScaleformMatrix.Identity).SetPlacementName("portrait");
        Assert.AreEqual("portrait", ScaleformScene.Build(ScaleformDocument.Parse(document.Compile())).Items.Single().Name);
    }

    [TestMethod]
    public void NewTextReusesFontAndLiteralTextAndButtonLabelRemainEditable()
    {
        var document = Movie(Tag(48, Join(U16(20), new byte[] { 0, 0, 4 }, Encoding.UTF8.GetBytes("Test"), U16(0), U16(0))));
        var text = document.AddElementDefinition(new() { Kind = ScaleformElementKind.Text, Name = "title", FontId = 20, Text = "<Welcome>\nShepard" });
        Assert.AreEqual("<Welcome>\nShepard", text.ContentTag.TextValue);
        Assert.AreEqual((ushort)20, document.GetElementFonts().Single().Id);
        var button = document.AddElementDefinition(new() { Kind = ScaleformElementKind.Button, Name = "continueButton", FontId = 20, Text = "Continue" });
        button.ContentTag.SetText("Launch");
        document.AddPlacement(button.CharacterId, ScaleformMatrix.Identity).SetPlacementName("continueButton");
        var reopened = ScaleformDocument.Parse(document.Compile());
        var children = ScaleformScene.Build(reopened, button.CharacterId).Items;
        Assert.AreEqual("Launch", children.Single(t => t.Name == "label").Definition.TextValue);
    }

    [TestMethod]
    public void InvalidCreationNeverChangesTheMovie()
    {
        var document = Movie(); byte[] before = document.Compile();
        Assert.ThrowsExactly<ArgumentException>(() => document.AddElementDefinition(new() { Name = "bad name" }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => document.AddElementDefinition(new() { Width = double.NaN }));
        Assert.ThrowsExactly<ArgumentException>(() => document.AddElementDefinition(new() { Kind = ScaleformElementKind.Image, PixelWidth = 3, PixelHeight = 1, Pixels = [0] }));
        Assert.ThrowsExactly<ArgumentException>(() => document.AddElementDefinition(new() { Kind = ScaleformElementKind.Text, FontId = 1 }));
        CollectionAssert.AreEqual(before, document.Compile());
    }

    [TestMethod]
    public void NewArtworkKeepsItsIdsWhenAnEarlierAdditionIsRemoved()
    {
        var first = new ScaleformElementSpec { Name = "First" };
        var second = new ScaleformElementSpec { Name = "Second" };
        var fullDraft = Movie(); fullDraft.AddElementDefinition(first);
        var originalSecond = fullDraft.AddElementDefinition(second);
        var afterRemoval = Movie(); var stableSecond = afterRemoval.AddElementDefinition(second);
        Assert.AreEqual(originalSecond.CharacterId, stableSecond.CharacterId);
        afterRemoval.AddPlacement(stableSecond.CharacterId, ScaleformMatrix.Identity).SetPlacementName("Second");
        Assert.AreEqual("Second", ScaleformScene.Build(ScaleformDocument.Parse(afterRemoval.Compile())).Items.Single().Name);
    }

    [TestMethod]
    public void NewNestedArtworkAndAllItsDependenciesPrecedeTheContainingSprite()
    {
        var document = Movie(Font(20), Clip(50));
        var image = document.AddElementDefinition(new() { Kind = ScaleformElementKind.Image, Name = "nestedImage", PixelWidth = 1, PixelHeight = 1, Pixels = [255, 0, 0, 255] }, beforeSpriteId: 50);
        var button = document.AddElementDefinition(new() { Kind = ScaleformElementKind.Button, Name = "nestedButton", FontId = 20, Text = "Click" }, beforeSpriteId: 50);
        document.AddPlacement(image.CharacterId, ScaleformMatrix.Identity, spriteId: 50).SetPlacementName("nestedImage");
        document.AddPlacement(button.CharacterId, ScaleformMatrix.Identity, spriteId: 50).SetPlacementName("nestedButton");
        var reopened = ScaleformDocument.Parse(document.Compile());
        var declarations = reopened.Tags.Select((tag, index) => (tag, index)).Where(p => p.tag.Code is 36 or 32 or 37 or 39 or 48)
            .ToDictionary(p => BinaryPrimitives.ReadUInt16LittleEndian(p.tag.Payload.Span), p => p.index);
        foreach (var sprite in reopened.Tags.Where(tag => tag.Code == 39))
        {
            int owner = declarations[BinaryPrimitives.ReadUInt16LittleEndian(sprite.Payload.Span)];
            foreach (var placed in sprite.Children.Where(tag => tag.Code is 4 or 26 or 70))
            {
                ushort character = ScaleformPlacement.Read(placed).CharacterId.Value;
                Assert.IsTrue(declarations[character] < owner, $"Character {character} must precede its containing sprite.");
            }
        }
        Assert.AreEqual(2, ScaleformScene.Build(reopened, 50).Items.Count);
        Assert.IsTrue(declarations[20] < declarations[BinaryPrimitives.ReadUInt16LittleEndian(button.ContentTag.Payload.Span)]);
    }

    [TestMethod]
    public void NestedPlacementRejectsLateOriginalAndGeneratedDeclarationsAndLateFontsAtomically()
    {
        var document = Movie(Clip(50), Clip(60), Font(20));
        byte[] before = document.Compile();
        Assert.ThrowsExactly<InvalidOperationException>(() => document.AddPlacement(60, ScaleformMatrix.Identity, spriteId: 50));
        Assert.ThrowsExactly<InvalidOperationException>(() => document.AddElementDefinition(new() { Kind = ScaleformElementKind.Text, Name = "lateFont", FontId = 20 }, beforeSpriteId: 50));
        CollectionAssert.AreEqual(before, document.Compile());
        // A root-created generated symbol has OriginalOffset == -1; actual record order still rejects its forward reference.
        var late = document.AddElementDefinition(new() { Name = "lateRootPanel" });
        before = document.Compile();
        Assert.ThrowsExactly<InvalidOperationException>(() => document.AddPlacement(late.CharacterId, ScaleformMatrix.Identity, spriteId: 50));
        CollectionAssert.AreEqual(before, document.Compile());
        var importedLate = Movie(Clip(50), Tag(57, Join(CString("fonts.gfx"), U16(1), U16(21), CString("$SharedFont"))));
        before = importedLate.Compile();
        Assert.ThrowsExactly<InvalidOperationException>(() => importedLate.AddElementDefinition(new() { Kind = ScaleformElementKind.Text, Name = "lateShared", FontId = 21 }, beforeSpriteId: 50));
        CollectionAssert.AreEqual(before, importedLate.Compile());
        var importedEarly = Movie(Tag(57, Join(CString("fonts.gfx"), U16(1), U16(21), CString("$SharedFont"))), Clip(50));
        var text = importedEarly.AddElementDefinition(new() { Kind = ScaleformElementKind.Text, Name = "earlyShared", FontId = 21 }, beforeSpriteId: 50);
        importedEarly.AddPlacement(text.CharacterId, ScaleformMatrix.Identity, spriteId: 50);
        Assert.AreEqual(1, ScaleformScene.Build(ScaleformDocument.Parse(importedEarly.Compile()), 50).Items.Count);
    }

    [TestMethod]
    public void ExistingTextFontChoicesExcludeLaterLocalAndSharedFontDeclarations()
    {
        var document = Movie(Font(20),
            Tag(57, Join(CString("early.gfx"), U16(1), U16(21), CString("$EarlyFont"))),
            Tag(37, Join(U16(30), [8, 0, 0x80, 0], CString("caption"), CString("Text"))),
            Font(22), Tag(57, Join(CString("late.gfx"), U16(1), U16(23), CString("$LateFont"))));
        var text = document.Tags.Single(tag => tag.Code == 37);
        byte[] before = document.Compile();
        CollectionAssert.AreEquivalent(new ushort[] { 20, 21, 22, 23 }, document.GetElementFonts().Select(font => font.Id).ToArray());
        CollectionAssert.AreEquivalent(new ushort[] { 20, 21 }, document.GetTextFonts(text).Select(font => font.Id).ToArray());
        Assert.IsFalse(document.GetTextFonts(text).Any(font => font.Id is 22 or 23),
            "Formatting must not introduce forward references to local or imported fonts.");
        CollectionAssert.AreEqual(before, document.Compile());

        var otherDocument = ScaleformDocument.Parse(before);
        Assert.ThrowsExactly<InvalidDataException>(() => otherDocument.GetTextFonts(text), "Font choices must belong to the text's own movie.");
    }

    private static byte[] Font(ushort id) => Tag(48, Join(U16(id), [0, 0, 4], Encoding.UTF8.GetBytes("Test"), U16(0), U16(0)));
    private static byte[] Clip(ushort id) => Tag(39, Join(U16(id), U16(1), Tag(1), Tag(0)));

    private static ScaleformDocument Movie(params byte[][] tags)
    {
        byte[] body = Join(new byte[] { 8, 0, 0, 24, 1, 0 }, Join(tags), Tag(1), Tag(0));
        return ScaleformDocument.Parse(Join(Encoding.ASCII.GetBytes("FWS"), [10], BitConverter.GetBytes(body.Length + 8), body));
    }
    private static byte[] Tag(ushort code, byte[] payload = null)
    {
        payload ??= []; bool large = payload.Length >= 63;
        return Join(U16((code << 6) | (large ? 63 : payload.Length)), large ? BitConverter.GetBytes(payload.Length) : [], payload);
    }
    private static byte[] U16(int value) { byte[] data = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)value); return data; }
    private static byte[] CString(string text) => Join(Encoding.UTF8.GetBytes(text), [0]);
    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
