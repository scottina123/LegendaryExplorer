using System.Collections.Generic;
using LegendaryExplorer.Lightmaps.Lightmass;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.MaterialShaderCompilation;
using LegendaryExplorerCore.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace LegendaryExplorer.Tests.Lightmaps;

/// <summary>Both bakers' "Recompile materials that don't accept lightmaps" option: on by default, carried from the job, and off really turns it off.</summary>
[TestClass]
public class LightmassMaterialRecompileOptionTests
{
    [TestMethod]
    public void Defaults_AreOn()
    {
        Assert.IsTrue(new LightmassBakeJob().RecompileMaterialsWithoutLightmaps);
        Assert.IsTrue(new LightmassBakeSettings().RecompileMaterialsWithoutLightmaps);
        Assert.IsTrue(new StaticLightingGenerationSettings().RecompileMaterialsWithoutLightmaps);
    }

    [TestMethod]
    public void Job_SavesAndLoadsTheOption_AndOlderJobsKeepRecompiling()
    {
        var job = new LightmassBakeJob { RecompileMaterialsWithoutLightmaps = false };
        var reloaded = JsonConvert.DeserializeObject<LightmassBakeJob>(JsonConvert.SerializeObject(job));
        Assert.IsFalse(reloaded.RecompileMaterialsWithoutLightmaps);
        // A job saved before the option existed has no such key
        var older = JsonConvert.DeserializeObject<LightmassBakeJob>("{\"Files\":[]}");
        Assert.IsTrue(older.RecompileMaterialsWithoutLightmaps);
    }

    [TestMethod]
    public void Job_PassesTheOptionToTheBake()
    {
        Assert.IsFalse(new LightmassBakeJob { RecompileMaterialsWithoutLightmaps = false }.BakeSettings([]).RecompileMaterialsWithoutLightmaps);
        Assert.IsTrue(new LightmassBakeJob().BakeSettings([]).RecompileMaterialsWithoutLightmaps);
    }

    [TestMethod]
    public void Off_LeavesTheSceneWithoutARecompiler_EvenIfOneWasGiven()
    {
        var settings = new LightmassBakeSettings { RecompileMaterialsWithoutLightmaps = false };
        settings.Scene.MaterialRecompiler = new LE3MaterialRecompiler();
        var log = new List<string>();
        LightmassBake.ConfigureMaterialRecompiler(settings, MEGame.LE3, log.Add);
        Assert.IsNull(settings.Scene.MaterialRecompiler);
        Assert.IsTrue(log.Exists(l => l.Contains("aren't recompiled")), "The bake log doesn't say materials aren't recompiled.");
    }

    [TestMethod]
    public void On_GivesTheSceneARecompiler_WhenTheGameCanBeCompiled()
    {
        var settings = new LightmassBakeSettings();
        LightmassBake.ConfigureMaterialRecompiler(settings, MEGame.LE3, null);
        Assert.AreEqual(LE3MaterialRecompiler.CanRecompile(MEGame.LE3, out _), settings.Scene.MaterialRecompiler != null);

        // LE1/LE2 levels can't be recompiled for: no recompiler
        var le2 = new LightmassBakeSettings();
        LightmassBake.ConfigureMaterialRecompiler(le2, MEGame.LE2, null);
        Assert.IsNull(le2.Scene.MaterialRecompiler);
    }
}
