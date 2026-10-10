using System;
using System.Collections.Generic;
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
using LegendaryExplorer.UserControls.ExportLoaderControls.ScriptEditor.IDE;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.SharedUI;

[TestClass]
[DoNotParallelize]
public class PackageDuplicateSaveWarningTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void DuplicateWarningPreservesWarningOrderSaveChoicesAndUnsavedPackageIdentity()
    {
        InitializeApplicationResources();
        var previousReferenceCallback = PackageSaver.PackageSaveReferenceWarningCallback;
        var previousDuplicateCallback = PackageSaver.PackageSaveDuplicateWarningCallback;
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
        try
        {
            ModalChoicesAndWindowCloseReturnExpectedResults();
            WorkerSavesWarnAboutReferencesThenDuplicatesBeforeWriting();
            CancelAndOpenUsesTheUnsavedPackageAndReusesItsEditorAndReport();
        }
        finally
        {
            PackageSaver.PackageSaveReferenceWarningCallback = previousReferenceCallback;
            PackageSaver.PackageSaveDuplicateWarningCallback = previousDuplicateCallback;
            Settings.PackageEditor_LiveFiltering = previousLiveFiltering;
            settingsLoaded.SetValue(null, previousSettingsLoaded);
            LegendaryExplorerCoreLib.SetSynchronizationContext(previousScheduler);
            SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
        }
    }

    private static void ModalChoicesAndWindowCloseReturnExpectedResults()
    {
        const string destination = @"C:\Game\DuplicateSaveTarget.pcc";
        var saveDialog = new PackageDuplicateSaveWarningDialog(destination, 2);
        Assert.AreEqual(destination, saveDialog.DestinationPath);
        StringAssert.Contains(saveDialog.WarningMessage, "2 duplicate index issues");
        var saveButton = (Button)saveDialog.FindName("SaveAnywayButton");
        var cancelButton = (Button)saveDialog.FindName("CancelButton");
        Assert.IsFalse(saveButton.IsDefault);
        Assert.IsTrue(cancelButton.IsDefault);
        Assert.IsTrue(cancelButton.IsCancel);
        Assert.AreEqual(true, ShowModalAndRespond(saveDialog, () => ClickButton(saveButton)));
        Assert.IsFalse(saveDialog.OpenDuplicateIssues);

        var cancelDialog = new PackageDuplicateSaveWarningDialog(destination, 1);
        StringAssert.Contains(cancelDialog.WarningMessage, "1 duplicate index issue.");
        Assert.AreEqual(false, ShowModalAndRespond(cancelDialog,
            () => ClickButton((Button)cancelDialog.FindName("CancelButton"))));
        Assert.IsFalse(cancelDialog.OpenDuplicateIssues);

        var openDialog = new PackageDuplicateSaveWarningDialog(destination, 1);
        var openButton = (Button)openDialog.FindName("OpenIssuesButton");
        Assert.IsFalse(openButton.IsDefault);
        Assert.IsFalse(openButton.IsCancel);
        Assert.AreEqual(false, ShowModalAndRespond(openDialog, () => ClickButton(openButton)));
        Assert.IsTrue(openDialog.OpenDuplicateIssues);

        var closedDialog = new PackageDuplicateSaveWarningDialog(destination, 3);
        Assert.AreEqual(false, ShowModalAndRespond(closedDialog, closedDialog.Close));
        Assert.IsFalse(closedDialog.OpenDuplicateIssues);
    }

    private static void WorkerSavesWarnAboutReferencesThenDuplicatesBeforeWriting()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"LEX_DuplicateWarningOrder_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var (saveReferences, saveDuplicates) in new[] { (false, false), (true, false), (true, true) })
            {
                string label = $"{saveReferences}_{saveDuplicates}";
                using var package = MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(directory, $"Source_{label}.pcc"), MEGame.LE3);
                var first = package.CreateExport("Twin", "SeqVar_Object", indexed: false);
                var second = package.CreateExport("Twin", "SeqVar_Object", indexed: false);
                second.WriteProperty(new ObjectProperty(900000, "ObjValue"));
                byte[] originalData = second.Data;
                string destination = Path.Combine(directory, $"Destination_{label}.pcc");
                byte[] existingFile = [10, 20, 30, 40, 50];
                File.WriteAllBytes(destination, existingFile);
                var warningOrder = new List<string>();
                PackageSaver.PackageSaveReferenceWarningCallback = PackageSaveService.ConfirmReferenceIssues;
                PackageSaver.PackageSaveDuplicateWarningCallback = PackageSaveService.ConfirmDuplicateIssues;

                bool saved = RunWorkerSaveWithResponses(() => package.TrySave(destination, compress: false), warning =>
                {
                    Assert.AreSame(Application.Current.Dispatcher, warning.Dispatcher);
                    CollectionAssert.AreEqual(existingFile, File.ReadAllBytes(destination),
                        "The existing destination must remain untouched until every warning has been accepted.");
                    switch (warning)
                    {
                        case PackageReferenceSaveWarningDialog referenceWarning:
                            Assert.HasCount(0, warningOrder, "The reference warning must be first.");
                            warningOrder.Add("reference");
                            Assert.AreEqual(destination, referenceWarning.DestinationPath);
                            ClickButton((Button)warning.FindName(saveReferences ? "SaveAnywayButton" : "CancelButton"));
                            break;
                        case PackageDuplicateSaveWarningDialog duplicateWarning:
                            CollectionAssert.AreEqual(new[] { "reference" }, warningOrder);
                            warningOrder.Add("duplicate");
                            Assert.AreEqual(destination, duplicateWarning.DestinationPath);
                            ClickButton((Button)warning.FindName(saveDuplicates ? "SaveAnywayButton" : "CancelButton"));
                            break;
                    }
                });

                Assert.AreEqual(saveReferences && saveDuplicates, saved);
                CollectionAssert.AreEqual(saveReferences ? new[] { "reference", "duplicate" } : new[] { "reference" }, warningOrder);
                Assert.AreEqual(first.ObjectName, second.ObjectName, "Saving anyway must not silently repair duplicate names.");
                CollectionAssert.AreEqual(originalData, second.Data);
                if (saved)
                {
                    Assert.IsTrue(new FileInfo(destination).Length > existingFile.Length);
                    using var reopened = MEPackageHandler.OpenMEPackage(destination, forceLoadFromDisk: true);
                    Assert.HasCount(1, EntryChecker.CheckForDuplicateIndices(reopened));
                    Assert.AreEqual(900000, reopened.GetUExport(second.UIndex).GetProperty<ObjectProperty>("ObjValue").Value);
                }
                else
                {
                    CollectionAssert.AreEqual(existingFile, File.ReadAllBytes(destination));
                    Assert.IsTrue(package.IsModified);
                }
            }
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    private static void CancelAndOpenUsesTheUnsavedPackageAndReusesItsEditorAndReport()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"LEX_OpenDuplicateIssues_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var package = MEPackageHandler.CreateMemoryEmptyPackage(Path.Combine(directory, "UnsavedSource.pcc"), MEGame.LE3);
        package.CreateExport("Twin", "SeqVar_Object", indexed: false);
        var second = package.CreateExport("Twin", "SeqVar_Object", indexed: false);
        second.WriteProperty(new StrProperty("Pending unsaved caption", "ObjName"));
        byte[] unsavedData = second.Data;
        string destination = Path.Combine(directory, "CancelledSaveAs.pcc");
        IReadOnlyList<EntryStringPair> warnedIssues = null;
        try
        {
            PackageSaver.PackageSaveReferenceWarningCallback = (_, _, _) =>
                throw new AssertFailedException("This fixture has duplicate names but no reference issues.");
            PackageSaver.PackageSaveDuplicateWarningCallback = (warnedPackage, warnedPath, issues) =>
            {
                Assert.AreSame(package, warnedPackage);
                Assert.AreEqual(destination, warnedPath);
                warnedIssues = issues;
                return PackageSaveService.ConfirmDuplicateIssues(warnedPackage, warnedPath, issues);
            };
            bool responded = false;
            bool saved = RunWorkerSaveWithResponses(() => package.TrySave(destination, compress: false), warning =>
            {
                Assert.IsInstanceOfType<PackageDuplicateSaveWarningDialog>(warning);
                var duplicateWarning = (PackageDuplicateSaveWarningDialog)warning;
                Assert.AreSame(Application.Current.Dispatcher, warning.Dispatcher);
                ClickButton((Button)warning.FindName("OpenIssuesButton"));
                Assert.IsTrue(duplicateWarning.OpenDuplicateIssues);
                responded = true;
            });
            Assert.IsTrue(responded);
            Assert.IsFalse(saved);
            Assert.IsFalse(File.Exists(destination));
            Assert.IsFalse(File.Exists(package.FilePath));
            Assert.IsTrue(package.IsModified);
            CollectionAssert.AreEqual(unsavedData, second.Data);

            var editor = Application.Current.Windows.OfType<PackageEditorWindow>()
                .Single(window => ReferenceEquals(window.Pcc, package));
            MoveOffscreen(editor);
            var issueDialog = editor.OwnedWindows.OfType<DuplicateIssuesDialog>().Single();
            MoveOffscreen(issueDialog);
            Assert.IsTrue(editor.IsVisible);
            Assert.IsTrue(issueDialog.IsVisible);
            Assert.AreSame(package, editor.Pcc);
            CollectionAssert.AreEqual(warnedIssues.ToArray(), issueDialog.Issues.ToArray(),
                "Opening the list must reuse the report objects that triggered the warning.");

            var list = (ListView)issueDialog.FindName("IssuesList");
            list.SelectedItem = issueDialog.Issues.Single();
            list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(issueDialog), Environment.TickCount, Key.Enter)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            });
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!ReferenceEquals(editor.SelectedItem?.Entry, second) && DateTime.UtcNow < deadline)
                WaitWithDispatcher(Task.Delay(30));
            Assert.AreSame(second, editor.SelectedItem?.Entry, "The opened duplicate list must retain working entry navigation.");

            Assert.AreSame(editor, PackageSaveService.OpenDuplicateIssues(package, warnedIssues));
            Assert.AreSame(issueDialog, editor.OwnedWindows.OfType<DuplicateIssuesDialog>().Single());
            Assert.AreEqual(1, Application.Current.Windows.OfType<PackageEditorWindow>()
                .Count(window => ReferenceEquals(window.Pcc, package)));

            second.ObjectName = "ManuallyRenamed";
            Assert.HasCount(0, EntryChecker.CheckForDuplicateIndices(package));
            EntryStringPair[] suppliedReport = [new(second, "Supplied warning report")];
            Assert.AreSame(editor, PackageSaveService.OpenDuplicateIssues(package, suppliedReport));
            Assert.AreSame(issueDialog, editor.OwnedWindows.OfType<DuplicateIssuesDialog>().Single());
            CollectionAssert.AreEqual(suppliedReport, issueDialog.Issues.ToArray(),
                "An explicitly supplied report must be shown without rerunning the duplicate checker.");
            Assert.IsTrue(package.IsModified);
            CollectionAssert.AreEqual(unsavedData, second.Data);
        }
        finally
        {
            foreach (var warning in Application.Current.Windows.OfType<PackageDuplicateSaveWarningDialog>().ToArray())
                warning.Close();
            foreach (var editor in Application.Current.Windows.OfType<PackageEditorWindow>()
                         .Where(window => ReferenceEquals(window.Pcc, package)).ToArray())
            {
                foreach (var dialog in editor.OwnedWindows.OfType<DuplicateIssuesDialog>().ToArray())
                    dialog.Close();
                typeof(WPFBase).GetMethod("UnLoadMEPackage", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(editor, null);
                editor.Close();
            }
            DeleteTestDirectory(directory);
        }
    }

    private static bool RunWorkerSaveWithResponses(Func<bool> save, Action<Window> respond)
    {
        Exception responseFailure = null;
        var responded = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(10) };
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        timer.Tick += (_, _) =>
        {
            foreach (var warning in Application.Current.Windows.OfType<Window>()
                         .Where(window => window.IsVisible && (window is PackageReferenceSaveWarningDialog or PackageDuplicateSaveWarningDialog)).ToArray())
            {
                if (!responded.Add(warning)) continue;
                MoveOffscreen(warning);
                try
                {
                    respond(warning);
                    if (warning.IsVisible)
                        throw new AssertFailedException("The warning response did not close its dialog.");
                }
                catch (Exception exception)
                {
                    responseFailure ??= exception;
                    warning.Close();
                }
            }
            if (DateTime.UtcNow < deadline) return;
            foreach (var warning in Application.Current.Windows.OfType<Window>()
                         .Where(window => window is PackageReferenceSaveWarningDialog or PackageDuplicateSaveWarningDialog).ToArray())
                warning.Close();
        };
        timer.Start();
        try
        {
            var task = Task.Run(save);
            WaitWithDispatcher(task);
            Assert.IsNull(responseFailure, responseFailure?.ToString());
            return task.GetAwaiter().GetResult();
        }
        finally
        {
            timer.Stop();
        }
    }

    private static bool? ShowModalAndRespond(Window dialog, Action respond)
    {
        MoveOffscreen(dialog);
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
            Assert.IsNull(responseFailure, responseFailure?.ToString());
            return result;
        }
        finally
        {
            dialog.Close();
        }
    }

    private static void MoveOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowActivated = window.ShowInTaskbar = false;
        window.Left = window.Top = -10000;
    }

    private static void ClickButton(Button button) => typeof(Button)
        .GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);

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
            Assert.IsTrue(task.IsCompleted, "The save operation did not finish while the UI dispatcher was running.");
        }
        finally
        {
            timer.Stop();
        }
    }

    private static void DeleteTestDirectory(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory))
            File.Delete(file);
        Directory.Delete(directory);
    }

    private static void InitializeApplicationResources()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(PackageDuplicateSaveWarningDialog).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
    }
}
