using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.SharedUI;

[TestClass]
[DoNotParallelize]
public class PackageReferenceSaveWarningTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void ReferenceWarningSupportsSavingCancellingAndWorkerThreadSaves()
    {
        InitializeApplicationResources();
        ModalButtonsAndWindowCloseReturnTheExpectedChoice();
        SaveServicesPropagateReferenceWarningChoices();
        WorkerThreadWarningRunsItsDialogOnTheApplicationDispatcher();
    }

    private static void ModalButtonsAndWindowCloseReturnTheExpectedChoice()
    {
        const string destination = @"C:\Game\SaveTarget.pcc";
        var saveDialog = new PackageReferenceSaveWarningDialog(destination, 2);
        Assert.AreEqual(destination, saveDialog.DestinationPath);
        StringAssert.Contains(saveDialog.WarningMessage, "2 reference issues");
        var saveButton = (Button)saveDialog.FindName("SaveAnywayButton");
        var cancelButton = (Button)saveDialog.FindName("CancelButton");
        Assert.IsFalse(saveButton.IsDefault);
        Assert.IsTrue(cancelButton.IsDefault);
        Assert.IsTrue(cancelButton.IsCancel);
        Assert.AreEqual(true, ShowModalAndRespond(saveDialog, () => ClickButton(saveButton)));

        var cancelDialog = new PackageReferenceSaveWarningDialog(destination, 1);
        StringAssert.Contains(cancelDialog.WarningMessage, "1 reference issue.");
        Assert.AreEqual(false, ShowModalAndRespond(cancelDialog,
            () => ClickButton((Button)cancelDialog.FindName("CancelButton"))));

        var closedDialog = new PackageReferenceSaveWarningDialog(destination, 3);
        Assert.AreEqual(false, ShowModalAndRespond(closedDialog, closedDialog.Close),
            "Closing the warning must not authorize a save.");
    }

    private static bool? ShowModalAndRespond(Window dialog, Action respond)
    {
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.ShowActivated = dialog.ShowInTaskbar = false;
        dialog.Left = dialog.Top = -10000;
        Exception responseFailure = null;
        dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                respond();
                if (dialog.IsVisible)
                    throw new AssertFailedException("The dialog action did not close the warning.");
            }
            catch (Exception exception)
            {
                responseFailure = exception;
                dialog.Close();
            }
        }));
        try
        {
            bool? result = dialog.ShowDialog();
            if (responseFailure is not null)
                throw new AssertFailedException($"Could not respond to the save warning: {responseFailure}");
            return result;
        }
        finally
        {
            dialog.Close();
        }
    }

    private static void ClickButton(Button button) => typeof(Button)
        .GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(button, null);

    private static void SaveServicesPropagateReferenceWarningChoices()
    {
        var originalCallback = PackageSaver.PackageSaveReferenceWarningCallback;
        string directory = Path.Combine(Path.GetTempPath(), $"LEX_ReferenceSaveServices_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (bool confirm in new[] { false, true })
            {
                string sourcePath = Path.Combine(directory, $"Source{confirm}.pcc");
                using var package = MEPackageHandler.CreateMemoryEmptyPackage(sourcePath, MEGame.LE3);
                var export = package.CreateExport("Source", "SeqVar_Object", indexed: false);
                export.WriteProperty(new ObjectProperty(900000, "ObjValue"));
                string destination = Path.Combine(directory, $"Sync{confirm}.pcc");
                int warnings = 0;
                PackageSaver.PackageSaveReferenceWarningCallback = (warnedPackage, warnedPath, _) =>
                {
                    warnings++;
                    Assert.AreSame(package, warnedPackage);
                    Assert.AreEqual(destination, warnedPath);
                    return confirm;
                };

                bool saved = package.SaveWithMountWarning(null, destination, compress: false,
                    choose: _ => throw new AssertFailedException("An explicit Save As destination must skip the mount warning."));

                Assert.AreEqual(confirm, saved);
                Assert.AreEqual(confirm, File.Exists(destination));
                Assert.AreEqual(1, warnings);
                Assert.AreEqual(900000, export.GetProperty<ObjectProperty>("ObjValue").Value);

                warnings = 0;
                destination = Path.Combine(directory, $"Async{confirm}.pcc");
                var saveTask = package.SaveWithMountWarningAsync(null, destination, compress: false,
                    choose: _ => throw new AssertFailedException("An explicit Save As destination must skip the mount warning."));
                WaitWithDispatcher(saveTask);

                Assert.AreEqual(confirm, saveTask.GetAwaiter().GetResult());
                Assert.AreEqual(confirm, File.Exists(destination));
                Assert.AreEqual(1, warnings);
                Assert.AreEqual(900000, export.GetProperty<ObjectProperty>("ObjValue").Value);
            }
        }
        finally
        {
            PackageSaver.PackageSaveReferenceWarningCallback = originalCallback;
            foreach (string file in Directory.EnumerateFiles(directory))
                File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static void WorkerThreadWarningRunsItsDialogOnTheApplicationDispatcher()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("WorkerReferenceWarning.pcc", MEGame.LE3);
        var references = new ReferenceCheckPackage();
        references.AddSignificantIssue("Invalid object reference");
        const string destination = @"C:\Game\WorkerSave.pcc";
        Exception responseFailure = null;
        bool responded = false;
        var responseTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        responseTimer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<PackageReferenceSaveWarningDialog>()
                .FirstOrDefault(window => window.IsVisible);
            if (dialog is null) return;
            responseTimer.Stop();
            try
            {
                Assert.AreSame(Application.Current.Dispatcher, dialog.Dispatcher);
                Assert.AreEqual(destination, dialog.DestinationPath);
                StringAssert.Contains(dialog.WarningMessage, "1 reference issue.");
                dialog.ShowActivated = false;
                dialog.Left = dialog.Top = -10000;
                responded = true;
                ClickButton((Button)dialog.FindName("SaveAnywayButton"));
                if (dialog.IsVisible)
                    throw new AssertFailedException("Save anyway did not close the warning.");
            }
            catch (Exception exception)
            {
                responseFailure = exception;
                dialog.Close();
            }
        };
        responseTimer.Start();
        try
        {
            var task = Task.Run(() => PackageSaveService.ConfirmReferenceIssues(package, destination, references));
            WaitWithDispatcher(task);
            Assert.IsTrue(responded);
            Assert.IsNull(responseFailure, responseFailure?.ToString());
            Assert.IsTrue(task.GetAwaiter().GetResult());
        }
        finally
        {
            responseTimer.Stop();
            foreach (var dialog in Application.Current.Windows.OfType<PackageReferenceSaveWarningDialog>().ToArray())
                dialog.Close();
        }
    }

    private static void WaitWithDispatcher(Task task)
    {
        var frame = new DispatcherFrame();
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (task.IsCompleted || DateTime.UtcNow >= deadline)
                frame.Continue = false;
        };
        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
            Assert.IsTrue(task.IsCompleted, "The save operation did not complete while the UI dispatcher was running.");
        }
        finally
        {
            timer.Stop();
        }
    }

    private static void InitializeApplicationResources()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(PackageReferenceSaveWarningDialog).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
    }
}
