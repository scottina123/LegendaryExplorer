using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Shapes;
using LegendaryExplorer.UserControls.ExportLoaderControls.Scaleform;
using LegendaryExplorerCore.Scaleform;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class ScaleformBehaviorEditorTests
{
    [STATestMethod]
    public void NonCoderGraphsConnectUndoCommitAndSaveWithoutReplacingGameScripts()
    {
        var original = Movie();
        var movie = ScaleformDocument.Parse(original);
        var graph = new ScaleformBehaviorGraph();
        var editor = new ScaleformBehaviorEditor();
        int edits = 0;
        editor.DraftChanged += (_, _) => edits++;
        editor.LoadMovie(movie, graph, ["_root.OpenButton", "_root.Caption", "_root.Panel"], authoringFrame: 2);

        var button = editor.AddBehavior(ScaleformBehaviorTrigger.ButtonClick, "_root.OpenButton");
        Assert.AreEqual(2, button.Frame, "Click handlers start on the selected visible authoring frame.");
        var show = editor.AddAction(ScaleformBehaviorNodeKind.Show);
        Target(editor).Text = "_root.Panel";
        Assert.IsTrue(editor.CommitPendingFields());
        var caption = editor.AddAction(ScaleformBehaviorNodeKind.SetText);
        Target(editor).Text = "_root.Caption";
        ((TextBox)editor.FindName("ActionValue")).Text = "Ready to explore";
        Assert.IsTrue(editor.CommitPendingFields());
        Assert.AreEqual(show.Id, button.EntryNodeId);
        Assert.AreEqual(caption.Id, show.Next, "A new action automatically follows the selected action.");
        Assert.AreEqual("Ready to explore", caption.Value);
        CollectionAssert.AreEqual(original, movie.Compile(), "Graph authoring must leave source bytes untouched.");

        var branch = editor.AddAction(ScaleformBehaviorNodeKind.IfEquals);
        Target(editor).Text = "menuState";
        ((TextBox)editor.FindName("ActionValue")).Text = "open";
        Assert.IsTrue(editor.CommitPendingFields());
        var hide = editor.AddAction(ScaleformBehaviorNodeKind.Hide);
        Assert.IsTrue(editor.Connect(branch.Id, null));
        Assert.IsTrue(editor.Connect(branch.Id, hide.Id, falseBranch: true));
        Assert.AreEqual(hide.Id, branch.FalseNext);
        Assert.IsFalse(editor.Connect(hide.Id, show.Id), "The visual editor must reject an infinite execution loop.");
        Assert.IsTrue(string.IsNullOrEmpty(hide.Next));
        var canvas = (Canvas)editor.FindName("GraphCanvas");
        Assert.IsTrue(canvas.Children.OfType<Path>().Count() >= 3, "The authored sequence must have visible connections.");

        Assert.IsTrue(editor.SelectNode(hide.Id));
        editor.RemoveSelection();
        Assert.AreEqual(3, editor.SelectedFlow.Nodes.Count);
        editor.Undo();
        Assert.AreEqual(4, editor.SelectedFlow.Nodes.Count);
        Assert.AreEqual(hide.Id, editor.SelectedFlow.Nodes.Single(n => n.Id == branch.Id).FalseNext);
        editor.Redo();
        Assert.AreEqual(3, editor.SelectedFlow.Nodes.Count);
        editor.Undo();

        var function = editor.AddBehavior(ScaleformBehaviorTrigger.Function);
        ((TextBox)editor.FindName("FunctionName")).Text = "RevealPanel";
        ((TextBox)editor.FindName("BehaviorName")).Text = "Reveal the panel";
        Assert.IsTrue(editor.CommitPendingFields());
        var reveal = editor.AddAction(ScaleformBehaviorNodeKind.Show);
        Target(editor).Text = "_root.Panel";
        Assert.IsTrue(editor.CommitPendingFields());
        Assert.IsTrue(editor.SelectBehavior(button.Id));
        Assert.IsTrue(editor.SelectNode(caption.Id));
        var call = editor.AddAction(ScaleformBehaviorNodeKind.CallFunction);
        Assert.AreEqual("RevealPanel", call.Target, "Function actions offer the user's named reusable behaviors.");
        Assert.IsTrue(editor.Connect(caption.Id, call.Id));
        Assert.IsTrue(editor.Connect(call.Id, branch.Id));

        Assert.AreEqual(0, ScaleformBehaviorCompiler.Validate(movie, graph).Count);
        ScaleformBehaviorCompiler.Apply(movie, graph);
        var reopened = ScaleformDocument.Parse(movie.Compile());
        var saved = ScaleformBehaviorCompiler.Read(reopened);
        Assert.AreEqual(2, saved.Flows.Count);
        Assert.AreEqual("RevealPanel", saved.Flows.Single(f => f.Trigger == ScaleformBehaviorTrigger.Function).FunctionName);
        var savedButton = saved.Flows.Single(f => f.Trigger == ScaleformBehaviorTrigger.ButtonClick);
        Assert.AreEqual(2, savedButton.Frame);
        Assert.AreEqual("_root.OpenButton", savedButton.Target);
        Assert.AreEqual("Ready to explore", savedButton.Nodes.Single(n => n.Kind == ScaleformBehaviorNodeKind.SetText).Value);
        Assert.AreEqual(hide.Id, savedButton.Nodes.Single(n => n.Kind == ScaleformBehaviorNodeKind.IfEquals).FalseNext);
        Assert.IsTrue(reopened.Tags.Any(t => t.Code == 12 && t.Payload.Span.SequenceEqual(new byte[] { 0x07, 0 })),
            "Pre-existing game ActionScript must remain byte-for-byte present.");
        Assert.IsTrue(edits > 10);

        AssertPendingCoordinatesValidateBeforeNavigation();
    }

    private static void AssertPendingCoordinatesValidateBeforeNavigation()
    {
        var editor = new ScaleformBehaviorEditor();
        var graph = new ScaleformBehaviorGraph();
        editor.LoadMovie(ScaleformDocument.Parse(Movie()), graph, ["_root.Panel"]);
        editor.AddBehavior(ScaleformBehaviorTrigger.Frame);
        var move = editor.AddAction(ScaleformBehaviorNodeKind.SetPosition);
        ((TextBox)editor.FindName("ActionNumber")).Text = "invalid";
        Assert.IsFalse(editor.CommitPendingFields());
        Assert.IsFalse(editor.SelectNode("@event"), "Invalid pending fields must not be silently discarded on selection changes.");
        Assert.AreSame(move, editor.SelectedNode);
        Assert.AreEqual(0, move.Number);
        ((TextBox)editor.FindName("ActionNumber")).Text = "125.5";
        ((TextBox)editor.FindName("ActionNumber2")).Text = "42";
        Assert.IsTrue(editor.CommitPendingFields());
        Assert.AreEqual(125.5, move.Number);
        Assert.AreEqual(42, move.Number2);
        editor.Undo();
        Assert.AreEqual(0, editor.SelectedNode.Number);
        editor.Redo();
        Assert.AreEqual(125.5, editor.SelectedNode.Number);
        editor.RefreshTargets(["_root.NewPicture", "_root.Panel"]);
        Assert.IsTrue(editor.Targets.Contains("_root.NewPicture"), "New visual elements must become available to behavior actions.");
        Assert.AreEqual("_root.Panel", editor.SelectedNode.Target);
    }

    private static ComboBox Target(ScaleformBehaviorEditor editor) => (ComboBox)editor.FindName("ActionTarget");

    private static byte[] Movie()
    {
        // A two-frame AS2 movie with a font declaration and an existing Stop action.
        byte[] body = [8, 0, 0, 24, 2, 0, 0x0D, 0x0C, 1, 0, 0, 0, 4, (byte)'T', (byte)'e', (byte)'s', (byte)'t', 0, 0, 0, 0,
            2, 3, 0x07, 0, 0x40, 0, 0x40, 0, 0, 0];
        var movie = ScaleformDocument.Parse("FWS"u8.ToArray().Concat(new byte[] { 8 }).Concat(BitConverter.GetBytes((uint)body.Length + 8)).Concat(body).ToArray());
        foreach (var spec in new[]
                 {
                     new ScaleformElementSpec { Kind = ScaleformElementKind.Button, Name = "OpenButton", FontId = 1, Text = "Open" },
                     new ScaleformElementSpec { Kind = ScaleformElementKind.Panel, Name = "Panel" },
                     new ScaleformElementSpec { Kind = ScaleformElementKind.Text, Name = "Caption", FontId = 1, Text = "Caption" }
                 })
        {
            var created = movie.AddElementDefinition(spec);
            movie.AddPlacement(created.CharacterId, ScaleformMatrix.Identity).SetPlacementName(spec.Name);
        }
        return movie.Compile();
    }
}
