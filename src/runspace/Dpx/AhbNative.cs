using System;
using System.Runtime.InteropServices;

namespace Subsystem.Dpx
{
    // AHardwareBuffer — Android's shared-region primitive, the DirectPort contract native to this OS:
    // one allocation, CPU-mapped and GPU-imported views of the SAME memory (probed on the razr:
    // VK_EXT_external_memory_host is absent on Adreno; VK_ANDROID_external_memory_android_hardware_buffer
    // is present). q4 weight storage allocates here at model load, the CPU fills it once through Lock,
    // and GpuVulkan imports the buffer as VkDeviceMemory — zero copies, no per-call upload.
    //
    // Pure P/Invoke on libandroid.so (NDK, API 26+): resolved at first call, so this file compiles into
    // BOTH heads — the Windows head simply never touches it (same discipline as DpxQnn/GpuVulkan).
    internal static unsafe class AhbNative
    {
        // AHardwareBuffer_Desc (hardware_buffer.h). BLOB usage: width = byte length, height/layers = 1.
        [StructLayout(LayoutKind.Sequential)]
        public struct Desc
        {
            public uint Width, Height, Layers, Format;
            public ulong Usage;
            public uint Stride, Rfu0;
            public ulong Rfu1;
        }

        public const uint FormatBlob = 0x21;                       // AHARDWAREBUFFER_FORMAT_BLOB
        public const ulong UsageCpuWriteRarely = 0x20;             // fill once at model load
        public const ulong UsageCpuReadRarely  = 0x2;              // CPU fallback rung reads in place
        public const ulong UsageGpuDataBuffer  = 1UL << 24;        // VkBuffer import (GPU_DATA_BUFFER)

        [DllImport("libandroid.so")]
        public static extern int AHardwareBuffer_allocate(in Desc desc, out IntPtr buffer);

        [DllImport("libandroid.so")]
        public static extern void AHardwareBuffer_release(IntPtr buffer);

        // fence = -1 (none); rect = null for BLOB. outVirtualAddress maps the whole allocation.
        [DllImport("libandroid.so")]
        public static extern int AHardwareBuffer_lock(IntPtr buffer, ulong usage, int fence, IntPtr rect, out IntPtr virtualAddress);

        [DllImport("libandroid.so")]
        public static extern int AHardwareBuffer_unlock(IntPtr buffer, IntPtr fence);

        // Read a buffer's descriptor back (the GpuVulkan import path reads Width = byte length without the
        // caller threading it, and can assert Format==BLOB before importing).
        [DllImport("libandroid.so")]
        public static extern void AHardwareBuffer_describe(IntPtr buffer, out Desc desc);

        // Allocate a BLOB AHB of `bytes`, lock it CPU-writable, and hand back (buffer, mapped pointer).
        // The caller fills the mapping, then keeps BOTH: the pointer is the tensor's native backing for
        // the CPU rung, the buffer handle is what GpuVulkan imports. Unlock happens at reclaim — the
        // mapping stays valid for the buffer's lifetime on UMA. Throws on failure (the caller's rung
        // ladder degrades to the managed-array path — absence, not error).
        public static (IntPtr Buffer, IntPtr Mapped) AllocBlob(long bytes)
        {
            var desc = new Desc
            {
                Width = checked((uint)bytes), Height = 1, Layers = 1, Format = FormatBlob,
                Usage = UsageCpuWriteRarely | UsageCpuReadRarely | UsageGpuDataBuffer,
            };
            int rc = AHardwareBuffer_allocate(in desc, out IntPtr buf);
            if (rc != 0) throw new InvalidOperationException($"AHardwareBuffer_allocate rc={rc} ({bytes} bytes)");
            rc = AHardwareBuffer_lock(buf, UsageCpuWriteRarely | UsageCpuReadRarely, -1, IntPtr.Zero, out IntPtr mapped);
            if (rc != 0) { AHardwareBuffer_release(buf); throw new InvalidOperationException($"AHardwareBuffer_lock rc={rc}"); }
            return (buf, mapped);
        }

        // Release an AllocBlob'd buffer: unlock the CPU map, then release the handle — paired and colocated
        // so the two never drift. This is the Reclaim action registered with Vom.RegisterNative; it runs
        // once at the weight handle's refcount-zero (free-at-zero).
        public static void Free(IntPtr buffer, IntPtr mapped)
        {
            _ = mapped;   // the map is owned by the buffer; unlock releases it
            if (buffer == IntPtr.Zero) return;
            try { AHardwareBuffer_unlock(buffer, IntPtr.Zero); } catch (Exception ex) { Subsystem.Dg.Warn("dpx", ex); }
            AHardwareBuffer_release(buffer);
        }
    }
}
