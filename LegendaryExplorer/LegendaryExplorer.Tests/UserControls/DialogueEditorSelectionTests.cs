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
        export.WriteProperties(new PropertyCollection
        {
            new ArrayProperty<IntProperty>("m_StartingList") { 0 },
            new ArrayProperty<StructProperty>("m_EntryList")
            {
                new("BioDialogEntryNode", entryProperties)
            },
            new ArrayProperty<NameProperty>("m_aSpeakerList")
        });
        var conversation = new ConversationExtended(export);
        conversation.LoadConversation(detailedParse: true);
        conversation.IsFirstParsed = true;

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
            typeof(WPFBase).GetMethod("RegisterPackage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(editor, [package]);
            editor.Conversations.Add(conversation);
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

            editor.SelectDialogueNodeByIndex(0);

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
