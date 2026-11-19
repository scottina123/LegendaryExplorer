using System;
using System.Linq;
using System.Runtime.CompilerServices;

namespace LegendaryExplorer.Lightmaps.Solver;

/// <summary>The solver's kernels (entry points in <see cref="SolverShaders.Source"/>)</summary>
internal enum SolverKernel { Direct, Gather, Reduce, Emit, Irradiance }

/// <summary>A structured buffer on the solver's device</summary>
internal abstract class GpuBuffer : IDisposable
{
    public int Stride, Count;
    public abstract void Dispose();
}

/// <summary>A CPU-readable buffer that device buffers are copied into to read them back</summary>
internal abstract class GpuStaging : IDisposable
{
    public abstract void Dispose();
}

/// <summary>
/// The solver's compute device: Direct3D 11 (a GPU or WARP, the BVH traversed in the kernels) or Direct3D 12 with hardware ray tracing (inline
/// DXR 1.1: the rays traced by the GPU's ray tracing units). Both run the same kernels and give the same lighting
/// </summary>
internal abstract class SolverGpu : IDisposable
{
    public string Name;
    public bool IsWarp;
    /// <summary>Whether rays are traced with the GPU's ray tracing hardware (the scene's acceleration structure, <see cref="BuildScene"/>)</summary>
    public bool HardwareRayTracing;

    public abstract GpuBuffer CreateStructured<T>(T[] data, int count, bool uav = false) where T : unmanaged;
    public abstract GpuStaging CreateStaging(int bytes);
    /// <summary>Writes the first count elements of data to the start of a buffer (in order with the queued work)</summary>
    public abstract void Upload<T>(GpuBuffer target, T[] data, int count) where T : unmanaged;
    /// <summary>Queues a copy of bytes of a buffer, from firstByte, to a staging buffer, to read with <see cref="ReadStaging"/></summary>
    public abstract void CopyToStaging(GpuBuffer source, GpuStaging staging, int bytes, int firstByte = 0);
    /// <summary>Reads count elements of a staging buffer into destination[destinationIndex..], waiting for the copy into it</summary>
    public abstract void ReadStaging<T>(GpuStaging staging, T[] destination, int count, int destinationIndex = 0) where T : unmanaged;
    /// <summary>Submits the queued work and waits until the device has finished it</summary>
    public abstract void Wait();
    /// <summary>Runs a kernel over threads with the constants and buffers bound (t0.. and u0..; null binds nothing)</summary>
    public abstract void Dispatch(SolverKernel kernel, GpuParams p, GpuBuffer[] srvs, GpuBuffer[] uavs, int threads);
    /// <summary>Hardware ray tracing: builds the scene's acceleration structure over the packed triangles (in their order) and traces against it from now on</summary>
    public virtual IDisposable BuildScene(PackedScene packed) => null;
    public abstract void Dispose();

    /// <summary>Copies the first count elements of a buffer back to the CPU (waiting for the device to finish)</summary>
    public void Download<T>(GpuBuffer source, GpuStaging staging, T[] destination, int count) where T : unmanaged
    {
        if (count <= 0) return;
        CopyToStaging(source, staging, count * Unsafe.SizeOf<T>());
        ReadStaging(staging, destination, count);
    }

    /// <summary>Copies count elements starting at element first of a buffer into destination[destinationIndex..] (staging must hold count elements)</summary>
    public void DownloadRange<T>(GpuBuffer source, GpuStaging staging, T[] destination, int destinationIndex, int first, int count) where T : unmanaged
    {
        if (count <= 0) return;
        int size = Unsafe.SizeOf<T>();
        CopyToStaging(source, staging, count * size, first * size);
        ReadStaging(staging, destination, count, destinationIndex);
    }

    /// <summary>
    /// The solver's device of the kind asked for. A GPU traces with its ray tracing hardware when it has DXR 1.1 and the DXC compiler is at hand,
    /// and otherwise (or when that fails to start) with the compute BVH on Direct3D 11. <paramref name="note"/> hears why it fell back
    /// </summary>
    public static SolverGpu Create(SolverDevice kind, Action<string> note = null)
    {
        if (kind == SolverDevice.Gpu && !DebugTokens().Contains("nohwrt"))
        {
            try
            {
                if (SolverD3D12.TryCreate(out var d3d12, out string why)) return d3d12;
                note?.Invoke($"Hardware ray tracing isn't available ({why}); the GPU traces rays with compute shaders.");
            }
            catch (Exception e)
            {
                note?.Invoke($"Hardware ray tracing couldn't start ({e.Message}); the GPU traces rays with compute shaders.");
            }
        }
        return SolverD3D.Create(kind);
    }

    internal static string[] DebugTokens() => System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',') ?? [];

    /// <summary>The kernels' source, with any tuning switches applied. Tuning against UDK: LEX_SOLVER_DEBUG=nopi drops the 1/pi of reflected light, pibounce scales each bounce's throughput by pi</summary>
    internal static string DebugSource()
    {
        string source = SolverShaders.Source;
        var debug = DebugTokens();
        if (debug.Contains("nopi")) source = source.Replace("L += thr * albedo * (1.0 / PI) * E;", "L += thr * albedo * E;");
        // gatheroffset: gather rays leave from NormalOffset off the surface (direct shadow rays keep the texel-based offset)
        if (debug.Contains("gatheroffset")) source = source.Replace("float3 o = s.pos + s.ng * SampleOffset(s);", "float3 o = s.pos + s.ng * NormalOffset;");
        if (debug.Contains("gatherthrough"))
            source = source.Replace("if (Classify(tri, u, v, front, fromInstance, true, false, tr) != 1) continue;",
                "if (Classify(tri, u, v, front, fromInstance, true, false, tr) != 1) continue; if (!front && (Materials[tri.flags & TF_MATERIAL_MASK].flags & MF_TWO_SIDED) == 0) continue;");
        // bounceflat: bounce light without the radial falloff inside the radius; bounceinvsq: (R/d)^2 capped at 1, no radius cut-off
        string bounceAtten = debug.Contains("bounceflat") ? "float BounceAtten(Light L, float3 p) { if (L.type == LIGHT_DIRECTIONAL) return 1.0; float3 v = p - L.pos; if (dot(v, v) >= L.radius * L.radius) return 0.0; float a = 1.0; if (L.type == LIGHT_SPOT) { float d2 = dot(v, v); float c = d2 > 0 ? dot(v * rsqrt(d2), L.dir) : 1.0; float denom = L.cosInner - L.cosOuter; float s = denom > 1e-6 ? saturate((c - L.cosOuter) / denom) : (c >= L.cosOuter ? 1.0 : 0.0); a *= s * s; } return a; }"
            : debug.Contains("bounceinvsq") ? "float BounceAtten(Light L, float3 p) { if (L.type == LIGHT_DIRECTIONAL) return 1.0; float3 v = p - L.pos; float d2 = max(dot(v, v), 1.0); float a = min(1.0, L.radius * L.radius / d2); if (L.type == LIGHT_SPOT) { float c = dot(v * rsqrt(d2), L.dir); float denom = L.cosInner - L.cosOuter; float s = denom > 1e-6 ? saturate((c - L.cosOuter) / denom) : (c >= L.cosOuter ? 1.0 : 0.0); a *= s * s; } return a; }"
            : debug.Contains("spotomni") ? "float BounceAtten(Light L, float3 p) { if (L.type == LIGHT_DIRECTIONAL) return 1.0; float3 v = p - L.pos; float d2 = dot(v, v); float r2 = L.radius * L.radius; if (!(d2 < r2)) return 0.0; return pow(max(1.0 - d2 / r2, 0.0), L.falloff); }"
            : debug.FirstOrDefault(t => t.StartsWith("bouncefalloff:")) is { } bf
                ? "float BounceAtten(Light L, float3 p) { if (L.type == LIGHT_DIRECTIONAL) return 1.0; float3 v = p - L.pos; float d2 = dot(v, v); float r2 = L.radius * L.radius; if (!(d2 < r2)) return 0.0; float a = pow(max(1.0 - d2 / r2, 0.0), " + bf[14..] + "); if (L.type == LIGHT_SPOT) { float c = d2 > 0 ? dot(v * rsqrt(d2), L.dir) : 1.0; float denom = L.cosInner - L.cosOuter; float s = denom > 1e-6 ? saturate((c - L.cosOuter) / denom) : (c >= L.cosOuter ? 1.0 : 0.0); a *= s * s; } return a; }"
            : null;
        if (bounceAtten != null)
        {
            // bouncemaxradius:R: lights with a bigger radius keep their own falloff for bounced light
            if (debug.FirstOrDefault(t => t.StartsWith("bouncemaxradius:")) is { } bmr)
                bounceAtten = bounceAtten.Replace("float BounceAtten(Light L, float3 p) {", "float BounceAtten(Light L, float3 p) { if (L.radius > " + bmr[16..] + ") return Attenuation(L, p);");
            // bouncefirstonly: the bounce falloff applies at the first bounce only; later bounces use the light's own falloff
            if (debug.Contains("bouncefirstonly"))
            {
                bounceAtten = "static bool gFirstBounce = true; " + bounceAtten.Replace("float BounceAtten(Light L, float3 p) {", "float BounceAtten(Light L, float3 p) { if (!gFirstBounce) return Attenuation(L, p);");
                source = source.Replace("float3 E = NextEventEstimate(po, ns, ngTri, tri.instance, h.tri, state, rays);",
                    "gFirstBounce = bounce == 0; float3 E = NextEventEstimate(po, ns, ngTri, tri.instance, h.tri, state, rays);");
            }
            source = source.Replace("float SampleOffset(Sample s)", bounceAtten + " float SampleOffset(Sample s)");
            source = source.Replace("float att = Attenuation(L, p);", "float att = BounceAtten(L, p);");
            source = source.Replace("L.indColor * (Attenuation(L, po) * cosN)", "L.indColor * (BounceAtten(L, po) * cosN)");
        }
        if (debug.Contains("pibounce")) source = source.Replace("thr *= albedo;", "thr *= albedo * PI;");
        return source;
    }

    protected static string EntryPoint(SolverKernel kernel) => kernel switch
    {
        SolverKernel.Direct => "DirectCS",
        SolverKernel.Gather => "GatherCS",
        SolverKernel.Reduce => "ReduceCS",
        SolverKernel.Emit => "EmitCS",
        _ => "IrradianceCS",
    };
}
