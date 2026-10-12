using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using LegendaryExplorer.Misc.AppSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.MainWindow;

[TestClass]
[DoNotParallelize]
public class ToolTaskbarPinTests
{
    [STATestMethod]
    public void PinsPersistIndependentlyFromFavoritesAndNotifyOnce()
    {
        using var scope = new TestToolSet();
        scope.First.IsFavorited = true;
        int notifications = 0;
        ToolSet.TaskbarPinsChanged += (_, _) => notifications++;

        scope.First.IsPinnedToTaskbar = true;

        Assert.AreEqual("First", Settings.MainWindow_TaskbarPins);
        Assert.AreEqual("First", Settings.MainWindow_Favorites);
        Assert.IsTrue(scope.First.IsFavorited);
        Assert.AreEqual(1, notifications);
        CollectionAssert.AreEqual(new[] { scope.First }, ToolSet.PinnedTools.ToArray());

        scope.First.IsFavorited = false;
        Assert.IsTrue(scope.First.IsPinnedToTaskbar);
        Assert.AreEqual("First", Settings.MainWindow_TaskbarPins);
        Assert.AreEqual(1, notifications, "Changing a favorite must not refresh taskbar pins.");

        scope.First.IsPinnedToTaskbar = false;
        Assert.AreEqual("", Settings.MainWindow_TaskbarPins);
        Assert.AreEqual(0, ToolSet.PinnedTools.Count);
        Assert.AreEqual(2, notifications);
    }

    [STATestMethod]
    public void RePinningAppendsTheToolAndExternalSettingsRestoreSavedOrder()
    {
        using var scope = new TestToolSet();
        scope.Second.IsPinnedToTaskbar = true;
        scope.First.IsPinnedToTaskbar = true;
        Assert.AreEqual("Second;First", Settings.MainWindow_TaskbarPins);

        scope.Second.IsPinnedToTaskbar = false;
        scope.Second.IsPinnedToTaskbar = true;
        Assert.AreEqual("First;Second", Settings.MainWindow_TaskbarPins);
        CollectionAssert.AreEqual(new[] { scope.First, scope.Second }, ToolSet.PinnedTools.ToArray());

        Settings.MainWindow_TaskbarPins = "Second;First";
        CollectionAssert.AreEqual(new[] { scope.Second, scope.First }, ToolSet.PinnedTools.ToArray());
    }

    [STATestMethod]
    public void LoadingSkipsDuplicateUnknownAndUnavailableToolsWithoutRewritingSettings()
    {
        using var scope = new TestToolSet();
        int notifications = 0;
        ToolSet.TaskbarPinsChanged += (_, _) => notifications++;
        const string savedPins = "Second;Missing;Second;;Unavailable;First";

        Settings.MainWindow_TaskbarPins = savedPins;

        CollectionAssert.AreEqual(new[] { scope.Second, scope.First }, ToolSet.PinnedTools.ToArray());
        Assert.IsTrue(scope.First.IsPinnedToTaskbar);
        Assert.IsTrue(scope.Second.IsPinnedToTaskbar);
        Assert.IsFalse(scope.Unavailable.IsPinnedToTaskbar);
        Assert.AreEqual(savedPins, Settings.MainWindow_TaskbarPins,
            "Loading each dependency property must not save a partial pin list.");
        Assert.AreEqual(1, notifications);

        Settings.MainWindow_TaskbarPins = null;
        Assert.AreEqual(0, ToolSet.PinnedTools.Count);
        Assert.IsFalse(scope.First.IsPinnedToTaskbar);
        Assert.IsFalse(scope.Second.IsPinnedToTaskbar);
        Assert.AreEqual(2, notifications);
    }

    [STATestMethod]
    public void MovingPinsEarlierLaterAndToTheEndSavesAndNotifiesOncePerMove()
    {
        using var scope = new TestToolSet();
        Settings.MainWindow_TaskbarPins = "First;Second;Third";
        int notifications = 0;
        int settingChanges = 0;
        ToolSet.TaskbarPinsChanged += (_, _) => notifications++;
        Settings.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Settings.MainWindow_TaskbarPins)) settingChanges++;
        };

        Assert.IsTrue(ToolSet.MovePinnedTool(scope.Third, 0));
        CollectionAssert.AreEqual(new[] { scope.Third, scope.First, scope.Second }, ToolSet.PinnedTools.ToArray());
        Assert.AreEqual("Third;First;Second", Settings.MainWindow_TaskbarPins);
        Assert.AreEqual(1, notifications);
        Assert.AreEqual(1, settingChanges);

        Assert.IsTrue(ToolSet.MovePinnedTool(scope.Third, 2),
            "Moving later must adjust the original gap after removing the dragged tool.");
        CollectionAssert.AreEqual(new[] { scope.First, scope.Third, scope.Second }, ToolSet.PinnedTools.ToArray());
        Assert.AreEqual("First;Third;Second", Settings.MainWindow_TaskbarPins);
        Assert.AreEqual(2, notifications);
        Assert.AreEqual(2, settingChanges);

        Assert.IsTrue(ToolSet.MovePinnedTool(scope.Third, ToolSet.PinnedTools.Count));
        CollectionAssert.AreEqual(new[] { scope.First, scope.Second, scope.Third }, ToolSet.PinnedTools.ToArray());
        Assert.AreEqual("First;Second;Third", Settings.MainWindow_TaskbarPins);
        Assert.AreEqual(3, notifications);
        Assert.AreEqual(3, settingChanges);
        Assert.IsTrue(scope.First.IsPinnedToTaskbar && scope.Second.IsPinnedToTaskbar && scope.Third.IsPinnedToTaskbar);
    }

    [STATestMethod]
    public void DroppingInAdjacentGapsOrMovingInvalidToolsDoesNotSaveOrNotify()
    {
        using var scope = new TestToolSet();
        Settings.MainWindow_TaskbarPins = "First;Second";
        int notifications = 0;
        int settingChanges = 0;
        ToolSet.TaskbarPinsChanged += (_, _) => notifications++;
        Settings.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Settings.MainWindow_TaskbarPins)) settingChanges++;
        };

        Assert.IsFalse(ToolSet.MovePinnedTool(scope.First, 0));
        Assert.IsFalse(ToolSet.MovePinnedTool(scope.First, 1));
        Assert.IsFalse(ToolSet.MovePinnedTool(scope.Second, 1));
        Assert.IsFalse(ToolSet.MovePinnedTool(scope.Second, 2));
        Assert.IsFalse(ToolSet.MovePinnedTool(scope.Third, 0));
        Assert.IsFalse(ToolSet.MovePinnedTool(scope.Unavailable, 0));
        Assert.IsFalse(ToolSet.MovePinnedTool(null, 0));
        Assert.IsFalse(ToolSet.MovePinnedTool(new Tool { name = "First", open = () => { } }, 0));

        CollectionAssert.AreEqual(new[] { scope.First, scope.Second }, ToolSet.PinnedTools.ToArray());
        Assert.AreEqual("First;Second", Settings.MainWindow_TaskbarPins);
        Assert.AreEqual(0, notifications);
        Assert.AreEqual(0, settingChanges);
    }

    [STATestMethod]
    public void ReorderedPinsReloadInTheSavedOrderAndOutOfRangeGapsAreClamped()
    {
        using var scope = new TestToolSet();
        Settings.MainWindow_TaskbarPins = "First;Second;Third";
        Assert.IsTrue(ToolSet.MovePinnedTool(scope.Second, int.MinValue));
        Assert.AreEqual("Second;First;Third", Settings.MainWindow_TaskbarPins);
        Assert.IsTrue(ToolSet.MovePinnedTool(scope.First, int.MaxValue));
        string savedOrder = Settings.MainWindow_TaskbarPins;
        Assert.AreEqual("Second;Third;First", savedOrder);

        Settings.MainWindow_TaskbarPins = "";
        Assert.AreEqual(0, ToolSet.PinnedTools.Count);
        Settings.MainWindow_TaskbarPins = savedOrder;

        CollectionAssert.AreEqual(new[] { scope.Second, scope.Third, scope.First }, ToolSet.PinnedTools.ToArray());
        Assert.IsTrue(scope.First.IsPinnedToTaskbar && scope.Second.IsPinnedToTaskbar && scope.Third.IsPinnedToTaskbar);
    }

    private sealed class TestToolSet : IDisposable
    {
        private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
        private readonly Dictionary<FieldInfo, object> previousFields = new();
        private readonly List<Tool> taskbarPins;
        private readonly Tool[] previousPins;

        public Tool First { get; } = new() { name = "First", open = () => { } };
        public Tool Second { get; } = new() { name = "Second", open = () => { } };
        public Tool Third { get; } = new() { name = "Third", open = () => { } };
        public Tool Unavailable { get; } = new() { name = "Unavailable" };

        public TestToolSet()
        {
            RememberAndSet(typeof(Settings), "Loaded", false); // Never save the user's settings to disk.
            RememberAndSet(typeof(Settings), "_mainwindow_favorites", "");
            RememberAndSet(typeof(Settings), "_mainwindow_taskbarpins", "");
            RememberAndSet(typeof(Settings), nameof(Settings.StaticPropertyChanged), null);
            RememberAndSet(typeof(ToolSet), nameof(ToolSet.FavoritesChanged), null);
            RememberAndSet(typeof(ToolSet), nameof(ToolSet.TaskbarPinsChanged), null);
            RememberAndSet(typeof(ToolSet), "synchronizingFavorites", false);
            RememberAndSet(typeof(ToolSet), "synchronizingTaskbarPins", false);
            RememberAndSet(typeof(ToolSet), "items", new HashSet<Tool> { First, Second, Third, Unavailable });

            taskbarPins = (List<Tool>)typeof(ToolSet).GetField("taskbarPins", StaticPrivate)!.GetValue(null)!;
            previousPins = taskbarPins.ToArray();
            taskbarPins.Clear();

            Settings.StaticPropertyChanged += (PropertyChangedEventHandler)typeof(ToolSet)
                .GetMethod("Settings_StaticPropertyChanged", StaticPrivate)!
                .CreateDelegate(typeof(PropertyChangedEventHandler));
        }

        private void RememberAndSet(Type owner, string name, object value)
        {
            FieldInfo field = owner.GetField(name, StaticPrivate)!;
            previousFields.Add(field, field.GetValue(null));
            field.SetValue(null, value);
        }

        public void Dispose()
        {
            taskbarPins.Clear();
            taskbarPins.AddRange(previousPins);
            foreach ((FieldInfo field, object value) in previousFields)
            {
                field.SetValue(null, value);
            }
        }
    }
}
