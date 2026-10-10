using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.SharedUI.PeregrineTreeView;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.ExportLoaderControls.ScriptEditor.IDE;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Misc;
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
        OpeningIssuesCancelsTheSaveAndUsesTheUnsavedPackage();
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
        Assert.IsFalse(saveDialog.OpenReferenceIssues);

        var cancelDialog = new PackageReferenceSaveWarningDialog(destination, 1);
        StringAssert.Contains(cancelDialog.WarningMessage, "1 reference issue.");
        Assert.AreEqual(false, ShowModalAndRespond(cancelDialog,
            () => ClickButton((Button)cancelDialog.FindName("CancelButton"))));
        Assert.IsFalse(cancelDialog.OpenReferenceIssues);

        var openDialog = new PackageReferenceSaveWarningDialog(destination, 1);
        var openButton = (Button)openDialog.FindName("OpenIssuesButton");
        Assert.IsFalse(openButton.IsDefault);
        Assert.IsFalse(openButton.IsCancel);
        Assert.AreEqual(false, ShowModalAndRespond(openDialog, () => ClickButton(openButton)),
            "Opening reference issues must cancel the pending save.");
        Assert.IsTrue(openDialog.OpenReferenceIssues);

        var closedDialog = new PackageReferenceSaveWarningDialog(destination, 3);
        Assert.AreEqual(false, ShowModalAndRespond(closedDialog, closedDialog.Close),
            "Closing the warning must not authorize a save.");
        Assert.IsFalse(closedDialog.OpenReferenceIssues);
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

    private static void OpeningIssuesCancelsTheSaveAndUsesTheUnsavedPackage()
    {
        var previousCallback = PackageSaver.PackageSaveReferenceWarningCallback;
        var previousSynchronizationContext = SynchronizationContext.Current;
        var previousScheduler = LegendaryExplorerCoreLib.SYNCHRONIZATION_CONTEXT;
        var settingsLoaded = typeof(Settings).GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
        object previousSettingsLoaded = settingsLoaded.GetValue(null);
        bool previousLiveFiltering = Settings.PackageEditor_LiveFiltering;
        settingsLoaded.SetValue(null, false);
        Settings.PackageEditor_LiveFiltering = false;
        DispatcherHelper.Initialize();
        SyntaxInfo.LoadFromSettings();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        LegendaryExplorerCoreLib.SetSynchronizationContext(TaskScheduler.FromCurrentSynchronizationContext());

        string directory = Path.Combine(Path.GetTempPath(), $"LEX_OpenReferenceIssues_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var package = MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(directory, "UnsavedSource.pcc"), MEGame.LE3);
        var export = package.CreateExport("UnsavedObjectVariable", "SeqVar_Object", indexed: false);
        export.WriteProperty(new ObjectProperty(900000, "ObjValue"));
        export.WriteProperty(new StrProperty("Pending edit", "ObjName"));
        byte[] unsavedData = export.Data;
        string destination = Path.Combine(directory, "CancelledSaveAs.pcc");
        ReferenceCheckPackage warnedReferences = null;
        Exception responseFailure = null;
        bool responded = false;
        var responseTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        responseTimer.Tick += (_, _) =>
        {
            var warning = Application.Current.Windows.OfType<PackageReferenceSaveWarningDialog>()
                .FirstOrDefault(window => window.IsVisible);
            if (warning is null) return;
            responseTimer.Stop();
            try
            {
                Assert.AreSame(Application.Current.Dispatcher, warning.Dispatcher);
                warning.ShowActivated = false;
                warning.Left = warning.Top = -10000;
                ClickButton((Button)warning.FindName("OpenIssuesButton"));
                Assert.IsTrue(warning.OpenReferenceIssues);
                responded = true;
            }
            catch (Exception exception)
            {
                responseFailure = exception;
                warning.Close();
            }
        };
        try
        {
            PackageSaver.PackageSaveReferenceWarningCallback = (warnedPackage, warnedPath, references) =>
            {
                Assert.AreSame(package, warnedPackage);
                Assert.AreEqual(destination, warnedPath);
                warnedReferences = references;
                return PackageSaveService.ConfirmReferenceIssues(warnedPackage, warnedPath, references);
            };
            responseTimer.Start();
            var saveTask = Task.Run(() => package.TrySave(destination, compress: false));
            WaitWithDispatcher(saveTask);
            Assert.IsTrue(responded);
            Assert.IsNull(responseFailure, responseFailure?.ToString());
            Assert.IsFalse(saveTask.GetAwaiter().GetResult());
            Assert.IsFalse(File.Exists(destination), "Cancel and open issues must not write the Save As destination.");
            Assert.IsFalse(File.Exists(package.FilePath));
            Assert.IsTrue(package.IsModified, "The pending edits must stay unsaved after opening issues.");
            CollectionAssert.AreEqual(unsavedData, export.Data);

            var editor = Application.Current.Windows.OfType<PackageEditorWindow>()
                .Single(window => ReferenceEquals(window.Pcc, package));
            editor.ShowActivated = editor.ShowInTaskbar = false;
            editor.Left = editor.Top = -10000;
            var issues = editor.OwnedWindows.OfType<ReferenceIssuesDialog>().Single();
            issues.ShowActivated = issues.ShowInTaskbar = false;
            issues.Left = issues.Top = -10000;
            Assert.IsTrue(editor.IsVisible);
            Assert.IsTrue(issues.IsVisible);
            Assert.AreSame(package, editor.Pcc, "The editor must use the exact package being saved, including its unsaved edits.");
            CollectionAssert.AreEqual(warnedReferences.GetBlockingErrors().Concat(warnedReferences.GetSignificantIssues()).ToArray(),
                issues.Issues.ToArray(), "The issue window must display the report that triggered the warning.");

            var list = (ListView)issues.FindName("IssuesList");
            var propertyIssue = issues.Issues.OfType<ReferenceIssue>()
                .Single(issue => ReferenceEquals(issue.Entry, export) && issue.Location == ReferenceIssueLocation.Property);
            list.SelectedItem = propertyIssue;
            list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(issues), Environment.TickCount, Key.Enter)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            });
            var interpreter = (InterpreterExportLoader)editor.FindName("InterpreterTab_Interpreter");
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (interpreter.SelectedItem?.Property?.ValueOffset != propertyIssue.ValueOffset && DateTime.UtcNow < deadline)
                WaitWithDispatcher(Task.Delay(30));
            Assert.AreSame(export, editor.SelectedItem?.Entry);
            Assert.AreEqual(propertyIssue.ValueOffset, interpreter.SelectedItem?.Property?.ValueOffset,
                "Opening issues from a save warning must retain working property navigation.");

            Assert.AreSame(editor, PackageSaveService.OpenReferenceIssues(package, warnedReferences),
                "Opening issues again must reuse the editor already working on this package.");
            Assert.AreSame(issues, editor.OwnedWindows.OfType<ReferenceIssuesDialog>().Single(),
                "Opening issues again must reuse the existing issue window.");
            Assert.AreEqual(1, Application.Current.Windows.OfType<PackageEditorWindow>()
                .Count(window => ReferenceEquals(window.Pcc, package)));
            Assert.IsTrue(package.IsModified);
            CollectionAssert.AreEqual(unsavedData, export.Data);
        }
        finally
        {
            responseTimer.Stop();
            PackageSaver.PackageSaveReferenceWarningCallback = previousCallback;
            foreach (var warning in Application.Current.Windows.OfType<PackageReferenceSaveWarningDialog>().ToArray())
                warning.Close();
            foreach (var editor in Application.Current.Windows.OfType<PackageEditorWindow>()
                         .Where(window => ReferenceEquals(window.Pcc, package)).ToArray())
            {
                foreach (var issues in editor.OwnedWindows.OfType<ReferenceIssuesDialog>().ToArray())
                    issues.Close();
                typeof(WPFBase).GetMethod("UnLoadMEPackage", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(editor, null);
                editor.Close();
            }
            Settings.PackageEditor_LiveFiltering = previousLiveFiltering;
            settingsLoaded.SetValue(null, previousSettingsLoaded);
            LegendaryExplorerCoreLib.SetSynchronizationContext(previousScheduler);
            SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
            foreach (string file in Directory.EnumerateFiles(directory))
                File.Delete(file);
            Directory.Delete(directory);
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
