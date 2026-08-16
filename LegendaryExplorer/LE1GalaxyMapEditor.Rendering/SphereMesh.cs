using System.Runtime.InteropServices;
using SharpDX;

namespace LE1GalaxyMapEditor.Rendering;

internal static class SphereMesh
{
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Vertex(Vector3 position, Vector3 normal, Vector4 tangent, Vector2 textureCoordinate)
    {
        public readonly Vector3 Position = position;
        public readonly Vector3 Normal = normal;
        public readonly Vector4 Tangent = tangent;
        public readonly Vector2 TextureCoordinate = textureCoordinate;
    }
}
