using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.ExportLoaderControls.Scaleform;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Scaleform;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class ScaleformVisualEditorTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    // All checks share one STA thread because the WPF application resource dictionary
    // is shared. These exercise the same draft operations used by canvas gestures.
    [STATestMethod]
    public void CanvasEditsRemainReversibleAndCompileIntoTheSelectedMovie()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(NativeScaleformExportLoader).Assembly);
        if (Application.Current == null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources = (ResourceDictionary)Application.LoadComponent(
                new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
        }

        AssertMoveResizeAndUndoLeaveTheSourceUntouched();
        AssertBlankNestedIntroChoosesVisibleAuthoringFrame();
        AssertNestedMovementUsesParentCoordinates();
        AssertLibraryAddRemoveAndUndoAreReversible();
        AssertLibraryInstancesCompileAndCanBeRemoved();
        AssertVisualDraftsSurviveNavigationAndApplyOnlyTheirMovie();
        AssertEditingAnInheritedFrameDoesNotMoveAnEarlierFrame();
        AssertPendingInspectorFieldsCommitBeforeApplyAndNavigation();
        AssertLiteralTextAndHtmlLineBreaksSurviveVisualEditing();
        AssertTextFormattingSurvivesUndoNavigationAndPackageApply();
        AssertTextFormattingInspectorCommitsAndFormatsNewArtwork();
        AssertFormattingBackToDefaultsRetainsHtmlState();
        AssertNewInstancesKeepEarlierFramesWhenMovedOrRemovedLater();
        AssertAuthoredLibraryCopiesSurviveDeletingTheirFirstInstanceAndUndoingAnOpenGroup();
        AssertFailedApplyKeepsVisualDraftAndExternalPackageChange();
    }

    private static void AssertFormattingBackToDefaultsRetainsHtmlState()
    {
        using (var fixture = new Fixture(caption: "Literal <caption> & text"))
        using (var host = new NativeScaleformExportLoader())
        {
            host.LoadExport(fixture.First);
            var editor = Designer(host); SelectTitle(editor);
            var original = editor.SelectedElement.Item.Definition.GetTextFormat();
            editor.ApplySelectedTextFormat(original with { FontSize = 22, LetterSpacing = 2 });
            editor.ApplySelectedTextFormat(original);
            editor.ReloadDraft(); SelectTitle(editor);
            Assert.AreEqual("Literal <caption> & text", ((TextBox)editor.FindName("VisualText")).Text);
            Assert.AreEqual(original, editor.SelectedElement.Item.Definition.GetTextFormat());
            Compile(host); SelectTitle(editor);
            Assert.AreEqual("Literal <caption> & text", ((TextBox)editor.FindName("VisualText")).Text,
                "Restoring default formatting must preserve the HTML flag associated with the canonical escaped string.");
        }
        using (var fixture = new Fixture(caption: "<p data-x='>'><font size='16'>Caption</font></p>", html: true))
        using (var host = new NativeScaleformExportLoader())
        {
            host.LoadExport(fixture.First);
            var editor = Designer(host); SelectTitle(editor);
            Assert.AreEqual("Caption", ((TextBox)editor.FindName("VisualText")).Text);
            editor.ApplySelectedText("Updated");
            Assert.AreEqual("Updated", ((TextBox)editor.FindName("VisualText")).Text);
            StringAssert.Contains(editor.SelectedElement.Item.Definition.TextValue, "data-x='>'");
        }
    }

    private static void AssertTextFormattingSurvivesUndoNavigationAndPackageApply()
    {
        using var fixture = new Fixture(caption: "Literal <title> & text");
        using var host = new NativeScaleformExportLoader();
        host.LoadExport(fixture.First);
        var editor = Designer(host);
        SelectTitle(editor);
        var originalFormat = editor.SelectedElement.Item.Definition.GetTextFormat();
        var desired = originalFormat with { FontSize = 22.5, Color = 0xE04080FF, LetterSpacing = 2.25, Alignment = ScaleformTextAlignment.Center };
        byte[] original = MovieBytes(fixture.First);
        editor.ApplySelectedTextFormat(desired);
        Assert.AreEqual(desired, editor.SelectedElement.Item.Definition.GetTextFormat());
        Assert.AreEqual("Literal <title> & text", ((TextBox)editor.FindName("VisualText")).Text);
        Assert.HasCount(1, editor.Draft.TextFormats);
        editor.ApplySelectedText("Updated <caption>\nSecond line");
        host.LoadExport(fixture.Second);
        Assert.IsFalse(editor.Draft.IsModified, "Another movie must not inherit text formatting.");
        host.LoadExport(fixture.First);
        SelectTitle(editor);
        Assert.AreEqual(desired, editor.SelectedElement.Item.Definition.GetTextFormat());
        Assert.AreEqual("Updated <caption>\nSecond line", ((TextBox)editor.FindName("VisualText")).Text);
        CollectionAssert.AreEqual(original, MovieBytes(fixture.First), "Formatting stays in the draft until apply.");
        editor.Undo();
        Assert.AreEqual("Literal <title> & text", ((TextBox)editor.FindName("VisualText")).Text);
        editor.Undo();
        Assert.IsFalse(editor.Draft.IsModified);
        Assert.AreEqual(originalFormat, editor.SelectedElement.Item.Definition.GetTextFormat());
        editor.Redo(); editor.Redo();
        Compile(host);
        var reopened = ScaleformDocument.Parse(MovieBytes(fixture.First));
        var field = ScaleformScene.Build(reopened).Definitions[1];
        Assert.AreEqual(desired, field.GetTextFormat());
        StringAssert.Contains(field.TextValue, "Updated &lt;caption&gt;<br/>");
        Assert.IsFalse(editor.Draft.IsModified);
        CollectionAssert.AreEqual(new byte[] { 0xFA, 0xCE, 0xDE, 0xAD }, reopened.Tags.Single(t => t.Code == 999).Payload.ToArray());
        CollectionAssert.AreEqual(Movie(caption: "Literal <title> & text"), MovieBytes(fixture.Second));
    }

    private static void AssertTextFormattingInspectorCommitsAndFormatsNewArtwork()
    {
        var document = ScaleformDocument.Parse(Movie(includeFonts: true));
        var originalField = document.Tags.Single(t => t.Code == 37);
        originalField.SetTextFormat(originalField.GetTextFormat() with { FontId = 3, FontName = "UnknownSharedFont" });
        var draft = new ScaleformVisualDraft();
        var texts = new Dictionary<string, string>();
        var editor = new ScaleformVisualEditor();
        editor.LoadMovie(document, _ => null, draft, texts);
        SelectTitle(editor);
        var font = (ComboBox)editor.FindName("TextFont");
        Assert.AreEqual("UnknownSharedFont", ((ScaleformVisualEditor.TextFontChoice)font.SelectedItem).Name,
            "The inspector must show an HTML face override even when its binary font ID names a different movie font.");
        font.SelectedItem = font.Items.Cast<ScaleformVisualEditor.TextFontChoice>().Single(f => f.Id == 3 && f.Name == "$FontA");
        Assert.AreEqual((ushort)3, editor.SelectedElement.Item.Definition.GetTextFormat().FontId);
        ((TextBox)editor.FindName("TextFontSize")).Text = "28";
        ((TextBox)editor.FindName("TextLetterSpacing")).Text = "1.5";
        ((TextBox)editor.FindName("PositionX")).Text = "73";
        Assert.IsTrue(editor.CommitPendingFields());
        Assert.AreEqual(73, editor.SelectedElement.Matrix.OffsetX, "Transform and formatting fields must commit together without refreshing away pending input.");
        var format = editor.SelectedElement.Item.Definition.GetTextFormat();
        Assert.AreEqual(28, format.FontSize);
        Assert.AreEqual(1.5, format.LetterSpacing);
        ((TextBox)editor.FindName("TextFontSize")).Text = "NaN";
        Assert.IsFalse(editor.CommitPendingFields(), "Invalid pending formatting must block apply/navigation.");
        Assert.AreEqual(format, editor.SelectedElement.Item.Definition.GetTextFormat());
        int frame = draft.Frame;
        var slider = (Slider)editor.FindName("FrameSlider");
        slider.Value = frame == 1 ? 2 : 1;
        Assert.AreEqual(frame, draft.Frame);
        Assert.AreEqual(frame, slider.Value, "A rejected frame change must restore the slider thumb.");
        ((TextBox)editor.FindName("TextFontSize")).Text = "28";
        Assert.IsTrue(editor.CommitPendingFields());
        editor.AddNewElement(new ScaleformElementSpec { Kind = ScaleformElementKind.Text, Name = "newCaption", FontId = 4 }, 0, 0);
        var createdFormat = editor.SelectedElement.Item.Definition.GetTextFormat() with { Color = 0x10A050FF, FontSize = 19, LetterSpacing = 3, Alignment = ScaleformTextAlignment.Right };
        editor.ApplySelectedTextFormat(createdFormat);
        editor.ApplySelectedText("New <field>");
        var compiled = ScaleformDocument.Parse(document.Compile());
        ScaleformVisualEditor.ApplyDraft(compiled, draft, texts);
        var reloaded = ScaleformDocument.Parse(compiled.Compile());
        var field = ScaleformScene.Build(reloaded).Items.Single(i => i.InstanceName == "newCaption").Definition;
        Assert.AreEqual(createdFormat, field.GetTextFormat());
        StringAssert.Contains(field.TextValue, "New &lt;field&gt;");
    }

    private static void AssertAuthoredLibraryCopiesSurviveDeletingTheirFirstInstanceAndUndoingAnOpenGroup()
    {
        var source = ScaleformDocument.Parse(Movie());
        var draft = new ScaleformVisualDraft();
        var texts = new Dictionary<string, string>();
        var editor = new ScaleformVisualEditor();
        editor.LoadMovie(source, _ => null, draft, texts);
        editor.SetFrame(1);
        editor.AddNewElement(new ScaleformElementSpec { Kind = ScaleformElementKind.Button, Name = "newButton" }, 30, 40);
        string firstIdentity = editor.SelectedElement.Identity;
        ushort character = editor.SelectedElement.CharacterId;
        editor.AddSymbolAt(character, 100, 100);
        string copyIdentity = editor.SelectedElement.Identity;
        editor.SelectElement(firstIdentity); editor.RemoveSelected();
        Assert.IsFalse(editor.VisibleElements.Any(element => element.Identity == firstIdentity));
        Assert.IsTrue(editor.VisibleElements.Any(element => element.Identity == copyIdentity && element.Item.Definition?.Code == 39));
        var compiled = ScaleformDocument.Parse(source.Compile());
        ScaleformVisualEditor.ApplyDraft(compiled, draft, texts);
        var reloaded = ScaleformDocument.Parse(compiled.Compile());
        var copy = ScaleformScene.Build(reloaded).Items.Single(element => element.CharacterId == character);
        Assert.IsNotNull(copy.Definition, "Deleting an authored symbol's first instance must retain artwork used by its Library copies.");
        Assert.IsFalse(string.IsNullOrEmpty(copy.InstanceName));

        editor = new ScaleformVisualEditor(); draft = new ScaleformVisualDraft();
        editor.LoadMovie(source, _ => null, draft, new Dictionary<string, string>());
        editor.AddNewElement(new ScaleformElementSpec { Kind = ScaleformElementKind.Panel, Name = "temporaryGroup" }, 0, 0);
        editor.EnterGroup(editor.SelectedElement.CharacterId);
        editor.Undo();
        Assert.IsNull(draft.SpriteId, "Undoing an open group's creation must return to the movie canvas.");
        Assert.IsFalse(draft.IsModified);
        Assert.IsTrue(editor.VisibleElements.Any(element => element.Name == "Title"));
    }

    private static void AssertMoveResizeAndUndoLeaveTheSourceUntouched()
    {
        byte[] original = Movie();
        var document = ScaleformDocument.Parse(original);
        var draft = new ScaleformVisualDraft();
        var texts = new Dictionary<string, string>();
        var editor = new ScaleformVisualEditor();
        editor.LoadMovie(document, _ => null, draft, texts);
        var title = editor.VisibleElements.Single(x => x.Name == "Title");
        Assert.IsTrue(editor.SelectElement(title.Identity));
        double width = title.WorldBounds.Width, height = title.WorldBounds.Height;
        editor.MoveSelectedBy(24.5, -12.25);
        AssertPosition(editor.SelectedElement.Matrix, 44.5, 27.75);
        editor.ResizeSelected(width * 2, height * 2);
        Assert.AreEqual(width * 2, editor.SelectedElement.WorldBounds.Width, .001);
        Assert.AreEqual(height * 2, editor.SelectedElement.WorldBounds.Height, .001);
        Assert.IsTrue(draft.IsModified);
        CollectionAssert.AreEqual(original, document.Compile(), "Canvas gestures must edit the isolated draft, not its source document.");

        editor.Undo();
        Assert.AreEqual(width, editor.SelectedElement.WorldBounds.Width, .001);
        AssertPosition(editor.SelectedElement.Matrix, 44.5, 27.75);
        editor.Undo();
        AssertPosition(editor.SelectedElement.Matrix, 20, 40);
        Assert.IsFalse(draft.IsModified, "Undoing every visual operation must clear the pending change.");
        editor.Redo();
        editor.Redo();
        AssertPosition(editor.SelectedElement.Matrix, 44.5, 27.75);
        Assert.AreEqual(width * 2, editor.SelectedElement.WorldBounds.Width, .001);
        editor.ApplySelectedText("A caption edited without code");
        Assert.AreEqual("A caption edited without code", texts["0"]);
        CollectionAssert.AreEqual(original, document.Compile());
    }

    private static void AssertNestedMovementUsesParentCoordinates()
    {
        var document = ScaleformDocument.Parse(Movie());
        var editor = new ScaleformVisualEditor();
        var draft = new ScaleformVisualDraft();
        editor.LoadMovie(document, _ => null, draft, new Dictionary<string, string>());
        var nested = editor.VisibleElements.Single(x => x.Name == "Group").Children.Single(x => x.Name == "Inside");
        Assert.IsTrue(editor.SelectElement(nested.Identity));
        editor.MoveSelectedBy(10, 20);
        AssertPosition(editor.SelectedElement.Matrix, 10, 20); // The parent has a 2x scale.
        editor.Undo();
        AssertPosition(editor.SelectedElement.Matrix, 5, 10);
        editor.EnterGroup(2);
        var child = editor.VisibleElements.Single(x => x.Name == "Inside");
        Assert.IsTrue(editor.SelectElement(child.Identity));
        editor.MoveSelectedBy(10, 15);
        // Inside a group the editor presents that group's local coordinates.
        AssertPosition(editor.SelectedElement.Matrix, 15, 25);
        editor.Undo();
        AssertPosition(editor.SelectedElement.Matrix, 5, 10);
        Assert.IsFalse(draft.IsModified);
        AssertPosition(ScaleformScene.Build(document, 2).Items.Single().Matrix, 5, 10);
    }

    private static void AssertBlankNestedIntroChoosesVisibleAuthoringFrame()
    {
        byte[] original = IntroMovie();
        var document = ScaleformDocument.Parse(original);
        // The root wrapper itself has full opacity on both frames. Only its nested
        // artwork is transparent in the intro, so counting root instances is insufficient.
        Assert.AreEqual(1, ScaleformScene.Build(document).Items.Single().ColorTransform.AlphaMultiplier);
        Assert.AreEqual(0, ScaleformScene.Build(document, 2, 1).Items.Single().ColorTransform.AlphaMultiplier);
        Assert.AreEqual(1, ScaleformScene.Build(document, 2, 2).Items.Single().ColorTransform.AlphaMultiplier);
        var draft = new ScaleformVisualDraft();
        var editor = new ScaleformVisualEditor();
        editor.LoadMovie(document, _ => null, draft, new Dictionary<string, string>());
        Assert.AreEqual(2, draft.Frame, "An opaque wrapper around invisible intro artwork must not keep the canvas on the blank frame.");
        editor.SetFrame(1);
        Assert.AreEqual(1, draft.Frame, "Choosing an authoring pose must leave the original intro frame accessible.");
        editor.SetFrame(2);
        Assert.AreEqual(2, draft.Frame);
        CollectionAssert.AreEqual(original, document.Compile(), "Choosing and scrubbing authoring poses must never change movie bytes.");
        Assert.IsFalse(draft.IsModified);
    }

    private static void AssertLibraryAddRemoveAndUndoAreReversible()
    {
        var document = ScaleformDocument.Parse(Movie());
        var draft = new ScaleformVisualDraft();
        var editor = new ScaleformVisualEditor();
        editor.LoadMovie(document, _ => null, draft, new Dictionary<string, string>());
        int originalCount = editor.VisibleElements.Count;
        editor.AddSymbolAt(1, 125, 175);
        Assert.HasCount(1, draft.Additions);
        Assert.AreEqual(originalCount + 1, editor.VisibleElements.Count);
        Assert.AreEqual((ushort)1, editor.SelectedElement.CharacterId);
        AssertPosition(editor.SelectedElement.Matrix, 125, 175);
        editor.RemoveSelected();
        Assert.AreEqual(originalCount, editor.VisibleElements.Count);
        editor.Undo();
        Assert.AreEqual(originalCount + 1, editor.VisibleElements.Count);
        editor.Undo();
        Assert.AreEqual(originalCount, editor.VisibleElements.Count);
        Assert.IsFalse(draft.IsModified);
        editor.Redo();
        Assert.AreEqual(originalCount + 1, editor.VisibleElements.Count);
        CollectionAssert.AreEqual(Movie(), document.Compile());
    }

    private static void AssertVisualDraftsSurviveNavigationAndApplyOnlyTheirMovie()
    {
        using var fixture = new Fixture();
        using var editor = new NativeScaleformExportLoader();
        byte[] firstOriginal = fixture.First.Data, secondOriginal = fixture.Second.Data;
        editor.LoadExport(fixture.First);
        var visual = Designer(editor);
        SelectTitle(visual);
        visual.MoveSelectedBy(31.75, 14.5);
        visual.ApplySelectedText("First movie's caption");
        editor.LoadExport(fixture.Second);
        SelectTitle(visual);
        visual.MoveSelectedBy(-8.5, 7.25);
        editor.LoadExport(fixture.First);
        SelectTitle(visual);
        AssertPosition(visual.SelectedElement.Matrix, 51.75, 54.5);
        CollectionAssert.AreEqual(firstOriginal, fixture.First.Data);
        CollectionAssert.AreEqual(secondOriginal, fixture.Second.Data);
        Compile(editor);
        StringAssert.Contains(editor.Status, "Compiled");
        var applied = ScaleformDocument.Parse(MovieBytes(fixture.First));
        AssertPosition(ScaleformScene.Build(applied).Items.Single(x => x.Name == "Title").Matrix, 51.75, 54.5);
        Assert.AreEqual("First movie's caption", applied.Tags[0].TextValue);
        Assert.AreEqual(73, fixture.First.GetProperty<IntProperty>("EditorTestMarker").Value);
        CollectionAssert.AreEqual(secondOriginal, fixture.Second.Data);

        editor.LoadExport(fixture.Second);
        SelectTitle(visual);
        AssertPosition(visual.SelectedElement.Matrix, 11.5, 47.25);
        visual.Undo();
        AssertPosition(visual.SelectedElement.Matrix, 20, 40);
        visual.Redo();
        AssertPosition(visual.SelectedElement.Matrix, 11.5, 47.25);
        Compile(editor);
        AssertPosition(ScaleformScene.Build(ScaleformDocument.Parse(MovieBytes(fixture.Second))).Items.Single(x => x.Name == "Title").Matrix, 11.5, 47.25);
    }

    private static void AssertLibraryInstancesCompileAndCanBeRemoved()
    {
        using var fixture = new Fixture();
        using var editor = new NativeScaleformExportLoader();
        editor.LoadExport(fixture.First);
        var visual = Designer(editor);
        visual.AddSymbolAt(1, 100, 150);
        Compile(editor);
        StringAssert.Contains(editor.Status, "Compiled");
        var addedMovie = ScaleformDocument.Parse(MovieBytes(fixture.First));
        var addedScene = ScaleformScene.Build(addedMovie);
        Assert.HasCount(3, addedScene.Items);
        var added = addedScene.Items.Single(x => x.Name is not ("Title" or "Group"));
        Assert.AreEqual((ushort)1, added.CharacterId);
        AssertPosition(added.Matrix, 100, 150);
        Assert.IsTrue(visual.SelectElement(visual.VisibleElements.Single(x => x.Name is not ("Title" or "Group")).Identity));
        visual.RemoveSelected();
        Compile(editor);
        StringAssert.Contains(editor.Status, "Compiled");
        var removedMovie = ScaleformDocument.Parse(MovieBytes(fixture.First));
        var removedScene = ScaleformScene.Build(removedMovie);
        Assert.HasCount(2, removedScene.Items);
        Assert.IsTrue(removedScene.Definitions.ContainsKey(1), "Removing an instance must retain its reusable artwork.");
        AssertPosition(removedScene.Items.Single(x => x.Name == "Title").Matrix, 20, 40);
        CollectionAssert.AreEqual(Movie(), MovieBytes(fixture.Second));
    }

    private static void AssertEditingAnInheritedFrameDoesNotMoveAnEarlierFrame()
    {
        foreach (bool includeSecondFrameUpdate in new[] { false, true })
        {
            using var fixture = new Fixture(includeSecondFrameUpdate);
            using var editor = new NativeScaleformExportLoader();
            editor.LoadExport(fixture.First);
            var visual = Designer(editor);
            visual.SetFrame(2);
            SelectTitle(visual);
            AssertPosition(visual.SelectedElement.Matrix, 20, 40);
            visual.MoveSelectedBy(30, 25);
            Compile(editor);
            StringAssert.Contains(editor.Status, "Compiled");
            var movie = ScaleformDocument.Parse(MovieBytes(fixture.First));
            AssertPosition(ScaleformScene.Build(movie, frame: 1).Items.Single(x => x.Name == "Title").Matrix, 20, 40);
            AssertPosition(ScaleformScene.Build(movie, frame: 2).Items.Single(x => x.Name == "Title").Matrix, 50, 65);
            Assert.AreEqual((ushort)2, movie.FrameCount);
        }
    }

    private static void AssertFailedApplyKeepsVisualDraftAndExternalPackageChange()
    {
        using var fixture = new Fixture();
        using var editor = new NativeScaleformExportLoader();
        editor.LoadExport(fixture.First);
        var visual = Designer(editor);
        SelectTitle(visual);
        visual.MoveSelectedBy(16, 32);
        var external = ScaleformDocument.Parse(MovieBytes(fixture.First));
        external.Tags.Single(x => x.Code == 9).SetBackgroundColor(90, 80, 70);
        fixture.First.WriteProperty(new ImmutableByteArrayProperty(external.Compile(), "RawData"));
        byte[] externalBytes = fixture.First.Data;
        Compile(editor);
        StringAssert.Contains(editor.Status, "another editor");
        CollectionAssert.AreEqual(externalBytes, fixture.First.Data);
        SelectTitle(visual);
        AssertPosition(visual.SelectedElement.Matrix, 36, 72);
        visual.Undo();
        AssertPosition(visual.SelectedElement.Matrix, 20, 40);
    }

    private static void AssertPendingInspectorFieldsCommitBeforeApplyAndNavigation()
    {
        using var fixture = new Fixture();
        using var editor = new NativeScaleformExportLoader();
        editor.LoadExport(fixture.First);
        var visual = Designer(editor);
        SelectTitle(visual);
        SetPendingX(visual, "51.25");
        AssertPosition(visual.SelectedElement.Matrix, 20, 40);
        Compile(editor); // Flush the field without raising LostKeyboardFocus or pressing Enter.
        StringAssert.Contains(editor.Status, "Compiled");
        AssertPosition(ScaleformScene.Build(ScaleformDocument.Parse(MovieBytes(fixture.First))).Items.Single(x => x.Name == "Title").Matrix, 51.25, 40);

        SelectTitle(visual);
        byte[] applied = fixture.First.Data;
        SetPendingX(visual, "98.5");
        editor.LoadExport(fixture.Second);
        CollectionAssert.AreEqual(applied, fixture.First.Data, "Navigation must capture the pending field in the draft without applying it.");
        editor.LoadExport(fixture.First);
        SelectTitle(visual);
        AssertPosition(visual.SelectedElement.Matrix, 98.5, 40);
        visual.Undo();
        AssertPosition(visual.SelectedElement.Matrix, 51.25, 40);
        visual.Redo();
        AssertPosition(visual.SelectedElement.Matrix, 98.5, 40);
        Compile(editor);
        StringAssert.Contains(editor.Status, "Compiled");
        AssertPosition(ScaleformScene.Build(ScaleformDocument.Parse(MovieBytes(fixture.First))).Items.Single(x => x.Name == "Title").Matrix, 98.5, 40);

        SelectTitle(visual);
        byte[] valid = fixture.First.Data;
        SetPendingX(visual, "invalid position");
        Compile(editor);
        CollectionAssert.AreEqual(valid, fixture.First.Data, "An invalid focused field must not alter package bytes.");
        StringAssert.Contains(editor.Status, "invalid");
    }

    private static void SetPendingX(ScaleformVisualEditor visual, string value)
    {
        var field = (TextBox)visual.FindName("PositionX");
        // Logical focus reproduces an uncommitted inspector edit without displaying
        // a test window or stealing keyboard focus from the user's desktop.
        FocusManager.SetIsFocusScope(visual, true);
        FocusManager.SetFocusedElement(visual, field);
        field.Text = value;
        Assert.AreSame(field, FocusManager.GetFocusedElement(visual));
    }

    private static void AssertLiteralTextAndHtmlLineBreaksSurviveVisualEditing()
    {
        using (var fixture = new Fixture(caption: "Hello <name> & welcome"))
        using (var editor = new NativeScaleformExportLoader())
        {
            editor.LoadExport(fixture.First);
            var visual = Designer(editor);
            SelectTitle(visual);
            Assert.AreEqual("Hello <name> & welcome", ((TextBox)visual.FindName("VisualText")).Text);
            const string replacement = "Use <name> and <left> & keep these literals";
            visual.ApplySelectedText(replacement);
            Compile(editor);
            StringAssert.Contains(editor.Status, "Compiled");
            Assert.AreEqual(replacement, ScaleformDocument.Parse(MovieBytes(fixture.First)).Tags[0].TextValue);
            SelectTitle(visual);
            Assert.AreEqual(replacement, ((TextBox)visual.FindName("VisualText")).Text);
        }
        using (var fixture = new Fixture(caption: "<p><font size=\"16\">First<br/>Second</font></p>", html: true))
        using (var editor = new NativeScaleformExportLoader())
        {
            editor.LoadExport(fixture.First);
            var visual = Designer(editor);
            SelectTitle(visual);
            Assert.AreEqual("First\nSecond", ((TextBox)visual.FindName("VisualText")).Text);
            visual.ApplySelectedText("Changed\n<name>&");
            Compile(editor);
            StringAssert.Contains(editor.Status, "Compiled");
            string compiled = ScaleformDocument.Parse(MovieBytes(fixture.First)).Tags[0].TextValue;
            StringAssert.Contains(compiled, "<br/>");
            StringAssert.Contains(compiled, "&lt;name&gt;&amp;");
            SelectTitle(visual);
            Assert.AreEqual("Changed\n<name>&", ((TextBox)visual.FindName("VisualText")).Text);
        }
    }

    private static void AssertNewInstancesKeepEarlierFramesWhenMovedOrRemovedLater()
    {
        using (var fixture = new Fixture())
        using (var editor = new NativeScaleformExportLoader())
        {
            editor.LoadExport(fixture.First);
            var visual = Designer(editor);
            visual.AddSymbolAt(1, 100, 150);
            string identity = visual.SelectedElement.Identity;
            ushort depth = visual.SelectedElement.Depth;
            visual.SetFrame(2);
            Assert.IsTrue(visual.SelectElement(identity));
            visual.MoveSelectedBy(20, 30);
            Assert.HasCount(1, visual.Draft.Keyframes);
            AssertPosition(visual.Draft.Additions.Single().Matrix, 100, 150);
            Compile(editor);
            StringAssert.Contains(editor.Status, "Compiled");
            var movie = ScaleformDocument.Parse(MovieBytes(fixture.First));
            AssertPosition(ScaleformScene.Build(movie, frame: 1).Items.Single(x => x.Depth == depth).Matrix, 100, 150);
            AssertPosition(ScaleformScene.Build(movie, frame: 2).Items.Single(x => x.Depth == depth).Matrix, 120, 180);
        }
        using (var fixture = new Fixture())
        using (var editor = new NativeScaleformExportLoader())
        {
            editor.LoadExport(fixture.First);
            var visual = Designer(editor);
            visual.AddSymbolAt(1, 100, 150);
            string identity = visual.SelectedElement.Identity;
            ushort depth = visual.SelectedElement.Depth;
            visual.SetFrame(2);
            Assert.IsTrue(visual.SelectElement(identity));
            visual.RemoveSelected();
            Assert.HasCount(1, visual.Draft.Additions);
            Assert.HasCount(1, visual.Draft.Removals);
            Compile(editor);
            StringAssert.Contains(editor.Status, "Compiled");
            var movie = ScaleformDocument.Parse(MovieBytes(fixture.First));
            AssertPosition(ScaleformScene.Build(movie, frame: 1).Items.Single(x => x.Depth == depth).Matrix, 100, 150);
            Assert.IsFalse(ScaleformScene.Build(movie, frame: 2).Items.Any(x => x.Depth == depth));
        }
    }

    private static void SelectTitle(ScaleformVisualEditor editor) =>
        Assert.IsTrue(editor.SelectElement(editor.VisibleElements.Single(x => x.Name == "Title").Identity));

    private static ScaleformVisualEditor Designer(NativeScaleformExportLoader editor)
    {
        Assert.IsTrue(editor.HasMovie, editor.Status);
        return (ScaleformVisualEditor)editor.FindName("Designer");
    }

    private static void Compile(NativeScaleformExportLoader editor) =>
        typeof(NativeScaleformExportLoader).GetMethod("CompileApply_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, [editor, new RoutedEventArgs()]);

    private static void AssertPosition(ScaleformMatrix matrix, double x, double y)
    {
        Assert.AreEqual(x, matrix.OffsetX, .001);
        Assert.AreEqual(y, matrix.OffsetY, .001);
    }

    private static byte[] MovieBytes(ExportEntry export) => export.GetProperty<ImmutableByteArrayProperty>("RawData").Bytes;

    private sealed class Fixture : IDisposable
    {
        private readonly IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("ScaleformVisualTest.pcc", MEGame.LE3);
        public ExportEntry First { get; }
        public ExportEntry Second { get; }
        public Fixture(bool includeSecondFrameUpdate = true, string caption = "Original caption", bool html = false)
        {
            First = package.CreateExport("FirstVisualMovie", "GFxMovieInfo", indexed: false);
            Second = package.CreateExport("SecondVisualMovie", "GFxMovieInfo", indexed: false);
            foreach (var export in new[] { First, Second })
            {
                export.WriteProperty(new ImmutableByteArrayProperty(Movie(includeSecondFrameUpdate, caption, html), "RawData"));
                export.WriteProperty(new IntProperty(73, "EditorTestMarker"));
            }
        }
        public void Dispose() => package.Dispose();
    }

    private static byte[] Movie(bool includeSecondFrameUpdate = true, string caption = "Original caption", bool html = false, bool includeFonts = false)
    {
        byte[] text = Join(BitConverter.GetBytes((ushort)1), Rectangle(0, 2400, 0, 800), [0x80, html ? (byte)2 : (byte)0], CString("caption"), CString(caption));
        byte[] sprite = Join(BitConverter.GetBytes((ushort)2), BitConverter.GetBytes((ushort)1), Placement(1, 1, 5, 10, "Inside"), Tag(1, []), Tag(0, []));
        byte[] body = Join(Rectangle(0, 8000, 0, 6000), [0, 24, 2, 0],
            includeFonts ? Tag(71, Join(CString("sharedfonts.swf"), [1, 0, 2, 0, 3, 0], CString("$FontA"), [4, 0], CString("$FontB"))) : [],
            Tag(37, text), Tag(39, sprite), Tag(9, [10, 20, 30]),
            Placement(1, 1, 20, 40, "Title"), Placement(2, 2, 150, 80, "Group", scale: 2),
            Tag(999, [0xFA, 0xCE, 0xDE, 0xAD]), Tag(1, []),
            includeSecondFrameUpdate ? Tag(26, [1, 1, 0]) : [], // A matrix-less move, or no placement record at all.
            Tag(1, []), Tag(0, []));
        return Join("FWS"u8.ToArray(), [10], BitConverter.GetBytes((uint)body.Length + 8), body);
    }

    private static byte[] IntroMovie()
    {
        var shape = new Bits();
        shape.Write(1, 4); shape.Write(0, 4); // One fill-index bit and no line styles.
        shape.Write(0, 1); shape.Write(5, 5); // MoveTo + FillStyle1.
        shape.Write(16, 5); shape.Write(0, 16); shape.Write(0, 16); shape.Write(1, 1);
        foreach (var delta in new[] { (2000, 0), (0, 1200), (-2000, 0), (0, -1200) })
        {
            shape.Write(1, 1); shape.Write(1, 1); shape.Write(14, 4); shape.Write(1, 1);
            shape.Write(delta.Item1, 16); shape.Write(delta.Item2, 16);
        }
        shape.Write(0, 6);
        byte[] definition = Tag(32, Join([1, 0], Rectangle(0, 2000, 0, 1200), [1, 0, 0, 220, 240, 255, 0], shape.Bytes()));
        byte[] sprite = Tag(39, Join([2, 0, 2, 0],
            Tag(26, Join([14, 1, 0, 1, 0, 0], ColorMultiplier(0))), Tag(1, []),
            Tag(26, Join([9, 1, 0], ColorMultiplier(256))), Tag(1, []), Tag(0, [])));
        byte[] body = Join(Rectangle(0, 8000, 0, 6000), [0, 24, 2, 0], definition, sprite,
            Placement(2, 1, 20, 40, "Wrapper"), Tag(1, []), Tag(1, []), Tag(0, []));
        return Join("FWS"u8.ToArray(), [10], BitConverter.GetBytes((uint)body.Length + 8), body);
    }

    private static byte[] ColorMultiplier(int alpha)
    {
        var bits = new Bits(); bits.Write(0, 1); bits.Write(1, 1); bits.Write(10, 4);
        foreach (int value in new[] { 256, 256, 256, alpha }) bits.Write(value, 10);
        return bits.Bytes();
    }

    private static byte[] Placement(ushort character, ushort depth, double x, double y, string name, double scale = 1)
    {
        var bits = new Bits();
        bits.Write(scale == 1 ? 0 : 1, 1);
        if (scale != 1) { bits.Write(19, 5); bits.Write((int)(scale * 65536), 19); bits.Write((int)(scale * 65536), 19); }
        bits.Write(0, 1); bits.Write(16, 5); bits.Write((int)(x * 20), 16); bits.Write((int)(y * 20), 16);
        return Tag(26, Join([0x26], BitConverter.GetBytes(depth), BitConverter.GetBytes(character), bits.Bytes(), CString(name)));
    }

    private static byte[] Rectangle(int xmin, int xmax, int ymin, int ymax)
    {
        var bits = new Bits(); bits.Write(15, 5);
        foreach (int value in new[] { xmin, xmax, ymin, ymax }) bits.Write(value, 15);
        return bits.Bytes();
    }

    private static byte[] CString(string value) => Encoding.UTF8.GetBytes(value + "\0");
    private static byte[] Tag(ushort code, byte[] payload)
    {
        int headerSize = payload.Length < 63 ? 2 : 6;
        byte[] result = new byte[headerSize + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)((code << 6) | (headerSize == 2 ? payload.Length : 63)));
        if (headerSize == 6) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2), (uint)payload.Length);
        payload.CopyTo(result, headerSize); return result;
    }
    private static byte[] Join(params byte[][] values) => values.SelectMany(x => x).ToArray();
    private sealed class Bits
    {
        private readonly List<byte> bytes = [];
        private int position;
        public void Write(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--, position++)
            {
                if (position % 8 == 0) bytes.Add(0);
                bytes[^1] |= (byte)(((value >> i) & 1) << (7 - position % 8));
            }
        }
        public byte[] Bytes() => bytes.ToArray();
    }
}
