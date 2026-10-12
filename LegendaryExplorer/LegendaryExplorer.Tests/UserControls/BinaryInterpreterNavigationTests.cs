using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Interfaces;
using LegendaryExplorer.SharedUI.PeregrineTreeView;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Gammtek.IO;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class BinaryInterpreterNavigationTests
{
    // Run this class in its own test host: WPF permits one Application and owning STA per process.
    private static readonly Lazy<Dispatcher> StaDispatcher = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = nameof(BinaryInterpreterNavigationTests) };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    });

    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    private static void RunOnSta(Action action) => StaDispatcher.Value.Invoke(action);

    [TestMethod]
    public void GoToButtonsNavigateEveryObjectTagWithoutUsingSelectionOrCommittingStagedEdits() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("BinaryNavigationTags.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Source", "Object", indexed: false);
        ExportEntry first = package.CreateExport("First", "Object", indexed: false);
        ImportEntry second = AddImport(package, "Second");
        ExportEntry third = package.CreateExport("Third", "Object", indexed: false);
        source.Data = IndexBytes(first.UIndex, second.UIndex, third.UIndex);
        byte[] originalData = source.Data.ToArray();
        BinInterpNode[] references =
        [
            new(0, "Object", BinaryInterpreterWPF.NodeType.ObjectProperty),
            new(4, "Array element", BinaryInterpreterWPF.NodeType.ArrayLeafObject),
            new(8, "Struct field", BinaryInterpreterWPF.NodeType.StructLeafObject)
        ];
        foreach (BinInterpNode node in references)
        {
            node.Length = 4;
            node.UIndexValue = source.UIndex; // An obsolete cache must not replace the actual binary reference.
        }
        var unrelated = new BinInterpNode("Unrelated selection");
        var navigated = new List<IEntry>();
        using var view = new BinaryTestView(source, entry => navigated.Add((IEntry)entry),
            items: references.Cast<ITreeItem>().Append(unrelated).ToArray());
        view.Select(unrelated);

        foreach (BinInterpNode node in references)
        {
            Assert.IsTrue(node.IsObjectReference);
            Button button = view.GoToButton(node);
            Assert.AreEqual("Go to", button.Content);
            Assert.IsTrue(button.IsEnabled);
            if (node.ShowObjectInlineEditor)
            {
                TextBox editor = FindVisualDescendants<TextBox>(view.Interpreter)
                    .Single(textBox => textBox.IsVisible && textBox.Width == 70 && ReferenceEquals(textBox.Tag, node));
                editor.Text = "invalid staged index";
                view.RefreshLayout();
                Assert.AreEqual("invalid staged index", node.InlineObjectIndexValue);
            }
            InvokeButton(button);
            Assert.AreSame(unrelated, view.Tree.SelectedItem);
        }

        CollectionAssert.AreEqual(new IEntry[] { first, second, third }, navigated);
        CollectionAssert.AreEqual(originalData, source.Data);
        Assert.IsFalse(unrelated.IsObjectReference);
        Assert.IsFalse(view.Interpreter.GoToReferenceCommand.CanExecute(unrelated));
    });

    [TestMethod]
    public void EmbeddedPropertiesAndArrayElementsExposeWorkingGoToButtons() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("BinaryNavigationProperties.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Source", "Object", indexed: false);
        ExportEntry first = package.CreateExport("First", "Object", indexed: false);
        ImportEntry second = AddImport(package, "Second");
        var firstNode = new UPropertyTreeViewEntry(new ObjectProperty(first.UIndex, "Embedded"), "Embedded")
        {
            AttachedExport = source,
            InlineObjectIndexValue = second.UIndex.ToString()
        };
        var array = new UPropertyTreeViewEntry(new ArrayProperty<ObjectProperty>("References"), "References")
        {
            AttachedExport = source,
            IsExpanded = true
        };
        var secondNode = new UPropertyTreeViewEntry(new ObjectProperty(second.UIndex), "[0]")
        {
            AttachedExport = source,
            UPParent = array
        };
        array.ChildrenProperties.Add(secondNode);
        var navigated = new List<IEntry>();
        byte[] originalData = source.Data.ToArray();
        using var view = new BinaryTestView(source, entry => navigated.Add((IEntry)entry), items: [firstNode, array]);

        InvokeButton(view.GoToButton(firstNode));
        InvokeButton(view.GoToButton(secondNode));

        CollectionAssert.AreEqual(new IEntry[] { first, second }, navigated);
        CollectionAssert.AreEqual(originalData, source.Data);
        Assert.AreEqual(first.UIndex, ((ObjectProperty)firstNode.Property).Value);
    });

    [TestMethod]
    public void ResolvedWwiseAndDerivedReferencesUseTheirMetadataInsteadOfUnrelatedBinaryValues() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("BinaryNavigationResolved.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Source", "Object", indexed: false);
        ExportEntry unrelated = package.CreateExport("Unrelated", "Object", indexed: false);
        ExportEntry stream = package.CreateExport("Stream", "WwiseStream", indexed: false);
        ExportEntry replacementStream = package.CreateExport("ReplacementStream", "WwiseStream", indexed: false);
        ImportEntry mesh = AddImport(package, "StaticMesh");
        source.Data = IndexBytes(unrelated.UIndex, unrelated.UIndex, replacementStream.UIndex);
        byte[] originalData = source.Data.ToArray();
        var wwise = new BinInterpNode(0, "Wwise ID", BinaryInterpreterWPF.NodeType.StructLeafInt)
        {
            ReferencedWwiseStreamUIndex = stream.UIndex,
            WwiseStreamTlkDisplayText = "No TLK ID found"
        };
        var derived = new BinInterpNode(4, "Mesh transform") { ReferencedEntryUIndex = mesh.UIndex };
        var taggedStream = new BinInterpNode(8, "Stream object reference", BinaryInterpreterWPF.NodeType.StructLeafObject)
        {
            ReferencedWwiseStreamUIndex = stream.UIndex
        };
        var nullDerived = new BinInterpNode(0, "Null mesh") { ReferencedEntryUIndex = 0 };
        var navigated = new List<IEntry>();
        using var view = new BinaryTestView(source, entry => navigated.Add((IEntry)entry), items: [wwise, derived, taggedStream, nullDerived]);

        Assert.IsTrue(wwise.IsObjectReference);
        Assert.IsTrue(derived.IsObjectReference);
        InvokeButton(view.GoToButton(wwise));
        InvokeButton(view.GoToButton(derived));
        InvokeButton(view.GoToButton(taggedStream));
        Assert.IsFalse(view.GoToButton(nullDerived).IsEnabled);
        Assert.IsFalse(view.Interpreter.GoToReferenceCommand.CanExecute(nullDerived));
        Navigate(view.Interpreter, nullDerived, false);

        CollectionAssert.AreEqual(new IEntry[] { stream, mesh, replacementStream }, navigated,
            "A tagged object reference must read current bytes even when it has cached Wwise metadata.");
        CollectionAssert.AreEqual(originalData, source.Data);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ResolverUsesCurrentBinaryAndPackageEndianAndRejectsOutOfBoundsOffsets(bool bigEndian) => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("BinaryNavigationEndian.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Source", "Object", indexed: false);
        ExportEntry target = package.CreateExport("Target", "Object", indexed: false);
        ImportEntry import = AddImport(package, "ImportedTarget");
        // The in-memory package factory creates PC packages; changing this fixture's endian exercises console reads.
        typeof(MEPackage).GetField("<Endian>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(package, bigEndian ? Endian.Big : Endian.Little);
        source.Data = IndexBytes(bigEndian, 0, target.UIndex, import.UIndex, int.MaxValue, int.MinValue);
        using var view = new BinaryTestView(source, _ => { });
        var reference = new BinInterpNode(4, "Reference", BinaryInterpreterWPF.NodeType.StructLeafObject)
        {
            UIndexValue = import.UIndex,
            InlineObjectIndexValue = "invalid"
        };
        Assert.AreSame(target, Resolve(view.Interpreter, reference));

        byte[] updated = source.Data;
        IndexBytes(bigEndian, import.UIndex).CopyTo(updated, 4);
        source.Data = updated;
        Assert.AreSame(import, Resolve(view.Interpreter, reference), "A cached scan value must not survive a binary change.");
        reference.Offset = 8;
        Assert.AreSame(import, Resolve(view.Interpreter, reference));

        foreach (int offset in new[] { -1, int.MinValue, 0, 12, 16, source.DataSize - 3, source.DataSize, int.MaxValue })
        {
            reference.Offset = offset;
            Assert.IsNull(Resolve(view.Interpreter, reference), $"Offset {offset} must not navigate.");
            Assert.IsFalse(view.Interpreter.GoToReferenceCommand.CanExecute(reference));
        }
        Assert.IsNull(Resolve(view.Interpreter, new BinInterpNode(4, "Integer", BinaryInterpreterWPF.NodeType.StructLeafInt)));
        SetLoadedExport(view.Interpreter, null);
        Assert.IsNull(Resolve(view.Interpreter, new BinInterpNode(4, "Unloaded", BinaryInterpreterWPF.NodeType.StructLeafObject)));
    });

    [TestMethod]
    public void InvalidEmbeddedAndSpecialReferencesCannotNavigate() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("BinaryNavigationInvalid.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Source", "Object", indexed: false);
        ExportEntry target = package.CreateExport("Target", "Object", indexed: false);
        int callbacks = 0;
        using var view = new BinaryTestView(source, _ => callbacks++, _ => callbacks++);
        object[] invalid =
        [
            null,
            new UPropertyTreeViewEntry(new ObjectProperty(0)) { AttachedExport = source },
            new UPropertyTreeViewEntry(new ObjectProperty(int.MaxValue)) { AttachedExport = source },
            new UPropertyTreeViewEntry(new ObjectProperty(int.MinValue)) { AttachedExport = source },
            new UPropertyTreeViewEntry(new IntProperty(target.UIndex)) { AttachedExport = source },
            new UPropertyTreeViewEntry(new ObjectProperty(target.UIndex)),
            new UPropertyTreeViewEntry(new ObjectProperty(target.UIndex)) { AttachedExport = target },
            new BinInterpNode("Invalid derived reference") { ReferencedEntryUIndex = int.MaxValue },
            new BinInterpNode("Invalid stream") { ReferencedWwiseStreamUIndex = int.MaxValue },
            new BinInterpNode("Not a stream") { ReferencedWwiseStreamUIndex = target.UIndex }
        ];

        foreach (object node in invalid)
        {
            Assert.IsNull(Resolve(view.Interpreter, node));
            Assert.IsFalse(view.Interpreter.GoToReferenceCommand.CanExecute(node));
            Navigate(view.Interpreter, node, false);
            Navigate(view.Interpreter, node, true);
        }
        Assert.AreEqual(0, callbacks);
    });

    [TestMethod]
    public void ShiftNavigationUsesNewWindowsAndNeverFallsBackToTheCurrentEditor() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("BinaryNavigationShift.pcc", MEGame.LE3);
        ExportEntry source = package.CreateExport("Source", "Object", indexed: false);
        ExportEntry target = package.CreateExport("Target", "Object", indexed: false);
        ImportEntry import = AddImport(package, "ImportedTarget");
        source.Data = IndexBytes(target.UIndex, import.UIndex);
        var exportNode = new BinInterpNode(0, "Export", BinaryInterpreterWPF.NodeType.StructLeafObject);
        var importNode = new BinInterpNode(4, "Import", BinaryInterpreterWPF.NodeType.ArrayLeafObject);
        var currentEditor = new List<IEntry>();
        var newWindows = new List<IEntry>();
        using var view = new BinaryTestView(source, entry => currentEditor.Add((IEntry)entry), entry => newWindows.Add((IEntry)entry));

        Navigate(view.Interpreter, exportNode, false);
        Navigate(view.Interpreter, exportNode, true);
        Navigate(view.Interpreter, importNode, true);
        CollectionAssert.AreEqual(new IEntry[] { target }, currentEditor);
        CollectionAssert.AreEqual(new IEntry[] { target, import }, newWindows);

        view.Interpreter.OpenEntryInNewWindowCommand = new RelayCommand(_ => Assert.Fail("Disabled new-window callback executed."), _ => false);
        Navigate(view.Interpreter, exportNode, true);
        view.Interpreter.OpenEntryInNewWindowCommand = null;
        Navigate(view.Interpreter, exportNode, true);
        view.Interpreter.NavigateToEntryCommand = new RelayCommand(_ => Assert.Fail("Disabled current-editor callback executed."), _ => false);
        Navigate(view.Interpreter, exportNode, false);
        Assert.IsFalse(view.Interpreter.GoToReferenceCommand.CanExecute(exportNode));
        CollectionAssert.AreEqual(new IEntry[] { target }, currentEditor);
        CollectionAssert.AreEqual(new IEntry[] { target, import }, newWindows);
    });

    [TestMethod]
    public void MaterialTextureReferencesResolveCurrentSlotsInTheirOwningPackage() => RunOnSta(() =>
    {
        using IMEPackage meshPackage = MEPackageHandler.CreateMemoryEmptyPackage("BinaryNavigationMesh.pcc", MEGame.LE3);
        ExportEntry mesh = meshPackage.CreateExport("Mesh", "Object", indexed: false);
        ExportEntry unrelated = meshPackage.CreateExport("Unrelated", "Object", indexed: false);
        mesh.Data = IndexBytes(unrelated.UIndex);
        byte[] originalMesh = mesh.Data.ToArray();
        using IMEPackage materialPackage = MEPackageHandler.CreateMemoryEmptyPackage("BinaryNavigationMaterial.pcc", MEGame.LE3);
        ExportEntry first = materialPackage.CreateExport("FirstTexture", "Texture2D", indexed: false);
        ExportEntry second = materialPackage.CreateExport("SecondTexture", "Texture2D", indexed: false);
        ExportEntry material = materialPackage.CreateExport("Material", "Material", indexed: false);
        Material materialBinary = Material.Create();
        materialBinary.SM3MaterialResource.UniformExpressionTextures = [first.UIndex];
        material.WriteBinary(materialBinary);
        var node = new BinInterpNode(-1, "Texture slot", BinaryInterpreterWPF.NodeType.StructLeafObject)
        {
            MaterialTexture = new MaterialTextureReference(material, "UniformExpressionTextures", 0, "Texture[0]"),
            UIndexValue = unrelated.UIndex,
            InlineObjectIndexValue = unrelated.UIndex.ToString()
        };
        var navigated = new List<IEntry>();
        using var view = new BinaryTestView(mesh, entry => navigated.Add((IEntry)entry));

        Assert.AreSame(first, Resolve(view.Interpreter, node));
        Navigate(view.Interpreter, node, false);
        materialBinary.SM3MaterialResource.UniformExpressionTextures = [second.UIndex];
        material.WriteBinary(materialBinary);
        Assert.AreSame(second, Resolve(view.Interpreter, node));
        Navigate(view.Interpreter, node, false);
        materialBinary.SM3MaterialResource.UniformExpressionTextures = [0];
        material.WriteBinary(materialBinary);
        Assert.IsNull(Resolve(view.Interpreter, node));
        Assert.IsFalse(view.Interpreter.GoToReferenceCommand.CanExecute(node));

        CollectionAssert.AreEqual(new IEntry[] { first, second }, navigated);
        CollectionAssert.AreEqual(originalMesh, mesh.Data);
    });

    [TestMethod]
    public void MaterialScanExposesUniformAndDependencyTextureReferencesAtTheirSerializedOffsets() => RunOnSta(() =>
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("BinaryNavigationMaterialScan.pcc", MEGame.LE3);
        ExportEntry material = package.CreateExport("Material", "Material", indexed: false);
        ExportEntry texture = package.CreateExport("Texture", "Texture2D", indexed: false);
        ImportEntry importedTexture = AddImport(package, "ImportedTexture");
        Material binary = Material.Create();
        binary.SM3MaterialResource.TextureDependencyLengthMap.Add(texture.UIndex, 37);
        binary.SM3MaterialResource.MaxTextureDependencyLength = 37;
        binary.SM3MaterialResource.UniformExpressionTextures = [texture.UIndex, importedTexture.UIndex];
        binary.SM3MaterialResource.bUsesSceneColor = true;
        binary.SM2MaterialResource.UniformExpressionTextures = [importedTexture.UIndex];
        binary.SM2MaterialResource.DummyDroppedFallbackComponents = 99;
        material.WriteBinary(binary);
        byte[] originalData = material.Data.ToArray();
        using var view = new BinaryTestView(material, _ => { });
        object[] args = [material.Data, material.propsEnd()];
        var scan = (List<ITreeItem>)typeof(BinaryInterpreterWPF)
            .GetMethod("StartMaterialScan", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view.Interpreter, args);
        BinInterpNode[] roots = scan.Cast<BinInterpNode>().ToArray();
        Assert.HasCount(2, roots);
        BinInterpNode[] nodes = roots.SelectMany(DescendantsAndSelf).ToArray();
        Assert.IsFalse(nodes.Any(node => node.Header?.Contains("Error reading binary data:") == true));
        BinInterpNode[] references = nodes.Where(node => node.IsObjectReference).ToArray();
        Assert.HasCount(4, references);
        CollectionAssert.AreEqual(new IEntry[] { texture, texture, importedTexture, importedTexture },
            references.Select(node => Resolve(view.Interpreter, node)).ToArray());
        foreach (BinInterpNode node in references)
        {
            Assert.AreEqual(4, node.Length);
            Assert.AreEqual(Resolve(view.Interpreter, node).UIndex,
                EndianReader.ToInt32(material.DataReadOnly, node.Offset, package.Endian));
        }

        BinInterpNode dependency = nodes.Single(node => node.Header?.Contains("TextureDependencyLengthMap") == true
            && node.Items.Count == 1).Items.Cast<BinInterpNode>().Single();
        Assert.AreEqual(8, dependency.Length);
        Assert.HasCount(2, dependency.Items);
        Assert.AreEqual(dependency.Offset, ((BinInterpNode)dependency.Items[0]).Offset);
        BinInterpNode length = (BinInterpNode)dependency.Items[1];
        Assert.AreEqual(dependency.Offset + 4, length.Offset);
        Assert.IsFalse(length.IsObjectReference);
        Assert.AreEqual(37, EndianReader.ToInt32(material.DataReadOnly, length.Offset, package.Endian));
        BinInterpNode tail = DescendantsAndSelf(roots[1]).Last();
        Assert.IsTrue(tail.Header.Contains("DummyDroppedFallbackComponents: 99"));
        Assert.AreEqual(material.DataSize, tail.Offset + tail.Length,
            "Adding reference children must preserve all reader positions through the final resource.");
        CollectionAssert.AreEqual(originalData, material.Data);
    });

    private static IEnumerable<BinInterpNode> DescendantsAndSelf(BinInterpNode node)
    {
        yield return node;
        foreach (BinInterpNode child in node.Items.OfType<BinInterpNode>())
        foreach (BinInterpNode descendant in DescendantsAndSelf(child))
            yield return descendant;
    }

    private static ImportEntry AddImport(IMEPackage package, string name)
    {
        var entry = new ImportEntry(package) { ObjectName = name, ClassName = "Object", PackageFile = "Engine" };
        package.AddImport(entry);
        return entry;
    }

    private static byte[] IndexBytes(params int[] values) => IndexBytes(false, values);

    private static byte[] IndexBytes(bool bigEndian, params int[] values) => values.SelectMany(value =>
    {
        byte[] bytes = BitConverter.GetBytes(value);
        if (bigEndian == BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return bytes;
    }).ToArray();

    private static IEntry Resolve(BinaryInterpreterWPF interpreter, object node) =>
        (IEntry)typeof(BinaryInterpreterWPF).GetMethod("GetReferenceEntry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(interpreter, [node]);

    private static void SetLoadedExport(BinaryInterpreterWPF interpreter, ExportEntry export) =>
        typeof(ExportLoaderControl).GetProperty(nameof(ExportLoaderControl.CurrentLoadedExport))!
            .SetValue(interpreter, export);

    private static void Navigate(BinaryInterpreterWPF interpreter, object node, bool newWindow) =>
        typeof(BinaryInterpreterWPF).GetMethod("NavigateToReference", BindingFlags.Instance | BindingFlags.NonPublic,
                null, [typeof(object), typeof(bool)], null)!
            .Invoke(interpreter, [node, newWindow]);

    private static void InvokeButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        FlushDispatcher();
    }

    private static void InitializeApplicationResources()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(BinaryInterpreterWPF).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
        DispatcherHelper.Initialize();
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

    private static void FlushDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private sealed class BinaryTestView : IDisposable
    {
        private readonly Window window;
        public BinaryInterpreterWPF Interpreter { get; }
        public TreeView Tree => (TreeView)Interpreter.FindName("BinaryInterpreter_TreeView");

        public BinaryTestView(ExportEntry export, Action<object> navigate, Action<object> newWindow = null,
            ITreeItem[] items = null)
        {
            InitializeApplicationResources();
            Interpreter = new BinaryInterpreterWPF
            {
                HideHexBox = true,
                NavigateToEntryCommand = new RelayCommand(navigate),
                OpenEntryInNewWindowCommand = newWindow is null ? null : new RelayCommand(newWindow)
            };
            // Supply the export synchronously so the fixture exercises the real control without starting an async scan.
            SetLoadedExport(Interpreter, export);
            window = new Window
            {
                Content = Interpreter, Width = 1500, Height = 700,
                ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000
            };
            window.Show();
            var root = new BinInterpNode("Binary start") { IsExpanded = true };
            root.Items.AddRange(items ?? []);
            Interpreter.TreeViewItems.Add(root);
            RefreshLayout();
        }

        public Button GoToButton(object node) => FindVisualDescendants<Button>(Interpreter)
            .Single(button => button.IsVisible && ReferenceEquals(button.Command, Interpreter.GoToReferenceCommand)
                && ReferenceEquals(button.CommandParameter, node));

        public void Select(BinInterpNode node)
        {
            TreeViewItem container = FindVisualDescendants<TreeViewItem>(Tree)
                .Single(item => ReferenceEquals(item.DataContext, node));
            container.IsSelected = true;
            RefreshLayout();
            Assert.AreSame(node, Tree.SelectedItem);
        }

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
