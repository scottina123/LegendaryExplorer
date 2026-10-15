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

    private static ExportEntry[] CreateAudio(IMEPackage package)
    {
        ExportEntry maleStream = package.CreateExport($"scene_{StringRef}_m_wav", "WwiseStream", indexed: false);
        ExportEntry femaleStream = package.CreateExport($"scene_{StringRef}_f_wav", "WwiseStream", indexed: false);
        ExportEntry maleEvent = CreateEvent(package, $"VO_{StringRef}_m_Play", maleStream);
        ExportEntry femaleEvent = CreateEvent(package, $"VO_{StringRef}_f_Play", femaleStream);
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

    private static void FlushDispatcher() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private sealed class InterpreterTestView : IDisposable
    {
        private readonly Window window;
        public InterpreterExportLoader Interpreter { get; }

        public InterpreterTestView(ExportEntry export, Action<object> navigate, Action<object> newWindow = null)
        {
            InitializeApplicationResources();
            Interpreter = new InterpreterExportLoader
            {
                ReloadPropertyDataAfterWrite = false,
                NavigateToEntryCommand = navigate is null ? null : new RelayCommand(navigate),
                OpenEntryInNewWindowCommand = newWindow is null ? null : new RelayCommand(newWindow)
            };
            window = new Window
            {
                Content = Interpreter, Width = 1500, Height = 700,
                ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000
            };
            window.Show();
            Interpreter.LoadExport(export);
            Interpreter.PropertyNodes[0].IsExpanded = true;
            RefreshLayout();
        }

        public UPropertyTreeViewEntry Node(string name) => Interpreter.PropertyNodes[0].ChildrenProperties
            .Single(node => node.Property?.Name.Name == name);

        public Button[] AudioButtons(UPropertyTreeViewEntry node, RelayCommand command) => FindVisualDescendants<Button>(Interpreter)
            .Where(button => button.IsVisible && ReferenceEquals(button.Command, command)
                && ReferenceEquals(button.CommandParameter, node)).ToArray();

        public void RefreshLayout()
        {
            FlushDispatcher();
            window.UpdateLayout();
        }

        public void Dispose()
        {
            window.Close();
            Interpreter.Dispose();
        }
    }
}
