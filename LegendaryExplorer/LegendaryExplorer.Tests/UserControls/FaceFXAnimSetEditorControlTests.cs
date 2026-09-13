using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class FaceFXAnimSetEditorControlTests
{
    private const string DeleteAnimations = "Delete Selected Animations";
    private const string DeleteKeys = "Delete All Keys in Selected Animations";

    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    [DataRow(false, 1)]
    [DataRow(false, 2)]
    [DataRow(true, 1)]
    [DataRow(true, 2)]
    public void DeleteKeysPreservesTracksSelectionAndUnselectedKeys(bool filtered, int selectionCount)
    {
        using var fixture = new EditorFixture();
        var animations = fixture.Editor.Animations.ToArray();
        var original = fixture.ReadLine();
        fixture.SelectAnimations(filtered, selectionCount);
        fixture.Editor.ReferenceAnimation = animations[selectionCount == 1 ? 1 : 3];

        fixture.Click(DeleteKeys);

        var saved = fixture.ReadLine();
        CollectionAssert.AreEqual(original.AnimationNames, saved.AnimationNames);
        CollectionAssert.AreEqual(new[] { 1, 0, 1, selectionCount == 1 ? 2 : 0 }, saved.NumKeys);
        CollectionAssert.AreEqual(
            original.Points.Where((_, index) => index == 0 || index == 3 || (selectionCount == 1 && index >= 4)).ToArray(),
            saved.Points);
        CollectionAssert.AreEqual(animations, fixture.Editor.Animations.ToArray());
        Assert.HasCount(selectionCount, fixture.List.SelectedItems);
        Assert.AreSame(animations[1], fixture.Editor.SelectedAnimation);
        Assert.AreSame(animations[1].Points, fixture.Graph.SelectedCurve.CurvePoints);
        Assert.HasCount(0, fixture.Graph.SelectedCurve.CurvePoints);
        Assert.IsNull(fixture.Graph.SelectedPoint);
        Assert.HasCount(0, fixture.Graph.Anchors);
        Assert.HasCount(0, fixture.Graph.ComparisonCurve.CurvePoints);
        Assert.AreEqual(selectionCount == 1 ? 4f : 0.75f, fixture.Editor.SelectedLineEntry.Length);
    }

    [STATestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DeleteAnimationsRemovesEverySelectedTrackEvenWhenFiltered(bool filtered)
    {
        using var fixture = new EditorFixture();
        var original = fixture.ReadLine();
        fixture.SelectAnimations(filtered, 2);
        fixture.Editor.ReferenceAnimation = fixture.Editor.Animations[3];

        fixture.Click(DeleteAnimations);

        var saved = fixture.ReadLine();
        CollectionAssert.AreEqual(new[] { 1, 3 }, saved.AnimationNames);
        CollectionAssert.AreEqual(new[] { 1, 1 }, saved.NumKeys);
        CollectionAssert.AreEqual(new[] { original.Points[0], original.Points[3] }, saved.Points);
        CollectionAssert.AreEqual(new[] { "Unselected", "Other" }, fixture.Editor.Animations.Select(a => a.Name).ToArray());
        Assert.IsNull(fixture.Editor.ReferenceAnimation);
        Assert.IsNull(fixture.Graph.ComparisonCurve);
        Assert.IsNull(fixture.Graph.SelectedPoint);
        Assert.HasCount(0, fixture.Graph.SelectedCurve.CurvePoints);
        Assert.HasCount(0, fixture.Graph.Anchors);
    }

    [STATestMethod]
    [DataRow(DeleteKeys)]
    [DataRow(DeleteAnimations)]
    public void DeleteWithNoSelectionDoesNotChangeExport(string action)
    {
        using var fixture = new EditorFixture();
        byte[] original = fixture.Export.Data;

        fixture.Click(action);

        CollectionAssert.AreEqual(original, fixture.Export.Data);
    }

    [STATestMethod]
    [DataRow(DeleteKeys)]
    [DataRow(DeleteAnimations)]
    public void DeleteAllSelectedLeavesAnEmptyGraphAndUpdatesDuration(string action)
    {
        using var fixture = new EditorFixture();
        fixture.List.SelectAll();

        fixture.Click(action);

        var saved = fixture.ReadLine();
        Assert.HasCount(0, saved.Points);
        Assert.AreEqual(0f, fixture.Editor.SelectedLineEntry.Length);
        Assert.HasCount(action == DeleteKeys ? 4 : 0, saved.AnimationNames);
        Assert.IsTrue(saved.NumKeys.All(count => count == 0));
        Assert.HasCount(0, fixture.Graph.SelectedCurve.CurvePoints);
        Assert.HasCount(0, fixture.Graph.Anchors);
        Assert.IsNull(fixture.Graph.SelectedPoint);
    }

    private sealed class EditorFixture : IDisposable
    {
        private readonly IMEPackage package;
        public ExportEntry Export { get; }
        public FaceFXAnimSetEditorControl Editor { get; }
        public ListBox List { get; }
        public CurveGraph Graph { get; }

        public EditorFixture()
        {
            package = MEPackageHandler.CreateMemoryEmptyPackage("FaceFXDeletionTest.pcc", MEGame.LE3);
            Export = package.CreateExport("TestAnimations", "FaceFXAnimSet", indexed: false);
            var set = FaceFXAnimSet.Create(package.Game);
            set.Names = ["TestLine", "Unselected", "Selected_A", "Other", "Selected_B"];
            set.Lines =
            [
                new FaceFXLine
                {
                    NameIndex = 0,
                    AnimationNames = [1, 2, 3, 4],
                    NumKeys = [1, 2, 1, 2],
                    Points =
                    [
                        new() { time = 0.25f, weight = 0.1f, inTangent = 2, leaveTangent = 3 },
                        new() { time = 0, weight = 0 },
                        new() { time = 3, weight = 1 },
                        new() { time = 0.75f, weight = 0.2f, inTangent = 4, leaveTangent = 5 },
                        new() { time = 0, weight = 0 },
                        new() { time = 4, weight = 1 }
                    ],
                    ID = "TestLine",
                    Path = ""
                }
            ];
            Export.WriteBinary(set);
            Editor = new FaceFXAnimSetEditorControl();
            Editor.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Editor.LoadExport(Export);
            Editor.SelectedLineEntry = Editor.Lines[0];
            List = (ListBox)Editor.FindName("animationListBox");
            Graph = (CurveGraph)Editor.FindName("graph");
            Assert.HasCount(4, List.Items);
        }

        public void SelectAnimations(bool filtered, int count)
        {
            if (filtered)
            {
                Editor.AnimationFilterText = "Selected_";
            }
            List.SelectedItems.Add(Editor.Animations[1]);
            if (count == 2)
            {
                List.SelectedItems.Add(Editor.Animations[3]);
            }
            Assert.HasCount(count, List.SelectedItems);
        }

        public FaceFXLine ReadLine() => Export.GetBinaryData<FaceFXAnimSet>().Lines[0];

        public void Click(string header) => List.ContextMenu.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, header)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        public void Dispose()
        {
            Editor.Dispose();
            package.Dispose();
        }
    }
}
