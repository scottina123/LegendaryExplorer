using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace LegendaryExplorer.UserControls.SharedToolControls.Scene3D
{
    public class MeshElement : IDisposable
    {
        public readonly List<Triangle> Triangles;
        public readonly List<LEVertex> Vertices;
        public SharpDX.Direct3D11.Buffer VertexBuffer { get; private set; }
        public SharpDX.Direct3D11.Buffer IndexBuffer { get; private set; }

        public BoxSphereBounds BaseBounds;

        public BoxSphereBounds TransformedBounds;

        private Matrix4x4 localToWorld = Matrix4x4.Identity;
        public Matrix4x4 LocalToWorld
        {
            get => localToWorld;
            set
            {
                localToWorld = value;
                TransformedBounds = BaseBounds.TransformBy(localToWorld);
                Matrix4x4.Invert(LocalToWorld, out Matrix4x4 wtl);
                worldToLocal = new SharpDX.Matrix3x3(wtl.M11, wtl.M12, wtl.M13, wtl.M21, wtl.M22, wtl.M23, wtl.M31, wtl.M32, wtl.M33);
            }
        }

        private SharpDX.Matrix3x3 worldToLocal;
        public SharpDX.Matrix3x3 WorldToLocal => worldToLocal;


        // Creates a new blank mesh.

        // Creates a blank mesh with the given data.
        public MeshElement(Device device, List<Triangle> triangles, List<LEVertex> vertices)
        {
            Triangles = triangles;
            Vertices = vertices;
            RebuildBuffer(device);
        }
        public void RebuildBuffer(Device device)
        {
            // Dispose all the old stuff
            VertexBuffer?.Dispose();
            IndexBuffer?.Dispose();
            if (Triangles.Count == 0 || Vertices.Count == 0) return; // Why build and empty buffer?


            // Update the AABB
            Box boundingBox = new();
            foreach (LEVertex v in Vertices)
            {
                Vector3 pos = v.Position;
                boundingBox.Add(pos);
            }

            TransformedBounds = BaseBounds = new BoxSphereBounds(boundingBox);

            int floatsPerVertex = LEVertex.Stride / 4;
            int numFloats = floatsPerVertex * Vertices.Count;
            float[] vertexdata = new float[numFloats];
            Span<float> vertexDataSpan = vertexdata.AsSpan();
            for (int vertIdx = 0, floatIdx = 0; vertIdx < Vertices.Count; vertIdx++, floatIdx += floatsPerVertex)
            {
                Vertices[vertIdx].ToFloats(vertexDataSpan[floatIdx..]);
            }

            // Create and populate the vertex and index buffers
            VertexBuffer = SharpDX.Direct3D11.Buffer.Create(device, BindFlags.VertexBuffer, vertexdata);
            IndexBuffer = SharpDX.Direct3D11.Buffer.Create(device, BindFlags.IndexBuffer, Triangles.ToArray());
        }

        public void Dispose()
        {
            VertexBuffer?.Dispose();
            IndexBuffer?.Dispose();
        }
    }

    /// <summary>
    /// Contains the indices of the three vertices that make up a triangle.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Triangle(uint vertex1, uint vertex2, uint vertex3)
    {
        public uint Vertex1 = vertex1;
        public uint Vertex2 = vertex2;
        public uint Vertex3 = vertex3;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    //Vertex used for FLocalVertexFactory vertex shaders in LE games
    public struct LEVertex
    {
        public Vector4 position;
        public Vector3 tangent_or_hitTestID; //tangent for game shaders, sometimes used as hittest id for the level editor
        public Vector4 normal;
        public Vector4 color;
        //actual number of UVs used by FLocalVertexFactory vertex shaders varies between 1 float2, and 3 float4s + 1 float2.
        //however, it's perfectly fine for the vertex buffer stride to be longer than the parameters for a vertex shader
        //and for the InputLayout to be bigger. So for simplicity, all vertexes are the maximum size regardless of shader
        private Fixed4<Vector4> uvs;
        public readonly Vector3 Position => new(position.X, position.Y, position.Z);

        private LEVertex(Vector4 position, Vector3 tangent, Vector4 normal, Vector4 color, Fixed4<Vector4> uvs)
        {
            this.position = position;
            this.tangent_or_hitTestID = tangent;
            this.normal = normal;
            this.color = color;
            this.uvs = uvs;
        }

        public LEVertex(Vector3 position, Vector3 normal, Vector2 uv) : this()
        {
            this.position = new Vector4(position, 1);
            this.normal = new Vector4(normal, 1);
            this.uvs[0] = new Vector4(uv, 1, 1);
        }

        //for use by the level editors primitives
        public LEVertex(Vector3 position, Vector4 color, Vector3 hitTestId)
        {
            this.position = new Vector4(position, 1);
            this.color = color;
            this.tangent_or_hitTestID = hitTestId;
        }

        public void ToFloats(Span<float> floats) => MemoryMarshal.CreateSpan(ref Unsafe.As<LEVertex, float>(ref this), Stride / 4).CopyTo(floats);

        public static LEVertex Create(Vector3 position, Vector3 tangent, Vector4 normal, Fixed4<Vector4> uvs)
        {
            return new LEVertex(new Vector4(position, 1), tangent, normal, Vector4.Zero, uvs);
        }
        public static unsafe int Stride => sizeof(Vector4) + sizeof(Vector3) + sizeof(Vector4) + sizeof(Vector4) + sizeof(Vector4) * 3 + sizeof(Vector2);


        public static InputElement[] InputElements { get; } =
        [
            new InputElement("POSITION", 0, Format.R32G32B32A32_Float, 0),
            new InputElement("TANGENT", 0, Format.R32G32B32_Float, 0),
            new InputElement("NORMAL", 0, Format.R32G32B32A32_Float, 0),
            new InputElement("COLOR", 1, Format.R32G32B32A32_Float, 0),
            new InputElement("TEXCOORD", 0, Format.R32G32B32A32_Float, 0),
            new InputElement("TEXCOORD", 1, Format.R32G32B32A32_Float, 0),
            new InputElement("TEXCOORD", 2, Format.R32G32B32A32_Float, 0),
            new InputElement("TEXCOORD", 3, Format.R32G32B32A32_Float, 0),
        ];
    }
}
