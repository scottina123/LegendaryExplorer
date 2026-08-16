using System.IO;
using System.Text.Json;
using SharpDX;

namespace LE1GalaxyMapEditor.Rendering;

/// <summary>
/// Minimal reader for the interleaved static-mesh glTF files exported by
/// Legendary Explorer. Keeping this local avoids coupling the renderer to
/// Legendary Explorer or a general-purpose scene library.
/// </summary>
internal static class GltfMesh
{
    public static (SphereMesh.Vertex[] Vertices, uint[] Indices) Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var primitive = root.GetProperty("meshes")[0].GetProperty("primitives")[0];
        var attributes = primitive.GetProperty("attributes");

        var bufferUri = root.GetProperty("buffers")[0].GetProperty("uri").GetString()
            ?? throw new InvalidDataException("The glTF buffer has no URI.");
        var buffer = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path)!, bufferUri));

        var position = GetAccessor(root, attributes.GetProperty("POSITION").GetInt32());
        var normal = GetAccessor(root, attributes.GetProperty("NORMAL").GetInt32());
        var tangent = GetAccessor(root, attributes.GetProperty("TANGENT").GetInt32());
        var uv = GetAccessor(root, attributes.GetProperty("TEXCOORD_0").GetInt32());

        if (position.Count != normal.Count || position.Count != tangent.Count || position.Count != uv.Count)
        {
            throw new InvalidDataException("The glTF vertex attribute counts do not match.");
        }

        var vertices = new SphereMesh.Vertex[position.Count];
        for (var index = 0; index < vertices.Length; index++)
        {
            vertices[index] = new SphereMesh.Vertex(
                ReadVector3(buffer, position, index),
                ReadVector3(buffer, normal, index),
                ReadVector4(buffer, tangent, index),
                ReadVector2(buffer, uv, index));
        }

        var indexAccessor = GetAccessor(root, primitive.GetProperty("indices").GetInt32());
        var indices = new uint[indexAccessor.Count];
        for (var index = 0; index < indices.Length; index++)
        {
            var offset = indexAccessor.Offset + index * indexAccessor.Stride;
            indices[index] = indexAccessor.ComponentType switch
            {
                5123 => BitConverter.ToUInt16(buffer, offset),
                5125 => BitConverter.ToUInt32(buffer, offset),
                _ => throw new InvalidDataException(
                    $"Unsupported glTF index component type {indexAccessor.ComponentType}.")
            };
        }

        return (vertices, indices);
    }

    private static Accessor GetAccessor(JsonElement root, int accessorIndex)
    {
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        var componentType = accessor.GetProperty("componentType").GetInt32();
        var components = accessor.GetProperty("type").GetString() switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            var type => throw new InvalidDataException($"Unsupported glTF accessor type {type}.")
        };
        var componentSize = componentType switch
        {
            5123 => sizeof(ushort),
            5125 => sizeof(uint),
            5126 => sizeof(float),
            _ => throw new InvalidDataException($"Unsupported glTF component type {componentType}.")
        };
        var viewOffset = view.TryGetProperty("byteOffset", out var viewOffsetElement)
            ? viewOffsetElement.GetInt32()
            : 0;
        var accessorOffset = accessor.TryGetProperty("byteOffset", out var accessorOffsetElement)
            ? accessorOffsetElement.GetInt32()
            : 0;
        var stride = view.TryGetProperty("byteStride", out var strideElement)
            ? strideElement.GetInt32()
            : componentSize * components;

        return new Accessor(
            viewOffset + accessorOffset,
            stride,
            accessor.GetProperty("count").GetInt32(),
            componentType);
    }

    private static Vector2 ReadVector2(byte[] buffer, Accessor accessor, int index)
    {
        var offset = accessor.Offset + index * accessor.Stride;
        return new Vector2(
            BitConverter.ToSingle(buffer, offset),
            BitConverter.ToSingle(buffer, offset + sizeof(float)));
    }

    private static Vector3 ReadVector3(byte[] buffer, Accessor accessor, int index)
    {
        var offset = accessor.Offset + index * accessor.Stride;
        return new Vector3(
            BitConverter.ToSingle(buffer, offset),
            BitConverter.ToSingle(buffer, offset + sizeof(float)),
            BitConverter.ToSingle(buffer, offset + sizeof(float) * 2));
    }

    private static Vector4 ReadVector4(byte[] buffer, Accessor accessor, int index)
    {
        var offset = accessor.Offset + index * accessor.Stride;
        return new Vector4(
            BitConverter.ToSingle(buffer, offset),
            BitConverter.ToSingle(buffer, offset + sizeof(float)),
            BitConverter.ToSingle(buffer, offset + sizeof(float) * 2),
            BitConverter.ToSingle(buffer, offset + sizeof(float) * 3));
    }

    private readonly record struct Accessor(int Offset, int Stride, int Count, int ComponentType);
}
