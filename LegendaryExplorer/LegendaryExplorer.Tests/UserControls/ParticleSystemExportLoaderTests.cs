using System.Threading.Tasks;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class ParticleSystemExportLoaderTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void EmitterActorResolvesParticleSystemComponentTemplate()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("EmitterPreviewTest.pcc", MEGame.LE3);
        ExportEntry particleSystem = package.CreateExport("ParticleSystem_0", "ParticleSystem", indexed: false);
        ExportEntry component = package.CreateExport("ParticleSystemComponent_0", "ParticleSystemComponent", indexed: false);
        ExportEntry emitter = package.CreateExport("Emitter_0", "Emitter", indexed: false);
        component.WriteProperty(new ObjectProperty(particleSystem, "Template"));
        emitter.WriteProperty(new ObjectProperty(component, "ParticleSystemComponent"));

        Assert.IsTrue(ParticleSystemExportLoader.CanPreview(emitter));
        Assert.AreSame(particleSystem, ParticleSystemExportLoader.ResolveParticleSystem(emitter));
    }

    [TestMethod]
    public void ParticleEmitterResolvesReferencingParticleSystem()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("ParticleEmitterPreviewTest.pcc", MEGame.LE3);
        ExportEntry particleSystem = package.CreateExport("ParticleSystem_0", "ParticleSystem", indexed: false);
        ExportEntry emitter = package.CreateExport("ParticleSpriteEmitter_0", "ParticleSpriteEmitter", indexed: false);
        particleSystem.WriteProperty(new ArrayProperty<ObjectProperty>("Emitters")
        {
            new(emitter)
        });

        Assert.IsTrue(ParticleSystemExportLoader.CanPreview(emitter));
        Assert.AreSame(particleSystem, ParticleSystemExportLoader.ResolveParticleSystem(emitter));
    }

    [TestMethod]
    public void EmitterActorOffersPreviewForImportedParticleSystemTemplate()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("ImportedEmitterPreviewTest.pcc", MEGame.LE3);
        IEntry particleSystemImport = package.GetEntryOrAddImport("VFX.ParticleSystem_0", "ParticleSystem");
        ExportEntry component = package.CreateExport("ParticleSystemComponent_0", "ParticleSystemComponent", indexed: false);
        ExportEntry emitter = package.CreateExport("Emitter_0", "Emitter", indexed: false);
        component.WriteProperty(new ObjectProperty(particleSystemImport, "Template"));
        emitter.WriteProperty(new ObjectProperty(component, "ParticleSystemComponent"));

        Assert.IsTrue(ParticleSystemExportLoader.CanPreview(emitter));
    }

    [TestMethod]
    public void UnconnectedEmitterDoesNotOfferParticleSystemPreview()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("UnconnectedEmitterPreviewTest.pcc", MEGame.LE3);
        ExportEntry emitter = package.CreateExport("Emitter_0", "Emitter", indexed: false);

        Assert.IsFalse(ParticleSystemExportLoader.CanPreview(emitter));
        Assert.IsNull(ParticleSystemExportLoader.ResolveParticleSystem(emitter));
    }
}
