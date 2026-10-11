using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using EditorWindow = LegendaryExplorer.Tools.LevelEditor.LevelEditor;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class StaticSceneLightCacheTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void NativeSkyLightMutationsInvalidateBackdropKeyWithoutChangingLegacyLights()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("SkyLighting.pcc", MEGame.LE3);
        ExportEntry actor = package.CreateExport("SkyLight", "SkyLight", indexed: false);
        ExportEntry component = package.CreateExport("SkyLightComponent", "SkyLightComponent", actor, indexed: false);
        component.WriteProperties([
            new FloatProperty(2, "Brightness"),
            new FloatProperty(0.5f, "LowerBrightness")
        ]);
        var context = new LevelEditorRenderContext();
        try
        {
            SceneLight sky = SceneLight.Create(component, "SkyLight", context.PackageCache);
            Assert.IsNotNull(sky);
            Assert.AreEqual(SceneLightType.Sky, sky.Type);

            // The key computation needs only its render context and visible-actor list. Avoid
            // loading window XAML, persisted editor settings, or an interactive GPU viewport.
            var editor = (EditorWindow)RuntimeHelpers.GetUninitializedObject(typeof(EditorWindow));
            const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(EditorWindow).GetField("<RenderContext>k__BackingField", privateInstance)!
                .SetValue(editor, context);
            FieldInfo actors = typeof(EditorWindow).GetField("staticVisibleActors", privateInstance)!;
            actors.SetValue(editor, Activator.CreateInstance(actors.FieldType));
            MethodInfo computeKey = typeof(EditorWindow).GetMethod("ComputeStaticSceneCacheKey", privateInstance)!;
            long Key() => (long)computeKey.Invoke(editor, null)!;

            int legacyRevision = context.SceneLightRevision;
            long emptyKey = Key();
            Assert.AreEqual(emptyKey, Key(), "Unchanged render inputs must keep the backdrop key stable.");

            context.AddLights([sky]);
            long addedKey = Key();
            Assert.HasCount(1, context.GetLights(out _));
            Assert.AreNotEqual(emptyKey, addedKey, "A newly added sky light changes cached base-pass ambient lighting.");
            Assert.AreEqual(addedKey, Key());

            context.RemoveLights([sky]);
            long removedKey = Key();
            Assert.IsEmpty(context.GetLights(out _));
            Assert.AreNotEqual(addedKey, removedKey, "Removing a sky light must discard its cached ambient contribution.");

            context.AddLights([sky]);
            long readdedKey = Key();
            context.ClearLights();
            long clearedKey = Key();
            Assert.IsEmpty(context.GetLights(out _));
            Assert.AreNotEqual(readdedKey, clearedKey, "Clearing native lights must invalidate the backdrop too.");
            Assert.AreEqual(clearedKey, Key());

            Assert.IsEmpty(context.SceneLights);
            Assert.AreEqual(legacyRevision, context.SceneLightRevision,
                "This regression exercises native sky lighting independently of the legacy light collection.");
        }
        finally
        {
            context.UnloadLevel();
            context.PackageCache.Dispose();
        }
    }
}
