using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.PackageEditorControls;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.TLK.ME2ME3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Dialogs;

[TestClass]
public class UpstreamMergeCompatibilityTests
{
    [STATestMethod]
    public void FilteringKeepsReferenceActionsAndUpdatesWithTheIssueList()
    {
        InitializeApplicationResources();
        var dialog = new ListDialog(new[] { new EntryStringPair("Missing texture"),
            new EntryStringPair("Invalid mesh") }, "Issues", "Package issues", null);
        try
        {
            int calls = 0;
            dialog.SecondaryActionText = "Repair references";
            dialog.SecondaryActionHandler = () => calls++;
            dialog.FilterText = "TEXTURE";
            Assert.HasCount(1, dialog.ItemsView.Cast<object>());
            dialog.Items.Add(new EntryStringPair("Missing texture parameter"));
            Assert.HasCount(2, dialog.ItemsView.Cast<object>());
            Assert.AreEqual(Visibility.Visible, dialog.SecondaryActionVisibility);
            dialog.SecondaryActionHandler();
            Assert.AreEqual(1, calls);
            dialog.FilterText = "";
            Assert.HasCount(3, dialog.ItemsView.Cast<object>());
        }
        finally
        {
            dialog.Close();
        }
    }

    [STATestMethod]
    public void ShortcutTargetsStillExistAfterExperimentsMoveIntoTheBrowser()
    {
        InitializeApplicationResources();
        var control = new ExperimentsMenuControl();
        Assert.HasCount(0, control.Items, "The fork's experiments browser owns the menu entries.");
        var targets = (System.Collections.ICollection)typeof(ExperimentsMenuControl)
            .GetMethod("GetExperimentShortcutTargets", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(control, null)!;
        Assert.IsTrue(targets.Count > 50, "Upstream shortcuts must search the retained browser catalog.");
    }

    [STATestMethod]
    public void LanguageSynchronizationFollowsTheActiveFileBackedTab()
    {
        InitializeApplicationResources();
        using var editor = new TLKEditorExportLoader();
        var tab = new TLKEditorExportLoader.TLKEditorTab("Merge_INT.tlk",
            new ME2ME3TalkFile { StringRefs = [new TLKStringRef(100, "Line")] });
        editor.OpenTabs.Add(tab);
        editor.ActiveTab = tab;
        Assert.IsTrue(editor.IsFileBackedTlkLoaded);
        Assert.IsTrue(editor.SynchronizeToOtherLanguagesCommand.CanExecute(null));
        editor.ActiveTab = null;
        Assert.IsFalse(editor.IsFileBackedTlkLoaded);
        Assert.IsFalse(editor.SynchronizeToOtherLanguagesCommand.CanExecute(null));
    }

    private static void InitializeApplicationResources()
    {
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
    }
}
