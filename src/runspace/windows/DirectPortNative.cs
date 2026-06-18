using System;
using System.Runtime.InteropServices;

namespace Subsystem.Windows;

// DirectPortNative — the home-spun P/Invoke binding to directport.dll (our vendored build of Scott's
// DirectPort D3D12 C API) PLUS the minimal Win32 interop to publish the VirtuaCam BroadcastManifest.
// ALL foreign names are fenced to this one file (ouroboros rule 0); classic [DllImport], never the
// source-generated [LibraryImport] — `ss build self` runs no source generators. Windows-only seam:
// it lives in host-windows and never compiles into the Android head.
//
// Ground truth is directport.h (the dp12_* exports) and the canonical BroadcastManifest struct read
// from VirtuaCam's source (Discovery.cpp / Broker.cpp), NOT the Gemini draft (whose field order and
// packing were wrong). The producer protocol: create a shared texture + fence with Global\ NT names,
// publish a session-local manifest mapping named DirectPort_Producer_Manifest_<PID> that points at
// them, then each frame signal the fence and bump FrameValue.
internal static unsafe class DirectPortNative
{
    private const string Lib = "directport";

    public enum DpFormat { Video = 0, Float = 1, Half = 2, Raw32 = 3 } // mirrors DP_FORMAT in directport.h

    // ---- directport.dll (the dp12_* C API) -------------------------------------------------------

    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool dp12_init();

    [DllImport(Lib)]
    public static extern void dp12_shutdown();

    [DllImport(Lib, CharSet = CharSet.Unicode)]
    public static extern IntPtr dp12_create_shared_resource(
        uint width, uint height, int format, [MarshalAs(UnmanagedType.I1)] bool is_system_ram,
        [MarshalAs(UnmanagedType.LPWStr)] string tex_name,
        [MarshalAs(UnmanagedType.LPWStr)] string fence_name);

    [DllImport(Lib)]
    public static extern IntPtr dp12_map_memory(IntPtr handle, out uint out_row_pitch);

    [DllImport(Lib)]
    public static extern void dp12_unmap_memory(IntPtr handle);

    [DllImport(Lib)]
    public static extern void dp12_signal_fence(IntPtr handle, ulong frame_value);

    [DllImport(Lib)]
    public static extern ulong dp12_get_completed_value(IntPtr handle);

    // LOCAL addition (see directport.h): the device's adapter LUID, packed little-endian into int64.
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool dp12_get_adapter_luid(out long out_luid);

    [DllImport(Lib)]
    public static extern int dp12_last_hresult();

    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool dp12_is_uma();

    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool dp12_upload_bgra(IntPtr handle, IntPtr src, uint src_row_pitch,
                                               uint width, uint height, ulong frame_value);

    [DllImport(Lib)]
    public static extern void dp12_close(IntPtr handle);

    // ---- The interop contract: BroadcastManifest --------------------------------------------------
    // VERIFIED byte layout (canonical struct, default pack(8); size 1056). LUID is two 4-byte members so
    // its natural alignment is 4 — modeled as a nested struct so AdapterLuid lands at offset 20 exactly
    // as C++ places it (a bare `long` would 8-align to offset 24 and corrupt every field after it).
    //   FrameValue  ulong @0    Width uint @8    Height uint @12   Format int @16
    //   AdapterLuid Luid  @20   TextureName char[256] @28   FenceName char[256] @540   Command int @1052

    [StructLayout(LayoutKind.Sequential)]
    public struct Luid
    {
        public uint LowPart;
        public int  HighPart;
        public static Luid FromInt64(long v) => new Luid { LowPart = (uint)(v & 0xFFFFFFFF), HighPart = (int)(v >> 32) };
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct BroadcastManifest
    {
        public ulong FrameValue;             // offset 0  — monotonic; readers dirty-read this for "new frame"
        public uint  Width;                  // offset 8
        public uint  Height;                 // offset 12
        public int   Format;                 // offset 16 — DXGI_FORMAT (87 = B8G8R8A8_UNORM)
        public Luid  AdapterLuid;            // offset 20 — broker's same-GPU filter
        public fixed char TextureName[256];  // offset 28  — Global\ NT name of the shared texture
        public fixed char FenceName[256];    // offset 540 — Global\ NT name of the shared fence
        public int   Command;                // offset 1052 — VCamCommand (None = 0)
    }

    public const int ManifestSize = 1056;

    // ---- Win32 interop to publish the manifest as a named file mapping (with a DACL) ---------------
    // .NET's MemoryMappedFile.CreateNew dropped the security-descriptor overload on .NET Core, so we
    // create the mapping the same way the C++ producers do: CreateFileMappingW with a SECURITY_ATTRIBUTES
    // carrying the projected SDDL. That's the cross-process form of the capability's reach.

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public uint   nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.I1)] public bool bInheritHandle;
    }

    private const uint PAGE_READWRITE      = 0x04;
    private const uint FILE_MAP_ALL_ACCESS = 0xF001F;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMappingW(IntPtr hFile, ref SECURITY_ATTRIBUTES lpAttributes,
        uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hMap, uint dwDesiredAccess,
        uint offHigh, uint offLow, UIntPtr bytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out IntPtr ppSecurityDescriptor, IntPtr pSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr h);

    // A live manifest mapping: the kernel handle, the mapped view, and a typed pointer for atomic
    // FrameValue updates. Close() reverses creation. Returns null on failure (caller degrades).
    public sealed unsafe class ManifestMap : IDisposable
    {
        private IntPtr _hMap;
        private IntPtr _view;
        public BroadcastManifest* Ptr { get; private set; }

        private ManifestMap(IntPtr hMap, IntPtr view)
        {
            _hMap = hMap; _view = view; Ptr = (BroadcastManifest*)view;
        }

        // Create DirectPort_Producer_Manifest_<PID> (session-local — Discovery opens it WITHOUT Global\)
        // secured by `sddl`, zero it, and return the live map. Null on any failure.
        public static ManifestMap? Create(string name, string sddl)
        {
            IntPtr sd = IntPtr.Zero;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out sd, IntPtr.Zero))
                return null;
            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = sd,
                bInheritHandle = false,
            };
            IntPtr hMap = CreateFileMappingW(INVALID_HANDLE_VALUE, ref sa, PAGE_READWRITE, 0, ManifestSize, name);
            LocalFree(sd);
            if (hMap == IntPtr.Zero) return null;

            IntPtr view = MapViewOfFile(hMap, FILE_MAP_ALL_ACCESS, 0, 0, (UIntPtr)ManifestSize);
            if (view == IntPtr.Zero) { CloseHandle(hMap); return null; }

            new Span<byte>((void*)view, ManifestSize).Clear();
            return new ManifestMap(hMap, view);
        }

        public void Dispose()
        {
            if (_view != IntPtr.Zero) { UnmapViewOfFile(_view); _view = IntPtr.Zero; Ptr = null; }
            if (_hMap != IntPtr.Zero) { CloseHandle(_hMap); _hMap = IntPtr.Zero; }
        }
    }

    // Copy up to cap-1 chars of `s` into a fixed WCHAR buffer and null-terminate (wcscpy_s semantics).
    public static void WriteFixed(char* dst, int cap, string s)
    {
        int n = Math.Min(s.Length, cap - 1);
        for (int i = 0; i < n; i++) dst[i] = s[i];
        dst[n] = '\0';
    }
}
