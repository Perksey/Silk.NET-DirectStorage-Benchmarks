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
            var len = (ulong)new FileInfo("file.bin").Length;
            using var d3d12Stuff = new D3D12Objects(len);
            using var vk = Vk.GetApi();
            var instance = vk.CreateInstance();
            Span<QueueInfo> queueFamilies = stackalloc[] { new QueueInfo(QueueFlags.QueueTransferBit, 1) };
            var physicalDevice = vk.SelectPhysicalDevice(instance, queueFamilies);
            PhysicalDeviceProperties2.Chain(out var props2).AddNext(out PhysicalDeviceIDProperties idProps);
            vk.GetPhysicalDeviceProperties2(physicalDevice, &props2);
            var luid = Unsafe.As<byte, Luid>(ref idProps.DeviceLuid[0]);
            if (luid.High != d3d12Stuff.AdapterDesc.AdapterLuid.High || luid.Low != d3d12Stuff.AdapterDesc.AdapterLuid.Low)
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
            var transitBuffer = vk.CreateBuffer
            (
                physicalDevice,
                device,
                len,
                BufferUsageFlags.BufferUsageTransferSrcBit,
                MemoryPropertyFlags.MemoryPropertyHostVisibleBit,
                out var uploadMemory
            );
            var data = default(void*);
            vk.MapMemory(device, uploadMemory, 0, len, 0, ref data).AssertOk();
            {
                using var fs = File.OpenRead("file.bin");
                var span = new Span<byte>(data, (int)len);
                do
                {
                    span = span[fs.Read(span)..];
                } while (span.Length != 0);
            }
            vk.UnmapMemory(device, uploadMemory);
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
            vk.BeginCommandBuffer
            (
                commandBuffer,
                new CommandBufferBeginInfo(flags: CommandBufferUsageFlags.CommandBufferUsageOneTimeSubmitBit)
            ).AssertOk();
            vk.CmdCopyBuffer(commandBuffer, transitBuffer, restBuffer, 1, new BufferCopy(0, 0, len));
            vk.EndCommandBuffer(commandBuffer).AssertOk();
            vk.QueueSubmit
            (
                queueFamilies.Get(QueueFlags.QueueTransferBit).Queue!.Value,
                1,
                new SubmitInfo(commandBufferCount: 1, pCommandBuffers: &commandBuffer),
                default
            ).AssertOk();
            vk.QueueWaitIdle(queueFamilies.Get(QueueFlags.QueueTransferBit).Queue!.Value).AssertOk();
            vk.FreeCommandBuffers(device, commandPool, 1, commandBuffer);

            // End Benchmark
            vk.DestroyCommandPool(device, commandPool, null);
            vk.DestroyBuffer(device, transitBuffer, null);
            vk.DestroyBuffer(device, restBuffer, null);
            vk.FreeMemory(device, uploadMemory, null);
            vk.FreeMemory(device, memory, null);
            vk.DestroyDevice(device, null);
            vk.DestroyInstance(instance, null);
        }
    }
}