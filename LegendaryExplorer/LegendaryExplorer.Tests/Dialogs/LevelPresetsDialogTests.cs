using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorerCore.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Dialogs;

[TestClass]
public class LevelPresetsDialogTests
{
    [STATestMethod]
    public void CameraSelectionIsOptionalAndClearsWhenFilteredOrParentChanges()
    {
        using var fixture = new DialogFixture();
        LevelPresetsDialog dialog = fixture.Dialog;
        Assert.IsNull(dialog.SelectedCameraPreset);
        Assert.IsTrue(Find<Button>(dialog, "OpenPresetButton").IsEnabled);

        dialog.ShowCameraLocations();
        Find<ListBox>(dialog, "CameraPresetsList").SelectedItem = fixture.First.CameraPresets[0];
        FlushDispatcher();
        Assert.AreEqual("Balcony", dialog.SelectedCameraPreset?.Name);

        dialog.CameraSearchText = "  BALCONY  ";
        FlushDispatcher();
        Assert.HasCount(1, dialog.CameraPresetView.Cast<LevelCameraPreset>());
        Assert.AreEqual("Balcony", dialog.SelectedCameraPreset?.Name);

        dialog.CameraSearchText = "Doorway";
        FlushDispatcher();
        Assert.IsNull(dialog.SelectedCameraPreset, "A hidden camera must never remain the opening location.");
        dialog.CameraSearchText = "";
        FlushDispatcher();
        Assert.IsNull(dialog.SelectedCameraPreset, "Search must not implicitly select a camera.");

        dialog.SelectedCameraPreset = fixture.First.CameraPresets[0];
        Click(dialog, "DefaultCameraButton");
        Assert.IsNull(dialog.SelectedCameraPreset);
        Assert.IsTrue(Find<Button>(dialog, "OpenPresetButton").IsEnabled);

        dialog.SelectedCameraPreset = fixture.First.CameraPresets[0];
        dialog.SelectedPreset = fixture.Second;
        FlushDispatcher();
        Assert.IsNull(dialog.SelectedCameraPreset, "Camera selection belongs to its parent level preset.");
    }

    [STATestMethod]
    public void NewCameraUsesCurrentViewAndRequiresValidSavedCoordinatesBeforeOpening()
    {
        var current = new LevelCameraPreset { X = 10, Y = -20, Z = 30, Roll = 15, Pitch = -45, Yaw = 120 };
        using var fixture = new DialogFixture(current);
        LevelPresetsDialog dialog = fixture.Dialog;
        dialog.ShowCameraLocations();
        Click(dialog, "NewCameraButton");
        dialog.CameraName = "Courtyard";
        Assert.IsFalse(Find<Button>(dialog, "OpenPresetButton").IsEnabled);
        Assert.IsTrue(Find<Button>(dialog, "SaveCameraButton").IsEnabled);

        dialog.CameraX = "invalid";
        Assert.IsFalse(Find<Button>(dialog, "SaveCameraButton").IsEnabled);
        Assert.HasCount(2, fixture.Store.Presets.Single(preset => preset.Id == fixture.First.Id).CameraPresets);
        Click(dialog, "UseCurrentCameraButton");
        Assert.IsTrue(Find<Button>(dialog, "SaveCameraButton").IsEnabled);
        Click(dialog, "SaveCameraButton");

        LevelCameraPreset camera = new LevelPresetStore(fixture.StorePath).Presets
            .Single(preset => preset.Id == fixture.First.Id).CameraPresets.Single(preset => preset.Name == "Courtyard");
        Assert.AreEqual(current with { Id = camera.Id, Name = "Courtyard" }, camera);
        Assert.AreEqual(camera.Id, dialog.SelectedCameraPreset?.Id);
        Assert.IsTrue(Find<Button>(dialog, "OpenPresetButton").IsEnabled);

        dialog.CameraPitch = "NaN";
        Assert.IsFalse(Find<Button>(dialog, "OpenPresetButton").IsEnabled);
        Assert.IsFalse(Find<Button>(dialog, "SaveCameraButton").IsEnabled);
        Click(dialog, "DefaultCameraButton");
        Assert.IsNull(dialog.SelectedCameraPreset);
        Assert.IsTrue(Find<Button>(dialog, "OpenPresetButton").IsEnabled);
    }

    [STATestMethod]
    public void SavingParentNamePreservesSelectedCameraAndItsUnfinishedEdits()
    {
        using var fixture = new DialogFixture();
        LevelPresetsDialog dialog = fixture.Dialog;
        dialog.ShowCameraLocations();
        dialog.SelectedCameraPreset = fixture.First.CameraPresets[0];
        dialog.CameraSearchText = "Balcony";
        dialog.CameraX = "12345";
        dialog.EditorName = "Renamed levels";
        Click(dialog, "SaveNameButton");

        Assert.AreEqual("Renamed levels", dialog.SelectedPreset.Name);
        Assert.AreEqual(fixture.First.CameraPresets[0].Id, dialog.SelectedCameraPreset?.Id);
        Assert.AreEqual("Balcony", dialog.CameraSearchText);
        Assert.AreEqual("12345", dialog.CameraX);
        Assert.IsFalse(Find<Button>(dialog, "OpenPresetButton").IsEnabled);
        Click(dialog, "SaveCameraButton");
        Assert.AreEqual(12345f, dialog.SelectedCameraPreset.X);
        Assert.IsTrue(Find<Button>(dialog, "OpenPresetButton").IsEnabled);
        Assert.HasCount(2, new LevelPresetStore(fixture.StorePath).Presets
            .Single(preset => preset.Id == fixture.First.Id).CameraPresets);
    }

    private static T Find<T>(LevelPresetsDialog dialog, string name) where T : FrameworkElement
        => (T)dialog.FindName(name);

    private static void Click(LevelPresetsDialog dialog, string name)
    {
        Button button = Find<Button>(dialog, name);
        Assert.IsTrue(button.IsEnabled, $"{name} should be available.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        FlushDispatcher();
    }

    private static void FlushDispatcher() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private sealed class DialogFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"LEXCameraDialogTests-{Guid.NewGuid():N}");
        public string StorePath => Path.Combine(directory, "LevelPresets.json");
        public LevelPresetStore Store { get; }
        public LevelPresetsDialog Dialog { get; }
        public LevelPreset First { get; }
        public LevelPreset Second { get; }

        public DialogFixture(LevelCameraPreset current = null)
        {
            typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, typeof(App).Assembly);
            Store = new LevelPresetStore(StorePath);
            First = Store.Save(new LevelPreset
            {
                Name = "Citadel", Game = MEGame.LE3, FilePaths = [Path.Combine(directory, "Citadel.pcc")],
                CameraPresets = [new LevelCameraPreset { Name = "Balcony" }, new LevelCameraPreset { Name = "Doorway" }]
            });
            Second = Store.Save(new LevelPreset
            {
                Name = "Normandy", Game = MEGame.LE3, FilePaths = [Path.Combine(directory, "Normandy.pcc")],
                CameraPresets = [new LevelCameraPreset { Name = "Balcony" }]
            });
            Dialog = new LevelPresetsDialog(store: Store, currentCamera: current)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
                Resources = (ResourceDictionary)Application.LoadComponent(
                    new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative))
            };
            Dialog.Show();
            FlushDispatcher();
        }

        public void Dispose()
        {
            Dialog.Close();
            Directory.Delete(directory, recursive: true);
        }
    }
}
