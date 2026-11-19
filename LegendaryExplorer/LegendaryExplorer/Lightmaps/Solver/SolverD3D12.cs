using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace LegendaryExplorer.Lightmaps.Solver;

/// <summary>A Direct3D 12 buffer in the default heap, with the state it was last put in</summary>
internal sealed unsafe class D3D12Buffer : GpuBuffer
{
    public ID3D12Resource* Resource;
    public D3D12_RESOURCE_STATES State;
    public ulong Address;

    public override void Dispose()
    {
        if (Resource != null) Resource->Release();
        Resource = null;
    }
}

/// <summary>A Direct3D 12 readback buffer, and the fence value its last copy completes at</summary>
internal sealed unsafe class D3D12Staging : GpuStaging
{
    public ID3D12Resource* Resource;
    public ulong Bytes;
    public ulong Fence;

    public override void Dispose()
    {
        if (Resource != null) Resource->Release();
        Resource = null;
    }
}

/// <summary>
/// The solver on Direct3D 12 with hardware ray tracing: the same kernels, compiled with DXC for shader model 6.5 with HW_RT defined, so they trace
/// their rays with inline ray queries against the scene's acceleration structure (the GPU's ray tracing units) instead of walking the BVH. Buffers
/// are bound as root descriptors (b0, t0..t14, u0..u5), so no descriptor heaps are needed; resource states are tracked explicitly
/// </summary>
internal sealed unsafe class SolverD3D12 : SolverGpu
{
    const int SrvCount = 15, UavCount = 6, Allocators = 3;
    const ulong RingBytes = 128UL << 20;

    ID3D12Device5* device;
    ID3D12CommandQueue* queue;
    ID3D12Fence* fence;
    HANDLE fenceEvent;
    ulong fenceValue;
    readonly ID3D12CommandAllocator*[] allocators = new ID3D12CommandAllocator*[Allocators];
    readonly ulong[] allocatorFence = new ulong[Allocators];
    int allocator;
    ID3D12GraphicsCommandList4* list;
    bool recording;
    ID3D12RootSignature* rootSignature;
    ID3D12PipelineState*[] pipelines;
    readonly List<D3D12_RESOURCE_BARRIER> barriers = [];

    // upload ring: chunk data and constants, reused once the device is past them
    ID3D12Resource* ring;
    byte* ringMapped;
    ulong ringAddress, ringHead;

    D3D12Buffer dummySrv, dummyUav;
    ulong sceneAddress;

    static readonly object compileLock = new();
    static byte[][] bytecode;
    static string compileFailure;

    /// <summary>A hardware ray tracing device, or why there's none (no DXR 1.1 GPU, no DXC)</summary>
    public static bool TryCreate(out SolverGpu solver, out string why)
    {
        solver = null;
        var d3d = new SolverD3D12();
        try
        {
            if (!d3d.CreateDevice(out why)) { d3d.Dispose(); return false; }
            var code = EnsureCompiled(out why);
            if (code == null) { d3d.Dispose(); return false; }
            d3d.CreatePipelines(code);
            solver = d3d;
            return true;
        }
        catch
        {
            d3d.Dispose();
            throw;
        }
    }

    static void Check(HRESULT hr, string what)
    {
        if (hr.Value < 0) throw new SolverUnavailableException($"Direct3D 12: {what} failed (0x{hr.Value:X8}).");
    }

    bool CreateDevice(out string why)
    {
        IDXGIFactory6* factory = null;
        Check(CreateDXGIFactory2(0, __uuidof<IDXGIFactory6>(), (void**)&factory), "CreateDXGIFactory2");
        try
        {
            for (uint i = 0; ; i++)
            {
                IDXGIAdapter1* adapter = null;
                if (factory->EnumAdapterByGpuPreference(i, DXGI_GPU_PREFERENCE.DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, __uuidof<IDXGIAdapter1>(), (void**)&adapter).Value < 0)
                    break;
                try
                {
                    DXGI_ADAPTER_DESC1 desc;
                    adapter->GetDesc1(&desc);
                    if ((desc.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;
                    ID3D12Device5* candidate = null;
                    if (D3D12CreateDevice((IUnknown*)adapter, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_12_0, __uuidof<ID3D12Device5>(), (void**)&candidate).Value < 0) continue;
                    D3D12_FEATURE_DATA_D3D12_OPTIONS5 options = default;
                    bool rt = candidate->CheckFeatureSupport(D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS5, &options, (uint)sizeof(D3D12_FEATURE_DATA_D3D12_OPTIONS5)).Value >= 0
                        && options.RaytracingTier >= D3D12_RAYTRACING_TIER.D3D12_RAYTRACING_TIER_1_1;
                    if (!rt)
                    {
                        candidate->Release();
                        continue;
                    }
                    device = candidate;
                    Name = $"{new string(desc.Description).TrimEnd('\0')} (hardware ray tracing)";
                    break;
                }
                finally
                {
                    adapter->Release();
                }
            }
        }
        finally
        {
            factory->Release();
        }
        if (device == null)
        {
            why = "no GPU with DXR 1.1 ray tracing";
            return false;
        }

        var queueDesc = new D3D12_COMMAND_QUEUE_DESC { Type = D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_COMPUTE };
        ID3D12CommandQueue* q;
        Check(device->CreateCommandQueue(&queueDesc, __uuidof<ID3D12CommandQueue>(), (void**)&q), "CreateCommandQueue");
        queue = q;
        ID3D12Fence* f;
        Check(device->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE, __uuidof<ID3D12Fence>(), (void**)&f), "CreateFence");
        fence = f;
        fenceEvent = CreateEventW(null, 0, 0, null);
        for (int i = 0; i < Allocators; i++)
        {
            ID3D12CommandAllocator* a;
            Check(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_COMPUTE, __uuidof<ID3D12CommandAllocator>(), (void**)&a), "CreateCommandAllocator");
            allocators[i] = a;
        }
        ID3D12GraphicsCommandList4* l;
        Check(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_COMPUTE, allocators[0], null, __uuidof<ID3D12GraphicsCommandList4>(), (void**)&l), "CreateCommandList");
        list = l;
        list->Close();

        ring = CreateResource(D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD, RingBytes, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ, false);
        void* mapped;
        Check(ring->Map(0, null, &mapped), "Map");
        ringMapped = (byte*)mapped;
        ringAddress = ring->GetGPUVirtualAddress();

        dummySrv = (D3D12Buffer)CreateStructured<uint>(null, 4);
        dummyUav = (D3D12Buffer)CreateStructured<uint>(null, 4, uav: true);
        why = null;
        return true;
    }

    ID3D12Resource* CreateResource(D3D12_HEAP_TYPE heap, ulong bytes, D3D12_RESOURCE_STATES state, bool uav)
    {
        var heapProps = new D3D12_HEAP_PROPERTIES { Type = heap, CreationNodeMask = 1, VisibleNodeMask = 1 };
        var desc = new D3D12_RESOURCE_DESC
        {
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER,
            Width = Math.Max(256UL, bytes),
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
            Flags = uav ? D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS : D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_NONE,
        };
        ID3D12Resource* resource;
        Check(device->CreateCommittedResource(&heapProps, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &desc, state, null, __uuidof<ID3D12Resource>(), (void**)&resource),
            $"creating a {bytes:N0} byte buffer");
        return resource;
    }

    // ------------------------------------------------------------------ kernels

    static byte[][] EnsureCompiled(out string why)
    {
        lock (compileLock)
        {
            why = compileFailure;
            if (bytecode != null || compileFailure != null)
                return bytecode;
            try
            {
                var compiler = new DxcCompiler();
                var kinds = Enum.GetValues<SolverKernel>();
                var code = new byte[kinds.Length][];
                string source = DebugSource();
                for (int i = 0; i < kinds.Length; i++)
                    code[i] = compiler.Compile(source, EntryPoint(kinds[i]));
                compiler.Dispose();
                return bytecode = code;
            }
            catch (Exception e)
            {
                why = compileFailure = e.Message;
                return null;
            }
        }
    }

    void CreatePipelines(byte[][] code)
    {
        // b0, t0..t14 (t14: the acceleration structure), u0..u5, all root descriptors
        var parameters = stackalloc D3D12_ROOT_PARAMETER[1 + SrvCount + UavCount];
        parameters[0] = new D3D12_ROOT_PARAMETER { ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_CBV, ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL };
        parameters[0].Descriptor.ShaderRegister = 0;
        for (int i = 0; i < SrvCount; i++)
        {
            parameters[1 + i] = new D3D12_ROOT_PARAMETER { ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_SRV, ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL };
            parameters[1 + i].Descriptor.ShaderRegister = (uint)i;
        }
        for (int i = 0; i < UavCount; i++)
        {
            parameters[1 + SrvCount + i] = new D3D12_ROOT_PARAMETER { ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_UAV, ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL };
            parameters[1 + SrvCount + i].Descriptor.ShaderRegister = (uint)i;
        }
        var desc = new D3D12_ROOT_SIGNATURE_DESC { NumParameters = 1 + SrvCount + UavCount, pParameters = parameters };
        ID3DBlob* blob = null, error = null;
        var hr = D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1, &blob, &error);
        if (error != null) error->Release();
        Check(hr, "D3D12SerializeRootSignature");
        ID3D12RootSignature* rs;
        Check(device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), __uuidof<ID3D12RootSignature>(), (void**)&rs), "CreateRootSignature");
        blob->Release();
        rootSignature = rs;

        pipelines = new ID3D12PipelineState*[code.Length];
        for (int i = 0; i < code.Length; i++)
            fixed (byte* p = code[i])
            {
                var pso = new D3D12_COMPUTE_PIPELINE_STATE_DESC
                {
                    pRootSignature = rootSignature,
                    CS = new D3D12_SHADER_BYTECODE { pShaderBytecode = p, BytecodeLength = (nuint)code[i].Length },
                };
                ID3D12PipelineState* state;
                Check(device->CreateComputePipelineState(&pso, __uuidof<ID3D12PipelineState>(), (void**)&state), $"creating the {EntryPoint((SolverKernel)i)} pipeline");
                pipelines[i] = state;
            }
    }

    // ------------------------------------------------------------------ recording and submission

    void Begin()
    {
        if (recording) return;
        WaitFence(allocatorFence[allocator]);
        Check(allocators[allocator]->Reset(), "resetting a command allocator");
        Check(list->Reset(allocators[allocator], null), "resetting the command list");
        recording = true;
    }

    void Submit()
    {
        if (!recording) return;
        FlushBarriers();
        Check(list->Close(), "closing the command list");
        var l = (ID3D12CommandList*)list;
        queue->ExecuteCommandLists(1, &l);
        fenceValue++;
        Check(queue->Signal(fence, fenceValue), "Signal");
        allocatorFence[allocator] = fenceValue;
        allocator = (allocator + 1) % Allocators;
        recording = false;
    }

    void WaitFence(ulong value)
    {
        if (value == 0 || fence->GetCompletedValue() >= value) return;
        Check(fence->SetEventOnCompletion(value, fenceEvent), "SetEventOnCompletion");
        while (WaitForSingleObject(fenceEvent, 500) != 0)
        {
            if (fence->GetCompletedValue() >= value) return;
            // a device the driver reset never signals the fence
            var removed = device->GetDeviceRemovedReason();
            if (removed.Value < 0)
                throw new SolverUnavailableException($"The light solver's Direct3D 12 device failed (0x{removed.Value:X8}). If the driver reset, try the CPU.");
        }
    }

    public override void Wait()
    {
        Submit();
        WaitFence(fenceValue);
    }

    void Transition(D3D12Buffer buffer, D3D12_RESOURCE_STATES state)
    {
        if (buffer.State == state) return;
        var b = new D3D12_RESOURCE_BARRIER { Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION };
        b.Transition.pResource = buffer.Resource;
        b.Transition.Subresource = 0xFFFFFFFF; // D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES
        b.Transition.StateBefore = buffer.State;
        b.Transition.StateAfter = state;
        barriers.Add(b);
        buffer.State = state;
    }

    void UavBarrier(ID3D12Resource* resource = null)
    {
        var b = new D3D12_RESOURCE_BARRIER { Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_UAV };
        b.UAV.pResource = resource;
        barriers.Add(b);
    }

    void FlushBarriers()
    {
        if (barriers.Count == 0) return;
        var array = barriers.ToArray();
        fixed (D3D12_RESOURCE_BARRIER* p = array)
            list->ResourceBarrier((uint)array.Length, p);
        barriers.Clear();
    }

    /// <summary>Room in the upload ring; when it's full, the device finishes what it was given first</summary>
    ulong RingAllocate(ulong bytes, ulong alignment)
    {
        ulong offset = (ringHead + alignment - 1) & ~(alignment - 1);
        if (offset + bytes > RingBytes)
        {
            if (bytes > RingBytes) throw new SolverUnavailableException("An upload is bigger than the Direct3D 12 upload ring.");
            Wait();
            Begin();
            offset = 0;
        }
        ringHead = offset + bytes;
        return offset;
    }

    // ------------------------------------------------------------------ buffers

    public override GpuBuffer CreateStructured<T>(T[] data, int count, bool uav = false)
    {
        int stride = sizeof(T);
        count = Math.Max(1, count);
        ulong bytes = (ulong)stride * (ulong)count;
        var buffer = new D3D12Buffer
        {
            Stride = stride,
            Count = count,
            Resource = CreateResource(D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT, bytes, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON, true),
            State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
        };
        buffer.Address = buffer.Resource->GetGPUVirtualAddress();
        if (data != null && data.Length > 0)
        {
            ulong dataBytes = (ulong)Math.Min(data.Length, count) * (ulong)stride;
            if (dataBytes <= RingBytes / 4)
                Upload(buffer, data, Math.Min(data.Length, count));
            else
            {
                // too big for the ring: a buffer of its own, gone once copied
                var upload = CreateResource(D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD, dataBytes, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ, false);
                void* mapped;
                Check(upload->Map(0, null, &mapped), "Map");
                fixed (T* p = data)
                    System.Buffer.MemoryCopy(p, mapped, (long)dataBytes, (long)dataBytes);
                upload->Unmap(0, null);
                Begin();
                Transition(buffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST);
                FlushBarriers();
                list->CopyBufferRegion(buffer.Resource, 0, upload, 0, dataBytes);
                Wait();
                upload->Release();
            }
        }
        return buffer;
    }

    public override GpuStaging CreateStaging(int bytes) => new D3D12Staging
    {
        Bytes = (ulong)Math.Max(16, bytes),
        Resource = CreateResource(D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK, (ulong)Math.Max(16, bytes), D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, false),
    };

    public override void Upload<T>(GpuBuffer target, T[] data, int count)
    {
        if (count <= 0) return;
        ulong bytes = (ulong)count * (ulong)sizeof(T);
        Begin();
        ulong offset = RingAllocate(bytes, 256);
        fixed (T* p = data)
            System.Buffer.MemoryCopy(p, ringMapped + offset, (long)bytes, (long)bytes);
        var buffer = (D3D12Buffer)target;
        Transition(buffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST);
        FlushBarriers();
        list->CopyBufferRegion(buffer.Resource, 0, ring, offset, bytes);
    }

    public override void CopyToStaging(GpuBuffer source, GpuStaging staging, int bytes, int firstByte = 0)
    {
        if (bytes <= 0) return;
        var buffer = (D3D12Buffer)source;
        var target = (D3D12Staging)staging;
        Begin();
        Transition(buffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
        FlushBarriers();
        list->CopyBufferRegion(target.Resource, 0, buffer.Resource, (ulong)firstByte, (ulong)bytes);
        // the device starts on it now; the read waits only for this copy
        Submit();
        target.Fence = fenceValue;
    }

    public override void ReadStaging<T>(GpuStaging staging, T[] destination, int count, int destinationIndex = 0)
    {
        if (count <= 0) return;
        var source = (D3D12Staging)staging;
        WaitFence(source.Fence);
        ulong bytes = (ulong)count * (ulong)sizeof(T);
        var range = new D3D12_RANGE { Begin = 0, End = (nuint)bytes };
        void* mapped;
        Check(source.Resource->Map(0, &range, &mapped), "Map");
        fixed (T* p = &destination[destinationIndex])
            System.Buffer.MemoryCopy(mapped, p, (long)(destination.Length - destinationIndex) * sizeof(T), (long)bytes);
        var none = new D3D12_RANGE();
        source.Resource->Unmap(0, &none);
    }

    // ------------------------------------------------------------------ dispatch

    public override void Dispatch(SolverKernel kernel, GpuParams p, GpuBuffer[] srvs, GpuBuffer[] uavs, int threads)
    {
        Begin();
        ulong constants = RingAllocate((ulong)((sizeof(GpuParams) + 255) & ~255), 256);
        *(GpuParams*)(ringMapped + constants) = p;

        Span<ulong> srvAddresses = stackalloc ulong[SrvCount];
        Span<ulong> uavAddresses = stackalloc ulong[UavCount];
        for (int i = 0; i < SrvCount - 1; i++)
        {
            var b = (i < srvs.Length ? srvs[i] as D3D12Buffer : null) ?? dummySrv;
            Transition(b, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            srvAddresses[i] = b.Address;
        }
        srvAddresses[SrvCount - 1] = sceneAddress != 0 ? sceneAddress : dummySrv.Address;
        for (int i = 0; i < UavCount; i++)
        {
            var b = (i < uavs.Length ? uavs[i] as D3D12Buffer : null) ?? dummyUav;
            Transition(b, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            uavAddresses[i] = b.Address;
        }
        FlushBarriers();
        list->SetComputeRootSignature(rootSignature);
        list->SetPipelineState(pipelines[(int)kernel]);
        list->SetComputeRootConstantBufferView(0, ringAddress + constants);
        for (int i = 0; i < SrvCount; i++)
            list->SetComputeRootShaderResourceView((uint)(1 + i), srvAddresses[i]);
        for (int i = 0; i < UavCount; i++)
            list->SetComputeRootUnorderedAccessView((uint)(1 + SrvCount + i), uavAddresses[i]);
        list->Dispatch((uint)((threads + SolverShaders.GroupSize - 1) / SolverShaders.GroupSize), 1, 1);
        // what the kernel wrote is there for whatever comes next
        UavBarrier();
    }

    // ------------------------------------------------------------------ the scene's acceleration structure

    /// <summary>Acceleration structures (bottom level over the triangles, top level with one instance of it)</summary>
    sealed class SceneStructure(SolverD3D12 owner, ID3D12Resource* bottom, ID3D12Resource* top) : IDisposable
    {
        public void Dispose()
        {
            if (owner.sceneAddress == top->GetGPUVirtualAddress()) owner.sceneAddress = 0;
            bottom->Release();
            top->Release();
        }
    }

    /// <summary>The 64-byte D3D12_RAYTRACING_INSTANCE_DESC</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct InstanceDesc
    {
        public Matrix3x4 Transform;
        public uint IdAndMask;
        public uint ContributionAndFlags;
        public ulong AccelerationStructure;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Matrix3x4
    {
        public float M00, M01, M02, M03, M10, M11, M12, M13, M20, M21, M22, M23;
    }

    public override IDisposable BuildScene(PackedScene packed)
    {
        int n = packed.Tris.Length;
        // one vertex triple per triangle, in the packed (BVH) order, so a hit's primitive index is its index in Tris
        var positions = new Vector3[n * 3];
        for (int i = 0; i < n; i++)
        {
            var t = packed.Tris[i];
            positions[i * 3] = t.V0;
            positions[i * 3 + 1] = t.V0 + t.E1;
            positions[i * 3 + 2] = t.V0 + t.E2;
        }
        using var vertices = (D3D12Buffer)CreateStructured(positions, positions.Length);
        Begin();
        Transition(vertices, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        FlushBarriers();

        var geometry = new D3D12_RAYTRACING_GEOMETRY_DESC
        {
            Type = D3D12_RAYTRACING_GEOMETRY_TYPE.D3D12_RAYTRACING_GEOMETRY_TYPE_TRIANGLES,
            // not opaque, so every candidate goes through Classify (masks, translucency, one-sided shadows), each exactly once
            Flags = D3D12_RAYTRACING_GEOMETRY_FLAGS.D3D12_RAYTRACING_GEOMETRY_FLAG_NO_DUPLICATE_ANYHIT_INVOCATION,
        };
        geometry.Triangles.VertexBuffer.StartAddress = vertices.Address;
        geometry.Triangles.VertexBuffer.StrideInBytes = 12;
        geometry.Triangles.VertexFormat = DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT;
        geometry.Triangles.VertexCount = (uint)positions.Length;
        geometry.Triangles.IndexFormat = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN;

        var bottomInputs = new D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS
        {
            Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE.D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL,
            Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAGS.D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE,
            NumDescs = 1,
            DescsLayout = D3D12_ELEMENTS_LAYOUT.D3D12_ELEMENTS_LAYOUT_ARRAY,
        };
        bottomInputs.pGeometryDescs = &geometry;
        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO bottomInfo;
        device->GetRaytracingAccelerationStructurePrebuildInfo(&bottomInputs, &bottomInfo);

        // the instance, read by the top level's build
        ulong instanceOffset = RingAllocate((ulong)sizeof(InstanceDesc), 256);
        var topInputs = new D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS
        {
            Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE.D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL,
            Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAGS.D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE,
            NumDescs = 1,
            DescsLayout = D3D12_ELEMENTS_LAYOUT.D3D12_ELEMENTS_LAYOUT_ARRAY,
        };
        topInputs.InstanceDescs = ringAddress + instanceOffset;
        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO topInfo;
        device->GetRaytracingAccelerationStructurePrebuildInfo(&topInputs, &topInfo);

        var asState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE;
        var bottom = CreateResource(D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT, bottomInfo.ResultDataMaxSizeInBytes, asState, true);
        var top = CreateResource(D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT, topInfo.ResultDataMaxSizeInBytes, asState, true);
        var scratch = CreateResource(D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT, Math.Max(bottomInfo.ScratchDataSizeInBytes, topInfo.ScratchDataSizeInBytes),
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS, true);
        try
        {
            var bottomBuild = new D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC
            {
                DestAccelerationStructureData = bottom->GetGPUVirtualAddress(),
                Inputs = bottomInputs,
                ScratchAccelerationStructureData = scratch->GetGPUVirtualAddress(),
            };
            list->BuildRaytracingAccelerationStructure(&bottomBuild, 0, null);
            UavBarrier(bottom);
            UavBarrier(scratch);
            FlushBarriers();

            *(InstanceDesc*)(ringMapped + instanceOffset) = new InstanceDesc
            {
                Transform = new Matrix3x4 { M00 = 1, M11 = 1, M22 = 1 },
                IdAndMask = 0xFFu << 24,
                // D3D12_RAYTRACING_INSTANCE_FLAG_TRIANGLE_CULL_DISABLE
                ContributionAndFlags = 1u << 24,
                AccelerationStructure = bottom->GetGPUVirtualAddress(),
            };
            var topBuild = new D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC
            {
                DestAccelerationStructureData = top->GetGPUVirtualAddress(),
                Inputs = topInputs,
                ScratchAccelerationStructureData = scratch->GetGPUVirtualAddress(),
            };
            list->BuildRaytracingAccelerationStructure(&topBuild, 0, null);
            UavBarrier(top);
            Wait();
        }
        finally
        {
            scratch->Release();
        }
        sceneAddress = top->GetGPUVirtualAddress();
        return new SceneStructure(this, bottom, top);
    }

    public override void Dispose()
    {
        try { if (device != null && queue != null && fence != null) Wait(); } catch { }
        dummySrv?.Dispose();
        dummyUav?.Dispose();
        if (pipelines != null)
            foreach (var p in pipelines) if (p != null) p->Release();
        if (rootSignature != null) rootSignature->Release();
        if (ring != null) { ring->Unmap(0, null); ring->Release(); }
        if (list != null) list->Release();
        foreach (var a in allocators) if (a != null) a->Release();
        if (fence != null) fence->Release();
        if (fenceEvent != default) CloseHandle(fenceEvent);
        if (queue != null) queue->Release();
        if (device != null) device->Release();
        device = null; queue = null; fence = null; list = null; rootSignature = null; ring = null;
    }

    // ------------------------------------------------------------------ DXC

    /// <summary>
    /// The DirectX shader compiler, from dxcompiler.dll (and dxil.dll, which signs its output) next to LEX or in the newest Windows SDK
    /// </summary>
    sealed class DxcCompiler : IDisposable
    {
        static readonly Guid ClsidDxcCompiler = new("73e22d93-e6ce-47f3-b5bf-f0664f39c1b0");
        IDxcCompiler3* compiler;

        public DxcCompiler()
        {
            LoadLibraries();
            var clsid = ClsidDxcCompiler;
            IDxcCompiler3* c;
            Check(DxcCreateInstance(&clsid, __uuidof<IDxcCompiler3>(), (void**)&c), "creating the DXC compiler");
            compiler = c;
        }

        static void LoadLibraries()
        {
            static IEnumerable<string> Folders()
            {
                yield return AppContext.BaseDirectory;
                string kits = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "bin");
                if (Directory.Exists(kits))
                    foreach (var dir in Directory.GetDirectories(kits).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                        yield return Path.Combine(dir, "x64");
            }
            foreach (var folder in Folders())
            {
                string compiler = Path.Combine(folder, "dxcompiler.dll"), signer = Path.Combine(folder, "dxil.dll");
                if (!File.Exists(compiler) || !File.Exists(signer)) continue;
                // loaded by full path first, so the DllImport and DXC's own LoadLibrary("dxil.dll") find these
                NativeLibrary.Load(signer);
                NativeLibrary.Load(compiler);
                return;
            }
            throw new SolverUnavailableException("the DirectX shader compiler (dxcompiler.dll and dxil.dll) wasn't found next to LEX or in the Windows SDK");
        }

        public byte[] Compile(string source, string entry)
        {
            var bytes = Encoding.UTF8.GetBytes(source);
            string[] args = ["-E", entry, "-T", "cs_6_5", "-O3", "-HV", "2018", "-D", "HW_RT=1", "-Qstrip_debug", "-Qstrip_reflect"];
            var handles = args.Select(a => Marshal.StringToHGlobalUni(a)).ToArray();
            try
            {
                var argPointers = stackalloc char*[handles.Length];
                for (int i = 0; i < handles.Length; i++) argPointers[i] = (char*)handles[i];
                fixed (byte* p = bytes)
                {
                    var buffer = new DxcBuffer { Ptr = p, Size = (nuint)bytes.Length, Encoding = 65001 };
                    IDxcResult* result;
                    Check(compiler->Compile(&buffer, argPointers, (uint)handles.Length, null, __uuidof<IDxcResult>(), (void**)&result), $"compiling {entry}");
                    try
                    {
                        HRESULT status;
                        result->GetStatus(&status);
                        if (status.Value < 0)
                        {
                            IDxcBlobUtf8* errors = null;
                            result->GetOutput(DXC_OUT_KIND.DXC_OUT_ERRORS, __uuidof<IDxcBlobUtf8>(), (void**)&errors, null);
                            string message = errors != null ? Marshal.PtrToStringUTF8((nint)errors->GetStringPointer()) : "no message";
                            if (errors != null) errors->Release();
                            throw new SolverUnavailableException($"the {entry} kernel didn't compile for ray tracing: {message}");
                        }
                        IDxcBlob* code = null;
                        Check(result->GetOutput(DXC_OUT_KIND.DXC_OUT_OBJECT, __uuidof<IDxcBlob>(), (void**)&code, null), "reading DXC's output");
                        var output = new byte[(int)code->GetBufferSize()];
                        Marshal.Copy((nint)code->GetBufferPointer(), output, 0, output.Length);
                        code->Release();
                        return output;
                    }
                    finally
                    {
                        result->Release();
                    }
                }
            }
            finally
            {
                foreach (var h in handles) Marshal.FreeHGlobal(h);
            }
        }

        public void Dispose()
        {
            if (compiler != null) compiler->Release();
            compiler = null;
        }
    }
}
