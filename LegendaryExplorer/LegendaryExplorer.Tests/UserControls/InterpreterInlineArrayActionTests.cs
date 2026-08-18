using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class InterpreterInlineArrayActionTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void InlineMoveButtonsReorderArrayAndReloadTree()
    {
        AssertMove("Up", 1, 2, 1, 3);
        AssertMove("Down", 1, 1, 3, 2);
        AssertMove("Top", 2, 3, 1, 2);
        AssertMove("Bottom", 0, 2, 3, 1);
    }

    [STATestMethod]
    public void ArrayTitleAddButtonAppendsElementAndReloadsTree()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterInlineAddTest.pcc", MEGame.LE3);
        ExportEntry export = package.CreateExport("InlineArrayActions", "SFXSeqVar_Hench", indexed: false);
        export.WriteProperty(new ArrayProperty<NameProperty>("m_aRealPriorities")
        {
            new("hench_marine"),
            new("hench_kaidan"),
            new("hench_ashley")
        });
        var interpreter = new InterpreterExportLoader { ReloadPropertyDataAfterWrite = false };
        var window = new Window
        {
            Content = interpreter,
            Width = 1000,
            Height = 700,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };

        try
        {
            window.Show();
            interpreter.LoadExport(export);
            UPropertyTreeViewEntry arrayNode = FindArrayNode(interpreter, "m_aRealPriorities");
            interpreter.PropertyNodes[0].IsExpanded = true;
            arrayNode.IsExpanded = true;
            FlushDispatcher();
            window.UpdateLayout();

            Button addButton = FindVisualDescendants<Button>(interpreter)
                .Single(button => Equals(button.Content, "+") && ReferenceEquals(button.Tag, arrayNode));

            addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.AreEqual(4, export.GetProperty<ArrayProperty<NameProperty>>("m_aRealPriorities").Count);
            Assert.AreEqual(4, FindArrayNode(interpreter, "m_aRealPriorities").ChildrenProperties.Count);
        }
        finally
        {
            window.Close();
            interpreter.Dispose();
        }
    }

    private static ExportEntry CreateExportWithValues(IMEPackage package)
    {
        ExportEntry export = package.CreateExport("InlineArrayActions", "Object", indexed: false);
        export.WriteProperty(new ArrayProperty<IntProperty>("Values")
        {
            new(1),
            new(2),
            new(3)
        });
        return export;
    }

    private static void AssertMove(string direction, int sourceIndex, params int[] expectedValues)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage($"InterpreterInlineMove{direction}Test.pcc", MEGame.LE3);
        ExportEntry export = CreateExportWithValues(package);
        var interpreter = new InterpreterExportLoader { ReloadPropertyDataAfterWrite = false };
        var window = new Window
        {
            Content = interpreter,
            Width = 1000,
            Height = 700,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };

        try
        {
            window.Show();
            interpreter.LoadExport(export);
            UPropertyTreeViewEntry arrayNode = FindValuesArrayNode(interpreter);
            UPropertyTreeViewEntry sourceNode = arrayNode.ChildrenProperties[sourceIndex];
            interpreter.PropertyNodes[0].IsExpanded = true;
            arrayNode.IsExpanded = true;
            FlushDispatcher();
            window.UpdateLayout();

            Button moveButton = FindVisualDescendants<Button>(interpreter)
                .Single(button => Equals(button.CommandParameter, direction) && ReferenceEquals(button.Tag, sourceNode));

            Assert.IsTrue(moveButton.IsEnabled);
            moveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            CollectionAssert.AreEqual(
                expectedValues,
                export.GetProperty<ArrayProperty<IntProperty>>("Values").Select(prop => prop.Value).ToArray());
            CollectionAssert.AreEqual(
                expectedValues,
                FindValuesArrayNode(interpreter).ChildrenProperties.Select(node => ((IntProperty)node.Property).Value).ToArray());
        }
        finally
        {
            window.Close();
            interpreter.Dispose();
        }
    }

    private static UPropertyTreeViewEntry FindValuesArrayNode(InterpreterExportLoader interpreter) =>
        FindArrayNode(interpreter, "Values");

    private static UPropertyTreeViewEntry FindArrayNode(InterpreterExportLoader interpreter, string propertyName) =>
        interpreter.PropertyNodes[0].ChildrenProperties.Single(node => node.Property?.Name.Name == propertyName);

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void FlushDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
