using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Gammtek.Extensions;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Device = SharpDX.Direct3D11.Device;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

public class Mesh<TVertex> : IDisposable where TVertex : IVertexBase
{
    public List<Triangle> Triangles { get; private set; }
    public List<TVertex> Vertices { get; private set; }
    private SharpDX.Direct3D11.Buffer vertexBuffer;
    private SharpDX.Direct3D11.Buffer indexBuffer;
    private SharedMeshData<TVertex> sharedData;
    private bool verticesAreUnique;
    private bool gpuWritableVertexBuffer;
    public SharpDX.Direct3D11.Buffer VertexBuffer => sharedData?.VertexBuffer ?? vertexBuffer;
    public SharpDX.Direct3D11.Buffer IndexBuffer => sharedData?.IndexBuffer ?? indexBuffer;

    private bool _isDynamic;

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

    private SharpDX.Matrix3x3 worldToLocal = SharpDX.Matrix3x3.Identity;
    public SharpDX.Matrix3x3 WorldToLocal => worldToLocal;

    // Dynamic vertex buffer for fast skinning updates (Map/Unmap instead of recreate)
    private SharpDX.Direct3D11.Buffer _dynamicVertexBuffer;
    private int _dynamicVertexCapacity;
    private float[] _vertexScratch; // reusable scratch array

    // Creates a blank mesh with the given data.
    public Mesh(Device device, List<Triangle> triangles, List<TVertex> vertices, bool isDynamic = false)
    {
        Triangles = triangles;
        Vertices = vertices;
        _isDynamic = isDynamic;
        RebuildBuffer(device);
    }

    internal Mesh(SharedMeshData<TVertex> data)
    {
        sharedData = data;
        sharedData.AddReference();
        Triangles = data.Triangles;
        Vertices = data.Vertices;
        BaseBounds = TransformedBounds = data.Bounds;
    }

    public void RebuildBuffer(Device device)
    {
        DetachSharedData();
        gpuWritableVertexBuffer = false;
        // Dispose all the old stuff
        vertexBuffer?.Dispose();
        indexBuffer?.Dispose();
        vertexBuffer = null;
        indexBuffer = null;


        // Update the AABB
        Box boundingBox = new();
        if (Vertices.Count is 0 || Triangles.Count is 0)
        {
            return;
        }

        foreach (TVertex v in Vertices)
        {
            boundingBox.Add(v.Position);
        }
        TransformedBounds = BaseBounds = new BoxSphereBounds(boundingBox);

        indexBuffer = SharpDX.Direct3D11.Buffer.Create(device, BindFlags.IndexBuffer, Triangles.ToArray());

        int stride = TVertex.Stride;
        int floatsPerVertex = stride / 4;
        float[] vertexdata = new float[floatsPerVertex * Vertices.Count];
        Span<float> vertexDataSpan = vertexdata.AsSpan();
        for (int vertIdx = 0, floatIdx = 0; vertIdx < Vertices.Count; vertIdx++, floatIdx += floatsPerVertex)
        {
            Vertices[vertIdx].ToFloats(vertexDataSpan[floatIdx..]);
        }

        if (!_isDynamic)
        {
            vertexBuffer = SharpDX.Direct3D11.Buffer.Create(device, BindFlags.VertexBuffer, vertexdata);
        }
        else
        {
            // Supply the initial contents to ID3D11Device::CreateBuffer. Resource preparation can run on the
            // Level Editor's background worker, but the immediate context belongs exclusively to the render
            // thread. Mapping it here used to race Direct2D's EndDraw and could fault inside the graphics driver.
            vertexBuffer = SharpDX.Direct3D11.Buffer.Create(device, vertexdata, new BufferDescription(
                stride * Vertices.Count,
                ResourceUsage.Dynamic,
                BindFlags.VertexBuffer,
                CpuAccessFlags.Write,
                ResourceOptionFlags.None,
                stride));
        }
    }

    public unsafe void UpdateVertices(DeviceContext context)
    {
        if (!_isDynamic || Vertices.Count == 0) return;

        if (sharedData is not null || gpuWritableVertexBuffer)
        {
            bool wasShared = sharedData is not null;
            DetachSharedData();
            if (wasShared)
            {
                indexBuffer = SharpDX.Direct3D11.Buffer.Create(context.Device, BindFlags.IndexBuffer,
                    Triangles.ToArray());
            }

            var cpuWritableBuffer = new SharpDX.Direct3D11.Buffer(context.Device, new BufferDescription(
                TVertex.Stride * Vertices.Count,
                ResourceUsage.Dynamic,
                BindFlags.VertexBuffer,
                CpuAccessFlags.Write,
                ResourceOptionFlags.None,
                TVertex.Stride));
            vertexBuffer?.Dispose();
            vertexBuffer = cpuWritableBuffer;
            gpuWritableVertexBuffer = false;
        }

        int stride = TVertex.Stride;
        int floatsPerVertex = stride / 4;

        // Calculate bounds before mapping so the D3D resource remains mapped only for the short
        // bulk copy. Holding a dynamic buffer mapped while serializing every field of every vertex
        // can serialize the render thread with the driver on animated dialogue meshes.
        Span<TVertex> vertices = CollectionsMarshal.AsSpan(Vertices);
        Box boundingBox = new();
        foreach (ref readonly TVertex vertex in vertices)
        {
            boundingBox.Add(vertex.Position);
        }

        var dataBox = context.MapSubresource(vertexBuffer, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TVertex>()
            && Unsafe.SizeOf<TVertex>() == stride)
        {
            ref TVertex firstVertex = ref MemoryMarshal.GetReference(vertices);
            ReadOnlySpan<byte> sourceBytes = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<TVertex, byte>(ref firstVertex), vertices.Length * stride);
            sourceBytes.CopyTo(new Span<byte>((void*)dataBox.DataPointer, sourceBytes.Length));
        }
        else
        {
            var destination = new Span<float>((void*)dataBox.DataPointer, Vertices.Count * floatsPerVertex);
            for (int vertexIndex = 0, floatIndex = 0; vertexIndex < Vertices.Count;
                 vertexIndex++, floatIndex += floatsPerVertex)
            {
                Vertices[vertexIndex].ToFloats(destination[floatIndex..]);
            }
        }
        context.UnmapSubresource(vertexBuffer, 0);

        BaseBounds = new BoxSphereBounds(boundingBox);
        TransformedBounds = BaseBounds.TransformBy(localToWorld);
    }

    /// <summary>
    /// Replaces this mesh's vertex buffer with a default-usage raw buffer that can be written by a
    /// compute shader and consumed directly by the existing vertex factory. This is deliberately
    /// opt-in: normal meshes retain their smaller immutable or CPU-writable buffers.
    /// </summary>
    internal bool EnableGpuVertexWrites(Device device)
    {
        if (Vertices.Count == 0)
        {
            return false;
        }
        if (gpuWritableVertexBuffer)
        {
            return true;
        }

        int stride = TVertex.Stride;
        int floatsPerVertex = stride / sizeof(float);
        float[] vertexData = new float[floatsPerVertex * Vertices.Count];
        Span<float> vertexDataSpan = vertexData;
        for (int vertexIndex = 0, floatIndex = 0;
             vertexIndex < Vertices.Count;
             vertexIndex++, floatIndex += floatsPerVertex)
        {
            Vertices[vertexIndex].ToFloats(vertexDataSpan[floatIndex..]);
        }

        var description = new BufferDescription
        {
            SizeInBytes = stride * Vertices.Count,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.VertexBuffer | BindFlags.UnorderedAccess,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.BufferAllowRawViews,
            StructureByteStride = 0,
        };
        using var stream = SharpDX.DataStream.Create(vertexData, true, true);
        var replacement = new SharpDX.Direct3D11.Buffer(device, stream, description);

        bool wasShared = sharedData is not null;
        EnsureUniqueVertices();
        DetachSharedData();
        if (wasShared)
        {
            indexBuffer = SharpDX.Direct3D11.Buffer.Create(device, BindFlags.IndexBuffer, Triangles.ToArray());
        }

        if (ReferenceEquals(vertexBuffer, _dynamicVertexBuffer))
        {
            _dynamicVertexBuffer = null;
            _dynamicVertexCapacity = 0;
        }
        vertexBuffer?.Dispose();
        vertexBuffer = replacement;
        gpuWritableVertexBuffer = true;
        _isDynamic = true;
        return true;
    }

    /// <summary>
    /// Updates the GPU vertex buffer in-place using Map/Unmap on a dynamic buffer.
    /// Much faster than RebuildBuffer for per-frame skinning updates because it avoids
    /// destroying and recreating GPU resources every frame.
    /// </summary>
    public void UpdateVertexBuffer(Device device)
    {
        if (Vertices.Count == 0) return;
        bool wasShared = sharedData is not null;
        EnsureUniqueVertices();
        DetachSharedData();
        if (wasShared)
        {
            indexBuffer = SharpDX.Direct3D11.Buffer.Create(device, BindFlags.IndexBuffer, Triangles.ToArray());
        }

        int floatsPerVertex = TVertex.Stride / 4;
        int numFloats = floatsPerVertex * Vertices.Count;
        int bufferSizeBytes = numFloats * 4;

        // Ensure dynamic buffer exists and is large enough
        if (_dynamicVertexBuffer == null || _dynamicVertexCapacity < Vertices.Count)
        {
            _dynamicVertexBuffer?.Dispose();
            _dynamicVertexCapacity = Vertices.Count;

            var desc = new BufferDescription
            {
                SizeInBytes = bufferSizeBytes,
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.VertexBuffer,
                CpuAccessFlags = CpuAccessFlags.Write,
                OptionFlags = ResourceOptionFlags.None,
                StructureByteStride = 0
            };
            _dynamicVertexBuffer = new SharpDX.Direct3D11.Buffer(device, desc);

            // Switch the mesh to use the dynamic buffer
            vertexBuffer?.Dispose();
            vertexBuffer = _dynamicVertexBuffer;
            gpuWritableVertexBuffer = false;
        }

        // Reuse scratch array
        if (_vertexScratch == null || _vertexScratch.Length < numFloats)
            _vertexScratch = new float[numFloats];

        Span<float> span = _vertexScratch.AsSpan(0, numFloats);
        for (int vertIdx = 0, floatIdx = 0; vertIdx < Vertices.Count; vertIdx++, floatIdx += floatsPerVertex)
        {
            Vertices[vertIdx].ToFloats(span[floatIdx..]);
        }

        // Map, write, unmap — no buffer allocation
        var context = device.ImmediateContext;
        var dataBox = context.MapSubresource(_dynamicVertexBuffer, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
        Marshal.Copy(_vertexScratch, 0, dataBox.DataPointer, numFloats);
        context.UnmapSubresource(_dynamicVertexBuffer, 0);
    }

    public void Dispose()
    {
        if (sharedData is not null)
        {
            sharedData.ReleaseReference();
            sharedData = null;
            return;
        }
        // If VertexBuffer == _dynamicVertexBuffer, only dispose once
        if (vertexBuffer != null && vertexBuffer != _dynamicVertexBuffer)
            vertexBuffer.Dispose();
        _dynamicVertexBuffer?.Dispose();
        indexBuffer?.Dispose();
    }

    /// <summary>
    /// Detaches the CPU vertex list before skinning or morphing an instance that currently shares
    /// immutable bind-pose geometry with other components.
    /// </summary>
    internal void EnsureUniqueVertices()
    {
        if (sharedData is not null && !verticesAreUnique)
        {
            Vertices = new List<TVertex>(Vertices);
            verticesAreUnique = true;
            _isDynamic = true;
        }
    }

    private void DetachSharedData()
    {
        if (sharedData is null)
        {
            return;
        }

        if (!verticesAreUnique)
        {
            Vertices = new List<TVertex>(Vertices);
            verticesAreUnique = true;
        }
        sharedData.ReleaseReference();
        sharedData = null;
    }
}

internal interface ISharedMeshData
{
    void ReleaseReference();
}

/// <summary>
/// Immutable CPU geometry and GPU buffers shared by mesh component instances. Components detach on
/// their first skinning/morph update, so per-actor deformation remains isolated.
/// </summary>
internal sealed class SharedMeshData<TVertex> : ISharedMeshData where TVertex : IVertexBase
{
    public List<Triangle> Triangles { get; }
    public List<TVertex> Vertices { get; }
    public SharpDX.Direct3D11.Buffer VertexBuffer { get; }
    public SharpDX.Direct3D11.Buffer IndexBuffer { get; }
    public BoxSphereBounds Bounds { get; }
    private int references = 1; // the render-context cache owns the initial reference

    public SharedMeshData(Device device, List<Triangle> triangles, List<TVertex> vertices)
    {
        Triangles = triangles;
        Vertices = vertices;
        if (vertices.Count == 0 || triangles.Count == 0)
        {
            return;
        }

        Box boundingBox = new();
        foreach (TVertex vertex in vertices)
        {
            boundingBox.Add(vertex.Position);
        }
        Bounds = new BoxSphereBounds(boundingBox);
        IndexBuffer = SharpDX.Direct3D11.Buffer.Create(device, BindFlags.IndexBuffer, triangles.ToArray());

        int floatsPerVertex = TVertex.Stride / 4;
        float[] vertexData = new float[floatsPerVertex * vertices.Count];
        Span<float> vertexDataSpan = vertexData;
        for (int vertexIndex = 0, floatIndex = 0;
             vertexIndex < vertices.Count;
             vertexIndex++, floatIndex += floatsPerVertex)
        {
            vertices[vertexIndex].ToFloats(vertexDataSpan[floatIndex..]);
        }
        VertexBuffer = SharpDX.Direct3D11.Buffer.Create(device, BindFlags.VertexBuffer, vertexData);
    }

    public void AddReference() => Interlocked.Increment(ref references);

    public void ReleaseReference()
    {
        if (Interlocked.Decrement(ref references) != 0)
        {
            return;
        }
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

/// <summary>
/// The base class for vertices that can be rendered. They must have a position. This is necessary for builtin AABB computation as well.
/// </summary>
public interface IVertexBase
{
    public Vector3 Position { get; }

    public void ToFloats(Span<float> dest);

    public static abstract InputElement[] InputElements { get; }

    public static abstract int Stride { get; }

    public static abstract IVertexBase Create(MEGame game, Vector3 position, Vector3 tangent, Vector4 normal, Fixed4<Vector4> uvs);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
//vertex used by LEX's generic shader
public struct WorldVertex : IVertexBase
{
    private Vector4 _position;
    private Vector3 HitTestID;
    public Vector4 Normal;
    public Vector4 Color;
    public Vector2 UV;
    public readonly Vector3 Position => new(_position.X, _position.Y, _position.Z);

    public WorldVertex(Vector3 position, Vector4 normal, Vector2 uv)
    {
        _position = new Vector4(position, 1);
        Normal = normal;
        UV = uv;
    }

    //for use by the level editors primitives
    public WorldVertex(Vector3 position, Vector4 color, Vector3 hitTestId)
    {
        _position = new Vector4(position, 1);
        Color = color;
        HitTestID = hitTestId;
    }

    public void ToFloats(Span<float> dest) => this.AsSpanOf<WorldVertex, float>().CopyTo(dest);

    public static InputElement[] InputElements =>
    [
        new InputElement("POSITION", 0, Format.R32G32B32A32_Float, 0),
        new InputElement("TANGENT", 0, Format.R32G32B32_Float, 0),
        new InputElement("NORMAL", 0, Format.R32G32B32A32_Float, 0),
        new InputElement("COLOR", 1, Format.R32G32B32A32_Float, 0),
        new InputElement("TEXCOORD", 0, Format.R32G32_Float, 0)
    ];

    public static unsafe int Stride => sizeof(Vector4) + sizeof(Vector3) + sizeof(Vector4) + sizeof(Vector4) + sizeof(Vector2);

    public static IVertexBase Create(MEGame game, Vector3 position, Vector3 tangent, Vector4 normal, Fixed4<Vector4> uvs)
    {
        return new WorldVertex(position, normal, new Vector2(uvs[0].X, uvs[0].Y));
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
// Vertex used for compiled FLocalVertexFactory game shaders.
public struct LEVertex : IVertexBase
{
    private Vector4 position;
    private Vector3 tangent;
    private Vector4 normal;
    private Vector4 color;
    //actual number of UVs used by FLocalVertexFactory vertex shaders varies between 1 float2, and 3 float4s + 1 float2.
    //however, it's perfectly fine for the vertex buffer stride to be longer than the parameters for a vertex shader
    //and for the InputLayout to be bigger. So for simplicity, all vertexes are the maximum size regardless of shader
    private Fixed4<Vector4> uvs;
    public readonly Vector3 Position => new(position.X, position.Y, position.Z);

    private LEVertex(Vector4 position, Vector3 tangent, Vector4 normal, Vector4 color, Fixed4<Vector4> uvs)
    {
        this.position = position;
        this.tangent = tangent;
        this.normal = normal;
        this.color = color;
        this.uvs = uvs;
    }

    public readonly LEVertex WithPositionAndNormal(MEGame game, Vector3 newPosition, Vector4 newNormal) =>
        new(new Vector4(newPosition, position.W), tangent, EncodeShaderNormal(game, newNormal), color, uvs);

    public readonly LEVertex WithColor(Vector4 newColor) =>
        new(position, tangent, normal, newColor, uvs);

    public void ToFloats(Span<float> floats) => MemoryMarshal.CreateSpan(ref Unsafe.As<LEVertex, float>(ref this), Stride / 4).CopyTo(floats);

    public static IVertexBase Create(MEGame game, Vector3 position, Vector3 tangent, Vector4 normal, Fixed4<Vector4> uvs)
    {
        return new LEVertex(
            new Vector4(position, 1),
            EncodeShaderNormal(game, tangent),
            EncodeShaderNormal(game, normal),
            Vector4.Zero,
            uvs);
    }

    // OT shaders preserve original UBYTE4 values and unpack with value * (2 / 255) - 1.
    // LE shaders receive normalized values and unpack with value * 2 - 1.
    private static Vector3 EncodeShaderNormal(MEGame game, Vector3 value)
    {
        float scale = game.IsLEGame() ? 0.5f : 127.5f;
        float max = game.IsLEGame() ? 1f : 255f;
        return Vector3.Clamp((value + Vector3.One) * scale, Vector3.Zero, new Vector3(max));
    }

    private static Vector4 EncodeShaderNormal(MEGame game, Vector4 value)
    {
        float scale = game.IsLEGame() ? 0.5f : 127.5f;
        float max = game.IsLEGame() ? 1f : 255f;
        return Vector4.Clamp((value + Vector4.One) * scale, Vector4.Zero, new Vector4(max));
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

/// <summary>
/// Complete UE3 sprite-particle vertex. The superset layout is accepted by all four sprite factories:
/// FParticleVertexFactory, FParticleSubUVVertexFactory, FParticleDynamicParameterVertexFactory, and
/// FParticleSubUVDynamicParameterVertexFactory. Factories ignore only the TEXCOORD semantics they do not use.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ParticleVertex : IVertexBase
{
    private Vector4 position;
    public Vector4 OldPosition;
    public Vector3 Size;
    public float Rotation;
    public Vector4 TextureCoordinates;
    public Vector4 Color;
    public Vector4 SubUVData;
    public Vector4 DynamicParameter;

    public readonly Vector3 Position => new(position.X, position.Y, position.Z);

    public ParticleVertex(
        Vector3 position,
        Vector3 oldPosition,
        Vector3 size,
        float rotation,
        Vector4 textureCoordinates,
        Vector4 color,
        Vector4 subUVData,
        Vector4 dynamicParameter)
    {
        this.position = new Vector4(position, 1);
        OldPosition = new Vector4(oldPosition, 1);
        Size = size;
        Rotation = rotation;
        TextureCoordinates = textureCoordinates;
        Color = color;
        SubUVData = subUVData;
        DynamicParameter = dynamicParameter;
    }

    public void ToFloats(Span<float> floats)
        => MemoryMarshal.CreateSpan(ref Unsafe.As<ParticleVertex, float>(ref this), Stride / 4).CopyTo(floats);

    public static IVertexBase Create(MEGame game, Vector3 position, Vector3 tangent, Vector4 normal, Fixed4<Vector4> uvs)
        => new ParticleVertex(position, position, Vector3.Zero, 0, uvs[0], Vector4.One, Vector4.Zero, Vector4.One);

    public static unsafe int Stride => sizeof(Vector4) * 6 + sizeof(Vector3) + sizeof(float);

    public static InputElement[] InputElements { get; } =
    [
        new InputElement("POSITION", 0, Format.R32G32B32A32_Float, 0),
        new InputElement("NORMAL", 0, Format.R32G32B32A32_Float, 0),
        new InputElement("TANGENT", 0, Format.R32G32B32_Float, 0),
        new InputElement("BLENDWEIGHT", 0, Format.R32_Float, 0),
        new InputElement("TEXCOORD", 0, Format.R32G32B32A32_Float, 0),
        new InputElement("TEXCOORD", 1, Format.R32G32B32A32_Float, 0),
        new InputElement("TEXCOORD", 2, Format.R32G32B32A32_Float, 0),
        new InputElement("TEXCOORD", 3, Format.R32G32B32A32_Float, 0),
    ];
}

/// <summary>
/// Complete UE3 beam/trail vertex. POSITION is an already expanded ribbon edge; NORMAL stores the adjacent
/// reference position used by the factory to derive its tangent basis. TANGENT is present even when a particular
/// compiled base-pass shader optimizes it away, so the input contract remains valid for every beam/trail shader.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ParticleBeamTrailVertex : IVertexBase
{
    private Vector4 position;
    public Vector4 DirectionReference;
    public Vector3 Tangent;
    public Vector4 TextureCoordinates;
    public float Rotation;
    public Vector4 Color;
    public Vector4 DynamicParameter;

    public readonly Vector3 Position => new(position.X, position.Y, position.Z);

    public ParticleBeamTrailVertex(
        Vector3 position,
        Vector3 directionReference,
        Vector3 tangent,
        Vector4 textureCoordinates,
        float rotation,
        Vector4 color,
        Vector4 dynamicParameter = default)
    {
        this.position = new Vector4(position, 1);
        DirectionReference = new Vector4(directionReference, 1);
        Tangent = tangent;
        TextureCoordinates = textureCoordinates;
        Rotation = rotation;
        Color = color;
        DynamicParameter = dynamicParameter;
    }

    public void ToFloats(Span<float> floats)
        => MemoryMarshal.CreateSpan(ref Unsafe.As<ParticleBeamTrailVertex, float>(ref this), Stride / 4).CopyTo(floats);

    public static IVertexBase Create(MEGame game, Vector3 position, Vector3 tangent, Vector4 normal, Fixed4<Vector4> uvs)
        => new ParticleBeamTrailVertex(position, position - tangent, tangent, uvs[0], 0, Vector4.One);

    public static unsafe int Stride => sizeof(Vector4) * 5 + sizeof(Vector3) + sizeof(float);

    public static InputElement[] InputElements { get; } =
    [
        new InputElement("POSITION", 0, Format.R32G32B32A32_Float, 0),
        new InputElement("NORMAL", 0, Format.R32G32B32A32_Float, 0),
        new InputElement("TANGENT", 0, Format.R32G32B32_Float, 0),
        new InputElement("TEXCOORD", 0, Format.R32G32B32A32_Float, 0),
        new InputElement("BLENDWEIGHT", 0, Format.R32_Float, 0),
        new InputElement("TEXCOORD", 1, Format.R32G32B32A32_Float, 0),
        new InputElement("TEXCOORD", 2, Format.R32G32B32A32_Float, 0),
    ];
}
