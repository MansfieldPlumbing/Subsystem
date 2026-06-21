using System;
using System.Runtime.InteropServices;

namespace Subsystem.Windows;

// VirtuaCamNative — the home-spun P/Invoke binding to DirectPortBroker.dll (the VirtuaCam broker), the
// native seam the managed gateway (`ss camera`, Camera.cs) hosts. ALL foreign names are fenced to this
// one file (ouroboros rule 0): the broker's exported C function names, the artifact file names, and the
// broker-state enum. Classic [DllImport], never the source-generated [LibraryImport] — `ss build self`
// runs no source generators.
//
// Ground truth is VirtuaCam's Broker.cpp public API (studied, not imported): VirtuaCam.exe loaded
// DirectPortBroker.dll and drove it as a render loop — InitializeBroker once, then RenderBrokerFrame at
// ~30 fps until shutdown. The managed gateway IS that host now: producers (ss surface, the capture
// producers) stay SEPARATE PROCESSES over DirectPort; the broker + the MF client are the consumer side,
// hosted here. Windows-only seam: it never compiles into the Android head.
internal static class VirtuaCamNative
{
    // The bundled native artifacts (src/native/virtuacam, copied beside ss.exe). The DLL stem is what
    // [DllImport] resolves; the loader searches the app dir where ss.exe lives.
    public const string BrokerLib   = "DirectPortBroker";    // the broker/multiplexer
    public const string ClientDll   = "DirectPortClient.dll"; // the Media Foundation virtual-camera COM source

    // Mirrors enum class BrokerState in Broker.cpp (Searching = 0, Connected = 1, Failed = 2).
    public enum BrokerState { Searching = 0, Connected = 1, Failed = 2 }

    // ---- DirectPortBroker.dll (the broker C API; VirtuaCam.exe called these via GetProcAddress) --------

    // Initialise D3D11, the discovery scanner, the multiplexer, and the shared output resources. Call once
    // before any other broker function. (Creates a D3D11 device — fails silently on a box with no GPU.)
    [DllImport(BrokerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void InitializeBroker();

    [DllImport(BrokerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ShutdownBroker();

    // Composite one frame from the discovered producers and signal the output fence. The render-loop step.
    [DllImport(BrokerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void RenderBrokerFrame();

    [DllImport(BrokerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern BrokerState GetBrokerState();

    // The ordered producer PID list: pids[0] = primary (fullscreen), pids[1..3] = PiP overlays; 0 = off.
    [DllImport(BrokerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe void UpdateProducerPriorityList(uint* pids, int count);

    // true = grid mode (all discovered producers tiled); false = priority-list mode (primary + PiP).
    [DllImport(BrokerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SetCompositingMode([MarshalAs(UnmanagedType.I1)] bool isGrid);

    // The composited output texture (AddRef'd) — the UI preview path. Held as IntPtr; the gateway slice
    // does not consume it yet (the MF client reads the broker's shared texture cross-process).
    [DllImport(BrokerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr GetSharedTexture();

    // ---- A no-D3D probe: does DirectPortBroker.dll load and do its exports resolve? ---------------------
    // Verifies the binding + the bundled artifact without creating a device (safe on a headless/RDP box).

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    private static readonly string[] BrokerExports =
    {
        "InitializeBroker", "ShutdownBroker", "RenderBrokerFrame",
        "GetBrokerState", "UpdateProducerPriorityList", "SetCompositingMode", "GetSharedTexture",
    };

    // Returns true if the broker DLL loads and every expected export resolves; otherwise false with a
    // reason. Loads beside ss.exe (no path literal): the app dir is on the default search path.
    public static bool Probe(out string detail)
    {
        IntPtr h = LoadLibraryW(BrokerLib + ".dll");
        if (h == IntPtr.Zero)
        {
            detail = $"LoadLibrary({BrokerLib}.dll) failed (Win32 {Marshal.GetLastWin32Error()}) — not bundled beside ss.exe, or a missing dependency (VC++ runtime / D3D).";
            return false;
        }
        try
        {
            foreach (var name in BrokerExports)
                if (GetProcAddress(h, name) == IntPtr.Zero)
                {
                    detail = $"export '{name}' not found in {BrokerLib}.dll (artifact drift)";
                    return false;
                }
            detail = $"{BrokerLib}.dll loaded; all {BrokerExports.Length} exports resolved";
            return true;
        }
        finally { FreeLibrary(h); }
    }
}
