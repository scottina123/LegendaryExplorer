using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using SharpDXException = SharpDX.SharpDXException;

namespace LegendaryExplorer.Lightmaps.Solver;

/// <summary>
/// The parts of a solve that depend only on the scene: its triangles and BVH, materials and textures, and lights, packed for the GPU. A level's
/// bake solves its mappings in batches against the same scene, so a <see cref="SolverSession"/> packs it once
/// </summary>
internal sealed class PackedScene
{
    public GpuTri[] Tris;
    public GpuBvhNode[] Nodes;
    public UInt4[] TriVerts;
    public GpuVert[] Verts;
    public GpuMaterial[] Materials;
    public Vector4[] Texels;
    public GpuLight[] Lights;
    public uint[] BounceLights;
    public uint NumBounceLights;
    public bool HasTranslucency;
    public Vector3 SkyUpper, SkyLower;
    /// <summary>The bounds of the scene's vertices (min above max when there are none)</summary>
    public Vector3 VertexMin, VertexMax;

    /// <summary>Most texels the material buffer holds (1 GB of float4s); past it, the largest textures are halved until they fit</summary>
    const long TexelBudget = 1L << 26;

    public static PackedScene Build(SolverScene scene, Action<string> report)
    {
        var packed = new PackedScene();
        packed.PackGeometry(scene, report);
        packed.PackLights(scene);
        return packed;
    }

    void PackGeometry(SolverScene scene, Action<string> report)
    {
        var vertsIn = scene.Vertices ?? [];
        var trisIn = scene.Triangles ?? [];
        var matsIn = scene.Materials ?? [];

        // materials: every texture into one texel buffer
        var textures = PackTextures(matsIn);
        Materials = new GpuMaterial[matsIn.Count + 1]; // the last is the default for out-of-range indices
        for (int m = 0; m < matsIn.Count; m++)
        {
            var mat = matsIn[m] ?? new SolverMaterial();
            var gm = new GpuMaterial
            {
                Flags = (mat.TwoSided ? GpuMaterial.TwoSided : 0) | (mat.Masked ? GpuMaterial.Masked : 0),
                Clip = mat.OpacityMaskClipValue,
            };
            (gm.DiffuseOffset, gm.DiffuseSize) = Lookup(textures, mat.Diffuse);
            (gm.EmissiveOffset, gm.EmissiveSize) = Lookup(textures, mat.Emissive);
            (gm.TransmissionOffset, gm.TransmissionSize) = Lookup(textures, mat.Transmission);
            Materials[m] = gm;
        }
        Materials[matsIn.Count] = new GpuMaterial { Clip = 0.3333f };

        Verts = new GpuVert[vertsIn.Count];
        for (int i = 0; i < Verts.Length; i++)
            Verts[i] = new GpuVert { Normal = SafeNormalize(vertsIn[i].Normal, Vector3.UnitZ), UV = vertsIn[i].MaterialUV };

        int n = trisIn.Count;
        var tmin = new Vector3[n];
        var tmax = new Vector3[n];
        var unordered = new GpuTri[n];
        var triVerts = new UInt4[Math.Max(1, n)];
        int translucent = 0;
        Parallel.For(0, n, () => 0, (i, _, local) =>
        {
            var t = trisIn[i];
            if (t.V0 >= vertsIn.Count || t.V1 >= vertsIn.Count || t.V2 >= vertsIn.Count)
                throw new ArgumentException($"Triangle {i} references a vertex that doesn't exist.");
            Vector3 p0 = vertsIn[(int)t.V0].Position, p1 = vertsIn[(int)t.V1].Position, p2 = vertsIn[(int)t.V2].Position;
            tmin[i] = Vector3.Min(p0, Vector3.Min(p1, p2));
            tmax[i] = Vector3.Max(p0, Vector3.Max(p1, p2));
            uint matIndex = t.Material < (uint)matsIn.Count ? t.Material : (uint)matsIn.Count;
            var mat = t.Material < (uint)matsIn.Count ? matsIn[(int)t.Material] : null;
            uint flags = matIndex & GpuTri.MaterialMask;
            if ((t.Flags & SolverTriangleFlags.CastShadow) != 0) flags |= GpuTri.CastShadow;
            if ((t.Flags & SolverTriangleFlags.TwoSidedShadow) != 0) flags |= GpuTri.TwoSidedShadow;
            if ((t.Flags & SolverTriangleFlags.SelfShadowOnly) != 0) flags |= GpuTri.SelfShadowOnly;
            if ((t.Flags & SolverTriangleFlags.ShadowIndirectOnly) != 0) flags |= GpuTri.ShadowIndirectOnly;
            if (mat?.TwoSided == true) flags |= GpuTri.MaterialTwoSided;
            if (mat?.Transmission is { Size: > 0 }) flags |= GpuTri.Transmissive;
            Vector3 e1 = p1 - p0, e2 = p2 - p0;
            Vector3 nsum = vertsIn[(int)t.V0].Normal + vertsIn[(int)t.V1].Normal + vertsIn[(int)t.V2].Normal;
            if (Vector3.Dot(Vector3.Cross(e1, e2), nsum) < 0) flags |= GpuTri.Flipped;
            unordered[i] = new GpuTri { V0 = p0, E1 = e1, E2 = e2, Flags = flags, Instance = t.Instance, Orig = (uint)i };
            triVerts[i] = new UInt4(t.V0, t.V1, t.V2, 0);
            if ((flags & GpuTri.Transmissive) != 0 && (flags & GpuTri.CastShadow) != 0 && mat is { Masked: false })
                local++;
            return local;
        }, local => Interlocked.Add(ref translucent, local));
        TriVerts = triVerts;
        HasTranslucency = translucent > 0;

        report?.Invoke("Building BVH");
        var bvh = SolverBvh.Build(tmin, tmax);
        Nodes = bvh.Nodes;
        Tris = new GpuTri[Math.Max(1, n)];
        for (int i = 0; i < n; i++)
            Tris[i] = unordered[bvh.TriangleOrder[i]];

        Vector3 bmin = new(float.MaxValue), bmax = new(float.MinValue);
        foreach (var v in vertsIn)
        {
            bmin = Vector3.Min(bmin, v.Position);
            bmax = Vector3.Max(bmax, v.Position);
        }
        (VertexMin, VertexMax) = (bmin, bmax);
    }

    static (uint Offset, uint Size) Lookup(Dictionary<SolverTexture, (uint, uint)> textures, SolverTexture tex) =>
        tex != null && textures.TryGetValue(tex, out var placed) ? placed : (0, 0);

    /// <summary>Lays out every distinct texture (shared ones once) in the texel buffer</summary>
    Dictionary<SolverTexture, (uint, uint)> PackTextures(List<SolverMaterial> matsIn)
    {
        var distinct = new List<SolverTexture>();
        var seen = new HashSet<SolverTexture>(ReferenceEqualityComparer.Instance);
        foreach (var mat in matsIn)
            foreach (var tex in new[] { mat?.Diffuse, mat?.Emissive, mat?.Transmission })
                if (tex?.Texels != null && tex.Size > 0 && tex.Texels.Length >= tex.Size * tex.Size && seen.Add(tex))
                    distinct.Add(tex);

        // the texels each texture goes in with, downsampled if the whole set is over budget
        var data = new (int Size, Vector4[] Texels)[distinct.Count];
        long total = 0;
        for (int i = 0; i < distinct.Count; i++)
        {
            data[i] = (distinct[i].Size, distinct[i].Texels);
            total += (long)distinct[i].Size * distinct[i].Size;
        }
        while (total > TexelBudget)
        {
            int largest = 0;
            for (int i = 1; i < data.Length; i++)
                if (data[i].Size > data[largest].Size)
                    largest = i;
            if (data[largest].Size <= 1)
                break;
            int size = data[largest].Size, half = size / 2;
            var src = data[largest].Texels;
            var dst = new Vector4[half * half];
            for (int y = 0; y < half; y++)
                for (int x = 0; x < half; x++)
                    dst[y * half + x] = (src[2 * y * size + 2 * x] + src[2 * y * size + 2 * x + 1] + src[(2 * y + 1) * size + 2 * x] + src[(2 * y + 1) * size + 2 * x + 1]) * 0.25f;
            total -= (long)size * size - (long)half * half;
            data[largest] = (half, dst);
        }

        var placed = new Dictionary<SolverTexture, (uint, uint)>(ReferenceEqualityComparer.Instance);
        Texels = new Vector4[Math.Max(1, total)];
        long offset = 0;
        for (int i = 0; i < data.Length; i++)
        {
            int count = data[i].Size * data[i].Size;
            Array.Copy(data[i].Texels, 0, Texels, offset, count);
            placed[distinct[i]] = ((uint)offset, (uint)data[i].Size);
            offset += count;
        }
        return placed;
    }

    void PackLights(SolverScene scene)
    {
        var lightsIn = scene.Lights ?? [];
        Lights = new GpuLight[Math.Max(1, lightsIn.Count)];
        var bounce = new List<uint>();
        SkyUpper = SkyLower = scene.Environment;
        for (int i = 0; i < lightsIn.Count; i++)
        {
            var l = lightsIn[i];
            if (l == null)
                continue;
            if (l.Type == SolverLightType.Sky)
            {
                SkyUpper += l.Color;
                SkyLower += l.LowerColor;
                continue;
            }
            Vector3 ind = l.Color * l.IndirectLightingScale;
            float grey = Vector3.Dot(ind, new Vector3(0.3f, 0.59f, 0.11f));
            ind = Vector3.Lerp(new Vector3(grey), ind, l.IndirectLightingSaturation);
            ind = Vector3.Max(ind, Vector3.Zero);
            Lights[i] = new GpuLight
            {
                Position = l.Position,
                Type = (uint)l.Type,
                Direction = SafeNormalize(l.Direction, -Vector3.UnitZ),
                Radius = l.Radius,
                Color = l.Color,
                FalloffExponent = l.FalloffExponent,
                IndirectColor = ind,
                CosInner = l.CosInnerCone,
                CosOuter = l.CosOuterCone,
                SourceSize = Math.Max(0, l.SourceSize),
                CastShadows = l.CastShadows ? 1u : 0u,
                ShadowExponent = l.ShadowExponent > 0 ? l.ShadowExponent : 1,
            };
            if (l.Bounces && Vector3.Dot(ind, Vector3.One) > 0)
                bounce.Add((uint)i);
        }
        BounceLights = bounce.Count > 0 ? bounce.ToArray() : [0u];
        NumBounceLights = (uint)bounce.Count;
    }

    internal static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        float l = v.Length();
        return l > 1e-12f && float.IsFinite(l) ? v / l : fallback;
    }
}

/// <summary>A packed scene's buffers on a device</summary>
internal sealed class SceneBuffers : IDisposable
{
    public readonly GpuBuffer Nodes, Tris, TriVerts, Verts, Materials, Texels, Lights, BounceLights;
    /// <summary>With hardware ray tracing, the scene's acceleration structure</summary>
    readonly IDisposable accelerationStructure;

    public SceneBuffers(SolverGpu d3d, PackedScene packed)
    {
        accelerationStructure = d3d.BuildScene(packed);
        Nodes = d3d.CreateStructured(packed.Nodes, packed.Nodes.Length);
        Tris = d3d.CreateStructured(packed.Tris, packed.Tris.Length);
        TriVerts = d3d.CreateStructured(packed.TriVerts, packed.TriVerts.Length);
        Verts = d3d.CreateStructured(packed.Verts, packed.Verts.Length);
        Materials = d3d.CreateStructured(packed.Materials, packed.Materials.Length);
        Texels = d3d.CreateStructured(packed.Texels, packed.Texels.Length);
        Lights = d3d.CreateStructured(packed.Lights, packed.Lights.Length);
        BounceLights = d3d.CreateStructured(packed.BounceLights, packed.BounceLights.Length);
    }

    public void Dispose()
    {
        Nodes.Dispose(); Tris.Dispose(); TriVerts.Dispose(); Verts.Dispose(); Materials.Dispose(); Texels.Dispose(); Lights.Dispose(); BounceLights.Dispose();
        accelerationStructure?.Dispose();
    }
}

/// <summary>
/// A bake's solver state for one scene: the scene packed once, and one device with the scene's buffers on it, shared by every batch the bake
/// solves (<see cref="LightSolver.Begin"/>, or <see cref="LightSolver.Solve(SolverJob, SolverSession, IProgress{SolverProgress}, CancellationToken)"/>).
/// The device does one batch's passes at a time; batches prepare and finish on the CPU alongside
/// </summary>
public sealed class SolverSession : IDisposable
{
    internal readonly SolverScene Scene;
    internal readonly SolverDevice Device;
    readonly object packLock = new();
    internal readonly object DeviceLock = new();
    PackedScene packed;
    SolverGpu d3d;
    SceneBuffers buffers;
    /// <summary>Why the device isn't the fastest kind (no hardware ray tracing), if so</summary>
    internal string DeviceNote;
    bool disposed;

    internal SolverSession(SolverScene scene, SolverDevice device)
    {
        Scene = scene ?? throw new ArgumentNullException(nameof(scene));
        Device = device;
    }

    /// <summary>The packed scene, built by the first batch that needs it</summary>
    internal PackedScene Packed(Action<string> report)
    {
        lock (packLock)
            return packed ??= PackedScene.Build(Scene, report);
    }

    /// <summary>The device and the scene's buffers on it, created on first use. Call with <see cref="DeviceLock"/> held</summary>
    internal (SolverGpu D3d, SceneBuffers Buffers) Gpu(PackedScene scene)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        d3d ??= SolverGpu.Create(Device, note => DeviceNote = note);
        buffers ??= new SceneBuffers(d3d, scene);
        return (d3d, buffers);
    }

    /// <summary>Drops the device after it failed (the next batch makes a new one)</summary>
    internal void ResetGpu()
    {
        buffers?.Dispose();
        d3d?.Dispose();
        (buffers, d3d) = (null, null);
    }

    public void Dispose()
    {
        lock (DeviceLock)
        {
            disposed = true;
            ResetGpu();
        }
    }
}

/// <summary>
/// One batch of a session (<see cref="LightSolver.Begin"/>), in three steps that can overlap with other batches': <see cref="Prepare"/> (CPU),
/// <see cref="RunOnDevice"/> (the device's passes, one batch at a time) and <see cref="Finish"/> (CPU). The result is the same as a single
/// <see cref="LightSolver.Solve(SolverJob, IProgress{SolverProgress}, CancellationToken)"/>
/// </summary>
public sealed class SolverBatch
{
    readonly SolverRun run;
    internal SolverBatch(SolverRun run) => this.run = run;

    /// <summary>Lays out the batch's samples and lights for the device</summary>
    public void Prepare() => run.Prepare();

    /// <summary>Traces the batch's direct and bounced light on the session's device</summary>
    public void RunOnDevice() => run.RunOnDevice();

    /// <summary>Filters the bounced light and combines the results</summary>
    public SolverOutput Finish() => run.Finish();
}
