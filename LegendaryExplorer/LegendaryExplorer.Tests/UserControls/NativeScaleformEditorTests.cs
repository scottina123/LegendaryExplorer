using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.MainWindow;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.ExportLoaderControls.ScriptEditor.IDE;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Scaleform;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class NativeScaleformEditorTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    // Keep the WPF resources and all interaction checks on a single STA thread, as the
    // application's resource dictionary is shared by controls constructed during these checks.
    [STATestMethod]
    public void DraftsCompileAtomicallyAndSurviveNavigationAndFailedApplies()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(NativeScaleformExportLoader).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));

        AssertNavigationPreservesSeparateDrafts();
        AssertDeselectionKeepsDraftAndDisablesTheEditor();
        AssertCompileChangesOnlyTheSelectedTag();
        AssertScriptCompilePreservesInitSpriteIdentity();
        AssertInvalidFrameRateKeepsPackageBytes();
        AssertInvalidAssemblyKeepsPackageAndPendingEdits();
        AssertExternalChangesKeepPackageAndDraft();
        AssertRepeatedUnloadAndDisposeAreSafe();
        AssertHostedCloseVetoPreservesTheEditor();
        AssertPackageCloseVetoPreservesTheEditor();
    }

    private static void AssertNavigationPreservesSeparateDrafts()
    {
        using var fixture = new Fixture();
        using var editor = new NativeScaleformExportLoader();
        byte[] firstOriginal = fixture.First.Data;
        byte[] secondOriginal = fixture.Second.Data;
        editor.LoadExport(fixture.First);
        Assert.IsTrue(editor.HasMovie, editor.Status);
        Select(editor, 43);
        Payload(editor).Text = "First movie draft";
        FrameRate(editor).Text = "29.5";
        editor.LoadExport(fixture.Second);
        Assert.IsTrue(editor.HasMovie, editor.Status);
        Select(editor, 43);
        Payload(editor).Text = "Second movie draft";

        editor.LoadExport(fixture.First);
        Assert.IsTrue(editor.HasMovie, editor.Status);
        Select(editor, 43);
        Assert.AreEqual("First movie draft", Payload(editor).Text);
        Assert.AreEqual("29.5", FrameRate(editor).Text);
        editor.LoadExport(fixture.Second);
        Assert.IsTrue(editor.HasMovie, editor.Status);
        Select(editor, 43);
        Assert.AreEqual("Second movie draft", Payload(editor).Text);
        CollectionAssert.AreEqual(firstOriginal, fixture.First.Data);
        CollectionAssert.AreEqual(secondOriginal, fixture.Second.Data);
    }

    private static void AssertCompileChangesOnlyTheSelectedTag()
    {
        using var fixture = new Fixture();
        using var editor = new NativeScaleformExportLoader();
        var before = ScaleformDocument.Parse(MovieBytes(fixture.First));
        byte[] secondOriginal = fixture.Second.Data;
        editor.LoadExport(fixture.First);
        Select(editor, 43);
        string edited = new('x', 85); // Also forces a short-to-long SWF tag-header transition.
        Payload(editor).Text = edited;
        Compile(editor);

        StringAssert.Contains(editor.Status, "Compiled");
        var after = ScaleformDocument.Parse(MovieBytes(fixture.First));
        Assert.AreEqual(edited, after.Tags[0].TextValue);
        Assert.AreEqual(before.FrameRate, after.FrameRate);
        for (int i = 1; i < before.Tags.Count; i++)
            CollectionAssert.AreEqual(before.Tags[i].Payload.ToArray(), after.Tags[i].Payload.ToArray(), $"Unselected tag {i} changed.");
        Assert.AreEqual(123, fixture.First.GetProperty<IntProperty>("EditorTestMarker").Value);
        CollectionAssert.AreEqual(secondOriginal, fixture.Second.Data);

        byte[] applied = fixture.First.Data;
        Compile(editor);
        CollectionAssert.AreEqual(applied, fixture.First.Data, "An unchanged second compile must be a no-op.");
    }

    private static void AssertDeselectionKeepsDraftAndDisablesTheEditor()
    {
        using var fixture = new Fixture();
        using var editor = new NativeScaleformExportLoader();
        editor.LoadExport(fixture.First);
        Select(editor, 43);
        Payload(editor).Text = "draft before filtering";
        typeof(NativeScaleformExportLoader).GetMethod("Tag_Selected", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, [editor, new RoutedPropertyChangedEventArgs<object>(editor.VisibleTags[0], null)]);
        Assert.IsFalse(editor.HasSelection);
        Assert.IsTrue(Payload(editor).IsReadOnly);
        Assert.AreEqual(string.Empty, Payload(editor).Text);
        ((TextBox)editor.FindName("FilterBox")).Text = "DoAction";
        Assert.IsTrue(editor.VisibleTags.All(node => node.Tag.Code == 12));
        ((TextBox)editor.FindName("FilterBox")).Text = string.Empty;
        Select(editor, 43);
        Assert.AreEqual("draft before filtering", Payload(editor).Text);
        Compile(editor);
        Assert.AreEqual("draft before filtering", ScaleformDocument.Parse(MovieBytes(fixture.First)).Tags[0].TextValue);
    }

    private static void AssertScriptCompilePreservesInitSpriteIdentity()
    {
        using var fixture = new Fixture();
        using var editor = new NativeScaleformExportLoader();
        editor.LoadExport(fixture.First);
        Select(editor, 12);
        const string source = ".pushstring \"compiled natively\"\nPop\nEnd";
        Payload(editor).Text = source;
        Compile(editor);
        StringAssert.Contains(editor.Status, "Compiled");
        var document = ScaleformDocument.Parse(MovieBytes(fixture.First));
        CollectionAssert.AreEqual(ScaleformActionScript.Assemble(source), document.Tags.Single(tag => tag.Code == 12).Payload.ToArray());
        Assert.AreEqual("original", document.Tags[0].TextValue);

        Select(editor, 59);
        Payload(editor).Text = ".pushstring \"sprite initialization\"\nPop\nEnd";
        Compile(editor);
        StringAssert.Contains(editor.Status, "Compiled");
        var init = ScaleformDocument.Parse(MovieBytes(fixture.First)).Tags.Single(tag => tag.Code == 59);
        Assert.AreEqual((ushort)0x1234, BinaryPrimitives.ReadUInt16LittleEndian(init.Payload.Span));
        CollectionAssert.AreEqual(ScaleformActionScript.Assemble(".pushstring \"sprite initialization\"\nPop\nEnd"), init.Payload.Span[2..].ToArray());
        CollectionAssert.AreEqual(ScaleformActionScript.Assemble(source), ScaleformDocument.Parse(MovieBytes(fixture.First)).Tags.Single(tag => tag.Code == 12).Payload.ToArray());
    }

    private static void AssertInvalidFrameRateKeepsPackageBytes()
    {
        using var fixture = new Fixture();
        using var editor = new NativeScaleformExportLoader();
        byte[] original = fixture.First.Data;
        editor.LoadExport(fixture.First);
        Select(editor, 43);
        Payload(editor).Text = "pending valid label";
        foreach (string invalid in new[] { "not a number", "0", "NaN", "256", "Infinity" })
        {
            FrameRate(editor).Text = invalid;
            Compile(editor);
            StringAssert.Contains(editor.Status, "Could not apply");
            CollectionAssert.AreEqual(original, fixture.First.Data, invalid);
            Assert.AreEqual("pending valid label", Payload(editor).Text);
        }
        FrameRate(editor).Text = "24";
        Compile(editor);
        Assert.AreEqual("pending valid label", ScaleformDocument.Parse(MovieBytes(fixture.First)).Tags[0].TextValue);
    }

    private static void AssertInvalidAssemblyKeepsPackageAndPendingEdits()
    {
        using var fixture = new Fixture();
        using var editor = new NativeScaleformExportLoader();
        byte[] original = fixture.First.Data;
        editor.LoadExport(fixture.First);
        Select(editor, 43);
        Payload(editor).Text = "valid edit before invalid script";
        Select(editor, 12);
        const string invalid = "99 Jump -> missing\nEnd";
        Payload(editor).Text = invalid;
        Compile(editor);
        StringAssert.Contains(editor.Status, "Could not apply");
        CollectionAssert.AreEqual(original, fixture.First.Data);

        // A partially staged label must not become the new baseline on navigation, otherwise
        // CaptureDraft can silently remove that pending edit when the user returns to it.
        editor.LoadExport(fixture.Second);
        editor.LoadExport(fixture.First);
        Select(editor, 43);
        Assert.AreEqual("valid edit before invalid script", Payload(editor).Text);
        Select(editor, 12);
        Assert.AreEqual(invalid, Payload(editor).Text);
        Payload(editor).Text = "Play\nEnd";
        Compile(editor);
        var compiled = ScaleformDocument.Parse(MovieBytes(fixture.First));
        Assert.AreEqual("valid edit before invalid script", compiled.Tags[0].TextValue);
        CollectionAssert.AreEqual(new byte[] { 6, 0 }, compiled.Tags.Single(tag => tag.Code == 12).Payload.ToArray());
    }

    private static void AssertExternalChangesKeepPackageAndDraft()
    {
        using var fixture = new Fixture();
        using var editor = new NativeScaleformExportLoader();
        editor.LoadExport(fixture.First);
        Select(editor, 43);
        Payload(editor).Text = "my pending draft";
        var external = ScaleformDocument.Parse(MovieBytes(fixture.First));
        external.Tags[1].SetText("changed by another editor");
        fixture.First.WriteProperty(new ImmutableByteArrayProperty(external.Compile(), "RawData"));
        byte[] externalData = fixture.First.Data;
        Compile(editor);
        StringAssert.Contains(editor.Status, "another editor");
        CollectionAssert.AreEqual(externalData, fixture.First.Data);
        Assert.AreEqual("my pending draft", Payload(editor).Text);

        editor.LoadExport(fixture.Second);
        editor.LoadExport(fixture.First);
        Select(editor, 43);
        Assert.AreEqual("my pending draft", Payload(editor).Text);
        Compile(editor);
        CollectionAssert.AreEqual(externalData, fixture.First.Data);
        Assert.AreEqual("changed by another editor", ScaleformDocument.Parse(MovieBytes(fixture.First)).Tags[1].TextValue);
    }

    private static void AssertRepeatedUnloadAndDisposeAreSafe()
    {
        using var fixture = new Fixture();
        using var editor = new NativeScaleformExportLoader();
        editor.LoadExport(fixture.First);
        Select(editor, 43);
        Payload(editor).Text = "survives unload";
        editor.UnloadExport();
        editor.UnloadExport();
        Assert.IsFalse(editor.HasMovie);
        Assert.IsEmpty(editor.VisibleTags);
        editor.LoadExport(fixture.First);
        Select(editor, 43);
        Assert.AreEqual("survives unload", Payload(editor).Text);
        editor.Dispose();
        editor.Dispose();
        editor.UnloadExport();
        Assert.IsFalse(editor.HasMovie);
        Assert.IsEmpty(editor.VisibleTags);
    }

    private static void AssertHostedCloseVetoPreservesTheEditor()
    {
        using var fixture = new Fixture();
        using var control = new CloseCheckControl();
        control.LoadExport(fixture.First);
        object controlContext = new();
        control.DataContext = controlContext;
        var window = new ExportLoaderHostedWindow(control, fixture.First);
        bool previousClosePermission = LEXMainWindow.IsAllowedToClose;
        bool closingEventRaised = false;
        window.Closing += (_, _) => closingEventRaised = true;
        try
        {
            var args = new CancelEventArgs();
            typeof(ExportLoaderHostedWindow).GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [args]);

            Assert.IsTrue(args.Cancel, "A host must honor the editor's pending-draft veto.");
            Assert.AreEqual(1, control.CloseChecks);
            Assert.IsFalse(closingEventRaised, "The veto must run before Closing handlers remove bindings and dispose controls.");
            Assert.IsFalse(control.Disposed);
            Assert.AreSame(window, window.DataContext);
            Assert.AreSame(controlContext, control.DataContext);
            Assert.AreSame(fixture.First, window.LoadedExport);
            Assert.AreSame(fixture.First, control.CurrentLoadedExport);
            Assert.IsTrue(window.ContentGrid.Children.Contains(control));
        }
        finally
        {
            control.AllowClose = true;
            window.Close();
            LEXMainWindow.IsAllowedToClose = previousClosePermission;
        }
    }

    private static void AssertPackageCloseVetoPreservesTheEditor()
    {
        using var fixture = new Fixture();
        using var control = new CloseCheckControl();
        control.LoadExport(fixture.First);
        object controlContext = new();
        control.DataContext = controlContext;
        SyntaxInfo.LoadFromSettings();
        var settingsLoaded = typeof(Settings).GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
        object previousSettingsLoaded = settingsLoaded.GetValue(null);
        bool previousClosePermission = LEXMainWindow.IsAllowedToClose;
        settingsLoaded.SetValue(null, false);
        PackageEditorWindow window = null;
        try
        {
            window = new PackageEditorWindow(submitTelemetry: false, enableRecents: false);
            var loaders = (Dictionary<ExportLoaderControl, TabItem>)typeof(PackageEditorWindow)
                .GetField("ExportLoaders", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
            loaders.Add(control, new TabItem());
            bool closingEventRaised = false;
            window.Closing += (_, _) => closingEventRaised = true;
            var args = new CancelEventArgs();
            typeof(PackageEditorWindow).GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [args]);

            Assert.IsTrue(args.Cancel, "Package Editor must query every export loader before disposal.");
            Assert.AreEqual(1, control.CloseChecks);
            Assert.IsFalse(closingEventRaised);
            Assert.IsFalse(control.Disposed);
            Assert.AreSame(window, window.DataContext);
            Assert.AreSame(controlContext, control.DataContext);
            Assert.AreSame(fixture.First, control.CurrentLoadedExport);
            Assert.IsTrue(loaders.ContainsKey(control));
        }
        finally
        {
            control.AllowClose = true;
            window?.Close();
            settingsLoaded.SetValue(null, previousSettingsLoaded);
            LEXMainWindow.IsAllowedToClose = previousClosePermission;
        }
    }

    private static void Select(NativeScaleformExportLoader editor, ushort code)
    {
        Assert.IsTrue(editor.HasMovie, editor.Status);
        var node = editor.VisibleTags.First(tag => tag.Tag.Code == code);
        typeof(NativeScaleformExportLoader).GetMethod("Tag_Selected", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, [editor, new RoutedPropertyChangedEventArgs<object>(null, node)]);
        Assert.IsFalse(Payload(editor).IsReadOnly, editor.SelectionHelp);
    }

    private static void Compile(NativeScaleformExportLoader editor) =>
        typeof(NativeScaleformExportLoader).GetMethod("CompileApply_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, [editor, new RoutedEventArgs()]);

    private static TextBox Payload(NativeScaleformExportLoader editor) => (TextBox)editor.FindName("PayloadEditor");
    private static TextBox FrameRate(NativeScaleformExportLoader editor) => (TextBox)editor.FindName("FrameRateEditor");
    private static byte[] MovieBytes(ExportEntry export) => export.GetProperty<ImmutableByteArrayProperty>(export.ClassName == "BioSWF" ? "Data" : "RawData").Bytes;

    private sealed class Fixture : IDisposable
    {
        private readonly IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("NativeScaleformEditorTest.pcc", MEGame.LE3);
        public ExportEntry First { get; }
        public ExportEntry Second { get; }

        public Fixture()
        {
            First = package.CreateExport("FirstMovie", "GFxMovieInfo", indexed: false);
            Second = package.CreateExport("SecondMovie", "GFxMovieInfo", indexed: false);
            byte[] movie = BuildMovie();
            First.WriteProperty(new ImmutableByteArrayProperty(movie, "RawData"));
            First.WriteProperty(new IntProperty(123, "EditorTestMarker"));
            Second.WriteProperty(new ImmutableByteArrayProperty(movie, "RawData"));
        }

        public void Dispose() => package.Dispose();
    }

    private sealed class CloseCheckControl() : ExportLoaderControl("Native Scaleform close-veto test")
    {
        public bool AllowClose { get; set; }
        public bool Disposed { get; private set; }
        public int CloseChecks { get; private set; }
        public override bool CanParse(ExportEntry exportEntry) => true;
        public override void LoadExport(ExportEntry exportEntry) => CurrentLoadedExport = exportEntry;
        public override void UnloadExport() => CurrentLoadedExport = null;
        public override bool CanClose()
        {
            CloseChecks++;
            return AllowClose;
        }
        public override void PopOut() { }
        public override void Dispose()
        {
            Disposed = true;
            CurrentLoadedExport = null;
        }
    }

    private static byte[] BuildMovie()
    {
        byte[] body = Join([8, 0, 0, 24, 1, 0], // Minimal RECT, FIXED8 frame rate=24, one frame.
            Tag(43, Encoding.UTF8.GetBytes("original\0")), Tag(43, Encoding.UTF8.GetBytes("other label\0")),
            Tag(12, [7, 0]), Tag(59, [0x34, 0x12, 7, 0]), Tag(999, [0xDE, 0xAD, 0xBE, 0xEF]), Tag(1, []), Tag(0, []));
        using var output = new MemoryStream();
        output.Write("FWS"u8);
        output.WriteByte(10);
        output.Write(BitConverter.GetBytes((uint)body.Length + 8));
        output.Write(body);
        return output.ToArray();
    }

    private static byte[] Tag(ushort code, byte[] payload)
    {
        bool longHeader = payload.Length >= 63;
        byte[] record = new byte[payload.Length + (longHeader ? 6 : 2)];
        BinaryPrimitives.WriteUInt16LittleEndian(record, (ushort)((code << 6) | (longHeader ? 63 : payload.Length)));
        if (longHeader) BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(2), (uint)payload.Length);
        payload.CopyTo(record, longHeader ? 6 : 2);
        return record;
    }

    private static byte[] Join(params byte[][] values) => values.SelectMany(value => value).ToArray();
}
