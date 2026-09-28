using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.DialogueEditor;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class DialogueEditorSelectionTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void NavigatingDialogueNodeDoesNotDirtyConversation()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(DialogueEditorWindow).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("DialogueEditorTest.pcc", MEGame.LE3);
        ExportEntry export = package.CreateExport("selection_test_dlg", "BioConversation", indexed: false);
        var entryProperties = new PropertyCollection
        {
            new EnumProperty("GUI_STYLE_NONE", "EConvGUIStyles", package.Game, "eGUIStyle"),
            new IntProperty(-1, "nSpeakerIndex"),
            new IntProperty(-2, "nListenerIndex"),
            new IntProperty(-1, "nScriptIndex"),
            new StringRefProperty(1, "srText"),
            new BoolProperty(true, "bFireConditional"),
            new BoolProperty(true, "bSkippable"),
            new IntProperty(-1, "nConditionalFunc"),
            new IntProperty(-1, "nConditionalParam"),
            new IntProperty(-1, "nStateTransition"),
            new IntProperty(-1, "nStateTransitionParam"),
            new IntProperty(1, "nCameraIntimacy"),
            new ArrayProperty<StructProperty>("ReplyListNew")
        };
        var replyProperties = entryProperties.DeepClone();
        replyProperties.RemoveNamedProperty("ReplyListNew");
        replyProperties.Add(new EnumProperty("REPLY_STANDARD", "EReplyTypes", package.Game, "ReplyType"));
        replyProperties.Add(new ArrayProperty<IntProperty>("EntryList"));
        export.WriteProperties(new PropertyCollection
        {
            new ArrayProperty<IntProperty>("m_StartingList") { 0 },
            new ArrayProperty<StructProperty>("m_EntryList")
            {
                new("BioDialogEntryNode", entryProperties),
                new("BioDialogEntryNode", entryProperties.DeepClone()),
                new("BioDialogEntryNode", entryProperties.DeepClone())
            },
            new ArrayProperty<StructProperty>("m_ReplyList")
            {
                new("BioDialogReplyNode", replyProperties)
            },
            new ArrayProperty<NameProperty>("m_aSpeakerList")
        });
        var conversation = new ConversationExtended(export);
        conversation.LoadConversation(detailedParse: true);
        conversation.IsFirstParsed = true;
        ExportEntry otherExport = package.CreateExport("other_selection_test_dlg", "BioConversation", indexed: false);
        otherExport.Data = export.Data;
        var otherConversation = new ConversationExtended(otherExport);
        otherConversation.LoadConversation(detailedParse: true);
        otherConversation.IsFirstParsed = true;
        otherExport.EntryHasPendingChanges = false;
        byte[] otherDataBeforeSelection = otherExport.Data;

        var editor = (DialogueEditorWindow)Activator.CreateInstance(
            typeof(DialogueEditorWindow),
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [false],
            null)!;
        editor.ShowActivated = false;
        editor.ShowInTaskbar = false;
        editor.Left = -10000;
        editor.Top = -10000;
        try
        {
            editor.Show();
            ((MenuItem)editor.FindName("AutoSaveView_MenuItem")).IsChecked = false;
            typeof(WPFBase).GetMethod("RegisterPackage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(editor, [package]);
            editor.Conversations.Add(conversation);
            editor.Conversations.Add(otherConversation);
            ((ListBox)editor.FindName("Conversations_ListBox")).SelectedItem = conversation;

            export.EntryHasPendingChanges = false;
            byte[] dataBeforeSelection = export.Data;
            string dirtyStack = null;
            export.EntryModifiedChanged += (_, _) =>
            {
                if (export.EntryHasPendingChanges)
                {
                    dirtyStack ??= Environment.StackTrace;
                }
            };

            var back = editor.NavigateSelectionBackCommand;
            var forward = editor.NavigateSelectionForwardCommand;
            Assert.IsFalse(back.CanExecute(null));
            Assert.IsFalse(forward.CanExecute(null));

            var firstNode = editor.SelectDialogueNodeByIndex(0).Node;
            var secondNode = editor.SelectDialogueNodeByIndex(1).Node;
            editor.SelectDialogueNodeByIndex(1); // Repeated selection must not add a history step.
            var replyNode = editor.SelectDialogueNodeByIndex(0, isReply: true).Node;
            Assert.IsTrue(back.CanExecute(null));
            Assert.IsFalse(forward.CanExecute(null));

            back.Execute(null);
            Assert.AreSame(secondNode, editor.SelectedDialogueNode);
            back.Execute(null);
            Assert.AreSame(firstNode, editor.SelectedDialogueNode);
            Assert.IsFalse(back.CanExecute(null));
            Assert.IsTrue(forward.CanExecute(null));

            forward.Execute(null);
            Assert.AreSame(secondNode, editor.SelectedDialogueNode);
            forward.Execute(null);
            Assert.AreSame(replyNode, editor.SelectedDialogueNode);
            Assert.IsFalse(forward.CanExecute(null));

            back.Execute(null);
            var thirdNode = editor.SelectDialogueNodeByIndex(2).Node;
            Assert.IsFalse(forward.CanExecute(null)); // A new selection replaces the forward branch.
            editor.RefreshView(); // Redrawing the graph must preserve node history.
            back.Execute(null);
            Assert.AreSame(secondNode, editor.SelectedDialogueNode);
            forward.Execute(null);
            Assert.AreSame(thirdNode, editor.SelectedDialogueNode);
            back.Execute(null);
            back.Execute(null);
            Assert.AreSame(firstNode, editor.SelectedDialogueNode);

            ((ListBox)editor.FindName("Conversations_ListBox")).SelectedItem = otherConversation;
            var otherNode = editor.SelectDialogueNodeByIndex(0).Node;
            back.Execute(null);
            Assert.AreEqual(conversation.UIndex, editor.SelectedConv.UIndex);
            Assert.AreSame(firstNode, editor.SelectedDialogueNode);
            forward.Execute(null);
            Assert.AreEqual(otherConversation.UIndex, editor.SelectedConv.UIndex);
            Assert.AreSame(otherNode, editor.SelectedDialogueNode);
            back.Execute(null);

            var viewportTabs = (TabControl)editor.FindName("BottomViewportTabControl");
            viewportTabs.SelectedItem = viewportTabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, "InterpData"));
            viewportTabs.SelectedItem = viewportTabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, "Matinee"));
            editor.SelectedDialogueNode.PlotChecksExpanded = true;
            editor.SelectedDialogueNode.PlotTransitionsExpanded = true;
            editor.SelectedDialogueNode.MatineeExpanded = true;

            Assert.AreEqual(-1, editor.SelectedDialogueNode.SpeakerIndex);
            Assert.AreEqual(-2, editor.SelectedDialogueNode.Listener);
            Assert.IsFalse(export.EntryHasPendingChanges, dirtyStack);
            CollectionAssert.AreEqual(dataBeforeSelection, export.Data);
            Assert.IsFalse(otherExport.EntryHasPendingChanges);
            CollectionAssert.AreEqual(otherDataBeforeSelection, otherExport.Data);

            editor.SelectDialogueNodeByIndex(1);
            editor.SelectDialogueNodeByIndex(2);
            editor.SelectedConv.EntryList.Remove(secondNode);
            thirdNode.NodeCount = 1;
            editor.RefreshView();
            back.Execute(null);
            Assert.AreSame(firstNode, editor.SelectedDialogueNode, "History must skip a deleted node.");
            forward.Execute(null);
            Assert.AreSame(thirdNode, editor.SelectedDialogueNode, "History must follow a node whose index changed.");

            typeof(DialogueEditorWindow).GetMethod("UnloadFile", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(editor, null);
            Assert.IsFalse(back.CanExecute(null));
            Assert.IsFalse(forward.CanExecute(null));
        }
        finally
        {
            MethodInfo closingMethod = typeof(DialogueEditorWindow).GetMethod(
                "DialogueEditorWPF_Closing", BindingFlags.Instance | BindingFlags.NonPublic)!;
            closingMethod.Invoke(editor, [editor, new CancelEventArgs()]);
            editor.Closing -= (CancelEventHandler)Delegate.CreateDelegate(
                typeof(CancelEventHandler), editor, closingMethod);
            typeof(WPFBase).GetMethod("UnLoadMEPackage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(editor, null);
            editor.Close();
        }
    }
}
