using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.ExportLoaderControls.Scaleform;
using LegendaryExplorerCore.Scaleform;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class ScaleformGroupNavigationTests
{
    [STATestMethod]
    public void ParentNavigationRestoresContainingInstanceAndFrameWithoutEditingTheMovie()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(NativeScaleformExportLoader).Assembly);
        if (Application.Current == null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources = (ResourceDictionary)Application.LoadComponent(
                new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
        }

        byte[] original = Movie();
        var document = ScaleformDocument.Parse(original);
        var draft = new ScaleformVisualDraft();
        var editor = new ScaleformVisualEditor();
        var texts = new Dictionary<string, string>();
        editor.LoadMovie(document, _ => null, draft, texts);
        Assert.IsFalse(editor.CanExitGroup);
        editor.SetFrame(3);
        string outerIdentity = editor.VisibleElements.Single().Identity;
        editor.SelectElement(outerIdentity);
        editor.EnterGroup(2);
        Assert.IsTrue(editor.CanExitGroup);
        editor.SetFrame(2);
        string innerIdentity = editor.VisibleElements.Single(element => element.Name == "Second inner").Identity;
        editor.SelectElement(innerIdentity);
        editor.EnterGroup(3);
        Assert.AreEqual((ushort?)3, draft.SpriteId);

        // Each movie owns its navigation trail even when another export is loaded.
        editor.LoadMovie(document, _ => null, new ScaleformVisualDraft(), new Dictionary<string, string>());
        editor.LoadMovie(document, _ => null, draft, texts);
        editor.ExitGroup();
        Assert.AreEqual((ushort?)2, draft.SpriteId);
        Assert.AreEqual(2, draft.Frame);
        Assert.AreEqual(innerIdentity, editor.SelectedElement?.Identity);
        editor.ExitGroup();
        Assert.IsNull(draft.SpriteId);
        Assert.AreEqual(3, draft.Frame);
        Assert.AreEqual(outerIdentity, editor.SelectedElement?.Identity);
        Assert.IsFalse(editor.CanExitGroup);

        // Opening an expanded nested row still returns to its immediate parent.
        var nested = editor.VisibleElements.Single().Children.Single(element => element.Name == "Second inner");
        editor.SelectElement(nested.Identity);
        editor.EnterGroup(3);
        editor.ExitGroup();
        Assert.AreEqual((ushort?)2, draft.SpriteId);
        Assert.AreEqual(2, draft.Frame);
        Assert.AreEqual(nested.Identity, editor.SelectedElement?.Identity);
        editor.ExitGroup();
        Assert.AreEqual(3, draft.Frame);

        editor.EnterGroup(2);
        editor.EnterGroup(null);
        Assert.IsFalse(editor.CanExitGroup);
        editor.ExitGroup();
        Assert.IsNull(draft.SpriteId);
        Assert.IsFalse(draft.IsModified, "Group navigation must not count as a movie edit.");
        CollectionAssert.AreEqual(original, document.Compile());
    }

    private static byte[] Movie()
    {
        byte[] inner = Tag(39, Join(BitConverter.GetBytes((ushort)3), BitConverter.GetBytes((ushort)1), Tag(1, []), Tag(0, [])));
        byte[] outer = Tag(39, Join(BitConverter.GetBytes((ushort)2), BitConverter.GetBytes((ushort)2),
            Placement(3, 1, "First inner"), Placement(3, 2, "Second inner"), Tag(1, []), Tag(1, []), Tag(0, [])));
        byte[] body = Join([0x08, 0x00], [0, 24, 3, 0], inner, outer, Placement(2, 1, "Outer"),
            Tag(1, []), Tag(1, []), Tag(1, []), Tag(0, []));
        return Join("FWS"u8.ToArray(), [10], BitConverter.GetBytes((uint)body.Length + 8), body);
    }

    private static byte[] Placement(ushort character, ushort depth, string name) =>
        Tag(26, Join([0x26], BitConverter.GetBytes(depth), BitConverter.GetBytes(character), [0], Encoding.UTF8.GetBytes(name + "\0")));

    private static byte[] Tag(ushort code, byte[] payload)
    {
        int headerSize = payload.Length < 63 ? 2 : 6;
        byte[] result = new byte[headerSize + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)((code << 6) | (headerSize == 2 ? payload.Length : 63)));
        if (headerSize == 6) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2), (uint)payload.Length);
        payload.CopyTo(result, headerSize);
        return result;
    }

    private static byte[] Join(params byte[][] values) => values.SelectMany(value => value).ToArray();
}
