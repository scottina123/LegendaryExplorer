using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.UserControls.ExportLoaderControls.ScriptEditor;
using LegendaryExplorer.UserControls.SharedToolControls;
using MEGame = LegendaryExplorerCore.Packages.MEGame;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace LegendaryExplorer.Tests.Misc;

[TestClass]
[DoNotParallelize]
public class ScottinaPresetTests
{
    private static readonly FieldInfo LoadedField = typeof(Settings)
        .GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
    private Dictionary<PropertyInfo, object> _originalSettings;
    private object _originalLoaded;

    [TestInitialize]
    public void Initialize()
    {
        _originalLoaded = LoadedField.GetValue(null);
        LoadedField.SetValue(null, false); // Never write to the user's settings during these tests.
        _originalSettings = typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.CanRead && property.CanWrite)
            .ToDictionary(property => property, property => property.GetValue(null));
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            // Restore the stored theme after its writable dark-mode compatibility view.
            foreach (var setting in _originalSettings.OrderBy(setting => setting.Key.Name == nameof(Settings.Global_Theme)))
            {
                setting.Key.SetValue(null, setting.Value);
            }
        }
        finally
        {
            LoadedField.SetValue(null, _originalLoaded);
        }
    }

    [TestMethod]
    public void EmbeddedPresetIncludesCurrentFavoritesAndPreferencesButNoSessionState()
    {
        JObject preset = ReadPreset();
        var settings = (JObject)preset["Settings"]!;
        string[] favorites = settings.Value<string>("mainwindow_favorites")!.Split(';');
        CollectionAssert.Contains(favorites, "Animation Viewer");
        CollectionAssert.Contains(favorites, "Mesh Explorer");
        CollectionAssert.Contains(favorites, "TLK Editor");
        CollectionAssert.Contains(favorites, "Wwise Graph Editor");
        Assert.IsTrue(settings.Value<bool>("packageeditor_showexperiments"));
        Assert.IsTrue(settings.Value<bool>("interpreter_showlinearcolorwheel"));
        Assert.AreEqual("Dark", settings.Value<string>("global_theme"));
        Assert.IsFalse(settings.Value<bool>("global_analytics_enabled"));

        foreach (string name in new[]
                 {
                     "mainwindow_completedinitialsetup", "global_lastuseddirectories",
                     "experimentsbrowser_lastusedexperiments", "tlkeditor_opentabs", "tlkeditor_selectedtab",
                     "dialogueeditor_bulkcloneinterpreplacements", "dialogueeditor_bulkinterpreplacements",
                     "coalescededitor_sourcepath", "coalescededitor_destinationpath"
                 })
        {
            Assert.IsNull(settings[name], $"The preset must preserve the user's {name} state.");
        }
    }

    [TestMethod]
    public void EmbeddedPresetIncludesToolLocalOptionsAndCameraPreferencesWithoutCredentials()
    {
        var tools = (JObject)ReadPreset()["ToolSettings"]!;
        CollectionAssert.AreEquivalent(new[]
            {
                "DialogueEditor/DialogueEditorOptions.JSON", "CoalescedEditorState.json",
                "FaceFXEditor/ElevenLabs.json", "CameraPresetDistanceScale.txt", "CameraPresetSelection.txt",
                "SequenceEditor/CustomSequenceObjectSources.json", "PackageEditor/PINNEDFILES"
            }, tools.Properties().Select(property => property.Name).ToArray());
        Assert.IsFalse(tools.Properties().Any(property => property.Name.EndsWith("RECENTFILES", StringComparison.OrdinalIgnoreCase)),
            "Pinned favorites are intentional preferences; recent-file history must be preserved.");
        var dialogue = (JObject)tools["DialogueEditor/DialogueEditorOptions.JSON"]!;
        Assert.AreEqual(2, dialogue.Value<int>("AutoSaveMode"));
        Assert.AreEqual("Dark", dialogue.Value<string>("LastTheme"));
        Assert.AreEqual("#1E1E1E", dialogue.Value<string>("GraphBackgroundColor"));
        var coalesced = (JObject)tools["CoalescedEditorState.json"]!;
        Assert.IsTrue(coalesced.Value<bool>("ShowTlkBoxes"));
        Assert.AreEqual(1, coalesced.Count, "Coalesced session documents and history must not be captured.");
        var elevenLabs = (JObject)tools["FaceFXEditor/ElevenLabs.json"]!;
        Assert.AreEqual("eleven_v3", elevenLabs.Value<string>("ModelId"));
        Assert.IsTrue(elevenLabs.Value<bool>("RvcEnabled"));
        AssertContainsNoApiKeyProperties(ReadPreset());
        Assert.AreEqual("84.61", tools.Value<string>("CameraPresetDistanceScale.txt"));
        StringAssert.Contains(tools.Value<string>("CameraPresetSelection.txt"), "Wide Arc Right");

        var sequenceSources = (JArray)tools["SequenceEditor/CustomSequenceObjectSources.json"]!;
        Assert.AreEqual(1, sequenceSources.Count);
        Assert.AreEqual(JTokenType.String, sequenceSources[0].Type, "Custom sequence sources use a JSON array of paths.");
        Assert.AreEqual("GetHenchmen.pcc", Path.GetFileName(sequenceSources[0].Value<string>()));
        using var pinnedReader = new StringReader(tools.Value<string>("PackageEditor/PINNEDFILES")!);
        string pinnedLine = pinnedReader.ReadLine()!;
        RecentsControl.RecentItem pinned = RecentsControl.RecentItem.FromRecentEntryString(pinnedLine);
        Assert.AreEqual(MEGame.LE3, pinned.Game);
        Assert.AreEqual("BioNPC_Miranda.pcc", Path.GetFileName(pinned.Path));
        Assert.AreEqual(pinnedLine, pinned.ConvertToRecentEntry(), "Pinned files use the existing game-prefix line format.");
        Assert.IsNull(pinnedReader.ReadLine());
    }

    [TestMethod]
    public void ApplyingPresetUpdatesPreferencesThroughSettersAndPreservesSessionState()
    {
        var settings = (JObject)ReadPreset()["Settings"]!;
        Settings.MainWindow_Favorites = "A different favorite";
        Settings.PackageEditor_ShowExperiments = false;
        Settings.Global_Analytics_Enabled = true;
        Settings.Global_Theme = "Light";
        Settings.SequenceEditor_MaxVarStringLength = 1;
        Settings.Interpreter_ShowLinearColorWheel = false;
        Settings.MainWindow_CompletedInitialSetup = true;
        var directories = new Dictionary<string, string> { ["OpenPackage"] = @"C:\User\Packages" };
        var experiments = new Dictionary<string, string> { ["PackageEditor"] = "User experiment" };
        var tabs = new List<string> { @"C:\User\Localization.tlk" };
        var replacements = new List<string> { "User replacement" };
        Settings.Global_LastUsedDirectories = directories;
        Settings.ExperimentsBrowser_LastUsedExperiments = experiments;
        Settings.TLKEditor_OpenTabs = tabs;
        Settings.TLKEditor_SelectedTab = tabs[0];
        Settings.DialogueEditor_BulkInterpReplacements = replacements;
        var notifications = new List<string>();
        PropertyChangedEventHandler onChanged = (_, args) => notifications.Add(args.PropertyName);
        Settings.StaticPropertyChanged += onChanged;
        try
        {
            ApplySettings(settings);
        }
        finally
        {
            Settings.StaticPropertyChanged -= onChanged;
        }

        Assert.AreEqual(settings.Value<string>("mainwindow_favorites"), Settings.MainWindow_Favorites);
        Assert.IsTrue(Settings.PackageEditor_ShowExperiments);
        Assert.IsFalse(Settings.Global_Analytics_Enabled);
        Assert.AreEqual("Dark", Settings.Global_Theme);
        Assert.AreEqual(40, Settings.SequenceEditor_MaxVarStringLength);
        Assert.IsTrue(Settings.Interpreter_ShowLinearColorWheel);
        foreach (string property in new[]
                 {
                     nameof(Settings.MainWindow_Favorites), nameof(Settings.PackageEditor_ShowExperiments),
                     nameof(Settings.Global_Analytics_Enabled), nameof(Settings.Global_Theme),
                     nameof(Settings.SequenceEditor_MaxVarStringLength), nameof(Settings.Interpreter_ShowLinearColorWheel)
                 })
        {
            CollectionAssert.Contains(notifications, property, "Open windows need normal settings-change notifications.");
        }
        Assert.IsTrue(Settings.MainWindow_CompletedInitialSetup);
        Assert.AreSame(directories, Settings.Global_LastUsedDirectories);
        Assert.AreEqual(@"C:\User\Packages", Settings.Global_LastUsedDirectories["OpenPackage"]);
        Assert.AreSame(experiments, Settings.ExperimentsBrowser_LastUsedExperiments);
        Assert.AreSame(tabs, Settings.TLKEditor_OpenTabs);
        Assert.AreEqual(tabs[0], Settings.TLKEditor_SelectedTab);
        Assert.AreSame(replacements, Settings.DialogueEditor_BulkInterpReplacements);
    }

    [TestMethod]
    public void ReapplyingPresetRestoresIndependentMutablePreferences()
    {
        var settings = (JObject)ReadPreset()["Settings"]!;
        string capturedPreset = settings.ToString();
        ApplySettings(settings);
        List<string> firstStartupFiles = Settings.CustomStartupFiles;
        List<string> firstAssetDirectories = Settings.CustomAssetDirectories;
        Dictionary<string, ThemeData> firstThemes = Settings.ScriptIDE_SavedThemes;
        firstStartupFiles.Add("user-added.pcc");
        firstAssetDirectories.Add(@"C:\User\Assets");
        firstThemes.Add("User theme", new ThemeData(default, new()));

        ApplySettings(settings);

        Assert.AreNotSame(firstStartupFiles, Settings.CustomStartupFiles);
        Assert.AreNotSame(firstAssetDirectories, Settings.CustomAssetDirectories);
        Assert.AreNotSame(firstThemes, Settings.ScriptIDE_SavedThemes);
        CollectionAssert.AreEqual(settings["customstartupfiles"]!.ToObject<List<string>>()!, Settings.CustomStartupFiles);
        CollectionAssert.AreEqual(settings["customassetdirectories"]!.ToObject<List<string>>()!, Settings.CustomAssetDirectories);
        Assert.IsFalse(Settings.ScriptIDE_SavedThemes.ContainsKey("User theme"));
        Assert.AreEqual(capturedPreset, settings.ToString(), "Applying and editing preferences must not mutate the saved preset.");
    }

    [TestMethod]
    public void ApplyingPresetRestoresCapturedGraphColorsAfterThemeListenersRun()
    {
        var settings = (JObject)ReadPreset()["Settings"]!;
        Settings.Global_Theme = "Light";
        const int themeCommentColor = -1;
        int capturedCommentColor = settings.Value<int>("sequenceeditor_commenttextcolor");
        Assert.AreNotEqual(themeCommentColor, capturedCommentColor);
        int themeNotifications = 0;
        PropertyChangedEventHandler themeListener = (_, args) =>
        {
            if (args.PropertyName == nameof(Settings.Global_Theme))
            {
                themeNotifications++;
                // Open graph editors replace their colors when the application theme changes.
                Settings.SequenceEditor_CommentTextColor = themeCommentColor;
            }
        };
        Settings.StaticPropertyChanged += themeListener;
        try
        {
            ApplySettings(settings);

            Assert.AreEqual(1, themeNotifications, "Applying the preset must notify theme listeners.");
            Assert.AreEqual("Dark", Settings.Global_Theme);
            Assert.AreEqual(capturedCommentColor, Settings.SequenceEditor_CommentTextColor,
                "Apply captured graph colors after the theme has replaced its default colors.");
        }
        finally
        {
            Settings.StaticPropertyChanged -= themeListener;
        }
    }

    [STATestMethod]
    public void ApplyingFavoritesRefreshesMainMenuFlagsWithoutOverwritingCapturedFavorites()
    {
        Settings.MainWindow_Favorites = "Coalesced Compiler";
        var itemsField = typeof(ToolSet).GetField("items", BindingFlags.Static | BindingFlags.NonPublic)!;
        object originalItems = itemsField.GetValue(null);
        var animation = new Tool { name = "Animation Viewer" };
        var mesh = new Tool { name = "Mesh Explorer" };
        var other = new Tool { name = "Another tool" };
        itemsField.SetValue(null, new HashSet<Tool> { animation, mesh, other });
        var settingsHandler = typeof(ToolSet)
            .GetMethod("Settings_StaticPropertyChanged", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<PropertyChangedEventHandler>();
        int favoritesChanged = 0;
        EventHandler favoritesHandler = (_, _) => favoritesChanged++;
        Settings.StaticPropertyChanged += settingsHandler;
        ToolSet.FavoritesChanged += favoritesHandler;
        try
        {
            string favorites = ((JObject)ReadPreset()["Settings"]!).Value<string>("mainwindow_favorites")!;

            ApplySettings(new JObject { ["mainwindow_favorites"] = favorites });

            Assert.IsTrue(animation.IsFavorited);
            Assert.IsTrue(mesh.IsFavorited);
            Assert.IsFalse(other.IsFavorited);
            Assert.AreEqual(favorites, Settings.MainWindow_Favorites,
                "Dependency-property callbacks must not save a partially refreshed favorites list.");
            Assert.AreEqual(1, favoritesChanged, "Refresh the open Favorites menu once after updating every flag.");

            Settings.MainWindow_Favorites = "Animation Viewer";
            Assert.IsTrue(animation.IsFavorited);
            Assert.IsFalse(mesh.IsFavorited, "Subsequent settings changes must remove stale favorite flags.");
            Assert.IsFalse(other.IsFavorited);
            Assert.AreEqual("Animation Viewer", Settings.MainWindow_Favorites);
            Assert.AreEqual(2, favoritesChanged);

            mesh.IsFavorited = true;
            CollectionAssert.AreEquivalent(new[] { "Animation Viewer", "Mesh Explorer" },
                Settings.MainWindow_Favorites.Split(';'), "Normal star toggles must still save favorites.");
            Assert.AreEqual(3, favoritesChanged);
            animation.IsFavorited = false;
            Assert.AreEqual("Mesh Explorer", Settings.MainWindow_Favorites);
            Assert.AreEqual(4, favoritesChanged);
            mesh.IsFavorited = false;
            Assert.AreEqual("", Settings.MainWindow_Favorites);
            Assert.AreEqual(5, favoritesChanged);
        }
        finally
        {
            ToolSet.FavoritesChanged -= favoritesHandler;
            Settings.StaticPropertyChanged -= settingsHandler;
            itemsField.SetValue(null, originalItems);
        }
    }

    [TestMethod]
    public void InvalidPresetDoesNotPartiallyReplaceSettings()
    {
        Settings.MainWindow_Favorites = "Keep my favorites";
        var invalidSettings = new JObject
        {
            ["mainwindow_favorites"] = "Preset favorites",
            ["not_a_lex_setting"] = true
        };

        Assert.Throws<TargetInvocationException>(() => ApplySettings(invalidSettings));

        Assert.AreEqual("Keep my favorites", Settings.MainWindow_Favorites,
            "Validate the whole preset before replacing any preferences.");
    }

    [TestMethod]
    public void InvalidValueDoesNotPartiallyReplaceSettings()
    {
        Settings.MainWindow_Favorites = "Keep my favorites";
        Settings.SequenceEditor_MaxVarStringLength = 17;
        var invalidSettings = new JObject
        {
            ["mainwindow_favorites"] = "Preset favorites",
            ["sequenceeditor_maxvarstringlength"] = "not-an-integer"
        };

        Assert.Throws<TargetInvocationException>(() => ApplySettings(invalidSettings));

        Assert.AreEqual("Keep my favorites", Settings.MainWindow_Favorites,
            "Convert the whole preset before replacing any preferences.");
        Assert.AreEqual(17, Settings.SequenceEditor_MaxVarStringLength);
    }

    [TestMethod]
    public void MergingToolPreferencesPreservesCredentialsHistoryAndUncapturedOptions()
    {
        var preset = new JObject
        {
            ["VoiceId"] = "Scottina voice",
            ["ShowTlkBoxes"] = true,
            ["Emotion"] = new JObject { ["Enabled"] = true }
        };
        var existing = new JObject
        {
            ["VoiceId"] = "User voice",
            ["ShowTlkBoxes"] = false,
            ["EncryptedApiKey"] = "User secret",
            ["RememberApiKey"] = true,
            ["RecentFiles"] = new JArray("User file"),
            ["UncapturedOption"] = 27
        };
        string presetBeforeMerge = preset.ToString();

        JObject merged = JObject.Parse(MergeToolSettings(existing.ToString(), preset));

        Assert.AreEqual("Scottina voice", merged.Value<string>("VoiceId"));
        Assert.IsTrue(merged.Value<bool>("ShowTlkBoxes"));
        Assert.IsTrue(merged["Emotion"]!.Value<bool>("Enabled"));
        Assert.AreEqual("User secret", merged.Value<string>("EncryptedApiKey"));
        Assert.IsTrue(merged.Value<bool>("RememberApiKey"));
        Assert.IsTrue(JToken.DeepEquals(existing["RecentFiles"], merged["RecentFiles"]));
        Assert.AreEqual(27, merged.Value<int>("UncapturedOption"));
        merged["Emotion"]!["Enabled"] = false;
        Assert.AreEqual(presetBeforeMerge, preset.ToString(), "Merged values must not alias the preset's JSON objects.");
    }

    [TestMethod]
    public void MergingToolPreferencesCreatesSettingsWhenNoExistingFileIsPresent()
    {
        var preset = new JObject { ["ShowTlkBoxes"] = true };

        JObject merged = JObject.Parse(MergeToolSettings(null, preset));

        Assert.IsTrue(JToken.DeepEquals(preset, merged));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ApplyingEmbeddedElevenLabsPreferencesPreservesOnlyTheUsersOwnCredentials(bool rememberApiKey)
    {
        var preset = (JObject)ReadPreset()["ToolSettings"]!["FaceFXEditor/ElevenLabs.json"]!;
        var existing = new JObject
        {
            ["VoiceId"] = "User voice",
            ["RememberApiKey"] = rememberApiKey,
            ["EncryptedApiKey"] = "Fake encrypted user credential",
            ["ApiKey"] = "Fake plaintext user credential",
            ["api_key"] = "Fake legacy user credential"
        };

        JObject merged = JObject.Parse(MergeToolSettings(existing.ToString(), preset));

        Assert.AreEqual(preset.Value<string>("VoiceId"), merged.Value<string>("VoiceId"),
            "Apply ElevenLabs preferences from the real embedded preset.");
        Assert.AreEqual(rememberApiKey, merged.Value<bool>("RememberApiKey"),
            "The preset must never opt a user into remembering an API key.");
        JProperty[] userCredentialProperties = existing.Properties().Where(IsApiKeyProperty).ToArray();
        CollectionAssert.AreEquivalent(userCredentialProperties.Select(property => property.Name).ToArray(),
            merged.Properties().Where(IsApiKeyProperty).Select(property => property.Name).ToArray(),
            "The preset must never introduce API-key credentials belonging to its owner.");
        foreach (JProperty credential in userCredentialProperties)
        {
            Assert.IsTrue(JToken.DeepEquals(credential.Value, merged[credential.Name]),
                $"Applying the preset must preserve the user's own {credential.Name} value.");
        }
        AssertContainsNoApiKeyProperties(preset);
    }

    [TestMethod]
    public void ApplyingEmbeddedElevenLabsPreferencesToFreshProfileCreatesNoApiKeyFields()
    {
        var preset = (JObject)ReadPreset()["ToolSettings"]!["FaceFXEditor/ElevenLabs.json"]!;

        JObject merged = JObject.Parse(MergeToolSettings(null, preset));

        Assert.IsTrue(JToken.DeepEquals(preset, merged));
        AssertContainsNoApiKeyProperties(merged);
    }

    private static bool IsApiKeyProperty(JProperty property)
    {
        string normalizedName = new(property.Name.Where(char.IsLetterOrDigit).ToArray());
        return normalizedName.Contains("apikey", StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertContainsNoApiKeyProperties(JObject settings)
    {
        foreach (JProperty property in settings.Descendants().OfType<JProperty>())
        {
            Assert.IsFalse(IsApiKeyProperty(property),
                $"The shared preset must not contain API-key credential fields, including {property.Path}.");
        }
    }

    private static JObject ReadPreset()
    {
        using Stream stream = typeof(ScottinaPreset).Assembly.GetManifestResourceStream(
            "LegendaryExplorer.Misc.AppSettings.ScottinaPreset.json")!;
        Assert.IsNotNull(stream, "The captured Scottina preset must ship with LEX.");
        using var reader = new StreamReader(stream);
        return JObject.Parse(reader.ReadToEnd());
    }

    private static void ApplySettings(JObject settings) => typeof(ScottinaPreset)
        .GetMethod("ApplySettings", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, new object[] { settings });

    private static string MergeToolSettings(string existingJson, JObject preset) => (string)typeof(ScottinaPreset)
        .GetMethod("MergeToolSettings", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, new object[] { existingJson, preset })!;
}
