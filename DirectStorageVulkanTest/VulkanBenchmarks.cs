using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BenchmarkDotNet;
using BenchmarkDotNet.Attributes;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DirectStorage;
using Silk.NET.DXGI;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace DirectStorageVulkanTest
{
    public class Benchmarks
    {
        [Benchmark]
        public unsafe void VulkanPlainLoadCopy()
        {
            var len = (ulong)new FileInfo("Data/file0.bin").Length;
            using var d3d12Stuff = new D3D12Objects(len);
            using var vk = Vk.GetApi();
            var instance = vk.CreateInstance();
            Span<QueueInfo> queueFamilies = stackalloc[] { new QueueInfo(QueueFlags.QueueTransferBit, 1) };
            var physicalDevice = vk.SelectPhysicalDevice(instance, queueFamilies);
            PhysicalDeviceProperties2.Chain(out var props2).AddNext(out PhysicalDeviceIDProperties idProps);
            vk.GetPhysicalDeviceProperties2(physicalDevice, &props2);
            var luid = Unsafe.As<byte, Luid>(ref idProps.DeviceLuid[0]);
            if (luid.High != d3d12Stuff.AdapterDesc.AdapterLuid.High ||
                luid.Low != d3d12Stuff.AdapterDesc.AdapterLuid.Low)
            {
                throw new("picked different physical devices between DXGI and Vulkan");
            }

            var device = vk.CreateDevice(physicalDevice, Array.Empty<string>(), ref queueFamilies);
            vk.GetPhysicalDeviceExternalBufferProperties
            (
                physicalDevice,
                new PhysicalDeviceExternalBufferInfo
                (
                    usage: BufferUsageFlags.BufferUsageStorageBufferBit,
                    handleType: ExternalMemoryHandleTypeFlags.ExternalMemoryHandleTypeD3D12ResourceBit
                ),
                out var props
            );

            if ((props.ExternalMemoryProperties.ExternalMemoryFeatures &
                 ExternalMemoryFeatureFlags.ExternalMemoryFeatureImportableBit) == 0)
            {
                throw new("External memory import of D3D12 resources not supported");
            }

            // Begin Benchmark
            var restBuffer = vk.CreateBuffer
            (
                physicalDevice,
                device,
                len,
                BufferUsageFlags.BufferUsageTransferDstBit,
                MemoryPropertyFlags.MemoryPropertyDeviceLocalBit,
                out var memory,
                new ImportMemoryWin32HandleInfoKHR
                (
                    handleType: ExternalMemoryHandleTypeFlags.ExternalMemoryHandleTypeD3D12ResourceBit,
                    handle: d3d12Stuff.BufferHandle
                )
            );
            vk.CreateCommandPool
            (
                device,
                new CommandPoolCreateInfo
                (
                    flags: CommandPoolCreateFlags.CommandPoolCreateTransientBit,
                    queueFamilyIndex: queueFamilies.Get(QueueFlags.QueueTransferBit).Family!.Value
                ),
                null,
                out var commandPool
            ).AssertOk();
            vk.AllocateCommandBuffers
            (
                device,
                new CommandBufferAllocateInfo
                (
                    commandPool: commandPool,
                    level: CommandBufferLevel.Primary,
                    commandBufferCount: 1
                ),
                out var commandBuffer
            ).AssertOk();
            var transitBuffer = vk.CreateBuffer
            (
                physicalDevice,
                device,
                len,
                BufferUsageFlags.BufferUsageTransferSrcBit,
                MemoryPropertyFlags.MemoryPropertyHostVisibleBit,
                out var uploadMemory
            );
            vk.BeginCommandBuffer
            (
                commandBuffer,
                new CommandBufferBeginInfo(flags: CommandBufferUsageFlags.CommandBufferUsageOneTimeSubmitBit)
            ).AssertOk();
            vk.CmdCopyBuffer(commandBuffer, transitBuffer, restBuffer, 1, new BufferCopy(0, 0, len));
            vk.EndCommandBuffer(commandBuffer).AssertOk();
            for (var i = 0; i < 4096; i++)
            {
                var data = default(void*);
                vk.MapMemory(device, uploadMemory, 0, len, 0, ref data).AssertOk();
                using var fs = File.OpenRead($"Data/file{i}.bin");
                var span = new Span<byte>(data, (int)len);
                do
                {
                    span = span[fs.Read(span)..];
                } while (span.Length != 0);

                vk.UnmapMemory(device, uploadMemory);
                vk.QueueSubmit
                (
                    queueFamilies.Get(QueueFlags.QueueTransferBit).Queue!.Value,
                    1,
                    new SubmitInfo(commandBufferCount: 1, pCommandBuffers: &commandBuffer),
                    default
                ).AssertOk();
                vk.QueueWaitIdle(queueFamilies.Get(QueueFlags.QueueTransferBit).Queue!.Value).AssertOk();
            }

            // End Benchmark
            vk.FreeCommandBuffers(device, commandPool, 1, commandBuffer);
            vk.DestroyCommandPool(device, commandPool, null);
            vk.DestroyBuffer(device, restBuffer, null);
            vk.DestroyBuffer(device, transitBuffer, null);
            vk.FreeMemory(device, uploadMemory, null);
            vk.FreeMemory(device, memory, null);
            vk.DestroyDevice(device, null);
            vk.DestroyInstance(instance, null);
        }
        [Benchmark]
        public unsafe void VulkanDirectStorage()
        {
            var len = (ulong)new FileInfo("Data/file0.bin").Length;
            using var d3d12Stuff = new D3D12Objects(len);
            using var vk = Vk.GetApi();
            using var dStorage = DStorage.GetApi();
            var instance = vk.CreateInstance();
            Span<QueueInfo> queueFamilies = stackalloc[] { new QueueInfo(QueueFlags.QueueTransferBit, 1) };
            var physicalDevice = vk.SelectPhysicalDevice(instance, queueFamilies);
            PhysicalDeviceProperties2.Chain(out var props2).AddNext(out PhysicalDeviceIDProperties idProps);
            vk.GetPhysicalDeviceProperties2(physicalDevice, &props2);
            var luid = Unsafe.As<byte, Luid>(ref idProps.DeviceLuid[0]);
            if (luid.High != d3d12Stuff.AdapterDesc.AdapterLuid.High ||
                luid.Low != d3d12Stuff.AdapterDesc.AdapterLuid.Low)
            {
                throw new("picked different physical devices between DXGI and Vulkan");
            }

            var device = vk.CreateDevice(physicalDevice, Array.Empty<string>(), ref queueFamilies);
            vk.GetPhysicalDeviceExternalBufferProperties
            (
                physicalDevice,
                new PhysicalDeviceExternalBufferInfo
                (
                    usage: BufferUsageFlags.BufferUsageStorageBufferBit,
                    handleType: ExternalMemoryHandleTypeFlags.ExternalMemoryHandleTypeD3D12ResourceBit
                ),
                out var props
            );

            if ((props.ExternalMemoryProperties.ExternalMemoryFeatures &
                 ExternalMemoryFeatureFlags.ExternalMemoryFeatureImportableBit) == 0)
            {
                throw new("External memory import of D3D12 resources not supported");
            }

            // Begin Benchmark
            var restBuffer = vk.CreateBuffer
            (
                physicalDevice,
                device,
                len,
                BufferUsageFlags.BufferUsageTransferDstBit,
                MemoryPropertyFlags.MemoryPropertyDeviceLocalBit,
                out var memory,
                new ImportMemoryWin32HandleInfoKHR
                (
                    handleType: ExternalMemoryHandleTypeFlags.ExternalMemoryHandleTypeD3D12ResourceBit,
                    handle: d3d12Stuff.BufferHandle
                )
            );
            using var factory = default(ComPtr<IDStorageFactory>);
            dStorage.GetFactory(SilkMarshal.GuidPtrOf<IDStorageFactory>(), (void**)factory.GetAddressOf()).AssertOk();
            using var queue = default(ComPtr<IDStorageQueue>);
            factory.Get()
                .CreateQueue(
                    new QueueDesc(RequestSourceType.RequestSourceFile, DStorage.MaxQueueCapacity, Priority.PriorityHigh,
                        null, d3d12Stuff.Device), SilkMarshal.GuidPtrOf<IDStorageQueue>(), (void**)queue.GetAddressOf())
                .AssertOk();
            using var fence = default(ComPtr<ID3D12Fence>);
            d3d12Stuff.Device.Get().CreateFence(0, FenceFlags.FenceFlagNone, SilkMarshal.GuidPtrOf<ID3D12Fence>(),
                (void**)fence.GetAddressOf()).AssertOk();
            fence.Get().AddRef();
            var @event = SilkMarshal.CreateWindowsEvent(null, false, false, null);
            for (var i = 0; i < 4096; i++)
            {
                var file = default(ComPtr<IDStorageFile>);
                // BUG: The string overload uses UTF8 instead of UTF16
                factory.Get().OpenFile
                (
                    ref Unsafe.AsRef(in Path.GetFullPath($"Data\\file{i}.bin").GetPinnableReference()),
                    SilkMarshal.GuidPtrOf<IDStorageFile>(),
                    (void**)file.GetAddressOf()
                ).AssertOk();
                file.Get().AddRef();
                queue.Get().EnqueueRequest
                (
                    new Request
                    (
                        new RequestOptions
                        (
                            sourceType: RequestSourceType.RequestSourceFile,
                            destinationType: RequestDestinationType.RequestDestinationBuffer
                        ),
                        new Source(file: new SourceFile(file.Handle, 0, (uint)len)),
                        new Destination(buffer: new DestinationBuffer(d3d12Stuff.RestBuffer.Handle, 0, (uint)len)),
                        uncompressedSize: (uint)len
                    )
                );
                fence.Get().SetEventOnCompletion(1, (void*)@event).AssertOk();
                // Console.WriteLine(@event);
                queue.Get().EnqueueSignal(fence.Handle, 1);
                queue.Get().Submit();
                SilkMarshal.WaitWindowsObjects(@event);
                ErrorRecord errorRecord;
                queue.Get().RetrieveErrorRecord(&errorRecord);
                errorRecord.FirstFailure.HResult.AssertOk();
                file.Release();
            }

            // End Benchmark
            vk.DestroyBuffer(device, restBuffer, null);
            vk.FreeMemory(device, memory, null);
            vk.DestroyDevice(device, null);
            vk.DestroyInstance(instance, null);
        }
    }
}