using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using LegendaryExplorerCore.Scaleform;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class ScaleformSceneTests
{
    [TestMethod]
    public void DisplayListFramesInheritPlacementFieldsAndApplyRemovals()
    {
        byte[] first = Place(26, 6 | 32 | 64, 4, 1, Matrix(65536, 65536, 0, 0, 200, -400), Join(CString("map"), U16(6)));
        byte[] second = Place(26, 6, 2, 2, Matrix(65536, 65536, 0, 0, 0, 0));
        byte[] move = Place(26, 1, 4, null, null);
        byte[] removeWrongCharacter = Tag(5, Join(U16(3), U16(4)));
        var document = ScaleformDocument.Parse(Movie("FWS", 3, Shape(1), Shape(2), first, second,
            Tag(1), move, removeWrongCharacter, Tag(1), Tag(28, U16(4)), Tag(1), Tag(0)));
        var frame1 = ScaleformScene.Build(document);
        CollectionAssert.AreEqual(new ushort[] { 2, 4 }, frame1.Items.Select(i => i.Depth).ToArray());
        var map = frame1.Items[1];
        Assert.AreEqual("map", map.Name);
        Assert.AreEqual((ushort)6, map.ClipDepth);
        Assert.AreEqual(new ScaleformMatrix(1, 0, 0, 1, 10, -20), map.Matrix);
        var frame2 = ScaleformScene.Build(document, frame: 2);
        Assert.AreEqual(map.Matrix, frame2.Items[1].Matrix);
        Assert.AreEqual(map.Name, frame2.Items[1].Name);
        Assert.AreEqual("5", frame2.Items[1].PlacementPath);
        Assert.AreSame(document.Tags[5], frame2.Items[1].PlacementTag);
        Assert.AreEqual(1, ScaleformScene.Build(document, frame: 3).Items.Count);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ScaleformScene.Build(document, frame: 4));
    }

    [TestMethod]
    public void EditingNestedRotatedReflectedMatrixPreservesOpaqueSuffixAndUnrelatedRecords()
    {
        var originalMatrix = new ScaleformMatrix(-1.25, 0.5, -0.75, 2, -3.5, 8.25);
        byte[] suffix = Join(new byte[] { 0 }, U16(123), CString("nested"), U16(12), new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        byte[] placement = Place(70, 0xFE, 8, 1, Matrix(-81920, 131072, 32768, -49152, -70, 165), suffix, flags2: 11, className: "ClipClass");
        // A deliberately long original header and opaque proprietary sibling must survive.
        byte[] sprite = Tag(39, Join(U16(9), U16(1), Tag(1004, new byte[] { 2, 7, 8, 9 }), placement, Tag(1), Tag(0)), true);
        byte[] original = Movie("CFX", 1, Shape(1), sprite, Tag(43, CString("label"), true), Tag(1), Tag(0));
        var document = ScaleformDocument.Parse(original);
        CollectionAssert.AreEqual(original, document.Compile());
        var scene = ScaleformScene.Build(document, 9);
        var item = scene.Items.Single();
        Assert.AreEqual("1/1", item.PlacementPath);
        Assert.AreEqual(originalMatrix, item.Matrix);
        Assert.AreEqual("nested", item.Name);
        Assert.IsTrue(item.HasEffects);
        var editedMatrix = originalMatrix with { OffsetX = 101.35, OffsetY = -50.2 };
        item.PlacementTag.SetPlacementMatrix(editedMatrix);
        var reloaded = ScaleformDocument.Parse(document.Compile());
        var reloadedItem = ScaleformScene.Build(reloaded, 9).Items.Single();
        Assert.AreEqual(editedMatrix, reloadedItem.Matrix);
        Assert.IsTrue(reloadedItem.PlacementTag.Payload.Span.EndsWith(suffix));
        CollectionAssert.AreEqual(document.Tags[0].OriginalRecord.ToArray(), reloaded.Tags[0].OriginalRecord.ToArray());
        CollectionAssert.AreEqual(document.Tags[2].OriginalRecord.ToArray(), reloaded.Tags[2].OriginalRecord.ToArray());
        CollectionAssert.AreEqual(document.Tags[1].Children[0].OriginalRecord.ToArray(), reloaded.Tags[1].Children[0].OriginalRecord.ToArray());
        item.PlacementTag.SetPlacementMatrix(originalMatrix);
        Assert.IsFalse(document.IsModified);
        CollectionAssert.AreEqual(original, document.Compile());
    }

    [TestMethod]
    public void AbsentMoveMatrixCanBeInsertedIncludingIdentityWithoutLosingNameOrEvents()
    {
        byte[] initial = Place(26, 6, 1, 1, Matrix(65536, 65536, 0, 0, 1200, 500));
        byte[] suffix = Join(CString("clickable"), new byte[] { 0xA5, 0x5A, 0, 1, 2, 3 });
        byte[] moving = Place(26, 1 | 32 | 128, 1, null, null, suffix);
        var document = ScaleformDocument.Parse(Movie("GFX", 2, Shape(1), initial, Tag(1), moving, Tag(1), Tag(0)));
        var item = ScaleformScene.Build(document, frame: 2).Items.Single();
        Assert.AreEqual(60.0, item.Matrix.OffsetX);
        Assert.IsNull(ScaleformPlacement.Read(item.PlacementTag).Matrix);
        item.PlacementTag.SetPlacementMatrix(ScaleformMatrix.Identity);
        var compiled = ScaleformDocument.Parse(document.Compile());
        Assert.AreEqual(ScaleformMatrix.Identity, ScaleformScene.Build(compiled, frame: 2).Items.Single().Matrix);
        var editedTag = compiled.Tags[3];
        Assert.AreEqual((byte)(1 | 4 | 32 | 128), editedTag.Payload.Span[0]);
        Assert.IsTrue(editedTag.Payload.Span.EndsWith(suffix));
        Assert.AreEqual(60.0, ScaleformScene.Build(compiled).Items.Single().Matrix.OffsetX);
    }

    [TestMethod]
    public void PlaceObjectOneAndThreeClassNameAreReadWithoutTreatingClassBytesAsCharacter()
    {
        byte[] first = Tag(4, Join(U16(3), U16(1), Matrix(65536, 65536, 0, 0, -40, 60), new byte[] { 0x11, 0x22 }));
        byte[] third = Place(70, 6, 2, 3, Matrix(65536, 65536, 65536, -65536, 0, 0), flags2: 8, className: "ExampleClass");
        var document = ScaleformDocument.Parse(Movie("FWS", 1, Shape(3), first, third, Tag(1), Tag(0)));
        var original = ScaleformPlacement.Read(document.Tags[1]);
        Assert.AreEqual((ushort)3, original.CharacterId);
        Assert.AreEqual(-2.0, original.Matrix.Value.OffsetX);
        document.Tags[1].SetPlacementMatrix(original.Matrix.Value with { OffsetX = 5 });
        Assert.IsTrue(document.Tags[1].Payload.Span.EndsWith(new byte[] { 0x11, 0x22 }));
        var placement3 = ScaleformPlacement.Read(document.Tags[2]);
        Assert.AreEqual((ushort)3, placement3.CharacterId);
        Assert.AreEqual(new ScaleformMatrix(1, 1, -1, 1, 0, 0), placement3.Matrix);
        Assert.AreEqual(2, ScaleformScene.Build(ScaleformDocument.Parse(document.Compile())).Items.Count);
    }

    [TestMethod]
    public void InvalidTransformsAndTruncatedPlacementsCannotModifyTheMovie()
    {
        byte[] source = Movie("FWS", 1, Shape(1), Place(26, 6, 1, 1, new byte[] { 0 }), Tag(1), Tag(0));
        var document = ScaleformDocument.Parse(source);
        var tag = document.Tags[1];
        foreach (var invalid in new[]
        {
            ScaleformMatrix.Identity with { M11 = double.NaN },
            ScaleformMatrix.Identity with { M12 = double.PositiveInfinity },
            ScaleformMatrix.Identity with { M11 = 16384 },
            ScaleformMatrix.Identity with { OffsetX = 100_000_000 }
        })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tag.SetPlacementMatrix(invalid));
        Assert.ThrowsExactly<InvalidOperationException>(() => document.Tags[0].SetPlacementMatrix(ScaleformMatrix.Identity));
        CollectionAssert.AreEqual(source, document.Compile());
        var truncated = ScaleformDocument.Parse(Movie("FWS", 1, Tag(26, new byte[] { 6, 1, 0, 1 }), Tag(1), Tag(0)));
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformPlacement.Read(truncated.Tags[0]));
        Assert.AreEqual(1, ScaleformScene.Build(truncated).Warnings.Count);
        Assert.IsFalse(truncated.IsModified);
    }

    [TestMethod]
    public void LibrarySymbolsCanBeAddedAndHiddenOnRootAndNestedFramesPreservingOriginalRecords()
    {
        byte[] sprite = Tag(39, Join(U16(9), U16(2), Tag(1), Tag(1), Tag(0)));
        byte[] source = Movie("CWS", 2, Shape(1), sprite, Tag(56, Join(U16(1), U16(9), CString("MapPanel"))), Tag(1), Tag(1), Tag(0));
        var document = ScaleformDocument.Parse(source);
        var originalTags = document.Tags.ToArray();
        var addition = document.AddPlacement(9, ScaleformMatrix.Identity with { OffsetX = 20 }, frame: 2);
        Assert.AreEqual(0, ScaleformScene.Build(document).Items.Count);
        var secondFrame = ScaleformScene.Build(document, frame: 2).Items.Single();
        Assert.AreSame(addition, secondFrame.PlacementTag);
        Assert.AreEqual("MapPanel", secondFrame.Name);
        Assert.AreEqual(20.0, secondFrame.Matrix.OffsetX);
        var nested = document.AddPlacement(1, ScaleformMatrix.Identity, spriteId: 9);
        Assert.AreSame(nested, ScaleformScene.Build(document, 9).Items.Single().PlacementTag);
        document.RemovePlacement(1, spriteId: 9, frame: 2);
        Assert.AreEqual(0, ScaleformScene.Build(document, 9, 2).Items.Count);
        var compiled = ScaleformDocument.Parse(document.Compile());
        Assert.AreEqual(1, ScaleformScene.Build(compiled, frame: 2).Items.Count);
        Assert.AreEqual(1, ScaleformScene.Build(compiled, 9).Items.Count);
        Assert.AreEqual(0, ScaleformScene.Build(compiled, 9, 2).Items.Count);
        foreach (var unmodified in originalTags.Where(t => t.Code != 39))
            Assert.IsTrue(compiled.Tags.Any(t => t.OriginalRecord.Span.SequenceEqual(unmodified.OriginalRecord.Span)));
        Assert.ThrowsExactly<InvalidOperationException>(() => document.AddPlacement(9, ScaleformMatrix.Identity, 9));
        Assert.ThrowsExactly<InvalidOperationException>(() => document.AddPlacement(1, ScaleformMatrix.Identity, 9, depth: 1));
        Assert.ThrowsExactly<ArgumentException>(() => document.AddPlacement(200, ScaleformMatrix.Identity));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => document.AddPlacement(1, ScaleformMatrix.Identity, depth: 0));
    }

    [TestMethod]
    public void TransformKeyframesLeaveEarlierFramesAndInheritedPropertiesIntact()
    {
        byte[] placed = Place(26, 6 | 32, 1, 1, Matrix(65536, 65536, 0, 0, 100, 200), CString("button"));
        byte[] source = Movie("FWS", 3, Shape(1), placed, Tag(1), Tag(1), Tag(1), Tag(0));
        var document = ScaleformDocument.Parse(source);
        var originalTag = document.Tags[1];
        var earlier = ScaleformScene.Build(document).Items.Single();
        var before = ScaleformScene.Build(document, frame: 2).Items.Single();
        Assert.AreEqual(1, before.PlacementFrame);
        var transform = ScaleformMatrix.Identity with { OffsetX = 50, OffsetY = 60 };
        var keyframe = document.AddTransformKeyframe(1, transform, frame: 2);
        Assert.AreNotSame(originalTag, keyframe);
        var after = ScaleformScene.Build(document, frame: 2).Items.Single();
        Assert.AreEqual(2, after.PlacementFrame);
        Assert.AreEqual("button", after.Name);
        Assert.AreEqual((ushort)1, after.CharacterId);
        Assert.AreEqual(earlier.Matrix, ScaleformScene.Build(document).Items.Single().Matrix);
        Assert.AreEqual(transform, ScaleformScene.Build(document, frame: 3).Items.Single().Matrix);
        Assert.AreSame(keyframe, document.AddTransformKeyframe(1, transform with { OffsetX = 55 }, frame: 2));
        var reparsed = ScaleformDocument.Parse(document.Compile());
        Assert.AreEqual(earlier.Matrix, ScaleformScene.Build(reparsed).Items.Single().Matrix);
        Assert.AreEqual(55.0, ScaleformScene.Build(reparsed, frame: 2).Items.Single().Matrix.OffsetX);
        CollectionAssert.AreEqual(originalTag.OriginalRecord.ToArray(), reparsed.Tags[1].OriginalRecord.ToArray());
    }

    [TestMethod]
    public void ColorAndVisibilityAreInheritedWhileImageCharacterIdsDoNotConsumeClassNames()
    {
        // CXFORMWITHALPHA: has-multiply, NBits=10, R=G=B=256, A=0.
        byte[] invisibleColor = BitFields((0, 1), (1, 1), (10, 4), (256, 10), (256, 10), (256, 10), (0, 10));
        byte[] place = Place(70, 6 | 8, 1, 1, new byte[] { 0 }, Join(invisibleColor, new byte[] { 0 }), flags2: 16 | 32);
        var document = ScaleformDocument.Parse(Movie("FWS", 2, Shape(1), place, Tag(1), Place(26, 1, 1, null, null), Tag(1), Tag(0)));
        foreach (int frame in new[] { 1, 2 })
        {
            var item = ScaleformScene.Build(document, frame: frame).Items.Single();
            Assert.AreEqual(0.0, item.ColorTransform.AlphaMultiplier);
            Assert.AreEqual(1.0, item.ColorTransform.RedMultiplier);
            Assert.IsFalse(item.Visible);
        }
        document.AddTransformKeyframe(1, ScaleformMatrix.Identity with { OffsetX = 10 }, frame: 2);
        var compiled = ScaleformDocument.Parse(document.Compile());
        Assert.IsFalse(ScaleformScene.Build(compiled, frame: 2).Items.Single().Visible);
        Assert.AreEqual(0.0, ScaleformScene.Build(compiled, frame: 2).Items.Single().ColorTransform.AlphaMultiplier);
    }

    private static byte[] Shape(ushort id) => Tag(2, Join(U16(id), new byte[] { 8, 0, 0, 0, 0 }));
    private static byte[] Place(ushort code, byte flags, ushort depth, ushort? id, byte[] matrix,
        byte[] suffix = null, byte flags2 = 0, string className = null)
    {
        using var data = new MemoryStream();
        data.WriteByte(flags);
        if (code == 70) data.WriteByte(flags2);
        data.Write(U16(depth));
        if (className != null) data.Write(CString(className));
        if (id.HasValue) data.Write(U16(id.Value));
        if (matrix != null) data.Write(matrix);
        if (suffix != null) data.Write(suffix);
        return Tag(code, data.ToArray());
    }

    // Test fixture deliberately uses wider fields than necessary to verify byte-exact reverts.
    private static byte[] Matrix(int sx, int sy, int r0, int r1, int x, int y)
    {
        var bits = new List<int>();
        void Put(int value, int width)
        {
            for (int i = width - 1; i >= 0; i--) bits.Add((value >> i) & 1);
        }
        Put(1, 1); Put(25, 5); Put(sx, 25); Put(sy, 25);
        Put(1, 1); Put(25, 5); Put(r0, 25); Put(r1, 25);
        Put(16, 5); Put(x, 16); Put(y, 16);
        byte[] result = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++) result[i / 8] |= (byte)(bits[i] << (7 - i % 8));
        return result;
    }

    private static byte[] Movie(string signature, ushort frames, params byte[][] records)
    {
        byte[] body = Join(new byte[] { 8, 0, 0, 24 }, U16(frames), Join(records));
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes(signature)); output.WriteByte(10);
        output.Write(BitConverter.GetBytes((uint)body.Length + 8));
        if (signature is "CWS" or "CFX")
        {
            using var compressor = new ZLibStream(output, CompressionLevel.SmallestSize, true);
            compressor.Write(body);
        }
        else output.Write(body);
        return output.ToArray();
    }

    private static byte[] Tag(ushort code, byte[] payload = null, bool longHeader = false)
    {
        payload ??= Array.Empty<byte>();
        int headerLength = longHeader || payload.Length >= 63 ? 6 : 2;
        byte[] record = new byte[headerLength + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(record, (ushort)((code << 6) | (headerLength == 6 ? 63 : payload.Length)));
        if (headerLength == 6) BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(2), (uint)payload.Length);
        payload.CopyTo(record, headerLength);
        return record;
    }
    private static byte[] U16(ushort value) => BitConverter.GetBytes(value);
    private static byte[] BitFields(params (int Value, int Width)[] fields)
    {
        var bits = fields.SelectMany(field => Enumerable.Range(0, field.Width).Select(i => (field.Value >> (field.Width - i - 1)) & 1)).ToArray();
        byte[] data = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++) data[i / 8] |= (byte)(bits[i] << (7 - i % 8));
        return data;
    }
    private static byte[] CString(string value) => Join(Encoding.UTF8.GetBytes(value), new byte[] { 0 });
    private static byte[] Join(params byte[][] arrays) => arrays.SelectMany(a => a).ToArray();
}
