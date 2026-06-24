using System;
using System.Runtime.InteropServices;
using Subsystem.Vom;

namespace Subsystem.Device;

// DirectPortVk — C# implementation of the Vulkan DirectPort fabric.
// Initializes the Vulkan instance and device, creates shared VkImages,
// exports opaque fds, and mounts resources under the VOM.
internal static unsafe class DirectPortVk
{
    private static nint _instance = nint.Zero;
    private static nint _device = nint.Zero;
    private static nint _physDevice = nint.Zero;
    private static uint _queueFamilyIndex = uint.MaxValue;

    // Resolved extension function delegates
    private static VulkanVk.vkGetMemoryFdKHR_t? _vkGetMemoryFdKHR;
    private static VulkanVk.vkGetSemaphoreFdKHR_t? _vkGetSemaphoreFdKHR;

    private static readonly object _lock = new();

    public enum DpFormat { Video = 0, Float = 1, Half = 2, Raw32 = 3 }

    public static bool Init()
    {
        lock (_lock)
        {
            if (_instance != nint.Zero) return true;

            // 1. Create Instance
            var appInfo = new VulkanVk.VkApplicationInfo
            {
                sType = VulkanVk.VK_STRUCTURE_TYPE_APPLICATION_INFO,
                pApplicationName = Marshal.StringToHGlobalAnsi("SubsystemDirectPort"),
                applicationVersion = 1,
                pEngineName = Marshal.StringToHGlobalAnsi("Subsystem"),
                engineVersion = 1,
                apiVersion = (1 << 22) | (1 << 12) | 0 // Vulkan 1.1 required for external memory extensions
            };

            var instExts = new[] {
                Marshal.StringToHGlobalAnsi("VK_KHR_external_memory_capabilities"),
                Marshal.StringToHGlobalAnsi("VK_KHR_external_semaphore_capabilities")
            };

            nint* extsPtr = stackalloc nint[instExts.Length];
            for (int i = 0; i < instExts.Length; i++) extsPtr[i] = instExts[i];

            var instInfo = new VulkanVk.VkInstanceCreateInfo
            {
                sType = VulkanVk.VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
                pApplicationInfo = &appInfo,
                enabledExtensionCount = (uint)instExts.Length,
                ppEnabledExtensionNames = extsPtr
            };

            nint instance;
            int res = VulkanVk.vkCreateInstance(&instInfo, nint.Zero, &instance);

            // Free temporary string allocations
            Marshal.FreeHGlobal(appInfo.pApplicationName);
            Marshal.FreeHGlobal(appInfo.pEngineName);
            foreach (var ext in instExts) Marshal.FreeHGlobal(ext);

            if (res != VulkanVk.VK_SUCCESS)
            {
                Subsystem.Dg.Warn("directport", $"vkCreateInstance failed: {res}");
                return false;
            }
            _instance = instance;

            // 2. Select Physical Device
            uint count = 0;
            VulkanVk.vkEnumeratePhysicalDevices(_instance, &count, null);
            if (count == 0)
            {
                Subsystem.Dg.Warn("directport", "No physical devices found");
                Shutdown();
                return false;
            }

            nint* physDevices = stackalloc nint[(int)count];
            VulkanVk.vkEnumeratePhysicalDevices(_instance, &count, physDevices);
            _physDevice = physDevices[0]; // Take primary device

            // 3. Find Queue Family
            uint qCount = 0;
            VulkanVk.vkGetPhysicalDeviceQueueFamilyProperties(_physDevice, &qCount, null);
            if (qCount > 0)
            {
                var qProps = stackalloc VulkanVk.VkQueueFamilyProperties[(int)qCount];
                VulkanVk.vkGetPhysicalDeviceQueueFamilyProperties(_physDevice, &qCount, qProps);
                for (uint i = 0; i < qCount; i++)
                {
                    // Look for Graphics queue family (bit 0 = graphics)
                    if ((qProps[i].queueFlags & 0x00000001) != 0)
                    {
                        _queueFamilyIndex = i;
                        break;
                    }
                }
            }

            if (_queueFamilyIndex == uint.MaxValue)
            {
                Subsystem.Dg.Warn("directport", "No suitable queue family found");
                Shutdown();
                return false;
            }

            // 4. Create Logical Device with extensions and Timeline Semaphore features
            float qPri = 1.0f;
            var qInfo = new VulkanVk.VkDeviceQueueCreateInfo
            {
                sType = VulkanVk.VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
                queueFamilyIndex = _queueFamilyIndex,
                queueCount = 1,
                pQueuePriorities = &qPri
            };

            var tsFeatures = new VulkanVk.VkPhysicalDeviceTimelineSemaphoreFeatures
            {
                sType = VulkanVk.VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_TIMELINE_SEMAPHORE_FEATURES,
                timelineSemaphore = VulkanVk.VK_TRUE
            };

            var devExts = new[] {
                Marshal.StringToHGlobalAnsi("VK_KHR_external_memory"),
                Marshal.StringToHGlobalAnsi("VK_KHR_external_memory_fd"),
                Marshal.StringToHGlobalAnsi("VK_KHR_external_semaphore"),
                Marshal.StringToHGlobalAnsi("VK_KHR_external_semaphore_fd"),
                Marshal.StringToHGlobalAnsi("VK_KHR_timeline_semaphore")
            };

            nint* devExtsPtr = stackalloc nint[devExts.Length];
            for (int i = 0; i < devExts.Length; i++) devExtsPtr[i] = devExts[i];

            var devInfo = new VulkanVk.VkDeviceCreateInfo
            {
                sType = VulkanVk.VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
                pNext = (nint)(&tsFeatures),
                queueCreateInfoCount = 1,
                pQueueCreateInfos = &qInfo,
                enabledExtensionCount = (uint)devExts.Length,
                ppEnabledExtensionNames = devExtsPtr
            };

            nint device;
            res = VulkanVk.vkCreateDevice(_physDevice, &devInfo, nint.Zero, &device);

            foreach (var ext in devExts) Marshal.FreeHGlobal(ext);

            if (res != VulkanVk.VK_SUCCESS)
            {
                Subsystem.Dg.Warn("directport", $"vkCreateDevice failed: {res}");
                Shutdown();
                return false;
            }
            _device = device;

            // 5. Resolve extension entry points
            nint pfnGetMemoryFd = VulkanVk.vkGetDeviceProcAddr(_device, "vkGetMemoryFdKHR");
            nint pfnGetSemaphoreFd = VulkanVk.vkGetDeviceProcAddr(_device, "vkGetSemaphoreFdKHR");

            if (pfnGetMemoryFd == nint.Zero || pfnGetSemaphoreFd == nint.Zero)
            {
                Subsystem.Dg.Warn("directport", "Failed to resolve Vulkan external FD extension pointers");
                Shutdown();
                return false;
            }

            _vkGetMemoryFdKHR = Marshal.GetDelegateForFunctionPointer<VulkanVk.vkGetMemoryFdKHR_t>(pfnGetMemoryFd);
            _vkGetSemaphoreFdKHR = Marshal.GetDelegateForFunctionPointer<VulkanVk.vkGetSemaphoreFdKHR_t>(pfnGetSemaphoreFd);

            Subsystem.Dg.Log("directport", "Vulkan DirectPort backend initialized successfully");
            return true;
        }
    }

    public static void Shutdown()
    {
        lock (_lock)
        {
            if (_device != nint.Zero)
            {
                VulkanVk.vkDestroyDevice(_device, nint.Zero);
                _device = nint.Zero;
            }
            if (_instance != nint.Zero)
            {
                VulkanVk.vkDestroyInstance(_instance, nint.Zero);
                _instance = nint.Zero;
            }
            _physDevice = nint.Zero;
            _queueFamilyIndex = uint.MaxValue;
            _vkGetMemoryFdKHR = null;
            _vkGetSemaphoreFdKHR = null;
            Subsystem.Dg.Log("directport", "Vulkan DirectPort backend shut down");
        }
    }

    // Handles the actual Vulkan resources allocated for a single DirectPort share.
    public sealed class SharedResource : IDisposable
    {
        public ulong Image { get; private set; }
        public ulong Memory { get; private set; }
        public ulong Semaphore { get; private set; }

        public int ImageFd { get; private set; } = -1;
        public int SemaphoreFd { get; private set; } = -1;

        public uint Width { get; }
        public uint Height { get; }
        public DpFormat Format { get; }
        public bool IsCpuMappable { get; }

        public SharedResource(uint width, uint height, DpFormat format, bool isCpuMappable)
        {
            Width = width;
            Height = height;
            Format = format;
            IsCpuMappable = isCpuMappable;

            if (_device == nint.Zero) throw new InvalidOperationException("Vulkan is not initialized");

            int vkFmt = format switch
            {
                DpFormat.Video => VulkanVk.VK_FORMAT_B8G8R8A8_UNORM,
                DpFormat.Float => VulkanVk.VK_FORMAT_R32_SFLOAT,
                DpFormat.Half => VulkanVk.VK_FORMAT_R16_SFLOAT,
                DpFormat.Raw32 => VulkanVk.VK_FORMAT_R32_UINT,
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };

            // 1. Create Image with external memory
            var extImageInfo = new VulkanVk.VkExternalMemoryImageCreateInfo
            {
                sType = VulkanVk.VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO,
                handleTypes = VulkanVk.VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_FD_BIT
            };

            var imageInfo = new VulkanVk.VkImageCreateInfo
            {
                sType = VulkanVk.VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
                pNext = (nint)(&extImageInfo),
                imageType = VulkanVk.VK_IMAGE_TYPE_2D,
                format = vkFmt,
                extent = new VulkanVk.VkExtent3D { width = width, height = height, depth = 1 },
                mipLevels = 1,
                arrayLayers = 1,
                samples = VulkanVk.VK_SAMPLE_COUNT_1_BIT,
                tiling = isCpuMappable ? VulkanVk.VK_IMAGE_TILING_LINEAR : VulkanVk.VK_IMAGE_TILING_OPTIMAL,
                usage = VulkanVk.VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VulkanVk.VK_IMAGE_USAGE_TRANSFER_DST_BIT | VulkanVk.VK_IMAGE_USAGE_SAMPLED_BIT | VulkanVk.VK_IMAGE_USAGE_STORAGE_BIT | VulkanVk.VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT,
                sharingMode = 0, // EXCLUSIVE
                initialLayout = VulkanVk.VK_IMAGE_LAYOUT_UNDEFINED
            };

            ulong img;
            int res = VulkanVk.vkCreateImage(_device, &imageInfo, nint.Zero, &img);
            if (res != VulkanVk.VK_SUCCESS) throw new InvalidOperationException($"vkCreateImage failed: {res}");
            Image = img;

            // 2. Allocate memory with export info
            VulkanVk.VkMemoryRequirements reqs;
            VulkanVk.vkGetImageMemoryRequirements(_device, Image, &reqs);

            // Find memory type index
            VulkanVk.VkPhysicalDeviceMemoryProperties memProps;
            VulkanVk.vkGetPhysicalDeviceMemoryProperties(_physDevice, &memProps);
            uint typeBits = reqs.memoryTypeBits;
            uint memTypeIndex = uint.MaxValue;

            uint reqFlags = VulkanVk.VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT;
            if (isCpuMappable) reqFlags = VulkanVk.VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VulkanVk.VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;

            for (uint i = 0; i < memProps.memoryTypeCount; i++)
            {
                if ((typeBits & (1u << (int)i)) != 0 && (memProps.memoryTypes[i].propertyFlags & reqFlags) == reqFlags)
                {
                    memTypeIndex = i;
                    break;
                }
            }

            if (memTypeIndex == uint.MaxValue) throw new InvalidOperationException("Failed to find compatible memory type for Vulkan image");

            var exportAllocInfo = new VulkanVk.VkExportMemoryAllocateInfo
            {
                sType = VulkanVk.VK_STRUCTURE_TYPE_EXPORT_MEMORY_ALLOCATE_INFO,
                handleTypes = VulkanVk.VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_FD_BIT
            };

            var allocInfo = new VulkanVk.VkMemoryAllocateInfo
            {
                sType = VulkanVk.VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
                pNext = (nint)(&exportAllocInfo),
                allocationSize = reqs.size,
                memoryTypeIndex = memTypeIndex
            };

            ulong mem;
            res = VulkanVk.vkAllocateMemory(_device, &allocInfo, nint.Zero, &mem);
            if (res != VulkanVk.VK_SUCCESS)
            {
                VulkanVk.vkDestroyImage(_device, Image, nint.Zero);
                throw new InvalidOperationException($"vkAllocateMemory failed: {res}");
            }
            Memory = mem;

            res = VulkanVk.vkBindImageMemory(_device, Image, Memory, 0);
            if (res != VulkanVk.VK_SUCCESS)
            {
                Dispose();
                throw new InvalidOperationException($"vkBindImageMemory failed: {res}");
            }

            // 3. Create timeline semaphore with export info
            var exportSemInfo = new VulkanVk.VkExportSemaphoreCreateInfo
            {
                sType = VulkanVk.VK_STRUCTURE_TYPE_EXPORT_SEMAPHORE_CREATE_INFO,
                handleTypes = VulkanVk.VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_OPAQUE_FD_BIT
            };

            var semTypeInfo = new VulkanVk.VkSemaphoreTypeCreateInfo
            {
                sType = VulkanVk.VK_STRUCTURE_TYPE_SEMAPHORE_TYPE_CREATE_INFO,
                pNext = (nint)(&exportSemInfo),
                semaphoreType = VulkanVk.VK_SEMAPHORE_TYPE_TIMELINE,
                initialValue = 0
            };

            var semInfo = new VulkanVk.VkSemaphoreCreateInfo
            {
                sType = VulkanVk.VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO,
                pNext = (nint)(&semTypeInfo)
            };

            ulong sem;
            res = VulkanVk.vkCreateSemaphore(_device, &semInfo, nint.Zero, &sem);
            if (res != VulkanVk.VK_SUCCESS)
            {
                Dispose();
                throw new InvalidOperationException($"vkCreateSemaphore failed: {res}");
            }
            Semaphore = sem;

            // 4. Export fds
            int imgFd = -1;
            var getFdInfo = new VulkanVk.VkMemoryGetFdInfoKHR
            {
                sType = VulkanVk.VK_STRUCTURE_TYPE_MEMORY_GET_FD_INFO_KHR,
                memory = Memory,
                handleType = VulkanVk.VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_FD_BIT
            };
            res = _vkGetMemoryFdKHR!(_device, &getFdInfo, &imgFd);
            if (res != VulkanVk.VK_SUCCESS)
            {
                Dispose();
                throw new InvalidOperationException($"Exporting VkDeviceMemory fd failed: {res}");
            }
            ImageFd = imgFd;

            int semFd = -1;
            var getSemFdInfo = new VulkanVk.VkSemaphoreGetFdInfoKHR
            {
                sType = VulkanVk.VK_STRUCTURE_TYPE_SEMAPHORE_GET_FD_INFO_KHR,
                semaphore = Semaphore,
                handleType = VulkanVk.VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_OPAQUE_FD_BIT
            };
            res = _vkGetSemaphoreFdKHR!(_device, &getSemFdInfo, &semFd);
            if (res != VulkanVk.VK_SUCCESS)
            {
                Dispose();
                throw new InvalidOperationException($"Exporting VkSemaphore fd failed: {res}");
            }
            SemaphoreFd = semFd;
        }

        public void Signal(ulong val)
        {
            var sigInfo = new VulkanVk.VkSemaphoreSignalInfo
            {
                sType = VulkanVk.VK_STRUCTURE_TYPE_SEMAPHORE_SIGNAL_INFO,
                semaphore = Semaphore,
                value = val
            };
            int res = VulkanVk.vkSignalSemaphore(_device, &sigInfo);
            if (res != VulkanVk.VK_SUCCESS) throw new InvalidOperationException($"vkSignalSemaphore failed: {res}");
        }

        public void Wait(ulong val, ulong timeout = ulong.MaxValue)
        {
            ulong sem = Semaphore;
            ulong v = val;
            var waitInfo = new VulkanVk.VkSemaphoreWaitInfo
            {
                sType = VulkanVk.VK_STRUCTURE_TYPE_SEMAPHORE_WAIT_INFO,
                semaphoreCount = 1,
                pSemaphores = &sem,
                pValues = &v
            };
            int res = VulkanVk.vkWaitSemaphores(_device, &waitInfo, timeout);
            if (res != VulkanVk.VK_SUCCESS) throw new InvalidOperationException($"vkWaitSemaphores failed: {res}");
        }

        public ulong GetCompletedValue()
        {
            ulong val = 0;
            int res = VulkanVk.vkGetSemaphoreCounterValue(_device, Semaphore, &val);
            if (res != VulkanVk.VK_SUCCESS) throw new InvalidOperationException($"vkGetSemaphoreCounterValue failed: {res}");
            return val;
        }

        public void Dispose()
        {
            if (Semaphore != 0)
            {
                VulkanVk.vkDestroySemaphore(_device, Semaphore, nint.Zero);
                Semaphore = 0;
            }
            if (Memory != 0)
            {
                VulkanVk.vkFreeMemory(_device, Memory, nint.Zero);
                Memory = 0;
            }
            if (Image != 0)
            {
                VulkanVk.vkDestroyImage(_device, Image, nint.Zero);
                Image = 0;
            }
            if (ImageFd != -1)
            {
                close(ImageFd);
                ImageFd = -1;
            }
            if (SemaphoreFd != -1)
            {
                close(SemaphoreFd);
                SemaphoreFd = -1;
            }
        }

        [DllImport("libc")]
        private static extern int close(int fd);
    }

    public static bool TryMount(uint width, uint height, DpFormat fmt, bool cpuMappable,
                                string name, out SharedResource? resource)
    {
        if (!Init())
        {
            resource = null;
            return false;
        }

        try
        {
            var res = new SharedResource(width, height, fmt, cpuMappable);
            var owner = Vom.Vom.CreateOwner("\\Surfaces");

            // We register the SharedResource in VOM so it gets closed at refcount-zero or Terminate
            Vom.Vom.RegisterNative(owner, "DirectPortVkResource", (nint)0, reclaim: () =>
            {
                res.Dispose();
            }, byteCount: 0, format: VomFormat.Bytes, subdir: "", name: name);

            Subsystem.Dg.Log("directport", $"mounted \\Surfaces\\{name} ({width}x{height}, cpuMappable={cpuMappable})");
            resource = res;
            return true;
        }
        catch (Exception ex)
        {
            Subsystem.Dg.Warn("directport", $"Failed to mount \\Surfaces\\{name}: {ex.Message}");
            resource = null;
            return false;
        }
    }
}
