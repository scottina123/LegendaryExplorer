using System;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3DBuffer = SharpDX.Direct3D11.Buffer;
using D3DDevice = SharpDX.Direct3D11.Device;
using DxgiDevice = SharpDX.DXGI.Device;

namespace LegendaryExplorer.Lightmaps.Solver;

/// <summary>A Direct3D 11 structured buffer with its views</summary>
internal sealed class D3D11Buffer : GpuBuffer
{
    public D3DBuffer Buffer;
    public ShaderResourceView Srv;
    public UnorderedAccessView Uav;

    public override void Dispose()
    {
        Srv?.Dispose();
        Uav?.Dispose();
        Buffer?.Dispose();
    }
}

internal sealed class D3D11Staging(D3DBuffer buffer) : GpuStaging
{
    public readonly D3DBuffer Buffer = buffer;
    public override void Dispose() => Buffer.Dispose();
}

/// <summary>The solver on a Direct3D 11 device (hardware or WARP): its compiled kernels, which traverse the scene's BVH themselves</summary>
internal sealed class SolverD3D : SolverGpu
{
    public D3DDevice Device;
    public DeviceContext Context;
    ComputeShader[] kernels;
    D3DBuffer constants;
    Query fence;

    static readonly object compileLock = new();
    static byte[][] bytecode;

    public static SolverD3D Create(SolverDevice kind)
    {
        var d3d = new SolverD3D { IsWarp = kind == SolverDevice.Cpu };
        try
        {
            if (kind == SolverDevice.Cpu)
            {
                d3d.Device = new D3DDevice(DriverType.Warp, DeviceCreationFlags.None, FeatureLevel.Level_11_0);
            }
            else
            {
                using var adapter = PickHardwareAdapter() ?? throw new SolverUnavailableException("No Direct3D 11 GPU was found.");
                d3d.Device = new D3DDevice(adapter, DeviceCreationFlags.None, FeatureLevel.Level_11_0);
            }
            if (d3d.Device.FeatureLevel < FeatureLevel.Level_11_0)
                throw new SolverUnavailableException("The Direct3D device doesn't support feature level 11_0.");
            d3d.Context = d3d.Device.ImmediateContext;
            using (var dxgi = d3d.Device.QueryInterface<DxgiDevice>())
            using (var adapter = dxgi.Adapter)
                d3d.Name = adapter.Description.Description?.TrimEnd('\0');
            if (d3d.IsWarp)
                d3d.Name = $"WARP ({d3d.Name})";
            d3d.fence = new Query(d3d.Device, new QueryDescription { Type = QueryType.Event });
            var code = EnsureCompiled();
            d3d.kernels = new ComputeShader[code.Length];
            for (int i = 0; i < code.Length; i++)
                d3d.kernels[i] = new ComputeShader(d3d.Device, code[i]);
            d3d.constants = new D3DBuffer(d3d.Device, new BufferDescription
            {
                SizeInBytes = (Marshal.SizeOf<GpuParams>() + 15) & ~15,
                BindFlags = BindFlags.ConstantBuffer,
                Usage = ResourceUsage.Default,
            });
            return d3d;
        }
        catch (SolverUnavailableException)
        {
            d3d.Dispose();
            throw;
        }
        catch (Exception e)
        {
            d3d.Dispose();
            throw new SolverUnavailableException($"Couldn't create the {(kind == SolverDevice.Cpu ? "WARP" : "GPU")} light solver device: {e.Message}", e);
        }
    }

    /// <summary>The hardware adapter with the most dedicated video memory, skipping software ones (WARP, the Basic Render Driver)</summary>
    public static Adapter1 PickHardwareAdapter()
    {
        using var factory = new Factory1();
        Adapter1 best = null;
        long bestMemory = -1;
        int count = factory.GetAdapterCount1();
        for (int i = 0; i < count; i++)
        {
            var adapter = factory.GetAdapter1(i);
            var desc = adapter.Description1;
            bool software = (desc.Flags & AdapterFlags.Software) != 0 || (desc.VendorId == 0x1414 && desc.DeviceId == 0x8C);
            long memory = (long)desc.DedicatedVideoMemory;
            if (software || memory <= bestMemory)
            {
                adapter.Dispose();
                continue;
            }
            if (D3DDevice.GetSupportedFeatureLevel(adapter) < FeatureLevel.Level_11_0)
            {
                adapter.Dispose();
                continue;
            }
            best?.Dispose();
            best = adapter;
            bestMemory = memory;
        }
        return best;
    }

    static byte[][] EnsureCompiled()
    {
        lock (compileLock)
        {
            if (bytecode != null)
                return bytecode;
            var kinds = Enum.GetValues<SolverKernel>();
            var code = new byte[kinds.Length][];
            for (int i = 0; i < kinds.Length; i++)
                code[i] = Compile(EntryPoint(kinds[i]));
            return bytecode = code;
        }
    }

    static byte[] Compile(string entry)
    {
        try
        {
            using var result = ShaderBytecode.Compile(DebugSource(), entry, "cs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None, "LightSolver.hlsl");
            if (result.HasErrors || result.Bytecode == null)
                throw new SolverUnavailableException($"The light solver's {entry} kernel didn't compile: {result.Message}");
            return result.Bytecode.Data;
        }
        catch (CompilationException e)
        {
            throw new SolverUnavailableException($"The light solver's {entry} kernel didn't compile: {e.Message}", e);
        }
    }

    public override GpuBuffer CreateStructured<T>(T[] data, int count, bool uav = false)
    {
        int stride = Marshal.SizeOf<T>();
        count = Math.Max(1, count);
        var desc = new BufferDescription
        {
            SizeInBytes = checked(stride * count),
            BindFlags = BindFlags.ShaderResource | (uav ? BindFlags.UnorderedAccess : BindFlags.None),
            Usage = ResourceUsage.Default,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = stride,
        };
        var gb = new D3D11Buffer { Stride = stride, Count = count };
        if (data != null && data.Length >= count)
            gb.Buffer = D3DBuffer.Create(Device, data, desc);
        else if (data != null && data.Length > 0)
        {
            var padded = new T[count];
            Array.Copy(data, padded, data.Length);
            gb.Buffer = D3DBuffer.Create(Device, padded, desc);
        }
        else
            gb.Buffer = new D3DBuffer(Device, desc);
        gb.Srv = new ShaderResourceView(Device, gb.Buffer, new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            Dimension = ShaderResourceViewDimension.ExtendedBuffer,
            BufferEx = new ShaderResourceViewDescription.ExtendedBufferResource { FirstElement = 0, ElementCount = count },
        });
        if (uav)
        {
            gb.Uav = new UnorderedAccessView(Device, gb.Buffer, new UnorderedAccessViewDescription
            {
                Format = Format.Unknown,
                Dimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new UnorderedAccessViewDescription.BufferResource { FirstElement = 0, ElementCount = count, Flags = UnorderedAccessViewBufferFlags.None },
            });
        }
        return gb;
    }

    public override GpuStaging CreateStaging(int bytes) => new D3D11Staging(new D3DBuffer(Device, new BufferDescription
    {
        SizeInBytes = Math.Max(16, bytes),
        BindFlags = BindFlags.None,
        Usage = ResourceUsage.Staging,
        CpuAccessFlags = CpuAccessFlags.Read,
        OptionFlags = ResourceOptionFlags.None,
    }));

    public override unsafe void Upload<T>(GpuBuffer target, T[] data, int count)
    {
        if (count <= 0)
            return;
        fixed (T* p = data)
            Context.UpdateSubresource(new DataBox((IntPtr)p), ((D3D11Buffer)target).Buffer, 0, new ResourceRegion(0, 0, 0, count * sizeof(T), 1, 1));
    }

    public override void CopyToStaging(GpuBuffer source, GpuStaging staging, int bytes, int firstByte = 0)
    {
        if (bytes > 0)
            Context.CopySubresourceRegion(((D3D11Buffer)source).Buffer, 0, new ResourceRegion(firstByte, 0, 0, firstByte + bytes, 1, 1), ((D3D11Staging)staging).Buffer, 0);
    }

    public override unsafe void ReadStaging<T>(GpuStaging staging, T[] destination, int count, int destinationIndex = 0)
    {
        if (count <= 0)
            return;
        var buffer = ((D3D11Staging)staging).Buffer;
        var box = Context.MapSubresource(buffer, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
        try
        {
            fixed (T* p = &destination[destinationIndex])
                System.Buffer.MemoryCopy((void*)box.DataPointer, p, (long)(destination.Length - destinationIndex) * sizeof(T), (long)count * sizeof(T));
        }
        finally
        {
            Context.UnmapSubresource(buffer, 0);
        }
    }

    public override void Dispatch(SolverKernel kernel, GpuParams p, GpuBuffer[] srvs, GpuBuffer[] uavs, int threads)
    {
        var ctx = Context;
        ctx.UpdateSubresource(ref p, constants);
        ctx.ComputeShader.Set(kernels[(int)kernel]);
        ctx.ComputeShader.SetConstantBuffer(0, constants);
        var srvViews = new ShaderResourceView[srvs.Length];
        for (int i = 0; i < srvs.Length; i++) srvViews[i] = (srvs[i] as D3D11Buffer)?.Srv;
        var uavViews = new UnorderedAccessView[uavs.Length];
        for (int i = 0; i < uavs.Length; i++) uavViews[i] = (uavs[i] as D3D11Buffer)?.Uav;
        ctx.ComputeShader.SetShaderResources(0, srvViews);
        ctx.ComputeShader.SetUnorderedAccessViews(0, uavViews);
        ctx.Dispatch((threads + SolverShaders.GroupSize - 1) / SolverShaders.GroupSize, 1, 1);
        ctx.ComputeShader.SetUnorderedAccessViews(0, new UnorderedAccessView[uavs.Length]);
    }

    public override void Wait()
    {
        Context.End(fence);
        Context.Flush();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        long nextCheck = 500;
        while (!Context.IsDataAvailable(fence))
        {
            System.Threading.Thread.Yield();
            if (waited.ElapsedMilliseconds < nextCheck)
                continue;
            nextCheck += 500;
            // a device the driver reset never signals the fence
            var removed = Device.DeviceRemovedReason;
            if (removed.Failure)
                throw new SharpDXException(removed);
        }
    }

    public override void Dispose()
    {
        if (kernels != null)
            foreach (var k in kernels) k?.Dispose();
        constants?.Dispose();
        fence?.Dispose();
        Context?.ClearState();
        Context?.Dispose();
        Device?.Dispose();
    }
}
