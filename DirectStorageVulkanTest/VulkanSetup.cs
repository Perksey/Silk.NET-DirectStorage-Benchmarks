using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace DirectStorageVulkanTest;

public record struct QueueInfo
(
    QueueFlags Kind,
    uint? QueueCount = null,
    uint? Family = null,
    Queue? Queue = null
);

public static class VulkanSetup
{
    public static unsafe Instance CreateInstance(this Vk vk)
    {
        const string name = nameof(DirectStorageVulkanTest);
        Span<byte> nameBytes = stackalloc byte[SilkMarshal.GetMaxSizeOf(name)];
        SilkMarshal.StringIntoSpan(name, nameBytes);

        const string engine = "No Engine";
        Span<byte> engineBytes = stackalloc byte[SilkMarshal.GetMaxSizeOf(engine)];
        SilkMarshal.StringIntoSpan(engine, engineBytes);

        var info = new ApplicationInfo
        (
            pApplicationName: (byte*)Unsafe.AsPointer(ref nameBytes[0]),
            applicationVersion: new Version32(1, 0, 0),
            pEngineName: (byte*)Unsafe.AsPointer(ref engineBytes[0]),
            engineVersion: new Version32(0, 0, 0),
            apiVersion: Vk.Version11
        );

        vk.CreateInstance(new InstanceCreateInfo(pApplicationInfo: &info), null, out var instance).AssertOk();
        return instance;
    }

    public static unsafe PhysicalDevice SelectPhysicalDevice(this Vk vk, Instance instance,
        Span<QueueInfo> queueFamilies)
    {
        var physicalDeviceCnt = 0u;
        vk.EnumeratePhysicalDevices(instance, ref physicalDeviceCnt, null).AssertOk();

        // DANGEROUS: var stackalloc of runtime-known-only length
        var physicalDevices = stackalloc PhysicalDevice[(int)physicalDeviceCnt];
        vk.EnumeratePhysicalDevices(instance, ref physicalDeviceCnt, physicalDevices).AssertOk();

        var allRequested = (QueueFlags)0;
        foreach (var (flags, _, _, _) in queueFamilies)
        {
            allRequested |= flags;
        }

        PhysicalDevice? physicalDevice = null;
        QueueFamilyProperties[]? families = null;
        for (var i = 0; i < physicalDeviceCnt; i++)
        {
            var queueCnt = 0u;
            vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevices[i], ref queueCnt, null);
            families = families is null || families.Length != queueCnt
                ? new QueueFamilyProperties[(int)queueCnt]
                : families;
            vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevices[i], ref queueCnt, out families[0]);
            var devQueueFamilies = (QueueFlags)0;
            foreach (var queueFamily in families)
            {
                devQueueFamilies |= queueFamily.QueueFlags;
            }

            if ((devQueueFamilies & allRequested) != allRequested)
            {
                continue;
            }

            physicalDevice = physicalDevices[i];
            break;
        }

        if (physicalDevice is null || families is null)
        {
            throw new("One or more queue families are not supported by any physical device.");
        }

        foreach (var family in families)
        {
            for (var index = 0u; index < queueFamilies.Length; index++)
            {
                ref var kvp = ref queueFamilies[(int)index];
                if ((family.QueueFlags & kvp.Kind) != kvp.Kind)
                {
                    continue;
                }

                kvp.Family = index;
                kvp.QueueCount ??= family.QueueCount;
                break;
            }
        }

        return physicalDevices[0];
    }

    public static unsafe Device CreateDevice
    (
        this Vk vk,
        PhysicalDevice physicalDevice,
        IReadOnlyList<string> extensions,
        ref Span<QueueInfo> queues
    )
    {
        // "Compact" the queue info so it only contains unique families
        var compacted = 0;
        for (var i = 0; i < queues.Length - compacted; i++)
        {
            ArgumentNullException.ThrowIfNull(queues[i].Family);
            ArgumentNullException.ThrowIfNull(queues[i].QueueCount);

            // Look back, have we seen this family before?
            for (var j = i - 1; j >= 0; j--)
            {
                if (queues[i].Family != queues[j].Family)
                {
                    continue;
                }

                // We have seen it before. Combine the info.
                queues[j].Kind |= queues[i].Kind;
                queues[j].QueueCount = Math.Max(queues[i].QueueCount!.Value, queues[j].QueueCount!.Value);

                // Swap
                compacted++;
                if (i != queues.Length - 1)
                {
                    (queues[i], queues[^compacted]) = (queues[^compacted], queues[i]);
                    i--; // We need to check i again now.
                }

                break;
            }
        }

        queues = queues[..^compacted];
        var queueInfos = stackalloc DeviceQueueCreateInfo[queues.Length];
        for (var i = 0; i < queues.Length; i++)
        {
            // TODO add support for priorities
            var priorities = (float*)SilkMarshal.Allocate((int)(queues[i].QueueCount!.Value * sizeof(float)));
            new Span<float>(priorities, (int)queues[i].QueueCount!.Value).Fill(1);
            queueInfos[i] = new DeviceQueueCreateInfo
            (
                queueFamilyIndex: queues[i].Family!.Value,
                queueCount: queues[i].QueueCount!.Value,
                pQueuePriorities: priorities
            );
        }

        var features = new PhysicalDeviceFeatures();

        var nStringArray = SilkMarshal.StringArrayToPtr(extensions);
        vk.CreateDevice
        (
            physicalDevice,
            new DeviceCreateInfo
            (
                queueCreateInfoCount: (uint)queues.Length,
                pQueueCreateInfos: queueInfos,
                pEnabledFeatures: &features,
                enabledExtensionCount: (uint)extensions.Count,
                ppEnabledExtensionNames: (byte**)nStringArray
            ),
            null,
            out var device
        ).AssertOk();
        SilkMarshal.Free(nStringArray);

        foreach (ref var queueInfo in queues)
        {
            vk.GetDeviceQueue(device, queueInfo.Family!.Value, 0, out var queue);
            queueInfo.Queue = queue;
        }

        return device;
    }

    public static unsafe uint FindMemoryType(this Vk vk, PhysicalDevice device, MemoryPropertyFlags flags,
        uint filter = 0)
    {
        vk.GetPhysicalDeviceMemoryProperties(device, out var memProperties);
        for (var i = 0; i < memProperties.MemoryTypeCount; i++)
        {
            if (((1 << i) & filter) != 0 && (memProperties.MemoryTypes[i].PropertyFlags & flags) == flags)
            {
                return (uint)i;
            }
        }

        throw new("No suitable memory type");
    }

    public static unsafe Silk.NET.Vulkan.Buffer CreateBuffer(this Vk vk, PhysicalDevice physicalDevice, Device device,
        ulong size, BufferUsageFlags usage,
        MemoryPropertyFlags properties, out DeviceMemory bufferMemory, ImportMemoryWin32HandleInfoKHR? extInfo = null)
    {
        var bci = new BufferCreateInfo(size: size, usage: usage, sharingMode: SharingMode.Exclusive);
        ExternalMemoryBufferCreateInfo emBci;
        if (extInfo is not null)
        {
            bci.AddNext(out emBci);
            emBci.HandleTypes = extInfo.Value.HandleType;
        }

        vk.CreateBuffer
        (
            device,
            bci,
            null,
            out var buffer
        ).AssertOk();
        vk.GetBufferMemoryRequirements(device, buffer, out var memRequirements);
        
        // ReSharper disable once NotAccessedVariable <-- false positive
        ImportMemoryWin32HandleInfoKHR extInfoNn;
        MemoryDedicatedAllocateInfo dedicatedInfo;
        
        var allocInfo = new MemoryAllocateInfo
        (
            allocationSize: memRequirements.Size,
            memoryTypeIndex: vk.FindMemoryType(physicalDevice, properties, memRequirements.MemoryTypeBits)
        );
        
        if (extInfo is not null)
        {
            allocInfo.AddNext(out dedicatedInfo).AddNext(out extInfoNn);
            dedicatedInfo.Buffer = buffer;
            extInfoNn = extInfo.Value;
        }

        vk.AllocateMemory(device, allocInfo, null, out bufferMemory).AssertOk();
        vk.BindBufferMemory(device, buffer, bufferMemory, 0).AssertOk();
        return buffer;
    }
}

public static partial class Helpers
{
    public static Result AssertOk(this Result result)
    {
        if ((int)result < 0)
        {
            throw new(result.ToString());
        }

        return result;
    }

    public static ref QueueInfo Get(this Span<QueueInfo> queues, QueueFlags queue)
    {
        foreach (ref var theQueue in queues)
        {
            if ((theQueue.Kind & queue) == queue)
            {
                return ref theQueue;
            }
        }

        throw new("Queue not found.");
    }
}