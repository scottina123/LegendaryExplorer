using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.SharedUI.Controls;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorer.UserControls.ExportLoaderControls.ScriptEditor.IDE;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.TLK.ME1;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.PackageEditor;

[TestClass]
public class NameTlkLookupTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow("VO_692097_f_Play")]
    [DataRow("vo_692097_m_play")]
    [DataRow("Stop_VO_692097_m")]
    [DataRow("audio:VO_692097")]
    [DataRow("audio:VO_692097_M")]
    [DataRow("citprs_miranda_talk1_m_00692097_m_wav")]
    [DataRow("conversation_692097_F")]
    [DataRow("692097_m")]
    public void ResolvesDialogueNamesAndPreservesNameAndIndex(string name)
    {
        var lookup = new NameTlkLookup(id =>
        {
            Assert.AreEqual(692097, id);
            return "\"I should go.\"";
        });

        var result = lookup.CreateName(42, name);

        Assert.AreEqual(name, result.Name);
        Assert.AreEqual(42, result.Index);
        Assert.AreEqual("I should go.", result.TlkText);
        Assert.IsTrue(NameTlkLookup.MatchesSearch(result, "SHOULD GO"));
        Assert.IsTrue(NameTlkLookup.MatchesSearch(result, name.ToUpperInvariant()));
        Assert.IsFalse(NameTlkLookup.MatchesSearch(result, "unrelated dialogue"));
    }

    [TestMethod]
    [DataRow("None")]
    [DataRow("Texture_692097")]
    [DataRow("Actor_1")]
    [DataRow("VO_notanumber_m_Play")]
    [DataRow("VO_692097extra_f_Play")]
    [DataRow("VO_99999999999999999999_f_Play")]
    [DataRow("VO_0_f_Play")]
    [DataRow("VO_-692097_f_Play")]
    [DataRow("MyVO_692097_Play")]
    public void DoesNotLookUpUnrelatedOrInvalidNames(string name)
    {
        var lookup = new NameTlkLookup(_ =>
        {
            Assert.Fail("An unrelated name must not trigger a TLK lookup.");
            return null;
        });

        var result = lookup.CreateName(0, name);

        Assert.IsNull(result.TlkText);
        Assert.IsTrue(NameTlkLookup.MatchesSearch(result, name));
        Assert.IsFalse(NameTlkLookup.MatchesSearch(result, "No Data"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("No Data")]
    [DataRow("no data")]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("\"\"")]
    [DataRow("\"   \"")]
    public void MissingOrEmptyDialogueHasNoSubtitle(string text)
    {
        var lookup = new NameTlkLookup(_ => text);
        var result = lookup.CreateName(0, "VO_692097_f_Play");

        Assert.IsNull(result.TlkText);
        Assert.IsFalse(NameTlkLookup.MatchesSearch(result, "No Data"));
    }

    [TestMethod]
    public void CachesSharedIdsIncludingMissesOnlyWithinOneRefresh()
    {
        int lookupCount = 0;
        var lookup = new NameTlkLookup(_ => { lookupCount++; return "No Data"; });
        Assert.IsNull(lookup.CreateName(0, "VO_692097_f_Play").TlkText);
        Assert.IsNull(lookup.CreateName(1, "VO_692097_m_Play").TlkText);
        Assert.AreEqual(1, lookupCount);

        var refreshedLookup = new NameTlkLookup(_ => "\"Newly loaded dialogue.\"");
        Assert.AreEqual("Newly loaded dialogue.", refreshedLookup.CreateName(0, "VO_692097_f_Play").TlkText);
    }

    [TestMethod]
    public void KeepsLineBreaksAndDialogueQuotesSearchable()
    {
        var lookup = new NameTlkLookup(_ => "\"She said \"hello\".\nThen she left.\"");
        var name = lookup.CreateName(0, "VO_692097_f_Play");

        Assert.AreEqual("She said \"hello\".\nThen she left.", name.TlkText);
        Assert.IsTrue(NameTlkLookup.MatchesSearch(name, "THEN SHE LEFT"));
        Assert.IsTrue(NameTlkLookup.MatchesSearch(name, "\"hello\""));
    }

    [TestMethod]
    public void UnsupportedGameHasNoSubtitle()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("NameTlkTest.udk", MEGame.UDK);

        Assert.IsNull(new NameTlkLookup(package).CreateName(0, "VO_692097_f_Play").TlkText);
    }

    [TestMethod]
    [DataRow(MEGame.ME1)]
    [DataRow(MEGame.LE1)]
    public void ResolvesPackageLocalDialogueWithoutLeakingBetweenPackages(MEGame game)
    {
        using var firstPackage = MEPackageHandler.CreateMemoryEmptyPackage("FirstNameTlkTest.pcc", game);
        using var secondPackage = MEPackageHandler.CreateMemoryEmptyPackage("SecondNameTlkTest.pcc", game);
        AddLocalTlk(firstPackage, "First package dialogue.");
        AddLocalTlk(secondPackage, "Second package dialogue.");

        var firstName = new NameTlkLookup(firstPackage).CreateName(0, "audio:VO_692097_M");
        var secondName = new NameTlkLookup(secondPackage).CreateName(0, "audio:VO_692097_M");

        Assert.AreEqual("First package dialogue.", firstName.TlkText);
        Assert.AreEqual("Second package dialogue.", secondName.TlkText);
        Assert.IsFalse(NameTlkLookup.MatchesSearch(secondName, "First package"));

        // Find References displays the dialogue without including it in the navigation path.
        var entry = firstPackage.CreateExport("audio:VO_692097_M", "Object", indexed: false);
        var result = new NameUsageResult(entry, "Property: Dialogue[0].Event", firstName.TlkText);
        Assert.AreEqual($"#{entry.UIndex} {entry.ObjectName.Instanced}: Property: Dialogue[0].Event\nFirst package dialogue.", result.Message);
        Assert.AreSame(entry, result.Entry);
        Assert.AreEqual(result.Message, result.ToString()); // Copy items includes the dialogue.
        Assert.AreEqual("Property: Dialogue[0].Event",
            typeof(PackageEditorWindow).GetMethod("GetUsageDetail", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [result]));

        var unresolvedResult = new NameUsageResult(entry, "Header: Object Name", null);
        Assert.AreEqual($"#{entry.UIndex} {entry.ObjectName.Instanced}: Header: Object Name", unresolvedResult.Message);
    }

    private static void AddLocalTlk(IMEPackage package, string text)
    {
        var export = package.CreateExport("tlk", "BioTlkFile", indexed: false);
        var compressor = new HuffmanCompression();
        compressor.LoadInputData([new TLKStringRef(692097, text)]);
        compressor.SerializeTalkfileToExport(export);
        package.LocalTalkFiles.Clear();
        package.LocalTalkFiles.Add(new ME1TalkFile(export));
    }

    [STATestMethod]
    public void NameBoxSearchesDialogueInBothDirectionsAndRefreshesAfterChanges()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(PackageEditorWindow).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
        SyntaxInfo.LoadFromSettings();

        using var package = MEPackageHandler.CreateMemoryEmptyPackage("NameSearchTest.pcc", MEGame.ME1);
        AddLocalTlk(package, "I should go.");
        int femaleIndex = package.FindNameOrAdd("VO_692097_f_Play");
        int maleIndex = package.FindNameOrAdd("VO_692097_m_Play");
        var window = new PackageEditorWindow(submitTelemetry: false);
        try
        {
            typeof(WPFBase).GetMethod("RegisterPackage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [package]);
            RefreshNames(null);
            // The hidden test window has no loaded native preview controls to clear when switching tabs.
            typeof(PackageEditorWindow).GetField("_currentView", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, PackageEditorWindow.CurrentViewMode.Names);
            typeof(PackageEditorWindow).GetMethod("RefreshView", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);
            var list = (ListBox)window.FindName("LeftSide_ListView");
            var searchBox = (WatermarkTextBox)window.FindName("Search_TextBox");
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.AreEqual(package.NameCount, list.Items.Count);
            searchBox.Text = "  SHOULD GO  ";
            list.SelectedIndex = -1;

            Search(reverse: false);
            Assert.AreEqual(femaleIndex, list.SelectedIndex);
            Search(reverse: false);
            Assert.AreEqual(maleIndex, list.SelectedIndex);
            Search(reverse: false);
            Assert.AreEqual(femaleIndex, list.SelectedIndex);
            Search(reverse: true);
            Assert.AreEqual(maleIndex, list.SelectedIndex);

            package.replaceName(maleIndex, "OrdinaryName");
            RefreshNames([new PackageUpdate(PackageChange.NameEdit, maleIndex)]);
            Assert.IsNull(window.NamesList[maleIndex].TlkText);
            searchBox.Text = "ordinaryname";
            Search(reverse: false);
            Assert.AreEqual(maleIndex, list.SelectedIndex);

            int addedIndex = package.FindNameOrAdd("conversation_692097_M");
            RefreshNames([new PackageUpdate(PackageChange.NameAdd, addedIndex)]);
            Assert.AreEqual("I should go.", window.NamesList[addedIndex].TlkText);

            list.SelectedIndex = femaleIndex;
            package.LocalTalkFiles[0].ReplaceString(692097, "Updated dialogue.");
            TLKManagerWPF.ME1LastReloaded = Guid.NewGuid().ToString();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.AreEqual(femaleIndex, list.SelectedIndex);
            Assert.AreEqual("Updated dialogue.", window.NamesList[femaleIndex].TlkText);
            searchBox.Text = "UPDATED DIALOGUE";
            Search(reverse: false);
            Assert.AreEqual(addedIndex, list.SelectedIndex);
        }
        finally
        {
            typeof(WPFBase).GetMethod("UnLoadMEPackage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);
            window.Close();
        }

        void RefreshNames(List<PackageUpdate> updates) =>
            typeof(PackageEditorWindow).GetMethod("RefreshNames", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [updates]);

        void Search(bool reverse) =>
            ((Task)typeof(PackageEditorWindow).GetMethod("SearchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [reverse])!).GetAwaiter().GetResult();
    }
}
