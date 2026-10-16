using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.TLK.ME2ME3;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class InterpreterVoTrackNavigationTests
{
    private const int StringRef = 37252219;

    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [DataTestMethod]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void InlineAudioButtonsNavigateTheirOwnCommittedStringRefWithoutUsingSelection(MEGame game) => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterVoTrackNavigation.pcc", game);
        ExportEntry track = CreateTrack(package);
        ExportEntry[] targets = CreateAudio(package);
        byte[][] originalData = package.Exports.Select(export => export.Data.ToArray()).ToArray();
        var navigated = new List<IEntry>();
        using var view = new InterpreterTestView(track, entry => navigated.Add((IEntry)entry));
        UPropertyTreeViewEntry node = view.Node("m_nStrRefID");
        UPropertyTreeViewEntry unrelated = view.Node("UnrelatedSelection");
        view.Interpreter.TreeSelectedItem = unrelated;
        view.Interpreter.SelectedItem = unrelated;
        view.RefreshLayout();
        Assert.IsTrue(node.ShowVoTrackAudioNavigation);

        TextBox valueEditor = FindVisualDescendants<TextBox>(view.Interpreter)
            .Single(textBox => ReferenceEquals(textBox.Tag, node) && textBox.MinWidth == 80);
        foreach (string stagedValue in new[] { "12345678", "invalid" })
        {
            valueEditor.Text = stagedValue;
            view.RefreshLayout();
            foreach ((RelayCommand command, string label, _, _) in Actions(view.Interpreter))
            {
                Button button = view.AudioButtons(node, command).Single();
                Assert.AreEqual(label, button.Content);
                Assert.IsTrue(button.IsEnabled, label);
                Assert.AreSame(node, button.CommandParameter);
                InvokeButton(button);
            }
            Assert.AreEqual(StringRef, ((IntProperty)node.Property).Value);
            Assert.AreEqual(StringRef, track.GetProperty<IntProperty>("m_nStrRefID").Value);
        }

        CollectionAssert.AreEqual(targets.Cast<IEntry>().Concat(targets).ToArray(), navigated);
        Assert.AreEqual(originalData.Length, package.Exports.Count);
        for (int i = 0; i < originalData.Length; i++)
        {
            CollectionAssert.AreEqual(originalData[i], package.Exports[i].Data);
        }
    });

    [TestMethod]
    public void SelectedStringRefAlsoExposesAudioButtonsInThePropertyEditor() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterSelectedVoTrack.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        ExportEntry[] targets = CreateAudio(package);
        var navigated = new List<IEntry>();
        using var view = new InterpreterTestView(track, entry => navigated.Add((IEntry)entry));
        UPropertyTreeViewEntry node = view.Node("m_nStrRefID");
        view.Interpreter.TreeSelectedItem = node;
        view.Interpreter.SelectedItem = node;
        view.RefreshLayout();

        foreach ((RelayCommand command, string label, _, _) in Actions(view.Interpreter))
        {
            Button[] buttons = view.AudioButtons(node, command);
            Assert.AreEqual(2, buttons.Length, $"{label} must appear in the row and selected-property editor.");
            foreach (Button button in buttons)
            {
                Assert.IsTrue(button.IsEnabled, label);
                Assert.AreEqual(label, button.Content);
                InvokeButton(button);
            }
        }
        CollectionAssert.AreEqual(targets.SelectMany(target => new IEntry[] { target, target }).ToArray(), navigated);
    });

    [TestMethod]
    public void MissingGenderTargetsDisableOnlyTheirButtons() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterMissingVoTrackAudio.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        ExportEntry stream = package.CreateExport($"scene_{StringRef}_m_wav", "WwiseStream", indexed: false);
        CreateEvent(package, $"VO_{StringRef}_m_Play", stream);
        int callbacks = 0;
        using var view = new InterpreterTestView(track, _ => callbacks++, _ => callbacks++);
        UPropertyTreeViewEntry node = view.Node("m_nStrRefID");
        view.Interpreter.TreeSelectedItem = view.Node("UnrelatedSelection");
        view.RefreshLayout();

        foreach ((RelayCommand command, string label, bool isFemale, bool isStream) in Actions(view.Interpreter))
        {
            Assert.AreEqual(!isFemale, command.CanExecute(node), label);
            Assert.AreEqual(!isFemale, view.AudioButtons(node, command).Single().IsEnabled, label);
            if (isFemale)
            {
                command.Execute(node);
                Navigate(view.Interpreter, node, isFemale, isStream, true);
            }
        }
        Assert.AreEqual(0, callbacks);
    });

    [DataTestMethod]
    [DataRow(MEGame.ME1, "BioEvtSysTrackVOElements", "m_nStrRefID")]
    [DataRow(MEGame.LE1, "BioEvtSysTrackVOElements", "m_nStrRefID")]
    [DataRow(MEGame.LE3, "Object", "m_nStrRefID")]
    [DataRow(MEGame.LE3, "BioEvtSysTrackVOElements", "OtherStringRef")]
    public void OtherPropertiesAndUnsupportedTracksDoNotExposeAudioButtons(MEGame game, string className, string propertyName) => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterNonVoTrack.pcc", game);
        ExportEntry source = package.CreateExport("Source", className, indexed: false);
        source.WriteProperty(new IntProperty(StringRef, propertyName));
        using var view = new InterpreterTestView(source, _ => Assert.Fail("Unexpected navigation."));
        UPropertyTreeViewEntry node = view.Node(propertyName);
        view.Interpreter.TreeSelectedItem = node;
        view.Interpreter.SelectedItem = node;
        view.RefreshLayout();

        Assert.IsFalse(node.ShowVoTrackAudioNavigation);
        Assert.AreEqual(0, view.PlayButtons(node).Length);
        Assert.IsFalse(view.Interpreter.PlayMaleVoAudioCommand.CanExecute(node));
        Assert.IsFalse(view.Interpreter.PlayFemaleVoAudioCommand.CanExecute(node));
        foreach ((RelayCommand command, _, bool isFemale, bool isStream) in Actions(view.Interpreter))
        {
            Assert.IsFalse(command.CanExecute(node));
            Assert.AreEqual(0, view.AudioButtons(node, command).Length);
            command.Execute(node);
            Navigate(view.Interpreter, node, isFemale, isStream, true);
        }
    });

    [TestMethod]
    public void AudioNavigationRejectsNodesFromPreviouslyLoadedTracks() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterStaleVoTrack.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        CreateAudio(package);
        ExportEntry otherTrack = package.CreateExport("OtherTrack", "BioEvtSysTrackVOElements", indexed: false);
        otherTrack.WriteProperty(new IntProperty(StringRef, "m_nStrRefID"));
        int callbacks = 0;
        using var view = new InterpreterTestView(track, _ => callbacks++, _ => callbacks++);
        UPropertyTreeViewEntry stale = view.Node("m_nStrRefID");
        Assert.IsTrue(view.Interpreter.GoToMaleVoEventCommand.CanExecute(stale));
        view.Interpreter.LoadExport(otherTrack);
        view.RefreshLayout();

        foreach ((RelayCommand command, _, bool isFemale, bool isStream) in Actions(view.Interpreter))
        {
            Assert.IsFalse(command.CanExecute(stale));
            command.Execute(stale);
            Navigate(view.Interpreter, stale, isFemale, isStream, true);
        }
        Assert.AreEqual(0, callbacks);
    });

    [TestMethod]
    public void AudioNavigationUsesNewWindowCallbacksForShiftAndStandaloneViews() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterVoTrackCallbacks.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        ExportEntry[] targets = CreateAudio(package);
        var currentEditor = new List<IEntry>();
        var newWindows = new List<IEntry>();
        using var view = new InterpreterTestView(track,
            entry => currentEditor.Add((IEntry)entry), entry => newWindows.Add((IEntry)entry));
        UPropertyTreeViewEntry node = view.Node("m_nStrRefID");

        foreach ((_, _, bool isFemale, bool isStream) in Actions(view.Interpreter))
        {
            Navigate(view.Interpreter, node, isFemale, isStream, false);
            Navigate(view.Interpreter, node, isFemale, isStream, true);
        }
        CollectionAssert.AreEqual(targets.Cast<IEntry>().ToArray(), currentEditor);
        CollectionAssert.AreEqual(targets.Cast<IEntry>().ToArray(), newWindows);

        view.Interpreter.OpenEntryInNewWindowCommand = new RelayCommand(_ => Assert.Fail("Disabled callback executed."), _ => false);
        foreach ((_, _, bool isFemale, bool isStream) in Actions(view.Interpreter))
        {
            Navigate(view.Interpreter, node, isFemale, isStream, true);
        }
        CollectionAssert.AreEqual(targets.Cast<IEntry>().ToArray(), currentEditor);
        view.Interpreter.NavigateToEntryCommand = null;
        foreach ((RelayCommand command, _, _, _) in Actions(view.Interpreter))
        {
            Assert.IsFalse(command.CanExecute(node));
            command.Execute(node);
        }

        view.Interpreter.OpenEntryInNewWindowCommand = new RelayCommand(entry => newWindows.Add((IEntry)entry));
        view.Interpreter.TreeSelectedItem = view.Node("UnrelatedSelection");
        view.RefreshLayout();
        foreach ((RelayCommand command, _, _, _) in Actions(view.Interpreter))
        {
            InvokeButton(view.AudioButtons(node, command).Single());
        }
        CollectionAssert.AreEqual(targets.Cast<IEntry>().Concat(targets).ToArray(), newWindows);
    });

    [TestMethod]
    public void PlayAudioMenuUsesTheClickedRowsCommittedStringRefAndGenderCommands() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterVoTrackPlayback.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        ExportEntry[] audio = CreateAudio(package);
        byte[] originalData = track.Data.ToArray();
        InitializeApplicationResources();
        var previewedAudio = new List<IEntry>();
        var interpreter = new InterpreterExportLoader
        {
            VoAudioPreviewCommand = new RelayCommand(audio => previewedAudio.Add((IEntry)audio))
        };
        using var view = new InterpreterTestView(track, _ => Assert.Fail("Playback must not navigate."), interpreter: interpreter);
        UPropertyTreeViewEntry node = view.Node("m_nStrRefID");
        view.Interpreter.TreeSelectedItem = view.Node("UnrelatedSelection");
        view.Interpreter.SelectedItem = view.Node("UnrelatedSelection");
        view.RefreshLayout();
        TextBox valueEditor = FindVisualDescendants<TextBox>(interpreter)
            .Single(textBox => ReferenceEquals(textBox.Tag, node) && textBox.MinWidth == 80);

        foreach (string stagedValue in new[] { "12345678", "invalid" })
        {
            valueEditor.Text = stagedValue;
            view.RefreshLayout();
            InvokePlayMenu(view, node);
            Assert.AreEqual(StringRef, ((IntProperty)node.Property).Value);
            Assert.AreEqual(StringRef, track.GetProperty<IntProperty>("m_nStrRefID").Value);
            CollectionAssert.AreEqual(originalData, track.Data);
        }

        CollectionAssert.AreEqual(new IEntry[] { audio[2], audio[3], audio[2], audio[3] }, previewedAudio);
    });

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SingleOrSharedVoicePlaysImmediatelyAndMissingVoicesDisablePlayback(bool sharedVoice) => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterSingleVoTrackPlayback.pcc", MEGame.LE3);
        ExportEntry track = CreateTrack(package);
        ExportEntry stream = package.CreateExport($"scene_{StringRef}_m_wav", "WwiseStream", indexed: false);
        CreateEvent(package, $"VO_{StringRef}_m_Play", stream);
        if (sharedVoice)
        {
            CreateEvent(package, $"VO_{StringRef}_f_Play", stream);
        }
        ExportEntry missingTrack = package.CreateExport("MissingTrack", "BioEvtSysTrackVOElements", indexed: false);
        missingTrack.WriteProperty(new IntProperty(99999999, "m_nStrRefID"));
        InitializeApplicationResources();
        var previewedAudio = new List<IEntry>();
        var interpreter = new InterpreterExportLoader
        {
            VoAudioPreviewCommand = new RelayCommand(audio => previewedAudio.Add((IEntry)audio))
        };
        using var view = new InterpreterTestView(track, _ => Assert.Fail("Playback must not navigate."), interpreter: interpreter);
        UPropertyTreeViewEntry node = view.Node("m_nStrRefID");
        view.Interpreter.TreeSelectedItem = view.Node("UnrelatedSelection");
        view.RefreshLayout();
        Button play = view.PlayButtons(node).Single();

        Assert.AreEqual("Play audio", play.Content);
        Assert.IsTrue(play.IsEnabled);
        Assert.IsTrue(interpreter.PlayMaleVoAudioCommand.CanExecute(node));
        Assert.AreEqual(sharedVoice, interpreter.PlayFemaleVoAudioCommand.CanExecute(node));
        InvokeButton(play);
        CollectionAssert.AreEqual(new IEntry[] { stream }, previewedAudio);
        Assert.IsTrue(play.ContextMenu is null || !play.ContextMenu.IsOpen);

        interpreter.LoadExport(missingTrack);
        view.ExpandAll();
        UPropertyTreeViewEntry missing = view.Node("m_nStrRefID");
        interpreter.TreeSelectedItem = null;
        interpreter.SelectedItem = null;
        view.RefreshLayout();
        Button disabledPlay = view.PlayButtons(missing).Single();
        Assert.IsFalse(disabledPlay.IsEnabled);
        Assert.IsFalse(interpreter.PlayMaleVoAudioCommand.CanExecute(missing));
        Assert.IsFalse(interpreter.PlayFemaleVoAudioCommand.CanExecute(missing));
        interpreter.PlayVoAudioCommand.Execute(disabledPlay);
        interpreter.PlayMaleVoAudioCommand.Execute(missing);
        interpreter.PlayFemaleVoAudioCommand.Execute(missing);
        // Reusing a row from a previously loaded export must not play its old voice.
        Assert.IsFalse(interpreter.PlayMaleVoAudioCommand.CanExecute(node));
        Assert.IsFalse(interpreter.PlayFemaleVoAudioCommand.CanExecute(node));
        interpreter.PlayMaleVoAudioCommand.Execute(node);
        interpreter.PlayFemaleVoAudioCommand.Execute(node);
        CollectionAssert.AreEqual(new IEntry[] { stream }, previewedAudio);
    });

    [TestMethod]
    public void FaceOnlyVoLinesExposeNavigationAndPlaybackForEachRowsOwnStringRef() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterFaceOnlyVoPlayback.pcc", MEGame.LE3);
        ExportEntry track = package.CreateExport("Track", "SFXInterpTrackPlayFaceOnlyVO", indexed: false);
        var properties = new PropertyCollection
        {
            new ArrayProperty<StructProperty>("m_aFOVOKeys")
            {
                CreateFaceOnlyVoKey(StringRef),
                CreateFaceOnlyVoKey(StringRef + 1)
            },
            new IntProperty(42, "UnrelatedSelection")
        };
        track.WriteProperties(properties);
        ExportEntry[][] targets = [CreateAudio(package), CreateAudio(package, StringRef + 1)];
        byte[] originalData = track.Data.ToArray();
        var navigated = new List<IEntry>();
        InitializeApplicationResources();
        var previewedAudio = new List<IEntry>();
        var interpreter = new InterpreterExportLoader
        {
            VoAudioPreviewCommand = new RelayCommand(audio => previewedAudio.Add((IEntry)audio))
        };
        using var view = new InterpreterTestView(track, entry => navigated.Add((IEntry)entry),
            interpreter: interpreter, properties: properties);
        interpreter.TreeSelectedItem = view.Node("UnrelatedSelection");
        interpreter.SelectedItem = view.Node("UnrelatedSelection");
        view.ExpandAll();
        UPropertyTreeViewEntry[] lines = view.AllNodes().Where(node => node.Property?.Name.Name == "nLineStrRef").ToArray();
        Assert.AreEqual(2, lines.Length);

        for (int i = 0; i < lines.Length; i++)
        {
            UPropertyTreeViewEntry node = lines[i];
            Assert.IsTrue(node.ShowVoTrackAudioNavigation);
            TextBox valueEditor = FindVisualDescendants<TextBox>(interpreter)
                .Single(textBox => ReferenceEquals(textBox.Tag, node) && textBox.MinWidth == 80);
            valueEditor.Text = "12345678";
            view.RefreshLayout();
            foreach ((RelayCommand command, string label, _, _) in Actions(interpreter))
            {
                Button button = view.AudioButtons(node, command).Single();
                Assert.AreEqual(label, button.Content);
                Assert.IsTrue(button.IsEnabled);
                InvokeButton(button);
            }
            InvokePlayMenu(view, node);
            Assert.AreEqual(StringRef + i, ((IntProperty)node.Property).Value);
        }

        foreach (UPropertyTreeViewEntry node in view.AllNodes().Where(node =>
                     node.Property?.Name.Name is "srActorNameOverride" or "pConversation"))
        {
            Assert.IsFalse(node.ShowVoTrackAudioNavigation);
            Assert.AreEqual(0, view.PlayButtons(node).Length);
            foreach ((RelayCommand command, _, _, _) in Actions(interpreter))
            {
                Assert.AreEqual(0, view.AudioButtons(node, command).Length);
            }
            Assert.IsFalse(interpreter.PlayMaleVoAudioCommand.CanExecute(node));
            Assert.IsFalse(interpreter.PlayFemaleVoAudioCommand.CanExecute(node));
        }
        CollectionAssert.AreEqual(targets.SelectMany(entries => entries.Cast<IEntry>()).ToArray(), navigated);
        CollectionAssert.AreEqual(targets.SelectMany(entries => entries.Skip(2).Cast<IEntry>()).ToArray(), previewedAudio);
        CollectionAssert.AreEqual(originalData, track.Data);
    });

    [DataTestMethod]
    [DataRow("BioEvtSysTrackSubtitles")]
    [DataRow("InterpTrackWwiseEvent")]
    public void SubtitleAndWwiseEventRowsUseTheirOwnCommittedValuesForNavigationAndPlayback(string className) => RunOnSta(() =>
    {
        using var subtitles = new LoadedTestSubtitles();
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("InterpreterSubtitleAndEventTracks.pcc", MEGame.LE3);
        ExportEntry track = package.CreateExport("Track", className, indexed: false);
        ExportEntry[][] targets = [CreateAudio(package), CreateAudio(package, StringRef + 1)];
        targets[1][1].ObjectName = $"Play_VO_{StringRef + 1}_f";
        bool subtitleTrack = className == "BioEvtSysTrackSubtitles";
        string structType = subtitleTrack ? "BioSubtitleTrackData" : "WwiseEventTrackKey";
        string propertyName = subtitleTrack ? "nStrRefID" : "Event";
        ExportEntry[] referencedEvents = [targets[0][0], targets[1][1]];
        var keys = new ArrayProperty<StructProperty>(subtitleTrack ? "m_aSubtitleData" : "WwiseEvents");
        for (int i = 0; i < targets.Length; i++)
        {
            keys.Add(new StructProperty(structType, subtitleTrack
                ? new PropertyCollection
                {
                    new IntProperty(StringRef + i, propertyName),
                    new FloatProperty(3, "fLength"),
                    new BoolProperty(false, "bShowAtTop")
                }
                : new PropertyCollection
                {
                    new ObjectProperty(referencedEvents[i], propertyName),
                    new FloatProperty(i, "Time"),
                    new ObjectProperty(referencedEvents[i], "OtherEvent")
                }));
            if (!subtitleTrack)
            {
                // The row's explicit reference must disambiguate identical event names.
                ExportEntry decoyStream = package.CreateExport($"DecoyAudio{i}", "WwiseStream", indexed: false);
                CreateEvent(package, referencedEvents[i].ObjectName.Name, decoyStream);
            }
        }
        var properties = new PropertyCollection
        {
            keys,
            new IntProperty(42, "UnrelatedSelection"),
            subtitleTrack
                ? new IntProperty(StringRef, propertyName)
                : new ObjectProperty(referencedEvents[0], propertyName)
        };
        track.WriteProperties(properties);
        byte[] originalData = track.Data.ToArray();
        var navigated = new List<IEntry>();
        var expectedNavigation = new List<IEntry>();
        var previewedAudio = new List<IEntry>();
        var expectedPlayback = new List<IEntry>();
        InitializeApplicationResources();
        var interpreter = new InterpreterExportLoader
        {
            VoAudioPreviewCommand = new RelayCommand(audio => previewedAudio.Add((IEntry)audio))
        };
        using var view = new InterpreterTestView(track, entry => navigated.Add((IEntry)entry),
            interpreter: interpreter, properties: properties);
        interpreter.TreeSelectedItem = view.Node("UnrelatedSelection");
        interpreter.SelectedItem = view.Node("UnrelatedSelection");
        view.ExpandAll();
        UPropertyTreeViewEntry[] lines = view.AllNodes().Where(node => node.Property?.Name.Name == propertyName
            && (node.UPParent?.Property as StructProperty)?.StructType == structType).ToArray();
        Assert.AreEqual(2, lines.Length);

        for (int i = 0; i < lines.Length; i++)
        {
            UPropertyTreeViewEntry node = lines[i];
            Assert.IsTrue(node.ShowVoTrackAudioNavigation);
            string expectedSubtitle = $"\"Track subtitle {i}\"";
            Assert.IsTrue(FindVisualDescendants<TextBlock>(interpreter).Any(textBlock => textBlock.IsVisible
                && ReferenceEquals(textBlock.DataContext, node) && textBlock.Text == expectedSubtitle));
            TextBox valueEditor = FindVisualDescendants<TextBox>(interpreter)
                .Single(textBox => ReferenceEquals(textBox.Tag, node)
                    && (subtitleTrack ? textBox.MinWidth == 80 : !textBox.IsReadOnly && textBox.Width == 60));
            string stagedReference = subtitleTrack ? "12345678" : referencedEvents[1 - i].UIndex.ToString();
            foreach (string stagedValue in new[] { stagedReference, "invalid" })
            {
                valueEditor.Text = stagedValue;
                view.RefreshLayout();
                foreach ((RelayCommand command, string label, _, _) in Actions(interpreter))
                {
                    Button button = view.AudioButtons(node, command).Single();
                    Assert.AreEqual(label, button.Content);
                    Assert.IsTrue(button.IsEnabled, label);
                    InvokeButton(button);
                }
                InvokePlayMenu(view, node);
                expectedNavigation.AddRange(targets[i]);
                expectedPlayback.AddRange(targets[i].Skip(2));
                Assert.AreEqual(subtitleTrack ? StringRef + i : referencedEvents[i].UIndex,
                    node.Property is IntProperty stringRef ? stringRef.Value : ((ObjectProperty)node.Property).Value);
                CollectionAssert.AreEqual(originalData, track.Data);
            }
        }

        interpreter.TreeSelectedItem = lines[0];
        interpreter.SelectedItem = lines[0];
        view.RefreshLayout();
        Assert.AreEqual(2, view.PlayButtons(lines[0]).Length);
        if (!subtitleTrack)
        {
            Assert.AreEqual(2, FindVisualDescendants<TextBlock>(interpreter).Count(textBlock => textBlock.IsVisible
                && ReferenceEquals(textBlock.DataContext, lines[0]) && textBlock.Text == "\"Track subtitle 0\""),
                "The referenced event's subtitle must appear inline and in the selected-property editor.");
        }
        foreach ((RelayCommand command, string label, _, _) in Actions(interpreter))
        {
            Button[] buttons = view.AudioButtons(lines[0], command);
            Assert.AreEqual(2, buttons.Length, $"{label} must appear in the row and selected-property editor.");
            Assert.IsTrue(buttons.All(button => button.IsEnabled), label);
        }

        foreach (UPropertyTreeViewEntry node in view.AllNodes().Except(lines))
        {
            Assert.IsFalse(node.ShowVoTrackAudioNavigation, node.DisplayName);
            Assert.AreEqual(0, view.PlayButtons(node).Length);
            Assert.IsFalse(interpreter.PlayMaleVoAudioCommand.CanExecute(node));
            Assert.IsFalse(interpreter.PlayFemaleVoAudioCommand.CanExecute(node));
            foreach ((RelayCommand command, _, _, _) in Actions(interpreter))
            {
                Assert.IsFalse(command.CanExecute(node));
                Assert.AreEqual(0, view.AudioButtons(node, command).Length);
            }
        }
        CollectionAssert.AreEqual(expectedNavigation, navigated);
        CollectionAssert.AreEqual(expectedPlayback, previewedAudio);
        CollectionAssert.AreEqual(originalData, track.Data);
    });

    private static StructProperty CreateFaceOnlyVoKey(int stringRef) => new("BioFOVOTrackData", new PropertyCollection
    {
        new IntProperty(stringRef, "nLineStrRef"),
        new StringRefProperty(StringRef, "srActorNameOverride"),
        new ObjectProperty(0, "pConversation")
    });

    private static ExportEntry CreateTrack(IMEPackage package)
    {
        ExportEntry track = package.CreateExport("Track", "BioEvtSysTrackVOElements", indexed: false);
        track.WriteProperties(new PropertyCollection
        {
            new IntProperty(StringRef, "m_nStrRefID"),
            new IntProperty(42, "UnrelatedSelection")
        });
        return track;
    }

    private static ExportEntry[] CreateAudio(IMEPackage package, int stringRef = StringRef)
    {
        ExportEntry maleStream = package.CreateExport($"scene_{stringRef}_m_wav", "WwiseStream", indexed: false);
        ExportEntry femaleStream = package.CreateExport($"scene_{stringRef}_f_wav", "WwiseStream", indexed: false);
        ExportEntry maleEvent = CreateEvent(package, $"VO_{stringRef}_m_Play", maleStream);
        ExportEntry femaleEvent = CreateEvent(package, $"VO_{stringRef}_f_Play", femaleStream);
        return [maleEvent, femaleEvent, maleStream, femaleStream];
    }

    private static ExportEntry CreateEvent(IMEPackage package, string name, ExportEntry stream)
    {
        ExportEntry audioEvent = package.CreateExport(name, "WwiseEvent", indexed: false);
        var properties = new PropertyCollection();
        if (package.Game == MEGame.LE2)
        {
            var relationships = new StructProperty("WwiseRelationships", false,
                new ArrayProperty<ObjectProperty>([new ObjectProperty(stream)], "Streams")) { Name = "Relationships" };
            properties.Add(new ArrayProperty<StructProperty>("References")
            {
                new("WwisePlatformRelationships", false, relationships)
            });
        }
        audioEvent.WritePropertiesAndBinary(properties, new WwiseEvent
        {
            Links = [new WwiseEvent.WwiseEventLink { WwiseBanks = [], WwiseStreams = [stream.UIndex] }]
        });
        return audioEvent;
    }

    private static (RelayCommand Command, string Label, bool IsFemale, bool IsStream)[] Actions(InterpreterExportLoader interpreter) =>
    [
        (interpreter.GoToMaleVoEventCommand, "Male event", false, false),
        (interpreter.GoToFemaleVoEventCommand, "Female event", true, false),
        (interpreter.GoToMaleVoStreamCommand, "Male stream", false, true),
        (interpreter.GoToFemaleVoStreamCommand, "Female stream", true, true)
    ];

    private static void Navigate(InterpreterExportLoader interpreter, UPropertyTreeViewEntry node,
        bool isFemale, bool isStream, bool newWindow)
    {
        MethodInfo method = typeof(InterpreterExportLoader).GetMethod("NavigateToVoTrackAudio",
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(UPropertyTreeViewEntry), typeof(bool), typeof(bool), typeof(bool)], null);
        Assert.IsNotNull(method);
        method.Invoke(interpreter, [node, isFemale, isStream, newWindow]);
    }

    private static void RunOnSta(Action action)
    {
        // Share the existing WPF test dispatcher: Application can only be created once per process.
        var dispatcher = (Lazy<Dispatcher>)typeof(InterpreterObjectNavigationTests)
            .GetField("StaDispatcher", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
        dispatcher.Value.Invoke(action);
    }

    private static void InitializeApplicationResources()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(InterpreterExportLoader).Assembly);
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
                yield return match;
            foreach (T descendant in FindVisualDescendants<T>(child))
                yield return descendant;
        }
    }

    private static void InvokeButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        FlushDispatcher();
    }

    private static void InvokePlayMenu(InterpreterTestView view, UPropertyTreeViewEntry node)
    {
        Button button = view.PlayButtons(node).Single();
        Assert.AreEqual("Play audio", button.Content);
        Assert.IsTrue(button.IsEnabled);
        Assert.AreSame(button, button.CommandParameter);
        InvokeButton(button);
        ContextMenu menu = button.ContextMenu;
        Assert.IsNotNull(menu);
        try
        {
            Assert.IsTrue(menu.IsOpen);
            MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
            Assert.AreEqual(2, items.Length);
            CollectionAssert.AreEqual(new[] { "Play male audio", "Play female audio" },
                items.Select(item => (string)item.Header).ToArray());
            CollectionAssert.AreEqual(new[] { view.Interpreter.PlayMaleVoAudioCommand, view.Interpreter.PlayFemaleVoAudioCommand },
                items.Select(item => item.Command).ToArray());
            foreach (MenuItem item in items)
            {
                Assert.AreSame(node, item.CommandParameter);
                Assert.IsTrue(item.Command.CanExecute(item.CommandParameter));
                item.Command.Execute(item.CommandParameter);
            }
        }
        finally
        {
            menu.IsOpen = false;
        }
    }

    private static void FlushDispatcher() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private sealed class LoadedTestSubtitles : IDisposable
    {
        private readonly ME2ME3LazyTLK talkFile = new();

        public LoadedTestSubtitles()
        {
            using var stream = HuffmanCompression.SaveToTlkStream([
                new TLKStringRef(StringRef, "Track subtitle 0"),
                new TLKStringRef(StringRef + 1, "Track subtitle 1")]);
            stream.Position = 0;
            talkFile.LoadTlkDataFromStream(stream);
            LE3TalkFiles.LoadedTlks.Add(talkFile);
        }

        public void Dispose() => LE3TalkFiles.LoadedTlks.Remove(talkFile);
    }

    private sealed class InterpreterTestView : IDisposable
    {
        private readonly Window window;
        public InterpreterExportLoader Interpreter { get; }

        public InterpreterTestView(ExportEntry export, Action<object> navigate, Action<object> newWindow = null,
            InterpreterExportLoader interpreter = null, PropertyCollection properties = null)
        {
            InitializeApplicationResources();
            Interpreter = interpreter ?? new InterpreterExportLoader();
            Interpreter.ReloadPropertyDataAfterWrite = false;
            Interpreter.NavigateToEntryCommand = navigate is null ? null : new RelayCommand(navigate);
            Interpreter.OpenEntryInNewWindowCommand = newWindow is null ? null : new RelayCommand(newWindow);
            window = new Window
            {
                Content = Interpreter, Width = 1500, Height = 700,
                ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000
            };
            window.Show();
            Interpreter.LoadExport(export, properties);
            Interpreter.PropertyNodes[0].IsExpanded = true;
            RefreshLayout();
        }

        public UPropertyTreeViewEntry Node(string name) => Interpreter.PropertyNodes[0].ChildrenProperties
            .Single(node => node.Property?.Name.Name == name);

        public Button[] AudioButtons(UPropertyTreeViewEntry node, RelayCommand command) => FindVisualDescendants<Button>(Interpreter)
            .Where(button => button.IsVisible && ReferenceEquals(button.Command, command)
                && ReferenceEquals(button.CommandParameter, node)).ToArray();

        public Button[] PlayButtons(UPropertyTreeViewEntry node) => FindVisualDescendants<Button>(Interpreter)
            .Where(button => button.IsVisible && ReferenceEquals(button.Command, Interpreter.PlayVoAudioCommand)
                && ReferenceEquals(button.DataContext, node)).ToArray();

        public IEnumerable<UPropertyTreeViewEntry> AllNodes() => Interpreter.PropertyNodes.SelectMany(DescendantsAndSelf);

        private static IEnumerable<UPropertyTreeViewEntry> DescendantsAndSelf(UPropertyTreeViewEntry node)
        {
            yield return node;
            foreach (UPropertyTreeViewEntry child in node.ChildrenProperties.SelectMany(DescendantsAndSelf))
                yield return child;
        }

        public void ExpandAll()
        {
            foreach (UPropertyTreeViewEntry node in AllNodes())
                node.IsExpanded = true;
            RefreshLayout();
        }

        public void RefreshLayout()
        {
            FlushDispatcher();
            window.UpdateLayout();
        }

        public void Dispose()
        {
            foreach (Button button in FindVisualDescendants<Button>(Interpreter))
            {
                if (button.ContextMenu is { } menu)
                    menu.IsOpen = false;
            }
            window.Close();
            Interpreter.Dispose();
        }
    }
}
