using System;
using System.Runtime.InteropServices;

namespace Subsystem.Device.Android;

// VulkanVk — pure C# Vulkan bindings for Android. Exposes the minimal set of structures,
// constants, and entry points required to allocate a shared VkImage and export opaque fds.
internal static unsafe class VulkanVk
{
    private const string Lib = "vulkan";

    // ---- Constants -------------------------------------------------------------
    public const int VK_SUCCESS = 0;
    public const int VK_TRUE = 1;
    public const int VK_FALSE = 0;

    public const int VK_STRUCTURE_TYPE_APPLICATION_INFO = 0;
    public const int VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO = 1;
    public const int VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO = 2;
    public const int VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO = 3;
    public const int VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO = 9;
    public const int VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO = 14;
    public const int VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO = 37;
    public const int VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2 = 1000094000;
    public const int VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_TIMELINE_SEMAPHORE_FEATURES = 1000100000;
    public const int VK_STRUCTURE_TYPE_SEMAPHORE_TYPE_CREATE_INFO = 1000100002;
    public const int VK_STRUCTURE_TYPE_SEMAPHORE_WAIT_INFO = 1000100004;
    public const int VK_STRUCTURE_TYPE_SEMAPHORE_SIGNAL_INFO = 1000100005;

    // External Memory & Semaphore KHR extensions
    public const int VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO = 1000072000;
    public const int VK_STRUCTURE_TYPE_EXPORT_MEMORY_ALLOCATE_INFO = 1000072002;
    public const int VK_STRUCTURE_TYPE_MEMORY_GET_FD_INFO_KHR = 1000072008;
    public const int VK_STRUCTURE_TYPE_EXPORT_SEMAPHORE_CREATE_INFO = 1000077000;
    public const int VK_STRUCTURE_TYPE_SEMAPHORE_GET_FD_INFO_KHR = 1000077003;

    public const int VK_IMAGE_TYPE_2D = 1;
    public const int VK_IMAGE_TILING_OPTIMAL = 0;
    public const int VK_IMAGE_TILING_LINEAR = 1;
    public const int VK_IMAGE_LAYOUT_UNDEFINED = 0;

    public const int VK_IMAGE_USAGE_TRANSFER_SRC_BIT = 0x00000001;
    public const int VK_IMAGE_USAGE_TRANSFER_DST_BIT = 0x00000002;
    public const int VK_IMAGE_USAGE_SAMPLED_BIT = 0x00000004;
    public const int VK_IMAGE_USAGE_STORAGE_BIT = 0x00000008;
    public const int VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT = 0x00000010;

    public const int VK_SAMPLE_COUNT_1_BIT = 0x00000001;
    public const int VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT = 0x00000001;
    public const int VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT = 0x00000002;
    public const int VK_MEMORY_PROPERTY_HOST_COHERENT_BIT = 0x00000004;

    public const int VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_FD_BIT = 0x00000001;
    public const int VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_OPAQUE_FD_BIT = 0x00000001;
    public const int VK_SEMAPHORE_TYPE_TIMELINE = 1;

    public const int VK_FORMAT_B8G8R8A8_UNORM = 44;
    public const int VK_FORMAT_R32_SFLOAT = 100;
    public const int VK_FORMAT_R16_SFLOAT = 76;
    public const int VK_FORMAT_R32_UINT = 98;

    // ---- Structures ------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct VkApplicationInfo
    {
        public int sType;
        public nint pNext;
        public nint pApplicationName;
        public uint applicationVersion;
        public nint pEngineName;
        public uint engineVersion;
        public uint apiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkInstanceCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public VkApplicationInfo* pApplicationInfo;
        public uint enabledLayerCount;
        public nint* ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public nint* ppEnabledExtensionNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkDeviceQueueCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint queueFamilyIndex;
        public uint queueCount;
        public float* pQueuePriorities;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkPhysicalDeviceTimelineSemaphoreFeatures
    {
        public int sType;
        public nint pNext;
        public int timelineSemaphore;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkPhysicalDeviceFeatures2
    {
        public int sType;
        public nint pNext;
        public int robustBufferAccess;
        // Remaining features truncated (not needed for simple timeline/ext memory setup)
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkDeviceCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint queueCreateInfoCount;
        public VkDeviceQueueCreateInfo* pQueueCreateInfos;
        public uint enabledLayerCount;
        public nint* ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public nint* ppEnabledExtensionNames;
        public nint pEnabledFeatures;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkExtent3D
    {
        public uint width;
        public uint height;
        public uint depth;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkExternalMemoryImageCreateInfo
    {
        public int sType;
        public nint pNext;
        public int handleTypes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkImageCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public int imageType;
        public int format;
        public VkExtent3D extent;
        public uint mipLevels;
        public uint arrayLayers;
        public int samples;
        public int tiling;
        public uint usage;
        public int sharingMode;
        public uint queueFamilyIndexCount;
        public uint* pQueueFamilyIndices;
        public int initialLayout;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkMemoryRequirements
    {
        public ulong size;
        public ulong alignment;
        public uint memoryTypeBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkExportMemoryAllocateInfo
    {
        public int sType;
        public nint pNext;
        public int handleTypes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkMemoryAllocateInfo
    {
        public int sType;
        public nint pNext;
        public ulong allocationSize;
        public uint memoryTypeIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkExportSemaphoreCreateInfo
    {
        public int sType;
        public nint pNext;
        public int handleTypes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkSemaphoreTypeCreateInfo
    {
        public int sType;
        public nint pNext;
        public int semaphoreType;
        public ulong initialValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkSemaphoreCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkMemoryGetFdInfoKHR
    {
        public int sType;
        public nint pNext;
        public ulong memory;
        public int handleType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkSemaphoreGetFdInfoKHR
    {
        public int sType;
        public nint pNext;
        public ulong semaphore;
        public int handleType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkSemaphoreWaitInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint semaphoreCount;
        public ulong* pSemaphores;
        public ulong* pValues;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkSemaphoreSignalInfo
    {
        public int sType;
        public nint pNext;
        public ulong semaphore;
        public ulong value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkQueueFamilyProperties
    {
        public uint queueFlags;
        public uint queueCount;
        public uint timestampValidBits;
        public VkExtent3D minImageTransferGranularity;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkPhysicalDeviceMemoryProperties
    {
        public uint memoryTypeCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public VkMemoryType[] memoryTypes;
        public uint memoryHeapCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public VkMemoryHeap[] memoryHeaps;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkMemoryType
    {
        public uint propertyFlags;
        public uint heapIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkMemoryHeap
    {
        public ulong size;
        public uint flags;
    }

    // ---- P/Invokes (Core) -------------------------------------------------------
    [DllImport(Lib)]
    public static extern nint vkGetInstanceProcAddr(nint instance, string pName);

    [DllImport(Lib)]
    public static extern nint vkGetDeviceProcAddr(nint device, string pName);

    [DllImport(Lib)]
    public static extern int vkCreateInstance(VkInstanceCreateInfo* pCreateInfo, nint pAllocator, nint* pInstance);

    [DllImport(Lib)]
    public static extern void vkDestroyInstance(nint instance, nint pAllocator);

    [DllImport(Lib)]
    public static extern int vkEnumeratePhysicalDevices(nint instance, uint* pPhysicalDeviceCount, nint* pPhysicalDevices);

    [DllImport(Lib)]
    public static extern void vkGetPhysicalDeviceQueueFamilyProperties(nint physicalDevice, uint* pQueueFamilyPropertyCount, VkQueueFamilyProperties* pQueueFamilyProperties);

    [DllImport(Lib)]
    public static extern void vkGetPhysicalDeviceMemoryProperties(nint physicalDevice, VkPhysicalDeviceMemoryProperties* pMemoryProperties);

    [DllImport(Lib)]
    public static extern int vkCreateDevice(nint physicalDevice, VkDeviceCreateInfo* pCreateInfo, nint pAllocator, nint* pDevice);

    [DllImport(Lib)]
    public static extern void vkDestroyDevice(nint device, nint pAllocator);

    [DllImport(Lib)]
    public static extern int vkCreateImage(nint device, VkImageCreateInfo* pCreateInfo, nint pAllocator, ulong* pImage);

    [DllImport(Lib)]
    public static extern void vkDestroyImage(nint device, ulong image, nint pAllocator);

    [DllImport(Lib)]
    public static extern void vkGetImageMemoryRequirements(nint device, ulong image, VkMemoryRequirements* pMemoryRequirements);

    [DllImport(Lib)]
    public static extern int vkAllocateMemory(nint device, VkMemoryAllocateInfo* pAllocateInfo, nint pAllocator, ulong* pMemory);

    [DllImport(Lib)]
    public static extern void vkFreeMemory(nint device, ulong memory, nint pAllocator);

    [DllImport(Lib)]
    public static extern int vkBindImageMemory(nint device, ulong image, ulong memory, ulong memoryOffset);

    [DllImport(Lib)]
    public static extern int vkCreateSemaphore(nint device, VkSemaphoreCreateInfo* pCreateInfo, nint pAllocator, ulong* pSemaphore);

    [DllImport(Lib)]
    public static extern void vkDestroySemaphore(nint device, ulong semaphore, nint pAllocator);

    [DllImport(Lib)]
    public static extern int vkGetSemaphoreCounterValue(nint device, ulong semaphore, ulong* pValue);

    [DllImport(Lib)]
    public static extern int vkSignalSemaphore(nint device, VkSemaphoreSignalInfo* pSignalInfo);

    [DllImport(Lib)]
    public static extern int vkWaitSemaphores(nint device, VkSemaphoreWaitInfo* pWaitInfo, ulong timeout);

    // ---- Extension Delegates ----------------------------------------------------
    public delegate int vkGetMemoryFdKHR_t(nint device, VkMemoryGetFdInfoKHR* pGetFdInfo, int* pFd);
    public delegate int vkGetSemaphoreFdKHR_t(nint device, VkSemaphoreGetFdInfoKHR* pGetFdInfo, int* pFd);
}
