using System.Collections.Generic;
using System.Linq;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.Tools.AssetDatabase.Filters;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TextureFilter = LegendaryExplorer.Tools.AssetDatabase.Filters.TextureFilter;

namespace LegendaryExplorer.Tests.Tools.AssetDatabase
{
    [TestClass]
    public class FilterSubclassTests
    {
        [TestMethod]
        public void MaterialFilterLoadsFromDatabase()
        {
            var mf = new MaterialFilter(new FileListSpecification());
            var adb = new AssetDB();
            var dbSpecs = new List<MaterialBoolSpec>() { new MaterialBoolSpec(), new MaterialBoolSpec(new BoolProperty(false, "TestBool")) };
            adb.MaterialBoolSpecs = dbSpecs;

            mf.LoadFromDatabase(adb);
            CollectionAssert.AreEqual(dbSpecs, mf.GeneratedOptions);
        }

        [TestMethod]
        public void TestTextureSearch()
        {
            var r1 = new TextureRecord("Name", "RandomPackage", false, false,
                "DXT1", "Environment512", 512, 512, "ABCDE");

            var r2 = new TextureRecord("Name", "RandomPackage", false, false,
                "TextureCube", "UI", 512, 1024, "ABCDE");

            Assert.IsTrue(TextureFilter.TextureSearch(("name", r1))); // Can search against name
            Assert.IsTrue(TextureFilter.TextureSearch(("ompack", r1))); // Parent Package
            Assert.IsTrue(TextureFilter.TextureSearch(("ABCDE", r1))); // Or CRC
            Assert.IsTrue(TextureFilter.TextureSearch(("dxt1", r1))); // Texture format/type
            Assert.IsTrue(TextureFilter.TextureSearch(("environment512", r1))); // Texture group/type
            Assert.IsTrue(TextureFilter.TextureSearch(("512x512", r1))); // Displayed texture size
            Assert.IsTrue(TextureFilter.TextureSearch(("type:dxt1", r1))); // Explicit type search
            Assert.IsTrue(TextureFilter.TextureSearch(("type:ui", r2))); // Explicit type search on texture group
            Assert.IsFalse(TextureFilter.TextureSearch(("type:dxt5", r1)));

            // Test size parsing
            Assert.IsFalse(TextureFilter.TextureSearch(("size:", r1)));
            Assert.IsFalse(TextureFilter.TextureSearch(("size: 256x256", r1)));
            Assert.IsFalse(TextureFilter.TextureSearch(("size: 512x513", r1)));
            Assert.IsFalse(TextureFilter.TextureSearch(("size: 512xhellox", r1)));
            Assert.IsTrue(TextureFilter.TextureSearch(("size: 512x512", r1)));

            Assert.IsTrue(TextureFilter.TextureSearch(("size: 512x1024", r2)));
            Assert.IsFalse(TextureFilter.TextureSearch(("size: 1024x512", r2)));
        }

        [TestMethod]
        public void TestMeshSearch()
        {
            var r1 = new MeshRecord("NameOfMesh", true, false, 101);
            Assert.IsTrue(AssetFilters.MeshSearch(("NameOfMesh", r1)));
            Assert.IsTrue(AssetFilters.MeshSearch(("nameofmesh", r1)));
            Assert.IsTrue(AssetFilters.MeshSearch(("OfMesh", r1)));
            Assert.IsFalse(AssetFilters.MeshSearch(("Nothing", r1)));

            Assert.IsTrue(AssetFilters.MeshSearch(("bones:101", r1)));
            Assert.IsFalse(AssetFilters.MeshSearch(("bones:", r1)));
            Assert.IsFalse(AssetFilters.MeshSearch(("bones:5000", r1)));
        }

        [TestMethod]
        public void TestClientEffectsFilterIsExactMatch()
        {
            var filters = new AssetFilters(new FileListSpecification());
            var particleSystem = new ParticleSysRecord("PS", "Pkg", false, false, 1, ParticleSysRecord.VFXClass.ParticleSystem);
            var clientEffect = new ParticleSysRecord("CE", "Pkg", false, false, 1, ParticleSysRecord.VFXClass.RvrClientEffect);
            var bioVfxTemplate = new ParticleSysRecord("Template", "Pkg", false, false, 1, ParticleSysRecord.VFXClass.BioVFXTemplate);

            var clientEffectsSpec = filters.ParticleFilter.Filters
                .First(spec => spec.FilterName == "Only Client Effects");

            filters.ParticleFilter.SetSelected(clientEffectsSpec);

            Assert.IsFalse(filters.ParticleFilter.Filter(particleSystem));
            Assert.IsTrue(filters.ParticleFilter.Filter(clientEffect));
            Assert.IsFalse(filters.ParticleFilter.Filter(bioVfxTemplate));
        }

        [TestMethod]
        public void TestAnimationFiltersAreExclusive()
        {
            var filters = new AssetFilters(new FileListSpecification());
            var normalAnimation = new AnimationRecord("Anim", "Anim", "Data", 1f, 30, "Comp", "Key", false, false);
            var ambientPerformance = new AnimationRecord("Perf", "Perf", "Data", 1f, 30, "Comp", "Key", true, false);

            var normalSpec = filters.AnimationFilter.Filters.First(spec => spec.FilterName == "Only Animations");
            var perfSpec = filters.AnimationFilter.Filters.First(spec => spec.FilterName == "Only Performances (ME3)");

            filters.AnimationFilter.SetSelected(normalSpec);
            Assert.IsTrue(filters.AnimationFilter.Filter(normalAnimation));
            Assert.IsFalse(filters.AnimationFilter.Filter(ambientPerformance));

            filters.AnimationFilter.SetSelected(perfSpec);
            Assert.IsFalse(filters.AnimationFilter.Filter(normalAnimation));
            Assert.IsTrue(filters.AnimationFilter.Filter(ambientPerformance));

            filters.AnimationFilter.SetSelected(perfSpec);
            Assert.IsTrue(filters.AnimationFilter.Filter(normalAnimation));
            Assert.IsTrue(filters.AnimationFilter.Filter(ambientPerformance));
        }

    }
}