using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.Dialogs;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Dialogs;

[TestClass]
public class EntrySelectorAudioPreviewTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void AudioPreviewFollowsSelectionAndPreservesSelectorLayout()
    {
        InitializeApplicationResources();
        MixedEntryListStartsWithoutAudioPreviewForNonAudioSelection();
        EmptyAndNonAudioListsRemainCompact(emptyList: false);
        EmptyAndNonAudioListsRemainCompact(emptyList: true);
        foreach (string audioClass in new[] { "WwiseEvent", "WwiseStream" })
        {
            AudioOnlyListStartsWithAudioPreviewVisible(audioClass);
            AudioPreviewTracksSelectionAndPreservesUserResizedWindow(audioClass);
        }
    }

    private static void MixedEntryListStartsWithoutAudioPreviewForNonAudioSelection()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MixedAudioEntries.pcc", MEGame.LE3);
        var nonAudio = package.CreateExport("NonAudioObject", "SeqVar_Object", indexed: false);
        var stream = package.CreateExport("TestAudioStream", "WwiseStream", indexed: false);
        var audioEvent = package.CreateExport("TestAudioEvent", "WwiseEvent", indexed: false);
        using var selector = CreateSelector([nonAudio, stream, audioEvent, "0 Null"]);
        try
        {
            Assert.AreSame(nonAudio, selector.SelectedEntryItem);
            AssertPreviewVisibility(selector, false);
            Assert.AreEqual(780d, selector.Width,
                "Audio elsewhere in a generic object list must not enlarge the selector.");
            Assert.AreEqual(520d, selector.Height);
        }
        finally
        {
            selector.Close();
        }
    }

    private static void EmptyAndNonAudioListsRemainCompact(bool emptyList)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("NonAudioEntries.pcc", MEGame.LE3);
        var nonAudio = package.CreateExport("NonAudioObject", "SeqVar_Object", indexed: false);
        using var selector = CreateSelector(emptyList ? [] : [nonAudio]);
        try
        {
            AssertPreviewVisibility(selector, false);
            Assert.IsNull(((ContentControl)selector.FindName("PreviewHost")).Content);
            Assert.AreEqual(780d, selector.Width);
            Assert.AreEqual(520d, selector.Height);
        }
        finally
        {
            selector.Close();
        }
    }

    private static void AudioOnlyListStartsWithAudioPreviewVisible(string audioClass)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("AudioOnlyEntries.pcc", MEGame.LE3);
        var audio = package.CreateExport("TestAudio", audioClass, indexed: false);
        using var selector = CreateSelector([audio]);
        try
        {
            Assert.AreSame(audio, selector.SelectedEntryItem);
            AssertPreviewVisibility(selector, true);
            Assert.IsGreaterThanOrEqualTo(1000d, selector.Width);
        }
        finally
        {
            selector.Close();
        }
    }

    private static void AudioPreviewTracksSelectionAndPreservesUserResizedWindow(string audioClass)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("AudioSelection.pcc", MEGame.LE3);
        var nonAudio = package.CreateExport("NonAudioObject", "SeqVar_Object", indexed: false);
        // These synthetic exports contain no playable media; selection still exercises the real preview UI.
        var audio = package.CreateExport("TestAudio", audioClass, indexed: false);
        const string nullEntry = "0 Null";
        using var selector = CreateSelector([nonAudio, audio, nullEntry]);
        try
        {
            selector.SelectedEntryItem = audio;
            AssertPreviewVisibility(selector, true);
            selector.Width = 1370;
            selector.Height = 740;
            var previewColumn = (ColumnDefinition)selector.FindName("PreviewColumn");
            previewColumn.Width = new GridLength(271);

            foreach (object selection in new object[] { nonAudio, null, nullEntry })
            {
                selector.SelectedEntryItem = selection;
                AssertPreviewVisibility(selector, false);
                Assert.AreEqual(1370d, selector.Width, 1d);
                Assert.AreEqual(740d, selector.Height, 1d);

                selector.SelectedEntryItem = audio;
                AssertPreviewVisibility(selector, true);
                Assert.AreEqual(1370d, selector.Width, 1d,
                    "Returning to audio must preserve the user's window size.");
                Assert.AreEqual(740d, selector.Height, 1d);
                Assert.AreEqual(new GridLength(271), previewColumn.Width,
                    "Returning to audio must preserve the user's splitter position.");
            }
        }
        finally
        {
            selector.Close();
        }
    }

    private static EntrySelector CreateSelector(IEnumerable<object> items)
    {
        var constructor = typeof(EntrySelector).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, [typeof(Window), typeof(IEnumerable<object>), typeof(string), typeof(string)], modifiers: null);
        Assert.IsNotNull(constructor);
        return (EntrySelector)constructor.Invoke([null, items, null, null]);
    }

    private static void AssertPreviewVisibility(EntrySelector selector, bool visible)
    {
        var host = (ContentControl)selector.FindName("PreviewHost");
        var splitter = (GridSplitter)selector.FindName("PreviewSplitter");
        var previewColumn = (ColumnDefinition)selector.FindName("PreviewColumn");
        var splitterColumn = (ColumnDefinition)selector.FindName("PreviewSplitterColumn");
        Assert.AreEqual(visible ? Visibility.Visible : Visibility.Collapsed, host.Visibility);
        Assert.AreEqual(visible ? Visibility.Visible : Visibility.Collapsed, splitter.Visibility);
        if (visible)
        {
            Assert.IsGreaterThan(0d, previewColumn.Width.Value);
            Assert.AreEqual(5d, splitterColumn.Width.Value);
        }
        else
        {
            Assert.AreEqual(new GridLength(0), previewColumn.Width);
            Assert.AreEqual(new GridLength(0), splitterColumn.Width);
        }
    }

    private static void InitializeApplicationResources()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(EntrySelector).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
    }
}
