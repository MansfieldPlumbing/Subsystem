// DirectPortVk.cs — DRAFT home-spun [DllImport] binding to the Android Vulkan DirectPort fabric
// (directport.android.vulkan.cpp). The VOM is the guide: a shared VkImage + timeline semaphore is
// registered as ONE VOM handle under \Surfaces\<name>, so the kill switch (DropPrefix/Terminate)
// reaches dp_vk_close — free-at-zero across processes. Classic [DllImport] (ouroboros rule 0).
// ALL foreign names (dp_vk_*) are fenced to THIS file (surge-protector doctrine), exactly like
// LiteRtNative.cs. Above this seam = ours (the \Surfaces mount, the Fence, the VOM handle).
//
// STATUS: draft. Depends on (a) libdirectport.so built for arm64-v8a from the Vulkan .cpp, with the
// review fixes applied (unistd.h, DP_EXPORT visibility, fd double-close, GPU queue-signal variant);
// (b) a Vom native-register variant that takes a raw pointer + a Reclaim delegate (see MountSurface).
//
// Cross-process note: dp_vk_create returns opaque Unix fds (out_image_fd/out_semaphore_fd) that must
// be handed to the consumer process via SCM_RIGHTS over a Unix domain socket (sendmsg/recvmsg) — that
// is the ON-DEVICE fabric hop (phone capture proc -> encoder proc). It is NOT the cross-device wire;
// the cross-device hop is encode -> Kestrel tunnel -> decode.

using System;
using System.Runtime.InteropServices;

namespace Subsystem;   // host/interop seam — add this file to SystemCatalog.json hostPaths

internal static class DirectPortVk
{
    // libdirectport.so (the Android Vulkan build). Foreign asset filename, fenced here only.
    private const string Lib = "directport";

    // DP_FORMAT — media-agnostic layout (directport.h). Matches VomFormat in spirit (layout, not meaning).
    public enum DpFormat { Video = 0, Float = 1, Half = 2, Raw32 = 3 }

    // --- lifecycle (global singleton in the .so; call once per process) ---
    [DllImport(Lib)] public static extern bool dp_vk_init();
    [DllImport(Lib)] public static extern void dp_vk_shutdown();

    // --- producer: create a shared resource; exports opaque fds for SCM_RIGHTS transmit ---
    [DllImport(Lib)] public static extern IntPtr dp_vk_create_shared_resource(
        uint width, uint height, DpFormat format, [MarshalAs(UnmanagedType.I1)] bool isCpuMappable,
        out int outImageFd, out int outSemaphoreFd);

    // --- consumer: open from received fds; metadata MUST match the producer (opaque fds carry none) ---
    [DllImport(Lib)] public static extern IntPtr dp_vk_open_shared_resource(
        uint width, uint height, DpFormat format, [MarshalAs(UnmanagedType.I1)] bool isCpuMappable,
        int imageFd, int semaphoreFd);

    // --- CPU access (is_cpu_mappable only): base ptr + driver-authoritative row pitch ---
    [DllImport(Lib)] public static extern IntPtr dp_vk_map_memory(IntPtr handle, out uint outRowPitch);
    [DllImport(Lib)] public static extern void dp_vk_unmap_memory(IntPtr handle);

    // --- fence (timeline semaphore), value-based ---
    // NOTE (review BUG 3): dp_vk_signal_fence host-signals; correct for the CPU-mapped path, RACY for the
    // GPU-rendered path. A queue-signal variant is needed before binding a GPU producer.
    [DllImport(Lib)] public static extern void dp_vk_signal_fence(IntPtr handle, ulong frameValue);
    [DllImport(Lib)] public static extern void dp_vk_cpu_wait(IntPtr handle, ulong targetValue);
    [DllImport(Lib)] public static extern ulong dp_vk_get_completed_value(IntPtr handle);
    // dp_vk_queue_wait(handle, VkQueue, value) is intentionally NOT bound: the .so hides its VkDevice/
    // VkQueue (review ARCH LIMIT), so an external caller has no queue to pass. Bind once the device is
    // exposed or accepted at init.

    [DllImport(Lib)] public static extern int dp_vk_get_image_fd(IntPtr handle);
    [DllImport(Lib)] public static extern int dp_vk_get_semaphore_fd(IntPtr handle);
    [DllImport(Lib)] public static extern void dp_vk_close(IntPtr handle);
}

// ---------------------------------------------------------------------------------------------------
// VOM MOUNT (sketch) — a DirectPort surface is a first-class VOM object, the Windows-Surface analog
// ("a Surface at \Surfaces\wallpaper"; here a VkImage at \Surfaces\<name>). The DP_HANDLE is the
// Resource; the timeline semaphore is the VOM Fence; dp_vk_close is the Reclaim (free-at-zero).
// 256-byte pitch parity: dp_vk_map_memory's row pitch already equals Vom.Alloc's alignment intent, so
// a VOM native staging buffer can feed the CPU-mapped image without realignment.
//
// This needs a Vom register variant that owns a NATIVE pointer with a custom Reclaim delegate (the VOM
// today has Alloc for native bytes and Register for managed objects via GCHandle; a DirectPort surface
// is "native handle + custom close", which is the Reclaim-closure pattern Alloc already uses).
// ---------------------------------------------------------------------------------------------------
internal static class DirectPortSurface
{
    // Producer: create + mount. Returns the VOM handle; the exported fds go to the encoder proc (SCM_RIGHTS).
    public static bool TryMount(uint width, uint height, DirectPortVk.DpFormat fmt, bool cpuMappable,
                                string name, out IntPtr dpHandle, out int imageFd, out int semaphoreFd)
    {
        dpHandle = DirectPortVk.dp_vk_create_shared_resource(width, height, fmt, cpuMappable,
                                                             out imageFd, out semaphoreFd);
        if (dpHandle == IntPtr.Zero)
        {
            imageFd = -1; semaphoreFd = -1;
            Subsystem.Dg.Warn("directport", $"dp_vk_create_shared_resource failed for \\Surfaces\\{name}");
            return false;
        }
        // TODO: Vom.RegisterNative(owner, "DirectPortSurface", dpHandle, reclaim: () => DirectPortVk.dp_vk_close(dpHandle),
        //                          subdir: "Surfaces", name: name);  // refcounted; Terminate -> dp_vk_close (free-at-zero)
        Subsystem.Dg.Log("directport", $"mounted \\Surfaces\\{name} ({width}x{height}, cpuMappable={cpuMappable})");
        return true;
    }
}
