using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.Tools.Sequence_Editor;
using LegendaryExplorer.Tools.SequenceObjects;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Piccolo.Event;
using Forms = System.Windows.Forms;

namespace LegendaryExplorer.Tests.Tools.SequenceEditor;

[TestClass]
public class SequenceObjectReferenceTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void ObjectVariableDoubleClickEditsReferencesAndPreservesOtherGestures()
    {
        InitializeApplicationResources();
        var loaded = typeof(Settings).GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
        object previousLoaded = loaded.GetValue(null);
        bool previousTree = Settings.EntrySelector_UseTreeView;
        bool previousAutoSave = Settings.SequenceEditor_AutoSaveViewV2;
        bool previousOutputNumbers = Settings.SequenceEditor_ShowOutputNumbers;
        loaded.SetValue(null, false);
        Settings.EntrySelector_UseTreeView = true;
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ObjectReferenceGesture.pcc", MEGame.LE3);
        var sequence = package.CreateExport("Main_Sequence", "Sequence", indexed: false);
        sequence.WriteProperty(new ArrayProperty<ObjectProperty>("SequenceObjects"));
        var firstTarget = package.CreateExport("FirstTarget", "Actor", indexed: false);
        var nextTarget = package.CreateExport("NextTarget", "Actor", indexed: false);
        var importedTarget = new ImportEntry(package)
        {
            ObjectName = "ImportedTarget", ClassName = "Actor", PackageFile = "Engine"
        };
        package.AddImport(importedTarget);
        var objectVariable = package.CreateExport("SeqVar_Object", "SeqVar_Object", indexed: false);
        objectVariable.WriteProperty(new ObjectProperty(firstTarget, "ObjValue"));
        var boolVariable = package.CreateExport("SeqVar_Bool", "SeqVar_Bool", indexed: false);
        boolVariable.WriteProperty(new IntProperty(1, "bValue"));
        var intVariable = package.CreateExport("SeqVar_Int", "SeqVar_Int", indexed: false);
        intVariable.WriteProperty(new IntProperty(7, "IntValue"));
        foreach (var variable in new[] { objectVariable, boolVariable, intVariable })
        {
            KismetHelper.AddObjectToSequence(variable, sequence);
        }
        var editor = new SequenceEditorWPF(enableRecents: false, loadCustomSources: false)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
            UseSavedViews = false
        };
        try
        {
            typeof(WPFBase).GetProperty(nameof(WPFBase.Pcc))!.SetValue(editor, package);
            ((MenuItem)editor.FindName("AutoSaveView_MenuItem")).IsChecked = false;
            typeof(SequenceEditorWPF).GetMethod("LoadSequences", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(editor, null);
            editor.Show();
            editor.SelectedItem = editor.TreeViewRootNodes.Single(node => node.Entry == sequence);
            SelectVariable(editor, objectVariable);

            AcceptReference(editor, objectVariable, firstTarget, nextTarget, importedTarget);
            AcceptReference(editor, objectVariable, nextTarget, importedTarget, nextTarget);

            var beforeCancelNode = SelectVariable(editor, objectVariable);
            byte[] beforeCancel = objectVariable.Data.ToArray();
            PerformDoubleClick(editor, beforeCancelNode, Forms.MouseButtons.Left, true, selector =>
            {
                Assert.AreSame(importedTarget, selector.SelectedEntryItem);
                selector.SelectedEntryItem = nextTarget;
                selector.Close();
            });
            CollectionAssert.AreEqual(beforeCancel, objectVariable.Data, "Cancel must leave ObjValue unchanged.");
            Assert.AreSame(beforeCancelNode, FindVariable(editor, objectVariable), "Cancel must not rebuild the graph.");
            AssertReferenceState(editor, objectVariable, importedTarget);

            var beforeNoOp = SelectVariable(editor, objectVariable);
            PerformDoubleClick(editor, beforeNoOp, Forms.MouseButtons.Left, true,
                selector => selector.OKCommand.Execute(null));
            CollectionAssert.AreEqual(beforeCancel, objectVariable.Data, "Accepting the existing reference must not rewrite it.");
            Assert.AreSame(beforeNoOp, FindVariable(editor, objectVariable));

            AcceptReference(editor, objectVariable, importedTarget, null, nextTarget);
            PerformDoubleClick(editor, SelectVariable(editor, objectVariable), Forms.MouseButtons.Left, true, selector =>
            {
                Assert.AreEqual("0 Null", selector.SelectedEntryItem);
                selector.Close();
            });

            var node = SelectVariable(editor, objectVariable);
            Assert.IsFalse(PerformDoubleClick(editor, node, Forms.MouseButtons.Right, false).Handled);
            node.PosAtDragStart = node.GlobalFullBounds;
            node.PosAtDragStart.Offset(1, 0);
            Assert.IsFalse(PerformDoubleClick(editor, node, Forms.MouseButtons.Left, false, resetDragBounds: false).Handled);
            AssertReferenceState(editor, objectVariable, null);

            var integerNode = SelectVariable(editor, intVariable);
            Assert.IsFalse(PerformDoubleClick(editor, integerNode, Forms.MouseButtons.Left, false).Handled);
            Assert.AreEqual(7, intVariable.GetProperty<IntProperty>("IntValue").Value);
            var booleanNode = SelectVariable(editor, boolVariable);
            Assert.IsTrue(PerformDoubleClick(editor, booleanNode, Forms.MouseButtons.Left, false).Handled);
            Assert.AreEqual(0, boolVariable.GetProperty<IntProperty>("bValue").Value);
            Assert.AreEqual("False", FindVariable(editor, boolVariable).Value);
        }
        finally
        {
            editor.DisposeEmbeddedContent();
            editor.Close();
            Settings.EntrySelector_UseTreeView = previousTree;
            Settings.SequenceEditor_AutoSaveViewV2 = previousAutoSave;
            Settings.SequenceEditor_ShowOutputNumbers = previousOutputNumbers;
            loaded.SetValue(null, previousLoaded);
        }
    }

    private static void AcceptReference(SequenceEditorWPF editor, ExportEntry variable,
        IEntry previousReference, IEntry chosenReference, IEntry otherCandidate)
    {
        var before = SelectVariable(editor, variable);
        var gesture = PerformDoubleClick(editor, before, Forms.MouseButtons.Left, true, selector =>
        {
            Assert.AreSame(previousReference, selector.SelectedEntryItem);
            Assert.IsTrue(selector.UseTreeView, "The node shortcut must use the saved Entry Selector view.");
            Assert.AreEqual(1, ((ComboBox)selector.FindName("EntryViewComboBox")).SelectedIndex);
            Assert.Contains(otherCandidate, selector.FilteredEntriesList);
            Assert.Contains("0 Null", selector.FilteredEntriesList);
            Assert.AreEqual(Visibility.Collapsed, ((ContentControl)selector.FindName("PreviewHost")).Visibility);
            selector.SelectedEntryItem = (object)chosenReference ?? "0 Null";
            Assert.IsTrue(selector.OKCommand.CanExecute(null));
            selector.OKCommand.Execute(null);
        });
        Assert.IsTrue(gesture.Handled);
        Assert.AreNotSame(before, FindVariable(editor, variable), "An edited reference must refresh the graph node.");
        AssertReferenceState(editor, variable, chosenReference);
    }

    private static void AssertReferenceState(SequenceEditorWPF editor, ExportEntry variable, IEntry expectedReference)
    {
        FlushBindings(editor);
        int expectedIndex = expectedReference?.UIndex ?? 0;
        Assert.AreEqual(expectedIndex, variable.GetProperty<ObjectProperty>("ObjValue").Value);
        var node = FindVariable(editor, variable);
        if (expectedReference == null)
        {
            Assert.AreEqual("???", node.Value);
        }
        else
        {
            StringAssert.Contains(node.Value, expectedReference.ObjectName.Instanced);
            StringAssert.Contains(node.Value, $"#{expectedIndex}");
        }
        var interpreter = (InterpreterExportLoader)editor.FindName("Properties_InterpreterWPF");
        Assert.AreSame(variable, interpreter.CurrentLoadedExport);
        var reference = interpreter.PropertyNodes.Single().ChildrenProperties.Select(child => child.Property)
            .OfType<ObjectProperty>().Single(property => property.Name == "ObjValue");
        Assert.AreEqual(expectedIndex, reference.Value, "The selected node's property editor must display the new reference.");
    }

    private static PInputEventArgs PerformDoubleClick(SequenceEditorWPF editor, SVar node, Forms.MouseButtons button,
        bool expectPicker, Action<EntrySelector> interact = null, bool resetDragBounds = true)
    {
        if (resetDragBounds)
        {
            node.PosAtDragStart = node.GlobalFullBounds;
        }
        bool pickerOpened = false;
        Exception failure = null;
        var elapsed = Stopwatch.StartNew();
        var polling = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        polling.Tick += (_, _) =>
        {
            var selector = Application.Current.Windows.OfType<EntrySelector>().FirstOrDefault(window => window.IsLoaded);
            if (selector != null)
            {
                polling.Stop();
                pickerOpened = true;
                try
                {
                    Assert.IsTrue(expectPicker, "This gesture must not open Entry Selector.");
                    selector.ShowActivated = selector.ShowInTaskbar = false;
                    selector.Left = selector.Top = -10000;
                    FlushBindings(editor);
                    interact?.Invoke(selector);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    if (selector.IsVisible)
                    {
                        selector.Close();
                    }
                }
            }
            else if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
            {
                polling.Stop();
                failure = new AssertFailedException("Timed out waiting for the object reference selector.");
                foreach (var pending in Application.Current.Windows.OfType<EntrySelector>().ToArray())
                {
                    pending.Close();
                }
            }
        };
        var args = new PInputEventArgs(null, new Forms.MouseEventArgs(button, 2, 10, 10, 0), PInputType.DoubleClick);
        polling.Start();
        try
        {
            node.OnDoubleClick(args);
        }
        finally
        {
            polling.Stop();
        }
        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        Assert.AreEqual(expectPicker, pickerOpened);
        FlushBindings(editor);
        return args;
    }

    private static SVar SelectVariable(SequenceEditorWPF editor, ExportEntry variable)
    {
        var node = FindVariable(editor, variable);
        ((ListBox)editor.FindName("CurrentObjects_ListBox")).SelectedItem = node;
        FlushBindings(editor);
        return node;
    }

    private static SVar FindVariable(SequenceEditorWPF editor, ExportEntry variable) =>
        (SVar)editor.CurrentObjects.Single(node => ReferenceEquals(node.Export, variable));

    private static void FlushBindings(SequenceEditorWPF editor)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        editor.UpdateLayout();
    }

    private static void InitializeApplicationResources()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(SequenceEditorWPF).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
    }
}
