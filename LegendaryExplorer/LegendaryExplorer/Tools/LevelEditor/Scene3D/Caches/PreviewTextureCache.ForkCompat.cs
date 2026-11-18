using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using SharpDX.Direct3D11;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

public partial class PreviewTextureCache
{
    public partial class TextureEntry
    {
        /// <summary>
        /// Fork API: the texture's view for drawing. Upstream streams textures, so this asks for the whole mip chain (fork callers draw icons,
        /// backdrops and particle atlases with no size to estimate from) and returns what's resident. Draws that know their size use <see cref="GetView"/>
        /// </summary>
        public ShaderResourceView TextureView => GetView(MeshRenderContext.FullChainPixels);

        private int? width, height;

        /// <summary>The texture's full size (its SizeX), whatever mip is resident</summary>
        public int Width => width ??= Export?.GetProperty<IntProperty>("SizeX")?.Value ?? Texture?.Description.Width ?? 0;

        /// <summary>The texture's full size (its SizeY), whatever mip is resident</summary>
        public int Height => height ??= Export?.GetProperty<IntProperty>("SizeY")?.Value ?? Texture?.Description.Height ?? 0;
    }
}
