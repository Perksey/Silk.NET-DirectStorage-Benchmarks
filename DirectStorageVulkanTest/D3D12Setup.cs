using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace DirectStorageVulkanTest;

public unsafe record struct D3D12Objects
(
    D3D12 D3D12 = default!,
    DXGI DXGI = default!,
    AdapterDesc AdapterDesc = default,
    ComPtr<IDXGIFactory> DXGIFactory = default,
#if DEBUG
    ComPtr<ID3D12Debug> DebugLayer = default,
#endif
    ComPtr<ID3D12Device> Device = default,
    ComPtr<ID3D12Resource> RestBuffer = default,
    nint BufferHandle = default,
    ComPtr<ID3D12InfoQueue> InfoQueue = default,
    CancellationTokenSource Token = default!
) : IDisposable
{
    public D3D12Objects(ulong len) : this(default!, default!)
    {
        D3D12 = D3D12.GetApi();
        DXGI = DXGI.GetApi();

        var factory = DXGIFactory;
        DXGI.CreateDXGIFactory(SilkMarshal.GuidPtrOf<IDXGIFactory>(), (void**)factory.GetAddressOf()).AssertOk();
        DXGIFactory = factory;

        var adapter = default(IDXGIAdapter*);
        DXGIFactory.Get().EnumAdapters(0, ref adapter).AssertOk();

        var desc = default(AdapterDesc);
        adapter->GetDesc(ref desc).AssertOk();
        AdapterDesc = desc;

#if DEBUG
        var layer = DebugLayer;
        D3D12.GetDebugInterface(SilkMarshal.GuidPtrOf<ID3D12Debug>(), (void**)layer.GetAddressOf()).AssertOk();
        layer.Get().EnableDebugLayer();
        DebugLayer = layer;
#endif

        var device = Device;
        D3D12.CreateDevice
        (
            (IUnknown*)adapter,
            D3DFeatureLevel.D3DFeatureLevel120,
            SilkMarshal.GuidPtrOf<ID3D12Device>(),
            (void**)device.GetAddressOf()
        ).AssertOk();
        Device = device;

#if DEBUG
        var iq = default(ComPtr<ID3D12InfoQueue>);
        SilkMarshal.ThrowHResult(Device.Get()
            .QueryInterface(SilkMarshal.GuidPtrOf<ID3D12InfoQueue>(), (void**)iq.GetAddressOf()));
        Token = new();
        var tok = Token.Token;
        Task.Run
        (
            () =>
            {
                while (tok.IsCancellationRequested)
                {
                    var numMessages = iq.Get().GetNumStoredMessages();
                    if (numMessages == 0)
                    {
                        continue;
                    }

                    for (var i = 0ul; i < numMessages; i++)
                    {
                        nuint msgByteLength;
                        SilkMarshal.ThrowHResult(iq.Get().GetMessageA(i, null, &msgByteLength));
                        using var memory = GlobalMemory.Allocate((int)msgByteLength);
                        SilkMarshal.ThrowHResult
                        (
                            iq.Get().GetMessageA(i, memory.AsPtr<Message>(), &msgByteLength)
                        );
                        ref var msg = ref memory.AsRef<Message>();
                        var descBytes = new Span<byte>(msg.PDescription, (int)msg.DescriptionByteLength);
                        var desc = Encoding.UTF8.GetString(descBytes[..^1]);
                        var str = $"{msg.Category.ToString()["MessageCategory".Length..]} (From D3D12): {desc}";
                        Console.WriteLine($"{msg.Severity} {str}");
                    }

                    iq.Get().ClearStoredMessages();
                }
            },
            tok
        );
#endif

        // Note: using upload heaps to transfer static data like vert buffers is not
        // recommended. Every time the GPU needs it, the upload heap will be marshalled
        // over. Please read up on Default Heap usage. An upload heap is used here for
        // code simplicity and because there are very few verts to actually transfer.var heapProperties = new
        // HeapProperties(HeapType.HeapTypeDefault);
        var bufferDesc = new ResourceDesc
        (
            ResourceDimension.ResourceDimensionBuffer, 0, len, 1, 1, 1, Silk.NET.DXGI.Format.FormatUnknown,
            new SampleDesc(1, 0),
            TextureLayout.TextureLayoutRowMajor, ResourceFlags.ResourceFlagNone
        );

        var heapProperties = new HeapProperties(HeapType.HeapTypeDefault);
        var restBuffer = RestBuffer;
        Device.Get().CreateCommittedResource
        (
            &heapProperties,
            HeapFlags.HeapFlagShared,
            &bufferDesc,
            ResourceStates.ResourceStateCommon,
            null,
            SilkMarshal.GuidPtrOf<ID3D12Resource>(),
            (void**)restBuffer.GetAddressOf()
        ).AssertOk();
        RestBuffer = restBuffer;

        void* handle;
        Device.Get().CreateSharedHandle
        (
            (ID3D12DeviceChild*)RestBuffer.Handle,
            null,
            0x10000000,
            (char*)0,
            &handle
        ).AssertOk();

        BufferHandle = (nint)handle;
    }

    [DllImport("kernel32")]
    static extern bool CloseHandle(nint handle);  

    public void Dispose()
    {
        CloseHandle(BufferHandle);
        RestBuffer.Release();
        Token.Cancel();
        Device.Release(); // info queue
        Device.Release(); // device
        DebugLayer.Release();
        D3D12.Dispose();
        DXGIFactory.Release(); // no idea
        DXGIFactory.Release(); // factory
        DXGI.Dispose();
    }
}

public static class D3D12Setup
{
}

public static partial class Helpers
{
    public static void AssertOk(this int hresult) => SilkMarshal.ThrowHResult(hresult);
}