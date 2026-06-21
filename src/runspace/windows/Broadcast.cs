using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Subsystem.Windows;

// Broadcast — the managed CONSUMER half of DirectPort (the producer half is DirectPortNative: create the
// shared resource, publish a ManifestMap, signal the fence). A Broadcast follows the canonical five-phase
// adapter lifecycle from DirectPort_Adapter_Protocol.md:
//   DISCOVER  — map the producer's BroadcastManifest from the NT namespace (OpenFileMappingW).
//   OPEN      — resolve the manifest's named texture+fence into a DirectPort handle (dp_open_shared_resource).
//   SYNC      — wait for the producer's current frame (CPU wait; the GPU-side wait is the device path).
//   PRESENT   — here, the system-RAM path: map the region and hand the CPU pointer to the reader.
//   RELEASE   — close the DirectPort handle, unmap the manifest, free the kernel handle.
//
// This is the system-RAM path (producers created with is_system_ram=true, so dp_map_memory is valid) — the
// simplest measurable loop, modeled on the protocol's WASAPI adapter. The zero-copy GPU-device path layers on
// dp_get_resource_handle / dp_get_fence_handle later. Windows seam (host-only, never the Android head);
// the VOM Broadcast-handle promotion (the #12 kernel-boundary / authority-inversion decision) is a deliberate
// later step — for now this is a plain host capability sitting beside the existing producer code.
internal sealed unsafe class Broadcast : IDisposable
{
    private IntPtr _dp;            // DP_HANDLE from dp_open_shared_resource
    private IntPtr _manifestMap;   // OpenFileMappingW kernel handle
    private DirectPortNative.BroadcastManifest* _manifest;   // mapped read-only view of the producer's manifest
    private ulong  _lastFrame;     // last frame this consumer presented (dirty-read latch)

    public uint  Width      => _manifest != null ? _manifest->Width  : 0;
    public uint  Height     => _manifest != null ? _manifest->Height : 0;
    public int   Format     => _manifest != null ? _manifest->Format : 0;   // DXGI_FORMAT
    public ulong FrameValue => _manifest != null ? Volatile.Read(ref _manifest->FrameValue) : 0;

    private Broadcast() { }

    // DISCOVER + OPEN. `manifestName` is a producer manifest name — process-scoped
    // DirectPort_Producer_Manifest_<PID>, or a named Global\<service>_Manifest. Returns null when the producer
    // is absent or its handles can't be resolved on this adapter (the caller degrades, never throws).
    public static Broadcast? Open(string manifestName)
    {
        // DISCOVER — map the producer's manifest read-only.
        IntPtr map = OpenFileMappingW(FILE_MAP_READ, false, manifestName);
        if (map == IntPtr.Zero) return null;
        IntPtr view = MapViewOfFile(map, FILE_MAP_READ, 0, 0, (UIntPtr)DirectPortNative.ManifestSize);
        if (view == IntPtr.Zero) { CloseHandle(map); return null; }
        var m = (DirectPortNative.BroadcastManifest*)view;

        // OPEN — bring up the DirectPort D3D12 singleton, then resolve the named texture+fence into a handle.
        if (!DirectPortNative.dp_init()) { UnmapViewOfFile(view); CloseHandle(map); return null; }
        string tex   = new string(m->TextureName);
        string fence = new string(m->FenceName);
        IntPtr dp = DirectPortNative.dp_open_shared_resource(tex, fence);
        if (dp == IntPtr.Zero) { UnmapViewOfFile(view); CloseHandle(map); return null; }

        return new Broadcast { _dp = dp, _manifestMap = map, _manifest = m };
    }

    // SYNC + PRESENT (system-RAM). If the producer has pushed a frame newer than the last one presented, wait
    // for it to complete, map the region, and hand (cpuPtr, rowPitch, width, height) to `read`. Returns false
    // when there is no new frame (the caller repeats the last frame or idles). The reader must not retain the
    // pointer past the callback — the region is unmapped on return.
    public bool TryRead(Action<IntPtr, uint, uint, uint> read)
    {
        if (read == null) throw new ArgumentNullException(nameof(read));
        if (_manifest == null) return false;

        ulong frame = _manifest->FrameValue;     // dirty-read the producer's latest frame
        if (frame <= _lastFrame) return false;   // nothing new

        DirectPortNative.dp_cpu_wait(_dp, frame);                       // SYNC
        IntPtr cpu = DirectPortNative.dp_map_memory(_dp, out uint pitch); // PRESENT (system-RAM only)
        if (cpu == IntPtr.Zero) return false;
        try { read(cpu, pitch, _manifest->Width, _manifest->Height); }
        finally { DirectPortNative.dp_unmap_memory(_dp); }

        _lastFrame = frame;
        return true;
    }

    // SYNC only (no PRESENT) — block until the producer's fence reaches `frame`, observed through this
    // consumer's own handle. Used by the latency harness and the GPU-resource path (no CPU map to present).
    public void WaitFrame(ulong frame) => DirectPortNative.dp_cpu_wait(_dp, frame);

    // RELEASE — close the DirectPort handle (decrements the NT refcount; VRAM frees at zero across processes),
    // unmap the manifest view, close the kernel handle. Idempotent.
    public void Dispose()
    {
        if (_dp != IntPtr.Zero) { DirectPortNative.dp_close(_dp); _dp = IntPtr.Zero; }
        if (_manifest != null)  { UnmapViewOfFile((IntPtr)_manifest); _manifest = null; }
        if (_manifestMap != IntPtr.Zero) { CloseHandle(_manifestMap); _manifestMap = IntPtr.Zero; }
    }

    // ---- Win32 interop for the read-only manifest view (fenced to this seam, classic [DllImport]) ----
    private const uint FILE_MAP_READ = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenFileMappingW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.I1)] bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hMap, uint dwDesiredAccess, uint offHigh, uint offLow, UIntPtr bytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
