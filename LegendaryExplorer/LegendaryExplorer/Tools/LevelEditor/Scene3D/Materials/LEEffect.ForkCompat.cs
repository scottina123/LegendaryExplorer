using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

/// <summary>
/// Fork draw API (VFX particles, lens flares, mesh emitters) on upstream's effect
/// </summary>
public unsafe partial class LEEffect
{
    /// <summary>The view constants <see cref="BeginFrame(DeviceContext, System.Numerics.Matrix4x4, System.Numerics.Matrix4x4, System.Numerics.Vector4)"/> uploaded</summary>
    private LEVSConstants FrameVertexConstants;

    /// <summary>
    /// Draws a mesh of any vertex type, with the shaders set by <see cref="PrepDraw"/> and this frame's view constants
    /// </summary>
    public void RenderObject<TVertex>(DeviceContext context, Mesh<TVertex> mesh, int indexstart, int indexcount) where TVertex : IVertexBase
    {
        if (mesh.Vertices.Count is 0) return;
        RenderObject(context, mesh.VertexBuffer, TVertex.Stride, mesh.IndexBuffer, Format.R32_UInt, indexstart, indexcount);
    }

    /// <summary>
    /// Fork: draws with view constants of the caller's own (a VFX renderer's camera space). The frame's constants are put back afterwards, so
    /// draws after this one see upstream's view, as <see cref="BeginFrame(MeshRenderContext)"/> set it
    /// </summary>
    public void RenderObject<TVertex>(DeviceContext context, LEVSConstants vertexConstants, LEPSConstants pixelConstants, Mesh<TVertex> mesh,
        int indexstart, int indexcount) where TVertex : IVertexBase
    {
        if (mesh.Vertices.Count is 0) return;
        context.UpdateSubresource(ref vertexConstants, VertexShaderConstants);
        context.UpdateSubresource(ref pixelConstants, PixelShaderConstants);
        try
        {
            RenderObject(context, mesh.VertexBuffer, TVertex.Stride, mesh.IndexBuffer, Format.R32_UInt, indexstart, indexcount);
        }
        finally
        {
            LEVSConstants frameVertexConstants = FrameVertexConstants;
            LEPSConstants framePixelConstants = SharedPixelConstants;
            context.UpdateSubresource(ref frameVertexConstants, VertexShaderConstants);
            context.UpdateSubresource(ref framePixelConstants, PixelShaderConstants);
        }
    }
}
