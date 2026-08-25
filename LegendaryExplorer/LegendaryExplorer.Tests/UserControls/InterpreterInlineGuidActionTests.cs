using System;
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
public class InterpreterInlineGuidActionTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void RandomizeButtonSupportsAnyGuidPropertyName()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterInlineGuidTest.pcc", MEGame.LE3);
        ExportEntry export = package.CreateExport("InlineGuidActions", "Object", indexed: false);
        var originalGuid = new Guid("f4ffdf12-e212-2421-1345-4211c23454ad");
        export.WriteProperty(CommonStructs.GuidProp(originalGuid, "MyGuid"));
        export.WriteProperty(new IntProperty(42, "NotAGuid"));

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
            UPropertyTreeViewEntry guidNode = interpreter.PropertyNodes[0].ChildrenProperties
                .Single(node => node.Property?.Name.Name == "MyGuid");
            interpreter.PropertyNodes[0].IsExpanded = true;
            FlushDispatcher();
            window.UpdateLayout();

            Assert.IsTrue(guidNode.IsGuidProperty);
            Button randomizeButton = FindVisualDescendants<Button>(interpreter)
                .Single(button => Equals(button.Content, "Randomize") && ReferenceEquals(button.Tag, guidNode));

            randomizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Guid randomizedGuid = CommonStructs.GetGuid(export.GetProperty<StructProperty>("MyGuid"));
            Assert.AreNotEqual(originalGuid, randomizedGuid);
            Assert.AreEqual(randomizedGuid.ToString(), guidNode.ParsedValue);
        }
        finally
        {
            window.Close();
            interpreter.Dispose();
        }
    }

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
