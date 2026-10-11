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
        InitializeApplicationResources();
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

    [STATestMethod]
    public void QueuedSelectionRestoresRespectExportSwitchesAndInterpreterDisposal()
    {
        InitializeApplicationResources();
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterSelectionLifetimeTest.pcc", MEGame.LE3);
        ExportEntry first = package.CreateExport("FirstSelection", "Object", indexed: false);
        first.WriteProperty(new NameProperty("Before", "SelectedName"));
        ExportEntry second = package.CreateExport("SecondSelection", "Object", indexed: false);
        second.WriteProperty(new NameProperty("After", "SelectedName"));
        byte[] firstData = first.Data.ToArray();
        byte[] secondData = second.Data.ToArray();
        var interpreter = new InterpreterExportLoader();
        var window = new Window
        {
            Content = interpreter,
            Width = 1000,
            Height = 700,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -10000,
            Top = -10000
        };
        bool disposed = false;
        try
        {
            window.Show();
            interpreter.LoadExport(first);
            SelectName(interpreter);
            FlushDispatcher();

            // A reload queues restoration after layout; switching exports must invalidate it.
            interpreter.LoadExport(first);
            interpreter.LoadExport(second);
            UPropertyTreeViewEntry selected = SelectName(interpreter);
            FlushDispatcher();
            Assert.AreSame(second, interpreter.CurrentLoadedExport);
            Assert.AreSame(selected, interpreter.TreeSelectedItem);

            interpreter.LoadExport(second);
            interpreter.Dispose();
            disposed = true;
            window.Close();
            FlushDispatcher();
            Assert.IsNull(interpreter.CurrentLoadedExport);
            CollectionAssert.AreEqual(firstData, first.Data);
            CollectionAssert.AreEqual(secondData, second.Data);
        }
        finally
        {
            window.Close();
            if (!disposed)
                interpreter.Dispose();
        }

        static UPropertyTreeViewEntry SelectName(InterpreterExportLoader interpreter)
        {
            var node = interpreter.PropertyNodes[0].ChildrenProperties.Single(item => item.Property is NameProperty);
            interpreter.TreeSelectedItem = node;
            interpreter.SelectedItem = node;
            return node;
        }
    }

    private static void InitializeApplicationResources()
    {
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
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
