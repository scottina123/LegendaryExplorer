using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.SharedUI.Controls;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorerCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Xceed.Wpf.Toolkit;
using LoadedTLK = LegendaryExplorer.Tools.TlkManagerNS.TLKManagerWPF.LoadedTLK;

namespace LegendaryExplorer.Tests.Tools.TlkManager;

[TestClass]
public class TLKManagerSearchTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void SharedSearchFiltersAllGamesWithoutChangingLoadSelections()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(TLKManagerWPF).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));

        var window = new TLKManagerWPF
        {
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000
        };
        var collections = new[]
        {
            window.ME1TLKItems, window.ME2TLKItems, window.ME3TLKItems,
            window.LE1TLKItems, window.LE2TLKItems, window.LE3TLKItems
        };
        var names = new[] { "ME1", "ME2", "ME3", "LE1", "LE2", "LE3" };
        try
        {
            foreach (var items in collections)
            {
                items.Clear();
                items.Add(new LoadedTLK("DLC_MOD_Example_INT.tlk", true));
                items.Add(new LoadedTLK("BIOGame_INT.tlk", true));
                items.Add(new LoadedTLK("Startup_INT.pcc", 1, "GlobalTlk_tlk", false));
            }
            ((TabItem)window.FindName("OTTab")).IsSelected = true;
            window.Show();
            FlushDispatcher();
            var search = (SearchBox)window.FindName("TLKSearchBox");
            var textBox = (TextBox)search.FindName("searchBox");
            var lists = names.Select(name => (CheckListBox)window.FindName(name + "TLKList")).ToArray();
            foreach (string name in names)
            {
                typeof(TLKManagerWPF).GetField("bSaveNeeded" + name, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, false);
            }

            textBox.Text = "  dlc_MOD  ";
            FlushDispatcher();
            foreach (var list in lists)
            {
                Assert.AreEqual("DLC_MOD_Example_INT.tlk", list.Items.Cast<LoadedTLK>().Single().tlkDisplayPath);
                Assert.HasCount(2, list.SelectedItems, "Hidden checked TLKs must remain selected.");
            }
            foreach (string name in names)
            {
                Assert.IsFalse((bool)typeof(TLKManagerWPF)
                    .GetField("bSaveNeeded" + name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!,
                    "Searching must not mark load selections as modified.");
            }
            ((TabItem)window.FindName("LETab")).IsSelected = true;
            FlushDispatcher();
            Assert.HasCount(1, lists[3].Items, "The same search must apply when switching tabs.");

            textBox.Text = "globaltlk";
            Assert.IsTrue(lists.All(list => list.Items.Cast<LoadedTLK>().Single().embedded),
                "Embedded export names must be searchable.");
            textBox.Text = "no matching tlk";
            Assert.IsTrue(lists.All(list => list.Items.Count == 0));
            Assert.IsTrue(collections.All(items => items.Count == 3 && items[0].selectedForLoad && items[1].selectedForLoad));

            textBox.Text = "example";
            collections[0].Add(new LoadedTLK("DLC_MOD_Example_FRA.tlk", false));
            collections[0].Add(new LoadedTLK("Unrelated.tlk", false));
            Assert.HasCount(2, lists[0].Items, "New TLKs must respect the active search.");
            lists[0].SelectedItems.Remove(collections[0][0]);
            Assert.IsFalse(collections[0][0].selectedForLoad);
            Assert.IsTrue(collections[0][1].selectedForLoad, "Changing a visible checkbox must not uncheck hidden TLKs.");

            search.Clear();
            Assert.HasCount(5, lists[0].Items);
            Assert.IsTrue(lists.Skip(1).All(list => list.Items.Count == 3));
            textBox.Text = "   ";
            Assert.HasCount(5, lists[0].Items, "Whitespace-only search must show every TLK.");
        }
        finally
        {
            foreach (string name in names)
            {
                typeof(TLKManagerWPF).GetField("bSaveNeeded" + name, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, false);
            }
            window.Close();
        }
    }

    private static void FlushDispatcher() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
