using System;
using LegendaryExplorer.Resources;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using SharpDX.Direct3D;
using Device = SharpDX.Direct3D11.Device;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

/// <summary>
/// A <see cref="MeshRenderContext"/> on a WARP device with upstream's <see cref="LEEffect"/>, and an orphan MaterialInstanceConstant's
/// <see cref="MaterialRenderProxy"/>: enough to run the production shader parameter setters without a viewport, an installed game or a shader cache
/// </summary>
internal sealed class WarpMaterialFixture : IDisposable
{
    private readonly IMEPackage package;
    private readonly Device device = new(DriverType.Warp);
    public MeshRenderContext Context { get; } = new();
    public MaterialRenderProxy Material { get; }

    public WarpMaterialFixture(MEGame game = MEGame.LE2)
    {
        typeof(RenderContext).GetProperty(nameof(RenderContext.Device))!.GetSetMethod(true)!.Invoke(Context, [device]);
        typeof(RenderContext).GetProperty(nameof(RenderContext.ImmediateContext))!.GetSetMethod(true)!
            .Invoke(Context, [device.ImmediateContext]);
        typeof(MeshRenderContext).GetProperty(nameof(MeshRenderContext.LEEffect))!.GetSetMethod(true)!
            .Invoke(Context, [new LEEffect(device, EmbeddedResources.LevelEditorShader)]);

        LegendaryExplorerCore.LegendaryExplorerCoreLib.InitLib(System.Threading.Tasks.TaskScheduler.Default);
        package = MEPackageHandler.CreateMemoryEmptyPackage("WarpMaterialFixture.pcc", game);
        ExportEntry export = package.CreateExport("Fixture", "MaterialInstanceConstant", indexed: false);
        export.WriteProperties([]);
        Material = new MaterialRenderProxy(Context, export);
        InjectEmptyShaderMap(Material);
    }

    /// <summary>
    /// Gives a material whose shaders were never loaded an empty shader map (no uniform expressions), which upstream's parameter setters evaluate
    /// </summary>
    internal static void InjectEmptyShaderMap(MaterialRenderProxy material) =>
        typeof(MaterialRenderProxy).GetField("ShaderMap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(material, new LegendaryExplorerCore.Unreal.BinaryConverters.MaterialShaderMap
            {
                UniformPixelScalarExpressions = [], UniformPixelVectorExpressions = [],
                UniformVertexScalarExpressions = [], UniformVertexVectorExpressions = [],
                Uniform2DTextureExpressions = [], UniformCubeTextureExpressions = []
            });

    public void Dispose()
    {
        package.Dispose();
        Context.LEEffect.Dispose();
        Context.ImmediateContext.Dispose();
        device.Dispose();
    }
}
